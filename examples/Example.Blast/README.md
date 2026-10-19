# Example Blast

A proof of concept for the kernel's [real-time lobbies](../../docs/realtime-lobbies.md):
a 3D battle game for 2–8 players inspired by Bomberman 64's Battle mode, simulated by the server
at 30 Hz and broadcast at 15 Hz. Last player standing wins the round; first to the configured
wins total wins the match.

- **Example.Blast.Plugin** registers one `IRealtimeGame` (`blast`). It references only the two
  abstractions assemblies and is installed dynamically; the host has no reference to it.
- **Example.Blast.Host** is its own executable on `Burbuja.Hosting`, with realtime and guests enabled,
  two in-process lobby servers (`blast-a`, `blast-b`, 4 lobbies each, `least-loaded` placement) and a
  single-page three.js client at `/blast`.

## Build, install, run

From this directory, with a physical path to a kernel checkout (this repository works):

```bash
BURBUJA_ROOT="$(cd ../.. && pwd -P)"
dotnet publish Example.Blast.Host/Example.Blast.Host.csproj -c Release -p:BurbujaRoot="$BURBUJA_ROOT" -o artifacts/host
dotnet publish Example.Blast.Plugin/Example.Blast.Plugin.csproj -c Release -p:BurbujaRoot="$BURBUJA_ROOT" \
  -o artifacts/host/plugins/Example.Blast
dotnet artifacts/host/Example.Blast.Host.dll
```

Open <http://127.0.0.1:18496/blast> in two or more browser windows. Enter a name and press
**Quick play** to enter the matchmaking queue; both windows will land in the same lobby. Or use
**Create private lobby** to pick settings (stage, win target, bots, round duration). The lobby's
join code shows while it waits for players. A lobby with bots starts with you alone, as in the original.

Operator view (development key from `appsettings.json`):

```bash
curl -s -H 'x-api-key: blast-operator-dev-key' http://127.0.0.1:18496/realtime/v1/servers
```

## Run a two-node fleet on one machine

The same host becomes a fleet node by installing the Redis backplane plugin next to the game. This runs two
nodes, `east` on :18496 and `west` on :18497, over a throwaway Valkey:

```bash
docker run -d --rm --name blast-valkey -p 127.0.0.1:16379:6379 valkey/valkey:8-alpine
dotnet publish ../../plugins/Burbuja.Plugins.Realtime.Redis -c Release -o artifacts/plugin-redis
for node in east:18496 west:18497; do
  id=${node%%:*}; port=${node##*:}
  cp -R artifacts/host artifacts/$id && cp -R artifacts/plugin-redis artifacts/$id/plugins/Realtime.Redis
  (cd artifacts/$id && urls=http://127.0.0.1:$port Kernel__NodeId=$id \
    Realtime__Servers__0__Id=$id-1 Realtime__Servers__0__PublicUrl=ws://127.0.0.1:$port \
    Realtime__Servers__1__Id=$id-2 Realtime__Servers__1__PublicUrl=ws://127.0.0.1:$port \
    Realtime__Redis__Configuration=127.0.0.1:16379 \
    Realtime__Guests__SigningKey=local-fleet-guest-signing-key-0123456789 \
    dotnet Example.Blast.Host.dll &)
done
```

Press **Quick play** in one window on <http://127.0.0.1:18496/blast> and another on
<http://127.0.0.1:18497/blast>. Both players land in one match on whichever node the fleet placed it, and
each browser connects to that node. `GET /realtime/v1/servers` on either node lists all four servers. Each
node needs its own server ids and `PublicUrl`, and every node shares the guest signing key.

## Controls

| Action | Keys |
|---|---|
| Move | WASD or arrow keys |
| Place bomb / pump | Space or F |
| Pick up / throw | Shift or J |
| Remote detonate | K or R |
| Gamepad | Left stick = move, others configurable |

## Lobby settings

