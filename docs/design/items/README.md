# Items

> Status: R3 of the [item refactor](../item-refactor.md), 2026-10-24. The code layout and the names
> on this page are built, and so is the ledger deciding every owned item's owner and home with live
> state following it (R3). One save (R4) and the rest are planned, and each part says which PR
> builds it. Every requirement is a row in [the checklist](../../item-overhaul-checklist.md). Until a
> subsystem's doc below is written, the [legacy docs](#legacy-docs) are the account of what its code
> does today.

## The guarantee

Built in R3 for the ledger and inventories; the save (R4), containers (R5) and Lost and Found (R6) finish it:

> Every owned item is, at every moment, in exactly one known home. That home is saved, and the item
> comes back there on reload. Anything that would leave it nowhere sends it to its owner's Lost and
> Found.

The homes: `Slot(holder, slot)`, `Hand(holder, hand)`, `World`, `TrainInterior(car)`, `Shelf(key)`,
`Container(container, slot)`, `LostAndFound`, `Installed(anchor)`, `Attached(car,
front)`, `Snapped(parent)`, `Magazine(tool, slot)` (loaded in a live tool, R5) and `Unloadable` (its prefab is gone, R4) ([the plan](../item-refactor.md#the-guarantee) says what each names).
A ledger entry's home is `Home`, its kind `HomeKind` (`Multiplayer.Core/Items/Ledger/LedgerEntry.cs`), exact since R3
([ledger.md](ledger.md)).

## Three names

Used in code and docs, and nothing else for these things:

| Name | In the code | What it is |
| --- | --- | --- |
| **ledger** | `ItemLedger` (Core `Items/Ledger`), `ServerLedger` (Server `Items/Ledger`) | Owned items only, keyed by the item's GUID, saved. Since R3 it alone decides each one's **owner** and **home** ([ledger.md](ledger.md)) |
| **live state** | `LiveItems` and its `LiveItem` records (Server `Items/LiveState`), `NetworkedItem` (Shared `Items/LiveState`), `ItemStateMachine` (Core `Items/LiveState`) | Every item, owned or not, right now: NetId, pose, physics, parent, the holder this second, what each client is shown, revisions |
| **saving and loading** | `ItemSaveLoad`, `SavedItemRow`, `ItemsSaveCodec`, `ItemIdentity` (Server `Items/SaveLoad`), `ItemLoadPlanner` (Core `Items/SaveLoad`) | Writes every item into one key of the save, `Multiplayer.Items`, and loads it once before anyone logs in, into the ledger and live state ([save-load.md](save-load.md)) |

**On a conflict, the ledger wins on owner and home, and live state on pose and physics** (R3,
[live-state.md](live-state.md)).

## Subsystems

Each subsystem is a folder `Items/<Subsystem>/` in each side that has code for it, with the
namespace of its folder: `Multiplayer.Core/Items/` (rules, no game references),
`Multiplayer/Server/Items/`, `Multiplayer/Client/Items/` and `Multiplayer/Shared/Items/`. Tests
mirror them in `Multiplayer.Core.Tests/Items/` and `Multiplayer.Protocol.Tests/Items/`.

| Subsystem | What | Core | Server | Client | Shared | Doc |
| --- | --- | --- | --- | --- | --- | --- |
| `Ledger` | owners and homes of owned items; the invariant | ✓ | ✓ | | | [ledger.md](ledger.md) |
| `LiveState` | every item now: the record, the state machine, the item component | ✓ | ✓ | ✓ | ✓ | [live-state.md](live-state.md) |
| `SaveLoad` | the item save, item identity in the save | ✓ | ✓ | | | [save-load.md](save-load.md) |
| `Inventory` | slots, the belt, claims as a client shows them | ✓ | ✓ | ✓ | ✓ | [inventory.md](inventory.md) |
| `Essentials` | starting items and the issuer (the claim rules are Core `Inventory` and `Ledger`) | | ✓ | | | [essentials.md](essentials.md) |
| `LostAndFound` | collection, entries, retrieval, a deleted car's items | ✓ | ✓ | ✓ | | [lost-and-found.md](lost-and-found.md) (R6) |
| `Containers` | cold containers | ✓ | ✓ | ✓ | | [containers.md](containers.md) |
| `OwnedList` | the owned-items page and retrieval | ✓ | ✓ | ✓ | | [owned-list.md](owned-list.md) |
| `Access` | `ItemAccess`, fixtures | ✓ | ✓ | | | [access.md](access.md) |
| `WorldItems` | scene-authored items, the catalogue, static parents, office stock | ✓ | ✓ | ✓ | ✓ | [world-items.md](world-items.md) (R7) |
| `Projection` | interest and what each client is shown | ✓ | ✓ | ✓ | | [projection.md](projection.md) (R7) |
| `Physics` | leases, samples, settlement, throws, fall rescue, the terrain guard | ✓ | ✓ | ✓ | ✓ | [physics.md](physics.md) (R7) |
| `TrainWake` | items in moving trains (the item's own part is `NetworkedItem.TrainWake`, in Shared `LiveState`) | ✓ | ✓ | | | [train-wake.md](train-wake.md) (R7) |
| `ChangeCauses` | why an item moved or went away | ✓ | ✓ | ✓ | ✓ | [change-causes.md](change-causes.md) |
| `VanillaStorage` | the guards that keep DV's own storage, Lost and Found and respawn from moving an item | ✓ | ✓ | | ✓ | [vanilla-storage.md](vanilla-storage.md) (R6) |
| `Behaviours` | one item type's own sync: flashlight, lantern, lighter, shovel, page book, money, the grabber | ✓ | ✓ | ✓ | ✓ | `behaviours.md` (R8) |

A partial class can't span namespaces, so each one sits whole in the subsystem most of its code
belongs to: `NetworkedItem` and `NetworkedItemManager` in Shared `LiveState`, `ServerItemManager`
and `ClientItemManager` in `Projection`. The parts that belong elsewhere become classes of their own
in the PR that changes them: `ServerItemManager.PlayerItems` went to `Inventory` (`PlayerItems`) in R3, the
fall-rescue, terrain-guard and world-integration parts to `Physics` and `WorldItems` in R7
(`FallRescueServer`, `FallRescueClient`, `ItemFallRescue`, `ItemTerrainGuard`, `TrainItemImpactWitness`,
`AuthoredItems`, `AuthoredProjections`).

The item packets and their data are in `Multiplayer/Shared/Protocol` (`Packets/Items/`,
`Data/Items/`): five packets since R2, described in [wire.md](wire.md).

## Legacy docs

These describe the item system before the refactor, with the class and file names from before R0
([the rename map](../item-refactor.md#rename-map) translates them). They are evidence of today's
behaviour, not design. They are in `docs/legacy/items/` (moved 2026-10-24), each with a banner naming its replacement:
[item-lifecycle.md](../../legacy/items/item-lifecycle.md), [owned-items.md](../../legacy/items/owned-items.md),
[lost-and-found.md](../../legacy/items/lost-and-found.md), [container-storage.md](../../legacy/items/container-storage.md),
[item-world-sync.md](../../legacy/items/item-world-sync.md), [item-spatial-physics.md](../../legacy/items/item-spatial-physics.md),
[train-item-wake.md](../../legacy/items/train-item-wake.md), [item-persistence.md](../../legacy/items/item-persistence.md),
[item-change-causes.md](../../legacy/items/item-change-causes.md),
[item-ownership-open-questions.md](../../legacy/items/item-ownership-open-questions.md), and, moved already,
[item-sync-review.md](../../legacy/items/item-sync-review.md).
