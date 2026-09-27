using System.Text.Json;
using Burbuja.Core.Abstractions.Artifacts;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Leases;
using Burbuja.Core.Abstractions.Outbox;
using Burbuja.Core.Abstractions.Redaction;
using Burbuja.Core.Abstractions.Store;
using Microsoft.Data.Sqlite;

namespace Burbuja.Plugins.Store.Sqlite;

/// <summary>Fenced leases in SQLite; acquisition is one serialized write transaction.</summary>
public sealed class SqliteLeaseStore(SqliteDatabase database, IClock clock) : ILeaseStore
{
    private const string Columns = "resource, owner, fencing_token, expires_at";

    public async ValueTask<LeaseAcquisition?> AcquireAsync(string resource, string owner, TimeSpan ttl, CancellationToken ct)
    {
        var now = clock.UtcNow;
        return await database.WriteAsync(async (connection, transaction) =>
        {
            var existing = await ReadOneAsync(connection, transaction, resource, ct).ConfigureAwait(false);
            if (existing is not null && existing.ExpiresAt > now)
                return (LeaseAcquisition?)null;

            var fence = await NextFenceAsync(connection, transaction, resource, ct).ConfigureAwait(false);
            var acquired = new LeaseAcquisition(resource, owner, fence.ToString(), now + ttl);

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO leases(resource, owner, fencing_token, expires_at) VALUES ($resource, $owner, $fence, $expires)
                ON CONFLICT(resource) DO UPDATE SET owner = excluded.owner, fencing_token = excluded.fencing_token, expires_at = excluded.expires_at
                """;
            command.Parameters.AddWithValue("$resource", acquired.Resource);
            command.Parameters.AddWithValue("$owner", acquired.Owner);
            command.Parameters.AddWithValue("$fence", acquired.FencingToken);
            command.Parameters.AddWithValue("$expires", acquired.ExpiresAt.ToString("O"));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return acquired;
        }, ct).ConfigureAwait(false);
    }

    public async ValueTask<bool> RenewAsync(string resource, string owner, string fencingToken, TimeSpan ttl, CancellationToken ct)
    {
        var now = clock.UtcNow;
        return await database.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE leases SET expires_at = $expires
                WHERE resource = $resource AND owner = $owner AND fencing_token = $fence
                """;
            command.Parameters.AddWithValue("$expires", (now + ttl).ToString("O"));
            command.Parameters.AddWithValue("$resource", resource);
            command.Parameters.AddWithValue("$owner", owner);
            command.Parameters.AddWithValue("$fence", fencingToken);
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }, ct).ConfigureAwait(false);
    }

    public async ValueTask ReleaseAsync(string resource, string owner, string fencingToken, CancellationToken ct)
    {
        await database.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM leases WHERE resource = $resource AND owner = $owner AND fencing_token = $fence";
            command.Parameters.AddWithValue("$resource", resource);
            command.Parameters.AddWithValue("$owner", owner);
            command.Parameters.AddWithValue("$fence", fencingToken);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
    }

    public ValueTask<LeaseAcquisition?> GetAsync(string resource, CancellationToken ct) =>
        new(database.ReadAsync(
            connection => ReadOneAsync(connection, null, resource, ct),
            ct));

    public ValueTask<IReadOnlyList<LeaseAcquisition>> ListExpiredAsync(CancellationToken ct) =>
        new(database.ReadAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {Columns} FROM leases WHERE expires_at <= $now ORDER BY resource";
            command.Parameters.AddWithValue("$now", clock.UtcNow.ToString("O"));
            var list = new List<LeaseAcquisition>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                list.Add(Map(reader));
            return (IReadOnlyList<LeaseAcquisition>)list;
        }, ct));

    private static async Task<long> NextFenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string resource,
        CancellationToken ct)
    {
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT fence FROM lease_fences WHERE resource = $resource";
            read.Parameters.AddWithValue("$resource", resource);
            var current = await read.ExecuteScalarAsync(ct).ConfigureAwait(false);
            var next = current is null or DBNull ? 1L : Convert.ToInt64(current, System.Globalization.CultureInfo.InvariantCulture) + 1;

            await using var write = connection.CreateCommand();
            write.Transaction = transaction;
            write.CommandText = """
                INSERT INTO lease_fences(resource, fence) VALUES ($resource, $fence)
                ON CONFLICT(resource) DO UPDATE SET fence = excluded.fence
                """;
            write.Parameters.AddWithValue("$resource", resource);
            write.Parameters.AddWithValue("$fence", next);
            await write.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return next;
        }
    }

    private static async Task<LeaseAcquisition?> ReadOneAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string resource,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Columns} FROM leases WHERE resource = $resource";
        command.Parameters.AddWithValue("$resource", resource);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    private static LeaseAcquisition Map(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind));
}

