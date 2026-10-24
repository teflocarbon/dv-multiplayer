# Saving and loading

> Status: built in R4 of the [item refactor](../item-refactor.md), 2026-10-24. This page says what the code does.
> It covers how items are saved and loaded (`Multiplayer/Server/Items/SaveLoad`, `Multiplayer.Core/Items/SaveLoad`):
> the `Multiplayer.Items` key, the load and its plan, rows that won't load, old saves and vanilla's lists.
> The entries themselves are [ledger.md](ledger.md), inventories [inventory.md](inventory.md), live
> records [live-state.md](live-state.md). Vanilla's own item save is in
> [research/DERAIL_VALLEY_ITEM_SAVE_LOAD_LOGIC.md](../../research/DERAIL_VALLEY_ITEM_SAVE_LOAD_LOGIC.md).

## The key

Every item is saved in one key of the save's Multiplayer root, `Items` (`ItemsSaveCodec`), written in
one pass and read in one pass. Enums are written by name.

```json
"Items": {
  "format": 1,
  "ledger":       [ { "id": "…", "owner": "…", "prefab": "…", "where": { "kind": "Slot", "holder": "…", "slot": 3 },
                      "claimSlot": 3, "born": { "reason": "Issued", "utc": 0 }, "ended": { … } } ],
  "states":       { "<id>": { "prefab": "…", "position": […], "state": { …the item's ItemSaveData… } } },
  "unowned":      [ { "id": "…", "prefab": "…", "placement": "World", "state": { … } } ],
  "authored":     [ { "authoredKey": "…", … }, { "authoredKey": "…", "placement": "Destroyed" } ],
  "containers":   [ { "id": "<the shell's item id>", "owner": "…", "prefab": "…", "capacity": 12 } ],
  "lostAndFound": [ { "id": "…", "reason": "OwnerDistance", "utc": 0, "display": "…" } ],
  "players":      { "<identity>": { "starterKit": true } }
}
```

| Section | Holds |
| --- | --- |
| `ledger` | every owned item's entry, live or ended: owner, prefab, exact home, claim, birth, end. The only place an owner, a home or a claim is saved |
| `states` | each owned item's one row (`SavedItemRow`): its pose and its state, by id. An installed gadget has none (vanilla's `Storage_InstalledGadgets` keeps its state) |
| `unowned` | each unowned item worth keeping, with its home in the row: one a player carries, one in a container, a player's item lying about |
| `authored` | scene items changed from the scene: an override (moved) or a tombstone (gone), by authored key |
| `containers` | each cold container: its id, which is its shell's item id (OI-CON-09), and its size. Its contents are items whose home is `Container(id, slot)`; a container is stored in another when an item with its id is. Its owner stays in this row until R5 |
| `lostAndFound` | each Lost and Found entry's reason and time. Its owner and claim are the ledger's |
| `players` | who has had a starter kit |

