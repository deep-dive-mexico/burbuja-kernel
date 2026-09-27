using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Agents;
using Burbuja.Agents.Runtime;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Burbuja.Agents.UnitTests;

public sealed class StatefulAgentResourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string AgentPath =
        "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Agents/agents/a1";

    [Fact]
    public async Task Agent_put_is_synchronous_and_creates_no_session()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);

        var put = await Harness.Send(sp, "PUT", AgentPath, """{"properties":{"displayName":"a1"}}""");
        put.StatusCode.ShouldBeOneOf(200, 201);

        var get = await Harness.Send(sp, "GET", AgentPath);
        get.StatusCode.ShouldBe(200);
        var json = Node(get);
        json!["properties"]!["state"]!.GetValue<string>().ShouldBe("idle");
        json["properties"]!["modelId"]!.GetValue<string>().ShouldBe(AgentsConstants.DefaultAgentModelId);

        var sessions = await Harness.Send(sp, "GET", AgentPath + "/sessions");
        sessions.StatusCode.ShouldBe(200);
        Node(sessions)!["value"]!.AsArray().Count.ShouldBe(0);
    }

    [Fact]
    public async Task Send_returns_202_and_appends_to_the_durable_inbox()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", AgentPath, """{"properties":{"displayName":"a1"}}""")).Ok();

        var send = await Harness.Send(sp, "POST", AgentPath + "/send", """{"mode":"followup","text":"do it"}""");
        send.StatusCode.ShouldBe(202);
        send.ResponseHeaders.ShouldContainKey("Azure-AsyncOperation");

        var inbox = Node(await Harness.Send(sp, "GET", AgentPath))!["properties"]!["inbox"]!.AsArray();
        inbox.Count.ShouldBe(1);
        inbox[0]!["mode"]!.GetValue<string>().ShouldBe("followup");
        inbox[0]!["text"]!.GetValue<string>().ShouldBe("do it");
    }

    [Fact]
    public async Task Dedup_key_is_held_by_the_target()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", AgentPath, """{"properties":{"displayName":"a1"}}""")).Ok();

        var first = await Harness.Send(sp, "POST", AgentPath + "/send", """{"mode":"followup","text":"one","dedupKey":"k1"}""");
        first.StatusCode.ShouldBe(202);

        var second = await Harness.Send(sp, "POST", AgentPath + "/send", """{"mode":"followup","text":"one","dedupKey":"k1"}""");
        second.StatusCode.ShouldBe(200);
        Node(second)!["accepted"]!.GetValue<bool>().ShouldBeFalse();

        Node(await Harness.Send(sp, "GET", AgentPath))!["properties"]!["inbox"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Session_put_is_rejected()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", AgentPath, """{"properties":{"displayName":"a1"}}""")).Ok();

        var put = await Harness.Send(sp, "PUT", AgentPath + "/sessions/s1", """{"properties":{"agentId":"a1"}}""");
        put.StatusCode.ShouldBe(405);
    }

    [Fact]
    public async Task Context_card_over_the_bound_is_rejected()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", AgentPath, """{"properties":{"displayName":"a1"}}""")).Ok();

        var big = new string('x', 9000);
        var put = await Harness.Send(sp, "PUT", AgentPath + "/contexts/c1",
            "{\"properties\":{\"label\":\"handoff\",\"body\":\"" + big + "\"}}");
        put.StatusCode.ShouldBe(400);

        var ok = await Harness.Send(sp, "PUT", AgentPath + "/contexts/c1",
            """{"properties":{"label":"handoff","body":"small"}}""");
        ok.StatusCode.ShouldBeOneOf(200, 201);
    }

    [Fact]
    public async Task Only_one_activation_per_agent_is_admitted()
    {
        using var sp = Harness.CreateHost();
        var activation = sp.GetRequiredService<AgentActivationManager>();

        var first = await activation.AdmitAsync("agt-1", "owner-a", TimeSpan.FromMinutes(1), Ct);
        var second = await activation.AdmitAsync("agt-1", "owner-b", TimeSpan.FromMinutes(1), Ct);

        first.ShouldNotBeNull();
        second.ShouldBeNull();
    }

    [Fact]
    public void Depth_budget_denies_a_fourth_level()
    {
        AgentActivationManager.CanSpawn(0, 0).ShouldBeTrue();
        AgentActivationManager.CanSpawn(2, 0).ShouldBeTrue();
        AgentActivationManager.CanSpawn(3, 0).ShouldBeFalse();
        AgentActivationManager.CanSpawn(0, AgentActivationManager.MaxOwnedChildren).ShouldBeFalse();
    }

    private static JsonNode? Node(ArmRequestContext ctx) =>
        JsonSerializer.SerializeToNode(ctx.Result, JsonWire.Options);
}
