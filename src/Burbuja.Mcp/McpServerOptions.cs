namespace Burbuja.Mcp;

/// <summary>
/// The MCP server's own settings. It is mounted on the control-plane role and speaks the ARM
/// tenancy the host was seeded with, because an MCP caller names an agent by bare name and the
/// authorisation decision needs a full scope.
/// </summary>
public sealed class McpServerOptions
{
    public const string Section = "Mcp:Server";

    /// <summary>The endpoint is mounted unless a deployment explicitly turns it off.</summary>
    public bool Enabled { get; set; } = true;

    public string SubscriptionId { get; set; } = "local";

    public string ResourceGroup { get; set; } = "dev";

    /// <summary>The name consumers configure for this server (Claude Code sees <c>mcp__burbuja__*</c>).</summary>
    public string ServerName { get; set; } = Abstractions.McpConstants.ServerName;

    /// <summary>A tool result larger than this is truncated and says so; MCP frames are not a stream.</summary>
    public int MaxToolResultChars { get; set; } = 200_000;

    /// <summary>
    /// How long a session may go between requests before the kernel forgets it. The SDK expires its own
    /// session on the same clock; this is the kernel's bookkeeping, and it is shorter than the SDK's two hours
    /// on purpose — the registry is what a portal reads to answer "who is connected", and a connection nobody
    /// has used for half an hour is not news.
    /// </summary>
    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The ceiling on sessions the kernel tracks. Past it the oldest idle session is dropped, mirroring the
    /// SDK's own rule: a client that never says goodbye must not be able to grow this without bound.
    /// </summary>
    public int MaxIdleSessions { get; set; } = 1_000;

    /// <summary>
    /// Whether a client is told its tool catalogue changed. On by default, because a catalogue that changes
    /// under a connected client without a word is how an agent ends up calling a tool it can no longer see —
    /// or failing to call one it now can.
    /// </summary>
    public bool AnnounceCatalogueChanges { get; set; } = true;
}
