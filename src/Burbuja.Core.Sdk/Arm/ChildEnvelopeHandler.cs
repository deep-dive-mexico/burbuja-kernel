using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;

namespace Burbuja.Core.Arm;

public abstract class ChildEnvelopeHandler<TProps>(IResourceStore store, IClock clock) : EnvelopeHandler<TProps>(store, clock)
    where TProps : class
{
    public override async ValueTask<ResourceEnvelope<TProps>?> GetAsync(ResourceRequest req, CancellationToken ct)
    {
        await EnsureParentAsync(req, ct);
        return await base.GetAsync(req, ct);
    }

    public override async ValueTask<PutOutcome<TProps>> PutAsync(ResourceRequest req, TProps desired, CancellationToken ct)
    {
        await EnsureParentAsync(req, ct);
        return await base.PutAsync(req, desired, ct);
    }

    public override async ValueTask<PutOutcome<TProps>> PatchAsync(ResourceRequest req, ResourcePatch patch, CancellationToken ct)
    {
        await EnsureParentAsync(req, ct);
        return await base.PatchAsync(req, patch, ct);
    }

    public override async ValueTask<DeleteOutcome> DeleteAsync(ResourceRequest req, CancellationToken ct)
    {
        await EnsureParentAsync(req, ct);
        return await base.DeleteAsync(req, ct);
    }

    public override async ValueTask<PagedResult<ResourceEnvelope<TProps>>> ListAsync(ResourceListRequest req, CancellationToken ct)
    {
        await EnsureParentAsync(req, ct);
        return await base.ListAsync(req, ct);
    }

    public override async ValueTask<ActionOutcome> InvokeAsync(ResourceActionRequest req, CancellationToken ct)
    {
        await EnsureParentAsync(req, ct);
        return await base.InvokeAsync(req, ct);
    }

    protected virtual async ValueTask EnsureParentAsync(ResourceRequest req, CancellationToken ct)
    {
        var parent = req.Id.Parent;
        if (parent is null || parent.Canonical.Length == 0)
            return;
        if (parent.Namespace.Length == 0 && parent.TypeSegments.Length == 0 && parent.ResourceGroup is null)
            return;

        var existing = await Store.GetAsync(parent.Key, ct);
        if (existing is null)
            throw new ArmException(ArmError.NotFound($"Parent '{parent.Canonical}' was not found.", parent.Canonical));
    }
}
