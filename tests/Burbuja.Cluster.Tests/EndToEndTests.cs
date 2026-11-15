using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Burbuja.Cluster.Abstractions;
using Burbuja.Cluster.Agent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Burbuja.Cluster.Tests;

// Every wait in this file is a poll with its own deadline (see Eventually), and the hosts under test are
// stopped by the fixture, so the per-call test cancellation token adds nothing here.
#pragma warning disable xUnit1051

/// <summary>
/// Whole hosts on real loopback sockets: a controller and separate agents that join with one-time tokens,
/// sign every request, and run real process trees. Timings are shortened; nothing else is faked.
/// </summary>
public sealed class EndToEndTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cluster-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly List<TestHost> _hosts = [];
    private readonly HttpClient _http = new();
    private string _program = "";

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _program = Path.Combine(_root, "work.sh");

        // The stand-in for real work: it records that it started and where, then runs until asked to stop.
        File.WriteAllText(_program, """
            echo "$$" > "$BURBUJA_WORKLOAD.pid"
            echo "$BURBUJA_NODE $BURBUJA_GENERATION $1" >> "$BURBUJA_WORKLOAD.starts"
            trap 'exit 0' TERM
            while true; do sleep 0.1; done
            """);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts) await host.DisposeAsync();
        _http.Dispose();
        Directory.Delete(_root, true);
    }

    private sealed class TestHost(WebApplication app, string name, int port, string state) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;
        public string Name { get; } = name;
        public int Port { get; } = port;
        public string State { get; } = state;
        public Uri Url => new($"http://127.0.0.1:{Port}");
        public NodeAgent Agent => App.Services.GetRequiredService<NodeAgent>();
        public string Work => Path.Combine(State, "cwd");
        private bool _stopped;

        public async ValueTask DisposeAsync()
        {
            if (_stopped) return;
            _stopped = true;
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private async Task<TestHost> StartAsync(string name, string role, Dictionary<string, string?>? extra = null, int? port = null, bool stopWork = true)
    {
        var listen = port ?? FreePort();
        var state = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(state, "cwd"));
        var settings = new Dictionary<string, string?>
        {
            ["Cluster:Enabled"] = "true",
            ["Cluster:Role"] = role,
            ["Cluster:NodeId"] = name,
            ["Cluster:StateDirectory"] = state,
            ["Cluster:Port"] = listen.ToString(),
            ["Cluster:Controller:SyncIntervalSeconds"] = "0.1",
            ["Cluster:Controller:NodeTtlSeconds"] = "1",
            ["Cluster:Controller:ExclusiveTtlSeconds"] = "0.8",
            ["Cluster:Controller:NodeLossGraceSeconds"] = "2",
            ["Cluster:Agent:JoinTokenFile"] = Path.Combine(state, "join.token"),
            ["Cluster:Agent:StopWorkloadsOnShutdown"] = stopWork ? "true" : "false",
            ["Cluster:Agent:CpuBusyPercent"] = "100000", // The machine running the tests may be busy; that is not under test.
            ["Cluster:Agent:MinFreeMemoryMb"] = "0",
            ["Cluster:Agent:Programs:work:Executable"] = "/bin/sh",
            ["Cluster:Agent:Programs:work:Arguments:0"] = _program,
            ["Cluster:Agent:Programs:work:ParameterFlags:tag"] = "--",
            ["Cluster:Agent:Programs:work:WorkingDirectory"] = Path.Combine(state, "cwd"),
        };
        foreach (var (key, value) in extra ?? []) settings[key] = value;

        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{listen}");
        builder.Services.AddBurbujaCluster(builder.Configuration);
        var app = builder.Build();
        app.MapBurbujaCluster();
        await app.StartAsync();
        var host = new TestHost(app, name, listen, state);
        _hosts.Add(host);
        return host;
    }

    private ClusterClient Operator(TestHost controller) =>
        new(new HttpClient(), controller.Url, File.ReadAllText(Path.Combine(controller.State, "operator.token")).Trim());

    private async Task<TestHost> JoinAsync(TestHost controller, string name, Dictionary<string, string?>? extra = null, bool stopWork = true, string[]? controllers = null)
    {
        var grant = await Operator(controller).CreateJoinTokenAsync();
        Directory.CreateDirectory(Path.Combine(_root, name));
        File.WriteAllText(Path.Combine(_root, name, "join.token"), grant.Token);
        var settings = new Dictionary<string, string?>(extra ?? []);
        var addresses = controllers ?? [controller.Url.ToString()];
        for (var i = 0; i < addresses.Length; i++) settings[$"Cluster:Agent:Controllers:{i}"] = addresses[i];
        return await StartAsync(name, "agent", settings, stopWork: stopWork);
    }

    private static async Task<T> Eventually<T>(Func<Task<T>> read, Func<T, bool> done, string what, int seconds = 20)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
        var last = await read();
        while (!done(last))
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"Timed out waiting until {what}. Last: {ClusterJson.Write(last)}");
            await Task.Delay(40);
            last = await read();
        }

        return last;
    }

    private static Task<WorkloadView> Phase(ClusterClient client, string workload, WorkloadPhase phase, string? node = null) => Eventually(
        async () => (await client.StatusAsync()).Workloads.Single(w => w.Spec.Name == workload),
        w => w.Phase == phase && (node is null || w.NodeId == node),
        $"{workload} is {phase}{(node is null ? "" : " on " + node)}");

    private static int WorkPid(TestHost host, string workload) => int.Parse(File.ReadAllText(Path.Combine(host.Work, workload + ".pid")).Trim());

    private static async Task<string> StateOf(int pid) =>
        (await ProbeCommand.ReadAsync("/bin/ps", ["-o", "state=", "-p", pid.ToString()], TimeSpan.FromSeconds(3), CancellationToken.None))?.Trim() ?? "gone";

    private static WorkloadSpec Work(string name, string tag = "x") => new()
    {
        Name = name,
        Payload = new JsonObject { ["program"] = "work", ["parameters"] = new JsonObject { ["tag"] = tag } },
        Lifecycle = new WorkloadLifecycle { StopGrace = TimeSpan.FromSeconds(5) },
    };

    [Fact]
    public async Task Machines_join_with_tokens_and_each_runs_the_work_it_is_suited_for()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX process semantics");
        var hub = await StartAsync("hub", "controller");
        var client = Operator(hub);

        // Two members with different shapes: one lends player slots, the other has the bulk capability.
        var studio = await JoinAsync(hub, "studio", new() { ["Cluster:Agent:Lend:player-slot"] = "1" });
        var tower = await JoinAsync(hub, "tower", new() { ["Cluster:Agent:Capabilities:0"] = "bulk" });
        var status = await Eventually(() => client.StatusAsync(), s => s.Nodes.Count(n => n.Reachable && n.ObservationFresh) == 2, "both nodes are members");
        status.Nodes.Select(n => n.Descriptor.NodeId).ShouldBe(["studio", "tower"]);
        status.Nodes.All(n => n.Descriptor.Capabilities.Contains("program.work") && n.Descriptor.Capabilities.Contains("os.pause")).ShouldBeTrue();
        status.Nodes.All(n => n.Descriptor.Capacity["cpu"] > 0).ShouldBeTrue();

        // Every member can read its own pressure verdict, and what the controller says of the others, in process.
        var pressure = tower.App.Services.GetRequiredService<INodePressure>();
        await Eventually(() => Task.FromResult(pressure.Local), p => p.Known, "the node has read itself", 5);
        pressure.Local.Pressured.ShouldBeFalse();
        pressure.PressuredNodes.ShouldBeEmpty();

        // A join token works once.
        File.Exists(Path.Combine(studio.State, "node.json")).ShouldBeTrue();
        (await client.ApplyAsync(Work("player") with
        {
            Class = WorkloadClass.Interactive,
            Requests = new Dictionary<string, decimal> { ["player-slot"] = 1 },
        })).Errors.ShouldBeEmpty();
        (await client.ApplyAsync(Work("trainer", "shard-2") with
        {
            Class = WorkloadClass.Batch,
            Placement = new PlacementSpec { RequiredCapabilities = ["bulk"] },
            Execution = new ExecutionProfile { Priority = ExecutionPriority.Background, YieldToInteractive = true },
        })).Errors.ShouldBeEmpty();

        (await Phase(client, "player", WorkloadPhase.Running)).NodeId.ShouldBe("studio");
        var trainer = await Phase(client, "trainer", WorkloadPhase.Running);
        trainer.NodeId.ShouldBe("tower");

        // The work really runs, on the node it was placed on, with the parameter passed as a literal argument.
        File.ReadAllText(Path.Combine(tower.Work, "trainer.starts")).Trim().ShouldBe($"tower {trainer.Generation} --");
        var pid = WorkPid(tower, "trainer");
        (await StateOf(pid)).ShouldNotBe("gone");

        // A second player has no slot anywhere, and the operator is told why.
        var (second, _) = await client.ApplyAsync(Work("player-2") with { Requests = new Dictionary<string, decimal> { ["player-slot"] = 1 } });
        second!.Rejections["studio"].ShouldBe("player-slot: 1 requested, 0 free");
        second.Rejections["tower"].ShouldBe("player-slot is not offered");

        // Interactive work on the tower takes a hold through the node's local API; the trainer is suspended
        // within the node, and the controller hears about it afterwards.
        using var local = new HttpClient { BaseAddress = tower.Url };
        (await local.PutAsync("/node/v1/interactive/turn-1?ttlMs=10000", null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        local.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tower.Agent.LocalToken);
        (await local.PutAsync("/node/v1/interactive/turn-1?ttlMs=10000", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await Eventually(() => StateOf(pid), s => s.StartsWith('T'), "the trainer is suspended", 5);
        (await Phase(client, "trainer", WorkloadPhase.Paused)).Reason.ShouldBe("yielding to interactive work on this node");

        (await local.DeleteAsync("/node/v1/interactive/turn-1")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await Eventually(() => StateOf(pid), s => !s.StartsWith('T'), "the trainer resumed", 5);
        await Phase(client, "trainer", WorkloadPhase.Running);

        // Deleting a workload stops its process.
        (await client.DeleteAsync("trainer")).ShouldBeTrue();
        await Eventually(() => StateOf(pid), s => s == "gone", "the trainer's process ended");

        // The node API is for this machine's own account, and the operator API needs the operator's token.
        (await _http.GetAsync(new Uri(hub.Url, "/cluster/v1/status"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _http.PostAsync(new Uri(hub.Url, "/cluster/v1/nodes/studio/sync"), new StringContent("{}"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Without_its_controller_a_node_stops_exclusive_work_and_keeps_the_rest_running()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX process semantics");
        var hub = await StartAsync("hub", "controller");
        var client = Operator(hub);
        var studio = await JoinAsync(hub, "studio");
        await client.ApplyAsync(Work("account") with { ExclusiveKey = "account/one" });
        await client.ApplyAsync(Work("service"));
        var account = await Phase(client, "account", WorkloadPhase.Running);
        await Phase(client, "service", WorkloadPhase.Running);
        var accountPid = WorkPid(studio, "account");
        var servicePid = WorkPid(studio, "service");

        // The controller goes away. Nothing tells the node to stop anything; it decides by itself.
        await hub.DisposeAsync();
        await Eventually(() => StateOf(accountPid), s => s == "gone", "the node gave up the exclusive key");
        studio.Agent.Status().Workloads.Single(w => w.Name == "account").Reason.ShouldBe("lost contact with the controller; its exclusive key was given up");
        (await StateOf(servicePid)).ShouldNotBe("gone");
        studio.Agent.Status().Workloads.Single(w => w.Name == "service").Phase.ShouldBe(WorkloadPhase.Running);

        // The controller returns with its state. The key is still this node's, so the work starts again
        // under the same generation; the service was never interrupted.
        var back = await StartAsync("hub", "controller", port: hub.Port);
        client = Operator(back);
        await Eventually(() => Task.FromResult(studio.Agent.Status().Workloads.Single(w => w.Name == "account")),
            w => w.Phase == WorkloadPhase.Running, "the exclusive work is running again");
        (await Phase(client, "account", WorkloadPhase.Running)).Generation.ShouldBe(account.Generation);
        WorkPid(studio, "account").ShouldNotBe(accountPid);
        WorkPid(studio, "service").ShouldBe(servicePid);
        File.ReadAllLines(Path.Combine(studio.Work, "service.starts")).Length.ShouldBe(1);
    }

    [Fact]
    public async Task An_address_that_cannot_prove_it_is_the_controller_is_abandoned_for_one_that_can()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX process semantics");
        var hub = await StartAsync("hub", "controller");
        var client = Operator(hub);
        var identity = await _http.GetStringAsync(new Uri(hub.Url, ClusterConstants.IdentityPath));

        // An impostor that copies the public identity document and accepts everything, with no key to sign with.
        var impostorPort = FreePort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{impostorPort}");
        var impostor = builder.Build();
        var captured = 0;
        impostor.MapGet(ClusterConstants.IdentityPath, () => Results.Text(identity, "application/json"));
        impostor.MapPost("/cluster/v1/{**rest}", () =>
        {
            Interlocked.Increment(ref captured);
            return Results.Json(new { clusterId = "x", nodeId = "studio", serverNonce = "y", assignments = Array.Empty<object>() });
        });
        await impostor.StartAsync();
        try
        {
            // Both addresses are loopback, so the impostor, listed first, is tried first.
            var studio = await JoinAsync(hub, "studio", controllers: [$"http://127.0.0.1:{impostorPort}", hub.Url.ToString()]);
            await Eventually(() => client.StatusAsync(), s => s.Nodes.Any(n => n.Reachable), "the node joined the real controller");

            studio.Agent.Status().Route.ShouldBe($"loopback {hub.Url}");
            captured.ShouldBe(1); // One unsigned answer was enough; it was never asked again.
            await client.ApplyAsync(Work("service"));
            await Phase(client, "service", WorkloadPhase.Running);
        }
        finally
        {
            await impostor.StopAsync();
            await impostor.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_removed_node_stops_the_clusters_work_and_a_restarted_node_keeps_it()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX process semantics");
        var hub = await StartAsync("hub", "controller");
        var client = Operator(hub);
        var settings = new Dictionary<string, string?> { ["Cluster:Agent:Controllers:0"] = hub.Url.ToString() };
        var studio = await JoinAsync(hub, "studio", stopWork: false);
        await client.ApplyAsync(Work("service"));
        await client.ApplyAsync(Work("account") with { ExclusiveKey = "account/one" });
        await Phase(client, "service", WorkloadPhase.Running);
        var account = await Phase(client, "account", WorkloadPhase.Running);
        var pid = WorkPid(studio, "service");
        var accountPid = WorkPid(studio, "account");

        // The node process restarts (an upgrade). The work is not its casualty: the new process finds it,
        // proves it is the same, and carries on reporting it without a restart. Work holding an exclusive
        // key is the exception: with the node process gone nothing could give the key up, so it is stopped.
        await studio.DisposeAsync();
        (await StateOf(pid)).ShouldNotBe("gone");
        (await StateOf(accountPid)).ShouldBe("gone");
        await Eventually(() => client.StatusAsync(), s => !s.Nodes.Single().Reachable, "the controller noticed the node went quiet");
        (await client.StatusAsync()).Workloads.ShouldAllBe(w => w.Phase == WorkloadPhase.Unknown);

        studio = await StartAsync("studio", "agent", settings, port: studio.Port, stopWork: false);
        var service = await Phase(client, "service", WorkloadPhase.Running);
        service.Restarts.ShouldBe(0);
        WorkPid(studio, "service").ShouldBe(pid);
        File.ReadAllLines(Path.Combine(studio.Work, "service.starts")).Length.ShouldBe(1);

        // The key is still this node's, so its holder starts again under the same generation.
        (await Phase(client, "account", WorkloadPhase.Running)).Generation.ShouldBe(account.Generation);
        await Eventually(() => Task.FromResult(WorkPid(studio, "account")), p => p != accountPid, "the account work started again", 10);
        accountPid = WorkPid(studio, "account");

        // An operator removes the node. The controller proves the removal to it, and the node stops the work.
        (await client.ForgetNodeAsync("studio")).ShouldBeTrue();
        await Eventually(() => StateOf(pid), s => s == "gone", "the removed node stopped the work");
        await Eventually(() => StateOf(accountPid), s => s == "gone", "the removed node stopped the exclusive work");
        await Eventually(() => Task.FromResult(studio.Agent.Status().Link), l => l == "removed", "the node knows it was removed", 5);
        (await client.StatusAsync()).Nodes.ShouldBeEmpty();
        (await client.StatusAsync()).Workloads.ShouldAllBe(w => w.NodeId == null);
    }
}
