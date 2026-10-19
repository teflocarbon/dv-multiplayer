#!/usr/bin/env bash
# Only a player's shovelling is reported to the host as coal added (playtest 2: stoker locos' fireboxes
# sat overfull and their coal flickered). Coal added used to be reported by watching the firebox's
# coal input port, which a mechanical stoker writes every simulation tick on every game (Custom Car
# Loader's SteamMechanicalStoker: the S484, the 2901). Every game reported its own stoker, the
# host's own client too, and the host added it all on top of its own stoker. Coal taken from the
# tender (TenderCoal) was reported the same way, by watching its coal pile's consume port, which the
# stoker writes too: the host's own client sent itself two a tick per stoker tender all night
# (340,000 log lines before the server's 01:08 crash).
#  1. Stoker feed, counting AddCoal and TenderCoal sends on both games (the host's own client included):
#     - with a Custom Car Loader stoker loco installed (STOKER=auto finds one, or names the livery),
#       the host starts it and opens its stoker, and the stoker runs on both games;
#     - without one (DV has no stoker of its own), the firebox's coal input and the tender's coal
#       pile are written the way a stoker writes them, on both games, on an S282.
#     No AddCoal or TenderCoal may be sent.
#  2. The client shovels coal into the firebox (FireboxSimController.TransferCoal, as the shovel
#     does): exactly one AddCoal is sent, from the client.
#  3. The client shovels coal out of the tender (CoalPileSimController.TryRemoveCoal, as the shovel
#     does): exactly one TenderCoal is sent, from the client.
#  The loco comes with its tender (Trains.SpawnWithTenderNearPlayer): spawning one car never brings
#  it, and without it there's no coal pile.
#   STOKER=auto|none|<livery id>  FEED=10 (seconds of stoker feed)
# Exit code 0 = pass. Needs a running pair; a dedicated host works too.
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
# A failed dvmp call prints its output (the eval's compile error or exception) instead of the run
# stopping silently under set -e with it captured in a variable or sent to /dev/null.
dvmp_bin=$D; D=dvmp_checked
dvmp_checked() { local out; if ! out=$("$dvmp_bin" "$@"); then echo "FAIL: dvmp ${*:1:3} failed: ${out:-no output}" >&2; return 1; fi; printf '%s\n' "$out"; }
P=DevTools/Scenarios/stoker-coal
C=DevTools/Scenarios/settle-window/count-sends.cs
STOKER=${STOKER:-auto}; FEED=${FEED:-10}
U="using Multiplayer.Client.Trains; using Multiplayer.Server.Trains; using Multiplayer.Shared.Trains;"
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
fail() { echo "FAIL: $*"; exit 1; }
count() { $D eval -t $1 -m class -a methods=SendAddCoal -a action=read -f $C | field counts | node -e 'const c=JSON.parse(require("fs").readFileSync(0,"utf8"));console.log(c["SendAddCoal:"+process.argv[1]]||0)' "$id"; }
# Coal-pile sends for any car: the coal pile is the tender's, not the loco's.
tender() { $D eval -t $1 -m class -a methods=SendTenderCoalPileInteraction -a action=read -f $C | field counts | node -e 'const c=JSON.parse(require("fs").readFileSync(0,"utf8"));console.log(Object.values(c).reduce((a,b)=>a+b,0))'; }
loco=""
cleanup() {
  $D eval -t client -m coroutine 'yield return LocalPlayer.TeleportToGround(7190f, 6880f);' >/dev/null 2>&1 || true
  [ -n "$loco" ] && $D eval -t host -m body -a id=$loco 'var car = Trains.Find(ctx.Args["id"]); foreach (var c in car.trainset.cars.ToList()) Trains.Delete(c); return 0;' >/dev/null 2>&1 || true
}
trap cleanup EXIT

