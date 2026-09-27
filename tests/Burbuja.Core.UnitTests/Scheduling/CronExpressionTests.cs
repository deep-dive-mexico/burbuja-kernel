using Burbuja.Core.Scheduling;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Scheduling;

/// <summary>
/// The cases are nightshift's <c>src/schedule/__tests__/cron.test.ts</c> verbatim (its zone,
/// <c>America/Mexico_City</c>, is UTC-6 with no DST, so those assertions transfer exactly), plus the
/// parts the C# side adds: <see cref="CronExpression.Next"/> and real daylight-saving behaviour in a
/// zone that has it.
/// </summary>
public sealed class CronExpressionTests
{
    private static readonly TimeZoneInfo Mexico = TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City");

    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static CronExpression Parse(string text)
    {
        CronExpression.TryParse(text, out var cron).ShouldBeTrue($"'{text}' must parse");
        return cron!;
    }

    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Matches_every_minute() => Parse("* * * * *").Matches(At("2026-09-02T14:30:00Z"), Mexico).ShouldBeTrue();

    [Fact]
    public void Matches_a_specific_minute_and_hour()
    {
        var cron = Parse("30 8 * * *");
        cron.Matches(At("2026-09-02T14:30:00Z"), Mexico).ShouldBeTrue();   // 08:30 in Mexico City
        cron.Matches(At("2026-09-02T14:31:00Z"), Mexico).ShouldBeFalse();
    }

    [Fact]
    public void Matches_a_weekday_range()
    {
        var cron = Parse("0 8 * * 1-5");
        cron.Matches(At("2026-09-07T14:00:00Z"), Mexico).ShouldBeTrue();   // Monday
        cron.Matches(At("2026-09-11T14:00:00Z"), Mexico).ShouldBeTrue();   // Friday
        cron.Matches(At("2026-09-12T14:00:00Z"), Mexico).ShouldBeFalse();  // Saturday
    }

    [Fact]
    public void Matches_a_step()
    {
        var cron = Parse("*/15 * * * *");
        cron.Matches(At("2026-09-02T14:00:00Z"), Mexico).ShouldBeTrue();
        cron.Matches(At("2026-09-02T14:15:00Z"), Mexico).ShouldBeTrue();
        cron.Matches(At("2026-09-02T14:30:00Z"), Mexico).ShouldBeTrue();
        cron.Matches(At("2026-09-02T14:17:00Z"), Mexico).ShouldBeFalse();
    }

    [Fact]
    public void Matches_comma_separated_values()
    {
        var cron = Parse("0,30 * * * *");
        cron.Matches(At("2026-09-02T14:00:00Z"), Mexico).ShouldBeTrue();
        cron.Matches(At("2026-09-02T14:30:00Z"), Mexico).ShouldBeTrue();
        cron.Matches(At("2026-09-02T14:15:00Z"), Mexico).ShouldBeFalse();
    }

    [Fact]
    public void Matches_a_specific_day_and_month()
    {
        var cron = Parse("0 12 1 1 *");
        cron.Matches(At("2026-01-01T18:00:00Z"), Mexico).ShouldBeTrue();   // noon, Jan 1
        cron.Matches(At("2026-01-02T18:00:00Z"), Mexico).ShouldBeFalse();
    }

    [Fact]
    public void Handles_the_timezone_offset_across_midnight()
    {
        var cron = Parse("0 0 * * *");
        cron.Matches(At("2026-09-02T06:00:00Z"), Mexico).ShouldBeTrue();   // midnight local
        cron.Matches(At("2026-09-02T05:00:00Z"), Mexico).ShouldBeFalse();
    }

    // ---------------------------------------------------------------- Next

    [Fact]
    public void Next_is_strictly_after_and_lands_on_the_next_matching_minute()
    {
        var cron = Parse("*/2 * * * *");
        // 14:30:00Z is 08:30 local; strictly after means 08:32, not 08:30.
        cron.Next(At("2026-09-02T14:30:00Z"), Mexico).ShouldBe(At("2026-09-02T14:32:00Z"));
        // A non-matching instant steps to the next even minute.
        cron.Next(At("2026-09-02T14:31:00Z"), Mexico).ShouldBe(At("2026-09-02T14:32:00Z"));
        // Half a minute past the boundary still belongs to 14:30, so the answer is 14:32.
        cron.Next(At("2026-09-02T14:30:30Z"), Mexico).ShouldBe(At("2026-09-02T14:32:00Z"));
    }

