using Burbuja.Cluster.Abstractions;
using Burbuja.Cluster.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Burbuja.Cluster.Tests;

/// <summary>One workload on one node, with a driver the test controls.</summary>
public sealed class SupervisorTests
{
    private sealed class ScriptedDriver(int failuresBeforeStart, bool refuse = false, bool brokenValidation = false) : IWorkloadDriver
    {
        public int Attempts;
        public readonly TaskCompletionSource<WorkloadExit> Exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<string> Calls = [];
        public TimeSpan? StopGrace;

        public string Kind => "scripted";
        public WorkloadValidation Validate(WorkloadSpec spec) =>
            brokenValidation ? throw new InvalidCastException("a plugin bug") : WorkloadValidation.Ok;

        public ValueTask<IWorkloadInstance> StartAsync(WorkloadStart start, CancellationToken ct)
        {
            if (refuse) throw new WorkloadRefusedException("not on this node");
            if (Interlocked.Increment(ref Attempts) <= failuresBeforeStart) throw new IOException("disk full");
            return ValueTask.FromResult<IWorkloadInstance>(new Instance(this));
        }

        private sealed class Instance(ScriptedDriver driver) : IWorkloadInstance
        {
            public string Handle => "scripted";
            public Task<WorkloadExit> Completion => driver.Exit.Task;
            public ValueTask PauseAsync(CancellationToken ct) { lock (driver.Calls) driver.Calls.Add("pause"); return ValueTask.CompletedTask; }
            public ValueTask ResumeAsync(CancellationToken ct) { lock (driver.Calls) driver.Calls.Add("resume"); return ValueTask.CompletedTask; }
            public ValueTask StopAsync(TimeSpan grace, CancellationToken ct)
            {
                driver.StopGrace = grace;
                lock (driver.Calls) driver.Calls.Add("stop");
                driver.Exit.TrySetResult(new WorkloadExit(0));
                return ValueTask.CompletedTask;
            }
        }
    }

    private static (WorkloadSupervisor Supervisor, InteractiveGate Gate) Supervise(
        ScriptedDriver driver, WorkloadSpec spec, AgentOptions? node = null, string? adopt = null)
    {
        var gate = new InteractiveGate(TimeProvider.System, new ClusterOptions());
        var context = new SupervisorContext(
            "studio", node ?? new AgentOptions(), new HashSet<string> { "pause", "priority" }, () => [], new DeviceLedger(), gate,
            new Dictionary<string, string>(), TimeProvider.System, NullLogger.Instance, () => { });
        return (new WorkloadSupervisor(new WorkloadAssignment(spec, 3), driver, context, adopt), gate);
    }

    private static WorkloadSpec Spec(RestartPolicy restart, bool yields = false) => new()
    {
        Name = "job",
        Kind = "scripted",
        Lifecycle = new WorkloadLifecycle { Restart = restart, RestartDelay = TimeSpan.FromMilliseconds(5) },
        Execution = new ExecutionProfile { YieldToInteractive = yields },
    };

