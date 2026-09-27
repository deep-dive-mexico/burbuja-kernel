using System.Text.RegularExpressions;

namespace Burbuja.Core.Sdk.Policy;

public sealed record ParsedSegment(
    IReadOnlyList<string> Argv,
    IReadOnlyList<string> Redirects,
    IReadOnlyDictionary<string, string> EnvAssigns,
    string Raw);

public sealed record ParsedCommandLine(
    IReadOnlyList<ParsedSegment> Segments,
    IReadOnlyList<string> Unparseable,
    IReadOnlyList<string> ArgSubstitutions,
    IReadOnlyList<(string Verb, string Body)> ScriptBodies);

public sealed record SshInvocation(
    string? Host,
    string? LoginUser,
    IReadOnlyList<string> JumpHosts,
    string Remote,
    bool ReverseForward);

public sealed record RemoteSpec(string Raw, string? User, string Host, int Index);

public sealed record NormalizedCommand(
    IReadOnlyList<string> Argv,
    string EffectiveTool,
    string? Wrapper,
    string? RemoteHost);

public sealed record Heredoc(string Verb, string Body, bool Quoted);

/// <summary>
/// Shell parsing for the hard gate. Port of nightshift <c>src/policy/bash.ts</c>.
/// </summary>
public static class ShellSegmenter
{
    private static readonly Regex[] SafeSubstitutions =
    [
        new(@"^\$\(\s*git\s+rev-parse\b", RegexOptions.Compiled),
        new(@"^\$\(\s*pwd\s*\)$", RegexOptions.Compiled),
        new(@"^\$\(\s*date\b", RegexOptions.Compiled),
        new(@"^\$\(\s*basename\b", RegexOptions.Compiled),
        new(@"^\$\(\s*dirname\b", RegexOptions.Compiled),
        new(@"^\$\(\s*id\s+-u\s*\)$", RegexOptions.Compiled),
        new(@"^\$\(\s*uname\b", RegexOptions.Compiled),
        new(@"^\$\(\s*nproc\s*\)$", RegexOptions.Compiled),
        new(@"^\$\(\s*sysctl\s+-n\s+hw\.ncpu\s*\)$", RegexOptions.Compiled),
        new(@"^`git\s+rev-parse\b", RegexOptions.Compiled),
        new(@"^`pwd`$", RegexOptions.Compiled),
        new(@"^`date\b", RegexOptions.Compiled),
        new(@"^`basename\b", RegexOptions.Compiled),
        new(@"^`dirname\b", RegexOptions.Compiled),
        new(@"^`id\s+-u`$", RegexOptions.Compiled),
        new(@"^`uname\b", RegexOptions.Compiled),
        new(@"^`nproc`$", RegexOptions.Compiled),
        new(@"^`sysctl\s+-n\s+hw\.ncpu`$", RegexOptions.Compiled),
        new(@"^\$\(\s*cat\s+<<-?\s*'[A-Za-z_][A-Za-z0-9_]*'\s*\)$", RegexOptions.Compiled),
        new(@"^`\s*cat\s+<<-?\s*'[A-Za-z_][A-Za-z0-9_]*'\s*`$", RegexOptions.Compiled),
    ];

    private static readonly HashSet<string> FlagSplitTools = new(StringComparer.Ordinal)
    {
        "ls", "rm", "rmdir", "cp", "mv", "mkdir", "git", "ssh", "scp", "grep", "egrep",
        "fgrep", "docker", "ps", "du", "df", "cat", "head", "tail", "pkill", "chmod", "chown",
    };

    private static readonly HashSet<string> InlineInterpreters = new(StringComparer.Ordinal)
    {
        "python", "python3", "node", "perl", "ruby",
    };

    internal static readonly HashSet<string> ShellVerbs = new(StringComparer.Ordinal)
    {
        "sh", "bash", "zsh", "dash", "ksh",
    };

    private static readonly HashSet<string> XargsMutating = new(StringComparer.Ordinal)
    {
        "rm", "mv", "cp", "chmod", "chown", "kill", "pkill", "truncate", "shred", "tee", "dd",
        "docker", "git",
    };

    private static readonly HashSet<string> ControlPrefixes = new(StringComparer.Ordinal)
    {
        "do", "then", "else", "elif", "{", "}", "!",
    };

    private static readonly HashSet<string> Wrappers = new(StringComparer.Ordinal)
    {
        "sudo", "doas", "env", "command", "nice", "nohup", "time", "caffeinate", "stdbuf",
    };

    private static readonly HashSet<string> TimeoutWrappers = new(StringComparer.Ordinal)
    {
        "timeout", "gtimeout",
    };

    private static readonly HashSet<string> TimeoutValueFlags = new(StringComparer.Ordinal)
    {
        "-k", "--kill-after", "-s", "--signal",
    };

    private static readonly HashSet<string> SshValueFlags = new(StringComparer.Ordinal)
    {
        "-b", "-c", "-D", "-E", "-e", "-F", "-I", "-i", "-J", "-L", "-l", "-m", "-O", "-o",
        "-p", "-Q", "-R", "-S", "-W", "-w",
    };

    internal static readonly HashSet<string> GitGlobalValueFlags = new(StringComparer.Ordinal)
    {
        "-c", "-C", "--git-dir", "--work-tree", "--namespace", "--exec-path",
    };

