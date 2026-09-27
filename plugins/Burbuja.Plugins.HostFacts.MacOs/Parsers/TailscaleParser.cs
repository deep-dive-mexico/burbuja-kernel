using System.Text.RegularExpressions;

namespace Burbuja.Plugins.HostFactsMacOs.Parsers;

/// <summary>
/// <c>tailscale status</c>. Semantics ported from nightshift's <c>src/host/diagnostics.ts</c>: the
/// first non-empty line is this node, and an <c>offline</c> on it means the tailnet is down; "Tailscale
/// is stopped" / "Logged out" is a real <c>false</c>.
///
/// <para>"Not installed" is <c>null</c>, never <c>false</c>. The reference already treats a CLI that
/// failed to launch as unknown, and this parser widens that set deliberately: a login shell's
/// "command not found", a daemon that is not answering ("failed to connect to local Tailscale
/// service; is Tailscale running?" — which the reference's regexes would have misread as
/// <em>online</em>, because that line contains no "offline"), and any CLIError are all "we could not
/// ask", not "the tailnet is down". The provider makes the same call one level up, by not treating a
/// missing binary as an answer at all.</para>
/// </summary>
public static class TailscaleParser
{
    private static readonly Regex UnknownPattern = new(
        @"CLIError|failed to start|couldn.t be completed|not installed|command not found|No such file or directory|failed to connect|is Tailscale running",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LoggedOutPattern = new(
        @"Tailscale is stopped|Logged out|not running",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex OfflinePattern = new(@"\boffline\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool? Parse(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;
        if (UnknownPattern.IsMatch(stdout))
            return null;
        if (LoggedOutPattern.IsMatch(stdout))
            return false;

        var first = FirstNonEmptyLine(stdout);
        return first is null ? null : !OfflinePattern.IsMatch(first);
    }

    private static string? FirstNonEmptyLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.Trim().Length > 0)
                return line;
        }

        return null;
    }
}
