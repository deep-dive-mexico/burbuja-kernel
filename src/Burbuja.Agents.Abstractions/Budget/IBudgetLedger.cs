namespace Burbuja.Agents.Abstractions.Budget;

using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

public enum BudgetKind
{
    Worker,
    Intake,
    Supervisor,
    Mentor,
}

public sealed record BudgetBooking(Guid JobId, Guid RunId, BudgetKind Kind, decimal Usd, int InputTokens, int OutputTokens);

public sealed record DailySpend(DateOnly Day, decimal SpentUsd, int JobCount);

public interface IBudgetLedger
{
    /// <summary>
    /// Books one charge and returns the ledger entry's id (M9 T1).
    ///
    /// <para>The id is what a turn event cites. A reader reconciling "what did this turn cost" against
    /// "what was booked" then has ONE record of the money — the ledger — and the turn points at it
    /// instead of carrying a second copy that two writers could disagree about. An implementation that
    /// has no entry to name returns <see cref="Guid.Empty"/>, which a reader must treat as "not booked"
    /// (the unpriced-model path).</para>
    /// </summary>
    ValueTask<Guid> BookAsync(BudgetBooking booking, CancellationToken ct);

    ValueTask<DailySpend> GetDailyAsync(DateOnly day, CancellationToken ct);

    ValueTask<decimal> GetJobSpendAsync(Guid jobId, CancellationToken ct);
}

/// <summary>
/// M10 H3 — the daily hard stop, READ AT THE MOMENT OF THE CALL.
///
/// <para><b>The defect this closes.</b> <c>Budget:DailyHardStopUsd</c> was read once, into a
/// <see cref="JobBudgets"/> snapshot at PUT (<c>JobHandler</c>), and enforced nowhere: a number the
/// configuration carried, the console could show, and no code compared against anything. On a host with a
/// real model staged and the AKOS ingress on, every turn a user opened spent against a limit that did not
/// exist. A limit nobody reads is not a limit.</para>
///
/// <para><b>Why configuration per call rather than the frozen snapshot.</b> An operator who raises the stop
/// while a job is parked is doing the one thing the park exists to allow. Reading the snapshot would hold the
/// job against the value that was set when it was created, so the fix would need the job re-PUT — and the
/// person who raises a spend limit is not usually the person who owns the job record.</para>
///
/// <para><b>What "at or above" means, precisely.</b> The check is <c>spent &gt;= stop</c>, and a stop that is
/// absent or <c>0</c> means <b>no hard stop configured</b> — the same reading
/// <see cref="JobBudgets.DailyHardStopUsd"/> has always had, so a host that never set the key keeps today's
/// behaviour rather than refusing every call. The neuter for a fact is therefore <c>stop = 0</c>: with no
/// limit configured nothing is refused, which is exactly the state the release was in before this
/// landed.</para>
/// </summary>
public static class DailyHardStop
{
    public const string Setting = "Budget:DailyHardStopUsd";

    /// <summary>The configured stop, or null when the host has none. Read fresh on every call, on purpose.</summary>
    public static decimal? Configured(IConfiguration configuration) =>
        Guard(configuration) is { Kind: BudgetGuardKind.Limited } guard ? guard.StopUsd : null;

    /// <summary>
    /// M2 — the host's answer about its own money guard, as THREE states rather than two.
    ///
    /// <para><b>Why "unset" and "zero" had to stop meaning the same thing.</b> The first version read the key
    /// and treated both an absent value and <c>0</c> as "no stop", which is the shape that lets a host forget
    /// the key entirely and spend without a ceiling — the M10 review's R7. They are different statements:
    /// <b>unset</b> is a host that has not decided (and a PAID provider must refuse to serve it),
    /// <b>zero</b> is a host that has decided to spend nothing (and everything must be refused), and
    /// <b>positive</b> is the ordinary limit. <c>Budget:DailyHardStopUsd=0</c> is the plan's own neuter: set it
    /// and the paid path must go silent, which is the only way to prove the guard is load-bearing.</para>
    /// </summary>
    public static BudgetGuard Guard(IConfiguration configuration)
    {
        var raw = configuration[Setting];
        if (string.IsNullOrWhiteSpace(raw))
            return new BudgetGuard(BudgetGuardKind.Unset, null);

        if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var stop))
            return new BudgetGuard(BudgetGuardKind.Unset, null);

        return stop <= 0m
            ? new BudgetGuard(BudgetGuardKind.Zero, 0m)
            : new BudgetGuard(BudgetGuardKind.Limited, stop);
    }

    /// <summary>
    /// The stop, the day, and what the ledger holds for it — or null when the host is inside its limit.
    /// A host with no stop configured is always inside it.
    /// </summary>
    public static async ValueTask<HardStopReading?> ReadAsync(
        IConfiguration configuration,
        IBudgetLedger ledger,
        TimeProvider time,
        CancellationToken ct)
    {
        var guard = Guard(configuration);
        if (guard.Kind is BudgetGuardKind.Unset)
            return null;

        var day = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var spend = await ledger.GetDailyAsync(day, ct).ConfigureAwait(false);

        // `0` refuses EVERYTHING, whatever the ledger says: a host that has decided to spend nothing has, by
        // that decision, already reached its stop.
        if (guard.Kind is BudgetGuardKind.Zero)
            return new HardStopReading(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), spend.SpentUsd, 0m);


        return spend.SpentUsd >= guard.StopUsd
            ? new HardStopReading(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), spend.SpentUsd, guard.StopUsd!.Value)
            : null;
    }
}

/// <summary>Which of the three things a host has said about its money guard. See <see cref="DailyHardStop.Guard"/>.</summary>
public enum BudgetGuardKind
{
    /// <summary>Nobody said anything. A paid provider must not serve such a host.</summary>
    Unset,

    /// <summary>The host decided to spend nothing, so everything is refused.</summary>
    Zero,

    /// <summary>An ordinary ceiling.</summary>
    Limited,
}

/// <summary>The reading, with the sentence a refusal prints.</summary>
public sealed record BudgetGuard(BudgetGuardKind Kind, decimal? StopUsd)
{
    public string Sentence => Kind switch
    {
        BudgetGuardKind.Zero =>
            $"'{DailyHardStop.Setting}' is 0, which means this host spends nothing at all.",
        _ =>
            $"'{DailyHardStop.Setting}' is not configured, so this host has no ceiling for the day. Configure it "
            + "before a paid model is allowed to serve — an unset guard is not an unlimited budget.",
    };
}

/// <summary>
/// A host that has reached its daily stop, as the three refusal paths report it. Carries the day and both
/// numbers so the sentence a user reads names what was spent and against what — the event
/// <c>agents.budget.hardstop</c> and an AKOS turn's <c>PAUSE</c> judgment both render from this one record.
/// </summary>
public sealed record HardStopReading(string Day, decimal SpentUsd, decimal StopUsd)
{
    public const string EventType = "agents.budget.hardstop";

    public string Sentence =>
        $"the day's budget is spent: {SpentUsd.ToString("0.######", CultureInfo.InvariantCulture)} of "
        + $"{StopUsd.ToString("0.######", CultureInfo.InvariantCulture)} USD on {Day}. Raise {DailyHardStop.Setting} "
        + "or wait for the next UTC day.";

    public JsonObject ToJson() => new()
    {
        ["day"] = Day,
        ["spentUsd"] = SpentUsd,
        ["stopUsd"] = StopUsd,
    };
}
