#!/usr/bin/env bash
# Cab controls from a client (#73). For each loco: every control that feeds a sim port is hooked, so
# the client can take its lease; every toggle switch flipped with Use() (what a VR hand does, it
# never grabs) reaches the host, and flipping back does too; on the DE2 a grabbed wipers turn
# reaches the host. No control lease is left held afterwards.
#   LOCOS="LocoDE2 LocoDM3" (default)
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/cab-controls
LOCOS=${LOCOS:-"LocoDE2 LocoDM3"}
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
result() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}console.log(JSON.stringify(x.result))'; }
loco=""
cleanup() { [ -n "$loco" ] && $D eval -t host -m body -a id=$loco 'Trains.Delete(Trains.Find(ctx.Args["id"])); return 0;' >/dev/null 2>&1 || true; }
trap cleanup EXIT
fail=0

# Client values (JSON object port -> value) against the host's, after a change.
compare() { # what clientJson
  local ports host
  ports=$(echo "$2" | node -e 'console.log(Object.keys(JSON.parse(require("fs").readFileSync(0,"utf8"))).join(","))')
  host=$($D eval -t host -m body -a loco=$loco -a ports="$ports" -f $P/ports.cs | result)
  node -e '
    const c=JSON.parse(process.argv[1]), h=JSON.parse(process.argv[2]);
    const bad=Object.keys(c).filter(p=>Math.abs(c[p]-h.values[p])>0.01).map(p=>p+" client "+c[p]+" host "+h.values[p]);
    console.log("   "+process.argv[3]+": "+Object.keys(c).length+" port(s)"+(bad.length?", differ: "+bad.join("; "):", all match")+(h.leases?", "+h.leases+" lease(s) still held":""));
    process.exit(bad.length||h.leases?1:0);' "$2" "$host" "$1"
}

for type in $LOCOS; do
  echo "== $type"
  # The handbrake on: a loco left free rolls away on a grade and leaves the client too far away for
  # the host to grant it a control.
  loco=$($D eval -t host -m coroutine -a t=$type 'var car = Trains.SpawnNearPlayer(ctx.Args["t"]); car.brakeSystem.SetHandbrakePosition(1f); for (int i = 0; i < 120; i++) yield return null; ctx.Result = new { id = car.ID };' | field id)
  $D eval -t client -m coroutine -a loco=$loco 'yield return Trains.Board(Trains.Find(ctx.Args["loco"])); yield return new WaitForSeconds(4);' >/dev/null

  h=$($D eval -t client -m body -a loco=$loco -f $P/hooked.cs)
  unhooked=$(echo "$h" | field unhooked)
  echo "   $(echo "$h" | field feeders) port-fed controls, unhooked: $unhooked"
  [ "$unhooked" = "[]" ] || fail=1

  on=$($D eval -t client -m coroutine --timeout 60 -a loco=$loco -f $P/toggle.cs | result)
  compare "toggle switches flipped" "$on" || fail=1
  off=$($D eval -t client -m coroutine --timeout 60 -a loco=$loco -f $P/toggle.cs | result)
  compare "and flipped back" "$off" || fail=1

  if [ "$type" = LocoDE2 ]; then
    w=$($D eval -t client -m coroutine --timeout 30 -a loco=$loco -a control=C_Wipers -a value=0.75 -f $P/grab-turn.cs | result)
    compare "wipers grabbed and turned" "$w" || fail=1
  fi

  $D eval -t client -m coroutine 'yield return LocalPlayer.TeleportToGround(7190f, 6880f);' >/dev/null
  cleanup; loco=""
done

[ $fail = 0 ] && echo "PASS" || { echo "FAIL"; exit 1; }
