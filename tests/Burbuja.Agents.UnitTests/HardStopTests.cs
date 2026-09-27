using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Budget;
using Burbuja.Agents.Abstractions.Domain;
using Burbuja.Agents.Runtime;
using Burbuja.Core.Abstractions.Events;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Burbuja.Agents.UnitTests;

/// <summary>
/// M10 H3 — the daily hard stop is ENFORCED, on every path that can spend.
///
/// <para><b>The defect these facts exist for.</b> <c>Budget:DailyHardStopUsd</c> was read once, into the
/// <c>Budgets</c> snapshot at job PUT, and compared against nothing anywhere in the tree: the console could
/// show a limit, the configuration carried it, and a user with a real model staged could spend without ever
/// meeting it. What follows pins the three things that make it real on the job path and the stateful agent
/// path — the refusal happens BEFORE a turn is invoked, the job parks in <c>blocked</c> rather than
/// <c>awaitingApproval</c> (nobody approves money), and the event carries the day and both numbers.</para>
///
/// <para><b>Every fact here is red with the seam removed and green with the stop set high</b>, which is the
/// shape the plan asked for: the neuter for this rule is <c>stop = 0</c> (no stop configured, nothing
/// refused) or the seam deleted (everything runs). Both were run.</para>
/// </summary>
public sealed class HardStopTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string AgentsRoot = "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Agents";
    private const string AgentPath = AgentsRoot + "/agents/a1";

    /// <summary>
    /// Books the day over the stop, through the SAME ledger every spender books into. A fact that wrote a
    /// counter of its own would prove the comparison against its own arithmetic.
    /// </summary>
    private static async Task SpendAsync(IServiceProvider sp, decimal usd)
    {
        var ledger = sp.GetRequiredService<IBudgetLedger>();
        await ledger.BookAsync(new BudgetBooking(Guid.NewGuid(), Guid.NewGuid(), BudgetKind.Worker, usd, 10, 10), Ct);
    }

    private static async Task<List<LedgerEvent>> EventsAsync(IServiceProvider sp, string streamId)
    {
        var log = sp.GetRequiredService<Burbuja.Core.Abstractions.Events.IEventLog>();
        var events = new List<LedgerEvent>();
        await foreach (var evt in log.ReadRangeAsync(streamId, 0, long.MaxValue, 200, null, Ct))
            events.Add(evt);
        return events;
    }

    [Fact]
    public async Task A_job_whose_day_is_over_the_stop_is_refused_before_its_first_turn()
    {
        using var sp = Harness.CreateHost(settings: new Dictionary<string, string?>
        {
            ["Budget:DailyHardStopUsd"] = "1.00",
        });
        await Harness.SeedAsync(sp);
        await SpendAsync(sp, 1.00m);

        var put = await Harness.Send(sp, "PUT", Harness.JobPath, Harness.JobBody("happy-path"));
        put.Ok();
        await Harness.RunJobAsync(sp, Harness.JobPath, Ct);

        var job = (await Harness.Send(sp, "GET", Harness.JobPath)).AsJson();
        var properties = job["properties"]!;
        properties["status"]!.GetValue<string>().ShouldBe("blocked",
            "a job at the day's stop is BLOCKED — not awaitingApproval, because a human approves a decision and nobody approves money");
        properties["error"]!.GetValue<string>().ShouldContain("Budget:DailyHardStopUsd");

        // The event carries the day and BOTH numbers, so a reader reconciling the console against the ledger
        // has one record with the same three fields the ledger GET reports.
        var events = await EventsAsync(sp, AgentsConstants.StreamId(Harness.JobPath.ToLowerInvariant()));
        var stop = events.FirstOrDefault(e => e.Type == HardStopReading.EventType);
        stop.ShouldNotBeNull("the refusal is on the stream: " + string.Join(", ", events.Select(e => e.Type)));
        stop!.Payload!["day"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
        stop.Payload!["spentUsd"]!.GetValue<decimal>().ShouldBe(1.00m);
        stop.Payload!["stopUsd"]!.GetValue<decimal>().ShouldBe(1.00m);

        // And nothing was run: the refusal is before the first turn, not a report about a turn that happened.
        events.Any(e => e.Type == AgentEventTypes.TurnStarted)
            .ShouldBeFalse("the stop is checked before a model turn is invoked, or the job has already spent");
    }

    [Fact]
    public async Task A_day_inside_the_stop_runs_the_job()
    {
        // The other direction, and the reason the neuter for this rule is `stop = 0`: with the stop set above
        // what the ledger holds, the same job runs. Without this the fact above would pass on a host that
        // refused everything.
        using var sp = Harness.CreateHost(settings: new Dictionary<string, string?>
        {
            ["Budget:DailyHardStopUsd"] = "50.00",
        });
        await Harness.SeedAsync(sp);
        await SpendAsync(sp, 1.00m);

        (await Harness.Send(sp, "PUT", Harness.JobPath, Harness.JobBody("happy-path"))).Ok();
        await Harness.RunJobAsync(sp, Harness.JobPath, Ct);

        var job = (await Harness.Send(sp, "GET", Harness.JobPath)).AsJson();
        job["properties"]!["status"]!.GetValue<string>().ShouldNotBe("blocked");
        var events = await EventsAsync(sp, AgentsConstants.StreamId(Harness.JobPath.ToLowerInvariant()));
        events.Any(e => e.Type == AgentEventTypes.TurnStarted)
            .ShouldBeTrue("inside the stop the job runs its turns");
    }

    /// <summary>
    /// M2 FLIPPED THIS FACT, and the flip is the plan's own neuter.
    ///
    /// <para><b>What it asserted before.</b> "`0` and absent both mean no stop" — so a host that forgot the key
    /// spent without a ceiling, and the fact PINNED that as the intended reading. R7 named it: the two
    /// statements are different, and a guard nobody set is not an unlimited budget. <c>0</c> now means "spend
    /// nothing", and the job is refused BEFORE the model is called.</para>
    /// </summary>
    [Fact]
    public async Task A_host_that_configured_zero_refuses_everything()
    {
        using var sp = Harness.CreateHost(settings: new Dictionary<string, string?>
        {
            ["Budget:DailyHardStopUsd"] = "0",
        });
        await Harness.SeedAsync(sp);

        (await Harness.Send(sp, "PUT", Harness.JobPath, Harness.JobBody("happy-path"))).Ok();
        await Harness.RunJobAsync(sp, Harness.JobPath, Ct);

        var job = (await Harness.Send(sp, "GET", Harness.JobPath)).AsJson();
        job["properties"]!["status"]!.GetValue<string>().ShouldBe("blocked",
            "a ceiling of zero is a ceiling that is already reached");
        job["properties"]!["error"]!.GetValue<string>().ShouldContain("0");
    }

    /// <summary>
    /// …and an ABSENT guard is still not a ceiling for the money the kernel's own paths spend: the three guards
    /// treat it as "nobody said", which is the reading every lane without the key depends on. What changed is
    /// that a PAID PROVIDER will not serve such a host at all — that is the provider's fact, next door.
    /// </summary>
    [Fact]
    public async Task A_host_with_no_stop_configured_still_runs_its_own_agents()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        await SpendAsync(sp, 5.00m);

        (await Harness.Send(sp, "PUT", Harness.JobPath, Harness.JobBody("happy-path"))).Ok();
        await Harness.RunJobAsync(sp, Harness.JobPath, Ct);

        var job = (await Harness.Send(sp, "GET", Harness.JobPath)).AsJson();
        job["properties"]!["status"]!.GetValue<string>().ShouldNotBe("blocked");
    }

    [Fact]
    public async Task An_agent_activation_at_the_stop_runs_no_turn()
    {
        // The stateful path, which has no USD cap of its own — so the daily stop is the ONLY ceiling a persona
        // agent (and therefore every AKOS turn) can meet. A refusal that came from `BookAsync` instead would be
        // a refusal written AFTER the turn had already been paid for: it would hide the spend, not stop it.
        using var sp = Harness.CreateHost(settings: new Dictionary<string, string?>
        {
            ["Budget:DailyHardStopUsd"] = "2.00",
        });
        await Harness.SeedAsync(sp);
        await SpendAsync(sp, 2.50m);

        (await Harness.Send(sp, "PUT", AgentPath, """{"properties":{"displayName":"a1"}}""")).Ok();
        (await Harness.Send(sp, "POST", AgentPath + "/send", """{"mode":"followup","text":"go"}""")).StatusCode.ShouldBe(202);

        var worker = sp.GetRequiredService<AgentActivationWorker>();
        await worker.ActivateAsync(AgentPath.ToLowerInvariant(), Ct);

        var agent = (await Harness.Send(sp, "GET", AgentPath)).AsJson();
        var sessionId = agent["properties"]!["currentSessionId"]?.GetValue<string>();
        sessionId.ShouldBeNullOrEmpty("no session was opened, because no turn was run");

        // The inbox was NOT drained either: the work is still there for when the limit moves. A refused
        // activation that consumed the message would lose the user's question to a budget event.
        agent["properties"]!["inbox"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_ledger_reports_the_stop_beside_the_spend()
    {
        // The console reads `spent / stop` from ONE resource. A page that read the ceiling from its own
        // configuration would be a second opinion about a number only the host can enforce, and the tile would
        // say "$3.00 spent" with no ceiling right up until the first refusal.
        using var sp = Harness.CreateHost(settings: new Dictionary<string, string?>
        {
            ["Budget:DailyHardStopUsd"] = "3",
        });
        await Harness.SeedAsync(sp);
        await SpendAsync(sp, 1.25m);

        var day = TimeProvider.System.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd");
        var ledger = (await Harness.Send(sp, "GET", AgentsRoot + "/ledgers/" + day)).AsJson();
        ledger["properties"]!["spentUsd"]!.GetValue<decimal>().ShouldBe(1.25m);
        ledger["properties"]!["hardStopUsd"]!.GetValue<decimal>().ShouldBe(3m);
        ledger["properties"]!["hardStopReached"]!.GetValue<bool>().ShouldBeFalse();

        await SpendAsync(sp, 1.75m);
        var reached = (await Harness.Send(sp, "GET", AgentsRoot + "/ledgers/" + day)).AsJson();
        reached["properties"]!["hardStopReached"]!.GetValue<bool>().ShouldBeTrue("3.00 spent against a 3.00 stop is AT the stop");
    }
}
