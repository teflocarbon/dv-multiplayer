# Essentials and starting items

> Status: built in R3 of the [item refactor](../item-refactor.md), 2026-10-24. This page says what the code does.
> It covers DV's essentials, the starter kit, the server's issuer, claim slots and recall. Where
> every item's slot is kept is [inventory.md](inventory.md); the entry, homes and births are
> [ledger.md](ledger.md); the recall request on the wire is [wire.md](wire.md#recall).

The issuer is `StartingItemsIssuer` (`Multiplayer/Server/Items/Essentials/StartingItemsIssuer.cs`).
The claim rules it shares with the ledger are in Core: `InventorySlots.ChooseClaim`
(`Multiplayer.Core/Items/Inventory/InventorySlots.cs`) and `ItemLedger`
(`Multiplayer.Core/Items/Ledger/ItemLedger.cs`). There is no Core `Items/Essentials` folder.

## What an essential is

An essential is an item whose vanilla `InventoryItemSpec.IsEssential` is true: the comms radio, the
maps, the compass, the wallet, the catalogue. The ledger marks an entry essential from that flag at
birth (`BirthRequest.Essential`, set in `ServerLedger.Own`). Which essentials a player should have
comes from DV itself: `AdminItemGrants.StartingItemPrefabs()` calls
`StartingItemsController.GetStartingItemsPrefabs` for the career's save data, so the difficulty
decides, and the issuer asks a claim for each of them.

An essential differs from an ordinary owned item in four ways:

- it has a **claim slot**: a slot of its owner's inventory that is its own, wherever the item is;
- its owner can **recall** it into that slot from anywhere;
- the owner's inventory shows its **silhouette** in that slot while it is away;
- it **never goes in a container**.

## Starter kit and issued essentials

No game makes its own starting items: DV's `StartingItemsSafeguard` and `LicensesSafeguard` are off in
every multiplayer game (`Shared/Career/StartingItemsControllerPatch.cs`). The server makes them.

| | Starter kit | Issued essentials |
| --- | --- | --- |
| What | shovel, lighter, Oiler, Lantern, Flashlight (full battery, `Restock`), Hanger, DuctTape | each prefab of `StartingItemPrefabs()` |
| When | once per player per career | at every join, for each prefab the player has no live item of |
| Marker | `ItemLedger.HasStarterKit` / `MarkStarterKit`, saved with the ledger | none: "has" is counted each time |
| Born | `StarterKit` | `Issued` |
| Slot | the first free slot from slot 14 up, then from slot 0, never the belt; none free: Lost and Found | a claim slot, the lowest free one off the belt |
| Claim | none | yes, `Reserved` |

Licence papers are never made in multiplayer (`PhysicalLicensePaperPatch` returns null from DV's
`CreateLicense` overloads while a session runs); a licence is an entitlement only. That row,
OI-START-07, is unproved and moves to R6.

### The issuer

`NetworkServer.CompleteLoading` calls `StartingItemsIssuer.OnPlayerLoaded(player)` when a player has
finished loading. That covers a client and the host's own player alike (OI-START-06): the host's
player loads through its local session like any other. A dedicated host has no player of its own.

One pass per player at a time: a second call while a pass runs (`issuing`) logs a warning and does
nothing. A pass:

1. **Waits for the ledger.** It waits up to 60 s (`LedgerWait`, unscaled time) for
   `ServerLedger.Loaded`, the stores' import and the load check. If the player leaves meanwhile it
   stops. If the ledger still isn't loaded it makes nothing and logs "the owned-items ledger didn't
   load in 60 s; nothing made": better nothing than a second set. The next join's pass tries again
   (OI-START-08).
2. **Reads what the player has, from the ledger alone** (OI-START-04).
   `Has(player, prefab)` is `ServerLedger.Ledger.CountLive(owner, prefab) > 0`. A live entry
   anywhere counts: in the inventory (a cold row's entry stays live), in Lost and Found, in a
   container, with another player, lying in the world. An ended one doesn't, so a destroyed or binned
   essential is made again under a new id. `OwnedItemsSummary` is gone.
3. **Picks slots the ledger would accept.** `TakenSlots` is every slot one of the player's essentials
   claims (`ClaimedSlots`) plus every slot `ItemLedger.CanTake` refuses: one an owned entry is in
   (an offline player's cold rows included, since their ledger home stays `Slot(holder, slot)`), one
   an unowned item fills (`SlotTakenOutside`), and one another essential's silhouette reserves. Each
   missing essential then takes `InventorySlots.ChooseClaim(-1, taken)`, the lowest free slot off the
   belt, the same function the ledger uses for a claim (OI-ESS-02). Kit items take `FreeSlot(taken,
   14)`: from slot 14 up to 32, then from 0, never onto the belt.
4. **Makes each item** (`Make`), as a cold-container withdrawal does: instantiated at DV's safety
   position with `BelongsToPlayer` set, its state loaded a frame later, registered through
   `LiveItems.RegisterMaterializedColdItem` with the cause `Issued` or `StarterKit` (the birth
   reason follows from it, `ServerLedger.BirthFrom`), and sent to everyone. An essential asks for its
   claim with `InventoryClaimSlot` and the `Reserved` flag; the ledger gives it at birth. A failed
   item is destroyed and logged; the others still go.
5. **Marks the kit** given, even if one kit item failed: a kit is given once.

### No free slot

The server knows every slot, so with none free a made item never lands in no slot (OI-START-09). An
item whose slot came back -1 is made at the player's feet, dropped, and collected straight into their
Lost and Found with reason `InventoryOverflow` ("made with the inventory full"). If Lost and Found
refuses it, it stays at their feet and a warning is logged.

An essential overflows when slots 0-32 are all taken or claimed, even with a belt slot free: a claim
never takes the belt. An overflowed essential asks for no claim; the ledger gives it one at birth only
if a slot off the belt has come free meanwhile, so it usually has none, and gets one the first time
its owner puts it in a slot off the belt (below).

Every other route into a full inventory (a Lost and Found retrieval, a container withdrawal, a recall)
is OI-START-10, R6.

## Claim slots

The ledger assigns claims; nothing repairs them afterwards (OI-ESS-01). `EnsureDistinctClaimSlots`
and `ClaimSlotPolicy` are gone. `LiveItem.InventoryClaimSlot` reads the ledger
(`ServerLedger.ClaimOf`).

- **At birth** (`ItemLedger.Register`): an essential takes the slot the request asks for if it's
  free, else the slot of its owner's inventory it's in, else the lowest free slot off the belt
  (`InventorySlots.ChooseClaim`), else none. That covers an issued essential and one a player owns by
  first taking it.
- **Following its owner** (`ItemLedger.FollowClaim`, on every `Commit`, `Repair` and `Swap`): when
  the owner puts their essential in a slot of their own inventory off the belt, that slot becomes its
  claim. If another of their essentials claimed that slot, the other takes this one's old claim, or
  the lowest free one. An essential put on the belt (slots 33-35) keeps the claim it had (OI-ESS-03).
- **Unique per owner.** Two claims of one owner never share a slot: every assignment picks from the
  slots `ClaimedSlots` leaves free, and the one-home check's rule 4 (`OneItemPerSlot`) reports two
  claims on one slot as a breach.
- **The slot stays free.** A commit into a slot another of the holder's essentials claims while that
  essential is elsewhere is refused, `slot-reserved` (`ItemLedger.SlotRefusal`). An ordinary item
  can't take a silhouette's slot, on any player's game (OI-ESS-09).
- **The host's own player is the same.** The rules are the ledger's, with no per-player repair, so the
  host's player has no clash to repair (OI-ESS-04).
- **The owner's game is told.** A claim the ledger moved for another essential raises a `ClaimSlot`
  ledger change, and `ServerLedger.OnLedgerChanged` sends the owner the item's new claim
  (`LiveItems.SendClaim`). On the wire the claim goes only to its owner ([wire.md](wire.md#naming-a-player)).
- **Re-keys merge claims.** When an install GUID is re-keyed to a Steam id that already has data
  (`ItemLedger.Rekey`, OI-ID-19), a claim on a slot the new identity already claims moves to a free
  one, or none.
- **Old saves.** At load, `ServerLedger.GiveSlots` gives inventory items saved without a slot one,
  essentials first, each taking its saved claim if it can.

The claim's flags (`Reserved`, `Locked`, `Dropped`, `Stolen`) are live state's, not the ledger's
(`LiveItems.ApplyCore`). A non-owner who takes a claimed essential marks it `Stolen`; a recall clears
`Dropped` and `Stolen`.

## The silhouette

While an essential is away from its claim slot, its owner's inventory shows it there as a red
silhouette with vanilla's Return button, which recalls it. The silhouette is the item's own object,
kept in the owner's game:

- **In the world or another player's hands.** The owner's interest keeps the item whatever its
  distance (`ServerItemManager.IsNonSpatialRelevantTo`, `ownerHasRecallClaim`), so the silhouette and
  its Return button don't vanish when the item is far away.
- **In Lost and Found, and after a rejoin** (OI-ESS-08). An owner who joined after the collection has
  no copy of the item for the Lost and Found tombstone to project.
  `ServerItemManager.TryCreateLostEssentialForOwner` sends that owner, and only them, the item's
  `Create` first; the tombstone after it makes it inert and leaves the silhouette. Lost and Found
  entries stay objects on the server until R6 (OI-COLD-08, user 2026-10-24).
- **Arriving after an ordinary item** (OI-ESS-09). The server never puts an ordinary item on a
  claimed slot (`slot-reserved`, above). On a client whose items arrive in any order,
  `InventoryIntegration.RestoreClaim` first moves a stale occupant without a claim on that slot to
  the last free slot off the belt (`MakeRoomForClaim`), and `PlaceAt` does the same for an item the
  server put in a slot.

## Recall

A recall is one step and only the owner's ([wire.md](wire.md#recall)). The owner clicks the
silhouette; their game sends `ItemAction.Recall` with the silhouette's slot
(`NetworkedItemManager.RequestItemRecall`); the server commits it and sends everyone the update
(`NetworkServer.CarryRecall`). If the essential is data only on the server (in a remote player's
inventory), it is made again first (`PlayerItems.WithObject`).

From anywhere but Lost and Found, `LiveItems.TryRecall`:

1. refuses an item that isn't an essential, `item-not-recallable`;
2. asks `ItemAccessServer.Allows(..., Recall, ...)`: someone else's essential is refused,
   `requester-not-persistent-owner` (OI-ACC-05; [access.md](access.md));
3. runs `ItemStateMachine.Recall`: the requester must be the owner, the item must have a claim slot
   with `Reserved` or `Locked`, must not be installed (`item-installed`), and the revision must match;
4. puts it in the slot `ServerLedger.FreeSlot` gives: the slot asked for (the silhouette clicked),
   else its claim, if the ledger would take it there, else the lowest free slot, the belt last;
   `inventory-full` if none;
5. commits the move to the ledger (cause `Recalled`); if the ledger refuses, the record is restored
   and nothing is sent. The update, sent to everyone, empties the holder's hand on every game.

From Lost and Found it is a collection: `LostAndFoundServer.TryRetrieveForRecall` collects the entry
into the silhouette's slot (`recall-claim-slot-missing` without one). Lost and Found checks the owner
itself (`LostItemRetrievalPlanner`, `requester-not-persistent-owner`); it doesn't ask `ItemAccess`.

Not done: a recall out of a moving cab doesn't end the item's physics lease (OI-ESS-11, R7).

## Kept out of containers

An essential can't be deposited, even by its owner: `ItemAccess.Decide` refuses a `Deposit` of an
essential with `essential-not-in-containers` before any other deposit rule, and
`ContainerServer.Deposit` asks it (OI-ESS-07). The item stays in the inventory. So an essential's
claim and recall always work: it is never cold inside a container where the claim can't reach it.

## Tests

Core (`Multiplayer.Core.Tests/Items/...`):

- `Ledger/ItemLedgerTests.cs`: `Essentials_EachGetAClaimSlotOfTheirOwn_TheLowestFreeOffTheBelt`,
  `AClaim_NeverTakesTheBelt_NorASlotAnotherItemFills`, `APlainItem_HasNoClaimSlot`,
  `AnEssential_ClaimsTheSlotItsOwnerFirstStashesItIn`,
  `AnEssentialPutWhereAnotherClaims_TakesTheClaim_AndTheOtherTakesItsOld`,
  `TwoItemsInOneInventory_SwapSlots_InOneCommit_AndTheClaimsFollow`, `ASwap_LeavesAThirdEssentialsClaimAlone`,
  `Restoring_TwoItemsSavedInOneSlot_SendsTheSecondToLostAndFound_AndMovesClashingClaimsApart`.
- `Ledger/HomeInvariantTests.cs`: `Rule4_TwoItemsInOneSlot_OwnedOrNot_AndTwoClaimsOnOne_AreBroken`.
- `Inventory/InventorySlotsTests.cs`: `AClaim_TakesTheSlotAskedFor_ElseTheLowestFreeOffTheBelt`.
- `LiveState/ItemStateMachineTests.cs`: the recall tests (`Recall_*`, `RecallByNonOwner_IsRejectedWithoutMutation`,
  `NobodyElseRecallsAnOfflineOwnersEssential`, `InstalledItem_TakesObjectStateAndCannotBeRecalled`).
- `Access/ItemAccessTests.cs`: `AnEssential_CantBeDeposited_EvenByItsOwner`, `AnEssential_IsStashedAsUsual`.

Scenarios (`DevTools/DvmpScenarios/Scenarios/`):

- `starting-items`: each essential once across rejoins, a destroyed radio made again born `Issued`,
  one in Lost and Found counts, the kit once per career born `StarterKit`.
- `ledger-rows`: the issuer makes nothing while the ledger isn't loaded and the next pass reads it
  (OI-START-08); the host's player has its starting items (OI-START-06); someone else's recall is
  refused (OI-ACC-05).
- `item-bug-full-inventory-essential`: an essential issued into a full inventory ends in a slot or in
  Lost and Found (OI-START-09).
- `item-bug-dropped-essential-rejoin`: the silhouette is back after a rejoin, from Lost and Found
  (OI-ESS-08) or the world (`LOST=0`, OI-ESS-09).
- `item-bug-essential-slot-rejoin`, `inventory-slots`: a moved essential claims its new slot, one on
  the belt keeps its old claim, a reserved slot is refused, and every item is back after a rejoin.
- `recall` (and `TAKEN=1`, out of the host's hands): a dropped essential recalled into its slot.
- `deposit-refused OWNER=essential`: `essential-not-in-containers`.
- `rekey` (`MERGE=true`): essentials kept, none issued again, across a re-key.
