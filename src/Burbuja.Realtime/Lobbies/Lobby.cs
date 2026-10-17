using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Burbuja.Realtime.Abstractions;
using Burbuja.Realtime.Transport;
using Microsoft.Extensions.Logging;

namespace Burbuja.Realtime.Lobbies;

/// <summary>Who hosts a lobby: told when a player's seat is gone and when the lobby has closed.</summary>
internal interface ILobbyObserver
{
    void SeatReleased(Lobby lobby, string playerId);

    void Closed(Lobby lobby);
}

/// <summary>
/// One dedicated lobby: its seats, its lifecycle and the loop that runs its <see cref="IGameSession"/>.
///
/// <para><b>One lock, one thread of game code.</b> Every mutation happens under the lobby's own lock, and the
/// session is only called from <see cref="Step"/>. Transports never call the session: they enqueue what they
/// received, and the next step drains it before it ticks. A reservation or an admission takes the same lock
/// briefly, so a seat count is never read half-updated, and the game sees a strictly ordered stream of joins,
/// messages, ticks and leaves without locking anything itself.</para>
///
/// <para><b>Time is passed in.</b> <see cref="Step"/> takes <c>now</c>, so the runner uses the host's
/// <see cref="TimeProvider"/> and a test drives the whole lifecycle deterministically.</para>
/// </summary>
public sealed class Lobby
{
    /// <summary>The step period for event-driven games, and the slowest step any lobby takes, so
    /// countdowns and reservation expiry stay responsive at low tick rates.</summary>
    internal static readonly TimeSpan MaxStepInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Ticks one step may run to catch up after a stall. Beyond that the lobby drops ticks rather
    /// than spiral: a late tick is worse than a skipped one in an action game.</summary>
    internal const int MaxCatchUpTicks = 5;

