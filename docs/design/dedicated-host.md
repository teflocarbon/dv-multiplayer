# Dedicated host: the full game as a server with no local player

> Status: built through stage 1 and stage 3 (a, b):
> - stage 0: a DevTools host-only harness mode and a perf probe;
> - stage 1: the `-mp-dedicated` service-identity host;
> - stage 3 (a, b): the world follows a player.
>
> Stage 2, stage 3c and stage 4 aren't built; see
> [Implementation status](#implementation-status-2026-10-17). The investigation below was checked
> against the decompiled game (build 99, Unity 2019.4.40) and this repository on 2026-10-17. The
> headless launch was tried for real with the dvmp harness, using the throwaway patches described
> in [Prototype](#prototype-what-was-run).

## Purpose

LocoMP ships a headless server that doesn't run Derail Valley, so it can't run DV's physics. This
mod's host is already the real game: it simulates every train and validates client intent (see
[architecture.md](../architecture.md#host-authority)). A dedicated server for this mod should
therefore be **the full game running as host with nobody sitting at it**: no window, no GPU if
possible, no local player playing, all players remote.

This document answers whether that works, what breaks, and what it would take. An earlier,
lighter feasibility report from the code-audit session reached the same overall shape: keep the
host's player but hide it from the network, start unattended, and centre streaming on clients.
That report also covers what is reusable from LocoMP. This document adds real-game measurements,
a headless run, and the full list of host-is-a-player assumptions.

**Short answer.** It works better than expected. With `-batchmode -nographics` and a handful of
small patches, the real game loaded a career save, hosted, let a client join and simulated a
locomotive the client saw move, using 4.0 GB of RAM and no GPU. The hard part isn't rendering.
It's that DV centres streaming, terrain colliders, physics LOD and origin shifting on **one**
player, and a dedicated host has players spread across the map. The second hard part is that the
mod's host is a player in many places, including its loopback client, which must stay.

The recommended target is still the **light host**: the real game minimised in desktop mode, which
keeps other mods working. `-nographics` is a later, optional step (section 1).

Decompiled paths below are relative to an `ilspycmd -p` project of each assembly, for example
`Assembly-CSharp/SaveGameManager.cs:476`. `WorldStreamer.dll` (`Streamer`, `WorldMover`) was
decompiled per type with `ilspycmd -t`.

## Implementation status (2026-10-17)

**Stages 0, 1 and 3 (a, b) are built.** Stage 2, stage 3c (several players far apart) and stage 4
of the [plan](#recommended-plan) are still to do.

### Stage 2 (first part): running unattended

Only on a dedicated host, apart from the admin commands, which an ordinary host's admins can use
too.

- **Pause when empty** (`DedicatedHost.OnPlayersChanged`, called from `NetworkServer.OnLoaded`, at
  each login and at the end of each disconnect):
  - With nobody online the host saves (if a player just left) and pauses through its own
    `AppUtil.RequestPause` request. `AllowPause` allows that only while a dedicated host has no
    players.
  - The first login removes the request.
  - Time, jobs and loose trains don't run unwatched. The network tick runs on real time, so joins
    still work while it's paused.
- **Saves:**
  - Every `-mp-autosave` minutes (default 10) while anyone is online, plus when the last player
    leaves, and on `/save` and `/stop`. These come on top of vanilla autosave.
  - `DedicatedHost.Save` writes an Auto save, but only when DV's `SaveAllowed` agrees.
  - Auto saves rotate through DV's autosave slots, so frequent timed saves push out older saves.
    A test with `-mp-autosave 1` rotated out the save it had loaded within about 15 minutes. The
    rotation acts as bounded backups, but a long-term backup copy outside the slots is still to do.
- **Admins** (`ChatManager.IsAdmin`):
  - Who: the host's own player, the player with this install's GUID (on a dedicated host, that's
    its owner playing from the same PC), and the GUIDs in the new `AdminGuids` setting.
  - `/kick`, `/server` and setting others' crew names now take an admin, not only the host.
  - New commands: `/save`; `/stop`, which saves, says so in chat and quits a dedicated host;
    `/payfees`, which pays every payable fee and debt from the shared wallet the way the career
    manager's cash register does.
  - GUIDs aren't secret or verified, so the server password is what keeps strangers out.
- **Heartbeat:** with `-mp-heartbeat PATH`, every 5 s the host replaces `PATH` with a JSON line: time,
  tick, fps, players, paused, last save, Mono heap. A watchdog can restart the game if it goes
  stale. The watchdog itself isn't built.

Checked in the game (2026-10-17):

- **Auto-hosted with `-mp-autosave 1 -mp-heartbeat`:** it paused at load with nobody online, and
  unpaused when a client joined. It saved on the minute, and the heartbeat showed the players, the
  pause and the last save.
- **The `dedicated-admin` scenario passed:**
  - a non-admin's `/save` did nothing, and an admin's saved;
  - `/payfees` was refused while the wallet couldn't cover 10 fees ($550,990), then paid them all
    once when it could;
  - the host saved and paused when the last player left, and unpaused when they came back.
- **Scenario setup:** chat commands are sent without the slash, because Git Bash turns `/save`
  into a Windows path.

Still to do in stage 2:
- moving the old host body's inventory to the owner's GUID the first time a save is opened as a
  dedicated host;
- a save lock, so the same save can't be opened in single player while the server runs;
- paying fees through the career manager's own screen from a client;
- the watchdog.

### Stage 3 (a, b): the world follows a player

Only on a dedicated host; an ordinary host loads its world around its own player as before.

- **Streaming anchor** (`Components/Networking/DedicatedHostAnchor.cs`, created from
  `NetworkServer.OnLoaded`):
  - DV's `PlayerTracker` follows `PlayerManager.ActiveCamera`, which is `PlayerCameraOverride`
    when that's set (`Assembly-CSharp/PlayerTracker.cs`, `PlayerManager.cs:14-42`).
  - The anchor is a disabled camera with culling mask 0, put in as that override. Each frame it
    moves to the player who has been connected longest, at eye height.
  - The scene streamers, the terrain grid with its colliders, the origin shift, `ItemDisablerGrid`
    and the other camera-distance checks follow it. The body stays parked, and vanilla still saves
    its position.
  - With nobody connected the anchor stays where it was. It takes the override back if DV clears it.
- **Physics LOD** (`Patches/Train/DistanceLodSystemPatch.cs`):
  - `DistanceLodSystem` measures from a `NativeList` of reference points (the local player, plus
    the external camera) and hands it to a scheduled job in the same `OnUpdate`, so a postfix
    would race the job.
  - A transpiler replaces the load of that list at the hand-over with a call that adds every
    gameplay player's position.
  - Cars near any player get the near LODs on the host: walkable and item colliders, and active
    items riding on them.
- **`far-derail` scenario** ([testing.md](../testing.md#regression-gate)): it passed on
  2026-10-17. The client went to OR, 4.5 km from the host's body. A tank car the host derailed
  next to the client came to rest on the host's ground: not kinematic, speed 0, world cell loaded,
  and 0.00 m from the client's copy. `cab-controls`, `lost-and-found`, `rejoin` and
  `rejoin-crash` passed on the same dedicated pair.

Still open:

- **Several players far apart:** one anchor follows one player, so the others get what a client
  far from a playing host gets today. That's stage 3c.
- **Normal hosting:** neither change applies there, so a client far from a playing host still sees
  that host freeze its derailments. Adding remote players to the physics-LOD points would also suit
  normal hosting, but it changes how cars near clients behave on a playing host, so it's left for
  its own change.

### Stage 1: the service-identity host (`-mp-dedicated`)

Without the flag nothing changes. With it, the game is a dedicated host:

- **The loopback client stays.** Its `ServerPlayer` is a service identity,
  `ServerPlayer.IsServiceHost` (`Networking/Data/ServerPlayer.cs`), which is computed from
  `NetworkLifecycle.IsDedicatedHost` and `IsHost(player)`, so a client can't claim it. It logs in
  as "Dedicated host" with `Settings.ServiceGuid`, so the player's own game on the same install
  joins as itself (`NetworkClient`).
- **It isn't a player.** `NetworkServer.GameplayPlayers` and `GameplayPlayerCount` leave it out.
  They're used for:
  - `MaxPlayers` at login;
  - `AnyPlayerSqrMag` (job generation, loco spawners, the car deleter, cash registers, refill
    stations, warehouses);
  - car visits;
  - Lost and Found's nearby-player protection;
  - world-item replenishment;
  - the existing-players list sent to a joiner;
  - the lobby's player count;
  - MPAPI's `PlayerCount`.

  When it finishes loading, it gets only `RemoveLoadingScreen`: no `PlayerJoined`, no chat line,
  no `PlayerReady` (`NetworkServer.OnServerboundLoadStateUpdatePacket`).
- **"Host is alone" checks** (`PlayerCount == 1` in nine patches, plus `AllowPause` and the debt
  tracker) go through `NetworkLifecycle.IsHostPlayingAlone`, which is never true on a dedicated
  host. Vanilla code that acts on the local player doesn't run, and the game can't be paused.
- **The parked body** gets no `LocalPlayerTracker`, so it sends no position. It also gets no chat
  UI, whose construction pauses the game.
- **Reduced rendering** (`DedicatedHostMode`, never saved): muted, quality level 0, texture limit
  2, a 512 MB texture budget, `runInBackground`, and a frame cap of at least 25 (default 30).
- **Auto-host** (`DedicatedHost.AutoHost`, a port of the dvmp `host` probe): with `-mp-save`, it
  hosts that save from the main menu through DV's launcher.
- **MPAPI** `IsDedicatedServer` is true.
- dvmp `up --dedicated` launches the host with the flag, and `gate.sh` passes `UP_ARGS` to its
  `dvmp up` calls.

To run one:

```text
DerailValley.exe -nonvr -mp-dedicated -mp-save "My Career" [-mp-port 7777] [-mp-visibility private|friends|public] [-mp-fps 30]
```

`-mp-save` takes a save name or UID, a session name (its latest save), or `continue` (what the
launcher shows). Server name, password, max players and details come from the mod's settings.
Without `-mp-save` the mode waits for hosting to start some other way, which is how dvmp drives it.

Checked in the game (2026-10-17):

- **Dedicated host:** zero gameplay players and out of loading mode, with no tracker and no chat.
  It ran at 30 fps, muted, at quality level 0, not paused.
- **Client:** no avatar for the host and no entry in its player list.
- **Host's view of the client:** it is the only gameplay player, it has an avatar, and the
  nearest-player distance is measured to it.
- **Auto-host:** `-mp-dedicated -mp-save "DVMP Test Career 2" -mp-port 7899 -mp-visibility private`
  hosted in 44 s with no probe, and a dvmp client joined.
- **Scenarios against a dedicated host** (`UP_ARGS=--dedicated`, one pair, the 03:37:14 save of
  "DVMP Test Career 2"): all nine passed. The runs were:
  - `lost-and-found`
  - `manual-uncouple` and `manual-recouple-lag`
  - `cab-controls`
  - `car-fire`
  - `rejoin` and `rejoin-crash`
  - `shop`
  - `jobs`

  The gate's `jobs` run is `ACCEPT=host`, so the harness took the job with the parked body. That
  shows the host-side job path still works, not that a dedicated host should be taking jobs.
- **Normal mode on the same build:** `rejoin`, `rejoin-crash` and `jobs` passed on one pair.

Not done in stage 1, because they belong to stage 2 or later:

- **The parked body still has physics.** It stands where the save put it and isn't made kinematic.
- **Old host inventory isn't migrated.** Its inventory from earlier, hosted play stays on the body.
- **Fees and debt** are still only payable at the host's own career manager.
- **No empty-server policy.** The game never pauses, so the world runs with nobody online.

### Stage 0: the harness light host

- `dvmp up --host-only --session NAME` starts or reuses only a host. Its player is `DVMP-Host` with
  its own GUID, and it runs at 30 fps in the background. A later `dvmp up` in the same session joins
  a dvmp client through the client-only path, or the user joins from their own game. See
  [DevTools/README.md](../../DevTools/README.md#host-only-sessions).
- `dvmp perf` samples frame times, ticks, memory (with the retained heap), physics and world
  counts, players, and packets per type.

First numbers (2026-10-17, "DVMP Test Career 2", 1,273 cars, 36 trainsets, lean harness host at a
30 fps cap):

| | Host alone | Host with one client |
|---|---|---|
| Frames | 30.0 fps, p99 34 ms | 29.9 fps, p99 34 ms |
| Ticks | 24.1/s | 24.0/s |
| Working set / Unity allocations / retained Mono heap | 5.3 GB / 3.3 GB / 1.06 GB | 5.3 GB / 3.3 GB / 1.06 GB |
| Packets sent | 72/s, all `CommonTrainPortsPacket` from its own loopback client | `ClientboundTrainsetPhysicsPacket` 301/s, `ClientboundTrainControlCommitPacket` 112/s, `ClientboundBrakeStateUpdatePacket` 62/s |

Two things show up straight away:

- **Its own loopback traffic.** Alone, the host sends its loco ports to its own server through its
  loopback client, as section 3 describes.
- **The parked host is still a player.** The client sees it as an avatar, and it's in
  `ServerPlayers`.

The headless (`-nographics`) runs in section 1 were done by hand, with the probe in
[Prototype](#prototype-what-was-run).

## 1. Running without graphics

### What was tried

A game was launched by hand in its own dvmp session (`dedi`), with the normal harness
arguments plus `-batchmode -nographics`:

```text
DerailValley.exe -nonvr -batchmode -nographics -logFile ... -dvmp-role host -dvmp-session dedi
```

`dvmp eval` then installed the patches below, ran the `host` probe on "DVMP Test Career 2", and
`dvmp up --session dedi` joined a normal (windowed, lean) client to it.

| Step | Result |
|---|---|
| Boot, UMM, both mods | Fine. `Renderer: Null Device`; `Application.isBatchMode` true; Steam valid and logged on (the Steam client was running). |
| Main menu | Fine; the `host` probe drives DV's launcher as usual. |
| World load | Fine, all 13 loading steps, about 40 s. |
| Host's own client join | Fine, through `RemoveLoadingScreen`. |
| **Unpatched: first pause** | **Native crash.** See below. |
| Patched: hosting | Stable for the whole session (about 15 minutes). |
| Save (`SaveGameManager.Save`) | Works with a blank thumbnail. |
| Client join | Full join in 68 s (launch to out of loading mode). |
| Loco driven on the host | DE2 started, drove 86 m up to 6.9 m/s and braked. The client's copy matched to within 4 cm. |

### What breaks

**Any camera render with effects crashes the process.** The first run crashed natively right after
the join. `ChatGUI.BuildUI` (`Multiplayer/Components/Networking/UI/ChatGUI.cs:486`) briefly
pauses the game to find the save/load prefab. Pausing raises `SaveGameManager.OnGamePausing`
(`Assembly-CSharp/SaveGameManager.cs:186`), which stashes a screenshot through
`ScreenshotTaker.TakeScreenshotWithoutUI` (`ScreenshotTaker.cs:59-86`). That calls
`PlayerManager.ActiveCamera.Render()`, the camera's `WetStuff.OnPreRender` command buffer throws
under the Null device, and Unity aborts. The same path runs on **every save**: `DoSaveIO` builds a
thumbnail with `GetThumbnail()` (`SaveGameManager.cs:424`, `:500-537`).

Explicit render calls in the game that a headless host has to stub:

| Call | Where | When it runs on a host |
|---|---|---|
| `ScreenshotTaker.TakeScreenshotWithoutUI` | `SaveGameManager.CaptureScreenshot` (`:476`) | Every pause and every save |
| `SaveGameManager.GetThumbnail` | `:500` | Every save |
| `RenderTextureSystem.RenderNextJob` → `cam.Render()` | `DV.RenderTextureSystem/RenderTextureSystem.cs:94-124` | Every booklet, receipt and licence texture. The first run loaded with this unpatched, so its plain camera may be fine. That isn't proven. |
| `BlackoutScreen.Blackout` → `camera.Render()` x3 | `DV.Hacks/BlackoutScreen.cs:26-45` | Scene switches (`SceneSwitcher.cs:90`), quit to menu (`PauseMenu.cs:31`) |
| `DynamicOcclusionDepthBuffer.ProcessOcclusionInternal` | `VLB/DynamicOcclusionDepthBuffer.cs:36` | Volumetric light beams with depth occlusion |

**Per-frame errors fill the log.** Nothing else threw fatally, but the host log grew by about
1 MB a minute (7 MB in 8 minutes, so about 1.4 GB a day):

| Exception | Count | Source |
|---|---|---|
| `colors.Length is 4 and exceeds the maximum number of supported render targets (1)` | ~4,100 (every frame) | `DV.VFX.DeferredDecalRenderer.LateUpdate` |
| `Kernel 'DropletRendering' not found` | ~100 | Rain on windows (`DV.Rain`) |
| `Kernel 'MergeInstancedIndirectBuffers' not found`, then 4 NREs | Once at load | Vegetation Studio Pro |

Disable `DeferredDecalRenderer`, the rain window simulation and the vegetation system on a
headless host. The vegetation and terrain visuals are pure presentation.

**Things that turned out fine:**

- **Steam.** `DVSteamworks` calls `SteamClient.Init(588030)` in a try/catch and only logs on
  failure (`DV.Platform.Steam/DVSteamworks.cs:47-72`). There is no ownership check, no
  `RestartAppIfNecessary`, no entitlement subclass (`Bootstrap.cs:137-161`), and the exe has no
  SteamStub wrapper. The game itself runs without Steam; the **mod's** transport needs it (see
  [Operations](#4-operations)).
- **VR.** `-nonvr` forces the desktop rig (`Bootstrap.cs:105-109`). Without it the game loads
  OpenVR because `boot.config` has `vr-enabled=1`. Always pass it.
- **Loading screen.** For game loads `LoadingScreenManager` makes no camera
  (`LoadingScreenManager.cs:44-76`). `ScreenFade` returns when `Camera.main` is null
  (`ScreenFade.cs:13-16`).
- **Focus and background.** `PauseInBackground` defaults to off for desktop
  (`DV.Preferences/Preferences.cs:65`) and `Application.runInBackground` stays on
  (`GraphicsOptions.cs:431-458`). Nothing pauses when unfocused or minimised.
- **Frame rate.** The harness cap held (about 20 fps). Without it, `FrameLimiter` sets
  `targetFrameRate = 0` with vsync (`FrameLimiter.cs:36-52`), and vsync means nothing in batchmode,
  so the game would spin uncapped. A server must set `Application.targetFrameRate` itself. Fixed
  step stays 1/60 (`FrameLimiter.cs:13`).
- **Most camera dereferences.** They are null-guarded, and the player rig's camera exists anyway
  (see the next point).

### The local player must exist, even if nobody plays it

The world doesn't finish loading without a registered player:
`StartGameData_FromSaveGame.LoadingNonBlockingCoro` waits for `PlayerManager.PlayerTransform`
(`StartGameData_FromSaveGame.cs:515-518`) before it sets `carsAndJobsLoadingFinished`.
`SaveGameManager.SaveAllowed` requires it too, along with the `Inventory` singleton
(`SaveGameManager.cs:200-212`). Many systems dereference it without checks (`BedSleepingController`,
`UnusedTrainCarDeleter.cs:226`, `HazmatTileManager.cs:1542`, ...).

So a dedicated host **keeps DV's player rig and parks it**: rendering off, input off, kinematic,
never shown to clients. It's an invisible anchor, not a player. The mod's loopback client stays
too, for different reasons (section 3).

### `-nographics` or plain `-batchmode`?

| | `-batchmode -nographics` | `-batchmode` (real device, no window) | Minimised window (today's harness) |
|---|---|---|---|
| GPU memory | None | Some (textures still upload) | ~3 GB lean, ~5 GB full |
| RAM (working set) | **4.0 GB** with one client joined | Not measured | 5.0-5.5 GB |
| Unity allocations | 1.95 GB | – | 3.1-3.4 GB |
| Render calls | Must be stubbed (crash) | Work | Work |
| Per-frame GPU errors | Must be silenced (log growth) | None expected | None |
| Linux / no-GPU VPS | Possible in principle (DV ships Windows only, so it would need Wine) | Needs a GPU | Needs a GPU and a desktop |

**Recommendation: build the light host first.** That's the real game in desktop mode, minimised,
lowest quality and `-nonvr`, as the dvmp harness already runs it. The target chosen for this work
is that light host, not a `-nographics` server, because other mods may render or read the camera
and the light host keeps them working. The table shows `-nographics` is close: it needs five
render stubs and three systems silenced, and it saves about 1 GB of RAM and all of the GPU. Keep it
as a later option for rented, GPU-less machines, behind a compatibility check of the mods in use.
Only the mods installed here were loaded under it (DVCustomCarLoader with a custom car,
PassengerJobs, DVLangHelper); none failed at load, but their gameplay wasn't exercised.

## 2. What is simulated far from the host's player

Measured on the headless host (1,273 cars, the parked player at the career start):

| Distance from the host's player | Cars | Physics LOD | Kinematic | Terrain collider under the car |
|---|---|---|---|---|
| 0-200 m | 4 | 2 | 0 | 4/4 |
| 200 m-1 km | 86 | 3 | 0 | 30/30 sampled |
| 1-3 km | 1,083 | 5 (38 at 4) | 1,046 | 5/30 |
| 3-6 km | 97 | 5 | 97 | 0/30 |
| 6 km+ | 4 | 4 | 4 | 0/4 |

Then the host's player was teleported 4 km away from a DE2:

- The loco still drove and coasted at LOD 3-4, dynamic, and the client agreed to within 7 cm.
- **Derailed there, it went kinematic and hung in place** (no ground). With the player back, the
  tiles loaded in about 7 s, the car became dynamic again and dropped 10 cm onto the ground.

### What runs regardless of distance

- **Loco simulation.** `SimController.Update` ticks the sim flow every frame with no distance term
  (`DV.Simulation.Cars/SimController.cs:240-267`). The exception is `MultipleUnitStateObserver`,
  which only runs with the player in the car or a paired remote (`MultipleUnitStateObserver.cs:66-92`).
- **Bogie and track physics.** `Bogie.FixedUpdate` follows the spline with no distance gating
  (`Bogie.cs:455-548`). Track is its own scene (`WorldStreamingInit.railwayScenePath`,
  `WorldStreamingInit.cs:230`) and stays loaded.
- **Sleep.** `TrainsOptimizer` decides sleep per track, from motion only (`DV.Optimizers/TrainsOptimizer.cs:44-93`),
  and `TrainCarPositionMonitorSystem` calls a car stationary after 1 s under 1 cm
  (`DV.ECS.Train/TrainCarPositionMonitorSystem.cs:576-611`). Why most cars beyond 1 km in the
  table were also kinematic wasn't established. They were all asleep, and neither
  `TrainPhysicsLod` nor `TrainsOptimizer` writes `isKinematic`, so check whether they were
  derailed (see `DerailedTrainCarIsKinematicHandler` below) or set by something else. A loco
  started 4 km away ran normally.
- **Stress derailment, couplers, collisions.** `TrainStress` (`TrainStress.cs:170-284`), coupler
  joints and auto-couple overlaps (`Coupler.cs:212`, `:400-520`), and the car `[collision]`
  colliders (`TrainCarColliders.cs:193-200`) have no player term.
- **Junctions** (`DV.RailTrack`), **hazmat fire on cars** (`TrainCarTileInteraction.cs:92-191`), and
  **out-of-world rescue**: a car below y = -1 is moved onto the global in-memory heightmap
  (`TrainCar.cs:1714-1723`, `DV.WorldTools/HeightMapProvider.cs`).

### What follows the one player

| System | Anchor | Effect far from it | Code |
|---|---|---|---|
| Near and far scene streamers | `WorldMover.playerTracker` (one) | Scenery, buildings and platforms unload | `WorldStreamer: Streamer` (tile from tracker, ±`loadingRange`) |
| Terrain grid | Same tracker | No `TerrainCollider` outside `(2·ring+1)²` cells; fixed pool | `DV.TerrainSystem/TerrainGrid.cs:244-268`, `:401-463` |
| Derailed cars | Loaded cells | Kinematic until near, far and terrain are all loaded | `DV/DerailedTrainCarIsKinematicHandler.cs:12-29`, `WorldStreamingInit.cs:132-164` |
| Physics LOD | `PlayerTransform` (+ external camera) | LOD 3-5: no walkable, item or interior colliders; items on cars deactivated and kinematic | `DistanceLodSystem.cs:436-446`, `TrainPhysicsLod.cs:13-15`, `:157-269`, `DV.Items/TrainItemActivityHandler.cs:192-223` |
| Cab interiors | Local player in the car (LOD -1) | Only loaded for the host's own player | `TrainPhysicsLod.cs:135-147`, `:174-191` |
| Origin shift | Same tracker | One origin for everyone | `WorldStreamer: WorldMover` (shift past `moveRange`) |
| World items | `ActiveCamera`, 128 m cells | Items outside the 3x3 cells are deactivated | `DV/ItemDisablerGrid.cs:9-50`, `DV/ItemDisabler.cs:158-210` |
| Dropped-item respawn | `PlayerTransform`, `ActiveCamera` | Respawn or Lost and Found beyond 200 m / 1 km; kinematic far from the camera | `RespawnOnDrop.cs:11-13`, `:86-89`, `:300-368` |
| Station job generation | `PlayerTransform` | Already patched to any player (`StationJobGenerationRangePatch.cs`) | `StationJobGenerationRange.cs:5-57`, `StationController.cs:190` |
| Loco spawners | Player distance | Already patched (`StationLocoSpawnerPatch.cs`) | `StationLocoSpawner.cs:75-99` |
| Garage car respawn | Player distance | **Not patched** | `GarageCarSpawner.cs:96-115` |
| Unused car deleter | `PlayerTransform`, visits | Partly patched (`UnusedTrainCarDeleterPatch.cs`); visits come only from the local `CarChanged` | `UnusedTrainCarDeleter.cs:141`, `:220-232`; `DV/CarVisitChecker.cs:47-82` |
| Warehouse machines | `ActiveCamera`, 600 m | Already patched (`PlayerDistanceGameObjectsDisablerPatch.cs`) | `DV.Optimizers/PlayerDistanceGameObjectsDisabler.cs:39-61` |

**This already affects today's mod, not only a dedicated host.** Whenever the host's player is a
few kilometres from a client, a car that derails near the client freezes on the host. Every car
near the client sits at LOD 3-5 on the host too. A dedicated host just makes it the normal case.

### What a no-player host must anchor around each remote player

1. **Terrain colliders, and the near and far tiles**, around each remote player and each moving or
   derailed consist. Otherwise derailed cars freeze and the host has no ground to validate items
   against. DV supports one anchor: `Streamer` reads the singleton `WorldMover.playerTracker`, and
   `TerrainGrid` has a fixed collider pool that drops everything outside one region. Options, from
   cheapest:
   - **Heightmap ground proxies.** The heightmap is global and in memory, so generate a coarse
     collider patch around each remote player and each derailed car. That covers derailments and
     items on open ground but not buildings, platforms or tunnels.
   - **Patched multi-anchor terrain.** Make `TerrainGrid` load the union of cells around N anchors,
     with a pool sized to match.
   - **Patched multi-anchor streamers.** Make `Streamer` load the union of tiles around N anchors.
     That's the most memory, and the most invasive.

   Whether station platforms and buildings live in the streamed scenes is scene data the code
   doesn't show. Measure it before choosing.
2. **Physics LOD reference points.** `DistanceLodSystem` already takes the minimum distance over a
   list (`DistanceLodSystem.cs:316-330`). Add each remote player's position, so cars near a client
   get walkable and item colliders and their items stay active. It's a transpiler on one method.
3. **Origin.** Pin the tracker at a fixed point, for example the map centre (the map is 16,384 m
   square, `LevelInfo.cs:20`), or move it to the players' centroid now and then. From the centre,
   no player is more than about 11.6 km away, where float32 resolution is about 1 mm. That's
   acceptable, though it equals the 2 mm stationary threshold for cars with loaded interiors.
4. **Player-distance systems.** Item disabling, drop respawn, garage spawns and car-visit tracking
   need to treat every remote player as a player, as job generation already does.

## 3. What in the mod assumes the host is a player

Paths are under `Multiplayer/`. Line numbers are from the working tree on 2026-10-17 (beta
`ee77d557` plus #87).

### Keep the loopback client: it isn't only a player

The obvious design, starting the server without `NetworkLifecycle.StartServer`'s loopback client
(`Components/Networking/NetworkLifecycle.cs:125-160`, client at `:153`), breaks the host itself.
That client is how the host **applies relayed state to its own world** and **sends its own
simulation**:

| Breaks without the loopback client | Where |
|---|---|
| **Loco state never reaches clients.** The host subscribes to every sim port, not only controls, and sends them through `Client.SendPorts`. The server takes the host's own packet and broadcasts the commit. The same route carries fuses, handbrake and brake-cylinder release. Most calls are `Client.` without `?.`, so they throw. | `Components/Networking/Train/NetworkedTrainCar.cs:514`, `:1681`, `:1607`, `:1714`, `:1754`; `Networking/Managers/Server/NetworkServer.cs:2353`, `:2444-2451` |
| **Client junction and turntable changes never reach the host's world.** The server relays them to every peer except the sender, and the host's own client is what applies them. Host trains would run the old route. | `NetworkServer.cs:2140-2148`; `Networking/Managers/Client/NetworkClient.cs:986-998` |
| **Time skips from client sleep or fast travel don't happen on the host.** They are applied by the host's client, and the 30 s weather sync then pulls clients back. | `NetworkServer.cs:2050-2059`, `:2095-2096`; `NetworkClient.cs:922-925`; `Patches/World/TimeAdvancePatch.cs:33` |
| **Host-side item changes aren't committed.** Interest, wake and job code look up `hostPlayer` by `Server.SelfId` and need it. | `Components/Networking/World/NetworkedItemManager.ServerInterest.cs:219-243`, `:291-294`; `Patches/Jobs/JobValidatorPatch.cs:72-79`; `NetworkedTrainItemWakeManager.cs:188-218` |
| **No remote avatars on the host.** `ClientPlayerManager` creates `NetworkedPlayer`s from relayed packets. Server logic relies on them: `HasPlayers` for delete and work-train occupancy, held items attached to avatar hands, plugs following a hand. | `Networking/Managers/Client/ClientPlayerManager.cs:34-65`; `NetworkedTrainCar.cs:265`; `NetworkServer.cs:2603`, `:2852`; `NetworkedItem.StateApplication.cs:45-103`; `NetworkedPluggableObject.cs:584-625` |
| **The host's loading screen never comes down.** `DisplayLoadingInfoPatch` unhooks it, and only `RemoveLoadingScreen` to the host's own client removes it. | `Patches/World/DisplayLoadingInfoPatch.cs:8-11`; `NetworkClient.cs:889-920` |
| **Station loco spawning never starts.** The spawner waits for `Client != null`. | `Patches/World/StationLocoSpawnerPatch.cs:30-31` |
| MPAPI NetId lookups aren't initialised (only the client's `Start()` calls `CheckInitialization`), and `RegisterPaintTheme` refuses. | `Networking/Managers/NetworkManager.cs:65-69`; `API/APIProvider.cs:68`, `:85` |

`IsHost()` alone is used about 300 times. Removing the client is a rewrite. **Keep it, and make it a
service identity**: the host's `ServerPlayer` exists for transport and `excludeSelf`, but isn't a
player. The earlier report calls this option A. The rest of this section is what that identity must
stop doing.

### Assumptions to change

| Where | Assumes | A no-player host needs |
|---|---|---|
| `NetworkServer.cs:92-105` | `SelfPeer`/`SelfId` come from the loopback client | Keep, plus an explicit `IsServiceHost` flag on that `ServerPlayer` |
| `NetworkServer.cs:1612` | `MaxPlayers` counts the host | Count remote players only |
| `NetworkServer.cs:1764-1769`, `:1957-1983` | The host's player completes loading, is announced (`PlayerJoined`, "joined the game" chat, `PlayerReady`) | Complete it silently: no announcement, no avatar, not in player lists |
| `Utils/DvExtensions.cs:122-137` (`AnyPlayerSqrMag`) | Every `ServerPlayer` is a player, the host included | Skip the service identity. Otherwise the parked body keeps its own station's jobs, spawners and registers alive (users: `StationJobGenerationRangePatch.cs`, `StationLocoSpawnerPatch.cs:41`, `PlayerDistanceGameObjectsDisablerPatch.cs:128`, `UnusedTrainCarDeleterPatch.cs:46`, `CashRegisterBasePatch.cs:128`) |
| Item interest, Lost and Found, cold containers, culling (`NetworkedItemManager.ServerInterest.cs:68`, `:113`, `:255`; `NetworkedLostAndFoundManager.cs:326`, `:512`, `:567`; `NetworkedColdContainerManager.cs:405`; `CullingManager.cs:85`) | Some skip `SelfPeer`, some don't | One `GameplayPlayers` view used everywhere |
| `Components/Networking/Player/LocalPlayerTrackerBase.cs:170`, `Patches/Player/CustomFirstPersonControllerPatch.cs:15-23` | The host's body sends its position every tick | Don't attach the tracker to the parked body |
| `WorldItems/NetworkedItemSpatialManager.cs:243`, `:537-541`, `:620-628`, `:660-663` | The host's player can simulate a released item and is the fallback simulator | The host can still simulate under the service identity, but only where it has the world loaded (section 2) |
| `NetworkServer.cs:2406`, `:2533`; `NetworkedTrainCar.cs:1072` | The host's own control and topology commands skip range checks | Fine: only the host's own simulation goes through them |
| `Patches/Train/CarVisitCheckerPatch.cs:16-17`, `UnusedTrainCarDeleterPatch.cs:70-73`, `SimulatedCarDebtTrackerPatch.cs:26`, `QuickTutorialInitiatorPatch.cs:34`, `FastTravelUIControllerPatch.cs:14`, `World/BedSleepingControllerPatch.cs:24-27`, `World/TimeAdvancePatch.cs:28` | `PlayerCount == 1` means "the host is alone, vanilla is right" | Use the remote player count. With one remote player these currently run vanilla against the parked body |
| `Utils/DvExtensions.cs:170-175` (`AllowPause`), `Patches/World/AppUtilPatch.cs:12-22` | Pausing is allowed when the host is alone | Decide the empty-server policy (section 4) instead |
| `Components/Networking/UI/ChatGUI.cs:486` | Chat UI is built on every host | Skip it. It also pauses the game, which crashed `-nographics` |

### The host player's own state

| Where | Assumes | A no-player host needs |
|---|---|---|
| `Components/SaveGame/NetworkedSaveGameManager.cs:73-76` | `Players.<guid>` is written for remote players; vanilla saves the host's own position and inventory (`Player_position`, `Storage_Inventory`) | Vanilla now saves the parked body: pin it at a fixed spot and keep its inventory empty |
| `WorldItems/WorldItemPersistenceSave.cs:115-123`; `Integrations/Inventory/InventoryIntegration.cs:95-98`; `NetworkedItem.Snapshots.cs:833-834` | The host's possessions live in vanilla's save and inventory | Nothing owned by the service identity |
| `Settings.cs:30`, `:212`; `NetworkServer.cs:1598` | One GUID per install. The stale-session check skips `SelfPeer`, so a client with the host's GUID is admitted | A distinct GUID for the service identity. The user's own client on the same install keeps the install GUID |
| Save opened for the first time as a dedicated host | The career's player was the host | A one-off migration of the old host body's inventory and position into `Players.<user guid>` (earlier report, 2.5) |
| `NetworkServer.cs:3225`, `ClientboundSaveGameDataPacket.cs:65-101` | Starter kits go to GUIDs with no saved data | Fine: the service identity never logs in through game data |

### Money, licences, debt

There is one wallet: the host's vanilla `Inventory.PlayerMoney`. Every spend comes from it
(`NetworkServer.cs:2610`, `:2653`, `:2743-2759`, `:2893`, `:3217-3301`), and changes are broadcast
from vanilla events (`NetworkedSaveGameManager.cs:25-66`). That works as well with no player.

**Paying fees and debt only works at the host's own career manager.** Clients are shown "host only"
(`Patches/SaveGame/CareerManagerFeesScreenPatch.cs:8-17`). With nobody at the host, debt can never
be cleared. Licence purchases are refused while there is debt (`NetworkServer.cs:2886-2891`), so
they're blocked for good. A dedicated host needs a serverbound fee-payment request.
`RerailControllerPatch.cs:19-25` takes the newbie rerail price from the host's own flag.

### Host-only UI and input

- **Hosting is interactive.** The Host button and pane (`Patches/MainMenu/LauncherControllerPatch.cs:29-67`,
  `Components/MainMenu/HostGamePane.cs:357-440`) fill `serverData` and press Run. The dvmp `host`
  probe (`DevTools/Dvmp/Probes/host.cs`) already does this from code; ship it behind command-line
  arguments.
- **Chat commands** (`Networking/Managers/Server/ChatManager.cs:16-104`): `/server`, `/whisper`,
  `/help`, `/kick` (host only, `:250-278`), `/crew` and `/log` (Debug only). There's no ban, no
  admin role and no console, and "host only" means `IsHost(sender)`, the loopback client (`:191-199`;
  the comments at `:193` and `:256` already note it). `ServerAPIProvider.SendServerChatMessage`
  only sends when `excludePlayer` is set (`API/ServerAPIProvider.cs:107-112`).
- **Other host-local powers:** key use (`Patches/World/KeyUsePatch.cs:10-13`), the weather editor
  and game parameters (broadcast by `GameParamsPatch`), and the host's mod settings (`Settings.cs:38-62`).
- **MPAPI:** `IsDedicatedServer` is hard-coded `false //feature not implemented`
  (`API/APIProvider.cs:27`). `ClientPlayerWrapper` never sets `isHost`, and `ClientAPIProvider.PlayerCount`
  adds one for the local player. Other mods need these defined.
- `Patches/Mods/RemoteDispatchPatch.cs:49-57` shows players only while a client runs; with the
  loopback client kept, that's fine.

## 4. Operations

### Steam

- **The game doesn't need Steam** (section 1). **The mod does.** The only transport is
  `SteamWorksTransport` (`Networking/Managers/NetworkManager.cs:41`; `LiteNetLibTransport` is
  commented out). Its "normal" UDP socket is Steam's networking-sockets library on DV's
  `SteamClient` (`Networking/TransportLayers/SteamworksTransport.cs:59-85`), and relay access starts
  only if `DVSteamworks.Success` (`Patches/Util/DVSteamworksPatch.cs:13-17`). `HostGamePane` refuses
  to host without Steam (`HostGamePane.cs:71-77`). There's no `SteamGameServer` path.
- So a dedicated host needs **a Steam client logged in to an account that owns DV**. On the user's
  PC that's their own account, shared with their client. The harness proves loopback joins work
  that way; relay joins by SteamID and lobby creation with two instances on one account are
  untested. On a separate machine it needs its own copy of DV.
- Ways out later: a Steam game-server login (DV has no dedicated-server app ID, so this needs
  checking), or reviving `LiteNetLibTransport` for direct UDP (no relay or NAT traversal, so port
  forwarding).
- Public listing creates a Steam lobby owned by the host's account (`Networking/Managers/Server/LobbyServerManager.cs:88-93`,
  `:188-217`), and its `CurrentPlayers` counts the host (`:159-183`).

### Memory and CPU per instance

| | RAM (working set) | Unity allocations | GPU memory | Source |
|---|---|---|---|---|
| Headless (`-batchmode -nographics`), one client joined | 4.0 GB | 1.95 GB | none | this investigation |
| Lean harness game (light host today) | 5.0-5.5 GB | 3.1-3.4 GB | 2.9-3.0 GB | [parallel-test-sessions.md](parallel-test-sessions.md#lean-harness-games), and the client in this test |
| Harness game before the texture cap | 5.2-5.3 GB | – | 4.7-5.0 GB | same |

About 3 CPU cores per game under gate load (same doc). Committed memory is about twice the
working set (9.2 GB for a lean game), so size the page file. The Mono heap was 1.0-1.2 GB.

**Frame rate and the tick.** `NetworkLifecycle.TICK_RATE` is 24. The harness background cap is 20
(`DevTools/DvmpAgent/HarnessMode.cs:26`), and the headless test ran at about 20 fps, so ticks and
polls ran at 20 a second. A server needs at least 30. Train speed is scaled by the host's measured
simulation rate (`NetworkTrainsetWatcher`), so a starved host slows the whole world for everyone.

### Saving

- **Vanilla autosave** runs every `AutosaveInterval` minutes of scaled time (default 3,
  `DV.Preferences/Preferences.cs:79-80`), only with unsaved progress (`SaveGameManager.cs:214-232`).
  `disableAutosave` starts true and `TutorialEnabler` clears it once the tutorials are done
  (`TutorialEnabler.cs:41-48`), so a finished career autosaves. Nothing saves on quit unless the
  save is single-save (hardcore) mode (`SaveGameManager.cs:152-193`).
- **The mod adds no disk saves.** `UpdateInternalData` on join and leave (`NetworkServer.cs:547-548`,
  `ClientboundSaveGameDataPacket.cs:44-45`) only updates the in-memory save. On disk, the
  `Multiplayer` section is written only when vanilla saves.
- **Saving needs the parked body**: `SaveAllowed` requires `PlayerTransform` and `Inventory`
  (`SaveGameManager.cs:200-212`). Under `-nographics` it also needs the thumbnail stub.
- **A server needs:** a save on a timer it controls (not the user's preference), a save on a stop
  command and on window close, rolling backups (LocoMP's `Autosaver` and backup and restore
  commands are the model), and a lock so the user can't open the same save in single player while
  the server runs.

### Admin

Today's admin is the host's keyboard plus `/kick` and `/server` for `IsHost(sender)` (section 3).
A dedicated host needs:

- **Owner or admin identities**: GUIDs in the host's settings. On one PC, the install's own GUID on a
  loopback connection is the obvious owner.
- **Commands**: save, stop (save then quit), kick, ban (there is none), say, pay fees, set difficulty
  and game parameters, list players and status.
- **A local console.** Stdin doesn't exist in a Unity player. The options are chat commands from an
  admin client, or a loopback HTTP endpoint like the debug firehose ([observability.md](../observability.md),
  which is off by default in Release).

### Restarts and health

There's nothing today. Unity's crash handler writes to `%TEMP%\Altfuture\Derail Valley\Crashes`
and exits. A supervisor (a scheduled task or a small watchdog) should relaunch the game with the
auto-host arguments. It can also catch a hang from a heartbeat file the mod updates every few
seconds, with tick, frame time, player count and retained heap, as LocoMP's `SoakReporter` does.
That heartbeat is also the "is the server healthy" signal a remote user can't otherwise see. A
daily restart is cheap insurance against slow growth like
[#78](https://github.com/teflocarbon/dv-multiplayer/issues/78).

Log growth matters for a long-running host: under `-nographics`, 1 MB a minute from one decal
renderer. The windowed game logs far less, but the mod's own debug log should be checked over a day.

### Time and pause when nobody is online

- Nothing pauses by itself. With only the host connected, `AllowPause` is true
  (`Utils/DvExtensions.cs:170-175`), but nobody presses pause. The world keeps running:
  - time and weather advance;
  - jobs age and expire;
  - an unattended train keeps rolling;
  - autosave keeps writing, because progress is always "unsaved".
- The server already unpauses on every login and at `ReadyForWorldState` (`NetworkServer.cs:1660-1662`,
  `:1757-1759`). So **pause when the last remote player leaves** is half built: call
  `AppUtil.PauseGame` from `OnPeerDisconnected` when no remote players are left. Pausing sets
  `Time.timeScale = 0` (`DV/AppUtil.cs:152-166`) and the network tick keeps running on real time
  (`NetworkLifecycle.cs:180-239`), so joins still work. Pausing also stashes a screenshot
  (`SaveGameManager.cs:186`), which is fine with graphics and must be stubbed without.
- Make the policy a setting: pause when empty (the default), or keep running.

## Recommended plan

Each stage is useful on its own. The earlier report's stage 0 to 2 still stand; this version adds
what the experiments showed.

| Stage | Work | Size |
|---|---|---|
| **0. Harness light host** (DevTools only) | **Built.** `dvmp up --host-only` in a named session, a distinct host GUID, a background cap of 30, and `dvmp perf`. Still to do: run it with the user joining from VR, sampling `dvmp perf` every 30 s over a long session, which is the split test for #78. | Done; the VR run remains |
| **1. Service-identity host** (shipped, behind `-mp-dedicated`) | **Built** (see [Implementation status](#implementation-status-2026-10-17)). Keep the loopback client and flag its `ServerPlayer`. Add a `GameplayPlayers` view and use it in `AnyPlayerSqrMag`, interest, Lost and Found, culling, `MaxPlayers`, `PlayerJoined` and player lists. Replace the `PlayerCount == 1` heuristics. Park the vanilla body: kinematic, no tracker, no chat UI. Use a distinct GUID. Set MPAPI's `IsDedicatedServer`. Port HarnessMode's non-persisting reductions, with a cap of at least 30. Auto-host from command-line arguments (port of `host.cs`). | Medium |
| **2. Unattended operation** | A timed save, save on stop, backups and a save lock. Owner and admin identities with chat commands (save, stop, kick, ban, pay fees, game parameters). A serverbound fee and debt payment. Pause when empty. A heartbeat file and a watchdog. Migrate the old host body's state to the user's GUID. Update `docs/architecture.md` ("the host is also a player"). | Medium |
| **3. Anchor the world on clients** | **(a) and (b) built** (see [Implementation status](#implementation-status-2026-10-17)). (a) Add remote players to `DistanceLodSystem`'s reference positions (one transpiler). (b) Move the parked body, or DV's external camera, to the single connected client, or to the players' centroid. That brings streaming, terrain and the origin to where play is. (c) For several players far apart: heightmap ground proxies first; multi-anchor `TerrainGrid` and streamers only if measurement shows platforms and buildings matter. Also fixes the freeze that exists today when the host's player is far from a client. | (a, b) small to medium, (c) large |
| **4. GPU-less host** (optional) | The five render stubs and three silenced systems from section 1, gated on `-nographics`. Tested against each mod in use. | Small, plus per-mod testing |

**Tests for each stage** go in `DevTools/Scenarios`: the existing gate with the host as a service
identity, plus a far-derail scenario that derails a car next to a client 4 km from the host's
anchor and checks it settles on the host as well as on the client. That scenario fails today.

## Biggest risks

1. **One world anchor for players spread across the map.** DV's streamers, terrain grid and
   origin each follow one tracker. Off the anchor, derailed cars freeze and there's no ground for
   items. Following one client is easy; real multi-anchor support means patching `WorldStreamer`
   and `TerrainGrid` internals, and grows memory with every anchor. It's also the most likely thing
   to break on a game update.
2. **The host-is-a-player assumption is everywhere.** About 300 `IsHost()` calls, plus `SelfId`,
   `PlayerCount == 1` heuristics and the loopback client carrying the host's own simulation.
   Keeping the client as a service identity contains this, but each missed spot is a quiet bug: a
   station kept alive by the parked body, a vanilla path running against it, a slot taken.
3. **Identity and personal state.** Two games on one install share `Settings.Guid`, and the login
   check lets the duplicate in. The old host player's inventory and position need migrating to the
   user, or they're stranded on the parked body.
4. **Steam.** Hosting needs a logged-in Steam account that owns DV. Two instances on one account
   work for loopback; relay and lobby behaviour is unknown.
5. **Resources on one PC.** A second game costs 4-5.5 GB of RAM and about 3 cores. If the host is
   starved, everyone's trains slow down, and the user can't see why without a health signal.
6. **`-nographics` crashes natively** on any render path that wasn't stubbed. Only one was hit in
   this test, and others may exist in mods or rarer game paths. That's why it's last and optional.
7. **Precision with one origin.** Pinned at the map centre, play stays within about 11.6 km, where
   float32 resolution is about 1 mm. That's borderline for the 2 mm stationary threshold on cars
   with loaded interiors.

## Prototype: what was run

The experiment changed nothing in the mod or the agent. On the headless game, a `dvmp eval`
probe installed these Harmony prefixes at the main menu:

- `SaveGameManager.CaptureScreenshot` and `ScreenshotTaker.TakeScreenshotWithoutUI`: skipped.
- `SaveGameManager.GetThumbnail`: returns a blank 16x9 texture.
- `RenderTextureSystem.RenderNextJob`: hands each queued job a blank `RenderTexture` without
  rendering.
- `BlackoutScreen.Blackout`: runs its action straight away.
- `VLB.DynamicOcclusionDepthBuffer.ProcessOcclusionInternal`: skipped.

The game was launched by hand with the harness arguments plus `-batchmode -nographics` in a dvmp
session named `dedi` on port 7899. The `host` probe loaded "DVMP Test Career 2", and
`dvmp up --session dedi` joined a client. One manual save was written to that test session. The
DE2 used for the tests was deleted afterwards, and autosave was turned off so it was never saved.
