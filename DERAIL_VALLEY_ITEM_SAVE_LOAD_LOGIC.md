# Derail Valley Item Save and Load Logic

This document records the supplied decompiled Derail Valley item persistence and restoration behavior. Runtime inventory, storage, recall, and container mutation behavior remains in [`DERAIL_VALLEY_ITEM_STORAGE_LOGIC.md`](DERAIL_VALLEY_ITEM_STORAGE_LOGIC.md).

## Sources inspected

- `StorageItemData`
- `StorageSerializer`
- `StartingItemsController`
- `ItemSaveData` event consumers in `DV.Items.ItemContainer`
- `AItemContainer`, `ItemContainer`, and `ItemContainerRegistry`
- `DV.InventorySystem.Inventory`
- `IRecursiveItemStorage`

## Executive conclusions

1. The save format stores item identity by prefab and item-specific state, not by a stable unique item ID.
2. Physical placement, inventory reservation, grabbing, car placement, and container placement are persisted separately.
3. Nested containers are persisted as direct parent edges using `containerId` and `containerSlotIndex`; ancestry is rebuilt during load.
4. Container loading is a two-phase process: instantiate and register all container nodes, then apply containment edges.
5. Save restoration has no graph-wide cycle, depth, duplicate-parent, or transactional validation.
6. Failure paths can orphan an inactive item, bind children to the wrong duplicate container ID, or throw while recovering a full container.
7. Multiplayer save restoration must remain separate from live item authority and validate detached data before calling Unity/container methods.

## StorageItemData projection

`StorageItemData` stores:

```text
itemPrefabName
position and rotation
belongsToPlayer
isGrabbed
inventorySlotIndex
containerSlotIndex
inLockedSlot
isDropped
carGuid
containerId
arbitrary JObject state
```

The base game therefore treats ownership, grabbing, inventory slot, dropped reservation, container placement, car placement, transform, and item-specific state as distinct dimensions. Inventory and container slot identity are necessary to restore an item correctly; a broad `InInventory` state is insufficient.

The class does **not** contain a unique item ID, a storage/location enum, an explicit reserved flag, or a revision. In single player, prefab plus saved placement is sufficient because the game controls reconstruction. Multiplayer persistence needs stable identity, owner identity, authoritative physical placement, reservation state, and revision in addition to this semantic shape.

The mod's `PlayerItemSaveData` mirrors much of `StorageItemData` and adds a `NetId`. Its helpers may be reusable, but it should not become the live transition protocol unchanged:

- it is a complete save snapshot rather than an explicit delta;
- it has no expected/current revision;
- it does not directly distinguish reservation from physical placement;
- arbitrary `JObject` state is expensive for frequent runtime events;
- coupling save restoration and live authority previously caused lifecycle regressions.

## Storage serialization

`StorageSerializer.SaveStorage` selects these save keys:

```text
Inventory        -> Storage_Inventory
LostAndFound     -> Storage_LostAndFound
World            -> Storage_World
InstalledGadgets -> Storage_InstalledGadgets
ItemContainers   -> Storage_ItemContainers
```

For inventory, it enumerates non-dropped inventory entries directly. Other types use the corresponding `StorageBase` list. Every included item records:

- prefab and `BelongsToPlayer` from `InventoryItemSpec`;
- transform, with world items on cars stored relative to the car interior;
- `carGuid` for world items resolved to a train car;
- component state from `ItemSaveData.SaveItemData()`;
- inventory slot, locked state, dropped state, and grabbed state;
- direct container ID and slot for `StorageType.ItemContainers`.

Contained-item placement delegates to `ItemContainerRegistry.GetItemContainerIdAndIndex`. It records only the direct containing edge. `NestedIn`, `childrenContainers`, full ancestry, and a graph revision are not serialized.

The inventory serializer calls `Inventory.GetItemsArray(false)`, so dropped/reserved silhouettes are not independently serialized through the inventory list. Their physical object is represented through its actual storage record, which can still carry `inventorySlotIndex` and `isDropped`.

## Container identity persistence

Container identity is stored inside the containing item's `ItemSaveData`, independently of its placement edge:

- `ItemContainer.OnItemSaveDataRequested` writes `ContainerId`;
- `OnItemSaveDataLoaded` reads it and calls `AssignIdAndRegister`;
- `PostLoadIdGenerationFallback` generates a new ID if the saved ID is absent or registration failed;
- starting items are expected to use this fallback.

Runtime-generated IDs use the container prefab name plus the first unused ordinal. They are persistence identifiers, not globally stable multiplayer identities.

Duplicate saved IDs are rejected by `ItemContainerRegistry`. The later container receives a generated fallback ID, but its saved child edges still reference the original ID and can bind to the first container instead.

