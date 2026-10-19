#!/usr/bin/env bash
# The comms radio's car tools as a player uses them, by the client or by the host's own player
# (WHO=host: its requests are handled in-process, docs/design/host-local-session.md). A car spawned
# near the host is deleted with the radio's request: it's gone on both games. Then a work train is
# summoned where it stood: an invalid request (no such track) is answered, not left to time out,
# and a valid one is answered Success and the loco is there on both games.
#   WHO (client), LIVERY (LocoDM3, the car deleted), WORK_LIVERY (auto: a loco with no car in the
#   world and no garage, so the summon spawns one for free)
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
# A failed dvmp call prints its output (the eval's compile error or exception) instead of the run
# stopping silently under set -e with it captured in a variable or sent to /dev/null.
dvmp_bin=$D; D=dvmp_checked
dvmp_checked() { local out; if ! out=$("$dvmp_bin" "$@"); then echo "FAIL: dvmp ${*:1:3} failed: ${out:-no output}" >&2; return 1; fi; printf '%s\n' "$out"; }
P=DevTools/Scenarios/radio-tools
WHO=${WHO:-client}; LIVERY=${LIVERY:-LocoDM3}; WORK_LIVERY=${WORK_LIVERY:-auto}
result() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}console.log(JSON.stringify(x.result))'; }
val() { node -e 'console.log(JSON.parse(process.argv[1])[process.argv[2]])' "$1" "$2"; }
fail=0
work=""
cleanup() { [ -n "$work" ] && $D eval -t host -m body -a livery=$work 'foreach (var c in UnityEngine.Object.FindObjectsOfType<TrainCar>().Where(x => x.carLivery.id == ctx.Args["livery"]).ToList()) Trains.Delete(c); return 0;' >/dev/null 2>&1 || true; }
trap cleanup EXIT
# Cars of a livery (and ID, if given) on one game: how many, and their IDs.
near() { $D eval -t $1 -m body -a livery=$2 -a id="${3:-}" 'var cars = UnityEngine.Object.FindObjectsOfType<TrainCar>().Where(c => c.carLivery.id == ctx.Args["livery"] && (!ctx.Args.TryGetValue("id", out var id) || id == "" || c.ID == id)).ToList(); return new { count = cars.Count, ids = string.Join(" ", cars.Select(c => c.ID)) };' | result; }

s=$($D eval -t host -m coroutine --timeout 60 -a livery=$LIVERY -f $P/setup.cs | result)
id=$(val "$s" id); netId=$(val "$s" netId); track=$(val "$s" track); index=$(val "$s" index)
echo "== spawned $LIVERY $id (net $netId) on track $track, point $index"
sleep 2
[ "$(val "$(near client $LIVERY $id)" count)" = 1 ] || { echo "FAIL: the client doesn't have $id"; exit 1; }

echo "== $WHO deletes it with the radio's request"
$D eval -t $WHO -m body -a net=$netId 'Multiplayer.Shared.Session.NetworkLifecycle.Instance.Client.SendTrainDeleteRequest(ushort.Parse(ctx.Args["net"])); return 0;' >/dev/null
sleep 3
for t in host client; do
  n=$(val "$(near $t $LIVERY $id)" count)
  echo "   $t: $id $([ "$n" = 0 ] && echo gone || echo "still there")"
  [ "$n" = 0 ] || fail=1
done

echo "== $WHO summons a work train on a track that doesn't exist"
r=$($D eval -t $WHO -m coroutine --timeout 30 -a livery=$WORK_LIVERY -a track=0 -a index=0 -a timeout=10 -f $P/work-train.cs | result)
echo "   answer: $(val "$r" response) after $(val "$r" seconds) s"
[ "$(val "$r" response)" != timeout ] || fail=1

echo "== $WHO summons a work train where $id stood"
r=$($D eval -t $WHO -m coroutine --timeout 30 -a livery=$WORK_LIVERY -a track=$track -a index=$index -a timeout=10 -f $P/work-train.cs | result)
work=$(val "$r" livery)
echo "   $work: $(val "$r" response) after $(val "$r" seconds) s"
[ "$(val "$r" response)" = Success ] || fail=1
sleep 3
for t in host client; do
  n=$(near $t $work)
  echo "   $t: $(val "$n" count) $work ($(val "$n" ids))"
  [ "$(val "$n" count)" = 1 ] || fail=1
done

[ $fail = 0 ] && echo "PASS" || { echo "FAIL"; exit 1; }
