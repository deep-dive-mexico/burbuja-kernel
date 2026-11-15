using Burbuja.Cluster.Abstractions;
using Burbuja.Cluster.Security;
using Microsoft.Extensions.Logging;

namespace Burbuja.Cluster.Controller;

internal enum SyncResult
{
    Ok,
    UnknownNode,

    /// <summary>A newer process holds this node id. The caller must stop its work and stand down.</summary>
    Superseded,
    ProtocolMismatch,
    Invalid,

    /// <summary>The node was removed from the cluster by an operator. It must stop the cluster's work.</summary>
    Removed,

    /// <summary>The request names another controller process (one from before a restart). The caller should resend.</summary>
    WrongController,

    /// <summary>Not newer than a request already applied. Ignored.</summary>
    Stale,
}

internal sealed record SyncOutcome(SyncResult Result, NodeSyncResponse? Response = null);

/// <summary>
/// The cluster's single source of desired state: which nodes are members, which workloads exist and where
/// each one runs.
///
/// <para><b>Level-triggered.</b> Every decision is recomputed from the whole state under one lock, and a node
/// is told the complete set it should run rather than a sequence of commands. A lost message therefore costs
/// one sync interval, not a divergence.</para>
///
/// <para><b>Nothing moves unless it must.</b> A placed workload stays where it is. It is placed again only when
/// its node is drained or lost and its own policy allows a move; there is no rebalancing and no preemption.</para>
///
/// <para><b>One controller.</b> There is no consensus between controllers; a second one is a second cluster.
/// Nodes keep running what they were last told while the controller is away, except work holding an exclusive
/// key, which stops itself (see <see cref="ReleaseRecord"/>).</para>
/// </summary>
internal sealed class ClusterController : IClusterControlPlane
{
    private readonly Lock _gate = new();
    private readonly ClusterOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly Scheduler _scheduler;
    private readonly Dictionary<string, IWorkloadDriver> _drivers;
    private readonly ClusterFileStore<ControllerState> _store;
    private readonly ControllerState _state;
    private readonly string _instanceId = ClusterSigner.NewId();
    private readonly DateTimeOffset _startedAt;
    private IReadOnlyList<AdvertisedEndpoint> _endpoints = [];

    public ClusterController(
        ClusterOptions options,
        TimeProvider time,
        IEnumerable<IWorkloadDriver> drivers,
        IEnumerable<IPlacementFilter> filters,
        IEnumerable<IPlacementScorer> scorers,
        ILogger<ClusterController> log)
    {
        _options = options;
        _time = time;
        _log = log;
        _scheduler = new Scheduler(filters, scorers);
        _drivers = drivers.ToDictionary(d => d.Kind, StringComparer.Ordinal);
        _store = new ClusterFileStore<ControllerState>(
            options.StateDirectory.Length > 0 ? Path.Combine(options.StateDirectory, "controller.json") : null);
        if (options.StateDirectory.Length > 0) PrivateFiles.EnsureDirectory(options.StateDirectory);
        _state = _store.Load() ?? new ControllerState { ClusterId = ClusterSigner.NewId(12) };
        _startedAt = time.GetUtcNow();
        foreach (var node in _state.Nodes.Values) node.LastSeen = _startedAt;
        foreach (var workload in _state.Workloads.Values)
        {
            workload.Since = _startedAt;
            workload.Phase = workload.NodeId is null ? WorkloadPhase.Pending : WorkloadPhase.Unknown;
        }

        _store.Save(_state);
    }

    public ClusterIdentity Identity => new(_state.ClusterId, _instanceId, ClusterConstants.ProtocolVersion);

    public void SetEndpoints(IReadOnlyList<AdvertisedEndpoint> endpoints)
    {
        lock (_gate) _endpoints = endpoints;
    }

    // ─── membership ──────────────────────────────────────────────────────────────────────────────

