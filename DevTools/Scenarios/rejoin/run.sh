#!/usr/bin/env bash
# Disconnect + rejoin regression test.
#  1. client holds an item and quits
#  2. host must keep every item of the departed player, and drop the held one into the world
#  3. client rejoins and must get exactly one of each item back, in its inventory
# Exit code 0 = pass. Set FRESH=1 to restart both games first; CRASH=1 to kill the client
# instead of quitting and rejoin before the host has noticed (stale-session reconnect).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/rejoin
U="using Multiplayer.Components.Networking; using Multiplayer.Components.Networking.World; using Multiplayer.Networking.Data.Items;"
# dvmp up, saying why on failure (its output is otherwise thrown away).
up() { local out; out=$($D up "$@" 2>&1) || { echo "FAIL: dvmp up: $(echo "$out" | tail -6 | tr -s ' \r\n' ' ')"; exit 1; }; }
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }

echo "== up"; up ${FRESH:+--fresh}
echo "== client holds an item"
# A fresh mug at the client's feet, so the test doesn't depend on loose items nearby.
$D eval -t host -m coroutine -u "$U" -a who=client -f DevTools/Scenarios/train-items/spawn-mug.cs >/dev/null
before=$($D eval -t client -m coroutine -u "$U" -f $P/hold.cs) || { echo "FAIL: hold: $(echo "$before" | grep -E "\"error\"|compileError" | head -2)"; exit 1; }
held=$(echo "$before" | field held); inventory=$(echo "$before" | field inventory); names=$(echo "$before" | field names); slots=$(echo "$before" | field slots)
echo "   holding $held ($(echo "$before" | field heldName)); owns $(echo "$inventory" | tr ',' '\n' | grep -c .) items"

client_pid=$($D status | node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));console.log(o.agents.find(a=>a.role==="client").pid)')
if [ -n "${CRASH:-}" ]; then
  # Simulated crash: kill the client and rejoin straight away, while the host still thinks the
  # old connection is alive (it only notices on timeout).
  echo "== client crashes"
  taskkill //F //PID "$client_pid" >/dev/null
  for i in $(seq 1 30); do tasklist //FI "PID eq $client_pid" //NH | grep -q DerailValley || break; sleep 1; done
else
  echo "== client quits"
  $D eval -t client 'Application.Quit();' >/dev/null || true
  for i in $(seq 1 30); do tasklist //FI "PID eq $client_pid" //NH | grep -q DerailValley || break; sleep 1; done
  $D eval -t host -m coroutine 'yield return new WaitForSecondsRealtime(4);' >/dev/null

  echo "== host keeps everything"
  r1=$($D eval -t host -m body -u "$U" -a held=$held -a inventory=$inventory -f $P/after-disconnect.cs)
  echo "   $(echo "$r1" | field failures)"; [ "$(echo "$r1" | field pass)" = "true" ] || { echo "FAIL after disconnect"; exit 1; }
fi

echo "== client rejoins"; up
$D eval -t client -m coroutine 'yield return new WaitForSeconds(8);' >/dev/null || { echo "FAIL: the rejoined client stopped answering"; exit 1; }
r2=$($D eval -t client -m body -u "$U" -a names=$names -a slots=$slots -f $P/after-rejoin.cs)
echo "   owned $(echo "$r2" | field owned): $(echo "$r2" | field failures)"; m=$(echo "$r2" | field moved); [ "$m" = "[]" ] || echo "   warning: slots changed $m"
[ "$(echo "$r2" | field pass)" = "true" ] || { echo "FAIL after rejoin"; exit 1; }
echo "PASS"
