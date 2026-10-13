#!/usr/bin/env bash
# Save/reload regression test: items must not multiply, fall from the sky, or drift into
# Lost and Found across repeated host save + reload cycles.
#   SESSION   session (or save) to load, default "DVMP Test Career 2"
#   CYCLES    number of reloads, default 3
# Exit code 0 = pass.
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe
P=DevTools/Scenarios/save-reload
U="using Multiplayer.Components.Networking.World; using Multiplayer.Networking.Data.Items;"
SESSION=${SESSION:-"DVMP Test Career 2"}
CYCLES=${CYCLES:-3}
count() { $D eval -t host -m body -u "$U" -f $P/count.cs | node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}console.log(JSON.stringify(x.result))'; }

first=""; fail=0
for cycle in $(seq 1 "$CYCLES"); do
  $D down >/dev/null
  $D up --save "$SESSION" >/dev/null
  $D eval -t host -m coroutine 'yield return new WaitForSeconds(8);' >/dev/null
  c=$(count); echo "load $cycle: $c"
  [ -z "$first" ] && first=$c
  node -e 'const a=JSON.parse(process.argv[1]),b=JSON.parse(process.argv[2]);const bad=[];if(b.items>a.items)bad.push(`items ${a.items} -> ${b.items}`);if(b.jobOverviews>a.jobOverviews)bad.push(`job overviews ${a.jobOverviews} -> ${b.jobOverviews}`);for(const k of ["activeInSky","lostAndFound","hostInventoryMisrecorded"])if(b[k]>0)bad.push(`${k}=${b[k]}`);if(bad.length){console.log("   FAIL:",bad.join(", "));process.exit(1)}' "$first" "$c" || fail=1
  $D eval -t host -m coroutine 'SaveGameManager.Instance.Save(DV.Common.SaveType.Manual); yield return new WaitForSeconds(3);' >/dev/null
done
[ $fail -eq 0 ] && echo PASS || { echo FAIL; exit 1; }
