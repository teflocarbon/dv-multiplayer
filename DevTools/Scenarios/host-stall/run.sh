#!/usr/bin/env bash
# Host frame-rate collapse (Discord: "host at 2 fps for a minute caused hoses to break and
# couplings to show weird positions", "brake pipes randomly split, draining the MR and applying
# all the wagon brakes"). The host pushes a loco and wagons with a charged brake pipe and then
# stalls at about 2 FPS for a while, and recovers. Neither peer may lose a coupling, hose or cock
# or see the brake pipe drop, and they must agree where the train stops.
#   STALL=seconds (default 30)  SPEED=m/s (default 8)  WAGONS=4  RIDE=1 (client in the loco cab,
#   default) or 0 (client standing by the line)  LAG=client receive lag in ms
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/host-stall
STALL=${STALL:-30}; SPEED=${SPEED:-8}; WAGONS=${WAGONS:-4}; RIDE=${RIDE:-1}; LAG=${LAG:-0}
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
lag() { $D eval -t client "Steamworks.SteamNetworkingUtils.FakeRecvPacketLag = $1f" >/dev/null 2>&1 || true; }
cars=""
cleanup() { lag 0; for c in ${cars//,/ }; do $D eval -t host -m body -a id=$c 'Trains.Delete(Trains.Find(ctx.Args["id"])); return 0;' >/dev/null 2>&1 || true; done; }
trap cleanup EXIT
T=$(mktemp -d)
s=$($D eval -t host -m coroutine --timeout 150 -a wagons=$WAGONS -f $P/setup.cs)
cars=$(echo "$s" | field cars); loco=$(echo "$s" | field loco); last=$(echo "$s" | field last)
echo "== $cars: brake pipe charged to $(echo "$s" | field charged) bar, cylinders at $(echo "$s" | field cylinders) bar after $(echo "$s" | field seconds)s"
[ "$RIDE" = "1" ] && $D eval -t client -m coroutine --timeout 60 -a car=$loco 'yield return Trains.Board(Trains.Find(ctx.Args["car"]));' >/dev/null
lag $LAG
W=$((STALL + 120))
$D eval -t host -m coroutine --timeout $((W + 30)) -a cars=$cars -a seconds=$W -f $P/watch.cs >"$T/hw.json" &
$D eval -t client -m coroutine --timeout $((W + 30)) -a cars=$cars -a seconds=$W -f $P/watch.cs >"$T/cw.json" &
$D eval -t client -m coroutine --timeout $((W + 30)) -a car=$last -a seconds=$W -f DevTools/Scenarios/uncouple-moving/track.cs >"$T/ct.json" &
$D eval -t host -m coroutine --timeout $((W + 30)) -a loco=$loco -a speed=$SPEED 'yield return Trains.Push(Trains.Find(ctx.Args["loco"]), float.Parse(ctx.Args["speed"]), 60f, 4f);' >/dev/null &
# stall once the train is at speed (a minute at most)
$D eval -t host -m coroutine --timeout 90 -a loco=$loco -a speed=$SPEED 'var l = Trains.Find(ctx.Args["loco"]); for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 60f && Mathf.Abs(l.GetForwardSpeed()) < 0.8f * float.Parse(ctx.Args["speed"]);) yield return null; ctx.Result = 1;' >/dev/null
v=$($D eval -t host -a loco=$loco 'Mathf.Abs(Trains.Find("'$loco'").GetForwardSpeed())' 2>/dev/null | node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));console.log(Number(Object.values(o.results)[0].result).toFixed(1))' || echo ?)
echo "== host stalls for ${STALL}s (train at ${v} m/s)"
st=$($D eval -t host -m coroutine --timeout $((STALL + 60)) -a seconds=$STALL -f $P/stall.cs)
echo "   $(echo "$st" | field frames) frames, host game time $(echo "$st" | field gameSeconds)s in $(echo "$st" | field realSeconds)s"
wait
lag 0
# compare once the train has stopped (the two games are read one after the other)
$D eval -t host -m coroutine --timeout 120 -a loco=$loco 'var l = Trains.Find(ctx.Args["loco"]); for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 90f && Mathf.Abs(l.GetForwardSpeed()) > 0.05f;) yield return null; ctx.Result = 1;' >/dev/null
sleep 3
$D eval -t host -m coroutine --timeout 60 -a cars=$cars -a seconds=1 -f $P/watch.cs >"$T/hf.json"
$D eval -t client -m coroutine --timeout 60 -a cars=$cars -a seconds=1 -f $P/watch.cs >"$T/cf.json"
node -e '
const v=f=>{const o=JSON.parse(require("fs").readFileSync(f,"utf8"));const x=Object.values(o.results)[0];if(!x.ok)throw new Error(f+": "+JSON.stringify(x));return x.result;};
const [d]=process.argv.slice(1); const hw=v(d+"/hw.json"), cw=v(d+"/cw.json"), ct=v(d+"/ct.json"), hf=v(d+"/hf.json"), cf=v(d+"/cf.json"); const bad=[];
for (const [who,w] of [["host",hw],["client",cw]]) {
  console.log(`   ${who}: worst ${w.worstUncoupled} uncoupled, ${w.worstHoses} hoses off, ${w.worstCocks} cocks shut; brake pipe min ${w.minPipe} bar, loco main res min ${w.minMainRes} bar`+(w.notes.length?" | "+w.notes.join(" / "):""));
  if (w.worstUncoupled||w.worstHoses||w.worstCocks) bad.push(who+" lost a connection");
  if (w.minPipe<3.5) bad.push(who+" brake pipe fell to "+w.minPipe+" bar");
}
console.log(`   client last wagon: worst step ${ct.worstJump} m at ${ct.worstAt}s`+(ct.notes.length?" | "+ct.notes.join(" / "):""));
const dist=Math.hypot(hf.final.lastAbs.x-cf.final.lastAbs.x,hf.final.lastAbs.z-cf.final.lastAbs.z);
console.log(`   after: last wagon apart ${dist.toFixed(2)} m (host speed ${hf.final.speed}), pipe host ${hf.final.pipe} client ${cf.final.pipe}`);
if (dist>1) bad.push("peers disagree by "+dist.toFixed(2)+" m");
if (ct.worstJump>3) bad.push("client wagon snapped "+ct.worstJump+" m");
if (bad.length){console.log("FAIL: "+bad.join("; "));process.exit(1);} console.log("PASS");' "$T"
