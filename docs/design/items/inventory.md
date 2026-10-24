# Inventories

> Status: built in R3 of the [item refactor](../item-refactor.md), 2026-10-24; the join as rows in
> R4; rows on every game (D14) in R4C, 2026-10-25. This page says what the code does. Every
> player's inventory is the server's rows: each item in it has an exact slot and its own saved state
> on the server, and no game has an object of it while it's in its slot. The server keeps it as
> data, and its holder's game draws it from a stand-in. A client's change to it is a request; a
> leaver's inventory goes cold and comes back at join. Claims are issued by the ledger
> ([ledger.md](ledger.md), [essentials.md](essentials.md)); this page says how a client shows them.
> The record and its transitions are [live-state.md](live-state.md)'s.

| Side | Code |
| --- | --- |
| Core | `Multiplayer.Core/Items/Inventory/`: `InventorySlots`, `BeltSlots`, `SlotClaimPlanner`, `InventoryPresentation` |
| Server | `Multiplayer/Server/Items/Inventory/`: `PlayerItems` (`PlayerItems.cs`, `.DataOnly.cs`, `.Rows.cs`, `.Host.cs`, `.Alarms.cs`), `ColdInventories`, `HostInventory`, `InventoryMembershipPatch`; `Multiplayer/Server/Jobs/JobPaperRows.cs` |
| Shared | `Multiplayer/Shared/Items/Inventory/`: `InventoryStandIn`, `InventoryRowHolder`, `InventoryPutAwayPatch`, `InventoryTakeOutPatch`, `InventoryStandInPatches`, `VanillaInventorySlots`, `InventorySilhouettes`, `ItemStateText`, `StashedItems`, `AlarmRows`, `ForcedItemRows`, `TutorialRadio`, `InventoryIntegration`, `InventoryItemStatePatch`; `Multiplayer/Shared/Jobs/JobPapers.cs` |
| Client | `Multiplayer/Client/Items/Inventory/`: `InventoryRows`, `InventorySlotMovePatch`, `InventoryOwnershipUiPatch`; `Multiplayer/Client/Items/Projection/`: `ClientItemManager.StandIns.cs`, `.Rows.cs`, `.TakeOut.cs` |
| Requests | `NetworkServer.CarryMoveSlot` and `DataOnlyNamed` (`Multiplayer/Server/Session/NetworkServer.Items.cs`) |

## Slots

`InventorySlots` has Derail Valley's 36 slots (`Capacity`); the last three, 33 to 35, are the VR belt
(`FirstBeltSlot`, `IsBelt`).

