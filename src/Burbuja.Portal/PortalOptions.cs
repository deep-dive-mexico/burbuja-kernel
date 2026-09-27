namespace Burbuja.Portal;

/// <summary>
/// Everything the portal needs to reach the kernel and to keep a browser session alive. The base
/// URL is the Host's <em>own</em> ARM front door (<c>Kestrel:Endpoints:Http:Url</c>), never a
/// second channel: the portal is an ordinary ARM client of the process that hosts it.
/// </summary>
public sealed class PortalOptions
{
    public const string SectionName = "Portal";

    /// <summary>Absolute base URL of the Host's own front door, e.g. <c>http://127.0.0.1:18080</c>.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>The api-version every portal call carries; the kernel rejects a missing one.</summary>
    public string ApiVersion { get; set; } = "2026-09-01";

    /// <summary>Subscription this portal reads. Empty permits discovery only when exactly one is visible.</summary>
    public string SubscriptionId { get; set; } = "";

    /// <summary>Resource group this portal reads within its subscription. Empty permits discovery only when exactly one is visible.</summary>
    public string ResourceGroup { get; set; } = "";

    /// <summary>
    /// M15 M-R1 (item 25, §4.2) — the public origin the console is reached at, e.g.
    /// <c>https://console.example.com</c>, or empty when the console is only reached on loopback.
    ///
    /// <para><b>Why this exists.</b> A channel replies into Discord with a link back to the trace, and a
    /// link built from <see cref="BaseUrl"/> is <c>http://127.0.0.1:18480</c> — an address that means
    /// "this Mac" to every reader except the one who sent the message. The host knows the name it is
    /// served under; this is where it says so. Empty is a real value: a reply then carries the
    /// loopback link and the caller knows it is loopback-only, rather than guessing a public name the
    /// host never claimed.</para>
    /// </summary>
    public string PublicBaseUrl { get; set; } = "";

    /// <summary>
    /// The absolute URL for a console path, using <see cref="PublicBaseUrl"/> when it is set and
    /// <see cref="BaseUrl"/> otherwise. The path is appended as given (leading slash normalized), so a
    /// caller building a trace link does not have to know which origin is configured.
    /// </summary>
    public string PublicUrl(string path)
    {
        var origin = PublicBaseUrl.Length > 0 ? PublicBaseUrl : BaseUrl;
        if (origin.Length == 0)
            return path;
        return origin.TrimEnd('/') + "/" + path.TrimStart('/');
    }

    /// <summary>Name of the session cookie. Its value is a session id, never a key.</summary>
    public string CookieName { get; set; } = "portal";

    /// <summary>Sliding lifetime of an in-memory session; a restart invalidates every session.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(12);

    /// <summary>Most concurrent event streams one circuit may hold.</summary>
    public int MaxStreamsPerCircuit { get; set; } = 3;

    /// <summary>How often buffered SSE frames are pushed to the renderer.</summary>
    public TimeSpan StreamFlushInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Bounded frame buffer per stream; the oldest frame is dropped first.</summary>
    public int StreamBufferSize { get; set; } = 2000;

    /// <summary>Bounded event range a detail page reads before offering "load older".</summary>
    public int EventPageSize { get; set; } = 500;

    /// <summary>Largest JSON document the record inspector renders verbatim.</summary>
    public int JsonDisplayLimit { get; set; } = 128 * 1024;

    /// <summary>
    /// Largest artifact body the ring renders inline. A pomnibot artifact arrives base64 in an
    /// action response, so the page decodes it to text; a patch larger than this is truncated with
    /// a visible marker rather than shipped into the render tree whole.
    /// </summary>
    public int ArtifactInlineLimit { get; set; } = 256 * 1024;

    /// <summary>Reconnect backoff bounds for a dropped event stream.</summary>
    public TimeSpan StreamBackoffMin { get; set; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan StreamBackoffMax { get; set; } = TimeSpan.FromSeconds(5);
}
