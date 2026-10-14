# Multiplayer Item World Synchronization Design

> Status: implementation complete pending the deferred two-instance runtime scenario pass. The
> host-authoritative path has replaced distance-only world-item discovery; there is deliberately no
> prefab-name compatibility fallback for scene-authored projections.

## Goal

Make the host the only authority for world-item identity, lifecycle, placement, ownership, state,
and persistence. Clients receive bounded projections for the world cells and player-bound items
relevant to them. A client can request an interaction, but it cannot introduce a scene-authored item
or commit an item state transition.

Derail Valley already supplies the spatial and presentation foundations:

- all authored world items are scene-resident and distance-hidden rather than destroyed;
- `ItemDisablerGrid` divides the absolute world into 128 metre cells;
- the current cell and its eight neighbours form the native active item region;
- `WorldStreamingInit.IsSceneAndTerrainRegionLoaded` gates safe item reactivation;
- the floating origin is removed before grid calculation.

Base-game findings are maintained separately in
[`research/DERAIL_VALLEY_WORLD_ITEM_STREAMING_LOGIC.md`](research/DERAIL_VALLEY_WORLD_ITEM_STREAMING_LOGIC.md).
The canonical cross-feature item state model, including the distinction between possession,
ownership, recovery, and disposable stock, is maintained in
[`MULTIPLAYER_ITEM_LIFECYCLE_DESIGN.md`](MULTIPLAYER_ITEM_LIFECYCLE_DESIGN.md).
Installed gadget, train-customization, and wiring findings are maintained in
[`research/DERAIL_VALLEY_GADGET_CUSTOMIZATION_LOGIC.md`](research/DERAIL_VALLEY_GADGET_CUSTOMIZATION_LOGIC.md).
Snapped accessory and train-coupler attachment findings are maintained in
[`research/DERAIL_VALLEY_ITEM_SNAPPING_LOGIC.md`](research/DERAIL_VALLEY_ITEM_SNAPPING_LOGIC.md).

## Implemented architecture

The current implementation includes:

- pre-migration stable authored keys captured from `game_content_w3` objects;
- inactive-inclusive host/client catalogues with count, collision, and digest negotiation;
- hard quarantine of every unbound client object, including inactive objects later awakened by
  DV's distance optimizers: exact authored objects become dormant, unauthorized dynamic objects
  are destroyed, and only the temporary compatibility-adoption policy may retain an unbound item;
- host-owned 128 metre cell indexes and exact 3 by 3 interest membership;
- per-recipient Create/retire/re-enter projection lifetimes and compact session acknowledgements;
- exact authored-key binding, with dynamic prefab instantiation kept as a separate path;
- host validation of snapshot-shaped logical interaction requests, authority revisions, possession,
  placement distance, and train/static-parent anchors;
- durable UUID identity for dynamic records without sending UUIDs in ordinary packets;
- parent-relative train-interior and static-parent placement;
- authored overrides, dynamic world records, and authored tombstones in `WorldItemsV1`;
- exclusion of multiplayer-owned world/inventory entries from vanilla storage persistence;
- durable player GUID rebinding across disconnect/reconnect;
- authored identity handoff through cold-container and Lost and Found detachment/materialization;
- compact protocol fixtures and detached authority/codec tests.

High-frequency rigidbody authority remains intentionally outside this implementation. The existing
physics presentation continues to operate behind the new logical authority boundary until that
separate project is undertaken. The separate spatial-motion, settlement, and delegated-simulation
design is maintained in
[`MULTIPLAYER_ITEM_SPATIAL_PHYSICS_SYNC_DESIGN.md`](MULTIPLAYER_ITEM_SPATIAL_PHYSICS_SYNC_DESIGN.md).

## Non-goals

- Do not network terrain, buildings, meshes, or decorative static scenery.
- Do not replace Derail Valley's terrain or scene streamer.
- Do not continuously simulate all distant Unity objects on the host.
- Do not redesign high-frequency rigidbody, collision, or thrown-item physics replication here.
  Network physics is specified separately in
  [`MULTIPLAYER_ITEM_SPATIAL_PHYSICS_SYNC_DESIGN.md`](MULTIPLAYER_ITEM_SPATIAL_PHYSICS_SYNC_DESIGN.md).
- Do not allow prefab-name matching to remain a second compatibility path.
- Do not send persistent UUIDs in routine movement/state packets.
- Do not preserve two competing item save authorities.
- Do not implement future container ACLs as part of world sync; current access remains owner-only.

## Core invariant

> The host's authoritative item registry is canonical. A Unity GameObject on the host or a client is
> a disposable presentation of that record and can never become authoritative merely because it is
> active, interactive, locally simulated, or present in a vanilla storage list.

