#!/usr/bin/env bash
# Pooled car reuse (#69): the host deletes cars and spawns cars of the same livery in the same
# frame, so DV's car pool hands the deleted car's object straight to the new car (on the client
# too, when the delete and spawn arrive in one packet batch). The new car used to keep the deleted
# car's doomed NetworkedTrainCar: the host announced it with the old net ID, the client rejected
# that ID as taken, and both ended up with an unnetworked car that blocked every topology snapshot
# on the client from then on.
# Pass: every car is networked on both peers, each new car has a fresh net ID that matches on the
# client, and no empty trainset is left behind.   N=3 (cars in the second round)
# Exit code 0 = pass. Needs a running pair (dvmp up).
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
P=DevTools/Scenarios/car-pool-reuse
N=${N:-3}
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
result() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];console.log(x&&x.ok?x.result:"ERROR "+(o.compileError||JSON.stringify(x)))'; }
cars=""
cleanup() { [ -n "$cars" ] && $D eval -t host -m body -a cars="$cars" 'var ids = ctx.Args["cars"].Split((char)44); foreach (var c in CarSpawner.Instance.AllCars.Where(c => c != null && ids.Contains(c.ID)).ToList()) CarSpawner.Instance.DeleteCar(c); return 0;' >/dev/null 2>&1 || true; }
trap cleanup EXIT
# Every car without a NetworkedTrainCar, on the peer it runs on.
unnetworked() { $D eval -t $1 -m body 'return string.Join(",", CarSpawner.Instance.AllCars.Where(c => c != null && c.GetComponent<Multiplayer.Components.Networking.Train.NetworkedTrainCar>() == null).Select(c => c.ID));' | result; }
empty_sets() { $D eval -t host -m body 'return Trainset.allSets.Count(s => s.cars.Count == 0).ToString();' | result; }
fails=0
before_host=$(unnetworked host); before_client=$(unnetworked client); before_empty=$(empty_sets)
[ -z "$before_host$before_client" ] || echo "   note: unnetworked before the test - host [$before_host] client [$before_client]"

for n in 1 $N; do
  echo "== host deletes $n boxcar(s) and spawns $n in the same frame"
  r=$($D eval -t host -m coroutine --timeout 60 -a n=$n -f $P/respawn.cs)
  new=$(echo "$r" | field cars); cars="$cars${cars:+,}$new"
  h=$(echo "$r" | field ids)
  echo "   on $(echo "$r" | field track): pool reused $(echo "$r" | field reused) of $n objects; old net IDs $(echo "$r" | field oldIds); new $h"
  sleep 2
  c=$($D eval -t client -m body -a cars="$new" 'var ids = ctx.Args["cars"].Split((char)44); return string.Join(",", ids.Select(id => { var c = CarSpawner.Instance.AllCars.FirstOrDefault(x => x != null && x.ID == id); return id + ":" + (c == null ? "missing" : (c.GetComponent<Multiplayer.Components.Networking.Train.NetworkedTrainCar>()?.NetId ?? 0).ToString()); }));' | result)
  echo "   client $c"
  o=$(node -e '
    const [h,c,oldIds]=process.argv.slice(1); const bad=[]; const old=new Set(oldIds.split(","));
    const hm=Object.fromEntries(h.split(",").map(s=>s.split(":"))), cm=Object.fromEntries(c.split(",").map(s=>s.split(":")));
    for (const id in hm) {
      if (hm[id]==="0") bad.push(id+" has no net ID on the host");
      else if (old.has(hm[id])) bad.push(id+" kept the deleted car\x27s net ID "+hm[id]);
      if (cm[id]!==hm[id]) bad.push(id+" is "+(cm[id]==="0"?"unnetworked":cm[id])+" on the client, "+hm[id]+" on the host");
    }
    console.log(bad.length?"FAIL "+bad.join("; "):"ok");' "$h" "$c" "$(echo "$r" | field oldIds)")
  echo "   $o"; case "$o" in FAIL*) fails=$((fails+1));; esac
  cleanup; cars=""   # free the track for the next round
done

after_host=$(unnetworked host); after_client=$(unnetworked client); after_empty=$(empty_sets)
echo "== after: unnetworked host [${after_host}] client [${after_client}]; empty trainsets ${before_empty} -> ${after_empty}"
[ "$after_host" = "$before_host" ] && [ "$after_client" = "$before_client" ] || fails=$((fails+1))
[ "$after_empty" = "$before_empty" ] || { echo "   FAIL an empty trainset was left behind (it makes the host refuse joins)"; fails=$((fails+1)); }
[ $fails -eq 0 ] && echo PASS || { echo "FAIL: $fails checks"; exit 1; }
