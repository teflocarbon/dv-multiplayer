#!/usr/bin/env bash
# A job from start to finish across both players: one takes a transport (or empty haul) job's overview to the
# validator, the cars are delivered (their logical track set to the destination, standing in for
# the drive), one hands the booklet in, and the payment is banked. Both peers must agree on the
# job's state at every step, see the same booklet, report and banknotes, and the shared wallet
# must go up by the payment exactly once.
#   ACCEPT=client|host (who takes the job)  HANDIN=client|host (who hands it in)  STATION=FM
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/jobs
ACCEPT=${ACCEPT:-client}; HANDIN=${HANDIN:-client}; STATION=${STATION:-FM}
U="using DV.Logic.Job; using Multiplayer.Server.Jobs; using Multiplayer.Shared.Jobs; using Multiplayer.Server.Items; using Multiplayer.Shared.Items; using Multiplayer.Shared.Jobs; using Multiplayer.Shared.Stations; using Multiplayer.Shared.World;"
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
fail() { echo "FAIL: $*"; exit 1; }
both() { # job state on both peers must match: prints "state booklets"
  h=$($D eval -t host -m body -u "$U" -a job=$job -f $P/job.cs); c=$($D eval -t client -m body -u "$U" -a job=$job -f $P/job.cs)
  hs="$(echo "$h" | field state) $(echo "$h" | field booklets)"; cs="$(echo "$c" | field state) $(echo "$c" | field booklets)"
  echo "   host: $hs | client: $cs"
  [ "$(echo "$h" | field state)" = "$(echo "$c" | field state)" ] || fail "job state differs"; }
# A job left taken at the station (test saves keep them) stops DV generating new jobs there until
# every player has been more than 2 km from it (StationController.Update), so without a job to
# take, both players go 2.6 km from FM's office and come back.
spare=$($D eval -t host -m body -a station=$STATION 'var st = StationController.allStations.First(s => s.stationInfo.YardID == ctx.Args["station"]); return new { n = st.logicStation.availableJobs.Count(j => j.jobType == DV.ThingTypes.JobType.Transport || j.jobType == DV.ThingTypes.JobType.EmptyHaul), taken = st.logicStation.takenJobs.Count };')
if [ "$(echo "$spare" | field n)" = "0" ]; then
  echo "== no transport or empty-haul job at $STATION ($(echo "$spare" | field taken) taken): going 2 km away so DV generates new ones"
  for who in host client; do
    $D eval -t $who -m coroutine --timeout 90 'yield return LocalPlayer.TeleportToGround(8600f, 6740f); yield return new WaitForSeconds(8);' >/dev/null
  done
fi
# the station office (validator and overviews)
$D eval -t host -m coroutine --timeout 90 'yield return LocalPlayer.TeleportToGround(5990f, 6740f); yield return new WaitForSeconds(6);' >/dev/null
$D eval -t client -m coroutine --timeout 90 'yield return LocalPlayer.TeleportToGround(5986f, 6740f); yield return new WaitForSeconds(6);' >/dev/null
p=$($D eval -t host -m body -u "$U" -a station=$STATION -f $P/pick.cs) ||
  fail "no job to take: $(echo "$p" | grep -o '"error": "[^"]*"' | head -1)"
job=$(echo "$p" | field job); echo "== $job (pays \$$(echo "$p" | field pay)); $ACCEPT takes it"
a=$($D eval -t $ACCEPT -m coroutine --timeout 60 -u "$U" -a job=$job -f $P/accept.cs)
[ "$(echo "$a" | field state)" = "InProgress" ] || fail "job not started: $(echo "$a" | field state)"
sleep 2; both
echo "== cars delivered"
d=$($D eval -t host -m coroutine --timeout 60 -u "$U" -a job=$job -f $P/deliver.cs)
[ "$(echo "$d" | field task)" = "Done" ] || fail "task not done after delivery"
w0=$($D eval -t host 'DV.InventorySystem.Inventory.Instance.PlayerMoney' | node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));console.log(Object.values(o.results)[0].result)')
echo "== $HANDIN hands the booklet in"
r=$($D eval -t $HANDIN -m coroutine --timeout 60 -u "$U" -a job=$job -f $P/handin.cs)
echo "   printed $(echo "$r" | field printed), cash \$$(echo "$r" | field cash)"
[ "$(echo "$r" | field state)" = "Completed" ] || fail "job not completed: $(echo "$r" | field state)"
sleep 2; both
note=$(echo "$r" | field printed | tr ',' '\n' | grep '^Banknotes#' | head -1 | cut -d'#' -f2)
[ -n "$note" ] || fail "no payment printed"
seen=$($D eval -t client -u "$U" "NetworkedItem.TryGet($note, out NetworkedItem x) && x != null" | node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));console.log(Object.values(o.results)[0].result)')
[ "$seen" = "true" ] || fail "client doesn't have the payment banknotes"
echo "== client banks the payment"
$D eval -t client -m coroutine --timeout 60 -u "$U" -a item=$note -f $P/bank.cs >/dev/null
sleep 2
w1=$($D eval -t host 'DV.InventorySystem.Inventory.Instance.PlayerMoney' | node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));console.log(Object.values(o.results)[0].result)')
wc=$($D eval -t client 'DV.InventorySystem.Inventory.Instance.PlayerMoney' | node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));console.log(Object.values(o.results)[0].result)')
echo "   wallet \$$w0 -> host \$$w1, client \$$wc (payment \$$(echo "$r" | field cash))"
node -e 'const [a,b,c,p]=process.argv.slice(1).map(Number);process.exit(Math.abs(b-a-p)<0.5&&Math.abs(c-b)<0.5?0:1)' "$w0" "$w1" "$wc" "$(echo "$r" | field cash)" || fail "the wallet didn't go up by the payment on both"
echo PASS
