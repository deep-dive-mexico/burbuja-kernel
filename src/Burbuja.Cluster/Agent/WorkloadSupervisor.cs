using Burbuja.Cluster.Abstractions;
using Microsoft.Extensions.Logging;

namespace Burbuja.Cluster.Agent;

/// <summary>What a supervisor needs from the node it runs on.</summary>
internal sealed record SupervisorContext(
    string NodeId,
    AgentOptions Options,
    IReadOnlySet<string> Enforceable,
    Func<IReadOnlyList<NodeDevice>> Devices,
    DeviceLedger Ledger,
    InteractiveGate Gate,
    IReadOnlyDictionary<string, string> Environment,
    TimeProvider Time,
    ILogger Log,
    Action Changed);

/// <summary>
/// Keeps one generation of one workload in the state it was asked to be in: started, restarted according to
/// its policy, suspended while interactive work answers, and stopped with its grace when asked.
///
/// <para>A supervisor never outlives its generation. A changed spec or a new placement is a new supervisor,
/// created only after this one has finished stopping, so two generations of the same workload never run side
/// by side on a node.</para>
/// </summary>
internal sealed class WorkloadSupervisor
{
    private const string WaitingForDevice = "waiting for a lent device on this node";
    private readonly SupervisorContext _context;
    private readonly IWorkloadDriver? _driver;
    private readonly string? _adopt;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _instanceLock = new(1, 1);
    private IWorkloadInstance? _instance;
    private WorkloadPhase _phase = WorkloadPhase.Starting;
    private string? _reason;
    private int _restarts;
    private int? _exitCode;
    private DateTimeOffset _at;
    private bool _abandon;
    private TimeSpan? _graceLimit;

    /// <param name="adoptHandle">
    /// When set, this supervisor exists to take back work a previous node process left running. It adopts that
    /// work or does nothing: it never starts a fresh copy, because whether the node should still be running
    /// the work is for the controller to confirm first.
    /// </param>
    public WorkloadSupervisor(WorkloadAssignment assignment, IWorkloadDriver? driver, SupervisorContext context, string? adoptHandle = null)
    {
        Spec = assignment.Spec;
        Generation = assignment.Generation;
        _driver = driver;
        _context = context;
        _adopt = adoptHandle;
        _at = context.Time.GetUtcNow();
        Completion = Task.Run(RunAsync);
    }

    public WorkloadSpec Spec { get; }
    public long Generation { get; }
    public Task Completion { get; }

    /// <summary>
    /// Set when this supervisor ended without the work having run its course here (a key given up, nothing
    /// left to adopt). The node may then start the same generation again once the controller still asks for it.
    /// </summary>
    public bool Restartable { get; private set; }
    public string? Handle => _instance?.Handle;
    public bool Finished => Completion.IsCompleted;

    public WorkloadReport Report() => new(Spec.Name, Generation, _phase, _at, _reason, _restarts, _exitCode);

    /// <param name="restartable">The same generation may be started again later (see <see cref="Restartable"/>).</param>
    /// <param name="abandon">Let go of the work without stopping it, because another process now owns it.</param>
    /// <param name="graceLimit">Caps the work's own stop grace, for a stop that must finish within a bound.</param>
    public void RequestStop(string reason, bool restartable = false, bool abandon = false, TimeSpan? graceLimit = null)
    {
        if (_stop.IsCancellationRequested) return;
        Restartable = restartable;
        _abandon = abandon;
        _graceLimit = graceLimit;
        _reason = reason;
        _stop.Cancel();
    }

    private void Set(WorkloadPhase phase, string? reason = null)
    {
        _phase = phase;
        _reason = reason;
        _at = _context.Time.GetUtcNow();
        _context.Changed();
    }

