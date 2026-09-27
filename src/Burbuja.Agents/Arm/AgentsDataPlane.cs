using Burbuja.Agents.Abstractions;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Artifacts;
using Burbuja.Core.Arm;

namespace Burbuja.Agents.Arm;

/// <summary>
/// Stream-id shapes for the Burbuja.Agents namespace. A job writes to its own stream; the stateful
/// runtime writes everything an agent does — turns, inbox delivery, gate intents and decisions — to
/// its <em>session</em> stream, so a session resource's events route resolves there. Without the
/// session branch, <c>…/agents/{a}/sessions/{s}/events</c> tailed a stream nobody writes.
/// </summary>
public sealed class AgentsEventStreams : IEventStreamResolver
{
    public string Namespace => AgentsConstants.Namespace;

    public bool TryResolve(ResourceId id, out string streamId)
    {
        var canonical = (id with { Action = null, DataPlaneSegment = null, DataPlanePath = null }).Canonical;
        if (id.FullyQualifiedType.Equals("workspaces/jobs", StringComparison.OrdinalIgnoreCase))
            streamId = EventStreamIds.AgentJob(canonical);
        else if (id.FullyQualifiedType.Equals("agents/sessions", StringComparison.OrdinalIgnoreCase))
            streamId = AgentsConstants.SessionStreamId(canonical);
        else
            streamId = EventStreamIds.Resource(canonical);
        return true;
    }
}

/// <summary>Download/Stream for agent job artifacts and run transcripts.</summary>
public sealed class AgentsArtifactsHandler(IArtifactStore artifacts) : ArtifactDataPlaneHandler(artifacts)
{
    public override string Namespace => AgentsConstants.Namespace;
}
