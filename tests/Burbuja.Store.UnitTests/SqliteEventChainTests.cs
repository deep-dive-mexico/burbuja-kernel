using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Redaction;
using Burbuja.Plugins.Store.Sqlite;
using Shouldly;
using Xunit;

namespace Burbuja.Store.UnitTests;

/// <summary>
/// H1 on the durable store: the chain is written inside the same transaction as the row, it survives a
/// restart, a pre-chain database is migrated and backfilled, and — the fact that justifies keeping the
/// global <c>Seq</c> out of the hash — <b>the same events hash the same in both stores</b>.
/// </summary>
public sealed class SqliteEventChainTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Directory.CreateTempSubdirectory("burbuja-chain-").FullName;
    private readonly TestClock _clock = new();
    private readonly CaptureGroupRedactor _redactor = new();
    private SqliteDatabase _database = null!;

    public ValueTask InitializeAsync()
    {
        _database = new SqliteDatabase(Path.Combine(_root, "chain.db"));
        return default;
    }

    public async ValueTask DisposeAsync()
    {
        await _database.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static EventAppend Evt(string stream, string summary, JsonNode? payload = null) =>
        new(stream, null, "test.event", summary, payload, RenderLane.Log, null, null, "test.stream");

    private async Task<IReadOnlyList<LedgerEvent>> ReadAsync(SqliteEventLog log, string stream)
    {
        var list = new List<LedgerEvent>();
        await foreach (var e in log.ReadRangeAsync(stream, 0, long.MaxValue, 0, null, Ct))
            list.Add(e);
        return list;
    }

    [Fact]
    public async Task Rows_are_written_with_a_chain_that_verifies()
    {
        var log = new SqliteEventLog(_database, _redactor, _clock);
        for (var i = 0; i < 4; i++)
            await log.AppendAsync(Evt("s", $"e{i}", new JsonObject { ["i"] = i }), Ct);

        var events = await ReadAsync(log, "s");

        events.Select(e => e.StreamSeq).ShouldBe([1L, 2L, 3L, 4L]);
        events[0].PreviousHash.ShouldBe(EventChain.Genesis);
        events[1].PreviousHash.ShouldBe(events[0].Hash);
        EventChain.Verify(events).ShouldBeNull();
    }

    [Fact]
    public async Task The_chain_survives_a_restart_on_a_fresh_handle()
    {
        var writer = new SqliteEventLog(_database, _redactor, _clock);
        for (var i = 0; i < 3; i++)
            await writer.AppendAsync(Evt("s", $"e{i}"), Ct);

        // A brand-new handle on the same file: the same recovery path a kill -9 exercises.
        await using var reopened = new SqliteDatabase(Path.Combine(_root, "chain.db"));
        var reader = new SqliteEventLog(reopened, _redactor, _clock);
        var events = await ReadAsync(reader, "s");

        events.Count.ShouldBe(3);
        EventChain.Verify(events).ShouldBeNull("the chain is in the file, not in the writer's memory");
    }

    /// <summary>
    /// The fact that justifies the design: the same events hash the same in both stores. The two stores
    /// assign DIFFERENT global sequence numbers to identical content — the in-memory log counts from its
    /// own process, SQLite's AUTOINCREMENT from the file — so if the hash covered <c>Seq</c> the chain
    /// would be a property of the store rather than of the events, and a row copied between them could
    /// never be verified.
    /// </summary>
    [Fact]
    public async Task The_same_events_hash_the_same_in_both_stores()
    {
        var sqlite = new SqliteEventLog(_database, _redactor, _clock);
        // The SAME clock, deliberately: `At` is part of the canonical form, so two stores told different
        // times produce different hashes — correctly, because they did not record the same event. The
        // property this fact is about is that the hash covers the CONTENT and not the store, and a shared
        // clock is what makes the two stores write the same content. (The first version of this fact used
        // two clocks and failed, which is the fact telling the truth.)
        var memory = new Burbuja.Core.InMemory.InMemoryEventLog(_redactor, _clock);

        // Offset SQLite's counter so the two stores genuinely number identical content differently. Left
        // alone they both start at 1 and the fact would pass without testing anything — which is how the
        // first version of it read.
        await sqlite.AppendAsync(Evt("other", "offset the AUTOINCREMENT"), Ct);

        foreach (var (summary, payload) in new[]
        {
            ("first", new JsonObject { ["b"] = 2, ["a"] = 1 }),
            ("second", new JsonObject { ["nested"] = new JsonObject { ["z"] = true, ["y"] = "s" } }),
            ("third", null),
        })
        {
            await sqlite.AppendAsync(Evt("s", summary, payload?.DeepClone()), Ct);
            await memory.AppendAsync(Evt("s", summary, payload?.DeepClone()), Ct);
        }

        var fromSqlite = await ReadAsync(sqlite, "s");
        var fromMemory = new List<LedgerEvent>();
        await foreach (var e in memory.ReadRangeAsync("s", 0, long.MaxValue, 0, null, Ct))
            fromMemory.Add(e);

        fromSqlite.Select(e => e.Seq).ShouldNotBe(fromMemory.Select(e => e.Seq), "the stores number rows differently");
        fromSqlite.Select(e => e.Hash).ShouldBe(fromMemory.Select(e => e.Hash));
    }

    /// <summary>
    /// A database written before the chain existed. Its rows carry <c>stream_seq = 0</c> and a null hash
    /// until the migration backfills them — and an unmigrated row verifies as tampered, which would make
    /// every pre-existing event in a live database look like an attack.
    /// </summary>
    [Fact]
    public async Task A_pre_chain_database_is_migrated_and_backfilled()
    {
        var path = Path.Combine(_root, "legacy.db");
        var legacySchema = """
            CREATE TABLE IF NOT EXISTS events(
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                stream_id TEXT NOT NULL,
                sub_stream_id TEXT,
                type TEXT NOT NULL,
                summary TEXT NOT NULL,
                payload TEXT,
                lanes INTEGER NOT NULL,
                subject TEXT,
                value TEXT,
                stream_type TEXT,
                at TEXT NOT NULL);
            """;

        // Hand-build a pre-chain database, exactly as an older build would have left it.
        await using (var legacy = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            await legacy.OpenAsync(Ct);
            await using (var create = legacy.CreateCommand())
            {
                create.CommandText = legacySchema;
                await create.ExecuteNonQueryAsync(Ct);
            }

            for (var i = 0; i < 3; i++)
            {
                await using var insert = legacy.CreateCommand();
                insert.CommandText = "INSERT INTO events(stream_id, sub_stream_id, type, summary, payload, lanes, subject, value, stream_type, at) VALUES ('s', NULL, 't', $s, NULL, 1, NULL, NULL, 'st', $at)";
                insert.Parameters.AddWithValue("$s", $"legacy{i}");
                insert.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                await insert.ExecuteNonQueryAsync(Ct);
            }

            await using var stamp = legacy.CreateCommand();
            stamp.CommandText = "PRAGMA user_version = 0";
            await stamp.ExecuteNonQueryAsync(Ct);
        }

        // Opening it through this build runs the migration.
        await using var migrated = new SqliteDatabase(path);
        await migrated.EnsureInitializedAsync(Ct);
        var log = new SqliteEventLog(migrated, _redactor, _clock);

        var events = await ReadAsync(log, "s");
        events.Count.ShouldBe(3);
        events.Select(e => e.StreamSeq).ShouldBe([1L, 2L, 3L]);
        events.ShouldAllBe(e => e.Hash != null);
        EventChain.Verify(events).ShouldBeNull("a backfilled database must verify, or every old event reads as a tamper");
    }
}
