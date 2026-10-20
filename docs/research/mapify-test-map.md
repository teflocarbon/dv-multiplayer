# A Mapify test map for the physics scenarios

Status: **dropped**, 2026-10-20. The user's call: Mapify loads the vanilla scenes before the map,
so a test map wouldn't save load time, and that was the reason to want one. The scenario
framework stays map-agnostic on the real world. The track-graph check in §3 item 3 is still worth
building on its own. The rest of this doc is kept as the reasoning.

Research, 2026-10-20. No game was launched and nothing was installed. Mapify Continued
was read at [`t0stiman/dv-mapify@efeb14d`](https://github.com/t0stiman/dv-mapify/tree/efeb14d)
(version 2026.09.08, the current Nexus release); the mod was read on `split/client-server`.

**Question.** Could a small custom map, loaded through Mapify Continued, host the physics-only
regression scenarios (`host-stall`, `couple-impact`, `cab-controls`, `uncouple-moving`) alongside
the real world?

**Answer.** Not now, and not as a harness change on its own. Today a client on a Mapify map
would load the vanilla world, because the join never tells it which map the host is on. Even with
that fixed, the mod identifies tracks, junctions and turntables by the order they wake up in each
game, and Mapify changes that order in ways nobody has checked. The test map's payoff is small:
the physics scenarios already find their own clear track, and Mapify still loads the vanilla
scenes to copy assets from them, so loads won't be much faster. Recommendation: park the trial.
Do the two mod changes below because they're worth having anyway (they catch real desyncs on the
vanilla map too). Run a one-pair trial in its own install only if the user still wants it after
that. See [Recommendation](#5-recommendation).

In this doc, `M:` marks a file in Mapify (`Mapify/` unless the path says `MapifyEditor/`) and
`mod:` a file in this repository.

## 1. How Mapify replaces the world

Mapify keeps the vanilla game: the same `WorldStreamingInit`, `WorldMover`, `SaveGameManager`,
car spawner, job generator and stations. What it changes is what those load, and it builds the
game objects the vanilla scenes would have held.

**Which map is picked.** The map is a `"mapify"` key in the session's save data, holding
`BasicMapInfo { name, version, mapifyVersion }` (`M:Utils/Extensions.cs:22`, `:352-378`,
`MapifyEditor/BasicMapInfo.cs:7-28`). No key means the default map. It's set once, from a selector
in the new-session screen (`M:Patches/ContinueLoadNewControllerSinglePatch.cs:60-66`,
`M:Patches/MainMenuControllerPatch.cs:24-32`). A session's map can't be changed later. Starting a
session whose map isn't installed is refused (`M:Patches/AScenarioProviderPatch.cs:14-21`,
`M:Patches/LauncherControllerPatch.cs:53-76`). Maps are found in the folders of enabled UMM mods
that hold a map info file (`M:Map/Maps.cs:67-105`). Each map is its own UMM mod.

**World load (`WorldStreamingInit`).** A prefix on `WorldStreamingInit.Awake` calls
`SaveGameManager.FindStartGameData()`, reads the map from `SaveGameManager.data` and, for a
custom map, skips vanilla `Awake`. It then runs its own coroutine and starts vanilla
`LoadingRoutine` only when that's done (`M:Patches/WorldStreamingInitPatch.cs:24-52`).
`MapLifeCycle.LoadMap` (`M:Map/MapLifeCycle.cs:36-192`):

1. loads the map's asset bundles;
2. **turns off every `DV.*` and global-namespace `MonoBehaviour` in Assembly-CSharp**: a Harmony
   prefix returning `false` on `Awake`, `OnEnable`, `Start`, `Update`, `OnDestroy` and the rest
   (`:145`, `M:Patches/MonoBehaviourDisablerPatch.cs:30-77`);
