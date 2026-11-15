using System.Text.Json.Nodes;
using Burbuja.Cluster.Abstractions;
using Burbuja.Cluster.Agent;
using Burbuja.Cluster.Os;
using Shouldly;
using Xunit;

namespace Burbuja.Cluster.Tests;

/// <summary>Real process trees on this machine: started, suspended, resumed, stopped and adopted.</summary>
public sealed class ProcessDriverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cluster-process-" + Guid.NewGuid().ToString("N"));
    private readonly ClusterOptions _options;
    private readonly List<IWorkloadInstance> _started = [];

    public ProcessDriverTests()
    {
        Directory.CreateDirectory(_root);
        _options = new ClusterOptions { Enabled = true, NodeId = "studio", StateDirectory = Path.Combine(_root, "state") };
    }

    public void Dispose()
    {
        foreach (var instance in _started) instance.StopAsync(TimeSpan.Zero, CancellationToken.None).AsTask().Wait(TimeSpan.FromSeconds(10));
        Directory.Delete(_root, true);
    }

    private ProcessWorkloadDriver Driver(string script, Dictionary<string, string>? flags = null)
    {
        var path = Path.Combine(_root, "program.sh");
        File.WriteAllText(path, script);
        _options.Agent.Programs["job"] = new ProgramOptions
        {
            Executable = "/bin/sh",
            Arguments = [path, "fixed"],
            ParameterFlags = flags ?? new(),
            WorkingDirectory = _root,
            Environment = new() { ["OPERATOR_SET"] = "yes" },
        };
        return new ProcessWorkloadDriver(_options, [new MacOsExecutionAdapter(), new LinuxExecutionAdapter(), new PortableExecutionAdapter()]);
    }

    private static WorkloadStart Start(JsonObject? parameters = null, ExecutionProfile? profile = null) => new(
        new WorkloadSpec
        {
            Name = "job",
            Payload = new JsonObject { ["program"] = "job", ["parameters"] = parameters },
            Execution = profile ?? new ExecutionProfile(),
        },
        Generation: 7, NodeId: "studio", new Dictionary<string, string> { ["BURBUJA_WORKLOAD"] = "job" }, Devices: []);

    private async Task<IWorkloadInstance> Run(ProcessWorkloadDriver driver, WorkloadStart start)
    {
        var instance = await driver.StartAsync(start, TestContext.Current.CancellationToken);
        _started.Add(instance);
        return instance;
    }

    private static int Pid(IWorkloadInstance instance) => int.Parse(instance.Handle.Split(':')[0]);

    private static async Task<string> StateOf(int pid) =>
        (await ProbeCommand.ReadAsync("/bin/ps", ["-o", "state=", "-p", pid.ToString()], TimeSpan.FromSeconds(3), CancellationToken.None))?.Trim() ?? "gone";

    private async Task WaitForFile(string name)
    {
        var path = Path.Combine(_root, name);
        for (var i = 0; i < 200 && !File.Exists(path); i++) await Task.Delay(25, TestContext.Current.CancellationToken);
        File.Exists(path).ShouldBeTrue($"{name} was never written");
    }

    [Fact]
    public async Task Parameters_are_literal_arguments_and_the_environment_is_the_operators()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX process semantics");
        Environment.SetEnvironmentVariable("CLUSTER_TEST_HOST_SECRET", "must-not-leak");
        var driver = Driver("printf '%s\\n' \"$@\" > args\nenv > environment\n", new() { ["prompt"] = "--prompt" });
        var hostile = "$(touch injected); `touch injected2`; ' \" *";

        var instance = await Run(driver, Start(new JsonObject { ["prompt"] = hostile }));
        (await instance.Completion).ExitCode.ShouldBe(0);

        File.ReadAllLines(Path.Combine(_root, "args")).ShouldBe(["fixed", "--prompt", hostile]);
        File.Exists(Path.Combine(_root, "injected")).ShouldBeFalse();
        File.Exists(Path.Combine(_root, "injected2")).ShouldBeFalse();

        var environment = File.ReadAllLines(Path.Combine(_root, "environment"));
        environment.ShouldContain("OPERATOR_SET=yes");
        environment.ShouldContain("BURBUJA_WORKLOAD=job");
        environment.ShouldNotContain(l => l.StartsWith("CLUSTER_TEST_HOST_SECRET"));
    }

    [Fact]
    public async Task A_node_refuses_programs_and_parameters_it_does_not_list()
    {
        var driver = Driver("exit 0\n", new() { ["shard"] = "--shard" });
        var ct = TestContext.Current.CancellationToken;

        var unknown = Start() with { Spec = new WorkloadSpec { Name = "job", Payload = new JsonObject { ["program"] = "rm" } } };
        (await Should.ThrowAsync<WorkloadRefusedException>(async () => await driver.StartAsync(unknown, ct)))
            .Message.ShouldBe("program 'rm' is not allowed on this node");
        (await Should.ThrowAsync<WorkloadRefusedException>(async () => await driver.StartAsync(Start(new JsonObject { ["command"] = "x" }), ct)))
            .Message.ShouldBe("program 'job' does not accept parameter 'command'");

        driver.Validate(unknown.Spec).RequiredCapabilities.ShouldBe(["program.rm"]);
        driver.Validate(new WorkloadSpec { Name = "x", Payload = new JsonObject { ["program"] = 5 } }).Errors.ShouldBe(["payload.program must name a program"]);
        driver.Validate(new WorkloadSpec { Name = "x", Payload = new JsonObject { ["program"] = "job", ["parameters"] = new JsonArray() } })
            .Errors.ShouldBe(["payload.parameters must be an object of strings"]);

        var description = new NodeDescription();
        driver.Describe(description);
        description.Capabilities.ShouldBe(["program.job"]);
    }

    [Fact]
    public async Task The_whole_tree_is_suspended_resumed_and_given_its_grace_to_finish()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX process semantics");
        // A parent that starts a child of its own, so the test covers the tree and not just its root.
        var driver = Driver("""
            trap 'echo finishing > stopped; kill $child; exit 0' TERM
            sleep 300 &
            child=$!
            echo $child > child
            echo ready > ready
            wait $child
            """);
        var instance = await Run(driver, Start(profile: new ExecutionProfile { Priority = ExecutionPriority.Background, YieldToInteractive = true }));
        await WaitForFile("ready");
        var child = int.Parse(File.ReadAllText(Path.Combine(_root, "child")).Trim());

        await instance.PauseAsync(CancellationToken.None);
        (await StateOf(Pid(instance))).ShouldStartWith("T");
        (await StateOf(child)).ShouldStartWith("T");

        await instance.ResumeAsync(CancellationToken.None);
        (await StateOf(Pid(instance))).ShouldNotStartWith("T");
        (await StateOf(child)).ShouldNotStartWith("T");

        // Stopping suspended work resumes it first, so it can hear the request and finish properly.
        await instance.PauseAsync(CancellationToken.None);
        await instance.StopAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        (await instance.Completion).ExitCode.ShouldBe(0);
        File.ReadAllText(Path.Combine(_root, "stopped")).Trim().ShouldBe("finishing");
        (await StateOf(child)).ShouldBe("gone");
    }

    [Fact]
    public async Task Work_that_ignores_the_request_is_ended_when_its_grace_runs_out()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX process semantics");
        var driver = Driver("trap '' TERM\necho ready > ready\nwhile true; do sleep 1; done\n");
        var instance = await Run(driver, Start());
        await WaitForFile("ready");

        var started = DateTimeOffset.UtcNow;
        await instance.StopAsync(TimeSpan.FromMilliseconds(400), CancellationToken.None);
        instance.Completion.IsCompleted.ShouldBeTrue();
        (await instance.Completion).ExitCode.ShouldNotBe(0);
        (DateTimeOffset.UtcNow - started).ShouldBeGreaterThan(TimeSpan.FromMilliseconds(350));
        (await StateOf(Pid(instance))).ShouldBe("gone");
    }

    [Fact]
    public async Task A_later_node_process_adopts_running_work_only_when_it_is_provably_the_same()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX process semantics");
        var driver = Driver("echo out; echo err >&2; echo ready > ready\nwhile true; do sleep 1; done\n");
        var original = await Run(driver, Start());
        await WaitForFile("ready");
        var ct = TestContext.Current.CancellationToken;

        // Output went to a file, not to a pipe the first node process holds: it survives that process.
        File.ReadAllText(driver.LogPath("job")).ShouldBe("out\nerr\n");

        var successor = Driver("exit 0\n");
        var adopted = await successor.AdoptAsync(Start(), original.Handle, ct);
        adopted.ShouldNotBeNull();
        adopted.Handle.ShouldBe(original.Handle);
        adopted.Completion.IsCompleted.ShouldBeFalse();

        // The same process id with another start time is a different process that reused the number.
        var pid = Pid(original);
        (await successor.AdoptAsync(Start(), $"{pid}:{long.Parse(original.Handle.Split(':')[1]) - 60_000}", ct)).ShouldBeNull();
        (await successor.AdoptAsync(Start(), "999999:1", ct)).ShouldBeNull();
        (await successor.AdoptAsync(Start(), "garbage", ct)).ShouldBeNull();

        await adopted.StopAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        (await adopted.Completion).ExitCode.ShouldBeNull(); // Not its child: the status cannot be read.
        (await StateOf(pid)).ShouldBe("gone");
    }
}
