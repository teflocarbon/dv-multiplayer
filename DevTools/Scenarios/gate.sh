#!/usr/bin/env bash
# Runs every scenario against a running pair (dvmp up) and summarises. Exit code 0 = all passed.
#   ONLY="rejoin recall" to run a subset (by scenario or run name). Each run's output is in
#   $LOG_DIR (default a temp dir).
#   PAIRS=N: run on N host+client pairs at once (dvmp sessions p1..pN). REPEAT=N: each run N
#   times (ONLY=host-stall REPEAT=6 PAIRS=3 hunts an intermittent bug). EXCLUSIVE= overrides
#   which scenarios run alone at the end.
#   UP_ARGS: extra `dvmp up` arguments for the pairs it starts, e.g. UP_ARGS=--dedicated to run
#   against a dedicated host (docs/design/dedicated-host.md).
cd "$(dirname "$0")/../.."
export DVMP=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
S=DevTools/Scenarios
LOG_DIR=${LOG_DIR:-$(mktemp -d)}; mkdir -p "$LOG_DIR"
runs=( # name | scenario | variables
  "train-items|train-items|"
  "train-items-host-throw-moving|train-items|WHO=host MODE=throw WHEN=moving"
  "lost-and-found|lost-and-found|"
  "recall|recall|"
  "recall-taken|recall|TAKEN=1"
  "containers|containers|"
  "containers-briefcase-flashlight|containers|CONTAINER=ItemContainerBriefcase PREFAB=Flashlight"
  "manual-uncouple|manual-uncouple|N=3"
  "manual-recouple-lag|manual-uncouple|MODE=recouple N=3 LAG=150"
  "uncouple-moving|uncouple-moving|"
  "uncouple-moving-loco-lag|uncouple-moving|LAST=LocoDE2 LAG=150 SPEED=10"
  "couple-impact|couple-impact|WAGON_BRAKE=0 LAG=150 SPEEDS=3,5"
  "couple-impact-chain|couple-impact|CHAIN=1 WAGON_BRAKE=0 SPEEDS=2,4"
  "coupler-desync|coupler-desync|LAG=150"
  "derail|pileup|SPEED=25 K=2 N=4 MIN_DERAILED=1 LAG=150"
  "car-pool-reuse|car-pool-reuse|"
  "car-delete|car-delete|"
  "cab-controls|cab-controls|"
  "settle-window|settle-window|"
  "compressor-signal|compressor-signal|"
  "stoker-coal|stoker-coal|"
  "fps-drop|fps-drop|"
  "host-stall|host-stall|SPEED=10 LAG=150"
  "client-derail|client-derail|"
  "far-derail|far-derail|"
  "far-derail-embankment|far-derail-embankment|"
  "dedicated-admin|dedicated-admin|"
  "explosion|explosion|"
  "car-fire|car-fire|"
  "hazmat|hazmat|"
  "rejoin|rejoin|"
  "rejoin-crash|rejoin|CRASH=1"
  "save-reload|save-reload|CYCLES=2"
  "inventory-persist|inventory-persist|"
  "host-player-persist|host-player-persist|"
  "borrowed-disconnect|borrowed-disconnect|"
  "shop|shop|"
  "jobs|jobs|ACCEPT=host HANDIN=client"
  "overview-pickup|overview-pickup|"
  "remote-held-item|remote-held-item|"
  "owned-items|owned-items|"
  "sandbox-cargo|sandbox-cargo|"
  "player-soak|player-soak|DURATION=120"
)
teleport_home() { # puts both players back where every run starts (scenarios move them)
  $DVMP eval -t host -m coroutine --timeout 60 'yield return LocalPlayer.TeleportToGround(7194f, 6873f);' >/dev/null 2>&1
  $DVMP eval -t client -m coroutine --timeout 60 'if (Hands.Held != null) yield return Hands.Drop(); yield return LocalPlayer.TeleportToGround(7190f, 6880f);' >/dev/null 2>&1
  # Scenarios hand the client mugs and don't always take them back: after a few gates its
  # inventory was full (22 mugs) and every "put it in the inventory" step failed. Drop them on the
  # client (a synced drop) and destroy every mug on the host.
  $DVMP eval -t client -m coroutine --timeout 60 'var inv = DV.InventorySystem.Inventory.Instance; foreach (var go in inv.GetItemsArray(false).Where(g => g != null && g.name.StartsWith("Mug")).ToList()) { inv.DropItemFromHandsOrInventory(go); yield return new WaitForSecondsRealtime(0.1f); } yield return new WaitForSecondsRealtime(1f);' >/dev/null 2>&1
  $DVMP eval -t host -m coroutine --timeout 30 'foreach (var m in UnityEngine.Object.FindObjectsOfType<Multiplayer.Components.Networking.World.NetworkedItem>().Where(i => i != null && i.name.StartsWith("Mug")).ToList()) UnityEngine.Object.Destroy(m.gameObject); yield return new WaitForSecondsRealtime(1f);' >/dev/null 2>&1
}
# Coupler joints break at 300 MN, which normal play never reaches: every break is logged as an
# error, and a run that logged one is flagged (coupler-desync breaks one on purpose). Counts the
# current session's games.
# The current session's game logs, as "role path; ...". Each launch's log is named by its launch ID,
# and so is the mod's own (Multiplayer.Debug/multiplayer-<launch ID>.log). A failed run records the
# ones from when it started and from after, so a game that crashed or was relaunched is still named.
game_logs() { $DVMP status 2>/dev/null | node -e 'try{const o=JSON.parse(require("fs").readFileSync(0,"utf8"));console.log((o.agents||[]).map(a=>a.role+" "+(a.logFile||"?")).join("; "))}catch(e){}'; }
note_logs() { echo; echo "game logs at the start: $1"; echo "game logs after: $(game_logs)"; }
coupler_breaks() { local n=0 t; for t in host client; do n=$(( n + $($DVMP logs -t $t -l error -c "Coupler broke" -n 1000 2>/dev/null | grep -c '"message"') )); done; echo $n; }

