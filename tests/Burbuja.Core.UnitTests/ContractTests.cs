using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Json;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests;

public sealed class HygieneTests
{
    [Fact]
    public void CoreAbstractions_does_not_reference_Agents()
    {
        foreach (var name in typeof(IClock).Assembly.GetReferencedAssemblies())
        {
            name.Name.ShouldNotBeNull();
            name.Name.StartsWith("Burbuja.Agents", StringComparison.Ordinal).ShouldBeFalse();
        }
    }

    [Fact]
    public void CoreAbstractions_references_only_allowed_assemblies()
        => AssertAllowedReferences(typeof(IClock).Assembly);

    [Fact]
    public void CoreSdk_references_only_Abstractions_and_JsonSchema()
    {
        foreach (var name in typeof(Burbuja.Core.Arm.ArmConstants).Assembly.GetReferencedAssemblies())
        {
            var n = name.Name ?? "";
            var allowed =
                n.StartsWith("System", StringComparison.Ordinal)
                || n.StartsWith("Microsoft.NETCore", StringComparison.Ordinal)
                || n == "netstandard"
                || n == "Burbuja.Core.Abstractions"
                || n == "Json.More"
                || n == "JsonSchema.Net";
            allowed.ShouldBeTrue($"Unexpected assembly reference '{n}' from Burbuja.Core.Sdk");
        }
    }

    private static void AssertAllowedReferences(System.Reflection.Assembly assembly)
    {
        foreach (var name in assembly.GetReferencedAssemblies())
        {
            var n = name.Name ?? "";
            var allowed =
                n.StartsWith("System", StringComparison.Ordinal)
                || n.StartsWith("Microsoft.Extensions", StringComparison.Ordinal)
                || n.StartsWith("Microsoft.NETCore", StringComparison.Ordinal)
                || n == "netstandard"
                || n == "Burbuja.Core.Abstractions";
            allowed.ShouldBeTrue($"Unexpected assembly reference '{n}' from {assembly.GetName().Name}");
        }
    }
}

public sealed class PolymorphicRoundTripTests
{
    private readonly JsonSerializerOptions _options = JsonWire.Create();

    [Fact]
    public void ApprovalOutcome_each_shape_round_trips()
    {
        ApprovalOutcome[] cases =
        [
            new Approved(true, "shape", "ok"),
            new Answered("use pytest"),
            new Denied("no"),
            new TimedOut(),
            new Cancelled(),
            new Deferred(),
        ];
        foreach (var original in cases)
        {
            var json = JsonSerializer.Serialize(original, _options);
            var back = JsonSerializer.Deserialize<ApprovalOutcome>(json, _options);
            back.ShouldNotBeNull();
            back.GetType().ShouldBe(original.GetType());
            using (var doc = JsonDocument.Parse(json))
                doc.RootElement.GetProperty("status").GetString().ShouldNotBeNullOrEmpty();
            switch (original)
            {
                case Approved a:
                    back.ShouldBeOfType<Approved>().Note.ShouldBe(a.Note);
                    break;
                case Answered ans:
                    back.ShouldBeOfType<Answered>().Answer.ShouldBe(ans.Answer);
                    break;
                case Denied d:
                    back.ShouldBeOfType<Denied>().Note.ShouldBe(d.Note);
                    break;
            }
            json = JsonSerializer.Serialize(back, _options);
            JsonSerializer.Deserialize<ApprovalOutcome>(json, _options)!.GetType().ShouldBe(original.GetType());
        }
    }

    [Fact]
    public void DeleteOutcome_each_shape_round_trips()
    {
        DeleteOutcome[] cases =
        [
            new DeleteOutcome.Deleted(),
            new DeleteOutcome.NotFound(),
            new DeleteOutcome.Accepted("op-1", TimeSpan.FromSeconds(5)),
            new DeleteOutcome.Rejected(ArmError.NotFound("missing")),
        ];
        foreach (var original in cases)
        {
            var json = JsonSerializer.Serialize(original, _options);
            var back = JsonSerializer.Deserialize<DeleteOutcome>(json, _options);
            back.ShouldNotBeNull();
            back.GetType().ShouldBe(original.GetType());
        }
    }

    [Fact]
    public void ActionOutcome_each_shape_round_trips()
    {
        ActionOutcome[] cases =
        [
            new ActionOutcome.Ok(new JsonObject { ["ok"] = true }),
            new ActionOutcome.NoContent(),
            new ActionOutcome.Accepted("op-2", TimeSpan.FromSeconds(2), null),
            new ActionOutcome.Rejected(ArmError.Forbidden("no")),
        ];
        foreach (var original in cases)
        {
            var json = JsonSerializer.Serialize(original, _options);
            var back = JsonSerializer.Deserialize<ActionOutcome>(json, _options);
            back.ShouldNotBeNull();
            back.GetType().ShouldBe(original.GetType());
        }
    }

