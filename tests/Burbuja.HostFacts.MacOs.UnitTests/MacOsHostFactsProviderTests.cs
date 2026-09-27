using System.ComponentModel;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Host;
using Burbuja.Core.Abstractions.Process;
using Burbuja.Core.Process;
using Burbuja.Plugins.HostFactsMacOs;
using Shouldly;
using Xunit;

namespace Burbuja.HostFactsMacOs.UnitTests;

/// <summary>
/// The provider's contract is totality: whatever the OS answers, it always comes back with a
/// <see cref="HostFacts"/> and never throws (bar a cancelled caller). These facts drive that with
/// stub runners rather than the real machine, so "no battery here" can be proven anywhere.
/// </summary>
public sealed class MacOsHostFactsProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 + 90_000);

    [Fact]
    public void Platform_is_macos()
    {
        new MacOsHostFactsProvider(new StubProcessRunner(Ok("")), new ExecutionGrantAuthority(new FixedClock(Now)), new FixedClock(Now))
            .Platform.ShouldBe("macos");
    }

    [Fact]
    public async Task Every_command_failing_still_returns_facts_with_every_field_null()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(1, "", "boom", false));

        var facts = (await CollectAsync(runner)).ShouldNotBeNull();

        facts.Battery.ShouldBe(new BatteryFacts(null, null, null));
        facts.MemoryFreeMb.ShouldBeNull();
        facts.MemoryPressureLevel.ShouldBeNull();
        facts.SwapUsedMb.ShouldBeNull();
        facts.DiskFreeGb.ShouldBeNull();
        facts.CpuSpeedLimit.ShouldBeNull();
        facts.UptimeSec.ShouldBeNull();
        facts.SleepDisabled.ShouldBeNull();
        facts.TailscaleOnline.ShouldBeNull();
        facts.ProcessRssMb.ShouldBeNull();
        facts.At.ShouldBe(Now);
        facts.Extra["batterySource"].ShouldBe("unknown");
        facts.Extra["batteryEstimateSource"].ShouldBe("unknown");
    }

    [Fact]
    public async Task A_runner_that_throws_for_every_spawn_still_returns_facts_with_every_field_null()
    {
        // A missing binary is a Win32Exception out of Process.Start — the shape of "this is not a
        // macOS host at all" (or a sandbox with no /usr/bin). It is the provider's job to absorb it.
        var runner = new StubProcessRunner(_ => throw new Win32Exception(2, "No such file or directory"));

        var facts = (await CollectAsync(runner)).ShouldNotBeNull();

        facts.Battery.Percent.ShouldBeNull();
        facts.MemoryFreeMb.ShouldBeNull();
        facts.MemoryPressureLevel.ShouldBeNull();
        facts.SwapUsedMb.ShouldBeNull();
        facts.DiskFreeGb.ShouldBeNull();
        facts.CpuSpeedLimit.ShouldBeNull();
        facts.UptimeSec.ShouldBeNull();
        facts.SleepDisabled.ShouldBeNull();
        facts.TailscaleOnline.ShouldBeNull();
        facts.ProcessRssMb.ShouldBeNull();
    }

    [Fact]
    public async Task Garbage_output_throws_nothing_and_reads_as_unknown()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(0, "\0\u0001not a fact\n%%%\n-1 -\n", "", false));

        var facts = (await CollectAsync(runner)).ShouldNotBeNull();

        facts.Battery.ShouldBe(new BatteryFacts(null, null, null));
        facts.MemoryFreeMb.ShouldBeNull();
        facts.MemoryPressureLevel.ShouldBeNull();
        facts.SwapUsedMb.ShouldBeNull();
        facts.DiskFreeGb.ShouldBeNull();
        // The one shape in that garbage the tailscale parser can read: a first line with no "offline"
        // is online. Garbage is not a reason to fabricate, and not a reason to invent "unknown" either.
        facts.TailscaleOnline.ShouldBe(true);
    }

    [Fact]
    public async Task A_tailscale_cli_that_cannot_be_started_is_unknown_and_never_offline()
    {
        var runner = new StubProcessRunner(request =>
            IsTailscaleStatus(request)
                ? throw new Win32Exception(2, "No such file or directory")
                : new ProcessResult(1, "", "boom", false));

        var facts = (await CollectAsync(runner)).ShouldNotBeNull();

        facts.TailscaleOnline.ShouldBeNull();
        // "absent" is a claim about this machine, so it may only be made when no candidate binary was
        // found; a named CLI has to be a real path.
        var cli = facts.Extra["tailscaleCli"];
        (cli == "absent" || File.Exists(cli)).ShouldBe(true);
    }

    [Fact]
    public async Task A_grant_that_cannot_be_minted_skips_the_probes_instead_of_throwing()
    {
        var runner = new StubProcessRunner(Ok(""));

        var facts = (await CollectAsync(runner, new RefusingGrantAuthority())).ShouldNotBeNull();

        runner.Calls.ShouldBeEmpty();
        facts.Battery.ShouldBe(new BatteryFacts(null, null, null));
        facts.At.ShouldBe(Now);
        facts.Extra["batterySource"].ShouldBe("unknown");
        facts.Extra["batteryEstimateSource"].ShouldBe("unknown");
    }

    [Fact]
    public async Task Cancellation_is_the_one_thing_that_does_propagate()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var runner = new StubProcessRunner(_ => throw new OperationCanceledException(cts.Token));
        var provider = new MacOsHostFactsProvider(
            runner,
            new ExecutionGrantAuthority(new FixedClock(Now)),
            new FixedClock(Now));

        await Should.ThrowAsync<OperationCanceledException>(async () => await provider.CollectAsync(cts.Token));
    }

    [Fact]
    public async Task The_probe_set_is_the_documented_one_with_a_five_second_timeout_and_a_64_kb_cap()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(1, "", "", false));

        await CollectAsync(runner);

        var commands = runner.Calls.Select(c => string.Join(' ', new[] { c.FileName }.Concat(c.Arguments))).ToList();
        commands.ShouldContain("/usr/bin/pmset -g batt");
        commands.ShouldContain("/usr/sbin/ioreg -rn AppleSmartBattery");
        commands.ShouldContain("/usr/bin/pmset -g therm");
        commands.ShouldContain("/usr/bin/vm_stat");
        commands.ShouldContain("/usr/sbin/sysctl kern.memorystatus_level");
        commands.ShouldContain("/usr/sbin/sysctl vm.swapusage");
        commands.ShouldContain("/bin/df -k /");
        commands.ShouldContain("/usr/sbin/sysctl -n kern.boottime");
        commands.ShouldContain("/usr/bin/pmset -g");
        commands.ShouldContain($"/bin/ps -o rss= -p {Environment.ProcessId}");
        runner.Calls.Count(IsTailscaleStatus).ShouldBe(1);
        runner.Calls.Count.ShouldBe(11);

        foreach (var call in runner.Calls)
        {
            call.Timeout.ShouldBe(TimeSpan.FromSeconds(5));
            call.MaxOutputBytes.ShouldBe(64 * 1024);
            call.WorkingDirectory.ShouldBe("/");
            call.RedactOutput.ShouldBe(false);
        }
    }

    [Fact]
    public async Task One_grant_is_minted_for_the_whole_collection()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(1, "", "", false));
        var clock = new FixedClock(Now);

        await CollectAsync(runner, new ExecutionGrantAuthority(clock), clock);

        var grants = runner.Grants.Distinct().ToList();
        grants.Count.ShouldBe(1);
        grants[0].SubjectId.ShouldBe("hostfacts:macos");
        grants[0].DecisionKey.ShouldBe("hostfacts.collect");
        grants[0].ExpiresAt.ShouldBe(Now.AddMinutes(1));
    }

    [Fact]
    public async Task Every_field_is_filled_from_the_captured_fixtures()
    {
        var runner = new StubProcessRunner(FakeMac.Respond);

        var facts = (await CollectAsync(runner)).ShouldNotBeNull();

        // The AC-connected gauge in the fixtures answers 65535 (IOKit's "no estimate"), so the
        // estimate is pmset's own `1:28` and Extra says where it came from.
        facts.Battery.ShouldBe(new BatteryFacts(63m, true, 88));
        facts.MemoryFreeMb.ShouldBe(4694m);
        facts.MemoryPressureLevel.ShouldBe(57m);
        facts.SwapUsedMb.ShouldBe(512m);
        facts.DiskFreeGb.ShouldBe(300m);
        facts.CpuSpeedLimit.ShouldBe("62");
        facts.UptimeSec.ShouldBe(90_000m);
        facts.SleepDisabled.ShouldBe(true);
        facts.TailscaleOnline.ShouldBe(true);
        facts.ProcessRssMb.ShouldBe(2m);
        facts.At.ShouldBe(Now);
        facts.Extra["batterySource"].ShouldBe("ac");
        facts.Extra["batteryEstimateSource"].ShouldBe("pmset");
        facts.Extra["memoryPageSizeBytes"].ShouldBe("16384");
        facts.Extra["diskTotalGb"].ShouldBe("500");
        facts.Extra["diskUsedGb"].ShouldBe("100");
        facts.Extra["tailscaleCli"].ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task The_gauge_beats_the_pmset_line_it_rounds()
    {
        // The whole reason the ioreg probe exists. Both fixtures model a battery-powered Mac:
        // pmset printed `70%; discharging; 20:00 remaining` (1200 minutes) and the synthetic gauge says 1440.
        var runner = new StubProcessRunner(request => request switch
        {
            _ when IsIoreg(request) => Fixture("ioreg-AppleSmartBattery-discharging.txt"),
            _ when IsPmsetBattery(request) => Fixture("pmset-batt-discharging.txt"),
            _ => FakeMac.Respond(request),
        });

        var facts = (await CollectAsync(runner)).ShouldNotBeNull();

        facts.Battery.ShouldBe(new BatteryFacts(70m, false, 1440));
        facts.Extra["batteryEstimateSource"].ShouldBe("gauge");
        facts.Extra["batterySource"].ShouldBe("battery");
    }

    [Fact]
    public async Task A_gauge_that_cannot_be_read_falls_back_to_the_pmset_estimate()
    {
        // `ioreg` failing is not a battery fact: the rounded estimate is still worth showing.
        var runner = new StubProcessRunner(request => IsIoreg(request)
            ? new ProcessResult(1, "", "ioreg: not permitted", false)
            : FakeMac.Respond(request));

        var facts = (await CollectAsync(runner)).ShouldNotBeNull();

        facts.Battery.ShouldBe(new BatteryFacts(63m, true, 88));
        facts.Extra["batteryEstimateSource"].ShouldBe("pmset");
    }

    [Fact]
    public void Every_probe_binary_exists_on_this_mac()
    {
        // The stub runner cannot see this one: it answers ANY path, so a probe whose path is wrong
        // passes every other fact in this file and returns nothing on the real machine. That is not
        // hypothetical — the ioreg probe shipped as `/usr/bin/ioreg` and ioreg lives in `/usr/sbin`,
        // so the first live host fell back to pmset's rounded estimate and `batteryEstimateSource`
        // said "pmset" where it should have said "gauge". A probe set is a fact about the host.
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "these are macOS paths");

        MacOsHostFactsProvider.ProbePaths.Where(path => !File.Exists(path)).ShouldBeEmpty();
    }

    private static Task<HostFacts?> CollectAsync(IProcessRunner runner) =>
        CollectAsync(runner, new ExecutionGrantAuthority(new FixedClock(Now)), new FixedClock(Now));

    private static async Task<HostFacts?> CollectAsync(
        IProcessRunner runner,
        IExecutionGrantAuthority grants,
        IClock? clock = null) =>
        await new MacOsHostFactsProvider(runner, grants, clock ?? new FixedClock(Now)).CollectAsync(Ct);

    private static bool IsTailscaleStatus(ProcessStartRequest request) =>
        request.Arguments.Count == 1 && request.Arguments[0] == "status";

    private static bool IsIoreg(ProcessStartRequest request) =>
        request.FileName.EndsWith("ioreg", StringComparison.Ordinal);

    private static bool IsPmsetBattery(ProcessStartRequest request) =>
        request.FileName.EndsWith("pmset", StringComparison.Ordinal)
        && request.Arguments.SequenceEqual(["-g", "batt"]);

    private static Func<ProcessStartRequest, ProcessResult> Ok(string stdout) =>
        _ => new ProcessResult(0, stdout, "", false);

    /// <summary>One successful command answered with one captured fixture.</summary>
    private static ProcessResult Fixture(string name) =>
        new(0, Fixtures.Read(name), "", false);
}

