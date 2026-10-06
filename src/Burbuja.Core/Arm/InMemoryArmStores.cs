using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;

namespace Burbuja.Core.Arm;

public sealed class InMemoryResourceStore(IClock clock) : IResourceStore
{
    private readonly ConcurrentDictionary<string, (ResourceEnvelope<JsonObject> Env, long Version)> _items = new(StringComparer.OrdinalIgnoreCase);

    public ValueTask<ResourceEnvelope<JsonObject>> CreateAsync(ResourceEnvelope<JsonObject> envelope, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var created = Stamp(envelope, 1, clock.UtcNow, created: true);
        if (!_items.TryAdd(envelope.Id.ToLowerInvariant(), (created, 1)))
            throw new ArmException(ArmError.PreconditionFailed("The resource already exists."));
        return new(created);
    }

    public ValueTask<ResourceEnvelope<JsonObject>?> GetAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return _items.TryGetValue(key, out var item)
            ? new(item.Env)
            : default;
    }

    public ValueTask<ResourceEnvelope<JsonObject>> UpsertAsync(ResourceEnvelope<JsonObject> envelope, string? ifMatchETag, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = envelope.Id.ToLowerInvariant();
        var now = clock.UtcNow;
        ResourceEnvelope<JsonObject> stored = envelope;
        _items.AddOrUpdate(key, _ =>
        {
            if (!string.IsNullOrEmpty(ifMatchETag) && ifMatchETag != "*")
                throw new ArmException(ArmError.PreconditionFailed("If-Match did not match because the resource does not exist."));
            var created = Stamp(envelope, 1, now, created: true);
            return (created, 1);
        }, (_, existing) =>
        {
            if (!string.IsNullOrEmpty(ifMatchETag) && ifMatchETag != "*"
                && !ETagMatches(existing.Env.ETag, ifMatchETag))
                throw new ArmException(ArmError.PreconditionFailed("The provided ETag does not match the current resource."));
            var ver = existing.Version + 1;
            var updated = Stamp(envelope, ver, now, created: false);
            return (updated, ver);
        });
        stored = _items[key].Env;
        return new(stored);
    }

    public ValueTask DeleteAsync(string key, string? ifMatchETag, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_items.TryGetValue(key, out var existing))
            return default;
        if (!string.IsNullOrEmpty(ifMatchETag) && !ETagMatches(existing.Env.ETag, ifMatchETag))
            throw new ArmException(ArmError.PreconditionFailed("The provided ETag does not match the current resource."));
        _items.TryRemove(key, out _);
        return default;
    }

    public ValueTask<PagedResult<ResourceEnvelope<JsonObject>>> ListAsync(ResourceStoreQuery query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var rows = _items.Values.Select(v => v.Env).AsEnumerable();
        if (query.IdPrefix is not null)
        {
            var prefix = query.IdPrefix.TrimEnd('/');
            rows = rows.Where(e =>
            {
                var id = e.Id.TrimEnd('/');
                return id.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                       || id.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
            });
        }
        else if (query.SubscriptionId is not null)
        {
            var prefix = "/subscriptions/" + query.SubscriptionId;
            rows = rows.Where(e =>
            {
                var id = e.Id;
                return id.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                       || id.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
            });
        }

        if (query.ResourceGroup is not null)
            rows = rows.Where(e => e.Id.Contains($"/resourceGroups/{query.ResourceGroup}/", StringComparison.OrdinalIgnoreCase)
                                   || e.Id.EndsWith($"/resourceGroups/{query.ResourceGroup}", StringComparison.OrdinalIgnoreCase));
        if (query.Namespace is not null)
            rows = rows.Where(e => e.Type.StartsWith(query.Namespace, StringComparison.OrdinalIgnoreCase));
        if (query.Type is not null)
            rows = rows.Where(e => e.Type.Equals(query.Type, StringComparison.OrdinalIgnoreCase)
                                   || e.Type.EndsWith("/" + query.Type, StringComparison.OrdinalIgnoreCase));
        if (query.Filter is not null)
            rows = rows.Where(e => ArmFilterParser.Matches(e, query.Filter));

        var ordered = rows.OrderBy(e => e.SystemData.CreatedAt).ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase).ToList();
        var skip = 0;
        if (!string.IsNullOrEmpty(query.ContinuationToken))
        {
            if (!ArmSkipTokenCodec.TryDecode(query.ContinuationToken, out var afterAt, out var afterId))
                throw new ArmException(ArmError.InvalidSkipToken("The $skipToken value is malformed."));
            skip = ordered.FindIndex(e =>
                (e.SystemData.CreatedAt ?? DateTimeOffset.MinValue, e.Id).CompareTo((afterAt, afterId)) > 0);
            if (skip < 0)
                skip = ordered.Count;
        }

        var take = Math.Clamp(query.Top, 1, 1000);
        var page = ordered.Skip(skip).Take(take).ToList();
        string? next = skip + page.Count < ordered.Count && page.Count > 0
            ? ArmSkipTokenCodec.Encode(page[^1].SystemData.CreatedAt ?? DateTimeOffset.MinValue, page[^1].Id)
            : null;
        return new(new PagedResult<ResourceEnvelope<JsonObject>>(page, next));
    }

    private static ResourceEnvelope<JsonObject> Stamp(ResourceEnvelope<JsonObject> envelope, long version, DateTimeOffset now, bool created)
    {
        var etag = $"W/\"{version}\"";
        var sd = envelope.SystemData;
        var system = created
            ? sd with { CreatedAt = sd.CreatedAt ?? now, LastModifiedAt = now }
            : sd with { LastModifiedAt = now };
        return envelope with { ETag = etag, SystemData = system };
    }

    internal static bool ETagMatches(string current, string provided) =>
        string.Equals(current, provided, StringComparison.Ordinal)
        || string.Equals(current.Trim('"'), provided.Trim('"'), StringComparison.Ordinal);
}

