#!/usr/bin/env bash
# Issue #37: an item that falls through the map near a client is reported by the client's game and
# rescued by the server. The server's own scan is turned off for the run (it loads terrain around
# remote players and would usually find the item first), so only the client's below-map report can
# rescue it: the host's copy is put 4 m under the client's ground and the client sent that pose.
# The client must report it, the server rescue it (its record's last move a below-map rescue from
# the client's report), and both games end with it on the ground at the same spot.
#   PREFAB=item prefab (default Mug)
# Exit code 0 = pass. Needs a running pair (dvmp up, or dvmp up --dedicated).
set -euo pipefail
cd "$(dirname "$0")/../../.."
dvmp_bin=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}; D=dvmp_checked
dvmp_checked() { local out; if ! out=$("$dvmp_bin" "$@"); then echo "FAIL: dvmp ${*:1:3} failed: ${out:-no output}" >&2; return 1; fi; printf '%s\n' "$out"; }
P=DevTools/Scenarios/client-fall-rescue
PREFAB=${PREFAB:-Mug}
U="using Multiplayer.Server.Items; using Multiplayer.Shared.Items; using Multiplayer.Shared.Jobs; using Multiplayer.Shared.Stations; using Multiplayer.Shared.World;"
val() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}console.log(JSON.stringify(x.result))'; }
get() { node -e 'const v=JSON.parse(process.argv[1])[process.argv[2]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1" "$2"; }
fail() { echo "FAIL: $*"; exit 1; }

$D eval -t host -m class -u "$U" -a action=install -f $P/no-host-scan.cs >/dev/null
item=
cleanup() {
  "$dvmp_bin" eval -t host -m class -u "$U" -a action=remove -f $P/no-host-scan.cs >/dev/null 2>&1 || true
  [ -n "$item" ] && "$dvmp_bin" eval -t host -m body -u "$U" -a item=$item 'if (NetworkedItem.TryGet(ushort.Parse(ctx.Args["item"]), out NetworkedItem n) && n != null) UnityEngine.Object.Destroy(n.gameObject); return 0;' >/dev/null 2>&1 || true
}
trap cleanup EXIT

item=$($D eval -t host -m coroutine -u "$U" -a prefab=$PREFAB -f $P/spawn.cs | val | node -e 'console.log(JSON.parse(require("fs").readFileSync(0,"utf8")).item)')
s=$($D eval -t client -m coroutine --timeout 60 -u "$U" -a item=$item -f $P/surface.cs | val)
surface=$(get "$s" surface)
echo "== $PREFAB $item next to the client, ground at $surface there; the host's copy goes 4 m under it"
k=$($D eval -t host -m body -u "$U" -a item=$item -a surface=$surface -f $P/sink.cs | val)
[ "$(get "$k" sent)" -ge 1 ] || fail "the client doesn't know the item: nothing to send it ($k)"
echo "   sent to $(get "$k" sent) client(s); the host has terrain there: $(get "$k" hostHasTerrain)"

r=
for i in $(seq 1 30); do
  r=$($D eval -t host -m body -u "$U" -a item=$item -f $P/record.cs | val)
  [ "$(get "$r" moveReason)" = RescuedFromBelowMap ] && break
  sleep 1
done
[ "$(get "$r" moveReason)" = RescuedFromBelowMap ] || fail "never rescued in 30 s (the client never reported it?): $r"
echo "   rescued: $(get "$r" moveDetail)"
sleep 3
h=$($D eval -t host -m body -u "$U" -a item=$item -f $P/where.cs | val)
c=$($D eval -t client -m body -u "$U" -a item=$item -f $P/where.cs | val)
node -e '
const [r,h,c,surface]=[JSON.parse(process.argv[1]),JSON.parse(process.argv[2]),JSON.parse(process.argv[3]),+process.argv[4]]; const f=[];
if (!/client-report/.test(r.moveDetail)) f.push("the rescue was not from the client'"'"'s report: "+r.moveDetail);
if (r.placement!=="World") f.push("record placement "+r.placement);
if (r.y < surface - 0.5) f.push("record is still under the ground ("+r.y.toFixed(2)+" < "+surface.toFixed(2)+")");
for (const [n,v] of [["host",h],["client",c]]) {
  if (!v.present) { f.push(n+": item missing"); continue; }
  if (v.pos[1] < surface - 0.5) f.push(n+": still under the ground (y "+v.pos[1].toFixed(2)+", ground "+surface.toFixed(2)+")");
}
if (c.present && c.hasTerrain && c.depth > 0.5) f.push("client: "+c.depth.toFixed(2)+" m under its terrain");
if (h.present && c.present) {
  const xz=Math.hypot(h.pos[0]-c.pos[0], h.pos[2]-c.pos[2]), dy=Math.abs(h.pos[1]-c.pos[1]);
  if (xz>0.3 || dy>1) f.push("host/client apart by "+xz.toFixed(2)+" m across, "+dy.toFixed(2)+" m up");
}
const p=v=>v.present?JSON.stringify(v.pos.map(x=>+x.toFixed(2))):"missing";
console.log("   host "+p(h)+", client "+p(c)+", ground "+surface.toFixed(2)+", record y "+r.y.toFixed(2));
if (f.length) { console.log("FAIL: "+f.join("; ")); process.exit(1);} console.log("PASS");' "$r" "$h" "$c" "$surface"
