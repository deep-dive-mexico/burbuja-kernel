# Clusters: nodes, networks, workloads and the operating system

[Kernel README](../README.md) · [Knowledge map](wiki/OVERVIEW.md) · [Three-machine example](../examples/Example.Cluster/README.md)

The cluster module turns machines into **nodes** of one cluster and decides which node runs which
**workload**, based on what each workload says it needs. It is optional and off until
`Cluster:Enabled` is set. Code: contracts in `src/Burbuja.Cluster.Abstractions`, runtime in
`src/Burbuja.Cluster`, the installable host in `src/Burbuja.Node`, network plugins in
`plugins/Burbuja.Plugins.Cluster.*`.

It exists because the alternative is hand wiring: SSH tunnels, copied status files, per-project
priority scripts and a different guard for every host. Those keep working. This module is the
generic mechanism underneath them, and the realtime fleet, the file-workload worker and the
`tools/cluster` monitor are unchanged by it.

## The model in one page

| Concept | What it is | Contract |
|---|---|---|
| Node | A machine that installed the kernel and joined. It describes itself (platform, capabilities, devices) and **lends** capacity; the scheduler only ever sees what was lent | `NodeDescriptor`, `INodeFactsSource` |
| Network | One way nodes reach each other: loopback, LAN, Tailscale, Twingate, an address the operator states | `INetworkProvider` |
| Workload | One unit of work with declared requests, placement rules, a class, a lifecycle and an execution profile. Its payload belongs to its kind | `WorkloadSpec`, `IWorkloadDriver` |
| Execution profile | What the workload asks of the operating system, in one vocabulary for every platform: priority, limits, devices, suspension | `ExecutionProfile`, `IOsExecutionAdapter` |
| Placement | Kernel-owned eligibility, then ranking | `IPlacementFilter`, `IPlacementScorer` |

