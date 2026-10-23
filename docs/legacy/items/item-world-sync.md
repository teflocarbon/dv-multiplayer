> Legacy: describes the item system before the refactor (names as on `beta` 04bfe277). Read it only as evidence of how the code behaves today, not as design. Replaced by `items/projection.md` and `items/world-items.md` (R7). The item docs start at [items/README.md](../../design/items/README.md).

# Item world sync

> Status: as built, checked against the code on 2026-10-23 (`items/lineage`). Built 2026-07 to
> 2026-10 (#12 onwards). Covers scene-authored items, interest, projection to clients, convergence,
> office stock and the placements anchored to other things. Motion is in
> [`item-spatial-physics.md`](item-spatial-physics.md), saving in
> [`item-persistence.md`](item-persistence.md).

> The server's item registry is canonical. A Unity object on the server or a client is a disposable
> presentation of its record, and never becomes authoritative because it is active, interactive,
> simulated locally, or in a vanilla storage list.

The server owns identity, NetIds, the authored keys, pose and cell, placement and holder, owner and
claim, containment and Lost and Found, each item's saved state, lifecycle and revision. A client owns
only its presentation, its input, and acknowledging that a projection was applied (or wasn't).

## Pieces

Paths under `Multiplayer/`.

| Where | What |
| --- | --- |
| `Shared/Items/NetworkedItemManager*.cs` | What every game runs: local observations, tracked-value finalisation, the prefab lookup, the authored catalogue, the spatial store, the terrain guard |
| `Server/Items/ServerItemManager*.cs` | The server's part: the cell index (`.WorldIndex`), interest and delivery (`.Interest`), convergence, fall rescue, players' items |
| `Client/Items/ClientItemManager*.cs` | A remote client's part: snapshot receipt, deferred projections, job documents, Lost and Found copies, authored binding, unbound items, the fall-rescue report |
| `Shared/Items/NetworkedItem*.cs` | One item. On the server, `NetworkedItemServer` (validation, commits) and `NetworkedItemServerPart`; on a remote client, `NetworkedItemClient` (binding gate, release grace, object-state gate, revisions it claims, convergence and own-drop checks) |
| `Server/Items/AuthoredWorldItemReplenishmentManager.cs` | Office stock |

## Scene-authored items and the catalogue

**The key.** A `NetworkedItem` captures its `AuthoredItemKey` in `Awake` (from `ItemBase_Patch`, so
before the scene load returns) when its scene starts with `game_content_` or its root is
`[origin shift content]` (`WorldItemStableIdentity`). DV then moves the content into `game_w3` and
flattens the hierarchy (`WorldStreamingInit.LoadingRoutine`; loose items are reparented again with
sibling index 999), so a scan after load can't rebuild the identity. The key is the first 128 bits of
SHA-256 over the normalised scene, the hierarchy path with sibling indices, the prefab name, and the
absolute position and rotation to the millimetre. Only the hash is kept. An object made after
staging has no key: it is a dynamic item.

**The catalogue** (`AuthoredWorldItemCatalogue.Rebuild`) scans `Resources.FindObjectsOfTypeAll<ItemBase>`
in `game*` scenes, inactive included, no clones, with inventory specs. A duplicate key is recorded as
a collision and the first object found keeps it. The baseline count, collision count and digest (16
bytes of SHA-256 over the sorted keys) are frozen when it is built, once, lazily, in every game.

**Joining.** A client makes every unbound item quarantined once its world has loaded: an authored one
dormant (no NetId, no outgoing state, no interaction, kept by key), any other destroyed. It sends its
catalogue (`ServerboundWorldItemCataloguePacket`). The server accepts it only with no collisions on
either side, the same count and the same digest, sets `WorldItemCatalogueAccepted` and answers
(`ClientboundWorldItemCataloguePacket`). A rejected player gets no scene-authored items for the
session; dynamic items still flow. Both sides compare the catalogue as built, not the live key map:
the live map loses a key while a taken office prop waits to be replenished, and comparing it once
rejected a player whose scanners then vanished for the session (2026-10-19).

**Static parents** (shelves) are keyed the same way, from scene, path and position, and resolved by
`WorldItemStaticParentRegistry`.

An authored item isn't saved as a vanilla world record: vanilla's `StartingItemsController` makes
every saved record a fresh prefab instance and could never bind it back, so the next load would have
the authored object and a copy. The mod saves the key and its override or tombstone instead
([item-persistence.md](item-persistence.md)).

## Dynamic items

An item that isn't scene-authored (bought, issued, restored, withdrawn, a taken office prop) has a
persistent GUID from the server ([owned-items.md](owned-items.md#one-id-for-life)) and a NetId while
it exists. Packets use the NetId. A client's copy is a fresh prefab instance for one Create's
lifetime, never reused as another item, and destroyed when the server retires it.

## Cells and interest

**Cells.** 128 m squares on absolute X and Z (`WorldItemCellCoord`). The server indexes each item
(`RefreshHostWorldItem`):

- nowhere: waiting for a restore, in Lost and Found, an installed gadget's item, destroyed;
- non-spatial (`HostNonSpatialItems`): hand, inventory, container, installed, train interior,
  attached, snapped;
- both, for a world item with an owner's recall claim;
- otherwise its cell.

It is re-indexed on every committed placement change (at the record's pose, in the same operation,
before the caller applies the Unity state), every accepted change to the server's own copy, every
`RegisterHostWorldItem`, and the fall rescue. Accepted spatial samples take a cheaper path
(`RefreshHostSpatialPosition`).

**Interest** (`ServerItemManager.Interest`). Each network tick (24 Hz) a `ReadyForItems` player's set
is rebuilt if their 3×3 cells changed, the index changed, or 0.25 s passed. The set is the items in
the 3×3 cells plus the non-spatial items relevant to them; an item already near stays until it is
outside 5×5. Membership is only rewritten when it changed (fe11afa). Non-spatial relevance
(`IsNonSpatialRelevantTo`):

- installed items: everyone;
- a hand or inventory item: its holder, its owner while the owner has a recall claim on it, or anyone
  whose cells hold the holder (`ItemPossessionInterestPolicy`);
- an item with an owner's recall claim: that owner, anywhere;
- a train-interior item: the car's occupants, or anyone whose cells hold its current position;
- attached and snapped items: the car's occupants, or by the car's position.

A leaving item is dropped from the player's nearby, known and acknowledged sets, and a `Destroy` with
`InterestRetirement` goes out. Lost and Found items are sent once as a collection projection to
players who don't know them.

## Delivery

`ProcessChanged`, every network tick:

1. Every change to the server's own copies (`GetSnapshot`) is committed as the server's own
   transition (its own player's, or actor 0 on a dedicated host). For an item another player holds,
   only server-authoritative object state is kept (`TryKeepRemotePossession`).
2. Per player, per nearby item (`KnownItemDeliveryEvaluator`): unknown gives a `Create` (marked known
   at once); dirty gives the dirty snapshot; known before its last change gives a `FullSync`. A Create
   is built from the record and the last accepted spatial pose; a job report's render recipe goes
   first. A Create waits while a negative acknowledgement holds it back.
3. Destroys go only to players who knew the item.
4. It all goes as one `CommonItemsBulkUpdatePacket` per player (reliable ordered, uncompressed), then
   any open leases.

A client's accepted update is relayed with `CommonItemUpdatePacket` to every `ReadyForItems` player,
interest ignored.

**Outbound from a game** (`NetworkedItem.GetSnapshot`). Dirty means re-evaluate. An `ItemState` goes
out only when its wire projection changes (`ItemWireStateComparer`; `Thrown` is one-shot and
baselines as `Dropped`). Object state goes only if the client part allows it. A client reserves the
revision it expects through `ItemOutboundRevisionPipeline`, so quick transitions pipeline; a server
correction resets it. Clients never send position-only updates: motion is the spatial stream.

**Server checks** (`NetworkedItemServer`). A non-possessor's update needs the item acknowledged
(`item-not-projected-to-sender`); a release must be within 64 m, its parent within 192 m; server-only
tracked values from a client are refused (`TrackedStateComposer`). Every placement change goes
through the state machine ([item-lifecycle.md](item-lifecycle.md#the-state-machine)). There is no
pickup range check.

## Projection on a client

`ClientItemManager.ProcessReceivedAsClient`:

- A Create for a dropped, thrown or attached item whose region isn't ready waits: ready means the car
  exists (train-interior, attached), else `WorldStreamingInit.IsSceneAndTerrainRegionLoaded`. Later
  updates for it queue (the last 64); a Destroy cancels it. Up to 8 ready ones are projected a frame.
- A Create with an authored key binds the dormant scene object; any other is a fresh prefab instance.
  Both acknowledge (`ServerboundWorldItemProjectionAckPacket`: NetId, revision, `Projected`).
- A Destroy retires the projection: an authored object goes dormant and keeps its key, a dynamic one
  is destroyed. An `InterestRetirement` is acknowledged `Projected=false`.
- Job documents bind to the job system's objects, or wait for them.

**Acknowledgements.** A positive one adds the item to the player's acknowledged set, if the player
knows it at that revision or older. A negative one removes it, and if the item was known, forgets it
and holds its Create back 5 s. The acknowledged set gates a non-possessor's updates and who may
simulate the item. The releasing player is added when the server accepts their release.

**Receiving a snapshot** (`NetworkedItem.ReceiveSnapshot`). An older revision than the copy's is
refused. A client ignores a convergence snapshot unless it disagrees (a newer revision, not lying in
the world, or more than 0.25 m off, car-relative in a train). Local echoes are filtered
(`ItemSnapshotApplicationPolicy`: its own throw and VR release, a superseded release or hold, its own
object state, its own desktop drop). Snapshots with tracked state wait until tracked values are
final. Placement is applied by state: dropped or thrown (world pose, parent, a dynamic body with no
velocity, a throw replayed by direction), hand or inventory (another player's through
`RemoteItemProjectionPlanner`), attached, installed; then tracked values (`TrackedStateComposer`).

**Convergence** (`ServerItemManager.Convergence`). Every 0.1 s, for each loaded remote player, the
next item they know in turn that is in `World`, `TrainInterior` or `StaticParent`, with no open
lease, not in Lost and Found and not waiting for a restore, is sent as a `Dropped` snapshot with the
server's live pose (`Convergence`). Drifted copies heal.

**DV's optimizers stay on.** `ItemDisabler`, `ItemDisablerGrid` and the office optimizers decide what
is active, on every game. On a client, DV activating an authored object doesn't make it interactive
before the server's projection; DV deactivating it doesn't retire the projection. On the server,
inactive objects stay in the catalogue and their state stays: the registry, not `activeSelf`, drives
saving and replication.

## Placements anchored to something else

The parent's own system saves the parent (the train, the shelf, the player); the item's record and
save stay the mod's. Parenting never changes who saves the item.

- **Train interior.** The record keeps the car (`WorldParentNetId`, its GUID) and an interior-local
  pose; it doesn't move through cells with the train. Leaving the train is a placement change to
  `World` at its absolute pose. Restored at its saved pose straight away
  ([item-persistence.md](item-persistence.md)).
- **Static parent.** A shelf (`ItemStaticParent`) by its stable key and a local pose.
- **Attached.** An EOT lantern on a car's end (`AttachedCarNetId`, front or rear).
- **Snapped** (`SnappedAttachment`). A `SnappableItem` on a snap point (a hanger, a coupler): still an
  ordinary item with its NetId, reparented through `ItemReparentingBase`. A snapped train item keeps
  `SnappedAttachment`, not `TrainInterior`. Anchors are a car's GUID and end for a coupler, or a gadget
  key and UID, never a transform's instance id.
- **Installed.** A gadget on a car ([gadgets.md](../../design/gadgets.md#the-installed-gadget-and-its-item)): only a
  gadget transition may enter or leave it.
- **Vanilla's `StorageWorld`** isn't a catalogue of world objects: it rejects non-player items, and
  authored furniture is never in it. Placement comes from the registry, never from
  `StorageController.IsInStorageWorld`.

### VR belt items

In VR, inventory slots 33–35 are the belt (`Inventory.IsValidVRBeltIndex`). An item there stays in the
`Inventory`, but DV keeps it active and `ItemBeltVR` snaps it to the player's body. It syncs as an
ordinary `InInventory` of its holder, and `BeltItemPolicy` covers where the snap got in the way:

- **Release.** Letting go next to the belt adds the item to the slot only at the end of that frame
  (`ItemSnapPointInteractionBelt.OnUngrabDelayed`), so `LateUpdate` saw a dropped item first. For the
  frame of a VR release of a belt-snappable item, the state is held (`HoldVrRelease`); the next frame
  reports `InInventory` or `Dropped`.
- **Acknowledgement.** The server's echo of the holder's own `InInventory` unsnapped the item, which
  fell while staying in the slot. A snapshot putting an item on the local player's belt into their
  inventory leaves the snap alone (`KeepBeltSnap`).
- **The hold's echo.** Taking an item off the belt and letting go sent `InHand` again, and its echo
  unsnapped the item, which DV then wouldn't let anyone grab. The release frame sends nothing, and the
  holder's own `InHand` echo after the item is in their inventory is ignored (`IsSupersededOwnHold`).
  Server corrections carry no originating player, so they're never taken for an echo.

Whether the belt takes an item is DV's decision alone. Other games show it as an inventory item of its
holder (`RemoteItemProjectionAction.RemoteInventory`), hidden with the avatar's inventory. A save keeps
it as a possession, but not the belt slot: it comes back in the first free slot.

## Replenishable office stock

Eligible authored office props (`NetworkedItemManager.IsEligibleAuthoredOfficeProp`: not personal,
essential, snapped, a container, a gadget, a job document or a shop slot) are stock slots, not one
object forever (`AuthoredWorldItemReplenishmentManager`, every 0.5 s):

1. **Taken.** Moved from `World` or `StaticParent` into a hand or inventory, the object leaves its
   authored key and becomes a dynamic item marked as a stock fork, with a GUID of its own. Clients
   detach their copy as they apply the possession. Dropping it back by the desk doesn't make it
   authored again.
2. **Replenished.** Once no `ReadyForItems` player's 3×3 holds the slot's cell for 3 s, a fresh
   instance takes the key at the baseline pose. Only a client that detached that key may make one;
   others bind their dormant object.
3. **Displaced but not taken.** A prop moved more than 0.25 m or 5 degrees (by its record or live) is
   put back at its baseline, with one spatial commit, after 15 s with nobody's 3×3 on its baseline
   cell.
4. **Abandoned.** An unowned, unclaimed fork in the world, on a shelf or in a train, with nobody's
   3×3 on its cell for 30 s, is destroyed (`AbandonedStockForkCleared`).

Office props aren't personal, so a fork is never saved and doesn't survive a restart. Lost and Found
membership, an owner or a claim makes a fork ineligible for cleanup
([item-lifecycle.md](item-lifecycle.md#cleanup-precedence)).

## Job documents

Overviews, booklets and reports go through the same interest and acknowledgement, but bind to the job
system's objects (a report's render recipe goes before its Create), and are never saved as items: the
job system saves the job and recreates them ([jobs.md](../../jobs.md)).

## Not built

- Recovery when a train-interior item's car isn't there at load (`LostItemReason.SaveRecovery` is
  unused).
- Compressed or chunked bulk updates (compression is commented out).
- A sync-completion barrier at join (a fixed 0.25 s wait).
- Relays that respect interest.
- A pickup reach check.

## Known issues

From the code, not run in game (2026-10-23). The bug review tracks them.

- **A static-parent item drops out of every player's interest after a spatial sample or a commit.**
  `RefreshHostSpatialPosition` makes any non-world parent non-spatial, but `IsNonSpatialRelevantTo`
  has no `StaticParent` case, so clients retire it until something re-indexes it.
- Every change to one of the server's own copies (a lantern flickering) re-indexes the item and clears
  every player's cached cells, so interest is rebuilt for everyone next tick.
- A projection that fails on the immediate (not deferred) path sends no negative acknowledgement, so
  the server never re-sends the Create.
- A late retirement acknowledgement can cancel a re-entry and hold the Create back 5 s.
- A key collision on the server rejects every client's catalogue.
- `WorldItemStaticParentRegistry` rescans the scene on every miss, and its keys include the parent's
  absolute position, so a moved parent never resolves.
- An office prop's reset checks interest at its baseline cell only, so one knocked into a watched cell
  can snap back in view.

## Tests

`RemoteItemProjectionPlannerTests`, `ItemSnapshotApplicationPolicyTests`, `ItemWireStateProjectionTests`,
`ItemOutboundRevisionPipelineTests`, `TrackedStateComposerTests`, `ItemPossessionInterestPolicyTests`,
`BeltItemPolicyTests`, `AuthoredHierarchyPolicyTests`; scenarios in [testing.md](../../testing.md). Not
covered in game: a catalogue mismatch, cell enter/leave/re-enter, two clients, office replenishment.
