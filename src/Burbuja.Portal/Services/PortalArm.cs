using System.Net;
using System.Net.Http.Headers;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Arm;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Burbuja.Portal.Services;

/// <summary>The portal's only door into the kernel.</summary>
public interface IPortalArm
{
    /// <summary>The signed-in session, or null for an anonymous render.</summary>
    ValueTask<PortalSession?> SessionAsync();

    /// <summary>A typed ARM client carrying this session's key, or null when nobody is signed in.</summary>
    ValueTask<IArmClient?> ClientAsync(CancellationToken ct = default);

    /// <summary>Same, but throws <see cref="PortalNotSignedInException"/> instead of returning null.</summary>
    ValueTask<IArmClient> RequireClientAsync(CancellationToken ct = default);

    /// <summary>Open a data-plane download (never buffered).</summary>
    ValueTask<PortalDownload?> DownloadAsync(string path, CancellationToken ct = default);

    /// <summary>Probe a data-plane route without holding its body.</summary>
    ValueTask<PortalArtifact> ProbeAsync(string path, CancellationToken ct = default);
}

public sealed record PortalDownload(int Status, string? ContentType, string? FileName, Stream? Content, string? Error)
    : IDisposable
{
    public void Dispose() => Content?.Dispose();
}

public sealed record PortalArtifact(string Path, int Status, string? ContentType, long? Length)
{
    public bool Exists => Status is >= 200 and < 300;

    /// <summary>
    /// The body of an artifact fetched through an ARM <em>action</em> (the pomnibot
    /// <c>getArtifact</c> POST returns base64). Null for a data-plane route, which the page links
    /// to <c>/portal/download</c> instead of inlining.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>Display name when <see cref="Path"/> is an action path rather than a download route.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// True when a GET of <see cref="Path"/> is what serves the bytes. An action-fetched artifact
    /// has no GET route (a probe of <c>jobs/{n}/artifacts/…</c> is a 400 InvalidResourceType), so
    /// it offers no download link rather than one that cannot work.
    /// </summary>
    public bool Downloadable => Exists && Text is null;

    /// <summary>True when the action answered but the run never wrote this artifact.</summary>
    public bool Absent => Status == 404;
}

/// <summary>
/// Resolves the live session for the current render. A Blazor circuit has no HttpContext after the
/// initial render, so the cascading <see cref="AuthenticationStateProvider"/> is the primary source;
/// during static SSR (and for endpoint calls) the request's principal is the same ticket, so it is
/// the fallback. Both paths end at <see cref="PortalSessionStore"/>: the claim is only an id.
/// </summary>
public sealed class PortalSessionAccessor(
    AuthenticationStateProvider authentication,
    IHttpContextAccessor http,
    PortalSessionStore sessions)
{
    public ValueTask<PortalSession?> GetAsync() => ResolveAsync();

    private async ValueTask<PortalSession?> ResolveAsync()
    {
        try
        {
            var state = await authentication.GetAuthenticationStateAsync();
            return From(state.User);
        }
        catch (InvalidOperationException)
        {
            // No authentication state provider in this scope (a bare component test); fall through.
        }

        return http.HttpContext is { } context ? From(context.User) : null;
    }

    private PortalSession? From(System.Security.Claims.ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
            return null;
        var id = principal.FindFirst(PortalAuth.SessionClaim)?.Value;
        return string.IsNullOrEmpty(id) ? null : sessions.Get(id);
    }
}

