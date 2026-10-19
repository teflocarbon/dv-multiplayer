#!/usr/bin/env bash
# Coupler desync (#64): the client has a loco and wagon coupled that the host has apart, then the
# host pulls the loco away. The client steers each car to the host's position against a joint the
# host doesn't know about; when that joint couldn't break, the solver fought itself until the
# cars went wild. Pass: the client's cars never move or spin far beyond the host's, and both
# peers end up agreeing (uncoupled, cars where the host has them).
#   LAG=client receive lag in ms  WATCH=seconds to watch (default 20)
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/coupler-desync
LAG=${LAG:-0}; WATCH=${WATCH:-20}
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
lag() { $D eval -t client "Steamworks.SteamNetworkingUtils.FakeRecvPacketLag = $1f" >/dev/null 2>&1 || true; }
breaks() { $D logs -t $1 -l error -c "Coupler broke" -n 1000 2>/dev/null | grep -c '"message"' || true; }
cars=""
cleanup() { lag 0; for c in $cars; do $D eval -t host -m body -a id=$c 'Trains.Delete(Trains.Find(ctx.Args["id"])); return 0;' >/dev/null 2>&1 || true; done; }
trap cleanup EXIT
T=$(mktemp -d)

s=$($D eval -t host -m coroutine --timeout 90 -a gap=0.4 -f DevTools/Scenarios/couple-impact/setup.cs)
L=$(echo "$s" | field loco); W=$(echo "$s" | field wagon); cars="$L $W"
A="-a loco=$L -a wagon=$W -a locoFront=$(echo "$s" | field locoFront) -a wagonFront=$(echo "$s" | field wagonFront)"
sleep 3
lag $LAG
hostBreaks=$(breaks host); clientBreaks=$(breaks client)
$D eval -t host -m body $A -f DevTools/Scenarios/manual-uncouple/state.cs >"$T/host.json"
[ "$(field coupled <"$T/host.json")" = "false" ] || { echo "FAIL: setup left the cars coupled on the host"; exit 1; }
d=$($D eval -t client -m coroutine --timeout 30 -u "using Multiplayer.Client.Session; using Multiplayer.Server.Coverage; using Multiplayer.Server.Dedicated; using Multiplayer.Shared.Session;" $A -f $P/desync.cs)
echo "== client coupled on its own: $(echo "$d" | field coupled) (gap $(field gap <"$T/host.json") m on the host)"
[ "$(echo "$d" | field coupled)" = "true" ] || { echo "FAIL: couldn't couple on the client"; exit 1; }

$D eval -t client -m coroutine --timeout $((WATCH + 30)) $A -a seconds=$WATCH -f $P/watch.cs >"$T/cw.json" &
$D eval -t host -m coroutine --timeout $((WATCH + 30)) $A -a seconds=$WATCH -f $P/watch.cs >"$T/hw.json" &
$D eval -t host -m coroutine --timeout 60 $A -f DevTools/Scenarios/couple-impact/pull.cs >/dev/null
wait
lag 0
sleep 3
$D eval -t host -m body $A -f DevTools/Scenarios/manual-uncouple/state.cs >"$T/host.json"
$D eval -t client -m body $A -f DevTools/Scenarios/manual-uncouple/state.cs >"$T/client.json"
echo "   coupler breaks logged: host $(( $(breaks host) - hostBreaks )), client $(( $(breaks client) - clientBreaks ))"
node -e '
  const v=f=>{const o=JSON.parse(require("fs").readFileSync(f,"utf8"));const x=Object.values(o.results)[0];if(o.compileError||!x.ok)throw new Error(o.compileError||JSON.stringify(x));return x.result;};
  const [d]=process.argv.slice(1); const hw=v(d+"/hw.json"), cw=v(d+"/cw.json"), h=v(d+"/host.json"), c=v(d+"/client.json"); const bad=[];
  console.log("   host:   max speed "+hw.maxSpeed+" m/s, spin "+hw.maxSpin+" deg/s, worst step "+hw.worstJump+" m");
  console.log("   client: max speed "+cw.maxSpeed+" m/s ("+cw.maxSpeedCar+"), spin "+cw.maxSpin+" deg/s, worst step "+cw.worstJump+" m, couplers up to "+cw.maxGap+" m apart");
  for (const e of cw.events) console.log("     "+e);
  if (!cw.coupledAtStart) { console.log("FAIL: the client was no longer coupled when the host pulled away, so nothing was tested"); process.exit(1); }
  if (cw.maxSpeed > hw.maxSpeed + 3) bad.push("client cars reached "+cw.maxSpeed+" m/s, host "+hw.maxSpeed);
  if (cw.maxSpin > hw.maxSpin + 45) bad.push("client cars spun at "+cw.maxSpin+" deg/s");
  if (cw.worstJump > 0.5) bad.push("client car jumped "+cw.worstJump+" m in one step");
  if (cw.derailed && !hw.derailed) bad.push("client derailed, host did not");
  if (h.coupled!==c.coupled || h.sameSet!==c.sameSet) bad.push("coupled: host "+h.coupled+"/"+h.sameSet+", client "+c.coupled+"/"+c.sameSet);
  const apart=Math.hypot(h.wagonAbs.x-c.wagonAbs.x,h.wagonAbs.z-c.wagonAbs.z);
  if (apart>1) bad.push("wagon "+apart.toFixed(2)+" m apart");
  console.log("   end: coupled host "+h.coupled+" client "+c.coupled+", wagon "+apart.toFixed(2)+" m apart");
  console.log(bad.length ? "FAIL: "+bad.join("; ") : "PASS"); process.exit(bad.length?1:0);' "$T"
