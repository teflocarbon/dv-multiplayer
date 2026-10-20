#!/usr/bin/env bash
# Service stations (pit stops and their plugs) with both players at one. One player pulls a
# resource lever and lets it go, then picks up a plug and puts it back home. The other game must
# show the lever held by someone else while it's pulled and free after, and the plug held by that
# player and then nobody, and the server must record who holds the plug. With WHO=host this is the
# host player's own requests, which the server handles in-process
# (docs/design/host-local-session.md, stage 2).
#   WHO=client (default) or host: who acts.
#   MODE=yank (run pit-stop-yank; WHO=client only): instead of putting the plug back, the client
#     walks off with it. Its avatar carries the plug on the host until the hose is stretched, and
#     the server's rope physics yanks it out of the hand (Server/Stations/PropHoseRopePatch): the
#     server has nobody holding it, the client's plug was moved by the yank the host sent, and both
#     games agree whether it ended up snapped home.
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
# A failed eval says so, rather than ending the run silently under set -e.
dvmp_bin=$D; D=dvmp_checked
dvmp_checked() { local out; if ! out=$("$dvmp_bin" "$@"); then echo "FAIL: dvmp ${*:1:3} failed: ${out:-no output}" >&2; return 1; fi; printf '%s\n' "$out"; }
P=DevTools/Scenarios/pit-stop
WHO=${WHO:-client}
MODE=${MODE:-dock}
[ "$WHO" = host ] && OTHER=client || OTHER=host
if [ "$MODE" = yank ] && [ "$WHO" = host ]; then
  echo "SKIP: MODE=yank needs the client's avatar to carry the plug"; exit 0
fi
U="using Multiplayer.Shared.Stations;"
fail() { echo "FAIL: $*"; exit 1; }
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }

DEDICATED=$($D eval -t host -m body 'return new { d = Multiplayer.Shared.Session.NetworkLifecycle.Instance.IsDedicatedHost };' | field d)
if [ "$WHO" = host ] && [ "$DEDICATED" = true ]; then
  echo "SKIP: a dedicated host has no player to act"; exit 0
fi
# A dedicated host's parked body stays where it is: the station is kept loaded on the host while a
# player is near it.
move() { # peer x z (C# float expressions)
  [ "$1" = host ] && [ "$DEDICATED" = true ] && return 0
  $D eval -t $1 -m coroutine --timeout 60 "yield return LocalPlayer.TeleportToGround($2, $3);" >/dev/null
}

# the service station at the shop scenario's station, (13421, 3618)
move client 13421f 3618f; move host 13424f 3622f; sleep 3
f=$($D eval -t host -m body -u "$U" -a op=find -a x=13421 -a z=3618 -f $P/station.cs)
X=$(echo "$f" | field x); Z=$(echo "$f" | field z)
A="-a station=$(echo "$f" | field station) -a plug=$(echo "$f" | field plug) -a resource=$(echo "$f" | field resource)"
echo "station $(echo "$f" | field name): plug $(echo "$f" | field plug), lever for resource $(echo "$f" | field resource)"
# The one who acts stands at the plug: the server only lets a player within reach (4 m) pick it up,
# measured from where that player last said it was.
if [ "$WHO" = host ]; then HX=1; HZ=1; CX=-3; CZ=3; else HX=-3; HZ=3; CX=1; CZ=1; fi
move host "${X}f + ${HX}f" "${Z}f + ${HZ}f"
move client "${X}f + ${CX}f" "${Z}f + ${CZ}f"
sleep 2
state() { $D eval -t $1 -m body -u "$U" -a op=${2:-state} $A ${3:-} -f $P/station.cs; }
# the client gets the station's state when it comes into range
for i in $(seq 1 30); do [ "$(state client | field refreshed)" = true ] && break; sleep 1; done
[ "$(state client | field refreshed)" = true ] || fail "the client never got the station's state"
[ "$(state client | field found)" = true ] && [ "$(state host | field found)" = true ] || fail "the station or plug is missing on a game"
me=$(state $WHO | field me)

