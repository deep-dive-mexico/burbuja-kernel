using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Burbuja.Cluster.Abstractions;
using Burbuja.Cluster.Os;
using Burbuja.Cluster.Security;

namespace Burbuja.Cluster.Agent;

/// <summary>
/// The built-in workload kind: one of the programs this node's operator listed, run as a process tree the
/// node owns.
///
/// <para><b>The payload names a program; it never supplies one.</b> <c>{"program":"trainer","parameters":
/// {"shard":"2"}}</c> selects an entry of <c>Cluster:Agent:Programs</c> and fills parameters that entry
/// allows. The executable, its fixed arguments and its environment are the operator's. Each parameter becomes
/// a literal argument after its flag; nothing is passed through a shell that could reinterpret it. A request
/// for a program or parameter the node does not list is refused by the node itself, whatever the controller
/// says, so compromising the controller does not yield a way to run arbitrary commands on members.</para>
///
/// <para><b>Work outlives the node process.</b> Output goes straight to a log file rather than through a pipe
/// held by the node, so stopping or upgrading the node does not break the work's output and kill it. The
/// handle records the process id and its start time; a later node process adopts the work only when both
/// still match, which is what protects against a recycled process id.</para>
/// </summary>
internal sealed class ProcessWorkloadDriver(ClusterOptions options, IEnumerable<IOsExecutionAdapter> adapters) : IWorkloadDriver
{
    private const string DefaultPath = "/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin";
    private static readonly string[] Inherited = ["HOME", "USER", "LOGNAME", "LANG", "TMPDIR", "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS"];
    private readonly IOsExecutionAdapter _os = OsExecution.Select(adapters);

    public string Kind => ClusterConstants.Kinds.Process;

    public static string ProgramCapability(string program) => "program." + program;

    public WorkloadValidation Validate(WorkloadSpec spec)
    {
        if (!TryRead(spec.Payload, out var program, out _, out var error)) return WorkloadValidation.Invalid(error);
        return new WorkloadValidation([], [ProgramCapability(program)]);
    }

    public void Describe(NodeDescription description)
    {
        foreach (var name in options.Agent.Programs.Keys) description.Capabilities.Add(ProgramCapability(name));
    }

    private static bool TryRead(JsonObject payload, out string program, out Dictionary<string, string> parameters, out string error)
    {
        program = "";
        error = "";
        parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (payload["program"] is not JsonValue name || !name.TryGetValue(out string? text) || string.IsNullOrEmpty(text))
        {
            error = "payload.program must name a program";
            return false;
        }

        program = text;
        switch (payload["parameters"])
        {
            case null:
                return true;
            case JsonObject map:
                foreach (var (key, value) in map)
                {
                    if (value is not JsonValue item || !item.TryGetValue(out string? literal) || literal.Length > 8192 || literal.Contains('\0'))
                    {
                        error = $"payload.parameters.{key} must be a string of at most 8192 characters";
                        return false;
                    }

                    parameters[key] = literal;
                }

                return true;
            default:
                error = "payload.parameters must be an object of strings";
                return false;
        }
    }

