namespace Burbuja.Core.Abstractions.Events;

[Flags]
public enum RenderLane
{
    None = 0,
    Output = 1,
    Log = 2,
    Monitor = 4,
}

/// <summary>
/// One event in the ledger.
///
/// <para><b>H1:</b> the last three fields are the tamper-evident chain. <paramref name="StreamSeq"/> is
/// the event's dense position WITHIN its stream (the global <paramref name="Seq"/> is the store's
/// insertion counter and is deliberately not hashed — two stores holding the same stream disagree about
/// it, and hashing it would make the chain a property of the store rather than of the events).
/// <paramref name="PreviousHash"/> links to the previous event of the same stream and
/// <paramref name="Hash"/> covers this one's canonical form. All three are defaulted so a store that has
/// not been migrated still constructs, and <see cref="EventChain.Verify"/> treats a missing hash as a
/// break rather than as "nothing to check".</para>
/// </summary>
public sealed record LedgerEvent(
    long Seq,
    string StreamId,
    string? SubStreamId,
    string Type,
    string Summary,
    System.Text.Json.Nodes.JsonNode? Payload,
    RenderLane Lanes,
    string? Subject,
    decimal? Value,
    string? StreamType,
    DateTimeOffset At,
    long StreamSeq = 0,
    string? PreviousHash = null,
    string? Hash = null);

public sealed record EventAppend(
    string StreamId,
    string? SubStreamId,
    string Type,
    string Summary,
    System.Text.Json.Nodes.JsonNode? Payload,
    RenderLane Lanes,
    string? Subject,
    decimal? Value,
    string? StreamType);

public interface IEventLog
{
    ValueTask<long> AppendAsync(EventAppend evt, CancellationToken ct);
    IAsyncEnumerable<LedgerEvent> TailAsync(string streamId, long afterSeq, CancellationToken ct);
    IAsyncEnumerable<LedgerEvent> TailAllAsync(RenderLane lanes, long afterSeq, CancellationToken ct);

    /// <summary>
    /// Bounded read of one stream: exactly the events whose <c>StreamId</c> equals
    /// <paramref name="streamId"/> (ordinal) with <c>fromSeq &lt;= Seq &lt; toSeqExclusive</c>, ascending
    /// <c>Seq</c>, capped at <paramref name="limit"/> items (<c>limit &lt;= 0</c> means no limit).
    /// <paramref name="lanes"/> filters only when it is non-null and not <see cref="RenderLane.None"/>,
    /// as in <see cref="TailAllAsync"/>. Unlike a tail, it never emits the synthetic <c>event.lag</c>
    /// events an in-memory tailer produces and never waits for appends past the head: it completes once
    /// the stored range is exhausted.
    /// </summary>
    IAsyncEnumerable<LedgerEvent> ReadRangeAsync(
        string streamId,
        long fromSeq,
        long toSeqExclusive,
        int limit,
        RenderLane? lanes,
        CancellationToken ct);
}
