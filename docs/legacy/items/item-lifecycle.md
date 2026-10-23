> Legacy: describes the item system before the refactor (names as on `beta` 04bfe277). Read it only as evidence of how the code behaves today, not as design. Replaced by [items/live-state.md](../../design/items/live-state.md) (R3). The item docs start at [items/README.md](../../design/items/README.md).

# Item lifecycle

> Status: as built, checked against the code on 2026-10-23 (`items/lineage`, the top of the owned-items
> stack #308–#312). This is the entry point for items: an item's whole life in short, and the rules
> every item system shares. Each system's detail is in its own doc ([below](#the-item-docs)), and
> each fact lives in one place.

The server is authoritative over items. It keeps one record per item (`AuthoritativeItemRegistry`,
keyed by NetId) that says where the item is, who holds it, who owns it and its revision. A Unity
object, on the server or a client, is a disposable presentation of that record.

> Logical state changes only through a transition the server accepts. Unity objects are disposable,
> and neither holding an item nor showing it changes who owns it.

## The item docs

| Doc | What it covers |
| --- | --- |
| [owned-items.md](owned-items.md) | Ownership, one id for life, the owned-items ledger, starting items, joining and leaving, essentials' claim slots and recall, the owned-items list and retrieval, lineage, `ItemAccess` and fixtures |
| [lost-and-found.md](lost-and-found.md) | What Lost and Found collects and when, entries, retrieval, saving and loading |
| [container-storage.md](container-storage.md) | Cold containers: contents as server records, browse, deposit, withdraw |
| [item-world-sync.md](item-world-sync.md) | Scene-authored items and the catalogue, cells and interest, projection to clients, convergence, anchored placements, the VR belt, office stock |
| [item-spatial-physics.md](item-spatial-physics.md) | Thrown and dropped items: simulation leases, samples, settlement, the terrain guard and fall rescue, leaving the hand |
| [train-item-wake.md](train-item-wake.md) | Items resting in moving trains, and waking them |
| [item-persistence.md](item-persistence.md) | Which store saves each item, `WorldItemsV1`, loading |
| [item-change-causes.md](item-change-causes.md) | Why each move and removal happened, on the server and clients |
| [item-ownership-open-questions.md](item-ownership-open-questions.md) | The ownership questions still open |
| [gadgets.md](../../design/gadgets.md), [shops.md](../../design/shops.md) | Installed gadgets and shop purchases, as their own systems; they use the item rules here |
| [research/item-bug-review.md](../../research/item-bug-review.md) | The catalogue of known ways a player loses an item, each with the `item-bug-*` scenario that proves it. The "Known issues" sections in the docs above are read from the code; this is where they are tracked. |
| [item-sync-review.md](item-sync-review.md) | Historical: the July 2026 review and its findings |

## What describes an item

These are different facts. None is inferred from another.

| Fact | Where it is | Detail |
| --- | --- | --- |
| Persistent identity | a GUID for a dynamic item (`PersistentItemId`); a stable authored key for an unmoved scene item | [owned-items.md](owned-items.md#one-id-for-life), [item-world-sync.md](item-world-sync.md#scene-authored-items-and-the-catalogue) |
| Runtime identity | the `ushort` NetId, while the item has a live object; the server prefers ids not issued yet this session | |
| Placement | `ItemPlacementKind`, [below](#placements) | |
| Holder | `PlacementPlayerId` and `PlacementPlayerIdentity`, for a hand or inventory | |
| Owner | `Owner`, an identity (empty for unowned); on the wire until R2 an owner number (0 unowned, 255 owned by someone offline) | [owned-items.md](owned-items.md#the-ledger-and-the-records) |
| Retrieval claim | an essential's slot and flags (`Reserved`, `Locked`, `Dropped`, `Stolen`); not the same as owning it | [owned-items.md](owned-items.md#essentials-claim-slots-and-recall) |
| Revision | `Revision`, advanced by every accepted logical change; the run at its tip that changed only object state | [below](#the-state-machine) |
| Projection | per player: nearby, known, acknowledged | [item-world-sync.md](item-world-sync.md#cells-and-interest) |
| Simulation | settled, or moving under a lease (epoch, sequence) | [item-spatial-physics.md](item-spatial-physics.md) |
| Where it is saved | worked out from the above, never stored | [item-persistence.md](item-persistence.md#where-each-item-is-saved) |

`PlayerInventory(P2)` says P2 holds the item. It doesn't say P2 owns it, may recall it, may put it in
an owner-only container, or gets it back on reconnecting.

## Placements

`ItemPlacementKind` (and its Core mirror `AuthorityPlacement`):

| Placement | Meaning |
| --- | --- |
| `World` | loose in the world |
| `TrainInterior` | loose in a car, parented to its interior |
| `StaticParent` | on a shelf (`ItemStaticParent`) |
| `PlayerHand`, `PlayerInventory` | with a player; a VR belt slot is an inventory slot |
| `Attached` | an EOT lantern on a car's end |
| `SnappedAttachment` | on a snap point; declared, but nothing assigns it yet |
| `Installed` | a gadget on a car; only gadget transitions enter or leave it |
| `LostAndFound` | in its owner's Lost and Found |
| `Container` | declared; a stored item has no record at all, the cold graph holds it |
| `Destroyed` | a tombstone for a destroyed scene item, at load |

## An item's life

**It begins** as one of these:

- **a scene-authored item**, keyed by its authored key, dormant on clients until the server binds it
  ([item-world-sync.md](item-world-sync.md#scene-authored-items-and-the-catalogue));
- **a dynamic item** the server makes: a purchase (`ShopDelivery`), a starting item
  (`StartingItemsIssuer`), an admin grant, a withdrawal, a save restore, a replacement. No game makes an
  item the server keeps: client item adoption is gone (owned-items PR 5, #307), and an unbound object
  on a client is made dormant (authored) or destroyed. DV's developer console spawns and a wallet coin
  flick vanish on a client.

**It is owned** from birth (a purchase, an issue, a grant), when an unowned personal item gets its first
taker, or never (a mug, an office bin, a job paper)
([owned-items.md](owned-items.md#what-an-owned-item-is)). Pickup never changes an owner.

**It moves** through accepted transitions between placements. Office props taken from their place
become dynamic stock forks and their slot is refilled
([item-world-sync.md](item-world-sync.md#replenishable-office-stock)). Thrown and dropped items move
under a lease until a commit fixes where they rest. Stored items leave the registry for the cold graph
and come back as new objects under the same id.

**Clients see it** while it is in their interest: a Create when it enters, updates while known, a
Destroy with `InterestRetirement` when it leaves. Neither changes the item.

**It ends** with a removal cause ([item-change-causes.md](item-change-causes.md)): used up, destroyed
with a car, a job paper destroyed with its job, an abandoned stock fork cleared, an authored slot
replaced, or `DestroyedByGame`. An owned item's ledger entry ends with that reason and is kept for
lineage.

```text
scene-authored slot
  -> bound on clients by key
  -> disturbed -> reset to baseline once nobody is near
  -> taken into a hand or inventory -> a dynamic stock fork; the slot is refilled once nobody is near

dynamic item
  <-> hand / inventory / world / train interior / shelf / attached / installed
  -> deposited -> a cold record      <- withdrawn: a new object, the same id
  -> collected -> a Lost and Found entry (the same object, inert)  <- retrieved
  -> destroyed, with a cause

client projection
  <- Create when relevant, -> Destroy when not (changes nothing)
```

## The state machine

`ItemAuthorityStateMachine.Apply` (`Multiplayer.Core/Items/`, unit-tested) decides every placement
change; `AuthoritativeItemRegistry.ApplyTransition` wraps it and commits. In order:

1. The server actor (0, a dedicated host's own) never takes a hand or inventory placement and never
   becomes an owner.
2. An item in Lost and Found changes only by a forced transition.
3. Only a gadget transition enters or leaves `Installed`.
4. The expected revision must be the current one, or inside the run of revisions that changed only
   object state (`ItemPlacementRevision`), unless forced.
5. A held or carried item's placement changes only from its holder, unless forced.

It then gives an unowned personal item its first taker as owner, sets placement and holder, updates
the owner's retrieval claim, and advances the revision. `Recall` is separate: the owner only, an
essential with a claim, the exact revision. Three registry paths advance the revision without it: a
spatial commit, a Lost and Found collection and a Lost and Found retrieval. A refused change leaves the
record as it was, and the sender gets a correction (`HostCorrection`).

Motion samples have their own ordering (epoch and sequence) and never change placement, holder, owner,
containment or lifecycle.

## Cleanup precedence

Cleanup is the server's, from the strongest obligation to the weakest:

1. Lost and Found membership;
2. a persistent owner;
3. a retrieval claim;
4. cold-container membership;
5. installed, snapped, job, shop or other protected placement;
6. a holder, or a player's interest;
7. an abandoned stock fork;
8. a projection only.

A weaker cleanup never erases a stronger record. Stock-fork cleanup refuses anything in Lost and
Found, with an owner (identity or number), with a claim, or in a protected placement. Unknown player
positions and unclear bindings fail safe: nothing is collected or cleaned.

## Rules every item system keeps

- One canonical item per NetId, with at most one placement and one holder.
- Pickup never duplicates an item, and never transfers an owner.
- An owner changes only through a named, logged transition.
- A non-recallable item never gets a recall claim from an ordinary reserved or locked slot.
- Lost and Found never takes an item from a hand, inventory, container, machine, snap point or other
  protected placement. A nearby player protects a dropped item.
- A departing player never carries off someone else's item, and nothing is saved under two players.
- Vanilla never moves an owned item behind the server's back: vanilla's own Lost and Found, respawn on
  drop and safeguards are off or blocked in a session.
- An inactive object isn't a gone item: lookups go by record, never `FindObjectsOfType`.

None of these happens as a side effect of presentation or possession:

- inventory insertion giving a non-personal item an owner;
- projection activation adopting an authored item;
- interest retirement destroying canonical state;
- a motion sample changing placement or holder;
- a deposit changing an item's owner;
- a container in Lost and Found making entries for its contents;
- stock replenishment duplicating the taken fork under the authored key;
- a stale Unity object becoming authoritative after a deposit or a collection.

## Observability

Each item's debug snapshot (`AuthoritativeItemRegistry`, the F12 overlay, `dvmp_eval`) shows its NetId,
id or key, prefab, placement and holder, owner number and identity, claim, container and Lost and
Found membership, the stock-fork mark, world parent and committed pose, revision and last reason,
`lastMove`, and its lease. Events are listed in [observability.md](../../observability.md). Scenarios
check cleanup as well as disappearance: no record, stored record, storage membership, inventory slot,
NetId binding or client projection may be left after a destructive cleanup
([testing.md](../../testing.md)).
