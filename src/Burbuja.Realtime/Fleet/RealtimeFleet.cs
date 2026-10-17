using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Burbuja.Realtime.Abstractions;
using Burbuja.Realtime.Lobbies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Burbuja.Realtime.Fleet;

/// <summary>This process's identity in the fleet. <paramref name="InstanceId"/> is new on every start.</summary>
public sealed record RealtimeNode(string NodeId, string InstanceId)
{
    public static RealtimeNode Create(string nodeId) => new(nodeId, Guid.NewGuid().ToString("N"));
}

/// <summary>A lobby server anywhere in the fleet: its node, its load, and the games its node can host.</summary>
public sealed record FleetServer(string NodeId, LobbyServerStatus Status, IReadOnlyList<string> Games);

/// <summary>A placed lobby with one reservation result per requested seat, in order, or why nothing was placed.</summary>
public sealed record FleetCreation(LobbySnapshot? Lobby, IReadOnlyList<ReservationResult> Reservations, string? Error)
{
    public static FleetCreation Fail(string error) => new(null, [], error);
}

/// <summary>
/// This node's part in a fleet of lobby nodes sharing one <see cref="IRealtimeBackplane"/>. The API on any node
/// can place a lobby on any node's server, reserve a seat in any lobby, and find a lobby by id or join code. Each
/// lobby still lives on exactly one node, with its loop and its connections, and players connect to that node.
///
/// <para><b>Routing.</b> An operation on a lobby or server of this node runs here on the local
/// <see cref="LobbyManager"/>; anything else becomes a call to the owning node, which runs the same local code.
/// The owner is the only authority for its capacity and its seats: placement ranks every live server from the
/// published records, then asks the owner, and a server that filled meanwhile answers <c>no_capacity</c> and the
/// balancer moves on.</para>
///
/// <para><b>One seat per player.</b> A reservation first claims the player's seat on the backplane, then
/// reserves locally, then confirms the claim still names this lobby, so two nodes seating the same player at
/// once leave exactly one seat. A claim that names a lobby whose record is gone (closed, or its node died)
/// or that no longer seats the player is taken over. Seats are released as the lobby gives them up, and every
/// heartbeat refreshes the claims of seats still held.</para>
///
/// <para><b>Failure.</b> A node that stops heartbeating drops out after <see cref="FleetOptions.NodeTtlSeconds"/>:
/// nothing more is placed there, and its players may take seats elsewhere. Lobbies keep running while the
/// backplane is unreachable; only fleet operations fail, with <see cref="RealtimeBackplaneException"/>.</para>
/// </summary>
public sealed class RealtimeFleet : IAsyncDisposable
{
    internal static readonly JsonSerializerOptions Json = CreateJson();

    private readonly LobbyManager _manager;
    private readonly IRealtimeBackplane _backplane;
    private readonly RealtimeNode _node;
    private readonly FleetOptions _options;
    private readonly bool _allowCrossRegion;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<Task, byte> _pending = new();
    private readonly DateTimeOffset _startedAt;
    private (IReadOnlyList<FleetNodeInfo> Nodes, DateTimeOffset ReadAt)? _nodes;
    private IAsyncDisposable? _serving;

    public RealtimeFleet(
        LobbyManager manager,
        IRealtimeBackplane backplane,
        RealtimeNode node,
        IOptions<RealtimeOptions> options,
        TimeProvider time,
        ILogger<RealtimeFleet> log)
        : this(manager, backplane, node, options.Value, time, log)
    {
    }

    internal RealtimeFleet(
        LobbyManager manager,
        IRealtimeBackplane backplane,
        RealtimeNode node,
        RealtimeOptions options,
        TimeProvider time,
        ILogger log)
    {
        _manager = manager;
        _backplane = backplane;
        _node = node;
        _options = options.Fleet;
        _allowCrossRegion = options.AllowCrossRegionPlacement;
        _time = time;
        _log = log;
        _startedAt = time.GetUtcNow();
        manager.SeatReleased += (lobby, playerId) => Track(() => _backplane.ReleaseSeatAsync(playerId, lobby.Id, CancellationToken.None));
        manager.LobbyClosed += lobby => Track(() => _backplane.RemoveLobbyAsync(
            new BackplaneLobby(lobby.Id, NodeId, lobby.GameId, false, lobby.JoinCode, [], ""), CancellationToken.None));
    }

