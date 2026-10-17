#!/usr/bin/env bash
# A derailment next to the player, kilometres from a dedicated host's parked body. DV only loads
# terrain and scenery around one point, and freezes a derailed car (kinematic) where nothing is
# loaded, so it used to hang in mid-air on the host. The host's world follows the player now
# (DedicatedHostAnchor), so the car must come to rest on the ground on the host, and the client's
# copy where the host has it.
# Needs a dedicated host (dvmp up --dedicated); on an ordinary host it skips, since the host's
# own player decides where its world is loaded there.
# Exit code 0 = pass.
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/far-derail
U="using Multiplayer.Components.Networking;"
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
fail() { echo "FAIL: $*"; exit 1; }

dedicated=$($D eval -t host -m body -u "$U" 'return new { d = NetworkLifecycle.Instance.IsDedicatedHost };' | field d)
[ "$dedicated" = "True" ] || [ "$dedicated" = "true" ] || { echo "SKIP: needs a dedicated host (dvmp up --dedicated)"; exit 0; }

# A station office 4-8 km from the host's parked body.
s=$($D eval -t host -m body 'var body = PlayerManager.PlayerTransform.position; var st = StationController.allStations.Select(x => new { x, d = Vector3.Distance(x.transform.position, body) }).Where(x => x.d > 4000 && x.d < 8000).OrderBy(x => x.d).First(); var a = Dev.Abs(st.x.transform.position); return new { station = st.x.stationInfo.YardID, x = a.x, z = a.z, d = Math.Round(st.d) };') || fail "no station 4-8 km from the host's body: $(echo "$s" | grep -o '"error": "[^"]*"' | head -1)"
echo "== client goes to $(echo "$s" | field station), $(echo "$s" | field d) m from the host's body"
$D eval -t client -m coroutine --timeout 120 -a x=$(echo "$s" | field x) -a z=$(echo "$s" | field z) \
  'yield return LocalPlayer.TeleportToGround(float.Parse(ctx.Args["x"], System.Globalization.CultureInfo.InvariantCulture), float.Parse(ctx.Args["z"], System.Globalization.CultureInfo.InvariantCulture)); yield return new WaitForSeconds(15);' >/dev/null

# A tank car on the track nearest the player, spawned by the host.
c=$($D eval -t host -m coroutine --timeout 60 -u "$U" 'var player = NetworkLifecycle.Instance.Server.GameplayPlayers.First(); var livery = DV.Globals.G.Types.Liveries.First(l => l.id == "TankChrome"); var car = CarSpawner.Instance.SpawnCarOnClosestTrack(player.WorldPosition, livery, false, true, false) ?? throw new Exception("no room to spawn next to the player"); car.brakeSystem.SetHandbrakePosition(1f); for (int i = 0; i < 180; i++) yield return null; ctx.Result = new { id = car.ID, anchor = DedicatedHostAnchor.Instance?.Followed?.Username };') || fail "spawn: $(echo "$c" | grep -o '"error": "[^"]*"' | head -1)"
car=$(echo "$c" | field id)
cleanup() { $D eval -t host -m body -a id=$car 'Trains.Delete(Trains.Find(ctx.Args["id"])); return 0;' >/dev/null 2>&1 || true; }
trap cleanup EXIT
echo "   spawned $car; the host's world follows $(echo "$c" | field anchor)"

echo "== host derails $car"
$D eval -t host -m body -a car=$car 'var car = Trains.Find(ctx.Args["car"]); car.Derail(); return new { derailed = car.derailed };' >/dev/null
sleep 12
h=$($D eval -t host -m body -a car=$car -f $P/state.cs); cl=$($D eval -t client -m body -a car=$car -f $P/state.cs)
apart=$(node -e 'const a=JSON.parse(process.argv[1]),b=JSON.parse(process.argv[2]);console.log(Math.hypot(a.x-b.x,a.y-b.y,a.z-b.z).toFixed(2))' "$(echo "$h" | field abs)" "$(echo "$cl" | field abs)")
echo "   host: derailed $(echo "$h" | field derailed), kinematic $(echo "$h" | field kinematic), world loaded $(echo "$h" | field loaded), ground $(echo "$h" | field groundCollider), speed $(echo "$h" | field speed), $(echo "$h" | field fromHostBody) m from its body"
echo "   client: derailed $(echo "$cl" | field derailed), $apart m from the host's"
[ "$(echo "$h" | field derailed)" = "true" ] || fail "the test derail didn't happen on the host"
[ "$(echo "$h" | field loaded)" = "true" ] || fail "the host has no world loaded at the car"
[ "$(echo "$h" | field kinematic)" = "false" ] || fail "the host froze the derailed car (kinematic)"
[ "$(echo "$h" | field ground)" = "true" ] || fail "no ground under the car on the host"
node -e 'process.exit(+process.argv[1] < 0.5 ? 0 : 1)' "$(echo "$h" | field speed)" || fail "the car hasn't come to rest on the host"
[ "$(echo "$cl" | field derailed)" = "true" ] || fail "not derailed on the client"
node -e 'process.exit(+process.argv[1] < 1.5 ? 0 : 1)' "$apart" || fail "client car $apart m from the host's"
echo PASS
