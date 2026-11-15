using System.Text.Json.Nodes;

namespace Burbuja.Cluster.Abstractions;

/// <summary>
/// What kind of work this is, which decides who waits for whom and what happens when the cluster cannot see.
///
/// <para><b>The class sets the failure direction.</b> A node whose telemetry went stale or that reports
/// pressure still accepts <see cref="Interactive"/> and <see cref="Service"/> work (fail open: refusing a
/// game room because a monitor died turns a monitoring fault into an outage) and refuses <see cref="Batch"/>
/// and <see cref="BestEffort"/> work (fail closed: nobody is waiting for it, and starting heavy compute on a
/// host nobody can see is how a shared machine gets hurt).</para>
/// </summary>
public enum WorkloadClass
{
    /// <summary>Somebody is waiting on each response. Never paused, never starved by other cluster work.</summary>
    Interactive,

    /// <summary>Long-running and expected to stay up.</summary>
    Service,

    /// <summary>Throughput work that can be paused, stopped and resumed.</summary>
    Batch,

    /// <summary>Runs only on spare capacity.</summary>
    BestEffort,
}

public enum RestartPolicy
{
    Always,

    /// <summary>Restart after a failure; a clean exit means the work is done and it stays down.</summary>
    OnFailure,
    Never,
}

public enum NodeLossPolicy
{
    /// <summary>
    /// Stay bound to the node and wait for it. The default, because moving work is rarely free: it can sit next
    /// to data that lives on one machine, or be part of a measurement that a move would invalidate.
    /// </summary>
    Wait,

    /// <summary>Place it again elsewhere once the node has been gone long enough to be sure it stopped.</summary>
    Reschedule,
}

public sealed record WorkloadLifecycle
{
    public RestartPolicy Restart { get; init; } = RestartPolicy.OnFailure;
    public TimeSpan RestartDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a stop waits for the work to finish by itself before the process tree is killed.</summary>
    public TimeSpan StopGrace { get; init; } = TimeSpan.FromSeconds(30);
    public NodeLossPolicy OnNodeLoss { get; init; } = NodeLossPolicy.Wait;
}

/// <summary>
/// Where a workload may and would rather run. The hard members remove nodes; the soft ones only order what is
/// left, so a preference can never place work on a node that fails a requirement.
/// </summary>
public sealed record PlacementSpec
{
    /// <summary>When non-empty, the only nodes allowed.</summary>
    public IReadOnlyList<string> Nodes { get; init; } = [];
    public IReadOnlyDictionary<string, string> RequiredLabels { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = [];

    /// <summary>Nodes in order of preference; pressure and load cannot reorder them, only remove them.</summary>
    public IReadOnlyList<string> PreferredNodes { get; init; } = [];

    /// <summary>Workloads sharing a group prefer different nodes.</summary>
    public string? SpreadGroup { get; init; }
}

/// <summary>
/// One unit of work the cluster places and keeps running.
///
/// <para><b><see cref="Payload"/> belongs to the kind.</b> The kernel does not read it; the driver for
/// <see cref="Kind"/> validates it against its own schema. That is the seam a plugin uses to bring its own
/// typed workloads without the kernel learning their vocabulary.</para>
///
/// <para><b><see cref="Requests"/> are declared, not measured.</b> Placement adds up what was asked for against
/// what each node lends. A workload that takes more than it asked for is a limit problem
/// (<see cref="ExecutionProfile.Limits"/>), not a scheduling one.</para>
/// </summary>
public sealed record WorkloadSpec
{
    public required string Name { get; init; }
    public string Kind { get; init; } = ClusterConstants.Kinds.Process;
    public JsonObject Payload { get; init; } = [];
    public WorkloadClass Class { get; init; } = WorkloadClass.Service;
    public IReadOnlyDictionary<string, decimal> Requests { get; init; } = ResourceMath.Empty;
    public PlacementSpec Placement { get; init; } = new();
    public ExecutionProfile Execution { get; init; } = new();
    public WorkloadLifecycle Lifecycle { get; init; } = new();

    /// <summary>
    /// A cluster-wide key at most one running workload may hold (an account, a promoter role, a device). It
    /// fails closed: a node that cannot reach the controller stops the holder before the key can be given to
    /// anyone else.
    /// </summary>
    public string? ExclusiveKey { get; init; }
}

public enum WorkloadPhase
{
    /// <summary>No node can take it right now; the reasons are recorded per node.</summary>
    Pending,

    /// <summary>A node was chosen and has not reported yet.</summary>
    Assigned,
    Starting,
    Running,

    /// <summary>Suspended in place so interactive work on the same node can answer.</summary>
    Paused,
    Stopping,
    Stopped,

    /// <summary>Exited cleanly and its restart policy says to stay down.</summary>
    Completed,
    Failed,

    /// <summary>The node's own policy declined it. The scheduler will not offer it to that node again unchanged.</summary>
    Refused,

    /// <summary>Its node is unreachable, so its real state is not known. Never reported as stopped.</summary>
    Unknown,
}

/// <summary>What a driver found wrong with a spec, and what a node must offer beyond the declared requests.</summary>
public sealed record WorkloadValidation(IReadOnlyList<string> Errors, IReadOnlyList<string> RequiredCapabilities)
{
    public static readonly WorkloadValidation Ok = new([], []);

    public static WorkloadValidation Invalid(params string[] errors) => new(errors, []);

    public bool IsValid => Errors.Count == 0;
}

public sealed record WorkloadStart(
    WorkloadSpec Spec,
    long Generation,
    string NodeId,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<NodeDevice> Devices);

public sealed record WorkloadExit(int? ExitCode, string? Reason = null)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>A running unit of work owned by this node.</summary>
public interface IWorkloadInstance
{
    /// <summary>An opaque, persistable reference a later process can use to find the same work again.</summary>
    string Handle { get; }

    Task<WorkloadExit> Completion { get; }

    ValueTask PauseAsync(CancellationToken ct);

    ValueTask ResumeAsync(CancellationToken ct);

    /// <summary>Ask the work to finish, wait up to <paramref name="grace"/>, then end it.</summary>
    ValueTask StopAsync(TimeSpan grace, CancellationToken ct);
}

/// <summary>
/// Runs one kind of workload on a node. The built-in kind is an allowlisted process; a plugin adds a kind by
/// registering another driver (a container, a game room, a model server) with its own payload schema.
///
/// <para><b>The node decides what it runs.</b> A driver validates against what <em>this node's operator</em>
/// configured, and declines everything else. A cluster controller can ask; it cannot make a node execute
/// something its owner never allowed.</para>
/// </summary>
public interface IWorkloadDriver
{
    string Kind { get; }

    /// <summary>Pure check of a spec. Runs on the controller when it has the driver, and always on the node.</summary>
    WorkloadValidation Validate(WorkloadSpec spec);

    /// <summary>Advertise what this driver offers on this node (for the process kind: the allowlisted programs).</summary>
    void Describe(NodeDescription description)
    {
    }

    ValueTask<IWorkloadInstance> StartAsync(WorkloadStart start, CancellationToken ct);

    /// <summary>
    /// Find work a previous process of this node started, so a node restart does not interrupt it. Returns null
    /// when it is gone or cannot be proven to be the same work.
    /// </summary>
    ValueTask<IWorkloadInstance?> AdoptAsync(WorkloadStart start, string handle, CancellationToken ct) =>
        ValueTask.FromResult<IWorkloadInstance?>(null);
}
