# Multiplayer Item Lifecycle Design

> Status: canonical description of the implemented item lifecycle. This document defines how
> identity, placement, possession, ownership, projections, persistence, recovery, and cleanup fit
> together. Feature-specific documents remain authoritative for their detailed protocols.
>
> Planned (2026-10-21, not built): [`owned-items.md`](owned-items.md) rebuilds ownership,
> inventories, rejoin and Lost and Found on a server-owned ledger of owned items, with one id per
> item for life, keyed by identity instead of per-session player numbers. Where it changes a rule
> below, the section says so.

## Implementation status (audited 2026-10-14)

- **Ownership:** an *unowned* personal (`BelongsToPlayer`) item becomes owned by the first player
  who puts it in their hand or inventory (9e641a2; `MayEstablishPersistentOwner` in
  `Multiplayer.Core/Items/ItemAuthorityStateMachine.cs`). Pickup never transfers an existing
  owner; a non-owner pickup of a claimed essential marks the claim `Stolen`. See "Ownership
  establishment" below.
- **Shop purchases:** a client's purchase is spawned by the host at the shop and owned by the
  buyer (`NetworkServer.SpawnShopPurchase`, f796ca8). The host's own purchases still go through
  vanilla and are unowned until someone takes them. `AuthoritativeItemRegistry.Ensure` no longer
  throws for a host-created item registered without a seed snapshot (e827ce8).
- **Starting items:** the server makes them (owned-items.md, PR 4: `StartingItemsIssuer`, when a
  player has loaded). The starter kit comes once per player per career (the ledger's saved
  starter-kit marker). Each of DV's essentials the player has no live item of is made into a
  reserved slot. DV's safeguards are off in every game, and no game makes its own.
- **Disconnect:** the departing player's hand item, and any item in their inventory that another
  player owns, is dropped where it is (`DropHeldItemsOfDepartingPlayer` in
  `Server/Items/ServerItemManager.PlayerItems.cs`, 2d52799). Items they own stay keyed to
  their identity. A dropped borrowed item reaches its owner's Lost and Found only through
  ordinary distance collection, which needs the owner connected. If the owner is offline when the
  borrower leaves, the item is not recognised as borrowed and stays with the borrower (see
  [`item-ownership-open-questions.md`](item-ownership-open-questions.md)).
- **Rejoin:** the returning player's fresh starter/essential copies are rejected and their
  originals put back in the copies' slots (`TryRestoreReturningOwnerItem`,
  `DiscardUnboundLocalDuplicate`; 040b3bf, 35914cc). Owner and possessor IDs are rebound from
  identity (`AuthoritativeItemRegistry.RebindPersistentOwner`), including a recalled item
  (0f5d0ab).
- **Lost and Found** records follow the owner's identity: unbound on disconnect, rebound on login
  (`LostItemRegistry.UnbindOwner`/`RebindOwner`, e0e9781); collected items stay inert after a
  save is loaded (b217af6).
- **Claim slots:** no two of a player's essentials claim the same inventory slot; the host moves
  one apart after a transition, a recall or a retrieval, and when the owner loads. See "Retrieval
  claim slots" below.
- **Host saves** keep other players' inventories and the item in their hand (325c2fb, dba0837).
  Unowned dynamic items are not saved (faa42c0), which includes dropped stock forks.
- **Recovery:** host convergence re-sends resting world items so drifted client copies heal
  (`ServerItemManager.Convergence.cs`, b601cc6); items that fall through the terrain are put
  back at their last safe pose (`ServerItemManager.FallRescue.cs`, 5cf63a0).
- **Change causes (2026-10-19):** every change of an item's placement, holder or owner, and every
  removal, has a recorded reason (`ItemMoveReason`, `ItemRemovalReason`), logged in one line on the
  host and sent with the update to clients. See "Why an item changed" below. Not yet checked in
  game.
