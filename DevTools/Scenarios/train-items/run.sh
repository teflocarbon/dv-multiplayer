#!/usr/bin/env bash
# Items on a moving train: a player carries a mug into a loco cab and drops or throws it (before or
# while the train moves); the host pushes the loco along the track. On both peers the mug must stay
# in the cab, attached to the loco, at the same cab position, and not fall through.
#   WHO=client|host (default client)  MODE=drop|throw (default drop)  WHEN=stopped|moving (default stopped)
#   SPEED=m/s (default 8)
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/train-items
WHO=${WHO:-client}; MODE=${MODE:-drop}; WHEN=${WHEN:-stopped}; SPEED=${SPEED:-8}
U="using Multiplayer.Components.Networking.World;"
step() { local out; out=$("$@" 2>&1) || { echo "$out" | head -20; return 1; }; if echo "$out" | grep -q "\"ok\": false|compileError"; then echo "$out" | grep -E "error|compileError" | head -5; return 1; fi; }
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }

echo "== host spawns a DE2"
loco=$($D eval -t host -m coroutine 'var car = Trains.SpawnNearPlayer("LocoDE2"); for (int i = 0; i < 120; i++) yield return null; ctx.Result = new { id = car.ID };' | field id)
echo "   $loco"
cleanup() { $D eval -t host -m body "Trains.Delete(Trains.Find(\"$loco\")); return 0;" >/dev/null 2>&1 || true; }
trap cleanup EXIT
for t in host client; do step $D eval -t $t -m coroutine -a loco=$loco 'yield return Trains.Board(Trains.Find(ctx.Args["loco"]));'; done

push() { step $D eval -t host -m coroutine --timeout 90 -a loco=$loco -a speed=$SPEED 'yield return Trains.Push(Trains.Find(ctx.Args["loco"]), float.Parse(ctx.Args["speed"]), 6f);'; }
if [ "$WHEN" = moving ]; then push & fi
mug=$($D eval -t host -m coroutine -u "$U" -a who=$WHO -f $P/spawn-mug.cs | field item)
echo "== $WHO ${MODE}s a mug in the cab ($WHEN)"
item=$($D eval -t $WHO -m coroutine --timeout 90 -u "$U" -a loco=$loco -a item=$mug -a mode=$MODE -f $P/carry.cs | field item)
echo "   mug $item"
if [ "$WHEN" = moving ]; then wait; else echo "== host pushes the train"; push; fi
sleep 3

h=$($D eval -t host -m body -u "$U" -a loco=$loco -a item=$item -f $P/verify.cs)
c=$($D eval -t client -m body -u "$U" -a loco=$loco -a item=$item -f $P/verify.cs)
node -e '
const val=s=>Object.values(JSON.parse(s).results)[0].result;
const h=val(process.argv[1]), c=val(process.argv[2]); const f=[];
for (const [n,v] of [["host",h],["client",c]]) {
  if (!v.present) { f.push(n+": mug missing"); continue; }
  if (!v.inLoco) f.push(n+": mug not attached to the loco ("+v.state+")");
  if (v.local[1] < 0.5) f.push(n+": mug below the cab floor (local y "+v.local[1].toFixed(2)+")");
}
if (h.present && c.present) { const d=Math.hypot(...h.local.map((x,i)=>x-c.local[i])); if (d>0.3) f.push("host/client cab positions differ by "+d.toFixed(2)+" m"); }
if (h.record && (h.record.placement!=="TrainInterior" || h.record.parentNetId!==h.locoNetId)) f.push("host record: "+JSON.stringify(h.record));
console.log("   host "+JSON.stringify(h.local&&h.local.map(x=>+x.toFixed(2)))+" client "+JSON.stringify(c.local&&c.local.map(x=>+x.toFixed(2)))+" record "+JSON.stringify(h.record));
if (f.length) { console.log("FAIL: "+f.join("; ")); process.exit(1);} console.log("PASS");' "$h" "$c"
