namespace Burbuja.Core.Arm;

public static class EventStreamIds
{
    public static string Approval(string approvalId) => $"core.approval/{approvalId.ToLowerInvariant()}";
    public static string Deployment(string canonicalId) => $"deployments.deployment/{canonicalId.ToLowerInvariant()}";
    public static string AgentJob(string canonicalId) => $"agents.job/{canonicalId.ToLowerInvariant()}";
    public static string Resource(string canonicalId) => canonicalId.Trim().ToLowerInvariant();
}
