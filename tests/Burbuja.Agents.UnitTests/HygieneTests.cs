using System.Text.Json;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Domain;
using Burbuja.Agents.Model;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Arm;
using Burbuja.Core.Sdk.Authorization;
using Burbuja.Agents.Roles;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Burbuja.Agents.UnitTests;

public sealed class HygieneTests
{
    [Fact]
    public void Schemas_compile()
    {
        Should.NotThrow(() => SchemaBuilder.Compile(AgentsResourceProvider.LoadSchema("workspace.schema.json")));
        Should.NotThrow(() => SchemaBuilder.Compile(AgentsResourceProvider.LoadSchema("job.schema.json")));
    }

    [Fact]
    public void Roles_are_in_catalogue()
    {
        using var sp = Harness.CreateHost();
        var table = sp.GetRequiredService<ArmRoutingTable>();
        foreach (var role in new AgentsRoles().Roles)
        {
            foreach (var action in role.Actions.Concat(role.DataActions))
                ArmActionMatcher.IsInCatalogue(action, table.ActionCatalogue).ShouldBeTrue($"{role.Name} {action}");
        }
    }

    [Fact]
    public void Agents_assembly_does_not_reference_core_or_deployments()
    {
        foreach (var name in typeof(AgentsPlugin).Assembly.GetReferencedAssemblies())
        {
            var n = name.Name ?? "";
            n.ShouldNotBe("Burbuja.Core");
            n.ShouldNotBe("Burbuja.Host");
            n.ShouldNotBe("Burbuja.Deployments");
            n.ShouldNotBe("Burbuja.Deployments.Abstractions");
        }

        foreach (var name in typeof(AgentsConstants).Assembly.GetReferencedAssemblies())
        {
            var n = name.Name ?? "";
            n.ShouldNotBe("Burbuja.Core");
            n.ShouldNotBe("Burbuja.Core.Sdk");
        }
    }

    [Fact]
    public void Enums_are_strings_on_the_wire()
    {
        var json = JsonSerializer.Serialize(new JobProperties
        {
            Status = JobExecutionStatus.Succeeded,
            Spec = new JobSpec { Kind = JobKind.Implement, Language = "es" },
        }, JsonWire.Options);
        json.ShouldContain("\"status\":\"succeeded\"");
        json.ShouldContain("\"kind\":\"implement\"");
        json.ShouldNotContain("\"status\":0");
    }
}
