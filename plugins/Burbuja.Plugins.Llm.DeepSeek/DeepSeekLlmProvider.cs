using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Burbuja.Agents.Abstractions.Budget;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Core.Abstractions.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Burbuja.Plugins.Llm.DeepSeek;

public sealed class DeepSeekOptions
{
    public const string Section = "DeepSeek";

    public string BaseUrl { get; set; } = "https://api.deepseek.com/anthropic";

    public string ModelId { get; set; } = "deepseek-flash";

    /// <summary>Command that prints the API key on stdout (never a literal key in config).</summary>
    public string ApiKeyCommand { get; set; } = "~/.local/bin/deepseek-key";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(20);

    public string? Effort { get; set; }

    public int MaxConcurrency { get; set; } = 4;

    /// <summary>
    /// The secret names this resolver OWNS and will answer for.
    ///
    /// <para>A name-based seam whose implementation ignores the name is worse than no seam: `ISecretResolver`
    /// is asked for a name by whoever needs a credential, and this resolver runs one command that prints the
    /// DeepSeek key. Before this list existed it answered for **every** name, so the first other component to
    /// ask — M8/I1's mailbox, asking for `graph-mail` — would have been handed the DeepSeek API key and sent
    /// it as a bearer token to Microsoft. Nothing leaked, because the mail caller landed in the same change
    /// and the leak was found by asking what the resolver does with a name it does not know.</para>
    /// </summary>
    public IReadOnlyList<string> SecretNames { get; set; } = ["deepseek"];

    public Uri MessagesUri => new(BaseUrl.TrimEnd('/') + "/v1/messages");
}

/// <summary>
/// DeepSeek over the Anthropic-compatible endpoint. The wire model is already Anthropic-shaped, so
/// this is a thin, strict mapping: no <c>cache_control</c>, no <c>top_k</c>, no <c>temperature</c>,
/// and thinking blocks are echoed back with their signature.
/// </summary>
public sealed class DeepSeekLlmProvider : ILlmProvider, IStructuredLlm, IDisposable
{
    public const string FlashModelId = "deepseek-flash";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly DeepSeekOptions _options;
    private readonly ISecretResolver? _secrets;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate;
    private bool _disposed;

    private readonly IConfiguration? _configuration;

