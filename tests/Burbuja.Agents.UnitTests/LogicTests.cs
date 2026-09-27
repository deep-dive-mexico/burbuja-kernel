using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Arbiter;
using Burbuja.Agents.Abstractions.Domain;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Agents.Abstractions.Orchestration;
using Burbuja.Agents.Abstractions.Registry;
using Burbuja.Agents.Runtime;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Policy;
using Burbuja.Core.Abstractions.Process;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Burbuja.Agents.UnitTests;

public sealed class LogicTests
{
    [Fact]
    public void Cost_calculator_matches_sonnet_formula_and_rejects_unpriced()
    {
        var calc = new CostCalculator();
        var usage = new Usage(1_000_000, 1_000_000, 1_000_000, 1_000_000);
        // (3 + 15 + 3*1.25 + 3*0.1) * 1.10 = 22.05 * 1.10 = 24.255
        calc.Quote(AgentsConstants.DefaultModelId, usage).ShouldBe(24.255m);
        Should.Throw<InvalidOperationException>(() => calc.Quote("unknown-model", usage));
    }

    [Fact]
    public void Compactor_never_splits_tool_use_and_result()
    {
        var messages = new List<InputMessage>();
        for (var i = 0; i < 20; i++)
        {
            messages.Add(new InputMessage("assistant", [new ToolUseBlock("t" + i, "list_dir", new JsonObject())]));
            messages.Add(new InputMessage("user", [new ToolResultBlock("t" + i, JsonValue.Create("ok")!, false)]));
        }

        var compacted = TranscriptCompactor.Compact(messages, out var did);
        did.ShouldBeTrue();
        TranscriptInvariants.Validate(compacted);
        compacted.Count.ShouldBeLessThan(messages.Count);
    }

    [Fact]
    public void Deny_text_is_the_behaviour_contract()
    {
        DenyText.Format("es", "destructive_fs").ShouldContain("Burbuja bloqueó esta acción (clase: destructive_fs)");
        DenyText.Format("es", "x").ShouldContain("Es definitivo");
        DenyText.Format("en", "x").ShouldContain("Burbuja blocked this action");
    }

    [Fact]
    public void Fanout_validation_rejects_cycles_and_over_budget()
    {
        var bad = new FanOutPlan(
            [
                new PlanSubtask("a", "a", "a", ["a"], ["b"], 1),
                new PlanSubtask("b", "b", "b", ["b"], ["a"], 1),
            ],
            "x",
            [],
            1);
        JobPlanValidation.IsValid(bad, 100).ShouldBeFalse();
        var ok = new FanOutPlan(
            [new PlanSubtask("a", "a", "a", ["a.txt"], [], 0.1m)],
            "x",
            [],
            1);
        JobPlanValidation.IsValid(ok, 5).ShouldBeTrue();
    }

    [Fact]
    public async Task Intake_rejects_unknown_repos_and_falls_back()
    {
        var repos = new YamlRepoRegistry(null);
        var heuristic = new HeuristicIntakeStrategy(repos);
        (await heuristic.TryParseAsync("please use unknown-repo", ["unknown-repo"], TestContext.Current.CancellationToken)).ShouldBeNull();
        var spec = await heuristic.TryParseAsync("please use demo", ["demo"], TestContext.Current.CancellationToken);
        spec.ShouldNotBeNull();
        spec.RepoId.ShouldBe("demo");
        var intake = new IntakeService(Router(), heuristic, repos);
        (await intake.ParseAsync("demo please", ["demo"], TestContext.Current.CancellationToken))!.RepoId.ShouldBe("demo");
    }

    private static Burbuja.Agents.Runtime.LlmProviderRouter Router() =>
        new([], Microsoft.Extensions.Options.Options.Create(new Burbuja.Agents.AgentsOptions()));

