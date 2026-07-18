# Derail Valley Item, Inventory, Storage, and Recall Logic

This document records facts established from the supplied decompiled Derail Valley classes. It is intended to guide multiplayer item synchronization without replacing the base game's inventory implementation.

## Sources inspected

- `DV.InventorySystem.Inventory` (abstract base implementation)
- `StorageController`
- `StorageBase`
- `DV.Items.ItemPositionHandler`
- `InventoryUIController`
- `InventoryItemSpec`
- `InventorySlotState`, `InventoryItemData`, `InventoryActionType`, and `InventoryItemState`
- `RespawnOnDrop`
- `AItemContainer`, `ItemContainer`, and `ItemContainerRegistry`
- `StorageItemTransformController` (lost-and-found presentation)

Two supplied `Inventory` attachments were byte-for-byte identical. The concrete implementation of some abstract presentation methods was not included, but `InventoryItemSpec` establishes the essential-item metadata directly.

## Executive conclusions

1. `Inventory.InventoryStatusChanged` is the best primary inventory transition hook. It identifies the exact item, inventory slot, equip slot, resulting item state, lock/reservation flags, and compound action flags. The multiplayer mod should consume this payload directly instead of scanning every `NetworkedItem` after an inventory notification.
2. Every `StorageBase` exposes exact `ItemAdded(ItemBase)` and `ItemRemoved(ItemBase)` events. These provide cheap, item-specific observation of world, inventory, lost-and-found, installed-gadget, and item-container storage transitions.
3. Essential-item recall normally reuses the same `GameObject`. Dropping an essential item retains it in its inventory slot as a dropped, reserved entry. The UI's Get action passes that same object back to `Inventory.AddItemToInventory`.
4. A dropped or equipped essential item can be physically in `StorageWorld` while its inventory slot still retains a reservation for the same object. Inventory reservation is therefore not a physical location and must be represented separately in a multiplayer manifest.
5. Lost and found is separate from the inventory Get action. It moves the same player-owned `ItemBase` objects between `StorageWorld` and `StorageLostAndFound`, with a transform controller responsible for presenting them at a shed/access point.
6. Item containers are another independent ownership/location layer. `ItemPositionHandler` demonstrates the events needed to follow nesting and inventory state through containers.
7. Save projection and reconstruction are documented separately in [`DERAIL_VALLEY_ITEM_SAVE_LOAD_LOGIC.md`](DERAIL_VALLEY_ITEM_SAVE_LOAD_LOGIC.md). Runtime storage state must not be conflated with persisted placement.
8. `InventoryItemSpec.IsEssential` is an explicit serialized property and is independent of `BelongsToPlayer`. Do not infer essential/recall behavior solely from ownership or prefab name.
9. `RespawnOnDrop` is a second item lifecycle controller. Each process can independently decide that an item is too far away, then move a player-owned item to lost and found, reset a non-player item to its spawn, or destroy it. In multiplayer this decision must eventually be host-authoritative.
10. Physical placement and inventory reservation can overlap for the same Unity object. The exact persisted representation and restoration sequence are documented in the save/load research file.
11. Item containers already provide exact mutation events and a persisted string `ContainerId`. They should be observed directly; periodic inference from object parents is only a reconciliation fallback.
12. Nested-container safety is enforced neither by `AItemContainer` nor by the inventory UI. `ValidItem` checks only direct self-insertion and an item-type mask. Persistent graph validation belongs at the save/load boundary; runtime network operations require the same independent graph-safety rules.

## Inventory structure

The inventory has 36 slots:

- slots `0–11`: hotbar;
- slots `12–35`: backpack;
- in VR, slots `33–35` are belt slots and may remain active in the scene;
- one equipped hand in non-VR and two equipped hands in VR.

Internally it keeps:

- `InventoryItemData[36] InventoryItems`;
- `GameObject[] EquippedItems`;
- `HashSet<GameObject> inventoryItemsExcludingDropped`;
- `HashSet<GameObject> droppedItems`;
- `Dictionary<GameObject, int> lockedAndReservedMap`;
- an `ItemContainerRegistry`.

`Contains(item, includeDropped)` searches the slot array. A dropped reserved item is still present when `includeDropped` is true, but is excluded when false.

### Inventory status event

The event signature is:

```csharp
InventoryStatusChangedDelegate(
    InventorySlotState primarySlotState,
    InventoryActionType primaryActionType,
    InventorySlotState secondarySlotState,
    InventoryActionType secondaryActionType)
```

