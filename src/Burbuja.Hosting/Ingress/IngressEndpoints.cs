using System.Diagnostics;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Ingress;
using Burbuja.Core.Plugin;

namespace Burbuja.Host.Ingress;

/// <summary>
/// M14 R10 (request row X5) — the kernel's side of <see cref="IHttpIngressEndpoint"/>: it maps each plugin's
/// door at <c>/ingress/{route}</c>, bounds what a stranger may send, tells the plugin, and writes one event.
///
/// <para><b>Anonymous on purpose, and that is not the same as unverified.</b> A third party (WhatsApp, a
/// Discord interaction, an OAuth callback) has no API key of ours; requiring one would make the door
/// unreachable by the only caller it exists for. What the kernel does instead is hold the PLUGIN to its own
/// declared bound, never hand it more than that, and record every call. Verification — a verify token, an
/// HMAC, an allow-list — belongs to the plugin, which owns the secret; a plugin that accepts anything says so
/// in its own code, where it can be read.</para>
///
/// <para><b>An event per call, never the payload.</b> Each call appends <c>plugin.ingress.request</c> to the
/// plugin's own stream with the method, the status, the byte counts and the duration. The body and the headers
/// are NOT in it: the body is a third party's data and a signature header is a secret, and an audit trail that
/// copies them into the ledger is a leak with a good name. Sizes are what an operator needs to see a door being
/// hammered.</para>
///
/// <para><b>Everything that can be known before the first request is checked at boot</b>, in
/// <see cref="Validate"/>: a route that is one valid segment, methods the kernel knows, a body bound within
/// <see cref="HttpIngressLimits.MaxBodyBytes"/>, and no two doors on one route — the last one is the failure
/// that would otherwise be discovered by whichever caller arrived second.</para>
/// </summary>
public static class IngressEndpoints
{
    /// <summary>The route prefix. One path segment after it: a door, not a router.</summary>
    public const string Prefix = "/ingress";

    /// <summary>The event kind every call writes.</summary>
    public const string EventType = "plugin.ingress.request";

    public static void Map(WebApplication app)
    {
        var endpoints = app.Services.GetService<IEnumerable<IHttpIngressEndpoint>>()?.ToList() ?? [];
        Validate(endpoints);

        foreach (var endpoint in endpoints)
        {
            var route = $"{Prefix}/{endpoint.Route}";
            // Every method the kernel knows is MAPPED, and the plugin's own list decides inside: mapping only
            // the listed methods answers 404 to a GET on a POST-only door (measured — the fact written for it
            // failed on the first run), and "there is no such URL" is the opposite of what a door that exists
            // and refuses a verb must say. The delegate is typed `Func<...>` on purpose: a bare lambda with an
            // HttpContext parameter binds as a RequestDelegate, whose IResult return value ASP.NET DISCARDS —
            // the response would be a 200 with no body and no compiler error (ASP0016 caught exactly that).
            Func<HttpContext, Task<IResult>> handler = http => HandleAsync(http, endpoint);
            app.MapMethods(route, HttpIngressLimits.AllowedMethods, handler).AllowAnonymous();
        }

        // The prefix belongs to the kernel. An unknown door is a 404 from HERE rather than whatever the shell
        // answers for a path it does not know — measured: a POST to `/ingress/nobody-mapped-this` was a 405
        // from the Razor component endpoint, which reads as "this door exists and refuses your verb". Literal
        // routes outrank this catch-all, so it never shadows a mounted door.
        app.Map($"{Prefix}/{{**rest}}", () => Results.NotFound()).AllowAnonymous();
    }

    /// <summary>
    /// The boot check for doors that DI can see. It throws rather than logs: a host that mounts a door it
    /// cannot describe is a host whose operator believes something is reachable that is not, and a duplicate
    /// route is two plugins claiming one URL — resolved silently by ASP.NET's "last one wins", which is
    /// exactly the kind of answer nobody can debug later.
    /// </summary>
    public static void Validate(IReadOnlyList<IHttpIngressEndpoint> endpoints)
    {
        var problems = new List<string>();
        var byRoute = new Dictionary<string, IHttpIngressEndpoint>(StringComparer.Ordinal);

        foreach (var endpoint in endpoints)
        {
            var who = $"plugin {Describe(endpoint)}";
            if (!HttpIngressLimits.IsValidRoute(endpoint.Route))
                problems.Add($"{who}: route '{endpoint.Route}' is not one valid segment ({HttpIngressLimits.MaxRouteLength} chars, lowercase, letters/digits/._-)");

            if (!HttpIngressLimits.IsValidBodyBound(endpoint.MaxBodyBytes))
                problems.Add($"{who}: MaxBodyBytes {endpoint.MaxBodyBytes} is outside (0, {HttpIngressLimits.MaxBodyBytes}]");

            if (endpoint.Methods.Count == 0)
                problems.Add($"{who}: declares no HTTP methods");

            foreach (var method in endpoint.Methods)
            {
                if (!HttpIngressLimits.AllowedMethods.Contains(method, StringComparer.Ordinal))
                    problems.Add($"{who}: method '{method}' is not one of {string.Join('/', HttpIngressLimits.AllowedMethods)} (uppercase)");
            }

            if (byRoute.TryGetValue(endpoint.Route, out var other))
                problems.Add($"{who}: route '{endpoint.Route}' is already mounted by plugin {Describe(other)} — one URL, one door");
            else
                byRoute[endpoint.Route] = endpoint;
        }

        if (problems.Count > 0)
            throw new PluginBootException(problems);
    }

