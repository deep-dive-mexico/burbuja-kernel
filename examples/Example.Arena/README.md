# Example Arena

A proof of concept for the kernel's [real-time lobbies](../../docs/realtime-lobbies.md):
a top-down arena shooter for 2–8 players, simulated by the server at 30 Hz.

- **Example.Arena.Plugin** registers one `IRealtimeGame` (`arena`). It references only the two
  abstractions assemblies and is installed dynamically; the host has no reference to it.
- **Example.Arena.Host** is its own executable on `Burbuja.Hosting`, with realtime and guests enabled,
  two in-process lobby servers (`arena-a`, `arena-b`, 4 lobbies each, `least-loaded` placement) and a
  single-page canvas client at `/arena`.

## Build, install, run

From this directory, with a physical path to a kernel checkout (this repository works):

```bash
BURBUJA_ROOT="$(cd ../.. && pwd -P)"
dotnet publish Example.Arena.Host/Example.Arena.Host.csproj -c Release -p:BurbujaRoot="$BURBUJA_ROOT" -o artifacts/host
dotnet publish Example.Arena.Plugin/Example.Arena.Plugin.csproj -c Release -p:BurbujaRoot="$BURBUJA_ROOT" \
  -o artifacts/host/plugins/Example.Arena
dotnet artifacts/host/Example.Arena.Host.dll
```

Open <http://127.0.0.1:18495/arena> in two browser windows (or two machines on the same network, with
`--urls=http://0.0.0.0:18495`). Enter a name in each and press **Quick play**: the queue matches both into
one lobby, a 3-second countdown starts, and the match runs. Move with WASD, aim with the mouse, hold click
to fire. First to 10 kills, or the leader after two minutes, wins. **Create private lobby** shows a join code
to type into the other window. Refresh mid-match and **Rejoin match** takes back the same seat within the
10-second reconnect grace.

The client needs a keyboard and mouse; there are no touch controls.

Operator view (development key from `appsettings.json`):

```bash
curl -s -H 'x-api-key: arena-operator-dev-key' http://127.0.0.1:18495/realtime/v1/servers
```

Data lives in memory and disappears on restart. The credentials are public development values.

## What the game does, and what the kernel does

| Arena (`ArenaSession`) | Kernel realtime module |
|---|---|
| Movement, walls, bullets, hits, respawns, score, win condition | Queue, matchmaking, placement on a lobby server, seat reservations |
| Clamps and normalises the client's `input` intent | Lifecycle (`open → countdown → running → ended → closed`), the 30 Hz fixed tick |
| Broadcasts a `state` snapshot each tick and a `kill` feed | WebSocket transport, rate and size limits, reconnect grace, ping |
| Builds the result scoreboard | Results broadcast, capacity release, isolation if the game throws |

`tests/Burbuja.Realtime.Tests/ArenaHostTests.cs` runs this host with the plugin installed dynamically,
matches two guests over HTTP and plays over real WebSocket frames.