3. **loads every vanilla Far scenery scene plus the vanilla Railway and GameContent scenes**
   additively, only to copy assets out of them (switch prefabs, station offices, shops, pit stops,
   turntables, buffer stops), then destroys their objects and unloads the scenes (`:151-168`,
   `M:AssetCopiers/AssetCopier.cs:37-75`);
4. destroys the vanilla `Streamer` components under `WorldStreamingInit` (`:156-157`) and points
   `terrainsScenePath`, `railwayScenePath` and `gameContentScenePath` at the map's scenes
   (`:163-165`);
5. turns the disabled behaviours back on (`:179`), writes the map's size, water level and spawn
   into `LevelInfo` with `worldOffset = 0` (`:194-207`), and adds a new `Streamer` for the map's
   own scenery tiles (`:182`, `:264-293`);
6. clears `StationController.allStations` and `CashRegisterBase.allCashRegisters` (`:339-343`).

Vanilla `LoadingRoutine` then loads the map's three scenes. A `sceneLoaded` hook builds each one
(`:295-337`).

**Streaming.** `WorldStreamingInit.IsSceneAndTerrainRegionLoaded` and `IsSceneAndTerrainCellLoaded`
are replaced on custom maps by a check over every `Streamer` plus the `TerrainGrid`
(`M:Patches/WorldStreamingInitPatch.cs:82-119`). Vegetation Studio is swapped for an inert object
(`:36-43`). The Near/Far split of vanilla is gone: there's one streamer, with the map's ring size.

**Terrain.** A new `TerrainGrid` under the origin-shift parent, configured from the map
(`M:SceneInitializers/Terrain/TerrainGridSetup.cs:12-37`). A transpiler swaps a shader name in
`TerrainGrid.Awake` (`M:Patches/TerrainGridPatch.cs:12-24`).

**Track and graph building.** The map's `Track` components get a `RailTrack` added at runtime.
Each track is renamed to `[Y]_[station]_[yard-NN-type]` (or the road name), and the track is woken
by setting it active in the order `FindObjectsOfType<Track>()` returns
(`M:SceneInitializers/Railway/TrackSetup.cs:19-35`, `:97-113`,
`MapifyEditor/Trackage/Track.cs:90-93`). Switches are vanilla switch prefabs instantiated and
re-wired (`TrackSetup.cs:115-292`). Connection snapping uses the map's snap range instead of 5 m.
`WarnIfConnectionsAreDisjoint` is skipped on custom maps (`M:Patches/RailTrackPatch.cs:10-74`).
Everything is parented to `WorldMover.OriginShiftParent`
(`M:SceneInitializers/Railway/RailwaySceneInitializer.cs:15-19`).

**Stations and jobs.** A `StationController` is added to each map `Station` while it's inactive
and filled in from the map: station info, storage/transfer track names, booklet surface,
generation ranges, cargo rules, warehouse machines, teleport anchor and loco spawners. The stations
are then switched on, and `LogicController` after them
(`M:SceneInitializers/GameContent/StationSetup.cs:15-131`). Job generation is still vanilla.
Mapify changes the 30-attempt cap to the station's job capacity, by a transpiler on
`GenerateProceduralJobsCoro` (`M:Patches/StationProceduralJobsControllerPatch.cs:10-37`), and
clamps the car counts (`M:Patches/StationProceduralJobGeneratorPatch.cs:5-12`). Shops and the
global shop controller are set up the same way (`M:SceneInitializers/GameContent/GameSceneSetup.cs:9-57`,
`StoreSetup.cs`).

**Saves.** Vanilla. Mapify only adds the `"mapify"` key and turns autosave back on after load
(`M:Map/MapLifeCycle.cs:191`). Players moved to another map are sent to the nearest station
(`M:Patches/StationFastTravelDestination_Patch.cs:12-31`).

**Floating origin.** Vanilla `WorldMover`. Everything Mapify creates goes under
`WorldMover.OriginShiftParent` (`M:AssetCopiers/AssetCopier.cs:19-26`,
`M:SceneInitializers/GameContent/GameContentSceneInitializer.cs:12-17`), and `LevelInfo.worldOffset`
is zero.

