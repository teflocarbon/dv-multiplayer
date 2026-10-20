#!/usr/bin/env bash
# Distance-based train send rates. The host sends a moving trainset to each player every tick
# within the full rate distance (1500 m), at the mid rate (4 Hz) out to the mid rate distance
# (4000 m) and at the far rate (1 Hz) beyond, and tells each player its interval. The host drives a
# DE2 and two boxcars with the throttle while the client stands by the line (near), goes to a
# station 1.7-3.8 km away (mid) and one over 4.5 km away (far), then boards the moving loco (back
# near). In each band the client's samples of the loco must come at about the band's rate, with
# the interval announced, no gap long enough to take the car as lost, no snap and no stall (far
# trains keep moving for dispatch maps); boarding must not jump the train; and the peers must
# agree where it is while far and where it stops. The host's packets per second of
# ClientboundTrainsetPhysicsPacket are printed per band (the PR's before/after numbers).
#   SPEED=m/s (default 5)  NEAR, MID, FAR, BACK = seconds in each band (6, 10, 16, 10)
#   BEFORE=1: raise the host's full rate distance to 50 km first (every tick to everyone, as
#   before distance-based rates) and only report, don't check the rates.
# Exit code 0 = pass. Needs a running pair (dvmp up, or dvmp up --dedicated).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/train-rate
SPEED=${SPEED:-5}; NEAR=${NEAR:-6}; MID=${MID:-10}; FAR=${FAR:-16}; BACK=${BACK:-10}; BEFORE=${BEFORE:-0}
FULL_M=1500; MID_M=4000
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
fail() { echo "FAIL: $*"; exit 1; }
# The host's ClientboundTrainsetPhysicsPacket sends a second to each player over a window: of this
# train, and of every trainset. Saved as $T/sends-<band>.json for the checks at the end.
rate() { $D eval -t host -m class --timeout $(( $2 + 60 )) -a seconds=$2 -a cars=$cars -f $P/sends.cs >"$T/sends-$1.json" 2>&1
  node -e 'const o=JSON.parse(require("fs").readFileSync(process.argv[1],"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.log("? ("+(o.compileError||(x&&x.error)||"no result")+")");process.exit(0)}const r=x.result;console.log(Object.keys(r.allTrainsets).map(k=>`to ${k}: this train ${r.train[k]}/s (${r.trainBytes[k]} B/s), every trainset ${r.allTrainsets[k]}/s (${r.allTrainsetsBytes[k]} B/s)`).join("; ")||"nothing sent")' "$T/sends-$1.json" 2>/dev/null || echo "? (unreadable)"; }
cars=""
settings_restore=""
# Deletes the train in one go and says so if any of it is left.
delete_train() {
  [ -n "$cars" ] || return 0
  left=$($D eval -t host -m coroutine --timeout 60 -a cars=$cars 'var ids = ctx.Args["cars"].Split((char)44); foreach (var c in CarSpawner.Instance.AllCars.Where(c => c != null && ids.Contains(c.ID)).ToList()) CarSpawner.Instance.DeleteCar(c); for (int i = 0; i < 30; i++) yield return null; ctx.Result = string.Join(",", CarSpawner.Instance.AllCars.Where(c => c != null && ids.Contains(c.ID)).Select(c => c.ID));' 2>/dev/null | node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];console.log(x&&x.ok?x.result:"cleanup failed: "+(o.compileError||JSON.stringify(x)))' 2>/dev/null || echo "cleanup failed")
  [ -z "$left" ] || echo "WARNING: train not cleaned up: $left"
}
cleanup() {
  [ -z "$settings_restore" ] || $D eval -t host -m body -a v=$settings_restore 'Multiplayer.Multiplayer.Settings.TrainFullRateDistance = float.Parse(ctx.Args["v"], System.Globalization.CultureInfo.InvariantCulture); return 0;' >/dev/null 2>&1 || true
  delete_train
}
trap cleanup EXIT
T=$(mktemp -d)

if [ "$BEFORE" = "1" ]; then
  settings_restore=$($D eval -t host -m body 'var s = Multiplayer.Multiplayer.Settings; var old = s.TrainFullRateDistance; s.TrainFullRateDistance = 50000f; return new { old };' | field old)
  echo "== BEFORE: host full rate distance raised to 50 km (was $settings_restore m)"
fi

