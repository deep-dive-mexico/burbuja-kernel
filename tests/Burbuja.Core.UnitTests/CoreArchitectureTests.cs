using Shouldly;
using Xunit;
using Burbuja.Core.Clock;

namespace Burbuja.Core.UnitTests;

public sealed class CoreArchitectureTests
{
    [Fact]
    public void Core_does_not_reference_Agents()
    {
        foreach (var name in typeof(TimeProviderClock).Assembly.GetReferencedAssemblies())
        {
            name.Name.ShouldNotBeNull();
            name.Name.StartsWith("Burbuja.Agents", StringComparison.Ordinal).ShouldBeFalse();
        }
    }

    [Fact]
    public void Assembly_names_are_unchanged_by_the_product_rename()
    {
        // The PluginLoadContext unifies *Abstractions* / Core.Sdk by string-matching the assembly
        // name, so the burbuja-server product rename must never leak into an assembly name.
        typeof(Burbuja.Core.Abstractions.Arm.ResourceId).Assembly.GetName().Name
            .ShouldBe("Burbuja.Core.Abstractions");
        typeof(Burbuja.Core.Sdk.Policy.PolicyLogic).Assembly.GetName().Name
            .ShouldBe("Burbuja.Core.Sdk");
        typeof(TimeProviderClock).Assembly.GetName().Name
            .ShouldBe("Burbuja.Core");
    }
}
