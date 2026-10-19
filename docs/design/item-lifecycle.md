# Multiplayer Item Lifecycle Design

> Status: canonical description of the implemented item lifecycle. This document defines how
> identity, placement, possession, ownership, projections, persistence, recovery, and cleanup fit
> together. Feature-specific documents remain authoritative for their detailed protocols.

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
- **Starter kit:** given on a player's first join only (`ClientboundSaveGameDataPacket.CreatePacket`
  with `NetworkServer.StarterKitsGiven`, 2962fa7).
- **Disconnect:** the departing player's hand item, and any item in their inventory that another
  player owns, is dropped where it is (`DropHeldItemsOfDepartingPlayer` in
  `NetworkedItemManager.ProjectionAndCompatibility.cs`, 2d52799). Items they own stay keyed to
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
  (`NetworkedItemManager.Convergence.cs`, b601cc6); items that fall through the terrain are put
  back at their last safe pose (`NetworkedItemManager.FallRescue.cs`, 5cf63a0).
- **Not implemented:** revoking a spatial lease on Lost-and-Found collection (the revision
  advance makes late samples fail instead), the container retirement retry, and per-recipient
  projection state in the debug snapshot.
- **Still temporary:** client item adoption (`TemporaryClientAdoptionCompatibility`) is used by the
  starter kit, DV's client-generated essentials and rejoin restoration, which matches a returning
  player's fresh copy to their original by prefab name.

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
purchases. `TemporaryClientAdoptionCompatibility` and
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