    private LaunchCommand Command(WorkloadStart start)
    {
        // The workload's name becomes a log, a pid file and a working directory under this node's state.
        if (!ClusterNames.IsValid(start.Spec.Name)) throw new WorkloadRefusedException("the workload name is not valid");
        if (!TryRead(start.Spec.Payload, out var name, out var parameters, out var error)) throw new WorkloadRefusedException(error);
        if (!options.Agent.Programs.TryGetValue(name, out var program))
            throw new WorkloadRefusedException($"program '{name}' is not allowed on this node");
        if (!File.Exists(program.Executable)) throw new WorkloadRefusedException($"program '{name}' is not installed on this node");
        var unknown = parameters.Keys.FirstOrDefault(p => !program.ParameterFlags.ContainsKey(p));
        if (unknown is not null) throw new WorkloadRefusedException($"program '{name}' does not accept parameter '{unknown}'");

        var arguments = new List<string>(program.Arguments);
        foreach (var (parameter, value) in parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            arguments.Add(program.ParameterFlags[parameter]);
            arguments.Add(value);
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["PATH"] = DefaultPath };
        foreach (var variable in Inherited)
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value) environment[variable] = value;
        }

        foreach (var (variable, value) in program.Environment) environment[variable] = value;
        foreach (var (variable, value) in start.Environment) environment[variable] = value;

        var directory = program.WorkingDirectory.Length > 0 ? program.WorkingDirectory : Path.Combine(Root, "work", start.Spec.Name);
        Directory.CreateDirectory(directory);
        return new LaunchCommand(program.Executable, arguments, environment, directory);
    }

    private string Root => options.StateDirectory.Length > 0 ? options.StateDirectory : Path.Combine(Path.GetTempPath(), "burbuja-cluster-" + options.ResolvedNodeId);

    /// <summary>Where a workload's output is written on this node.</summary>
    public string LogPath(string workload) => Path.Combine(Root, "logs", workload + ".log");

    /// <summary>
    /// A fixed script that starts the work with its output pointed at a file, records the work's process id
    /// and waits for it, passing its exit status on. The command and its arguments arrive as positional
    /// parameters and are never parsed.
    ///
    /// <para><b>The work is deliberately not this process's direct child.</b> On macOS, suspending a direct
    /// child of a .NET process makes the runtime's child-signal handling spin, and every later process start
    /// in that process then blocks for good (witness: <c>ProcessDriverTests.The_whole_tree_is_suspended_
    /// resumed_and_given_its_grace_to_finish</c> hung exactly there before this launcher existed). With the
    /// shell in between, the node only ever suspends a grandchild, which the runtime is never told about,
    /// and the shell still hands back the real exit status.</para>
    /// </summary>
    private const string Launcher =
        "log=$1; pidfile=$2; shift 2; \"$@\" >>\"$log\" 2>&1 </dev/null & child=$!; echo \"$child\" > \"$pidfile\"; wait \"$child\"";

    public async ValueTask<IWorkloadInstance> StartAsync(WorkloadStart start, CancellationToken ct)
    {
        var command = _os.Wrap(Command(start), start.Spec.Execution, start.Devices);
        var info = new ProcessStartInfo { UseShellExecute = false, WorkingDirectory = command.WorkingDirectory };
        info.Environment.Clear();
        foreach (var (variable, value) in command.Environment) info.Environment[variable] = value;

        if (OperatingSystem.IsWindows())
        {
            info.FileName = command.FileName;
            foreach (var argument in command.Arguments) info.ArgumentList.Add(argument);
            var direct = Process.Start(info) ?? throw new IOException("The workload process could not be started.");
            return new ProcessInstance(direct, direct.Id, _os);
        }

        var log = LogPath(start.Spec.Name);
        var pidFile = Path.Combine(Root, "run", start.Spec.Name + ".pid");
        PrivateFiles.EnsureDirectory(Path.GetDirectoryName(log)!);
        PrivateFiles.EnsureDirectory(Path.GetDirectoryName(pidFile)!);
        File.Delete(pidFile);
        if (File.Exists(log) && new FileInfo(log).Length > 64 * 1024 * 1024) File.Delete(log);
        info.FileName = "/bin/sh";
        foreach (var argument in (string[])["-c", Launcher, "burbuja-workload", log, pidFile, command.FileName, .. command.Arguments])
            info.ArgumentList.Add(argument);

        var launcher = Process.Start(info) ?? throw new IOException("The workload process could not be started.");
        for (var attempt = 0; attempt < 500; attempt++)
        {
            if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid))
                return new ProcessInstance(launcher, pid, _os);
            if (launcher.HasExited) break;
            await Task.Delay(10, ct);
        }

        try { launcher.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        throw new IOException("The workload process did not report that it started.");
    }

    public ValueTask<IWorkloadInstance?> AdoptAsync(WorkloadStart start, string handle, CancellationToken ct)
    {
        var parts = handle.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var pid) || !long.TryParse(parts[1], out var startedMs))
            return ValueTask.FromResult<IWorkloadInstance?>(null);
        try
        {
            var process = Process.GetProcessById(pid);
            if (!process.HasExited && Math.Abs(new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds() - startedMs) < 2000)
                return ValueTask.FromResult<IWorkloadInstance?>(new ProcessInstance(process, _os));
            process.Dispose();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // No such process, or it is not ours to inspect: either way it is not the work we started.
        }

        return ValueTask.FromResult<IWorkloadInstance?>(null);
    }

    private sealed class ProcessInstance : IWorkloadInstance
    {
        private readonly Process _process;
        private readonly IOsExecutionAdapter _os;
        private readonly int _pid;
        private bool _paused;

        /// <summary>Work this node started: <paramref name="launcher"/> waits for it and carries its exit status.</summary>
        public ProcessInstance(Process launcher, int workloadPid, IOsExecutionAdapter os)
        {
            _process = launcher;
            _os = os;
            _pid = workloadPid;
            Handle = string.Create(CultureInfo.InvariantCulture, $"{_pid}:{StartedMs(workloadPid) ?? StartedMs(launcher.Id) ?? 0}");
            Completion = WatchOwnedAsync();
        }

        /// <summary>Work an earlier node process started, found again by its own process id.</summary>
        public ProcessInstance(Process workload, IOsExecutionAdapter os)
        {
            _process = workload;
            _os = os;
            _pid = workload.Id;
            Handle = string.Create(CultureInfo.InvariantCulture, $"{_pid}:{StartedMs(_pid) ?? 0}");
            Completion = WatchAdoptedAsync();
        }

        private static long? StartedMs(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds();
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }

        public string Handle { get; }
        public Task<WorkloadExit> Completion { get; }

        private async Task<WorkloadExit> WatchOwnedAsync()
        {
            await _process.WaitForExitAsync();
            var exit = new WorkloadExit(_process.ExitCode);
            _process.Dispose();
            return exit;
        }

        /// <summary>A process this node did not start cannot be waited on, and its exit status is not available.</summary>
        private async Task<WorkloadExit> WatchAdoptedAsync()
        {
            while (true)
            {
                try
                {
                    if (_process.HasExited) break;
                }
                catch (InvalidOperationException)
                {
                    break;
                }

                await Task.Delay(500);
            }

            _process.Dispose();
            return new WorkloadExit(null, "exit status unknown: the node restarted while this ran");
        }

        public async ValueTask PauseAsync(CancellationToken ct)
        {
            if (_paused || Completion.IsCompleted) return;
            await _os.SignalAsync(_pid, OsSignal.Pause, ct);
            _paused = true;
        }

        public async ValueTask ResumeAsync(CancellationToken ct)
        {
            if (!_paused || Completion.IsCompleted) return;
            await _os.SignalAsync(_pid, OsSignal.Resume, ct);
            _paused = false;
        }

        public async ValueTask StopAsync(TimeSpan grace, CancellationToken ct)
        {
            if (Completion.IsCompleted) return;

            // A suspended process cannot act on a request to finish, so it is resumed first.
            if (_paused) await ResumeAsync(CancellationToken.None);
            await _os.SignalAsync(_pid, OsSignal.Terminate, CancellationToken.None);
            try
            {
                await Completion.WaitAsync(grace, ct);
                return;
            }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException)
            {
                // Out of patience, or the node itself is shutting down.
            }

            await _os.SignalAsync(_pid, OsSignal.Kill, CancellationToken.None);
            try { await Completion.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); }
            catch (TimeoutException) { }
        }
    }
}
