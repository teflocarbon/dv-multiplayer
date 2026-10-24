# Environment for Wine and dvmp on a test pod; sourced by the dvmp wrapper and, through
# /etc/profile.d/dvmp.sh, by login shells (so wine run by hand uses the pod's prefix).
export PATH=/opt/proton/files/bin:$PATH
export WINEPREFIX=/home/dvmp/dvmp/prefix WINEDEBUG=-all DISPLAY=:1
# UMM's doorstop loader is winhttp.dll beside the game; DXVK's DLLs are in the prefix; no crash
# dialog (it waits for a click that never comes).
export WINEDLLOVERRIDES="winhttp=n,b;d3d11,dxgi,d3d10core,d3d9=n,b;winedbg.exe=d"
export VK_ICD_FILENAMES=/etc/vulkan/icd.d/nvidia_icd.json XDG_RUNTIME_DIR=/tmp/runtime-dvmp
export DXVK_LOG_PATH=/tmp/dxvk-logs DXVK_LOG_LEVEL=warn
# Wine's locks: fsync where the kernel call is allowed, else esync (RunPod's containers block
# fsync; esync worked), not the slow default that sends every lock through wineserver. esync
# needs many open files.
export WINEFSYNC=1 WINEESYNC=1
# Unity's job workers (DvmpAgent's HarnessMode): Unity counts every core of the machine, not the
# pod's quota, and one worker per core held games at 18 fps.
export DVMP_JOB_WORKERS=4
ulimit -n "$(ulimit -Hn)" 2>/dev/null
# dvmp's Windows paths for the game and DV's user data (Application.persistentDataPath to seed from).
export DVMP_DV_DIR='Z:\home\dvmp\dvmp\game' DVMP_DV_PROFILE='Z:\home\dvmp\dvmp\profile'
mkdir -p "$XDG_RUNTIME_DIR" "$DXVK_LOG_PATH" 2>/dev/null && chmod 700 "$XDG_RUNTIME_DIR" 2>/dev/null
