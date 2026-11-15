using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Burbuja.Cluster.Abstractions;

namespace Burbuja.Cluster.Os;

/// <summary>Signals for a whole process tree on POSIX systems.</summary>
internal static class ProcessTree
{
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);

    // SIGSTOP and SIGCONT have different numbers on Linux and on the BSD family; SIGTERM and SIGKILL do not.
    private static int Number(OsSignal signal) => signal switch
    {
        OsSignal.Pause => OperatingSystem.IsLinux() ? 19 : 17,
        OsSignal.Resume => OperatingSystem.IsLinux() ? 18 : 19,
        OsSignal.Terminate => 15,
        _ => 9,
    };

    /// <summary>The root and every descendant alive right now, parents before children.</summary>
    public static async ValueTask<List<int>> MembersAsync(int root, CancellationToken ct)
    {
        var members = new List<int> { root };
        var ps = ProbeCommand.Find("/bin/ps", "/usr/bin/ps");
        var listing = ps is null ? null : await ProbeCommand.ReadAsync(ps, ["-axo", "pid=,ppid="], TimeSpan.FromSeconds(3), ct);
        if (listing is null) return members;
        var children = new Dictionary<int, List<int>>();
        foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var pid) || !int.TryParse(parts[1], out var parent)) continue;
            if (!children.TryGetValue(parent, out var list)) children[parent] = list = [];
            list.Add(pid);
        }

        for (var i = 0; i < members.Count; i++)
        {
            if (children.TryGetValue(members[i], out var list)) members.AddRange(list);
        }

        return members;
    }

    public static async ValueTask SignalAsync(int root, OsSignal signal, CancellationToken ct)
    {
        var members = await MembersAsync(root, ct);

        // Stop parents first so they cannot start more children; kill children first so a parent cannot
        // restart them on the way down.
        if (signal is OsSignal.Kill or OsSignal.Terminate) members.Reverse();
        var number = Number(signal);
        foreach (var pid in members)
        {
            if (pid > 1) _ = Kill(pid, number); // A process that already exited is not an error.
        }
    }
}

internal abstract class PosixExecutionAdapter : IOsExecutionAdapter
{
    protected const string Nice = "/usr/bin/nice";

    public abstract string Id { get; }
    public int Order => 10;
    public abstract bool IsUsable { get; }
    public abstract IReadOnlySet<string> Aspects { get; }

    public abstract LaunchCommand Wrap(LaunchCommand command, ExecutionProfile profile, IReadOnlyList<NodeDevice> devices);

    public ValueTask SignalAsync(int rootProcessId, OsSignal signal, CancellationToken ct) => ProcessTree.SignalAsync(rootProcessId, signal, ct);

    /// <summary>Put <paramref name="prefix"/> in front of a command. Every wrapper used here replaces itself with the command, so the process id stays the work's own.</summary>
    protected static LaunchCommand Prefix(LaunchCommand command, params string[] prefix) =>
        prefix.Length == 0 ? command : command with { FileName = prefix[0], Arguments = [.. prefix[1..], command.FileName, .. command.Arguments] };
}

/// <summary>
/// macOS: quality-of-service classes through <c>taskpolicy</c>, plus <c>nice</c> for the lowest class. A
/// background task is kept off the performance cores and throttled for I/O, which is what "yield to
/// everything else" means on Apple Silicon. macOS offers no ceiling on a process tree's memory or processor
/// share, so those aspects are not advertised and a strict profile that needs them is placed elsewhere.
/// </summary>
internal sealed class MacOsExecutionAdapter : PosixExecutionAdapter
{
    private const string TaskPolicy = "/usr/sbin/taskpolicy";

    public override string Id => "macos";
    public override bool IsUsable => OperatingSystem.IsMacOS();
    public override IReadOnlySet<string> Aspects { get; } = new HashSet<string> { OsAspects.Priority, OsAspects.Pause };

    public override LaunchCommand Wrap(LaunchCommand command, ExecutionProfile profile, IReadOnlyList<NodeDevice> devices) => profile.Priority switch
    {
        ExecutionPriority.Background => Prefix(command, TaskPolicy, "-b"),
        ExecutionPriority.Idle => Prefix(command, TaskPolicy, "-b", Nice, "-n", "19"),
        _ => command,
    };
}

/// <summary>
/// Linux: <c>nice</c> for priority, and a transient systemd scope for limits when the control-group
/// controllers they need are actually delegated to this account.
///
/// <para><b>Delegation is checked, not assumed.</b> <c>systemd-run</c> accepts a <c>CPUQuota</c> for a user
/// scope whether or not the <c>cpu</c> controller was delegated to the user manager, and without delegation
/// the property does nothing. So each limit is advertised only when its controller is listed for this
/// account, and a node without it says it cannot enforce the limit instead of pretending.</para>
///
/// <para>Accelerators are selected with <c>CUDA_VISIBLE_DEVICES</c>, set on every launch: work that was given
/// no device sees none. That keeps cooperating programs off devices the node did not lend; it is a convention
/// the program honours, not a barrier.</para>
/// </summary>
internal sealed class LinuxExecutionAdapter : PosixExecutionAdapter
{
    private const string SystemdRun = "/usr/bin/systemd-run";
    private readonly bool _system;
    private readonly HashSet<string> _aspects = [OsAspects.Priority, OsAspects.Pause];
    private readonly bool _scopes;

