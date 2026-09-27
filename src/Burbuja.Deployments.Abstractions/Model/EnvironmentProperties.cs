namespace Burbuja.Deployments.Abstractions;

public sealed class EnvironmentTarget
{
    public string Kind { get; set; } = "in-memory";
    public string? WorkRoot { get; set; }
    public string? Profile { get; set; }
}

public sealed class EnvironmentDefaults
{
    public int MaxParallel { get; set; } = 4;
    public TimeSpan StepTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan ApprovalTimeout { get; set; } = TimeSpan.FromMinutes(15);
    public bool RequireApprovalForDeletes { get; set; }
}

public sealed class EnvironmentProperties
{
    public string DisplayName { get; set; } = "";
    public EnvironmentTarget Target { get; set; } = new();
    public EnvironmentDefaults Defaults { get; set; } = new();
}
