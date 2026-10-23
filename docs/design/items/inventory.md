# Inventories

> Status: built in R3 of the [item refactor](../item-refactor.md), 2026-10-24. This page says what the
> code does. Every player's inventory is server data: each item in it has an exact slot on the
> server, a client's change to it is a request, a leaver's inventory goes cold and comes back at
> join. Claims are issued by the ledger ([ledger.md](ledger.md), [essentials.md](essentials.md));
> this page says how a client shows them. The record and its transitions are [live-state.md](live-state.md)'s.

| Side | Code |
| --- | --- |
| Core | `Multiplayer.Core/Items/Inventory/`: `InventorySlots`, `BeltSlots`, `SlotClaimPlanner`, `InventoryPresentation` |
| Server | `Multiplayer/Server/Items/Inventory/`: `PlayerItems` (`PlayerItems.cs`, `PlayerItems.DataOnly.cs`), `ColdInventories`, `InventoryMembershipPatch` |
| Shared | `Multiplayer/Shared/Items/Inventory/`: `InventoryIntegration`, `InventoryItemStatePatch` |
| Client | `Multiplayer/Client/Items/Inventory/`: `InventorySlotMovePatch`, `InventoryOwnershipUiPatch` |
| Requests | `NetworkServer.CarryMoveSlot` and `DataOnlyNamed` (`Multiplayer/Server/Session/NetworkServer.Items.cs`) |

## Slots

`InventorySlots` has Derail Valley's 36 slots (`Capacity`); the last three, 33 to 35, are the VR belt
(`FirstBeltSlot`, `IsBelt`).

- **Every inventory item has an exact slot on the server,** owned or not: `LiveItem.Slot` (OI-SLOT-01).
  An owned item's ledger home is `Slot(holder, slot)` with the same slot; an unowned one's slot is its
  record's. The update carries it (the home `Slot` on the wire, [wire.md](wire.md#the-update)), and a
  client puts the item in exactly that slot (`InventoryIntegration.PlaceAt`).
- **One item per slot** (OI-SLOT-03). The ledger refuses a home another item fills, owned, unowned
  (`ServerLedger.UnownedInSlot`) or a cold row (`ColdInventories.At`): `slot-occupied`. A slot one of
  the holder's essentials claims while that essential is elsewhere (its silhouette fills it) is
  `slot-reserved`; a slot out of range is `slot-invalid`.

Which slot a change gets (`LiveItems.TryApplyTransition` and the methods beside it):

