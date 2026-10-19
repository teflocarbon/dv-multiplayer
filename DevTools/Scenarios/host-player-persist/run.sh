#!/usr/bin/env bash
# The host's own player is stored by GUID like any player (docs/design/server-player-split.md):
# the host stashes a mug, saves, both games restart from the save, and the host must get its whole
# inventory back once (nothing duplicated, nothing lost), at the same place. The save must be in
# the per-player format, with the host's items as records owned by its GUID, and DV's own inventory
# list kept as a copy for vanilla DV.
#   SESSION=save to reload (default "DVMP Test Career 2")
# Needs an ordinary host (on a dedicated host the local player is a parked body); skips otherwise.
# Exit code 0 = pass. Restarts both games.
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/host-player-persist
SESSION=${SESSION:-"DVMP Test Career 2"}
U="using Multiplayer.Components.Networking;using Multiplayer.Components.Networking.World;"
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
fail() { echo "FAIL: $*"; exit 1; }

[ "$($D eval -t host -m body -u "$U" 'return new { d = NetworkLifecycle.Instance.IsDedicatedHost };' | field d)" = "false" ] ||
  { echo "SKIP: needs an ordinary host"; exit 0; }

item=$($D eval -t host -m coroutine -u "$U" -a who=host -f DevTools/Scenarios/train-items/spawn-mug.cs | field item)
$D eval -t host -m coroutine --timeout 60 -u "$U" -a item=$item -f DevTools/Scenarios/inventory-persist/stash.cs >/dev/null ||
  fail "the host couldn't stash mug $item"
before=$($D eval -t host -m body -u "$U" -f $P/state.cs)
echo "== host stashed mug $item; inventory: $(echo "$before" | field inventory)"
[ "$(echo "$before" | field duplicates)" = "" ] || fail "duplicates before saving: $(echo "$before" | field duplicates)"

echo "== host saves; both games restart from the save"
$D eval -t host -m coroutine 'SaveGameManager.Instance.Save(DV.Common.SaveType.Manual); yield return new WaitForSeconds(3);' >/dev/null
saved=$($D eval -t host -m body -u "$U" -f $P/saved.cs)
echo "   save: format $(echo "$saved" | field format), $(echo "$saved" | field hostRecords) record(s) owned by the host's GUID, DV's own inventory list holds $(echo "$saved" | field mirror)"
[ "$(echo "$saved" | field format)" = "2" ] || fail "the save isn't in the per-player format"
[ "$(echo "$saved" | field hostRecords)" != "0" ] || fail "no records owned by the host's GUID"
[ "$(echo "$saved" | field mirror)" != "0" ] || fail "DV's own inventory list is empty (no copy for vanilla DV)"

$D down >/dev/null
$D up --save "$SESSION" >/dev/null || fail "dvmp up after the save failed"
$D eval -t host -m coroutine 'yield return new WaitForSeconds(12);' >/dev/null
after=$($D eval -t host -m body -u "$U" -f $P/state.cs)
echo "   after: inventory $(echo "$after" | field inventory)"
[ "$(echo "$after" | field duplicates)" = "" ] || fail "duplicates after reloading: $(echo "$after" | field duplicates)"
[ "$(echo "$after" | field inventory)" = "$(echo "$before" | field inventory)" ] || fail "the host's inventory changed across the reload"
moved=$(node -e 'const a=JSON.parse(process.argv[1]),b=JSON.parse(process.argv[2]);console.log(Math.hypot(a.x-b.x,a.z-b.z).toFixed(1))' "$(echo "$before" | field position)" "$(echo "$after" | field position)")
echo "   the host spawned $moved m from where it saved"
node -e 'process.exit(+process.argv[1] < 5 ? 0 : 1)' "$moved" || fail "the host spawned $moved m from where it saved"
echo PASS
