using System.Text.Json.Nodes;

namespace Burbuja.Deployments.Abstractions;

public enum DeploymentMode
{
    Incremental,
    Complete,
}

public enum DeploymentExecutionState
{
    Pending,
    Planning,
    Running,
    AwaitingApproval,
    Canceling,
    Succeeded,
    Failed,
    Canceled,
}

public enum DeploymentResourceKind
{
    Container,
    Process,
    Queue,
    Bucket,
    Database,
    Secret,
    Function,
}

public enum StepChange
{
    Create,
    Update,
    Delete,
    NoOp,
}

public enum StepStatus
{
    Pending,
    Skipped,
    Running,
    AwaitingApproval,
    Succeeded,
    Failed,
    Canceled,
    Deleted,
}

public sealed class DeploymentResource
{
    public string Name { get; set; } = "";
    public DeploymentResourceKind Kind { get; set; } = DeploymentResourceKind.Process;
    public JsonObject Spec { get; set; } = [];
    public List<string> DependsOn { get; set; } = [];
    public bool RequiresApproval { get; set; }
}

public sealed class DeploymentTemplate
{
    public string Schema { get; set; } = DeploymentsConstants.TemplateSchema;
    public List<DeploymentResource> Resources { get; set; } = [];
}

public sealed class DeploymentStep
{
    public string Resource { get; set; } = "";
    public DeploymentResourceKind Kind { get; set; }
    public StepChange Change { get; set; }
    public int Wave { get; set; }
    public StepStatus Status { get; set; } = StepStatus.Pending;
    public int Attempt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? Error { get; set; }
    public JsonObject? Outputs { get; set; }
    public string? ApprovalId { get; set; }
}

public sealed class DeploymentProperties
{
    public DeploymentTemplate Template { get; set; } = new();
    public JsonObject Parameters { get; set; } = [];
    public DeploymentMode Mode { get; set; } = DeploymentMode.Incremental;
    public bool RequiresApproval { get; set; }
    public DeploymentExecutionState ExecutionState { get; set; } = DeploymentExecutionState.Pending;
    public List<DeploymentStep> Steps { get; set; } = [];
    public JsonObject? Outputs { get; set; }
    public string? OperationId { get; set; }
    public string? TemplateHash { get; set; }
    public string? PendingApprovalId { get; set; }
    public int RunId { get; set; }
    public int PercentComplete { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
}

public static class DeploymentExecution
{
    public static bool IsTerminal(DeploymentExecutionState state) =>
        state is DeploymentExecutionState.Succeeded
            or DeploymentExecutionState.Failed
            or DeploymentExecutionState.Canceled;
}
