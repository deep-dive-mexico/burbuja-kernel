using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Example.Blast.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Burbuja.Realtime.Tests;

/// <summary>
/// End-to-end test for the Blast host: the real host with the Blast plugin installed dynamically,
/// two guests create and join a lobby, connect over WebSockets, receive the stage layout and a
/// state snapshot, and send an input whose ack appears in the next state.
/// </summary>
public sealed class BlastHostTests : IAsyncDisposable
{
    private const string Operator = "blast-operator-dev-key";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "burbuja-blast-tests", Guid.NewGuid().ToString("N"));
    private WebApplication? _app;

    private async Task<WebApplication> StartAsync()
    {
        var plugins = Path.Combine(_root, "plugins");
        Copy(Path.Combine(AppContext.BaseDirectory, "blast-plugin-fixture"), Path.Combine(plugins, "Example.Blast"));
        _app = BlastHost.Create(new WebApplicationOptions
        {
            ApplicationName = typeof(BlastHost).Assembly.GetName().Name,
            ContentRootPath = _root,
            EnvironmentName = "Production",
        }, builder =>
        {
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kernel:Role"] = "control-plane",
                ["Kernel:NodeId"] = "blast-test",
                ["Kernel:Seed"] = "false",
                ["Kernel:PluginsDirectory"] = plugins,
                ["Auth:ApiKeys:0:Key"] = Operator,
                ["Auth:ApiKeys:0:PrincipalId"] = "operator",
                ["Auth:ApiKeys:0:Roles:0"] = "Owner",
                ["Realtime:Enabled"] = "true",
                ["Realtime:Guests:Enabled"] = "true",
                ["Realtime:Matchmaking:IntervalMilliseconds"] = "50",
                ["Realtime:Servers:0:Id"] = "blast-a",
                ["Realtime:Servers:0:MaxLobbies"] = "2",
                ["Realtime:Servers:1:Id"] = "blast-b",
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
    public async Task Two_guests_host_join_lobby_connect_receive_stage_and_state()
    {
        var app = await StartAsync();

        var games = await Json(await Http().GetAsync("/realtime/v1/games", Ct), HttpStatusCode.OK);
        Assert.Equal("blast", (string?)Assert.Single(games["value"]!.AsArray())!["id"]);

        // Host creates a private lobby with 2 bots so the match starts with just them.
        using var host = await GuestAsync("Alice");
        using var friend = await GuestAsync("Bob");

        var created = await Json(await host.PostAsJsonAsync("/realtime/v1/lobbies", new
        {
            game = "blast",
            visibility = "private",
            settings = new Dictionary<string, string> { ["wins"] = "1", ["bots"] = "2", ["round-seconds"] = "60" },
        }, Ct), HttpStatusCode.Created);

        var code = (string)created["lobby"]!["joinCode"]!;
        Assert.Equal("hosted", (string?)created["lobby"]!["origin"]);

        var friendSeat = await Json(await friend.PostAsJsonAsync("/realtime/v1/lobbies/join",
            new { code }, Ct), HttpStatusCode.OK);

        var hostSeat = created["reservation"]!.AsObject();

        // Both connect over WebSocket.
        using var hostSocket = await ConnectAsync(app, hostSeat);
        using var friendSocket = await ConnectAsync(app, friendSeat);

        // Both receive sys.welcome.
        var welcome = await NextAsync(hostSocket, "sys.welcome");
        Assert.Equal((string?)hostSeat["playerId"], (string?)welcome["d"]!["playerId"]);
        Assert.Equal(30, (int)welcome["d"]!["tickRate"]!);

        // Both receive the stage layout.
        var stage = await NextAsync(hostSocket, "stage");
        Assert.NotNull(stage["d"]);
        Assert.True((int)stage["d"]!["width"]! > 0);
        Assert.True((int)stage["d"]!["height"]! > 0);
        Assert.NotEmpty(stage["d"]!["cells"]!.AsArray());

        // Wait for the lobby to start running (3-second countdown).
        await NextAsync(hostSocket, "sys.lobby", f => (string?)f["d"]!["state"] == "running", TimeSpan.FromSeconds(10));

        // Receive at least one state snapshot.
        var first = await NextAsync(hostSocket, "state");
        Assert.NotNull(first["d"]);
        Assert.NotNull(first["d"]!["players"]);
        Assert.True(first["d"]!["players"]!.AsArray().Count >= 1);

        // Send an input and verify ack advances.
        await SendAsync(hostSocket, """{"t":"input","d":{"x":1,"z":0,"a":false,"b":false,"r":false},"s":1}""");

        var acked = await NextAsync(hostSocket, "state",
            f => f["d"]!["players"]!.AsArray()
                .Any(p => (string?)p!["id"] == (string?)hostSeat["playerId"] && p["ack"] is not null),
            TimeSpan.FromSeconds(5));
        var me = acked["d"]!["players"]!.AsArray()
            .Single(p => (string?)p!["id"] == (string?)hostSeat["playerId"])!;
        Assert.Equal(1, (long)me["ack"]!);

        // Request a stage resend.
        await SendAsync(hostSocket, """{"t":"stage"}""");
        var stage2 = await NextAsync(hostSocket, "stage", null, TimeSpan.FromSeconds(5));
        Assert.NotNull(stage2["d"]);
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

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

    private static async Task<WebSocket> ConnectAsync(WebApplication app, JsonObject reservation)
    {
        var server = app.GetTestServer();
        return await server.CreateWebSocketClient().ConnectAsync(new Uri(server.BaseAddress, (string)reservation["connectUrl"]!), Ct);
    }

    private static Task SendAsync(WebSocket socket, string json) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, Ct);

    private static async Task<JsonObject> NextAsync(WebSocket socket, string type, Func<JsonObject, bool>? match = null, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
        var buffer = new byte[128 * 1024];
        while (true)
        {
            var count = 0;
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), deadline.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException($"socket closed ({result.CloseStatus}) while waiting for {type}");
                count += result.Count;
            }
            while (!result.EndOfMessage);
            if (result.MessageType != WebSocketMessageType.Text) continue;
            var frame = JsonNode.Parse(buffer.AsSpan(0, count))!.AsObject();
            if ((string?)frame["t"] == type && (match is null || match(frame)))
                return frame;
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
        if (_app is not null) await _app.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
