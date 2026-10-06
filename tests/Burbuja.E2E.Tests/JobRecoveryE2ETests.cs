using System.Net;
using System.Net.Http.Json;
using Xunit;
using static Burbuja.E2E.Tests.KernelTestHost;

namespace Burbuja.E2E.Tests;

[Trait("Category", "E2E")]
[Trait("OwnsHost", "true")]
public sealed class JobRecoveryE2ETests
{
    [Fact]
    public async Task Accepted_cancellation_is_durable_and_an_old_approval_cannot_restart_the_job()
    {
        await using var host = new KernelTestHost();
        await host.StartAsync();
        var path = Workspace + "/jobs/cancel";
        string id;
        using (var before = host.Client())
        {
            await ExpectAsync(await before.PutAsJsonAsync(path + Api, new
            {
                tags = new Dictionary<string, string> { ["burbuja.io/scenario"] = "gray-supervisor-escalate-approved" },
                properties = new { spec = new { kind = "implement", title = "Cancel", slug = "cancel", brief = "guarded action" } },
            }, Ct), HttpStatusCode.Accepted);
            var pending = await WaitAsync(before, path, row => row["properties"]?["status"]?.GetValue<string>() == "awaitingApproval");
            id = pending["properties"]!["pendingApprovalId"]!.GetValue<string>();
            await ExpectAsync(await before.PostAsJsonAsync(path + "/cancel" + Api, new { }, Ct), HttpStatusCode.Accepted);
        }
        await host.KillAsync();
        await host.StartAsync(bootstrap: false);
        using var after = host.Client();
        await ExpectAsync(await after.PostAsJsonAsync("/providers/Burbuja.Resources/approvals/" + id + "/approve" + Api,
            new { }, Ct), HttpStatusCode.OK);
        var job = await GetAsync(after, path);
        Assert.Equal("cancelled", job["properties"]!["status"]!.GetValue<string>());
        using var request = new HttpRequestMessage(HttpMethod.Get, path + "/events" + Api);
        request.Headers.Accept.ParseAdd("application/json");
        var events = (await ExpectAsync(await after.SendAsync(request, Ct), HttpStatusCode.OK))["value"]!.AsArray();
        Assert.DoesNotContain(events, evt => evt!["type"]!.GetValue<string>() == "agents.tool.call");
    }

    [Fact]
    public async Task Fan_out_recovery_preserves_finished_workers_and_stable_run_ids_before_merging_once()
    {
        await using var host = new KernelTestHost();
        await host.StartAsync();
        var path = Workspace + "/jobs/fanout";
        string[] runIds;
        string fastRunId;
        using (var before = host.Client())
        {
            await ExpectAsync(await before.PutAsJsonAsync(path + Api, new
            {
                tags = new Dictionary<string, string> { ["burbuja.io/scenario"] = "recovery-fanout" },
                properties = new { spec = new
                {
                    kind = "implement", title = "Fan-out recovery", slug = "fanout", brief = "run the two independent workers",
                    autoAccept = true, strategy = "plan-fan-out",
                } },
            }, Ct), HttpStatusCode.Accepted);
            var checkpoint = await WaitAsync(before, path, body =>
                body["properties"]?["recovery"]?["runs"] is System.Text.Json.Nodes.JsonArray runs &&
                runs.Any(run => run!["label"]?.GetValue<string>() == "fast" && run["status"]?.GetValue<string>() == "succeeded"));
            var runs = checkpoint["properties"]!["recovery"]!["runs"]!.AsArray();
            runIds = runs.Select(run => run!["runId"]!.GetValue<string>()).ToArray();
            fastRunId = Assert.Single(runs, run => run!["label"]!.GetValue<string>() == "fast")!["runId"]!.GetValue<string>();
        }
        await host.KillAsync();
        await host.StartAsync(bootstrap: false);
        using var after = host.Client();
        var finished = await WaitAsync(after, path, body => body["properties"]?["status"]?.GetValue<string>() == "succeeded");
        Assert.Equal(runIds, finished["properties"]!["recovery"]!["runs"]!.AsArray()
            .Select(run => run!["runId"]!.GetValue<string>()));
        using var request = new HttpRequestMessage(HttpMethod.Get, path + "/events" + Api);
        request.Headers.Accept.ParseAdd("application/json");
        var events = (await ExpectAsync(await after.SendAsync(request, Ct), HttpStatusCode.OK))["value"]!.AsArray();
        Assert.Equal(2, events.Count(evt => evt!["type"]!.GetValue<string>() == "agents.turn.started"
            && evt["payload"]?["runId"]?.GetValue<string>() == Guid.Parse(fastRunId).ToString("N")));
        Assert.Single(events, evt => evt!["type"]!.GetValue<string>() == "agents.result");
        Assert.Equal(3, (await GetAsync(after, path + "/runs"))["value"]!.AsArray().Count);
    }

