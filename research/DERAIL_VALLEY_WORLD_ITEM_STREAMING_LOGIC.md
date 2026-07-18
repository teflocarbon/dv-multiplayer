# Derail Valley World Item Streaming and Optimizer Logic

This document records established behavior in Derail Valley's world-item, distance-optimization,
floating-origin, terrain-streaming, and hazmat-grid systems. It is research material for the
multiplayer item-world synchronization design; it does not itself prescribe the multiplayer
protocol.

The corresponding multiplayer design is
[`MULTIPLAYER_ITEM_WORLD_SYNC_DESIGN.md`](../MULTIPLAYER_ITEM_WORLD_SYNC_DESIGN.md).
Installed gadget and train-customization behavior is maintained separately in
[`DERAIL_VALLEY_GADGET_CUSTOMIZATION_LOGIC.md`](DERAIL_VALLEY_GADGET_CUSTOMIZATION_LOGIC.md).
Snapped-item and train-accessory behavior is maintained in
[`DERAIL_VALLEY_ITEM_SNAPPING_LOGIC.md`](DERAIL_VALLEY_ITEM_SNAPPING_LOGIC.md).

## Sources inspected

- `DV.Optimizers.PlayerDistanceGameObjectsDisabler`
- `DV.Optimizers.PlayerDistanceMultipleGameObjectsOptimizer`
- `DV.ItemDisablerGrid`
- `DV.ItemDisabler`
- `WorldStreamingInit`, including `LoadingRoutine`, scene migration, and readiness predicates
- `StartingItemsController`, including every storage instantiation and finalization path
- `DV.CabControls.ItemReparentingBase`
- `HazmatTileManager`
- Existing multiplayer `NetworkedItemManager`, `NetworkedItem`, `ServerPlayer`, and
  `AuthoritativeItemRegistry` behavior
- Existing multiplayer `ItemBase_Patch`, which adds `NetworkedItem` from an `ItemBase.Awake`
  postfix
- Runtime inspection of `origin_shift_parent` and office/item hierarchies
- Runtime enumeration of every additively loaded scene through `SceneManager`

Still required:

- the full `TerrainGrid` implementation;
- any office/station region loader layered above terrain streaming;
- save-time handling of inactive scene-authored `ItemBase` objects;
- the exact initialization order of `ItemBase`, `ItemDisabler`, storage loading, and the
  multiplayer item catalogue.

## Executive conclusions

1. Derail Valley already divides world items into an origin-shift-corrected 128 metre grid through
   `ItemDisablerGrid`.
2. The locally active item area is the current grid cell plus its eight neighbours: a 3 by 3 cell
   square.
3. `ItemDisabler` deactivates distant item GameObjects; it does not destroy them. Runtime inspection
   shows that the office and world item objects are scene-resident from startup and hidden by
   distance systems.
4. Re-enabling a distant item is explicitly gated by
   `WorldStreamingInit.IsSceneAndTerrainRegionLoaded(item.transform.position)`. This is the best
   currently known native readiness predicate for applying a client item projection.
5. Inventory, hand, train-car, Lost and Found, snapped, and most player-owned items are exempt from
   ordinary grid disabling. Spatial world interest cannot therefore replace placement/ownership
   interest.
6. `PlayerDistanceGameObjectsDisabler` and
   `PlayerDistanceMultipleGameObjectsOptimizer` independently toggle larger office or system roots.
   A network projection can be logically present while an ancestor is locally inactive.
7. `HazmatTileManager` provides a separate 8 metre, packed-coordinate grid and subscribes directly
   to terrain chunk load/unload events. It is useful for compact position indexing, but it is much
   finer than the native item-activation grid.
8. `activeSelf` and `activeInHierarchy` are presentation/optimization facts, not item existence or
   authority facts.
9. The current multiplayer item system scans all networked items per player using distance,
   remembers items in `KnownItems`, and removes only `NearbyItems` when relevance is lost. A known
   item therefore does not naturally receive a fresh Create after leaving and re-entering interest.
10. Current client scene-object matching is primarily by prefab name. That is ambiguous when many
    identical scene-authored items exist and should be replaced with stable scene-object identity.
11. `game_w3` is the active scene, while `game_content_w3`, terrain, railway, and generated Near/Far
    scenes are loaded additively. Scene ownership and transform ancestry are not equivalent: the
    content scene reports zero roots even though inspected items report `scene=game_content_w3` and
    are parented beneath `[origin shift content]`.

## Floating origin

Most physical world objects are beneath `WorldMover.OriginShiftParent`. Derail Valley periodically
moves that parent so the local player remains close to Unity's numeric origin. Consequently,
`transform.position` is process-local and changes when the world origin shifts.

