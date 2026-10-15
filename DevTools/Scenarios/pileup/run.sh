#!/usr/bin/env bash
# Pile-up: on K parallel tracks of Steel Mill's B yard, a loco runs into a row of N free wagons
# (from both ends with ENDS=2), all at once, so cars hit each other in chains. When everything has
# stopped on BOTH peers, every car must agree between host and client: same derailed state, same
# coupled neighbours, and the same place. It doesn't judge what the crash should look like, only
# that both games saw the same one.
#   K=4 N=6 SPEED=8 (m/s) GAP=3 (m between wagons) ENDS=1|2 DERAIL=1|0 (0: derailing off, as the
#   difficulty option) LAG=client receive lag in ms
# Exit code 0 = pass. Needs a running pair (dvmp up). Deletes whatever was on the tracks it uses.
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/pileup
K=${K:-4}; N=${N:-6}; SPEED=${SPEED:-8}; GAP=${GAP:-3}; ENDS=${ENDS:-1}; DERAIL=${DERAIL:-1}; LAG=${LAG:-0}
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
lag() { $D eval -t client "Steamworks.SteamNetworkingUtils.FakeRecvPacketLag = $1f" >/dev/null 2>&1 || true; }
# Derailing off is the difficulty option's effect: an unreachable derail stress threshold.
derail() { $D eval -t all "DV.Globals.G.GameParams.DerailStressThreshold = $1; return 0;" >/dev/null 2>&1 || true; }
cars=""
T=$(mktemp -d)
orig=$($D eval -t host 'return DV.Globals.G.GameParams.DerailStressThreshold;' | node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));console.log(Object.values(o.results)[0].result)')
cleanup() {
  lag 0; derail "${orig}f"
  [ -n "$cars" ] && $D eval -t host -m body -a cars="$cars" 'foreach (var id in ctx.Args["cars"].Split(",".ToCharArray())) { var c = Trains.Find(id); if (c != null) Trains.Delete(c); } return 0;' >/dev/null 2>&1 || true
}
trap cleanup EXIT

echo "== both players to Steel Mill"
$D eval -t host -m coroutine --timeout 90 'yield return LocalPlayer.TeleportToGround(7927f, 7338f);' >/dev/null
$D eval -t client -m coroutine --timeout 90 'if (Hands.Held != null) yield return Hands.Drop(); yield return LocalPlayer.TeleportToGround(7932f, 7342f);' >/dev/null
sleep 10   # let the area stream in on both before spawning (a spawn mid-stream misplaces cars)
lag $LAG
[ "$DERAIL" = "0" ] && derail "float.MaxValue"

s=$($D eval -t host -m coroutine --timeout 120 -a k=$K -a n=$N -a gap=$GAP -a ends=$ENDS -f $P/setup.cs)
cars=$(echo "$s" | field cars); rams=$(echo "$s" | field rams)
echo "   $(echo "$cars" | tr ',' '\n' | wc -l | tr -d ' ') cars on $K tracks: $(echo "$s" | field rows | node -e 'console.log(JSON.parse(require("fs").readFileSync(0,"utf8")).map(r=>r.track.replace("[Y]_[SM]_","")).join(" "))')"
sleep 3

if [ "${WATCH:-0}" = "1" ]; then
  # Stand the host 20 m to one side of the first impact and the client 20 m to the other, both
  # facing it, so the two windows (dvmp up --visible) show the same crash from either side.
  first=$(echo "$s" | field rows | node -e 'console.log(JSON.parse(require("fs").readFileSync(0,"utf8"))[0].wagons.split(",")[0])')
  for side in "host 1" "client -1"; do set -- $side
    $D eval -t $1 -m coroutine --timeout 90 -a id=$first -a side=$2 '
      var car = Trains.Find(ctx.Args["id"]); float side = float.Parse(ctx.Args["side"]);
      Vector3 at = Dev.Abs(car.transform.position) + car.transform.right * 20f * side;
      yield return LocalPlayer.TeleportToGround(at.x, at.z);
      Vector3 look = Vector3.ProjectOnPlane(car.transform.position - PlayerManager.PlayerTransform.position, Vector3.up);
      PlayerManager.PlayerTransform.rotation = Quaternion.LookRotation(look);' >/dev/null
  done
  for n in 5 4 3 2 1; do echo "   $n..."; sleep 1; done
fi

