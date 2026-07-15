# Derail Valley Multiplayer Lost and Found Logic

> Status: work in progress. The host-authoritative registry, collection/retrieval flow, Career Manager page, shed suppression, observability, and persistent identity described below are implemented, but still require broader in-game acceptance testing.

## Multiplayer integration boundary

Lost and Found policy should remain a multiplayer domain service, while direct Derail Valley manipulation moves behind shared internal adapters:

```text
NetworkedLostAndFoundService
  eligibility, ownership, grace, persistence, retrieval transactions
        |
        +-- InventoryIntegration
        |     slots, silhouettes, recall action, add/remove/equip projection
        |
        +-- StorageIntegration
              world/inventory/container/storage membership and physical projection
```

Do not model the overridden vanilla Lost and Found as another authoritative integration API. Multiplayer Lost and Found is the authority; the storage adapter only projects its decisions into DV storage lists and Unity state. Existing direct calls in `NetworkedLostAndFoundManager`, `InventoryOwnershipUiPatch`, and `StorageControllerPatch` are sufficiently concentrated that they should be migrated now while the boundary is introduced, rather than preserved as compatibility debt.

## Related documents

- `DERAIL_VALLEY_ITEM_STORAGE_LOGIC.md` contains the established base-game inventory, storage, `RespawnOnDrop`, and shed-presentation behavior.
- `ITEM_SYNC.md` sections 3.4-3.5 describe the current canonical item-authority and retrieval-claim model.
- `OBSERVABILITY.md` describes the implemented inventory/storage tracing and profiler facilities.
- `DERAIL_VALLEY_JOB_VALIDATOR_REPORT_LOGIC.md` documents machine-owned job artifacts that must not be collected while being processed.
- `DERAIL_VALLEY_PAGEBOOK_LOGIC.md` documents booklet semantic state; page state does not determine Lost and Found eligibility.
- `DERAIL_VALLEY_CAREER_MANAGER_UI_LOGIC.md` owns the reusable Career Manager extension API and Lost Items screen integration contract.

## Goal

Replace Derail Valley's physical, process-local Lost and Found shed with a host-authoritative, per-player lost-item registry. A player should be able to inspect and retrieve their lost items from any Career Manager terminal.

The system must preserve the existing multiplayer item identity and authority rules:

```text
one canonical item per nonzero NetId
one physical placement at a time
one persistent owner at most
authority revisions only increase
retrieval never creates a duplicate item
```

The physical shed should no longer present or release items. The base `StorageLostAndFound` object may temporarily remain as an internal integration point while all game references are identified, but it must not be the multiplayer source of truth.

## Core distinction: retrievable is not a location

Derail Valley currently overlaps two different concepts:

1. an item can be physically present in the world while a reserved inventory slot allows its owner to recall it;
2. an item can be inactive and physically stored in `StorageLostAndFound` for later presentation at a shed.

These must remain separate in multiplayer.

| State | Physical object | Inventory claim | Lost-item registry | Meaning |
|---|---|---|---|---|
| World | Active in world | None | No | Ordinary world item |
| Recallable world | Active in world | Reserved/locked | No | Owner can retrieve it immediately |
| Lost and found | Inactive/non-world | May be retained | Yes | Host has removed it from the world |
| Player inventory/hand | Local player presentation | Optional | No | Item is possessed by a player |
| Quarantined | Inactive/non-world | Policy-dependent | Separate | Save repair or unresolved data, not ordinary loss |

The phrase **recallable world item** should be used for the first case. It is misleading to call that item "in Lost and Found." Retrievability is a capability represented by the authoritative inventory claim; Lost and Found is an authoritative placement.

This distinction directly handles essential items:

- an essential item dropped in the world retains its silhouette/reserved slot;
- it can be recalled through the normal inventory Get action at any time;
- if the host later decides it is genuinely lost, it moves to the personal Lost and Found registry;
- retrieving it must transition that same NetId back to inventory, never instantiate a second copy.

## Facts established from the base game

### `RespawnOnDrop`

Every enabled `RespawnOnDrop` runs its own distance check every 0.2 seconds. Its default range is:

```text
BelongsToPlayer = true  -> 200 metres
BelongsToPlayer = false -> 1000 metres
```

The full class confirms that `Checker()` compares the item to the process-local player, active camera, and saved spawn position. It also changes `Rigidbody.isKinematic` based on that local camera distance, so separate peers can freeze the same item's local physics at different times.

When a player item is considered out of range, `RespawnOrDestroy()` waits one second, checks again, and then:

1. removes it from train physics LOD;
2. zeros all child rigidbodies;
3. deactivates it;
4. parents it to `WorldMover.OriginShiftParent` at local zero/identity;
5. calls `StorageController.AddItemToLostAndFound()`;
6. raises `Respawned`.

This is unsuitable as multiplayer authority. A host and client can make different decisions based on different local players, and hundreds of independent 0.2-second coroutines are unnecessary when the host already owns the canonical item registry. Blocking only `AddItemToLostAndFound()` is too late because the object has already been hidden, reset, and removed from train physics. Multiplayer-managed player items must be intercepted before these mutations, or have the vanilla checker disabled once canonical management begins.

For non-player items, `RespawnOnDrop` may instead restore the original spawn transform or destroy the object. The new personal Lost and Found must not absorb that separate scene-prop behavior.

### `StorageController`

The base game creates one process-local `StorageLostAndFound`. Adding an item also records transform data through `StorageItemTransformController`.

When a player enters a Lost and Found access point, `StorageController` moves the shared shed customization to that access point and activates the stored items there. Leaving deactivates them again. `RequestLostAndFoundItemActivation()` can also move eligible world items into the storage before requesting physical presentation.

