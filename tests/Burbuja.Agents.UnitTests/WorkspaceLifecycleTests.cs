using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Handlers;
using Burbuja.Agents.Model;
using Burbuja.Agents.Runtime;
using Burbuja.Compute;
using Burbuja.Compute.Runtime;
using Burbuja.Core.Abstractions.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Burbuja.Agents.UnitTests;

/// <summary>
/// W1 — the workspace lifecycle: a workspace PUT that asks for provisioning answers 202 and an operation, and
/// the initializer binds the workspace's <c>Burbuja.Compute</c> workspace and closes that operation.
///
/// <para><b>What the facts are about.</b> Two of them are the plan's own acceptance: the PUT must stay an
/// ordinary 200/201 write when there is nothing to provision (every existing caller depends on it), and a
/// re-initialize must reconcile rather than restart the world — which is why the second fact asserts the
/// GENERATION is unchanged rather than that a second operation succeeded. The third is the binding itself: a
/// compute workspace that names the agents workspace, which is the M8 seam nothing had written.</para>
/// </summary>
public sealed class WorkspaceLifecycleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Ws = Harness.WsPath;
    private const string ComputeWs = "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Compute/workspaces/demo";

    /// <summary>
    /// M10 Q1/U7 — the ROLE the initialize runs as, and the compute binding it writes, are named and stable.
    ///
    /// <para><b>Why this is its own fact.</b> The compose lane had no <c>Agents__LifecycleKey</c> until Q1, so
    /// the initializer was proven in the unit harness and in no container: an initialize there would have
    /// failed its operation on a host that looked healthy. This asserts the two things the lane's environment
    /// and the page's form both depend on — the setting is what the initializer reads, and the binding it
    /// writes names THIS workspace, which is the two-way link M8 named and nothing wrote.</para>
    /// </summary>
    [Fact]
    public async Task The_initialize_runs_as_the_configured_principal_and_binds_this_workspace()
    {
        // The COMPUTE plugin, booted for real: the claim is an ARM call to another provider, and a host without
        // it answers `'workspaces' is not a type, action, or data-plane segment of 'Burbuja.Compute/'` — which is
        // exactly what this fact reported before the plugin was added, on a harness that had never needed it.
        using var sp = Harness.CreateHost(plugins: [new ComputePlugin()]);
        await Harness.SeedAsync(sp);

        var workspace = await Harness.Send(sp, "PUT", Harness.WsPath, """
        {"properties":{"displayName":"demo","repoId":"demo","profile":"DevFullAuto","initialize":true}}
        """);
        workspace.Ok();

        // Driven directly, the way the W1 facts do it: the initializer is a hosted worker on the lifecycle
        // topic, and a polling read does not pump it — the first version of this fact waited twenty seconds for
        // a lifecycle that only an explicit RunAsync produces.
        await sp.GetRequiredService<WorkspaceInitializer>().RunAsync(Harness.WsPath.ToLowerInvariant(), Ct);

        var read = await Harness.Send(sp, "GET", Harness.WsPath);
        var properties = read.AsJson()["properties"] as JsonObject;
        properties!["lifecycle"]!.GetValue<string>().ShouldBe("Ready",
            "the initializer has to have run: " + properties.ToJsonString());
        var computeId = properties["computeWorkspaceId"]!.GetValue<string>();
        computeId.ShouldNotBeNullOrWhiteSpace("the binding is what the initialize exists to write");

        // And the binding points BACK at this workspace — the reverse half M8 named.
        var compute = await Harness.Send(sp, "GET", computeId);
        compute.Ok();
        compute.AsJson()["properties"]!["repo"]!["agentsWorkspaceId"]!.GetValue<string>()
            .ShouldBe(Harness.WsPath,
                "a compute workspace bound to nothing is a workspace nobody can trace back");
    }

    [Fact]
    public async Task A_workspace_put_that_asks_for_nothing_stays_an_ordinary_write()
    {
        using var sp = Harness.CreateHost(plugins: [new ComputePlugin()]);
        await Harness.SeedAsync(sp);

        // Harness.SeedAsync already PUT the workspace; a second PUT with no `initialize` is still a write.
        var put = await Harness.Send(sp, "PUT", Ws, """{"properties":{"displayName":"demo","repoId":"demo"}}""");
        put.StatusCode.ShouldBe(200, "a PUT that provisions nothing answers now — pomnibot's client and the E2E suites depend on it");
        put.AsJson()["properties"]!["lifecycle"].ShouldBeNull();
    }

    [Fact]
    public async Task An_initialize_answers_202_binds_compute_and_is_idempotent()
    {
        using var sp = Harness.CreateHost(plugins: [new ComputePlugin()]);
        await Harness.SeedAsync(sp);

        var put = await Harness.Send(
            sp, "PUT", Ws,
            """{"properties":{"displayName":"demo","repoId":"demo","initialize":true}}""");
        put.StatusCode.ShouldBe(202, "provisioning is an operation, not a write");
        put.ResponseHeaders.ShouldContainKey("Azure-AsyncOperation");
        var operationId = put.AsJson()["properties"]!["operationId"]!.GetValue<string>();

        var accepted = put.AsJson()["properties"]!;
        accepted["lifecycle"]!.GetValue<string>().ShouldBe(WorkspaceHandler.Lifecycle.Provisioning);
        accepted["computeWorkspaceId"]!.GetValue<string>().ShouldBe(
            ComputeWs, "the default binding is the same name in the same resource group");
        accepted["generation"]!.GetValue<int>().ShouldBe(1);

        // The compute workspace is created through ARM, so its own worker provisions it — exactly as a caller
        // would. Both workers run here, which is the whole point: neither is stubbed.
        await sp.GetRequiredService<WorkspaceInitializer>().RunAsync(Ws.ToLowerInvariant(), Ct);
        await sp.GetRequiredService<ComputeExecutor>()
            .RunAsync(ComputeWs.ToLowerInvariant(), "provision", new JsonObject(), Ct);

        var read = await Harness.Send(sp, "GET", Ws);
        var properties = read.AsJson()["properties"]!;
        properties["lifecycle"]!.GetValue<string>().ShouldBe(WorkspaceHandler.Lifecycle.Ready);
        properties["generation"]!.GetValue<int>().ShouldBe(1);

        var compute = await Harness.Send(sp, "GET", ComputeWs);
        compute.StatusCode.ShouldBe(200, "the binding created a real compute workspace");
        var computeJson = System.Text.Json.JsonSerializer.SerializeToNode(compute.Result, JsonWire.Options)!;
        computeJson["properties"]!["state"]!.GetValue<string>().ShouldBe("running");
        computeJson["properties"]!["repo"]!["agentsWorkspaceId"]!.GetValue<string>()
            .ShouldBe(Ws, "the two resources are bound to each other: this is the M8 seam made real");

        // The operation the PUT opened reached a terminal state, so a caller polling it is not left waiting.
        var operation = await Harness.Send(
            sp, "GET", $"/subscriptions/sub1/providers/Burbuja.Resources/operations/{operationId}");
        operation.StatusCode.ShouldBe(200);
        operation.AsJson()["properties"]!["status"]!.GetValue<string>().ShouldBe("Succeeded");

        // Re-initialize reconciles: nothing is provisioned again and the generation does not move.
        var again = await Harness.Send(sp, "POST", $"{Ws}/initialize", "{}");
        again.StatusCode.ShouldBe(200);
        var reconciled = again.AsJson();
        reconciled["reconciled"]!.GetValue<bool>().ShouldBeFalse();
        reconciled["generation"]!.GetValue<int>().ShouldBe(1, "a second initialize of an unchanged workspace changes nothing");

        (await Harness.Send(sp, "GET", Ws)).AsJson()["properties"]!["generation"]!.GetValue<int>().ShouldBe(1);
    }

    /// <summary>
    /// A caller that names a compute workspace this host does not have is asking for it to be provisioned —
    /// that is the second trigger, and the body does not need `initialize` for it.
    /// </summary>
    [Fact]
    public async Task A_missing_named_compute_binding_is_itself_a_request_to_provision()
    {
        using var sp = Harness.CreateHost(plugins: [new ComputePlugin()]);
        await Harness.SeedAsync(sp);

        var named = "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Compute/workspaces/named-elsewhere";
        var body = new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["displayName"] = "demo",
                ["repoId"] = "demo",
                ["computeWorkspaceId"] = named,
            },
        }.ToJsonString();
        var put = await Harness.Send(sp, "PUT", Ws, body);
        put.StatusCode.ShouldBe(202);
        put.AsJson()["properties"]!["computeWorkspaceId"]!.GetValue<string>().ShouldBe(named);

        await sp.GetRequiredService<WorkspaceInitializer>().RunAsync(Ws.ToLowerInvariant(), Ct);

        var read = await Harness.Send(sp, "GET", Ws);
        read.AsJson()["properties"]!["lifecycle"]!.GetValue<string>().ShouldBe(WorkspaceHandler.Lifecycle.Ready);
        (await Harness.Send(sp, "GET", named)).StatusCode.ShouldBe(200);
    }

    /// <summary>
    /// A host with no lifecycle key cannot provision, and says so ON THE RESOURCE AND THE OPERATION rather than
    /// failing from inside the pipeline — a configuration gap reported as a product fault is the shape this
    /// repository keeps removing.
    /// </summary>
    [Fact]
    public async Task A_host_with_no_lifecycle_key_fails_the_operation_with_the_reason()
    {
        using var sp = Harness.CreateHost(
            settings: new Dictionary<string, string?> { ["Agents:LifecycleKey"] = "" },
            plugins: [new ComputePlugin()]);
        await Harness.SeedAsync(sp);

        var put = await Harness.Send(
            sp, "PUT", Ws, """{"properties":{"displayName":"demo","repoId":"demo","initialize":true}}""");
        var operationId = put.AsJson()["properties"]!["operationId"]!.GetValue<string>();

        await sp.GetRequiredService<WorkspaceInitializer>().RunAsync(Ws.ToLowerInvariant(), Ct);

        var read = await Harness.Send(sp, "GET", Ws);
        var properties = read.AsJson()["properties"]!;
        properties["lifecycle"]!.GetValue<string>().ShouldBe(WorkspaceHandler.Lifecycle.Failed);
        properties["lifecycleError"]!.GetValue<string>().ShouldContain(WorkspaceInitializer.LifecycleKeySetting);

        var operation = await Harness.Send(
            sp, "GET", $"/subscriptions/sub1/providers/Burbuja.Resources/operations/{operationId}");
        operation.AsJson()["properties"]!["status"]!.GetValue<string>().ShouldBe("Failed");
    }

    /// <summary>
    /// M13 R0 — ONE FAILING MESSAGE DOES NOT STOP THE PROVISIONER, and this is the fact for a red browser lane.
    ///
    /// <para><b>What was measured.</b> `2026-09-16`, the browser lane: a single
    /// <c>ArmException: The provided ETag does not match the current resource.</c> escaped
    /// <c>WorkspaceInitializer.OnMessage</c>, and <c>InMemoryMessageBus</c> did what its own comment says it
    /// does — logged <c>bus subscriber for topic 'agents.workspace.lifecycle' failed and stopped</c> and ENDED
    /// the pump. Every workspace provisioned after that stayed <c>Provisioning</c> forever with no error on the
    /// resource and no second message, and two browser facts went red (`G25` 90 s on `Provisioning`, `G26`
    /// waiting for a button that needs the list to load) in a way that reads exactly like a slow host.</para>
    ///
    /// <para><b>The fact drives the path that broke.</b> A host with NO compute plugin cannot bind, so the
    /// initialize fails — and the point is what happens NEXT: the failure is caught, written to the resource,
    /// and a SECOND workspace still provisions. The old code threw out of <c>OnMessage</c> on the first one;
    /// here the second is the assertion.</para>
    /// </summary>
    [Fact]
    public async Task A_failing_initialize_does_not_stop_the_provisioner()
    {
        // No ComputePlugin: the initializer's own ARM call to Burbuja.Compute fails, which is a real failure
        // inside the try, not a synthetic throw.
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);

        (await Harness.Send(
            sp, "PUT", Ws, """{"properties":{"displayName":"demo","repoId":"demo","initialize":true}}"""))
            .StatusCode.ShouldBe(202);

        var before = (await Harness.Send(sp, "GET", Ws)).AsJson()["properties"]!;
        var initializer = sp.GetRequiredService<WorkspaceInitializer>();

        // The subscribER's own entry point, so the catch this fact is about is the one under test.
        await initializer.OnMessage(
            new Burbuja.Core.Abstractions.Bus.BusMessage(Guid.NewGuid(), AgentsConstants.TopicWorkspaceLifecycle,
                System.Text.Json.JsonSerializer.SerializeToNode(new JsonObject { ["workspaceKey"] = Ws.ToLowerInvariant() })!,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            Ct);

        var first = (await Harness.Send(sp, "GET", Ws)).AsJson()["properties"]!;
        first["lifecycle"]!.GetValue<string>().ShouldBe(WorkspaceHandler.Lifecycle.Failed,
            "the failure is still REPORTED — catching it is not swallowing it: " + first.ToJsonString());

        // The second workspace is the assertion. With a dead subscriber this never leaves Provisioning.
        const string second = "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Agents/workspaces/second";
        (await Harness.Send(
            sp, "PUT", second, """{"properties":{"displayName":"second","repoId":"demo","initialize":true}}"""))
            .StatusCode.ShouldBe(202);

        await initializer.OnMessage(
            new Burbuja.Core.Abstractions.Bus.BusMessage(Guid.NewGuid(), AgentsConstants.TopicWorkspaceLifecycle,
                System.Text.Json.JsonSerializer.SerializeToNode(new JsonObject { ["workspaceKey"] = second })!,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            Ct);

        var next = (await Harness.Send(sp, "GET", second)).AsJson()["properties"]!;
        next["lifecycle"]!.GetValue<string>().ShouldBe(WorkspaceHandler.Lifecycle.Failed,
            "the second message is HANDLED — the subscriber is still there: " + next.ToJsonString());
    }

    /// <summary>
    /// M13 R0 — a write that lands while the initialize is between its read and its write is a NON-EVENT: the
    /// workspace ends where the kernel says it is, the operation closes, and the subscriber is still there.
    ///
    /// <para><b>What it provokes.</b> The initializer reads the envelope, and a second caller writes the same
    /// resource — the smallest version of the 20-second <c>ComputeWait</c> window in which the ETag it started
    /// with goes stale. The front door answers an IDENTICAL body from the first request's idempotent-response
    /// cache, so the second body differs; that is also why the concurrent write here carries no
    /// <c>initialize</c>, which is what an ordinary PUT of the resource looks like.</para>
    ///
    /// <para><b>Why it ends where it does, stated rather than implied.</b> An ordinary PUT CLEARS
    /// <c>lifecycle</c> (the body describes the resource and asks for nothing to be provisioned), so the
    /// initialize's own <c>Lifecycle != Provisioning</c> guard stops it before it writes anything: there is no
    /// operation left to close and no state to claim. That is the right answer, and it is the reason this fact
    /// asserts the RESULT (nothing dangling, nothing half-written) rather than the path.</para>
    ///
    /// <para><b>What is NOT proven, stated:</b> the supersession branch inside <c>PersistAsync</c> — a message
    /// whose operation was replaced by a newer initialize, dropped rather than written. Driving it needs the
    /// late message to read the row before the newer PUT lands, and the in-process front door's response cache
    /// makes that unreachable from a fact (measured 2026-09-16: the second PUT's cache entry answered the
    /// message for the first operation with the second operation's id, so the message was always the CURRENT
    /// one). The branch is guarded and commented; it is not claimed. What IS claimed is the fix for the red
    /// lane: <c>OnMessage</c> catches (the fact above), and the write re-reads instead of trusting a stale
    /// <c>If-Match</c>.</para>
    /// </summary>
    [Fact]
    public async Task A_write_that_lands_between_the_read_and_the_write_leaves_nothing_dangling()
    {
        using var sp = Harness.CreateHost(plugins: [new ComputePlugin()]);
        await Harness.SeedAsync(sp);

        var initializer = sp.GetRequiredService<WorkspaceInitializer>();
        var store = sp.GetRequiredService<Burbuja.Core.Abstractions.Arm.IResourceStore>();

        var put = await Harness.Send(
            sp, "PUT", Ws, """{"properties":{"displayName":"demo","repoId":"demo","initialize":true}}""");
        put.StatusCode.ShouldBe(202);
        var operation = put.AsJson()["properties"]!["operationId"]!.GetValue<string>();

        var touched = await Harness.Send(
            sp, "PUT", Ws, """{"properties":{"displayName":"touched while binding","repoId":"demo"}}""");
        touched.StatusCode.ShouldBe(200);
        touched.AsJson()["properties"]!["lifecycle"].ShouldBeNull(
            "an ordinary PUT describes the resource, and this body asks for nothing to be provisioned");

        // The initialize runs anyway, the way a queued message does.
        await initializer.RunAsync(Ws.ToLowerInvariant(), Ct);

        var properties = System.Text.Json.JsonSerializer.Deserialize<WorkspaceProperties>(
            (await store.GetAsync(Ws.ToLowerInvariant(), Ct))!.Properties, JsonWire.Options)!;
        properties.Lifecycle.ShouldBeNull(
            "nothing is provisioned for a resource whose body no longer asks for it — and nothing is left half-written");
        properties.DisplayName.ShouldBe("touched while binding", "the concurrent write is the one that stands");

        // And the subscriber is still alive afterwards: an initialize on a fresh workspace still works. This is
        // the half that was dead on the browser lane.
        const string next = "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Agents/workspaces/next";
        (await Harness.Send(
            sp, "PUT", next, """{"properties":{"displayName":"next","repoId":"demo","initialize":true}}"""))
            .StatusCode.ShouldBe(202);
        await initializer.RunAsync(next.ToLowerInvariant(), Ct);

        var after = System.Text.Json.JsonSerializer.Deserialize<WorkspaceProperties>(
            (await store.GetAsync(next, Ct))!.Properties, JsonWire.Options)!;
        after.Lifecycle.ShouldBe(WorkspaceHandler.Lifecycle.Ready, "the next initialize still completes");
    }
}
