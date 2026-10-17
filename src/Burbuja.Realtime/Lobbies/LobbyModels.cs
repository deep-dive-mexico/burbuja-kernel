using Burbuja.Realtime.Abstractions;

namespace Burbuja.Realtime.Lobbies;

/// <summary>A held seat and the single-use credential to claim it over a transport.</summary>
/// <param name="ConnectUrl">Absolute when the server has a public URL; otherwise a path on the same origin.</param>
public sealed record SeatReservation(
    string Token,
    string LobbyId,
    string ServerId,
    string PlayerId,
    DateTimeOffset ExpiresAt,
    string ConnectUrl);

/// <summary>A reservation, or why there is none (a stable lowercase code an API client can switch on).</summary>
public sealed record ReservationResult(SeatReservation? Reservation, string? Error)
{
    public static ReservationResult Fail(string error) => new(null, error);
}

public sealed record LobbyMember(string PlayerId, string DisplayName, int Slot, bool Connected);

/// <summary>A consistent read of one lobby, taken under its lock.</summary>
public sealed record LobbySnapshot(
    string LobbyId,
    string GameId,
    string ServerId,
    string Region,
    LobbyState State,
    LobbyVisibility Visibility,
    LobbyOrigin Origin,
    string? JoinCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CountdownEndsAt,
    DateTimeOffset? StartedAt,
    int Seats,
    int MaxPlayers,
    double AverageSkill,
    IReadOnlyList<LobbyMember> Members,
    MatchResult? Result)
{
    public int OpenSeats => Math.Max(0, MaxPlayers - Seats);
}

/// <summary>What a lobby needs from whoever hosts it, given at creation.</summary>
public sealed record LobbySpec(
    string LobbyId,
    IRealtimeGame Game,
    string ServerId,
    string Region,
    string? PublicUrl,
    LobbyVisibility Visibility,
    LobbyOrigin Origin,
    string? JoinCode,
    IReadOnlyDictionary<string, string> Settings,
    int Seed);

/// <summary>Kernel timers a lobby applies on top of its game's rules.</summary>
public sealed record LobbyLimits(TimeSpan ReservationTtl, TimeSpan EmptyTimeout, int SendBufferFrames)
{
    public static LobbyLimits From(RealtimeOptions options) => new(
        TimeSpan.FromSeconds(Math.Max(1, options.ReservationTtlSeconds)),
        TimeSpan.FromSeconds(Math.Max(1, options.EmptyLobbyTimeoutSeconds)),
        Math.Max(8, options.SendBufferFrames));
}