This means the current implementation couples three concerns:

```text
eligibility and loss detection
storage/persistence
physical shed presentation
```

The multiplayer replacement should separate all three.

The supplied decompilation confirms the complete physical path:

```text
LostAndFoundItemsSummoner.OnSummonPressed
  -> StorageController.ForceSummonAllWorldItemsToLostAndFound(...)
     -> StorageItemTransformController.DeactivateItems()
     -> MoveItemsFromWorldToLostAndFound(...)
        -> PrepareItemForLostAndFound(...)
        -> StorageWorld.RemoveItem(item)
        -> StorageLostAndFound.AddItem(item)
     -> StorageItemTransformController.DeactivateItems()
     -> RequestItemActivation()

StorageAccessPointBase.PlayerInActivationRange
  -> StorageController.OnPlayerInActivationRange(...)
     -> move/enable StorageShedCustomization
     -> StorageItemTransformController.ActivateItems(...)

StorageAccessPointBase.PlayerInDeactivationRange
  -> StorageController.OnPlayerInDeactivationRange(...)
     -> StorageItemTransformController.DeactivateItems()
     -> disable StorageShedCustomization
```

`RequestLostAndFoundItemActivation()` is another entry point. It deactivates the current presentation, performs a default world sweep, and requests access-point activation.

The base sweep is far too broad for multiplayer. It considers `BelongsToPlayer`, grabbed state, respawn-parent state, and snapping, but it has no concept of persistent owner, current remote possessor, nearby remote players, authority revision, machine ownership, or pending replication. It must not remain an authoritative collection decision.

`StorageItemTransformController.UpdateItemTransformData()` is also an indirect insertion path: when the item is not already in `StorageLostAndFound`, it calls `AddItemToLostAndFound(item, false)`. Blocking only the summoner button would therefore be incomplete.

### Confirmed caller classification

| Entry point | Caller | Meaning | Replacement policy |
|---|---|---|---|
| `AddItemToLostAndFound` | `RespawnOnDrop.RespawnOrDestroy` | Distance/fall recovery | Intercept before mutation; host policy only |
| `AddItemToLostAndFound` | `GlobalShopController.InstantiatePurchasedItems` | Purchased-item delivery/fallback | Establish owner and route deliberately |
| `AddItemToLostAndFound` | `ItemContainer.Clear` | Contents of a destroyed/cleared container | Host recovery reason, not distance loss |
| `AddItemToLostAndFound` | `GadgetItem.OnItemSaveDataLoadedInternal` | Invalid customization recovery | Import or quarantine after load |
| `AddItemToLostAndFound` | `ItemSnapPointBase.ItemSnapState.ToggleInteraction` | Shed snap/restore behavior | Guard when physical shed is inert |
| `AddItemToLostAndFound` | `StorageItemTransformController.UpdateItemTransformData` | Membership/transform repair | Local projection only, not authority |
| `MoveItemsFromWorldToLostAndFound` | `ForceSummonAllWorldItemsToLostAndFound` | Shed-button global sweep | Suppress |
| `MoveItemsFromWorldToLostAndFound` | `RequestLostAndFoundItemActivation` | Fast-travel global sweep | Suppress |

The bulk move bypasses `AddItemToLostAndFound`: it directly removes from `StorageWorld` and adds to `StorageLostAndFound` after it may have unsnapped and reparented the item. `MoveItemsFromWorldToLostAndFound()` is therefore the essential bulk-collection firewall.

`RequestLostAndFoundItemActivation()` is called by `FastTravelController.FastTravel()`. Fast travel must no longer sweep world items; the host candidate scanner handles genuine abandonment. `ForceSummonAllWorldItemsToLostAndFound()` is called only by `LostAndFoundItemsSummoner.OnSummonPressed()`.

Do not globally no-op `AddItemToLostAndFound()`: several callers represent recovery/fallback semantics. They should be routed through the host coordinator with explicit reason codes. Approved transitions may still project membership into the inert base storage.

Additional caller semantics are now confirmed:

- `AddItemToInventoryFallback()` inserts directly into `StorageLostAndFound` when inventory insertion fails; it does not run the normal Lost and Found transform/deactivation helper. Import must normalize it as `InventoryOverflow`.
- `ItemContainer.Clear()` removes each player-owned entry from the container and then sends it to Lost and Found. This is `ContainerCleared` recovery and must preserve the same owner and NetId.
- `GadgetItem.OnItemSaveDataLoadedInternal()` uses Lost and Found when a saved customization destination cannot be resolved. This is `GadgetDestinationMissing`, or quarantine when owner identity is unavailable.
- `ItemSnapPointBase.ItemSnapState.ToggleInteraction()` only repairs membership when the old or new static parent is the physical `StorageStaticParent`. Once the shed is inert, suppress this shed-specific repair rather than treating it as a new loss decision.
- `GlobalShopController.InstantiatePurchasedItems()` stages every purchase in Lost and Found and deactivates it for at least a frame. If the player is near the shop it then moves the object to World and activates it; otherwise it remains in Lost and Found. Near purchases are normal world placement. Far purchases become `PurchasedDeliveryFallback` owned by the purchaser.

Base storage membership is therefore heterogeneous and transient. Never import every member as an ordinary `OwnerDistance` record without its recovery provenance.

### `ItemDisabler` dependency

`ItemDisabler` directly treats membership in `StorageLostAndFound` as a protected/non-streamed state. It initializes from that membership and subscribes to `StorageLostAndFound.ItemAdded` and `.ItemRemoved`. While an item is marked lost, the world grid will not reactivate it.

