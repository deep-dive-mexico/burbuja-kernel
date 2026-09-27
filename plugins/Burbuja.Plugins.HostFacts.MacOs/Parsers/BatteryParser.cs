using System.Globalization;
using System.Text.RegularExpressions;
using Burbuja.Core.Abstractions.Host;

namespace Burbuja.Plugins.HostFactsMacOs.Parsers;

/// <summary>
/// <c>pmset -g batt</c>. Semantics ported from nightshift's <c>src/host/battery.ts</c>: the percentage
/// is the first <c>NN%</c>, the estimate is the <c>h:mm</c> that pmset prints before "remaining", and
/// "charging" is the word right after a semicolon (so <c>discharging</c> and <c>not charging</c> are
/// both <em>not</em> charging).
///
/// <para>The one shape that matters most is the machine with no battery: a desktop Mac prints only
/// <c>Now drawing from 'AC Power'</c>, with no percentage anywhere. That is
/// <see cref="BatteryFacts"/> with all three members null — never 0%/false.</para>
///
/// <para><b>The estimate is a fallback, not the answer.</b> This line is where the number is
/// <em>rounded</em>: it is printed as <c>h:mm</c> and, measured on the reference Mac, it does not
/// track the gauge at all — three readings 35 minutes apart printed <c>20:00</c> every time while
/// <c>ioreg</c> moved 20 h 18 min → 26 h 41 min → 35 h 19 min. So the value here is minutes derived
/// from that string, and <see cref="IoregBatteryParser"/> is asked first.</para>
/// </summary>
public static class BatteryParser
{
    private static readonly Regex PercentPattern = new(@"(\d{1,3})%", RegexOptions.Compiled);
    private static readonly Regex RemainingPattern = new(@"(\d{1,2}):(\d{2})\s+remaining", RegexOptions.Compiled);
    private static readonly Regex ChargingPattern = new(@";\s*charging", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FinishingPattern = new(@"finishing charge", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AcPattern = new(@"drawing from 'AC Power'", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BatteryPattern = new(@"drawing from 'Battery Power'", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>What the battery line says, exactly as nightshift's <c>BatteryState.source</c> does:
    /// <c>"ac"</c>, <c>"battery"</c>, or <c>"unknown"</c> when pmset said neither.</summary>
    public static string Source(string? stdout)
    {
        if (string.IsNullOrEmpty(stdout))
            return "unknown";
        if (AcPattern.IsMatch(stdout))
            return "ac";
        return BatteryPattern.IsMatch(stdout) ? "battery" : "unknown";
    }

    public static BatteryFacts Parse(string? stdout)
    {
        if (string.IsNullOrEmpty(stdout))
            return new BatteryFacts(null, null, null);

        var percent = PercentPattern.Match(stdout);
        if (!percent.Success)
            return new BatteryFacts(null, null, null);

        var value = decimal.Parse(percent.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
        var remaining = RemainingPattern.Match(stdout);
        var charging = ChargingPattern.IsMatch(stdout) || FinishingPattern.IsMatch(stdout);
        return new BatteryFacts(value, charging, Minutes(remaining));
    }

    /// <summary>
    /// <c>h:mm</c> as minutes — and <c>0:00</c> reads as unknown, not as "empty in a moment". pmset
    /// prints <c>0:00 remaining</c> for a fully charged battery on AC (the held fixture
    /// <c>pmset-batt-charged.txt</c>), which is the shape of having no estimate rather than one.
    /// </summary>
    private static int? Minutes(Match remaining)
    {
        if (!remaining.Success)
            return null;

        var hours = int.Parse(remaining.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
        var minutes = int.Parse(remaining.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
        var total = (hours * 60) + minutes;
        return total > 0 ? total : null;
    }
}