/// <summary>Records every spawn (and the grant it carried) so a fact can assert on the request shape
/// itself — the probes run concurrently, hence the lock.</summary>
internal sealed class StubProcessRunner(Func<ProcessStartRequest, ProcessResult> respond) : IProcessRunner
{
    private readonly object _gate = new();
    private readonly List<ProcessStartRequest> _calls = [];
    private readonly List<ExecutionGrant> _grants = [];

    public IReadOnlyList<ProcessStartRequest> Calls
    {
        get
        {
            lock (_gate)
                return [.. _calls];
        }
    }

    public IReadOnlyList<ExecutionGrant> Grants
    {
        get
        {
            lock (_gate)
                return [.. _grants];
        }
    }

    public ValueTask<ProcessResult> RunAsync(ProcessStartRequest request, ExecutionGrant grant, CancellationToken ct)
    {
        lock (_gate)
        {
            _calls.Add(request);
            _grants.Add(grant);
        }

        return new(respond(request));
    }
}

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}

/// <summary>An authority that cannot mint — the plugin was wired into a container without one.</summary>
internal sealed class RefusingGrantAuthority : IExecutionGrantAuthority
{
    public ExecutionGrant Mint(string subjectId, string decisionKey, TimeSpan ttl) =>
        throw new InvalidOperationException("no grant authority");
}

