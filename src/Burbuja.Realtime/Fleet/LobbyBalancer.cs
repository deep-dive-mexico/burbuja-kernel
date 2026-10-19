using Burbuja.Realtime.Abstractions;

namespace Burbuja.Realtime.Fleet;

/// <summary>
/// Chooses the lobby server a new dedicated lobby is placed on. The balancer owns eligibility (active, below
/// capacity, region preference with optional fallback); the strategy only ranks. On one node
/// <see cref="Allocate"/> also claims the slot atomically. Across a fleet the claim belongs to the node that
/// owns the chosen server, so the fleet calls <see cref="Choose"/> and asks that node.
/// </summary>
public sealed class LobbyBalancer
{
    private readonly IReadOnlyList<LobbyServer> _servers;
    private readonly bool _allowCrossRegion;

    public LobbyBalancer(IReadOnlyList<LobbyServer> servers, ILobbyPlacementStrategy strategy, bool allowCrossRegion)
    {
        _servers = servers;
        Strategy = strategy;
        _allowCrossRegion = allowCrossRegion;
    }

    public ILobbyPlacementStrategy Strategy { get; }

    /// <summary>This node's servers.</summary>
    public IReadOnlyList<LobbyServer> Servers => _servers;

    /// <summary>A server with one lobby slot already claimed for the caller, or null when none has room.
    /// The caller must <see cref="LobbyServer.Release"/> the slot if it does not go on to host a lobby.</summary>
    public LobbyServer? Allocate(PlacementRequest request)
    {
        // A server can fill between the status read and the claim; retry with fresh statuses, never more
        // often than there are servers, since each lost race means another server filled.
        for (var attempt = 0; attempt <= _servers.Count; attempt++)
        {
            var chosen = Choose(_servers.Select(s => s.Status()).ToList(), request);
            if (chosen is null)
                return null;
            var server = _servers.First(s => s.Id == chosen);
            if (server.TryAllocate())
                return server;
        }

        return null;
    }

    /// <summary>The id of the server among <paramref name="statuses"/> the strategy picks for
    /// <paramref name="request"/>, or null when none is eligible. Claims nothing.</summary>
    public string? Choose(IReadOnlyList<LobbyServerStatus> statuses, PlacementRequest request)
    {
        var candidates = statuses.Where(s => s.HasCapacity).ToList();
        if (request.Region is { } region)
        {
            var local = candidates.Where(s => string.Equals(s.Region, region, StringComparison.OrdinalIgnoreCase)).ToList();
            if (local.Count > 0)
                candidates = local;
            else if (!_allowCrossRegion)
                return null;
        }

        if (candidates.Count == 0)
            return null;
        var chosen = Strategy.Choose(candidates, request);
        return chosen is not null && candidates.Any(c => c.ServerId == chosen) ? chosen : null;
    }
}

/// <summary>Spread: the least utilised server, so every lobby has the most headroom. The default. Among
/// equally allocated servers the one doing the least measured simulation work wins, then the fewest players.</summary>
public sealed class LeastLoadedPlacement : ILobbyPlacementStrategy
{
    public string Name => "least-loaded";

    public string? Choose(IReadOnlyList<LobbyServerStatus> candidates, PlacementRequest request) =>
        candidates.OrderBy(s => s.Utilization).ThenBy(s => s.Load).ThenBy(s => s.Players)
            .ThenBy(s => s.ServerId, StringComparer.Ordinal)
            .First().ServerId;
}

/// <summary>Pack: the fullest server that still has room, so idle servers stay empty and can be scaled away.</summary>
public sealed class PackedPlacement : ILobbyPlacementStrategy
{
    public string Name => "packed";

    public string? Choose(IReadOnlyList<LobbyServerStatus> candidates, PlacementRequest request) =>
        candidates.OrderByDescending(s => s.Utilization).ThenBy(s => s.ServerId, StringComparer.Ordinal)
            .First().ServerId;
}
