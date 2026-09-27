using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Deployments.Execution;
using Shouldly;
using Xunit;

namespace Burbuja.Deployments.UnitTests;

public sealed class ExpressionResolverTests
{
    [Fact]
    public void Whole_string_preserves_json_type()
    {
        var errors = new List<ArmError>();
        var node = ExpressionResolver.Resolve(JsonValue.Create("${parameters.count}"), new JsonObject { ["count"] = 3 }, [], "/p", errors);
        errors.ShouldBeEmpty();
        node!.GetValue<int>().ShouldBe(3);
    }

    [Fact]
    public void Spliced_expression_becomes_string()
    {
        var errors = new List<ArmError>();
        var node = ExpressionResolver.Resolve(JsonValue.Create("pre-${parameters.name}-post"), new JsonObject { ["name"] = "x" }, [], "/p", errors);
        errors.ShouldBeEmpty();
        node!.GetValue<string>().ShouldBe("pre-x-post");
    }

    [Fact]
    public void Dollar_dollar_escapes()
    {
        var errors = new List<ArmError>();
        var node = ExpressionResolver.Resolve(JsonValue.Create("$$not"), [], [], "/p", errors);
        errors.ShouldBeEmpty();
        node!.GetValue<string>().ShouldBe("$not");
    }

    [Fact]
    public void Unresolved_records_pointer()
    {
        var errors = new List<ArmError>();
        ExpressionResolver.Resolve(JsonValue.Create("${parameters.missing}"), [], [], "/spec/name", errors);
        errors.ShouldHaveSingleItem();
        errors[0].Target.ShouldBe("/spec/name");
        errors[0].Code.ShouldBe("UnresolvedReference");
    }

    [Fact]
    public void Step_output_lookup()
    {
        var errors = new List<ArmError>();
        var outputs = new JsonObject { ["db"] = new JsonObject { ["host"] = "h1" } };
        var node = ExpressionResolver.Resolve(JsonValue.Create("${steps.db.outputs.host}"), [], outputs, "/p", errors);
        errors.ShouldBeEmpty();
        node!.GetValue<string>().ShouldBe("h1");
    }
}
