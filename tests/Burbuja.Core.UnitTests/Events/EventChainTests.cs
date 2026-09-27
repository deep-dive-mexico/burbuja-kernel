using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Policy;
using Burbuja.Core.Abstractions.Redaction;
using Burbuja.Core.Clock;
using Burbuja.Core.InMemory;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Events;

/// <summary>
/// H1 — the tamper-evident chain.
///
/// <para>The ledger is the record of what the host decided and why: the gate's verdicts, the approvals,
/// the bookings, the tool calls. Every other guarantee here rests on that record being what it was, and
/// until this existed the only thing behind it was the file's own permissions — anything that could write
/// to the database could rewrite history and nothing would notice. A hash chain makes a rewrite
/// detectable without trusting the writer, which is the difference between an audit trail and a log.</para>
/// </summary>
public sealed class EventChainTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class NullRedactor : IRedactor
    {
        public string Redact(string input) => input;
    }

    private static InMemoryEventLog NewLog() => new(new NullRedactor(), new TimeProviderClock(TimeProvider.System));

    private static EventAppend Evt(string stream, string type, string summary, JsonNode? payload = null) =>
        new(stream, null, type, summary, payload, RenderLane.Log, null, null, "test.stream");

    [Fact]
    public async Task Each_stream_gets_its_own_dense_sequence()
    {
        // The global `Seq` is the store's insertion counter and interleaves every stream; the chain needs
        // a position WITHIN a stream or a verifier cannot tell a gap from an unrelated job's traffic.
        var log = NewLog();
        await log.AppendAsync(Evt("a", "t", "a1"), Ct);
        await log.AppendAsync(Evt("b", "t", "b1"), Ct);
        await log.AppendAsync(Evt("a", "t", "a2"), Ct);

        var a = new List<LedgerEvent>();
        await foreach (var e in log.ReadRangeAsync("a", 0, long.MaxValue, 0, null, Ct))
            a.Add(e);

        a.Count.ShouldBe(2);
        a.Select(e => e.StreamSeq).ShouldBe([1L, 2L]);
        a.Select(e => e.Seq).ShouldNotBe([1L, 2L], "the global sequence deliberately does interleave");
        a[0].PreviousHash.ShouldBe(EventChain.Genesis);
        a[1].PreviousHash.ShouldBe(a[0].Hash);
    }

    [Fact]
    public async Task A_stream_verifies_when_nothing_was_touched()
    {
        var log = NewLog();
        for (var i = 0; i < 5; i++)
            await log.AppendAsync(Evt("a", "t", $"e{i}", new JsonObject { ["i"] = i }), Ct);

        EventChain.Verify(await ReadAsync(log, "a")).ShouldBeNull();
    }

    [Fact]
    public async Task An_edited_summary_is_reported_as_a_break_at_that_event()
    {
        var log = NewLog();
        for (var i = 0; i < 4; i++)
            await log.AppendAsync(Evt("a", "t", $"e{i}"), Ct);
        var events = await ReadAsync(log, "a");

        // The tamper: somebody with write access changes what the record says, and leaves the hash alone —
        // which is the only thing an attacker who does not understand the chain can do.
        var edited = events.ToList();
        edited[2] = edited[2] with { Summary = "nothing to see here" };

        var broke = EventChain.Verify(edited);
        broke.ShouldNotBeNull();
        broke!.StreamSeq.ShouldBe(3);
        broke.StreamId.ShouldBe("a");
        broke.Reason.ShouldContain("does not match its hash");
    }

    [Fact]
    public async Task A_removed_event_is_reported_as_a_gap()
    {
        var log = NewLog();
        for (var i = 0; i < 4; i++)
            await log.AppendAsync(Evt("a", "t", $"e{i}"), Ct);
        var events = (await ReadAsync(log, "a")).ToList();

        // Deleting the middle: the sequence is no longer dense, which is what "an event is missing" looks
        // like from the inside.
        events.RemoveAt(1);

        var broke = EventChain.Verify(events);
        broke.ShouldNotBeNull();
        broke!.Reason.ShouldContain("expected 2");
    }

    [Fact]
    public async Task A_reordered_pair_is_reported_as_a_cut_chain()
    {
        var log = NewLog();
        for (var i = 0; i < 3; i++)
            await log.AppendAsync(Evt("a", "t", $"e{i}"), Ct);
        var events = (await ReadAsync(log, "a")).ToList();
        (events[1], events[2]) = (events[2], events[1]);

        EventChain.Verify(events).ShouldNotBeNull().Reason.ShouldContain("expected");
    }

    [Fact]
    public async Task An_event_with_no_hash_is_a_break_not_a_pass()
    {
        // The failure mode that matters: a store that stopped chaining would otherwise verify as clean,
        // because there is nothing to disagree with.
        var log = NewLog();
        await log.AppendAsync(Evt("a", "t", "e0"), Ct);
        var events = (await ReadAsync(log, "a")).ToList();

        EventChain.Verify([events[0] with { Hash = null }]).ShouldNotBeNull()
            .Reason.ShouldContain("no hash");
    }

    [Fact]
    public async Task The_global_sequence_is_not_part_of_the_hash()
    {
        // Two stores holding the same events disagree about the global sequence — the in-memory lane's
        // rows carry its own numbers and the SQLite lane's carry theirs. Hashing it would make the chain a
        // property of the STORE rather than of the events, and "the same events hash the same" false.
        var log = NewLog();
        await log.AppendAsync(Evt("a", "t", "e0", new JsonObject { ["x"] = 1 }), Ct);
        var first = (await ReadAsync(log, "a")).Single();

        EventChain.HashOf(first with { Seq = 999 }).ShouldBe(first.Hash);
    }

    [Fact]
    public async Task The_payload_hash_does_not_depend_on_property_order()
    {
        // Canonical JSON: a payload written by a different serialiser — or edited by hand into a different
        // key order — must hash the same, or the chain breaks on a JSON round trip.
        var log = NewLog();
        await log.AppendAsync(Evt("a", "t", "e0", new JsonObject { ["b"] = 2, ["a"] = 1 }), Ct);
        var first = (await ReadAsync(log, "a")).Single();

        (first.Payload! as JsonObject)!.ShouldNotBeNull();
        var reordered = new JsonObject { ["a"] = 1, ["b"] = 2 };
        EventChain.HashOf(first with { Payload = reordered }).ShouldBe(first.Hash);
    }

    /// <summary>
    /// H1's named requirement: two concurrent writers on ONE stream. Without the link computed inside the
    /// stream's lock/transaction, both writers would read the same predecessor and produce a fork that the
    /// store itself created — and every later verification would report it as a tamper, blaming a writer
    /// that never touched anything.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_writers_produce_a_valid_chain()
    {
        var log = NewLog();
        const int perWriter = 60;

        var first = Task.Run(async () =>
        {
            for (var i = 0; i < perWriter; i++)
                await log.AppendAsync(Evt("shared", "t", $"a{i}"), Ct);
        }, Ct);
        var second = Task.Run(async () =>
        {
            for (var i = 0; i < perWriter; i++)
                await log.AppendAsync(Evt("shared", "t", $"b{i}"), Ct);
        }, Ct);

        await Task.WhenAll(first, second);

        var events = await ReadAsync(log, "shared");
        events.Count.ShouldBe(perWriter * 2);
        events.Select(e => e.StreamSeq).ShouldBe(Enumerable.Range(1, perWriter * 2).Select(i => (long)i));
        EventChain.Verify(events).ShouldBeNull("the store must not fork its own chain under concurrency");
    }

    private static async Task<IReadOnlyList<LedgerEvent>> ReadAsync(InMemoryEventLog log, string stream)
    {
        var list = new List<LedgerEvent>();
        await foreach (var e in log.ReadRangeAsync(stream, 0, long.MaxValue, 0, null, Ct))
            list.Add(e);
        return list;
    }
}
