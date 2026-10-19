#!/usr/bin/env bash
# Rerail (playtest 2, FEF-3 L-069): after a rerail the client's copy slid off at 1.5 m/s and was
# snapped back and flung every ~1.3 s, forever, while the host had it still. Its followers still
# aimed at where the car had been: the rigidbody queue at its derailed pose, the bogie queues at
# samples from before the derail. The host derails a car beside the client and shoves it off the
# track; once it's at rest the car is rerailed SHIFT metres along its track, by the client through
# the host (as the comms radio does; WHO=client) or by the host itself (WHO=host). For DURATION seconds after
# that the client's car must stay within MAX_OFF metres of the host's and not move.
#   LIVERY (default LocoDE2), WHO (client), SHIFT (12), DURATION (8), MAX_OFF (0.5)
# Exit code 0 = pass. Needs a running pair (dvmp up, or dvmp up --dedicated).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/rerail
LIVERY=${LIVERY:-LocoDE2}; WHO=${WHO:-client}; SHIFT=${SHIFT:-12}; DURATION=${DURATION:-8}; MAX_OFF=${MAX_OFF:-0.5}
U="using Multiplayer.Components.Networking; using Multiplayer.Components.Networking.Train; using Multiplayer.Components.Networking.World;"
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
fail() { echo "FAIL: $*"; exit 1; }
T=$(mktemp -d)

s=$($D eval -t host -m coroutine --timeout 60 -a livery=$LIVERY -a shift=$SHIFT -f $P/setup.cs) || fail "setup: $(echo "$s" | grep -o '"error": "[^"]*"' | head -1)"
car=$(echo "$s" | field id); track=$(echo "$s" | field track)
cleanup() { $D eval -t host -m body -a id=$car 'Trains.Delete(Trains.Find(ctx.Args["id"])); return 0;' >/dev/null 2>&1 || true; rm -rf "$T"; }
trap cleanup EXIT
pos="-a x=$(echo "$s" | field x) -a y=$(echo "$s" | field y) -a z=$(echo "$s" | field z) -a fx=$(echo "$s" | field fx) -a fy=$(echo "$s" | field fy) -a fz=$(echo "$s" | field fz)"
echo "== spawned $LIVERY $car beside the client; it'll be rerailed $(echo "$s" | field shift) m along its track"
sleep 3

echo "== host derails $car and shoves it off the track"
$D eval -t host -m body -a car=$car 'var car = Trains.Find(ctx.Args["car"]); car.Derail(); car.rb.AddForce(car.transform.right * 3f + Vector3.up, ForceMode.VelocityChange); return 0;' >/dev/null
sleep 12
c=$($D eval -t client -m body -a car=$car 'var car = Trains.Find(ctx.Args["car"]); return new { derailed = car.derailed, kinematic = car.rb.isKinematic };')
echo "   client: derailed $(echo "$c" | field derailed), kinematic $(echo "$c" | field kinematic)"
[ "$(echo "$c" | field derailed)" = "true" ] || fail "not derailed on the client"

echo "== $WHO rerails it"
for t in host client; do
  $D eval -t $t -m coroutine --timeout $((DURATION + 30)) -a car=$car -a seconds=$((DURATION + 1)) -f $P/sample.cs >"$T/$t.json" &
done
sleep 0.5
F='float A(string k) => float.Parse(ctx.Args[k], System.Globalization.CultureInfo.InvariantCulture); var car = Trains.Find(ctx.Args["car"]); var p = new Vector3(A("x"), A("y"), A("z")); var f = new Vector3(A("fx"), A("fy"), A("fz"));'
if [ "$WHO" = client ]; then
  # What the comms radio's rerail does on a client (RerailControllerPatch).
  $D eval -t client -m body -u "$U" -a car=$car -a track=$track $pos "$F"' NetworkedTrainCar.TryGetFromTrainCar(car, out var n); NetworkLifecycle.Instance.Client.SendTrainRerailRequest(n.NetId, ushort.Parse(ctx.Args["track"]), p, f); return 0;' >/dev/null
else
  $D eval -t host -m body -u "$U" -a car=$car -a track=$track $pos "$F"' NetworkedRailTrack.TryGet(ushort.Parse(ctx.Args["track"]), out var t); car.Rerail(t.RailTrack, p + WorldMover.currentMove, f); return 0;' >/dev/null
fi
wait

# The host's car is at rest from the rerail on: its last sample is where it is. Compare the client's
# samples with it once the rerail has had 2 s to land.
node -e '
const fs=require("fs");
const read=f=>{const o=JSON.parse(fs.readFileSync(f,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(f,o.compileError||JSON.stringify(x));process.exit(2)}return x.result.samples};
const h=read(process.argv[1]),c=read(process.argv[2]),maxOff=+process.argv[3];
const ref=h[h.length-1],settled=s=>s.t>=2.5;
const dist=s=>Math.hypot(s.x-ref.x,s.y-ref.y,s.z-ref.z);
const hs=h.filter(settled),cs=c.filter(settled);
const hMove=Math.max(...hs.map(s=>Math.hypot(s.x-ref.x,s.y-ref.y,s.z-ref.z)));
const off=Math.max(...cs.map(dist)),v=Math.max(...cs.map(s=>s.v));
const placing=cs.filter(s=>s.placing).length,kin=cs.filter(s=>s.kinematic).length;
console.log(`   host: derailed ${ref.derailed}, moved ${hMove.toFixed(2)} m after the rerail landed`);
console.log(`   client: derailed ${c[c.length-1].derailed}, up to ${off.toFixed(2)} m from the host and ${v.toFixed(2)} m/s; placing in ${placing}/${cs.length} samples, kinematic in ${kin}`);
console.log("   client distance by second: "+[...Array(Math.ceil(c[c.length-1].t)).keys()].map(k=>{const g=c.filter(s=>Math.floor(s.t)===k);return g.length?Math.max(...g.map(dist)).toFixed(2):"-"}).join(" "));
if(ref.derailed){console.log("FAIL: the host did not rerail the car"+(process.argv[4]==="client"?" (the request was refused: money?)":""));process.exit(1)}
if(hMove>0.2){console.log("FAIL: the host car moved after the rerail");process.exit(1)}
if(c[c.length-1].derailed){console.log("FAIL: still derailed on the client");process.exit(1)}
if(off>maxOff){console.log(`FAIL: client car ${off.toFixed(2)} m from the host`);process.exit(1)}
if(v>0.5){console.log(`FAIL: client car moving at ${v.toFixed(2)} m/s`);process.exit(1)}
' "$T/host.json" "$T/client.json" "$MAX_OFF" "$WHO" || exit 1
echo PASS
