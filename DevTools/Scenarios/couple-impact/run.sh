#!/usr/bin/env bash
# Coupling on impact (the "bounce" report): the host runs a loco into a standing wagon a bit too
# fast and a player couples the moment the couplers meet, while the buffers are still bouncing.
# Both peers must agree whether the cars are coupled, and when the host pulls away the wagon must
# follow (or stay) on both.
#   SPEEDS="2 3 4" (or 2,3,4) m/s  WHO=host|client (who couples)  CHAIN=1: hook the chain instead of a full
#   couple  WAGON_BRAKE=0|1 (default 1)  LAG=client receive lag in ms
#   HITCH=ms: the coupling peer stalls four frames that long just after coupling
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/couple-impact
SPEEDS=${SPEEDS:-2 3 4}; WHO=${WHO:-client}; CHAIN=${CHAIN:-0}; WAGON_BRAKE=${WAGON_BRAKE:-1}; LAG=${LAG:-0}; HITCH=${HITCH:-0}
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
lag() { $D eval -t client "Steamworks.SteamNetworkingUtils.FakeRecvPacketLag = $1f" >/dev/null 2>&1 || true; }
cars=""
cleanup() { lag 0; for c in $cars; do $D eval -t host -m body -a id=$c 'Trains.Delete(Trains.Find(ctx.Args["id"])); return 0;' >/dev/null 2>&1 || true; done; }
trap cleanup EXIT
lag $LAG
T=$(mktemp -d); fails=0
state() { $D eval -t $1 -m body $A -f DevTools/Scenarios/manual-uncouple/state.cs >"$T/$1.json"; }
agree() { # label: compare host and client coupling and wagon position
  node -e '
    const v=f=>{const o=JSON.parse(require("fs").readFileSync(f,"utf8"));const x=Object.values(o.results)[0];if(!x.ok)throw new Error(JSON.stringify(x));return x.result;};
    const h=v(process.argv[1]), c=v(process.argv[2]); const bad=[];
    if (h.coupled!==c.coupled || h.sameSet!==c.sameSet) bad.push("coupled: host "+h.coupled+"/"+h.sameSet+", client "+c.coupled+"/"+c.sameSet);
    const d=Math.hypot(h.wagonAbs.x-c.wagonAbs.x,h.wagonAbs.z-c.wagonAbs.z);
    if (d>1) bad.push("wagon apart "+d.toFixed(2)+" m");
    console.log((bad.length?"FAIL ":"ok ")+process.argv[3]+": coupled "+h.coupled+", gap host "+h.gap+" client "+c.gap+", chain host "+h.chain+" client "+c.chain+", wagon apart "+d.toFixed(2)+" m"+(bad.length?" - "+bad.join("; "):""));' "$T/host.json" "$T/client.json" "$1"; }
for speed in ${SPEEDS//,/ }; do
  s=$($D eval -t host -m coroutine --timeout 90 -f $P/setup.cs)
  L=$(echo "$s" | field loco); W=$(echo "$s" | field wagon); cars="$L $W"
  A="-a loco=$L -a wagon=$W -a locoFront=$(echo "$s" | field locoFront) -a wagonFront=$(echo "$s" | field wagonFront)"
  sleep 2
  $D eval -t $WHO -m coroutine -u "using Multiplayer.Components.Networking;" --timeout 60 $A -a chain=$CHAIN -a hitch=$HITCH -f $P/couple.cs >"$T/couple.json" &
  r=$($D eval -t host -m coroutine --timeout 60 $A -a speed=$speed -a wagonBrake=$WAGON_BRAKE -f $P/ram.cs)
  wait
  echo "== $speed m/s: hit at $(echo "$r" | field hitSpeed) m/s, buffers closed to $(echo "$r" | field minGap) m; $WHO coupled at gap $(field gapAt <"$T/couple.json") via $(field how <"$T/couple.json"), chain $(field fsm <"$T/couple.json") (now $(field coupledNow <"$T/couple.json"))"
  # let the bounce and any roll die out (the peers are read one after the other)
  for i in $(seq 1 12); do sleep 2; state host; [ "$(field speed <"$T/host.json")" = "0" ] && break; done
  sleep 2; state host; state client
  o=$(agree "after impact"); echo "   $o"; case "$o" in FAIL*) fails=$((fails+1));; esac
  $D eval -t host -m coroutine --timeout 60 $A -f $P/pull.cs >/dev/null
  sleep 3; state host; state client
  o=$(agree "after pulling away"); echo "   $o"; case "$o" in FAIL*) fails=$((fails+1));; esac
  for c in $cars; do $D eval -t host -m body -a id=$c 'Trains.Delete(Trains.Find(ctx.Args["id"])); return 0;' >/dev/null 2>&1 || true; done; cars=""
done
[ $fails -eq 0 ] && echo PASS || { echo "FAIL: $fails checks"; exit 1; }