This extends the model already used by cold containers and Lost and Found to ordinary world items.

## Authority boundaries

The host owns:

- persistent identity;
- runtime NetId allocation and retirement;
- stable authored-object identity;
- absolute transform and native grid cell;
- placement and holder;
- persistent owner and inventory claim;
- container and Lost and Found membership;
- item-specific serialized state;
- lifecycle/tombstones;
- authoritative revision;
- saved state.

Clients own only:

- local presentation and prediction;
- input intent;
- acknowledgement that a projection was found and applied;
- reporting local readiness or an explicit projection failure.

## Spatial model

World interest uses Derail Valley's native item cell calculation:

```csharp
Vector3 absolute = unityPosition - WorldMover.currentMove;
int cellX = Mathf.FloorToInt(absolute.x / 128f);
int cellZ = Mathf.FloorToInt(absolute.z / 128f);
```

The default subscribed world region is the player's current cell plus its eight neighbours, matching
`ItemDisablerGrid.IsCoordActive`.

```text
NW  N  NE
 W  P   E
SW  S  SE
```

Cell coordinates are derived independently on the host from authoritative absolute player
positions. The client may report region readiness, but it cannot choose which authoritative records
belong to a cell.

### Interest is not only spatial

The following records remain relevant independently of their world cell:

- items in the recipient's inventory or hand;
- items held by another nearby/visible player;
- equipped or attached items whose parent entity is relevant;
- an opened container shell and its bounded cold-data view;
- owner Lost and Found list metadata;
- active job artifacts required by a relevant job/station lifecycle.

Spatial interest and placement interest are combined by the host into one recipient projection set.

## Catalogue bootstrap

### Host catalogue

After the world, items, and storage loading phases are ready, the host performs one inactive-inclusive
catalogue pass over scene-authored `ItemBase` objects. For each object it records:

```text
SceneItemRecord
  stableSceneKey
  canonicalIdentityInput
  prefabName
  defaultAbsolutePosition
  defaultAbsoluteRotation
  currentAbsolutePosition
  currentAbsoluteRotation
  currentCell
  placement
  persistentOwner
  authorityRevision
  itemStatePayload
  runtimeNetId
  lifecycle
```

This is a one-time scan. Runtime movement updates spatial indexes incrementally.

### Client catalogue

Each client performs the same inactive-inclusive authored-object discovery, but the resulting objects
are dormant projections:

- no self-assigned authoritative NetId;
- no outgoing state declaration;
- no interaction until host binding;
- no adoption merely because DV activates an ancestor;
- indexed by stable scene key;
- retained rather than destroyed when interest is retired.

The client sends a catalogue version/count/hash after discovery. A mismatch is a visible loading or
debug error, not permission to bind the next object with a matching prefab name.

### Stable scene key

The identity input must be captured before DV migrates authored content out of its loading scene:

```text
authored loading-scene path/name
+ complete pre-migration hierarchy path
+ deterministic sibling discriminator at each ambiguous level
+ ItemBase/component discriminator
```

The full input remains available in debug data. A fixed-size hash is used on the wire and in the
runtime lookup. Catalogue construction must detect collisions and refuse ambiguous entries.

Runtime inspection followed Unity instance `1098658` from registration as
`game_content_w3/[origin shift content]/Offices/.../ElectricStove` to its final location as
`game_w3/origin_shift_parent/ElectricStove`. DV preserves the object but changes its scene and
flattens its hierarchy. Therefore an after-load scan cannot reconstruct authored identity.

`WorldStreamingInit.LoadingRoutine` defines the first transition exactly. It synchronously loads
`game_content_w3`, yields one frame, reparents every direct child of `[origin shift content]` to
`origin_shift_parent`, and moves the remaining content-scene roots into the active `game_w3` scene.
Loose-item physics then flattens individual objects further: `ItemReparentingBase.OnCollisionEnter`
selects `WorldMover.OriginShiftParent` when no valid train/static parent exists, and `ParentItem`
forces sibling index `999`. The catalogue must not depend on either post-transition hierarchy.

The integration must capture the identity in `NetworkedItem.Awake`/`Register` while the staging
hierarchy still exists, associate it with the surviving object across migration, and only
finalize/count/hash the catalogue from `WorldStreamingInit.LoadingFinished`. A lightweight identity
component or lifecycle-owned object-to-key map can carry it. The captured default catalogue record
must survive even if save restoration removes or relocates its Unity projection. Client and host must
run the identical capture algorithm. Objects created after staging without such an identity are
dynamic items, not silently adopted authored items.

