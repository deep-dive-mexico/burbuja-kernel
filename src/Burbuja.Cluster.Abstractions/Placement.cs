namespace Burbuja.Cluster.Abstractions;

/// <summary>The controller's view of one node at the moment of a decision.</summary>
public sealed record NodeView
{
    public required NodeDescriptor Descriptor { get; init; }
    public NodeAvailability Availability { get; init; }
    public string? InstanceId { get; init; }
    public DateTimeOffset? LastSeen { get; init; }

    /// <summary>The node synced within its time to live.</summary>
    public bool Reachable { get; init; }

    /// <summary>The node's reading is recent enough to act on. False means "cannot see", not "fine".</summary>
    public bool ObservationFresh { get; init; }

    /// <summary>Pressure after hysteresis: several bad readings to set, more good ones to clear.</summary>
    public bool Pressured { get; init; }
    public NodeObservation? Observation { get; init; }

    /// <summary>Sum of the requests of everything assigned here.</summary>
    public IReadOnlyDictionary<string, decimal> Allocated { get; init; } = ResourceMath.Empty;

    /// <summary>Lent capacity minus <see cref="Allocated"/>.</summary>
    public IReadOnlyDictionary<string, decimal> Free { get; init; } = ResourceMath.Empty;
    public IReadOnlyList<string> Workloads { get; init; } = [];
    public IReadOnlyList<string> SpreadGroups { get; init; } = [];
}

public sealed record WorkloadPlacementRequest(
    WorkloadSpec Spec,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlySet<string> RefusedBy);

/// <summary>
/// An extra reason a node may not take a workload. The kernel's own eligibility rules always run first and
/// cannot be removed; a filter can only take more nodes away.
/// </summary>
public interface IPlacementFilter
{
    string Id { get; }

    /// <summary>Null when the node is acceptable; otherwise the reason, shown to the operator.</summary>
    string? Reject(WorkloadPlacementRequest request, NodeView node);
}

/// <summary>Orders the nodes that survived filtering. Higher is better; scores from all scorers are added.</summary>
public interface IPlacementScorer
{
    string Id { get; }

    decimal Score(WorkloadPlacementRequest request, NodeView node);
}

/// <summary>The chosen node, or null with the reason each node was passed over.</summary>
public sealed record PlacementDecision(string? NodeId, IReadOnlyDictionary<string, string> Rejections);
