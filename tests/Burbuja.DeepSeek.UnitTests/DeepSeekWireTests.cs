using System.Net;
using System.Text;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Core.Abstractions.Process;
using Microsoft.Extensions.Configuration;
using Burbuja.Plugins.Llm.DeepSeek;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Burbuja.DeepSeek.UnitTests;

/// <summary>Replays recorded responses and captures the exact outbound request.</summary>
public sealed class ReplayHandler(params (HttpStatusCode Status, string Body, string? RetryAfter)[] responses) : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body, string? RetryAfter)> _responses = new(responses);

    public List<string> Bodies { get; } = [];

    public List<Dictionary<string, string?>> Headers { get; } = [];

    public int Calls => Bodies.Count;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "x-api-key", "authorization", "anthropic-version" })
            headers[name] = request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
        Headers.Add(headers);

        var next = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.OK, Recorded.ToolUseResponse, null);
        var response = new HttpResponseMessage(next.Status)
        {
            Content = new StringContent(next.Body, Encoding.UTF8, "application/json"),
        };
        if (next.RetryAfter is not null)
            response.Headers.TryAddWithoutValidation("Retry-After", next.RetryAfter);
        return response;
    }
}

public sealed class FakeSecretResolver(string? value) : Burbuja.Core.Abstractions.Secrets.ISecretResolver
{
    public ValueTask<string?> ResolveAsync(string name, CancellationToken ct) => new(value);
}

public sealed class SpyProcessRunner(string stdout = "sk-test-key", int exitCode = 0) : IProcessRunner
{
    public List<ProcessStartRequest> Calls { get; } = [];

    public ValueTask<ProcessResult> RunAsync(ProcessStartRequest request, ExecutionGrant grant, CancellationToken ct)
    {
        Calls.Add(request);
        return new(new ProcessResult(exitCode, stdout, "", false));
    }
}

/// <summary>Recorded DeepSeek (Anthropic-compatible) fixtures, captured 2026-09-10.</summary>
public static class Recorded
{
    public const string ToolUseResponse = """
        {
          "id": "msg_deepseek_1",
          "type": "message",
          "role": "assistant",
          "model": "deepseek-flash",
          "content": [
            { "type": "thinking", "thinking": "I should read the file first.", "signature": "sig-abc" },
            { "type": "text", "text": "Reading the file." },
            { "type": "tool_use", "id": "toolu_1", "name": "read_file", "input": { "path": "a.txt" } }
          ],
          "stop_reason": "tool_use",
          "usage": { "input_tokens": 120, "output_tokens": 40, "cache_read_input_tokens": 10, "cache_creation_input_tokens": 5 }
        }
        """;

    public const string OutOfBalanceResponse = """
        { "type": "error", "error": { "type": "insufficient_balance", "message": "Insufficient Balance" } }
        """;

    public const string UnavailableResponse = """
        { "type": "error", "error": { "type": "insufficient_system_resource", "message": "busy" } }
        """;

    public const string ThrottledResponse = """
        { "type": "error", "error": { "type": "rate_limit_error", "message": "slow down" } }
        """;
}

public static class ProviderFactory
{
    /// <param name="dailyHardStopUsd">
    /// M2 — the money guard this provider reads. The default is a positive ceiling, because a PAID provider
    /// refuses to serve a host that configured none; pass <c>null</c> to exercise that refusal and <c>"0"</c>
    /// for the "spend nothing" neuter.
    /// </param>
    public static DeepSeekLlmProvider Create(
        HttpMessageHandler handler,
        string? apiKey = "sk-test-key",
        Action<DeepSeekOptions>? configure = null,
        string? dailyHardStopUsd = "4")
    {
        var options = new DeepSeekOptions();
        configure?.Invoke(options);
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Budget:DailyHardStopUsd"] = dailyHardStopUsd,
            })
            .Build();
        return new DeepSeekLlmProvider(
            Microsoft.Extensions.Options.Options.Create(options),
            new FakeSecretResolver(apiKey),
            handler,
            configuration);
    }

    public static LlmRequest Request(
        IReadOnlyList<InputMessage>? messages = null,
        IReadOnlyList<ToolSpec>? tools = null,
        ToolChoice? choice = null,
        ThinkingConfig? thinking = null,
        string? effort = null) =>
        new(
            "deepseek-flash",
            messages ?? [new InputMessage("user", [new TextBlock("hello")])],
            "you are helpful",
            tools,
            choice,
            1024,
            thinking,
            effort);
}