    private static async Task<WorkloadReport> Until(WorkloadSupervisor supervisor, Func<WorkloadReport, bool> done)
    {
        for (var i = 0; i < 400 && !done(supervisor.Report()); i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        return supervisor.Report();
    }

    [Fact]
    public async Task Work_that_cannot_start_follows_its_restart_policy_like_work_that_failed()
    {
        var retried = new ScriptedDriver(failuresBeforeStart: 2);
        var (supervisor, _) = Supervise(retried, Spec(RestartPolicy.OnFailure));
        var running = await Until(supervisor, r => r.Phase == WorkloadPhase.Running);
        running.Phase.ShouldBe(WorkloadPhase.Running);
        running.Restarts.ShouldBe(2);
        running.Generation.ShouldBe(3);

        // A clean exit under "on failure" means done, not "start it again".
        retried.Exit.SetResult(new WorkloadExit(0));
        (await Until(supervisor, r => r.Phase == WorkloadPhase.Completed)).Phase.ShouldBe(WorkloadPhase.Completed);
        await supervisor.Completion;
        retried.Attempts.ShouldBe(3);

        var (once, _) = Supervise(new ScriptedDriver(failuresBeforeStart: 1), Spec(RestartPolicy.Never));
        var failed = await Until(once, r => r.Phase == WorkloadPhase.Failed);
        failed.Phase.ShouldBe(WorkloadPhase.Failed);
        failed.Reason.ShouldBe("it could not be started on this node");
    }

    [Fact]
    public async Task A_refusal_is_final_for_the_generation_and_carries_the_nodes_reason()
    {
        var (byDriver, _) = Supervise(new ScriptedDriver(0, refuse: true), Spec(RestartPolicy.Always));
        var refused = await Until(byDriver, r => r.Phase == WorkloadPhase.Refused);
        refused.Reason.ShouldBe("not on this node");
        await byDriver.Completion;

        // The node's own policy is asked before the driver is.
        var driver = new ScriptedDriver(0);
        var (byPolicy, _) = Supervise(driver, Spec(RestartPolicy.Always), new AgentOptions { Kinds = ["process"] });
        (await Until(byPolicy, r => r.Phase == WorkloadPhase.Refused)).Reason.ShouldBe("kind 'scripted' is not allowed on this node");
        driver.Attempts.ShouldBe(0);
    }

    [Fact]
    public async Task Yielding_work_is_suspended_for_a_hold_and_a_stop_wins_over_a_later_hold()
    {
        var driver = new ScriptedDriver(0);
        var (supervisor, gate) = Supervise(driver, Spec(RestartPolicy.Never, yields: true));
        gate.Changed += busy => { _ = supervisor.ApplyGateAsync(); };
        await Until(supervisor, r => r.Phase == WorkloadPhase.Running);

        gate.Hold("turn", TimeSpan.FromSeconds(30));
        (await Until(supervisor, r => r.Phase == WorkloadPhase.Paused)).Reason.ShouldBe("yielding to interactive work on this node");
        gate.Release("turn");
        (await Until(supervisor, r => r.Phase == WorkloadPhase.Running)).Phase.ShouldBe(WorkloadPhase.Running);

        // A notification that arrives late is harmless: the gate is read when it is applied, so work is never
        // left suspended by a "busy" that was already over.
        gate.Hold("turn", TimeSpan.FromSeconds(30));
        gate.Release("turn");
        await supervisor.ApplyGateAsync();
        (await Until(supervisor, r => r.Phase == WorkloadPhase.Running)).Phase.ShouldBe(WorkloadPhase.Running);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        supervisor.Report().Phase.ShouldBe(WorkloadPhase.Running);
        lock (driver.Calls) driver.Calls.Count(c => c == "pause").ShouldBe(driver.Calls.Count(c => c == "resume"));

        supervisor.RequestStop("no longer assigned to this node");
        await supervisor.Completion;
        gate.Hold("turn", TimeSpan.FromSeconds(30)); // Arrives after the stop: nothing left to suspend.
        await supervisor.ApplyGateAsync();

        var report = supervisor.Report();
        report.Phase.ShouldBe(WorkloadPhase.Stopped);
        report.Reason.ShouldBe("no longer assigned to this node");
        driver.Calls.Last().ShouldBe("stop");
    }

    [Fact]
    public async Task A_stop_that_must_finish_within_a_bound_caps_the_works_own_grace()
    {
        var spec = Spec(RestartPolicy.Never) with { Lifecycle = new WorkloadLifecycle { StopGrace = TimeSpan.FromMinutes(20) } };
        var patient = new ScriptedDriver(0);
        var (ordinary, _) = Supervise(patient, spec);
        await Until(ordinary, r => r.Phase == WorkloadPhase.Running);
        ordinary.RequestStop("no longer assigned to this node");
        await ordinary.Completion;
        patient.StopGrace.ShouldBe(TimeSpan.FromMinutes(20));

        // The node process is stopping: it cannot wait twenty minutes and still leave nothing behind.
        var hurried = new ScriptedDriver(0);
        var (leaving, _) = Supervise(hurried, spec);
        await Until(leaving, r => r.Phase == WorkloadPhase.Running);
        leaving.RequestStop("the node is shutting down", graceLimit: TimeSpan.FromSeconds(20));
        await leaving.Completion;
        hurried.StopGrace.ShouldBe(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task A_node_does_not_trust_a_name_or_a_driver_and_never_looks_alive_after_a_fault()
    {
        // The name becomes file names on the node, so the node checks it whatever the controller accepted.
        foreach (var name in new[] { "../../etc/x", "/absolute", "..", ".hidden", "UPPER", "" })
        {
            var driver = new ScriptedDriver(0);
            var (escaped, _) = Supervise(driver, Spec(RestartPolicy.Always) with { Name = name });
            (await Until(escaped, r => r.Phase == WorkloadPhase.Refused)).Reason.ShouldBe("the workload name is not valid");
            driver.Attempts.ShouldBe(0);
        }

        // A driver that throws something unexpected ends the supervisor in a state the controller can act on.
        var (broken, _) = Supervise(new ScriptedDriver(0, brokenValidation: true), Spec(RestartPolicy.Always));
        await broken.Completion;
        broken.Report().Phase.ShouldBe(WorkloadPhase.Failed);
        broken.Report().Reason.ShouldBe("the node could not supervise it (InvalidCastException)");
    }

    [Fact]
    public async Task Work_left_by_a_previous_node_process_is_adopted_or_left_alone_but_never_started_afresh()
    {
        // The scripted driver cannot adopt anything, like a process that is gone.
        var driver = new ScriptedDriver(0);
        var (supervisor, _) = Supervise(driver, Spec(RestartPolicy.Always), adopt: "gone:1");
        await supervisor.Completion;

        supervisor.Report().Phase.ShouldBe(WorkloadPhase.Stopped);
        supervisor.Report().Reason.ShouldBe("it was no longer running when the node restarted");
        supervisor.Restartable.ShouldBeTrue(); // The controller may ask for it again; the node did not decide that alone.
        driver.Attempts.ShouldBe(0);
    }
}
