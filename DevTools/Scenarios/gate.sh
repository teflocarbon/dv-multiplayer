#!/usr/bin/env bash
# Runs every scenario against a running pair (dvmp up) and summarises. Exit code 0 = all passed.
#   ONLY="rejoin recall" to run a subset.  Each run's output is in $LOG_DIR (default a temp dir).
cd "$(dirname "$0")/../.."
export DVMP=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
S=DevTools/Scenarios
LOG_DIR=${LOG_DIR:-$(mktemp -d)}; mkdir -p "$LOG_DIR"
runs=(
  "train-items|"
  "train-items-host-throw-moving|WHO=host MODE=throw WHEN=moving"
  "lost-and-found|"
  "recall|"
  "recall-taken|TAKEN=1"
  "containers|"
  "containers-briefcase-flashlight|CONTAINER=ItemContainerBriefcase PREFAB=Flashlight"
  "fps-drop|"
  "rejoin|"
  "rejoin-crash|CRASH=1"
  "save-reload|CYCLES=2"
)
failed=0
for entry in "${runs[@]}"; do
  name=${entry%%|*}; vars=${entry#*|}; dir=${name%%-host*}; dir=${dir%-taken}; dir=${dir%-crash}; dir=${dir%-briefcase-flashlight}
  [ -n "${ONLY:-}" ] && [[ " $ONLY " != *" $dir "* && " $ONLY " != *" $name "* ]] && continue
  start=$(date +%s)
  if env $vars bash "$S/$dir/run.sh" >"$LOG_DIR/$name.log" 2>&1; then r=PASS; else r=FAIL; failed=1; fi
  printf '%-32s %s  %3ss  %s\n' "$name" "$r" "$(( $(date +%s) - start ))" "$( [ $r = FAIL ] && grep -E 'FAIL|rror' "$LOG_DIR/$name.log" | head -1 | cut -c1-90)"
done
echo "logs: $LOG_DIR"
exit $failed
