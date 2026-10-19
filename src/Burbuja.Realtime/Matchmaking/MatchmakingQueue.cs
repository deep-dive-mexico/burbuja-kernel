using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Burbuja.Realtime.Abstractions;
using Burbuja.Realtime.Fleet;
using Burbuja.Realtime.Lobbies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Burbuja.Realtime.Matchmaking;

/// <summary>A ticket as its owner reads it. <paramref name="Detail"/> explains a wait or a terminal state.</summary>
public sealed record TicketView(
    string TicketId,
    string PlayerId,
    string GameId,
    string? Region,
    TicketState State,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset UpdatedAt,
    SeatReservation? Reservation,
    string? Detail);

public sealed record TicketResult(TicketView? Ticket, string? Error);

/// <summary>
/// The matchmaking queue: one ticket per player across the fleet, grouped every cycle by the game's
/// <see cref="IMatchmakingStrategy"/> into backfills of running matchmade lobbies or new lobbies placed by the
/// fleet. A matched ticket carries the seat reservation the player connects with.
///
/// <para><b>One pool per game, one matchmaker per game.</b> Tickets live on the backplane, so a player may queue
/// through any node and is grouped with players who queued through others. Every node runs the cycle, but only
/// the holder of a game's lease groups that game's tickets; a node competes only for the games it registers,
/// and when the holder stops renewing, another node takes over within the lease's lifetime.</para>
///
/// <para><b>Exactly one outcome.</b> A ticket changes only while it is queued, so a cancel on one node and a
/// match on another have one winner. A match that loses to a cancel gives its seat back.</para>
/// </summary>
public sealed class MatchmakingQueue : IAsyncDisposable
{
    private const string LeasePrefix = "matchmaker:";

    private readonly RealtimeFleet _fleet;
    private readonly IRealtimeBackplane _backplane;
    private readonly MatchmakingOptions _options;
    private readonly TimeSpan _callTimeout;
    private readonly IMatchmakingStrategy _default;
    private readonly string _owner;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _waiters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _leading = new(StringComparer.Ordinal);
    private IAsyncDisposable? _subscription;

    public MatchmakingQueue(RealtimeFleet fleet, IOptions<RealtimeOptions> options, RealtimeNode node, TimeProvider time,
        ILogger<MatchmakingQueue> log)
        : this(fleet, options.Value, node.InstanceId, time, log)
    {
    }

    internal MatchmakingQueue(RealtimeFleet fleet, RealtimeOptions options, string owner, TimeProvider time, ILogger log)
    {
        _fleet = fleet;
        _backplane = fleet.Backplane;
        _options = options.Matchmaking;
        _callTimeout = TimeSpan.FromMilliseconds(Math.Max(100, options.Fleet.CallTimeoutMilliseconds));
        _default = new SkillWindowMatchmaker(_options);
        _owner = owner;
        _time = time;
        _log = log;
    }

    public TimeSpan Interval => TimeSpan.FromMilliseconds(Math.Clamp(_options.IntervalMilliseconds, 20, 5000));

    /// <summary>A matchmaker that stops renewing is replaced after this long. It outlasts a cycle whose
    /// placement waits out a call to a slow node, and the lease is renewed before every decision too.</summary>
    private TimeSpan LeaseTtl => TimeSpan.FromTicks(Math.Clamp(
        Math.Max(Interval.Ticks * 8, (_callTimeout * 2 + Interval).Ticks), TimeSpan.FromSeconds(2).Ticks, TimeSpan.FromSeconds(120).Ticks));

    private TimeSpan Timeout => TimeSpan.FromSeconds(Math.Max(1, _options.TicketTimeoutSeconds));

    private TimeSpan Retention => TimeSpan.FromSeconds(Math.Max(1, _options.TicketRetentionSeconds));

    /// <summary>A queued ticket outlives its timeout, so it expires through the matchmaker (and its owner reads
    /// why) unless no matchmaker is left at all.</summary>
    private TimeSpan QueuedTtl => Timeout + Retention + TimeSpan.FromSeconds(30);

    /// <summary>Listens for tickets settling anywhere in the fleet, so long-polls answer at once.</summary>
    public async Task StartAsync(CancellationToken ct) =>
        _subscription ??= await _backplane.SubscribeTicketsAsync(Wake, ct);

    public async ValueTask DisposeAsync()
    {
        if (_subscription is { } subscription)
        {
            _subscription = null;
            await subscription.DisposeAsync();
        }

        // Hand the games over now rather than when the leases expire.
        foreach (var game in _leading.Keys)
        {
            try
            {
                await _backplane.ReleaseLeaseAsync(LeasePrefix + game, _owner, CancellationToken.None);
            }
            catch (RealtimeBackplaneException)
            {
            }
        }
    }