public sealed class InMemoryOperationTracker(IClock clock) : IOperationTracker
{
    private readonly ConcurrentDictionary<string, OperationResource> _ops = new(StringComparer.OrdinalIgnoreCase);

    public ValueTask StartAsync(string operationId, string subscriptionId, string ns, string location, string? targetResourceId, JsonObject? props, CancellationToken ct)
    {
        var id = $"/subscriptions/{subscriptionId}/providers/{ns}/locations/{location}/operations/{operationId}";
        _ops.TryAdd(operationId, new OperationResource(
            id,
            operationId,
            OperationStatus.Running,
            clock.UtcNow,
            null,
            0,
            null,
            targetResourceId,
            props));
        return default;
    }

    public ValueTask ReportAsync(string operationId, int percent, JsonObject? props, CancellationToken ct)
    {
        if (_ops.TryGetValue(operationId, out var op))
            _ops[operationId] = op with { PercentComplete = percent, Properties = props ?? op.Properties };
        return default;
    }

    public ValueTask SucceedAsync(string operationId, JsonObject? result, CancellationToken ct)
    {
        if (_ops.TryGetValue(operationId, out var op))
            _ops[operationId] = op with { Status = OperationStatus.Succeeded, EndTime = clock.UtcNow, PercentComplete = 100, Properties = result ?? op.Properties };
        return default;
    }

    public ValueTask FailAsync(string operationId, ArmError error, CancellationToken ct)
    {
        if (_ops.TryGetValue(operationId, out var op))
            _ops[operationId] = op with { Status = OperationStatus.Failed, EndTime = clock.UtcNow, Error = error };
        return default;
    }

    public ValueTask CancelAsync(string operationId, string reason, CancellationToken ct)
    {
        if (_ops.TryGetValue(operationId, out var op))
            _ops[operationId] = op with { Status = OperationStatus.Canceled, EndTime = clock.UtcNow, Error = ArmError.Conflict("Canceled", reason) };
        return default;
    }

