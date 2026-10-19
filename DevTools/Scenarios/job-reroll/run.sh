#!/usr/bin/env bash
# Re-rolling an offered job by dropping its overview in the station office bin (docs/jobs.md).
#  1. WHO drops an offered job's overview in the bin with nobody on its cars: on both peers the job
#     expires and its overview and cars are gone at once, and the station offers a new job in its
#     place. Both peers end up offering the same jobs.
#  2. The client boards a car of another offered job and WHO bins that job's overview: the paper
#     flies back up out of the bin and lands at the same spot on both peers, and the job, its
#     overview and its cars stay.
#   WHO=client|host  STATION=FM
# Exit code 0 = pass. Needs a running pair (dvmp up); on a dedicated host only WHO=client applies.
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/job-reroll
WHO=${WHO:-client}; STATION=${STATION:-FM}
U="using DV.Logic.Job; using Multiplayer.Shared.Jobs; using Multiplayer.Shared.Items; using Multiplayer.Shared.Trains; using Multiplayer.Shared.Session; using Multiplayer.Server.Session;"
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
fail() { echo "FAIL: $*"; exit 1; }
state() { $D eval -t $1 -m body -u "$U" -a job=$2 -a cars=$3 -a station=$STATION -f $P/state.cs; }
office() { # both players at the office (a dedicated host has no player to move)
  [ "$DEDICATED" = true ] || $D eval -t host -m coroutine --timeout 90 'yield return LocalPlayer.TeleportToGround(5990f, 6740f); yield return new WaitForSeconds(4);' >/dev/null
  $D eval -t client -m coroutine --timeout 90 'yield return LocalPlayer.TeleportToGround(5986f, 6740f); yield return new WaitForSeconds(4);' >/dev/null; }
cleanup() { $D eval -t $WHO -m coroutine 'if (Hands.Held != null) yield return Hands.Drop(); Hands.ReleaseControl();' >/dev/null 2>&1 || true; office >/dev/null 2>&1 || true; }

DEDICATED=$($D eval -t host 'Multiplayer.Shared.Session.NetworkLifecycle.Instance.Server.LocalPlayer == null' | node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));console.log(Object.values(o.results)[0].result)')
[ "$DEDICATED" = true ] && [ "$WHO" = host ] && fail "WHO=host needs a player host"
trap cleanup EXIT

pick() { # an offered job at the office that can be re-rolled; DV makes new ones once every player has been 2 km away
  p=$($D eval -t host -m body -u "$U" -a station=$STATION -f $P/pick.cs 2>/dev/null) && echo "$p" | field job >/dev/null 2>&1 && { echo "$p"; return; }
  echo "== no job to re-roll at $STATION: going 2.6 km away so DV generates new ones" >&2
  for who in host client; do
    [ "$who" = host ] && [ "$DEDICATED" = true ] && continue
    $D eval -t $who -m coroutine --timeout 90 'yield return LocalPlayer.TeleportToGround(8600f, 6740f); yield return new WaitForSeconds(8);' >/dev/null
  done
  office; sleep 15
  $D eval -t host -m body -u "$U" -a station=$STATION -f $P/pick.cs; }

office
echo "== 1. $WHO bins an offered job's overview, nobody on its cars"
p=$(pick); job=$(echo "$p" | field job); cars=$(echo "$p" | field cars); offered=$(echo "$p" | field offered)
echo "   $(echo "$p" | field jobId) (job $job, overview $(echo "$p" | field overview)), cars $cars; $offered of $(echo "$p" | field capacity) jobs offered"
b=$($D eval -t $WHO -m coroutine --timeout 60 -u "$U" -a job=$job -f $P/bin.cs)
echo "   bin at $(echo "$b" | field bin) (size $(echo "$b" | field binSize)): went in $(echo "$b" | field wentIn), paper gone $(echo "$b" | field gone)"
[ "$(echo "$b" | field wentIn)" = true ] || fail "the paper didn't fall into the bin"
[ "$(echo "$b" | field gone)" = true ] || fail "the paper is still there: the job wasn't re-rolled"
for i in $(seq 1 30); do # the station generates the replacement a job at a time
  h=$(state host $job $cars)
  [ "$(echo "$h" | field generating)" = false ] && [ "$(echo "$h" | field offeredCount)" -ge "$offered" ] && break
  sleep 1
