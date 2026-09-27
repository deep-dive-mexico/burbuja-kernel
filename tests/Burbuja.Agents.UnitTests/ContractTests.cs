using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions.Arbiter;
using Burbuja.Agents.Abstractions.Domain;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Agents.Abstractions.Orchestration;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Abstractions.Policy;
using Shouldly;
using Xunit;

namespace Burbuja.Agents.UnitTests;

public sealed class ContractTests
{
    private readonly JsonSerializerOptions _options = JsonWire.Create();

    [Fact]
    public void Job_and_run_state_round_trip()
    {
        var job = new Job(
            Guid.NewGuid(),
            new JobSpec { Title = "t", Slug = "slug", Brief = "b", Language = "es" },
            JobExecutionStatus.Running,
            new JobBudgets(5, 8, 10, 20),
            new JobCost(1, 0, 0, 0, 10, 4),
            "1",
            new Dictionary<string, string> { ["burbuja.io/scenario"] = "happy-path" });
        var back = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job, _options), _options);
        back.ShouldNotBeNull();
        back.Status.ShouldBe(JobExecutionStatus.Running);
        back.Spec.Slug.ShouldBe("slug");
        back.Tags["burbuja.io/scenario"].ShouldBe("happy-path");

        var state = new AgentRunState(1, 2, 0, 0, 0, false, null, null, "tok", [new InputMessage("user", [new TextBlock("hi")])], null, null, null, null);
        var stateBack = JsonSerializer.Deserialize<AgentRunState>(JsonSerializer.Serialize(state, _options), _options);
        stateBack!.Turn.ShouldBe(2);
        stateBack.Transcript.Count.ShouldBe(1);
    }

    [Fact]
    public void Llm_response_stop_reason_round_trips()
    {
        var resp = new LlmResponse(LlmStopReason.ToolUse, [new TextBlock("x")], new Usage(1, 2, 0, 0), "tool_use");
        var json = JsonSerializer.Serialize(resp, _options);
        json.ShouldContain("\"stopReason\":\"toolUse\"");
        var back = JsonSerializer.Deserialize<LlmResponse>(json, _options);
        back!.StopReason.ShouldBe(LlmStopReason.ToolUse);
        back.StopReasonRaw.ShouldBe("tool_use");
    }

    [Fact]
    public void Structured_request_carries_forced_tool_choice()
    {
        var req = new StructuredLlmRequest("m", [], "sys", new ToolSpec("emit_spec", "d", new JsonObject()), new ToolChoiceTool("emit_spec"), 16);
        var back = JsonSerializer.Deserialize<StructuredLlmRequest>(JsonSerializer.Serialize(req, _options), _options);
        back!.ToolChoice.ShouldBeOfType<ToolChoiceTool>();
        ((ToolChoiceTool)back.ToolChoice).Name.ShouldBe("emit_spec");
    }

    [Fact]
    public void Arbiter_request_tier_is_risk_tier()
    {
        var req = new ArbiterRequest(
            new Burbuja.Agents.Abstractions.Tools.ToolCallSubject("bash", new JsonObject(), ".", "r", "j", false),
            "b",
            "c",
            RiskTier.Gray,
            "p",
            1,
            0);
        var json = JsonSerializer.Serialize(req, _options);
        json.ShouldContain("\"classifierTier\":\"gray\"");
        JsonSerializer.Deserialize<ArbiterRequest>(json, _options)!.ClassifierTier.ShouldBe(RiskTier.Gray);
    }

    [Fact]
    public void Agent_run_role_is_enum()
    {
        var run = new AgentRun(Guid.NewGuid(), Guid.NewGuid(), null, null, AgentRunRole.Worker, 1, "n", 0.1m);
        var json = JsonSerializer.Serialize(run, _options);
        json.ShouldContain("\"role\":\"worker\"");
        JsonSerializer.Deserialize<AgentRun>(json, _options)!.Role.ShouldBe(AgentRunRole.Worker);
    }

    [Fact]
    public void Event_types_are_namespaced()
    {
        AgentEventTypes.Init.ShouldStartWith("agents.");
        AgentEventTypes.ToolIntent.ShouldBe("agents.tool.intent");
        AgentEventTypes.SteeringReceived.ShouldBe("agents.steering.received");
    }
}