    public string NodeId => _node.NodeId;

    /// <summary>The lobbies, servers and games of this node.</summary>
    public LobbyManager Local => _manager;

    public IRealtimeBackplane Backplane => _backplane;

    internal TimeSpan HeartbeatInterval => TimeSpan.FromMilliseconds(Math.Clamp(_options.HeartbeatMilliseconds, 50, 30_000));

    internal TimeSpan NodeTtl => TimeSpan.FromSeconds(Math.Max(1, _options.NodeTtlSeconds));

    private TimeSpan SeatTtl => TimeSpan.FromSeconds(Math.Max(_options.NodeTtlSeconds * 2, _options.SeatTtlSeconds));

    private TimeSpan CallTimeout => TimeSpan.FromMilliseconds(Math.Clamp(_options.CallTimeoutMilliseconds, 100, 60_000));

    // ─── lifecycle ───────────────────────────────────────────────────────────────────────────

    /// <summary>Joins the fleet: checks this node's identity and servers against the live fleet, publishes its
    /// record and starts answering calls. Every refusal is a configuration mistake and stops the host.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        var errors = new List<string>();
        if (_backplane.IsDistributed)
            errors.AddRange(_manager.Balancer.Servers.Where(s => s.PublicUrl is null).Select(s =>
                $"server '{s.Id}' needs a PublicUrl: in a distributed fleet a player connects to the node hosting the lobby"));
        foreach (var other in (await ReadNodesAsync(ct)).Where(n => n.NodeId != NodeId))
            foreach (var clash in other.Servers.Select(s => s.ServerId).Intersect(_manager.Balancer.Servers.Select(s => s.Id)))
                errors.Add($"server '{clash}' is already hosted by node '{other.NodeId}'; server ids must be unique in the fleet");
        if (errors.Count == 0 && !await _backplane.PublishNodeAsync(NodeRecord(), NodeTtl, ct))
            errors.Add($"node id '{NodeId}' is already used by another live node; give each node its own Kernel:NodeId");
        if (errors.Count > 0)
            throw new InvalidOperationException("Realtime fleet configuration is invalid: " + string.Join("; ", errors));

