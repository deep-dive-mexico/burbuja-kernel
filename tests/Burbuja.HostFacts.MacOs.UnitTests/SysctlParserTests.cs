using Burbuja.Plugins.HostFactsMacOs.Parsers;
using Shouldly;
using Xunit;

namespace Burbuja.HostFactsMacOs.UnitTests;

public sealed class SysctlParserTests
{
    [Fact]
    public void Memory_pressure_level_is_read_from_the_named_output()
    {
        // `sysctl kern.memorystatus_level` prints "kern.memorystatus_level: 58"; `-n` prints "58".
        SysctlParser.ParseMemoryPressureLevel(Fixtures.Read("sysctl-memorystatus-level.txt")).ShouldBe(57m);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("100", 100)]
    [InlineData("  42\n", 42)]
    [InlineData("kern.memorystatus_level: 15", 15)]
    public void Memory_pressure_level_accepts_both_the_named_and_the_bare_form(string stdout, int expected)
    {
        SysctlParser.ParseMemoryPressureLevel(stdout).ShouldBe((decimal)expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sysctl: unknown oid 'kern.memorystatus_level'")]
    [InlineData("kern.memorystatus_level: 200")]
    [InlineData("kern.memorystatus_level: -1")]
    public void A_memory_pressure_level_that_is_not_a_level_is_unknown(string stdout)
    {
        SysctlParser.ParseMemoryPressureLevel(stdout).ShouldBeNull();
    }

    [Fact]
    public void Swap_in_use_is_parsed_and_rounded_to_whole_megabytes()
    {
        // vm.swapusage: total = 2048.00M  used = 512.25M  free = 1023.75M  (encrypted)
        SysctlParser.ParseSwapUsedMb(Fixtures.Read("sysctl-swapusage.txt")).ShouldBe(512m);
    }

    [Theory]
    [InlineData("vm.swapusage: total = 8.00G  used = 1.50G  free = 6.50G  (encrypted)", 1536)]
    [InlineData("vm.swapusage: total = 0.00M  used = 0.00M  free = 0.00M", 0)]
    public void Swap_honours_the_unit_and_reports_a_real_zero_as_zero(string stdout, int expected)
    {
        SysctlParser.ParseSwapUsedMb(stdout).ShouldBe((decimal)expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sysctl: unknown oid 'vm.swapusage'")]
    [InlineData("vm.swapusage: total = 2048.00M  free = 1275.12M")]
    public void Unreadable_swap_is_unknown(string stdout)
    {
        SysctlParser.ParseSwapUsedMb(stdout).ShouldBeNull();
    }

    [Fact]
    public void Uptime_is_measured_against_the_supplied_now()
    {
        // { sec = 1700000000, usec = 123456 } Sat Sep 12 09:13:22 2026
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 + 90_000);

        SysctlParser.ParseUptimeSeconds(Fixtures.Read("sysctl-boottime.txt"), now).ShouldBe(90_000m);
    }

    [Fact]
    public void A_boot_time_in_the_future_is_unknown_not_a_negative_uptime()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 - 10);

        SysctlParser.ParseUptimeSeconds(Fixtures.Read("sysctl-boottime.txt"), now).ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ usec = 208525 }")]
    public void Unreadable_boot_time_is_unknown(string stdout)
    {
        SysctlParser.ParseUptimeSeconds(stdout, DateTimeOffset.UnixEpoch.AddYears(60)).ShouldBeNull();
    }
}
