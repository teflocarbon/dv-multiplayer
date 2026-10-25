> Legacy: describes the item system before the refactor (names as on `beta` 04bfe277). The refactor replaced that code, so this is history: it is not how the code works now and not design. Replaced by [items/save-load.md](../../design/items/save-load.md) (R4). The item docs start at [items/README.md](../../design/items/README.md).

# Item persistence

> Status: as built, checked against the code on 2026-10-23 (`items/lineage`). Where every item is
> saved, when, and how a load rebuilds it. Vanilla's own item save is in
> [research/DERAIL_VALLEY_ITEM_SAVE_LOAD_LOGIC.md](../../research/DERAIL_VALLEY_ITEM_SAVE_LOAD_LOGIC.md).

The server keeps the vanilla save as its store (decided 2026-10-19,
[server-player-split.md](../../design/server-player-split.md#save-format)). The mod's item state is in the save's
`Multiplayer` section, beside vanilla's own. Each item is saved by exactly one store, and vanilla's
`ItemSaveData` stays the serializer for each item's own state: the mod wraps it, never reimplements
it.

## Where each item is saved

| What | Store |
| --- | --- |
| Authored items moved from their baseline, and tombstones for destroyed ones | `Multiplayer.WorldItemsV1` |
| Dynamic items in the world, a train interior or on a shelf, that a player owns or that are personal | `Multiplayer.WorldItemsV1` |
| Everything any player carries, the host's own player included, owned or not | `Multiplayer.WorldItemsV1`, keyed by the holder's identity |
| Cold-container contents | `Multiplayer.ColdContainers` ([container-storage.md](container-storage.md#saving)) |
| Lost and Found entries | `Multiplayer.LostAndFound`, with the inert items in vanilla's Lost and Found storage ([lost-and-found.md](lost-and-found.md#saving-and-loading)) |
| Installed gadgets | vanilla's `Storage_InstalledGadgets`, with `dvmpPersistentOwner` and the item's id in each one's state ([gadgets.md](../../design/gadgets.md)) |
| Owned items' index | `Multiplayer.OwnedItemsV1` ([owned-items.md](owned-items.md#saving-and-loading)) |
| Unmoved authored items | nothing: the scene has them |
| Job overviews and booklets | nothing: the job system saves the job and remakes them ([jobs.md](../../jobs.md)) |

Not saved: unowned, non-personal dynamic items lying in the world (the paint station's spray can is
respawned by its source each load, so saving it added a copy every load), including office stock forks
once dropped; dynamic `Attached` items.

Vanilla's stores, around the mod's:

- `Storage_World` is saved minus every item marked `dvmpManagedWorldItem`.
- `Storage_Inventory` keeps the host's own player's copy, so the career still opens in single player.
  The mod clears it when it loads the save in multiplayer (`PerPlayerSaveFormat.PrepareLoad`) and
  restores the host's inventory from `WorldItemsV1` like anyone's.
- `Storage_ItemContainers` isn't touched: in multiplayer no live item is ever inside a container.

## Identity in the item's own save data

`PersistentWorldItemIdentityPatch` (a postfix on `ItemSaveData.SaveItemData`, a prefix on
`LoadItemData`) writes and reads:

- `dvmpPersistentWorldItemId`: the item's id for life;
- `dvmpManagedWorldItem`, true for an item the mod saves (`WorldItemPersistenceManager.OwnsWorldPersistence`),
  with `dvmpAuthoredWorldItemKey` for an authored one;
- `dvmpPersistentOwner`, for an installed owned gadget.

So every store that keeps an item's save data keeps its id. An item with no `ItemSaveData` (a mug)
has none there; its Lost and Found entry or cold record carries it.

## `WorldItemsV1`

One JSON object per item (`WorldItemPersistenceRecord`): `persistentItemId`, `authoredItemKey`,
`prefabName`, `revision`, `hasPersistentOverride`, `replenishableStockFork`, `placement` (the byte of
`ItemPlacementKind`), `ownerIdentity`, `placementPlayerIdentity`, `claimSlot`, `claimFlags`, the world
parent (kind, NetId, key, car GUID), the attached car (GUID, front), the parent-local and absolute
pose, and `state` (the item's `ItemSaveData` JSON). A tombstone has only the authored key, prefab,
revision and `placement` = `Destroyed`.

**What is written** (`WorldItemPersistenceSave.ShouldPersist`):

- never: `Container`, `LostAndFound`, `Installed` or `Destroyed` placement, job overviews and booklets;
- a dynamic item in a player's hand or inventory, whoever they are and whether connected: always. A
  hand item is saved as `PlayerInventory`: it comes back in the inventory;
- any other dynamic item: only when personal (`BelongsToPlayer`) or owned, and in `World`,
  `TrainInterior` or `StaticParent`. A restored item is a fresh prefab copy, and a mug's prefab isn't
  personal, so ownership counts on its own (found by `ledger-save`, 2026-10-21);
- an authored item: when it has an override, has moved or turned from its baseline, or its placement
  differs; plus every tombstone;
- any item with a lease open, whatever the rules above say: its pose and placement are the last
  accepted spatial state.

**Loading** (`WorldItemPersistenceRestore.TryImport`), once, on the first server tick after vanilla's
starting items have loaded:

1. A tombstone deactivates the authored object with that key and records it `Destroyed`.
2. Job documents, and `World` records above 1,500 m (inventory items older builds left there), are
   skipped.
3. The item is found by its authored key, else by a live item with its id, else made from its prefab
   with the saved id.
4. An owned record makes the item personal again. A record for an item vanilla already has installed
   is ignored.
5. The record is registered and then overwritten from the save: revision, placement, holder, owner
   and holder (identities: nothing waits for them to connect), claim, pose, parents (a train by
   its car GUID), flags, and `LastMove` = `RestoredFromSave` (not logged). A train-interior item is
   restored straight away at its saved pose: nothing waits for its car, and a missing car has no
   recovery path.
6. A hand or inventory record whose holder isn't connected is deactivated and held back
   (`pendingOwnerRepresentations`). When they connect, `OnOwnerConnected` puts it in their inventory,
   logged `RestoredFromSave`, with a correction to their game.
7. The saved state is loaded 0.25 s later, retried 5 times with back-off; the item joins the interest
   index after that.

## When it is written

In the postfix of `SaveGameManager.UpdateInternalData`, on the server
(`NetworkedSaveGameManager.Server_UpdateInternalData`), in this order: players' positions, Lost and
Found, cold containers, `WorldItemsV1`, `OwnedItemsV1`. It runs for vanilla's saves and autosaves, for
each joining player's save packet, and at a disconnect while the world is loaded (before the player is
removed). It only updates the save in memory; the file is written when vanilla writes it. Each store
leaves its saved value as it was until its own import has run, so a save made before a store loaded
doesn't wipe it.

## Code

| Where | What |
| --- | --- |
| `Multiplayer/Server/Items/WorldItemPersistenceManager.cs`, `WorldItemPersistenceSave.cs`, `WorldItemPersistenceRestore.cs`, `WorldItemPersistenceRecord.cs` | `WorldItemsV1` |
| `Multiplayer/Server/Items/PersistentWorldItemIdentity.cs`, `PersistentWorldItemIdentityPatch.cs` | The id in the item's save data |
| `Multiplayer/Server/Career/NetworkedSaveGameManager.cs` | The save pass |
| `Multiplayer/Server/Career/PerPlayerSaveFormat.cs` | Per-player saves and the conversion of old careers |
| Scenarios | `save-reload`, `inventory-persist`, `host-player-persist`, `rejoin`, `ledger-save`, `item-id-roundtrip` ([testing.md](../../testing.md)) |

## Known issues

From the code, not run in game (2026-10-23):

- An item in flight at save time is saved even if it is unowned and in the world, so an unowned thrown
  spray can or office prop comes back as an extra copy.
- A dynamic owned `Attached` item (a bought EOT lantern on a car) isn't in `WorldItemsV1` and isn't
  marked managed, so only vanilla's world storage may keep it, without its owner. Unverified.
- `Multiplayer.Players.<id>` still gets an inventory chunk per player from `ServerPlayer.Inventory`,
  which nothing fills: it is always empty.
- A cold-container save the import rejects is overwritten with an empty graph by the next save
  ([container-storage.md](container-storage.md#known-issues)).
