# Burbuja kernel release notes

Dates are UTC. Consumer upgrades pin an immutable release tag and full commit.
See [release policy](docs/releases.md), [notes workflow](docs/release-notes.md)
and [standalone deployment evidence](docs/aws-deployment.md).

## Unreleased

### Added

- `Store.Connected` plugin: a kernel-image host can serve every durable store
  from a separate kernel service over the connected-application protocol, with
  no local database and explicit unavailability when the kernel is unreachable
  ([connected applications](docs/connected-applications.md#connected-store-plugin-for-kernel-image-hosts)).
- A connected host whose kernel service is unreachable answers ARM requests with
  503 `KernelUnavailable` and a retry hint instead of a 500.
- `EmbeddedRuntime` application registrations: a connected kernel-image host
  owns its whole isolated binding, including its runtime's kernel-namespace
  state, and cannot combine with the kernel's scoped runtime.
- Prototype opt-in application-scoped ARM runtime calls and authorized resource
  SSE through the kernel service. Request-local delegated roles stay within the
  application registration, and read-only registrations cannot start runtime
  workers. SDK-only process probes exercise runtime unavailability/restarts;
  production provider, product dispatch and consumer migration gates remain open.

- Begin the 0.7 development line with an opt-in connected-application storage
  interface: credential-bound environment/tenant/application scopes, kernel-owned
  SQLite and in-memory bindings, typed HTTP/SSE clients, conditional/atomic writes,
  isolated auxiliary stores and durable JSON write receipts. The independent
  connected example proves separate application processes without embedded runtime
  dependencies. This is the storage foundation; generic execution/dispatch,
  per-application provider/budget wiring and consumer migrations remain pending.

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