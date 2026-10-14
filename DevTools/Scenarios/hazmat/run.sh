#!/usr/bin/env bash
# Hazmat ground tiles (fire and spills): the host spills gasoline and sets it alight, the client
# sets a second spill alight, and the client's own spill must do nothing (the ground is the
# host's). Both peers must show the same tiles, the same burning tiles with flames, and about the
# same liquid (it is sent rounded, and burns between updates).
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/hazmat
fail() { echo "FAIL: $*"; exit 1; }
# a spot away from the gate's other scenarios
X1=7260; Z1=6960; X2=7290; Z2=6960; X3=7320; Z3=6960
$D eval -t host -m coroutine --timeout 60 "yield return LocalPlayer.TeleportToGround(7275f, 6940f);" >/dev/null
$D eval -t client -m coroutine --timeout 60 "yield return LocalPlayer.TeleportToGround(7280f, 6940f);" >/dev/null
check() { # label x z: compare host and client within 25 m of the point
  h=$($D eval -t host -m body -a x=$2 -a z=$3 -f $P/tiles.cs); c=$($D eval -t client -m body -a x=$2 -a z=$3 -f $P/tiles.cs)
  node -e '
    const v=s=>Object.values(JSON.parse(s).results)[0].result;
    const h=v(process.argv[1]), c=v(process.argv[2]); const bad=[];
    if (h.tiles!==c.tiles) bad.push("tiles "+h.tiles+"/"+c.tiles);
    if (h.burning!==c.burning || h.flames!==c.flames || c.flames!==c.burning) bad.push("burning "+h.burning+"/"+c.burning+", flames "+h.flames+"/"+c.flames);
    if (Math.abs(h.liquid-c.liquid) > Math.max(200, 0.1*h.liquid)) bad.push("liquid "+h.liquid+"/"+c.liquid);
    console.log("   "+process.argv[3]+": tiles "+h.tiles+"/"+c.tiles+", liquid "+h.liquid+"/"+c.liquid+" L, burning "+h.burning+"/"+c.burning+", flames "+h.flames+"/"+c.flames+" (host/client)"+(bad.length?" - "+bad.join("; "):""));
    process.exit(bad.length?1:0);' "$h" "$c" "$1" || fail "$1"
  echo "$h" | node -e 'const r=Object.values(JSON.parse(require("fs").readFileSync(0,"utf8")).results)[0].result;console.log(r.tiles+" "+r.burning)'; }
echo "== host spills 5000 L of gasoline"
$D eval -t host -m coroutine -a x=$X1 -a z=$Z1 -a amount=5000 -a cargo=Gasoline -f $P/spill.cs >/dev/null; sleep 5
r=$(check "spill" $X1 $Z1 | tee /dev/stderr | tail -1); [ "${r%% *}" -gt 0 ] || fail "no tiles from the spill"
echo "== host sets it alight"
$D eval -t host -m coroutine -a x=$X1 -a z=$Z1 -f $P/ignite.cs >/dev/null; sleep 6
r=$(check "host's fire" $X1 $Z1 | tee /dev/stderr | tail -1); [ "${r##* }" -gt 0 ] || fail "nothing burning"
echo "== host spills again; the client sets that one alight"
$D eval -t host -m coroutine -a x=$X2 -a z=$Z2 -a amount=5000 -a cargo=Gasoline -f $P/spill.cs >/dev/null; sleep 4
$D eval -t client -m coroutine -a x=$X2 -a z=$Z2 -f $P/ignite.cs >/dev/null; sleep 6
r=$(check "client's fire" $X2 $Z2 | tee /dev/stderr | tail -1); [ "${r##* }" -gt 0 ] || fail "the client's ignition didn't reach the host"
echo "== the client spills on its own"
$D eval -t client -m coroutine -a x=$X3 -a z=$Z3 -a amount=5000 -a cargo=Gasoline -f $P/spill.cs >/dev/null; sleep 4
h=$($D eval -t host -m body -a x=$X3 -a z=$Z3 -f $P/tiles.cs | node -e 'const r=Object.values(JSON.parse(require("fs").readFileSync(0,"utf8")).results)[0].result;console.log(r.tiles)')
check "client's own spill" $X3 $Z3 >/dev/null || fail "the client's own spill changed its tiles"
echo "   nothing new on either game"
echo PASS
