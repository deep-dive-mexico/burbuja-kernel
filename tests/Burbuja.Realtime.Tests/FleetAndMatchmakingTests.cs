using Burbuja.Realtime.Abstractions;
using Burbuja.Realtime.Fleet;
using Burbuja.Realtime.Lobbies;
using Burbuja.Realtime.Matchmaking;
using Burbuja.Realtime.Players;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Burbuja.Realtime.Tests;

public sealed class BalancerTests
{
    private static readonly FakeTimeProvider Time = new();

    private static LobbyServer Server(string id, int max = 2, string region = "local") =>
        new(id, region, max, null, Time, NullLogger.Instance);

    private static string?[] Place(LobbyBalancer balancer, int count, string? region = null) =>
        Enumerable.Range(0, count).Select(_ => balancer.Allocate(new PlacementRequest("g", region, 4))?.Id).ToArray();

    [Fact]
    public void Least_loaded_spreads_lobbies_across_servers()
    {
        var balancer = new LobbyBalancer([Server("a"), Server("b")], new LeastLoadedPlacement(), true);
        Assert.Equal(new[] { "a", "b", "a", "b", null }, Place(balancer, 5));
    }

    [Fact]
    public void Packed_fills_one_server_before_the_next()
    {
        var balancer = new LobbyBalancer([Server("a"), Server("b")], new PackedPlacement(), true);
        Assert.Equal(new[] { "a", "a", "b", "b", null }, Place(balancer, 5));
    }

    [Fact]
    public void Draining_servers_take_nothing_new_and_a_released_slot_is_reused()
    {
        var a = Server("a", 1);
        var b = Server("b", 1);
        var balancer = new LobbyBalancer([a, b], new LeastLoadedPlacement(), true);
        a.Drain();
        Assert.Equal(new[] { "b", null }, Place(balancer, 2));
        b.Release();
        a.Activate();
        Assert.Equal(new[] { "a", "b" }, Place(balancer, 2));
    }

    [Fact]
    public void Region_is_preferred_and_fallback_follows_configuration()
    {
        var eu = Server("eu-1", 1, "eu");
        var us = Server("us-1", 1, "us");
        var strict = new LobbyBalancer([eu, us], new LeastLoadedPlacement(), allowCrossRegion: false);
        Assert.Equal(new[] { "us-1", null }, Place(strict, 2, "us"));

        var lenient = new LobbyBalancer([Server("eu-2", 1, "eu"), Server("us-2", 1, "us")], new LeastLoadedPlacement(), true);
        Assert.Equal(new[] { "us-2", "eu-2" }, Place(lenient, 2, "us"));
    }

    [Fact]
    public void Concurrent_placements_never_exceed_capacity()
    {
        var servers = Enumerable.Range(0, 4).Select(i => Server($"s{i}", 25)).ToList();
        var balancer = new LobbyBalancer(servers, new LeastLoadedPlacement(), true);
        var placed = 0;
        Parallel.For(0, 400, _ =>
        {
            if (balancer.Allocate(new PlacementRequest("g", null, 4)) is not null)
                Interlocked.Increment(ref placed);
        });
        Assert.Equal(100, placed);
        Assert.All(servers, s => Assert.Equal(25, s.Status().Lobbies));
    }
}