**Each fact is saved once.** An item's state is one row, wherever its copy is: its object, a cold row
(an offline player's inventory), a container's row, or Lost and Found's object. The row is read from
the ledger and live state together in `UpdateInternalData`'s postfix (`NetworkedSaveGameManager`), for
saves, autosaves and the save sent at a join, so the save is one picture. A hand is never saved: an
item in a hand is saved in a free slot of its holder's (its claim, else the lowest free, the belt last),
or in its owner's Lost and Found (`InventoryOverflow`) when the inventory is full.

**Rows stand alone.** A ledger row, a state, a container or a Lost and Found row that can't be read
(no id, an unknown home, junk) is kept as it was and written back unchanged at the next save
(`ItemsSave.Unreadable*`); one bad row never empties the rest. A newer format than the build knows is
left in the save as it is, nothing is loaded from it, and an error is logged.

**The item's id** rides in its own vanilla state as `dvmpPersistentWorldItemId` (`ItemIdentityPatch`),
so vanilla's installed storage keeps a gadget's id. Nothing else is written into vanilla state: the
owner the gadget code used to write there (`dvmpPersistentOwner`) is gone.

## Loading

`ItemSaveLoad.HostTick` loads once on the server, after the world and the cars have loaded, before
anyone logs in. Until it has run, no item request is served: container operations are refused with
`items-loading` (OI-CON-05), Lost and Found doesn't collect or hand anything back, and the invariant
check waits (`ServerLedger.Loaded`).

1. The ledger's rows go into the ledger (`ServerLedger.LoadEntries`); unreadable rows are kept. Job
   paperwork's rows are dropped (the job system prints its own), and an entry whose prefab is gone
   goes in as `Unloadable`, holding no slot, so an item that took its slot since keeps it.
2. `ItemLoadPlanner.Plan` (Core) gives each live entry one step, from its home and whether it has a row:

   | Step | When | What |
   | --- | --- | --- |
   | `KeepCold` | in its owner's slot | a cold row (`ColdInventories`), made in its exact slot when the owner joins (D12) |
   | `IntoContainer` | in a container | a row of that container |
   | `IntoLostAndFound` | in Lost and Found | made there from its row, with its saved reason and time |
   | `MakeLive` | in the world, on a shelf, at a snap point, in a car or at a car's end whose car exists | made in its home from its row |
   | `LeaveInstalled` | an installed gadget whose car exists, or installed on the world | nothing: vanilla's installed storage makes it |
   | `Recover` | it can't go home | made from its row (or fresh, with no row) in its owner's Lost and Found |
   | `Unloadable` | its prefab is gone | its rows kept verbatim ([Rows that won't load](#rows-that-wont-load)) |

   `Recover`'s reasons: an installed gadget whose car is gone (`GadgetDestinationMissing`, OI-LF-22;
   vanilla moves the gadget's object to its own Lost and Found list, and the load finds it there by id);
   an item in a car that is gone, a slot saved under someone other than its owner, or no saved row
   (`SaveRecovery`). Cars load before items, so a train-interior item finds its car if it exists.
3. Unowned rows: an inventory or hand item becomes its holder's cold row, a stored one its container's
   row (owned by the container's owner), anything else is made live.
4. Containers and their items go into the container graph (`ContainerServer.LoadRows`), one row at a
   time, and a row the graph refuses stands alone (OI-CON-04, F18): an owned item refused (a slot past
   the end, a taken slot) goes to its owner's Lost and Found (`SaveRecovery`), logged as a warning ("its
   container refused it"); a refused container or unowned item keeps its row as it was. The container's
   other rows load.
5. Scene items' overrides and tombstones are applied.
6. `ServerLedger.FinishLoad` marks the ledger loaded and runs the one-home check.

An item made from a row goes through live state's ordinary birth (`LiveItems.Ensure`), with its saved
record applied before its object is made, so the projection sends it to clients as it was saved.
Its state is applied after its object exists, with up to five tries across frames.

There is nothing to reconcile: no `Revive`, `Migrated` or `Missing`. An ended entry stays ended and its
item isn't made; a state row with no live entry is not loaded, and not written again; an entry with no
state row is made fresh in its owner's Lost and Found (`SaveRecovery`).

## Rows that won't load

D9, OI-LED-16. Two things can stop an item loading:

- **Its prefab is gone** (an item mod removed). Its entry's home becomes `Unloadable`, and its ledger
  and state rows (and Lost and Found row) are kept exactly as saved and written back at every save, so
  putting the mod back brings the item back at the next load. It has no object, holds no slot, and the
  one-home check counts it as home. Its saved row still names its old slot, and an item put in that
  slot meanwhile keeps it at the next load.
- **Its state won't load** (vanilla's `LoadItemData` throws, at load or when a cold row goes live at a
  join). The item is destroyed and made fresh from its prefab, with the same id and owner, in its owner's
  Lost and Found (`SaveRecovery`), and an error with the id is logged. Its own state (a lighter's fuel) is
  lost, the item isn't. The destroy is `ItemRemovalReason.RemadeFresh`, which the ledger ignores.

`Missing` (an entry no store held) is gone: with one key there are no stores to disagree.

## Old saves

D1. A save without `Items` loads the world and the career (money, licences, trains, jobs) and starts
every player's items fresh: `WorldItemsV1`, `OwnedItemsV1`, `LostAndFound` and `ColdContainers` are
never read, the load logs once that the save was "saved before Multiplayer.Items", and the next save
writes `Items` and removes the old keys. Players get a new starter kit and their essentials at their
next join.

## Vanilla's lists

D2. Vanilla's `Storage_Inventory`, `Storage_World` and `Storage_LostAndFound` are a write-only mirror,
never read in multiplayer (`PerPlayerSaveFormat.PrepareLoad` empties them before vanilla's load sees
them). The save writes the host's own player's inventory into `Storage_Inventory`, slot for slot, from
the same rows as `Items`, so the career still opens in single player with the host's things. Nothing
goes into `Storage_World` or `Storage_LostAndFound`. A dedicated host has no player, so its mirror is
empty. `Storage_InstalledGadgets` stays vanilla's: it is how installed gadgets are made.

Because vanilla's inventory starts empty, a job booklet DV reprints at load (it saves none of its own
in multiplayer: `Multiplayer.Items` skips job paperwork, claimed or not) would take the first slot vanilla has free, one the host's own saved item still holds
as a row. On a player host the reprint waits until the host's items are back in their slots
(`JobBookletRecoveryPatch`, at most 20 s); a dedicated host prints none.

## Re-keying a player

`InstallGuidRekey` moves a player's install GUID to their Steam ID. Before the load it edits the saved
`Items` key in place: every string equal to the old identity (an owner, a holder, a `born.by`, a
container's owner) and every property named by it (a starter kit), so a row the load can't read moves
too. After the load it re-keys the ledger, live state, cold rows, Lost and Found and the containers, and
the rows the load kept verbatim (`ItemSaveLoad.RekeyKept`).

## Reading it

`ServerLedger.Dump()` in a `dvmp_eval` probe lists the loaded entries; `Unloadable` ones show their
home as such. The load logs one line ("Items: loaded …"): items made, kept cold, put in containers, unloadable, and rows
kept as they were.
Scenarios edit a save with `SaveEdits.SaveWith` (`DevTools/DvmpScenarios/Library/SaveEdits.cs`):
`ledger-save`, `item-id-roundtrip`, `item-bug-container-save-refused`, `old-save`, `unloadable-item`.