    [Fact]
    public void Next_rolls_over_the_day_and_the_month()
    {
        Parse("0 8 * * 1-5").Next(At("2026-09-12T14:00:00Z"), Mexico)   // Saturday 08:00 local
            .ShouldBe(At("2026-09-14T14:00:00Z"));                     // Monday 08:00 local

        // The month skip must not overshoot a matching first-of-month.
        Parse("0 12 1 1 *").Next(At("2026-09-02T14:00:00Z"), Mexico).ShouldBe(At("2027-01-01T18:00:00Z"));
    }

    [Fact]
    public void Next_returns_null_when_nothing_matches_inside_the_horizon()
    {
        // 31 February never happens.
        Parse("0 0 31 2 *").Next(At("2026-09-02T14:00:00Z"), Mexico).ShouldBeNull();
    }

    // ---------------------------------------------------------------- DST

    [Fact]
    public void A_wall_time_inside_the_spring_forward_gap_is_skipped_not_doubled()
    {
        // America/New_York springs forward 2026-03-08: 02:00-02:59 local does not exist.
        var cron = Parse("30 2 * * *");
        var next = cron.Next(At("2026-03-08T04:00:00Z"), NewYork);   // 2026-03-07 23:00 EST
        next.ShouldNotBeNull();

        // The 02:30 wall time on the 8th is invalid, so the next real match is 02:30 on the 9th.
        var local = TimeZoneInfo.ConvertTime(next!.Value, NewYork);
        local.Day.ShouldBe(9);
        local.Hour.ShouldBe(2);
        local.Minute.ShouldBe(30);
    }

    [Fact]
    public void A_wall_time_inside_the_fall_back_overlap_fires_once()
    {
        // America/New_York falls back 2026-11-01: 01:00-01:59 local happens twice.
        var cron = Parse("30 1 * * *");
        var after = At("2026-11-01T04:00:00Z");                      // 2026-10-31 00:00 EDT
        DateTimeOffset? firstOrNull = cron.Next(after, NewYork);
        firstOrNull.ShouldNotBeNull();
        var first = firstOrNull!.Value;

        var local = TimeZoneInfo.ConvertTime(first, NewYork);
        local.Day.ShouldBe(1);
        local.Hour.ShouldBe(1);
        local.Minute.ShouldBe(30);

        // Stepping past the first occurrence must reach the NEXT DAY, not the repeated hour.
        DateTimeOffset? secondOrNull = cron.Next(first, NewYork);
        secondOrNull.ShouldNotBeNull();
        TimeZoneInfo.ConvertTime(secondOrNull!.Value, NewYork).Day.ShouldBe(2);
    }

    // ---------------------------------------------------------------- parsing

    [Theory]
    [InlineData("@hourly", "0 * * * *")]
    [InlineData("@daily", "0 0 * * *")]
    [InlineData("@midnight", "0 0 * * *")]
    public void Aliases_expand(string text, string expected) => Parse(text).Text.ShouldBe(expected);

    [Fact]
    public void Seven_means_sunday() =>
        Parse("0 8 * * 7").Matches(At("2026-09-13T14:00:00Z"), Mexico).ShouldBeTrue();  // a Sunday

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("* * * *")]                  // four fields
    [InlineData("* * * * * *")]              // six fields
    [InlineData("60 * * * *")]               // minute out of range
    [InlineData("* 24 * * *")]               // hour out of range
    [InlineData("* * 0 * *")]                // day 0 does not exist
    [InlineData("* * * 13 *")]               // month 13
    [InlineData("* * * * 8")]                // weekday 8
    [InlineData("*/0 * * * *")]              // zero step
    [InlineData("*/-1 * * * *")]
    [InlineData("5-1 * * * *")]              // reversed range
    [InlineData("* * * * MON")]              // names are not in the grammar
    [InlineData("nonsense")]
    public void TryParse_rejects_what_it_does_not_understand(string text) =>
        CronExpression.TryParse(text, out _).ShouldBeFalse($"'{text}' must be rejected, never guessed at");

    [Fact]
    public void Both_day_fields_restricted_matches_either()
    {
        // Standard cron: day-of-month OR day-of-week when both are restricted.
        var cron = Parse("0 0 13 * 5");                                   // the 13th, or any Friday
        cron.Matches(At("2026-11-13T06:00:00Z"), Mexico).ShouldBeTrue();  // a Friday the 13th
        cron.Matches(At("2026-10-13T06:00:00Z"), Mexico).ShouldBeTrue();  // the 13th, a Tuesday
        cron.Matches(At("2026-10-16T06:00:00Z"), Mexico).ShouldBeTrue();  // a Friday, not the 13th
        cron.Matches(At("2026-10-14T06:00:00Z"), Mexico).ShouldBeFalse(); // neither
    }
}