| Change | Slot |
| --- | --- |
| a player's stash or slot move naming a slot | that slot, or a refusal |
| a change that doesn't move the item (object state) | the slot it had |
| a server-made move into an inventory (a leaver's hand item, a forced change) | the slot named, else the one it had in that inventory, else its claim (if the holder owns it), else the lowest free one (`ServerLedger.FreeSlot`) |
| a recall | its claim, else the lowest free one |
| a Lost and Found retrieval, a withdrawal, a starting item, a cold row going live | the slot named, else its claim, else the lowest free one |

The lowest free slot is counted from 0, so the belt comes last (`InventorySlots.FirstFree`). With no
free slot a move into an inventory is refused `inventory-full`.

An old save's inventory items without a slot are given one at load (`ServerLedger.GiveSlots`):
essentials first, each its saved claim if free, else the lowest free one; cold rows the same.

## The belt

The belt slots are ordinary slots to the server: an item on the belt is in its holder's inventory,
and only the owner's game shows it snapped to their body. `BeltSlots` keeps a VR release onto the belt
from looking like a drop: nothing is sent in the frame the hand lets go (`HoldVrRelease`), the echo
of the player's own hold after the belt took the item is skipped (`IsSupersededOwnHold`), and an
update that puts a belt item in its holder's inventory leaves it snapped (`KeepBeltSnap`).

A claim never takes a belt slot (`InventorySlots.ChooseClaim`, OI-ESS-03), and an essential moved onto
the belt keeps its old claim; moved to another slot off the belt, its claim follows it
([ledger.md](ledger.md)). When a client makes room for an item, the item it moves aside goes to the
last free slot off the belt, never onto it.

## Claims as a client shows them

The claim is sent only to its claimant ([wire.md](wire.md#naming-a-player)). On that player's game:

- **A silhouette.** An essential out of its owner's inventory shows as a dropped, reserved entry in its
  claim slot (`InventoryIntegration.EnsureDroppedClaim`, planned by `SlotClaimPlanner.EnsureDroppedClaim`).
  An update putting it back in its claim slot restores that entry (`RestoreClaim`); an item without a
  claim in that slot is moved aside first (`MakeRoomForClaim`).
- **Red.** An item someone else owns is red, online owner or not (D3, OI-ESS-12):
  `InventoryPresentationPlanner.Plan` from the item's relation, drawn by `InventoryOwnershipUiPatch`
  (`ForeignItemInventoryMarker`). Someone else's silhouette left in the local inventory is purged
  (`PurgeForeignDroppedClaims`, run after each inventory change by `InventoryItemStatePatch`).
- **The Get button** on a silhouette routes by `InventoryRecallRoute`: someone else's item is blocked,
  an item that can't be recalled is blocked, the owner's own essential sends a `Recall`
  ([live-state.md](live-state.md#recall-in-one-step)), and one in Lost and Found is collected.

## Moving between slots

Vanilla moves an item to an empty slot (`Inventory.MoveItemFromTo`) or swaps two (`Inventory.SwapItems`)
on the player's game. `InventorySlotMovePatch` then sends one `MoveSlot` request with the item now in
the target slot and that slot. Moves the mod makes itself for the server (`PlaceAt`,
`MakeRoomForClaim`, inside `BeginAuthoritativePlacement`) are already the server's and aren't sent.

`NetworkServer.CarryMoveSlot` checks the slot is valid (`slot-invalid`), then:

- **into an empty slot** it's a stash with the slot named (`ItemRequestTranslation.ToChange` gives it
  `InInventory`), through the ordinary commit, so the slot rules above refuse a reserved or taken slot;
- **onto another of the requester's items** the two swap: `LiveItems.TrySwapSlots` checks both are in
  the requester's inventory (`item-not-in-your-inventory`), runs the state machine for each (the moved
  item at the requester's revision, the other at its own), and `ServerLedger.Swap` commits both owned
  ones at once (`ItemLedger.Swap`), or the one owned, or the slot rules for two unowned ones. Both
  updates go to everyone.

A move is logged `SlotMoved` with the slots it went between.

## The optimistic client

Every inventory change is a request the ledger commits first (OI-COLD-05). A client's game shows a
stash, slot move, hotbar switch, drop or pick-up at once, as vanilla does it, and sends it: the
observed change as a request (`NetworkedItemClient.SendLocalObservation`) or the slot move above. It
doesn't wait. The server commits it in its own order and sends the accepted update to everyone, the
requester included as its acknowledgement.

Only a refusal rolls the client back: the server sends the requester a correction, the record's
state with the cause `HostCorrection` (`SendCanonicalRejectionCorrection`), and their game applies it,
which puts the item back in its slot, hand or the world. A refused swap corrects both items. A refused
stash the server keeps in the player's hand drops at their feet, so the two games agree.

## Data only on the server

An item in a connected remote player's inventory is data on the server, not a second object
(OI-COLD-06, `PlayerItems.DataOnly.cs`). That player's game holds the only object.

- **Going to data.** `PlayerItems.Update` scans every 0.25 s. An item that has sat 1 s in a remote
  player's inventory (`ShouldBeData`: a slot, the holder connected and loaded and sent the item, not
  being made or moved, not waiting for its restore) goes: its row is kept in `ColdInventories` (its
  state, slot, owner, claim), its `LiveItem` stays, and the server's object is destroyed
  (`GoData`). The holder keeps theirs, and so does an owner whose silhouette stands for it; anyone
  else who was shown it gets a `Destroy` (`WentCold`). The NetId stays the item's
  ([live-state.md](live-state.md#data-only-records)). Container shells go to data too
  (`PlayerItems.DataOnlyShell` finds one by its container id).
- **Made again first.** Anything that names such an item has the server's object made from its row,
  under the same NetId and in its slot, before it runs (`PlayerItems.WithObject`): an item request
  naming the item, the shell it opens, the item a `MoveSlot` would swap with, or a retrieval's subject
  (`NetworkServer.DataOnlyNamed`); a shop banknote deposit (`HandleShopDeposit`); a container browse
  (`HandleItemListRequest`). The players who had it are marked as knowing the new object, so it isn't
  sent to them again. Once the request is done and it settles, it goes back to data.
- **Not data:** the host's own player's items (the host's game is theirs) and job papers, which stay
  objects on the server; making them data is OI-COLD-07, R6.

## Cold inventories

A player who isn't here has a cold inventory (D12): rows in `ColdInventories`, each one item's saved
state (`SavedItemRow`), its exact slot and its claim, with no object on any game. An owned item's
ledger entry stays, home `Slot(holder, slot)`.

**Leaving** (`PlayerItems.Leave`, from the server's disconnect):

1. Someone else's item, in their hand or inventory, is dropped where they stood and sent to its owner's
   Lost and Found (`BorrowerLeft`); one kept as data is made again first.
2. A hand item of their own, or nobody's, goes into a free slot, its claim first (D5, OI-JOIN-06). With
   none free it's dropped; an owned one then goes to its owner's Lost and Found (`InventoryOverflow`),
   an unowned one stays at their feet.
3. Every other item in their inventory goes cold (`PlayerItems.GoCold`, OI-COLD-02): its row is kept,
   a `Destroy` with the cause `WentCold` goes to every other game, and the server's object is destroyed.
   A data-only item already has its row: only its record and NetId go. A scene-authored item becomes
   an ordinary one first, so the scene doesn't make it again. Job papers don't go cold (R6, OI-COLD-07).

The save writes every row (`ItemSaveLoad.Save`), and a load makes no object for an item whose holder
isn't connected: its row goes straight into `ColdInventories` (`ItemSaveLoad.Restore`). A row saved in a
hand has no slot, and gets one at load. Writing it in R4's format is R4's (`save-load.md`).

**Joining** (`PlayerItems.Join`, from `NetworkServer.AdmitPlayer`, after the re-key): each of the
player's rows is made again (`GoLive`), once the ledger is loaded. The prefab is made, its state loaded
a frame later, as vanilla's starting items are, and it's registered in its holder's inventory under
its own id (`LiveItems.RegisterMaterializedColdItem`) and sent to everyone. Its slot:

- its saved slot if it's still free (`ServerLedger.FreeSlot`, the row's slot first), else its claim,
  else the lowest free one;
- with no slot free, or with the ledger's home `LostAndFound` (a re-key clash), it overflows: it's
  dropped at the player's feet and sent to their Lost and Found (`InventoryOverflow`); an unowned one
  stays there.

A row whose prefab is unknown, or whose making fails, stays cold. Making the rows before the player's
inventory is shown is OI-COLD-03, R4.

## The re-key merge

When an install GUID is re-keyed to a Steam id (`InstallGuidRekey.MoveInto`, at login), it merges
into whatever the Steam id already has, never skipping (OI-ID-19):

- live records' holders follow (`LiveItems.RekeyIdentity`);
- the ledger moves owners and homes (`ItemLedger.Rekey`); an item whose slot the Steam id already
  fills goes to Lost and Found, and a clashing claim moves to a free slot;
- cold rows follow after it (`ColdInventories.Rekey`): a row whose slot the Steam id's rows already
  use, or that the ledger sent to Lost and Found, loses its slot. At join it takes a free slot, or goes
  to Lost and Found as above.

## The host's own player

The host's own player has an inventory like anyone's, keyed by identity, with the same slots, rules
and ledger homes. How its changes reach the server:

- Its grabs, drops, stashes and hotbar switches are the server's own world already: the server's item
  manager sees them (`ServerItemManager.ProcessChanged`) and commits each as a forced change by the
  host's player (`HostLocalState`), with the slot vanilla put it in. Forced skips the revision and
  possessor checks; access, the ledger's slot rules and the commit order still apply.
- Its slot moves and swaps, recalls, collections and container requests are requests through its local
  session (`LocalPlayerSession.Items`), handled in-process by the same handlers as a client's
  (`NetworkServer.RegisterLocalItems`).
- Its items are never data only, and `PlayerItems.Join` makes its cold rows as it is admitted.

A dedicated host has no player and no inventory: the server moves items about the world but never
holds one ([live-state.md](live-state.md#a-change)).

## Tests

- `Multiplayer.Core.Tests/Items/Inventory/`: `InventorySlotsTests` (a claim's slot off the belt, the
  lowest free slot, belt last), `BeltSlotsTests`, `SlotClaimPlannerTests`, `InventoryPresentationTests`
  (red, the Get button's routes).
- `Multiplayer.Core.Tests/Items/Ledger/ItemLedgerTests.cs`: two items never share a slot, a swap in
  one commit with the claims following, a stale or cross-inventory swap refused, a claim never on the
  belt, the re-key merge (`Rekey_OntoAnIdentityWithData_MergesIt_ASlotClashGoesToLostAndFound`).
- Scenarios ([testing.md](../../testing.md)): `inventory-slots` (and `WHO=host`): stash, move, swap,
  the belt, a reserved slot refused, the same slots after a rejoin; `cold-homes`: data on the server,
  a move while data, a refused move rolled back, cold rows and D5 across a rejoin;
  `item-bug-essential-slot-rejoin`; `rekey MERGE=true`; `borrowed-disconnect`; `shop` (the banknote
  deposit); `starting-items`; `owned-list` and `owned-retrieve` (an item in an inventory); every run
  ends with the one-home check.
