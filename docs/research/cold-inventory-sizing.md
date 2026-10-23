# Sizing D14: cold inventories on the client

> Status: a read-only sizing, 2026-10-24, for decision D14 of the [item refactor](../design/item-refactor.md)
> (rows OI-COLD-09 to -11 in the [checklist](../item-overhaul-checklist.md)). Nothing was built or run
> in a game. It was read against `origin/items/refactor` at 5afef89d (R3a #342, R3b #343, the D14 doc
> #345) and R3c's docs on `items/refactor-r3c` (#344, not merged), with the game decompiled
> (`DV.Inventory`, `Assembly-CSharp`, `DV.UI`, `DV.Interaction`, `DV.Common`). Line numbers in vanilla
> files are of the `ilspycmd -p` output.

D14: a player's inventory holds no game objects on any game, their own included. The client draws
its inventory, hotbar, icons and essential outlines from the server's rows (item, exact slot, state),
as the cold container UI does for a box. Taking an item out (to the hand, a drop, a use) makes its
object at once from its row, predicted, and the server confirms or rolls it back; putting it away
destroys the object and it is a row again. Only items in hands and in the world are objects.

## The answer

- **It can be built, and vanilla's UI can stay.** Vanilla's inventory, UI and hotbar are written
  against a GameObject in each slot and the `IInventoryItemSpec` on it, but `IInventoryItemSpec` is
  an interface. Each slot can hold a **stand-in**, as `ColdContainerItemProxy` does for a cold box
  today: a bare GameObject with no renderer, collider, rigidbody, `ItemBase` or `NetworkedItem`,
  carrying only the prefab's spec and its row's id. Vanilla's own slot bookkeeping (locked, reserved,
  dropped, the ghost icon, hotbar selection, its events) keeps working on it. The real object is made
  at the moment an item leaves the slot and destroyed when it comes back. Replacing vanilla's
  inventory and UI outright instead means rewriting about 4,000 lines of object-based UI and hand code
  (`InventoryUIController` alone is 1,899), so it isn't recommended. **The user should confirm a
  stand-in counts as "no game objects"** (question 1 below): it is a GameObject, but not an item.
  Nothing in it can fall, be swept, be simulated, be saved by vanilla or be destroyed as an item.
- **Size:** eight stacked PRs, roughly 4,500 changed lines with the scenarios. That is about 1.5 to 2
  times R3a and R3b together. The largest and riskiest piece is the predicted take-out (PR C3).