The action is a flag set, not a single enum value. Known relevant flags include:

```text
Add
Drop
Purge
Equip
Unequip
Move
Swap
Reserve
Unreserve
Lock
Unlock
Destroy
```

The exact numeric flags are:

```text
Add=1, Drop=2, Move=4, Swap=8, Purge=16
Equip=32, Unequip=64
Lock=128, Unlock=256
Reserve=512, Unreserve=1024
Destroy=2048
BeltVisible=4096, BeltHidden=8192
BeltDisabled=16384, BeltEnabled=32768
```

`InventorySlotState` is a detached value containing slot index, item `GameObject`, `InventoryItemState`, locked, reserved, and equip slot. The item-state values are `None`, `Disabled`, `Enabled`, `Dropped`, and `Destroyed`.

`InventoryItemData.ToggleDropped(true)` also sets `IsReserved=true` when the slot is locked. This is another reason not to treat reservation as proof that an item is physically stashed.

Move and swap operations use both primary and secondary states. A correct multiplayer observer must process both sides and must not assume that only the primary state matters.

The event fires after the inventory arrays have been mutated. `StorageController` also listens to it, so listener ordering should not be relied upon for final storage membership. Capture the event payload immediately, then reconcile storage membership after the base-game operation has settled if necessary.

## Adding an item to inventory

`AddItemToInventory`:

1. reuses the item's reserved dropped slot when one exists, otherwise selects a free slot;
2. force-ends interaction if the item is grabbed;
3. consumes and destroys money-like items whose `IMoney.ShouldDestroyOnUse` is true;
4. sets or updates `InventoryItemData`;
5. marks essential items as reserved;
6. resolves the presentation as `Disabled`, `Enabled`, or `Dropped`;
7. fires `InventoryStatusChanged` with `Add` (and sometimes `Reserve` or `Destroy`).

For a normal non-VR stashed item, `FinalizeRemoveItemFromWorld`:

```text
assigns the inventory layer
parents the object to null while preserving world transform
sets the rigidbody kinematic
sets collision detection to Discrete
deactivates the GameObject
```

VR removal is delayed until the end of the frame. VR belt items may remain enabled rather than following the ordinary inactive inventory presentation.

`StorageController.OnInventoryStatusChanged` sees `Add` and moves the `ItemBase` to `StorageInventory`. `AddItemToStorageItemList` first removes it from its current storage and then adds it to the target storage.

## Dropping, equipping, and unequipping

### Ordinary non-reserved item

Dropping removes its inventory slot entry, returns it to world presentation, and emits `Drop | Purge`.

### Locked or reserved item

Dropping retains the same object in the same inventory slot, marks the slot dropped, and retains the reservation. The physical object returns to the world. The slot becomes a reference/claim on the world object rather than a stashed object.

### Essential item

`IsEssential(item)` causes the inventory slot to be reserved. Therefore an essential item normally follows the retained-slot path when dropped.

### Equipped item

Equipping records the object in `EquippedItems`. If it came from inventory, its inventory slot is changed to dropped/reserved form and the object is returned to active world presentation. The hand array, rather than storage membership alone, distinguishes an equipped object from an ordinary dropped object.

This means all of the following may be true for an equipped or dropped essential item:

```text
the object is associated with the player
the object has a reserved inventory slot
the object is in world storage
the object is active
```

The actual current state must therefore be resolved using hand/grab/container information before storage membership.

## Essential-item Get/recall behavior

The inventory UI marks reserved entries as item getters. For a dropped reserved item, the UI still retrieves the original `GameObject` with:

```csharp
Inventory.PeekItemAtSlot(slot, includeDropped: true)
```

When the player clicks Get, `InventoryUIController.OnGetClicked` verifies that the slot is dropped, then calls:

```csharp
Inventory.AddItemToInventory(gameObject, sameSlot, addAsDropped: false)
```

`AddItemToInventory` finds the object's reserved dropped slot, toggles `IsDropped` off, preserves/recalculates its reservation, and stashes the same object. There is no instantiation or identity replacement in this path.

### Multiplayer consequence

The recall/Get operation should preserve the `NetworkedItem` and NetId. It is an authoritative transition of the existing item:

```text
physical: World -> StashedInventory
reserved slot: unchanged
owner: unchanged
NetId: unchanged
```

If the mod currently observes only `InInventory` versus `Dropped`, it can replicate the broad state but cannot prove which reserved slot was recalled or whether two clients retained incompatible claims. The host manifest should track the reservation and revision.