public sealed class SkillWindowMatchmakerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly GameRules Rules = new() { MinPlayers = 2, MaxPlayers = 3 };
    private readonly SkillWindowMatchmaker _matchmaker = new(new MatchmakingOptions
    {
        SkillWindow = 100, SkillWindowGrowthPerSecond = 50, MaxSkillWindow = 1000,
    });

    private static MatchTicket Ticket(string id, int skill, double waitedSeconds = 0, string? region = null) =>
        new(id, "p-" + id, "g", region, skill, Now.AddSeconds(-waitedSeconds));

    private IReadOnlyList<MatchDecision> Decide(IReadOnlyList<MatchTicket> tickets, params BackfillCandidate[] backfill) =>
        _matchmaker.Decide(new MatchmakingPool("g", Rules, tickets, backfill, Now));

    [Fact]
    public void Close_players_form_a_lobby_and_a_lone_player_waits()
    {
        var decision = Assert.IsType<NewLobbyDecision>(Assert.Single(Decide([Ticket("a", 1000, 2), Ticket("b", 1050)])));
        Assert.Equal(new[] { "a", "b" }, decision.TicketIds);
        Assert.Empty(Decide([Ticket("a", 1000, 30)]));
    }

    [Fact]
    public void A_wide_skill_gap_matches_only_after_both_have_waited()
    {
        Assert.Empty(Decide([Ticket("a", 1000), Ticket("b", 1400)]));
        // window = 100 + 50/s * 7 s = 450 for both, and the gap is 400.
        Assert.Single(Decide([Ticket("a", 1000, 7), Ticket("b", 1400, 7)]));
    }

    [Fact]
    public void Groups_stop_at_max_players_and_respect_regions()
    {
        var decisions = Decide([Ticket("a", 1000, 3, "eu"), Ticket("b", 1000, 2, "us"), Ticket("c", 1000, 1, "eu"),
            Ticket("d", 1000, 1), Ticket("e", 1000, 0, "eu")]);
        var first = Assert.IsType<NewLobbyDecision>(decisions[0]);
        Assert.Equal("eu", first.Region);
        Assert.Equal(new[] { "a", "c", "d" }, first.TicketIds);
        Assert.DoesNotContain(decisions, d => d.TicketIds.Contains("b") && d.TicketIds.Contains("e"));
    }

    [Fact]
    public void Backfill_prefers_the_fullest_compatible_lobby()
    {
        var decisions = Decide([Ticket("a", 1000)],
            new BackfillCandidate("roomy", "local", LobbyState.Running, 3, 1000),
            new BackfillCandidate("almost", "local", LobbyState.Countdown, 1, 1020),
            new BackfillCandidate("pros", "local", LobbyState.Open, 1, 2000));
        var backfill = Assert.IsType<BackfillDecision>(Assert.Single(decisions));
        Assert.Equal("almost", backfill.LobbyId);
    }
}

public sealed class MatchmakingQueueTests
{
    [Fact]
    public void Queued_players_are_matched_into_one_placed_lobby_and_later_players_backfill_it()
    {
        var h = new Harness();
        var a = h.Queue.Enqueue(Harness.Player("a"), "test", null).Ticket!;
        h.Queue.RunCycle(h.Time.GetUtcNow());
        Assert.Equal(TicketState.Queued, h.Queue.Get(a.TicketId)!.State);

        var b = h.Queue.Enqueue(Harness.Player("b"), "test", null).Ticket!;
        h.Queue.RunCycle(h.Time.GetUtcNow());
        var matchedA = h.Queue.Get(a.TicketId)!;
        var matchedB = h.Queue.Get(b.TicketId)!;
        Assert.Equal(TicketState.Matched, matchedA.State);
        Assert.Equal(matchedA.Reservation!.LobbyId, matchedB.Reservation!.LobbyId);
        var lobby = h.Manager.Find(matchedA.Reservation.LobbyId)!;
        Assert.Equal(LobbyOrigin.Matchmaking, lobby.Origin);

        var c = h.Queue.Enqueue(Harness.Player("c"), "test", null).Ticket!;
        h.Queue.RunCycle(h.Time.GetUtcNow());
        Assert.Equal(lobby.Id, h.Queue.Get(c.TicketId)!.Reservation!.LobbyId);
        Assert.Equal(3, lobby.SeatCount);
        Assert.Single(h.Manager.Lobbies);
    }

