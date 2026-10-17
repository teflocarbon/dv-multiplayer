# Multi-anchor streaming: loading the world around several far-apart players

> Status: research (2026-10-17), nothing built. This is stage 3c of
> [dedicated-host.md](dedicated-host.md). Under [server-player-split.md](server-player-split.md),
> every host needs it, not only a dedicated one: a player host must keep its own player's area
> loaded and every remote player's too. Written before any code; no game was launched for it.

**Where the citations point:**

- `decomp3c/...` and `decomp/...` are `ilspycmd -p` decompiles of the game assemblies from
  `DerailValley_Data/Managed`. `decomp3c` covers `WorldStreamer`, `DV.WorldTools.Runtime.Common`,
  `DV.RailTrack` and `DV.Utils.ECS`; `decomp` covers `Assembly-CSharp`, `DV.TerrainSystem` and the
  rest. Line numbers refer to those decompiles of build 99, so regenerate them with
  `ilspycmd -p -o <dir> -r <Managed> <assembly>.dll` to follow along.
- `Multiplayer/...` and `docs/...` are this repository (branch `dev/dedicated-host` when written).
- `DerailValley_Data/...` is the game install
  (`C:\Program Files (x86)\Steam\steamapps\common\Derail Valley\DerailValley_Data`).

**How scene contents were read.** UnityPy wasn't available, so the research used small read-only
Node parsers (not in the repo). They read the Unity 2019.4 SerializedFile (v21) object table and
the UnityFS bundle format, then pulled GameObject names, layers, components, transforms and
collider sizes out of the level files.

- Type trees are stripped, so a MonoBehaviour's fields were decoded by their declared order in the
  decompiled class.
- Every value quoted from a data file was checked for consistency: the names, sizes and pointer
  targets line up.

---

## 0. Answer in one page

- **What a derailed car or an item rests on is spread over four scene groups** (section 4):
  - **Ballast:** all of it is in the **Near** tiles: 6,934 segments, 285 km of track, 34,670 box
    colliders.
  - **Bridge decks:** in **both Near and Far** tiles.
  - **Station platforms, buildings, roads, industries and tunnel walls:** in **Far** tiles.
  - **Buffer stops:** in the always-loaded **railway** scene.
  - **Ground:** the **TerrainGrid** cells.

  So "terrain only" (or heightmap proxies) is **not enough**:
  - a car derailed on a viaduct falls to the valley;
  - in a tunnel it ends up under, or pushed out through, the terrain;
  - on open track it settles about 0.3-1 m lower than on a client, which has ballast colliders.

  The host is authoritative, so the client's car follows the host's into the ground.
- **One `Streamer` per collection, loading the union of tile boxes, is the right approach.** It
  needs two Harmony patches: replace `CheckPositionTiles`, and filter `SceneUnloading`.
  - **Several `Streamer` instances can't work.** Two of them over the same collection load
    duplicate scenes. The loaded scene registers with only the first streamer found by tag, which
    corrupts both streamers' counters and `sceneGo` references, and the origin-shift offsets of
    the orphaned copy (section 1.5).
- **`TerrainGrid` needs:**
  - a larger pool, set at `Awake`;
  - the region predicates (`IsInTargetRegion`, `GetCellsInRegion`, `GetActiveCellsOutsideRegion`)
    extended to all anchors;
  - a re-evaluation trigger in `Update`;
  - a per-cell `IsInLoadedRegion`.

  This is cheap. A cell is 512 m with a 513² heightmap: about 0.64 MB on disk and about 1.6 MB of
  RAM once it has a collider. That's about 40 MB per anchor.
- **What an extra anchor costs** (section 6):
  - about **40 MB** of terrain;
  - about **20-150 MB** of scene objects (the Far 5x5 window is 14 MB on disk at the median and
    50 MB at worst);
  - load bursts of a few seconds.

  That's roughly 1.5-4 % of a 4-5.5 GB host per anchor.
- **Origin:** DV shifts every 823 m (`moveRange`, read from the scene) around one tracker.
  - **Following one player:** another player can be up to 16.4 km away on one axis. float32
    spacing there is 1.95 mm, and a body slower than about 6 cm/s doesn't move at all from one
    60 Hz step to the next.
  - **Centre of the anchors' bounding box:** with shifts only past a threshold, that worst case is
    at most 0.98 mm (at most 8,192 m per axis) for any spread, and 0.49 mm while the anchors fit
    in 8 km. It's a small `WorldMover.ForceMove` prefix.
- **The alternatives:**
  - **Heightmap proxies** are dominated by loading the real 1 m terrain cells, which costs about
    the same, and they miss ballast, bridges and tunnels.
  - **Pointing the kinematic checks at "near any player"** without loading the world makes cars
    fall into the void, then get "rescued" onto the heightmap plus 10 m.
  - **Delegating derailment physics to clients** fights the train authority model, and isn't
    needed at these costs.
- **Do these first:**
  - Measure the "whole map scenery" option (near and far ranges of 15) on a dedicated host with
    the existing `-dvmp-near-range` / `-dvmp-far-range` harness flags. If it's cheap, the Streamer
    patch can be skipped entirely and only `TerrainGrid` needs multi-anchor work.
  - dvmp only supports one client per session, so a second-client or "virtual anchor" test
    facility is a prerequisite (section 7).

---

## 1. WorldStreamer internals

### 1.1 Configuration actually shipped (read from the data files)

**Build scenes** (`DerailValley_Data/globalgamemanagers` build settings, in build order):

| Index | Scene |
|---|---|
| 8-263 | `Assets/DV/World/Work/output/Near_/Near__x{0..15}_z{0..15}.unity` (256) |
| 264-519 | `.../Far_/Far__x{0..15}_z{0..15}.unity` (256) |
| 520 | `loading/railway_w3_LFS.unity` |
| 521 | `game_content_w3` |
| 522 | `terrains_w3` |

Each tile scene is its own build scene, loaded by name. There are no Addressables or bundles for
tiles.

**`SceneCollection`s** (`DerailValley_Data/sharedassets4.assets`, pathID 3199 is Far and 3211 is
Near; the fields are in `decomp3c/WorldStreamer/SceneCollection.cs:8-38`):

- Both have `xSize = zSize = 1024`, `ySize = 0`, x and z split, and limits 0..15. So the map is
  16 × 16 tiles of 1024 m (16,384 m, matching `decomp/Assembly-CSharp/LevelInfo.cs:20`).
- A tile's root sits at its absolute position: the root of `Far__x12_z10` is at (12288, 0, 10240).

**`Streamer`s** (`DerailValley_Data/level4`, which is `game_w3`; MonoBehaviours 7863 on the
GameObject `[far]` and 7865 on `[near]`):

| | `[far]` | `[near]` |
|---|---|---|
| `loadingRange` | (2,0,2): 5×5 tiles | (1,0,1): 3×3 tiles |
| `deloadingRange` | (2,0,2) | (2,0,2) |
| `useLoadingRangeMin` | false | false |
| `positionCheckTime` | 0.111 s | 0.111 s |
| `destroyTileDelay` | 1.3 s | 1.3 s |
| `sceneLoadWaitFrames` | 1 | 1 |
| `looping` | false | false |

- Both reference `StreamerConcurrentManager` 8563, with `maxParallelSceneLoading = 1`. **One scene
  loads at a time across both streamers.**
