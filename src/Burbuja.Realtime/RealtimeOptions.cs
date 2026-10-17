namespace Burbuja.Realtime;

/// <summary>The <c>Realtime</c> configuration section. The module is off unless <see cref="Enabled"/>.</summary>
public sealed class RealtimeOptions
{
    public const string Section = "Realtime";

    /// <summary>Maps <c>/realtime/v1</c> and starts the lobby fleet. Off by default: a host that serves
    /// no game must not expose a player surface by accident.</summary>
    public bool Enabled { get; set; }

    /// <summary>The <see cref="Abstractions.ILobbyPlacementStrategy.Name"/> the balancer uses:
    /// <c>least-loaded</c> (spread, the default) or <c>packed</c> (fill one server before the next).</summary>
    public string Placement { get; set; } = "least-loaded";

    /// <summary>Place a lobby outside the requested region when that region has no capacity.</summary>
    public bool AllowCrossRegionPlacement { get; set; } = true;

    /// <summary>The lobby servers this node hosts. Empty means one server named after the node.</summary>
    public List<LobbyServerOptions> Servers { get; set; } = [];

    /// <summary>How long a reserved seat waits for its connection before it is freed.</summary>
    public int ReservationTtlSeconds { get; set; } = 30;

    /// <summary>A pre-match lobby with no seats at all is closed after this long.</summary>
    public int EmptyLobbyTimeoutSeconds { get; set; } = 60;

    /// <summary>Frames buffered per connection before the oldest is dropped.</summary>
    public int SendBufferFrames { get; set; } = 256;

    /// <summary>Kernel API-key roles allowed to use the operator endpoints (fleet, drain, close).</summary>
    public string[] OperatorRoles { get; set; } = ["Owner"];

    public GuestOptions Guests { get; set; } = new();

    public MatchmakingOptions Matchmaking { get; set; } = new();
}

/// <summary>One in-process lobby server: a capacity unit the balancer places dedicated lobbies on.</summary>
public sealed class LobbyServerOptions
{
    public string Id { get; set; } = "";

    public string Region { get; set; } = "local";

    public int MaxLobbies { get; set; } = 100;

    /// <summary>The base URL players connect to (<c>wss://eu-1.example.com</c>). Null hands out a path
    /// relative to the API the player called.</summary>
    public string? PublicUrl { get; set; }
}

/// <summary>Anonymous players with a signed, expiring name — for demos, jams and local play.</summary>
public sealed class GuestOptions
{
    public bool Enabled { get; set; }

    public int TokenLifetimeMinutes { get; set; } = 720;

    /// <summary>HMAC key for guest tokens. Empty generates one per process, so guests sign in again after a
    /// restart; set it (32+ characters) for tokens that survive restarts or span several nodes.</summary>
    public string SigningKey { get; set; } = "";
}

public sealed class MatchmakingOptions
{
    public int IntervalMilliseconds { get; set; } = 250;

    public int TicketTimeoutSeconds { get; set; } = 120;

    /// <summary>How long a finished ticket stays readable for polling clients.</summary>
    public int TicketRetentionSeconds { get; set; } = 60;

    /// <summary>The skill difference accepted at once, and how it widens with every second of waiting.</summary>
    public int SkillWindow { get; set; } = 100;

    public int SkillWindowGrowthPerSecond { get; set; } = 25;

    public int MaxSkillWindow { get; set; } = 1000;
}
