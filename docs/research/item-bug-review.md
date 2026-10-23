# Item lifecycle bug review

> Status: 2026-10-23, against beta ff06e150 (owned-items PRs 1a-5 merged). A deliberate hunt for
> ways a player loses an item, essential items first. Each bug is proved by an `item-bug-*`
> scenario that fails today, or marked "code only" when no game run reproduced it. Fixes belong to
> the owned-items work ([owned-items.md](../legacy/items/owned-items.md)) or the home named below; this
> review changes no mod code.

The rule to hold: **a player never loses an essential item, by any path.** Severity, worst first:
essential loss > inventory loss > duplicate > cosmetic.

## How the scenarios are marked

A scenario that proves a bug carries `[KnownBug("<id>")]` (`DevTools/DvmpScenarios/Framework/Attributes.cs`).
`dvmp scenario list` and the run's header say it is expected to fail. Its result is never a
regression: it prints `KNOWN` (exit 4) when it fails as expected and `FIXED?` (exit 5) when it
passes, and the gate's summary lists both without failing the batch. The PR that fixes the bug
deletes the attribute, and from then on the scenario is a required pass in that PR's batch and in
the gate. The scenarios are listed in [testing.md](../testing.md#known-item-bugs-expected-to-fail),
not in `gate.sh`'s default list.

## Proved in game

| Id | Bug | Scenario | Severity | Host kinds | Cause (today's beta) | Home |
| --- | --- | --- | --- | --- | --- | --- |
| SL4 | An essential **recalled out of a moving cab** keeps its physics lease, and the save writes the lease's placement: after a restart it lies in the cab, not the inventory. | `item-bug-recall-lease` | essential displaced | both (proved on each; on a dedicated host the server's record even stays TrainInterior after the recall, with the client showing the radio in its slot) | `Server/Items/LiveState/LiveItems.cs:572`: the recall commits through `ApplyCore`, skipping `OnAuthoritativeItemTransition` (`:351`), the only `RevokeHostLease("logical-placement-changed")` (`ServerItemSpatial.cs:113`); the save prefers the lease (`WorldItemPersistenceSave.cs:43,188`) | none: spatial leases. Small and clearly scoped: revoke the lease when a recall commits. |
| LF2 | An **unowned item in a deleted car** is destroyed (a mug, a job booklet, the shop's scanner, office stock). Vanilla puts a deleted car's items back in the world; a destroyed scene item is tombstoned in the save. | `item-bug-car-delete-unowned` | inventory loss; the scanner is a permanent world loss | both (proved on each) | `Server/Items/LostAndFound/LostAndFoundServer.cs:288-292`: `CollectFromDeletedCar` destroys whatever `Collect` refuses, and `Collect` refuses anything not `BelongsToPlayer` (`:303`) | none: car-delete handling. Fixtures (the scanner) go back to their spot under the ledger's Fixtures rule; other unowned items should drop into the world as in vanilla. |

## Checked, holds

| Id | What was checked | Scenario | Result |
| --- | --- | --- | --- |
| E6 | The player moves an essential to another slot and an ordinary item into its old one (the report: "an essential was put in the same slot as another item"), then rejoins; and the same across a save and restart, for a client and for the host's own player. | `item-bug-essential-slot-rejoin` (`RESTART=1`, `WHO=host`) | Holds on both host kinds: every essential comes back in its own slot. The server never hears of moves inside the inventory, so the layout returns to the claim slots (the moved essential back in its old slot, the ordinary item elsewhere). Not a loss; the reported case must have another route (E1, E2a or E10 fit). |
| E1 | A dropped essential's silhouette refused at a rejoin because ordinary items took its slot first (arrival order). | `item-bug-dropped-essential-rejoin LOST=0` | Held in 6 rejoins on a player host with 6-13 ordinary items. The code path is real (below), but natural arrival order didn't trigger it. |

## Code only (not reproduced in game)

| Id | Bug | Severity | Host kinds | Cause | Home |
| --- | --- | --- | --- | --- | --- |
| E1 | A silhouette arriving after an ordinary item took its slot is refused; nothing retries. Reached through E10's retrieval too. | essential loss | both | `InventoryIntegration.cs:141-171` (`EnsureDroppedClaim` never calls `MakeRoomForClaim`); `Multiplayer.Core/Items/Inventory/SlotClaimPlanner.cs:57-58` | none: client claim placement |
| E2b | The client restores any owner's claim slot, and `MakeRoomForClaim` won't move an item that claims the slot for another owner: two radios on slot 0, one left in no slot. The old route (a borrower leaving with an offline owner's essential) is closed by #304/#305; a restart while borrowing (SL1) remains. | essential loss | both | `StateApplication.cs:814-821`, `InventoryIntegration.cs:203-206` | none: client claim placement |
| E4 | Claim repair and make-room both aim at the highest free slot (35 down, the VR belt), and the repair ranks by a slot the server may have wrong (E6). | essential loss (recall impossible) | both | `Multiplayer.Core/Items/Essentials/ClaimSlotPolicy.cs:82-86`, `InventoryIntegration.cs:209`, `LiveItems.cs:1097-1100` | none |
| E5 | The host's own player skips `ReadyForItems`, so a claim clash its save holds is never repaired. | essential loss (needs a clash in the save) | player host | `Server/Session/NetworkServer.cs:1062-1066`; the repair runs only at `:1195-1198` | none |
| SL1 | An item borrowed when the server restarts is saved under the borrower and released only to them. The owner never gets it while the borrower stays away, and no replacement essential is issued (the ledger counts it live). | essential loss | both | `WorldItemPersistenceSave.cs:64`, `WorldItemPersistenceRestore.cs:210-216`, `ItemSaveLoad.cs:81` | owned-items PR 6 (#310, retrieve from a player) partly; the save rule: none. Needs a restart without the client rejoining, which the pair harness can't do yet. |
| SL2 | A held item at disconnect: dropped off the train it was on, its lease given to the leaver, never saved if unowned, office stock destroyed after 30 s. Owned items now reach their owner's Lost and Found (#304/#305). | inventory loss (unowned carried items) | both | `ServerItemManager.PlayerItems.cs:109-118`, `NetworkServer.cs:492-493`, `WorldItemPersistenceSave.cs:155-157`, `OfficeStockReplenishment.cs:26,127-133,163` | owned-items "3+" (the departing player's hand item goes into their inventory) |
| SL3 | A scene-authored item in a player's inventory survives one restart, not two (its baseline comes from the restore). | inventory loss | both | `LiveItems.cs:147`, `WorldItemPersistenceSave.cs:165-168` | none: world persistence |
| SL5 | A saved row that fails to restore once (missing item mod, authored key not found) is erased at the next save, offline players' inventories included. | inventory loss (situational) | both | `WorldItemPersistenceRestore.cs:29,35-45,129`; `WorldItemPersistenceSave.cs:26-31` | none: world persistence (the ledger's `Missing` keeps the entry, not the item) |
| LF3 | A container withdrawal trusts the client's inventory slot; into a full inventory the server records the item in the inventory while the client has it in no slot. | inventory loss | both, remote client | `ContainerServer.cs:265,382` | none: cold containers (ItemAccess's Withdraw isn't wired) |
| LF5 | `CarSpawner.DeleteTrainCarsFromTrack` skips the Lost and Found patch: a car's items go to the pool. No vanilla caller found. | inventory loss | both | `Server/Items/LostAndFound/CarDeleteLostAndFoundPatch.cs:15-29` | none: car-delete handling |
| LF7 | An essential deposited in a container comes back without its Reserved claim. | essential degraded (no recall) | both | `Multiplayer.Core/Items/Access/ItemAccess.cs:74-79`, `ContainerServer.cs:383` | owned-items #311 (draft: essentials refused from containers) |
| LF8 | Office stock left in a parked cab is destroyed after 30 s. | cosmetic | both | `OfficeStockReplenishment.cs:26,127-133,163` | by design; needs the user's decision |
| D1 | Dedicated host: a rejoined owner's ground item is collected into Lost and Found after the second restart (the owner's position isn't settled 10 s after loading). | inventory loss (recoverable) | dedicated | `LostAndFoundServer.cs:727-730` | none (from the ledger's #282 runs; `ledger-save`) |
| D2 | Dedicated host: a cab item stays in vanilla's Lost and Found list after a load while its record says TrainInterior. | cosmetic / duplicate risk | dedicated | `WorldItemPersistenceRestore.cs:64-115` never moves it out of vanilla's list | none (from the ledger's #282 runs; `ledger-save`) |

Also open from the ledger's batches, listed in `items-ledger-bug-map.md` (notes) and not repeated
here: LF-inert, CAB-POS, SR-+1 and COIN.

## Fixed by the owned-items PRs

| Id | Bug | Fixed by |
| --- | --- | --- |
| E3 | The join summary hid the claim slots of essentials not in the inventory, so DV's safeguard put a fresh essential on a dropped one's slot | #306 (server-made essentials, safeguards off) |
| E7 | `DiscardUnboundLocalDuplicate` destroyed the wrong copy | #306 |
| E8 | A client-made starter kit existed only on the client for ~10 s | #306, #307 |
| E9 | The host's safeguard counted a held, borrowed essential as its own | #306 |
| LF1 | A car delete destroyed an offline owner's items | #304 (+#305); scenario `car-delete-owner` |
| LF4 | An offline owner's world item went to vanilla's Lost and Found behind the server's back | #304 |
| LF6 | A withdrawal spanning the owner's disconnect left the item with nobody | #304 |
| E10 | An essential in Lost and Found had no silhouette after its owner rejoined: the server sent the rejoined owner only the tombstone, and their game had no copy of the item to project it onto | items/lf-essential-silhouette: the owner is sent the item before its tombstone (`ServerItemManager.Interest.cs`); scenario `item-bug-dropped-essential-rejoin` (gate run `lost-essential-rejoin`) |
| E2a | An essential issued into a full inventory at a rejoin was in no slot, active at the world origin, while the server recorded it in the inventory | items/essential-overflow: the issuer counts what the player carries and makes what doesn't fit at their feet, straight into their Lost and Found (`InventoryOverflow`; `StartingItemsIssuer.cs`, `LiveItems.RegisterMaterializedColdItem` takes a dropped snapshot); scenario `item-bug-full-inventory-essential` (gate run `full-inventory-essential`). Another route into a full inventory (a Lost and Found retrieval, a container withdrawal, LF3) still has the client-side gap: a refused Reserved claim counts as placed (`NetworkedItem.StateApplication.cs:673-679`) |

## Vanilla facts the bugs rest on

From the decompiled game (DV.Inventory, Assembly-CSharp):

- `Inventory.AddItemToInventory(item, slot)` never overwrites: an occupied slot, a silhouette
  included, returns -1. Without a slot it takes the first free one; `GetLastFreeSlot` scans from 35
  down (slots 33-35 are the belt in VR).
- `MoveItemFromTo` refuses a target that holds anything; `SwapItems` doesn't update the reserved-slot
  map, so a purge after a swap can blank the other item's slot.
- At load, `StartingItemsController.InitializeInventory` places items by saved slot and ignores the
  result: of two records for one slot, the loser is in no slot and no storage.
- `StorageController.AddItemToStorageItemList` removes an item from its old storage before checking
  the new one accepts it; a refused move leaves it in none, so it isn't saved.
