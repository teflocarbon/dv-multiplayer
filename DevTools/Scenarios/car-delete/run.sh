#!/usr/bin/env bash
# A car deleted twice (#84): the host bulk-deletes a loaded consist the way the yard's cleanup does
# and deletes each car again in the same frame. That threw inside DV's bulk delete, which stopped it
# with cars switched off but never deleted: they stayed in their trainsets unnetworked, and the host
# refused every join. Pass: no such trainset is left, and the bulk delete didn't throw.
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/car-delete
throws() { $D logs -t host -l exception -n 1000 2>/dev/null | grep -c "DeleteTrainCarsThroughPeriodOfTime" || true; }

before=$(throws)
r=$($D eval -t host -m coroutine --timeout 60 -f $P/delete-twice.cs)
stale=$(echo "$r" | node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}console.log(JSON.stringify(x.result.staleSets))')
thrown=$(( $(throws) - before ))
echo "   trainsets left holding deleted cars: $stale; bulk delete threw $thrown time(s)"
[ "$stale" = "[]" ] && [ "$thrown" = 0 ] && echo "PASS" || { echo "FAIL"; exit 1; }
