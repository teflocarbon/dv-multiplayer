# A RunPod pod that runs dvmp test pairs: the Windows game under GE-Proton's Wine on the pod's
# GPU, with no Steam. Holds no game files and no secrets: the game comes from an archive on the
# global volume at boot (boot.sh), the mod, dev tools and dvmp come from the PC (push.sh), and
# sshd takes the key RunPod passes in PUBLIC_KEY. See README.md.
FROM ubuntu:22.04

# Tested on the trial pod (2026-10-24). GE-Proton11 also needs only glibc 2.34; bump and retest.
ARG GE_PROTON=GE-Proton10-34

ENV DEBIAN_FRONTEND=noninteractive \
    NVIDIA_VISIBLE_DEVICES=all \
    NVIDIA_DRIVER_CAPABILITIES=all

# The libraries Proton's Wine loads from the system (read from the running game's maps on the
# trial pod; Proton carries the rest). The NVIDIA driver and its Vulkan ICD are mounted in by the
# container runtime. i386: Wine's 32-bit helpers during wineboot.
RUN dpkg --add-architecture i386 && apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates curl zstd xz-utils tar procps psmisc sudo openssh-server \
        tigervnc-standalone-server novnc python3-websockify \
        libvulkan1 libx11-6 libx11-xcb1 libxext6 libxrandr2 libxi6 libxcursor1 libxcomposite1 \
        libxinerama1 libxrender1 libxfixes3 libxxf86vm1 libxcb1 libxcb-dri3-0 libxcb-present0 \
        libxcb-randr0 libxcb-sync1 libxcb-xfixes0 libfreetype6 libfontconfig1 libgnutls30 \
        libgssapi-krb5-2 libpulse0 libsndfile1 libdbus-1-3 libusb-1.0-0 libudev1 \
        libc6:i386 libx11-6:i386 libxext6:i386 libfreetype6:i386 libfontconfig1:i386 \
        libgnutls30:i386 libdbus-1-3:i386 libudev1:i386 \
    && rm -rf /var/lib/apt/lists/*

RUN curl -fsSL -o /tmp/p.tar.gz https://github.com/GloriousEggroll/proton-ge-custom/releases/download/${GE_PROTON}/${GE_PROTON}.tar.gz \
    && curl -fsSL -o /tmp/p.sha512sum https://github.com/GloriousEggroll/proton-ge-custom/releases/download/${GE_PROTON}/${GE_PROTON}.sha512sum \
    && echo "$(cut -d' ' -f1 /tmp/p.sha512sum)  /tmp/p.tar.gz" | sha512sum -c - \
    && mkdir -p /opt/proton && tar -xzf /tmp/p.tar.gz -C /opt/proton --strip-components=1 \
    && rm /tmp/p.tar.gz /tmp/p.sha512sum

# sshd reads keys from /etc/ssh/keys/<user>: RunPod's home folders can be world-writable, which
# StrictModes rejects for ~/.ssh.
RUN useradd -m -s /bin/bash dvmp && echo 'dvmp ALL=(ALL) NOPASSWD:ALL' > /etc/sudoers.d/dvmp \
    && cat /proc/sys/kernel/random/uuid | tr -d - > /etc/machine-id \
    && mkdir -p /etc/ssh/keys /run/sshd \
    && printf 'PasswordAuthentication no\nPermitRootLogin no\nAuthorizedKeysFile /etc/ssh/keys/%%u\n' > /etc/ssh/sshd_config.d/dvmp.conf \
    && rm -f /etc/ssh/ssh_host_*

COPY env.sh /opt/dvmp/env.sh
COPY dvmp boot.sh /opt/dvmp/bin/
RUN chmod 755 /opt/dvmp/bin/* && ln -s /opt/dvmp/bin/dvmp /usr/local/bin/dvmp

# The Wine prefix, made at build time so a pod boots ready: Proton's own Wine Mono (for the
# scenario runner) installs from /opt/proton/files/share/wine/mono, and DXVK's DLLs go in.
USER dvmp
RUN mkdir -p /home/dvmp/dvmp/game /home/dvmp/dvmp/repo/DevTools /home/dvmp/dvmp/profile \
    && . /opt/dvmp/env.sh \
    && WINEDLLOVERRIDES="winedbg.exe=d;mshtml=" timeout 900 wine wineboot -u \
    && timeout 300 wineserver -w \
    && cp /opt/proton/files/lib/wine/dxvk/x86_64-windows/*.dll "$WINEPREFIX/drive_c/windows/system32/" \
    && cp /opt/proton/files/lib/wine/dxvk/i386-windows/*.dll "$WINEPREFIX/drive_c/windows/syswow64/" \
    && test -d "$WINEPREFIX/drive_c/windows/Microsoft.NET/Framework64/v4.0.30319"
USER root

EXPOSE 22
CMD ["/opt/dvmp/bin/boot.sh"]