echo "== $SPEED m/s into every row at once (ends: $ENDS, derailing: $([ "$DERAIL" = 0 ] && echo off || echo on), lag: ${LAG} ms)"
r=$($D eval -t host -m coroutine --timeout 90 -a cars="$cars" -a rams="$rams" -a speed=$SPEED -f $P/ram.cs)
echo "   hit at $(echo "$r" | field hitSpeeds) m/s; fastest car $(echo "$r" | field maxSpeed) m/s; $(echo "$r" | field derailed) derailed, $(echo "$r" | field coupled) coupled (host)"

# Wait until every car has stopped on both peers, twice in a row. Cars a hard crash sends rolling
# down the yard would take minutes: after BRAKE_AFTER seconds the host puts every handbrake on, as a
# crew catching runaways would. That stops them on the host; clients must then stop where it did.
BRAKE_AFTER=${BRAKE_AFTER:-10}
still=0; braked=0
moving_counts() { node -e '
  const out=[];let total=0;for(const [name,f] of [["host",process.argv[1]],["client",process.argv[2]]]){const o=JSON.parse(require("fs").readFileSync(f,"utf8"));const r=Object.values(o.results)[0].result;let n=0,max=0;for(const k in r) if(r[k]&&r[k].speed>0.05){n++;max=Math.max(max,r[k].speed);}total+=n;out.push(name+" "+n+(n?" (up to "+max.toFixed(2)+" m/s)":""));}console.log(total+" "+out.join(", "))' "$T/host.json" "$T/client.json"; }
for i in $(seq 1 40); do
  sleep 2
  $D eval -t host -a cars="$cars" -f $P/state.cs >"$T/host.json"
  $D eval -t client -a cars="$cars" -f $P/state.cs >"$T/client.json"
  m=$(moving_counts); moving=${m%% *}
  if [ "$moving" = "0" ]; then still=$((still+1)); [ $still -ge 2 ] && break; else still=0; fi
  if [ $braked = 0 ] && [ $((i * 2)) -ge $BRAKE_AFTER ]; then
    echo "   still moving after ${BRAKE_AFTER} s: ${m#* }; host applies every handbrake"
    $D eval -t host -a cars="$cars" 'foreach (var id in ctx.Args["cars"].Split(",".ToCharArray())) { try { Trains.Find(id).brakeSystem.SetHandbrakePosition(1f); } catch { } } return 0;' >/dev/null
    braked=1
  fi
done
[ "$moving" = "0" ] || echo "   warning: still moving after 80 s: ${m#* }"

node -e '
  const v=f=>Object.values(JSON.parse(require("fs").readFileSync(f,"utf8")).results)[0].result;
  const h=v(process.argv[1]), c=v(process.argv[2]); const bad=[]; let worst=["",0], derailed=0; const skipped=[];
  for (const id in h) {
    const a=h[id], b=c[id];
    if (!a || !b) { bad.push(id+": "+(!a?"missing on host":"missing on client")); continue; }
    // A car still moving on the host is not where it will stop; reading host then client gives a
    // gap that is not a desync. Only cars the host has stopped are compared.
    if (a.speed > 0.05) { skipped.push(id+" ("+a.speed.toFixed(2)+" m/s)"); continue; }
    if (a.derailed) derailed++;
    if (a.derailed!==b.derailed) bad.push(id+": derailed host "+a.derailed+", client "+b.derailed);
    if (a.coupled!==b.coupled) bad.push(id+": coupled to host ["+a.coupled+"], client ["+b.coupled+"]");
    const d=Math.hypot(a.x-b.x,a.y-b.y,a.z-b.z), limit=a.derailed?1.0:0.25;
    if (d>worst[1]) worst=[id,d];
    if (d>limit) bad.push(id+": "+d.toFixed(2)+" m apart"+(a.derailed?" (derailed)":""));
  }
  console.log("   "+(Object.keys(h).length-skipped.length)+" cars compared, "+derailed+" derailed; largest gap "+worst[1].toFixed(2)+" m ("+worst[0]+")"+(skipped.length?"; skipped, still moving on the host: "+skipped.join(", "):""));
  for (const b of bad) console.log("   FAIL "+b);
  console.log(bad.length?"FAIL: "+bad.length+" cars disagree":"PASS");
  process.exit(bad.length?1:0);' "$T/host.json" "$T/client.json"