The existing integration already supplies this hook: `ItemBase_Patch` postfixes `ItemBase.Awake` and
immediately adds `NetworkedItem`, causing `NetworkedItem.Awake` to execute before the synchronous
scene load returns to `WorldStreamingInit`. Identity capture belongs at the beginning of that method
or its `Register` path; patching the loader coroutine is unnecessary.

`Scene.handle`, the final `game_w3` sibling index, and the flattened runtime path are diagnostic only.

### Separation from vanilla storage materialization

`StartingItemsController` creates every saved world/storage record as a new instance of
`Resources.Load(itemPrefabName)` at a safety position, then applies transform, parent, inventory,
container, and `ItemSaveData` state. It does not bind the record back to an authored scene object, and
`StorageItemData` carries no authored stable key.

Therefore an authored multiplayer-managed item must not be persisted as an anonymous vanilla world
record. Multiplayer persistence stores its stable authored key plus override/tombstone state. A saved
dynamic item stores a complete dynamic record. During the transition period, the save boundary must
ensure that exactly one system owns each item; otherwise the next load can produce both the default
authored projection and a generic prefab materialization.

Unity instance IDs, runtime NetIds, floating-origin positions, display names, and prefab names alone
are not persistent authored identities.

## Authoritative record categories

### Unchanged scene-authored item

The item is implied by the host catalogue and requires no full persistent record. Runtime baselines
can send only stable identity, runtime binding, revision, and any required authority metadata.

### Modified scene-authored item

The host stores an override when an authored item is moved, held, owned, contained, internally
modified, or otherwise differs from its default catalogue state.

### Destroyed scene-authored item

The host stores a tombstone. A client's dormant scene copy remains quarantined and cannot resurrect
it when the area activates.

### Dynamically spawned item

The host stores a complete persistent record including prefab, persistent item ID, absolute transform,
placement, ownership, state payload, and lifecycle. Clients instantiate it only after an authoritative
Create/baseline entry.

### Detached/cold item

Cold-container and Lost and Found records remain authoritative without an ordinary world projection.
They participate in the same persistent identity/placement graph, but they do not consume a world
cell entry or routine NetId while detached.

## Placement anchors

Persistence and authority are separate concerns. An item on a train must be saved, but that does not
make vanilla storage its authority. Multiplayer owns the item record while referring to a parent
entity owned and persisted by another game system.

```text
ItemPlacement
  World
    absolutePosition
    absoluteRotation

  TrainInterior
    persistentCarGuid
    sessionCarNetId        // runtime/baseline only
    localPosition
    localRotation
    lastKnownAbsolutePose  // recovery/debug

  StaticParent
    stableParentKey
    localPosition
    localRotation
    lastKnownAbsolutePose

  PlayerInventory / PlayerHand
  Container
  LostAndFound
  InstalledGadget
  SnappedAttachment
  Retired
```

### Train-interior placement

DV already persists the train car. Multiplayer persists the item with the car's stable `CarGUID` and
an interior-local transform. The session car NetId can be included in live baselines but is never the
durable reference.

On load:

1. Restore train cars through the existing train save/load path.
2. Keep train-anchored item records logically present but unmaterialized until `TrainCarRegistry`
   resolves their `CarGUID`.
3. Materialize/bind the item and parent it through `ItemReparentingBase.ParentItemExternal` to the
   resolved interior.
4. If the car cannot be resolved after the train-load completion boundary, apply an explicit recovery
   policy: owned/retrievable items go to their owner's Lost and Found; unowned items fall back to the
   last known absolute pose or a designated recovery record. Never silently discard them.

A train-anchored item inherits relevance from its train/occupants and nearby observers. It does not
churn through the world-cell index as the train moves; its absolute position is derived from the
anchor when needed. When it leaves the train, the host commits one placement transition to `World`
using its current absolute pose and inserts it into the appropriate native item cell.

This category describes an active loose item physically parented to a train interior. It is not the
same as an installed gadget.

### Installed-gadget placement

The full vanilla lifecycle and identity/wiring model are documented in
[`research/DERAIL_VALLEY_GADGET_CUSTOMIZATION_LOGIC.md`](research/DERAIL_VALLEY_GADGET_CUSTOMIZATION_LOGIC.md).

Vanilla moves an installed gadget item into `StorageInstalledGadgets` and deactivates the item
GameObject. Its `ItemSaveData` retains the customization anchor (`placedOn`), local placement, and
gadget-specific state while a separate installed presentation is reconstructed on the target.

Multiplayer models this as:

```text
InstalledGadget
  persistentItemIdentity
  stableCustomizationAnchor
  localPosition
  localRotation
  gadgetStatePayload
  optionalSessionParentNetId
```

