namespace Burbuja.Cluster.Abstractions;

/// <summary>Served without authentication at <see cref="ClusterConstants.IdentityPath"/>.</summary>
public sealed record ClusterIdentity(string ClusterId, string ControllerInstanceId, int Protocol);

/// <summary>A workload as the controller knows it, for operators.</summary>
public sealed record WorkloadView
{
    public required WorkloadSpec Spec { get; init; }
    public long Generation { get; init; }
    public string? NodeId { get; init; }
    public WorkloadPhase Phase { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset Since { get; init; }
    public int Restarts { get; init; }

    /// <summary>Why each node was passed over the last time placement was tried.</summary>
    public IReadOnlyDictionary<string, string> Rejections { get; init; } = new Dictionary<string, string>();
}

public sealed record JoinTokenGrant(string Token, DateTimeOffset ExpiresAt);

/// <summary>Whether a submitted workload was accepted, and the reasons when it was not.</summary>
public sealed record ApplyOutcome(bool Accepted, IReadOnlyList<string> Errors, WorkloadView? Workload = null);

/// <summary>
/// What an operator, or a plugin acting for one, can ask of the cluster this host controls. It is the
/// in-process face of the controller: the HTTP operator API and any resource-provider projection are written
/// against it, and so is a plugin that needs to read placements without depending on the runtime assembly.
/// Registered only on a host whose cluster role includes the controller.
/// </summary>
public interface IClusterControlPlane
{
    ClusterIdentity Identity { get; }

    ClusterStatus Status();

    /// <summary>Create or change a workload.</summary>
    ApplyOutcome Apply(WorkloadSpec spec);

    bool Delete(string name);

    bool SetAvailability(string nodeId, NodeAvailability availability);

    /// <summary>Remove a node from the cluster; it stops the cluster's work.</summary>
    bool Forget(string nodeId);

    /// <summary>A one-time token for one machine to join with. Shown once and never stored in readable form.</summary>
    JoinTokenGrant CreateJoinToken();
}

public sealed record ClusterStatus(
    ClusterIdentity Identity,
    IReadOnlyList<NodeView> Nodes,
    IReadOnlyList<WorkloadView> Workloads);