`AItemContainer.AssignIdAndRegister` also contains a suspicious branch: if the registry reports that the container GameObject is already contained, it calls `UpdateNesting(this)`. Taken literally, this assigns the container as its own immediate parent. Runtime ordering may normally make the branch unreachable, but malformed restoration must not be allowed to reproduce that self-link.

## StartingItemsController restoration sequence

`StartingItemsController.AddStartingItemsCoro` loads:

```text
Inventory
LostAndFound
World
InstalledGadgets
ItemContainers
```

The restoration sequence is:

1. load all five `StorageItemData` collections;
2. combine them for narrow starting-item/license duplicate safeguards;
3. set `Inventory.ItemContainerRegistry.canRegisterContainers = false`;
4. instantiate installed, inventory, world, item-container, and lost-and-found objects at safe/deferred positions;
5. retain `(item, containerId, data)` tuples for valid direct container edges;
6. redirect entries without a usable container ID to the lost-and-found load list using the ignore-position sentinel;
7. enable container registration and run every `ItemSaveData.LoadItemData`, then every `PostLoadItemData`;
8. restore/register saved container IDs before containment edges are applied;
9. initialize normal inventory;
10. resolve and apply every saved direct container edge;
11. finalize lost-and-found and world storage lists;
12. set `itemsLoaded = true`.

This is a two-phase graph load: create and identify nodes first, then resolve and apply edges. Edge order need not be parent-first because later `AddItem` calls recursively refresh already-attached descendants.

Multiplayer code must respect the `itemsLoaded` barrier. Objects at safety positions or awaiting item-state callbacks must not be classified as world clutter or adopted as new runtime items.

## Inventory and physical-placement overlap

A world or contained record with `inventorySlotIndex >= 0` or `isGrabbed=true` also participates in inventory reconstruction. One Unity object can simultaneously have a physical world/container placement and a dropped/reserved inventory claim. That overlap is intentional, not a duplicate object.

The controller's duplicate resolution is deliberately narrow. It resolves basic starting items and physical licenses by prefab priority across the five collections. It does not deduplicate arbitrary saved items and provides no stable general-purpose identity reconciliation.

## Multiplayer disconnect inventory partition

Client inventory state is not itself an authoritative save source. When a player disconnects, the
host must reconcile their current inventory projection before producing persistence records:

1. freeze new mutations for that authenticated player;
2. enumerate canonical held, equipped, direct-inventory, and cold/live contained items;
3. resolve each item's stable persistent owner identity rather than consulting red styling,
   `IsEssential`, recall eligibility, or the current placement player;
4. save items owned by the departing player into their own inventory/container persistence;
5. atomically detach every item owned by somebody else and create one Lost-and-Found record for
   its original owner, even when that owner is offline;
6. apply/persist the resulting graph and placement revisions before retiring session NetIds or
   Unity projections;
7. make retries idempotent by persistent item UUID and committed authority revision.

This rule includes ordinary non-red items. A player leaving with a borrowed or stolen item never
causes ownership transfer and never writes that item into the thief/borrower's save. If a foreign
item is nested inside the departing player's container, detach the foreign edge transactionally
and recover that item (or its intact foreign-owned subtree where policy permits) to its persistent
owner; container nesting cannot be used to bypass disconnect recovery.

Required failure handling follows the same prepare/commit/finalize model as cold containers. A
failure before logical commit leaves the previous canonical placement intact. A presentation or
NetId-retirement failure after commit leaves the Lost-and-Found placement authoritative and
quarantines the obsolete projection for retry rather than duplicating or rolling back the item.

## Container edge restoration

For every `(item, containerId, data)` tuple, `AddItemsToContainers`:

1. resolves the parent through `ItemContainerRegistry`;
2. validates the saved slot range;
3. adds directly if the requested slot is empty;
4. defers slot collisions;
5. retries collisions using the first free slot;
6. attempts to move unresolved/full placements to lost and found.

`AItemContainer.AddItem` reconstructs `childrenContainers` and `NestedIn`. Neither restoration nor `IRecursiveItemStorage` validates the complete graph. `IRecursiveItemStorage` only exposes `Capacity` and `GetItemsArray(includingDropped)`.

## Confirmed restoration hazards

The loader has no graph-wide duplicate-parent check, ancestor-cycle check, depth bound, node budget, or rollback.

Concrete failure-path defects include:

- missing container IDs, unresolved parents, and out-of-range slots go to the load-time lost-and-found list;
- requested-slot collisions are silently remapped to the first free slot;
- the Boolean result of `container.AddItem` is ignored for both direct and deferred placement;
- an item rejected by `ValidItem` can remain instantiated, inactive, and absent from both its intended container and lost and found;
- when a collided container is full, `MoveToLostAndFound` receives `data = null` but immediately writes `data.itemPositionX/Y/Z`, which appears to be a `NullReferenceException`;
- duplicate saved container IDs can redirect children to the wrong container;
- indirect cycles and excessive depth are not rejected before recursive base-game methods run.

