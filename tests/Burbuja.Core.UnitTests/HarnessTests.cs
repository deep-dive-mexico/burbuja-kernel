using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests;

public sealed class HarnessTests
{
    [Fact]
    public void SkipUnless_runs_when_condition_is_true()
    {
        Assert.SkipUnless(true, "xunit.v3 Assert.SkipUnless must be available");
        true.ShouldBeTrue();
    }
}
