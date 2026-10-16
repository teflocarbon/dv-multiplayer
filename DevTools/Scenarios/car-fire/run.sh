#!/usr/bin/env bash
# Radio damage (#74) and car fires from a client. The client's comms radio damages a gasoline
# tank car until it leaks, and lights the leak with a lighter's igniter: the host must take the
# damage, burn and explode the car, and both peers must agree throughout. Then the host lights a
# diesel tank of its own, and the client must show it burning. Turns the radio's cheat mode on in
# both games for the run.
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/car-fire
result() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}console.log(JSON.stringify(x.result))'; }
cars=""
cleanup() { for c in $cars; do $D eval -t host -m body -a id=$c 'var c = Trains.Find(ctx.Args["id"]); if (c != null) Trains.Delete(c); return 0;' >/dev/null 2>&1 || true; done; }
trap cleanup EXIT
fail=0
tank() { # cargo -> car id: a loaded tank car near the host, handbrake on
  $D eval -t host -m coroutine -a cargo=$1 'var c = Trains.SpawnNearPlayer("TankChrome"); c.brakeSystem.SetHandbrakePosition(1f); for (int i = 0; i < 90; i++) yield return null; c.logicCar.LoadCargo(c.cargoCapacity, DV.Globals.G.Types.cargos.First(x => x.id == ctx.Args["cargo"]).v1); for (int i = 0; i < 180; i++) yield return null; ctx.Result = c.ID;' | result | tr -d '"'; }
both() { # car -> "host-state|client-state" of the fields given
  local h c; h=$($D eval -t host -m body -a car=$1 -f $P/fire-state.cs | result); c=$($D eval -t client -m body -a car=$1 -f $P/fire-state.cs | result)
  node -e 'const h=JSON.parse(process.argv[1]),c=JSON.parse(process.argv[2]);const f=process.argv[3].split(",");console.log(f.map(k=>k+" "+h[k]+"/"+c[k]).join(", ")+(f.every(k=>h[k]===c[k])?"":"  DIFFERENT"))' "$h" "$c" "$2"; }
expect() { # what line... : FAIL unless the line has the wanted values and no DIFFERENT
  echo "   $1: $2"; [[ "$2" != *DIFFERENT* && "$2" == *"$3"* ]] || { echo "   FAIL: wanted $3 on both"; fail=1; }; }

$D eval -t all -m body 'DV.Globals.G.GameParams.CommsRadioCheatMode = true; return 0;' >/dev/null

echo "== the client's radio damages a gasoline tank"
T=$(tank Gasoline); cars="$T"
for i in 1 2; do $D eval -t client -m coroutine -u "using DV;" -a car=$T -a mode=damage -a level=4 -f $P/radio.cs >/dev/null; sleep 2; done
expect "after two hits" "$(both $T leaking,health)" "leaking true/true"

echo "== the client lights the leak"
$D eval -t client -m coroutine -a car=$T -f $P/light.cs >/dev/null; sleep 2
expect "burning" "$(both $T ignited,aboutToExplode)" "ignited true/true"
for i in $(seq 1 12); do [[ "$(both $T exploded)" == *"true/true"* ]] && break; sleep 5; done
expect "then" "$(both $T exploded)" "exploded true/true"

echo "== the host lights a diesel tank of its own"
T2=$(tank Diesel); cars="$cars $T2"
for i in 1 2; do $D eval -t host -m coroutine -u "using DV;" -a car=$T2 -a mode=damage -a level=4 -f $P/radio.cs >/dev/null; sleep 2; done
$D eval -t host -m coroutine -a car=$T2 -f $P/light.cs >/dev/null; sleep 2
expect "burning" "$(both $T2 ignited)" "ignited true/true"

[ $fail = 0 ] && echo "PASS" || { echo "FAIL"; exit 1; }
