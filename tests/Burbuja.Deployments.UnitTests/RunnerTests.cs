using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Deployments.Abstractions;
using Shouldly;
using Xunit;

namespace Burbuja.Deployments.UnitTests;

public sealed class RunnerTests
{
    [Fact]
    public async Task Happy_path_succeeds()
    {
        var box = await RunnerBox.CreateAsync();
        await box.Run(TestContext.Current.CancellationToken);
        var env = await box.States.GetAsync(box.Key, TestContext.Current.CancellationToken);
        env!.Properties.ExecutionState.ShouldBe(DeploymentExecutionState.Succeeded);
        env.ProvisioningState.ShouldBe(ProvisioningState.Succeeded);
        (await box.Ops.GetAsync("op1", TestContext.Current.CancellationToken))!.Status.ShouldBe(OperationStatus.Succeeded);
    }

    [Fact]
    public async Task Wave_parallelism_advances_once_for_two_delays()
    {
        var a = Res("a", delay: 5000);
        var b = Res("b", delay: 5000);
        var box = await RunnerBox.CreateAsync(resources: [a, b]);
        var run = box.Run(TestContext.Current.CancellationToken);
        await WaitUntil(async () =>
        {
            var e = await box.States.GetAsync(box.Key, TestContext.Current.CancellationToken);
            return e?.Properties.Steps.Count(s => s.Status == StepStatus.Running) == 2;
        });
        box.Time.Advance(TimeSpan.FromSeconds(5));
        await Task.Yield();
        await run.WaitAsync(TestContext.Current.CancellationToken);
        var env = await box.States.GetAsync(box.Key, TestContext.Current.CancellationToken);
        env!.Properties.ExecutionState.ShouldBe(DeploymentExecutionState.Succeeded);
        env.Properties.Steps.All(s => s.Status == StepStatus.Succeeded).ShouldBeTrue();
    }

    [Fact]
    public async Task Fault_fails_and_skips_later_waves()
    {
        var a = Res("a");
        a.Spec["_test"] = new JsonObject { ["fail"] = true };
        var b = Res("b", depends: ["a"]);
        var box = await RunnerBox.CreateAsync(resources: [a, b]);
        var stored = await box.States.GetAsync(box.Key, TestContext.Current.CancellationToken);
        stored!.Properties.Steps.First(s => s.Resource == "b").Wave = 1;
        await box.States.PersistAsync(stored, TestContext.Current.CancellationToken);
        await box.Run(TestContext.Current.CancellationToken);
        var env = await box.States.GetAsync(box.Key, TestContext.Current.CancellationToken);
        env!.Properties.ExecutionState.ShouldBe(DeploymentExecutionState.Failed);
        env.Properties.Steps.First(s => s.Resource == "a").Status.ShouldBe(StepStatus.Failed);
        env.Properties.Steps.First(s => s.Resource == "b").Status.ShouldBe(StepStatus.Skipped);
    }

    [Fact]
    public async Task Step_timeout_fails()
    {
        var r = Res("slow", delay: 60_000);
        var box = await RunnerBox.CreateAsync(resources: [r]);
        box.Environment.Defaults.StepTimeout = TimeSpan.FromMilliseconds(80);
        var run = box.Run(TestContext.Current.CancellationToken);
        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var env = await box.States.GetAsync(box.Key, TestContext.Current.CancellationToken);
        env!.Properties.ExecutionState.ShouldBe(DeploymentExecutionState.Failed);
        env.Properties.Steps[0].Error.ShouldBe("Timeout");
    }

    [Fact]
    public async Task Approval_park_then_resume()
    {
        var approvals = new ControllableApprovals { Hold = true };
        var box = await RunnerBox.CreateAsync(approvals: approvals, requiresApproval: true);
        var run = box.Run(TestContext.Current.CancellationToken);
        await WaitUntil(() => approvals.LastId is not null);
        var parked = await box.States.GetAsync(box.Key, TestContext.Current.CancellationToken);
        parked!.Properties.ExecutionState.ShouldBe(DeploymentExecutionState.AwaitingApproval);
        parked.Properties.PendingApprovalId.ShouldBe(approvals.LastId);
        approvals.LastRequest!.SubjectType.ShouldBe("deployment");
        approvals.LastRequest.DecisionKey.ShouldStartWith("deploy:");
        approvals.Release(new Approved(false, null, null));
        await run.WaitAsync(TestContext.Current.CancellationToken);
        (await box.States.GetAsync(box.Key, TestContext.Current.CancellationToken))!.Properties.ExecutionState
            .ShouldBe(DeploymentExecutionState.Succeeded);
    }

    [Fact]
    public async Task Approval_denied_fails()
    {
        var approvals = new ControllableApprovals { Next = new Denied("no") };
        var box = await RunnerBox.CreateAsync(approvals: approvals, requiresApproval: true);
        await box.Run(TestContext.Current.CancellationToken);
        var env = await box.States.GetAsync(box.Key, TestContext.Current.CancellationToken);
        env!.Properties.ExecutionState.ShouldBe(DeploymentExecutionState.Failed);
        env.Properties.Error.ShouldBe("DeploymentDenied");
    }

    [Fact]
    public async Task Approval_timeout_fails()
    {
        var approvals = new ControllableApprovals { Next = new TimedOut() };
        var box = await RunnerBox.CreateAsync(approvals: approvals, requiresApproval: true);
        await box.Run(TestContext.Current.CancellationToken);
        (await box.States.GetAsync(box.Key, TestContext.Current.CancellationToken))!.Properties.Error.ShouldBe("ApprovalTimedOut");
    }

