using System.Collections.Concurrent;
using System.Security.Cryptography;
using Burbuja.Realtime.Abstractions;
using Burbuja.Realtime.Fleet;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Burbuja.Realtime.Lobbies;

public sealed record CreateLobbyRequest(
    string GameId,
    LobbyVisibility Visibility,
    LobbyOrigin Origin,
    string? Region = null,
    IReadOnlyDictionary<string, string>? Settings = null);

public sealed record LobbyCreation(Lobby? Lobby, string? Error);

/// <summary>
/// The lifecycle manager of every lobby on this node: creation through the balancer, join codes, seat
/// reservations, admission of transport connections, and release of capacity when a lobby closes.
///
/// <para><b>One lobby per player.</b> A player holds at most one seat across the node. A finished match
/// releases its players at once (they may queue again from the results screen); a seat in a lobby that is
/// still playing must be left before another can be taken.</para>
/// </summary>
public sealed class LobbyManager : IRealtimeGateway, ILobbyObserver
{
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private readonly ConcurrentDictionary<string, Lobby> _lobbies = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _occupancy = new(StringComparer.Ordinal);
    private readonly RealtimeGames _games;
    private readonly LobbyBalancer _balancer;
    private readonly LobbyLimits _limits;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _logs;
    private readonly ILogger _log;
    private readonly bool _runLoops;

    public LobbyManager(
        RealtimeGames games,
        LobbyBalancer balancer,
        IOptions<RealtimeOptions> options,
        TimeProvider time,
        ILoggerFactory logs)
        : this(games, balancer, options.Value, time, logs, runLoops: true)
    {
    }

    /// <summary>With <paramref name="runLoops"/> false no lobby loop runs; the caller drives
    /// <see cref="StepAll"/> with its own clock.</summary>
    internal LobbyManager(
        RealtimeGames games,
        LobbyBalancer balancer,
        RealtimeOptions options,
        TimeProvider time,
        ILoggerFactory logs,
        bool runLoops)
    {
        _games = games;
        _balancer = balancer;
        _limits = LobbyLimits.From(options);
        _time = time;
        _logs = logs;
        _log = logs.CreateLogger<LobbyManager>();
        _runLoops = runLoops;
    }

    public RealtimeGames Games => _games;

    public LobbyBalancer Balancer => _balancer;

    public IEnumerable<Lobby> Lobbies => _lobbies.Values;

    public Lobby? Find(string? lobbyId) =>
        lobbyId is not null && _lobbies.TryGetValue(lobbyId, out var lobby) ? lobby : null;

    public Lobby? FindByCode(string? code) =>
        code is not null && _codes.TryGetValue(code.Trim().ToUpperInvariant(), out var id) ? Find(id) : null;

    /// <summary>The lobby holding this player's seat, if it is still being played.</summary>
    public Lobby? LobbyOf(string playerId) =>
        _occupancy.TryGetValue(playerId, out var id) && Find(id) is { State: < LobbyState.Ended } lobby
        && lobby.HasSeat(playerId) ? lobby : null;

    /// <summary>Places a new dedicated lobby. Hosted lobbies get a shareable join code; matchmade ones do not.</summary>
    public LobbyCreation Create(CreateLobbyRequest request)
    {
        var game = _games.Find(request.GameId);
        if (game is null)
            return new LobbyCreation(null, "unknown_game");
        var server = _balancer.Allocate(new PlacementRequest(game.Id, request.Region, game.Rules.MaxPlayers));
        if (server is null)
            return new LobbyCreation(null, "no_capacity");

        var id = "lby-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var code = request.Origin == LobbyOrigin.Hosted ? ClaimCode(id) : null;
        try
        {
            var settings = request.Settings ?? new Dictionary<string, string>();
            var lobby = new Lobby(
                new LobbySpec(id, game, server.Id, server.Region, server.PublicUrl, request.Visibility, request.Origin,
                    code, settings, RandomNumberGenerator.GetInt32(int.MaxValue)),
                _limits, _time.GetUtcNow(), this, _logs.CreateLogger<Lobby>());
            _lobbies[id] = lobby;
            server.Host(lobby, _runLoops);
            _log.LogInformation("Lobby {LobbyId} ({GameId}, {Origin}) placed on {ServerId} by {Strategy}.",
                id, game.Id, request.Origin, server.Id, _balancer.Strategy.Name);
            return new LobbyCreation(lobby, null);
        }
        catch (Exception ex)
        {
            server.Release();
            if (code is not null)
                _codes.TryRemove(code, out _);
            _log.LogError(ex, "Game {GameId} failed to create a session.", game.Id);
            return new LobbyCreation(null, "game_failed");
        }
    }

