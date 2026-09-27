using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Abstractions.Outbox;
using Burbuja.Deployments.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Burbuja.Deployments.UnitTests;

public sealed class HandlerTests
{
    [Fact]
    public async Task Put_returns_202_with_one_operation_and_one_enqueue()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        var put = await Harness.Send(sp, "PUT", Harness.DepPath, Harness.Template("web"));
        put.StatusCode.ShouldBe(202);
        put.ResponseHeaders.ShouldContainKey("Azure-AsyncOperation");
        var ops = await sp.GetRequiredService<IOperationTracker>().ListAsync("sub1", "Burbuja.Deployments", TestContext.Current.CancellationToken);
        ops.Count.ShouldBe(1);
        var claimed = await sp.GetRequiredService<IOutboxStore>().ClaimAsync(32, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        claimed.Count(r => r.Message.Topic == DeploymentsConstants.TopicExecute).ShouldBe(1);
    }

    [Fact]
    public async Task Replay_same_hash_returns_same_operation()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        var first = await Harness.Send(sp, "PUT", Harness.DepPath, Harness.Template("web"));
        first.Ok();
        var op = first.AsJson()["properties"]!["operationId"]!.GetValue<string>();
        var second = await Harness.Send(sp, "PUT", Harness.DepPath, Harness.Template("web"));
        second.StatusCode.ShouldBe(202);
        second.AsJson()["properties"]!["operationId"]!.GetValue<string>().ShouldBe(op);
        var ops = await sp.GetRequiredService<IOperationTracker>().ListAsync("sub1", "Burbuja.Deployments", TestContext.Current.CancellationToken);
        ops.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Different_hash_while_running_is_409()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", Harness.DepPath, Harness.Template("web"))).Ok();
        var conflict = await Harness.Send(sp, "PUT", Harness.DepPath, Harness.Template("other"));
        conflict.StatusCode.ShouldBe(409);
        conflict.Error!.Code.ShouldBe("OperationInProgress");
    }

    [Fact]
    public async Task Get_body_re_put_is_idempotent()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", Harness.DepPath, Harness.Template("web"))).Ok();
        var get = await Harness.Send(sp, "GET", Harness.DepPath);
        get.Ok();
        var replay = await Harness.Send(sp, "PUT", Harness.DepPath, get.AsJson().ToJsonString());
        replay.StatusCode.ShouldBe(202);
        var ops = await sp.GetRequiredService<IOperationTracker>().ListAsync("sub1", "Burbuja.Deployments", TestContext.Current.CancellationToken);
        ops.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Missing_environment_is_404()
    {
        using var sp = Harness.CreateHost();
        (await Harness.Send(sp, "PUT", "/subscriptions/sub1", """{"properties":{"displayName":"s","state":"Enabled"}}""")).Ok();
        (await Harness.Send(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", """{"properties":{}}""")).Ok();
        var put = await Harness.Send(sp, "PUT", Harness.DepPath, Harness.Template("web"));
        put.StatusCode.ShouldBe(404);
    }

    [Fact]
    public async Task Unknown_target_is_400()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        var store = sp.GetRequiredService<IResourceStore>();
        var env = await store.GetAsync(Harness.EnvPath.ToLowerInvariant(), TestContext.Current.CancellationToken);
        env!.Properties["target"] = new JsonObject { ["kind"] = "k8s" };
        await store.UpsertAsync(env, env.ETag, TestContext.Current.CancellationToken);
        var put = await Harness.Send(sp, "PUT", Harness.DepPath, Harness.Template("web"));
        put.StatusCode.ShouldBe(400);
        put.Error!.Message.ShouldContain("UnsupportedTarget");
    }

    [Fact]
    public async Task WhatIf_and_validate_do_not_mutate()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        var store = sp.GetRequiredService<IResourceStore>();
        var before = await store.GetAsync(Harness.EnvPath.ToLowerInvariant(), TestContext.Current.CancellationToken);
        var validate = await Harness.Send(sp, "POST", Harness.DepPath + "/validate", Harness.Template("web"));
        validate.Ok();
        validate.AsJson()["valid"]!.GetValue<bool>().ShouldBeTrue();
        var whatIf = await Harness.Send(sp, "POST", Harness.DepPath + "/whatIf", Harness.Template("web"));
        whatIf.Ok();
        whatIf.AsJson()["changes"]!.AsArray().Count.ShouldBeGreaterThan(0);
        var after = await store.GetAsync(Harness.EnvPath.ToLowerInvariant(), TestContext.Current.CancellationToken);
        after!.ETag.ShouldBe(before!.ETag);
        (await store.GetAsync(Harness.DepPath.ToLowerInvariant(), TestContext.Current.CancellationToken)).ShouldBeNull();
        (await sp.GetRequiredService<IOperationTracker>().ListAsync("sub1", "Burbuja.Deployments", TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancel_on_terminal_is_409()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", Harness.DepPath, Harness.Template("web"))).Ok();
        await Harness.RunDeploymentAsync(sp, Harness.DepPath, TestContext.Current.CancellationToken);
        var cancel = await Harness.Send(sp, "POST", Harness.DepPath + "/cancel");
        cancel.StatusCode.ShouldBe(409);
    }

    [Fact]
    public async Task Async_delete_destroys()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", Harness.DepPath, Harness.Template("web"))).Ok();
        await Harness.RunDeploymentAsync(sp, Harness.DepPath, TestContext.Current.CancellationToken);
        var del = await Harness.Send(sp, "DELETE", Harness.DepPath);
        del.StatusCode.ShouldBe(202);
        await Harness.RunDeploymentAsync(sp, Harness.DepPath, TestContext.Current.CancellationToken);
        var get = await Harness.Send(sp, "GET", Harness.DepPath);
        get.Ok();
        var steps = get.AsJson()["properties"]!["steps"]!.AsArray();
        steps.ShouldContain(n => n!["change"]!.GetValue<string>() == "delete");
        steps.ShouldContain(n => n!["status"]!.GetValue<string>() == "deleted");
    }

    [Fact]
    public async Task Probe_returns_ready()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        var probe = await Harness.Send(sp, "POST", Harness.EnvPath + "/probe");
        probe.Ok();
        probe.AsJson()["ready"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task Targets_are_subscription_less()
    {
        using var sp = Harness.CreateHost();
        var list = await Harness.Send(sp, "GET", "/providers/Burbuja.Deployments/targets");
        list.Ok();
        var names = list.AsJson()["value"]!.AsArray().Select(n => n!["name"]!.GetValue<string>()).ToArray();
        names.ShouldContain("in-memory");
        names.ShouldContain("local-process");
        var get = await Harness.Send(sp, "GET", "/providers/Burbuja.Deployments/targets/in-memory");
        get.Ok();
        get.AsJson()["properties"]!["kind"]!.GetValue<string>().ShouldBe("in-memory");
    }
}
