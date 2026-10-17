using System.Security.Cryptography;
using Burbuja.Realtime.Abstractions;
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
/// The matchmaking queue: one ticket per player, grouped every cycle by the game's
/// <see cref="IMatchmakingStrategy"/> into backfills of running matchmade lobbies or new lobbies placed by the
/// balancer. A matched ticket carries the seat reservation the player connects with.
///
/// <para>The queue is in memory on the node that hosts the lobbies; a ticket does not survive a restart, and
/// a client that loses its ticket simply queues again.</para>
/// </summary>
public sealed class MatchmakingQueue
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _tickets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _byPlayer = new(StringComparer.Ordinal);
    private readonly LobbyManager _lobbies;
    private readonly MatchmakingOptions _options;
    private readonly IMatchmakingStrategy _default;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    public MatchmakingQueue(LobbyManager lobbies, IOptions<RealtimeOptions> options, TimeProvider time, ILogger<MatchmakingQueue> log)
    {
        _lobbies = lobbies;
        _options = options.Value.Matchmaking;
        _default = new SkillWindowMatchmaker(_options);
        _time = time;
        _log = log;
    }

    public TimeSpan Interval => TimeSpan.FromMilliseconds(Math.Clamp(_options.IntervalMilliseconds, 20, 5000));

    public TicketResult Enqueue(PlayerIdentity identity, string gameId, string? region)
    {
        if (_lobbies.Games.Find(gameId) is null)
            return new TicketResult(null, "unknown_game");
        if (_lobbies.LobbyOf(identity.PlayerId) is not null)
            return new TicketResult(null, "already_in_lobby");
        lock (_gate)
        {
            if (_byPlayer.TryGetValue(identity.PlayerId, out var existing))
                return new TicketResult(_tickets[existing].View(), "already_queued");
            var now = _time.GetUtcNow();
            var ticket = new MatchTicket(
                "tkt-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(),
                identity.PlayerId, gameId, string.IsNullOrWhiteSpace(region) ? null : region.Trim(),
                PlayerAttributes.SkillOf(identity.AttributesOrEmpty), now);
            var entry = new Entry(ticket, identity) { UpdatedAt = now };
            _tickets.Add(ticket.TicketId, entry);
            _byPlayer.Add(identity.PlayerId, ticket.TicketId);
            return new TicketResult(entry.View(), null);
        }
    }

    public TicketView? Get(string ticketId)
    {
        lock (_gate)
            return _tickets.TryGetValue(ticketId, out var entry) ? entry.View() : null;
    }

    /// <summary>The ticket once it leaves <see cref="TicketState.Queued"/>, or as it is after <paramref name="wait"/>.</summary>
    public async Task<TicketView?> WaitAsync(string ticketId, TimeSpan wait, CancellationToken ct)
    {
        Task changed;
        lock (_gate)
        {
            if (!_tickets.TryGetValue(ticketId, out var entry))
                return null;
            if (entry.State != TicketState.Queued || wait <= TimeSpan.Zero)
                return entry.View();
            changed = entry.Settled.Task;
        }

        try
        {
            await changed.WaitAsync(wait, _time, ct);
        }
        catch (TimeoutException)
        {
        }

        return Get(ticketId);
    }

    public bool Cancel(string ticketId, string playerId)
    {
        lock (_gate)
        {
            if (!_tickets.TryGetValue(ticketId, out var entry) || entry.Ticket.PlayerId != playerId
                || entry.State != TicketState.Queued)
                return false;
            Settle(entry, TicketState.Cancelled, "cancelled", null, _time.GetUtcNow());
            return true;
        }
    }

    /// <summary>One matchmaking pass: expire, group per game, apply decisions, purge old finished tickets.</summary>
    public void RunCycle(DateTimeOffset now)
    {
        lock (_gate)
        {
            var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.TicketTimeoutSeconds));
            var retention = TimeSpan.FromSeconds(Math.Max(1, _options.TicketRetentionSeconds));
            foreach (var entry in _tickets.Values.ToList())
            {
                if (entry.State == TicketState.Queued && now - entry.Ticket.EnqueuedAt >= timeout)
                    Settle(entry, TicketState.Expired, "no_match_found", null, now);
                else if (entry.State != TicketState.Queued && now - entry.UpdatedAt >= retention)
                    _tickets.Remove(entry.Ticket.TicketId);
            }

            foreach (var group in _tickets.Values.Where(e => e.State == TicketState.Queued)
                         .GroupBy(e => e.Ticket.GameId).ToList())
            {
                var game = _lobbies.Games.Find(group.Key)!;
                var queued = group.ToDictionary(e => e.Ticket.TicketId, StringComparer.Ordinal);
                var pool = new MatchmakingPool(game.Id, game.Rules, group.Select(e => e.Ticket).ToList(),
                    _lobbies.BackfillCandidates(game.Id), now);
                IReadOnlyList<MatchDecision> decisions;
                try
                {
                    decisions = (game.Matchmaking ?? _default).Decide(pool);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Matchmaking strategy for {GameId} failed; its tickets stay queued.", game.Id);
                    continue;
                }

                var used = new HashSet<string>(StringComparer.Ordinal);
                foreach (var decision in decisions)
                {
                    // A strategy is game code: hold it to the contract instead of trusting it.
                    if (decision.TicketIds.Count == 0 || decision.TicketIds.Any(id => !queued.ContainsKey(id) || !used.Add(id)))
                    {
                        _log.LogWarning("Matchmaking for {GameId} returned an invalid decision; skipped.", game.Id);
                        continue;
                    }

                    Apply(game, decision, decision.TicketIds.Select(id => queued[id]).ToList(), now);
                }
            }
        }
    }

    private void Apply(IRealtimeGame game, MatchDecision decision, List<Entry> entries, DateTimeOffset now)
    {
        Lobby? lobby;
        if (decision is BackfillDecision backfill)
        {
            lobby = _lobbies.Find(backfill.LobbyId);
            if (lobby is null || lobby.GameId != game.Id || lobby.Origin != LobbyOrigin.Matchmaking
                || lobby.Visibility != LobbyVisibility.Public)
                return;
        }
        else
        {
            var created = _lobbies.Create(new CreateLobbyRequest(game.Id, LobbyVisibility.Public, LobbyOrigin.Matchmaking,
                ((NewLobbyDecision)decision).Region));
            lobby = created.Lobby;
            if (lobby is null)
            {
                foreach (var entry in entries)
                    entry.Detail = created.Error == "no_capacity" ? "waiting_for_capacity" : created.Error;
                return;
            }
        }

        foreach (var entry in entries)
        {
            var reserved = _lobbies.Reserve(lobby, entry.Identity);
            if (reserved.Reservation is { } reservation)
                Settle(entry, TicketState.Matched, null, reservation, now);
            else if (reserved.Error == "already_in_lobby")
                Settle(entry, TicketState.Cancelled, "already_in_lobby", null, now);
            // lobby_full and friends: the lobby changed since the pool was read; try again next cycle.
        }
    }

    private void Settle(Entry entry, TicketState state, string? detail, SeatReservation? reservation, DateTimeOffset now)
    {
        entry.State = state;
        entry.Detail = detail;
        entry.Reservation = reservation;
        entry.UpdatedAt = now;
        _byPlayer.Remove(entry.Ticket.PlayerId);
        entry.Settled.TrySetResult();
    }

    private sealed class Entry(MatchTicket ticket, PlayerIdentity identity)
    {
        public MatchTicket Ticket { get; } = ticket;
        public PlayerIdentity Identity { get; } = identity;
        public TicketState State { get; set; } = TicketState.Queued;
        public SeatReservation? Reservation { get; set; }
        public string? Detail { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TicketView View() => new(Ticket.TicketId, Ticket.PlayerId, Ticket.GameId, Ticket.Region, State,
            Ticket.EnqueuedAt, UpdatedAt, Reservation, Detail);
    }
}