    /// <summary>Reserves a seat in <paramref name="lobby"/>, refusing a player already playing elsewhere.</summary>
    public ReservationResult Reserve(Lobby lobby, PlayerIdentity identity)
    {
        var playerId = identity.PlayerId;
        var held = _occupancy.GetOrAdd(playerId, lobby.Id);
        if (held != lobby.Id)
        {
            if (LobbyOf(playerId) is not null)
                return ReservationResult.Fail("already_in_lobby");
            // The recorded lobby closed or finished: take over the stale entry, unless another reservation
            // raced us to it.
            if (!_occupancy.TryUpdate(playerId, lobby.Id, held))
                return ReservationResult.Fail("already_in_lobby");
        }

        var result = lobby.Reserve(identity, _time.GetUtcNow());
        if (result.Reservation is null)
            _occupancy.TryRemove(new KeyValuePair<string, string>(playerId, lobby.Id));
        return result;
    }

    /// <summary>Frees a player's seat at the lobby's next step.</summary>
    public bool Leave(Lobby lobby, string playerId)
    {
        if (!lobby.HasSeat(playerId))
            return false;
        lobby.RequestLeave(playerId);
        return true;
    }

    public GatewayAdmission Admit(string reservationToken, IPlayerConnection connection)
    {
        var dot = reservationToken?.IndexOf('.') ?? -1;
        if (dot <= 0 || Find(reservationToken![..dot]) is not { } lobby)
            return GatewayAdmission.Refused("invalid_reservation");
        return lobby.Admit(reservationToken, connection, _time.GetUtcNow());
    }

    /// <summary>Public matchmade lobbies of <paramref name="gameId"/> that can still take a player.</summary>
    public IReadOnlyList<BackfillCandidate> BackfillCandidates(string gameId) =>
        _lobbies.Values
            .Where(l => l.GameId == gameId && l.Visibility == LobbyVisibility.Public && l.Origin == LobbyOrigin.Matchmaking)
            .Select(l => l.Snapshot())
            .Where(s => s.OpenSeats > 0 && (s.State is LobbyState.Open or LobbyState.Countdown
                || (s.State == LobbyState.Running && _games.Find(s.GameId)!.Rules.AllowJoinInProgress)))
            .Select(s => new BackfillCandidate(s.LobbyId, s.Region, s.State, s.OpenSeats, s.AverageSkill))
            .ToList();

    public LobbyServer? Server(string serverId) => _balancer.Servers.FirstOrDefault(s => s.Id == serverId);

    /// <summary>Steps every lobby once. Only for hosts that run without loops (tests).</summary>
    internal void StepAll(DateTimeOffset now)
    {
        foreach (var lobby in _lobbies.Values.ToList())
            lobby.Step(now);
    }

    internal async Task ShutdownAsync()
    {
        foreach (var server in _balancer.Servers)
            await server.StopAsync();
    }

    void ILobbyObserver.SeatReleased(Lobby lobby, string playerId) =>
        _occupancy.TryRemove(new KeyValuePair<string, string>(playerId, lobby.Id));

    void ILobbyObserver.Closed(Lobby lobby)
    {
        if (!_lobbies.TryRemove(new KeyValuePair<string, Lobby>(lobby.Id, lobby)))
            return;
        if (lobby.JoinCode is { } code)
            _codes.TryRemove(code, out _);
        if (Server(lobby.ServerId) is { } server)
        {
            server.Forget(lobby);
            server.Release();
        }
    }

    private string ClaimCode(string lobbyId)
    {
        while (true)
        {
            var code = string.Create(6, 0, static (span, _) =>
            {
                for (var i = 0; i < span.Length; i++)
                    span[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
            });
            if (_codes.TryAdd(code, lobbyId))
                return code;
        }
    }
}
