using Burbuja.Realtime.Abstractions;
using Burbuja.Realtime.Fleet;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Burbuja.Realtime.Tests;

/// <summary>
/// What every <see cref="IRealtimeBackplane"/> must do, run against each implementation. Expiry is tested with
/// <see cref="Ttl"/> and <see cref="ExpireAsync"/>: a fake clock for the in-process backplane, a real wait for a
/// shared one.
/// </summary>
public abstract class BackplaneContractTests
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A fresh, isolated backplane. Two calls in one test must share state, like two nodes would.</summary>
    protected abstract IRealtimeBackplane Backplane { get; }

    /// <summary>A lifetime short enough to wait out.</summary>
    protected virtual TimeSpan Ttl => TimeSpan.FromSeconds(1);

    /// <summary>Lets every entry written with <see cref="Ttl"/> expire.</summary>
    protected abstract Task ExpireAsync();

    private static readonly TimeSpan Long = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task Nodes_are_listed_until_they_expire_and_a_node_id_has_one_owner()
    {
        var bp = Backplane;
        Assert.True(await bp.PublishNodeAsync(new BackplaneNode("n1", "i1", "{\"a\":1}"), Ttl, Ct));
        Assert.True(await bp.PublishNodeAsync(new BackplaneNode("n2", "i2", "{}"), Long, Ct));
        Assert.True(await bp.PublishNodeAsync(new BackplaneNode("n1", "i1", "{\"a\":2}"), Ttl, Ct));
        Assert.False(await bp.PublishNodeAsync(new BackplaneNode("n1", "other", "{}"), Ttl, Ct));

        var nodes = await bp.ListNodesAsync(Ct);
        Assert.Equal(["n1", "n2"], nodes.Select(n => n.NodeId).Order());
        Assert.Equal("{\"a\":2}", nodes.Single(n => n.NodeId == "n1").Payload);

        // Only the owning instance removes a record.
        await bp.RemoveNodeAsync("n2", "not-i2", Ct);
        Assert.Equal(2, (await bp.ListNodesAsync(Ct)).Count);
        await bp.RemoveNodeAsync("n2", "i2", Ct);

        await ExpireAsync();
        Assert.Empty(await bp.ListNodesAsync(Ct));
        Assert.True(await bp.PublishNodeAsync(new BackplaneNode("n1", "other", "{}"), Ttl, Ct));
    }

    [Fact]
    public async Task Lobbies_are_found_listed_by_game_and_removed_with_their_join_code()
    {
        var bp = Backplane;
        var listed = new BackplaneLobby("l1", "n1", "g", true, "ABC123", [], "{\"x\":1}");
        var hidden = new BackplaneLobby("l2", "n1", "g", false, null, [], "{}");
        var other = new BackplaneLobby("l3", "n2", "h", true, null, [], "{}");
        Assert.True(await bp.ClaimJoinCodeAsync("ABC123", "l1", Long, Ct));
        Assert.True(await bp.ClaimJoinCodeAsync("ABC123", "l1", Long, Ct));
        Assert.False(await bp.ClaimJoinCodeAsync("ABC123", "l9", Long, Ct));
        await bp.PublishLobbiesAsync([listed, hidden, other], Long, Long, Ct);

        Assert.Equal("{\"x\":1}", (await bp.FindLobbyAsync("l1", Ct))!.Payload);
        Assert.Equal("n2", (await bp.FindLobbyAsync("l3", Ct))!.NodeId);
        Assert.Equal(["l1"], (await bp.ListLobbiesAsync("g", Ct)).Select(l => l.LobbyId));
        Assert.Equal(["l3"], (await bp.ListLobbiesAsync("h", Ct)).Select(l => l.LobbyId));
        Assert.Equal("l1", await bp.FindJoinCodeAsync("ABC123", Ct));

        // A finished lobby is republished unlisted.
        await bp.PublishLobbiesAsync([listed with { Listed = false }], Long, Long, Ct);
        Assert.Empty(await bp.ListLobbiesAsync("g", Ct));

        await bp.RemoveLobbyAsync(listed, Ct);
        Assert.Null(await bp.FindLobbyAsync("l1", Ct));
        Assert.Null(await bp.FindJoinCodeAsync("ABC123", Ct));
        Assert.True(await bp.ClaimJoinCodeAsync("ABC123", "l9", Long, Ct));
    }

    [Fact]
    public async Task Lobby_records_and_codes_expire_unless_republished()
    {
        var bp = Backplane;
        var lobby = new BackplaneLobby("l1", "n1", "g", true, "CODE22", [], "{}");
        Assert.True(await bp.ClaimJoinCodeAsync("CODE22", "l1", Ttl, Ct));
        await bp.PublishLobbiesAsync([lobby], Ttl, Long, Ct);
        await ExpireAsync();
        Assert.Null(await bp.FindLobbyAsync("l1", Ct));
        Assert.Null(await bp.FindJoinCodeAsync("CODE22", Ct));
        Assert.Empty(await bp.ListLobbiesAsync("g", Ct));
    }

    [Fact]
    public async Task A_player_holds_one_seat_and_only_its_lobby_releases_it()
    {
        var bp = Backplane;
        Assert.Equal("l1", await bp.ClaimSeatAsync("p", "l1", Long, Ct));
        Assert.Equal("l1", await bp.ClaimSeatAsync("p", "l1", Long, Ct));
        Assert.Equal("l1", await bp.ClaimSeatAsync("p", "l2", Long, Ct));
        Assert.Equal("l1", await bp.SeatOfAsync("p", Ct));

        await bp.ReleaseSeatAsync("p", "l2", Ct);
        Assert.Equal("l1", await bp.SeatOfAsync("p", Ct));

        Assert.False(await bp.TransferSeatAsync("p", "l9", "l2", Long, Ct));
        Assert.True(await bp.TransferSeatAsync("p", "l1", "l2", Long, Ct));
        Assert.Equal("l2", await bp.SeatOfAsync("p", Ct));

        await bp.ReleaseSeatAsync("p", "l2", Ct);
        Assert.Null(await bp.SeatOfAsync("p", Ct));
        Assert.False(await bp.TransferSeatAsync("p", "l2", "l3", Long, Ct));
        Assert.Equal("l3", await bp.ClaimSeatAsync("p", "l3", Long, Ct));
    }

    [Fact]
    public async Task Concurrent_claims_for_one_player_have_exactly_one_winner()
    {
        var bp = Backplane;
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
            Task.Run(async () => (Lobby: $"l{i}", Holder: await bp.ClaimSeatAsync("racer", $"l{i}", Long, Ct)), Ct)));
        var winners = results.Where(r => r.Lobby == r.Holder).ToList();
        Assert.Single(winners);
        Assert.All(results, r => Assert.Equal(winners[0].Lobby, r.Holder));
    }

    [Fact]
    public async Task Publishing_a_lobby_refreshes_its_seats_but_never_revives_a_released_one()
    {
        var bp = Backplane;
        Assert.Equal("l1", await bp.ClaimSeatAsync("kept", "l1", Ttl, Ct));
        Assert.Equal("l1", await bp.ClaimSeatAsync("gone", "l1", Ttl, Ct));
        Assert.Equal("l2", await bp.ClaimSeatAsync("elsewhere", "l2", Long, Ct));
        await bp.ReleaseSeatAsync("gone", "l1", Ct);

        await bp.PublishLobbiesAsync([new BackplaneLobby("l1", "n1", "g", true, null, ["kept", "gone", "elsewhere"], "{}")], Long, Long, Ct);
        await ExpireAsync();

        Assert.Equal("l1", await bp.SeatOfAsync("kept", Ct));
        Assert.Null(await bp.SeatOfAsync("gone", Ct));
        Assert.Equal("l2", await bp.SeatOfAsync("elsewhere", Ct));
    }

    [Fact]
    public async Task Seat_claims_expire()
    {
        var bp = Backplane;
        await bp.ClaimSeatAsync("p", "l1", Ttl, Ct);
        await ExpireAsync();
        Assert.Null(await bp.SeatOfAsync("p", Ct));
        Assert.Equal("l2", await bp.ClaimSeatAsync("p", "l2", Long, Ct));
    }

    [Fact]
    public async Task A_player_has_one_queued_ticket_and_settling_it_notifies_and_frees_the_player()
    {
        var bp = Backplane;
        var settled = new List<string>();
        await using var subscription = await bp.SubscribeTicketsAsync(id =>
        {
            lock (settled)
                settled.Add(id);
        }, Ct);
        var t0 = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        var first = new BackplaneTicket("t1", "p", "g", true, t0, "{\"n\":1}");
        Assert.Equal("t1", (await bp.AddTicketAsync(first, Long, Ct)).TicketId);
        Assert.Equal("t1", (await bp.AddTicketAsync(first with { TicketId = "t2" }, Long, Ct)).TicketId);
        Assert.Null(await bp.GetTicketAsync("t2", Ct));
        await bp.AddTicketAsync(new BackplaneTicket("t3", "q", "g", true, t0.AddSeconds(-1), "{}"), Long, Ct);
        await bp.AddTicketAsync(new BackplaneTicket("t4", "r", "h", true, t0, "{}"), Long, Ct);
        Assert.Equal(["t3", "t1"], (await bp.QueuedTicketsAsync("g", Ct)).Select(t => t.TicketId));

        // An update that stays queued only replaces the payload.
        Assert.True(await bp.UpdateTicketAsync(first with { Payload = "{\"n\":2}" }, Long, Ct));
        Assert.Equal("{\"n\":2}", (await bp.GetTicketAsync("t1", Ct))!.Payload);

        Assert.True(await bp.UpdateTicketAsync(first with { Queued = false, Payload = "{\"done\":true}" }, Long, Ct));
        Assert.False(await bp.UpdateTicketAsync(first with { Queued = false, Payload = "{\"late\":true}" }, Long, Ct));
        Assert.Equal("{\"done\":true}", (await bp.GetTicketAsync("t1", Ct))!.Payload);
        Assert.Equal(["t3"], (await bp.QueuedTicketsAsync("g", Ct)).Select(t => t.TicketId));
        await Eventually(() =>
        {
            lock (settled)
                return settled.Contains("t1");
        });

        Assert.Equal("t5", (await bp.AddTicketAsync(first with { TicketId = "t5" }, Long, Ct)).TicketId);
        Assert.False(await bp.UpdateTicketAsync(new BackplaneTicket("nope", "z", "g", false, t0, "{}"), Long, Ct));
    }

    [Fact]
    public async Task Tickets_expire()
    {
        var bp = Backplane;
        var ticket = new BackplaneTicket("t1", "p", "g", true, DateTimeOffset.UnixEpoch, "{}");
        await bp.AddTicketAsync(ticket, Ttl, Ct);
        await ExpireAsync();
        Assert.Null(await bp.GetTicketAsync("t1", Ct));
        Assert.Empty(await bp.QueuedTicketsAsync("g", Ct));
        Assert.Equal("t2", (await bp.AddTicketAsync(ticket with { TicketId = "t2" }, Long, Ct)).TicketId);
    }

    [Fact]
    public async Task A_lease_has_one_holder_until_it_is_released_or_expires()
    {
        var bp = Backplane;
        Assert.True(await bp.HoldLeaseAsync("mm:g", "a", Ttl, Ct));
        Assert.False(await bp.HoldLeaseAsync("mm:g", "b", Ttl, Ct));
        Assert.True(await bp.HoldLeaseAsync("mm:g", "a", Ttl, Ct));
        Assert.True(await bp.HoldLeaseAsync("mm:h", "b", Long, Ct));

        await bp.ReleaseLeaseAsync("mm:g", "b", Ct);
        Assert.False(await bp.HoldLeaseAsync("mm:g", "b", Ttl, Ct));
        await bp.ReleaseLeaseAsync("mm:g", "a", Ct);
        Assert.True(await bp.HoldLeaseAsync("mm:g", "b", Ttl, Ct));

        await ExpireAsync();
        Assert.True(await bp.HoldLeaseAsync("mm:g", "a", Ttl, Ct));
        Assert.False(await bp.HoldLeaseAsync("mm:h", "a", Ttl, Ct));
    }

    [Fact]
    public async Task A_call_reaches_the_serving_node_and_fails_cleanly_otherwise()
    {
        var bp = Backplane;
        await using (await bp.ServeAsync("n1", (request, _) => Task.FromResult("echo:" + request), Ct))
        await using (await bp.ServeAsync("bad", (_, _) => throw new InvalidOperationException("boom"), Ct))
        {
            var replies = await Task.WhenAll(Enumerable.Range(0, 20)
                .Select(i => bp.CallAsync("n1", $"{{\"i\":{i}}}", TimeSpan.FromSeconds(5), Ct)));
            Assert.Equal(Enumerable.Range(0, 20).Select(i => $"echo:{{\"i\":{i}}}"), replies);
            await Assert.ThrowsAsync<RealtimeBackplaneException>(() => bp.CallAsync("bad", "{}", TimeSpan.FromSeconds(5), Ct));
        }

        await Assert.ThrowsAsync<TimeoutException>(() => bp.CallAsync("n1", "{}", TimeSpan.FromMilliseconds(300), Ct));
        await Assert.ThrowsAsync<TimeoutException>(() => bp.CallAsync("nobody", "{}", TimeSpan.FromMilliseconds(300), Ct));
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(20, Ct);
        Assert.True(condition());
    }
}

public sealed class LocalBackplaneContractTests : BackplaneContractTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
    private readonly LocalBackplane _backplane;

    public LocalBackplaneContractTests() => _backplane = new LocalBackplane(_time);

    protected override IRealtimeBackplane Backplane => _backplane;

    protected override Task ExpireAsync()
    {
        _time.Advance(Ttl + TimeSpan.FromMilliseconds(1));
        return Task.CompletedTask;
    }
}
