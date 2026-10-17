namespace Burbuja.Realtime.Abstractions;

public enum TicketState
{
    Queued,

    /// <summary>A seat is reserved; the ticket carries the reservation to connect with.</summary>
    Matched,

    Cancelled,

    /// <summary>Not matched within <c>Realtime:Matchmaking:TicketTimeoutSeconds</c>.</summary>
    Expired,
}

/// <summary>A queued player as a matchmaking strategy sees them.</summary>
public sealed record MatchTicket(
    string TicketId,
    string PlayerId,
    string GameId,
    string? Region,
    int Skill,
    DateTimeOffset EnqueuedAt);

/// <summary>A running public matchmade lobby that still has room.</summary>
public sealed record BackfillCandidate(
    string LobbyId,
    string Region,
    LobbyState State,
    int OpenSeats,
    double AverageSkill);

/// <summary>Everything a strategy decides over for one game in one cycle.</summary>
public sealed record MatchmakingPool(
    string GameId,
    GameRules Rules,
    IReadOnlyList<MatchTicket> Tickets,
    IReadOnlyList<BackfillCandidate> Backfill,
    DateTimeOffset Now);

public abstract record MatchDecision(IReadOnlyList<string> TicketIds);

/// <summary>Seat these tickets in an existing lobby.</summary>
public sealed record BackfillDecision(string LobbyId, IReadOnlyList<string> TicketIds) : MatchDecision(TicketIds);

/// <summary>Place a new dedicated lobby and seat these tickets in it.</summary>
public sealed record NewLobbyDecision(string? Region, IReadOnlyList<string> TicketIds) : MatchDecision(TicketIds);

/// <summary>
/// Groups queued tickets. Pure: it sees a pool and returns decisions; the kernel applies them, skips any
/// decision that names a ticket twice or an unknown lobby, and leaves undecided tickets queued.
/// </summary>
public interface IMatchmakingStrategy
{
    IReadOnlyList<MatchDecision> Decide(MatchmakingPool pool);
}
