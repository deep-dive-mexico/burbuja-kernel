using System.Collections.Concurrent;
using System.Security.Cryptography;
using Burbuja.Realtime.Abstractions;
using Burbuja.Realtime.Fleet;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Burbuja.Realtime.Lobbies;

/// <param name="ServerId">Place on this server only. The fleet sets it once it has chosen a server, which may
/// be on another node; null lets this node's balancer choose among its own servers.</param>
/// <param name="LobbyId">The new lobby's id; null generates one. The fleet chooses it before asking another node,
/// so a call that times out can still close whatever it created.</param>
public sealed record CreateLobbyRequest(
    string GameId,
    LobbyVisibility Visibility,
    LobbyOrigin Origin,
    string? Region = null,
    IReadOnlyDictionary<string, string>? Settings = null,
    string? ServerId = null,
    string? LobbyId = null);

public sealed record LobbyCreation(Lobby? Lobby, string? Error);

/// <summary>
/// The lifecycle manager of every lobby on this node: creation on one of its servers, join codes, seat
/// reservations, admission of transport connections, and release of capacity when a lobby closes. It knows
/// nothing of other nodes; <see cref="Fleet.RealtimeFleet"/> routes requests here and keeps the fleet-wide
/// facts, such as one seat per player, from the events this manager raises.
/// </summary>
public sealed class LobbyManager : IRealtimeGateway, ILobbyObserver
{
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private readonly ConcurrentDictionary<string, Lobby> _lobbies = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _codes = new(StringComparer.Ordinal);
    private readonly RealtimeGames _games;
    private readonly LobbyBalancer _balancer;
    private readonly LobbyLimits _limits;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _logs;
    private readonly ILogger _log;
    private readonly bool _runLoops;
    private readonly SessionResources _resources;

    public LobbyManager(
        RealtimeGames games,
        LobbyBalancer balancer,
        IOptions<RealtimeOptions> options,
        RealtimeNode node,
        TimeProvider time,
        ILoggerFactory logs,
        IEnumerable<ISessionResourceAdmission>? admission = null)
        : this(games, balancer, options.Value, node.NodeId, time, logs, runLoops: true, admission)
    {
    }

    /// <summary>With <paramref name="runLoops"/> false no lobby loop runs; the caller drives
    /// <see cref="StepAll"/> with its own clock.</summary>
    internal LobbyManager(
        RealtimeGames games,
        LobbyBalancer balancer,
        RealtimeOptions options,
        string nodeId,
        TimeProvider time,
        ILoggerFactory logs,
        bool runLoops,
        IEnumerable<ISessionResourceAdmission>? admission = null)
    {
        NodeId = nodeId;
        _games = games;
        _balancer = balancer;
        _limits = LobbyLimits.From(options);
        _time = time;
        _logs = logs;
        _log = logs.CreateLogger<LobbyManager>();
        _runLoops = runLoops;
        _resources = new SessionResources(options.Resources, admission);
    }

    /// <summary>Raised when a lobby gives up a player's seat: they left, were removed, or the match ended. Called
    /// from the lobby's loop under its lock, so a handler must not block.</summary>
    public event Action<Lobby, string>? SeatReleased;

    /// <summary>Raised once when a lobby has closed and released its server slot. Same rules as
    /// <see cref="SeatReleased"/>.</summary>
    public event Action<Lobby>? LobbyClosed;

    public string NodeId { get; }

    public IReadOnlyDictionary<string, int> ResourceLimits => _resources.Limits;
    public IReadOnlyDictionary<string, int> AvailableResources => _resources.Available;

    public RealtimeGames Games => _games;

    public LobbyBalancer Balancer => _balancer;

    public IEnumerable<Lobby> Lobbies => _lobbies.Values;

    public Lobby? Find(string? lobbyId) =>
        lobbyId is not null && _lobbies.TryGetValue(lobbyId, out var lobby) ? lobby : null;

    public Lobby? FindByCode(string? code) =>
        code is not null && _codes.TryGetValue(code.Trim().ToUpperInvariant(), out var id) ? Find(id) : null;

    /// <summary>A fresh lobby id.</summary>
    public static string NewLobbyId() => "lby-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    /// <summary>A random six-character join code from an alphabet without look-alike characters.</summary>
    public static string NewJoinCode() => string.Create(6, 0, static (span, _) =>
    {
        for (var i = 0; i < span.Length; i++)
            span[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
    });

    /// <summary>
    /// Places a new dedicated lobby on one of this node's servers. Hosted lobbies get a shareable join code:
    /// <paramref name="joinCode"/> when the fleet has already claimed one, otherwise one unique on this node.
    /// Matchmade lobbies have none.
    /// </summary>
    public LobbyCreation Create(CreateLobbyRequest request, string? joinCode = null)
    {
        var game = _games.Find(request.GameId);
        if (game is null)
            return new LobbyCreation(null, "unknown_game");
        LobbyServer? server;
        if (request.ServerId is { } pinned)
            server = Server(pinned) is { } named && named.TryAllocate(game.Id) ? named : null;
        else
            server = _balancer.Allocate(new PlacementRequest(game.Id, request.Region, game.Rules.MaxPlayers));
        if (server is null)
            return new LobbyCreation(null, "no_capacity");

        var id = request.LobbyId ?? NewLobbyId();
        if (_lobbies.ContainsKey(id))
        {
            server.Release();
            return new LobbyCreation(null, "lobby_exists");
        }

        var code = request.Origin != LobbyOrigin.Hosted ? null
            : joinCode is not null && _codes.TryAdd(joinCode, id) ? joinCode
            : ClaimCode(id);
        try
        {
            var settings = request.Settings ?? new Dictionary<string, string>();
            var lobby = new Lobby(
                new LobbySpec(id, game, NodeId, server.Id, server.Region, server.PublicUrl, request.Visibility, request.Origin,
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

    /// <summary>Reserves a seat in <paramref name="lobby"/>. One seat per player is the fleet's to enforce:
    /// reserve through <see cref="Fleet.RealtimeFleet.ReserveAsync"/>, which claims it first.</summary>
    internal ReservationResult Reserve(Lobby lobby, PlayerIdentity identity) => lobby.Reserve(identity, _time.GetUtcNow());

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

    void ILobbyObserver.SeatReleased(Lobby lobby, string playerId) => SeatReleased?.Invoke(lobby, playerId);

    bool ILobbyObserver.TryReserveResource(Lobby lobby, string resource, int units) =>
        _lobbies.TryGetValue(lobby.Id, out var owned) && ReferenceEquals(owned, lobby)
        && _resources.TrySet(lobby.Id, resource, units, Server(lobby.ServerId)?.State == LobbyServerState.Active);

    void ILobbyObserver.Closed(Lobby lobby)
    {
        if (!_lobbies.TryRemove(new KeyValuePair<string, Lobby>(lobby.Id, lobby)))
            return;
        _resources.Release(lobby.Id);
        if (lobby.JoinCode is { } code)
            _codes.TryRemove(code, out _);
        if (Server(lobby.ServerId) is { } server)
        {
            server.Forget(lobby);
            server.Release();
        }

        LobbyClosed?.Invoke(lobby);
    }

    private string ClaimCode(string lobbyId)
    {
        while (true)
        {
            var code = NewJoinCode();
            if (_codes.TryAdd(code, lobbyId))
                return code;
        }
    }
}
