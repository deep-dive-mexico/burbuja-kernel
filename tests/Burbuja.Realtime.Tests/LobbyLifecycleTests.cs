using Burbuja.Realtime.Abstractions;
using Burbuja.Realtime.Lobbies;
using Xunit;

namespace Burbuja.Realtime.Tests;

public sealed class LobbyLifecycleTests
{
    [Fact]
    public void A_lobby_walks_open_countdown_running_ended_closed_and_releases_its_capacity()
    {
        var h = new Harness();
        var lobby = h.CreateLobby();
        var server = h.Manager.Server(lobby.ServerId)!;
        Assert.Equal(1, server.Status().Lobbies);

        var a = h.Connect(lobby, "a");
        h.Step();
        Assert.Equal(LobbyState.Open, lobby.State);
        var welcome = Assert.Single(a.OfType(RealtimeProtocol.Welcome));
        Assert.Equal("a", (string?)welcome["d"]!["playerId"]);
        Assert.Equal(0, (int)welcome["d"]!["slot"]!);

        var b = h.Connect(lobby, "b");
        h.Step();
        Assert.Equal(LobbyState.Countdown, lobby.State);
        Assert.Equal(new[] { "joined:a:0", "joined:b:1" }, h.Game.Sessions[0].Calls);
        Assert.Equal("countdown", (string?)b.OfType(RealtimeProtocol.Lobby).Last()["d"]!["state"]);

        h.Run(TimeSpan.FromSeconds(3));
        Assert.Equal(LobbyState.Running, lobby.State);
        Assert.Contains("started", h.Game.Sessions[0].Calls);

        // 20 Hz for one second, stepped every 50 ms: twenty fixed ticks, each broadcast to both players.
        var before = h.Game.Sessions[0].Ticks;
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(20, h.Game.Sessions[0].Ticks - before);
        Assert.True(a.OfType("state").Count() >= 20);

        a.Send("""{"t":"end"}""");
        h.Step();
        Assert.Equal(LobbyState.Ended, lobby.State);
        var ended = Assert.Single(b.OfType(RealtimeProtocol.Ended));
        Assert.Equal("won", (string?)ended["d"]!["reason"]);
        Assert.Equal("a", (string?)ended["d"]!["winners"]![0]);
        Assert.Null(h.Manager.LobbyOf("a"));

        h.Run(TimeSpan.FromSeconds(5));
        Assert.Equal(LobbyState.Closed, lobby.State);
        Assert.Equal(RealtimeCloseCode.LobbyClosed, a.ClosedWith);
        Assert.Equal(RealtimeCloseCode.LobbyClosed, b.ClosedWith);
        Assert.True(h.Game.Sessions[0].Disposed);
        Assert.Null(h.Manager.Find(lobby.Id));
        Assert.Equal(0, server.Status().Lobbies);
    }

    [Fact]
    public void Countdown_returns_to_open_when_the_lobby_drops_below_its_minimum()
    {
        var h = new Harness();
        var lobby = h.CreateLobby();
        h.Connect(lobby, "a");
        var b = h.Connect(lobby, "b");
        h.Step();
        Assert.Equal(LobbyState.Countdown, lobby.State);

        b.Send("""{"t":"sys.leave"}""");
        h.Step();
        Assert.Equal(LobbyState.Open, lobby.State);
        Assert.Equal(RealtimeCloseCode.Left, b.ClosedWith);
        Assert.Contains("left:b:Left", h.Game.Sessions[0].Calls);
        Assert.False(lobby.HasSeat("b"));
    }

    [Fact]
    public void An_unclaimed_reservation_expires_and_frees_the_seat_and_the_player()
    {
        var h = new Harness(options: new RealtimeOptions { Enabled = true, ReservationTtlSeconds = 5 });
        var lobby = h.CreateLobby();
        var reservation = h.Manager.Reserve(lobby, Harness.Player("late")).Reservation!;
        Assert.Equal(1, lobby.SeatCount);

        h.Run(TimeSpan.FromSeconds(6));
        Assert.Equal(0, lobby.SeatCount);
        Assert.Null(h.Manager.LobbyOf("late"));
        Assert.Equal("invalid_reservation", h.Manager.Admit(reservation.Token, new FakeConnection()).Error);
    }

