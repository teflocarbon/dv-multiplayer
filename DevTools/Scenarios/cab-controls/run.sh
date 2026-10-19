#!/usr/bin/env bash
# Cab controls from a client (#73). For each loco: every control that feeds a sim port is hooked, so
# the client can take its lease; every toggle switch flipped with Use() (what a VR hand does, it
# never grabs) reaches the host, and flipping back does too; on the DE2 a grabbed wipers turn
# reaches the host. On the S282 and the DE6, valves and windows set with no grab and no Use() (a VR
# hand turning a valve, a click on a window) reach the host too: they take no lease. No control
# lease is left held afterwards.
#   LOCOS="LocoDE2 LocoDM3" (default); "LocoS282A LocoDE6" covers controls set without a grab
#   MODE=authority: one lever, both players in the DE2's cab. The client holds it and turns it: the
#   host's cab has it blocked, the host can't take it, and gets the client's value. Then the
#   reverse: the host holds it (its request handled in-process) and turns it, the client's cab has
#   it blocked, can't take it, and gets the host's value. No lease is left held. CONTROL=C_Wipers.
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
# A failed dvmp call prints its output (the eval's compile error or exception) instead of the run
# stopping silently under set -e with it captured in a variable or sent to /dev/null.
dvmp_bin=$D; D=dvmp_checked
dvmp_checked() { local out; if ! out=$("$dvmp_bin" "$@"); then echo "FAIL: dvmp ${*:1:3} failed: ${out:-no output}" >&2; return 1; fi; printf '%s\n' "$out"; }
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

if [ "${MODE:-}" = authority ]; then
  CONTROL=${CONTROL:-C_Wipers}
  loco=$($D eval -t host -m coroutine -a t=LocoDE2 'var car = Trains.SpawnNearPlayer(ctx.Args["t"]); car.brakeSystem.SetHandbrakePosition(1f); for (int i = 0; i < 120; i++) yield return null; ctx.Result = new { id = car.ID };' | field id)
  $D eval -t host -m coroutine -a loco=$loco 'yield return Trains.Board(Trains.Find(ctx.Args["loco"])); yield return new WaitForSeconds(2);' >/dev/null
  $D eval -t client -m coroutine -a loco=$loco 'yield return Trains.Board(Trains.Find(ctx.Args["loco"])); yield return new WaitForSeconds(4);' >/dev/null
  lever() { $D eval -t $1 -m coroutine --timeout 30 -a loco=$loco -a control=$CONTROL -a action=$2 -a value="${3:-}" -f $P/lever.cs | result; }
  holder() { $D eval -t host -m body -a loco=$loco -a control=$CONTROL -f $P/holder.cs | result; }
  val() { node -e 'console.log(JSON.parse(process.argv[1])[process.argv[2]])' "$1" "$2"; }
  # expect "what" json 'js condition over o'
  expect() { node -e 'const o=JSON.parse(process.argv[1]);const ok=eval(process.argv[3]);console.log("   "+(ok?"ok   ":"FAIL ")+process.argv[2]+": "+process.argv[1]);process.exit(ok?0:1)' "$2" "$1" "$3"; }

  echo "== the client holds $CONTROL"
  c=$(lever client grab 0.75); expect "client grabs and turns it" "$c" 'o.owned && !o.blocked' || fail=1
  h=$(holder); expect "host: the client holds it, blocked in the host's cab, at the client's value" "$h" \
    "o.holder!=='none' && o.holder!=='host' && o.blocked && Math.abs(o.value-$(val "$c" value))<0.01" || fail=1
  h=$(lever host grab); expect "host grabs it: no lease" "$h" '!o.owned && o.blocked' || fail=1
  h=$(holder); expect "host: still the client's" "$h" "o.holder!=='none' && o.holder!=='host'" || fail=1
  $D eval -t host -m coroutine --timeout 30 -a loco=$loco -a control=$CONTROL -a action=release -f $P/lever.cs >/dev/null
  c=$(lever client release); expect "client lets go" "$c" '!o.owned' || fail=1
  h=$(holder); expect "host: nobody holds it, not blocked" "$h" "o.holder==='none' && !o.blocked && o.leases===0" || fail=1

  echo "== the host holds $CONTROL"
  h=$(lever host grab 0.25); expect "host grabs and turns it" "$h" 'o.owned && !o.blocked' || fail=1
  hv=$(val "$h" value)
  h=$(holder); expect "host: the host's player holds it" "$h" "o.holder==='host'" || fail=1
  c=$(lever client grab); expect "client grabs it: no lease, blocked, at the host's value" "$c" \
    "!o.owned && o.blocked && Math.abs(o.value-$hv)<0.01" || fail=1
  h=$(holder); expect "host: still the host's" "$h" "o.holder==='host'" || fail=1
  $D eval -t client -m coroutine --timeout 30 -a loco=$loco -a control=$CONTROL -a action=release -f $P/lever.cs >/dev/null
  h=$(lever host release); expect "host lets go" "$h" '!o.owned' || fail=1
  h=$(holder); expect "host: nobody holds it" "$h" "o.holder==='none' && o.leases===0" || fail=1
  c=$(lever client release); expect "client: not blocked" "$c" '!o.blocked' || fail=1

  $D eval -t client -m coroutine 'yield return LocalPlayer.TeleportToGround(7190f, 6880f);' >/dev/null
  $D eval -t host -m coroutine 'yield return LocalPlayer.TeleportToGround(7194f, 6873f);' >/dev/null
  [ $fail = 0 ] && echo "PASS" || { echo "FAIL"; exit 1; }
  exit 0
fi

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

  # Controls that never ask for a lease; the host refused them all before 2026-10-17.
  unleased=$(case $type in
    LocoS282A) echo "compressorControl.EXT_IN,dynamoControl.EXT_IN,cylinderCock.EXT_IN,lubricatorControl.EXT_IN" ;;
    LocoDE6) echo "windowL1.EXT_IN,windowR1.EXT_IN" ;;
  esac)
  if [ -n "$unleased" ]; then
    s=$($D eval -t client -m coroutine --timeout 30 -a loco=$loco -a ports=$unleased -a value=1 -f $P/set.cs | result)
    compare "set without a grab" "$s" || fail=1
    s=$($D eval -t client -m coroutine --timeout 30 -a loco=$loco -a ports=$unleased -a value=0 -f $P/set.cs | result)
    compare "and set back" "$s" || fail=1
  fi

  if [ "$type" = LocoDE2 ]; then
    w=$($D eval -t client -m coroutine --timeout 30 -a loco=$loco -a control=C_Wipers -a value=0.75 -f $P/grab-turn.cs | result)
    compare "wipers grabbed and turned" "$w" || fail=1
  fi

  $D eval -t client -m coroutine 'yield return LocalPlayer.TeleportToGround(7190f, 6880f);' >/dev/null
  cleanup; loco=""
done

[ $fail = 0 ] && echo "PASS" || { echo "FAIL"; exit 1; }
