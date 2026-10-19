#!/usr/bin/env bash
# What a player owns isn't counted from another player's items (playtest 2). Player numbers are
# handed out per session, and a record restored from a save, or left by a player who has gone,
# keeps the number its owner had then; another player can have that number now. OwnedItemsSummary
# matched by that number as well as by identity, so the other player's comms radio counted as
# this player's: DV's starting-items safeguard made them none on joining, and /give refused one
# ("already has a CommsRadio somewhere").
# A pair has one client, so the shift is set up by hand: the host spawns an essential item owned by
# a player who isn't here (a new identity) and gives its record the client's number, as a restored
# record whose owner hasn't joined would have. Then:
#  1. the client's owned items (what its join packet tells DV it has, and what /give checks) don't
#     include it;
#  2. if the client didn't own one already, /give (sent by the client, made an admin in memory
#     only) gives it one, into its inventory.
#   PREFAB= an essential item (default: one the client doesn't own, else the comms radio)
# Exit code 0 = pass. Needs a running pair; a dedicated host works too.
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/owned-items
U="using Multiplayer.Client.Session; using Multiplayer.Server.Coverage; using Multiplayer.Server.Dedicated; using Multiplayer.Shared.Session; using Multiplayer.Server.Items; using Multiplayer.Shared.Items; using Multiplayer.Shared.Jobs; using Multiplayer.Shared.Stations; using Multiplayer.Shared.World; using Multiplayer.Server.Career; using Multiplayer.Server.Chat; using Multiplayer.Server.Players; using Multiplayer.Server.Session;"
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
fail() { echo "FAIL: $*"; exit 1; }
host() { $D eval -t host -m body -u "$U" "$@"; }
items=""
cleanup() {
  host 'Multiplayer.Multiplayer.Settings.Admins = ""; return 0;' >/dev/null 2>&1 || true
  for i in $items; do host -a item=$i 'if (NetworkedItem.TryGet(ushort.Parse(ctx.Args["item"]), out NetworkedItem n) && n != null) UnityEngine.Object.Destroy(n.gameObject); return 0;' >/dev/null 2>&1 || true; done
}
trap cleanup EXIT

s=$($D eval -t host -m coroutine -u "$U" -a prefab="${PREFAB:-}" -f $P/stale-owner.cs)
prefab=$(echo "$s" | field prefab); items=$(echo "$s" | field item)
echo "== host spawned $prefab $(echo "$s" | field item), owned by an absent player whose number ($(echo "$s" | field number)) is now the client's"
echo "   the client owned $(echo "$s" | field before) $prefab before, counts $(echo "$s" | field after) now"
[ "$(echo "$s" | field after)" = "$(echo "$s" | field before)" ] || fail "the absent player's $prefab counts as the client's"

if [ "$(echo "$s" | field before)" = "0" ]; then
  guid=$(host 'var server = NetworkLifecycle.Instance.Server; return new { g = server.ServerPlayers.First(p => p.PlayerId != server.SelfId).Guid.ToString() };' | field g)
  name=$(host 'var server = NetworkLifecycle.Instance.Server; return new { n = server.ServerPlayers.First(p => p.PlayerId != server.SelfId).Username };' | field n)
  host -a guid=$guid 'Multiplayer.Multiplayer.Settings.Admins = ctx.Args["guid"]; return 0;' >/dev/null
  echo "== the client sends /give $name $prefab"
  # Without its slash: Git Bash would turn "/give" into a Windows path.
  $D eval -t client -m body -u "$U" -a cmd="give $name $prefab" 'NetworkLifecycle.Instance.Client.SendChat("/" + ctx.Args["cmd"]); return 0;' >/dev/null
  g=$($D eval -t client -m coroutine -u "$U" -a prefab=$prefab 'NetworkedItem got = null; for (float t0 = Time.realtimeSinceStartup; got == null && Time.realtimeSinceStartup - t0 < 10f; ) { yield return new WaitForSecondsRealtime(0.5f); got = DV.InventorySystem.Inventory.Instance.GetItemsArray(false).Where(go => go != null && go.name.StartsWith(ctx.Args["prefab"])).Select(go => go.GetComponent<NetworkedItem>()).FirstOrDefault(n => n != null && n.NetId != 0); } ctx.Result = new { item = got == null ? 0 : got.NetId };')
  given=$(echo "$g" | field item)
  echo "   in the client's inventory: $([ "$given" = 0 ] && echo nothing || echo "$prefab $given")"
  [ "$given" != "0" ] || fail "/give didn't give the client a $prefab"
  items="$items $given"
else
  echo "   (the client already owns a $prefab, so /give is left out; set PREFAB to one it doesn't own)"
fi
echo PASS
