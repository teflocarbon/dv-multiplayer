# Derail Valley Gadget and Train Customization Logic

This document records established behavior in Derail Valley's installed-gadget,
train-customization, mounting, wiring, and persistence systems. Gadgets are not ordinary loose world
items: installation replaces an active item representation with a linked customization projection
while retaining the underlying item as the persistent record.

The multiplayer authority boundary is described in
[`MULTIPLAYER_ITEM_WORLD_SYNC_DESIGN.md`](../MULTIPLAYER_ITEM_WORLD_SYNC_DESIGN.md). Generic loose
world-item behavior is recorded in
[`DERAIL_VALLEY_WORLD_ITEM_STREAMING_LOGIC.md`](DERAIL_VALLEY_WORLD_ITEM_STREAMING_LOGIC.md).
Items snapped to couplers or gadget-hosted points are documented in
[`DERAIL_VALLEY_ITEM_SNAPPING_LOGIC.md`](DERAIL_VALLEY_ITEM_SNAPPING_LOGIC.md).

## Sources inspected

- `DV.Customization.Gadgets.GadgetItem`
- `DV.Customization.Gadgets.GadgetBase`
- `DV.Customization.Customization`
- `DV.Customization.TrainCarCustomization`
- `StorageController.AddItemToInstalledGadgets`
- `StorageController.MoveFromInstalledGadgetsToWorld`
- `StorageSerializer.SaveStorage`
- An archived third-party networked-customization diff, used only to locate integration points

Implementation-time validation targets:

- `GadgetWiringModule`, wire ports, compatibility, and edge teardown;
- `GadgetComponent` and important subclasses;
- `GadgetSystemUtility` placement/removal restrictions;
- `Mount`, reservation groups, `Drillable`, and required mount points;
- gadget placement/removal tools and their input entry points;
- any remaining non-gadget/non-snappable train attachment systems;
- wired electrical propagation and multiplayer authority expectations;
- UID collision behavior after loading saved customizers.

These remaining targets are implementation-time validation, not blockers for the generic world-item
catalogue.

## Namespace and subsystem boundary

The complete type inventory confirms that gadget placement is concentrated under
`DV.Customization` and `DV.Customization.Gadgets`.

### Customization anchors and ownership

`DV.Customization` contains the target/anchor families:

- `TrainCarCustomization`;
- `WorldCustomization`;
- `PlayerHouseCustomization`;
- `StorageShedCustomization`;
- `PaintStationCustomization`;
- static-parent and singleton customization bases; and
- `CustomizerLODObject` presentation helpers.

These classes determine stable customization identity, parenting, target lifetime, and LOD. They do
not create a separate generic world-item family.

### Gadget infrastructure

`DV.Customization.Gadgets` contains the shared lifecycle and validation layer:

- `GadgetItem`, `GadgetBase`, and `GadgetComponent`;
- `GadgetWiringModule`, wiring tools, and soldering resources;
- `Mount`, `MountPoint`, drilling, holes, and attachment options;
- placement, interaction, highlighting, and removal tools; and
- collider and unlink cleanup helpers.

Only this shared layer needs complete scrutiny before implementing host-validated gadget
transactions. It defines the invariants common to every installed gadget.

### Concrete implementations

`DV.Customization.Gadgets.Implementations` contains concrete behavior such as switches, lights,
sensors, DPU/ATS devices, displays, controllers, snap-point gadgets, and their LOD projections.
These generally layer device state and presentation over the established `GadgetBase` lifecycle.

They should not each become a new item placement type. Their state should flow through one of:

- native `GadgetComponent` save/load data;
- the authoritative wiring graph;
- synchronized train simulation/control state; or
- locally derived LOD/presentation.

Individual implementations only require deeper research when they introduce a new external
authority transition, persistent identity, or nondeterministic state that cannot be derived from
already synchronized inputs. A suggestive class name such as `ProximitySensorNetwork` is not, by
itself, evidence of multiplayer networking.

### Paint

