# Burbuja.Plugins.Realtime.Redis

Redis/Valkey backplane for the Burbuja realtime fleet. Implements `IRealtimeBackplane` so lobby
nodes on different machines form one fleet: they share node records, lobby listings, join codes,
seat claims, matchmaking tickets and RPC calls through a common Redis server or cluster.

## Configuration

| Key | Required | Default | Description |
|---|---|---|---|
| `Realtime:Redis:Configuration` | **yes** | — | A StackExchange.Redis connection string, e.g. `localhost:6379` or `my-cluster.cache.amazonaws.com:6379,ssl=true,abortConnect=false` |
| `Realtime:Redis:KeyPrefix` | no | `{burbuja-rt}:` | Prefix for every key. The default is a Redis hash tag — see note below. |

The plugin throws `InvalidOperationException` naming `Realtime:Redis:Configuration` when the key
is missing or empty.

### Hash-tag prefix and Redis Cluster

The default prefix `{burbuja-rt}:` is intentionally a Redis hash tag. Redis hashes every key's
`{…}` portion for slot placement, so **every key produced by this plugin lands in the same slot**.
This allows the Lua scripts that touch multiple keys (claim seat, add ticket, batch lobby publish,
etc.) to work correctly on Redis Cluster and ElastiCache cluster-mode.

If you change the prefix, keep the `{…}` pattern (e.g. `{myapp-rt}:`) unless you are on a
single-node Redis where slot affinity is irrelevant.

## Key layout

With `P` = key prefix (e.g. `{burbuja-rt}:`)

| Key | Type | TTL | Content |
|---|---|---|---|
| `P node:{nodeId}` | Hash | node TTL | `i`=instanceId, `p`=payload |
| `P nodes` | Set | none | nodeId members (lazy-pruned on list) |
| `P lobby:{lobbyId}` | Hash | lobby TTL | `nid`, `gid`, `lst`, `jc`, `pls` (players JSON), `p`=payload |
| `P listed:{gameId}` | Set | none | listed lobbyId members (lazy-pruned on list) |
| `P code:{code}` | String | code TTL | lobbyId |
| `P seat:{playerId}` | String | seat TTL | lobbyId |
| `P ticket:{ticketId}` | Hash | ticket TTL | `pid`, `gid`, `q`=queued, `eat`=enqueuedAtMs, `p`=payload |
| `P queue:{gameId}` | ZSet | none | member=ticketId, score=enqueuedAtMs (lazy-pruned on list) |
| `P queued:{playerId}` | String | ticket TTL | ticketId (while ticket is queued) |
| `P lease:{name}` | String | lease TTL | owner |
| `P rpc:{nodeId}` | Pub/Sub channel | — | Incoming RPC envelopes for a node |
| `P reply:{guid}:{correlationId}` | Pub/Sub channel | — | Single-call reply channel |
| `P tickets` | Pub/Sub channel | — | Settled ticket IDs (best-effort notifications) |

## Atomicity guarantees

All compare-and-set operations (node publish, join-code claim, seat claim/transfer/release,
ticket add, ticket update/settle, lease acquire/renew/release) are implemented as Lua scripts
evaluated atomically on the Redis server. The `{burbuja-rt}` hash tag ensures all keys involved
in each script are in the same slot, satisfying Redis Cluster's cross-key script restriction.

Lobby batch publishes are pipelined (one round trip); each lobby's upsert and its join-code
refresh are individually atomic. Seat TTL refresh is a compare-and-extend, never a revive.

## Failure behaviour

Redis exceptions (`RedisException`, `RedisTimeoutException`, `RedisConnectionException`) are
wrapped in `RealtimeBackplaneException`. RPC reply channels use a per-instance prefix so
parallel backplane instances in one process do not interfere. A PUBLISH to a node with no active
server returns zero subscribers, which immediately throws `TimeoutException`. Pub/Sub unsubscribe
on dispose is best-effort.

## Compatibility notes

- **Valkey 8+**: fully supported; Valkey is Redis-compatible at the protocol level.
- **Redis 7.x / 6.x**: supported; all commands and scripts used are available in Redis 6+.
- **ElastiCache cluster mode**: supported when the hash-tag prefix is used (the default).
- **ElastiCache serverless**: uses a single endpoint; disable cluster mode; otherwise identical.
- **Sentinel / replica**: StackExchange.Redis handles transparent failover; configure the
  connection string with Sentinel endpoints.
