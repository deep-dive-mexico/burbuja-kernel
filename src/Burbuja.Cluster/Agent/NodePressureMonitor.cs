using Burbuja.Cluster.Abstractions;
using Burbuja.Cluster.Controller;

namespace Burbuja.Cluster.Agent;

/// <summary>
/// Holds what this node knows about pressure, as immutable snapshots that any thread can read.
///
/// <para>The node applies the same hysteresis to its own readings that the controller applies to every
/// member's, so a node can act on its own verdict (refuse new claims, hold back batch work) with no
/// controller in the path, and a single machine gets the behaviour without any network at all.</para>
/// </summary>
internal sealed class NodePressureMonitor(ClusterOptions options, TimeProvider time) : INodePressure
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>();
    private readonly PressureHysteresis _hysteresis = new(options.Controller.PressureBadSamples, options.Controller.PressureGoodSamples);
    private readonly TimeSpan _maxAge = TimeSpan.FromSeconds(options.Controller.ObservationMaxAgeSeconds);
    private volatile NodePressure _local = NodePressure.Unknown;
    private volatile Advice _advice = new(None, DateTimeOffset.MinValue);

    private sealed record Advice(IReadOnlySet<string> Nodes, DateTimeOffset At);

    public NodePressure Local
    {
        get
        {
            var local = _local;
            return local.At is { } at && time.GetUtcNow() - at <= _maxAge ? local : NodePressure.Unknown;
        }
    }

    public IReadOnlySet<string> PressuredNodes
    {
        get
        {
            var advice = _advice;
            return time.GetUtcNow() - advice.At <= _maxAge ? advice.Nodes : None;
        }
    }

    /// <summary>One reading of this node. A reading that says "unknown" counts as pressed, as it does on the controller.</summary>
    public void Sample(NodeObservation observation)
    {
        _hysteresis.Sample(observation.Pressure is PressureLevel.Unknown or >= PressureLevel.Elevated);
        _local = new NodePressure(_hysteresis.Pressured, true, observation.Pressure, observation.Reasons, time.GetUtcNow());
    }

    /// <summary>What the controller said about the whole cluster in its last answer.</summary>
    public void Advise(IReadOnlyList<string> pressuredNodes) =>
        _advice = new Advice(pressuredNodes.ToHashSet(StringComparer.Ordinal), time.GetUtcNow());
}
