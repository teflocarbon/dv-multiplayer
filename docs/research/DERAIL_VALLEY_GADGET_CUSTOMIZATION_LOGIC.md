# Derail Valley Gadget and Train Customization Logic

This document records established behavior in Derail Valley's installed-gadget,
train-customization, mounting, wiring, and persistence systems. Gadgets are not ordinary loose world
items: installation replaces an active item representation with a linked customization projection
while retaining the underlying item as the persistent record.

How the mod syncs gadgets is designed in [`../design/gadgets.md`](../design/gadgets.md). The item
authority boundary is described in [`../design/item-world-sync.md`](../design/item-world-sync.md).
Generic loose world-item behavior is recorded in
[`DERAIL_VALLEY_WORLD_ITEM_STREAMING_LOGIC.md`](DERAIL_VALLEY_WORLD_ITEM_STREAMING_LOGIC.md).
Items snapped to couplers or gadget-hosted points are documented in
[`DERAIL_VALLEY_ITEM_SNAPPING_LOGIC.md`](DERAIL_VALLEY_ITEM_SNAPPING_LOGIC.md).

## Sources inspected

Decompiled `Assembly-CSharp` (`ilspycmd -p`), checked again in full on 2026-10-21:

- `DV.Customization`: `Customization`, `TrainCarCustomization`, `WorldCustomization`,
  `GadgetColliderHolder`, `CustomizationPlacementMeshes`, `STDSimPort` and the singleton anchors;
- `DV.Customization.Gadgets`: `GadgetItem`, `GadgetBase`, `GadgetComponent`, `GadgetWiringModule`,
  `GadgetWiringTool`, `GadgetSolderingTool`, `GadgetSolderingResource`, `Drillable`, `DrillTool`,
  `Mount`, `MountPoint`, `MountHoleInteractor`, `GadgetInteractor`, `GadgetRemover`,
  `GadgetHandBase`/`NonVR`/`VR`, `TelegrabbableGadget`, `GadgetSystemUtility`, `DestroyOnGadgetUnlink`;
- every class in `DV.Customization.Gadgets.Implementations` and `HUD.GadgetImplementations`;
- `StorageController`, `StorageSerializer`, `SaveGameManager`, `CarsSaveManager`,
  `StartingItemsController`, `StartGameData_FromSaveGame`, `CarSpawner`, `TrainCar`,
  `TrainCarColliders`, `WindowsBreakingController`, `OverridableBaseControl`,
  `InteriorControlsManager`, `LabelMakerController`, `GenericGadgetSpawner`, `SnappableItem`.

Line references below are to that decompile. `GI` is `GadgetItem.cs`, `GB` is `GadgetBase.cs`, `C` is
`Customization.cs`, `TCC` is `TrainCarCustomization.cs`.

## Validation targets (checked 2026-10-21)

The first version of this document left these open. Each is now answered in the section named.

