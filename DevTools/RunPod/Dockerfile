# A RunPod pod that runs dvmp test pairs: the Windows game under GE-Proton's Wine on the pod's
# GPU, with no Steam. Holds no game files and no secrets: the game comes from an archive on the
# global volume at boot (boot.sh), the mod, dev tools and dvmp come from the PC (push.sh), and
# sshd takes the key RunPod passes in PUBLIC_KEY. See README.md.
FROM ubuntu:22.04@sha256:0a9887931b0e38af2e3487ed39e29436b0535c4f3f52e57745baaf79d1898956

# Tested on pods (2026-10-24/25). GE-Proton11 also needs only glibc 2.34; to bump it, set both
# from the release's .sha512sum file and retest.
ARG GE_PROTON=GE-Proton10-34
ARG GE_PROTON_SHA512=9fd0b2cfbd501c0b5c892239c392c7283a029b5e5d5a77d3f85b0ce190d555456241a18eebca16b53f094b403499201c13550a3f0b9b365e1a5eb5737cbb7303

ENV DEBIAN_FRONTEND=noninteractive \
    NVIDIA_VISIBLE_DEVICES=all \
    NVIDIA_DRIVER_CAPABILITIES=all

# The libraries Proton's Wine loads from the system (read from the running game's maps on the
# trial pod; Proton carries the rest). The NVIDIA driver and its Vulkan ICD are mounted in by the
# container runtime. i386: Wine's 32-bit helpers during wineboot.
RUN dpkg --add-architecture i386 && apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates curl zstd xz-utils tar procps psmisc sudo openssh-server \
        tigervnc-standalone-server novnc python3-websockify icewm x11-utils \
        libvulkan1 libx11-6 libx11-xcb1 libxext6 libxrandr2 libxi6 libxcursor1 libxcomposite1 \
        libxinerama1 libxrender1 libxfixes3 libxxf86vm1 libxcb1 libxcb-dri3-0 libxcb-present0 \
        libxcb-randr0 libxcb-sync1 libxcb-xfixes0 libfreetype6 libfontconfig1 libgnutls30 \
        libgssapi-krb5-2 libpulse0 libsndfile1 libdbus-1-3 libusb-1.0-0 libudev1 \
        libc6:i386 libx11-6:i386 libxext6:i386 libfreetype6:i386 libfontconfig1:i386 \
        libgnutls30:i386 libdbus-1-3:i386 libudev1:i386 \
    && rm -rf /var/lib/apt/lists/*

RUN curl -fsSL -o /tmp/p.tar.gz https://github.com/GloriousEggroll/proton-ge-custom/releases/download/${GE_PROTON}/${GE_PROTON}.tar.gz \
    && echo "${GE_PROTON_SHA512}  /tmp/p.tar.gz" | sha512sum -c - \
    && mkdir -p /opt/proton && tar -xzf /tmp/p.tar.gz -C /opt/proton --strip-components=1 \
    && rm /tmp/p.tar.gz

# sshd reads keys from /etc/ssh/keys/<user>: RunPod's home folders can be world-writable, which
# StrictModes rejects for ~/.ssh.
RUN useradd -m -s /bin/bash dvmp && echo 'dvmp ALL=(ALL) NOPASSWD:ALL' > /etc/sudoers.d/dvmp \
    && cat /proc/sys/kernel/random/uuid | tr -d - > /etc/machine-id \
    && mkdir -p /etc/ssh/keys /run/sshd \
    && printf 'PasswordAuthentication no\nPermitRootLogin no\nAuthorizedKeysFile /etc/ssh/keys/%%u\n' > /etc/ssh/sshd_config.d/dvmp.conf \
    && rm -f /etc/ssh/ssh_host_*

COPY env.sh /opt/dvmp/env.sh
COPY dvmp boot.sh /opt/dvmp/bin/
RUN chmod 755 /opt/dvmp/bin/* && ln -s /opt/dvmp/bin/dvmp /usr/local/bin/dvmp \
    && echo '[ "$(id -un)" = dvmp ] && . /opt/dvmp/env.sh' > /etc/profile.d/dvmp.sh

# The Wine prefix, made at build time so a pod boots ready: Proton's own Wine Mono (for the
# scenario runner) installs from /opt/proton/files/share/wine/mono, and DXVK's DLLs go in.
# GrabPointer=N: a focused game confines its cursor, which Wine turns into a grab of the whole
# display, so a VNC viewer can't click the taskbar or the other game.
USER dvmp
RUN mkdir -p /home/dvmp/.icewm && printf 'DesktopBackgroundImage=""\nDesktopBackgroundColor="rgb:20/22/26"\n' > /home/dvmp/.icewm/prefoverride
RUN mkdir -p /home/dvmp/dvmp/game /home/dvmp/dvmp/repo/DevTools /home/dvmp/dvmp/profile \
    && . /opt/dvmp/env.sh \
    && WINEDLLOVERRIDES="winedbg.exe=d;mshtml=" timeout 900 wine wineboot -u \
    && timeout 300 wineserver -w \
    && wine reg add 'HKCU\Software\Wine\X11 Driver' /v GrabPointer /t REG_SZ /d N /f </dev/null \
    && wine reg add 'HKCU\Software\Wine\X11 Driver' /v GrabFullscreen /t REG_SZ /d N /f </dev/null \
    && timeout 300 wineserver -w \
    && cp /opt/proton/files/lib/wine/dxvk/x86_64-windows/*.dll "$WINEPREFIX/drive_c/windows/system32/" \
    && cp /opt/proton/files/lib/wine/dxvk/i386-windows/*.dll "$WINEPREFIX/drive_c/windows/syswow64/" \
    && test -d "$WINEPREFIX/drive_c/windows/Microsoft.NET/Framework64/v4.0.30319"
USER root

EXPOSE 22
CMD ["/opt/dvmp/bin/boot.sh"]
