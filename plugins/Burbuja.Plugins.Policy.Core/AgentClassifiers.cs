using System.Text;
using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions.Agents;
using Burbuja.Agents.Abstractions.Context;
using Burbuja.Core.Abstractions.Policy;

namespace Burbuja.Plugins.Policy.Core;

/// <summary>Shared adjacency/bounds evaluation for the agent tools (M4.3).</summary>
internal static class AgentClassifierLogic
{
    public static string Target(JsonNode input) =>
        input["target"]?.GetValue<string>()
        ?? input["targetAgentId"]?.GetValue<string>()
        ?? input["agentId"]?.GetValue<string>()
        ?? "";

    public static string Mode(JsonNode input) =>
        input["mode"]?.GetValue<string>()?.ToLowerInvariant() ?? "followup";

    public static int BodyBytes(JsonNode input)
    {
        var body = input["body"]?.GetValue<string>() ?? input["text"]?.GetValue<string>() ?? "";
        return Encoding.UTF8.GetByteCount(body);
    }

    /// <summary>The kernel may narrow an Allow to Gray when the profile is read-only.</summary>
    public static PolicyFinding Narrow(PolicyContext context, PolicyFinding finding) =>
        context.Profile is PolicyProfile.ReadOnly or PolicyProfile.RestrictedProd && finding.Tier == RiskTier.Allow
            ? finding with { Tier = RiskTier.Gray, Class = "read_only" }
            : finding;
}

/// <summary>
/// Reads of another agent's context require adjacency. A missing or unparseable target, or a
/// context with no peer facts at all, escalates — it never silently allows.
/// </summary>
public sealed class AgentReadClassifier : IPolicyClassifier
{
    public bool Handles(string kind) => kind.Equals("agent.read", StringComparison.OrdinalIgnoreCase);

    public PolicyVerdict Classify(ActionPolicySubject subject)
    {
        var target = AgentClassifierLogic.Target(subject.Input);
        var peers = subject.Context.Peers;

        PolicyFinding finding;
        if (peers is null || target.Length == 0)
            finding = new PolicyFinding(RiskTier.Escalate, "agent_unknown_target", "agent.read", "no adjacency facts or unparseable target");
        else if (!AgentAuthority.MayAddress(peers, target))
            finding = new PolicyFinding(RiskTier.HardDeny, "authority_violation", "agent.read", $"non-adjacent read of '{target}'");
        else
            finding = new PolicyFinding(RiskTier.Allow, "agent_read", "agent.read", $"adjacent read of '{target}'");

        finding = AgentClassifierLogic.Narrow(subject.Context, finding);
        return new PolicyVerdict(finding.Tier, finding, [finding]);
    }
}

/// <summary>
/// Writes to another agent (context cards, messages). Inject is only ever allowed towards a direct
/// child; every other target must be adjacent, and the payload is bounded.
/// </summary>
public sealed class AgentWriteClassifier : IPolicyClassifier
{
    public const int MaxBodyBytes = ContextBudget.MaxCardBytes;

    public bool Handles(string kind) => kind.Equals("agent.write", StringComparison.OrdinalIgnoreCase);

    public PolicyVerdict Classify(ActionPolicySubject subject)
    {
        var target = AgentClassifierLogic.Target(subject.Input);
        var peers = subject.Context.Peers;
        var mode = AgentClassifierLogic.Mode(subject.Input);

        PolicyFinding finding;
        if (peers is null || target.Length == 0)
            finding = new PolicyFinding(RiskTier.Escalate, "agent_unknown_target", "agent.write", "no adjacency facts or unparseable target");
        else if (mode == "inject" && !AgentAuthority.MayInject(peers, target))
            finding = new PolicyFinding(RiskTier.HardDeny, "authority_violation", "agent.write", $"inject to a non-child '{target}'");
        else if (!AgentAuthority.MayAddress(peers, target))
            finding = new PolicyFinding(RiskTier.HardDeny, "authority_violation", "agent.write", $"non-adjacent write to '{target}'");
        else if (AgentClassifierLogic.BodyBytes(subject.Input) > MaxBodyBytes)
            finding = new PolicyFinding(RiskTier.HardDeny, "bounds_exceeded", "agent.write", $"payload over {MaxBodyBytes} bytes");
        else
            finding = new PolicyFinding(RiskTier.Allow, "agent_write", "agent.write", $"adjacent write to '{target}'");

        finding = AgentClassifierLogic.Narrow(subject.Context, finding);
        return new PolicyVerdict(finding.Tier, finding, [finding]);
    }
}
