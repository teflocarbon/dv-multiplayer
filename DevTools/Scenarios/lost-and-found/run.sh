#!/usr/bin/env bash
# Lost & Found round trip: the client claims a personal item, drops it and walks away; the host
# collects it into Lost & Found; the client retrieves it into their inventory. The policy is
# tightened for the run (owner distance 50 m, grace 1 s) and restored afterwards. While it is
# tightened, other personal items both players walk away from are collected too.
#   PREFAB=item prefab name (default Mug)
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/lost-and-found
PREFAB=${PREFAB:-Mug}
U="using Multiplayer.Components.Networking.World; using Multiplayer.Networking.Data.Items;"
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
record() { $D eval -t host -m body -u "$U" -a item=$1 -f $P/record.cs; }
fail() { echo "FAIL: $*"; exit 1; }

old=$($D eval -t host -m body -a owner=50 -a nearby=10 -a grace=1 -f $P/settings.cs | field owner)
restore() { $D eval -t host -m body -a owner=${old:-200} -a nearby=100 -a grace=10 -f $P/settings.cs >/dev/null 2>&1 || true; }
trap restore EXIT

echo "== host spawns a personal $PREFAB at the client (as a purchase does)"
item=$($D eval -t host -m coroutine -u "$U" -a prefab=$PREFAB -f $P/spawn-personal.cs | field item)
echo "   item $item"
echo "== client picks it up and drops it"
$D eval -t client -m coroutine --timeout 60 -u "$U" -a item=$item -f $P/claim.cs | field item >/dev/null
r=$(record $item); owner=$(echo "$r" | field owner); cid=$(echo "$r" | field clientId)
[ "$owner" = "$cid" ] || fail "item owner is $owner, expected the client ($cid): $r"

echo "== both players walk 300 m away"
$D eval -t client -m coroutine -a dx=300 -a dz=0 -f $P/away.cs | field at
$D eval -t host -m coroutine -a dx=300 -a dz=20 -f $P/away.cs | field at
for i in $(seq 1 30); do
  [ "$(record $item | field lost)" = "true" ] && break; sleep 1
done
r=$(record $item); [ "$(echo "$r" | field lost)" = "true" ] || fail "host never collected the item: $r"
echo "   collected: placement $(echo "$r" | field placement)"

echo "== client retrieves it"
res=$($D eval -t client -m coroutine --timeout 60 -u "$U" -a item=$item -f $P/retrieve.cs)
echo "$res" | field slot >/dev/null || fail "retrieval: $(echo "$res" | grep -E 'error|Exception' | head -3)"
sleep 2
r=$(record $item)
[ "$(echo "$r" | field lost)" = "false" ] || fail "still listed as lost on the host: $r"
[ "$(echo "$r" | field placement)" = "PlayerInventory" ] || fail "host placement: $r"
[ "$(echo "$r" | field placementPlayer)" = "$cid" ] || fail "host thinks another player holds it: $r"
echo "   slot $(echo "$res" | field slot), host $(echo "$r" | field placement)"
echo PASS
