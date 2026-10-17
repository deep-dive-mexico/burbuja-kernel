# Real-time lobbies

[Kernel wiki](wiki/OVERVIEW.md) · [Runtime capabilities](runtime-capabilities.md) · [Arena PoC](../examples/Example.Arena/README.md)

The realtime module hosts **dedicated, server-authoritative lobbies** for multiplayer games and other
room-shaped, low-latency uses (live quizzes, shared boards). It owns the parts every such server
repeats: matchmaking, placement across lobby servers, the lobby lifecycle, the fixed-tick loop, seat
reservations, reconnects, inbound limits and the transport. A game supplies only its simulation.

| Project | Owns |
|---|---|
| `src/Burbuja.Realtime.Abstractions` | Contracts with no dependencies: `IRealtimeGame`, `GameRules`, `IGameSession`/`GameSession`, `ISessionContext`, players and `IPlayerAuthenticator`, `IPlayerConnection`/`IConnectionInbox`/`IRealtimeGateway`, `ILobbyPlacementStrategy`, `IMatchmakingStrategy`, the wire protocol, `RealtimeJson` (game JSON options with numerics vectors) and `RealtimeBinary` (the binary envelope) |
| `src/Burbuja.Realtime` | `Lobby` (state machine and loop), `LobbyManager` (lifecycle, codes, reservations, admission), `LobbyServer`/`LobbyBalancer` (fleet and placement), `MatchmakingQueue`/`SkillWindowMatchmaker`, guest and API-key players, the WebSocket transport and `/realtime/v1` |
| `examples/Example.Arena` | The PoC: a 2–8 player arena shooter installed as a dynamic plugin, and a host with a browser client |

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
are pure: the queue rejects decisions that reuse or invent tickets. When no server has room, tickets stay
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
`Realtime:Placement`.

## Players and credentials

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
| `GET /servers` · `POST /servers/{id}/drain|activate` · `POST /lobbies/{id}/close` | API key with a role in `Realtime:OperatorRoles` (default `Owner`) | Fleet utilization, measured `load`, hosted lobbies with `tick` and `performance` · state change · 202 |
| `GET /connect?token=` | reservation holder | WebSocket |

Errors are `{"error":{"code","message"}}` with stable codes: `unknown_game` 404, `no_capacity` 503,
`lobby_full`/`match_in_progress`/`already_in_lobby` 409, `lobby_closed` 410, `unauthenticated` 401,
`operator_required` 403.

## Verification

```sh
dotnet test tests/Burbuja.Realtime.Tests/Burbuja.Realtime.Tests.csproj -c Release
```

41 tests, all run by `make test`. They cover the lifecycle under a fake clock (exact tick counts, the
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

`ArenaHostTests` boots the real Arena host with the game installed as a
dynamic plugin in its own load context. Two guests queue, get one placed lobby and play over real
WebSocket frames: the server moves a player from JSON input and then from binary input, acknowledging
each input's sequence. `sys.ping` round-trips, and the operator sees the fleet with its load and tick
accounting. That test also covers join by code, the refusals, forged and replayed tokens,
and a disabled host.

## Limits and next steps (not implemented)

- **Single node.** Lobby servers are in-process capacity units on the node that serves the API.
  `PublicUrl` and the placement seam are ready for a fleet, but there is no cross-node server registry,
  heartbeat or shared ticket queue. Tickets and lobbies are in memory and do not survive a restart.
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
