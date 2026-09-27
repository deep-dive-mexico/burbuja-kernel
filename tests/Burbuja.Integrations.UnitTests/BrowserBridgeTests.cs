using System.Text.Json.Nodes;
using Burbuja.Plugins.Policy.Core;
using Burbuja.TestKit;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Burbuja.Integrations.UnitTests;

/// <summary>
/// I2 — the browser bridge: a real MCP server over stdio, and the prefix that keeps the policy in charge.
///
/// <para><b>Why these facts spawn a process instead of calling a double.</b> I0 declared
/// <c>IntegrationTransport</c> with a mode, a command and arguments, and nothing implemented any of it: the
/// data model was there and the transport was not. The parts of a stdio transport that break are the parts a
/// double cannot reproduce — the spawn, the framing on the child's stdin, the read from its stdout, the exit
/// that arrives before the answer, the line that is not JSON, the deadline that passes. So the facts here run
/// <c>tools/Burbuja.FakeMcp</c>, which speaks the same JSON-RPC on the same two pipes a real bridge does, and
/// the switches it takes exist because the interesting cases are all failures.</para>
///
/// <para><b>The prefix is the contract.</b> The declared tools are the string the policy pipeline dispatches
/// on, and the browser rules key on <c>mcp__browser__*</c>. A bridge that returned the server's bare names
/// would make every declared tool classify as an unknown kind — which escalates, and would look like a
/// cautious policy rather than a broken integration. One fact asserts this class's prefix IS the
/// classifier's, which is the only way two constants in two assemblies stay honest.</para>
/// </summary>
public sealed class BrowserBridgeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_bridges_prefix_is_the_classifiers_own()
    {
        BrowserBridgeCaller.Prefix.ShouldBe(
            BrowserClassifier.Prefix,
            "the bridge exists so the browser rules finally see real requests; a prefix that drifted would " +
            "make every declared browser tool classify as an unknown kind");
    }

    // ---------------------------------------------------------------- the socket rule

    /// <summary>
    /// Only <c>&lt;pid&gt;.sock</c> counts, and only while that pid is running — the reference's
    /// <c>liveSockets(names, isAlive)</c> rule, which is pure there for the same reason it is pure here.
    /// </summary>
    [Fact]
    public void Only_a_socket_whose_process_is_alive_counts_as_a_bridge()
    {
        var names = new[] { "4242.sock", "7.sock", "notes.txt", "abc.sock", ".sock", "13.sock.bak" };

        BrowserBridgeHealth.LivePids(names, pid => pid == 4242).ShouldBe([4242]);
        // `13.sock.bak` is not a socket either: the name has to END in .sock.
        BrowserBridgeHealth.LivePids(names, _ => true).ShouldBe([4242, 7], ignoreOrder: true);
        BrowserBridgeHealth.LivePids(names, _ => false).ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- the transport, over a real child

    /// <summary>
    /// A listing spawns the declared command, answers <c>initialize</c>, asks <c>tools/list</c> and returns the
    /// server's tools in the POLICY's spelling — the whole transport, end to end, against a real process.
    /// </summary>
    [Fact]
    public async Task A_listing_returns_the_servers_tools_spelled_the_way_the_policy_spells_them()
    {
        using var bridge = BridgeDirectory.Live();
        var caller = Caller(bridge);

        var tools = await caller.ListToolsAsync(Integration(bridge, Fake("--tools", "echo,read_page")), Ct);

        tools.ShouldBe(["mcp__browser__echo", "mcp__browser__read_page"]);
    }

    /// <summary>
    /// A tool call arrives at the server under its BARE name with its arguments intact — asserted from the
    /// server's own echo, so the fact is about what crossed the pipe rather than about what the caller
    /// believed it sent.
    /// </summary>
    [Fact]
    public async Task A_call_sends_the_bare_tool_name_and_the_arguments_that_were_given()
    {
        using var bridge = BridgeDirectory.Live();
        var caller = Caller(bridge);

        var result = await caller.CallAsync(
            Integration(bridge, Fake("--tools", "echo")),
            "mcp__browser__echo",
            new JsonObject { ["x"] = 1, ["q"] = "hola" },
            Ct);

        result.Ok.ShouldBeTrue(result.Error);
        result.Output!["tool"]!.GetValue<string>().ShouldBe("echo", "the server knows the bare name, not the policy's");
        result.Output["text"]!.GetValue<string>().ShouldContain("\"q\":\"hola\"");
        result.Output["truncated"]!.GetValue<bool>().ShouldBeFalse();
    }

    /// <summary>
    /// The permission mode reaches the child. The fake answers the <c>env</c> tool with the variable, so this
    /// is observed rather than asserted from the options object the caller was built with.
    /// </summary>
    [Fact]
    public async Task The_child_is_started_with_the_configured_permission_mode()
    {
        using var bridge = BridgeDirectory.Live();
        var caller = Caller(bridge);

        var result = await caller.CallAsync(
            Integration(bridge, Fake("--tools", "env")),
            "mcp__browser__env",
            new JsonObject(),
            Ct);

        result.Ok.ShouldBeTrue(result.Error);
        result.Output!["text"]!.GetValue<string>().ShouldBe("skip_all_permission_checks");
    }

    /// <summary>A page read is clamped, and the clamp says so — a browser can return megabytes of text.</summary>
    [Fact]
    public async Task A_large_answer_is_clamped_and_the_clamp_is_named()
    {
        using var bridge = BridgeDirectory.Live();
        var caller = Caller(bridge);

        var result = await caller.CallAsync(
            Integration(bridge, Fake("--tools", "echo")),
            "mcp__browser__echo",
            new JsonObject { ["blob"] = new string('x', 40_000) },
            Ct);

        result.Ok.ShouldBeTrue(result.Error);
        result.Output!["truncated"]!.GetValue<bool>().ShouldBeTrue();
        result.Output["text"]!.GetValue<string>().Length.ShouldBe(20_000);
    }

    // ---------------------------------------------------------------- the failures, each with its own sentence

    /// <summary>
    /// No live socket is reported BEFORE anything is spawned, and the refusal names the page that wakes the
    /// extension.
    ///
    /// <para>The attribution is the whole point: the transport in this fact points at a command that does not
    /// exist, so if the socket check did not come first the message would be about the missing binary. The
    /// reference implementation wrote a helper for exactly this — a bridge whose extension is asleep answers
    /// nothing, whatever the server does.</para>
    /// </summary>
    [Fact]
    public async Task No_live_bridge_socket_is_reported_before_the_server_is_even_started()
    {
        using var bridge = BridgeDirectory.Empty();
        var caller = Caller(bridge);

        var error = await Should.ThrowAsync<McpTransportException>(
            async () => await caller.ListToolsAsync(Integration(bridge, Fake()), Ct));

        error.Message.ShouldContain("no live bridge socket");
        error.Message.ShouldContain(bridge.Path);
        error.Message.ShouldContain("https://clau.de/chrome/reconnect");
        error.Message.ShouldNotContain("could not be started");
    }

    /// <summary>A command that is not there is named, along with the arguments that were asked for.</summary>
    [Fact]
    public async Task A_command_that_cannot_be_started_names_the_command()
    {
        using var bridge = BridgeDirectory.Live();
        var caller = Caller(bridge);

        var error = await Should.ThrowAsync<McpTransportException>(async () => await caller.ListToolsAsync(
            Integration(bridge, new IntegrationTransport(Mode: "stdio", Command: "/nonexistent/bridge", Args: ["--x"])),
            Ct));

        error.Message.ShouldContain("/nonexistent/bridge");
        error.Message.ShouldContain("--x");
    }

    /// <summary>A server that exits before answering reports the exit code and quotes its stderr.</summary>
    [Fact]
    public async Task A_server_that_exits_early_reports_the_exit_and_its_stderr()
    {
        using var bridge = BridgeDirectory.Live();
        var caller = Caller(bridge);

        var error = await Should.ThrowAsync<McpTransportException>(async () => await caller.ListToolsAsync(
            Integration(bridge, Fake("--stderr", "no browser is connected", "--fail")),
            Ct));

        error.Message.ShouldContain("code 3");
        error.Message.ShouldContain("no browser is connected");
    }

    /// <summary>
    /// A line that is not JSON is reported as such, with the line — the shape of a bridge that writes a banner
    /// or a log to stdout.
    /// </summary>
    [Fact]
    public async Task A_line_that_is_not_json_is_reported_with_the_line()
    {
        using var bridge = BridgeDirectory.Live();
        var caller = Caller(bridge);

        var error = await Should.ThrowAsync<McpTransportException>(async () => await caller.ListToolsAsync(
            Integration(bridge, Fake("--garbage")),
            Ct));

        error.Message.ShouldContain("not JSON");
        error.Message.ShouldContain("this line is not JSON");
    }

    /// <summary>
    /// A bridge that never answers stops at the deadline and says so. Without this the transport would hang
    /// until something else killed the request, which is the failure the reference implementation's own
    /// "wake the extension" helper exists to avoid.
    /// </summary>
    [Fact]
    public async Task A_bridge_that_never_answers_stops_at_the_deadline()
    {
        using var bridge = BridgeDirectory.Live();
        var caller = Caller(bridge, timeout: TimeSpan.FromSeconds(1));

        var started = DateTimeOffset.UtcNow;
        var error = await Should.ThrowAsync<McpTransportException>(async () => await caller.ListToolsAsync(
            Integration(bridge, Fake("--hang")),
            Ct));

        error.Message.ShouldContain("did not answer initialize within 1s");
        (DateTimeOffset.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(20));
    }

    /// <summary>A browser integration with no command says which declaration is missing.</summary>
    [Fact]
    public async Task A_transport_with_no_command_is_refused_by_name()
    {
        using var bridge = BridgeDirectory.Live();
        var caller = Caller(bridge);

        var error = await Should.ThrowAsync<McpTransportException>(
            async () => await caller.ListToolsAsync(Integration(bridge, new IntegrationTransport(Mode: "stdio")), Ct));

        error.Message.ShouldContain("transport.command");
        error.Message.ShouldContain("claude --claude-in-chrome-mcp");
    }

    /// <summary>
    /// A tool that is not a browser tool is refused by the bridge rather than sent to the server, and the
    /// refusal shows the spelling the rules are written against.
    /// </summary>
    [Fact]
    public async Task A_tool_outside_the_prefix_is_refused_before_the_server_sees_it()
    {
        using var bridge = BridgeDirectory.Live();
        var caller = Caller(bridge);

        var result = await caller.CallAsync(Integration(bridge, Fake()), "outlook_send", new JsonObject(), Ct);

        result.Ok.ShouldBeFalse();
        result.Error!.ShouldContain("mcp__browser__");
        result.Error!.ShouldContain("outlook_send");
    }

    /// <summary>
    /// A failed CALL carries the bridge's state, because that is the fact that explains the commonest cause:
    /// zero live sockets reads as "the extension is asleep, open this page", and a live one reads as "the
    /// bridge is there, so the failure is inside it".
    /// </summary>
    [Fact]
    public async Task A_failed_call_carries_the_bridge_state_and_the_reconnect_page()
    {
        using var asleep = BridgeDirectory.Empty();
        var caller = Caller(asleep, requireLiveBridge: false);

        var asleepResult = await caller.CallAsync(
            Integration(asleep, Fake("--stderr", "boom", "--fail")),
            "mcp__browser__echo",
            new JsonObject(),
            Ct);

        asleepResult.Ok.ShouldBeFalse();
        asleepResult.Error!.ShouldContain("no live bridge socket");
        asleepResult.Error!.ShouldContain("https://clau.de/chrome/reconnect");
        asleepResult.Error!.ShouldContain("boom", customMessage: "the bridge's own words come first");

        using var awake = BridgeDirectory.Live();
        var awakeCaller = Caller(awake, requireLiveBridge: false);
        var awakeResult = await awakeCaller.CallAsync(
            Integration(awake, Fake("--stderr", "boom", "--fail")),
            "mcp__browser__echo",
            new JsonObject(),
            Ct);

        awakeResult.Ok.ShouldBeFalse();
        awakeResult.Error!.ShouldContain("1 live bridge socket(s)");
        awakeResult.Error!.ShouldNotContain("https://clau.de/chrome/reconnect");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// A browser caller over a fake bridge directory and the fake MCP server.
    ///
    /// <para>Nothing here is registered by DI: the options object is the configuration the host would bind,
    /// and the transport rides on the integration the way a resource declares it.</para>
    /// </summary>
    private static BrowserBridgeCaller Caller(
        BridgeDirectory bridge,
        TimeSpan? timeout = null,
        bool requireLiveBridge = true)
    {
        var options = new BrowserBridgeOptions
        {
            BridgeDirectoryTemplate = bridge.Path,
            RequireLiveBridge = requireLiveBridge,
        };
        var mcp = new McpStdioClient(Options.Create(new McpClientOptions
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
        }));
        return new BrowserBridgeCaller(Options.Create(options), mcp);
    }

    private static IntegrationProperties Integration(BridgeDirectory bridge, IntegrationTransport transport) =>
        new(
            Kind: IntegrationConstants.KindBrowser,
            Transport: transport,
            Tools: ["mcp__browser__echo", "mcp__browser__read_page", "mcp__browser__navigate", "mcp__browser__env"],
            DevHosts: ["127.0.0.1", "localhost"],
            Enabled: true);

    /// <summary>The fake MCP server, spawned the way a real one is: an executable and its arguments.</summary>
    private static IntegrationTransport Fake(params string[] args) => new(
        Mode: "stdio",
        Command: "dotnet",
        Args: ["exec", FakeMcpAssembly, .. args]);

    /// <summary>
    /// The fake server's built assembly. Located through the same helper the plugin stager uses — the newest
    /// <c>bin/**/net10.0</c> directory — so the fact runs against whatever configuration the lane built.
    /// </summary>
    private static string FakeMcpAssembly { get; } =
        Path.Combine(
            PluginStager.FindBuiltDirectory(Path.Combine(RepoLayout.Root, "tools", "Burbuja.FakeMcp"))
                ?? throw new InvalidOperationException("tools/Burbuja.FakeMcp has no net10.0 build output"),
            "Burbuja.FakeMcp.dll");

    /// <summary>
    /// A bridge directory under the test's own temp path. <see cref="Live"/> writes a
    /// <c>&lt;pid&gt;.sock</c> for a process that is certainly running (this one), which is what the liveness
    /// rule reads; <see cref="Empty"/> is a directory with nothing in it.
    /// </summary>
    private sealed class BridgeDirectory : IDisposable
    {
        private BridgeDirectory(string path) => Path = path;

        public string Path { get; }

        public static BridgeDirectory Live()
        {
            var directory = Create();
            File.WriteAllText(
                System.IO.Path.Combine(directory.Path, $"{Environment.ProcessId}.sock"),
                "");
            return directory;
        }

        public static BridgeDirectory Empty() => Create();

        private static BridgeDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "burbuja-bridge-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new BridgeDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover temp directory is not a failed fact.
            }
        }
    }
}
