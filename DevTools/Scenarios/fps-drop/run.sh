#!/usr/bin/env bash
# Frame-rate drop on a client riding a moving train: the client's frame rate is cut (default to
# 6 FPS) while the host pushes the train; the client's train must stay close to the host's
# during the drop and recover after it.
#   FPS=dropped frame rate (default 6)  SPEED=m/s (default 10)  MAX=worst allowed error in m (default 3)
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/fps-drop
FPS=${FPS:-6}; SPEED=${SPEED:-10}; MAX=${MAX:-3}
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }

loco=$($D eval -t host -m coroutine 'var car = Trains.SpawnNearPlayer("LocoDE2"); for (int i = 0; i < 120; i++) yield return null; ctx.Result = new { id = car.ID };' | field id)
echo "== $loco; client boards"
cleanup() { $D eval -t host -m body "Trains.Delete(Trains.Find(\"$loco\")); return 0;" >/dev/null 2>&1 || true; }
trap cleanup EXIT
$D eval -t client -m coroutine -a loco=$loco 'yield return Trains.Board(Trains.Find(ctx.Args["loco"]));' >/dev/null
echo "== host pushes at $SPEED m/s; client drops to $FPS FPS mid-run"
$D eval -t host -m coroutine --timeout 120 -a loco=$loco -a speed=$SPEED 'yield return Trains.Push(Trains.Find(ctx.Args["loco"]), float.Parse(ctx.Args["speed"]), 26f);' >/dev/null &
sleep 6
r=$($D eval -t client -m coroutine --timeout 90 -a loco=$loco -a fps=$FPS -a seconds=7 -f $P/measure.cs | field phases)
wait
node -e '
const ph=JSON.parse(process.argv[1]), max=+process.argv[2]; let bad=[];
for (const p of ph) { console.log(`   ${p.name.padEnd(6)} ${String(p.fps).padStart(3)} fps: worst ${p.worst} m, mean ${p.mean} m, game/real time ${p.gameTimeRatio}, speed ${p.speed} m/s`);
  if (p.worst > max) bad.push(`${p.name} error ${p.worst} m`); }
if (bad.length) { console.log("FAIL: "+bad.join(", ")); process.exit(1); } console.log("PASS");' "$r" "$MAX"
