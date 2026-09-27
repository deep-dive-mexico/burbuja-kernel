using System.Text;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Outbox;
using Burbuja.Core.Arm;
using Shouldly;
using Xunit;

namespace Burbuja.Store.UnitTests;

/// <summary>
/// The store contract, exercised against both the kernel in-memory stores and the SQLite plugin.
/// Every test must pass for both — that is what makes the SQLite plugin a drop-in replacement.
/// </summary>
public abstract class StoreContractTests : IAsyncLifetime
{
    private IStoreFixture _fixture = null!;

    protected IStoreFixture Fixture => _fixture;

    protected TestClock Clock => _fixture.Clock;

    protected abstract IStoreFixture CreateFixture();

    public ValueTask InitializeAsync()
    {
        _fixture = CreateFixture();
        return default;
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    // ---------------------------------------------------------------- resources

    private static ResourceEnvelope<JsonObject> Envelope(string id, string type, string name) => new(
        id,
        name,
        type,
        "westus",
        new Dictionary<string, string> { ["env"] = "test" },
        new JsonObject { ["size"] = "small" },
        ProvisioningState.Succeeded,
        new SystemData(null, "tester", "User", null, null, null),
        "");

    [Fact]
    public async Task Upsert_creates_then_stamps_version_and_etag()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = await Fixture.Resources.UpsertAsync(
            Envelope("/subscriptions/s1/resourceGroups/rg/providers/Burbuja.Pomnibot/jobs/a", "Burbuja.Pomnibot/jobs", "a"),
            null,
            ct);

        created.ETag.ShouldBe("W/\"1\"");
        created.SystemData.CreatedAt.ShouldNotBeNull();
        created.SystemData.LastModifiedAt.ShouldNotBeNull();

        Clock.Advance(TimeSpan.FromMinutes(1));
        var updated = await Fixture.Resources.UpsertAsync(
            created with { Tags = new Dictionary<string, string> { ["env"] = "prod" } },
            created.ETag,
            ct);

        updated.ETag.ShouldBe("W/\"2\"");
        var reread = await Fixture.Resources.GetAsync(created.Id, ct);
        reread.ShouldNotBeNull();
        reread!.ETag.ShouldBe("W/\"2\"");
        reread.Tags["env"].ShouldBe("prod");
    }

    [Fact]
    public async Task Upsert_rejects_stale_etag_and_missing_target()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = await Fixture.Resources.UpsertAsync(Envelope("id-1", "Burbuja.Pomnibot/jobs", "one"), null, ct);

        var stale = await Should.ThrowAsync<ArmException>(
            async () => await Fixture.Resources.UpsertAsync(created with { Name = "two" }, "W/\"99\"", ct));
        stale.Error.Code.ShouldBe("PreconditionFailed");

        var missing = await Should.ThrowAsync<ArmException>(
            async () => await Fixture.Resources.UpsertAsync(Envelope("id-2", "Burbuja.Pomnibot/jobs", "two"), "W/\"1\"", ct));
        missing.Error.Code.ShouldBe("PreconditionFailed");

