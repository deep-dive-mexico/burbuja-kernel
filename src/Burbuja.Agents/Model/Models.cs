using Burbuja.Agents.Abstractions.Domain;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Agents.Abstractions.Orchestration;
using Burbuja.Agents.Runtime;

namespace Burbuja.Agents.Model;

public sealed class WorkspaceProperties
{
    public string DisplayName { get; set; } = "";
    public string? RepoId { get; set; }
    public string? Remote { get; set; }
    public string BaseBranch { get; set; } = "main";
    public List<string> ProtectedBranches { get; set; } = ["main"];
    public List<string> PushAllowedPrefixes { get; set; } = ["agent/"];
    public bool PullRequests { get; set; }
    public List<string> Verify { get; set; } = [];
    public List<string> Bootstrap { get; set; } = [];
    public decimal? DefaultMaxUsd { get; set; }
    public int? DefaultMaxTurns { get; set; }
    public string? PersonaRef { get; set; }
    public string Profile { get; set; } = nameof(PermissionProfile.DevFullAuto);
    public string DefaultStrategy { get; set; } = "single-worker";
    public int Revision { get; set; } = 1;

    /// <summary>
    /// K3: what this workspace holds — job counts by status, its spend, its schedules and its most recent
    /// jobs. Filled when ONE workspace is read and null on every row of a list: a list of a hundred
    /// workspaces must not scan a hundred job sets, and a reader who wants the hub asks for the workspace by
    /// name.
    /// </summary>
    public WorkspaceHub? Hub { get; set; }

    // ---------------------------------------------------------------- W1: the lifecycle

    /// <summary>
    /// W1: this PUT asks the host to provision the workspace's defaults (its compute binding, and later its
    /// endpoint, its connections and its event subscriptions).
    ///
    /// <para><b>Why an explicit flag rather than always provisioning.</b> Every existing caller — pomnibot's
    /// client, the E2E suites, a person writing a workspace by hand — depends on a workspace PUT being an
    /// ordinary resource write that answers 200/201 now. Provisioning is what turns it into a 202 with an
    /// operation to poll, so it has to be ASKED for, and the other trigger is a caller naming a
    /// <c>computeWorkspaceId</c> that does not exist yet.</para>
    /// </summary>
    public bool Initialize { get; set; }

    /// <summary>
    /// W1: the <c>Burbuja.Compute/workspaces</c> resource this workspace runs on. Absent until an initialize
    /// binds one; a caller may name its own, and a name that does not exist yet is itself a request to
    /// provision.
    /// </summary>
    public string? ComputeWorkspaceId { get; set; }

    /// <summary>
    /// W1: <c>Provisioning | Ready | Failed</c>, or null on a workspace nobody asked to initialize. It is a
    /// value on the resource rather than a fact in a log because a console has to render it beside the
    /// workspace, and a reader who has to reconstruct it from events is reading a second representation.
    /// </summary>
    public string? Lifecycle { get; set; }

    /// <summary>The operation an initialize is running under, so a caller can poll it after a reload.</summary>
    public string? OperationId { get; set; }

    /// <summary>
    /// W1: bumped when the host actually provisions something for this workspace — not on every PUT. That is
    /// what makes "re-initialize reconciles" checkable: a second initialize of an unchanged workspace leaves
    /// this number alone.
    /// </summary>
    public int Generation { get; set; }

    /// <summary>Why the last initialize failed, cleared when one succeeds.</summary>
    public string? LifecycleError { get; set; }
}