- **Where:** its own stage, **R4C**, right after R4 and before R5 and R6. Two pieces belong in R4,
  so that R4's join isn't built twice: the owner's rows (with their state and NetId) sent to the
  joiner, as OI-COLD-03, and vanilla's mirror (D2) written from rows. See
  [Where it fits](#where-it-fits).
- **Five decisions for the user** are in [Questions](#questions): stand-ins, the VR belt, items
  that act while stashed, job papers, and building OI-MOD-02's state-on-the-wire now.

## 1. Vanilla's inventory: what expects a real object

The full catalogue, file and line for every entry, is in the shared notes
(`cold-inventory-sizing-vanilla-catalogue.md`). In short:

**The model** (`DV.Inventory`, `Inventory.cs`). There are 36 `InventoryItemData` slots: each is a
GameObject plus Locked, Reserved and Dropped flags, with two hands (`EquippedItems`). It is already
row-shaped, but every set and map is keyed by the GameObject, and the public API takes and returns
GameObjects (`PeekItemAtSlot`, `Contains`, `IndexOf`, `GetItemByName` on `go.name`).

- A stashed item is parked: layer `Inventory`, unparented, kinematic, `SetActive(false)`
  (`FinalizeRemoveItemFromWorld` 550). In VR this waits until the end of the frame.
- **A held item keeps its slot.** `EquipItem` (782) marks the slot dropped and reserved, still
  pointing at the held object. Unequip puts it back, or clears the slot unless it is locked or
  essential.
- **An essential's silhouette is the world object itself.** It sits in its slot as a dropped,
  reserved entry, and vanilla's Get button (`InventoryUIController` 1591) pulls that world object
  back into the slot.
- **VR belt slots 33 to 35 stay active:** the item is snapped, visible and grabbable on the
  player's body (`ResolveItemStateOnAdd` 1075, `ItemBeltVR`, `ItemSnapPointInteractionBelt`).
- `InventoryDV` answers every hook (`IsValidItem`, `IsEssential`, `IsPlaceableInBelt`, `IsTwoHanded`,
  `IsGrabbed`, `ForceEndInteraction`, layers) through `GetComponent<ItemBase>()`.

| Area | What it does with the object | Under D14 |
| --- | --- | --- |
| **Drawing** (`HotbarController` 216, `InventoryUIController` 311, 326, 463, 547) | `PeekItemAtSlot(i)?.GetComponent<IInventoryItemSpec>()`. Icons and the preview prefab are per prefab. Names are per instance: `LocalizedName` has the job id on booklets and overviews, the fee id, the cassette number, and the label maker's `NameOverride` | Stand-in spec from the prefab list (`Globals.G.Items.items`), with the row's name fields. `InventoryHotbarProvider.GetLocalizedNameForItem` (140) casts to the concrete `InventoryItemSpec`, so it is patched |
| **Essentials and the ghost** | `IsItemGetter = spec.IsEssential`, `IsGhost = dropped` (`InventorySlotDisplayData`) | Flags on the stand-in. The Get button sends a recall (`InventoryOwnershipUiPatch` already routes it) instead of pulling a world object |
| **UI identity** (`InventoryUIController` 225-336, 1440-1460) | Hand and ghost slots are linked by spec reference; actions go through `spec.GetGameObject()` (equip `AInventoryProvider` 101, drag and drop 600-645 and 819-1084, the belt check) | One stable stand-in per row. Every `GetGameObject()` that leads to an action becomes a take-out hook |
| **Hotbar switch** (`GrabberStashingHandler.UnStash` 226, `Stash` 217) | Peek, `EquipItem`, `RequestForceHold`, all in one frame. The stash plays a 0.07 s animation, then unequips. VR does the same in `InventoryViewVR.OnInventoryStatusChanged` (519-548) with `ForceGrabInteractable` | The object is made synchronously, in the frame of the switch ([hard cases](#instant-hotbar-switch)) |
| **Drop, move, swap, lock** (`Inventory` 461, 613, 683, 727) | Pure slot data, except belt moves (activate or deactivate) and drops (`ReturnItemToWorld`). `StorageController.OnInventoryStatusChanged` (375) moves the `ItemBase` between storage lists; `ItemReparentingBase` reparents on drop | Moves and swaps are row moves with no object. A drop is a take-out to the world |
| **Containers** (`InventorySlotDisplayData` 41, `InventorySlotVisualController` 176-226) | `GetGameObject().GetComponent<AItemContainer>()`; the tooltip count and child icons come from child objects. `ItemMagazine` is an `ItemContainer` (soldering spool, sprayer's can, boombox cassette) | Rows with the container's handle (`ColdProxyContainer` already opens a child by handle) |
| **Money** | `PlayerMoney` is a double; cash (`Money`) is destroyed on add. Every spender reads `PlayerMoney` | Unchanged |
| **Consumables** (duct tape uses, spool `AMMO`, `PaintCan.isSpent`) | State in the save JObject, used only in hand | Row state, fine |
| **Job papers** (`JobBooklet` 26-157, `JobSaveManager` 63 and 458-470, `JobValidator.SummonAllActiveJobBooklets` 152-200, `StationController.ClearAvailableJobOverviewGOs` 254-281) | A static list of every booklet; job-completed and abandoned subscriptions (abandon destroys a stashed booklet); load matches booklets by job id; the validator summons every booklet, stashed ones too; overviews in the inventory are kept by reference | Rows by job id; see [Job papers](#job-papers) |
| **Gameplay reads while stashed** | The comms radio tutorial units turn modes on a possibly stashed radio (`CommsModeControlUnit` 49); `WorldClockController.PlayerHasClock` counts the player's clock objects (the HUD time); `ItemLocationForcer` checks a held item's distance every second; about 20 tutorial and Bolt units output inventory GameObjects; gadgets, paint, soldering, magazines and the label maker act only in hand | Patched to read rows (the clock, the tutorial units), or hand-only and fine |
| **Runs while stashed** | `InventoryItemEventsPropagator`'s subscribers (harmless with no object). **The alarm clock rings** (`AnalogAlarmClock` 64, `unsubscribeOnDisable = false`). **The boombox keeps playing** (its players are on a `noDisable` object). Battery, flicker and respawn coroutines stop, so state freezes, which a row matches | Needs a decision ([items that act while stashed](#items-that-act-while-stashed)) |
| **Destroy side effects** | `ShopRestocker.OnDestroy` restocks the shop; `ItemContainer.OnDestroy` clears its contents (to Lost and Found or the world); the container id is unregistered; `JobBooklet` and the clock unregister; `InventoryItemDestructionNotifier` clears the row | A put-away destroys through the mod's retirement path, which already handles the restock (`ShopRestockerRetirementPatch`) and a container shell (`BeginColdStorageRetirement`) |
| **Save and load** (`StorageSerializer.SaveStorage` 16-101, `StartingItemsController` 62-117, 445, 630) | Built from GameObjects: per item its prefab, `BelongsToPlayer`, `ItemSaveData` JObject, slot, locked, dropped, grabbed. Load does Instantiate, waits a frame, then LoadItemData, PostLoad, add with flags, PostContainerLoad | **The save format is already a row**; only its source and load are objects. In multiplayer the mod's save (R4) owns it |

Things that work in D14's favour:
- Icons, names, flags, item type and prefab are all per prefab.
- Money is already data.
- Tool and consumable state is already in the JObject, and `ItemSaveData` keeps the loaded JObject
  whole, so a row round-trips exactly.
- Every gadget, paint and soldering use happens in the hand.

## 2. Our code: what reads a player's inventory objects

The full catalogue is in the shared notes (`cold-inventory-sizing-mod-catalogue.md`). There are about 170 call
sites in `Multiplayer/` and `Multiplayer.Core/`, and about 110 in the scenarios.

**How the client's changes work today.** The mod predicts nothing itself: vanilla changes the object,
and the mod reports what it sees.

- `InventoryItemStatePatch` (postfixes on Equip, Unequip, Drop and `OnInventoryStatusChanged`)
  queues each changed item.
- `NetworkedItem.GetSnapshot` and `GetItemState` call `InInventory` when `Inventory.Contains`.
  `CaptureLocalInventoryClaim` reads the slot and the claim flags from vanilla's slot.
- `NetworkedItemClient.SendLocalObservation` turns the difference into a request
  (`ItemRequestTranslation.FromObservation`). Slot moves come from `InventorySlotMovePatch`.
- A refusal is a correction (`SendCanonicalRejectionCorrection`) that puts the existing object back
  (`HandleInventoryOrHandState`). The result packet is only traced; the cold container UI and the
  career screens are its only listeners.

**The groups and what each becomes:**

| Group | Sites | Under D14 |
| --- | --- | --- |
| Detection: `InventoryItemStatePatch`, `GetItemState`, `CaptureLocalInventoryClaim`, echo skips, `IsLocallyPossessed` | ~30 | **Replaced** by explicit hooks: stash, take-out, move, each a predicted request |
| `InventoryIntegration` | ~14 | `PlaceAt`, `AddActive`, `RestoreClaim`, `MakeRoomForClaim`, `EnsureDroppedClaim`, `PurgeForeignDroppedClaims` go; `TrySnapshotSlot` and `Project` read rows; `RevokeMembership` keeps its hand part |
| Client patches: `InventorySlotMovePatch`, `InventoryOwnershipUiPatch` | 4 | The move sends a row move. Get recalls by row; red styling goes on the stand-in |
| Core: `InventorySlots`, `InventoryPresentation`, `SlotClaimPlanner`, `BeltSlots` | 4 files | The first two stay. `SlotClaimPlanner` goes. `BeltSlots` depends on question 2 |
| Client projection, the Lost and Found silhouette restore (`NetworkedItemClient` 281-333), `ItemExchange.CollectLost`, `ContainerUi`/`ContainerUiPatch` (inventory side) | ~12 | A Create for the local player's own inventory item becomes a row. Deposit and withdrawal are row moves |
| Server `PlayerItems` (`Leave`, `GoCold`, `Join`, `GoLive`; `DataOnly`: `Update`, `ShouldBeData`, `GoData`, `WithObject`) | ~22 | `ColdInventories` holds every inventory's rows, online or not. The settle-then-destroy scan goes. `WithObject` is needed only for a take-out |
| Server requests: `CarryMoveSlot`, `TrySwapSlots`, `TryRecall`, `TryRetrieveFromLostAndFound` | ~18 | Row changes, with no `CreateUpdateData` from an object |
| Server-made inventory items: `StartingItemsIssuer.Make`, `AdminItemGrants`, container withdraw and deposit | ~10 | Each issues a row, not make, send a Create, destroy a second later |
| Save and load (`ItemSaveLoad.Save` 52-99, the D2 mirror, `ServerPlayer.Inventory`) | ~14 | Inventory rows straight from `ColdInventories`; the mirror written from rows |
| Job papers (`NetworkedJob` 98-139 and 314-336, `JobBookletSummonCoordinator`, `JobBookletRecoveryPatch`) | ~10 | See [Job papers](#job-papers) |
| Money (wallet calls) | ~30 | Unchanged; a banknote deposit takes a row |
| Invariant (`HomeInvariant` 153-159), debug (`DebugDesyncDetector`, `DebugInventoryObserver`) | ~10 | A new rule: no item object in any game's inventory slot except a hand's |
| Scenarios: `ItemSteps.Stash`, `Ledger.LocalSlot`, `OwnedItems`, `World`, `Items.Destroy`; scenarios calling `MoveItemFromTo`, `SwapItems`, `PeekItemAtSlot` (heaviest: `ItemBugEssentialSlotRejoin` 14, `ItemBugDroppedEssentialRejoin` 12, `InventorySlots` 12) | ~110 in ~27 files | A library API for inventory rows (read a slot, move, stash, take out), then each scenario ported |

One possible bug, inferred from the code and not seen in a game: an owner who joins late may get no
silhouette for an essential another connected player holds as data only, because interest scans
objects. Worth a check on its own.

## 3. What the cold containers give

**Reused as it is:**
- `ColdContainerItemProxy` is the stand-in. The inventory one is the same class made general: the
  prefab's spec, the row's handle, slot and flags. `ColdProxyContainer` already opens a child
  container by its handle.
- The spec lookup (`Globals.G.Items.items` by prefab name) is the same. Mod items need OI-MOD-01's
  `Resources.Load` fallback.
- `ContainerUiPatch` already intercepts the UI's adds, removes and moves before vanilla touches the
  object. With a stand-in on the inventory side, a deposit becomes a row move from inventory to
  container, and a withdrawal into a slot makes no object at all.
- On the server, D14 reuses:
  - `ColdInventories` rows, and `PlayerItems.WithObject` and `Materialize` (the server's object made
    from a row under the same NetId);
  - `HandOverToData`/`TakeUpFromData`;
  - the retirement path (`BeginColdStorageRetirement`, `ShopRestockerRetirementPatch`), which
    destroys an object without vanilla's side effects;
  - `GoLive`'s make-from-row sequence: Instantiate, `LoadItemData`, `PostLoadItemData`.

**Not reusable:**
- **The cold container UI isn't optimistic.** It sends a request, waits for the result, and reloads
  the whole view each time (`ContainerUi.OnMutation`). That's fine for a box but not for the hotbar:
  D14 needs prediction, a table of pending take-outs and put-aways by request id, and a rollback that
  can make or destroy an object, not only move one.
- **The list packet carries no state or NetId** (`ItemRow`: handle, prefab, name, where, flags, slot,
  revision). A row the client makes an object from needs the item's `ItemSaveData` JObject and the
  NetId the server keeps for it.

## 4. Hard cases

### Instant hotbar switch

`UnStash` equips and force-holds the slot's object in one frame, so the object has to exist in that
frame: made synchronously from the row's prefab, with its state loaded, before vanilla's
`EquipItem`. Two concerns:

- **State in the first frame.** Vanilla and `GoLive` both wait one frame between Instantiate and
  `LoadItemData`. Some setup is delayed by nature: `ItemDisabler` is built after a frame and the end
  of the frame (`ItemBase` 276); some components start in `Start` (alarm clock, wallet, job
  overview); booklet textures render asynchronously.
  - The 0.07 s stash animation covers a few frames. A safe order is: make, load the state, hold it
    hidden, then let the animation reveal it.
  - Whether `LoadItemData` in the same frame as Instantiate is right for every prefab is
    **unverified**. A probe that cycles every vanilla prefab through take-out and compares its saved
    state would settle it (scenario `hotbar-cycle`).
- **A hitch.** Instantiating a heavy prefab costs a frame spike that vanilla doesn't have, since its
  objects already exist. Measure with `dvmp_perf`. Pre-making the hotbar's objects would break D14,
  so it isn't an option.

The take-out uses the NetId the server keeps for the row (R3b keeps the `LiveItem` and NetId while
an item is data). The client makes the object under it and sends a Take; the server makes its own
copy (`WithObject`), because a hand is a live home others see. A refusal destroys the predicted
object and puts the stand-in back.

### An item's own state

The row carries the `ItemSaveData` JObject: a lighter's fuel, duct tape's uses, a spool's ammo, a
label. Two directions:

- **Take-out:** the client loads the state from its own row, so the rows sent to the owner need
  their state. That's a new message, or a state field on the inventory row.
- **Put-away:** the client destroys its object, so the state has to be captured on that game. The
  recommendation is that the stash request carries the client's JObject, which the server keeps as
  the row's state. Today the server captures it from its own copy (`GoData`), which only tracked
  values keep in step; whether those cover every saved key is **unverified**.

This is exactly OI-MOD-02's wire (an item's `ItemSaveData` goes to clients, and a client's goes up
with the request), which D11 paused until after the refactor. D14 builds it for vanilla items
(question 5).

### Items from other mods

- **custom_item_mod** (D11, paused): a stand-in needs the item's prefab on the client for its icon,
  and a take-out needs it to make the object. Both wait on OI-MOD-01's `Resources.Load` fallback.
  Until then a mod item in an inventory is a row no client can draw. It is shown as an unknown
  stand-in that can't be taken out, never dropped (as D9's `Unloadable`). Its state is in the JObject
  and round-trips.
- **Other mods that look in the inventory** for their own components see stand-ins and fail quietly.
  None is in the test install today; this goes on the compatibility list for the later D11 pass.
- **CCL** adds cars, not inventory items. The mod's CCL compatibility (`CustomCarLoaderPorts`,
  `CustomCarLoaderRopes`) is about cars, so D14 doesn't touch it. Not checked for CCL-defined items,
  if any exist.

### Items that act while stashed

- **The HUD clock:** `WorldClockController.PlayerHasClock` counts the player's clock objects
  anywhere, so the HUD time vanishes while the only clock is a row. A patch that also counts rows is
  required.
- **The alarm clock rings in the inventory** today, and **the boombox keeps playing**. As rows, both
  stop. To keep them, either they stay live objects in the inventory (an exception to D14), or a
  small row-side ringer sounds the alarm at its set time (the boombox has no equivalent).
  Question 3.
- `ItemLocationForcer` and the comms radio tutorial units hold or change a stashed object. They are
  tutorial and edge paths, patched to the row or to a take-out.

### Job papers

The job system keeps a booklet by its object: `JobBooklet`'s static list, its job subscriptions,
the validator's summon of every booklet, the load matching by job id, `StationController`'s overview
list, and on the server `NetworkedJob.TryGetIssuedJobBooklet` and its `GetItemsArray` scan.

- R3 keeps papers as objects on the server, and OI-COLD-07 (R6) makes them data there, made again by
  `BookletCreator` when needed.
- D14 is the same work on the client. Doing both in one PR, in this stage, avoids rewriting booklet
  tracking twice. Question 4.

### The host's own player (player-hosted)

Today its inventory objects are the server's only objects for its items. Its changes are committed
from observation (`ServerItemManager.ProcessChanged`, `InventoryMembershipPatch`), with no request
and no correction. Under D14:

- It holds rows like a client and draws stand-ins.
- A take-out makes the one object that is both the server's and the player's. The commit is in
  process and synchronous, so it can be checked before acting, and nothing needs predicting.
- Vanilla paths that add an object to the host's inventory directly have to be caught and turned into
  rows: `JobBookletRecoveryPatch` printing booklets on load, a shop purchase into the inventory, and
  starting items.
- The D2 mirror (`Storage_Inventory`, so the career opens in single player with the host's things) is
  written from rows. Vanilla's `StorageSerializer` would otherwise find nothing.

### A dedicated host

There is no local player, and since R3b the server already keeps remote inventories as data. What
changes is that the make-then-destroy churn stops (starting items, withdrawals, the join), and a
request on a data-only item makes no object unless it's a take-out. It is the simplest case, but it
is tested like the others.

### Late joiners

The join sends the joiner their rows, with state and NetIds, and the client draws them before
anything else. Today each cold row is made as a server object, sent as a Create, and destroyed a
second later. No one else is sent another player's inventory, which is unchanged.

### VR

- **The belt (33 to 35)** holds visible, snapped objects on the body that a hand grabs directly.
  Either the belt is a live home like the hands (its items stay objects), or it shows stand-ins with
  the prefab's preview mesh that become real on grab. The first is much simpler. Question 2.
- **The VR backpack** (`InventoryViewVR`) force-grabs in the same frame, like `UnStash`, so it gets
  the same synchronous take-out.
- dvmp drives desktop games, so VR is proved only partly in scenarios (by calling the same methods),
  and needs a playtest.

### Containers and magazines in an inventory

A toolbox in a slot is a row. Its stand-in opens its contents by handle, as a nested cold box does
today. A magazine's child (the soldering tool's spool, the sprayer's can, the boombox's cassette) is
a cold container row, so a take-out of the tool must also make its child live, or the tool won't
work. How magazines behave under cold containers today is **unverified**: the mod's code never names
`ItemMagazine`. Check it in PR C3. It may be an R5 question.

## 5. The plan

Stage **R4C, cold inventories on the client**, stacked into `items/refactor`. Sizes are rough
estimates of changed lines.

| PR | What | Size | Closes |
| --- | --- | --- | --- |
| **C1** Rows to the owner | A per-owner inventory-rows message (NetId, id, prefab, slot, claim, flags, relation, revision, `ItemSaveData` state), sent at join and on every change; the client's row store. `ColdInventories` holds every inventory, online or not. Protocol fixtures. **Its server half is built in R4 as OI-COLD-03** | M, ~400 | part of OI-COLD-03 (in R4), OI-COLD-09 |
| **C2** Stand-ins and put-away | `InventoryStandIn` (from `ColdContainerItemProxy`); `InventoryDV`'s hooks answer for stand-ins; the hotbar, inventory, silhouettes, red and Get drawn from rows; a put-away destroys the object through the retirement path and its request carries the state; detection (`InventoryItemStatePatch` and the rest) replaced by explicit hooks | L, ~700 | OI-COLD-09, part of -10 and -11 |
| **C3** Predicted take-out | Hooks on `UnStash`, `RequestEquipItem`, the VR backpack grab, drop from the UI and container moves; the synchronous make from a row under its NetId; a pending-request table on `ItemResult`; a rollback that destroys or makes; the magazine child | L, ~800 | OI-COLD-10 |
| **C4** The host's own player | A row holder in process; catching vanilla's direct adds (booklet recovery, purchases, starting items); `ProcessChanged`/`InventoryMembershipPatch` for inventory items gone; the D2 mirror from rows (built in R4) | M, ~500 | part of OI-COLD-11 |
| **C5** Server-made items as rows | Starting items, admin grants, withdrawal, Lost and Found retrieval and recall into an inventory issue a row; `PlayerItems.DataOnly`'s scan goes | M, ~400 (removes about as much) | part of OI-COLD-11 |
| **C6** Vanilla's side effects | The HUD clock from rows; the alarm and boombox per question 3; the tutorial units, `ItemLocationForcer`, `StationController`'s overview list; an audit of each destroy side effect | S-M, ~300 | part of OI-COLD-11 |
| **C7** Job papers | Booklets and reports as rows on the client and, with OI-COLD-07 if it moves here, on the server: `TryGetIssuedJobBooklet` and the summon from rows, the load matching | M, ~400 | part of OI-COLD-11 (+ OI-COLD-07) |
| **C8** Scenarios and docs | The library's inventory API and the ~27 scenario files ported (spread over C2-C7, each PR porting what it breaks); the invariant's new rule; `docs/design/items/inventory.md` rewritten for D14 | M, ~1,000 | proof for OI-COLD-09 to -11 |

**Risks, highest first:**
1. **The synchronous make.** State in the first frame and the instantiate hitch (C3).
2. **State fidelity.** The client's JObject at put-away against the server's tracked copy (C2).
3. **A vanilla path that adds an object straight into an inventory.** Unhooked, a real object slips
   into a slot. The invariant's new rule finds it in every scenario run (C2, C4).
4. **VR,** which dvmp can only partly drive.
5. **Mods that look in the inventory.**
6. **The size of the scenario port,** and two detection paths living side by side mid-stack. The
   stack should merge into `items/refactor` as a whole, after its game pass.

**Scenarios that prove it,** on a player host and a dedicated host (`PAIRS="p:2 d:1"`), with
`WHO=host` where it applies:
- **Existing:** `inventory-slots` and `inventory-slots RESTART=1`, `cold-homes`,
  `item-bug-essential-slot-rejoin`, `item-bug-dropped-essential-rejoin`, `full-inventory-essential`,
  `recall`, `starting-items`, `containers`, `booklet-summon`, `booklet-summon-host`, `jobs`,
  `shop-basket`, `shop-cash`, `rejoin`.
- **New:**
  - `hotbar-cycle`: every vanilla prefab out and back, its saved state equal, frame time within a
    bound.
  - `takeout-refused`: the server refuses a take-out; the object is gone and the stand-in is back in
    its slot.
  - `stash-state`: a lighter, a paint can, duct tape and a spool through stash, take-out and rejoin.
  - `join-rows`: a late joiner draws their inventory with no object made, and no Create for their
    own items.
  - `hud-clock`: a stashed clock still shows the HUD time.
- The invariant's new rule after every run.

## Where it fits

- **R4 hasn't started** (no `items/refactor-r4` branch today), so its scope can still take the shared
  pieces.
- **Two pieces belong in R4.** R4 closes OI-COLD-03: a joiner's cold inventory made in its exact
  slots before their inventory shows. Built as today (the server makes objects and sends Creates),
  D14 would replace that join a few days later. Built as C1's server half, it lasts: the joiner is
  sent their rows (with state and NetId), and until R4C the client makes the objects from them in
  their slots, a make-from-row that C3 reuses for take-out. Likewise R4's D2 mirror is written from
  rows, not by vanilla's serializer from the host player's objects. **R4 grows by about 600 lines**
  (C1's server half, the client's make-from-row, the mirror from rows). That's a change to R4's
  scope, so it's the user's call.
- **R4C after R4, before R5 and R6.**
  - R5 (containers on the ledger) rewrites the withdrawal and deposit that C5 turns into row moves.
  - R6 owns job papers (OI-COLD-07) and every vanilla path into an inventory, which C4, C6 and C7
    touch.
  - Building R4C first means R5 and R6 build on rows, not on objects they'd change twice.
  - R7 (leases, physics) touches little of this and can run beside it.
- **Can it be a stage of its own?** Yes, apart from the two R4 pieces. Nothing in R4C has to be in
  R3 or R4 to work, and the five decisions below don't block R4.

## Questions

1. **Stand-ins.** Are inventory stand-ins acceptable as "no game objects"? Each is an empty
   GameObject carrying only the prefab's spec and its row's id, the same as the cold container UI
   uses. The alternative is replacing vanilla's inventory and UI, about twice the work and much more
   risk.
2. **The VR belt.** A live home like the hands (objects, simple), or stand-ins with preview meshes
   that become real on grab?
3. **Items that act while stashed.** Should the alarm clock still ring and the boombox still play in
   an inventory? As rows they stop. Keeping them means an exception to D14 for those two, or a
   row-side ringer for the alarm. (The HUD clock is fixed either way.)
4. **Job papers.** Move OI-COLD-07 from R6 into R4C, so booklet tracking is rewritten once for the
   client and server together?
5. **OI-MOD-02's wire.** D14 needs each item's `ItemSaveData` state on the wire both ways, which is
   OI-MOD-02's design. Build it in R4C for vanilla items (mod items still wait for OI-MOD-01, D11)?
6. **Where.** C1's server half and the D2 mirror from rows in R4, and the rest as stage R4C after R4,
   before R5 and R6?

## Unverified

- `LoadItemData` in the same frame as Instantiate for every prefab.
- Whether tracked values keep the server's copy of a hand item's every saved key.
- Magazines under cold containers today.
- The late-join silhouette for an essential someone else holds as data.
- CCL-defined items, if any.
