using Burbuja.Realtime.Abstractions;

namespace Burbuja.Realtime.Fleet;

/// <summary>
/// The in-process backplane, the default: a single node is a fleet of one. Several nodes in one process may
/// share an instance, which is how the fleet tests run a multi-node fleet under a fake clock. Expiry follows the
/// given <see cref="TimeProvider"/>; calls go straight to the serving node's handler, still as strings, so a
/// payload that would not survive the wire does not survive here either.
/// </summary>
public sealed class LocalBackplane(TimeProvider time) : IRealtimeBackplane
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Expiring<BackplaneNode>> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Expiring<BackplaneLobby>> _lobbies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Expiring<string>> _codes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Expiring<string>> _seats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Expiring<BackplaneTicket>> _tickets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _queuedByPlayer = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Expiring<string>> _leases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<string, CancellationToken, Task<string>>> _handlers = new(StringComparer.Ordinal);
    private readonly List<Action<string>> _subscribers = [];

    public LocalBackplane()
        : this(TimeProvider.System)
    {
    }

    public bool IsDistributed => false;

    public ValueTask<bool> PublishNodeAsync(BackplaneNode node, TimeSpan ttl, CancellationToken ct)
    {
        lock (_gate)
        {
            if (Live(_nodes, node.NodeId) is { } current && current.InstanceId != node.InstanceId)
                return ValueTask.FromResult(false);
            _nodes[node.NodeId] = new(node, Until(ttl));
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask RemoveNodeAsync(string nodeId, string instanceId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (Live(_nodes, nodeId) is { } current && current.InstanceId == instanceId)
                _nodes.Remove(nodeId);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<BackplaneNode>> ListNodesAsync(CancellationToken ct)
    {
        lock (_gate)
            return ValueTask.FromResult<IReadOnlyList<BackplaneNode>>(LiveValues(_nodes).OrderBy(n => n.NodeId, StringComparer.Ordinal).ToList());
    }

    public ValueTask PublishLobbiesAsync(IReadOnlyList<BackplaneLobby> lobbies, TimeSpan ttl, TimeSpan seatTtl, CancellationToken ct)
    {
        lock (_gate)
        {
            var until = Until(ttl);
            var seatsUntil = Until(seatTtl);
            foreach (var lobby in lobbies)
            {
                _lobbies[lobby.LobbyId] = new(lobby, until);
                if (lobby.JoinCode is { } code && Live(_codes, code) == lobby.LobbyId)
                    _codes[code] = new(lobby.LobbyId, until);
                foreach (var player in lobby.Players)
                    if (Live(_seats, player) == lobby.LobbyId)
                        _seats[player] = new(lobby.LobbyId, seatsUntil);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveLobbyAsync(BackplaneLobby lobby, CancellationToken ct)
    {
        lock (_gate)
        {
            _lobbies.Remove(lobby.LobbyId);
            if (lobby.JoinCode is { } code && Live(_codes, code) == lobby.LobbyId)
                _codes.Remove(code);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<BackplaneLobby?> FindLobbyAsync(string lobbyId, CancellationToken ct)
    {
        lock (_gate)
            return ValueTask.FromResult(Live(_lobbies, lobbyId));
    }

    public ValueTask<IReadOnlyList<BackplaneLobby>> ListLobbiesAsync(string gameId, CancellationToken ct)
    {
        lock (_gate)
            return ValueTask.FromResult<IReadOnlyList<BackplaneLobby>>(
                LiveValues(_lobbies).Where(l => l.Listed && l.GameId == gameId).ToList());
    }

    public ValueTask<bool> ClaimJoinCodeAsync(string code, string lobbyId, TimeSpan ttl, CancellationToken ct)
    {
        lock (_gate)
        {
            if (Live(_codes, code) is { } holder && holder != lobbyId)
                return ValueTask.FromResult(false);
            _codes[code] = new(lobbyId, Until(ttl));
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<string?> FindJoinCodeAsync(string code, CancellationToken ct)
    {
        lock (_gate)
            return ValueTask.FromResult(Live(_codes, code));
    }

    public ValueTask<string> ClaimSeatAsync(string playerId, string lobbyId, TimeSpan ttl, CancellationToken ct)
    {
        lock (_gate)
        {
            if (Live(_seats, playerId) is { } holder && holder != lobbyId)
                return ValueTask.FromResult(holder);
            _seats[playerId] = new(lobbyId, Until(ttl));
            return ValueTask.FromResult(lobbyId);
        }
    }

    public ValueTask<bool> TransferSeatAsync(string playerId, string fromLobbyId, string toLobbyId, TimeSpan ttl, CancellationToken ct)
    {
        lock (_gate)
        {
            if (Live(_seats, playerId) != fromLobbyId)
                return ValueTask.FromResult(false);
            _seats[playerId] = new(toLobbyId, Until(ttl));
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask ReleaseSeatAsync(string playerId, string lobbyId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (Live(_seats, playerId) == lobbyId)
                _seats.Remove(playerId);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> SeatOfAsync(string playerId, CancellationToken ct)
    {
        lock (_gate)
            return ValueTask.FromResult(Live(_seats, playerId));
    }

    public ValueTask<BackplaneTicket> AddTicketAsync(BackplaneTicket ticket, TimeSpan ttl, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_queuedByPlayer.TryGetValue(ticket.PlayerId, out var existingId)
                && Live(_tickets, existingId) is { Queued: true } existing)
                return ValueTask.FromResult(existing);
            _tickets[ticket.TicketId] = new(ticket, Until(ttl));
            _queuedByPlayer[ticket.PlayerId] = ticket.TicketId;
            return ValueTask.FromResult(ticket);
        }
    }

    public ValueTask<BackplaneTicket?> GetTicketAsync(string ticketId, CancellationToken ct)
    {
        lock (_gate)
            return ValueTask.FromResult(Live(_tickets, ticketId));
    }

    public ValueTask<IReadOnlyList<BackplaneTicket>> QueuedTicketsAsync(string gameId, CancellationToken ct)
    {
        lock (_gate)
            return ValueTask.FromResult<IReadOnlyList<BackplaneTicket>>(LiveValues(_tickets)
                .Where(t => t.Queued && t.GameId == gameId)
                .OrderBy(t => t.EnqueuedAt).ThenBy(t => t.TicketId, StringComparer.Ordinal)
                .ToList());
    }

    public ValueTask<bool> UpdateTicketAsync(BackplaneTicket ticket, TimeSpan ttl, CancellationToken ct)
    {
        Action<string>[] notify;
        lock (_gate)
        {
            if (Live(_tickets, ticket.TicketId) is not { Queued: true })
                return ValueTask.FromResult(false);
            _tickets[ticket.TicketId] = new(ticket, Until(ttl));
            if (ticket.Queued)
                return ValueTask.FromResult(true);
            if (_queuedByPlayer.TryGetValue(ticket.PlayerId, out var id) && id == ticket.TicketId)
                _queuedByPlayer.Remove(ticket.PlayerId);
            notify = _subscribers.ToArray();
        }

        foreach (var subscriber in notify)
            subscriber(ticket.TicketId);
        return ValueTask.FromResult(true);
    }

    public ValueTask<IAsyncDisposable> SubscribeTicketsAsync(Action<string> settled, CancellationToken ct)
    {
        lock (_gate)
            _subscribers.Add(settled);
        return ValueTask.FromResult<IAsyncDisposable>(new Disposer(() =>
        {
            lock (_gate)
                _subscribers.Remove(settled);
        }));
    }

    public ValueTask<bool> HoldLeaseAsync(string name, string owner, TimeSpan ttl, CancellationToken ct)
    {
        lock (_gate)
        {
            if (Live(_leases, name) is { } holder && holder != owner)
                return ValueTask.FromResult(false);
            _leases[name] = new(owner, Until(ttl));
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask ReleaseLeaseAsync(string name, string owner, CancellationToken ct)
    {
        lock (_gate)
        {
            if (Live(_leases, name) == owner)
                _leases.Remove(name);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IAsyncDisposable> ServeAsync(string nodeId, Func<string, CancellationToken, Task<string>> handler, CancellationToken ct)
    {
        lock (_gate)
            _handlers[nodeId] = handler;
        return ValueTask.FromResult<IAsyncDisposable>(new Disposer(() =>
        {
            lock (_gate)
            {
                if (_handlers.TryGetValue(nodeId, out var current) && current == handler)
                    _handlers.Remove(nodeId);
            }
        }));
    }

    public async Task<string> CallAsync(string nodeId, string request, TimeSpan timeout, CancellationToken ct)
    {
        Func<string, CancellationToken, Task<string>>? handler;
        lock (_gate)
            _handlers.TryGetValue(nodeId, out handler);
        if (handler is null)
            throw new TimeoutException($"No node '{nodeId}' answers calls.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await handler(request, cts.Token).WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Node '{nodeId}' did not answer within {timeout.TotalMilliseconds:F0} ms.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new RealtimeBackplaneException($"Node '{nodeId}' failed to handle the call.", ex);
        }
    }

    private DateTimeOffset Until(TimeSpan ttl) => time.GetUtcNow() + ttl;

    private T? Live<T>(Dictionary<string, Expiring<T>> map, string key) where T : class
    {
        if (!map.TryGetValue(key, out var entry))
            return null;
        if (entry.ExpiresAt > time.GetUtcNow())
            return entry.Value;
        map.Remove(key);
        return null;
    }

    private List<T> LiveValues<T>(Dictionary<string, Expiring<T>> map) where T : class
    {
        var now = time.GetUtcNow();
        foreach (var key in map.Where(e => e.Value.ExpiresAt <= now).Select(e => e.Key).ToList())
            map.Remove(key);
        return map.Values.Select(e => e.Value).ToList();
    }

    private sealed record Expiring<T>(T Value, DateTimeOffset ExpiresAt);

    private sealed class Disposer(Action dispose) : IAsyncDisposable
    {
        private int _done;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                dispose();
            return ValueTask.CompletedTask;
        }
    }
}
