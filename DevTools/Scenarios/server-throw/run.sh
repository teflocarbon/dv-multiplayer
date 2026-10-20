#!/usr/bin/env bash
# The host throws an item itself, naming a player as the throw's actor, as the job re-roll throws a
# binned job overview back out: the actor drops a mug in a loco cab and, while the lease of that
# drop is still open, the host throws it out of the side of the loco into the world. Its flight
# must survive:
#   - the transition indexes the mug in its world cell at once (moved from a car interior into the
#     world it used to be in no cell until a spatial sample came, and every player's interest
#     retired it and re-created it at rest);
#   - the throw keeps its force when the actor is the host's own player (granting the new lease
#     used to stop the body under the old one, losing GrabHandlerItem.Throw's one-step force).
# Both games must then have it at the same spot outside the loco, no lease open, the client's copy
# never retired and re-created.
#   ACTOR=host|client (default host). ACTOR=host needs a player host (the host's body boards).
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
dvmp_bin=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}; D=dvmp_checked
dvmp_checked() { local out; if ! out=$("$dvmp_bin" "$@"); then echo "FAIL: dvmp ${*:1:3} failed: ${out:-no output}" >&2; return 1; fi; printf '%s\n' "$out"; }
P=DevTools/Scenarios/server-throw
ACTOR=${ACTOR:-host}
U="using Multiplayer.Server.Items; using Multiplayer.Server.Session; using Multiplayer.Shared.Items; using Multiplayer.Shared.Session; using Multiplayer.Shared.Protocol.Data.Items;"
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
val() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}console.log(JSON.stringify(x.result))'; }

echo "== host spawns a DE2"
loco=$($D eval -t host -m coroutine 'var car = Trains.SpawnNearPlayer("LocoDE2"); for (int i = 0; i < 120; i++) yield return null; ctx.Result = new { id = car.ID };' | field id)
echo "   $loco"
mug=
cleanup() {
  [ -n "$mug" ] && "$dvmp_bin" eval -t host -m body -a item=$mug 'if (Multiplayer.Shared.Items.NetworkedItem.TryGet(ushort.Parse(ctx.Args["item"]), out var n) && n != null) UnityEngine.Object.Destroy(n.gameObject); return 0;' >/dev/null 2>&1 || true
  "$dvmp_bin" eval -t host -m body "Trains.Delete(Trains.Find(\"$loco\")); return 0;" >/dev/null 2>&1 || true
}
trap cleanup EXIT
# Both on board: the onlooker is near enough to have the mug in its interest all along.
for t in host client; do $D eval -t $t -m coroutine -a loco=$loco 'yield return Trains.Board(Trains.Find(ctx.Args["loco"]));' >/dev/null; done

mug=$($D eval -t host -m coroutine -u "$U" -a who=$ACTOR -f DevTools/Scenarios/train-items/spawn-mug.cs | field item)
before=$($D eval -t client -m body -u "$U" -a item=$mug -f $P/verify.cs | val)
echo "== $ACTOR drops mug $mug in the cab, the host throws it out under the drop's lease"
thrown=$(mktemp)
"$dvmp_bin" eval -t host -m coroutine --timeout 120 -u "$U" -a item=$mug -a loco=$loco -a actor=$ACTOR -f $P/throw.cs >"$thrown" &
watcher=$!
$D eval -t $ACTOR -m coroutine --timeout 90 -u "$U" -a loco=$loco -a item=$mug -f $P/drop.cs >/dev/null
wait $watcher || { cat "$thrown"; exit 1; }
t=$(val <"$thrown"); rm -f "$thrown"
echo "   $t"
sleep 3
h=$($D eval -t host -m body -u "$U" -a item=$mug -f $P/verify.cs | val)
c=$($D eval -t client -m body -u "$U" -a item=$mug -f $P/verify.cs | val)
node -e '
const [t,b,h,c]=process.argv.slice(1).map(s=>JSON.parse(s)); const f=[];
if (!t.leaseLiveAtThrow) f.push("no lease from the drop was open when the host threw it ("+t.droppedFor.toFixed(1)+" s after it was in the cab): the throw didn'"'"'t supersede one, so this run shows nothing about its force");
if (!t.indexedOnCommit) f.push("the transition left the mug in no world cell");
// Rescued from below the map, it was put back where it was thrown and fell straight down from there:
// where it lies says nothing about the force of the throw.
const rescued = h.present && h.lastMove==="RescuedFromBelowMap";
if (rescued) f.push("the mug fell through the ground after flying "+t.travel.toFixed(2)+" m sideways and was rescued from below the map");
else if (t.travel < 1.5) f.push("the host'"'"'s copy went "+t.travel.toFixed(2)+" m sideways: the throw lost its force");
for (const [n,v] of [["host",h],["client",c]]) {
  if (!v.present) { f.push(n+": mug missing"); continue; }
  if (v.leased) f.push(n+": a spatial lease is still open");
  if (v.state!=="Dropped") f.push(n+": state "+v.state);
  const d=Math.hypot(v.pos[0]-t.start[0], v.pos[2]-t.start[2]); if (d<1.5 && !rescued) f.push(n+": came down "+d.toFixed(2)+" m from where it was thrown");
}
if (h.present && h.recordPlacement!=="World") f.push("host record: "+h.recordPlacement);
if (b.present && c.present && b.instance!==c.instance) f.push("the client'"'"'s copy was retired and re-created");
if (h.present && c.present) {
  const d=Math.hypot(...h.pos.map((x,i)=>x-c.pos[i])); if (d>0.3) f.push("host/client positions differ by "+d.toFixed(2)+" m");
  if (h.revision!==c.revision) f.push("revisions differ: host "+h.revision+", client "+c.revision);
}
const p=v=>v.present?JSON.stringify(v.pos.map(x=>+x.toFixed(2)))+" r"+v.revision:"missing";
console.log("   flew "+t.travel.toFixed(2)+" m (rose "+t.rise.toFixed(2)+" m); host "+p(h)+" ("+h.recordPlacement+", last move "+h.lastMove+"), client "+p(c));
if (f.length) { console.log("FAIL: "+f.join("; ")); process.exit(1);} console.log("PASS");' "$t" "$before" "$h" "$c"