    public JoinTokenGrant CreateJoinToken()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var expired in _state.Tokens.Values.Where(t => t.ExpiresAt <= now).Select(t => t.TokenId).ToArray())
                _state.Tokens.Remove(expired);
            var (token, id, key) = ClusterSigner.NewJoinToken();
            var expires = now.AddMinutes(_options.Controller.JoinTokenMinutes);
            _state.Tokens[id] = new JoinTokenRecord { TokenId = id, JoinKey = Convert.ToBase64String(key), ExpiresAt = expires };
            _store.Save(_state);
            return new JoinTokenGrant(token, expires);
        }
    }

    internal byte[]? JoinKeyFor(string tokenId)
    {
        lock (_gate)
        {
            return _state.Tokens.TryGetValue(tokenId, out var token) && token.ExpiresAt > _time.GetUtcNow()
                ? Convert.FromBase64String(token.JoinKey)
                : null;
        }
    }

    /// <summary>
    /// Admit a node whose join request was already verified against the token's key. The token is consumed: a
    /// second node needs a second token.
    /// </summary>
    internal JoinResponse? CompleteJoin(JoinRequest request)
    {
        lock (_gate)
        {
            if (request.Protocol != ClusterConstants.ProtocolVersion || !ClusterNames.IsValid(request.Descriptor.NodeId)) return null;
            if (!_state.Tokens.Remove(request.TokenId, out var token) || token.ExpiresAt <= _time.GetUtcNow()) return null;
            var nodeId = request.Descriptor.NodeId;
            var serverNonce = ClusterSigner.NewSecret(16);
            var key = ClusterSigner.NodeKey(Convert.FromBase64String(token.JoinKey), _state.ClusterId, nodeId, request.ClientNonce, serverNonce);
            var node = Upsert(nodeId, request.Descriptor);
            _state.Removed.Remove(nodeId);

            // A node that joins again (a lost credential, a rebuilt machine) replaces the old key. Whatever
            // process still holds the old one can no longer sign, so it cannot keep reporting as this node.
            node.Key = Convert.ToBase64String(key);
            _store.Save(_state);
            _log.LogInformation("Node {NodeId} joined cluster {ClusterId}", nodeId, _state.ClusterId);
            return new JoinResponse(_state.ClusterId, nodeId, serverNonce);
        }
    }

    /// <summary>The controller's own in-process node. It needs no key because it never crosses a network.</summary>
    internal void RegisterLocalNode(NodeDescriptor descriptor)
    {
        lock (_gate)
        {
            Upsert(descriptor.NodeId, descriptor);
            _store.Save(_state);
        }
    }

    private NodeRecord Upsert(string nodeId, NodeDescriptor descriptor)
    {
        if (!_state.Nodes.TryGetValue(nodeId, out var node))
            _state.Nodes[nodeId] = node = new NodeRecord { NodeId = nodeId, LastSeen = _time.GetUtcNow() };
        node.Descriptor = descriptor;
        return node;
    }

    internal byte[]? NodeKeyFor(string nodeId)
    {
        lock (_gate)
        {
            if (_state.Nodes.TryGetValue(nodeId, out var node)) return node.Key.Length > 0 ? Convert.FromBase64String(node.Key) : null;
            return _state.Removed.TryGetValue(nodeId, out var key) ? Convert.FromBase64String(key) : null;
        }
    }

    internal bool AcceptNonce(string nodeId, string nonce)
    {
        lock (_gate)
        {
            // A removed node has no window; its only answer is "removed", which is safe to repeat.
            return !_state.Nodes.TryGetValue(nodeId, out var node) || node.Nonces.TryAccept(nonce, _time.GetUtcNow());
        }
    }

    public bool SetAvailability(string nodeId, NodeAvailability availability)
    {
        lock (_gate)
        {
            if (!_state.Nodes.TryGetValue(nodeId, out var node)) return false;
            node.Availability = availability;
            Reconcile();
            _store.Save(_state);
            return true;
        }
    }

    /// <summary>
    /// Remove a node from the cluster. Its credential stops working at once. Workloads bound to it become
    /// pending; they are placed again only after the node has been silent long enough to have stopped them.
    /// </summary>
    public bool Forget(string nodeId)
    {
        lock (_gate)
        {
            if (!_state.Nodes.Remove(nodeId, out var removed)) return false;

            // The key is kept so the controller can tell the node, with proof, that it was removed. Without
            // it a removed node could not distinguish "you are out" from an address that merely answers 404.
            if (removed.Key.Length > 0) _state.Removed[nodeId] = removed.Key;
            Reconcile();
            _store.Save(_state);
            return true;
        }
    }

    // ─── node sync ───────────────────────────────────────────────────────────────────────────────

    public SyncOutcome Sync(string nodeId, NodeSyncRequest request)
    {
        lock (_gate)
        {
            if (!_state.Nodes.TryGetValue(nodeId, out var node))
                return new(_state.Removed.ContainsKey(nodeId) ? SyncResult.Removed : SyncResult.UnknownNode);
            if (request.Protocol != ClusterConstants.ProtocolVersion) return new(SyncResult.ProtocolMismatch);
            if (!string.Equals(request.Descriptor.NodeId, nodeId, StringComparison.Ordinal) || request.InstanceId.Length is 0 or > 64)
                return new(SyncResult.Invalid);

            // A request is meant for one controller process. Everything a controller remembers about what it
            // has already seen (nonces, sequences, replaced processes) is lost when it restarts, so a request
            // from before the restart is refused outright instead of being judged without that memory.
            if (!string.Equals(request.ControllerInstanceId, _instanceId, StringComparison.Ordinal)) return new(SyncResult.WrongController);

            // The newest process wins the node id. An older one that is still alive learns it was replaced
            // and stands down, so two processes with one identity cannot both keep running the node's work.
            if (!string.Equals(node.InstanceId, request.InstanceId, StringComparison.Ordinal))
            {
                if (node.RetiredInstances.Contains(request.InstanceId)) return new(SyncResult.Superseded);
                if (node.InstanceId is not null)
                {
                    node.RetiredInstances.Add(node.InstanceId);
                    if (node.RetiredInstances.Count > 16) node.RetiredInstances.RemoveAt(0);
                    _log.LogWarning("Node {NodeId} is now served by a new process", nodeId);
                }

                node.InstanceId = request.InstanceId;
                node.LastSequence = 0;
            }

            // What a node reports can end a release, so reports are applied in the order the node made them.
            if (request.Sequence <= node.LastSequence) return new(SyncResult.Stale);
            node.LastSequence = request.Sequence;

            var now = _time.GetUtcNow();
            var dirty = ClusterJson.Write(node.Descriptor) != ClusterJson.Write(request.Descriptor);
            node.Descriptor = request.Descriptor;
            node.LastSeen = now;
            node.Synced = true;
            node.Pressure ??= new PressureHysteresis(_options.Controller.PressureBadSamples, _options.Controller.PressureGoodSamples);
            if (request.Observation is { } observation)
            {
                node.Observation = observation;
                node.ObservedAt = now;
                node.Pressure.Sample(observation.Pressure is PressureLevel.Unknown or >= PressureLevel.Elevated);
            }
            else
            {
                node.Pressure.Sample(pressed: true);
            }

            dirty |= ApplyReports(nodeId, request.Workloads, now);

            // A release ends when the node no longer reports that generation as alive.
            dirty |= _state.Releases.RemoveAll(r => r.NodeId == nodeId && !request.Workloads.Any(w =>
                w.Name == r.Name && w.Generation == r.Generation && !IsSettled(w.Phase))) > 0;

            dirty |= Reconcile();
            if (dirty) _store.Save(_state);

            return new(SyncResult.Ok, new NodeSyncResponse
            {
                ControllerInstanceId = _instanceId,
                Availability = node.Availability,
                Assignments = [.. _state.Workloads.Values.Where(w => w.NodeId == nodeId)
                    .OrderBy(w => w.Spec.Name, StringComparer.Ordinal)
                    .Select(w => new WorkloadAssignment(w.Spec, w.Generation))],
                SyncIntervalMs = (int)(_options.Controller.SyncIntervalSeconds * 1000),
                ExclusiveTtlMs = (int)(_options.Controller.ExclusiveTtlSeconds * 1000),
                ControllerEndpoints = _endpoints,
            });
        }
    }

    private bool ApplyReports(string nodeId, IReadOnlyList<WorkloadReport> reports, DateTimeOffset now)
    {
        var dirty = false;
        foreach (var report in reports)
        {
            // A report under another generation describes an earlier placement and says nothing about this one.
            if (!_state.Workloads.TryGetValue(report.Name, out var workload) || workload.NodeId != nodeId
                || workload.Generation != report.Generation)
                continue;

            if (report.Phase == WorkloadPhase.Refused)
            {
                if (!workload.RefusedBy.Contains(nodeId)) workload.RefusedBy.Add(nodeId);
                workload.NodeId = null;
                SetPhase(workload, WorkloadPhase.Pending, $"refused by {nodeId}: {report.Reason}", now);
                dirty = true;
                continue;
            }

            SetPhase(workload, report.Phase, report.Reason, now);
            workload.Restarts = report.Restarts;
        }

        return dirty;
    }

    private static bool IsSettled(WorkloadPhase phase) =>
        phase is WorkloadPhase.Stopped or WorkloadPhase.Completed or WorkloadPhase.Failed or WorkloadPhase.Refused;

    private static void SetPhase(WorkloadRecord workload, WorkloadPhase phase, string? reason, DateTimeOffset now)
    {
        if (workload.Phase != phase) workload.Since = now;
        workload.Phase = phase;
        workload.Reason = reason;
    }

    // ─── workloads ───────────────────────────────────────────────────────────────────────────────

    public ApplyOutcome Apply(WorkloadSpec spec)
    {
        var errors = Validate(spec);
        if (errors.Count > 0) return new(false, errors);
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (!_state.Workloads.TryGetValue(spec.Name, out var workload))
            {
                _state.Workloads[spec.Name] = workload = new WorkloadRecord { Spec = spec, Since = now };
            }
            else if (ClusterJson.Write(workload.Spec) != ClusterJson.Write(spec))
            {
                var previous = workload.Spec;
                workload.Spec = spec;
                workload.RefusedBy.Clear();
                if (workload.NodeId is { } nodeId)
                {
                    // A changed spec stays on its node when the node can still take it: the node stops the
                    // old generation before it starts the new one, so the change never runs twice. Only what
                    // the spec itself asks is checked; the node being cordoned, quiet or pressed right now
                    // is no reason to uproot work that is already there.
                    var view = Views(excluding: workload).FirstOrDefault(v => v.Descriptor.NodeId == nodeId);
                    if (!string.Equals(previous.ExclusiveKey, spec.ExclusiveKey, StringComparison.Ordinal))
                    {
                        // A different key is a different claim. The old one stays held until the node confirms
                        // the old generation stopped, and the new one is asked for like any other placement.
                        Release(workload, previous, "its exclusive key changed", now);
                    }
                    else if (view is not null && _scheduler.RejectionForUpdate(Request(workload), view) is null)
                    {
                        workload.Generation = ++_state.LastGeneration;
                        SetPhase(workload, WorkloadPhase.Assigned, "specification changed", now);
                    }
                    else
                    {
                        Release(workload, previous, "specification no longer fits its node", now);
                    }
                }
            }

            Reconcile();
            _store.Save(_state);
            return new(true, [], View(workload));
        }
    }

    public bool Delete(string name)
    {
        lock (_gate)
        {
            if (!_state.Workloads.Remove(name, out var workload)) return false;
            if (workload.NodeId is not null) Release(workload, workload.Spec, "deleted", _time.GetUtcNow());
            Reconcile();
            _store.Save(_state);
            return true;
        }
    }

    private List<string> Validate(WorkloadSpec spec)
    {
        var errors = new List<string>();
        if (!ClusterNames.IsValid(spec.Name)) errors.Add("name must be 1-63 lower-case letters, digits, '-', '.' or '_', starting with a letter or digit");
        if (spec.Kind.Length == 0) errors.Add("kind is required");
        if (spec.Requests.Any(r => r.Value < 0 || r.Key.Length == 0)) errors.Add("requests must be named and not negative");
        if (spec.Lifecycle.StopGrace < TimeSpan.Zero || spec.Lifecycle.StopGrace > TimeSpan.FromHours(6)) errors.Add("lifecycle.stopGrace must be between 0 and 6 hours");
        if (spec.Lifecycle.RestartDelay < TimeSpan.Zero) errors.Add("lifecycle.restartDelay must not be negative");
        if (spec.ExclusiveKey is { Length: > 128 }) errors.Add("exclusiveKey is too long");
        if (_drivers.TryGetValue(spec.Kind, out var driver)) errors.AddRange(driver.Validate(spec).Errors);
        return errors;
    }

    private WorkloadPlacementRequest Request(WorkloadRecord workload)
    {
        var spec = workload.Spec;
        var required = new List<string>(spec.Placement.RequiredCapabilities);
        if (_drivers.TryGetValue(spec.Kind, out var driver)) required.AddRange(driver.Validate(spec).RequiredCapabilities);
        if (spec.Execution.Strict) required.AddRange(OsAspects.RequiredBy(spec.Execution).Select(OsAspects.Capability));
        return new WorkloadPlacementRequest(spec, [.. required.Distinct(StringComparer.Ordinal)], workload.RefusedBy.ToHashSet(StringComparer.Ordinal));
    }

    private void Release(WorkloadRecord workload, WorkloadSpec ranAs, string reason, DateTimeOffset now)
    {
        _state.Releases.Add(new ReleaseRecord
        {
            NodeId = workload.NodeId!,
            Name = ranAs.Name,
            Generation = workload.Generation,
            ExclusiveKey = ranAs.ExclusiveKey,
            Requests = new(ranAs.Requests),
            StopGraceSeconds = ranAs.Lifecycle.StopGrace.TotalSeconds,
        });
        workload.NodeId = null;
        SetPhase(workload, WorkloadPhase.Pending, reason, now);
    }

    // ─── reconciliation ──────────────────────────────────────────────────────────────────────────

    /// <summary>Called on a timer so that a node going silent is noticed even when nothing else happens.</summary>
    public void Tick()
    {
        lock (_gate)
        {
            if (Reconcile()) _store.Save(_state);
        }
    }

    private bool Reachable(NodeRecord node, DateTimeOffset now) =>
        node.Synced && now - node.LastSeen <= TimeSpan.FromSeconds(_options.Controller.NodeTtlSeconds);

    /// <summary>
    /// How long a node must be silent before the controller may assume work it held has stopped.
    ///
    /// <para>A node that loses contact starts stopping exclusive work one time-to-live after its last
    /// successful sync, and stopping takes up to the work's own grace plus the time to end what ignored the
    /// request. The controller counts from the last sync it <em>received</em>, which is never later than the
    /// node's own mark, and waits a second time-to-live on top to cover the node's check interval and clocks
    /// that run at slightly different rates. A long grace therefore holds a key for that long: that is the
    /// price of letting work finish properly.</para>
    /// </summary>
    private TimeSpan FenceDelay(double stopGraceSeconds) =>
        TimeSpan.FromSeconds(_options.Controller.ExclusiveTtlSeconds * 2 + stopGraceSeconds) + KillMargin;

    /// <summary>How long a node allows for work that ignored a stop request to actually end.</summary>
    private static readonly TimeSpan KillMargin = TimeSpan.FromSeconds(5);

    private bool Reconcile()
    {
        var now = _time.GetUtcNow();
        var dirty = false;
        var lossGrace = TimeSpan.FromSeconds(_options.Controller.NodeLossGraceSeconds);

        dirty |= _state.Releases.RemoveAll(r => _state.Nodes.TryGetValue(r.NodeId, out var node)
            ? !Reachable(node, now) && now - node.LastSeen > FenceDelay(r.StopGraceSeconds)
            : ForgottenLongEnough(r, now)) > 0;

        foreach (var workload in _state.Workloads.Values.Where(w => w.NodeId is not null).ToArray())
        {
            if (!_state.Nodes.TryGetValue(workload.NodeId!, out var node))
            {
                Release(workload, workload.Spec, "its node was removed from the cluster", now);
                dirty = true;
                continue;
            }

            var movable = workload.Spec.Lifecycle.OnNodeLoss == NodeLossPolicy.Reschedule;
            if (!Reachable(node, now))
            {
                // Work holding a key leaves a silent node only once the node must have stopped it.
                var fence = workload.Spec.ExclusiveKey is { Length: > 0 } ? FenceDelay(workload.Spec.Lifecycle.StopGrace.TotalSeconds) : TimeSpan.Zero;
                if (movable && now - node.LastSeen > (fence > lossGrace ? fence : lossGrace))
                {
                    Release(workload, workload.Spec, $"node {node.NodeId} was lost", now);
                    dirty = true;
                }
                else
                {
                    SetPhase(workload, WorkloadPhase.Unknown, $"node {node.NodeId} is unreachable", now);
                }
            }
            else if (node.Availability == NodeAvailability.Draining && movable)
            {
                // Leave a draining node only for somewhere to go; stopping work with no destination would
                // turn a drain into an outage.
                var elsewhere = _scheduler.Place(Request(workload), Views(excluding: workload));
                if (elsewhere.NodeId is not null)
                {
                    Release(workload, workload.Spec, $"node {node.NodeId} is draining", now);
                    dirty = true;
                }
            }
        }

        // Work somebody is waiting for is placed first, then by name so the order never depends on a dictionary.
        foreach (var workload in _state.Workloads.Values.Where(w => w.NodeId is null)
                     .OrderBy(w => w.Spec.Class).ThenBy(w => w.Spec.Name, StringComparer.Ordinal))
        {
            if (workload.Spec.ExclusiveKey is { Length: > 0 } key && KeyHolder(key, workload) is { } holder)
            {
                SetPhase(workload, WorkloadPhase.Pending, $"exclusive key '{key}' is held by {holder}", now);
                workload.Rejections = new Dictionary<string, string>();
                continue;
            }

            var decision = _scheduler.Place(Request(workload), Views());
            workload.Rejections = decision.Rejections;
            if (decision.NodeId is null)
            {
                SetPhase(workload, WorkloadPhase.Pending, _state.Nodes.Count == 0 ? "the cluster has no nodes" : "no node can take it", now);
                continue;
            }

            workload.NodeId = decision.NodeId;
            workload.Generation = ++_state.LastGeneration;
            workload.Restarts = 0;
            SetPhase(workload, WorkloadPhase.Assigned, null, now);
            dirty = true;
            _log.LogInformation("Workload {Workload} generation {Generation} placed on {NodeId}", workload.Spec.Name, workload.Generation, decision.NodeId);
        }

        return dirty;
    }

    /// <summary>
    /// A release on a node that is no longer a member has nobody to confirm it, so it is kept for the fence
    /// delay counted from the first time the controller noticed, and then dropped.
    /// </summary>
    private bool ForgottenLongEnough(ReleaseRecord release, DateTimeOffset now)
    {
        var key = (release.NodeId, release.Name, release.Generation);
        _forgotten.TryAdd(key, now);
        if (now - _forgotten[key] <= FenceDelay(release.StopGraceSeconds)) return false;
        _forgotten.Remove(key);
        return true;
    }

    private readonly Dictionary<(string, string, long), DateTimeOffset> _forgotten = new();

    private string? KeyHolder(string key, WorkloadRecord asking)
    {
        var running = _state.Workloads.Values.FirstOrDefault(w => w != asking && w.NodeId is not null && w.Spec.ExclusiveKey == key);
        if (running is not null) return $"workload '{running.Spec.Name}'";
        var release = _state.Releases.FirstOrDefault(r => r.ExclusiveKey == key);
        return release is null ? null : $"'{release.Name}', which node {release.NodeId} has not confirmed stopped";
    }

    private List<NodeView> Views(WorkloadRecord? excluding = null)
    {
        var now = _time.GetUtcNow();
        var maxAge = TimeSpan.FromSeconds(_options.Controller.ObservationMaxAgeSeconds);
        var views = new List<NodeView>(_state.Nodes.Count);
        foreach (var node in _state.Nodes.Values)
        {
            var assigned = _state.Workloads.Values.Where(w => w.NodeId == node.NodeId && w != excluding).ToArray();
            var allocated = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var workload in assigned) allocated = ResourceMath.Add(allocated, workload.Spec.Requests);
            foreach (var release in _state.Releases.Where(r => r.NodeId == node.NodeId)) allocated = ResourceMath.Add(allocated, release.Requests);
            var reachable = Reachable(node, now);
            views.Add(new NodeView
            {
                Descriptor = node.Descriptor,
                Availability = node.Availability,
                InstanceId = node.InstanceId,
                LastSeen = node.Synced ? node.LastSeen : null,
                Reachable = reachable,
                ObservationFresh = reachable && node.ObservedAt is { } at && now - at <= maxAge,
                Pressured = node.Pressure?.Pressured ?? false,
                Observation = node.Observation,
                Allocated = allocated,
                Free = ResourceMath.Remaining(node.Descriptor.Capacity, allocated),
                Workloads = [.. assigned.Select(w => w.Spec.Name).Order(StringComparer.Ordinal)],
                SpreadGroups = [.. assigned.Select(w => w.Spec.Placement.SpreadGroup).OfType<string>().Distinct(StringComparer.Ordinal)],
            });
        }

        return views;
    }

    private static WorkloadView View(WorkloadRecord workload) => new()
    {
        Spec = workload.Spec,
        Generation = workload.Generation,
        NodeId = workload.NodeId,
        Phase = workload.Phase,
        Reason = workload.Reason,
        Since = workload.Since,
        Restarts = workload.Restarts,
        Rejections = workload.Rejections,
    };

    public ClusterStatus Status()
    {
        lock (_gate)
        {
            if (Reconcile()) _store.Save(_state);
            return new ClusterStatus(
                Identity,
                [.. Views().OrderBy(v => v.Descriptor.NodeId, StringComparer.Ordinal)],
                [.. _state.Workloads.Values.OrderBy(w => w.Spec.Name, StringComparer.Ordinal).Select(View)]);
        }
    }
}
