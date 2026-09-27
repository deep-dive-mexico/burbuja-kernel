using Burbuja.Core.Abstractions.Host;
using Burbuja.Core.DependencyInjection;
using Burbuja.Core.Host;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Burbuja.HostFactsMacOs.UnitTests;

public sealed class HostFactsSnapshotTests
{
    private static readonly HostFacts Facts = new(
        new BatteryFacts(50m, false, 240),
        1024m,
        60m,
        0m,
        100m,
        null,
        3600m,
        true,
        null,
        128m,
        new Dictionary<string, string> { ["batterySource"] = "battery" },
        DateTimeOffset.UnixEpoch);

    [Fact]
    public void An_empty_snapshot_has_no_latest()
    {
        new HostFactsSnapshot().Latest.ShouldBeNull();
    }

    [Fact]
    public void Publish_replaces_the_previous_reading()
    {
        var snapshot = new HostFactsSnapshot();
        var second = Facts with { At = DateTimeOffset.UnixEpoch.AddMinutes(1), SwapUsedMb = 7m };

        snapshot.Publish(Facts);
        snapshot.Latest.ShouldBeSameAs(Facts);

        snapshot.Publish(second);
        snapshot.Latest.ShouldBeSameAs(second);
        snapshot.Latest!.SwapUsedMb.ShouldBe(7m);
    }

    [Fact]
    public void Publish_rejects_null_instead_of_erasing_the_last_reading()
    {
        var snapshot = new HostFactsSnapshot();
        snapshot.Publish(Facts);

        // A consumer polls Latest on every tick; a null Publish that silently cleared it would turn a
        // bug in one publisher into "this host has no facts" everywhere.
        Should.Throw<ArgumentNullException>(() => snapshot.Publish(null!));
        snapshot.Latest.ShouldBeSameAs(Facts);
    }

    [Fact]
    public void The_kernel_registers_the_snapshot_for_every_host()
    {
        // The holder is registered by AddBurbujaCore, not by the provider plugin: a host with no
        // provider staged still has a snapshot to read (and a publisher can still be constructed).
        var plugins = Path.Combine(Path.GetTempPath(), "burbuja-hostfacts-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(plugins);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kernel:Role"] = "all",
            ["Kernel:PluginsDirectory"] = plugins,
            ["Kernel:EnabledPlugins"] = "",
        }).Build();
        var services = new ServiceCollection();
        services.AddBurbujaCore(config, []);
        using var provider = services.BuildServiceProvider();

        var snapshot = provider.GetRequiredService<IHostFactsSnapshot>();

        snapshot.ShouldBeOfType<HostFactsSnapshot>();
        snapshot.Latest.ShouldBeNull();
    }
}