    public LinuxExecutionAdapter()
        : this(Environment.UserName == "root", File.Exists(SystemdRun) ? ReadControllers(Environment.UserName == "root") : null)
    {
    }

    /// <param name="system">Use the system manager (running as root) rather than the user manager.</param>
    /// <param name="controllers">Delegated control-group controllers; null when scopes cannot be used at all.</param>
    internal LinuxExecutionAdapter(bool system, IReadOnlySet<string>? controllers)
    {
        _system = system;
        _scopes = controllers is not null;
        if (controllers is null) return;
        if (controllers.Contains("cpu")) _aspects.Add(OsAspects.CpuLimit);
        if (controllers.Contains("memory")) _aspects.Add(OsAspects.MemoryLimit);
        if (controllers.Contains("cpuset")) _aspects.Add(OsAspects.CpuSet);
    }

    public override string Id => "linux";
    public override bool IsUsable => OperatingSystem.IsLinux();
    public override IReadOnlySet<string> Aspects => _aspects;

    private static IReadOnlySet<string>? ReadControllers(bool system)
    {
        try
        {
            var path = "/sys/fs/cgroup/cgroup.controllers";
            if (!system)
            {
                var uid = File.ReadLines("/proc/self/status").FirstOrDefault(l => l.StartsWith("Uid:", StringComparison.Ordinal))
                    ?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
                if (uid is null || Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is not { Length: > 0 }) return null;
                path = $"/sys/fs/cgroup/user.slice/user-{uid}.slice/user@{uid}.service/cgroup.controllers";
            }

            return File.Exists(path) ? File.ReadAllText(path).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public override LaunchCommand Wrap(LaunchCommand command, ExecutionProfile profile, IReadOnlyList<NodeDevice> devices)
    {
        var environment = new Dictionary<string, string>(command.Environment)
        {
            ["CUDA_VISIBLE_DEVICES"] = string.Join(',', devices.Where(d => d.Backend == ClusterConstants.Capabilities.Cuda).Select(d => d.Id)),
        };
        var wrapped = command with { Environment = environment };
        wrapped = profile.Priority switch
        {
            ExecutionPriority.Background => Prefix(wrapped, Nice, "-n", "10"),
            ExecutionPriority.Idle => Prefix(wrapped, Nice, "-n", "19"),
            _ => wrapped,
        };

        var properties = new List<string>();
        var limits = profile.Limits;
        if (limits.MemoryMb is { } memory && _aspects.Contains(OsAspects.MemoryLimit))
        {
            properties.Add($"MemoryMax={memory.ToString("0", CultureInfo.InvariantCulture)}M");
            properties.Add("MemorySwapMax=0");
        }

        if (_aspects.Contains(OsAspects.CpuLimit))
        {
            if (limits.CpuCores is { } cores) properties.Add($"CPUQuota={(cores * 100).ToString("0", CultureInfo.InvariantCulture)}%");
            var weight = profile.Priority switch
            {
                ExecutionPriority.Interactive => 1000,
                ExecutionPriority.Background => 20,
                ExecutionPriority.Idle => 1,
                _ => 100,
            };
            if (weight != 100) properties.Add($"CPUWeight={weight}");
        }

        if (limits.CpuSet is { Length: > 0 } set && _aspects.Contains(OsAspects.CpuSet)) properties.Add($"AllowedCPUs={set}");
        if (!_scopes || properties.Count == 0) return wrapped;

        var scope = new List<string> { SystemdRun };
        if (!_system) scope.Add("--user");
        scope.AddRange(["--scope", "--quiet", "--collect"]);
        foreach (var property in properties) scope.AddRange(["-p", property]);
        scope.Add("--");
        return Prefix(wrapped, [.. scope]);
    }
}

/// <summary>
/// The fallback for a platform with no adapter of its own. It starts work unchanged and can only end it, so
/// it advertises no aspects: a strict profile that needs priority, limits or pausing is not placed here.
/// </summary>
internal sealed class PortableExecutionAdapter : IOsExecutionAdapter
{
    public string Id => "portable";
    public int Order => 0;
    public bool IsUsable => true;
    public IReadOnlySet<string> Aspects { get; } = new HashSet<string>();

    public LaunchCommand Wrap(LaunchCommand command, ExecutionProfile profile, IReadOnlyList<NodeDevice> devices) => command;

    public ValueTask SignalAsync(int rootProcessId, OsSignal signal, CancellationToken ct)
    {
        if (signal is OsSignal.Pause or OsSignal.Resume) throw new NotSupportedException("This platform cannot suspend a process tree.");
        try
        {
            using var process = Process.GetProcessById(rootProcessId);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            // Already gone.
        }

        return ValueTask.CompletedTask;
    }
}

internal static class OsExecution
{
    public static IOsExecutionAdapter Select(IEnumerable<IOsExecutionAdapter> adapters) =>
        adapters.Where(a => a.IsUsable).OrderByDescending(a => a.Order).First();
}