        var overwrite = await Fixture.Resources.UpsertAsync(created with { Name = "renamed" }, "*", ct);
        overwrite.Name.ShouldBe("renamed");
    }

    [Fact]
    public async Task Delete_honours_etag_and_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = await Fixture.Resources.UpsertAsync(Envelope("id-del", "Burbuja.Pomnibot/jobs", "del"), null, ct);

        await Should.ThrowAsync<ArmException>(
            async () => await Fixture.Resources.DeleteAsync(created.Id, "W/\"42\"", ct));

        await Fixture.Resources.DeleteAsync(created.Id, created.ETag, ct);
        (await Fixture.Resources.GetAsync(created.Id, ct)).ShouldBeNull();
        await Fixture.Resources.DeleteAsync(created.Id, null, ct);
    }

    [Fact]
    public async Task List_filters_by_type_and_pages_with_a_continuation_token()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 3; i++)
        {
            await Fixture.Resources.UpsertAsync(Envelope($"job-{i}", "Burbuja.Pomnibot/jobs", $"job{i}"), null, ct);
            Clock.Advance(TimeSpan.FromSeconds(1));
        }

        await Fixture.Resources.UpsertAsync(Envelope("other-1", "Burbuja.Pomnibot/repos", "other"), null, ct);

        var first = await Fixture.Resources.ListAsync(
            new ResourceStoreQuery(null, null, "Burbuja.Pomnibot", null, 2, null),
            ct);
        first.Value.Count.ShouldBe(2);
        first.NextLink.ShouldNotBeNull();

        var second = await Fixture.Resources.ListAsync(
            new ResourceStoreQuery(null, null, "Burbuja.Pomnibot", null, 2, first.NextLink),
            ct);
        second.Value.Count.ShouldBe(2);
        second.Value.ShouldContain(e => e.Id == "other-1");

        var all = first.Value.Concat(second.Value).Select(e => e.Id).ToList();
        all.Distinct().Count().ShouldBe(all.Count);
    }

    // ---------------------------------------------------------------- event log

    [Fact]
    public async Task Append_stores_redacted_events_and_tails_them_in_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await Fixture.Events.AppendAsync(
            new EventAppend(
                "jobs/one",
                null,
                "job.started",
                $"using key sk-{new string('a', 40)}",
                new JsonObject { ["token"] = $"sk-{new string('b', 40)}", ["note"] = "plain" },
                RenderLane.Output | RenderLane.Monitor,
                "user-1",
                1.5m,
                "jobs"),
            ct);
        var second = await Fixture.Events.AppendAsync(
            new EventAppend("jobs/one", null, "job.finished", "done", null, RenderLane.Log, null, null, "jobs"),
            ct);
        second.ShouldBeGreaterThan(first);

        var tailed = await TakeAsync(Fixture.Events.TailAsync("jobs/one", 0, ct), 2, ct);
        tailed.Select(e => e.Seq).ShouldBe(new[] { first, second });
        tailed[0].Summary.ShouldContain("[redacted:api-key]");
        tailed[0].Payload!["token"]!.GetValue<string>().ShouldBe("[redacted:api-key]");
        tailed[0].Payload!["note"]!.GetValue<string>().ShouldBe("plain");
        tailed[0].Subject.ShouldBe("user-1");
        tailed[0].Value.ShouldBe(1.5m);
        tailed[0].StreamType.ShouldBe("jobs");
        tailed[0].Lanes.ShouldBe(RenderLane.Output | RenderLane.Monitor);

        var afterFirst = await TakeAsync(Fixture.Events.TailAsync("jobs/one", first, ct), 1, ct);
        afterFirst.Single().Type.ShouldBe("job.finished");
    }

    [Fact]
    public async Task Tail_all_respects_the_render_lane_mask()
    {
        var ct = TestContext.Current.CancellationToken;
        await Fixture.Events.AppendAsync(
            new EventAppend("s", null, "out", "output", null, RenderLane.Output, null, null, null), ct);
        await Fixture.Events.AppendAsync(
            new EventAppend("s", null, "log", "log", null, RenderLane.Log, null, null, null), ct);

        var monitorOnly = await TakeAsync(
            Fixture.Events.TailAllAsync(RenderLane.Monitor, 0, ct),
            1,
            ct,
            TimeSpan.FromMilliseconds(500));
        monitorOnly.ShouldBeEmpty();

        var logs = await TakeAsync(Fixture.Events.TailAllAsync(RenderLane.Log, 0, ct), 1, ct);
        logs.Single().Type.ShouldBe("log");

        var everything = await TakeAsync(Fixture.Events.TailAllAsync(RenderLane.None, 0, ct), 2, ct);
        everything.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Range_read_returns_the_events_inside_the_half_open_interval()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await AppendAsync("jobs/range", "a", ct);
        var second = await AppendAsync("jobs/range", "b", ct);
        var third = await AppendAsync("jobs/range", "c", ct);
        await AppendAsync("jobs/other", "x", ct);
        var fourth = await AppendAsync("jobs/range", "d", ct);

        var all = await DrainAsync(Fixture.Events.ReadRangeAsync("jobs/range", 0, long.MaxValue, 0, null, ct), ct);
        all.ShouldNotBeNull();
        all!.Select(e => e.Seq).ShouldBe(new[] { first, second, third, fourth });
        all.Select(e => e.StreamId).ShouldBe(new[] { "jobs/range", "jobs/range", "jobs/range", "jobs/range" });

        // [from, to) is half-open and stream-scoped: seq 4 is excluded, the other stream never appears.
        var window = await DrainAsync(Fixture.Events.ReadRangeAsync("jobs/range", second, fourth, 0, null, ct), ct);
        window.ShouldNotBeNull();
        window!.Select(e => e.Seq).ShouldBe(new[] { second, third });
        window.Select(e => e.Type).ShouldBe(new[] { "b", "c" });
    }

    [Fact]
    public async Task Range_paging_with_a_limit_visits_every_seq_exactly_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var expected = new List<long>();
        for (var i = 0; i < 7; i++)
            expected.Add(await AppendAsync("jobs/page", $"e{i}", ct));

        const int limit = 3;
        var seen = new List<long>();
        var from = 0L;
        while (true)
        {
            var page = await DrainAsync(Fixture.Events.ReadRangeAsync("jobs/page", from, long.MaxValue, limit, null, ct), ct);
            page.ShouldNotBeNull();
            seen.AddRange(page!.Select(e => e.Seq));
            if (page.Count < limit)
                break;
            from = page[^1].Seq + 1;
        }

        seen.ShouldBe(expected);
        seen.Distinct().Count().ShouldBe(seen.Count);
    }

    [Fact]
    public async Task Range_read_honours_the_render_lane_mask()
    {
        var ct = TestContext.Current.CancellationToken;
        await AppendAsync("jobs/lanes", "out", ct, RenderLane.Output);
        await AppendAsync("jobs/lanes", "log", ct, RenderLane.Log);
        await AppendAsync("jobs/lanes", "both", ct, RenderLane.Output | RenderLane.Monitor);

        var logs = await DrainAsync(
            Fixture.Events.ReadRangeAsync("jobs/lanes", 0, long.MaxValue, 0, RenderLane.Log, ct),
            ct);
        logs.ShouldNotBeNull();
        logs!.Select(e => e.Type).ShouldBe(new[] { "log" });

        var none = await DrainAsync(
            Fixture.Events.ReadRangeAsync("jobs/lanes", 0, long.MaxValue, 0, RenderLane.None, ct),
            ct);
        none.ShouldNotBeNull();
        none!.Count.ShouldBe(3);

        var unfiltered = await DrainAsync(Fixture.Events.ReadRangeAsync("jobs/lanes", 0, long.MaxValue, 0, null, ct), ct);
        unfiltered.ShouldNotBeNull();
        unfiltered!.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Range_read_of_an_empty_stream_completes_with_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var unknown = await DrainAsync(Fixture.Events.ReadRangeAsync("jobs/missing", 0, long.MaxValue, 0, null, ct), ct);
        unknown.ShouldNotBeNull("ReadRangeAsync must complete instead of tailing an empty stream");
        unknown!.ShouldBeEmpty();

        var only = await AppendAsync("jobs/gap", "only", ct);
        var pastHead = await DrainAsync(
            Fixture.Events.ReadRangeAsync("jobs/gap", only + 10, only + 20, 0, null, ct),
            ct);
        pastHead.ShouldNotBeNull("a window with no matches must complete too");
        pastHead!.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- leases

    [Fact]
    public async Task Leases_fence_and_expire()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await Fixture.Leases.AcquireAsync("repo:main", "owner-a", TimeSpan.FromMinutes(5), ct);
        first.ShouldNotBeNull();
        first!.FencingToken.ShouldBe("1");
        first.ExpiresAt.ShouldBe(Clock.UtcNow + TimeSpan.FromMinutes(5));

        (await Fixture.Leases.AcquireAsync("repo:main", "owner-b", TimeSpan.FromMinutes(5), ct)).ShouldBeNull();
        (await Fixture.Leases.RenewAsync("repo:main", "owner-b", "1", TimeSpan.FromMinutes(5), ct)).ShouldBeFalse();
        (await Fixture.Leases.RenewAsync("repo:main", "owner-a", "9", TimeSpan.FromMinutes(5), ct)).ShouldBeFalse();
        (await Fixture.Leases.RenewAsync("repo:main", "owner-a", "1", TimeSpan.FromMinutes(30), ct)).ShouldBeTrue();

        var fetched = await Fixture.Leases.GetAsync("repo:main", ct);
        fetched!.Owner.ShouldBe("owner-a");
        fetched.ExpiresAt.ShouldBe(Clock.UtcNow + TimeSpan.FromMinutes(30));

        (await Fixture.Leases.ListExpiredAsync(ct)).ShouldBeEmpty();
        Clock.Advance(TimeSpan.FromMinutes(31));
        (await Fixture.Leases.ListExpiredAsync(ct)).Single().Resource.ShouldBe("repo:main");

        var second = await Fixture.Leases.AcquireAsync("repo:main", "owner-b", TimeSpan.FromMinutes(5), ct);
        second!.FencingToken.ShouldBe("2");

        await Fixture.Leases.ReleaseAsync("repo:main", "owner-a", "2", ct);
        (await Fixture.Leases.GetAsync("repo:main", ct))!.Owner.ShouldBe("owner-b");

        await Fixture.Leases.ReleaseAsync("repo:main", "owner-b", "2", ct);
        (await Fixture.Leases.GetAsync("repo:main", ct)).ShouldBeNull();
        var third = await Fixture.Leases.AcquireAsync("repo:main", "owner-c", TimeSpan.FromMinutes(5), ct);
        third!.FencingToken.ShouldBe("3");
    }

    // ---------------------------------------------------------------- outbox

    [Fact]
    public async Task Outbox_claims_retries_and_buries()
    {
        var ct = TestContext.Current.CancellationToken;
        var message = new OutboxMessage(Guid.NewGuid(), Guid.NewGuid(), "job.updated", new JsonObject { ["state"] = "ok" });
        await Fixture.Outbox.EnqueueAsync(message, ct);
        await Fixture.Outbox.EnqueueAsync(message, ct);

        var firstClaim = await Fixture.Outbox.ClaimAsync(10, Clock.UtcNow, ct);
        firstClaim.Count.ShouldBe(1);
        firstClaim[0].Status.ShouldBe(OutboxStatus.InFlight);
        firstClaim[0].Attempts.ShouldBe(1);

        (await Fixture.Outbox.ClaimAsync(10, Clock.UtcNow, ct)).ShouldBeEmpty();

        await Fixture.Outbox.RetryLaterAsync(message.MessageId, Clock.UtcNow + TimeSpan.FromSeconds(30), ct);
        (await Fixture.Outbox.ClaimAsync(10, Clock.UtcNow, ct)).ShouldBeEmpty();
        var retried = await Fixture.Outbox.ClaimAsync(10, Clock.UtcNow + TimeSpan.FromSeconds(31), ct);
        retried.Single().Attempts.ShouldBe(2);
        retried[0].Message.Payload["state"]!.GetValue<string>().ShouldBe("ok");

        await Fixture.Outbox.MarkSentAsync(message.MessageId, ct);
        (await Fixture.Outbox.ClaimAsync(10, Clock.UtcNow + TimeSpan.FromHours(1), ct)).ShouldBeEmpty();

        var dead = new OutboxMessage(Guid.NewGuid(), Guid.NewGuid(), "job.updated", new JsonObject { ["state"] = "dead" });
        await Fixture.Outbox.EnqueueAsync(dead, ct);
        await Fixture.Outbox.MarkDeadAsync(dead.MessageId, "too many attempts", ct);
        var deadList = await Fixture.Outbox.ListDeadAsync(ct);
        deadList.Single().Message.MessageId.ShouldBe(dead.MessageId);
        deadList[0].DeadReason.ShouldBe("too many attempts");
    }

    // ---------------------------------------------------------------- decision cache

    [Fact]
    public async Task Decision_cache_round_trips_and_overwrites()
    {
        var ct = TestContext.Current.CancellationToken;
        (await Fixture.Decisions.GetAsync("stream-1", "gate", ct)).ShouldBeNull();

        await Fixture.Decisions.SetAsync("stream-1", "gate", new SampleDecision("allow", 3), ct);
        var hit = await Fixture.Decisions.GetAsync("stream-1", "gate", ct);
        hit.ShouldNotBeNull();
        hit!.Verdict.ShouldBe("allow");
        hit.Score.ShouldBe(3);

        (await Fixture.Decisions.GetAsync("stream-1", "other", ct)).ShouldBeNull();

        await Fixture.Decisions.SetAsync("stream-1", "gate", new SampleDecision("deny", 9), ct);
        (await Fixture.Decisions.GetAsync("stream-1", "gate", ct))!.Verdict.ShouldBe("deny");
    }

    // ---------------------------------------------------------------- artifacts

    [Fact]
    public async Task Artifacts_redact_text_and_round_trip_binary()
    {
        var ct = TestContext.Current.CancellationToken;
        var secret = $"prefix sk-{new string('c', 40)} suffix";
        await Fixture.Artifacts.PutAsync(
            "reports/one.txt",
            new MemoryStream(Encoding.UTF8.GetBytes(secret)),
            "text/plain",
            null,
            ct);

        (await Fixture.Artifacts.ExistsAsync("reports/one.txt", ct)).ShouldBeTrue();
        (await Fixture.Artifacts.ExistsAsync("reports/missing.txt", ct)).ShouldBeFalse();
        (await Fixture.Artifacts.GetAsync("reports/missing.txt", ct)).ShouldBeNull();

        var blob = await Fixture.Artifacts.GetAsync("reports/one.txt", ct);
        blob.ShouldNotBeNull();
        using (var reader = new StreamReader(blob!.Content))
        {
            var text = await reader.ReadToEndAsync(ct);
            text.ShouldNotContain("sk-");
            text.ShouldContain("[redacted:api-key]");
        }

        byte[] payload = [0, 1, 2, 250, 251, 255];
        await Fixture.Artifacts.PutAsync("blobs/one.bin", new MemoryStream(payload), "application/octet-stream", null, ct);
        var binary = await Fixture.Artifacts.GetAsync("blobs/one.bin", ct);
        binary!.ContentType.ShouldBe("application/octet-stream");
        using var buffer = new MemoryStream();
        await binary.Content.CopyToAsync(buffer, ct);
        buffer.ToArray().ShouldBe(payload);
    }

    [Fact]
    public async Task Artifacts_refuse_keys_that_escape_the_root()
    {
        var ct = TestContext.Current.CancellationToken;
        await Should.ThrowAsync<ArgumentException>(async () =>
            await Fixture.Artifacts.PutAsync("../escape.txt", new MemoryStream([1]), "text/plain", null, ct));
        await Should.ThrowAsync<ArgumentException>(async () => await Fixture.Artifacts.ExistsAsync("", ct));
    }

    // ---------------------------------------------------------------- helpers

    private Task<long> AppendAsync(
        string streamId,
        string type,
        CancellationToken ct,
        RenderLane lanes = RenderLane.Log) =>
        Fixture.Events.AppendAsync(
            new EventAppend(streamId, null, type, type, null, lanes, null, null, null),
            ct).AsTask();

    /// <summary>Drains a bounded read; null means it did not complete inside the timeout.</summary>
    private static async Task<List<LedgerEvent>?> DrainAsync(
        IAsyncEnumerable<LedgerEvent> source,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        var drained = new List<LedgerEvent>();
        var deadline = Task.Delay(timeout ?? TimeSpan.FromSeconds(5), ct);
        var enumerator = source.GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                var move = enumerator.MoveNextAsync().AsTask();
                if (await Task.WhenAny(move, deadline) == deadline)
                    return null;
                if (!await move)
                    return drained;
                drained.Add(enumerator.Current);
            }
        }
        finally
        {
            try
            {
                await enumerator.DisposeAsync();
            }
            catch (NotSupportedException)
            {
            }
        }
    }

    /// <summary>Reads up to <paramref name="count"/> events, failing the test if they do not arrive.</summary>
    private static async Task<List<LedgerEvent>> TakeAsync(
        IAsyncEnumerable<LedgerEvent> source,
        int count,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        var taken = new List<LedgerEvent>();
        var deadline = Task.Delay(timeout ?? TimeSpan.FromSeconds(10), ct);
        var enumerator = source.GetAsyncEnumerator(ct);
        try
        {
            while (taken.Count < count)
            {
                var move = enumerator.MoveNextAsync().AsTask();
                var finished = await Task.WhenAny(move, deadline);
                if (finished == deadline)
                    break;
                if (!await move)
                    break;
                taken.Add(enumerator.Current);
            }
        }
        finally
        {
            // Compiler-generated async iterators reject DisposeAsync for some shapes; the
            // fixture is torn down either way.
            try
            {
                await enumerator.DisposeAsync();
            }
            catch (NotSupportedException)
            {
            }
        }

        return taken;
    }

    // ---------------------------------------------------------------- payload cap (N6)

    [Fact]
    public async Task Event_payload_over_the_cap_spills_identically_in_both_stores()
    {
        var ct = TestContext.Current.CancellationToken;
        var big = new JsonObject { ["blob"] = new string('x', 9000) };
        var seq = await Fixture.Events.AppendAsync(
            new EventAppend("s/spill", null, "big", "big", big, RenderLane.Output, null, null, null), ct);

        var evt = (await TakeAsync(Fixture.Events.TailAsync("s/spill", seq - 1, ct), 1, ct)).Single();
        evt.Payload.ShouldNotBeNull();
        var spill = evt.Payload!["$spill"];
        spill.ShouldNotBeNull();
        spill!["bytes"]!.GetValue<int>().ShouldBeGreaterThan(8192);
        spill["sha256"]!.GetValue<string>().Length.ShouldBe(64);

        var key = spill["artifactKey"]!.GetValue<string>();
        var blob = await Fixture.Artifacts.GetAsync(key, ct);
        blob.ShouldNotBeNull();
        using var reader = new StreamReader(blob!.Content, Encoding.UTF8);
        (await reader.ReadToEndAsync(ct)).ShouldBe(big.ToJsonString());
    }

    // ---------------------------------------------------------------- operations

    [Fact]
    public async Task Operations_start_report_and_reach_a_terminal_status()
    {
        var ct = TestContext.Current.CancellationToken;
        await Fixture.Operations.StartAsync("op1", "s1", "Burbuja.Agents", "local", "/target", new JsonObject { ["step"] = 0 }, ct);

        var started = await Fixture.Operations.GetAsync("op1", ct);
        started.ShouldNotBeNull();
        started!.Status.ShouldBe(OperationStatus.Running);
        started.PercentComplete.ShouldBe(0);

        Clock.Advance(TimeSpan.FromSeconds(1));
        await Fixture.Operations.ReportAsync("op1", 50, new JsonObject { ["step"] = 1 }, ct);
        (await Fixture.Operations.GetAsync("op1", ct))!.PercentComplete.ShouldBe(50);

        await Fixture.Operations.SucceedAsync("op1", new JsonObject { ["ok"] = true }, ct);
        var done = await Fixture.Operations.GetAsync("op1", ct);
        done!.Status.ShouldBe(OperationStatus.Succeeded);
        done.EndTime.ShouldNotBeNull();
        done.PercentComplete.ShouldBe(100);

        await Fixture.Operations.StartAsync("op2", "s1", "Burbuja.Agents", "local", null, null, ct);
        await Fixture.Operations.FailAsync("op2", ArmError.Conflict("Boom", "failed"), ct);
        (await Fixture.Operations.GetAsync("op2", ct))!.Status.ShouldBe(OperationStatus.Failed);

        var list = await Fixture.Operations.ListAsync("s1", "Burbuja.Agents", ct);
        list.Count.ShouldBe(2);
    }

    // ---------------------------------------------------------------- idempotency

    [Fact]
    public void Idempotency_begin_is_exclusive_and_get_expires()
    {
        Fixture.Idempotency.TryBegin("k1").ShouldBeTrue();
        Fixture.Idempotency.TryBegin("k1").ShouldBeFalse();
        Fixture.Idempotency.End("k1");
        Fixture.Idempotency.TryBegin("k1").ShouldBeTrue();

        var record = new IdempotencyRecord(
            "hash",
            new JsonObject { ["ok"] = true },
            200,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Clock.UtcNow.AddMinutes(1));
        Fixture.Idempotency.Put("k2", record);

        var read = Fixture.Idempotency.Get("k2");
        read.ShouldNotBeNull();
        read!.StatusCode.ShouldBe(200);

        Clock.Advance(TimeSpan.FromMinutes(2));
        Fixture.Idempotency.Get("k2").ShouldBeNull();
    }

    // ---------------------------------------------------------------- approvals

    [Fact]
    public void Approval_records_and_grants_round_trip()
    {
        var request = new ApprovalRequest(
            "stream/1", null, ApprovalKind.Permission, "job", "j1", "dk", null, "low", "bash", null, "why", [], true, TimeSpan.FromMinutes(5));
        var record = new ApprovalRecord("a1", request, null, Clock.UtcNow, Clock.UtcNow.AddMinutes(5), null, null);

        Fixture.Approvals.Get("a1").ShouldBeNull();
        Fixture.Approvals.Put(record);
        var read = Fixture.Approvals.Get("a1");
        read.ShouldNotBeNull();
        read!.Request.DecisionKey.ShouldBe("dk");
        Fixture.Approvals.All().Count.ShouldBe(1);

        Fixture.Approvals.PutGrant(new ApprovalGrant("dk", null, "stream/1", "/", Clock.UtcNow.AddHours(1)));
        Fixture.Approvals.FindGrant("dk", null, transition: null, Clock.UtcNow).ShouldNotBeNull();
        Clock.Advance(TimeSpan.FromHours(2));
        Fixture.Approvals.FindGrant("dk", null, transition: null, Clock.UtcNow).ShouldBeNull();

        Fixture.Approvals.PutGrant(new ApprovalGrant("dk2", null, "stream/1", null, Clock.UtcNow.AddHours(1)));
        Fixture.Approvals.RemoveGrantsForStream("stream/1");
        Fixture.Approvals.FindGrant("dk2", null, transition: null, Clock.UtcNow).ShouldBeNull();
    }
}

