# Item behaviours

> Status: written in R8 of the [item refactor](../item-refactor.md), 2026-10-25. This page says what
> the code does. It covers each item type's own sync: the values an item type registers, who sends
> them, and the item-specific patches. Where an item is, who may hold it and how a change is
> committed are [live-state.md](live-state.md) and [physics.md](physics.md); what is saved is
> [save-load.md](save-load.md).

| Side | Code |
| --- | --- |
| Core | `Multiplayer.Core/Items/Behaviours/`: `TrackedStateComposer` (`TrackedStateEntry`, `TrackedStateMergePlan`), `PageBookSyncPlanner`, `GrabberRecovery.cs` (`GrabberRecoveryPlanner`) |
| Shared | `Multiplayer/Shared/Items/Behaviours/`: `TrackedValue`, `FlashlightPatch.cs`, `LanternPatch`, `LighterPatch`, `ShovelPatch`, `MoneyAmountPatch`, `BoomboxLiveState.cs`, `PageBookPatch`, `NetworkedPageBookState`, `MagazinePatch`, `ItemInteractionPatch.cs`, `GrabberRecoveryPatch`; the item's side in `Multiplayer/Shared/Items/LiveState/NetworkedItem.Snapshots.cs` and `ItemBasePatch.cs` |
| Client | `Multiplayer/Client/Items/Behaviours/`: `MoneyPatch`, `UseUpRequestPatch`, `DevConsoleRefusalPatch` |
| Server | `Multiplayer/Server/Items/Behaviours/`: `MoneyDepositPatch`, `DevConsolePatch` |

## Tracked values

An item type's patch gives the item its `NetworkedItem` and registers the values it syncs
(`NetworkedItem.RegisterTrackedValue`): a key, a getter, a setter, an optional comparer that says
when the value has changed enough to send (the default: not equal), and whether the value is
**server-authoritative**. Every game registers the same values, since every patch runs in every
game.

