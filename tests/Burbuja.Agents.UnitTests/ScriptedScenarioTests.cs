using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Agents.Abstractions.Orchestration;
using Burbuja.Agents.Abstractions.Runtime;
using Burbuja.Core.Clock;
using Burbuja.Core.InMemory;
using Burbuja.Plugins.Llm.Scripted;
using Shouldly;
using Xunit;

namespace Burbuja.Agents.UnitTests;

/// <summary>
/// W8 — scenario format v2 (first-call-order script binding, loud failure, AssertConsumed) and
/// v1 backwards compatibility. Provider-level: no host, no HTTP, no clock movement.
/// </summary>
public sealed class ScriptedScenarioTests
{
    private const string ScenarioDir = "scenarios";

    [Fact]
    public async Task Version_one_scenarios_still_load()
    {
        var provider = NewProvider();
        var ambient = Ambient("happy-path", "job-v1", "run-v1");
        var previous = AgentAmbient.Value;
        try
        {
            AgentAmbient.Value = ambient;
            var response = await provider.InvokeAsync(Request(), TestContext.Current.CancellationToken);
            response.StopReasonRaw.ShouldBe("tool_use");
            var tool = response.Blocks.OfType<ToolUseBlock>().Single();
            tool.Name.ShouldBe("write_file");
            provider.AssertConsumed(); // v1 turn 0 is `firstTurn` and was just consumed
        }
        finally
        {
            AgentAmbient.Value = previous;
        }
    }

    [Fact]
    public async Task Scripts_bind_by_first_call_order()
    {
        var provider = NewProvider();
        var previous = AgentAmbient.Value;
        try
        {
            AgentAmbient.Value = Ambient("agent-fork-resume", "job-A", "run-A");
            var first = await provider.InvokeAsync(Request(), TestContext.Current.CancellationToken);
            ToolPath(first).ShouldBe("seed.txt");

            // A different job is a different live session: it claims the next unbound script.
            AgentAmbient.Value = Ambient("agent-fork-resume", "job-B", "run-B");
            var second = await provider.InvokeAsync(Request(), TestContext.Current.CancellationToken);
            ToolPath(second).ShouldBe("fork.txt");

            // ...and the same job stays pinned to the script it already claimed, rather than
            // drifting onto a later script.
            AgentAmbient.Value = Ambient("agent-fork-resume", "job-A", "run-A");
            var pinned = await provider.InvokeAsync(Request(1), TestContext.Current.CancellationToken);
            Text(pinned).ShouldBe("parent done");

            // The third live session gets the third script.
            AgentAmbient.Value = Ambient("agent-fork-resume", "job-C", "run-C");
            var third = await provider.InvokeAsync(Request(), TestContext.Current.CancellationToken);
            Text(third).ShouldBe("resumed after cold start");
        }
        finally
        {
            AgentAmbient.Value = previous;
        }
    }

    [Fact]
    public async Task Unrecorded_call_fails_loud()
    {
        var provider = NewProvider();
        var previous = AgentAmbient.Value;
        try
        {
            AgentAmbient.Value = Ambient("no-such-scenario", "job-x", "run-x");
            var unknown = await Should.ThrowAsync<InvalidOperationException>(
                async () => await provider.InvokeAsync(Request(), TestContext.Current.CancellationToken));
            unknown.Message.ShouldContain("Unknown scripted scenario");

            // agent-fork-resume has exactly three scripts; a fourth live session has nothing to bind.
            foreach (var job in new[] { "job-1", "job-2", "job-3" })
            {
                AgentAmbient.Value = Ambient("agent-fork-resume", job, "run-" + job);
                await provider.InvokeAsync(Request(), TestContext.Current.CancellationToken);
            }

            AgentAmbient.Value = Ambient("agent-fork-resume", "job-4", "run-4");
            var exhausted = await Should.ThrowAsync<InvalidOperationException>(
                async () => await provider.InvokeAsync(Request(), TestContext.Current.CancellationToken));
            exhausted.Message.ShouldContain("no unbound script left");
        }
        finally
        {
            AgentAmbient.Value = previous;
        }
    }

