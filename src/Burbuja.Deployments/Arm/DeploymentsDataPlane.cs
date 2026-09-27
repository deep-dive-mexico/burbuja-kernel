using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Arm;
using Burbuja.Deployments.Abstractions;

namespace Burbuja.Deployments.Arm;

/// <summary>Stream-id shape for deployment SSE (environments/deployments).</summary>
public sealed class DeploymentsEventStreams : IEventStreamResolver
{
    public string Namespace => DeploymentsConstants.Namespace;

    public bool TryResolve(ResourceId id, out string streamId)
    {
        var canonical = (id with { Action = null, DataPlaneSegment = null, DataPlanePath = null }).Canonical;
        streamId = id.FullyQualifiedType.Equals("environments/deployments", StringComparison.OrdinalIgnoreCase)
            ? EventStreamIds.Deployment(canonical)
            : EventStreamIds.Resource(canonical);
        return true;
    }
}
