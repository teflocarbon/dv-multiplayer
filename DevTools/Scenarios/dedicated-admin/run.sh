#!/usr/bin/env bash
# A dedicated host run by its players: admin commands and the empty-server pause.
#  1. A player who isn't an admin can't /save.
#  2. Made an admin (Settings.Admins, in memory only), /save saves. /payfees is refused while
#     the wallet can't cover the fees; then the fees screen's request pays them once, cheapest
#     first until the insurance quota is reached, and insurance clears the rest.
#  3. When the last player leaves, the host saves and pauses; when they come back it unpauses.
# Needs a dedicated host (dvmp up --dedicated); on an ordinary host it skips.
# Exit code 0 = pass. Sets the test save's wallet.
set -euo pipefail
cd "$(dirname "$0")/../../.."
D=${DVMP:-DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe}
U="using Multiplayer.Components.Networking;"
field() { node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));const x=Object.values(o.results||{})[0];if(o.compileError||!x||!x.ok){console.error(o.compileError||JSON.stringify(x));process.exit(1)}const v=x.result[process.argv[1]];console.log(typeof v==="object"?JSON.stringify(v):v)' "$1"; }
fail() { echo "FAIL: $*"; exit 1; }
host() { $D eval -t host -m body -u "$U" "$1"; }
# The command goes without its slash: Git Bash would turn "/save" into a Windows path.
chat() { $D eval -t client -m body -u "$U" -a cmd="$1" 'NetworkLifecycle.Instance.Client.SendChat("/" + ctx.Args["cmd"]); return 0;' >/dev/null; sleep 3; }
state='var debts = DV.ServicePenalty.CareerManagerDebtController.Instance; debts.RefreshExistingDebtsState(); var payable = debts.currentNonZeroPricedDebts.Where(d => d.IsPayable).ToList(); var q = debts.feeQuota; double cost = 0; if (!(q.InsuranceUsed && q.QuotaReached)) foreach (double p in payable.Select(d => (double)d.GetTotalPrice()).OrderBy(p => p)) { if (q.InsuranceUsed && cost >= q.LeftToReachQuota) break; cost += p; } return new { saved = (DedicatedHost.LastSave?.ToString("o") ?? "never") + " " + DedicatedHost.LastSaveReason, paused = DV.AppUtil.Instance.IsTimePaused, money = DV.InventorySystem.Inventory.Instance.PlayerMoney, fees = payable.Count, cost, players = NetworkLifecycle.Instance.Server.GameplayPlayerCount };'
money() { host "DV.InventorySystem.Inventory.Instance.SetMoney($1); return 0;" >/dev/null; }

[ "$(host 'return new { d = NetworkLifecycle.Instance.IsDedicatedHost };' | field d)" = "true" ] ||
  { echo "SKIP: needs a dedicated host (dvmp up --dedicated)"; exit 0; }
guid=$(host 'return new { g = NetworkLifecycle.Instance.Server.GameplayPlayers.First().Guid.ToString() };' | field g)
restore() { host 'Multiplayer.Multiplayer.Settings.Admins = ""; return 0;' >/dev/null 2>&1 || true; }
trap restore EXIT

echo "== a player who isn't an admin tries /save"
chat "save"; s1=$(host "$state")
echo "   last save: $(echo "$s1" | field saved)"
echo "$s1" | field saved | grep -q "/save" && fail "a non-admin's /save saved the game"

echo "== made an admin, /save"
host "Multiplayer.Multiplayer.Settings.Admins = \"$guid\"; return 0;" >/dev/null
chat "save"; s2=$(host "$state")
echo "   last save: $(echo "$s2" | field saved)"
echo "$s2" | field saved | grep -q "/save by" || fail "an admin's /save didn't save"

fees=$(echo "$s2" | field fees); cost=$(echo "$s2" | field cost)
echo "== /payfees: $fees payable, \$$cost to pay with insurance"
if [ "$fees" != "0" ]; then
  # Not enough in the wallet: refused, nothing paid. (Nothing to refuse when insurance covers it all.)
  if [ "$cost" != "0" ]; then
    low=$(node -e 'console.log((+process.argv[1] / 2).toFixed(2))' "$cost"); money "$low"
    chat "payfees"; s3=$(host "$state")
    echo "   with \$$low: fees left $(echo "$s3" | field fees), wallet \$$(echo "$s3" | field money)"
    [ "$(echo "$s3" | field fees)" = "$fees" ] || fail "fees were paid from a wallet that couldn't cover them"
    node -e 'process.exit(Math.abs(+process.argv[1] - +process.argv[2]) < 0.01 ? 0 : 1)' "$(echo "$s3" | field money)" "$low" || fail "a refused /payfees took money"
  fi
  # Enough, from the fees screen: paid once, and insurance clears the rest.
  rich=$(node -e 'console.log((+process.argv[1] + 1000).toFixed(2))' "$cost"); money "$rich"
  $D eval -t client -m body -u "$U" 'NetworkLifecycle.Instance.Client.SendPayFeesRequest(); return 0;' >/dev/null; sleep 3
  s4=$(host "$state")
  echo "   with \$$rich: fees left $(echo "$s4" | field fees), wallet \$$(echo "$s4" | field money)"
  [ "$(echo "$s4" | field fees)" = "0" ] || fail "fees left after the fees screen paid them"
  node -e 'const [a,b,p]=process.argv.slice(1).map(Number);process.exit(Math.abs((a-b)-p)<0.01?0:1)' "$rich" "$(echo "$s4" | field money)" "$cost" ||
    fail "the wallet didn't go down by the fees exactly once"
fi

echo "== the last player leaves"
before=$(host "$state" | field saved)
client_pid=$($D status | node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8"));console.log(o.agents.find(a=>a.role==="client").pid)')
$D eval -t client 'Application.Quit();' >/dev/null || true
for i in $(seq 1 30); do tasklist //FI "PID eq $client_pid" //NH | grep -q DerailValley || break; sleep 1; done
sleep 4; s5=$(host "$state")
echo "   players $(echo "$s5" | field players), paused $(echo "$s5" | field paused), last save: $(echo "$s5" | field saved)"
[ "$(echo "$s5" | field paused)" = "true" ] || fail "the host didn't pause with nobody online"
echo "$s5" | field saved | grep -q "the last player left" || fail "the host didn't save when the last player left"

echo "== the player comes back"
$D up >/dev/null || fail "the client couldn't rejoin"
s6=$(host "$state")
echo "   players $(echo "$s6" | field players), paused $(echo "$s6" | field paused)"
[ "$(echo "$s6" | field paused)" = "false" ] || fail "the host stayed paused with a player online"
echo PASS
