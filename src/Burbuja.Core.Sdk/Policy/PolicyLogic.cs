using System.Threading;
using Burbuja.Core.Abstractions.Policy;

namespace Burbuja.Core.Sdk.Policy;

/// <summary>
/// Token-anchored policy predicates. Port of nightshift <c>src/policy/rules.ts</c>
/// (helpers + the 20 segment rules + exfiltration). Kernel infrastructure is Burbuja-only.
/// </summary>
public static class PolicyLogic
{
    private static readonly AsyncLocal<PolicyContext?> Ambient = new();

    public static PolicyContext? AmbientContext
    {
        get => Ambient.Value;
        set => Ambient.Value = value;
    }

    public static readonly string[] DefaultProtectedHosts = ["pi", "pi.local", "pi.example.test"];

    public static PolicyFinding? Worse(PolicyFinding? a, PolicyFinding? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return b.Tier > a.Tier ? b : a;
    }

    public static bool IsMoreSevere(RiskTier a, RiskTier b) => a > b;

    private static PolicyFinding Deny(string riskClass, string reason) =>
        new(RiskTier.HardDeny, riskClass, "", reason);
    private static PolicyFinding Esc(string riskClass, string reason) =>
        new(RiskTier.Escalate, riskClass, "", reason);
    private static PolicyFinding Gray(string riskClass, string reason) =>
        new(RiskTier.Gray, riskClass, "", reason);
    private static PolicyFinding Ok(string riskClass, string reason) =>
        new(RiskTier.Allow, riskClass, "", reason);

    public static bool IsPiHost(string token)
    {
        var h = BareHost(token);
        var patterns = AmbientContext?.ProtectedHosts is { Count: > 0 } listed
            ? listed
            : DefaultProtectedHosts;
        if (patterns.Any(p => HostMatches(h, p) || string.Equals(BareHost(p), h, StringComparison.OrdinalIgnoreCase)))
            return true;
        return h.EndsWith(".pi", StringComparison.Ordinal);
    }

    public static bool HostMatches(string host, string pattern)
    {
        var h = host.ToLowerInvariant();
        var p = pattern.ToLowerInvariant();
        if (p == h) return true;
        if (p.StartsWith("*."))
            return h.EndsWith(p[1..], StringComparison.Ordinal) || h == p[2..];
        return false;
    }

    public static bool MatchesAnyHost(string host, IReadOnlyList<string> patterns) =>
        patterns.Any(p => HostMatches(host, p));

    private static string BareHost(string token)
    {
        var h = token.ToLowerInvariant();
        var at = h.LastIndexOf('@');
        if (at >= 0)
            h = h[(at + 1)..];
        if (h.StartsWith('['))
        {
            var close = h.IndexOf(']');
            h = h[1..(close > 0 ? close : h.Length)];
        }
        else if (h.Count(c => c == ':') == 1)
            h = h.Split(':')[0];
        return h.TrimEnd('/');
    }

    private static readonly string[] LocalHosts =
        ["localhost", "127.0.0.1", "::1", "0.0.0.0", "host.docker.internal"];

    private static bool IsLocalHost(string host) => LocalHosts.Contains(BareHost(host));

    public static IReadOnlyList<string> UrlHosts(IReadOnlyList<string> argv)
    {
        var output = new List<string>();
        foreach (var t in argv)
        {
            var m = UrlHostRx.Match(t);
            if (m.Success)
                output.Add(BareHost(m.Groups[1].Value));
        }
        return output;
    }