public sealed class DeepSeekWireTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Anthropic_request_shape_matches_the_recorded_fixture()
    {
        var handler = new ReplayHandler();
        using var provider = ProviderFactory.Create(handler);

        var response = await provider.InvokeAsync(ProviderFactory.Request(), Ct);

        handler.Calls.ShouldBe(1);
        var body = handler.Bodies[0];
        body.ShouldContain("\"model\":\"deepseek-flash\"");
        body.ShouldContain("\"max_tokens\":1024");
        body.ShouldContain("\"system\":\"you are helpful\"");
        body.ShouldNotContain("cache_control");
        body.ShouldNotContain("top_k");
        body.ShouldNotContain("temperature");

        var headers = handler.Headers[0];
        headers["x-api-key"].ShouldBe("sk-test-key");
        headers["authorization"].ShouldBe("Bearer sk-test-key");
        headers["anthropic-version"].ShouldBe("2023-06-01");
        body.ShouldNotContain("sk-test-key");

        response.StopReason.ShouldBe(LlmStopReason.ToolUse);
        response.Blocks.OfType<ThinkingBlock>().Single().Signature.ShouldBe("sig-abc");
        response.Blocks.OfType<ToolUseBlock>().Single().Name.ShouldBe("read_file");
        response.Usage.CacheReadInputTokens.ShouldBe(10);
    }

    [Fact]
    public async Task Thinking_blocks_are_echoed_back_in_full_on_a_tool_using_request()
    {
        var handler = new ReplayHandler();
        using var provider = ProviderFactory.Create(handler);
        var messages = new List<InputMessage>
        {
            new("user", [new TextBlock("read a.txt")]),
            new("assistant", [new ThinkingBlock("prior reasoning", "sig-xyz"), new ToolUseBlock("t1", "read_file", new System.Text.Json.Nodes.JsonObject { ["path"] = "a.txt" })]),
            new("user", [new ToolResultBlock("t1", System.Text.Json.Nodes.JsonValue.Create("contents")!, false)]),
        };
        var tools = new List<ToolSpec> { new("read_file", "Read a file", new System.Text.Json.Nodes.JsonObject { ["type"] = "object" }) };

        await provider.InvokeAsync(
            ProviderFactory.Request(messages, tools, new ToolChoiceTool("read_file"), new ThinkingConfig(ThinkingKind.Enabled, 4096)),
            Ct);

        var body = handler.Bodies[0];
        body.ShouldContain("\"type\":\"thinking\"");
        body.ShouldContain("\"thinking\":\"prior reasoning\"");
        body.ShouldContain("\"signature\":\"sig-xyz\"");
        body.ShouldContain("\"tool_choice\":{\"type\":\"tool\",\"name\":\"read_file\"}");
        body.ShouldContain("\"type\":\"enabled\"");
        // budget_tokens is ignored by DeepSeek, so it must not be sent.
        body.ShouldNotContain("budget_tokens");
    }

    [Fact]
    public async Task Tool_choice_none_and_any_map_to_the_anthropic_shape()
    {
        var none = new ReplayHandler();
        using (var provider = ProviderFactory.Create(none))
            await provider.InvokeAsync(ProviderFactory.Request(choice: new ToolChoiceNone()), Ct);
        none.Bodies[0].ShouldContain("\"tool_choice\":{\"type\":\"none\"}");

        var any = new ReplayHandler();
        using (var provider = ProviderFactory.Create(any))
            await provider.InvokeAsync(ProviderFactory.Request(choice: new ToolChoiceAny()), Ct);
        any.Bodies[0].ShouldContain("\"tool_choice\":{\"type\":\"any\"}");
    }

    [Fact]
    public void Capabilities_for_deepseek_flash_are_exact()
    {
        using var provider = ProviderFactory.Create(new ReplayHandler());
        var caps = provider.Capabilities("deepseek-flash");
        caps.ContextWindow.ShouldBe(1_000_000);
        caps.MaxOutputTokens.ShouldBe(384_000);
        caps.SupportsEffort.ShouldBeTrue();
        caps.SupportsImages.ShouldBeTrue();
        caps.RequiresThinkingRoundTrip.ShouldBeTrue();
        caps.SupportsCacheControl.ShouldBeFalse();
        caps.StructuredOutputs.ShouldBeFalse();
    }

    [Fact]
    public void Unknown_model_throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => ProviderFactory.Create(new ReplayHandler()).Capabilities("deepseek-nope"));

    [Fact]
    public async Task Out_of_balance_402_is_not_retried()
    {
        var handler = new ReplayHandler((System.Net.HttpStatusCode.PaymentRequired, Recorded.OutOfBalanceResponse, null));
        using var provider = ProviderFactory.Create(handler);

        var ex = await Should.ThrowAsync<LlmOutOfBalanceException>(async () => await provider.InvokeAsync(ProviderFactory.Request(), Ct));
        ex.ShouldNotBeAssignableTo<LlmThrottledException>();
        handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Throttled_429_carries_retry_after()
    {
        var handler = new ReplayHandler(((System.Net.HttpStatusCode)429, Recorded.ThrottledResponse, "7"));
        using var provider = ProviderFactory.Create(handler);

        var ex = await Should.ThrowAsync<LlmThrottledException>(async () => await provider.InvokeAsync(ProviderFactory.Request(), Ct));
        ex.RetryAfter.ShouldBe("7");
    }

    [Fact]
    public async Task Insufficient_system_resource_is_unavailable()
    {
        var handler = new ReplayHandler((System.Net.HttpStatusCode.BadRequest, Recorded.UnavailableResponse, null));
        using var provider = ProviderFactory.Create(handler);

        var ex = await Should.ThrowAsync<LlmUnavailableException>(async () => await provider.InvokeAsync(ProviderFactory.Request(), Ct));
        ex.Raw!.ShouldContain("insufficient_system_resource");
    }
}

