# Multiplayer Item Ownership: Open Questions

This document records item-ownership behaviour that is technically valid but depends on subjective game-design decisions. It is not a commitment to the current policy.

## Separate concepts

The implementation must continue to distinguish these values:

- **Persistent owner**: the player associated with persistence and Lost and Found.
- **Current possessor**: the player whose hand or inventory currently contains the item.
- **Placement**: world, hand, inventory, container, attached, installed, Lost and Found, or destroyed.
- **Recall entitlement**: whether the persistent owner can forcibly retrieve the item through Derail Valley's item-getter interface.
- **Inventory classification**: Derail Valley fields such as `BelongsToPlayer` and `IsEssential`; these are inputs to policy, not holder identity.

Changing one value must not implicitly change the others unless an explicit policy says it should.

## Current behaviour

At present, picking up or inventorying an item does **not** transfer its persistent owner.

Example:

1. Player A owns an ordinary, non-essential item.
2. Player B takes it into their hand or inventory.
3. Player B becomes the current possessor, while Player A remains the persistent owner.
4. Player A cannot force-recall it because the item is not eligible for the base-game item getter.
5. Lost and Found cannot collect it while it is held or inventoried.
6. If it is dropped, every nearby player protects it from collection.
7. If the owner is far away, no player remains nearby, and the grace period expires, it is collected into Player A's Lost and Found.

Ordinary foreign-owned items do not appear red. Red is reserved for items that another player can actually force-recall.

## Decided disconnect policy

When a player leaves the server, possession does not become persistence. Before saving or
discarding that player's runtime inventory, the host must partition every held, equipped,
inventoried, and contained item by persistent owner:

- items persistently owned by the departing player remain in that player's saved inventory or
  container graph;
- items with a different nonzero persistent owner are removed from the departing player's
  possession and moved exactly once into the original owner's Lost and Found;
- the original owner may be offline; the record is keyed by stable owner identity and becomes
  visible when they next connect;
- unowned items are not assigned to either player merely because of disconnect and require their
  normal world/abandonment policy.

This applies to recallable red items and ordinary non-red borrowed/stolen items alike. Red is a UI
warning about immediate recall entitlement, not a persistence or ownership test. Disconnect
recovery must use canonical persistent ownership exclusively.

The reconciliation must be host-authoritative, idempotent, and complete before the departing
player's inventory projection is serialized or destroyed. It preserves persistent item identity,
tracked state, container edges where valid, and monotonic authority revision; it must not create a
second item or save the same item under both players.

## Decisions still required

### When should ownership transfer?

Options to consider:

- Never transfer automatically; require an explicit gift or transfer action.
- Transfer when the owner deliberately places an item into another player's inventory.
- Transfer after the recipient possesses the item for a configured duration.
- Transfer when an ordinary item is abandoned and then claimed.
- Treat purchased items differently from starting items, rewards, job documents, and world props.

Pickup alone is ambiguous: it could mean theft, borrowing, cooperation, cleanup, or gifting.

### What should Lost and Found mean for borrowed items?

Questions:

- Should an abandoned borrowed item return to its original owner or its most recent possessor?
- Should the last possessor receive a temporary recovery opportunity before the persistent owner?
- Should explicit gifts immediately change the Lost and Found destination?
- What happens when the persistent owner is disconnected for a long time?
- Should another nearby player protect the item indefinitely, or only while actively interacting with it?

### Which creation paths establish ownership?

Each path needs an explicit answer:

- Starting inventory
- Shop purchase
- Client adoption of an existing local item
- Job reward or generated document
- Item spawned by a machine or printer
- World-authored personal item
- Item removed from a container
- Admin/debug spawn
- Save restoration
- Lost and Found restoration

For each path, determine whether it creates a persistent owner, an unowned item, or inherits ownership from another entity.

### How should containers affect ownership?

Questions:

- Does depositing an item into another player's container transfer ownership?
- Does container ownership merely control access while contained items retain individual owners?
- When a container enters Lost and Found, do all descendants follow the container owner or their own owners?
- How are mixed-owner container contents presented and restored?
- How will a future friend/ACL system affect possession without changing ownership?

### What should players see?

Potential presentation rules:

- Red: another player can forcibly recall this item.
- Owner label: item has a persistent owner but is not recallable.
- Gift/transfer action: explicitly changes persistent ownership.
- Borrowed indicator: current possessor differs from persistent owner.
- Lost and Found destination shown in debug UI and, if useful, normal UI.

The red style must not be used as a generic foreign-ownership indicator because that incorrectly implies a recall threat.

## Invariants to preserve

Regardless of the chosen policy:

- One canonical item exists per nonzero NetId.
- An item has at most one current placement and possessor.
- Pickup does not accidentally duplicate an item.
- Persistent ownership changes only through a named, observable authority transition.
- Recall entitlement is independent from persistent ownership.
- A non-recallable item cannot acquire a recall claim from an ordinary reserved or locked inventory slot.
- Lost and Found never removes an item from a hand, inventory, container, machine, snap point, or another protected placement.
- A nearby player protects a dropped item from distance collection under the current safety policy.
- Unknown player position fails safe rather than deleting or collecting an item unexpectedly.
- Authority revision increases for every accepted ownership or placement transition.
- Rejected transitions do not mutate canonical authority.
- Disconnect persistence cannot save a foreign-owned item into the departing possessor's inventory.
- Each foreign-owned item recovered during disconnect produces exactly one original-owner Lost-and-Found record.

## Required observability for future decisions

Ownership-related events should expose:

```text
itemNetId
persistentOwnerPlayerId
previousPersistentOwnerPlayerId
placementPlayerId
previousPlacementPlayerId
placement
transitionReason
recallEligible
inventoryClaimPlayerId
inventoryClaimSlot
inventoryClaimFlags
authorityRevision
```

Any future transfer should emit explicit events such as:

```text
item.ownership-transfer-requested
item.ownership-transfer-accepted
item.ownership-transfer-rejected
```

The transition reason should distinguish gifting, purchase, administrative reassignment, save restoration, and any automatic abandonment policy.

## Scenario matrix to decide and test

| Scenario | Current result | Decision needed |
| --- | --- | --- |
| A lends an ordinary item to B | A remains owner; B possesses it | Is explicit return sufficient? |
| A gives an ordinary item to B | Indistinguishable from lending | Define an explicit gift path |
| B drops A's item and stays nearby | Item remains in world | Confirm protection duration |
| B drops A's item and everyone leaves | Item goes to A's Lost and Found | Confirm destination |
| A disconnects while item is abandoned | Collection is protected because owner position is unknown | Decide long-term offline policy |
| B holds A's recallable item | A remains owner and may recall; B sees red | Confirm intended theft/recall behaviour |
| B holds A's non-recallable item | A remains owner; B does not see red | Decide whether a neutral owner label is wanted |
| B disconnects while holding or storing A's item | Item moves to A's Lost and Found, including non-red items | Decided |
| B purchases an item | Purchase path ownership is not yet fully audited | Define shop ownership hook |
| Mixed-owner items are stored in a container | Individual ownership should remain explicit | Define container and Lost and Found rules |

## Deferred implementation ideas

Do not implement these until the policy is chosen:

- Explicit gift/ownership-transfer UI
- Abandonment timers that transfer ownership
- Ownership transfer through player-owned containers
- Friend or ACL-based borrowing
- Offline-owner expiry rules
- Purchase-specific ownership transfer
