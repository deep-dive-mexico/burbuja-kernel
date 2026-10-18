namespace Burbuja.Realtime.Abstractions;

/// <summary>
/// The state a fleet of lobby nodes shares, and the channel between them. Each node keeps its own lobbies,
/// loops and connections; the backplane holds only what another node must see: which nodes and servers are
/// alive, where each lobby is, which lobby holds each player, the matchmaking tickets and the matchmaker
/// leases. A call from one node to another (place a lobby on your server, reserve these seats) travels over
/// <see cref="CallAsync"/>.
///
/// <para>The kernel's default is in-process: one node, or several nodes in one process sharing an instance
/// (the fleet tests). A shared implementation (the Redis plugin) lets nodes on different machines form one
/// fleet. Records carry the few fields an implementation must index and an opaque JSON
/// <c>Payload</c> it stores as given, so the kernel can evolve its records without a backplane change.</para>
///
/// <para>Every entry has a time to live, refreshed by the node that owns it, so a node that dies takes its
/// servers, lobbies and seat claims with it once they expire. A failure to reach the backplane throws
/// <see cref="RealtimeBackplaneException"/>; lobbies already running are unaffected.</para>
/// </summary>
public interface IRealtimeBackplane
{
    /// <summary>True when nodes in other processes can share this backplane. Such a fleet needs every server's
    /// public URL, since a player must connect to the node that hosts their lobby.</summary>
    bool IsDistributed { get; }

    // ─── nodes ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Publishes a node's record. False when another live instance already publishes this node id,
    /// or any server id. Node and server claims are acquired atomically, with no partial claims
    /// on refusal. Server ids are immutable for the lifetime of an instance.</summary>
    ValueTask<bool> PublishNodeAsync(BackplaneNode node, TimeSpan ttl, CancellationToken ct);

    /// <summary>Removes the node's record if <paramref name="instanceId"/> still owns it.</summary>
    ValueTask RemoveNodeAsync(string nodeId, string instanceId, CancellationToken ct);

    /// <summary>Every live node.</summary>
    ValueTask<IReadOnlyList<BackplaneNode>> ListNodesAsync(CancellationToken ct);

    // ─── lobbies ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Upserts lobby records and refreshes what they own: their join codes (for <paramref name="ttl"/>),
    /// and for <paramref name="seatTtl"/> the seat claims of <see cref="BackplaneLobby.Players"/> that still name
    /// the lobby. A refresh never creates a claim, so a seat released meanwhile stays released.</summary>
    ValueTask PublishLobbiesAsync(IReadOnlyList<BackplaneLobby> lobbies, TimeSpan ttl, TimeSpan seatTtl, CancellationToken ct);

    /// <summary>Removes the record, its listing and its join code.</summary>
    ValueTask RemoveLobbyAsync(BackplaneLobby lobby, CancellationToken ct);

    ValueTask<BackplaneLobby?> FindLobbyAsync(string lobbyId, CancellationToken ct);

    /// <summary>The records of <paramref name="gameId"/> published with <see cref="BackplaneLobby.Listed"/>.</summary>
    ValueTask<IReadOnlyList<BackplaneLobby>> ListLobbiesAsync(string gameId, CancellationToken ct);

    /// <summary>Takes a join code for a lobby; false when another lobby holds it.</summary>
    ValueTask<bool> ClaimJoinCodeAsync(string code, string lobbyId, TimeSpan ttl, CancellationToken ct);

    /// <summary>The lobby a join code names, if any.</summary>
    ValueTask<string?> FindJoinCodeAsync(string code, CancellationToken ct);

    // ─── seats: one lobby per player across the fleet ─────────────────────────────────────────

    /// <summary>Claims <paramref name="playerId"/>'s seat for <paramref name="lobbyId"/> when nobody holds it,
    /// and returns the lobby that holds it afterwards: <paramref name="lobbyId"/> on success (or when it already
    /// held the seat), another lobby's id otherwise.</summary>
    ValueTask<string> ClaimSeatAsync(string playerId, string lobbyId, TimeSpan ttl, CancellationToken ct);

    /// <summary>Moves a claim from <paramref name="fromLobbyId"/> to <paramref name="toLobbyId"/> only if it
    /// still names <paramref name="fromLobbyId"/>: the takeover of a claim whose lobby is gone.</summary>
    ValueTask<bool> TransferSeatAsync(string playerId, string fromLobbyId, string toLobbyId, TimeSpan ttl, CancellationToken ct);