The established conversion is:

```csharp
Vector3 absolutePosition = unityPosition - WorldMover.currentMove;
Vector3 unityPosition = absolutePosition + WorldMover.currentMove;
```

The sign follows the game code in both `ItemDisablerGrid.GetGridCoords` and
`HazmatTileManager.GetGridPositionFromWorldPosition`.

Network interest, persistence, stable spatial identity, and server validation must use absolute
coordinates. Unity positions remain appropriate only for an already loaded local representation.

## Native 128 metre item grid

`ItemDisablerGrid` is an auto-created singleton. Every frame it derives the active camera's cell:

```csharp
worldPosition -= WorldMover.currentMove;
int x = Mathf.FloorToInt(worldPosition.x / 128f);
int y = Mathf.FloorToInt(worldPosition.z / 128f);
```

It raises `PositionUpdated` only when the camera crosses a cell boundary. This is event-driven at
the expensive layer: item listeners do not need to recompute distance every frame.

### Active neighbourhood

`IsCoordActive` returns true when both coordinate deltas from the camera's current cell are at most
one:

```text
NW  N  NE
 W  P   E
SW  S  SE
```

This is a 3 by 3 active region. The class reports `MIN_ACTIVE_RANGE = 192f`, corresponding to one
full neighbouring cell plus the half-width of the current cell.

The native cell coordinate is already the best starting point for multiplayer item interest:

- it matches the game's own item activation behavior;
- it is invariant across independently shifted clients;
- it changes infrequently;
- it can be serialized as two signed integers or packed into a larger key;
- no arbitrary multiplayer-only spatial boundary is required.

The 128 metre coordinates should not be confused with `TerrainGrid` coordinates until the full
terrain implementation confirms their relationship.

## `ItemDisabler`

Every `ItemBase` constructs an `ItemDisabler` with its `ItemReparentingBase`. The disabler tracks:

- grabbed state;
- inventory membership;
- train-car parenting;
- Lost and Found membership;
- snapping;
- player ownership;
- paint-station static parenting;
- dumpster state;
- the grid coordinate in which it was disabled.

It listens to:

- `ItemDisablerGrid.PositionUpdated`;
- `ItemReparentingBase.ItemParented`;
- `ItemBase.Grabbed` and `ItemBase.Ungrabbed`;
- `StorageLostAndFound.ItemAdded` and `ItemRemoved`;
- `ItemBase.ItemInventoryStateChanged`.

### Protected placements

The ordinary distance check returns without disabling an item when it is:

- grabbed or in inventory;
- in Lost and Found;
- parented to a train car;
- snapped;
- a player-owned item not on the special disabling paint-station parent.

These exemptions demonstrate that world-cell relevance and player/storage relevance are separate
dimensions. An item in a player's hand must remain projected even if its absolute world cell is not
one of the recipient's subscribed world cells.

### Disabling

For an eligible active item, `ItemDisabler` computes the native 128 metre cell. If that cell is not
active, it performs:

```csharp
item.gameObject.SetActive(false);
disabledCoords = gridCoords;
```

The object is retained. Its identity, transform, components, serialized references, and disabler
remain available when the relevant hierarchy is inspected with inactive objects included.

### Re-enabling

When the camera grid changes and the remembered cell becomes active, the item is enabled only if:

```csharp
WorldStreamingInit.Instance == null ||
WorldStreamingInit.Instance.IsSceneAndTerrainRegionLoaded(item.transform.position)
```

If the region is not ready, a coroutine retries once per second. It abandons the attempt if the
item is destroyed or its cell leaves the active neighbourhood before readiness is reached.

This provides an important multiplayer rule:

> Receiving an authoritative item baseline does not imply that its local projection can be bound
> immediately. The client must retain it until Derail Valley reports the scene and terrain region
> ready.

## Larger distance optimizers

### `PlayerDistanceGameObjectsDisabler`

This component owns a list of GameObjects and processes one list entry per interval. Its defaults
are:

- squared disable distance: `250000`, or 500 metres;
- interval per GameObject: 2 seconds;
- startup wait: one second, followed by `StartingItemsController.itemsLoaded`.

It toggles each entry's `activeSelf` according to distance from `PlayerManager.ActiveCamera`. When
the optimizer itself is disabled, it activates every managed GameObject.

Because it processes one entry per interval, a large list can take many seconds to converge after a
teleport. Multiplayer must not interpret temporary ancestor activation as authoritative item
presence.

### `PlayerDistanceMultipleGameObjectsOptimizer`

