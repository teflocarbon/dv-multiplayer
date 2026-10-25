# Live state

> Status: built in R3 of the [item refactor](../item-refactor.md), 2026-10-24. This page says what the
> code does. Live state is every item right now, owned or not: its record on the server, the rules a
> change goes through, and the item component on each game. Owners and homes are the
> [ledger](ledger.md)'s, slots and cold inventories are [inventory.md](inventory.md)'s, access is
> [access.md](access.md)'s and the packets are [wire.md](wire.md)'s.

| Side | Code |
| --- | --- |
| Core | `Multiplayer.Core/Items/LiveState/`: `ItemStateMachine` (with `LiveItemState`, `ItemTransition`, `RecallCommand`), `ItemOutboundRevisionPipeline`, `ItemSnapshotApplicationPolicy`, `ItemStateObservationPolicy`, `ItemRequests` |
| Server | `Multiplayer/Server/Items/LiveState/`: `LiveItems` and its `LiveItem`, `NetworkedItemServer`, `NetworkedItemServerPart`, `HostItemActor`, `PlayerNumbers` |
| Shared | `Multiplayer/Shared/Items/LiveState/`: `NetworkedItem` and its partials, `NetworkedItemManager`, `ItemRequestTranslation` |
| Client | `Multiplayer/Client/Items/LiveState/NetworkedItemClient.cs` |

## What live state owns, and what the ledger owns

**On a conflict, the ledger wins on owner and home, and live state on pose and physics.**

| Live state (`LiveItem`) | The ledger (`ItemLedger`, through `ServerLedger`) |
| --- | --- |
| the NetId, the revision | the item's id (its GUID) for life |
| the placement, the holder this second, the hand, the slot | the home, exact (`Slot(holder, slot)`, `Hand(holder, hand)`, ...) |
| the pose, the world parent, the car an item is attached to | the owner (`OwnerRef`) and an essential's claim slot |
| the claim flags a client shows (dropped, stolen, locked) | births and ends |
| physics leases and settlement, projection to each client | the one-home check ([ledger.md](ledger.md)) |