    [Fact]
    public void A_dropped_player_keeps_their_seat_and_slot_within_the_grace_and_loses_it_after()
    {
        var h = new Harness();
        var lobby = h.CreateLobby();
        h.Connect(lobby, "a");
        var b = h.Connect(lobby, "b");
        h.Run(TimeSpan.FromSeconds(3.1));
        Assert.Equal(LobbyState.Running, lobby.State);

        b.Inbox.Disconnected();
        h.Step();
        Assert.True(lobby.HasSeat("b"));
        Assert.Contains("dropped:b", h.Game.Sessions[0].Calls);

        // Asking again is the reconnect path: same seat, same slot, a fresh token.
        var again = h.Manager.Reserve(lobby, Harness.Player("b")).Reservation!;
        var b2 = h.Admit(again);
        h.Step();
        Assert.Contains("reconnected:b", h.Game.Sessions[0].Calls);
        Assert.Equal(1, (int)Assert.Single(b2.OfType(RealtimeProtocol.Welcome))["d"]!["slot"]!);
        Assert.Equal(LobbyState.Running, lobby.State);

        b2.Inbox.Disconnected();
        h.Run(TimeSpan.FromSeconds(11));
        Assert.False(lobby.HasSeat("b"));
        Assert.Contains("left:b:Disconnected", h.Game.Sessions[0].Calls);
    }

    [Fact]
    public void A_reservation_token_is_single_use_and_a_newer_connection_replaces_the_older()
    {
        var h = new Harness();
        var lobby = h.CreateLobby();
        var first = h.Manager.Reserve(lobby, Harness.Player("a")).Reservation!;
        var a = h.Admit(first);
        Assert.Equal("invalid_reservation", h.Manager.Admit(first.Token, new FakeConnection()).Error);

        var second = h.Admit(h.Manager.Reserve(lobby, Harness.Player("a")).Reservation!);
        h.Step();
        Assert.Equal(RealtimeCloseCode.Replaced, a.ClosedWith);
        Assert.Single(second.OfType(RealtimeProtocol.Welcome));
        Assert.Single(h.Game.Sessions[0].Calls, c => c.StartsWith("joined:a", StringComparison.Ordinal));
    }

    [Fact]
    public void Inbound_messages_beyond_the_rate_are_dropped_and_reported_once()
    {
        var h = new Harness();
        var lobby = h.CreateLobby();
        var a = h.Connect(lobby, "a");
        h.Step();
        for (var i = 0; i < 50; i++)
            a.Send("""{"t":"say","d":"spam"}""");
        h.Step();

        // The inbox holds one second's allowance (10), and the bucket admits the same burst.
        Assert.Equal(10, h.Game.Sessions[0].Messages);

        // With no time passed the bucket is empty: these are dropped, and the client is told.
        for (var i = 0; i < 5; i++)
            a.Send("""{"t":"say","d":"spam"}""");
        h.Step();
        Assert.Equal(10, h.Game.Sessions[0].Messages);
        Assert.Single(a.OfType(RealtimeProtocol.Error), e => (string?)e["d"]!["code"] == "rate_limited");

        h.Time.Advance(TimeSpan.FromSeconds(1));
        for (var i = 0; i < 12; i++)
            a.Send("""{"t":"say","d":"spam"}""");
        h.Step();
        Assert.Equal(20, h.Game.Sessions[0].Messages);
    }

    [Fact]
    public void System_messages_are_answered_by_the_kernel_and_never_reach_the_game()
    {
        var h = new Harness();
        var lobby = h.CreateLobby();
        var a = h.Connect(lobby, "a");
        h.Step();
        a.Send("""{"t":"sys.ping","d":{"n":7}}""");
        a.Send("""{"t":"sys.nope"}""");
        a.Send("""not json""");
        h.Step();

        var pong = Assert.Single(a.OfType(RealtimeProtocol.Pong));
        Assert.Equal(7, (int)pong["d"]!["echo"]!["n"]!);
        Assert.Equal(new[] { "unknown_system_type", "bad_frame" },
            a.OfType(RealtimeProtocol.Error).Select(e => (string?)e["d"]!["code"]).ToArray());
        Assert.Equal(0, h.Game.Sessions[0].Messages);
    }

