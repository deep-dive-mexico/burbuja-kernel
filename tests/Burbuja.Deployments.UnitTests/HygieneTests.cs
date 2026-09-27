using System.Text.Json;
using Burbuja.Core.Abstractions.Arm;
using Microsoft.Extensions.DependencyInjection;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Arm;
using Burbuja.Core.Sdk.Authorization;
using Burbuja.Deployments;
using Burbuja.Deployments.Abstractions;
using Shouldly;
using Xunit;

namespace Burbuja.Deployments.UnitTests;

public sealed class HygieneTests
{
    [Fact]
    public void Schemas_compile()
    {
        var env = SchemaFromEmbedded("environment.schema.json");
        var dep = SchemaFromEmbedded("deployment.schema.json");
        Should.NotThrow(() => SchemaBuilder.Compile(env));
        Should.NotThrow(() => SchemaBuilder.Compile(dep));
    }

    private static System.Text.Json.JsonElement SchemaFromEmbedded(string fileName)
    {
        var asm = typeof(DeploymentsPlugin).Assembly;
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return SchemaBuilder.Parse(reader.ReadToEnd());
    }

    [Fact]
    public void Roles_are_in_catalogue()
    {
        using var sp = Harness.CreateHost();
        var table = sp.GetRequiredService<ArmRoutingTable>();
        foreach (var role in new DeploymentsRoles().Roles)
        {
            foreach (var action in role.Actions.Concat(role.DataActions))
                ArmActionMatcher.IsInCatalogue(action, table.ActionCatalogue).ShouldBeTrue($"{role.Name} {action}");
        }
    }

    [Fact]
    public void Deployments_assembly_does_not_reference_core_or_agents()
    {
        foreach (var name in typeof(DeploymentsPlugin).Assembly.GetReferencedAssemblies())
        {
            var n = name.Name ?? "";
            n.ShouldNotBe("Burbuja.Core");
            n.ShouldNotBe("Burbuja.Host");
            n.StartsWith("Burbuja.Agents", StringComparison.Ordinal).ShouldBeFalse();
        }

        foreach (var name in typeof(DeploymentsConstants).Assembly.GetReferencedAssemblies())
        {
            var n = name.Name ?? "";
            n.ShouldNotBe("Burbuja.Core");
            n.ShouldNotBe("Burbuja.Core.Sdk");
            n.StartsWith("Burbuja.Agents", StringComparison.Ordinal).ShouldBeFalse();
        }
    }

    [Fact]
    public void Enums_are_strings_on_the_wire()
    {
        var json = JsonSerializer.Serialize(new DeploymentProperties
        {
            Mode = DeploymentMode.Incremental,
            ExecutionState = DeploymentExecutionState.Succeeded,
            Steps = [new DeploymentStep { Change = StepChange.Create, Status = StepStatus.Succeeded, Kind = DeploymentResourceKind.Process }],
        }, JsonWire.Options);
        json.ShouldContain("\"mode\":\"incremental\"");
        json.ShouldContain("\"executionState\":\"succeeded\"");
        json.ShouldContain("\"change\":\"create\"");
        json.ShouldContain("\"kind\":\"process\"");
        json.ShouldNotContain("\"executionState\":0");
        json.ShouldNotContain("\"mode\":0");
    }
}
