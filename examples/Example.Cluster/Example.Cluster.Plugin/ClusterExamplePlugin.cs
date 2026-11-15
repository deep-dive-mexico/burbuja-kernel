using System.Text.Json;
using Burbuja.Cluster.Abstractions;
using Burbuja.Core.Abstractions.Plugin;
using Microsoft.Extensions.DependencyInjection;

namespace Example.Cluster.Plugin;

/// <summary>
/// Shows the two things an application adds to a cluster without touching the kernel: a workload kind with
/// its own typed payload, and a placement preference. Both are ordinary services. The plugin references only
/// the contract assemblies, so the same build works in any host that has the cluster module.
/// </summary>
public sealed class ClusterExamplePlugin : IBurbujaPlugin
{
    public PluginManifest Manifest { get; } = new(
        "Example.Cluster", "Example cluster workload kind", "1.0.0", PluginContracts.Version,
        "Example.Cluster.Plugin.dll", typeof(ClusterExamplePlugin).FullName!, PluginLoadKind.Dynamic,
        ["Example.Cluster"], [], [], ["all"], null, false, [], 900);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IWorkloadDriver, CountdownDriver>();
        services.AddSingleton<IPlacementScorer, QuietNodeScorer>();
    }
}

/// <summary>The payload of an <c>example.countdown</c> workload. The kernel never reads it; this plugin does.</summary>
public sealed record CountdownPayload(decimal Seconds, string? Label = null);

/// <summary>
/// A workload kind that is not a process at all: it waits for a number of seconds and finishes. It stands in
/// for anything an application runs inside its own host (a game room, a model session, a queue consumer),
/// and shows the whole contract: validate the payload, say what a node must offer, start, suspend, stop.
/// </summary>
public sealed class CountdownDriver : IWorkloadDriver
{
    public const string KindName = "example.countdown";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Kind => KindName;

    public WorkloadValidation Validate(WorkloadSpec spec)
    {
        CountdownPayload? payload;
        try
        {
            payload = spec.Payload.Deserialize<CountdownPayload>(Json);
        }
        catch (JsonException)
        {
            return WorkloadValidation.Invalid("payload must be {\"seconds\": number, \"label\"?: string}");
        }

        if (payload is null || payload.Seconds <= 0 || payload.Seconds > 3600)
            return WorkloadValidation.Invalid("payload.seconds must be greater than 0 and at most 3600");

        // Only nodes whose host loaded this plugin can run the kind; saying so here keeps the scheduler from
        // offering it to a node that would have to refuse it.
        return new WorkloadValidation([], [KindName]);
    }

    public void Describe(NodeDescription description) => description.Capabilities.Add(KindName);

    public ValueTask<IWorkloadInstance> StartAsync(WorkloadStart start, CancellationToken ct)
    {
        var payload = start.Spec.Payload.Deserialize<CountdownPayload>(Json)!;
        return ValueTask.FromResult<IWorkloadInstance>(new Countdown(TimeSpan.FromSeconds((double)payload.Seconds)));
    }

    private sealed class Countdown : IWorkloadInstance
    {
        private readonly CancellationTokenSource _stop = new();
        private volatile bool _paused;

        public Countdown(TimeSpan duration) => Completion = RunAsync(duration);

        public string Handle { get; } = "countdown:" + Guid.NewGuid().ToString("N");
        public Task<WorkloadExit> Completion { get; }

        private async Task<WorkloadExit> RunAsync(TimeSpan remaining)
        {
            var step = TimeSpan.FromMilliseconds(20);
            while (remaining > TimeSpan.Zero)
            {
                try { await Task.Delay(step, _stop.Token); }
                catch (OperationCanceledException) { return new WorkloadExit(null, "stopped before it finished"); }
                if (!_paused) remaining -= step; // Suspended time does not count.
            }

            return new WorkloadExit(0);
        }

        public ValueTask PauseAsync(CancellationToken ct)
        {
            _paused = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask ResumeAsync(CancellationToken ct)
        {
            _paused = false;
            return ValueTask.CompletedTask;
        }

        public async ValueTask StopAsync(TimeSpan grace, CancellationToken ct)
        {
            await _stop.CancelAsync();
            await Completion;
        }
    }
}

/// <summary>
/// A placement preference: among the nodes the kernel found eligible, favour the one running the least. It
/// can only reorder; whether a node is eligible at all is decided before any scorer is asked.
/// </summary>
public sealed class QuietNodeScorer : IPlacementScorer
{
    public string Id => "example-quiet-node";

    public decimal Score(WorkloadPlacementRequest request, NodeView node) => -node.Workloads.Count;
}
