using Burbuja.Plugins.HostFactsMacOs.Parsers;
using Shouldly;
using Xunit;

namespace Burbuja.HostFactsMacOs.UnitTests;

public sealed class DiskFreeParserTests
{
    // df-k-root.txt: 524288000 blocks total / 104857600 used / 314572800 available → 500 / 100 / 300 GB.
    [Fact]
    public void The_root_volume_row_is_read_as_total_used_and_free_gigabytes()
    {
        var disk = DiskFreeParser.Parse(Fixtures.Read("df-k-root.txt"));

        disk.ShouldNotBeNull();
        disk.TotalGb.ShouldBe(500m);
        disk.UsedGb.ShouldBe(100m);
        disk.FreeGb.ShouldBe(300m);
    }

    [Fact]
    public void A_volume_whose_name_contains_spaces_does_not_shift_the_columns()
    {
        // //fixture@files.example/Time Machine splits into two tokens, so a parser that reads columns
        // 1-3 of the row (the reference host check does exactly that) would read "Time" as the block
        // count and report nothing. Finding the numbers by their shape is what makes this work.
        var disk = DiskFreeParser.Parse(Fixtures.Read("df-k-volume-with-spaces.txt"));

        disk.ShouldNotBeNull();
        disk.TotalGb.ShouldBe(1907m);
        disk.UsedGb.ShouldBe(477m);
        disk.FreeGb.ShouldBe(1177m);
    }

    [Fact]
    public void A_real_map_auto_home_row_still_parses_a_zero()
    {
        // Captured from `df -k /home`: the filesystem column is "map auto_home" (two tokens) and the
        // %iused column is "-", not a percentage. Every number here is df's own answer — 0 free is a
        // fact about this pseudo-volume, not a parse failure, which is why it must not be null.
        var disk = DiskFreeParser.Parse(Fixtures.Read("df-k-auto-home.txt"));

        disk.ShouldNotBeNull();
        disk.TotalGb.ShouldBe(0m);
        disk.UsedGb.ShouldBe(0m);
        disk.FreeGb.ShouldBe(0m);
    }

    [Theory]
    [InlineData("")]
    [InlineData("df: /: No such file or directory")]
    [InlineData("Filesystem 1024-blocks Used Available Capacity Mounted on\n")]
    [InlineData("Filesystem 1024-blocks Used Available Capacity Mounted on\n/dev/disk3s1s1  971298980  12343064  -\n")]
    public void Unreadable_output_is_unknown(string stdout)
    {
        DiskFreeParser.Parse(stdout).ShouldBeNull();
    }
}