`DV.Customization.Paint` is a separate appearance subsystem (`PaintTheme` and `TrainCarPaint`). It
does not alter item placement and remains outside gadget/world-item authority. Existing paint sync
can continue to own it.

## Executive conclusions

1. An installed gadget consists of an underlying `GadgetItem` and a separate `GadgetBase`
   projection.
2. Installation moves the underlying item into `StorageInstalledGadgets` and deactivates it. The
   linked projection remains active on the target customization.
3. Removal unlinks and deactivates the projection, reactivates the item, moves it out of installed
   storage, and parents it to the train interior or world root.
4. `TrainCarCustomization.GetIdentificationKey()` returns `TrainCar.CarGUID`; this is the durable
   train-customization anchor in gadget save data.
5. A customizer's `Index` is mutable list position. Its saved `UID` is used for intra-customization
   wiring references.
6. Persistence has three phases: placement, state/wiring load, and after-load callbacks. Wiring
   cannot resolve safely until all referenced gadget nodes exist.
7. Mounting, drilling, reservations, target requirements, collision bounds, reach, glass, train
   destruction, physics LOD, fuses, and simulation ports participate in this subsystem.
8. EOT lights use the separate `SnappableItem` lifecycle. Brake LED bars and wired lights are
   confirmed installed gadgets. Other attachment families still require classification as found.

## Object model

### `GadgetItem`: persistent item record

`GadgetItem` requires `ItemSaveData` and `InventoryItemSpec`. During `Awake` it subscribes to the
item-save request, load, and after-load events; instantiates its configured `GadgetBase` prefab;
deactivates the projection; and calls `GadgetBase.AssignItem(this)`.

The item therefore owns a reusable installed projection even while it is loose, held, or in
inventory. The active item and active installed projection are alternate presentations of one
persistent object, not two independently authoritative items.

### `GadgetBase`: installed projection

`GadgetBase` derives from `TrainCarCustomization.TrainCarCustomizerBase`. It coordinates:

- the owning `GadgetItem`;
- target customization and train relationship;
- mount, required mount points, and a mount reservation group;
- `GadgetWiringModule` and gadget components;
- window/glass placement state;
- removal restrictions; and
- inherited train-customization requirements.

`Gadget.Custom != null` indicates that the vanilla projection is installed. That is useful for
observation but insufficient for network idempotency because it does not identify the intended
anchor, pose, or revision.

### `Customization`: target and collection

`Customization` owns the linked customizer list. Linking validates the target, assigns `Index` to
the current list count, adds the customizer, and fires link hooks/events. Unlinking fires before
hooks, removes it, resets `Index` to `-1`, fires after hooks/events, and clears `Custom`.

`GetParentingTransform()` normally returns the customization transform. The train specialization
returns `TrainCar.interior`.

`Customization` separately owns drilled holes. Holes can be added, removed, cleared, moved,
serialized, and deserialized. They are customization graph state, not ordinary item transforms.

## Installation lifecycle

The static `GadgetItem.Place` path performs the real transition:

1. Parent the `GadgetBase` projection to `destination.GetParentingTransform()`.
2. Apply local position and rotation.
3. Link the projection to the destination customization.
4. Activate the projection and generate optional placement data.
5. Force the underlying item to stop interaction.
6. Drop/purge it from inventory state.
7. Call `StorageController.AddItemToInstalledGadgets(item)`.
8. Force-remove the item from the activity handler.

`AddItemToInstalledGadgets` adds the underlying `ItemBase` to `StorageInstalledGadgets` and
deactivates its GameObject. The projection is what the player sees; the inactive item remains the
storage/save owner.

This is a multi-system transition. Parenting an item to a train or simply deactivating it would skip
customization registration, mounts, wiring, save ownership, inventory removal, and projection
initialization.

## Placement validation

The native path accounts for:

- feature and gadget-system restrictions;
- interaction reach, normally about 3 metres;
- raycast or proximity target resolution;
- `Customization` resolution and `GadgetBase.IsValidTarget`;
- train requirements, reservations, and target capacity/rules;
- mount and required mount-point availability;
- drilled-hole requirements;
- overlap and placement bounds;
- optional one-axis and strict placement modes; and
- window/broken-glass behavior.