For an owned item the record takes the new placement first, then the ledger commits the same home;
nothing is written, logged or sent before that, and a refusal puts the record back
([A change](#a-change)). An unowned item has no ledger entry: its placement is its home, and the ledger's slot rules
still cover it (`ServerLedger.UnownedInSlot`).

## The live item

`LiveItems` (`Multiplayer/Server/Items/LiveState/LiveItems.cs`) keeps one `LiveItem` per NetId.

| Field | What |
| --- | --- |
| `NetId` | the item's network id this session |
| `Revision` | counts every accepted change; see [Revisions](#revisions) |
| `Placement` | `ItemPlacementKind`: `World`, `PlayerHand`, `PlayerInventory`, `TrainInterior`, `StaticParent`, `Attached`, `SnappedAttachment`, `Installed`, `LostAndFound`, `Container`, `Destroyed`, `Magazine` |
| `Holder` | the identity of the player whose hand or inventory it's in, or empty. They may be offline |
| `PlacementHand` | which hand, for `PlayerHand` |
| `Slot` | its slot of the holder's inventory, for every inventory item, owned or not (OI-SLOT-01); else -1 |
| `Owner` | read from the ledger, never stored: `ServerLedger.OwnerOf(PersistentItemId, StoredOwner)` (OI-LED-05) |
| `InventoryClaimSlot` | read from the ledger: `ServerLedger.ClaimOf(...)` |
| `InventoryClaimFlags` | how the claim shows; none without a claim, and always `Reserved` with one |
| `StoredOwner`, `StoredClaimSlot` | what the item's own store named, read only until the ledger's saved entries are in (`ServerLedger.Restored`) |
| `PersistentItemId`, `AuthoredItemKey` | the item's id, or a scene-authored item's key |
| `Position`, `Rotation`, `WorldParent*`, `ParentLocal*`, `AttachedCar*` | the pose and what it hangs from |
| `MagazineNetId`, `MagazineToolId`, `MagazineSlot` | for `Magazine`: the tool's NetId and id, and the slot ([containers.md](containers.md#ammo-in-a-magazine)) |
| `LastReason`, `LastMove` | the last transition's reason and its cause ([change-causes.md](change-causes.md)) |

`ServerLedger.HomeOf(record)` turns a record into its exact home: a hand by holder and hand, a slot
by holder and slot, a train interior by the car's GUID, a shelf by its key, an installed gadget by
its car's GUID or the world (`Home.WorldAnchor`, when it has no car). Nothing in a record is a session
number: `PlayerNumbers` works out a player's number for the wire, and a player's relation to the
item for the server's own copy (`PlayerNumbers.LocalRelation`).

`LiveItems.Ensure` makes a record the first time an item is seen. It never makes an owner: an item
becomes owned only by a ledger commit (a first take, a purchase, an issue, a grant).

## A change

`LiveItems.TryApplyTransition(item, snapshot, actor, reason, force, ...)` is the one way a player's
change to an item is committed. Its order:

1. **Access for a take.** Into a player's hand or inventory from the world, or from another player,
   asks `ItemAccessServer.Allows` (`TakeFromWorld`, `TakeFromPlayer`) before anything else. So a take
   from another player is refused `take-from-player` by `ItemAccess`, not by the state machine
   (OI-ACC-07).
2. **The state machine.** `ItemStateMachine.Apply` decides on the record's state, as a pure
   function ([below](#the-state-machine)).
3. **Access for a stash.** Into an inventory from anywhere else asks `Allows(Stash)` (a station
   fixture is refused), for whoever commits it, the holder if there's no actor.
4. **The record takes the new state** (placement, holder, hand, slot, pose). Nothing is sent yet.
5. **The ledger commits.** `ServerLedger.CommitTransition(record, from, to, newOwner, cause)`: an
   owned item moves in the ledger from its old home; an item a player takes first is born theirs
   (`Claimed`); an unowned one is held to the slot rules. An inventory item with no slot is refused
   `inventory-full` here. A refusal (`stale-home`, `slot-invalid`, `slot-occupied`, `slot-reserved`)
   puts the record back as it was (`LiveItem.RestoreFrom`) and leaves the ledger as it was
   (OI-LED-04).
6. **Then apply and publish.** The update is written from the record (`WriteToSnapshot`), the move
   is logged (`ItemChangeLog.RecordMove`), and the caller applies it to the server's object and
   sends it.

A player's request reaches it through `NetworkedItemServer.Server_ReceiveItemUpdate`
(`Multiplayer/Server/Items/LiveState/NetworkedItemServer.cs`), which first checks the sender may
make it at all (possession, the item shown to them, a sane pose, a book's page), then commits, then
sends the accepted update to everyone, the sender included as its acknowledgement. A refusal at any
step sends the sender a correction (`SendCanonicalRejectionCorrection`): the record's state, with the
cause `HostCorrection`, which puts their game back. One case gets no correction: object state from a
player who doesn't hold the item is ignored (`non-holder-object-state-ignored`), since it isn't a
dispute about where the item is.

The other ways in, each also ledger-first:

| Method | For |
| --- | --- |
| `TryApplyServerTransition` | the server itself as an empty actor, which moves items about the world but never holds one or becomes an owner: the server's own world on a host with no player (`HostItemActor.IsServer`, from `ProcessChanged`), and on any host a deleted car's items (`DeletedCarItems.IntoWorld`) and a destroyed tool's ammo (`MagazineServer`) dropped into the world |
| `TryApplyGadgetTransition` | the gadget code: the only way into or out of `Installed` |
| `TryRecall` | an owner's recall ([below](#recall-in-one-step)) |
| `TrySwapSlots` | two items of one inventory swapping slots ([inventory.md](inventory.md#moving-between-slots)) |
| `TryMoveToLostAndFound`, `TryRetrieveFromLostAndFound`, `RestoreLostRecord` | Lost and Found's collection and retrieval |
| `RegisterDataOnly` | a row put straight into an inventory with no object: `PlayerItems.Issue` (a withdrawal into a slot, the starting items, an admin's give) and a joiner's rows (`PlayerItems.SendRows`) |
| `RegisterMaterializedColdItem` | an object made from a row into an inventory or at its holder's feet: a cold row that can't be a row at join (`GoLive`: no free slot, a ledger home of `LostAndFound`, an item that can't be a stand-in), a starting item made with the inventory full |
| `TryCommitSpatialPose` | a physics settlement; the ledger takes where it landed (`ServerLedger.OnSettled`) |

A move made without a ledger commit first is caught afterwards (`ServerLedger.OnMove`,
[ledger.md](ledger.md#the-ledger-decides-live-state-follows)).

`KeepRemotePossession` is a backstop: a record that lands in the host's own player's hands while the
server's copy is another present player's goes back to that player, with a warning.

## The state machine

`ItemStateMachine` (`Multiplayer.Core/Items/LiveState/ItemStateMachine.cs`) is pure Core. A refusal
never changes the state it's given; an accepted change returns a new state one revision higher.
`Apply` refuses, in order:

| Refusal | When |
| --- | --- |
| `server-cannot-possess` | the server itself (an empty actor) puts an item in a hand or inventory |
| `item-in-lost-and-found` | the item is in Lost and Found, unless forced |
| `item-installed`, `installed-only-by-gadgets` | a change into or out of `Installed` that isn't a gadget transition |
| `stale-authority-revision` | the revision the request expects is behind, unless forced |
| `authority-revision-exhausted` | the revision is at `uint.MaxValue` |
| `sender-not-current-possessor` | another player holds it, unless forced |

Then:

- A player who takes an unowned personal item (`BelongsToPlayer`) first becomes its owner; the
  server itself never does.
- The holder is the actor for a hand or an inventory, and nobody for anywhere else.
- An essential's claim flags follow its owner: into their inventory clears `Dropped` and `Stolen`;
  out of it sets `Dropped`; anyone else moving it sets `Dropped`, `Reserved` and `Stolen`.
- A forced change with `ClearRetrievalClaim` (a summoned job booklet) clears owner and claim.

The record takes only the placement, holder and claim flags from the result (`LiveItems.ApplyCore`):
the owner and the claim slot are the ledger's, and the ledger commit changes them.

A **forced** change skips the revision and possessor checks, never the gadget rule or the ledger.
The server forces its own changes: the host's own player's observed changes (`ServerItemManager.ProcessChanged`,
`HostLocalState`) and its put-aways (`HostInventory`), a leaver's items
([inventory.md](inventory.md#cold-inventories)), the invariant repair's move into a slot, and the
server's own moves of a deleted car's items and a destroyed tool's ammo. An admin's give isn't a
transition: it is born `Granted` by `ServerLedger.Own`, as a row (`PlayerItems.Issue`) or at the
player's feet.

## Revisions

- **On the server** each accepted change adds one to `LiveItem.Revision`. A change that alters only
  object state (a lantern's flame) is a run (`ObjectStateRunBase`, `ObjectStateRunTop`), and a request
  made against a revision inside that run still holds (`ItemPlacementRevision.UnchangedSince`): the
  server's copy of a flickering lantern doesn't refuse its holder's drop.
- **A player's own settle commit** doesn't make their requests stale either. When the player
  simulating a dropped item commits where it came to rest (`TryCommitSpatialPose`), the record keeps
  them as `SettledBy`, with the revisions from `SettledBase` (the oldest whose placement they still
  matched) to `SettledTop` (the commit's). While their settlements, and object state after them, are
  the only changes since, a request of theirs made against a revision in that window still holds
  (`ItemPlacementRevision.UnchangedFor`): a regrab just as the item comes to rest isn't refused
  because their own commit arrived first (OI-PHY-16). Anyone else's request, or one made after
  another move, is refused as before. The server's own settling has no window.
- **A stale request** is refused `stale-authority-revision` (code `StaleRevision` on the wire) with a
  correction to the sender. Recall needs the exact revision. Lost and Found's retrieval is refused
  `stale-lost-item-revision`, a physics commit `stale-spatial-authority-revision`. A swap checks the
  moved item against the requester's revision and the other against its own.
- **On a client** `NetworkedItemClient` reserves the next expected revision for each request it sends
  (`ItemOutboundRevisionPipeline`), so quick changes can follow each other before the answers come.
  Its own acknowledgement keeps the reservations; a correction (no originating player) resets them.
- **A late update** whose revision is older than the item's is dropped (`NetworkedItem.ReceiveSnapshot`,
  `item.stale-authority-snapshot-rejected`).

## Recall in one step

`Recall` (the owner's Get button on a silhouette) is one request and one commit, with no exchange
with the holder's game ([wire.md](wire.md#recall)). `NetworkServer.CarryRecall` sends it to Lost and
Found's collection if the item is there, else to `LiveItems.TryRecall`:

1. only an essential (`item-not-recallable`);
2. `ItemAccessServer.Allows(Recall)`: someone else's essential is refused
   `requester-not-persistent-owner` (OI-ACC-05);
3. `ItemStateMachine.Recall`: the owner, a claim with `Reserved` or `Locked`, not installed, the exact
   revision;
4. the slot: the one asked for (the silhouette clicked) if free, else its claim slot, else the lowest
   free one (`ServerLedger.FreeSlot`); none is `inventory-full`;
5. the ledger commit; a refusal puts the record back.

The update goes to everyone with no originating player, so every game applies it, the holder's
included: their hand empties and the item is in the owner's slot.

## NetworkedItem on each game

`NetworkedItem` (`Multiplayer/Shared/Items/LiveState/NetworkedItem*.cs`) is the item's component on
every game. It keeps what the server last said: `AuthorityRevision`, `Relation` (this game's player's
relation, [wire.md](wire.md#naming-a-player)), `InventoryClaimSlot` and flags, the last reason and
cause. It applies updates to the Unity object (`NetworkedItem.StateApplication.cs`: a hand, the local
inventory in its slot, the world, a car's end, installed), and watches the local game for changes
(`NetworkedItem.Snapshots.cs`).

Each game adds its part:

- **The server** (`NetworkedItemServerPart`, `NetworkedItemServer`): a local change waiting for the
  item manager's pass, validating and committing players' changes, applying its own canonical updates.
- **A client** (`NetworkedItemClient`): a local change goes out as a request
  (`ItemRequestTranslation.FromObservation`, [wire.md](wire.md#one-request-one-result)); it never
  makes an item of its own; a release grace for VR throws; its revision pipeline.
- `ItemSnapshotApplicationPolicy` keeps a client from applying the echo of its own throw, VR release,
  hold or object state a second time; `ItemStateObservationPolicy` keeps a remote player's placement
  from being recomputed through the local inventory.
- The server's copy of an item another player holds or carries reports only its server-authoritative
  values (`TrackedStateComposer.HostReportsForRemoteHolder`); a change of its copy with none of those
  isn't a change at all (`ProcessChanged` keeps its `LastDirtyTick`), or the players who knew it were
  sent the copy whole (a FullSync) and its drift overwrote the holder's (a lantern's wick swinging in
  their projected hand, after a take-out made the copy afresh). The copy's drift is put back too,
  each pass, to the last value the holder sent (`NetworkedItem.RevertRemoteHolderDrift`, before
  `GetSnapshot`): a drop carries no object state, so once they let go the server's copy is the
  item's, and its drifted wick (the knob turned back to full) was sent to everyone (`retrieved-state`).
  "Holds or carries" is the record's word as well as the snapshot's (`HeldByAnotherPlayer`): an object
  made for an item kept as data in their inventory lies inactive at the safety position and reads
  itself as dropped there, and that was taken as the server moving it out of their slot into the
  world (`stand-in-object-state`).

## Data-only records

An item in a connected player's inventory (a remote player's, and the host's own player's, D14), and
ammo in the magazine of a tool kept so (`PlayerItems.Ammo`), keeps its `LiveItem` (NetId, holder,
slot, revision: it's live), but the server has no object for it: its state is a row.
`PlayerItems.IsDataOnly(record)` says so. When it goes to data, and how its object is made again for
a request (`PlayerItems.WithObject`, so everything on this page runs on an object), is
[inventory.md](inventory.md#data-only-on-the-server)'s. The NetId stays the item's while it's data:
`NetworkedItem.HandOverToData` lets go of it without freeing it, the object made again takes it up
(`TakeUpFromData`), and it's freed only if the item goes cold (`FreeDataNetId`). The one-home check
reads such an item's row, not an object.

A Lost and Found entry is kept as data the same way, under its record and NetId, its row kept by
Lost and Found (OI-COLD-08); `PlayerItems.WithObject` makes its object again there
([lost-and-found.md](lost-and-found.md#kept-as-data)).

## Attached and snapped items

An item on a snap point is `Attached` in vanilla's terms (`ItemState.Attached`); the record says which
kind of snap point:

- **On a car's end** (an end-of-train lantern on `ItemSnapPointCoupler`): `Attached`, the car by its
  NetId (`AttachedCarNetId`) and GUID (`AttachedCarPersistentId`) and which end; ledger home
  `Attached(car, front)`.
- **On a gadget's snap point** (a hanger, a mount: `SnapPointGadget`, the only other kind in the game
  besides the player's belt): `SnappedAttachment`, the gadget by its item. The update names the
  gadget item's NetId (`ItemWorldParentKind.SnapPoint`, `WorldParentNetId`; the wire's `Snapped`
  home), which every game knows it by; the server keeps the stable key, the gadget item's own id
  (`SnapPointKey.OfGadget`, `gadget:<id>`, in `WorldParentKey`; `LiveItems.SnapPointKeyOf`), made from the NetId and never sent:
  clients never learn an item's id (OI-ID-07). The ledger home is `Snapped(key)`, and the save row's
  `snappedTo` is the key (OI-SYNC-03).

`NetworkedItem.CreateUpdateData` names the gadget (`GadgetSnapPoints.GadgetOf`), and every game snaps
the item onto it (`NetworkedItem.SnapToGadget`, from `HandleAttachedState`). At a load the gadget is
found by its id (`ServerLedger.NetIdOf`); one not there yet is waited for up to 30 s
(`ItemSaveLoad.SnapWhenItsGadgetIs`), then the item goes to its owner's Lost and Found
(`SaveRecovery`), as an item whose car is gone does; an unowned one, or one Lost and Found refuses,
into the world where it is (`DeletedCarItems.IntoWorld`), never left snapped to nothing. Before R8 a live gadget snap was `Attached` with
car 0, so its home was `Attached` with no car, and every load sent it to Lost and Found.

## Not on this page

- Physics (leases, samples, settlement, throws, fall rescue): [physics.md](physics.md).
- Projection (interest, what each client is shown, convergence): [projection.md](projection.md).

## Tests

- `Multiplayer.Core.Tests/Items/LiveState/ItemStateMachineTests.cs`: one record per NetId, one revision
  per change, stale and exhausted revisions, the possessor, first-taker ownership, claim flags,
  recall, forced changes, the server actor, installed items, offline owners, a mixed fuzz run.
- `Multiplayer.Core.Tests/Items/LiveState/ItemOutboundRevisionPipelineTests.cs`,
  `ItemSnapshotApplicationPolicyTests.cs`, `ItemStateObservationPolicyTests.cs`, `ItemRequestsTests.cs`.
- `Multiplayer.Core.Tests/Items/Ledger/ItemLedgerTests.cs`: a refused commit changes nothing
  (`ARefusedChange_LeavesTheLedgerAsItWas_FuzzRun`).
- Scenarios ([testing.md](../../testing.md)): `ledger-rows` (a take from another player refused, someone
  else's recall refused), `recall` (both `TAKEN` modes), `cold-homes` (data-only records, a stale move
  rolled back), `owned-retrieve`, `borrowed-disconnect`, `remote-held-item`, `item-request-repeat`;
  every scenario run ends with the one-home check.