done
for i in $(seq 1 15); do # and its packets reach the client
  h=$(state host $job $cars); c=$(state client $job $cars)
  [ "$(echo "$h" | field offered)" = "$(echo "$c" | field offered)" ] && break
  sleep 1
done
for who in host client; do
  s=$([ $who = host ] && echo "$h" || echo "$c")
  echo "   $who: job $(echo "$s" | field state), overview $(echo "$s" | field overview), cars left: $(echo "$s" | field carsLeft); offered $(echo "$s" | field offered)"
  case "$(echo "$s" | field state)" in Expired|gone) ;; *) fail "$who: the job is $(echo "$s" | field state), not expired" ;; esac
  [ "$(echo "$s" | field overview)" = none ] || fail "$who still has the overview"
  [ "$(echo "$s" | field carsLeft)" = 0 ] || fail "$who still has the job's cars: $(echo "$s" | field cars)"
done
n=$(echo "$h" | field offeredCount)
[ "$n" -ge "$offered" ] || fail "the station offers $n jobs, $offered before: no replacement was generated"
[ "$(echo "$h" | field offered)" = "$(echo "$c" | field offered)" ] || fail "the peers offer different jobs"

echo "== 2. the client boards a car of another job; $WHO bins its overview"
office
p=$(pick); job=$(echo "$p" | field job); cars=$(echo "$p" | field cars); car=$(echo "$p" | field firstCar)
echo "   $(echo "$p" | field jobId) (job $job, overview $(echo "$p" | field overview)), the client boards $car"
[ "$WHO" = client ] && $D eval -t client -m coroutine --timeout 30 -u "$U" -a item=$(echo "$p" | field overview) 'yield return Hands.Pickup(ushort.Parse(ctx.Args["item"]));' >/dev/null
$D eval -t client -m coroutine --timeout 60 -a car=$car 'yield return Trains.Board(Trains.Find(ctx.Args["car"]));' >/dev/null || fail "the client couldn't board $car"
sleep 1
b=$($D eval -t $WHO -m coroutine --timeout 60 -u "$U" -a job=$job -f $P/bin.cs)
echo "   went in $(echo "$b" | field wentIn), paper gone $(echo "$b" | field gone), flew $(echo "$b" | field flewUp) m up, landed $(echo "$b" | field fromBin) m from the bin"
[ "$(echo "$b" | field wentIn)" = true ] || fail "the paper didn't fall into the bin"
[ "$(echo "$b" | field gone)" = false ] || fail "the paper was taken: the job was re-rolled with a player on its car"
node -e 'process.exit(Number(process.argv[1])>=0.3?0:1)' "$(echo "$b" | field flewUp)" || fail "the paper wasn't thrown back out of the bin"
[ "$(echo "$b" | field inBin)" = false ] || fail "the paper ended up in the bin"
sleep 4
h=$(state host $job $cars); c=$(state client $job $cars)
total=$(echo "$cars" | tr ',' '\n' | grep -c .)
for who in host client; do
  s=$([ $who = host ] && echo "$h" || echo "$c")
  echo "   $who: job $(echo "$s" | field state), overview $(echo "$s" | field overview) at $(echo "$s" | field overviewAt), cars left: $(echo "$s" | field carsLeft)/$total"
  [ "$(echo "$s" | field state)" = Available ] || fail "$who: the job is $(echo "$s" | field state), not offered"
  [ "$(echo "$s" | field overview)" != none ] || fail "$who has no overview"
  [ "$(echo "$s" | field carsLeft)" = "$total" ] || fail "$who lost cars of the job"
done
node -e 'const p=s=>s.replace(/[()]/g,"").split(",").map(Number);const [a,b]=[p(process.argv[1]),p(process.argv[2])];const d=Math.hypot(a[0]-b[0],a[1]-b[1],a[2]-b[2]);console.log("   the peers'"'"' papers are "+d.toFixed(2)+" m apart");process.exit(d<0.5?0:1)' \
  "$(echo "$h" | field overviewAt)" "$(echo "$c" | field overviewAt)" || fail "the paper landed at different spots on the peers"
echo PASS
