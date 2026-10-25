using Burbuja.Realtime.Abstractions;
using Burbuja.Realtime.Lobbies;
using Burbuja.Realtime.Fleet;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Burbuja.Realtime.Tests;

public sealed class CapacityPoolTests
{
    private sealed class SettingsPlacement : ILobbyPlacementStrategy
    {
        public string Name => "settings";
        public string? Choose(IReadOnlyList<LobbyServerStatus> candidates, PlacementRequest request) => request.Settings.GetValueOrDefault("pool", "a");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Product_placement_receives_creation_settings_locally_and_through_the_fleet(bool fleet)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["Realtime:Placement"]="settings",["Realtime:Servers:0:Id"]="a",["Realtime:Servers:0:Region"]="local",
            ["Realtime:Servers:1:Id"]="b",["Realtime:Servers:1:Region"]="local",
        }).Build();
        var services = new ServiceCollection();services.AddLogging();services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IRealtimeGame>(new RecordingGame());services.AddBurbujaRealtime();services.AddSingleton<ILobbyPlacementStrategy,SettingsPlacement>();
        await using var provider = services.BuildServiceProvider();
        var manager=provider.GetRequiredService<LobbyManager>();
        var request=new CreateLobbyRequest("test",LobbyVisibility.Private,LobbyOrigin.Hosted,Settings:new Dictionary<string,string>{{"pool","b"}});
        try
        {
            if(fleet)
            {
                var coordinator=provider.GetRequiredService<RealtimeFleet>();await coordinator.StartAsync(TestContext.Current.CancellationToken);
                var created=await coordinator.CreateAsync(request,[],TestContext.Current.CancellationToken);
                Assert.Equal("b",created.Lobby?.ServerId);await coordinator.DisposeAsync();
            }
            else Assert.Equal("b",manager.Create(request).Lobby?.ServerId);
        }
        finally {await manager.ShutdownAsync();}
    }
    [Theory]
    [InlineData("[]")]
    [InlineData("[\"peer\",\"peer\"]")]
    [InlineData("[\"missing\"]")]
    public void Invalid_explicit_pool_configuration_fails_at_boot(string games)
    {
        var json = "{\"Realtime\":{\"Servers\":[{\"Id\":\"pool\",\"Region\":\"local\",\"MaxLobbies\":1,\"AllowedGames\":" + games + "}]}}";
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddJsonStream(source).Build());
        services.AddSingleton<IRealtimeGame>(new RecordingGame(id: "peer"));
        services.AddBurbujaRealtime();
        using var provider = services.BuildServiceProvider();
        Assert.Contains("AllowedGames", Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<LobbyManager>()).Message);
    }
    private sealed class Admission : ISessionResourceAdmission
    {
        public bool Allowed { get; set; } = true;
        public bool CanIncrease(string resource) => Allowed;
    }
    private sealed class JoinPolicySession : GameSession
    {
        private bool _joinable = true;
        public override bool AcceptsNewPlayers => _joinable;
        public override void OnMessage(ISessionContext context, RealtimePlayer player, InboundMessage message)
        { if (message.Type == "lock") _joinable = false; if (message.Type == "unlock") _joinable = true; }
    }

    [Fact]
    public void Playing_room_can_close_new_admission_without_losing_reconnects_or_rematches()
    {
        var harness = new Harness(new RecordingGame(session: () => new JoinPolicySession()));
        var room = harness.CreateLobby();
        var host = harness.Connect(room, "a");
        harness.Step();
        host.Send("{\"t\":\"lock\",\"d\":{}}");
        harness.Step();
        Assert.Equal("match_in_progress", harness.Reserve(room, "b").Error);
        Assert.NotNull(harness.Reserve(room, "a").Reservation);
        host.Send("{\"t\":\"unlock\",\"d\":{}}");
        harness.Step();
        Assert.NotNull(harness.Reserve(room, "b").Reservation);
    }

    private sealed class ReservingSession : GameSession
    {
        public override int? MinPlayersToStart => 1;
        public override void OnMessage(ISessionContext context, RealtimePlayer player, InboundMessage message)
        {
            if (message.Type == "reserve")
                context.Send(player.PlayerId, "reserved", new { accepted = context.TryReserveResource("execution", message.Data.GetInt32()) });
            if (message.Type == "fault") throw new InvalidOperationException("owned session failed");
        }
    }

    [Fact]
    public async Task Match_permits_are_independent_of_room_slots_and_released_after_fault()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Realtime:Resources:execution"] = "1",
            ["Realtime:Servers:0:Id"] = "rooms",
            ["Realtime:Servers:0:Region"] = "local",
            ["Realtime:Servers:0:MaxLobbies"] = "4",
        }).Build();
        var services = new ServiceCollection();
        var admission = new Admission();
        services.AddSingleton<ISessionResourceAdmission>(admission);
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IRealtimeGame>(new RecordingGame(session: () => new ReservingSession()));
        services.AddBurbujaRealtime();
        await using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<LobbyManager>();
        try
        {
            (Lobby Room, FakeConnection Client) Join(string id)
            {
                var room = manager.Create(new("test", LobbyVisibility.Private, LobbyOrigin.Hosted)).Lobby!;
                var reservation = room.Reserve(new(id, id), DateTimeOffset.UtcNow).Reservation!;
                var client = new FakeConnection();
                client.Inbox = manager.Admit(reservation.Token, client).Inbox!;
                room.Step(DateTimeOffset.UtcNow);
                return (room, client);
            }
            bool Reserve((Lobby Room, FakeConnection Client) seat, int count)
            {
                seat.Client.Send("{\"t\":\"reserve\",\"d\":" + count + "}");
                seat.Room.Step(DateTimeOffset.UtcNow);
                return seat.Client.OfType("reserved").Last()["d"]!["accepted"]!.GetValue<bool>();
            }
            var a = Join("a"); var b = Join("b");
            Assert.True(Reserve(a, 1));
            Assert.True(Reserve(a, 1));
            Assert.False(Reserve(b, 1));
            admission.Allowed = false;
            Assert.True(Reserve(a, 1));
            Assert.True(Reserve(a, 0));
            Assert.False(Reserve(b, 1));
            admission.Allowed = true;
            Assert.True(Reserve(b, 1));
            Assert.Equal(2, manager.Balancer.Servers.Single().Status().Lobbies);
            b.Client.Send("{\"t\":\"fault\",\"d\":{}}");
            b.Room.Step(DateTimeOffset.UtcNow);
            Assert.True(Reserve(a, 1));
            var server = manager.Balancer.Servers.Single();
            server.Drain();
            Assert.True(Reserve(a, 1));
            Assert.True(Reserve(a, 0));
            Assert.False(Reserve(a, 1));
            server.Activate();
            Assert.True(Reserve(a, 1));
        }
        finally { await manager.ShutdownAsync(); }
    }

    [Fact]
    public async Task Explicit_server_cannot_admit_a_game_outside_its_capacity_pool()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Realtime:Servers:0:Id"] = "broker",
            ["Realtime:Servers:0:Region"] = "local",
            ["Realtime:Servers:0:MaxLobbies"] = "1",
            ["Realtime:Servers:0:AllowedGames:0"] = "peer",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IRealtimeGame>(new RecordingGame(id: "simulation"));
        services.AddSingleton<IRealtimeGame>(new RecordingGame(id: "peer"));
        services.AddBurbujaRealtime();
        await using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<LobbyManager>();
        try
        {
            var refused = manager.Create(new("simulation", LobbyVisibility.Private, LobbyOrigin.Hosted, ServerId: "broker"));
            Assert.Null(refused.Lobby);
            Assert.Equal("no_capacity", refused.Error);
            var accepted = manager.Create(new("peer", LobbyVisibility.Private, LobbyOrigin.Hosted, ServerId: "broker"));
            Assert.NotNull(accepted.Lobby);
            Assert.Equal(1, manager.Balancer.Servers.Single().Status().Lobbies);
        }
        finally { await manager.ShutdownAsync(); }
    }

    [Fact]
    public async Task Concurrent_match_starts_share_the_node_budget_and_shutdown_frees_every_claim()
    {
        var time = new FakeTimeProvider();
        var server = new LobbyServer("rooms", "local", 100, null, time, NullLogger.Instance);
        var manager = new LobbyManager(new RealtimeGames([new RecordingGame(session: () => new ReservingSession())]),
            new LobbyBalancer([server], new LeastLoadedPlacement(), true),
            new RealtimeOptions { Resources = new() { ["execution"] = 4 } }, "node", time, NullLoggerFactory.Instance, false);
        var accepted = 0;
        Parallel.For(0, 100, i =>
        {
            var room = manager.Create(new("test", LobbyVisibility.Private, LobbyOrigin.Hosted)).Lobby!;
            var reservation = room.Reserve(new($"p{i}", "Player"), time.GetUtcNow()).Reservation!;
            var client = new FakeConnection();
            client.Inbox = manager.Admit(reservation.Token, client).Inbox!;
            client.Send("{\"t\":\"reserve\",\"d\":1}");
            room.Step(time.GetUtcNow());
            if (client.OfType("reserved").Single()["d"]!["accepted"]!.GetValue<bool>()) Interlocked.Increment(ref accepted);
        });
        Assert.Equal(4, accepted);
        Assert.Equal(4, manager.Lobbies.Sum(l => l.Snapshot().ResourceClaims.GetValueOrDefault("execution")));
        await manager.ShutdownAsync();
        Assert.Empty(manager.Lobbies);
        Assert.Equal(0, server.Status().Lobbies);
    }

    [Fact]
    public async Task Fleet_routes_only_to_eligible_pools_and_reports_effective_support()
    {
        var time = new FakeTimeProvider();
        var local = new LobbyServer("peer-only", "local", 4, null, time, NullLogger.Instance, ["peer"]);
        var h = new Harness(new RecordingGame(id: "test"), null, local);
        var remote = h.Join("node-2", servers: [new LobbyServer("execution", "local", 2, null, h.Time, NullLogger.Instance, ["test"])]);
        var fleet = await h.Fleet.ServersAsync(TestContext.Current.CancellationToken);
        Assert.Empty(fleet.Single(s => s.Status.ServerId == "peer-only").Games);
        var result = await h.Fleet.CreateAsync(new("test", LobbyVisibility.Private, LobbyOrigin.Hosted), [], TestContext.Current.CancellationToken);
        Assert.NotNull(result.Lobby);
        Assert.Equal("execution", result.Lobby.ServerId);
        Assert.Equal(0, local.Status().Lobbies);
        Assert.Equal("no_capacity", remote.Manager.Create(new("test", LobbyVisibility.Private, LobbyOrigin.Hosted, ServerId: "peer-only")).Error);
        await h.Manager.ShutdownAsync(); await remote.Manager.ShutdownAsync();
        await h.Fleet.DisposeAsync(); await remote.Fleet.DisposeAsync();
    }
}
