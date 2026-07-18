# Multiplayer Container Storage Design

> Status: first owner-only implementation complete; requires two-process in-game acceptance testing. This deliberately replaces Derail Valley's always-instantiated contained-item model for multiplayer-owned containers.

## Implemented surface (2026-07-15)

The first implementation now includes:

- a pure, host-authoritative cold graph with persistent UUIDs, compact session handles,
  per-container revisions, owner-only authorization, direct-slot browsing, nesting, moves,
  transactional deposit/withdrawal phases, bounded idempotency, and graph validation;
- hard limits for depth, visited nodes, capacity, owner item count, detached state size,
  browse page size, and encoded save size;
- a version-2 binary host-save chunk under `Multiplayer.ColdContainers`; no legacy-record
  migration is included because development is currently single-user;
- persistent container UUID injection through `ItemSaveData`, so shell identity survives
  inventory, world, and Lost-and-Found save paths;
- four bounded request/result packet types. Routine packets use NetIds and session handles;
  persistent UUIDs remain save/internal data and are not sent to clients;
- host-side hydration/dehydration using the existing inventory/storage integration APIs and
  `ItemSaveData`, with persistent item ownership preserved independently of the container owner;
- runtime compatibility checks derived from the currently loaded Derail Valley container and
  item prefabs, cached per prefab pair rather than hard-coded item-type tables;
- one-level-at-a-time non-VR/VR inventory projection using the game's shared
  `ItemContainerProvider`, including nested-container navigation and foreign-owner red styling;
- a Containers page in the F12 debug overlay showing UUID, compact handle, owner, revision,
  capacity, parent edge, and direct cold-item count.

Physical shell replication remains the normal item system. Only contents are detached. A shell
collected by multiplayer Lost and Found retains its graph unchanged; retrieving the shell does not
eagerly instantiate its descendants.

The relationship between materialized shells, detached contents, persistent ownership, Lost and
Found, and presentation retirement is canonicalized in
[`MULTIPLAYER_ITEM_LIFECYCLE_DESIGN.md`](MULTIPLAYER_ITEM_LIFECYCLE_DESIGN.md).

## Goal

Make item containers scalable, host-authoritative, private by default, and resistant to client crashes or bandwidth abuse. Closed contents live as detached host records backed by the host save. Clients receive a physical container shell and no contents until authorized access requires a bounded view.

## Core model

```text
physical container shell
  Unity GameObject only while the container itself is in inventory/hand/world
  normal NetId and item replication
  persistent container identity and graph revision

cold container contents
  host in-memory records backed by the host save
  no dormant Unity GameObjects
  no ordinary item NetIds until materialized
  no replication to uninterested or unauthorized clients
```

The save file is backing persistence, not a database queried on every open. The host loads a bounded detached manifest once, mutates it transactionally during play, and writes it through the normal host-save lifecycle.

## Identity layers

```text
persistent item/container ID
  save-only durable identity
  never included in routine gameplay packets

session container/item handle
  compact uint used by authorized container UI requests
  allocated by the host and not persisted

NetId
  assigned only while an item has a materialized gameplay representation
  retired rather than immediately reused after dehydration
```

A cold item does not require a NetId. Materializing it binds its persistent record to a new runtime NetId. Dehydrating it writes state back to the same persistent record and retires the runtime representation.

## Authoritative records

The pure host model should include at least:

```text
ContainerRecord
  persistentContainerId
  ownerIdentity
  capacity
  graphRevision
  direct slot records

StoredItemRecord
  persistentItemId
  prefabName
  persistentOwnerIdentity
  detached ItemSaveData/tracked state
  direct parent container ID and slot
  optional child container ID
  state version

```

Ownership of the container and ownership of a contained item are independent. Depositing an item never silently changes its persistent owner.

## Owner-only access policy

Containers are private to their persistent owner. Physical possession of a shell does not grant access to its contents.

Initial permissions:

