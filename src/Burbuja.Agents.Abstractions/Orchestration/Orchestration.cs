using System.Text.Json.Serialization;
using Burbuja.Agents.Abstractions.Domain;

namespace Burbuja.Agents.Abstractions.Orchestration;

public sealed record PlanSubtask(
    string Id,
    string Title,
    string Brief,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> DependsOn,
    decimal EstimateUsd);

public sealed record FanOutPlan(
    IReadOnlyList<PlanSubtask> Subtasks,
    string MergeStrategy,
    IReadOnlyList<string> Risks,
    double Confidence);

/// <summary>Everything a strategy needs to act on a job without reaching into the runner.</summary>
public sealed record OrchestrationContext(
    string JobId,
    string JobKey,
    string? Strategy,
    JobSpec Spec);

public sealed record RunOutcome(Guid RunId, string Status, bool Terminal, decimal CostUsd);

/// <summary>What a strategy republished after a restart (empty when nothing was stuck).</summary>
public sealed record StrategyRecovery(IReadOnlyList<string> RepublishedTaskIds);

public interface IOrchestrationStrategy
{
    string Id { get; }

    ValueTask StartAsync(OrchestrationContext context, CancellationToken ct);

    ValueTask OnRunCompletedAsync(OrchestrationContext context, RunOutcome outcome, CancellationToken ct);

    ValueTask<StrategyRecovery> RecoverAsync(OrchestrationContext context, CancellationToken ct);
}

public interface IIntakeStrategy
{
    ValueTask<JobSpec?> TryParseAsync(string rawRequest, IReadOnlyList<string> candidateRepoIds, CancellationToken ct);
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentRunRole
{
    Intake,
    Planner,
    Worker,
    Arbiter,
    Mentor,
    Reviewer,
    Merger,
}
