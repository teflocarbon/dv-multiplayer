#!/usr/bin/env bash
# Cold containers: the client picks up a container (making it theirs), opens it, deposits an item
# from the inventory, closes and reopens it (contents come from the host), and withdraws the
# item again. The host must not be able to browse the client's container.
#   CONTAINER=prefab (default ItemContainerToolbox)  PREFAB=item to store (default Mug)
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/containers
CONTAINER=${CONTAINER:-ItemContainerToolbox}; PREFAB=${PREFAB:-Mug}
U="using Multiplayer.Server.Items; using Multiplayer.Shared.Items; using Multiplayer.Shared.Jobs; using Multiplayer.Shared.Stations; using Multiplayer.Shared.World;"
UC="$U using CUI = Multiplayer.Integrations.Inventory.ColdContainerUiIntegration;"
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
fail() { echo "FAIL: $*"; exit 1; }

echo "== host spawns a $CONTAINER and a $PREFAB at the client"
box=$($D eval -t host -m coroutine -u "$U" -a prefab=$CONTAINER -f $P/spawn.cs | field item)
item=$($D eval -t host -m coroutine -u "$U" -a prefab=$PREFAB -f $P/spawn.cs | field item)
echo "   container $box, item $item"
# Batteries are host-authoritative: drain the host's copy to a distinctive level and let it sync.
$D eval -t host -m coroutine -u "$U" -a item=$item 'NetworkedItem.TryGet(ushort.Parse(ctx.Args["item"]), out NetworkedItem n); var b = n.GetComponentInChildren<Battery>(true); if (b != null) b.Drain(b.CurrentPower - 37f); yield return new WaitForSeconds(2);' >/dev/null
echo "== client stores the item, reopens the container and takes it back"
r=$($D eval -t client -m coroutine --timeout 120 -u "$UC" -a box=$box -a item=$item -f $P/roundtrip.cs) ||
  fail "$(echo "$r" | grep -E '"error"' | head -1)"
echo "$r" | field log | node -e 'for (const l of JSON.parse(require("fs").readFileSync(0,"utf8"))) console.log("   "+l)'
echo "== host tries to browse the client's container"
h=$($D eval -t host -m body -u "$U" -a box=$box -f $P/host-browse.cs)
echo "   owner $(echo "$h" | field owner), host browse: $(echo "$h" | field status) $(echo "$h" | field reason)"
[ "$(echo "$h" | field owner)" != "$(echo "$h" | field hostId)" ] || fail "the host owns the client's container"
[ "$(echo "$h" | field status)" = "Unauthorized" ] || fail "the host could browse the client's container"
echo PASS