```text
owner
  view, deposit, withdraw, organize

everyone else
  none
```

The host compares the authenticated stable player identity with `ContainerRecord.ownerIdentity` before disclosing names, counts beyond an intentionally public shell summary, tracked state, or slot contents. Transient byte player IDs are never persisted as ownership.

Friend sharing and ACLs are explicitly out of scope for the current implementation. The data model may be extended later, but no access lists, permission combinations, management UI, or ACL protocol should be built now.

Explicit persistent-owner recall of a recallable child remains a separate authority operation and may remove that child from another player's container without granting general container access.

## Browse versus gameplay hydration

Opening a container should not immediately instantiate every direct child.

```text
browse hydration
  bounded slot DTOs for the active container UI
  display name/icon/prefab classification
  compact item handle, ownership marker, child-container marker
  no Unity item objects

gameplay hydration
  instantiate the canonical host object only when an item leaves cold storage
  load detached item state
  finalise tracked values
  allocate/register a NetId
  replicate normal inventory/hand/world placement
```

The preferred UI implementation is a container data provider backed by browse DTOs. Creating fake item GameObjects solely to satisfy the UI risks production scripts treating previews as canonical objects.

Nested containers are browsed one level at a time. Opening the general inventory does not request every carried container; selecting a specific active container requests only its direct slots.

## Host transactions

Unity lifecycle work is not safely reversible after teardown begins. Logical state changes atomically; Unity presentation may finalize afterward but can never become authoritative accidentally. Use directional operation states rather than generic rollback language:

```text
PreparingDeposit
  physical item remains authoritative
  detached state and candidate graph are validated
  destination is reserved

ColdCommittedPendingRetirement
  cold record and edge are authoritative
  revisions have advanced
  obsolete physical projection is quarantined
  retirement cleanup is retryable

Cold
  cold record remains authoritative
  obsolete physical projection is gone

PreparingWithdrawal
  cold record remains authoritative and reserved
  staged Unity object is being built

MaterializedPendingCommit
  staged object is fully initialized but still non-authoritative
  cold edge remains authoritative

Materialized
  physical item is authoritative
  cold edge has been removed
```

`PreparingDeposit`, `PreparingWithdrawal`, and `MaterializedPendingCommit` are transient. They can be abandoned during shutdown/recovery without changing logical authority. `ColdCommittedPendingRetirement` is a durable recovery obligation: saves suppress the stale physical projection, retain cold authority, and record enough cleanup identity to finish retirement safely after restart.

Every operation has an idempotency/operation ID. Retrying a logically committed operation returns its existing result and never advances revisions twice.

Every mutation carries expected graph revisions and is atomic.

### Withdraw

1. Authenticate and authorize the viewer.
2. Validate expected container revision and direct source slot.
3. Reserve the stored record and source slot against concurrent operations.
4. Instantiate a staged, inactive, non-interactable host object and apply detached state.
5. Enter `MaterializedPendingCommit`: finalise tracked values, validate the resulting Unity state, allocate a tentative NetId, and prepare authority registration without publishing it.
6. If preparation fails, destroy only the staged object and release reservations; the cold edge remains authoritative and unchanged.
7. Commit atomically to `Materialized` by removing the cold edge, registering physical authority, assigning placement, and incrementing revisions.
8. Publish normal item replication and container deltas with retryable delivery.

The cold edge is never removed before the physical object is fully initialized and ready to become canonical. A staged object is not visible to ordinary registries, interaction, saves, or clients.

### Deposit

1. Authenticate the depositor and verify both current possession and persistent ownership. A
   stolen, borrowed, or ownerless item cannot cross into a player's private cold-storage graph.
