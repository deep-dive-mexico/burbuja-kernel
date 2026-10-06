using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Domain;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Agents.Abstractions.Orchestration;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Agents.Model;

namespace Burbuja.Agents.Runtime;

/// <summary>Strategy ids are plugin-declared; a duplicate id is a boot failure, an unknown id a 400.</summary>
public sealed class StrategyRegistry
{
    private readonly Dictionary<string, IOrchestrationStrategy> _byId = new(StringComparer.OrdinalIgnoreCase);

    public StrategyRegistry(IEnumerable<IOrchestrationStrategy> strategies)
    {
        foreach (var strategy in strategies)
        {
            if (strategy.Id.Length == 0)
                throw new InvalidOperationException("an orchestration strategy must declare an id");
            if (!_byId.TryAdd(strategy.Id, strategy))
                throw new InvalidOperationException($"duplicate orchestration strategy id '{strategy.Id}'");
        }
    }

    public IReadOnlyList<string> Ids => _byId.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    public bool TryGet(string id, out IOrchestrationStrategy strategy) => _byId.TryGetValue(id, out strategy!);
}

public static class StrategyIds
{
    public const string SingleWorker = "single-worker";
    public const string PlanFanOut = "plan-fan-out";

    /// <summary>Normalise the wire spellings onto one id.</summary>
    public static string Normalize(string? strategy, string? scenario)
    {
        if (!string.IsNullOrWhiteSpace(strategy))
        {
            if (strategy.Equals(PlanFanOut, StringComparison.OrdinalIgnoreCase)
                || strategy.Equals("planFanOutMerge", StringComparison.OrdinalIgnoreCase))
                return PlanFanOut;
            if (strategy.Equals(SingleWorker, StringComparison.OrdinalIgnoreCase))
                return SingleWorker;
            return strategy;
        }

        return scenario is not null && scenario.StartsWith("fanout", StringComparison.OrdinalIgnoreCase)
            ? PlanFanOut
            : SingleWorker;
    }
}

/// <summary>Reads a plan from `tests/scenarios/<scenario>.json` (`plan` section) when present.</summary>
public sealed class ScenarioPlanProvider
{
    private readonly string _directory;

    public ScenarioPlanProvider()
        : this(Environment.GetEnvironmentVariable("BURBUJA_SCRIPTED_SCENARIO_DIR")
               ?? Path.Combine(AppContext.BaseDirectory, "scenarios"))
    {
    }

    public ScenarioPlanProvider(string directory) => _directory = directory;

    public FanOutPlan? TryLoad(string scenario)
    {
        var path = Path.Combine(_directory, scenario + ".json");
        if (!File.Exists(path))
            return null;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            return node?["plan"] is { } plan
                ? plan.Deserialize<FanOutPlan>(JsonWire.Options)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A benign default: two independent files. Never a destructive fixture.</summary>
    public static FanOutPlan SafeDefault => new(
        [
            new PlanSubtask("a", "a", "write alpha.txt", ["alpha.txt"], [], 0.1m),
            new PlanSubtask("b", "b", "write beta.txt", ["beta.txt"], [], 0.1m),
        ],
        "concat",
        [],
        0.8);
}

public sealed class SingleWorkerStrategy(AgentRunner runner, JobStateStore states) : IOrchestrationStrategy
{
    public string Id => StrategyIds.SingleWorker;

    public async ValueTask StartAsync(OrchestrationContext context, CancellationToken ct)
    {
        var job = await states.MutateAsync(context.JobKey, p =>
        {
            p.Recovery ??= new JobRecoveryState();
            p.Recovery.Strategy = Id;
            if (p.Recovery.Runs.Count == 0)
                p.Recovery.Runs.Add(new JobRunCheckpoint { RunId = p.ActiveRunId ?? Guid.NewGuid(), Label = "worker", State = p.State });
            p.ActiveRunId = p.Recovery.Runs[0].RunId;
        }, ct);
        await runner.RunAsync(context.JobKey, job.Properties.Recovery!.Runs[0].RunId, AgentRunRole.Worker, null, null, null, ct);
    }

    public ValueTask OnRunCompletedAsync(OrchestrationContext context, RunOutcome outcome, CancellationToken ct) => default;

