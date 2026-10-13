#!/usr/bin/env bash
# Uncoupling a moving train (upstream #141): the host drives a loco and two wagons and uncouples
# the last wagon on the move, then shoves back into it (DV does not couple on contact). The client must see
# the wagon roll free without being dragged or snapping, and agree with the host on where it ends
# up and whether it is coupled.
#   SPEED=m/s (default 5)  LAG=client receive lag in ms  N=runs (default 2)
#   RIDE=1: the last car is a second loco with the client riding in its cab (the report's setup)
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/uncouple-moving
SPEED=${SPEED:-5}; LAG=${LAG:-0}; N=${N:-2}; LAST=${LAST:-BoxcarBrown}; [ -n "${RIDE:-}" ] && LAST=LocoDE2
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
lag() { $D eval -t client "Steamworks.SteamNetworkingUtils.FakeRecvPacketLag = $1f" >/dev/null 2>&1 || true; }
cars=""
cleanup() { lag 0; for c in $cars; do $D eval -t host -m body -a id=$c 'Trains.Delete(Trains.Find(ctx.Args["id"])); return 0;' >/dev/null 2>&1 || true; done; }
trap cleanup EXIT
lag $LAG
T=$(mktemp -d); fails=0
compare() { # label
  LAG=$LAG SPEED=$SPEED node -e '
    const v=f=>Object.values(JSON.parse(require("fs").readFileSync(f,"utf8")).results)[0].result;
    const h=v(process.argv[1]), c=v(process.argv[2]); const [label,expect]=process.argv.slice(3); const bad=[];
    // With lag the client sees a change of speed late: allow the distance covered meanwhile.
    // With lag the client learns of an impact late and has to win the distance back.
    const slack=0.5+1.5*(+process.env.LAG/1000)*(+process.env.SPEED);
    const d=Math.hypot(h.final.x-c.final.x,h.final.z-c.final.z);
    if (d>slack) bad.push("peers disagree by "+d.toFixed(2)+" m");
    if (c.worstJump>1.0) bad.push("client snap "+c.worstJump+" m at "+c.worstAt+" s");
    // buffers can bounce a shoved wagon back on both peers; only extra travel on the client is a snap
    if (c.overshoot-h.overshoot>slack) bad.push("client wagon dragged/snapped back "+c.overshoot+" m (host "+h.overshoot+" m)");
    if (bad.length && c.notes && c.notes.length) bad.push("client: "+c.notes.join(" / "));
    console.log((bad.length?"FAIL ":"ok ")+label+": wagon moved "+h.travelled+" m (client "+c.travelled+"), apart "+d.toFixed(2)+" m, client worst step "+c.worstJump+" m"+(bad.length?" - "+bad.join("; "):""));' "$T/h.json" "$T/c.json" "$@"; }
for run in $(seq 1 $N); do
  s=$($D eval -t host -m coroutine --timeout 60 -a last=$LAST -f $P/setup.cs)
  L=$(echo "$s" | field loco); M=$(echo "$s" | field mid); W=$(echo "$s" | field last); LF=$(echo "$s" | field lastFront)
  cars="$L $M $W"; sleep 2
  [ -n "${RIDE:-}" ] && $D eval -t client -m coroutine --timeout 60 -a car=$W 'yield return Trains.Board(Trains.Find(ctx.Args["car"]));' >/dev/null
  $D eval -t client -m coroutine --timeout 40 -a car=$W -a seconds=14 -f $P/track.cs >"$T/c.json" &
  $D eval -t host -m coroutine --timeout 40 -a car=$W -a seconds=14 -f $P/track.cs >"$T/h.json" &
  sleep 0.3
  $D eval -t host -m coroutine --timeout 60 -a loco=$L -a last=$W -a lastFront=$LF -a speed=$SPEED -a at=4 -f $P/drive.cs >/dev/null
  wait
  r=$(compare "run $run uncouple"); echo "   $r"; case "$r" in FAIL*) fails=$((fails+1));; esac
  $D eval -t client -m coroutine --timeout 60 -a car=$W -a seconds=25 -f $P/track.cs >"$T/c.json" &
  $D eval -t host -m coroutine --timeout 60 -a car=$W -a seconds=25 -f $P/track.cs >"$T/h.json" &
  sleep 0.3
  rc=$($D eval -t host -m coroutine --timeout 60 -a loco=$L -a last=$W -f $P/reverse.cs | field coupled)
  wait; sleep 2
  cc=$($D eval -t client -m body -a l=$L -a w=$W 'return new { coupled = Trains.Find(ctx.Args["l"]).trainset == Trains.Find(ctx.Args["w"]).trainset };' | field coupled)
  r=$(compare "run $run shoved back into it (coupled: host $rc, client $cc)"); [ "$rc" = "$cc" ] || r="FAIL $r - coupling differs"
  echo "   $r"; case "$r" in FAIL*) fails=$((fails+1));; esac
  for c in $cars; do $D eval -t host -m body -a id=$c 'Trains.Delete(Trains.Find(ctx.Args["id"])); return 0;' >/dev/null 2>&1 || true; done; cars=""
done
[ $fails -eq 0 ] && echo PASS || { echo "FAIL: $fails checks"; exit 1; }