2. Resolve the canonical physical item and destination shell/record.
3. Call the running game's compatibility policy and validate capacity, cycles, depth, ownership invariants, and expected revisions.
4. Snapshot all detached item state without mutating or unregistering the physical object.
5. Enter `PreparingDeposit`: apply the proposed edge to a cloned transaction graph, reserve the destination slot, construct the persisted DTO, and validate the complete result.
6. Commit atomically to `ColdCommittedPendingRetirement` by adding the cold record/edge, incrementing revisions, and marking the physical representation non-authoritative and non-interactable.
7. Publish the container delta and removal from ordinary item interest.
8. Finalize by retiring the NetId, unregistering the physical representation, and destroying/deactivating the Unity object.

Failure before commit releases reservations and leaves the physical item untouched. Failure during retirement does **not** roll the graph back or attempt to reconstruct a partially dismantled Unity object. The committed cold record remains authoritative; the obsolete representation stays quarantined in `ColdCommittedPendingRetirement`, emits diagnostics, and is cleaned up by an idempotent retry worker before transitioning to `Cold`.

Save/unload handling must either wait for prepared operations to finish or discard them without authority changes. Committed-but-not-finalized operations are saved as cold authority plus pending cleanup metadata so restart recovery cannot resurrect both representations.

### Transaction observability

Emit explicit phase events with operation ID, persistent/session item handle, container handle, source/destination slots, revision before/after, tentative/current NetId, cleanup attempt, and failure reason:

```text
container.deposit.preparing
container.deposit.logical-committed
container.deposit.retirement-pending
container.deposit.retirement-retry
container.deposit.cold

container.withdrawal.preparing
container.withdrawal.materialized-pending-commit
container.withdrawal.logical-committed
container.withdrawal.materialized

container.transaction.failed-before-commit
container.transaction.invariant-violation
```

The debugger must clearly distinguish a failure before logical commit from retryable presentation cleanup after commit.

### Move within/between containers

Operate entirely on cold records when both sides are cold. Validate access to both containers, compatibility, cycles, slots, and both expected revisions. No Unity hydration is required.

## Runtime compatibility

Do not hard-code `allowedItemTypes`. The host derives compatibility from the running game. For a physical deposit it can call the destination shell's `ValidItem` on the canonical candidate. For cold-to-cold moves, use a runtime-built compatibility adapter based on the host's loaded prefab/item specifications. The adapter is runtime data, not persisted protocol configuration.

Graph integrity remains independent of base-game compatibility:

- one direct parent per stored item;
- no self or ancestor cycles;
- bounded depth and total nodes;
- valid destination slot and capacity;
- canonical record identity uniqueness.

## Replication and subscriptions

Suggested flow:

```text
OpenContainerRequest
  container handle/NetId, expected known revision

ContainerViewSnapshot
  container handle, capacity, revision, bounded direct slots

ContainerMutationRequest
  operation, source/destination handles and slots, expected revisions

ContainerMutationResult
  accepted/rejected reason, new revisions

ContainerDelta
  changed direct slots for currently authorized subscribers

CloseContainer
  release subscription
```

Snapshots must be size-capped and optionally paged even though current base containers are small. Never send arbitrary recursive trees. A viewer subscribes only to the active direct container, and authorization is rechecked for every request and delta.

## Lost and Found

A lost physical container produces one Lost-and-Found root entry. Its cold descendant graph remains unchanged and private.

- Collection suppresses the root shell and retains cold records.
- Retrieval restores only the shell.
- Opening the retrieved container performs normal authorized browse hydration.
- Contents do not become individual Lost-and-Found entries.
- Clearing/destroying a container is different and must explicitly relocate or recover each child.
- New deposits cannot introduce foreign-owned contents: both the Unity-facing server boundary and
  the pure graph reject an item whose persistent owner is not the requesting container owner.
- Import validation must reject any foreign-owned edge from malformed or externally edited data.

## Save integration

Once contents are truly cold, vanilla `Storage_ItemContainers` can no longer be the authoritative source for those records because it serializes live Unity objects. Multiplayer needs one versioned canonical container-storage chunk written by the host.

