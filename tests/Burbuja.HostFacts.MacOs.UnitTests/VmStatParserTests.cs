using Burbuja.Plugins.HostFactsMacOs.Parsers;
using Shouldly;
using Xunit;

namespace Burbuja.HostFactsMacOs.UnitTests;

public sealed class VmStatParserTests
{
    // vm-stat.txt: free 20000 + inactive 250000 + speculative 30422 = 300422 pages, at the 16384-byte
    // page size the header names → 4_922_114_048 bytes → 4694.10 MB, floored to 4694.
    [Fact]
    public void Free_memory_sums_free_inactive_and_speculative_at_the_reported_page_size()
    {
        var text = Fixtures.Read("vm-stat.txt");

        VmStatParser.ParsePageSizeBytes(text).ShouldBe(16384);
        VmStatParser.ParseFreeMegabytes(text).ShouldBe(4694m);
    }

    [Fact]
    public void The_page_size_is_read_from_the_header_not_assumed()
    {
        // The same page counts at the 4096-byte page size an Intel Mac reports are a quarter of the
        // RAM. If the page size were assumed (the reference defaults to 4096), this would read 4694.
        var intel = Fixtures.Read("vm-stat.txt").Replace("page size of 16384 bytes", "page size of 4096 bytes", StringComparison.Ordinal);

        VmStatParser.ParsePageSizeBytes(intel).ShouldBe(4096);
        VmStatParser.ParseFreeMegabytes(intel).ShouldBe(1173m);
    }

    [Fact]
    public void A_missing_page_size_is_unknown_rather_than_a_default()
    {
        var noHeader = string.Join('\n', Fixtures.Read("vm-stat.txt").Split('\n').Skip(1));

        VmStatParser.ParsePageSizeBytes(noHeader).ShouldBeNull();
        VmStatParser.ParseFreeMegabytes(noHeader).ShouldBeNull();
    }

    [Fact]
    public void A_missing_page_count_is_unknown_rather_than_zero()
    {
        var noSpeculative = Fixtures.Read("vm-stat.txt")
            .Replace("Pages speculative:                             30422.\n", "", StringComparison.Ordinal);

        noSpeculative.ShouldNotContain("Pages speculative:");
        VmStatParser.ParseFreeMegabytes(noSpeculative).ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Mach Virtual Memory Statistics: (page size of 16384 bytes)\n")]
    [InlineData("not vm_stat output at all")]
    public void Unreadable_output_is_null(string stdout)
    {
        VmStatParser.ParseFreeMegabytes(stdout).ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not vm_stat output at all")]
    public void A_header_that_names_no_page_size_has_no_page_size(string stdout)
    {
        VmStatParser.ParsePageSizeBytes(stdout).ShouldBeNull();
    }
}
