using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using Burbuja.Cluster;
using Burbuja.Cluster.Abstractions;
using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Core.Plugin;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Burbuja.Hosting.Tests.WorkbenchFixture;

namespace Burbuja.Hosting.Tests;

/// <summary>The cluster module inside the full kernel host, with a workload kind brought by a dynamically loaded plugin.</summary>
public sealed class ClusterModuleTests
{
    private const string Identity = "/.well-known/burbuja/cluster";

    [Fact]
    public async Task The_cluster_is_absent_until_a_host_enables_it()
    {
        await using var host = new WorkbenchFixture();
        await host.StartAsync();

        Assert.Null(host.App.Services.GetService<IClusterControlPlane>());
        Assert.Null(host.App.Services.GetService<INodePressure>());
        Assert.DoesNotContain(host.App.Services.GetServices<Burbuja.Realtime.Abstractions.ISessionResourceAdmission>(), a => a.GetType().Name == "ClusterRealtimeAdmission");
        Assert.False(host.App.Services.GetRequiredService<ClusterOptions>().Enabled);
        using var client = host.Client();
        using var response = await client.GetAsync(Identity, Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_pressed_node_holds_back_new_realtime_claims_only_when_asked_to_and_only_when_it_knows()
    {
        static Dictionary<string, string?> Settings(bool guard) => new()
        {
            ["Cluster:Enabled"] = "true",
            ["Cluster:Http"] = "false",
            ["Cluster:Role"] = "all",
            ["Cluster:NodeId"] = "local",
            ["Cluster:Agent:GuardRealtime"] = guard ? "true" : "false",
        };

        await using (var unguarded = new WorkbenchFixture())
        {
            unguarded.Create(role: "all", configure: builder => builder.Configuration.AddInMemoryCollection(Settings(guard: false)));
            Assert.NotNull(unguarded.App.Services.GetService<INodePressure>());
            Assert.DoesNotContain(unguarded.App.Services.GetServices<Burbuja.Realtime.Abstractions.ISessionResourceAdmission>(), a => a.GetType().Name == "ClusterRealtimeAdmission");
        }

        await using var host = new WorkbenchFixture();
        host.Create(role: "all", configure: builder => builder.Configuration.AddInMemoryCollection(Settings(guard: true)));
        var admission = Assert.Single(host.App.Services.GetServices<Burbuja.Realtime.Abstractions.ISessionResourceAdmission>(), a => a.GetType().Name == "ClusterRealtimeAdmission");
        var monitor = host.App.Services.GetRequiredService<Burbuja.Cluster.Agent.NodePressureMonitor>();
        Assert.Same(monitor, host.App.Services.GetRequiredService<INodePressure>());

        // Unknown admits: a node that cannot read itself must not turn that into an outage for players.
        Assert.True(admission.CanIncrease("simulation"));

        var pressed = new NodeObservation { Pressure = PressureLevel.Critical, Reasons = ["memory is low"] };
        monitor.Sample(pressed);
        monitor.Sample(pressed);
        Assert.True(admission.CanIncrease("simulation"));
        monitor.Sample(pressed);
        Assert.False(admission.CanIncrease("simulation"));
        Assert.False(admission.CanIncrease("peer-relay"));

        var clear = new NodeObservation { Pressure = PressureLevel.Normal };
        for (var i = 0; i < 6; i++) monitor.Sample(clear);
        Assert.True(admission.CanIncrease("simulation"));

        // This host uses the cluster in process only: with Cluster:Http off nothing of it is on the listener,
        // not even the public identity document.
        await host.App.StartAsync(Ct);
        using var client = host.Client();
        foreach (var path in new[] { Identity, "/cluster/v1/status", "/node/v1/status" })
        {
            using var response = await client.GetAsync(path, Ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_plugin_brings_its_own_workload_kind_to_a_cluster_enabled_host()
    {
        await using var host = new WorkbenchFixture();
        CopyDirectory(Path.Combine(AppContext.BaseDirectory, "cluster-plugin-fixture"), Path.Combine(host.Root, "plugins", "Example.Cluster"));
        await host.StartAsync(role: "all", configure: builder => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cluster:Enabled"] = "true",
            ["Cluster:Role"] = "all",
            ["Cluster:NodeId"] = "local",
            ["Cluster:Controller:SyncIntervalSeconds"] = "0.1",
        }));

        // The plugin is contracts-only and lives in its own load context; its driver is the host's contract type.
        var loaded = Assert.Single(host.App.Services.GetRequiredService<PluginHost>().Loaded, p => p.Manifest.Id == "Example.Cluster");
        Assert.Equal(PluginLoadKind.Dynamic, loaded.EffectiveLoad);
        Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(loaded.Assembly));
        Assert.Equal(new[] { "Burbuja.Cluster.Abstractions", "Burbuja.Core.Abstractions" },
            loaded.Assembly.GetReferencedAssemblies().Select(a => a.Name!).Where(n => n.StartsWith("Burbuja.", StringComparison.Ordinal)).Order().ToArray());
        var driver = Assert.Single(host.App.Services.GetServices<IWorkloadDriver>(), d => d.Kind == "example.countdown");
        Assert.Same(loaded.Assembly, driver.GetType().Assembly);

        // The identity document is public even though this host requires authentication by default; the
        // operator API is not, and the host's own API keys are not what opens it.
        using var anonymous = host.Client(key: null);
        using (var identity = await anonymous.GetAsync(Identity, Ct))
            Assert.Equal(ClusterConstants.ProtocolVersion, (await ExpectAsync(identity, HttpStatusCode.OK))["protocol"]!.GetValue<int>());
        using (var withApiKey = host.Client())
        using (var refused = await withApiKey.GetAsync("/cluster/v1/status", Ct))
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        using var operatorClient = host.Client(key: null);
        operatorClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", host.App.Services.GetRequiredService<ClusterTokens>().OperatorToken);

        // The plugin's own schema is enforced when the workload is submitted, in the plugin's own words.
        using (var invalid = await operatorClient.PutAsJsonAsync("/cluster/v1/workloads/broken",
                   new { name = "broken", kind = "example.countdown", payload = new { seconds = 0 } }, Ct))
        {
            var errors = (await ExpectAsync(invalid, HttpStatusCode.BadRequest))["errors"]!.AsArray();
            Assert.Equal("payload.seconds must be greater than 0 and at most 3600", errors.Single()!.GetValue<string>());
        }

        using (var accepted = await operatorClient.PutAsJsonAsync("/cluster/v1/workloads/tick",
                   new { name = "tick", kind = "example.countdown", payload = new { seconds = 0.2, label = "demo" }, lifecycle = new { restart = "never" } }, Ct))
            await ExpectAsync(accepted, HttpStatusCode.OK);

        // The host's own node runs it, with the kind the plugin advertised, and reports it finished.
        JsonObject? workload = null;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            using var response = await operatorClient.GetAsync("/cluster/v1/status", Ct);
            var status = await ExpectAsync(response, HttpStatusCode.OK);
            workload = status["workloads"]!.AsArray().Single()!.AsObject();
            if (workload["phase"]!.GetValue<string>() == "completed")
            {
                var node = status["nodes"]!.AsArray().Single()!["descriptor"]!;
                Assert.Equal("local", node["nodeId"]!.GetValue<string>());
                Assert.Contains("example.countdown", node["capabilities"]!.AsArray().Select(c => c!.GetValue<string>()));
                Assert.Contains("example.countdown", node["kinds"]!.AsArray().Select(c => c!.GetValue<string>()));
                break;
            }

            await Task.Delay(50, Ct);
        }

        Assert.Equal("completed", workload!["phase"]!.GetValue<string>());
        Assert.Equal("local", workload["nodeId"]!.GetValue<string>());
    }
}
