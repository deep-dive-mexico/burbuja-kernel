using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests;

/// <summary>The extracted kernel keeps the legacy opt-in packaging gate with its own public boundary.</summary>
public sealed class PackagingTests
{
    [Fact]
    public void Only_the_explicit_public_artifacts_opt_into_packing()
    {
        string[] expected =
        [
            "Burbuja.Agents.Abstractions", "Burbuja.Compute.Abstractions", "Burbuja.Core",
            "Burbuja.Core.Abstractions", "Burbuja.Core.Sdk", "Burbuja.Deployments.Abstractions",
            "Burbuja.Mcp.Abstractions", "Burbuja.TestKit",
        ];
        var projects = new[] { "src", "plugins", "tests", "tools", "examples" }
            .SelectMany(dir => Directory.EnumerateFiles(Path.Combine(Root(), dir), "*.csproj", SearchOption.AllDirectories))
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin" or "artifacts"));
        var packable = projects.Where(path => XDocument.Load(path).Descendants("IsPackable").Any(p => p.Value == "true"))
            .Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal).ToArray();
        packable.ShouldBe(expected.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Packaging_defaults_to_private_and_points_to_the_authoritative_kernel()
    {
        var props = XDocument.Load(Path.Combine(Root(), "Directory.Build.props"));
        props.Descendants("IsPackable").Single().Value.ShouldBe("false");
        props.Descendants("RepositoryUrl").Single().Value.ShouldBe("https://github.com/deep-dive-mexico/burbuja-kernel.git");
        props.Descendants("PackageLicenseExpression").Single().Value.ShouldBe("MIT");
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Burbuja.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Kernel solution not found above the test assembly.");
    }
}
