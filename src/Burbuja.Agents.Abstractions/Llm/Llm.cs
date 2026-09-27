using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Burbuja.Agents.Abstractions.Llm;

[JsonConverter(typeof(ContentBlockConverterFactory))]
public abstract record ContentBlock;

[JsonConverter(typeof(ContentBlockConverterFactory))]
public sealed record TextBlock(string Text) : ContentBlock;

[JsonConverter(typeof(ContentBlockConverterFactory))]
public sealed record ToolUseBlock(string Id, string Name, JsonNode Input) : ContentBlock;

[JsonConverter(typeof(ContentBlockConverterFactory))]
public sealed record ToolResultBlock(string ToolUseId, JsonNode Content, bool IsError) : ContentBlock;

[JsonConverter(typeof(ContentBlockConverterFactory))]
public sealed record ThinkingBlock(string Thinking, string? Signature = null) : ContentBlock;

/// <summary>An Anthropic-shaped image source: <c>base64</c> (with data) or <c>url</c>.</summary>
public sealed record ImageSource(string Kind, string MediaType, string? Data = null, string? Url = null);

[JsonConverter(typeof(ContentBlockConverterFactory))]
public sealed record ImageBlock(ImageSource Source) : ContentBlock;

[JsonConverter(typeof(ContentBlockConverterFactory))]
public sealed record UnknownBlock(string Type, JsonElement Raw) : ContentBlock;

public sealed class ContentBlockConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeof(ContentBlock).IsAssignableFrom(typeToConvert);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var conv = typeof(ContentBlockTypedConverter<>).MakeGenericType(typeToConvert);
        return (JsonConverter)Activator.CreateInstance(conv)!;
    }

    private sealed class ContentBlockTypedConverter<T> : JsonConverter<T> where T : ContentBlock
    {
        private static readonly ContentBlockConverter Inner = new();

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            (T)Inner.Read(ref reader, typeof(ContentBlock), options);

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            Inner.Write(writer, value, options);
    }
}

public sealed class ContentBlockConverter : JsonConverter<ContentBlock>
{
    public override ContentBlock Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Content block must be an object.");
        if (!root.TryGetProperty("type", out var typeProp))
            throw new JsonException("Content block missing type.");
        var type = typeProp.GetString() ?? "";
        return type switch
        {
            "text" => new TextBlock(root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : ""),
            "tool_use" => new ToolUseBlock(
                root.GetProperty("id").GetString() ?? "",
                root.GetProperty("name").GetString() ?? "",
                root.TryGetProperty("input", out var input)
                    ? JsonNode.Parse(input.GetRawText()) ?? new JsonObject()
                    : new JsonObject()),
            "tool_result" => new ToolResultBlock(
                root.GetProperty("tool_use_id").GetString() ?? "",
                root.TryGetProperty("content", out var content)
                    ? JsonNode.Parse(content.GetRawText()) ?? JsonValue.Create("")!
                    : JsonValue.Create("")!,
                root.TryGetProperty("is_error", out var err) && err.GetBoolean()),
            "thinking" => new ThinkingBlock(
                root.TryGetProperty("thinking", out var th) ? th.GetString() ?? "" : "",
                root.TryGetProperty("signature", out var sig) ? sig.GetString() : null),
            "image" => new ImageBlock(ReadImage(root)),
            _ => new UnknownBlock(type, root.Clone()),
        };
    }

    private static ImageSource ReadImage(JsonElement root)
    {
        if (!root.TryGetProperty("source", out var source))
            return new ImageSource("base64", "application/octet-stream");
        var kind = source.TryGetProperty("type", out var t) ? t.GetString() ?? "base64" : "base64";
        var mediaType = source.TryGetProperty("media_type", out var m) ? m.GetString() ?? "" : "";
        var data = source.TryGetProperty("data", out var d) ? d.GetString() : null;
        var url = source.TryGetProperty("url", out var u) ? u.GetString() : null;
        return new ImageSource(kind, mediaType, data, url);
    }

    public override void Write(Utf8JsonWriter writer, ContentBlock value, JsonSerializerOptions options)
    {
        if (value is UnknownBlock unknown)
        {
            unknown.Raw.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        switch (value)
        {
            case TextBlock text:
                writer.WriteString("type", "text");
                writer.WriteString("text", text.Text);
                break;
            case ToolUseBlock use:
                writer.WriteString("type", "tool_use");
                writer.WriteString("id", use.Id);
                writer.WriteString("name", use.Name);
                writer.WritePropertyName("input");
                use.Input.WriteTo(writer);
                break;
            case ToolResultBlock result:
                writer.WriteString("type", "tool_result");
                writer.WriteString("tool_use_id", result.ToolUseId);
                writer.WritePropertyName("content");
                result.Content.WriteTo(writer);
                writer.WriteBoolean("is_error", result.IsError);
                break;
            case ThinkingBlock thinking:
                writer.WriteString("type", "thinking");
                writer.WriteString("thinking", thinking.Thinking);
                if (thinking.Signature is not null)
                    writer.WriteString("signature", thinking.Signature);
                break;
            case ImageBlock image:
                writer.WriteString("type", "image");
                writer.WritePropertyName("source");
                writer.WriteStartObject();
                writer.WriteString("type", image.Source.Kind);
                if (image.Source.MediaType.Length > 0)
                    writer.WriteString("media_type", image.Source.MediaType);
                if (image.Source.Data is not null)
                    writer.WriteString("data", image.Source.Data);
                if (image.Source.Url is not null)
                    writer.WriteString("url", image.Source.Url);
                writer.WriteEndObject();
                break;
            default:
                throw new JsonException($"Unknown content block {value.GetType().Name}.");
        }

        writer.WriteEndObject();
    }
}

