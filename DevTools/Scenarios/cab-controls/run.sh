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
#   MODE=no-grab-sequence (#159): an S282A, both players in its cab. The client sets the valves
#   (compressor, dynamo, lubricator, cylinder cocks) with no grab, again and again on the one loco,
#   now the cab's copies, now the outside ones: every change reaches the host. Then the client steps
#   off, out of reach, and turns the outside valves: the host keeps its value and the client's go
#   back to it (left turned, they showed only on the client, for good). Back aboard, its next change
#   reaches the host again. The client must still be aboard after the steps: a rolling loco left
#   it behind in #159. A dedicated host works too (only the client boards).
#   STEPS="external 1;external 0;..." (which copies, value)
#   MODE=cab-reload: the client in an S282A's cab (LOCO=). Its cab reloads and, half a second
#   later, it grabs a lever: hooked, and the grab takes its lease (before 2026-10-20 a cab with a
#   control the old ready check never passed hooked nothing for 2 s; the run says whether this cab
#   has one). The cab stays unloaded 2.5 s and loads: no "failed to load an interior" (logged on
#   every such unload before). It reloads three times in quick succession, each unloaded mid-hook:
#   no hook pass throws, every control ends up hooked.
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
loco=""; tender=""
cleanup() {
  for c in $loco $tender; do "$dvmp_bin" eval -t host -m body -a id=$c 'Trains.Delete(Trains.Find(ctx.Args["id"])); return 0;' >/dev/null 2>&1 || true; done
  loco=""; tender=""
}
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

# The loco, held still (spawn.cs), and its id and tender set; where it is in $spawned.
spawn() { spawned=$($D eval -t host -m coroutine --timeout 150 -a t=$1 -f $P/spawn.cs); loco=$(echo "$spawned" | field id); tender=$(echo "$spawned" | field tender); }
board() { $D eval -t $1 -m coroutine --timeout 60 -a loco=$loco "yield return Trains.Board(Trains.Find(ctx.Args[\"loco\"])); yield return new WaitForSeconds($2);" >/dev/null; }

if [ "${MODE:-}" = no-grab-sequence ]; then
  PORTS="compressorControl.EXT_IN,dynamoControl.EXT_IN,lubricatorControl.EXT_IN,cylinderCock.EXT_IN"
  STEPS=${STEPS:-"external 1;external 0;external 1;cab 0;cab 1;external 0;cab 1;cab 0;external 1;external 0"}
  spawn LocoS282A
  t=$(echo "$spawned" | field tried); [ -z "$t" ] || echo "   spots tried (the loco crept): $t"
  echo "   braked: $(echo "$spawned" | field braked)"
  # A dedicated host has no player to board.
  [ "$($D eval -t host -m body 'return new { d = Multiplayer.Shared.Session.NetworkLifecycle.Instance.IsDedicatedHost };' | field d)" = true ] || board host 2
  board client 4
  # The client sets one copy of each valve (valve.cs), and the host's values must match.
  valve() { $D eval -t client -m coroutine --timeout 30 -a loco=$loco -a which=$1 -a ports=$PORTS -a value=$2 -f $P/valve.cs | field values; }
  IFS=";" read -ra steps <<< "$STEPS"
  last=0
  for step in "${steps[@]}"; do
    set -- $step
    c=$(valve $1 $2); sleep 1
    compare "the $1 copies set to $2" "$c" || fail=1
    last=$2
  done
  # The steps are only the in-reach case while the client is still aboard (#159's ran on a client
  # left behind by its rolling loco).
  r=$($D eval -t client -m body -a loco=$loco 'var car = Trains.Find(ctx.Args["loco"]); return new { d = Math.Round(Vector3.Distance(PlayerManager.PlayerTransform.position, car.transform.position), 1), reach = Math.Round(CarSpawner.Instance.carLiveryToCarLength[car.carLivery], 1) };' | result)
  node -e 'const o=JSON.parse(process.argv[1]);const ok=o.d<=o.reach;console.log("   "+(ok?"ok   ":"FAIL ")+"the client is still aboard: "+o.d+" m from the loco (reach "+o.reach+" m)");process.exit(ok?0:1)' "$r" || fail=1

  echo "== the client steps off, out of reach"
  a=$($D eval -t client -m coroutine --timeout 60 -a loco=$loco -f $P/away.cs | result)
  echo "   $a"
  which=$(echo "$a" | node -e 'console.log(JSON.parse(require("fs").readFileSync(0,"utf8")).external?"external":"cab")')
  turned=$((1 - last))
  c=$(valve $which $turned); sleep 1
  compare "out of reach, the $which copies set to $turned: back to the host's" "$c" || fail=1
  h=$($D eval -t host -m body -a loco=$loco -a ports=$PORTS -f $P/ports.cs | result)
  node -e 'const v=JSON.parse(process.argv[1]).values;const bad=Object.keys(v).filter(p=>Math.abs(v[p]-process.argv[2])>0.01);console.log("   host: "+(bad.length?"FAIL took the change: "+bad.join(", "):"kept "+process.argv[2]));process.exit(bad.length?1:0)' "$h" $last || fail=1

  echo "== back aboard"
  board client 4
  c=$(valve $which $turned); sleep 1
  compare "the $which copies set to $turned" "$c" || fail=1

  moved=$($D eval -t host -m body -a loco=$loco -a x=$(echo "$spawned" | field x) -a y=$(echo "$spawned" | field y) -a z=$(echo "$spawned" | field z)     'float F(string k) => float.Parse(ctx.Args[k], System.Globalization.CultureInfo.InvariantCulture); return Math.Round(Vector3.Distance(Dev.Abs(Trains.Find(ctx.Args["loco"]).transform.position), new Vector3(F("x"), F("y"), F("z"))), 2);' | result)
  echo "   the loco moved $moved m"


  $D eval -t client -m coroutine 'yield return LocalPlayer.TeleportToGround(7190f, 6880f);' >/dev/null
  $D eval -t host -m coroutine 'yield return LocalPlayer.TeleportToGround(7194f, 6873f);' >/dev/null
  [ $fail = 0 ] && echo "PASS" || { echo "FAIL"; exit 1; }
  exit 0
