#!/usr/bin/env bash
# The host's own world takes other players' actions, and the host's actions reach the others, each
# applied once (docs/design/host-local-session.md, stage 2: the server applies junctions,
# turntables and time skips itself, not the host's own client). One player throws the junction
# nearest it, turns the nearest turntable half round (both players go to it), fast-travels and sleeps;
# after each, both games must agree: the same branch (switched exactly once on each game), the same
# turntable rotation, each clock skipped forward once, and the same shared sleep cooldown.
#   WHO=client (default) or host: who acts. MODE=all (default), or one of junction, turntable,
#   fast-travel, sleep. SKIP=3600: the time skip in game seconds.
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
# A failed eval says so, rather than ending the run silently under set -e.
dvmp_bin=$D; D=dvmp_checked
dvmp_checked() { local out; if ! out=$("$dvmp_bin" "$@"); then echo "FAIL: dvmp ${*:1:3} failed: ${out:-no output}" >&2; return 1; fi; printf '%s\n' "$out"; }
P=DevTools/Scenarios/host-actions
WHO=${WHO:-client}; MODE=${MODE:-all}; SKIP=${SKIP:-3600}
[ "$WHO" = host ] && OTHER=client || OTHER=host
U="using Multiplayer.Shared.World;"
fail() { echo "FAIL: $*"; exit 1; }
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
want() { [ "$MODE" = all ] || [ "$MODE" = "$1" ]; }
host_warnings() { $D logs -t host -l warning -n 2000 2>/dev/null | grep -cE "(Ignored (CommonChangeJunctionPacket|CommonRotateTurntablePacket|ClientboundTimeAdvancePacket) sent to the host's own client|which doesn't exist here)" || true; }

if [ "$WHO" = host ] && [ "$($D eval -t host -m body 'return new { d = Multiplayer.Shared.Session.NetworkLifecycle.Instance.IsDedicatedHost };' | field d)" = true ]; then
  echo "SKIP: a dedicated host has no player to act"; exit 0
fi

pick=$($D eval -t $WHO -m body -u "$U" -f $P/pick.cs)
J=$(echo "$pick" | field junction); T=$(echo "$pick" | field turntable)
echo "$WHO acts: junction $J ($(echo "$pick" | field junctionDistance) m away), turntable $T ($(echo "$pick" | field turntableDistance) m away)"
state() { $D eval -t $1 -m body -u "$U" -a junction=$J -a turntable=$T ${2:+-a watch=1} -f $P/state.cs; }
warnings0=$(host_warnings)

# Game seconds a game's clock moved between two states, less the skip: should be about the time
# that passed (a few seconds of play), and nowhere near a second skip.
clock_moved() { node -e 'const a=+process.argv[1],b=+process.argv[2];console.log(Math.round((b-a)*86400))' "$1" "$2"; }
check_skip() { # label host-before host-after client-before client-after
  local h c; h=$(clock_moved "$(echo "$2" | field clock)" "$(echo "$3" | field clock)"); c=$(clock_moved "$(echo "$4" | field clock)" "$(echo "$5" | field clock)")
  echo "   clocks moved: host ${h}s, client ${c}s (skip ${SKIP}s)"
  for v in $h $c; do
    node -e 'const v=+process.argv[1],s=+process.argv[2];process.exit(v>=s-60&&v<s+Math.max(900,s/4)?0:1)' "$v" "$SKIP" ||
      fail "$1: a clock moved ${v}s for a ${SKIP}s skip (host ${h}s, client ${c}s): not skipped, or skipped twice"
  done
}

if want junction; then
  echo "== $WHO throws junction $J"
  h0=$(state host 1); c0=$(state client 1)
  [ "$(echo "$h0" | field branch)" = "$(echo "$c0" | field branch)" ] || fail "the junction differs before the test: host $(echo "$h0" | field branch), client $(echo "$c0" | field branch)"
  $D eval -t $WHO -m coroutine --timeout 30 -u "$U" -a action=junction -a junction=$J -a turntable=$T -f $P/act.cs >/dev/null
  sleep 2
  h=$(state host); c=$(state client)
  echo "   branch $(echo "$h0" | field branch) -> host $(echo "$h" | field branch), client $(echo "$c" | field branch); switched host $(echo "$h" | field switched)x, client $(echo "$c" | field switched)x"
  [ "$(echo "$h" | field branch)" != "$(echo "$h0" | field branch)" ] || fail "the host's junction didn't switch"
  [ "$(echo "$h" | field branch)" = "$(echo "$c" | field branch)" ] || fail "the junction differs"
  [ "$(echo "$h" | field switched)" = 1 ] || fail "the host's junction switched $(echo "$h" | field switched) times"
  [ "$(echo "$c" | field switched)" = 1 ] || fail "the client's junction switched $(echo "$c" | field switched) times"
