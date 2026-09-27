using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Json;

namespace Burbuja.Core.Arm;

/// <summary>
/// The typed façade over <see cref="IArmClient"/>: every call funnels through <c>SendAsync</c>, throws
/// <see cref="ArmException"/> on any status &gt;= 400, and never returns <c>default</c> to hide an error.
/// </summary>
public static class ArmClientExtensions
{
    /// <summary>GET a resource and deserialize its body; <c>default</c> only for an empty 2xx body.</summary>
    public static async ValueTask<T?> GetAsync<T>(
        this IArmClient client,
        string path,
        IReadOnlyDictionary<string, string>? query = null,
        CancellationToken ct = default)
    {
        var response = await Send(client, "GET", path, query, ct: ct);
        return response.Body<T>();
    }

    /// <summary>PUT a resource body and deserialize the stored resource.</summary>
    public static async ValueTask<T?> PutAsync<T>(
        this IArmClient client,
        string path,
        JsonNode? body,
        CancellationToken ct = default)
    {
        var response = await Send(client, "PUT", path, body: body, ct: ct);
        return response.Body<T>();
    }

    /// <summary>PATCH a resource body, optionally guarded by <paramref name="ifMatch"/>.</summary>
    public static async ValueTask<T?> PatchAsync<T>(
        this IArmClient client,
        string path,
        JsonNode? body,
        string? ifMatch = null,
        CancellationToken ct = default)
    {
        var response = await Send(client, "PATCH", path, headers: IfMatch(ifMatch), body: body, ct: ct);
        return response.Body<T>();
    }

    /// <summary>DELETE a resource, optionally guarded by <paramref name="ifMatch"/>.</summary>
    public static async ValueTask DeleteAsync(
        this IArmClient client,
        string path,
        string? ifMatch = null,
        CancellationToken ct = default)
    {
        var response = await Send(client, "DELETE", path, headers: IfMatch(ifMatch), ct: ct);
        response.EnsureSuccess();
    }

    /// <summary>POST to a resource action and deserialize the action result.</summary>
    public static async ValueTask<T?> PostActionAsync<T>(
        this IArmClient client,
        string path,
        JsonNode? body = null,
        CancellationToken ct = default)
    {
        var response = await Send(client, "POST", path, body: body, ct: ct);
        return response.Body<T>();
    }

    /// <summary>
    /// GET a collection page by page: yields every element of each <c>value</c> array and follows
    /// <c>nextLink</c> until it is absent. A <c>nextLink</c> that repeats a URL already fetched ends
    /// the enumeration instead of looping forever.
    /// </summary>
    public static async IAsyncEnumerable<T> ListAsync<T>(
        this IArmClient client,
        string path,
        IReadOnlyDictionary<string, string>? query = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string? next = path;
        IReadOnlyDictionary<string, string>? nextQuery = query;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (next is not null && visited.Add(next))
        {
            var response = await Send(client, "GET", next, nextQuery, ct: ct);
            var page = response.Body<JsonObject>();
            if (page?["value"] is JsonArray items)
            {
                foreach (var item in items)
                {
                    if (item is not null)
                        yield return item.Deserialize<T>(JsonWire.Options)!;
                }
            }

            // A nextLink carries its own query ($skipToken, $top, api-version), so it is authoritative.
            next = Text(page?["nextLink"]);
            nextQuery = null;
        }
    }

    /// <summary>
    /// Polls the long-running operation behind <paramref name="accepted"/> (the 202 response) until its
    /// body reaches a terminal state. The first poll is immediate; later polls wait
    /// <paramref name="pollInterval"/> within the overall <paramref name="timeout"/>. A terminal
    /// <c>Failed</c>/<c>Canceled</c> state throws <see cref="ArmException"/> built from the operation's error.
    /// </summary>
    public static async ValueTask<T?> PollOperationAsync<T>(
        this IArmClient client,
        ArmClientResponse accepted,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        accepted.EnsureSuccess();
        var location = Header(accepted, "Azure-AsyncOperation") ?? Header(accepted, "Location")
            ?? throw new ArmException(new ArmError(
                "MissingAsyncOperationHeader",
                "The accepted response carries neither an Azure-AsyncOperation nor a Location header.",
                null,
                [],
                null,
                accepted.StatusCode));

        var clock = Stopwatch.StartNew();
        while (true)
        {
            var response = await Send(client, "GET", location, ct: ct);
            response.EnsureSuccess();
            var status = Text(response.Body?["status"] ?? response.Body?["properties"]?["status"]);
            if (string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase))
                return response.Body<T>();
            if (status is not null
                && (status.Equals("Failed", StringComparison.OrdinalIgnoreCase) || status.Equals("Canceled", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArmException(OperationError(response.Body, status));
            }

            var remaining = timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw new ArmException(new ArmError(
                    "OperationTimedOut",
                    $"The operation did not reach a terminal state within {timeout}.",
                    null,
                    [],
                    null,
                    504));
            }

            await Task.Delay(remaining < pollInterval ? remaining : pollInterval, ct);
        }
    }

    /// <summary>Tail a resource's event stream; a thin alias for <see cref="IArmClient.StreamAsync"/>.</summary>
    public static IAsyncEnumerable<ArmSseEvent> TailAsync(
        this IArmClient client,
        string path,
        string? lastEventId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.StreamAsync(path, lastEventId, ct);
    }