This component controls a group of GameObjects and MonoBehaviours from one reference transform. Its
defaults are also 500 metres and a two-second check period. It waits for
`WorldStreamingInit.IsLoaded`, and it performs an additional check one frame after
`PlayerManager.PlayerTeleportFinished`.

It can disable scripts without deactivating their owner, or deactivate roots containing many item
objects. Stable catalogue lookup must therefore include inactive descendants and must not rely on
enabled behavior components.

## Hazmat spatial model

`HazmatTileManager` uses 8 metre cells. It removes the current floating-origin shift before
quantization:

```csharp
Vector3 shifted = (position - WorldMover.currentMove) / 8f;
int x = Mathf.FloorToInt(shifted.x);
int y = Mathf.FloorToInt(shifted.z);
int packed = (x << 16) | (ushort)y;
```

It reverses the key with:

```csharp
int x = packed >> 16;
int y = packed & 0xffff;
```

The current implementation treats the low coordinate as an unsigned 16-bit value. Reusing this
exact packing outside the game's expected positive map range requires care; a multiplayer spatial
key should either validate the world bounds or use two signed coordinates/a wider packing.

### Terrain lifecycle

After `TerrainGrid.Initialized`, the manager subscribes to:

- `TerrainGrid.TerrainDataLoaded`;
- `TerrainGrid.TerrainDataAboutToBeUnloaded`.

It stores per-terrain-chunk data separately from its 8 metre tile dictionary. When terrain unloads,
it releases terrain/splat presentation data but retains modified overlay state so it can be painted
again after reload.

That is a useful precedent for multiplayer projections: presentation resources can be released
while canonical state remains durable.

## Scene residency established at runtime

Runtime hierarchy inspection established that office items and apparently the complete authored
world item set exist in the loaded Unity scene from startup. Distance systems hide or disable them;
they are not normally reconstructed from prefabs each time a player approaches.

This significantly reduces the multiplayer problem:

- the host can catalogue the complete authored item set once;
- clients can catalogue corresponding dormant objects once;
- a chunk baseline can bind an existing client object instead of creating it;
- unchanged scene objects do not require full prefab/state payloads;
- object retirement should quarantine a projection, not destroy the authored GameObject.

This runtime observation still needs a deterministic count/hash test on host and client. A mismatch
must be detectable and reported rather than silently falling back to prefab-name matching.

## Loaded scene topology established at runtime

`SceneManager` reported 38 simultaneously loaded scenes in the inspected world session:

- active `game_w3` at `Assets/DV/World/Work/game_w3.unity`, with 309 roots;
- `terrains_w3`, `railway_w3_LFS`, and `game_content_w3` loading scenes, each reporting zero roots;
- a 5 by 5 set of generated `Far__xN_zN` scenes, each with one root;
- a 3 by 3 set of generated `Near__xN_zN` scenes, each with one root.

The registration event for one `ElectricStove` reported:

```text
scene=game_content_w3
path=[origin shift content]/Offices/Offices_w3/CitySouth/ItemsOffice_07/ElectricStove
```

The event retained Unity instance ID `1098658`. A later exact-instance query found the same object as:

```text
name=ElectricStove
scene=game_w3
parent=origin_shift_parent
activeSelf=False
activeInHierarchy=False
```

Every one of the 14 currently existing `ElectricStove` objects was likewise owned by `game_w3` and
parented directly to `origin_shift_parent`. `game_content_w3` reported `rootCount=0` at that time.
The full `WorldStreamingInit.LoadingRoutine` establishes the first migration precisely:

```csharp
SceneManager.LoadScene(gameContentScenePath, LoadSceneMode.Additive);
yield return null;

Scene contentScene = SceneManager.GetSceneByPath(gameContentScenePath);
contentScene.GetRootGameObjects()
    .First(go => go.name == "[origin shift content]")
    .Children()
    .ToList()
    .ForEach(go => go.transform.SetParent(originShiftParent));

contentScene.GetRootGameObjects()
    .ToList()
    .ForEach(go => SceneManager.MoveGameObjectToScene(go, SceneManager.GetActiveScene()));
```

DV loads authored objects synchronously in `game_content_w3`, yields one frame, reparents every direct
child of `[origin shift content]` beneath the active `game_w3` scene's `origin_shift_parent`, then
moves all roots remaining in the content scene into the active scene. This explains why
`game_content_w3` remains loaded but has zero roots.

The first reparent preserves each direct child's internal hierarchy. The later per-item flattening is
performed by `ItemReparentingBase`, not by `StartingItemsController`. On a loose item's collision with
something that is neither a train nor a valid `ItemStaticParent`, it selects
`WorldMover.OriginShiftParent` and calls `ParentItem`. Every `ParentItem` call performs:

```csharp
transform.SetParent(newParent);
transform.SetSiblingIndex(999);
```

Dropping an inventory item performs the same normalization to the local train interior or
`WorldMover.OriginShiftParent`. Consequently, ordinary physics contact destroys the useful authored
ancestry and deliberately destabilizes the runtime sibling index. The Unity instance survives, but
its authored scene/hierarchy cannot be reconstructed afterward.

### Saved storage items are separate instances

`StartingItemsController` does not reconcile a saved `StorageItemData` with a corresponding authored
scene object. For every inventory, world, Lost and Found, installed-gadget, or item-container record,
`InstantiateItem` performs:

```csharp
GameObject prefab = Resources.Load(itemData.itemPrefabName) as GameObject;
GameObject instance = Instantiate(prefab, safetyPosition + safetyOffset, Quaternion.identity);
instance.name = itemData.itemPrefabName;
```

It marks the Rigidbody kinematic, collects `ItemSaveData`, and later materializes the saved state.
For world records, `InstantiateStorageItemsWorld` chooses a train interior, a nearby valid
`ItemStaticParent`, or `WorldMover.OriginShiftParent`. `FinalizeItemStateLoading` applies the saved
transform and uses `ParentItemExternal`, which ultimately invokes the same sibling-index-999
reparenting behavior.

This establishes two distinct categories:

- authored catalogue objects loaded from `game_content_w3`, whose identity can only be captured
  before migration/physics reparenting;
- saved storage objects instantiated from prefab records, which are dynamic materializations unless
  multiplayer persistence explicitly associates them with an authored stable key.

Vanilla `StorageItemData` contains a prefab name and state but no authored-scene identity. Allowing an
authored multiplayer-managed item to fall through vanilla world persistence would therefore lose its
stable identity and risk recreating a generic duplicate alongside the default authored object.

### `StorageWorld` is not a world-object catalogue

`StorageController.Initialize` creates `StorageWorld` with `acceptsNonEssential=false`. The common
`AddItemToStorageItemList` path rejects an item when both conditions hold:

```text
InventoryItemSpec.BelongsToPlayer == false
storage.acceptsNonEssential == false
```

`OnInventoryStatusChanged` adds an item to `StorageWorld` after drop, purge, or equip only when
`ItemBase.BelongsToPlayer()` is true. Non-player-owned items are removed from inventory storage but
are not added to world storage. This agrees with runtime observations that junk furniture is absent
from `StorageWorld`.

Consequently:

- `IsInStorageWorld` means a player-owned loose/persistent item is registered in that vanilla
  storage category;
- it does not mean every physical world item is known to DV persistence;
- authored non-player junk is supplied by `game_content_w3` and ordinarily resets from scene data;
- multiplayer cannot use `StorageWorld` membership as its definition of world placement or item
  existence.

The previously observed `storage.SaveStorage(saveData)` call is confirmed extension-method syntax:

```csharp
public static void StorageSerializer.SaveStorage(
    this StorageBase storage,
    SaveGameData saveGameData)
```

It serializes inventory from the actual inventory array and other categories from
`storage.GetStorageItemList()`. Items are skipped when their
`InventoryItemSpec.excludeFromStorageSerialization` mask contains the current storage type.

For `StorageType.World` only, it calls `TrainCar.Resolve(item.gameObject)`. When a car is found it
saves `CarGUID` and a pose relative to `car.interior`; otherwise it saves the item's absolute world
position. Every category can include `ItemSaveData.SaveItemData()` output. This confirms that loose
player-owned train items are vanilla world-storage entries with a train anchor, while non-player junk
never reaches that list.

### Loose train items versus installed gadgets

A player-owned loose item can remain registered in `StorageWorld` while its physical parent is a
train interior. `StartingItemsController` uses `StorageItemData.carGuid` to restore that placement.
This is separate from installed gadgets.

`StorageInstalledGadgets` accepts nonessential items. `GadgetItem.Place` links and activates a
separate `GadgetBase` projection, moves the underlying item into that storage, and deactivates the
item GameObject. `GadgetBase.Remove` performs the inverse and calls
`MoveFromInstalledGadgetsToWorld`. Installed gadgets are therefore inactive persistent item records
with anchored customization projections, not ordinary loose `TrainInterior` items.

For train customization, the durable anchor key is `TrainCar.CarGUID`. Gadget component state,
mounting, drilled holes, wiring links, fuses, simulation ports, collision removal, and glass/train
destruction make this a distinct subsystem. Its complete lifecycle, save ordering, UID model, and
archived implementation archaeology are recorded in
[`DERAIL_VALLEY_GADGET_CUSTOMIZATION_LOGIC.md`](DERAIL_VALLEY_GADGET_CUSTOMIZATION_LOGIC.md).

