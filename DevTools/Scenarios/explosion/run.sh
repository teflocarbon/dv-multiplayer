#!/usr/bin/env bash
# Explosions (Discord: "explosions aren't synced"; cars derailed by an explosion on one screen
# only). For a loco's fuel explosion and a gasoline tank car's cargo explosion: the client trying
# to set one off itself must do nothing anywhere; the host setting it off must leave both peers
# with the same burnt-out car, the same derailments and the same positions.
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/explosion
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
fail() { echo "FAIL: $*"; exit 1; }
cars=""
cleanup() { for c in $cars; do $D eval -t host -m body -a id=$c 'Trains.Delete(Trains.Find(ctx.Args["id"])); return 0;' >/dev/null 2>&1 || true; done; }
trap cleanup EXIT
compare() { # cars: both peers must agree on exploded, derailed and position (1 m)
  h=$($D eval -t host -m body -a cars=$1 -f $P/state.cs); c=$($D eval -t client -m body -a cars=$1 -f $P/state.cs)
  node -e '
    const v=s=>Object.values(JSON.parse(s).results)[0].result;
    const h=v(process.argv[1]), c=v(process.argv[2]); const bad=[]; const out=[];
    h.forEach((a,i)=>{const b=c[i];const d=Math.hypot(a.abs.x-b.abs.x,a.abs.z-b.abs.z);
      out.push(a.id+": exploded "+a.exploded+"/"+b.exploded+", derailed "+a.derailed+"/"+b.derailed+", apart "+d.toFixed(2)+" m");
      if(a.exploded!==b.exploded||a.derailed!==b.derailed||d>1)bad.push(a.id);});
    console.log("   "+out.join(" | ")+" (host/client)"); if(bad.length){console.log("FAIL: peers disagree on "+bad.join(", "));process.exit(1);}' "$h" "$2"; }
exploded() { $D eval -t $1 -m body -a cars=$2 -f $P/state.cs | node -e 'console.log(Object.values(JSON.parse(require("fs").readFileSync(0,"utf8")).results)[0].result[0].exploded)'; }

echo "== loco fuel explosion (a wagon beside it)"
s=$($D eval -t host -m coroutine --timeout 60 -a wagon=BoxcarBrown -f DevTools/Scenarios/manual-uncouple/setup.cs)
L=$(echo "$s" | field loco); W=$(echo "$s" | field wagon); cars="$L $W"; sleep 3
$D eval -t client -m coroutine -a car=$L -f $P/boom.cs >/dev/null; sleep 2
[ "$(exploded host $L)" = "false" ] && [ "$(exploded client $L)" = "false" ] || fail "a client set off an explosion"
echo "   client's own attempt: nothing happened"
$D eval -t host -m coroutine -a car=$L -f $P/boom.cs >/dev/null; sleep 8
[ "$(exploded client $L)" = "true" ] || fail "the client didn't see the host's explosion"
compare $L,$W "$($D eval -t client -m body -a cars=$L,$W -f $P/state.cs)"

echo "== gasoline tank explosion"
T=$($D eval -t host -m coroutine 'var c = Trains.SpawnNearPlayer("TankChrome"); c.brakeSystem.SetHandbrakePosition(1f); for (int i = 0; i < 90; i++) yield return null; c.logicCar.LoadCargo(c.cargoCapacity, DV.Globals.G.Types.cargos.First(x => x.id == "Gasoline").v1); for (int i = 0; i < 180; i++) yield return null; ctx.Result = new { id = c.ID };' | field id)
cars="$cars $T"
$D eval -t client -m coroutine -a car=$T -a kind=cargo -f $P/boom.cs >/dev/null; sleep 2
[ "$(exploded host $T)" = "false" ] && [ "$(exploded client $T)" = "false" ] || fail "a client set off a cargo explosion"
echo "   client's own attempt: nothing happened"
$D eval -t host -m coroutine -a car=$T -a kind=cargo -f $P/boom.cs >/dev/null; sleep 8
[ "$(exploded client $T)" = "true" ] || fail "the client didn't see the host's cargo explosion"
compare $T "$($D eval -t client -m body -a cars=$T -f $P/state.cs)"
echo PASS
