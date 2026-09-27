using Burbuja.Plugins.HostFactsMacOs.Parsers;
using Shouldly;
using Xunit;

namespace Burbuja.HostFactsMacOs.UnitTests;

public sealed class PmsetParserTests
{
    [Fact]
    public void An_Apple_Silicon_therm_report_with_no_speed_limit_is_unknown()
    {
        // The synthetic output is three "Note: …" lines and nothing else. macOS reporting
        // nothing is not the same as macOS reporting 100.
        var text = Fixtures.Read("pmset-therm-absent.txt");

        text.ShouldNotContain("CPU_Speed_Limit");
        PmsetParser.ParseCpuSpeedLimit(text).ShouldBeNull();
    }

    [Fact]
    public void An_Intel_therm_report_yields_the_speed_limit_as_printed()
    {
        PmsetParser.ParseCpuSpeedLimit(Fixtures.Read("pmset-therm-intel.txt")).ShouldBe("62");
    }

    [Theory]
    [InlineData("CPU_Speed_Limit = 100", "100")]
    [InlineData("CPU_Speed_Limit     = 31", "31")]
    [InlineData("", null)]
    [InlineData("Note: No thermal warning level has been recorded", null)]
    public void The_speed_limit_is_the_value_pmset_printed(string stdout, string? expected)
    {
        PmsetParser.ParseCpuSpeedLimit(stdout).ShouldBe(expected);
    }

    [Fact]
    public void Sleep_disabled_is_read_from_the_captured_power_settings()
    {
        // pmset-g.txt carries " SleepDisabled\t\t1": the example has sleep disabled.
        PmsetParser.ParseSleepDisabled(Fixtures.Read("pmset-g.txt")).ShouldBe(true);
    }

    [Theory]
    [InlineData("System-wide power settings:\n SleepDisabled\t\t0\n", false)]
    [InlineData(" SleepDisabled 1", true)]
    [InlineData("System-wide power settings:\n", null)]
    [InlineData("", null)]
    public void Sleep_disabled_is_null_only_when_the_setting_is_absent(string stdout, bool? expected)
    {
        PmsetParser.ParseSleepDisabled(stdout).ShouldBe(expected);
    }
}