- **Identity gaps (read from the code 2026-10-21, not run in game):**
  - An item withdrawn from a cold container is registered without its persistent id
    (`RegisterMaterializedColdItem`), so it is not saved. The id is still on the object.
  - Lost and Found and cold-container records mint ids of their own instead of using the item's.
  - Owner numbers are unbound while an owner is offline, so many checks treat their items as unowned.

  All three are fixed by [`owned-items.md`](owned-items.md) (PRs 1 and 2).
- **Not implemented:** revoking a spatial lease on Lost-and-Found collection (the revision
  advance makes late samples fail instead), the container retirement retry, and per-recipient
  projection state in the debug snapshot.
- **No client-made items:** client item adoption is gone (owned-items.md, PR 5). A client's game
  never makes an item the server keeps: the server makes starting items (PR 4), purchases and
  grants, and an unbound local item is destroyed (an authored scene object is kept dormant).

## Purpose

An item is no longer equivalent to a Unity `GameObject`. Multiplayer maintains one canonical host
record which may have a live Unity representation, a client projection, a detached cold record, or
no presentation at all. This document defines the transitions between those forms and the
invariants shared by world sync, spatial physics, inventories, cold containers, and Lost and Found.

The central invariant is:

> Logical state changes only through an accepted host authority transition. Unity objects are
> disposable representations and neither possession nor presentation implicitly changes ownership.

## Supporting documents

- [`item-world-sync.md`](item-world-sync.md) defines authored
  identity, dynamic records, interest streaming, projection binding, persistence, and stock
  replenishment.
- [`item-spatial-physics.md`](item-spatial-physics.md)
  defines simulation leases, motion samples, settlement, and train-relative motion.
- [`container-storage.md`](container-storage.md) defines the
  cold container graph and transactional materialization/dehydration.
- [`item-ownership-open-questions.md`](item-ownership-open-questions.md)
  records subjective ownership-transfer policies that have not been decided.
- [`../research/DERAIL_VALLEY_LOST_AND_FOUND_LOGIC.md`](../research/DERAIL_VALLEY_LOST_AND_FOUND_LOGIC.md)
  and [`../research/DERAIL_VALLEY_ITEM_STORAGE_LOGIC.md`](../research/DERAIL_VALLEY_ITEM_STORAGE_LOGIC.md)
  record the relevant base-game behavior.

## Orthogonal state axes

These values describe different facts and must never be inferred from one another:

| Axis | Meaning |
| --- | --- |
| Persistent identity | Durable UUID for dynamic/cold records, or stable authored key for a scene slot. |
| Runtime identity | Compact NetId allocated only while an ordinary gameplay representation exists. |
| Placement | Authored slot, world, static parent, train interior, hand, inventory, cold container, Lost and Found, installed/snapped, or destroyed. |
| Current possessor | Player whose hand or inventory currently contains the item. |
| Persistent owner | Stable player identity that governs persistence and Lost and Found destination. |
| Retrieval claim | Reserved/locked inventory slot and recall entitlement; not synonymous with ownership. |
| Projection state | Whether a particular client currently has a bound Unity representation. |
| Simulation state | Settled, moving under a host-issued lease, or awaiting reliable settlement. |
| Persistence class | Authored override/tombstone, durable dynamic item, cold record, personal recovery record, or disposable stock fork. |

For example, `PlayerInventory(P2)` proves that P2 possesses an item. It does not prove that P2 owns
it, may recall it, may deposit it in an owner-only container, or should receive it on reconnect.

## Identity layers

### Scene-authored items

A scene-authored item begins with a stable key captured before Derail Valley flattens and reparents
the content hierarchy. The key identifies the authored slot across host restart and projection
retirement. Its runtime NetId is session-local.

### Dynamic materialized items

A dynamic item has a durable UUID in host persistence and a NetId while materialized. Routine
packets use the NetId; the UUID is not sent unless a persistence boundary genuinely requires it.

### Detached items

Cold-container contents and Lost-and-Found records retain durable identity and serialized state but
do not require an ordinary world NetId or Unity object. Materialization allocates a fresh NetId and
binds it to the same durable record.

