using Burbuja.Core.Abstractions.Policy;
using Burbuja.Core.Sdk.Policy;

namespace Burbuja.Plugins.Policy.Core;

internal abstract class SegmentRule(string id) : IPolicyRule<SegmentPolicySubject>
{
    public string Id { get; } = id;
    public abstract PolicyFinding? Evaluate(SegmentPolicySubject subject);

    protected static PolicyFinding? Stamp(string id, PolicyFinding? finding) =>
        finding is null ? null : finding with { RuleId = id };
}

internal sealed class RuleGateEvasion() : SegmentRule("gate_evasion")
{
    public override PolicyFinding? Evaluate(SegmentPolicySubject subject) =>
        Stamp(Id, PolicyLogic.RuleGateEvasion(subject.Command));
}

internal sealed class RulePi() : SegmentRule("pi")
{
    public override PolicyFinding? Evaluate(SegmentPolicySubject subject) =>
        Stamp(Id, PolicyLogic.RulePi(subject.Command));
}

internal sealed class RuleSecretPaths() : SegmentRule("secret_paths")
{
    public override PolicyFinding? Evaluate(SegmentPolicySubject subject) =>
        Stamp(Id, PolicyLogic.RuleSecretPaths(subject.Command, subject.Context));
}

internal sealed class RuleFsWrites() : SegmentRule("fs_writes")
{
    public override PolicyFinding? Evaluate(SegmentPolicySubject subject) =>
        Stamp(Id, PolicyLogic.RuleFsWrites(subject.Command, subject.Context));
}

internal sealed class RuleFind() : SegmentRule("find")
{
    public override PolicyFinding? Evaluate(SegmentPolicySubject subject) =>
        Stamp(Id, PolicyLogic.RuleFind(subject.Command, subject.Context));
}

internal sealed class RuleGit() : SegmentRule("git")
{
    public override PolicyFinding? Evaluate(SegmentPolicySubject subject) =>
        Stamp(Id, PolicyLogic.RuleGit(subject.Command, subject.Context));
}

internal sealed class RuleAws() : SegmentRule("aws")
{
    public override PolicyFinding? Evaluate(SegmentPolicySubject subject) =>
        Stamp(Id, PolicyLogic.RuleAws(subject.Command, subject.Context));
}

internal sealed class RuleAwsProfile() : SegmentRule("aws_profile")
{
    public override PolicyFinding? Evaluate(SegmentPolicySubject subject) =>
        Stamp(Id, PolicyLogic.RuleAwsProfile(subject.Command, subject.Context));
}

internal sealed class RuleGh() : SegmentRule("gh")
{
    public override PolicyFinding? Evaluate(SegmentPolicySubject subject) =>
        Stamp(Id, PolicyLogic.RuleGh(subject.Command, subject.Context));
}

internal sealed class RuleHttp() : SegmentRule("http")
{
    public override PolicyFinding? Evaluate(SegmentPolicySubject subject) =>
        Stamp(Id, PolicyLogic.RuleHttp(subject.Command, subject.Context));
}

internal sealed class RuleDocker() : SegmentRule("docker")
{
    public override PolicyFinding? Evaluate(SegmentPolicySubject subject) =>
        Stamp(Id, PolicyLogic.RuleDocker(subject.Command));
}

internal sealed class RuleInfra() : SegmentRule("infra")
{
    public override PolicyFinding? Evaluate(SegmentPolicySubject subject) =>
        Stamp(Id, PolicyLogic.RuleInfra(subject.Command));
}

internal sealed class RuleDb() : SegmentRule("db")
{
    public override PolicyFinding? Evaluate(SegmentPolicySubject subject) =>
        Stamp(Id, PolicyLogic.RuleDb(subject.Command));
}

internal sealed class RulePackages() : SegmentRule("packages")
{
    public override PolicyFinding? Evaluate(SegmentPolicySubject subject) =>
        Stamp(Id, PolicyLogic.RulePackages(subject.Command));
}

internal sealed class RuleInterpreters() : SegmentRule("interpreters")
{
    public override PolicyFinding? Evaluate(SegmentPolicySubject subject) =>
        Stamp(Id, PolicyLogic.RuleInterpreters(subject.Command));
}

internal sealed class RuleLocalDestructive() : SegmentRule("local_destructive")
{
    public override PolicyFinding? Evaluate(SegmentPolicySubject subject) =>
        Stamp(Id, PolicyLogic.RuleLocalDestructive(subject.Command));
}

internal sealed class RuleSsh() : SegmentRule("ssh")
{
    public override PolicyFinding? Evaluate(SegmentPolicySubject subject) =>
        Stamp(Id, PolicyLogic.RuleSsh(subject.Command));
}

internal sealed class RuleEnvFileRead() : SegmentRule("env_file_read")
{
    public override PolicyFinding? Evaluate(SegmentPolicySubject subject) =>
        Stamp(Id, PolicyLogic.RuleEnvFileRead(subject.Command));
}

internal sealed class RuleShellControl() : SegmentRule("shell_control")
{
    public override PolicyFinding? Evaluate(SegmentPolicySubject subject) =>
        Stamp(Id, PolicyLogic.RuleShellControl(subject.Command));
}

