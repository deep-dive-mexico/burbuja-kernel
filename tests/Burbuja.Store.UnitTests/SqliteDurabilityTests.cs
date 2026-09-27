using System.Text;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Outbox;
using Burbuja.Core.Redaction;
using Burbuja.Plugins.Store.Sqlite;
using Shouldly;
using Xunit;

namespace Burbuja.Store.UnitTests;

/// <summary>
/// Durability proof for P6: state written through one database handle is visible again through a
/// brand-new handle on the same file, without any graceful shutdown in between. That is the same
/// recovery path a <c>kill -9</c> of the host exercises (SQLite replays the WAL on next open).
/// </summary>
public sealed class SqliteDurabilityTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("pomnibot-durability-").FullName;
    private SqliteDatabase _abandoned = null!;
    private string _databasePath = null!;
    private string _artifactRoot = null!;
    private readonly TestClock _clock = new();

    public ValueTask InitializeAsync()
    {
        _databasePath = Path.Combine(_root, "pomnibot.db");
        _artifactRoot = Path.Combine(_root, "artifacts");
        _abandoned = new SqliteDatabase(_databasePath);
        return default;
    }

    public async ValueTask DisposeAsync()
    {
        await _abandoned.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task State_survives_an_abandoned_handle_and_a_fresh_one_reads_it_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var redactor = new CaptureGroupRedactor();

        var resourceId = "/subscriptions/s1/resourceGroups/rg/providers/Burbuja.Pomnibot/jobs/job-1";
        var message = new OutboxMessage(Guid.NewGuid(), Guid.NewGuid(), "job.updated", new JsonObject { ["state"] = "running" });

        // ---- first "process": write, then walk away without disposing anything.
        var writerResources = new SqliteResourceStore(_abandoned, _clock);
        var writerEvents = new SqliteEventLog(_abandoned, redactor, _clock);
        var writerLeases = new SqliteLeaseStore(_abandoned, _clock);
        var writerOutbox = new SqliteOutbox(_abandoned);
        var writerCache = new SqliteDecisionCache<SampleDecision>(_abandoned);
        var writerArtifacts = new FileArtifactStore(_artifactRoot, redactor);

        var created = await writerResources.UpsertAsync(
            new ResourceEnvelope<JsonObject>(
                resourceId,
                "job-1",
                "Burbuja.Pomnibot/jobs",
                "westus",
                new Dictionary<string, string> { ["env"] = "test" },
                new JsonObject { ["goal"] = "ship P6" },
                ProvisioningState.Succeeded,
                new SystemData(null, "tester", "User", null, null, null),
                ""),
            null,
            ct);
        created.ETag.ShouldBe("W/\"1\"");
        var seq = await writerEvents.AppendAsync(
            new EventAppend(resourceId, null, "job.started", "started", new JsonObject { ["n"] = 1 }, RenderLane.Output, "user-1", 2.5m, "jobs"),
            ct);
        var lease = await writerLeases.AcquireAsync(resourceId, "worker-a", TimeSpan.FromMinutes(10), ct);
        await writerOutbox.EnqueueAsync(message, ct);
        await writerCache.SetAsync(resourceId, "gate", new SampleDecision("allow", 7), ct);
        await writerArtifacts.PutAsync("reports/last.txt", new MemoryStream(Encoding.UTF8.GetBytes("all good")), "text/plain", null, ct);

        // ---- second "process": a new handle on the same file, no cooperation from the first.
        await using var recovered = new SqliteDatabase(_databasePath);
        var resources = new SqliteResourceStore(recovered, _clock);
        var events = new SqliteEventLog(recovered, redactor, _clock);
        var leases = new SqliteLeaseStore(recovered, _clock);
        var outbox = new SqliteOutbox(recovered);
        var cache = new SqliteDecisionCache<SampleDecision>(recovered);
        var artifacts = new FileArtifactStore(_artifactRoot, redactor);

        var reread = await resources.GetAsync(resourceId, ct);
        reread.ShouldNotBeNull();
        reread!.ETag.ShouldBe(created.ETag);
        reread.Properties["goal"]!.GetValue<string>().ShouldBe("ship P6");
        reread.Tags["env"].ShouldBe("test");

        var updated = await resources.UpsertAsync(reread with { Name = "job-1-renamed" }, reread.ETag, ct);
        updated.ETag.ShouldBe("W/\"2\"");

        var tailed = new List<LedgerEvent>();
        await foreach (var e in events.TailAsync(resourceId, 0, ct))
        {
            tailed.Add(e);
            if (tailed.Count == 1)
                break;
        }

        tailed.Single().Seq.ShouldBe(seq);
        tailed[0].Type.ShouldBe("job.started");
        tailed[0].Value.ShouldBe(2.5m);
        tailed[0].Subject.ShouldBe("user-1");

        var recoveredLease = await leases.GetAsync(resourceId, ct);
        recoveredLease.ShouldNotBeNull();
        recoveredLease!.Owner.ShouldBe("worker-a");
        recoveredLease.FencingToken.ShouldBe(lease!.FencingToken);
        (await leases.RenewAsync(resourceId, "worker-a", lease.FencingToken, TimeSpan.FromMinutes(20), ct)).ShouldBeTrue();

        var claimed = await outbox.ClaimAsync(5, _clock.UtcNow, ct);
        claimed.Single().Message.MessageId.ShouldBe(message.MessageId);
        claimed[0].Attempts.ShouldBe(1);

        (await cache.GetAsync(resourceId, "gate", ct))!.Verdict.ShouldBe("allow");

        var blob = await artifacts.GetAsync("reports/last.txt", ct);
        blob.ShouldNotBeNull();
        using (var reader = new StreamReader(blob!.Content))
            (await reader.ReadToEndAsync(ct)).ShouldBe("all good");
    }
}