        _serving = await _backplane.ServeAsync(NodeId, HandleAsync, ct);
        _nodes = null;
    }

    /// <summary>Republishes this node, its lobbies and their seat claims. The host runs it every
    /// <see cref="FleetOptions.HeartbeatMilliseconds"/>.</summary>
    public async Task HeartbeatAsync(CancellationToken ct)
    {
        if (!await _backplane.PublishNodeAsync(NodeRecord(), NodeTtl, ct))
            _log.LogError("Another live process publishes node id {NodeId}; this node's servers are hidden from the fleet.", NodeId);
        var lobbies = _manager.Lobbies.Select(l => Record(l.Snapshot())).ToList();
        if (lobbies.Count > 0)
            await _backplane.PublishLobbiesAsync(lobbies, NodeTtl, SeatTtl, ct);
        _nodes = null;
    }

    /// <summary>Waits for the backplane writes this node has started in the background (seat releases, lobby
    /// removals), so a test or a shutdown sees them done.</summary>
    public Task FlushAsync() => Task.WhenAll(_pending.Keys);

    public async ValueTask DisposeAsync()
    {
        if (_serving is { } serving)
        {
            _serving = null;
            await serving.DisposeAsync();
        }

        try
        {
            await FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await _backplane.RemoveNodeAsync(NodeId, _node.InstanceId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is RealtimeBackplaneException or TimeoutException)
        {
            _log.LogWarning(ex, "Node {NodeId} left the fleet without removing its records; they expire on their own.", NodeId);
        }
    }

    // ─── reads ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Every live server: this node's measured now, the others' as of their last heartbeat.</summary>
    public async Task<IReadOnlyList<FleetServer>> ServersAsync(CancellationToken ct)
    {
        var games = _manager.Games.All.Select(g => g.Id).ToList();
        var servers = _manager.Balancer.Servers.Select(s => new FleetServer(NodeId, s.Status(), games)).ToList();
        foreach (var node in await NodesAsync(ct))
            if (node.NodeId != NodeId)
                servers.AddRange(node.Servers.Select(s => new FleetServer(node.NodeId, s, node.Games)));
        return servers;
    }

    /// <summary>Listed lobbies (public, not finished) of one game or of every game the fleet hosts. This node's
    /// are exact; the others' are as of their last heartbeat.</summary>
    public async Task<IReadOnlyList<LobbySnapshot>> ListLobbiesAsync(string? gameId, CancellationToken ct)
    {
        var local = _manager.Lobbies
            .Where(l => l.Visibility == LobbyVisibility.Public && (gameId is null || l.GameId == gameId))
            .Select(l => l.Snapshot())
            .Where(s => s.State < LobbyState.Ended);
        var games = gameId is not null
            ? [gameId]
            : (await NodesAsync(ct)).SelectMany(n => n.Games).Concat(_manager.Games.All.Select(g => g.Id)).Distinct().ToList();
        var remote = new List<LobbySnapshot>();
        foreach (var game in games)
            foreach (var record in await _backplane.ListLobbiesAsync(game, ct))
                if (record.NodeId != NodeId && Parse<LobbySnapshot>(record.Payload) is { State: < LobbyState.Ended } snapshot)
                    remote.Add(snapshot);
        return local.Concat(remote).OrderBy(s => s.CreatedAt).ToList();
    }

    /// <summary>A lobby anywhere in the fleet: exact when it is on this node or its node answers, otherwise as of
    /// its last heartbeat. Null when no live node has it.</summary>
    public async Task<LobbySnapshot?> FindAsync(string lobbyId, CancellationToken ct)
    {
        if (_manager.Find(lobbyId) is { } lobby)
            return lobby.Snapshot();
        if (await _backplane.FindLobbyAsync(lobbyId, ct) is not { } record)
            return null;
        try
        {
            var reply = await CallAsync(record.NodeId, new FleetCall("snapshot") { LobbyId = lobbyId }, ct);
            return reply.Error is null ? reply.Lobby : null;
        }
        catch (Exception ex) when (ex is TimeoutException or RealtimeBackplaneException)
        {
            return Parse<LobbySnapshot>(record.Payload);
        }
    }

    /// <summary>The id of the lobby a join code names, anywhere in the fleet.</summary>
    public async Task<string?> FindByCodeAsync(string? code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;
        var normalized = code.Trim().ToUpperInvariant();
        return _manager.FindByCode(normalized)?.Id ?? await _backplane.FindJoinCodeAsync(normalized, ct);
    }

    /// <summary>The lobby holding this player's seat, if it is still being played.</summary>
    public async Task<string?> LobbyOfAsync(string playerId, CancellationToken ct)
    {
        if (await _backplane.SeatOfAsync(playerId, ct) is not { } lobbyId)
            return null;
        return await HoldsAsync(lobbyId, playerId, ct) ? lobbyId : null;
    }

    /// <summary>Every lobby on every live node, by node: what operators see. A node that does not answer maps to
    /// null.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<LobbySnapshot>?>> HostedLobbiesAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyList<LobbySnapshot>?>(StringComparer.Ordinal)
        {
            [NodeId] = _manager.Lobbies.Select(l => l.Snapshot()).OrderBy(s => s.CreatedAt).ToList(),
        };
        var others = (await NodesAsync(ct)).Where(n => n.NodeId != NodeId).ToList();
        var replies = await Task.WhenAll(others.Select(async node =>
        {
            try
            {
                return (await CallAsync(node.NodeId, new FleetCall("hosted"), ct)).Lobbies;
            }
            catch (Exception ex) when (ex is TimeoutException or RealtimeBackplaneException)
            {
                return null;
            }
        }));
        for (var i = 0; i < others.Count; i++)
            result[others[i].NodeId] = replies[i];
        return result;
    }

    /// <summary>Public matchmade lobbies of <paramref name="game"/> that can still take a player.</summary>
    public async Task<IReadOnlyList<BackfillCandidate>> BackfillCandidatesAsync(IRealtimeGame game, CancellationToken ct) =>
        (await ListLobbiesAsync(game.Id, ct))
            .Where(s => s.Origin == LobbyOrigin.Matchmaking && s.OpenSeats > 0
                && (s.State is LobbyState.Open or LobbyState.Countdown
                    || (s.State == LobbyState.Running && game.Rules.AllowJoinInProgress)))
            .Select(s => new BackfillCandidate(s.LobbyId, s.Region, s.State, s.OpenSeats, s.AverageSkill))
            .ToList();

    // ─── operations ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Places a new lobby on the server the balancer picks among every live server whose node hosts the game (or
    /// on <see cref="CreateLobbyRequest.ServerId"/> when set), and reserves <paramref name="seats"/> in it, on
    /// whichever node that server lives. A server that filled
    /// since its last heartbeat refuses and the next is tried. A node that does not answer fails the placement
    /// with <c>node_unavailable</c> after being told to close whatever it may have created.
    /// </summary>
    public async Task<FleetCreation> CreateAsync(CreateLobbyRequest request, IReadOnlyList<PlayerIdentity> seats, CancellationToken ct)
    {
        if (_manager.Games.Find(request.GameId) is not { } game)
            return FleetCreation.Fail("unknown_game");
        var placement = new PlacementRequest(game.Id, request.Region, game.Rules.MaxPlayers);
        var balancer = new LobbyBalancer(_manager.Balancer.Servers, _manager.Balancer.Strategy, _allowCrossRegion);
        var refused = new HashSet<string>(StringComparer.Ordinal);
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidates = (await ServersAsync(ct))
                .Where(s => s.Games.Contains(game.Id) && !refused.Contains(s.Status.ServerId)
                    && (request.ServerId is null || s.Status.ServerId == request.ServerId))
                .ToList();
            if (balancer.Choose(candidates.Select(c => c.Status).ToList(), placement) is not { } serverId)
                return FleetCreation.Fail("no_capacity");
            var target = candidates.First(c => c.Status.ServerId == serverId);
            var pinned = request with { ServerId = serverId, LobbyId = request.LobbyId ?? LobbyManager.NewLobbyId() };
            FleetCreation result;
            if (target.NodeId == NodeId)
            {
                result = await CreateHereAsync(pinned, seats, ct);
            }
            else
            {
                try
                {
                    var reply = await CallAsync(target.NodeId, new FleetCall("create") { Create = pinned, Players = seats }, ct);
                    result = new FleetCreation(reply.Lobby, reply.Reservations ?? [], reply.Error);
                }
                catch (Exception ex) when (ex is TimeoutException or RealtimeBackplaneException)
                {
                    _log.LogWarning(ex, "Node {NodeId} did not place lobby {LobbyId}.", target.NodeId, pinned.LobbyId);
                    Track(() => CallAsync(target.NodeId, new FleetCall("close") { LobbyId = pinned.LobbyId, Reason = "placement_abandoned" },
                        CancellationToken.None));
                    return FleetCreation.Fail("node_unavailable");
                }
            }

            if (result.Error != "no_capacity")
                return result;
            refused.Add(serverId);
            _nodes = null;
        }

        return FleetCreation.Fail("no_capacity");
    }

    public async Task<ReservationResult> ReserveAsync(string lobbyId, PlayerIdentity player, CancellationToken ct) =>
        (await ReserveAsync(lobbyId, [player], ct))[0];

    /// <summary>Reserves a seat for each player in a lobby anywhere in the fleet; one result per player, in order.
    /// Reserving again for a seated player keeps their seat and issues a fresh token (the reconnect path).</summary>
    public async Task<IReadOnlyList<ReservationResult>> ReserveAsync(string lobbyId, IReadOnlyList<PlayerIdentity> players, CancellationToken ct)
    {
        if (_manager.Find(lobbyId) is { } lobby)
        {
            var results = new List<ReservationResult>(players.Count);
            foreach (var player in players)
                results.Add(await ReserveHereAsync(lobby, player, ct));
            return results;
        }

        if (await _backplane.FindLobbyAsync(lobbyId, ct) is not { } record)
            return players.Select(_ => ReservationResult.Fail("lobby_not_found")).ToList();
        try
        {
            var reply = await CallAsync(record.NodeId, new FleetCall("reserve") { LobbyId = lobbyId, Players = players }, ct);
            return reply.Reservations ?? players.Select(_ => ReservationResult.Fail(reply.Error ?? "lobby_not_found")).ToList();
        }
        catch (Exception ex) when (ex is TimeoutException or RealtimeBackplaneException)
        {
            _log.LogWarning(ex, "Node {NodeId} did not answer a reservation in lobby {LobbyId}.", record.NodeId, lobbyId);
            return players.Select(_ => ReservationResult.Fail("node_unavailable")).ToList();
        }
    }

    /// <summary>Frees a player's seat; false when the lobby is unknown or the player has no seat there.</summary>
    public Task<bool> LeaveAsync(string lobbyId, string playerId, CancellationToken ct) =>
        _manager.Find(lobbyId) is { } lobby
            ? Task.FromResult(_manager.Leave(lobby, playerId))
            : RemoteDoneAsync(lobbyId, new FleetCall("leave") { LobbyId = lobbyId, PlayerId = playerId }, ct);

    /// <summary>Ends a lobby's match and closes it at its next step.</summary>
    public Task<bool> CloseAsync(string lobbyId, string reason, CancellationToken ct)
    {
        if (_manager.Find(lobbyId) is not { } lobby)
            return RemoteDoneAsync(lobbyId, new FleetCall("close") { LobbyId = lobbyId, Reason = reason }, ct);
        lobby.RequestClose(reason);
        return Task.FromResult(true);
    }

    /// <summary>Drains or activates a server on whichever node hosts it. Null when no live node has it.</summary>
    public async Task<LobbyServerState?> SetServerStateAsync(string serverId, bool drain, CancellationToken ct)
    {
        if (_manager.Server(serverId) is { } server)
        {
            if (drain)
                server.Drain();
            else
                server.Activate();
            _nodes = null;
            return server.State;
        }

        var owner = (await ServersAsync(ct)).FirstOrDefault(s => s.Status.ServerId == serverId);
        if (owner is null)
            return null;
        var reply = await CallAsync(owner.NodeId, new FleetCall("server") { ServerId = serverId, Drain = drain }, ct);
        _nodes = null;
        return reply.State;
    }

    // ─── the owner's side ────────────────────────────────────────────────────────────────────

    private async Task<FleetCreation> CreateHereAsync(CreateLobbyRequest request, IReadOnlyList<PlayerIdentity> seats, CancellationToken ct)
    {
        var lobbyId = request.LobbyId ?? LobbyManager.NewLobbyId();
        string? code = null;
        if (request.Origin == LobbyOrigin.Hosted)
        {
            // Codes are short, so uniqueness is checked fleet-wide before the lobby exists.
            do
                code = LobbyManager.NewJoinCode();
            while (!await _backplane.ClaimJoinCodeAsync(code, lobbyId, NodeTtl, ct));
        }

        var created = _manager.Create(request with { LobbyId = lobbyId }, code);
        if (created.Lobby is not { } lobby)
        {
            if (code is not null)
                await _backplane.RemoveLobbyAsync(new BackplaneLobby(lobbyId, NodeId, request.GameId, false, code, [], ""), ct);
            return FleetCreation.Fail(created.Error!);
        }

        // Published before any seat is claimed, so a claim naming this lobby is never mistaken for a stale one,
        // and with the node's new allocation, so a burst of placements elsewhere sees this server fill.
        await _backplane.PublishLobbiesAsync([Record(lobby.Snapshot())], NodeTtl, SeatTtl, ct);
        await _backplane.PublishNodeAsync(NodeRecord(), NodeTtl, ct);
        var reservations = new List<ReservationResult>(seats.Count);
        foreach (var seat in seats)
            reservations.Add(await ReserveHereAsync(lobby, seat, ct));
        if (seats.Count > 0 && reservations.All(r => r.Reservation is null))
            lobby.RequestClose("unseated");
        else
            await _backplane.PublishLobbiesAsync([Record(lobby.Snapshot())], NodeTtl, SeatTtl, ct);
        return new FleetCreation(lobby.Snapshot(), reservations, null);
    }

    private async Task<ReservationResult> ReserveHereAsync(Lobby lobby, PlayerIdentity identity, CancellationToken ct)
    {
        var playerId = identity.PlayerId;
        var holder = await _backplane.ClaimSeatAsync(playerId, lobby.Id, SeatTtl, ct);
        if (holder != lobby.Id
            && (await HoldsAsync(holder, playerId, ct)
                || !await _backplane.TransferSeatAsync(playerId, holder, lobby.Id, SeatTtl, ct)))
            return ReservationResult.Fail("already_in_lobby");

        var seated = lobby.HasSeat(playerId);
        var result = _manager.Reserve(lobby, identity);
        if (result.Reservation is null)
        {
            await _backplane.ReleaseSeatAsync(playerId, lobby.Id, ct);
            return result;
        }

        // Another node may have taken the claim over between our claim and our seat; the claim decides.
        if (await _backplane.SeatOfAsync(playerId, ct) != lobby.Id)
        {
            if (!seated)
                lobby.RequestLeave(playerId);
            return ReservationResult.Fail("already_in_lobby");
        }

        return result;
    }

    /// <summary>Whether a lobby still being played seats the player: asked of this node directly, of another
    /// node by a call. A lobby with no record is gone. A node that does not answer is assumed to hold the seat,
    /// so a slow node never loses a player to a double seat.</summary>
    private async Task<bool> HoldsAsync(string lobbyId, string playerId, CancellationToken ct)
    {
        if (_manager.Find(lobbyId) is { } lobby)
            return lobby.State < LobbyState.Ended && lobby.HasSeat(playerId);
        if (await _backplane.FindLobbyAsync(lobbyId, ct) is not { } record || record.NodeId == NodeId)
            return false;
        try
        {
            return (await CallAsync(record.NodeId, new FleetCall("holds") { LobbyId = lobbyId, PlayerId = playerId }, ct)).Done;
        }
        catch (Exception ex) when (ex is TimeoutException or RealtimeBackplaneException)
        {
            return true;
        }
    }

    private async Task<string> HandleAsync(string request, CancellationToken ct)
    {
        FleetReply reply;
        try
        {
            reply = Parse<FleetCall>(request) is { } call ? await ExecuteAsync(call, ct) : new FleetReply { Error = "bad_request" };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Node {NodeId} failed a fleet call.", NodeId);
            reply = new FleetReply { Error = "internal_error" };
        }

        return JsonSerializer.Serialize(reply, Json);
    }

    private async Task<FleetReply> ExecuteAsync(FleetCall call, CancellationToken ct)
    {
        if (call.Op == "create")
        {
            var created = await CreateHereAsync(call.Create!, call.Players ?? [], ct);
            return new FleetReply { Error = created.Error, Lobby = created.Lobby, Reservations = created.Reservations };
        }

        if (call.Op == "hosted")
            return new FleetReply { Lobbies = _manager.Lobbies.Select(l => l.Snapshot()).OrderBy(s => s.CreatedAt).ToList() };

        if (call.Op == "server")
        {
            if (_manager.Server(call.ServerId ?? "") is not { } server)
                return new FleetReply { Error = "server_not_found" };
            if (call.Drain)
                server.Drain();
            else
                server.Activate();
            return new FleetReply { State = server.State, Done = true };
        }

        if (_manager.Find(call.LobbyId) is not { } lobby)
            return new FleetReply { Error = "lobby_not_found" };
        switch (call.Op)
        {
            case "reserve":
                var results = new List<ReservationResult>();
                foreach (var player in call.Players ?? [])
                    results.Add(await ReserveHereAsync(lobby, player, ct));
                return new FleetReply { Reservations = results };
            case "snapshot":
                return new FleetReply { Lobby = lobby.Snapshot() };
            case "holds":
                return new FleetReply { Done = lobby.State < LobbyState.Ended && lobby.HasSeat(call.PlayerId ?? "") };
            case "leave":
                return new FleetReply { Done = _manager.Leave(lobby, call.PlayerId ?? "") };
            case "close":
                lobby.RequestClose(call.Reason ?? "closed");
                return new FleetReply { Done = true };
            default:
                return new FleetReply { Error = "unknown_operation" };
        }
    }

    // ─── plumbing ────────────────────────────────────────────────────────────────────────────

    private async Task<bool> RemoteDoneAsync(string lobbyId, FleetCall call, CancellationToken ct)
    {
        if (await _backplane.FindLobbyAsync(lobbyId, ct) is not { } record || record.NodeId == NodeId)
            return false;
        return (await CallAsync(record.NodeId, call, ct)).Done;
    }

    private async Task<FleetReply> CallAsync(string nodeId, FleetCall call, CancellationToken ct)
    {
        var reply = await _backplane.CallAsync(nodeId, JsonSerializer.Serialize(call, Json), CallTimeout, ct);
        return Parse<FleetReply>(reply) ?? throw new RealtimeBackplaneException($"Node '{nodeId}' sent an unreadable reply.");
    }

    private async Task<IReadOnlyList<FleetNodeInfo>> NodesAsync(CancellationToken ct)
    {
        // Placement reads the fleet on every decision; a fraction of a heartbeat is fresh enough.
        var maxAge = TimeSpan.FromTicks(Math.Min(HeartbeatInterval.Ticks, TimeSpan.FromMilliseconds(500).Ticks));
        if (_nodes is { } cached && _time.GetUtcNow() - cached.ReadAt < maxAge)
            return cached.Nodes;
        var nodes = await ReadNodesAsync(ct);
        _nodes = (nodes, _time.GetUtcNow());
        return nodes;
    }

    private async Task<IReadOnlyList<FleetNodeInfo>> ReadNodesAsync(CancellationToken ct) =>
        (await _backplane.ListNodesAsync(ct)).Select(n => Parse<FleetNodeInfo>(n.Payload)).OfType<FleetNodeInfo>().ToList();

    private BackplaneNode NodeRecord() => new(NodeId, _node.InstanceId, JsonSerializer.Serialize(new FleetNodeInfo(
        NodeId,
        _manager.Games.All.Select(g => g.Id).ToList(),
        _manager.Balancer.Servers.Select(s => s.Status()).ToList(),
        _startedAt), Json));

    private BackplaneLobby Record(LobbySnapshot snapshot) => new(
        snapshot.LobbyId,
        NodeId,
        snapshot.GameId,
        snapshot.Visibility == LobbyVisibility.Public && snapshot.State < LobbyState.Ended,
        snapshot.JoinCode,
        snapshot.State < LobbyState.Ended ? snapshot.SeatHolders : [],
        JsonSerializer.Serialize(snapshot, Json));

    /// <summary>Starts a backplane write without waiting for it, logging its failure. The lobby loop raises the
    /// events that start these and must never wait on the network; the write itself starts here, in order.</summary>
    private void Track(Func<ValueTask> work)
    {
        Task task;
        try
        {
            task = work().AsTask();
        }
        catch (Exception ex)
        {
            task = Task.FromException(ex);
        }

        TrackTask(task);
    }

    private void Track(Func<Task> work)
    {
        Task task;
        try
        {
            task = work();
        }
        catch (Exception ex)
        {
            task = Task.FromException(ex);
        }

        TrackTask(task);
    }

    private void TrackTask(Task task)
    {
        var logged = LogFailureAsync(task);
        if (logged.IsCompleted)
            return;
        _pending[logged] = 0;
        logged.ContinueWith(t => _pending.TryRemove(t, out _), TaskScheduler.Default);
    }

    private async Task LogFailureAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "A background fleet update from node {NodeId} failed; the next heartbeat repairs it.", NodeId);
        }
    }

    private T? Parse<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Ignoring an unreadable {Type} from the backplane.", typeof(T).Name);
            return null;
        }
    }

    private static JsonSerializerOptions CreateJson()
    {
        var options = RealtimeJson.CreateOptions();
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>What a node publishes about itself on every heartbeat.</summary>
internal sealed record FleetNodeInfo(
    string NodeId,
    IReadOnlyList<string> Games,
    IReadOnlyList<LobbyServerStatus> Servers,
    DateTimeOffset StartedAt);

/// <summary>A call from one node to another. <see cref="Op"/> names it; the other members are its arguments.</summary>
internal sealed record FleetCall(string Op)
{
    public CreateLobbyRequest? Create { get; init; }
    public IReadOnlyList<PlayerIdentity>? Players { get; init; }
    public string? LobbyId { get; init; }
    public string? PlayerId { get; init; }
    public string? ServerId { get; init; }
    public string? Reason { get; init; }
    public bool Drain { get; init; }
}

internal sealed record FleetReply
{
    public string? Error { get; init; }
    public LobbySnapshot? Lobby { get; init; }
    public IReadOnlyList<LobbySnapshot>? Lobbies { get; init; }
    public IReadOnlyList<ReservationResult>? Reservations { get; init; }
    public bool Done { get; init; }
    public LobbyServerState? State { get; init; }
}
