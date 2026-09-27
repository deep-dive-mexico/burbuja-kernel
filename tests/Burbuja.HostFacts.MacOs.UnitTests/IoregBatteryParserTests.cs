using Burbuja.Plugins.HostFactsMacOs.Parsers;
using Shouldly;
using Xunit;

namespace Burbuja.HostFactsMacOs.UnitTests;

/// <summary>
/// The gas gauge's own time to empty. The parser exists because <c>pmset</c>'s line is not this
/// number: in the synthetic example the gauge differs from pmset, while
/// <c>AvgTimeToEmpty</c> reports 1440 minutes.
/// </summary>
public sealed class IoregBatteryParserTests
{
    [Fact]
    public void The_synthetic_AppleSmartBattery_dump_reads_as_minutes()
    {
        IoregBatteryParser.ParseMinutes(Fixtures.Read("ioreg-AppleSmartBattery-discharging.txt"))
            .ShouldBe(1440);
    }

    [Fact]
    public void The_averaged_value_wins_over_the_instantaneous_one()
    {
        // Both keys are present on a real discharging Mac and they usually agree; when they do not,
        // AvgTimeToEmpty is the number macOS itself reports.
        const string Text = """
            +-o AppleSmartBattery  <class AppleSmartBattery, id 0x1000009b7>
                {
                  "TimeRemaining" = 12
                  "AvgTimeToEmpty" = 90
                }
            """;

        IoregBatteryParser.ParseMinutes(Text).ShouldBe(90);
    }

    [Fact]
    public void The_instantaneous_value_is_the_fallback_when_there_is_no_average()
    {
        const string Text = """
            +-o AppleSmartBattery  <class AppleSmartBattery, id 0x1000009b7>
                {
                  "IsCharging" = No
                  "TimeRemaining" = 47
                }
            """;

        IoregBatteryParser.ParseMinutes(Text).ShouldBe(47);
    }

    [Theory]
    [InlineData("\"AvgTimeToEmpty\" = 65535")]                       // 0xFFFF: IOKit's "no estimate"
    [InlineData("\"AvgTimeToEmpty\" = 65535\n\"TimeRemaining\" = 65535")]
    [InlineData("\"AvgTimeToEmpty\" = 0")]                           // nothing is about to run out
    [InlineData("\"AvgTimeToEmpty\" = -1")]                          // not a shape ioreg prints
    [InlineData("\"AvgTimeToFull\" = 65535")]                        // a different question
    [InlineData("")]
    [InlineData("ioreg: command not found")]
    [InlineData("\"AvgTimeToEmpty\" = not-a-number")]
    public void Anything_that_is_not_a_real_estimate_reads_as_unknown(string stdout)
    {
        IoregBatteryParser.ParseMinutes(stdout).ShouldBeNull();
    }

    [Fact]
    public void A_null_dump_is_unknown_rather_than_zero()
    {
        // The provider hands this whatever the process runner produced, and "the probe failed" must
        // not become "the battery is empty".
        IoregBatteryParser.ParseMinutes(null).ShouldBeNull();
    }
}
