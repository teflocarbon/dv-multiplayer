#!/usr/bin/env bash
# Essential item recall: the client drops an essential item (its slot keeps a reserved
# silhouette), walks away and recalls it into that slot. With TAKEN=1 the host picks the item up
# first, so the recall has to take it from another player's hands.
#   SLOT=inventory slot of the essential item (default 0, the comms radio)  TAKEN=1
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/recall
SLOT=${SLOT:-0}
U="using Multiplayer.Server.Items; using Multiplayer.Shared.Items; using Multiplayer.Shared.Jobs; using Multiplayer.Shared.Stations; using Multiplayer.Shared.World; using Multiplayer.Shared.Protocol.Data.Items;"
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
fail() { echo "FAIL: $*"; exit 1; }

echo "== client drops its slot $SLOT essential"
d=$($D eval -t client -m coroutine -u "$U" -a slot=$SLOT -f $P/drop.cs)
item=$(echo "$d" | field item); echo "   $(echo "$d" | field name) $item: $(echo "$d" | field state), slot reserved $(echo "$d" | field reserved)"
[ "$(echo "$d" | field state)" = "Dropped" ] || fail "item not dropped"
if [ -n "${TAKEN:-}" ]; then
  echo "== host picks it up"
  [ "$($D eval -t host -m coroutine -u "$U" -a item=$item -f $P/take.cs | field held)" = "$item" ] || fail "host could not pick it up"
fi
echo "== client walks 40 m away and recalls it"
$D eval -t client -m coroutine -a dx=40 -a dz=0 -f DevTools/Scenarios/lost-and-found/away.cs >/dev/null
r=$($D eval -t client -m coroutine -u "$U" -a item=$item -a slot=$SLOT -f $P/recall.cs)
echo "   client: slot $(echo "$r" | field slot), $(echo "$r" | field state)"
[ "$(echo "$r" | field slot)" = "$SLOT" ] || fail "item not back in slot $SLOT"
sleep 2
h=$($D eval -t host -m body -u "$U" -a item=$item -f DevTools/Scenarios/lost-and-found/record.cs)
echo "   host: $(echo "$h" | field placement) for player $(echo "$h" | field placementPlayer)"
[ "$(echo "$h" | field placement)" = "PlayerInventory" ] && [ "$(echo "$h" | field placementPlayer)" = "$(echo "$h" | field clientId)" ] || fail "host record: $h"
if [ -n "${TAKEN:-}" ]; then
  held=$($D eval -t host -u "$U" 'Hands.Held?.NetId ?? 0' | node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));console.log(Object.values(o.results)[0].result)')
  [ "$held" != "$item" ] || fail "host still holds the recalled item"
fi
echo PASS
