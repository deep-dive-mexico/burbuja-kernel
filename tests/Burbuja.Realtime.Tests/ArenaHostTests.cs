using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json.Nodes;
using Burbuja.Core.Plugin;
using Burbuja.Realtime.Abstractions;
using Example.Arena.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Burbuja.Realtime.Tests;

/// <summary>
/// The PoC end to end: the real Arena host, the Arena game installed as a dynamic plugin from its own
/// directory, guests over HTTP, matchmaking, placement, and play over real WebSocket frames. Only the network
/// is in-memory (TestServer).
/// </summary>
public sealed class ArenaHostTests : IAsyncDisposable
{
    private const string Operator = "arena-operator-dev-key";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "burbuja-arena-tests", Guid.NewGuid().ToString("N"));
    private WebApplication? _app;

    private async Task<WebApplication> StartAsync(bool enabled = true)
    {
        var plugins = Path.Combine(_root, "plugins");
        Copy(Path.Combine(AppContext.BaseDirectory, "arena-plugin-fixture"), Path.Combine(plugins, "Example.Arena"));
        _app = ArenaHost.Create(new WebApplicationOptions
        {
            ApplicationName = typeof(ArenaHost).Assembly.GetName().Name,
            ContentRootPath = _root,
            EnvironmentName = "Production",
        }, builder =>
        {
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kernel:Role"] = "control-plane",
                ["Kernel:NodeId"] = "arena-test",
                ["Kernel:Seed"] = "false",
                ["Kernel:PluginsDirectory"] = plugins,
                ["Auth:ApiKeys:0:Key"] = Operator,
                ["Auth:ApiKeys:0:PrincipalId"] = "operator",
                ["Auth:ApiKeys:0:Roles:0"] = "Owner",
                ["Auth:ApiKeys:1:Key"] = "arena-player-key",
                ["Auth:ApiKeys:1:PrincipalId"] = "bot",
                ["Realtime:Enabled"] = enabled.ToString(),
                ["Realtime:Guests:Enabled"] = "true",
                ["Realtime:Matchmaking:IntervalMilliseconds"] = "50",
                ["Realtime:Servers:0:Id"] = "arena-a",
                ["Realtime:Servers:0:MaxLobbies"] = "2",
                ["Realtime:Servers:1:Id"] = "arena-b",
                ["Realtime:Servers:1:MaxLobbies"] = "2",
                ["Workspace:WorktreeRoot"] = Path.Combine(_root, "worktrees"),
                ["Workspace:RepoRoot"] = _root,
                ["Logging:LogLevel:Default"] = "Warning",
            });
            builder.WebHost.UseTestServer();
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        });
        await _app.StartAsync(Ct);
        return _app;
    }

    [Fact]
    public async Task Two_guests_queue_get_one_dedicated_lobby_and_play_over_websockets()
    {
        var app = await StartAsync();

        // The game came from the installed plugin, loaded in its own context — not from a project reference.
        var loaded = Assert.Single(app.Services.GetRequiredService<PluginHost>().Loaded, p => p.Manifest.Id == "Example.Arena");
        Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(loaded.Assembly));
        var games = await Json(await Http().GetAsync("/realtime/v1/games", Ct), HttpStatusCode.OK);
        Assert.Equal("arena", (string?)Assert.Single(games["value"]!.AsArray())!["id"]);

        using var ana = await GuestAsync("Ana");
        using var ben = await GuestAsync("Ben");
        var anaTicket = await Json(await ana.PostAsJsonAsync("/realtime/v1/queue", new { game = "arena" }, Ct), HttpStatusCode.Accepted);
        var benTicket = await Json(await ben.PostAsJsonAsync("/realtime/v1/queue", new { game = "arena" }, Ct), HttpStatusCode.Accepted);
        var anaSeat = await MatchedAsync(ana, anaTicket);
        var benSeat = await MatchedAsync(ben, benTicket);
        Assert.Equal((string?)anaSeat["lobbyId"], (string?)benSeat["lobbyId"]);

        using var anaSocket = await ConnectAsync(app, anaSeat);
        using var benSocket = await ConnectAsync(app, benSeat);
        var welcome = await NextAsync(anaSocket, "sys.welcome");
        Assert.Equal((string?)anaSeat["playerId"], (string?)welcome["d"]!["playerId"]);
        Assert.Equal(30, (int)welcome["d"]!["tickRate"]!);
        Assert.Equal(4, (await NextAsync(anaSocket, "map"))["d"]!["walls"]!.AsArray()[0]!.AsArray().Count);

        // Arena counts down for 3 seconds, then simulates at 30 Hz.
        await NextAsync(anaSocket, "sys.lobby", f => (string?)f["d"]!["state"] == "running", TimeSpan.FromSeconds(8));
        var first = await NextAsync(anaSocket, "state");
        var me = first["d"]!["players"]!.AsArray().Single(p => (string?)p!["id"] == (string?)anaSeat["playerId"])!;
        var startX = (double)me["x"]!;
        Assert.Equal(2, first["d"]!["players"]!.AsArray().Count);

        // Intent only: hold "right" and let the server move us. 60 Arena ticks later we are well right of the start
        // (or stopped by a wall), and every state frame is the server's.
        await SendAsync(anaSocket, """{"t":"input","d":{"x":1,"y":0,"aim":0,"fire":false}}""");
        var later = await NextAsync(anaSocket, "state", f => (long)f["k"]! >= (long)first["k"]! + 60, TimeSpan.FromSeconds(5));
        var moved = later["d"]!["players"]!.AsArray().Single(p => (string?)p!["id"] == (string?)anaSeat["playerId"])!;
        Assert.True((double)moved["x"]! > startX + 20, $"expected to move right from {startX}, got {moved["x"]}");

        await SendAsync(benSocket, """{"t":"sys.ping","d":{"t":1}}""");
        Assert.Equal(1, (int)(await NextAsync(benSocket, "sys.pong"))["d"]!["echo"]!["t"]!);

        // The operator sees one placed lobby holding both players; a player key is not an operator.
        using var op = Http(Operator);
        var fleet = await Json(await op.GetAsync("/realtime/v1/servers", Ct), HttpStatusCode.OK);
        Assert.Equal("least-loaded", (string?)fleet["placement"]);
        var hosting = fleet["value"]!.AsArray().Single(s => s!["hosted"]!.AsArray().Count == 1)!;
        Assert.Equal(2, (int)hosting["players"]!);
        await Json(await Http("arena-player-key").GetAsync("/realtime/v1/servers", Ct), HttpStatusCode.Forbidden);
        await Json(await Http().GetAsync("/realtime/v1/servers", Ct), HttpStatusCode.Unauthorized);

        // Leaving is immediate and final: the socket closes with the kernel's "left" code.
        await SendAsync(benSocket, """{"t":"sys.leave"}""");
        Assert.Equal((WebSocketCloseStatus)RealtimeCloseCode.Left, await ClosedAsync(benSocket));
        await NextAsync(anaSocket, "sys.lobby", f => f["d"]!["players"]!.AsArray().Count == 1);
    }

    [Fact]
    public async Task A_hosted_private_lobby_is_joined_by_code_and_refusals_are_explicit()
    {
        var app = await StartAsync();
        using var host = await GuestAsync("Host");
        using var friend = await GuestAsync("Friend");

        var created = await Json(await host.PostAsJsonAsync("/realtime/v1/lobbies", new { game = "arena", visibility = "private" }, Ct),
            HttpStatusCode.Created);
        var code = (string)created["lobby"]!["joinCode"]!;
        Assert.Equal("hosted", (string?)created["lobby"]!["origin"]);
        var listed = await Json(await Http().GetAsync("/realtime/v1/lobbies?game=arena", Ct), HttpStatusCode.OK);
        Assert.Empty(listed["value"]!.AsArray());

        var seat = await Json(await friend.PostAsJsonAsync("/realtime/v1/lobbies/join", new { code = code.ToLowerInvariant() }, Ct),
            HttpStatusCode.OK);
        Assert.Equal((string?)created["lobby"]!["lobbyId"], (string?)seat["lobbyId"]);
        var asFriend = await Json(await friend.GetAsync($"/realtime/v1/lobbies/{seat["lobbyId"]}", Ct), HttpStatusCode.OK);
        Assert.Equal(code, (string?)asFriend["joinCode"]);

        // One seat per player, an unknown game, a bad name, a missing credential and a forged token.
        await Json(await friend.PostAsJsonAsync("/realtime/v1/queue", new { game = "arena" }, Ct), HttpStatusCode.Conflict);
        using var other = await GuestAsync("Other");
        await Json(await other.PostAsJsonAsync("/realtime/v1/queue", new { game = "chess" }, Ct), HttpStatusCode.NotFound);
        await Json(await Http().PostAsJsonAsync("/realtime/v1/guests", new { displayName = "" }, Ct), HttpStatusCode.BadRequest);
        await Json(await Http().PostAsJsonAsync("/realtime/v1/queue", new { game = "arena" }, Ct), HttpStatusCode.Unauthorized);

        using var forged = await app.GetTestServer().CreateWebSocketClient()
            .ConnectAsync(new Uri(app.GetTestServer().BaseAddress, "/realtime/v1/connect?token=lby-0.nope"), Ct);
        Assert.Equal((WebSocketCloseStatus)RealtimeCloseCode.InvalidReservation, await ClosedAsync(forged));

        // A claimed token cannot be replayed.
        using var friendSocket = await ConnectAsync(app, seat);
        await NextAsync(friendSocket, "sys.welcome");
        using var replay = await ConnectAsync(app, seat);
        Assert.Equal((WebSocketCloseStatus)RealtimeCloseCode.InvalidReservation, await ClosedAsync(replay));
    }

    [Fact]
    public async Task A_host_without_realtime_enabled_exposes_nothing()
    {
        await StartAsync(enabled: false);
        using var response = await Http().GetAsync("/realtime/v1/games", Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────────────────

    private HttpClient Http(string? apiKey = null)
    {
        var client = _app!.GetTestClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        if (apiKey is not null)
            client.DefaultRequestHeaders.Add("x-api-key", apiKey);
        return client;
    }

    private async Task<HttpClient> GuestAsync(string name)
    {
        var guest = await Json(await Http().PostAsJsonAsync("/realtime/v1/guests", new { displayName = name }, Ct), HttpStatusCode.Created);
        var client = Http();
        client.DefaultRequestHeaders.Add("x-player-token", (string)guest["token"]!);
        return client;
    }

    private static async Task<JsonObject> MatchedAsync(HttpClient player, JsonObject ticket)
    {
        for (var i = 0; i < 10 && (string?)ticket["state"] == "queued"; i++)
            ticket = await Json(await player.GetAsync($"/realtime/v1/queue/{ticket["ticketId"]}?waitSeconds=5", Ct), HttpStatusCode.OK);
        Assert.Equal("matched", (string?)ticket["state"]);
        return ticket["reservation"]!.AsObject();
    }

    private static async Task<WebSocket> ConnectAsync(WebApplication app, JsonObject reservation)
    {
        var server = app.GetTestServer();
        return await server.CreateWebSocketClient().ConnectAsync(new Uri(server.BaseAddress, (string)reservation["connectUrl"]!), Ct);
    }

    private static Task SendAsync(WebSocket socket, string json) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, Ct);

    /// <summary>The next frame of <paramref name="type"/> matching <paramref name="match"/>, skipping others.</summary>
    private static async Task<JsonObject> NextAsync(WebSocket socket, string type, Func<JsonObject, bool>? match = null, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = 0;
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), deadline.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException($"socket closed ({result.CloseStatus} {result.CloseStatusDescription}) while waiting for {type}");
                count += result.Count;
            }
            while (!result.EndOfMessage);
            var frame = JsonNode.Parse(buffer.AsSpan(0, count))!.AsObject();
            if ((string?)frame["t"] == type && (match is null || match(frame)))
                return frame;
        }
    }

    private static async Task<WebSocketCloseStatus?> ClosedAsync(WebSocket socket)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                return result.CloseStatus;
        }
    }

    private static async Task<JsonObject> Json(HttpResponseMessage response, HttpStatusCode expected)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == expected, $"Expected {(int)expected}, got {(int)response.StatusCode}: {body}");
            return string.IsNullOrEmpty(body) ? new JsonObject() : JsonNode.Parse(body)!.AsObject();
        }
    }

    private static void Copy(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
