using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Policy;
using Burbuja.Core.Sdk.Policy;

namespace Burbuja.Core.Policy;

/// <summary>
/// The path a file tool is talking about. The Claude Code tools send <c>file_path</c> (Read,
/// Write, Edit) while this kernel's own workspace tools send <c>path</c>; reading only the latter
/// meant every Claude file call was classified against the *cwd*, so <c>Read ~/.ssh/id_rsa</c>
/// came back "Read inside workspace" — an allow. Missing the key is a fail-open here, so every
/// known spelling is accepted and an absent path is treated as "unknown", never as "safe".
/// </summary>
internal static class FileToolInput
{
    private static readonly string[] PathKeys = ["file_path", "notebook_path", "path", "file"];

    public static string Path(JsonNode input)
    {
        foreach (var key in PathKeys)
        {
            if (input[key]?.GetValue<string>() is { Length: > 0 } value)
                return value;
        }

        return "";
    }
}

public sealed class FsWriteClassifier : IPolicyClassifier
{
    public bool Handles(string kind) =>
        kind.Equals("fs.write", StringComparison.OrdinalIgnoreCase);

    public PolicyVerdict Classify(ActionPolicySubject subject)
    {
        var path = FileToolInput.Path(subject.Input);
        if (path.Length == 0)
        {
            // Same rule as the read side: a write with no target is a question, never a yes.
            var unknown = new PolicyFinding(RiskTier.Gray, "unknown_path", "fs.write", "Write without a path");
            return new PolicyVerdict(unknown.Tier, unknown, [unknown]);
        }

        var abs = PathClassifier.ExpandPath(path, subject.Context.WorkRoot);
        var real = PathClassifier.RealPathLoose(abs);
        PolicyFinding finding;
        if (PolicyLogic.IsSelfModTarget(real, subject.Context) || PolicyLogic.IsProtectedHomePath(real))
            finding = new PolicyFinding(RiskTier.HardDeny, "self_modification", "fs.write", $"Write to protected path {path}");
        else if (!PolicyLogic.IsWritableTarget(real, subject.Context))
            finding = new PolicyFinding(RiskTier.HardDeny, "destructive_fs", "fs.write", $"Write outside writable root: {path}");
        else
            finding = new PolicyFinding(RiskTier.Allow, "worktree_write", "fs.write", "Write inside writable root");
        if (subject.Context.Profile is PolicyProfile.ReadOnly or PolicyProfile.RestrictedProd
            && finding.Tier == RiskTier.Allow)
            finding = finding with { Tier = RiskTier.Escalate, Class = "read_only" };
        return new PolicyVerdict(finding.Tier, finding, [finding]);
    }
}

public sealed class FsReadClassifier : IPolicyClassifier
{
    public bool Handles(string kind) =>
        kind.Equals("fs.read", StringComparison.OrdinalIgnoreCase);

    public PolicyVerdict Classify(ActionPolicySubject subject)
    {
        var path = FileToolInput.Path(subject.Input);
        if (path.Length == 0)
        {
            // No path at all is not "inside the workspace": an unknown target escalates.
            var unknown = new PolicyFinding(RiskTier.Gray, "unknown_path", "fs.read", "Read without a path");
            return new PolicyVerdict(unknown.Tier, unknown, [unknown]);
        }

        var abs = PathClassifier.ExpandPath(path, subject.Context.WorkRoot);
        var real = PathClassifier.RealPathLoose(abs);
        PolicyFinding finding;
        if (real.Contains("/.aws/", StringComparison.Ordinal) || real.Contains("/.ssh/", StringComparison.Ordinal)
            || real.EndsWith("/.netrc", StringComparison.Ordinal) || real.Contains("/home/app/.aws", StringComparison.Ordinal)
            || real.StartsWith("/aws/", StringComparison.Ordinal))
            finding = new PolicyFinding(RiskTier.HardDeny, "secrets_read", "fs.read", $"Read of credential path {path}");
        else if (PolicyLogic.IsProtectedHomePath(real))
            finding = new PolicyFinding(RiskTier.Gray, "env_read", "fs.read", $"Read of protected path {path}");
        else
            finding = new PolicyFinding(RiskTier.Allow, "read", "fs.read", "Read inside workspace");
        return new PolicyVerdict(finding.Tier, finding, [finding]);
    }
}

public sealed class HttpClassifier : IPolicyClassifier
{
    public bool Handles(string kind) =>
        kind.Equals("http", StringComparison.OrdinalIgnoreCase);

    public PolicyVerdict Classify(ActionPolicySubject subject)
    {
        var url = subject.Input["url"]?.GetValue<string>() ?? "";
        var method = (subject.Input["method"]?.GetValue<string>() ?? "GET").ToUpperInvariant();
        var host = "";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            host = uri.Host;
        PolicyFinding finding;
        if (host.Length == 0)
            finding = new PolicyFinding(RiskTier.Escalate, "unknown_host", "http", "HTTP without a parseable host");
        else if (PolicyLogic.IsPiHost(host) && method is not "GET" and not "HEAD")
            finding = new PolicyFinding(RiskTier.HardDeny, "pi_mutation", "http", $"Non-GET HTTP to protected host {host}");
        else if (PolicyLogic.IsPiHost(host))
            finding = new PolicyFinding(RiskTier.Allow, "pi_read", "http", $"GET {host}");
        else if (PolicyLogic.MatchesAnyHost(host, subject.Context.DevHosts))
            finding = new PolicyFinding(RiskTier.Allow, "dev_http", "http", $"HTTP to allowlisted host {host}");
        else if (method is "POST" or "PUT" or "PATCH" or "DELETE")
            finding = new PolicyFinding(RiskTier.Gray, "external_write", "http", $"HTTP {method} {host}");
        else
            finding = new PolicyFinding(RiskTier.Gray, "external_read", "http", $"HTTP GET {host}");
        return new PolicyVerdict(finding.Tier, finding, [finding]);
    }
}
