#!/usr/bin/env bash
# A derailed car dragged at speed between on-rail cars (playtest 2026-10-19: "carriages juddering like
# crazy"). The host spawns a loco and two wagons, derails the middle one and drags the train by the
# loco. On the client the derailed car must follow as smoothly as on the host: the gap between it and
# the loco may change no faster than RATIO times the host's (RMS, with a floor of FLOOR m/s), and no
# coupling may break on either game. Before the fix the client aimed the car ~0.3 m behind its
# neighbours and overwrote its velocity on every sample, jolting it against its couplers 24 times a second.
#   SPEED=6 (m/s, ~22 km/h)  DRAG_SECONDS=15  RATIO=3  FLOOR=0.3
# Exit code 0 = pass. Needs a running pair (dvmp up); a dedicated host works too.
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
# A failed dvmp call prints its output instead of the run stopping silently under set -e.
dvmp_bin=$D; D=dvmp_checked
dvmp_checked() { local out; if ! out=$("$dvmp_bin" "$@"); then echo "FAIL: dvmp ${*:1:3} failed: ${out:-no output}" >&2; return 1; fi; printf '%s\n' "$out"; }
P=DevTools/Scenarios/derail-drag
SPEED=${SPEED:-6}; SECONDS_DRAG=${DRAG_SECONDS:-15}; RATIO=${RATIO:-3}; FLOOR=${FLOOR:-0.3}
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
fail() { echo "FAIL: $*"; exit 1; }

s=$($D eval -t host -m coroutine --timeout 60 -a last=FlatbedEmpty -f DevTools/Scenarios/uncouple-moving/setup.cs)
L=$(echo "$s" | field loco); M=$(echo "$s" | field mid); W=$(echo "$s" | field last)
cleanup() { for c in $L $M $W; do "$dvmp_bin" eval -t host -m body -a id=$c 'Trains.Delete(Trains.Find(ctx.Args["id"])); return 0;' >/dev/null 2>&1 || true; done; }
trap cleanup EXIT
sleep 3
echo "== host derails $M (between $L and $W)"
d=$($D eval -t host -m body -a car=$M 'var car = Trains.Find(ctx.Args["car"]); car.Derail(); return new { derailed = car.derailed };')
[ "$(echo "$d" | field derailed)" = "true" ] || fail "the test derail didn't happen"
sleep 3
c=$($D eval -t client -m body -a car=$M 'return new { derailed = Trains.Find(ctx.Args["car"]).derailed };')
[ "$(echo "$c" | field derailed)" = "true" ] || fail "the derail didn't reach the client"

echo "== host drags the train at $SPEED m/s for $SECONDS_DRAG s; both games watch the derailed car's coupling"
$D eval -t host -m coroutine --timeout $((SECONDS_DRAG + 30)) -a loco=$L -a others=$M,$W -a speed=$SPEED -a seconds=$SECONDS_DRAG -f $P/drag.cs >/dev/null &
drag=$!
sleep 1
watch() { $D eval -t $1 -m coroutine --timeout $((SECONDS_DRAG + 30)) -a loco=$L -a car=$M -a seconds=$((SECONDS_DRAG - 2)) -a settle=4 -f $P/watch.cs; }
watch client > /tmp/derail-drag-client.$$ &
cw=$!
h=$(watch host)
wait $cw; c=$(cat /tmp/derail-drag-client.$$); rm -f /tmp/derail-drag-client.$$
wait $drag || fail "the host's drag failed"
for side in host client; do
  r=$([ $side = host ] && echo "$h" || echo "$c")
  echo "   $side: gap change RMS $(echo "$r" | field rms) m/s, worst $(echo "$r" | field worst) m/s, gap range $(echo "$r" | field gapRange) m, velocity change RMS loco $(echo "$r" | field locoAccel) / car $(echo "$r" | field carAccel) m/s2, $(echo "$r" | field speedKmh) km/h, derailed $(echo "$r" | field derailed), coupled $(echo "$r" | field coupled), $(echo "$r" | field samples) samples"
done
[ "$(echo "$h" | field coupled)" = "true" ] || fail "the derailed car came off the train on the host"
[ "$(echo "$c" | field coupled)" = "true" ] || fail "the derailed car came off the train on the client"
node -e '
  const h=+process.argv[1], c=+process.argv[2], ratio=+process.argv[3], floor=+process.argv[4];
  const limit=Math.max(floor, ratio*h);
  console.log("   client RMS "+c+" vs limit "+limit.toFixed(3)+" (host "+h+" x "+ratio+", floor "+floor+")");
  process.exit(c<=limit?0:1);' "$(echo "$h" | field rms)" "$(echo "$c" | field rms)" "$RATIO" "$FLOOR" ||
  fail "the derailed car judders on the client: its coupling gap changes much faster than on the host"
echo "PASS"