fi

if [ "${MODE:-}" = cab-reload ]; then
  LOCO=${LOCO:-LocoS282A}
  spawn $LOCO
  board client 4
  expect() { node -e 'const o=JSON.parse(process.argv[1]);const ok=eval(process.argv[3]);console.log("   "+(ok?"ok   ":"FAIL ")+process.argv[2]+": "+process.argv[1]);process.exit(ok?0:1)' "$2" "$1" "$3"; }
  # Exceptions and errors from the cab's hook pass, so far in the client's log.
  hook_errors() { { $D logs -t client -l exception -n 2000 2>/dev/null | grep -cE "HookControls|HookInteriorControls|WaitForInterior" || true; $D logs -t client -l error -c "failed to load an interior" -n 2000 2>/dev/null | grep -c '"message"' || true; } | node -e 'console.log(require("fs").readFileSync(0,"utf8").trim().split(/s+/).reduce((a,b)=>a+Number(b||0),0))'; }
  reload() { $D eval -t client -m coroutine --timeout 60 -a loco=$loco -a after=$1 -a quick=$2 -a gap=$3 -f $P/reload.cs | result; }
  errors0=$(hook_errors)

  echo "== the client's cab reloads; it grabs a lever half a second later"
  r=$(reload 0.5 0 0)
  # The 2 s window needs a cab with a control the old ready check never passed. Reported, not
  # required: the next step fails without the fix on any cab.
  node -e 'const o=JSON.parse(process.argv[1]);console.log("   "+(o.oldWait?"the old hook pass waited 2 s on this cab for: "+o.oldWait:"note: no control on this cab kept the old hook pass waiting; the 2 s window is not exercised here"))' "$r"
  expect "hooked in time and the grab took the lease, given back on release" "$r" 'o.hooked && o.owned && o.released' || fail=1
  echo "== the cab unloads for 2.5 s, then loads"
  r=$(reload 2.5 0 2.5)
  expect "hooked and leased" "$r" 'o.hooked && o.owned && o.released' || fail=1
  echo "== the cab reloads three times in quick succession, each unloaded mid-hook"
  r=$(reload 2.5 3 0)
  expect "the last cab's lever hooked and leased" "$r" 'o.hooked && o.owned && o.released' || fail=1
  h=$($D eval -t client -m body -a loco=$loco -f $P/hooked.cs)
  echo "   $(echo "$h" | field feeders) port-fed controls, unhooked: $(echo "$h" | field unhooked)"
  [ "$(echo "$h" | field unhooked)" = "[]" ] || fail=1
  sleep 3   # a stale hook pass from the quick reloads throws once its 2 s are up
  errors=$(( $(hook_errors) - errors0 ))
  echo "   $errors hook error(s) or exception(s) in the client's log ('failed to load an interior', HookControls)"
  [ "$errors" = 0 ] || fail=1

  $D eval -t client -m coroutine 'yield return LocalPlayer.TeleportToGround(7190f, 6880f);' >/dev/null
  [ $fail = 0 ] && echo "PASS" || { echo "FAIL"; exit 1; }
  exit 0
fi

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
  # Held still: a loco left free rolls away on a grade and leaves the client too far away for the
  # host to take its changes.
  spawn $type
  board client 4

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