- **Registration ends** with `FinaliseTrackedValues`. An update that arrives before then waits in
  the item's queue and is drained when registration ends (`DeferredSnapshotDrainPlanner`: applied
  whole, its tracked state only, or skipped if a newer revision has come). An item whose patch
  never finalises (a plain item with no patch, a page book's adapter) is finalised by
  `NetworkedItemManager` 0.75 s after it was scheduled (`TrackedValueFinalizationGraceSeconds`,
  `item.tracked-values-finalized` with reason `manager-grace-expired`). A value registered after
  that is still added, and logged `item.tracked-value-registered-late`.
- **Sending.** A value is dirty when its comparer says so against the last value sent or applied.
  A `Create` or `FullSync` carries every value, an `ObjectState` change only the dirty ones, in
  `ItemUpdate.States` ([wire.md](wire.md#the-update)). A client never sends a server-authoritative
  value (`TrackedStateComposer.Compose`); its values go up in its request's states (`SetState`, or
  with a `Take`, `Stash`, `Drop` and the rest, [wire.md](wire.md#one-request-one-result)). Only the
  holder sends object state, or a player working the item where it lies, and anyone may turn the
  pages of a book lying in the world ([physics.md](physics.md#throws-and-release),
  `ItemObjectStateAuthority`).
- **Receiving** (`TrackedStateComposer.PlanMerge`): a key the item doesn't have is logged and
  skipped; the server refuses a client's server-authoritative value (logged); the rest go through
  their setters. Applying a value makes it the last one sent, so it isn't echoed back.
- **The server's copy of an item another player holds** reports only its server-authoritative
  values ([live-state.md](live-state.md#networkeditem-on-each-game)).
- **Tracked values are live sync only.** What is saved, and what a cold row carries, is the item's
  own `ItemSaveData` ([save-load.md](save-load.md), [inventory.md](inventory.md)); an object made
  from a row gets it through vanilla's load, then registers its values as any object does.
  [item-state.md](../item-state.md) designs a generic replacement for these per-item patches; it
  is not started.

## Each item type

| Item | Value (key) | Authority | What the code does |
| --- | --- | --- | --- |
| Flashlight (`FlashlightItem.Start`) | `originalLightIntensity`, `originalBeamColour` (packed as a `uint`) | server | |
| | `batteryPower` | the holder's | sent when it moves by 1.0 or more, or reaches 0 or 1; applying it runs `UpdatePower(0)`. The holder's game is the one in the sun (its solar panel) |
| | `buttonState` | the holder's | applying it sets the button and toggles the light |
| Lantern (`Lantern.Initialize`; the component from `Awake`) | `wickSize` | the holder's, or a player turning its knob | applying it runs `UpdateWickRelatedLogic` and moves the knob too, unless the local player is turning it (the knob is a physics body: left behind, its next movement would set the wick back) |
| | `Ignited` | the holder's, or a player lighting it | applying it ignites (`Ignite(1)`) or puts it out |
| Lighter (`Lighter.Start`) | `isOpen`, `Ignited` | the holder's | an inactive lighter (stashed, or a stand-in) takes the value without its animation; `OnEnable` plays the open lid if it is open |
| Shovel (`Shovel.Start`) | `coalMassCapacity`, `coalMassLoaded` | the holder's | applying the load runs `UpdateLoadedCoal`. A shovel with no `ShovelNonPhysicalCoal` logs a warning and registers nothing |
| Page book (`PageBook.Start`, `NetworkedPageBookState`) | `pageBook.currentPage` | the holder's, or anyone's for a book in the world | below |
| Money (`Money.Awake`, `MoneyAmountPatch`) | `amount` (a round-trip string, so a large payout survives exactly) | server | a client's copy of a $1,234 payout was otherwise worth vanilla's default $10. Spending one is [below](#used-up-money-and-the-dev-console) |
| Boombox (`BoomboxInteractionController.Awake`, `BoomboxLiveState`) | `powerOn`, `radioMode`, `rsIndex` (the station) | the holder's, or a player working it | [below](#the-boombox) |
| | `vol`, `antenna` | the holder's, or a player working it | sent when they move by 0.02 or more |
| | `mpCassette` (track, playing and position, with a mark) | the holder's, or a player working it | sent only on a press of play, stop, pause, next or previous: [below](#the-boombox) |
| Any other item (`ItemBase.Awake`) | none | | the component only, finalised by the manager's grace |

**The flashlight's beam colour isn't synced:** DV derives it each frame from `originalBeamColor`
and the battery (`LowPowerFlicker`). Synced, a low battery on the server's copy sent about 700
updates a minute, each a new revision, which made the holder's own requests stale (playtest
2026-10-19). **A flashlight's battery never drains** (`FlashlightBatteryDrainPatch`: the first
drain tops it up, so one loaded flat comes back at full power; other batteries drain as usual).
**A flashlight that never ran `Start`** has no button, so its save keeps its battery and the switch
as loaded (`FlashlightItemSavePatch`).

**On enable, an item's flick reaction is reset** (`ItemBase_Patch.OnEnable`): DV's lighter flick
and wallet coin block themselves with a flag a coroutine clears, and deactivating the item inside
that window left it set for good. Multiplayer toggles items far more often than vanilla does.

### The boombox

A boombox sounds the same on every game (everything heard in the world is shared): each copy plays
what the boombox plays. Before, a copy made from a live `Create` had none of its state: a
boombox one player took out of their inventory playing (stashed-cassette, `WHO=host`) was silent on
the others' games. Its power, mode, volume, antenna and station are plain values. Applying them runs
vanilla's own setters (`SetPower`, `SetMode`, `SetVolume`, `SetAntenna`; a station change turns the
radio off and on again on the new station, as it picks its station as it turns on), then vanilla's
own sync of its knobs to its state (`SyncInteractables`), unless the local player is working them.
A station is an index into each player's own radio playlist, as vanilla saves it.

**The cassette** is one value, `mark|track|playing|seek` (`seek` in samples, as vanilla's player
counts them). Its position runs on by itself in every copy, and so does its track as each copy's tape
moves on to its next song, so neither is a change: the value is dirty only when its **mark** changes,
and the mark changes when play, stop, pause, next or previous is pressed on that game
(`BoomboxCassettePressPatch`, on `CassettePlayerController`; a load that plays it counts, applying a
received value doesn't). A received value carries the position at that press, or, in a `Create`, the
position now. A copy whose tape is on that track within ~2 s of it plays on undisturbed; else it
stops (stop, not pause: from stopped, play starts again, and a second stop would eject), takes the
track and position, and plays if the value does. A copy waits up to 10 s for its cassette (its
`Create` can come after the boombox's), and the value goes if none comes. A dedicated server's copy
may not advance its tape (it has no audio device), so a copy made from it mid-song may start the
track early; not confirmed.

**Before its set-up.** A boombox sets itself up a frame after it wakes, and its knobs report their
own positions the frame after that ([inventory.md](inventory.md#stashed-items-that-act),
`BoomboxStatePatch`). Its values are registered in `Awake`, and any that arrive before then wait,
with the getters giving the waiting values meanwhile, so a knob reporting its own position isn't
taken for a change of volume and sent. A boombox made from its row holds its loaded volume the same
way (`BoomboxLiveState.HoldVolume`).

### The page book

`PageBookPatch` adds `NetworkedPageBookState` to an item with a `PageBook`, which bridges the
book's page index into one tracked value. Input stays the game's: a page turn
(`PageBook.PageFlipped`) updates the value and queues an observation of the item
(`QueueLocalStateObservation`, `page-flipped`), since turning a page moves nothing and no other
item callback would run.

A received page goes through `PageBookSyncPlanner.Plan`:

- outside 0 to 4,095 (`MaximumSupportedPageIndex`), or at or past the book's page count: rejected
  (`item.page-index-invalid`);
- the book's pages not generated yet: kept (`pendingPage`) and applied when generation ends
  (`PageBookGenerated`, `item.page-apply-drained`);
- else applied (`PageBook.ForceCurrentPage`).

The value changes at once in either case, so a waiting page isn't echoed back as the old one. A
book lying in the world sends only this value from a player who doesn't hold it.

## Working an item you don't hold

`ItemInteractionPatch.cs` tells an item that the local player is working it, which opens its
interaction window (`ItemInteractionWindow`, [physics.md](physics.md#throws-and-release)): grabbing,
releasing or using one of its controls (a set-down lantern's knob, a mounted gadget's switch, not
the item's own grab), and lighting a lantern with a flame the player holds (`IgnitableUse.HandleUse`,
`Igniter.IgniteNow`, then `Lantern.Ignite`). A value changing alone can't tell: every game's copy
drifts.

## Magazines

`MagazinePatch` tells an ammo item when it goes into or out of a tool's magazine
(`ItemMagazine.AddItem`, `RemoveItem`, `NoteMagazineChanged`), since vanilla makes the ammo inactive
in its slot and its own frame never sees it go in. Its game then reports its new home. A load the
server applied isn't reported back. The home and its rules are
[containers.md](containers.md#ammo-in-a-magazine).

## The grabber

`GrabberRecoveryPatch` runs in every game, after `Grabber.DoUpdate`. A forced or quick inventory
change can leave DV's non-VR grabber `Holding` or `Dragging` after its handler has let go; while it
is latched, hover is off and every hotbar item is refused. `GrabberRecoveryPlanner` names two stale
states, and after 8 frames in a row of the same one the patch acts:

- a held handler that no longer reports grabbed, in `Holding` or `DraggingWhileHeld`: it is let go
  (`ReleaseHolding`);
- no held or dragged handler, in `Holding`, `Dragging` or `DraggingWhileHeld`: the state is reset to
  idle and the raycast refreshed.

Each recovery logs `interaction.grabber-stale-state-recovered` (or `-failed`, with the exception).

## Used up, money and the dev console

These are routes for an item's end, described where the route is:

- **A key or a spray can used up** on a client is a `UseUp` request (`UseUpRequestPatch`); vanilla's
  own destroy and replacement don't run there, and a spray's paint is applied when the server
  accepts ([vanilla-storage.md](vanilla-storage.md)).
- **A note or coin spent** on a client is deposited first (`MoneyPatch.TrySpend`, `DepositMoney`);
  the host's own spend notes its end `MoneyDeposited` (`MoneyDepositPatch`)
  ([ledger.md](ledger.md#births-and-ends)).
- **The dev console's item commands** run on the host, whose destroys end with the cause
  `DevConsole` (`DevConsolePatch`); a client's are refused with a message (`DevConsoleRefusalPatch`)
  ([vanilla-storage.md](vanilla-storage.md)).

## Tests

Core (`Multiplayer.Core.Tests/Items/Behaviours/`):

- `TrackedStateComposerTests`: a full sync carries every value, a client's delta only its dirty
  values that aren't the server's, the host's delta its dirty values; a merge sorts applicable,
  unknown and refused keys; the host's copy of a held item reports only server values; blank and
  duplicate keys are refused.
- `PageBookSyncPlannerTests`: a valid page applies, an ungenerated book defers, an invalid page is
  rejected.
- `GrabberRecoveryTests`: a stale hold is released, an empty latched state reset, a healthy hold or
  drag never touched.

Scenarios (`DevTools/DvmpScenarios/Scenarios/`):

- `item-release MODE=object-state`: a non-holder's wick change isn't taken; `MODE=interact`: a knob
  turn on a lantern lying in the world is, knob and all; `MODE=regrab-late`: the server's copy's
  flame changing doesn't make the holder's pickup stale.
- `remote-held-item`: a client lights a lantern in their hand and puts it away.
- `retrieved-state`: a lantern from Lost and Found keeps its wick through a take-out and a drop.
- `stand-in-object-state`: the server changing a stashed flashlight's light intensity leaves the
  client's stand-in in its slot.
- `money-deposit`: a client's and the host's note reach the wallet once.
