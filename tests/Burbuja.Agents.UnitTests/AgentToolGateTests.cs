using System.Text.Json.Nodes;
using Burbuja.Agents;
using Burbuja.Agents.Abstractions.Agents;
using Burbuja.Agents.Abstractions.Context;
using Burbuja.Agents.Abstractions.Tools;
using Burbuja.Agents.Runtime;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Policy;
using Burbuja.Plugins.Policy.Core;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Burbuja.Agents.UnitTests;

/// <summary>
/// M4.3f: agent-to-agent access is classified against the kernel-supplied adjacency facts. A
/// missing fact escalates; a non-adjacent target is a hard deny; nothing here ever fails open.
/// </summary>
public sealed class AgentClassifierTests
{
    private static readonly AgentReadClassifier Read = new();
    private static readonly AgentWriteClassifier Write = new();

    [Fact]
    public void Missing_peers_escalates_not_allows()
    {
        var verdict = Read.Classify(Subject("agent.read", Target("agt-b"), peers: null));
        verdict.Tier.ShouldBe(RiskTier.Escalate);

        var write = Write.Classify(Subject("agent.write", Target("agt-b"), peers: null));
        write.Tier.ShouldBe(RiskTier.Escalate);
    }

    [Fact]
    public void Unparseable_target_escalates()
    {
        var verdict = Read.Classify(Subject("agent.read", new JsonObject(), ["child:agt-b"]));
        verdict.Tier.ShouldBe(RiskTier.Escalate);
    }

    [Fact]
    public void Adjacent_read_is_allowed()
    {
        Read.Classify(Subject("agent.read", Target("agt-parent"), ["parent:agt-parent"])).Tier.ShouldBe(RiskTier.Allow);
        Read.Classify(Subject("agent.read", Target("agt-child"), ["child:agt-child"])).Tier.ShouldBe(RiskTier.Allow);
        Read.Classify(Subject("agent.read", Target("agt-peer"), ["team:agt-peer"])).Tier.ShouldBe(RiskTier.Allow);
    }

    [Fact]
    public void Sibling_read_is_hard_denied()
    {
        var verdict = Read.Classify(Subject("agent.read", Target("agt-sibling"), ["parent:agt-root", "child:agt-b", "team:tm-1"]));
        verdict.Tier.ShouldBe(RiskTier.HardDeny);
        verdict.Finding.Class.ShouldBe("authority_violation");
    }

    [Fact]
    public void Grandparent_message_is_hard_denied()
    {
        var verdict = Write.Classify(Subject("agent.write", Target("agt-grandparent"), ["parent:agt-root", "team:tm-1"]));
        verdict.Tier.ShouldBe(RiskTier.HardDeny);
        verdict.Finding.Class.ShouldBe("authority_violation");
    }

    [Fact]
    public void Inject_to_a_non_child_is_hard_denied()
    {
        var input = Target("agt-parent");
        input["mode"] = "inject";
        var verdict = Write.Classify(Subject("agent.write", input, ["parent:agt-parent", "child:agt-b"]));

        verdict.Tier.ShouldBe(RiskTier.HardDeny);
        verdict.Finding.Reason.ShouldContain("non-child");
    }

    [Fact]
    public void Inject_to_a_child_is_allowed()
    {
        var input = Target("agt-b");
        input["mode"] = "inject";
        Write.Classify(Subject("agent.write", input, ["child:agt-b"])).Tier.ShouldBe(RiskTier.Allow);
    }

    [Fact]
    public void Over_bounds_write_is_hard_denied()
    {
        var input = Target("agt-b");
        input["body"] = new string('x', AgentWriteClassifier.MaxBodyBytes + 1);
        var verdict = Write.Classify(Subject("agent.write", input, ["child:agt-b"]));

        verdict.Tier.ShouldBe(RiskTier.HardDeny);
        verdict.Finding.Class.ShouldBe("bounds_exceeded");
    }

