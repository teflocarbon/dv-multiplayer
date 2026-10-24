#!/bin/bash
# Container start on a test pod (runs as root): sshd with RunPod's key, a display for the games, and
# the game extracted from the global volume to the pod's own disk. Then waits; push.sh brings the
# mod, the dev tools and dvmp, and the PC drives the games over SSH.
set -u
log() { echo "[boot $(date +%T)] $*"; }
home=/home/dvmp
game=$home/dvmp/game

# SSH: RunPod passes the account's public keys in PUBLIC_KEY. Host keys are made per pod.
ssh-keygen -A >/dev/null
if [ -n "${PUBLIC_KEY:-}" ]; then
    printf '%s\n' "$PUBLIC_KEY" > /etc/ssh/keys/dvmp
    chmod 644 /etc/ssh/keys/dvmp
else
    log "no PUBLIC_KEY: add an SSH key to the RunPod account, or nobody can log in"
fi
/usr/sbin/sshd

# The NVIDIA driver is mounted in by the container runtime; its Vulkan ICD file may not be.
if [ ! -f /etc/vulkan/icd.d/nvidia_icd.json ]; then
    mkdir -p /etc/vulkan/icd.d
    echo '{"file_format_version":"1.0.0","ICD":{"library_path":"libGLX_nvidia.so.0","api_version":"1.3"}}' \
        > /etc/vulkan/icd.d/nvidia_icd.json
fi

# The games' display. VNC listens on localhost only: view it through SSH with
# `ssh -L 6901:localhost:6901 ...` and http://localhost:6901/vnc.html.
su dvmp -c 'Xvnc :1 -geometry 1920x1080 -depth 24 -localhost -rfbport 5901 -SecurityTypes None -AlwaysShared >/tmp/xvnc.log 2>&1 &'
su dvmp -c 'websockify --web /usr/share/novnc 127.0.0.1:6901 127.0.0.1:5901 >/tmp/novnc.log 2>&1 &'

# The game: one archive on the global volume (DV_ARCHIVE, else the newest DerailValley-*.tar.zst
# in /workspace), extracted to local disk. Never run it from the volume: its small-file reads are
# 3-7 MB/s, against 100+ MB/s for one big file. Skipped when this archive is already extracted.
archive=${DV_ARCHIVE:-$(ls -t /workspace/DerailValley-*.tar.zst 2>/dev/null | head -1)}
if [ -z "$archive" ] || [ ! -f "$archive" ]; then
    log "no game archive (DV_ARCHIVE, or /workspace/DerailValley-*.tar.zst)"
elif [ "$(cat "$game/.archive" 2>/dev/null)" != "$(basename "$archive")" ]; then
    log "extracting $archive"
    t0=$(date +%s)
    su dvmp -c "mkdir -p '$game' && zstd -dc -T0 '$archive' | tar -x -C '$game' && echo '$(basename "$archive")' > '$game/.archive'" \
        && log "extracted in $(( $(date +%s) - t0 ))s" || log "extracting failed"
fi

echo "$(date -u +%FT%TZ)" > $home/dvmp/ready
log "ready"
exec sleep infinity