It is constructed by `ItemBase.EndOfFrameSetupCoro`, retained on `ItemBase.itemDisabler`, and destroyed from `ItemBase.OnDestroy`. `SetActive(false)` does not remove its storage subscriptions once construction completed. A restored or newly projected item must be allowed to complete that end-of-frame setup before deactivation, or the coordinator must explicitly ensure equivalent streaming protection.

This means the base storage object should not be deleted. There are two viable projections:

1. keep each authoritative lost item in the local base `StorageLostAndFound` list, but disable all shed activation and transform-presentation behavior; or
2. patch `ItemDisabler` to consume the multiplayer authority placement instead.

The first option is initially safer and less invasive. The multiplayer registry remains the source of truth; base storage membership is only a local Unity lifecycle projection. Code must avoid `AddItemToLostAndFound()`'s transform-presentation side effects unless those effects have been explicitly neutralized.

### Save restoration and licenses

`StartingItemsController` uses Lost and Found for more than ordinary lost objects. Known sources include:

- missing general/job license safeguards;
- missing or invalid item-container references;
- missing train cars for saved world items;
- inventory overflow during restoration;
- duplicate/save reconciliation.

These are recovery and migration cases, not distance-based player loss. They must not silently become ordinary personal Lost and Found entries.

Licenses are synchronized career entitlements, not multiplayer items. Physical license papers are deliberately out of scope for the replacement Lost and Found system. The host's acquired-license sets are authoritative, clients mirror those sets, and the existing Career Manager Licenses screen is the presentation.

## Existing multiplayer foundations

The mod already has most of the vocabulary required:

- `AuthorityPlacement.LostAndFound`;
- `StorageMembership.LostAndFound`;
- persistent owner, placement player, inventory claim, claim flags, and authority revision;
- host-authoritative recall validation;
- a canonical `NetworkedItem`/NetId lookup;
- inventory and storage observability;
- existing Career Manager Harmony integration.

The new registry should extend this authority model rather than create an unrelated item identity system.

### Current implementation gaps

The vocabulary exists, but Lost and Found is not yet an authoritative runtime transition:

- `RespawnOnDropDebugPatch` only observes `RespawnOrDestroy`; it does not stop the base component from making process-local decisions.
- `StorageControllerPatch` contains old tracing experiments but is commented out, so shed activation and bulk world collection still run as base-game behavior.
- `AuthoritativeItemRegistry.PlacementFrom()` currently derives only world, hand, inventory, and attached placements from `ItemState`; no normal item snapshot can presently select `LostAndFound`.
- `ItemTransitionReason` has no explicit Lost and Found store/retrieve reasons.
- Career Manager integration currently patches fees and license purchasing only; there is no custom list/detail screen or controller input path for lost items.
- `PersistentOwnerPlayerId` is currently established from a retrieval claim. Items without a reserved/locked claim may therefore remain owner `0` even when humans would describe them as "the player's item." Lost-item eligibility must not guess an owner from `BelongsToPlayer`.

The existing `StorageControllerPatch` is entirely commented out, and `RespawnOnDropDebugPatch` is observational only. `NetworkedItemManager.SendToCache()` does destroy `RespawnOnDrop`, but only when a client scene item is being converted into an inactive reusable cache entry; that cache-specific behavior is not an existing Lost and Found override and should not be generalized to canonical lost items.

The implementation must address these gaps deliberately. In particular, do not encode a lost item as merely `Dropped` plus `activeSelf = false`; that recreates an ambiguous split between authority state and Unity presentation. The registry delta and canonical authority record must explicitly agree that the placement is `LostAndFound`.

## Proposed authority model

### Ownership

`PersistentOwnerPlayerId` is the owner of a personal Lost and Found entry. It must not be inferred from the current holder.

Examples:

- P1 drops their radio, P2 picks it up: owner remains P1, placement becomes P2's hand/inventory.
- The item must never be moved to Lost and Found while P2 is actively holding or storing it.
- If it later becomes unpossessed and qualifies as lost, it returns to P1's registry.
- An unowned scene prop with owner `0` does not enter a personal registry.

`InventorySpecs.BelongsToPlayer` and `IsEssential` are classification hints, not player identities. They are insufficient on their own to select an owner.

### Registry record

A detached host record should contain at least:

```csharp
public sealed class LostItemRecord
{
    public Guid PersistentItemId { get; init; } // host/save only
    public uint Handle { get; init; }           // compact runtime/wire identity
    public ushort NetId { get; init; }
    public byte OwnerPlayerId { get; init; }
    public uint AuthorityRevision { get; init; }
    public string PrefabName { get; init; }

    public LostItemReason Reason { get; init; }
    public DateTime StoredUtc { get; init; }
    public uint? StoredTick { get; init; }

    public DetachedVector3 LastAbsolutePosition { get; init; }
    public DetachedQuaternion LastRotation { get; init; }
    public object TrackedState { get; init; }

    public int InventoryClaimSlot { get; init; }
    public AuthorityClaimFlags InventoryClaimFlags { get; init; }
}
```

Likely reason values:

```text
OwnerDistance
BelowWorld
ManualRecovery
SaveMigration
MissingContainer
MissingTrainCar
InventoryOverflow
ContainerCleared
GadgetDestinationMissing
PurchasedDeliveryFallback
```

Recovery reasons should remain visible because some may need different UI or migration policy.

Identity has three deliberately separate layers:

```text
PersistentItemId (UUID)  durable semantic identity in the host save
Handle (uint)            compact, process-local Lost and Found list/retrieval identity
NetId (ushort)           current live multiplayer object association
```

The UUID is generated when an item first enters the host registry and is never sent in ordinary Lost and Found packets. Handles are allocated monotonically for the current host runtime, are not persisted, and are not reused during that runtime. List snapshots include the handle and current NetId; retrieval requests and results use the handle. This avoids adding a 16-byte UUID to routine packets while ensuring that a save record does not pretend a transient NetId is its durable identity.

