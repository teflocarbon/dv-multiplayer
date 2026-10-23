# Lost and Found

> Status: as built, checked against the code on 2026-10-23 (`items/lineage`). Vanilla's own Lost and
> Found is in [research/DERAIL_VALLEY_LOST_AND_FOUND_LOGIC.md](../research/DERAIL_VALLEY_LOST_AND_FOUND_LOGIC.md).
> How ownership works is in [`owned-items.md`](owned-items.md).

The server keeps a Lost and Found for every player. An owned item left lying far from its owner, or
anywhere while its owner is offline, is collected into it. The owner retrieves it into their
inventory from the career manager's Lost Items page.

Vanilla's Lost and Found storage on the server is only an inert copy: the server's entries
(`LostItemRegistry`, in `NetworkedLostAndFoundManager`) decide what is in it. Clients never run
vanilla's collection.

## What gets collected

`LostItemCollectionPolicy.Evaluate` (`Multiplayer.Core/Items/LostAndFound.cs`, unit-tested) decides for
each item, every 24 ticks (`HostTick`). Unknown state always protects the item.

1. **Ineligible:** unowned (owner number 0); not a personal item (`BelongsToPlayer`); a licence
   paper; not in a place it can be collected from; grabbed, snapped or in a machine.
2. **Places:** an online owner's items only from `World`. An offline owner's (the absent owner number,
   [owned-items.md](owned-items.md#the-ledger-and-the-records)) from `World`, `TrainInterior` and
   `StaticParent` too: offline counts as far away (decided 2026-10-21). Attached and snapped items
   aren't collected.
3. **Protected:** for an online owner, their position unknown, or within the owner distance; then,
   for anyone, no player position known, or another player within the nearby distance.
4. **Grace:** the item must stay eligible for the grace period, then it is collected (`OwnerDistance`).

| Setting | Default | Range |
| --- | --- | --- |
| `LostItemOwnerDistance` | 200 m | 50–2000 m |
| `LostItemNearbyPlayerProtectionDistance` | 100 m | 10–500 m |
| `LostItemCollectionGraceSeconds` | 10 s | 1–120 s |

**Positions.** An owner's position counts once they have been loaded for the grace period, and
another player's once they have sent one (the host's own player always has one). Before that a
player's position reads as the origin, and a just-joined owner's items on the ground were collected
seconds after they loaded (found 2026-10-21). With nobody connected, nothing is collected.

So: log off with your toolbox on the platform or your lantern in your cab, and both are in your Lost
and Found 10 s later unless another player is within 100 m. An online owner's cab items stay where
they are.

## Other ways in

Each goes through `NetworkedLostAndFoundManager.Collect(item, reason, detail)`:

| `LostItemReason` | When | Code |
| --- | --- | --- |
| `OwnerDistance` | the policy above | `HostTick` |
| `ContainerCleared` | a car is about to be deleted: a player's item in its interior goes to its owner, anything else is destroyed with the car (`DestroyedWithCar`) | `CollectFromDeletedCar`, from `CarDeleteLostAndFoundPatch` |
| `BorrowerLeft` | a player left carrying someone else's item ([owned-items.md](owned-items.md#joining-rejoining-and-leaving)) | `ServerItemManager.DropHeldItemsOfDepartingPlayer` |
| `RetrievedByOwner` | the owner retrieved it from their owned-items list | `OwnedItemRetrieval`, through `OwnedItemsServer.SendToLostAndFound` |
| `InventoryOverflow`, `GadgetDestinationMissing`, `PurchasedDeliveryFallback`, `SaveRecovery` | reserved; nothing collects with them yet | |

`LostItemReason` is a byte on the wire: append, never reorder.

`Collect` refuses an item that isn't personal, is still in the server's own inventory, or that the
registry won't move (`AuthoritativeItemRegistry.TryMoveToLostAndFound`; a place other than `World`
needs a reason other than `OwnerDistance`, or an absent owner).

## Collection

