#!/bin/sh
# Runs the three-machine example on one computer: a controller and three nodes as separate processes on
# loopback ports 18620-18623, each with its own state directory. Build the kernel first (`make build`).
set -eu

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../../.." && pwd)
node="$root/src/Burbuja.Node/bin/Release/net10.0/burbuja-node.dll"
[ -f "$node" ] || { echo "Build first: make build" >&2; exit 1; }

work=$(mktemp -d "${TMPDIR:-/tmp}/burbuja-three-machines.XXXXXX")
pids=""
cleanup() {
  # Stopping a node stops its work here because the example sets StopWorkloadsOnShutdown.
  for pid in $pids; do kill "$pid" 2>/dev/null || true; done
  for pid in $pids; do wait "$pid" 2>/dev/null || true; done
  rm -rf "$work"
}
trap cleanup EXIT INT TERM

ctl() { dotnet "$node" ctl --controller http://127.0.0.1:18620 --state "$work/hub" "$@"; }
start() { # name, then extra settings
  name=$1; shift
  dotnet "$node" --config "$here/$name.json" "--Cluster:StateDirectory=$work/$name" "--Logging:LogLevel:Default=Warning" "$@" \
    > "$work/$name.log" 2>&1 &
  pids="$pids $!"
}
wait_for() { # description, then a command that succeeds when ready
  what=$1; shift
  i=0
  until "$@" >/dev/null 2>&1; do
    i=$((i + 1)); [ "$i" -lt 150 ] || { echo "Timed out waiting for $what" >&2; exit 1; }
    sleep 0.2
  done
}

echo "== starting the controller"
start hub "--Cluster:Controller:SyncIntervalSeconds=1" "--Cluster:Controller:NodeTtlSeconds=5" "--Cluster:Controller:ExclusiveTtlSeconds=5"
wait_for "the controller" ctl status

echo "== each machine joins with its own one-time token"
for name in studio tower edge; do
  mkdir -p "$work/$name"
  ctl token 2>/dev/null > "$work/$name/join.token"
  # A node runs only the programs its own file lists; here each is pointed at the stand-in script.
  case $name in
    studio) set -- "--Cluster:Agent:Programs:player:Arguments:0=$here/work.sh" ;;
    tower)  set -- "--Cluster:Agent:Programs:player:Arguments:0=$here/work.sh" "--Cluster:Agent:Programs:trainer:Arguments:0=$here/work.sh" ;;
    edge)   set -- "--Cluster:Agent:Programs:proxy:Arguments:0=$here/work.sh" ;;
  esac
  start "$name" "--Cluster:Agent:JoinTokenFile=$work/$name/join.token" "$@"
done
wait_for "three nodes" sh -c "dotnet '$node' ctl --controller http://127.0.0.1:18620 --state '$work/hub' status | grep -c ' ready ' | grep -q 3"

echo "== applying the workloads"
ctl apply "$here/workloads.json" || true
sleep 4
echo
ctl status

echo
echo "== a player on the workstation starts answering: batch work there is suspended at once"
token=$(cat "$work/tower/local.token")
curl -fsS -X PUT -H "Authorization: Bearer $token" "http://127.0.0.1:18622/node/v1/interactive/turn-1?ttlMs=20000"
sleep 3
ctl status | sed -n '/WORKLOADS/,$p'

echo
echo "== the turn is over: it resumes"
curl -fsS -X DELETE -H "Authorization: Bearer $token" "http://127.0.0.1:18622/node/v1/interactive/turn-1"
sleep 3
ctl status | sed -n '/WORKLOADS/,$p'

echo
echo "== what the trainer wrote on the workstation"
cat "$work/tower/logs/trainer.log"
