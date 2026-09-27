using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Json;

namespace Burbuja.Core.Arm;

public abstract class EnvelopeHandler<TProps>(IResourceStore store, IClock clock) : IResourceHandler<TProps>
    where TProps : class
{
    protected IResourceStore Store => store;
    protected IClock Clock => clock;
    protected abstract string TypeName { get; }

    protected static ResourceId ResourceIdentity(ResourceId id) =>
        id.Action is null && id.DataPlaneSegment is null
            ? id
            : id with { Action = null, DataPlaneSegment = null };

    protected static string StoreKey(ResourceId id) => ResourceIdentity(id).Key;

    public virtual async ValueTask<ResourceEnvelope<TProps>?> GetAsync(ResourceRequest req, CancellationToken ct)
    {
        var env = await store.GetAsync(StoreKey(req.Id), ct);
        return env is null ? null : Map(env);
    }

    public virtual async ValueTask<PutOutcome<TProps>> PutAsync(ResourceRequest req, TProps desired, CancellationToken ct)
    {
        var existing = await store.GetAsync(StoreKey(req.Id), ct);
        var identity = ResourceIdentity(req.Id);
        var envelope = new ResourceEnvelope<JsonObject>(
            identity.Canonical,
            identity.NameSegments.Length > 0 ? identity.NameSegments[^1] : identity.ResourceGroup ?? identity.SubscriptionId,
            TypeName,
            string.IsNullOrWhiteSpace(req.Location) ? "local" : req.Location,
            new Dictionary<string, string>(req.Tags, StringComparer.OrdinalIgnoreCase),
            JsonSerializer.SerializeToNode(desired, JsonWire.Options)!.AsObject(),
            ProvisioningState.Succeeded,
            new SystemData(clock.UtcNow, req.Principal.PrincipalId, req.Principal.PrincipalType, clock.UtcNow, req.Principal.PrincipalId, req.Principal.PrincipalType),
            "");
        var stored = await store.UpsertAsync(envelope, req.IfMatch, ct);
        var mapped = Map(stored);
        return existing is null ? new PutOutcome<TProps>.Created(mapped) : new PutOutcome<TProps>.Updated(mapped);
    }

    public virtual async ValueTask<PutOutcome<TProps>> PatchAsync(ResourceRequest req, ResourcePatch patch, CancellationToken ct)
    {
        var existing = await store.GetAsync(StoreKey(req.Id), ct) ?? throw new ArmException(ArmError.NotFound(ResourceIdentity(req.Id).Canonical, ResourceIdentity(req.Id).Canonical));
        var props = existing.Properties;
        if (patch.Properties is not null)
        {
            foreach (var kv in patch.Properties)
                props[kv.Key] = kv.Value?.DeepClone();
        }

        var tags = patch.Tags ?? existing.Tags;
        var updated = existing with { Properties = props, Tags = tags };
        var stored = await store.UpsertAsync(updated, req.IfMatch, ct);
        return new PutOutcome<TProps>.Updated(Map(stored));
    }

    public virtual async ValueTask<DeleteOutcome> DeleteAsync(ResourceRequest req, CancellationToken ct)
    {
        var existing = await store.GetAsync(StoreKey(req.Id), ct);
        if (existing is null)
            return new DeleteOutcome.NotFound();
        await store.DeleteAsync(StoreKey(req.Id), req.IfMatch, ct);
        return new DeleteOutcome.Deleted();
    }

    public virtual async ValueTask<PagedResult<ResourceEnvelope<TProps>>> ListAsync(ResourceListRequest req, CancellationToken ct)
    {
        var prefix = req.Id.IsCollection && req.Id.Parent is { Canonical.Length: > 0 } parent
            ? parent.Canonical
            : null;
        var page = await Store.ListAsync(new ResourceStoreQuery(
            req.Id.SubscriptionId.Length == 0 ? null : req.Id.SubscriptionId,
            req.Id.ResourceGroup,
            req.Id.Namespace.Length == 0 ? null : req.Id.Namespace,
            TypeName,
            req.Top,
            req.ContinuationToken,
            prefix,
            req.ParsedFilter), ct);
        return new PagedResult<ResourceEnvelope<TProps>>(page.Value.Select(Map).ToList(), page.NextLink);
    }

    public virtual ValueTask<ActionOutcome> InvokeAsync(ResourceActionRequest req, CancellationToken ct) =>
        new(new ActionOutcome.Rejected(ArmError.Validation($"Unknown action '{req.Action}'.", [])));

    protected ResourceEnvelope<TProps> Map(ResourceEnvelope<JsonObject> env)
    {
        var props = env.Properties.Deserialize<TProps>(JsonWire.Options)
                   ?? Activator.CreateInstance<TProps>();
        return new ResourceEnvelope<TProps>(env.Id, env.Name, env.Type, env.Location, env.Tags, props, env.ProvisioningState, env.SystemData, env.ETag);
    }
}
