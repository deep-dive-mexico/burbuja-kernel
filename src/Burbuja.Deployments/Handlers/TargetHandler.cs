using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Deployments.Abstractions;
using Burbuja.Deployments.Targets;

namespace Burbuja.Deployments.Handlers;

public sealed class TargetProjection
{
    public string Kind { get; set; } = "";
    public List<string> SupportedResourceKinds { get; set; } = [];
}

public sealed class TargetHandler(TargetRegistry registry, IClock clock) : IResourceHandler<TargetProjection>
{
    public ValueTask<ResourceEnvelope<TargetProjection>?> GetAsync(ResourceRequest req, CancellationToken ct)
    {
        var name = req.Id.NameSegments.LastOrDefault() ?? "";
        if (!registry.TryGet(name, out var target))
            return new((ResourceEnvelope<TargetProjection>?)null);
        return new(Envelope(target, clock));
    }

    public ValueTask<PutOutcome<TargetProjection>> PutAsync(ResourceRequest req, TargetProjection desired, CancellationToken ct) =>
        new(new PutOutcome<TargetProjection>.Rejected(ArmError.MethodNotAllowed("targets are projected.")));

    public ValueTask<PutOutcome<TargetProjection>> PatchAsync(ResourceRequest req, ResourcePatch patch, CancellationToken ct) =>
        new(new PutOutcome<TargetProjection>.Rejected(ArmError.MethodNotAllowed("targets are projected.")));

    public ValueTask<DeleteOutcome> DeleteAsync(ResourceRequest req, CancellationToken ct) =>
        new(new DeleteOutcome.Rejected(ArmError.MethodNotAllowed("targets are projected.")));

    public ValueTask<PagedResult<ResourceEnvelope<TargetProjection>>> ListAsync(ResourceListRequest req, CancellationToken ct)
    {
        var items = registry.All.OrderBy(t => t.Kind, StringComparer.Ordinal).Select(t => Envelope(t, clock)).ToList();
        return new(new PagedResult<ResourceEnvelope<TargetProjection>>(items, null));
    }

    public ValueTask<ActionOutcome> InvokeAsync(ResourceActionRequest req, CancellationToken ct) =>
        new(new ActionOutcome.Rejected(ArmError.ActionNotSupported(req.Action)));

    private static ResourceEnvelope<TargetProjection> Envelope(IDeploymentTarget target, IClock clock) =>
        new(
            $"/providers/{DeploymentsConstants.Namespace}/targets/{target.Kind}",
            target.Kind,
            $"{DeploymentsConstants.Namespace}/targets",
            "local",
            new Dictionary<string, string>(),
            new TargetProjection
            {
                Kind = target.Kind,
                SupportedResourceKinds = target.SupportedResourceKinds.Select(k => k.ToString()).ToList(),
            },
            ProvisioningState.Succeeded,
            new SystemData(clock.UtcNow, "system", "System", clock.UtcNow, "system", "System"),
            "W/\"1\"");
}
