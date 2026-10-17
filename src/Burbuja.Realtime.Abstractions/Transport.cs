namespace Burbuja.Realtime.Abstractions;

/// <summary>
/// The wire contract shared by every transport. Each frame is one UTF-8 JSON object:
/// <c>{"t":"type","d":payload,"s":clientSeq}</c> inbound and <c>{"t":"type","k":tick,"d":payload}</c> outbound.
/// Types starting with <see cref="SystemPrefix"/> belong to the kernel; a game's own types must not.
/// </summary>
public static class RealtimeProtocol
{
    public const string BasePath = "/realtime/v1";
    public const string ConnectPath = BasePath + "/connect";

    public const string SystemPrefix = "sys.";

    /// <summary>Server → client, once per connection: your player, the lobby, the roster and the rules.</summary>
    public const string Welcome = "sys.welcome";

    /// <summary>Server → clients: the lobby's state or roster changed.</summary>
    public const string Lobby = "sys.lobby";

    /// <summary>Server → clients: the match ended, with its result.</summary>
    public const string Ended = "sys.ended";

    /// <summary>Server → client: a refused message (rate limit, unknown system type, bad frame).</summary>
    public const string Error = "sys.error";

    /// <summary>Client → server, answered with <see cref="Pong"/> carrying the client's data and server time.</summary>
    public const string Ping = "sys.ping";

    public const string Pong = "sys.pong";

    /// <summary>Client → server: leave the lobby for good (no reconnect grace).</summary>
    public const string Leave = "sys.leave";

    public static bool IsSystemType(string type) => type.StartsWith(SystemPrefix, StringComparison.Ordinal);
}

/// <summary>
/// One open transport connection, as the lobby uses it. Both methods are non-blocking: the lobby loop calls
/// them inside its tick and must never wait on a client's network.
/// </summary>
public interface IPlayerConnection
{
    string ConnectionId { get; }

    /// <summary>Queues one complete, already-encoded UTF-8 text frame. The buffer is shared between
    /// connections and must not be modified. Returns false once the connection is closing.</summary>
    bool TrySend(ReadOnlyMemory<byte> utf8Frame);

    /// <summary>Starts an orderly close after the queued frames.</summary>
    void Close(RealtimeCloseCode code, string reason);
}

/// <summary>Where a transport delivers what it receives for an admitted connection.</summary>
public interface IConnectionInbox
{
    /// <summary>One complete inbound frame. The span is only valid during the call.</summary>
    void Receive(ReadOnlySpan<byte> utf8Frame);

    /// <summary>The connection is gone (closed by either side or broken). Idempotent.</summary>
    void Disconnected();
}

/// <summary>
/// The seam a transport plugs into: a WebSocket endpoint, a test harness, or a future UDP/WebTransport
/// listener. The transport authenticates nothing itself — the seat reservation token is the credential.
/// </summary>
public interface IRealtimeGateway
{
    /// <summary>Claims a seat with <paramref name="reservationToken"/>. On success the transport must deliver
    /// frames to the returned inbox and call <see cref="IConnectionInbox.Disconnected"/> exactly when the
    /// connection ends.</summary>
    GatewayAdmission Admit(string reservationToken, IPlayerConnection connection);
}

/// <param name="MaxMessageBytes">The admitted game's inbound frame bound; the transport closes a connection
/// (1009) that sends a larger frame, before buffering it whole.</param>
public sealed record GatewayAdmission(IConnectionInbox? Inbox, string? Error, int MaxMessageBytes = 0)
{
    public bool Accepted => Inbox is not null;

    public static GatewayAdmission Refused(string error) => new(null, error);
}

/// <summary>WebSocket close codes; 4000+ are the kernel's application codes.</summary>
public enum RealtimeCloseCode
{
    Normal = 1000,
    GoingAway = 1001,
    UnsupportedData = 1003,
    PolicyViolation = 1008,
    MessageTooBig = 1009,
    InternalError = 1011,
    InvalidReservation = 4001,
    Kicked = 4002,
    LobbyClosed = 4003,
    Replaced = 4004,
    Left = 4005,
}
