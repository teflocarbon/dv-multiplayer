#!/usr/bin/env bash
# Runs every scenario against a running pair (dvmp up) and summarises. Exit code 0 = all passed.
#   ONLY="rejoin recall" to run a subset (by scenario or run name). Each run's output is in
#   $LOG_DIR (default a temp dir).
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
  "fps-drop|fps-drop|"
  "rejoin|rejoin|"
  "rejoin-crash|rejoin|CRASH=1"
  "save-reload|save-reload|CYCLES=2"
)
failed=0
for entry in "${runs[@]}"; do
  IFS='|' read -r name dir vars <<<"$entry"
  [ -n "${ONLY:-}" ] && [[ " $ONLY " != *" $dir "* && " $ONLY " != *" $name "* ]] && continue
  # Start every run from the same place: scenarios move the players and leave them there.
  $DVMP eval -t host -m coroutine --timeout 60 'yield return LocalPlayer.TeleportToGround(7194f, 6873f);' >/dev/null 2>&1
  $DVMP eval -t client -m coroutine --timeout 60 'if (Hands.Held != null) yield return Hands.Drop(); yield return LocalPlayer.TeleportToGround(7190f, 6880f);' >/dev/null 2>&1
  start=$(date +%s)
  if env $vars bash "$S/$dir/run.sh" >"$LOG_DIR/$name.log" 2>&1; then r=PASS; else r=FAIL; failed=1; fi
  printf '%-32s %s  %3ss  %s\n' "$name" "$r" "$(( $(date +%s) - start ))" "$( [ $r = FAIL ] && grep -E 'FAIL|rror' "$LOG_DIR/$name.log" | head -1 | cut -c1-90)"
done
echo "logs: $LOG_DIR"
exit $failed