/// <summary>
/// Every portal read is an ARM call this process makes to its own front door, as the signed-in
/// principal: an <see cref="HttpArmClient"/> against the Host's base URL
/// (<c>Kestrel:Endpoints:Http:Url</c>) carrying that session's key. RBAC is therefore enforced by
/// the kernel: a Reader login sees a 403 envelope on a write, and hiding a button in the UI is UX,
/// never security.
/// </summary>
public sealed class PortalArm(
    IHttpClientFactory http,
    PortalSessionAccessor sessions,
    PortalOptions options,
    ILogger<PortalArm> logger) : IPortalArm
{
    public const string ClientName = "portal-arm";

    public ValueTask<PortalSession?> SessionAsync() => sessions.GetAsync();

    public async ValueTask<IArmClient?> ClientAsync(CancellationToken ct = default)
    {
        var session = await sessions.GetAsync();
        return session is null
            ? null
            : new GuardedArmClient(new HttpArmClient(http.CreateClient(ClientName), options.ApiVersion, session.ApiKey),
                async () => (await sessions.GetAsync())?.Id == session.Id);
    }

    public async ValueTask<IArmClient> RequireClientAsync(CancellationToken ct = default) =>
        await ClientAsync(ct) ?? throw new ArmException(GuardedArmClient.ExpiredError);

    public async ValueTask<PortalDownload?> DownloadAsync(string path, CancellationToken ct = default)
    {
        var session = await sessions.GetAsync();
        if (session is null)
            return null;

        var request = new HttpRequestMessage(HttpMethod.Get, Build(path));
        request.Headers.TryAddWithoutValidation("x-api-key", session.ApiKey);
        var response = await http.CreateClient(ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            return new PortalDownload(status, null, null, null, body);
        }

        var stream = await response.Content.ReadAsStreamAsync(ct);
        return new PortalDownload(
            (int)response.StatusCode,
            response.Content.Headers.ContentType?.ToString(),
            response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName
                ?? FileName(path),
            stream,
            null);
    }

    public async ValueTask<PortalArtifact> ProbeAsync(string path, CancellationToken ct = default)
    {
        var session = await sessions.GetAsync();
        if (session is null)
            return new PortalArtifact(path, 401, null, null);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Build(path));
            request.Headers.TryAddWithoutValidation("x-api-key", session.ApiKey);
            using var response = await http.CreateClient(ClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return new PortalArtifact(
                path,
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.ToString(),
                response.Content.Headers.ContentLength);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Probe of {Path} failed.", path);
            return new PortalArtifact(path, 0, null, null);
        }
    }

    /// <summary>
    /// The client's base address is the Host's own URL, so a relative data-plane path is enough;
    /// api-version is appended unless the path already carries one.
    /// </summary>
    private string Build(string path)
    {
        var relative = path.StartsWith('/') ? path : "/" + path;
        if (relative.Contains("api-version=", StringComparison.OrdinalIgnoreCase))
            return relative;
        return relative + (relative.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "api-version=" + Uri.EscapeDataString(options.ApiVersion);
    }

    private static string? FileName(string path)
    {
        var leaf = path.Split('?')[0].TrimEnd('/').Split('/').LastOrDefault();
        return string.IsNullOrEmpty(leaf) ? null : leaf;
    }
}

public sealed class PortalNotSignedInException()
    : InvalidOperationException("The portal has no live session; the caller is anonymous.");

/// <summary>
/// Turns a transport failure into an ARM failure. The portal calls the Host it runs in, so a
/// connection error means the front door is not answering (a wrong <c>Portal:BaseUrl</c>, a host
/// that is shutting down, a cancelled request): that is a rendered 503 envelope, never a 500 from
/// an unhandled <see cref="HttpRequestException"/> that hides which panel was affected.
/// </summary>
internal sealed class GuardedArmClient(IArmClient inner, Func<ValueTask<bool>>? isLive = null) : IArmClient
{
    internal static ArmError ExpiredError => new("PortalSessionExpired", "The portal session ended. Sign in again.", null, [], null, 401);
    public async ValueTask<ArmClientResponse> SendAsync(ArmClientRequest request, CancellationToken ct)
    {
        if (isLive is not null && !await isLive())
            return new ArmClientResponse(401, new Dictionary<string, string>(), null, ExpiredError);
        try
        {
            return await inner.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            return Unavailable(request.Path, ex.Message);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return Unavailable(request.Path, ex.Message);
        }
    }

    public async IAsyncEnumerable<ArmSseEvent> StreamAsync(string path, string? lastEventId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (isLive is not null && !await isLive()) yield break;
        await foreach (var frame in inner.StreamAsync(path, lastEventId, ct))
        {
            if (isLive is not null && !await isLive()) yield break;
            yield return frame;
        }
    }

    private static ArmClientResponse Unavailable(string path, string message) =>
        new(
            503,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            null,
            new ArmError(
                "ArmUnavailable",
                $"The kernel's front door did not answer for '{path}': {message}",
                path,
                [],
                null,
                503));
}

public static class PortalArmExtensions
{
    /// <summary>
    /// POST an action with an <c>If-Match</c> precondition. A 412 is returned as a failed response
    /// for the page to render — never retried, never silently swallowed: the caller's view of the
    /// resource is stale and a human has to look at it.
    /// </summary>
    public static async ValueTask<ArmClientResponse> PostActionAsync(
        this IArmClient client,
        string path,
        System.Text.Json.Nodes.JsonNode? body,
        string? ifMatch,
        CancellationToken ct = default)
    {
        var headers = string.IsNullOrEmpty(ifMatch)
            ? null
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["If-Match"] = ifMatch };
        var response = await client.SendAsync(new ArmClientRequest("POST", path, Headers: headers, Body: body), ct);
        return response;
    }
}
