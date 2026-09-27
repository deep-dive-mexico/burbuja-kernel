using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Agents.Abstractions.Orchestration;
using Burbuja.Core.Abstractions.Policy;

namespace Burbuja.Agents.Abstractions.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JobKind
{
    Implement,
    Investigate,
    Review,
    Research,
    Briefing,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TargetEnv
{
    Dev,
    Prod,
    None,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PermissionProfile
{
    DevFullAuto,
    ProdReadonly,
    ReadOnly,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JobExecutionStatus
{
    Queued,
    Starting,
    Running,
    AwaitingApproval,
    Paused,
    /// <summary>
    /// M10 H3 — the day's hard stop was reached and the job was refused before spending again. A state of
    /// its own rather than <see cref="AwaitingApproval"/>: a human approves a decision, and nobody can
    /// approve money the host does not have. It is not a verdict on the work — the job did nothing wrong —
    /// which is why re-PUTting it re-queues it with a fresh run.
    /// </summary>
    Blocked,
    Succeeded,
    Failed,
    Cancelled,
    BudgetExceeded,
    TurnsExceeded,
}

public sealed record JobSpec
{
    public JobKind Kind { get; init; } = JobKind.Implement;
    public string? RepoId { get; init; }
    public TargetEnv TargetEnv { get; init; } = TargetEnv.Dev;
    public string Title { get; init; } = "";
    public string Slug { get; init; } = "";
    public string Brief { get; init; } = "";
    public decimal? MaxBudgetUsd { get; init; }
    public int? MaxTurns { get; init; }
    public string Language { get; init; } = "en";
    public double Confidence { get; init; }
    public string? Clarify { get; init; }
    public string? Strategy { get; init; }
    public bool AutoAccept { get; init; } = true;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed record JobBudgets(decimal MaxUsd, int MaxTurns, decimal DailySoftCapUsd, decimal DailyHardStopUsd);

public sealed record JobCost(
    decimal WorkerUsd,
    decimal IntakeUsd,
    decimal SupervisorUsd,
    decimal MentorUsd,
    int InputTokens,
    int OutputTokens)
{
    public decimal TotalUsd => WorkerUsd + IntakeUsd + SupervisorUsd + MentorUsd;
}

public sealed record Job(
    Guid JobId,
    JobSpec Spec,
    JobExecutionStatus Status,
    JobBudgets Budgets,
    JobCost Cost,
    string WorkspaceRevision,
    IReadOnlyDictionary<string, string> Tags);

public sealed record ToolIntent(
    string ToolUseId,
    string ToolName,
    JsonNode RedactedInput,
    RiskTier Tier,
    string Class,
    string DecisionKey,
    string? ShapeKey,
    string? Fingerprint);

public sealed record PendingToolBatch(
    string BatchId,
    IReadOnlyList<ToolUseBlock> Calls,
    IReadOnlyDictionary<string, ToolResultBlock>? Completed);

public sealed record AgentRunState(
    int Version,
    int Turn,
    int ConsecutiveFailures,
    int MaxTokenContinues,
    int BudgetExtensions,
    bool FinishNudged,
    PendingToolBatch? PendingBatch,
    string? AwaitingApprovalId,
    string? LeaseToken,
    IReadOnlyList<InputMessage> Transcript,
    string? WorkspacePath,
    string? WorkspaceBranch,
    string? WorkspaceBaseSha,
    string? VolumeId);

public sealed record AgentRun(
    Guid RunId,
    Guid JobId,
    Guid? ParentRunId,
    Guid? GroupId,
    AgentRunRole Role,
    int Attempt,
    string? WorkerNodeId,
    decimal CostUsd);

public static class AgentEventTypes
{
    public const string Init = "agents.init";
    public const string AssistantText = "agents.assistant.text";
    public const string ToolUse = "agents.tool.use";
    public const string ToolCall = "agents.tool.call";
    public const string ToolResult = "agents.tool.result";
    public const string ToolDenied = "agents.tool.denied";
    public const string ToolIntent = "agents.tool.intent";
    public const string Supervisor = "agents.supervisor";
    public const string Approval = "agents.approval";
    public const string Subagent = "agents.subagent";
    public const string Compact = "agents.compact";
    public const string Result = "agents.result";
    public const string Error = "agents.error";
    public const string Note = "agents.note";
    public const string Resume = "agents.resume";
    public const string Progress = "agents.progress";
    public const string Decision = "agents.decision";
    public const string SteeringReceived = "agents.steering.received";
    public const string BudgetExceeded = "agents.budget.exceeded";
    public const string BudgetExtended = "agents.budget.extended";
    public const string QaLog = "agents.qa.log";

    /// <summary>
    /// M9 T1 — the per-turn telemetry pair. `started` opens a turn and `completed` closes it with what
    /// the turn actually cost in TIME (llmMs), what the model reported (usage) and which ledger entry the
    /// money went to (bookingId). Before this pair the stream recorded what happened but never how long it
    /// took, so a trajectory's durations were the deltas between row timestamps — a property of when rows
    /// were written, not of how long the model ran.
    /// </summary>
    public const string TurnStarted = "agents.turn.started";

    /// <inheritdoc cref="TurnStarted"/>
    public const string TurnCompleted = "agents.turn.completed";
}