livery=$($D eval -t host -m body -a stoker=$STOKER -f $P/pick.cs | field livery)
real=true; [ "$livery" = "none" ] && { real=false; livery=LocoS282A; }
s=$($D eval -t host -m coroutine --timeout 60 -u "$U" -a t=$livery 'var car = Trains.SpawnWithTenderNearPlayer(ctx.Args["t"]); car.brakeSystem.SetHandbrakePosition(1f); for (int i = 0; i < 120; i++) yield return null; yield return Trains.CoupleTender(car); ctx.Result = new { id = car.ID, net = (int)car.GetComponent<NetworkedTrainCar>().NetId };')
loco=$(echo "$s" | field id); id=$(echo "$s" | field net)
echo "== $livery $loco (a real stoker: $real); the client boards it"
$D eval -t client -m coroutine --timeout 60 -a loco=$loco 'yield return Trains.Board(Trains.Find(ctx.Args["loco"])); yield return new WaitForSeconds(4);' >/dev/null
for t in host client; do $D eval -t $t -m class -a methods=SendAddCoal,SendTenderCoalPileInteraction -a action=install -f $C >/dev/null; done

if [ $real = true ]; then
  echo "== the host fires it up and opens the stoker for ${FEED} s"
  f=$($D eval -t host -m coroutine --timeout $((FEED + 60)) -a loco=$loco -a feed=$FEED -a mode=real -f $P/feed.cs)
  r=$($D eval -t client -m body -a loco=$loco -f $P/state.cs)
  echo "   stoker rate (normalised) host $(echo "$f" | field stoking), client $(echo "$r" | field stoking); firebox host $(echo "$f" | field firebox), client $(echo "$r" | field firebox)"
  [ "$(echo "$f" | field ran)" = "true" ] || fail "the stoker never ran on the host (no steam pressure?)"
else
  echo "== no stoker loco: both games write the firebox's coal input and the tender's coal pile the way a stoker does, for ${FEED} s"
  $D eval -t client -m coroutine --timeout $((FEED + 30)) -a loco=$loco -a feed=$FEED -a mode=simulate -f $P/feed.cs >/dev/null &
  fed=$!
  $D eval -t host -m coroutine --timeout $((FEED + 30)) -a loco=$loco -a feed=$FEED -a mode=simulate -f $P/feed.cs >/dev/null
  wait $fed
fi
h=$(count host); c=$(count client)
echo "   AddCoal sent: host's own client $h, client $c"
[ "$h" = 0 ] && [ "$c" = 0 ] || fail "stoker feed was sent as coal added (host $h, client $c)"
h=$(tender host); c=$(tender client)
echo "   TenderCoal sent: host's own client $h, client $c"
[ "$h" = 0 ] && [ "$c" = 0 ] || fail "stoker feed was sent as coal taken from the tender (host $h, client $c)"

echo "== the client shovels coal in"
$D eval -t client -m body -a loco=$loco 'var car = Trains.Find(ctx.Args["loco"]); car.SimController.firebox.TransferCoal(2f); return 0;' >/dev/null
sleep 2
h=$(count host); c=$(count client)
echo "   AddCoal sent: host's own client $h, client $c"
[ "$c" = 1 ] || fail "one shovelful sent $c AddCoal from the client"
[ "$h" = 0 ] || fail "the host's own client sent $h AddCoal"

echo "== the client shovels coal out of the tender"
taken=$($D eval -t client -m body -a loco=$loco 'var car = Trains.Find(ctx.Args["loco"]); var pile = car.trainset.cars.Select(c => c.SimController?.coalPile).First(p => p != null); return new { taken = pile.TryRemoveCoal(2f) };' | field taken)
sleep 2
h=$(tender host); c=$(tender client)
echo "   took $taken; TenderCoal sent: host's own client $h, client $c"
[ "$c" = 1 ] || fail "one shovelful from the tender sent $c TenderCoal from the client"
[ "$h" = 0 ] || fail "the host's own client sent $h TenderCoal"
echo PASS
