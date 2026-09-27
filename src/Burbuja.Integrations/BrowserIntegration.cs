using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Burbuja.Integrations;

/// <summary>How the browser bridge is found, woken, and allowed to run.</summary>
public sealed class BrowserBridgeOptions
{
    public const string Section = "Integrations:Browser";

    /// <summary>
    /// Where Chrome's native host puts its socket. The reference implementation's path, and a template
    /// rather than a literal because the socket directory carries the user name:
    /// <c>/tmp/claude-mcp-browser-bridge-&lt;user&gt;</c>.
    /// </summary>
    public string BridgeDirectoryTemplate { get; set; } = "/tmp/claude-mcp-browser-bridge-{user}";

    /// <summary>
    /// The page that wakes an extension which is installed but not connected. Nightshift's rule is that the
    /// bridge does not exist until somebody opens this in Chrome, and a bridge that is asleep does not answer
    /// at all — so the refusal names it rather than leaving a caller to guess.
    /// </summary>
    public string ReconnectUrl { get; set; } = "https://clau.de/chrome/reconnect";

    /// <summary>
    /// Whether a probe requires a live socket before it will ask the server anything.
    ///
    /// <para>True by default, because it is the failure the reference implementation had to write a helper
    /// for: the MCP server starts happily with no extension attached and every call then fails or hangs, so
    /// "no socket" is the one fact worth reporting before anything is spawned.</para>
    /// </summary>
    public bool RequireLiveBridge { get; set; } = true;

    /// <summary>
    /// The value of <c>CLAUDE_CHROME_PERMISSION_MODE</c> for the spawned bridge.
    ///
    /// <para>Ported from the reference, and the reason is the same one: the extension's own side-panel prompt
    /// has nobody to click it, and this host's gate plus its supervisor already decide every browser call
    /// before the bridge is reached — an unanswerable second prompt is not a safety property, it is a hang.
    /// It lives in CONFIGURATION and not in the resource on purpose: a resource property that set a child
    /// process's environment would let a declaration change how the command behaves rather than what it is,
    /// which is a door this resource should not have.</para>
    /// </summary>
    public string PermissionMode { get; set; } = "skip_all_permission_checks";
}

/// <summary>What the bridge looks like from here, as a fact rather than a boolean.</summary>
/// <param name="Directory">The directory that was looked in.</param>
/// <param name="Sockets">How many <c>&lt;pid&gt;.sock</c> entries it holds, live or not.</param>
/// <param name="Live">How many of them belong to a process that is still running.</param>
public sealed record BrowserBridgeState(string Directory, int Sockets, int Live)
{
    public string Describe() => Live > 0
        ? $"{Live} live bridge socket(s) in '{Directory}'"
        : $"no live bridge socket in '{Directory}'";
}

/// <summary>
/// The socket liveness rule, lifted from the reference's <c>chrome.ts</c> — <c>liveSockets</c> takes the
/// aliveness predicate as a parameter there and for a good reason: the rule (only <c>&lt;pid&gt;.sock</c>
/// names count, and only if that pid is running) is worth testing without a Chrome on the machine.
/// </summary>
public static class BrowserBridgeHealth
{
    public static string Directory(BrowserBridgeOptions options)
    {
        var user = Environment.UserName;
        if (string.IsNullOrWhiteSpace(user))
            user = Environment.GetEnvironmentVariable("USER") ?? "default";
        return options.BridgeDirectoryTemplate.Replace("{user}", user, StringComparison.Ordinal);
    }

    /// <summary>A socket file is <c>&lt;pid&gt;.sock</c> and its process is alive. Anything else is ignored.</summary>
    public static IReadOnlyList<int> LivePids(IEnumerable<string> names, Func<int, bool> isAlive) =>
        [.. names
            .Select(n => Path.GetFileName(n))
            .Select(n => n.Length > 5 && n.EndsWith(".sock", StringComparison.Ordinal)
                ? n[..^5]
                : null)
            .Where(stem => stem is not null && stem.All(char.IsAsciiDigit))
            .Select(stem => int.Parse(stem!, System.Globalization.CultureInfo.InvariantCulture))
            .Where(isAlive)];

    public static BrowserBridgeState Snapshot(BrowserBridgeOptions options, Func<int, bool>? isAlive = null)
    {
        var directory = Directory(options);
        string[] names;
        try
        {
            names = System.IO.Directory.Exists(directory)
                ? [.. System.IO.Directory.GetFiles(directory, "*.sock").Select(Path.GetFileName).Where(n => n is not null).Select(n => n!)]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new BrowserBridgeState(directory, 0, 0);
        }

        var live = LivePids(names, isAlive ?? IsAlive);
        return new BrowserBridgeState(directory, names.Length, live.Count);
    }

    /// <summary>The reference's <c>process.kill(pid, 0)</c>: does the process exist?</summary>
    public static bool IsAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or SystemException)
        {
            return false;
        }
    }
}

