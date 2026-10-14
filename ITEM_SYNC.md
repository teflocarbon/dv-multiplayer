# Item Synchronisation: Current Design, Behaviour, and Gaps

> Status: implementation in progress
>
> Scope: the initial item-sync implementation currently present in the repository
>
> Game: Derail Valley
>
> Code reviewed: 2026-07-14

## 1. Purpose and scope

This document describes how general item synchronisation currently works, based on the implementation in `NetworkedItem`, `NetworkedItemManager`, the item packets, and the associated Harmony patches.

The implementation is not yet a complete authoritative inventory or physics system. At present it provides:

- host-assigned network identities for `ItemBase` objects;
- proximity-based creation of world items on remote clients;
- synchronisation of broad item lifecycle states such as held, stored, dropped, thrown, attached, and removed;
- per-item tracked values for a small set of useful items;
- client-side caching and reuse of vanilla item objects;
- reliable, ordered delivery for item updates;
- a host-side canonical item authority record with monotonic revisions;
- separate persistent owner, physical placement/possessor, and retained inventory claim fields;
- host-authoritative essential-item recall using the existing networked object.

It does **not** currently provide continuous rigidbody/transform synchronisation, persistent save identity across sessions, comprehensive adversarial validation, disconnect ownership policy, or robust recovery from every race and missing dependency.

## 2. High-level architecture

The host owns the canonical set of item objects and assigns each one a `ushort` network ID. Remote clients remove most of their locally spawned world items, then recreate host items as they enter the 100 metre interest radius.

```text
Derail Valley ItemBase.Awake
        |
        v
attach NetworkedItem to every item
        |
        +--> host: allocate authoritative ushort NetId
        |
        +--> client: wait for a host Create snapshot

Host NetworkedItemManager, once per network tick
        |
        +--> calculate nearby items per ready player
        +--> collect host-side dirty item snapshots
        +--> send Create for newly known nearby items
        +--> send delta or FullSync for known nearby items
        +--> send queued Destroy snapshots

Client local item interaction
        |
        +--> NetworkedItem detects state/tracked-value change
        +--> CommonItemUpdatePacket to host
        +--> host validates minimally and applies it
        +--> host relays it to other ready clients
```

The important distinction is that there are two update paths:

1. **Tick/bulk path:** host-originated changes, initial proximity creates, catch-up full syncs, and destroys use `CommonItemsBulkUpdatePacket`.
2. **Immediate/single-item path:** client-originated changes use `CommonItemUpdatePacket`, are processed by the host, then relayed immediately.

## 3. Main components

### 3.1 `NetworkedItem`

`NetworkedItem` is attached to every `ItemBase` by the `ItemBase.Awake` patch. It is responsible for:

- mapping an `ItemBase` to a network ID;
- observing item state transitions;
- observing registered item-specific values;
- creating `ItemUpdateData` snapshots;
- applying received snapshots;
- tracking the current host-side holder in `BelongsTo`;
- translating network states into Derail Valley inventory, hand, physics, and snap-point operations.

The host is authoritative for ID allocation because `IsIdServerAuthoritative` is `true`. Remote client instances remain at the default ID until a `Create` snapshot assigns the host ID.

### 3.2 `NetworkedItemManager`

The manager performs host-side interest management and client-side projection management.

Host responsibilities:

- wait until each player reaches `ReadyForItems`;
- consider an item nearby at up to 100 metres;
- retain nearby status for three seconds after it leaves the radius;
- remember which item objects each player already knows;
- send initial `Create`, dirty delta, catch-up `FullSync`, and `Destroy` data.

Client responsibilities:

- build a prefab-name-to-`InventoryItemSpec` lookup for host-requested dynamic projections;
- catalogue exact scene-authored objects and keep them dormant until bound by stable key;
- instantiate a fresh dynamic object for every received `Create` lifetime;
- destroy dynamic projections and deactivate authored projections on host retirement;
- destroy unauthorized unbound client objects after classification;
- never turn an ordinary client state update into canonical existence.

### 3.3 Per-player server state

Each `ServerPlayer` contains three item-related collections:

| Collection | Current purpose |
|---|---|
| `KnownItems` | Maps a `NetworkedItem` to the tick at which that player was last sent an update. |
| `NearbyItems` | Maps a `NetworkedItem` to the last time it was within the interest radius. |
| `OwnedItems` | Compatibility projection of canonical persistent ownership for player-owned/adopted items. It is not used as an independent transition authority. |

`AuthoritativeItemRegistry` is the host source of truth. `NetworkedItem.BelongsTo` is now the current host-side physical possessor projection, while `ServerPlayer.OwnedItems` is populated only from canonical persistent ownership. Neither is allowed to independently decide a transition.

### 3.4 Canonical item authority

Each host item has a canonical record containing:

- network ID and monotonic authority revision;
- physical placement and current placement player;
- persistent/provenance owner;
- retained inventory-claim player, slot, and lock/reserve/dropped flags;
- prefab, last world pose, and transition reason.

Every accepted host-local change, client update, temporary compatibility adoption, and owner recall is converted into a canonical transition before it is applied or broadcast. The host replaces packet holder fields with the authenticated actor and returns the resulting revision and ownership projection to the sender. A client update with an old revision is rejected as `stale-authority-revision`; a non-possessor attempting to mutate a held/inventory item is rejected as `sender-not-current-possessor`.

`TemporaryClientAdoptionCompatibility` is an explicitly temporary bridge for unconverted DV item
producers. It permits only non-authored, non-job items marked as player property and associated with
player/storage state. Its token exchange remains idempotent, but it is not general client creation
authority. Convert each producer to a named host operation, then remove this policy, its packets,
coordinators, and `ClientAdoption` transition.

`InventoryItemSpec.BelongsToPlayer` and `IsEssential` are used only to establish initial persistent ownership. They are not treated as the current holder. Physical state remains `World`, `PlayerHand`, `PlayerInventory`, or `Attached`, independently of any retained inventory reservation.

### 3.5 Essential-item recall

The inventory Get button is intercepted only while multiplayer is active. For a locally owned networked essential item it sends `ServerboundItemRecallPacket` with the item ID, expected revision, and requested inventory slot. The host validates the persistent owner and revision, changes the canonical placement to that owner's inventory, and broadcasts the same item's new state. It never creates a second item.

On every process, applying a placement for another player purges any stale local inventory/container membership before projecting the remote hand or inventory state. This is the possession-revocation step that allows an owner to retrieve an item currently borrowed by another player. Foreign-owned items are tinted and outlined red in the inventory UI, and their local Get action is disabled.

For essential items, the persistent owner's first authoritative inventory slot becomes an immovable recall claim. Owner drop, throw, and equip transitions retain that slot as `Reserved | Dropped`; a non-owner pickup additionally marks it `Stolen` but cannot replace or move it. The owner's client preserves or repairs the silhouette during world-state application. Recall clears `Dropped | Stolen` and restores that same reserved slot in place. When recall or another authoritative transfer removes the item from a borrower, that borrower's local inventory entry—including any dropped essential silhouette—is purged completely. This keeps exactly one usable recall silhouette: the persistent owner's.

Current limitation: the red indicator uses colour/outline only; it does not yet display the owner's player name or a tooltip. Lost-and-found remains a separate system and is not redirected through recall.

## 4. Initial connection and initial item sync

### 4.1 Client world preparation

After the client world finishes loading, `NetworkClient.SyncWorldState()` starts the item manager and calls `QuarantineUnboundClientItems()` before reporting `ReadyForItems`.

`QuarantineUnboundClientItems()` skips the host. On a remote client it builds the authored catalogue,
deactivates exact scene-authored objects for later stable-key binding, retains only items accepted by
the explicitly temporary compatibility-adoption policy, and destroys every other unbound dynamic
object. There is no prefab-name pool and no generic retained-inventory exception.

### 4.2 Loading-state handshake

The client sends `ReadyForItems` after train sets and restoration states have loaded. The server's direct handler for that state does not send an explicit all-items snapshot. Instead, the regular item-manager tick begins considering that player eligible for item updates.