Consequences:

- an after-load hierarchy scan is too late to reconstruct authored identity because both world
  migration and ordinary item collision rewrite ancestry;
- authored identity must be captured while the object is still in `game_content_w3`, before the
  loader moves/flattens it;
- that identity must be carried forward against the surviving object, for example in a host/client
  catalogue map or a lightweight identity component;
- `NetworkedItem.Awake`/`Register` is the proven capture point: the existing `ItemBase.Awake`
  Harmony postfix calls `GetOrAddComponent<NetworkedItem>()`, which immediately invokes
  `NetworkedItem.Awake` during the synchronous additive scene load, before `LoadingRoutine` reaches
  its following-frame migration; the later `NetworkedItem.Start` registration event empirically
  still observed the complete staging path;
- runtime wrapper names such as `origin_shift_parent` and final sibling indices must not enter the
  persistent key; `ItemReparentingBase` explicitly forces sibling index `999`;
- catalogue finalization can subscribe to `WorldStreamingInit.LoadingFinished`, after save state,
  cars/jobs, terrain streaming, and other initialization complete, while identity capture itself
  must happen during the earlier additive load;
- generated Near/Far terrain scenes are streaming presentation and should not automatically enter
  the item catalogue merely because they are loaded.

The negative runtime scene handles are opaque session-local values. Persistent identity must use the
scene path/name, never `Scene.handle`.

## Current multiplayer behavior

The current server performs a per-player scan over `NetworkedItem.GetAll()` and compares each item
to `ServerPlayer.WorldPosition`. It maintains:

```text
NearbyItems: item -> last relevant time
KnownItems:  item -> last update tick
```

Leaving interest removes the item from `NearbyItems` after a grace period, but ordinary leave does
not remove it from `KnownItems` or send an explicit projection-retirement message. Re-entering can
therefore retain the assumption that the client still owns a valid local representation.

The server marks an item known when it queues/sends Create metadata; it does not wait for the client
to prove that an authored object was found, bound, initialized, and applied.

The previous client implementation used `CacheWorldItems` and `GetFromCache` to quarantine and then
reuse objects by prefab name. That architecture was removed: it could not distinguish identical
authored objects and leaked Unity component state between logical lifetimes. The current client
keeps exact authored objects dormant for stable-key binding, instantiates fresh dynamic projections
for host Creates, and destroys unauthorized unbound or retired dynamic objects.

These are the main behaviors the chunked catalogue design must replace, not merely optimize.

## Research implications

### Stable authored identity

A scene-authored item needs an identity independent of:

- Unity instance ID;
- runtime NetId;
- activation state;
- floating-origin position;
- prefab name alone.

The candidate input captured during staging is:

```text
authored loading-scene path/name
+ complete pre-migration hierarchy path
+ deterministic sibling discriminators in that authored hierarchy
+ ItemBase/component discriminator
```

After migration, the captured identity is associated with the surviving Unity object. Its final
`game_w3/origin_shift_parent/<item>` path is diagnostic only and cannot distinguish identical items.

The catalogue should retain the full canonical identity for collision diagnostics and send a fixed
size hash in normal baselines.

### Projection readiness

At minimum, a client's projection of an authored host item has three readiness stages:

```text
catalogued dormant object
scene and terrain region loaded
host baseline bound and applied
```

`WorldStreamingInit.IsSceneAndTerrainRegionLoaded` supplies the middle stage. The last stage belongs
to multiplayer and must be acknowledged.

### Performance

The complete scene hierarchy must be scanned once, not periodically. Afterwards the host should
maintain:

```text
cell -> authored/dynamic item records
item -> current cell
player -> subscribed cells
stable scene key -> dormant local projection
```

Moving items update only their old and new cell membership. Player interest changes only when the
player crosses a native 128 metre cell boundary or their non-spatial placement changes.

## Remaining dnSpy targets

1. `TerrainGrid`, including coordinate conversion, terrain size, generated terrain lifecycle, and
   whether its chunks align with the 128 metre item grid.
2. All call sites and construction order for `ItemDisabler` and `ItemDisablerGrid`.
3. Office/station root activation components and any scene-region identifiers above terrain.
4. The item prefab/scene initialization path that assigns `ItemSaveData` and storage membership.
5. Any existing deterministic scene object IDs in serialized components or Unity scene metadata.
6. Coupler/hose attachments and any other train attachment families not covered by `GadgetBase` or
   the documented `SnappableItem` system. Brake LED bars and wired lights are confirmed gadgets.