/// <summary>Transactional outbox; claims are an atomic update-and-select inside one write transaction.</summary>
public sealed class SqliteOutbox(SqliteDatabase database) : IOutbox, IOutboxStore
{
    private const string Columns = "message_id, aggregate_id, topic, payload, status, attempts, next_attempt_at, dead_reason";

    public ValueTask EnqueueAsync(OutboxMessage message, CancellationToken ct) =>
        new(database.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO outbox(message_id, aggregate_id, topic, payload, status, attempts, next_attempt_at, dead_reason)
                VALUES ($id, $aggregate, $topic, $payload, $status, 0, $next, NULL)
                ON CONFLICT(message_id) DO NOTHING
                """;
            command.Parameters.AddWithValue("$id", message.MessageId.ToString());
            command.Parameters.AddWithValue("$aggregate", message.AggregateId.ToString());
            command.Parameters.AddWithValue("$topic", message.Topic);
            command.Parameters.AddWithValue("$payload", message.Payload.ToJsonString());
            command.Parameters.AddWithValue("$status", (int)OutboxStatus.Pending);
            command.Parameters.AddWithValue("$next", DateTimeOffset.MinValue.ToString("O"));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return true;
        }, ct));

    public ValueTask<IReadOnlyList<OutboxRecord>> ClaimAsync(int max, DateTimeOffset now, CancellationToken ct) =>
        new(database.WriteAsync(async (connection, transaction) =>
        {
            var claimed = new List<OutboxRecord>();
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = $"""
                    SELECT {Columns} FROM outbox
                    WHERE status = $pending AND next_attempt_at <= $now
                    ORDER BY next_attempt_at, message_id
                    LIMIT $max
                    """;
                select.Parameters.AddWithValue("$pending", (int)OutboxStatus.Pending);
                select.Parameters.AddWithValue("$now", now.ToString("O"));
                select.Parameters.AddWithValue("$max", max);
                await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var record = Map(reader);
                    claimed.Add(record with
                    {
                        Status = OutboxStatus.InFlight,
                        Attempts = record.Attempts + 1,
                    });
                }
            }

            foreach (var record in claimed)
            {
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE outbox SET status = $status, attempts = $attempts WHERE message_id = $id";
                update.Parameters.AddWithValue("$status", (int)OutboxStatus.InFlight);
                update.Parameters.AddWithValue("$attempts", record.Attempts);
                update.Parameters.AddWithValue("$id", record.Message.MessageId.ToString());
                await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            return (IReadOnlyList<OutboxRecord>)claimed;
        }, ct));

    public ValueTask MarkSentAsync(Guid messageId, CancellationToken ct) => UpdateStatusAsync(messageId, OutboxStatus.Sent, ct);

    public ValueTask MarkDeadAsync(Guid messageId, string reason, CancellationToken ct) =>
        new(database.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE outbox SET status = $status, dead_reason = $reason WHERE message_id = $id";
            command.Parameters.AddWithValue("$status", (int)OutboxStatus.Dead);
            command.Parameters.AddWithValue("$reason", reason);
            command.Parameters.AddWithValue("$id", messageId.ToString());
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return true;
        }, ct));

    public ValueTask RetryLaterAsync(Guid messageId, DateTimeOffset nextAttemptAt, CancellationToken ct) =>
        new(database.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE outbox SET status = $status, next_attempt_at = $next WHERE message_id = $id";
            command.Parameters.AddWithValue("$status", (int)OutboxStatus.Pending);
            command.Parameters.AddWithValue("$next", nextAttemptAt.ToString("O"));
            command.Parameters.AddWithValue("$id", messageId.ToString());
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return true;
        }, ct));

    public ValueTask<IReadOnlyList<OutboxRecord>> ListDeadAsync(CancellationToken ct) =>
        new(database.ReadAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {Columns} FROM outbox WHERE status = $dead ORDER BY message_id";
            command.Parameters.AddWithValue("$dead", (int)OutboxStatus.Dead);
            var list = new List<OutboxRecord>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                list.Add(Map(reader));
            return (IReadOnlyList<OutboxRecord>)list;
        }, ct));

    private ValueTask UpdateStatusAsync(Guid messageId, OutboxStatus status, CancellationToken ct) =>
        new(database.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE outbox SET status = $status WHERE message_id = $id";
            command.Parameters.AddWithValue("$status", (int)status);
            command.Parameters.AddWithValue("$id", messageId.ToString());
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return true;
        }, ct));

    private static OutboxRecord Map(SqliteDataReader reader) => new(
        new OutboxMessage(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            System.Text.Json.Nodes.JsonNode.Parse(reader.GetString(3))!.AsObject()),
        (OutboxStatus)reader.GetInt32(4),
        reader.GetInt32(5),
        DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
        reader.IsDBNull(7) ? null : reader.GetString(7));
}

/// <summary>Decision cache keyed by stream + decision key; values are JSON so any payload type round-trips.</summary>
public sealed class SqliteDecisionCache<T>(SqliteDatabase database) : IDecisionCache<T>
{
    public async ValueTask<T?> GetAsync(string streamId, string decisionKey, CancellationToken ct)
    {
        var json = await database.ReadAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM decision_cache WHERE stream_id = $stream AND decision_key = $key";
            command.Parameters.AddWithValue("$stream", streamId);
            command.Parameters.AddWithValue("$key", decisionKey);
            return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }, ct).ConfigureAwait(false);
        return json is null ? default : JsonSerializer.Deserialize<T>(json, SqliteJson.Options);
    }

    public ValueTask SetAsync(string streamId, string decisionKey, T value, CancellationToken ct) =>
        new(database.WriteAsync(async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO decision_cache(stream_id, decision_key, value) VALUES ($stream, $key, $value)
                ON CONFLICT(stream_id, decision_key) DO UPDATE SET value = excluded.value
                """;
            command.Parameters.AddWithValue("$stream", streamId);
            command.Parameters.AddWithValue("$key", decisionKey);
            command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(value, SqliteJson.Options));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return true;
        }, ct));
}

