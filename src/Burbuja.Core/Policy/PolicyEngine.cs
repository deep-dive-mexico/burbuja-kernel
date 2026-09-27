using System.Security.Cryptography;
using System.Text;
using Burbuja.Core.Abstractions.Policy;
using Burbuja.Core.Options;
using Burbuja.Core.Sdk.Policy;
using Microsoft.Extensions.Options;

namespace Burbuja.Core.Policy;

public sealed class PolicyEngine(
    IShellCommandParser parser,
    IEnumerable<IPolicyRule<SegmentPolicySubject>> segmentRules,
    IEnumerable<IPolicyRule<ShellLineSubject>> lineRules,
    IEnumerable<IPolicyClassifier>? classifiers = null,
    IOptions<KernelOptions>? kernel = null,
    IOptions<PolicyOptions>? policy = null) : IPolicyPipeline<ActionPolicySubject>
{
    private readonly IPolicyRule<SegmentPolicySubject>[] _segmentRules = [.. segmentRules];
    private readonly IPolicyRule<ShellLineSubject>[] _lineRules = [.. lineRules];
    private readonly IPolicyClassifier[] _classifiers = [.. (classifiers ?? [])];

    public string Fingerprint { get; } = FingerprintOf(segmentRules, lineRules);

    public PolicyVerdict Classify(ActionPolicySubject subject)
    {
        subject = subject with { Context = Overlay(subject.Context) };
        var prevCtx = PolicyLogic.AmbientContext;
        var prevProbe = PathClassifier.Probe;
        PolicyLogic.AmbientContext = subject.Context;
        if (subject.Context.Probe is { } probe)
            PathClassifier.Probe = probe;
        try
        {
            var kind = subject.Kind.Trim();
            if (kind.Equals("bash", StringComparison.OrdinalIgnoreCase)
                || kind.Equals("shell", StringComparison.OrdinalIgnoreCase)
                || kind.Equals("Bash", StringComparison.Ordinal))
                return Stamp(ClassifyBash(subject.Input["command"]?.GetValue<string>() ?? "", subject.Context, 0));

            foreach (var c in _classifiers)
            {
                if (c.Handles(kind))
                    return Stamp(c.Classify(subject));
            }

            var finding = new PolicyFinding(RiskTier.Escalate, "unknown_kind", "kind", $"Unknown policy kind '{kind}'", Fingerprint);
            return new PolicyVerdict(RiskTier.Escalate, finding, [finding]);
        }
        catch (Exception ex)
        {
            var finding = new PolicyFinding(RiskTier.HardDeny, "policy_error", "policy_error", ex.Message, Fingerprint);
            return new PolicyVerdict(RiskTier.HardDeny, finding, [finding]);
        }
        finally
        {
            PolicyLogic.AmbientContext = prevCtx;
            PathClassifier.Probe = prevProbe;
        }
    }

    private PolicyContext Overlay(PolicyContext ctx)
    {
        var trusted = ctx.TrustedPaths is { Count: > 0 } ? ctx.TrustedPaths : kernel?.Value.TrustedPaths;
        var hosts = ctx.ProtectedHosts is { Count: > 0 } ? ctx.ProtectedHosts : policy?.Value.ProtectedHosts;
        var aws = ctx.AllowedAwsProfiles is { Count: > 0 } ? ctx.AllowedAwsProfiles : policy?.Value.AllowedAwsProfiles;
        var probe = ctx.Probe ?? RealPathProbe.Shared;
        return ctx with
        {
            TrustedPaths = trusted,
            ProtectedHosts = hosts,
            AllowedAwsProfiles = aws,
            Probe = probe,
        };
    }

    private PolicyVerdict Stamp(PolicyVerdict v)
    {
        var all = v.All.Select(f => f.Fingerprint is null ? f with { Fingerprint = Fingerprint } : f).ToArray();
        var overall = v.Finding.Fingerprint is null ? v.Finding with { Fingerprint = Fingerprint } : v.Finding;
        return new PolicyVerdict(v.Tier, overall, all);
    }

    private PolicyVerdict ClassifyBash(string raw, PolicyContext ctx, int depth)
    {
        var parsed = Parse(raw);
        var cmds = new List<ShellCommand>();
        var cwd = ctx.WorkRoot;
        foreach (var seg in parsed.Detailed)
        {
            var norm = ShellSegmenter.NormalizeArgv(seg.Argv, cwd);
            var cmd = new ShellCommand(
                seg.Raw,
                norm.Argv,
                seg.Argv,
                norm.Argv.Count > 0 ? norm.Argv[0] : "",
                (seg.Argv.Count > 0 ? seg.Argv[0] : "").ToLowerInvariant(),
                norm.EffectiveTool,
                seg.EnvAssigns,
                seg.Redirects,
                cwd,
                norm.Wrapper,
                cmds.Select(c => c.Verb).ToArray());
            cmds.Add(cmd);
            if (cmd.Verb == "cd")
                cwd = ShiftCwd(cmd, cwd);
        }

        PolicyFinding? overall = null;
        var all = new List<PolicyFinding>();

        var lineSubject = new ShellLineSubject(cmds, ctx);
        foreach (var rule in _lineRules)
        {
            PolicyFinding? next;
            try
            {
                next = rule.Evaluate(lineSubject);
            }
            catch (Exception ex)
            {
                next = new PolicyFinding(RiskTier.HardDeny, "policy_error", rule.Id, ex.Message, Fingerprint);
            }

            overall = Worse(overall, next);
            if (next is not null)
                all.Add(next with { Fingerprint = Fingerprint });
        }

        foreach (var cmd in cmds)
        {
            PolicyFinding? finding = null;
            foreach (var rule in _segmentRules)
            {
                PolicyFinding? next;
                try
                {
                    next = rule.Evaluate(new SegmentPolicySubject(cmd, ctx));
                }
                catch (Exception ex)
                {
                    next = new PolicyFinding(RiskTier.HardDeny, "policy_error", rule.Id, ex.Message, Fingerprint);
                }

                finding = Worse(finding, next);
            }

            if (MatchesVerify(cmd, ctx) && finding?.Tier != RiskTier.HardDeny)
                finding = new PolicyFinding(RiskTier.Allow, "verify", "verify", "Comando de verificación del registro", Fingerprint);

            if (finding is null)
            {
                if (cmd.Argv.Count == 0)
                    finding = new PolicyFinding(RiskTier.Allow, "read", "empty", "Solo asignaciones de entorno", Fingerprint);
                else if (PolicyLogic.AllowVerbs.Contains(cmd.EffectiveTool))
                    finding = new PolicyFinding(RiskTier.Allow, "read", "allow_verb", $"Comando permitido ({cmd.EffectiveTool})", Fingerprint);
                else
                    finding = new PolicyFinding(RiskTier.Gray, "unknown_command", "unknown", $"Comando desconocido: {cmd.Verb}", Fingerprint);
            }

            all.Add(finding with { Fingerprint = Fingerprint });
            overall = Worse(overall, finding);
        }

        foreach (var sb in parsed.ScriptBodies)
        {
            var toks = ScriptTokRx.Split(sb.Body).Where(t => t.Length > 0).ToArray();
            var secret = toks.FirstOrDefault(t => PolicyLogic.IsLocalCredentialToken(t, ctx.WorkRoot, ctx));
            var dotEnv = toks.FirstOrDefault(PolicyLogic.IsDotEnvToken);
            var sendsOut = PolicyLogic.ScriptSendsOut.IsMatch(sb.Body);
            PolicyFinding bodyFinding;
            if (secret is not null)
            {
                bodyFinding = new PolicyFinding(RiskTier.HardDeny, "secrets_read", "script_body",
                    $"Script ({sb.Verb}) referencia credenciales locales: {secret[..Math.Min(60, secret.Length)]}", Fingerprint);
            }
            else if (dotEnv is not null && sendsOut)
            {
                bodyFinding = new PolicyFinding(RiskTier.HardDeny, "exfiltration", "script_body",
                    $"Script ({sb.Verb}) lee {dotEnv[..Math.Min(40, dotEnv.Length)]} y habla con la red", Fingerprint);
            }
            else if (dotEnv is not null)
            {
                bodyFinding = new PolicyFinding(RiskTier.Gray, "env_read", "script_body",
                    $"Script embebido para {sb.Verb} menciona {dotEnv[..Math.Min(60, dotEnv.Length)]}", Fingerprint);
            }
            else
            {
                bodyFinding = new PolicyFinding(RiskTier.Gray, "script_body", "script_body",
                    $"Script embebido para {sb.Verb} ({sb.Body.Split('\n').Length} líneas)", Fingerprint);
            }

            overall = Worse(overall, bodyFinding);
            all.Add(bodyFinding);
        }

        if (cmds.Count == 0 && parsed.Unparseable.Count == 0)
            overall = new PolicyFinding(RiskTier.Allow, "read", "empty", "Comando vacío", Fingerprint);

        var unparseable = parsed.Unparseable.ToList();
        if (unparseable.Count > 0
            && unparseable.All(u => u == "inline code with dangerous constructs")
            && (overall is null || !PolicyLogic.IsMoreSevere(overall.Tier, RiskTier.Gray))
            && !cmds.Any(c => PolicyLogic.InlineDestructive.IsMatch(string.Join(' ', c.Argv.Skip(1)))))
        {
            overall = new PolicyFinding(RiskTier.Gray, "inline_code_risky", "inline_code",
                "Código inline con subprocess/red/escritura de archivos: el supervisor lo lee", Fingerprint);
            unparseable.Clear();
        }

        if (unparseable.Count > 0 && (overall is null || !PolicyLogic.IsMoreSevere(overall.Tier, RiskTier.Escalate)))
        {
            var reason = $"Comando no analizable de forma segura: {string.Join(", ", unparseable)}";
            var argSubs = parsed.ArgSubstitutions;
            var onlySubstitutions = argSubs.Count > 0
                && unparseable.All(u => PolicyLogic.SubstitutionUnparseable.IsMatch(u));

            var bodiesAllow = onlySubstitutions
                && depth == 0
                && (overall is null || overall.Tier == RiskTier.Allow)
                && !cmds.Any(c => PolicyLogic.OpaqueDestructiveVerbs.Contains(c.Verb))
                && argSubs.All(b => ClassifyBash(b, ctx, depth + 1).Tier == RiskTier.Allow);

            if (bodiesAllow)
            {
                var allReads = cmds.All(c => c.Argv.Count == 0 || PolicyLogic.ReadVerbs.Contains(c.Verb));
                overall = new PolicyFinding(
                    RiskTier.Gray,
                    allReads ? "opaque_read" : "opaque_arg",
                    "unparseable",
                    $"Sustitución opaca en argumentos: {string.Join(", ", argSubs.Select(b => $"$({(b.Length > 60 ? b[..60] : b)})"))}",
                    Fingerprint);
            }
            else
            {
                overall = overall is { Tier: RiskTier.Escalate }
                    ? overall with { Reason = $"{overall.Reason}; {reason}" }
                    : new PolicyFinding(RiskTier.Escalate, "unparseable", "unparseable", reason, Fingerprint);
            }
        }

        overall ??= new PolicyFinding(RiskTier.Gray, "unknown_command", "unknown", "Comando desconocido", Fingerprint);
        overall = ApplyProfile(overall, cmds, ctx);
        return new PolicyVerdict(overall.Tier, overall with { Fingerprint = Fingerprint }, all);
    }

    internal static PolicyFinding? Worse(PolicyFinding? a, PolicyFinding? b)
    {
        if (b is null) return a;
        if (a is null) return b;
        return b.Tier > a.Tier ? b : a;
    }

    private static bool MatchesVerify(ShellCommand cmd, PolicyContext ctx)
    {
        var candidates = new[]
        {
            string.Join(' ', cmd.Argv),
            string.Join(' ', cmd.ArgvRaw),
            cmd.Raw.Trim(),
        };
        return ctx.VerifyCommands.Any(vc =>
            vc.Length > 0 && candidates.Any(c => c.StartsWith(vc, StringComparison.Ordinal)));
    }

    private static string ShiftCwd(ShellCommand cmd, string cwd)
    {
        var args = ShellSegmenter.NonFlagArgs(cmd.Argv);
        if (args.Count == 0)
            return PathClassifier.HomeDir;
        var arg = args[0];
        if (arg == "-")
            return cwd;
        return ShellSegmenter.IsPathLike(arg) ? arg : ShellSegmenter.ExpandToken($"./{arg}", cwd);
    }

    /// <summary>
    /// Visible to the test assembly so the "a terminal tier survives every profile" fact can reach it
    /// without a policy pipeline: the rule is about this one function, and building a context to observe
    /// it would test the context as much as the rule.
    /// </summary>
    internal static PolicyFinding ApplyProfile(PolicyFinding finding, IReadOnlyList<ShellCommand> cmds, PolicyContext ctx)
    {
        // A7/A5: a terminal tier is never softened. The profile may turn an escalation into a gray — it
        // may not touch a deny, a forbidden transition or a request that could not be judged.
        //
        // `>=` rather than `==`, and the honest note that goes with it: **the two forms behave identically
        // today**. Both branches below are already unreachable for a tier above HardDeny — the FullAuto
        // branch tests `== Escalate`, and the read-only branch requires `!IsMoreSevere(tier, Gray)` — so
        // the neuter check for this line PASSED, which is how that was established rather than assumed.
        // The comparison stays because it states the rule the function is implementing, and because the
        // next branch somebody adds will not necessarily repeat those guards.
        if (finding.Tier >= RiskTier.HardDeny)
            return finding;

        if (ctx.Profile == PolicyProfile.FullAuto && finding.Tier == RiskTier.Escalate)
        {
            if (finding.Class is "external_write" or "cloud_write")
                return finding with { Tier = RiskTier.Gray };
            if (finding.Class == "write_prod" && cmds.Any(c => c.Verb == "aws") && !cmds.Any(c => PolicyLogic.MentionsProd(c.Argv)))
                return finding with { Tier = RiskTier.Gray, Class = "aws_write", Reason = $"{finding.Reason} — sin señal de prod/qa en el comando; el supervisor decide" };
            if (finding.Class == "remote_exec" && cmds.Any(c => c.Verb == "aws") && !cmds.Any(c => PolicyLogic.MentionsProd(c.Argv)))
                return finding with { Tier = RiskTier.Gray, Reason = $"{finding.Reason} — sin señal de prod/qa en el comando; el supervisor decide" };
        }

        if (ctx.Profile is PolicyProfile.ReadOnly or PolicyProfile.RestrictedProd
            && finding.Tier != RiskTier.HardDeny && !PolicyLogic.IsMoreSevere(finding.Tier, RiskTier.Gray)
            && cmds.Any(PolicyLogic.SegmentWrites))
        {
            return new PolicyFinding(
                RiskTier.Escalate,
                ctx.Profile == PolicyProfile.ReadOnly ? "read_only" : "prod_readonly",
                "prod_readonly",
                "Perfil restrictivo: una escritura necesita aprobación",
                finding.Fingerprint);
        }

        return finding;
    }

    private ShellParseResult Parse(string command)
    {
        if (parser is ShellCommandParser scp)
            return scp.ParseDetailed(command);
        var segs = parser.Parse(command);
        var detailed = segs.Select(s => new DetailedParsedSegment(
            s.Argv, s.Redirects, new Dictionary<string, string>(), string.Join(' ', s.Argv))).ToList();
        var unparseable = segs.Any(s => s.Unparseable) ? new List<string> { "unparseable construct" } : new List<string>();
        return new ShellParseResult(segs, unparseable, [], [], detailed);
    }

    private static string FingerprintOf(
        IEnumerable<IPolicyRule<SegmentPolicySubject>> segment,
        IEnumerable<IPolicyRule<ShellLineSubject>> line)
    {
        var ids = segment.Select(r => r.Id).Concat(line.Select(r => r.Id));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(',', ids)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static readonly System.Text.RegularExpressions.Regex ScriptTokRx = new(
        @"[\s""'`()]+", System.Text.RegularExpressions.RegexOptions.Compiled);
}
