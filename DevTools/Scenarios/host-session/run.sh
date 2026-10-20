#!/usr/bin/env bash
# The host's own player, served in-process (docs/design/host-local-session.md, stage 2: chat,
# preferences and position). Player host only; skips on a dedicated host, which has no player.
#  chat:     a chat line from the host reaches the client, and so does a whisper (/w, a command).
#  prefs:    the host changes its character model: the server records it and the client's avatar
#            of the host changes to it. Restored afterwards.
#  position: the host jumps into a DE2's cab from 60 m away and takes a lever in the same frame:
#            granted, because the server reads the host's position from its tracker right before
#            its requests (with only its last position it would be 60 m out of range). The
#            server's copy of the host then matches the host's body, and no lease is left.
#   MODE=all (default), or one of chat, prefs, position. CONTROL=C_Wipers (the lever).
# Exit code 0 = pass. Needs a running pair (dvmp up). Spawns and deletes a DE2.
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
# A failed eval says so, rather than ending the run silently under set -e.
dvmp_bin=$D; D=dvmp_checked
dvmp_checked() { local out; if ! out=$("$dvmp_bin" "$@"); then echo "FAIL: dvmp ${*:1:3} failed: ${out:-no output}" >&2; return 1; fi; printf '%s\n' "$out"; }
P=DevTools/Scenarios/host-session
MODE=${MODE:-all}; CONTROL=${CONTROL:-C_Wipers}
U="using Multiplayer.Shared.Session;"
fail() { echo "FAIL: $*"; exit 1; }
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
want() { [ "$MODE" = all ] || [ "$MODE" = "$1" ]; }
loco=""; restore_model=""
cleanup() {
  [ -n "$loco" ] && "$dvmp_bin" eval -t host -m body -a id=$loco 'Trains.Delete(Trains.Find(ctx.Args["id"])); return 0;' >/dev/null 2>&1 || true
  [ -n "$restore_model" ] && "$dvmp_bin" eval -t host -m coroutine -a action=restore -a id="$restore_model" -f $P/prefs.cs >/dev/null 2>&1 || true
  loco=""; restore_model=""
}
trap cleanup EXIT

if [ "$($D eval -t host -m body -u "$U" 'return new { d = NetworkLifecycle.Instance.IsDedicatedHost };' | field d)" = true ]; then
  echo "SKIP: a dedicated host has no player"; exit 0
fi
host_id=$($D eval -t host -m body -u "$U" 'return new { id = Multiplayer.Server.Session.NetworkServer.Instance.LocalPlayer.PlayerId };' | field id)
seen() { $D eval -t $1 -m body -a text="$2" -f $P/chat-seen.cs | field seen; }

if want chat; then
  nonce="hs$RANDOM$RANDOM"
  echo "== the host says $nonce"
  $D eval -t host -m body -u "$U" -a text="$nonce" 'Multiplayer.Client.Session.NetworkClient.Instance.SendChat(ctx.Args["text"]); return 0;' >/dev/null
  sleep 2
  [ "$(seen client "$nonce")" = true ] || fail "the client's chat doesn't show the host's line"
  echo "   the client sees it"

  name=$($D eval -t client -m body -u "$U" 'return new { n = Multiplayer.Client.Session.NetworkClient.Instance.Username };' | field n)
  echo "== the host whispers $name"
  # The command goes without its slash: Git Bash would turn "/w" into a Windows path.
  $D eval -t host -m body -u "$U" -a to="$name" -a text="w$nonce" \
    'string to = ctx.Args["to"]; Multiplayer.Client.Session.NetworkClient.Instance.SendChat("/w " + (to.Contains(" ") ? "\"" + to + "\"" : to) + " " + ctx.Args["text"]); return 0;' >/dev/null
  sleep 2
  [ "$(seen client "w$nonce")" = true ] || fail "the client's chat doesn't show the host's whisper"
  echo "   the client sees it"
fi

if want prefs; then
  echo "== the host changes its character model"
  p=$($D eval -t host -m coroutine -a action=set -f $P/prefs.cs)
  before=$(echo "$p" | field before); set_to=$(echo "$p" | field set)
  if [ -z "$set_to" ]; then
    echo "   only one character model installed: skipped"
  else
    restore_model=$before
    echo "   $before -> $set_to; the server has $(echo "$p" | field server)"
    [ "$(echo "$p" | field server)" = "$set_to" ] || fail "the server didn't record the host's new character"
    sleep 1
    m=$($D eval -t client -m body -a host=$host_id -a id="$set_to" -f $P/model.cs)
    echo "   the client's avatar of the host: $(echo "$m" | field model)"
    [ "$(echo "$m" | field wears)" = true ] || fail "the client's avatar of the host didn't change model"
    $D eval -t host -m coroutine -a action=restore -a id="$before" -f $P/prefs.cs >/dev/null; restore_model=""
  fi
fi

if want position; then
  loco=$($D eval -t host -m coroutine 'var car = Trains.SpawnNearPlayer("LocoDE2"); car.brakeSystem.SetHandbrakePosition(1f); for (int i = 0; i < 120; i++) yield return null; ctx.Result = new { id = car.ID };' | field id)
  echo "== the host jumps into $loco's cab from 60 m and takes $CONTROL at once"
  r=$($D eval -t host -m coroutine --timeout 60 -a loco=$loco -a control=$CONTROL -f $P/lever.cs)
  echo "   server had the host $(echo "$r" | field farBefore) m from the loco; teleported to $(echo "$r" | field movedAtOnce) m of the cab floor in that frame"
  echo "   lease: $(echo "$r" | field holder); server's copy $(echo "$r" | field serverDistance) m from the host's body; leases after letting go: $(echo "$r" | field leasesAfter)"
  node -e 'process.exit(+process.argv[1] > 40 ? 0 : 1)' "$(echo "$r" | field farBefore)" || fail "the server didn't have the host away from the loco first"
  [ "$(echo "$r" | field holder)" = host ] || fail "the host's lever request was refused: its position was stale"
  node -e 'process.exit(+process.argv[1] < 1 ? 0 : 1)' "$(echo "$r" | field serverDistance)" || fail "the server's copy of the host isn't where the host is"
  [ "$(echo "$r" | field leasesAfter)" = 0 ] || fail "a lease was left held"
fi

echo PASS