/// <summary>File-backed artifacts under a fixed root, with the kernel's text-redaction rule.</summary>
public sealed class FileArtifactStore(string root, IRedactor redactor) : IArtifactStore
{
    public async ValueTask PutAsync(string key, Stream content, string contentType, IReadOnlyDictionary<string, string>? metadata, CancellationToken ct)
    {
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var textLike = contentType.Length == 0
            || contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase);
        if (textLike)
        {
            using var reader = new StreamReader(content, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(path, redactor.Redact(text), ct).ConfigureAwait(false);
        }
        else
        {
            await using var stream = File.Create(path);
            await content.CopyToAsync(stream, ct).ConfigureAwait(false);
        }

        await File.WriteAllTextAsync(path + ".meta", contentType, ct).ConfigureAwait(false);
    }

    public async ValueTask<ArtifactBlob?> GetAsync(string key, CancellationToken ct)
    {
        var path = Resolve(key);
        if (!File.Exists(path))
            return null;
        var type = File.Exists(path + ".meta")
            ? await File.ReadAllTextAsync(path + ".meta", ct).ConfigureAwait(false)
            : "application/octet-stream";
        return new ArtifactBlob(key, File.OpenRead(path), type, null, new Dictionary<string, string>());
    }

    public ValueTask<bool> ExistsAsync(string key, CancellationToken ct) => new(File.Exists(Resolve(key)));

    internal string Resolve(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Contains('\0', StringComparison.Ordinal))
            throw new ArgumentException("Invalid artifact key.", nameof(key));
        var rootFull = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(rootFull, key.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, StringComparison.Ordinal) && !string.Equals(combined, rootFull, StringComparison.Ordinal))
            throw new ArgumentException("Artifact key escapes the store root.", nameof(key));
        return combined;
    }
}

internal static class SqliteJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
