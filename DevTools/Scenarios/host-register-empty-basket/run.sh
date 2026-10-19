#!/usr/bin/env bash
# The host's player at a service station's cash register (one with modules, not a shop's) with an
# empty basket and cash already deposited, a client watching (the host-follower family:
# docs/design/host-local-session.md). The host pays cash in: nothing is owed, so nothing may change
# on either game, the host's wallet keeps its money, and the request finishes on the server's
# answer rather than a timeout. Then the host presses Buy: vanilla refuses an empty basket, and
# both games must keep the deposited cash (the host's buy used to be replayed on clients whether
# or not it happened, emptying their register). Then the client presses Buy: the host refuses it,
# and the client must keep the cash too (the refusal used to empty the client's register). Skips
# on a dedicated host: nobody plays there.
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
# A failed eval says so, rather than ending the run silently under set -e.
dvmp_bin=$D; D=dvmp_checked
dvmp_checked() { local out; if ! out=$("$dvmp_bin" "$@"); then echo "FAIL: dvmp ${*:1:3} failed: ${out:-no output}" >&2; return 1; fi; printf '%s\n' "$out"; }
P=DevTools/Scenarios/host-register-empty-basket
U="using Multiplayer.Shared.Stations; using Multiplayer.Shared.Protocol.Packets.Common;"
SEED=50; PAY=20
fail() { echo "FAIL: $*"; exit 1; }
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
near() { node -e 'process.exit(Math.abs(+process.argv[1]-(+process.argv[2]))<0.01?0:1)' "$1" "$2"; }

if [ "$($D eval -t host -m body 'return new { d = Multiplayer.Shared.Session.NetworkLifecycle.Instance.IsDedicatedHost };' | field d)" = true ]; then
  echo "SKIP: a dedicated host has no player to use the register"; exit 0
fi

# the service station by the shop scenario's station, (13421, 3618)
$D eval -t client -m coroutine --timeout 60 'yield return LocalPlayer.TeleportToGround(13421f, 3618f);' >/dev/null
$D eval -t host -m coroutine --timeout 60 'yield return LocalPlayer.TeleportToGround(13424f, 3622f);' >/dev/null
sleep 3
f=$($D eval -t host -m coroutine -u "$U" -a op=find -a x=13421 -a z=3618 -f $P/register.cs)
R=$(echo "$f" | field register); X=$(echo "$f" | field x); Z=$(echo "$f" | field z)
echo "register $R ($(echo "$f" | field name)) at ($X, $Z)"
# both players within reach of the register (the server checks where each last said it was)
$D eval -t host -m coroutine --timeout 60 "yield return LocalPlayer.TeleportToGround(${X}f + 1f, ${Z}f);" >/dev/null
$D eval -t client -m coroutine --timeout 60 "yield return LocalPlayer.TeleportToGround(${X}f - 1f, ${Z}f + 1f);" >/dev/null
sleep 3
state() { $D eval -t $1 -m coroutine -u "$U" -a op=${2:-state} -a register=$R ${3:-} -f $P/register.cs; }
show() { echo "   $1: host deposited \$$(echo "$2" | field deposited), basket $(echo "$2" | field units), busy $(echo "$2" | field busy); client deposited \$$(echo "$3" | field deposited), basket $(echo "$3" | field units)"; }

echo "== \$$SEED deposited, basket empty"
state host seed "-a amount=$SEED" >/dev/null; sleep 1
h=$(state host); c=$(state client); show "before" "$h" "$c"
[ "$(echo "$h" | field units)" = 0 ] && [ "$(echo "$c" | field units)" = 0 ] || fail "the basket isn't empty"
near "$(echo "$h" | field deposited)" $SEED && near "$(echo "$c" | field deposited)" $SEED || fail "the deposit didn't reach both games"
wallet0=$(echo "$h" | field wallet)

echo "== host pays \$$PAY in"
d=$(state host deposit "-a amount=$PAY"); sleep 1
h=$(state host); c=$(state client); show "after" "$h" "$c"
echo "   host's request waited $(echo "$d" | field busyFor) s for the server; wallet $wallet0 -> $(echo "$h" | field wallet)"
near "$(echo "$h" | field deposited)" $SEED || fail "the host's register changed: deposited \$$(echo "$h" | field deposited)"
near "$(echo "$c" | field deposited)" $SEED || fail "the client's register changed: deposited \$$(echo "$c" | field deposited)"
near "$(echo "$h" | field wallet)" "$wallet0" || fail "the host's wallet changed: $wallet0 -> $(echo "$h" | field wallet)"
[ "$(echo "$h" | field busy)" = false ] || fail "the host's request is still waiting"
node -e 'process.exit(+process.argv[1]<1.5?0:1)' "$(echo "$d" | field busyFor)" || fail "the host's request waited $(echo "$d" | field busyFor) s: no answer from the server, only the timeout"

echo "== host presses Buy"
state host buy >/dev/null; sleep 1
h=$(state host); c=$(state client); show "after" "$h" "$c"
near "$(echo "$h" | field deposited)" $SEED || fail "the host's register changed: deposited \$$(echo "$h" | field deposited)"
near "$(echo "$c" | field deposited)" $SEED || fail "the client emptied its register for a buy that didn't happen: deposited \$$(echo "$c" | field deposited), the host's \$$(echo "$h" | field deposited)"

echo "== client presses Buy"
state client buy >/dev/null; sleep 2
h=$(state host); c=$(state client); show "after" "$h" "$c"
near "$(echo "$h" | field deposited)" $SEED || fail "the host's register changed: deposited \$$(echo "$h" | field deposited)"
near "$(echo "$c" | field deposited)" $SEED || fail "the client emptied its register on the host's refusal: deposited \$$(echo "$c" | field deposited), the host's \$$(echo "$h" | field deposited)"
[ "$(echo "$c" | field busy)" = false ] || fail "the client's request is still waiting"

# leave the register empty again
state host seed "-a amount=0" >/dev/null
echo PASS
