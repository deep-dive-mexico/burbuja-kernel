using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Core.Abstractions.Json;

namespace Burbuja.Plugins.Llm.Scripted;

/// <summary>
/// Decorates any <see cref="ILlmProvider"/> and writes every request/response pair to disk as
/// JSON, so a real-model run (DeepSeek, M4.6+) can be turned into a scripted scenario instead of
/// being hand-written. Off unless <c>BURBUJA_LLM_RECORD=1</c>; the directory defaults to
/// <c>artifacts/llm-recordings</c> and can be overridden with <c>BURBUJA_LLM_RECORD_DIR</c>.
/// </summary>
public sealed class LlmRecorder(ILlmProvider inner) : ILlmProvider
{
    private static int _sequence;

    public static bool Enabled => EnabledFor(Environment.GetEnvironmentVariable("BURBUJA_LLM_RECORD"));

    /// <summary>The flag rule, split out so it can be asserted without mutating the process env.</summary>
    public static bool EnabledFor(string? value) => value?.Trim() == "1";

    public string Id => inner.Id;

    public ModelCapabilities Capabilities(string modelId) => inner.Capabilities(modelId);

    public static string Directory =>
        Environment.GetEnvironmentVariable("BURBUJA_LLM_RECORD_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine("artifacts", "llm-recordings");

    public async ValueTask<LlmResponse> InvokeAsync(LlmRequest request, CancellationToken ct)
    {
        var response = await inner.InvokeAsync(request, ct);
        await RecordAsync(request, response, ct);
        return response;
    }

    private static async ValueTask RecordAsync(LlmRequest request, LlmResponse response, CancellationToken ct)
    {
        var dir = Directory;
        System.IO.Directory.CreateDirectory(dir);
        var seq = Interlocked.Increment(ref _sequence);
        var name = $"{DateTimeOffset.UtcNow:yyyyMMdd'T'HHmmss'fff'}-{seq:D4}-{Sanitize(request.ModelId)}.json";
        var payload = new JsonObject
        {
            ["recordedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["provider"] = "recorder",
            ["modelId"] = request.ModelId,
            ["request"] = JsonSerializer.SerializeToNode(request, JsonWire.Options),
            ["response"] = JsonSerializer.SerializeToNode(response, JsonWire.Options),
        };
        await File.WriteAllTextAsync(
            Path.Combine(dir, name),
            payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            ct);
    }

    private static string Sanitize(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray();
        return new string(chars);
    }
}