1. The record moves to `LostAndFound` and its revision advances, so any spatial lease's late samples
   and settlements fail (the lease isn't revoked).
2. An entry is added under the item's own id (`LostItemRecord`): owner number and identity, prefab,
   display name, authored key, the claim's slot and flags, reason and time.
3. The item is made inert in vanilla's Lost and Found storage on the server
   (`StorageIntegration.ProjectLostAndFound`). It keeps its NetId and its object.
4. Every player forgets it (nearby, known, acknowledged), and a Destroy carrying the collection's
   cause goes to the clients. A client that knew the item keeps an inactive inert copy
   (`NetworkedItemClient.ApplyLostAndFoundProjection`), which the item's final Destroy ends.
5. The ledger moves the entry to `LostAndFound` (`OwnedItemsServer.OnRemoved`), and the owner, if
   connected, gets a fresh list.

If a step fails, the registry change is rolled back (`RollbackLostAndFoundCollection`). A container
goes as one entry with its contents still in its cold graph, never materialized or collected one by
one ([container-storage.md](container-storage.md)).

## Retrieval

The Lost Items page (`LostItemsCareerManagerScreen`) asks for the list
(`ServerboundLostItemsRequestPacket`, `ClientboundLostItemsSnapshotPacket`) and retrieves an entry by
its per-session handle (`ServerboundLostItemRetrievePacket`, `ClientboundLostItemRetrieveResultPacket`).
The host's own player goes in-process through its local session (`LostAndFoundExchange`).

`LostItemRetrievalPlanner.Plan` checks the requester is the owner (by number), the revision, and
picks the slot: the essential's existing claim slot first, then the requested one, then the first
free. The server then revives the same object into that slot (`TryRetrieveFromLostAndFound`,
`StorageIntegration.RestoreLostAndFound`), removes the entry, and only then indexes the item for
interest again (`RegisterHostWorldItem`): indexed while the entry still held it, it was skipped,
and a player who joined later, its owner included, was never sent it (found by `starting-items` on
a dedicated host, fixed with the 6a–7 batch, 2026-10-23). A stale revision, a full inventory,
an unknown handle or a failure leaves the entry as it was; any rollback restores both the entry and
the record.

An essential's red silhouette also retrieves it from Lost and Found when it's there
(`TryRetrieveForRecall`, the reserved slot).

## An entry lives as long as its item

The periodic reconcile (`ReconcileCanonicalPlacements`, policy `LostItemEntryReconciler.Classify`)
checks every entry against its item:

| Item | Record placement | Result |
| --- | --- | --- |
| present | `LostAndFound` | kept, and made inert again if a load left it active |
| present | anything else | removed: another system moved it out |
| present | no record | kept until a record exists |
| gone | any | removed: nothing is left to retrieve |

A retrieval that finds the item gone (`missing-canonical-unity-item`) or out of Lost and Found
removes the entry at once. Each removal sends the owner a fresh list. Job booklets are collected like
any other personal item, and a retrieved booklet can still be handed in. The job system destroys
every booklet copy when its job ends, one in Lost and Found too, and the reconcile then drops its
entry.

## Owner numbers

An entry keeps its owner's identity and their number. At disconnect `UnbindOwner` sets the number to
the absent owner number; at login `RebindOwner` sets it back from the identity. The list, retrieval
and recall compare numbers.

## Saving and loading

**Save** (`WriteSave`, key `Multiplayer.LostAndFound`): after a reconcile, so a destroyed item's entry
isn't saved, one object per entry: `persistentItemId` (the item's id), owner identity, prefab,
display name, authored key, revision, the claim, reason, time, and `storageIndex` (its index in
vanilla's Lost and Found storage list). Vanilla saves the inert items themselves in its own storage.

**Load** (`TryImportSave`, once vanilla's starting items and storage have loaded):

1. Licence papers in vanilla's Lost and Found storage are destroyed (multiplayer makes none).
2. Each saved entry finds its stored item: the one with the entry's id, else (an older save) the
   one at its storage index if the prefab matches, else the first with its prefab. An item with
   saved state keeps its own id and the entry takes it; an item with none (a mug) takes the entry's
   (`UseItemIds`).
3. Every entry is bound at load, owner online or not (PR 2a), with the owner's current number or the
   absent one. A claim takes the owner's number now, not the saved one.
4. An entry with no id, no owner, `storageIndex` -1 (written for destroyed items by older builds) or
   no stored item left is dropped and logged.

An entry not yet bound is kept as it was saved and written back by the next save. The owned-items
load check counts such entries as the item's place.

## Code

| Where | What |
| --- | --- |
| `Multiplayer.Core/Items/LostAndFound.cs` | `LostItemReason`, the collection policy, `LostItemRegistry`, the entry reconciler, the retrieval planner |
| `Multiplayer/Server/Items/NetworkedLostAndFoundManager.cs` | The server's Lost and Found: scan, collect, retrieve, save and load |
| `Multiplayer/Server/Trains/CarDeleteLostAndFoundPatch.cs` | A deleted car's items |
| `Multiplayer/Client/Items/LostAndFoundList.cs`, `LostAndFoundExchange.cs` | The client's list and retrieval |
| Tests | `LostAndFoundTests`, `LostAndFoundAwayOwnerTests`; scenarios `lost-and-found`, `offline-lost-and-found`, `lost-and-found-many-owners`, `car-delete-owner`, `borrowed-disconnect` ([testing.md](../testing.md)) |
