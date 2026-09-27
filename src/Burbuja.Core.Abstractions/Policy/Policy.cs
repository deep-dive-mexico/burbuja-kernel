using System.Text.Json.Nodes;

namespace Burbuja.Core.Abstractions.Policy;

/// <summary>
/// How severe a policy verdict is. The ordering IS the lattice — <c>a &gt; b</c> is "a is more severe" —
/// so a new tier must be placed where its severity puts it, not appended for convenience.
/// </summary>
public enum RiskTier
{
    Allow = 0,
    Gray = 1,
    Escalate = 2,
    HardDeny = 3,

    /// <summary>
    /// A7: the host will not do this, and no approval reaches it.
    ///
    /// <para>The distinction from <see cref="HardDeny"/> is not cosmetic. A deny is a decision policy
    /// made about a request; a forbidden transition is a decision about what this host IS — the real-
    /// broker case, the un-grantable action. It never opens an approval, so there is no card to tap and
    /// no grant that satisfies it, which is the only way to express "not even the owner can authorise
    /// this from here".</para>
    /// </summary>
    Forbidden = 4,

    /// <summary>
    /// A5: the request cannot be judged, because something it needs was not declared.
    ///
    /// <para>The most severe tier, because a request nobody can evaluate must not proceed: "clarify
    /// grants nothing" is a rule, not a courtesy. It is deliberately NOT a flavour of
    /// <see cref="Escalate"/> — an escalation is a decision waiting for a human, and this is a request
    /// waiting for its own author to finish writing it.</para>
    /// </summary>
    Clarify = 5,
}

public enum PolicyProfile
{
    RestrictedProd = 0,
    ReadOnly = 1,
    FullAuto = 2,
}

public static class PolicyProfileParser
{
    public static PolicyProfile Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return PolicyProfile.RestrictedProd;
        var n = value.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        if (n.Equals("fullauto", StringComparison.OrdinalIgnoreCase)
            || n.Equals("devfullauto", StringComparison.OrdinalIgnoreCase))
            return PolicyProfile.FullAuto;
        if (n.Equals("readonly", StringComparison.OrdinalIgnoreCase)
            || n.Equals("prodreadonly", StringComparison.OrdinalIgnoreCase))
            return PolicyProfile.ReadOnly;
        if (n.Equals("restrictedprod", StringComparison.OrdinalIgnoreCase)
            || n.Equals("prod", StringComparison.OrdinalIgnoreCase))
            return PolicyProfile.RestrictedProd;
        return PolicyProfile.RestrictedProd;
    }
}

public interface IPathProbe
{
    bool Exists(string path);
    string RealPath(string path);
    bool Contains(string parent, string child);
}

public sealed record PolicyContext(
    string StreamId,
    string WorkRoot,
    string ArtifactDir,
    PolicyProfile Profile,
    string TargetEnv,
    string HostRepoPath,
    IReadOnlyList<string> VerifyCommands,
    IReadOnlyList<string> DevHosts,
    IReadOnlyList<string> AllowedSecretIds,
    string? Branch = null,
    IReadOnlyList<string>? TrustedPaths = null,
    IReadOnlyList<string>? ProtectedHosts = null,
    IReadOnlyList<string>? AllowedAwsProfiles = null,
    IPathProbe? Probe = null,
    /// <summary>Kernel-supplied adjacency facts (<c>parent:agt-root</c>, <c>child:agt-b</c>, <c>team:tm-1</c>). Model arguments are never rewritten.</summary>
    IReadOnlyList<string>? Peers = null);

public sealed record ShellCommand(
    string Raw,
    IReadOnlyList<string> Argv,
    IReadOnlyList<string> ArgvRaw,
    string Verb,
    string VerbRaw,
    string EffectiveTool,
    IReadOnlyDictionary<string, string> EnvAssigns,
    IReadOnlyList<string> Redirects,
    string Cwd,
    string? Wrapper,
    IReadOnlyList<string> PriorVerbs);

public sealed record ActionPolicySubject(
    string Kind,
    JsonNode Input,
    PolicyContext Context);

public sealed record SegmentPolicySubject(ShellCommand Command, PolicyContext Context);

public sealed record ShellLineSubject(IReadOnlyList<ShellCommand> Commands, PolicyContext Context);

public sealed record PolicyFinding(
    RiskTier Tier,
    string Class,
    string RuleId,
    string Reason,
    string? Fingerprint = null,
    /// <summary>
    /// A5: the permissions the request needed and did not declare, verbatim and complete. A CLARIFY that
    /// said only "insufficient permissions" would make the caller guess, and a guess is what produced the
    /// incomplete request in the first place.
    /// </summary>
    IReadOnlyList<Permission>? Missing = null);

public sealed record PolicyVerdict(RiskTier Tier, PolicyFinding Finding, IReadOnlyList<PolicyFinding> All);

public interface IPolicyRule<TSubject>
{
    string Id { get; }
    PolicyFinding? Evaluate(TSubject subject);
}

public interface IPolicyClassifier
{
    bool Handles(string kind);
    PolicyVerdict Classify(ActionPolicySubject subject);
}

public interface IPolicyPipeline<TSubject>
{
    PolicyVerdict Classify(TSubject subject);
    string Fingerprint { get; }
}

public interface IShellCommandParser
{
    IReadOnlyList<ParsedShellSegment> Parse(string command);
}

public sealed record ParsedShellSegment(
    IReadOnlyList<string> Argv,
    string? Executable,
    IReadOnlyList<string> Redirects,
    bool Unparseable);