One process is the **controller** (`Cluster:Role=controller` or `all`); every member runs an
**agent** (`agent` or `all`). An agent only ever dials out to its controller, on an interval, and
is told the complete set of work it should be running. Nothing connects to an agent, so a machine
behind NAT that can dial out is a full member. The reverse case is **not** covered: a machine that
others can reach but that may not open connections itself cannot reach its controller. Until the
controller can dial such a node, the operator supplies the path (for example a forwarded port on
the node's loopback, listed as a controller address).

## Try it

```sh
make build
examples/Example.Cluster/three-machines/run.sh
```

That starts a controller and three differently shaped nodes as four processes, joins them with
one-time tokens, places six workloads and suspends a batch job while interactive work answers.
The [example README](../examples/Example.Cluster/README.md) walks through the output.

## Installing a node

Publish the host once per platform and copy it to the machine:

```sh
dotnet publish src/Burbuja.Node -c Release -r linux-x64 --self-contained true -o artifacts/node
```

**Controller** (one machine). `Listen` is what other machines connect to; bind it to an address on
a network you trust, not to every interface:

```json
{ "Cluster": {
    "Role": "all", "NodeId": "hub", "StateDirectory": "/var/lib/burbuja-cluster",
    "Listen": [ "http://127.0.0.1:18520", "http://<overlay address>:18520" ] } }
```

```sh
burbuja-node --config hub.json
burbuja-node ctl --state /var/lib/burbuja-cluster token     # prints a one-time join token
```

**Member.** Carry the token to the machine out of band, put it in a file only the node's account
can read, and list the controller's addresses in any order:

```json
{ "Cluster": {
    "Role": "agent", "NodeId": "tower", "StateDirectory": "/var/lib/burbuja-cluster",
    "Agent": {
      "Controllers": [ "http://hub.example-tailnet.ts.net:18520", "http://192.168.1.10:18520" ],
      "JoinTokenFile": "/var/lib/burbuja-cluster/join.token",
      "Lend": { "cpu": 12, "player-slot": 2 },
      "LendDevices": [ "1" ],
      "Programs": {
        "trainer": { "Executable": "/opt/app/bin/train", "Arguments": [ "--resume" ],
                     "ParameterFlags": { "shard": "--shard" }, "WorkingDirectory": "/srv/app" } } } } }
```

The token is used once. The node then keeps its own credential in `node.json` and the token file
can be deleted. The same module runs inside any kernel host (`Burbuja.Host`, a consumer's own
executable): set the same `Cluster:` section there and the host is a node as well. In such a host:

- `Cluster:NodeId` defaults to `Kernel:NodeId`, so a machine has one id.
- `Cluster:Scheme` and `Cluster:Port` must match the host's real listener. They are what the
  advertised addresses and the `BURBUJA_NODE_URL` given to workloads are built from; the module
  does not bind anything itself.
- In-process code and plugins use `IClusterControlPlane` (status, apply, delete, node
  availability, join tokens) from the contracts assembly instead of the HTTP API.
- `Cluster:Http=false` keeps the cluster off the host's listener entirely, for a host that uses it
  only in process (its own pressure verdict, `IClusterControlPlane`). A public-facing server
  should set it unless other nodes join through that listener.
- Use one flat, project-prefixed namespace for workload names (`<project>.<name>`) when several
  projects share a cluster; names are cluster-wide.

Operating the cluster (`burbuja-node ctl`, or the HTTP API under `/cluster/v1` with the operator
token from `operator.token`): `status`, `apply FILE`, `delete NAME`, `cordon|drain|activate NODE`,
`forget NODE`, `token`. `status` prints, for every pending workload, the reason each node was
passed over.

## What a node lends, and why the node has the last word

A node's operator decides what the cluster may do on that machine. None of this can be changed
from the controller.

- **Capacity.** `Lend` caps a measured resource (`cpu`, `memory-mb`, `gpu`) and declares custom
  ones (a measured number of rooms or sessions the machine carries). A name the node never
  declared is *not offered*, which is different from zero and is reported that way.
- **Devices.** `LendDevices` lists the accelerator ids the cluster may use. A GPU that also drives
  a desktop exists and is still not lent. Detection is a read-only `nvidia-smi` query; the module
  never changes a driver, a binding or a mode.
- **Programs.** The built-in `process` kind starts only programs listed in `Programs`. A workload
  names one and fills parameters that entry allows; each becomes a literal argument after its
  flag. There is no shell and no caller-supplied command line. Compromising the controller does
  not yield a way to run arbitrary commands on members.
- **Priority and kinds.** `MaxPriority` bounds what cluster work may ask for; `Kinds` restricts
  which workload kinds run here.

The controller filters on what nodes advertise, so it rarely offers work a node will refuse. When
it does, the node refuses (`NodeExecutionPolicy`), the workload is reported `refused` with the
node's reason, and the scheduler does not offer it to that node again until the spec changes.

## Networks

The design follows one idea: **a node has an identity and an ordered list of ways to reach it**.
Networks only supply addresses.

- Each `INetworkProvider` reports whether its network is usable on this machine, which addresses
  it gives the local listener, and whether a given address is one of its own. It describes; it
  does not retry, choose or change anything.
- An agent orders candidate addresses nearest first (loopback, LAN, private network, public,
  tunnel), probes them together, and uses the nearest that answers. A failed route cools down
  (15 s doubling to 5 min); a better route that starts answering again is taken up within a
  minute. The agent loop is the single owner of retries.
- Addresses are **hints**. Before anything signed is sent to an address, it must answer the
  public identity document (`/.well-known/burbuja/cluster`) as the expected cluster, and every
  response must then carry a signature made with the node's key. An address that copies the
  identity document but cannot sign is dropped after one request
  (`EndToEndTests.An_address_that_cannot_prove_it_is_the_controller_is_abandoned_for_one_that_can`).
- The controller reports its own addresses in every sync. A node adds them as learned routes and
  forgets the ones the controller stops reporting.
- **The network is recorded, not inferred from the address.** Carrier-grade NAT space
  (`100.64.0.0/10`) is used by more than one overlay, so the same address can belong to different
  networks on different machines. A provider claims an address by membership or by name, never by
  numeric range alone.

| Network | Provider | What it does |
|---|---|---|
| `loopback`, `lan`, `static` | built in | Loopback; private IPv4 addresses on active non-tunnel interfaces; addresses listed in `Cluster:Advertise` |
| `tailscale` | `Burbuja.Plugins.Cluster.Tailscale` | Reads `tailscale status --json` (read-only). Advertises the MagicDNS name and tailnet address, or `Cluster:Tailscale:Advertise` for a node published with `tailscale serve`. Claims tailnet members and names under the tailnet suffix |
| `twingate` | `Burbuja.Plugins.Cluster.Twingate` | Twingate is not a mesh: a machine is reachable because an administrator defined it as a Resource behind a Connector. The operator states those addresses in `Cluster:Twingate:Advertise` and the alias suffixes in `Cluster:Twingate:Suffixes`; the provider claims names under those suffixes |

Another network is another `INetworkProvider` registered by a plugin. A mechanism that needs
setup before it can be dialled (a forwarded port) implements `PrepareAsync` and removes exactly
what it created when the route is released.

## Workloads and placement

```json
{
  "name": "trainer",
  "kind": "process",
  "class": "batch",
  "payload": { "program": "trainer", "parameters": { "shard": "0" } },
  "requests": { "gpu": 1, "cpu": 8 },
  "placement": { "requiredCapabilities": [ "cuda" ], "preferredNodes": [ "tower" ] },
  "execution": { "priority": "background", "yieldToInteractive": true, "limits": { "memoryMb": 40000 } },
  "lifecycle": { "restart": "always", "stopGrace": "00:02:00", "onNodeLoss": "wait" },
  "exclusiveKey": "learner/gpu"
}
```

**Eligibility is the kernel's; strategies only rank.** A node is eligible when it is active and
reachable, runs the kind, is in the workload's node list if there is one, has every required
label and capability, has room for the declared requests, and was not the node that refused it.
Contributed filters can remove more nodes; scorers order what is left. No score can place work on
an ineligible node. Decisions are deterministic: same state, same answer.

**Requests are declared, not measured.** Placement adds requests against lent capacity. Measured
load is advisory and feeds pressure only.

**The class sets the failure direction.**

| Class | On a node with no fresh reading, or under pressure |
|---|---|
| `interactive`, `service` | Still placed, ranked lower. A dead monitor must not become an outage |
| `batch`, `bestEffort` | Not placed. Nobody is waiting, and starting heavy work on a host nobody can see is how a shared machine gets hurt |

Pressure has hysteresis: three pressed readings set it, six clear ones release it.

**Nothing moves unless it must.** A placed workload stays where it is: no rebalancing, no
preemption. `onNodeLoss: wait` (the default) keeps a workload bound to its node however long the
node is gone, reported as `unknown`, never as stopped. `reschedule` places it elsewhere once the
node has been silent for the loss grace. Draining a node moves only `reschedule` work, and only
when another node can take it.

**Exclusive keys fail closed.** At most one running workload holds a key (an account, a role, a
device), provided each node's agent is running or was stopped normally (see the limit below).

- A node that has had no successful sync for `ExclusiveTtlSeconds` starts stopping the holder. The
  check runs on its own 200 ms clock, independent of how long a failing sync attempt takes.
- The controller gives the key to nobody else until the old node confirms the work stopped. If
  that node is silent, it waits `2 x ExclusiveTtlSeconds + the workload's stopGrace + 5 s`,
  counted from the last sync it received. A long grace therefore holds a key for that long.
- Changing a placed workload's key is a new claim: the old key stays held until the node confirms
  the old generation stopped, and the new key is asked for like any other placement.
- Stopping a node process stops the work holding exclusive keys there, whatever
  `StopWorkloadsOnShutdown` says, because with the agent gone nothing on the machine could give
  the key up. Other work is left running.
- Keep `ExclusiveTtlSeconds` (default 30) comfortably above the 10-second timeout of one call to
  the controller. The controller's extra time-to-live is what absorbs the gap between when it
  processed a node's last sync and when the node received the answer.
- **Limit:** the fence is the agent. If the agent process dies without the chance to stop its
  work and is not started again, that work keeps running while the controller eventually frees
  the key. Run the node under a service manager that restarts it: the next process adopts the
  work and fences it against the last contact the dead process recorded, not against its own
  start. The timing also assumes the two machines' clocks run at the same rate; they need not be
  set alike.

**Generations.** Every placement or spec change takes the next number from one cluster-wide
counter that is never reused, so a workload deleted and created again under the same name gets a
generation no node has seen. A node runs exactly the generation it was last told and reports under
it; a report under any other generation says nothing about the current one. A changed spec stays
on its node when the node can still satisfy what the spec asks (the node being cordoned, silent or
pressed at that moment is no reason to uproot it), and the node stops the old generation before
starting the new one.

## Pressure, for anything that should hold back

Every member computes its own verdict with the same three-set, six-clear hysteresis the
controller applies, and can read it in process through `INodePressure`:

- `Local`: this node's verdict (`Pressured`, `Known`, the level and its reasons). `Known` is false
  when the node has no reading of itself from the last 90 seconds; an unknown verdict is not a
  clear one, and what to do without it is the reader's decision.
- `PressuredNodes`: the members the controller last reported as pressed. It empties when that
  advice is older than 90 seconds, so stale advice never keeps excluding a node.

Both only read cached snapshots, so they are safe on a game loop. A node needs no controller in
the path for its own verdict; one machine with `Cluster:Role=all` gets it with no network at all.

What counts as pressed: processors busy (`CpuBusyPercent`, default 85), memory low
(`MinFreeMemoryMb`, default 768), and on Linux a thermal zone at or above `MaxTemperatureC` when
that is set. Other signals are another `INodeFactsSource`.

**Holding back the realtime module.** With `Cluster:Agent:GuardRealtime=true` in a full host, a
pressed node lets no session claim more node resources (`ISessionResourceAdmission`); claims
already held are untouched, and an unknown verdict admits. A game's own placement strategy can
read `PressuredNodes` to steer new rooms away from pressed members. Together these replace a
status file written by an external monitor and copied between hosts.

## One vocabulary for the operating system

`ExecutionProfile` is what a workload asks of the machine. Each platform's adapter says which
**aspects** it can really enforce; the node advertises them as `os.<aspect>` capabilities; and a
strict profile (the default) is placed only where every aspect it depends on is enforceable.
Asking is not getting, and the difference is never silent.

| Aspect | macOS | Linux | Notes |
|---|---|---|---|
| `priority` | `taskpolicy -b`, `nice` | `nice`, `CPUWeight` in a scope | Background work is kept off performance cores on Apple Silicon |
| `pause` | signals to the process tree | same | See the interactive gate below |
| `cpu-limit`, `memory-limit`, `cpu-set` | not available | transient systemd scope, **only when the controller is delegated** | Read from `cgroup.controllers`; an undelegated limit is neither advertised nor half-applied |
| devices | none needed | `CUDA_VISIBLE_DEVICES`, set on every launch | Work given no accelerator sees none. A convention the program honours, not a barrier |
| `network-isolation`, `run-as` | not available | not available | In the vocabulary so a profile can require them; no built-in adapter enforces them yet |

A stronger mechanism (a container runtime, a sandbox) is another `IOsExecutionAdapter` with a
higher `Order`.

**The interactive gate.** A latency-critical workload takes a hold for the seconds it needs the
machine: `PUT /node/v1/interactive/{holder}?ttlMs=…` on the node's loopback API, with the token
from `local.token` (workloads receive `BURBUJA_NODE_URL` and `BURBUJA_NODE_TOKEN_FILE`). Every
workload on that node with `yieldToInteractive` is suspended until the last hold is released.
It is decided and applied on the node with no controller round trip. A hold always expires
(`InteractiveHoldMaxSeconds`), so a holder that crashes cannot leave batch work suspended.

## Security

- **Join.** The controller issues a one-time token. Both sides derive a join key from it, the
  node signs its join request, and both derive the node's long-lived key from the join key and
  two nonces. Neither the token nor the node key crosses the wire.
- **Every node request** is signed (method, path, time, nonce, body hash) and every response is
  signed back over the request's nonce and its own status. Requests outside a 90-second clock
  window are refused, and a nonce is accepted once.
- **Requests are addressed and ordered.** Each names the controller process it is meant for and
  carries a rising sequence number. A controller forgets nonces and sequences when it restarts,
  so a request from before the restart is refused for naming the old process, and a request that
  is not newer than the last one applied is ignored. A delayed or replayed report therefore
  cannot confirm that work stopped when it did not.
- **A replaced or removed node stands down.** A newer process with the same node id supersedes
  the older one; on the same machine the older one hands its work over without stopping it.
  Removing a node keeps its key long enough to prove the removal to it with a signed answer.
- **An unsigned "unknown node" is not believed.** Anything can answer 404. The node keeps its
  credential, sets that address aside and tries its other routes; only a fresh join token, and a
  signed welcome, replace a credential.
- **Operators** present a bearer token read from a file; **local programs** reach the node API
  only over the loopback and only with the node's local token. Credentials and state files are
  owner-only, written atomically.
- **Signing authenticates and protects integrity; it does not hide content.** Use a network that
  encrypts, or HTTPS, when workload specifications are themselves sensitive.
- **Work runs as the node's own account.** No built-in adapter enforces `run-as`, so a workload
  can read whatever that account can, including the node's credential and, on a node that is also
  the controller, the operator token. Run only programs you would trust with the node itself, and
  keep untrusted work off the controller's machine.
- **A node checks names itself.** Workload names become file names on the node (a log, a working
  directory), so every node validates them again rather than trust what the controller sent.
- **The node-local API trusts the loopback.** `/node/v1` requires a loopback connection and the
  local token. Behind a reverse proxy on the same machine every request looks local, so the token
  is then the only check; do not proxy that path.
- **Members are trusted to describe themselves.** A node states its own capacity and labels, and
  a join token admits a node under whatever id it presents, including one already in use, whose
  key it then replaces. The join token is the admission control; issue one per machine.

## Bring your own types

Everything above is a contract in `Burbuja.Cluster.Abstractions`, and a plugin adds to it by
registering ordinary services. A dynamic plugin references only the contract assemblies.

| To add | Implement | Example |
|---|---|---|
| A kind of workload with its own payload schema | `IWorkloadDriver` | [`CountdownDriver`](../examples/Example.Cluster/Example.Cluster.Plugin/ClusterExamplePlugin.cs): a kind that is not a process at all |
| A network | `INetworkProvider` | `plugins/Burbuja.Plugins.Cluster.Tailscale` |
| A sensor, an accelerator vendor | `INodeFactsSource` | `NvidiaFactsSource` |
| A platform or sandbox | `IOsExecutionAdapter` | `LinuxExecutionAdapter` |
| A placement rule | `IPlacementFilter`, `IPlacementScorer` | `QuietNodeScorer` |
| A resource | nothing: it is a name in `Lend` and `requests` | `player-slot` in the example |

`Hosting.Tests/ClusterModuleTests` loads the example plugin from its own load context into the
full kernel host, submits a workload of its kind, sees the plugin's own validation message for a
bad payload, and sees the host's node run a good one to completion.

## Gotchas, with evidence

- **Never suspend a direct child of a .NET process on macOS.** When a direct child is stopped
  with `SIGSTOP`, the runtime's child-signal handling spins and every later `Process.Start` in
  that process blocks for good. The process kind therefore starts work through a small waiting
  shell, so the node only ever suspends a grandchild, and the shell hands back the real exit
  status. Witness: `ProcessDriverTests.The_whole_tree_is_suspended_resumed_and_given_its_grace_to_finish`
  hung at its first process start after the suspend until the launcher was introduced.
- **Work outlives the node process, by design.** Output goes to `logs/<workload>.log`, not to a
  pipe the node holds, and a restarted node adopts work whose process id *and* start time still
  match (`EndToEndTests.A_removed_node_stops_the_clusters_work_and_a_restarted_node_keeps_it`).
  A restarted node adopts or leaves alone; it never starts a recorded workload afresh before the
  controller has confirmed the node should still run it. Work holding an exclusive key is the
  exception to outliving the node, as described above.
- **Two node processes on one machine share `workloads.json`.** The newer one owns it. The older
  one notices on its next write, lets go of the work without signalling it and stands down.
  A service manager can undo this: systemd's default `KillMode=control-group` and launchd without
  `AbandonProcessGroup` both end a service's children with it. Set those when the node is
  installed as a service. This interaction was reasoned from the managers' documented behaviour
  and has not been exercised by a test.
- **Suspension needs `ps`.** The process tree is enumerated with `ps`. On a host without it only
  the root process is signalled, while the node still advertises `os.pause`. Install `procps`.
- **Stopping a node waits at most `ShutdownGraceSeconds` (default 20) for work it must stop,**
  then ends it, whatever the work's own `stopGrace`. A host's shutdown timeout must be longer;
  `Burbuja.Node` sets its own. Work that needs its full grace should be stopped (drain, delete)
  before the node is.
- **The order of two enums is contract.** `ExecutionPriority` (most favourable first) and
  `WorkloadClass` (placed first to last) are compared by position. New members go at the end only
  if that is where they belong.
- **Background work started by the launcher ignores `SIGINT`.** That is how a shell starts a
  background command. Stops are requested with `SIGTERM`.
- **Linux limits are unit-tested, not exercised.** The whole cluster suite passes on Linux (an
  x64 container, 2026-11-15), which covers the launcher, `nice`, tree signals, adoption and the
  node protocol there. The transient systemd scope that enforces `cpu-limit`, `memory-limit` and
  `cpu-set` has not been run: `OperatingSystemTests` checks the exact `systemd-run` invocation and
  the delegation rule only. Try a limit on the target host before relying on it.
- **Twingate client states are unverified.** The provider maps the client tool's one-word state;
  the words were not checked against a live client. Anything unrecognised is reported unknown.

## What it does not do

1. **One controller.** There is no consensus between controllers; a second one is a second
   cluster. Nodes carry on with their last instructions while it is away.
2. **No preemption or rebalancing.** An interactive workload that does not fit stays pending even
   when batch work occupies the capacity. The interactive gate handles contention at run time;
   capacity is the operator's to size.
3. **No ARM projection.** Nodes and workloads are not yet `Burbuja.Cluster/*` resources with RBAC,
   ETags and event streams. The operator API is its own small surface with one bearer token.
4. **No data movement.** Replication, staged promotion, archives and versioned rollout are not
   modelled. Co-location is expressed with labels and node lists.
5. **No isolation between a workload and its node.** Running work under another account, in a
   sandbox or without network access is in the vocabulary (`run-as`, `network-isolation`) and is
   refused by default because nothing built in can enforce it.
6. **No fence without an agent.** See the limit under exclusive keys.
7. **No scheduling of other people's processes.** The module governs the work it started. Other
   tenants of a machine are respected only through what the operator lends.
8. **`Burbuja.Node` composes its networks statically.** Dynamic plugins (other kinds, other
   networks) load in a full kernel host. The abstractions project is not yet a published package.
9. **Not yet wired to the realtime fleet or the file-workload worker.** They keep their own
   membership and admission. Folding the fleet's node records and the monitor's hysteresis onto
   this module is the natural next step and was deliberately left out of this change.

## Planned next, in order

1. **The rest of pressure.** The node verdict, `INodePressure` and the realtime admission bridge
   exist. Still to do: a facts source for operator-declared service checks (a protected service
   that is down), a bridge into the file worker's headroom, and retiring the SSH collector in
   `tools/cluster` once a consumer has moved.
2. **A static `Burbuja.Cluster` plugin with declared extension points** (`cluster.network`,
   `cluster.workload-kind`, `cluster.facts`, `cluster.os-adapter`, `cluster.placement`) in the
   full host, so manifests are validated; and the probe helper behind an interface.
3. **An ARM projection** (`Burbuja.Cluster/workloads`, `nodes` with cordon/drain/activate actions,
   a join-token action, roles, ETags from generations, an event stream), written against
   `IClusterControlPlane`, with the bearer operator API then off by default in full hosts. The
   signed node protocol stays as it is, and `Burbuja.Node` stays free of `Burbuja.Core`.
4. **State through the kernel's stores** (`IResourceStore`, `ILeaseStore`) in the full host.
5. **One protocol revision**: a controller-dialled link for nodes that cannot dial out, routes
   with a base path, version negotiation, and a per-workload fence policy.
6. **Realtime drain and resource unification**, last, because it touches a live consumer.

## Verify

```sh
dotnet test tests/Burbuja.Cluster.Tests -c Release -m:1     # placement, security, networks, OS, real processes, real hosts
dotnet test tests/Burbuja.Hosting.Tests -c Release -m:1 --filter FullyQualifiedName~ClusterModuleTests
examples/Example.Cluster/three-machines/run.sh              # the installable binary, four processes
```

The cluster tests start real process trees and real loopback listeners; the process and
end-to-end cases skip on Windows.
