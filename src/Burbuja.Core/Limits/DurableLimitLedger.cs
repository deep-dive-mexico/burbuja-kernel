using System.Globalization;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Limits;
using Burbuja.Core.Arm;
using Burbuja.Core.Store;

namespace Burbuja.Core.Limits;

/// <summary>
/// A4: <see cref="ILimitLedger"/> over <see cref="IResourceStore"/>, one resource per
/// <c>(scope, dimension)</c>.
///
/// <para><b>One resource per dimension, holding every window bucket.</b> A resource per
/// <c>(dimension, bucket)</c> would make the daily rollover free and the "where does this dimension
/// stand" question a scan — and the question a limit exists to answer is asked on every booking, while
/// the rollover happens once. The buckets are keyed by the period the definition names, so a definition
/// whose window changes starts a new bucket rather than reinterpreting the old one.</para>
///
/// <para><b>The events are appended after the write, never before.</b> An event that announced a breach
/// the store then failed to record would be a lie in the permanent record, and the record is what other
/// systems read. A crash between the two loses an announcement, which the next booking or a probe
/// re-derives; it never invents one.</para>
/// </summary>
public sealed class DurableLimitLedger(
    IResourceStore store,
    IEventLog events,
    IClock clock) : ILimitLedger
{
    public const string Namespace = "Burbuja.Resources";

    public const string Type = Namespace + "/limits";

    /// <summary>How many recent bookings a dimension keeps. Bounded: the log is the record, this is a tail.</summary>
    public const int RetainedBookings = 50;

    public static string Key(string scope, string dimension) =>
        $"{Namespace}/limits/{Sanitize(scope)}/{Sanitize(dimension)}";

    /// <summary>
    /// Where a booking lands. Lifetime never rolls; the others use the period's calendar key, so two
    /// bookings a month apart are two buckets and the same booking replayed inside one minute is one.
    /// </summary>
    internal static string Bucket(LimitDefinition definition, DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        return definition.Window switch
        {
            LimitWindow.Daily => "d:" + utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            LimitWindow.Weekly => "w:" + ISOWeek(utc),
            LimitWindow.Monthly => "m:" + utc.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            // A rolling window is bucketed by the SPAN rather than by the clock: the trailing window is
            // derived from the retained bookings below, and this key only keeps the totals from mixing
            // with a different span.
            LimitWindow.Rolling => $"r:{(int)(definition.RollingSpan ?? TimeSpan.FromHours(24)).TotalSeconds}",
            _ => "life",
        };
    }

    private static string ISOWeek(DateTimeOffset utc)
    {
        var week = System.Globalization.ISOWeek.GetWeekOfYear(utc.UtcDateTime);
        return $"{System.Globalization.ISOWeek.GetYear(utc.UtcDateTime):0000}-{week:00}";
    }

    public async ValueTask<LimitOutcome> BookAsync(
        string scope,
        LimitDefinition definition,
        LimitBooking booking,
        CancellationToken ct)
    {
        var at = booking.At ?? clock.UtcNow;
        var bucket = Bucket(definition, at);
        var key = Key(scope, definition.Dimension);
        var entry = new LimitEntry(Guid.NewGuid(), booking.Amount, bucket, at, booking.Reference, booking.Note);
        var alerts = new List<LimitAlert>();
        var breachedNow = false;

        var envelope = await EnvelopeAppender.AppendAsync(
            store,
            key,
            Type,
            existing => EnvelopeAppender.Read(existing, () => new LimitStateEnvelope()),
            (state, _) =>
            {
                var before = LimitMath.Evaluate(state, definition, bucket, at);
                state.Bookings.Add(entry);
                if (state.Bookings.Count > RetainedBookings)
                    state.Bookings.RemoveRange(0, state.Bookings.Count - RetainedBookings);

                var after = LimitMath.Evaluate(state, definition, bucket, at);
                breachedNow = after.Breached && !before.Breached;

                // Persist the peak. `LimitStateEnvelope.HighWaterMark` has always been DOCUMENTED as stored
                // rather than derived — "recomputing it from the retained bookings would forget every peak
                // older than the retention window" — and nothing wrote it, so it was always zero and the
                // drawdown was in fact recomputed from the retained bookings: exactly the failure the
                // comment says the field exists to prevent. The peak is monotone, so taking the max of the
                // stored value and the recomputed one cannot walk it backwards.
                state.HighWaterMark = Math.Max(state.HighWaterMark, after.HighWaterMark);

                // Edge-triggered: each fraction fires once per bucket, as it is crossed. A warning that
                // repeats on every booking is one a reader learns to scroll past.
                foreach (var fraction in definition.Fractions)
                {
                    if (after.Fraction >= fraction && !state.Raised.Contains(RaisedKey(bucket, fraction)))
                    {
                        state.Raised.Add(RaisedKey(bucket, fraction));
                        alerts.Add(new LimitAlert(definition.Dimension, fraction, after.Used, after.Max ?? 0m, bucket));
                    }
                }

                return state.ToJson();
            },
            // The entry id is what makes a blind create verifiable: re-running the same booking must not
            // count twice.
            idempotent: state => state.Bookings.Any(b => b.Id == entry.Id),
            ct).ConfigureAwait(false);

        var final = EnvelopeAppender.Read(envelope, () => new LimitStateEnvelope());
        var status = LimitMath.Evaluate(final, definition, bucket, at).ToStatus(final, definition, bucket);

        foreach (var alert in alerts)
            await AnnounceAsync(scope, "core.limit.alert", alert, status, ct).ConfigureAwait(false);
        if (breachedNow)
            await AnnounceAsync(scope, "core.limit.breached", null, status, ct).ConfigureAwait(false);

        return new LimitOutcome(status, alerts) { BreachedNow = breachedNow };
    }

    public async ValueTask<LimitStatus> ProbeAsync(string scope, LimitDefinition definition, CancellationToken ct)
    {
        var at = clock.UtcNow;
        var bucket = Bucket(definition, at);
        var envelope = await store.GetAsync(Key(scope, definition.Dimension), ct).ConfigureAwait(false);
        var state = EnvelopeAppender.Read(envelope, () => new LimitStateEnvelope());
        return LimitMath.Evaluate(state, definition, bucket, at).ToStatus(state, definition, bucket);
    }

    public async ValueTask<IReadOnlyList<LimitStatus>> StatusAsync(
        string scope,
        IReadOnlyList<LimitDefinition> definitions,
        CancellationToken ct)
    {
        var statuses = new List<LimitStatus>(definitions.Count);
        foreach (var definition in definitions)
            statuses.Add(await ProbeAsync(scope, definition, ct).ConfigureAwait(false));
        return statuses;
    }

    /// <summary>
    /// Announce a crossing on the scope's own stream, so the event sits beside the work that caused it
    /// rather than in a limits-only lane nobody reads.
    /// </summary>
    private async ValueTask AnnounceAsync(string scope, string type, LimitAlert? alert, LimitStatus status, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["dimension"] = status.Dimension,
            ["used"] = status.Used,
            ["max"] = status.Max,
            ["remaining"] = status.Remaining,
            ["bucket"] = status.Bucket,
            ["blocked"] = status.Blocked,
            ["halted"] = status.Halted,
        };
        if (alert is not null)
        {
            payload["fraction"] = alert.Fraction;
            payload["level"] = alert.Fraction.ToString("P0", CultureInfo.InvariantCulture);
        }

        await events.AppendAsync(
            new EventAppend(
                EventStreamIds.Resource($"{Namespace}/scopes/{Sanitize(scope)}"),
                null,
                type,
                status.Breached ? "breached" : "alert",
                payload,
                RenderLane.Monitor | RenderLane.Log,
                status.Dimension,
                status.Used,
                "core.limit"),
            ct).ConfigureAwait(false);
    }

    private static string RaisedKey(string bucket, decimal fraction) =>
        bucket + "@" + fraction.ToString(CultureInfo.InvariantCulture);

    private static string Sanitize(string value)
    {
        var chars = value.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray();
        var s = new string(chars).Trim('_');
        return s.Length == 0 ? "default" : s;
    }
}