/// <summary>Same contract, kernel in-memory stores.</summary>
public sealed class InMemoryStoreContractTests : StoreContractTests
{
    protected override IStoreFixture CreateFixture() => new InMemoryStoreFixture();
}

/// <summary>Same contract, SQLite plugin stores.</summary>
public sealed class SqliteStoreContractTests : StoreContractTests
{
    protected override IStoreFixture CreateFixture() => new SqliteStoreFixture();
}

/// <summary>
/// The skip-token wire format is a public SDK contract (C2), so a token minted while paging one
/// store must be accepted by the other.
/// </summary>
public sealed class CrossStoreSkipTokenTests
{
    [Fact]
    public async Task Skip_token_from_one_store_is_accepted_by_the_other()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var memory = new InMemoryStoreFixture();
        await using var sqlite = new SqliteStoreFixture();

        foreach (var fixture in new IStoreFixture[] { memory, sqlite })
        {
            for (var i = 0; i < 3; i++)
            {
                await fixture.Resources.UpsertAsync(
                    new ResourceEnvelope<JsonObject>(
                        $"/subscriptions/s1/resourceGroups/rg/providers/Burbuja.Pomnibot/jobs/j{i}",
                        $"j{i}",
                        "Burbuja.Pomnibot/jobs",
                        "local",
                        new Dictionary<string, string>(),
                        new JsonObject(),
                        ProvisioningState.Succeeded,
                        new SystemData(DateTimeOffset.UnixEpoch.AddSeconds(i), "t", "User", null, null, null),
                        "W/\"1\""),
                    null,
                    ct);
            }
        }

        var memoryFirst = await memory.Resources.ListAsync(new ResourceStoreQuery(null, null, "Burbuja.Pomnibot", null, 2, null), ct);
        var sqliteFirst = await sqlite.Resources.ListAsync(new ResourceStoreQuery(null, null, "Burbuja.Pomnibot", null, 2, null), ct);

        memoryFirst.NextLink.ShouldNotBeNullOrEmpty();
        sqliteFirst.NextLink.ShouldNotBeNullOrEmpty();

        // Swap the tokens: each store must accept the other's continuation token.
        var memoryPage2 = await memory.Resources.ListAsync(
            new ResourceStoreQuery(null, null, "Burbuja.Pomnibot", null, 2, sqliteFirst.NextLink), ct);
        var sqlitePage2 = await sqlite.Resources.ListAsync(
            new ResourceStoreQuery(null, null, "Burbuja.Pomnibot", null, 2, memoryFirst.NextLink), ct);

        memoryFirst.Value.Concat(memoryPage2.Value).Select(e => e.Id).Distinct().Count().ShouldBe(3);
        sqliteFirst.Value.Concat(sqlitePage2.Value).Select(e => e.Id).Distinct().Count().ShouldBe(3);
    }
}
