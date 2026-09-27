using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents;
using Burbuja.Agents.Abstractions.Agents;
using Burbuja.Agents.Runtime;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Burbuja.Agents.UnitTests;

public sealed class ActivationWorkerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string AgentsRoot =
        "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Agents/agents";

    [Fact]
    public async Task Wake_delivers_the_inbox_runs_a_turn_and_settles()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", AgentsRoot + "/a1", """{"properties":{"displayName":"a1"}}""")).Ok();
        (await Harness.Send(sp, "POST", AgentsRoot + "/a1/send", """{"mode":"followup","text":"go"}""")).StatusCode.ShouldBe(202);

        await sp.GetRequiredService<AgentActivationWorker>().ActivateAsync(Key("a1"), Ct);

        var agent = Node(await Harness.Send(sp, "GET", AgentsRoot + "/a1"));
        agent!["properties"]!["inbox"]!.AsArray().Count.ShouldBe(0, "the inbox is drained into the session log");
        agent["properties"]!["currentSessionId"]!.GetValue<string>().ShouldStartWith("session-");

        var sessions = Node(await Harness.Send(sp, "GET", AgentsRoot + "/a1/sessions"))!["value"]!.AsArray();
        sessions.Count.ShouldBe(1);
        sessions[0]!["properties"]!["turn"]!.GetValue<int>().ShouldBe(1);
        sessions[0]!["properties"]!["lastSeq"]!.GetValue<long>().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Fork_creates_a_new_session_seeded_at_the_boundary()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", AgentsRoot + "/a1", """{"properties":{"displayName":"a1"}}""")).Ok();
        (await Harness.Send(sp, "POST", AgentsRoot + "/a1/send", """{"mode":"followup","text":"go"}""")).StatusCode.ShouldBe(202);

        var worker = sp.GetRequiredService<AgentActivationWorker>();
        await worker.ActivateAsync(Key("a1"), Ct);
        var forkId = await worker.ForkAsync(Key("a1"), "session-forked", Ct);

        forkId.ShouldBe("session-forked");
        var sessions = Node(await Harness.Send(sp, "GET", AgentsRoot + "/a1/sessions"))!["value"]!.AsArray();
        sessions.Count.ShouldBe(2);
        var forked = sessions.First(s => s!["name"]!.GetValue<string>() == "session-forked")!;
        var props = forked["properties"]!;
        props["forkedFromSessionId"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        props["forkedAtSeq"]!.GetValue<long>().ShouldBeGreaterThan(0);
        props["endSeedSeq"]!.GetValue<long>().ShouldBe(props["forkedAtSeq"]!.GetValue<long>());
    }

    [Fact]
    public async Task Settlement_notice_is_written_to_the_parent_once()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        var parentId = AgentsRoot + "/parent";
        (await Harness.Send(sp, "PUT", parentId, """{"properties":{"displayName":"parent"}}""")).Ok();
        (await Harness.Send(sp, "PUT", AgentsRoot + "/child",
            "{\"properties\":{\"displayName\":\"child\",\"parentAgentId\":\"" + parentId.ToLowerInvariant() + "\"}}")).Ok();

        var worker = sp.GetRequiredService<AgentActivationWorker>();
        await worker.ActivateAsync(Key("child"), Ct);

        var parent = Node(await Harness.Send(sp, "GET", parentId));
        var inbox = parent!["properties"]!["inbox"]!.AsArray();
        inbox.Count.ShouldBe(1);
        inbox[0]!["source"]!.GetValue<string>().ShouldBe("subagentSettled");
        inbox[0]!["dedupKey"]!.GetValue<string>().ShouldBe("settled:child");
    }

    [Fact]
    public async Task Only_one_activation_runs_even_if_woken_twice()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", AgentsRoot + "/a1", """{"properties":{"displayName":"a1"}}""")).Ok();
        (await Harness.Send(sp, "POST", AgentsRoot + "/a1/send", """{"mode":"followup","text":"go"}""")).StatusCode.ShouldBe(202);

        var worker = sp.GetRequiredService<AgentActivationWorker>();
        await worker.ActivateAsync(Key("a1"), Ct);
        await worker.ActivateAsync(Key("a1"), Ct);

        var sessions = Node(await Harness.Send(sp, "GET", AgentsRoot + "/a1/sessions"))!["value"]!.AsArray();
        sessions.Count.ShouldBe(1, "the session is reused, not recreated");
        sessions[0]!["properties"]!["turn"]!.GetValue<int>().ShouldBe(2, "the second wake runs a second turn");
    }

    [Fact]
    public async Task Activation_emits_session_and_settlement_events()
    {        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", AgentsRoot + "/a1", """{"properties":{"displayName":"a1"}}""")).Ok();
        (await Harness.Send(sp, "POST", AgentsRoot + "/a1/send", """{"mode":"followup","text":"go"}""")).StatusCode.ShouldBe(202);
        await sp.GetRequiredService<AgentActivationWorker>().ActivateAsync(Key("a1"), Ct);

        var session = Node(await Harness.Send(sp, "GET", AgentsRoot + "/a1/sessions"))!["value"]!.AsArray()[0]!;
        var streamId = session["properties"]!["streamId"]!.GetValue<string>();

        var log = sp.GetRequiredService<IEventLog>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var types = new List<string>();
        await foreach (var e in log.TailAsync(streamId, 0, cts.Token))
        {
            types.Add(e.Type);
            if (e.Type == AgentActivationEvents.Settled)
                break;
        }

        types.ShouldContain(AgentSessionEvents.Started);
        types.ShouldContain(AgentSessionEvents.Message);
        types.ShouldContain(AgentInboxEvents.Delivered);
        types.ShouldContain(AgentActivationEvents.Settled);
    }

    /// <summary>
    /// M7.0b, the M6.0 wedge itself. <c>agents.activation</c> used to be delivered on the bus's one
    /// serial pump, and <see cref="AgentGateSubject.OnEscalatedAsync"/> waits up to
    /// <c>Runtime:AgentApprovalWait</c> (two minutes by default) for a human — so one unanswered
    /// approval held every later agent, and in the E2E lane every later job sat <c>queued</c>.
    ///
    /// <para>The assertion is the ordering, not a duration: agent <c>free</c> must settle while
    /// agent <c>blocked</c> is demonstrably still parked on its approval.</para>
    /// </summary>
    [Fact]
    public async Task A_parked_activation_does_not_hold_a_later_agents_wake()
    {
        using var sp = Harness.CreateHost(settings: new Dictionary<string, string?>
        {
            // Long enough that "free" has to finish while "blocked" is still inside its window.
            ["Runtime:AgentApprovalWait"] = "00:00:15",
            ["Runtime:WorkerConcurrency"] = "4",
        });
        await Harness.SeedAsync(sp);

        (await Harness.Send(sp, "PUT", AgentsRoot + "/blocked",
            """{"properties":{"displayName":"blocked","profile":"agent-escalate-parked"}}""")).Ok();
        (await Harness.Send(sp, "PUT", AgentsRoot + "/free",
            """{"properties":{"displayName":"free"}}""")).Ok();

        // The bus and the outbox relay have to actually run: this fact is about the pump, not about
        // a hand-called ActivateAsync (which the other facts here use and which bypasses it).
        var hosted = sp.GetServices<IHostedService>().ToList();
        foreach (var service in hosted)
            await service.StartAsync(Ct);

        // Both wakes travel the real path — outbox → relay → agents.activation → the dispatcher —
        // and the first one parks inside the approval window.
        (await Harness.Send(sp, "POST", AgentsRoot + "/blocked/send", """{"mode":"followup","text":"go"}"""))
            .StatusCode.ShouldBe(202);
        (await Harness.Send(sp, "POST", AgentsRoot + "/free/send", """{"mode":"followup","text":"go"}"""))
            .StatusCode.ShouldBe(202);

        // 1. The first wake parks on its approval. The agent resource's pendingApprovalId is only
        //    persisted once the wait returns, so the approval record is the observable that proves
        //    the activation is inside its window right now.
        var approvals = sp.GetRequiredService<IApprovalService>();
        string? pendingId = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTime.UtcNow < deadline && pendingId is null)
        {
            var page = await approvals.ListAsync(new ApprovalQuery(null, null, null, null, true, 20, null), Ct);
            pendingId = page.Value.FirstOrDefault()?.Id;
            if (pendingId is null)
                await Task.Delay(50, Ct);
        }

        pendingId.ShouldNotBeNullOrEmpty(
            "'blocked' never parked on an approval — the scenario did not escalate");

        // 2. The second agent is already done, and the first has still not settled: before M7.0b the
        //    serial pump made these mutually exclusive for the whole approval window.
        //
        //    M13 R0 — the WAIT IS ON THE OBSERVABLE, not on a sleep. `free/sessions` IS the observable
        //    ("free settled while blocked is parked"), and `pendingId` is re-checked on every pass so the
        //    overlap this fact claims is still true at the moment it is asserted. The first version read the
        //    sessions ONCE, immediately after the blocked agent was seen to park; under parallel load the free
        //    agent's wake had not written its turn yet, so the fact failed on a race it never meant to test.
        //    Polling WITHOUT the re-check would have been worse: it would pass after the approval window
        //    closed, which is exactly the state this fact says cannot happen.
        var freeDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        var freeSessions = new JsonArray();
        var stillParked = false;
        while (DateTime.UtcNow < freeDeadline)
        {
            var pending = await approvals.ListAsync(new ApprovalQuery(null, null, null, null, true, 20, null), Ct);
            stillParked = pending.Value.Any(a => string.Equals(a.Id, pendingId, StringComparison.Ordinal));
            freeSessions = Node(await Harness.Send(sp, "GET", AgentsRoot + "/free/sessions"))!["value"]!.AsArray();
            if (!stillParked || (freeSessions.Count == 1 && (freeSessions[0]!["properties"]!["turn"]?.GetValue<int>() ?? 0) >= 1))
                break;
            await Task.Delay(25, Ct);
        }

        stillParked.ShouldBeTrue(
            "'blocked' left its approval window before 'free' settled, so this fact never observed the overlap it is about");
        freeSessions.Count.ShouldBe(1);
        (freeSessions[0]!["properties"]!["turn"]?.GetValue<int>() ?? 0).ShouldBeGreaterThanOrEqualTo(1,
            "'free' must settle while 'blocked' is parked");

        var blockedSessions = Node(await Harness.Send(sp, "GET", AgentsRoot + "/blocked/sessions"))!["value"]!.AsArray();
        blockedSessions.Count.ShouldBe(1);
        (blockedSessions[0]!["properties"]!["turn"]?.GetValue<int>() ?? 0).ShouldBe(0,
            "'blocked' is parked inside its approval window, so its turn has not completed");

        // Leave nothing parked behind the test.
        await approvals.ApproveAsync(pendingId, "test cleanup", null, null, CancellationToken.None);
    }

    private static string Key(string name) => $"{AgentsRoot}/{name}".ToLowerInvariant();

    private static JsonNode? Node(ArmRequestContext ctx) =>
        JsonSerializer.SerializeToNode(ctx.Result, JsonWire.Options);
}
