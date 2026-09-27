using System.Globalization;
using System.Text.RegularExpressions;

namespace Burbuja.Plugins.HostFactsMacOs.Parsers;

/// <summary>
/// <c>ioreg -rn AppleSmartBattery</c>: the battery gas gauge's own time to empty, in <b>minutes</b>.
///
/// <para><b>Why this exists next to a parser that already reads an estimate.</b> <c>pmset -g batt</c>
/// prints its remaining time as <c>h:mm</c> and, on the reference Mac, that string is not the gauge's
/// value: three readings 35 minutes apart printed <c>20:00</c> while <c>AvgTimeToEmpty</c> moved
/// 1218 → 1601 → 2119 minutes. Whatever pmset does to the number (it is at least pinned, and it
/// never printed more than <c>20:00</c>), the gauge is where the un-rounded answer lives.</para>
///
/// <para><b>Minutes, and the 16-bit sentinel.</b> IOKit documents these as minutes, and the field is
/// 16 bits, so <c>65535</c> (<c>0xFFFF</c>) is what an unknown looks like — a discharging Mac with a
/// full battery reports exactly that for <c>AvgTimeToFull</c>. A value that is out of range or not
/// positive is not an estimate and reads as null; nothing here fabricates a number.</para>
/// </summary>
public static class IoregBatteryParser
{
    /// <summary>IOKit's "no estimate" sentinel: the field is 16 bits wide.</summary>
    private const int UnknownSentinel = 65535;

    private static readonly Regex AvgTimeToEmptyPattern = new(@"""AvgTimeToEmpty""\s*=\s*(\d{1,5})", RegexOptions.Compiled);
    private static readonly Regex TimeRemainingPattern = new(@"""TimeRemaining""\s*=\s*(\d{1,5})", RegexOptions.Compiled);

    /// <summary>
    /// Minutes to empty, or null when the gauge did not say. <c>AvgTimeToEmpty</c> is the averaged
    /// value macOS itself reports, so it wins; <c>TimeRemaining</c> is the same quantity without the
    /// average and is the fallback, not a second opinion.
    /// </summary>
    public static int? ParseMinutes(string? stdout)
    {
        if (string.IsNullOrEmpty(stdout))
            return null;

        return Read(AvgTimeToEmptyPattern.Match(stdout)) ?? Read(TimeRemainingPattern.Match(stdout));
    }

    private static int? Read(Match match)
    {
        if (!match.Success)
            return null;

        var minutes = int.Parse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
        return minutes is > 0 and < UnknownSentinel ? minutes : null;
    }
}
