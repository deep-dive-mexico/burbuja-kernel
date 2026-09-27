using System.Globalization;
using System.Text.RegularExpressions;

namespace Burbuja.Plugins.HostFactsMacOs.Parsers;

/// <summary>One <c>df</c> row, in whole GB. All three come from the same row so they always add up the
/// way df printed them.</summary>
public sealed record DiskUsage(decimal TotalGb, decimal UsedGb, decimal FreeGb);

/// <summary>
/// <c>df -k /</c>. nightshift's <c>src/host/diagnostics.ts</c> reads columns 1-3 of the first data row
/// and rounds KB to GB the same way. This parser keeps that arithmetic and replaces the naive column
/// split, which breaks on any volume or mount point whose name contains a space and silently shifts
/// the numbers: the row is found by its shape instead — three consecutive integers immediately
/// followed by the <c>NN%</c> capacity column — which is unique to the block/used/available/capacity
/// group whether or not the filesystem name has spaces in it.
/// </summary>
public static class DiskFreeParser
{
    private static readonly Regex IntegerPattern = new(@"^\d+$", RegexOptions.Compiled);
    private static readonly Regex CapacityPattern = new(@"^\d+%$", RegexOptions.Compiled);

    private const decimal KbPerGb = 1024m * 1024m;

    public static DiskUsage? Parse(string? stdout)
    {
        if (string.IsNullOrEmpty(stdout))
            return null;

        foreach (var line in stdout.Split('\n'))
        {
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i + 3 < tokens.Length; i++)
            {
                if (!IntegerPattern.IsMatch(tokens[i])
                    || !IntegerPattern.IsMatch(tokens[i + 1])
                    || !IntegerPattern.IsMatch(tokens[i + 2])
                    || !CapacityPattern.IsMatch(tokens[i + 3]))
                {
                    continue;
                }

                return new DiskUsage(
                    ToGb(tokens[i]),
                    ToGb(tokens[i + 1]),
                    ToGb(tokens[i + 2]));
            }
        }

        return null;
    }

    private static decimal ToGb(string kb)
    {
        var value = decimal.Parse(kb, NumberStyles.Integer, CultureInfo.InvariantCulture);
        return Math.Round(value / KbPerGb, 0, MidpointRounding.AwayFromZero);
    }
}
