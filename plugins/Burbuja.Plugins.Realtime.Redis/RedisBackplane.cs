using System.Collections.Concurrent;
using System.Text.Json;
using Burbuja.Realtime.Abstractions;
using StackExchange.Redis;

namespace Burbuja.Plugins.Realtime.Redis;

/// <summary>
/// Redis/Valkey <see cref="IRealtimeBackplane"/> implementation.
/// All keys share a hash tag so they land in one Redis Cluster slot and multi-key Lua scripts work.
/// See the plugin README for the full key layout.
/// </summary>
public sealed class RedisBackplane : IRealtimeBackplane, IAsyncDisposable
{
    private readonly IConnectionMultiplexer _mux;
    private readonly string _prefix;
    private readonly bool _ownsMultiplexer;

    // One reply channel per instance, subscribed once; replies are matched to calls by correlation id.
    private readonly RedisChannel _replyChannel;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _calls = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _replySubscription = new(1, 1);
    private bool _listening;

    // ─── Lua scripts ─────────────────────────────────────────────────────────────

    // PublishNodeAsync: set node hash if no live instance owns it (or this instance does).
    private static readonly string ScriptPublishNode = @"
local cur = redis.call('HGET', KEYS[1], 'i')
if cur and cur ~= '' and cur ~= ARGV[1] then return 0 end
local servers = redis.call('HGET', KEYS[1], 's')
if servers and servers ~= ARGV[5] then return 0 end
for k = 3, #KEYS do
  local owner = redis.call('GET', KEYS[k])
  if owner and owner ~= ARGV[6] then return 0 end
end
redis.call('HSET', KEYS[1], 'i', ARGV[1], 'p', ARGV[2], 's', ARGV[5])
redis.call('PEXPIRE', KEYS[1], ARGV[3])
redis.call('SADD', KEYS[2], ARGV[4])
for k = 3, #KEYS do redis.call('SET', KEYS[k], ARGV[6], 'PX', ARGV[3]) end
return 1";

    // RemoveNodeAsync: DEL if this instance still owns the node.
    private static readonly string ScriptRemoveNode = @"
local cur = redis.call('HGET', KEYS[1], 'i')
if cur == ARGV[1] then
  for k = 3, #KEYS do
    if redis.call('GET', KEYS[k]) == ARGV[3] then redis.call('DEL', KEYS[k]) end
  end
  redis.call('DEL', KEYS[1])
  redis.call('SREM', KEYS[2], ARGV[2])
end";

    // PublishOneLobby: upsert the lobby hash + listing + code refresh (PEXPIRE, no SET).
    private static readonly string ScriptPublishLobby = @"
redis.call('HSET', KEYS[1],
  'nid', ARGV[1], 'gid', ARGV[2], 'lst', ARGV[3], 'jc', ARGV[4], 'pls', ARGV[5], 'p', ARGV[6])
redis.call('PEXPIRE', KEYS[1], ARGV[7])
if ARGV[3] == '1' then
  redis.call('SADD', KEYS[2], ARGV[8])
else
  redis.call('SREM', KEYS[2], ARGV[8])
end
if ARGV[4] ~= '' then
  local cur = redis.call('GET', KEYS[3])
  if cur == ARGV[8] then redis.call('PEXPIRE', KEYS[3], ARGV[7]) end
end";

    // RefreshOneSeat: PEXPIRE the seat only if it still names this lobby.
    private static readonly string ScriptRefreshSeat = @"
local cur = redis.call('GET', KEYS[1])
if cur == ARGV[1] then redis.call('PEXPIRE', KEYS[1], ARGV[2]) end";

    // RemoveLobbyAsync: DEL lobby, SREM from listing, optionally DEL code.
    private static readonly string ScriptRemoveLobby = @"
redis.call('DEL', KEYS[1])
redis.call('SREM', KEYS[2], ARGV[1])
if ARGV[2] ~= '' then
  local cur = redis.call('GET', KEYS[3])
  if cur == ARGV[1] then redis.call('DEL', KEYS[3]) end
end";

