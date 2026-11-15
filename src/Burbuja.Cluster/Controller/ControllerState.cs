using System.Text.Json.Serialization;
using Burbuja.Cluster.Abstractions;
using Burbuja.Cluster.Security;

namespace Burbuja.Cluster.Controller;

internal sealed class JoinTokenRecord
{
    public string TokenId { get; set; } = "";
    public string JoinKey { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}

internal sealed class NodeRecord
{
    public string NodeId { get; set; } = "";

    /// <summary>Base64 node key. Empty for the controller's own in-process node, which never signs.</summary>
    public string Key { get; set; } = "";
    public NodeDescriptor Descriptor { get; set; } = new() { NodeId = "" };
    public NodeAvailability Availability { get; set; }

    // Everything below is observed, not remembered: after a controller restart a node is unreachable until it
    // syncs again, and its loss clock starts at the restart rather than at a time the controller never saw.
    [JsonIgnore] public string? InstanceId { get; set; }
    [JsonIgnore] public long LastSequence { get; set; }
    [JsonIgnore] public List<string> RetiredInstances { get; } = [];
    [JsonIgnore] public DateTimeOffset LastSeen { get; set; }
    [JsonIgnore] public bool Synced { get; set; }
    [JsonIgnore] public NodeObservation? Observation { get; set; }
    [JsonIgnore] public DateTimeOffset? ObservedAt { get; set; }
    [JsonIgnore] public PressureHysteresis? Pressure { get; set; }
    [JsonIgnore] public NonceWindow Nonces { get; } = new();
}

internal sealed class WorkloadRecord
{
    public WorkloadSpec Spec { get; set; } = new() { Name = "" };
    public long Generation { get; set; }
    public string? NodeId { get; set; }
    public List<string> RefusedBy { get; set; } = [];

    [JsonIgnore] public WorkloadPhase Phase { get; set; } = WorkloadPhase.Pending;
    [JsonIgnore] public string? Reason { get; set; }
    [JsonIgnore] public DateTimeOffset Since { get; set; }
    [JsonIgnore] public int Restarts { get; set; }
    [JsonIgnore] public IReadOnlyDictionary<string, string> Rejections { get; set; } = new Dictionary<string, string>();
}

/// <summary>
/// Work that was taken away from a node and may still be running there. Until the node confirms it stopped
/// (or has been silent long enough to have stopped it itself), the work still occupies the node's capacity
/// and still holds its exclusive key. This is what keeps a move from becoming two copies.
/// </summary>
internal sealed class ReleaseRecord
{
    public string NodeId { get; set; } = "";
    public string Name { get; set; } = "";
    public long Generation { get; set; }
    public string? ExclusiveKey { get; set; }
    public Dictionary<string, decimal> Requests { get; set; } = new();

    /// <summary>How long the node may take to stop it once it starts stopping; part of how long a silent node is waited for.</summary>
    public double StopGraceSeconds { get; set; }
}

internal sealed class ControllerState
{
    public string ClusterId { get; set; } = "";

    /// <summary>
    /// The last generation handed out. One counter for the whole cluster, never reused: a workload deleted
    /// and created again under the same name gets a generation no node has ever seen.
    /// </summary>
    public long LastGeneration { get; set; }
    public Dictionary<string, JoinTokenRecord> Tokens { get; set; } = new();
    public Dictionary<string, NodeRecord> Nodes { get; set; } = new();
    public Dictionary<string, WorkloadRecord> Workloads { get; set; } = new();
    public List<ReleaseRecord> Releases { get; set; } = [];

    /// <summary>Keys of nodes an operator removed, kept so the removal can be proven to the node.</summary>
    public Dictionary<string, string> Removed { get; set; } = new();
}

/// <summary>
/// Several pressed readings to set, more clear ones to release. One bad sample is noise and one good sample
/// after a bad stretch is not a recovery; without the asymmetry placement flaps between nodes at exactly the
/// moment a host is struggling.
/// </summary>
internal sealed class PressureHysteresis(int badToSet, int goodToClear)
{
    private int _bad;
    private int _good;

    public bool Pressured { get; private set; }

    public void Sample(bool pressed)
    {
        if (pressed)
        {
            _good = 0;
            if (++_bad >= badToSet) Pressured = true;
        }
        else
        {
            _bad = 0;
            if (++_good >= goodToClear) Pressured = false;
        }
    }
}

/// <summary>One JSON document, replaced atomically. A null path keeps state in memory only.</summary>
internal sealed class ClusterFileStore<T>(string? path) where T : class
{
    public T? Load()
    {
        if (path is null || !File.Exists(path)) return null;
        return ClusterJson.Read<T>(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"The cluster state file '{path}' is unreadable. It was left in place; move it aside to start empty.");
    }

    public void Save(T value)
    {
        if (path is not null) PrivateFiles.WriteAtomic(path, ClusterJson.Write(value));
    }
}