- With load 1 and unload 2, near tiles have hysteresis: between 9 and 25 near tiles stay loaded,
  depending on the path walked. Far tiles are exactly the 5×5 box.
- `WorldStreamingInit` tells the two apart by the substrings "near" and "far" in the GameObject
  name (`decomp/Assembly-CSharp/WorldStreamingInit.cs:310-332`).

**`WorldMover`** (`level4`, MonoBehaviour 11487):

- `movingEnabled = false`. Code turns it on once loading finishes (`WorldStreamingInit.cs:362`).
- **`moveRange = 823 m`**, not the class default of 100 (`decomp3c/WorldStreamer/WorldMover.cs:16`).

### 1.2 How tiles are chosen

- `PositionChecker` runs every `positionCheckTime` (`Streamer.cs:273-290`) and calls
  `CheckPositionTiles` while `streamerActive` and a tracker exists.
- `CheckPositionTiles` (`Streamer.cs:292-311`):
  1. reads the singleton tracker, `SingletonBehaviour<WorldMover>.Instance.playerTracker`, only if
     `IsSynced()`;
  2. computes the absolute tile `floor((pos - currentMove) / size)`;
  3. **only when that tile changes**, stores it in the public fields `xPos/yPos/zPos`, calls
     `SceneLoading()`, and schedules `SceneUnloading` after `destroyTileDelay`
     (`InvokeDelayed`, `:313-317`).
- The tracker is DV's `PlayerTracker`. It follows `PlayerManager.ActiveCamera`, then the player
  (`decomp/Assembly-CSharp/PlayerTracker.cs:32-36`, `:38-52`, `:63-74`), and
  `PlayerCameraOverride` wins (`PlayerManager.cs:14-42`). That's what stage 3b's
  `DedicatedHostAnchor` exploits (`Multiplayer/Components/Networking/DedicatedHostAnchor.cs:40-83`).

### 1.3 The loading pipeline

**`SceneLoading()`** (`Streamer.cs:319-449`):

- It loops `xPos ± loadingRange.x` and `zPos ± loadingRange.z` (`:385-445`). The y range is 0.
- For each existing `SceneSplit` that isn't `loaded`, it sets `loaded = true` and adds it to the
  private `scenesToLoad` and the public `loadedScenes` (`:426-441`).
- The `loaded` flag is the **duplicate guard**: a tile is queued at most once.
- It sets `tilesToLoad` to the number newly queued **in this call** (`:446`).

**`Update` → `LoadLevelAsyncManage`** (`:235-271`):

- It returns while `currentlySceneLoading > 0`.
- It pops `scenesToLoad[0]` (FIFO) while the shared counter is below `maxParallelSceneLoading`
  (`:251-264`).
- It sets `Application.backgroundLoadingPriority = Low` (`:262`), so Unity spends at most about
  2 ms a frame integrating the loaded scene on the main thread.
- It calls `SceneManager.LoadSceneAsync(sceneName, Additive)` (`:263`).

**Registration** happens when the loaded scene's root `SceneSplitManager.Start` runs:

- `AddToStreamer()` (`decomp3c/WorldStreamer/SceneSplitManager.cs:15-40`) finds streamers **by
  tag** `SceneStreamer` and calls `AddSceneGO` on **the first** one whose collection contains the
  name, then `return`s.
- `AddSceneGO` (`Streamer.cs:205-233`):
  - stores `sceneGo`;
  - offsets the root by `currentMove` (`:215`);
  - `tilesLoaded++` and `currentlySceneLoading--` (`:217-222`);
  - adds the coordinate to the private `loadedScenesCoords` (`:227`);
  - raises `LoadedScenesChanged` (`:228`).

**`SceneUnloading()`** (`:451-502`):

- For each loaded scene whose `sceneGo != null` and which is more than `deloadingRange` away from
  **`xPos/zPos`** (`:456`), it:
  - removes it from `loadedScenes` and `loadedScenesCoords` (`:467-468`);
  - raises `LoadedScenesChanged`;
  - destroys its `Terrain` child (`:470-481`), of which there is none here;
  - calls `UnloadSceneAsync(sceneGo.scene.name)` (`:484`).
- **It only runs after a tile change.** Changing ranges at runtime unloads nothing until the
  tracker crosses a tile, which `docs/design/parallel-test-sessions.md` observed too.
- A harmless vanilla slip: the z test uses `deloadingRange.x` (`:456`).

**No pooling, and assets are never released:**

- Scenes are loaded and unloaded whole.
- `UnloadAssets` calls `Resources.UnloadUnusedAssets()` only if the static
  `unloadUnusedAssetsEnabled` is set (`:14`, `:544-571`). It's false. DV toggles it only through
  console commands (`decomp/Assembly-CSharp/DV/Console.cs:2861-2877`), and otherwise calls it
  only on the main menu (`DV.UI/MainMenu.cs:26`).
- Unity doesn't free scene assets on `UnloadSceneAsync`. Meshes and other assets of every tile
  ever visited therefore stay resident until the main menu. GameObjects and colliders are freed.

**`IsSceneLoaded(worldPos)`** (`:615-622`) is
`loadedScenesCoords.Contains(floor((pos - currentMove)/size))`:

- **true only once that tile's scene has finished loading and registered**;
- false again as soon as unloading starts.

It doesn't care which anchor caused the load.

**Origin shift:** `OnWorldMoved` (`:188-198`) subtracts the move from every loaded scene root.
`AddSceneGO` adds `currentMove` to newly arrived roots. Both work for any set of loaded tiles.

### 1.4 One Streamer loading the union of boxes: feasible, two patches

**Replace `public void CheckPositionTiles()`** with a Harmony prefix that returns false on a
dedicated host:

- Compute the tile of the primary tracker exactly as vanilla does, plus the tile of each extra
  anchor (each gameplay player; section 7).
- If the de-duplicated tile set (a signature) changed since the last check, then:
  - for each extra anchor, set `xPos/zPos` (public fields) to its tile and call the private
    `SceneLoading()` through `AccessTools.Method`;
  - do the primary last, so `xPos/zPos` ends on the primary;
  - start the private static `InvokeDelayed(SceneUnloading, destroyTileDelay)`, or the mod's own
    coroutine.
- An anchor appearing **or leaving** must change the signature, or the left-behind tiles never
  unload.
- Per-anchor ranges are possible: temporarily set `loadingRange` around the `SceneLoading()` call.
  For example, far range 1 (3×3) for extra anchors to save memory; section 6.

**Filter `private void SceneUnloading()`** with a prefix and a postfix:

- The prefix takes out of the public `loadedScenes` list every scene within that anchor's
  deloading range of any **extra** anchor, and stores them in `__state`.
- The postfix puts them back.
- The vanilla body then only unloads what no anchor keeps. The primary keeps its own range through
  vanilla's `xPos` test.

**Optional: prioritise loading.** Reorder the private `scenesToLoad` (with `FieldRefAccess`) by
distance to the nearest anchor. Near tiles at an anchor's own tile go first; a new anchor's far
ring goes last. With `maxParallelSceneLoading = 1`, FIFO lets a newly joined distant player's
essential tiles wait behind another anchor's far ring.

**What needs no patch:**

- `IsSceneLoaded`;
- `OnWorldMoved` and `AddSceneGO`;
- the duplicate guard (`SceneSplit.loaded`);
- `WorldStreamingInit.IsSceneAndTerrain*Loaded` (`WorldStreamingInit.cs:132-164`), which asks both
  streamers about a position.