    private readonly object _gate = new();
    private readonly ConcurrentQueue<Inbound> _inbox = new();
    private readonly Dictionary<string, Seat> _seats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);
    private readonly List<(string PlayerId, string Reason)> _pendingKicks = [];
    private readonly ILobbyObserver _observer;
    private readonly ILogger _log;
    private readonly LobbyLimits _limits;
    private readonly IGameSession _session;
    private readonly SessionContext _context;
    private readonly string? _publicUrl;
    private readonly TimeSpan _tickInterval;
    private List<RealtimePlayer>? _roster;
    private MatchResult? _pendingEnd;
    private bool _lobbyChanged;
    private DateTimeOffset _now;
    private DateTimeOffset _nextTickAt;
    private DateTimeOffset _closeAt;
    private DateTimeOffset? _emptySince;
    private volatile LobbyState _state = LobbyState.Open;

    internal Lobby(LobbySpec spec, LobbyLimits limits, DateTimeOffset now, ILobbyObserver observer, ILogger log)
    {
        Id = spec.LobbyId;
        Game = spec.Game;
        Rules = spec.Game.Rules;
        ServerId = spec.ServerId;
        Region = spec.Region;
        Visibility = spec.Visibility;
        Origin = spec.Origin;
        JoinCode = spec.JoinCode;
        Settings = spec.Settings;
        CreatedAt = now;
        _publicUrl = spec.PublicUrl;
        _limits = limits;
        _observer = observer;
        _log = log;
        _now = now;
        _emptySince = now;
        _tickInterval = Rules.TickRate > 0 ? TimeSpan.FromSeconds(1.0 / Rules.TickRate) : TimeSpan.Zero;
        StepInterval = _tickInterval > TimeSpan.Zero && _tickInterval < MaxStepInterval ? _tickInterval : MaxStepInterval;
        _context = new SessionContext(this, new Random(spec.Seed));
        _session = spec.Game.CreateSession(new GameSessionOptions(Id, GameId, Settings, spec.Seed))
            ?? throw new InvalidOperationException($"Game '{GameId}' created no session.");
    }

    public string Id { get; }
    public IRealtimeGame Game { get; }
    public string GameId => Game.Id;
    public GameRules Rules { get; }
    public string ServerId { get; }
    public string Region { get; }
    public LobbyVisibility Visibility { get; }
    public LobbyOrigin Origin { get; }
    public string? JoinCode { get; }
    public IReadOnlyDictionary<string, string> Settings { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? CountdownEndsAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    public MatchResult? Result { get; private set; }
    public long Tick { get; private set; }

    /// <summary>The period the runner calls <see cref="Step"/> at.</summary>
    public TimeSpan StepInterval { get; }

    /// <summary>The current state; safe to read without the lock, exact only inside a snapshot.</summary>
    public LobbyState State => _state;

    // ─── called from API threads ────────────────────────────────────────────────────────────

    /// <summary>
    /// Holds a seat for <paramref name="identity"/> and returns the token to claim it. The same player asking
    /// again keeps their seat and slot and gets a fresh token — that is the reconnect path, and it supersedes
    /// any earlier unclaimed token.
    /// </summary>
    public ReservationResult Reserve(PlayerIdentity identity, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_state is LobbyState.Ended or LobbyState.Closed)
                return ReservationResult.Fail("lobby_closed");
            if (_seats.TryGetValue(identity.PlayerId, out var seat))
                return IssueToken(seat, now);
            if (_state == LobbyState.Running && !Rules.AllowJoinInProgress)
                return ReservationResult.Fail("match_in_progress");
            if (_seats.Count >= Rules.MaxPlayers)
                return ReservationResult.Fail("lobby_full");

            var slot = 0;
            while (_seats.Values.Any(s => s.Player.Slot == slot))
                slot++;
            seat = new Seat(new RealtimePlayer(identity.PlayerId, identity.DisplayName, slot, identity.AttributesOrEmpty));
            _seats.Add(identity.PlayerId, seat);
            _emptySince = null;
            return IssueToken(seat, now);
        }
    }

    /// <summary>Claims a seat with its single-use token for an opened connection.</summary>
    internal GatewayAdmission Admit(string token, IPlayerConnection connection, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_state is LobbyState.Ended or LobbyState.Closed)
                return GatewayAdmission.Refused("lobby_closed");
            if (!_tokens.Remove(token, out var playerId) || !_seats.TryGetValue(playerId, out var seat))
                return GatewayAdmission.Refused("invalid_reservation");

            seat.Token = null;
            if (seat.TokenExpiresAt <= now)
            {
                if (!seat.Joined && seat.Connection is null)
                    RemoveSeat(seat, LeaveReason.Disconnected);
                return GatewayAdmission.Refused("reservation_expired");
            }

            // A second connection for the same seat wins: the newest tab or the reconnecting client.
            var previous = seat.Connection;
            seat.Connection = connection;
            seat.Welcomed = false;
            previous?.Close(RealtimeCloseCode.Replaced, "replaced");
            var inbox = new SeatInbox(this, playerId, connection, Rules.MaxInboundMessagesPerSecond);
            _inbox.Enqueue(new Inbound(InboundKind.Connected, playerId, connection));
            return new GatewayAdmission(inbox, null, Rules.MaxMessageBytes);
        }
    }

    /// <summary>Asks the lobby to free a player's seat at its next step.</summary>
    public void RequestLeave(string playerId) => _inbox.Enqueue(new Inbound(InboundKind.Leave, playerId, null));

    /// <summary>Asks the lobby to end its match (if any) and close at its next step.</summary>
    public void RequestClose(string reason) =>
        _inbox.Enqueue(new Inbound(InboundKind.Close, "", null) { Reason = reason });

    /// <summary>Seats held, counting unclaimed reservations.</summary>
    public int SeatCount
    {
        get
        {
            lock (_gate)
                return _seats.Count;
        }
    }

    public bool HasSeat(string playerId)
    {
        lock (_gate)
            return _seats.ContainsKey(playerId);
    }

    public LobbySnapshot Snapshot()
    {
        lock (_gate)
        {
            var members = _seats.Values.Where(s => s.Joined).OrderBy(s => s.Player.Slot)
                .Select(s => new LobbyMember(s.Player.PlayerId, s.Player.DisplayName, s.Player.Slot, s.Live))
                .ToList();
            var skill = _seats.Count == 0
                ? PlayerAttributes.DefaultSkill
                : _seats.Values.Average(s => PlayerAttributes.SkillOf(s.Player.Attributes));
            return new LobbySnapshot(Id, GameId, ServerId, Region, _state, Visibility, Origin, JoinCode, CreatedAt,
                CountdownEndsAt, StartedAt, _seats.Count, Rules.MaxPlayers, skill, members, Result);
        }
    }

    /// <summary>Ends the match and closes now, from the host's shutdown path rather than the loop.</summary>
    internal void Shutdown(string reason, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_state == LobbyState.Closed)
                return;
            _now = now;
            try
            {
                End(MatchResult.Without(reason), now);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Lobby {LobbyId} failed while ending for shutdown.", Id);
            }

            CloseCore(RealtimeCloseCode.GoingAway, reason, faulted: false);
        }
    }

    // ─── the loop ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One pass of the lobby: expire reservations, drain inbound, run due ticks, apply lifecycle transitions
    /// and flush the coalesced lobby update. Returns false once the lobby has closed. Any exception — the
    /// game's or the kernel's — ends this lobby only.
    /// </summary>
    public bool Step(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_state == LobbyState.Closed)
                return false;
            _now = now;
            try
            {
                ExpireReservations(now);
                Drain(now);
                ApplyPending(now);
                ExpireDisconnected(now);
                Advance(now);
                ApplyPending(now);
                FlushLobby();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Lobby {LobbyId} ({GameId}) faulted; closing it.", Id, GameId);
                CloseCore(RealtimeCloseCode.InternalError, "lobby_faulted", faulted: true);
            }

            return _state != LobbyState.Closed;
        }
    }

    private void ExpireReservations(DateTimeOffset now)
    {
        foreach (var seat in _seats.Values.Where(s => s.Token is not null && s.TokenExpiresAt <= now).ToList())
        {
            _tokens.Remove(seat.Token!);
            seat.Token = null;
            if (!seat.Joined && seat.Connection is null)
                RemoveSeat(seat, LeaveReason.Disconnected);
        }
    }

    private void ExpireDisconnected(DateTimeOffset now)
    {
        foreach (var seat in _seats.Values
                     .Where(s => s.Joined && s.Connection is null && now - s.DisconnectedAt >= Rules.ReconnectGrace)
                     .ToList())
            RemoveSeat(seat, LeaveReason.Disconnected);
    }

    private void Drain(DateTimeOffset now)
    {
        while (_state != LobbyState.Closed && _inbox.TryDequeue(out var item))
        {
            item.Source?.Consumed();
            if (item.Kind == InboundKind.Close)
            {
                End(MatchResult.Without(item.Reason ?? "closed"), now);
                CloseCore(RealtimeCloseCode.LobbyClosed, item.Reason ?? "closed", faulted: false);
                return;
            }

            if (!_seats.TryGetValue(item.PlayerId, out var seat))
                continue;
            if (item.Kind == InboundKind.Leave)
            {
                RemoveSeat(seat, LeaveReason.Left);
                continue;
            }

            // Anything from a connection the seat no longer has is stale: a replaced tab, a late close.
            if (!ReferenceEquals(seat.Connection, item.Connection))
                continue;
            switch (item.Kind)
            {
                case InboundKind.Connected:
                    OnConnected(seat, now);
                    break;
                case InboundKind.Frame:
                    OnFrame(seat, item.Frame!, now);
                    break;
                case InboundKind.Disconnected:
                    OnDisconnected(seat, now);
                    break;
            }
        }
    }

    private void OnConnected(Seat seat, DateTimeOffset now)
    {
        seat.Welcomed = true;
        seat.DisconnectedAt = null;
        seat.Allowance = Rules.MaxInboundMessagesPerSecond;
        seat.AllowanceAt = now;
        var rejoin = seat.Joined;
        if (!rejoin)
        {
            seat.Joined = true;
            _roster = null;
        }

        SendTo(seat, RealtimeProtocol.Welcome, new
        {
            playerId = seat.Player.PlayerId,
            slot = seat.Player.Slot,
            tickRate = Rules.TickRate,
            serverTime = now.ToUnixTimeMilliseconds(),
            lobby = LobbyPayload(),
        });
        _lobbyChanged = true;
        if (rejoin)
            _session.OnPlayerConnectionChanged(_context, seat.Player, true);
        else if (_state < LobbyState.Ended)
            _session.OnPlayerJoined(_context, seat.Player);
    }

    private void OnDisconnected(Seat seat, DateTimeOffset now)
    {
        seat.Connection = null;
        seat.Welcomed = false;
        if (seat.Joined && _state < LobbyState.Ended && Rules.ReconnectGrace > TimeSpan.Zero)
        {
            seat.DisconnectedAt = now;
            _lobbyChanged = true;
            _session.OnPlayerConnectionChanged(_context, seat.Player, false);
        }
        else
        {
            RemoveSeat(seat, LeaveReason.Disconnected);
        }
    }

    private void OnFrame(Seat seat, byte[] frame, DateTimeOffset now)
    {
        if (!seat.Live)
            return;

        // A token bucket: a burst up to one second's allowance, refilled continuously. Excess is dropped, not
        // queued — a queued input from a flooding client would arrive late and still be applied.
        var rate = Rules.MaxInboundMessagesPerSecond;
        seat.Allowance = Math.Min(rate, seat.Allowance + (now - seat.AllowanceAt).TotalSeconds * rate);
        seat.AllowanceAt = now;
        if (seat.Allowance < 1)
        {
            if (now - seat.LastRateNotice >= TimeSpan.FromSeconds(1))
            {
                seat.LastRateNotice = now;
                SendTo(seat, RealtimeProtocol.Error, new { code = "rate_limited" });
            }

            return;
        }

        seat.Allowance -= 1;
        if (!Frames.TryDecode(frame, out var type, out var data, out var sequence))
        {
            SendTo(seat, RealtimeProtocol.Error, new { code = "bad_frame" });
            return;
        }

        if (RealtimeProtocol.IsSystemType(type))
        {
            switch (type)
            {
                case RealtimeProtocol.Ping:
                    SendTo(seat, RealtimeProtocol.Pong, new
                    {
                        echo = data.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : data,
                        serverTime = now.ToUnixTimeMilliseconds(),
                    });
                    break;
                case RealtimeProtocol.Leave:
                    RemoveSeat(seat, LeaveReason.Left);
                    break;
                default:
                    SendTo(seat, RealtimeProtocol.Error, new { code = "unknown_system_type", type });
                    break;
            }

            return;
        }

        if (_state < LobbyState.Ended)
            _session.OnMessage(_context, seat.Player, new InboundMessage(type, data, sequence, now));
    }

    private void Advance(DateTimeOffset now)
    {
        var connected = _seats.Values.Count(s => s.Live);
        switch (_state)
        {
            case LobbyState.Open when connected >= Rules.MinPlayers:
                if (Rules.StartCountdown <= TimeSpan.Zero)
                {
                    Start(now);
                }
                else
                {
                    _state = LobbyState.Countdown;
                    CountdownEndsAt = now + Rules.StartCountdown;
                    _lobbyChanged = true;
                }

                break;
            case LobbyState.Countdown when connected < Rules.MinPlayers:
                _state = LobbyState.Open;
                CountdownEndsAt = null;
                _lobbyChanged = true;
                break;
            case LobbyState.Countdown when now >= CountdownEndsAt:
                Start(now);
                break;
            case LobbyState.Running when _seats.Count == 0:
                End(MatchResult.Without("abandoned"), now);
                break;
            case LobbyState.Running when now - StartedAt >= Rules.MaxMatchDuration:
                End(MatchResult.Without("time_limit"), now);
                break;
            case LobbyState.Running:
                RunTicks(now);
                break;
            case LobbyState.Ended when now >= _closeAt || connected == 0:
                CloseCore(RealtimeCloseCode.LobbyClosed, "match_over", faulted: false);
                return;
        }

        if (_state is LobbyState.Open or LobbyState.Countdown)
        {
            if (_seats.Count > 0)
                _emptySince = null;
            else if (now - (_emptySince ??= now) >= _limits.EmptyTimeout)
                CloseCore(RealtimeCloseCode.LobbyClosed, "empty", faulted: false);
        }
    }

    private void Start(DateTimeOffset now)
    {
        _state = LobbyState.Running;
        StartedAt = now;
        CountdownEndsAt = null;
        Tick = 0;
        _nextTickAt = now + _tickInterval;
        _lobbyChanged = true;
        FlushLobby();
        _log.LogInformation("Lobby {LobbyId} ({GameId}) started on {ServerId} with {Players} players.",
            Id, GameId, ServerId, _seats.Count);
        _session.OnMatchStarted(_context);
    }

    private void RunTicks(DateTimeOffset now)
    {
        if (_tickInterval <= TimeSpan.Zero)
            return;
        var ran = 0;
        while (now >= _nextTickAt && ran < MaxCatchUpTicks && _pendingEnd is null && _pendingKicks.Count == 0)
        {
            Tick++;
            _session.OnTick(_context);
            _nextTickAt += _tickInterval;
            ran++;
        }

        if (now >= _nextTickAt && _pendingEnd is null && _pendingKicks.Count == 0)
            _nextTickAt = now + _tickInterval;
    }

    private void ApplyPending(DateTimeOffset now)
    {
        if (_pendingKicks.Count > 0)
        {
            var kicks = _pendingKicks.ToList();
            _pendingKicks.Clear();
            foreach (var (playerId, reason) in kicks)
                if (_seats.TryGetValue(playerId, out var seat))
                    RemoveSeat(seat, LeaveReason.Kicked, reason);
        }

        if (_pendingEnd is { } result)
        {
            _pendingEnd = null;
            End(result, now);
        }
    }

    private void End(MatchResult result, DateTimeOffset now)
    {
        if (_state is LobbyState.Ended or LobbyState.Closed)
            return;
        _state = LobbyState.Ended;
        EndedAt = now;
        Result = result;
        CountdownEndsAt = null;
        _closeAt = now + Rules.ResultsLinger;
        _lobbyChanged = true;
        _log.LogInformation("Lobby {LobbyId} ({GameId}) ended: {Reason}.", Id, GameId, result.Reason);
        _session.OnMatchEnded(_context, result);
        Broadcast(RealtimeProtocol.Ended, EndedPayload(result), null);

        // The seats stay for the results screen, but nobody is held by a finished match: each player may
        // queue or join elsewhere now, and no new token is issued for this lobby.
        foreach (var token in _tokens.Keys.ToList())
            _tokens.Remove(token);
        foreach (var seat in _seats.Values)
        {
            seat.Token = null;
            _observer.SeatReleased(this, seat.Player.PlayerId);
        }
    }

    private void CloseCore(RealtimeCloseCode code, string reason, bool faulted)
    {
        if (_state == LobbyState.Closed)
            return;
        if (faulted && _state < LobbyState.Ended)
        {
            Result = MatchResult.Without("faulted");
            Broadcast(RealtimeProtocol.Ended, EndedPayload(Result), null);
        }

        _state = LobbyState.Closed;
        EndedAt ??= _now;
        foreach (var seat in _seats.Values)
        {
            seat.Connection?.Close(code, reason);
            seat.Connection = null;
            _observer.SeatReleased(this, seat.Player.PlayerId);
        }

        _seats.Clear();
        _tokens.Clear();
        _roster = null;
        while (_inbox.TryDequeue(out _))
        {
        }

        if (_session is IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Lobby {LobbyId} session failed to dispose.", Id);
            }
        }

        _log.LogInformation("Lobby {LobbyId} ({GameId}) closed: {Reason}.", Id, GameId, reason);
        _observer.Closed(this);
    }

    private void RemoveSeat(Seat seat, LeaveReason reason, string? detail = null)
    {
        var playerId = seat.Player.PlayerId;
        _seats.Remove(playerId);
        if (seat.Token is not null)
            _tokens.Remove(seat.Token);
        _roster = null;
        _lobbyChanged = true;
        var code = reason switch
        {
            LeaveReason.Kicked => RealtimeCloseCode.Kicked,
            LeaveReason.Left => RealtimeCloseCode.Left,
            _ => RealtimeCloseCode.LobbyClosed,
        };
        seat.Connection?.Close(code, detail ?? reason.ToString().ToLowerInvariant());
        seat.Connection = null;
        if (_seats.Count == 0)
            _emptySince ??= _now;
        _observer.SeatReleased(this, playerId);
        if (seat.Joined && _state < LobbyState.Ended)
            _session.OnPlayerLeft(_context, seat.Player, reason);
    }

    private ReservationResult IssueToken(Seat seat, DateTimeOffset now)
    {
        if (seat.Token is not null)
            _tokens.Remove(seat.Token);
        var token = Id + "." + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        seat.Token = token;
        seat.TokenExpiresAt = now + _limits.ReservationTtl;
        _tokens[token] = seat.Player.PlayerId;
        var path = RealtimeProtocol.ConnectPath + "?token=" + Uri.EscapeDataString(token);
        var url = string.IsNullOrWhiteSpace(_publicUrl) ? path : _publicUrl.TrimEnd('/') + path;
        return new ReservationResult(
            new SeatReservation(token, Id, ServerId, seat.Player.PlayerId, seat.TokenExpiresAt, url), null);
    }

    // ─── outbound ────────────────────────────────────────────────────────────────────────────

    private void FlushLobby()
    {
        if (!_lobbyChanged || _state == LobbyState.Closed)
            return;
        _lobbyChanged = false;
        Broadcast(RealtimeProtocol.Lobby, LobbyPayload(), null);
    }

    private object LobbyPayload() => new
    {
        lobbyId = Id,
        gameId = GameId,
        serverId = ServerId,
        region = Region,
        state = _state.ToString().ToLowerInvariant(),
        minPlayers = Rules.MinPlayers,
        maxPlayers = Rules.MaxPlayers,
        countdownEndsAt = CountdownEndsAt?.ToUnixTimeMilliseconds(),
        startedAt = StartedAt?.ToUnixTimeMilliseconds(),
        players = Roster.Select(p => new
        {
            playerId = p.PlayerId,
            displayName = p.DisplayName,
            slot = p.Slot,
            connected = _seats.TryGetValue(p.PlayerId, out var s) && s.Live,
        }),
    };

    private static object EndedPayload(MatchResult result) =>
        new { reason = result.Reason, winners = result.Winners, details = result.Details };

    private void SendTo<T>(Seat seat, string type, T payload)
    {
        if (seat.Connection is { } connection)
            connection.TrySend(Frames.Encode(type, Tick, payload));
    }

    private void Broadcast<T>(string type, T payload, string? exceptPlayerId)
    {
        byte[]? frame = null;
        foreach (var seat in _seats.Values)
        {
            if (!seat.Live || string.Equals(seat.Player.PlayerId, exceptPlayerId, StringComparison.Ordinal))
                continue;
            frame ??= Frames.Encode(type, Tick, payload);
            seat.Connection!.TrySend(frame);
        }
    }

    private IReadOnlyList<RealtimePlayer> Roster =>
        _roster ??= _seats.Values.Where(s => s.Joined).Select(s => s.Player).OrderBy(p => p.Slot).ToList();

    private void Enqueue(Inbound item) => _inbox.Enqueue(item);

    // ─── types ───────────────────────────────────────────────────────────────────────────────

    private sealed class Seat(RealtimePlayer player)
    {
        public RealtimePlayer Player { get; } = player;
        public string? Token { get; set; }
        public DateTimeOffset TokenExpiresAt { get; set; }
        public IPlayerConnection? Connection { get; set; }

        /// <summary>The connection has had its welcome and receives broadcasts.</summary>
        public bool Welcomed { get; set; }

        /// <summary>The session has been told about this player.</summary>
        public bool Joined { get; set; }

        public DateTimeOffset? DisconnectedAt { get; set; }
        public double Allowance { get; set; }
        public DateTimeOffset AllowanceAt { get; set; }
        public DateTimeOffset LastRateNotice { get; set; }
        public bool Live => Connection is not null && Welcomed;
    }

    private enum InboundKind
    {
        Connected,
        Frame,
        Disconnected,
        Leave,
        Close,
    }

    private sealed record Inbound(InboundKind Kind, string PlayerId, IPlayerConnection? Connection)
    {
        public byte[]? Frame { get; init; }
        public string? Reason { get; init; }
        public SeatInbox? Source { get; init; }
    }

    /// <summary>A transport's handle on one admitted connection. Bounds what a client may have queued
    /// between two steps to one second's allowance, so a flood costs the lobby nothing but a counter.</summary>
    private sealed class SeatInbox(Lobby lobby, string playerId, IPlayerConnection connection, int maxQueued) : IConnectionInbox
    {
        private int _queued;
        private int _gone;

        public void Receive(ReadOnlySpan<byte> utf8Frame)
        {
            if (Volatile.Read(ref _gone) != 0)
                return;
            if (Interlocked.Increment(ref _queued) > maxQueued)
            {
                Interlocked.Decrement(ref _queued);
                return;
            }

            lobby.Enqueue(new Inbound(InboundKind.Frame, playerId, connection) { Frame = utf8Frame.ToArray(), Source = this });
        }

        public void Disconnected()
        {
            if (Interlocked.Exchange(ref _gone, 1) == 0)
                lobby.Enqueue(new Inbound(InboundKind.Disconnected, playerId, connection));
        }

        public void Consumed() => Interlocked.Decrement(ref _queued);
    }

    private sealed class SessionContext(Lobby lobby, Random random) : ISessionContext
    {
        public string LobbyId => lobby.Id;
        public string GameId => lobby.GameId;
        public LobbyState State => lobby._state;
        public IReadOnlyDictionary<string, string> Settings => lobby.Settings;
        public IReadOnlyList<RealtimePlayer> Players => lobby.Roster;
        public long Tick => lobby.Tick;
        public TimeSpan TickDelta => lobby._tickInterval;
        public DateTimeOffset Now => lobby._now;
        public TimeSpan Elapsed => lobby.StartedAt is { } started ? lobby._now - started : TimeSpan.Zero;
        public Random Random { get; } = random;

        public bool IsConnected(string playerId) => lobby._seats.TryGetValue(playerId, out var seat) && seat.Live;

        public void Send<T>(string playerId, string type, T payload)
        {
            Guard(type);
            if (lobby._seats.TryGetValue(playerId, out var seat) && seat.Live)
                lobby.SendTo(seat, type, payload);
        }

        public void Broadcast<T>(string type, T payload, string? exceptPlayerId = null)
        {
            Guard(type);
            lobby.Broadcast(type, payload, exceptPlayerId);
        }

        public void EndMatch(MatchResult result)
        {
            ArgumentNullException.ThrowIfNull(result);
            if (lobby._state < LobbyState.Ended)
                lobby._pendingEnd ??= result;
        }

        public void Kick(string playerId, string reason) => lobby._pendingKicks.Add((playerId, reason));

        private static void Guard(string type)
        {
            if (string.IsNullOrEmpty(type) || RealtimeProtocol.IsSystemType(type))
                throw new ArgumentException($"'{type}' is not a game message type; 'sys.' types belong to the kernel.", nameof(type));
        }
    }
}