    [Fact]
    public async Task Cancel_mid_wave_with_hang()
    {
        var r = Res("hang");
        r.Spec["_test"] = new JsonObject { ["hangUntilCanceled"] = true };
        var box = await RunnerBox.CreateAsync(resources: [r]);
        var run = box.Run(TestContext.Current.CancellationToken);
        await WaitUntil(async () =>
        {
            var e = await box.States.GetAsync(box.Key, TestContext.Current.CancellationToken);
            return e?.Properties.Steps[0].Status == StepStatus.Running;
        });
        box.Cancel.Cancel(box.Key);
        await run.WaitAsync(TestContext.Current.CancellationToken);
        (await box.States.GetAsync(box.Key, TestContext.Current.CancellationToken))!.Properties.ExecutionState
            .ShouldBe(DeploymentExecutionState.Canceled);
    }

    [Fact]
    public async Task Cancel_between_waves_without_signal()
    {
        var a = Res("a", delay: 50);
        var b = Res("b", depends: ["a"]);
        var box = await RunnerBox.CreateAsync(resources: [a, b]);
        var stored = await box.States.GetAsync(box.Key, TestContext.Current.CancellationToken);
        stored!.Properties.Steps.First(s => s.Resource == "b").Wave = 1;
        await box.States.PersistAsync(stored, TestContext.Current.CancellationToken);
        var run = box.Run(TestContext.Current.CancellationToken);
        await WaitUntil(async () =>
        {
            var e = await box.States.GetAsync(box.Key, TestContext.Current.CancellationToken);
            return e?.Properties.Steps.First(s => s.Resource == "a").Status == StepStatus.Running;
        });
        await box.States.MutateAsync(box.Key, p => p.ExecutionState = DeploymentExecutionState.Canceling, TestContext.Current.CancellationToken);
        box.Time.Advance(TimeSpan.FromMilliseconds(50));
        await Task.Yield();
        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var env = await box.States.GetAsync(box.Key, TestContext.Current.CancellationToken);
        env!.Properties.ExecutionState.ShouldBe(DeploymentExecutionState.Canceled);
        env.Properties.Steps.First(s => s.Resource == "b").Status.ShouldBe(StepStatus.Canceled);
    }

    [Fact]
    public async Task Complete_deletes_orphan()
    {
        var box = await RunnerBox.CreateAsync();
        await box.Target.ApplyAsync(
            new StepRequest(box.EnvironmentId, box.Canonical, "old", DeploymentResourceKind.Process, StepChange.Create, [], [], [], 1, TimeSpan.FromSeconds(5)),
            TestContext.Current.CancellationToken);
        var stored = await box.States.GetAsync(box.Key, TestContext.Current.CancellationToken);
        stored!.Properties.Mode = DeploymentMode.Complete;
        stored.Properties.Steps =
        [
            new DeploymentStep { Resource = "web", Kind = DeploymentResourceKind.Process, Change = StepChange.Create, Wave = 0 },
            new DeploymentStep { Resource = "old", Kind = DeploymentResourceKind.Process, Change = StepChange.Delete, Wave = 1 },
        ];
        await box.States.PersistAsync(stored, TestContext.Current.CancellationToken);
        await box.Run(TestContext.Current.CancellationToken);
        var env = await box.States.GetAsync(box.Key, TestContext.Current.CancellationToken);
        env!.Properties.Steps.First(s => s.Resource == "old").Status.ShouldBe(StepStatus.Deleted);
        env.Properties.ExecutionState.ShouldBe(DeploymentExecutionState.Succeeded);
    }

    [Fact]
    public async Task Lease_contention_is_noop()
    {
        var box = await RunnerBox.CreateAsync(leases: new BlockingLeaseStore());
        await box.Run(TestContext.Current.CancellationToken);
        (await box.States.GetAsync(box.Key, TestContext.Current.CancellationToken))!.Properties.ExecutionState
            .ShouldBe(DeploymentExecutionState.Pending);
    }

    [Fact]
    public async Task Events_order_per_resource_and_across_waves()
    {
        var a = Res("a");
        var b = Res("b", depends: ["a"]);
        var box = await RunnerBox.CreateAsync(resources: [a, b]);
        var stored = await box.States.GetAsync(box.Key, TestContext.Current.CancellationToken);
        stored!.Properties.Steps.First(s => s.Resource == "b").Wave = 1;
        await box.States.PersistAsync(stored, TestContext.Current.CancellationToken);
        await box.Run(TestContext.Current.CancellationToken);
        var types = new List<string>();
        await foreach (var ev in box.Events.TailAsync(DeploymentsConstants.StreamId(box.Canonical), 0, TestContext.Current.CancellationToken))
        {
            types.Add(ev.Type + ":" + ev.Subject);
            if (ev.Type == DeploymentEvents.Succeeded)
                break;
        }

        var aStart = types.IndexOf(DeploymentEvents.StepStarted + ":a");
        var aDone = types.IndexOf(DeploymentEvents.StepSucceeded + ":a");
        var bStart = types.IndexOf(DeploymentEvents.StepStarted + ":b");
        aStart.ShouldBeLessThan(aDone);
        aDone.ShouldBeLessThan(bStart);
    }

    private static DeploymentResource Res(string name, int delay = 0, string[]? depends = null)
    {
        var spec = new JsonObject();
        if (delay > 0)
            spec["_test"] = new JsonObject { ["delayMs"] = delay };
        return new DeploymentResource { Name = name, Kind = DeploymentResourceKind.Process, Spec = spec, DependsOn = depends?.ToList() ?? [] };
    }

    private static async Task WaitUntil(Func<Task<bool>> pred)
    {
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 200; i++)
        {
            if (await pred())
                return;
            await Task.Delay(10, ct);
        }

        throw new TimeoutException("condition not reached");
    }

    private static Task WaitUntil(Func<bool> pred) => WaitUntil(() => Task.FromResult(pred()));
}
