using System.Text.Json.Nodes;
using Burbuja.Cluster.Abstractions;
using Burbuja.Cluster.Agent;
using Burbuja.Cluster.Controller;
using Burbuja.Cluster.Os;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Burbuja.Cluster.Tests;

/// <summary>
/// A controller on a fake clock with three synthetic machines of different shapes: an Apple Silicon desktop,
/// a GPU workstation that lends one of its two accelerators, and a small always-on ARM box. Nodes are driven
/// by calling <see cref="Sync"/> directly, so every test states exactly what each node said and when.
/// </summary>
internal sealed class Lab
{
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    public ClusterOptions Options { get; }
    public ClusterController Controller { get; private set; }
    private readonly IPlacementFilter[] _filters;
    private readonly IPlacementScorer[] _scorers;

    public Lab(Action<ClusterOptions>? configure = null, IPlacementFilter[]? filters = null, IPlacementScorer[]? scorers = null)
    {
        Options = new ClusterOptions { Enabled = true, Role = "controller" };
        configure?.Invoke(Options);
        _filters = filters ?? [];
        _scorers = scorers ?? [];
        Controller = Create();
    }

    private ClusterController Create() => new(
        Options, Time, [new ProcessWorkloadDriver(Options, [new PortableExecutionAdapter()])], _filters, _scorers,
        NullLogger<ClusterController>.Instance);

    /// <summary>A second controller process over the same state directory.</summary>
    public void RestartController() => Controller = Create();

    public static NodeDescriptor Studio => new()
    {
        NodeId = "studio",
        Platform = new NodePlatform("macos", "arm64"),
        Capabilities = ["mps", "os.pause", "os.priority", "program.player", "program.trainer"],
        Capacity = new Dictionary<string, decimal> { ["cpu"] = 10, ["memory-mb"] = 16000, ["gpu"] = 1, ["player-slot"] = 2 },
        Devices = [new NodeDevice("gpu", "0", "Apple GPU", "mps")],
        Kinds = ["process"],
    };

    public static NodeDescriptor Tower => new()
    {
        NodeId = "tower",
        Platform = new NodePlatform("linux", "x64"),
        Capabilities = ["cuda", "os.cpu-limit", "os.memory-limit", "os.pause", "os.priority", "program.player", "program.trainer"],
        Capacity = new Dictionary<string, decimal> { ["cpu"] = 16, ["memory-mb"] = 64000, ["gpu"] = 1, ["player-slot"] = 2 },
        Devices = [new NodeDevice("gpu", "0", "Desktop GPU", "cuda", 8192, Lendable: false), new NodeDevice("gpu", "1", "Compute GPU", "cuda", 24576)],
        Kinds = ["process"],
    };

    public static NodeDescriptor Edge => new()
    {
        NodeId = "edge",
        Platform = new NodePlatform("linux", "arm64"),
        Labels = new Dictionary<string, string> { ["role"] = "edge" },
        Capabilities = ["os.pause", "os.priority", "program.proxy"],
        Capacity = new Dictionary<string, decimal> { ["cpu"] = 4, ["memory-mb"] = 8000 },
        Kinds = ["process"],
    };

    public void Join(params NodeDescriptor[] nodes)
    {
        foreach (var node in nodes)
        {
            Controller.RegisterLocalNode(node);
            Sync(node);
        }
    }

    public NodeSyncResponse Sync(NodeDescriptor node, params WorkloadReport[] reports) => Sync(node, "first", PressureLevel.Normal, reports);

    private long _sequence;

    /// <summary>A request addressed to the current controller process with the next sequence number, as a node's link would send it.</summary>
    public NodeSyncRequest Request(NodeDescriptor node, string instance = "first") => new()
    {
        InstanceId = instance,
        ControllerInstanceId = Controller.Identity.ControllerInstanceId,
        Sequence = ++_sequence,
        Descriptor = node,
    };

    public NodeSyncResponse Sync(NodeDescriptor node, string instance, PressureLevel? pressure, params WorkloadReport[] reports)
    {
        var outcome = Controller.Sync(node.NodeId, new NodeSyncRequest
        {
            InstanceId = instance,
            ControllerInstanceId = Controller.Identity.ControllerInstanceId,
            Sequence = ++_sequence,
            Descriptor = node,
            Observation = pressure is null ? null : new NodeObservation { At = Time.GetUtcNow(), Pressure = pressure.Value },
            Workloads = reports,
        });
        return outcome.Response ?? throw new InvalidOperationException("sync failed: " + outcome.Result);
    }

    public WorkloadReport Running(WorkloadView workload) => new(workload.Spec.Name, workload.Generation, WorkloadPhase.Running, Time.GetUtcNow());

    public WorkloadView Workload(string name) => Controller.Status().Workloads.Single(w => w.Spec.Name == name);

    public NodeView Node(string id) => Controller.Status().Nodes.Single(n => n.Descriptor.NodeId == id);

    public WorkloadView Apply(WorkloadSpec spec)
    {
        var outcome = Controller.Apply(spec);
        if (!outcome.Accepted) throw new InvalidOperationException(string.Join("; ", outcome.Errors));
        return Workload(spec.Name);
    }

    public static WorkloadSpec Program(string name, string program, WorkloadClass @class = WorkloadClass.Service) => new()
    {
        Name = name,
        Class = @class,
        Payload = new JsonObject { ["program"] = program },
    };
}
