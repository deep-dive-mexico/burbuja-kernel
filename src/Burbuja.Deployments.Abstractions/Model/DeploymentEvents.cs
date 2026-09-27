namespace Burbuja.Deployments.Abstractions;

public static class DeploymentEvents
{
    public const string Accepted = "deployments.deployment.accepted";
    public const string Started = "deployments.deployment.started";
    public const string WaveStarted = "deployments.deployment.wave_started";
    public const string ApprovalRequested = "deployments.deployment.approval_requested";
    public const string ApprovalResolved = "deployments.deployment.approval_resolved";
    public const string Canceling = "deployments.deployment.canceling";
    public const string Succeeded = "deployments.deployment.succeeded";
    public const string Failed = "deployments.deployment.failed";
    public const string Canceled = "deployments.deployment.canceled";
    public const string StepStarted = "deployments.step.started";
    public const string StepSucceeded = "deployments.step.succeeded";
    public const string StepFailed = "deployments.step.failed";
    public const string StepSkipped = "deployments.step.skipped";
    public const string PolicyIntent = "deployments.step.policy_intent";
    public const string PolicyDecision = "deployments.step.policy_decision";
}
