using System.Text.Json.Serialization;
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
    [JsonIgnore]
    public int OpenSeats => Math.Max(0, MaxPlayers - Seats);

    /// <summary>Ticks simulated since the match started.</summary>
    public long Tick { get; init; }

    /// <summary>The fleet node hosting the lobby.</summary>
    public string NodeId { get; init; } = "";

    /// <summary>Every player holding a seat, including reservations not yet claimed by a connection.</summary>
    public IReadOnlyList<string> SeatHolders { get; init; } = [];

    public LobbyPerformance Performance { get; init; } = LobbyPerformance.Idle;
}

/// <summary>
/// How a lobby's simulation kept up. The window values cover its last measurement window (about one second of
/// lobby time); <paramref name="Overruns"/> and <paramref name="DroppedTicks"/> count since it was created.
/// </summary>
/// <param name="Busy">Time spent in the lobby's steps (game code and the kernel's own work), measured by the
/// stopwatch, per second of lobby time: 0.25 is a quarter of a core. Zero once the lobby has closed.</param>
/// <param name="Overruns">Ticks whose <see cref="IGameSession.OnTick"/> took longer than the tick interval.</param>
/// <param name="DroppedTicks">Ticks skipped because the lobby fell further behind than its catch-up budget.</param>
public sealed record LobbyPerformance(
    double Busy,
    int TicksInWindow,
    double AverageTickMilliseconds,
    double MaxTickMilliseconds,
    long Overruns,
    long DroppedTicks)
{
    public static readonly LobbyPerformance Idle = new(0, 0, 0, 0, 0, 0);
}

/// <summary>What a lobby needs from whoever hosts it, given at creation.</summary>
public sealed record LobbySpec(
    string LobbyId,
    IRealtimeGame Game,
    string NodeId,
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
