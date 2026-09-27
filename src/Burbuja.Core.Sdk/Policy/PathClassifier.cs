using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Burbuja.Core.Abstractions.Policy;

namespace Burbuja.Core.Sdk.Policy;

/// <summary>
/// Path containment that survives symlinks, <c>..</c>, case-folding (APFS) and
/// <c>~</c> / <c>$HOME</c> spellings. Port of nightshift <c>src/policy/paths.ts</c>.
/// </summary>
public static class PathClassifier
{
    private static readonly AsyncLocal<IPathProbe?> AmbientProbe = new();
    private static readonly Regex HomeRx = new(@"\$\{HOME\}|\$HOME\b", RegexOptions.Compiled);
    private static readonly Regex TmpRx = new(@"\$\{?TMPDIR\}?", RegexOptions.Compiled);

    public static IPathProbe Probe
    {
        get => AmbientProbe.Value ?? RealPathProbe.Shared;
        set => AmbientProbe.Value = value;
    }

    public static readonly string HomeDir =
        Environment.GetEnvironmentVariable("HOME")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static readonly string TmpDir = RealPathLoose(Path.GetTempPath());

    public static string ExpandPath(string p, string cwd)
    {
        var s = p.Trim();
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
            s = s[1..^1];

        if (s.StartsWith('~') && (s.Length == 1 || s[1] == '/'))
            s = HomeDir + s[1..];

        s = HomeRx.Replace(s, HomeDir);
        s = TmpRx.Replace(s, Path.GetTempPath().TrimEnd('/'));

        try
        {
            return Path.IsPathRooted(s)
                ? Path.GetFullPath(s)
                : Path.GetFullPath(Path.Combine(cwd, s));
        }
        catch
        {
            return s;
        }
    }

    public static string RealPathLoose(string p) =>
        AmbientProbe.Value is { } probe ? probe.RealPath(p) : LexicalRealPath(p);

    public static string LexicalRealPath(string p)
    {
        var cur = p;
        var tail = new List<string>();
        while (!(File.Exists(cur) || Directory.Exists(cur)))
        {
            var parent = Path.GetDirectoryName(cur);
            if (string.IsNullOrEmpty(parent) || parent == cur)
                break;
            tail.Insert(0, Path.GetFileName(cur));
            cur = parent;
        }

        var real = cur;
        try
        {
            if (Directory.Exists(cur))
            {
                var dir = new DirectoryInfo(cur);
                real = dir.ResolveLinkTarget(true)?.FullName ?? dir.FullName;
            }
            else if (File.Exists(cur))
            {
                var file = new FileInfo(cur);
                real = file.ResolveLinkTarget(true)?.FullName ?? file.FullName;
            }
            else
            {
                real = Path.GetFullPath(cur);
            }
        }
        catch
        {
            try { real = Path.GetFullPath(cur); }
            catch { /* lexical fallback */ }
        }

        if (tail.Count == 0)
            return real;
        try
        {
            return Path.GetFullPath(Path.Combine([real, .. tail]));
        }
        catch
        {
            return Path.Combine([real, .. tail]);
        }
    }

    public static bool RealContains(string root, string target)
    {
        if (AmbientProbe.Value is { } probe)
            return probe.Contains(root, target);
        var r = FoldForCompare(LexicalRealPath(root));
        var t = FoldForCompare(LexicalRealPath(target));
        var sep = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? '\\' : '/';
        return t == r || t.StartsWith(r + sep, StringComparison.Ordinal);
    }

    public static string? UnderAny(IReadOnlyList<string> roots, string target)
    {
        foreach (var r in roots)
        {
            if (RealContains(r, target))
                return r;
        }

        return null;
    }

    internal static string FoldForCompare(string p) =>
        p.ToLowerInvariant().TrimEnd('/', '\\');
}