The canonical runtime item still retains its current NetId while the host process is alive. The implementation detaches, deactivates, and retains the same `NetworkedItem` rather than creating a second live object. After save/load, the UUID remains stable while the restored physical object may receive a different runtime NetId and handle.

## Eligibility policy

The host may move an item to a player's Lost and Found only when all required conditions hold:

```text
NetId is nonzero and canonical
PersistentOwnerPlayerId is nonzero
placement is World
item is not grabbed or held
item is not in any player's inventory
item is not in an ItemContainer
item is not attached, installed, snapped, or machine-owned
no active player is within the nearby-player protection radius
item is not being created/destroyed/applied
item is not within a recent-interaction grace period
item is not in a recent throw/airborne grace period
item satisfies an explicit lost-item policy
```

The following must not enter a personal registry merely because they are distant:

- unowned scene props and clutter;
- ordinary host world items with no persistent owner;
- attached train equipment;
- items currently being processed by validators, printers, bins, or other machines;
- job artifacts unless their artifact-specific policy permits recovery;
- physical license representations;
- unresolved ID-zero objects.

World-storage membership alone is not eligibility. It indicates that a player-classified item is physically in the world, but recallable dropped items and ordinary world objects can both appear there.

### Possession and nearby-player protection

Lost and Found must never behave like an involuntary steal from another player.

If an item is in any player's hand, inventory, equipped slot, or container, it is categorically ineligible regardless of its distance from the persistent owner. The current possessor does not become the persistent owner, but their live possession protects the physical placement.

An unpossessed world item should also remain protected while any active player is nearby. Otherwise the host could remove an item that P2 is approaching, looking at, guarding, loading onto a train, or about to pick up merely because its persistent owner P1 is far away.

The host should therefore evaluate both distances:

```text
ownerDistance > ownerLostDistance
AND
nearestActivePlayerDistance > nearbyPlayerProtectionDistance
AND
both conditions remain true for the grace period
```

The nearby-player check should use authoritative absolute player positions and ignore disconnected, loading, or stale player samples. Entering the protection radius cancels an existing loss candidate. Interest/known-item membership is useful diagnostic context but should not replace an actual distance calculation.

Recommended initial policy:

```text
owner lost distance:              200 m
nearby-player protection radius:   75-100 m
candidate grace:                   10 s or longer
```

These values should be configurable after real host/client testing. The protection radius may eventually account for a player's current train or machine interaction, but the first implementation should remain conservative and avoid removing visible nearby items.

Explicit owner recall or reclaim is a different authority operation. A recallable item may be returned to its persistent owner even while held by another player if that is the intended gameplay policy, but it must go through the existing owner-recall validation and possession-revocation transition. It must not be disguised as a Lost and Found distance collection.

Normal owner recall now uses a two-phase transaction. The host first computes a candidate recall revision without changing canonical placement, then asks the authenticated owner's runtime to restore the existing Unity object into the requested silhouette slot. Only a successful prepared response may commit the candidate authority revision. A failed insertion, stale revision, timeout, or disconnect cancels the transaction; the host retains the previous canonical placement and sends that projection back to repair any partial local presentation. Local item-change observations are suppressed only while this bounded transaction is pending. Lost and Found retrieval remains a separate operation because it also changes persistent registry placement.

### Player-owned container aggregates

A player-owned item container that becomes lost is one Lost-and-Found aggregate rooted at the outermost world container. Its contents are not independently distance-collected and must not become separate Lost-and-Found rows merely because their owner is also distant.

Collection semantics are:

```text
outermost player-owned container
  -> one Lost-and-Found registry entry for the root

every contained item/container
  -> retain canonical identity, persistent owner, revision, tracked state,
     direct parent container identity, and slot
  -> no independent Lost-and-Found entry
```

The root owner determines whose Lost-and-Found page lists the aggregate. Contained items do not silently change persistent owner. This matters when P1's crate contains an item owned by P2: retrieving the crate restores the same tree and the child remains owned by P2.

Only an outermost container is eligible for distance collection. A nested container or ordinary contained item is protected by its direct container placement. Before collecting the root, the host must validate the detached containment graph: unique canonical nodes, one direct parent per child, valid slots, no cycles, and bounded depth. Invalid graphs are rejected rather than partially collected.

Collection deactivates/suppresses the root presentation but preserves the internal direct edges. It must not call `PurgeItemFromContainer` on descendants, flatten the tree, move every descendant into `StorageLostAndFound`, or allocate new descendant identities. Retrieval restores the same root object and its existing tree; contents remain inactive container members until the base inventory UI exposes/removes them normally.

An explicit owner recall for a recallable **child** remains a separate operation. It may detach that one child from a stored aggregate and return it to its persistent owner, with an atomic container-edge and authority revision update. The surrounding lost container and its other contents remain stored. Ordinary distance collection never performs this extraction.

`ItemContainer.Clear()` is different from losing an intact container. Clearing/destroying the container deliberately removes its children; each eligible child may then enter Lost and Found under `ContainerCleared`. It must not leave a registry entry that claims the destroyed container still owns those edges.

## Distance and loss detection

The host should perform one bounded, low-frequency scan over eligible canonical items. It should not allow each process's `RespawnOnDrop` to decide network placement.

Recommended initial behavior:

```text
scan interval: 1-2 seconds
default owner distance: 200 metres
required consecutive matches: 2 or more
recent interaction grace: 5-10 seconds
recent throw grace: enough for the throw to settle
```

Distance must use canonical absolute coordinates:

```csharp
absolutePosition = transform.position - WorldMover.currentMove;
```

