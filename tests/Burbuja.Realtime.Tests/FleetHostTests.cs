using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using Burbuja.Core.Plugin;
using Burbuja.Realtime.Abstractions;
using Example.Blast.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Burbuja.Realtime.Tests;

/// <summary>
/// Two real Blast hosts form one fleet over a real Redis/Valkey: each installs the Blast game and the Redis
/// backplane as dynamic plugins, as an operator would. Players sign in and queue through different nodes, meet in
/// one lobby on whichever node the fleet placed it, and connect to that node over WebSockets. Only the network
/// between players and hosts is in-memory (TestServer).
/// </summary>
[Collection("Redis")]
public sealed class FleetHostTests : IAsyncLifetime
{
    private const string Operator = "fleet-operator-dev-key";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "burbuja-fleet-tests", Guid.NewGuid().ToString("N"));
    private readonly string _prefix = "{brt-fleet-" + Guid.NewGuid().ToString("N")[..8] + "}:";
    private readonly Dictionary<string, WebApplication> _nodes = new(StringComparer.Ordinal);

    public async ValueTask InitializeAsync()
    {
        if (await RedisFixture.TryGetMultiplexerAsync() is null)
            Assert.Skip("No Redis server available: set BURBUJA_REDIS to a connection string or ensure Docker is running.");
    }

    private async Task<WebApplication> StartNodeAsync(string nodeId)
    {
        var plugins = Path.Combine(_root, nodeId, "plugins");
        Copy(Path.Combine(AppContext.BaseDirectory, "blast-plugin-fixture"), Path.Combine(plugins, "Example.Blast"));
        Copy(Path.Combine(AppContext.BaseDirectory, "redis-plugin-fixture"), Path.Combine(plugins, "Realtime.Redis"));
        var app = BlastHost.Create(new WebApplicationOptions
        {
            ApplicationName = typeof(BlastHost).Assembly.GetName().Name,
            ContentRootPath = Path.Combine(_root, nodeId),
            EnvironmentName = "Production",
        }, builder =>
        {
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kernel:Role"] = "control-plane",
                ["Kernel:NodeId"] = nodeId,
                ["Kernel:Seed"] = "false",
                ["Kernel:PluginsDirectory"] = plugins,
                ["Auth:ApiKeys:0:Key"] = Operator,
                ["Auth:ApiKeys:0:PrincipalId"] = "operator",
                ["Auth:ApiKeys:0:Roles:0"] = "Owner",
                ["Realtime:Enabled"] = "true",
                ["Realtime:Guests:Enabled"] = "true",
                // One signing key for the fleet, so a guest signed in on one node is known to every node.
                ["Realtime:Guests:SigningKey"] = "fleet-test-guest-signing-key-0123456789",
                ["Realtime:Matchmaking:IntervalMilliseconds"] = "50",
                ["Realtime:Fleet:HeartbeatMilliseconds"] = "200",
                ["Realtime:Servers:0:Id"] = nodeId + "-1",
                ["Realtime:Servers:0:MaxLobbies"] = "2",
                ["Realtime:Servers:0:PublicUrl"] = $"ws://{nodeId}.test",
                ["Realtime:Redis:Configuration"] = RedisFixture.ConnectionString,
                ["Realtime:Redis:KeyPrefix"] = _prefix,
                ["Workspace:WorktreeRoot"] = Path.Combine(_root, nodeId, "worktrees"),
                ["Workspace:RepoRoot"] = _root,
                ["Logging:LogLevel:Default"] = "Warning",
            });
            builder.WebHost.UseTestServer();
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        });
        await app.StartAsync(Ct);
        _nodes[nodeId] = app;
        return app;
    }

    [Fact]
    public async Task Players_queued_through_two_nodes_play_one_match_on_the_node_the_fleet_chose()
    {
        var east = await StartNodeAsync("east");
        var west = await StartNodeAsync("west");

        // The backplane came from the installed plugin, in its own load context, and it is the shared one.
        var plugin = Assert.Single(east.Services.GetRequiredService<PluginHost>().Loaded, p => p.Manifest.Id == "Realtime.Redis");
        Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(plugin.Assembly));
        Assert.True(east.Services.GetRequiredService<IRealtimeBackplane>().IsDistributed);

        // A guest signs in on one node and is known on the other; each queues through a different node.
        var players = new List<(string Name, WebApplication Via, HttpClient Client)>();
        foreach (var (name, via) in new[] { ("Ana", east), ("Ben", east), ("Cai", west), ("Dee", west) })
            players.Add((name, via, await GuestAsync(via, east, name)));
        var tickets = new List<JsonObject>();
        foreach (var player in players)
            tickets.Add(await Json(await player.Client.PostAsJsonAsync("/realtime/v1/queue", new { game = "blast" }, Ct), HttpStatusCode.Accepted));
        var seats = new List<JsonObject>();
        for (var i = 0; i < players.Count; i++)
            seats.Add(await MatchedAsync(players[i].Client, tickets[i]));

        // One lobby for all four, wherever it was placed; every reservation names its node's public URL.
        var lobbyId = (string)seats[0]["lobbyId"]!;
        Assert.All(seats, s => Assert.Equal(lobbyId, (string?)s["lobbyId"]));
        var owner = new Uri((string)seats[0]["connectUrl"]!).Host.Split('.')[0];
        Assert.Contains(owner, _nodes.Keys);
        Assert.All(seats, s => Assert.StartsWith($"ws://{owner}.test/realtime/v1/connect?token=", (string)s["connectUrl"]!));
        var other = _nodes.Keys.Single(k => k != owner);

        // Any node answers for the lobby and for the players' single seat.
        var asOther = await Json(await players[0].Client.GetAsync($"/realtime/v1/lobbies/{lobbyId}", Ct), HttpStatusCode.OK);
        Assert.Equal(owner + "-1", (string?)asOther["serverId"]);
        var queuedAgain = await players[0].Client.PostAsJsonAsync("/realtime/v1/queue", new { game = "blast" }, Ct);
        Assert.Equal("already_in_lobby", (string?)(await Json(queuedAgain, HttpStatusCode.Conflict))["error"]!["code"]);
        using var onOther = SameGuestOn(_nodes[other], players[0].Client);
        var me = await Json(await onOther.GetAsync("/realtime/v1/me", Ct), HttpStatusCode.OK);
        Assert.Equal(lobbyId, (string?)me["lobbyId"]);

        // Everyone connects to the owning node and plays the same match.
        var sockets = new List<WebSocket>();
        foreach (var seat in seats)
            sockets.Add(await ConnectAsync(seat));
        foreach (var socket in sockets)
            await NextAsync(socket, "sys.welcome");
        await NextAsync(sockets[0], "sys.lobby", f => (string?)f["d"]!["state"] == "running", TimeSpan.FromSeconds(10));
        var state = await NextAsync(sockets[3], "state", f => f["d"]!["players"]!.AsArray().Count == 4, TimeSpan.FromSeconds(5));
        Assert.Equal(players.Select(p => p.Name).Order(), state["d"]!["players"]!.AsArray().Select(p => (string)p!["name"]!).Order());

        // The operator asks the node that does not host the match and sees the whole fleet.
        using var op = Http(_nodes[other], Operator);
        var fleet = await Json(await op.GetAsync("/realtime/v1/servers", Ct), HttpStatusCode.OK);
        var servers = fleet["value"]!.AsArray();
        Assert.Equal(["east-1", "west-1"], servers.Select(s => (string)s!["serverId"]!).Order());
        var hosting = servers.Single(s => (string?)s!["nodeId"] == owner)!;
        var hosted = Assert.Single(hosting["hosted"]!.AsArray())!;
        Assert.Equal(lobbyId, (string?)hosted["lobbyId"]);
        Assert.Equal(4, (int)hosted["seats"]!);

        // A node leaving says so: the rest of the fleet stops listing its servers at once.
        await _nodes[other].StopAsync(Ct);
        using var stillUp = Http(_nodes[owner], Operator);
        var after = await Json(await stillUp.GetAsync("/realtime/v1/servers", Ct), HttpStatusCode.OK);
        Assert.Equal([owner + "-1"], after["value"]!.AsArray().Select(s => (string)s!["serverId"]!));
        foreach (var socket in sockets)
            socket.Dispose();
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────────────────

    private static HttpClient Http(WebApplication node, string? apiKey = null)
    {
        var client = node.GetTestClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        if (apiKey is not null)
            client.DefaultRequestHeaders.Add("x-api-key", apiKey);
        return client;
    }

    /// <summary>A guest signed in on <paramref name="signIn"/>, calling the API of <paramref name="via"/>.</summary>
    private static async Task<HttpClient> GuestAsync(WebApplication via, WebApplication signIn, string name)
    {
        var guest = await Json(await Http(signIn).PostAsJsonAsync("/realtime/v1/guests", new { displayName = name }, Ct), HttpStatusCode.Created);
        var client = Http(via);
        client.DefaultRequestHeaders.Add("x-player-token", (string)guest["token"]!);
        return client;
    }

    private static HttpClient SameGuestOn(WebApplication node, HttpClient existing)
    {
        var client = Http(node);
        client.DefaultRequestHeaders.Add("x-player-token", existing.DefaultRequestHeaders.GetValues("x-player-token").Single());
        return client;
    }

    private static async Task<JsonObject> MatchedAsync(HttpClient player, JsonObject ticket)
    {
        for (var i = 0; i < 10 && (string?)ticket["state"] == "queued"; i++)
            ticket = await Json(await player.GetAsync($"/realtime/v1/queue/{ticket["ticketId"]}?waitSeconds=5", Ct), HttpStatusCode.OK);
        Assert.Equal("matched", (string?)ticket["state"]);
        return ticket["reservation"]!.AsObject();
    }

    /// <summary>Connects to the node a reservation names, as a client resolving its connect URL would.</summary>
    private async Task<WebSocket> ConnectAsync(JsonObject reservation)
    {
        var url = new Uri((string)reservation["connectUrl"]!);
        var server = _nodes[url.Host.Split('.')[0]].GetTestServer();
        return await server.CreateWebSocketClient().ConnectAsync(new Uri(server.BaseAddress, url.PathAndQuery), Ct);
    }

    private static async Task<JsonObject> NextAsync(WebSocket socket, string type, Func<JsonObject, bool>? match = null, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
        var buffer = new byte[256 * 1024];
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
            if (result.MessageType != WebSocketMessageType.Text)
                continue;
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
        foreach (var app in _nodes.Values)
        {
            try
            {
                await app.StopAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
            {
            }

            await app.DisposeAsync();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
