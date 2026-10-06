// Adapted from burbuja-server 97940d9; the fixture here is the independent generic kernel host.
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Burbuja.TestKit;
using Shouldly;
using Xunit;

namespace Burbuja.E2E.Tests;

/// <summary>
/// Group F: the kernel MCP lane, black-box against the published Host. The caller is a plain HTTP
/// client speaking JSON-RPC to <c>/mcp</c> — which is what a real MCP client does — because the
/// point of the lane is that the kernel's own gate decides, whoever is calling.
/// </summary>
[Trait("Category", "E2E")]
public sealed class McpE2ETests(SharedServerFixture server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string AgentsRoot = "/subscriptions/local/resourceGroups/dev/providers/Burbuja.Agents";

    [Fact]
    public async Task F1_tools_list_returns_the_kernels_own_tools()
    {
        using var client = server.Client();
        var agent = server.NewName("mcp");
        await PutAgentAsync(client, agent, workRoot: null, scenario: null, driver: "external");

        using var mcp = server.Mcp(agent);
        var result = await mcp.CallAsync("tools/list", new JsonObject(), agent, Ct);

        var tools = result!["tools"]!.AsArray();
        var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToList();
        names.ShouldContain("write_file");
        names.ShouldContain("read_file");
        names.ShouldContain("bash");
        names.ShouldContain("send_message");
        foreach (var tool in tools)
        {
            tool!["description"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
            tool["inputSchema"].ShouldNotBeNull();
        }

        // An agent that does not exist gets no catalogue: the filter is a fact about an agent.
        var unknown = await mcp.CallAsync("tools/list", new JsonObject(), server.NewName("ghost"), Ct);
        unknown!["tools"]!.AsArray().Count.ShouldBe(0);
    }

    [Fact]
    public async Task F2_a_gated_write_runs_on_the_agents_own_session_stream()
    {
        using var client = server.Client();
        var workRoot = NewWorkRoot();
        try
        {
            var agent = server.NewName("mcp");
            await PutAgentAsync(client, agent, workRoot, scenario: null, driver: "external");

            using var mcp = server.Mcp(agent);
            var result = await mcp.CallAsync(
                "tools/call",
                new JsonObject
                {
                    ["name"] = "write_file",
                    ["arguments"] = new JsonObject { ["path"] = "from-mcp.txt", ["content"] = "written over mcp" },
                },
                agent,
                Ct);

            (result!["isError"]?.GetValue<bool>() ?? false).ShouldBeFalse(result.ToJsonString());
            if (!server.IsRemote)
                File.ReadAllText(Path.Combine(workRoot, "from-mcp.txt")).ShouldBe("written over mcp");

            // The audit trail is the kernel's: intent, then the verdict, then the call and its result.
            var lane = await SessionEventsAsync(client, agent);
            var types = lane.Select(e => e.Type).ToList();
            types.ShouldContain("agents.tool.intent", "the session lane is " + string.Join(',', types));
            types.ShouldContain("agents.decision");
            lane.First(e => e.Type == "agents.tool.intent").Seq
                .ShouldBeLessThan(lane.First(e => e.Type == "agents.decision").Seq);
            lane.First(e => e.Type == "agents.decision").Seq
                .ShouldBeLessThan(lane.First(e => e.Type == "agents.tool.call").Seq);
            lane.First(e => e.Type == "agents.tool.call").Seq
                .ShouldBeLessThan(lane.First(e => e.Type == "agents.tool.result").Seq);
        }
        finally
        {
            Drop(workRoot);
        }
    }

    [Fact]
    public async Task F3_a_hard_denied_command_never_runs()
    {
        using var client = server.Client();
        var workRoot = NewWorkRoot();
        var sentinel = Path.Combine(workRoot, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "still here", Ct);
        try
        {
            var agent = server.NewName("mcp");
            await PutAgentAsync(client, agent, workRoot, scenario: null, driver: "external");

            using var mcp = server.Mcp(agent);
            var result = await mcp.CallAsync(
                "tools/call",
                new JsonObject
                {
                    ["name"] = "bash",
                    ["arguments"] = new JsonObject { ["command"] = "rm -rf /" },
                },
                agent,
                Ct);

            result!["isError"]!.GetValue<bool>().ShouldBeTrue();
            var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
            text.ShouldContain("Burbuja blocked");
            File.Exists(sentinel).ShouldBeTrue("a hard deny must never reach the shell");

            var lane = await SessionEventsAsync(client, agent);
            var denied = lane.First(e => e.Type == "agents.tool.denied");
            denied.Data!["class"]!.GetValue<string>().ShouldBe("destructive_fs");
            lane.ShouldNotContain(e => e.Type == "agents.tool.call", "the denied command never ran");
        }
        finally
        {
            Drop(workRoot);
        }
    }

    [Fact]
    public async Task F4_a_resource_read_is_redacted()
    {
        using var client = server.Client();
        var agent = server.NewName("mcp");
        await PutAgentAsync(client, agent, workRoot: null, scenario: null, driver: "external");

        const string secret = "sk-abcdefghijklmnopqrstuvwxyz0123456789";
        using (var card = await client.PutAsync(
                   $"{AgentsRoot}/agents/{agent}/contexts/handoff",
                   "{\"properties\":{\"label\":\"handoff\",\"body\":\"the key is " + secret + "\"}}",
                   Ct))
        {
            card.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
        }

        using var mcp = server.Mcp(agent);
        var result = await mcp.CallAsync(
            "resources/read",
            new JsonObject { ["uri"] = $"burbuja://agents/{agent}/contexts" },
            agent,
            Ct);

        var text = result!["contents"]!.AsArray()[0]!["text"]!.GetValue<string>();
        text.Contains(secret, StringComparison.Ordinal)
            .ShouldBeFalse("the redactor is the only way state leaves the kernel");
        text.ShouldContain("handoff");
    }

    [Fact]
    public async Task F5_an_escalated_call_returns_pending_then_runs_after_approval()
    {
        using var client = server.Client();
        var agent = server.NewName("mcp");
        await PutAgentAsync(client, agent, workRoot: null, scenario: "gray-supervisor-escalate-approved", driver: "external");

        using var mcp = server.Mcp(agent);
        var arguments = new JsonObject { ["command"] = "unknowncmd --escalate" };
        var started = DateTimeOffset.UtcNow;
        var pending = await mcp.CallAsync(
            "tools/call",
            new JsonObject { ["name"] = "bash", ["arguments"] = arguments.DeepClone() },
            agent,
            Ct);
        var elapsed = DateTimeOffset.UtcNow - started;

        pending!["isError"]!.GetValue<bool>().ShouldBeTrue();
        var approvalId = pending["structuredContent"]!["approvalId"]!.GetValue<string>();
        approvalId.ShouldNotBeNullOrEmpty();
        pending["structuredContent"]!["code"]!.GetValue<string>().ShouldBe("approval_pending");
        elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5), "an MCP call is answered, never pinned on an approval");

        using (var approve = await client.PostAsync(
                   $"/subscriptions/local/providers/Burbuja.Resources/approvals/{approvalId}/approve",
                   """{"note":"e2e"}""",
                   Ct))
        {
            approve.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // pomni-style retry: the same call, carrying the approval it was handed.
        arguments["approvalId"] = approvalId;
        var retry = await mcp.CallAsync(
            "tools/call",
            new JsonObject { ["name"] = "bash", ["arguments"] = arguments.DeepClone() },
            agent,
            Ct);

        retry!["structuredContent"].ShouldBeNull("the second attempt is a real call, not another pending answer");
        var lane = await SessionEventsAsync(client, agent);
        lane.Count(e => e.Type == "agents.tool.call").ShouldBeGreaterThanOrEqualTo(1, "the approved command ran");
        lane.ShouldContain(e => e.Type == "agents.approval");
    }

    [Fact]
    public async Task F6_a_reader_key_is_refused_before_any_tool_logic()
    {
        using var client = server.Client();
        var agent = server.NewName("mcp");
        await PutAgentAsync(client, agent, workRoot: null, scenario: null, driver: "external");

        using var mcp = server.Mcp(agent, SharedServerFixture.ReaderKey);
        using var response = await mcp.PostAsync(
            "tools/call",
            new JsonObject { ["name"] = "write_file", ["arguments"] = new JsonObject { ["path"] = "x", ["content"] = "y" } },
            Ct);

        // The Reader role has no agent tool action, so the call is refused — and the refusal names
        // the authorization, not some incidental "error" substring.
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Forbidden);
        body.Contains("does not have authorization", StringComparison.OrdinalIgnoreCase)
            .ShouldBeTrue($"{response.StatusCode}: {body}");
    }

    [Fact]
    public async Task F7_the_mcp_endpoint_requires_a_key()
    {
        using var anonymous = server.Mcp(server.NewName("mcp"), apiKey: "not-a-key");
        // No handshake on purpose: authentication runs before the transport, so a bad key is refused even on
        // a request that names no session — and connecting first would make this fact fail inside the client's
        // own handshake assertion rather than on the thing it is about.
        using var response = await anonymous.PostWithoutSessionAsync("tools/list", new JsonObject(), anonymous.AgentName, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldContain("ApiKey");
    }

    /// <summary>
    /// K1: the endpoint tracks a session, and the handshake is what mints one.
    ///
    /// <para><b>This is the fact that says the lane is stateful.</b> Before K1 the endpoint answered every
    /// request on its own and ignored <c>Mcp-Session-Id</c> entirely, so a client could not be told anything
    /// between requests and a call could not be traced to the connection that made it. The negative half
    /// matters as much as the positive: a request that names NO session is refused, which is what makes the
    /// header a requirement rather than a decoration.</para>
    /// </summary>
    [Fact]
    public async Task F8_initialize_mints_a_session_and_a_request_without_one_is_refused()
    {
        using var client = server.Client();
        var agent = server.NewName("mcp");
        await PutAgentAsync(client, agent, workRoot: null, scenario: null, driver: "external");

        using var mcp = server.Mcp(agent);
        var sessionId = await mcp.ConnectAsync(Ct);
        sessionId.ShouldNotBeNullOrEmpty("initialize must mint a session id");
        sessionId!.Length.ShouldBeGreaterThan(8);

        // The session works: the catalogue comes back on it.
        var tools = await mcp.CallAsync("tools/list", new JsonObject(), agent, Ct);
        tools!["tools"]!.AsArray().Count.ShouldBeGreaterThan(0);

        // And naming none is refused: the endpoint is stateful, and this is the observable difference.
        using (var orphan = await mcp.PostWithoutSessionAsync("tools/list", new JsonObject(), agent, Ct))
        {
            orphan.StatusCode.ShouldNotBe(HttpStatusCode.OK);
            (await orphan.Content.ReadAsStringAsync(Ct)).ShouldNotContain("read_file");
        }

        // The protocol's own way to end a session, and afterwards the id is unknown to the server.
        using (var deleted = await mcp.DeleteSessionAsync(agent, Ct))
        {
            deleted.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent, HttpStatusCode.Accepted);
        }

        using var afterDelete = await mcp.PostWithoutSessionAsync("tools/list", new JsonObject(), agent, Ct);
        afterDelete.StatusCode.ShouldNotBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// K1: a connected client is TOLD when its tool catalogue changes.
    ///
    /// <para><b>The producer this milestone exists for.</b> An agent's catalogue is filtered by its
    /// <c>toolFilter</c>, so an ordinary PUT can change what a connected client may call with no error
    /// anywhere — the client keeps calling a list it was shown once. The fact drives the whole path: open the
    /// session's server-to-client stream, narrow the filter through ARM, list again, and require
    /// <c>notifications/tools/list_changed</c> to arrive on that stream.</para>
    /// </summary>
    [Fact]
    public async Task F9_a_changed_catalogue_reaches_the_session_as_a_list_changed_notification()
    {
        using var client = server.Client();
        var workRoot = NewWorkRoot();
        try
        {
            var agent = server.NewName("mcp");
            await PutAgentWithToolsAsync(client, agent, ["bash"], workRoot);

            using var mcp = server.Mcp(agent);
            await mcp.ConnectAsync(Ct);

            var before = await mcp.CallAsync("tools/list", new JsonObject(), agent, Ct);
            var names = before!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
            names.ShouldContain("bash");
            names.ShouldNotContain("read_file", "the filter is what makes the catalogue changeable");

            // One real call, so the agent has a session: the catalogue fact is about a connection that is
            // being used, and the audit the second half reads lives on that session's stream.
            var ran = await mcp.CallAsync(
                "tools/call",
                new JsonObject { ["name"] = "bash", ["arguments"] = new JsonObject { ["command"] = "echo ok" } },
                agent,
                Ct);
            ran!["isError"]?.GetValue<bool>().ShouldNotBe(true, ran.ToJsonString());

            // The session's server-to-client stream is opened as well, because the transport allows a
            // notification on either it or the response stream. Measured: it arrives on the RESPONSE (the
            // session's GET stream stays silent unless the application runs the session loop itself), and the
            // fact accepts both so it is about the client being told and not about which pipe carried it.
            using var stream = await mcp.OpenStreamAsync(agent, Ct);
            stream.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync(Ct));

            // The change, through the front door: nothing about the session knows it is coming. It is a PUT
            // because this type answers 405 to PATCH (the capability is declared and the door is not open —
            // its own defect, named in the session log), and a PUT replaces the properties, which also
            // resets the agent's current-session pointer. Both consequences are asserted below: the
            // catalogue changed AND the notification was recorded on the session the agent now points at.
            await PutAgentWithToolsAsync(client, agent, ["read_file"], workRoot);

            // Any request on the session is where the comparison happens; a listing is the natural one. Its
            // RAW body is read because a notification may ride on the response stream, and a client that only
            // parsed the result would never notice.
            var raw = await mcp.PostRawAsync("tools/list", new JsonObject(), agent, Ct);
            var afterNames = ToolNamesFrom(raw);
            afterNames.ShouldContain("read_file");
            afterNames.ShouldNotContain("bash");

            // The ledger half first: the notification is not durable, so the record of having sent one is what
            // tells "nothing changed" apart from "the change was not delivered".
            var events = await CurrentSessionEventsAsync(client, agent);
            events.ShouldContain(
                e => e.Type == "mcp.tools.changed",
                $"the catalogue changed on a live session. Events seen: {string.Join(", ", events.Select(e => e.Type).Distinct())}");

            // Delivery, both ways the transport allows: on the POST's own stream, or on the session's
            // server-to-client stream. Either is the client being told; neither is the ledger claiming it was.
            var onResponse = raw.Contains("notifications/tools/list_changed", StringComparison.Ordinal);
            var (onStream, lines) = onResponse
                ? (false, new List<string>())
                : await ReadMethodAsync(reader, "notifications/tools/list_changed", TimeSpan.FromSeconds(15), Ct);

            (onResponse || onStream).ShouldBeTrue(
                "the session's catalogue moved; a client that is not told keeps working from a stale list. "
                + $"The response body was {raw.Length} char(s) and the stream produced {lines.Count} line(s): "
                + string.Join(" | ", lines.Take(10)));
        }
        finally
        {
            Drop(workRoot);
        }
    }

    /// <summary>
    /// Reads the session's SSE stream until the named method arrives, or the deadline passes. It returns what
    /// it saw as well as the verdict: "no notification" and "the stream was silent" are different failures,
    /// and a fact that cannot tell them apart sends the next reader hunting in the wrong place.
    /// </summary>
    /// <summary>
    /// The tool names out of whatever the response body carried: one JSON answer, or an SSE stream that may
    /// hold the answer AND a notification. Picking "the last data line" read the notification as the result —
    /// the answer is the message whose <c>result</c> has a <c>tools</c> array.
    /// </summary>
    private static List<string> ToolNamesFrom(string raw)
    {
        foreach (var line in raw.Split('\n'))
        {
            var payload = line.StartsWith("data:", StringComparison.Ordinal) ? line[5..].Trim() : line.Trim();
            if (payload.Length == 0 || !payload.StartsWith('{'))
                continue;
            if (JsonNode.Parse(payload)?["result"]?["tools"] is JsonArray tools)
                return [.. tools.Select(t => t!["name"]!.GetValue<string>())];
        }

        return [];
    }

    private static async Task<(bool Seen, List<string> Lines)> ReadMethodAsync(
        StreamReader reader,
        string method,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var lines = new List<string>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            while (!deadline.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(deadline.Token);
                if (line is null)
                    return (false, lines);
                if (line.Length == 0)
                    continue;
                lines.Add(line);
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                    continue;
                var payload = line[5..].Trim();
                if (payload.Length == 0 || !payload.StartsWith('{'))
                    continue;
                if (payload.Contains(method, StringComparison.Ordinal))
                    return (true, lines);
            }
        }
        catch (OperationCanceledException)
        {
            return (false, lines);
        }

        return (false, lines);
    }

    private static async Task PutAgentWithToolsAsync(ArmTestClient client, string agent, IReadOnlyList<string> tools, string workRoot)
    {
        var filter = string.Join(',', tools.Select(t => "\"" + t + "\""));
        using var put = await client.PutAsync(
            $"{AgentsRootPath}/agents/{agent}",
            "{\"properties\":{\"displayName\":\"" + agent + "\",\"driver\":\"external\","
            + "\"workspaceId\":\"" + workRoot.Replace("\\", "\\\\", StringComparison.Ordinal) + "\","
            + "\"toolFilter\":[" + filter + "]}}",
            Ct);
        put.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
    }

    // ---------------------------------------------------------------- helpers

    private static string AgentsRootPath => AgentsRoot;

    private static async Task PutAgentAsync(ArmTestClient client, string agent, string? workRoot, string? scenario, string driver)
    {
        var parts = new List<string> { "\"displayName\":\"" + agent + "\"", "\"driver\":\"" + driver + "\"" };
        if (scenario is not null)
            parts.Add("\"profile\":\"" + scenario + "\"");
        if (workRoot is not null)
            parts.Add("\"workspaceId\":\"" + workRoot.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"");
        using var put = await client.PutAsync(
            $"{AgentsRootPath}/agents/{agent}",
            "{\"properties\":{" + string.Join(',', parts) + "}}",
            Ct);
        put.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
    }

    private static async Task<List<SseEvent>> SessionEventsAsync(ArmTestClient client, string agent)
    {
        using var sessions = await client.GetAsync($"{AgentsRootPath}/agents/{agent}/sessions", Ct);
        var page = await ArmTestClient.ReadJsonAsync(sessions, Ct);
        var list = page!["value"]!.AsArray();
        list.Count.ShouldBeGreaterThanOrEqualTo(1, "an MCP call creates the agent's session");
        var path = $"{AgentsRootPath}/agents/{agent}/sessions/{list[0]!["name"]!.GetValue<string>()}/events";
        return await SseTail.CollectAsync(client, path, _ => false, TimeSpan.FromSeconds(3), ct: Ct);
    }

    /// <summary>
    /// The events of the session the AGENT POINTS AT, which is where a runtime fact about the current
    /// connection belongs. Reading "the first session in the list" was the shortcut that made this fact fail
    /// against correct code: a PUT resets the pointer, so the newest session is not the one the list happens
    /// to sort first.
    /// </summary>
    private static async Task<List<SseEvent>> CurrentSessionEventsAsync(ArmTestClient client, string agent)
    {
        using var read = await client.GetAsync($"{AgentsRootPath}/agents/{agent}", Ct);
        var body = await ArmTestClient.ReadJsonAsync(read, Ct);
        var current = body!["properties"]!["currentSessionId"]!.GetValue<string>();
        current.ShouldNotBeNullOrEmpty("an MCP connection creates the agent's session");

        var path = $"{AgentsRootPath}/agents/{agent}/sessions/{current}/events";
        return await SseTail.CollectAsync(client, path, _ => false, TimeSpan.FromSeconds(3), ct: Ct);
    }

    private static string NewWorkRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "burbuja-e2e-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Drop(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// A minimal MCP Streamable HTTP client: one JSON-RPC call per POST, with the two headers the lane