It must compare against the persistent owner's authoritative absolute position, not the host's local player or active camera.

Use hysteresis and a stable timer so an origin shift, teleport, streaming transition, or brief position discontinuity cannot immediately remove an item. Pause evaluation while:

- the world or player is loading;
- an origin shift is in progress;
- the owner's position is stale or unavailable;
- item authority/application work is pending;
- the item is attached to a moving train or has an unresolved coordinate frame.

Distance should be a policy input, not the only loss reason. Falling below the world and explicit administrative recovery are useful independent triggers.

## Transition into Lost and Found

The host transition should be atomic from the authority model's perspective:

1. Re-evaluate eligibility immediately before mutation.
2. Capture detached tracked state and the last absolute transform.
3. Increment the authority revision and set placement to `LostAndFound`.
4. Detach the root from hands, inventory, snapping, cars, and conflicting storage memberships; preserve descendant container edges when collecting an intact container aggregate.
5. Zero rigidbody velocity/angular velocity and make the object non-simulating.
6. Deactivate the canonical Unity object without destroying its identity.
7. Add exactly one registry record under the persistent owner.
8. Broadcast the authoritative placement and registry delta.

Clients should apply the placement by removing/deactivating their existing representation. They must not independently run the base Lost and Found distance transition.

If any required detach step fails, the host should keep the prior placement and emit a rejection/invariant event instead of recording an item that remains physically present.

For container roots, the registry metadata should include an integrity fingerprint of the ordered direct-edge set for diagnostics and save/reload verification. Normal list/retrieve packets still need only the compact root handle; the full descendant graph already belongs to item/container authority and must not be duplicated into every Lost-and-Found packet.

## Career Manager retrieval

Every Career Manager terminal should expose a Lost Items page. Opening it requests or displays the authenticated player's registry view; clients never receive another player's private list unless a future administrator view explicitly allows it.

The screen should show:

```text
item display name/prefab
compact Lost and Found handle
current NetId (debug mode)
reason and time stored
recallable/reserved status
target inventory availability
Retrieve action
```

The retrieval request contains the compact Lost and Found handle and expected authority revision. The host resolves the handle to the current registry record and runtime NetId, then validates:

- requester is authenticated;
- requester matches persistent owner;
- the record still exists;
- expected revision is current;
- the item is not already being retrieved;
- a valid inventory destination is available.

Recommended first implementation: retrieve directly into the requesting player's inventory. Preserve the existing reserved owner-claim slot when one exists; otherwise let the host select a free valid slot. Then send the normal authoritative `PlayerInventory` placement and claim. This avoids creating a universal physical printer/tray at every Career Manager terminal and preserves the immovable essential-item silhouette invariant.

If the inventory is full, leave the item in the registry and return a clear failure. Never remove the record before placement succeeds.

Within a running session, retrieval reuses the same NetId and canonical object:

```text
LostAndFound -> PlayerInventory
revision + 1
same NetId
same persistent owner
registry entry removed only after success
```

For VR, the screen must use the Career Manager's existing navigable input model rather than requiring a desktop mouse.

Career Manager implementation details live in `DERAIL_VALLEY_CAREER_MANAGER_UI_LOGIC.md`. Lost and Found registers a feature with that internal API; it does not patch main-screen input directly.

## Recallable world items versus Career Manager items

An item with a live reserved/locked silhouette can still use the existing immediate recall path. The Career Manager is for items actually moved into the Lost and Found registry.

The UI may eventually display both, but they need different actions and wording:

```text
Recallable in world -> Recall now (existing claim transition)
Stored as lost      -> Retrieve from Lost and Found
Held by another     -> Reclaim/steal back, if policy permits
```

Mixing these actions would recreate the duplicate and stale-silhouette bugs already encountered.

## Removing the shed safely

The end state is that the shed does not activate or present multiplayer Lost and Found items. The likely integration points to suppress are:

- `StorageController.RequestLostAndFoundItemActivation()`;
- Lost and Found branches of `OnPlayerInActivationRange` and `OnPlayerInDeactivationRange`;
- `StorageItemTransformController.ActivateItems()` for Lost and Found;
- `StorageShedCustomization.Enable()` for Lost and Found access points;
- bulk `MoveItemsFromWorldToLostAndFound()` calls that bypass host policy.

`LostAndFoundItemsSummoner.OnSummonPressed()` does not need to be the primary enforcement point. It only forwards to `ForceSummonAllWorldItemsToLostAndFound()`. Blocking the central base collection methods prevents every shed button from performing the unsafe global sweep, while the physical button can later be disabled or repurposed for clarity.

Do not destroy the base `StorageController.StorageLostAndFound` object. Save loading, membership checks, `ItemDisabler`, and cleanup code assume it exists. It should remain inert as presentation and non-authoritative as data. Initially it may still contain the local projections of authoritative lost items so `ItemDisabler` cannot reactivate them; the host registry, not the list itself, decides what belongs there.

Recommended interception boundary:

```text
clients:
  suppress every base distance/summon collection decision

host:
  suppress base bulk collection
  run multiplayer eligibility policy
  commit canonical LostAndFound placement
  project that placement into local StorageLostAndFound membership

all processes:
  suppress shed movement and physical ActivateItems presentation
  allow authoritative retrieval to remove projected membership
```

The initial functional patches should remain conditional and narrow:

- skip `MoveItemsFromWorldToLostAndFound()` while multiplayer is active, before it unsnaps or reparents anything;
- skip `RequestLostAndFoundItemActivation()` and `ForceSummonAllWorldItemsToLostAndFound()` so their initial `DeactivateItems()` calls cannot hide projected objects accidentally;
- skip only the Lost and Found branch of `StorageController.OnPlayerInActivationRange()`;
- defensively suppress `ActivateItems()` only on `StorageController.ItemTransformControllerLostAndFound`;
- replace or suppress `RespawnOnDrop.RespawnOrDestroy()` only for multiplayer-managed player items before any mutation;
- leave ordinary non-player respawn/reset behavior untouched;
- route direct recovery callers through host reason codes instead of globally blocking `AddItemToLostAndFound()`.

