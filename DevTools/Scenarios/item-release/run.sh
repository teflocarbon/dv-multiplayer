#!/usr/bin/env bash
# What happens to an item once it leaves a player's hand (playtest 2: VR hand swaps dropping items,
# thrown lanterns snapping back, jittering for others, and vanishing when thrown onto a train).
#   MODE=regrab (default)  the client lets go of a mug and takes it again the next frame, as a VR
#                          hand swap does: it must stay in the hand, and no drop reaches the server
#   MODE=regrab-late       the client takes a lantern again 1 s after letting go, while the
#                          server's copy's flame keeps changing: the pickup must still be taken
#   MODE=throw-flatcar     the client throws a lantern from the ground onto a flatcar while its
#                          flame changes on the server; the host then pushes the car. The lantern
#                          must be on the car, at the same spot, in both games
#   MODE=object-state      the client changes the wick of a lantern lying in the world: nothing
#                          is sent, so the server corrects nothing
#   MODE=interact          the client turns the knob of a lantern lying in the world (grabbing it,
#                          as a VR hand does): the server takes the new wick, knob included
# Every mode fails if the server sent the client a rejection correction for the item.
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/item-release
MODE=${MODE:-regrab}
U="using Multiplayer.Server.Items; using Multiplayer.Shared.Items; using Multiplayer.Shared.Jobs; using Multiplayer.Shared.Stations; using Multiplayer.Shared.World;"
step() { local out; out=$("$@" 2>&1) || { echo "$out" | head -20; return 1; }; if echo "$out" | grep -q "\"ok\": false\|compileError"; then echo "$out" | grep -E "error|compileError" | head -5; return 1; fi; }
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
val() { node -e 'console.log(JSON.stringify(Object.values(JSON.parse(require("fs").readFileSync(0,"utf8")).results)[0].result))'; }
fail() { echo "FAIL: $*"; exit 1; }

step $D eval -t host -m class -u "$U" -a action=install -f $P/corrections.cs
car=
cleanup() {
  $D eval -t host -m class -u "$U" -a action=remove -f $P/corrections.cs >/dev/null 2>&1 || true
  [ -n "$car" ] && $D eval -t host -m body "Trains.Delete(Trains.Find(\"$car\")); return 0;" >/dev/null 2>&1 || true
}
trap cleanup EXIT
client_id=$($D eval -t client -m body 'return new { id = (int)Multiplayer.Client.Session.NetworkClient.Instance.PlayerId };' | field id)

case $MODE in
regrab|regrab-late)
  if [ "$MODE" = regrab ]; then prefab=Mug; delay=0; else prefab=Lantern; delay=1; fi
  item=$($D eval -t host -m coroutine -u "$U" -a prefab=$prefab -f $P/spawn.cs | field item)
  echo "== client lets go of $prefab $item and takes it again after ${delay}s"
  [ "$MODE" = regrab-late ] && $D eval -t host -m coroutine --timeout 60 -u "$U" -a item=$item -a seconds=3 -f $P/flicker.cs >/dev/null &
  r=$($D eval -t client -m coroutine --timeout 90 -u "$U" -a item=$item -a delay=$delay -f $P/regrab.cs | val)
  wait
  rec=$($D eval -t host -m body -u "$U" -a item=$item -f $P/record.cs | val)
  echo "   client $r; server $rec"
  node -e '
    const r=JSON.parse(process.argv[1]), rec=JSON.parse(process.argv[2]), me=+process.argv[3], quick=process.argv[4]==="regrab"; const f=[];
    if (!r.held) f.push("the client is no longer holding it");
    if (rec.placement!=="PlayerHand" || rec.player!==me) f.push("server has it "+rec.placement+" (player "+rec.player+")");
    if (quick && rec.revision!==r.heldRevision) f.push("revision moved "+r.heldRevision+" -> "+rec.revision+": the release went out as a drop");
    if (f.length) { console.log("FAIL: "+f.join("; ")); process.exit(1); }' "$r" "$rec" "$client_id" "$MODE" || exit 1
  ;;