    public DeepSeekLlmProvider(
        IOptions<DeepSeekOptions> options,
        ISecretResolver? secrets = null,
        HttpMessageHandler? handler = null,
        /// <summary>
        /// The ROOT configuration, so the provider can read the money guard itself. Optional so a fact that
        /// constructs a provider directly keeps compiling; null means "no guard visible", which is treated as
        /// UNCONFIGURED — a provider that cannot see the ceiling must not assume there is none.
        /// </summary>
        IConfiguration? configuration = null)
    {
        _configuration = configuration;
        _options = options.Value;
        _secrets = secrets;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = _options.Timeout;
        _gate = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrency));
    }

    public string Id => "llm.deepseek";

    public ModelCapabilities Capabilities(string modelId) => modelId switch
    {
        FlashModelId => new ModelCapabilities(
            SupportsEffort: true,
            Thinking: "enabled",
            StructuredOutputs: false,
            ForcedToolChoice: true,
            MinCacheablePrefix: 0,
            ContextWindow: 1_000_000,
            MaxOutputTokens: 384_000,
            SupportsImages: true,
            RequiresThinkingRoundTrip: true,
            SupportsCacheControl: false),
        _ => throw new ArgumentOutOfRangeException(nameof(modelId), $"Unknown DeepSeek model '{modelId}'."),
    };

    /// <summary>
    /// M2 — <b>no stop, no paid provider.</b> A model that costs money is not served by a host that has not
    /// said what its ceiling is.
    ///
    /// <para><b>Why the provider and not the three guards.</b> Job runs, stateful agents and the AKOS pipeline
    /// each check the day's stop before calling the model, and each of those checks is a place the next spender
    /// can forget — R7's finding was that the job path's check sat <em>after</em> the run was persisted, and
    /// that the pipeline's own interpret/judge calls booked nothing at all. The one place every paid call MUST
    /// pass through is the provider, so the refusal lives here as well as there: the guards decide whether the
    /// day is spent, and this decides whether the host was ever allowed to spend.</para>
    ///
    /// <para><b>Two statements, two answers.</b> <c>Budget:DailyHardStopUsd</c> ABSENT is a host that has not
    /// decided — it must not serve (this is the plan's whole point, and the sentence names the setting).
    /// <c>0</c> is a host that decided to spend nothing — nothing is served, which is the neuter a fact can set
    /// to prove the rule is load-bearing. A positive value serves, because the day's ceiling is the three
    /// guards' business and refusing here on a spent day would double the refusal into a fault.</para>
    /// </summary>
    private void RequireGuard()
    {
        var guard = _configuration is null
            ? new BudgetGuard(BudgetGuardKind.Unset, null)
            : DailyHardStop.Guard(_configuration);

        // Only a POSITIVE ceiling serves. `Unset` is a host that has not decided, `Zero` a host that decided to
        // spend nothing — both are refusals, and they are different sentences so an operator is not left
        // guessing which one they wrote.
        if (guard.Kind is BudgetGuardKind.Limited)
            return;

        throw new HardStopUnconfigured(guard.Sentence);
    }

    /// <summary>Thrown when a paid provider is asked to serve a host that configured no ceiling.</summary>
    public sealed class HardStopUnconfigured(string message) : InvalidOperationException(message);

    public async ValueTask<LlmResponse> InvokeAsync(LlmRequest request, CancellationToken ct)
    {
        RequireGuard();
        var capabilities = Capabilities(request.ModelId);
        var body = BuildRequest(request, capabilities);
        var payload = await PostAsync(body, ct).ConfigureAwait(false);
        return Parse(payload, request.ModelId);
    }

    public async ValueTask<JsonNode?> CallAsync(StructuredLlmRequest request, CancellationToken ct) =>
        (await CallWithUsageAsync(request, ct).ConfigureAwait(false)).Content;

    /// <summary>
    /// M14 R5c (request row K2) — the same forced-tool call, and what it cost.
    ///
    /// <para>The provider already parsed `usage` for the agent runtime; the pipeline's record could not see it
    /// because <see cref="IStructuredLlm"/> returns only the tool input. The effort the caller asked for is
    /// forwarded as well (<see cref="StructuredLlmRequest.Effort"/> → <see cref="LlmRequest.Effort"/>), so the
    /// record's <c>akos.turn.llm.request</c> can say which effort produced this answer instead of null.</para>
    /// </summary>
    public async ValueTask<(JsonNode? Content, StructuredLlmUsage? Usage)> CallWithUsageAsync(
        StructuredLlmRequest request,
        CancellationToken ct)
    {
        var response = await InvokeAsync(new LlmRequest(
            request.ModelId,
            request.Messages,
            request.System,
            [request.Tool],
            request.ToolChoice,
            request.MaxTokens,
            null,
            request.Effort), ct).ConfigureAwait(false);
        return (
            response.Blocks.OfType<ToolUseBlock>().FirstOrDefault()?.Input,
            new StructuredLlmUsage(
                response.Usage.InputTokens,
                response.Usage.OutputTokens,
                response.Usage.CacheReadInputTokens,
                response.Usage.CacheWriteInputTokens));
    }

    internal JsonObject BuildRequest(LlmRequest request, ModelCapabilities capabilities)
    {
        var messages = new JsonArray();
        foreach (var message in request.Messages)
        {
            var content = new JsonArray();
            foreach (var block in message.Content)
                content.Add(JsonSerializer.SerializeToNode(block, typeof(ContentBlock), Json));
            messages.Add(new JsonObject { ["role"] = message.Role, ["content"] = content });
        }

        var body = new JsonObject
        {
            ["model"] = request.ModelId,
            ["max_tokens"] = request.MaxTokens,
            ["messages"] = messages,
        };
        if (!string.IsNullOrEmpty(request.System))
            body["system"] = request.System;
        if (request.Tools is { Count: > 0 })
        {
            body["tools"] = new JsonArray(request.Tools.Select(t => (JsonNode?)new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = t.InputSchema.DeepClone(),
            }).ToArray());
        }

        if (request.ToolChoice is not null)
            body["tool_choice"] = ToolChoiceJson(request.ToolChoice);
        if (request.Thinking is { Type: ThinkingKind.Enabled })
        {
            body["thinking"] = new JsonObject { ["type"] = "enabled" }; // DeepSeek ignores budget_tokens
        }
        else if (request.ToolChoice is ToolChoiceTool or ToolChoiceAny)
        {
            // M10 A11 — `thinking` must be DISABLED EXPLICITLY when the caller forces a tool, and saying nothing
            // is not the same as saying no.
            //
            // Measured against the live provider 2026-09-14: a request with `tool_choice: {type: tool}` and no
            // `thinking` field answers `400 invalid_request_error — "Thinking mode does not support this
            // tool_choice"`, and the SAME request with `thinking: {type: disabled}` answers 200 with the tool
            // call. This model thinks by default, so every STRUCTURED call the kernel makes — the AKOS
            // pipeline's `akos_interpret` and `akos_judge`, the supervisor's `decide` — failed on a real host
            // while the tool-free turns worked, which is why the failure looked like "AKOS is broken" rather
            // than "one field is missing". The scripted fixture ignores both fields, so no lane could see it.
            body["thinking"] = new JsonObject { ["type"] = "disabled" };
        }
        var effort = request.Effort ?? _options.Effort;
        if (!string.IsNullOrEmpty(effort) && capabilities.SupportsEffort)
            body["effort"] = effort;
        return body;
    }

    internal static JsonObject ToolChoiceJson(ToolChoice choice) => choice switch
    {
        ToolChoiceTool tool => new JsonObject { ["type"] = "tool", ["name"] = tool.Name },
        ToolChoiceNone => new JsonObject { ["type"] = "none" },
        ToolChoiceAny => new JsonObject { ["type"] = "any" },
        _ => new JsonObject { ["type"] = "auto" },
    };

    internal static LlmResponse Parse(string payload, string modelId)
    {
        _ = modelId;
        var node = JsonNode.Parse(payload) ?? throw new LlmUnavailableException("response was not JSON", payload);
        var blocks = new List<ContentBlock>();
        if (node["content"] is JsonArray content)
        {
            foreach (var item in content)
            {
                if (item is null)
                    continue;
                var block = JsonSerializer.Deserialize<ContentBlock>(item.ToJsonString(), Json);
                if (block is not null)
                    blocks.Add(block);
            }
        }

        var usage = node["usage"];
        var stopRaw = node["stop_reason"]?.GetValue<string>();
        var stop = stopRaw switch
        {
            "end_turn" => LlmStopReason.EndTurn,
            "tool_use" => LlmStopReason.ToolUse,
            "max_tokens" => LlmStopReason.MaxTokens,
            "stop_sequence" => LlmStopReason.StopSequence,
            _ => LlmStopReason.Unknown,
        };
        return new LlmResponse(
            stop,
            blocks,
            new Usage(
                usage?["input_tokens"]?.GetValue<int>() ?? 0,
                usage?["output_tokens"]?.GetValue<int>() ?? 0,
                usage?["cache_read_input_tokens"]?.GetValue<int>() ?? 0,
                usage?["cache_creation_input_tokens"]?.GetValue<int>() ?? 0),
            stopRaw);
    }

    private async Task<string> PostAsync(JsonObject body, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var key = await ResolveKeyAsync(ct).ConfigureAwait(false);
            using var message = new HttpRequestMessage(HttpMethod.Post, _options.MessagesUri)
            {
                Content = new StringContent(body.ToJsonString(Json), Encoding.UTF8, "application/json"),
            };
            message.Headers.TryAddWithoutValidation("x-api-key", key);
            message.Headers.TryAddWithoutValidation("authorization", "Bearer " + key);
            message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

            using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw MapError(response.StatusCode, response.Headers.RetryAfter?.ToString(), payload);
            }
            return payload;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static Exception MapError(HttpStatusCode status, string? retryAfter, string payload)
    {
        var kind = ErrorKind(payload);
        if (status == HttpStatusCode.PaymentRequired)
            return new LlmOutOfBalanceException(payload);
        if (status == HttpStatusCode.TooManyRequests)
            return new LlmThrottledException(retryAfter);
        if (kind is "insufficient_system_resource" or "aborted")
            return new LlmUnavailableException(kind, payload);
        // 2026-09-26 (live review) — the provider's own sentence ("Thinking mode does not support this tool_choice",
        // "invalid image media type"…) travels in the exception message: a bare "http 400 invalid_request_error" in
        // the log cost a live debugging session. The payload is the provider's error JSON; it carries no key.
        var detail = ErrorMessage(payload);
        return new LlmUnavailableException($"http {(int)status} {kind}{(detail is null ? "" : " — " + detail)}".TrimEnd(), payload);
    }

    internal static string? ErrorMessage(string payload)
    {
        try
        {
            var message = JsonNode.Parse(payload)?["error"]?["message"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(message) ? null : (message.Length > 240 ? message[..240] + "…" : message);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string? ErrorKind(string payload)
    {
        try
        {
            var node = JsonNode.Parse(payload);
            return node?["error"]?["type"]?.GetValue<string>()
                   ?? node?["error"]?["code"]?.GetValue<string>()
                   ?? node?["type"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async ValueTask<string> ResolveKeyAsync(CancellationToken ct)
    {
        if (_secrets is null)
            return "";
        return await _secrets.ResolveAsync("deepseek", ct).ConfigureAwait(false) ?? "";
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _http.Dispose();
        _gate.Dispose();
    }
}
