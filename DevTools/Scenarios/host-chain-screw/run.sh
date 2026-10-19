#!/usr/bin/env bash
# The host's own player works a coupling's chain and screw, with a client watching (the host-follower
# family, docs/design/host-local-session.md, stage 1). Before stage 1 the host's screw used in a state
# the mod didn't expect (CouplerChainInteractionPatch's Start fallback) went to the server through the
# host's own client, which applied it to the host's coupler a second time: the screw button stopped
# taking input and the coupler stayed marked in use.
#  1. The host spawns a loco and a wagon, coupled, near its player; the client comes to watch.
#  2. The host uncouples them by hand and couples them again (manual-uncouple's steps), then loosens
#     and tightens the screw twice, and the mod's screw postfix runs on the other car's chain (the
#     Start path).
#  3. On both games: coupled, in one trainset, the chain tight, the same chain states, the hooked
#     chain's screw button taking input and each knob taking input as vanilla has it in its state
#     (off in Attached_Tight and Other_Attached*); on the host, neither car's end marked in use.
#   WAGON=livery (default BoxcarBrown)  PAUSE=seconds between hand steps (default 1)
# A player host only: a dedicated host has no player to work the chain.
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
# A failed dvmp call prints its output (the eval's compile error or exception) instead of the run
# stopping silently under set -e with it captured in a variable or sent to /dev/null.
dvmp_bin=$D; D=dvmp_checked
dvmp_checked() { local out; if ! out=$("$dvmp_bin" "$@"); then echo "FAIL: dvmp ${*:1:3} failed: ${out:-no output}" >&2; return 1; fi; printf '%s\n' "$out"; }
P=DevTools/Scenarios/host-chain-screw
M=DevTools/Scenarios/manual-uncouple
WAGON=${WAGON:-BoxcarBrown}; PAUSE=${PAUSE:-1}
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
fail() { echo "FAIL: $*"; exit 1; }
cars=""
cleanup() {
  for c in $cars; do "$dvmp_bin" eval -t host -m body -a id=$c 'Trains.Delete(Trains.Find(ctx.Args["id"])); return 0;' >/dev/null 2>&1 || true; done
  "$dvmp_bin" eval -t host -m coroutine 'yield return LocalPlayer.TeleportToGround(7194f, 6873f);' >/dev/null 2>&1 || true
  "$dvmp_bin" eval -t client -m coroutine 'yield return LocalPlayer.TeleportToGround(7190f, 6880f);' >/dev/null 2>&1 || true
}
trap cleanup EXIT

s=$($D eval -t host -m coroutine --timeout 60 -a wagon=$WAGON -f $P/setup.cs)
L=$(echo "$s" | field loco); W=$(echo "$s" | field wagon)
A="-a loco=$L -a wagon=$W -a locoFront=$(echo "$s" | field locoFront) -a wagonFront=$(echo "$s" | field wagonFront)"
cars="$L $W"
echo "== $L and $W coupled near the host; the client comes to watch"
$D eval -t client -m coroutine --timeout 60 -a x=$(echo "$s" | field x) -a z=$(echo "$s" | field z) \
  'yield return LocalPlayer.TeleportToGround(float.Parse(ctx.Args["x"], System.Globalization.CultureInfo.InvariantCulture) - 3f, float.Parse(ctx.Args["z"], System.Globalization.CultureInfo.InvariantCulture) + 3f);' >/dev/null
sleep 2

echo "== the host uncouples them by hand"
u=$($D eval -t host -m coroutine --timeout 90 $A -a pause=$PAUSE -f $M/uncouple.cs)
echo "   $(echo "$u" | field steps)"
sleep 2
echo "== the host couples them again by hand"
u=$($D eval -t host -m coroutine --timeout 90 $A -a pause=$PAUSE -f $M/couple.cs)
echo "   $(echo "$u" | field steps)"
sleep 2
echo "== the host works the screw: loosen and tighten twice, then the Start path"
u=$($D eval -t host -m coroutine --timeout 90 $A -f $P/screw.cs)
echo "   $(echo "$u" | field chain) chain: $(echo "$u" | field steps)"
sleep 3

h=$($D eval -t host -m coroutine --timeout 30 $A -f $P/state.cs); c=$($D eval -t client -m coroutine --timeout 30 $A -f $P/state.cs)
for who in host client; do
  r=$([ $who = host ] && echo "$h" || echo "$c")
  echo "   $who: coupled $(echo "$r" | field coupled), one set $(echo "$r" | field sameSet), chain $(echo "$r" | field chain) ($(echo "$r" | field states)), screw/knob $(echo "$r" | field allowed) (S/K: takes input), $(echo "$r" | field inUse)"
done
for who in host client; do
  r=$([ $who = host ] && echo "$h" || echo "$c")
  [ "$(echo "$r" | field coupled)" = true ] && [ "$(echo "$r" | field sameSet)" = true ] || fail "the cars aren't coupled on the $who"
  [ "$(echo "$r" | field tight)" = true ] || fail "the chain isn't tight on the $who ($(echo "$r" | field chain))"
  p=$(echo "$r" | field problems); [ -z "$p" ] || fail "on the $who: $p"
done
[ "$(echo "$h" | field chain)" = "$(echo "$c" | field chain)" ] || fail "the chain differs: host $(echo "$h" | field chain), client $(echo "$c" | field chain)"
case "$(echo "$h" | field inUse)" in *"in use"*) fail "the host's server still has a coupler marked in use ($(echo "$h" | field inUse))";; esac
echo PASS
