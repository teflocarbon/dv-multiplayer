#!/usr/bin/env bash
# Shop sync (upstream #43/#103): a client buys an item and pays from the shared wallet. The host's
# wallet must pay (once), the item must exist for both players with one NetId, the stock must drop
# on both; a cancelled basket must hand the money back without printing a local banknote.
#   ITEM=shelf item name prefix (default Lighter)
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/shop
ITEM=${ITEM:-Lighter}
U="using DV.CabControls; using DV.CashRegister; using DV.InventorySystem; using DV.Items; using DV.Shops; using Multiplayer.Components.Networking.World;"
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
fail() { echo "FAIL: $*"; exit 1; }
near() { node -e 'process.exit(Math.abs(+process.argv[1]-(+process.argv[2]))<0.5?0:1)' "$1" "$2"; }
# the shop by the station at (13421, 3618)
$D eval -t client -m coroutine --timeout 60 'yield return LocalPlayer.TeleportToGround(13421f, 3618f);' >/dev/null
$D eval -t host -m coroutine --timeout 60 'yield return LocalPlayer.TeleportToGround(13424f, 3622f);' >/dev/null
sleep 2
echo "== client buys a $ITEM"
b=$($D eval -t client -m coroutine --timeout 60 -u "$U" -a item=$ITEM -f $P/buy.cs)
prefab=$(echo "$b" | field prefab); cost=$(echo "$b" | field cost); start=$(echo "$b" | field start); ids=$(echo "$b" | field netIds)
echo "   register $(echo "$b" | field distance) m away, \$$cost, bought $(echo "$b" | field bought); client wallet $start -> $(echo "$b" | field after), stock $(echo "$b" | field stock0) -> $(echo "$b" | field stock1), item NetId $ids, banknotes printed $(echo "$b" | field banknotes)"
[ "$(echo "$b" | field bought)" = "true" ] || fail "purchase failed"
[ -n "$ids" ] && [ "$ids" != "0" ] && [[ "$ids" != *,* ]] || fail "client should see one bought item with a NetId, got '$ids'"
sleep 2
h=$($D eval -t host -m body -u "$U" -a prefab=$prefab -a netId=$ids -f $P/peer.cs)
echo "   host: wallet $(echo "$h" | field money), stock $(echo "$h" | field stock), item seen $(echo "$h" | field seen)"
near "$(echo "$h" | field money)" "$(node -e "console.log($start-$cost)")" || fail "host wallet should be $start - $cost"
[ "$(echo "$h" | field stock)" = "$(echo "$b" | field stock1)" ] || fail "stock differs"
[ "$(echo "$h" | field seen)" = "true" ] || fail "host does not have the bought item"
[ "$(echo "$b" | field banknotes)" = "0" ] || fail "change was printed as a local banknote"
echo "== client pays and cancels"
c=$($D eval -t client -m coroutine --timeout 60 -u "$U" -a item=$ITEM -a cancel=1 -f $P/buy.cs)
sleep 2
h=$($D eval -t host -m body -u "$U" -a prefab=$prefab -a netId=0 -f $P/peer.cs)
echo "   host wallet $(echo "$c" | field start) -> $(echo "$h" | field money), banknotes printed $(echo "$c" | field banknotes)"
near "$(echo "$h" | field money)" "$(echo "$c" | field start)" || fail "cancel did not refund"
[ "$(echo "$c" | field banknotes)" = "0" ] || fail "refund printed a local banknote"
echo PASS
