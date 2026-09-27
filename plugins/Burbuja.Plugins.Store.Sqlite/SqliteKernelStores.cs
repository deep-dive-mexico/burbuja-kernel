using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Json;
using Microsoft.Data.Sqlite;

namespace Burbuja.Plugins.Store.Sqlite;

/// <summary>
/// M4.1: durable idempotency keys. <c>TryBegin</c>/<c>End</c> stay process-local (they are
/// in-flight exclusivity); the record itself survives a restart.
/// </summary>
public sealed class SqliteIdempotencyStore(SqliteDatabase database, IClock clock) : IIdempotencyStore
{
    /// <summary>
    /// How many expired rows one purge deletes. Small enough that the delete cannot become the thing a
    /// caller waits on; large enough that a table which grew for a day is clean within a few writes.
    /// </summary>
    public const int PurgeBatch = 500;

    private readonly ConcurrentDictionary<string, byte> _inflight = new(StringComparer.Ordinal);

    public bool TryBegin(string key) => _inflight.TryAdd(key, 0);

    public void End(string key) => _inflight.TryRemove(key, out _);

    public IdempotencyRecord? Get(string key)
    {
        return database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT expires_at, payload FROM idempotency WHERE key = $key";
            command.Parameters.AddWithValue("$key", key);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;
            var expires = Parse(reader.GetString(0));
            if (expires <= clock.UtcNow)
                return null;
            return JsonSerializer.Deserialize<IdempotencyRecord>(reader.GetString(1), JsonWire.Options);
        });
    }

    public void Put(string key, IdempotencyRecord record)
    {
        database.Write<object?>((connection, transaction) =>
        {
            using (var purge = connection.CreateCommand())
            {
                purge.Transaction = transaction;
                // M11 P6 — BOUNDED. The purge used to be `DELETE … WHERE expires_at <= $now` with no
                // limit, so the first write after a day of a per-second reporter would have deleted tens
                // of thousands of rows inside one write transaction while the reporter waited on the
                // store. It deletes the same rows eventually; it just cannot turn one heartbeat into a
                // long lock. The unbounded half of the cleanup is the boot sweep in `SqliteDatabase`.
                purge.CommandText =
                    "DELETE FROM idempotency WHERE key IN "
                    + "(SELECT key FROM idempotency WHERE expires_at <= $now LIMIT $batch)";
                purge.Parameters.AddWithValue("$now", clock.UtcNow.ToString("O"));
                purge.Parameters.AddWithValue("$batch", PurgeBatch);
                purge.ExecuteNonQuery();
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO idempotency(key, expires_at, payload) VALUES ($key, $expires, $payload)
                ON CONFLICT(key) DO UPDATE SET expires_at = excluded.expires_at, payload = excluded.payload
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$expires", record.ExpiresAt.ToString("O"));
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(record, JsonWire.Options));
            command.ExecuteNonQuery();
            return null;
        });
    }

    internal static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

/// <summary>M4.1: approvals and grants persist, so a restarted process can still read them.</summary>
public sealed class SqliteApprovalStore(SqliteDatabase database) : IApprovalStore
{
    public ApprovalRecord? Get(string id)
    {
        return database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM approvals WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            var payload = command.ExecuteScalar() as string;
            return payload is null ? null : JsonSerializer.Deserialize<ApprovalRecord>(payload, JsonWire.Options);
        });
    }

    public void Put(ApprovalRecord record)
    {
        database.Write<object?>((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO approvals(id, opened_at, payload) VALUES ($id, $opened, $payload)
                ON CONFLICT(id) DO UPDATE SET opened_at = excluded.opened_at, payload = excluded.payload
                """;
            command.Parameters.AddWithValue("$id", record.Id);
            command.Parameters.AddWithValue("$opened", record.OpenedAt.ToString("O"));
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(record, JsonWire.Options));
            command.ExecuteNonQuery();
            return null;
        });
    }

    public IReadOnlyCollection<ApprovalRecord> All() =>
        database.Read(connection =>
        {
            var records = new List<ApprovalRecord>();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM approvals ORDER BY opened_at";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var record = JsonSerializer.Deserialize<ApprovalRecord>(reader.GetString(0), JsonWire.Options);
                if (record is not null)
                    records.Add(record);
            }

            return (IReadOnlyCollection<ApprovalRecord>)records;
        });

    public ApprovalGrant? FindGrant(string decisionKey, string? shapeKey, string? transition, DateTimeOffset now)
    {
        return database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM approval_grants WHERE grant_key = $key";
            command.Parameters.AddWithValue("$key", GrantKey(decisionKey, shapeKey));
            var payload = command.ExecuteScalar() as string;
            if (payload is null)
                return null;
            var grant = JsonSerializer.Deserialize<ApprovalGrant>(payload, JsonWire.Options);
            // A7: the transition is part of the match. A grant for one named change never satisfies
            // another, and an unnamed grant never satisfies a named one — "no name" is not a wildcard.
            return grant is not null
                   && grant.ExpiresAt > now
                   && string.Equals(grant.Transition ?? "", transition ?? "", StringComparison.Ordinal)
                ? grant
                : null;
        });
    }

    public void PutGrant(ApprovalGrant grant)
    {
        database.Write<object?>((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO approval_grants(grant_key, expires_at, payload) VALUES ($key, $expires, $payload)
                ON CONFLICT(grant_key) DO UPDATE SET expires_at = excluded.expires_at, payload = excluded.payload
                """;
            command.Parameters.AddWithValue("$key", GrantKey(grant.DecisionKey, grant.ShapeKey));
            command.Parameters.AddWithValue("$expires", grant.ExpiresAt.ToString("O"));
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(grant, JsonWire.Options));
            command.ExecuteNonQuery();
            return null;
        });
    }

    public void RemoveGrantsForStream(string streamId)
    {
        database.Write<object?>((connection, transaction) =>
        {
            var keys = new List<string>();
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT grant_key, payload FROM approval_grants";
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    var grant = JsonSerializer.Deserialize<ApprovalGrant>(reader.GetString(1), JsonWire.Options);
                    if (grant is not null && grant.StreamId.Equals(streamId, StringComparison.Ordinal))
                        keys.Add(reader.GetString(0));
                }
            }

            foreach (var key in keys)
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM approval_grants WHERE grant_key = $key";
                delete.Parameters.AddWithValue("$key", key);
                delete.ExecuteNonQuery();
            }

            return null;
        });
    }

    public void PurgeGrants(DateTimeOffset now)
    {
        database.Write<object?>((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM approval_grants WHERE expires_at <= $now";
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.ExecuteNonQuery();
            return null;
        });
    }

    private static string GrantKey(string decisionKey, string? shapeKey) => decisionKey + "\n" + (shapeKey ?? "");
}

