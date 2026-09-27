using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Domain;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Agents.Abstractions.Orchestration;
using Burbuja.Core.Abstractions.Json;

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

public sealed class SingleWorkerStrategy(AgentRunner runner) : IOrchestrationStrategy
{
    public string Id => StrategyIds.SingleWorker;

    public async ValueTask StartAsync(OrchestrationContext context, CancellationToken ct) =>
        await runner.RunAsync(context.JobKey, Guid.NewGuid(), AgentRunRole.Worker, null, null, null, ct);

    public ValueTask OnRunCompletedAsync(OrchestrationContext context, RunOutcome outcome, CancellationToken ct) => default;

    public ValueTask<StrategyRecovery> RecoverAsync(OrchestrationContext context, CancellationToken ct) =>
        new(new StrategyRecovery([]));
}

public sealed class PlanFanOutMergeStrategy(
    AgentRunner runner,
    JobStateStore states,
    RunGroupJoiner joiner,
    ScenarioPlanProvider plans,
    LlmProviderRouter llmRouter) : IOrchestrationStrategy
{
    public string Id => StrategyIds.PlanFanOut;

    public async ValueTask StartAsync(OrchestrationContext context, CancellationToken ct)
    {
        var spec = context.Spec;
        var scenario = spec.Extra is not null
                       && spec.Extra.TryGetValue(AgentsConstants.ScenarioTag, out var tag)
            ? tag.GetString() ?? ""
            : "";
        var plan = plans.TryLoad(scenario) ?? await TryLlmPlanAsync(ct) ?? ScenarioPlanProvider.SafeDefault;

        if (!JobPlanValidation.IsValid(plan, spec.MaxBudgetUsd ?? 5m)
            || plan.Subtasks.Any(s => string.IsNullOrWhiteSpace(s.Brief)))
        {
            await states.MutateAsync(context.JobKey, p => p.Notes.Add("invalid plan; falling back to single-worker"), ct);
            await runner.RunAsync(context.JobKey, Guid.NewGuid(), AgentRunRole.Worker, null, null, null, ct);
            return;
        }

        var groupId = Guid.NewGuid();
        var remaining = plan.Subtasks.Count;
        joiner.Register(groupId, remaining, async token =>
            await runner.RunAsync(context.JobKey, Guid.NewGuid(), AgentRunRole.Merger, "merge the results", null, groupId, token));

        await Task.WhenAll(plan.Subtasks.Select(async sub =>
        {
            await runner.RunAsync(context.JobKey, Guid.NewGuid(), AgentRunRole.Worker, sub.Brief, Guid.NewGuid(), groupId, ct, ownsTerminal: false);
            await joiner.SignalAsync(groupId, ct);
        }));
    }

    public ValueTask OnRunCompletedAsync(OrchestrationContext context, RunOutcome outcome, CancellationToken ct) => default;

    public ValueTask<StrategyRecovery> RecoverAsync(OrchestrationContext context, CancellationToken ct) =>
        new(new StrategyRecovery([]));

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
