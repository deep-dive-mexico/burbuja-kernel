using System.Text.Json.Nodes;

namespace Burbuja.Deployments.Abstractions;

public sealed record TargetPlanRequest(
    string EnvironmentId,
    DeploymentMode Mode,
    IReadOnlyList<DeploymentResource> Desired);

public sealed record PlannedChange(
    string Resource,
    DeploymentResourceKind Kind,
    StepChange Change,
    JsonObject Spec);

public sealed record TargetPlan(IReadOnlyList<PlannedChange> Changes);

public sealed record StepRequest(
    string EnvironmentId,
    string DeploymentId,
    string Resource,
    DeploymentResourceKind Kind,
    StepChange Change,
    JsonObject Spec,
    JsonObject Parameters,
    JsonObject PriorOutputs,
    int Attempt,
    TimeSpan Timeout,
    bool ApprovalGranted = false);

public sealed record StepResult(
    StepStatus Status,
    JsonObject? Outputs = null,
    string? Error = null,
    bool NeedsApproval = false,
    string? RiskClass = null,
    string? Preview = null);

public sealed record TargetProbe(string EnvironmentId, bool Ready, string? Detail);

public interface IDeploymentTarget
{
    string Kind { get; }
    IReadOnlyList<DeploymentResourceKind> SupportedResourceKinds { get; }
    ValueTask<TargetPlan> PlanAsync(TargetPlanRequest request, CancellationToken ct);
    ValueTask<StepResult> ApplyAsync(StepRequest request, CancellationToken ct);
    ValueTask<StepResult> DestroyAsync(StepRequest request, CancellationToken ct);
    ValueTask<TargetProbe> ProbeAsync(string environmentId, CancellationToken ct);
}