/// <summary>One booked movement, kept for the audit tail and for the rolling window's arithmetic.</summary>
public sealed record LimitEntry(
    Guid Id,
    decimal Amount,
    string Bucket,
    DateTimeOffset At,
    string? Reference,
    string? Note);

/// <summary>
/// A dimension's durable state: the bookings, which alert fractions have already fired, and the peak the
/// drawdown is measured from.
/// </summary>
public sealed class LimitStateEnvelope
{
    public List<LimitEntry> Bookings { get; set; } = [];

    /// <summary>Which <c>bucket@fraction</c> alerts have fired. Without this every booking re-alerts.</summary>
    public List<string> Raised { get; set; } = [];

    /// <summary>
    /// The highest level the running total has ever reached, for <see cref="LimitKind.Drawdown"/>. It is
    /// stored rather than derived because a drawdown is a fact about history: recomputing it from the
    /// retained bookings would forget every peak older than the retention window and the limit would
    /// quietly stop binding.
    /// </summary>
    public decimal HighWaterMark { get; set; }

    public JsonObject ToJson() => new()
    {
        ["bookings"] = new JsonArray(Bookings.Select(b => (JsonNode)new JsonObject
        {
            ["id"] = b.Id.ToString("D"),
            ["amount"] = b.Amount,
            ["bucket"] = b.Bucket,
            ["at"] = b.At.ToString("O"),
            ["reference"] = b.Reference,
            ["note"] = b.Note,
        }).ToArray()),
        ["raised"] = new JsonArray(Raised.Select(r => (JsonNode)r!).ToArray()),
        ["highWaterMark"] = HighWaterMark,
    };

}

