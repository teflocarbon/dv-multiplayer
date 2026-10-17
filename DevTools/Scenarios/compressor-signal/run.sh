#!/usr/bin/env bash
# A DE6 with its engine running and an empty main reservoir builds air (playtest 2: L-056 had no
# brakes). The compressor's on/off port is typed CONTROL, but DV's CompressorSimController writes
# it from the brake system, and only when the brake system's decision changes. Once the port held 0
# while the brake system wanted the compressor on, nothing put it back: no air on any game. The
# host now keeps it matched to the brake system.
#  1. The host spawns a DE6, starts it and empties its main reservoir: the brake system wants the
#     compressor on. The client boards it.
#  2. The client's copy of the port goes to 0 (an unleased control change, as playtest 2 most
#     likely had it), then the reservoir is emptied again.
#  3. The host's own port goes to 0, then the reservoir is emptied again.
# After 2 and 3 the port is back to 1 and the main reservoir pressure rises on host and client.
#   LOCO=LocoDE6  WAIT=20 (seconds of compressor time per step)  RISE=0.5 (bar it must gain)
# Exit code 0 = pass. Needs a running pair; a dedicated host works too.
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/compressor-signal
LOCO=${LOCO:-LocoDE6}; WAIT=${WAIT:-20}; RISE=${RISE:-0.5}
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
fail() { echo "FAIL: $*"; exit 1; }
loco=""
cleanup() {
  $D eval -t client -m coroutine 'yield return LocalPlayer.TeleportToGround(7190f, 6880f);' >/dev/null 2>&1 || true
  [ -n "$loco" ] && $D eval -t host -m body -a id=$loco 'Trains.Delete(Trains.Find(ctx.Args["id"])); return 0;' >/dev/null 2>&1 || true
}
trap cleanup EXIT

loco=$($D eval -t host -m coroutine --timeout 60 -a t=$LOCO 'var car = Trains.SpawnNearPlayer(ctx.Args["t"]); car.brakeSystem.SetHandbrakePosition(1f); for (int i = 0; i < 120; i++) yield return null; yield return Trains.StartLoco(car); car.brakeSystem.SetMainReservoirPressure(1f); yield return new WaitForSeconds(2); ctx.Result = new { id = car.ID };' | field id)
echo "== $LOCO $loco started, main reservoir emptied; the client boards it"
$D eval -t client -m coroutine --timeout 60 -a loco=$loco 'yield return Trains.Board(Trains.Find(ctx.Args["loco"])); yield return new WaitForSeconds(3);' >/dev/null

step() { # what who
  $D eval -t $2 -m body -a loco=$loco -a value=0 -f $P/signal.cs >/dev/null
  sleep 2
  $D eval -t host -m body -a loco=$loco 'Trains.Find(ctx.Args["loco"]).brakeSystem.SetMainReservoirPressure(1f); return 0;' >/dev/null
  h0=$($D eval -t host -m body -a loco=$loco -f $P/signal.cs)
  sleep "$WAIT"
  h=$($D eval -t host -m body -a loco=$loco -f $P/signal.cs); c=$($D eval -t client -m body -a loco=$loco -f $P/signal.cs)
  echo "   $1: brake system wants the compressor $(echo "$h" | field wanted); port host $(echo "$h" | field port), client $(echo "$c" | field port); main reservoir host $(echo "$h0" | field pressure) -> $(echo "$h" | field pressure) bar, client $(echo "$c" | field pressure) bar"
  [ "$(echo "$h" | field port)" = "1" ] || fail "after '$1' the host's compressor signal is $(echo "$h" | field port), with the brake system wanting it $(echo "$h" | field wanted)"
  node -e 'const [a,b,c,r]=process.argv.slice(1).map(Number);process.exit(b-a>=r&&c-a>=r?0:1)' "$(echo "$h0" | field pressure)" "$(echo "$h" | field pressure)" "$(echo "$c" | field pressure)" "$RISE" ||
    fail "after '$1' the main reservoir didn't build air on both (needs +$RISE bar)"
}
echo "== the client's copy of the compressor signal goes to 0"
step "client wrote 0" client
echo "== the host's own compressor signal goes to 0"
step "host wrote 0" host
echo PASS