| Target | Outcome |
| --- | --- |
| `GadgetWiringModule`, ports, compatibility, teardown | Topology only, a depth-1 star from switches to consumers; at most one edge per pair. See [Wiring](#wiring). |
| `GadgetComponent` and important subclasses | `Drillable`, `Mount`, `TextGadget` and the helpers; see [Mounting and holes](#mounting-and-holes) and [Concrete implementations](#concrete-implementations). |
| `GadgetSystemUtility` placement/removal restrictions | Tutorial allow-lists and wire drawing only. Feature flags are denied only by the tutorial or the console. |
| `Mount`, reservation groups, `Drillable`, required mount points | Reservation groups and train requirements are **not** checked at placement. See [Placement validation](#placement-validation). |
| Placement/removal tools and their input entry points | See [Player actions](#player-actions). |
| Other train attachment systems | `SnappableItem` (EOT lanterns, proximity sensors, items on gadget hangers); battery charger. See [Separate attachment families](#separate-attachment-families). |
| Wired electrical propagation | Deterministic push from switch values, wiring and power. See [Power](#power) and [Wiring](#wiring). |
| UID collision after loading | One static counter per process, raised to every UID seen: no collision within a process, but two processes allocate independently. See [Identity model](#identity-model). |

## Namespace and subsystem boundary

Gadget placement is concentrated under `DV.Customization` and `DV.Customization.Gadgets`.

### Customization anchors

`DV.Customization` contains the target/anchor families. Each resolves from an identification key
(`C:204-261`):

| Anchor | Key | Holes saved |
| --- | --- | --- |
| `TrainCarCustomization` | the car's `CarGUID` | with the car, `carsData[].modCarState` |
| `WorldCustomization` | `:global:` (terrain or `Default` layer with no rigidbody parent) | top-level `"Customizers"` (`SaveGameManager.cs:287`) |
| `StorageShedCustomization` | `:storage:` | never |
| `PlayerHouseCustomization` | `:player_house:` | never |
| `PaintStationCustomization` | `:paint_station:` | never |

Gadgets can be installed on all of them, not only on trains. These classes determine identity,
parenting and LOD; they do not create a separate generic world-item family.

### Gadget infrastructure

`DV.Customization.Gadgets` contains the shared lifecycle and validation layer: `GadgetItem`,
`GadgetBase` and `GadgetComponent`; wiring and soldering; `Mount`, `MountPoint`, `Drillable`; the
placement, interaction, highlighting and removal tools; collider and unlink cleanup helpers.

### Concrete implementations

`DV.Customization.Gadgets.Implementations` layers device state and presentation over the
`GadgetBase` lifecycle. The full list is under [Concrete implementations](#concrete-implementations).

### Paint

`DV.Customization.Paint` is a separate appearance subsystem (`PaintTheme`, `TrainCarPaint`). It
does not alter item placement. Existing paint sync owns it.

## Executive conclusions

1. An installed gadget consists of an underlying `GadgetItem` and a separate `GadgetBase`
   projection, a root object of its own (`GI:206-208`), never a child of the item.
2. Installation moves the item into `StorageInstalledGadgets` and deactivates it. The linked
   projection stays active on the anchor.
3. Removal unlinks the projection, reactivates the item, moves it out of installed storage, and
   parents it to the train interior or world root. Unlinking also erases the UID, soldering, wires,
   the mount, and turns bolted mount points into holes in the anchor.
4. `TrainCarCustomization.GetIdentificationKey()` returns `TrainCar.CarGUID`.
5. A customizer's `UID` is the identity wiring, mounts and hanger snaps refer to. It comes from one
   static counter per process. `Index` is not an identity.
6. Load places a gadget and restores its state in one callback per item; wiring tolerates any load
   order because both ends save the edge. Only mounts and hanger snaps wait for the after-load pass.
7. Placement checks only geometry and mounts. Train requirements and reservation groups are checked
   at link time and only gate soldering and a few implementations.
8. Several paths are random or frame-time dependent: collision dislodge, soldering amounts, the
   alternating controller, the control panel's rain fault, the hanger's swing.
9. Gadgets read and write the train simulation directly: ports (`Port.Value`) and the controls
   overrider (`Set`, `Move`). `Move` behaves differently with and without a loaded cab interior.
10. Several paths create or destroy items: the label maker, the empty duct tape, the empty solder
    spool, label gadgets on removal.

## Object model

### `GadgetItem`: persistent item record

`GadgetItem` requires `ItemSaveData` and `InventoryItemSpec`. During `Awake` it subscribes to the
item-save request, load, and after-load events; instantiates its configured `GadgetBase` prefab as a
root object; deactivates the projection; and calls `GadgetBase.AssignItem(this)`.

The item owns its projection even while it is loose, held, or in inventory. The active item and the
active installed projection are alternate presentations of one persistent object.

`GadgetItem.Item` is set only in `Start`, which an inactive object never runs.

`GadgetItem` has **no `OnDestroy`**. Destroying the item leaves the projection behind: an inactive
root object, or still linked and visible if the gadget was installed. Code that destroys gadget items
must destroy `item.Gadget.gameObject` with an uninstalled one and must never destroy an installed one.

### `GadgetBase`: installed projection

`GadgetBase` derives from `TrainCarCustomization.TrainCarCustomizerBase`. It coordinates the owning
`GadgetItem`, the anchor and train, the mount and mount points, the wiring module and components,
glass placement, removal restrictions and the train requirements.

`Gadget.Custom != null` indicates that the projection is installed. That is useful for observation
but insufficient for network idempotency.

### `Customization`: target and collection

`Customization` owns the linked customizer list. `Link` runs `OnBeforeLinked`, where
`IsValidTarget` sets `ArePlacementRequirementsMet` (`C:48-51`); assigns `Index` to the current list
count (`C:95`); adds the customizer; and fires link hooks. `Unlink` fires before hooks, removes it,
resets `Index` to `-1`, fires after hooks and clears `Custom`.

`Index` is never renumbered when another customizer unlinks, so it goes stale and can repeat. Its only
use is `IsLinked` (`Index >= 0`).

`GetParentingTransform()` normally returns the customization transform. The train specialization
returns `TrainCar.interior`.

`Customization` separately owns holes (`C:312-406`): a `HashSet<Collider>` of `drilled_hole`
colliders on the `Interactable` layer under the parenting transform. They have no identity; they are
found by position with `FindHole(localPos, radius)` and saved as `{pos, dir}` pairs.

## Placement validation

Input (`GI:225-242, 368-416`): while the item is grabbed, `Update` previews the placement. Non-VR
places on the use click; VR places on trigger release (raycast) or after a one-frame proximity
overlap (`GI:450-591`). The mouse wheel rotates in 15° steps or cycles mount attachment positions.

`TryPlaceOnHit` (`GI:632-779`) checks:

- the `MountingGadgets` feature flag and the tutorial allow-lists;
- the hit layer is `Default`, `Terrain` or `Gadget_Mesh_Placing`, or the hit is a `Mount`;
- a gadget that has a `Mount` cannot go onto a mount;
- mounts: `RequiredMountPoints > 0` needs a mount; the host `Drillable` has enough mount points and
  enough taped or bolted ones; the mount is not in use;
- `Customization.Resolve(hit)` finds an anchor;
- unless `StrictPlacementMode` (tutorial): `CanPlace`, a `Physics.CheckBox` of the bounds deflated by
  5 mm (`GI:781-784`), and the hit is not a hole;
- broken-window colliders are filtered out (`GI:418-434`); the reach is a 3 m raycast.

It **does not** call `IsValidTarget`. Train requirements (`requirements.cabin`, `simPorts`,
`controls`, the fuse, MU support) and reservation groups are checked only when the projection links,
and a failure only clears `ArePlacementRequirementsMet`. A gadget that fails them still installs; it
then can't be soldered (`GadgetSolderingTool.cs:259`) and some implementations (DPU, overheat
protection) stay inert. `requirements.trainCarPresence` is declared but never read.

### Placement colliders depend on the local player

The `Gadget_Mesh_Placing` colliders that interior placement and the drill/tape surface checks hit
are copies built at car setup from the interior prefab (`TrainCarColliders.cs:415-432`). They are
active only within a frame of a placing query **and** while `PlayerManager.PlayerTransform` is within
20 m (xz) of the car (`GadgetColliderHolder.cs:40-56`; it returns early when there is no player).
A game with no player near the car (a dedicated host, a listen host far away) answers `CanPlace`,
`GeneratePlacementData` and `Drillable.CheckIfCanChangeToState` differently.

## Installation lifecycle

`TryPlace` (`GI:786-808`) rechecks `CanPlace`, converts the pose into the anchor's parenting space,
instantiates a new item when the context says so (the label maker), and calls the static
`GadgetItem.Place` (`GI:323-347`):

1. Parent the projection to `destination.GetParentingTransform()` and set the local pose.
2. `Link` (allocates a UID if it is 0, binds the fuse, raises the power state, sets the LOD state).
3. Activate the projection; `GeneratePlacementData(hitCollider)`: `IsOnGlass` is true when the hit
   has `LocoWindowMesh`. `Drillable` overwrites it with false and sets each mount point's `IsOnGlass`
   from an `OverlapSphere` on `Gadget_Mesh_Placing` (`Drillable.cs:146-156`).
4. `ForceEndInteraction` on the item.
5. `Inventory.DropItemFromHandsOrInventory` and `PurgeFromInventory` on **the local `Inventory`
   singleton**.
6. `StorageController.AddItemToInstalledGadgets(item)`: remove from other storage, add to
   `StorageInstalledGadgets`, `SetActive(false)` (`StorageController.cs:132-136`).
7. `ForceRemoveFromActivityHandler`.

Then `mount.MountGadget(gadget)` if a mount was hit (`GI:760-763`), and the place sound.

The item's own transform is not moved; it keeps the pose where it was held.

## Removal lifecycle

`GadgetBase.Remove(reparentToTrainCar)` (`GB:264-297`) fails if the gadget is not linked or no
removal method is allowed (only a `Mount` in use returns `None`). Otherwise:

1. `Unlink`, which:
   - turns the LODs off;
   - fires `BeforeUnlinked`: `Drillable` produces a hole in the anchor at each **bolted** mount
     point and resets every point to `None` (`Drillable.cs:134-144, 191-208`);
   - runs `OnBeforeUnlinked`: unmount, unsubscribe train and window events,
     `GadgetWiringModule.UnwireAll()`, unbind the fuse, `UID = 0`, **soldering = 0**
     (`TCC:339-347`, `GB:462-479`);
   - removes the projection from the list and fires `AfterUnlinked`, where `DestroyOnGadgetUnlink`
     (label gadgets) destroys the item and spawns or activates a replacement
     (`DestroyOnGadgetUnlink.cs:20-33`).
2. Move the item to the projection's world pose.
3. `StorageController.MoveFromInstalledGadgetsToWorld(item)`: remove from installed storage, add to
   `StorageWorld` only when `BelongsToPlayer()`, activate the item (`StorageController.cs:159-171`).
4. Parent the item to `trainCar.interior` with the car rigidbody when requested and on a car,
   otherwise to `WorldMover.OriginShiftParent`.
5. Detach and deactivate the projection.

The item keeps whatever velocity it last had and can be thrown across the cab. A re-install is a
fresh node: a new UID, unsoldered, no wires.

`ForceRemove` fires `ForceRemoveCalled` (a `Mount` removes the gadget mounted on it first,
`Mount.cs:124-131`) and then calls the normal path.

## Player actions

Every action below runs only in the game of the player holding the tool, by plain method calls with
no events in between. Each is deterministic once its result is known; the minimal command is what a
server would need to reproduce it.

Tools derive from `GadgetInteractor` (`GadgetInteractor.cs:152-218`): while held, a 3 m raycast on
the `Interactable` layer from the camera or the VR interaction point resolves
`GetComponentInParent<GadgetBase>()`, and `ItemBase.Used` sets a one-frame `use` latch if
`GadgetSystemUtility.CheckGadgetAgainstRestrictions` passes.

| Action | Entry point | State change | Minimal command |
| --- | --- | --- | --- |
| Install | `GadgetItem.TryPlace` from the held item's use or release | [Installation lifecycle](#installation-lifecycle) | item, anchor key, local position and rotation, mount host UID, the on-glass flags of the gadget and each mount point |
| Remove with the hammer | `GadgetRemover` (method `Remover`, flag `HammeringGadgets`): non-VR after a work animation, VR on a click or a fast swing (`GadgetRemover.cs:69-131`) | `Remove(true)` | anchor, UID, method |
| Remove by hand | `GadgetHandNonVR` (3 m raycast, empty hand) or `GadgetHandVR` (grabbing the projection); allowed only if the prefab permits `EmptyHand` and no mount point is taped or bolted (`Drillable.cs:35-43`) | `Remove()` then force-hold the item (`GadgetHandNonVR.cs:21-70`, `GadgetHandVR.cs:22-58`) | as above, and "the item goes to this player's hand" |
| Remove by telegrab | VR, `EmptyHand` prefabs: `TelegrabbableGadget` redirects to the item; telegrab start calls `ForceRemove()` (`GB:182-224`) | `ForceRemove()` | as hand removal |
| Bolt a mount point | `DrillTool` (`MountHoleInteractor`): nearest point within 2.5 cm of the ray; `CheckIfCanChangeToState(i, Mounted)` needs a non-gadget surface within 2 cm and no glass (`Drillable.cs:89-132`); progress local | `Drillable.SetMountPointState(i, Mounted)` | item, point index, state |
| Tape a mount point | `DuctTape`: same check, glass allowed unless broken; `ConsumeOneUse()` (`DuctTape.cs:101-206`) | `SetMountPointState(i, Taped)`; `uses` minus one; at 0 the tape item is destroyed and an empty-tape item is instantiated and equipped | item, point index, state, tape item |
| Untape | empty-hand click on a taped point, only when `Remover` is allowed and `EmptyHand` isn't (`GadgetHandBase.cs:12-57`) | `SetMountPointState(i, None)`; the gadget stays installed with no attached points | item, point index |
| Fill a hole | `HoleFiller`: non-VR after a work animation, VR immediately (`HoleFiller.cs:62-110`); unlimited uses | `Customization.RemoveHole(collider)` | anchor, hole local position |
| Wire or unwire | `GadgetWiringTool.OnUsed` (`GadgetWiringTool.cs:173-220`): first click picks the source, second click on another gadget wires it, or unwires it if already wired | `WireLinkPort.Wire` / `Unwire` | anchor, source UID, target UID |
| Solder | `GadgetSolderingTool.ProcessSoldering` every frame while used (`GadgetSolderingTool.cs:307-348`) | `target.MakeSoldered(units)` with `units = (int)(65536 · 0.5 · Time.deltaTime)`, clamped to the tool's remaining units; the first use of a fresh spool destroys it and instantiates an empty spool in the tool's magazine | gadget, explicit units, tool |
| Load a solder spool | non-VR `IItemUse.HandleUse`; VR `GadgetSolderingToolReloadVolume` | spool into the tool's `ItemMagazine`; `remainingUnits = 72089` (65536 plus a 6553 bonus); ejectable at 6553 or less; -1 means spent | item-container move |
| Label | `LabelMakerController` (`GenericGadgetSpawner`, `instantiateGadget: true`): `TryPlace` instantiates a **new** item and installs it, then a text popup; cancel or empty text calls `DestroyOnGadgetUnlink.Disable()`, `ForceRemove()` and `Destroy(item)` (`LabelMakerController.cs:138-161`) | item create, install, text, or create and destroy | prefab, anchor, pose, text |

Local-only effects: preview highlights (`Graphics.DrawMesh`), wire line renderers, interaction
text, sounds (place and remove are positional, played only by the actor), particles, haptics and work
animations. `HUDGadgetManager` refreshes itself from link events.

## Forced and automatic changes

| Trigger | Path | Notes |
| --- | --- | --- |
| Car deleted | `CarSpawner.DeleteCar` → `InteriorOnDestroy` → `TrainCar.InteriorAboutToBeDestroyed` → `GB.OnTrainCarDestroyed` → `ForceRemove(false)` (`GB:487-493`, `CarSpawner.cs:668-728`) | The items survive as world items at the gadget's pose. Covers job and unused-car deletion, debug delete and loco restoration, which only calls `DeleteCar`. Skipped while unloading. |
| Pooled car reused | `ReturnCarToPool` never calls `ClearHoles` (`CarSpawner.cs:275-290`, `TrainCar.cs:910-992`) | Vanilla bug: a pooled car keeps its holes, including those its force-removed gadgets just made, until `Deserialize` runs. |
| Window glass breaks | collision damage above 1200 → `WindowsBroken` (`WindowsBreakingController.cs:109-148`) → `GB.OnGlassBroken` (`GB:240-251`) | `Drillable` resets glass points to `None` without an event and force-removes the gadget if no point is attached; then any gadget with `IsOnGlass` is force-removed. `RepairWindows` re-attaches nothing. Gadgets saved on broken glass survive load. |
| Collision | `TrainCarCustomization.OnCollisionEnter` (`TCC:544-564`) | **Random**: `p = InverseLerp(0.09, 9, Δv²)` for Δv between 0.3 and 3 m/s; each gadget with a `Drillable` and no bolted point is force-removed when `p >= Random.value`. Taped-only gadgets can fall off; gadgets without `Drillable` never do. |
| Mount host removed | `Mount.OnForceRemoveCalled` | Removes the mounted gadget first. |
| Hanger flips | `Dangler` swings with the train and force-unsnaps its item when upside-down | Per-frame physics with a random kick on snap. |
| Interior unload, physics LOD | `TrainCar.UnloadInterior` destroys only the cab prefab instance; gadgets and holes are under the persistent `TrainCar.interior` (`TrainCar.cs:1235-1316`). `TCC.PhysicsLODChanged` only toggles LOD children (`TCC:469-481`) | Never destroys or recreates gadget state. |
| World LOD | `WorldCustomization.Update`, camera within 128 m | Presentation only. |

## Persistence pipeline

### Save

`SaveGameManager.UpdateInternalData` saves `StorageInstalledGadgets` under
`"Storage_InstalledGadgets"` (`SaveGameManager.cs:280`). Installed gadgets are found **only**
through that list; a car has no list of its gadgets. Each entry's `state` is the item's
`ItemSaveData`:

```text
state
├─ placedOn   CarGUID | ":global:" | ":storage:" | ":player_house:" | ":paint_station:" | "" (uninstalled)
├─ position   local position under GetParentingTransform()            [installed only]
├─ rotation   local Euler angles                                      [installed only]
└─ gadgetData
   ├─ <GadgetComponent keys, in GetComponents order>
   │    Drillable "holes":   [{ "state": 0 None | 1 Mounted (bolted) | 2 Taped, "onGlass": bool }] per mount point
   │    Mount     "mounted": UID of the mounted gadget, 0 = none
   │    <implementation keys, see Concrete implementations>
   ├─ "links":       int[] partner UIDs, one per edge ([] when uninstalled)
   ├─ "onGlass":     bool
   ├─ "wiringUnits": int, soldering progress 0..65536 (not wiring)
   └─ "uid":         int, 0 when uninstalled
```

`TrainCarCustomizerBase` saves exactly `uid` and `wiringUnits` (`TCC:377-382`); `GadgetBase` adds
`links` and `onGlass` (`GB:364-389`). The entry's own `itemPosition`/`itemRotation` are the stale
pose of the inactive item and are ignored on load. Other item components add their own keys (for
example `LabelableItem`'s `label_text`).

Car holes are saved with the car: `carsData[i].modCarState = TrainCarCustomization.Serialize()`,
`{"holes":[{"pos","dir"}]}` (`CarsSaveManager.cs:625-629`). Deleted unique cars keep theirs through
`deletedUniqueCarLiveryToLastCarState`. World holes are saved under `"Customizers"`. House, shed and
paint station holes are never saved.

### Load

`StartGameData_FromSaveGame` loads cars first (`CarsSaveManager.Load` spawns them synchronously and
`Deserialize` runs `ClearHoles` and `AddHole`), then world holes, then items
(`StartingItemsController.AddStartingItems`): installed gadgets are instantiated first, one frame is
yielded so `GadgetItem.Start` runs, then every item's `LoadItemData`, then every item's
`PostLoadItemData` (`StartingItemsController.cs:77, 665-686`).

For a gadget, `LoadItemData` is one callback (`GI:271-300`):

1. Read `placedOn` and the pose, resolve the anchor with `Customization.TryGetFromIdentificationKey`
   (for a car: `IdGenerator.carGuidToCar` → `TrainCarRegistry` → `TrainCar.Customization`), and call
   `Place`. `Link` allocates a throwaway UID here.
2. If the anchor or pose can't be resolved, move the item to Lost and Found
   (`StorageController.AddItemToLostAndFound`). `SaveDataLoaded` still runs on the unlinked projection
   and restores its UID and soldering, which it keeps on a later install.
3. `Gadget.SaveDataLoaded(gadgetData)`: components restore their state; the base restores `uid`
   (`AssignUID`) and soldering (`MakeSoldered(wiringUnits)`), then wires each saved link it can resolve
   through `Custom.TryGetCustomizerByUID` and the first compatible port pair (`GB:400-412`).

A link to a partner that hasn't loaded yet is skipped without a log line. Because both ends save the
edge, the partner wires it when it loads, so wiring tolerates any order. `PostLoadItemData` →
`AfterSaveDataLoaded` binds `Mount` (`Mount.cs:141-159`), restores the alternating controller's order
and `TextGadget`'s text, and binds `SnappableItem`s to hangers.

`SaveDataLoaded` is **not idempotent**: called again on a linked gadget it logs errors for existing
wires, never removes stale ones, and adds soldering again. It is safe only on a freshly placed
projection, as in a load.

## Identity model

| Identity | Scope and purpose |
| --- | --- |
| Persistent item identity | The underlying gadget item across saves and materialization. |
| Anchor identification key | Placement anchor; `CarGUID` for a car, or a singleton key. |
| Customizer `UID` | Node identity within one anchor: wiring `links`, `Mount.mounted`, `SnappableItem`'s `SnappedOnHangerUIDKey`. |
| Customizer `Index` | Set once at link and never renumbered; stale and repeatable. Never an identity. |
| Item/car NetId | Compact session identity (the mod's). |

`uidCounter` is one `private static int` on `TrainCarCustomizerBase` (`TCC:79`), shared by every
anchor in the process and never reset, not even across loads. `OnAfterLinked` allocates
`++uidCounter` only when `UID == 0` (`TCC:323-328, 400-403`); `AssignUID(uid)` sets the UID and raises
the counter to at least that value (`TCC:405-412`); unlink sets it back to 0. So:

- within one process new UIDs never collide with any UID seen;
- two processes allocate independently, and their UIDs for the same gadget will differ;
- a UID set with `AssignUID` **before** `Link` is kept, because `OnAfterLinked` sees it non-zero;
- `TryGetCustomizerByUID` (`C:298-310`) is a linear scan of one anchor returning the first match, so
  duplicates resolve silently to the wrong gadget.

## Mounting and holes

`Drillable` holds a gadget's mount points. Each point has a state (`None`, `Mounted` = bolted,
`Taped`) and an on-glass flag. `AttachedPointCount` counts taped and bolted points;
`FirmlyAttachedPointCount` counts bolted ones. Drilling does **not** make a hole in the anchor; it
bolts the gadget's own point. Holes in the anchor appear only when a gadget with bolted points is
removed, and the hole filler removes them.

`Mount` is a gadget that other gadgets mount onto (a plate). A mounted gadget needs
`RequiredMountPoints` attached points on its host. `Mount` saves the mounted gadget's UID and binds it
after load. While a gadget is mounted, the host can't be removed by any method.

## Power

`PowerState = powerSwitch && (fuse?.State ?? !requireSoldering)` (`TCC:111-121`).

- `fuse` is the car's electronics fuse, bound in `OnAfterLinked` and `MakeSoldered` only once the
  gadget is soldered or doesn't need soldering. Off a train there is no fuse, so solderable gadgets
  are never powered there.
- `powerSwitch` defaults to true. Only `ExternallySwitchableGadget` changes it:
  `sw == null || sw.OutputValueOf(this) > 0`. It is not saved.
- Changes propagate synchronously: fuse `StateUpdated`, the power switch or soldering →
  `RaisePowerStateChanged` → the subclass, then each LOD, then the `PowerStateChanged` event.
- `PopFuse()` exists but nothing calls it.

## Wiring

`GadgetWiringModule` holds topology only and fires `onWired`/`onUnwired`; it carries no values and
has no update loop. Implementations register ports with `RegisterWireLink<T>(onWired, onUnwired,
allowMultiple, passive)` (`GB:323-330`) in a fixed order.

- `GadgetSwitch` (and `AlternatingController`) has a passive multi port accepting any `GadgetBase`.
- Consumers (lights, beacons, every `ExternallySwitchableGadget`) have an active mono port accepting
  only a `GadgetSwitch`.
- Ports link only within the **same anchor**; two passive ports can't link; a mono port must be
  empty (`GadgetWiringModule.cs:90-129`).

So the graph is a star of depth 1: each consumer has at most one switch, switch-to-switch and
consumer-to-consumer links are impossible, there are no chains or cycles. `TryGetCompatiblePorts(a,
b)` returns the first compatible pair, iterating a's ports then b's (`:248-269`), so a pair has at
most one edge and the port chosen can depend on direction. Ports are not saved; they are re-picked on
load.

Values flow by push: `GadgetSwitch.SetOutputValue(v)` (clamped 0..1) → `OnOutputValueUpdated` → each
consumer recomputes from `sw.OutputValueOf(consumer)` (`PowerState ? outputValue : 0`). A light's
value is `PowerState ? (sw ? sw.OutputValueOf(this) : 1) : 0`, so an unwired light is fully on when
powered. Every output is therefore a function of the edges, the switch values and each node's power,
except `AlternatingController`, which is driven by `Time.deltaTime` and the order of its subscribers.

## How gadgets write the train

- **Ports.** `TryReadPort`/`TryWritePort(STDSimPort, value)` (`TCC:248-276`) read and write
  `Port.Value` on the car's simulation through the `standardizedPorts` map on the car prefab
  (`TCC:488-506`). Only the amp limiter writes a port (`TractionMotorAmpLimit`).
- **Controls.** `Controls` is the sim's `BaseControlsOverrider`. `control.Set(v)` →
  `ExternalValueUpdate(clamp01(v))` unless a `ControlBlocker` blocks it. `control.Move(notches)`
  (`OverridableBaseControl.cs:93-106`): with a loaded cab interior, `InteriorControlsManager.
  MoveScrollable` scrolls the **cab lever** one step per notch and the lever writes the port; with no
  interior, `Set(Value + notches)` for a non-notched control (**a jump to 0 or 1**) or
  `Set(Value + notches / NotchCount)` for a notched one.
- `InteriorControlsManager.TryUnhandControl(type)` makes the local player let go of a lever the gadget
  is fighting.

Anti-slip reads the throttle's notch count from the loaded interior, which falls back to 1 with no
interior.

## Concrete implementations

`GadgetBase` stays active while linked, and all logic that reads or writes the train is in its
`Update`, regardless of distance. `CustomizerLODObject` children hold the visuals and the physical
controls and load only at physics LOD 0. Input reaches a gadget two ways: the LOD's physical controls
(`ControlImplBase.ValueChanged`/`Used`, `SteppedJoint.PositionChanged`, `JoystickDriver.ValueUpdated`)
and the loco HUD (`HUDGadgetManager` rents a `GadgetHUDModule` per gadget), which writes straight to
the base setters and then calls `gadgetLOD.SyncControls()`. `SyncControls` sets the controls, which
raise `ValueChanged`, which calls the setter again.

| Gadget | Classes | Player state and input | Saved keys | Reads | Writes | Diverges by |
| --- | --- | --- | --- | --- | --- | --- |
| Switch (rotary, button, lever, slider, analog) | `GadgetSwitch`, LOD, HUD | `outputValue` 0..1: knob, HUD toggle or ±0.1 | `value` | power | consumers' power switch | knob physics |
| Alternating controller | `AlternatingController : GadgetSwitch`, LOD, HUD | `SelectedInterval` 0..4 (off, 1 s, 0.5 s, 0.25 s, steady) | `value` (interval), `order` (subscriber UIDs) | power | which consumer is on | `Time.deltaTime` phase, wiring order |
| Light, ditch, head, tail, light bar | `GadgetLight` | none (colour is an item attribute) | none | power, switch | none | none |
| Oscillating light, beacon | `GadgetOscillatingLight`, `GadgetBeacon` | none | none | power, switch | none | cosmetic motion |
| Switch setter | `GadgetSwitchSetter`, LOD, HUD | mode (50..300 m), direction, side knobs; change button → `Junction.Switch` | `mode`, `sideCorrection`, `dir` | reverser, consist, bogie track (LOD, every 0.5 s) | a world junction | bogie position near a junction boundary |
| Brake LED bar, brakes thermometer, clinometer, digital clock, digital speedometer | `GadgetBase` + LOD | none | none | brake cylinder pressure, brake heat, car transform, game time, `WheelSpeedKMH` | none | none |
| Defect detector | `GadgetBase` + LOD (state in the LOD) | snooze button | none | derail events, brake overheat event | none | none |
| Amp limiter | `GadgetAmpLimiter`, LOD, HUD | mode 0..7 (off, 1600..200 A) | `mode` | `TractionMotorAmpLimitEffect` | port `TractionMotorAmpLimit` on mode or power change; +∞ on unlink | none |
| Anti-slip | `GadgetAntiSlip`, LOD, HUD (indicator) | none | none | wheelslip, throttle, sander, notch count from the interior | `Throttle.Move(±1)`, `Sander.Set`, `TryUnhandControl` | timers, interior-dependent `Move` |
| Overheat protection | `GadgetOverheatProtection`, LOD, HUD | mode 0..8 (120..80 °C), cut switch | `mode`, `cut` | `Temperature`, control events | `PowerOff.Set(1)` or `Throttle`/`DynamicBrake.Move` every 0.5 s, `TryUnhandControl` | timers, interior-dependent `Move` |
| ATS | `GadgetATS`, LOD, HUD | regime 0..5 (off, 30..240 s) by stepped knob; acknowledge button resets `currentTimer` | `mode` | `WheelSpeedKMH`, whether a remote controller is in control | when expired, every 0.2 s `Throttle.Move(-1)`, `Brake.Move(+1)`, release the cab levers | `currentTimer -= dt`, not saved |
| Roadrunner | `GadgetRoadrunner`, LOD, HUD | length 0..2000 m; start and acknowledge buttons | `countup`, `target` | `WheelSpeedKMH`, integrated over `dt` | none | `dt` integration |
| DPU, wireless MU | `GadgetDPU`, LOD, HUD | regime (transmit, off, receive), channel 0..7, reverse | `regime`, `channel`, `reverse` | `muModule.RemoteChannel` | `muModule.SetRadioChannel`: a transmitter MU-overrides every receiver on its channel anywhere on the map and blocks their controls | none |
| Control panel (universal control stand) | `GadgetControlPanel`, `GadgetControlPanelLODObject` | tilt; 11 joysticks → `Controls.Move`; horn, starter, power-off | `tilt` | many ports, brakes, MU observers, rain | all of those controls | `Random.value` rain fault every 0.125 s blanks the display and blocks input |
| Proximity screen | `GadgetProximityScreen`, LOD | channel 0..8, mode | `channel`, `mode` | sensors, reverser | none | none |
| Text (labels) | `TextGadget : GadgetComponent`, `LabelMakerController` | text from a popup | `text` (after load) | none | none | none |
| Flag marker | `GadgetFlagMarker` | none | none | none | none | none |
| Hanger | `GadgetWithSnapPoint`, `SnapPointGadget`, `Dangler` | items snapped onto it | on the snapped item | train velocity | forced unsnap when upside-down | swing, random kick; `OnSnap` reads `PlayerManager.ActiveCamera` |
| Shelf, shovel mount, sliding snap points | `SnapPointGadget`, `SnapPointGadgetSliding` | snap an item; the sliding anchor offset comes from sphere casts | not saved separately | none | none | local physics casts |
| Remote signal booster | `RemoteControllerSignalBooster` | none | none | none | while its object is enabled (presumably switched by power through `EnableOnGadgetPower`; unconfirmed) it is in a static list; a `LocomotiveRemoteController` within its `range` (2000 m) of a booster gets the extra range (`LocomotiveRemoteController.cs:859-861`), checked in the game of the player holding the remote | none |

Sun visors, nameplates and the other purely cosmetic gadgets have no class of their own: they are
plain `GadgetBase` prefabs with generic components. The prefabs themselves (requirements,
`requireSoldering`, removal methods) can only be listed in game.

The battery charger (`ChargerBatteryPowerProvider`) charges any `Battery` in its trigger every 0.1 s
in every game.

### The game's own list

The game ships its manual offline (`DerailValley_Data/StreamingAssets/manual/<lang>.json`). Its gadget
pages, matched to the classes above:

| Manual name | Class | Needs soldering (manual) |
| --- | --- | --- |
| Amp Limiter | `GadgetAmpLimiter` | yes |
| Anti-wheelslip Computer | `GadgetAntiSlip` | yes |
| Automatic Train Stop | `GadgetATS` | yes |
| Battery Charger | `ChargerBatteryPowerProvider` | yes |
| Clinometer | `GadgetClinometerLOD` | no |
| Defect Detector | `GadgetDefectDetectorLOD` | yes |
| Distance Tracker | `GadgetRoadrunner` | yes |
| Infrared Thermometer | `GadgetBrakesThermometerLOD` | yes |
| Overheating Protection | `GadgetOverheatProtection` | yes |
| Remote Signal Booster | `RemoteControllerSignalBooster` | yes |
| Switch Setter | `GadgetSwitchSetter` | yes |
| Universal Control Stand | `GadgetControlPanel` | yes (MU vehicles only; malfunctions when wet) |
| Wireless MU Controller | `GadgetDPU` | yes (MU vehicles only; the MU licence to buy) |
| Proximity Sensor and Proximity Reader | `ProximitySensor` (coupler item), `GadgetProximityScreen` | the reader |
| Controllers: button, rotary, lever, slider, analog, alternating | `GadgetSwitch`, `AlternatingController` | yes |
| Cosmetic: lights (head, tail, ditch, lightbars, beacons), shelves, sun visors, hangers, nameplates, flags, labels | `GadgetLight`, `GadgetBeacon`, `GadgetOscillatingLight`, `SnapPointGadget`, `GadgetWithSnapPoint`, `GadgetFlagMarker`, `TextGadget` | lights yes, the rest no |

The brake LED bar, digital clock and digital speedometer exist in code with no manual page. Tools:
electric hand drill, duct tape, hammer, filler gun, soldering gun and wire, crimping tool (the wiring
tool), label maker. The manual also says gadgets on a despawned company (DVRT) vehicle "return to a
tool shed"; the code for that hasn't been found yet.

Almost every gadget needs soldering into a vehicle's electrics, and only vehicles have a fuse, so off
a train only the unpowered cosmetics and the clinometer do anything.

## Presentation and train lifecycle

Projections parent to `TrainCar.interior`, so they follow the train and floating-origin movement.
Physics LOD toggles their LOD children only. Train destruction is a logical change: gadgets
force-remove, items return to loose world placement, wiring is torn down, and bolted points become
holes in the car.

## Separate attachment families

EOT lanterns, proximity sensors (`ProximitySensor` on a `SnappableItem` snapped to a coupler, with
`channelKnob` and `rangeKnob` item save keys) and items hung on gadget hangers use the
`SnappableItem` system; they stay active items rather than installed projections. A hanger snap
saves `SnappedOnHangerCarID` and `SnappedOnHangerUIDKey` (`SnappableItem.cs:140-180`): a gadget UID
inside another item's state. See
[`DERAIL_VALLEY_ITEM_SNAPPING_LOGIC.md`](DERAIL_VALLEY_ITEM_SNAPPING_LOGIC.md).

`LabelableItem` puts a label on an ordinary item (`label_text` in its save data) and is not a gadget.

## Prior implementation: the feat/gadget-sync fork

Patrick van Halm's `feat/gadget-sync` branch (github.com/Patrick-van-Halm/dv-multiplayer, 13 commits
from 2026-08-15 to 2026-09-13 on an older base) syncs gadgets in a player-hosted game. Its hook set is
right and it found real quirks; its trust model and state transport are not reusable.

Hooks: `GadgetItem.Place` (postfix), `GadgetBase.Remove` (prefix and postfix),
`Customization.AddHole`/`MoveHole`/`RemoveHole`, `Mount.MountGadget`/`UnmountGadget`,
`WireLinkPort.Wire`/`Unwire`, the soldering tool's `ProcessSoldering` and `DropEmptySpool`,
`ItemMagazine.AddItem`, `DuctTape.ConsumeOneUse`, `ItemSnapPointBase.SnapItem`/`UnsnapItem`.
Gadgets are identified by their item's network id; mounts, ports and snap points by index; anchors by
identification key. Per-gadget settings are polled item values.

Quirks it found, confirmed in the decompile: `GadgetItem.Item` is set only in `Start`; `Wire` fails
silently across anchors or onto a full mono port; the alternating controller's output order is the
wire order; a removed item keeps its velocity; solder spool units are 72089 loaded, ejectable at 6553
or less, -1 spent; sliding snap checks depend on physics queries; forced snaps need a non-kinematic
rigidbody. Its most useful idea is the item swap for spools and tape: the client keeps its native
replacement and the host assigns the id.

What not to copy: clients are trusted without checks; every peer publishes every polled value, so
continuous values (the alternating phase, the roadrunner count) are written by every peer; forced
removals (car deletion, glass, the random collision) run and broadcast in every game; actions can go
out with item id 0; a late-join snapshot tears down every relationship globally; there is no dedicated
host support. Its useful output is the hook map and the quirks.