The inactive item record remains authoritative. The installed presentation is a projection of that
record and cannot become a second independently saved item. For a train gadget, the durable anchor
must ultimately resolve through the owned train/car identity, preferably `CarGUID` plus a stable
customization/snap target key. Missing anchors follow the gadget recovery policy—currently compatible
with Lost and Found—without duplicating the item and projection.

DV already supplies the serialization bridge:

- capture state with `GadgetItem.OnItemSaveDataRequestedInternal`;
- read `placedOn` and resolve it through `Customization.TryGetFromIdentificationKey`;
- reconstruct with `GadgetItem.OnItemSaveDataLoadedInternal` only after the anchor is ready.

The host accepts an install intent only after validating actor authority, ownership policy, item and
target revisions, range, gadget/target compatibility, and target capacity/rules. It then commits one
`InstalledGadget` placement revision. Clients receive authoritative placement data; they never send
an arbitrary save JObject that the host blindly applies.

If a client receives placement before the train/customization exists, it retains the operation in a
deferred-by-anchor queue and acknowledges only after successful reconstruction. Repeated delivery is
idempotent by persistent item identity, anchor identity, and placement revision—not merely by testing
whether `Gadget.Custom` is non-null.

Installation does not silently erase persistent player ownership. Ownership and placement are
orthogonal fields; any transfer-to-vehicle policy must be an explicit later decision.

Drilled customization holes belong to train/customization authority rather than item placement. They
may reuse `TrainCarCustomization.Serialize`/`Deserialize`, but require their own host-validated
revision stream and should not be bundled into every gadget item update.

Gadget wiring is likewise a separate revisioned graph. Vanilla stores edges by saved customizer UID
and restores them only after gadget nodes have been placed and loaded. Wiring, fuse, and train
simulation-port state must not be flattened into generic world-item transform updates.

`TrainCarCustomization.GetIdentificationKey()` confirms that a train gadget's durable customization
anchor is `TrainCar.CarGUID`. EOT lanterns are now confirmed to use `SnappableItem`, not
`GadgetBase`. Brake LED bars and wired lights are confirmed `GadgetBase` implementations rather
than separate placement families.

### Snapped-attachment placement

`SnappableItem` attachments remain ordinary active item representations. Snapping reparents the
item through `ItemReparentingBase`, aligns its item-specific anchor, changes physics/interaction
state, and occupies one `ItemSnapPointBase`. It does not retire the item NetId or create a second
projection.

The authoritative record is:

```text
SnappedAttachment
  persistentItemIdentity
  snapPointType
  anchorKind
  stableAnchorIdentity
  optionalLocalSnapData
  placementRevision
```

Known stable anchors are:

- coupler: `CarGUID` plus front/rear;
- gadget-hosted point: customization identification key plus saved gadget UID; and
- future snap families: an explicit stable snap-point key, never a transform instance ID.

`Belt` is not a `SnappedAttachment`. It is the physical projection of a player inventory slot and
its independent reservation state, and remains under inventory authority.

Coupler validation includes point occupancy, allowed type, actor/item authority, proximity, and the
chain's parked/coupled state. Coupling or moving the chain out of parked state commits a
host-originated forced-unsnap transition. An occupied `GadgetWithSnapPoint` blocks ordinary removal;
forced removal unsnaps the dependent item before removing the support. Those logical transitions
must commit in that order or in one host transaction.

Load ordering is dependency-based: trains and customizations, then installed gadgets and restored
UIDs, then snapped accessories, then wiring/after-load state. Missing anchors remain deferred rather
than allowing vanilla's load callback to log and abandon the authoritative snap.

Snapped train items inherit train relevance. Although their physical parent may be the train
interior, they retain `SnappedAttachment` placement because occupancy and forced-unsnap behavior are
semantically different from a loose `TrainInterior` item.

### Static-parent placement

A valid `ItemStaticParent` is likewise an anchor, not another save authority. An authored static
parent requires its own stable key; a dynamic static parent requires persistent identity. If the
anchor is missing during load, use the recorded absolute pose or the appropriate owner recovery path
and emit an invariant warning.

### Single-owner invariant

Parenting never changes the item save owner:

```text
MultiplayerManaged item on world root       -> multiplayer save
MultiplayerManaged item on train interior   -> multiplayer save
MultiplayerManaged item on static parent    -> multiplayer save
MultiplayerManaged item in cold/container   -> multiplayer save
```

The parent system may save the train, static customization, player, or container shell. It must not
also serialize a second anonymous copy of the child item.

### Vanilla `StorageWorld` boundary

