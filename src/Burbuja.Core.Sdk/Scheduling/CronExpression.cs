using System.Collections.Frozen;
using System.Globalization;

namespace Burbuja.Core.Scheduling;

/// <summary>
/// A minimal five-field cron expression: <c>minute hour day-of-month month day-of-week</c>.
///
/// <para>Ported from nightshift's <c>src/schedule/cron.ts</c> — same accepted grammar, same
/// wall-clock evaluation in a named time zone, same <c>*/N</c> semantics (a step divides the field
/// value, so <c>*/15</c> fires at :00, :15, :30 and :45 within every hour). Fields support
/// <c>*</c>, a single value, a range <c>N-M</c>, a comma list of those, and <c>*/S</c>. Day-of-week
/// is 0–6 with Sunday as 0, and 7 is accepted as Sunday too.</para>
///
/// <para>This is deliberately not a general cron library: no seconds, no <c>L</c>/<c>W</c>/<c>#</c>,
/// no step-on-range. The kernel only needs "does this minute match" and "when is the next one", and
/// a small auditable grammar is worth more here than coverage. <see cref="TryParse"/> rejects what
/// it does not understand rather than guessing, so a schedule either fires as written or fails at
/// PUT time.</para>
/// </summary>
public sealed class CronExpression
{
    /// <summary>How far <see cref="Next"/> will look before giving up: 366 days of minutes.</summary>
    public const int MaxLookaheadDays = 366;

    private static readonly FrozenDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["@hourly"] = "0 * * * *",
        ["@daily"] = "0 0 * * *",
        ["@midnight"] = "0 0 * * *",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private readonly FrozenSet<int> _minutes;
    private readonly FrozenSet<int> _hours;
    private readonly FrozenSet<int> _days;
    private readonly FrozenSet<int> _months;
    private readonly FrozenSet<int> _weekdays;

    private CronExpression(
        string text,
        FrozenSet<int> minutes,
        FrozenSet<int> hours,
        FrozenSet<int> days,
        FrozenSet<int> months,
        FrozenSet<int> weekdays)
    {
        Text = text;
        _minutes = minutes;
        _hours = hours;
        _days = days;
        _months = months;
        _weekdays = weekdays;
    }

    /// <summary>The expression as written, aliases already expanded (so a round trip is stable).</summary>
    public string Text { get; }

    /// <summary>True when the day-of-week field is a wildcard, i.e. the date match is day-of-month only.</summary>
    private bool DayOfMonthRestricted => _days.Count != 31;

    private bool DayOfWeekRestricted => _weekdays.Count != 7;

    public static bool TryParse(string? text, out CronExpression? expression)
    {
        expression = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = text.Trim();
        if (Aliases.TryGetValue(normalized.ToLowerInvariant(), out var expanded))
            normalized = expanded;

        var fields = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
            return false;

        if (!TryField(fields[0], 0, 59, out var minutes)
            || !TryField(fields[1], 0, 23, out var hours)
            || !TryField(fields[2], 1, 31, out var days)
            || !TryField(fields[3], 1, 12, out var months)
            || !TryField(fields[4], 0, 7, out var weekdays))
        {
            return false;
        }

        // 7 is Sunday in some dialects; fold it so matching only ever sees 0-6.
        if (weekdays.Contains(7))
        {
            weekdays = new HashSet<int>(weekdays.Where(d => d != 7)) { 0 };
        }

        expression = new CronExpression(
            string.Join(' ', fields),
            minutes.ToFrozenSet(),
            hours.ToFrozenSet(),
            days.ToFrozenSet(),
            months.ToFrozenSet(),
            weekdays.ToFrozenSet());
        return true;
    }

