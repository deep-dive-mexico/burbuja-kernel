using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Burbuja.Core.Abstractions.Arm;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Arm;

namespace Burbuja.Host.Arm;

public static class ArmEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapMethods("/subscriptions/{subscriptionId}/{**rest}", ["GET", "PUT", "PATCH", "DELETE", "POST", "HEAD"], (Delegate)HandleAsync);
        app.MapMethods("/subscriptions", ["GET", "HEAD"], (Delegate)HandleAsync);
        app.MapMethods("/providers/{**rest}", ["GET", "PUT", "PATCH", "DELETE", "POST", "HEAD"], (Delegate)HandleAsync);
        app.MapMethods("/providers", ["GET", "HEAD"], (Delegate)HandleAsync);
        app.MapMethods("/health/{**probe}", ["GET"], (Delegate)HandleAsync);
    }

    internal static Task<IResult> HandleAsync(HttpContext http) => HandleDelegatedAsync(http, null);

    internal static async Task<IResult> HandleDelegatedAsync(HttpContext http, ArmPrincipal? delegatedPrincipal)
    {
        using var scope = http.RequestServices.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<ArmPipeline>();
        var events = scope.ServiceProvider.GetRequiredService<IEventLog>();
        var table = scope.ServiceProvider.GetRequiredService<ArmRoutingTable>();
        var context = Bind(http, scope.ServiceProvider);
        context.Principal = delegatedPrincipal;
        if (IsDataPlaneRequest(table, context.Path))
        {
            await pipeline.RunThroughAsync(context, 700, http.RequestAborted);
            if (context.Error is not null)
                return ToHttp(http, context);
            if (context.IsDataPlane)
                return await DataPlane(http, context, table, events, scope.ServiceProvider);
        }

        await pipeline.RunAsync(context, http.RequestAborted);
        return ToHttp(http, context);
    }

    /// <summary>
    /// A data-plane request is one the routing table resolves to a declared data-plane segment —
    /// including artifact downloads whose segment carries a sub-path (`…/artifacts/patch.diff`),
    /// which a suffix heuristic misses.
    /// </summary>
    private static bool IsDataPlaneRequest(ArmRoutingTable table, string path) =>
        ResourceId.TryParse(path, table, out var id, out _) && id.DataPlaneSegment is not null;

    internal static ArmRequestContext Bind(HttpContext http, IServiceProvider services)
    {
        http.Request.EnableBuffering();
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in http.Request.Query)
            query[kv.Key] = kv.Value.ToString();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in http.Request.Headers)
            headers[header.Key] = header.Value.ToString();
        return new ArmRequestContext
        {
            Method = http.Request.Method.ToUpperInvariant(),
            Path = http.Request.Path.Value ?? "/",
            Query = query,
            Headers = headers,
            Body = http.Request.Body,
            Services = services,
        };
    }

    internal static IResult ToHttp(HttpContext http, ArmRequestContext context)
    {
        foreach (var (key, value) in context.ResponseHeaders)
            http.Response.Headers[key] = value;
        if (context.StatusCode == StatusCodes.Status204NoContent)
            return Results.StatusCode(StatusCodes.Status204NoContent);
        if (context.Result is null)
            return Results.StatusCode(context.StatusCode);
        return Results.Json(context.Result, JsonWire.Options, "application/json", context.StatusCode);
    }

    private static async Task<IResult> DataPlane(
        HttpContext http,
        ArmRequestContext context,
        ArmRoutingTable table,
        IEventLog events,
        IServiceProvider services)
    {
        foreach (var (key, value) in context.ResponseHeaders)
            http.Response.Headers[key] = value;
        var id = context.ResourceId ?? throw new InvalidOperationException("data-plane request is missing a resource id.");
        var kind = ResolveKind(table, id);
        var segment = id.DataPlaneSegment ?? "";

        if (kind is DataPlaneKind.Stream or DataPlaneKind.Download)
        {
            // Download/Stream is served by the namespace's IDataPlaneHandler. No handler, or a
            // handler that does not know the key, is a 404 — never an empty 200.
            var handler = ResolveDataPlane(services, id.Namespace);
            if (handler is null)
                return NotFound($"No data-plane handler is registered for '{id.Namespace}'.");

            var payload = await handler.OpenAsync(id, segment, kind, context.Principal, context.Query, http.RequestAborted);
            if (payload is null)
                return NotFound($"No data-plane content for '{id.Canonical}'.");

            if (!string.IsNullOrWhiteSpace(payload.ETag))
                http.Response.Headers.ETag = payload.ETag;
            return Results.Stream(payload.Content, payload.ContentType, payload.FileName);
        }

        var resolver = ResolveEventStreams(services, id.Namespace);
        var canonical = (id with { DataPlaneSegment = null, Action = null, DataPlanePath = null }).Canonical;
        var streamId = resolver is not null && resolver.TryResolve(id, out var resolved)
            ? resolved
            : EventStreamIds.Resource(canonical);
        // H1: `/events/verify` walks the stream's hash chain and reports the FIRST link that disagrees.
        // It pages through the stream itself rather than tailing it, because a chain cannot be verified
        // from its tail: the link that broke may be a thousand events back, and a verifier that only saw
        // the end would call a tampered stream clean.
        if (string.Equals(id.DataPlanePath, "verify", StringComparison.OrdinalIgnoreCase))
            return await VerifyChainAsync(events, streamId, http.RequestAborted);

        return WantsJsonPage(http)
            ? await JsonPage(events, streamId, context.Query, http.RequestAborted)
            : TypedResults.ServerSentEvents(Tail(events, streamId, LastEventId(http), http.RequestAborted));
    }

    /// <summary>How many events one verification reads before it stops and says it stopped.</summary>
    private const int VerifyPageLimit = 10_000;

    /// <summary>
    /// Verify a stream's chain.
    ///
    /// <para><b>The bound is reported, not hidden.</b> A stream longer than the limit is verified as far
    /// as it was read and the answer says <c>complete: false</c> — reporting "verified" over a prefix is
    /// exactly the vacuous green this repository keeps finding in other people's checks.</para>
    /// </summary>
    private static async Task<IResult> VerifyChainAsync(IEventLog events, string streamId, CancellationToken ct)
    {
        var chain = new List<LedgerEvent>();
        await foreach (var evt in events.ReadRangeAsync(streamId, 0, long.MaxValue, 0, null, ct))
        {
            chain.Add(evt);
            if (chain.Count > VerifyPageLimit)
                break;
        }

        var truncated = chain.Count > VerifyPageLimit;
        if (truncated)
            chain.RemoveAt(chain.Count - 1);

        var broke = EventChain.Verify(chain);
        return Results.Json(
            new JsonObject
            {
                ["streamId"] = streamId,
                ["events"] = chain.Count,
                ["complete"] = !truncated,
                ["verified"] = broke is null,
                // The FIRST break, because the ones after it are its consequences.
                ["firstBrokenStreamSeq"] = broke?.StreamSeq,
                ["streamSeq"] = broke?.StreamSeq is { } s ? (JsonNode)s : null,
                ["reason"] = broke?.Reason,
                ["headHash"] = chain.Count > 0 ? chain[^1].Hash : null,
            },
            statusCode: 200);
    }

    /// <summary>An explicit application/json accept that does not also ask for the live event stream.</summary>
    private static bool WantsJsonPage(HttpContext http)
    {
        var json = false;
        foreach (var header in http.Request.Headers.Accept)
        {
            if (header is null)
                continue;
            foreach (var part in header.Split(','))
            {
                var media = part.Split(';')[0].Trim();
                if (media.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (media.Equals("application/json", StringComparison.OrdinalIgnoreCase))
                    json = true;
            }
        }

        return json;
    }

    private static async Task<IResult> JsonPage(
        IEventLog events,
        string streamId,
        IReadOnlyDictionary<string, string> query,
        CancellationToken ct)
    {
        if (!TryQueryLong(query, "from", 0, out var from))
            return InvalidQuery("Query parameter 'from' must be an integer.");
        if (!TryQueryLong(query, "to", long.MaxValue, out var to))
            return InvalidQuery("Query parameter 'to' must be an integer.");
        if (!TryQueryLong(query, "limit", 500, out var limit))
            return InvalidQuery("Query parameter 'limit' must be an integer.");
        if (from < 0)
            from = 0;
        if (to <= from)
            return InvalidQuery("Query parameter 'to' must be greater than 'from'.");
        var pageSize = (int)Math.Clamp(limit, 1, 5000);

        // T4: `q` turns the page into a search. Same page shape, but the walk reads past the events it
        // does not return, so `nextFrom` is where the SCAN resumes and `scanned` says how much of the
        // stream that page actually looked at — a search that returned nothing has to be able to say
        // whether it looked everywhere.
        var terms = EventSearch.Parse(query.TryGetValue("q", out var raw) ? raw : null);
        if (terms.Count > 0)
            return await SearchEventsPage(events, streamId, from, to, pageSize, terms, ct);

        var value = new List<LedgerEvent>();
        await foreach (var evt in events.ReadRangeAsync(streamId, from, to, pageSize, null, ct))
            value.Add(evt);
        var nextFrom = value.Count == pageSize ? value[^1].Seq + 1 : (long?)null;
        return Results.Json(new EventPage(value, value.Count, nextFrom), JsonWire.Options, "application/json");
    }

    /// <summary>
    /// One page of matches. The scan stops at <paramref name="pageSize"/> matches or at
    /// <see cref="EventSearch.ScanLimit"/> events read, whichever comes first; either way
    /// <c>nextFrom</c> names the first event the next page has NOT looked at, so following it cannot
    /// skip an event between two matches.
    /// </summary>
    private static async Task<IResult> SearchEventsPage(
        IEventLog events,
        string streamId,
        long from,
        long toExclusive,
        int pageSize,
        IReadOnlyList<string> terms,
        CancellationToken ct)
    {
        var value = new List<LedgerEvent>();
        long scanned = 0;
        long? nextFrom = null;
        await foreach (var evt in events.ReadRangeAsync(streamId, from, toExclusive, 0, null, ct))
        {
            scanned++;
            if (EventSearch.Matches(evt, terms))
                value.Add(evt);
            if (value.Count >= pageSize || scanned >= EventSearch.ScanLimit)
            {
                nextFrom = evt.Seq + 1;
                break;
            }
        }

        return Results.Json(new SearchPage(value, value.Count, nextFrom, scanned, terms), JsonWire.Options, "application/json");
    }

    private static bool TryQueryLong(IReadOnlyDictionary<string, string> query, string key, long fallback, out long value)
    {
        value = fallback;
        return !query.TryGetValue(key, out var raw)
               || string.IsNullOrWhiteSpace(raw)
               || long.TryParse(
                   raw,
                   System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out value);
    }

    /// <summary>Pages carry nextFrom explicitly as null, so the null must survive JsonWire's WhenWritingNull.</summary>
    private sealed record EventPage(
        IReadOnlyList<LedgerEvent> Value,
        int Count,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] long? NextFrom);

    /// <summary>
    /// A search page. <c>scanned</c> and <c>terms</c> cost two fields and remove the two questions a
    /// bare result set leaves open: "did it look at everything?" and "what did it think I asked for?".
    /// </summary>
    private sealed record SearchPage(
        IReadOnlyList<LedgerEvent> Value,
        int Count,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] long? NextFrom,
        long Scanned,
        IReadOnlyList<string> Terms);

    private static IResult NotFound(string message) =>
        Results.Json(new { error = new { code = "NotFound", message } }, JsonWire.Options, "application/json", StatusCodes.Status404NotFound);

    private static IResult InvalidQuery(string message) =>
        Results.Json(new { error = new { code = "InvalidQueryParameter", message } }, JsonWire.Options, "application/json", StatusCodes.Status400BadRequest);

    private static IDataPlaneHandler? ResolveDataPlane(IServiceProvider services, string ns)
    {
        foreach (var handler in services.GetServices<IDataPlaneHandler>())
        {
            if (string.Equals(handler.Namespace, ns, StringComparison.OrdinalIgnoreCase))
                return handler;
        }

        return null;
    }

    private static IEventStreamResolver? ResolveEventStreams(IServiceProvider services, string ns)
    {
        foreach (var resolver in services.GetServices<IEventStreamResolver>())
        {
            if (string.Equals(resolver.Namespace, ns, StringComparison.OrdinalIgnoreCase))
                return resolver;
        }

        return null;
    }

    private static DataPlaneKind ResolveKind(ArmRoutingTable table, ResourceId id)
    {
        var reg = table.Find(id);
        var segment = id.DataPlaneSegment ?? "";
        var route = reg?.Descriptor.DataPlaneRoutes.FirstOrDefault(r =>
            r.Segment.Equals(segment, StringComparison.OrdinalIgnoreCase));
        return route?.Kind ?? DataPlaneKind.ServerSentEvents;
    }

    private static long LastEventId(HttpContext http)
    {
        if (!http.Request.Headers.TryGetValue("Last-Event-ID", out var raw))
            return 0;
        return long.TryParse(raw.ToString(), out var seq) ? seq : 0;
    }

    private static async IAsyncEnumerable<SseItem<string>> Tail(
        IEventLog events,
        string streamId,
        long last,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var ev in events.TailAsync(streamId, last, ct))
        {
            var data = ev.Payload?.ToJsonString() ?? "{}";
            yield return new SseItem<string>(data, ev.Type) { EventId = ev.Seq.ToString() };
        }
    }
}