Implemented authority boundary: `StorageController.AddItemToLostAndFound()` is suppressed for an
already network-managed player item on both host and client. The vanilla method is reached by local
physical-shed contact as well as several recovery helpers, so treating every invocation as
`SaveRecovery` caused thrown items touching the shed model to disappear immediately. Legitimate
multiplayer recovery must call `NetworkedLostAndFoundManager.Collect()` explicitly with its stable
reason after host validation. Unbound/base-game restoration objects still use vanilla behavior and
are classified after loading.

## Licenses

Physical license papers are not supported multiplayer possessions and must never participate in the authoritative item registry or Lost and Found. The host already owns synchronized career/license state, so recovery means repairing the entitlement/UI, not spawning a paper license into a shed or registry.

The current multiplayer flow already provides the required source of truth:

1. a client submits `ServerboundLicensePurchaseRequestPacket`;
2. the host validates the purchase and calls `LicenseManager.AcquireGeneralLicense()` or `AcquireJobLicense()`;
3. `NetworkedSaveGameManager` observes the host acquisition and broadcasts `ClientboundLicenseAcquiredPacket`;
4. every client applies the acquisition to its local `LicenseManager` mirror;
5. joining clients load the host save's license data in `StartGameData_ServerSave.DoLoad()`;
6. the client handler refreshes every live `CareerManagerLicensesScreen` after applying the packet.

This is shared campaign progression, not a per-player paper inventory. The phrase "the player's licenses" in the UI therefore means the licenses available in the current host career session.

The supplied `LicenseManager` confirms that entitlement and paper creation are separate:

- `AcquireGeneralLicense()` and `AcquireJobLicense()` update the acquired sets, unlock gameplay, update fee/bonus values, raise events, and mark save data dirty; they do not create a paper.
- `SaveData()` persists only acquired license IDs and garages.
- `LoadData()` normally restores those entitlement IDs without creating papers.
- In its tutorial-repair branch, when required tutorial entitlements are unexpectedly missing, `LoadData()` first acquires the entitlement and then explicitly calls `BookletCreator.CreateLicense(...)` at `Vector3.zero` under `WorldMover.OriginShiftParent`.

`BookletCreator_Licenses` now confirms the next step. `CreateLicense()` instantiates `license.licensePrefab` through `SpawnLicenseRelatedPrefab(..., isPlayerOwned: true)`. Unless `dontAddToStorage` is set, it:

1. sets `InventoryItemSpec.BelongsToPlayer = true`;
2. calls `StorageController.AddItemToWorldStorageAfterOneFrame(gameObject)`.

It does not directly add the paper to Lost and Found. `CreateLicenseInfo()` uses `isPlayerOwned: false`, so informational/license-preview papers do not receive this player-owned world-storage treatment.

The tutorial-repair call creates the physical license at world position zero under `WorldMover.OriginShiftParent`, with normal storage addition enabled. That makes the paper a player-classified world item far from the player; vanilla `RespawnOnDrop` can subsequently move it into Lost and Found. This explains why apparently arbitrary license papers reach the shed.

The multiplayer fix should suppress the physical-paper side effect while preserving the preceding entitlement acquisition. Existing physical license papers encountered during host save migration should be removed or ignored as legacy presentation objects; they must not receive NetIds, persistent owners, registry records, or retrieval entries. Client replicas must likewise be ignored.

Do not add a second multiplayer license list. Keep the vanilla `CareerManagerLicensesScreen`, which already reads `LicenseManager` and is refreshed by the existing packet handler. If more detail is useful later, enrich that screen's presentation from entitlement data only.

## Persistence and identity

The host save is authoritative for the registry. Persist detached records with enough information to restore the same semantic item:

```text
persistent item UUID and revision
owner player identity
prefab and tracked state
inventory claim
loss reason/time
last absolute transform for diagnostics
```

The save does not persist the compact runtime handle or treat NetId as durable identity. Player IDs are session-local, so ownership is persisted through the existing stable player GUID and rebound to the current byte player ID when that player is present.

This development version intentionally uses a clean-break format. Records without a valid `persistentItemId` UUID are rejected and removed from the pending import list. There is no legacy `net:{NetId}` token migration and no attempt to infer identity from old records. This is acceptable while the mod has only one development user and avoids carrying compatibility complexity into the unfinished state machine.

Vanilla restoration places Lost and Found objects near the end of `StartingItemsController.AddStartingItemsCoro`, after it loads the separate Inventory, LostAndFound, World, InstalledGadgets, and ItemContainers collections and applies recovery safeguards. Registry import must wait until `itemsLoaded == true`; importing earlier would miss fallback and license-repair objects created during restoration.

Recommended compatibility flow:

```text
load:
  let host vanilla restoration create one physical object
  wait for itemsLoaded
  classify/import StorageLostAndFound membership into the host registry
  keep the object inactive and shed presentation disabled
  broadcast registry and canonical item state to clients

save:
  project host registry membership into StorageLostAndFound
  let vanilla serialize prefab and tracked object data once
  persist only multiplayer metadata separately
```

Multiplayer metadata includes the persistent item UUID, stable owner identity, authority revision, loss reason/time, and retrieval claim state. Runtime handles are rebuilt after load and NetIds are rebound to the restored canonical Unity objects. Do not serialize a second physical object recipe in a parallel custom list; bind metadata back to the single vanilla-restored object during load.