    private static bool TryField(string field, int min, int max, out HashSet<int> values)
    {
        values = [];
        if (field.Length == 0)
            return false;

        if (field == "*")
        {
            for (var v = min; v <= max; v++)
                values.Add(v);
            return true;
        }

        if (field.StartsWith("*/", StringComparison.Ordinal))
        {
            if (!int.TryParse(field.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var step) || step <= 0)
                return false;
            for (var v = min; v <= max; v++)
            {
                if (v % step == 0)
                    values.Add(v);
            }

            return true;
        }

        foreach (var part in field.Split(','))
        {
            if (part.Length == 0)
                return false;

            var dash = part.IndexOf('-', StringComparison.Ordinal);
            if (dash > 0)
            {
                if (!int.TryParse(part.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out var start)
                    || !int.TryParse(part.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var end))
                {
                    return false;
                }

                if (start > end || start < min || end > max)
                    return false;
                for (var v = start; v <= end; v++)
                    values.Add(v);
                continue;
            }

            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var single))
                return false;
            if (single < min || single > max)
                return false;
            values.Add(single);
        }

        return values.Count > 0;
    }

    /// <summary>Does <paramref name="instant"/>, read as wall time in <paramref name="timeZone"/>, match?</summary>
    public bool Matches(DateTimeOffset instant, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        return MatchesWall(TimeZoneInfo.ConvertTime(instant, timeZone).DateTime);
    }

    private bool MatchesWall(DateTime wall) =>
        _minutes.Contains(wall.Minute)
        && _hours.Contains(wall.Hour)
        && _months.Contains(wall.Month)
        && MatchesDay(wall);

    /// <summary>
    /// Standard cron day semantics: when BOTH day-of-month and day-of-week are restricted the day
    /// matches if EITHER does; when only one is restricted, only that one applies.
    /// </summary>
    private bool MatchesDay(DateTime wall)
    {
        var byMonth = _days.Contains(wall.Day);
        var byWeek = _weekdays.Contains((int)wall.DayOfWeek);

        if (DayOfMonthRestricted && DayOfWeekRestricted)
            return byMonth || byWeek;
        if (DayOfMonthRestricted)
            return byMonth;
        if (DayOfWeekRestricted)
            return byWeek;
        return true;
    }

    /// <summary>
    /// The first matching minute strictly after <paramref name="after"/>, or <c>null</c> when nothing
    /// matches inside <see cref="MaxLookaheadDays"/>.
    ///
    /// <para>The walk is in the zone's <b>wall time</b>, one minute at a time, which is what makes DST
    /// behave the way a person expects: a wall time that does not exist (the spring-forward gap) is
    /// skipped rather than firing twice, and a wall time that happens twice (the autumn overlap) fires
    /// once, at the first occurrence. That is also what nightshift's matcher did — it formatted the
    /// instant into the zone and compared fields, so a skipped local minute simply never matched.</para>
    /// </summary>
    public DateTimeOffset? Next(DateTimeOffset after, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        var startWall = TimeZoneInfo.ConvertTime(after, timeZone).DateTime;
        // Truncate to the minute, then step past it: "strictly after".
        var wall = new DateTime(startWall.Year, startWall.Month, startWall.Day, startWall.Hour, startWall.Minute, 0, DateTimeKind.Unspecified)
            .AddMinutes(1);

        var limit = MaxLookaheadDays * 24 * 60;
        for (var i = 0; i < limit; i++, wall = wall.AddMinutes(1))
        {
            // A cheap month skip: 30 days of minutes cannot match a month the expression excludes.
            if (!_months.Contains(wall.Month))
            {
                var firstOfNext = new DateTime(wall.Year, wall.Month, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(1);
                i += (int)(firstOfNext - wall).TotalMinutes - 1;
                wall = firstOfNext;
                continue;
            }

            if (!MatchesWall(wall))
                continue;

            if (timeZone.IsInvalidTime(wall))
                continue;

            var offset = timeZone.GetUtcOffset(wall);
            return new DateTimeOffset(wall, offset);
        }

        return null;
    }

    public override string ToString() => Text;
}
