# Why an item changed

> Status: as built, checked against the code on 2026-10-23 (`items/lineage`). Built 2026-10-19 (#106).
> Part of the item lifecycle ([`item-lifecycle.md`](item-lifecycle.md)).

"Where did my radio go?" is answered from a recorded cause, not reconstructed from timings. Each
cause is a `ChangeCause<TReason>` (`Multiplayer.Core/Diagnostics/ChangeCause.cs`, shared with
trains): a reason, the acting player, the tick, and a detail for what the reason doesn't say. The
reasons are in `Multiplayer.Core/Items/ItemChangeReasons.cs`. Both enums are bytes on the wire:
append values, never reorder.

- `ItemMoveReason`: `Unknown`, `PickedUp`, `TakenFromInventory`, `StoredInInventory`,
  `TakenFromPlayer`, `Dropped`, `Thrown`, `Attached`, `Detached`, `Moved`, `Settled`, `Recalled`,
  `RetrievedFromLostAndFound`, `WithdrawnFromContainer`, `Purchased`, `Granted`, `Adopted`,
  `ReturnedToRejoiningOwner`, `RestoredFromSave`, `DroppedOnDisconnect`, `JobBookletSummoned`,
  `ClaimSlotMoved`, `RescuedFromBelowMap`, `HostCorrection`, `ThrownOutOfBin`, `InstalledAsGadget`,
  `RemovedAsGadget`, `Issued`, `StarterKit`. `Adopted` and `ReturnedToRejoiningOwner` are no longer
  sent (client adoption and the rejoin swap are gone); they keep their numbers.
- `ItemRemovalReason`: `Unknown`, `DestroyedByGame`, `CollectedToLostAndFound`,
  `DepositedInContainer`, `DestroyedWithCar`, `MoneyDeposited`, `JobDocumentDestroyed`,
  `AbandonedStockForkCleared`, `AuthoredSlotReplaced`, `DestroyedFromInventory`, and on clients only
  `LeftInterest`.

The owned-items ledger follows these causes: a move's reason gives a new entry its birth, and a
removal's reason ends it ([owned-items.md](owned-items.md#the-ledger-and-the-records)).

## On the server

`ItemChangeLog` (`Multiplayer/Server/Items/ItemChangeLog.cs`) writes every change as one line:

```text
Item ItemRadio (netId 763) moved: TakenFromPlayer by Liam (Bentgear29's inventory -> Liam's hand)
Item JobOverview (netId 801) removed: JobDocumentDestroyed (job CME-SU-12 Completed; callers ...)
Item Scanner (netId 812) moved: Purchased by Bentgear29 (at ShopHarbor, 1 of 1, owner Bentgear29, lying in the world)
```

- **Moves.** `AuthoritativeItemRegistry.TryApplyTransition` compares the item's placement, holder
  and owner before and after an accepted transition. When any changed, or the caller named a cause,
  it logs the cause and keeps it as the record's `LastMove` (`lastMove` in the record's debug
  snapshot). A caller that knows why passes the cause: a disconnect drop, an admin grant, a booklet
  summon, a deposit's possession repair, a retrieval taking an item from its holder. Otherwise
  `ItemMoveClassifier.Classify` names it from what changed, and a change made by the server's own
  copy says "host's game" and what last changed that copy
  (`INetworkedItemServerPart.ObservationTrigger`, such as `inventory.AddItemToInventory`). A change
  the classifier can't name is `Unknown`, with the calling methods in its detail.
- The registry's own transitions record their cause too: recall, Lost and Found retrieval and its
  rollback, cold withdrawal, starting items, a settlement that lands an item in a car (one that comes
  to rest where it was thrown isn't recorded, so `LastMove` still says who threw it), claim slot
  moves, fall rescue, and gadget installs and removals. A shop purchase and a save restore set it
  where they set the owner; restored records keep `RestoredFromSave` without logging.
- **Removals.** Code that destroys an item says why first with `NetworkedItem.NoteRemovalCause`, and
  `NetworkedItem.OnDestroy` logs that cause and sends it with the Destroy. Notes are made for money
  deposits, deleted cars, cold deposits, abandoned stock forks, replaced authored slots, job booklets,
  overviews and reports (the job patches, whose callers are still on the stack) and
  `Inventory.DestroyItem`. A Lost and Found collection isn't a Unity destroy: `Collect` logs it, with
  the `LostItemReason` and the owner.
- **Unexplained removals** are `DestroyedByGame`. Unity runs `OnDestroy` at the end of the frame,
  after the caller of `Object.Destroy` has returned, so the stack rarely names it; the detail says
  what is known instead: the record's placement and holder, the parent the item went with, an
  unloaded scene, its last move, and the server's last trigger. A note older than two seconds is
  treated as stale and reported in the detail.
- `ItemChangeLog.RecentRemovals` keeps the last 256 removals, newest first for a reused NetId,
  because the record is gone with the item. Read it with `dvmp_eval`.

## On clients

`ItemUpdateData` carries the cause with the change: `RemovalCause` on a Destroy, `MoveCause` on an
update with item state (`ItemState`, `FullSync` or `Create`), each behind a presence byte, so
position and object-state updates are unchanged on the wire. The server sends a move cause only on
the update that carries a change, never on interest creates or convergence, and drops any cause a
client put in its own update. Corrections carry one too: a refused change (`HostCorrection`, "your
change was refused: stale-authority-revision"), a cancelled recall, a claim slot move and a fall
rescue. A Destroy that ends an item never carries a projection's reason, so a client doesn't take an
item's final Destroy for another Lost and Found collection.

The client logs each cause it receives (`ClientItemChangeLog.OnSnapshot`) in the same format and
keeps it on the item (`NetworkedItem.LastMoveCause`, `LastRemovalCause`, shown in the entity debug
state). Changes to the local player's own or held items go to the log; everyone else's, and items
that only left the player's interest (`LeftInterest`), go to the debug log.

## Not attributed

- Destroys by Derail Valley code other than the patched job and inventory methods: `DestroyedByGame`
  with the context above but no caller.
- Settlement poses travel in the spatial commit packet, which carries no cause.
- Owner numbers unbound on disconnect and rebound on login (`UnbindPersistentOwner`,
  `RebindPersistentOwner`) are the same owner under a new number and aren't logged.
- Cold-container moves between containers have no NetId; they are logged as `Cold item moved: ...`.
