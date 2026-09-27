using System.Diagnostics;
using System.Text.Json.Nodes;
using Burbuja.Compute;
using Burbuja.Compute.Abstractions;
using Burbuja.Compute.Backends;
using Burbuja.Compute.Runtime;
using Burbuja.Core.Abstractions.Arm;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Burbuja.Compute.UnitTests;

public sealed class GitBackendTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Git_worktree_provision_shows_in_worktree_list_and_destroy_removes_it()
    {
        var harness = Harness.CreateHost(spyProcesses: false);
        using var sp = harness.Services;
        var backend = sp.GetRequiredService<GitWorktreeBackend>();
        var key = "/subscriptions/s/resourceGroups/rg/providers/Burbuja.Compute/workspaces/g1";

        var result = await backend.ProvisionAsync(
            new ComputeProvisionRequest(key, "", new ComputeWorkspaceProperties { Backend = "git-worktree" }),
            Ct);

        Directory.Exists(result.RootPath).ShouldBeTrue();
        result.Branch.ShouldBe("compute/g1");
        result.BaseSha.ShouldNotBeNullOrWhiteSpace();

        var repo = Path.Combine(Path.GetDirectoryName(result.RootPath)!, "repo");
        Git(repo, "worktree", "list").ShouldContain(result.RootPath);

        await backend.DestroyAsync(result.RootPath, Ct);
        Directory.Exists(result.RootPath).ShouldBeFalse();
        Directory.Exists(Path.GetDirectoryName(result.RootPath)!).ShouldBeFalse();
    }

    [Fact]
    public async Task Git_remote_outside_the_allowlist_is_rejected()
    {
        var harness = Harness.CreateHost(spyProcesses: false);
        using var sp = harness.Services;
        var backend = sp.GetRequiredService<GitWorktreeBackend>();
        var spec = new ComputeWorkspaceProperties
        {
            Backend = "git-worktree",
            Repo = new ComputeRepoBinding { Remote = "https://evil.example/x.git" },
        };

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await backend.ProvisionAsync(new ComputeProvisionRequest("w", "", spec), Ct));
    }

    [Fact]
    public async Task Snapshot_action_creates_a_child_resource()
    {
        var harness = Harness.CreateHost(spyProcesses: false);
        using var sp = harness.Services;
        await Harness.SeedAsync(sp);
        var path = Harness.WorkspacePath("g2");
        (await Harness.Send(sp, "PUT", path, Harness.Spec(backend: "git-worktree", image: "git"))).StatusCode.ShouldBe(202);
        await sp.GetRequiredService<ComputeExecutor>().RunAsync(Key("g2"), "provision", new JsonObject(), Ct);

        var snapshot = await Harness.Send(sp, "POST", path + "/snapshot", """{"name":"snap1"}""");
        snapshot.StatusCode.ShouldBe(202);
        await sp.GetRequiredService<ComputeExecutor>().RunAsync(Key("g2"), "snapshot", new JsonObject { ["name"] = "snap1" }, Ct);

        var get = await Harness.Send(sp, "GET", path + "/snapshots/snap1");
        get.StatusCode.ShouldBe(200);
        var json = System.Text.Json.JsonSerializer.SerializeToNode(get.Result, Burbuja.Core.Abstractions.Json.JsonWire.Options)!;
        json["properties"]!["sizeBytes"]!.GetValue<long>().ShouldBeGreaterThan(0);
        json["properties"]!["sha256"]!.GetValue<string>().Length.ShouldBe(64);
    }

    [Fact]
    public async Task Workspace_access_attaches_to_a_running_workspace()
    {
        var harness = Harness.CreateHost(spyProcesses: false);
        using var sp = harness.Services;
        await Harness.SeedAsync(sp);
        var path = Harness.WorkspacePath("g3");
        (await Harness.Send(sp, "PUT", path, Harness.Spec(backend: "git-worktree", image: "git"))).StatusCode.ShouldBe(202);
        await sp.GetRequiredService<ComputeExecutor>().RunAsync(Key("g3"), "provision", new JsonObject(), Ct);

        var access = sp.GetRequiredService<IComputeWorkspaceAccess>();
        var attached = await access.AttachAsync(Key("g3"), "agents-bridge", Ct);
        Directory.Exists(attached.RootPath).ShouldBeTrue();
        attached.Branch.ShouldBe("compute/g3");

        await access.TouchAsync(Key("g3"), Ct);
        var env = await sp.GetRequiredService<ComputeStateStore>().GetAsync(Key("g3"), Ct);
        env!.Properties.LastActivityAt.ShouldNotBeNull();
    }

    private static string Key(string name) =>
        $"/subscriptions/sub1/resourceGroups/rg1/providers/{ComputeConstants.Namespace}/workspaces/{name}".ToLowerInvariant();

    private static string Git(string cwd, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }
}
