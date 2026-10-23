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

For an owned item the record's placement is written only after the ledger has committed the same
home. An unowned item has no ledger entry: its placement is its home, and the ledger's slot rules
still cover it (`ServerLedger.UnownedInSlot`).

## The live item

`LiveItems` (`Multiplayer/Server/Items/LiveState/LiveItems.cs`) keeps one `LiveItem` per NetId.

| Field | What |
| --- | --- |
| `NetId` | the item's network id this session |
| `Revision` | counts every accepted change; see [Revisions](#revisions) |
| `Placement` | `ItemPlacementKind`: `World`, `PlayerHand`, `PlayerInventory`, `TrainInterior`, `StaticParent`, `Attached`, `SnappedAttachment`, `Installed`, `LostAndFound`, `Container`, `Destroyed` |
| `Holder` | the identity of the player whose hand or inventory it's in, or empty. They may be offline |
| `PlacementHand` | which hand, for `PlayerHand` |
| `Slot` | its slot of the holder's inventory, for every inventory item, owned or not (OI-SLOT-01); else -1 |
| `Owner` | read from the ledger, never stored: `ServerLedger.OwnerOf(PersistentItemId, StoredOwner)` (OI-LED-05) |
| `InventoryClaimSlot` | read from the ledger: `ServerLedger.ClaimOf(...)` |
| `InventoryClaimFlags` | how the claim shows; none without a claim, and always `Reserved` with one |
| `StoredOwner`, `StoredClaimSlot` | what the item's own store named, read only until the ledger's saved entries are in (`ServerLedger.Restored`) |
| `PersistentItemId`, `AuthoredItemKey` | the item's id, or a scene-authored item's key |
| `Position`, `Rotation`, `WorldParent*`, `ParentLocal*`, `AttachedCar*` | the pose and what it hangs from |
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
cause `HostCorrection`, which puts their game back.

The other ways in, each also ledger-first:

| Method | For |
| --- | --- |
| `TryApplyServerTransition` | the server's own world on a host with no player (`HostItemActor.IsServer`): an empty actor, which moves items about the world but never holds one or becomes an owner |
| `TryApplyGadgetTransition` | the gadget code: the only way into or out of `Installed` |
| `TryRecall` | an owner's recall ([below](#recall-in-one-step)) |
| `TrySwapSlots` | two items of one inventory swapping slots ([inventory.md](inventory.md#moving-between-slots)) |
| `TryMoveToLostAndFound`, `TryRetrieveFromLostAndFound`, `RestoreLostRecord` | Lost and Found's collection and retrieval |
| `RegisterMaterializedColdItem` | an item made from data into an inventory: a withdrawal, the starting items, a cold row going live |
| `TryCommitSpatialPose` | a physics settlement; the ledger takes where it landed (`ServerLedger.OnSettled`) |

`ServerLedger.OnMove` checks every accepted move afterwards: an owned item that moved without a
ledger commit first is logged `item.ledger-not-first` and the ledger follows.

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
`HostLocalState`), a leaver's items ([inventory.md](inventory.md#cold-inventories)), an admin's give.

## Revisions

- **On the server** each accepted change adds one to `LiveItem.Revision`. A change that alters only
  object state (a lantern's flame) is a run (`ObjectStateRunBase`, `ObjectStateRunTop`), and a request
  made against a revision inside that run still holds (`ItemPlacementRevision.UnchangedSince`): the
  server's copy of a flickering lantern doesn't refuse its holder's drop.
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
4. the slot: its claim slot if free, else the lowest free one (`ServerLedger.FreeSlot`); none is
   `inventory-full`;
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

## Data-only records

An item in a connected remote player's inventory keeps its `LiveItem` (NetId, holder, slot, revision:
it's live), but the server has no object for it: its state is a row (OI-COLD-06,
[inventory.md](inventory.md#data-only-on-the-server)). `PlayerItems.IsDataOnly(record)` says so. The
NetId stays the item's while it's data: `NetworkedItem.HandOverToData` lets go of it without freeing
it, the object made again takes it up (`TakeUpFromData`), and it's freed only if the item goes cold
(`FreeDataNetId`). The one-home check reads such an item's row, not an object. A request that names
it has the object made first (`PlayerItems.WithObject`), so everything on this page runs on an object.

Lost and Found entries are still objects on the server; whether they become data is waiting on the
user (OI-COLD-01).

## Not on this page

- Physics (leases, samples, settlement, throws, fall rescue): R7's `physics.md`; until then
  [item-spatial-physics.md](../../legacy/items/item-spatial-physics.md).
- Projection (interest, what each client is shown, convergence): R7's `projection.md`; until then
  [item-world-sync.md](../../legacy/items/item-world-sync.md).

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
