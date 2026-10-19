#!/usr/bin/env bash
# A client's held and carried item stays the client's on the server (playtest 2, dedicated host).
# The server's copy of an item a client holds mirrors the client's. When it changed (taken in a
# hand, a lantern lit), the server applied that as a change by its own player, booked the item to
# that player (a dedicated host's parked body), and the next update took it out of the client's
# hand: lanterns vanished as they were lit. The client:
#  1. picks up a lantern the host spawned beside it,
#  2. lights it in its hand,
#  3. puts it in its inventory.
# After each step the client still has it, and the host records it in the client's hand or
# inventory, not its own player's. Also counts the server's backstop warnings ("kept it theirs"):
# each one is a path that still books a client's item to the server's player.
#   PREFAB=Lantern (EOTLantern, Flashlight...)
# Meant for a dedicated host (dvmp up --dedicated), where playtest 2 found it; runs on a player
# host too. Exit code 0 = pass. Needs a running pair.
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/remote-held-item
PREFAB=${PREFAB:-Lantern}
U="using Multiplayer.Components.Networking; using Multiplayer.Components.Networking.World;"
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
fail() { echo "FAIL: $*"; exit 1; }
item=""
cleanup() {
  $D eval -t client -m coroutine 'if (Hands.Held != null) yield return Hands.Drop(); Hands.ReleaseControl();' >/dev/null 2>&1 || true
  [ -n "$item" ] && $D eval -t host -m body -u "$U" -a item=$item 'if (NetworkedItem.TryGet(ushort.Parse(ctx.Args["item"]), out NetworkedItem n) && n != null) UnityEngine.Object.Destroy(n.gameObject); return 0;' >/dev/null 2>&1 || true
}
trap cleanup EXIT

echo "== dedicated host: $($D eval -t host -m body -u "$U" 'return new { d = NetworkLifecycle.Instance.IsDedicatedHost };' | field d)"
backstop() { $D logs -t host -l warning -c "kept it theirs" -n 1000 2>/dev/null | grep -c '"message"' || true; }
before=$(backstop)
item=$($D eval -t host -m coroutine -u "$U" -a prefab=$PREFAB -f $P/spawn.cs | field item)
echo "== client picks up $PREFAB $item, lights it, puts it away"

check() { # step clientState hostPlacement
  sleep 4
  c=$($D eval -t client -m body -u "$U" -a item=$item -f $P/client.cs)
  h=$($D eval -t host -m body -u "$U" -a item=$item -f $P/host.cs)
  echo "   $1: client has it $(echo "$c" | field state) (lit $(echo "$c" | field lit)); host records $(echo "$h" | field placement) of player $(echo "$h" | field player), the client is $(echo "$h" | field client) (host copy lit $(echo "$h" | field lit))"
  [ "$(echo "$c" | field state)" = "$2" ] || fail "after '$1' the client's $PREFAB is $(echo "$c" | field state), not $2"
  [ "$(echo "$h" | field player)" = "$(echo "$h" | field client)" ] || fail "after '$1' the host has the client's $PREFAB with player $(echo "$h" | field player)"
  [ "$(echo "$h" | field placement)" = "$3" ] || fail "after '$1' the host records $(echo "$h" | field placement), not $3"
}

$D eval -t client -m coroutine --timeout 60 -u "$U" -a item=$item 'ushort id = ushort.Parse(ctx.Args["item"]); NetworkedItem n = null; for (float t0 = Time.realtimeSinceStartup; (!NetworkedItem.TryGet(id, out n) || n == null) && Time.realtimeSinceStartup - t0 < 20f;) yield return null; if (Hands.Held != null) yield return Hands.Drop(); yield return LocalPlayer.GoTo(n); yield return Hands.Pickup(id);' >/dev/null
check "picked up" InHand PlayerHand
lit=$($D eval -t client -m coroutine -u "$U" -a item=$item -f $P/light.cs | field lit)
[ "$lit" = "true" ] || fail "the $PREFAB didn't light on the client"
check "lit in the hand" InHand PlayerHand
$D eval -t client -m coroutine -u "$U" -a item=$item 'NetworkedItem.TryGet(ushort.Parse(ctx.Args["item"]), out NetworkedItem n); DV.InventorySystem.Inventory.Instance.AddItemToInventory(n.gameObject); yield return new WaitForSecondsRealtime(1f); Hands.ReleaseControl();' >/dev/null
check "put in the inventory" InInventory PlayerInventory

echo "   the server's backstop acted $(( $(backstop) - before )) time(s)"
echo PASS