# ---- parallel mode: PAIRS=N independent host+client pairs (dvmp sessions p1..pN) share a queue
# of runs, REPEAT=N runs each one N times. See docs/design/parallel-test-sessions.md.
if [ -n "${PAIRS:-}" ] || [ -n "${REPEAT:-}" ]; then
  PAIRS=${PAIRS:-1}; REPEAT=${REPEAT:-1}
  # Measure frame timing (run alone) or write saves into the shared profile (alone until each
  # session has its own data folder): these run on p1 after the parallel phase, other pairs down.
  EXCLUSIVE=${EXCLUSIVE:-"fps-drop host-stall player-soak save-reload inventory-persist host-player-persist"}
  Q="$LOG_DIR/.queue"; mkdir -p "$Q/claim" "$Q/result"
  items=(); exclusive=()
  for entry in "${runs[@]}"; do
    IFS='|' read -r name dir vars <<<"$entry"
    [ -n "${ONLY:-}" ] && [[ " $ONLY " != *" $dir "* && " $ONLY " != *" $name "* ]] && continue
    for k in $(seq 1 "$REPEAT"); do
      label=$name; [ "$REPEAT" -gt 1 ] && label="$name#$k"
      if [[ " $EXCLUSIVE " == *" $dir "* ]]; then exclusive+=("$label|$dir|$vars"); else items+=("$label|$dir|$vars"); fi
    done
  done
  run_one() { # label dir vars session
    local label=$1 dir=$2 vars=$3 session=$4 start r first breaks logs
    export DVMP_SESSION=$session
    teleport_home
    start=$(date +%s); breaks=$(coupler_breaks); logs=$(game_logs)
    if env $vars bash "$S/$dir/run.sh" >"$LOG_DIR/$label.$session.log" 2>&1; then r=PASS; else r=FAIL; note_logs "$logs" >>"$LOG_DIR/$label.$session.log"; fi
    first=$( [ $r = FAIL ] && grep -E 'FAIL|rror' "$LOG_DIR/$label.$session.log" | head -1 | cut -c1-90)
    breaks=$(( $(coupler_breaks) - breaks )); [ $breaks -gt 0 ] && first="$first [$breaks coupler break(s) logged]"
    printf '%s|%s|%s|%s|%s\n' "$label" "$r" "$(( $(date +%s) - start ))" "$session" "$first" >"$Q/result/${label//#/_}.$session"
    printf '%-34s %s  %3ss  %-4s %s\n' "$label" "$r" "$(( $(date +%s) - start ))" "$session" "$first"
  }
  healthy() { # session: both the host and the client have a world loaded
    local st; st=$(DVMP_SESSION=$1 $DVMP status 2>/dev/null)
    [ "$(echo "$st" | grep -c '"worldLoaded": true')" -ge 2 ] && echo "$st" | grep -q '"role": "client"'
  }
  worker() { # session: claims items until the queue is empty
    local session=$1 i
    for i in "${!items[@]}"; do
      mkdir "$Q/claim/$i" 2>/dev/null || continue   # atomic claim
      IFS='|' read -r label dir vars <<<"${items[$i]}"
      run_one "$label" "$dir" "$vars" "$session"
      # A run that left its pair without a loaded world on both games (a crash, a failed
      # rejoin leaving the client at the menu) gets it back.
      healthy "$session" || DVMP_SESSION=$session $DVMP up ${UP_ARGS:-} >/dev/null 2>&1
    done
  }
  echo "== starting $PAIRS pair(s): ${#items[@]} parallel run(s), ${#exclusive[@]} exclusive"
  for p in $(seq 1 "$PAIRS"); do DVMP_SESSION=p$p $DVMP up ${UP_ARGS:-} >"$LOG_DIR/up-p$p.log" 2>&1 & done
  wait
  for p in $(seq 1 "$PAIRS"); do
    grep -q '"ok": true' "$LOG_DIR/up-p$p.log" || { echo "pair p$p failed to start (see $LOG_DIR/up-p$p.log)"; exit 2; }
  done
  for p in $(seq 1 "$PAIRS"); do worker "p$p" & done
  wait
  if [ ${#exclusive[@]} -gt 0 ]; then
    for p in $(seq 2 "$PAIRS"); do DVMP_SESSION=p$p $DVMP down >/dev/null 2>&1; done
    for e in "${exclusive[@]}"; do IFS='|' read -r label dir vars <<<"$e"; run_one "$label" "$dir" "$vars" p1; done
  fi
  echo; echo "== summary"
  cat "$Q"/result/* 2>/dev/null | node -e '
    const rows=require("fs").readFileSync(0,"utf8").trim().split("\n").filter(Boolean).map(l=>l.split("|"));
    const by={};for(const [label,r,secs,session,first] of rows){const n=label.replace(/#\d+$/,"");(by[n]||=[]).push({r,secs:+secs,session,first});}
    let bad=0;for(const n of Object.keys(by).sort()){const g=by[n],p=g.filter(x=>x.r==="PASS").length;if(p<g.length)bad++;
      const avg=Math.round(g.reduce((a,x)=>a+x.secs,0)/g.length),max=Math.max(...g.map(x=>x.secs));
      const f=g.find(x=>x.r==="FAIL");console.log(`${n.padEnd(34)} ${p}/${g.length}  avg ${avg}s max ${max}s${f?"  first FAIL on "+f.session+": "+f.first:""}`);}
    process.exit(bad?1:0);'
  status=$?
  echo "logs: $LOG_DIR"
  exit $status
fi

failed=0
for entry in "${runs[@]}"; do
  IFS='|' read -r name dir vars <<<"$entry"
  [ -n "${ONLY:-}" ] && [[ " $ONLY " != *" $dir "* && " $ONLY " != *" $name "* ]] && continue
  teleport_home
  start=$(date +%s); breaks=$(coupler_breaks); logs=$(game_logs)
  if env $vars bash "$S/$dir/run.sh" >"$LOG_DIR/$name.log" 2>&1; then r=PASS; else r=FAIL; failed=1; note_logs "$logs" >>"$LOG_DIR/$name.log"; fi
  breaks=$(( $(coupler_breaks) - breaks )); flag=""; [ $breaks -gt 0 ] && flag=" [$breaks coupler break(s) logged]"
  printf '%-32s %s  %3ss  %s%s\n' "$name" "$r" "$(( $(date +%s) - start ))" "$( [ $r = FAIL ] && grep -E 'FAIL|rror' "$LOG_DIR/$name.log" | head -1 | cut -c1-90)" "$flag"
done
echo "logs: $LOG_DIR"
exit $failed