| Setting | Values | Default |
|---|---|---|
| `stage` | `rock-garden` · `up-and-down` | `rock-garden` |
| `wins` | 1–5 | 3 |
| `round-seconds` | 60–300 | 120 |
| `sudden-death` | `on` · `off` | `on` |
| `ghosts` | `on` · `off` | `on` |
| `bots` | 0–7 | 0 |

## Rules as implemented

**Bombs** are placed at the player's current grid cell (one bomb per cell). A bomb has:
- **Fire level** 1–3 (normal) or up to 6 with pumps; blast radius = `level × 0.9 + pumps × 0.75 + 0.4` cells.
- **Fuse** 2.5 s by default; remote bombs detonate on the R button.
- **Red** bombs have a piercing blast (ignores soft blocks for occlusion) and a red tint.
- **Pumping**: press A while holding a bomb to grow it (max 3 pumps; must pick it up first).

**Kicks**: walking into a resting bomb sends it rolling in the player's direction. A rolling bomb
stuns any player it hits (1.5 s); hitting a resting bomb stops the rolling bomb and sends the
resting one forward (billiards).

**Picks and throws**: press B to lift a resting bomb or a stunned player. Release B to throw in
the facing direction; distance scales with analog stick magnitude. A bomb landing on a player's
head stuns them and bounces on.

**Explosions** are spherical. A pillar or soft block between the explosion center and a target
blocks the blast (unless the bomb is red). Chain reactions detonate any bomb within the blast
radius. Soft blocks have a 30% chance to drop an item.

**Items**: Fire (+1 fire level), Bomb (+1 capacity, max 8), Remote (detonates on R), Red (piercing
blast), Heart (absorb one hit). One collected item is dropped on death.

**Ghosts** (when enabled): a player killed for the first time becomes a translucent ghost. Ghosts
cannot place bombs but can kick, pick up and throw them. A ghost can latch onto a living player and
override their movement until the victim does 6 direction reversals within 4 s. Touching an item
destroys it without effect. Ghosts are removed when Hurry Up starts.

**Sudden death ("Hurry Up!")** triggers with 60 s left. One column closes from each side every
second; players and resting bombs in a closed column are eliminated/exploded. Ghosts are removed
when it starts. A time-out with two or more survivors alive is a draw; no win is awarded.

**Bots** use a simple deterministic AI: danger map from live bombs, BFS to safety when in danger,
bomb placement next to soft blocks or players when an escape exists. All randomness uses
`ctx.Random` (seeded per lobby), so the same seed + same inputs → identical state.

**Rounds**: Ready (2 s frozen) → Fight → optional Hurry → Over (3 s scoreboard) → repeat. A bot
win ends the match without a named winner.

## Designer choices vs the original

| Decision point | Original unclear / missing | Implemented as |
|---|---|---|
| Fuse length | Not documented | 2.5 s |
| Time-up with ≥2 alive | Ambiguous | Draw; no win awarded |
| Item drop on death | Unspecified | One collected power-up drops at death position |
| Mid-match joiner | N/A (console local) | Spectates as ghost until next round |

## Wire messages

### Client → server

```
{"t":"input","d":{"x":f,"z":f,"a":bool,"b":bool,"r":bool},"s":seq}
{"t":"stage"}   — request a resend of the static stage layout
```

### Server → client

```
{"t":"stage","d":{id, name, width, height, cells[{k,t,rx,rz,rf,rt}], spawns, round, winsTarget, roundSeconds}}
{"t":"state","k":tick,"d":{round, phase, timeLeft, closedLeft, closedRight,
  players[{slot,id,name,bot,x,y,z,fx,fz,alive,ghost,stunned,carrying,heart,fire,bombs,red,remote,wins,ack}],
  bombs[{id,x,y,z,level,pumps,red,remote,fuse,state}],
  hazards[{id,x,z,r}], items[{id,cx,cz,type}], softBlocks:base64}}
```

Cell kinds: `0=Floor`, `1=Pillar`, `2=Void`, `3=Water`, `4=Ramp`.  
Phase values: `0=Ready`, `1=Fight`, `2=Hurry`, `3=Over`.  
Bomb states: `0=Resting`, `1=Rolling`, `2=Carried`, `3=Flying`.
