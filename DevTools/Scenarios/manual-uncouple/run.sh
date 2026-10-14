#!/usr/bin/env bash
# Manual decoupling by a client (upstream #166): close both angle cocks, part the hoses, loosen
# the screw, lift the chain onto its parking hook. Both peers must see the cars uncoupled and the
# chains parked, and when the host drives the loco away the wagon must stay. Reported to fail a
# third to half of the time, so it repeats.
#   N=runs (default 4)  PAUSE=seconds between steps (default 1)  LAG=client receive lag in ms
#   WAGON=livery (default BoxcarBrown)  MODE=uncouple|recouple: recouple then couples the cars again
#   by hand (hook the chain, tighten, hoses, cocks) and the wagon must follow the loco instead.
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/manual-uncouple
N=${N:-4}; PAUSE=${PAUSE:-1}; LAG=${LAG:-0}; WAGON=${WAGON:-BoxcarBrown}; MODE=${MODE:-uncouple}
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
lag() { $D eval -t client "Steamworks.SteamNetworkingUtils.FakeRecvPacketLag = $1f" >/dev/null 2>&1 || true; }
cars=""
cleanup() { lag 0; for c in $cars; do $D eval -t host -m body -a id=$c 'Trains.Delete(Trains.Find(ctx.Args["id"])); return 0;' >/dev/null 2>&1 || true; done; }
trap cleanup EXIT
lag $LAG
fails=0
for run in $(seq 1 $N); do
  s=$($D eval -t host -m coroutine --timeout 60 -a wagon=$WAGON -f $P/setup.cs)
  L=$(echo "$s" | field loco); W=$(echo "$s" | field wagon)
  A="-a loco=$L -a wagon=$W -a locoFront=$(echo "$s" | field locoFront) -a wagonFront=$(echo "$s" | field wagonFront)"
  cars="$cars $L $W"
  sleep 2
  u=$($D eval -t client -m coroutine --timeout 90 $A -a pause=$PAUSE -f $P/uncouple.cs) || { echo "   run $run: probe failed: $(echo "$u" | grep -E '"error"' | head -1)"; fails=$((fails+1)); continue; }
  if [ "$MODE" = recouple ]; then
    sleep 2
    u=$($D eval -t client -m coroutine --timeout 90 $A -a pause=$PAUSE -f $P/couple.cs) || { echo "   run $run: couple probe failed: $(echo "$u" | grep -E '"error"' | head -1)"; fails=$((fails+1)); continue; }
  fi
  sleep 3
  h=$($D eval -t host -m body $A -f $P/state.cs); c=$($D eval -t client -m body $A -f $P/state.cs)
  # Drive the loco away on the host (no air, so push it) and see whether the wagon follows.
  $D eval -t host -m coroutine --timeout 60 -a l=$L -a w=$W 'var w = Trains.Find(ctx.Args["w"]); w.brakeSystem.SetHandbrakePosition(1f); var l = Trains.Find(ctx.Args["l"]); l.brakeSystem.SetHandbrakePosition(0f); // Away from the wagon: pushing into it shoves it along (or re-couples on contact).
float away = Vector3.Dot(l.transform.forward, w.transform.position - l.transform.position) > 0 ? -3f : 3f;
yield return Trains.Push(l, away, 3f, 1f); l.brakeSystem.SetHandbrakePosition(1f);' >/dev/null
  sleep 2
  h2=$($D eval -t host -m body $A -f $P/state.cs); c2=$($D eval -t client -m body $A -f $P/state.cs)
  r=$(MODE=$MODE node -e '
    const v=s=>Object.values(JSON.parse(s).results)[0].result;
    const [h,c,h2,c2]=process.argv.slice(1).map(v); const bad=[];
    const recouple=process.env.MODE==="recouple";
    const moved=(a,b)=>Math.hypot(a.wagonAbs.x-b.wagonAbs.x,a.wagonAbs.z-b.wagonAbs.z);
    for (const [n,x,y] of [["host",h,h2],["client",c,c2]]) {
      if (recouple) {
        if (!x.coupled||!x.sameSet||!x.hose||x.cocks!=="OO") bad.push(n+" not fully coupled (chain "+x.chain+", hose "+x.hose+", cocks "+x.cocks+")");
        if (moved(x,y)<5) bad.push(n+" wagon stayed behind ("+moved(x,y).toFixed(1)+" m)");
      } else {
        if (x.coupled||x.sameSet) bad.push(n+" still coupled ("+x.chain+")");
        if (!/^Parked\/|\/Parked$/.test(x.chain)) bad.push(n+" chain "+x.chain);
        if (moved(x,y)>0.5) bad.push(n+" wagon followed the loco "+moved(x,y).toFixed(1)+" m");
      }
    }
    console.log(bad.length? "FAIL "+bad.join("; ") : recouple ? "ok (coupled, chain "+h.chain+", wagon followed "+moved(h,h2).toFixed(1)+" m)" : "ok (chains "+h.chain+", loco pulled away "+h2.gap+" m)");' "$h" "$c" "$h2" "$c2")
  echo "   run $run ($L + $W): $r"
  case "$r" in FAIL*) echo "     client steps: $(echo "$u" | field steps)";; esac
  # Clear the pair away: a later pair spawns on the same track.
  for c in $L $W; do $D eval -t host -m body -a id=$c 'Trains.Delete(Trains.Find(ctx.Args["id"])); return 0;' >/dev/null 2>&1 || true; done
  cars=""
  case "$r" in FAIL*) fails=$((fails+1));; esac
done
[ $fails -eq 0 ] && echo PASS || { echo "FAIL: $fails of $N runs"; exit 1; }