**What it relies on:** the private member names `SceneLoading`, `SceneUnloading`,
`InvokeDelayed` and `scenesToLoad`. The public ones are `xPos`, `yPos`, `zPos`, `loadedScenes`,
`scenesArray`, `loadingRange`, `deloadingRange`, `destroyTileDelay`, `sceneCollection` and
`currentMove`.

**`LoadingProgress`** (`tilesLoaded / tilesToLoad`, `:130-140`) stays above 1 after the first
load, because `tilesLoaded` is cumulative. `LoadLevelAsyncManage` then paces with
`sceneLoadWaitFrames`; that's harmless.

### 1.5 Several Streamer instances, one per anchor: not workable

- **Shared collection, duplicate loads.** Each instance has its own `SceneSplit` objects
  (`PrepareScenesArray`, `:573-590`), so both would `LoadSceneAsync` the same scene name. Unity
  then loads two copies of the tile.
- **Mis-registration.** Both copies' `SceneSplitManager` register with the **first** tagged
  streamer only (`SceneSplitManager.cs:22-37`):
  - The first streamer's `sceneGo` is overwritten by the second copy (`Streamer.cs:214`), and its
    `currentlySceneLoading` goes negative.
  - The second streamer's `currentlySceneLoading` never drops back, so it stops loading for good
    (`:243`).
- **Origin shifts.** The orphaned copy is in nobody's `loadedScenes`, so `OnWorldMoved` never moves
  it (`:191-197`). It drifts by every shift and is never unloaded.
- **Unloading by name.** `UnloadSceneAsync(scene.name)` is ambiguous with two same-named scenes
  loaded (`:484`).
- **Concurrency.** Even with disjoint tiles, all instances share the one `StreamerConcurrentManager`
  (level4 object 8563), so nothing gets faster.

### 1.6 A cheaper variant to measure first: scenery for the whole map

Setting both streamers to `loadingRange = 15` and `deloadingRange = 15+` loads every tile once and
never unloads.

- The harness already does this at launch with `-dvmp-near-range` / `-dvmp-far-range`
  (`DevTools/DvmpAgent/HarnessMode.cs:41-50`, `:135-154`).
- On disk that's 31 MB of Near and 170 MB of Far scenes (section 6).
- It needs **no Streamer patch at all**. `IsSceneLoaded` is then true everywhere, and only
  `TerrainGrid` needs multi-anchor work.
- **Unknowns:**
  - runtime memory and per-frame cost: 17,560 MonoBehaviours, 5,481 lights and 29,653 renderers in
    Far tiles; 9,352 MonoBehaviours in Near;
  - initial load time: 512 scenes, serial, at background priority Low;
  - origin-shift cost with every collider loaded (section 3).

  Measure before choosing (section 7, M1).