    // ClaimJoinCodeAsync: SET NX-like with owner check.
    private static readonly string ScriptClaimCode = @"
local cur = redis.call('GET', KEYS[1])
if cur and cur ~= '' and cur ~= ARGV[1] then return 0 end
redis.call('SET', KEYS[1], ARGV[1], 'PX', ARGV[2])
return 1";

    // ClaimSeatAsync: SET if nobody else holds it; returns {0/1, holder}.
    private static readonly string ScriptClaimSeat = @"
local cur = redis.call('GET', KEYS[1])
if cur and cur ~= '' and cur ~= ARGV[1] then return {0, cur} end
redis.call('SET', KEYS[1], ARGV[1], 'PX', ARGV[2])
return {1, ARGV[1]}";

    // TransferSeatAsync: CAS from→to.
    private static readonly string ScriptTransferSeat = @"
local cur = redis.call('GET', KEYS[1])
if not cur or cur == '' or cur ~= ARGV[1] then return 0 end
redis.call('SET', KEYS[1], ARGV[2], 'PX', ARGV[3])
return 1";

    // ReleaseSeatAsync: DEL if it still names this lobby.
    private static readonly string ScriptReleaseSeat = @"
local cur = redis.call('GET', KEYS[1])
if cur == ARGV[1] then redis.call('DEL', KEYS[1]) end";

    // AddTicketAsync: add ticket unless player already has a live queued one. KEYS[4] is the ticket the caller
    // read from the player's slot (ARGV[7], '' for none); a different slot value means a race, and the caller
    // reads again, so the script touches only declared keys.
    // Returns: {-1} to retry, or {new(1)/existing(0), ticketId, playerId, gameId, queued('0'/'1'), enqueuedAtMs, payload}
    private static readonly string ScriptAddTicket = @"
local existId = redis.call('GET', KEYS[1]) or ''
if existId ~= ARGV[7] then return {-1} end
if existId ~= '' then
  local f = redis.call('HMGET', KEYS[4], 'pid', 'gid', 'q', 'eat', 'p')
  if f[3] == '1' then return {0, existId, f[1], f[2], f[3], f[4], f[5]} end
end
redis.call('HSET', KEYS[2], 'pid', ARGV[2], 'gid', ARGV[3], 'q', '1', 'eat', ARGV[4], 'p', ARGV[5])
redis.call('PEXPIRE', KEYS[2], ARGV[6])
redis.call('SET', KEYS[1], ARGV[1], 'PX', ARGV[6])
redis.call('ZADD', KEYS[3], ARGV[4], ARGV[1])
return {1, ARGV[1], ARGV[2], ARGV[3], '1', ARGV[4], ARGV[5]}";

    // UpdateTicketAsync: replace only while queued; settle removes from queue/player slot.
    // Returns: 0=not found/already settled, 1=updated+queued, 2=settled(notify).
    private static readonly string ScriptUpdateTicket = @"
local cur = redis.call('HGET', KEYS[1], 'q')
if not cur or cur ~= '1' then return 0 end
redis.call('HSET', KEYS[1], 'q', ARGV[4], 'p', ARGV[5])
redis.call('PEXPIRE', KEYS[1], ARGV[6])
local cq = redis.call('GET', KEYS[2])
if ARGV[4] == '0' then
  redis.call('ZREM', KEYS[3], ARGV[1])
  if cq == ARGV[1] then redis.call('DEL', KEYS[2]) end
  return 2
end
if cq == ARGV[1] then redis.call('PEXPIRE', KEYS[2], ARGV[6]) end
return 1";

    // HoldLeaseAsync: acquire or renew.
    private static readonly string ScriptHoldLease = @"
local cur = redis.call('GET', KEYS[1])
if cur and cur ~= '' and cur ~= ARGV[1] then return 0 end
redis.call('SET', KEYS[1], ARGV[1], 'PX', ARGV[2])
return 1";

