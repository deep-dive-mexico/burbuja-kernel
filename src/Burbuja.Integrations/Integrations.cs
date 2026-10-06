using System.Text.Json.Nodes;

namespace Burbuja.Integrations;

/// <summary>
/// I0: what an integration IS, as the kernel sees it.
///
/// <para><b>The abstraction is the point of this milestone, and it is one thing rather than four.</b> An MCP
/// server, the owner's mailbox, a browser, a Notion workspace: each is a named surface with a set of tools,
/// a way to reach it, and a policy that already knows how to judge its tools. What differs between them is
/// the tool vocabulary — which is data — and everything else is the same resource. So `outlook_send`,
/// `mcp__browser__navigate` and a Notion write are judged by the same pipeline that judges a shell command,
/// because the classifiers for them are already registered and keyed by exactly these tool names.</para>
///
/// <para><b>A tool is callable only when the resource DECLARES it.</b> The tools a server reports when asked
/// are recorded as <see cref="DiscoveredTools"/> — a fact about the server — and they are deliberately not
/// authority: authority is <see cref="Tools"/>, which a human wrote. A server that grows a new tool
/// overnight must not thereby gain a way to act, and a tool list nobody has read is not a decision anybody
/// made. This is the same rule the MCP lane's identity check follows: authenticated is not authorised.</para>
/// </summary>
/// <param name="Tools">
/// The tool names a caller may invoke, <b>spelled the way the POLICY sees them</b> — a browser tool is
/// <c>mcp__browser__navigate</c> and a mail tool is <c>outlook_send</c>, because that string is the action
/// kind the classifiers dispatch on. A name no classifier handles is not waved through: it classifies as
/// <c>unknown_kind</c> and escalates, which is the fail-closed direction. Empty is a valid state and means
/// the integration is declared but grants nothing yet.
/// </param>
/// <param name="DevHosts">
/// The hosts this integration may navigate to or read from without being treated as leaving the
/// development surface — the browser classifier's dev-host rule and the only piece of a browser session an
/// agent chooses freely. Declared on the integration because it is the surface that knows its own hosts.
/// </param>
public sealed record IntegrationProperties(
    string Kind = "mcp",
    IntegrationTransport? Transport = null,
    IReadOnlyList<string>? Tools = null,
    IReadOnlyList<string>? DevHosts = null,
    bool Enabled = false,
    /// <summary>The last probe's outcome, for a reader: "ok", "no transport", or the failure's message.</summary>
    string? State = null,
    DateTimeOffset? ProbedAt = null,
    /// <summary>What the surface reported when asked. A fact about it, never a permission.</summary>
    IReadOnlyList<string>? DiscoveredTools = null,
    /// <summary>How many times a declared tool has been invoked. Read by an operator, never enforced here.</summary>
    int Calls = 0,
    int SchemaVersion = 1,
    /// <summary>The explicit policy namespace for generic MCP tools: mcp__ServerName__remoteTool.</summary>
    string? ServerName = null);

/// <summary>
/// How the kernel reaches a surface. Deliberately data rather than a live client: the resource is a
/// declaration a portal renders and an operator edits, and the connection itself belongs to whatever
/// <see cref="IIntegrationCaller"/> the host registers.
/// </summary>
/// <param name="SecretRef">
/// The NAME of a secret, never the secret. A resource is read by a portal, an audit and a backup, and a
/// token in a resource property is a token in all three.
/// </param>
public sealed record IntegrationTransport(
    string Mode = "stdio",
    string? Command = null,
    IReadOnlyList<string>? Args = null,
    string? Url = null,
    string? SecretRef = null,
    /// <summary>Environment variable to secret NAME mappings for stdio; resolved values are never resource properties.</summary>
    IReadOnlyDictionary<string, string>? EnvironmentSecretRefs = null);

/// <summary>What a call returned, as the resource reports it.</summary>
public sealed record IntegrationCallResult(bool Ok, JsonNode? Output = null, string? Error = null);

/// <summary>
/// The seam to the outside world, and the only thing this milestone does not implement.
///
/// <para><b>Absent by default, and that is the honest state rather than a gap.</b> A host with no
/// registered caller refuses <c>call</c> and <c>probe</c> with a reason that names the configuration,
/// exactly as a channel with no transport refuses to be driven. Nothing here reaches the network on its own:
/// the abstraction is what this milestone is, and the four surfaces of I1–I3 are instances of it.</para>
/// </summary>
public interface IIntegrationCaller
{
    /// <summary>
    /// The integration kinds this caller serves.
    ///
    /// <para>I0 registered exactly one caller and the handler resolved it by type; with a mailbox, a browser
    /// and a Notion workspace registered side by side, "which caller reaches this surface" is a question the
    /// RESOURCE answers through its <c>kind</c>, and a caller that stayed silent about what it serves would
    /// make the answer whichever registration happened to come first.</para>
    /// </summary>
    IReadOnlyList<string> Kinds { get; }

    /// <summary>Ask the surface which tools it offers. A fact about the surface, not a permission.</summary>
    ValueTask<IReadOnlyList<string>> ListToolsAsync(IntegrationProperties integration, CancellationToken ct);

    /// <summary>
    /// Invoke one tool. Called ONLY after the tool was found declared and the policy pipeline allowed it —
    /// the caller is not a second gatekeeper and must not behave like one.
    /// </summary>
    ValueTask<IntegrationCallResult> CallAsync(
        IntegrationProperties integration,
        string tool,
        JsonNode input,
        CancellationToken ct);
}

public static class IntegrationConstants
{
    /// <summary>The namespace a portal, a CLI and a policy read.</summary>
    public const string Namespace = "Burbuja.Integrations";

    public static string ResourceType(string type) => $"{Namespace}/{type}";

    /// <summary>An integration's own event stream, keyed by the resource's canonical id.</summary>
    public static string StreamId(string canonicalId) => "integrations.resource/" + canonicalId.ToLowerInvariant();

    /// <summary>The mailbox's kind, and the one <c>GraphMailCaller</c> serves.</summary>
    public const string KindMail = "mail";

    /// <summary>The browser's kind, and the one <c>BrowserBridgeCaller</c> serves.</summary>
    public const string KindBrowser = "browser";

    /// <summary>A generic MCP server's kind.</summary>
    public const string KindMcp = "mcp";

    /// <summary>A Notion workspace's kind.</summary>
    public const string KindNotion = "notion";

    /// <summary>The kinds of surface the plan names. Data, so a descriptor and a test can enumerate them.</summary>
    public static readonly IReadOnlyList<string> Kinds = [KindMcp, KindMail, KindBrowser, KindNotion];
}