    [Fact]
    public void One_ticket_per_player_cancel_and_expiry()
    {
        var h = new Harness(options: new RealtimeOptions { Enabled = true, Matchmaking = { TicketTimeoutSeconds = 10 } });
        var first = h.Queue.Enqueue(Harness.Player("a"), "test", null);
        Assert.Equal("already_queued", h.Queue.Enqueue(Harness.Player("a"), "test", null).Error);
        Assert.Equal("unknown_game", h.Queue.Enqueue(Harness.Player("z"), "nope", null).Error);
        Assert.False(h.Queue.Cancel(first.Ticket!.TicketId, "someone-else"));
        Assert.True(h.Queue.Cancel(first.Ticket.TicketId, "a"));
        Assert.Equal(TicketState.Cancelled, h.Queue.Get(first.Ticket.TicketId)!.State);

        var second = h.Queue.Enqueue(Harness.Player("a"), "test", null).Ticket!;
        h.Time.Advance(TimeSpan.FromSeconds(11));
        h.Queue.RunCycle(h.Time.GetUtcNow());
        Assert.Equal(TicketState.Expired, h.Queue.Get(second.TicketId)!.State);
    }

    [Fact]
    public void Without_capacity_tickets_wait_and_say_why()
    {
        var h = new Harness(null, null, new Harness(new RecordingGame()).Server("full", 1));
        h.CreateLobby();
        var a = h.Queue.Enqueue(Harness.Player("a"), "test", null).Ticket!;
        h.Queue.Enqueue(Harness.Player("b"), "test", null);
        h.Queue.RunCycle(h.Time.GetUtcNow());
        var waiting = h.Queue.Get(a.TicketId)!;
        Assert.Equal(TicketState.Queued, waiting.State);
        Assert.Equal("waiting_for_capacity", waiting.Detail);
    }

    [Fact]
    public async Task A_long_poll_returns_as_soon_as_the_ticket_is_matched()
    {
        var h = new Harness();
        var a = h.Queue.Enqueue(Harness.Player("a"), "test", null).Ticket!;
        var wait = h.Queue.WaitAsync(a.TicketId, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.False(wait.IsCompleted);
        h.Queue.Enqueue(Harness.Player("b"), "test", null);
        h.Queue.RunCycle(h.Time.GetUtcNow());
        var settled = await wait.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(TicketState.Matched, settled!.State);
    }
}

public sealed class GuestAndRegistrationTests
{
    [Fact]
    public void Guest_tokens_round_trip_and_refuse_tampering_and_expiry()
    {
        var time = new FakeTimeProvider();
        var tokens = new GuestTokens(Options.Create(new RealtimeOptions { Guests = { TokenLifetimeMinutes = 5 } }), time);
        var (identity, token, _) = tokens.Issue("Ana");
        Assert.Equal(identity, tokens.Validate(token));
        Assert.Null(tokens.Validate(token[..^2] + (token[^2] == 'A' ? "BB" : "AA")));
        Assert.Null(tokens.Validate("not-a-token"));
        Assert.Null(new GuestTokens(Options.Create(new RealtimeOptions()), time).Validate(token));
        time.Advance(TimeSpan.FromMinutes(6));
        Assert.Null(tokens.Validate(token));
    }

    [Fact]
    public void Display_names_are_bounded_and_printable()
    {
        Assert.Equal("Ana", GuestTokens.CleanDisplayName("  Ana "));
        Assert.Null(GuestTokens.CleanDisplayName(""));
        Assert.Null(GuestTokens.CleanDisplayName(new string('x', 25)));
        Assert.Null(GuestTokens.CleanDisplayName("bad\nname"));
    }

    [Fact]
    public void Invalid_game_registrations_are_refused_together()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new RealtimeGames([
            new RecordingGame(id: "Bad Id"),
            new RecordingGame(id: "dup"),
            new RecordingGame(id: "dup"),
            new RecordingGame(new GameRules { MinPlayers = 5, MaxPlayers = 2 }, "rules"),
        ]));
        Assert.Contains("'Bad Id'", error.Message);
        Assert.Contains("registered twice", error.Message);
        Assert.Contains("MaxPlayers must be between", error.Message);
    }
}
