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
# A window manager with a taskbar: harness games start minimised, and with none a minimised
# window can't be seen or restored.
su dvmp -c 'sleep 2; DISPLAY=:1 icewm-session >/tmp/icewm.log 2>&1 &'
su dvmp -c 'websockify --web /usr/share/novnc 127.0.0.1:6901 127.0.0.1:5901 >/tmp/novnc.log 2>&1 &'

# The game: one archive on the global volume (DV_ARCHIVE, else the newest DerailValley-*.tar.zst
# in /workspace), extracted to local disk. Never run it from the volume: its small-file reads are
# 3-7 MB/s, against 100+ MB/s for one big file. Skipped when this archive is already extracted; a
# different one replaces the old game (Mods stays: push.sh puts it there).
# dvmp/ready is written only when the game is in place; otherwise dvmp/boot-error says why, and
# push.sh prints it.
rm -f $home/dvmp/ready $home/dvmp/boot-error
fail() { log "$1"; echo "$1" > $home/dvmp/boot-error; chown dvmp: $home/dvmp/boot-error; exec sleep infinity; }
archive=${DV_ARCHIVE:-$(ls -t /workspace/DerailValley-*.tar.zst 2>/dev/null | head -1)}
if [ -z "$archive" ] || [ ! -f "$archive" ]; then
    fail "no game archive: attach the global volume at /workspace (or set DV_ARCHIVE)"
elif [ "$(cat "$game/.archive" 2>/dev/null)" != "$(basename "$archive")" ]; then
    log "extracting $archive"
    t0=$(date +%s)
    # Read as root (whatever the archive's permissions), written as dvmp.
    su dvmp -c "mkdir -p '$game' && find '$game' -mindepth 1 -maxdepth 1 ! -name Mods -exec rm -rf {} +" \
        && zstd -dc -T0 "$archive" | su dvmp -c "tar -x -C '$game'" \
        && su dvmp -c "echo '$(basename "$archive")' > '$game/.archive'" \
        || fail "extracting $archive failed"
    log "extracted in $(( $(date +%s) - t0 ))s"
fi

echo "$(date -u +%FT%TZ)" > $home/dvmp/ready
log "ready"
exec sleep infinity
