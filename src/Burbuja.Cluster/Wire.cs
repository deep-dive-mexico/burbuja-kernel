using Burbuja.Cluster.Abstractions;

namespace Burbuja.Cluster;

// The messages a node and its controller exchange. They are internal on purpose: the protocol is an
// implementation detail of this assembly, versioned by ClusterConstants.ProtocolVersion, and nothing a plugin
// or a consumer builds against. What consumers see is in Burbuja.Cluster.Abstractions.

/// <summary>
/// A node asking to join. Signed with a key derived from a one-time join token; the token itself never crosses
/// the wire, and neither does the node key both sides derive from this exchange.
/// </summary>
internal sealed record JoinRequest(string TokenId, string ClientNonce, NodeDescriptor Descriptor, int Protocol);

internal sealed record JoinResponse(string ClusterId, string NodeId, string ServerNonce);

/// <summary>What a node says about one workload it was given.</summary>
internal sealed record WorkloadReport(
    string Name,
    long Generation,
    WorkloadPhase Phase,
    DateTimeOffset At,
    string? Reason = null,
    int Restarts = 0,
    int? ExitCode = null);

/// <summary>
/// A node's periodic call to its controller. It is the only traffic between them and the node always starts it,
/// so a node needs no inbound access: a leaf that can dial out can be a full member.
/// </summary>
internal sealed record NodeSyncRequest
{
    /// <summary>New on every process start; lets the controller tell a restart from a duplicate node id.</summary>
    public required string InstanceId { get; init; }

    /// <summary>
    /// The controller process this request is meant for. A request captured before a controller restart names
    /// the previous process and is refused, so the controller needs no memory of old requests to reject them.
    /// </summary>
    public string ControllerInstanceId { get; init; } = "";

    /// <summary>
    /// Rises with every request a node process sends. The controller ignores a request that is not newer than
    /// the last one it applied, so a delayed request cannot overwrite what a later one already said.
    /// </summary>
    public long Sequence { get; init; }
    public required NodeDescriptor Descriptor { get; init; }
    public NodeObservation? Observation { get; init; }
    public IReadOnlyList<WorkloadReport> Workloads { get; init; } = [];
    public int Protocol { get; init; } = ClusterConstants.ProtocolVersion;
}

/// <summary>
/// One workload a node should be running. <paramref name="Generation"/> rises whenever the workload is placed
/// again or its spec changes; a node runs exactly the generation it was last told and reports under it, so a
/// late report from an earlier placement can never be mistaken for the current one.
/// </summary>
internal sealed record WorkloadAssignment(WorkloadSpec Spec, long Generation);

/// <summary>
/// The complete set of work the node should be running. It is a statement of desired state, not a list of
/// commands: anything the node runs that is absent here, or present under an older generation, is stopped.
/// </summary>
internal sealed record NodeSyncResponse
{
    public required string ControllerInstanceId { get; init; }
    public NodeAvailability Availability { get; init; }
    public IReadOnlyList<WorkloadAssignment> Assignments { get; init; } = [];
    public int SyncIntervalMs { get; init; }

    /// <summary>
    /// How long work holding an exclusive key may keep running without a successful sync. The controller waits
    /// longer than this before giving the key to anyone else.
    /// </summary>
    public int ExclusiveTtlMs { get; init; }

    /// <summary>
    /// Other addresses the controller believes it has. The node adds them as learned routes, tries them like
    /// any other, and forgets the ones the controller stops reporting.
    /// </summary>
    public IReadOnlyList<AdvertisedEndpoint> ControllerEndpoints { get; init; } = [];
}