    private static string Describe(IHttpIngressEndpoint endpoint) =>
        string.IsNullOrWhiteSpace(endpoint.PluginId) ? "(no plugin id)" : endpoint.PluginId;

    private static async Task<IResult> HandleAsync(HttpContext http, IHttpIngressEndpoint endpoint)
    {
        var started = Stopwatch.GetTimestamp();
        var events = http.RequestServices.GetRequiredService<IEventLog>();
        var log = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Burbuja.Host.Ingress");

        if (!endpoint.Methods.Contains(http.Request.Method, StringComparer.Ordinal))
        {
            http.Response.Headers.Allow = string.Join(", ", endpoint.Methods);
            await RecordAsync(events, endpoint, http.Request.Method, 405, -1, started, http, log, "method not allowed");
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
        }

        var request = await ReadAsync(http, endpoint);
        if (request is null)
        {
            // Over the bound: answered without reading the rest and without calling the plugin. The event
            // still happens — a door that is being pushed at is exactly what an operator wants to see.
            await RecordAsync(events, endpoint, http.Request.Method, 413, -1, started, http, log, "body over bound");
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        try
        {
            var reply = await endpoint.HandleAsync(request, http.RequestAborted);
            await RecordAsync(events, endpoint, request.Method, reply.StatusCode, request.Body.Length, started, http, log, null);

            // Written by hand rather than through `Results.Bytes`: that helper cannot carry a status code,
            // and a plugin's 403 with a reason in the body is the normal case for a webhook (WhatsApp's
            // verification handshake answers a challenge with 200 and a wrong token with 403).
            http.Response.StatusCode = reply.StatusCode;
            if (reply.ContentType is { Length: > 0 })
                http.Response.ContentType = reply.ContentType;
            if (!reply.Body.IsEmpty)
                await http.Response.Body.WriteAsync(reply.Body, http.RequestAborted);

            return Results.Empty;
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            // The caller hung up. Nothing to answer; the fact that the call happened is still recorded.
            await RecordAsync(events, endpoint, request.Method, 499, request.Body.Length, started, http, log, "caller aborted");
            throw;
        }
        catch (Exception ex)
        {
            // The plugin's failure is the plugin's; the caller gets a 500 with no internals in it, and the
            // operator gets the exception in the log and the status in the ledger.
            log.LogError(ex, "ingress /{Route} ({Plugin}) threw", endpoint.Route, endpoint.PluginId);
            await RecordAsync(events, endpoint, request.Method, 500, request.Body.Length, started, http, log, ex.GetType().Name);
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// The body, or <c>null</c> when it is over the door's bound. Read in bounded chunks rather than trusting
    /// <c>Content-Length</c>: a caller that lies about the length, or streams with no length at all, must not
    /// be able to make the kernel buffer a gigabyte before the plugin is asked anything.
    /// </summary>
    private static async Task<HttpIngressRequest?> ReadAsync(HttpContext http, IHttpIngressEndpoint endpoint)
    {
        if (http.Request.ContentLength is { } declared && declared > endpoint.MaxBodyBytes)
            return null;

        var limit = (int)Math.Min(endpoint.MaxBodyBytes, HttpIngressLimits.MaxBodyBytes);
        using var buffer = new MemoryStream();
        var chunk = new byte[Math.Min(limit, 16 * 1024)];
        while (true)
        {
            var read = await http.Request.Body.ReadAsync(chunk, http.RequestAborted);
            if (read == 0)
                break;

            if (buffer.Length + read > limit)
                return null;

            buffer.Write(chunk, 0, read);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in http.Request.Headers)
            headers[header.Key] = header.Value.ToString();

        return new HttpIngressRequest(
            http.Request.Method,
            endpoint.Route,
            headers,
            buffer.ToArray(),
            http.Connection.RemoteIpAddress?.ToString());
    }

    private static async Task RecordAsync(
        IEventLog events,
        IHttpIngressEndpoint endpoint,
        string method,
        int statusCode,
        long requestBytes,
        long started,
        HttpContext http,
        ILogger log,
        string? note)
    {
        var payload = new JsonObject
        {
            ["pluginId"] = endpoint.PluginId,
            ["route"] = endpoint.Route,
            ["method"] = method,
            ["statusCode"] = statusCode,
            ["requestBytes"] = requestBytes < 0 ? null : requestBytes,
            ["remoteAddress"] = http.Connection.RemoteIpAddress?.ToString(),
            ["durationMs"] = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1),
            // Present only when something went wrong, and never a message the caller could have written
            // into the ledger by sending it.
            ["note"] = note,
        };

        try
        {
            await events.AppendAsync(
                new EventAppend(
                    $"plugins/{endpoint.PluginId}",
                    endpoint.Route,
                    EventType,
                    $"{method} /{Prefix}/{endpoint.Route} → {statusCode}",
                    payload,
                    RenderLane.Output | RenderLane.Log,
                    endpoint.PluginId,
                    null,
                    "plugin.ingress"),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The call already happened and its answer is on the wire; failing it now because the ledger
            // refused would turn an audit problem into a refused webhook. Loud in the log, never silent.
            log.LogError(ex, "ingress /{Route} ({Plugin}): the request event could not be written", endpoint.Route, endpoint.PluginId);
        }
    }
}
