#!/usr/bin/env bash
# A client's control change made just after a host resync still reaches the host (playtest 2:
# closed oiling caps on a 2901 reopened 10-15 s later). A client takes a control's change as the
# player's only once the host's last value for it has settled (1.5 s), so it isn't the host's own
# value echoing back. A change inside that window was dropped for good, and every game that loaded
# the car's interior asked for a full resync, which restarts the window for every port: a player
# at the loading distance reloaded it every few seconds.
#  1. The client asks for a resync and, 0.3 s after it lands, sets a car-body control with no grab
#     (an oiling cap by default). The host must get the change, and it must survive the next resync.
#  2. The client's game reloads the car's interior RELOADS times, a second apart. It may ask for at
#     most one resync per car per 10 s: no more than 2 requests in 12 s.
#   LOCO=LocoS282A  PORT=oil (a port id or part of one; LOCO=LocoDE6 PORT=windowL1 for a cab window)
#   RELOADS=4
# Exit code 0 = pass. Needs a running pair; a dedicated host works too.
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
# A failed dvmp call prints its output (the eval's compile error or exception) instead of the run
# stopping silently under set -e with it captured in a variable or sent to /dev/null.
dvmp_bin=$D; D=dvmp_checked
dvmp_checked() { local out; if ! out=$("$dvmp_bin" "$@"); then echo "FAIL: dvmp ${*:1:3} failed: ${out:-no output}" >&2; return 1; fi; printf '%s\n' "$out"; }
P=DevTools/Scenarios/settle-window
LOCO=${LOCO:-LocoS282A}; PORT=${PORT:-oil}; RELOADS=${RELOADS:-4}
U="using Multiplayer.Client.Session; using Multiplayer.Server.Coverage; using Multiplayer.Server.Dedicated; using Multiplayer.Shared.Session; using Multiplayer.Client.Trains; using Multiplayer.Server.Trains; using Multiplayer.Shared.Trains;"
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
fail() { echo "FAIL: $*"; exit 1; }
value() { $D eval -t $1 -m body -u "$U" -a loco=$loco -a port="$2" 'var ntc = Trains.Find(ctx.Args["loco"]).GetComponent<NetworkedTrainCar>(); return new { v = ntc.simulationFlow.TryGetPort(ctx.Args["port"], out var p) ? Math.Round(p.Value, 3) : -1 };' | field v; }
loco=""
cleanup() {
  $D eval -t client -m coroutine 'yield return LocalPlayer.TeleportToGround(7190f, 6880f);' >/dev/null 2>&1 || true
  [ -n "$loco" ] && $D eval -t host -m body -a id=$loco 'Trains.Delete(Trains.Find(ctx.Args["id"])); return 0;' >/dev/null 2>&1 || true
}
trap cleanup EXIT

loco=$($D eval -t host -m coroutine -a t=$LOCO 'var car = Trains.SpawnNearPlayer(ctx.Args["t"]); car.brakeSystem.SetHandbrakePosition(1f); for (int i = 0; i < 120; i++) yield return null; ctx.Result = new { id = car.ID };' | field id)
echo "== $LOCO $loco; the client boards it"
$D eval -t client -m coroutine --timeout 60 -a loco=$loco 'yield return Trains.Board(Trains.Find(ctx.Args["loco"])); yield return new WaitForSeconds(4);' >/dev/null

echo "== the client sets a control 0.3 s after a resync lands"
s=$($D eval -t client -m coroutine --timeout 30 -u "$U" -a loco=$loco -a port="$PORT" -f $P/resync-set.cs)
port=$(echo "$s" | field port); want=$(echo "$s" | field value)
h=$(value host "$port")
echo "   $port set to $want $(echo "$s" | field since) s after the resync: client $(echo "$s" | field client), host $h"
node -e 'process.exit(Math.abs(+process.argv[1] - +process.argv[2]) < 0.02 ? 0 : 1)' "$h" "$want" || fail "the host never got the client's change to $port"

echo "== another resync"
$D eval -t client -m coroutine -u "$U" -a loco=$loco 'NetworkLifecycle.Instance.Client.SendTrainSyncRequest(Trains.Find(ctx.Args["loco"]).GetComponent<NetworkedTrainCar>().NetId); yield return new WaitForSecondsRealtime(3f);' >/dev/null
c=$(value client "$port"); h=$(value host "$port")
echo "   $port: client $c, host $h"
node -e 'process.exit(Math.abs(+process.argv[1] - +process.argv[3]) < 0.02 && Math.abs(+process.argv[2] - +process.argv[3]) < 0.02 ? 0 : 1)' "$c" "$h" "$want" || fail "$port didn't stay at $want after the next resync"

echo "== the client's game reloads the interior $RELOADS times"
# Off the car, next to it: unloading the cab with the player in it would drop them through its floor.
$D eval -t client -m coroutine -a loco=$loco 'var car = Trains.Find(ctx.Args["loco"]); yield return LocalPlayer.TeleportToGround(Dev.Abs(car.transform.position + car.transform.right * 4f).x, Dev.Abs(car.transform.position + car.transform.right * 4f).z);' >/dev/null
$D eval -t client -m class -a methods=SendTrainSyncRequest -a action=install -f $P/count-sends.cs >/dev/null
r=$($D eval -t client -m coroutine --timeout 40 -u "$U" -a loco=$loco -a reloads=$RELOADS 'var car = Trains.Find(ctx.Args["loco"]); ushort id = car.GetComponent<NetworkedTrainCar>().NetId; float t0 = Time.realtimeSinceStartup; for (int i = 0; i < int.Parse(ctx.Args["reloads"]); i++) { car.UnloadInterior(); yield return null; car.LoadInterior(); yield return new WaitForSecondsRealtime(1f); } while (Time.realtimeSinceStartup - t0 < 12f) yield return null; ctx.Result = new { id = (int)id, loaded = car.IsInteriorLoaded };')
id=$(echo "$r" | field id)
n=$($D eval -t client -m class -a methods=SendTrainSyncRequest -a action=read -f $P/count-sends.cs | field counts | node -e 'const c=JSON.parse(require("fs").readFileSync(0,"utf8"));console.log(c["SendTrainSyncRequest:"+process.argv[1]]||0)' "$id")
echo "   resync requests for car $id in 12 s: $n (interior loaded at the end: $(echo "$r" | field loaded))"
[ "$n" -le 2 ] || fail "$RELOADS interior reloads asked for $n resyncs in 12 s"
[ "$n" -ge 1 ] || fail "the interior reloads asked for no resync at all"
echo PASS