    public async Task<TicketResult> EnqueueAsync(PlayerIdentity identity, string gameId, string? region, CancellationToken ct)
    {
        if (_fleet.Local.Games.Find(gameId) is null)
            return new TicketResult(null, "unknown_game");
        if (await _fleet.LobbyOfAsync(identity.PlayerId, ct) is not null)
            return new TicketResult(null, "already_in_lobby");
        var now = _time.GetUtcNow();
        var ticket = new MatchTicket(
            "tkt-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(),
            identity.PlayerId, gameId, string.IsNullOrWhiteSpace(region) ? null : region.Trim(),
            PlayerAttributes.SkillOf(identity.AttributesOrEmpty), now);
        var entry = new StoredTicket(ticket, identity, TicketState.Queued, now, null, null);
        var stored = await _backplane.AddTicketAsync(entry.Record(), QueuedTtl, ct);
        if (stored.TicketId == ticket.TicketId)
            return new TicketResult(entry.View(), null);
        return Parse(stored) is { } existing
            ? new TicketResult(existing.View(), "already_queued")
            : new TicketResult(null, "already_queued");
    }

    public async Task<TicketView?> GetAsync(string ticketId, CancellationToken ct) =>
        await _backplane.GetTicketAsync(ticketId, ct) is { } record ? Parse(record)?.View() : null;

    /// <summary>The ticket once it leaves <see cref="TicketState.Queued"/>, or as it is after <paramref name="wait"/>.</summary>
    public async Task<TicketView?> WaitAsync(string ticketId, TimeSpan wait, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + wait;
        while (true)
        {
            var waiter = _waiters.GetOrAdd(ticketId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            // Read after registering, so a settlement between the two is never missed.
            var ticket = await GetAsync(ticketId, ct);
            var remaining = deadline - _time.GetUtcNow();
            if (ticket is null || ticket.State != TicketState.Queued || remaining <= TimeSpan.Zero)
            {
                // A waiter left behind by a ticket that never settles would never be removed. Another poll of the
                // same ticket loses at most one wake and re-reads within a second.
                _waiters.TryRemove(new KeyValuePair<string, TaskCompletionSource>(ticketId, waiter));
                return ticket;
            }

            // A notification can be lost (a reconnecting subscriber), so re-read at least once a second.
            var recheck = remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1);
            await Task.WhenAny(waiter.Task, Task.Delay(recheck, _time, ct));
            ct.ThrowIfCancellationRequested();
        }
    }

    public async Task<bool> CancelAsync(string ticketId, string playerId, CancellationToken ct)
    {
        if (await _backplane.GetTicketAsync(ticketId, ct) is not { } record || Parse(record) is not { } entry
            || entry.Ticket.PlayerId != playerId || entry.State != TicketState.Queued)
            return false;
        return await SettleAsync(entry, TicketState.Cancelled, "cancelled", null, _time.GetUtcNow(), ct);
    }

