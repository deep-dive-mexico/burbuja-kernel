# Burbuja kernel release notes

Dates are UTC. Consumer upgrades pin an immutable release tag and full commit.
See [release policy](docs/releases.md), [notes workflow](docs/release-notes.md)
and [standalone deployment evidence](docs/aws-deployment.md).

## Unreleased

### Added

- Optional realtime capacity-pool game allowlists, node-owned session resource
  reservations, cached resource-admission policies and dynamic new-player
  admission. Waiting rooms can release execution capacity between matches;
  operator snapshots distinguish room counts from active resource claims.
- Opt-in reliable control delivery with replaceable full snapshots. Queue
  pressure preserves stage/control ordering or closes the slow connection;
  opted-in outbound queues also have a one-MiB byte bound. Existing games keep
  their previous delivery policy unless they opt in.

- Optional private native file worker with fresh-headroom admission, fixed
  executable/argument allowlists, bounded input/output, cancellation and status;
  host monitoring and cooperative background budgets ([guide](docs/shared-workloads.md)).

- Dedicated real-time lobbies for multiplayer games (`Burbuja.Realtime`, contracts in
  `Burbuja.Realtime.Abstractions`): a matchmaking queue with a widening skill window and
  backfill, lobby-server placement (`least-loaded` or `packed`, region-aware, drainable),
  the lobby lifecycle with a fixed-tick single-threaded session loop, seat reservations,
  reconnect grace, inbound limits, guest players and a WebSocket transport at `/realtime/v1`.
  Off unless `Realtime:Enabled`. The `examples/Example.Arena` shooter is the proof of concept
  ([guide](docs/realtime-lobbies.md)).
- Games bring their own datatypes and encodings:
  - typed reads of client input (`InboundMessage.TryRead<T>`);
  - per-game serializer options (`IRealtimeGame.SerializerOptions`, from `RealtimeJson.CreateOptions()`);
  - `System.Numerics` vectors and quaternions as `{x,y,z}`;
  - binary frames in both directions (`SendBinary`/`BroadcastBinary`, the `RealtimeBinary` envelope).

  For netcode, the kernel adds per-player input acknowledgement (`ISessionContext.LastSequence`) and
  tick timing in `sys.welcome`. Each lobby records tick performance (busy fraction, overruns, dropped
  ticks), and servers report a measured `Load` that placement uses to break ties. `GET /servers` shows
  both.