A client may calculate previews, but the host must validate installation against the canonical
actor, item, target, and current revisions. Arbitrary client save JSON is not an install command.

## Removal lifecycle

`GadgetBase.Remove`:

1. Verifies that the gadget is linked and removal is allowed.
2. Retains the current train reference and unlinks the projection.
3. Moves the underlying item to the projection's world pose.
4. Calls `StorageController.MoveFromInstalledGadgetsToWorld(item)`.
5. Parents the item to the train interior when requested and available, otherwise to world root.
6. Detaches and deactivates the projection.

`MoveFromInstalledGadgetsToWorld` removes installed-storage membership, activates the item, and adds
it to `StorageWorld` only when `BelongsToPlayer()` is true. A non-player gadget still becomes an
active world item, but is not a vanilla world-storage record.

`ForceRemove` fires `ForceRemoveCalled` before using the normal removal path. Forced removal occurs
when the train is destroyed, supporting glass breaks, or a sufficiently strong collision dislodges
an inadequately attached drillable gadget. Before unlink completes, the gadget unmounts,
unsubscribes train/window listeners, and calls `GadgetWiringModule.UnwireAll()`. Removal is therefore
also an electrical-graph mutation.

## Persistence pipeline

### Save

`GadgetItem.OnItemSaveDataRequestedInternal` asks the projection to save state and stores:

```text
placedOn    Customization.GetIdentificationKey(), or empty when uninstalled
position    gadget local position
rotation    gadget local Euler rotation
gadgetData  component, UID, wiring, and gadget-specific data
```

`GadgetBase.SaveDataRequested` invokes component save callbacks and base customizer save. Wiring is
stored as an integer `links` array containing linked gadget-owner UIDs. Glass placement is also
stored. `TrainCarCustomizerBase` stores at least `uid` and `wiringUnits`. The result is embedded in
the ordinary `ItemSaveData` payload written by `StorageInstalledGadgets`.

### Load phase 1: placement

`GadgetItem.OnItemSaveDataLoadedInternal` reads `placedOn` and the local pose, resolves the target
through `Customization.TryGetFromIdentificationKey`, and calls `GadgetItem.Place`.

For `TrainCarCustomization`, the identification key is the train's `CarGUID`.

If the target or pose cannot be resolved, vanilla immediately moves the item to Lost and Found. A
multiplayer apply path must defer this callback until the anchor is ready; calling it early invokes
a destructive recovery policy.

### Load phase 2: state and wiring

After placement, `GadgetItem` passes `gadgetData` to `GadgetBase.SaveDataLoaded`. The base restores
the customizer UID and soldering/wiring progress; components restore their own state.

Wiring links resolve through `Custom.TryGetCustomizerByUID(uid)`, then locate compatible ports and
wire them. All referenced projections and restored UIDs must exist before this can reliably finish.

### Load phase 3: after-load

`AfterItemSaveDataLoaded` delegates to `GadgetBase.AfterSaveDataLoaded`, which invokes component
after-load callbacks. The ordering invariant is:

```text
all nodes placed and linked
  -> all UIDs and component state restored
  -> wiring edges resolved
  -> after-load callbacks
```

## Identity model

| Identity | Scope and purpose |
| --- | --- |
| Persistent item identity | The underlying gadget item across saves and materialization. |
| Customization identification key | Placement anchor; for train customization this is `CarGUID`. |
| Customizer `UID` | Saved node identity used for wiring references inside a customization graph. |
| Customizer `Index` | Mutable list position; never persistent/network identity. |
| Item/car NetId | Compact session identity after persistent item/anchor binding. |

`TrainCarCustomizerBase` allocates a UID when linking an object whose UID is zero, restores a saved
UID during load, and resets it to zero on unlink. The allocator is process-local; saved UIDs retain
graph references. Collision behavior after restoring UIDs still needs inspection.

