namespace Burbuja.Portal.Services;

/// <summary>
/// The wire names of the <c>pomnibot.job</c> stream, as the portal reads them.
///
/// The portal must not reference the pomnibot assemblies (they are another application's plugins,
/// loaded into their own ALC), so the names the trace views bind are declared here as strings —
/// exactly the ones the catalogue in pomnibot's plan lists and the ones a live host emits.
/// </summary>
public static class PomnibotEventTypes
{
    private const string Prefix = "pomnibot.job.";

    public const string Accepted = Prefix + "accepted";
    public const string Intake = Prefix + "intake";
    public const string ConfirmRequested = Prefix + "confirm_requested";
    public const string Started = Prefix + "started";
    public const string Resumed = Prefix + "resumed";
    public const string Progress = Prefix + "progress";
    public const string Note = Prefix + "note";
    public const string AssistantText = Prefix + "assistant.text";
    public const string ToolUse = Prefix + "tool.use";
    public const string ToolIntent = Prefix + "tool.intent";
    public const string ToolDecision = Prefix + "tool.decision";
    public const string ToolResult = Prefix + "tool.result";
    public const string ToolDenied = Prefix + "tool.denied";
    public const string Supervisor = Prefix + "supervisor";
    public const string MentorScout = Prefix + "mentor.scout";
    public const string MentorConsult = Prefix + "mentor.consult";
    public const string MentorReview = Prefix + "mentor.review";
    public const string ApprovalRequested = Prefix + "approval.requested";
    public const string ApprovalResolved = Prefix + "approval.resolved";
    public const string Compact = Prefix + "compact";
    public const string StateCard = Prefix + "state_card";
    public const string Subagent = Prefix + "subagent";
    public const string Steering = Prefix + "steering";
    public const string Verify = Prefix + "verify";
    public const string BudgetWarned = Prefix + "budget.warned";
    public const string BudgetExtended = Prefix + "budget.extended";
    public const string Deferred = Prefix + "deferred";
    public const string Finalizing = Prefix + "finalizing";
    public const string Delivery = Prefix + "delivery";
    public const string Report = Prefix + "report";
    public const string Qa = Prefix + "qa";
    public const string HostAction = Prefix + "host.action";
    public const string EngineSpawned = Prefix + "engine.spawned";
    public const string AgentLinked = Prefix + "agent.linked";
    public const string Succeeded = Prefix + "succeeded";
    public const string Failed = Prefix + "failed";
    public const string Cancelled = Prefix + "cancelled";

    /// <summary>
    /// The kernel's own approval lifecycle frames ride the same stream as the job's events, so the
    /// portal reads them here rather than pretending the approval only exists in the inbox.
    /// </summary>
    public const string KernelApprovalOpened = "core.approval.opened";

    public const string KernelApprovalGranted = "core.approval.granted";
    public const string KernelApprovalApproved = "core.approval.approved";
    public const string KernelApprovalDenied = "core.approval.denied";
    public const string KernelApprovalAnswered = "core.approval.answered";
    public const string KernelApprovalTimedOut = "core.approval.timed_out";
    public const string KernelApprovalCancelled = "core.approval.cancelled";
}
