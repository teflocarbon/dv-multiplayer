#!/usr/bin/env bash
# Service stations (pit stops and their plugs) with both players at one. One player pulls a
# resource lever and lets it go, then picks up a plug and puts it back home. The other game must
# show the lever held by someone else while it's pulled and free after, and the plug held by that
# player and then nobody, and the server must record who holds the plug. With WHO=host this is the
# host player's own requests, which the server handles in-process
# (docs/design/host-local-session.md, stage 2).
#   WHO=client (default) or host: who acts.
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/pit-stop
WHO=${WHO:-client}
[ "$WHO" = host ] && OTHER=client || OTHER=host
U="using Multiplayer.Shared.Stations;"
fail() { echo "FAIL: $*"; exit 1; }
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }

if [ "$WHO" = host ] && [ "$($D eval -t host -m body 'return new { d = Multiplayer.Shared.Session.NetworkLifecycle.Instance.IsDedicatedHost };' | field d)" = true ]; then
  echo "SKIP: a dedicated host has no player to act"; exit 0
fi

# the service station at the shop scenario's station, (13421, 3618)
$D eval -t host -m coroutine --timeout 60 'yield return LocalPlayer.TeleportToGround(13424f, 3622f);' >/dev/null
f=$($D eval -t host -m body -u "$U" -a op=find -f $P/station.cs)
X=$(echo "$f" | field x); Z=$(echo "$f" | field z)
A="-a station=$(echo "$f" | field station) -a plug=$(echo "$f" | field plug) -a resource=$(echo "$f" | field resource)"
echo "station $(echo "$f" | field name): plug $(echo "$f" | field plug), lever for resource $(echo "$f" | field resource)"
$D eval -t host -m coroutine --timeout 60 "yield return LocalPlayer.TeleportToGround(${X}f + 3f, ${Z}f + 3f);" >/dev/null
$D eval -t client -m coroutine --timeout 60 "yield return LocalPlayer.TeleportToGround(${X}f - 3f, ${Z}f + 3f);" >/dev/null
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
echo "== $WHO puts it back home"
state $WHO dock >/dev/null; sleep 2
h=$(state host); o=$(state $OTHER)
echo "   server: held by P$(echo "$h" | field heldBy); $OTHER shows P$(echo "$o" | field holding)"
[ "$(echo "$h" | field heldBy)" = 0 ] || fail "the server still has the plug held by P$(echo "$h" | field heldBy)"
[ "$(echo "$o" | field holding)" = 0 ] || fail "$OTHER still shows P$(echo "$o" | field holding) holding the plug"
echo PASS