    // ReleaseLeaseAsync: DEL if this owner holds it.
    private static readonly string ScriptReleaseLease = @"
local cur = redis.call('GET', KEYS[1])
if cur == ARGV[1] then redis.call('DEL', KEYS[1]) end";

    // ─── Constructor ─────────────────────────────────────────────────────────────

    /// <summary>Creates a backplane using an existing multiplexer. Disposes it when <paramref name="ownsMultiplexer"/> is true.</summary>
    public RedisBackplane(IConnectionMultiplexer mux, string prefix, bool ownsMultiplexer = false)
    {
        _mux = mux;
        _prefix = prefix;
        _ownsMultiplexer = ownsMultiplexer;
        _replyChannel = RedisChannel.Literal(prefix + "reply:" + Guid.NewGuid().ToString("N"));
    }

    public bool IsDistributed => true;

    // ─── Key helpers ─────────────────────────────────────────────────────────────

    /// <summary>Returns the full key string (not <see cref="RedisKey"/> to avoid ambiguous overload resolution).</summary>
    private string K(string suffix) => _prefix + suffix;

    private IDatabase Db() => _mux.GetDatabase();
    private ISubscriber Sub() => _mux.GetSubscriber();

    // ─── Nodes ───────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<bool> PublishNodeAsync(BackplaneNode node, TimeSpan ttl, CancellationToken ct)
    {
        try
        {
            var result = await Db().ScriptEvaluateAsync(ScriptPublishNode,
                [K("node:" + node.NodeId), K("nodes"), .. node.ServerIds.Select(id => K("server:" + id))],
                [node.InstanceId, node.Payload, (long)ttl.TotalMilliseconds, node.NodeId,
                 JsonSerializer.Serialize(node.ServerIds), JsonSerializer.Serialize(new[] { node.NodeId, node.InstanceId })]);
            return (long)result == 1L;
        }
        catch (Exception ex) when (IsRedisFailure(ex)) { throw new RealtimeBackplaneException("Failed to publish node.", ex); }
    }