`SaveGameManager.UpdateInternalData()` synchronously saves Inventory, World, LostAndFound, InstalledGadgets, and ItemContainers in that order. `OnInternalDataUpdate` fires only after those calls, so it is too late to change Lost and Found membership for the current save. Authoritative projection must remain continuously correct or run in a prefix/before-save hook. `OnInternalDataUpdate` can still write the separate multiplayer metadata chunk after vanilla storage data exists.

No additional base-game pre-save event is required for the first implementation. A narrow Harmony prefix on the known synchronous `SaveGameManager.UpdateInternalData()` is itself the before-save boundary. Any temporary client projection filtering must be restored in a postfix/finalizer even if serialization throws; longer term, continuously correct host-only projection is preferable to mutating storage lists around a save.

Only the host may persist authoritative Lost and Found membership and metadata. Clients may need hidden local projections for `ItemDisabler`, but allowing ordinary client save serialization to include replicated host or other-player objects would contaminate the client's save. Every projection needs authority/session provenance, and client storage serialization must exclude replicated Lost and Found projections deterministically.

Vanilla `StorageItemData` does not carry the multiplayer UUID. The current clean-break format therefore persists the UUID together with the exact index of its one physical object in the vanilla `StorageLostAndFound` list. After `itemsLoaded`, rebinding requires that index to exist and its prefab to agree; it never falls back to selecting the first object with the same prefab. The host then assigns the restored object its current NetId and a new compact handle. A future item-save-data extension could embed the UUID directly, but the explicit indexed contract is sufficient while this save format is development-only.

## Network protocol outline

Suggested messages:

```text
ClientboundLostItemListPacket
    full personal list after login or Career Manager activation
    compact handle + current runtime NetId; never persistent UUID

ClientboundLostItemChangedPacket
    added/updated/removed registry entry

ServerboundLostItemRetrievePacket
    compact handle + expected revision + inventory evidence

ClientboundLostItemRetrieveResultPacket
    compact handle + accepted/rejected + reason + resulting revision
```

Registry messages describe availability and UI state. The existing item snapshot protocol remains responsible for the actual canonical placement transition.

## Observability

Add a Lost and Found debug page with owner filters and these events:

```text
item.lost-evaluation
item.lost-candidate-started
item.lost-candidate-cancelled
item.lost-eligibility-rejected
item.lost-and-found.before
item.lost-and-found.after
item.lost-and-found-added
item.lost-and-found-removed
item.lost-and-found-retrieve-requested
item.lost-and-found-retrieve-accepted
item.lost-and-found-retrieve-rejected
item.lost-and-found-retrieve-applied
item.lost-and-found-invariant-violation
```

Useful fields include owner, current holder, placement, inventory claim, absolute distance, eligibility reason, grace timers, revision, storage memberships, active state, and Unity instance ID.

The self-profiler should record scan duration, eligible item count, candidates checked, transitions made, and skipped scans. This is important because the replacement is also an opportunity to remove the base game's per-item 0.2-second checking overhead.

## Pure state tests and invariants

The registry, eligibility policy, distance hysteresis, and retrieval coordinator should live in `Multiplayer.Core` and be tested without Unity.

Required tests include:

```text
one registry record per nonzero NetId
one item cannot be both World and LostAndFound
unowned world item never enters a personal registry
scene prop never enters because it is merely distant
held, inventoried, contained, attached, snapped, or machine-owned item is ineligible
recently thrown item remains in the world during grace
two consecutive distant samples are required
near sample cancels a pending candidate
another active player near the item prevents collection
another player entering the protection radius cancels a pending candidate
an item held or inventoried by a non-owner is never distance-collected
disconnecting while holding, inventorying, or containing a foreign-owned item recovers it to the persistent owner's registry
disconnect recovery includes ordinary non-red, non-recallable owned items
disconnect recovery is idempotent and cannot create both a departing-player save record and an owner LostAndFound record
nearby protection does not transfer persistent ownership
origin/player position unavailable prevents transition
persistent owner, not current holder, selects the registry
recallable-world state is not LostAndFound
explicit owner reclaim remains separate from LostAndFound collection
only the persistent owner can retrieve
stale retrieval revision is rejected
full inventory leaves the registry unchanged
successful retrieval preserves NetId and increments revision once
successful retrieval removes exactly one registry entry
retrying a completed retrieval cannot create a duplicate
save migration never imports physical licenses as ordinary items
license paper creation is suppressed without suppressing entitlement acquisition
Career Manager license presentation matches the host's authoritative acquired-license sets
client save excludes replicated LostAndFound projections
duplicate same-prefab records rebind by stable persistence token
OnInternalDataUpdate metadata corresponds to already-projected vanilla storage
inventory-overflow, container, gadget, and shop reasons are not misclassified as OwnerDistance
```

## Recommended delivery order

1. Complete dnSpy mapping and keep this document current.
2. Add pure registry, eligibility, hysteresis, retrieval-planning, and persistence DTOs with tests.
3. Add host-only candidate scanning and observability without changing behavior.
4. Replace host `RespawnOnDrop` player-item decisions and suppress them on clients.
5. Add authoritative Lost and Found placement/application and registry synchronization.
6. Add Career Manager list and retrieval UI.
7. Disable physical shed activation and bulk base-game collection paths.
8. Add save migration and suppress/clean up legacy physical license papers.
9. Run host/client, late-join, reconnect, save/reload, origin-shift, train, and VR acceptance tests.

## dnSpy targets still needed

The supplied storage, access-point, shed, respawn, streaming, caller, load-order, and save-order analysis is sufficient for the first implementation. Remaining work is narrower and does not block an MVP.

### 1. Save/load and recovery routing

