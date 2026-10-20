#!/usr/bin/env bash
# The client's own freeze while riding a moving train: the client's main thread sleeps (default
# 3.25 s, like loading after a teleport) while the host pushes the train on. Right after it the
# client's train must be put where the host's is (logged "caught up"), not driven after it and
# snapped by a full sync.
#   FREEZE_MS=freeze (default 3250)  SPEED=m/s (default 10)  MAX=worst error 1-4 s after the first frame back, m (default 1)
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/client-freeze
FREEZE_MS=${FREEZE_MS:-3250}; SPEED=${SPEED:-10}; MAX=${MAX:-1}
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
count() { $D logs -t client -l "$1" -c "$2" -n 2000 2>/dev/null | grep -c '"message"' || true; }

loco=$($D eval -t host -m coroutine 'var car = Trains.SpawnNearPlayer("LocoDE2"); for (int i = 0; i < 120; i++) yield return null; ctx.Result = new { id = car.ID };' | field id)
echo "== $loco; client boards"
cleanup() { $D eval -t host -m body "Trains.Delete(Trains.Find(\"$loco\")); return 0;" >/dev/null 2>&1 || true; }
trap cleanup EXIT
$D eval -t client -m coroutine -a loco=$loco 'yield return Trains.Board(Trains.Find(ctx.Args["loco"]));' >/dev/null
caught0=$(count log "$loco: caught up"); snaps0=$(count warning "$loco: full sync")
echo "== host pushes at $SPEED m/s; the client freezes for $FREEZE_MS ms"
$D eval -t host -m coroutine --timeout 120 -a loco=$loco -a speed=$SPEED 'yield return Trains.Push(Trains.Find(ctx.Args["loco"]), float.Parse(ctx.Args["speed"]), 20f);' >/dev/null &
sleep 6
r=$($D eval -t client -m coroutine --timeout 90 -a loco=$loco -a freezeMs=$FREEZE_MS -a seconds=4 -f $P/measure.cs | node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}console.log(JSON.stringify(x.result))')
wait
caught=$(( $(count log "$loco: caught up") - caught0 )); snaps=$(( $(count warning "$loco: full sync") - snaps0 ))
$D logs -t client -l log -c "$loco: caught up" -n 5 2>/dev/null | grep -o '"message": *"[^"]*"' | tail -1 || true
node -e '
const r=JSON.parse(process.argv[1]), max=+process.argv[2], caught=+process.argv[3], snaps=+process.argv[4]; let bad=[];
console.log(`   before: worst ${r.before} m at ${r.speed} m/s; the first frame back took ${r.catchUpFrame} s; after it: first error ${r.firstError} m, under 1 m at ${r.settledAt} s, worst 1-4 s ${r.worstLate} m, biggest step beyond speed 1-4 s ${r.worstStepLate} m`);
console.log(`   errors: ${r.errors.map(e=>`${e.t}s ${e.e}m`).join(", ")}`);
console.log(`   client log: ${caught} catch-up(s), ${snaps} full-sync snap(s) of the loco`);
if (r.speed < 5) bad.push(`the train was at ${r.speed} m/s, not moving`);
if (caught < 1) bad.push("no catch-up logged");
if (snaps > 0) bad.push(`${snaps} full-sync snap(s)`);
if (!(r.worstLate <= max)) bad.push(`error ${r.worstLate} m 1-4 s after the freeze`);
if (r.worstStepLate > 2) bad.push(`stepped ${r.worstStepLate} m beyond its speed 1-4 s after`);
if (bad.length) { console.log("FAIL: "+bad.join(", ")); process.exit(1); } console.log("PASS");' "$r" "$MAX" "$caught" "$snaps"
