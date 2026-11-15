using Burbuja.Cluster.Abstractions;
using Burbuja.Cluster.Controller;
using Burbuja.Cluster.Networks;
using Burbuja.Cluster.Os;
using Burbuja.Cluster.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Burbuja.Cluster.Agent;

internal sealed record NodeAgentStatus(
    string NodeId,
    string Route,
    string Link,
    DateTimeOffset? LastSync,
    string? LastProblem,
    bool Interactive,
    IReadOnlyList<string> Holders,
    IReadOnlyList<WorkloadReport> Workloads);

/// <summary>
/// The part of the kernel that makes a machine a cluster member. It describes the machine, calls its
/// controller on an interval, and makes the work running here match what the controller last asked for.
///
/// <para><b>It only dials out.</b> Nothing ever connects to an agent, so a machine behind NAT joins the
/// same way as any other. A machine that may be reached but may not open connections itself is not covered:
/// it needs a path to the controller that the operator provides.</para>
///
/// <para><b>It keeps working when the controller is away.</b> Losing contact stops nothing by itself: the
/// node carries on with its last instructions and reports when contact returns. The one exception is work
/// holding an exclusive key, which the node stops once the key's time to live has passed without contact,
/// because only then may the controller safely hand the key to another node.</para>
///
/// <para><b>The fence is this process, so exclusive work does not outlive it.</b> Other work is left running
/// when the node process stops and is adopted by the next one. Work holding an exclusive key is stopped
/// first, because with the agent gone nothing on the machine could give the key up. If the process dies
/// without the chance to do that, the next one adopts the work and fences it against the last contact the
/// dead process recorded, not against its own start.</para>
///
/// <para><b>This is the single owner of retries.</b> The link below makes one attempt per call; the backoff,
/// the decision to carry on and the decision to stand down all live here.</para>
/// </summary>
internal sealed class NodeAgent : BackgroundService
{
    private static readonly TimeSpan FenceCheckEvery = TimeSpan.FromMilliseconds(200);

    private readonly ClusterOptions _options;
    private readonly IControllerLink _link;
    private readonly INodeFactsSource[] _facts;
    private readonly Dictionary<string, IWorkloadDriver> _drivers;
    private readonly IOsExecutionAdapter _os;
    private readonly EndpointAdvertiser _advertiser;
    private readonly InteractiveGate _gate;
    private readonly NodePressureMonitor _pressure;
    private readonly DeviceLedger _ledger = new();
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly Lock _lock = new();
    private readonly Lock _persistLock = new();
    private readonly Dictionary<string, WorkloadSupervisor> _supervisors = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _kick = new(0, 1);
    private readonly string _instanceId = ClusterSigner.NewId();
    private readonly DateTimeOffset _startedAt;
    private readonly ClusterFileStore<PersistedState> _store;
    private readonly Dictionary<string, string> _environment = new(StringComparer.Ordinal);
    private NodeDescriptor _descriptor;
    private DateTimeOffset? _lastSync;
    private DateTimeOffset _lastContact;
    private string? _problem;
    private string _linkState = "starting";
    private TimeSpan _exclusiveTtl = TimeSpan.FromSeconds(30);
    private volatile bool _yielded;

    internal NodeAgent(
        ClusterOptions options,
        IControllerLink link,
        IEnumerable<INodeFactsSource> facts,
        IEnumerable<IWorkloadDriver> drivers,
        IEnumerable<IOsExecutionAdapter> adapters,
        EndpointAdvertiser advertiser,
        InteractiveGate gate,
        NodePressureMonitor pressure,
        TimeProvider time,
        ILogger<NodeAgent> log)
    {
        _options = options;
        _link = link;
        _facts = [.. facts];
        _drivers = drivers.ToDictionary(d => d.Kind, StringComparer.Ordinal);
        _os = OsExecution.Select(adapters);
        _advertiser = advertiser;
        _gate = gate;
        _pressure = pressure;
        _time = time;
        _log = log;
        _startedAt = time.GetUtcNow();
        _lastContact = _startedAt;
        _descriptor = new NodeDescriptor { NodeId = options.ResolvedNodeId };
        _store = new ClusterFileStore<PersistedState>(
            options.StateDirectory.Length > 0 ? Path.Combine(options.StateDirectory, "workloads.json") : null);
        if (options.StateDirectory.Length > 0)
        {
            PrivateFiles.EnsureDirectory(options.StateDirectory);
            LocalTokenFile = Path.Combine(options.StateDirectory, "local.token");
            LocalToken = PrivateFiles.ReadOrCreateToken(LocalTokenFile);
            _environment["BURBUJA_NODE_TOKEN_FILE"] = LocalTokenFile;
        }
        else
        {
            LocalToken = ClusterSigner.NewSecret();
        }

        _environment["BURBUJA_NODE_URL"] = $"{options.Scheme}://127.0.0.1:{options.Port}";
        gate.Changed += OnGateChanged;
    }