The client waits only 0.25 seconds before advancing to `ReadyForJobs`; there is no item-sync completion acknowledgement or item count barrier.

### 4.3 Proximity discovery

On each host tick, `UpdatePlayerItemLists()` checks every registered host item against every player at or beyond `ReadyForItems`.

- Within 100 metres: the item is inserted or refreshed in `NearbyItems`.
- Outside 100 metres: its timestamp is not refreshed.
- More than three seconds since its last refresh: it is removed from `NearbyItems`.

The item is **not** removed from `KnownItems` when it leaves range, and no range-based `Destroy` is sent. Consequently, proximity currently controls update eligibility and first creation, not client object lifetime.

### 4.4 First creation for a player

For every nearby item absent from `KnownItems`, the host creates an `ItemUpdateType.Create` snapshot. A create contains:

- the host network ID;
- the prefab name;
- the current high-level item state;
- state-dependent placement/ownership data;
- all registered tracked values.

The item is then marked known at the current network tick. For a dynamic record, the remote client
instantiates a fresh object from `Globals.G.Items`, assigns the host ID, and applies the snapshot. For
an authored record, it binds the exact dormant catalogue object by stable authored key.

If the same network ID already exists on the client, the invalid dynamic projection is destroyed or
the authored projection is returned to dormancy before the authoritative representation binds.

### 4.5 Items deliberately intended to use another sync system

Job overviews, booklets, and reports are intended to be excluded from general proximity creation because the job system creates them with job-specific context. `DoNotCreateItem()` contains these exclusions.

However, the active caller passes `nearbyItem.GetType()`, which is always `NetworkedItem`, rather than `TrackedItemType`. The exclusion therefore does not currently match any of the intended job item types. This is a concrete implementation bug, not merely unfinished design.

## 5. Item identity and lookup

Host item IDs are allocated from an `IdPool<ushort>` through `IdMonoBehaviour`. `NetworkedItem` also maintains:

- an inherited ID-to-object lookup;
- a static `ItemBase`-to-`NetworkedItem` lookup.

Network IDs are used as the sole runtime identity in item packets. Prefab names are included only in `Create` snapshots and are used to obtain the appropriate client object.

There is no persistent item GUID in this general sync path. A network ID identifies an item only for the current loaded network session.

## 6. Snapshot model and wire format

### 6.1 Update flags

`ItemUpdateData.ItemUpdateType` defines:

| Flag | Meaning in current implementation |
|---|---|
| `Create` | Create/reconcile an item and include prefab name, state data, and all tracked values. |
| `Destroy` | Retire the client projection. Dynamic objects are destroyed; authored objects become dormant. Only ID is serialized. |
| `ItemState` | Send a high-level lifecycle transition. |
| `ItemPosition` | Defined, but not independently implemented end to end. |
| `ObjectState` | Send dirty registered values. |
| `FullSync` | Combination of item state, position, and object state flags. |

`ItemPosition` by itself is currently ineffective: the serializer only writes placement when `Create` or `ItemState` is present, and `ApplySnapshot()` does not handle a standalone position flag. It works incidentally inside `FullSync` because `FullSync` also contains `ItemState`.

### 6.2 High-level item states

| State | Detection | Data sent | Receive behaviour |
|---|---|---|---|
| `Dropped` | Parent is `WorldMover.OriginShiftParent` and the item was not just thrown; otherwise also the fallback state. | Absolute-world position relative to `WorldMover.currentMove`, plus rotation. | Activate, move, and rotate. |
| `Thrown` | `GrabHandlerItem.Throw` patch calls `OnThrow`. | Captured release position/rotation and throw direction. | Move to release pose and invoke the local throw handler. |
| `InHand` | `Item.IsGrabbed()`. | Player ID. | Resolve `NetworkedPlayer` and place the object in that player's hand. |
| `InInventory` | Local `Inventory` contains the object. | Player ID. | Add it to the resolved player's inventory, or disable it if the player cannot be resolved. |
| `Attached` | `SnappableItem.IsSnapped`. | Train-car network ID and front/rear coupler selection. | Find the train and coupler snap point and call `SnapItem`. |
| `Removed` | Gadget remover patch sets a one-shot flag. | No target/context beyond item ID. | Call `GadgetBase.Remove(true)`. |