/// authenticates on. It accepts either framing — a JSON body or an SSE frame — because the spec
/// lets a server choose and the kernel answers JSON to a JSON-only client.
/// </summary>
internal sealed class McpTestClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string? _agent;
    private string? _sessionId;
    private string? _protocolVersion;
    private int _id;

    private McpTestClient(HttpClient http, string apiKey, string? agent)
    {
        _http = http;
        _apiKey = apiKey;
        _agent = agent;
    }

    public static McpTestClient Create(string baseUrl, string apiKey, string? agent)
    {
        var http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        return new McpTestClient(http, apiKey, agent);
    }

    public string? SessionId => _sessionId;

    public string? AgentName => _agent;

    public Task<HttpResponseMessage> PostAsync(string method, JsonObject parameters, CancellationToken ct) =>
        PostAsync(method, parameters, _agent, ct);

    /// <summary>
    /// One request, on this client's session.
    ///
    /// <para><b>K1: the handshake is the client's job now.</b> The endpoint tracks a session per client, so a
    /// request that names none is refused; every real client initialises first and echoes
    /// <c>Mcp-Session-Id</c> afterwards, and the lane does the same rather than being taught a shortcut the
    /// protocol does not have.</para>
    /// </summary>
    public async Task<HttpResponseMessage> PostAsync(string method, JsonObject parameters, string? agent, CancellationToken ct)
    {
        var session = method == "initialize" ? null : await ConnectAsync(agent, ct);
        return await SendAsync(method, parameters, agent, session, notification: false, ct);
    }

    /// <summary>initialize once per client, keep the id it is given, and send the notification that ends the
    /// handshake. Returns the session id, which is what a fact asserts is non-empty.</summary>
    public async Task<string?> ConnectAsync(CancellationToken ct) => await ConnectAsync(_agent, ct);

    public async Task<string?> ConnectAsync(string? agent, CancellationToken ct)
    {
        if (_sessionId is not null)
            return _sessionId;

        using (var init = await SendAsync(
                   "initialize",
                   new JsonObject
                   {
                       ["protocolVersion"] = "2025-11-25",
                       ["capabilities"] = new JsonObject(),
                       ["clientInfo"] = new JsonObject { ["name"] = "burbuja-e2e", ["version"] = "1.0" },
                   },
                   agent,
                   session: null,
                   notification: false,
                   ct))
        {
            init.StatusCode.ShouldBe(HttpStatusCode.OK, await init.Content.ReadAsStringAsync(ct));
            init.Headers.TryGetValues("Mcp-Session-Id", out var values)
                .ShouldBeTrue("initialize must mint an Mcp-Session-Id; without one the endpoint is stateless");
            _sessionId = values!.First();
            // The handshake comes back SSE-framed, so it is parsed the same way every other answer is.
            var handshake = Parse(await init.Content.ReadAsStringAsync(ct));
            _protocolVersion = handshake?["result"]?["protocolVersion"]?.GetValue<string>() ?? "2025-11-25";
        }

        using var note = await SendAsync("notifications/initialized", null, agent, _sessionId, notification: true, ct);
        note.StatusCode.ShouldBeOneOf(HttpStatusCode.Accepted, HttpStatusCode.OK);
        return _sessionId;
    }

    /// <summary>
    /// One request whose raw answer is returned. Used where the answer may carry more than the response to
    /// this request — a server-to-client notification can be multiplexed onto it, which is the other half of
    /// how the Streamable HTTP transport delivers one.
    /// </summary>
    public async Task<string> PostRawAsync(string method, JsonObject parameters, string? agent, CancellationToken ct)
    {
        var session = await ConnectAsync(agent, ct);
        using var response = await SendAsync(method, parameters, agent, session, notification: false, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>A request that deliberately names no session, for the fact about the refusal.</summary>
    public Task<HttpResponseMessage> PostWithoutSessionAsync(string method, JsonObject parameters, string? agent, CancellationToken ct) =>
        SendAsync(method, parameters, agent, session: null, notification: false, ct);

    /// <summary>Ends the session the way the protocol says: DELETE with the id.</summary>
    public async Task<HttpResponseMessage> DeleteSessionAsync(string? agent, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/mcp");
        request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        if (!string.IsNullOrEmpty(agent))
            request.Headers.TryAddWithoutValidation(Burbuja.Mcp.Abstractions.McpConstants.AgentHeader, agent);
        if (_sessionId is { Length: > 0 } id)
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", id);
        return await _http.SendAsync(request, ct);
    }

    /// <summary>
    /// The session's server-to-client stream (the GET half of Streamable HTTP). A <c>notifications/*</c>
    /// message MAY arrive here and may arrive on a response stream instead; the caller keeps this open and
    /// reads it line by line when the response did not carry one.
    /// </summary>
    public async Task<HttpResponseMessage> OpenStreamAsync(string? agent, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        if (!string.IsNullOrEmpty(agent))
            request.Headers.TryAddWithoutValidation(Burbuja.Mcp.Abstractions.McpConstants.AgentHeader, agent);
        if (_sessionId is { Length: > 0 } id)
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", id);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        // The revision both sides agreed at initialize. Subsequent requests are supposed to carry it, and the
        // Streamable HTTP transport uses it to decide how the connection is served.
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _protocolVersion ?? "2025-11-25");
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(
        string method,
        JsonObject? parameters,
        string? agent,
        string? session,
        bool notification,
        CancellationToken ct)
    {
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (!notification)
            body["id"] = Interlocked.Increment(ref _id);
        if (parameters is not null)
            body["params"] = parameters;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        if (!string.IsNullOrEmpty(agent))
            request.Headers.TryAddWithoutValidation(Burbuja.Mcp.Abstractions.McpConstants.AgentHeader, agent);
        if (session is { Length: > 0 })
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return await _http.SendAsync(request, ct);
    }

    /// <summary>Sends one request and returns the JSON-RPC <c>result</c>, failing loudly on an error.</summary>
    public async Task<JsonNode?> CallAsync(string method, JsonObject parameters, string? agent, CancellationToken ct)
    {
        using var response = await PostAsync(method, parameters, agent, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        var json = Parse(text);
        json.ShouldNotBeNull($"{response.StatusCode} {text}");
        if (json!["error"] is { } error)
            return error;
        return json["result"];
    }

    /// <summary>JSON-RPC out of either framing: a plain body, or the last <c>data:</c> line of an SSE one.</summary>
    public static JsonNode? Parse(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return null;
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return JsonNode.Parse(trimmed);
        // SSE framing: take the last data: line, which is the response to this request.
        JsonNode? last = null;
        foreach (var line in trimmed.Split('\n'))
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;
            var payload = line[5..].Trim();
            if (payload.Length > 0 && (payload.StartsWith('{') || payload.StartsWith('[')))
                last = JsonNode.Parse(payload);
        }

        return last;
    }

    public void Dispose() => _http.Dispose();
}

internal static class McpFixtureExtensions
{
    /// <summary>An MCP client speaking as <paramref name="agent"/>, with the lane key.</summary>
    public static McpTestClient Mcp(this SharedServerFixture server, string? agent = null, string? apiKey = null) =>
        McpTestClient.Create(server.BaseUrl, apiKey ?? SharedServerFixture.McpKey, agent);
}
