using System.Text.Json.Nodes;
using Burbuja.Compute;
using Burbuja.Compute.Abstractions;
using Burbuja.Compute.Backends;
using Burbuja.Compute.Runtime;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Arm;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Burbuja.Compute.UnitTests;

public sealed class LifecycleTests
{
    [Theory]
    [InlineData(ComputeWorkspaceState.Creating, ComputeWorkspaceState.Running, true)]
    [InlineData(ComputeWorkspaceState.Running, ComputeWorkspaceState.Stopping, true)]
    [InlineData(ComputeWorkspaceState.Stopping, ComputeWorkspaceState.Stopped, true)]
    [InlineData(ComputeWorkspaceState.Deallocated, ComputeWorkspaceState.Creating, true)]
    [InlineData(ComputeWorkspaceState.Deleted, ComputeWorkspaceState.Running, false)]
    [InlineData(ComputeWorkspaceState.Running, ComputeWorkspaceState.Deleted, false)]
    public void CanTransition_follows_the_table(ComputeWorkspaceState from, ComputeWorkspaceState to, bool expected)
    {
        ComputeLifecycle.CanTransition(from, to).ShouldBe(expected);
    }

    [Fact]
    public void Nothing_is_reachable_from_a_terminal_state()
    {
        foreach (var state in Enum.GetValues<ComputeWorkspaceState>())
            ComputeLifecycle.CanTransition(ComputeWorkspaceState.Deleted, state).ShouldBeFalse();

        ComputeLifecycle.IsTerminal(ComputeWorkspaceState.Deleted).ShouldBeTrue();
        ComputeLifecycle.IsTerminal(ComputeWorkspaceState.Running).ShouldBeFalse();
    }
}

