using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Burbuja.Core.Abstractions.Events;

/// <summary>
/// H1: the tamper-evident chain over the event log.
///
/// <para><b>What it is for.</b> The ledger is the record of what the host decided and why — the gate's
/// verdicts, the approvals, the bookings, the tool calls. Every other guarantee in this kernel rests on
/// that record being what it was, and until this existed the only thing standing behind it was the file's
/// own permissions: anything that could write to the database could rewrite history, and nothing would
/// notice. A hash chain makes a rewrite detectable without trusting the writer, which is the difference
/// between an audit trail and a log.</para>
///
/// <para><b>Per stream, not global.</b> Each event carries its stream's dense sequence number and the
/// hash of the previous event <em>in that stream</em>. A global chain would serialise every unrelated
/// job against one another and would make one stream's verification depend on all of them; per-stream
/// means a verifier can walk a single job's history and say exactly where it breaks.</para>
///
/// <para><b>The global <c>Seq</c> is deliberately excluded from the hash.</b> It is the store's insertion
/// counter and two stores that hold the same stream disagree about it — the SQLite lane's rows carry the
/// numbers the container assigned, the in-memory lane's carry its own. Hashing it would make the two
/// stores produce different hashes for identical content and the chain would be a property of the STORE
/// rather than of the events. Excluding it is what makes "the same events hash the same" true, and that is
/// tested.</para>
/// </summary>
public static class EventChain
{
    /// <summary>The hash of an event with no predecessor in its stream.</summary>
    public const string Genesis = "genesis";

    /// <summary>
    /// The canonical bytes of one event: sorted keys, no whitespace, the stream's own sequence number in
    /// place of the global one, and the payload in its OWN canonical form.
    ///
    /// <para>Canonicalisation is what makes the hash stable across the two stores, a JSON round trip, and
    /// a property-order change in a payload somebody edited by hand. It is also the reason this is a
    /// function rather than a <c>ToString()</c>: two serialisers that disagree about property order would
    /// produce two chains over the same events.</para>
    /// </summary>
    public static string Canonicalize(LedgerEvent evt)
    {
        var canonical = new StringBuilder();
        Field(canonical, "streamId", evt.StreamId, quoted: true);
        Field(canonical, "subStreamId", evt.SubStreamId, quoted: true);
        Field(canonical, "streamSeq", evt.StreamSeq, quoted: false);
        Field(canonical, "type", evt.Type, quoted: true);
        Field(canonical, "summary", evt.Summary, quoted: true);
        Field(canonical, "lanes", (int)evt.Lanes, quoted: false);
        Field(canonical, "subject", evt.Subject, quoted: true);
        Field(canonical, "value", evt.Value, quoted: false);
        Field(canonical, "streamType", evt.StreamType, quoted: true);
        Field(canonical, "at", evt.At.ToUniversalTime().ToString("O"), quoted: true);
        Field(canonical, "previousHash", evt.PreviousHash, quoted: true);
        Field(canonical, "payload", CanonicalJson(evt.Payload), quoted: false);
        return canonical.ToString();
    }

    /// <summary>The chain hash: SHA-256 over the canonical form, lowercase hex.</summary>
    public static string ComputeHash(LedgerEvent evt)
    {
        var bytes = Encoding.UTF8.GetBytes(Canonicalize(evt));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>The hash an event carries: its own content plus the link to its predecessor.</summary>
    public static string HashOf(LedgerEvent evt) => ComputeHash(evt with { Hash = null });

    /// <summary>One link that failed, or null when the whole run verified.</summary>
    public sealed record Break(long? GlobalSeq, long StreamSeq, string StreamId, string Reason);

    /// <summary>
    /// Walk a stream and return the FIRST break, or null. "First" is the point: a verifier that reported
    /// every subsequent mismatch would describe the consequences of a tamper as if they were separate
    /// facts, and the one that matters is where the chain first disagreed with itself.
    /// </summary>
    public static Break? Verify(IEnumerable<LedgerEvent> stream)
    {
        string? previous = null;
        long expectedSeq = 0;
        foreach (var evt in stream)
        {
            expectedSeq++;
            if (evt.StreamSeq != expectedSeq)
            {
                return new Break(
                    evt.Seq,
                    evt.StreamSeq,
                    evt.StreamId,
                    $"stream sequence is {evt.StreamSeq}, expected {expectedSeq} — an event is missing or was inserted");
            }

            var wanted = previous ?? Genesis;
            var carries = evt.PreviousHash ?? Genesis;
            if (!string.Equals(carries, wanted, StringComparison.Ordinal))
            {
                return new Break(
                    evt.Seq,
                    evt.StreamSeq,
                    evt.StreamId,
                    $"previous hash is '{Short(carries)}', expected '{Short(wanted)}' — the chain was cut or reordered");
            }

            if (evt.Hash is not { Length: > 0 })
                return new Break(evt.Seq, evt.StreamSeq, evt.StreamId, "the event carries no hash");

            if (!string.Equals(evt.Hash, HashOf(evt), StringComparison.Ordinal))
            {
                return new Break(
                    evt.Seq,
                    evt.StreamSeq,
                    evt.StreamId,
                    $"the event's content does not match its hash ({Short(evt.Hash)}) — the record was edited after it was written");
            }

            previous = evt.Hash;
        }

        return null;
    }

    /// <summary>The next link for a stream whose last event is <paramref name="last"/> (null for the first).</summary>
    public static (long StreamSeq, string PreviousHash) Next(LedgerEvent? last) =>
        last is null ? (1, Genesis) : (last.StreamSeq + 1, last.Hash ?? Genesis);

    private static void Field(StringBuilder sb, string name, object? value, bool quoted)
    {
        sb.Append(name).Append('=');
        switch (value)
        {
            case null:
                sb.Append('~');
                break;
            case string s when quoted:
                sb.Append(JsonSerializer.Serialize(s));
                break;
            case decimal d:
                // Invariant and round-trippable: a culture-sensitive decimal would hash differently on a
                // machine with a comma separator and the chain would break across hosts.
                sb.Append(d.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            default:
                sb.Append(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                break;
        }

        sb.Append('\u001f');
    }

    /// <summary>
    /// A payload in canonical JSON: object keys sorted ordinal, arrays in order, numbers invariant. Null
    /// and the empty object are different inputs and hash differently.
    /// </summary>
    internal static string CanonicalJson(JsonNode? node)
    {
        if (node is null)
            return "~";
        var sb = new StringBuilder();
        Write(sb, node);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
                sb.Append('{');
                var first = true;
                foreach (var (key, value) in obj.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    if (!first)
                        sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(key)).Append(':');
                    Write(sb, value);
                }

                sb.Append('}');
                break;
            case JsonArray array:
                sb.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                        sb.Append(',');
                    Write(sb, array[i]);
                }

                sb.Append(']');
                break;
            default:
                sb.Append(node.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
                break;
        }
    }

    private static string Short(string hash) => hash.Length <= 12 ? hash : hash[..12] + "…";
}
