using System.Globalization;
using System.Text.RegularExpressions;

namespace Burbuja.Plugins.HostFactsMacOs.Parsers;

/// <summary>
/// The three <c>sysctl</c> reads this milestone wants. Semantics ported from nightshift's
/// <c>src/host/pressure.ts</c> (memory pressure level, swap) and <c>src/host/diagnostics.ts</c>
/// (boot time). Each parser accepts either the named or the bare (<c>-n</c>) form of the same output,
/// because both are the same fact.
/// </summary>
public static class SysctlParser
{
    private static readonly Regex NamedLevelPattern = new(@"kern\.memorystatus_level\s*:\s*(-?\d+)", RegexOptions.Compiled);
    private static readonly Regex SwapUsedPattern = new(@"\bused\s*=\s*([0-9]+(?:\.[0-9]+)?)\s*([MG])", RegexOptions.Compiled);
    private static readonly Regex BootSecPattern = new(@"\bsec\s*=\s*(\d+)", RegexOptions.Compiled);

    /// <summary>
    /// <c>sysctl kern.memorystatus_level</c> — 0-100, higher is healthier; below 15 is critical, which
    /// is the threshold the reference host check escalates on. Anything unreadable, or outside 0-100,
    /// is null: a pressure level that cannot exist is not a fact.
    /// </summary>
    public static decimal? ParseMemoryPressureLevel(string? stdout)
    {
        if (string.IsNullOrEmpty(stdout))
            return null;

        var match = NamedLevelPattern.Match(stdout);
        var text = match.Success ? match.Groups[1].Value : stdout.Trim();
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level))
            return null;
        return level is >= 0 and <= 100 ? level : null;
    }

    /// <summary>
    /// <c>sysctl vm.swapusage</c> — <c>total = 0.00M used = 0.00M free = 0.00M</c>; the value is
    /// rounded to whole MB exactly as the reference does (and a <c>G</c> unit is scaled to MB).
    /// </summary>
    public static decimal? ParseSwapUsedMb(string? stdout)
    {
        if (string.IsNullOrEmpty(stdout))
            return null;
        var match = SwapUsedPattern.Match(stdout);
        if (!match.Success)
            return null;
        if (!decimal.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;
        var mb = match.Groups[2].Value == "G" ? value * 1024m : value;
        return Math.Round(mb, 0, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// <c>sysctl -n kern.boottime</c> — <c>{ sec = 1756800000, usec = 123 } …</c> → seconds since boot,
    /// measured against <paramref name="now"/> (passed in, so this stays pure and testable). A boot
    /// time in the future is null rather than a negative uptime. The <c>\b</c> matters: without it the
    /// pattern matches the <c>sec</c> inside <c>usec</c> and a boot time of 1970 reads as valid.
    /// </summary>
    public static decimal? ParseUptimeSeconds(string? stdout, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(stdout))
            return null;
        var match = BootSecPattern.Match(stdout);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bootSec))
            return null;
        var uptime = now.ToUnixTimeSeconds() - bootSec;
        return uptime >= 0 ? uptime : null;
    }
}
