using Burbuja.Plugins.HostFactsMacOs.Parsers;
using Shouldly;
using Xunit;

namespace Burbuja.HostFactsMacOs.UnitTests;

public sealed class PsParserTests
{
    [Fact]
    public void Rss_is_read_from_the_synthetic_ps_output()
    {
        // ps-rss.txt is "  2016" — RSS in 1024-byte blocks → 1.97 MB → 2 MB.
        PsParser.ParseRssMb(Fixtures.Read("ps-rss.txt")).ShouldBe(2m);
    }

    [Theory]
    [InlineData("262144", 256)]
    [InlineData("   1024\n", 1)]
    [InlineData("0", 0)]
    public void Rss_in_kilobytes_becomes_whole_megabytes(string stdout, int expected)
    {
        PsParser.ParseRssMb(stdout).ShouldBe((decimal)expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  RSS\n")]
    [InlineData("ps: No such process")]
    public void Unreadable_rss_is_unknown(string stdout)
    {
        PsParser.ParseRssMb(stdout).ShouldBeNull();
    }
}
