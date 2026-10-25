# Containers

> Status: built in R5 of the [item refactor](../item-refactor.md) (#367-#377, merged, 2026-10-25), not yet
> proved in a game: that is the end pass (OI-REF-01). It covers cold containers: what a stored
> item is, who keeps a box, deposit, withdrawal, moves between boxes and ammo in a magazine. The ledger it commits to is
> [ledger.md](ledger.md); the save rows are [save-load.md](save-load.md); who may deposit is
> [access.md](access.md); the request packets are [wire.md](wire.md). It replaces the legacy
> [container-storage.md](../../legacy/items/container-storage.md).

| Side | Code |
| --- | --- |
| Core | `ContainerGraph`, `ContainerRecord`, `StoredItemRecord`, `IContainerOwners` (`Multiplayer.Core/Items/Containers/ContainerGraph.cs`) |
| Server | `ContainerServer`, `MagazineServer` (`Multiplayer/Server/Items/Containers/`); the requests in `NetworkServer.Items.cs` (`CarryDeposit`, `CarryWithdraw`, `CarryMoveInContainer`, `ContainerList`); `ServerLedger.CommitStored` |
| Client | `ContainerUi`, `ColdContainerItemProxy`, `ColdProxyContainer`, `ContainerUiPatch` (`Multiplayer/Client/Items/Containers/`) |

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
nothing to the save. A deposit that goes no further takes away a container it made for the
deposited box, and the save leaves out an empty container whose item is gone (no record, row,
ledger entry or stored row has its id; `ItemSaveLoad.WriteContainers`): its shell makes it again
if it ever binds.

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
no deposit or withdrawal of it, or of anything inside it, part way; else `container-item-busy`, which
a retrieval reports as `container-busy`, `Retrieval.Busy`; OI-LIST-18); the ledger moves the item from its box to `World`; the item is made an object again
(`ItemSaveLoad.MakeLive`); and `ContainerGraph.TakeOut` removes the row and moves the box's revision
on. If the item can't be made, the ledger moves it back and the row stays. A box taken out keeps its
own container and contents: its shell binds to them by its id.

## A box destroyed

A box's shell destroyed for good (by the game, a deleted car, a scenario; not put in a box,
collected, gone cold, left interest, remade or an authored slot's old object) takes its container with it (OI-CON-11; `ContainerServer.OnRemoved`,
called from `ItemChangeLog.RecordRemoval`). `ContainerGraph.RemoveContainer` removes the container
and gives back its rows:

- an **owned** item is made an object where the box was (the ledger moved to `World` first) and goes
  on to its owner's Lost and Found (`ContainerCleared`), whole if it's a box, its contents still in it
  (OI-LF-07); if Lost and Found refuses it, it stays where the box was. If it can't be made, its
  ledger entry ends: its box is gone, so it has nowhere to go back to, and a box's own
  contents are dealt with as a destroyed box's are;
- an **unowned** item goes with the box. An unowned box among them is dealt with the same way, its
  owned contents to their owners and its unowned ones gone.

Unloading the world or quitting destroys every shell without clearing any container. However a
shell goes, its NetId stops naming its container at once (`shellByNetId`), so a NetId given to
another shell before the next scan doesn't find the old one's.

## Ammo in a magazine

A vanilla magazine (`ItemMagazine`: a boombox's cassette, a sprayer's paint can, a soldering tool's
spool) is an item container too, its ammo inactive under the world's origin parent, not under the
tool. Its ammo has a home in each of the tool's forms (CN1 and OI-CON-16; R5 C7 and C8):

