using System.Globalization;
using System.Text.RegularExpressions;

namespace Burbuja.Plugins.HostFactsMacOs.Parsers;

/// <summary>
/// <c>ps -o rss= -p &lt;pid&gt;</c> → this process's resident set size. macOS prints RSS in 1024-byte
/// blocks and nothing else (the trailing <c>=</c> in the format suppresses the header), so the answer
/// is one integer and anything else is unknown. RSS rather than the managed working set, deliberately:
/// it is the number the field is named after and the one an operator compares against <c>ps</c>.
/// </summary>
public static class PsParser
{
    private static readonly Regex RssPattern = new(@"^\s*(\d+)", RegexOptions.Compiled);

    private const decimal KbPerMb = 1024m;

    public static decimal? ParseRssMb(string? stdout)
    {
        if (string.IsNullOrEmpty(stdout))
            return null;
        var match = RssPattern.Match(stdout);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
            return null;
        return Math.Round(kb / KbPerMb, 0, MidpointRounding.AwayFromZero);
    }
}