/// <summary>Answers each probe with the fixture captured from that exact command.</summary>
internal static class FakeMac
{
    public static ProcessResult Respond(ProcessStartRequest request)
    {
        if (request.Arguments.Count == 1 && request.Arguments[0] == "status")
            return Ok("tailscale-status-online.txt");

        var command = string.Join(' ', new[] { request.FileName }.Concat(request.Arguments));
        // The pid is not a constant, so this one probe cannot be a switch arm.
        if (command.StartsWith("/bin/ps -o rss= -p ", StringComparison.Ordinal))
            return Ok("ps-rss.txt");

        return command switch
        {
            "/usr/bin/pmset -g batt" => Ok("pmset-batt-charging.txt"),
            "/usr/sbin/ioreg -rn AppleSmartBattery" => Ok("ioreg-AppleSmartBattery-charging.txt"),
            "/usr/bin/pmset -g therm" => Ok("pmset-therm-intel.txt"),
            "/usr/bin/pmset -g" => Ok("pmset-g.txt"),
            "/usr/bin/vm_stat" => Ok("vm-stat.txt"),
            "/usr/sbin/sysctl kern.memorystatus_level" => Ok("sysctl-memorystatus-level.txt"),
            "/usr/sbin/sysctl vm.swapusage" => Ok("sysctl-swapusage.txt"),
            "/usr/sbin/sysctl -n kern.boottime" => Ok("sysctl-boottime.txt"),
            "/bin/df -k /" => Ok("df-k-root.txt"),
            _ => new ProcessResult(1, "", $"unexpected probe: {command}", false),
        };
    }

    private static ProcessResult Ok(string fixture) => new(0, Fixtures.Read(fixture), "", false);
}