DV's `StorageWorld` is not an authoritative catalogue of physical world objects. It rejects
non-player-owned items and is populated by inventory transitions for player-owned loose items.
Nonessential authored furniture is scene-resident and absent from this storage. Multiplayer world
placement must therefore be derived from the authoritative registry, never from
`StorageController.IsInStorageWorld` alone.

## Projection lifecycle

Per recipient, an item projection moves through explicit states:

```text
Unknown
  -> CreatePending
  -> DeferredForRegion
  -> BoundPendingApply
  -> Known
  -> RetirePending
  -> Unknown
```

### Entering interest

1. Host calculates newly subscribed native cells.
2. Host sends a cell baseline containing relevant authoritative records and the cell revision.
3. Client resolves each stable scene key or creates each dynamic representation.
4. If `WorldStreamingInit.IsSceneAndTerrainRegionLoaded` is false, the entry remains deferred.
5. Client binds the host NetId, applies the authoritative state, and gates interaction appropriately.
6. Client acknowledges successful apply or reports a specific failure.
7. Host marks only successfully acknowledged projections as known.

Sending a packet is not proof that the client has a representation.

### Leaving interest

1. Host removes cells no longer subscribed after the selected hysteresis/grace policy.
2. Host sends an explicit cell or entity retirement.
3. Client revokes interaction and networking authority immediately.
4. Scene-authored objects return to the dormant catalogue; they are not destroyed.
5. Dynamic projections are unregistered and retired/destroyed unless another interest source retains
   them.
6. Client acknowledges retirement.
7. Host removes the corresponding known projection state.

Re-entering always permits a fresh baseline. The current permanent `KnownItems` assumption must not
survive retirement.

### Moving between cells

The host commits transform and cell membership together:

```text
validate transition
commit absolute transform and authority revision
remove old cell edge
add new cell edge
publish update/enter/retire effects per recipient
```

A recipient subscribed to both cells receives an ordinary transform update. A recipient subscribed
only to the old cell receives retirement. A recipient subscribed only to the new cell receives a
baseline/Create.

## Client interaction protocol

Blocking all client traffic would also block valid gameplay. Client state declarations should be
replaced with host-validated intents:

```text
RequestPickup
RequestDrop
RequestThrow
RequestEquip
RequestInsertIntoContainer
RequestWithdrawFromContainer
RequestUse
RequestAttachOrSnap
```

Each request identifies the runtime item, expected authority revision, actor, and operation-specific
data. The host validates:

- the actor/session;
- projection knowledge and revision;
- distance and region readiness where applicable;
- current placement and holder;
- ownership and theft rules;
- container/Lost and Found state;
- whether another operation is in flight;
- operation-specific game rules.

The host commits and broadcasts the resulting authoritative transition. Clients may predict visual
motion, but rejection applies the latest authoritative correction.

For this feature, a validated `RequestDrop` or `RequestThrow` establishes the logical placement,
holder release, revision, and initial motion parameters. Subsequent high-frequency rigidbody motion
continues through the existing item-update path as an explicit compatibility boundary. That path may
update the host's accepted canonical transform, but it cannot create an item or change identity,
ownership, holder, storage membership, attachment, or lifecycle.

An unbound scene-authored client object can never send an adoption/create request. The remaining
`TemporaryClientAdoptionCompatibility` path is explicitly transitional: it accepts only a
non-authored, non-job item already marked as player property and associated with player/storage
state. It exists solely for Derail Valley producers such as unconverted shop outputs. Each producer
must become an explicit request -> host validation -> host creation operation; once the last is
converted, the adoption packets, coordinators, transition reason, and compatibility policy are
deleted. It is not a supported extension point or a second authority model.

## Cell baseline and delta protocol

Conceptually:

```text
WorldCellBaseline
  cellX, cellZ
  cellRevision
  catalogueVersion
  entries[]

WorldCellDelta
  cellX, cellZ
  fromRevision, toRevision
  changes[]

WorldCellRetire
  cellX, cellZ
  expectedRevision

WorldProjectionAck
  cellX, cellZ
  appliedRevision
  appliedEntityIds[]
  failures[]
```

Reliable ordered delivery is appropriate for baseline, lifecycle, and authoritative placement
changes. High-frequency physical prediction can use the existing item update path once authority and
revision semantics are preserved.

If a delta's `fromRevision` does not match the client's applied cell revision, the client requests a
fresh baseline. Failed/deferred entities are never silently marked applied.

## Deferred network-physics authority

Logical item authority and physical simulation authority are deliberately separate projects.

