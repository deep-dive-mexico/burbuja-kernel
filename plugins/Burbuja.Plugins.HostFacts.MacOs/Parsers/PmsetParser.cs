using System.Text.RegularExpressions;

namespace Burbuja.Plugins.HostFactsMacOs.Parsers;

/// <summary>
/// The two <c>pmset</c> reads that are not the battery line. Nightshift keeps both in
/// <c>src/host/diagnostics.ts</c> (<c>parseTherm</c> and <c>parseSleepDisabled</c>); they live in their
/// own parser here because they are neither sysctl output nor battery facts.
/// </summary>
public static class PmsetParser
{
    private static readonly Regex CpuSpeedLimitPattern = new(@"CPU_Speed_Limit\s*=\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex SleepDisabledPattern = new(@"SleepDisabled\s+(\d)", RegexOptions.Compiled);

    /// <summary>
    /// <c>pmset -g therm</c> → the <c>CPU_Speed_Limit = 100</c> value as printed (<c>100</c> =
    /// unclamped; lower = the machine is throttling itself). Null when macOS reports no such line —
    /// which is the normal answer on Apple Silicon and on a machine with no thermal event recorded,
    /// and is why the fact is a string: there is no number to invent.
    /// </summary>
    public static string? ParseCpuSpeedLimit(string? stdout)
    {
        if (string.IsNullOrEmpty(stdout))
            return null;
        var match = CpuSpeedLimitPattern.Match(stdout);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// <c>pmset -g</c> → the <c>SleepDisabled 1</c> line (set by the always-on setup script). Null when
    /// the line is absent: a Mac that does not report the setting has not told us it is enabled.
    /// </summary>
    public static bool? ParseSleepDisabled(string? stdout)
    {
        if (string.IsNullOrEmpty(stdout))
            return null;
        var match = SleepDisabledPattern.Match(stdout);
        return match.Success ? match.Groups[1].Value == "1" : null;
    }
}
