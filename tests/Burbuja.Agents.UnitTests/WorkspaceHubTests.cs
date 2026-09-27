using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions.Domain;
using Burbuja.Agents.Handlers;
using Burbuja.Agents.Model;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Arm;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Burbuja.Agents.UnitTests;

/// <summary>
/// K3 — the workspace hub: what a workspace holds, in one read.
///
/// <para><b>Why this is a kernel projection and not a portal query.</b> The answer is assembled from
/// resources that already exist — the workspace's jobs and its schedules — and if each surface assembled it
/// itself, two surfaces would eventually disagree about the same workspace. The facts here are about the
/// three things that makes it worth having: the numbers are the store's, the groups cover every status, and
/// the block is filled for one resource and NOT for a list.</para>
/// </summary>
public sealed class WorkspaceHubTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string WsPath = Harness.WsPath;

    // ---------------------------------------------------------------- where it is filled, and where not

    /// <summary>
    /// Filled when ONE workspace is read, absent on every row of a LIST.
    ///
    /// <para><b>Both halves are one fact on purpose.</b> "The hub is there" is obvious; "the hub is not there
    /// in a list" is a decision — a list of a hundred workspaces must not scan a hundred job sets — and a
    /// fact that only asserted the first would pass against an implementation that quietly made every list
    /// view expensive.</para>
    /// </summary>
    [Fact]
    public async Task The_hub_is_filled_for_one_workspace_and_absent_from_a_list()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);

        (await Harness.Send(sp, "PUT", WsPath + "/jobs/j1", Harness.JobBody("happy-path"))).Ok();

        var one = (await Harness.Send(sp, "GET", WsPath)).AsJson();
        var hub = one["properties"]!["hub"];
        hub.ShouldNotBeNull("a single workspace read answers what it holds");
        hub!["jobs"]!.GetValue<int>().ShouldBe(1);
        hub["scanned"]!.GetValue<int>().ShouldBe(1);
        hub["truncated"]!.GetValue<bool>().ShouldBeFalse("the scan bound was not reached, and it says so");
        hub["byStatus"]!["queued"]!.GetValue<int>().ShouldBe(1);
        hub["generatedAt"].ShouldNotBeNull();
        hub["recent"]!.AsArray().Count.ShouldBe(1);

        var list = (await Harness.Send(sp, "GET", "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Agents/workspaces")).AsJson();
        var rows = list["value"]!.AsArray();
        rows.Count.ShouldBe(1);
        rows[0]!["properties"]!.AsObject().Select(p => p.Key).ShouldNotContain(
            "hub",
            "a list must not scan every workspace's jobs; the hub is asked for by name");
    }

    /// <summary>A workspace with nothing in it reports zeros rather than omitting the block: a reader that
    /// has to tell "no jobs" from "no hub" is a reader that will get it wrong.</summary>
    [Fact]
    public async Task An_empty_workspace_reports_zeros()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);

        var hub = (await Harness.Send(sp, "GET", WsPath)).AsJson()["properties"]!["hub"]!;
        hub["jobs"]!.GetValue<int>().ShouldBe(0);
        hub["spendUsd"]!.GetValue<decimal>().ShouldBe(0m);
        hub["recent"]!.AsArray().ShouldBeEmpty();
        hub["schedules"]!.GetValue<int>().ShouldBe(0);
        hub["nextScheduleRun"].ShouldBeNull();
        foreach (var group in WorkspaceHandler.JobGroups.Keys)
        {
            hub["byStatus"]![group].ShouldNotBeNull($"'{group}' is a group and must be present at zero");
            hub["byStatus"]![group]!.GetValue<int>().ShouldBe(0);
        }
    }

    // ---------------------------------------------------------------- the numbers

    /// <summary>
    /// The counts, the spend and the recent list come from the jobs the workspace actually holds.
    ///
    /// <para><b>Why the statuses are written straight into the store.</b> Driving three jobs to
    /// <c>succeeded</c>, <c>awaitingApproval</c> and <c>budgetExceeded</c> through the orchestrator would
    /// take a scenario, a provider and a worker per status — and the claim here is not that the orchestrator
    /// reaches those states (other facts hold that) but that the hub COUNTS them. Writing the envelopes is
    /// the shortest path to the states, and the comment says so rather than pretending the jobs ran.</para>
    /// </summary>
    [Fact]
    public async Task The_hub_counts_the_jobs_the_workspace_holds()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);

        await SeedJobAsync(sp, "j1", JobExecutionStatus.Succeeded, worker: 1.50m, supervisor: 0.25m, mentor: 0.25m);
        await SeedJobAsync(sp, "j2", JobExecutionStatus.AwaitingApproval, pendingApprovalId: "ap-1");
        await SeedJobAsync(sp, "j3", JobExecutionStatus.BudgetExceeded, worker: 0.75m);
        await SeedJobAsync(sp, "j4", JobExecutionStatus.Queued);

        var hub = (await Harness.Send(sp, "GET", WsPath)).AsJson()["properties"]!["hub"]!;

        hub["jobs"]!.GetValue<int>().ShouldBe(4);
        hub["byStatus"]!["succeeded"]!.GetValue<int>().ShouldBe(1);
        hub["byStatus"]!["parked"]!.GetValue<int>().ShouldBe(1);
        hub["byStatus"]!["failed"]!.GetValue<int>().ShouldBe(1);
        hub["byStatus"]!["queued"]!.GetValue<int>().ShouldBe(1);
        hub["byStatus"]!["active"]!.GetValue<int>().ShouldBe(0);

        // The SPEND is the job's whole cost, not its worker part: a supervisor and a mentor are turns the
        // workspace paid for, and a sum that forgot them would under-report every job the mentor reviewed.
        hub["spendUsd"]!.GetValue<decimal>().ShouldBe(2.75m, "(1.50 worker + 0.25 supervisor + 0.25 mentor) + 0.75 worker");

        var parked = hub["recent"]!.AsArray().Single(r => r!["name"]!.GetValue<string>() == "j2");
        parked!["pendingApprovalId"]!.GetValue<string>().ShouldBe(
            "ap-1",
            "a parked job's row points at the card, so the hub can be acted on rather than only read");
        parked["status"]!.GetValue<string>().ShouldBe("AwaitingApproval");
    }

    /// <summary>The recent list is the newest ten while the COUNT is everything: a list capped by the same
    /// bound as the count would report ten jobs for a workspace holding twelve.</summary>
    [Fact]
    public async Task The_recent_list_is_the_newest_ten_and_the_count_is_everything()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);

        for (var i = 1; i <= 12; i++)
            await SeedJobAsync(sp, $"j{i:00}", JobExecutionStatus.Queued);

        var hub = (await Harness.Send(sp, "GET", WsPath)).AsJson()["properties"]!["hub"]!;

        hub["jobs"]!.GetValue<int>().ShouldBe(12);
        hub["byStatus"]!["queued"]!.GetValue<int>().ShouldBe(12);
        var recent = hub["recent"]!.AsArray();
        recent.Count.ShouldBe(WorkspaceHandler.HubRecentLimit);
        recent[0]!["name"]!.GetValue<string>().ShouldBe("j12", "newest first: the question is what just happened");
    }

    /// <summary>
    /// The schedules are counted, and the next window is the soonest among the LIVE ones — a paused
    /// schedule's window is not a thing that will happen, and reporting it would tell an operator to expect a
    /// run that the sweeper will skip.
    /// </summary>
    [Fact]
    public async Task The_schedules_are_counted_and_a_paused_one_does_not_supply_the_next_window()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);

        await SeedScheduleAsync(sp, "paused-first", paused: true, next: new DateTimeOffset(2026, 9, 14, 3, 0, 0, TimeSpan.Zero));
        await SeedScheduleAsync(sp, "live-later", paused: false, next: new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero));

        var hub = (await Harness.Send(sp, "GET", WsPath)).AsJson()["properties"]!["hub"]!;

        hub["schedules"]!.GetValue<int>().ShouldBe(2);
        hub["pausedSchedules"]!.GetValue<int>().ShouldBe(1);
        hub["nextScheduleRun"]!.GetValue<DateTimeOffset>()
            .ShouldBe(new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero), "the paused schedule's earlier window will not happen");
    }

    // ---------------------------------------------------------------- the partition

    /// <summary>
    /// The groups are a PARTITION of <see cref="JobExecutionStatus"/>: pairwise disjoint, and together
    /// covering every value the enum has. A status added without deciding which group it belongs in fails
    /// here — the alternative is a job that is counted in <c>jobs</c> and in none of the groups, which is
    /// exactly the kind of quiet gap a summary surface must not have.
    /// </summary>
    [Fact]
    public void The_status_groups_are_a_partition_of_every_job_status()
    {
        var groups = WorkspaceHandler.JobGroups;
        groups.Count.ShouldBeGreaterThan(1);
        groups.Keys.ShouldNotContain(k => string.IsNullOrWhiteSpace(k));

        var all = Enum.GetValues<JobExecutionStatus>();
        var seen = new List<JobExecutionStatus>();
        foreach (var (name, set) in groups)
        {
            set.ShouldNotBeEmpty($"'{name}' is a group and must hold at least one status");
            foreach (var status in set)
            {
                seen.ShouldNotContain(status, $"'{status}' is in two groups, and a partition has no overlap");
                seen.Add(status);
            }
        }

        seen.ShouldBe(all, ignoreOrder: true, customMessage: "every status belongs to exactly one group");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Write a job envelope with a status and a cost.
    ///
    /// <para>The cost is split across the four parts on purpose: a hub that summed only the worker's share
    /// would pass a fact that set only the worker's share, so the helper always sets all of them and the
    /// spend fact asserts the total.</para>
    /// </summary>
    private static async Task SeedJobAsync(
        IServiceProvider sp,
        string name,
        JobExecutionStatus status,
        decimal worker = 0m,
        decimal supervisor = 0m,
        decimal mentor = 0m,
        string? pendingApprovalId = null)
    {
        var store = sp.GetRequiredService<IResourceStore>();
        var properties = new JobProperties
        {
            Spec = new JobSpec { Kind = JobKind.Implement, Title = $"job {name}", Slug = name },
            Status = status,
            Cost = new JobCost(worker, 0m, supervisor, mentor, 100, 50),
            PendingApprovalId = pendingApprovalId,
        };

        await store.UpsertAsync(
            new ResourceEnvelope<JsonObject>(
                WsPath + "/jobs/" + name,
                name,
                "Burbuja.Agents/workspaces/jobs",
                "local",
                new Dictionary<string, string>(),
                JsonSerializer.SerializeToNode(properties, JsonWire.Options)!.AsObject(),
                ProvisioningState.Succeeded,
                new SystemData(DateTimeOffset.UnixEpoch, "owner", "User", DateTimeOffset.UnixEpoch, "owner", "User"),
                ""),
            null,
            Ct);
    }

    private static async Task SeedScheduleAsync(IServiceProvider sp, string name, bool paused, DateTimeOffset next)
    {
        var store = sp.GetRequiredService<IResourceStore>();
        await store.UpsertAsync(
            new ResourceEnvelope<JsonObject>(
                WsPath + "/schedules/" + name,
                name,
                "Burbuja.Agents/workspaces/schedules",
                "local",
                new Dictionary<string, string>(),
                new JsonObject
                {
                    ["cron"] = "0 3 * * *",
                    ["timeZone"] = "UTC",
                    ["paused"] = paused,
                    ["nextRunAt"] = next.ToString("O"),
                },
                ProvisioningState.Succeeded,
                new SystemData(DateTimeOffset.UnixEpoch, "owner", "User", DateTimeOffset.UnixEpoch, "owner", "User"),
                ""),
            null,
            Ct);
    }
}
