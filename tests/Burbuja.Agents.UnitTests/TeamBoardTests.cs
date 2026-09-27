using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Agents;
using Burbuja.Agents.Abstractions.Teams;
using Burbuja.Agents.Runtime;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Burbuja.Agents.UnitTests;

public sealed class TeamBoardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string TeamPath =
        "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Agents/teams/t1";

    [Fact]
    public async Task Concurrent_task_patches_serialise_by_cas()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", TeamPath, """{"properties":{"displayName":"t"}}""")).Ok();
        (await Harness.Send(sp, "PUT", TeamPath + "/tasks/task-1", """{"properties":{"title":"one"}}""")).Ok();

        // Eight writers all hold revision 1 and race; exactly one may win.
        var patches = Enumerable.Range(0, 8).Select(i => Harness.Send(
            sp,
            "PATCH",
            TeamPath + "/tasks/task-1",
            """{"properties":{"revision":1,"status":"running"}}""",
            extraHeaders: new Dictionary<string, string> { ["x-ms-client-request-id"] = "race-" + i })).ToArray();
        var results = await Task.WhenAll(patches);

        results.Count(r => r.StatusCode == 200).ShouldBe(1);
        results.Count(r => r.StatusCode == 412).ShouldBe(7);

        var board = sp.GetRequiredService<TeamBoard>();
        var task = await board.GetTaskAsync(TeamKey(), "task-1", Ct);
        task!.Revision.ShouldBe(2);
    }

    [Fact]
    public async Task Task_patch_requires_the_current_revision()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", TeamPath, """{"properties":{"displayName":"t"}}""")).Ok();
        (await Harness.Send(sp, "PUT", TeamPath + "/tasks/task-1", """{"properties":{"title":"one"}}""")).Ok();

        var noRevision = await Harness.Send(sp, "PATCH", TeamPath + "/tasks/task-1", """{"properties":{"status":"running"}}""",
            extraHeaders: new Dictionary<string, string> { ["x-ms-client-request-id"] = "no-rev" });
        noRevision.StatusCode.ShouldBe(400);

        var stale = await Harness.Send(sp, "PATCH", TeamPath + "/tasks/task-1", """{"properties":{"revision":9,"status":"running"}}""",
            extraHeaders: new Dictionary<string, string> { ["x-ms-client-request-id"] = "stale-rev" });
        stale.StatusCode.ShouldBe(412);
    }

    [Fact]
    public async Task Blocked_by_cycle_is_rejected()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", TeamPath, """{"properties":{"displayName":"t"}}""")).Ok();
        (await Harness.Send(sp, "PUT", TeamPath + "/tasks/a", """{"properties":{"title":"a","blockedBy":["b"]}}""")).Ok();

        var cyclic = await Harness.Send(sp, "PUT", TeamPath + "/tasks/b", """{"properties":{"title":"b","blockedBy":["a"]}}""");
        cyclic.StatusCode.ShouldBe(400);
        cyclic.Error!.Code.ShouldBe("BoardCycle");
    }

    [Fact]
    public async Task Write_scopes_overlap_warns_and_never_locks()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", TeamPath, """{"properties":{"displayName":"t"}}""")).Ok();
        (await Harness.Send(sp, "PUT", TeamPath + "/tasks/a", """{"properties":{"title":"a","ownerAgentId":"a1","writeScopes":["src/x"]}}""")).Ok();

        var second = await Harness.Send(sp, "PUT", TeamPath + "/tasks/b",
            """{"properties":{"title":"b","ownerAgentId":"b1","writeScopes":["src/x"]}}""");
        second.StatusCode.ShouldBeOneOf(200, 201, 202, 204, 405);

        var board = sp.GetRequiredService<TeamBoard>();
        var result = await board.PatchTaskAsync(TeamKey(), "a", 1, null, _ => { }, Ct);
        result.WriteScopeWarning.ShouldBeTrue("overlap is a warning, never a lock");
        result.Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task Deleted_task_is_a_tombstone_not_a_hole()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", TeamPath, """{"properties":{"displayName":"t"}}""")).Ok();
        (await Harness.Send(sp, "PUT", TeamPath + "/tasks/a", """{"properties":{"title":"a"}}""")).Ok();

        var board = sp.GetRequiredService<TeamBoard>();
        var deleted = await board.DeleteTaskAsync(TeamKey(), "a", Ct);
        deleted.Ok.ShouldBeTrue();
        deleted.Task!.Deleted.ShouldBeTrue();

        var listed = await board.ListTasksAsync(TeamKey(), Ct);
        listed.ShouldNotContain(t => t.Id == "a");
        var raw = await sp.GetRequiredService<IResourceStore>().GetAsync(TeamBoard.TaskKey(TeamKey(), "a"), Ct);
        raw.ShouldNotBeNull("the row stays as a tombstone");
    }

    [Fact]
    public async Task Fold_of_the_team_stream_equals_the_projection()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", TeamPath, """{"properties":{"displayName":"t"}}""")).Ok();
        (await Harness.Send(sp, "PUT", TeamPath + "/tasks/a", """{"properties":{"title":"a"}}""")).Ok();
        (await Harness.Send(sp, "PUT", TeamPath + "/tasks/b", """{"properties":{"title":"b"}}""")).Ok();

        var board = sp.GetRequiredService<TeamBoard>();
        var projection = await board.ListTasksAsync(TeamKey(), Ct);

        var events = sp.GetRequiredService<IEventLog>();
        var streamId = $"{TeamKey()}/stream";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(250));
        var seen = new List<LedgerEvent>();
        try
        {
            await foreach (var e in events.TailAsync(streamId, 0, cts.Token))
                seen.Add(e);
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
        }

        var folded = TeamFolder.Fold(seen);
        folded.Select(t => (t.Id, t.Status, t.Revision))
            .ShouldBe(projection.Select(t => (t.Id, t.Status, t.Revision)));
    }

    [Fact]
    public async Task Roster_and_message_bounds_fail_loud()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", TeamPath, """{"properties":{"displayName":"t"}}""")).Ok();

        for (var i = 0; i < TeamBounds.RosterMax; i++)
        {
            var ok = await Harness.Send(sp, "POST", TeamPath + "/spawnMember", "{\"agentId\":\"a" + i + "\"}",
                extraHeaders: new Dictionary<string, string> { ["x-ms-client-request-id"] = "spawn-" + i });
            ok.StatusCode.ShouldBe(200);
        }

        var overflow = await Harness.Send(sp, "POST", TeamPath + "/spawnMember", """{"agentId":"too-many"}""",
            extraHeaders: new Dictionary<string, string> { ["x-ms-client-request-id"] = "spawn-overflow" });
        overflow.StatusCode.ShouldBe(400);
        overflow.Error!.Code.ShouldBe("RosterFull");

        var big = new string('x', TeamBounds.MessageMaxBytes + 1);
        var message = await Harness.Send(sp, "PUT", TeamPath + "/messages/m1",
            "{\"properties\":{\"fromAgentId\":\"a0\",\"body\":\"" + big + "\"}}");
        message.StatusCode.ShouldBe(400);
    }

    [Fact]
    public async Task Wait_for_change_timeout_is_bounded()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", TeamPath, """{"properties":{"displayName":"t"}}""")).Ok();

        var tooShort = await Harness.Send(sp, "POST", TeamPath + "/waitForChange", """{"timeoutSeconds":1}""");
        tooShort.StatusCode.ShouldBe(400);

        var tooLong = await Harness.Send(sp, "POST", TeamPath + "/waitForChange", """{"timeoutSeconds":7200}""",
            extraHeaders: new Dictionary<string, string> { ["x-ms-client-request-id"] = "wait-long" });
        tooLong.StatusCode.ShouldBe(400);
    }

    private static string TeamKey() => TeamPath.ToLowerInvariant();
}
