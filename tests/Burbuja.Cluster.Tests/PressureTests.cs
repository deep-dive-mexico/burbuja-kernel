using Burbuja.Cluster.Abstractions;
using Burbuja.Cluster.Agent;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Burbuja.Cluster.Tests;

public sealed class PressureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cluster-pressure-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static NodeObservation Reading(PressureLevel level, params string[] reasons) => new() { Pressure = level, Reasons = reasons };

    [Fact]
    public void A_node_reaches_its_own_verdict_with_the_same_hysteresis_and_forgets_it_when_it_goes_stale()
    {
        var time = new FakeTimeProvider();
        var monitor = new NodePressureMonitor(new ClusterOptions(), time);
        INodePressure pressure = monitor;

        // Before any reading the verdict is unknown, which is not the same as clear.
        pressure.Local.ShouldBe(NodePressure.Unknown);

        monitor.Sample(Reading(PressureLevel.Elevated, "processors are busy"));
        monitor.Sample(Reading(PressureLevel.Critical, "memory is low"));
        pressure.Local.Known.ShouldBeTrue();
        pressure.Local.Pressured.ShouldBeFalse();
        monitor.Sample(Reading(PressureLevel.Elevated, "processors are busy"));
        pressure.Local.Pressured.ShouldBeTrue();
        pressure.Local.Reasons.ShouldBe(["processors are busy"]);

        for (var i = 0; i < 5; i++) monitor.Sample(Reading(PressureLevel.Normal));
        pressure.Local.Pressured.ShouldBeTrue();
        monitor.Sample(Reading(PressureLevel.Normal));
        pressure.Local.Pressured.ShouldBeFalse();

        // A node that stopped reading itself does not keep reporting its last answer.
        for (var i = 0; i < 3; i++) monitor.Sample(Reading(PressureLevel.Critical));
        pressure.Local.Pressured.ShouldBeTrue();
        time.Advance(TimeSpan.FromSeconds(91));
        pressure.Local.ShouldBe(NodePressure.Unknown);
    }

    [Fact]
    public void Advice_about_other_members_expires_so_it_never_keeps_excluding_a_node()
    {
        var time = new FakeTimeProvider();
        var monitor = new NodePressureMonitor(new ClusterOptions(), time);
        INodePressure pressure = monitor;
        pressure.PressuredNodes.ShouldBeEmpty();

        monitor.Advise(["tower", "edge"]);
        pressure.PressuredNodes.ShouldBe(["tower", "edge"], ignoreOrder: true);
        monitor.Advise(["edge"]);
        pressure.PressuredNodes.ShouldBe(["edge"]);

        time.Advance(TimeSpan.FromSeconds(91));
        pressure.PressuredNodes.ShouldBeEmpty();
    }

    [Fact]
    public void The_controller_tells_every_member_which_members_are_pressed()
    {
        var lab = new Lab();
        lab.Join(Lab.Studio, Lab.Tower);
        lab.Sync(Lab.Studio).PressuredNodes.ShouldBeEmpty();

        for (var i = 0; i < 3; i++) lab.Sync(Lab.Tower, "first", PressureLevel.Elevated);
        lab.Sync(Lab.Studio).PressuredNodes.ShouldBe(["tower"]);

        // A member that went silent is unreachable, not pressed: its old verdict is not passed on.
        lab.Time.Advance(TimeSpan.FromSeconds(25));
        lab.Sync(Lab.Studio).PressuredNodes.ShouldBeEmpty();
    }

    [Fact]
    public void Load_is_judged_against_the_machines_processors_not_a_containers_limit()
    {
        Directory.CreateDirectory(_root);
        var cpuinfo = Path.Combine(_root, "cpuinfo");
        File.WriteAllText(cpuinfo, string.Concat(Enumerable.Range(0, 16).Select(i => $"processor\t: {i}\nmodel name\t: Synthetic CPU\nprocessor_note: not a processor line\n\n")));

        // A container limited to one processor still sees all sixteen here, which is what the load average counts.
        SystemFactsSource.HostProcessors(cpuinfo).ShouldBe(16);
        SystemFactsSource.HostProcessors(Path.Combine(_root, "absent")).ShouldBeNull();
        File.WriteAllText(cpuinfo, "nothing useful\n");
        SystemFactsSource.HostProcessors(cpuinfo).ShouldBeNull();
    }

    [Fact]
    public void The_hottest_thermal_zone_is_read_and_missing_or_absurd_values_are_not_a_reading()
    {
        SystemFactsSource.ReadLinuxTemperature(Path.Combine(_root, "absent")).ShouldBeNull();
        foreach (var (zone, value) in new[] { ("thermal_zone0", "45000\n"), ("thermal_zone1", "81500"), ("thermal_zone2", "garbage"), ("thermal_zone3", "999000"), ("cooling_device0", "95000") })
        {
            Directory.CreateDirectory(Path.Combine(_root, zone));
            File.WriteAllText(Path.Combine(_root, zone, "temp"), value);
        }

        SystemFactsSource.ReadLinuxTemperature(_root).ShouldBe(81.5m);
    }
}