`PrepareForStateChange()` first unsnaps the item and drops it from the previous remote player's right hand where applicable. It then resolves the new player ID.

### 6.3 Position model

World positions are sent with `WorldMover.currentMove` subtracted and restored on receipt. This keeps snapshots stable across Derail Valley origin shifts.

There is no periodic position stream for ordinary items. Positions are sent for creation, full state transitions involving a dropped/thrown item, and full sync. Resting physics motion after a transition is not observed unless some other dirty event causes a later full snapshot.

### 6.4 Tracked object values

Useful item patches register named getter/setter pairs in `TrackedValue<T>`. Each value stores the last sent value and is dirty when its comparer reports a meaningful difference.

Supported wire types are currently:

- `bool`;
- `int`;
- `uint`;
- `float`;
- `string`.

Create and full-sync snapshots include all values. Object-state deltas include only dirty values. Received snapshots are queued until the item-specific patch calls `FinaliseTrackedValues()`, preventing a snapshot from being applied before its setters exist.

The currently registered values are:

| Item/component | Key | Authority/threshold notes |
|---|---|---|
| Flashlight | `originalLightIntensity` | Server-authoritative. |
| Flashlight | `originalBeamColour` | Server-authoritative, encoded as `uint`. |
| Flashlight | `beamColour` | Server-authoritative, encoded as `uint`. |
| Flashlight | `batteryPower` | Server-authoritative; sends at differences of at least 1, and at boundary values. |
| Flashlight | `buttonState` | Client-changeable. |
| Lantern | `wickSize` | Client-changeable. |
| Lantern | `Ignited` | Client-changeable. |
| Lighter | `isOpen` | Client-changeable. |
| Lighter | `Ignited` | Client-changeable. |
| Shovel | `coalMassCapacity` | Client-changeable. |
| Shovel | `coalMassLoaded` | Client-changeable. |
| Gadget switch | `value` | Client-changeable. |

All other `ItemBase` objects receive identity and lifecycle tracking but no item-specific state values.

## 7. Change detection and runtime propagation

### 7.1 Local change detection

Every `NetworkedItem` runs a `LateUpdate()` state check. A snapshot is produced when:

- an event marked `stateDirty` (grab, ungrab, throw, or gadget removal);
- the computed high-level state differs from `lastState`; or
- a registered value is dirty.

Host-side `NetworkedItemManager.ProcessChanged()` also calls `GetSnapshot()` for every item once per network tick. Because `GetSnapshot()` consumes the dirty state, the frame path and tick path can race to be the first consumer on a listen host. The implementation appears designed to allow host-local changes to travel through the local client/server path, but this split ownership of dirty consumption is fragile and should be made explicit.

### 7.2 Client-to-host path

A client sends one `CommonItemUpdatePacket` using `ReliableOrdered`. The server:

1. identifies the sender from the transport peer;
2. overwrites `PlayerId` with the authenticated sender ID;
3. looks up the host item by network ID;
4. calls `Server_ReceiveItemUpdate()`;
5. rejects client `Create` and `Destroy` flags;
6. performs the current holder-conflict checks;
7. updates `BelongsTo` based on the state;
8. applies the snapshot on the listen host where necessary;
9. relays the original snapshot to every other ready client.

The sender is excluded from the relay and keeps its locally predicted result.

### 7.3 Host-to-client tick path

The host gathers one dirty snapshot per item, then builds a different bulk list for each player:

- unknown + nearby: `Create`;
- known + nearby + dirty this tick: the dirty snapshot;
- known + nearby + player's recorded tick older than `LastDirtyTick`: `FullSync`;
- otherwise: nothing.

This catch-up mechanism lets a known item receive a full state after missing a host-originated change while out of range.

### 7.4 Destruction

Destroying a host item outside scene unloading creates a `Destroy` snapshot and removes the object from every player's known/nearby sets. The manager adds that destroy to the next bulk update for each eligible player, then clears the destroy list.