The main load order, overflow fallback, direct recovery callers, and `UpdateInternalData()` save order are now known. Later migration hardening may still inspect:

```text
StorageBase.SaveStorage / StorageSerializer internals, if client projection filtering cannot be done safely at UpdateInternalData
invalid/missing container fallback
missing train-car fallback
duplicate cleanup
legacy missing-license paper safeguards, only where needed to suppress the paper after preserving entitlement repair
where stable per-item JObject/tracked persistence data can be stored
```

These cases affect migration, repair, and license policy. They do not need to block the initial host registry, distance eligibility, shed suppression, or Career Manager UI work. Unknown recovery entries should remain quarantined/inert rather than being deleted or assigned to a guessed owner.

### 2. Career Manager UI

The main screen, native dynamic-list pattern, switcher, scrolling base, interface, and input enum are documented separately in `DERAIL_VALLEY_CAREER_MANAGER_UI_LOGIC.md`. Follow that document's smaller remaining target list.

### 3. License implementation boundary

No further physical-license reverse engineering blocks the Lost and Found implementation. Treat `LicenseManager` as the gameplay source of truth, retain the existing synchronized Career Manager list, and suppress or discard physical paper side effects at their creation/import boundaries. If a future feature proves it consumes a paper object rather than an entitlement, handle that feature explicitly instead of restoring papers to the general item model.

The direct recovery caller bodies and `ItemDisabler` lifetime are now sufficiently mapped.

## Initial acceptance test

1. P1 owns and drops a recallable radio; its reserved silhouette remains.
2. P2 may pick it up without changing persistent ownership.
3. The item is never collected while held or inventoried by either player.
4. When dropped near P2, it remains in the world even if P1 is beyond the owner distance.
5. Once unpossessed, beyond the configured distance from P1, and outside every active player's protection radius for the grace period, only the host moves it to P1's Lost and Found.
6. Both processes remove the physical world representation for the same NetId.
7. P1 opens any Career Manager and sees the item; P2 does not see it in their personal list.
8. P1 retrieves it into an available inventory slot.
9. The same NetId returns, the revision increases, and no duplicate or stale silhouette exists.
10. An unowned scene prop at the same distance remains a world/scene item.
11. Entering a physical Lost and Found shed does not activate or relocate registry items.

## Implemented WIP (2026-07-14)

The first complete implementation now exists behind the multiplayer runtime:

- a pure, unit-tested collection policy, owner-partitioned registry, and retrieval planner in `Multiplayer.Core`;
- host scans every 24 network ticks with owner-distance, nearby-player protection, interaction/container protection, and a grace period;
- collection retains the canonical host Unity object as an inactive projection, removes it from client interest, and sends a Destroy update without destroying its authority record;
- retrieval preserves the NetId, checks owner and revision, chooses a free client-reported inventory slot, increments authority, restores the projection, and distributes the normal item snapshot;
- dedicated list/retrieve packets use primitive-array wire contracts and are covered by protocol round-trip tests;
- semantic records are stored under `Multiplayer.LostAndFound`, keyed to the player's persistent GUID, including records whose owner is not connected during an intermediate save;
- vanilla shed activation, bulk summon, world-to-shed collection, and managed `RespawnOnDrop` collection are suppressed while multiplayer is active;
- direct base-game recovery calls for an already-networked player-owned item are routed to the host registry;
- physical license creation is suppressed in multiplayer and legacy license papers are removed from the inert storage projection;
- collection, rejection, retrieval, and eligibility decisions emit structured inventory observability events.

Post-test invariant fixes:

- entering an inventory does not by itself make scene junk a persistently owned item; only items
  whose `InventorySpecs.BelongsToPlayer` classification is true may establish a personal owner or
  enter the virtual Lost and Found;
- a direct vanilla recovery call is suppressed, rather than collected, when the canonical object
  is still physically present in the local inventory/storage projection;
- retrieval requests carry the existing local slot for the same NetId. A reserved/dropped
  silhouette is revived in that immutable slot even though generic occupied-slot evidence marks
  it as occupied;
- the ordinary inventory Return button detects virtual Lost and Found placement and completes the
  same registry-removal/projection-restoration transaction as the Career Manager screen. On a
  client it sends the full Lost and Found retrieval request; on the host it uses `Server.SelfId`
  rather than assuming a local client object exists;
- a client receiving `LostAndFoundCollection` keeps the inactive canonical component bound to its
  NetId and ownership metadata instead of sending it through the generic destroyed-item cache;
  this preserves the inventory silhouette's Return action. Retrieval reactivates that same client
  projection in place, and authoritative personal-item metadata repairs projections corrupted by
  older builds;
- the host reconciles every virtual entry with canonical item placement. If the job validator,
  booklet reprint, or another authoritative system has already moved the item out of Lost and
  Found, the stale row is removed and a new list snapshot is pushed to its owner. A retrieval
  attempt also performs this cleanup immediately before returning `item-not-in-lost-and-found`.

The settings currently exposed are owner distance (200 m), nearby-player protection (100 m), and collection grace (10 s). All are host decisions.

Known WIP limitation: save rebinding has a persistence-token field, but existing physical save objects do not yet carry that token through Derail Valley's `ItemSaveData`. Reload therefore matches an offline record by owner GUID and prefab. Multiple lost objects with the same prefab remain individually preserved, but their semantic record-to-instance pairing can be ambiguous until a stable per-item save token is embedded. NetId and revision invariants resume after binding.

Before calling this production-ready, run the acceptance test above plus: a disconnected-owner save/reload/save cycle, two identical lost prefabs, inventory-full retrieval, stale revision, client reconnect, origin shift, train interior, VR terminal controls, and UMM unload.
