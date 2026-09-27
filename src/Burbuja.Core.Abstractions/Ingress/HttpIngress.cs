using System.Text;

namespace Burbuja.Core.Abstractions.Ingress;

/// <summary>
/// M14 R10 (request row X5) — a PLUGIN's own HTTP door, mounted by the kernel at <c>/ingress/{route}</c>.
///
/// <para><b>Why this is not <see cref="IngressEnvelope"/>.</b> The two live in one folder and share a word:
/// an envelope is a CHANNEL handing the kernel a message to become a turn, and this is a plugin SERVING a
/// request nobody in the kernel can interpret (a WhatsApp webhook, a Discord interaction, an OAuth callback).
/// A plugin that speaks a third party's HTTP dialect should not have to make the kernel learn it.</para>
///
/// <para><b>What the kernel does, and what the plugin does.</b> The kernel maps the route, allows anonymous
/// transport (a third party has no API key of ours and never will), enforces
/// <see cref="MaxBodyBytes"/> before the plugin sees a byte, and writes one <c>plugin.ingress.request</c>
/// event per call carrying the method, the status and the sizes — <b>never the body or the headers</b>, because
/// a webhook's payload is somebody else's data and its signature header is a secret. The PLUGIN verifies what
/// it receives: a verify token, an HMAC, a shared secret is the plugin's own (<c>Secrets</c> capability), not
/// the kernel's, and a plugin that trusts every anonymous POST has said so in its own code.</para>
///
/// <para><b>Capability.</b> Registering one of these requires <c>PluginCapability.HttpEndpoints</c> in the
/// plugin's manifest, and declaring the capability requires registering at least one — both refused at boot
/// (<c>PluginHost</c> step 11b), which is the check the manifest field has been promising since it existed.</para>
/// </summary>
public interface IHttpIngressEndpoint
{
    /// <summary>The plugin this door belongs to, as its manifest spells it. The call's audit event is written
    /// to this plugin's own stream, so a door cannot report under another plugin's name.</summary>
    string PluginId { get; }

    /// <summary>ONE path segment — <c>/ingress/{route}</c> and nothing deeper. A plugin cannot mount a
    /// subtree: the route is a door, not a router. Validated at boot by
    /// <see cref="HttpIngressLimits.IsValidRoute"/>.</summary>
    string Route { get; }

    /// <summary>The HTTP methods the door takes, uppercase, drawn from
    /// <see cref="HttpIngressLimits.AllowedMethods"/>. A method that is not listed is answered 405 by the
    /// kernel without reaching the plugin.</summary>
    IReadOnlyList<string> Methods { get; }

    /// <summary>The largest request body this door accepts. Must be positive and no larger than
    /// <see cref="HttpIngressLimits.MaxBodyBytes"/>; the kernel refuses to map a door that asks for more, and
    /// answers 413 without calling the plugin when a request exceeds it.</summary>
    long MaxBodyBytes { get; }

    /// <summary>The plugin's answer, returned as-is. A throw is the kernel's 500 — the plugin's own failure
    /// does not become the caller's business, and the event records it.</summary>
    ValueTask<HttpIngressReply> HandleAsync(HttpIngressRequest request, CancellationToken ct);
}

/// <summary>One inbound call, already bounded. <paramref name="Body"/> is at most the endpoint's
/// <see cref="IHttpIngressEndpoint.MaxBodyBytes"/> and is empty — not null — when the request carried
/// none.</summary>
public sealed record HttpIngressRequest(
    string Method,
    string Route,
    IReadOnlyDictionary<string, string> Headers,
    ReadOnlyMemory<byte> Body,
    string? RemoteAddress)
{
    /// <summary>The body as UTF-8 text, for the many dialects that are JSON or form-encoded.</summary>
    public string BodyText() => Body.IsEmpty ? "" : Encoding.UTF8.GetString(Body.Span);

    /// <summary>One header, case-insensitively — HTTP header names are not case-sensitive and a plugin that
    /// looked up <c>X-Hub-Signature-256</c> exactly would reject a correct caller.</summary>
    public string? Header(string name) =>
        Headers.TryGetValue(name, out var value)
            ? value
            : Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>What the kernel writes back. <paramref name="Body"/> defaults to empty, which is the honest
/// answer for a door that only says "received".</summary>
public sealed record HttpIngressReply(int StatusCode, string? ContentType = null, ReadOnlyMemory<byte> Body = default)
{
    public static HttpIngressReply Text(int statusCode, string text, string contentType = "text/plain; charset=utf-8") =>
        new(statusCode, contentType, Encoding.UTF8.GetBytes(text));

    public static HttpIngressReply Json(int statusCode, string json) =>
        new(statusCode, "application/json", Encoding.UTF8.GetBytes(json));

    public static HttpIngressReply Empty(int statusCode) => new(statusCode);
}

/// <summary>
/// The kernel's own bounds on every door, in one place so the boot check and the mapper cannot disagree.
/// </summary>
public static class HttpIngressLimits
{
    /// <summary>The ceiling the kernel will buffer for any plugin. A plugin may ask for less; it may not ask
    /// for more, because the size of a third party's POST is not the plugin's decision to make.</summary>
    public const long MaxBodyBytes = 1024 * 1024;

    /// <summary>Long enough for a readable name, short enough that the route stays a segment.</summary>
    public const int MaxRouteLength = 64;

    public static readonly string[] AllowedMethods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD"];

    /// <summary>A route is one lowercase segment: a letter or digit, then letters, digits, <c>.</c>, <c>_</c>
    /// or <c>-</c>. No slash (a door is not a subtree), no case (two doors that differ only in case are two
    /// doors, and <c>/ingress/WhatsApp</c> beside <c>/ingress/whatsapp</c> is a bug waiting to be reported).</summary>
    public static bool IsValidRoute(string? route)
    {
        if (string.IsNullOrEmpty(route) || route.Length > MaxRouteLength)
            return false;
        if (!char.IsAsciiLetterLower(route[0]) && !char.IsAsciiDigit(route[0]))
            return false;

        // Lowercase on purpose, and this is the half a first draft got wrong: `WhatsApp` passed a check that
        // only asked for letters, and two doors differing in case are two routes a caller reaches differently
        // and an operator reads as one.
        return route.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '-');
    }

    /// <summary>Whether a plugin's declared bound is one the kernel can honour.</summary>
    public static bool IsValidBodyBound(long maxBodyBytes) => maxBodyBytes > 0 && maxBodyBytes <= MaxBodyBytes;
}