fi

if want turntable; then
  [ "$T" != 0 ] || fail "no turntable found"
  # A turntable only turns where the world is loaded: both players go to its controls.
  TX=$(echo "$pick" | field tx); TZ=$(echo "$pick" | field tz)
  $D eval -t $WHO -m coroutine --timeout 90 "yield return LocalPlayer.TeleportToGround(${TX}f + 2f, ${TZ}f + 2f);" >/dev/null
  $D eval -t $OTHER -m coroutine --timeout 90 "yield return LocalPlayer.TeleportToGround(${TX}f - 6f, ${TZ}f + 6f);" >/dev/null
  sleep 3
  echo "== $WHO turns turntable $T (players at its controls, ($TX, $TZ))"
  a=$($D eval -t $WHO -m coroutine --timeout 60 -u "$U" -a action=turntable -a junction=$J -a turntable=$T -f $P/act.cs)
  echo "   $WHO turned it $(echo "$a" | field before) -> $(echo "$a" | field rotation) degrees"
  node -e 'const d=Math.abs(+process.argv[1]-(+process.argv[2]))%360;process.exit(Math.min(d,360-d)>1?0:1)' "$(echo "$a" | field before)" "$(echo "$a" | field rotation)" || fail "the turntable didn't turn"
  sleep 2
  h=$(state host); c=$(state client)
  echo "   host $(echo "$h" | field rotation), client $(echo "$c" | field rotation) degrees"
  for r in "$(echo "$h" | field rotation)" "$(echo "$c" | field rotation)"; do
    node -e 'const d=Math.abs(+process.argv[1]-(+process.argv[2]))%360;process.exit(Math.min(d,360-d)<0.5?0:1)' "$r" "$(echo "$a" | field rotation)" || fail "the turntable differs: host $(echo "$h" | field rotation), client $(echo "$c" | field rotation), $WHO set $(echo "$a" | field rotation)"
  done
fi

if want fast-travel; then
  echo "== $WHO fast-travels (${SKIP}s)"
  [ "$(echo "$pick" | field fastTravelAdvancesTime)" = true ] || fail "invalid: the host's FastTravelAdvancesTime setting is off"
  h0=$(state host); c0=$(state client)
  $D eval -t $WHO -m coroutine --timeout 30 -u "$U" -a action=fast-travel -a seconds=$SKIP -a junction=$J -a turntable=$T -f $P/act.cs >/dev/null
  sleep 1
  h=$(state host); c=$(state client)
  check_skip "fast travel" "$h0" "$h" "$c0" "$c"
fi

if want sleep; then
  echo "== $WHO sleeps (${SKIP}s)"
  if [ "$WHO" = client ]; then
    # the shared cooldown must have run out, or the host refuses the client's sleep
    cd=$($D eval -t host -m body 'DV.BedSleepingController.Instance.lastWakeTime = DateTime.MinValue; return Globals.G.GameParams.SleepCooldownInHours;' | node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));console.log(Object.values(o.results)[0].result)')
    node -e 'process.exit(+process.argv[1]>=0?0:1)' "$cd" || fail "invalid: sleeping is disabled in this save (SleepCooldownInHours $cd)"
  fi
  h0=$(state host); c0=$(state client)
  a=$($D eval -t $WHO -m coroutine --timeout 90 -u "$U" -a action=sleep -a seconds=$SKIP -a junction=$J -a turntable=$T -f $P/act.cs)
  [ "$(echo "$a" | field stillSleeping)" = false ] || fail "$WHO is still asleep"
  sleep 1
  h=$(state host); c=$(state client)
  check_skip "sleep" "$h0" "$h" "$c0" "$c"
  hw=$(echo "$h" | field wake); cw=$(echo "$c" | field wake)
  echo "   last wake time: host $hw, client $cw"
  node -e 'process.exit(+process.argv[1]>+process.argv[2]?0:1)' "$hw" "$(echo "$h0" | field wake)" || fail "the host's sleep cooldown didn't restart"
  node -e 'process.exit(Math.abs(+process.argv[1]-(+process.argv[2]))*86400<300?0:1)' "$hw" "$cw" || fail "the client's sleep cooldown differs from the host's"
fi

warnings=$(( $(host_warnings) - warnings0 ))
[ "$warnings" -le 0 ] || fail "the host logged $warnings warning(s) about a relayed junction, turntable or time skip"
echo PASS