    private async Task RunAsync()
    {
        var spec = Spec;
        try
        {
            // The node checks the name itself: it becomes file names here, and a name is not safe just
            // because the controller sent it.
            var refusal = (ClusterNames.IsValid(spec.Name) ? null : "the workload name is not valid")
                ?? NodeExecutionPolicy.Refusal(spec, _context.Options, _context.Enforceable, _context.Devices())
                ?? (_driver is null ? $"this node has no driver for kind '{spec.Kind}'" : null)
                ?? _driver!.Validate(spec).Errors.FirstOrDefault();
            if (refusal is not null)
            {
                Set(WorkloadPhase.Refused, refusal);
                return;
            }

            var quickFailures = 0;
            var adopt = _adopt;
            while (!_stop.IsCancellationRequested)
            {
                // Work that yields does not start in the middle of an interactive burst either.
                while (adopt is null && spec.Execution.YieldToInteractive && _context.Gate.Busy && !_stop.IsCancellationRequested)
                    await Task.Delay(TimeSpan.FromMilliseconds(50), _context.Time);
                if (_stop.IsCancellationRequested) break;

                var devices = _context.Ledger.Acquire(spec);
                if (devices is null)
                {
                    // Another workload on this node still holds the device (it may be on its way out). That
                    // passes, so wait for it rather than turn the whole node down.
                    if (_reason != WaitingForDevice) Set(WorkloadPhase.Starting, WaitingForDevice);
                    try { await Task.Delay(TimeSpan.FromMilliseconds(500), _context.Time, _stop.Token); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                var start = new WorkloadStart(spec, Generation, _context.NodeId, Environment(), devices);
                var startedAt = _context.Time.GetUtcNow();

                // Work that cannot be started is treated like work that started and failed at once: its
                // restart policy decides whether to try again, with the same backoff.
                IWorkloadInstance? instance = null;
                WorkloadExit? exit = null;
                try
                {
                    if (adopt is not null)
                    {
                        instance = await _driver!.AdoptAsync(start, adopt, CancellationToken.None);
                        if (instance is null)
                        {
                            Restartable = true;
                            Set(WorkloadPhase.Stopped, "it was no longer running when the node restarted");
                            return;
                        }
                    }
                    else
                    {
                        instance = await _driver!.StartAsync(start, CancellationToken.None);
                    }
                }
                catch (WorkloadRefusedException refused)
                {
                    Set(WorkloadPhase.Refused, refused.Message);
                    return;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _context.Log.LogWarning(e, "Workload {Workload} could not start", spec.Name);
                    exit = new WorkloadExit(null, "it could not be started on this node");
                }

                adopt = null;
                if (instance is not null)
                {
                    await _instanceLock.WaitAsync();
                    _instance = instance;
                    _instanceLock.Release();
                    Set(WorkloadPhase.Running);
                    await ApplyGateAsync();

                    var stopped = Task.Delay(Timeout.Infinite, _stop.Token);
                    await Task.WhenAny(instance.Completion, stopped);
                    if (!instance.Completion.IsCompleted)
                    {
                        var why = _reason;
                        if (_abandon)
                        {
                            Set(WorkloadPhase.Stopped, why);
                            return;
                        }

                        Set(WorkloadPhase.Stopping, why);

                        // Let a suspend or resume that is already under way finish; later ones see the stop
                        // and do nothing. The lock is not held across the grace period, which can be long.
                        await _instanceLock.WaitAsync();
                        _instanceLock.Release();
                        var grace = _graceLimit is { } limit && limit < spec.Lifecycle.StopGrace ? limit : spec.Lifecycle.StopGrace;
                        await instance.StopAsync(grace, CancellationToken.None);
                        Set(WorkloadPhase.Stopped, why);
                        return;
                    }

                    exit = await instance.Completion;
                }

                _exitCode = exit!.ExitCode;
                _context.Ledger.Release(spec.Name);
                var restart = spec.Lifecycle.Restart switch
                {
                    RestartPolicy.Always => true,
                    RestartPolicy.OnFailure => !exit.Succeeded,
                    _ => false,
                };
                if (!restart)
                {
                    Set(exit.Succeeded ? WorkloadPhase.Completed : WorkloadPhase.Failed, exit.Reason ?? $"exited with code {exit.ExitCode}");
                    return;
                }

                // Work that dies straight after starting is backed off, so a broken program does not spin the machine.
                quickFailures = _context.Time.GetUtcNow() - startedAt < TimeSpan.FromSeconds(10) ? quickFailures + 1 : 0;
                var delay = TimeSpan.FromTicks(Math.Min(
                    TimeSpan.FromMinutes(5).Ticks,
                    spec.Lifecycle.RestartDelay.Ticks * (1L << Math.Min(quickFailures, 6))));
                _restarts++;
                Set(WorkloadPhase.Starting, exit.Reason ?? $"restarting after exit code {exit.ExitCode}");
                try { await Task.Delay(delay, _context.Time, _stop.Token); }
                catch (OperationCanceledException) { break; }
            }

            Set(WorkloadPhase.Stopped, _reason);
        }
        catch (Exception e)
        {
            // Whatever a driver throws, the supervisor ends in a state the controller can see. A fault that
            // left the phase at "starting" would look alive for ever and hold its capacity with it.
            _context.Log.LogError(e, "Workload {Workload} supervisor failed", spec.Name);
            _phase = WorkloadPhase.Failed;
            _reason = "the node could not supervise it (" + e.GetType().Name + ")";
            _at = _context.Time.GetUtcNow();
        }
        finally
        {
            _context.Ledger.Release(spec.Name);
            _instance = null;
            try { _context.Changed(); }
            catch (Exception e) { _context.Log.LogWarning("Workload {Workload} change was not recorded: {Error}", spec.Name, e.GetType().Name); }
        }
    }

    private Dictionary<string, string> Environment() => new(_context.Environment, StringComparer.Ordinal)
    {
        ["BURBUJA_WORKLOAD"] = Spec.Name,
        ["BURBUJA_GENERATION"] = Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["BURBUJA_NODE"] = _context.NodeId,
    };

    /// <summary>
    /// Bring this work in line with the interactive gate as it is <em>now</em>. The gate is read under the
    /// instance lock rather than passed in, so calls that arrive out of order still leave the work in the
    /// state of the latest change: whichever call runs last reads the final answer.
    /// </summary>
    public async Task ApplyGateAsync()
    {
        if (!Spec.Execution.YieldToInteractive || _stop.IsCancellationRequested) return;
        await _instanceLock.WaitAsync();
        try
        {
            if (_instance is not { } instance || instance.Completion.IsCompleted || _stop.IsCancellationRequested) return;
            var busy = _context.Gate.Busy;
            if (busy && _phase == WorkloadPhase.Running)
            {
                await instance.PauseAsync(CancellationToken.None);
                Set(WorkloadPhase.Paused, "yielding to interactive work on this node");
            }
            else if (!busy && _phase == WorkloadPhase.Paused)
            {
                await instance.ResumeAsync(CancellationToken.None);
                Set(WorkloadPhase.Running);
            }
        }
        catch (NotSupportedException)
        {
            // The platform cannot suspend; a strict profile would not have been placed here at all.
        }
        finally
        {
            _instanceLock.Release();
        }
    }
}
