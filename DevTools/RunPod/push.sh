#!/bin/bash
# Sends a test pod what it needs from this PC, over the pod's direct SSH port: the mod and dev agent
# as built into the test install, Unity Mod Manager's loader, dvmp (its Windows build, which runs
# under the pod's Wine) with its probes, the scenarios, and the DV user data dvmp seeds sessions
# from. Run it after building, and again after every rebuild. See README.md.
#   bash DevTools/RunPod/push.sh HOST PORT
# HOST and PORT: the pod's public IP and the port its 22/tcp maps to. Env overrides:
#   DVMP_TEST_INSTALL  the test install the mod is built into (%LOCALAPPDATA%\dvmp\test-install)
#   DVMP_POD_DVMP      dvmp published for win-x64 (%LOCALAPPDATA%\dvmp\pod-dvmp)
#   DVMP_DV_PROFILE    DV's user data to seed from (DV's LocalLow folder)
#   SSH_KEY            the key the RunPod account has (~/.ssh/runpod_dvmp)
set -euo pipefail
[ $# -eq 2 ] || { sed -n '2,13p' "$0"; exit 2; }
host=$1 port=$2
repo=$(cd "$(dirname "$0")/../.." && pwd)
install=$(cygpath -u "${DVMP_TEST_INSTALL:-$LOCALAPPDATA\\dvmp\\test-install}")
published=$(cygpath -u "${DVMP_POD_DVMP:-$LOCALAPPDATA\\dvmp\\pod-dvmp}")
profile=$(cygpath -u "${DVMP_DV_PROFILE:-$USERPROFILE\\AppData\\LocalLow\\Altfuture\\Derail Valley}")
key=${SSH_KEY:-$HOME/.ssh/runpod_dvmp}

pod() { ssh -i "$key" -p "$port" -o BatchMode=yes -o StrictHostKeyChecking=accept-new "dvmp@$host" "$@"; }
# send FROM TO NAME...: copies FROM/NAME... into ~/TO on the pod (-h: the test install's big
# folders are junctions to the real game, whose UMM Log.txt a running game may be writing).
send() {
    local from=$1 to=$2; shift 2
    echo "  $to: $*"
    tar -C "$from" --exclude=Log.txt -chf - "$@" | pod "mkdir -p '$to' && tar -xf - -C '$to'"
}

for need in "$install/Mods/Multiplayer" "$install/Mods/MultiplayerDevTools" "$published/dvmp.exe" \
    "$repo/DevTools/DvmpScenarios/bin/Debug/net48/DvmpScenarios.exe" "$profile/Users"; do
    [ -e "$need" ] || { echo "missing $need (see README.md, 'Each build')" >&2; exit 1; }
done
pod 'test -f dvmp/ready' || { echo "the pod isn't ready yet (its boot script is still running)" >&2; exit 1; }

echo "sending to $host:$port"
pod 'rm -rf dvmp/game/Mods/Multiplayer dvmp/game/Mods/MultiplayerDevTools dvmp/repo/DevTools/Dvmp/bin/pod dvmp/repo/DevTools/DvmpScenarios/bin'
send "$install" dvmp/game Mods/Multiplayer Mods/MultiplayerDevTools winhttp.dll doorstop_config.ini steam_appid.txt \
    DerailValley_Data/Managed/UnityModManager
send "$published" dvmp/repo/DevTools/Dvmp/bin/pod .
# dvmp finds its probes and the scenario runner beside Dvmp.csproj (Paths.ProbeSourceDir).
send "$repo/DevTools/Dvmp" dvmp/repo/DevTools/Dvmp Dvmp.csproj Probes
send "$repo/DevTools/DvmpScenarios/bin/Debug" dvmp/repo/DevTools/DvmpScenarios/bin/Debug net48
send "$repo/DevTools" dvmp/repo/DevTools Scenarios
send "$profile" dvmp/profile Users Preferences
echo "done: ssh -i $key -p $port dvmp@$host, then dvmp up --session NAME --transport gns"