This design establishes host authority for identity, lifecycle, placement transitions, ownership,
interest, graph membership, revisions, and persistence. It does not attempt to solve remote terrain
physics, rigidbody ownership, collision validation, sleeping, or reconciliation for thrown objects.

During the transition:

- existing movement/physics replication remains in service;
- its packets are accepted only for an already known, materialized item in a compatible logical
  placement;
- movement reports cannot imply pickup, drop, inventory, ownership, snap, container, or lifecycle
  transitions;
- discontinuities and invalid placement changes remain observable; and
- the protocol boundary is isolated so a later physics-authority implementation can replace it
  without changing the catalogue, placement graph, interest lifecycle, or persistence format.

A future physics project may choose host simulation, delegated simulation leases, or another
reconciliation model. This world-sync implementation does not commit to one.

## Host persistence

### Save authority

The host is the only authority that saves multiplayer-managed items. Derail Valley continues to save
the world and non-item gameplay systems, while multiplayer owns item identity, placement, ownership,
graph relationships, and lifecycle.

DV's `ItemSaveData` remains the serializer for specialized per-item state. Multiplayer wraps that
payload with canonical metadata rather than reimplementing every item's save logic.

### Persisted data

```text
WorldItemSave
  formatVersion
  catalogueVersion/hash
  sceneItemOverrides[]
  sceneItemTombstones[]
  dynamicItemRecords[]
  detachedItemRecords[]
  placement/anchor/container graph
```

An untouched scene-authored item is not written individually. The default catalogue supplies it on
load. Only differences and tombstones are persisted.

Dynamic and detached items require complete records with persistent UUIDs. Routine session packets
continue to use compact NetIds or handles; persistent UUIDs appear only in persistence, baseline
identity establishment when required, and exceptional reconciliation/debug paths.

### Save transaction

```text
PREPARE
- revision-fence item mutations
- request ItemSaveData from materialized host objects
- combine materialized snapshots with dormant/cold records
- validate ownership and placement graph
- build and size-check save DTO

COMMIT
- write authoritative item save chunk
- release revision fence
```

The save must include inactive host scene items and records with no Unity representation. It cannot
depend solely on vanilla `StorageWorld.SaveStorage()` enumeration.

### Preventing double save/load

Every item must have exactly one save owner:

```text
VanillaManaged
MultiplayerManaged
```

Vanilla storage save/load must exclude `MultiplayerManaged` objects. Multiplayer load creates or
binds those records exactly once and prevents vanilla StartingItems/storage reconstruction from
duplicating them.

Initially, explicit exceptions may remain for categories not yet integrated, such as specialized
installed gadgets or physical license artifacts. The target architecture has one authority, not two
parallel compatibility versions.

### Load reconstruction

1. Build the host's authored scene catalogue.
2. Validate the saved catalogue version/hash.
3. Apply scene overrides and tombstones.
4. Restore dynamic and detached records.
5. Rebuild placement/container/Lost and Found graph indexes.
6. Materialize only records required by the host's current presentation and player-bound state.
7. Begin accepting client catalogue hashes and cell subscriptions.

## Cooperation with Derail Valley optimizers

DV activation remains a presentation decision. Multiplayer should not globally disable
`ItemDisabler`, `ItemDisablerGrid`, or office optimizers.

On clients:

- DV may activate an authored object because it entered the 3 by 3 grid;
- multiplayer keeps interaction/network emission gated until the host projection is applied;
- a host baseline may arrive before DV reports the scene/terrain region ready and must remain
  deferred;
- DV may deactivate an ancestor without retiring the host projection state;
- explicit host retirement revokes the projection even if DV leaves the object active.

On the host:

- inactive objects remain members of the authoritative catalogue;
- their canonical state does not disappear when DV hides them;
- registry changes, not `activeSelf`, drive persistence and replication.

## Replenishable authored office stock

Eligible scene-authored office props represent replenishable stock slots rather than eternal
one-off objects. When a player takes one from world/static placement into a hand or inventory:

1. The taken object permanently leaves the authored catalogue and receives a persistent dynamic
   item identity.
2. The authored slot retains its stable key, prefab, baseline transform, and interest cell.
3. The host waits until no ready player is interested in the slot's 3 by 3 cell neighbourhood,
   followed by a short empty-area dwell.
4. The host instantiates a fresh replacement at the baseline transform and publishes it under the
   original authored key.
5. A client may instantiate that replacement only when it previously detached the same local
   authored key; every other missing-key projection remains a catalogue integrity failure.

Dropping the taken dynamic item back near the original desk does not re-adopt it. This keeps the
fork rule deterministic and prevents its persistent state from becoming authored state again.

Initial exclusions are conservative: player-owned or essential items, job documents, shop items,
installed gadgets, snapped items, and item containers are never replenished through this path.