**Built for Near (2026-10-17):** every multiplayer server now loads every Near tile
(`ServerWorldCoverage`, see [server-player-split.md](server-player-split.md#world-coverage-around-every-player)).
Measured on a player host: all 256 tiles in 36 s in the background, about +76 MB, no change in frame
time, and Near ballast under 38 of 40 random far track points (the other two are yard tracks on Far
yard geometry). Far tiles and terrain are still single-anchor.

---

## 2. TerrainGrid, DistantTerrain, railway visuals, tunnels

### 2.1 Configuration (read from the data files)

**`TerrainGrid`** (`DerailValley_Data/level522` = `terrains_w3`, MonoBehaviour 365, at the
world origin):

| Field | Value |
|---|---|
| `loadingRingSize` | **2**: a 5×5-cell region |
| `worldNameToLoad` | `"w3"` |
| `addToVegetationStudio` | true |
| `pixelError` | 10 |
| `drawInstanced` | true |
| `terrainLayer` | 8 (Terrain) |
| `vegetationReloadWaitFrames` | 2 |
| `maxConcurrentLoads` | 3 |

**Cells:** `StreamingAssets/w3/info` gives `terrainSizeInWorld = 512` and
`numberOfTerrains = 1024`, so **32 × 32 cells of 512 m**.

**Per cell:** `terraindata_N` is one uncompressed bundle of 642,496 bytes. It holds:

- a `TerrainData` of 543,300 bytes, whose **heightmap is 513 × 513** (263,169 samples, about 1 m);
- 4 `SplatAlpha` textures of 64 × 64.

The terrains scene itself is 37 KB: TerrainGrid, TerrainHoleManager and `DistantTerrain_w3`
display meshes only, with no colliders.

### 2.2 How it works (`decomp/DV.TerrainSystem/DV.TerrainSystem/TerrainGrid.cs`)

**Single anchor.**

- `trackingReference` is set to the PlayerTracker transform (`WorldStreamingInit.cs:293`).
- `Update` (`:429-464`) calls `SetTargetCoord(ToGridCoords(trackingReference.position))` (`:439`).
- `ToGridCoords` uses `transform.InverseTransformPoint` (`:376-380`). The grid sits under the
  origin-shift parent (`WorldStreamingInit.cs:227`), so it is shift-safe.

**`SetTargetCoord`** (`:401-427`) early-returns if the target is unchanged (`:403`). Otherwise
`toLoad` = the region around the target ∪ the region around the current cell (`:408-413`), and:

- every non-unloaded cell **not** in `toLoad` is unloaded at once (`:417-421`). That hides its
  Terrain and calls `AssetBundle.Unload(true)` (`GridCell.Unload` `:63-74`;
  `TerrainInfoLoadWrapper.cs:126-132` in the loader file);
- missing cells are queued (`:422-425`).

**Loading** runs at up to `maxConcurrentLoads` bundle loads at a time (`:451-459`), through
`AssetBundle.LoadFromFileAsync` and then `LoadAllAssetsAsync`
(`TerrainInfoLoadWrapper.LoadFromAssetBundleInternal`).

**`UpdateWorld`** starts when the queue and in-progress list are empty (`:460-463`; body
`:466-525`). It:

1. unloads cells outside the target region, yielding `vegetationReloadWaitFrames` per displayed
   cell (`:472-498`);
2. **displays every target cell in one frame** (`:500-508`);
3. sets `currentCoord = targetCoord` and raises `TerrainsMoved` (`:509-510`);
4. re-enables VS Pro per cell, yielding again (`:511-523`).

**Fixed pool.** `Awake` creates `(2·ring+1)² = 25` Terrain GameObjects, each with a
`TerrainCollider`, a VS Pro `UnityTerrain`, `MicroSplatTerrain` and `MicroSplatVisibilityHack`
(`:244-288`).

- `DisplayTerrain` takes `terrainPool[Count-1]` (`:88-89`). **An empty pool throws**
  (index -1) inside `UpdateWorld`, which `Update` steps by hand (`:431`). The exception would
  recur every frame and wedge the grid.
- `HazmatTileManager` adds a `HazmatParticleDetector` to every `generatedTerrains` entry when the
  grid initialises (`decomp/Assembly-CSharp/HazmatTileManager.cs:333-338`).

**Predicates.**

| Predicate | What it checks | Line |
|---|---|---|
| `IsInTargetRegion(Vector2Int)` | within the ring of the **single** `targetCoord` | `:363-374` |
| `IsInLoadedCell` | in the target region and the cell is `Displayed` | `:335-343` |
| `IsInLoadedRegion` | **no loading anywhere** and in the target region | `:345-352`, with `IsLoadingInProgress` at `:298-319` |
| `GetLoadedTerrainAt` | the cell's terrain if in the target region | `:326-333` |

**Users of these predicates:**

| User | Uses | Line |
|---|---|---|
| `WorldStreamingInit` | Cell and Region checks | `:132-164` |
| `BufferStop` | `IsInLoadedCell` | `BufferStop.cs:79` |
| `DerailedTrainCarIsKinematicHandler` | through `IsSceneAndTerrainCellLoaded` | `DV/DerailedTrainCarIsKinematicHandler.cs:25-29` |
| `ItemDisabler` | `IsSceneAndTerrainRegionLoaded` | `DV/ItemDisabler.cs:168`, `:207` |
| `ItemLocationForcer` | Region check | `DV/ItemLocationForcer.cs:97` |
| `TerrainAndStreamersLoadedGameObjectsDisabler` | Cell or Region check | `DV.Optimizers/TerrainAndStreamersLoadedGameObjectsDisabler.cs:65-77` |
| `TerrainHoleManager` | `GetLoadedTerrainAt` | `TerrainHoleManager.cs:167-171` |
| `HazmatTileManager` | `GetLoadedTerrainAt` | `HazmatTileManager.cs:665` |
| `CattleZone` | cell checks (visual) | `DV.VFX/CattleZone.cs:260` |
| `FastTravelController` | Region check | `FastTravelController.cs:372`, `:436` |
| `PlayerCameraSwitcher` | Region check | `PlayerCameraSwitcher.cs:443` |

### 2.3 The multi-anchor change

The anchor set is the cell coordinates of the extra anchors. The primary stays vanilla's
`targetCoord`.

1. **Pool, sized at `Awake`.** On a dedicated host, a prefix on `TerrainGrid.Awake` sets
   `loadingRingSize` to a pool ring R, and a postfix sets it back to 2. `LoadingRegionSize` and the
   pool are computed only in `Awake` (`:244-246`, `:257`). Vanilla code then builds
   `(2R+1)²` fully set-up terrains, and Hazmat's detectors follow automatically.
   - R = 5 gives 121 terrains, enough for 4 separate anchors (4 × 25).
   - R = 7 gives 225, enough for 8.
   - Also set `addToVegetationStudio = false`. VS Pro is visual, and without it the per-cell
     yields in `UpdateWorld` disappear (`:480-488`, `:513-522` are guarded by `if (component)`).
     Otherwise `UpdateWorld` with 100 cells takes about 400 frames (13 s at 30 fps) and blocks new
     loads, because `Update` returns while the coroutine runs (`:435`).
   - Add a guard prefix on `GridCell.DisplayTerrain`: with an empty pool, log and skip rather than
     throw.
2. **`IsInTargetRegion(Vector2Int)`, postfix:** if false, also true when the cell is valid and
   within `loadingRingSize` of any extra anchor's cell. This one change fixes:
   - `IsInLoadedCell`, used by derailed cars and buffer stops;
   - `GetLoadedTerrainAt`, used by tunnel holes and hazmat;
   - `IsInTargetRegion(Vector3)`.
3. **`GetCellsInRegion(center, set)` (private, `:540-552`), postfix:** add the cells of every extra
   anchor's region. It's called by `SetTargetCoord` for target and current (`:409-413`) and by
   `UpdateWorld` for target (`:500`), so the extra regions get loaded and displayed.
4. **`GetActiveCellsOutsideRegion(center, set)` (private, `:527-538`), postfix:** remove cells
   inside any extra region, so `UpdateWorld` doesn't unload them.
5. **Trigger, a prefix on `Update`:** when the extra-anchor cell set changes and the private
   `worldUpdateCoro` is null, set the private `targetCoord = null`. The original `Update` then
   calls `SetTargetCoord` straight away (`:439`), and that recomputes `toLoad` with the union and
   unloads the rest. `IsInTargetRegion` is briefly false while `targetCoord` is null, but the
   assignment and the recompute happen in the same call.
6. **`IsInLoadedRegion(Vector3)`, prefix replacing the body on a dedicated host:** true if the cell
   at the position and its valid neighbours that are in the target set are all `Displayed`.
   Vanilla's "nothing loading anywhere" (`:347`) would otherwise stall every item enable
   (`ItemDisabler`) and `ItemLocationForcer` whenever any anchor is loading, which becomes common.

**Display hitch.** When a new anchor appears, its 25 cells are displayed in one frame (`:501-508`).
Setting `TerrainCollider.terrainData` builds a PhysX heightfield of 513² on the main thread, for
each cell (`GridCell.DisplayTerrain` `:90-95`).

- **Measure the hitch.** If it's large, add the new anchor's region in steps: ring 0, then 1, then
  2. Each step is one re-evaluation.
- Keep `maxConcurrentLoads` at 3. The bundle loads are asynchronous.

### 2.4 Not needed for physics

**`DistantTerrain`** follows the same reference (`WorldStreamingInit.cs:294-296`) and positions
display meshes only (`decomp/DV.DistantTerrain/DV.WorldTools/DistantTerrain.cs:88-106`). The
`DistantTerrain_w3` hierarchy is `Disp_Level` MeshFilter and MeshRenderer, with no colliders.
Leave it on the primary.

**`RailwayMeshGenerator`** follows the tracker (`WorldStreamingInit.cs:237-239`):

- It generates rail and ballast meshes for chunks within `spatialHashFindRange` of the reference
  (`RailwayMeshGenerator.cs:22-24`, `:170-228`), plus instanced sleepers.
- The pooled chunk objects have **only a MeshFilter and a MeshRenderer**
  (`TrackChunkPoolObject.cs:27-34`). There are no colliders.
- Bogies follow the track spline (doc §2). Visual only.

**`TerrainHoleManager`** is visual for its MicroSplat alpha hole (`TerrainHoleManager.cs:224-280`),
and its culling group follows one camera (`:56-79`, `:191-222`). **But** its
`OnTerrainsMoved → UpdateHoleTerrain → GetLoadedTerrainAt` (`:159-171`) is what hands each
`TerrainHole` its current Terrain. Tunnel physics depends on that, below.

### 2.5 Tunnels: they need per-cell terrain resolution for every anchor

**The portal triggers.** The always-loaded railway scene has 89 `tunnel_collision_ignore`
GameObjects, each with a capsule trigger and a `TunnelCollisionIgnore`.

**How `TunnelCollisionIgnore` works** (`decomp/Assembly-CSharp/TunnelCollisionIgnore.cs`):

- `Start` caches the terrain-grid cell of its portal and becomes the cell's "master" (`:32-56`).
- `OnTriggerEnter`: any collider entering, except on the Interactable layer, gets
  `Physics.IgnoreCollision(cellTerrainCollider, col, true)` and is remembered per cell
  (`:93-104`, `:119-152`).
- `TransferTo` re-applies the remembered ignores whenever `TerrainHole.Terrain` changes
  (`:58-91`, raised by `TerrainHole.cs:18-32`).

**So it works for any cell, provided `hole.Terrain` is resolved.** That needs
`GetLoadedTerrainAt` to return the cell's terrain, which needs patch 2.3-2 for extra anchors.
Without it, a car inside a tunnel near an extra anchor:

- collides with the terrain heightfield above or around it, and is pushed out to the surface; or
- with no terrain loaded, isn't ignored at all.

**The tunnel walls themselves** are `Far_tunnel` MeshColliders, 24 of them, in Far tiles. Portal
frame colliders are `Near_tunnel Start/End Object` (5 boxes each) in Near tiles. So tunnels need
terrain, Near and Far.

---

## 3. Origin shift with anchors far apart

### 3.1 What DV does

- `WorldMover.Update → ForceMove` runs each frame once it's enabled (`WorldMover.cs:63-69`).
- `ForceMove` (`:82-92`): if `|x|` or `|z|` of the tracker is above `moveRange` (823 m), call
  `MoveWorld(floor(pos/823)·823)`. The tracker always ends within [0, 823) of the origin on each
  axis.
- `MoveWorld` (`:99-123`):
  - raises `AboutToMoveWorld`;
  - updates `OriginShift.currentMove`;
  - moves the tracker's player, `objectsToMove` and the **origin-shift parent**: terrain, railway,
    game content, cars (`CarSpawner.cs:222-226` waits for that parent);
  - sets a shader global;
  - calls **`Physics.SyncTransforms()`**;
  - raises `WorldMoved`. Its subscribers include `Streamer.OnWorldMoved`, `TrainCarInteriorPhysics`,
    `CharacterControllerProvider`, `ExternalCamera`, `WorldStreamingInit` (hole positions) and the
    mod's `DebugRuntime` (`Multiplayer/Debugging/DebugRuntime.cs:297-298`).
- The physics settings (`globalgamemanagers`, PhysicsManager pathID 10):
  - fixed step 1/60 (`FrameLimiter.cs:13`);
  - gravity -10;
  - sleep threshold 0.005;
  - contact offset 0.01;
  - 6 position and 1 velocity solver iterations;
  - `autoSyncTransforms` off;
  - Sweep-and-Prune broadphase.

### 3.2 Precision

float32 spacing is 2^(k-23) for |x| in [2^k, 2^(k+1)). The slowest speed that still changes the
position each step is about (ulp/2) / (1/60 s). Anything slower is **rounded away**: the body
doesn't move at all, even though its velocity isn't zero.

| \|coordinate\| on one axis | Spacing | Slowest movement per 60 Hz step |
|---|---|---|
| 0.5-1 km | 0.06 mm | 0.2 cm/s |
| 1-2 km | 0.12 mm | 0.4 cm/s |
| 2-4 km | 0.24 mm | 0.7 cm/s |
| 4-8 km | 0.49 mm | 1.5 cm/s |
| 8-16 km | 0.98 mm | 2.9 cm/s |
| 16-32 km | 1.95 mm | 5.9 cm/s |

What this means here:

- **Origin following one player (today).** The other players can be up to about 16.4 km away on
  an axis, which is the third row from the bottom of the map's range (map 16,384 m plus up to
  823 m). There, a car creeping below about 6 cm/s, or a settling item, doesn't move between
  steps.
- **Thresholds near that level:**
  - DV calls a car stationary under 1 cm in 1 s;
  - car-interior items have a 2 mm stationary threshold (doc §2);
  - PhysX's 1 cm contact offset.

  Expect jitter or "stuck then jump" when settling, and slow-roll differences against clients.
  Clients each have their own origin near themselves.
- **Origin at the map centre:** at most 8,192 m per axis anywhere, so at most 0.98 mm.
- **Origin at the centre of the anchors' bounding box:** at most 0.49 mm while the anchors fit in
  8 × 8 km, otherwise at most 0.98 mm.
- **Distances that matter are per axis,** not the 11.6 km Euclidean figure in dedicated-host.md §2.
  The conclusion ("about 1 mm") holds.

### 3.3 Recommended policy

A `WorldMover.ForceMove` prefix on a dedicated host:

- **Target:** the centre of the axis-aligned bounding box of all anchors in absolute coordinates,
  snapped to multiples of `moveRange`. Snapping keeps `currentMove` an exact integer float.
- **When to shift:** only if the largest per-axis distance from any anchor to the current origin
  exceeds a threshold, and the shift improves it by a margin (hysteresis).
  - The threshold is 4,096 m, or 8,192 m once the box is larger than 8 km.
  - The margin is 1 km.
- **Single anchor:** this reduces to roughly vanilla behaviour, but with far fewer shifts.
- **Leave the anchor camera on its player.** Many systems use `ActiveCamera` as "the player"
  (section 5b). Only the origin decision changes.

**Cost of a shift.** It moves every collider under the origin-shift parent plus every loaded tile
root, then calls `Physics.SyncTransforms`.

- The cost grows linearly with loaded content, so N anchors means about N times the spike. SAP
  stays coherent under a uniform translation, but every shape's bounds update.
- Today it happens every 823 m of primary travel, so the threshold policy makes it rare: only when
  the player spread changes by kilometres.
- **Measure the spike** (M5). Avoid shifting during a derailment that's still settling if the
  spike is large.

---

## 4. What lives where: streamed versus always-loaded scenes

These counts were extracted from every tile's level file: colliders grouped by top-level parent,
component types and layers. The layers come from TagManager: 0 Default, 8 Terrain,
10 Train_Big_Collider, 11 Train_Walkable, 12 Train_Interior, 13 Interactable, 15
Laser_Pointer_Target, 25 PostProcessing, 27 World_Item.

**Collision matrix** (globalgamemanagers PhysicsManager):

- **Train_Big_Collider (derailed cars) collides with Default, Terrain and Train_Big only.**
- **World_Item collides with Default, Terrain, Train_Interior, Interactable, Grabbed and World_Item.**
- So Default-layer statics plus terrain are what cars and items rest on.

| Scene | Loaded | Physics-relevant contents |
|---|---|---|
| `railway_w3_LFS` (520) | Always (`WorldStreamingInit.cs:230-232`) | **Buffer stops**: `term-buffer/buffer_stop_N` boxes, about 1,100 on layer 0 and 320 on layer 10, plus triggers; turntable bridges and pits; junction triggers (layers 13, 15); **89 `tunnel_collision_ignore`** triggers. No ground and no ballast. |
| `game_content_w3` (521) | Always (`:261-273`) | Station **offices** (about 2,000 Default boxes plus interiors on layers 26 and 27), refill stations, shops, depots, player house, tutorial, "Station/Property Blockers". |
| `terrains_w3` (522) + `StreamingAssets/w3/terraindata_*` | TerrainGrid, a 5×5 region of 512 m cells | Ground: `TerrainCollider` on layer 8. |
| **Near tiles** (3×3, unloaded beyond 2) | Near streamer | **Ballast**: `Near_Colliders_Gravel`, 6,934 segments of 25-75 m along **all** track (285 km in total, 138 tiles). Each is 5 boxes forming the ballast profile: a 3.24 m core plus sloped shoulders, top about 0.2 m below the segment origin (34,670 boxes). **Bridge decks**: `Near_BoxCollider`, 870 boxes of 6.6 × 2 × 27.3 m. **Bridge ends**: `Near_collider`, 262 boxes of 6.6 × 2 × 17.9 m. **Tunnel portals**: `Near_tunnel Start/End Object`, 44 + 44 with 5 boxes each. `Near_TunnelPresenceSegment`, 289 triggers on layer 25 (post-processing and audio). `Near_Cube`, 440 boxes, at portals and similar places. Rail-crossing MeshColliders (31). Sign anchors (capsule triggers on layer 15). |
| **Far tiles** (5×5) | Far streamer | **Everything built**: station yards (`Far_Station_*`), **platforms** (`Railway_Platform_*`, `Far_PassengerPlatform`), Russian, industry and civil buildings, roads, parking lots, vehicles, props, poles, fences, containers, silos, cranes, pipes, cargo, liquids and military bays. **Tunnel walls**: `Far_tunnel` MeshCollider (24). **Bridge decks again**: `Far_BoxCollider(Clone)`, 870 boxes with the same sizes and poses as Near. Viaduct start and end pieces (131 + 131, with colliders). Turntable pits. Walkable-layer structures (layer 11; player only). |

**Conclusions:**

- **Open-track derailment.** The car first meets the **Near** ballast boxes (Default layer), then
  terrain.
  - With terrain only, the host's car settles lower than on a client: the ballast top sits around
    rail level minus 0.2 m, and the terrain is lower on embankments.
  - The client's car then follows the host's snapshots into the ballast.
- **Bridges.** Decks are in Near **and** Far. Either streamer covers them, but terrain-only drops
  the car into the valley.
- **Tunnels.** They need terrain (with the hole ignore, section 2.5), Near (portals) and Far
  (walls).
- **Stations.** Platforms and yards are in Far. Offices are always loaded.
- **Buffer stops** are always loaded, but a **broken** buffer stop is kinematic unless its terrain
  cell is loaded (`BufferStop.cs:54-80`, `:79`). That needs only the TerrainGrid patch.
- **Items.** On station platforms, yards and industries they need Far. On open ground they need
  terrain. In offices they need nothing more. Near only matters for items on the track bed.
- **`DerailedTrainCarIsKinematicHandler` already encodes this.** A car is dynamic only if the near
  tile, the far tile **and** the terrain cell at its position are loaded
  (`DV/DerailedTrainCarIsKinematicHandler.cs:25-29`, `WorldStreamingInit.cs:149-164`). It
  re-checks on each streamer `LoadedScenesChanged` or `TerrainsMoved`
  (`WorldStreamingInit.cs:166-169`, `:289`, `:331`). It's created in `TrainCar.Derail`
  (`TrainCar.cs:1491-1501`).
- **"Terrain only" is not enough. Near, Far and terrain are all needed** around each player.

---

## 5. Cheaper alternatives

### (a) Heightmap ground proxies: rejected

- `HeightMapProvider` holds `WorldHeight.hmap`, 8 MB of `ushort`: **2048 × 2048 at 8 m**
  (`decomp/Assembly-CSharp/DV.WorldTools/HeightMapProvider.cs:56-59`, `:98-111`; `GetInterpolated`
  `:113`).
- The real terrain cells are 513 × 513 per 512 m (about 1 m) and cost only about 0.64 MB on disk
  and about 1.6 MB of RAM each with a collider. **Loading the real cells (section 2.3) is as cheap
  as building proxies, and exact.**
- A proxy misses ballast (car lower than the client's), **bridges** (the car falls to the valley,
  and the client's car is dragged after it) and **tunnels**. Under the proxy surface a car has
  nothing below, falls, and `TrainCar.OnOutOfWorld` "rescues" it to the heightmap plus 10 m, on
  the mountain (`TrainCar.cs:1715-1723`).
- It also misses platforms and buildings for items.

### (b) Pointing the kinematic checks at "near any player"

`DerailedTrainCarIsKinematicHandler` and `BufferStop` already test the **object's own position**
against what's loaded. They have no anchor term.

- Changing them to "within X m of a player" **without** loading the world makes the car dynamic
  over nothing: it falls, then the out-of-world rescue fires.
- It's only correct as part of multi-anchor loading. There the TerrainGrid predicate patch (2.3-2)
  plus the Streamer union make the vanilla checks right with **no** changes to these classes.

**The `ActiveCamera` and player-distance systems do need multi-anchor patches** (all in
`decomp/Assembly-CSharp`):

| System | Code | Patch |
|---|---|---|
| `ItemDisablerGrid` | 3×3 cells of 128 m around `ActiveCamera` (`DV/ItemDisablerGrid.cs:22-43`), driving `ItemDisabler.OnItemDisablePositionUpdated` (`DV/ItemDisabler.cs:156-190`) | Postfix `IsCoordActive` to true near any anchor's cell; postfix `Update` to raise `PositionUpdated` (event field) when any anchor's cell changes |
| `RespawnOnDrop.CheckDistances` | `farFromPlayer` uses `PlayerTransform`, the parked body; `farFromActiveCamera` makes items kinematic (`RespawnOnDrop.cs:326-355`, `:357-369`; 200 m / 1 km at `:11-13`, `:88`). The mod already suppresses `RespawnOrDestroy` for managed items (`Multiplayer/Patches/World/Items/RespawnOnDropDebugPatch.cs:19-32`), but the kinematic flag isn't suppressed. | Postfix the tuple to use the nearest gameplay player and anchor |
| `ChainCouplerVisibilityOptimizer.Update` | Disables the chain and the `CouplingScanner` when not visible and more than 50 m from `ActiveCamera` (`ChainCouplerVisibilityOptimizer.cs:80-117`). It's only enabled by renderer visibility (`:101-107`), which no host camera provides for remote cars. The scanner drives buffer pairing (`TrainBufferController.cs:35-63`) and chain interaction. Mostly visual on the host. | Low priority: nearest-anchor distance, plus enabling within 50 m of any player |
| `PlayerDistanceMultipleGameObjectsOptimizer.CheckDistance` | 500 m from `ActiveCamera` (`DV.Optimizers/PlayerDistanceMultipleGameObjectsOptimizer.cs:15`, `:82-86`) | Not patched by the mod. Nearest anchor |
| Mod: host as item simulator | Eligible only if the **service host's** position is within 192 m (`Multiplayer/Components/Networking/World/WorldItems/NetworkedItemSpatialManager.cs:39`, `:566-578`, `:656-667`). The service host has no tracked position, so it never qualifies far from the parked body. | Make host eligibility "item active and `IsSceneAndTerrainCellLoaded(item)`" instead of the host's distance, so the host can simulate and validate near any player |

### (c) Delegating far derailment physics to the nearest client

The mod leases **item** physics to clients (docs/design/item-spatial-physics.md, "Why the host
cannot always be the physical simulator", "Simulator selection"). The train model is the
opposite (docs/design/trains.md, "Core invariant", "Motion"):

- the host's canonical records are truth;
- **derailed cars use authoritative rigidbody snapshots from the host**;
- derailment changes couplers, and the host re-commits the topology.

A derailment lease would have to cover:

- impacts with host-simulated on-rail trains (the car must stay a host body other trains collide
  with);
- damage and cargo effects of collisions (host-side);
- coupler breakage and topology;
- train stress;
- fires and explosions;
- rerail;
- several clients nearby;
- disconnects mid-slide.

**Feasible only as a narrow "settle" lease:** a client simulates from the derail until rest, the
host keeps the car kinematic, follows validated samples (speed and teleport envelope, staying near
the derail point, a final pose near the heightmap or a ballast height), and commits the final pose.

**Not recommended.** Real loading costs about 100 MB per anchor (section 6), and the lease adds a
second authority path to the most complex part of the mod. Keep it as an idea for players beyond
an anchor cap.

---

## 6. Memory and CPU per extra anchor

**Disk sizes** of each tile's `level` + `sharedassets` + `.resource`:

- Near: **31 MB** for all 256 tiles; 146 not empty; median tile 0.04 MB; largest 2.1 MB.
  - 3×3 window: median **0.9 MB**, 90th percentile 2.0, maximum 5.1.
  - 5×5 window: median 2.6, maximum 7.7.
- Far: **170 MB** for all; 150 not empty; largest 26 MB (`Far__x1_z5`, which holds many shared
  meshes, because Unity stores a shared asset with the first scene that uses it).
  - 5×5 window: median **13.7 MB**, 90th percentile 27, maximum 50 (around tile 3,6).
  - 3×3 window: median 4.3, maximum 30.
- Textures are mostly outside the tiles (Far `.resource` files total 2 MB). The harness's 512 MB
  texture-streaming budget bounds them anyway (parallel-test-sessions.md).

**Estimated per extra anchor** (runtime is assumed to be about 1.5-3 times the disk size for
GameObjects, components and PhysX shapes; measure, M2):

| Part | Memory | Notes |
|---|---|---|
| Terrain | **about 40 MB RAM** + about 13 MB VRAM | 25 cells × (0.53 MB heights + about 1.05 MB PhysX heightfield of 513² × 4 B + small splats) |
| Near scenes, 9-25 tiles | about 2-20 MB | 1-8 MB on disk |
| Far scenes, 25 tiles | about 20-150 MB, typically about 40 MB | 14-50 MB on disk. With far range 1 for extra anchors, 4-30 MB on disk |
| **Total** | **about 60-200 MB; typical about 80-100 MB** | 1.5-4 % of a 4.0 GB (`-nographics`) or 5.0-5.5 GB (light host) game. Eight anchors add up to about 1-1.5 GB at worst |

Because DV never unloads scene **assets** (section 1.3), the host converges over a session to all
visited tiles' assets being resident, at most about 200 MB on disk worth. GameObjects and colliders
are freed. On a host with no viewer, enabling `Streamer.unloadUnusedAssetsEnabled` is an option:
it hitches, and that slows the host's simulation rate. Measure it (M7).

**CPU:**

- **Load bursts.** A new anchor queues up to 34 scenes, serially (`maxParallelSceneLoading = 1`
  for both streamers), plus 25 bundle loads (3 in flight). Integration at Low priority limits the
  main-thread cost to about 2 ms a frame. The single-anchor teleport took about 7 s to settle
  (dedicated-host.md §2).
- **The terrain display spike** (section 2.3) is unknown.
- **Steady state:** per 5×5 Far window, roughly 1,700-3,000 MonoBehaviours, about 900 lights and
  several thousand renderers, culled by whichever host camera renders, plus about 5-9k static
  colliders. Static shapes cost nothing until touched. Unknown, measure (M3).
- **Origin shifts** scale with all loaded colliders (section 3).

---

## 7. Recommended design

### 7.1 Shape

**A `WorldAnchors` service** (mod side, dedicated host only):

- Positions in Unity space of every followable gameplay player, using the
  `DedicatedHostAnchor.IsFollowable` rule (`DedicatedHostAnchor.cs:86-88`).
- The **primary** is `DedicatedHostAnchor.Followed`, which keeps driving the PlayerTracker and
  `ActiveCamera`.
- De-duplicated per system grid: 1024 m tiles, 512 m cells, 128 m item cells.
- **A cap**, for example 8 regions, ordered by priority: players with a derailed or moving consist
  near them first, then the most recently active. The terrain pool is sized to the cap.
- **A debug hook** to add virtual anchors from `dvmp eval`. It's needed for testing with one
  client.

`DistanceLodSystemPatch` already adds all players to physics-LOD points
(`Multiplayer/Patches/Train/DistanceLodSystemPatch.cs:49-61`). Keep it as is.

### 7.2 Exact patch points

| Target | Member | Patch | Purpose |
|---|---|---|---|
| `Streamer` (WorldStreamer.dll, global namespace) | `public void CheckPositionTiles()` (`Streamer.cs:292-311`) | Prefix, replace | Tile signature of all anchors; `SceneLoading()` per anchor tile (private, via reflection, with `xPos/zPos` set); schedule `SceneUnloading` |
| `Streamer` | `private void SceneUnloading()` (`:451-502`) | Prefix and postfix | Remove the scenes extra anchors keep from `loadedScenes`, then add them back |
| `Streamer` | private `scenesToLoad` (`:98`) | Optional reorder | Nearest-anchor-first loading |
| `TerrainGrid` (DV.TerrainSystem.dll) | `protected override void Awake()` (`TerrainGrid.cs:239-291`) | Prefix and postfix | Pool ring R; `addToVegetationStudio = false`; ring back to 2 |
| `TerrainGrid` | `public bool IsInTargetRegion(Vector2Int)` (`:363-374`) | Postfix | Or any anchor region |
| `TerrainGrid` | `private void GetCellsInRegion(Vector2Int, HashSet<GridCell>)` (`:540-552`) | Postfix | Add anchor regions |
| `TerrainGrid` | `private void GetActiveCellsOutsideRegion(Vector2Int, HashSet<GridCell>)` (`:527-538`) | Postfix | Remove anchor regions |
| `TerrainGrid` | `private void Update()` (`:429-464`) | Prefix | On an anchor change with `worldUpdateCoro == null`: `targetCoord = null` |
| `TerrainGrid` | `public bool IsInLoadedRegion(Vector3)` (`:345-352`) | Prefix, replace | Per-cell and neighbours `Displayed` |
| `TerrainGrid.GridCell` | `public void DisplayTerrain(Vector3)` (`:76-96`) | Prefix guard | Never throw on an empty pool |
| `WorldMover` | `public void ForceMove()` (`WorldMover.cs:82-92`) | Prefix | Bounding-box origin policy |
| `ItemDisablerGrid` | `IsCoordActive` (`:35-43`); `Update` (`:22-33`) | Postfixes | Any anchor; raise `PositionUpdated` |
| `RespawnOnDrop` | `CheckDistances()` (`:357-369`) | Postfix | Nearest player and anchor |
| `ChainCouplerVisibilityOptimizer` | `Update()` (`:109-117`) | Prefix | Nearest anchor (low priority) |
| `PlayerDistanceMultipleGameObjectsOptimizer` | `CheckDistance()` (`:82-86`) | Prefix or transpiler | Nearest anchor |
| Mod | `NetworkedItemSpatialManager` simulator selection (`:566-578`, `:656-667`) | Code change | Host eligible where its world is loaded |
| Mod docs | `docs/design/dedicated-host.md` | Doc | Stage 3c section; §2 answers ("where do platforms live") |

No changes are needed to `DerailedTrainCarIsKinematicHandler`, `BufferStop`, `WorldStreamingInit`,
`TunnelCollisionIgnore`, `TerrainHoleManager`, `DistantTerrain` or `RailwayMeshGenerator`.

### 7.3 Stages

**3c.0: test facility and decisive measurements.**

- A virtual-anchor debug hook, and/or a second dvmp client:
  - `Session.cs` handles exactly one client per session today (`DevTools/Dvmp/Session.cs:176-215`);
  - the targets accept `clients` and pids (`DevTools/Dvmp/Agents.cs:63-77`);
  - a second client needs its own name and GUID patch, as the host probe does.
- Run M1: whole-map scenery on a dedicated host.
- Run the M6 baseline: which collider is under the derailed car in `far-derail` today, on the host
  and on the client.

**3c.1: multi-anchor world.**

- The `WorldAnchors` service.
- The **TerrainGrid** patches.
- **Either** the Streamer union patches (1.4) **or** whole-map ranges on a dedicated host (1.6), if
  M1 is acceptable.
- This alone makes derailed cars, broken buffer stops and tunnels right around every player.

**3c.2: items.**

- `ItemDisablerGrid`, `RespawnOnDrop`, `PlayerDistanceMultipleGameObjectsOptimizer`.
- Host simulator eligibility. The chain optimizer is optional.

**3c.3: origin policy** (`ForceMove` prefix), with the precision tests (M4) and the shift-spike
measurement (M5).

**3c.4: tuning.**

- Smaller far range for extra anchors.
- Load priority.
- Staged terrain display if M3 shows a spike.
- The anchor cap.
- The `UnloadUnusedAssets` policy (M7).

### 7.4 Tests (dvmp, `DevTools/Scenarios`)

`far-derail` (`DevTools/Scenarios/far-derail/run.sh`, `state.cs`) is the single-anchor template.
It derails next to the client 4-8 km from the parked body, then checks on the host:

- `IsSceneAndTerrainCellLoaded` true;
- not kinematic;
- ground below;
- speed under 0.5;
- the client's copy within 1.5 m.

New scenarios:

- **`far-derail-multi`.**
  - The anchor follows client A (longest connected), at station X.
  - Client B (or a virtual anchor) is at station Y, at least 6 km from both A and the parked body,
    so the boxes don't overlap.
  - Derail next to B, then assert:
    - the far-derail checks on the host;
    - **`groundCollider` is a ballast `Cube` (BoxCollider), not `TerrainCollider`**, on host and
      client;
    - the host and client heights agree within 5 cm.
  - Repeat with B moving away, beyond 2 tiles. The car must go kinematic again: tiles unloaded,
    no crash.
- **`far-derail-bridge`.** Pick track over a `Near_BoxCollider` (a downward raycast from the car
  hits a box named `Near_BoxCollider` or `Far_BoxCollider(Clone)`). Derail there and assert the car
  rests on the deck, no more than 3 m below rail height.
- **`far-derail-tunnel`.** Pick a car inside a `Far_tunnel` (a raycast up hits the tunnel
  MeshCollider). Derail and assert:
  - it stays inside, no more than 3 m from the rail;
  - not kinematic;
  - `TerrainHole.Terrain` is non-null for that portal's cell on the host.
- **`far-bufferstop`.** Near B, push a car into a buffer stop at more than 7 m/s
  (`breakVelocitySqr = 49`, `BufferStop.cs:16`). Assert the broken buffer stop's rigidbody isn't
  kinematic on the host (`:77-80`).
- **`far-items`.** Near B, drop an item on a platform. Assert it's active and not kinematic on the
  host, and that the host is the simulator after the lease moves on (3c.2).
- **Regression.** Run the existing scenarios with `UP_ARGS=--dedicated` (dedicated-host.md lists
  nine), plus normal mode, which must be unchanged because every patch is gated on the dedicated
  host.

### 7.5 Risks

1. **TerrainGrid pool exhaustion wedges terrain for everyone** (`TerrainGrid.cs:88-89`, `:431`).
   It needs the cap plus the guard.
2. **`IsInLoadedRegion` semantics.** Vanilla gates on "no loading anywhere" (`:347`). Changing it
   affects `ItemDisabler`, `ItemLocationForcer`, fast travel and camera switching
   (`PlayerCameraSwitcher.cs:443`).
3. **Streamer unloading** only happens on a signature change. A player leaving must change it, or
   tiles leak.
4. **Load starvation and hitches:**
   - serial scene loads (one in flight for both streamers);
   - terrain cells displayed in one frame;
   - VS Pro per-cell yields if vegetation is left on.

   A starved host slows every train (doc §4, simulation-rate scaling).
5. **Precision far from the origin** (section 3.2). The origin policy limits it to at most about
   1 mm. Slow creep below 1.5-3 cm/s may still stall at the edges; measure.
6. **Origin-shift spikes** grow with loaded content (M5).
7. **Game-update fragility.** Private names: `SceneLoading`, `SceneUnloading`, `InvokeDelayed`,
   `scenesToLoad`, `targetCoord`, `worldUpdateCoro`, `GetCellsInRegion`,
   `GetActiveCellsOutsideRegion`, `GridCell`, `terrainPool`. Each patch should log loudly when its
   target isn't found, as `DistanceLodSystemPatch` does.
8. **Memory creep:** scene assets are never released (section 1.3). This is a candidate
   contributor to long-session growth like #78 on a dedicated host with players spread out.
9. **Vanilla systems that assume one camera** remain partly single-anchor (visual ones such as
   `TerrainHoleManager`'s hole rendering, `CattleZone`, VS Pro). Harmless on a host, but check
   that nothing physics-relevant was missed: grep for `PlayerManager.ActiveCamera` with
   `isKinematic`/`SetActive` in `decomp/Assembly-CSharp` gives about 80 files.

### 7.6 What can't be known without running the game (measurements to take)

- **M1. Whole-map scenery.** Dedicated host, launched with `-dvmp-near-range 15 -dvmp-far-range 15`.
  - Working set, Unity allocations and Mono heap after settling.
  - Load time to `IsStreamingDone` and to all tiles loaded (`HarnessMode` snapshot `loadedScenes`).
  - fps and p99 at the 30 fps cap.
  - Origin-shift spike.

  This decides between union and whole map.
- **M2. Memory per anchor.** One versus two versus four non-overlapping anchors (virtual).
  - Deltas in working set, Unity allocations, Mono heap and VRAM.
  - Loaded near and far tile counts, and terrain cells displayed.
  - Compare against the estimates in section 6.
- **M3. Load latency and hitches when an anchor appears.**
  - Time until `IsSceneAndTerrainCellLoaded` at the anchor.
  - Longest frame during `UpdateWorld`: 25 heightfields at once, with VS Pro on and off.
  - Frame times while 34 scenes integrate.
- **M4. Precision**, with the origin offset 1, 4, 8, 12 and 16 km from the test site (shift the
  origin by hand):
  - a derailed car settling: final jitter amplitude and time to sleep;
  - a slow roll at 1, 2, 5 and 10 cm/s on a flat track: does it move;
  - coupling at 0.3 m/s;
  - a resting item: jitter and sleep;
  - a car with a loaded interior: the 2 mm threshold.
- **M5. Origin-shift cost.** Time `MoveWorld` (the `AboutToMoveWorld` → `WorldMoved` hooks already
  exist in `DebugRuntime`) with 1, 2 and 4 anchors' content loaded.
- **M6. Ground under derailed cars today.** In `far-derail`: the collider name and type below the
  car, and the height difference between host and client. Also the same with only terrain loaded,
  to confirm the ballast offset.
- **M7. Releasing assets.** Memory over a long session with players wandering, with and without
  `Streamer.unloadUnusedAssetsEnabled` (the hitch length of each `UnloadUnusedAssets`).
- **M8. Steady-state CPU per anchor.** Main-thread ms per frame: scripts, physics and culling from
  the extra Far windows.
