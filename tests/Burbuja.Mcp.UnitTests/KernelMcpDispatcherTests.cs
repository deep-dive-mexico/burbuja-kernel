using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Agents;
using Burbuja.Agents.Abstractions.Domain;
using Burbuja.Agents.Abstractions.Tools;
using Burbuja.Agents.Model;
using Burbuja.Agents.Runtime;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Store;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Arm;
using Burbuja.Core.DependencyInjection;
using Burbuja.Mcp;
using Burbuja.Mcp.Abstractions;
using Burbuja.Plugins.Llm.Scripted;
using Burbuja.Plugins.Policy.Core;
using Burbuja.Plugins.Supervisor;
using Burbuja.Plugins.Tools.Agents;
using Burbuja.Plugins.Tools.Http;
using Burbuja.Plugins.Tools.Workspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Burbuja.Mcp.UnitTests;

/// <summary>
/// The kernel MCP lane at the dispatcher, without a server: authorisation, the same gate and
/// executor the kernel's own loop uses, and the approval contract. The protocol framing itself is
/// proven black-box in <c>Burbuja.E2E.Tests.McpE2ETests</c>.
/// </summary>
public sealed class KernelMcpDispatcherTests
{
    [Fact]
    public async Task Retrying_an_undecided_call_keeps_its_original_approval()
    {
        using var sp = Host();
        await SeedAsync(sp);
        await PutAgentAsync(sp, "pending", null, scenario: "gray-supervisor-escalate-approved");
        var dispatcher = sp.GetRequiredService<KernelMcpDispatcher>();
        var first = await dispatcher.CallToolAsync(Caller("pending"), "bash",
            new JsonObject { ["command"] = "unknowncmd --escalate" }, Ct);
        Assert.True(first.ApprovalPending);
        var retry = await dispatcher.CallToolAsync(Caller("pending"), "bash",
            new JsonObject { ["command"] = "unknowncmd --escalate", ["approvalId"] = first.ApprovalId }, Ct);
        Assert.True(retry.ApprovalPending);
        Assert.Equal(first.ApprovalId, retry.ApprovalId);
        var events = await SessionEventsAsync(sp, "pending");
        Assert.DoesNotContain(events, evt => evt.Type == AgentEventTypes.ToolCall);
    }