echo "== $WHO pulls the lever"
state $WHO lever "-a value=1" >/dev/null; sleep 2
o=$(state $OTHER)
echo "   $OTHER sees it held by another player: $(echo "$o" | field remoteGrabbed)"
[ "$(echo "$o" | field remoteGrabbed)" = true ] || fail "$OTHER doesn't see the lever pulled"
echo "== $WHO lets it go"
state $WHO lever "-a value=0" >/dev/null; sleep 2
o=$(state $OTHER)
[ "$(echo "$o" | field remoteGrabbed)" = false ] || fail "$OTHER still sees the lever pulled"
echo "   $OTHER sees it free"

echo "== $WHO picks the plug up"
state $WHO grab >/dev/null; sleep 2
h=$(state host); o=$(state $OTHER)
echo "   server: held by P$(echo "$h" | field heldBy); $OTHER shows P$(echo "$o" | field holding) holding it ($WHO is P$me)"
[ "$(echo "$h" | field heldBy)" = "$me" ] || fail "the server has the plug held by P$(echo "$h" | field heldBy), not P$me"
if [ "$OTHER" = client ]; then [ "$(echo "$o" | field holding)" = "$me" ] || fail "the client shows P$(echo "$o" | field holding) holding the plug, not P$me"; fi

if [ "$MODE" = yank ]; then
  PLUG=$(echo "$f" | field plug)
  T=$(mktemp -d); trap 'rm -rf "$T"' EXIT
  for t in host client; do
    "$dvmp_bin" eval -t $t -m coroutine --timeout 60 -u "$U" -a plug=$PLUG -a seconds=14 -f $P/yank.cs >"$T/$t.json" &
  done
  sleep 1
  echo "== client walks off with the plug"
  move client "${X}f + 35f" "${Z}f + 35f"
  wait
  h=$(field maxAway <"$T/host.json"); c=$(field maxAway <"$T/client.json")
  echo "   furthest from home: host $h m, client $c m (the hose yanks at $(field limit <"$T/host.json") m from its origin); yank settle seen: host $(field settleSeen <"$T/host.json"), client $(field settleSeen <"$T/client.json")"
  echo "   at the end: host $(field away <"$T/host.json") m from home (at home: $(field atHome <"$T/host.json")), client $(field away <"$T/client.json") m (at home: $(field atHome <"$T/client.json")); server: held by P$(field heldBy <"$T/host.json")"
  [ "$(field heldBy <"$T/host.json")" = 0 ] || fail "the server still has the plug held by P$(field heldBy <"$T/host.json") after the client walked off"
  node -e 'process.exit(+process.argv[1] > 1.5 ? 0 : 1)' "$h" || fail "the host's plug never left home (${h} m): the client's avatar didn't carry it"
  node -e 'process.exit(+process.argv[1] > 1.5 ? 0 : 1)' "$c" || fail "the client's plug never left home (${c} m): the host's yank never reached it"
  [ "$(field atHome <"$T/host.json")" = "$(field atHome <"$T/client.json")" ] || fail "the games disagree whether the plug is home (host $(field atHome <"$T/host.json"), client $(field atHome <"$T/client.json"))"
  echo PASS
  exit 0
fi

echo "== $WHO puts it back home"
state $WHO dock >/dev/null; sleep 2
h=$(state host); o=$(state $OTHER)
echo "   server: held by P$(echo "$h" | field heldBy); $OTHER shows P$(echo "$o" | field holding)"
[ "$(echo "$h" | field heldBy)" = 0 ] || fail "the server still has the plug held by P$(echo "$h" | field heldBy)"
[ "$(echo "$o" | field holding)" = 0 ] || fail "$OTHER still shows P$(echo "$o" | field holding) holding the plug"
echo PASS
