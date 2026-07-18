# Multiplayer Item Lifecycle Design

> Status: canonical description of the implemented item lifecycle. This document defines how
> identity, placement, possession, ownership, projections, persistence, recovery, and cleanup fit
> together. Feature-specific documents remain authoritative for their detailed protocols.

## Purpose

An item is no longer equivalent to a Unity `GameObject`. Multiplayer maintains one canonical host
record which may have a live Unity representation, a client projection, a detached cold record, or
no presentation at all. This document defines the transitions between those forms and the
invariants shared by world sync, spatial physics, inventories, cold containers, and Lost and Found.

The central invariant is:

> Logical state changes only through an accepted host authority transition. Unity objects are
> disposable representations and neither possession nor presentation implicitly changes ownership.

## Supporting documents

- [`MULTIPLAYER_ITEM_WORLD_SYNC_DESIGN.md`](MULTIPLAYER_ITEM_WORLD_SYNC_DESIGN.md) defines authored
  identity, dynamic records, interest streaming, projection binding, persistence, and stock
  replenishment.
- [`MULTIPLAYER_ITEM_SPATIAL_PHYSICS_SYNC_DESIGN.md`](MULTIPLAYER_ITEM_SPATIAL_PHYSICS_SYNC_DESIGN.md)
  defines simulation leases, motion samples, settlement, and train-relative motion.
- [`MULTIPLAYER_CONTAINER_STORAGE_DESIGN.md`](MULTIPLAYER_CONTAINER_STORAGE_DESIGN.md) defines the
  cold container graph and transactional materialization/dehydration.
- [`MULTIPLAYER_ITEM_OWNERSHIP_OPEN_QUESTIONS.md`](MULTIPLAYER_ITEM_OWNERSHIP_OPEN_QUESTIONS.md)
  records subjective ownership-transfer policies that have not been decided.
- [`research/DERAIL_VALLEY_LOST_AND_FOUND_LOGIC.md`](research/DERAIL_VALLEY_LOST_AND_FOUND_LOGIC.md)
  and [`research/DERAIL_VALLEY_ITEM_STORAGE_LOGIC.md`](research/DERAIL_VALLEY_ITEM_STORAGE_LOGIC.md)
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
authority state machine establishes an owner in either of these cases:

1. the explicitly temporary `ClientAdoption` compatibility transition accepts a player-marked item
   from a not-yet-converted Derail Valley producer; or
2. an item classified by Derail Valley as `BelongsToPlayer` receives an eligible reserved/locked
   retrieval claim with a concrete inventory slot.

Ordinary pickup, hand placement, inventory insertion, dropping, throwing, projection creation, or
interest re-entry does **not** establish or transfer persistent ownership. Existing ownership is
also retained when another player possesses the item.

### Explicit office-bin example

An eligible scene-authored office bin is nonpersonal (`BelongsToPlayer == false`), nonessential, and
has no retrieval claim. If a player puts it in their inventory:

- placement becomes `PlayerInventory` and that player becomes its current possessor;
- persistent owner remains empty;
- it does not become recallable or Lost-and-Found eligible;
- taking it from its authored office slot promotes the taken representation to an unowned dynamic
  replenishable-stock fork; and
- after it is dropped and abandoned outside every player's interest, disposable-fork cleanup may
  retire it.

The adoption compatibility path is not valid for an already catalogued scene-authored bin. It is
also not part of the target lifecycle: the host must ultimately create shop outputs, receipts, and
every other runtime item through explicit operations. `TemporaryClientAdoptionCompatibility` and
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
   retry cleanup rather than rolling back the cold graph.

Withdrawal keeps the cold edge until the new Unity item has initialized, loaded state, finalized
tracked values, received a NetId, and registered with canonical authority. Failure before commit
destroys the partial representation and leaves the cold record untouched.

Container ownership controls access to the shell's cold graph. Each contained item retains its own
persistent owner; deposit never transfers ownership.

## Lost and Found lifecycle

Lost and Found applies only to a canonical personal item with a nonzero persistent owner. Collection
must validate that the item is not held, inventoried, contained, installed, snapped, protected by a
nearby player, or already governed by a newer transition.

On collection:

- any simulation lease is revoked;
- ordinary world/interest projection is retired;
- durable identity and serialized state move to the owner's recovery registry;
- stale possessor silhouettes and claims are cleared as part of the authoritative transition; and
- a container shell retains its cold descendant graph without materializing or separately
  collecting the contents.

Retrieval materializes the same durable record into a requested valid inventory slot with a fresh
runtime binding. A stale revision, full inventory, unknown handle, or failed materialization leaves
the Lost-and-Found record intact.

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

- authored overrides and tombstones plus dynamic world items: multiplayer world-item persistence;
- player inventory/hand items: multiplayer player persistence and canonical authority records;
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
remain after destructive cleanup.