# Drive time: the bands, the client's two trips and boarding (~25 s), and getting up to speed.
HOLD=$(( NEAR + MID + FAR + BACK + 30 ))
ROOM=$(( SPEED * (HOLD + 20) + 60 ))
dedicated=$($D eval -t host -m body 'return new { d = Multiplayer.Shared.Session.NetworkLifecycle.Instance.IsDedicatedHost };' | field d)
# The train doesn't always move: on Road 36 it never did, on either host kind (batches 13 and
# q4), with the engine running, full throttle and brakes off (DV's pressures are absolute: a
# 1.0 bar cylinder is released). Then its traction is printed and it's set up again elsewhere.
exclude=""
for attempt in 1 2; do
  s=$($D eval -t host -m coroutine --timeout 200 -a wagons=2 -a room=$ROOM ${exclude:+-a exclude=$exclude} -f DevTools/Scenarios/host-stall/setup.cs)
  cars=$(echo "$s" | field cars); loco=$(echo "$s" | field loco); dir=$(echo "$s" | field dir); track=$(echo "$s" | field track)
  echo "== $cars on $track, $(echo "$s" | field clear) m clear; the host drives it at ${SPEED} m/s"
  # On a player host, the host's player stands beside the line (they aren't a recipient: the
  # rates are the client's).
  if [ "$dedicated" != "true" ]; then
    $D eval -t host -m coroutine --timeout 90 -a loco=$loco 'var l = Trains.Find(ctx.Args["loco"]); var a = Dev.Abs(l.transform.position + l.transform.right * 15f); yield return LocalPlayer.TeleportToGround(a.x, a.z);' >/dev/null || fail "the host's player couldn't go to the line"
  fi
  # Drive with the reverser towards the clear side, then hold until the run is done and brake.
  $D eval -t host -m coroutine --timeout $(( HOLD + 120 )) -a loco=$loco -a dir=$dir -a speed=$SPEED -a hold=$HOLD \
    'var l = Trains.Find(ctx.Args["loco"]); var cars = l.trainset.cars.ToList(); l.SimController.controlsOverrider.Reverser?.Set(int.Parse(ctx.Args["dir"]) > 0 ? 1f : 0f); yield return Trains.Drive(l, float.Parse(ctx.Args["speed"]), float.Parse(ctx.Args["hold"]), float.Parse(ctx.Args["hold"]) + 90f); foreach (var c in cars) c.brakeSystem.SetHandbrakePosition(1f); ctx.Result = new { v = Math.Round(l.GetForwardSpeed(), 2) };' >"$T/drive.json" 2>&1 &
  DRIVE=$!
  v=$($D eval -t host -m coroutine --timeout 90 -a loco=$loco -a speed=$SPEED 'var l = Trains.Find(ctx.Args["loco"]); for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 60f && Mathf.Abs(l.GetForwardSpeed()) < 0.85f * float.Parse(ctx.Args["speed"]);) yield return null; ctx.Result = new { v = Math.Round(Mathf.Abs(l.GetForwardSpeed()), 1) };' | field v)
  awk -v v="$v" -v s="$SPEED" 'BEGIN { exit !(v >= 0.85 * s) }' && break
  echo "   $loco on $track didn't get up to speed (${v} m/s):"
  echo "   loco: $($D eval -t host -m body -a loco=$loco 'var l = Trains.Find(ctx.Args["loco"]); var o = l.SimController.controlsOverrider; var ports = l.SimController.simFlow.fullPortIdToPort.Where(p => new[] { "TRACTION", "AMPS", "RPM", "POWER", "WHEEL", "SLIP", "SAND", "REVERSER", "THROTTLE" }.Any(k => p.Key.ToUpperInvariant().Contains(k))).Take(16).ToDictionary(p => p.Key, p => Math.Round(p.Value.Value, 2)); return new { v = Math.Round(l.GetForwardSpeed(), 2), throttle = o.Throttle?.Value, reverser = o.Reverser?.Value, independent = o.IndependentBrake?.Value, train = o.Brake?.Value, handbrake = l.brakeSystem.handbrakePosition, cylinder = Math.Round(l.brakeSystem.brakeCylinderPressure, 2), engine = o.EngineOnReader?.IsOn, l.derailed, ports };' 2>&1 | tr -d '\r\n' | head -c 1500)"
  echo "   drive: $(tr -d '\r\n' <"$T/drive.json" | head -c 600)"
  [ "$attempt" = 2 ] && fail "the train never got up to speed (${v} m/s)"
  delete_train
  wait $DRIVE 2>/dev/null || true
  cars=""
  exclude="${exclude:+$exclude,}$track"
done

W=$(( NEAR + MID + FAR + BACK + 45 ))
$D eval -t client -m coroutine --timeout $(( W + 30 )) -a loco=$loco -a cars=$cars -a seconds=$W -a full=$FULL_M -a mid=$MID_M -a speed=$SPEED -f $P/watch.cs >"$T/watch.json" &
WATCH=$!

