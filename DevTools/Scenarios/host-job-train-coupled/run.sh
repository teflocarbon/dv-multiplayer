#!/usr/bin/env bash
# The host's job trains stay coupled (the "cargo containers randomly decouple" family): the host's
# own client used to apply packets the server meant for remote clients to the host's world, and a
# train-sync snapshot from before a spawn's auto-couple uncoupled the host's consists. Loading a
# loco's cab asked for such a snapshot, so the host stepping into the cab of a loco on a job train
# uncoupled that train (docs/design/host-local-session.md, stage 1).
#  1. The station's job generator spawns fresh jobs, as when a player arrives (its available jobs
#     are expired, then it generates new ones); the host takes a transport or empty-haul one.
#  2. A shunter rolls onto the job's train and the host couples it, as a player does.
#  3. The host loads the loco's cab RELOADS times from beside it, then boards it.
#  4. For HOLD seconds, the host's and the client's coupling of the train (and the loco) must stay
#     exactly as it was after step 2, and match each other.
# The host's own client must also never have been sent a train packet (the server-side half of the
# same rule: HostOwnClient logs one it drops).
#   STATION=FM  LOCO=LocoDE2  RELOADS=3  HOLD=60
# Exit code 0 = pass. Needs a running pair with a player host (a dedicated host has no player to
# take the job).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
# A failed dvmp call prints its output (the eval's compile error or exception) instead of the run
# stopping silently under set -e with it captured in a variable or sent to /dev/null.
dvmp_bin=$D; D=dvmp_checked
dvmp_checked() { local out; if ! out=$("$dvmp_bin" "$@"); then echo "FAIL: dvmp ${*:1:3} failed: ${out:-no output}" >&2; return 1; fi; printf '%s\n' "$out"; }
P=DevTools/Scenarios/host-job-train-coupled
J=DevTools/Scenarios/jobs
STATION=${STATION:-FM}; LOCO=${LOCO:-LocoDE2}; RELOADS=${RELOADS:-3}; HOLD=${HOLD:-60}
U="using DV.Logic.Job; using Multiplayer.Shared.Jobs; using Multiplayer.Shared.Items; using Multiplayer.Shared.Protocol.Data.Jobs; using Multiplayer.Shared.Trains;"
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
# To stderr: topo_both fails inside a $(...) capture.
fail() { echo "FAIL: $*" >&2; exit 1; }
loco=""
cleanup() {
  "$dvmp_bin" eval -t host -m coroutine --timeout 60 'yield return LocalPlayer.TeleportToGround(7194f, 6873f);' >/dev/null 2>&1 || true
  "$dvmp_bin" eval -t client -m coroutine --timeout 60 'yield return LocalPlayer.TeleportToGround(7190f, 6880f);' >/dev/null 2>&1 || true
  [ -n "$loco" ] && "$dvmp_bin" eval -t host -m body -a id=$loco 'Trains.Delete(Trains.Find(ctx.Args["id"])); return 0;' >/dev/null 2>&1 || true
}
trap cleanup EXIT

# The train packets the host's own client has dropped so far this session (it logs each type once).
dropped_train_packets() {
  "$dvmp_bin" logs -t host -c "sent to the host's own client" -n 2000 2>/dev/null |
    grep -o 'Ignored Clientbound[A-Za-z]*' | grep -E 'Train|Topology|Coupl|Car' | sort -u | tr '\n' ' ' || true
}
# Prints "host-topology"; fails if the two games disagree or a car is missing.
topo_both() {
  local h c ht ct
  h=$($D eval -t host -m body -u "$U" -a cars=$1 -f $P/topo.cs); c=$($D eval -t client -m body -u "$U" -a cars=$1 -f $P/topo.cs)
  ht=$(echo "$h" | field topo); ct=$(echo "$c" | field topo)
  [ "$(echo "$h" | field missing)" = "0" ] || fail "the host is missing a car: $ht"
  [ "$(echo "$c" | field missing)" = "0" ] || fail "the client is missing a car: $ct"
  [ "$ht" = "$ct" ] || fail "host and client couple the train differently: host $ht | client $ct"
  echo "$ht|$(echo "$h" | field couplings)|$(echo "$h" | field sets)"
}

before=$(dropped_train_packets)