public sealed class JobProperties
{
    public JobSpec Spec { get; set; } = new();
    public JobExecutionStatus Status { get; set; } = JobExecutionStatus.Queued;
    public JobBudgets? Budgets { get; set; }
    public JobCost Cost { get; set; } = new(0, 0, 0, 0, 0, 0);
    public string? WorkspaceRevision { get; set; }
    public string? SpecHash { get; set; }
    public string? OperationId { get; set; }
    public int RunId { get; set; }
    public int RunAttempts { get; set; }
    public AgentRunState? State { get; set; }
    public string? PendingApprovalId { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? WorkerNodeId { get; set; }
    public List<string> Notes { get; set; } = [];
    public Guid? ActiveRunId { get; set; }
    public Guid? GroupId { get; set; }
    public JobRecoveryState? Recovery { get; set; }
    public bool CostIsPartial { get; set; }

    /// <summary>
    /// The stateful agent this job runs as, and its team. Both feed the adjacency facts of
    /// `PolicyContext.Peers` (M4.3): a job that runs as no agent has no peers, which escalates
    /// every agent-to-agent tool call instead of allowing it.
    /// </summary>
    public string? AgentId { get; set; }

    public string? TeamId { get; set; }

    /// <summary>The model this job's turns are sent to and quoted at; null means the host default.</summary>
    public string? ModelId { get; set; }

    /// <summary>Per-job thinking effort, passed through to the provider (null = provider default).</summary>
    public string? Effort { get; set; }

    /// <summary>The compute workspace the job is bound to, when a compute plugin supplied one.</summary>
    public string? ComputeWorkspaceId { get; set; }
}

/// <summary>One durable plan per job generation; run identity and completed workers survive process loss.</summary>
public sealed class JobRecoveryState
{
    public string Strategy { get; set; } = "";
    public string? FenceToken { get; set; }
    public Guid? GroupId { get; set; }
    public FanOutPlan? Plan { get; set; }
    public List<JobRunCheckpoint> Runs { get; set; } = [];
}

public sealed class JobRunCheckpoint
{
    public Guid RunId { get; set; }
    public string Label { get; set; } = "";
    public AgentRunRole Role { get; set; } = AgentRunRole.Worker;
    public string? Brief { get; set; }
    public AgentRunState? State { get; set; }
    public JobExecutionStatus Status { get; set; } = JobExecutionStatus.Queued;
    public string? PendingApprovalId { get; set; }
    public string? Error { get; set; }
    public bool ModelInFlight { get; set; }
    public int InterruptedModelCalls { get; set; }
    public DateTimeOffset? ModelStartedAt { get; set; }
    public LlmResponse? PendingResponse { get; set; }
    public bool ResponseBooked { get; set; }
    public int LlmMs { get; set; }
    public decimal CostUsd { get; set; }
    public LlmStopReason? LastStopReason { get; set; }
}

/// <summary>`ledgers`: one read-only day of the runtime's durable budget ledger (M5.1).</summary>
public sealed class LedgerDayProjection
{
    public string Day { get; set; } = "";
    public decimal SpentUsd { get; set; }
    public int JobCount { get; set; }
    public List<LedgerEntry> Bookings { get; set; } = [];

    /// <summary>
    /// M10 H3 — the stop this spend is measured against, or null when the host has none configured.
    ///
    /// <para><b>Why it is on the ledger and not read from configuration by the page.</b> The console is a
    /// CLIENT: it holds a key, not the host's environment, and a page that read the limit from its own
    /// configuration would be a second opinion about a number only the host can enforce. Reporting
    /// <c>spentUsd</c> without the ceiling it is compared to is how "$3.00 spent" reads as fine right up until
    /// the first refusal — the console shows <c>spent / stop</c> and the HARD STOP pill from this one field.
    /// It is read fresh on every GET, so raising the limit shows on the next read rather than on restart.</para>
    /// </summary>
    public decimal? HardStopUsd { get; set; }

    /// <summary>True at or above the stop — the same comparison the three refusal paths make.</summary>
    public bool HardStopReached { get; set; }
}

public sealed class RunProperties
{
    public Guid RunId { get; set; }
    public Guid JobId { get; set; }
    public Guid? ParentRunId { get; set; }
    public Guid? GroupId { get; set; }
    public AgentRunRole Role { get; set; } = AgentRunRole.Worker;
    public int Attempt { get; set; }
    public string? WorkerNodeId { get; set; }
    public decimal CostUsd { get; set; }
    public JobExecutionStatus Status { get; set; } = JobExecutionStatus.Queued;
    public List<InputMessage> Transcript { get; set; } = [];
}

public sealed class ScheduleProperties
{
    public string Cron { get; set; } = "";
    public bool Paused { get; set; }
    public JobSpec? Template { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
}

public sealed class NodeProjection
{
    public string NodeId { get; set; } = "";
    public DateTimeOffset LastHeartbeat { get; set; }
    public int CapacityConfigured { get; set; }
    public int CapacityCurrent { get; set; }
    public string Status { get; set; } = "ready";
}

public sealed class ApprovalProjection
{
    public string ApprovalId { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string? DecisionKey { get; set; }
    public string? RiskClass { get; set; }
    public string? Preview { get; set; }
}

public static class JobExecution
{
    public static bool IsTerminal(JobExecutionStatus status) =>
        status is JobExecutionStatus.Succeeded
            or JobExecutionStatus.Failed
            or JobExecutionStatus.Cancelled
            or JobExecutionStatus.BudgetExceeded
            or JobExecutionStatus.TurnsExceeded
            // M10 H3: a job parked at the day's hard stop is DONE running — the refusal happens at the top
            // of a run, so a terminal reading here is what stops the outbox from re-dispatching it in a loop
            // against a limit that will not move until tomorrow. Re-PUTting the job clears it (PutAsync
            // rewrites the status), and that is the sanctioned way to try again.
            or JobExecutionStatus.Blocked;
}