Authored office props that remain authored but are physically displaced use a different cleanup
path. The host compares their canonical and live transforms with the authored baseline. Once the
slot's interest neighbourhood has been empty continuously for 15 seconds, the existing object is
returned to its baseline pose, its velocities are cleared, and one authoritative spatial commit is
published. No new identity or duplicate object is created. This covers furniture disturbed by
collisions or other non-possession interactions; any object that entered a hand or inventory already
followed the persistent-fork and replenishment path above.

The taken fork is durable only while it remains relevant gameplay state. Its host record carries a
`replenishableStockFork` classification through save/reload. While held it behaves like an ordinary
dynamic item. After it is dropped into world, static-parent, or train-interior placement and remains
outside every player's interest continuously for 30 seconds, the host destroys it and removes its
persistent record. This prevents abandoned replenished bins and furniture from accumulating forever
without allowing clients to decide what disappears.

Lost and Found has strict precedence over disposable-fork cleanup. Any Lost and Found registry
membership, persistent player owner, or inventory retrieval claim makes the fork ineligible for
abandonment deletion. Lost and Found itself already rejects unowned/nonpersonal stock, so an
ordinary office bin normally never enters that subsystem; the explicit precedence guard protects
future ownership transitions and recovery paths from turning cleanup into an item-loss race.

## Job items and station state

Job overviews, booklets, and reports already have specialized construction/binding requirements.
They should use the same interest and acknowledgement lifecycle but retain logical job recipes where
the physical object cannot be reconstructed from a prefab alone.

Station/job logical state should be baselined before or alongside dependent physical artifacts. A
client that has terrain ready but not station/job dependencies keeps those projections deferred and
reports the dependency, rather than creating a generic duplicate.

## Failure handling

- Stable scene key not found: report mismatch, keep entry unresolved, request catalogue/baseline
  reconciliation.
- Stable key collision: fail catalogue construction for those entries; never choose arbitrarily.
- Region not loaded: defer without marking known.
- Apply failure: quarantine the projection and return the failure reason.
- Missing delta revision: request a fresh cell baseline.
- Client state declaration for host-managed item: reject and send authoritative correction.
- Stale interaction intent: reject using expected/current revision diagnostics.
- Retirement acknowledgement timeout: retain host-side pending state and retry; do not assume cleanup.
- Host save serialization failure: keep the previous durable save and surface a blocking invariant.

## Performance requirements

- One inactive-inclusive catalogue scan per world load, never a periodic hierarchy scan.
- Cell membership indexed as dictionaries/sets; do not scan every item for every player each tick.
- Player world interest recalculated on native cell transitions, teleport, load-state change, or
  explicit reconciliation.
- Item cell membership updated only when an authoritative world item crosses a boundary.
- Baselines are bounded and packet-chunked.
- Debug detail gathering remains demand-driven; normal authority code records compact facts.
- No new fixed-period full-world reconciliation loop.

These constraints are important because previous periodic item/debug scans caused visible stutter on
both host and client.

## Observability

Required events include:

```text
world.catalogue-built
world.catalogue-mismatch
world.catalogue-key-collision
world.cell-entered
world.cell-baseline-sent
world.cell-baseline-received
world.projection-deferred
world.projection-bound
world.projection-apply-failed
world.projection-acknowledged
world.cell-retire-sent
world.projection-retired
world.cell-revision-mismatch
world.item-cell-migrated
world.client-authority-rejected
world.save-snapshot-created
world.save-invariant-violation
```

The dashboard should expose per-player subscribed cells, baseline revision, pending/deferred
projections, stable scene key, runtime NetId, authority revision, readiness reason, and last failure.

## Implementation phases

### Phase 1: catalogue and diagnostics

Implemented.

- Implement stable scene identity and collision detection.
- Build host/client inactive-inclusive catalogues.
- Compare count/hash without changing gameplay behavior.
- Add dashboard catalogue and native-cell diagnostics.
- Add runtime scenarios proving host/client identity agreement across distant offices.

### Phase 2: native cell interest lifecycle

Implemented with per-item baselines batched through the existing item bulk packet.

- Replace per-player all-item distance scanning with 128 metre cell indexes.
- Add explicit enter, baseline, acknowledgement, retire, and re-enter behavior.
- Keep current item mutation protocol temporarily, but remove permanent known-state assumptions.
- Verify teleport and region-unavailable deferral.

### Phase 3: scene projection binding

Implemented.

- Bind scene-authored items by stable key rather than prefab order.
- Make exact unbound authored objects dormant and destroy unauthorized unbound dynamic objects.
- Instantiate fresh dynamic projections; never reuse an old logical lifetime by prefab name.
- Add dynamic-item creation alongside authored binding.