    internal static readonly HashSet<string> GhValueFlags = new(StringComparer.Ordinal)
    {
        "-X", "--request", "-H", "--header", "-F", "-f", "--field", "--raw-field", "-R",
        "--repo", "-t", "--title", "-b", "--body", "-q", "--jq", "--template", "--method",
    };

    internal static readonly HashSet<string> DockerValueFlags = new(StringComparer.Ordinal)
    {
        "-c", "--context", "-H", "--host", "--config", "--log-level", "--tlscacert",
        "--tlscert", "--tlskey",
    };

    private static readonly HashSet<string> AwsGlobalValueFlags = new(StringComparer.Ordinal)
    {
        "--profile", "--region", "--output", "--query", "--endpoint-url",
        "--cli-connect-timeout", "--cli-read-timeout", "--ca-bundle", "--color",
        "--cli-binary-format",
    };

    private static readonly HashSet<string> HeredocDataSinks = new(StringComparer.Ordinal)
    {
        "cat", "tee", "head", "tail", "wc", "grep", "sort", "uniq", "cut", "tr", "sed", "awk",
        "jq", "yq", "base64", "md5", "shasum", "sha256sum", "diff", "patch", "git", "gh",
        "less", "more", "read", "column", "fold", "fmt", "nl", "rev", "xxd", "od", "hexdump",
        "true", ":",
    };

    private static readonly string[] TrustedBinDirs =
    [
        "/opt/homebrew/bin",
        "/opt/homebrew/opt/python@3.12/bin",
        "/usr/local/bin",
        "/usr/bin",
        "/bin",
        $"{PathClassifier.HomeDir}/.local/bin",
        $"{PathClassifier.HomeDir}/.cargo/bin",
        $"{PathClassifier.HomeDir}/.bun/bin",
        $"{PathClassifier.HomeDir}/Library/pnpm",
    ];

    private static readonly string[] RedirectOps = ["<", ">", ">>", "2>", "&>", ">|"];

    private static readonly Regex AssignmentPrefix = new(
        @"(?:^|[\s;&|])(?:[A-Za-z_][A-Za-z0-9_]*=)""?$", RegexOptions.Compiled);

    private static readonly Regex AfterAssignment = new(@"^""?(?:$|[\s;&|)])", RegexOptions.Compiled);

    private static readonly Regex BundledShortFlags = new(@"^-[a-zA-Z]{2,}$", RegexOptions.Compiled);

    private static readonly Regex PathLikeRx = new(
        @"^(~|\$HOME|\$\{HOME\}|\$TMPDIR|\$\{TMPDIR\}|/|\./|\.\./)", RegexOptions.Compiled);

    private static readonly Regex EnvAssignRx = new(@"^[A-Za-z_][A-Za-z0-9_]*=", RegexOptions.Compiled);

    private static readonly Regex IndirectExpansion = new(@"\$\{!", RegexOptions.Compiled);

    private static readonly Regex ProcessSubstOut = new(@">\(", RegexOptions.Compiled);

    private static readonly Regex HexOrAnsiC = new(@"\\x[0-9a-fA-F]{2}|\$'", RegexOptions.Compiled);

    private static readonly Regex SingleQuoted = new(@"'[^']*'", RegexOptions.Compiled);

    private static readonly Regex DoubleQuoted = new(@"""(?:[^""\\]|\\.)*""", RegexOptions.Compiled);

    private static readonly Regex ExecSink = new(
        @"\|\s*(sudo\s+)?(sh|bash|zsh|dash|python3?|node|perl|ruby|eval)\b(?!\s+-[ce]\b)(?!\s+-m\s+json)",
        RegexOptions.Compiled);

    private static readonly Regex CurlWgetPipe = new(@"(^|[|;&\s])(curl|wget)\b[^|;]*\|", RegexOptions.Compiled);

    private static readonly Regex Base64Decode = new(@"\bbase64\s+(-d|-D|--decode)\b", RegexOptions.Compiled);

    private static readonly Regex Base64DecodePipe = new(@"\bbase64\s+(-d|-D|--decode)\b[^|;]*\|", RegexOptions.Compiled);

    private static readonly Regex DangerousInline = new(
        @"(subprocess|os\.system|os\.popen|child_process|require\(['""]fs|open\([^)]*,\s*['""]w|socket|urllib|requests\.|fetch\()",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ShCNonLiteral = new(@"\$\{?\w", RegexOptions.Compiled);

    private static readonly Regex HeredocMark = new(
        @"<<(-?)\s*(['""]?)([A-Za-z_][A-Za-z0-9_]*)\2", RegexOptions.Compiled);

    private static readonly Regex RemoteSpecRx = new(
        @"^(?:([^@:/]+)@)?([A-Za-z0-9._-]+):(?!//)", RegexOptions.Compiled);

    private static readonly Regex DurationRx = new(@"^\d+(\.\d+)?[smhd]?$", RegexOptions.Compiled);

    private static readonly Regex NodeBinRx = new(@"^(.*)/node_modules/\.bin/([^/]+)$", RegexOptions.Compiled);

    private static readonly Regex VenvBinRx = new(@"^(.*)/\.venv/bin/([^/]+)$", RegexOptions.Compiled);

    private static readonly Regex PythonBinRx = new(@"^python\d*(\.\d+)?$", RegexOptions.Compiled);

    private static readonly Regex DollarVarInHeredoc = new(@"\$[A-Za-z_{]", RegexOptions.Compiled);

    private static bool IsSafeSubstitution(string s) =>
        SafeSubstitutions.Any(re => re.IsMatch(s));

    public static string Dequote(string s)
    {
        var outSb = new System.Text.StringBuilder(s.Length);
        var sq = false;
        var dq = false;
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch == '\\' && !sq)
            {
                if (i + 1 < s.Length)
                {
                    outSb.Append(s[i + 1]);
                    i++;
                }

                continue;
            }

            if (ch == '\'' && !dq)
            {
                sq = !sq;
                continue;
            }

            if (ch == '"' && !sq)
            {
                dq = !dq;
                continue;
            }

            outSb.Append(ch);
        }

