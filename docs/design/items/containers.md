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
4. The object retires: its Destroy goes out, then it leaves interest, the inventory and vanilla's
   storage, and is destroyed. Until the Destroy is sent the object is the item, so a failure takes
   the deposit back (`ContainerGraph.RevokeDeposit`, the ledger moved back, the object live again;
   OI-CON-06). Once it is sent the row is the item, and the object goes whatever else fails. The item
   is never an object and a row at once, so nothing half-retired is ever saved.

## Withdrawal

A withdrawal takes the item in a slot of a box its keeper has open into the keeper's inventory, as a
row ([inventory.md](inventory.md), D14): no object is made on the server. Its owner is the
ledger's (OI-CON-02): somebody else's item comes out only while they are connected, and an unowned
one comes out unowned. In order:

1. `ContainerGraph.PrepareWithdrawal` reserves the row.
2. The server picks the inventory slot (`ServerLedger.FreeSlot`).
3. The ledger moves the item from `Container(id, slot)` to that slot.
4. The row is issued into the inventory (`PlayerItems.Issue`), and `ContainerGraph.CommitWithdrawal`
   removes it from the box.

If the withdrawer has left, the item can't be made, or the graph refuses the commit, the issued row
is taken back, the ledger moves the item back into the box, and the reservation ends (OI-CON-03).

## Moving between boxes

A move from one slot to another, in one box or between a box and one nested in it, needs the keeper
of both. `ContainerGraph.CheckMove` checks it and names the item; the ledger moves it from one
`Container` home to the other; `ContainerGraph.Move` commits, and a refusal moves the ledger back.