    [Fact]
    public async Task A_completed_side_effect_is_not_repeated_and_an_uncertain_effect_blocks_after_a_hard_restart()
    {
        await using var host = new KernelTestHost();
        await host.StartAsync();
        var path = Workspace + "/jobs/effects";
        string directory;
        using (var before = host.Client())
        {
            await ExpectAsync(await before.PutAsJsonAsync(path + Api, new
            {
                tags = new Dictionary<string, string> { ["burbuja.io/scenario"] = "recovery-partial-batch" },
                properties = new
                {
                    spec = new { kind = "implement", title = "Effect recovery", slug = "effects", brief = "run the two test-owned effects", autoAccept = true },
                },
            }, Ct), HttpStatusCode.Accepted);
            var running = await WaitAsync(before, path, body =>
                body["properties"]?["state"]?["workspacePath"] is not null);
            directory = running["properties"]!["state"]!["workspacePath"]!.GetValue<string>();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            while (true)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path + "/events" + Api);
                request.Headers.Accept.ParseAdd("application/json");
                var events = await ExpectAsync(await before.SendAsync(request, deadline.Token), HttpStatusCode.OK);
                if (events["value"]!.AsArray().Any(x =>
                        x!["type"]!.GetValue<string>() == "agents.tool.call" &&
                        x["subStreamId"]?.GetValue<string>() == "interrupted"))
                    break;
                await Task.Delay(50, deadline.Token);
            }
            Assert.Equal("x", File.ReadAllText(Path.Combine(directory, "counter.txt")));
        }
        await host.KillAsync();
        await host.StartAsync(bootstrap: false);
        using var after = host.Client();
        var blocked = await WaitAsync(after, path,
            body => body["properties"]?["status"]?.GetValue<string>() == "blocked");
        Assert.Contains("InterruptedToolExecution", blocked["properties"]!["error"]!.GetValue<string>());
        Assert.Equal("x", File.ReadAllText(Path.Combine(directory, "counter.txt")));
    }

    [Fact]
    public async Task Approval_wait_survives_a_hard_restart_and_the_original_job_settles_once()
    {
        await using var host = new KernelTestHost();
        await host.StartAsync();
        var path = Workspace + "/jobs/approval";
        string approvalId;
        using (var before = host.Client())
        {
            await ExpectAsync(await before.PutAsJsonAsync(path + Api, new
            {
                tags = new Dictionary<string, string> { ["burbuja.io/scenario"] = "gray-supervisor-escalate-approved" },
                properties = new
                {
                    spec = new { kind = "implement", title = "Approval recovery", slug = "approval", brief = "request the guarded action", autoAccept = true },
                },
            }, Ct), HttpStatusCode.Accepted);
            var parked = await WaitAsync(before, path,
                body => body["properties"]?["status"]?.GetValue<string>() == "awaitingApproval");
            approvalId = parked["properties"]!["pendingApprovalId"]!.GetValue<string>();
        }
        await host.KillAsync();
        await host.StartAsync(bootstrap: false);
        using var after = host.Client();
        var restored = await GetAsync(after, path);
        Assert.Equal(approvalId, restored["properties"]!["pendingApprovalId"]!.GetValue<string>());
        await ExpectAsync(await after.PostAsJsonAsync(
            "/providers/Burbuja.Resources/approvals/" + approvalId + "/approve" + Api,
            new { note = "approve this same action after restart" }, Ct), HttpStatusCode.OK);
        await WaitAsync(after, path,
            body => body["properties"]?["status"]?.GetValue<string>() == "succeeded");
        using var request = new HttpRequestMessage(HttpMethod.Get, path + "/events" + Api);
        request.Headers.Accept.ParseAdd("application/json");
        var events = await ExpectAsync(await after.SendAsync(request, Ct), HttpStatusCode.OK);
        Assert.Single(events["value"]!.AsArray(), x => x!["type"]!.GetValue<string>() == "agents.result");
        Assert.Single(events["value"]!.AsArray(), x => x!["type"]!.GetValue<string>() == "agents.tool.call");
        var runs = await GetAsync(after, path + "/runs");
        Assert.Single(runs["value"]!.AsArray());
    }
}