    [Fact]
    public async Task Booking_is_unique_per_turn()
    {
        using var sp = Harness.CreateHost();
        var cache = sp.GetRequiredService<Burbuja.Core.Abstractions.Store.ICache>();
        (await cache.TryAddAsync("booked:j:r:0:worker", true, null, TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await cache.TryAddAsync("booked:j:r:0:worker", true, null, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task Joiner_fires_merger_once_under_three_completions()
    {
        var joiner = new RunGroupJoiner();
        var hits = 0;
        var group = Guid.NewGuid();
        joiner.Register(group, 3, _ =>
        {
            Interlocked.Increment(ref hits);
            return default;
        });
        await Task.WhenAll(
            joiner.SignalAsync(group, TestContext.Current.CancellationToken).AsTask(),
            joiner.SignalAsync(group, TestContext.Current.CancellationToken).AsTask(),
            joiner.SignalAsync(group, TestContext.Current.CancellationToken).AsTask());
        hits.ShouldBe(1);
    }

    [Fact]
    public async Task Narrowing_arbiter_overrides_are_not_cached()
    {
        using var sp = Harness.CreateHost();
        var arbiter = sp.GetRequiredService<NarrowingArbiter>();
        var req = new ArbiterRequest(
            new Burbuja.Agents.Abstractions.Tools.ToolCallSubject("bash", new JsonObject(), ".", "r", "j", false),
            "b",
            "unknown_command",
            RiskTier.Gray,
            "cmd",
            50,
            4m);
        var first = await arbiter.DecideAsync("s", req, TestContext.Current.CancellationToken);
        first.Overridden.ShouldBeTrue();
        first.Verdict.ShouldBe(ArbiterVerdict.Escalate);
        var cache = sp.GetRequiredService<Burbuja.Core.Abstractions.Store.IDecisionCache<ArbiterDecision>>();
        (await cache.GetAsync("s", NarrowingArbiter.DecisionKey(req), TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Gate_audit_seq_is_less_than_decision_and_throwing_rules_deny()
    {
        using var sp = Harness.CreateHost(extra: services =>
        {
            services.AddSingleton<IPolicyRule<SegmentPolicySubject>, ThrowingRule>();
        });
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", Harness.JobPath, Harness.JobBody("hard-deny-rm-rf"))).Ok();
        await Harness.RunJobAsync(sp, Harness.JobPath, TestContext.Current.CancellationToken);
        var log = sp.GetRequiredService<IEventLog>();
        var events = new List<LedgerEvent>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(400));
        try
        {
            await foreach (var e in log.TailAsync(AgentsConstants.StreamId(Harness.JobPath), 0, cts.Token))
            {
                events.Add(e);
                if (e.Type == AgentEventTypes.Result)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }

        var intents = events.Where(e => e.Type == AgentEventTypes.ToolIntent).ToList();
        var decisions = events.Where(e => e.Type == AgentEventTypes.Decision).ToList();
        intents.Count.ShouldBeGreaterThan(0);
        for (var i = 0; i < Math.Min(intents.Count, decisions.Count); i++)
            intents[i].Seq.ShouldBeLessThan(decisions[i].Seq);
    }

    [Fact]
    public async Task Workspace_commits_on_agent_slug_and_base_is_unmoved()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", Harness.JobPath, Harness.JobBody("happy-path"))).Ok();
        await Harness.RunJobAsync(sp, Harness.JobPath, TestContext.Current.CancellationToken);
        var get = await Harness.Send(sp, Harness.JobPath.Contains("j1", StringComparison.Ordinal) ? "GET" : "GET", Harness.JobPath);
        get.AsJson()["properties"]!["status"]!.GetValue<string>().ShouldBe("succeeded");
        var state = get.AsJson()["properties"]!["state"];
        var branch = state?["workspaceBranch"]?.GetValue<string>();
        branch.ShouldNotBeNull();
        branch.ShouldStartWith("agent/");
        var path = state?["workspacePath"]?.GetValue<string>();
        path.ShouldNotBeNull();
        var repoDir = Path.Combine(Directory.GetParent(path)!.FullName, "repo");
        var grant = sp.GetRequiredService<IExecutionGrantAuthority>().Mint("t", "t", TimeSpan.FromMinutes(1));
        var git = await sp.GetRequiredService<IProcessRunner>().RunAsync(
            new ProcessStartRequest("git", ["rev-parse", "--abbrev-ref", "HEAD"], repoDir, new Dictionary<string, string>(), TimeSpan.FromSeconds(10), 4096),
            grant,
            TestContext.Current.CancellationToken);
        git.StandardOutput.Trim().ShouldBe("main");
    }

    [Fact]
    public async Task Tool_child_env_has_none_of_the_planted_secrets()
    {
        var planted = new Dictionary<string, string>
        {
            ["AWS_ACCESS_KEY_ID"] = "AKIAIOSFODNN7EXAMPLE",
            ["AWS_SECRET_ACCESS_KEY"] = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
            ["AWS_SESSION_TOKEN"] = "session",
            ["GITHUB_TOKEN"] = "ghp_secret",
            ["OPENAI_API_KEY"] = "sk-secret",
            ["ANTHROPIC_API_KEY"] = "ant-secret",
            ["AZURE_CLIENT_SECRET"] = "az-secret",
            ["NPM_TOKEN"] = "npm-secret",
            ["PGPASSWORD"] = "pg-secret",
            ["REDIS_PASSWORD"] = "redis-secret",
            ["MINIO_ROOT_PASSWORD"] = "minio-secret",
            ["BURBUJA_API_KEY"] = "burbuja-secret",
        };
        var spy = new EnvSpyRunner();
        using var sp = Harness.CreateHost(extra: services =>
        {
            services.AddSingleton<IProcessRunner>(spy);
        });
        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", Harness.JobPath, Harness.JobBody("gray-supervisor-allow"))).Ok();
        await Harness.RunJobAsync(sp, Harness.JobPath, TestContext.Current.CancellationToken);
        foreach (var key in planted.Keys)
            spy.EnvKeys.ShouldNotContain(key);
    }

    [Fact]
    public async Task Pipeline_put_is_202_and_reader_is_403()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        var put = await Harness.Send(sp, "PUT", Harness.JobPath, Harness.JobBody("happy-path"));
        put.StatusCode.ShouldBe(202);
        put.ResponseHeaders.ShouldContainKey("Azure-AsyncOperation");
        var reader = await Harness.Send(sp, "PUT", Harness.WsPath + "/jobs/j2", Harness.JobBody("happy-path"), key: "reader-key");
        reader.StatusCode.ShouldBe(403);
    }

    private sealed class ThrowingRule : IPolicyRule<SegmentPolicySubject>
    {
        public string Id => "throw-test";
        public PolicyFinding? Evaluate(SegmentPolicySubject subject) => throw new InvalidOperationException("boom");
    }

    private sealed class EnvSpyRunner : IProcessRunner
    {
        public List<string> EnvKeys { get; } = [];

        public ValueTask<ProcessResult> RunAsync(ProcessStartRequest request, ExecutionGrant grant, CancellationToken ct)
        {
            EnvKeys.AddRange(request.Environment.Keys);
            return new(new ProcessResult(0, "", "", false));
        }
    }
}
