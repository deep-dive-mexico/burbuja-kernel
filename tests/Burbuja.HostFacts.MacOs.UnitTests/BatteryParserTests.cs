using Burbuja.Core.Abstractions.Host;
using Burbuja.Plugins.HostFactsMacOs.Parsers;
using Shouldly;
using Xunit;

namespace Burbuja.HostFactsMacOs.UnitTests;

public sealed class BatteryParserTests
{
    [Fact]
    public void A_discharging_battery_reports_percent_estimate_and_not_charging()
    {
        var battery = BatteryParser.Parse(Fixtures.Read("pmset-batt-discharging.txt"));

        battery.Percent.ShouldBe(70m);
        battery.Charging.ShouldBe(false);
        // "20:00 remaining" is twenty hours, in minutes — the unit pmset prints is h:mm, and this is
        // the conversion that stops a reader seeing twenty minutes.
        battery.EstimateMinutes.ShouldBe(1200);
        BatteryParser.Source(Fixtures.Read("pmset-batt-discharging.txt")).ShouldBe("battery");
    }

    [Fact]
    public void A_charging_battery_reports_charging()
    {
        var text = Fixtures.Read("pmset-batt-charging.txt");

        var battery = BatteryParser.Parse(text);

        battery.Percent.ShouldBe(63m);
        battery.Charging.ShouldBe(true);
        battery.EstimateMinutes.ShouldBe(88);
        BatteryParser.Source(text).ShouldBe("ac");
    }

    [Fact]
    public void A_desktop_Mac_with_no_battery_is_unknown_in_every_member_never_zero()
    {
        var text = Fixtures.Read("pmset-batt-no-battery.txt");

        var battery = BatteryParser.Parse(text);

        battery.Percent.ShouldBeNull();
        battery.Charging.ShouldBeNull();
        battery.EstimateMinutes.ShouldBeNull();
        battery.ShouldBe(new BatteryFacts(null, null, null));
        // The AC line is still a real answer about power source, even with no battery to report.
        BatteryParser.Source(text).ShouldBe("ac");
    }

    [Fact]
    public void A_full_battery_on_AC_reports_no_estimate_rather_than_zero_minutes()
    {
        // pmset prints `0:00 remaining` here. That is "no estimate" in the shape of one: nothing is
        // about to run out, and rendering it would put "0m restante" on a machine at 100% on AC.
        var text = Fixtures.Read("pmset-batt-charged.txt");

        var battery = BatteryParser.Parse(text);

        battery.Percent.ShouldBe(100m);
        battery.Charging.ShouldBe(false);
        battery.EstimateMinutes.ShouldBeNull();
    }

    [Fact]
    public void Holding_at_80_percent_is_not_charging_and_has_no_estimate()
    {
        // "not charging" must not be read as charging: the reference anchors on the semicolon so
        // `; discharging` and `; not charging` both stay false.
        var battery = BatteryParser.Parse(Fixtures.Read("pmset-batt-not-charging.txt"));

        battery.Percent.ShouldBe(80m);
        battery.Charging.ShouldBe(false);
        battery.EstimateMinutes.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Now drawing from 'Battery Power'\n")]
    [InlineData("pmset: command not found")]
    public void No_percentage_anywhere_is_all_null(string stdout)
    {
        BatteryParser.Parse(stdout).ShouldBe(new BatteryFacts(null, null, null));
    }

    [Theory]
    [InlineData("", "unknown")]
    [InlineData("100%", "unknown")]
    public void Source_is_unknown_when_pmset_named_neither_source(string stdout, string expected)
    {
        BatteryParser.Source(stdout).ShouldBe(expected);
    }
}