On a client, retirement destroys a dynamic projection. An exact scene-authored projection is instead
deactivated and retained for later stable-key re-entry. Lost and Found and cold containers retain
their explicit subsystem-specific retirement semantics.

## 8. Player inventory and save-data interaction

There are currently two distinct mechanisms which should not be confused:

1. General runtime item sync (`ItemUpdateData` and `NetworkedItem`) handles world/hand/inventory states for live host items.
2. `PlayerItemSaveData` is placed in `ClientboundSaveGameDataPacket` and converted into Derail Valley `StorageItemData` while the joining client builds its local save state.

The save packet currently populates `PlayerItems` from a hard-coded test list (shovel, lighter, oiler, lantern, flashlight, hanger, and duct tape), rather than serialising the connecting player's authoritative inventory. The test records do not carry meaningful network IDs into `StorageItemData`.

This means initial personal inventory sync is scaffolding and is not yet reconciled with the host's runtime `NetworkedItem` identities or `BelongsTo` ownership. Duplicate retained client inventory items and later host-created items are therefore a known design risk.

## 9. Client projection lifetime

There is no generic dynamic item cache. A dynamic Unity projection represents exactly one canonical
host lifetime and is destroyed when that lifetime is retired. This deliberately favors correctness
over allocation savings: component state, subscriptions, tracked values, and other untracked prefab
state cannot leak into a different logical item.

Authored objects are different. Their Unity identity is part of the loaded scene and may be
referenced by other scene components, so the exact object is deactivated outside host interest and
rebound by stable authored key on re-entry. It is never selected by prefab name or repurposed as
another logical item.

## 10. Known issues and gaps

The following findings are grouped by likely impact. Severity describes the potential effect, not the maturity expected from an initial implementation.

### 10.1 Critical/high-impact correctness and security gaps

#### A. Network ID lookup entries are not removed when an ID changes or an object is normally destroyed

`IdMonoBehaviour.NetId` releases the old ID and registers the new one, but it does not remove the old key from the static ID-to-object dictionary. Normal `OnDestroy()` also does not remove the individual entry; the dictionary is cleared only during a particular unload path.

Consequences include stale lookup entries after ordinary identity changes or destruction, delayed
packets resolving an object after its logical lifetime, and unpredictable collisions after ID reuse.
Generic dynamic projection reuse has been removed, which eliminates the cross-lifetime pooling case,
but lookup removal must still remain correct for authored dormancy and normal destruction.

#### B. Initial creation is proximity-filtered, but relayed client updates are not

The tick path sends creates only to nearby players. In contrast, `Server_ReceiveItemUpdate()` relays a client update to all other clients at `ReadyForItems`, regardless of `KnownItems` or range.

A distant client which has never received the item create will receive an update for an unknown network ID and discard it with a warning. If it later enters range, the create provides current state, but this produces avoidable errors and makes event delivery dependent on whether the item happened to be known.

The live relay should use the same interest/known-item policy as creation, or force a create/full snapshot before an update.

#### C. Server-authoritative tracked values can still be relayed from a client

On the host, `ApplyTrackedValues()` refuses a client change to a tracked value marked server-authoritative. However, the server then relays the original, unfiltered client snapshot to other clients. Remote clients do not perform that authority rejection and will apply the value.

Thus a client can potentially make other clients display a forged flashlight battery, beam colour, or intensity while the host retains the canonical value. The host must remove/rewrite rejected keys before relay, or construct the outgoing update from accepted host state.

#### D. Validation is minimal and rejection has no correction path

The active validation checks only:

- clients may not send `Create` or `Destroy`; and
- another player may not take/update an item while `BelongsTo` names a different holder for a subset of states.

It does not currently validate:

- pickup or interaction distance;
- whether the sender knows or is near the item;
- whether the sender previously owned an item before dropping, throwing, attaching, or removing it;
- valid state transitions;
- attachment target existence, distance, or availability;
- tracked-value ranges or which client may change each key;
- packet flag combinations or enum values.

There is older validation code in `NetworkedItemManager`, including reach checks, but it begins with `return true` and is not used by the active packet path.