    private const string AgentsRoot = "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Agents";
    private const string McpKey = "mcp-key";
    private const string OwnerKey = "owner-key";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Exported_tools_are_exactly_the_registered_kernel_tools()
    {
        using var sp = Host();
        var registered = sp.GetRequiredService<IEnumerable<IAgentTool>>()
            .Select(t => t.Descriptor.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        var exported = sp.GetRequiredService<KernelToolExporter>().Export();

        registered.ShouldNotBeEmpty();
        exported.Select(t => t.McpName).ShouldBe(registered);
        exported.Select(t => t.ToolName).ShouldBe(registered);
        foreach (var tool in exported)
        {
            tool.Description.ShouldNotBeNullOrWhiteSpace();
            tool.InputSchema.ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task A_gated_call_writes_intent_before_decision_and_runs_the_tool()
    {
        var workRoot = NewWorkRoot();
        try
        {
            using var sp = Host();
            await SeedAsync(sp);
            await PutAgentAsync(sp, "worker", workRoot, scenario: null);
            var dispatcher = sp.GetRequiredService<KernelMcpDispatcher>();

            var outcome = await dispatcher.CallToolAsync(
                Caller("worker"),
                "write_file",
                new JsonObject { ["path"] = "note.txt", ["content"] = "hello" },
                Ct);

            outcome.IsError.ShouldBeFalse(outcome.Text);
            File.ReadAllText(Path.Combine(workRoot, "note.txt")).ShouldBe("hello");

            var events = await SessionEventsAsync(sp, "worker");
            var intent = events.First(e => e.Type == AgentEventTypes.ToolIntent);
            var decision = events.First(e => e.Type == AgentEventTypes.Decision);
            var call = events.First(e => e.Type == AgentEventTypes.ToolCall);
            var result = events.First(e => e.Type == AgentEventTypes.ToolResult);
            intent.Seq.ShouldBeLessThan(decision.Seq);
            decision.Seq.ShouldBeLessThan(call.Seq);
            call.Seq.ShouldBeLessThan(result.Seq);
            decision.Summary.ShouldBe("Allow");
        }
        finally
        {
            Drop(workRoot);
        }
    }

    [Fact]
    public async Task An_unknown_agent_is_refused_and_writes_nothing()
    {
        using var sp = Host();
        await SeedAsync(sp);
        var dispatcher = sp.GetRequiredService<KernelMcpDispatcher>();

        var outcome = await dispatcher.CallToolAsync(
            Caller("ghost"),
            "write_file",
            new JsonObject { ["path"] = "x", ["content"] = "y" },
            Ct);

        outcome.IsError.ShouldBeTrue();
        outcome.Text.ShouldContain("unknown agent");
    }

    [Fact]
    public async Task A_reader_key_cannot_call_a_tool()
    {
        using var sp = Host();
        await SeedAsync(sp);
        await PutAgentAsync(sp, "worker", null, scenario: null);
        var dispatcher = sp.GetRequiredService<KernelMcpDispatcher>();

        // The Reader role holds `*/read` and never the agent tool action: reading the control plane
        // is not permission to make the kernel do something.
        var outcome = await dispatcher.CallToolAsync(
            Caller("worker", principalId: "reader"),
            "write_file",
            new JsonObject { ["path"] = "x", ["content"] = "y" },
            Ct);

        outcome.IsError.ShouldBeTrue();
        outcome.Text.ShouldContain("does not have authorization");
    }

    [Fact]
    public async Task An_escalation_is_answered_with_the_approval_and_never_blocks()
    {
        using var sp = Host();
        await SeedAsync(sp);
        await PutAgentAsync(sp, "gray", null, scenario: "gray-supervisor-escalate-approved");
        var dispatcher = sp.GetRequiredService<KernelMcpDispatcher>();

        var started = DateTimeOffset.UtcNow;
        var outcome = await dispatcher.CallToolAsync(
            Caller("gray"),
            "bash",
            new JsonObject { ["command"] = "unknowncmd --escalate" },
            Ct);
        var elapsed = DateTimeOffset.UtcNow - started;

        outcome.IsError.ShouldBeTrue();
        outcome.ApprovalPending.ShouldBeTrue(outcome.Text);
        outcome.ApprovalId.ShouldNotBeNullOrEmpty();
        // Generous on purpose: the point is that the call is answered at all, and the suite runs nine
        // assemblies concurrently — a 1 s bound was a flake, not a guarantee.
        elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), "an MCP call is answered, never pinned on an approval");

        // The pending id is on the agent resource, which is what makes `agents.state` read waiting.
        var agent = await AgentPersistence.GetAsync(sp.GetRequiredService<IResourceStore>(), Key("gray"), Ct);
        agent!.Properties.PendingApprovalId.ShouldBe(outcome.ApprovalId);

        var events = await SessionEventsAsync(sp, "gray");
        events.ShouldContain(e => e.Type == AgentEventTypes.Approval && e.Summary == "pending");
    }

    [Fact]
    public async Task An_approved_call_runs_on_the_retry_that_carries_the_approval_id()
    {
        using var sp = Host();
        await SeedAsync(sp);
        await PutAgentAsync(sp, "gray", null, scenario: "gray-supervisor-escalate-approved");
        var dispatcher = sp.GetRequiredService<KernelMcpDispatcher>();

        var pending = await dispatcher.CallToolAsync(
            Caller("gray"), "bash", new JsonObject { ["command"] = "unknowncmd --escalate" }, Ct);
        pending.ApprovalPending.ShouldBeTrue(pending.Text);
        var approvalId = pending.ApprovalId!;

        await sp.GetRequiredService<Burbuja.Core.Abstractions.Approvals.IApprovalService>()
            .ApproveAsync(approvalId, "test", null, null, Ct);

        var retry = await dispatcher.CallToolAsync(
            Caller("gray"),
            "bash",
            new JsonObject { ["command"] = "unknowncmd --escalate", ["approvalId"] = approvalId },
            Ct);

        retry.ApprovalPending.ShouldBeFalse("the approved call must run, not ask again: " + retry.Text);

        var events = await SessionEventsAsync(sp, "gray");
        events.Count(e => e.Type == AgentEventTypes.ToolCall).ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task An_approval_id_does_not_authorise_a_different_call()
    {
        using var sp = Host();
        await SeedAsync(sp);
        await PutAgentAsync(sp, "gray", null, scenario: "gray-supervisor-escalate-approved");
        var dispatcher = sp.GetRequiredService<KernelMcpDispatcher>();

        var benign = await dispatcher.CallToolAsync(
            Caller("gray"), "bash", new JsonObject { ["command"] = "unknowncmd --escalate" }, Ct);
        benign.ApprovalPending.ShouldBeTrue(benign.Text);

        await sp.GetRequiredService<Burbuja.Core.Abstractions.Approvals.IApprovalService>()
            .ApproveAsync(benign.ApprovalId!, "test", null, null, Ct);

        // The id was minted for one command. Replaying it with another must not execute: the
        // executor re-derives the preview the human saw and falls through to a fresh decision.
        var replay = await dispatcher.CallToolAsync(
            Caller("gray"),
            "bash",
            new JsonObject { ["command"] = "rm -rf /", ["approvalId"] = benign.ApprovalId! },
            Ct);

        replay.ApprovalPending.ShouldBeFalse("rm -rf / is a hard deny, never a pending approval");
        replay.IsError.ShouldBeTrue();
        replay.Text.ShouldContain("Burbuja blocked");

        var events = await SessionEventsAsync(sp, "gray");
        var denied = events.First(e => e.Type == AgentEventTypes.ToolDenied);
        // The denied call is audited after the approval it was replayed against, and it is the only
        // tool call on the stream (the first attempt never ran).
        denied.Seq.ShouldBeGreaterThan(events.First(e => e.Type == AgentEventTypes.Approval).Seq);
        events.Count(e => e.Type == AgentEventTypes.ToolCall).ShouldBe(0, "neither attempt reached the tool");
    }

    [Fact]
    public async Task A_kernel_driven_agent_cannot_be_driven_over_mcp()
    {
        using var sp = Host();
        await SeedAsync(sp);
        // driver defaults to `kernel`: its turns belong to the activation loop.
        var status = await SendAsync(sp, "PUT", $"{AgentsRoot}/agents/inproc", """{"properties":{"displayName":"inproc"}}""");
        status.ShouldBeLessThan(300);
        var dispatcher = sp.GetRequiredService<KernelMcpDispatcher>();

        var outcome = await dispatcher.CallToolAsync(
            Caller("inproc"), "write_file", new JsonObject { ["path"] = "x", ["content"] = "y" }, Ct);

        outcome.IsError.ShouldBeTrue();
        outcome.Text.ShouldContain("not 'external'");
    }

    [Fact]
    public async Task An_unknown_agent_lists_no_tools()
    {
        using var sp = Host();
        await SeedAsync(sp);
        var dispatcher = sp.GetRequiredService<KernelMcpDispatcher>();

        (await dispatcher.ListToolsAsync(Caller("ghost"), Ct)).ShouldBeEmpty(
            "the whole catalogue is not an answer about an agent that does not exist");

        await PutAgentAsync(sp, "listed", null, scenario: null);
        var tools = await dispatcher.ListToolsAsync(Caller("listed"), Ct);
        tools.ShouldNotBeEmpty();
        tools.ShouldContain(t => t.ToolName == "write_file");
    }

    [Fact]
    public async Task The_persona_is_redacted_like_every_other_egress()
    {
        using var sp = Host();
        await SeedAsync(sp);
        const string secret = "sk-abcdefghijklmnopqrstuvwxyz0123456789";
        var status = await SendAsync(
            sp,
            "PUT",
            $"{AgentsRoot}/agents/secrets",
            "{\"properties\":{\"displayName\":\"secrets\",\"driver\":\"external\",\"persona\":\"key " + secret + "\"}}");
        status.ShouldBeLessThan(300);
        var dispatcher = sp.GetRequiredService<KernelMcpDispatcher>();

        var persona = await dispatcher.GetPersonaAsync(Caller("secrets"), Ct);

        persona.ShouldNotBeNull();
        persona!.Contains(secret, StringComparison.Ordinal).ShouldBeFalse();
    }

    [Fact]
    public void The_pending_code_is_the_contract_not_a_copy()
    {
        var block = ToolBatchExecutor.PendingResult("tu-1", "ap-1");
        block.Content["code"]!.GetValue<string>().ShouldBe(McpConstants.ApprovalPendingCode);
    }

    [Fact]
    public void Tool_names_survive_normalisation_without_colliding()
    {
        McpToolName.For("burbuja", "write_file").ShouldBe("mcp__burbuja__write_file");
        McpToolName.IsFor("mcp__burbuja__write_file", "burbuja").ShouldBeTrue();
        McpToolName.IsFor("mcp__host__write_file", "burbuja").ShouldBeFalse();

        // A tool name the convention cannot carry keeps its identity through the digest suffix,
        // so two different remote tools can never normalise onto one local name.
        var dotted = McpToolName.For("remote", "fs.read");
        var spaced = McpToolName.For("remote", "fs read");
        dotted.ShouldNotBe(spaced);
        dotted.ShouldStartWith("mcp__remote__fs_read__");
    }

    // ---------------------------------------------------------------- harness

    private static ServiceProvider Host()
    {
        var dir = Path.Combine(Path.GetTempPath(), "burbuja-mcp-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var scenarioDir = Path.Combine(AppContext.BaseDirectory, "scenarios");
        Environment.SetEnvironmentVariable("BURBUJA_SCRIPTED_SCENARIO_DIR", scenarioDir);
        var values = new Dictionary<string, string?>
        {
            ["Kernel:Role"] = "all",
            ["Kernel:PluginsDirectory"] = dir,
            ["Kernel:EnabledPlugins"] = "",
            ["Kernel:TrustedPlugins:0"] = "Policy.Core",
            ["Kernel:TrustedPlugins:1"] = "Supervisor",
            ["Kernel:TrustedPlugins:2"] = "Tools.Agents",
            ["Kernel:NodeId"] = "node-a",
            ["Auth:ApiKeys:0:Key"] = OwnerKey,
            ["Auth:ApiKeys:0:PrincipalId"] = "owner",
            ["Auth:ApiKeys:0:Roles:0"] = "Owner",
            ["Auth:ApiKeys:1:Key"] = McpKey,
            ["Auth:ApiKeys:1:PrincipalId"] = "mcp-lane",
            ["Auth:ApiKeys:1:PrincipalType"] = "Service",
            ["Auth:ApiKeys:1:Roles:0"] = McpConstants.LaneRole,
            ["Auth:ApiKeys:2:Key"] = "reader-key",
            ["Auth:ApiKeys:2:PrincipalId"] = "reader",
            ["Auth:ApiKeys:2:Roles:0"] = "Reader",
            ["Runtime:OutboxRelayInterval"] = "00:00:00.050",
            ["Runtime:AgentApprovalWait"] = "00:00:00",
            ["Budget:DefaultJobUsd"] = "5",
            ["Arm:Throttle:WritesPerMinute"] = "12000",
            ["Arm:Throttle:ReadsPerMinute"] = "12000",
            ["Arm:Throttle:DeletesPerMinute"] = "12000",
            ["Agents:ScenarioDirectory"] = scenarioDir,
            ["Agents:ProviderId"] = "scripted",
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddBurbujaCore(config, [
            new AgentsPlugin(),
            new PolicyCorePlugin(),
            new ScriptedLlmPlugin(),
            new SupervisorPlugin(),
            new WorkspaceToolsPlugin(),
            new HttpToolsPlugin(),
            new AgentToolsPlugin(),
        ]);
        services.AddBurbujaMcp();
        // AddMcpServer registers a hosted service that wants the application lifetime; a bare
        // ServiceCollection has none, and this lane never runs a host.
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostApplicationLifetime, NoLifetime>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private sealed class NoLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    private static McpCaller Caller(string agent, string principalId = "mcp-lane")
    {
        var scope = $"{AgentsRoot}/agents/{agent}";
        return new McpCaller(
            new ArmPrincipal(principalId, "Service", null, [], new Dictionary<string, string>()),
            agent,
            scope,
            scope.ToLowerInvariant());
    }

    private static async Task SeedAsync(IServiceProvider sp)
    {
        (await SendAsync(sp, "PUT", "/subscriptions/sub1", """{"properties":{"displayName":"s","state":"Enabled"}}""")).ShouldBeLessThan(300);
        (await SendAsync(sp, "PUT", "/subscriptions/sub1/resourceGroups/rg1", """{"properties":{}}""")).ShouldBeLessThan(300);
    }

    private static async Task PutAgentAsync(IServiceProvider sp, string name, string? workRoot, string? scenario)
    {
        var parts = new List<string> { "\"displayName\":\"" + name + "\"", "\"driver\":\"" + AgentDrivers.External + "\"" };
        if (workRoot is not null)
            parts.Add("\"workspaceId\":\"" + workRoot.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"");
        if (scenario is not null)
            parts.Add("\"profile\":\"" + scenario + "\"");
        var status = await SendAsync(sp, "PUT", $"{AgentsRoot}/agents/{name}", "{\"properties\":{" + string.Join(',', parts) + "}}");
        status.ShouldBeLessThan(300);
    }

    private static async Task<int> SendAsync(IServiceProvider sp, string method, string path, string? body)
    {
        Stream? stream = body is null ? null : new MemoryStream(Encoding.UTF8.GetBytes(body));
        var ctx = new ArmRequestContext
        {
            Method = method,
            Path = path,
            Query = new Dictionary<string, string> { ["api-version"] = ArmConstants.ApiVersion },
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-api-key"] = OwnerKey },
            Body = stream,
            Services = sp,
        };
        await sp.GetRequiredService<ArmPipeline>().RunAsync(ctx, Ct);
        return ctx.StatusCode;
    }

    private static async Task<List<LedgerEvent>> SessionEventsAsync(IServiceProvider sp, string agent)
    {
        var store = sp.GetRequiredService<IResourceStore>();
        var page = await store.ListAsync(
            new ResourceStoreQuery(null, null, AgentsConstants.Namespace, "agents/sessions", 50, null, Key(agent)),
            Ct);
        page.Value.Count.ShouldBeGreaterThanOrEqualTo(1, "the MCP call creates the agent's session");
        var session = page.Value[0].Properties.Deserialize<SessionProperties>(JsonWire.Options)!;
        var log = sp.GetRequiredService<IEventLog>();
        var events = new List<LedgerEvent>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        await foreach (var e in log.ReadRangeAsync(session.StreamId, 0, long.MaxValue, 500, null, cts.Token))
            events.Add(e);
        return events;
    }

    private static string Key(string name) => $"{AgentsRoot}/agents/{name}".ToLowerInvariant();

    private static string NewWorkRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "burbuja-mcp-work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Drop(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