throw-flatcar)
  flat=$($D eval -t host -m body 'return new { id = DV.Globals.G.Types.Liveries.First(l => l.id.StartsWith("Flatbed")).id };' | field id)
  car=$($D eval -t host -m coroutine "var c = Trains.SpawnNearPlayer(\"$flat\"); for (int i = 0; i < 120; i++) yield return null; ctx.Result = new { id = c.ID };" | field id)
  item=$($D eval -t host -m coroutine -u "$U" -a prefab=Lantern -f $P/spawn.cs | field item)
  echo "== client throws lantern $item onto $flat $car"
  $D eval -t host -m coroutine --timeout 60 -u "$U" -a item=$item -a seconds=3 -f $P/flicker.cs >/dev/null &
  step $D eval -t client -m coroutine --timeout 90 -u "$U" -a item=$item -a car=$car -f $P/throw.cs
  wait
  check() {
    local h c rec
    h=$($D eval -t host -m body -u "$U" -a item=$item -a car=$car -f $P/where.cs | val)
    c=$($D eval -t client -m body -u "$U" -a item=$item -a car=$car -f $P/where.cs | val)
    rec=$($D eval -t host -m body -u "$U" -a item=$item -f $P/record.cs | val)
    node -e '
      const h=JSON.parse(process.argv[1]), c=JSON.parse(process.argv[2]), rec=JSON.parse(process.argv[3]), when=process.argv[4]; const f=[];
      for (const [n,v] of [["host",h],["client",c]]) {
        if (!v.present) { f.push(n+": lantern missing"); continue; }
        if (!v.onCar) f.push(n+": lantern not on the car ("+v.state+")");
        if (v.local[1] < 0.5) f.push(n+": lantern below the deck (local y "+v.local[1].toFixed(2)+")");
      }
      if (h.present && c.present) { const d=Math.hypot(...h.local.map((x,i)=>x-c.local[i])); if (d>0.3) f.push("host/client positions differ by "+d.toFixed(2)+" m"); }
      if (rec.placement!=="TrainInterior" || rec.parentNetId!==h.carNetId) f.push("server record "+JSON.stringify(rec));
      console.log("   "+when+": host "+JSON.stringify(h.local&&h.local.map(x=>+x.toFixed(2)))+" client "+JSON.stringify(c.local&&c.local.map(x=>+x.toFixed(2)))+" record "+rec.placement);
      if (f.length) { console.log("FAIL ("+when+"): "+f.join("; ")); process.exit(1); }' "$h" "$c" "$rec" "$1" || exit 1
  }
  check landed
  echo "== host pushes the car"
  step $D eval -t host -m coroutine --timeout 90 -a car=$car 'yield return Trains.Push(Trains.Find(ctx.Args["car"]), 5f, 4f);'
  sleep 2
  check moved
  ;;
object-state)
  item=$($D eval -t host -m coroutine -u "$U" -a prefab=Lantern -f $P/spawn.cs | field item)
  sleep 3
  before=$($D eval -t host -m body -u "$U" -a item=$item -f $P/record.cs | field revision)
  echo "== client changes the wick of lantern $item lying in the world"
  step $D eval -t client -m coroutine --timeout 60 -u "$U" -a item=$item -f $P/nudge.cs
  after=$($D eval -t host -m body -u "$U" -a item=$item -f $P/record.cs | field revision)
  [ "$before" = "$after" ] || fail "server revision moved $before -> $after: a non-holder's object state was taken"
  ;;
interact)
  item=$($D eval -t host -m coroutine -u "$U" -a prefab=Lantern -f $P/spawn.cs | field item)
  sleep 3
  wick=$($D eval -t host -m body -u "$U" -a item=$item 'NetworkedItem.TryGet(ushort.Parse(ctx.Args["item"]), out var i); return new { w = i.GetComponent<Lantern>().wickSize < 0.5f ? 0.85f : 0.15f };' | field w)
  echo "== client turns the knob of lantern $item lying in the world to $wick"
  c=$($D eval -t client -m coroutine --timeout 60 -u "$U" -a item=$item -a wick=$wick -f $P/interact.cs | val)
  h=$($D eval -t host -m body -u "$U" -a item=$item 'NetworkedItem.TryGet(ushort.Parse(ctx.Args["item"]), out var i); return new { wick = i.GetComponent<Lantern>().wickSize, knob = i.GetComponent<Lantern>().knob.Value };' | val)
  echo "   client $c; server $h"
  node -e '
    const c=JSON.parse(process.argv[1]), h=JSON.parse(process.argv[2]), w=+process.argv[3]; const f=[];
    if (Math.abs(c.wick-w)>0.05) f.push("the client'"'"'s own wick is "+c.wick+" (set back?)");
    if (Math.abs(h.wick-w)>0.05) f.push("the server'"'"'s wick is "+h.wick+", not "+w);
    if (Math.abs(h.knob-w)>0.05) f.push("the server'"'"'s knob is at "+h.knob+": its next movement would undo the wick");
    if (f.length) { console.log("FAIL: "+f.join("; ")); process.exit(1); }' "$c" "$h" "$wick" || exit 1
  ;;
*) fail "unknown MODE $MODE" ;;
esac

n=$($D eval -t host -m class -u "$U" -a action=read -a item=$item -f $P/corrections.cs | val)
node -e 'const n=JSON.parse(process.argv[1]); if (n.count>0) { console.log("FAIL: the server sent "+n.count+" rejection correction(s): "+n.reasons); process.exit(1); }' "$n" || exit 1
echo PASS
