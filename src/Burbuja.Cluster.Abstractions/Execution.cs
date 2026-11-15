namespace Burbuja.Cluster.Abstractions;

/// <summary>How the operating system should treat the work relative to everything else on the machine.</summary>
public enum ExecutionPriority
{
    Interactive,
    Normal,

    /// <summary>Yields the processor to anything more important (efficiency cores, low scheduler weight).</summary>
    Background,

    /// <summary>Runs only when nothing else wants the processor.</summary>
    Idle,
}

public enum NetworkAccess
{
    /// <summary>Whatever the node process itself has.</summary>
    Inherit,
    LoopbackOnly,
    None,
}

public sealed record ExecutionLimits
{
    public decimal? CpuCores { get; init; }
    public decimal? MemoryMb { get; init; }

    /// <summary>Processor list in the operating system's own notation, e.g. <c>0-7,10-17</c>.</summary>
    public string? CpuSet { get; init; }
}

/// <summary>
/// What a workload asks of the operating system, in one vocabulary for every platform.
///
/// <para><b>Asking is not getting, and the difference is never silent.</b> Each member maps to an aspect in
/// <see cref="OsAspects"/>. A node advertises the aspects its adapter can really enforce, and a workload whose
/// profile needs an aspect a node lacks is not placed there. macOS has no memory ceiling for a process tree;
/// a profile with <see cref="ExecutionLimits.MemoryMb"/> therefore lands on a node that has one, or stays
/// pending with that reason, rather than running unlimited under a limit somebody believes is in force. Set
/// <see cref="Strict"/> to false to accept best effort instead.</para>
/// </summary>
public sealed record ExecutionProfile
{
    public ExecutionPriority Priority { get; init; } = ExecutionPriority.Normal;
    public ExecutionLimits Limits { get; init; } = new();

    /// <summary>
    /// Suspend this work while interactive work on the same node is answering, and resume it afterwards. The
    /// pause is node-local and takes effect in milliseconds; it is much finer than moving or stopping work.
    /// </summary>
    public bool YieldToInteractive { get; init; }

    public NetworkAccess Network { get; init; } = NetworkAccess.Inherit;

    /// <summary>Operating-system account to run as; null runs as the node's own account.</summary>
    public string? RunAs { get; init; }

    /// <summary>Specific device ids to use. Empty lets the node choose from what it lends.</summary>
    public IReadOnlyList<string> Devices { get; init; } = [];

    public bool Strict { get; init; } = true;
}

/// <summary>The things an adapter may be able to enforce. Advertised by nodes as <c>os.&lt;aspect&gt;</c>.</summary>
public static class OsAspects
{
    public const string Priority = "priority";
    public const string Pause = "pause";
    public const string CpuLimit = "cpu-limit";
    public const string MemoryLimit = "memory-limit";
    public const string CpuSet = "cpu-set";
    public const string NetworkIsolation = "network-isolation";
    public const string RunAs = "run-as";

    public static string Capability(string aspect) => ClusterConstants.Capabilities.OsPrefix + aspect;

    /// <summary>The aspects a profile depends on. A default profile depends on none.</summary>
    public static IReadOnlyList<string> RequiredBy(ExecutionProfile profile)
    {
        var aspects = new List<string>();
        if (profile.Priority != ExecutionPriority.Normal) aspects.Add(Priority);
        if (profile.YieldToInteractive) aspects.Add(Pause);
        if (profile.Limits.CpuCores is not null) aspects.Add(CpuLimit);
        if (profile.Limits.MemoryMb is not null) aspects.Add(MemoryLimit);
        if (profile.Limits.CpuSet is { Length: > 0 }) aspects.Add(CpuSet);
        if (profile.Network != NetworkAccess.Inherit) aspects.Add(NetworkIsolation);
        if (profile.RunAs is { Length: > 0 }) aspects.Add(RunAs);
        return aspects;
    }
}

/// <summary>A process to start: literal arguments, never a shell string.</summary>
public sealed record LaunchCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string WorkingDirectory);

public enum OsSignal
{
    Pause,
    Resume,

    /// <summary>Ask politely; the work may take its stop grace to finish.</summary>
    Terminate,
    Kill,
}

/// <summary>
/// The one place that knows how a platform expresses priority, limits and suspension. Everything above it
/// speaks <see cref="ExecutionProfile"/>; a plugin adds a platform or a stronger mechanism (a container
/// runtime, a sandbox) by registering another adapter with a higher <see cref="Order"/>.
/// </summary>
public interface IOsExecutionAdapter
{
    string Id { get; }

    /// <summary>Among adapters usable on this machine the highest order wins.</summary>
    int Order { get; }

    bool IsUsable { get; }

    /// <summary>What this adapter enforces on this machine, in <see cref="OsAspects"/> names.</summary>
    IReadOnlySet<string> Aspects { get; }

    /// <summary>Rewrite a launch so the profile holds from the first instruction the work executes.</summary>
    LaunchCommand Wrap(LaunchCommand command, ExecutionProfile profile, IReadOnlyList<NodeDevice> devices);

    /// <summary>Deliver a signal to the whole process tree rooted at <paramref name="rootProcessId"/>.</summary>
    ValueTask SignalAsync(int rootProcessId, OsSignal signal, CancellationToken ct);
}
