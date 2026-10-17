#!/usr/bin/env bash
# A derailed car off the ballast, kilometres from the host's body. The server keeps every Near tile
# loaded (the track ballast) but not the terrain beside the track, so a car that ends up off the
# ballast there has nothing under it on the host. It must be frozen where it is (kinematic) rather
# than fall through the map: ServerWorldCoverage lets a derailed car move only with ground under
# both ends, and checks again a few times a second.
# Where the host does have ground there (a dedicated host loads the terrain around the player), it
# skips. The client's copy isn't checked: the client has terrain there and its copy rests on it,
# below the host's frozen one, until the server also loads terrain around derailed cars.
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/far-derail-embankment
F=DevTools/Scenarios/far-derail
U="using Multiplayer.Components.Networking;"
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
fail() { echo "FAIL: $*"; exit 1; }

# A station office 4-8 km from the host's body.
s=$($D eval -t host -m body 'var body = PlayerManager.PlayerTransform.position; var st = StationController.allStations.Select(x => new { x, d = Vector3.Distance(x.transform.position, body) }).Where(x => x.d > 4000 && x.d < 8000).OrderBy(x => x.d).First(); var a = Dev.Abs(st.x.transform.position); return new { station = st.x.stationInfo.YardID, x = a.x, z = a.z, d = Math.Round(st.d) };') || fail "no station 4-8 km from the host's body: $(echo "$s" | grep -o '"error": "[^"]*"' | head -1)"
echo "== client goes to $(echo "$s" | field station), $(echo "$s" | field d) m from the host's body"
$D eval -t client -m coroutine --timeout 120 -a x=$(echo "$s" | field x) -a z=$(echo "$s" | field z) \
  'yield return LocalPlayer.TeleportToGround(float.Parse(ctx.Args["x"], System.Globalization.CultureInfo.InvariantCulture), float.Parse(ctx.Args["z"], System.Globalization.CultureInfo.InvariantCulture)); yield return new WaitForSeconds(15);' >/dev/null

c=$($D eval -t host -m coroutine --timeout 150 -u "$U" -f $F/spawn.cs) || fail "spawn: $(echo "$c" | grep -o '"error": "[^"]*"' | head -1)"
car=$(echo "$c" | field id)
cleanup() { $D eval -t host -m body -a id=$car 'Trains.Delete(Trains.Find(ctx.Args["id"])); return 0;' >/dev/null 2>&1 || true; }
trap cleanup EXIT
echo "   spawned $car next to $(echo "$c" | field player)"
[ "$(echo "$c" | field ground)" = "true" ] || fail "no ground under the car on the host after $(echo "$c" | field waited) s"

echo "== host derails $car and lets it settle on the ballast"
$D eval -t host -m body -a car=$car 'var car = Trains.Find(ctx.Args["car"]); car.Derail(); return new { derailed = car.derailed };' >/dev/null
sleep 8
o=$($D eval -t host -m body -a car=$car -f $P/offset.cs) || fail "offset: $(echo "$o" | grep -o '"error": "[^"]*"' | head -1)"
[ "$(echo "$o" | field found)" = "true" ] || { echo "SKIP: the host has ground everywhere beside the track here"; exit 0; }
y0=$(echo "$o" | field y)
echo "== moved it $(echo "$o" | field moved) m off the track, where the host has no ground; waiting 10 s"
sleep 10
h=$($D eval -t host -m body -a car=$car -f $F/state.cs)
drop=$(node -e 'console.log((+process.argv[1] - JSON.parse(process.argv[2]).y).toFixed(2))' "$y0" "$(echo "$h" | field abs)")
echo "   host: derailed $(echo "$h" | field derailed), kinematic $(echo "$h" | field kinematic), ground $(echo "$h" | field groundCollider), dropped $drop m"
[ "$(echo "$h" | field derailed)" = "true" ] || fail "rerailed on the host"
[ "$(echo "$h" | field kinematic)" = "true" ] || fail "the host left the car dynamic with nothing under it"
node -e 'process.exit(+process.argv[1] < 2 ? 0 : 1)' "$drop" || fail "the car fell $drop m on the host before it was frozen"
echo PASS
