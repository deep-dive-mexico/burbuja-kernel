namespace Burbuja.Realtime.Abstractions;

/// <summary>Whether a lobby server takes new lobbies.</summary>
public enum LobbyServerState
{
    Active,

    /// <summary>Existing lobbies run to completion; the balancer places nothing new here.</summary>
    Draining,
}

/// <summary>One lobby server's load as the balancer sees it at a decision.</summary>
/// <param name="Load">Measured compute: the time its lobbies spent in their steps (game code and kernel work)
/// per second of lobby time over their last measurement window, summed — roughly the cores in use. Unlike
/// <paramref name="Lobbies"/> it tells a 60 Hz eight-player match from an idle quiz room.</param>
public sealed record LobbyServerStatus(
    string ServerId,
    string Region,
    LobbyServerState State,
    int Lobbies,
    int MaxLobbies,
    int Players,
    string? PublicUrl,
    double Load = 0)
{
    public bool HasCapacity => State == LobbyServerState.Active && Lobbies < MaxLobbies;

    /// <summary>Allocated lobbies as a fraction of capacity, 0..1.</summary>
    public double Utilization => MaxLobbies == 0 ? 1 : (double)Lobbies / MaxLobbies;
}

/// <summary>A request for a new dedicated lobby. <paramref name="Region"/> null means "anywhere".</summary>
public sealed record PlacementRequest(string GameId, string? Region, int Seats);

/// <summary>
/// The balancer's choice among servers. The kernel has already removed draining and full servers and applied
/// the region preference, so a strategy only ranks: spread for latency headroom, pack for scale-down.
/// Register extra strategies as DI services and select one by <see cref="Name"/> with
/// <c>Realtime:Placement</c>.
/// </summary>
public interface ILobbyPlacementStrategy
{
    string Name { get; }

    /// <summary>The chosen server id from <paramref name="candidates"/> (never empty), or null to refuse.</summary>
    string? Choose(IReadOnlyList<LobbyServerStatus> candidates, PlacementRequest request);
}
