# Connected application storage — development candidate

This is the first storage slice of the shared-service architecture. An independent
application consumes contracts and `Burbuja.Core.Sdk` over HTTP. The kernel owns
provider selection, connections, migration and persistent files. The existing
embedded mode remains supported and the new endpoints are disabled by default.

This candidate does **not** migrate a product host. It adds an opt-in generic
ARM runtime entry scoped to each binding, but authenticated product-step dispatch,
production model/budget wiring, witnessed remote approval actions,
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

## Opt-in scoped ARM runtime — development candidate

A writable registration may explicitly enable `RuntimeEnabled` and list
`DelegatedRoles` (default: Reader). `ConnectedKernelClient.Arm` accepts a principal
resolver invoked for **each** request. Real applications must derive that identity
from their authenticated entry; the independent example uses a synthetic Reader.
The application credential bounds both the storage scope and the allowed roles.
A principal naming another tenant or a role outside the registration is denied.
Assertions are request-local; an earlier Owner assertion does not grant a later
Reader request write permission. Read-only registrations cannot start runtime
workers through this capability.

`POST /kernel/v1/runtime/arm` carries a bounded typed ARM request and delegated
principal. The kernel runs its generic routing/authentication/authorization
pipeline against explicitly captured binding stores. Generic hosted workers are
kernel-owned, and local Compute directories are derived from the binding scope.
No application starts these workers or opens their database. This prototype uses
only the scripted provider; it does not advertise production provider parity.
`POST /kernel/v1/runtime/stream` uses the same authority and native ARM data-plane
authorization for resource SSE, preserving event names, IDs, resume and cancellation.

Runtime calls currently use existing ARM request semantics, **not** the storage
RPC's durable effect receipt protocol. Network loss never automatically retries
an ARM mutation. Runtime reconciliation, audit completeness, all generic provider
coverage, real model/budget configuration and end-to-end product dispatch remain
release gates. Do not use this candidate for a production cutover.

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
without a fallback, and read their original data after a kernel restart. They
also read generic ARM providers through the separate runtime before/after restart
and report 503 for runtime calls while the kernel is unavailable. It writes
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

## Runtime tracer receipt — 2026-10-07

On storage foundation `426856a4c552d5d326d72a84e4532592fcad91b9` with the
runtime candidate uncommitted, the connected socket suite passed 13 cases.
The broader kernel gate passed 1,338 unit cases plus 47 HTTP, seven owned-process
and 20 Chromium E2E cases (`artifacts/e2e-runs/1aca3961386b/receipt.json`).
New tracers cover generic provider discovery, isolated ARM subscription state,
Owner-to-Reader downgrade, rejected undelegated identities/read-only runtime,
and resource SSE isolation/resume/cancellation. The three-process proof at
`artifacts/connected-proof/f71ffddc92b6488eb2a9597218c807f4/receipt.json`
passed with SDK-only application closures and runtime unavailability/restart
checks. These witnesses do not substitute for AKOS product migration acceptance.
