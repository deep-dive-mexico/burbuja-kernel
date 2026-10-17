using System.Text.Json;
using System.Text.Json.Nodes;

namespace Burbuja.Realtime.Abstractions;

/// <summary>
/// The authoritative simulation of one lobby. The kernel calls it from that lobby's loop only, one call at a
/// time, so a session keeps plain fields and needs no locks. A call must not block: it runs inside the tick
/// budget of every player in the lobby. An exception ends the match with reason <c>faulted</c> and closes the
/// lobby; it never reaches another lobby.
/// </summary>
public interface IGameSession
{
    /// <summary>A player took their seat (first connection). Called in every pre-end state.</summary>
    void OnPlayerJoined(ISessionContext context, RealtimePlayer player);

    /// <summary>A player's connection dropped (seat held for <see cref="GameRules.ReconnectGrace"/>) or came back.</summary>
    void OnPlayerConnectionChanged(ISessionContext context, RealtimePlayer player, bool connected);

    /// <summary>A player left the lobby for good; their seat is free.</summary>
    void OnPlayerLeft(ISessionContext context, RealtimePlayer player, LeaveReason reason);

    /// <summary>The countdown finished: the lobby is now <see cref="LobbyState.Running"/>.</summary>
    void OnMatchStarted(ISessionContext context);

    /// <summary>One game message from a connected player. <c>sys.*</c> messages never reach the session.</summary>
    void OnMessage(ISessionContext context, RealtimePlayer player, InboundMessage message);

    /// <summary>One fixed simulation step while running. <see cref="ISessionContext.TickDelta"/> is constant.</summary>
    void OnTick(ISessionContext context);

    /// <summary>The match ended, by the session itself or by the kernel (time limit, abandonment).</summary>
    void OnMatchEnded(ISessionContext context, MatchResult result);
}

/// <summary>An <see cref="IGameSession"/> whose every callback is an overridable no-op.</summary>
public abstract class GameSession : IGameSession
{
    public virtual void OnPlayerJoined(ISessionContext context, RealtimePlayer player) { }

    public virtual void OnPlayerConnectionChanged(ISessionContext context, RealtimePlayer player, bool connected) { }

    public virtual void OnPlayerLeft(ISessionContext context, RealtimePlayer player, LeaveReason reason) { }

    public virtual void OnMatchStarted(ISessionContext context) { }

    public virtual void OnMessage(ISessionContext context, RealtimePlayer player, InboundMessage message) { }

    public virtual void OnTick(ISessionContext context) { }

    public virtual void OnMatchEnded(ISessionContext context, MatchResult result) { }
}

/// <summary>
/// What a session may read and do. Valid only during the callback it was passed to. Outbound messages are
/// encoded once and fanned out to each connection's bounded buffer; a slow connection drops its oldest
/// frames rather than slowing the lobby, so state messages should be whole snapshots, not deltas that depend
/// on every earlier frame arriving.
/// </summary>
public interface ISessionContext
{
    string LobbyId { get; }
    string GameId { get; }
    LobbyState State { get; }
    IReadOnlyDictionary<string, string> Settings { get; }

    /// <summary>Seated players, connected or within their reconnect grace, ordered by slot.</summary>
    IReadOnlyList<RealtimePlayer> Players { get; }

    bool IsConnected(string playerId);

    /// <summary>Ticks simulated since the match started; 0 before the first tick.</summary>
    long Tick { get; }

    /// <summary>The fixed simulated time per tick (1 / <see cref="GameRules.TickRate"/>); zero when event-driven.</summary>
    TimeSpan TickDelta { get; }

    DateTimeOffset Now { get; }

    /// <summary>Time since the match started; zero before it starts.</summary>
    TimeSpan Elapsed { get; }

    /// <summary>A per-lobby random source seeded from <see cref="GameSessionOptions.Seed"/>.</summary>
    Random Random { get; }

    /// <summary>Sends one message to one connected player. A disconnected player is skipped.</summary>
    void Send<T>(string playerId, string type, T payload);

    /// <summary>Sends one message to every connected player, optionally except one.</summary>
    void Broadcast<T>(string type, T payload, string? exceptPlayerId = null);

    /// <summary>Ends the match after this callback returns. Later calls in the same match are ignored.</summary>
    void EndMatch(MatchResult result);

    /// <summary>Removes a player from the lobby after this callback returns.</summary>
    void Kick(string playerId, string reason);
}

/// <summary>One inbound game message. <paramref name="Data"/> is detached from the transport buffer, so a
/// session may keep it; <see cref="JsonValueKind.Undefined"/> when the frame had no <c>d</c>.</summary>
public sealed record InboundMessage(string Type, JsonElement Data, long? Sequence, DateTimeOffset ReceivedAt);

/// <summary>How a match ended. <paramref name="Details"/> is the game's own scoreboard, sent as-is.</summary>
public sealed record MatchResult(string Reason, IReadOnlyList<string> Winners, JsonObject? Details = null)
{
    public static MatchResult Without(string reason) => new(reason, []);
}

public enum LeaveReason
{
    /// <summary>The player asked to leave.</summary>
    Left,

    /// <summary>The connection dropped and the reconnect grace expired (or no grace applied).</summary>
    Disconnected,

    Kicked,

    /// <summary>The lobby closed with the player still seated.</summary>
    LobbyClosed,
}

/// <summary>A lobby's lifecycle. Moves forward only, except Countdown → Open when players drop below the minimum.</summary>
public enum LobbyState
{
    /// <summary>Accepting players; fewer than the minimum are connected.</summary>
    Open,

    /// <summary>The minimum is connected; the match starts when the countdown ends.</summary>
    Countdown,

    Running,

    /// <summary>The match is over; results are shown until the lobby closes.</summary>
    Ended,

    /// <summary>Connections are closed and the seat capacity is released. Terminal.</summary>
    Closed,
}

public enum LobbyVisibility
{
    /// <summary>Listed, and eligible for matchmaking backfill.</summary>
    Public,

    /// <summary>Unlisted; joined by id or join code only.</summary>
    Private,
}

/// <summary>Who asked for the lobby.</summary>
public enum LobbyOrigin
{
    Matchmaking,

    /// <summary>Created explicitly by a player or an operator through the lobby API.</summary>
    Hosted,
}
