# The ledger

> Status: built in R3 of the [item refactor](../item-refactor.md), 2026-10-24. This page says what the code does.
> It covers the ledger of owned items (`Multiplayer.Core/Items/Ledger`, `Multiplayer/Server/Items/Ledger`):
> entries, homes, owners, operations, births, cold and live homes, the re-key merge and the one-home check.
> Inventories are [inventory.md](inventory.md), causes [change-causes.md](change-causes.md), the wire
> [wire.md](wire.md), the save [save-load.md](save-load.md).

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
| `Magazine` | `Home.Magazine(tool, slot)` | loaded in a live tool's magazine, by the tool's item id and the slot ([containers.md](containers.md#ammo-in-a-magazine)); a row tool's ammo is `Container(tool, slot)` |
| `Unloadable` | `Home.Unloadable` | its saved row can't be made because its prefab is gone (an item mod removed, D9): no object, the row kept verbatim and tried again at each load ([save-load.md](save-load.md#rows-that-wont-load)) |

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
| `Register(BirthRequest)` | An item becomes owned. `Invalid` without an owner or prefab; `Refused` when its home is a slot it can't take (`slot-invalid`, `slot-occupied`, `slot-reserved`). An id the ledger already has, live or ended, is a copy: registered under a new id, born `Duplicate` from the first (`Reminted`). An essential gets its claim here |
| `Replace(usedId, reason, replacement)` | Vanilla used an item up and made another: the new one is the used one's owner's, born `Replaced` from it; the used entry ends with `replacedBy` |
| `Commit(id, from, to, cause)` | The one way a player's change moves an owned item. Refusals below |
| `Repair(id, to, cause)` | Puts a live item in `to` whatever its home was: for the load, the one-home check and the server's own placing, never a player's request. The slot rules still hold |
| `Swap(id, from, otherId, otherFrom, cause)` | Two items of one holder's inventory change slots in one commit. Refused with `stale-home`, `slot-invalid` (not two slots of one holder) or `slot-reserved` (a third essential claims either slot) |
| `End(id, reason, replacedBy, detail)` | Ends a live entry. It stays for lineage; each owner keeps the newest 100 ended |
| `Revive(id, home)` | Brings an ended entry back, raised as `Born`. Nothing calls it |
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

A change to an owned item is a ledger commit (`ServerLedger.CommitTransition`) before it is written,
logged or sent; a refusal puts the record back and corrects the sender ([live-state.md](live-state.md#a-change)).
A job paper is its job's, never owned or in the ledger (D5, OI-ID-12: `Own` makes nothing of one).

Recall (`TryRecall`), Lost and Found collection and retrieval and slot swaps (`TrySwapSlots` →
`ServerLedger.Swap`) commit the same way. A `MoveSlot` request into an empty slot is a stash there, onto
another item of the requester's a swap ([inventory.md](inventory.md)). A container's deposit,
withdrawal and move commit by id first (`ServerLedger.CommitStored`); a withdrawal then only places the
row (`PlayerItems.Issue` with an `own` that does nothing, or `ItemSaveLoad.MakeLive` at the feet). The
server's other placings go through `ServerLedger.Settle`, a `Repair` to where the record has it or a
birth: a Lost and Found restore (`LiveItems.RestoreLostRecord`), a row made into an inventory
(`LiveItems.RegisterDataOnly`, from `PlayerItems.Issue` and the join's rows) and an object made into
one (`LiveItems.RegisterMaterializedColdItem`). A refusal is logged `item.ledger-refused`.

Two checks catch a path that skips the ledger. `ServerLedger.Commit` meeting `stale-home` (record and
ledger disagree on `from`) logs `item.ledger-stale-home` and repairs the ledger to `to`, so a drift never
wedges an item. `ServerLedger.OnMove`, called by `RecordMove`, logs `item.ledger-not-first` with the
callers when an owned item is somewhere its home disagrees with, and the ledger follows. A physics
settlement is the one move made record first on purpose: physics decides where a thrown or dropped
item lands, so `ServerLedger.OnSettled` gives the ledger that home (the world, a car's interior, a
shelf) as it lands, from one car into another too.

`ServerLedger.OnRemoved` (from `ItemChangeLog.RecordRemoval`) ends the entry with the removal's reason,
except `CollectedToLostAndFound` (home becomes `LostAndFound`), `DepositedInContainer` (the deposit
committed it first, `ServerLedger.CommitStored`), `LeftInterest`, `WentCold`, `RemadeFresh` (the item
made again keeps its entry, D9), and while the world unloads. Every change raises
`ItemLedger.Changed` with a `LedgerChange`: its kind (`Born`, `Moved`, `Ended`, `ShopStocked`,
`ClaimSlot`), a copy of the entry, the home it left and a `LedgerCause` ([change-causes.md](change-causes.md#the-ledgers-cause)).

## Births and ends

| Birth | Made by |
| --- | --- |
| `Purchased` | a shop delivery (`ShopDelivery`) |
| `StarterKit`, `Issued` | the issuer (`StartingItemsIssuer`): a row in its slot through `PlayerItems.Issue` (`LiveItems.RegisterDataOnly`, then `Settle`); with the inventory full, an object at the player's feet through `RegisterMaterializedColdItem` and `Settle` |
| `Granted` | an admin's give (`AdminItemGrants`, `ServerLedger.Own`), owned by its target |
| `Claimed` | the first player to take an unowned personal item; and any other birth whose move names none of the reasons above (`ServerLedger.BirthFrom`): the check's repair of a stray with a stored owner, an item `OnMove` or `Settle` finds owned by its store but not in the ledger |
| `Replaced` | `ServerLedger.RecordReplacement`: a spray can's empty can (`UseUp`, [vanilla-storage.md](vanilla-storage.md#used-up)); the gadget tools later (OI-LIN-02..04, paused with gadgets) |
| `Made` | nothing yet |
| `Duplicate` | a copy of an id already in use: `Register`, or `Own` for an object `ClaimPersistentId` re-minted |

An id is for life. When an object registers an id another live object has,
`ServerLedger.ClaimPersistentId` keeps the first, gives the copy a new id (`ItemIdentity.CopiedFrom`
names the original) and logs `item.duplicate-persistent-id`; once owned, the copy is born `Duplicate`
from the original (OI-ID-05, -18). An ended id that comes back gets a new one too (`TakeNewId`). An
entry ends with an `ItemRemovalReason`: binning (`Discarded`, by its binner, [vanilla-storage.md](vanilla-storage.md#bins)), using up (`UsedUp`,
[vanilla-storage.md](vanilla-storage.md#used-up)), a money deposit (`MoneyDeposited`, a client's or the host
player's, [vanilla-storage.md](vanilla-storage.md#money)), the dev console (`DevConsole`), or `DestroyedByGame` when
nothing said why.

## Cold and live homes

D12. A home nobody is using is **cold**: the item is data, its saved state as a row plus its ledger
entry, with no object on any game, so no vanilla path, sweep or respawn can touch it. Every other home
is **live**: an object people can see and touch. An inventory item that goes cold, or is data only on
the server, keeps its entry and its home `Slot(holder, slot)`. How inventories do it is
[inventory.md](inventory.md): [Cold inventories](inventory.md#cold-inventories) (leaving and
joining), [Data only on the server](inventory.md#data-only-on-the-server) and
[The optimistic client](inventory.md#the-optimistic-client).

**Lost and Found** (R6, OI-COLD-08): an entry is data on the server too. Its object goes at the scan
after it arrives, once its state is loaded, and Lost and Found keeps its row; a retrieval or a recall
makes it again first ([lost-and-found.md](lost-and-found.md#kept-as-data)).

**Containers (R5).** A stored item's home is `Container(id, slot)`, and the graph keeps no owner of
its own: who owns an item, and so who keeps a box (its shell's owner, else its outer box's keeper,
else its holder), is the ledger's. A deposit, a withdrawal and a move between boxes commit the
ledger first by the item's id (`ServerLedger.CommitStored`, since a stored item has no live record)
and move it back if the graph or the object refuses after ([containers.md](containers.md)).

## The re-key merge

A re-key (`InstallGuidRekey.MoveInto`) merges one identity into another; the ledger's part is
`ItemLedger.Rekey`, which moves owners, holders, `Born.By` and the starter kit, ended entries included.
The merge as a whole is [inventory.md](inventory.md#the-re-key-merge).

## The invariant check

`HomeInvariant.Check` (Core) takes the ledger and every copy of an item the server can see and returns
each violation. `ServerLedger.Invariant.cs` gathers the copies: live records' objects (inactive ones
too: records are walked, never `FindObjectsOfType`, OI-LED-10), cold and data-only rows, pending Lost
and Found entries and container rows, with vanilla's storage lists.

| Rule | Holds when |
| --- | --- |
| 1 `OneHome` | each live entry's item has exactly one copy (an `Unloadable` entry isn't looked for) |
| 2 `LiveAgrees` | that copy is in the entry's home (a loose item's kind and anchor; a hand's holder) |
| 3 `NoStray` | no owned copy without a live entry, and no copy of an ended entry |
| 4 `OneItemPerSlot` | one item per holder slot, owned and unowned together; one claim per owner slot |
| 5 `VanillaAgrees` | in at most one vanilla list; nothing in a dedicated host's own inventory; only the host's player's items in its inventory list; nothing owned in its Lost and Found list unless home `LostAndFound` |

`ServerLedger.CheckInvariant(when)` runs at load (`load`, from `ServerLedger.FinishLoad`), before every save
(`before-save`, from `ItemSaveLoad.WriteSave`, after the scene items in inventories are promoted), every 5 s
in a Debug build (`debug-tick`), on demand (`ServerLedger.CheckNow`, `asked`), and after every scenario
run (`ScenarioRun.LeakCheckAsync` calls the `Invariant.Check` step in `DevTools/DvmpScenarios/Library/Ledger.cs`
and fails the run on any line). `ServerLedger.ChecksRun(when)` counts them, for scenarios. Each breach is logged once as an error,
`item.invariant-broken (<when>): <violation>`, and published as the debug event `item.invariant-broken`
(category `inventory`; [observability.md](../../observability.md)).

A Debug build stops there, and the scenario fails. A Release build then repairs each breach (`ServerLedger.Repair` in `ServerLedger.Repair.cs` and
`ServerLedger.Strays.cs`, D7), once per item per check. The ledger wins on where; what isn't the
ledger's item in its home is a stray: an owned one goes to its owner's Lost and Found, an unowned one
into the world. An item part way into or out of a box waits for the next check (`ContainerGraph.IsSettled`).

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
  the item has no owner, it stays and the ledger follows it. An item on a car's end, on a gadget's snap
  point or loaded in a tool is taken off as a retrieval takes it off (OI-LIST-14), into its owner's Lost
  and Found (`TakeOff`), and from there recalled into its slot if its home is one. An installed item
  stays and the ledger follows: gadget removal is paused (OI-LIST-15).
- `OneHome`, no copy anywhere: made again fresh from its prefab, with the same id, in its owner's Lost
  and Found (`RemakeLost`, `ItemSaveLoad.MakeLostAgain`, `SaveRecovery`), as an item whose state won't
  load is (D9); with no player owner or no prefab it is logged only. The violation says how many copies
  it found (`InvariantViolation.Copies`), and that count picks this repair, not the repair home (which
  is Lost and Found for two copies of an item whose home is Lost and Found too). Both steps look again
  first, and make nothing when a copy is there by then: a live record, a Lost and Found entry or row,
  or an object with the id.
- `OneHome`, two copies or more: the copy in the ledger's home keeps the id (else the first live one,
  put home); each other live copy is a new item, re-minted as `Register` re-mints a copy (`Remint`: a
  new id, born `Duplicate` from the first), its owner's, into their Lost and Found. A copy already in
  Lost and Found stays there, its entry and any data row re-keyed to the new id
  (`LostAndFoundServer.RekeyItem`, `LostItemRegistry.RekeyItem`), so it is saved and found under it.
- `NoStray` of an ended entry: each live copy is re-minted the same way, its ended entry's owner's,
  into their Lost and Found; with no player owner, into the world. The ended entry stays ended.
- `NoStray` of a record whose store named an owner (`StoredOwner`): registered as that owner's, born
  `Claimed`.
- `OneItemPerSlot`, two items in one holder's slot (the violation names the slot): the one the ledger
  has there stays; another owned one goes to its own home (one whose home is this slot too, to its
  owner's Lost and Found); an unowned one drops into the world at its holder's feet (`OneInSlot`).
- `OneItemPerSlot`, two live entries claiming one owner slot (no slot named): the first registered
  (born first, then by id) keeps the claim, and each other one takes its owner's next free claim slot,
  or no claim when none is free (Core `ItemLedger.SettleClaimClash`; the user, 2026-10-25). Each moved
  claim is a `ClaimSlot` change, which tells the owner's game, as any moved claim is
  (`ServerLedger.OnLedgerChanged`, `PlayerItems.SendClaim`).
- `VanillaAgrees`: the item comes out of the vanilla lists its home has no place in, and one in none
  it may be in goes into its home's (`MirrorLists`, Core `HomeInvariant.ListsFor`): vanilla's lists
  follow the homes. The installed list is the gadget code's and is left as it is.

Not repaired, logged only: a stray or a copy that is a row, which has no object to move (the load
re-mints a copy and registers a stray); a cold row that disagrees, whose holder's join makes it again,
and the next check (at the latest before the save) puts home.

`ServerLedger.SendToLostAndFound(id, reason, detail, out refused)` is the one way into Lost and Found by
id (OI-LF-08). An item already there is accepted; else it is refused as `unknown-item`,
`item-has-no-owner` or `item-is-with-a-player` before its object is looked for, so a data-only item is
never made just to be refused.

## Saving the ledger

The ledger's rows are saved with every item's in the one key, `Multiplayer.Items`
(`ItemSaveLoad.WriteSave`, `ItemsSaveCodec`; [save-load.md](save-load.md#the-key)). The load hands
them to `ServerLedger.LoadEntries` (`ItemLedger.Restore`) before it makes any item, makes each live
entry's item by `ItemLoadPlanner`'s plan, and ends with `ServerLedger.FinishLoad`, which runs the check
([save-load.md](save-load.md#loading)). `ServerLedger.HostTick` only runs the Debug check.

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