    /// <inheritdoc/>
    public async ValueTask RemoveNodeAsync(string nodeId, string instanceId, CancellationToken ct)
    {
        try
        {
            var db = Db();
            var servers = await db.HashGetAsync(K("node:" + nodeId), "s");
            var ids = servers.IsNullOrEmpty ? [] : JsonSerializer.Deserialize<string[]>((string)servers!) ?? [];
            await db.ScriptEvaluateAsync(ScriptRemoveNode,
                [K("node:" + nodeId), K("nodes"), .. ids.Select(id => K("server:" + id))],
                [instanceId, nodeId, JsonSerializer.Serialize(new[] { nodeId, instanceId })]);
        }
        catch (Exception ex) when (IsRedisFailure(ex)) { throw new RealtimeBackplaneException("Failed to remove node.", ex); }
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<BackplaneNode>> ListNodesAsync(CancellationToken ct)
    {
        try
        {
            var db = Db();
            var members = await db.SetMembersAsync(K("nodes"));
            if (members.Length == 0) return [];

            var batch = db.CreateBatch();
            var tasks = members.Select(m => batch.HashGetAllAsync(K("node:" + m))).ToArray();
            batch.Execute();
            await Task.WhenAll(tasks);

            var result = new List<BackplaneNode>(members.Length);
            var dead = new List<RedisValue>();
            for (var i = 0; i < members.Length; i++)
            {
                var fields = tasks[i].Result;
                if (fields.Length == 0) { dead.Add(members[i]); continue; }
                var h = fields.ToDictionary(f => (string)f.Name!, f => (string?)f.Value);
                result.Add(new BackplaneNode((string)members[i]!, h.GetValueOrDefault("i") ?? "", h.GetValueOrDefault("p") ?? "{}")
                {
                    ServerIds = JsonSerializer.Deserialize<string[]>(h.GetValueOrDefault("s") ?? "[]") ?? [],
                });
            }

            if (dead.Count > 0)
                await db.SetRemoveAsync(K("nodes"), dead.ToArray());

            return result.OrderBy(n => n.NodeId, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (IsRedisFailure(ex)) { throw new RealtimeBackplaneException("Failed to list nodes.", ex); }
    }

    // ─── Lobbies ─────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask PublishLobbiesAsync(
        IReadOnlyList<BackplaneLobby> lobbies, TimeSpan ttl, TimeSpan seatTtl, CancellationToken ct)
    {
        if (lobbies.Count == 0) return;
        try
        {
            var db = Db();
            var batch = db.CreateBatch();
            var tasks = new List<Task>(lobbies.Count * 4);
            var ttlMs = (long)ttl.TotalMilliseconds;
            var seatTtlMs = (long)seatTtl.TotalMilliseconds;

            foreach (var lobby in lobbies)
            {
                var playersJson = JsonSerializer.Serialize(lobby.Players);
                tasks.Add(batch.ScriptEvaluateAsync(ScriptPublishLobby,
                    [K("lobby:" + lobby.LobbyId), K("listed:" + lobby.GameId), K("code:" + lobby.JoinCode)],
                    [lobby.NodeId, lobby.GameId, lobby.Listed ? "1" : "0",
                     lobby.JoinCode ?? "", playersJson, lobby.Payload, ttlMs, lobby.LobbyId]));

                foreach (var player in lobby.Players)
                {
                    tasks.Add(batch.ScriptEvaluateAsync(ScriptRefreshSeat,
                        [K("seat:" + player)],
                        [lobby.LobbyId, seatTtlMs]));
                }
            }

            batch.Execute();
            await Task.WhenAll(tasks);
        }
        catch (Exception ex) when (IsRedisFailure(ex)) { throw new RealtimeBackplaneException("Failed to publish lobbies.", ex); }
    }

    /// <inheritdoc/>
    public async ValueTask RemoveLobbyAsync(BackplaneLobby lobby, CancellationToken ct)
    {
        try
        {
            await Db().ScriptEvaluateAsync(ScriptRemoveLobby,
                [K("lobby:" + lobby.LobbyId), K("listed:" + lobby.GameId), K("code:" + lobby.JoinCode)],
                [lobby.LobbyId, lobby.JoinCode ?? "", lobby.GameId]);
        }
        catch (Exception ex) when (IsRedisFailure(ex)) { throw new RealtimeBackplaneException("Failed to remove lobby.", ex); }
    }

    /// <inheritdoc/>
    public async ValueTask<BackplaneLobby?> FindLobbyAsync(string lobbyId, CancellationToken ct)
    {
        try
        {
            var fields = await Db().HashGetAllAsync(K("lobby:" + lobbyId));
            return fields.Length == 0 ? null : LobbyFromHash(lobbyId, fields);
        }
        catch (Exception ex) when (IsRedisFailure(ex)) { throw new RealtimeBackplaneException("Failed to find lobby.", ex); }
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<BackplaneLobby>> ListLobbiesAsync(string gameId, CancellationToken ct)
    {
        try
        {
            var db = Db();
            var members = await db.SetMembersAsync(K("listed:" + gameId));
            if (members.Length == 0) return [];

            var batch = db.CreateBatch();
            var tasks = members.Select(m => batch.HashGetAllAsync(K("lobby:" + m))).ToArray();
            batch.Execute();
            await Task.WhenAll(tasks);

            var result = new List<BackplaneLobby>(members.Length);
            var dead = new List<RedisValue>();
            for (var i = 0; i < members.Length; i++)
            {
                var fields = tasks[i].Result;
                if (fields.Length == 0) { dead.Add(members[i]); continue; }
                var lobby = LobbyFromHash((string)members[i]!, fields);
                if (!lobby.Listed) { dead.Add(members[i]); continue; }
                result.Add(lobby);
            }

            if (dead.Count > 0)
                await db.SetRemoveAsync(K("listed:" + gameId), dead.ToArray());

            return result;
        }
        catch (Exception ex) when (IsRedisFailure(ex)) { throw new RealtimeBackplaneException("Failed to list lobbies.", ex); }
    }

    /// <inheritdoc/>
    public async ValueTask<bool> ClaimJoinCodeAsync(string code, string lobbyId, TimeSpan ttl, CancellationToken ct)
    {
        try
        {
            var result = await Db().ScriptEvaluateAsync(ScriptClaimCode,
                [K("code:" + code)],
                [lobbyId, (long)ttl.TotalMilliseconds]);
            return (long)result == 1L;
        }
        catch (Exception ex) when (IsRedisFailure(ex)) { throw new RealtimeBackplaneException("Failed to claim join code.", ex); }
    }

    /// <inheritdoc/>
    public async ValueTask<string?> FindJoinCodeAsync(string code, CancellationToken ct)
    {
        try
        {
            var val = await Db().StringGetAsync(K("code:" + code));
            return val.IsNullOrEmpty ? null : (string?)val;
        }
        catch (Exception ex) when (IsRedisFailure(ex)) { throw new RealtimeBackplaneException("Failed to find join code.", ex); }
    }

    // ─── Seats ───────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<string> ClaimSeatAsync(string playerId, string lobbyId, TimeSpan ttl, CancellationToken ct)
    {
        try
        {
            var result = await Db().ScriptEvaluateAsync(ScriptClaimSeat,
                [K("seat:" + playerId)],
                [lobbyId, (long)ttl.TotalMilliseconds]);
            var arr = (RedisResult[])result!;
            return (string)arr[1]!;
        }
        catch (Exception ex) when (IsRedisFailure(ex)) { throw new RealtimeBackplaneException("Failed to claim seat.", ex); }
    }

    /// <inheritdoc/>
    public async ValueTask<bool> TransferSeatAsync(
        string playerId, string fromLobbyId, string toLobbyId, TimeSpan ttl, CancellationToken ct)
    {
        try
        {
            var result = await Db().ScriptEvaluateAsync(ScriptTransferSeat,
                [K("seat:" + playerId)],
                [fromLobbyId, toLobbyId, (long)ttl.TotalMilliseconds]);
            return (long)result == 1L;
        }
        catch (Exception ex) when (IsRedisFailure(ex)) { throw new RealtimeBackplaneException("Failed to transfer seat.", ex); }
    }

    /// <inheritdoc/>
    public async ValueTask ReleaseSeatAsync(string playerId, string lobbyId, CancellationToken ct)
    {
        try
        {
            await Db().ScriptEvaluateAsync(ScriptReleaseSeat,
                [K("seat:" + playerId)],
                [lobbyId]);
        }
        catch (Exception ex) when (IsRedisFailure(ex)) { throw new RealtimeBackplaneException("Failed to release seat.", ex); }
    }

    /// <inheritdoc/>
    public async ValueTask<string?> SeatOfAsync(string playerId, CancellationToken ct)
    {
        try
        {
            var val = await Db().StringGetAsync(K("seat:" + playerId));
            return val.IsNullOrEmpty ? null : (string?)val;
        }
        catch (Exception ex) when (IsRedisFailure(ex)) { throw new RealtimeBackplaneException("Failed to get seat.", ex); }
    }

    // ─── Matchmaking tickets ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<BackplaneTicket> AddTicketAsync(BackplaneTicket ticket, TimeSpan ttl, CancellationToken ct)
    {
        try
        {
            var db = Db();
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var existing = (string?)await db.StringGetAsync(K("queued:" + ticket.PlayerId)) ?? "";
                var result = (RedisResult[])(await db.ScriptEvaluateAsync(ScriptAddTicket,
                    [K("queued:" + ticket.PlayerId), K("ticket:" + ticket.TicketId), K("queue:" + ticket.GameId),
                     K("ticket:" + (existing.Length > 0 ? existing : ticket.TicketId))],
                    [ticket.TicketId, ticket.PlayerId, ticket.GameId,
                     ticket.EnqueuedAt.ToUnixTimeMilliseconds(),
                     ticket.Payload,
                     (long)ttl.TotalMilliseconds,
                     existing]))!;
                if ((long)result[0] != -1)
                    return TicketFromResult(result);
            }

            throw new RealtimeBackplaneException($"Player '{ticket.PlayerId}' kept racing for a queue slot.");
        }
        catch (Exception ex) when (IsRedisFailure(ex)) { throw new RealtimeBackplaneException("Failed to add ticket.", ex); }
    }

    /// <inheritdoc/>
    public async ValueTask<BackplaneTicket?> GetTicketAsync(string ticketId, CancellationToken ct)
    {
        try
        {
            var fields = await Db().HashGetAllAsync(K("ticket:" + ticketId));
            return fields.Length == 0 ? null : TicketFromHash(ticketId, fields);
        }
        catch (Exception ex) when (IsRedisFailure(ex)) { throw new RealtimeBackplaneException("Failed to get ticket.", ex); }
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<BackplaneTicket>> QueuedTicketsAsync(string gameId, CancellationToken ct)
    {
        try
        {
            var db = Db();
            var members = await db.SortedSetRangeByScoreAsync(K("queue:" + gameId));
            if (members.Length == 0) return [];

            var batch = db.CreateBatch();
            var tasks = members.Select(m => batch.HashGetAllAsync(K("ticket:" + m))).ToArray();
            batch.Execute();
            await Task.WhenAll(tasks);

            var result = new List<BackplaneTicket>(members.Length);
            var dead = new List<RedisValue>();
            for (var i = 0; i < members.Length; i++)
            {
                var fields = tasks[i].Result;
                if (fields.Length == 0) { dead.Add(members[i]); continue; }
                var t = TicketFromHash((string)members[i]!, fields);
                if (!t.Queued) { dead.Add(members[i]); continue; }
                result.Add(t);
            }

            if (dead.Count > 0)
                await db.SortedSetRemoveAsync(K("queue:" + gameId), dead.ToArray());

            return result
                .OrderBy(t => t.EnqueuedAt)
                .ThenBy(t => t.TicketId, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (IsRedisFailure(ex)) { throw new RealtimeBackplaneException("Failed to list queued tickets.", ex); }
    }

    /// <inheritdoc/>
    public async ValueTask<bool> UpdateTicketAsync(BackplaneTicket ticket, TimeSpan ttl, CancellationToken ct)
    {
        try
        {
            var result = await Db().ScriptEvaluateAsync(ScriptUpdateTicket,
                [K("ticket:" + ticket.TicketId), K("queued:" + ticket.PlayerId), K("queue:" + ticket.GameId)],
                [ticket.TicketId, ticket.PlayerId, ticket.GameId,
                 ticket.Queued ? "1" : "0",
                 ticket.Payload,
                 (long)ttl.TotalMilliseconds]);
            var code = (long)result;
            if (code == 0) return false;
            if (code == 2)
            {
                // Notify subscribers of settlement.
                try
                {
                    await Sub().PublishAsync(RedisChannel.Literal(K("tickets")), ticket.TicketId);
                }
                catch (RedisException)
                {
                    // Best-effort; the contract says delivery is not guaranteed.
                }
            }

            return true;
        }
        catch (Exception ex) when (IsRedisFailure(ex)) { throw new RealtimeBackplaneException("Failed to update ticket.", ex); }
    }

    /// <inheritdoc/>
    public async ValueTask<IAsyncDisposable> SubscribeTicketsAsync(Action<string> settled, CancellationToken ct)
    {
        try
        {
            var sub = Sub();
            void OnMessage(RedisChannel _, RedisValue value)
            {
                if (!value.IsNullOrEmpty)
                    settled((string)value!);
            }

            await sub.SubscribeAsync(RedisChannel.Literal(K("tickets")), OnMessage);
            return new Disposer(async () => await sub.UnsubscribeAsync(
                RedisChannel.Literal(K("tickets")), OnMessage));
        }
        catch (Exception ex) when (IsRedisFailure(ex)) { throw new RealtimeBackplaneException("Failed to subscribe to tickets.", ex); }
    }

    // ─── Coordination (leases) ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<bool> HoldLeaseAsync(string name, string owner, TimeSpan ttl, CancellationToken ct)
    {
        try
        {
            var result = await Db().ScriptEvaluateAsync(ScriptHoldLease,
                [K("lease:" + name)],
                [owner, (long)ttl.TotalMilliseconds]);
            return (long)result == 1L;
        }
        catch (Exception ex) when (IsRedisFailure(ex)) { throw new RealtimeBackplaneException("Failed to hold lease.", ex); }
    }

    /// <inheritdoc/>
    public async ValueTask ReleaseLeaseAsync(string name, string owner, CancellationToken ct)
    {
        try
        {
            await Db().ScriptEvaluateAsync(ScriptReleaseLease,
                [K("lease:" + name)],
                [owner]);
        }
        catch (Exception ex) when (IsRedisFailure(ex)) { throw new RealtimeBackplaneException("Failed to release lease.", ex); }
    }

    // ─── RPC (calls between nodes) ───────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<IAsyncDisposable> ServeAsync(
        string nodeId, Func<string, CancellationToken, Task<string>> handler, CancellationToken ct)
    {
        var sub = Sub();
        var channel = RedisChannel.Literal(K("rpc:" + nodeId));

        // The subscriber callback only hands the request over; the handler runs on the thread pool.
        void OnMessage(RedisChannel from, RedisValue value)
        {
            if (!value.IsNullOrEmpty)
                _ = Task.Run(() => AnswerAsync((string)value!, handler));
        }

        try
        {
            await sub.SubscribeAsync(channel, OnMessage);
        }
        catch (Exception ex) when (IsRedisFailure(ex)) { throw new RealtimeBackplaneException($"Failed to serve node '{nodeId}'.", ex); }

        return new Disposer(async () =>
        {
            try { await sub.UnsubscribeAsync(channel, OnMessage); }
            catch (RedisException) { /* the connection is going away anyway */ }
        });
    }

    private async Task AnswerAsync(string message, Func<string, CancellationToken, Task<string>> handler)
    {
        RpcEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<RpcEnvelope>(message);
        }
        catch (JsonException)
        {
            return;
        }

        if (envelope is null)
            return;
        RpcReply reply;
        try
        {
            reply = new RpcReply(envelope.CorrelationId, false, await handler(envelope.Payload, CancellationToken.None));
        }
        catch (Exception ex)
        {
            reply = new RpcReply(envelope.CorrelationId, true, ex.Message);
        }

        try
        {
            await Sub().PublishAsync(RedisChannel.Literal(envelope.ReplyChannel), JsonSerializer.Serialize(reply));
        }
        catch (RedisException)
        {
            // The caller times out; it already cannot tell whether the request ran.
        }
    }

    /// <inheritdoc/>
    public async Task<string> CallAsync(string nodeId, string request, TimeSpan timeout, CancellationToken ct)
    {
        await ListenForRepliesAsync();
        var correlationId = Guid.NewGuid().ToString("N");
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _calls[correlationId] = pending;
        try
        {
            var envelope = JsonSerializer.Serialize(new RpcEnvelope(correlationId, _replyChannel.ToString(), request));
            // PUBLISH's receiver count covers only the shard that took it, so on a cluster zero proves nothing;
            // a node that is gone shows up as the timeout below.
            try
            {
                await Sub().PublishAsync(RedisChannel.Literal(K("rpc:" + nodeId)), envelope);
            }
            catch (Exception ex) when (IsRedisFailure(ex)) { throw new RealtimeBackplaneException($"Failed to call node '{nodeId}'.", ex); }

            try
            {
                return await pending.Task.WaitAsync(timeout, ct);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"Node '{nodeId}' did not answer within {timeout.TotalMilliseconds:F0} ms.");
            }
        }
        finally
        {
            _calls.TryRemove(correlationId, out _);
        }
    }

    private async Task ListenForRepliesAsync()
    {
        if (Volatile.Read(ref _listening))
            return;
        await _replySubscription.WaitAsync();
        try
        {
            if (_listening)
                return;
            await Sub().SubscribeAsync(_replyChannel, (_, value) =>
            {
                if (value.IsNullOrEmpty)
                    return;
                RpcReply? reply;
                try
                {
                    reply = JsonSerializer.Deserialize<RpcReply>((string)value!);
                }
                catch (JsonException)
                {
                    return;
                }

                if (reply is null || !_calls.TryRemove(reply.CorrelationId, out var call))
                    return;
                if (reply.Error)
                    call.TrySetException(new RealtimeBackplaneException($"The node failed to handle the call: {reply.Payload}"));
                else
                    call.TrySetResult(reply.Payload);
            });
            Volatile.Write(ref _listening, true);
        }
        catch (Exception ex) when (IsRedisFailure(ex)) { throw new RealtimeBackplaneException("Failed to listen for call replies.", ex); }
        finally
        {
            _replySubscription.Release();
        }
    }

    // ─── Dispose ─────────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        foreach (var call in _calls.Values)
            call.TrySetCanceled();
        if (_listening)
        {
            try { await Sub().UnsubscribeAsync(_replyChannel); }
            catch (RedisException) { /* closing */ }
        }

        if (_ownsMultiplexer)
            await _mux.CloseAsync();
    }

    // ─── Private helpers ─────────────────────────────────────────────────────────

    private static BackplaneLobby LobbyFromHash(string lobbyId, HashEntry[] fields)
    {
        var h = fields.ToDictionary(f => (string)f.Name!, f => (string?)f.Value);
        var playersJson = h.GetValueOrDefault("pls") ?? "[]";
        var players = JsonSerializer.Deserialize<List<string>>(playersJson) ?? [];
        return new BackplaneLobby(
            lobbyId,
            h.GetValueOrDefault("nid") ?? "",
            h.GetValueOrDefault("gid") ?? "",
            (h.GetValueOrDefault("lst") ?? "0") == "1",
            string.IsNullOrEmpty(h.GetValueOrDefault("jc")) ? null : h["jc"],
            players,
            h.GetValueOrDefault("p") ?? "{}");
    }

    private static BackplaneTicket TicketFromHash(string ticketId, HashEntry[] fields)
    {
        var h = fields.ToDictionary(f => (string)f.Name!, f => (string?)f.Value);
        return new BackplaneTicket(
            ticketId,
            h.GetValueOrDefault("pid") ?? "",
            h.GetValueOrDefault("gid") ?? "",
            (h.GetValueOrDefault("q") ?? "0") == "1",
            DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(h.GetValueOrDefault("eat") ?? "0")),
            h.GetValueOrDefault("p") ?? "{}");
    }

