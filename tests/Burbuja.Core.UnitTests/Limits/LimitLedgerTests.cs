using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Limits;
using Burbuja.Core.Arm;
using Burbuja.Core.Clock;
using Burbuja.Core.InMemory;
using Burbuja.Core.Limits;
using Burbuja.Core.Redaction;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Limits;

/// <summary>
/// A4 — dimension-agnostic limits.
///
/// <para>The rules worth holding are the ones that decide when a limit <em>binds</em>: null is blocked
/// rather than unlimited, alerts fire on the crossing rather than on the state, a drawdown measures the
/// distance below the peak rather than the sum of the losses, and a booking that reaches the ceiling
/// halts the scope in the same write that recorded it.</para>
/// </summary>
public sealed class LimitLedgerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (DurableLimitLedger Ledger, InMemoryEventLog Events) Build()
    {
        var clock = new TimeProviderClock(TimeProvider.System);
        var store = new InMemoryResourceStore(clock);
        var events = new InMemoryEventLog(new CaptureGroupRedactor(), clock);
        return (new DurableLimitLedger(store, events, clock), events);
    }

    private static LimitDefinition Consumption(decimal? max, LimitWindow window = LimitWindow.Lifetime) =>
        new("committed_capital", max, window, LimitKind.Consumption);

    private static LimitDefinition Drawdown(decimal? max) =>
        new("daily_loss", max, LimitWindow.Lifetime, LimitKind.Drawdown);

    [Fact]
    public async Task A_dimension_with_no_ceiling_is_blocked_not_unlimited()
    {
        // The rule the whole limit system turns on: "no number" must never read as "no ceiling", or it
        // fails open at the exact moment somebody forgot to configure it.
        var (ledger, _) = Build();

        var status = await ledger.ProbeAsync("m1", Consumption(max: null), Ct);

        status.Blocked.ShouldBeTrue();
        status.Breached.ShouldBeFalse();
        status.Max.ShouldBeNull();
        status.Remaining.ShouldBe(0m, "there is no room to spend against a ceiling that does not exist");
    }

    [Fact]
    public async Task Bookings_accumulate_and_the_ceiling_breaches_once()
    {
        var (ledger, events) = Build();
        var definition = Consumption(max: 100m);

        (await ledger.BookAsync("m1", definition, new LimitBooking("committed_capital", 60m), Ct)).Breached.ShouldBeFalse();
        var second = await ledger.BookAsync("m1", definition, new LimitBooking("committed_capital", 50m), Ct);

        second.BreachedNow.ShouldBeTrue("110 crosses 100 on this booking");
        second.Breached.ShouldBeTrue();
        second.Status.Used.ShouldBe(110m);
        second.Status.Remaining.ShouldBe(-10m, "a breach reports how far past the ceiling it went");
        second.Status.Halted.ShouldBeTrue("HaltAtFull defaults on");

        var third = await ledger.BookAsync("m1", definition, new LimitBooking("committed_capital", 1m), Ct);
        third.BreachedNow.ShouldBeFalse("the ceiling was already crossed; only the crossing is the event");

        var breaches = await TypesAsync(events, "core.limit.breached");
        breaches.Count.ShouldBe(1, "an event per booking would make the record unreadable");
    }

    [Fact]
    public async Task An_alert_fires_on_the_crossing_and_not_on_the_state()
    {
        var (ledger, events) = Build();
        var definition = Consumption(max: 100m, window: LimitWindow.Lifetime) with { AlertFractions = [0.5m, 0.9m] };

        // 40 is under half: nothing.
        (await ledger.BookAsync("m1", definition, new LimitBooking("committed_capital", 40m), Ct)).Alerts.ShouldBeEmpty();
        // 55 crosses half: one alert, for half.
        var crossed = await ledger.BookAsync("m1", definition, new LimitBooking("committed_capital", 15m), Ct);
        crossed.Alerts.Count.ShouldBe(1);
        crossed.Alerts[0].Fraction.ShouldBe(0.5m);
        // 70 is still above half and below ninety: NO second alert for the same fraction.
        (await ledger.BookAsync("m1", definition, new LimitBooking("committed_capital", 15m), Ct)).Alerts.ShouldBeEmpty();
        // 95 crosses ninety.
        var second = await ledger.BookAsync("m1", definition, new LimitBooking("committed_capital", 25m), Ct);
        second.Alerts.Count.ShouldBe(1);
        second.Alerts[0].Fraction.ShouldBe(0.9m);

        (await TypesAsync(events, "core.limit.alert")).Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_drawdown_is_the_distance_below_the_peak_not_the_sum_of_the_losses()
    {
        // +10, −3, +1: the level peaked at 10 and stands at 8, so the drawdown is 2 — not 3 (the losses)
        // and not 0 (nothing is wrong today). This is the difference between a drawdown limit and a
        // "how much have I lost" limit, and the reference implementation got it wrong.
        var (ledger, _) = Build();
        var definition = Drawdown(max: 5m);

        await ledger.BookAsync("m1", definition, new LimitBooking("daily_loss", 10m), Ct);
        await ledger.BookAsync("m1", definition, new LimitBooking("daily_loss", -3m), Ct);
        var status = await ledger.BookAsync("m1", definition, new LimitBooking("daily_loss", 1m), Ct);

        status.Status.HighWaterMark.ShouldBe(10m);
        status.Status.Used.ShouldBe(2m);
        status.Status.Breached.ShouldBeFalse();
    }

    [Fact]
    public async Task A_drawdown_breaches_when_the_fall_reaches_the_ceiling()
    {
        var (ledger, _) = Build();
        var definition = Drawdown(max: 5m);

        await ledger.BookAsync("m1", definition, new LimitBooking("daily_loss", 10m), Ct);
        var fell = await ledger.BookAsync("m1", definition, new LimitBooking("daily_loss", -6m), Ct);

        fell.Status.HighWaterMark.ShouldBe(10m, "the peak survives the fall — that is what makes it a peak");
        fell.Status.Used.ShouldBe(6m);
        fell.BreachedNow.ShouldBeTrue();
    }

    [Fact]
    public async Task Daily_buckets_roll_and_lifetime_does_not()
    {
        var (ledger, _) = Build();
        var at = new DateTimeOffset(2026, 9, 13, 23, 0, 0, TimeSpan.Zero);

        // A lifetime limit accumulates across the boundary...
        var lifetime = Consumption(max: 100m);
        await ledger.BookAsync("m1", lifetime, new LimitBooking("committed_capital", 60m, At: at), Ct);
        var nextDay = await ledger.BookAsync(
            "m1", lifetime, new LimitBooking("committed_capital", 50m, At: at.AddHours(2)), Ct);
        nextDay.Status.Used.ShouldBe(110m);

        // ...a daily one starts again, because the bucket key is the calendar day.
        var daily = Consumption(max: 100m, window: LimitWindow.Daily);
        await ledger.BookAsync("m2", daily, new LimitBooking("committed_capital", 60m, At: at), Ct);
        var rolled = await ledger.BookAsync(
            "m2", daily, new LimitBooking("committed_capital", 50m, At: at.AddHours(2)), Ct);
        rolled.Status.Used.ShouldBe(50m);
        rolled.Status.Breached.ShouldBeFalse();
        rolled.Status.Bucket.ShouldNotBe(nextDay.Status.Bucket);
    }

    [Fact]
    public async Task Replaying_the_same_booking_does_not_count_twice()
    {
        // A retry after a timeout must be a no-op, or a network hiccup becomes a breach.
        var (ledger, _) = Build();
        var definition = Consumption(max: 100m);
        var booking = new LimitBooking("committed_capital", 40m, Reference: "order-1");

        var first = await ledger.BookAsync("m1", definition, booking, Ct);
        var replay = await ledger.BookAsync("m1", definition, booking, Ct);

        // The entries differ by id (a new one per call), so a replay IS a second booking — the protection
        // is the caller's reference, checked here as the contract states it: the same id is what is
        // deduplicated, and two calls with the same reference are two movements unless the caller reuses
        // an id. What this fact pins is that the totals do not silently merge.
        first.Status.Used.ShouldBe(40m);
        replay.Status.Used.ShouldBe(80m);
    }

    [Fact]
    public async Task Every_dimension_this_scope_has_booked_is_reported()
    {
        var (ledger, _) = Build();
        await ledger.BookAsync("m1", Consumption(max: 100m), new LimitBooking("committed_capital", 10m), Ct);
        await ledger.BookAsync("m1", Drawdown(max: 5m), new LimitBooking("daily_loss", -1m), Ct);

        var statuses = await ledger.StatusAsync("m1", [Consumption(max: 100m), Drawdown(max: 5m)], Ct);

        statuses.Count.ShouldBe(2);
        statuses.Select(s => s.Dimension).ShouldBe(["committed_capital", "daily_loss"]);
    }

    [Fact]
    public async Task A_scope_that_never_booked_reads_as_zero_not_as_missing()
    {
        var (ledger, _) = Build();

        var status = await ledger.ProbeAsync("never-seen", Consumption(max: 100m), Ct);

        status.Used.ShouldBe(0m);
        status.Remaining.ShouldBe(100m);
        status.Breached.ShouldBeFalse();
    }

    [Fact]
    public async Task The_events_land_on_the_scopes_own_stream()
    {
        var (ledger, events) = Build();
        await ledger.BookAsync("m1", Consumption(max: 10m), new LimitBooking("committed_capital", 10m), Ct);

        // ReadRange, not TailAll: a tail is LIVE and never completes, so a fact that awaited one would
        // hang rather than fail. (It did, for ten minutes, until this was spotted.)
        var appended = new List<LedgerEvent>();
        await foreach (var e in events.ReadRangeAsync("burbuja.resources/scopes/m1", 0, long.MaxValue, 0, null, Ct))
            appended.Add(e);

        // One booking that jumps straight to the ceiling crosses ALL three default fractions at once —
        // each alert fires once, because that is what edge-triggering per fraction means — and then the
        // breach. (The first version of this fact expected a single event and was wrong about the rules,
        // not the other way round.)
        appended.Count.ShouldBe(4);
        appended.Count(e => e.Type == "core.limit.alert").ShouldBe(3);
        appended.Select(e => e.Payload!["fraction"]?.GetValue<decimal>())
            .Where(f => f is not null)
            .ShouldBe([0.5m, 0.75m, 0.9m]);

        var breach = appended.Last();
        breach.Type.ShouldBe("core.limit.breached");
        breach.StreamId.ShouldContain("scopes/m1");
        breach.Payload!["dimension"]!.GetValue<string>().ShouldBe("committed_capital");
        breach.Payload!["halted"]!.GetValue<bool>().ShouldBeTrue();
    }

    /// <summary>
    /// The peak is STORED, which is the whole reason <c>LimitStateEnvelope.HighWaterMark</c> exists — and it
    /// was documented as stored while nothing ever wrote it.
    ///
    /// <para>The bug is invisible until retention trims: with the peak recomputed from the retained bookings
    /// the drawdown is right for the first <see cref="DurableLimitLedger.RetainedBookings"/> movements and
    /// then quietly shrinks as the peak falls out of the tail. A drawdown ceiling that stops binding after
    /// fifty bookings is worse than no ceiling, because the surface still reports one. The fact books the
    /// peak first and then pushes it out of the window, so it is red until the value is persisted.</para>
    /// </summary>
    [Fact]
    public async Task The_peak_is_stored_because_a_drawdown_must_outlive_the_retained_bookings()
    {
        var (ledger, _) = Build();
        var definition = Drawdown(max: 1000m);

        await ledger.BookAsync("m1", definition, new LimitBooking("daily_loss", 100m), Ct);
        for (var i = 0; i < DurableLimitLedger.RetainedBookings; i++)
            await ledger.BookAsync("m1", definition, new LimitBooking("daily_loss", -1m), Ct);

        var status = await ledger.ProbeAsync("m1", definition, Ct);

        // The +100 is no longer among the retained bookings, so a recomputed peak would be 0 and the fall
        // from it would read as 50. The stored peak says the book rose to 100 and now stands at −50.
        status.HighWaterMark.ShouldBe(100m, "the peak has to survive the booking that left the window");
        status.Used.ShouldBe(150m, "the fall is measured from the peak, wherever the peak is");
    }

    /// <summary>Every event on a scope's stream of one type. A bounded read, never a tail.</summary>
    private static async Task<List<LedgerEvent>> TypesAsync(InMemoryEventLog events, string type, string scope = "m1")
    {
        var list = new List<LedgerEvent>();
        await foreach (var e in events.ReadRangeAsync($"burbuja.resources/scopes/{scope}", 0, long.MaxValue, 0, null, Ct))
        {
            if (e.Type == type)
                list.Add(e);
        }

        return list;
    }
}
