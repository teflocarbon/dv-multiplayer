#!/usr/bin/env bash
# Client-only derail (the other session's finding #6): a physics hitch derails a car on one client
# while the host's stays on the rails. It used to stay derailed there for good, since bogie samples
# are ignored while derailed. The client must put it back on the host's track, where the host has
# it, still coupled, and the host's train must not be touched.
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/client-derail
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
fail() { echo "FAIL: $*"; exit 1; }
s=$($D eval -t host -m coroutine --timeout 60 -f DevTools/Scenarios/uncouple-moving/setup.cs)
L=$(echo "$s" | field loco); M=$(echo "$s" | field mid); W=$(echo "$s" | field last)
cleanup() { for c in $L $M $W; do $D eval -t host -m body -a id=$c 'Trains.Delete(Trains.Find(ctx.Args["id"])); return 0;' >/dev/null 2>&1 || true; done; }
trap cleanup EXIT
sleep 3
echo "== client derails $W (coupled to $M) on its own"
c=$($D eval -t client -m body -a car=$W 'var car = Trains.Find(ctx.Args["car"]); car.Derail(); return new { derailed = car.derailed };')
echo "   client: derailed $(echo "$c" | field derailed)"
[ "$(echo "$c" | field derailed)" = "true" ] || fail "the test derail didn't happen"
sleep 8
c=$($D eval -t client -m body -a car=$W -a other=$M -f $P/state.cs); h=$($D eval -t host -m body -a car=$W -a other=$M -f $P/state.cs)
d=$(node -e 'const a=JSON.parse(process.argv[1]),b=JSON.parse(process.argv[2]);console.log(Math.hypot(a.x-b.x,a.z-b.z).toFixed(2))' "$(echo "$c" | field abs)" "$(echo "$h" | field abs)")
echo "   after 8 s: client derailed $(echo "$c" | field derailed), coupled $(echo "$c" | field coupled), on $(echo "$c" | field track); host derailed $(echo "$h" | field derailed), coupled $(echo "$h" | field coupled); apart $d m"
[ "$(echo "$h" | field derailed)" = "false" ] && [ "$(echo "$h" | field coupled)" = "true" ] || fail "the host's train was changed"
[ "$(echo "$c" | field derailed)" = "false" ] || fail "still derailed on the client"
[ "$(echo "$c" | field coupled)" = "true" ] || fail "rerailed but left uncoupled on the client"
node -e 'process.exit(+process.argv[1] < 1 ? 0 : 1)' "$d" || fail "client car $d m from the host's"
echo PASS