    /// <summary>
    /// Reads the JSON event page an events route serves for <c>Accept: application/json</c>, following
    /// <c>nextFrom</c> (sent as the next <c>from</c>) until it is null, so the caller gets the whole range.
    ///
    /// <para><paramref name="search"/> sends the kernel's <c>q</c>: whitespace-separated terms, all of
    /// which must appear in an event's summary or payload, case-insensitively. With a search the stream
    /// is walked rather than paged — <c>nextFrom</c> becomes the point the SCAN resumes, so an unmatched
    /// event between two matches is never skipped, and the iterator can run for as long as the stream is
    /// long. A caller that must bound the work breaks out of the loop.</para>
    /// </summary>
    public static async IAsyncEnumerable<T> ReadEventsAsync<T>(
        this IArmClient client,
        string path,
        long from = 0,
        long? to = null,
        int limit = 500,
        string? search = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/json" };
        var cursor = from;
        var visited = new HashSet<long> { from };
        while (true)
        {
            var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["from"] = cursor.ToString(CultureInfo.InvariantCulture),
                ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
            };
            if (to is { } end)
                query["to"] = end.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(search))
                query["q"] = search;

            var response = await Send(client, "GET", path, query, headers, ct: ct);
            var page = response.Body<JsonObject>();
            if (page?["value"] is JsonArray items)
            {
                foreach (var item in items)
                {
                    if (item is not null)
                        yield return item.Deserialize<T>(JsonWire.Options)!;
                }
            }

            // nextFrom may only move forward: a repeated cursor would re-read the same page forever.
            if (ReadLong(page?["nextFrom"]) is not { } next || !visited.Add(next))
                yield break;
            cursor = next;
        }
    }

    /// <summary>Throws <see cref="ArmException"/> for any status &gt;= 400; returns the response for chaining.</summary>
    public static ArmClientResponse EnsureSuccess(this ArmClientResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.StatusCode >= 400)
        {
            throw new ArmException(response.Error ?? new ArmError(
                "Unknown",
                $"The request failed with status {response.StatusCode}.",
                null,
                [],
                null,
                response.StatusCode));
        }

        return response;
    }

    /// <summary>Deserializes the body of a successful response; <c>default</c> for a genuinely empty body.</summary>
    public static T? Body<T>(this ArmClientResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.EnsureSuccess();
        if (response.Body is null)
            return default;
        return response.Body is T typed ? typed : response.Body.Deserialize<T>(JsonWire.Options);
    }

    private static ValueTask<ArmClientResponse> Send(
        IArmClient client,
        string method,
        string url,
        IReadOnlyDictionary<string, string>? query = null,
        IReadOnlyDictionary<string, string>? headers = null,
        JsonNode? body = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.SendAsync(Request(method, url, query, headers, body), ct);
    }

    /// <summary>
    /// Splits a URL's embedded query into <see cref="ArmClientRequest.Query"/>. <c>HttpArmClient.BuildUri</c>
    /// appends <c>api-version</c> unless the query dictionary already carries one, so an ARM nextLink or
    /// Azure-AsyncOperation that embeds its own <c>api-version</c> must travel as query pairs — leaving it
    /// in the path would send the parameter twice.
    /// </summary>
    private static ArmClientRequest Request(
        string method,
        string url,
        IReadOnlyDictionary<string, string>? query,
        IReadOnlyDictionary<string, string>? headers,
        JsonNode? body)
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mark = url.IndexOf('?', StringComparison.Ordinal);
        var path = mark < 0 ? url : url[..mark];
        if (mark >= 0)
        {
            foreach (var part in url[(mark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=', StringComparison.Ordinal);
                var key = Uri.UnescapeDataString(eq < 0 ? part : part[..eq]);
                if (key.Length > 0)
                    pairs[key] = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..]);
            }
        }

        if (query is not null)
        {
            foreach (var (key, value) in query)
                pairs[key] = value;
        }

        // HttpArmClient always resolves against its base address, so an absolute location is reduced to
        // its path; the query pairs above carry everything else.
        if (Uri.TryCreate(path, UriKind.Absolute, out var absolute))
            path = absolute.AbsolutePath;

        return new ArmClientRequest(method, path, pairs, headers, body);
    }

    private static IReadOnlyDictionary<string, string>? IfMatch(string? ifMatch) =>
        string.IsNullOrEmpty(ifMatch)
            ? null
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["If-Match"] = ifMatch };

    private static string? Header(ArmClientResponse response, string name) =>
        response.Headers.TryGetValue(name, out var value)
            ? value
            : response.Headers.FirstOrDefault(kv => kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static long? ReadLong(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

    private static ArmError OperationError(JsonNode? body, string status)
    {
        var node = body?["error"] ?? body?["properties"]?["error"];
        if (node is JsonObject error)
        {
            return new ArmError(
                Text(error["code"]) ?? status,
                Text(error["message"]) ?? $"The operation reported '{status}'.",
                Text(error["target"]),
                [],
                error["innererror"] as JsonObject,
                500);
        }

        return status.Equals("Canceled", StringComparison.OrdinalIgnoreCase)
            ? ArmError.Conflict("Canceled", "The operation was canceled.")
            : new ArmError("OperationFailed", $"The operation reported '{status}'.", null, [], null, 500);
    }
}
