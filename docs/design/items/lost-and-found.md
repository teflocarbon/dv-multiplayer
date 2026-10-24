# Lost and Found

> Status: being written in R6 of the [item refactor](../item-refactor.md) (2026-10-25), one section per
> PR, not yet proved in a game: that is the end pass (OI-REF-01). Until R6 is done, what this page
> doesn't cover yet is as the legacy [lost-and-found.md](../../legacy/items/lost-and-found.md)
> describes. The ledger it commits to is [ledger.md](ledger.md); vanilla's own Lost and Found is in
> [research/DERAIL_VALLEY_LOST_AND_FOUND_LOGIC.md](../../research/DERAIL_VALLEY_LOST_AND_FOUND_LOGIC.md).

| Side | Code |
| --- | --- |
| Core | `LostItemCollectionPolicy`, `LostItemRegistry`, `LostItemReason` (`Multiplayer.Core/Items/LostAndFound/`) |
| Server | `LostAndFoundServer`, `DeletedCarItems`, `CarDeleteLostAndFoundPatch` (`Multiplayer/Server/Items/LostAndFound/`); `LiveItems.TryMoveToLostAndFound` (`LiveItems.LostAndFound.cs`) |

The server keeps a Lost and Found for every player. **An owned item goes to its owner's Lost and
Found; an unowned item never does.** Owned means the ledger has an owner for it: vanilla's
`BelongsToPlayer` flips in the dumpster, the shop and a withdrawal, so nothing decides on it.

## Ways in

Every way in goes through `LostAndFoundServer.Collect(item, reason, detail)`, which refuses an item
the ledger has no owner for (`item-has-no-owner`) and an id that already has an entry (OI-ID-06,
asked before the ledger moves, LF9).

| `LostItemReason` | When | Code |
| --- | --- | --- |
| `OwnerDistance` | the collection policy: far from its owner, or its owner offline | `LostAndFoundServer.HostTick` |
| `ContainerCleared` | a car is about to be deleted with an owned item in it or on it ([below](#a-deleted-car)); an owned ammo row whose tool went cold without it | `DeletedCarItems`; `PlayerItems.Ammo` |
| `BorrowerLeft` | a player left holding someone else's item | `PlayerItems.SendHome` |
| `InventoryOverflow` | an owned item with no free slot: a leaver's own hand item, a row going live into a taken slot, a hand item at a save | `PlayerItems`; `ItemSaveLoad.Save` |
| `RetrievedByOwner` | the owner retrieved it from their owned-items list | `Retrieval`, through `ServerLedger.SendToLostAndFound` |
| `SaveRecovery`, `GadgetDestinationMissing` | a saved item that can't go back where it was | `ItemSaveLoad.Load`, `ServerLedger.Repair` |

`LostItemReason` is a byte on the wire: append, never reorder.

## A deleted car

A car about to be deleted has its items taken off it first (`DeletedCarItems.Collect`), before
vanilla's delete runs and while the records still name the car. Every way vanilla deletes a car is
patched (`CarDeleteLostAndFoundPatch`, at a low priority, after the players on it are stepped off):
`DeleteCar`, `DeleteTrainCars` (at once or over a few frames) and `DeleteTrainCarsFromTrack`, which
starts vanilla's coroutine itself and so skipped the other two (LF5, OI-LF-11).

What is taken off:

- **Snapped to a point on the car:** an end-of-train lantern on its coupler, an item on a hanger it
  carries. It is unsnapped first: an unsnap gives the item back the parent it had.
- **In its interior:** every record whose home is `TrainInterior` on this car.
- **Attached to it:** every record `Attached` with this car's net ID.

Each item then goes, by the ledger:

- **Owned:** to its owner's Lost and Found (`ContainerCleared`), connected or not (OI-LF-09).
- **Unowned:** into the world where it is, committed by the server (`ItemMoveReason.Detached`, "left
  beside the track") and sent to every player, and physics takes it down beside the track, as
  vanilla puts a deleted car's items back (`TrainCar.InteriorOnDestroy`). It is never destroyed (LF2,
  OI-LF-10). An owned item Lost and Found refuses goes the same way, with a warning.

**An installed gadget** comes off with vanilla's own removal (`GadgetBase.OnTrainCarDestroyed`,
`ForceRemove`), which the gadget code commits into the world at its pose
(`GadgetItemTransitions.CommitRemoved`). On a car being deleted (`NetworkedTrainCar.IsDestroying`)
an owned gadget then goes on to its owner's Lost and Found (`DeletedCarItems.InstalledOffDeletedCar`);
an unowned one stays where it came off.

**A fixture** (OI-FIX) would go back to its spot rather than lie by the track; the fixture list is
empty until the shops work (paused, D4), so today every unowned item lies by the track.

Scenarios: `car-delete-owner` (an owned mug in a cab, its owner online, gone or never here),
`car-delete-attached` (an owned lantern on a coupler and an owned gadget to Lost and Found, an
unowned lantern into the world), `item-bug-car-delete-unowned` (`HOW=car`, `HOW=track`: an unowned
mug stays in the world).
