# Connected application storage — development candidate

This is the first storage slice of the shared-service architecture. An independent
application consumes contracts and `Burbuja.Core.Sdk` over HTTP. The kernel owns
provider selection, connections, migration and persistent files. The existing
embedded mode remains supported and the new endpoints are disabled by default.

This candidate does **not** move an application's product handlers or generic
execution into a connected lifecycle. Authenticated product-step dispatch,
application-scoped runtime/model/budget configuration, remote approval actions,
application host adapters and live state migration remain separate gates. Do not
change an installed consumer lock or present this as a migrated AKOS/Pomnibot host.

## Identity and ownership

`ConnectedKernel:Enabled=true` requires an explicit `Environment`, `StorageRoot`
and application registrations. Each registration names `Tenant`, `Application`,
private `Credential` (at least 32 characters), `Provider`, `Writable`, product
`WriteNamespaces` and product `EventPrefixes`. Credentials are generated private
runtime configuration. They are unrelated to the ARM portal's Owner key.

One deployment serves one environment. Development and production have separate
kernel processes, credentials, binding directories and backups. A credential
resolves a fixed `(environment, tenant, application)` scope. Resource IDs and
caller-supplied paths cannot select another scope. The SDK verifies its expected
scope at handshake and on every request, including after a kernel restart.

The registry rejects duplicate scopes/credentials, unknown adapters and reserved
kernel namespaces/event prefixes. SQLite and the test in-memory adapter are the
supported providers. Each binding gets one coherent set of resources, events,
artifacts, leases/fences, outbox, idempotency, approvals/grants, operations and
decision-cache stores. SQLite files and artifact roots stay inside a server-derived
scope directory; clients receive no connection string or filesystem path. An
exclusive writer handle prevents a second kernel opening the same binding.

Protocol control receipts/audits have their own kernel-owned binding. Each RPC
records its authenticated scope, request identity and delegated actor, without
raw arguments. The delegated actor is audit identity, never a role grant.

Persistence authority permits product resource writes only in registered
namespaces. It cannot write kernel runtime or authorization resources, forge
approval outcomes/grants, replace kernel operation receipts, or emit reserved
runtime events/topics. Application lease/idempotency/cache keys have a private
purpose prefix so they do not shadow kernel mechanisms. Approval mutations need
the future separately authorized runtime capability, not an `IApprovalStore`
write from an application.

## Network interface

`GET /kernel/v1/handshake` negotiates `burbuja-applications/1`, service build
identity, registered scope and required capabilities. Authentication uses a
Bearer application credential. `X-Burbuja-Application` carries an expectation,
not a scope selector; a mismatch fails authentication.

`ConnectedKernelClient` implements the familiar typed persistence contracts.
`POST /kernel/v1/storage` carries their versioned operations with bounded JSON;
there is no SQL endpoint. Conditional writes and atomic creates retain **412**
conflicts. Resource paging retains adapter continuation tokens. Bounded event
pages implement the contract's larger/unlimited ranges through client paging.
`GET /kernel/v1/events` streams scope-local events with SSE and cancellation;
stream sequences and hashes remain intact.

Every mutating JSON RPC has a stable request identity. A durable started receipt
precedes the effect; completion stores the result. Replay returns the same result,
including a conditional-write rejection. A started receipt after interruption
returns `OutcomeUnknown` and refuses automatic execution. Reusing an identity for
different arguments is a conflict. `GET /kernel/v1/receipts/{id}` provides only the
authenticated application's reconciliation receipt. Network errors/timeouts and
canceled requests retain that identity in the SDK exception.

Artifacts stream through `GET/PUT /kernel/v1/artifacts`. Uploads are bounded to
128 MiB and staged before provider writes, including non-seekable/chunked streams.
Metadata/hash travel separately from the bytes. Artifact writes currently require
destination readback to reconcile a lost response; the JSON RPC receipt mechanism
does not claim atomic effect settlement across filesystem/provider operations.

The client performs no local fallback and no automatic write retries. HTTPS is
required except explicitly selected HTTP development connections. Required
capabilities and scope mismatches fail startup. Human-facing authorization and
product policy still need enforcement in each application's connected entry.

## Verification and remaining work

`make test` includes the socket tests in `ConnectedApplicationTests`: identical
resource/event/artifact/lease IDs stay independent, CAS/atomic-create races have
one winner, JSON write replay survives restart, event tails cancel, credentials
cannot select another application/environment, auxiliary state stays isolated,
reserved kernel writes fail and a second writer is refused.

`python3 scripts/prove-connected-apps.py` publishes and starts one kernel plus
two independent `Example.Connected` processes on free loopback ports. The
applications publish only kernel contracts/SDK dependencies, retain conflicting
note IDs separately, restart independently, report explicit kernel unavailability
without a fallback, and read their original data after a kernel restart. It writes
an ignored `artifacts/connected-proof/<run>/receipt.json`; no model call occurs.

This proves the storage slice. It does not prove generic execution with a durable
application scope, authenticated product dispatch, per-application model/budget
enforcement, a real application's connected composition, import/restore or live
cutover. Those must be implemented and separately witnessed before promotion.


## Candidate receipt — 2026-10-06

On development base `29301150e486307327245f123c38ad2784e9c884` with this
candidate uncommitted, `make test` passed 1,333 cases and the final connected
suite passed 9/9 (one additional fault-window case). `make test-e2e` passed
47 HTTP, seven owned-process and 20 Chromium cases. The real-process proof at
`artifacts/connected-proof/a2b6fe94c42b4429a7dcd193c658eff3/receipt.json`
reports 0.7.0-dev, SDK-only client closures, independent restarts, scoped
readback and explicit 503 unavailability. No live service was restarted,
consumer lock advanced or application database migrated.