    [Fact]
    public async Task Assert_consumed_throws_on_an_unused_turn()
    {
        var provider = NewProvider();
        var previous = AgentAmbient.Value;
        try
        {
            AgentAmbient.Value = Ambient("subagent-settled-error", "job-ok", "run-ok");
            await provider.InvokeAsync(Request(), TestContext.Current.CancellationToken);

            // job-boom claims the `boom` script but only ever reaches its first turn, so the
            // tool-error turn is left unexercised and must be reported.
            AgentAmbient.Value = Ambient("subagent-settled-error", "job-boom", "run-boom");
            await provider.InvokeAsync(Request(), TestContext.Current.CancellationToken);

            var thrown = Should.Throw<InvalidOperationException>(provider.AssertConsumed);
            thrown.Message.ShouldContain("assert-consumed");
            thrown.Message.ShouldContain("boom turn 1 was never consumed");
        }
        finally
        {
            AgentAmbient.Value = previous;
        }
    }

    [Fact]
    public void Recorder_is_opt_in_only()
    {
        LlmRecorder.EnabledFor("1").ShouldBeTrue();
        LlmRecorder.EnabledFor(" 1 ").ShouldBeTrue();
        LlmRecorder.EnabledFor(null).ShouldBeFalse();
        LlmRecorder.EnabledFor("0").ShouldBeFalse();
        LlmRecorder.EnabledFor("true").ShouldBeFalse();
    }

    [Fact]
    public async Task Recorder_writes_one_file_per_call()
    {
        var dir = Path.Combine(Path.GetTempPath(), "burbuja-llm-record-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("BURBUJA_LLM_RECORD_DIR", dir);
        try
        {
            var recorder = new LlmRecorder(NewProvider());
            var previous = AgentAmbient.Value;
            try
            {
                AgentAmbient.Value = Ambient("happy-path", "job-rec", "run-rec");
                var response = await recorder.InvokeAsync(Request(), TestContext.Current.CancellationToken);
                response.StopReasonRaw.ShouldBe("tool_use");
            }
            finally
            {
                AgentAmbient.Value = previous;
            }

            var files = Directory.GetFiles(dir, "*.json");
            files.Length.ShouldBe(1);
            var recorded = JsonNode.Parse(await File.ReadAllTextAsync(files[0], TestContext.Current.CancellationToken))!;
            recorded["modelId"]!.GetValue<string>().ShouldBe("deepseek-flash");
            recorded["request"]!["messages"]!.AsArray().Count.ShouldBe(1);
            recorded["response"]!["blocks"]!.AsArray()[0]!["type"]!.GetValue<string>().ShouldBe("tool_use");
        }
        finally
        {
            Environment.SetEnvironmentVariable("BURBUJA_LLM_RECORD_DIR", null);
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    private static ScriptedLlmProvider NewProvider()
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ScenarioDir));
        Directory.Exists(dir).ShouldBeTrue($"scenario directory {dir} is missing");
        Environment.SetEnvironmentVariable("BURBUJA_SCRIPTED_SCENARIO_DIR", dir);
        var clock = new TimeProviderClock(TimeProvider.System);
        return new ScriptedLlmProvider(new InMemoryCache(clock), TimeProvider.System);
    }

    private static AgentAmbient.State Ambient(string scenario, string jobId, string runId) =>
        new(
            JobKey: "job-" + jobId,
            JobId: jobId,
            RunId: runId,
            Scenario: scenario,
            Role: AgentRunRole.Worker,
            Brief: "do the work",
            Language: "es",
            Tags: new Dictionary<string, string>());

    private static LlmRequest Request(int assistantTurns = 0)
    {
        var messages = new List<InputMessage> { new("user", [new TextBlock("do the work")]) };
        for (var i = 0; i < assistantTurns; i++)
            messages.Add(new InputMessage("assistant", [new TextBlock("working")]));
        return new LlmRequest("deepseek-flash", messages, null, null, null, 4096, null);
    }

    private static string Text(LlmResponse response) =>
        response.Blocks.OfType<TextBlock>().Single().Text;

    private static string? ToolPath(LlmResponse response) =>
        response.Blocks.OfType<ToolUseBlock>().Single().Input["path"]?.GetValue<string>();
}
