#!/usr/bin/env bash
# Sandbox cargo loader (Discord: "setting the cargo load with the sandbox controls is not synced"):
# a client fills a car with the comms radio's cargo loader, then empties it. Both peers must show
# the same cargo each time. Turns on the radio cheat mode on the host for the test (career saves
# hide the loader otherwise) and turns it back off.
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/sandbox-cargo
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
fail() { echo "FAIL: $*"; exit 1; }
was=$($D eval -t host 'DV.Globals.G.GameParams.CommsRadioCheatMode' | node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));console.log(Object.values(o.results)[0].result)')
car=$($D eval -t host -m coroutine 'var c = Trains.SpawnNearPlayer("BoxcarBrown"); for (int i = 0; i < 120; i++) yield return null; ctx.Result = new { id = c.ID };' | field id)
cleanup() { $D eval -t host -m body "DV.Globals.G.GameParams.CommsRadioCheatMode = $was; return 0;" >/dev/null 2>&1 || true; $D eval -t host -m body -a id=$car 'Trains.Delete(Trains.Find(ctx.Args["id"])); return 0;' >/dev/null 2>&1 || true; }
trap cleanup EXIT
$D eval -t host -m body 'DV.Globals.G.GameParams.CommsRadioCheatMode = true; return 0;' >/dev/null
sleep 2
for step in load unload; do
  arg=$([ $step = load ] && echo first || echo none)
  r=$($D eval -t client -m coroutine --timeout 30 -a car=$car -a cargo=$arg -f $P/load.cs)
  sleep 2
  h=$($D eval -t host -m body -a car=$car -f $P/state.cs); c=$($D eval -t client -m body -a car=$car -f $P/state.cs)
  echo "== client ${step}s $car ($(echo "$r" | field cargo)): host $(echo "$h" | field loaded) $(echo "$h" | field amount), client $(echo "$c" | field loaded) $(echo "$c" | field amount)"
  [ "$(echo "$h" | field loaded)" = "$(echo "$c" | field loaded)" ] || fail "cargo differs"
  if [ $step = load ]; then [ "$(echo "$h" | field loaded)" != "None" ] || fail "the host's car wasn't loaded"; else [ "$(echo "$h" | field loaded)" = "None" ] || fail "the host's car wasn't emptied"; fi
done
echo PASS