# A station the given distance range from the client's copy of the train, or nothing.
station() { $D eval -t client -m body -a lo=$1 -a hi=$2 -a loco=$loco 'var l = Trains.Find(ctx.Args["loco"]).transform.position; float lo = float.Parse(ctx.Args["lo"]), hi = float.Parse(ctx.Args["hi"]); var st = StationController.allStations.Select(x => new { x, d = Vector3.Distance(x.transform.position, l) }).Where(x => x.d > lo && x.d < hi).OrderBy(x => Math.Abs(x.d - (lo + hi) / 2)).FirstOrDefault(); if (st == null) return new { none = true, x = 0f, z = 0f, d = 0.0, yard = "" }; var a = Dev.Abs(st.x.transform.position); return new { none = false, x = a.x, z = a.z, d = Math.Round(st.d), yard = st.x.stationInfo.YardID };'; }
go() { $D eval -t client -m coroutine --timeout 120 -a x=$1 -a z=$2 'yield return LocalPlayer.TeleportToGround(float.Parse(ctx.Args["x"], System.Globalization.CultureInfo.InvariantCulture), float.Parse(ctx.Args["z"], System.Globalization.CultureInfo.InvariantCulture));' >/dev/null; }
# Host and client's loco at once, the host's projected to the client's tick: metres apart.
apart() { $D eval -t all -m body -a loco=$loco 'var l = Trains.Find(ctx.Args["loco"]); var p = Dev.Abs(l.transform.position); var f = l.transform.forward; return new { host = Multiplayer.Shared.Session.NetworkLifecycle.Instance.IsHost(), tick = Multiplayer.Shared.Session.NetworkLifecycle.Instance.Tick, x = p.x, y = p.y, z = p.z, v = l.GetForwardSpeed(), fx = f.x, fy = f.y, fz = f.z };' |
  node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const r=Object.values(o.results||{}).filter(x=>x.ok).map(x=>x.result);const h=r.find(x=>x.host),c=r.find(x=>!x.host);if(!h||!c){console.log("?");process.exit(0)}const dt=(c.tick-h.tick)/24;const q=[h.x+h.fx*h.v*dt,h.y+h.fy*h.v*dt,h.z+h.fz*h.v*dt];console.log(Math.hypot(q[0]-c.x,q[1]-c.y,q[2]-c.z).toFixed(2))'; }

echo "== near: client by the line"
echo "   host sends $(rate near $NEAR)"

m=$(station 1700 3800)
if [ "$(echo "$m" | field none)" = "false" ]; then
  echo "== mid: client goes to $(echo "$m" | field yard), $(echo "$m" | field d) m from the train"
  go $(echo "$m" | field x) $(echo "$m" | field z)
  echo "   host sends $(rate mid $MID)"
else
  echo "== mid: no station 1.7-3.8 km from the train; skipped"
fi

f=$(station 4500 20000)
[ "$(echo "$f" | field none)" = "false" ] || fail "no station over 4.5 km from the train"
echo "== far: client goes to $(echo "$f" | field yard), $(echo "$f" | field d) m from the train"
go $(echo "$f" | field x) $(echo "$f" | field z)
sleep 3
a1=$(apart); half=$(( (FAR - 3) / 2 ))
echo "   host sends $(rate far $half)"
a2=$(apart)
sleep $(( FAR - 3 - half ))
a3=$(apart)
echo "   host and client trains apart (host projected to the client's tick): $a1 / $a2 / $a3 m"

echo "== back near: client boards the moving loco"
$D eval -t client -m coroutine --timeout 90 -a loco=$loco 'yield return Trains.Board(Trains.Find(ctx.Args["loco"]));' >/dev/null || fail "the client couldn't board the moving loco"
echo "   host sends $(rate back $BACK)"

wait $WATCH || true
wait
grep -q '"ok": true' "$T/drive.json" || fail "the drive didn't finish: $(grep -E '"error"' "$T/drive.json" | head -1)"
# Compare where it stopped (read one after the other once stopped).
$D eval -t host -m coroutine --timeout 60 -a loco=$loco 'var l = Trains.Find(ctx.Args["loco"]); for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 30f && Mathf.Abs(l.GetForwardSpeed()) > 0.05f;) yield return null; yield return new WaitForSeconds(3); ctx.Result = Dev.Abs(l.transform.position);' >"$T/hs.json"
$D eval -t client -m body -a loco=$loco 'return Dev.Abs(Trains.Find(ctx.Args["loco"]).transform.position);' >"$T/cs.json"

node -e '
const fs=require("fs");
const v=f=>{const o=JSON.parse(fs.readFileSync(f,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok)throw new Error(f+": "+(o.compileError||JSON.stringify(x)));return x.result;};
const sends=b=>{try{return v(d+"/sends-"+b+".json");}catch(e){return null;}};
const [d,before,speed,a1,a2,a3]=process.argv.slice(1); const w=v(d+"/watch.json"), hs=v(d+"/hs.json"), cs=v(d+"/cs.json"); const bad=[], warn=[];
// Client samples counted per frame: several arriving in one frame count once, so near is at most the fps.
const want={near:{interval:1,min:12,max:30},mid:{interval:6,min:2,max:6.5},far:{interval:24,min:0.5,max:2.5}};
// The host decides the rate: this train to the client per band, against the tick on the host:
// its handlers run at most once a frame, so "every tick" is at most its fps (reliable track
// changes and stops come on top).
const hostWant=(b,r)=>{const t=r.ticksPerSecond||24, calls=Math.min(t,r.fps||24);
  return b==="near"?{min:0.75*calls,max:t*1.1}:b==="mid"?{min:0.7*t/6,max:1.75*t/6}:{min:0.5*t/24,max:2.5*t/24};};
for (const b of ["near","mid","far"]) {
  const s=w.bands[b]; if(!s){console.log(`   ${b}: not visited`); if(b!=="mid") bad.push("never "+b); continue;}
  console.log(`   ${b} (${s.minDistance}-${s.maxDistance} m, ${s.seconds} s, ${s.fps} fps, game time at ${s.gameRate}x): ${s.samplesPerSecond} samples/s, interval ${s.intervalTicks} ticks, worst gap ${s.worstGap} s, ${s.staleGaps} lost, worst step ${s.worstStep} m at ${s.worstStepAt}s, ${s.freezeSnaps} catch-ups after this game froze (worst ${s.worstFreezeSnap} m), slowest second ${s.slowestSecond} m/s (real ${s.slowestRealSecond})`);
  if (s.staleGaps>0) bad.push(`${b}: ${s.staleGaps} gaps long enough to take the car as lost`);
  if (s.worstStep>0.5) {
    // Near is sent every tick as before distance-based rates: a snap there before the client
    // first left is the open intermittent client snap (#44), reported with what led up to it.
    if (b==="near" && w.firstLeftNear>=0 && s.worstStepAt<w.firstLeftNear) warn.push(`near: the train snapped ${s.worstStep} m at ${s.worstStepAt}s, before the client first left (#44; every tick, as before this change)`);
    else bad.push(`${b}: the train snapped ${s.worstStep} m`);
  }
  if (b!=="near" && s.slowestSecond < 0.5*speed) bad.push(`${b}: the train stalled (slowest second ${s.slowestSecond} m/s)`);
  if (before!=="1") {
    const min=b==="near"?Math.min(want.near.min,0.75*s.fps):want[b].min;
    if (s.intervalTicks!==want[b].interval) bad.push(`${b}: interval ${s.intervalTicks} ticks, not ${want[b].interval}`);
    if (s.samplesPerSecond<min||s.samplesPerSecond>want[b].max) bad.push(`${b}: ${s.samplesPerSecond} samples/s, not ${min.toFixed(1)}-${want[b].max} (${s.fps} fps)`);
    const r=sends(b);
    if (r) { const rates=Object.values(r.train); const t=rates.length?Math.max(...rates):0; const h=hostWant(b,r);
      console.log(`   ${b}: the host sent this train ${t}/s at ${r.fps} fps, ${r.ticksPerSecond} ticks/s`);
      if (t<h.min||t>h.max) bad.push(`${b}: the host sent this train ${t}/s, not ${h.min.toFixed(1)}-${h.max.toFixed(1)} (${r.fps} fps, ${r.ticksPerSecond} ticks/s)`); }
    else bad.push(`${b}: no host send count`);
  }
}
console.log(`   boarding: worst step in the 8 s back near ${w.approachWorst} m`+(w.notes.length?" | "+w.notes.join(" / "):""));
if (w.approachWorst>0.5) bad.push("the train jumped "+w.approachWorst+" m when the client came back near");
for (const a of [a1,a2,a3]) if (a!=="?" && +a>3) bad.push("far: host and client trains "+a+" m apart");
const stop=Math.hypot(hs.x-cs.x,hs.y-cs.y,hs.z-cs.z);
console.log(`   stopped ${stop.toFixed(2)} m apart`);
if (stop>1) bad.push("peers disagree where it stopped by "+stop.toFixed(2)+" m");
for (const x of warn) console.log("   WARNING: "+x);
if (bad.length){console.log("FAIL: "+bad.join("; "));process.exit(1);} console.log("PASS");' "$T" "$BEFORE" "$SPEED" "$a1" "$a2" "$a3"

