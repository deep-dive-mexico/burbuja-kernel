using Burbuja.Agents;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Orchestration;
using Burbuja.Agents.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Burbuja.Agents.UnitTests;

public sealed class StrategyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Unknown_strategy_is_rejected_at_job_put()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);

        var body = "{\"properties\":{\"spec\":{\"kind\":\"implement\",\"title\":\"t\",\"slug\":\"s\",\"brief\":\"b\",\"strategy\":\"no-such-strategy\"}}}";
        var put = await Harness.Send(sp, "PUT", Harness.WsPath + "/jobs/j-bad", body);
        put.StatusCode.ShouldBe(400);
        put.Error!.Code.ShouldBe("UnknownStrategy");
    }

    [Fact]
    public async Task Known_strategies_are_accepted_at_job_put()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);

        var body = "{\"properties\":{\"spec\":{\"kind\":\"implement\",\"title\":\"t\",\"slug\":\"s\",\"brief\":\"b\",\"strategy\":\"single-worker\",\"autoAccept\":true}}}";
        var put = await Harness.Send(sp, "PUT", Harness.WsPath + "/jobs/j-ok", body);
        put.StatusCode.ShouldBe(202);
    }

    [Fact]
    public void Registry_rejects_duplicate_ids()
    {
        var strategies = new IOrchestrationStrategy[] { new StubStrategy("dup"), new StubStrategy("dup") };
        Should.Throw<InvalidOperationException>(() => new StrategyRegistry(strategies))
            .Message.ShouldContain("duplicate");
    }

    [Fact]
    public void Registry_normalises_wire_spellings()
    {
        StrategyIds.Normalize("planFanOutMerge", null).ShouldBe(StrategyIds.PlanFanOut);
        StrategyIds.Normalize(null, "fanout-plan-3").ShouldBe(StrategyIds.PlanFanOut);
        StrategyIds.Normalize(null, null).ShouldBe(StrategyIds.SingleWorker);
        StrategyIds.Normalize("plan-fan-out", null).ShouldBe(StrategyIds.PlanFanOut);
    }

    [Fact]
    public void Production_code_contains_no_destructive_fallback_plan()
    {
        var root = RepoRoot();
        var offenders = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(p => File.ReadAllText(p).Contains("rm -rf /", StringComparison.Ordinal))
            .ToList();

        offenders.ShouldBeEmpty("the destructive fan-out fixture belongs to tests/scenarios, not src");
    }

    [Fact]
    public void The_destructive_plan_lives_in_the_test_scenario()
    {
        var provider = new ScenarioPlanProvider(Path.Combine(RepoRoot(), "tests", "scenarios"));
        var plan = provider.TryLoad("fanout-plan-3");

        plan.ShouldNotBeNull();
        plan!.Subtasks.Count.ShouldBe(3);
        plan.Subtasks.Select(s => s.Brief).ShouldContain("rm -rf /");
    }

    [Fact]
    public void Safe_default_plan_has_no_destructive_brief()
    {
        ScenarioPlanProvider.SafeDefault.Subtasks.ShouldAllBe(s => !s.Brief.Contains("rm -rf", StringComparison.Ordinal));
    }

    private sealed class StubStrategy(string id) : IOrchestrationStrategy
    {
        public string Id => id;

        public ValueTask StartAsync(OrchestrationContext context, CancellationToken ct) => default;

        public ValueTask OnRunCompletedAsync(OrchestrationContext context, RunOutcome outcome, CancellationToken ct) => default;

        public ValueTask<StrategyRecovery> RecoverAsync(OrchestrationContext context, CancellationToken ct) => new(new StrategyRecovery([]));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Burbuja.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Burbuja.slnx not found");
    }
}