/// <summary>
/// M14 R5c (request row K2) — the structured call can say what it cost, and it forwards the effort.
///
/// <para><b>Why this is a fact and not a getter.</b> The AKOS pipeline logs an <c>akos.turn.llm.request</c>
/// event per call, and K's row is that the record wrote <c>usage: null</c> and <c>effort: null</c> for facts
/// the provider had already parsed. The contract change is additive
/// (<see cref="IStructuredLlmWithUsage"/> beside <see cref="IStructuredLlm"/>, <c>Effort</c> last on the
/// request), so the half that can be proven HERE is the provider's: the usage comes back from the response the
/// API sent, and the effort reaches the wire.</para>
/// </summary>
public sealed class StructuredUsageTests
{
    [Fact]
    public async Task The_structured_call_returns_the_usage_the_api_reported()
    {
        var handler = new ReplayHandler((HttpStatusCode.OK, Recorded.ToolUseResponse, null));
        var provider = ProviderFactory.Create(handler);

        var (content, usage) = await provider.CallWithUsageAsync(
            new StructuredLlmRequest(
                "deepseek-flash",
                [new InputMessage("user", [new TextBlock("hello")])],
                "you are helpful",
                new ToolSpec("read_file", "read a file", System.Text.Json.Nodes.JsonNode.Parse("""{"type":"object"}""")!),
                new ToolChoiceTool("read_file"),
                512),
            CancellationToken.None);

        content!["path"]!.GetValue<string>().ShouldBe("a.txt", "the tool input is still the return value");
        usage.ShouldNotBeNull("the provider parsed usage for the agent runtime and must hand it to the pipeline too");
        usage!.InputTokens.ShouldBe(120);
        usage.OutputTokens.ShouldBe(40);
        usage.CacheReadInputTokens.ShouldBe(10);
        usage.CacheWriteInputTokens.ShouldBe(5);
    }

    [Fact]
    public async Task The_effort_the_caller_asked_for_reaches_the_wire()
    {
        var handler = new ReplayHandler((HttpStatusCode.OK, Recorded.ToolUseResponse, null));
        var provider = ProviderFactory.Create(handler);

        await provider.CallWithUsageAsync(
            new StructuredLlmRequest(
                "deepseek-flash",
                [new InputMessage("user", [new TextBlock("hello")])],
                "you are helpful",
                new ToolSpec("read_file", "read a file", System.Text.Json.Nodes.JsonNode.Parse("""{"type":"object"}""")!),
                new ToolChoiceTool("read_file"),
                512,
                Effort: "high"),
            CancellationToken.None);

        handler.Bodies.Count.ShouldBe(1);
        // The provider's own wire body is the only place the effort can be checked: the record can say what was
        // asked, and this is the fact that says the provider did something with it.
        handler.Bodies[0].ShouldContain("high", Case.Insensitive,
            $"the effort must reach the request body, not stop at the record; body: {handler.Bodies[0]}");
    }
}