    [Fact]
    public void A_faulting_game_closes_only_its_own_lobby()
    {
        var h = new Harness();
        var broken = h.CreateLobby();
        var healthy = h.CreateLobby();
        var a = h.Connect(broken, "a");
        var b = h.Connect(healthy, "b");
        h.Step();

        a.Send("""{"t":"boom"}""");
        h.Step();
        Assert.Equal(LobbyState.Closed, broken.State);
        Assert.Equal(RealtimeCloseCode.InternalError, a.ClosedWith);
        Assert.Equal("faulted", (string?)Assert.Single(a.OfType(RealtimeProtocol.Ended))["d"]!["reason"]);
        Assert.Equal(LobbyState.Open, healthy.State);
        Assert.Null(b.ClosedWith);
    }

    [Fact]
    public void Seats_are_bounded_and_join_in_progress_follows_the_rules()
    {
        var game = new RecordingGame(new GameRules
        {
            MinPlayers = 2, MaxPlayers = 2, TickRate = 10, StartCountdown = TimeSpan.Zero, AllowJoinInProgress = false,
        });
        var h = new Harness(game);
        var lobby = h.CreateLobby();
        h.Connect(lobby, "a");
        h.Manager.Reserve(lobby, Harness.Player("b"));
        Assert.Equal("lobby_full", h.Manager.Reserve(lobby, Harness.Player("c")).Error);

        var open = h.CreateLobby();
        h.Connect(open, "d");
        h.Connect(open, "e");
        h.Step();
        Assert.Equal(LobbyState.Running, open.State);
        Assert.Equal("match_in_progress", h.Manager.Reserve(open, Harness.Player("f")).Error);
    }

    [Fact]
    public void A_player_holds_one_seat_until_their_match_is_over()
    {
        var h = new Harness();
        var first = h.CreateLobby();
        var second = h.CreateLobby();
        var a = h.Connect(first, "a");
        h.Connect(first, "b");
        Assert.Equal("already_in_lobby", h.Manager.Reserve(second, Harness.Player("a")).Error);
        Assert.Equal("already_in_lobby", h.Queue.Enqueue(Harness.Player("a"), "test", null).Error);

        h.Run(TimeSpan.FromSeconds(3.1));
        a.Send("""{"t":"end"}""");
        h.Step();
        Assert.Equal(LobbyState.Ended, first.State);
        Assert.NotNull(h.Manager.Reserve(second, Harness.Player("a")).Reservation);
    }

    [Fact]
    public void Kicks_and_empty_lobbies_are_handled_by_the_kernel()
    {
        var h = new Harness(options: new RealtimeOptions { Enabled = true, EmptyLobbyTimeoutSeconds = 10 });
        var lobby = h.CreateLobby();
        var a = h.Connect(lobby, "a");
        var b = h.Connect(lobby, "b");
        h.Step();
        a.Send("""{"t":"kick","d":"b"}""");
        h.Step();
        Assert.Equal(RealtimeCloseCode.Kicked, b.ClosedWith);
        Assert.Contains("left:b:Kicked", h.Game.Sessions[0].Calls);

        a.Send("""{"t":"sys.leave"}""");
        h.Run(TimeSpan.FromSeconds(9));
        Assert.Equal(LobbyState.Open, lobby.State);
        h.Run(TimeSpan.FromSeconds(2));
        Assert.Equal(LobbyState.Closed, lobby.State);
    }

    [Fact]
    public void Join_codes_find_hosted_lobbies_and_die_with_them()
    {
        var h = new Harness();
        var hosted = h.CreateLobby(LobbyVisibility.Private);
        var matchmade = h.CreateLobby(LobbyVisibility.Public, LobbyOrigin.Matchmaking);
        Assert.Matches("^[A-Z2-9]{6}$", hosted.JoinCode!);
        Assert.Null(matchmade.JoinCode);
        Assert.Same(hosted, h.Manager.FindByCode(hosted.JoinCode!.ToLowerInvariant()));

        hosted.RequestClose("test");
        h.Step();
        Assert.Null(h.Manager.FindByCode(hosted.JoinCode));
    }
}