/// <summary>M4.1: operation polling survives a restart (a mid-flight operation is not orphaned).</summary>
public sealed class SqliteOperationTracker(SqliteDatabase database, IClock clock) : IOperationTracker
{
    public async ValueTask StartAsync(string operationId, string subscriptionId, string ns, string location, string? targetResourceId, JsonObject? props, CancellationToken ct)
    {
        var id = $"/subscriptions/{subscriptionId}/providers/{ns}/locations/{location}/operations/{operationId}";
        var op = new OperationResource(id, operationId, OperationStatus.Running, clock.UtcNow, null, 0, null, targetResourceId, props);
        await database.WriteAsync<object?>(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO operations(operation_id, id, start_time, payload) VALUES ($op, $id, $start, $payload)
                ON CONFLICT(operation_id) DO UPDATE SET id = excluded.id, start_time = excluded.start_time, payload = excluded.payload
                """;
            command.Parameters.AddWithValue("$op", operationId);
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$start", op.StartTime.ToString("O"));
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(op, JsonWire.Options));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return null;
        }, ct).ConfigureAwait(false);
    }

    public ValueTask ReportAsync(string operationId, int percent, JsonObject? props, CancellationToken ct) =>
        MutateAsync(operationId, op => op with { PercentComplete = percent, Properties = props ?? op.Properties }, ct);

    public ValueTask SucceedAsync(string operationId, JsonObject? result, CancellationToken ct) =>
        MutateAsync(operationId, op => op with { Status = OperationStatus.Succeeded, EndTime = clock.UtcNow, PercentComplete = 100, Properties = result ?? op.Properties }, ct);

    public ValueTask FailAsync(string operationId, ArmError error, CancellationToken ct) =>
        MutateAsync(operationId, op => op with { Status = OperationStatus.Failed, EndTime = clock.UtcNow, Error = error }, ct);

    public ValueTask CancelAsync(string operationId, string reason, CancellationToken ct) =>
        MutateAsync(operationId, op => op with { Status = OperationStatus.Canceled, EndTime = clock.UtcNow, Error = ArmError.Conflict("Canceled", reason) }, ct);

    public async ValueTask<OperationResource?> GetAsync(string operationId, CancellationToken ct)
    {
        return await database.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM operations WHERE operation_id = $op";
            command.Parameters.AddWithValue("$op", operationId);
            var payload = command.ExecuteScalar() as string;
            return Task.FromResult(payload is null ? null : JsonSerializer.Deserialize<OperationResource>(payload, JsonWire.Options));
        }, ct).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<OperationResource>> ListAsync(string? subscriptionId, string? ns, CancellationToken ct)
    {
        return await database.ReadAsync(connection =>
        {
            var rows = new List<OperationResource>();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, payload FROM operations ORDER BY start_time";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                if (subscriptionId is not null
                    && !id.StartsWith("/subscriptions/" + subscriptionId + "/", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (ns is not null && !id.Contains("/providers/" + ns + "/", StringComparison.OrdinalIgnoreCase))
                    continue;
                var op = JsonSerializer.Deserialize<OperationResource>(reader.GetString(1), JsonWire.Options);
                if (op is not null)
                    rows.Add(op);
            }

            return Task.FromResult<IReadOnlyList<OperationResource>>(rows);
        }, ct).ConfigureAwait(false);
    }

    private ValueTask MutateAsync(string operationId, Func<OperationResource, OperationResource> mutate, CancellationToken ct) =>
        new(database.WriteAsync<object?>(async (connection, transaction) =>
        {
            OperationResource? current;
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT payload FROM operations WHERE operation_id = $op";
                select.Parameters.AddWithValue("$op", operationId);
                var payload = await select.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
                current = payload is null ? null : JsonSerializer.Deserialize<OperationResource>(payload, JsonWire.Options);
            }

            if (current is null)
                return null;

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE operations SET payload = $payload WHERE operation_id = $op";
            update.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(mutate(current), JsonWire.Options));
            update.Parameters.AddWithValue("$op", operationId);
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return null;
        }, ct));
}
