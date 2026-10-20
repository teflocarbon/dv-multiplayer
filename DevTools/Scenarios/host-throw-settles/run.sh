#!/usr/bin/env bash
# The host throws an item itself (no hand-off to a client): the host simulates it under its own
# spatial lease and commits where it comes to rest. It must settle at the same spot in both games,
# with the same revision, no lease left open, and the host's copy's revision never going back.
# Nothing about it may reach the host's own player: the server leaves it out of the lease, samples
# and commit (its session drops them if they come, LocalPlayerSession.Drop, and that counts as a
# failure here). This is the host-follower family (docs/design/host-local-session.md, stages 1-3):
# the host applying what the server sent for remote players to the host's own world.
#   PREFAB=item prefab (default Mug)
# Exit code 0 = pass. Needs a running pair (dvmp up). Works on a dedicated host only if its parked
# body can throw, so it isn't run there.
set -euo pipefail
cd "$(dirname "$0")/../../.."
dvmp_bin=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}; D=dvmp_checked
dvmp_checked() { local out; if ! out=$("$dvmp_bin" "$@"); then echo "FAIL: dvmp ${*:1:3} failed: ${out:-no output}" >&2; return 1; fi; printf '%s\n' "$out"; }
P=DevTools/Scenarios/host-throw-settles
PREFAB=${PREFAB:-Mug}
U="using Multiplayer.Server.Items; using Multiplayer.Shared.Items; using Multiplayer.Shared.Jobs; using Multiplayer.Shared.Stations; using Multiplayer.Shared.World;"
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
val() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}console.log(JSON.stringify(x.result))'; }

$D eval -t host -m class -u "$U" -a action=install -f $P/ignored.cs >/dev/null
item=
cleanup() {
  "$dvmp_bin" eval -t host -m class -u "$U" -a action=remove -f $P/ignored.cs >/dev/null 2>&1 || true
  # Don't leave the host's mug behind: host-player-persist counts the host's items.
  [ -n "$item" ] && "$dvmp_bin" eval -t host -m body -u "$U" -a item=$item 'if (NetworkedItem.TryGet(ushort.Parse(ctx.Args["item"]), out NetworkedItem n) && n != null) UnityEngine.Object.Destroy(n.gameObject); return 0;' >/dev/null 2>&1 || true
}
trap cleanup EXIT

echo "== the client comes over to watch"
at=$($D eval -t host -m body 'var p = Dev.Abs(PlayerManager.PlayerTransform.position); return new { x = p.x, z = p.z };' | val)
x=$(echo "$at" | node -e 'console.log(JSON.parse(require("fs").readFileSync(0,"utf8")).x)')
z=$(echo "$at" | node -e 'console.log(JSON.parse(require("fs").readFileSync(0,"utf8")).z)')
$D eval -t client -m coroutine -a x=$x -a z=$z 'yield return LocalPlayer.TeleportToGround(float.Parse(ctx.Args["x"]) - 6f, float.Parse(ctx.Args["z"]));' >/dev/null

item=$($D eval -t host -m coroutine -u "$U" -a prefab=$PREFAB -f $P/spawn.cs | field item)
echo "== host throws $PREFAB $item"
t=$($D eval -t host -m coroutine --timeout 90 -u "$U" -a item=$item -f $P/throw.cs | val)
echo "   $t"
sleep 2
h=$($D eval -t host -m body -u "$U" -a item=$item -f $P/verify.cs | val)
c=$($D eval -t client -m body -u "$U" -a item=$item -f $P/verify.cs | val)
ig=$($D eval -t host -m class -u "$U" -a action=read -f $P/ignored.cs | val)
node -e '
const [t,h,c,ig]=process.argv.slice(1).map(s=>JSON.parse(s)); const f=[];
if (!t.leaseSeen) f.push("the throw never opened a spatial lease");
if (!t.settled) f.push("the host never saw it settle ("+t.seconds.toFixed(1)+" s)");
if (t.wentBack) f.push("the host copy'"'"'s revision went back "+t.wentBack+" time(s): "+t.revisions);
for (const [n,v] of [["host",h],["client",c]]) {
  if (!v.present) { f.push(n+": item missing"); continue; }
  if (v.leased) f.push(n+": a spatial lease is still open");
  if (v.state!=="Dropped") f.push(n+": state "+v.state);
}
if (h.present && h.recordPlacement!=="World") f.push("host record: "+h.recordPlacement);
if (h.present && h.recordPos) { const d=Math.hypot(...h.pos.map((x,i)=>x-h.recordPos[i])); if (d>0.3) f.push("host copy is "+d.toFixed(2)+" m from its record"); }
if (h.present && c.present) {
  const d=Math.hypot(...h.pos.map((x,i)=>x-c.pos[i])); if (d>0.3) f.push("host/client positions differ by "+d.toFixed(2)+" m");
  if (h.revision!==c.revision) f.push("revisions differ: host "+h.revision+", client "+c.revision);
}
if (ig.items) f.push("the host'"'"'s own client was sent item packets it dropped: "+ig.byType);
const p=v=>v.present?JSON.stringify(v.pos.map(x=>+x.toFixed(2)))+" r"+v.revision:"missing";
console.log("   host "+p(h)+" ("+h.recordPlacement+" r"+h.recordRevision+"), client "+p(c)+"; host client dropped: "+(ig.byType||"nothing"));
if (f.length) { console.log("FAIL: "+f.join("; ")); process.exit(1);} console.log("PASS");' "$t" "$h" "$c" "$ig"