## Storage controller

`StorageController` creates these storages:

| Storage | Accepts non-essential | Meaning |
|---|---:|---|
| `StorageWorld` | No | Player-owned/essential physical world items |
| `StorageLostAndFound` | No | Player-owned items moved to lost and found |
| `StorageInventory` | Yes | Locally stashed inventory items |
| `StorageInstalledGadgets` | Yes | Installed gadgets |
| `StorageItemContainers` | Yes | Items contained by other items |

The `acceptsNonEssential` check uses `InventoryItemSpec.BelongsToPlayer`. In storage terminology, “essential” is closely tied to player ownership rather than merely whether an object can be picked up.

`StorageBase.AddItem` and `RemoveItem` mutate a list and publish exact item events. `StorageBase` itself does not prevent duplicate insertion. Normal `StorageController.AddItemToStorageItemList` enforces cross-storage movement by removing the item from its previous storage first, but direct `StorageBase.AddItem` calls can bypass that convenience layer. `StorageController.ItemInMultipleStorages` exists specifically to diagnose invalid multiple membership.

## Lost and found

Lost and found is not the same as the inventory Get button.

`RequestLostAndFoundItemActivation`:

1. deactivates the currently presented lost-and-found items;
2. calls `MoveItemsFromWorldToLostAndFound(true, false, false)`;
3. asks access points to re-evaluate activation.

`MoveItemsFromWorldToLostAndFound` iterates a copy of `StorageWorld` and considers items that:

- are non-null;
- are not grabbed;
- have `InventoryItemSpec.BelongsToPlayer`;
- satisfy respawn-parent and snapped-item selection flags.

It prepares the existing object by unsnapping and/or parenting it to `WorldMover.OriginShiftParent`, then removes that same object from `StorageWorld` and adds it to `StorageLostAndFound`.

`ItemTransformControllerLostAndFound` later activates/deactivates and positions those existing objects at a lost-and-found access point. No item clone is created by the inspected `StorageController` path.

`ForceSummonAllWorldItemsToLostAndFound` is a more configurable version that controls inclusion of non-respawn-parent, respawn-parent, and snapped objects.

### Multiplayer consequence

Lost and found is global world-state movement, not a player inventory operation. It needs its own host-authoritative design. A client must not independently move every authoritative world item into its local lost-and-found storage. Until a global implementation exists, the mod should at least trace requests and storage transitions and detect host/client disagreements.

## Item containers

Inventory UI operations can move items:

```text
inventory <-> item container
container <-> container
container <-> world
```

Those operations may call `AItemContainer.AddItem`, `RemoveItem`, or `MoveOrSwapItem` without producing a simple inventory Add/Drop pair for every affected object.

`DV.Items.ItemPositionHandler` shows the relevant lifecycle signals:

- `ItemBase.ItemInContainerStateChanged`;
- `AItemContainer.ItemContainerNestedInChanged`;
- `ItemBase.ItemInventoryStateChanged` on containing items;
- `ItemBase.AboutToBeDestroyed`;
- `Inventory.InventoryStatusChanged`.

The supplied container classes make the transition semantics more precise:

- `AItemContainer.ItemContainerDataChanged(container, sourceIndex, destinationIndex)` reports exact slot mutations;
- `AItemContainer.ItemDropped(GameObject)` reports a contained item leaving for the world;
- `AItemContainer.ItemContainerNestedInChanged` reports nesting changes;
- `ItemContainerRegistry.RegistryUpdated(container, added)` reports container lifecycle;
- `ItemContainerRegistry.GetItemContainerIdAndIndex(item)` resolves exact live membership;
- `ItemContainerRegistry.ActiveContainerChanged` and active-container content/drop events support UI-focused tracing.

`ItemContainer.AddItem` sets `ItemBase.InContainer`, places the item in `StorageItemContainers`, reparents it, and deactivates it. Removal clears `InContainer`, changes storage according to ownership, and can activate/reparent/drop it. A contained item entering normal inventory is removed from its container. Clearing a container sends player-owned contents to lost and found and drops non-player contents.

`ContainerId` is persisted with the container item state. Runtime-generated values use the container name plus an ordinal. This is useful as a save/session identifier, but a multiplayer protocol should still handle collisions or reassignment and should associate the container with its owning item NetId where possible.

The multiplayer item manifest should use these events to follow nested containers. Container placement needs both the container identity and contained slot index; transform parent alone is not sufficient.