When validation does fail, the server silently rejects the operation from the sender's point of view. Because the sender is prediction-based and receives no authoritative correction, it remains desynchronised. Simultaneous-grab races have the same problem for the losing client.

#### E. Ownership is not cleaned up on disconnect

`NetworkedItemManager.PlayerDisconnected()` throws `NotImplementedException`, and the server disconnect subscription is commented out. An item whose `BelongsTo` points at a disconnected player can remain locked against other players.

The unused `OwnedItems` collection further increases the risk of two ownership sources diverging. One canonical ownership model and explicit disconnect transfer/drop rules are needed.

### 10.2 Initial-sync and lifecycle gaps

#### F. Personal inventory sync is test data, not authoritative inventory data

`ClientboundSaveGameDataPacket.CreatePacket()` currently creates a hard-coded item list. It does not enumerate the connecting player's stored server inventory, preserve runtime network IDs, or establish host ownership for the resulting client objects.

The temporary compatibility-adoption path still exists for player-marked objects produced by DV
systems not yet converted to explicit host operations. Those producers remain migration work, but
unrelated unbound inventory/world objects are no longer retained or pooled.

#### G. There is no item-sync completion barrier

`ReadyForItems` enables the tick path, but the client advances after a fixed 0.25 second delay. There is no count, end marker, or acknowledgement confirming that relevant creates have been received and applied.

This can race with job creation and any subsystem that expects referenced items to exist. It also makes loading behaviour dependent on tick rate, frame rate, network latency, and batch size.

#### H. Intended job-document exclusion is ineffective

As described earlier, `DoNotCreateItem(nearbyItem.GetType())` checks `NetworkedItem` rather than the wrapped tracked item type. Job documents may consequently be created by both item proximity sync and job sync.

Additionally, job item wrappers are initialized as useful items but do not register/finalise tracked values in the general item patches, so a mistakenly received general snapshot can remain queued indefinitely.

#### I. Leaving interest range must retire the correct projection kind

Removing an item from `NearbyItems` stops host tick updates but leaves the item in `KnownItems` and alive on the client. This may be an intentional bandwidth-only culling policy, but it should be named as such.

If object-count culling is desired, the protocol needs a distinction between temporary interest removal and authoritative destruction. Reusing `Destroy` for both without care would lose identity/history semantics.

#### J. Destroy delivery is one-tick and global

Every destroy is appended to every eligible player's next bulk packet, including players who never knew the item. Unknown destroyed IDs must remain null-safe; a known dynamic projection is destroyed and a known authored projection becomes dormant.

Players below `ReadyForItems` are skipped, after which `DestroyedItems` is cleared. A player which retained a special/essential local version may never receive the removal. Destroy should be targeted to known players, null-safe, and represented in initial authoritative state for late joiners.

### 10.3 State and physics gaps

#### K. There is no continuous transform or rigidbody synchronisation

Only lifecycle events carry transforms. Thrown objects simulate independently on each peer after the common release pose and direction. Differences in physics timing, collisions, floating-origin timing, or frame rate can leave different resting positions.

No later correction occurs unless another state event or catch-up condition causes a full snapshot. The unused standalone `ItemPosition` flag suggests this area is planned but incomplete.

#### L. State detection has ambiguous fallbacks

Any item which is not recognized as grabbed, inventory-held, snapped, just removed, or parented under the expected world origin falls back to `Dropped`. Items parented to other containers or modded attachment systems can therefore be represented incorrectly.

The parent check also precedes several other checks. Correctness depends on Derail Valley's exact transform parenting during each interaction.

#### M. Attachment failures are not retried

If the referenced train car or snap point does not yet exist, or `SnapItem` fails, the client logs a warning and stops. There is no dependency queue, retry, or corrective full state. The item may remain active at an old/default position.

#### N. Player-resolution failures are not retried

If an `InHand` or `InInventory` snapshot references a player which the receiving client has not created, the item is disabled. There is no queued retry when that player later appears.

#### O. VR/hand selection is incomplete

`ItemUpdateData.PlayerHand` exists but is neither serialized nor used. Cleanup explicitly checks only `RightHandItemGO`, with a TODO for VR. Left/right-hand ownership and two-hand edge cases are therefore unsupported.

