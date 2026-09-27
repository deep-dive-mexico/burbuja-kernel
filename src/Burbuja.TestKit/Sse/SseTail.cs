using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Burbuja.TestKit;

/// <summary>One SSE frame from a resource's event stream.</summary>
public sealed record SseEvent(long Seq, string Type, JsonNode? Data);

/// <summary>
/// Reads a resource's SSE stream from the Host. Used by the resume assertions, which need to
/// re-attach with a captured <c>Last-Event-ID</c> and prove there is no duplicate and no gap.
/// </summary>
public static class SseTail
{
    public static async IAsyncEnumerable<SseEvent> ReadAsync(
        ArmTestClient client,
        string path,
        long lastEventId = 0,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ArmTestClient.Versioned(path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (lastEventId > 0)
            request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId.ToString(CultureInfo.InvariantCulture));

        using var response = await client.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        long id = 0;
        var type = "message";
        var data = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                yield break;

            if (line.Length == 0)
            {
                if (data.Length > 0)
                    yield return new SseEvent(id, type, Parse(data.ToString()));
                id = 0;
                type = "message";
                data.Clear();
                continue;
            }

            if (line.StartsWith("id:", StringComparison.Ordinal))
            {
                if (long.TryParse(line[3..].Trim(), CultureInfo.InvariantCulture, out var parsed))
                    id = parsed;
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                type = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Append(line[5..].Trim());
            }
        }
    }

    /// <summary>
    /// Reads until <paramref name="stop"/> matches (inclusive) or the budget expires. Always
    /// returns what it has; a caller asserting on a missing event gets a readable list instead of
    /// a hang.
    /// </summary>
    public static async Task<List<SseEvent>> CollectAsync(
        ArmTestClient client,
        string path,
        Func<SseEvent, bool> stop,
        TimeSpan budget,
        long lastEventId = 0,
        CancellationToken ct = default)
    {
        var events = new List<SseEvent>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(budget);
        try
        {
            await foreach (var ev in ReadAsync(client, path, lastEventId, cts.Token))
            {
                events.Add(ev);
                if (stop(ev))
                    break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }

        return events;
    }

    private static JsonNode? Parse(string data)
    {
        try
        {
            return JsonNode.Parse(data);
        }
        catch (System.Text.Json.JsonException)
        {
            return JsonValue.Create(data);
        }
    }
}
