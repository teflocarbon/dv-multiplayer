#!/usr/bin/env bash
# Arriving at a station: both players travel to a far station (whose jobs have not been generated
# yet) while both peers record frame times. Players reported everyone freezing for 1-2 s when
# someone reaches a station. Reports stalls per peer; fails if a frame took longer than MAX_MS.
#   SKIP=n picks the n-th farthest station instead (default 0)  WHO=host|client|both (default
#   client: only the client travels, the host stays and watches)  MAX_MS (default 1000)
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/station-load
WHO=${WHO:-client}; MAX_MS=${MAX_MS:-1000}
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }

s=$($D eval -t host -m body -a skip=${SKIP:-0} -f $P/pick.cs)
id=$(echo "$s" | field id); x=$(echo "$s" | field x); z=$(echo "$s" | field z)
echo "== $WHO travels to $id, $(echo "$s" | field km) km away; recording frames on both"
T=$(mktemp -d)
$D eval -t host -m coroutine --timeout 60 -a seconds=30 -f $P/frames.cs >"$T/host.json" &
$D eval -t client -m coroutine --timeout 60 -a seconds=30 -f $P/frames.cs >"$T/client.json" &
sleep 3
case $WHO in
  host|client) $D eval -t $WHO -m coroutine --timeout 60 -a x=$x -a z=$z -a dx=0 -f $P/go.cs >/dev/null ;;
  both) $D eval -t client -m coroutine --timeout 60 -a x=$x -a z=$z -a dx=4 -f $P/go.cs >/dev/null
        $D eval -t host -m coroutine --timeout 60 -a x=$x -a z=$z -a dx=0 -f $P/go.cs >/dev/null ;;
esac
wait
fail=0
for peer in host client; do
  r=$(cat "$T/$peer.json")
  w=$(echo "$r" | field worstMs)
  echo "   $peer: worst $w ms, $(echo "$r" | field stallsOver100ms) stalls over 100 ms ($(echo "$r" | field stalledSeconds) s), $(echo "$r" | field worst)"
  [ "${w%.*}" -le "$MAX_MS" ] || fail=1
done
[ $fail -eq 0 ] && echo PASS || { echo "FAIL: a frame took longer than $MAX_MS ms"; exit 1; }
