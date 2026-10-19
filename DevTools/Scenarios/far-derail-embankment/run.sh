#!/usr/bin/env bash
# A derailed car off the track ballast, kilometres from the host's body. The server loads terrain
# around every remote player and every derailed car (ServerTerrainCoverage), as well as every Near
# tile (ServerWorldCoverage), so:
# - the host has terrain under the client's player;
# - a derailed car the host puts beside the track, 3 m above the terrain, falls onto it and comes
#   to rest on the host, and the client's copy rests where the host's does.
# Before that, the host froze such a car in mid-air (no terrain there), and the client's copy fell
# to the client's terrain below it.
# Exit code 0 = pass. Needs a running pair (dvmp up, or dvmp up --dedicated).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/far-derail-embankment
F=DevTools/Scenarios/far-derail
U="using Multiplayer.Client.Session; using Multiplayer.Server.Coverage; using Multiplayer.Server.Dedicated; using Multiplayer.Shared.Session;"
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
fail() { echo "FAIL: $*"; exit 1; }

# Open line 4-8 km from the host's body, away from stations.
s=$($D eval -t host -m body -f $P/place.cs) || fail "no open line 4-8 km from the host's body: $(echo "$s" | grep -o '"error": "[^"]*"' | head -1)"
echo "== client goes to open line at $(echo "$s" | field x), $(echo "$s" | field z), $(echo "$s" | field d) m from the host's body"
$D eval -t client -m coroutine --timeout 120 -a x=$(echo "$s" | field x) -a z=$(echo "$s" | field z) \
  'yield return LocalPlayer.TeleportToGround(float.Parse(ctx.Args["x"], System.Globalization.CultureInfo.InvariantCulture), float.Parse(ctx.Args["z"], System.Globalization.CultureInfo.InvariantCulture)); yield return new WaitForSeconds(15);' >/dev/null

t=$($D eval -t host -m body -u "$U" -f $P/terrain-at.cs) || fail "terrain probe: $(echo "$t" | grep -o '"error": "[^"]*"' | head -1)"
echo "   host, under the client ($(echo "$t" | field fromHostBody) m from the host's body): top $(echo "$t" | field top), terrain $(echo "$t" | field terrain), Far tile loaded $(echo "$t" | field farLoaded)"
[ "$(echo "$t" | field farLoaded)" = "true" ] || fail "the host hasn't loaded the Far scenery tile under the client's player"
[ "$(echo "$t" | field terrain)" != "" ] && [ "$(echo "$t" | field terrain)" != "null" ] || fail "the host has no terrain under the client's player"

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
[ "$(echo "$o" | field found)" = "true" ] || fail "no terrain beside the track on the host, 15-80 m either side"
echo "== moved it $(echo "$o" | field moved) m off the track, 3 m above the host's terrain ($(echo "$o" | field terrain)); waiting 12 s"
sleep 12
h=$($D eval -t host -m body -a car=$car -f $F/state.cs); cl=$($D eval -t client -m body -a car=$car -f $F/state.cs)
apart=$(node -e 'const a=JSON.parse(process.argv[1]),b=JSON.parse(process.argv[2]);console.log(Math.hypot(a.x-b.x,a.y-b.y,a.z-b.z).toFixed(2))' "$(echo "$h" | field abs)" "$(echo "$cl" | field abs)")
echo "   host: derailed $(echo "$h" | field derailed), kinematic $(echo "$h" | field kinematic), ground $(echo "$h" | field groundCollider), speed $(echo "$h" | field speed)"
echo "   client: derailed $(echo "$cl" | field derailed), kinematic $(echo "$cl" | field kinematic), ground $(echo "$cl" | field groundCollider), $apart m from the host's"
[ "$(echo "$h" | field derailed)" = "true" ] || fail "rerailed on the host"
[ "$(echo "$h" | field kinematic)" = "false" ] || fail "the host froze the car instead of letting it land on its terrain"
[ "$(echo "$h" | field ground)" = "true" ] || fail "no ground under the car on the host"
node -e 'process.exit(+process.argv[1] < 0.5 ? 0 : 1)' "$(echo "$h" | field speed)" || fail "the car hasn't come to rest on the host"
node -e 'process.exit(+process.argv[1] < 1.5 ? 0 : 1)' "$apart" || fail "client car $apart m from the host's"
echo PASS