public sealed record InputMessage(string Role, IReadOnlyList<ContentBlock> Content);

public sealed record Usage(
    int InputTokens,
    int OutputTokens,
    int CacheReadInputTokens,
    int CacheWriteInputTokens);

public enum ThinkingKind
{
    Enabled,
    Disabled,
}

public sealed record ThinkingConfig(ThinkingKind Type, int? BudgetTokens);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(ToolChoiceAuto), "auto")]
[JsonDerivedType(typeof(ToolChoiceTool), "tool")]
[JsonDerivedType(typeof(ToolChoiceNone), "none")]
[JsonDerivedType(typeof(ToolChoiceAny), "any")]
public abstract record ToolChoice;

public sealed record ToolChoiceAuto : ToolChoice;

public sealed record ToolChoiceTool(string Name) : ToolChoice;

/// <summary>The model must not call a tool.</summary>
public sealed record ToolChoiceNone : ToolChoice;

/// <summary>The model must call some tool, but may choose which.</summary>
public sealed record ToolChoiceAny : ToolChoice;

public sealed record LlmRequest(
    string ModelId,
    IReadOnlyList<InputMessage> Messages,
    string? System,
    IReadOnlyList<ToolSpec>? Tools,
    ToolChoice? ToolChoice,
    int MaxTokens,
    ThinkingConfig? Thinking,
    string? Effort = null);

public sealed record ToolSpec(string Name, string Description, JsonNode InputSchema);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LlmStopReason
{
    EndTurn,
    ToolUse,
    MaxTokens,
    StopSequence,
    Unknown,
}

public sealed record LlmResponse(
    LlmStopReason StopReason,
    IReadOnlyList<ContentBlock> Blocks,
    Usage Usage,
    string? StopReasonRaw = null);

public interface ILlmProvider
{
    string Id { get; }
    ModelCapabilities Capabilities(string modelId);
    ValueTask<LlmResponse> InvokeAsync(LlmRequest request, CancellationToken ct);
}

