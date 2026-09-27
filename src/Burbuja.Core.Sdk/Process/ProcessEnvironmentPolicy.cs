using System.Collections.Frozen;
using Burbuja.Core.Abstractions.Process;

namespace Burbuja.Core.Process;

public static class ProcessEnvironmentPolicy
{
    public static readonly FrozenSet<string> Allowlist = new[]
    {
        "PATH", "HOME", "USER", "LOGNAME", "SHELL",
        "LANG", "LC_ALL", "LC_CTYPE", "LC_MESSAGES", "LC_NUMERIC", "LC_TIME", "LC_COLLATE",
        "TZ", "TERM", "TMPDIR", "TMP", "TEMP",
        "HOSTNAME", "USERNAME", "USERPROFILE",
        "SystemRoot", "ComSpec", "PATHEXT", "WINDIR",
        "XDG_RUNTIME_DIR",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, string> Build(
        IReadOnlyDictionary<string, string> parent,
        IReadOnlyDictionary<string, string> requested)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in parent)
        {
            if (Allowlist.Contains(key) && !IsDenied(key))
                env[key] = value;
        }

        foreach (var (key, value) in requested)
        {
            if (!Allowlist.Contains(key) || IsDenied(key))
                continue;
            env[key] = value;
        }

        env["GIT_TERMINAL_PROMPT"] = "0";
        env["CI"] = "1";
        env["NO_COLOR"] = "1";

        parent.TryGetValue("HOME", out var home);
        if (string.IsNullOrEmpty(home))
            parent.TryGetValue("USERPROFILE", out home);
        if (env.TryGetValue("PATH", out var path))
            env["PATH"] = StripLocalBin(path, home);

        return env;
    }

    public static bool IsDenied(string key)
    {
        if (key.StartsWith("BURBUJA_", StringComparison.OrdinalIgnoreCase))
            return true;
        if (key.StartsWith("AWS_SECRET_", StringComparison.OrdinalIgnoreCase))
            return true;
        if (key.EndsWith("_TOKEN", StringComparison.OrdinalIgnoreCase))
            return true;
        if (key.EndsWith("_SECRET", StringComparison.OrdinalIgnoreCase))
            return true;
        if (key.EndsWith("_PASSWORD", StringComparison.OrdinalIgnoreCase))
            return true;
        if (key.Contains("API_KEY", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    public static string StripLocalBin(string path, string? home)
    {
        var localBin = string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".local", "bin");
        var parts = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p =>
            {
                if (p.Contains(".local/bin", StringComparison.Ordinal) || p.Contains(@".local\bin", StringComparison.Ordinal))
                    return false;
                if (localBin is not null && PathsEqual(p, localBin))
                    return false;
                return true;
            });
        return string.Join(Path.PathSeparator, parts);
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a.Trim()), Path.GetFullPath(b.Trim()), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
