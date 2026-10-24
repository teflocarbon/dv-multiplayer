# Containers

> Status: being built in R5 of the [item refactor](../item-refactor.md), 2026-10-25. This page says
> what the code does, section by section as R5's PRs land. It covers cold containers: what a stored
> item is, who keeps a box, deposit, withdrawal and moves between boxes. The ledger it commits to is
> [ledger.md](ledger.md); the save rows are [save-load.md](save-load.md); who may deposit is
> [access.md](access.md); the request packets are [wire.md](wire.md). It replaces the legacy
> [container-storage.md](../../legacy/items/container-storage.md).

| Side | Code |
| --- | --- |
| Core | `ContainerGraph`, `ContainerRecord`, `StoredItemRecord`, `IContainerOwners` (`Multiplayer.Core/Items/Containers/ContainerGraph.cs`) |
| Server | `ContainerServer` (`Multiplayer/Server/Items/Containers/ContainerServer.cs`); the requests in `NetworkServer.Items.cs` (`CarryDeposit`, `CarryWithdraw`, `CarryMoveInContainer`, `ContainerList`); `ServerLedger.CommitStored` |
| Client | `ContainerUi`, `ContainerUiPatch` (`Multiplayer/Client/Items/Containers/`) |

## Stored items

An item container (anything with an `AItemContainer`, such as a toolbox) is an ordinary item: the
**shell**. Its contents are not objects on any game. They are rows in one server-side graph,
`ContainerServer.Graph`:

- **A container** (`ContainerRecord`) is its id, prefab, capacity, a revision and a session handle.
  Its id is its shell's item id (OI-CON-09): the id the item has from its birth or its saved row,
  before its state loads, so a restored shell can never mint another.
- **A stored item** (`StoredItemRecord`) is its own id, prefab, display name, authored key, its
  container and slot, and its state (`DetachedState`, the item's `ItemSaveData`). A container is
  stored in another when the graph holds an item with its id; nothing else links them.
- **Its home** in the ledger, when it is owned, is `Container(id, slot)`: the container's id and the
  exact slot. An unowned stored item has no entry; its row is its only record.

Neither row names an owner. Owners are the ledger's (`IContainerOwners`, which the server answers
from the ledger and the live records), so a re-key moves them without the graph (`InstallGuidRekey`),
and a box's contents may belong to different players.

## Who keeps a box

A box is private to its **keeper** (OI-CON-01), the one player who may browse it, deposit into it and
take from it (`ContainerGraph.KeeperOf`):

1. its shell's owner;
2. else, when the shell is stored in another box, that box's keeper;
3. else whoever holds the shell, in a hand or an inventory (OI-CON-08).

A box nobody owns, holds or keeps is shut. The server binds a live shell to its container (a row
made from its id and capacity) once it has an owner or a holder, so a box nobody has touched adds
nothing to the save.

A browse marks each slot whose item is owned by someone other than the keeper (`ForeignOwned`): a
box picked up from someone else shows their items as theirs.

## Deposit

A player deposits an item they hold and own (`ItemAccess`: not an essential or a fixture; the
graph checks ownership again from the ledger). The deposit never changes ownership. In order:

1. `ContainerGraph.CheckDeposit` checks it without changing anything: the keeper, the revision,
   the slot (in range, free), the game's own compatibility, no cycle, the size of the state, the
   per-owner quota.
2. The ledger moves the item from its hand or slot to `Container(id, slot)`
   (`ServerLedger.CommitStored`).
3. `ContainerGraph.CommitDeposit` adds the row, pending its object's retirement; if it refuses
   after all, the ledger moves the item back.
4. The object retires: its Destroy goes out, by interest (OI-CON-12): to the players who know the
   object and the depositor, whose stand-in it was, never to everyone. Then it leaves interest, the
   inventory and vanilla's storage, and is destroyed. Until the Destroy is sent the object is the
   item, so a failure takes the deposit back (`ContainerGraph.RevokeDeposit`, the ledger moved back,
   the object live again; OI-CON-06). Once it is sent the row is the item, and the object goes
   whatever else fails. The item is never an object and a row at once, so nothing half-retired is
   ever saved.

## Withdrawal

A withdrawal takes the item in a slot of a box its keeper has open into the keeper's inventory, as a
row ([inventory.md](inventory.md), D14): no object is made on the server. Its owner is the
ledger's (OI-CON-02): somebody else's item comes out only while they are connected, and an unowned
one comes out unowned. In order:

1. `ContainerGraph.PrepareWithdrawal` reserves the row.
2. `ItemAccess` is asked (`Withdraw`, with the row's facts: `ItemAccessServer.AllowsRow`, OI-ACC-04).
3. The server picks the inventory slot (`ServerLedger.FreeSlot`): the item's claim when the
   withdrawer owns it, else the lowest free one. The slot the client asked for isn't used (BR LF3).
4. The ledger moves the item from `Container(id, slot)` to that slot.
5. The row is issued into the inventory (`PlayerItems.Issue`), and `ContainerGraph.CommitWithdrawal`
   removes it from the box.

With no slot free the item still comes out, as an object at the withdrawer's feet (the ledger
moves it to `World`; `ItemSaveLoad.MakeLive`). An owned item goes on from there to its owner's
Lost and Found (`InventoryOverflow`), as an issued essential with no slot does; if Lost and Found
refuses it, it stays at their feet. An unowned item stays at their feet.

If the withdrawer has left, the item can't be made, or the graph refuses the commit, the issued row
(or the object made at their feet) is taken back, the ledger moves the item back into the box, and
the reservation ends (OI-CON-03).

## Taken out by the server

The server takes a row out of its box with no player's request when an owner retrieves their item
from a box that isn't theirs ([owned-list.md](owned-list.md#out-of-a-box), OI-LIST-23).
`ContainerServer.TakeOutLoose` does it in one step: the row must be settled (`ContainerGraph.IsSettled`:
no deposit or withdrawal of it, or of anything inside it, part way; else `container-busy`,
OI-LIST-18); the ledger moves the item from its box to `World`; the item is made an object again
(`ItemSaveLoad.MakeLive`); and `ContainerGraph.TakeOut` removes the row and moves the box's revision
on. If the item can't be made, the ledger moves it back and the row stays. A box taken out keeps its
own container and contents: its shell binds to them by its id.

## A box destroyed

A box's shell destroyed for good (by the game, a deleted car, a scenario; not put in a box,
collected, gone cold, remade or an authored slot's old object) takes its container with it (OI-CON-11; `ContainerServer.OnRemoved`,
called from `ItemChangeLog.RecordRemoval`). `ContainerGraph.RemoveContainer` removes the container
and gives back its rows:

- an **owned** item is made an object where the box was (the ledger moved to `World` first) and goes
  on to its owner's Lost and Found (`ContainerCleared`), whole if it's a box, its contents still in it
  (OI-LF-07); if Lost and Found refuses it, it stays where the box was. If it can't be made, its
  ledger entry ends: its box is gone, so it has nowhere to go back to;
- an **unowned** item goes with the box. An unowned box among them is dealt with the same way, its
  owned contents to their owners and its unowned ones gone.

Unloading the world or quitting destroys every shell without clearing any container.

## Moving between boxes

A move from one slot to another, in one box or between a box and one nested in it, needs the keeper
of both. `ContainerGraph.CheckMove` checks it and names the item; the ledger moves it from one
`Container` home to the other; `ContainerGraph.Move` commits, and a refusal moves the ledger back.

The container UI has one box open and sends that box's revision as both the source's and the
destination's. For an item moved between that box and the one directly inside it (or the one
around it) the source isn't the box the client has open, so the server checks the source by the row
the handle names and the open box by its revision (`NetworkServer.CarryMoveInContainer`, OI-CON-13).
Between boxes not nested directly, both revisions are checked as sent.

A withdrawal's item goes by interest too: a row is sent to the withdrawer alone (`PlayerItems.Issue`),
and one made loose at their feet reaches others through the world index, as any item does.

## Depth

A box may sit at most `ContainerLimits.MaximumDepth` (8) boxes deep inside the outermost one. A
deposit or a move of a box checks where it would land (`ValidateDestination`): how deep its
destination is, plus one, plus how deep the boxes inside it go. A ninth level is refused
(`container-depth-exceeded`, OI-CON-15), so no graph is made that the load, which holds a save to the
same limit (`ValidateDepthFrom`), would refuse. A box can't go inside itself or one of its own boxes
(`destination-is-descendant`).

## Before the load

No item request is carried out before the load has run (OI-PER-23): every request is refused
`items-loading` (`ItemRefusal.Busy`) at the one entry (`NetworkServer.Carry`), and a container browse
too (OI-CON-05). A request made before the load would act on items the load hasn't placed, and the
load would wipe it (F19).
