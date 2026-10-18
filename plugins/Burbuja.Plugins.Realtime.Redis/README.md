# Burbuja.Plugins.Realtime.Redis

Redis/Valkey backplane for the Burbuja realtime fleet ([guide](../../docs/realtime-lobbies.md#fleet-many-nodes)).
It implements `IRealtimeBackplane`, so lobby nodes on different machines form one fleet. They share node
records, lobby listings, join codes, seat claims, matchmaking tickets and calls between nodes through one
Redis or Valkey server or cluster.

## Install

It is a dynamic plugin, so a host references nothing. Publish it into each node's plugins directory and
configure it:

```sh
dotnet publish plugins/Burbuja.Plugins.Realtime.Redis -c Release -o <host>/plugins/Realtime.Redis
```

Every node of a fleet then needs the same `Realtime:Redis:*` settings, its own `Kernel:NodeId`, a
`PublicUrl` on every server, and a shared `Realtime:Guests:SigningKey` if guests are enabled.

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
| `P node:{nodeId}` | Hash | node TTL | `i`=instanceId, `p`=payload, `s`=immutable server-id list |
| `P server:{serverId}` | String | node TTL | `[nodeId, instanceId]`; claimed atomically with the node |
| `P nodes` | Set | none | nodeId members (lazy-pruned on list) |
| `P lobby:{lobbyId}` | Hash | lobby TTL | `nid`, `gid`, `lst`, `jc`, `pls` (players JSON), `p`=payload |
| `P listed:{gameId}` | Set | none | listed lobbyId members (lazy-pruned on list) |
| `P code:{code}` | String | code TTL | lobbyId |
| `P seat:{playerId}` | String | seat TTL | lobbyId |
| `P ticket:{ticketId}` | Hash | ticket TTL | `pid`, `gid`, `q`=queued, `eat`=enqueuedAtMs, `p`=payload |
| `P queue:{gameId}` | ZSet | none | member=ticketId, score=enqueuedAtMs (lazy-pruned on list) |
| `P queued:{playerId}` | String | ticket TTL | ticketId (while ticket is queued) |
| `P lease:{name}` | String | lease TTL | owner |
| `P rpc:{address}` | Pub/Sub channel | — | Incoming RPC envelopes; fleet address is JSON `[nodeId, instanceId]` |
| `P reply:{guid}` | Pub/Sub channel | — | One per backplane instance; replies carry the call's correlation id |
| `P tickets` | Pub/Sub channel | — | Settled ticket IDs (best-effort notifications) |

## Atomicity guarantees

All compare-and-set operations (node publish, join-code claim, seat claim/transfer/release,
ticket add, ticket update/settle, lease acquire/renew/release) are implemented as Lua scripts
evaluated atomically on the Redis server. Each script touches only the keys it declares, and the
`{burbuja-rt}` hash tag puts them in one slot, satisfying Redis Cluster's cross-key script rule.
Adding a ticket first reads the player's queue slot and passes the queued ticket as a declared key;
a slot that changed in between makes the script ask for a retry.

Lobby batch publishes are pipelined (one round trip); each lobby's upsert and its join-code
refresh are individually atomic. Seat TTL refresh is a compare-and-extend, never a revive.

## Failure behaviour

Redis exceptions (`RedisException`, `RedisTimeoutException`, `RedisConnectionException`) are
wrapped in `RealtimeBackplaneException`; the HTTP API answers `503 fleet_unavailable`, and lobbies
already running keep running. Each backplane instance subscribes one reply channel once and matches
replies by correlation id; requests are handed to the thread pool rather than run on the subscriber
callback. A call to a node that is gone fails with `TimeoutException` after the call timeout; PUBLISH's receiver count
is not used, because on a cluster it covers only one shard. Pub/Sub delivery is at most once: a lost reply is a timeout, and a lost
ticket notification is covered by the waiter's own re-read.

## Compatibility notes

- **Valkey 8+**: fully supported; Valkey is Redis-compatible at the protocol level.
- **Redis 7.x / 6.x**: supported; all commands and scripts used are available in Redis 6+.
- **ElastiCache cluster mode**: supported when the hash-tag prefix is used (the default).
- **ElastiCache Serverless for Valkey**: a cluster-compatible endpoint; the hash-tag prefix keeps every
  script on one slot.
- **Sentinel / replicas**: StackExchange.Redis follows a failover; configure the connection string with
  the Sentinel endpoints. Writes acknowledged by a primary that fails before replicating can be lost;
  the fleet repairs node and lobby records on the next heartbeat, and a lost seat claim or ticket is
  the one gap.
- **Scale**: all keys share one slot, so one shard serves the fleet. Fleet state is small (a record per
  node, lobby and queued player) and heartbeats are batched, so a single shard covers a large fleet.
