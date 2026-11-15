namespace Burbuja.Cluster.Abstractions;

/// <summary>
/// This node's own verdict on its load, after hysteresis.
/// </summary>
/// <param name="Pressured">Several pressed readings in a row set it; more clear ones release it.</param>
/// <param name="Known">
/// False when the node has no recent reading of itself. An unknown verdict is not a clear one: what to do
/// without it is the reader's decision, and differs by what the reader protects (work somebody is waiting
/// for usually carries on; work nobody is waiting for usually does not start).
/// </param>
public sealed record NodePressure(bool Pressured, bool Known, PressureLevel Level, IReadOnlyList<string> Reasons, DateTimeOffset? At)
{
    public static readonly NodePressure Unknown = new(false, false, PressureLevel.Unknown, [], null);
}

/// <summary>
/// Pressure as a node can read it without doing any work: its own verdict, and which members the controller
/// last said were pressed. Registered on every host that is a cluster member.
///
/// <para><b>Both members only read cached state.</b> They are safe to call from a game loop or any other path
/// that must not block or perform I/O; the node refreshes them on its own schedule.</para>
///
/// <para>This is the seam for anything that should hold back under load without knowing how load is measured:
/// a realtime admission rule, a placement strategy that avoids pressed nodes, a batch job deciding whether to
/// start. It replaces the need to pass that verdict between processes as a file.</para>
/// </summary>
public interface INodePressure
{
    NodePressure Local { get; }

    /// <summary>
    /// Ids of the members the controller reported as under pressure, as of the last successful sync. Empty
    /// when that advice is older than the node trusts, so stale advice never keeps excluding a node.
    /// </summary>
    IReadOnlySet<string> PressuredNodes { get; }
}
