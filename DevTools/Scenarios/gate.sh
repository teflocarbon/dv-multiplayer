#!/usr/bin/env bash
# Runs every scenario against a running pair (dvmp up) and summarises. Exit code 0 = all passed.
#   ONLY="rejoin recall" to run a subset (by scenario or run name). Each run's output is in
#   $LOG_DIR (default a temp dir).
#   RUNS: the runs to queue instead of the gate's list below, a file or lines separated by
#   newlines or ";". A line is a run name from the list, or a scenario and its variables, with
#   an optional pair kind and label:
#     derail                              the gate's run of that name (its scenario and variables)
#     jobs ACCEPT=client HANDIN=host      a scenario with exactly these variables
#     host-actions @dedicated             only on a dedicated host (@player, @dedicated, @any)
#     jobs-reverse: jobs ACCEPT=client    "label:" names the run (default: the scenario, plus
#                                         [variables] and @kind when given)
#     *                                   every run of the gate's list
#   Values may be quoted (LOCOS="LocoS282A LocoDE6"); "#" starts a comment line.
#   PAIRS: run on several host+client pairs at once, all taking runs from one queue. A count
#   (PAIRS=3: player-host pairs p1..p3) or kinds with counts: PAIRS="p:2 d:1" is two player-host
#   pairs (p1, p2) and one dedicated-host pair (d1, `dvmp up --dedicated`). A run marked @kind
#   only goes to that kind of pair; other runs prefer player hosts, or take any pair when there
#   are none. REPEAT=N: each run N times (ONLY=host-stall REPEAT=6 PAIRS=3 hunts an
#   intermittent bug). EXCLUSIVE= overrides which scenarios run alone at the end.
#   Runs start longest first, from their last passing durations in $DURATIONS (default
#   %LOCALAPPDATA%\dvmp\durations, a file per run); runs with none yet start first.
#   UP_ARGS: extra `dvmp up` arguments for every pair it starts. UP_ARGS=--dedicated still
#   works: it makes a plain PAIRS=N count dedicated pairs (docs/design/dedicated-host.md).
#   MONEY: the host's wallet is topped up to this before every run (default 100000; 0 leaves it).
#   A pair runs nothing until its host and client both answer with a world loaded (READY_WAIT s,
#   default 150, then one `dvmp up` and as long again). A run that failed because dvmp couldn't
#   reach its games is an ERROR, not a FAIL: it's counted apart, and retried once with PAIRS.
[ -n "${RUNS:-}" ] && [ -f "$RUNS" ] && RUNS=$(cat "$RUNS")   # read before the cd: a path relative to the caller
cd "$(dirname "$0")/../.."
export DVMP=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
S=DevTools/Scenarios
LOG_DIR=${LOG_DIR:-$(mktemp -d)}; mkdir -p "$LOG_DIR"
runs=( # name | scenario | variables | pair kind it prefers (default player)
  "train-items|train-items|"
  "train-items-host-throw-moving|train-items|WHO=host MODE=throw WHEN=moving|player"
  "host-throw-settles|host-throw-settles||player"
  "server-throw|server-throw||player"
  "server-throw-client|server-throw|ACTOR=client"
  "item-release-regrab|item-release|"
  "item-release-regrab-late|item-release|MODE=regrab-late"
  "item-release-throw-flatcar|item-release|MODE=throw-flatcar"
  "item-release-object-state|item-release|MODE=object-state"
  "item-release-interact|item-release|MODE=interact"
  "lost-and-found|lost-and-found|"
  "lost-and-found-host|lost-and-found|WHO=host|player"
  "recall|recall|"
  "recall-taken|recall|TAKEN=1|player"
  "containers|containers|"
  "containers-briefcase-flashlight|containers|CONTAINER=ItemContainerBriefcase PREFAB=Flashlight"
  "item-id-roundtrip|item-id-roundtrip|"
  "ledger-save|ledger-save|"
  "fixture-no-stash|fixture-no-stash|"
  "containers-host|containers|WHO=host|player"
  "client-fall-rescue|client-fall-rescue|"
  "manual-uncouple|manual-uncouple|N=3"
  "manual-recouple-lag|manual-uncouple|MODE=recouple N=3 LAG=150"
  "host-chain-screw|host-chain-screw||player"
  "host-coupling-tools|host-coupling-tools||player"
  "uncouple-moving|uncouple-moving|"
  "uncouple-moving-loco-lag|uncouple-moving|LAST=LocoDE2 LAG=150 SPEED=10"
  "couple-impact|couple-impact|WAGON_BRAKE=0 LAG=150 SPEEDS=3,5"
  "couple-impact-chain|couple-impact|CHAIN=1 WAGON_BRAKE=0 SPEEDS=2,4"
  "coupler-desync|coupler-desync|LAG=150"
  "derail|pileup|SPEED=25 K=2 N=4 MIN_DERAILED=1 LAG=150"
  "car-pool-reuse|car-pool-reuse|"
  "car-delete|car-delete|"
  "radio-tools|radio-tools|"
  "radio-tools-host|radio-tools-host||player"
  "cab-controls|cab-controls|"
  "cab-controls-authority|cab-controls-authority||player"
  "cab-controls-no-grab|cab-controls-no-grab|"
  "cab-controls-reload|cab-reload|"
  "settle-window|settle-window|"
  "compressor-signal|compressor-signal|"
  "stoker-coal|stoker-coal|"
  "fps-drop|fps-drop|"
  "host-stall|host-stall|SPEED=10 LAG=150"
  "client-derail|client-derail|"
  "derail-drag|derail-drag|"
  "rerail|rerail|"
  "rerail-host|rerail|WHO=host"
  "rerail-host-radio|rerail|WHO=host-radio"
  "far-derail|far-derail|"
  "far-derail-embankment|far-derail-embankment|"
  "dedicated-admin|dedicated-admin||dedicated"
  "explosion|explosion|"
  "car-fire|car-fire|"
  "hazmat|hazmat|"
  "rejoin|rejoin|"
  "rejoin-crash|rejoin|CRASH=1"
  "rejoin-autocouple|rejoin-autocouple|"
  "save-reload|save-reload|CYCLES=2"
  "inventory-persist|inventory-persist|"
  "host-player-persist|host-player-persist||player"
  "gadget-save|gadget-save|"
  "gadget-join|gadget-join|"
  "gadget-settings|gadget-settings|"
  "gadget-settings-lag|gadget-settings|LAG=250"
  "gadget-buttons|gadget-buttons|"
  "gadget-buttons-lag|gadget-buttons|LAG=250"
  "gadget-knobs|gadget-knobs|"
  "gadget-knobs-lag|gadget-knobs|LAG=250"
  "gadget-ats|gadget-ats|"
  "gadget-antislip|gadget-antislip|"
  "borrowed-disconnect|borrowed-disconnect||player"
  "host-actions|host-actions|"
  "host-actions-host|host-actions|WHO=host"
  "server-world-changes|server-world-changes|"
  "pit-stop|pit-stop|"
  "pit-stop-host|pit-stop|WHO=host"
  "pit-stop-yank|pit-stop|MODE=yank"
  "host-register-empty-basket|host-register-empty-basket|"
  "host-session|host-session||player"
  "local-session|local-session|"
  "host-chat|host-chat|"
  "mpapi-roundtrip|mpapi-roundtrip|"
  "shop|shop|"
  "jobs|jobs|ACCEPT=host HANDIN=client"
  "overview-pickup|overview-pickup|"
  "job-reroll|job-reroll|"
  "job-reroll-host|job-reroll|WHO=host|player"
  "booklet-summon|booklet-summon|"
  "booklet-summon-host|booklet-summon|WHO=host|player"
  "station-loco-spawn|station-loco-spawn|"
  "station-loco-spawn-host|station-loco-spawn|WHO=host|player"
  "host-job-train-coupled|host-job-train-coupled|"
  "remote-held-item|remote-held-item|"
  "owned-items|owned-items|"
  "sandbox-cargo|sandbox-cargo|"
  "player-soak|player-soak|DURATION=120"
)
teleport_home() { # puts both players back where every run starts (scenarios move them)
  $DVMP eval -t host -m coroutine --timeout 60 'yield return LocalPlayer.TeleportToGround(7194f, 6873f);' >/dev/null 2>&1
  $DVMP eval -t client -m coroutine --timeout 60 'if (Hands.Held != null) yield return Hands.Drop(); yield return LocalPlayer.TeleportToGround(7190f, 6880f);' >/dev/null 2>&1
  # Scenarios hand players mugs and don't always take them back: after a few gates the client's
  # inventory was full (22 mugs) and every "put it in the inventory" step failed, and mugs left in
  # the host's inventory failed host-player-persist's duplicate check. Drop them on both games (a
  # synced drop) and destroy every mug on the host.
  $DVMP eval -t all -m coroutine --timeout 60 'var inv = DV.InventorySystem.Inventory.Instance; foreach (var go in inv.GetItemsArray(false).Where(g => g != null && g.name.StartsWith("Mug")).ToList()) { inv.DropItemFromHandsOrInventory(go); yield return new WaitForSecondsRealtime(0.1f); } yield return new WaitForSecondsRealtime(1f);' >/dev/null 2>&1
  $DVMP eval -t host -m coroutine --timeout 30 'foreach (var m in UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Items.NetworkedItem>().Where(i => i != null && i.name.StartsWith("Mug")).ToList()) UnityEngine.Object.Destroy(m.gameObject); yield return new WaitForSecondsRealtime(1f);' >/dev/null 2>&1
  # Rerails, car deletes and fees spend the shared wallet, and the save-writing runs keep what's
  # left: one batch left $1000 and the next rerail failed "not enough money". Top it up on the host
  # (AddMoney syncs to clients) to $MONEY; MONEY=0 leaves it.
  [ "${MONEY:-100000}" -gt 0 ] && $DVMP eval -t host -m coroutine --timeout 30 "var inv = DV.InventorySystem.Inventory.Instance; if (inv != null && inv.PlayerMoney < ${MONEY:-100000}) inv.AddMoney(${MONEY:-100000} - inv.PlayerMoney); yield break;" >/dev/null 2>&1
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
# A run that failed because dvmp couldn't reach its pair (games not answering yet, or gone) says
# nothing about the code: it's an ERROR, reported apart from FAILs (and retried once with PAIRS).
harness_error() { grep -qE "no agent matches|no game is running|no (host|client) running|No connection could be made|actively refused|Connection refused" "$1"; }

# Scenarios are bash (DevTools/Scenarios/<name>/run.sh) or C# (DevTools/DvmpScenarios, run by
# `dvmp scenario run`; docs/design/scenarios.md) while they're ported. Same variables either way: a
# C# scenario gets them as --arg NAME=value, and its exit code 3 is a harness ERROR.
CS=DevTools/DvmpScenarios
is_cs() { grep -qs "\[Scenario(\"$1\"" "$CS"/Scenarios/*.cs; }
run_scenario() { # dir vars: one run of the scenario, its output on stdout
  local dir=$1 a=() v
  if [ -f "$S/$dir/run.sh" ]; then eval "env $2 bash \"$S/$dir/run.sh\""; return; fi
  eval "set -- $2"
  for v in "$@"; do a+=(--arg "$v"); done
  $DVMP scenario run "$dir" "${a[@]}" --log-dir "$LOG_DIR"
}

# ---- the queue: the gate's runs, or the lines of RUNS. Each entry is
# "label|scenario|variables|kind|strict": kind is the pair it needs (player, dedicated or any),
# strict that the line asked for it (@kind) rather than the gate's list preferring it.
queue=()
gate_run() { # name: "scenario|variables|kind" of the gate's run of that name
  local e n d v k
  for e in "${runs[@]}"; do IFS='|' read -r n d v k <<<"$e"; [ "$n" = "$1" ] && { echo "$d|$v|$k"; return 0; }; done
  return 1
}
gate_name() { # scenario variables: the name of the gate's run with exactly these
  local e n d v k
  for e in "${runs[@]}"; do IFS='|' read -r n d v k <<<"$e"; [ "$d" = "$1" ] && [ "$v" = "$2" ] && { echo "$n"; return 0; }; done
  return 1
}
add_line() { # one line of RUNS
  local line=${1%$'\r'} toks=() label="" kind="" strict=0 name t q vars="" shown="" e n d v k ok
  [[ $line =~ ^[[:space:]]*(#.*)?$ ]] && return 0
  set -f; eval "toks=($line)" 2>/dev/null; ok=$?; set +f
  [ $ok -eq 0 ] && [ ${#toks[@]} -gt 0 ] || { echo "RUNS: can't read the line: $line" >&2; return 1; }
  if [[ ${toks[0]} == *: ]]; then label=${toks[0]%:}; toks=("${toks[@]:1}"); fi
  name=${toks[0]}
  for t in "${toks[@]:1}"; do
    case $t in
      @p|@player) kind=player strict=1 ;;
      @d|@dedicated) kind=dedicated strict=1 ;;
      @any) kind=any strict=1 ;;
      [A-Za-z_]*=*)
        if [[ $t =~ ^[A-Za-z0-9_=,.:/+@-]*$ ]]; then q=$t; else q=$(printf '%q' "$t"); fi
        vars+="${vars:+ }$q"; shown+="${shown:+,}$t" ;;
      *) echo "RUNS: '$t' is neither VAR=value nor @kind (in: $line)" >&2; return 1 ;;
    esac
  done
  if [ "$name" = "*" ]; then
    [ -z "$vars" ] || { echo "RUNS: '*' takes no variables" >&2; return 1; }
    for e in "${runs[@]}"; do
      IFS='|' read -r n d v k <<<"$e"
      [ $strict = 1 ] && { k=$kind; n+="@$kind"; }
      queue+=("$n|$d|$v|${k:-player}|$strict")
    done
    return 0
  fi
  if [ -z "$vars" ] && e=$(gate_run "$name"); then        # a run of the gate's list
    IFS='|' read -r d v k <<<"$e"; shown=$name
  elif [ -f "$S/$name/run.sh" ] || is_cs "$name"; then     # a scenario with these variables
    d=$name v=$vars k=""
    shown=$(gate_name "$d" "$v" || echo "$name${vars:+[$shown]}")
  elif e=$(gate_run "$name"); then                         # a run of the gate's list, more variables
    IFS='|' read -r d v k <<<"$e"; v+="${v:+ }$vars"; shown="$name[$shown]"
  else
    echo "RUNS: no scenario or gate run named '$name'" >&2; return 1
  fi
  [ $strict = 1 ] && { k=$kind; [ -z "$label" ] && shown+="@$kind"; }
  # Labels name log and result files: keep them to characters every file system takes.
  queue+=("$(echo "${label:-$shown}" | sed 's/[^][A-Za-z0-9_.,=@+-]/_/g')|$d|$v|${k:-player}|$strict")
}
if [ -n "${RUNS:-}" ]; then
  # Comment lines go first: a ";" inside one isn't a separator.
  while IFS= read -r line; do
    [[ $line =~ ^[[:space:]]*# ]] && continue
    while IFS= read -r part; do add_line "$part" || exit 2; done <<<"${line//;/$'\n'}"
  done <<<"$RUNS"
else
  for e in "${runs[@]}"; do IFS='|' read -r n d v k <<<"$e"; queue+=("$n|$d|$v|${k:-player}|0"); done
fi
if [ -n "${ONLY:-}" ]; then # by label, run or scenario name
  kept=()
  for q in "${queue[@]}"; do
    IFS='|' read -r label d _ <<<"$q"
    [[ " $ONLY " == *" $label "* || " $ONLY " == *" ${label%%[@[]*} "* || " $ONLY " == *" $d "* ]] && kept+=("$q")
  done
  queue=("${kept[@]}")
fi
parallel=; [ -n "${PAIRS:-}" ] || [ -n "${REPEAT:-}" ] && parallel=1
REPEAT=${REPEAT:-1}
# Each run REPEAT times; a label that appears more than once (repeats, or a line given twice)
# is numbered: name#1, name#2...
declare -A total=() seen=()
for q in "${queue[@]}"; do l=${q%%|*}; total[$l]=$(( ${total[$l]:-0} + REPEAT )); done
all=()
for q in "${queue[@]}"; do
  for _ in $(seq 1 "$REPEAT"); do
    l=${q%%|*}
    if [ "${total[$l]}" -gt 1 ]; then seen[$l]=$(( ${seen[$l]:-0} + 1 )); l="$l#${seen[$l]}"; fi
    all+=("$l|${q#*|}")
  done
done
[ ${#all[@]} -gt 0 ] || { echo "nothing to run (check ONLY and RUNS)"; exit 2; }
# The C# scenarios are built once, before the first run: one that doesn't compile against this
# Multiplayer.dll stops the gate here instead of failing halfway through. (Build Multiplayer first.)
for it in "${all[@]}"; do
  IFS='|' read -r _ dir _ <<<"$it"
  [ -f "$S/$dir/run.sh" ] && continue
  echo "== building the C# scenarios"
  dotnet build "$CS/DvmpScenarios.csproj" -c Debug -nologo -v q >"$LOG_DIR/scenarios-build.log" 2>&1 \
    || { grep -E " error " "$LOG_DIR/scenarios-build.log" | sort -u | head -20; echo "the C# scenarios don't build (see $LOG_DIR/scenarios-build.log)"; exit 2; }
  $DVMP scenario check || { echo "the C# scenarios don't pass their checks"; exit 2; }
  break
done

# ---- parallel mode: PAIRS host+client pairs (dvmp sessions p1.., d1..) take runs from one
# queue, REPEAT=N runs each one N times. See docs/design/parallel-test-sessions.md.
if [ -n "$parallel" ]; then
  # These measure frame timing, so they run alone after the parallel phase, every other pair down.
  # Runs that write saves run in parallel: each session's games keep their own user data (dvmp
  # seeds it from your profile; `dvmp up --reseed` copies it again).
  EXCLUSIVE=${EXCLUSIVE:-"fps-drop host-stall player-soak"}
  Q="$LOG_DIR/.queue"; mkdir -p "$Q/claim" "$Q/result"

  # Pairs: "session|kind". A plain count is player hosts (dedicated with UP_ARGS=--dedicated).
  plain=player
  if [[ " ${UP_ARGS:-} " == *" --dedicated "* ]]; then plain=dedicated; UP_ARGS=$(echo " $UP_ARGS " | sed 's/ --dedicated / /g'); fi
  pairs=(); declare -A made=()
  spec=${PAIRS:-1}
  for part in ${spec//,/ }; do
    case $part in
      p:*|player:*) kind=player n=${part#*:} ;;
      d:*|dedicated:*) kind=dedicated n=${part#*:} ;;
      *) kind=$plain n=$part ;;
    esac
    [[ $n =~ ^[0-9]+$ ]] || { echo "PAIRS: can't read '$part' (a count, or p:N and d:N)"; exit 2; }
    for _ in $(seq 1 "$n"); do made[$kind]=$(( ${made[$kind]:-0} + 1 )); pairs+=("${kind:0:1}${made[$kind]}|$kind"); done
  done
  up_args() { echo "${UP_ARGS:-} $([ "$1" = dedicated ] && echo --dedicated)"; }
  fits() { [ "$1" = any ] || [ "$1" = "$2" ]; } # run kind, pair kind
  first_pair() { local p; for p in "${pairs[@]}"; do fits "$1" "${p#*|}" && { echo "${p%%|*}"; return; }; done; }
  kind_of() { local p; for p in "${pairs[@]}"; do [ "${p%%|*}" = "$1" ] && { echo "${p#*|}"; return; }; done; }

  # A run that asked for a kind of pair (@kind) must get one; the gate's preferences give way.
  items=(); exclusive=(); missing=()
  for it in "${all[@]}"; do
    IFS='|' read -r label dir vars kind strict <<<"$it"
    if [ "$kind" != any ] && [ -z "${made[$kind]:-}" ]; then
      [ "$strict" = 1 ] && { missing+=("$label"); continue; }
      kind=any
    fi
    if [[ " $EXCLUSIVE " == *" $dir "* || " $EXCLUSIVE " == *" ${label%%#*} "* ]]; then exclusive+=("$label|$dir|$vars|$kind"); else items+=("$label|$dir|$vars|$kind"); fi
  done
  [ ${#missing[@]} -eq 0 ] || { echo "no pair of the kind these runs need: ${missing[*]} (PAIRS=\"$PAIRS\"; e.g. PAIRS=\"p:2 d:1\")"; exit 2; }
  # Longest first, so a long run never starts last while the other pairs sit idle. Each run's
  # last 5 passing durations are kept in $DURATIONS/<run name>; runs with none yet go first.
  if [ -z "${DURATIONS:-}" ]; then
    if [ -n "${LOCALAPPDATA:-}" ] && command -v cygpath >/dev/null; then DURATIONS=$(cygpath -u "$LOCALAPPDATA")/dvmp/durations
    else DURATIONS=${LOCALAPPDATA:-$HOME/.cache}/dvmp/durations; fi
  fi
  estimate() { [ -s "$DURATIONS/${1%%#*}" ] && awk '{ s += $1; n++ } END { if (n) printf "%d", s / n }' "$DURATIONS/${1%%#*}"; }
  longest_first() { # entries on stdin, the same out in the order to run them
    local e i=0
    while IFS= read -r e; do printf '%s\t%s\t%s\n' "$(estimate "${e%%|*}" || echo 999999)" $((i++)) "$e"; done |
      sort -t$'\t' -k1,1nr -k2,2n | cut -f3-
  }
  [ ${#items[@]} -gt 0 ] && mapfile -t items < <(printf '%s\n' "${items[@]}" | longest_first)
  [ ${#exclusive[@]} -gt 0 ] && mapfile -t exclusive < <(printf '%s\n' "${exclusive[@]}" | longest_first)
  unknown=0; for e in "${items[@]}" "${exclusive[@]}"; do estimate "${e%%|*}" >/dev/null || unknown=$((unknown + 1)); done
  # The pairs that run the exclusive runs; every other pair goes down once the queue has
  # nothing left for it, so the pairs still busy get its CPU and memory.
  keep=" "
  for e in "${exclusive[@]}"; do IFS='|' read -r _ _ _ kind <<<"$e"; keep+="$(first_pair "$kind") "; done

  run_one() { # label dir vars session; sets r to PASS, FAIL or ERROR
    local label=$1 dir=$2 vars=$3 session=$4 start first breaks logs log="$LOG_DIR/$1.$4.log"
    export DVMP_SESSION=$session
    [ -f "$S/$dir/run.sh" ] && teleport_home   # a C# scenario resets in process (ScenarioRun.ResetAsync)
    start=$(date +%s); breaks=$(coupler_breaks); logs=$(game_logs)
    if run_scenario "$dir" "$vars" >"$log" 2>&1; then r=PASS; else [ $? = 3 ] && r=ERROR || r=FAIL; note_logs "$logs" >>"$log"; fi
    [ $r = FAIL ] && harness_error "$log" && r=ERROR
    first=$( [ $r != PASS ] && grep -E 'FAIL|rror|no agent|no game' "$log" | head -1 | cut -c1-90)
    breaks=$(( $(coupler_breaks) - breaks )); [ $breaks -gt 0 ] && first="$first [$breaks coupler break(s) logged]"
    first+=${retry:-}
    result "$label" $r "$(( $(date +%s) - start ))" "$session" "$first"
  }
  result() { # label PASS|FAIL|ERROR secs session first-line
    printf '%s|%s|%s|%s|%s\n' "$@" >"$Q/result/${1//#/_}"
    printf '%-34s %-5s %3ss  %-4s %s\n' "$@"
  }
  healthy() { # session: a host and a client answering, both with a world loaded
    local st; st=$(DVMP_SESSION=$1 $DVMP status 2>/dev/null)
    [ "$(echo "$st" | grep -c '"worldLoaded": true')" -ge 2 ] && echo "$st" | grep -q '"role": "host"' && echo "$st" | grep -q '"role": "client"'
  }
  ready() { # session kind: waits for the pair to be healthy, with one `dvmp up` after READY_WAIT s
    local s=$1 k=$2 try until
    for try in 1 2; do
      until=$(( $(date +%s) + ${READY_WAIT:-150} ))
      while [ "$(date +%s)" -lt $until ]; do healthy "$s" && return 0; sleep 5; done
      [ $try = 1 ] && DVMP_SESSION=$s $DVMP up $(up_args "$k") >/dev/null 2>&1
    done
    return 1
  }
  run_ready() { # label dir vars session kind: runs on a ready pair, once more after a harness error
    if ! ready "$4" "$5"; then r=ERROR; result "$1" ERROR 0 "$4" "pair $4 never became ready"; return; fi
    run_one "$@"
    [ $r = ERROR ] || return
    mv "$LOG_DIR/$1.$4.log" "$LOG_DIR/$1.$4.error.log"
    ready "$4" "$5" || { result "$1" ERROR 0 "$4" "pair $4 not ready again after a harness error"; return; }
    retry=" [retried after a harness error]" run_one "$@"
  }
  worker() { # session kind: claims the runs that fit its pair until none are left
    local session=$1 kind=$2 i label dir vars need
    # `up` can return before both games answer status: claim nothing until the pair is ready, and
    # leave the queue to the other pairs if it never is.
    ready "$session" "$kind" || { echo "pair $session never became ready: it runs nothing"; return; }
    for i in "${!items[@]}"; do
      IFS='|' read -r label dir vars need <<<"${items[$i]}"
      fits "$need" "$kind" || continue
      mkdir "$Q/claim/$i" 2>/dev/null || continue   # atomic claim
      # Checked before every run: one that left its pair without a loaded world on both games (a
      # crash, a failed rejoin leaving the client at the menu) gets it back.
      run_ready "$label" "$dir" "$vars" "$session" "$kind"
    done
    [ ${#exclusive[@]} -gt 0 ] && [[ $keep != *" $session "* ]] && DVMP_SESSION=$session $DVMP down >/dev/null 2>&1
  }
  echo "== starting ${#pairs[@]} pair(s) (${pairs[*]%%|*}): ${#items[@]} parallel run(s), ${#exclusive[@]} exclusive, longest first ($unknown with no recorded duration)"
  for p in "${pairs[@]}"; do s=${p%%|*}; DVMP_SESSION=$s $DVMP up $(up_args "${p#*|}") >"$LOG_DIR/up-$s.log" 2>&1 & done
  wait
  for p in "${pairs[@]}"; do
    s=${p%%|*}; grep -q '"ok": true' "$LOG_DIR/up-$s.log" || { echo "pair $s failed to start (see $LOG_DIR/up-$s.log)"; exit 2; }
  done
  for p in "${pairs[@]}"; do worker "${p%%|*}" "${p#*|}" & done
  wait
  for i in "${!items[@]}"; do # runs no ready pair took
    [ -d "$Q/claim/$i" ] || result "${items[$i]%%|*}" ERROR 0 - "not run: no ready pair of its kind"
  done
  for e in "${exclusive[@]}"; do # each alone: every other pair down, its own back up if needed
    IFS='|' read -r label dir vars kind <<<"$e"
    s=$(first_pair "$kind")
    for p in "${pairs[@]}"; do [ "${p%%|*}" = "$s" ] || DVMP_SESSION=${p%%|*} $DVMP down >/dev/null 2>&1; done
    run_ready "$label" "$dir" "$vars" "$s" "$(kind_of "$s")"
  done
  mkdir -p "$DURATIONS"
  for f in "$Q"/result/*; do
    [ -f "$f" ] || continue
    IFS='|' read -r label r secs _ <"$f"; [ "$r" = PASS ] || continue
    d="$DURATIONS/${label%%#*}"; { [ -f "$d" ] && tail -n 4 "$d"; echo "$secs"; } >"$d.tmp" && mv "$d.tmp" "$d"
  done
  echo; echo "== summary"
  cat "$Q"/result/* 2>/dev/null | node -e '
    const rows=require("fs").readFileSync(0,"utf8").trim().split("\n").filter(Boolean).map(l=>l.split("|"));
    const by={};for(const [label,r,secs,session,first] of rows){const n=label.replace(/#\d+$/,"");(by[n]||=[]).push({r,secs:+secs,session,first});}
    let fails=0,errors=0,passed=0;for(const n of Object.keys(by).sort()){const g=by[n],p=g.filter(x=>x.r==="PASS").length;passed+=p;
      const f=g.find(x=>x.r==="FAIL"),e=g.find(x=>x.r==="ERROR"),ne=g.filter(x=>x.r==="ERROR").length;if(f)fails++;else if(e)errors++;
      const rt=g.filter(x=>/retried after a harness error/.test(x.first)).length,ran=g.filter(x=>x.r!=="ERROR"),avg=ran.length?Math.round(ran.reduce((a,x)=>a+x.secs,0)/ran.length)+"s":"-",max=ran.length?Math.max(...ran.map(x=>x.secs))+"s":"-";
      console.log(`${n.padEnd(34)} ${p}/${g.length}${ne?` (${ne} ERROR)`:""}${rt?` (${rt} retried)`:""}  avg ${avg} max ${max}${f?"  first FAIL on "+f.session+": "+f.first:e?"  ERROR on "+e.session+": "+e.first:""}`);}
    console.log(`\n${passed} run(s) passed; ${fails} name(s) with a FAIL; ${errors} more with only harness ERRORs (dvmp couldn\x27t reach the pair: not a test result)`);
    process.exit(fails||errors?1:0);'
  status=$?
  echo "logs: $LOG_DIR"
  exit $status
fi

failed=0
for it in "${all[@]}"; do
  IFS='|' read -r name dir vars _ <<<"$it"
  [ -f "$S/$dir/run.sh" ] && teleport_home   # a C# scenario resets in process (ScenarioRun.ResetAsync)
  start=$(date +%s); breaks=$(coupler_breaks); logs=$(game_logs)
  if run_scenario "$dir" "$vars" >"$LOG_DIR/$name.log" 2>&1; then r=PASS; else [ $? = 3 ] && r=ERROR || r=FAIL; failed=1; note_logs "$logs" >>"$LOG_DIR/$name.log"; harness_error "$LOG_DIR/$name.log" && r=ERROR; fi
  breaks=$(( $(coupler_breaks) - breaks )); flag=""; [ $breaks -gt 0 ] && flag=" [$breaks coupler break(s) logged]"
  printf '%-32s %-5s %3ss  %s%s\n' "$name" "$r" "$(( $(date +%s) - start ))" "$( [ $r != PASS ] && grep -E 'FAIL|rror|no agent|no game' "$LOG_DIR/$name.log" | head -1 | cut -c1-90)" "$flag"
done
echo "logs: $LOG_DIR"
exit $failed