### Nested-container graph semantics

`AItemContainer` is an `IRecursiveItemStorage`, and the supplied implementation confirms that containers form a directed parent/child graph rather than a flat secondary inventory.

`NestedIn` is a tuple containing:

```text
Item1 = immediate containing AItemContainer
Item2 = outermost/root AItemContainer
```

`UpdateNesting(newFirstNest)` walks `NestedIn.Item1` from the immediate parent to the outermost ancestor, stores both values, and then recursively updates every container in `childrenContainers`. The seemingly unrelated consumers in `ItemAudioSpatialBlendController` and `ItemPositionHandler` use the outermost `ItemContainer` to follow the inventory/hand state of an entire nested hierarchy. A document inside a folder inside a registrator therefore inherits presentation and position behaviour from the outermost carried container.

The base class itself does **not** make this graph safe:

- `AddItem` calls the abstract `ValidItem`, but contains no explicit self-containment or ancestor-cycle test;
- `UpdateNesting` walks parent links without a visited set or maximum depth;
- it then propagates to child containers recursively, also without a visited set or depth bound;
- `Contains(item, recursive: true)` recursively descends into child containers without either guard;
- `ItemContainerRegistry` is a flat ID dictionary and does not validate graph integrity;
- `GetItemContainerAndIndex` only linearly scans direct slots in registered containers.

The concrete `ItemContainer.ValidItem` implementation is now known. It rejects `null`, the container's own GameObject, objects without a usable `ItemBase`, and items whose `SpecItem.itemType` does not intersect the container prefab's serialized `allowedItemTypes` mask. It does **not** test whether the proposed child container is an ancestor of the destination, call recursive `Contains`, enforce a depth limit, or otherwise validate the graph. The inventory UI repeatedly calls `ValidItem` before moves, but `HandleItemContainerMoveOrSwap` contains no separate ancestor/cycle check either.

Consequently, direct self-containment is blocked, but an indirect `A -> B -> A` cycle is not generically blocked by code. A particular prefab hierarchy may happen to prevent it through its `allowedItemTypes` masks, but save data and multiplayer validation cannot rely on those masks being globally acyclic. A cycle or sufficiently deep valid-looking chain can produce an infinite parent walk, unbounded recursive propagation, or a stack overflow.

Host-authoritative container operations should therefore validate a detached graph before calling Unity/container methods:

1. the moving item and destination container resolve to canonical, distinct items;
2. the source direct membership and source slot are correct;
3. the destination slot exists, is empty or is part of an explicitly valid swap, and passes concrete item compatibility;
4. the destination is not the moving container itself or any of its descendants;
5. every container has at most one direct parent and every item has one direct placement;
6. traversal uses an iterative visited set, a small maximum depth, and a total node/edge budget;
7. malformed full-sync/save graphs are rejected or quarantined before `AddItem`/`UpdateNesting` is invoked.

A reasonable initial multiplayer limit is depth 8 and 256 visited nodes per validation. The host should call the destination container's runtime `ValidItem(candidate)` method rather than copying serialized `allowedItemTypes` masks into the mod. Graph safety remains an independent host-side rule.

Save serialization, container identity restoration, two-phase edge reconstruction, and confirmed loader failure paths are documented in [`DERAIL_VALLEY_ITEM_SAVE_LOAD_LOGIC.md`](DERAIL_VALLEY_ITEM_SAVE_LOAD_LOGIC.md).

### Multiplayer container virtualization

For multiplayer scale, clients should not need a permanent Unity representation of every item inside every remote or closed container. Use an authoritative host graph with lazy client projection:

```text
host
  complete canonical container graph and item authority
  direct parent/slot edges
  ownership, tracked state, and revisions
  detached cold records backed by the host save

client closed container
  root container shell only
  container identity, capacity, direct item count, graph revision

client active/open container
  subscribed direct-slot snapshot
  bounded display DTOs through a container UI provider
  revisioned deltas while the subscription remains active
```

Opening the general inventory should not hydrate every carried container. `ItemContainerRegistry.ActiveContainerChanged` is the natural subscription boundary: request direct contents when a specific container becomes active, and release or cache them briefly after it closes. Nested contents are requested when the nested container itself is opened.

All moves remain host transactions. A client command identifies source/destination containers and slots plus expected graph revisions. The host resolves canonical records, calls the running game's compatibility policy, validates cycles/depth/ownership, applies the edge change once, and publishes a delta to viewers of the affected containers. Simultaneous viewers use revision conflicts rather than client-side last-writer-wins behavior.

