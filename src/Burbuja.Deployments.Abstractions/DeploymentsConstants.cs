namespace Burbuja.Deployments.Abstractions;

public static class DeploymentsConstants
{
    public const string Namespace = "Burbuja.Deployments";
    public const string ApiVersion = "2026-09-01";
    public const string TopicExecute = "deployments.execute";
    public const string StreamType = "deployments.deployment";
    public const string TemplateSchema = "burbuja.deployments/v1";

    public static string StreamId(string canonicalId) =>
        "deployments.deployment/" + canonicalId.ToLowerInvariant();

    public static string LeaseId(string key) => "deployments/" + key.ToLowerInvariant();
}
