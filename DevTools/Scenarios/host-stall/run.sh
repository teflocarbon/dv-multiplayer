#!/usr/bin/env bash
# Host frame-rate collapse (Discord: "host at 2 fps for a minute caused hoses to break and
# couplings to show weird positions", "brake pipes randomly split, draining the MR and applying
# all the wagon brakes"). The host pushes a loco and wagons with a charged brake pipe and then
# stalls at about 2 FPS for a while, and recovers. Neither peer may lose a coupling, hose or cock
# or see the brake pipe drop, and they must agree where the train stops.
#   STALL=seconds (default 30)  SPEED=m/s (default 8)  WAGONS=4  RIDE=1 (client in the loco cab,
#   default) or 0 (client standing by the line)  LAG=client receive lag in ms  HOLD=seconds of game
#   time at speed (default 15). The train is spawned with enough clear track on the side it's
#   pushed to, so it never runs into anything.
# Only breakage fails. With the host in trouble a moving train's cars on the client snap, during
# the stall and after it (trains slow down to avoid rubber-banding), so the client's jumps are
# printed as notes, split into during and after the stall, and never fail the run.
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/host-stall
STALL=${STALL:-30}; SPEED=${SPEED:-8}; WAGONS=${WAGONS:-4}; RIDE=${RIDE:-1}; LAG=${LAG:-0}; HOLD=${HOLD:-15}
# clear track needed: the push ramps up over 4 s, holds and ramps down over 4 s, plus a margin.
# Then the handbrakes go on, as a driver would: unbraked cars roll away on the slightest grade.
ROOM=$(( SPEED * (HOLD + 4) + 60 ))
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
lag() { $D eval -t client "Steamworks.SteamNetworkingUtils.FakeRecvPacketLag = $1f" >/dev/null 2>&1 || true; }
cars=""
# Deletes the train in one go and says so if any of it is left: a train left behind fouls the
# next run's track, and the save-writing runs after this one would save it into the world.
cleanup() {
  lag 0; [ -n "$cars" ] || return 0
  left=$($D eval -t host -m coroutine --timeout 60 -a cars=$cars 'var ids = ctx.Args["cars"].Split((char)44); foreach (var c in CarSpawner.Instance.AllCars.Where(c => c != null && ids.Contains(c.ID)).ToList()) CarSpawner.Instance.DeleteCar(c); for (int i = 0; i < 30; i++) yield return null; ctx.Result = string.Join(",", CarSpawner.Instance.AllCars.Where(c => c != null && ids.Contains(c.ID)).Select(c => c.ID));' 2>/dev/null | node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];console.log(x&&x.ok?x.result:"cleanup failed: "+(o.compileError||JSON.stringify(x)))' 2>/dev/null || echo "cleanup failed")
  [ -z "$left" ] || echo "WARNING: train not cleaned up: $left"
}
trap cleanup EXIT
T=$(mktemp -d)
s=$($D eval -t host -m coroutine --timeout 150 -a wagons=$WAGONS -a room=$ROOM -f $P/setup.cs)
cars=$(echo "$s" | field cars); loco=$(echo "$s" | field loco); last=$(echo "$s" | field last); dir=$(echo "$s" | field dir)
echo "== $cars on $(echo "$s" | field track), $(echo "$s" | field clear) m clear: brake pipe charged to $(echo "$s" | field charged) bar, cylinders at $(echo "$s" | field cylinders) bar after $(echo "$s" | field seconds)s"
[ "$RIDE" = "1" ] && $D eval -t client -m coroutine --timeout 60 -a car=$loco 'yield return Trains.Board(Trains.Find(ctx.Args["car"]));' >/dev/null
lag $LAG
W=$((STALL + 120))
$D eval -t host -m coroutine --timeout $((W + 30)) -a cars=$cars -a seconds=$W -f $P/watch.cs >"$T/hw.json" &
$D eval -t client -m coroutine --timeout $((W + 30)) -a cars=$cars -a seconds=$W -f $P/watch.cs >"$T/cw.json" &
$D eval -t client -m coroutine --timeout $((W + 30)) -a car=$last -a seconds=$W -f $P/track.cs >"$T/ct.json" &
$D eval -t host -m coroutine --timeout $((W + 30)) -a loco=$loco -a speed=$((dir * SPEED)) -a hold=$HOLD 'yield return Trains.Push(Trains.Find(ctx.Args["loco"]), float.Parse(ctx.Args["speed"]), float.Parse(ctx.Args["hold"]), 4f); foreach (var c in Trains.Find(ctx.Args["loco"]).trainset.cars) c.brakeSystem.SetHandbrakePosition(1f); ctx.Result = 1;' >"$T/push.json" &
# stall once the train is at speed (half a minute at most)
v=$($D eval -t host -m coroutine --timeout 60 -a loco=$loco -a speed=$SPEED 'var l = Trains.Find(ctx.Args["loco"]); for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 30f && Mathf.Abs(l.GetForwardSpeed()) < 0.8f * float.Parse(ctx.Args["speed"]);) yield return null; ctx.Result = new { v = Math.Round(Mathf.Abs(l.GetForwardSpeed()), 1) };' | field v)
awk -v v="$v" -v s="$SPEED" 'BEGIN { exit !(v >= 0.8 * s) }' || { echo "FAIL: the train never got up to speed (${v} m/s)"; exit 1; }
echo "== host stalls for ${STALL}s (train at ${v} m/s)"
st=$($D eval -t host -m coroutine --timeout $((STALL + 60)) -a seconds=$STALL -f $P/stall.cs); echo "$st" >"$T/stall.json"
echo "   $(echo "$st" | field frames) frames, host game time $(echo "$st" | field gameSeconds)s in $(echo "$st" | field realSeconds)s"
wait
lag 0
grep -q '"ok": true' "$T/push.json" || { echo "FAIL: the push didn't finish: $(grep -E '"error"' "$T/push.json" | head -1)"; exit 1; }
# compare once the train has stopped (the two games are read one after the other)
v=$($D eval -t host -m coroutine --timeout 60 -a loco=$loco 'var l = Trains.Find(ctx.Args["loco"]); for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 30f && Mathf.Abs(l.GetForwardSpeed()) > 0.05f;) yield return null; ctx.Result = new { v = Math.Round(Mathf.Abs(l.GetForwardSpeed()), 2) };' | field v)
awk -v v="$v" 'BEGIN { exit !(v <= 0.05) }' || { echo "FAIL: the host's train didn't stop (still at ${v} m/s)"; exit 1; }
sleep 3
$D eval -t host -m coroutine --timeout 60 -a cars=$cars -a seconds=1 -f $P/watch.cs >"$T/hf.json"
$D eval -t client -m coroutine --timeout 60 -a cars=$cars -a seconds=1 -f $P/watch.cs >"$T/cf.json"
node -e '
const v=f=>{const o=JSON.parse(require("fs").readFileSync(f,"utf8"));const x=Object.values(o.results)[0];if(!x.ok)throw new Error(f+": "+JSON.stringify(x));return x.result;};
const [d]=process.argv.slice(1); const hw=v(d+"/hw.json"), cw=v(d+"/cw.json"), ct=v(d+"/ct.json"), hf=v(d+"/hf.json"), cf=v(d+"/cf.json"), st=v(d+"/stall.json"); const bad=[];
for (const [who,w] of [["host",hw],["client",cw]]) {
  console.log(`   ${who}: worst ${w.worstUncoupled} uncoupled, ${w.worstHoses} hoses off, ${w.worstCocks} cocks shut; brake pipe min ${w.minPipe} bar, loco main res min ${w.minMainRes} bar`+(w.notes.length?" | "+w.notes.join(" / "):""));
  if (w.worstUncoupled||w.worstHoses||w.worstCocks) bad.push(who+" lost a connection");
  if (w.minPipe<3.5) bad.push(who+" brake pipe fell to "+w.minPipe+" bar");
}
// Snaps are expected with the host in trouble, during the stall and after it: noted, never failed.
const describe=j=>`${j.t}s jump ${j.jump} m: ${j.note}`;
const before=ct.jumps.filter(j=>j.utc<st.startUtc), inStall=ct.jumps.filter(j=>j.utc>=st.startUtc&&j.utc<=st.endUtc), after=ct.jumps.filter(j=>j.utc>st.endUtc);
console.log(`   client last wagon: worst step ${ct.worstJump} m at ${ct.worstAt}s (noted, not failed)`);
for (const [when,js] of [["before the stall",before],["during the stall",inStall],["after the stall",after]])
  if (js.length) console.log(`   ${when}: `+js.slice(0,5).map(describe).join(" / "));
const dist=Math.hypot(hf.final.lastAbs.x-cf.final.lastAbs.x,hf.final.lastAbs.z-cf.final.lastAbs.z);
console.log(`   after: last wagon apart ${dist.toFixed(2)} m (host speed ${hf.final.speed}), pipe host ${hf.final.pipe} client ${cf.final.pipe}`);
if (dist>1) bad.push("peers disagree by "+dist.toFixed(2)+" m");
if (bad.length){console.log("FAIL: "+bad.join("; "));process.exit(1);} console.log("PASS");' "$T"