- **While the tool is an object**, its ammo is an object loaded in the tool's magazine: live
  placement `Magazine`, home `Magazine(tool, slot)` (the tool's item id and the magazine's slot). On
  the wire it is `ItemState.InMagazine` with the tool's NetId and the slot (`MagazineNetId`,
  `MagazineSlot`; [wire.md](wire.md#the-update)). The game that loads it (`MagazinePatch` on
  `AddItem` and `RemoveItem`) observes it as any change: a client may send it while it holds or
  stashes the tool, and a client's load is a `LoadMagazine` request with the tool and the slot
  ([wire.md](wire.md#one-request-one-result)). The server takes it from the ammo's holder (`SenderPossesses`), checks
  the tool (not one another player holds), the slot and that the magazine takes that item
  (`LiveItems.MagazineRefusal`: `magazine-tool-unknown`, `magazine-tool-foreign`,
  `magazine-slot-invalid`, `magazine-refused-item`, `magazine-slot-taken`), within 64 m of the
  sender as a drop is (`magazine-outside-sender-bound`), and a load into a tool the server is still
  making again from data waits for it (`DataOnlyNamed`), and every other game loads its copy into its copy of the tool (`NetworkedItem.LoadInto`; an update
  that arrives before the tool waits for it ten seconds). Loaded ammo is non-spatial: a player gets
  it with its tool. Ammo in a tool another player holds or has stashed is theirs: a take of it
  into someone else's hand is a take from that player (`ItemAccess` refuses it), and moving it
  anywhere else is `magazine-tool-foreign` (H1).
- **While the tool is a row** (in a box, in a cold inventory), its ammo is a row of the tool's
  container, `Container(tool, slot)`, as a box's contents are; the tool's container id is its item id
  (OI-CON-09). `MagazineServer.OnToolRemoved` (from `ContainerServer.OnRemoved`) stows each
  loaded item when the tool's object goes into a box, cold with its leaving holder, into Lost and
  Found, remade or replaced in an authored slot: the ledger first, then the graph (`ContainerGraph.Stow`),
  then its object retires by interest, as a deposit's does. `MagazineServer.LoadFromRows` makes them
  objects in the magazine again when the tool's object is bound (the server's shell scan), whichever
  way it came back.
- **A stow that fails** as the tool's object goes lets its ammo out as a destroyed tool's ammo goes
  (`MagazineServer.Release`): an owned item to its owner's Lost and Found (`ContainerCleared`), any
  other, or one Lost and Found refuses, dropped where the tool was. Ammo of a stand-in tool that
  can't go cold with its holder is below (`AmmoLeftBehind`).
- **The tool destroyed for good** lets its ammo go as a destroyed box's contents go: an owned item to
  its owner's Lost and Found (`ContainerCleared`), any other loose where the tool was.
- **Retrieved by its owner** (owned items), loaded ammo is where its tool is: nothing to do while the
  tool is with its owner; otherwise it comes out of the tool alone into their Lost and Found
  (`Retrieval.ToolOf`), and a player who has the tool is told. Every game's copy leaves the magazine
  (the client's Lost and Found projection unloads it, `NetworkedItem.LeaveMagazine`). Out of a tool
  that is a stand-in, the tool's object is made for it and goes back to data, and its holder is sent
  the tool's row (`PlayerItems.WithObject`), so their stand-in no longer nests it.
- **Saved,** loaded ammo is a row with placement `Magazine`, its container the tool and its slot;
  unowned ammo is saved when its tool is. The load makes it after the tools (`LoadAction.IntoMagazine`,
  `ItemSaveLoad.LoadIntoMagazine`), into the live tool's magazine, or as a row of the tool's container
  when the tool is a row.

- **While the tool is a stand-in** (data in a connected player's slot; [inventory.md](inventory.md),
  OI-CON-16's second half, R5 C8), its ammo is data too: its record stays, home still
  `Magazine(tool, slot)`, its NetId reserved and its row kept by the server (`PlayerItems.Ammo`), with
  no object on any game. The stand-in carries the same rows nested in its state, under
  `mpMagazineAmmo` (`StandInAmmo`), so a stash, an inventory row and a join carry them without a
  new packet; the server splits them off a stash's state and nests them in every row it sends.
  - **Put away:** the holder's game captures the ammo into the stand-in's state and retires the ammo
    objects before the tool's (vanilla empties a destroyed magazine into the world). The server's
    `GoData` does the same with its copies (`AmmoGoesData`): each becomes a row, and anyone who knew it
    other than the tool's keepers is told it's gone. A tool whose ammo has no record in its magazine
    stays an object (`AmmoCanGoData`). The server checks what the stash nested against its own
    magazine: only this tool's ammo there takes its nested state (`SplitStashedAmmo`), and if the
    holder's stand-in nested anything else, it is sent the server's word, the tool's row as it goes
    to data, or its state with only the ammo the server has loaded if it stays an object
    (`NestingDiffers`, `CorrectNesting`). A boombox playing its cassette stands in too: its music plays
    on from its row, where the cassette had got to, and is heard by the players near its holder
    ([inventory.md](inventory.md#stashed-items-that-act), R5 C10).
  - **Taken out:** the holder's game makes the ammo with the tool from the nested rows, under their
    NetIds, loaded as the server's word (`ClientItemManager.MakeAmmo`); a refusal retires them with
    the tool. The server makes its copies as the tool's object is made (`TakeUpObject`, `TakeUpAmmo`),
    the host's own take-out in that frame. A request that names the ammo makes its tool.
  - **The holder leaves:** the ammo becomes rows of the tool's container (`AmmoGoesCold`), and at
    their next join it is data again with fresh NetIds, nested in the tool's row (`AmmoFromRows`).
    Ammo the tool's container or the graph refuses is made where its holder was, and an owned item
    goes on to its owner's Lost and Found (`AmmoLeftBehind`), as a destroyed tool's ammo does.
  - **Saved:** an ammo row as data saves as any loaded ammo, and loads into the tool's container
    (the tool is a row then).

Ammo in a scene tool stays the scene's until a player loads it.

## Job papers in a box

Vanilla's folder and registrator take papers (`ItemType.Paper`), and a box keeps a booklet, an
overview or a report as a row, as an inventory does ([inventory.md](inventory.md), OI-COLD-07):

- **Deposit:** its row's state names its job (`JobPapers.Complete`; a report carries its recipe),
  and a paper that couldn't be made again isn't taken (`deposit-paper-cannot-be-row`). Its object
  goes from every game, as every game had a copy from its job, and its job no longer keeps it by
  NetId (`JobPaperRows.Forget`).
- **Withdrawal:** it's made again from its job, a row in the withdrawer's slot that the job keeps by
  its new NetId (`JobPaperRows.Admit`), or loose at their feet with no slot free. One its job no
  longer wants (`JobPaperRows.Wanted`) goes from the box instead (`withdraw-paper-job-over`), and so
  does a booklet whose job has since printed the withdrawer another copy at a validator (the deposit
  let the job forget it): theirs is that copy (`withdraw-paper-copy-issued`, H2). The job takes the
  paper only once the withdrawal is committed, so a rollback never costs the board its overview (H5).
  Nobody owns a job paper (D5, OI-ID-12), so whoever carries one may deposit it: the access policy
  lets a paper in (`ItemAccessRequest.IsJobPaper`), and the graph takes it unowned
  (`DepositCommand.JobPaper`), still held to the box keeper's quota. An owned paper from an older
  save goes in only by its owner.
- **Its job ends:** a booklet or overview in a box goes from it (`JobPaperRows.DropStored`); a
  report outlives its job. A booklet in a box counts as carried, so the load doesn't print another
  (`JobPaperRows.HasBookletRow`).
- **Saved** with its box, as a carried paper is (`IsUnsavedJobPaper`), and back in it after a
  restart while its job is still on.

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

## The client's view

No game but the server has a stored item, as an object or a row. The client shows a box's list
([wire.md](wire.md#one-list)) in vanilla's own non-VR and VR inventory views, and `ContainerUiPatch`
turns the player's drags in them into item requests (`Multiplayer/Client/Items/Containers/ContainerUi.cs`):

- Opening a box (`ItemContainer.ToggleContainerAccess`) asks for its list by its shell's NetId
  (`ContainerUi.Open`). Each row is one slot's item: its handle, prefab, display name, slot and
  flags (`Container`, `SomeoneElses`; `NetworkServer.ContainerList`).
- `ContainerUi.Project` makes each row a hidden object (`HideFlags.HideAndDontSave`) with a
  `ColdContainerItemProxy`: an `IInventoryItemSpec` that takes its name and icons from the prefab's
  spec and keeps the row's container handle, item handle and slot. It has no `NetworkedItem` or
  `ItemBase`. The proxies fill the open box's slot model in each `ItemContainerProvider` showing it.
  A row that is a box gets a `ColdProxyContainer` too, an `AItemContainer` that takes no item
  (`ValidItem` is false) and whose `ToggleContainerAccess` opens that box's list by its handle
  (`ContainerUi.OpenChild`). A click on that row on the inventory screen would make the row's
  `ColdProxyContainer` vanilla's active container (`ToggleActiveContainerOnClick`). That is an open,
  not a close: a prefix on vanilla's setter (`ContainerUi.TryOpenRow`) opens the stored box's list in
  the view instead and skips vanilla's change, so the box shown stays active, vanilla's change event
  never meets a proxy it has no model for, and the box shown doesn't replay its open effects. Once the
  stored box's list shows, the inventory screen's title is its name (OI-CON-17).
  The proxies go when the box closes: vanilla's active container changing
  away from it (closed, or another box opened) ends its view (`ContainerUi.Closed`, from
  `ContainerUiPatch`), and a late answer to its list is ignored. Each new list, or a refused one,
  destroys the old proxies first too (`ClearProxies`).
- Every operation names a row by its handle, never a NetId: a withdrawal and a move take the
  handle of the row in the slot (`RowAt(slot).Handle`), and a proxy dragged into the open box is a
  `MoveInContainer` by its `ItemHandle` (`TryInterceptAdd`). The only item NetId sent is that of a
  real item being deposited: its object's, or an inventory row's stand-in's (`InventoryStandIn.NetId`).
  The open box itself goes by its shell's NetId when it is opened and in a deposit (`ShellNetId`),
  and by its handle otherwise.
- A deposit goes to the slot asked for when it is free in the view, else the first free one; with
  none free it isn't sent. Each result asks for the list again (`ContainerUi.OnMutation`), so the
  view is the server's.

## Depth

A box may sit at most `ContainerLimits.MaximumDepth` (8) boxes deep inside the outermost one. A
deposit or a move of a box checks where it would land (`ValidateDestination`): how deep its
destination is, plus one, plus how deep the boxes inside it go. A ninth level is refused
(`container-depth-exceeded`, OI-CON-15), so no graph is made that the load, which holds a save to the
same limit (`ValidateDepthFrom`), would refuse. A box can't go inside itself (`container-self-cycle`) or one of its own boxes
(`destination-is-descendant`; `ContainerGraph.ValidateNoCycle`).

## Before the load

No item request is carried out before the load has run (OI-PER-23): every request is refused
`items-loading` (`ItemRefusal.Busy`) at the one entry (`NetworkServer.Carry`). A container browse
is a list request, not an item request: `ContainerServer.Browse` refuses it `items-loading` too
(OI-CON-05), as `ContainerOperationStatus.NotFound`, which the list carries as
`ItemRefusal.UnknownItem` (`NetworkServer.RefusalOf`). A request made before the load would act on items the load hasn't placed, and the
load would wipe it (F19).