/// <summary>
/// One forced-tool call: the caller names the tool, the provider returns its input and nothing else.
///
/// <para><b>M14 R5c (request row K2).</b> <see cref="Effort"/> is carried (the same value
/// <see cref="LlmRequest.Effort"/> already carries) because the AKOS turn record writes an
/// <c>akos.turn.llm.request</c> event for every pipeline call and was writing <c>effort: null</c> for a fact
/// the caller HAD — the provider resolved an effort and the record could not say which. It is optional and
/// last, so every existing positional caller still compiles; a provider that does not support effort ignores
/// it, and the record then shows what was asked rather than what a later reader has to guess.</para>
/// </summary>
public sealed record StructuredLlmRequest(
    string ModelId,
    IReadOnlyList<InputMessage> Messages,
    string? System,
    ToolSpec Tool,
    ToolChoice ToolChoice,
    int MaxTokens,
    string? Effort = null);

public interface IStructuredLlm
{
    ValueTask<JsonNode?> CallAsync(StructuredLlmRequest request, CancellationToken ct);
}

/// <summary>
/// What a structured call cost, in the shape the provider's own API reports it.
///
/// <para><b>Why a separate record from <see cref="Usage"/>.</b> This travels out of a PLUGIN and into the
/// caller's record; <see cref="Usage"/> is the agent-runtime's own accounting type and a provider that only
/// implements <see cref="IStructuredLlm"/> should not have to reference the runtime to answer "how many
/// tokens". The four numbers mean exactly what they mean there: input, output, cache reads, cache writes.</para>
/// </summary>
public sealed record StructuredLlmUsage(
    int InputTokens,
    int OutputTokens,
    int CacheReadInputTokens = 0,
    int CacheWriteInputTokens = 0);

/// <summary>
/// A structured call that can also say what it cost.
///
/// <para><b>M14 R5c (request row K2) — additive on purpose.</b> The AKOS pipeline logs an
/// <c>akos.turn.llm.request</c> event per call and was writing <c>usage: null</c>, so the Técnico depth could
/// not show what a stage cost from stored data. Widening <see cref="IStructuredLlm.CallAsync"/>'s return type
/// would be a breaking change for every provider in the tree — one of them is another lane's file — so the
/// usage travels on a SECOND interface and a caller asks for it when it wants it:
/// <c>provider is IStructuredLlmWithUsage withUsage ? … : provider.CallAsync(…)</c>. A provider that does not
/// implement this keeps working unchanged, and the record keeps writing null for it, which is the honest
/// answer for a provider that cannot say.</para>
/// </summary>
public interface IStructuredLlmWithUsage : IStructuredLlm
{
    ValueTask<(JsonNode? Content, StructuredLlmUsage? Usage)> CallWithUsageAsync(
        StructuredLlmRequest request,
        CancellationToken ct);
}

public sealed record ModelCapabilities(
    bool SupportsEffort,
    string Thinking,
    bool StructuredOutputs,
    bool ForcedToolChoice,
    int MinCacheablePrefix,
    int ContextWindow,
    int MaxOutputTokens = 0,
    bool SupportsImages = false,
    bool RequiresThinkingRoundTrip = false,
    bool SupportsCacheControl = false);

public sealed class LlmThrottledException : Exception
{
    public LlmThrottledException(string? retryAfter = null)
        : base("The LLM provider throttled the request.")
    {
        RetryAfter = retryAfter;
    }

    public string? RetryAfter { get; }
}

/// <summary>
/// The provider account is out of balance (DeepSeek 402). Deliberately NOT a subclass of
/// <see cref="LlmThrottledException"/>: retrying cannot help, so callers must not retry it.
/// </summary>
public sealed class LlmOutOfBalanceException(string? detail = null)
    : Exception("The LLM provider account is out of balance." + (detail is null ? "" : " " + detail));

/// <summary>The provider reported a transient internal condition (e.g. insufficient_system_resource).</summary>
public sealed class LlmUnavailableException(string detail, string? raw = null)
    : Exception($"The LLM provider is temporarily unavailable: {detail}")
{
    public string? Raw { get; } = raw;
}
