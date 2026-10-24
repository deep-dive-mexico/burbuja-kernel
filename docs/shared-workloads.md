# Bounded file workloads on a shared host

`Burbuja.Worker` is an optional, independent native host for expensive file
transformations. It references the kernel's Compute abstractions and does not
upgrade or combine an existing game, bot, database, credentials or deployment.
Existing `Burbuja.Compute` workspaces and their execution grants are unchanged.

The operator supplies `Worker` configuration: an absolute token-file path,
headroom-file path, private scratch directory and named `Workloads`. Each
workload has an absolute executable, fixed argument array, an allowlist mapping
parameter names to flags, a deadline and input/output limits. `{input}` and
`{output}` expand to generated private files; the program writes `{output}.json`.
Arguments use `ProcessStartInfo.ArgumentList`, never a shell. The subprocess
receives a minimal environment, not host API keys. Use an operator-configured
priority wrapper and fixed application thread count where appropriate.

The host binds only `127.0.0.1:18510`. Every endpoint requires `Authorization:
Bearer <token>`; the token is read from a file and must contain at least 32
characters. Carry remote access over an authenticated encrypted tunnel. Do not
publish this endpoint through the player gateway. `GET /workloads/v1/status`
reports activity and counters. `POST /workloads/v1/{name}/run` accepts a binary
body and optional `X-Workload-Parameters` (base64 UTF-8 JSON string dictionary).
Unknown workloads/options and malformed input fail; there is no remote shell.

One admitted request owns the slot through upload, execution and cleanup.
Capacity or denied headroom returns 429 with Retry-After. No unbounded server
queue accumulates audio. Clients retain durable input and own retries; only pure
file transformations should use this interface. It does not promise exactly-once
execution after a lost response. Input is limited to 256 MiB by default and JSON
output to 4 MiB. Request cancellation, shutdown and deadlines kill the owned
process tree. Critical/stale host telemetry also interrupts execution. Temporary
files are removed on handled completion/failure/cancellation. Abrupt host loss
can leave scratch files; protect the disk and inspect leftovers during recovery.

`WorkloadHeadroom` contains `observedAt`, `admit` and `reason`. Missing, malformed,
future or older-than-90-second observations do not admit work. The optional
`tools/cluster` monitor samples CPU, memory pressure, disk, containers, configured
protected services, DNS and Tailscale; authenticated local kernel fleet reads add
room/player counts. Machine inventories and tokens belong in private operator
configuration, not this repository. Expected HTTP status codes can be explicit
for authentication-protected services. Swap occupancy alone is not a failure.

The monitor publishes a cooperative `background.json` budget and `realtimeBusy`:
background consumers can defer or lower priority while local game rooms or file
workloads run. Realtime pressure recommendations use three bad samples to defer
and six good samples to recover. The collector never changes manual drains,
running matches, measured game capacity or the backplane. A separately deployed
product placement adapter can consume the recommendations; `placement-enabled`
marks that verified installation for the monitor UI. Pressure on the primary also
reserves background headroom on the compute host for incoming rooms. Without the
adapter the recommendations remain observation-only.

On macOS the Tailscale app executable needs `TAILSCALE_BE_CLI=1` in a LaunchAgent;
otherwise it can launch the GUI rather than return JSON. See the
[official CLI documentation](https://tailscale.com/docs/reference/tailscale-cli?tab=macos).
The monitor sets this only for its own subprocesses, with no network changes.

Build and verify:

```sh
dotnet test tests/Burbuja.Worker.Tests -c Release -m:1
python3 -m unittest discover -s tools/cluster -p 'test_*.py'
make test
dotnet publish src/Burbuja.Worker -c Release -r osx-arm64 --self-contained true -m:1 -o artifacts/cluster-worker
```

The 2026-10-24 candidate passed all 1,433 kernel tests, including the independent
Workbench consumer/host smoke, plus four monitor tests. Worker tests exercise
real subprocess execution, literal arguments, size limits, stale admission,
concurrent refusal, cancellation and slot reuse. A separate TypeScript consumer
round-tripped synthetic Spanish audio over an SSH reverse tunnel to a native
Apple Silicon worker, retaining raw transcript JSON and deleting scratch input.
This is a tested source candidate, not a new kernel release or a game-kernel pin.
