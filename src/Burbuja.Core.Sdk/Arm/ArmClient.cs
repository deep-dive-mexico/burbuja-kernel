using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Json;

namespace Burbuja.Core.Arm;

public sealed record ArmSseEvent(string? Id, string? Name, string Data);

public sealed record ArmClientRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string>? Query = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    JsonNode? Body = null);

public sealed record ArmClientResponse(
    int StatusCode,
    IReadOnlyDictionary<string, string> Headers,
    JsonNode? Body,
    ArmError? Error);

public interface IArmClient
{
    ValueTask<ArmClientResponse> SendAsync(ArmClientRequest request, CancellationToken ct);
    IAsyncEnumerable<ArmSseEvent> StreamAsync(string path, string? lastEventId, CancellationToken ct);
}

public sealed class HttpArmClient(HttpClient http, string apiVersion, string? apiKey = null) : IArmClient
{
    public async ValueTask<ArmClientResponse> SendAsync(ArmClientRequest request, CancellationToken ct)
    {
        using var message = Build(request, lastEventId: null, acceptSse: false);
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct);
        return await ReadResponseAsync(response, ct);
    }

    public async IAsyncEnumerable<ArmSseEvent> StreamAsync(string path, string? lastEventId, [EnumeratorCancellation] CancellationToken ct)
    {
        using var message = Build(new ArmClientRequest("GET", path), lastEventId, acceptSse: true);
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
        string? id = null, name = null;
        var data = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                yield break;
            if (line.Length == 0)
            {
                if (data.Length > 0 || id is not null || name is not null)
                    yield return new ArmSseEvent(id, name, data.ToString());
                id = null;
                name = null;
                data.Clear();
                continue;
            }

            if (line.StartsWith(':'))
                continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' '))
                value = value[1..];
            switch (field)
            {
                case "id":
                    id = value;
                    break;
                case "event":
                    name = value;
                    break;
                case "data":
                    if (data.Length > 0)
                        data.Append('\n');
                    data.Append(value);
                    break;
            }
        }
    }

    private HttpRequestMessage Build(ArmClientRequest request, string? lastEventId, bool acceptSse)
    {
        var uri = BuildUri(request.Path, request.Query);
        var message = new HttpRequestMessage(new HttpMethod(request.Method), uri);
        if (acceptSse)
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        else
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(apiKey))
            message.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        if (!string.IsNullOrEmpty(lastEventId))
            message.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
        if (request.Headers is not null)
        {
            foreach (var (k, v) in request.Headers)
                message.Headers.TryAddWithoutValidation(k, v);
        }

        if (request.Body is not null)
        {
            message.Content = new StringContent(request.Body.ToJsonString(JsonWire.Options), Encoding.UTF8, "application/json");
        }

        return message;
    }

    private Uri BuildUri(string path, IReadOnlyDictionary<string, string>? query)
    {
        var pairs = new List<string>();
        var hasApiVersion = false;
        if (query is not null)
        {
            foreach (var (k, v) in query)
            {
                if (k.Equals("api-version", StringComparison.OrdinalIgnoreCase))
                    hasApiVersion = true;
                pairs.Add($"{Uri.EscapeDataString(k)}={Uri.EscapeDataString(v)}");
            }
        }

        if (!hasApiVersion)
            pairs.Add($"api-version={Uri.EscapeDataString(apiVersion)}");

        var q = string.Join('&', pairs);
        var rel = path.StartsWith('/') ? path : "/" + path;
        if (rel.Contains('?', StringComparison.Ordinal))
            return new Uri(rel + (q.Length == 0 ? "" : "&" + q), UriKind.RelativeOrAbsolute);
        return new Uri(rel + (q.Length == 0 ? "" : "?" + q), UriKind.RelativeOrAbsolute);
    }

    private static async Task<ArmClientResponse> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in response.Headers)
            headers[h.Key] = string.Join(',', h.Value);
        foreach (var h in response.Content.Headers)
            headers[h.Key] = string.Join(',', h.Value);

        var text = await response.Content.ReadAsStringAsync(ct);
        JsonNode? body = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                body = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                body = JsonValue.Create(text);
            }
        }

        ArmError? error = null;
        if ((int)response.StatusCode >= 400)
            error = ReadError(body, (int)response.StatusCode);

        return new ArmClientResponse((int)response.StatusCode, headers, body, error);
    }

    private static ArmError ReadError(JsonNode? body, int status)
    {
        var err = body?["error"];
        if (err is JsonObject obj)
        {
            var code = obj["code"]?.GetValue<string>() ?? "Unknown";
            var message = obj["message"]?.GetValue<string>() ?? "Request failed.";
            var target = obj["target"]?.GetValue<string>();
            return new ArmError(code, message, target, [], obj["innererror"] as JsonObject, status);
        }

        return new ArmError("Unknown", body?.ToJsonString() ?? "Request failed.", null, [], null, status);
    }
}