    /// <summary>Bearer token for this node's loopback API, readable only by the node's own account.</summary>
    public string LocalToken { get; }
    public string? LocalTokenFile { get; }
    public InteractiveGate Gate => _gate;
    public NodeDescriptor Descriptor => _descriptor;

    public NodeAgentStatus Status()
    {
        lock (_lock)
        {
            return new NodeAgentStatus(
                _descriptor.NodeId, _link.Route, _linkState, _lastSync, _problem, _gate.Busy, _gate.Holders,
                [.. _supervisors.Values.Select(s => s.Report()).OrderBy(r => r.Name, StringComparer.Ordinal)]);
        }
    }

    private sealed class PersistedWorkload
    {
        public WorkloadSpec Spec { get; set; } = new() { Name = "" };
        public long Generation { get; set; }
        public string Handle { get; set; } = "";
    }

    /// <summary>What one node process leaves for the next: the work it was running, and when it last heard from the controller.</summary>
    private sealed class PersistedState
    {
        public string InstanceId { get; set; } = "";
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset LastContact { get; set; }
        public double ExclusiveTtlSeconds { get; set; }
        public List<PersistedWorkload> Workloads { get; set; } = [];
    }

    // ─── description ─────────────────────────────────────────────────────────────────────────────

    internal async Task<NodeDescriptor> DescribeAsync(CancellationToken ct)
    {
        var description = new NodeDescription();
        foreach (var source in _facts)
        {
            try { await source.DescribeAsync(description, ct); }
            catch (Exception e) when (e is not OperationCanceledException) { _log.LogWarning("Facts source {Source} failed: {Error}", source.Id, e.GetType().Name); }
        }

        var agent = _options.Agent;
        var kinds = _drivers.Keys.Where(k => agent.Kinds is null || agent.Kinds.Contains(k, StringComparer.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        foreach (var kind in kinds) _drivers[kind].Describe(description);
        foreach (var (label, value) in agent.Labels) description.Labels[label] = value;
        foreach (var capability in agent.Capabilities) description.Capabilities.Add(capability);
        foreach (var aspect in _os.Aspects) description.Capabilities.Add(OsAspects.Capability(aspect));

        // Lending is the operator's decision on top of what was detected: a device not on the list exists and
        // is not offered, and a measured resource is capped, never raised.
        var devices = description.Devices
            .Select(d => agent.LendDevices is null || agent.LendDevices.Contains(d.Id, StringComparer.Ordinal) ? d : d with { Lendable = false })
            .ToArray();
        var gpus = devices.Count(d => d.Kind == ClusterConstants.Resources.Gpu && d.Lendable);
        if (devices.Any(d => d.Kind == ClusterConstants.Resources.Gpu)) description.Capacity[ClusterConstants.Resources.Gpu] = gpus;
        foreach (var (resource, amount) in agent.Lend)
            description.Capacity[resource] = description.Capacity.TryGetValue(resource, out var measured) ? Math.Min(measured, amount) : amount;

        _ledger.SetDevices(devices);
        return new NodeDescriptor
        {
            NodeId = _options.ResolvedNodeId,
            Name = _options.NodeName is { Length: > 0 } name ? name : _options.ResolvedNodeId,
            Platform = description.Platform,
            Labels = description.Labels,
            Capabilities = [.. description.Capabilities.Order(StringComparer.Ordinal)],
            Capacity = description.Capacity,
            Devices = devices,
            Kinds = kinds,
            Endpoints = await _advertiser.AdvertiseAsync(ct),
        };
    }

    private async Task<NodeObservation?> ObserveAsync(CancellationToken ct)
    {
        var reading = new NodeReading();
        foreach (var source in _facts)
        {
            try { await source.ObserveAsync(reading, ct); }
            catch (Exception e) when (e is not OperationCanceledException) { _log.LogWarning("Facts source {Source} failed: {Error}", source.Id, e.GetType().Name); }
        }

        var observation = new NodeObservation { At = _time.GetUtcNow(), Available = reading.Available, Pressure = reading.Pressure, Reasons = reading.Reasons };
        _pressure.Sample(observation);
        return observation;
    }

    // ─── the loop ────────────────────────────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The fence runs on its own clock. A sync attempt can take many seconds to fail, and a key must be
        // given up on time whatever the network is doing.
        ITimer? fence = null;
        var standDown = false;
        try
        {
            _descriptor = await DescribeAsync(stoppingToken);
            Adopt();
            fence = _time.CreateTimer(_ => CheckFence(), null, FenceCheckEvery, FenceCheckEvery);
            var describedAt = _time.GetUtcNow();
            var interval = TimeSpan.FromSeconds(_options.Controller.SyncIntervalSeconds);
            var failures = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                if (_yielded)
                {
                    standDown = true;
                    Note("superseded", "a newer process on this machine took over this node");
                    AbandonAll("handed to the process that replaced this one");
                    return;
                }

                var delay = interval;
                var ok = false;
                try
                {
                    if (_time.GetUtcNow() - describedAt > TimeSpan.FromSeconds(60))
                    {
                        _descriptor = await DescribeAsync(stoppingToken);
                        describedAt = _time.GetUtcNow();
                    }

                    var result = await _link.SyncAsync(new NodeSyncRequest
                    {
                        InstanceId = _instanceId,
                        Descriptor = _descriptor,
                        Observation = await ObserveAsync(stoppingToken),
                        Workloads = Status().Workloads,
                    }, stoppingToken);

                    switch (result.Status)
                    {
                        case LinkStatus.Ok:
                            ok = true;
                            failures = 0;
                            interval = TimeSpan.FromMilliseconds(Math.Max(50, result.Response!.SyncIntervalMs));
                            delay = interval;
                            lock (_lock)
                            {
                                _lastContact = _time.GetUtcNow();
                                _lastSync = _lastContact;
                                _exclusiveTtl = TimeSpan.FromMilliseconds(Math.Max(100, result.Response.ExclusiveTtlMs));
                                _problem = null;
                                _linkState = "connected";
                            }

                            _pressure.Advise(result.Response.PressuredNodes);
                            Reconcile(result.Response.Assignments);
                            break;

                        case LinkStatus.Superseded:
                            // Another process is this node now. If it is on this machine it has adopted the
                            // very same processes, so stopping them here would stop its work: let go instead.
                            // If the identity moved to another machine, what runs here is nobody's and stops.
                            standDown = true;
                            Note("superseded", "a newer process serves this node id");
                            if (SuccessorOnThisMachine()) AbandonAll("handed to the process that replaced this one");
                            else await StopAsync(_ => true, "another machine now serves this node id");
                            return;

                        case LinkStatus.Removed:
                            standDown = true;
                            Note("removed", "this node was removed from the cluster");
                            await StopAsync(_ => true, "the node was removed from the cluster");
                            return;

                        default:
                            failures++;
                            Note(result.Status == LinkStatus.Unreachable ? "disconnected" : "blocked", result.Detail);

                            // Transient failures back off with jitter; a refusal that will not change by itself is
                            // retried slowly, so a fixed configuration is picked up without hammering the controller.
                            var ceiling = result.Status == LinkStatus.Unreachable
                                ? Math.Min(60_000, interval.TotalMilliseconds * Math.Pow(2, Math.Min(failures, 6)))
                                : Math.Max(30_000, interval.TotalMilliseconds);
                            delay = TimeSpan.FromMilliseconds(ceiling / 2 + Random.Shared.NextDouble() * ceiling / 2);
                            break;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    // One bad pass must not end the loop: the loop is what reports, reconciles and renews.
                    failures++;
                    _log.LogError(e, "A cluster sync pass failed");
                    Note("faulted", "an internal error interrupted a sync (" + e.GetType().Name + ")");
                }

                Persist();
                var kicked = _kick.WaitAsync(stoppingToken);
                try { await Task.WhenAny(Task.Delay(delay, _time, stoppingToken), kicked); }
                catch (OperationCanceledException) { break; }
                if (ok && kicked.IsCompletedSuccessfully)
                {
                    // A change is reported promptly, but batched: several workloads changing together are one sync.
                    try { await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(50, interval.TotalMilliseconds)), _time, stoppingToken); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            fence?.Dispose();
            if (!standDown)
            {
                // Stopping the node always stops work that holds an exclusive key: once this process is
                // gone nothing here could give the key up. Other work stays unless the operator said otherwise.
                var all = _options.Agent.StopWorkloadsOnShutdown;
                await StopAsync(s => all || s.Spec.ExclusiveKey is { Length: > 0 }, "the node is shutting down",
                    TimeSpan.FromSeconds(_options.Agent.ShutdownGraceSeconds));
            }

            Persist();
        }
    }

    private void Note(string state, string? problem)
    {
        lock (_lock)
        {
            if (_problem != problem && problem is not null) _log.LogWarning("Cluster link {State}: {Problem}", state, problem);
            _linkState = state;
            _problem = problem;
        }
    }

    // ─── making the node match the desired state ─────────────────────────────────────────────────

    private SupervisorContext Context() => new(
        _descriptor.NodeId, _options.Agent, _os.Aspects, () => _descriptor.Devices, _ledger, _gate, _environment, _time, _log, Changed);

    /// <summary>A workload changed state: record it at once (a crash must not forget a started process) and report it soon.</summary>
    private void Changed()
    {
        Persist();
        try { _kick.Release(); }
        catch (SemaphoreFullException) { }
    }

    private void Reconcile(IReadOnlyList<WorkloadAssignment> assignments)
    {
        // Stop requests are issued after the lock is released: cancelling can run a supervisor's next step on
        // this thread, and that step records state under another lock.
        var stops = new List<(WorkloadSupervisor Supervisor, string Reason)>();
        lock (_lock)
        {
            var wanted = assignments.ToDictionary(a => a.Spec.Name, StringComparer.Ordinal);
            foreach (var (name, supervisor) in _supervisors.ToArray())
            {
                if (wanted.TryGetValue(name, out var assignment) && assignment.Generation == supervisor.Generation)
                {
                    // Work that gave up a key, or that was not there to adopt, starts again now that the
                    // controller has confirmed this node should still be running it.
                    if (supervisor.Finished && supervisor.Restartable) _supervisors.Remove(name);
                    continue;
                }

                // Absent or superseded: stop it, and keep it listed until it has finished so that it is still
                // reported and its replacement cannot start alongside it.
                if (supervisor.Finished) _supervisors.Remove(name);
                else stops.Add((supervisor, assignment is null ? "no longer assigned to this node" : "replaced by a newer generation"));
            }

            foreach (var assignment in assignments)
            {
                if (_supervisors.ContainsKey(assignment.Spec.Name)) continue;
                _supervisors[assignment.Spec.Name] = new WorkloadSupervisor(assignment, _drivers.GetValueOrDefault(assignment.Spec.Kind), Context());
            }
        }

        foreach (var (supervisor, reason) in stops) supervisor.RequestStop(reason);
    }

    /// <summary>
    /// Without contact, an exclusive key cannot be renewed. Its holder is stopped one time-to-live after the
    /// last successful sync; the controller waits longer than that, plus the work's own stop grace, before it
    /// lets the key go anywhere else.
    /// </summary>
    private void CheckFence()
    {
        try
        {
            WorkloadSupervisor[] holders;
            lock (_lock)
            {
                if (_time.GetUtcNow() - _lastContact <= _exclusiveTtl) return;
                holders = [.. _supervisors.Values.Where(s => s.Spec.ExclusiveKey is { Length: > 0 } && !s.Finished)];
            }

            foreach (var supervisor in holders)
                supervisor.RequestStop("lost contact with the controller; its exclusive key was given up", restartable: true);
        }
        catch (Exception e)
        {
            _log.LogError("The exclusive-key fence could not run: {Error}", e.GetType().Name);
        }
    }

    private void OnGateChanged(bool busy)
    {
        WorkloadSupervisor[] supervisors;
        lock (_lock) supervisors = [.. _supervisors.Values];
        foreach (var supervisor in supervisors) _ = supervisor.ApplyGateAsync();
    }

    private async Task StopAsync(Func<WorkloadSupervisor, bool> which, string reason, TimeSpan? graceLimit = null)
    {
        WorkloadSupervisor[] supervisors;
        lock (_lock) supervisors = [.. _supervisors.Values.Where(which)];
        foreach (var supervisor in supervisors) supervisor.RequestStop(reason, graceLimit: graceLimit);
        await Task.WhenAll(supervisors.Select(s => s.Completion));
    }

    private void AbandonAll(string reason)
    {
        WorkloadSupervisor[] supervisors;
        lock (_lock) supervisors = [.. _supervisors.Values];
        foreach (var supervisor in supervisors) supervisor.RequestStop(reason, abandon: true);
    }

    // ─── surviving a restart of the node process ─────────────────────────────────────────────────

    private PersistedState? Recorded()
    {
        try { return _store.Load(); }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>A newer process of this node, on this machine, has taken over the state file.</summary>
    private bool SuccessorOnThisMachine() =>
        _yielded || (Recorded() is { } recorded && recorded.InstanceId != _instanceId && recorded.StartedAt > _startedAt);

    private void Persist()
    {
        lock (_persistLock)
        {
            // Two processes of one node on one machine share this file. The newer one owns it; the older one
            // must not overwrite it, and learns here that it has been replaced.
            if (_yielded) return;
            if (Recorded() is { } recorded && recorded.InstanceId != _instanceId && recorded.StartedAt > _startedAt)
            {
                _yielded = true;
                try { _kick.Release(); }
                catch (SemaphoreFullException) { }
                return;
            }

            PersistedState state;
            lock (_lock)
            {
                state = new PersistedState
                {
                    InstanceId = _instanceId,
                    StartedAt = _startedAt,
                    LastContact = _lastContact,
                    ExclusiveTtlSeconds = _exclusiveTtl.TotalSeconds,
                    Workloads = [.. _supervisors.Values.Where(s => s.Handle is not null && !s.Finished)
                        .Select(s => new PersistedWorkload { Spec = s.Spec, Generation = s.Generation, Handle = s.Handle! })],
                };
            }

            try { _store.Save(state); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log.LogWarning("Could not record running workloads: {Error}", e.GetType().Name); }
        }
    }

    /// <summary>
    /// Take back the work a previous process of this node left running. Each one is given a supervisor under
    /// its recorded generation that adopts the work or does nothing; it never starts a new copy. The first
    /// sync then confirms it or, if the controller has moved on, stops it like any other superseded work.
    ///
    /// <para>The previous process's last contact is carried over, so exclusive work that outlived a crashed
    /// process is fenced against the moment the cluster last confirmed it, not against this process's start.</para>
    /// </summary>
    private void Adopt()
    {
        if (Recorded() is not { } recorded) return;
        lock (_lock)
        {
            if (recorded.LastContact != default) _lastContact = recorded.LastContact;
            if (recorded.ExclusiveTtlSeconds > 0) _exclusiveTtl = TimeSpan.FromSeconds(recorded.ExclusiveTtlSeconds);
            foreach (var workload in recorded.Workloads)
            {
                _supervisors[workload.Spec.Name] = new WorkloadSupervisor(
                    new WorkloadAssignment(workload.Spec, workload.Generation), _drivers.GetValueOrDefault(workload.Spec.Kind), Context(), workload.Handle);
            }
        }

        // Take ownership of the state file straight away, so an older process still alive on this machine
        // sees it has been replaced and lets go.
        Persist();
    }
}

/// <summary>Asks every network what addresses it gives this process. A network that fails contributes nothing.</summary>
internal sealed class EndpointAdvertiser(ClusterOptions options, IEnumerable<INetworkProvider> providers, ILogger<EndpointAdvertiser> log)
{
    public async ValueTask<IReadOnlyList<AdvertisedEndpoint>> AdvertiseAsync(CancellationToken ct)
    {
        var listener = new ListenerBinding(options.Scheme, options.Port);
        var endpoints = new List<AdvertisedEndpoint>();
        foreach (var provider in providers)
        {
            try { endpoints.AddRange(await provider.AdvertiseAsync(listener, ct)); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("Network {Network} could not advertise: {Error}", provider.Id, e.GetType().Name); }
        }

        return [.. endpoints.DistinctBy(e => e.Url).OrderBy(e => e.Reachability).ThenBy(e => e.Url.ToString(), StringComparer.Ordinal)];
    }
}