The save transaction should contain the detached records, direct edges, persistent identities, ownership, revisions, state versions, and committed cleanup obligations. Root shells may still use normal item persistence, but their persistent container IDs bind them to the cold graph. On load:

1. validate and load the detached graph;
2. reject/quarantine malformed records;
3. restore physical root shells;
4. bind shells to container records;
5. bind Lost-and-Found root metadata;
6. expose container access only after all bindings complete.

Do not materialize every cold item merely to invoke vanilla saving; that creates a large save-time spike and reintroduces the lifecycle hazards this design removes.

## Abuse and performance limits

- per-player total stored-item quota;
- per-container capacity from runtime game data;
- maximum nesting depth;
- maximum graph nodes visited per operation;
- maximum view snapshot bytes and slots;
- open/mutation request rate limits;
- bounded concurrent materializations;
- no recursive packet payloads;
- no disclosure to unauthorized clients;
- reject duplicate identities, parents, or occupied edges during load.

Dumping hundreds of full containers in one location then costs clients roughly one shell per visible container, not every descendant item. Existing world-interest limits and pooling still apply to the shells themselves.

## Implementation order

1. Extract and unit-test the pure container graph, revisions, owner-only access policy, quotas, and transaction state machine.
2. Add versioned host persistence for cold records.
3. Add shell identity and binding without changing existing physical contained items.
4. Add browse DTOs and a data-driven container UI provider for non-VR and VR.
5. Convert deposit/withdraw to dehydrate/materialize transactions.
6. Add nested cold moves and Lost-and-Found root integration.
7. Profile host saving, hydration spikes, reconnects, and malicious limits.

Friend ACLs are a separate future feature and are not part of this delivery order.

## Confirmed Derail Valley integration seams

The supplied UI decompilation confirms that the existing inventory presentation can be reused, but the live-container provider and item-container mutation branches must be replaced as one coherent seam.

### Container UI data path

- `ItemContainerProvider` is entirely coupled to `ItemContainerRegistry`, live `AItemContainer` keys, container events, and child `GameObject[]` contents. Replace its item-container model with a multiplayer provider keyed by persistent container handle and browse revision.
- `InventorySectionController` already consumes an observable collection of `InventorySlotDisplayData` and can add, remove, replace, move, swap, or replace the whole active model. Its grid/rendering machinery is suitable for detached browse rows.
- `InventoryGridElement` is presentation-only: it stores `InventorySlotDisplayData` and forwards visual, hover, access-hover, and drag updates. It does not itself require a Unity item.
- `InventorySlotDisplayData` is almost detached-friendly, but its constructor eagerly calls `spec.GetGameObject()` to discover an `AItemContainer`. Cold rows therefore need an explicit child-container handle/flag rather than a fabricated `AItemContainer` or preview `GameObject`.
- `IInventoryItemSpec` contains enough display metadata for a detached read-only adapter: prefab identity, localized name/description, icons, preview prefab/bounds/rotation, and ownership/essential flags. Its `GetGameObject()` member may return `null` only on code paths proven to be display-only.
- `AInventoryProvider` and `InventoryProvider` are shared by VR and non-VR. Equip, unequip, and belt checks require a real `GameObject`; those operations must materialize a cold item first and then continue through the ordinary inventory path.
- `InventoryUIInteractionObserver` reports UI intent and does not own container state. It can remain, with item-container intents routed to host requests instead of immediate local mutation.
- `InventoryUIController` directly mutates live containers in `RequestAddItem`, `RequestDropItem`, `RequestMoveItem`, `RequestSwapItem`, `HandleItemContainerMoveOrSwap`, nested-container access, and magazine ejection. Every branch involving the item-container section must be intercepted; patching `ItemContainerProvider` alone would leave authoritative local mutations and null `GetGameObject()` dereferences.

The client-side active-container concept should therefore be a lightweight view state:

```text
activeContainerHandle
activeContainerRevision
parentContainerHandle
capacity
readOnly/loading/error state
bounded slot DTO collection
```