    /// <summary>One matchmaking pass for every game this node leads: expire, group, apply decisions.</summary>
    public async Task RunCycleAsync(DateTimeOffset now, CancellationToken ct)
    {
        foreach (var game in _fleet.Local.Games.All)
        {
            if (!await _backplane.HoldLeaseAsync(LeasePrefix + game.Id, _owner, LeaseTtl, ct))
            {
                _leading.TryRemove(game.Id, out _);
                continue;
            }

            _leading[game.Id] = 0;
            try
            {
                await RunGameAsync(game, now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Matchmaking for {GameId} failed this cycle; its tickets stay queued.", game.Id);
            }
        }
    }

    private async Task RunGameAsync(IRealtimeGame game, DateTimeOffset now, CancellationToken ct)
    {
        var queued = new Dictionary<string, StoredTicket>(StringComparer.Ordinal);
        foreach (var record in await _backplane.QueuedTicketsAsync(game.Id, ct))
        {
            if (Parse(record) is not { State: TicketState.Queued } entry)
                continue;
            if (now - entry.Ticket.EnqueuedAt >= Timeout)
                await SettleAsync(entry, TicketState.Expired, "no_match_found", null, now, ct);
            else
                queued[entry.Ticket.TicketId] = entry;
        }

        if (queued.Count == 0)
            return;
        var pool = new MatchmakingPool(game.Id, game.Rules, queued.Values.Select(e => e.Ticket).ToList(),
            await _fleet.BackfillCandidatesAsync(game, ct), now);
        IReadOnlyList<MatchDecision> decisions;
        try
        {
            decisions = (game.Matchmaking ?? _default).Decide(pool);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Matchmaking strategy for {GameId} failed; its tickets stay queued.", game.Id);
            return;
        }

        var backfill = pool.Backfill.Select(c => c.LobbyId).ToHashSet(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var decision in decisions)
        {
            // A strategy is game code: hold it to the contract instead of trusting it.
            if (decision.TicketIds.Count == 0 || decision.TicketIds.Any(id => !queued.ContainsKey(id) || !used.Add(id))
                || (decision is BackfillDecision named && !backfill.Contains(named.LobbyId)))
            {
                _log.LogWarning("Matchmaking for {GameId} returned an invalid decision; skipped.", game.Id);
                continue;
            }

            // A slow placement can outlast the lease; stop rather than match alongside a new holder.
            if (!await _backplane.HoldLeaseAsync(LeasePrefix + game.Id, _owner, LeaseTtl, ct))
            {
                _leading.TryRemove(game.Id, out _);
                return;
            }

            await ApplyAsync(game, decision, decision.TicketIds.Select(id => queued[id]).ToList(), now, ct);
        }
    }

    private async Task ApplyAsync(IRealtimeGame game, MatchDecision decision, List<StoredTicket> entries, DateTimeOffset now,
        CancellationToken ct)
    {
        var players = entries.Select(e => e.Identity).ToList();
        string lobbyId;
        IReadOnlyList<ReservationResult> results;
        if (decision is BackfillDecision backfill)
        {
            lobbyId = backfill.LobbyId;
            results = await _fleet.ReserveAsync(lobbyId, players, ct);
        }
        else
        {
            var created = await _fleet.CreateAsync(new CreateLobbyRequest(game.Id, LobbyVisibility.Public, LobbyOrigin.Matchmaking,
                ((NewLobbyDecision)decision).Region), players, ct);
            if (created.Lobby is null)
            {
                var detail = created.Error == "no_capacity" ? "waiting_for_capacity" : created.Error;
                foreach (var entry in entries.Where(e => e.Detail != detail))
                    await _backplane.UpdateTicketAsync((entry with { Detail = detail, UpdatedAt = now }).Record(), QueuedTtl, ct);
                return;
            }

            lobbyId = created.Lobby.LobbyId;
            results = created.Reservations;
        }

        for (var i = 0; i < entries.Count && i < results.Count; i++)
        {
            var entry = entries[i];
            if (results[i].Reservation is { } reservation)
            {
                if (!await SettleAsync(entry, TicketState.Matched, null, reservation, now, ct))
                    await _fleet.LeaveAsync(lobbyId, entry.Ticket.PlayerId, ct);
            }
            else if (results[i].Error == "already_in_lobby")
            {
                await SettleAsync(entry, TicketState.Cancelled, "already_in_lobby", null, now, ct);
            }

            // lobby_full and friends: the lobby changed since the pool was read; try again next cycle.
        }
    }

    private async Task<bool> SettleAsync(StoredTicket entry, TicketState state, string? detail, SeatReservation? reservation,
        DateTimeOffset now, CancellationToken ct) =>
        await _backplane.UpdateTicketAsync(
            (entry with { State = state, Detail = detail, Reservation = reservation, UpdatedAt = now }).Record(), Retention, ct);

    private void Wake(string ticketId)
    {
        if (_waiters.TryRemove(ticketId, out var waiter))
            waiter.TrySetResult();
    }

    private StoredTicket? Parse(BackplaneTicket record)
    {
        try
        {
            return JsonSerializer.Deserialize<StoredTicket>(record.Payload, RealtimeFleet.Json);
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Ignoring unreadable ticket {TicketId}.", record.TicketId);
            return null;
        }
    }

    /// <summary>A ticket as the backplane stores it: everything its owner reads and the matchmaker needs to seat
    /// the player on any node.</summary>
    private sealed record StoredTicket(
        MatchTicket Ticket,
        PlayerIdentity Identity,
        TicketState State,
        DateTimeOffset UpdatedAt,
        SeatReservation? Reservation,
        string? Detail)
    {
        public BackplaneTicket Record() => new(Ticket.TicketId, Ticket.PlayerId, Ticket.GameId, State == TicketState.Queued,
            Ticket.EnqueuedAt, JsonSerializer.Serialize(this, RealtimeFleet.Json));

        public TicketView View() => new(Ticket.TicketId, Ticket.PlayerId, Ticket.GameId, Ticket.Region, State,
            Ticket.EnqueuedAt, UpdatedAt, Reservation, Detail);
    }
}