## Train requirements and electrical state

`TrainCarCustomizerBase.IsValidTarget` can require the train interior or car object, controls, a
main electronics fuse, MU support, a cabin, and named standardized simulation ports with read/write
capabilities.

When linked, it binds train/control/fuse state, standardized simulation ports, its power switch, and
wiring progress. `TrainCarCustomization` maps standardized port definitions to train simulation and
can read/write them. Power may therefore derive from train electronics, fuse state, a gadget switch,
soldering completion, and wiring topology rather than one serialized item boolean.

These are related but distinct authority domains:

```text
gadget placement graph
gadget wiring topology
train electrical/simulation state
```

Wiring should be a revisioned graph, not repeatedly embedded in generic item state. A canonical
undirected edge should identify ordered endpoint UIDs and ports so duplicate endpoint save entries
cannot create duplicate network edges.

### Brake LED bar and wired light examples

`GadgetBrakeLEDBarLOD` is a train-aware presentation component attached to a `GadgetBase`. While its
LOD projection is enabled it binds `BrakeWarningChecker` to the installed gadget's train, displays
normalized brake-cylinder pressure when powered, and drives a blinking warning lamp. Placement and
identity remain ordinary `InstalledGadget` state. Its displayed value is derived from canonical
train brake state plus gadget power rather than persisted as an independent item property.

`GadgetLight` directly derives from `GadgetBase`. It registers a wire link to `GadgetSwitch`, derives
its output value from the switch and its own power state, and updates lighting/emission presentation.
Its color is extracted from an attribute on the underlying `GadgetItem`. This confirms that wired
lights belong to the installed-gadget wiring graph; they are not loose or snapped accessories.

## Presentation and train lifecycle

Installed projections parent to `TrainCar.interior`, so they follow train and floating-origin
movement. Train physics LOD controls their presentation; LOD changes must not create or delete
canonical gadget records.

Train destruction is a logical change: gadgets force-remove, underlying items return to loose world
placement, and wiring is torn down. The host must own that transition rather than allowing clients
to dislodge gadgets independently.

## Multiplayer boundary

Generic world-item authority owns the underlying persistent item and broad placement state. The
gadget subsystem owns the installed projection details:

```text
InstalledGadget
  stable customization anchor
  local pose
  gadget/customizer state
  mount and reservation state
  wiring graph references
```

Installation and removal are atomic logical transitions between world/inventory placement and
`InstalledGadget`. Projection construction or cleanup may finalize afterward, but a leftover
projection cannot become a second authority.

Host validation includes actor/item ownership policy, item/target/graph revisions, proximity,
compatibility, mounts, holes, reservations, bounds, train/customization existence, and
wiring/removal restrictions.

Placement received before its anchor exists stays in a deferred-by-anchor queue. Idempotency uses
persistent item identity, anchor identity, and placement revision, not only `Gadget.Custom != null`.
Ownership remains orthogonal to installation.

## Archived implementation archaeology

The archived diff identified useful native hooks: `GadgetItem.Place`, placed-state observation,
native save/load callbacks, customization-key resolution, `CarGUID`, and hole mutation/serialization.

Its protocol must not be copied. It trusted client gadget JSON without full authorization or
revision checks, discarded missing-anchor operations, used a non-null `Custom` as idempotency,
silently changed ownership, embedded complete `JObject` payloads in generic updates, and omitted
uninstall, retirement, duplicate-reconstruction, and hole-graph invariants. The useful output is the
hook/serializer map, not its trust model.

## Separate train attachment families

EOT lanterns and the inspected gadget-hosted accessories use the separate `SnappableItem` system;
they remain active items rather than installed projections. See
[`DERAIL_VALLEY_ITEM_SNAPPING_LOGIC.md`](DERAIL_VALLEY_ITEM_SNAPPING_LOGIC.md).

Coupler/hose attachments, loose train-interior devices, and wired devices outside `GadgetBase` may
still have different save ownership, physics, authority transitions, and interaction entry points.
They should be researched before introducing a common train-attachment abstraction.