Today each of those records has an id of its own, minted at deposit or collection. The item's own id
travels inside its serialized state. Planned ([`owned-items.md`](owned-items.md#one-id-for-life)):
the record's id is the item's id, and that one id is kept for the item's whole life, through every
placement and save. A second live item with an id already registered keeps the first and re-mints
the copy.

## Ownership establishment

Persistent ownership changes only through a named, host-authenticated transition. The implemented
authority state machine establishes an owner in these cases:

1. the explicitly temporary `ClientAdoption` compatibility transition accepts a player-marked item
   from a not-yet-converted Derail Valley producer;
2. an *unowned* item classified by Derail Valley as `BelongsToPlayer` is put into a player's hand
   or inventory, or receives an eligible reserved/locked retrieval claim: that player becomes the
   owner; or
3. a host operation carries the owner with it: a client's shop purchase (the buyer), cold-container
   withdrawal (the stored owner), Lost-and-Found retrieval, and save restoration (the saved owner
   identity).

Dropping, throwing, projection creation, or interest re-entry does **not** establish ownership,
and nothing makes a nonpersonal item owned. Pickup or inventory insertion never transfers an
existing owner: ownership is retained when another player possesses the item.

Planned ([`owned-items.md`](owned-items.md#what-an-owned-item-is)):

- the host's own purchases go through the server like everyone's (the `ClientAdoption` path is
  gone since PR 5; the server makes the starter kit and essentials since PR 4; licence papers
  aren't made in multiplayer);
- a replacement vanilla makes while using an item up is owned by the used item's owner;
- job documents are never owned;
- nonpersonal items stay unowned even when carried (decided 2026-10-21).

### Explicit office-bin example

An eligible scene-authored office bin is nonpersonal (`BelongsToPlayer == false`), nonessential, and
has no retrieval claim. If a player puts it in their inventory:

- placement becomes `PlayerInventory` and that player becomes its current possessor;
- persistent owner remains empty;
- it does not become recallable or Lost-and-Found eligible;
- taking it from its authored office slot promotes the taken representation to an unowned dynamic
  replenishable-stock fork; and
- after it is dropped and abandoned outside every player's interest, disposable-fork cleanup may
  retire it; and
- a host save does not keep it, because unowned dynamic items are not persisted.

The adoption compatibility path is not valid for an already catalogued scene-authored bin. It is
also not part of the target lifecycle: the host must ultimately create receipts, starting items,
and every other runtime item through explicit operations, as it now does for client shop
purchases. `TemporaryClientAdoptionCompatibility` (with its host half,
`TemporaryHostAdoptionCompatibility`) and
its packets must be removed when those producers are converted. If a future code path nevertheless
gives a stock fork a persistent owner or retrieval claim, cleanup fails safe: the item is no longer
disposable and recovery/persistence takes precedence.

## Client projection invariant

The host owns the complete canonical pipeline. A remote client may hold only:

- an exact scene-authored object made dormant and rebound by stable authored key;
- a fresh dynamic Unity projection instantiated for one host `Create` lifetime;
- an inert Lost-and-Found or cold-storage presentation explicitly controlled by that subsystem; or
- a narrowly eligible object awaiting the temporary adoption compatibility handshake.

Dynamic projections are never reused as another logical item. Host retirement destroys them.
Unbound objects outside the temporary compatibility policy are interaction-gated and destroyed;
they cannot elevate themselves by sending state. Persistent player ownership does not weaken this
rule: it is a host record, not client authority.

## Canonical lifecycle

```text
scene-authored slot
  -> bound world projection
  -> disturbed but still authored -> reset after area vacancy
  -> taken into hand/inventory
       -> dynamic stock fork + pending authored replenishment
       -> authored slot replenished after area vacancy

materialized dynamic item
  <-> hand / inventory / world / static parent / train interior
  -> cold-container deposit -> detached cold record
  <- cold-container withdrawal <- newly materialized representation
  -> Lost and Found collection -> detached owner recovery record
  <- Lost and Found retrieval <- newly materialized representation
  -> authoritative destruction/tombstone

client projection
  <- Create when relevant
  -> retire when irrelevant
  (neither transition changes the canonical lifecycle or ownership)
```

Every accepted logical transition increments the authority revision. Rejected transitions leave
the record unchanged. Motion samples use their separate simulation sequence and cannot mutate
placement, ownership, containment, recovery membership, or lifecycle.

## Authored office stock lifecycle

Eligible office furniture is a replenishable stock slot, not one eternal shared object:

1. The host catalogues the authored slot and baseline pose.
2. A player may disturb it while it remains the authored record.
3. If nobody takes it and all players leave the source interest area for the reset grace period, the
   host restores its baseline pose.
4. Taking it from `World`/`StaticParent` into a hand or inventory detaches the taken item from the
   authored key and marks it as a replenishable-stock fork.
5. After every player leaves the source interest area, the host replenishes the original authored
   slot exactly once. All clients discover the replacement through normal host interest projection.
6. The taken fork continues as an independent dynamic item while possessed or relevant.
7. Once an unowned, unclaimed fork is dropped and remains outside all player interest for the
   abandonment grace period, the host destroys its canonical record and representations.

Personal, essential, job, shop, container, snapped, and installed-gadget items are excluded from
this stock lifecycle. They continue through their dedicated persistence/recovery paths.

## Cold-container lifecycle

Depositing an item is a logical commit followed by presentation retirement:

1. authenticate the actor and validate possession, persistent ownership, compatibility, capacity,
   graph depth/cycles, expected revisions, and serialized state;
2. commit the cold record and graph edge atomically;
3. mark the cold record authoritative and increment revisions;
4. remove interest, retire the NetId, and destroy/deactivate the stale Unity representation;
5. if presentation cleanup fails, retain a recoverable committed-pending-retirement condition and
   retry cleanup rather than rolling back the cold graph (the retry worker is not built yet; the
   failed representation is deactivated and left).

Withdrawal keeps the cold edge until the new Unity item has initialized, loaded state, finalized
tracked values, received a NetId, and registered with canonical authority. Failure before commit
destroys the partial representation and leaves the cold record untouched.

Container ownership controls access to the shell's cold graph. Each contained item retains its own
persistent owner; deposit never transfers ownership.

## Lost and Found lifecycle

Lost and Found applies only to a canonical personal item with a nonzero persistent owner. Collection
must validate that the item is not held, inventoried, contained, installed, snapped, protected by a
nearby player, or already governed by a newer transition. The owner ID is unbound while the owner
is offline, so an offline owner's items are not collected.

Planned ([`owned-items.md`](owned-items.md#lost-and-found-for-every-owned-item), decided
2026-10-21):

- An offline owner counts as far away. Their items are collected under the same nearby-player and
  grace rules, from train interiors, shelves and attachments as well as the world.
- A departing borrower's items go straight to their owner's Lost and Found.
- An owner can retrieve any owned item into their Lost and Found from their owned-items list.
- Nothing expires.

On collection:

- any simulation lease is superseded: the revision advances, so late samples and settlements are
  rejected (the lease is not revoked explicitly);
- ordinary world/interest projection is retired;
- durable identity and serialized state move to the owner's recovery registry;
- stale possessor silhouettes and claims are cleared as part of the authoritative transition; and
- a container shell retains its cold descendant graph without materializing or separately
  collecting the contents.

Retrieval materializes the same durable record into a requested valid inventory slot with a fresh
runtime binding. A stale revision, full inventory, unknown handle, or failed materialization leaves
the Lost-and-Found record intact.

An entry only lives as long as its item. Job booklets are personal items and are collected like any
other (a booklet retrieved from Lost and Found can still be handed in), but the job system owns
them: when a job completes, expires or is abandoned the host destroys every booklet copy,
including one stored in Lost and Found, and vanilla's job load destroys stored booklets whose job is
gone. Destroying the item drops its authority record but not the entry. The host's periodic
reconcile (`LostItemEntryReconciler.Classify`, unit tested in `Multiplayer.Core`) checks every
entry against its item:

| Item | Authority placement | Result |
| --- | --- | --- |
| present | Lost and Found | kept (re-projected inert if a load left it active) |
| present | anything else | removed: another system moved it out (validator, booklet reprint) |
| present | no record | kept until a record exists |
| gone | any or none | removed: nothing is left to retrieve |

A retrieval that finds the item gone (`missing-canonical-unity-item`) or out of Lost and Found
removes the entry at once. Each removal pushes a fresh list to the owner and logs the entry. The
save is written after a reconcile, so destroyed items are not saved, and a save record with no
storage slot (`storageIndex` -1, written for destroyed items by earlier builds) is dropped at load
because it can never bind.

## Retrieval claim slots

An essential's retrieval claim names one slot of its owner's inventory. While the item is elsewhere
(dropped, held by another player, or in Lost and Found) the owner's client shows a red silhouette
there, and the silhouette is what the owner clicks to recall it. A Derail Valley slot holds one
item, so **each of a player's essentials must claim a slot of its own**. When two shared one, the
client refused the second silhouette (`essential-claim-slot-occupied`), a recall was refused too
(`recall-claim-slot-occupied`), and the essential could never come back.

`ItemAuthorityStateMachine` sees one item at a time and follows the owner's client when the owner
places an essential, so it can't notice the clash: putting the map into slot 3 while the stolen
radio claims slot 3 gave both slot 3 (the radio's slot looks empty in DV, or holds only a
silhouette DV may have lost). The host checks all of an owner's claims together
(`AuthoritativeItemRegistry.EnsureDistinctClaimSlots`, policy in
`Multiplayer.Core/Items/EssentialClaimSlotPolicy.cs`):

- after an accepted transition of an essential, an owner recall, and a Lost-and-Found retrieval;
  and
- when the owner reaches `ReadyForItems`, before their items go out, which repairs a clash that
  a save or an earlier session left (records restored by persistence keep the saved slots).

Of the claims on one slot, the item the owner just put there keeps it, then an item really in the
owner's inventory, then a claim that isn't a silhouette, then the lowest NetId. The others move to
the highest slot no claim of that owner holds: DV fills the backpack from the front, and the host
doesn't know where the owner's unclaimed items sit. The move keeps the item's revision (only the
owner's metadata changes, so a holder's next transition isn't refused as stale), updates the
Lost-and-Found record's copy of the slot, and sends the owner a correction, so their client puts
the silhouette in the new slot. An owner still loading gets it with the rest of their items. With
every slot claimed the clash stays rather than drop a claim.

## Cleanup and recovery precedence

Cleanup is host-authoritative and ordered from strongest durable obligation to weakest disposable
presentation:

1. active Lost-and-Found membership;
2. persistent player ownership;
3. inventory retrieval claim;
4. cold-container graph membership;
5. installed, snapped, job, shop, or other protected placement;
6. current possession or player interest;
7. disposable replenishable-stock-fork abandonment;
8. projection-only retirement.

A lower-priority cleanup path must never erase a higher-priority record. In particular, stock-fork
cleanup rejects any record with Lost-and-Found membership, a persistent owner identity, a nonzero
owner player ID, an inventory claim player/slot, or a protected placement. Unknown player location
and ambiguous bindings fail safe.

## Placement and motion

World, static-parent, and train-interior placement are logical states. A train-interior item stores
the train identity and parent-local pose so world motion of the train does not look like independent
item motion. A simulation lease may advance a thrown rigidbody, but only a reliable accepted
settlement commits the durable pose used by interest, saving, Lost and Found distance checks, and
later reprojection.

Pickup, containment, Lost and Found, snapping/installation, and destruction revoke or supersede any
older motion stream. Late samples cannot resurrect or relocate an item after those transitions.

## Persistence ownership

Only one subsystem saves each logical form:

- authored overrides and tombstones plus owned dynamic world items: multiplayer world-item
  persistence (`WorldItemsV1`);
- the host's own inventory: vanilla storage;
- other players' inventory and hand items: `WorldItemsV1` records keyed by the holder's identity,
  held back until that player connects and then put in their inventory (a hand item comes back in
  the inventory);
- cold descendants: versioned cold-container graph;
- Lost-and-Found entries: owner recovery registry;
- ordinary scene defaults: Derail Valley scene content, unless overridden or tombstoned.

Planned ([`owned-items.md`](owned-items.md#the-ledger)): an owned-items ledger (`OwnedItemsV1`, in
the same `Multiplayer` section). It holds each owned item's owner, which store holds it, and how its
life began and ended. It is the index the stores above must agree with at load. It doesn't replace
them.

Vanilla storage serialization must not also save a multiplayer-owned detached or authoritative
record. Save/load rebuilds identity bindings and placement indexes before allowing interaction.

## Forbidden implicit transitions

The following must never happen merely as a side effect of presentation or possession:

- inventory insertion assigns ownership to a nonpersonal world prop;
- pickup transfers an existing persistent owner;
- projection activation adopts an authored item;
- interest retirement destroys canonical state;
- a movement sample changes placement or holder;
- container deposit changes item ownership;
- a container entering Lost and Found creates individual descendant recovery entries;
- stock replenishment duplicates the taken fork under the authored identity;
- abandoned-stock cleanup deletes a personal, claimed, contained, or Lost-and-Found item;
- a stale Unity object becomes authoritative after logical dehydration or collection.

## Required lifecycle observability

Lifecycle events and debug snapshots should expose enough information to distinguish these axes:

```text
netId and durable/authored debug identity
prefab and lifecycle classification
placement and placementPlayerId
persistentOwnerPlayerId and persistentOwnerIdentity
inventoryClaimPlayerId, slot, and flags
container/LostAndFound membership
authored slot key and replenishableStockFork marker
world parent kind/key/NetId and committed pose
authority revision and transition reason
projection known/retired state per recipient
simulation lease holder, epoch, and settlement state
```

Automated scenarios should assert canonical cleanup as well as visible disappearance: no authority
record, durable record, storage membership, inventory slot, NetId binding, or client projection may
remain after destructive cleanup. In-game scenarios run through the dvmp harness
([`../testing.md`](../testing.md), [`../../DevTools/README.md`](../../DevTools/README.md)).

## Why an item changed

"Where did my radio go?" is answered from a recorded cause, not reconstructed from timings. Each
cause is a `ChangeCause<TReason>` (`Multiplayer.Core/Diagnostics/ChangeCause.cs`, shared with
trains): a reason, the acting player, the tick, and a detail for what the reason doesn't say. The
reasons are in `Multiplayer.Core/Items/ItemChangeReasons.cs`:

- `ItemMoveReason`: `PickedUp`, `TakenFromInventory`, `StoredInInventory`, `TakenFromPlayer`,
  `Dropped`, `Thrown`, `Attached`, `Detached`, `Moved`, `Settled`, `Recalled`,
  `RetrievedFromLostAndFound`, `WithdrawnFromContainer`, `Purchased`, `Granted`, `Adopted`,
  `ReturnedToRejoiningOwner`, `RestoredFromSave`, `DroppedOnDisconnect`, `JobBookletSummoned`,
  `ClaimSlotMoved`, `RescuedFromBelowMap`, `HostCorrection`.
- `ItemRemovalReason`: `DestroyedByGame`, `CollectedToLostAndFound`, `DepositedInContainer`,
  `DestroyedWithCar`, `MoneyDeposited`, `JobDocumentDestroyed`, `AbandonedStockForkCleared`,
  `AuthoredSlotReplaced`, `DestroyedFromInventory`, and on clients only `LeftInterest`.

Both are byte-sized and on the wire: append values, never reorder.

### On the host

`ItemChangeLog` (`Multiplayer/Server/Items/ItemChangeLog.cs`) writes every change as
one line:

```text
Item ItemRadio (netId 763) moved: TakenFromPlayer by Liam (Bentgear29's inventory -> Liam's hand)
Item JobOverview (netId 801) removed: JobDocumentDestroyed (job CME-SU-12 Completed; callers ...)
Item Scanner (netId 812) moved: Purchased by Bentgear29 (at ShopHarbor, 1 of 1, owner Bentgear29, lying in the world)
```

- **Moves.** `AuthoritativeItemRegistry.TryApplyTransition` compares the item's placement, holder
  and owner before and after an accepted transition. When any changed, or the caller named a cause,
  it logs the cause and keeps it as the record's `LastMove` (in the record's debug snapshot as
  `lastMove`). A caller that knows why passes the cause (a disconnect drop, a rejoin restore, an
  admin grant, a booklet summon, an adoption, a deposit's possession repair). Otherwise
  `ItemMoveClassifier.Classify` names it from what changed, and a change made by the host's own
  copy says "host's game" and what last changed that copy (`INetworkedItemServerPart.ObservationTrigger`,
  such as `inventory.AddItemToInventory`). A change the classifier can't name has `Unknown` and the
  calling methods in its detail. The registry's own transitions record their cause too: recall,
  Lost-and-Found retrieval and its rollback, cold withdrawal, a settlement that lands an item in a
  car (one that comes to rest where it was thrown is not recorded, so `LastMove` still says who
  threw it), claim slot moves, and fall rescue. A shop purchase and a save restore set it where
  they set the owner; restored records keep `RestoredFromSave` without logging, and a held item is
  logged as it reaches its returning holder.
- **Removals.** Code that destroys an item says why first with `NetworkedItem.NoteRemovalCause`,
  and `NetworkedItem.OnDestroy` logs that cause and sends it with the Destroy. Notes are made for
  money deposits, deleted cars, cold-container deposits, abandoned stock forks, replaced authored
  slots, job booklets, overviews and reports (the job patches, whose callers are still on the stack
  and say which job event it was) and `Inventory.DestroyItem`. A Lost-and-Found collection isn't
  a Unity destroy and is logged by `NetworkedLostAndFoundManager.Collect`, with the `LostItemReason`
  and the owner.
- **Unexplained removals** are `DestroyedByGame`. Unity runs `OnDestroy` at the end of the frame,
  after the caller of `Object.Destroy` has returned, so the stack rarely names it; the detail says
  what is known instead: the record's placement and holder, the parent the item went with, an
  unloaded scene, its last move, and the host's last trigger. A note older than two seconds is
  treated as stale and reported in the detail.
- `ItemChangeLog.RecentRemovals` keeps the last 256 removals (`RecentItemRemovals`, newest wins for
  a reused netId), because the record is gone with the item. Read it with `dvmp_eval`.

### On clients

`ItemUpdateData` carries the cause with the change: `RemovalCause` on a Destroy, `MoveCause` on an
update with item state (ItemState, FullSync or Create), each behind a presence byte, so position and
object-state updates are unchanged on the wire. The host sends a move cause only on the update that
carries a change, never on interest creates or convergence, and drops any cause a client put in
its own update. Corrections carry one too: a refused change (`HostCorrection`, "your change was
refused: stale-authority-revision"), a cancelled recall, a claim slot move and a fall rescue.

The client logs each cause it receives (`ClientItemChangeLog.OnSnapshot`) in the same format and
keeps it on the item (`NetworkedItem.LastMoveCause`, `LastRemovalCause`, shown in the entity debug
state). Changes to the local player's own or held items go to the log; everyone else's, and items
that only left the player's interest (`LeftInterest`), go to the debug log.

### Not attributed

- Destroys by Derail Valley code other than the patched job and inventory methods. They are
  `DestroyedByGame` with the context above but no caller.
- Settlement poses travel in the spatial commit packet, which carries no cause.
- Owner IDs unbound on disconnect and rebound on login (`UnbindPersistentOwner`,
  `RebindPersistentOwner`) are the same owner under a new session ID and are not logged.
- Cold-container moves between containers have no netId; they are logged as `Cold item moved: ...`.