public sealed class ValidatorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Unknown_backend_image_and_size_are_rejected()
    {
        var harness = Harness.CreateHost();
        using var sp = harness.Services;
        await Harness.SeedAsync(sp);

        (await Harness.Send(sp, "PUT", Harness.WorkspacePath("w"), Harness.Spec(backend: "docker"))).StatusCode.ShouldBe(400);
        (await Harness.Send(sp, "PUT", Harness.WorkspacePath("w"), Harness.Spec(image: "windows"))).StatusCode.ShouldBe(400);
        (await Harness.Send(sp, "PUT", Harness.WorkspacePath("w"), Harness.Spec(size: "huge"))).StatusCode.ShouldBe(400);
    }

    [Fact]
    public async Task Denied_environment_key_is_rejected()
    {
        var harness = Harness.CreateHost();
        using var sp = harness.Services;
        await Harness.SeedAsync(sp);

        var body = Harness.Spec(extra: ""","envAllowlist":["BURBUJA_TOKEN"]""");
        (await Harness.Send(sp, "PUT", Harness.WorkspacePath("w"), body)).StatusCode.ShouldBe(400);
    }

    [Fact]
    public async Task Both_repo_bindings_are_rejected()
    {
        var harness = Harness.CreateHost();
        using var sp = harness.Services;
        await Harness.SeedAsync(sp);

        var body = Harness.Spec(extra: ""","repo":{"agentsWorkspaceId":"/subscriptions/s/resourceGroups/rg/providers/Burbuja.Agents/workspaces/a","remote":"https://example.com/r.git"}""");
        (await Harness.Send(sp, "PUT", Harness.WorkspacePath("w"), body)).StatusCode.ShouldBe(400);
    }

    [Fact]
    public async Task Accepting_a_workspace_returns_202_then_provisions()
    {
        var harness = Harness.CreateHost();
        using var sp = harness.Services;
        await Harness.SeedAsync(sp);

        var put = await Harness.Send(sp, "PUT", Harness.WorkspacePath("w1"), Harness.Spec());
        put.StatusCode.ShouldBe(202);
        put.ResponseHeaders.ShouldContainKey("Azure-AsyncOperation");

        await sp.GetRequiredService<ComputeExecutor>().RunAsync(Key("w1"), "provision", new JsonObject(), Ct);

        var get = await Harness.Send(sp, "GET", Harness.WorkspacePath("w1"));
        get.StatusCode.ShouldBe(200);
        var json = System.Text.Json.JsonSerializer.SerializeToNode(get.Result, Burbuja.Core.Abstractions.Json.JsonWire.Options)!;
        json["properties"]!["state"]!.GetValue<string>().ShouldBe("running");
        json["properties"]!["generation"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task Replaying_the_same_spec_returns_the_same_operation()
    {
        var harness = Harness.CreateHost();
        using var sp = harness.Services;
        await Harness.SeedAsync(sp);

        var first = await Harness.Send(sp, "PUT", Harness.WorkspacePath("w2"), Harness.Spec(), clientRequestId: "req-1");
        var second = await Harness.Send(sp, "PUT", Harness.WorkspacePath("w2"), Harness.Spec(), clientRequestId: "req-2");
        first.StatusCode.ShouldBe(202);
        second.StatusCode.ShouldBe(202);
        first.OperationId.ShouldBe(second.OperationId);

        var changed = await Harness.Send(sp, "PUT", Harness.WorkspacePath("w2"), Harness.Spec(size: "medium"), clientRequestId: "req-3");
        changed.StatusCode.ShouldBe(409);
    }

    private static string Key(string name) =>
        $"/subscriptions/sub1/resourceGroups/rg1/providers/{ComputeConstants.Namespace}/workspaces/{name}".ToLowerInvariant();
}

public sealed class ExecTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Exec_on_a_non_running_workspace_is_409()
    {
        var harness = Harness.CreateHost();
        using var sp = harness.Services;
        await Harness.SeedAsync(sp);
        await Harness.Send(sp, "PUT", Harness.WorkspacePath("w"), Harness.Spec());

        var exec = await Harness.Send(sp, "POST", Harness.WorkspacePath("w") + "/exec", """{"command":"echo hello"}""");
        exec.StatusCode.ShouldBe(409);
        harness.Processes.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Hard_deny_never_spawns_a_process()
    {
        var harness = Harness.CreateHost();
        using var sp = harness.Services;
        await RunWorkspace(sp, "w");

        var exec = await Harness.Send(sp, "POST", Harness.WorkspacePath("w") + "/exec", """{"command":"rm -rf /"}""");
        exec.StatusCode.ShouldBe(403);
        exec.Error!.Code.ShouldBe("PolicyDenied");
        harness.Processes.Calls.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Allow_runs_the_command_and_filters_the_environment()
    {
        var harness = Harness.CreateHost();
        using var sp = harness.Services;
        await RunWorkspace(sp, "w", extra: ""","envAllowlist":["LANG"]""");

        var exec = await Harness.Send(sp, "POST", Harness.WorkspacePath("w") + "/exec",
            """{"command":"echo hello","env":{"LANG":"C","BURBUJA_TOKEN":"secret"}}""");
        exec.StatusCode.ShouldBe(200);
        harness.Processes.Calls.Count.ShouldBe(1);
        harness.Processes.Calls[0].Environment.ShouldContainKey("LANG");
        harness.Processes.Calls[0].Environment.ShouldNotContainKey("BURBUJA_TOKEN");
    }

    [Fact]
    public async Task Policy_intent_is_written_before_the_decision()
    {
        var harness = Harness.CreateHost();
        using var sp = harness.Services;
        await RunWorkspace(sp, "w");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        await Harness.Send(sp, "POST", Harness.WorkspacePath("w") + "/exec", """{"command":"rm -rf /"}""");

        var log = sp.GetRequiredService<IEventLog>();
        var streamId = ComputeConstants.StreamId(Key("w"));
        var seen = new List<LedgerEvent>();
        await foreach (var e in log.TailAsync(streamId, 0, cts.Token))
        {
            seen.Add(e);
            if (e.Type == ComputeEvents.PolicyDecision)
                break;
        }

        var intent = seen.First(e => e.Type == ComputeEvents.PolicyIntent);
        var decision = seen.First(e => e.Type == ComputeEvents.PolicyDecision);
        intent.Seq.ShouldBeLessThan(decision.Seq);
    }

    private static async Task RunWorkspace(IServiceProvider sp, string name, string extra = "")
    {
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", Harness.WorkspacePath(name), Harness.Spec(extra: extra))).StatusCode.ShouldBe(202);
        await sp.GetRequiredService<ComputeExecutor>().RunAsync(Key(name), "provision", new JsonObject(), Ct);
        var get = await Harness.Send(sp, "GET", Harness.WorkspacePath(name));
        var json = System.Text.Json.JsonSerializer.SerializeToNode(get.Result, Burbuja.Core.Abstractions.Json.JsonWire.Options)!;
        json["properties"]!["state"]!.GetValue<string>().ShouldBe("running");
    }

    private static string Key(string name) =>
        $"/subscriptions/sub1/resourceGroups/rg1/providers/{ComputeConstants.Namespace}/workspaces/{name}".ToLowerInvariant();
}

public sealed class BackendAndDescriptorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Provider_exposes_five_resource_types_and_no_action_segment_collision()
    {
        var harness = Harness.CreateHost();
        using var sp = harness.Services;
        var provider = new ComputeResourceProvider(sp.GetRequiredService<BackendRegistry>());
        provider.ResourceTypes.Count.ShouldBe(5);

        var workspaces = provider.ResourceTypes.First(t => t.Type == "workspaces");
        var actions = workspaces.Actions.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var route in workspaces.DataPlaneRoutes)
            actions.ShouldNotContain(route.Segment);
        actions.ShouldContain("exec");
    }

    [Fact]
    public void Compute_assembly_does_not_reference_core_or_agents()
    {
        foreach (var name in typeof(ComputePlugin).Assembly.GetReferencedAssemblies())
        {
            name.Name.ShouldNotBeNull();
            name.Name.ShouldNotBe("Burbuja.Core");
            name.Name.ShouldNotBe("Burbuja.Agents");
            name.Name.StartsWith("Burbuja.Agents", StringComparison.Ordinal).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Provisioning_confines_the_workspace_to_the_configured_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "compute-escape-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var harness = Harness.CreateHost(root);
        using var sp = harness.Services;
        var backend = sp.GetRequiredService<LocalDirectoryBackend>();

        var result = await backend.ProvisionAsync(
            new ComputeProvisionRequest("../../../../etc/evil", "", new ComputeWorkspaceProperties()),
            Ct);
        result.RootPath.ShouldStartWith(Path.GetFullPath(root));
    }

    [Fact]
    public async Task Exec_refuses_a_cwd_that_escapes_the_root_without_spawning()
    {
        var harness = Harness.CreateHost();
        using var sp = harness.Services;
        var backend = sp.GetRequiredService<LocalDirectoryBackend>();
        var root = Path.Combine(Path.GetTempPath(), "compute-cwd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var provision = await backend.ProvisionAsync(new ComputeProvisionRequest("w", root, new ComputeWorkspaceProperties()), Ct);

        var grant = sp.GetRequiredService<Burbuja.Core.Abstractions.Process.IExecutionGrantAuthority>().Mint("w", "compute:exec", TimeSpan.FromMinutes(1));
        var result = await backend.ExecAsync(new ComputeExecRequest(provision.RootPath, "echo hi", "../../..", new Dictionary<string, string>(), [], TimeSpan.FromSeconds(5), 4096), grant, Ct);

        result.ExitCode.ShouldBe(-1);
        result.Stderr.ShouldBe("CwdEscape");
        harness.Processes.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Snapshot_round_trips_into_the_artifact_store()
    {
        var harness = Harness.CreateHost();
        using var sp = harness.Services;
        var backend = sp.GetRequiredService<LocalDirectoryBackend>();
        var root = Path.Combine(Path.GetTempPath(), "compute-snap-" + Guid.NewGuid().ToString("N"));
        var provision = await backend.ProvisionAsync(new ComputeProvisionRequest("w", root, new ComputeWorkspaceProperties()), Ct);
        await File.WriteAllTextAsync(Path.Combine(provision.RootPath, "hello.txt"), "hello", Ct);

        var snapshot = await backend.SnapshotAsync(provision.RootPath, "snap1", Ct);
        snapshot.SizeBytes.ShouldBeGreaterThan(0);
        snapshot.Sha256.Length.ShouldBe(64);
        (await sp.GetRequiredService<Burbuja.Core.Abstractions.Artifacts.IArtifactStore>().ExistsAsync(snapshot.ArtifactKey, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Destroy_removes_only_the_workspace_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "compute-destroy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var harness = Harness.CreateHost(root);
        using var sp = harness.Services;
        var backend = sp.GetRequiredService<LocalDirectoryBackend>();
        var provision = await backend.ProvisionAsync(new ComputeProvisionRequest("w1", "", new ComputeWorkspaceProperties()), Ct);
        var sentinel = Path.Combine(root, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "keep", Ct);

        await backend.DestroyAsync(provision.RootPath, Ct);

        Directory.Exists(provision.RootPath).ShouldBeFalse();
        File.Exists(sentinel).ShouldBeTrue();
    }
}
