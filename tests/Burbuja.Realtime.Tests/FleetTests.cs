using Burbuja.Realtime.Abstractions;
using Burbuja.Realtime.Fleet;
using Burbuja.Realtime.Lobbies;
using Burbuja.Realtime.Matchmaking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Burbuja.Realtime.Tests;

/// <summary>
/// Several nodes of one fleet in one process: each <see cref="Harness"/> from <see cref="Harness.Join"/> has its
/// own lobby manager, servers, fleet and queue, and they share a clock and an in-process backplane. Every
/// cross-node operation goes through a backplane call, serialized as it would be on the wire.
/// </summary>
public sealed class FleetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (Harness A, Harness B) TwoNodes(int lobbiesEach = 1, RealtimeOptions? options = null)
    {
        var a = new Harness(null, options, new Harness().Server("a1", lobbiesEach));
        var b = a.Join("node-2", servers: a.Server("b1", lobbiesEach));
        return (a, b);
    }

    private static async Task HeartbeatAsync(params Harness[] nodes)
    {
        foreach (var node in nodes)
            await node.Fleet.HeartbeatAsync(Ct);
    }

    [Fact]
    public async Task Placement_spreads_across_nodes_and_any_node_finds_lists_and_seats_any_lobby()
    {
        var (a, b) = TwoNodes();
        var servers = await a.Fleet.ServersAsync(Ct);
        Assert.Equal(["a1@node-1", "b1@node-2"], servers.Select(s => $"{s.Status.ServerId}@{s.NodeId}").Order());

        var first = await a.Fleet.CreateAsync(new CreateLobbyRequest("test", LobbyVisibility.Public, LobbyOrigin.Hosted), [], Ct);
        var second = await a.Fleet.CreateAsync(new CreateLobbyRequest("test", LobbyVisibility.Public, LobbyOrigin.Hosted), [], Ct);
        Assert.Equal(["node-1", "node-2"], new[] { first.Lobby!.NodeId, second.Lobby!.NodeId }.Order());
        Assert.Equal("no_capacity", (await a.Fleet.CreateAsync(new CreateLobbyRequest("test", LobbyVisibility.Public, LobbyOrigin.Hosted), [], Ct)).Error);

        var remote = first.Lobby.NodeId == "node-2" ? first.Lobby : second.Lobby;
        Assert.NotNull(b.Manager.Find(remote.LobbyId));
        Assert.Null(a.Manager.Find(remote.LobbyId));

        // Node 1 seats a player in node 2's lobby and reads it back exactly, by id and by join code.
        var reservation = a.Reserve(remote.LobbyId, "p").Reservation!;
        Assert.Equal("b1", reservation.ServerId);
        var found = (await a.Fleet.FindAsync(remote.LobbyId, Ct))!;
        Assert.Equal(1, found.Seats);
        Assert.Equal(remote.LobbyId, await a.Fleet.FindByCodeAsync(remote.JoinCode!.ToLowerInvariant(), Ct));
        Assert.Equal(2, (await a.Fleet.ListLobbiesAsync("test", Ct)).Count);
        Assert.Equal(2, (await b.Fleet.ListLobbiesAsync(null, Ct)).Count);

        // The connection itself is admitted only by the node hosting the lobby.
        Assert.Equal("invalid_reservation", a.Manager.Admit(reservation.Token, new FakeConnection()).Error);
        Assert.True(b.Manager.Admit(reservation.Token, new FakeConnection()).Accepted);
    }

    [Fact]
    public async Task A_server_that_filled_since_its_heartbeat_refuses_and_placement_moves_on()
    {
        var (a, b) = TwoNodes(lobbiesEach: 2);
        a.Manager.Create(new CreateLobbyRequest("test", LobbyVisibility.Public, LobbyOrigin.Hosted));
        await HeartbeatAsync(a, b);
        Assert.Equal(1, (await a.Fleet.ServersAsync(Ct)).Single(s => s.Status.ServerId == "a1").Status.Lobbies);

        // Node 2 fills without telling anyone; node 1 still sees it empty and least loaded.
        b.Manager.Create(new CreateLobbyRequest("test", LobbyVisibility.Public, LobbyOrigin.Hosted));
        b.Manager.Create(new CreateLobbyRequest("test", LobbyVisibility.Public, LobbyOrigin.Hosted));

        var placed = await a.Fleet.CreateAsync(new CreateLobbyRequest("test", LobbyVisibility.Public, LobbyOrigin.Hosted), [], Ct);
        Assert.Equal("a1", placed.Lobby!.ServerId);
        Assert.Equal(2, b.Manager.Balancer.Servers[0].Status().Lobbies);
    }

    [Fact]
    public async Task A_player_holds_one_seat_across_the_fleet_until_the_match_is_over()
    {
        var (a, b) = TwoNodes();
        var here = a.CreateLobby();
        var there = (await a.Fleet.CreateAsync(new CreateLobbyRequest("test", LobbyVisibility.Public, LobbyOrigin.Hosted), [], Ct)).Lobby!;
        Assert.Equal("node-2", there.NodeId);
        var p = a.Connect(here, "p");
        a.Connect(here, "q");

        Assert.Equal("already_in_lobby", b.Reserve(there.LobbyId, "p").Error);
        Assert.Equal("already_in_lobby", (await b.Queue.EnqueueAsync(Harness.Player("p"), "test", null, Ct)).Error);
        Assert.Equal(here.Id, await b.Fleet.LobbyOfAsync("p", Ct));

        a.Run(TimeSpan.FromSeconds(3.1));
        p.Send("""{"t":"end"}""");
        a.Step();
        await a.Fleet.FlushAsync();
        Assert.Null(await b.Fleet.LobbyOfAsync("p", Ct));
        Assert.NotNull(b.Reserve(there.LobbyId, "p").Reservation);
    }

    [Fact]
    public async Task Racing_reservations_on_two_nodes_leave_each_player_exactly_one_seat()
    {
        var big = new RecordingGame(new GameRules { MinPlayers = 2, MaxPlayers = 64 });
        var a = new Harness(big, null, new Harness().Server("a1", 1));
        var b = a.Join("node-2", servers: a.Server("b1", 1));
        var here = a.CreateLobby();
        var there = (await a.Fleet.CreateAsync(new CreateLobbyRequest("test", LobbyVisibility.Public, LobbyOrigin.Hosted), [], Ct)).Lobby!;

        var players = Enumerable.Range(0, 40).Select(i => $"racer-{i}").ToList();
        await Task.WhenAll(players.SelectMany(p => new[]
        {
            Task.Run(() => a.Fleet.ReserveAsync(here.Id, Harness.Player(p), Ct), Ct),
            Task.Run(() => a.Fleet.ReserveAsync(there.LobbyId, Harness.Player(p), Ct), Ct),
        }));
        // A seat that lost its claim is given back at the lobby's next step.
        a.Step();
        b.Step();

        var thereLobby = b.Manager.Find(there.LobbyId)!;
        Assert.All(players, p => Assert.True(here.HasSeat(p) ^ thereLobby.HasSeat(p), p));
        foreach (var p in players)
            Assert.Equal(here.HasSeat(p) ? here.Id : thereLobby.Id, await b.Backplane.SeatOfAsync(p, Ct));
    }

    [Fact]
    public async Task Players_who_queue_through_different_nodes_meet_in_one_lobby()
    {
        var (a, b) = TwoNodes();
        var ticketA = (await a.Queue.EnqueueAsync(Harness.Player("p"), "test", null, Ct)).Ticket!;
        var ticketB = (await b.Queue.EnqueueAsync(Harness.Player("q"), "test", null, Ct)).Ticket!;
        Assert.Equal("already_queued", (await b.Queue.EnqueueAsync(Harness.Player("p"), "test", null, Ct)).Error);
        var waiting = a.Queue.WaitAsync(ticketB.TicketId, TimeSpan.FromSeconds(30), Ct);

        // Every node runs the cycle; one leads the game and the other stands by.
        await b.Queue.RunCycleAsync(a.Time.GetUtcNow(), Ct);
        await a.Queue.RunCycleAsync(a.Time.GetUtcNow(), Ct);

        var p = (await a.Queue.GetAsync(ticketA.TicketId, Ct))!;
        var q = (await waiting.WaitAsync(TimeSpan.FromSeconds(5), Ct))!;
        Assert.Equal(TicketState.Matched, p.State);
        Assert.Equal(TicketState.Matched, q.State);
        Assert.Equal(p.Reservation!.LobbyId, q.Reservation!.LobbyId);
        Assert.Single(a.Manager.Lobbies.Concat(b.Manager.Lobbies));
    }

    [Fact]
    public async Task When_the_matchmaker_stops_renewing_another_node_takes_over()
    {
        var (a, b) = TwoNodes();
        await a.Queue.RunCycleAsync(a.Time.GetUtcNow(), Ct);
        await a.Queue.EnqueueAsync(Harness.Player("p"), "test", null, Ct);
        var q = (await b.Queue.EnqueueAsync(Harness.Player("q"), "test", null, Ct)).Ticket!;

        // Node 1 holds the game's lease, so node 2 matches nothing while node 1 is silent.
        await b.Queue.RunCycleAsync(a.Time.GetUtcNow(), Ct);
        Assert.Equal(TicketState.Queued, (await b.Queue.GetAsync(q.TicketId, Ct))!.State);

        a.Time.Advance(TimeSpan.FromSeconds(3));
        await b.Queue.RunCycleAsync(a.Time.GetUtcNow(), Ct);
        Assert.Equal(TicketState.Matched, (await b.Queue.GetAsync(q.TicketId, Ct))!.State);
    }

    [Fact]
    public async Task A_cancel_that_beats_the_match_gets_the_seat_back()
    {
        Harness? h = null;
        var game = new RecordingGame { Matchmaking = new CancellingStrategy(() => h!, "p") };
        h = new Harness(game);
        var p = (await h.Queue.EnqueueAsync(Harness.Player("p"), "test", null, Ct)).Ticket!;
        var q = (await h.Queue.EnqueueAsync(Harness.Player("q"), "test", null, Ct)).Ticket!;

        await h.Queue.RunCycleAsync(h.Time.GetUtcNow(), Ct);
        Assert.Equal(TicketState.Cancelled, (await h.Queue.GetAsync(p.TicketId, Ct))!.State);
        var matched = (await h.Queue.GetAsync(q.TicketId, Ct))!;
        Assert.Equal(TicketState.Matched, matched.State);
        var lobby = h.Manager.Find(matched.Reservation!.LobbyId)!;

        h.Step();
        await h.Fleet.FlushAsync();
        Assert.False(lobby.HasSeat("p"));
        Assert.Null(await h.Fleet.LobbyOfAsync("p", Ct));
        Assert.Null((await h.Queue.EnqueueAsync(Harness.Player("p"), "test", null, Ct)).Error);
    }

    [Fact]
    public async Task A_node_that_stops_heartbeating_drops_out_and_its_players_may_seat_elsewhere()
    {
        var (a, b) = TwoNodes(lobbiesEach: 2);
        var there = (await b.Fleet.CreateAsync(new CreateLobbyRequest("test", LobbyVisibility.Public, LobbyOrigin.Hosted, ServerId: "b1"), [], Ct)).Lobby!;
        Assert.Equal("node-2", there.NodeId);
        Assert.NotNull(b.Reserve(there.LobbyId, "p").Reservation);
        var here = a.CreateLobby();
        Assert.Equal("already_in_lobby", a.Reserve(here, "p").Error);

        // Node 2 freezes. Node 1 keeps heartbeating; after the node lifetime node 2's records are gone.
        a.Time.Advance(TimeSpan.FromSeconds(6));
        await HeartbeatAsync(a);
        Assert.Equal(["a1"], (await a.Fleet.ServersAsync(Ct)).Select(s => s.Status.ServerId));
        Assert.Null(await a.Fleet.FindAsync(there.LobbyId, Ct));

        // The claim itself lasts longer, but it names a lobby nobody publishes any more.
        Assert.Equal(there.LobbyId, await a.Backplane.SeatOfAsync("p", Ct));
        Assert.NotNull(a.Reserve(here, "p").Reservation);
        Assert.Equal(here.Id, await a.Backplane.SeatOfAsync("p", Ct));
    }

    [Fact]
    public async Task Operators_drain_list_and_close_on_whichever_node_owns_the_server_or_lobby()
    {
        var (a, b) = TwoNodes(lobbiesEach: 2);
        a.CreateLobby();
        var there = (await b.Fleet.CreateAsync(new CreateLobbyRequest("test", LobbyVisibility.Private, LobbyOrigin.Hosted), [], Ct)).Lobby!;

        Assert.Equal(LobbyServerState.Draining, await a.Fleet.SetServerStateAsync("b1", drain: true, Ct));
        Assert.Equal(LobbyServerState.Draining, b.Manager.Server("b1")!.State);
        Assert.Null(await a.Fleet.SetServerStateAsync("nope", drain: true, Ct));

        var hosted = await a.Fleet.HostedLobbiesAsync(Ct);
        Assert.Single(hosted["node-1"]!);
        Assert.Equal(there.LobbyId, Assert.Single(hosted["node-2"]!).LobbyId);

        Assert.True(await a.Fleet.CloseAsync(there.LobbyId, "closed_by_operator", Ct));
        b.Step();
        await b.Fleet.FlushAsync();
        Assert.Null(b.Manager.Find(there.LobbyId));
        Assert.Null(await a.Fleet.FindAsync(there.LobbyId, Ct));
        Assert.False(await a.Fleet.CloseAsync(there.LobbyId, "again", Ct));
    }

    [Fact]
    public void A_fleet_refuses_a_duplicate_node_a_duplicate_server_and_a_distributed_server_without_a_public_url()
    {
        var time = new FakeTimeProvider();
        var shared = new DistributedBackplane(new LocalBackplane(time));

        RealtimeFleet Node(string nodeId, string serverId, string? publicUrl)
        {
            var options = new RealtimeOptions { Enabled = true };
            var manager = new LobbyManager(new RealtimeGames([new RecordingGame()]),
                new LobbyBalancer([new LobbyServer(serverId, "local", 1, publicUrl, time, NullLogger.Instance)], new LeastLoadedPlacement(), true),
                options, nodeId, time, NullLoggerFactory.Instance, runLoops: false);
            var fleet = new RealtimeFleet(manager, shared, RealtimeNode.Create(nodeId), options, time, NullLogger.Instance);
            fleet.StartAsync(Ct).GetAwaiter().GetResult();
            return fleet;
        }

        Node("n1", "s1", "wss://n1.example.com");
        Assert.Contains("needs a PublicUrl", Assert.Throws<InvalidOperationException>(() => Node("n2", "s2", null)).Message);
        Assert.Contains("already hosted by node 'n1'", Assert.Throws<InvalidOperationException>(() => Node("n2", "s1", "wss://n2.example.com")).Message);
        Assert.Contains("already used by another live node", Assert.Throws<InvalidOperationException>(() => Node("n1", "s9", "wss://n1b.example.com")).Message);
    }

    /// <summary>Cancels one ticket while the strategy runs, then matches everyone: a cancel landing between the
    /// matchmaker's read and its settle.</summary>
    private sealed class CancellingStrategy(Func<Harness> harness, string victim) : IMatchmakingStrategy
    {
        public IReadOnlyList<MatchDecision> Decide(MatchmakingPool pool)
        {
            var ticket = pool.Tickets.Single(t => t.PlayerId == victim);
            Assert.True(Harness.Sync(harness().Queue.CancelAsync(ticket.TicketId, victim, TestContext.Current.CancellationToken)));
            return [new NewLobbyDecision(null, pool.Tickets.Select(t => t.TicketId).ToList())];
        }
    }

    /// <summary>The in-process backplane presenting itself as shared, to exercise the distributed-fleet checks.</summary>
    private sealed class DistributedBackplane(LocalBackplane inner) : IRealtimeBackplane
    {
        public bool IsDistributed => true;
        public ValueTask<bool> PublishNodeAsync(BackplaneNode node, TimeSpan ttl, CancellationToken ct) => inner.PublishNodeAsync(node, ttl, ct);
        public ValueTask RemoveNodeAsync(string nodeId, string instanceId, CancellationToken ct) => inner.RemoveNodeAsync(nodeId, instanceId, ct);
        public ValueTask<IReadOnlyList<BackplaneNode>> ListNodesAsync(CancellationToken ct) => inner.ListNodesAsync(ct);
        public ValueTask PublishLobbiesAsync(IReadOnlyList<BackplaneLobby> lobbies, TimeSpan ttl, TimeSpan seatTtl, CancellationToken ct) => inner.PublishLobbiesAsync(lobbies, ttl, seatTtl, ct);
        public ValueTask RemoveLobbyAsync(BackplaneLobby lobby, CancellationToken ct) => inner.RemoveLobbyAsync(lobby, ct);
        public ValueTask<BackplaneLobby?> FindLobbyAsync(string lobbyId, CancellationToken ct) => inner.FindLobbyAsync(lobbyId, ct);
        public ValueTask<IReadOnlyList<BackplaneLobby>> ListLobbiesAsync(string gameId, CancellationToken ct) => inner.ListLobbiesAsync(gameId, ct);
        public ValueTask<bool> ClaimJoinCodeAsync(string code, string lobbyId, TimeSpan ttl, CancellationToken ct) => inner.ClaimJoinCodeAsync(code, lobbyId, ttl, ct);
        public ValueTask<string?> FindJoinCodeAsync(string code, CancellationToken ct) => inner.FindJoinCodeAsync(code, ct);
        public ValueTask<string> ClaimSeatAsync(string playerId, string lobbyId, TimeSpan ttl, CancellationToken ct) => inner.ClaimSeatAsync(playerId, lobbyId, ttl, ct);
        public ValueTask<bool> TransferSeatAsync(string playerId, string fromLobbyId, string toLobbyId, TimeSpan ttl, CancellationToken ct) => inner.TransferSeatAsync(playerId, fromLobbyId, toLobbyId, ttl, ct);
        public ValueTask ReleaseSeatAsync(string playerId, string lobbyId, CancellationToken ct) => inner.ReleaseSeatAsync(playerId, lobbyId, ct);
        public ValueTask<string?> SeatOfAsync(string playerId, CancellationToken ct) => inner.SeatOfAsync(playerId, ct);
        public ValueTask<BackplaneTicket> AddTicketAsync(BackplaneTicket ticket, TimeSpan ttl, CancellationToken ct) => inner.AddTicketAsync(ticket, ttl, ct);
        public ValueTask<BackplaneTicket?> GetTicketAsync(string ticketId, CancellationToken ct) => inner.GetTicketAsync(ticketId, ct);
        public ValueTask<IReadOnlyList<BackplaneTicket>> QueuedTicketsAsync(string gameId, CancellationToken ct) => inner.QueuedTicketsAsync(gameId, ct);
        public ValueTask<bool> UpdateTicketAsync(BackplaneTicket ticket, TimeSpan ttl, CancellationToken ct) => inner.UpdateTicketAsync(ticket, ttl, ct);
        public ValueTask<IAsyncDisposable> SubscribeTicketsAsync(Action<string> settled, CancellationToken ct) => inner.SubscribeTicketsAsync(settled, ct);
        public ValueTask<bool> HoldLeaseAsync(string name, string owner, TimeSpan ttl, CancellationToken ct) => inner.HoldLeaseAsync(name, owner, ttl, ct);
        public ValueTask ReleaseLeaseAsync(string name, string owner, CancellationToken ct) => inner.ReleaseLeaseAsync(name, owner, ct);
        public ValueTask<IAsyncDisposable> ServeAsync(string nodeId, Func<string, CancellationToken, Task<string>> handler, CancellationToken ct) => inner.ServeAsync(nodeId, handler, ct);
        public Task<string> CallAsync(string nodeId, string request, TimeSpan timeout, CancellationToken ct) => inner.CallAsync(nodeId, request, timeout, ct);
    }
}