        return outSb.ToString();
    }

    public static bool IsPathLike(string token)
    {
        var t = token.StartsWith('@') ? token[1..] : token;
        if (t is "." or "..")
            return true;
        return PathLikeRx.IsMatch(t);
    }

    public static string ExpandToken(string token, string cwd)
    {
        if (token.StartsWith('@'))
            return "@" + PathClassifier.ExpandPath(token[1..], cwd);
        return PathClassifier.ExpandPath(token, cwd);
    }

    private static List<string> SplitShortFlags(IReadOnlyList<string> argv)
    {
        if (argv.Count == 0 || !FlagSplitTools.Contains(argv[0].ToLowerInvariant()))
            return [.. argv];
        var output = new List<string> { argv[0] };
        for (var i = 1; i < argv.Count; i++)
        {
            var arg = argv[i];
            if (BundledShortFlags.IsMatch(arg))
            {
                foreach (var f in arg[1..])
                    output.Add("-" + f);
            }
            else
            {
                output.Add(arg);
            }
        }

        return output;
    }

    private static List<string> LineLevelUnparseable(string cmd)
    {
        var output = new List<string>();
        if (CurlWgetPipe.IsMatch(cmd))
        {
            var idx = CurlWgetPipe.Match(cmd).Index;
            if (ExecSink.IsMatch(cmd[idx..]))
                output.Add("curl|wget piped to shell");
        }

        if (Base64DecodePipe.IsMatch(cmd))
        {
            var m = Base64Decode.Match(cmd);
            if (m.Success && ExecSink.IsMatch(cmd[m.Index..]))
                output.Add("base64 -d in a pipeline");
        }

        return output;
    }

    private static bool IsAssignmentValue(string before, string after) =>
        AssignmentPrefix.IsMatch(before) && AfterAssignment.IsMatch(after);

    private static (List<string> Segments, List<string> Unparseable, List<string> AssignedSubstitutions, List<string> ArgSubstitutions)
        SplitStatements(string cmd)
    {
        var unparseable = new List<string>();
        var segments = new List<string>();
        var assignedSubstitutions = new List<string>();
        var argSubstitutions = new List<string>();
        var current = new System.Text.StringBuilder();
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escapeNext = false;
        var parenDepth = 0;
        var i = 0;

        void Push()
        {
            var trimmed = current.ToString().Trim();
            if (trimmed.Length > 0)
                segments.Add(trimmed);
            current.Clear();
        }

        string CurrentText() => current.ToString();

        while (i < cmd.Length)
        {
            var ch = cmd[i];
            var next = i + 1 < cmd.Length ? cmd[i + 1] : '\0';

            if (escapeNext)
            {
                current.Append(ch);
                escapeNext = false;
                i++;
                continue;
            }

            if (ch == '\\' && !inSingleQuote)
            {
                current.Append(ch);
                escapeNext = true;
                i++;
                continue;
            }

            if (inSingleQuote)
            {
                current.Append(ch);
                if (ch == '\'')
                    inSingleQuote = false;
                i++;
                continue;
            }

            if (ch == '$' && next == '(' && i + 2 < cmd.Length && cmd[i + 2] == '(')
            {
                var depth = 0;
                var start = i;
                i += 1;
                while (i < cmd.Length)
                {
                    if (cmd[i] == '(')
                        depth++;
                    if (cmd[i] == ')')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            i++;
                            break;
                        }
                    }

                    i++;
                }

                current.Append(Slice(cmd, start, i));
                continue;
            }

            if (ch == '<' && next == '(' && !inDoubleQuote)
            {
                var start = i;
                i += 2;
                parenDepth = 1;
                while (i < cmd.Length && parenDepth > 0)
                {
                    if (cmd[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (cmd[i] == '(')
                        parenDepth++;
                    if (cmd[i] == ')')
                        parenDepth--;
                    i++;
                }

                var body = Slice(cmd, start + 2, i - 1);
                unparseable.Add($"command substitution: <({body})");
                argSubstitutions.Add(body);
                current.Append("/dev/fd/63");
                continue;
            }

            if ((ch == '$' && next == '(') || ch == '`')
            {
                var start = i;
                string body;
                if (ch == '`')
                {
                    i++;
                    while (i < cmd.Length && cmd[i] != '`')
                    {
                        if (cmd[i] == '\\')
                            i += 2;
                        else
                            i++;
                    }

                    if (i < cmd.Length)
                        i++;
                    body = Slice(cmd, start + 1, i - 1);
                }
                else
                {
                    i += 2;
                    parenDepth = 1;
                    while (i < cmd.Length && parenDepth > 0)
                    {
                        if (cmd[i] == '\\')
                        {
                            i += 2;
                            continue;
                        }

                        if (cmd[i] == '(')
                            parenDepth++;
                        if (cmd[i] == ')')
                            parenDepth--;
                        i++;
                    }

                    body = Slice(cmd, start + 2, i - 1);
                }

                var sub = Slice(cmd, start, i);
                if (!IsSafeSubstitution(sub))
                {
                    if (IsAssignmentValue(CurrentText(), Slice(cmd, i, cmd.Length)))
                    {
                        assignedSubstitutions.Add(body);
                        current.Append(inDoubleQuote ? sub : $"\"{sub}\"");
                        continue;
                    }

                    unparseable.Add($"{(ch == '`' ? "backtick" : "command")} substitution: {sub}");
                    argSubstitutions.Add(body);
                }

                current.Append(sub);
                continue;
            }

            if (inDoubleQuote)
            {
                current.Append(ch);
                if (ch == '"')
                    inDoubleQuote = false;
                i++;
                continue;
            }

            if (ch == '\'')
            {
                current.Append(ch);
                inSingleQuote = true;
                i++;
                continue;
            }

            if (ch == '"')
            {
                current.Append(ch);
                inDoubleQuote = true;
                i++;
                continue;
            }

            if (ch is ';' or '\n')
            {
                Push();
                i++;
                continue;
            }

            if ((ch == '&' && next == '&') || (ch == '|' && next == '|'))
            {
                Push();
                i += 2;
                continue;
            }

            if (ch == '|')
            {
                Push();
                i++;
                continue;
            }

            if (ch == '&')
            {
                if (next == '>' || CurrentText().TrimEnd().EndsWith('>'))
                {
                    current.Append(ch);
                    i++;
                    continue;
                }

                Push();
                i++;
                continue;
            }

            current.Append(ch);
            i++;
        }

        Push();
        if (inSingleQuote || inDoubleQuote)
            unparseable.Add("unbalanced quotes");

        return (segments, unparseable, assignedSubstitutions, argSubstitutions);
    }

    private static (ParsedSegment? Parsed, List<string> Unparseable) ParseSegment(string raw)
    {
        var unparseable = new List<string>();
        var argv = new List<string>();
        var redirects = new List<string>();
        var envAssigns = new Dictionary<string, string>(StringComparer.Ordinal);

        if (ProcessSubstOut.IsMatch(raw))
        {
            unparseable.Add("process substitution");
            return (null, unparseable);
        }

        if (IndirectExpansion.IsMatch(raw))
        {
            unparseable.Add("indirect expansion ${!var}");
            return (null, unparseable);
        }

        var unquoted = SingleQuoted.Replace(raw, "''");
        unquoted = DoubleQuoted.Replace(unquoted, "\"\"");
        if (HexOrAnsiC.IsMatch(unquoted))
        {
            unparseable.Add("hex escapes or $'...'");
            return (null, unparseable);
        }

        var i = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escapeNext = false;
        var token = new System.Text.StringBuilder();
        var tokens = new List<string>();

        void PushToken()
        {
            if (token.Length > 0)
            {
                tokens.Add(Dequote(token.ToString()));
                token.Clear();
            }
        }

        while (i < raw.Length)
        {
            var ch = raw[i];
            var next = i + 1 < raw.Length ? raw[i + 1] : '\0';

            if (escapeNext)
            {
                token.Append(ch);
                escapeNext = false;
                i++;
                continue;
            }

            if (ch == '\\' && !inSingleQuote)
            {
                token.Append(ch);
                escapeNext = true;
                i++;
                continue;
            }

            if (inSingleQuote)
            {
                token.Append(ch);
                if (ch == '\'')
                    inSingleQuote = false;
                i++;
                continue;
            }

            if (inDoubleQuote)
            {
                token.Append(ch);
                if (ch == '"')
                    inDoubleQuote = false;
                i++;
                continue;
            }

            if (ch == '\'')
            {
                token.Append(ch);
                inSingleQuote = true;
                i++;
                continue;
            }

            if (ch == '"')
            {
                token.Append(ch);
                inDoubleQuote = true;
                i++;
                continue;
            }

            if (ch == '>')
            {
                PushToken();
                if (next == '>')
                {
                    tokens.Add(">>");
                    i += 2;
                }
                else if (next == '|')
                {
                    tokens.Add(">|");
                    i += 2;
                }
                else
                {
                    tokens.Add(">");
                    i++;
                }

                continue;
            }

            if (ch == '<')
            {
                PushToken();
                tokens.Add("<");
                i++;
                continue;
            }

            if (ch == '2' && next == '>' && token.Length == 0)
            {
                PushToken();
                if (i + 3 < raw.Length && raw[i + 2] == '&' && raw[i + 3] == '1')
                {
                    tokens.Add("2>&1");
                    i += 4;
                }
                else
                {
                    tokens.Add("2>");
                    i += 2;
                }

                continue;
            }

            if (ch == '&' && next == '>')
            {
                PushToken();
                tokens.Add("&>");
                i += 2;
                continue;
            }

            if (ch == '$' && next == '{' && !inSingleQuote)
            {
                var depth = 0;
                var closed = false;
                while (i < raw.Length)
                {
                    var c = raw[i];
                    if (c == '\\')
                    {
                        token.Append(c);
                        if (i + 1 < raw.Length)
                            token.Append(raw[i + 1]);
                        i += 2;
                        continue;
                    }

                    if (c is '\'' or '"')
                    {
                        var j = i + 1;
                        while (j < raw.Length && raw[j] != c)
                        {
                            if (c == '"' && raw[j] == '\\')
                                j++;
                            j++;
                        }

                        if (j >= raw.Length)
                            break;
                        token.Append(Slice(raw, i, j + 1));
                        i = j + 1;
                        continue;
                    }

                    token.Append(c);
                    i++;
                    if (c == '{')
                        depth++;
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            closed = true;
                            break;
                        }
                    }
                }

                if (!closed)
                {
                    unparseable.Add("unterminated ${…} expansion");
                    return (null, unparseable);
                }

                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                PushToken();
                i++;
                continue;
            }

            token.Append(ch);
            i++;
        }

        PushToken();

        var seenNonAssign = false;
        for (var j = 0; j < tokens.Count; j++)
        {
            var t = tokens[j];
            if (!seenNonAssign && ControlPrefixes.Contains(t.ToLowerInvariant()))
            {
                argv.Add(t);
                continue;
            }

            if (RedirectOps.Contains(t))
            {
                var target = j + 1 < tokens.Count ? tokens[j + 1] : null;
                if (target is not null && !RedirectOps.Contains(target) && target != "2>&1")
                {
                    if (t == "<")
                        argv.Add(target);
                    else
                        redirects.Add(target);
                    j++;
                }

                continue;
            }

            if (t == "2>&1")
                continue;

            if (!seenNonAssign && EnvAssignRx.IsMatch(t))
            {
                var eq = t.IndexOf('=');
                envAssigns[t[..eq]] = t[(eq + 1)..];
                continue;
            }

            seenNonAssign = true;
            argv.Add(t);
        }

        if (argv.Count == 0)
            return (null, unparseable);

        var split = SplitShortFlags(argv);
        var dequoted = split.Select(Dequote).ToList();
        var eff = StripWrappers(dequoted).Argv;
        var effVerb = (TrustedBinBasename(eff.Count > 0 ? eff[0] : "") ?? (eff.Count > 0 ? eff[0] : "")).ToLowerInvariant();
        var verb = effVerb.Length > 0
            ? effVerb
            : (TrustedBinBasename(split[0]) ?? split[0]).ToLowerInvariant();

        if (verb == "eval")
        {
            unparseable.Add("eval");
            return (null, unparseable);
        }

        if (verb is "source" or ".")
        {
            unparseable.Add("source with non-literal");
            return (null, unparseable);
        }

        if (verb == "xargs" && split.Any(t => XargsMutating.Contains(t)))
        {
            unparseable.Add("xargs with mutating verb");
            return (null, unparseable);
        }

        if (ShellVerbs.Contains(verb))
        {
            var cIdx = IndexOf(eff, "-c");
            var body = cIdx >= 0 && cIdx + 1 < eff.Count ? eff[cIdx + 1] : null;
            if (cIdx >= 0 && (body is null || ShCNonLiteral.IsMatch(body)))
            {
                unparseable.Add("sh -c with non-literal body");
                return (null, unparseable);
            }
        }

        if (InlineInterpreters.Contains(verb))
        {
            var fIdx = -1;
            for (var fi = 0; fi < eff.Count; fi++)
            {
                if (eff[fi] is "-c" or "-e")
                {
                    fIdx = fi;
                    break;
                }
            }
            var body = fIdx >= 0 && fIdx + 1 < eff.Count ? eff[fIdx + 1] : "";
            if (DangerousInline.IsMatch(body))
                unparseable.Add("inline code with dangerous constructs");
        }

        return (new ParsedSegment(split, redirects, envAssigns, raw), unparseable);
    }

    public static SshInvocation ParseSshInvocation(IReadOnlyList<string> argv)
    {
        var host = (string?)null;
        var loginUser = (string?)null;
        var jumpHosts = new List<string>();
        var remote = "";
        var reverseForward = false;
        var i = 1;
        if (argv.Count > 1 && argv[0] == "tailscale" && argv[1] == "ssh")
            i = 2;
        for (; i < argv.Count; i++)
        {
            var t = argv[i];
            if (t.StartsWith('-'))
            {
                if (t == "-R")
                    reverseForward = true;
                if (t.StartsWith("-R", StringComparison.Ordinal) && t.Length > 2)
                    reverseForward = true;
                if (t == "-J" && i + 1 < argv.Count)
                    jumpHosts.Add(argv[i + 1]);
                if (t == "-l" && i + 1 < argv.Count)
                    loginUser = argv[i + 1];
                if (SshValueFlags.Contains(t) && i + 1 < argv.Count)
                    i++;
                continue;
            }

            host = t;
            remote = string.Join(' ', argv.Skip(i + 1)).Trim();
            break;
        }

        return new SshInvocation(host, loginUser, jumpHosts, remote, reverseForward);
    }

    public static IReadOnlyList<RemoteSpec> RemoteSpecsOf(IReadOnlyList<string> argv)
    {
        var output = new List<RemoteSpec>();
        for (var i = 1; i < argv.Count; i++)
        {
            var t = argv[i];
            if (t.StartsWith('-') || t.StartsWith('/') || t.StartsWith('.'))
                continue;
            var m = RemoteSpecRx.Match(t);
            if (m.Success)
                output.Add(new RemoteSpec(t, m.Groups[1].Success && m.Groups[1].Length > 0 ? m.Groups[1].Value : null, m.Groups[2].Value, i));
        }

        return output;
    }

    public static (IReadOnlyList<string> Argv, string? Wrapper) StripWrappers(IReadOnlyList<string> argv)
    {
        var output = argv.ToList();
        string? wrapper = null;
        while (output.Count > 0)
        {
            var first = (TrustedBinBasename(output[0]) ?? output[0]).ToLowerInvariant();
            if (Wrappers.Contains(first) || ControlPrefixes.Contains(first))
            {
                if (first is "sudo" or "doas")
                    wrapper = "sudo";
                output.RemoveAt(0);
                if (first == "env")
                {
                    while (output.Count > 0 && (output[0].StartsWith('-') || EnvAssignRx.IsMatch(output[0])))
                    {
                        var f = output[0];
                        output.RemoveAt(0);
                        if ((f is "-u" or "--unset" or "-C" or "--chdir" or "-S" or "--split-string") && output.Count > 0)
                            output.RemoveAt(0);
                    }
                }
            }
            else if (TimeoutWrappers.Contains(first))
            {
                output.RemoveAt(0);
                while (output.Count > 0 && output[0].StartsWith('-'))
                {
                    var f = output[0];
                    output.RemoveAt(0);
                    if (TimeoutValueFlags.Contains(f) && output.Count > 0)
                        output.RemoveAt(0);
                }

                if (output.Count > 0 && DurationRx.IsMatch(output[0]))
                    output.RemoveAt(0);
            }
            else
            {
                break;
            }
        }

        return (output, wrapper);
    }

    private static string? TrustedBinBasename(string verb)
    {
        if (!verb.StartsWith('/'))
            return null;
        var slash = verb.LastIndexOf('/');
        var dir = verb[..slash];
        var basename = verb[(slash + 1)..];
        if (basename.Length == 0 || !TrustedBinDirs.Contains(dir))
            return null;
        return basename;
    }

    private static List<string> CanonicalToolchain(IReadOnlyList<string> argv, string cwd)
    {
        var first = argv.Count > 0 ? argv[0] : "";
        if (!first.StartsWith('/'))
            return [.. argv];
        var root = cwd.TrimEnd('/');
        var node = NodeBinRx.Match(first);
        if (node.Success && (node.Groups[1].Value == root || node.Groups[1].Value.StartsWith(root + "/", StringComparison.Ordinal)))
            return ["pnpm", "exec", node.Groups[2].Value, .. argv.Skip(1)];
        var venv = VenvBinRx.Match(first);
        if (venv.Success && (venv.Groups[1].Value == root || venv.Groups[1].Value.StartsWith(root + "/", StringComparison.Ordinal)))
        {
            if (PythonBinRx.IsMatch(venv.Groups[2].Value))
                return [venv.Groups[2].Value, .. argv.Skip(1)];
            return ["uv", "run", venv.Groups[2].Value, .. argv.Skip(1)];
        }

        return [.. argv];
    }

    public static IReadOnlyList<string> NonFlagArgs(
        IReadOnlyList<string> argv,
        IReadOnlySet<string>? valueFlags = null,
        int start = 1)
    {
        valueFlags ??= new HashSet<string>(StringComparer.Ordinal);
        var output = new List<string>();
        for (var i = start; i < argv.Count; i++)
        {
            var t = argv[i];
            if (t.StartsWith('-'))
            {
                if (valueFlags.Contains(t) && i + 1 < argv.Count)
                    i++;
                continue;
            }

            output.Add(t);
        }

        return output;
    }

    public static string GitSubcommand(IReadOnlyList<string> argv)
    {
        var args = NonFlagArgs(argv, GitGlobalValueFlags);
        return (args.Count > 0 ? args[0] : "").ToLowerInvariant();
    }

    public static IReadOnlyList<string> GhArgs(IReadOnlyList<string> argv) =>
        NonFlagArgs(argv, GhValueFlags);

    public static IReadOnlyList<string> DockerArgs(IReadOnlyList<string> argv) =>
        NonFlagArgs(argv, DockerValueFlags);

    public static IReadOnlyList<string> AwsArgs(IReadOnlyList<string> argv)
    {
        var output = new List<string>();
        var i = 1;
        while (i < argv.Count && argv[i].StartsWith('-') && output.Count == 0)
        {
            var t = argv[i];
            if (t.Contains('='))
                i += 1;
            else if (AwsGlobalValueFlags.Contains(t))
                i += 2;
            else
                i += 1;
        }

        for (; i < argv.Count; i++)
        {
            var t = argv[i];
            if (t.StartsWith('-'))
                break;
            output.Add(t.ToLowerInvariant());
        }

        return output;
    }

    public static NormalizedCommand NormalizeArgv(IReadOnlyList<string> argv, string cwd)
    {
        if (argv.Count == 0)
            return new NormalizedCommand([], "", null, null);

        var stripped = StripWrappers(argv.Select(Dequote).ToList());
        var normalized = stripped.Argv.ToList();
        var wrapper = stripped.Wrapper;
        if (normalized.Count == 0)
            return new NormalizedCommand([], "", wrapper, null);

        for (var i = 0; i < normalized.Count; i++)
        {
            var arg = normalized[i];
            if (IsPathLike(arg))
                normalized[i] = ExpandToken(arg, cwd);
            else if (i == 0)
                normalized[i] = arg.ToLowerInvariant();
        }

        var basename = TrustedBinBasename(normalized[0]);
        if (basename is not null)
            normalized[0] = basename.ToLowerInvariant();
        normalized = CanonicalToolchain(normalized, cwd);
        normalized = SplitShortFlags(normalized);

        var tool = normalized[0];
        var effectiveTool = tool;
        string? remoteHost = null;

        if (tool is "ssh" or "mosh" or "sftp")
        {
            remoteHost = ParseSshInvocation(normalized).Host;
        }
        else if (tool == "tailscale")
        {
            effectiveTool = $"tailscale-{(normalized.Count > 1 ? normalized[1] : "").ToLowerInvariant()}";
            if (normalized.Count > 1 && normalized[1] == "ssh")
                remoteHost = ParseSshInvocation(normalized).Host;
        }
        else if (tool is "scp" or "rsync")
        {
            var specs = RemoteSpecsOf(normalized);
            remoteHost = specs.Count > 0 ? specs[0].Host : null;
        }
        else if (tool == "docker")
        {
            var parts = DockerArgs(normalized);
            effectiveTool = parts.Count > 0 && parts[0] == "compose" && parts.Count > 1
                ? $"docker-compose-{parts[1]}"
                : $"docker-{(parts.Count > 0 ? parts[0] : "")}";
            foreach (var f in new[] { "--context", "-c" })
            {
                var idx = IndexOf(normalized, f);
                if (idx >= 0 && idx + 1 < normalized.Count)
                    remoteHost = $"docker-context:{normalized[idx + 1]}";
            }
        }
        else if (tool == "dpi")
        {
            effectiveTool = $"dpi-{(normalized.Count > 1 ? normalized[1] : "").ToLowerInvariant()}";
        }
        else if (tool == "aws")
        {
            var parts = AwsArgs(normalized);
            effectiveTool = $"aws-{(parts.Count > 0 ? parts[0] : "")}";
        }
        else if (tool == "gh")
        {
            var parts = GhArgs(normalized);
            effectiveTool = parts.Count == 0
                ? "gh-"
                : parts[0] is "api" || parts.Count < 2
                    ? $"gh-{parts[0]}"
                    : $"gh-{parts[0]}-{parts[1]}";
        }
        else if (tool == "git")
        {
            effectiveTool = $"git-{GitSubcommand(normalized)}";
        }
        else if (tool == "uv")
        {
            effectiveTool = normalized.Count > 1 && normalized[1] == "run"
                ? $"uv-run-{(normalized.Count > 2 ? normalized[2] : "")}"
                : $"uv-{(normalized.Count > 1 ? normalized[1] : "")}";
        }
        else if (tool is "pnpm" or "npm" or "yarn" or "bun")
        {
            var sub = (normalized.Count > 1 ? normalized[1] : "").ToLowerInvariant();
            effectiveTool = sub is "exec" or "dlx" or "x" && normalized.Count > 2
                ? $"{tool}-{sub}-{normalized[2].ToLowerInvariant()}"
                : $"{tool}-{sub}";
        }
        else if (tool is "npx" or "pnpx" or "bunx")
        {
            var bin = normalized.Skip(1).FirstOrDefault(a => !a.StartsWith('-')) ?? "";
            effectiveTool = $"{tool}-{bin.ToLowerInvariant()}";
        }
        else if (tool == "make")
        {
            effectiveTool = $"make-{(normalized.Count > 1 ? normalized[1] : "").ToLowerInvariant()}";
        }

        return new NormalizedCommand(normalized, effectiveTool, wrapper, remoteHost);
    }

    public static IReadOnlyList<string> SubstitutionBodies(string text)
    {
        var output = new List<string>();
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\\')
            {
                i++;
                continue;
            }

            if (ch == '$' && i + 1 < text.Length && text[i + 1] == '(' && (i + 2 >= text.Length || text[i + 2] != '('))
            {
                var depth = 1;
                var j = i + 2;
                for (; j < text.Length && depth > 0; j++)
                {
                    if (text[j] == '\\')
                    {
                        j++;
                        continue;
                    }

                    if (text[j] == '(')
                        depth++;
                    else if (text[j] == ')')
                        depth--;
                }

                output.Add(Slice(text, i + 2, j - 1));
                i = j - 1;
            }
            else if (ch == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end < 0)
                    break;
                output.Add(Slice(text, i + 1, end));
                i = end;
            }
        }

        return output.Where(b => b.Trim().Length > 0).ToList();
    }

    private static IReadOnlyList<string> UnsafeSubstitutionBodies(string text) =>
        SubstitutionBodies(text).Where(b => !IsSafeSubstitution($"$({b})")).ToList();

    public static (string Text, List<Heredoc> Heredocs) ExtractHeredocs(string raw)
    {
        var lines = raw.Split('\n');
        var output = new List<string>();
        var heredocs = new List<Heredoc>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            output.Add(line);
            var marks = HeredocMark.Matches(line).Cast<Match>().Where(m =>
            {
                var at = m.Index;
                var prevOk = at == 0 || line[at - 1] != '<';
                var after = at + m.Length;
                var nextOk = after >= line.Length || line[after] != '<';
                return prevOk && nextOk;
            }).ToList();

            foreach (var m in marks)
            {
                var strip = m.Groups[1].Value == "-";
                var word = m.Groups[3].Value;
                var body = new List<string>();
                var j = i + 1;
                for (; j < lines.Length; j++)
                {
                    var l = strip ? LeadingTabs.Replace(lines[j], "") : lines[j];
                    if (l == word)
                        break;
                    body.Add(lines[j]);
                }

                var text = string.Join('\n', body);
                var quoted = m.Groups[2].Value.Length > 0;
                if (!quoted && !DollarVarInHeredoc.IsMatch(text) && UnsafeSubstitutionBodies(text).Count == 0)
                {
                    quoted = true;
                    output[^1] = output[^1].Replace(m.Value, $"<<{m.Groups[1].Value}'{word}'");
                }

                heredocs.Add(new Heredoc(HeredocVerb(line), text, quoted));
                i = j;
            }
        }

        return (string.Join('\n', output), heredocs);
    }

    private static readonly Regex LeadingTabs = new(@"^\t+", RegexOptions.Compiled);
    private static readonly Regex VerbSplit = new(@";|&&|\|\||\|", RegexOptions.Compiled);

    private static string HeredocVerb(string line)
    {
        var cut = IndexOfHeredoc(line);
        var head = cut >= 0 ? line[..cut] : line;
        var outer = VerbOf(head);
        if (ShellVerbs.Contains(outer) || InlineInterpreters.Contains(outer))
            return outer;
        var sub = Math.Max(head.LastIndexOf("$(", StringComparison.Ordinal), head.LastIndexOf('`'));
        if (sub < 0)
            return outer;
        return VerbOf(head[(sub + (head[sub] == '$' ? 2 : 1))..]);
    }

    private static int IndexOfHeredoc(string line)
    {
        for (var i = 0; i < line.Length - 1; i++)
        {
            if (line[i] == '<' && line[i + 1] == '<' && (i + 2 >= line.Length || line[i + 2] != '<'))
                return i;
        }

        return -1;
    }

    private static string VerbOf(string head)
    {
        var last = VerbSplit.Split(head).LastOrDefault() ?? head;
        var toks = last.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var k = 0;
        while (k < toks.Length && (EnvAssignRx.IsMatch(toks[k]) || toks[k] is "sudo" or "env"))
            k++;
        var v = k < toks.Length ? toks[k] : "";
        var leaf = v.Split('/').LastOrDefault() ?? "";
        return leaf.TrimStart('"', '\'').ToLowerInvariant();
    }

    private static ParsedCommandLine ParseRecursive(string raw, int depth = 0)
    {
        if (depth > 3)
            return new ParsedCommandLine([], ["nested too deeply"], [], []);

        var (text, heredocs) = ExtractHeredocs(raw);
        var scriptBodies = new List<(string Verb, string Body)>();
        var split = SplitStatements(text);
        var segments = new List<ParsedSegment>();
        var unparseable = new List<string>();
        unparseable.AddRange(split.Unparseable);
        unparseable.AddRange(LineLevelUnparseable(text));
        var argSubs = new List<string>(split.ArgSubstitutions);

        foreach (var h in heredocs)
        {
            if (ShellVerbs.Contains(h.Verb))
            {
                var nested = ParseRecursive(h.Body, depth + 1);
                segments.AddRange(nested.Segments);
                unparseable.AddRange(nested.Unparseable);
                argSubs.AddRange(nested.ArgSubstitutions);
                scriptBodies.AddRange(nested.ScriptBodies);
                continue;
            }

            if (!h.Quoted)
            {
                foreach (var body in UnsafeSubstitutionBodies(h.Body))
                {
                    var nested = ParseRecursive(body, depth + 1);
                    segments.AddRange(nested.Segments);
                    unparseable.AddRange(nested.Unparseable);
                    argSubs.AddRange(nested.ArgSubstitutions);
                    scriptBodies.AddRange(nested.ScriptBodies);
                }

                unparseable.Add("command substitution inside an unquoted heredoc");
            }

            if (!HeredocDataSinks.Contains(h.Verb))
                scriptBodies.Add((h.Verb, h.Body));
        }

        foreach (var body in split.AssignedSubstitutions)
        {
            var nested = ParseRecursive(body, depth + 1);
            segments.AddRange(nested.Segments);
            unparseable.AddRange(nested.Unparseable);
            argSubs.AddRange(nested.ArgSubstitutions);
            scriptBodies.AddRange(nested.ScriptBodies);
        }

        foreach (var seg in split.Segments)
        {
            var parsed = ParseSegment(seg);
            unparseable.AddRange(parsed.Unparseable);
            if (parsed.Parsed is null)
                continue;

            var eff = StripWrappers(parsed.Parsed.Argv.Select(Dequote).ToList()).Argv;
            var verb = (TrustedBinBasename(eff.Count > 0 ? eff[0] : "") ?? (eff.Count > 0 ? eff[0] : "")).ToLowerInvariant();
            if (ShellVerbs.Contains(verb))
            {
                var cIdx = IndexOf(eff, "-c");
                if (cIdx >= 0 && cIdx + 1 < eff.Count)
                {
                    var nested = ParseRecursive(eff[cIdx + 1], depth + 1);
                    segments.AddRange(nested.Segments);
                    unparseable.AddRange(nested.Unparseable);
                    argSubs.AddRange(nested.ArgSubstitutions);
                    scriptBodies.AddRange(nested.ScriptBodies);
                    continue;
                }
            }

            segments.Add(parsed.Parsed);
        }

        return new ParsedCommandLine(
            segments,
            unparseable.Distinct(StringComparer.Ordinal).ToList(),
            argSubs,
            scriptBodies);
    }

    public static ParsedCommandLine SplitCommandLine(string cmd) => ParseRecursive(cmd);

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
                return i;
        }

        return -1;
    }

    private static string Slice(string s, int start, int end)
    {
        if (start < 0)
            start = 0;
        if (end > s.Length)
            end = s.Length;
        if (start >= end)
            return "";
        return s[start..end];
    }
}
