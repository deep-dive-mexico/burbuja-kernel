using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Agents;
using Burbuja.Agents.Abstractions.Domain;
using Burbuja.Agents.Runtime;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Burbuja.Agents.UnitTests;

/// <summary>
/// M5.1 slice D: a stateful agent now calls tools through the same gate and the same executor as a
/// job, books what its turns cost, and — when its driver is `external` — never runs a model turn at
/// all. The job path itself is proven unchanged by the D1–D4 scenarios in <see cref="ScenarioTests"/>.
/// </summary>
public sealed class AgentToolLoopTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string AgentsRoot =
        "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Agents/agents";

    [Fact]
    public async Task Agent_tool_loop_gates_writes_and_books_cost()
    {
        var workRoot = NewWorkRoot();
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", AgentsRoot + "/a1", AgentBody("a1", "agent-tool-loop", workRoot))).Ok();
        (await Harness.Send(sp, "POST", AgentsRoot + "/a1/send", """{"mode":"followup","text":"write the note"}"""))
            .StatusCode.ShouldBe(202);

        await sp.GetRequiredService<AgentActivationWorker>().ActivateAsync(Key("a1"), Ct);

        // The tool really ran, in the agent's own work root.
        File.ReadAllText(Path.Combine(workRoot, "note.txt")).ShouldBe("written by the agent");

        // The audit trail is the job's, in order: intent < decision < call < result.
        var session = Node(await Harness.Send(sp, "GET", AgentsRoot + "/a1/sessions"))!["value"]!.AsArray()[0]!;
        var events = await ReadAsync(sp, session["properties"]!["streamId"]!.GetValue<string>());

        var intent = events.Single(e => e.Type == AgentEventTypes.ToolIntent);
        var decision = events.Single(e => e.Type == AgentEventTypes.Decision);
        var call = events.Single(e => e.Type == AgentEventTypes.ToolCall);
        var result = events.Single(e => e.Type == AgentEventTypes.ToolResult);
        intent.Seq.ShouldBeLessThan(decision.Seq, "the intent is written before the verdict");
        decision.Seq.ShouldBeLessThan(call.Seq, "the tool runs only after the decision");
        call.Seq.ShouldBeLessThan(result.Seq);
        decision.Summary.ShouldBe("Allow");
        result.Summary.ShouldBe("ok");

        // Two turns (tool use, then the answer), both booked.
        var agent = Node(await Harness.Send(sp, "GET", AgentsRoot + "/a1"))!;
        var cost = agent["properties"]!["costUsd"]!.GetValue<decimal>();
        cost.ShouldBeGreaterThan(0);
        var ledger = sp.GetRequiredService<Burbuja.Agents.Abstractions.Budget.IBudgetLedger>();
        (await ledger.GetJobSpendAsync(EntityIds.Derive(Key("a1")), Ct)).ShouldBe(cost);

        var messages = events.Where(e => e.Type == AgentSessionEvents.Message).ToList();
        messages.Select(m => m.Payload!["role"]!.GetValue<string>()).ShouldBe(["user", "assistant", "user", "assistant"]);
    }

    /// <summary>
    /// M9 T1: the stream says what a turn cost in TIME and USAGE, and cites the ledger entry it paid —
    /// instead of leaving a reader to infer a duration from the gap between two row timestamps.
    ///
    /// <para>The same activation runs two model turns (the tool call, then the answer), so this is also
    /// where the session's `turn` counter is pinned to TURNS rather than activations.</para>
    /// </summary>
    [Fact]
    public async Task Every_turn_reports_its_own_timing_usage_and_the_ledger_entry_it_paid()
    {
        var workRoot = NewWorkRoot();
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", AgentsRoot + "/t1", AgentBody("t1", "agent-tool-loop", workRoot))).Ok();
        (await Harness.Send(sp, "POST", AgentsRoot + "/t1/send", """{"mode":"followup","text":"write the note"}"""))
            .StatusCode.ShouldBe(202);

        await sp.GetRequiredService<AgentActivationWorker>().ActivateAsync(Key("t1"), Ct);

        var session = Node(await Harness.Send(sp, "GET", AgentsRoot + "/t1/sessions"))!["value"]!.AsArray()[0]!;
        var events = await ReadAsync(sp, session["properties"]!["streamId"]!.GetValue<string>());

        var started = events.Where(e => e.Type == AgentEventTypes.TurnStarted).ToList();
        var completed = events.Where(e => e.Type == AgentEventTypes.TurnCompleted).ToList();
        started.Count.ShouldBe(2, "one `started` per model turn");
        completed.Count.ShouldBe(2, "and one `completed`");

        foreach (var turn in completed)
        {
            turn.Payload!["llmMs"].ShouldNotBeNull("a completed turn carries a MEASURED duration");
            turn.Payload!["llmMs"]!.GetValue<int>().ShouldBeGreaterThanOrEqualTo(0);
            turn.Payload!["usage"]!["input"].ShouldNotBeNull();
            turn.Payload!["usage"]!["cacheRead"].ShouldNotBeNull("the cache columns a trajectory shows are on the event");
            turn.Payload!["model"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
            turn.Payload!["stopReason"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        }

        completed[0].Payload!["stopReason"]!.GetValue<string>().ShouldBe(
            nameof(Burbuja.Agents.Abstractions.Llm.LlmStopReason.ToolUse),
            "the first turn asked for the tool");

        // The booking is CITED, not restated: each turn names the ledger entry it paid.
        var bookings = completed.Select(t => t.Payload!["bookingId"]?.GetValue<string>()).ToList();
        bookings.ShouldAllBe(b => !string.IsNullOrEmpty(b));
        bookings.Distinct(StringComparer.Ordinal).Count().ShouldBe(2, "two turns, two ledger entries");
        completed.ShouldAllBe(t => t.Payload!["booked"]!.GetValue<bool>());

        // A tool result says how long the TOOL took, which is not a timestamp delta either.
        var result = events.Single(e => e.Type == AgentEventTypes.ToolResult);
        result.Payload!["durationMs"].ShouldNotBeNull();
        result.Payload!["durationMs"]!.GetValue<int>().ShouldBeGreaterThanOrEqualTo(0);

        // One activation, two model turns: the session counter must say two.
        session["properties"]!["turn"]!.GetValue<int>().ShouldBe(2, "the counter counts turns, not activations");
    }

    [Fact]
    public async Task External_agent_delivers_its_inbox_and_never_calls_a_model()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", AgentsRoot + "/ext", ExternalAgentBody("ext"))).Ok();
        (await Harness.Send(sp, "POST", AgentsRoot + "/ext/send", """{"mode":"followup","text":"do the work"}"""))
            .StatusCode.ShouldBe(202);

        await sp.GetRequiredService<AgentActivationWorker>().ActivateAsync(Key("ext"), Ct);

        var session = Node(await Harness.Send(sp, "GET", AgentsRoot + "/ext/sessions"))!["value"]!.AsArray()[0]!;
        var events = await ReadAsync(sp, session["properties"]!["streamId"]!.GetValue<string>());

        // The inbox arrived (that is the point of an external activation) …
        events.Count(e => e.Type == AgentInboxEvents.Delivered).ShouldBe(1);
        events.Count(e => e.Type == AgentSessionEvents.Message).ShouldBe(1, "the inbox message is the only transcript entry");
        // … and no turn happened: no assistant block, no tool, no cost.
        Node(await Harness.Send(sp, "GET", AgentsRoot + "/ext"))!["properties"]!["costUsd"]!.GetValue<decimal>().ShouldBe(0);
        events.ShouldNotContain(e => e.Type == AgentEventTypes.AssistantText);
        events.ShouldNotContain(e => e.Type == AgentEventTypes.ToolIntent);
    }

    [Fact]
    public async Task An_unknown_driver_is_rejected_at_put()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        var ctx = await Harness.Send(sp, "PUT", AgentsRoot + "/bad", """{"properties":{"displayName":"bad","driver":"robot"}}""");
        ctx.StatusCode.ShouldBe(400);
        ctx.Error!.Code.ShouldBe("InvalidRequestContent");
        ctx.Error.Details.ShouldContain(d => d.Code == "InvalidDriver");
    }

    [Fact]
    public async Task Escalation_answers_pending_over_mcp_and_waits_in_the_kernel_loop()
    {
        // The subject is the whole difference between the two callers: no wait means the caller is
        // answered with the approval id (M5.2 over MCP), a wait means the kernel's own activation
        // loop blocks and resumes on the decision.
        var approvals = new RecordingApprovals();
        var recorded = new List<string>();
        var mcp = new AgentGateSubject(
            "key", "job", "canonical", "en", "brief", "DevFullAuto", "dev", [], null, false, 0, 0m,
            TimeSpan.Zero, approvals, (id, _) =>
            {
                recorded.Add(id);
                return default;
            });

        var outcome = await mcp.OnEscalatedAsync("ap-1", Ct);
        outcome.ShouldBe(EscalationOutcome.Pending);
        recorded.ShouldBe(["ap-1"]);
        approvals.Waits.ShouldBe(0, "an MCP call is never pinned on WaitAsync");

        var kernel = mcp with { WaitFor = TimeSpan.FromSeconds(5) };
        outcome = await kernel.OnEscalatedAsync("ap-2", Ct);
        outcome.ShouldBe(EscalationOutcome.Approved);
        approvals.Waits.ShouldBe(1);
    }

    [Fact]
    public void The_pending_tool_result_names_the_code_and_the_approval()
    {
        var block = ToolBatchExecutor.PendingResult("tu-1", "ap-9");
        block.IsError.ShouldBeTrue();
        block.ToolUseId.ShouldBe("tu-1");
        block.Content["code"]!.GetValue<string>().ShouldBe("approval_pending");
        block.Content["approvalId"]!.GetValue<string>().ShouldBe("ap-9");
    }

    private sealed class RecordingApprovals : IApprovalService
    {
        public int Waits { get; private set; }

        public ValueTask<ApprovalOutcome> WaitAsync(string approvalId, CancellationToken ct)
        {
            Waits++;
            return new ValueTask<ApprovalOutcome>(new Approved(false, null, null));
        }

        public ValueTask<string> OpenAsync(ApprovalRequest request, CancellationToken ct) => new("ap");

        public ValueTask<ApprovalOutcome> ApproveAsync(string approvalId, string? note, string? grantScope, double? grantHours, CancellationToken ct, string? actorId = null) =>
            new((ApprovalOutcome)new Approved(false, null, null));

        public ValueTask<ApprovalOutcome> DenyAsync(string approvalId, string? note, CancellationToken ct, string? actorId = null) => new((ApprovalOutcome)new Denied(note));

        public ValueTask<ApprovalOutcome> AnswerAsync(string approvalId, string answer, CancellationToken ct, string? actorId = null) => new((ApprovalOutcome)new Answered(answer));

        public ValueTask<ApprovalOutcome> CancelAsync(string approvalId, CancellationToken ct, string? actorId = null) => new((ApprovalOutcome)new Cancelled());

        public ValueTask<ApprovalRecord?> GetAsync(string id, CancellationToken ct) => new((ApprovalRecord?)null);

        public ValueTask<PagedResult<ApprovalRecord>> ListAsync(ApprovalQuery query, CancellationToken ct) =>
            new(new PagedResult<ApprovalRecord>([], null));
    }

    private static string AgentBody(string name, string scenario, string workRoot) =>
        "{\"properties\":{\"displayName\":\"" + name + "\",\"profile\":\"" + scenario
        + "\",\"modelId\":\"" + AgentsConstants.DefaultModelId
        + "\",\"workspaceId\":\"" + workRoot.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"}}";

    private static string ExternalAgentBody(string name) =>
        "{\"properties\":{\"displayName\":\"" + name + "\",\"driver\":\"" + AgentDrivers.External + "\"}}";

    private static string NewWorkRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "burbuja-agent-work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Key(string name) => $"{AgentsRoot}/{name}".ToLowerInvariant();

    private static async Task<List<LedgerEvent>> ReadAsync(IServiceProvider sp, string streamId)
    {
        var log = sp.GetRequiredService<IEventLog>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var events = new List<LedgerEvent>();
        await foreach (var e in log.TailAsync(streamId, 0, cts.Token))
        {
            events.Add(e);
            if (e.Type == AgentActivationEvents.Settled)
                break;
        }

        return events;
    }

    private static JsonNode? Node(ArmRequestContext ctx) =>
        JsonSerializer.SerializeToNode(ctx.Result, JsonWire.Options);
}