    /// <summary>Releases the claim only if it names <paramref name="lobbyId"/>, so a late release cannot free a
    /// seat the player has since taken elsewhere.</summary>
    ValueTask ReleaseSeatAsync(string playerId, string lobbyId, CancellationToken ct);

    ValueTask<string?> SeatOfAsync(string playerId, CancellationToken ct);

    // ─── matchmaking tickets ─────────────────────────────────────────────────────────────────

    /// <summary>Adds a queued ticket, unless the player already has one queued: then that ticket is returned
    /// and nothing is added. Compare <see cref="BackplaneTicket.TicketId"/> to tell which.</summary>
    ValueTask<BackplaneTicket> AddTicketAsync(BackplaneTicket ticket, TimeSpan ttl, CancellationToken ct);

    ValueTask<BackplaneTicket?> GetTicketAsync(string ticketId, CancellationToken ct);

    /// <summary>The queued tickets of one game, oldest first.</summary>
    ValueTask<IReadOnlyList<BackplaneTicket>> QueuedTicketsAsync(string gameId, CancellationToken ct);

    /// <summary>
    /// Replaces a ticket only while it is still queued, so a cancel and a match racing on two nodes have exactly
    /// one winner. An update that leaves the queue (<see cref="BackplaneTicket.Queued"/> false) removes it from
    /// the queue and the player's queued slot and notifies <see cref="SubscribeTicketsAsync"/> subscribers.
    /// False when the ticket is gone or no longer queued.
    /// </summary>
    ValueTask<bool> UpdateTicketAsync(BackplaneTicket ticket, TimeSpan ttl, CancellationToken ct);

    /// <summary>Calls <paramref name="settled"/> with the id of every ticket that leaves the queue, on any node.
    /// Delivery is best effort: a waiter must also re-read the ticket on its own schedule.</summary>
    ValueTask<IAsyncDisposable> SubscribeTicketsAsync(Action<string> settled, CancellationToken ct);

    // ─── coordination ────────────────────────────────────────────────────────────────────────

    /// <summary>Acquires or renews a named lease for <paramref name="owner"/>. True while the owner holds it;
    /// false when another owner does. A lease that is not renewed expires after <paramref name="ttl"/>.</summary>
    ValueTask<bool> HoldLeaseAsync(string name, string owner, TimeSpan ttl, CancellationToken ct);

    ValueTask ReleaseLeaseAsync(string name, string owner, CancellationToken ct);

    // ─── calls between nodes ─────────────────────────────────────────────────────────────────

    /// <summary>Answers calls addressed to <paramref name="nodeId"/> until the returned handle is disposed.
    /// The handler's result is the reply.</summary>
    ValueTask<IAsyncDisposable> ServeAsync(string nodeId, Func<string, CancellationToken, Task<string>> handler, CancellationToken ct);

    /// <summary>Sends <paramref name="request"/> to the node serving <paramref name="nodeId"/> and returns its
    /// reply. Throws <see cref="TimeoutException"/> when no reply arrives within <paramref name="timeout"/>,
    /// whether the node is gone or slow; the caller cannot tell whether the request ran. A handler that throws
    /// fails the call with <see cref="RealtimeBackplaneException"/>.</summary>
    Task<string> CallAsync(string nodeId, string request, TimeSpan timeout, CancellationToken ct);
}

/// <summary>A node as the fleet sees it. <paramref name="InstanceId"/> is unique per process start, so a
/// duplicated node id is detected instead of two processes overwriting each other.</summary>
public sealed record BackplaneNode(string NodeId, string InstanceId, string Payload)
{
    public IReadOnlyList<string> ServerIds { get; init; } = [];
}

/// <summary>Where a lobby lives, and whether it is listed (public and not finished).</summary>
/// <param name="Players">Players whose seat claims this lobby owns, refreshed with the record.</param>
public sealed record BackplaneLobby(
    string LobbyId,
    string NodeId,
    string GameId,
    bool Listed,
    string? JoinCode,
    IReadOnlyList<string> Players,
    string Payload);

/// <summary>A matchmaking ticket. <paramref name="Queued"/> false means it has settled (matched, cancelled,
/// expired) and is kept only so its owner can read the outcome.</summary>
public sealed record BackplaneTicket(
    string TicketId,
    string PlayerId,
    string GameId,
    bool Queued,
    DateTimeOffset EnqueuedAt,
    string Payload);

/// <summary>The backplane could not be reached or refused an operation.</summary>
public sealed class RealtimeBackplaneException(string message, Exception? inner = null) : Exception(message, inner);
