namespace Burbuja.Agents.Abstractions.Nodes;

public sealed record NodeCapacity(int Configured, int Current);

public sealed record NodeSnapshot(
    string NodeId,
    DateTimeOffset LastHeartbeat,
    NodeCapacity Capacity,
    string Status);

public interface INodeDirectory
{
    void Heartbeat(string nodeId, DateTimeOffset at);
    void ObserveThrottle(string nodeId);
    void ObserveSuccess(string nodeId);
    NodeSnapshot? Get(string nodeId);
    IReadOnlyList<NodeSnapshot> List();
    void Kill(string nodeId);
}