The shell must not pretend an unhydrated container is empty. Base UI add/swap paths must be gated until the authoritative direct-slot snapshot is present. Prefer a data-driven container UI provider over temporary or fake item GameObjects; gameplay hydration occurs only when an item leaves cold storage.

The intended scalable target is host-side cold storage: contained items become detached authoritative records backed by the host save and do not retain dormant Unity objects or ordinary NetIds. Browsing hydrates bounded UI data only; withdrawing an item materializes its canonical Unity object and assigns a runtime NetId. This deliberately replaces part of the base-game lifecycle and is specified in [`MULTIPLAYER_CONTAINER_STORAGE_DESIGN.md`](MULTIPLAYER_CONTAINER_STORAGE_DESIGN.md).

Lost and Found naturally stores a virtualized container as one root shell/registry entry plus its existing host graph. Retrieving the root does not hydrate all descendants; opening the retrieved container subscribes to its direct contents normally.

## RespawnOnDrop lifecycle

`RespawnOnDrop` checks distance on a 0.2-second cadence. The default maximum distance is approximately 200 metres for player-owned objects and 1000 metres for non-player objects. When an eligible item exceeds the threshold, `RespawnOrDestroy` is scheduled after a delay (normally one second).

With `respawnOnDropThroughFloor` enabled:

- rigidbody linear and angular velocity are zeroed;
- a non-player item is reparented/reset to its recorded spawn and removed from `StorageWorld`;
- a player-owned item is deactivated, parented under `WorldMover.OriginShiftParent`, reset, and added to lost and found;
- the `Respawned` event fires.

Without that mode, the object is destroyed through the game destruction handler. Being bound to a player suppresses the distance trigger.

This class is a likely source of multiplayer-only disappearance and teleportation. Host and client have different player transforms and can make different distance decisions for the same network item. The eventual functional design should make the host decide and replicate the resulting storage/transform transition. Until then, every scheduled respawn/destruction and resulting storage move should be traced.

## Lost-and-found presentation

`StorageItemTransformController` does not create replacement objects. It activates existing lost-and-found items near the shed/access point, assigns saved or sequential display transforms, and zeroes rigidbody velocity. When out of range it deactivates those same objects again.

Therefore `activeSelf=false` does not necessarily mean the item has been deleted, and a lost-and-found presentation activation must not be mistaken for a new network creation. Identity should remain the same across world, lost-and-found, and displayed states.

## Recommended host manifest

A single mutually exclusive `location` enum is insufficient because reservation and physical placement overlap. A record should separate these dimensions:

```csharp
public sealed class AuthoritativeItemRecord
{
    public ushort NetId;
    public byte? OwnerPlayerId;
    public uint Revision;

    public ItemPhysicalState PhysicalState;
    // World, StashedInventory, Equipped, Container,
    // LostAndFound, Installed, Attached, Destroyed

    public int InventorySlot;       // -1 when there is no slot claim
    public bool InventoryReserved;
    public bool InventoryLocked;
    public bool InventoryDropped;
    public int EquippedSlot;        // -1, right/left hand index otherwise

    public ushort ContainerNetId;
    public int ContainerSlot;

    public string CarGuid;
    public Vector3 WorldPosition;
    public Quaternion WorldRotation;
    public object DetachedItemState;
}
```

This is intentionally close to `StorageItemData`, with the multiplayer-only identity and revision layered on top.

For early implementation, the authenticated client can report transitions and the host can accept them structurally. Each transition should include the previous revision so lost or reordered operations are detectable.

## Recommended event-driven integration

### Client observation

Subscribe when `Inventory` and `StorageController` are ready:

```text
Inventory.InventoryStatusChanged
StorageWorld.ItemAdded / ItemRemoved
StorageInventory.ItemAdded / ItemRemoved
StorageLostAndFound.ItemAdded / ItemRemoved
StorageInstalledGadgets.ItemAdded / ItemRemoved
StorageItemContainers.ItemAdded / ItemRemoved
```

For inventory events:

1. inspect both primary and secondary slot states;
2. resolve the exact `NetworkedItem` from the supplied `GameObject`;
3. if its NetId is zero, accept it only through the explicitly temporary compatibility-adoption
   policy for unconverted player-item producers; otherwise quarantine and destroy it;
