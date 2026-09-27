namespace Burbuja.Deployments;

public sealed class DeploymentsOptions
{
    public const string Section = "Deployments";

    public LocalProcessOptions LocalProcess { get; set; } = new();
}

public sealed class LocalProcessOptions
{
    public string Root { get; set; } = Path.Combine(Path.GetTempPath(), "burbuja-deployments");
}