Nested access uses the child's persistent handle from its slot DTO. Inventory-to-container deposit still starts with a real inventory `GameObject`; container-to-inventory withdrawal starts with a cold record and cannot call equip/add APIs until host-authorized materialization completes.

### Multiplayer inventory integration API

Container storage must consume a general internal inventory API rather than patching Derail Valley's UI and `Inventory` class directly throughout the feature. This is the inventory equivalent of the Career Manager extension boundary: game-version-specific knowledge stays in one adapter while replication, Lost and Found, ownership, recall, and container storage use stable multiplayer contracts.

Initial extraction is implemented in `Multiplayer/Integrations/Inventory/InventoryIntegration.cs` and `Multiplayer/Integrations/Storage/StorageIntegration.cs`. Ownership presentation and recall routing use the pure `InventoryPresentationPlanner`; `NetworkedItem` claim restoration/removal and world-storage transitions, the ownership UI patch, and Lost-and-Found inert projection now consume the adapters. Container browse DTOs and command interception remain the next phase.

Keep the first version deliberately narrow. It is an internal compatibility boundary, not a public mod SDK and not a second inventory implementation.

```text
Inventory domain
  stable item/container handles
  detached slot snapshots
  placement and ownership facts
  validated operation commands/results

Derail Valley adapter
  reads Inventory/InventorySlotState/AItemContainer
  calls EquipItem, UnequipItem, AddItemToInventory, and removal APIs
  translates InventoryStatusChanged and container events
  owns Harmony/version-specific UI interception

Inventory UI bridge
  projects detached rows into the existing grid
  routes clicks, VR selection, drag/drop, and nested access to commands
  never treats a cold row as a Unity GameObject
```

The stable operation surface should cover:

- snapshot inventory, equipped slots, reserved silhouettes, and active container view;
- resolve a physical item to its current inventory/equipped/container placement;
- add, remove, equip, unequip, move, swap, and drop physical items;
- open/close/navigate a container by persistent handle;
- request cold deposit, withdrawal, cold-to-cold move, and nested-container access;
- return structured results such as `Accepted`, `StaleRevision`, `SlotOccupied`, `Incompatible`, `InventoryFull`, `NotMaterialized`, and `Unauthorized`;
- emit detached before/after events for observability without retaining mutable Unity references.

Do not expose raw `Inventory`, `AItemContainer`, `GameObject`, or UI controller types above the Derail Valley adapter. A physical-item operation may use an opaque runtime item handle that the adapter resolves on Unity's main thread. Cold operations use persistent record/container handles plus expected revisions.

This boundary also provides one place to preserve version-specific behavior such as reserved-slot removal, VR click handling, belt rules, tutorial hints, and the distinction between ordinary inventory removal and `PurgeFromInventory`.

Existing multiplayer inventory behavior must migrate into this boundary rather than remain as independent patches. In particular, the foreign/stolen item presentation currently implemented by `InventoryOwnershipUiPatch` becomes an inventory projection policy:

```text
InventorySlotProjection
  persistent owner
  current holder
  local player
  retrievable/reserved state
  foreign-owned marker
  interaction/recall permissions
  optional colour/style hint
```

The adapter applies that projection to the current DV visual controller. The ownership system decides the semantic flags; UI code does not rediscover authority by walking `NetworkedItem` and inventory objects. The same route should own the reserved-slot star action so ordinary recall and Lost-and-Found retrieval no longer require a separate Harmony decision tree.

The full `InventoryViewVR` body confirms that VR is not merely presentation. `OnContainerAccessClicked`, `OnBackpackAccessClicked`, `HandleContainerSlotClick`, grab/unstash handling, and related callbacks directly call `AItemContainer.AddItem/RemoveItem` and `Inventory.DropItemFromHandsOrInventory/AddItemToInventory`. These are additional version-specific mutation entry points that the Derail Valley adapter must patch or redirect. Both VR and non-VR should ultimately submit the same stable inventory commands and consume the same resulting projections.

