namespace Burbuja.Cluster.Abstractions;

public sealed record NodePlatform(string Os, string Architecture, string? OsVersion = null);

/// <summary>
/// A discrete device a workload can be given, today an accelerator. <paramref name="Lendable"/> is the node
/// operator's decision, not a fact about the hardware: a GPU that also drives the desktop exists and is still
/// not the cluster's to hand out.
/// </summary>
public sealed record NodeDevice(string Kind, string Id, string Name, string? Backend = null, decimal? MemoryMb = null, bool Lendable = true);

/// <summary>
/// What a node says it is. Everything here changes rarely (hardware, labels, what the operator lends); the
/// moving numbers are in <see cref="NodeObservation"/>.
///
/// <para><b><see cref="Capacity"/> is what the node lends, not what it has.</b> The scheduler never sees the
/// machine's real totals; it sees the share its operator offered. That is how a workstation that other people
/// use, or a host with production services of its own, joins a cluster without handing it the whole box.</para>
/// </summary>
public sealed record NodeDescriptor
{
    public required string NodeId { get; init; }
    public string Name { get; init; } = "";
    public NodePlatform Platform { get; init; } = new("unknown", "unknown");
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public IReadOnlyDictionary<string, decimal> Capacity { get; init; } = ResourceMath.Empty;
    public IReadOnlyList<NodeDevice> Devices { get; init; } = [];

    /// <summary>Workload kinds this node has a driver for <em>and</em> its operator allows.</summary>
    public IReadOnlyList<string> Kinds { get; init; } = [];

    /// <summary>Where this node can be reached. Hints only: a reader verifies each one answers as this cluster.</summary>
    public IReadOnlyList<AdvertisedEndpoint> Endpoints { get; init; } = [];
}

/// <summary>Ordered so that a fold over several opinions is a max: the node is as pressed as its worst signal.</summary>
public enum PressureLevel
{
    /// <summary>No fresh reading. Never treated as "fine": see <see cref="WorkloadClass"/> for who may still start.</summary>
    Unknown = 0,
    Normal = 1,
    Elevated = 2,
    Critical = 3,
}

/// <summary>
/// One reading of a node, taken by the node itself. <see cref="Available"/> is measured free capacity and is
/// advisory; admission is decided on declared requests against <see cref="NodeDescriptor.Capacity"/>, so two
/// schedulers reading the same state reach the same answer whatever the load was at that instant.
/// </summary>
public sealed record NodeObservation
{
    public DateTimeOffset At { get; init; }
    public IReadOnlyDictionary<string, decimal> Available { get; init; } = ResourceMath.Empty;
    public PressureLevel Pressure { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];
}

/// <summary>What the operator wants from a node. Liveness is observed separately and never written by hand.</summary>
public enum NodeAvailability
{
    Active,

    /// <summary>Keeps what it runs, receives nothing new.</summary>
    Cordoned,

    /// <summary>Receives nothing new and gives up every workload that is allowed to move.</summary>
    Draining,
}

/// <summary>Mutable view a facts source fills in. Later sources may refine what earlier ones wrote.</summary>
public sealed class NodeDescription
{
    public NodePlatform Platform { get; set; } = new("unknown", "unknown");
    public Dictionary<string, string> Labels { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Capabilities { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, decimal> Capacity { get; } = new(StringComparer.Ordinal);
    public List<NodeDevice> Devices { get; } = [];
}

public sealed class NodeReading
{
    public Dictionary<string, decimal> Available { get; } = new(StringComparer.Ordinal);
    public PressureLevel Pressure { get; private set; }
    public List<string> Reasons { get; } = [];

    /// <summary>Raise the pressure; a source can never lower what another one reported.</summary>
    public void Report(PressureLevel level, string? reason = null)
    {
        if (level > Pressure) Pressure = level;
        if (reason is { Length: > 0 } && level >= PressureLevel.Elevated) Reasons.Add(reason);
    }
}

/// <summary>
/// A source of facts about the machine: processors and memory, an accelerator vendor, a custom sensor. A
/// source that cannot read something leaves it out; it never writes a zero it did not measure.
/// </summary>
public interface INodeFactsSource
{
    string Id { get; }

    ValueTask DescribeAsync(NodeDescription description, CancellationToken ct);

    ValueTask ObserveAsync(NodeReading reading, CancellationToken ct);
}
