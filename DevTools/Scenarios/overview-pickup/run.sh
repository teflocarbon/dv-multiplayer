#!/usr/bin/env bash
# Job overviews appear while both players are at the station office, and the client can pick
# them up (playtest 2). The client acknowledges each overview it creates at the revision it got.
# Every game near a new overview sends an update as it appears, so the overview's revision moves
# on while that acknowledgement is in flight. A server that took it only at the exact revision
# dropped it, and then refused that client's pickups (the overview jumped back to the table).
#  1. Both players go 2.6 km away so DV drops the station's jobs. Then the client (with LAG ms of
#     receive lag) and the host come back to the office together, and new overviews generate.
#  2. While they appear, the host nudges each overview the client has been sent but hasn't
#     acknowledged yet. That stands in for the other games' updates (NUDGE=0 relies on those
#     happening by themselves).
#  3. Every overview the client has must be acknowledged on the server, and the client picks up
#     PICKS of them: each must stay in its hand, recorded as the client's on the host.
#   STATION=FM  LAG=250  PICKS=3  NUDGE=1
# Exit code 0 = pass. Needs a running pair (dvmp up); a dedicated host works too.
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/overview-pickup
STATION=${STATION:-FM}; LAG=${LAG:-250}; PICKS=${PICKS:-3}; NUDGE=${NUDGE:-1}
U="using DV.Logic.Job; using Multiplayer.Components.Networking; using Multiplayer.Components.Networking.Jobs; using Multiplayer.Components.Networking.World;"
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
fail() { echo "FAIL: $*"; exit 1; }
lag() { $D eval -t client "Steamworks.SteamNetworkingUtils.FakeRecvPacketLag = $1f" >/dev/null 2>&1 || true; }
T=$(mktemp -d)
cleanup() { lag 0; $D eval -t client -m coroutine 'if (Hands.Held != null) yield return Hands.Drop(); Hands.ReleaseControl();' >/dev/null 2>&1 || true; }
trap cleanup EXIT

# Debts make DV refuse jobs; a job left taken keeps the station from making new ones until every
# player is far away (jobs/run.sh).
$D eval -t host -m body 'var c = DV.ServicePenalty.CareerManagerDebtController.Instance; c.ClearRestOfThePayableDebts(); c.RefreshExistingDebtsState(); return 0;' >/dev/null
echo "== both players 2.6 km from $STATION so its jobs expire"
for who in host client; do
  $D eval -t $who -m coroutine --timeout 90 'yield return LocalPlayer.TeleportToGround(8600f, 6740f); yield return new WaitForSeconds(8);' >/dev/null
done

lag $LAG
if [ "$NUDGE" = 1 ]; then
  $D eval -t host -m coroutine --timeout 120 -u "$U" -f $P/nudge.cs >"$T/nudge.json" 2>&1 &
  nudger=$!
fi
echo "== both come back to the office (client receive lag ${LAG} ms)"
$D eval -t client -m coroutine --timeout 90 'yield return LocalPlayer.TeleportToGround(5986f, 6740f);' >/dev/null &
arriving=$!
$D eval -t host -m coroutine --timeout 90 'yield return LocalPlayer.TeleportToGround(5990f, 6740f);' >/dev/null
wait $arriving || true
sleep 20
if [ "$NUDGE" = 1 ]; then
  wait $nudger || true
  echo "   host nudged $(field nudged <"$T/nudge.json") overview(s) before the client's acknowledgement came; $(field moved <"$T/nudge.json") moved on a revision"
fi
lag 0

a=$($D eval -t host -m coroutine --timeout 40 -u "$U" -a station=$STATION -f $P/acks.cs)
echo "   overviews at $STATION: $(echo "$a" | field overviews), the client has $(echo "$a" | field known), acknowledged $(echo "$a" | field acked)"
[ "$(echo "$a" | field overviews)" != "0" ] || fail "no job overviews at $STATION"
[ "$(echo "$a" | field unacked)" = "" ] || fail "overviews the client has but the server never took its acknowledgement for: $(echo "$a" | field unacked)"

ids=$(echo "$a" | field knownIds | tr ',' ' ')
n=0
for id in $ids; do
  [ $n -ge "$PICKS" ] && break
  n=$((n + 1))
  c=$($D eval -t client -m coroutine --timeout 60 -u "$U" -a item=$id -f $P/pick.cs)
  h=$($D eval -t host -m body -u "$U" -a item=$id 'ushort id = ushort.Parse(ctx.Args["item"]); var server = NetworkLifecycle.Instance.Server; var client = server.ServerPlayers.First(p => p.PlayerId != server.SelfId); AuthoritativeItemRegistry.TryGet(id, out var r); return new { placement = r?.Placement.ToString(), mine = r != null && r.PlacementPlayerId == client.PlayerId };')
  echo "   overview $id: client holds it $(echo "$c" | field held) (still after 3 s: $(echo "$c" | field kept)); host records $(echo "$h" | field placement), the client's: $(echo "$h" | field mine)"
  [ "$(echo "$c" | field kept)" = "true" ] || fail "the client's pickup of overview $id was refused"
  [ "$(echo "$h" | field mine)" = "true" ] || fail "the host doesn't have overview $id in the client's hand"
  $D eval -t client -m coroutine 'yield return Hands.Drop(); yield return new WaitForSeconds(1);' >/dev/null
done
[ $n -gt 0 ] || fail "the client has no overview to pick up"
echo PASS