    private static readonly System.Text.RegularExpressions.Regex UrlHostRx = new(
        @"^(?:https?|ftp|ws|wss):\/\/([^/?#]+)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly HashSet<string> HttpVerbs = new(StringComparer.Ordinal)
        { "curl", "wget", "http", "https", "httpie", "xh" };

    private static readonly HashSet<string> MutatingMethods = new(StringComparer.OrdinalIgnoreCase)
        { "POST", "PUT", "PATCH", "DELETE" };

    private static readonly HashSet<string> HttpBodyFlags = new(StringComparer.Ordinal)
    {
        "-d", "--data", "--data-raw", "--data-binary", "--data-urlencode", "--data-ascii",
        "--json", "-F", "--form", "--form-string", "-T", "--upload-file", "--post-data",
        "--post-file", "--body-data", "--body-file",
    };

    public static bool IsMutatingHttp(IReadOnlyList<string> argv)
    {
        for (var i = 1; i < argv.Count; i++)
        {
            var t = argv[i];
            if (t is "-X" or "--request" or "--method")
            {
                var m = (i + 1 < argv.Count ? argv[i + 1] : "").ToUpperInvariant();
                if (MutatingMethods.Contains(m))
                    return true;
                continue;
            }

            var inline = InlineMethodRx.Match(t);
            if (inline.Success && MutatingMethods.Contains(inline.Groups[1].Value.ToUpperInvariant()))
                return true;
            if (HttpBodyFlags.Contains(t))
                return true;
            var eq = t.IndexOf('=');
            if (eq > 0 && HttpBodyFlags.Contains(t[..eq]))
                return true;
        }

        return false;
    }

    private static readonly System.Text.RegularExpressions.Regex InlineMethodRx = new(
        @"^(?:-X|--request=|--method=)(\w+)$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public static IReadOnlyList<string> WriteRoots(PolicyContext ctx)
    {
        string[] baseRoots = [ctx.WorkRoot, ctx.ArtifactDir, PathClassifier.TmpDir, "/tmp", "/private/tmp"];
        return baseRoots;
    }

    public static readonly string[] ProtectedHome = new[]
    {
        ".ssh", ".aws", ".claude", ".claude.json", ".config/gh", ".gnupg", ".docker", ".kube",
        ".netrc", ".npmrc", ".zshrc", ".zprofile", ".zshenv", ".bashrc", ".bash_profile",
        ".profile", ".local/state/burbuja", ".local/bin", "Library/Keychains",
        "Library/LaunchAgents",
        "Library/Application Support/Google/Chrome/NativeMessagingHosts",
        // Both the product name and the pre-rename checkout stay protected.
        "Developer/burbuja-server",
        "Developer/burbuja-core",
    }.Select(rel => $"{PathClassifier.HomeDir}/{rel}").ToArray();

    public static bool IsProtectedHomePath(string abs) =>
        ProtectedHome.Any(p => PathClassifier.RealContains(p, abs));

    private static readonly HashSet<string> DevPassthrough = new(StringComparer.OrdinalIgnoreCase)
        { "/dev/null", "/dev/stdout", "/dev/stderr", "/dev/tty", "/dev/zero" };

    public static bool IsDevPassthrough(string p)
    {
        var l = p.ToLowerInvariant();
        return DevPassthrough.Contains(l) || l.StartsWith("/dev/fd/", StringComparison.Ordinal);
    }

    public static bool IsDevicePath(string p) =>
        p.StartsWith("/dev/", StringComparison.OrdinalIgnoreCase) && !IsDevPassthrough(p);

    public static readonly string[] DefaultTrustedPaths =
    [
        "src/Burbuja.Core", "src/Burbuja.Core.Abstractions", "src/Burbuja.Core.Sdk",
        "src/Burbuja.Host", "plugins/", "config/", "secrets",
    ];

    public static IReadOnlyList<string> SelfModTargets(PolicyContext ctx)
    {
        var trusted = ctx.TrustedPaths is { Count: > 0 } t ? t : DefaultTrustedPaths;
        var resolved = trusted
            .Select(p => Path.IsPathRooted(p) ? p : Path.Combine(ctx.HostRepoPath, p))
            .ToList();
        var home = PathClassifier.HomeDir;
        resolved.AddRange(
        [
            $"{home}/.claude", $"{home}/.ssh", $"{home}/.aws", $"{home}/.config/gh",
            $"{home}/Library/LaunchAgents", $"{home}/.zshrc", $"{home}/.zprofile",
            $"{home}/.zshenv", $"{home}/.bashrc", $"{home}/.bash_profile",
            $"{home}/.profile", $"{home}/.gitconfig", $"{home}/.netrc",
            "/etc", "/System", "/usr", "/bin", "/sbin",
            "/Library/LaunchAgents", "/Library/LaunchDaemons",
        ]);
        return resolved;
    }

    public static bool IsSelfModTarget(string abs, PolicyContext ctx) =>
        SelfModTargets(ctx).Any(p => PathClassifier.RealContains(p, abs));

    public static bool IsWritableTarget(string abs, PolicyContext ctx)
    {
        if (IsDevPassthrough(abs)) return true;
        if (IsProtectedHomePath(abs)) return false;
        return WriteRoots(ctx).Any(r => PathClassifier.RealContains(r, abs));
    }

    private static readonly System.Text.RegularExpressions.Regex SshLeafRx = new(
        @"/\.ssh/(.+)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly Func<string, bool>[] SecretPathRules =
    [
        p => p.Contains("/.aws/credentials", StringComparison.Ordinal),
        p => p.Contains("/.aws/config", StringComparison.Ordinal),
        p => p.Contains("/.aws/sso/cache", StringComparison.Ordinal),
        p => p.Contains("/.claude/.credentials.json", StringComparison.Ordinal),
        p => p.Contains("/.config/gh/hosts.yml", StringComparison.Ordinal),
        p => p.EndsWith("/.netrc", StringComparison.Ordinal),
        p => p.EndsWith("/.npmrc", StringComparison.Ordinal),
        p => p.Contains("/.docker/config.json", StringComparison.Ordinal),
        p => p.Contains("/.kube/config", StringComparison.Ordinal),
        p => p.Contains("/.gnupg/", StringComparison.Ordinal),
        p => p.Contains("/.password-store/", StringComparison.Ordinal),
        p =>
        {
            var m = SshLeafRx.Match(p);
            if (!m.Success) return false;
            var leaf = m.Groups[1].Value;
            return leaf is not "config" and not "known_hosts" and not "known_hosts2";
        },
    ];

    private static readonly System.Text.RegularExpressions.Regex KeyFileRe = new(
        @"\.(pem|p12|pfx|key|keystore|jks|kdbx)$",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string StripAt(string token) => token.StartsWith('@') ? token[1..] : token;

    private static string TokenPath(string token)
    {
        var t = token;
        var flag = FlagFileRx.Match(t);
        if (flag.Success)
            t = flag.Groups[1].Value;
        var form = FormFileRx.Match(t);
        if (form.Success)
            t = form.Groups[1].Value;
        return StripAt(t);
    }

    private static readonly System.Text.RegularExpressions.Regex FlagFileRx = new(
        @"^--?[A-Za-z][\w-]*=(.+)$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex FormFileRx = new(
        @"^[^=@\s]*=@(.+)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool IsSecretPathToken(string token)
    {
        var t = TokenPath(token).ToLowerInvariant();
        if (!t.Contains('/')) return false;
        return SecretPathRules.Any(f => f(t)) || KeyFileRe.IsMatch(t);
    }

    public static bool IsLocalCredentialToken(string token, string cwd, PolicyContext ctx)
    {
        var raw = TokenPath(token);
        var t = raw.ToLowerInvariant();
        if (SecretPathRules.Any(f => f(t))) return true;
        if (!KeyFileRe.IsMatch(t)) return false;
        if (UnresolvedRx.IsMatch(raw)) return true;
        var abs = PathClassifier.ExpandPath(raw, cwd);
        return PathClassifier.UnderAny([ctx.WorkRoot, ctx.ArtifactDir], abs) is null;
    }

    private static readonly System.Text.RegularExpressions.Regex UnresolvedRx = new(
        @"[$*?`]", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex DotEnvRx = new(
        @"(^|/)\.env(\.[\w-]+)?$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex DotEnvTemplateRx = new(
        @"\.env\.(example|sample|template|dist|defaults|schema)$",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static bool IsDotEnvToken(string token)
    {
        var t = token.StartsWith('@') ? token[1..] : token;
        if (!DotEnvRx.IsMatch(t)) return false;
        return !DotEnvTemplateRx.IsMatch(t);
    }

    private static readonly System.Text.RegularExpressions.Regex SecretVarRe = new(
        @"(SECRET|TOKEN|PASSWORD|PASSWD|CREDENTIAL|_KEY|KEY_|APIKEY)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex EnvVarRefRx = new(
        @"\$\{?([A-Za-z_][A-Za-z0-9_]*)\}?",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool ReferencesSecretVar(string token) =>
        EnvVarRefRx.Matches(token).Cast<System.Text.RegularExpressions.Match>()
            .Any(m => SecretVarRe.IsMatch(m.Groups[1].Value));

    public static readonly HashSet<string> ReadVerbs = new(StringComparer.Ordinal)
    {
        "cat", "less", "more", "head", "tail", "wc", "sort", "uniq", "cut", "tr", "column", "nl",
        "rev", "grep", "egrep", "fgrep", "rg", "ag", "sed", "awk", "jq", "yq", "ls", "find", "file",
        "stat", "tree", "basename", "dirname", "realpath", "readlink", "diff", "cmp", "md5",
        "md5sum", "shasum", "sha256sum", "cksum", "strings", "xxd", "od", "hexdump", "base64",
        "pwd", "cd", "echo", "printf", "date", "cal", "which", "type", "whoami", "id", "groups",
        "hostname", "uname", "sw_vers", "arch", "env", "printenv", "export", "set", "df", "du",
        "free", "uptime", "ps", "top", "vm_stat", "sleep", "seq", "true", "false", "test", "man",
        "help", "lsof", "netstat", "ifconfig", "ping", "ping6", "dig", "nslookup", "host",
        "traceroute", "nc", "ncat", "curl-config", "git-lfs", "wait", "history", "tput", "clear",
        "watch", "xattr", "sysctl", "caller",
    };

    private static readonly HashSet<string> WriteAllArgs = new(StringComparer.Ordinal)
    {
        "rm", "rmdir", "unlink", "truncate", "shred", "mkdir", "touch", "chmod", "chown", "chgrp",
        "chflags", "tee",
    };

    private static readonly HashSet<string> WriteLastArg = new(StringComparer.Ordinal)
        { "cp", "mv", "ln", "install", "rsync", "scp" };

    private static readonly HashSet<string> ArchiveVerbs = new(StringComparer.Ordinal)
        { "tar", "zip", "gzip", "bzip2", "xz" };

    public static readonly HashSet<string> MutatingFsVerbs = new(StringComparer.Ordinal);
    static PolicyLogic()
    {
        MutatingFsVerbs.UnionWith(WriteAllArgs);
        MutatingFsVerbs.UnionWith(WriteLastArg);
        MutatingFsVerbs.UnionWith(ArchiveVerbs);
        MutatingFsVerbs.Add("dd");
        MutatingFsVerbs.Add("sed");
    }

    public static readonly HashSet<string> NetworkSenders = new(StringComparer.Ordinal)
    {
        "curl", "wget", "http", "httpie", "xh", "nc", "ncat", "socat", "telnet", "ftp", "scp",
        "sftp", "rsync", "ssh", "mosh", "pbcopy", "mail", "mailx", "sendmail", "osascript",
    };

    private static readonly HashSet<string> EnvDumpVerbs = new(StringComparer.Ordinal)
        { "env", "printenv", "set" };

    private static bool DumpsEnvironment(ShellCommand cmd)
    {
        var tokens = cmd.ArgvRaw.Count > 0 ? cmd.ArgvRaw : cmd.Argv;
        foreach (var verb in new[] { cmd.Verb, cmd.VerbRaw })
        {
            if (!EnvDumpVerbs.Contains(verb)) continue;
            if (verb == "set" && tokens.Count != 1) continue;
            var args = ShellSegmenter.NonFlagArgs(tokens);
            if (args.Count == 0) return true;
            if (args.Any(a => SecretVarRe.IsMatch(a))) return true;
        }

        return false;
    }

    public static readonly string[] DefaultProtectedBranches =
        ["main", "master", "develop", "dev", "prod", "production", "qa", "release"];

    private static readonly HashSet<string> GitReadSubs = new(StringComparer.Ordinal)
    {
        "status", "log", "diff", "show", "branch", "fetch", "rev-parse", "rev-list", "describe",
        "blame", "shortlog", "ls-files", "ls-remote", "ls-tree", "cat-file", "remote", "reflog",
        "grep", "whatchanged", "count-objects", "verify-commit", "version", "help", "check-ignore",
        "diff-tree", "symbolic-ref", "for-each-ref",
    };

    private static readonly HashSet<string> GitWriteSubs = new(StringComparer.Ordinal)
    {
        "add", "commit", "checkout", "switch", "restore", "tag", "apply", "am", "mv", "rm", "reset",
        "clean", "init", "worktree", "notes", "update-index", "update-ref", "gc", "config", "lfs",
        "bisect",
    };

    private static readonly HashSet<string> GitHistorySubs = new(StringComparer.Ordinal)
    {
        "rebase", "cherry-pick", "revert", "stash", "merge", "pull", "filter-branch", "replace",
        "fast-import",
    };

    public static readonly string[] AllowVerbs =
    [
        "git-status", "git-log", "git-diff", "git-show", "git-branch", "git-fetch", "git-rev-parse",
        "git-add", "git-commit", "git-checkout", "git-switch", "git-restore", "git-tag", "git-apply",
        "git-mv", "git-rm", "git-reset", "git-clean", "pnpm-test", "pnpm-build", "pnpm-lint",
        "pnpm-install", "npm-test", "npm-ci", "make-lint", "make-test", "make-build",
        "uv-run-pytest", "uv-run-ruff", "uv-run-mypy", "uv-sync", "gh-pr-create", "gh-pr-view",
        "gh-pr-list", "gh-run-view",
    ];

    private static readonly HashSet<string> PiDockerRead = new(StringComparer.Ordinal)
    {
        "ps", "logs", "inspect", "stats", "images", "version", "info", "port", "diff", "history",
        "events",
    };

    private static readonly HashSet<string> PiSystemctlRead = new(StringComparer.Ordinal)
    {
        "status", "list-units", "list-unit-files", "list-timers", "is-active", "is-enabled",
        "is-failed", "show", "cat",
    };

    private static readonly HashSet<string> PiPlainRead = new(StringComparer.Ordinal)
    {
        "journalctl", "df", "du", "free", "uptime", "cat", "tail", "head", "less", "more", "ls",
        "ps", "grep", "wc", "echo", "hostname", "uname", "id", "whoami", "date", "pwd", "stat",
        "file", "which", "sort", "uniq", "vcgencmd", "lsblk", "true",
    };

    private static bool IsPiReadSegment(IReadOnlyList<string> argv, IReadOnlyList<string> redirects)
    {
        if (redirects.Count > 0) return false;
        var verb = (argv.Count > 0 ? argv[0] : "").ToLowerInvariant();
        if (verb == "docker")
        {
            var parts = ShellSegmenter.DockerArgs(argv);
            if (parts.Count > 0 && parts[0] == "compose")
                return parts.Count > 1 && parts[1] is "ps" or "logs" or "config" or "top";
            return PiDockerRead.Contains(parts.Count > 0 ? parts[0] : "");
        }

        if (verb == "systemctl")
            return PiSystemctlRead.Contains((ShellSegmenter.NonFlagArgs(argv).Count > 0
                ? ShellSegmenter.NonFlagArgs(argv)[0]
                : "").ToLowerInvariant());
        if (verb == "tailscale")
            return (argv.Count > 1 ? argv[1] : "").ToLowerInvariant() is "status" or "ip" or "version" or "netcheck" or "whois";
        if (verb == "top")
            return argv.Skip(1).Any(a => TopBatchRx.IsMatch(a));
        if (verb == "journalctl")
            return !argv.Any(a => a.StartsWith("--vacuum", StringComparison.Ordinal) || a == "--rotate");
        if (verb == "find")
            return !argv.Any(a => a == "-delete" || a.StartsWith("-exec", StringComparison.Ordinal) || a == "-ok");
        return PiPlainRead.Contains(verb);
    }

    private static readonly System.Text.RegularExpressions.Regex TopBatchRx = new(
        @"^-[a-z]*b", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private sealed record PiTarget(string Kind, string Host);

    private static PiTarget? PiTargetOf(ShellCommand cmd)
    {
        var verb = cmd.Verb;
        var argv = cmd.Argv;

        if (verb is "ssh" or "mosh" or "sftp")
        {
            var inv = ShellSegmenter.ParseSshInvocation(argv);
            var hosts = new[] { inv.Host }.Concat(inv.JumpHosts).Where(h => h is not null).Cast<string>();
            var hit = hosts.FirstOrDefault(IsPiHost);
            if (hit is not null)
                return new PiTarget(verb == "sftp" ? "copy" : "ssh", hit);
        }

        if (verb == "tailscale" && (argv.Count > 1 ? argv[1] : "").ToLowerInvariant() == "ssh")
        {
            var inv = ShellSegmenter.ParseSshInvocation(argv);
            if (inv.Host is not null && IsPiHost(inv.Host))
                return new PiTarget("ssh", inv.Host);
        }

        if (verb is "scp" or "rsync")
        {
            var hit = ShellSegmenter.RemoteSpecsOf(argv).FirstOrDefault(s => IsPiHost(s.Host));
            if (hit is not null)
                return new PiTarget("copy", hit.Host);
        }

        if (verb == "docker")
        {
            foreach (var f in new[] { "--context", "-c" })
            {
                var i = IndexOf(argv, f);
                var v = i >= 0 && i + 1 < argv.Count ? argv[i + 1] : null;
                if (v is not null && IsPiHost(v))
                    return new PiTarget("docker", v);
            }

            cmd.EnvAssigns.TryGetValue("DOCKER_HOST", out var dh);
            dh ??= "";
            var m = DockerHostRx.Match(dh);
            if (m.Success && IsPiHost(m.Groups[1].Value))
                return new PiTarget("docker", m.Groups[1].Value);
        }

        if (cmd.EffectiveTool.StartsWith("dpi-", StringComparison.Ordinal))
            return new PiTarget("dpi", "pi");
        if (HttpVerbs.Contains(verb))
        {
            var hit = UrlHosts(argv).FirstOrDefault(IsPiHost);
            if (hit is not null)
                return new PiTarget("http", hit);
        }

        return null;
    }

    private static readonly System.Text.RegularExpressions.Regex DockerHostRx = new(
        @"^(?:ssh|tcp):\/\/(.+)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly HashSet<string> DpiReadVerbs = new(StringComparer.Ordinal)
        { "ps", "logs", "stats", "images", "inspect", "status", "df", "top", "ls" };

    private const string PiDenyReason = "Comando que modifica el Raspberry Pi (prohibido: solo lecturas)";

    public static PolicyFinding? RulePi(ShellCommand cmd)
    {
        var target = PiTargetOf(cmd);
        if (target is null) return null;

        if (target.Kind == "dpi")
        {
            var sub = cmd.EffectiveTool.Length > 4 ? cmd.EffectiveTool[4..] : "";
            if (sub == "ship")
                return Esc("deploy", "dpi ship: publica una imagen al Pi");
            if (DpiReadVerbs.Contains(sub))
                return Ok("pi_read", "Lectura del Pi vía dpi");
            return Deny("pi_mutation", PiDenyReason);
        }

        if (target.Kind == "docker")
            return IsPiReadSegment(cmd.Argv, cmd.Redirects)
                ? Ok("pi_read", "Lectura de Docker en el Pi")
                : Deny("pi_mutation", PiDenyReason);

        if (target.Kind == "http")
            return IsMutatingHttp(cmd.Argv)
                ? Deny("pi_mutation", "HTTP no-GET contra el Pi (prohibido)")
                : Ok("pi_read", "GET HTTP al Pi");

        if (target.Kind == "copy")
        {
            if (cmd.Verb == "rsync" && cmd.Argv.Contains("--delete"))
                return Deny("pi_mutation", "rsync --delete contra el Pi");
            var specs = ShellSegmenter.RemoteSpecsOf(cmd.Argv);
            var args = ShellSegmenter.NonFlagArgs(cmd.Argv);
            var last = args.Count > 0 ? args[^1] : "";
            var uploadsToPi = specs.Any(s => IsPiHost(s.Host) && s.Raw == last);
            if (cmd.Verb == "sftp" || uploadsToPi)
                return Deny("pi_mutation", "Escritura de archivos en el Pi");
            return Ok("pi_read", "Copia de lectura desde el Pi");
        }

        var inv = ShellSegmenter.ParseSshInvocation(cmd.Argv);
        if (inv.ReverseForward)
            return Deny("exfiltration", "Túnel inverso SSH (-R) prohibido");
        if (string.IsNullOrWhiteSpace(inv.Remote))
            return Ok("pi_read", "Sesión SSH al Pi sin comando");

        var remote = ShellSegmenter.SplitCommandLine(inv.Remote);
        if (remote.Segments.Count == 0 || remote.Unparseable.Count > 0)
            return Deny("pi_mutation", "Comando remoto no analizable en el Pi");
        foreach (var seg in remote.Segments)
        {
            if (!IsPiReadSegment(seg.Argv, seg.Redirects))
                return Deny("pi_mutation", PiDenyReason);
        }

        return Ok("pi_read", "Lectura del Raspberry Pi");
    }

    private static readonly HashSet<string> LaunchctlWrite = new(StringComparer.Ordinal)
    {
        "load", "unload", "bootstrap", "bootout", "enable", "disable", "kickstart", "remove",
        "submit", "setenv", "unsetenv", "start", "stop",
    };

    private static readonly System.Text.RegularExpressions.Regex HostProcessTokens = new(
        @"(burbuja-host|burbuja\.host|launchd|\$ppid|\$\$)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static PolicyFinding? RuleGateEvasion(ShellCommand cmd)
    {
        var verb = cmd.Verb;
        var argv = cmd.Argv;

        if (cmd.Wrapper == "sudo")
            return Deny("gate_evasion", "sudo no está permitido");
        if (verb == "claude" || verb.EndsWith("/claude", StringComparison.Ordinal))
            return Deny("gate_evasion", "No se permite invocar otro agente claude");
        if (argv.Contains("--dangerously-skip-permissions"))
            return Deny("gate_evasion", "--dangerously-skip-permissions prohibido");
        if (verb is "csrutil" or "spctl")
            return Deny("gate_evasion", "Modificación de la seguridad del sistema");
        if (verb == "launchctl")
        {
            var sub = (ShellSegmenter.NonFlagArgs(argv).Count > 0 ? ShellSegmenter.NonFlagArgs(argv)[0] : "").ToLowerInvariant();
            return LaunchctlWrite.Contains(sub) || sub.Length == 0
                ? Deny("gate_evasion", "launchctl no está permitido")
                : Gray("host_read", "Lectura de launchd");
        }

        if (verb == "defaults")
        {
            var sub = (argv.Count > 1 ? argv[1] : "").ToLowerInvariant();
            if (sub is "write" or "delete" or "import" or "rename")
                return Deny("gate_evasion", "defaults write no está permitido");
            return Gray("host_read", "Lectura de defaults");
        }

        if (verb == "crontab")
        {
            if (argv.Any(a => a is "-e" or "-r") || ShellSegmenter.NonFlagArgs(argv).Count > 0)
                return Deny("gate_evasion", "Modificación de crontab prohibida");
            return Ok("read", "crontab -l");
        }

        if (verb is "kill" or "pkill" or "killall")
        {
            if (argv.Skip(1).Any(a => HostProcessTokens.IsMatch(a)))
                return Deny("gate_evasion", "No se permite matar el proceso del host");
            return Gray("process_kill", "Terminar procesos locales");
        }

        if (verb == "bcore" || verb.EndsWith("/bcore", StringComparison.Ordinal))
            return Deny("gate_evasion", "bcore is reserved");
        if (verb == "ns" || verb.EndsWith("/ns", StringComparison.Ordinal) || verb.EndsWith("/ns.js", StringComparison.Ordinal))
            return Deny("gate_evasion", "El CLI del host (ns) no se usa desde un job");
        if (argv.Any(a => NsJsRx.IsMatch(a)))
            return Deny("gate_evasion", "No se ejecuta el CLI del host desde un job");
        if (argv.Concat(cmd.Redirects).Any(a => HostSockRx.IsMatch(a)))
            return Deny("gate_evasion", "El socket de control del host está fuera de alcance");
        if (verb is "nc" or "ncat" or "netcat" or "socat"
            && argv.Any(a => a is "-U" or "--unixsock" || a.StartsWith("UNIX", StringComparison.Ordinal)))
            return Deny("gate_evasion", "No se habla por sockets unix locales");

        if (verb == "chmod" && argv.Contains("+x"))
        {
            var downloaded = cmd.PriorVerbs.Any(v => v is "curl" or "wget" or "ftp" or "scp");
            if (downloaded)
                return Deny("gate_evasion", "chmod +x sobre un archivo descargado");
        }

        return null;
    }

    private static readonly System.Text.RegularExpressions.Regex NsJsRx = new(
        @"(^|/)ns\.js$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex HostSockRx = new(
        @"burbuja[/\\]host\.sock|host\.sock$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static PolicyFinding? RuleSecretPaths(ShellCommand cmd, PolicyContext ctx)
    {
        var tokens = cmd.Argv.Concat(cmd.Redirects);
        if (tokens.Any(t => IsLocalCredentialToken(t, cmd.Cwd, ctx)))
            return Deny("secrets_read", "Lectura de credenciales locales prohibida");

        if (cmd.Verb == "security")
        {
            var args = ShellSegmenter.NonFlagArgs(cmd.Argv);
            var sub = (args.Count > 0 ? args[0] : "").ToLowerInvariant();
            if (sub.StartsWith("find-", StringComparison.Ordinal) || sub is "dump-keychain" or "export")
                return Deny("secrets_read", "Lectura del llavero de macOS prohibida");
            return Gray("host_read", "Comando security de macOS");
        }

        if (cmd.EffectiveTool == "gh-auth-token")
            return Deny("secrets_read", "gh auth token expone un credencial");
        if (cmd.Verb == "op" && cmd.Argv.Count > 1 && cmd.Argv[1] == "read")
            return Deny("secrets_read", "Lectura de 1Password prohibida");
        if (cmd.Verb == "vault" && cmd.Argv.Count > 1 && cmd.Argv[1] is "read" or "kv")
            return Deny("secrets_read", "Lectura de Vault prohibida");
        if (cmd.EffectiveTool == "git-credential")
            return Deny("secrets_read", "git credential expone credenciales");
        if (cmd.Verb.StartsWith("git-credential-", StringComparison.Ordinal))
            return Deny("secrets_read", "git credential helper expone credenciales");
        return null;
    }

    private static readonly string[] AwsReadPrefixes =
        ["describe-", "list-", "get-", "batch-get-", "search-", "lookup-", "select-"];
    private static readonly HashSet<string> AwsReadOps = new(StringComparer.Ordinal)
    {
        "ls", "tail", "help", "presign", "wait", "filter-log-events", "start-live-tail", "current",
        "history",
    };
    private static readonly string[] AwsWritePrefixes =
    [
        "create-", "update-", "delete-", "put-", "start-", "stop-", "terminate-", "modify-",
        "invoke", "run-", "execute-", "attach-", "detach-", "add-", "remove-", "register-",
        "deregister-", "set-", "enable-", "disable-", "cancel-", "reboot-", "restore-", "copy-",
        "import-", "publish", "send-", "tag-", "untag-", "associate-", "disassociate-", "deploy",
    ];

    private static string? OptValue(IReadOnlyList<string> argv, params string[] names)
    {
        for (var i = 1; i < argv.Count; i++)
        {
            var t = argv[i];
            if (names.Contains(t))
                return i + 1 < argv.Count ? argv[i + 1] : null;
            var eq = t.IndexOf('=');
            if (eq > 0 && names.Contains(t[..eq]))
                return t[(eq + 1)..];
        }

        return null;
    }

    private static bool IdAllowed(string? id, IReadOnlyList<string> allowed)
    {
        if (id is null) return false;
        return allowed.Any(a => id == a || id.StartsWith(a, StringComparison.Ordinal));
    }

    public static PolicyFinding? RuleAws(ShellCommand cmd, PolicyContext ctx)
    {
        if (cmd.Verb != "aws") return null;
        var parts = ShellSegmenter.AwsArgs(cmd.Argv);
        var service = parts.Count > 0 ? parts[0] : "";
        var op = parts.Count > 1 ? parts[1] : "";
        var allowed = ctx.AllowedSecretIds;

        if (service == "sts" && op is "get-session-token" or "get-federation-token")
            return Deny("secrets_read", "aws sts emite credenciales temporales");
        if (service == "configure")
        {
            if (op == "export-credentials")
                return Deny("secrets_read", "aws configure export-credentials");
            if (op == "get" && ConfigureSecretRx.IsMatch(parts.Count > 2 ? parts[2] : ""))
                return Deny("secrets_read", "aws configure get de una credencial");
            return Ok("read", "aws configure (lectura)");
        }

        if (service == "iam" && IamCreateRx.IsMatch(op))
            return Deny("secrets_read", "aws iam crea credenciales de larga vida");

        if (service == "secretsmanager")
        {
            if (op is "get-secret-value" or "batch-get-secret-value" or "describe-secret")
            {
                var id = OptValue(cmd.Argv, "--secret-id", "--secret-ids", "--ids") ?? (parts.Count > 2 ? parts[2] : null);
                return IdAllowed(id, allowed)
                    ? Ok("secrets_read_allowed", "Secreto de dev en la lista permitida")
                    : Esc("secrets_read_other", $"Lectura de un secreto fuera de la lista permitida ({id ?? "sin --secret-id"})");
            }
        }

        if (service == "ssm" && op is "send-command" or "start-session" or "start-automation-execution")
        {
            var target = OptValue(cmd.Argv, "--instance-ids", "--target", "--targets") ?? "(sin instancia)";
            return Esc("remote_exec", $"Comando remoto vía SSM en {target}");
        }

        if (service == "ssm" && SsmGetParamRx.IsMatch(op))
        {
            var decrypt = cmd.Argv.Any(a => a is "--with-decryption" or "--with-decryption=true");
            if (!decrypt) return Ok("read", "aws ssm (sin descifrar)");
            var id = OptValue(cmd.Argv, "--name", "--names", "--path") ?? (parts.Count > 2 ? parts[2] : null);
            return IdAllowed(id, allowed)
                ? Ok("secrets_read_allowed", "Parámetro de dev en la lista permitida")
                : Esc("secrets_read_other", $"Lectura descifrada de un parámetro fuera de la lista permitida ({id ?? "sin --name"})");
        }

        if (service is "s3" or "s3api")
        {
            if (op is "ls" or "presign" or "website" || op.StartsWith("get-", StringComparison.Ordinal)
                || op.StartsWith("list-", StringComparison.Ordinal) || op.StartsWith("head-", StringComparison.Ordinal))
                return Ok("read", "Lectura de S3");
            return Esc("write_prod", $"Escritura en S3 ({op})");
        }

        if (service == "logs")
        {
            var readVerb = op.StartsWith("start-live-tail", StringComparison.Ordinal) || op is "start-query" or "stop-query" or "get-query-results";
            return AwsWritePrefixes.Any(p => op.StartsWith(p, StringComparison.Ordinal)) && !readVerb
                ? Esc("write_prod", $"Escritura en CloudWatch Logs ({op})")
                : Ok("read", "Lectura de CloudWatch Logs");
        }

        if (AwsReadOps.Contains(op) || AwsReadPrefixes.Any(p => op.StartsWith(p, StringComparison.Ordinal)))
            return Ok("read", "Lectura de AWS");
        if (AwsWritePrefixes.Any(p => op.StartsWith(p, StringComparison.Ordinal)))
            return Esc("write_prod", $"Operación de escritura en AWS ({service} {op})");
        return Esc("write_prod", $"Operación AWS no clasificada ({service} {op})");
    }

    private static readonly System.Text.RegularExpressions.Regex ConfigureSecretRx = new(
        @"^(aws_secret_access_key|aws_session_token|aws_access_key_id)$",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex IamCreateRx = new(
        @"^create-(access-key|login-profile|service-specific-credential)$",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex SsmGetParamRx = new(
        @"^get-parameters?(-by-path)?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static PolicyFinding? RuleAwsProfile(ShellCommand cmd, PolicyContext ctx)
    {
        if (cmd.Verb != "aws") return null;
        var profile = OptValue(cmd.Argv, "--profile");
        if (profile is null) return null;
        var allowed = ctx.AllowedAwsProfiles ?? [];
        if (allowed.Count > 0 && allowed.Any(a => a.Equals(profile, StringComparison.OrdinalIgnoreCase)))
            return null;
        return Esc("aws_profile", $"AWS named profile '{profile}' is not allowlisted");
    }

    private static IReadOnlyList<string> PushRefspecs(IReadOnlyList<string> argv)
    {
        var args = ShellSegmenter.NonFlagArgs(argv, new HashSet<string>(StringComparer.Ordinal)
            { "-c", "-C", "--repo", "-o", "--push-option" });
        return args.Count > 2 ? args.Skip(2).ToList() : [];
    }

    private static (string Dst, bool Deletion, bool Force) RefspecDestination(string spec)
    {
        var s = spec;
        var force = false;
        if (s.StartsWith('+'))
        {
            force = true;
            s = s[1..];
        }

        var parts = s.Split(':');
        if (parts.Length > 1)
        {
            var dst = parts[1].StartsWith("refs/heads/", StringComparison.Ordinal)
                ? parts[1]["refs/heads/".Length..]
                : parts[1];
            return (dst, parts[0].Length == 0, force);
        }

        var dst2 = s.StartsWith("refs/heads/", StringComparison.Ordinal) ? s["refs/heads/".Length..] : s;
        return (dst2, false, force);
    }

    public static PolicyFinding? RuleGit(ShellCommand cmd, PolicyContext ctx)
    {
        if (cmd.Verb != "git") return null;
        var argv = cmd.Argv;
        var sub = ShellSegmenter.GitSubcommand(argv);

        var dashCValues = new List<string>();
        for (var i = 1; i < argv.Count; i++)
        {
            if (argv[i] == "-c" && i + 1 < argv.Count)
                dashCValues.Add(argv[i + 1].ToLowerInvariant());
        }

        bool Evasive(string v) => GitEvasiveRx.IsMatch(v);
        if (dashCValues.Any(Evasive))
            return Esc("gate_evasion", "git -c reconfigura hooks o aliases");
        if (sub == "config")
        {
            var cfgArgs = ShellSegmenter.NonFlagArgs(argv, new HashSet<string>(StringComparer.Ordinal)
                    { "-c", "-C", "--get", "--get-all" })
                .Skip(1).Select(a => a.ToLowerInvariant()).ToList();
            if (cfgArgs.Any(Evasive))
                return Esc("gate_evasion", "git config toca hooks o aliases");
            return Ok("read", "git config");
        }

        if (sub == "credential")
            return Deny("secrets_read", "git credential expone credenciales");

        if (sub == "push")
        {
            var protectedBranches = new HashSet<string>(
                DefaultProtectedBranches.Select(b => b.ToLowerInvariant()), StringComparer.Ordinal);
            var prefixes = new[] { "agent/" };
            var flags = argv.Where(a => a.StartsWith('-')).ToList();
            var forceFlag = flags.Any(f => ForceFlagRx.IsMatch(f));
            var deleteFlag = flags.Any(f => f is "-d" or "--delete");
            if (flags.Contains("--mirror"))
                return Deny("protected_push", "git push --mirror sobre ramas protegidas");

            var specs = PushRefspecs(argv);
            if (specs.Count == 0)
            {
                if (ctx.Branch is not null && !forceFlag && !deleteFlag
                    && prefixes.Any(pfx => ctx.Branch.StartsWith(pfx, StringComparison.Ordinal)))
                    return Ok("worktree_write", $"git push de la rama del job ({ctx.Branch})");
                return Esc("protected_push", "git push sin refspec: no se puede verificar la rama destino");
            }

            PolicyFinding? worst = null;
            foreach (var spec in specs)
            {
                var (dst, deletion, force) = RefspecDestination(spec);
                var isProtected = protectedBranches.Contains(dst.ToLowerInvariant());
                var del = deletion || deleteFlag;
                if (isProtected)
                    worst = Worse(worst, Deny("protected_push", $"Push a la rama protegida {dst}"));
                else if (del)
                    worst = Worse(worst, Esc("protected_push", $"Borrado de la rama {dst}"));
                else if (prefixes.Any(p => dst.StartsWith(p, StringComparison.Ordinal)))
                    worst = Worse(worst, Ok("worktree_write", $"Push a {dst}{(force || forceFlag ? " (forzado)" : "")}"));
                else
                    worst = Worse(worst, Esc("protected_push", $"Push a {dst}: prefijo no permitido"));
            }

            return worst;
        }

        if (GitReadSubs.Contains(sub)) return Ok("read", $"git {sub}");
        if (sub == "stash")
        {
            var what = (ShellSegmenter.NonFlagArgs(argv).Count > 1 ? ShellSegmenter.NonFlagArgs(argv)[1] : "").ToLowerInvariant();
            if (what is "list" or "show" or "")
                return Ok("read", $"git stash {what}");
        }

        if (GitHistorySubs.Contains(sub))
            return Gray("git_history", $"Reescritura de historial: git {sub}");
        if (GitWriteSubs.Contains(sub))
            return Ok("worktree_write", $"git {sub}");
        return Gray("git_other", $"Subcomando git no clasificado: {(sub.Length > 0 ? sub : "(ninguno)")}");
    }

    private static readonly System.Text.RegularExpressions.Regex GitEvasiveRx = new(
        @"^(core\.hookspath|alias\.|core\.fsmonitor|diff\.external|diff\.[^.]+\.(textconv|command)|core\.pager|core\.sshcommand|credential\.helper|core\.editor|filter\.|merge\.[^.]+\.driver|url\.|include\.|includeif\.)",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex ForceFlagRx = new(
        @"^(-f|--force|--force-with-lease(=.*)?|--force-if-includes)$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public static PolicyFinding? RuleHttp(ShellCommand cmd, PolicyContext ctx)
    {
        if (!HttpVerbs.Contains(cmd.Verb)) return null;
        var hosts = UrlHosts(cmd.Argv);
        if (hosts.Count == 0) return null;
        var mutating = IsMutatingHttp(cmd.Argv);
        PolicyFinding? worst = null;
        foreach (var host in hosts)
        {
            if (IsPiHost(host))
            {
                worst = Worse(worst, mutating
                    ? Deny("pi_mutation", "HTTP no-GET contra el Pi")
                    : Ok("pi_read", "GET HTTP al Pi"));
                continue;
            }

            if (IsLocalHost(host) || MatchesAnyHost(host, ctx.DevHosts))
            {
                worst = Worse(worst, Ok("http_dev", $"HTTP a un host conocido ({host})"));
                continue;
            }

            worst = Worse(worst, mutating
                ? Esc("external_write", $"Escritura HTTP a {host}")
                : Gray("http_get", $"GET HTTP a un host desconocido ({host})"));
        }

        return worst;
    }

    private static readonly HashSet<string> GhAllow = new(StringComparer.Ordinal)
    {
        "gh-pr-view", "gh-pr-list", "gh-pr-checks", "gh-pr-diff", "gh-pr-status", "gh-pr-create",
        "gh-run-view", "gh-run-list", "gh-run-watch", "gh-issue-view", "gh-issue-list",
        "gh-repo-view", "gh-release-list", "gh-release-view", "gh-workflow-list", "gh-workflow-view",
        "gh-auth-status", "gh-search-prs", "gh-search-issues", "gh-label-list", "gh-cache-list",
    };

    private static readonly HashSet<string> GhDeploy = new(StringComparer.Ordinal)
    {
        "gh-workflow-run", "gh-workflow-enable", "gh-workflow-disable", "gh-release-create",
        "gh-release-upload", "gh-release-delete",
    };

    private static readonly System.Text.RegularExpressions.Regex GhExternalWrite = new(
        @"^gh-(issue|pr|repo|secret|variable|gist|label|api)-",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public static PolicyFinding? RuleGh(ShellCommand cmd, PolicyContext ctx)
    {
        if (cmd.Verb != "gh") return null;
        var tool = cmd.EffectiveTool;
        var parts = ShellSegmenter.GhArgs(cmd.Argv);

        if (tool == "gh-api")
        {
            if (!IsMutatingHttp(cmd.Argv)) return Ok("read", "gh api (GET)");
            return Esc("external_write", "gh api con método de escritura");
        }

        if (tool == "gh-pr-merge") return Esc("merge", "Fusión de pull request");
        if (tool == "gh-pr-comment")
            return Esc("external_write", "Comentario en un pull request");
        if (GhAllow.Contains(tool)) return Ok("read", $"gh {string.Join(' ', parts)}");
        if (GhDeploy.Contains(tool)) return Esc("deploy", $"gh {string.Join(' ', parts)}");
        if (GhExternalWrite.IsMatch(tool))
            return Esc("external_write", $"gh {string.Join(' ', parts)} escribe en GitHub");
        _ = ctx;
        return Gray("gh_other", $"Comando gh no clasificado: {string.Join(' ', parts)}");
    }

    private static readonly HashSet<string> DockerMutating = new(StringComparer.Ordinal)
    {
        "rm", "rmi", "kill", "stop", "start", "restart", "pause", "unpause", "prune", "update",
        "rename", "commit", "load", "import", "tag", "create",
    };

    public static PolicyFinding? RuleDocker(ShellCommand cmd)
    {
        if (cmd.Verb != "docker") return null;
        if (PiTargetOf(cmd) is not null) return null;
        var parts = ShellSegmenter.DockerArgs(cmd.Argv);
        var sub = parts.Count > 0 ? parts[0] : "";
        var sub2 = parts.Count > 1 ? parts[1] : "";

        if (sub == "push") return Esc("deploy", "docker push");
        if (sub == "compose")
        {
            if (sub2 is "up" or "down" or "stop" or "restart" or "rm" or "kill" or "pull")
                return Esc("deploy", $"docker compose {sub2}");
            return Gray("docker_local", $"docker compose {sub2}");
        }

        if (sub is "system" or "image" or "container" or "volume" or "network" or "builder")
        {
            if (sub2 is "prune" or "rm" or "rmi" or "remove")
                return Esc("destructive_local", $"docker {sub} {sub2}");
            return Gray("docker_local", $"docker {sub} {sub2}");
        }

        if (DockerMutating.Contains(sub))
            return Esc("destructive_local", $"docker {sub}");
        if (sub is "run" or "exec" or "create")
        {
            var target = parts.Count > 1 ? parts[1] : null;
            var stop = target is not null ? IndexOf(cmd.Argv, target) : -1;
            var flags = cmd.Argv.Skip(1).Take(stop == -1 ? cmd.Argv.Count : Math.Max(0, stop - 1)).ToList();
            var escapes = flags.Any(a =>
                a is "--privileged" or "--pid=host" or "--network=host" or "--userns=host"
                || DockerCapAddRx.IsMatch(a) || a is "-v" or "--volume" or "--mount"
                || DockerVolumeEqRx.IsMatch(a) || DockerBindRx.IsMatch(a));
            if (escapes)
                return Esc("destructive_local", $"docker {sub} con acceso al host (montaje o privilegios)");
        }

        return Gray("docker_local", $"docker {sub}");
    }

    private static readonly System.Text.RegularExpressions.Regex DockerCapAddRx = new(
        @"^--cap-add(=|$)", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex DockerVolumeEqRx = new(
        @"^(-v|--volume)=", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex DockerBindRx = new(
        @"^(--mount=)?type=bind", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly Dictionary<string, string[]> InfraApply = new(StringComparer.Ordinal)
    {
        ["terraform"] = ["apply", "destroy", "import", "taint", "untaint", "force-unlock"],
        ["terragrunt"] = ["apply", "destroy"],
        ["cdk"] = ["deploy", "destroy", "import"],
        ["cdktf"] = ["deploy", "destroy"],
        ["pulumi"] = ["up", "destroy", "refresh", "import"],
        ["serverless"] = ["deploy", "remove"],
        ["sls"] = ["deploy", "remove"],
        ["helm"] = ["install", "upgrade", "uninstall", "rollback"],
        ["flyctl"] = ["deploy"],
        ["fly"] = ["deploy"],
        ["eb"] = ["deploy"],
        ["sam"] = ["deploy"],
        ["ansible"] = [],
        ["ansible-playbook"] = [],
    };

    private static readonly HashSet<string> KubectlWrite = new(StringComparer.Ordinal)
    {
        "apply", "delete", "create", "patch", "replace", "scale", "rollout", "drain", "cordon",
        "uncordon", "taint", "exec", "cp", "edit", "label", "annotate", "set",
    };

    public static PolicyFinding? RuleInfra(ShellCommand cmd)
    {
        var verb = cmd.Verb;
        var nf = ShellSegmenter.NonFlagArgs(cmd.Argv);
        var sub = (nf.Count > 0 ? nf[0] : "").ToLowerInvariant();

        if (verb is "kubectl" or "k9s")
        {
            if (KubectlWrite.Contains(sub))
                return Esc("deploy", $"kubectl {sub} modifica el clúster");
            return Gray("infra_read", $"kubectl {sub}");
        }

        if (verb is "vercel" or "vc")
        {
            if (cmd.Argv.Contains("--prod") || sub is "deploy" or "promote")
                return Esc("deploy", "Despliegue a producción en Vercel");
            return Gray("infra_read", "Comando vercel");
        }

        if (InfraApply.TryGetValue(verb, out var apply))
        {
            if (apply.Contains(sub) || apply.Length == 0)
                return Esc("deploy", $"{verb} {sub} cambia infraestructura");
            return Gray("infra_read", $"{verb} {sub}");
        }

        if (verb == "alembic")
        {
            if (sub == "revision") return Gray("migration", "alembic revision");
            if (sub is "upgrade" or "downgrade" or "stamp" or "merge")
                return Esc("db_migration", $"alembic {sub} aplica migraciones");
            return Gray("migration", $"alembic {sub}");
        }

        if (verb == "prisma" || cmd.EffectiveTool == "npx-prisma")
            return sub == "migrate" || cmd.Argv.Contains("migrate")
                ? Esc("db_migration", "prisma migrate")
                : Gray("migration", "prisma");
        return null;
    }

    private static readonly HashSet<string> DbClients = new(StringComparer.Ordinal)
        { "psql", "mysql", "mariadb", "mongosh", "mongo", "redis-cli", "pgcli", "mycli" };

    public static PolicyFinding? RuleDb(ShellCommand cmd)
    {
        if (cmd.Verb == "sqlite3") return Gray("db_local", "sqlite3");
        if (!DbClients.Contains(cmd.Verb)) return null;
        var host = OptValue(cmd.Argv, "-h", "--host") ?? "";
        if (host.Length == 0 || IsLocalHost(host))
            return Gray("db_local", "Base de datos local");
        return Esc("db_remote", $"Conexión a la base de datos {host}");
    }

    private static readonly HashSet<string> PkgManagers = new(StringComparer.Ordinal)
        { "npm", "pnpm", "yarn", "bun" };

    public static readonly HashSet<string> CheckTools = new(StringComparer.Ordinal)
    {
        "tsc", "vitest", "jest", "mocha", "eslint", "prettier", "tsx", "playwright", "vite",
        "biome", "oxlint", "tsup", "knip", "vue-tsc", "next", "astro", "pytest", "ruff", "mypy",
        "pyright", "black", "isort", "flake8", "pylint",
    };

    private static readonly HashSet<string> PkgInstallSubs = new(StringComparer.Ordinal)
        { "install", "i", "add", "remove", "rm", "uninstall", "up", "update", "upgrade" };

    private static readonly HashSet<string> ScriptAllow = new(StringComparer.Ordinal)
    {
        "test", "tests", "build", "lint", "typecheck", "types", "format", "format:check", "check",
        "ci", "dev", "start", "coverage", "test:watch", "test:unit", "e2e",
    };

    private static readonly System.Text.RegularExpressions.Regex ScriptDeploy = new(
        @"(deploy|publish|release|prod|push)", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static PolicyFinding? RulePackages(ShellCommand cmd)
    {
        var verb = cmd.Verb;
        var argv = cmd.Argv;
        var globalFlag = argv.Any(a => a is "-g" or "--global");

        if (PkgManagers.Contains(verb))
        {
            var nf = ShellSegmenter.NonFlagArgs(argv);
            var sub = (nf.Count > 0 ? nf[0] : "").ToLowerInvariant();
            var rest = nf.Skip(1).ToList();
            if (PkgInstallSubs.Contains(sub))
            {
                if (globalFlag)
                    return Esc("destructive_local", $"{verb} {sub} -g instala globalmente");
                if (rest.Count == 0) return Ok("worktree_write", $"{verb} {sub}");
                return Gray("dep_add", $"{verb} {sub} {string.Join(' ', rest)}");
            }

            if (sub == "ci") return Ok("worktree_write", $"{verb} ci");
            if (sub == "exec")
            {
                var tool = (rest.Count > 0 ? rest[0] : "").ToLowerInvariant();
                if (tool == "playwright" && rest.Count > 1 && rest[1] == "install")
                    return Gray("unknown_command", $"{verb} exec playwright install");
                return CheckTools.Contains(tool)
                    ? Ok("verify", $"{verb} exec {tool} (verificación)")
                    : Gray("unknown_command", $"{verb} exec {tool}");
            }

            if (sub is "dlx" or "x")
                return Gray("unknown_command", $"{verb} {sub}");
            if (sub is "run" or "run-script")
            {
                var script = (rest.Count > 0 ? rest[0] : "").ToLowerInvariant();
                if (ScriptDeploy.IsMatch(script))
                    return Esc("deploy", $"{verb} run {script}");
                return ScriptAllow.Contains(script)
                    ? Ok("worktree_write", $"{verb} run {script}")
                    : Gray("unknown_command", $"{verb} run {script}");
            }

            if (ScriptDeploy.IsMatch(sub)) return Esc("deploy", $"{verb} {sub}");
            if (ScriptAllow.Contains(sub)) return Ok("worktree_write", $"{verb} {sub}");
            return Gray("unknown_command", $"{verb} {sub}");
        }

        if (verb is "pip" or "pip3" || cmd.EffectiveTool.StartsWith("uv-pip", StringComparison.Ordinal))
        {
            var sub = (ShellSegmenter.NonFlagArgs(argv).Count > 0 ? ShellSegmenter.NonFlagArgs(argv)[0] : "").ToLowerInvariant();
            if (sub is "install" or "uninstall")
                return Gray("dep_add", $"{verb} {sub}");
            return Ok("read", $"{verb} {sub}");
        }

        if (verb == "uv")
        {
            var sub = (argv.Count > 1 ? argv[1] : "").ToLowerInvariant();
            if (sub is "add" or "remove") return Gray("dep_add", $"uv {sub}");
            if (sub is "sync" or "lock" or "venv" or "tree" or "version" or "pip")
                return Ok("worktree_write", $"uv {sub}");
            if (sub == "run")
            {
                var positional = ShellSegmenter.NonFlagArgs(argv, UvRunValueFlags, 2);
                var tool = (positional.Count > 0 ? positional[0] : "").ToLowerInvariant();
                var idx = positional.Count > 0 ? IndexOf(argv, positional[0]) : -1;
                var afterTool = idx >= 0 && idx + 1 < argv.Count ? argv[idx + 1] : "";
                if (PythonBinNameRx.IsMatch(tool) && afterTool is "-c" or "-")
                    return Gray("inline_code", $"Código inline con uv run {tool}");
                return tool is "pytest" or "ruff" or "mypy" or "black" or "isort" or "python" or "vitest"
                    ? Ok("worktree_write", $"uv run {tool}")
                    : Gray("unknown_command", $"uv run {tool}");
            }

            return Gray("unknown_command", $"uv {sub}");
        }

        if (verb == "brew")
        {
            var sub = (ShellSegmenter.NonFlagArgs(argv).Count > 0 ? ShellSegmenter.NonFlagArgs(argv)[0] : "").ToLowerInvariant();
            return sub is "install" or "uninstall" or "upgrade" or "reinstall" or "link" or "unlink" or "tap" or "services"
                ? Esc("destructive_local", $"brew {sub} cambia el sistema")
                : Gray("host_read", $"brew {sub}");
        }

        if (verb is "cargo" or "go")
        {
            var sub = (ShellSegmenter.NonFlagArgs(argv).Count > 0 ? ShellSegmenter.NonFlagArgs(argv)[0] : "").ToLowerInvariant();
            if (sub is "install" or "publish")
                return Esc("destructive_local", $"{verb} {sub}");
            if (sub is "get" or "add")
                return Gray("dep_add", $"{verb} {sub}");
            return sub is "build" or "test" or "check" or "fmt" or "clippy" or "vet" or "doc" or "tree"
                or "version" or "mod" or "run" or "bench" or "list" or "env"
                ? Ok("worktree_write", $"{verb} {sub}")
                : Gray("unknown_command", $"{verb} {sub}");
        }

        if (verb == "make")
        {
            var target = (ShellSegmenter.NonFlagArgs(argv).Count > 0 ? ShellSegmenter.NonFlagArgs(argv)[0] : "").ToLowerInvariant();
            if (ScriptDeploy.IsMatch(target)) return Esc("deploy", $"make {target}");
            return ScriptAllow.Contains(target) || target is "clean" or "setup" or "install" or "fmt" or "all" or ""
                ? Ok("worktree_write", $"make {target}")
                : Gray("unknown_command", $"make {target}");
        }

        if (verb is "npx" or "bunx" or "pnpx")
        {
            var positional = ShellSegmenter.NonFlagArgs(argv);
            var tool = (positional.Count > 0 ? positional[0] : "").ToLowerInvariant();
            if (tool == "playwright" && positional.Count > 1 && positional[1] == "install")
                return Gray("unknown_command", $"{verb} playwright install");
            return CheckTools.Contains(tool)
                ? Ok("worktree_write", $"{verb} {tool}")
                : Gray("unknown_command", $"{verb} {tool}");
        }

        if (verb is "tsc" or "vitest" or "jest" or "pytest" or "ruff" or "mypy" or "eslint" or "prettier"
            or "black" or "isort" or "flake8" or "biome")
            return Ok("worktree_write", $"{verb} (verificación)");
        return null;
    }

    private static readonly HashSet<string> UvRunValueFlags = new(StringComparer.Ordinal)
    {
        "--with", "--with-requirements", "--python", "-p", "--directory", "--project", "--group",
        "--only-group", "--extra", "--package", "--env-file",
    };

    private static readonly System.Text.RegularExpressions.Regex PythonBinNameRx = new(
        @"^python\d*(\.\d+)?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly HashSet<string> Interpreters = new(StringComparer.Ordinal)
        { "python", "python3", "node", "perl", "ruby", "deno" };

    public static PolicyFinding? RuleInterpreters(ShellCommand cmd)
    {
        if (!Interpreters.Contains(cmd.Verb)) return null;
        if (cmd.Argv.Any(a => a is "-c" or "-e"))
            return Gray("inline_code", $"Código inline con {cmd.Verb}");
        var mIdx = IndexOf(cmd.Argv, "-m");
        if (mIdx >= 0)
        {
            var mod = (mIdx + 1 < cmd.Argv.Count ? cmd.Argv[mIdx + 1] : "").ToLowerInvariant();
            if (mod is "pytest" or "ruff" or "mypy" or "black" or "isort" or "unittest")
                return Ok("worktree_write", $"{cmd.Verb} -m {mod}");
            if (mod == "pip") return Gray("dep_add", "python -m pip");
            return Gray("unknown_command", $"{cmd.Verb} -m {mod}");
        }

        return Gray("script_run", $"Ejecución de script con {cmd.Verb}");
    }

    private static readonly HashSet<string> LocalDestructive = new(StringComparer.Ordinal)
    {
        "open", "osascript", "say", "softwareupdate", "systemsetup", "networksetup", "dscl",
        "scutil", "pmset", "nvram",
    };

    private static readonly System.Text.RegularExpressions.Regex BrowserApps = new(
        @"^(google chrome|chrome|arc|safari|firefox|microsoft edge|brave browser|brave)$",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static PolicyFinding? RuleLocalDestructive(ShellCommand cmd)
    {
        if (cmd.Verb == "open")
        {
            var app = OptValue(cmd.Argv, "-a");
            var urls = cmd.Argv.Where(a => HttpUrlRx.IsMatch(a)).ToList();
            if (app is not null && BrowserApps.IsMatch(app) && urls.Count > 0)
                return Ok("browser_open", $"Abrir {urls[0]} en {app}");
            if (urls.Count > 0 && app is null)
                return Ok("browser_open", $"Abrir {urls[0]}");
            return Gray("open_app", "open: abrir archivo/app en el Mac");
        }

        if (LocalDestructive.Contains(cmd.Verb))
            return Esc("destructive_local", $"{cmd.Verb} afecta la máquina anfitriona");
        if (cmd.Verb == "tailscale")
        {
            var sub = (cmd.Argv.Count > 1 ? cmd.Argv[1] : "").ToLowerInvariant();
            if (sub == "ssh") return null;
            return sub is "status" or "ip" or "version" or "netcheck" or "whois" or "ping"
                ? Ok("read", $"tailscale {sub}")
                : Esc("destructive_local", $"tailscale {sub} cambia la red del host");
        }

        return null;
    }

    private static readonly System.Text.RegularExpressions.Regex HttpUrlRx = new(
        @"^https?://", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static IReadOnlyList<string> WriteTargetsOf(ShellCommand cmd)
    {
        string Abs(string t) =>
            ShellSegmenter.IsPathLike(t) ? ShellSegmenter.ExpandToken(t, cmd.Cwd) : ShellSegmenter.ExpandToken($"./{t}", cmd.Cwd);

        var output = new List<string>();
        foreach (var r in cmd.Redirects)
        {
            if (r.StartsWith('&')) continue;
            output.Add(Abs(r));
        }

        var verb = cmd.Verb;
        var argv = cmd.Argv;
        if (verb == "sed" && !argv.Any(a => a == "-i" || a.StartsWith("-i", StringComparison.Ordinal)))
            return output;

        if (verb == "dd")
        {
            var of = argv.FirstOrDefault(a => a.StartsWith("of=", StringComparison.Ordinal));
            if (of is not null)
                output.Add(Abs(of[3..]));
            return output;
        }

        if (ArchiveVerbs.Contains(verb))
        {
            var fIdx = -1;
            for (var i = 0; i < argv.Count; i++)
            {
                if (ArchiveFRx.IsMatch(argv[i]))
                {
                    fIdx = i;
                    break;
                }
            }

            if (fIdx >= 0 && fIdx + 1 < argv.Count)
                output.Add(Abs(argv[fIdx + 1]));
            return output;
        }

        var args = ShellSegmenter.NonFlagArgs(argv);
        if (WriteAllArgs.Contains(verb) || verb == "sed")
        {
            foreach (var a in args)
                output.Add(Abs(a));
        }
        else if (WriteLastArg.Contains(verb))
        {
            var last = args.Count > 0 ? args[^1] : null;
            if (last is not null && !RemoteLikeRx.IsMatch(last))
                output.Add(Abs(last));
        }

        return output;
    }

    private static readonly System.Text.RegularExpressions.Regex ArchiveFRx = new(
        @"^-[a-z]*f$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex RemoteLikeRx = new(
        @"^[^/]*:", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex DiskutilWrite = new(
        @"^(erase|partition|reformat|repair|resize|mount|unmount|apfs|split|merge)",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex RecursiveFlag = new(
        @"^-[a-zA-Z]*[rR]", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static PolicyFinding? RuleFsWrites(ShellCommand cmd, PolicyContext ctx)
    {
        var verb = cmd.Verb;
        if (verb.StartsWith("mkfs", StringComparison.Ordinal) || verb is "newfs" or "fdisk")
            return Deny("destructive_fs", "Formateo de disco prohibido");
        if (verb == "diskutil")
        {
            var sub = (ShellSegmenter.NonFlagArgs(cmd.Argv).Count > 0
                ? ShellSegmenter.NonFlagArgs(cmd.Argv)[0]
                : "").ToLowerInvariant();
            return DiskutilWrite.IsMatch(sub)
                ? Deny("destructive_fs", $"diskutil {sub} es destructivo")
                : Gray("host_read", $"diskutil {sub}");
        }

        var isDelete = verb is "rm" or "rmdir" or "unlink" or "shred";
        var recursive = isDelete && cmd.Argv.Any(a => RecursiveFlag.IsMatch(a) || a == "--recursive");
        PolicyFinding? worst = null;
        foreach (var target in WriteTargetsOf(cmd))
        {
            var real = PathClassifier.RealPathLoose(target);
            if (IsDevicePath(target))
                worst = Worse(worst, Deny("destructive_fs", $"Escritura al dispositivo {target}"));
            else if (IsWritableTarget(real, ctx))
                worst = Worse(worst, Ok("worktree_write", "Escritura en el worktree o TMPDIR"));
            else if (IsSelfModTarget(real, ctx))
                worst = Worse(worst, Deny("self_modification", $"Escritura prohibida en {target}"));
            else
                worst = Worse(worst, Deny("destructive_fs", $"Escritura fuera del worktree: {target}"));
        }

        return worst;
    }

    private static readonly HashSet<string> FindMutatingExec = new(StringComparer.Ordinal)
        { "rm", "mv", "cp", "chmod", "chown", "truncate", "shred", "tee", "sed", "unlink", "rmdir" };

    public static PolicyFinding? RuleFind(ShellCommand cmd, PolicyContext ctx)
    {
        if (cmd.Verb != "find") return null;
        var argv = cmd.Argv;
        var roots = new List<string>();
        for (var i = 1; i < argv.Count; i++)
        {
            var t = argv[i];
            if (t.StartsWith('-') || t is "(" or "!") break;
            roots.Add(t);
        }

        var mutates = argv.Contains("-delete");
        var execIdx = -1;
        for (var i = 0; i < argv.Count; i++)
        {
            if (argv[i] is "-exec" or "-execdir" or "-ok" or "-okdir")
            {
                execIdx = i;
                break;
            }
        }

        if (execIdx >= 0)
        {
            var target = ((execIdx + 1 < argv.Count ? argv[execIdx + 1] : "").Split('/').LastOrDefault() ?? "");
            if (FindMutatingExec.Contains(target.ToLowerInvariant()))
                mutates = true;
        }

        if (!mutates) return Ok("read", "find (búsqueda)");

        var resolved = (roots.Count > 0 ? roots : ["."]).Select(r =>
            PathClassifier.RealPathLoose(ShellSegmenter.IsPathLike(r)
                ? ShellSegmenter.ExpandToken(r, cmd.Cwd)
                : ShellSegmenter.ExpandToken($"./{r}", cmd.Cwd))).ToList();
        var outside = resolved.Where(r => !IsWritableTarget(r, ctx)).ToList();
        if (outside.Count > 0)
            return Deny("destructive_fs", $"find destructivo fuera del worktree: {outside[0]}");
        return Ok("worktree_write", "find destructivo dentro del worktree");
    }

    public static PolicyFinding? RuleSsh(ShellCommand cmd)
    {
        var verb = cmd.Verb;
        if (PiTargetOf(cmd) is not null) return null;
        if (verb is "ssh" or "mosh" or "sftp" or "tailscale")
        {
            var inv = ShellSegmenter.ParseSshInvocation(cmd.Argv);
            if (inv.ReverseForward)
                return Deny("exfiltration", "Túnel inverso SSH (-R) prohibido");
            if (verb == "tailscale" && (cmd.Argv.Count > 1 ? cmd.Argv[1] : "") != "ssh")
                return null;
            if (inv.Host is null) return null;
            if (string.IsNullOrWhiteSpace(inv.Remote))
                return Ok("read", $"Sesión/túnel SSH a {inv.Host}");
            return Gray("remote_exec", $"Comando remoto en {inv.Host}");
        }

        if (verb is "scp" or "rsync")
        {
            var specs = ShellSegmenter.RemoteSpecsOf(cmd.Argv);
            if (specs.Count == 0) return null;
            var args = ShellSegmenter.NonFlagArgs(cmd.Argv);
            var last = args.Count > 0 ? args[^1] : "";
            var uploads = specs.Any(s => s.Raw == last);
            return uploads
                ? Esc("external_write", $"Subida de archivos a {specs[0].Host}")
                : Gray("remote_exec", $"Descarga desde {specs[0].Host}");
        }

        return null;
    }

    public static PolicyFinding? RuleEnvFileRead(ShellCommand cmd)
    {
        if (!ReadVerbs.Contains(cmd.Verb)) return null;
        return cmd.Argv.Skip(1).Any(IsDotEnvToken)
            ? Gray("env_read", "Lectura de un archivo .env")
            : null;
    }

    private static readonly HashSet<string> ShellControl = new(StringComparer.Ordinal)
    {
        "for", "while", "until", "if", "case", "select", "fi", "done", "esac", "in", "function",
        "local", "declare", "shift", "break", "continue", "return", "exit", "[", "[[", "]]", ":",
    };

    public static PolicyFinding? RuleShellControl(ShellCommand cmd) =>
        ShellControl.Contains(cmd.Verb) ? Ok("read", "Estructura de control del shell") : null;

    public static PolicyFinding? RuleReadOnly(ShellCommand cmd) =>
        ReadVerbs.Contains(cmd.Verb) ? Ok("read", $"Comando de lectura ({cmd.Verb})") : null;

    public static PolicyFinding? RuleExfiltration(IReadOnlyList<ShellCommand> cmds, PolicyContext ctx)
    {
        if (cmds.Any(c => ShellSegmenter.ParseSshInvocation(c.Argv).ReverseForward && c.Verb is "ssh" or "mosh"))
            return Deny("exfiltration", "Túnel inverso SSH (-R) prohibido");

        var secretish = cmds.Any(c =>
            DumpsEnvironment(c)
            || c.Argv.Any(t => IsLocalCredentialToken(t, c.Cwd, ctx) || IsDotEnvToken(t) || ReferencesSecretVar(t))
            || c.EnvAssigns.Values.Any(ReferencesSecretVar));
        if (!secretish) return null;
        var sender = cmds.FirstOrDefault(c => NetworkSenders.Contains(c.Verb));
        if (sender is null) return null;
        return Deny("exfiltration", $"Posible exfiltración de secretos vía {sender.Verb}");
    }

    public static bool MentionsProd(IReadOnlyList<string> argv) =>
        argv.Any(t => ProdTokenRx.IsMatch(t));

    private static readonly System.Text.RegularExpressions.Regex ProdTokenRx = new(
        @"(^|[^a-z])(prod|production|producci[oó]n|prd|qa|staging|release)([^a-z]|$)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static bool SegmentWrites(ShellCommand cmd)
    {
        if (cmd.Redirects.Any(r => !r.StartsWith('&') && !IsDevPassthrough(r)))
            return true;
        if (cmd.Verb == "sed" && !cmd.Argv.Any(a => a == "-i" || a.StartsWith("-i", StringComparison.Ordinal)))
            return false;
        if (MutatingFsVerbs.Contains(cmd.Verb)) return true;
        if (cmd.Verb == "git")
        {
            var sub = ShellSegmenter.GitSubcommand(cmd.Argv);
            if (GitWriteSubs.Contains(sub) || GitHistorySubs.Contains(sub) || sub == "push")
                return true;
        }

        if (HttpVerbs.Contains(cmd.Verb) && IsMutatingHttp(cmd.Argv)) return true;
        if (cmd.Verb is "docker" or "kubectl")
        {
            var sub = (ShellSegmenter.NonFlagArgs(cmd.Argv).Count > 0
                ? ShellSegmenter.NonFlagArgs(cmd.Argv)[0]
                : "").ToLowerInvariant();
            if (cmd.Verb == "docker")
                return !PiDockerRead.Contains(sub) && sub != "compose";
            return KubectlWrite.Contains(sub);
        }

        if (cmd.Verb == "aws")
        {
            var aws = ShellSegmenter.AwsArgs(cmd.Argv);
            var op = aws.Count > 1 ? aws[1] : "";
            if (AwsWritePrefixes.Any(p => op.StartsWith(p, StringComparison.Ordinal))) return true;
            var service = aws.Count > 0 ? aws[0] : "";
            if (service is "s3" or "s3api" && op is not "ls" and not "presign")
                return true;
        }

        if (PkgManagers.Contains(cmd.Verb) || cmd.Verb is "pip" or "brew")
        {
            var sub = (ShellSegmenter.NonFlagArgs(cmd.Argv).Count > 0
                ? ShellSegmenter.NonFlagArgs(cmd.Argv)[0]
                : "").ToLowerInvariant();
            if (PkgInstallSubs.Contains(sub)) return true;
        }

        if (InfraApply.ContainsKey(cmd.Verb) || cmd.Verb is "alembic" or "vercel")
            return true;
        return false;
    }

    public static bool SegmentWritesOutsideScratch(ShellCommand cmd, PolicyContext ctx)
    {
        var roots = WriteRoots(ctx);
        bool Scratch(string r)
        {
            var t = r.Trim().Trim('"', '\'');
            t = TmpdirPrefixRx.Replace(t, PathClassifier.TmpDir);
            if (!t.StartsWith('/')) return false;
            return roots.Any(root =>
                root.Length > 0 && (t == root || t.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal)));
        }

        var filtered = cmd with { Redirects = cmd.Redirects.Where(r => !Scratch(r)).ToList() };
        return SegmentWrites(filtered);
    }

    private static readonly System.Text.RegularExpressions.Regex TmpdirPrefixRx = new(
        @"^\$\{?TMPDIR\}?(?=/)", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static readonly HashSet<string> OpaqueDestructiveVerbs = new(StringComparer.Ordinal)
    {
        "rm", "rmdir", "mv", "cp", "dd", "chmod", "chown", "truncate", "shred", "unlink", "git",
        "find", "xargs", "sudo", "ssh", "scp", "rsync", "curl", "wget", "kill", "pkill",
    };

    public static readonly System.Text.RegularExpressions.Regex InlineDestructive = new(
        @"\b(rm\b|rmdir\b|shutil\.rmtree|os\.remove|os\.unlink|os\.rmdir|\bdd\b|mkfs|sudo\b|kill\b|git push|aws\s+\w+\s+(create|update|delete|put|terminate|modify|run)-|scp\s|ssh\s|chmod\b|chown\b|launchctl|osascript)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public static readonly System.Text.RegularExpressions.Regex ScriptSendsOut = new(
        @"(requests\.|urllib|http\.client|httpx|aiohttp|socket\.|fetch\(|axios|https?://)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static readonly System.Text.RegularExpressions.Regex SubstitutionUnparseable = new(
        @"^(command|backtick) substitution:",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == value) return i;
        }

        return -1;
    }
}