internal sealed class RuleReadOnly() : SegmentRule("read_only")
{
    public override PolicyFinding? Evaluate(SegmentPolicySubject subject) =>
        Stamp(Id, PolicyLogic.RuleReadOnly(subject.Command));
}

internal sealed class RuleExfiltration : IPolicyRule<ShellLineSubject>
{
    public string Id => "exfiltration";

    public PolicyFinding? Evaluate(ShellLineSubject subject)
    {
        var f = PolicyLogic.RuleExfiltration(subject.Commands, subject.Context);
        return f is null ? null : f with { RuleId = Id };
    }
}

internal sealed class RuleKernelInfrastructure : IPolicyRule<ShellLineSubject>
{
    public string Id => "kernel_infrastructure";

    private static readonly HashSet<string> ReservedCli = new(StringComparer.Ordinal)
        { "bcore" };

    private static readonly HashSet<string> InfraHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "postgres", "rabbitmq", "redis", "minio",
        "burbuja-postgres", "burbuja-rabbitmq", "burbuja-redis", "burbuja-minio",
    };

    private static readonly string[] ApiOrigins =
    [
        "127.0.0.1:18080", "localhost:18080", "[::1]:18080",
        "control-plane:8080", "control-plane:80",
    ];

    private static readonly System.Text.RegularExpressions.Regex ProcEnviron = new(
        @"^/proc/([^/]+)/environ$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex ContainerAws = new(
        @"^/home/app/\.aws(/|$)|^/aws/(credentials|config)(/|$)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex ComposeProject = new(
        @"^(burbuja-server|burbuja_server|burbuja-core|burbuja_core)$",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex ContainerPrefix = new(
        @"^(burbuja-server-|burbuja_server-|burbuja-core-|burbuja_core-)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public PolicyFinding? Evaluate(ShellLineSubject subject)
    {
        foreach (var cmd in subject.Commands)
        {
            var tokens = cmd.Argv.Concat(cmd.Redirects).Concat(cmd.EnvAssigns.Select(kv => kv.Key + "=" + kv.Value));
            if (ReservedCli.Contains(cmd.Verb) || cmd.EffectiveTool == "bcore")
                return Deny("self_modification", "bcore is reserved");

            foreach (var t in tokens)
            {
                var path = t.StartsWith('@') ? t[1..] : t;
                if (ProcEnviron.IsMatch(path) || path.Equals("/proc/self/environ", StringComparison.Ordinal))
                    return Deny("secrets_read", "/proc/*/environ");
                if (ContainerAws.IsMatch(path) || path.Contains("/home/app/.aws", StringComparison.OrdinalIgnoreCase))
                    return Deny("secrets_read", "container aws credentials");
                if (path.StartsWith("BURBUJA_", StringComparison.Ordinal)
                    || path.StartsWith("$BURBUJA_", StringComparison.Ordinal)
                    || path.StartsWith("${BURBUJA_", StringComparison.Ordinal))
                    return Deny("secrets_read", "BURBUJA_ env");
            }

            foreach (var key in cmd.EnvAssigns.Keys)
            {
                if (key.StartsWith("BURBUJA_", StringComparison.Ordinal))
                    return Deny("secrets_read", "BURBUJA_ env");
            }

            if (cmd.Verb is "psql" or "mysql" or "redis-cli" or "mongosh" or "pgcli" or "mycli")
            {
                var host = OptHost(cmd.Argv);
                if (host is not null && InfraHosts.Contains(host))
                    return Deny("self_modification", $"kernel {cmd.Verb} host {host}");
            }

            if (cmd.Verb is "curl" or "wget" or "http" or "httpie" or "xh")
            {
                foreach (var t in cmd.Argv)
                {
                    if (ApiOrigins.Any(o => t.Contains(o, StringComparison.OrdinalIgnoreCase)))
                        return Deny("gate_evasion", "kernel API origin");
                }
            }

            if (cmd.Verb == "docker")
            {
                var parts = ShellSegmenter.DockerArgs(cmd.Argv);
                for (var i = 0; i < cmd.Argv.Count; i++)
                {
                    if (cmd.Argv[i] is "-p" or "--project-name" && i + 1 < cmd.Argv.Count
                        && ComposeProject.IsMatch(cmd.Argv[i + 1]))
                        return Deny("self_modification", "compose project burbuja-server");
                }

                if (parts.Count > 0 && parts[0] is "kill" or "rm" or "stop" or "restart")
                {
                    var target = parts.Count > 1 ? parts[1] : "";
                    if (target.Length > 0 && ContainerPrefix.IsMatch(target))
                        return Deny("self_modification", "kernel container prefix");
                }
            }
        }

        return null;
    }

    private static string? OptHost(IReadOnlyList<string> argv)
    {
        for (var i = 1; i < argv.Count; i++)
        {
            if (argv[i] is "-h" or "--host" && i + 1 < argv.Count)
                return argv[i + 1];
            if (argv[i].StartsWith("--host=", StringComparison.Ordinal))
                return argv[i]["--host=".Length..];
        }

        return null;
    }

    private PolicyFinding Deny(string cls, string reason) =>
        new(RiskTier.HardDeny, cls, Id, reason);
}
