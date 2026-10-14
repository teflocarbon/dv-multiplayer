#!/usr/bin/env bash
# Player soak (report: "after some time a player freezes for the others and shows up somewhere
# else"): both players spend DURATION seconds hopping, travelling far, boarding and riding a loco the host
# pushes, going home and idling. Each records where it really is and where it sees the other;
# the views must never stay more than MAX metres off for more than HOLD seconds.
#   DURATION=240 (seconds)  MAX=3 (m)  HOLD=5 (s)  LAG=client receive lag in ms  SEED=1
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/player-soak
DURATION=${DURATION:-240}; MAX=${MAX:-3}; HOLD=${HOLD:-5}; LAG=${LAG:-0}; SEED=${SEED:-1}
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
lag() { $D eval -t client "Steamworks.SteamNetworkingUtils.FakeRecvPacketLag = $1f" >/dev/null 2>&1 || true; }
loco=$($D eval -t host -m coroutine 'var car = Trains.SpawnNearPlayer("LocoDE2"); for (int i = 0; i < 120; i++) yield return null; ctx.Result = new { id = car.ID };' | field id)
cleanup() { lag 0; $D eval -t host -m body "Trains.Delete(Trains.Find(\"$loco\")); return 0;" >/dev/null 2>&1 || true; }
trap cleanup EXIT
lag $LAG
T=$(mktemp -d)
echo "== both players act for ${DURATION}s (loco $loco)"
A="-a seconds=$DURATION -a loco=$loco -a homeX=7194 -a homeZ=6873"
$D eval -t host -m coroutine --timeout $((DURATION + 120)) $A -a seed=$SEED -f $P/act.cs >"$T/h.json" &
$D eval -t client -m coroutine --timeout $((DURATION + 120)) $A -a seed=$((SEED + 100)) -f $P/act.cs >"$T/c.json" &
wait
node $P/compare.js "$T/h.json" "$T/c.json" $MAX $((HOLD * 1000))
