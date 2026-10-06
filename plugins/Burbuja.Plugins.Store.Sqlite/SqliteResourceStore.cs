using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Arm;
using Microsoft.Data.Sqlite;

namespace Burbuja.Plugins.Store.Sqlite;

/// <summary>Durable <see cref="IResourceStore"/> over SQLite, with the kernel's ETag/paging semantics.</summary>
public sealed class SqliteResourceStore(SqliteDatabase database, IClock clock) : IResourceStore
{
    public async ValueTask<ResourceEnvelope<JsonObject>?> GetAsync(string key, CancellationToken ct)
    {
        var normalized = key.ToLowerInvariant();
        return await database.ReadAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, name, type, location, tags, properties, provisioning_state, system_data, etag, version FROM resources WHERE key = $key";
            command.Parameters.AddWithValue("$key", normalized);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
        }, ct).ConfigureAwait(false);
    }

    public ValueTask<ResourceEnvelope<JsonObject>> CreateAsync(ResourceEnvelope<JsonObject> envelope, CancellationToken ct) =>
        WriteAsync(envelope, null, ct, createOnly: true);

    public ValueTask<ResourceEnvelope<JsonObject>> UpsertAsync(ResourceEnvelope<JsonObject> envelope, string? ifMatchETag, CancellationToken ct) =>
        WriteAsync(envelope, ifMatchETag, ct, createOnly: false);

    private async ValueTask<ResourceEnvelope<JsonObject>> WriteAsync(ResourceEnvelope<JsonObject> envelope, string? ifMatchETag, CancellationToken ct, bool createOnly)
    {
        var key = envelope.Id.ToLowerInvariant();
        return await database.WriteAsync(async (connection, transaction) =>
        {
            var existing = await ReadVersionAsync(connection, transaction, key, ct).ConfigureAwait(false);
            if (createOnly && existing is not null)
                throw new ArmException(ArmError.PreconditionFailed("The resource already exists."));
            long version = 1;
            if (existing is not null)
            {
                if (!string.IsNullOrEmpty(ifMatchETag) && ifMatchETag != "*" && !ETagMatches(existing.Value.ETag, ifMatchETag))
                    throw new ArmException(ArmError.PreconditionFailed("The provided ETag does not match the current resource."));
                version = existing.Value.Version + 1;
            }
            else if (!string.IsNullOrEmpty(ifMatchETag) && ifMatchETag != "*")
            {
                throw new ArmException(ArmError.PreconditionFailed("If-Match did not match because the resource does not exist."));
            }

            var now = clock.UtcNow;
            var system = existing is null
                ? envelope.SystemData with { CreatedAt = envelope.SystemData.CreatedAt ?? now, LastModifiedAt = now }
                : envelope.SystemData with { LastModifiedAt = now };
            var stored = envelope with { ETag = $"W/\"{version}\"", SystemData = system };

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO resources(key, id, name, type, location, tags, properties, provisioning_state, system_data, etag, version, created_at)
                VALUES ($key, $id, $name, $type, $location, $tags, $properties, $state, $system, $etag, $version, $created)
                ON CONFLICT(key) DO UPDATE SET
                    id=excluded.id, name=excluded.name, type=excluded.type, location=excluded.location,
                    tags=excluded.tags, properties=excluded.properties, provisioning_state=excluded.provisioning_state,
                    system_data=excluded.system_data, etag=excluded.etag, version=excluded.version, created_at=excluded.created_at
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$id", stored.Id);
            command.Parameters.AddWithValue("$name", stored.Name);
            command.Parameters.AddWithValue("$type", stored.Type);
            command.Parameters.AddWithValue("$location", (object?)stored.Location ?? DBNull.Value);
            command.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(stored.Tags, JsonWire.Options));
            command.Parameters.AddWithValue("$properties", stored.Properties.ToJsonString());
            command.Parameters.AddWithValue("$state", (int)stored.ProvisioningState);
            command.Parameters.AddWithValue("$system", JsonSerializer.Serialize(stored.SystemData, JsonWire.Options));
            command.Parameters.AddWithValue("$etag", stored.ETag);
            command.Parameters.AddWithValue("$version", version);
            command.Parameters.AddWithValue("$created", (stored.SystemData.CreatedAt ?? now).ToString("O"));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return stored;
        }, ct).ConfigureAwait(false);
    }

    public async ValueTask DeleteAsync(string key, string? ifMatchETag, CancellationToken ct)
    {
        var normalized = key.ToLowerInvariant();
        await database.WriteAsync(async (connection, transaction) =>
        {
            var existing = await ReadVersionAsync(connection, transaction, normalized, ct).ConfigureAwait(false);
            if (existing is null)
                return 0;
            if (!string.IsNullOrEmpty(ifMatchETag) && ifMatchETag != "*" && !ETagMatches(existing.Value.ETag, ifMatchETag))
                throw new ArmException(ArmError.PreconditionFailed("The provided ETag does not match the current resource."));
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM resources WHERE key = $key";
            command.Parameters.AddWithValue("$key", normalized);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return 0;
        }, ct).ConfigureAwait(false);
    }

    public async ValueTask<PagedResult<ResourceEnvelope<JsonObject>>> ListAsync(ResourceStoreQuery query, CancellationToken ct)
    {
        var rows = await database.ReadAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, name, type, location, tags, properties, provisioning_state, system_data, etag, version FROM resources ORDER BY created_at, id";
            var list = new List<ResourceEnvelope<JsonObject>>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                list.Add(Map(reader));
            return list;
        }, ct).ConfigureAwait(false);

        IEnumerable<ResourceEnvelope<JsonObject>> filtered = rows;

        if (query.IdPrefix is { Length: > 0 } prefix)
        {
            var trimmed = prefix.TrimEnd('/');
            filtered = filtered.Where(e =>
            {
                var id = e.Id.TrimEnd('/');
                return id.Equals(trimmed, StringComparison.OrdinalIgnoreCase)
                       || id.StartsWith(trimmed + "/", StringComparison.OrdinalIgnoreCase);
            });
        }
        else if (query.SubscriptionId is { Length: > 0 } subscription)
        {
            var subPrefix = "/subscriptions/" + subscription;
            filtered = filtered.Where(e =>
                e.Id.Equals(subPrefix, StringComparison.OrdinalIgnoreCase)
                || e.Id.StartsWith(subPrefix + "/", StringComparison.OrdinalIgnoreCase));
        }

        if (query.ResourceGroup is { Length: > 0 } rg)
            filtered = filtered.Where(e => e.Id.Contains($"/resourceGroups/{rg}/", StringComparison.OrdinalIgnoreCase)
                                           || e.Id.EndsWith($"/resourceGroups/{rg}", StringComparison.OrdinalIgnoreCase));
        if (query.Namespace is { Length: > 0 } ns)
            filtered = filtered.Where(e => e.Type.StartsWith(ns, StringComparison.OrdinalIgnoreCase));
        if (query.Type is { Length: > 0 } type)
            filtered = filtered.Where(e => e.Type.Equals(type, StringComparison.OrdinalIgnoreCase)
                                           || e.Type.EndsWith("/" + type, StringComparison.OrdinalIgnoreCase));
        if (query.Filter is { } filter)
            filtered = filtered.Where(e => Matches(e, filter));

        var ordered = filtered
            .OrderBy(e => e.SystemData.CreatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var skip = 0;
        if (!string.IsNullOrEmpty(query.ContinuationToken))
        {
            if (!SkipToken.TryDecode(query.ContinuationToken, out var afterAt, out var afterId))
                throw new ArmException(ArmError.InvalidSkipToken("The $skipToken value is malformed."));
            skip = ordered.FindIndex(e =>
                (e.SystemData.CreatedAt ?? DateTimeOffset.MinValue, e.Id).CompareTo((afterAt, afterId)) > 0);
            if (skip < 0)
                skip = ordered.Count;
        }

        var take = Math.Clamp(query.Top, 1, 1000);
        var page = ordered.Skip(skip).Take(take).ToList();
        var next = skip + page.Count < ordered.Count && page.Count > 0
            ? SkipToken.Encode(page[^1].SystemData.CreatedAt ?? DateTimeOffset.MinValue, page[^1].Id)
            : null;
        return new PagedResult<ResourceEnvelope<JsonObject>>(page, next);
    }

    private static bool Matches(ResourceEnvelope<JsonObject> envelope, ArmListFilter filter)
    {
        if (filter.NameEq is { } nameEq && !envelope.Name.Equals(nameEq, StringComparison.OrdinalIgnoreCase))
            return false;
        if (filter.NameStartsWith is { } starts && !envelope.Name.StartsWith(starts, StringComparison.OrdinalIgnoreCase))
            return false;
        if (filter.TagKey is { } tagKey && !envelope.Tags.ContainsKey(tagKey))
            return false;
        if (filter.TagEq is { } tagEq && !envelope.Tags.Values.Any(v => v.Equals(tagEq, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (filter.PropertyKey is { } propertyKey)
        {
            var value = envelope.Properties[propertyKey]?.ToJsonString()?.Trim('"');
            if (value is null)
                return false;
            if (filter.PropertyEq is { } propertyEq && !value.Equals(propertyEq, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static async Task<(string ETag, long Version)?> ReadVersionAsync(SqliteConnection connection, SqliteTransaction transaction, string key, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT etag, version FROM resources WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? (reader.GetString(0), reader.GetInt64(1)) : null;
    }

    private static ResourceEnvelope<JsonObject> Map(SqliteDataReader reader)
    {
        var tags = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(4), JsonWire.Options) ?? [];
        var properties = JsonNode.Parse(reader.GetString(5))?.AsObject() ?? new JsonObject();
        var system = JsonSerializer.Deserialize<SystemData>(reader.GetString(7), JsonWire.Options) ?? new SystemData(null, null, null, null, null, null);
        return new ResourceEnvelope<JsonObject>(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            tags,
            properties,
            (ProvisioningState)reader.GetInt32(6),
            system,
            reader.GetString(8));
    }

    public static bool ETagMatches(string current, string provided) =>
        string.Equals(current, provided, StringComparison.Ordinal)
        || string.Equals(current.Trim('"'), provided.Trim('"'), StringComparison.Ordinal);
}

/// <summary>Verbatim copy of the kernel's Core-internal skip-token format (plan §10 N3).</summary>
public static class SkipToken
{
    public static string Encode(DateTimeOffset createdAt, string id)
    {
        var payload = createdAt.UtcTicks.ToString(CultureInfo.InvariantCulture) + "\n" + id;
        return ToBase64Url(Encoding.UTF8.GetBytes(payload));
    }

    public static bool TryDecode(string token, out DateTimeOffset createdAt, out string id)
    {
        createdAt = default;
        id = "";
        try
        {
            var text = Encoding.UTF8.GetString(FromBase64Url(token));
            var nl = text.IndexOf('\n');
            if (nl <= 0 || !long.TryParse(text[..nl], CultureInfo.InvariantCulture, out var ticks))
                return false;
            createdAt = new DateTimeOffset(ticks, TimeSpan.Zero);
            id = text[(nl + 1)..];
            return id.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string token)
    {
        var s = token.Replace('-', '+').Replace('_', '/');
        s = (s.Length % 4) switch
        {
            2 => s + "==",
            3 => s + "=",
            _ => s,
        };
        return Convert.FromBase64String(s);
    }
}
