using Burbuja.Cluster.Abstractions;

namespace Burbuja.Cluster.Agent;

/// <summary>
/// The node-local signal that interactive work is answering right now.
///
/// <para>A latency-critical workload takes a hold for the few seconds it needs the machine, and every
/// workload on this node that declared <see cref="ExecutionProfile.YieldToInteractive"/> is suspended until
/// the last hold is gone. It is decided and applied on the node, with no controller round trip, because the
/// window it protects is shorter than any sync interval.</para>
///
/// <para><b>A hold always expires.</b> A holder that crashes cannot leave batch work suspended: every hold
/// carries a time to live, capped by the node's operator, and must be renewed to last longer.</para>
/// </summary>
internal sealed class InteractiveGate
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _holds = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly TimeSpan _maximum;
    private readonly ITimer _timer;
    private bool _busy;

    public InteractiveGate(TimeProvider time, ClusterOptions options)
    {
        _time = time;
        _maximum = TimeSpan.FromSeconds(options.Agent.InteractiveHoldMaxSeconds);
        _timer = time.CreateTimer(_ => Evaluate(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Raised outside the lock whenever the node goes from idle to busy or back.</summary>
    public event Action<bool>? Changed;

    public bool Busy
    {
        get { lock (_gate) return _busy; }
    }

    public IReadOnlyList<string> Holders
    {
        get { lock (_gate) return [.. _holds.Keys.Order(StringComparer.Ordinal)]; }
    }

    public void Hold(string holder, TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero || ttl > _maximum) ttl = _maximum;
        lock (_gate) _holds[holder] = _time.GetUtcNow() + ttl;
        Evaluate();
    }

    public void Release(string holder)
    {
        lock (_gate) _holds.Remove(holder);
        Evaluate();
    }

    private void Evaluate()
    {
        bool busy;
        bool changed;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var expired in _holds.Where(h => h.Value <= now).Select(h => h.Key).ToArray()) _holds.Remove(expired);
            busy = _holds.Count > 0;
            changed = busy != _busy;
            _busy = busy;
            _timer.Change(busy ? Max(_holds.Values.Min() - now, TimeSpan.FromMilliseconds(1)) : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        if (changed) Changed?.Invoke(busy);

        static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    }
}

/// <summary>
/// The node's own verdict on work it was offered, before any driver runs. A pure function of the workload and
/// this node's configuration, returning the reason for a refusal or null to allow.
///
/// <para>The controller filters on what nodes advertise, so a correct controller rarely reaches a refusal
/// here. This check exists for the incorrect one: it is the node's operator, not the cluster, who decides
/// what runs on the machine, and the node enforces that itself.</para>
/// </summary>
internal static class NodeExecutionPolicy
{
    public static string? Refusal(WorkloadSpec spec, AgentOptions node, IReadOnlySet<string> enforceable, IReadOnlyList<NodeDevice> devices)
    {
        if (node.Kinds is { } kinds && !kinds.Contains(spec.Kind, StringComparer.Ordinal))
            return $"kind '{spec.Kind}' is not allowed on this node";

        // Lower enum values are more favourable, so "more favourable than the maximum" is a smaller number.
        if (spec.Execution.Priority < node.MaxPriority)
            return $"priority '{Name(spec.Execution.Priority)}' is above this node's maximum '{Name(node.MaxPriority)}'";

        if (spec.Execution.Strict)
        {
            var missing = OsAspects.RequiredBy(spec.Execution).FirstOrDefault(a => !enforceable.Contains(a));
            if (missing is not null) return $"this node cannot enforce '{missing}'";
        }

        foreach (var id in spec.Execution.Devices)
        {
            if (!devices.Any(d => d.Id == id && d.Lendable)) return $"device '{id}' is not lent by this node";
        }

        return null;

        static string Name(ExecutionPriority priority) => priority.ToString().ToLowerInvariant();
    }
}

/// <summary>Which lent devices each workload on this node holds. A device is given to one workload at a time.</summary>
internal sealed class DeviceLedger
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _owner = new(StringComparer.Ordinal);
    private IReadOnlyList<NodeDevice> _devices = [];

    public void SetDevices(IReadOnlyList<NodeDevice> devices)
    {
        lock (_gate) _devices = devices;
    }

    /// <summary>The devices for a workload, an empty list when it asked for none, or null when they are not free.</summary>
    public IReadOnlyList<NodeDevice>? Acquire(WorkloadSpec spec)
    {
        lock (_gate)
        {
            Release(spec.Name);
            var free = _devices.Where(d => d.Lendable && d.Kind == ClusterConstants.Resources.Gpu && !_owner.ContainsKey(d.Id)).ToList();
            List<NodeDevice> chosen;
            if (spec.Execution.Devices.Count > 0)
            {
                chosen = [.. free.Where(d => spec.Execution.Devices.Contains(d.Id, StringComparer.Ordinal))];
                if (chosen.Count != spec.Execution.Devices.Count) return null;
            }
            else
            {
                var wanted = (int)Math.Ceiling(spec.Requests.GetValueOrDefault(ClusterConstants.Resources.Gpu));
                if (free.Count < wanted) return null;
                chosen = free.Take(wanted).ToList();
            }

            foreach (var device in chosen) _owner[device.Id] = spec.Name;
            return chosen;
        }
    }

    public void Release(string workload)
    {
        lock (_gate)
        {
            foreach (var id in _owner.Where(o => o.Value == workload).Select(o => o.Key).ToArray()) _owner.Remove(id);
        }
    }
}