    [Fact]
    public void Read_only_profile_narrows_agent_write_to_gray()
    {
        var verdict = Write.Classify(Subject("agent.write", Target("agt-b"), ["child:agt-b"], PolicyProfile.ReadOnly));
        verdict.Tier.ShouldBe(RiskTier.Gray);
    }

    [Fact]
    public void Args_are_never_rewritten()
    {
        var input = Target("agt-b");
        input["text"] = "original";
        var before = input.ToJsonString();

        Write.Classify(Subject("agent.write", input, ["child:agt-b"]));

        input.ToJsonString().ShouldBe(before, "the classifier inspects the model's arguments, it never rewrites them");
    }

    private static JsonObject Target(string target) => new() { ["target"] = target };

    private static ActionPolicySubject Subject(string kind, JsonObject input, IReadOnlyList<string>? peers, PolicyProfile profile = PolicyProfile.FullAuto) =>
        new(kind, input, new PolicyContext(
            "stream/1",
            "/work",
            "/work/.artifacts",
            profile,
            "dev",
            "/work",
            [],
            [],
            [],
            Peers: peers));
}

public sealed class AgentToolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string AgentsRoot =
        "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Agents/agents";

    [Fact]
    public async Task Context_read_is_wrapped_as_untrusted_background_with_exact_counts()
    {
        using var sp = Harness.CreateHost();
        var target = AgentsRoot + "/ctx-b";
        await sp.GetRequiredService<Burbuja.Core.Abstractions.Arm.IResourceStore>().UpsertAsync(
            new ResourceEnvelope<JsonObject>(
                target + "/contexts/handoff",
                "handoff",
                "Burbuja.Agents/agents/contexts",
                "local",
                new Dictionary<string, string>(),
                new JsonObject { ["label"] = "handoff", ["body"] = "the previous agent said hello" },
                ProvisioningState.Succeeded,
                new SystemData(DateTimeOffset.UnixEpoch, "s", "System", DateTimeOffset.UnixEpoch, "s", "System"),
                ""),
            null,
            Ct);

        var tool = sp.GetServices<IAgentTool>().Single(t => t.Descriptor.Name == "read_agent_context");
        var result = await tool.ExecuteAsync(
            Context(AgentsRoot + "/a1/sessions/s1"),
            new JsonObject { ["target"] = target },
            Pass(),
            Ct);

        result.IsError.ShouldBeFalse();
        var text = result.Content["text"]!.GetValue<string>();
        text.ShouldContain(UntrustedBackground.Marker);
        text.ShouldContain("the previous agent said hello");
        result.Content["cards"]!.GetValue<int>().ShouldBe(1);
        result.Content["omitted"]!.GetValue<int>().ShouldBe(0);
    }

    [Fact]
    public async Task Posted_context_is_redacted_at_write_and_at_read()
    {
        using var sp = Harness.CreateHost();
        var target = AgentsRoot + "/redact-b";
        var secret = "AKIA" + new string('A', 16);

        var post = sp.GetServices<IAgentTool>().Single(t => t.Descriptor.Name == "post_context");
        var posted = await post.ExecuteAsync(
            Context(AgentsRoot + "/a1/sessions/s1"),
            new JsonObject { ["target"] = target, ["label"] = "note", ["body"] = "key=" + secret },
            Pass(),
            Ct);
        posted.IsError.ShouldBeFalse();

        var read = sp.GetServices<IAgentTool>().Single(t => t.Descriptor.Name == "read_agent_context");
        var result = await read.ExecuteAsync(
            Context(AgentsRoot + "/a1/sessions/s1"),
            new JsonObject { ["target"] = target },
            Pass(),
            Ct);

        var text = result.Content["text"]!.GetValue<string>();
        text.ShouldNotContain(secret);
        text.ShouldContain("[redacted:");
    }

    [Fact]
    public async Task Send_message_queues_once_per_dedup_key()
    {
        using var sp = Harness.CreateHost();
        var target = AgentsRoot + "/msg-b";
        var store = sp.GetRequiredService<Burbuja.Core.Abstractions.Arm.IResourceStore>();
        await store.UpsertAsync(
            new ResourceEnvelope<JsonObject>(
                target,
                "msg-b",
                "Burbuja.Agents/agents",
                "local",
                new Dictionary<string, string>(),
                new JsonObject { ["displayName"] = "msg-b", ["inbox"] = new JsonArray(), ["deliveredDedupKeys"] = new JsonArray() },
                ProvisioningState.Succeeded,
                new SystemData(DateTimeOffset.UnixEpoch, "s", "System", DateTimeOffset.UnixEpoch, "s", "System"),
                ""),
            null,
            Ct);

        var send = sp.GetServices<IAgentTool>().Single(t => t.Descriptor.Name == "send_message");
        var input = new JsonObject { ["target"] = target, ["text"] = "hello", ["dedupKey"] = "k1" };
        var first = await send.ExecuteAsync(Context(AgentsRoot + "/a1/sessions/s1"), input, Pass(), Ct);
        var second = await send.ExecuteAsync(Context(AgentsRoot + "/a1/sessions/s1"), input.DeepClone(), Pass(), Ct);

        first.Content["queued"]!.GetValue<bool>().ShouldBeTrue();
        second.Content["queued"]!.GetValue<bool>().ShouldBeFalse();

        var stored = await store.GetAsync(target.ToLowerInvariant(), Ct);
        stored!.Properties["inbox"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public void Every_agent_tool_declares_an_agent_risk_hint()
    {
        using var sp = Harness.CreateHost();
        string[] names = ["list_agents", "read_agent_context", "session_search", "post_context", "send_message"];
        var tools = sp.GetServices<IAgentTool>().Where(t => names.Contains(t.Descriptor.Name)).ToList();
        tools.Count.ShouldBe(5);
        tools.All(t => t.Descriptor.RiskHint is "agent.read" or "agent.write").ShouldBeTrue();
    }

    private static ToolExecutionContext Context(string streamId) =>
        new("/work", streamId, "run-1", "job-1", TimeSpan.FromSeconds(30), new Dictionary<string, string>());

    private static GatePass Pass() =>
        (GatePass)Activator.CreateInstance(
            typeof(GatePass),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null,
            ["tu-1", "tool", "dk-1", null!],
            null)!;
}

/// <summary>
/// D7 at the unit layer: the job tool path supplies real adjacency facts, so a model that tries to
/// address a sibling is hard-denied instead of escalated or allowed.
/// </summary>
public sealed class AgentAdjacencyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string AgentsRoot =
        "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Agents/agents";

    [Fact]
    public async Task Peers_are_parent_children_and_team_with_bare_names()
    {
        using var sp = Harness.CreateHost();
        var store = sp.GetRequiredService<Burbuja.Core.Abstractions.Arm.IResourceStore>();
        await PutAgentAsync(store, "parent");
        await PutAgentAsync(store, "runner", parent: AgentsRoot + "/parent", team: "team-1");
        await PutAgentAsync(store, "kid-1", parent: AgentsRoot + "/runner");
        await PutAgentAsync(store, "sibling", parent: AgentsRoot + "/parent");

        var peers = await AgentAdjacency.PeersAsync(store, AgentsRoot + "/runner", null, Ct);

        peers.ShouldNotBeNull();
        peers!.ShouldContain("parent:parent");
        peers.ShouldContain("child:kid-1");
        peers.ShouldContain("team:team-1");
        peers.ShouldNotContain("child:sibling", "a sibling is not adjacent");
    }

    [Fact]
    public async Task No_agent_means_no_facts_at_all()
    {
        using var sp = Harness.CreateHost();
        var store = sp.GetRequiredService<Burbuja.Core.Abstractions.Arm.IResourceStore>();

        (await AgentAdjacency.PeersAsync(store, null, null, Ct)).ShouldBeNull();
        (await AgentAdjacency.PeersAsync(store, null, "team-9", Ct))!.ToList().ShouldBe(["team:team-9"]);
        (await AgentAdjacency.PeersAsync(store, "missing-agent", null, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Send_to_a_sibling_is_hard_denied_on_the_job_stream()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        var store = sp.GetRequiredService<Burbuja.Core.Abstractions.Arm.IResourceStore>();
        await PutAgentAsync(store, "parent");
        await PutAgentAsync(store, "runner", parent: AgentsRoot + "/parent");
        await PutAgentAsync(store, "sibling-agent", parent: AgentsRoot + "/parent");

        var body = "{\"tags\":{\"" + Burbuja.Agents.Abstractions.AgentsConstants.ScenarioTag + "\":\"adjacency-denied\"},"
                   + "\"properties\":{\"agentId\":\"" + AgentsRoot + "/runner\","
                   + "\"spec\":{\"kind\":\"implement\",\"title\":\"t1\",\"slug\":\"job\",\"brief\":\"reach out\",\"language\":\"es\",\"targetEnv\":\"dev\",\"autoAccept\":true}}}";
        (await Harness.Send(sp, "PUT", Harness.JobPath, body)).Ok();
        await Harness.RunJobAsync(sp, Harness.JobPath, Ct);

        var get = await Harness.Send(sp, "GET", Harness.JobPath);
        get.AsJson()!["properties"]!["status"]!.GetValue<string>().ShouldBe("succeeded");

        var events = new List<Burbuja.Core.Abstractions.Events.LedgerEvent>();
        var log = sp.GetRequiredService<Burbuja.Core.Abstractions.Events.IEventLog>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));
        try
        {
            await foreach (var e in log.TailAsync(Burbuja.Agents.Abstractions.AgentsConstants.StreamId(Harness.JobPath), 0, cts.Token))
            {
                events.Add(e);
                if (e.Type == Burbuja.Agents.Abstractions.Domain.AgentEventTypes.Result)
                    break;
            }
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
        }

        var denied = events.Where(e => e.Type == Burbuja.Agents.Abstractions.Domain.AgentEventTypes.ToolDenied).ToList();
        denied.Count.ShouldBe(1);
        denied[0].Payload!["class"]!.GetValue<string>().ShouldBe("authority_violation");
        events.ShouldNotContain(e => e.Type == Burbuja.Agents.Abstractions.Domain.AgentEventTypes.Approval);

        // The sibling's inbox is untouched: a denied call never reaches the tool.
        var sibling = await store.GetAsync((AgentsRoot + "/sibling-agent").ToLowerInvariant(), Ct);
        (sibling!.Properties["inbox"] as JsonArray)?.Count.ShouldNotBe(1);
    }

    private static async Task PutAgentAsync(
        Burbuja.Core.Abstractions.Arm.IResourceStore store,
        string name,
        string? parent = null,
        string? team = null)
    {
        var id = AgentsRoot + "/" + name;
        var properties = new JsonObject
        {
            ["displayName"] = name,
            ["inbox"] = new JsonArray(),
            ["deliveredDedupKeys"] = new JsonArray(),
        };
        if (parent is not null)
            properties["parentAgentId"] = parent;
        if (team is not null)
            properties["teamId"] = team;

        await store.UpsertAsync(
            new Burbuja.Core.Abstractions.Arm.ResourceEnvelope<JsonObject>(
                id,
                name,
                "Burbuja.Agents/agents",
                "local",
                new Dictionary<string, string>(),
                properties,
                Burbuja.Core.Abstractions.Arm.ProvisioningState.Succeeded,
                new Burbuja.Core.Abstractions.Arm.SystemData(DateTimeOffset.UnixEpoch, "s", "System", DateTimeOffset.UnixEpoch, "s", "System"),
                ""),
            null,
            Ct);
    }
}
