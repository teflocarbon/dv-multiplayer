#!/usr/bin/env bash
# A player leaves with someone else's item (the item audit: borrowed items left with the player).
# The host's mug goes into the client's inventory; the client quits. The mug must be put down in
# the world, still the host's, not kept in the departed player's inventory.
# Exit code 0 = pass. Needs a running pair (dvmp up); restarts the client.
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/borrowed-disconnect
U="using Multiplayer.Client.Session; using Multiplayer.Server.Coverage; using Multiplayer.Server.Dedicated; using Multiplayer.Shared.Session; using Multiplayer.Server.Items; using Multiplayer.Shared.Items; using Multiplayer.Shared.Jobs; using Multiplayer.Shared.Stations; using Multiplayer.Shared.World; using Multiplayer.Shared.Protocol.Data.Items;"
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
fail() { echo "FAIL: $*"; exit 1; }
item=$($D eval -t host -m coroutine -u "$U" -a who=host -f DevTools/Scenarios/train-items/spawn-mug.cs | field item)
o=$($D eval -t host -m coroutine -u "$U" -a item=$item -f $P/own.cs)
echo "== mug $item belongs to the host (owner P$(echo "$o" | field owner), host P$(echo "$o" | field host))"
[ "$(echo "$o" | field owner)" = "$(echo "$o" | field host)" ] || fail "host doesn't own it"
echo "== client puts it in its inventory, then quits"
$D eval -t client -m coroutine -u "$U" -a item=$item -f DevTools/Scenarios/inventory-persist/stash.cs >/dev/null
sleep 2
before=$($D eval -t host -m body -u "$U" -a item=$item -f DevTools/Scenarios/lost-and-found/record.cs)
echo "   host record: $(echo "$before" | field placement) for P$(echo "$before" | field placementPlayer), owner P$(echo "$before" | field owner)"
$D eval -t client 'Application.Quit();' >/dev/null || true
$D eval -t host -m coroutine 'yield return new WaitForSeconds(6);' >/dev/null
after=$($D eval -t host -m body -u "$U" -a item=$item 'ushort id = ushort.Parse(ctx.Args["item"]); bool k = AuthoritativeItemRegistry.TryGet(id, out var r); return new { placement = k ? r.Placement.ToString() : "", owner = k ? (int)r.PersistentOwnerPlayerId : -1, lost = NetworkedLostAndFoundManager.Contains(id) };')
echo "   after the client left: $(echo "$after" | field placement), owner P$(echo "$after" | field owner), in Lost and Found $(echo "$after" | field lost)"
case "$(echo "$after" | field placement)" in World|LostAndFound) ;; *) fail "the host's mug left with the client";; esac
[ "$(echo "$after" | field owner)" = "$(echo "$o" | field host)" ] || fail "owner changed"
echo "== client rejoins"; $D up >/dev/null
echo PASS
