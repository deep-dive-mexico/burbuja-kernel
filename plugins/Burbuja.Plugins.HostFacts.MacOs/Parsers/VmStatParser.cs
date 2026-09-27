using System.Globalization;
using System.Text.RegularExpressions;

namespace Burbuja.Plugins.HostFactsMacOs.Parsers;

/// <summary>
/// <c>vm_stat</c>. Ported from nightshift's <c>src/host/pressure.ts</c>: free RAM is
/// <c>(Pages free + Pages inactive + Pages speculative) × page size</c>, floored to whole MB, because
/// macOS keeps most of RAM as reclaimable cache and "Pages free" alone understates what is available.
///
/// <para>One deliberate difference from the reference: nightshift defaults the page size to 4096 and
/// treats a missing page count as 0. Both are guesses that would silently misreport — on Apple
/// Silicon the real page size is 16384, so the default would divide the answer by four. Here a
/// missing page size, or any missing one of the three counts, is <c>null</c>.</para>
/// </summary>
public static class VmStatParser
{
    private static readonly Regex PageSizePattern = new(@"page size of (\d+) bytes", RegexOptions.Compiled);
    private static readonly Regex PagesFreePattern = new(@"Pages free:\s+(\d+)", RegexOptions.Compiled);
    private static readonly Regex PagesInactivePattern = new(@"Pages inactive:\s+(\d+)", RegexOptions.Compiled);
    private static readonly Regex PagesSpeculativePattern = new(@"Pages speculative:\s+(\d+)", RegexOptions.Compiled);

    private const decimal BytesPerMb = 1024m * 1024m;

    /// <summary>The page size the header names, in bytes.</summary>
    public static int? ParsePageSizeBytes(string? stdout)
    {
        if (string.IsNullOrEmpty(stdout))
            return null;
        var match = PageSizePattern.Match(stdout);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
            ? size
            : null;
    }

    /// <summary>Free RAM in MB, or null when the page size or any of the three counts is unreadable.</summary>
    public static decimal? ParseFreeMegabytes(string? stdout)
    {
        if (string.IsNullOrEmpty(stdout))
            return null;

        var pageSize = ParsePageSizeBytes(stdout);
        var free = ParseCount(PagesFreePattern, stdout);
        var inactive = ParseCount(PagesInactivePattern, stdout);
        var speculative = ParseCount(PagesSpeculativePattern, stdout);
        if (pageSize is null || free is null || inactive is null || speculative is null)
            return null;

        var bytes = (long)(free.Value + inactive.Value + speculative.Value) * pageSize.Value;
        return Math.Floor(bytes / BytesPerMb);
    }

    private static long? ParseCount(Regex pattern, string text)
    {
        var match = pattern.Match(text);
        return match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