### 10.4 Tracked-value and projection gaps

#### P. Tracked-value schema is implicit

Keys and types are string-based and must match on every peer. Unknown keys are only logged. Unsupported types throw, and serialising a null value would fail while attempting to inspect its type.

There is no per-item schema/version marker, duplicate-key prevention, range validation, or compatibility negotiation beyond the broader protocol manifest.

#### Q. Authored re-entry must reset network lifetime without resetting scene identity

Dynamic projection reuse has been removed. Exact authored objects still survive interest retirement
because other scene components may reference them. Their network identity, authority metadata,
pending snapshots, and presentation must be reset before stable-key rebinding without erasing their
authored classification or unrelated scene wiring.

#### R. Useful-item registration can stall snapshot application

Useful items queue all snapshots until `FinaliseTrackedValues()` is called. If a patch exits early (for example, the shovel cannot find its coal component or the gadget switch cannot find its LOD), finalisation may never occur and the queue can grow indefinitely.

There should be a fail-safe finalisation/error state and a bounded pending queue.

### 10.5 Performance and observability gaps

#### S. Interest calculation is an all-players by all-items scan

Every network tick computes distance from every ready player to every registered item. This is `O(players * items)` and allocates lists/dictionaries in several parts of the path. It may be acceptable initially but will scale poorly with large worlds, many dropped items, or higher tick rates.

Spatial partitioning, staggered scans, or Derail Valley tile/streaming data could reduce this cost.

#### T. Bulk packet compression code is disabled

The former custom raw/compressed implementation in `CommonItemsBulkUpdatePacket` is entirely commented out. The active packet framework serializes the object graph, but the explicit “compress after 50 items” behaviour is not active.

Large initial nearby batches should be profiled for packet size, fragmentation, serialization cost, and transport limits.

#### U. Error handling often logs and drops without recovery

Missing prefabs, items, players, cars, tracked keys, or snap points generally produce a warning/error and discard the operation. There is no request-resync packet or retry budget. A single ordering problem can therefore become a permanent client divergence.

## 11. Recommended completion plan

The following order addresses identity and authority before adding more item types.

### Phase 1: Make identity and ownership safe

1. Remove old ID dictionary entries whenever `NetId` changes.
2. Remove normal destroyed objects from all lookup maps.
3. Avoid registering ID `0` as a real lookup key.
4. Choose one ownership source (`BelongsTo` or a server-owned ID map) and delete/derive the other.
5. On disconnect, authoritatively drop, store, or release every held item and broadcast the result.
6. Add an authoritative correction/rejection response for predicted client actions.

### Phase 2: Unify interest management and delivery

1. Apply `KnownItems`/nearby filtering to immediate relays.
2. If a recipient is eligible but unknown, send `Create` before the delta.
3. Target destroys only to players who knew the item and make unknown destroys harmless.
4. Decide whether range culling means bandwidth-only or object despawn, and encode that distinction.
5. Add a resync request for unknown IDs and failed dependency application.

### Phase 3: Finish initial inventory sync

1. Replace the hard-coded `PlayerItems` list with server player inventory data.
2. Define persistent identity/reconciliation between save items and runtime network items.
3. Define whether inventories are private, globally replicated, or replicated only while visible/held.
4. Reconcile or remove retained client-local inventory items before accepting runtime creates.
5. Add an explicit initial-item-sync end marker/acknowledgement rather than a fixed delay.

### Phase 4: Harden state authority

1. Validate reach, previous ownership, allowed transitions, attachment target, and value ranges.
2. Build relayed snapshots from accepted host state; never forward rejected fields.
3. Implement or remove standalone `ItemPosition`.
4. Add dependency queues/retries for player and train references.
5. Implement hand selection for VR and left/right hands.

### Phase 5: Physics, projections, and scale

1. Decide which items need periodic transform/rigidbody correction after throws.
2. Add sleep/rest snapshots or low-rate authoritative corrections rather than full continuous physics for every item.
3. Verify authored dormancy/re-entry reset invariants and bound snapshot queues.
4. Fix the job-item exclusion to use `TrackedItemType`, and document ownership between job sync and general item sync.
5. Profile spatial scans and bulk packet size under realistic worst cases.

