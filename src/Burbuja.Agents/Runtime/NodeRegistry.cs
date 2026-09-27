using System.Collections.Concurrent;
using Burbuja.Agents.Abstractions.Nodes;

namespace Burbuja.Agents.Runtime;

public sealed class NodeRegistry : INodeDirectory
{
    private readonly ConcurrentDictionary<string, Mutable> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _configured;

    public NodeRegistry(int configured = 4) => _configured = Math.Max(1, configured);

    public void Heartbeat(string nodeId, DateTimeOffset at)
    {
        var n = _nodes.GetOrAdd(nodeId, _ => new Mutable(_configured));
        n.LastHeartbeat = at;
        if (!string.Equals(n.Status, "killed", StringComparison.OrdinalIgnoreCase))
            n.Status = "ready";
    }

    public void ObserveThrottle(string nodeId)
    {
        var n = _nodes.GetOrAdd(nodeId, _ => new Mutable(_configured));
        n.Current = Math.Max(0, n.Current - 1);
    }

    public void ObserveSuccess(string nodeId)
    {
        var n = _nodes.GetOrAdd(nodeId, _ => new Mutable(_configured));
        n.Current = n.Configured;
    }

    public NodeSnapshot? Get(string nodeId) =>
        _nodes.TryGetValue(nodeId, out var n) ? n.Snapshot(nodeId) : null;

    public IReadOnlyList<NodeSnapshot> List() =>
        _nodes.Select(kv => kv.Value.Snapshot(kv.Key)).OrderBy(n => n.NodeId, StringComparer.Ordinal).ToArray();

    public void Kill(string nodeId)
    {
        var n = _nodes.GetOrAdd(nodeId, _ => new Mutable(_configured));
        n.Status = "killed";
    }

    private sealed class Mutable(int configured)
    {
        public int Configured { get; } = configured;
        public int Current { get; set; } = configured;
        public DateTimeOffset LastHeartbeat { get; set; }
        public string Status { get; set; } = "ready";

        public NodeSnapshot Snapshot(string id) =>
            new(id, LastHeartbeat, new NodeCapacity(Configured, Current), Status);
    }
}
