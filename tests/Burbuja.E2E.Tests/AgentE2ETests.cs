// Adapted from burbuja-server 97940d9; the fixture here is the independent generic kernel host.
using System.Net;
using System.Text.Json.Nodes;
using Burbuja.TestKit;
using Shouldly;
using Xunit;

namespace Burbuja.E2E.Tests;

/// <summary>
/// Group D: the Agents control plane and the stateful runtime through the published Host — jobs end
/// to end, the audit ordering around a deny, fan-out, approvals, teams CAS, adjacency, settlement
/// notices and SSE resume. Every name is GUID-suffixed; assertions are relative to a captured seq.
/// </summary>
[Trait("Category", "E2E")]
public sealed class AgentE2ETests(SharedServerFixture server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string ScenarioTag = "burbuja.io/scenario";

    // ---------------------------------------------------------------- D1–D3: jobs

    [Fact]
    public async Task D1_job_runs_to_success_and_books_cost()
    {
        using var client = server.Client();
        var ws = server.NewName("ws");
        var job = server.NewName("job");
        await ArmTestHelpers.PutWorkspaceAsync(client, ws, Ct);
        var path = await ArmTestHelpers.PutJobAsync(client, ws, job, "happy-path", null, null, Ct);

        var status = await ArmTestHelpers.WaitForJobAsync(client, path, "succeeded", 40, Ct);
        status.ShouldBe("succeeded");

        using var get = await client.GetAsync(path, Ct);
        var body = await ArmTestClient.ReadJsonAsync(get, Ct);
        body!["properties"]!["cost"]!["workerUsd"]!.GetValue<decimal>().ShouldBeGreaterThan(0);

        var events = await SseTail.CollectAsync(
            client, path + "/events", e => e.Type == "agents.result", TimeSpan.FromSeconds(10), ct: Ct);
        events.ShouldNotBeEmpty("the job stream must be readable after the fact");
        events.Select(e => e.Seq).ShouldBe(events.Select(e => e.Seq).OrderBy(s => s));
        events.ShouldContain(e => e.Type == "agents.tool.call");
        events.ShouldContain(e => e.Type == "agents.result");
    }

    [Fact]
    public async Task D2_hard_deny_is_audited_before_its_result()
    {
        using var client = server.Client();
        var ws = server.NewName("ws");
        await ArmTestHelpers.PutWorkspaceAsync(client, ws, Ct);
        var path = await ArmTestHelpers.PutJobAsync(client, ws, server.NewName("job"), "hard-deny-rm-rf", null, null, Ct);

        (await ArmTestHelpers.WaitForJobAsync(client, path, "succeeded", 40, Ct)).ShouldBe("succeeded");

        var events = await SseTail.CollectAsync(
            client, path + "/events", e => e.Type == "agents.result", TimeSpan.FromSeconds(15), ct: Ct);
        var denies = events.Where(e => e.Type == "agents.tool.denied").ToList();
        var results = events.Where(e => e.Type == "agents.tool.result").ToList();
        denies.Count.ShouldBeGreaterThanOrEqualTo(1);
        results.Count.ShouldBeGreaterThanOrEqualTo(1);
        denies[0].Seq.ShouldBeLessThan(results[0].Seq, "the deny must be written before the tool result");
        denies[0].Data!["class"]!.GetValue<string>().ShouldBe("destructive_fs");
    }

    [Fact]
    public async Task D3_fan_out_spawns_three_distinct_members_and_denies_one()
    {
        using var client = server.Client();
        var ws = server.NewName("ws");
        await ArmTestHelpers.PutWorkspaceAsync(client, ws, Ct);
        var path = await ArmTestHelpers.PutJobAsync(client, ws, server.NewName("job"), "fanout-plan-3", null, null, Ct);

        (await ArmTestHelpers.WaitForJobAsync(client, path, "succeeded", 40, Ct)).ShouldBe("succeeded");

        // Three subtasks run as three workers with distinct run ids, plus the merger.
        using (var runs = await client.GetAsync(path + "/runs", Ct))
        {
            runs.StatusCode.ShouldBe(HttpStatusCode.OK);
            var page = await ArmTestClient.ReadJsonAsync(runs, Ct);
            var rows = page!["value"]!.AsArray();
            rows.Count.ShouldBe(4, "three fan-out workers plus the merger");
            var roles = rows.Select(r => r!["properties"]!["role"]!.GetValue<string>()).ToList();
            roles.Count(r => r.Equals("worker", StringComparison.OrdinalIgnoreCase)).ShouldBe(3);
            roles.Count(r => r.Equals("merger", StringComparison.OrdinalIgnoreCase)).ShouldBe(1);
            rows.Select(r => r!["name"]!.GetValue<string>()).Distinct().Count().ShouldBe(4);
        }

        var events = await SseTail.CollectAsync(
            client, path + "/events", e => e.Type == "agents.result", TimeSpan.FromSeconds(20), ct: Ct);
        events.Count(e => e.Type == "agents.tool.denied").ShouldBe(1, "only the rm -rf subtask is destructive");
        events.Count(e => e.Type == "agents.tool.use").ShouldBeGreaterThanOrEqualTo(2);
        // The parallel writers never collide: their work is serialised through the job's history.
        events.Count(e => e.Type == "agents.result").ShouldBe(1);
    }

    // ---------------------------------------------------------------- D4: approval

    [Fact]
    public async Task D4_gray_decision_opens_one_approval_and_finishes_after_it()
    {
        using var client = server.Client();
        var ws = server.NewName("ws");
        await ArmTestHelpers.PutWorkspaceAsync(client, ws, Ct);
        var path = await ArmTestHelpers.PutJobAsync(client, ws, server.NewName("job"), "gray-supervisor-escalate-approved", null, null, Ct);

        var parked = await ArmTestHelpers.WaitForJobAsync(client, path, "awaitingApproval", 40, Ct);
        parked.ShouldBe("awaitingApproval");

        var approvalPath = await ArmTestHelpers.WaitForApprovalAsync(client, path, 30, Ct);
        approvalPath.ShouldNotBeNull();

        var callsBefore = await CountAsync(client, path + "/events", "agents.tool.call");
        using var approve = await client.PostAsync(approvalPath + "/approve", """{"note":"e2e ok"}""", Ct);
        approve.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await ArmTestHelpers.WaitForJobAsync(client, path, "succeeded", 40, Ct)).ShouldBe("succeeded");
        var callsAfter = await CountAsync(client, path + "/events", "agents.tool.call");
        callsAfter.ShouldBe(callsBefore + 1, "the approved action runs exactly once, on the second attempt");
    }

    // ---------------------------------------------------------------- D5: agents

    [Fact]
    public async Task D5_agent_send_is_async_deduped_and_settles()
    {
        using var client = server.Client();
        var agent = AgentPath(server.NewName("agent"));
        using (var put = await client.PutAsync(agent, """{"properties":{"displayName":"runner"}}""", Ct))
        {
            put.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
        }

        // Creating an agent never runs a turn.
        using (var idle = await client.GetAsync(agent, Ct))
        {
            var body = await ArmTestClient.ReadJsonAsync(idle, Ct);
            body!["properties"]!["state"]!.GetValue<string>().ShouldBe("idle");
            body["properties"]!["modelId"]!.GetValue<string>().ShouldBe("deepseek-flash");
        }

        using (var send = await client.PostAsync(
                   agent + "/send", """{"mode":"followup","text":"go","dedupKey":"k-1"}""", Ct))
        {
            send.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            send.Headers.Contains("Azure-AsyncOperation").ShouldBeTrue();
        }

        using (var replay = await client.PostAsync(
                   agent + "/send", """{"mode":"followup","text":"go","dedupKey":"k-1"}""", Ct))
        {
            replay.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await ArmTestClient.ReadJsonAsync(replay, Ct))!["accepted"]!.GetValue<bool>().ShouldBeFalse();
        }

        var settled = await WaitForAsync(async () =>
        {
            using var get = await client.GetAsync(agent, Ct);
            var body = await ArmTestClient.ReadJsonAsync(get, Ct);
            return body!["properties"]!["state"]!.GetValue<string>();
        }, "settled", TimeSpan.FromSeconds(20));
        settled.ShouldBe("settled");

        // The session's `turn` is a bounded WAIT, not an instant read, and why is worth writing down: the
        // agent's `settled` state is derived from `CurrentSessionId` while the session's `turn` counter is
        // written by the runner's own state update, so the two resources are not updated atomically and a
        // reader can catch them disagreeing for a moment. The compose lane caught exactly that (turn 0 beside
        // a settled agent) while the self-hosted lane never did — and the transient is inherent to two
        // resources without a transaction, so the fact waits for the pair to agree instead of demanding they
        // agree at the first instant.
        var list = await WaitForAsync(
            async () =>
            {
                using var sessions = await client.GetAsync(agent + "/sessions", Ct);
                var page = await ArmTestClient.ReadJsonAsync(sessions, Ct);
                var rows = page!["value"]!.AsArray();
                rows.Count.ShouldBe(1, "the dedup key is held by the target, so one wake creates one session");
                return rows[0]!["properties"]!["turn"]!.GetValue<int>().ToString();
            },
            "1",
            BrowserWait.Timeout);

        list.ShouldBe("1", "one wake is one turn");
        using (var sessions = await client.GetAsync(agent + "/sessions", Ct))
        {
            var page = await ArmTestClient.ReadJsonAsync(sessions, Ct);
            page!["value"]!.AsArray()[0]!["properties"]!["lastSeq"]!.GetValue<long>().ShouldBeGreaterThan(0);
        }
    }

    // ---------------------------------------------------------------- D11–D13: stateful tools

    [Fact]
    public async Task D11_stateful_agent_calls_a_tool_through_the_gate_and_books_cost()
    {
        using var client = server.Client();
        var workRoot = Path.Combine(Path.GetTempPath(), "burbuja-e2e-agent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);
        try
        {
            var agent = AgentPath(server.NewName("worker"));
            using (var put = await client.PutAsync(agent, AgentBody("worker", "agent-tool-loop", workRoot, driver: null), Ct))
            {
                put.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
            }

            using (var send = await client.PostAsync(agent + "/send", """{"mode":"followup","text":"write the note"}""", Ct))
            {
                send.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            }

            // M13 R0 — WAIT ON THE OBSERVABLE. `state == settled` is not the same question as "the turn's cost
            // is booked": the activation writes `settled` and only then adds the turn's cost to the agent
            // record, so reading `costUsd` the instant the state flips is a race — reported as part of `D11`
            // failing while a neighbour held the Host. Both halves are assertions this fact makes, so both are
            // what it waits for. The state is written once and does not go back, so `settled && priced` is a
            // state that stays true; the 60 s is a ceiling over that observable, not a latency claim (a
            // latency SLO would belong in the chaos lane, where a slow host is the subject rather than noise).
            var settled = await WaitForAsync(async () =>
            {
                using var get = await client.GetAsync(agent, Ct);
                var body = await ArmTestClient.ReadJsonAsync(get, Ct);
                var state = body!["properties"]!["state"]!.GetValue<string>();
                var cost = body["properties"]!["costUsd"]!.GetValue<decimal>();
                return state == "settled" && cost > 0 ? "settled and priced" : $"{state} (cost {cost})";
            }, "settled and priced", TimeSpan.FromSeconds(60));
            settled.ShouldBe("settled and priced");

            // The tool really ran. The file is only observable when the Host shares this machine: in
            // remote mode (the Pi lane) the agent's work root is inside the container, so the
            // filesystem check would be a test of the test. The audit trail below is the assertion
            // that holds in both modes.
            if (!server.IsRemote)
            {
                File.Exists(Path.Combine(workRoot, "note.txt")).ShouldBeTrue("the agent's write_file must reach the work root");
                File.ReadAllText(Path.Combine(workRoot, "note.txt")).ShouldBe("written by the agent");
            }

            var lane = await SessionEventsAsync(client, await SessionPathAsync(client, agent));

            var types = lane.Select(e => e.Type).ToList();
            types.ShouldContain("agents.tool.intent", "the session lane is " + string.Join(',', types));
            types.ShouldContain("agents.decision");
            var intent = lane.First(e => e.Type == "agents.tool.intent");
            var decision = lane.First(e => e.Type == "agents.decision");
            var call = lane.First(e => e.Type == "agents.tool.call");
            var result = lane.First(e => e.Type == "agents.tool.result");
            intent.Seq.ShouldBeLessThan(decision.Seq, "the intent is written before the verdict");
            decision.Seq.ShouldBeLessThan(call.Seq, "the tool runs only after the decision");
            call.Seq.ShouldBeLessThan(result.Seq);
            decision.Data!["tier"]!.GetValue<string>().ShouldBe("Allow");

            using var get2 = await client.GetAsync(agent, Ct);
            var body2 = await ArmTestClient.ReadJsonAsync(get2, Ct);
            body2!["properties"]!["costUsd"]!.GetValue<decimal>().ShouldBeGreaterThan(0, "the agent's turns are priced");
        }
        finally
        {
            try
            {
                Directory.Delete(workRoot, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task D12_stateful_agent_sending_to_a_non_adjacent_agent_is_hard_denied()
    {
        using var client = server.Client();
        var root = server.NewName("root");
        await PutAgentAsync(client, root, null);
        var runner = server.NewName("runner");
        var agent = AgentPath(runner);
        // One PUT: a second PUT would replace the properties and silently drop the parent, which is
        // exactly the fact the adjacency classifier needs.
        using (var put = await client.PutAsync(
                   agent,
                   AgentBody(runner, "adjacency-denied", null, driver: null, parentPath: AgentPath(root)),
                   Ct))
        {
            put.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
        }

        await PutAgentAsync(client, "sibling-agent", AgentPath(root));

        using (var send = await client.PostAsync(agent + "/send", """{"mode":"followup","text":"reach out"}""", Ct))
        {
            send.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        var settled = await WaitForAsync(async () =>
        {
            using var get = await client.GetAsync(agent, Ct);
            var body = await ArmTestClient.ReadJsonAsync(get, Ct);
            return body!["properties"]!["state"]!.GetValue<string>();
        }, "settled", TimeSpan.FromSeconds(30));
        settled.ShouldBe("settled");

        var lane = await SessionEventsAsync(client, await SessionPathAsync(client, agent));
        var denied = lane.Where(e => e.Type == "agents.tool.denied").ToList();
        denied.Count.ShouldBe(1, "an agent's turn may not reach a non-adjacent peer");
        denied[0].Data!["class"]!.GetValue<string>().ShouldBe("authority_violation");
        lane.ShouldNotContain(e => e.Type == "agents.approval", "adjacency is a hard deny, not a question");
    }

    [Fact]
    public async Task D13_external_agent_delivers_its_inbox_and_never_runs_a_turn()
    {
        using var client = server.Client();
        var agent = AgentPath(server.NewName("external"));
        using (var put = await client.PutAsync(agent, AgentBody("external", scenario: null, workRoot: null, driver: "external"), Ct))
        {
            put.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
        }

        using (var send = await client.PostAsync(agent + "/send", """{"mode":"followup","text":"work from outside"}""", Ct))
        {
            send.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        var settled = await WaitForAsync(async () =>
        {
            using var get = await client.GetAsync(agent, Ct);
            var body = await ArmTestClient.ReadJsonAsync(get, Ct);
            return body!["properties"]!["state"]!.GetValue<string>();
        }, "settled", TimeSpan.FromSeconds(30));
        settled.ShouldBe("settled");

        var lane = await SessionEventsAsync(client, await SessionPathAsync(client, agent));
        lane.Count(e => e.Type == "agents.inbox.delivered").ShouldBe(1, "the inbox is delivered to the session log");
        lane.ShouldNotContain(e => e.Type == "agents.assistant.text", "an external agent's turns happen in another process");
        lane.ShouldNotContain(e => e.Type == "agents.tool.intent");

        using var get2 = await client.GetAsync(agent, Ct);
        var body2 = await ArmTestClient.ReadJsonAsync(get2, Ct);
        body2!["properties"]!["costUsd"]!.GetValue<decimal>().ShouldBe(0);
    }

    // ---------------------------------------------------------------- D6: teams

    [Fact]
    public async Task D6_task_patch_race_yields_one_200_and_one_412()
    {
        using var client = server.Client();
        var team = TeamPath(server.NewName("team"));
        using (var put = await client.PutAsync(team, """{"properties":{"displayName":"board"}}""", Ct))
        {
            put.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
        }

        using (var task = await client.PutAsync(team + "/tasks/task-1", """{"properties":{"title":"one"}}""", Ct))
        {
            task.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
        }

        var first = client.Http.PatchAsync(
            ArmTestClient.Versioned(team + "/tasks/task-1"),
            Json("""{"properties":{"revision":1,"status":"running"}}"""),
            Ct);
        var second = client.Http.PatchAsync(
            ArmTestClient.Versioned(team + "/tasks/task-1"),
            Json("""{"properties":{"revision":1,"status":"done"}}"""),
            Ct);
        var responses = await Task.WhenAll(first, second);
        var codes = responses.Select(r => r.StatusCode).OrderBy(c => (int)c).ToList();
        codes.ShouldBe([HttpStatusCode.OK, HttpStatusCode.PreconditionFailed]);
        foreach (var response in responses)
            response.Dispose();

        // A PATCH with no revision at all is a 400, never a silent overwrite.
        using (var noRevision = await client.Http.PatchAsync(
                   ArmTestClient.Versioned(team + "/tasks/task-1"),
                   Json("""{"properties":{"status":"blocked"}}"""),
                   Ct))
        {
            noRevision.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        // A blockedBy cycle is refused at write time.
        using (var a = await client.PutAsync(team + "/tasks/a", """{"properties":{"title":"a"}}""", Ct))
        {
            a.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
        }

        using (var cycle = await client.PutAsync(
                   team + "/tasks/b", """{"properties":{"title":"b","blockedBy":["a"]}}""", Ct))
        {
            cycle.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
        }

        using (var back = await client.PutAsync(
                   team + "/tasks/a", """{"properties":{"title":"a","blockedBy":["b"]}}""", Ct))
        {
            back.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await ArmTestClient.ReadJsonAsync(back, Ct))!["error"]!["code"]!.GetValue<string>().ShouldBe("BoardCycle");
        }
    }

    // ---------------------------------------------------------------- D7: adjacency

    [Fact]
    public async Task D7_non_adjacent_send_is_hard_denied()
    {
        using var client = server.Client();
        var ws = server.NewName("ws");
        await ArmTestHelpers.PutWorkspaceAsync(client, ws, Ct);
        await PutAgentAsync(client, "parent", null);
        await PutAgentAsync(client, "runner", AgentPath("parent"));
        await PutAgentAsync(client, "sibling-agent", AgentPath("parent"));

        var body = "{\"tags\":{\"" + ScenarioTag + "\":\"adjacency-denied\"},\"properties\":{"
                   + "\"agentId\":\"" + AgentPath("runner").ToLowerInvariant() + "\","
                   + "\"spec\":{\"kind\":\"implement\",\"title\":\"t1\",\"slug\":\"reach\",\"brief\":\"reach out\","
                   + "\"language\":\"es\",\"targetEnv\":\"dev\",\"autoAccept\":true}}}";
        using var put = await client.PutAsync(ArmTestHelpers.JobPath(ws, "job"), body, Ct);
        put.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var path = ArmTestHelpers.JobPath(ws, "job");

        (await ArmTestHelpers.WaitForJobAsync(client, path, "succeeded", 40, Ct)).ShouldBe("succeeded");

        var events = await SseTail.CollectAsync(
            client, path + "/events", e => e.Type == "agents.result", TimeSpan.FromSeconds(15), ct: Ct);
        var denied = events.Where(e => e.Type == "agents.tool.denied").ToList();
        denied.Count.ShouldBe(1);
        denied[0].Data!["class"]!.GetValue<string>().ShouldBe("authority_violation");
        events.ShouldNotContain(e => e.Type == "agents.approval", "adjacency is a hard deny, not a question");

        // The sibling never received the message.
        using var sibling = await client.GetAsync(AgentPath("sibling-agent"), Ct);
        var siblingBody = await ArmTestClient.ReadJsonAsync(sibling, Ct);
        (siblingBody!["properties"]!["inbox"]?.AsArray().Count ?? 0).ShouldBe(0);
    }

    // ---------------------------------------------------------------- D8: settlement

    [Fact]
    public async Task D8_each_member_writes_exactly_one_settlement_notice()
    {
        using var client = server.Client();
        var lead = server.NewName("lead");
        var parent = AgentPath(lead);
        await PutAgentAsync(client, lead, null);

        var members = new[] { "m-one", "m-two", "m-three" };
        foreach (var member in members)
            await PutAgentAsync(client, member, parent);

        foreach (var member in members)
        {
            using var send = await client.PostAsync(
                AgentPath(member) + "/send", """{"mode":"followup","text":"work"}""", Ct);
            send.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        await WaitForAsync(async () =>
        {
            using var get = await client.GetAsync(parent, Ct);
            var body = await ArmTestClient.ReadJsonAsync(get, Ct);
            return (body!["properties"]!["inbox"] as JsonArray)?.Count.ToString() ?? "0";
        }, "3", TimeSpan.FromSeconds(30));

        // Waking a settled member again must not add a second notice.
        using (var send = await client.PostAsync(
                   AgentPath(members[0]) + "/send", """{"mode":"followup","text":"again"}""", Ct))
        {
            send.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        await Task.Delay(1500, Ct);
        using var final = await client.GetAsync(parent, Ct);
        var leadBody = await ArmTestClient.ReadJsonAsync(final, Ct);
        var notices = (leadBody!["properties"]!["inbox"] as JsonArray ?? [])
            .Where(i => i!["source"]!.GetValue<string>() == "subagentSettled")
            .ToList();
        notices.Count.ShouldBe(3, "one notice per member, no duplicates");
        notices.Select(n => n!["dedupKey"]!.GetValue<string>()).Distinct().Count().ShouldBe(3);
    }

    // ---------------------------------------------------------------- D9: authz + bounds

    [Fact]
    public async Task D9_sessions_are_immutable_and_context_cards_are_bounded()
    {
        using var client = server.Client();
        var bounded = server.NewName("bounded");
        var agent = AgentPath(bounded);
        await PutAgentAsync(client, bounded, null);

        // Sessions are the runtime's, not the caller's.
        using (var put = await client.PutAsync(agent + "/sessions/s1", """{"properties":{}}""", Ct))
        {
            put.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        }

        var huge = new string('x', 9000);
        using (var over = await client.PutAsync(
                   agent + "/contexts/big", "{\"properties\":{\"label\":\"handoff\",\"body\":\"" + huge + "\"}}", Ct))
        {
            over.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var ok = await client.PutAsync(
                   agent + "/contexts/small", """{"properties":{"label":"handoff","body":"small"}}""", Ct))
        {
            ok.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
        }

        // A reader can read the control plane but cannot create resources.
        using var reader = server.Client(SharedServerFixture.ReaderKey);
        using (var read = await reader.GetAsync(agent, Ct))
        {
            read.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var write = await reader.PutAsync(
                   AgentPath(server.NewName("reader-made")), """{"properties":{"displayName":"nope"}}""", Ct))
        {
            write.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }

    // ---------------------------------------------------------------- D10: SSE resume

    [Fact]
    public async Task D10_sse_resume_from_last_event_id_has_no_duplicate_and_no_gap()
    {
        using var client = server.Client();
        var ws = server.NewName("ws");
        await ArmTestHelpers.PutWorkspaceAsync(client, ws, Ct);
        var path = await ArmTestHelpers.PutJobAsync(client, ws, server.NewName("job"), "happy-path", null, null, Ct);
        (await ArmTestHelpers.WaitForJobAsync(client, path, "succeeded", 40, Ct)).ShouldBe("succeeded");

        var first = await SseTail.CollectAsync(
            client, path + "/events", e => e.Type == "agents.result", TimeSpan.FromSeconds(10), ct: Ct);
        first.Count.ShouldBeGreaterThan(3);

        var midpoint = first[first.Count / 2].Seq;
        var resumed = await SseTail.CollectAsync(
            client, path + "/events", e => e.Type == "agents.result", TimeSpan.FromSeconds(10), midpoint, Ct);

        resumed.ShouldNotBeEmpty("resuming from a captured id must replay what came after it");
        resumed.Select(e => e.Seq).ShouldBe(resumed.Select(e => e.Seq).Distinct(), "no duplicate frames");
        resumed[0].Seq.ShouldBeGreaterThan(midpoint);

        var expected = first.Where(e => e.Seq > midpoint).Select(e => e.Seq).ToList();
        resumed.Select(e => e.Seq).ShouldBe(expected, "no gap: the resumed lane is exactly the tail");
    }

    // ---------------------------------------------------------------- helpers
    //
    // M13 R0: `PutWorkspaceAsync`, `PutJobAsync`, `WaitForJobAsync` and `WaitForApprovalAsync` used to be
    // defined HERE, byte-identical to LedgerE2ETests' copies and to the browser group's. They now live in
    // `ArmTestHelpers`; the call sites above say `ArmTestHelpers.` so there is exactly one body each. What
    // stays here is what is genuinely this file's: the agent recipes and the path builders.

    private const string AgentsRoot = ArmTestHelpers.AgentsRoot;

    private static string AgentPath(string agent) => AgentsRoot + "/agents/" + agent;

    private static string TeamPath(string team) => AgentsRoot + "/teams/" + team;

    private static StringContent Json(string json) =>
        new(json, System.Text.Encoding.UTF8, "application/json");

    private static async Task PutAgentAsync(ArmTestClient client, string name, string? parentPath)
    {
        var parent = parentPath is null ? "" : ",\"parentAgentId\":\"" + parentPath.ToLowerInvariant() + "\"";
        using var put = await client.PutAsync(
            AgentPath(name),
            "{\"properties\":{\"displayName\":\"" + name + "\"" + parent + "}}",
            Ct);
        put.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
    }

    /// <summary>The `agents` PUT body the stateful-tool scenarios need.</summary>
    private static string AgentBody(string name, string? scenario, string? workRoot, string? driver, string? parentPath = null)
    {
        var parts = new List<string> { "\"displayName\":\"" + name + "\"" };
        if (scenario is not null)
            parts.Add("\"profile\":\"" + scenario + "\"");
        if (workRoot is not null)
            parts.Add("\"workspaceId\":\"" + workRoot.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"");
        if (driver is not null)
            parts.Add("\"driver\":\"" + driver + "\"");
        if (parentPath is not null)
            parts.Add("\"parentAgentId\":\"" + parentPath.ToLowerInvariant() + "\"");
        // The stateful runtime's default model is deepseek-flash, which this host prices only when
        // the DeepSeek pricebook is composed in (M5.1 slice C). Until then the priced Sonnet id is
        // the honest choice for a cost assertion, and the scripted provider ignores the model.
        parts.Add("\"modelId\":\"" + PricedModelId + "\"");
        return "{\"properties\":{" + string.Join(',', parts) + "}}";
    }

    private const string PricedModelId = "us.anthropic.claude-sonnet-4-5-20250929-v1:0";

    /// <summary>The only session of an agent (the runtime creates one per agent on first wake).</summary>
    private static async Task<string> SessionPathAsync(ArmTestClient client, string agentPath)
    {
        using var sessions = await client.GetAsync(agentPath + "/sessions", Ct);
        var page = await ArmTestClient.ReadJsonAsync(sessions, Ct);
        var list = page!["value"]!.AsArray();
        list.Count.ShouldBeGreaterThanOrEqualTo(1);
        return agentPath + "/sessions/" + list[0]!["name"]!.GetValue<string>();
    }

    /// <summary>
    /// The stateful runtime writes turns, inbox delivery and gate verdicts on the session's own
    /// stream, and <c>agents/sessions</c> declares an <c>events</c> route, so this is the lane that
    /// carries them.
    ///
    /// <para>M13 R0: the 10 s bound here is a CEILING over an observable, not the assertion —
    /// <c>CollectAsync</c> tails from the beginning of the stream, so an activation that already settled
    /// returns immediately and only a slow host can spend the budget. D11's wall-clock flake was not here: it
    /// was the cost read that raced `state: settled` (see that fact's own note).</para>
    /// </summary>
    private static async Task<List<SseEvent>> SessionEventsAsync(ArmTestClient client, string sessionPath) =>
        await SseTail.CollectAsync(
            client,
            sessionPath + "/events",
            e => e.Type == "agents.activation.settled",
            TimeSpan.FromSeconds(10),
            ct: Ct);

    private static async Task<int> CountAsync(ArmTestClient client, string eventsPath, string type) =>
        (await SseTail.CollectAsync(client, eventsPath, _ => false, TimeSpan.FromSeconds(6), ct: Ct))
            .Count(e => e.Type == type);

    private static async Task<string> WaitForAsync(Func<Task<string>> probe, string expected, TimeSpan budget)
    {
        var deadline = DateTimeOffset.UtcNow + budget;
        var last = "unknown";
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await probe();
            if (last.Equals(expected, StringComparison.OrdinalIgnoreCase))
                return last;
            await Task.Delay(250, Ct);
        }

        return last;
    }
}
