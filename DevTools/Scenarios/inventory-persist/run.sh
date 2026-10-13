#!/usr/bin/env bash
# A client's inventory survives the host saving and reloading: the client stashes a personal item
# (as a purchase gives them), the host saves, both games restart from the save, and the client must
# get the item back once - bound to the host, not as a stray local copy.
#   PREFAB=item (default Mug)  SESSION=save to reload (default "DVMP Test Career 2")
# Exit code 0 = pass. Needs a running pair (dvmp up). Restarts both games.
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/inventory-persist
PREFAB=${PREFAB:-Mug}; SESSION=${SESSION:-"DVMP Test Career 2"}
U="using Multiplayer.Components.Networking.World;"
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
fail() { echo "FAIL: $*"; exit 1; }

before=$($D eval -t client -m body -u "$U" -a prefab=$PREFAB -f $P/contents.cs | field count)
item=$($D eval -t host -m coroutine -u "$U" -a prefab=$PREFAB -f DevTools/Scenarios/lost-and-found/spawn-personal.cs | field item)
echo "== client stashes $PREFAB $item (holding $before before)"
$D eval -t client -m coroutine --timeout 60 -u "$U" -a item=$item -f $P/stash.cs | field slot >/dev/null
want=$((before + 1))
echo "== host saves; both games restart from the save"
$D eval -t host -m coroutine 'SaveGameManager.Instance.Save(DV.Common.SaveType.Manual); yield return new WaitForSeconds(3);' >/dev/null
$D down >/dev/null
$D up --save "$SESSION" >/dev/null
$D eval -t client -m coroutine 'yield return new WaitForSeconds(12);' >/dev/null
r=$($D eval -t client -m body -u "$U" -a prefab=$PREFAB -f $P/contents.cs)
count=$(echo "$r" | field count); bound=$(echo "$r" | field bound)
echo "   client holds $count $PREFAB ($bound bound to the host); inventory: $(echo "$r" | field all)"
[ "$count" = "$want" ] || fail "expected $want $PREFAB, found $count"
[ "$bound" = "$want" ] || fail "only $bound of $count are bound to the host"
echo PASS