## 2. Where that collides with the mod

Ordered by how likely each one is to break a scenario.

1. **The client loads the vanilla world.** A client never loads the host's save. It builds one
   from the join packet with `SaveGameManager.MakeEmptySave()`
   (`mod:Multiplayer/Client/Session/StartGameData_ServerSave.cs:48`), and
   `ClientboundSaveGameDataPacket` carries no map
   (`mod:Multiplayer/Shared/Protocol/Packets/Clientbound/ClientboundSaveGameDataPacket.cs:20-48`).
   Mapify reads the map from that data (`M:Patches/WorldStreamingInitPatch.cs:26-30`), finds no
   `"mapify"` key and loads the default map. The host is on the test map and the client in Derail
   Valley. This alone blocks any trial.

2. **Track, junction and turntable IDs come from wake-up order.** `NetworkedRailTrack`,
   `NetworkedJunction` and `NetworkedTurntable` are added in `RailTrack.Awake`, `Junction.Awake`
   and `TurntableController.Awake` (`mod:Multiplayer/Shared/World/RailTrackPatch.cs:5-12`,
   `JunctionPatch.cs:6-12`, `TurntableControllerPatch.cs:6-12`). Their NetIds aren't
   host-assigned (`IsIdServerAuthoritative => false`, `NetworkedRailTrack.cs:49`). Each game takes
   the next unused ID from its own allocator as each one wakes
   (`mod:Multiplayer/Shared/Session/IdMonoBehaviour.cs:31-38`,
   `Multiplayer.Core/Identity/NetIdAllocator.cs:27-43`). Every bogie position, spawn, rerail and
   move-to-track carries those IDs (`mod:Multiplayer/Client/Trains/NetworkedBogie.cs:90`,
   `NetworkedCarSpawner.cs:224-236`, `Server/Session/NetworkServer.Trains.cs:1068`). The join's
   junction and turntable state is a plain array ordered by NetId
   (`mod:Multiplayer/Server/Session/NetworkServer.cs:1027-1028`, `NetworkedJunction.cs:12`). On
   the vanilla map this works because both games load the same scene the same way. Under Mapify:
   - the vanilla Railway scene is loaded first to copy switches from. Mapify's disabler skips
     `RailTrack.Awake`, but **the mod's prefixes still run**, because Harmony skips the remaining
     prefixes only when they have side effects (a return value or `ref` argument) and these
     return `void` ([Harmony: prefix](https://harmony.pardeike.net/articles/patching-prefix.html)).
     So every vanilla track and junction takes an ID and gives it back when destroyed. The
     allocator prefers IDs it has never issued, so the map's tracks start after however many
     vanilla ones woke first;
   - the map's tracks are woken in `FindObjectsOfType` order, which Unity doesn't guarantee,
     after the custom switches, which are instantiated from prefabs
     (`M:SceneInitializers/Railway/TrackSetup.cs:19-35`).

   Both are probably the same in two copies of one build, but nothing checks it. If the order
   differs, the client puts cars on the wrong track and throws the wrong switches, and nothing
   reports it. This is the risk the trial would have to rule out, and a check for it is worth
   having on the vanilla map too (§3).

3. **Mod hooks fire on the vanilla objects Mapify loads and throws away.** While Mapify's disabler
   is on, vanilla `Awake`/`Start` bodies are skipped. Mod postfixes always run, and so do `void`
   prefixes. The mod has about 40 patches on vanilla `Awake`, `Start` or `OnEnable`, among them
   `StationController.Awake` (adds `NetworkedStationController` and `ServerStationController`,
   `mod:Multiplayer/Server/Jobs/StationControllerPatch.cs:10-16`), `ItemBase.Awake`/`OnEnable`,
   `JobValidator.Start`, `WarehouseMachineController.Awake`, `PluggableObject.Awake`,
   `CashRegisterBase.OnEnable` and `StationLocoSpawner.Start`. They would run on
   the vanilla GameContent scene's half-built objects before those are destroyed. Expect
   exceptions in the log, NetIds taken and given back, and possibly registry entries (items,
   stations, cash registers) left pointing at destroyed objects. Not proven: it's the first thing
   to read in a trial's logs.

4. **Server scenery coverage turns itself off.** `ServerSceneryCoverage` keeps Far tiles loaded
   around remote players and derailed cars by finding `WorldStreamingInit.farScenesStreamer`
   (`mod:Multiplayer/Server/Coverage/ServerSceneryCoverage.cs:56`, `:61-66`). Mapify destroys that
   streamer and doesn't set the field to its own (`M:Map/MapLifeCycle.cs:156-157`, `:264-293`),
   so coverage finds nothing and stays idle. That matters on a dedicated host and for the
   far-derail scenarios, not for physics near the host. Terrain coverage uses
   `TerrainGrid.Instance`, which Mapify does create, and the mod's `IsInLoadedRegion`/`IsInTargetRegion`
   patches (`mod:Multiplayer/Server/Coverage/TerrainGridPatch.cs:69-95`) are still reached through
   Mapify's replacement streaming check. It assumes vanilla's 512 m cells and 5×5 box
   (`ServerTerrainCoverage.cs:19`), which a map can change.

5. **The mod list makes both games carry Mapify and the map, but not use the same map.** Mapify and
   any map mod have no `MultiplayerCompatibility` entry, so they count as required on both sides
   at the same version (`mod:Multiplayer/Shared/Api/ModCompatibilityManager.cs:107-139`, `:180-182`).
   Nothing says which map the host's session is on. A client with the map installed but a
   default-map save, or a map mod that ships two maps, gets through login.

Checked and not in conflict:

- **Floating origin and positions.** The mod uses `WorldMover.currentMove` and `OriginShiftParent`
  throughout, and Mapify keeps both.
- **Stations.** A station's NetId is the FNV hash of its ID
  (`mod:Multiplayer/Shared/Jobs/NetworkedStationController.cs:147-150`).
- **Jobs.** Jobs name tracks by GameObject name (`mod:Multiplayer/Shared/Protocol/Data/Jobs/TaskNetworkData.cs:305-325`),
  and Mapify's names are stable.
- **Job generation.** Mapify's transpiler is on `GenerateProceduralJobsCoro` and the mod's patch
  is on `TryToGenerateJobs` (`mod:Multiplayer/Server/Jobs/StationProceduralJobsControllerPatch.cs:6`),
  so they don't touch.
- **`FindStartGameData`.** Mapify calls it a second time. The mod's postfix there returns early
  once the server or client is running (`mod:Multiplayer/Menus/MainMenu/SaveGameManagerStartServerPatch.cs:16-24`).
- **`DisplayLoadingInfo.Start` postfixes.** Mapify's raises its `CanLoad` flag, and the mod's
  unhooks `LoadingFinished` (`mod:Multiplayer/Client/Session/DisplayLoadingInfoPatch.cs:5-11`).
  Neither undoes the other.
- **`LauncherController.OnRunClicked` prefixes.** Both refuse a run: Mapify's for a missing map,
  the mod's for a save that a dedicated host on this PC has open
  (`mod:Multiplayer/Menus/MainMenu/LauncherControllerSaveLockPatch.cs:12-31`). Whichever
  refuses first wins, and that's fine.

Not checked (the session can't launch the game): what Mapify does on a dedicated host with no
rendering, and whether `DedicatedHostAnchor` finds Mapify's streamer.

## 3. What multiplayer needs

Both games must load the same map at the same version, and must agree on every track, junction
and turntable ID. The minimum mod work:

1. **Carry the map in the join.** The host reads its session's `"mapify"` object from
   `SaveGameManager.data`. It can read the raw JSON key, so the mod needs no Mapify reference. The
   host puts it in `ClientboundSaveGameDataPacket`, and `StartGameData_ServerSave.SetFromPacket`
   writes it into the save it builds, so the client's Mapify loads the same map. With no key the
   client loads the default map, as today.
2. **Check the map at login.** Send the host's map name and version in the login response. Before
   loading, the client refuses with a clear reason if that map isn't installed at that version.
   The mod list already makes both games carry Mapify and the map mod (§2.5); this adds which map.
3. **Check the track graph after load.** Each game hashes its NetId → GameObject-name tables for
   tracks, junctions and turntables once `WorldStreamingInit.LoadingFinished` fires. The client
   sends its hash with `ReadyForWorldState`, and the host disconnects it with a reason on a
   mismatch, before any train baseline is sent. This is the safety net for collision 2. It also
   catches the same failure on the vanilla map (a mod that adds or removes track, an install with
   a different scene), where today it shows up only as cars on the wrong track.

Items 1 and 2 are a packet field and a login check. Item 3 is a hash and one more packet field.
All three are protocol changes, so the fingerprint changes too. They need a design note in
`docs/world-sync.md` and the usual listen-plus-dedicated test.

## 4. Payoff

What a minimal test map would give:

- **Level, straight, known track.** Real, but small. The physics scenarios already spawn near the
  client and search for track (`couple-impact/setup.cs`, `uncouple-moving/setup.cs`), and
  `host-stall` measures the clear track ahead before pushing (`host-stall/setup.cs`, `ROOM` in
  `run.sh:20`). Grades are a known nuisance: "unbraked cars roll away on the slightest grade"
  (`host-stall/run.sh:18`), and the same in `cab-controls/run.sh:206`. The scenarios work around
  them with handbrakes, and none of the 2026-10-20 harness failures came from the map.
- **A station at a known distance.** Only on Mapify's station setup
  (`M:SceneInitializers/GameContent/StationSetup.cs`), not the vanilla stations players use. A
  station scenario passing there says little about the real game.
- **Faster loads.** Doubtful. Mapify loads every vanilla Far scene plus the vanilla Railway and
  GameContent scenes to copy assets before it loads the map (`M:Map/MapLifeCycle.cs:151-168`), so
  a load does the vanilla work and then the map's. It could be slower; only a measurement would
  tell.
- **Less memory per pair.** Likely in steady state: a small terrain, no vegetation
  (`WorldStreamingInitPatch.cs:36-43`) and few scenery tiles. The peak during load includes all the
  vanilla Far scenes at once. Unmeasured: `dvmp perf` on both maps would answer it.

Which scenarios in `DevTools/Scenarios` could move:

| Could move (cars, couplers, brakes, controls near the players) | Can't move (they test the real world) |
|---|---|
| `host-stall`, `couple-impact`, `uncouple-moving` | `far-derail`, `far-derail-embankment`, `client-fall-rescue`: terrain, embankments, streaming coverage |
| `cab-controls`: once its fixed `TeleportToGround(7190, 6880)` spots (`run.sh:107`, `:157`, `:198`, `:238`) come from a map anchor | `station-load`, `jobs`, `job-reroll`, `overview-pickup`, `host-job-train-coupled`: vanilla stations and job generation |
| `fps-drop`, `derail-drag`, `client-derail`, `coupler-desync`, `rerail`, `manual-uncouple`, `host-chain-screw` | `shop`, `host-register-empty-basket`, `pit-stop`: shops and service stations |
| `car-delete`, `car-pool-reuse`, `car-fire`, `explosion`, `compressor-signal`, `stoker-coal`, `sandbox-cargo`, `train-items` | `save-reload`, `inventory-persist`, `host-player-persist`, `rejoin`, `borrowed-disconnect`, `dedicated-admin`: saves and sessions |
| `pileup`, only with a map yard of K parallel tracks standing in for Steel Mill's B yard | `lost-and-found`, `recall`, `containers`, `owned-items`, `player-soak`, `hazmat`: station props, travel, ground tiles |

So about half the scenarios could run on a test map. The ones that can't are the ones that need
the most game slots and time (saves, jobs, streaming), so the test map wouldn't shorten a full gate
much. Every moved scenario would also stop testing the vanilla track it runs on today.

**Ready-made maps.** Nexus has two test maps, both built for the original Mapify, before Mapify
Continued. Whether either still loads in 2026.09.08 is unknown: `MapLifeCycle` keeps fallbacks
for maps exported with older versions (`:71`, `:94`, `:123`).

- **Wiz's Test Track** ([mods/801](https://www.nexusmods.com/derailvalley/mods/801), 0.1.0,
  2023-09-13): a double-track loop for about 160 km/h, 3 stations, and grade ramps from 0.5 % to
  5.5 % (each with 1 km of grade and 200 m of flat). It's the closest to what the physics
  scenarios want.
- **Tostiman's test map** ([mods/798](https://www.nexusmods.com/derailvalley/mods/798), 0.3.0,
  2024-11-27): a top-speed track, 2 stations with jobs, ramps in various grades, and service
  centres.

The "Rolling material testing map" (mods/832) has been hidden on Nexus. A map of our own would
need Unity 2019.4.40f1 and the Mapify editor package
([project setup](https://dv-mapify.readthedocs.io)), which is days of work before the first test.

## 5. Recommendation

**Park the test-map trial, and keep the scenario framework map-agnostic** as planned. The payoff
(§4) doesn't cover the cost: two protocol changes before a client can even join, an unproven
track-ID order, mod hooks firing on Mapify's throwaway vanilla objects, and an extra mod in the
harness's games.

**Do the cheap part anyway.** The track-graph check (§3 item 3) is worth building on its own: it
turns a silent desync on the vanilla map into a refused join with a reason. It's a separate,
small mod change. The map-in-join and login check (§3 items 1-2) belong with it only if Mapify
support is wanted for players. Without them, a player who hosts a Mapify session today gets
clients in the vanilla world and no error.

**If the user still wants the trial afterwards**, the concrete next step is one pair, once:

1. Land §3 items 1-3 on a branch off `split/client-server` (unit tests, then the usual
   listen-plus-dedicated run on the vanilla map).
2. Make a second test install, for example `%LOCALAPPDATA%\dvmp\mapify-install`, with
   `DevTools/make-test-install.ps1 -TestDir ...`. Put Mapify Continued and the test map in its
   `Mods` as real folders, never in the Steam install, so the user's own games and their AI-mod
   server don't load them. A named session (`--session mapify`) keeps its saves apart.
3. Create a sandbox session on the map once, by hand in that install's menu (the map selector is
   UI-only), and save it as "DVMP Mapify Test".
4. With one game slot from the coordinator: `dvmp up --session mapify --save "DVMP Mapify Test"`.
   Read both logs for collision 3, compare the track-graph hashes, then run `host-stall`,
   `couple-impact`, `uncouple-moving` and `cab-controls` (after giving it a map anchor). Compare
   load time and `dvmp perf` memory with the same pair on the vanilla map.

What that needs from the user:

- the downloads: Mapify Continued 2026.09.08 ([mods/1382](https://www.nexusmods.com/derailvalley/mods/1382))
  and Wiz's Test Track ([mods/801](https://www.nexusmods.com/derailvalley/mods/801)). Nexus needs
  a signed-in browser, so the user downloads them;
- a check that the map still loads in single player on the current game build, before any
  multiplayer work;
- the go-ahead for the protocol changes in §3;
- one game slot (a single pair) for about an hour, booked through the coordinator.

The trial passes if both games build the same track graph and the four scenarios pass, and it pays
off only if loads or memory are clearly better than on the vanilla map. Otherwise drop the test
map for good and keep the check from §3.