/// <summary>
/// I2: the browser, as an instance of the integration abstraction.
///
/// <para><b>What the previous milestone left.</b> I0 made a surface a resource and I1 gave the mailbox a
/// caller; the browser had a classifier that judges <c>mcp__browser__*</c> perfectly and no bridge to judge,
/// so its rules had never seen a real request through this host either. This is the bridge: the declared
/// command is spawned over stdio, the server's own tool names are returned in the POLICY's spelling —
/// <c>mcp__browser__navigate</c> — and a call is mapped back to the bare name the server knows.</para>
///
/// <para><b>Why the prefix mapping is the caller's job and not the operator's.</b> The declared tools are
/// what the policy pipeline dispatches on, and the pipeline's vocabulary is fixed by the classifier that
/// judges it: a resource that declared the server's bare names would have every call classified as an
/// unknown kind, which escalates and looks like a policy that is working. Translating in one place means the
/// two spellings cannot drift — and a fact asserts this prefix is the classifier's own.</para>
///
/// <para><b>The bridge is not a second gatekeeper.</b> It refuses only what it cannot DO: a tool that is not
/// a browser tool, a transport that names no command, a bridge with no live socket. Everything about whether
/// a call is ALLOWED was decided before this class was reached.</para>
/// </summary>
public sealed class BrowserBridgeCaller(
    IOptions<BrowserBridgeOptions> options,
    McpStdioClient? mcp = null) : IIntegrationCaller
{
    /// <summary>The policy vocabulary for a browser tool, and the same string <c>BrowserClassifier</c> keys on.</summary>
    public const string Prefix = "mcp__browser__";

    private readonly BrowserBridgeOptions _options = options.Value;
    private readonly McpStdioClient _mcp = mcp ?? new McpStdioClient();

    public IReadOnlyList<string> Kinds { get; } = [IntegrationConstants.KindBrowser];

    /// <summary>
    /// What the browser offers, spelled the way the policy spells it.
    ///
    /// <para>The socket check comes first and is not a formality: the MCP server starts whether or not the
    /// extension is connected, so a probe that only asked the server would report a healthy surface that
    /// cannot perform a single call. Reporting "no live bridge socket" and naming the page that wakes it is
    /// the difference between an operator fixing this in ten seconds and reading a hang.</para>
    /// </summary>
    public async ValueTask<IReadOnlyList<string>> ListToolsAsync(IntegrationProperties integration, CancellationToken ct)
    {
        var transport = Require(integration);
        if (_options.RequireLiveBridge)
        {
            var state = BrowserBridgeHealth.Snapshot(_options);
            if (state.Live == 0)
            {
                throw new McpTransportException(
                    $"There is {state.Describe()}, so no browser tool can run. Open {_options.ReconnectUrl} in "
                    + "Chrome to wake the extension, then probe again.");
            }
        }

        var tools = await _mcp.ListToolsAsync(transport, Environment(), ct).ConfigureAwait(false);
        return [.. tools.Select(t => Prefix + t)];
    }

    public async ValueTask<IntegrationCallResult> CallAsync(
        IntegrationProperties integration,
        string tool,
        JsonNode input,
        CancellationToken ct)
    {
        var transport = Require(integration);
        if (!tool.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return new IntegrationCallResult(false, null,
                $"'{tool}' is not a browser tool. This bridge serves tools spelled '{Prefix}<tool>', which is "
                + "the name the browser rules are written against.");
        }

        var result = await _mcp
            .CallAsync(transport, Environment(), tool[Prefix.Length..], input, ct)
            .ConfigureAwait(false);

        if (result.Ok)
            return result;

        // The bridge's own words first, then the one fact that explains the commonest cause.
        var state = BrowserBridgeHealth.Snapshot(_options);
        return result with
        {
            Error = $"{result.Error} The bridge reports {state.Describe()}"
                + (state.Live == 0 ? $"; open {_options.ReconnectUrl} in Chrome to wake the extension." : "."),
        };
    }

    /// <summary>The transport, or the reason there is none. A browser integration always names its command.</summary>
    private static IntegrationTransport Require(IntegrationProperties integration)
    {
        if (integration.Transport is not { } transport || string.IsNullOrWhiteSpace(transport.Command))
        {
            throw new McpTransportException(
                "This browser integration declares no transport.command, so there is no bridge to start. "
                + "The reference invocation is 'claude --claude-in-chrome-mcp'.");
        }

        return transport;
    }

    /// <summary>
    /// The environment the bridge is started with. Only the permission mode: everything else the child needs
    /// is the child's own business, and a caller-supplied environment would be a way to change what a
    /// declared command does without changing the declaration.
    /// </summary>
    private Dictionary<string, string> Environment() => new(StringComparer.Ordinal)
    {
        ["CLAUDE_CHROME_PERMISSION_MODE"] = _options.PermissionMode,
    };
}