## 12. Suggested test matrix

The current implementation would benefit from repeatable two- and three-player tests covering:

| Scenario | Expected invariant |
|---|---|
| Join near a dropped item | Exactly one client item exists with the host ID, pose, and tracked values. |
| Join outside range, then approach | No unknown-ID errors; one create is applied on entry. |
| Two clients grab simultaneously | One host winner; loser is corrected to the authoritative result. |
| Holder disconnects | Ownership is released and all peers see an accessible item. |
| Throw into collisions on all peers | Resting pose converges within a defined tolerance. |
| Modify flashlight battery on a client | Host rejects it and no other client applies the forged value. |
| Update an item unknown to a distant client | Distant client receives neither an unusable delta nor a permanent desync. |
| Destroy an item unknown to a client | No exception; no later ghost create. |
| Retire dynamic ID A, create same prefab as ID B, deliver late A packet | Fresh projection B cannot be resolved or mutated through A. |
| Join with inventory items | No duplicate local/host copies; IDs and ownership are canonical. |
| Attach before train dependency exists | Attachment is retried and eventually converges. |
| Job overview/booklet/report creation | Exactly one subsystem creates each document. |
| Origin shift during drop/throw | All peers resolve the same absolute world pose. |
| Missing useful-item component | Snapshot queue stays bounded and item reaches a defined fallback state. |

Useful stress cases include hundreds of nearby items, repeated enter/leave cycles, rapid grab/drop/throw, item destruction during join, packet latency/loss simulation, and ID reuse after long sessions.

## 13. Code map

| Area | Primary files |
|---|---|
| Item identity, state, snapshots, validation | `Multiplayer/Components/Networking/World/NetworkedItem.cs` |
| Interest management, bulk sync, caching | `Multiplayer/Components/Networking/World/NetworkedItemManager.cs` |
| Base network-ID implementation | `Multiplayer/Components/IdMonoBehaviour.cs` |
| Per-player known/nearby/owned state | `Multiplayer/Networking/Data/ServerPlayer.cs` |
| Item snapshot flags and serialization | `Multiplayer/Networking/Data/Items/ItemUpdateData.cs` |
| Dirty-value abstraction | `Multiplayer/Networking/Data/Items/TrackedValue.cs` |
| Bulk and single packets | `Multiplayer/Networking/Packets/Common/CommonItemsBulkUpdatePacket.cs`, `CommonItemUpdatePacket.cs` |
| Client receive/send and load order | `Multiplayer/Networking/Managers/Client/NetworkClient.cs` |
| Server receive/relay and loading state | `Multiplayer/Networking/Managers/Server/NetworkServer.cs` |
| Automatic item registration | `Multiplayer/Patches/World/Items/ItemBasePatch.cs` |
| Useful-item tracked fields | `FlashlightPatch.cs`, `LanternPatch.cs`, `LighterPatch.cs`, `ShovelPatch.cs`, `GadgetSwitchPatch.cs` |
| Throw/removal hooks | `GrabHandlerItem.cs`, `GadgetRemoverPatch.cs` |
| Client starting inventory data | `ClientboundSaveGameDataPacket.cs`, `StartGameData_ServerSave.cs`, `PlayerItemSaveData.cs` |
| Job document overlap | `BookletCreatorPatch.cs`, `NetworkedStationController.cs`, `NetworkedJob.cs` |

## 14. Current design summary

The initial implementation has a useful foundation: host-issued IDs, a compact event/state snapshot, reusable tracked values, reliable delivery, origin-shift-aware positions, and basic proximity-based discovery. Its central limitation is that identity, interest, ownership, and prediction are not yet enforced as one coherent authoritative system.

The most important next step is not adding more tracked item types. It is making network-ID lifecycle, recipient selection, accepted-field relay, ownership cleanup, and rejection recovery reliable. Once those invariants are established, persistent inventory reconciliation and additional item behaviours can be built on top without multiplying desynchronisation cases.
