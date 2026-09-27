using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Domain;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Agents.Abstractions.Nodes;
using Burbuja.Agents.Runtime;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Leases;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Burbuja.Agents.UnitTests;

public sealed class ScenarioTests
{
    [Fact]
    public async Task Happy_path_succeeds_with_commit_and_cost()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        var put = await Harness.Send(sp, "PUT", Harness.JobPath, Harness.JobBody("happy-path"));
        put.StatusCode.ShouldBe(202);
        await Harness.RunJobAsync(sp, Harness.JobPath, TestContext.Current.CancellationToken);
        var get = await Harness.Send(sp, "GET", Harness.JobPath);
        get.Ok();
        get.AsJson()["properties"]!["status"]!.GetValue<string>().ShouldBe("succeeded");
        var events = await Events(sp, Harness.JobPath);
        SeqIncreasing(events);
        var cost = get.AsJson()["properties"]!["cost"]!["workerUsd"]!.GetValue<decimal>();
        cost.ShouldBeGreaterThan(0);
        var calculator = new CostCalculator();
        var expected = calculator.Quote(AgentsConstants.DefaultModelId, new Usage(100, 50, 0, 0))
                       + calculator.Quote(AgentsConstants.DefaultModelId, new Usage(100, 50, 0, 0));
        cost.ShouldBe(expected);
    }

    [Fact]
    public async Task Hard_deny_rm_rf_continues_and_supervisor_cost_is_zero()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", Harness.JobPath, Harness.JobBody("hard-deny-rm-rf"))).Ok();
        await Harness.RunJobAsync(sp, Harness.JobPath, TestContext.Current.CancellationToken);
        var get = await Harness.Send(sp, "GET", Harness.JobPath);
        get.AsJson()["properties"]!["status"]!.GetValue<string>().ShouldBe("succeeded");
        get.AsJson()["properties"]!["cost"]!["supervisorUsd"]!.GetValue<decimal>().ShouldBe(0);
        var events = await Events(sp, Harness.JobPath);
        var denies = events.Where(e => e.Type == AgentEventTypes.ToolDenied).ToList();
        denies.Count.ShouldBe(2);
        var results = events.Where(e => e.Type == AgentEventTypes.ToolResult).ToList();
        results.Count.ShouldBeGreaterThanOrEqualTo(2);
        denies[0].Seq.ShouldBeLessThan(results[0].Seq);
        denies[1].Seq.ShouldBeLessThan(results[1].Seq);
        denies[0].Summary.ShouldContain("Burbuja bloqueó");
        foreach (var r in results)
            (r.Payload is JsonObject o && o["is_error"]?.GetValue<bool>() == true).ShouldBeTrue();
    }

    [Fact]
    public async Task Gray_supervisor_allow_has_no_approval()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", Harness.JobPath, Harness.JobBody("gray-supervisor-allow"))).Ok();
        await Harness.RunJobAsync(sp, Harness.JobPath, TestContext.Current.CancellationToken);
        var events = await Events(sp, Harness.JobPath);
        var sup = events.Single(e => e.Type == AgentEventTypes.Supervisor);
        sup.Summary.ShouldBe("Allow");
        sup.Payload!["confidence"]!.GetValue<double>().ShouldBeGreaterThanOrEqualTo(0.6);
        events.Count(e => e.Type == AgentEventTypes.Approval).ShouldBe(0);
        (await Harness.Send(sp, "GET", Harness.JobPath)).AsJson()["properties"]!["status"]!.GetValue<string>().ShouldBe("succeeded");
    }

    [Fact]
    public async Task Gray_supervisor_escalate_then_approve()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", Harness.JobPath, Harness.JobBody("gray-supervisor-escalate-approved"))).Ok();
        var run = Harness.RunJobAsync(sp, Harness.JobPath, TestContext.Current.CancellationToken);
        string? approvalId = null;
        for (var i = 0; i < 80 && approvalId is null; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            var recs = await sp.GetRequiredService<IApprovalService>().ListAsync(new ApprovalQuery(null, null, null, null, true, 20, null), TestContext.Current.CancellationToken);
            approvalId = recs.Value.FirstOrDefault()?.Id;
        }

        approvalId.ShouldNotBeNull();
        await sp.GetRequiredService<IApprovalService>().ApproveAsync(approvalId, "ok", null, null, TestContext.Current.CancellationToken);
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var events = await Events(sp, Harness.JobPath);
        var approval = events.Last(e => e.Type == AgentEventTypes.Approval && e.Summary == "approved");
        var call = events.First(e => e.Type == AgentEventTypes.ToolCall);
        approval.Seq.ShouldBeLessThan(call.Seq);
        (await Harness.Send(sp, "GET", Harness.JobPath)).AsJson()["properties"]!["status"]!.GetValue<string>().ShouldBe("succeeded");
    }

    [Fact]
    public async Task Gray_zone_defer_then_approve_is_second_attempt()
    {
        using var sp = Harness.CreateHost(settings: new Dictionary<string, string?> { ["Policy:DeferAfter"] = "00:00:00.200" });
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", Harness.JobPath, Harness.JobBody("gray-zone-escalate-defer"))).Ok();
        await Harness.RunJobAsync(sp, Harness.JobPath, TestContext.Current.CancellationToken);
        var parked = await Harness.Send(sp, "GET", Harness.JobPath);
        parked.AsJson()["properties"]!["status"]!.GetValue<string>().ShouldBe("awaitingApproval");
        parked.AsJson()["properties"]!["state"]!["transcript"].ShouldNotBeNull();
        var approvalId = parked.AsJson()["properties"]!["pendingApprovalId"]!.GetValue<string>();
        await sp.GetRequiredService<IApprovalService>().ApproveAsync(approvalId, "ok", null, null, TestContext.Current.CancellationToken);
        await Harness.RunJobAsync(sp, Harness.JobPath, TestContext.Current.CancellationToken);
        var done = await Harness.Send(sp, "GET", Harness.JobPath);
        done.AsJson()["properties"]!["status"]!.GetValue<string>().ShouldBe("succeeded");
        done.AsJson()["properties"]!["runAttempts"]!.GetValue<int>().ShouldBe(2);
    }

    [Fact]
    public async Task Budget_auto_extend_books_once_per_turn()
    {
        using var sp = Harness.CreateHost(settings: new Dictionary<string, string?> { ["Budget:DefaultJobUsd"] = "1" });
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", Harness.JobPath, Harness.JobBody("budget-auto-extend"))).Ok();
        await Harness.RunJobAsync(sp, Harness.JobPath, TestContext.Current.CancellationToken);
        var get = await Harness.Send(sp, "GET", Harness.JobPath);
        get.AsJson()["properties"]!["status"]!.GetValue<string>().ShouldBe("succeeded");
        var events = await Events(sp, Harness.JobPath);
        events.ShouldContain(e => e.Type == AgentEventTypes.BudgetExceeded);
        events.ShouldContain(e => e.Type == AgentEventTypes.BudgetExtended);
    }

    [Fact]
    public async Task Max_turns_stops_after_eight()
    {
        using var sp = Harness.CreateHost(settings: new Dictionary<string, string?> { ["Runtime:MaxTurns"] = "8" });
        await Harness.SeedAsync(sp);
        var spec = """{"kind":"implement","title":"t1","slug":"job","brief":"loop","language":"en","maxTurns":8,"autoAccept":true}""";
        (await Harness.Send(sp, "PUT", Harness.JobPath, Harness.JobBody("max-turns", spec))).Ok();
        await Harness.RunJobAsync(sp, Harness.JobPath, TestContext.Current.CancellationToken);
        var get = await Harness.Send(sp, "GET", Harness.JobPath);
        get.AsJson()["properties"]!["status"]!.GetValue<string>().ShouldBe("turnsExceeded");
        get.AsJson()["properties"]!["state"]!["turn"]!.GetValue<int>().ShouldBe(8);
        var lease = await sp.GetRequiredService<ILeaseStore>().GetAsync(AgentsConstants.LeaseId(Harness.JobPath.ToLowerInvariant()), TestContext.Current.CancellationToken);
        lease.ShouldBeNull();
    }

    [Fact]
    public async Task Cancel_mid_run_completes_quickly()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", Harness.JobPath, Harness.JobBody("cancel-mid-run"))).Ok();
        var run = Harness.RunJobAsync(sp, Harness.JobPath, TestContext.Current.CancellationToken);
        await Task.Delay(80, TestContext.Current.CancellationToken);
        (await Harness.Send(sp, "POST", Harness.JobPath + "/cancel", "{}")).Ok();
        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var get = await Harness.Send(sp, "GET", Harness.JobPath);
        get.AsJson()["properties"]!["status"]!.GetValue<string>().ShouldBe("cancelled");
        var lease = await sp.GetRequiredService<ILeaseStore>().GetAsync(AgentsConstants.LeaseId(Harness.JobPath.ToLowerInvariant()), TestContext.Current.CancellationToken);
        lease.ShouldBeNull();
    }

    [Fact]
    public async Task Steering_is_applied_before_next_tool_call()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", Harness.JobPath, Harness.JobBody("steering"))).Ok();
        (await Harness.Send(sp, "POST", Harness.JobPath + "/steer", """{"properties":{"text":"usa pytest"}}""")).Ok();
        await Harness.RunJobAsync(sp, Harness.JobPath, TestContext.Current.CancellationToken);
        var events = await Events(sp, Harness.JobPath);
        var steer = events.First(e => e.Type == AgentEventTypes.SteeringReceived);
        var tools = events.Where(e => e.Type == AgentEventTypes.ToolCall || e.Type == AgentEventTypes.ToolUse).ToList();
        tools.Count.ShouldBeGreaterThan(1);
        steer.Seq.ShouldBeLessThan(tools[1].Seq);
        (await Harness.Send(sp, "GET", Harness.JobPath)).AsJson()["properties"]!["status"]!.GetValue<string>().ShouldBe("succeeded");
    }

    [Fact]
    public async Task Throttle_storm_drops_then_recovers_capacity()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", Harness.JobPath, Harness.JobBody("throttle-storm"))).Ok();
        await Harness.RunJobAsync(sp, Harness.JobPath, TestContext.Current.CancellationToken);
        var nodes = sp.GetRequiredService<INodeDirectory>();
        var snap = nodes.Get("node-a");
        snap.ShouldNotBeNull();
        snap.Capacity.Current.ShouldBe(snap.Capacity.Configured);
        (await Harness.Send(sp, "GET", Harness.JobPath)).AsJson()["properties"]!["status"]!.GetValue<string>().ShouldBe("succeeded");
    }

    [Fact]
    public async Task Fanout_plan_three_denies_rm_independently()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", Harness.JobPath, Harness.JobBody("fanout-plan-3"))).Ok();
        await Harness.RunJobAsync(sp, Harness.JobPath, TestContext.Current.CancellationToken);
        var get = await Harness.Send(sp, "GET", Harness.JobPath);
        get.AsJson()["properties"]!["status"]!.GetValue<string>().ShouldBe("succeeded");
        var events = await Events(sp, Harness.JobPath);
        events.Count(e => e.Type == AgentEventTypes.ToolDenied).ShouldBe(1);
        events.Count(e => e.Type == AgentEventTypes.Decision).ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task Redaction_strips_akia_from_events()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", Harness.JobPath, Harness.JobBody("redaction"))).Ok();
        await Harness.RunJobAsync(sp, Harness.JobPath, TestContext.Current.CancellationToken);
        var events = await Events(sp, Harness.JobPath);
        foreach (var e in events)
        {
            e.Summary.ShouldNotContain("AKIAIOSFODNN7EXAMPLE");
            e.Payload?.ToJsonString().ShouldNotContain("AKIAIOSFODNN7EXAMPLE");
        }
    }

    private static async Task<List<LedgerEvent>> Events(IServiceProvider sp, string jobPath)
    {
        var log = sp.GetRequiredService<IEventLog>();
        var list = new List<LedgerEvent>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(400));
        try
        {
            await foreach (var e in log.TailAsync(AgentsConstants.StreamId(jobPath), 0, cts.Token))
            {
                list.Add(e);
                if (e.Type == AgentEventTypes.Result)
                    break;
            }
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
        }

        return list;
    }

    private static void SeqIncreasing(IReadOnlyList<LedgerEvent> events)
    {
        events.Count.ShouldBeGreaterThan(0);
        for (var i = 1; i < events.Count; i++)
            events[i].Seq.ShouldBeGreaterThan(events[i - 1].Seq);
    }
}
