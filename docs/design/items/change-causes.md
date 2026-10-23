# Why an item changed

> Status: built in R3 of the [item refactor](../item-refactor.md), 2026-10-24. This page says what the code does.
> It covers the causes of item moves and removals (`Items/ChangeCauses` in Core, Server, Client and
> Shared): what a cause holds, how the server names one, what goes on the wire, what a client does
> with it, and how the [ledger](ledger.md) carries it. The update that carries a cause is [wire.md](wire.md#the-update).

"Where did my radio go?" is answered from a cause recorded when it happened, not worked out from
timings afterwards.

## A cause

`ChangeCause<TReason>` (`Multiplayer.Core/Diagnostics/ChangeCause.cs`, shared with trains) is:

| Field | What |
| --- | --- |
| `Reason` | a value of the system's own reason enum |
| `PlayerId` | the acting player's session number, 0 when no player did it |
| `PlayerName` | their name at the time, kept because they may have left since |
| `Tick` | the network tick it happened on |
| `Detail` | free text for what the reason doesn't say: what changed, a refusal's reason, the callers |

`ToString()` is the one form every log line uses: `PickedUp by Bentgear29 (world -> Bentgear29's hand)`.
`WithDetail` replaces the detail. The actor is a session number on purpose: the cause is for display and
for a game to know its own change coming back ([wire.md](wire.md#naming-a-player)).

## The reasons

Both enums are in `Multiplayer.Core/Items/ChangeCauses/ItemChangeReasons.cs`, a byte on the wire each,
`Unknown` first: append values, never reorder.

- **`ItemMoveReason`**, why an item's placement, holder or owner changed: `PickedUp`,
  `TakenFromInventory`, `StoredInInventory`, `TakenFromPlayer`, `Dropped`, `Thrown`, `Attached`,
  `Detached`, `Moved`, `Settled`, `Recalled`, `RetrievedFromLostAndFound`, `WithdrawnFromContainer`,
  `Purchased`, `Granted`, `RestoredFromSave`, `DroppedOnDisconnect`, `JobBookletSummoned`,
  `ClaimSlotMoved`, `RescuedFromBelowMap`, `HostCorrection`, `ThrownOutOfBin`, `InstalledAsGadget`,
  `RemovedAsGadget`, `Issued`, `StarterKit`, `CollectedToLostAndFound`, `DepositedInContainer`, and
  from R3 `SlotMoved` (between two slots of one inventory) and `WentLive` (a cold item made again in
  its slot at join).
- **`ItemRemovalReason`**, why an item stopped being a live object: `DestroyedByGame` (nothing said
  why), `CollectedToLostAndFound`, `DepositedInContainer`, `DestroyedWithCar`, `MoneyDeposited`,
  `JobDocumentDestroyed`, `AbandonedStockForkCleared`, `AuthoredSlotReplaced`, `DestroyedFromInventory`,
  `LeftInterest` (a client's own: the item left its interest and still exists), and from R3 `WentCold`
  (an inventory item kept as data, its home unchanged).

`ItemWhereabouts` (placement, holder identity, owner identity) is what a move compares. A pose alone
isn't a move. `ItemTransitionReason`, also on every update, is how live state changed (a client's state,
a recall, a settlement), not why: it isn't a cause.

## On the server

`ItemChangeLog` (`Multiplayer/Server/Items/ChangeCauses/ItemChangeLog.cs`) builds causes with
`Move(reason, by, detail)` and `Removal(reason, by, detail)` (the player's session number and name, the
tick), and records them:

- `RecordMove(item, record, cause)` keeps the cause as the record's `LastMove`, logs one line
  (`ItemChangeLogFormat.Moved`: `Item ItemRadio (netId 763) moved: <cause>`) and calls
  `ServerLedger.OnMove`.
- `RecordRemoval(item, cause)` adds it to `RecentRemovals` (the last 256, newest first for a reused
  NetId, since the record goes with the item), logs one line (`... removed: <cause>`) and calls
  `ServerLedger.OnRemoved`.

**Naming a move.** `LiveItems.ApplyTransition` compares the whereabouts before and after an accepted
transition. When the caller passed a cause it is used, with what changed added to its detail. Callers
that know why pass one: a disconnect drop or stash, an admin grant, a shop delivery, a booklet summon, a
bin throw-out, a deposit's possession repair, a gadget installed or removed. Otherwise `TransitionCause` names it:
a save restore is `RestoredFromSave`, a booklet summon `JobBookletSummoned`, and anything else
`ItemMoveClassifier.Classify(before, after, thrown)`; a change made by the server's own copy says
"host's game" and its last trigger (`ObservationTrigger`); an `Unknown` carries its callers
(`ItemChangeCauses.Callers`). A transition whose only change is the slot is `SlotMoved`, "slot 4 -> 9".
Nothing is recorded when nothing changed and no cause was given.

The server's own operations name theirs: `Recalled`, `RetrievedFromLostAndFound` and its rollback
(`HostCorrection`), `SlotMoved` for a swap ("slot 4 <-> 9"), `CollectedToLostAndFound`,
`WithdrawnFromContainer` (or the caller's: `Issued`, `StarterKit`, `WentLive`), `ClaimSlotMoved`
(`LiveItems.SendClaim`), `Settled` (only when a settlement lands the item somewhere else, so `LastMove`
still says who threw it) and `RescuedFromBelowMap` (`ServerItemManager.FallRescue`).

**Naming a removal.** Code that destroys an item says why first, `NetworkedItem.NoteRemovalCause`; then
`NetworkedItemServer.OnDestroyed` records that note and sends it with the Destroy. Notes are made for
money deposits, deleted cars, cold deposits, going cold, abandoned stock forks, replaced authored slots,
job papers (`ItemChangeLog.NoteJobDocumentRemoval`, from the job patches, whose callers are still on the
stack) and `Inventory.DestroyItem`. A note older than two seconds is stale. With no fresh note the
removal is `DestroyedByGame`, and its detail says what is known: where it was, its parent, an unloaded
scene, its last move, the last host trigger, a stale note, the callers. A Lost and Found collection
isn't a destroy: `LostAndFoundServer.Collect` records `CollectedToLostAndFound` with the
`LostItemReason` and the owner. A data-only item's holder leaving (`PlayerItems.DataGoesCold`) has no
object to destroy: it logs `WentCold` itself.

## On the wire: only the host's cause

`ItemUpdate` (`Multiplayer/Shared/Protocol/Data/Items/ItemUpdate.cs`) carries `RemovalCause` on a
Destroy, and `MoveCause` on an update with item state (`ItemState`, `FullSync`, `Create`), each behind a
presence flag (`ChangeCauseData`), so pose and object-state updates carry none.

Only the host's cause goes out. A player's own change goes up as a request, which has no cause; and
`LiveItems.ApplyTransition` sets `snapshot.MoveCause = null` before anything else, so a cause a
client's state put in the update it is turned into is dropped. The server then sets the cause it
named, only when something moved. Each recipient's copy (`ItemUpdate.For`) keeps it.
`ItemUpdate.OriginatingPlayerId`, the cause's actor, is on every update, cause or not.

Corrections carry one too: a refused change (`HostCorrection`, "your change was refused: <reason>",
`SendCanonicalRejectionCorrection`), a claim slot moved (`ClaimSlotMoved`, to the owner), a fall rescue
(on the convergence update). A Destroy that ends an item never carries a Lost and Found collection's, a
deposit's or going cold's transition reason (`OnDestroyed` clears it), so a client doesn't take an
item's final Destroy for another collection. A Destroy for a recipient the item merely left the
interest of carries no cause.

## On a client

`ClientItemManager.ProcessReceivedAsClient` hands every Destroy and every update with a `MoveCause`
to `ClientItemChangeLog.OnSnapshot` before the item applies it, while the item still says whose it was.
It keeps the cause on the item (`NetworkedItem.LastMoveCause`, `LastRemovalCause`, in the entity debug
state) and logs it in the server's one-line format. A Destroy without a cause is `LeftInterest` when its
transition reason is an interest retirement, else `Unknown`. Changes to the local player's items (its
relation `Mine`, or held or placed by it) go to the log; everyone else's, and items that only left its
interest, go to the debug log.

A client acts on one reason: `HostCorrection`. A correction is never taken for an echo of the
player's own hold (`NetworkedItem.Snapshots`), and a refused stash's "in your hand" drops the item out
of the inventory DV already put it in (`NetworkedItem.StateApplication`). Everything else is for
reading.

## The ledger's cause

A ledger commit carries the cause as a `LedgerCause` (`Multiplayer.Core/Items/Ledger/ItemLedger.cs`):
the `ItemMoveReason`, `By` (the identity of the cause's player, `PlayerNumbers.Identity(cause.PlayerId)`;
the ledger never holds a session number) and the detail. `ServerLedger.CommitTransition`, `Commit`,
`Swap` and `Settle` build it from the move's `ChangeCause`; `Repair` from the invariant check or a
removal names its own (`CollectedToLostAndFound`, `DepositedInContainer`).

- Each change raises `ItemLedger.Changed` with its `LedgerChange` (kind, entry, the home it left, the
  cause). The entry doesn't keep the cause; the record's `LastMove` does. Today the server listens only
  for `ClaimSlot` changes.
- A **birth** takes its reason from the move: `ServerLedger.BirthFrom` maps `Purchased`, `Granted`,
  `Issued` and `StarterKit` to the same birth; `RestoredFromSave`, `WithdrawnFromContainer` and
  `RetrievedFromLostAndFound` to `Migrated`; anything else to `Claimed`. `Birth.By` is the cause's
  player's identity, `Birth.Detail` its detail.
- An **end** takes the removal's reason and detail (`End.Reason` is an `ItemRemovalReason`), except
  for the removals that aren't ends ([ledger.md](ledger.md#the-ledger-decides-live-state-follows)).
- A drift the ledger follows (`item.ledger-stale-home`, `item.ledger-not-first`) is logged with the cause.

## Not attributed

- A destroy by Derail Valley code other than the patched job and inventory methods: `DestroyedByGame`,
  with the context above but rarely a caller (Unity runs `OnDestroy` at the end of the frame).
- A settlement's pose travels in the spatial commit packet, which carries no cause.
- A re-key (`InstallGuidRekey`) changes owners and holders without moving anything: it logs a count,
  and each item the ledger sent to Lost and Found, but no cause per item.
- A move between cold containers has no NetId: it is logged as `Cold item moved: <cause>`.

## Tests

- `Multiplayer.Core.Tests/Items/ChangeCauses/ItemChangeReasonsTests.cs`: the classifier for each move,
  another player's item taken, a hand swap unchanged, an owner change alone counted, whereabouts, the
  detail and the one-line format, `RecentItemRemovals` (newest of a reused NetId, capacity), and both
  enums byte-sized with `Unknown` first.
- `Multiplayer.Protocol.Tests/Items/ItemPacketFixtureTests.cs`: a Destroy round-trips its removal cause
  and no move cause; state updates round-trip their move cause; updates without one, and object-state
  updates, carry none.
- Scenarios ([testing.md](../../testing.md)): `item-lineage` (an entry ends with the removal's reason,
  `DestroyedByGame`), `client-fall-rescue` (the record's `LastMove` is `RescuedFromBelowMap`, from the
  client's report), `server-throw` (its `LastMove` is never `RescuedFromBelowMap`). No scenario checks
  the `WentCold`, `WentLive` or `SlotMoved` causes by name; `cold-homes` and `inventory-slots` run those paths.
