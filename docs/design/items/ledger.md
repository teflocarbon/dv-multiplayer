# The ledger

> Status: built in R3 of the [item refactor](../item-refactor.md), 2026-10-24. This page says what the code does.
> It covers the ledger of owned items (`Multiplayer.Core/Items/Ledger`, `Multiplayer/Server/Items/Ledger`):
> entries, homes, owners, operations, births, cold and live homes, the re-key merge and the one-home check.
> Inventories are [inventory.md](inventory.md), causes [change-causes.md](change-causes.md), the wire
> [wire.md](wire.md); the save format changes in R4 (`save-load.md`).

## The entry

`ItemLedger` (Core) holds one `LedgerEntry` per owned item, live or ended, keyed by the item's GUID.
An unowned item has no entry: its live record's placement is its home. Only `ItemLedger` changes an
entry; everything else gets copies (`TryGet`, `All`, `LiveOwnedBy`, `LiveWith`).

| Field | What |
| --- | --- |
| `Id` | the item's GUID, for life |
| `Owner` | an `OwnerRef` ([Owners](#owners)) |
| `Prefab` | the item's prefab name |
| `Home` | where it is, exactly ([Homes](#homes)) |
| `ClaimSlot` | for an essential (`Essential`, vanilla's `IsEssential`): the slot of its owner's inventory it comes back to; else -1 |
| `ShopStocked` | it still has vanilla's Restock flag, so it counts against its owner's shop stock |
| `Born` | `Birth`: reason, time, by (an identity, or empty for the server), from (an item id), detail |
| `Ended` | `End`: reason (an `ItemRemovalReason`), time, replacedBy, detail; null while live |

## Homes

`Home` (`Multiplayer.Core/Items/Ledger/LedgerEntry.cs`) is a kind (`HomeKind`) and what names it there.
The pose isn't part of it: live state keeps poses. `ServerLedger.HomeOf(record)` turns a live record's
placement into a home.

| Kind | Made with | Names |
| --- | --- | --- |
| `Slot` | `Home.InSlot(holder, slot)` | a player's exact inventory slot, by identity (the VR belt is slots 33-35) |
| `Hand` | `Home.InHand(holder, hand)` | a player's hand, by identity. Never saved: a leaver's hand item goes to a slot |
| `World` | `Home.World` | loose in the world |
| `TrainInterior` | `Home.TrainInterior(car)` | loose in a car, by the car's GUID |
| `Shelf` | `Home.Shelf(key)` | on a static parent, by its key |
| `Container` | `Home.Container(id, slot)` | a cold container's slot, by the container's id |
| `LostAndFound` | `Home.LostAndFound` | its owner's Lost and Found |
| `Installed` | `Home.Installed(anchor)` | a gadget on a car (its GUID) or on the world (`Home.WorldAnchor`, `:global:`) |
| `Attached` | `Home.Attached(car, front)` | an end-of-train lantern on a car's end |
| `Snapped` | `Home.Snapped(parent)` | on a snap point, by its parent's key |
| `Missing` | `Home.Missing` | the load found no store holding it; goes in R4 (D9, OI-LED-16) |

An installed gadget's home names only its anchor; the gadget keeps its own UID (D13). A save's gadget
on the world stays installed at load (`GadgetItemTransitions.CommitInstalled`) and a joining player
gets it (`GadgetJoinSnapshot`, car 0 for the world), OI-GAD-10. One on the house, shed or paint
station is still taken off into the world; installing on the world live is still refused (OI-GAD-11, paused).

## Owners

`OwnerRef` is `OwnerRef.None` or `OwnerRef.Player(identity)`, never a session number, so an offline
owner's items are as owned as anyone's. `OwnerKind` has room for a company; nothing uses it yet. The
live record stores no owner: `LiveItem.Owner` reads `ServerLedger.OwnerOf(id)` and
`LiveItem.InventoryClaimSlot` reads `ServerLedger.ClaimOf(id)`, falling back to what the item's store
named (`StoredOwner`, `StoredClaimSlot`) only until the saved ledger is restored. "Has a radio" reads the
ledger (`CountLive`). The wire carries no owner, only each player's relation ([wire.md](wire.md#naming-a-player)).

## Operations

| Operation | What it does |
| --- | --- |
| `Register(BirthRequest)` | An item becomes owned. `Invalid` without an owner or prefab; `Refused` when its home is a taken slot. An id the ledger already has, live or ended, is a copy: registered under a new id, born `Duplicate` from the first (`Reminted`). An essential gets its claim here |
| `Replace(usedId, reason, replacement)` | Vanilla used an item up and made another: the new one is the used one's owner's, born `Replaced` from it; the used entry ends with `replacedBy` |
| `Commit(id, from, to, cause)` | The one way a player's change moves an owned item. Refusals below |
| `Repair(id, to, cause)` | Puts a live item in `to` whatever its home was: for the load, the one-home check and the server's own placing, never a player's request. The slot rules still hold |
| `Swap(id, from, otherId, otherFrom, cause)` | Two items of one holder's inventory change slots in one commit. Refused with `stale-home`, `slot-invalid` (not two slots of one holder) or `slot-reserved` (a third essential claims either slot) |
| `End(id, reason, replacedBy, detail)` | Ends a live entry. It stays for lineage; each owner keeps the newest 100 ended |
| `Revive(id, home)` | Brings an ended entry back, for a load that finds its item in a store. Raised as `Born` |
| `Rekey(from, to)` | One identity merges into another ([The re-key merge](#the-re-key-merge)). Raises nothing |
| `Restore(saved, starterKits, displaced)` | Replaces the ledger with saved entries, raising nothing: an id saved twice keeps its first; a second live entry in one slot goes to Lost and Found; clashing claims move apart |
| `CanTake(home, id, owner)` | The slot rules alone, for an item the ledger doesn't hold (OI-SLOT-03) |

`Commit` refuses, and changes nothing (OI-LED-04), with:

| Refusal | When |
| --- | --- |
| `unknown-item` | no live entry |
| `stale-home` | the entry's home isn't `from`: a stale or repeated request |
| `slot-invalid` | `to` is a slot of no holder, or out of range |
| `slot-occupied` | another item is in that slot, owned or not (`SlotTakenOutside` asks live state and the cold rows about unowned ones) |
| `slot-reserved` | another of the holder's essentials, not in their inventory, claims that slot (its silhouette fills it) |

**Claims** are the ledger's (OI-ESS-01..03): the slot asked for, else the owner's slot the item is in,
else the lowest free slot off the belt (`InventorySlots.ChooseClaim`, which the issuer asks too), so an
owner's claims are unique by construction. An essential its owner puts in a slot off the belt claims
that slot; one that claimed it before takes the mover's old claim, or the lowest free one, and its
owner's game is told (`ClaimSlot` change, `LiveItems.SendClaim`). On the belt it keeps its old claim.

## The ledger decides, live state follows

A change to an owned item is a ledger commit before anything else sees it. In `LiveItems.ApplyTransition`
the record works out the new placement and slot, then `ServerLedger.CommitTransition` commits it; only
then is the update written, logged (`ItemChangeLog.RecordMove`) and sent. A refusal puts the record back
(`RestoreFrom`) and corrects the sender. `CommitTransition` commits an owned item (`ServerLedger.Commit`);
makes an unowned personal item (`BelongsToPlayer`) its first taker's (`Own`, born `Claimed`, or the
cause's birth: `Purchased`, `Granted`, ...); and holds any other unowned item to `CanTake`.

Recall (`TryRecall`), Lost and Found collection and retrieval and slot swaps (`TrySwapSlots` →
`ServerLedger.Swap`) commit the same way. A `MoveSlot` request into an empty slot is a stash there, onto
another item of the requester's a swap ([inventory.md](inventory.md)). A server-made placement (a
withdrawal, a cold item made live, a Lost and Found restore) goes through `ServerLedger.Settle`: a
`Repair` to where the record has it, or a birth; a refusal is logged `item.ledger-refused`.

Two checks catch a path that skips the ledger. `ServerLedger.Commit` meeting `stale-home` (record and
ledger disagree on `from`) logs `item.ledger-stale-home` and repairs the ledger to `to`, so a drift never
wedges an item. `ServerLedger.OnMove`, called by `RecordMove`, logs `item.ledger-not-first` with the
callers when an owned item is somewhere its home disagrees with, and the ledger follows. A physics
settlement is the one move made record first on purpose: physics decides where a thrown or dropped
item lands, so `ServerLedger.OnSettled` gives the ledger that home (the world, a car's interior, a
shelf) as it lands, from one car into another too.

`ServerLedger.OnRemoved` (from `ItemChangeLog.RecordRemoval`) ends the entry with the removal's reason,
except `CollectedToLostAndFound` (home becomes `LostAndFound`), `DepositedInContainer` (the deposit
committed it first, `ServerLedger.CommitStored`), `LeftInterest`, `WentCold`, and while the world unloads. Every change raises
`ItemLedger.Changed` with a `LedgerChange`: its kind (`Born`, `Moved`, `Ended`, `ShopStocked`,
`ClaimSlot`), a copy of the entry, the home it left and a `LedgerCause` ([change-causes.md](change-causes.md#the-ledgers-cause)).

## Births and ends

| Birth | Made by |
| --- | --- |
| `Purchased` | a shop delivery (`ShopDelivery`) |
| `StarterKit`, `Issued` | the issuer (`StartingItemsIssuer`), through `RegisterMaterializedColdItem` and `Settle` |
| `Granted` | an admin's give (`AdminItemGrants`), owned by its target |
| `Claimed` | the first player to take an unowned personal item |
| `Replaced` | `ServerLedger.RecordReplacement`; no game path calls it yet (OI-LIN-02..04, paused with gadgets) |
| `Made` | nothing yet |
| `Migrated` | the load registering an owned item the ledger never saw; a gadget's saved owner; the check's repair of a stray; a restored, withdrawn or retrieved item with no entry |
| `Duplicate` | a copy of an id already in use: `Register`, or `Own` for an object `ClaimPersistentId` re-minted |

An id is for life. When an object registers an id another live object has,
`ServerLedger.ClaimPersistentId` keeps the first, gives the copy a new id (`ItemIdentity.CopiedFrom`
names the original) and logs `item.duplicate-persistent-id`; once owned, the copy is born `Duplicate`
from the original (OI-ID-05, -18). An ended id that comes back gets a new one too (`TakeNewId`). An
entry ends with an `ItemRemovalReason`: binning, using up, a deleted car, a money deposit, or
`DestroyedByGame` when nothing said why.

## Cold and live homes

D12. A home nobody is using is **cold**: the item is data, its saved state as a row plus its ledger
entry, with no object on any game, so no vanilla path, sweep or respawn can touch it. Every other home
is **live**: an object people can see and touch.

- **Going cold** (OI-COLD-02). `PlayerItems.Leave` (`Server/Items/Inventory`): a leaver's own or
  nobody's hand item goes to a free slot, its claim first (D5); a borrowed item, in hand or inventory,
  is dropped and sent to its owner's Lost and Found (`BorrowerLeft`); an owned hand item with no free
  slot goes there too (`InventoryOverflow`). Every inventory item then `GoCold`s: its state becomes a
  row in `ColdInventories` (state, exact slot, claim) and its object is destroyed on every game
  (`WentCold`). The ledger entry stays, home `Slot(holder, slot)`. Job papers stay objects. The save
  writes the rows; a load makes no object for an offline holder's items.
- **Going live.** At join `PlayerItems.Join` makes each row again (`GoLive`) in its exact slot, once
  the ledger has loaded. A taken slot means a free one; with none free, or with the ledger's home
  `LostAndFound` (a re-key clash), it is dropped at the player's feet and sent to Lost and Found
  (`InventoryOverflow`). R4 makes this happen before the inventory shows (OI-COLD-03).
- **Data only on the server** (OI-COLD-06). An item in a connected remote player's inventory, settled
  there 1 s (`PlayerItems.Update`), becomes a row too: the server hands its NetId over
  (`HandOverNetId`), destroys its own object and keeps the live record; the holder's game has the only
  object. Anything that names it has the object made again first under the same NetId
  (`PlayerItems.WithObject`, `TakeUpNetId`): an item request (the item, the shell it opens, the slot it
  swaps with, a retrieval's subject), a shop banknote deposit, a container browse. Container shells
  included. The host's own player's items and job papers stay objects (OI-COLD-07, moved to R6).
- **Optimistic clients** (OI-COLD-05). A client shows a stash, slot move or drop at once and sends a
  request; the server commits it to the ledger and confirms. Only a refusal rolls the client back (a
  refused swap corrects both items).

**Still open:** Lost and Found entries stay objects on the server until R6 (OI-COLD-08, user 2026-10-24).

**Containers (R5).** A stored item's home is `Container(id, slot)`, and the graph keeps no owner of
its own: who owns an item, and so who keeps a box (its shell's owner, else its outer box's keeper,
else its holder), is the ledger's. A deposit, a withdrawal and a move between boxes commit the
ledger first by the item's id (`ServerLedger.CommitStored`, since a stored item has no live record)
and move it back if the graph or the object refuses after ([containers.md](containers.md)).

## The re-key merge

When a player's install GUID is re-keyed to their Steam id (`InstallGuidRekey.MoveInto`, at a verified
login), everything under the old id merges into the new one, never skipped (OI-ID-19). `ItemLedger.Rekey`
moves owners, holders, `Born.By` and the starter kit, ended entries included. An item whose slot the
new id already fills goes to `LostAndFound` (`RekeyResult.ToLostAndFound`, logged); a clashing claim
moves to a free slot. Cold rows follow (`ColdInventories.Rekey`): a row that clashed, or that the ledger
sent to Lost and Found, loses its slot and is placed at join. Live records, Lost and Found and the
container graph are re-keyed too.

## The invariant check

`HomeInvariant.Check` (Core) takes the ledger and every copy of an item the server can see and returns
each violation. `ServerLedger.Invariant.cs` gathers the copies: live records' objects (inactive ones
too: records are walked, never `FindObjectsOfType`, OI-LED-10), cold and data-only rows, pending Lost
and Found entries and container rows, with vanilla's storage lists.

| Rule | Holds when |
| --- | --- |
| 1 `OneHome` | each live entry's item has exactly one copy (a `Missing` entry isn't looked for) |
| 2 `LiveAgrees` | that copy is in the entry's home (a loose item's kind and anchor; a hand's holder) |
| 3 `NoStray` | no owned copy without a live entry, and no copy of an ended entry |
| 4 `OneItemPerSlot` | one item per holder slot, owned and unowned together; one claim per owner slot |
| 5 `VanillaAgrees` | in at most one vanilla list; nothing in a dedicated host's own inventory; only the host's player's items in its inventory list; nothing owned in its Lost and Found list unless home `LostAndFound` |

`ServerLedger.CheckInvariant(when)` runs at load (`load`), before every save (`before-save`), every 5 s
in a Debug build (`debug-tick`), on demand (`ServerLedger.CheckNow`, `asked`), and after every scenario
run (`ScenarioRun.LeakCheckAsync` calls the `Invariant.Check` step in `DevTools/DvmpScenarios/Library/Ledger.cs`
and fails the run on any line). Each breach is logged once as an error,
`item.invariant-broken (<when>): <violation>`, and published as the debug event `item.invariant-broken`
(category `inventory`; [observability.md](../../observability.md)).

A Debug build stops there, and the scenario fails. A Release build then repairs (`ServerLedger.Repair`
in `ServerLedger.Repair.cs`, D7): the ledger wins on where.

- `LiveAgrees`: `PutHome` puts the item in its ledger home. The ledger first follows the record, so
  the move commits from where the item is; a data-only item is made again first (`WithObject`).

  | Ledger home | The repair |
  | --- | --- |
  | a connected player's slot | into that slot: out of Lost and Found as a recall (`TryRetrieveForRecall`), out of another player's hands by way of the ground (`Retrieval.DropFromHolder`; a take from a player is refused), from anywhere else directly. Their claim or another free slot if that one is taken |
  | a connected player's hand | into a free slot of theirs: the server doesn't put things in hands |
  | an offline player's slot | a cold row there, its object gone (`PlayerItems.GoColdInto`); not from Lost and Found |
  | `LostAndFound` | collected (`SendToLostAndFound`, `SaveRecovery`), dropped from a player's hands first |
  | the ground, a train, a shelf (the ledger keeps no pose), a container's slot, an install | the owner's Lost and Found |

  Where the move is refused, the item goes to its owner's Lost and Found; where that is refused too, or
  the item has no owner, it stays and the ledger follows it. An item installed, attached or snapped
  where it is stays, and the ledger follows: the server can't take it off yet (a retrieval can't
  either, R8).
- `NoStray` of a record whose store named an owner: registered, born `Migrated`;
- a cold row that disagrees has no object to move: its holder's join makes it again, and the next check
  puts it home;
- the rest (lost, copied, two in a slot, vanilla's lists) stays logged for R4's load recovery.

`ServerLedger.SendToLostAndFound(id, reason, detail, out refused)` is the one way into Lost and Found by
id (OI-LF-08). An item already there is accepted; else it is refused as `unknown-item`,
`item-has-no-owner` or `item-is-with-a-player` before its object is looked for, so a data-only item is
never made just to be refused.

## Saving the ledger

`ServerLedger.WriteSave` writes it with `LedgerSaveCodec` under `Multiplayer.OwnedItemsV1`, format 2
(exact homes; format 1 reads as none, and the load migrates from the stores). `ServerLedger.HostTick`
restores it before any store imports an item; once the item save, Lost and Found and containers have
imported, `Reconcile` gives slotless inventory items a slot (`GiveSlots`: the saved claim, else the
lowest free, essentials first), applies `LoadReconciler`'s plan and runs the check. R4 replaces this
with one key, `Multiplayer.Items`, and carries out the steps only logged today (`DropCopy`, `RecoverToLostAndFound`).

**Reading it.** `ItemLedger.Dump(owner)` lists entries one per line, live first, with home, claim and
birth (OI-LED-13): in a `dvmp_eval` probe on the host, `return ServerLedger.Dump();`. Scenarios use the
`Ledger.Dump(owner)` and `Ledger.Where(netId)` steps in `DevTools/DvmpScenarios/Library/Ledger.cs`.

## Open

The ownership questions open before the refactor, asked again. None is answered here.

- **When should ownership change hands?** The ledger changes an owner only at birth and in a re-key,
  with no transfer operation, and taking from another player is refused (OI-ACC-07), so a pick-up can't
  stand for a gift. An explicit gift (from the owned-items page, or a handover with a confirm), or
  never? A transfer would be a new ledger change with its own cause, and would move the Lost and Found
  destination and an essential's claim to the new owner at once.
- **Items made by machines or printers.** `BirthReason.Made` exists and nothing registers it. Owned by
  the player who operated it, the one who paid, or nobody? Decide when the first one syncs.
- **Installed gadgets saved with no owner.** One with no saved owner stays unowned and anyone may
  remove it. With D1 (an old save starts items fresh, R4), does that case still arise, and should such
  a gadget stay unowned or go to the career's owner?
- **Permissions.** `ItemAccess.Decide` is the one decider ([access.md](access.md)), with fixed rules and
  no notion of friends. What may others do with your items (take, retrieve, deposit), what is the
  default, and is it granted per player or per company?
- **What players see.** Red for every item someone else owns is built (D3). Open: an owner label (the
  wire tells only the relation, so a name would have to be sent), a "borrowed" mark beyond the red, and
  an item's Lost and Found destination outside the debug UI.
- **Companies.** The model is chosen (2026-10-20: a company shares money, licences and trains). Open:
  whether a company owns items (`OwnerKind` has room), a company Lost and Found, and what leaving a
  company does to its items in your hands.

## Tests

- `Multiplayer.Core.Tests/Items/Ledger/ItemLedgerTests.cs`: registration and re-minting, claims (off
  the belt, following the owner's stash, displacing another), commits and their refusals, swaps,
  replacement, ended entries and pruning, restore, re-key and the merge, `HasIdentity`, `Dump`, and a
  fuzz run (a refused change leaves the ledger as it was).
- `Multiplayer.Core.Tests/Items/Ledger/HomeInvariantTests.cs`: each rule, a cold row as the one copy,
  and a fuzz run of a ledger that only ever committed and a world that followed it.
- Scenarios ([testing.md](../../testing.md)): `ledger-rows` (an inactive item, the dump, `Duplicate` and
  `Granted` births, `SendToLostAndFound` refusing an item with a player), `ledger-repair` (the
  Release repair, called directly, for each kind of home), `inventory-slots`, `cold-homes`,
  `rekey` (`MERGE=true`), `item-bug-essential-slot-rejoin`, `ledger-save`, `item-id-roundtrip`,
  `item-lineage`, `starting-items`, `owned-list`, `owned-retrieve`, `recall`, `borrowed-disconnect`,
  `gadget-save`, `shop`. Every run ends with the one-home check.