Useful observability outcomes are:

```text
container-load-invalid-item
container-load-slot-remapped
container-load-full
container-load-id-reassigned
container-load-orphaned
container-load-cycle-rejected
container-load-depth-rejected
```

## Multiplayer persistence requirements

Before applying saved or full-sync container edges, validate a detached graph:

1. every persisted item and container identity resolves exactly once;
2. each item has at most one direct physical parent;
3. source and destination are distinct canonical objects;
4. destination slot and collision policy are valid;
5. destination compatibility is evaluated by the host against the running game's real `ItemContainer.ValidItem` implementation;
6. destination is not the moving container or one of its descendants;
7. traversal uses an iterative visited set, maximum depth, and node/edge budget;
8. malformed graphs are rejected before `AddItem` or `UpdateNesting`;
9. a failed edge cannot leave an invisible orphan.

A reasonable initial limit is depth 8 and 256 visited nodes per player's restored container graph.

### Lost-and-Found container roots

An intact player-owned container stored in the multiplayer Lost and Found is persisted as one lost root plus the normal direct container edges for its descendants. Do not serialize a second copy of every child inside Lost-and-Found metadata. On load, rebuild and validate the container graph first, bind the lost root metadata second, and only then expose the owner's Lost-and-Found list. See [`DERAIL_VALLEY_LOST_AND_FOUND_LOGIC.md`](DERAIL_VALLEY_LOST_AND_FOUND_LOGIC.md) for collection and retrieval semantics.

Closed client shells are replication projections and never authoritative save sources. The planned host-side cold-storage architecture does change persistence: cold contents require one versioned multiplayer container-storage chunk because vanilla `Storage_ItemContainers` serializes live Unity objects. Root shells bind to that graph through persistent container identity. The complete target design is in [`MULTIPLAYER_CONTAINER_STORAGE_DESIGN.md`](MULTIPLAYER_CONTAINER_STORAGE_DESIGN.md).

`SaveGameData` directly supports opaque binary extension data through `SetCustomChunkData(int, byte[])` and `GetCustomChunkData(int)`. Writes replace an existing chunk of the same type or append it; a missing read returns `null`. `SaveGameManager.UpdateInternalData()` serializes vanilla inventory/world/Lost-and-Found/gadget/container storage before invoking `OnInternalDataUpdate`. This provides a clean host save hook for the cold graph: serialize the already-detached authoritative manifest in the callback and never hydrate cold records for saving.

## Runtime compatibility policy

Do not copy folder, registrator, briefcase, toolbox, and crate `allowedItemTypes` masks into the multiplayer mod. The host should validate a proposed direct placement using the destination container instance's public `ValidItem(candidateGameObject)` method on Unity's main thread. This automatically follows prefab data and base-game changes after an update.

Runtime compatibility and multiplayer graph safety remain separate checks:

```text
base-game compatibility
  destination.ValidItem(candidate)

multiplayer integrity
  canonical identities
  direct source membership
  slot/capacity policy
  no self or ancestor cycle
  one physical parent
  maximum depth and node budget
```

The host is authoritative for both results. A client may call its local `ValidItem` for immediate UI feedback, but it must tolerate host rejection if the installations differ. If the dashboard needs to explain a rejection, snapshot the candidate item type, destination prefab/container ID, and the Boolean `ValidItem` result. Reading the private mask is optional diagnostic context, not protocol state.

For pure unit tests, inject a small compatibility policy interface rather than depending on Unity or duplicating production masks. Runtime adapts that interface to `ValidItem`; tests provide explicit compatible/incompatible fixtures.

VR appears to share the same mutation path. `InventoryUIController.Awake` sets `disableDragging = provider.IsVREnabled`, while `RequestMoveItem`, `RequestSwapItem`, and `HandleItemContainerMoveOrSwap` remain common. VR changes input and presentation, not the authoritative container transition rules, unless a future game version introduces another `AInventoryUIController` implementation.

## Remaining research

- Confirm through dnSpy's derived-type analysis that `InventoryUIController` is the only concrete `AInventoryUIController` implementation.
- Exercise runtime `ValidItem` checks for every container/candidate combination as diagnostics, without persisting the resulting matrix as mod configuration.
- Exercise malformed saves: duplicate IDs, incompatible items, full destinations, indirect cycles, and excessive depth.
- Inspect broader player/save ownership boundaries before designing persistent multiplayer inventory identity.
- Decide when persistence needs a stable UUID rather than a session NetId allocated after restoration.
- Capture the `StartingItemsController.itemsLoaded` barrier and restore-source classification in observability when save restoration becomes the active implementation focus.
