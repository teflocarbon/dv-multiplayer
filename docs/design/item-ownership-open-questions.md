# Multiplayer Item Ownership: Open Questions

This document records item-ownership behaviour that is technically valid but depends on subjective game-design decisions. It is not a commitment to the current policy.

Implemented lifecycle and ownership-establishment rules are canonicalized in
[`item-lifecycle.md`](item-lifecycle.md). This document should
only hold unresolved or explicitly deferred game-design choices, plus a short record of the ones
since decided (checked against the code on 2026-10-14).

The ownership model is being rebuilt on a server-owned ledger of owned items, with one id per item
for life: [`owned-items.md`](owned-items.md) (designed 2026-10-21, not built). Most of the questions
below were decided for it, and are recorded under
[Decided for the ledger](#decided-for-the-ledger-2026-10-21). "Current behaviour" and "Decided"
describe the code as it is until that work lands.

## Separate concepts

The implementation must continue to distinguish these values:

- **Persistent owner**: the player associated with persistence and Lost and Found.
- **Current possessor**: the player whose hand or inventory currently contains the item.
- **Placement**: world, hand, inventory, container, attached, installed, Lost and Found, or destroyed.
- **Recall entitlement**: whether the persistent owner can forcibly retrieve the item through Derail Valley's item-getter interface.
- **Inventory classification**: Derail Valley fields such as `BelongsToPlayer` and `IsEssential`; these are inputs to policy, not holder identity.

Changing one value must not implicitly change the others unless an explicit policy says it should.

## Current behaviour

Picking up or inventorying an item does **not** transfer an existing persistent owner. An unowned
personal (`BelongsToPlayer`) item is the exception: the first player to put it in their hand or
inventory becomes its owner (see Decided).

Example:

1. Player A owns an ordinary, non-essential item.
2. Player B takes it into their hand or inventory.
3. Player B becomes the current possessor, while Player A remains the persistent owner.
4. Player A cannot force-recall it because the item is not eligible for the base-game item getter.
5. Lost and Found cannot collect it while it is held or inventoried.
6. If it is dropped, every nearby player protects it from collection.
7. If the owner is connected and far away, no player remains nearby, and the grace period expires,
   it is collected into Player A's Lost and Found. While A is offline it is not collected.

Ordinary foreign-owned items do not appear red. Red is reserved for items that another player can actually force-recall.

## Decided

- **Unowned personal items** belong to the first player who puts them in their hand or inventory;
  nonpersonal items never gain an owner. Pickup never transfers an existing owner.
  `ItemAuthorityStateMachine.Apply` (`MayEstablishPersistentOwner`), 9e641a2. Known gap: while
  the owner is offline their owner ID is unbound, so the item looks unowned and the next player
  to take it is recorded as owner ID (the owner identity stays the original owner's, and their
  ID is restored when they log in).
- **Creation paths:**
  - starting inventory: given on a player's first join only and owned by them through the
    temporary adoption path (`SaveGameDataPacketBuilder.Create`,
    `NetworkServer.StarterKitsGiven`, 2962fa7);
  - shop purchase by a client: spawned by the host at the shop, owned by the buyer
    (`NetworkServer.SpawnShopPurchase`, f796ca8); the host's own purchases are vanilla spawns and
    unowned until someone takes them;
  - world-authored personal item: first taker, as above;
  - item removed from a container: keeps its stored owner
    (`AuthoritativeItemRegistry.RegisterMaterializedColdItem`);
  - save restoration: the saved owner identity (`WorldItemPersistenceRestore`);
  - Lost and Found restoration: the record's owner.
- **Containers** control access only and are private to their owner. A deposit needs the depositor
  to possess and own the item, so contents are never foreign-owned and deposit never changes
  ownership. A lost container is one Lost-and-Found root entry with its contents untouched. See
  [`container-storage.md`](container-storage.md).
- **Offline owners:** Lost-and-Found records and item records are keyed by stable identity, unbound
  when the owner leaves and rebound when they log in (`LostItemRegistry.UnbindOwner`/`RebindOwner`
  via `NetworkedLostAndFoundManager.OnOwnerDisconnected`/`OnOwnerConnected`, e0e9781;
  `AuthoritativeItemRegistry.RebindPersistentOwner`).
- **Red** means another player can recall the item (`InventoryPresentationPlanner`).
- **Disconnect:** the host drops the departing player's hand item, and every item in their
  inventory that another player owns, where it is (`DropHeldItemsOfDepartingPlayer` in
  `Server/Items/ServerItemManager.PlayerItems.cs`, 2d52799; the `borrowed-disconnect` scenario). Items they own stay keyed to their identity, are saved
  with the host's game, and come back into their inventory on rejoin. A dropped borrowed item then
  follows ordinary Lost-and-Found collection: it reaches the owner only if the owner is connected,
  nobody is near it, and the grace period passes. This replaces the earlier written policy of
  moving it straight into the owner's Lost and Found; whether to do that is still open (below).
  Known gaps: when the owner is offline at the time, their owner ID is already unbound, so the
  item is not recognised as borrowed and stays in the departing player's saved inventory; unowned
  items in the inventory also stay with them.

## Decided for the ledger (2026-10-21)

Decided with the user for [`owned-items.md`](owned-items.md). None of this is built yet.

- **One id for life.** A random GUID in the save and the server's records, and the per-session NetId
  on the wire.
  - Uniqueness comes from detecting a duplicate when an item registers: keep the first, re-mint the
    copy, log it.
  - Lost and Found and cold-container records use the item's id instead of minting their own.
- **Keyed by identity.** Owners and holders are identities everywhere on the server. Player numbers
  stay on the wire only, and nothing is unbound on disconnect.
- **Retrieval.** An owner can retrieve any owned item from their list:
  - from anywhere, even another player's hand;
  - an installed gadget from any car, at any time;
  - it goes to the owner's Lost and Found.
- **Borrower leaves.** Another player's item they carry goes straight to its owner's Lost and Found,
  whether the owner is online or not.
- **Offline owners count as far away.** Their items are collected under the standard rules (nearby
  players protect; grace period), except items with a player, in a container, installed or already
  in Lost and Found. That includes train interiors and shelves. An online owner's are collected from
  the world only, as now.
- **No expiry** for an absent player's items.
- **Found items stay unowned.** A non-personal item a player keeps is saved while carried but never
  owned.
- **Used-up items** leave the owner's list. The ledger keeps the lineage (what replaced what).
- **A destroyed essential** is issued again at the owner's next join, only when the ledger says its
  life ended.
- **Job papers** belong to the job, not the ledger. A lost booklet goes to the Lost and Found of the
  player it was issued to.
- **Red** means "its owner can take it back", so it now marks every item another player owns, online
  or not.

## Decisions still required

### When should ownership transfer?

Options to consider:

- Never transfer automatically; require an explicit gift or transfer action.
- Transfer when the owner deliberately places an item into another player's inventory.
- Transfer after the recipient possesses the item for a configured duration.
- Transfer when an ordinary item is abandoned and then claimed.

Pickup alone is ambiguous: it could mean theft, borrowing, cooperation, cleanup, or gifting.

### What should Lost and Found mean for borrowed items?

Decided for the ledger (above):

- a borrower's departure sends the item straight to its owner's Lost and Found;
- an abandoned item always goes to its owner, never its last possessor, and the possessor gets no
  recovery window;
- offline owners' items are collected, and never expire;
- a nearby player protects an item while near, as now.

Still open: whether an explicit gift changes the destination at once. It would, if gifts change the
owner (see the question above).

### Which creation paths establish ownership?

Decided (2026-10-21, for the ledger):

- job documents are the job's, not owned;
- an admin `/give` is owned by its target, as `AdminItemGrants` already does;
- the starter kit, essentials and licence papers become server-made, which removes temporary
  adoption (owned-items PRs 4 and 5);
- a replacement vanilla makes while using an item up is owned by the used item's owner.

Still undecided:

- Item spawned by a machine or printer (recommended: owned by the player who operated or paid)

For each path, determine whether it creates a persistent owner, an unowned item, or inherits ownership from another entity.

### How should containers affect ownership?

- How will a future friend/ACL system affect possession without changing ownership? Planned: one
  server-side decision point, `ItemAccess.Decide` ([owned-items.md](owned-items.md#permissions)), whose
  default policy is today's rules. The permissions system replaces the policy.

### What should players see?

Planned:

- a player's own list of everything they own, with where each item is and a Retrieve button
  ([owned-items.md](owned-items.md#the-owned-items-list));
- red for every item another player owns.

Still open from the list below: an explicit gift action.

Potential presentation rules:

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
- Disconnect persistence cannot save a foreign-owned item into the departing possessor's inventory
  (holds while the owner is connected; see the gap under Decided).
- Disconnect never creates a second copy of a borrowed item or saves it under both players.

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

| Scenario | Current result | Decided (owned-items, not built) |
| --- | --- | --- |
| A lends an ordinary item to B | A remains owner; B possesses it | A can retrieve it from B any time, to A's Lost and Found |
| A gives an ordinary item to B | Indistinguishable from lending | Open: an explicit gift path |
| B drops A's item and stays nearby | Item remains in world | Protected while B is near, as now |
| B drops A's item and everyone leaves | Item goes to A's Lost and Found if A is connected | A's Lost and Found, A online or not |
| A disconnects while item is abandoned | Not collected: A's owner ID is unbound until A returns | Collected into A's Lost and Found under the standard rules, also from trains and shelves; never expires |
| B holds A's recallable item | A remains owner and may recall; B sees red | Unchanged; red |
| B holds A's non-recallable item | A remains owner; B does not see red | A can retrieve it; B sees red |
| B disconnects while holding or carrying A's item | Dropped where B was; reaches A's Lost and Found by ordinary collection | Straight to A's Lost and Found |
| B disconnects carrying A's item while A is offline | Stays in B's inventory and save | Straight to A's Lost and Found; migration fixes old saves |
| B purchases an item | B owns it (client purchase); a host purchase goes to the first taker | The buyer owns it, the host's player too |
| B stores items in a container | Only B's own items can go in; ownership unchanged | Unchanged |

## Deferred implementation ideas

Do not implement these until the policy is chosen:

- Explicit gift/ownership-transfer UI
- Abandonment timers that transfer ownership
- Ownership transfer through player-owned containers
- Friend or ACL-based borrowing (through `ItemAccess`, when the permissions system lands)
- ~~Offline-owner expiry rules~~: decided 2026-10-21, nothing expires