    public ValueTask<OperationResource?> GetAsync(string operationId, CancellationToken ct) =>
        new(_ops.TryGetValue(operationId, out var op) ? op : null);

    public ValueTask<IReadOnlyList<OperationResource>> ListAsync(string? subscriptionId, string? ns, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IEnumerable<OperationResource> rows = _ops.Values;
        if (subscriptionId is not null)
            rows = rows.Where(o => o.Id.StartsWith("/subscriptions/" + subscriptionId + "/", StringComparison.OrdinalIgnoreCase));
        if (ns is not null)
            rows = rows.Where(o => o.Id.Contains("/providers/" + ns + "/", StringComparison.OrdinalIgnoreCase));
        return new(rows.OrderBy(o => o.StartTime).ToArray());
    }
}

public sealed class InMemoryIdempotencyStore(IClock clock) : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, IdempotencyRecord> _keys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _inflight = new(StringComparer.Ordinal);

    /// <summary>
    /// How many responses this store is holding. M11 P6 — the assertion a probe fact needs is about what was
    /// NOT written, and "nothing was stored" has no behavioural proxy: a verb that skipped only the lookup
    /// and one that skipped only the store look identical from the caller's side. The count is how the fact
    /// tells them apart, and it lives on the in-memory implementation because it is observability for a
    /// lane, not part of the seam.
    /// </summary>
    public int Count => _keys.Count;

    public bool TryBegin(string key)
    {
        if (!_inflight.TryAdd(key, 0))
            return false;
        return true;
    }

    public void End(string key) => _inflight.TryRemove(key, out _);

    public IdempotencyRecord? Get(string key)
    {
        if (_keys.TryGetValue(key, out var rec) && rec.ExpiresAt > clock.UtcNow)
            return rec;
        _keys.TryRemove(key, out _);
        return null;
    }

    public void Put(string key, IdempotencyRecord record) => _keys[key] = record;
}

public sealed class InMemoryArmDirectory : IArmDirectory
{
    private readonly ConcurrentDictionary<string, SubscriptionRecord> _subscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ResourceGroupRecord> _resourceGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ProviderRegistrationState> _providerRegistrations = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGetSubscription(string subscriptionId, out SubscriptionRecord record) =>
        _subscriptions.TryGetValue(subscriptionId, out record!);

    public void UpsertSubscription(SubscriptionRecord record) => _subscriptions[record.Id] = record;

    public bool RemoveSubscription(string subscriptionId) => _subscriptions.TryRemove(subscriptionId, out _);

    public bool TryGetResourceGroup(string subscriptionId, string name, out ResourceGroupRecord record) =>
        _resourceGroups.TryGetValue(IArmDirectory.RgKey(subscriptionId, name), out record!);

    public void UpsertResourceGroup(ResourceGroupRecord record) =>
        _resourceGroups[IArmDirectory.RgKey(record.SubscriptionId, record.Name)] = record;

    public bool RemoveResourceGroup(string subscriptionId, string name) =>
        _resourceGroups.TryRemove(IArmDirectory.RgKey(subscriptionId, name), out _);

    public void RemoveResourceGroupsFor(string subscriptionId)
    {
        var prefix = subscriptionId.ToLowerInvariant() + "/";
        foreach (var key in _resourceGroups.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            _resourceGroups.TryRemove(key, out _);
    }

    public bool TryGetProviderRegistration(string subscriptionId, string ns, out ProviderRegistrationState state) =>
        _providerRegistrations.TryGetValue(IArmDirectory.ProviderKey(subscriptionId, ns), out state);

    public void SetProviderRegistration(string subscriptionId, string ns, ProviderRegistrationState state) =>
        _providerRegistrations[IArmDirectory.ProviderKey(subscriptionId, ns)] = state;
}