- Lobby nodes on many machines form one fleet ([guide](docs/realtime-lobbies.md#fleet-many-nodes)). Nodes
  share an `IRealtimeBackplane`: the in-process default, or Redis/Valkey through the new dynamic plugin
  `Burbuja.Plugins.Realtime.Redis`. The API on any node then serves the whole fleet:
  - placement ranks every live server, and the owning node claims its own capacity;
  - each player holds one seat fleet-wide, even when two nodes seat them at once;
  - one matchmaking pool spans the nodes, with one matchmaker per game holding a lease and failing over;
  - join codes resolve on every node, and operators see and drive every server.

  A node that stops heartbeating drops out and frees its players. A distributed fleet requires each
  server's `PublicUrl`. Single-node hosts behave as before.
- A session with CPU players may start below `GameRules.MinPlayers` (`IGameSession.MinPlayersToStart`).
- `examples/Example.Blast`: a 3D battle game for 2–8 players after Bomberman 64's Battle mode. It has
  kick, throw, pump, spherical chain blasts, items, ghosts, sudden death, CPU players and a three.js
  client, and it is the fleet's two-node end-to-end proof.

### Changed

- Fleet node publication claims all server ids atomically. RPC targets the current process
  instance, an obsolete instance is fenced from new fleet mutations, and recovering nodes
  evict seats whose backplane claim moved or expired. Blast replaces a bot when a human takes
  its slot, counts the victim's controls for ghost shake-off, and removes ghost interactions
  during sudden death. Local/Valkey contracts and simulation regressions cover these cases.
- The Blast camera fits the projected arena corners and elevated pillars on portrait screens;
  a centre-line width estimate could still clip the near corners on a phone.

- `LobbyManager` is node-local. One seat per player, lobby lookup by id or code, backfill candidates and
  the matchmaking queue go through `RealtimeFleet`, and the queue's methods are asynchronous
  (`EnqueueAsync`, `GetAsync`, `CancelAsync`, `RunCycleAsync`).

### Delivery

- Move validation, release, consumer compatibility and deployment jobs to disposable
  GitHub-hosted Ubuntu runners. Public pull requests receive no private-consumer
  checkout or deployment authority. Keep private test output on its temporary VM.
- Replace deployment-runner instance credentials with separate environment-bound
  AWS OIDC roles. Manual validation checks authentication by default and installs
  only when its `deploy` input is enabled.

- Run the AKOS product's complete offline suite against every validated kernel
  commit on both product `dev` and `main` (`consumer-akos` job) before a release
  is published or a tenant deployed, so generic regressions that would affect the product fail in the
  kernel's own CI ([consumer gate](https://github.com/deep-dive-mexico/burbuja-kernel/blob/main/docs/releases.md#consumer-gate)).

- Require fresh, matching consumer receipts and complete passing test counts; retain
  an aggregate result when an executed browser failure passes its single retry.
  Preserve source identities and test counts in CI logs when diagnostic upload
  storage is unavailable; keep uploads to receipts and diagnostics for seven days.

## v0.6.1 — 2026-10-06

### Fixed

- Refresh installed runtime OS packages during image builds so inherited
  OpenSSL vulnerabilities receive available security updates.
- Preserve an explicitly empty maintenance-release summary without including
  unrelated future changes from Unreleased.

### Delivery

- Include a curated release summary from the validated source's changelog in
  new GitHub releases, alongside source identity and GitHub's generated PR list.
  Publication reruns preserve existing release pages and immutable tags.
- Establish a release-note workflow that records consumer compatibility,
  validation and deployment separately.

Release: [v0.6.1](https://github.com/deep-dive-mexico/burbuja-kernel/releases/tag/v0.6.1).

## v0.6.0 — 2026-10-06

### Added

- Call declared remote MCP tools over stdio or Streamable HTTP, with resource
  authorization, policy/approval binding, secret references, bounded output,
  deadlines and cancellation.
- Resume persisted single-worker and plan-fan-out jobs with leases, stable run
  identities, model checkpoints, pending approvals and durable effect receipts.
  An interrupted effect without a recorded result blocks for review rather
  than running automatically again; unknown model usage remains partial cost.
- Run independent HTTP, owned-process recovery and Chromium journeys in CI,
  alongside generic units and the Workbench consumer.

### Compatibility

- Resource-store adapters must implement atomic `IResourceStore.CreateAsync`.
  The 0.6 minor is a consumer contract change; review the store adapter and
  run the consuming product's compatibility gates before adopting it.
- MCP calls are bounded outbound operations. Persistent MCP sessions, automatic
  agent-tool catalogue import and arbitrary server-to-client requests are outside
  this contract. Custom orchestration strategies own their continuation.

### Delivery

- Use dedicated release runners and wait for durable provisioning/result/run
  settlement in restart tests before asserting completion.

Release: [v0.6.0](https://github.com/deep-dive-mexico/burbuja-kernel/releases/tag/v0.6.0).

## v0.5.1 — 2026-10-06

### Delivery

- Deploy `dev` to a separate hosted tenant, with independent credentials, state,
  backups and environment branch policy; keep stable production releases on `main`.
- Move validation/release jobs to the CI host without application deployment
  authority; keep deployment on the separate deployment runner.

Release: [v0.5.1](https://github.com/deep-dive-mexico/burbuja-kernel/releases/tag/v0.5.1).

## v0.5.0 — 2026-10-06

### Delivery

- Publish validated stable releases automatically from version-only children
  of `main`, preserve tags and rerun identity, and deploy the validated digest.
- Include the standalone AWS Host/Portal, clean source/export boundaries,
  release-line version checks and independent Workbench validation.

Release: [v0.5.0](https://github.com/deep-dive-mexico/burbuja-kernel/releases/tag/v0.5.0).

## v0.4.0 — 2026-10-06

### Existing consumer baseline

- Publish the existing reusable Core/SDK, agents, compute, deployments,
  integrations, hosting, generic Host/Portal, plugins, TestKit and Workbench
  source as the first GitHub release.
- Preserve the exact consumer baseline already in use: the tag was created
  retroactively on October 5, without changing source or dependency pins.

Release: [v0.4.0](https://github.com/deep-dive-mexico/burbuja-kernel/releases/tag/v0.4.0).
