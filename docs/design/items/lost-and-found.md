# Lost and Found

> Status: written in R6 of the [item refactor](../item-refactor.md) (#378 to #387, 2026-10-25), not
> yet proved in a game: that is the end pass (OI-REF-01). It replaces the legacy
> [lost-and-found.md](../../legacy/items/lost-and-found.md), which is evidence of the old behaviour
> only. An owner sends an item here from their list ([owned-list.md](owned-list.md)), and a full
> inventory's routes here are [inventory.md](inventory.md). The ledger it commits to is [ledger.md](ledger.md); vanilla's own Lost and Found is in
> [research/DERAIL_VALLEY_LOST_AND_FOUND_LOGIC.md](../../research/DERAIL_VALLEY_LOST_AND_FOUND_LOGIC.md).

| Side | Code |
| --- | --- |
| Core | `LostItemCollectionPolicy`, `LostItemRegistry`, `LostItemReason` (`Multiplayer.Core/Items/LostAndFound/`) |
| Server | `LostAndFoundServer`, `PlayerItems.ToLostData` and `FromLostData` (`Inventory/PlayerItems.Lost.cs`), `DeletedCarItems`, `CarDeleteLostAndFoundPatch` (`Multiplayer/Server/Items/LostAndFound/`); `LiveItems.TryMoveToLostAndFound`, `LiveItems.KeeperOf` (`LiveItems.LostAndFound.cs`) |

The server keeps a Lost and Found for every player. **An owned item goes to its owner's Lost and
Found; an unowned item never does,** except a job booklet, kept for the player it was issued to
([below](#job-papers)). Owned means the ledger has an owner for it: vanilla's `BelongsToPlayer`
flips in the dumpster, the shop and a withdrawal, so nothing decides on it. Whose Lost and Found an
item goes to is its keeper (`LiveItems.KeeperOf`): its owner, or a booklet's issued player. A binned
item is never collected: it is going, its binner's choice ([vanilla-storage.md](vanilla-storage.md#bins)).

## Ways in

Every way in goes through `LostAndFoundServer.Collect(item, reason, detail)`, which refuses an item
with no keeper (`item-has-no-owner`) and an id that already has an entry (OI-ID-06,
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

## The collection

The server's sweep (`LostAndFoundServer.HostTick`, the pure rule `LostItemCollectionPolicy`) collects
an owned item lying in the world 200 m or more from its owner, unless another player is within
100 m, once it has been so for a 10 s grace (OI-LF-01). **An offline owner counts as far** (OI-LF-02):
their items are collected from the world, a train's interior, a shelf (a shed's `ItemStaticParent`),
a car's end or a snap point, under the same protection and grace. With nobody connected nothing is
collected (OI-LF-03). Unknown state protects: an owner whose position isn't known, or no player's.

Where a player is comes from their game's position packets (`ServerPlayer.ApplyPosition`):

- **Their position is in the frame it was sent in.** On a car a game sends where the player is in
  the car's frame, and every packet names the car they are on, with a new position or not. The
  server reads the last position in the frame of the car it came with (`positionCarId`), not the
  car the latest packet names: read in a just-boarded car's frame, a player standing in a cab was
  hundreds of metres off, and their protection gone (OI-JOIN-11).
- **A just-joined owner's position counts a grace period after the first one their loaded game
  sent** (`FirstPositionAfterLoadAt`), not after the load itself: before it a position can be from
  before their game put them at their saved spot, and after a restart on a dedicated host that took
  longer than the grace, so a rejoined owner's ground item was collected (OI-JOIN-10). A game sends
  a position only when its player moves, so one standing still since its load counts from a minute
  after it (`ServerPlayer.PositionSettlesFrom`, K5). The host's own player counts at once.

Scenarios: `lost-and-found`, `offline-lost-and-found`, `owner-offline` (`WHERE=cab`, `WHERE=shelf`).

## Leaving

A player who leaves (a quit or a crash, `PlayerItems.Leave`) takes nothing with them and leaves
nothing lying about (OI-JOIN-06, D5): their own or nobody's hand item goes into a free slot of their
inventory, its claim first, and goes cold with the rest; with no free slot an owned one comes here
(`InventoryOverflow`) and an unowned one drops at their feet. Someone else's item, in their hand or
inventory, comes to its owner's Lost and Found at once (`BorrowerLeft`), whether its owner is online
or not (OI-JOIN-04).

Scenarios: `leave-holding` (own, unowned and borrowed, at a quit and a crash), `leave-full-inventory`,
`borrowed-disconnect`, `cold-homes`.

## A refused collection

A collection that fails after the record has moved (Lost and Found refuses the entry, or its
projection into inert storage throws) is rolled back whole (`LiveItems.RollbackLostAndFoundCollection`,
J4): the record is restored from a copy taken before the move, the ledger goes back to its home, and
every game is sent it. One that was attached to a car or loaded in a tool can't go back where it
was, its collection having taken it off, so it goes into the world where it is, as a deleted car's
unowned item does (`DeletedCarItems.IntoWorld`).

Scenario: `lost-and-found-data`.

## Kept as data

An entry is data on the server (OI-COLD-08, D12): no object until it is retrieved or recalled. Every
way in collects an object as before (`Collect`, or the load's `RestoreEntry`), and the scan after it
(`ReconcileCanonicalPlacements`, every 24 ticks and before a list or a save) hands it to data
(`PlayerItems.ToLostData`): its state is a row Lost and Found keeps, by its id, and its record and
NetId stay, as a cold inventory row's do. It waits while its saved state is still loading
(`ItemSaveLoad.IsRestorePending`) and while its ammo can't go with it. Its ammo goes to data with it
(`PlayerItems.AmmoRows`). It stays an object when it couldn't be made again from its row
(`PlayerItems.CanBeLostData`, J3): its prefab is one this game doesn't list (a mod item without
its mod), or a job paper that can't be a row. So does one still in an active inventory slot (a VR
belt's, J8).

- **Out:** a retrieval, a recall (`CarryRecall`, the repair's `IntoSlot`) and anything else that names
  it (`PlayerItems.WithObject`) has the object made again from its row under its NetId, inert in Lost
  and Found (`LostAndFoundServer.ObjectOf`, `PlayerItems.FromLostData`), and goes on as with an
  object. One made for a request that's then refused goes back to data at the next scan. One that
  can't be made (its prefab gone) is refused (`lost-item-not-made`) and stays.
- **Saved** from its row (`ItemSaveLoad.OwnedRow`), and the one-home check reads the row
  (`ObservedSource.LostAndFoundRow`).
- **A booklet** kept as data goes when its job ends, as its object would (the scan asks
  `JobPaperRows.Wanted`).
- **Its owner's silhouette** of a lost essential, their recall, is sent from its record
  (`SendDataEssentials`): an owner who joins after the collection has no copy for the tombstone.
- **Clients** keep what they had: a game that had the item when it was collected keeps an inert copy
  (`EnforceLostAndFoundProjection`), as before. When an entry kept as data goes (`PlayerItems.LostDataGone`) every
  game is sent its destroy before its NetId is freed, so a reused NetId never names that copy (J7).
- **Its id** is the entry's: the ledger gives a second item asking for it a new one
  (`ServerLedger.ClaimPersistentId`, J6), as it does for an id a live object holds.

## A deleted car

A car about to be deleted has its items taken off it first (`DeletedCarItems.Collect`), before
vanilla's delete runs and while the records still name the car. Every way vanilla deletes a car
prepares it first (`CarSpawner.PrepareTrainCarForDeleting`): `DeleteCar`, `DeleteTrainCars` (at once
or over a few frames) and `DeleteTrainCarsFromTrack`, which starts vanilla's coroutine itself (LF5,
OI-LF-11). The take-off is a prefix there (`CarDeleteLostAndFoundPatch`, at a low priority, after
the players on the car are stepped off).

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

One car at a time, as each is prepared (J1): taken off once for a whole list deleted over a few
frames, an item put into the world could fall back into a car not deleted yet and go with it. An
installed gadget's removal comes as vanilla destroys the car's interior, after the car's NetId is
gone on the slow path, so the cars being deleted are kept by their GUID until they are
(`DeletedCarItems.Deleted`), and the gadget's record names its car by GUID
(`AttachedCarPersistentId`, J2). A take-off that throws is logged and the rest go on (J5).

Scenarios: `car-delete-owner` (an owned mug in a cab, its owner online, gone or never here),
`car-delete-attached` (an owned lantern on a coupler and an owned gadget to Lost and Found, an
unowned lantern and an unowned mug in the cab into the world; `HOW=slow`: the several-car delete),
`lost-and-found-data`, `item-bug-car-delete-unowned` (`HOW=car`, `HOW=track`: an unowned
mug stays in the world).

## Attached and snapped items

An item on a coupler (an end-of-train lantern) or a snap point (a hanger) goes to Lost and Found off
it: `LiveItems.TryMoveToLostAndFound` takes it off first (`NetworkedItem.LeaveSnapPoint`: unsnapped
and into the world's parent, since an unsnap gives back the parent it had, which may be the car),
and every client's inert copy comes off too (`EnforceLostAndFoundProjection`), so no hidden lantern
holds the coupler. That serves each way in:

- **Collection:** an offline owner's attached and snapped items are collected like their items in a
  cab or on a shelf (OI-LF-04); a snap no longer protects them. An online owner's stay where they are.
- **Retrieval:** the owner retrieves one from their list like an item lying anywhere (`Collect`,
  OI-LIST-14). An installed gadget's retrieval waits on gadgets (OI-LIST-15, paused).
- **A deleted car:** [above](#a-deleted-car).

The owned-items list words an attached item "on the end of car L-042" (OI-LIST-06).

Scenario: `attached-offline`.

## Job papers

A booklet, an overview and a report are the job's, never owned (D5, OI-ID-12): no ledger entry, not
in the owned list. A booklet is still somebody's to lose: **a lost booklet goes to the Lost and
Found of the player it was issued to** (OI-LF-17), by identity, so it reaches them offline or after
a rejoin. Its entry's owner is that player (`LiveItems.KeeperOf`, from the job's
`JobBookletIssuanceIndex`), the collection policy weighs that player's distance and presence, and
only they retrieve it: the retrieval asks the entry (`LostAndFoundServer.KeeperOf`), not the ledger.
A booklet on a deleted car goes there too. An overview or a report nobody was issued stays where it
is.

A booklet in Lost and Found isn't saved, as one lying about isn't (`ItemSaveLoad.IsUnsavedJobPaper`):
after a restart the job is still on, and its player summons another at a validator.

Scenario: `booklet-not-owned`.