- **Every inventory item has an exact slot on the server,** owned or not: `LiveItem.Slot` (OI-SLOT-01).
  An owned item's ledger home is `Slot(holder, slot)` with the same slot; an unowned one's slot is its
  record's. The update carries it (the home `Slot` on the wire, [wire.md](wire.md#the-update)), and the
  holder's game puts the item's stand-in in exactly that slot (`InventoryStandIn.Place`, through
  `InventoryIntegration.PlaceAt`).
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
and only the owner's game shows it snapped to their body. Unlike the other slots, an enabled VR belt
slot holds the item's object, as a hand does (OI-COLD-14): moving an item onto the belt takes it out
of its row ([Taking out](#taking-out)), and moving it off puts it away again. `BeltSlots` keeps a VR
release onto the belt from looking like a drop: nothing is sent in the frame the hand lets go
(`HoldVrRelease`), the echo of the player's own hold after the belt took the item is skipped
(`IsSupersededOwnHold`), and an update that puts a belt item in its holder's inventory leaves it
snapped (`KeepBeltSnap`).

A claim never takes a belt slot (`InventorySlots.ChooseClaim`, OI-ESS-03), and an essential moved onto
the belt keeps its old claim; moved to another slot off the belt, its claim follows it
([ledger.md](ledger.md)). When a client makes room for an item, the item it moves aside goes to the
last free slot off the belt, never onto it.

## Stand-ins

What a slot holds under D14 (OI-COLD-09): an `InventoryStandIn`, an empty, inactive object with no
renderer, collider, `ItemBase` or `NetworkedItem`. It carries its prefab's look (the
`IInventoryItemSpec` vanilla's inventory UI, hotbar and item icons draw from, with the item's name
from its state: a label, a job's id) and its row: the NetId the server keeps the item by, its claim,
its relation, its revision and its own saved state. Nothing in it can fall, be swept, be saved by
vanilla or be destroyed as an item. It is the cold container UI's proxy made general.

On which games: a remote client's own inventory (`ClientItemManager.StandIns.cs`), and on a player
host the host's own player's ([The host's own player](#the-hosts-own-player)). Both act through one
row holder (`InventoryRowHolder.Current`), so every hook below is the same on either. A dedicated
host has no inventory. Other players' items are never in the local inventory.

Vanilla keys its slots, its dropped and active sets, its claims and its hands by the GameObject
(`VanillaInventorySlots`). An item's object and its stand-in swap places there (`Rebind`) before
vanilla acts on either, so vanilla, its UI and every listener of its events see one consistent
object for the slot. `InventoryStandInPatches` answers every hook `InventoryDV` asks of a slot
object's `ItemBase` (essential, belt-snappable, two-handed) from the row and its prefab, and keeps
vanilla from parking, laying out or firing item events for a stand-in.

These stay objects in a slot:

- an item on an enabled VR belt slot (OI-COLD-14, above);
- a magazine that holds anything (a boombox's cassette, a sprayer's can, a soldering tool's spool),
  whose ammo has no row yet (`InventoryStandIn.HoldsAmmo`; OI-CON-16, R5);
- an item whose prefab this game doesn't know (a mod item without its mod), and a job paper whose job
  this game doesn't know ([Job papers](#job-papers)).

Every scenario run ends by checking that no slot on either game holds any other object
(`Invariant.ObjectsInSlots`).

### Rows on the client

The client keeps the server's rows for its own inventory (`InventoryRows`): one per item, by NetId,
with its update (prefab, exact slot, claim, relation, revision) and its saved state (`ItemSaveData`
JSON). The server sends whole rows, with their state, at the join and for every item it puts into the
inventory itself (`ClientboundInventoryRowsPacket`: a starting item, a grant, a withdrawal).
Every other change to a row is that item's ordinary update:

- an update that puts an item in the local inventory makes or moves its stand-in to its slot, and an
  object of it on this game goes (`ReceiveAsStandIn`); one the player is holding stays theirs unless
  the update is the server refusing a take-out;
- an update that takes it anywhere else makes its object from the row, with its state, for the update
  to place, and the stand-in goes (`LeaveStandIn`, `MakeFromRow`). A `Destroy` just ends the row.

### Putting away

The player puts an object away: a hand's item into the inventory (vanilla's unequip with add, the
hotbar's stash) or an object added straight in (`InventoryPutAwayPatch`). Before vanilla adds it,
the object and its stand-in swap places, so vanilla adds the stand-in and its events carry it. Once
the stand-in is in its slot the server is asked for the stash with the item's state for its row
(`CommitPutAway`, OI-COLD-13), and the object goes through the retirement path (no drop is reported).
On a player host the stash is committed there as the host's own change. If vanilla didn't add it the
swap is undone; a refusal comes back as the item's update and makes it again
([The optimistic client](#the-optimistic-client)).

### Taking out

The take-out is predicted (OI-COLD-10, `ClientItemManager.TakeOut.cs`, `InventoryTakeOutPatch`): the
player takes an item out of a slot (the hotbar's unstash, the UI's equip or drop, the VR backpack, a
move onto the belt), and its object is made from the row in that frame, under its NetId and with its
state, parked as vanilla parks a stashed item and swapped in for the stand-in before vanilla acts.
Vanilla then equips or drops it as it would any item, and the object's own change goes up as the
request (a take, a drop). The server confirms it, or refuses: a refusal with a correction puts the
item back in its slot as that update does, and one without is rolled back from what the take-out
kept. A player host's own take-out is the same object, made from the server's row: the one object
that is both the server's and the player's, committed by the host's observed change.

### State

An item's own saved state goes with its row (OI-COLD-13, `ItemStateText`): what vanilla's
`ItemSaveData` keeps (a lighter's fuel, duct tape's uses, a spool's ammo, a label, an alarm's time),
captured as the object is put away, carried on the stash request, kept in the server's row and the
client's, and loaded into the object made at a take-out. A job paper's state also names its job
([Job papers](#job-papers)).

## Silhouettes

An essential out of its owner's inventory keeps its claim slot, shown dropped and reserved: its
silhouette (`InventoryIntegration.EnsureDroppedClaim`, planned by `SlotClaimPlanner.EnsureDroppedClaim`).
Under D14 the silhouette is the item's stand-in, not its object, while the item is elsewhere (in the
world, in Lost and Found, held by another player): `InventorySilhouettes`. Vanilla routes an item back
to its silhouette by the GameObject (its reserved slot, the Get button, an equip), so the object takes
the slot back (`ToObject`) just before vanilla acts on it by identity, and gives it to the stand-in
again (`ToStandIn`) once vanilla leaves it there dropped and out of the player's hands. While the
local player holds the item the slot is vanilla's own shape. A silhouette lives as long as the
object would have kept it: whatever takes the object's claim away takes the stand-in.

A silhouette isn't stored: `Contains(go, false)` and a slot peek without dropped entries skip it, its
item's updates go to the item's object, and none of the stashed items' effects run for it.

An update putting the essential back in its claim slot restores the entry (`RestoreClaim`); an item
without a claim in that slot is moved aside first (`MakeRoomForClaim`).

## Claims as a client shows them

The claim is sent only to its claimant ([wire.md](wire.md#naming-a-player)). On that player's game:

- **A silhouette**, above.
- **Red.** An item someone else owns is red, online owner or not (D3, OI-ESS-12):
  `InventoryPresentationPlanner.Plan` from the item's relation (a stand-in's row has it), drawn by
  `InventoryOwnershipUiPatch` (`ForeignItemInventoryMarker`). Someone else's silhouette left in the
  local inventory is purged (`PurgeForeignDroppedClaims`, run after each inventory change by
  `InventoryItemStatePatch`).
- **The Get button** on a silhouette routes by `InventoryRecallRoute`: someone else's item is blocked,
  an item that can't be recalled is blocked, the owner's own essential sends a `Recall`
  ([live-state.md](live-state.md#recall-in-one-step)), and one in Lost and Found is collected.

## Moving between slots

Vanilla moves an item to an empty slot (`Inventory.MoveItemFromTo`) or swaps two (`Inventory.SwapItems`)
on the player's game, stand-ins as objects. `InventorySlotMovePatch` then sends one `MoveSlot` request
with the item now in the target slot (a stand-in's NetId and revision are its row's) and that slot.
Moves the mod makes itself for the server (`PlaceAt`, `MakeRoomForClaim`, inside
`BeginAuthoritativePlacement`) are already the server's and aren't sent.

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
stash, slot move, hotbar switch, take-out, drop or pick-up at once, as vanilla does it, and sends it:
the put-away's stash ([Putting away](#putting-away)), the taken-out object's own change
([Taking out](#taking-out)), the observed change of an object in the world
(`NetworkedItemClient.SendLocalObservation`) or the slot move above. It doesn't wait. The server
commits it in its own order and sends the accepted update to everyone, the requester included as its
acknowledgement.

Only a refusal rolls the client back: the server sends the requester a correction, the record's
state with the cause `HostCorrection` (`SendCanonicalRejectionCorrection`), and their game applies it,
which puts the item back in its slot (as its stand-in), hand or the world. A refused swap corrects
both items. A refused stash the server keeps in the player's hand drops at their feet, so the two
games agree.

## Data only on the server

An item in a player's inventory is data on the server, not an object (OI-COLD-06,
`PlayerItems.DataOnly.cs`): a remote player's, and since R4C C4 the host's own player's too.

- **Going to data.** When the server commits an item into a player's inventory (a stash, a slot
  move, a recall, a Lost and Found retrieval: `PlayerItems.Stored`, from each commit), it queues it,
  and `PlayerItems.Update` puts it to data that frame, or as soon as the holder's game is ready for
  items and the item isn't being made, moved or restored: its row is kept in `ColdInventories` (its
  state, slot, owner, claim), its `LiveItem` stays, and the server's object is destroyed
  (`GoData`). The holder draws its stand-in, and so does an owner whose silhouette stands for it;
  anyone else who was shown it gets a `Destroy` (`WentCold`). The NetId stays the item's
  ([live-state.md](live-state.md#data-only-records)). Container shells go to data too
  (`PlayerItems.DataOnlyShell` finds one by its container id). Items the server makes straight into
  an inventory (starting items, admin grants, a container withdrawal) are data from the start, with
  no object made (`PlayerItems.Issue`): the holder is sent the row and draws its stand-in.
- **Made again first.** Anything that names such an item has the server's object made from its row,
  under the same NetId and in its slot, before it runs (`PlayerItems.WithObject`): an item request
  naming the item, the shell it opens, the item a `MoveSlot` would swap with, or a retrieval's subject
  (`NetworkServer.DataOnlyNamed`); a shop banknote deposit (`HandleShopDeposit`); a container browse
  (`HandleItemListRequest`); a job's end, for its papers ([Job papers](#job-papers)). The players who
  had it are marked as knowing the new object, so it isn't sent to them again. Once the request is
  done it goes back to data (it is queued again, after a second's grace).
- **Not data:** a loaded magazine, whose ammo has no row yet (OI-CON-16, R5), and an item whose
  prefab isn't known. Job papers are data like any item since R4C C7 (OI-COLD-07).

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
   an ordinary one first, so the scene doesn't make it again. Job papers go cold as any item does.

The save writes every row (`ItemSaveLoad.WriteSave`), and the load makes no object for an item whose
holder isn't connected: its row goes straight into `ColdInventories` (`KeepCold`,
[save-load.md](save-load.md#loading)). A row saved from a hand is written in a free slot.

**Joining** (R4, OI-COLD-03, OI-JOIN-02). A remote player's inventory goes live as rows when their game
is ready for items (`PlayerItems.SendRows`, from the `ReadyForItems` step of the join), before
anything else of theirs is sent:

1. Their rows are read from the ledger: each entry whose home is a slot of theirs, with its cold row,
   then their other rows (unowned items they carry, a row the ledger moved to Lost and Found).
2. Each row becomes a live record kept as data only from the start (`LiveItems.RegisterDataOnly`),
   under a NetId reserved for it (`NetworkedItem.ReserveDataNetId`), in its saved slot if it's still
   free (`ServerLedger.FreeSlot`, the row's slot first), else its claim, else the lowest free one. Its
   row stays in `ColdInventories`. No object is made on the server. A job paper joins its job, or
   goes if its job no longer wants it ([Job papers](#job-papers)).
3. The joiner is sent one `ClientboundInventoryRowsPacket`: each item's `Create`, built for them, and
   its saved state. Their game keeps the rows (`InventoryRows`) and draws each as its stand-in in its
   slot ([Stand-ins](#stand-ins)); an item that stays an object there is made from its row
   (`ClientItemManager.MakeFromRow`, at the safety position under its NetId, its state loaded a frame
   later as vanilla's starting items are) and placed by its `Create`.
4. An essential of theirs that another connected player holds as data has no object for interest to
   find, so they are sent its `Create` too and kept up to date with it (OI-ESS-17).

Made as objects instead, by `GoLive` (the prefab made, its state loaded a frame later, registered
in its holder's inventory under its own id by `LiveItems.RegisterMaterializedColdItem`, sent to
everyone): a row with no free slot or whose ledger home is `LostAndFound` (a re-key clash), which
overflows: dropped at the player's feet and sent to their Lost and Found (`InventoryOverflow`), or
left there if unowned. A row whose prefab is unknown stays cold; one whose state won't load comes back
fresh in its owner's Lost and Found (D9).

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
and ledger homes, and its game is the server's (`HostInventory`, the row holder there). How its
changes reach the server:

- Its grabs, drops, stashes and hotbar switches are the server's own world already: the server's item
  manager sees them (`ServerItemManager.ProcessChanged`) and commits each as a forced change by the
  host's player (`HostLocalState`), with the slot vanilla put it in. Forced skips the revision and
  possessor checks; access, the ledger's slot rules and the commit order still apply.
- Its slot moves and swaps, recalls, collections and container requests are requests through its local
  session (`LocalPlayerSession.Items`), handled in-process by the same handlers as a client's
  (`NetworkServer.RegisterLocalItems`).
- Its items are data only too (R4C C4, D14): its slots hold stand-ins drawn from the server's own rows
  (`HostInventory.Draw`). A put-away commits the stash there and the object goes to data with the
  state it had; a take-out makes the object from its row in that frame, the one object that is both
  the server's and the player's. An object that reaches a slot any other way (a recall, a Lost and
  Found retrieval, a purchase vanilla adds directly) is put away once the server has committed it
  there (`HostInventory.Stow`, from `PlayerItems.Stored`). The load takes its cold rows up as data and
  draws their stand-ins (`PlayerItems.JoinHost`; the load joins it: it is admitted first).

A dedicated host has no player and no inventory: the server moves items about the world but never
holds one ([live-state.md](live-state.md#a-change)).

## Stashed items that act

Vanilla keeps a stashed item's object alive, disabled, and a few of them carry on in a pocket. Under
D14 a stashed item is a row with no object, so the local player's game does what they did from the
stand-ins' rows (`StashedItems`, OI-COLD-11 and OI-COLD-12), at the player's camera as vanilla's
stashed sounds are (`ItemPositionHandler`). A silhouette's item is elsewhere, so it does none of
these: its object does.

- **The HUD clock.** A clock registers itself as the player's when its object starts
  (`WorldClockController.PlayerHasClock`, read as the player enters a cab). A stand-in of an alarm
  clock the player owns counts too.
- **The alarm.** A set alarm clock counts down and rings: vanilla's `AlarmTick` runs on the row's own
  state (`alarmTime`, `alarmSet`) once a world minute (`AlarmRows`), a turn of its increment and the
  ring sound from copies of the prefab's own audio sources, and the ring unsets it. The server runs
  the same tick on its copy of the row (`PlayerItems.UpdateAlarms`, the rows of players here), so a
  clock made from the row later is the one that rang. Both work its due time out the first time
  they tick the row, as vanilla's load does, from the minute before that tick, so a time skip
  right after a stash (sleep, `/time`) rings it as a pocketed clock would. A cold row's clock is as
  its holder left it.
- **The boombox.** One left on in radio mode keeps playing its station (`rsIndex`) at its volume
  (`vol`): vanilla's own radio player on a copy of the boombox's music source, stopped as the item
  is taken out (its object plays from the same state) or goes. A stashed boombox's interference is
  paused in vanilla, so the copy has none. One with a cassette stays an object until R5 (OI-CON-16)
  and plays as vanilla's does.
- **The tutorials.** A quick tutorial's radio service (a host playing alone) that starts while the
  comms radio is stashed binds to it once it's taken out (`TutorialRadio`). The tutorial's mode
  control takes a stashed radio out first, and the modes it turns off are kept with the row as the
  radio is put away and given back to its next object. The quick tutorials' pointer at a stashed
  radio has no object to point at, so it isn't drawn.
- **Items kept near their place** (`ItemLocationForcer`). The forcer follows its item by NetId
  (`ForcedItemRows`): a stashed one is where the player's camera is, and once that is too far (or the
  player fast-travels) it is taken out and put back as vanilla does.

A stand-in is never destroyed as an item (`InventoryStandInPatches.DestroyItem` skips it). Vanilla's
`Inventory.DestroyItem` callers are money added to the wallet (never a stand-in: money is destroyed
on add), and spent duct tape, a spent soldering spool and a spent paint can, each in the hand or in a
live magazine. The other destroy side effects run on an object only, as a put-away destroys through
the mod's retirement path: a shop's restock (`ShopRestockerRetirementPatch`), a container's contents
(`BeginColdStorageRetirement`), the container id, a clock's registration. Job papers are under
"Job papers".

## Job papers

A booklet, an overview or a report in an inventory is a row like any item (D14, OI-COLD-07,
OI-COLD-15): data only on the server, and a stand-in in its holder's game, whose look is the
paper's prefab's (`InventoryStandIn.FindSpec` takes it from the game's resources). Its job keeps it
by NetId (`NetworkedJob`), and its object is made again from its job when it leaves the slot
(`JobPapers.Make`, with `BookletCreator`), not from its prefab, which has nothing printed on it.

- **The row's state names its job.** A booklet saves its own `jobId`; `JobPapers.Complete` adds an
  overview's, and a report's render recipe (its artifact), which outlives its job.
- **Saved with its holder.** A paper a player carries is saved as their item is, and is back in its
  slot after a restart. Ones on a board or lying about aren't saved, as before: the job system makes
  them again. Vanilla's reprint at the load skips a job whose booklet a player carries.
- **A row the job no longer wants goes**, at the holder's join or when the job ends. That is a booklet
  of a job that's over, or an overview of a job someone took or that expired. A connected holder's is
  made and destroyed, so their game is told.

docs/jobs.md ("Papers in inventories") has the job side.

## Tests

- `Multiplayer.Core.Tests/Items/Inventory/`: `InventorySlotsTests` (a claim's slot off the belt, the
  lowest free slot, belt last), `BeltSlotsTests`, `SlotClaimPlannerTests`, `InventoryPresentationTests`
  (red, the Get button's routes).
- `Multiplayer.Core.Tests/Items/Ledger/ItemLedgerTests.cs`: two items never share a slot, a swap in
  one commit with the claims following, a stale or cross-inventory swap refused, a claim never on the
  belt, the re-key merge (`Rekey_OntoAnIdentityWithData_MergesIt_ASlotClashGoesToLostAndFound`).
- Scenarios ([testing.md](../../testing.md)), most also `WHO=host` for the host's own player:
  `inventory-slots`: stash, move, swap, the belt, a reserved slot refused, the same slots after a
  rejoin; `stash-state`: an item's state through a stash and a take-out; `hotbar-cycle`: every vanilla
  item out of the hotbar and back, a stand-in in its slot, the take-out within a frame;
  `takeout-refused`; `stashed-items`: the clock, the alarm and the boombox in a pocket; `join-rows`;
  `cold-homes`: data on the server, a move while data, a refused move rolled back, cold rows and D5
  across a rejoin; `item-bug-essential-slot-rejoin`; `item-bug-booklet-restart`: a carried booklet
  back in its slot after a restart; `rekey MERGE=true`; `borrowed-disconnect`; `shop` (the banknote
  deposit); `starting-items`; `owned-list` and `owned-retrieve` (an item in an inventory). Every run
  ends with the one-home check and with no object in a slot where its row should be
  (`Invariant.ObjectsInSlots`).
