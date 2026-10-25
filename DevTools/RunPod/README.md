# Test pairs on RunPod

A RunPod GPU pod runs dvmp test pairs in the cloud: the Windows game under GE-Proton's Wine, on the
pod's GPU, with no Steam. Pairs join over the Debug-only GameNetworkingSockets transport
(`--transport gns`), and dvmp itself is its Windows build, run by the same Wine. A pair on an RTX
A4500 pod passed `recall` (trial, 2026-10-24).

| File | What it is |
|---|---|
| `Dockerfile` | The pod image: Ubuntu 22.04, GE-Proton with its DXVK, a ready Wine prefix with Wine Mono, sshd, a VNC display. No game files and no secrets. |
| `boot.sh` | Runs when a pod starts: sshd with RunPod's key, the display, and the game extracted from the global volume. |
| `env.sh`, `dvmp` | The Wine and dvmp environment on the pod; `dvmp` runs dvmp there. |
| `push.sh` | Run on the PC: sends a pod the mod, the dev agent, dvmp, the scenarios and the DV user data. |

## The game

The game lives on a RunPod **global volume** (any datacenter, GPU pods only) as one archive,
`DerailValley-<appid>-<buildid>.tar.zst`: `DerailValley_Data`, `MonoBleedingEdge` and the three
launcher files, with no `Mods`. `boot.sh` extracts the newest one (or `DV_ARCHIVE`) to the pod's disk,
about 26 s for 4.8 GB. Never run the game from the volume: it's object storage, small-file reads
run at 3–7 MB/s, and the game sits on a black window for minutes.

## Building the image

Any Docker works; on Windows, WSL's. From `DevTools/RunPod`:

```bash
docker build -t dvmp-pod:dev .
docker tag dvmp-pod:dev <registry>/<owner>/dvmp-pod:dev
docker push <registry>/<owner>/dvmp-pod:dev
```

- Push it to a **private** registry (for example GHCR). The image holds no game files and no
  secrets, but there's no reason to publish it. For GHCR, log in with a token that has
  `write:packages`, piped to `docker login --password-stdin`, and `docker logout` afterwards.
- RunPod pulls it with a **container registry auth** holding a read-only token (for GHCR, a classic
  token with only `read:packages`). Make a private **template** with the image, that registry auth,
  `22/tcp` and a 40 GB container disk, and create pods from it.

## A pod

- Image: this one, through the template. GPU: about 8 GB of VRAM per pair (Sizing, below).
- Mount the global volume at `/workspace` (`mounts.global: [{volumeId, path: "/workspace"}]` in the
  v2 pod API). Expose `22/tcp` (direct SSH; RunPod's ssh.runpod.io proxy is PTY-only).
- The RunPod account's SSH public key reaches the pod in `PUBLIC_KEY`; log in as `dvmp`.

## Each build

On the PC, after building the mod (Debug, into the test install) and DvmpScenarios:

```bash
dotnet publish DevTools/Dvmp/Dvmp.csproj -c Debug -r win-x64 --self-contained true -o "$LOCALAPPDATA/dvmp/pod-dvmp"
bash DevTools/RunPod/push.sh <pod ip> <port of 22/tcp>
```

Then on the pod: `dvmp up --session g1 --transport gns`, `dvmp scenario run NAME --session g1`.
To watch the games: `ssh -L 6901:localhost:6901 ...` and open http://localhost:6901/vnc.html.

## Sizing

- Per game: 6–7 GB RAM and 3.3–4.4 GB VRAM, about 7.7 GB VRAM a pair. The host is bigger: it
  loads the whole map's tiles.
- Two pairs on a 20 GB pod with 15.3 vCPU passed `recall` at once (2026-10-25): four games at the
  20 fps background cap took about 7.2 cores and 15.4 GB VRAM. So VRAM decides how many pairs
  fit: about one pair per 8 GB.
- That relies on `DVMP_JOB_WORKERS=4`, which `env.sh` exports and harness mode reads. Unity starts
  a worker per core it sees, and under Wine it sees the whole machine (47 workers on a 48-core host),
  which held games at 18 fps.
- Four pairs on an RTX PRO 4500 SE (32 GB, 13.6 vCPU) passed `recall` at once: 31.3 GB of VRAM,
  so that's the most a 32 GB card holds; about 8–9 cores.
- Create pods from the RunPod console with the `dvmp-pod` template: the API's create has refused
  GPUs the console offered. Attach the global volume when creating the pod; it can't be added later.

## Under Wine

- WineHQ's Wine crashes DV in d3d11 (no `VK_KHR_external_memory_win32`); GE-Proton's works.
- `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` for dvmp: .NET otherwise loads Proton's ICU and exits.
- GNS binds a dual-stack IPv6 socket under Wine; dvmp's port check reads both tables.
- fsync is blocked in RunPod's containers; esync works and is on.
- The dev agent doesn't hook `MonoBehaviour.StartCoroutine` under Wine: Harmony wrote that patch as
  an indirect jump through uncommitted memory, and every coroutine in the game threw (2026-10-25).
  Probe coroutines started with a plain `StartCoroutine` go untracked on pods; use
  `ctx.StartCoroutine` or `ctx.StartBackground`.