4. otherwise send a location delta containing the action flags and resulting slot state;
5. sample the settled physical/storage state after the operation when needed;
6. deduplicate compound events by Unity frame plus item instance ID and resulting revision/state.

This replaces the current full `NetworkedItem.GetAll()` inventory scan.

### Host state

The host should maintain one manifest per player plus a global NetId index. It should enforce initially:

```text
one live Unity object per nonzero NetId
one manifest record per nonzero NetId
one equipped slot occupant per player/hand
one stashed item per inventory slot
one container occupant per container slot
monotonically increasing revision per item
```

Reservations are claims, not additional physical representations, and therefore do not violate the one-object rule.

### Reconciliation

Periodically compare:

```text
host manifest
client-reported manifest
local Inventory slots and EquippedItems
StorageBase memberships
ItemContainerRegistry and nested container state
live NetworkedItem/Unity object registry
```

Emit differences before attempting automated repair.

## Useful observability events

```text
inventory.transition-observed
inventory.item-added
inventory.item-dropped
inventory.item-equipped
inventory.item-unequipped
inventory.item-moved
inventory.item-swapped
inventory.item-purged
inventory.item-destroyed
inventory.reservation-changed
inventory.recall-requested
inventory.recall-completed

storage.item-added
storage.item-removed
storage.membership-changed
storage.multiple-membership

container.item-added
container.item-removed
container.item-moved
container.item-dropped
container.registered
container.unregistered
container.nesting-changed

item.respawn-or-destroy-scheduled
item.respawned
item.lost-and-found-presented

manifest.delta-requested
manifest.delta-accepted
manifest.delta-rejected
manifest.revision-gap
manifest.host-client-mismatch
```

Each inventory event should include both raw action flags and normalized state:

```text
NetId and Unity instance ID
primary/secondary slot
inventory item state
equipped slot
locked/reserved/dropped flags
physical active state and layer
grabbed state
storage membership
container identity and slot
owner
manifest revision
```

## Implemented observability

The mod now includes an additive, event-driven `DebugInventoryObserver`. It does not change the inventory or item application paths. While debugging is enabled it subscribes to the real inventory, storage, and container events and emits:

```text
inventory.observer-attached
inventory.transition-observed
inventory.item-transition
storage.item-added
storage.item-removed
container.registered
container.unregistered
container.contents-changed
container.item-dropped
container.nesting-changed
item.respawn-or-destroy-scheduled
```

Inventory transitions preserve both primary and secondary slot snapshots and the raw numeric action flags. Per-item events include NetId (or Unity instance fallback), prefab, essential/owner flags, inventory and equipped slots, container ID/slot, every relevant storage membership, multiple-storage detection, active state, layer, parent, and position.

The observer uses game events rather than recurring full-item scans. Its `Update` only waits for the singleton lifecycles to become available. `RespawnOnDrop` has a separate observational prefix because the scheduling decision occurs before any storage event; it records the delay, mode, identity, transform, velocity, ownership, and essential status.

Useful next diagnostics after a live test are:

- correlate `item.respawn-or-destroy-scheduled` with subsequent lost-and-found/storage events by NetId;
- warn when a nonzero NetId has contradictory live storage memberships;
- compare the exact inventory/container event sequence across host and client;
- display dropped/reserved slot claims separately from physical placement in the replication dashboard;

## Remaining decompilation requests

The supplied code now covers the ordinary inventory, Get/recall, storage, container, and distance-respawn lifecycles well enough to implement their runtime event model. Save/load research and its remaining requests live in the separate save/load document. Additional runtime decompilation is only needed when one of these narrower questions becomes active:

1. The concrete inventory presentation methods (`AssignInventoryLayer`, `AssignWorldItemLayer`, `ForceEndInteraction`, and `GetPlayerTransform`) if host-side world/in-hand presentation still diverges.
2. The provider implementing `IsEssentialItemsGetterAllowed`, if multiplayer needs to reproduce the exact circumstances under which the Get button is disabled.

## Multiplayer cold-container implementation note

Multiplayer-owned container contents no longer need to mirror Derail Valley's recursively live
`Storage_ItemContainers` objects. The physical container remains an ordinary item shell, while its
direct children are stored by the host as bounded detached records. The integration deliberately
continues to ask the running game's `AItemContainer.ValidItem`/prefab components for compatibility,
so the folder/registrator/briefcase/toolbox/crate policy follows game updates without a duplicated
hard-coded table. Both desktop and VR use the shared `ItemContainerProvider`, which is now the
single presentation seam for bounded cold views.