    private static BackplaneTicket TicketFromResult(RedisResult[] arr)
    {
        // Result: {new/existing flag, ticketId, playerId, gameId, queued, enqueuedAtMs, payload}
        var ticketId = (string)arr[1]!;
        var playerId = (string)arr[2]!;
        var gameId = (string)arr[3]!;
        var queued = arr[4].ToString() == "1";
        var eatMs = long.Parse((string)arr[5]!);
        var payload = (string)arr[6]!;
        return new BackplaneTicket(ticketId, playerId, gameId, queued,
            DateTimeOffset.FromUnixTimeMilliseconds(eatMs), payload);
    }

    /// <summary>StackExchange.Redis reports a timeout as <see cref="RedisTimeoutException"/>, a
    /// <see cref="TimeoutException"/> rather than a <see cref="RedisException"/>; both mean the backplane failed.</summary>
    private static bool IsRedisFailure(Exception ex) => ex is RedisException or RedisTimeoutException;

    private sealed class Disposer(Func<ValueTask> dispose) : IAsyncDisposable
    {
        private int _done;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                return dispose();
            return ValueTask.CompletedTask;
        }
    }

    // ─── RPC envelope types ──────────────────────────────────────────────────────

    private sealed record RpcEnvelope(string CorrelationId, string ReplyChannel, string Payload);
    private sealed record RpcReply(string CorrelationId, bool Error, string Payload);
}
