namespace Burbuja.Realtime.Abstractions;

/// <summary>
/// One kind of lobby-scoped, server-authoritative session that players can queue for or host. A game is
/// registered as a DI service (<c>services.AddSingleton&lt;IRealtimeGame, MyGame&gt;()</c>), usually from a
/// plugin's <c>ConfigureServices</c>; the realtime module discovers every registration at boot.
///
/// <para>"Game" is the common case, not the limit: a quiz round, a shared whiteboard or a live auction is the
/// same shape — a bounded room, a roster, a lifecycle and messages — and may set <see cref="GameRules.TickRate"/>
/// to 0 to run event-driven.</para>
/// </summary>
public interface IRealtimeGame
{
    /// <summary>The id players and operators name it by. One lowercase segment, validated at boot by
    /// <see cref="RealtimeIds.IsValidGameId"/>.</summary>
    string Id { get; }

    string DisplayName { get; }

    GameRules Rules { get; }

    /// <summary>
    /// Creates the simulation for one dedicated lobby. Called once per lobby; the instance is owned by that
    /// lobby, called from its loop only, and disposed (when <see cref="IDisposable"/>) after the lobby closes.
    /// </summary>
    IGameSession CreateSession(GameSessionOptions options);

    /// <summary>The game's own grouping policy. Null uses the kernel's skill-window matchmaker.</summary>
    IMatchmakingStrategy? Matchmaking => null;
}

/// <summary>What a lobby tells the game when it creates the session.</summary>
/// <param name="Settings">Host-chosen lobby settings (map, mode), already bounded by the kernel. Never trusted
/// for anything a game would not let a player choose: a lobby creator wrote them.</param>
/// <param name="Seed">A per-lobby seed; the context's <see cref="ISessionContext.Random"/> is seeded with it.</param>
public sealed record GameSessionOptions(
    string LobbyId,
    string GameId,
    IReadOnlyDictionary<string, string> Settings,
    int Seed);

/// <summary>
/// The fixed facts of a game that the kernel enforces on its behalf: roster bounds, the tick, the lifecycle
/// timers and the inbound limits. A session never has to re-check them.
/// </summary>
public sealed record GameRules
{
    /// <summary>Connected players needed before the start countdown begins.</summary>
    public int MinPlayers { get; init; } = 2;

    /// <summary>Seats per lobby, counting unclaimed reservations.</summary>
    public int MaxPlayers { get; init; } = 8;

    /// <summary>Simulation ticks per second while running. 0 runs event-driven: no
    /// <see cref="IGameSession.OnTick"/>, messages are still delivered.</summary>
    public int TickRate { get; init; } = 20;

    /// <summary>Time between reaching <see cref="MinPlayers"/> and the match starting. Zero starts at once.</summary>
    public TimeSpan StartCountdown { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>A running match that has not ended itself by then is ended with reason <c>time_limit</c>.</summary>
    public TimeSpan MaxMatchDuration { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Whether a seat may be taken after the match started (matchmaking backfill or a direct join).</summary>
    public bool AllowJoinInProgress { get; init; } = true;

    /// <summary>How long a dropped player's seat is held during a running match before they are removed.</summary>
    public TimeSpan ReconnectGrace { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long an ended lobby keeps its connections so clients can show results.</summary>
    public TimeSpan ResultsLinger { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Sustained inbound messages per second per player; excess is dropped, not queued.</summary>
    public int MaxInboundMessagesPerSecond { get; init; } = 60;

    /// <summary>The largest inbound frame. A larger one closes the connection (1009).</summary>
    public int MaxMessageBytes { get; init; } = 4096;

    public const int MaxTickRate = 128;
    public const int MaxSeats = 256;

    /// <summary>Every problem with these rules, empty when they are usable. Checked at boot.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (MinPlayers < 1)
            errors.Add("MinPlayers must be at least 1");
        if (MaxPlayers < MinPlayers || MaxPlayers > MaxSeats)
            errors.Add($"MaxPlayers must be between MinPlayers and {MaxSeats}");
        if (TickRate < 0 || TickRate > MaxTickRate)
            errors.Add($"TickRate must be between 0 and {MaxTickRate}");
        if (StartCountdown < TimeSpan.Zero || ReconnectGrace < TimeSpan.Zero || ResultsLinger < TimeSpan.Zero)
            errors.Add("StartCountdown, ReconnectGrace and ResultsLinger cannot be negative");
        if (MaxMatchDuration <= TimeSpan.Zero)
            errors.Add("MaxMatchDuration must be positive");
        if (MaxInboundMessagesPerSecond < 1)
            errors.Add("MaxInboundMessagesPerSecond must be positive");
        if (MaxMessageBytes < 64 || MaxMessageBytes > 1024 * 1024)
            errors.Add("MaxMessageBytes must be between 64 and 1048576");
        return errors;
    }
}

public static class RealtimeIds
{
    /// <summary>A game id is one lowercase segment: a letter or digit, then letters, digits, <c>.</c>, <c>_</c>
    /// or <c>-</c>, at most 48 characters.</summary>
    public static bool IsValidGameId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 48
        && (char.IsAsciiLetterLower(id[0]) || char.IsAsciiDigit(id[0]))
        && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '-');
}