# The station office (validator and overviews), as the jobs scenario does.
$D eval -t host -m coroutine --timeout 90 'yield return LocalPlayer.TeleportToGround(5990f, 6740f); yield return new WaitForSeconds(6);' >/dev/null
$D eval -t client -m coroutine --timeout 90 'yield return LocalPlayer.TeleportToGround(5986f, 6740f); yield return new WaitForSeconds(6);' >/dev/null

echo "== $STATION generates fresh jobs"
g=$($D eval -t host -m coroutine --timeout 90 -u "$U" -a station=$STATION -f $P/generate.cs)
job=$(echo "$g" | field job); cars=$(echo "$g" | field cars)
[ -n "$cars" ] || fail "$job has no cars"
t=$(topo_both "$cars")
echo "   $job ($(echo "$g" | field type)), cars $cars: ${t%%|*} ($(echo "$t" | cut -d'|' -f2) coupling(s))"

echo "== the host takes $job"
a=$($D eval -t host -m coroutine --timeout 60 -u "$U" -a job=$job -f $J/accept.cs)
[ "$(echo "$a" | field state)" = "InProgress" ] || fail "job not started: $(echo "$a" | field state)"
sleep 2
t2=$(topo_both "$cars")
[ "${t2%%|*}" = "${t%%|*}" ] || fail "taking the job changed its train's coupling: ${t%%|*} -> ${t2%%|*}"

echo "== a $LOCO couples to the train"
l=$($D eval -t host -m coroutine --timeout 150 -u "$U" -a cars=$cars -a loco=$LOCO -f $P/couple-loco.cs)
loco=$(echo "$l" | field loco); all="$cars,$(echo "$l" | field locoNetId)"
echo "   $loco coupled to $(echo "$l" | field end) ($(echo "$l" | field how))"
sleep 3
want=$(topo_both "$all")
[ "$(echo "$want" | cut -d'|' -f2)" -eq $(( $(echo "$t" | cut -d'|' -f2) + 1 )) ] ||
  fail "coupling the loco made $(echo "$want" | cut -d'|' -f2) coupling(s) out of $(echo "$t" | cut -d'|' -f2): ${want%%|*}"
echo "   ${want%%|*}"

echo "== the host loads $loco's cab $RELOADS times, then boards it"
r=$($D eval -t host -m coroutine --timeout 60 -a loco=$loco -a reloads=$RELOADS 'var car = Trains.Find(ctx.Args["loco"]); Vector3 side = Dev.Abs(car.transform.position + car.transform.right * 4f); yield return LocalPlayer.TeleportToGround(side.x, side.z); for (float t0 = Time.realtimeSinceStartup; !car.IsInteriorLoaded && Time.realtimeSinceStartup - t0 < 15f;) yield return null; for (int i = 0; i < int.Parse(ctx.Args["reloads"]); i++) { car.UnloadInterior(); yield return null; car.LoadInterior(); yield return new WaitForSecondsRealtime(1.5f); } yield return Trains.Board(car); yield return new WaitForSeconds(3); ctx.Result = new { loaded = car.IsInteriorLoaded, onboard = PlayerManager.Car == car };')
echo "   interior loaded: $(echo "$r" | field loaded), host on board: $(echo "$r" | field onboard)"
[ "$(echo "$r" | field onboard)" = "true" ] || fail "the host didn't board $loco"
# The client watches from beside the train (its own followers and interest).
$D eval -t client -m coroutine --timeout 60 -a loco=$loco 'var car = Trains.Find(ctx.Args["loco"]); Vector3 side = Dev.Abs(car.transform.position - car.transform.right * 6f); yield return LocalPlayer.TeleportToGround(side.x, side.z);' >/dev/null

echo "== $HOLD s: the train stays coupled on both games"
for (( s = 0; s < HOLD; s += 10 )); do
  sleep 10
  now=$(topo_both "$all")
  [ "${now%%|*}" = "${want%%|*}" ] || fail "after $(( s + 10 )) s the train's coupling changed: ${want%%|*} -> ${now%%|*}"
done
echo "   unchanged on both: $(echo "$want" | cut -d'|' -f2) coupling(s) in $(echo "$want" | cut -d'|' -f3) trainset(s)"

after=$(dropped_train_packets)
new=$(comm -13 <(tr ' ' '\n' <<<"$before" | sort -u) <(tr ' ' '\n' <<<"$after" | sort -u) | tr '\n' ' ')
[ -z "${new// /}" ] || fail "the server sent the host's own client train packets: $new"
echo "PASS"
