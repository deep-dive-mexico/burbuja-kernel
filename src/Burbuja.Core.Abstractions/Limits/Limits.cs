namespace Burbuja.Core.Abstractions.Limits;

/// <summary>Which period a limit is measured over.</summary>
public enum LimitWindow
{
    /// <summary>A trailing span — callers pass the span; this is the one window whose length is not fixed.</summary>
    Rolling,

    Daily,
    Weekly,
    Monthly,

    /// <summary>Since the limit existed. The bucket never rolls.</summary>
    Lifetime,
}

/// <summary>What the number counts.</summary>
public enum LimitKind
{
    /// <summary>
    /// Everything booked in the window, added up. The model for "no more than N of this, ever, this
    /// month": a spend, a call count, a byte total.
    /// </summary>
    Consumption,

    /// <summary>
    /// The distance below the highest level the window has ever reached. The model for a loss limit: a
    /// drawdown of 5 % is a fall of five points from the peak, however high the peak was — which is a
    /// different question from "how much has been lost in total", and the one a risk limit actually
    /// means.
    /// </summary>
    Drawdown,
}

/// <summary>
/// One dimension of a budget.
///
/// <para><b><see cref="Max"/> is nullable, and null does not mean unlimited.</b> An undefined limit is
/// <see cref="LimitStatus.Blocked"/> — a dimension nobody has bounded is one the host has no authority to
/// spend against, and treating "no number" as "no ceiling" is how a limit system fails open at exactly
/// the moment somebody forgot to configure it. A caller that genuinely wants no limit must say so with a
/// number it chose.</para>
///
/// <para><b><see cref="AlertFractions"/> is edge-triggered, not level-triggered.</b> Each fraction fires
/// once per bucket as it is crossed, because a warning that repeats on every booking is one a reader
/// learns to scroll past — the same rule the host-facts notifier already follows.</para>
/// </summary>
/// <param name="Dimension">The name of the thing being limited, e.g. <c>committed_capital</c>.</param>
/// <param name="Max">The ceiling. Null blocks; it does not permit.</param>
/// <param name="AlertFractions">Fractions of <paramref name="Max"/> at which to raise an alert, ascending.</param>
/// <param name="HaltAtFull">Whether reaching <paramref name="Max"/> halts the scope or only breaches it.</param>
public sealed record LimitDefinition(
    string Dimension,
    decimal? Max,
    LimitWindow Window = LimitWindow.Lifetime,
    LimitKind Kind = LimitKind.Consumption,
    IReadOnlyList<decimal>? AlertFractions = null,
    bool HaltAtFull = true,
    /// <summary>Only meaningful for <see cref="LimitWindow.Rolling"/>; defaults to 24 hours.</summary>
    TimeSpan? RollingSpan = null)
{
    /// <summary>The fractions this definition alerts at, defaulting to half, three quarters and ninety percent.</summary>
    public IReadOnlyList<decimal> Fractions =>
        AlertFractions is { Count: > 0 } given ? given : [0.5m, 0.75m, 0.9m];
}

/// <summary>One movement against a dimension. Negative amounts are refunds or losses, by kind.</summary>
public sealed record LimitBooking(
    string Dimension,
    decimal Amount,
    /// <summary>What caused it, for the audit trail — a job id, an order id, a ticket.</summary>
    string? Reference = null,
    string? Note = null,
    DateTimeOffset? At = null);

/// <summary>One crossing of an alert fraction, raised once per bucket.</summary>
public sealed record LimitAlert(
    string Dimension,
    decimal Fraction,
    decimal Used,
    decimal Max,
    string Bucket);

/// <summary>Where a dimension stands, without booking anything.</summary>
/// <param name="Blocked">
/// True when the dimension has no ceiling. It is reported as its own fact rather than folded into
/// "breached", because the two need different responses: a breach is a limit doing its job, and a block
/// is a limit that was never configured.
/// </param>
public sealed record LimitStatus(
    string Dimension,
    decimal? Max,
    decimal Used,
    decimal Remaining,
    decimal HighWaterMark,
    string Bucket,
    bool Breached,
    bool Blocked,
    bool Halted,
    IReadOnlyList<decimal> Raised);

/// <summary>The result of a booking: the new status plus what the booking caused.</summary>
public sealed record LimitOutcome(LimitStatus Status, IReadOnlyList<LimitAlert> Alerts)
{
    public bool Breached => Status.Breached;

    /// <summary>True when this booking is the one that reached the ceiling.</summary>
    public bool BreachedNow { get; init; }

    /// <summary>True when this booking is the one that crossed an alert fraction.</summary>
    public bool AlertedNow => Alerts.Count > 0;

    /// <summary>True when the definition halts at full and this booking filled it.</summary>
    public bool HaltedNow => BreachedNow && Status.Halted;
}

/// <summary>
/// Dimension-agnostic limits: book a movement, get back where the dimension stands and what the booking
/// crossed.
///
/// <para><b>One write.</b> Bookings, alerts, the high-water mark and the halt decision are persisted in a
/// single resource write, so a crash between "the money moved" and "the limit noticed" is not a state the
/// store can be in. A ledger that booked first and updated its counters afterwards would lose the counter
/// on a crash and silently permit the next booking.</para>
/// </summary>
public interface ILimitLedger
{
    /// <summary>Book a movement and return the dimension's new state and what the booking crossed.</summary>
    ValueTask<LimitOutcome> BookAsync(string scope, LimitDefinition definition, LimitBooking booking, CancellationToken ct);

    /// <summary>Where a dimension stands, without moving it.</summary>
    ValueTask<LimitStatus> ProbeAsync(string scope, LimitDefinition definition, CancellationToken ct);

    /// <summary>Every dimension this scope has ever booked against, and where each stands.</summary>
    ValueTask<IReadOnlyList<LimitStatus>> StatusAsync(string scope, IReadOnlyList<LimitDefinition> definitions, CancellationToken ct);
}
