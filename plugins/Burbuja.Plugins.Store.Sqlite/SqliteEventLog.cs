using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Artifacts;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Redaction;
using Burbuja.Core.Events;
using Microsoft.Data.Sqlite;

namespace Burbuja.Plugins.Store.Sqlite;

/// <summary>Durable append-only event log; tails poll the table so other processes see appends.</summary>
public sealed class SqliteEventLog(
    SqliteDatabase database,
    IRedactor redactor,
    IClock clock,
    IArtifactStore? spill = null,
    int maxPayloadBytes = EventPayloadCap.DefaultMaxBytes) : IEventLog
{
    private const string Columns = "seq, stream_id, sub_stream_id, type, summary, payload, lanes, subject, value, stream_type, at, stream_seq, previous_hash, hash";

    public async ValueTask<long> AppendAsync(EventAppend evt, CancellationToken ct)
    {
        var summary = redactor.Redact(evt.Summary ?? "");
        if (summary.Length > 512)
            summary = summary[..512];
        var redacted = evt.Payload is null ? null : RedactJson(evt.Payload.DeepClone());
        var payload = redacted?.ToJsonString();
        var willSpill = redacted is not null
                        && spill is not null
                        && System.Text.Encoding.UTF8.GetByteCount(payload!) > maxPayloadBytes;

        var stored = await database.WriteAsync(async (connection, transaction) =>
        {
            // H1: the predecessor is read INSIDE the transaction, which is what makes the sequence dense
            // and the chain single when two writers append to one stream at once. Reading it outside would
            // let both take the same link and write two events claiming the same position — a fork the
            // store itself produced, which every later verification would report as a tamper.
            long streamSeq;
            string previousHash;
            await using (var head = connection.CreateCommand())
            {
                head.Transaction = transaction;
                head.CommandText = "SELECT stream_seq, hash FROM events WHERE stream_id = $stream ORDER BY seq DESC LIMIT 1";
                head.Parameters.AddWithValue("$stream", evt.StreamId);
                await using var reader = await head.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    streamSeq = reader.GetInt64(0) + 1;
                    previousHash = reader.IsDBNull(1) ? Burbuja.Core.Abstractions.Events.EventChain.Genesis : reader.GetString(1);
                }
                else
                {
                    streamSeq = 1;
                    previousHash = Burbuja.Core.Abstractions.Events.EventChain.Genesis;
                }
            }

            // The spill happens BEFORE the insert and is keyed on (streamId, streamSeq) rather than on the
            // rowid: the payload a spill produces is the payload that gets hashed, so the substitution has
            // to be finished before the hash is computed. A spilled row whose published hash covered the
            // ORIGINAL bytes would fail its own verification the moment anything re-read it.
            var finalPayload = payload;
            if (willSpill)
            {
                finalPayload = (await EventPayloadCap.TrySpillAsync(
                    redacted, evt.StreamId, streamSeq, maxPayloadBytes, spill!, ct).ConfigureAwait(false))?.ToJsonString();
            }

            var at = clock.UtcNow;
            var chained = new LedgerEvent(
                Seq: 0,
                evt.StreamId,
                evt.SubStreamId,
                evt.Type,
                summary,
                finalPayload is null ? null : JsonNode.Parse(finalPayload),
                evt.Lanes,
                evt.Subject,
                evt.Value,
                evt.StreamType,
                at,
                streamSeq,
                previousHash,
                null);
            var hash = Burbuja.Core.Abstractions.Events.EventChain.HashOf(chained);

            long seq;
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO events(stream_id, sub_stream_id, type, summary, payload, lanes, subject, value, stream_type, at, stream_seq, previous_hash, hash)
                    VALUES ($stream, $sub, $type, $summary, $payload, $lanes, $subject, $value, $streamType, $at, $sseq, $prev, $hash)
                    """;
                insert.Parameters.AddWithValue("$stream", evt.StreamId);
                insert.Parameters.AddWithValue("$sub", (object?)evt.SubStreamId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$type", evt.Type);
                insert.Parameters.AddWithValue("$summary", summary);
                insert.Parameters.AddWithValue("$payload", (object?)finalPayload ?? DBNull.Value);
                insert.Parameters.AddWithValue("$lanes", (int)evt.Lanes);
                insert.Parameters.AddWithValue("$subject", (object?)evt.Subject ?? DBNull.Value);
                insert.Parameters.AddWithValue(
                    "$value",
                    evt.Value is null
                        ? DBNull.Value
                        : (object)evt.Value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("$streamType", (object?)evt.StreamType ?? DBNull.Value);
                insert.Parameters.AddWithValue("$at", at.ToString("O"));
                insert.Parameters.AddWithValue("$sseq", streamSeq);
                insert.Parameters.AddWithValue("$prev", previousHash);
                insert.Parameters.AddWithValue("$hash", hash);
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using var last = connection.CreateCommand();
            last.Transaction = transaction;
            last.CommandText = "SELECT last_insert_rowid()";
            seq = (long)(await last.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
            return (Seq: seq, StreamSeq: streamSeq, PreviousHash: previousHash, Hash: hash, At: at, Payload: finalPayload);
        }, ct).ConfigureAwait(false);

        LastAppended = (stored.Seq, stored.StreamSeq, stored.PreviousHash, stored.Hash, stored.At, stored.Payload);
        return stored.Seq;
    }

    /// <summary>
    /// The last event this store appended, with its chain fields. Read by the durability facts, which need
    /// to assert the chain landed rather than the sequence number alone.
    /// </summary>
    public (long Seq, long StreamSeq, string PreviousHash, string Hash, DateTimeOffset At, string? Payload)? LastAppended { get; private set; }

    public IAsyncEnumerable<LedgerEvent> TailAsync(string streamId, long afterSeq, CancellationToken ct) =>
        TailAsync(streamId, null, afterSeq, ct);

    public IAsyncEnumerable<LedgerEvent> TailAllAsync(RenderLane lanes, long afterSeq, CancellationToken ct) =>
        TailAsync(null, lanes, afterSeq, ct);

    public async IAsyncEnumerable<LedgerEvent> ReadRangeAsync(
        string streamId,
        long fromSeq,
        long toSeqExclusive,
        int limit,
        RenderLane? lanes,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var rows = await database.ReadAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            // The lane mask belongs in the predicate, not after the LIMIT: filtering in memory would
            // silently return short pages and skip matching events past the window. The in-memory
            // store filters before it limits, and the two must mean the same thing.
            var laneFilter = lanes is null || lanes == RenderLane.None ? "" : " AND (lanes & $lanes) <> 0";
            command.CommandText =
                $"SELECT {Columns} FROM events WHERE stream_id = $stream AND seq >= $from AND seq < $to{laneFilter} ORDER BY seq LIMIT $limit";
            command.Parameters.AddWithValue("$stream", streamId);
            command.Parameters.AddWithValue("$from", fromSeq);
            command.Parameters.AddWithValue("$to", toSeqExclusive);
            command.Parameters.AddWithValue("$limit", limit > 0 ? limit : -1);
            if (laneFilter.Length > 0)
                command.Parameters.AddWithValue("$lanes", (int)lanes!.Value);
            var list = new List<LedgerEvent>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                list.Add(Map(reader));
            return list;
        }, ct).ConfigureAwait(false);

        foreach (var e in rows)
            yield return e;
    }

    private async IAsyncEnumerable<LedgerEvent> TailAsync(
        string? streamId,
        RenderLane? lanes,
        long afterSeq,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var last = afterSeq;
        while (!ct.IsCancellationRequested)
        {
            var rows = await database.ReadAsync(async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = streamId is null
                    ? $"SELECT {Columns} FROM events WHERE seq > $after ORDER BY seq LIMIT 500"
                    : $"SELECT {Columns} FROM events WHERE seq > $after AND stream_id = $stream ORDER BY seq LIMIT 500";
                command.Parameters.AddWithValue("$after", last);
                if (streamId is not null)
                    command.Parameters.AddWithValue("$stream", streamId);
                var list = new List<LedgerEvent>();
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    list.Add(Map(reader));
                return list;
            }, ct).ConfigureAwait(false);

            foreach (var e in rows)
            {
                last = Math.Max(last, e.Seq);
                if (lanes is null || lanes == RenderLane.None || (e.Lanes & lanes.Value) != 0)
                    yield return e;
            }

            if (rows.Count == 0)
            {
                try
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
            }
        }
    }

    private static LedgerEvent Map(SqliteDataReader reader) =>
        DatabaseMap(reader) with
        {
            StreamSeq = reader.IsDBNull(11) ? 0 : reader.GetInt64(11),
            PreviousHash = reader.IsDBNull(12) ? null : reader.GetString(12),
            Hash = reader.IsDBNull(13) ? null : reader.GetString(13),
        };

    /// <summary>The first eleven columns, which the migration reads with the same meaning.</summary>
    private static LedgerEvent DatabaseMap(SqliteDataReader reader) => SqliteDatabase.MapRow(reader);

    private JsonNode RedactJson(JsonNode node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => JsonValue.Create(redactor.Redact(s))!,
        JsonObject o => RedactObject(o),
        JsonArray a => RedactArray(a),
        _ => node.DeepClone(),
    };

    private JsonObject RedactObject(JsonObject source)
    {
        var copy = new JsonObject();
        foreach (var (key, value) in source)
            copy[key] = value is null ? null : RedactJson(value);
        return copy;
    }

    private JsonArray RedactArray(JsonArray source)
    {
        var copy = new JsonArray();
        foreach (var item in source)
            copy.Add(item is null ? null : RedactJson(item));
        return copy;
    }
}
