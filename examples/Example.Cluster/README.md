# Example: a cluster of three differently shaped machines

[Cluster guide](../../docs/cluster.md) · [Kernel README](../../README.md)

Two things live here:

- **`three-machines/`**: configuration for a controller and three nodes, the workloads to place on
  them, and a script that runs all four as separate processes on one computer.
- **`Example.Cluster.Plugin/`**: a contracts-only plugin that adds its own workload kind and a
  placement preference, to show what an application brings without changing the kernel.

## The scenario

A game-playing agent runs on three machines that are good at different things:

| Node | Stands for | Lends | Allows |
|---|---|---|---|
| `studio` | A fast desktop used for live play | 2 player slots | the `player` program |
| `tower` | A GPU workstation shared with other people | 2 player slots, 6 cores, the `bulk-compute` capability | `player`, `trainer` |
| `edge` | A small always-on box | 2 cores, label `role=edge`, nothing above normal priority | `proxy` |

`workloads.json` asks for three players (interactive, one slot each, each holding its own
exclusive account key, preferring `studio`), a trainer (batch, needs `bulk-compute`, yields to
interactive work), a proxy (must run on a node labelled `role=edge`) and a second trainer that
asks for a memory ceiling.

On real hardware the workstation's accelerators are detected and the trainer would ask for
`"requests": {"gpu": 1}` and `"requiredCapabilities": ["cuda"]`. The example uses a declared
capability instead so that it behaves the same on any one computer.

## Run it

```sh
make build
examples/Example.Cluster/three-machines/run.sh
```

What to look for in the output:

- Each node joins with its own one-time token, created by `burbuja-node ctl token`.
- Players one and two land on `studio`; the third overflows to `tower` because `studio` lends two
  slots. The trainer lands on `tower`, the proxy on `edge`.
- The second trainer stays **pending, with a reason per node**. On macOS the reason for `tower` is
  `capability 'os.memory-limit' required`: the platform cannot cap a process tree's memory, so the
  cluster does not run the job there under a limit somebody would believe is in force.
- When interactive work on `tower` takes a hold through the node's local API, the trainer is
  reported `paused` and its process tree is actually suspended; when the hold is released it
  resumes.

Everything runs under a temporary directory that the script removes when it ends. Stopping a node
stops its work here because the example sets `StopWorkloadsOnShutdown`; the default is the
opposite, so that upgrading a node does not interrupt what it runs.

## The plugin

`CountdownDriver` implements `IWorkloadDriver` for the kind `example.countdown`. Its payload is
its own record (`CountdownPayload`), validated by the plugin, and the work is not a process at
all: it stands in for anything an application runs inside its own host. `QuietNodeScorer` ranks
eligible nodes by how little they run.

```json
{ "name": "tick", "kind": "example.countdown", "payload": { "seconds": 30, "label": "demo" } }
```

Install it like any other dynamic plugin, in a kernel host with `Cluster:Enabled=true`:

```sh
dotnet publish examples/Example.Cluster/Example.Cluster.Plugin -c Release \
  -o <host>/plugins/Example.Cluster
```

`tests/Burbuja.Hosting.Tests/ClusterModuleTests.cs` does exactly that against the real loader and
runs a countdown to completion on the host's own node.

## From one computer to three

1. Publish `src/Burbuja.Node` for each platform and copy it over.
2. On the controller, bind `Cluster:Listen` to an address the others can reach (an overlay
   address, a LAN address) as well as the loopback.
3. On each member, list those addresses in `Cluster:Agent:Controllers`, put a fresh join token in
   `Cluster:Agent:JoinTokenFile`, and write the node's own `Lend`, `LendDevices`, `Labels` and
   `Programs`. Machine inventories, addresses and tokens belong in private operator
   configuration, not in a repository.
4. Start the node. It needs no inbound access.