/// <summary>Where a dimension stands, as arithmetic — pure, so the rules are testable without a store.</summary>
public sealed record LimitReading(decimal Used, decimal? Max, decimal HighWaterMark, bool Breached, bool Blocked, decimal Fraction)
{
    public decimal Remaining => Max is { } max ? max - Used : 0m;

    public LimitStatus ToStatus(LimitStateEnvelope state, LimitDefinition definition, string bucket) =>
        new(
            definition.Dimension,
            Max,
            Used,
            Remaining,
            HighWaterMark,
            bucket,
            Breached,
            Blocked,
            Breached && definition.HaltAtFull,
            [.. definition.Fractions.Where(f => state.Raised.Contains(bucket + "@" + f.ToString(CultureInfo.InvariantCulture)))]);
}

/// <summary>The limit arithmetic, with no store and no clock in it.</summary>
public static class LimitMath
{
    public static LimitReading Evaluate(LimitStateEnvelope state, LimitDefinition definition, string bucket, DateTimeOffset now)
    {
        var inBucket = state.Bookings.Where(b => b.Bucket == bucket).ToList();
        var running = inBucket.Sum(b => b.Amount);

        decimal used;
        var peak = state.HighWaterMark;
        if (definition.Kind == LimitKind.Drawdown)
        {
            // The level is the window's running total, and the drawdown is the distance below the highest
            // the level has ever been. Folding in time order is what makes that right: a sequence of
            // +10, −3, +1 peaked at 10 and stands at 8, so it is 2 below the peak — not 3 (the losses) and
            // not 0 (nothing is wrong today). The stored high-water mark is the floor, because it carries
            // peaks older than the retained bookings.
            var level = 0m;
            foreach (var b in inBucket.OrderBy(b => b.At))
            {
                level += b.Amount;
                peak = Math.Max(peak, level);
            }

            used = Math.Max(0m, peak - level);
        }
        else
        {
            used = running;
            peak = Math.Max(peak, running);
        }

        // Null is BLOCKED, not unlimited. A dimension nobody bounded is one the host has no authority to
        // spend against, and "no number" must never read as "no ceiling".
        var blocked = definition.Max is null;
        var breached = definition.Max is { } max && used >= max;
        var fraction = definition.Max is { } m && m > 0m ? used / m : 0m;
        return new LimitReading(used, definition.Max, peak, breached, blocked, fraction);
    }
}
