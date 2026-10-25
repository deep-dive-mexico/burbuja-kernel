# Real-time lobbies

[Kernel wiki](wiki/OVERVIEW.md) · [Runtime capabilities](runtime-capabilities.md) · [Arena PoC](../examples/Example.Arena/README.md) · [Blast example](../examples/Example.Blast/README.md) · [Redis backplane](../plugins/Burbuja.Plugins.Realtime.Redis/README.md)

The realtime module hosts **dedicated, server-authoritative lobbies** for multiplayer games and other
room-shaped, low-latency uses (live quizzes, shared boards). It owns the parts every such server
repeats: matchmaking, placement across lobby servers and nodes, the lobby lifecycle, the fixed-tick loop,
seat reservations, reconnects, inbound limits and the transport. A game supplies only its simulation.

| Project | Owns |
|---|---|
| `src/Burbuja.Realtime.Abstractions` | Contracts with no dependencies: `IRealtimeGame`, `GameRules`, `IGameSession`/`GameSession`, `ISessionContext`, players and `IPlayerAuthenticator`, `IPlayerConnection`/`IConnectionInbox`/`IRealtimeGateway`, `ILobbyPlacementStrategy`, `IMatchmakingStrategy`, `IRealtimeBackplane` (what a fleet shares), the wire protocol, `RealtimeJson` (game JSON options with numerics vectors) and `RealtimeBinary` (the binary envelope) |
| `src/Burbuja.Realtime` | `Lobby` (state machine and loop), `LobbyManager` (this node's lifecycle, codes, reservations, admission), `LobbyServer`/`LobbyBalancer` (capacity and placement), `RealtimeFleet` (routing between nodes, one seat per player), `LocalBackplane` (the in-process default), `MatchmakingQueue`/`SkillWindowMatchmaker`, guest and API-key players, the WebSocket transport and `/realtime/v1` |
| `plugins/Burbuja.Plugins.Realtime.Redis` | The shared backplane: nodes on many machines form one fleet over Redis or Valkey |
| `examples/Example.Arena` | The PoC: a 2–8 player arena shooter installed as a dynamic plugin, and a host with a browser client |
| `examples/Example.Blast` | A 3D battle game for 2–8 players after Bomberman 64's Battle mode, with CPU players and a three.js client; the fleet's end-to-end proof |

`Burbuja.Hosting` registers the module for every host (`AddBurbujaRealtime`) and maps it for every
role, like plugin ingress: a player connects to the node hosting the lobby. **It is inert unless
`Realtime:Enabled` is true**, so an existing consumer gains no player surface by upgrading.

## Writing a game

Register an `IRealtimeGame` from a plugin's `ConfigureServices`; the plugin references only
`Burbuja.Core.Abstractions` and `Burbuja.Realtime.Abstractions`. Any `Burbuja.*.Abstractions`
assembly is shared into plugin load contexts, so a dynamically installed game sees the host's contract
types (the Arena test asserts it loads in its own context).

```csharp
public sealed class MyGame : IRealtimeGame
{
    public string Id => "mygame";                     // one lowercase segment
    public string DisplayName => "My game";
    public GameRules Rules { get; } = new() { MinPlayers = 2, MaxPlayers = 8, TickRate = 30 };
    public IGameSession CreateSession(GameSessionOptions options) => new MySession();
}

sealed class MySession : GameSession
{
    public override void OnMessage(ISessionContext ctx, RealtimePlayer p, InboundMessage m) { /* store intent */ }
    public override void OnTick(ISessionContext ctx) { /* simulate one TickDelta, then */ ctx.Broadcast("state", snapshot); }
}
```

**Threading contract.** A session is called from its lobby's loop only, one call at a time, so it keeps
plain fields and no locks. A call must not block or await I/O: it spends every player's tick budget.
`EndMatch` and `Kick` take effect after the callback returns. An exception from game code ends that
lobby (`sys.ended` with reason `faulted`, close code 1011) and nothing else
(`A_faulting_game_closes_only_its_own_lobby`).

**CPU players.** Rules fix the minimum a matchmade lobby is grouped at. A session whose own settings add CPU
players may start with fewer humans by answering `MinPlayersToStart` (read once, kept between 1 and
`GameRules.MinPlayers`); Blast starts with one human when its lobby has bots
(`A_session_with_cpu_players_may_start_below_the_rules_minimum_but_never_below_one`).

**Authority.** Clients send intent; the session decides outcomes. The kernel enforces what the rules
declare (seats, rate, frame size, durations); the game validates what only it understands, as Arena
clamps and normalises its `input` vector.

**Dimensions are the game's.** The kernel has no notion of a world, a map or an axis count: a 2D shooter
and a 3D flight game are the same `GameSession` with different state. `System.Numerics` `Vector2`,
`Vector3`, `Vector4` and `Quaternion` serialize as `{"x":…,"y":…,"z":…}` out of the box
(`Numerics_vectors_travel_as_named_components_not_as_empty_objects`). Plain System.Text.Json writes them as `{}`.

**Outbound.** `Broadcast` encodes once and fans the same bytes out. Each connection's buffer
(`Realtime:SendBufferFrames`, default 256) **drops its oldest frame when full**, so a slow client gets the
newest state instead of stalling the lobby. Design state messages as whole snapshots, not deltas that
need every earlier frame.

## Game datatypes and encodings

A game defines its own message types and picks their encoding per message:

- **Typed JSON.** Read client input as the game's own type with `message.TryRead<MyInput>(out var input)`.
  A payload that does not fit returns false, so malformed client input is simply ignored. `Read<T>()`
  throws instead. Payloads written with `Send`/`Broadcast` use the same options.
- **The game's serializer options.** `IRealtimeGame.SerializerOptions` (default `RealtimeJson.Default`)
  governs every JSON payload the game sends and reads. Start from `RealtimeJson.CreateOptions()` to keep
  the vector converters and add the game's converters, naming policy or a source-generated
  `TypeInfoResolver`. Kernel `sys.*` messages keep the kernel's options, so a game cannot change their
  shape (`A_game_brings_its_own_serializer_options_and_the_kernel_keeps_its_own`).
- **Binary.** `SendBinary`/`BroadcastBinary` send the game's own bytes, such as packed structs,
  MessagePack or FlatBuffers. A binary client message reaches `OnMessage` with
  `Format == FrameFormat.Binary` and the bytes in `message.Binary`. A broadcast is encoded once. The
  envelope (`RealtimeBinary`) is `[u8 n][n bytes: type, printable ASCII without spaces][i64 LE][payload]`, where the number is the
  server tick outbound and the client's sequence inbound, the same roles as `k` and `s` in JSON. Binary
  frames obey the same rate and size limits. `sys.*` types are refused in binary
  (`Binary_frames_carry_the_games_own_encoding_both_ways`).

Arena accepts its `input` both ways: JSON `{x,y,aim,fire}` or 13 binary bytes (`f32 x, y, aim, u8 fire`).

## Netcode: ticks, time and acknowledgement

The kernel provides the server-side half of client prediction and interpolation. The client-side half
is the game's:

- **Tick.** Every outbound frame carries the tick it was produced in (`k`, or the binary envelope's
  number). `sys.welcome` gives `tickRate`, `tickIntervalMs`, the current `tick` and `serverTime`.
  `sys.ping`/`sys.pong` give round-trip time and clock offset.
- **Input acknowledgement.** A client numbers its inputs (`s` in JSON, the envelope's number in binary).
  `ISessionContext.LastSequence(playerId)` is the highest sequence the session has received on that
  player's current connection. It never moves back, and a JSON message without `s` leaves it unchanged. Put it in that player's state, as Arena does with `ack`. A predicting
  client then drops the inputs the snapshot already includes and replays the rest. A new connection
  starts again at null (`A_3d_session_reads_typed_input_acknowledges_it_and_broadcasts_typed_state`).
- **Fixed step.** `OnTick` always advances one constant `TickDelta`, so the simulation is deterministic
  given its inputs and the seeded `ISessionContext.Random`.

**Tick budget.** Each lobby measures itself. `LobbyPerformance` reports these per measurement window
(one second): busy fraction (time in the lobby's steps, game code and kernel work, per second, so 0.25
is a quarter of a core; zero once closed),
ticks run, and average and worst tick time. Two counters run for the whole lobby: **overruns** (ticks
longer than the tick interval, logged at most once per window) and **dropped ticks** (skipped after the
catch-up budget). Operators see this per lobby in `GET /servers`
(`A_lobby_accounts_for_every_tick_it_drops_and_publishes_its_load`, `A_tick_over_its_budget_is_counted_as_an_overrun`).

## Lifecycle

```
Open ──(MinPlayers connected)──▶ Countdown ──(StartCountdown elapsed)──▶ Running ──(EndMatch | MaxMatchDuration | all seats gone)──▶ Ended ──(ResultsLinger | nobody connected)──▶ Closed
  ▲                                  │
  └──────(below MinPlayers)──────────┘
```

- A **seat** is taken by a reservation and counts against `MaxPlayers` until it expires
  (`Realtime:ReservationTtlSeconds`, default 30) or is claimed by a connection.
- A pre-match lobby with no seats closes after `Realtime:EmptyLobbyTimeoutSeconds` (default 60).
- A dropped connection keeps its seat and slot for `GameRules.ReconnectGrace`. Reserving the same lobby
  again returns a fresh token for that seat; the session sees `OnPlayerConnectionChanged`.
- **One seat per player.** A player in a lobby that is still playing cannot queue or join elsewhere.
  `Ended` releases everyone immediately, so "play again" works from the results screen.
- Each step drains inbound, applies joins/leaves, runs due ticks at a fixed `TickDelta` (at most five
  catch-up ticks; beyond that ticks are dropped), applies lifecycle transitions and flushes one
  coalesced `sys.lobby` update. `Step(now)` takes its clock as an argument; the unit tests drive the
  entire lifecycle with a fake clock.
- Closing releases the server's lobby slot and disposes the session.

## Matchmaking and placement

`POST /realtime/v1/queue` creates one ticket per player. Every `Realtime:Matchmaking:IntervalMilliseconds`
(default 250) the queue passes each game's queued tickets and its backfill candidates (public matchmade
lobbies with open seats, still joinable under the rules) to the game's `IMatchmakingStrategy`, or to the
default `SkillWindowMatchmaker`:

- oldest ticket first; its acceptable skill gap starts at `SkillWindow` (100) and widens by
  `SkillWindowGrowthPerSecond` (25) up to `MaxSkillWindow` (1000);
- it **backfills the fullest compatible lobby** first, otherwise it anchors a group of compatible tickets
  up to `MaxPlayers`, which becomes a new lobby once it reaches `MinPlayers`;
- regions are compatible when equal or when either side has none.

Skill is the identity attribute `skill`, set by the authenticator; a client cannot claim it. Strategies
are pure: the queue rejects decisions that reuse or invent tickets, or name a lobby that was not a backfill
candidate. When no server has room, tickets stay
queued with `detail: "waiting_for_capacity"`; they expire after `TicketTimeoutSeconds` (120).

**Lobby servers.** `Realtime:Servers` lists the capacity units this node hosts
(`Id`, `Region`, `MaxLobbies`, optional `PublicUrl` for the connect URL). The balancer removes
draining and full servers, prefers the requested region (falling back elsewhere unless
`AllowCrossRegionPlacement` is false) and claims a slot atomically, so concurrent placements never exceed
capacity (`Concurrent_placements_never_exceed_capacity`: 400 racing requests, 100 slots). The strategy
only ranks: `least-loaded` (default) spreads load, `packed` fills one server before the next so idle ones
can be scaled away. Each `LobbyServerStatus` also carries `Load`, the measured simulation compute of its
lobbies (the sum of their busy fractions, roughly cores in use). A 60 Hz eight-player match and an idle
quiz room weigh differently there. `least-loaded` uses it to break ties between equally allocated
servers, and a custom strategy may rank by it. Status reads are lock-free, so placement never waits on a
running tick (`Placement_reads_never_wait_on_a_running_tick`). Register more `ILobbyPlacementStrategy` services and select them by name with
`Realtime:Placement`. Across a fleet the balancer ranks every live server, as described next.

## Fleet: many nodes

Fleet identity is generation-specific: a node publication atomically claims its node id and
immutable server-id list. A rejected renewal permanently fences that process from new fleet
mutations and publication; restart it with unique ids. Calls resolve the live instance and use
its own RPC address, so an old subscriber cannot answer its replacement's calls. Existing
matches can run during a backplane outage; on recovery, a seat whose claim moved or expired
is removed at the next lobby step. This does not make disconnected game simulations atomic:
a partition may temporarily leave an obsolete connected player until recovery reconciles it.

A **fleet** is every node sharing one `IRealtimeBackplane`. The default `LocalBackplane` is in-process, so a
single node is a fleet of one and runs the same code. Installing the `Realtime.Redis` plugin
([README](../plugins/Burbuja.Plugins.Realtime.Redis/README.md)) makes nodes on different machines one fleet
over Redis or Valkey (on AWS, ElastiCache for Valkey). Each lobby still lives on exactly one node, with its
loop and connections. The API on any node serves the whole fleet.

**Why Redis.** Fleet state is small, short-lived and contended: heartbeats with lifetimes, one seat per
player, a shared queue, per-game leases, and request/reply between nodes. Redis gives each of these one
atomic primitive (keys with lifetimes, Lua compare-and-set, sorted sets, pub/sub) at sub-millisecond latency,
and it is one piece of infrastructure with no node-to-node networking. Colyseus scales rooms the same way:
presence and room calls over Redis, players connecting to the process hosting the room. Open Match keeps
its matchmaking state in Redis. A relational store would need polling for notifications and leases. The
backplane is a contract, so another store is a plugin, not a change to the fleet.

| Shared on the backplane | Owner and lifetime |
|---|---|
| Node record: servers with load and capacity, games it registers | The node, refreshed every `Fleet:HeartbeatMilliseconds` (1 s), gone after `Fleet:NodeTtlSeconds` (5) |
| Lobby record: location, state, seats, listing; join code | The hosting node, published at creation and every heartbeat, removed at close |
| Seat claim: the one lobby holding each player | The hosting node; refreshed while the seat is held, released when it is given up |
| Matchmaking tickets and each game's queue | Any node adds or cancels; the game's matchmaker settles |
| Matchmaker lease per game | The node running that game's matchmaking |

**Routing.** `RealtimeFleet` runs an operation on a local lobby or server directly. Anything else becomes a
call to the owning node over the backplane, which runs the same local code: place a lobby with its seats,
reserve, leave, close, read a lobby exactly, drain or activate a server. The WebSocket door alone is local, so
a reservation's `connectUrl` is the hosting server's `PublicUrl`. In a distributed fleet every server needs
one, and boot fails without it.

**Placement.** The balancer filters every live server whose node registers the game (same eligibility,
regions and strategy as above), then asks the owner, which alone claims its capacity. A server that filled
since its last heartbeat answers `no_capacity` and the next is tried
(`A_server_that_filled_since_its_heartbeat_refuses_and_placement_moves_on`). A node publishes its record
again whenever it places a lobby, so a burst of placements sees servers fill. A node that does not answer is
told to close whatever it may have created, using the lobby id the caller chose beforehand. A close that
overtakes the slow create is remembered, so the lobby closes itself once it exists. The placement fails
with `node_unavailable` for this cycle.

**One seat per player, fleet-wide.** A reservation claims the player's seat on the backplane, reserves
locally, then confirms the claim still names its lobby. So two nodes seating the same player at once leave
exactly one seat (`Racing_reservations_on_two_nodes_leave_each_player_exactly_one_seat`). A claim naming a
lobby whose record is gone (closed, or its node died) or that no longer seats the player is taken over. A
heartbeat refreshes a claim and never revives one released meanwhile. If the claim's node does not answer
the "do you seat this player?" call, the seat is neither taken over nor reported as held. The reservation
answers `503 seat_unconfirmed`, and a matchmaking ticket stays queued
(`A_node_that_does_not_answer_neither_loses_nor_turns_away_its_players`).

**Matchmaking.** Tickets live on the backplane, so a player may queue through any node and meets players who
queued through others (`Players_who_queue_through_different_nodes_meet_in_one_lobby`). Every node runs the
cycle. Only the holder of a game's lease matches that game, and a node competes only for the games it
registers. When the holder stops renewing, another node takes over within the lease
(`When_the_matchmaker_stops_renewing_another_node_takes_over`). The lease outlasts two calls that time out
(`2 × Fleet:CallTimeoutMilliseconds` plus the interval, at least 2 s). The holder renews it before every
decision and stops matching once it is lost, so a slow cycle never runs alongside its successor. A ticket changes only while queued, so a
cancel on one node and a match on another have one winner. A match that loses to a cancel gives the seat
back (`A_cancel_that_beats_the_match_gets_the_seat_back`). A long-poll on any node answers as soon as the
ticket settles anywhere, and re-reads every second in case a notification is lost.

**Failure.** A node silent for `NodeTtlSeconds` drops out. Nothing more is placed there, and its players
may seat elsewhere at once (`A_node_that_stops_heartbeating_drops_out_and_its_players_may_seat_elsewhere`).
A node that stops cleanly removes its records immediately. If the backplane is unreachable, lobbies keep
running and fleet calls answer `503 fleet_unavailable`. Boot refuses a server id another node hosts, and a
node id another live process heartbeats. A node restarted after a crash waits out its old record and joins.

**Running a fleet.** On every node, set the same backplane settings and guest `SigningKey`, give the node
its own `Kernel:NodeId`, and give its servers unique ids and a `PublicUrl` players can reach:

```json
{
  "Kernel": { "NodeId": "eu-1" },
  "Realtime": {
    "Enabled": true,
    "Servers": [ { "Id": "eu-1-a", "Region": "eu", "MaxLobbies": 50, "PublicUrl": "wss://eu-1.example.com" } ],
    "Guests": { "Enabled": true, "SigningKey": "<the same 32+ characters on every node>" },
    "Redis": { "Configuration": "valkey.internal:6379,ssl=true,abortConnect=false" }
  }
}
```

Put the HTTP API behind any load balancer. Players' WebSockets go to `PublicUrl`, so each node needs an
address of its own. The Blast README runs a two-node fleet on one machine.

## Players and credentials

`Realtime:EnableWebSocketCompression` optionally enables negotiated `permessage-deflate`
for realtime sockets. It defaults to false and requires client opt-in; older clients
continue uncompressed. Use it only for game frames that do not carry credentials or
other secrets, since TLS does not remove compression side channels. Server compression
history never carries across messages. Inbound size limits apply after decompression.
The real-Kestrel negotiation tests cover all four client/server combinations, ordinary
ping/pong traffic, and refusal of oversized input, including compressed input.


HTTP calls authenticate a player through the ordered `IPlayerAuthenticator` chain; the first identity wins:

1. a kernel API key (`x-api-key` or `Authorization: Bearer`, the same `Auth:ApiKeys` ARM uses), for
   backends and bots;
2. a **guest token** (`x-player-token`) from `POST /realtime/v1/guests`, when `Realtime:Guests:Enabled`.
   It is an HMAC-signed id, name and expiry. Without `Guests:SigningKey` the key is random per process
   and guests sign in again after a restart.

A product adds its own account system by registering another authenticator.

**The WebSocket carries no player credential.** `GET /realtime/v1/connect?token=…` takes the seat
reservation the API issued: single-use, short-lived, naming one lobby, seat and player. Browsers cannot set
WebSocket headers, and a token that opens one seat once is safe in a URL. Without cookies, a cross-site page
cannot carry a player's session into a lobby. A refused token is closed with **4001** and a reason,
which a browser can read where it could not read an HTTP status.

## Wire protocol

One message per WebSocket message. A text frame is UTF-8 JSON: inbound `{"t":type,"d":data,"s":seq?}`,
outbound `{"t":type,"k":tick,"d":payload}`. A binary frame is a game message in the `RealtimeBinary`
envelope ([above](#game-datatypes-and-encodings)). Types starting with `sys.` belong to the kernel, are
always JSON, and a session cannot send them.

| Type | Direction | Meaning |
|---|---|---|
| `sys.welcome` | → client | First frame: your `playerId`, `slot`, `tickRate`, `tickIntervalMs`, `tick`, `serverTime`, the lobby |
| `sys.lobby` | → clients | State, countdown end, roster with connection status |
| `sys.ended` | → clients | `reason`, `winners`, game `details` |
| `sys.ping` / `sys.pong` | ↔ | RTT and clock offset: `d` is echoed with `serverTime` |
| `sys.leave` | client → | Leave now, no reconnect grace (closes 4005) |
| `sys.error` | → client | `rate_limited` (at most once a second), `bad_frame`, `unknown_system_type` |

Close codes: 1000 normal, 1001 server shutdown, 1009 frame over `MaxMessageBytes`,
1011 lobby faulted, 4001 invalid/expired reservation, 4002 kicked, 4003 lobby closed, 4004 replaced by a
newer connection for the same seat, 4005 left. A close reason, including a game's kick reason, is cut to
the 123 bytes a close frame allows, never inside a character.

**Inbound limits.** A token bucket admits a burst of one second's `MaxInboundMessagesPerSecond`, refilled
continuously. Excess is dropped, not queued, because a late input that still gets applied is worse than a
lost one. The per-connection inbox is bounded to the same allowance, so a flood costs a counter.

### Why raw WebSockets, not SignalR

SignalR is built for fan-out messaging. A tick server needs control of every frame. Hub sends wait on
each connection's buffer, which would let one slow player slow the lobby's loop; this transport drops
the oldest frame instead. SignalR's backplane is unused when players connect straight to the server
holding their lobby. Its fallbacks (server-sent events, long-polling) cannot carry action play. Its
clients are official only for JS, .NET and Java, while every game engine and browser speaks WebSocket.
The runtime still knows only `IPlayerConnection`/`IRealtimeGateway`, so another transport (SignalR for
casual browser games, WebTransport or UDP for lower latency) is an adapter, not a rewrite.

## HTTP API (`/realtime/v1`)

| Call | Who | Result |
|---|---|---|
| `GET /games` | anyone | Registered games and their rules, including the inbound limits |
| `POST /guests` `{displayName}` | anyone (guests enabled) | 201 `{playerId, displayName, token, expiresAt}` |
| `GET /me` | player | Identity and the lobby still holding your seat (rejoin after refresh) |
| `POST /queue` `{game, region?}` | player | 202 ticket; 409 `already_queued` (with the ticket) or `already_in_lobby` |
| `GET /queue/{id}?waitSeconds=0..30` | ticket owner | Long-poll; `state` `queued`/`matched`/`cancelled`/`expired`, `reservation` when matched |
| `DELETE /queue/{id}` | ticket owner | 204 |
| `GET /lobbies?game=` | anyone | Public lobbies not yet ended (no join codes) |
| `POST /lobbies` `{game, visibility?, region?, settings?}` | player | 201 `{lobby (with joinCode), reservation}` for a hosted lobby; private by default |
| `POST /lobbies/join` `{code}` · `POST /lobbies/{id}/join` | player | A reservation; also the reconnect path |
| `GET /lobbies/{id}` · `POST /lobbies/{id}/leave` | player | Snapshot (code only for members) · 204 |
| `GET /servers` · `POST /servers/{id}/drain|activate` · `POST /lobbies/{id}/close` | API key with a role in `Realtime:OperatorRoles` (default `Owner`) | Every live server in the fleet with its `nodeId`, `games`, utilization, measured `load` and hosted lobbies (`tick`, `performance`; `null` when its node does not answer) · state change on whichever node owns the server · 202 |
| `GET /connect?token=` | reservation holder | WebSocket |

Errors are `{"error":{"code","message"}}` with stable codes: `unknown_game` 404, `no_capacity` 503,
`lobby_full`/`match_in_progress`/`already_in_lobby` 409, `lobby_closed` 410, `unauthenticated` 401,
`operator_required` 403, `node_unavailable`, `seat_unconfirmed` and `fleet_unavailable` 503.

## Verification

```sh
dotnet test tests/Burbuja.Realtime.Tests/Burbuja.Realtime.Tests.csproj -c Release
```

103 tests, all run by `make test`. They cover the lifecycle under a fake clock (exact tick counts, the
countdown reverting, reservation expiry, reconnect grace and slot retention, single-use tokens, newer
connections replacing older ones, rate limiting, system messages, fault isolation, seat bounds,
join-in-progress, one seat per player, kicks, empty-lobby reaping, join codes). They also cover
placement (spread, pack, drain, region and fallback, concurrent capacity), matchmaking strategy and
queue (skill window widening, region grouping, fullest-first backfill, capacity waits, expiry,
long-poll) and guest tokens. `GameDataAndNetcodeTests` cover these with a small 3D session:
- typed JSON input and state with numerics vectors;
- input acknowledgement: it only moves forward and resets on reconnect;
- binary frames in both directions, with `sys.*` and truncated envelopes refused;
- per-game serializer options;
- dropped-tick and overrun accounting, published load (zero once closed) and lock-free status;
- close-reason bounds.

`BackplaneContractTests` state the backplane contract once and run it against `LocalBackplane` and, through
`RedisBackplaneTests`, a real Valkey. The tests use `BURBUJA_REDIS` when set, or start a throwaway
container with the local Docker, and skip with a reason when neither is available. `FleetTests` run several
nodes in one process under a fake clock: placement across nodes and refusal of a server that filled,
fleet-wide single seats under racing reservations, one matchmaking pool across nodes, matchmaker failover,
cancel-versus-match, a dead node's players seating elsewhere, operator routing, boot refusals and crash
restarts. `FleetHostTests` boots two real Blast hosts that install the game and the Redis backplane as
dynamic plugins. Four guests queue through different nodes and play one match on the node the fleet chose.
The other node answers for the lobby, the seats and the operator view, and a node leaving removes its
servers at once.

`BlastSimTests` drive the Blast session through internal test seams with exact assertions on its mechanics
(blast sphere and pillar shielding, chains, kicks, pumped throws onto heads, hearts, the edge, rounds and
the match end, sudden death, ghosts, bots and determinism). `BlastHostTests` play it over WebSockets.

`ArenaHostTests` boots the real Arena host with the game installed as a
dynamic plugin in its own load context. Two guests queue, get one placed lobby and play over real
WebSocket frames: the server moves a player from JSON input and then from binary input, acknowledging
each input's sequence. `sys.ping` round-trips, and the operator sees the fleet with its load and tick
accounting. That test also covers join by code, the refusals, forged and replayed tokens,
and a disabled host.

## Limits and next steps (not implemented)

- **A match lives and dies with its node.** Lobbies are not migrated. If a node dies, its matches end,
  its players are freed within `NodeTtlSeconds`, and they queue again. Drain a node
  (`POST /servers/{id}/drain`) before stopping it, so its matches finish first.
- **No autoscaler.** The fleet publishes what one needs: per-server allocation, measured `load` and
  draining, plus `packed` placement so idle nodes empty out. Starting and stopping nodes is the platform's
  job (ECS, Kubernetes, Agones-style fleets).
- **The backplane is the queue's only copy.** Use a Redis or Valkey with persistence or replicas if queued
  tickets must survive its restart. Otherwise players simply queue again. All fleet keys share one slot, so
  one shard serves the fleet.
- **No persistence or events.** Results are not written to the kernel event log or a store, and there
  are no metrics exporter yet beyond logs and `/servers` (with per-lobby `performance`). A consumer that
  needs match history records it from `OnMatchEnded`.
- **No delta compression or interest management in the kernel.** A game can already send whole
  snapshots in its own binary encoding and per-player state with `Send`/`SendBinary`. A large world
  culls per player in its session. Kernel-level area-of-interest helpers are not provided.
- **No parties, rating updates or ready checks.** A party would be a ticket with several players. Rating
  belongs to the product's authenticator and `OnMatchEnded`.
- **Prediction is the client's.** The server half is provided (fixed step, tick on every frame,
  `LastSequence` acknowledgement). The Arena client numbers its inputs but only interpolates. A
  predicting client replays its unacknowledged inputs on each snapshot.
- **No UDP or WebTransport transport yet.** WebSocket is TCP, so a lost packet delays the frames after
  it. Dropping the oldest frames bounds that, but a UDP-class transport behind `IRealtimeGateway` is the
  next step for twitch shooters on lossy networks.

## Optional capacity pools and session resources

`Realtime:Servers:<index>:AllowedGames` optionally restricts a server's capacity
pool to registered game IDs. An omitted/null list preserves the node-wide default;
explicit empty, duplicate or unknown lists fail validation. Fleet status advertises
and filters each pool's effective support, and the allocation owner enforces it
again even for pinned-server requests. Pool counts remain atomic lobby-lifetime
claims; adding pools does not grant additional physical CPU or memory.

`Realtime:Resources` configures node-wide named integer limits, for example an
`execution` limit of two. During callbacks, a session may call
`ISessionContext.TryReserveResource(name, units)` to set its claim. Repeating a
claim is idempotent, zero releases it, competing rooms cannot exceed the node's
limit, and closing/faulting the lobby releases all claims. Unknown resources and
invalid or unavailable amounts refuse. Older custom contexts default to refusal,
not unlimited capacity. A waiting room can therefore release its execution claim
without closing its room or losing its code.

Optional `ISessionResourceAdmission` implementations can defer new/increased
claims using cached advice. They must not do I/O, await or block: the decision
runs in the session callback. Existing claims and releases remain valid during
pressure or operator drain; new claims on draining servers are refused. This is
admission, not an eviction or live-migration mechanism. `availableResources` is
node-wide information repeated on server status: do not sum it across pools.
The owner still rechecks at reservation time because advertisements may be stale.

`IGameSession.AcceptsNewPlayers` is cached by the sequential lobby owner after
callbacks. It controls new reservations and advertised open seats; a current seat
can still reconnect. A game must also handle a previously issued reservation
connecting after its roster becomes fixed. `UsesResourceReservations` lets
cooperative monitors distinguish a quiet persistent room from active execution.
Operator room snapshots expose `resourceClaims`; the local fleet response includes
its `resourceLimits`. These counters do not replace runtime latency/load testing.

## Optional control-preserving delivery

An `IRealtimeGame` can opt into `ReliableControlFrames` and declare complete
snapshot types in `ReplaceableMessageTypes`. The connection then retains control
frames, coalesces queued snapshots of the same type, and appends a replacement
after intervening stage/control barriers. System messages are always control.
This policy bounds both frame count and queued bytes (one MiB per connection).
When reliable traffic cannot fit, the connection explicitly closes with
`client_too_slow` rather than silently losing a control message. Disposable
snapshots can be dropped. The opt-out path retains the prior drop-oldest policy.

Transports can implement the `IPlayerConnection.TrySend` overload accepting
`FrameDelivery`; older adapters keep their default implementation. The built-in
WebSocket adapter supplies the new queue behavior. This prevents application
queue loss of accepted controls; it does not remove TCP head-of-line blocking,
replace input/action acknowledgements, or imply client prediction.

`CapacityPoolTests` cover owner enforcement, pool routing, concurrent execution
claims, pressure/drain behavior, cleanup after faults and dynamic admission.
`OutboundDeliveryTests` cover stage retention, barrier ordering, explicit reliable
overflow and bounded snapshot discard. Run the full realtime/Redis and independent
consumer gates before promoting a new dependency pin.