### Phase 4: host intent authority

Implemented by treating client snapshots as revision-fenced requests. They are validated and then
rewritten from the canonical record before application/relay; client Create and Destroy are rejected.

- Convert pickup/drop/throw/equip/container/snap/use paths from client state declaration to validated
  logical intent.
- Add logical prediction correction and revision handling without replacing high-frequency physics
  replication.
- Reject all client attempts to create or authoritatively mutate catalogue items.

### Phase 5: authoritative item persistence

Implemented for free world, train-interior, static-parent, player-bound authored overrides, dynamic
world records, and authored tombstones. Installed gadgets remain owned by their specialized gadget
save/sync subsystem, while cold containers and Lost and Found retain their detached save authorities.

- Add default catalogue plus overrides/tombstones/dynamic records.
- Reuse `ItemSaveData` payloads.
- Exclude multiplayer-managed items from vanilla storage save/load.
- Validate host restart, client reconnect, cold containers, Lost and Found, jobs, and ownership.

Network physics is implemented as the separate spatial lease/settlement layer described in
`MULTIPLAYER_ITEM_SPATIAL_PHYSICS_SYNC_DESIGN.md`; it does not alter catalogue identity or cell
authority.

## Required runtime scenario suite

One scenario per file, discovered through the existing scenario registry:

1. Host and client authored catalogue counts/hashes match.
2. Identical prefabs in one office bind to the correct stable scene keys.
3. Client enters a cell while the host remains elsewhere and receives the authoritative baseline.
4. Client leaves and re-enters an unchanged cell without duplicates or missing items.
5. Host changes an item while the client is absent; re-entry receives the latest revision.
6. Authored item crosses a cell boundary with recipients subscribed to old, new, both, and neither.
7. Client receives a baseline before region readiness, defers it, then applies it after readiness.
8. Client catalogue mismatch produces a visible failure and no prefab-name misbinding.
9. Lost or duplicated baseline acknowledgement causes retry/resync without duplicate Unity objects.
10. Client attempts to authoritatively create a scene item and is rejected.
11. Client attempts pickup with a stale revision and receives correction.
12. Two clients in different offices receive disjoint bounded projection sets.
13. A player-held item remains projected across world-cell boundaries.
14. A permanently destroyed authored item remains absent after leave/re-enter.
15. A modified authored item survives host save/restart and binds to the correct projection.
16. A dynamic item survives host save/restart with persistent identity and a fresh session NetId.
17. A cold-container shell crosses cells without hydrating its contents.
18. A Lost and Found item is removed from world-cell projection and remains owner-retrievable.
19. Job state arrives before its physical artifact and binds without a duplicate.
20. Repeated teleport across boundaries produces no leaked known entries, projections, or fixtures.
21. An item placed in a train interior follows the moving car without world-cell membership churn.
22. A train-interior item survives host save/restart, resolves the same `CarGUID`, and restores its
    local pose exactly once.
23. An item thrown from a train commits one `TrainInterior -> World` transition and enters the correct
    native item cell.
24. A saved item whose train no longer exists follows the configured recovery path and is never
    duplicated or discarded.
25. Host and client throws inside a moving cab remain train-relative, converge without host/client
    correction jitter, and settle inside the same train interior.
26. A settled train-interior item follows a subsequently moving train on every observer without
    receiving world velocity a second time.
27. Taking an eligible authored office prop forks it into a persistent dynamic item, but does not
    replenish while any player remains interested in the source area.
28. Once every player leaves the source area, the authored office slot replenishes exactly once and
    all clients bind the replacement to the original authored key.
29. Essential, job, shop, installed, snapped, and container items never enter authored stock
    replenishment.

Every scenario must assert cleanup isolation on host and client.

## Open questions

1. Do `TerrainGrid` and the 128 metre item grid share boundaries or only overlapping readiness?
2. Does DV already serialize a stable authored object identifier that is preferable to a hierarchy
   hash?
3. Which vanilla storage/save categories can be transferred first without breaking gadgets,
   customization, licenses, or tutorials?
4. Which interactions require client prediction to remain responsive at real network latency?
5. Should a cell baseline carry per-entity acknowledgement only on failure, or always carry a compact
   applied bitmap/hash?
6. For an unowned item whose saved train no longer exists, should recovery use its last absolute pose,
   a station recovery location, or another durable holding state?
7. Which train relevance signal should item projections inherit while the current train protocol still
   creates every trainset on every client?
8. What stable identity is available for authored and dynamic `ItemStaticParent` anchors?