    [Fact]
    public void PutOutcome_each_shape_round_trips()
    {
        var envelope = new ResourceEnvelope<DummyProps>(
            "/subscriptions/s/resourceGroups/rg/providers/Burbuja.Agents/workspaces/w",
            "w",
            "Burbuja.Agents/workspaces",
            "local",
            new Dictionary<string, string>(),
            new DummyProps { Title = "t" },
            ProvisioningState.Succeeded,
            new SystemData(null, null, null, null, null, null),
            "W/\"1\"");
        PutOutcome<DummyProps>[] cases =
        [
            new PutOutcome<DummyProps>.Created(envelope),
            new PutOutcome<DummyProps>.Updated(envelope),
            new PutOutcome<DummyProps>.Accepted(envelope, "op-3", TimeSpan.FromSeconds(1)),
            new PutOutcome<DummyProps>.Rejected(ArmError.Conflict("Conflict", "in flight")),
        ];
        foreach (var original in cases)
        {
            var json = JsonSerializer.Serialize(original, _options);
            var back = JsonSerializer.Deserialize<PutOutcome<DummyProps>>(json, _options);
            back.ShouldNotBeNull();
            back.GetType().ShouldBe(original.GetType());
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("kind").GetString().ShouldNotBeNullOrEmpty();
            if (original is PutOutcome<DummyProps>.Accepted)
            {
                doc.RootElement.GetProperty("retryAfter").GetInt32().ShouldBe(1);
                doc.RootElement.GetProperty("operationId").GetString().ShouldBe("op-3");
                back.ShouldBeOfType<PutOutcome<DummyProps>.Accepted>().OperationId.ShouldBe("op-3");
            }
            if (original is PutOutcome<DummyProps>.Created)
                back.ShouldBeOfType<PutOutcome<DummyProps>.Created>().Resource.Properties.Title.ShouldBe("t");
        }
    }

    [Fact]
    public void ContentBlock_known_and_unknown_round_trip()
    {
        ContentBlock[] cases =
        [
            new TextBlock("hola"),
            new ToolUseBlock("tu_1", "read_file", new JsonObject { ["path"] = "a.py" }),
            new ToolResultBlock("tu_1", JsonValue.Create("ok")!, false),
            new ThinkingBlock("hmm"),
        ];
        foreach (var original in cases)
        {
            var json = JsonSerializer.Serialize(original, _options);
            var back = JsonSerializer.Deserialize<ContentBlock>(json, _options);
            back.ShouldNotBeNull();
            back.GetType().ShouldBe(original.GetType());
            using var parsed = JsonDocument.Parse(json);
            parsed.RootElement.GetProperty("type").GetString().ShouldNotBeNullOrEmpty();
            switch (original)
            {
                case TextBlock t:
                    parsed.RootElement.GetProperty("text").GetString().ShouldBe(t.Text);
                    JsonSerializer.Serialize(t, _options).ShouldContain("\"type\":\"text\"");
                    break;
                case ToolUseBlock u:
                    parsed.RootElement.GetProperty("name").GetString().ShouldBe(u.Name);
                    parsed.RootElement.GetProperty("id").GetString().ShouldBe(u.Id);
                    break;
                case ToolResultBlock r:
                    parsed.RootElement.GetProperty("tool_use_id").GetString().ShouldBe(r.ToolUseId);
                    break;
                case ThinkingBlock th:
                    parsed.RootElement.GetProperty("thinking").GetString().ShouldBe(th.Thinking);
                    break;
            }
        }

        const string raw = """{"type":"redacted_thinking","data":"abc"}""";
        var unknown = JsonSerializer.Deserialize<ContentBlock>(raw, _options);
        unknown.ShouldBeOfType<UnknownBlock>();
        var again = JsonSerializer.Serialize(unknown, _options);
        using var doc = JsonDocument.Parse(again);
        doc.RootElement.GetProperty("type").GetString().ShouldBe("redacted_thinking");
    }

    [Fact]
    public void Enums_serialize_as_camel_case_strings()
    {
        var json = JsonSerializer.Serialize(ProvisioningState.Succeeded, _options);
        json.ShouldBe("\"succeeded\"");
        JsonSerializer.Deserialize<ProvisioningState>(json, _options).ShouldBe(ProvisioningState.Succeeded);
    }

    [Fact]
    public void ToolChoice_round_trips()
    {
        ToolChoice auto = new ToolChoiceAuto();
        ToolChoice forced = new ToolChoiceTool("emit_spec");
        JsonSerializer.Deserialize<ToolChoice>(JsonSerializer.Serialize(auto, _options), _options)
            .ShouldBeOfType<ToolChoiceAuto>();
        var back = JsonSerializer.Deserialize<ToolChoice>(JsonSerializer.Serialize(forced, _options), _options);
        back.ShouldBeOfType<ToolChoiceTool>();
        ((ToolChoiceTool)back!).Name.ShouldBe("emit_spec");
    }

    public sealed class DummyProps
    {
        public string? Title { get; set; }
    }
}