    public async ValueTask<StrategyRecovery> RecoverAsync(OrchestrationContext context, CancellationToken ct)
    {
        await StartAsync(context, ct);
        return new([context.JobKey]);
    }
}

public sealed class PlanFanOutMergeStrategy(
    AgentRunner runner,
    JobStateStore states,
    ScenarioPlanProvider plans,
    LlmProviderRouter llmRouter) : IOrchestrationStrategy
{
    public string Id => StrategyIds.PlanFanOut;

    public async ValueTask StartAsync(OrchestrationContext context, CancellationToken ct)
    {
        var spec = context.Spec;
        var job = await states.GetAsync(context.JobKey, ct);
        if (job is null)
            return;
        var scenario = job.Tags.TryGetValue(AgentsConstants.ScenarioTag, out var declared) ? declared : spec.Extra is not null
                       && spec.Extra.TryGetValue(AgentsConstants.ScenarioTag, out var tag)
            ? tag.GetString() ?? ""
            : "";
        var plan = job.Properties.Recovery?.Plan ?? plans.TryLoad(scenario) ?? await TryLlmPlanAsync(ct) ?? ScenarioPlanProvider.SafeDefault;

        if (!JobPlanValidation.IsValid(plan, spec.MaxBudgetUsd ?? 5m)
            || plan.Subtasks.Any(s => string.IsNullOrWhiteSpace(s.Brief)))
        {
            await states.MutateAsync(context.JobKey, p => p.Notes.Add("invalid plan; falling back to single-worker"), ct);
            await new SingleWorkerStrategy(runner, states).StartAsync(context with { Strategy = StrategyIds.SingleWorker }, ct);
            return;
        }

        job = await states.MutateAsync(context.JobKey, p =>
        {
            p.Recovery ??= new JobRecoveryState();
            p.Recovery.Strategy = Id;
            if (p.Recovery.Plan is not null)
                return;
            p.Recovery.Plan = plan;
            p.Recovery.GroupId = Guid.NewGuid();
            p.GroupId = p.Recovery.GroupId;
            p.Recovery.Runs = plan.Subtasks.Select(sub => new JobRunCheckpoint
            { RunId = Guid.NewGuid(), Label = sub.Id, Brief = sub.Brief }).ToList();
            p.Recovery.Runs.Add(new JobRunCheckpoint
            { RunId = Guid.NewGuid(), Label = "merger", Role = AgentRunRole.Merger, Brief = "merge the results" });
            p.Status = JobExecutionStatus.Running;
        }, ct, Burbuja.Core.Abstractions.Arm.ProvisioningState.Updating);
        var runs = job.Properties.Recovery!.Runs;
        var groupId = job.Properties.Recovery.GroupId;
        var pending = runs.Where(run => run.Role == AgentRunRole.Worker).ToList();
        var done = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var ready = pending.Where(run => plan.Subtasks.Single(sub => sub.Id == run.Label)
                .DependsOn?.All(done.Contains) != false).ToList();
            if (ready.Count == 0)
                break;
            await Task.WhenAll(ready.Select(run => runner.RunAsync(context.JobKey, run.RunId, run.Role,
                run.Brief, runs.Single(saved => saved.Role == AgentRunRole.Merger).RunId, groupId, ct, ownsTerminal: false)));
            job = (await states.GetAsync(context.JobKey, ct))!;
            foreach (var run in ready)
            {
                pending.Remove(run);
                var checkpoint = job.Properties.Recovery!.Runs.Single(saved => saved.RunId == run.RunId);
                if (checkpoint.Status == JobExecutionStatus.Succeeded)
                    done.Add(run.Label);
                else if (JobExecution.IsTerminal(checkpoint.Status))
                {
                    await runner.SettleAsync(context.JobKey, checkpoint.Status, checkpoint.Error, ct);
                    return;
                }
            }
        }
        job = (await states.GetAsync(context.JobKey, ct))!;
        if (job.Properties.Recovery!.Runs.Any(run => run.Role == AgentRunRole.Worker && run.Status != JobExecutionStatus.Succeeded))
        {
            await states.MutateAsync(context.JobKey, p =>
            {
                p.Status = JobExecutionStatus.AwaitingApproval;
                p.PendingApprovalId = p.Recovery!.Runs.FirstOrDefault(run => run.PendingApprovalId is not null)?.PendingApprovalId;
            }, ct);
            return;
        }
        var merger = runs.Single(run => run.Role == AgentRunRole.Merger);
        await runner.RunAsync(context.JobKey, merger.RunId, merger.Role, merger.Brief, null, groupId, ct);
    }

    public ValueTask OnRunCompletedAsync(OrchestrationContext context, RunOutcome outcome, CancellationToken ct) => default;

    public async ValueTask<StrategyRecovery> RecoverAsync(OrchestrationContext context, CancellationToken ct)
    {
        await StartAsync(context, ct);
        return new([context.JobKey]);
    }

    private async ValueTask<FanOutPlan?> TryLlmPlanAsync(CancellationToken ct)
    {
        var structured = llmRouter.Structured;
        if (structured is null)
            return null;
        var node = await structured.CallAsync(new StructuredLlmRequest(
            AgentsConstants.DefaultModelId,
            [new InputMessage("user", [new TextBlock("emit a fan-out plan")])],
            "plan",
            new ToolSpec("emit_plan", "plan", new JsonObject()),
            new ToolChoiceTool("emit_plan"),
            1024), ct);
        if (node is null || node["subtasks"] is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<FanOutPlan>(node, JsonWire.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Plan validity is a pure function so both the strategy and tests share one rule set.</summary>
public static class JobPlanValidation
{
    public static bool IsValid(FanOutPlan plan, decimal budget)
    {
        if (plan.Subtasks is null || plan.Subtasks.Count is 0 or > 4)
            return false;
        if (plan.Subtasks.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id)))
            return false;
        var ids = plan.Subtasks.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != plan.Subtasks.Count)
            return false;
        foreach (var sub in plan.Subtasks)
        {
            if ((sub.DependsOn ?? []).Any(d => !ids.Contains(d)))
                return false;
        }

        var independent = plan.Subtasks.Where(s => (s.DependsOn ?? []).Count == 0).ToList();
        var files = independent.SelectMany(s => s.Files ?? []).ToList();
        if (files.Count != files.Distinct(StringComparer.Ordinal).Count())
            return false;
        if (plan.Subtasks.Sum(s => s.EstimateUsd) > budget * 0.8m)
            return false;
        return !HasCycle(plan);
    }

    public static bool HasCycle(FanOutPlan plan)
    {
        var edges = plan.Subtasks.ToDictionary(s => s.Id, s => s.DependsOn ?? (IReadOnlyList<string>)[], StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        bool Dfs(string id)
        {
            if (visiting.Contains(id))
                return true;
            if (done.Contains(id))
                return false;
            visiting.Add(id);
            foreach (var d in edges[id])
            {
                if (Dfs(d))
                    return true;
            }

            visiting.Remove(id);
            done.Add(id);
            return false;
        }

        return edges.Keys.Any(Dfs);
    }
}
