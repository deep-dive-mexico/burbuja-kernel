using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Policy;
using Burbuja.Core.Abstractions.Process;
using Burbuja.Core.Clock;
using Burbuja.Core.InMemory;
using Burbuja.Core.Process;
using Burbuja.Deployments.Abstractions;
using Burbuja.Deployments.Execution;
using Burbuja.Deployments.Targets;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Burbuja.Deployments.UnitTests;

public sealed class LocalProcessTests
{
    [Fact]
    public async Task Allow_runs_echo_and_captures_outputs()
    {
        var (target, _) = Create(new FakePolicy(), new SpyProcessRunner());
        var result = await target.ApplyAsync(Req("echo", ["hello"]), TestContext.Current.CancellationToken);
        result.Status.ShouldBe(StepStatus.Succeeded);
        result.Outputs!["stdout"]!.GetValue<string>().ShouldContain("hello");
        result.Outputs["exitCode"]!.GetValue<int>().ShouldBe(0);
    }

    [Fact]
    public async Task Rm_rf_root_is_denied_and_does_not_spawn()
    {
        var spy = new SpyProcessRunner();
        var policy = new FakePolicy { Tier = RiskTier.HardDeny, Reason = "write outside worktree" };
        var (target, _) = Create(policy, spy);
        var result = await target.ApplyAsync(Req("rm", ["-rf", "/"]), TestContext.Current.CancellationToken);
        result.Status.ShouldBe(StepStatus.Failed);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("PolicyDenied");
        spy.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Gray_parks_then_runs_after_approve()
    {
        var spy = new SpyProcessRunner();
        var policy = new FakePolicy { Tier = RiskTier.Gray, Reason = "unknown_command" };
        var (target, _) = Create(policy, spy);
        var first = await target.ApplyAsync(Req("custom-tool"), TestContext.Current.CancellationToken);
        first.NeedsApproval.ShouldBeTrue();
        first.Status.ShouldBe(StepStatus.AwaitingApproval);
        spy.Calls.ShouldBe(0);

        var second = await target.ApplyAsync(Req("custom-tool") with { ApprovalGranted = true }, TestContext.Current.CancellationToken);
        second.Status.ShouldBe(StepStatus.Succeeded);
        spy.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Gray_through_runner_parks_then_succeeds()
    {
        var spy = new SpyProcessRunner();
        var policy = new FakePolicy { Tier = RiskTier.Gray };
        var approvals = new ControllableApprovals { Hold = true };
        var time = new FakeTimeProvider();
        var clock = new TimeProviderClock(time);
        var events = new InMemoryEventLog(new NoopRedactor(), clock);
        var grants = new ExecutionGrantAuthority(clock);
        var target = new LocalProcessTarget(policy, events, grants, spy, new NoopRedactor(), time);
        var box = await RunnerBox.CreateAsync(approvals: approvals, resources: [Proc("echo")]);
        var runner = new DeploymentRunner(box.States, new TargetRegistry([target]), box.Leases, approvals, events, box.Ops, box.Cancel, time);
        box.Environment.Target.Kind = "local-process";
        box.Environment.Target.WorkRoot = Path.Combine(Path.GetTempPath(), "burbuja-deployments-tests");
        Directory.CreateDirectory(box.Environment.Target.WorkRoot);
        var run = runner.RunAsync(box.Key, box.EnvironmentId, box.Environment, TestContext.Current.CancellationToken);
        await WaitUntil(() => approvals.LastId is not null);
        spy.Calls.ShouldBe(0);
        approvals.Release(new Approved(false, null, null));
        await run.WaitAsync(TestContext.Current.CancellationToken);
        spy.Calls.ShouldBe(1);
        (await box.States.GetAsync(box.Key, TestContext.Current.CancellationToken))!.Properties.ExecutionState
            .ShouldBe(DeploymentExecutionState.Succeeded);
    }

    [Fact]
    public async Task Planted_secret_is_absent_from_child_env()
    {
        var spy = new SpyProcessRunner();
        var (target, _) = Create(new FakePolicy(), spy);
        var spec = Spec("echo", ["ok"]);
        spec["env"] = new JsonObject
        {
            ["AWS_SECRET_ACCESS_KEY"] = "planted-secret",
            ["GH_TOKEN"] = "planted-token",
            ["PATH"] = "/usr/bin",
        };
        await target.ApplyAsync(ReqFrom(spec), TestContext.Current.CancellationToken);
        spy.Last.ShouldNotBeNull();
        var parent = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => e.Value?.ToString() ?? "", StringComparer.OrdinalIgnoreCase);
        var child = ProcessEnvironmentPolicy.Build(parent, spy.Last.Environment);
        child.ShouldNotContainKey("AWS_SECRET_ACCESS_KEY");
        child.ShouldNotContainKey("GH_TOKEN");
        child.ContainsKey("PATH").ShouldBeTrue();
    }

    [Fact]
    public async Task Timeout_is_failed()
    {
        var spy = new TimingOutRunner();
        var (target, _) = Create(new FakePolicy(), spy);
        var spec = Spec("sleep", ["30"]);
        spec["timeoutSeconds"] = 1;
        var result = await target.ApplyAsync(ReqFrom(spec), TestContext.Current.CancellationToken);
        result.Status.ShouldBe(StepStatus.Failed);
        result.Error.ShouldBe("Timeout");
    }

    [Fact]
    public async Task Cwd_escape_is_rejected()
    {
        var spy = new SpyProcessRunner();
        var (target, _) = Create(new FakePolicy(), spy);
        var spec = Spec("echo", ["x"]);
        spec["cwd"] = "../outside";
        var result = await target.ApplyAsync(ReqFrom(spec), TestContext.Current.CancellationToken);
        result.Status.ShouldBe(StepStatus.Failed);
        result.Error.ShouldBe("CwdEscape");
        spy.Calls.ShouldBe(0);
    }

    private static (LocalProcessTarget Target, TimeProvider Time) Create(IPolicyPipeline<ActionPolicySubject> policy, IProcessRunner processes)
    {
        var time = TimeProvider.System;
        var clock = new TimeProviderClock(time);
        var events = new InMemoryEventLog(new NoopRedactor(), clock);
        var grants = new ExecutionGrantAuthority(clock);
        return (new LocalProcessTarget(policy, events, grants, processes, new NoopRedactor(), time), time);
    }

    private static DeploymentResource Proc(string name) =>
        new()
        {
            Name = name,
            Kind = DeploymentResourceKind.Process,
            Spec = new JsonObject { ["command"] = "echo", ["args"] = new JsonArray("hello") },
        };

    private static JsonObject Spec(string command, string[] args) => new()
    {
        ["command"] = command,
        ["args"] = new JsonArray(args.Select(a => JsonValue.Create(a)).ToArray()),
    };

    private static StepRequest Req(string command, string[]? args = null) =>
        ReqFrom(Spec(command, args ?? []));

    private static StepRequest ReqFrom(JsonObject spec)
    {
        var root = Path.Combine(Path.GetTempPath(), "burbuja-deployments-tests");
        Directory.CreateDirectory(root);
        return new StepRequest(
            "env",
            "dep",
            "web",
            DeploymentResourceKind.Process,
            StepChange.Create,
            spec,
            new JsonObject { ["workRoot"] = root, ["profile"] = "fullAuto" },
            [],
            1,
            TimeSpan.FromSeconds(5));
    }

    private static async Task WaitUntil(Func<bool> pred)
    {
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 200; i++)
        {
            if (pred())
                return;
            await Task.Delay(10, ct);
        }

        throw new TimeoutException("condition not reached");
    }

    private sealed class TimingOutRunner : IProcessRunner
    {
        public ValueTask<ProcessResult> RunAsync(ProcessStartRequest request, ExecutionGrant grant, CancellationToken ct) =>
            new(new ProcessResult(-1, "", "", true));
    }
}