### Visual and tooltip projection

`InventorySlotVisualController.UpdateVisuals` confirms that ordinary icon/name/description rendering only needs `IInventoryItemSpec` metadata. Container presentation is the exception: it subscribes to a live `AItemContainer`, reads `ItemCount`, enumerates child GameObjects for tooltip icons, and calls `ValidItem` during drag-hover calculations.

Cold slot DTOs must therefore include the presentation facts required by that controller without recreating a container object:

```text
item icon/name/description
is child container
child container handle
contained item count
bounded contained icon preview
canOpen
loading/read-only/error state
```

The UI bridge should set tooltip text/icons directly from those facts and use host/runtime compatibility results for hover state. `TooltipHandler` itself needs no replacement; its active tooltip is a UI component GameObject, not the represented item GameObject.

### Priority 2: detached state and materialization

1. `ItemSaveData` — complete save/load/post-load event ordering, supported null/default state, and safe standalone use on a staged object.
2. Any subclasses or adapters used by unusual stateful items that override the normal `ItemSaveData` lifecycle.
3. The base-game prefab lookup/factory used for inventory items, if anything more specific than `Resources.Load(prefabName)` exists.
4. `ItemBase` initialization around `ItemSaveData`, `InventoryItemSpec`, `ItemReparentingBase`, rigidbody state, and interaction enabling.
5. Destruction/unregistration entry points needed to retire a deposited physical object without triggering vanilla Lost and Found, container clearing, or duplicate network transitions.

`StartingItemsController.InstantiateItem`, `StorageItemData`, `StorageSerializer`, and the surrounding restoration order are already available and documented.

### Host save integration

`SaveGameData` exposes `SetCustomChunkData(int, byte[])` and `GetCustomChunkData(int)`. Setting replaces the first matching chunk or appends a new one; missing reads return `null`. `SaveGameManager.UpdateInternalData()` writes all five vanilla storage collections first and invokes `OnInternalDataUpdate(SaveGameData)` afterward. The multiplayer cold graph can therefore serialize a versioned custom chunk from that event without materializing cold contents.

The host must load and validate that chunk before exposing container browse operations, then bind physical root shells after `StartingItemsController.itemsLoaded`. Remaining save research is limited to choosing a collision-safe custom chunk type and confirming the earliest reliable load callback for the active save.

### Useful dnSpy analyses

- Analyze remaining implementations and callers of `IInventoryItemSpec.GetGameObject`; the known equip, belt, access, and mutation paths are unsafe for cold specs.
- Fetch the actual `ItemSaveData` class body. Its usage analysis confirms broad event-driven composition and no subclasses, but does not expose save/load/post-load ordering.
- Analyze `ItemSaveData.SaveItemData`, `LoadItemData`, and `PostLoadItemData` implementations and subscribers.

## Required tests

- unauthorized users cannot discover or mutate contents;
- physical possession does not grant access;
- a player cannot deposit an item owned by another player, even while holding it;
- owner can browse without Unity materialization;
- withdraw creates exactly one canonical item and NetId;
- deposit creates exactly one cold record and retires the representation;
- deposit failure before commit leaves the physical object and graph untouched;
- retirement failure after commit leaves cold authority canonical and retries cleanup without another revision;
- withdrawal failure before commit leaves the cold edge authoritative and removes only the staged object;
- withdrawal never publishes or registers a partially initialized item;
- retrying either operation is idempotent;
- stale revisions reject without partial mutation;
- concurrent viewers receive consistent deltas;
- nested moves cannot create cycles;
- mixed child ownership is preserved;
- recallable child extraction does not expose other contents;
- Lost-and-Found root retrieval preserves the cold graph;
- save/reload preserves identities, edges, owners, tracked state, revisions, and committed cleanup obligations;
- malformed or oversized save graphs are rejected safely;
- hundreds of full nearby shells do not replicate their descendants.
