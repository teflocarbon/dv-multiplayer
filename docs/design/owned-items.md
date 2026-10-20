# Owned items

> Status: designed 2026-10-21, not started. Nothing below is built. It replaces how player
> inventories, rejoin, Lost and Found and item returns work today
> ([`item-lifecycle.md`](item-lifecycle.md), [`item-ownership-open-questions.md`](item-ownership-open-questions.md)),
> and it is what the gadget design's [Owned items](gadgets.md#owned-items) section points to.

Every item a player owns has one id for its whole life, and the server keeps a ledger of those items:
whose each one is, where it is now, and how its life ended. Inventories, rejoin, Lost and Found, the
player's list of their items and retrieving them are all built on the ledger. Vanilla's storage lists,
on the server and in every client, are made to agree with it.

## Implementation status

Not started (2026-10-21). See the [plan](#plan).

## Why

Derail Valley keeps a single player's items in `StorageController`'s lists (inventory, world, Lost and
Found, installed gadgets, item containers), and vanilla moves items between them itself. A Minecraft
server has one inventory per player and its own save format. We have neither: we sit on vanilla's
single-player model and keep its save format
([decided 2026-10-19](server-player-split.md#save-format)). Those lists can't be the truth for many
players, so the ledger decides where each item is, and vanilla's lists follow.

Ownership was tied to vanilla's storage plus a per-session player number. That made sense while the
server and a client were one game. Now that the server is split out, numbers come and go with
sessions, and much of the code still keys by them. That is why inventories have been fragile and
items haven't reliably come back to their owners. What the code does today (read 2026-10-21, not
run in game):

| What goes wrong | Why |
| --- | --- |
| An offline owner's items look unowned. Lost and Found never collects them, and the next player to take one becomes its owner by number. | On disconnect `UnbindPersistentOwner` zeroes the owner number of every record the player owns (`AuthoritativeItemRegistry.cs:949-962`, from `ServerPlayer.cs:28-34`). Lost and Found then sees "unowned-or-unbound" (`Multiplayer.Core/Items/LostAndFound.cs:62`) and can't find the owner (`NetworkedLostAndFoundManager.cs:615-616`). The state machine makes a taker the owner when the number is 0 (`ItemAuthorityStateMachine.cs:152-157`). The GUID survives (it is only set when empty, `AuthoritativeItemRegistry.cs:289-290`), so the record says two different things. |
| A borrower who leaves while the owner is offline keeps the item for good. | The disconnect drop's "borrowed" test needs a non-zero owner number (`ServerItemManager.PlayerItems.cs:259-261`), and an offline owner's number is already 0. |
| Another player's radio was counted as yours (playtest 2). | Records restored from a save keep the number their owner had then. `OwnedItemsSummary` had to fall back from identity to number (`OwnedItemsSummary.cs:49-55`, quoted in the `owned-items` scenario). |
| An item withdrawn from a container is not saved. | `RegisterMaterializedColdItem` builds the record without the item's persistent id or authored key (`AuthoritativeItemRegistry.cs:735-753`), so `ShouldPersist` returns false (`WorldItemPersistenceSave.cs:141-160`). The id survives on the object: the deposit stored the item's own save data, including `dvmpPersistentWorldItemId` (`NetworkedColdContainerManager.cs:174-186`), and withdrawal reloads it (`:345-349`, `PersistentWorldItemIdentityPatch.Load`). The record never reads it back. |
| One item, several ids. "Where is item X" means parsing payloads. | Each Lost and Found collection mints a record id (`NetworkedLostAndFoundManager.cs:325`), and so does each container deposit (`NetworkedColdContainerManager.cs:177`). Both differ from the item's own id. |
| Lost and Found entries rebind to the wrong item, or wait forever. | After a load an entry binds by its index in vanilla's list plus prefab, falling back to the first item with that prefab (`NetworkedLostAndFoundManager.cs:556-562`), and only once its owner connects (`:553-555`). |
| A cold container's owner is resolved through numbers. | The cold record takes its owner from the depositor's number (`NetworkedColdContainerManager.cs:180`, `:529-531`). Withdrawal refuses when the owner is offline (`:291-303`). |
| Rejoin duplicates, then repairs. | Each game's DV adds missing starting items while loading. The server then rejects the fresh copies and puts the originals back, matching by prefab name only (`TryRestoreReturningOwnerItem`, `ServerItemManager.PlayerItems.cs:188-240`, match at `:213`; client side `DiscardUnboundLocalDuplicate`, `ClientItemManager.Compatibility.cs:230-263`). Before that, the join packet tells DV what the player already owns (`StartingItemsClientPatch`, `StartingItemsServerPatch`). |
| Claim slots clash and are repaired after the fact. | `EnsureDistinctClaimSlots` runs after transitions, recalls, retrievals and at every owner load (`AuthoritativeItemRegistry.cs:964-1021`, `NetworkServer.cs:1178-1181`). |
| The starter kit can be given twice across a restart. | `StarterKitsGiven` is in memory only (`NetworkServer.cs:1413-1414`). The kit is given when the player has no `Players` entry (`SaveGameDataPacketBuilder.cs:51`). |
| The host's own purchases are unowned until someone picks them up. | Only remote clients send a purchase to the server (`GlobalShopControllerClientPatch.cs:23`). The host's go through vanilla. |
| Used-up and swapped items leave no trace. | No mod code handles spent duct tape, solder spools or labels. A destroy drops the record and its owner (`NetworkedItemServer.cs:387-414`), and a replacement starts unowned. |
| Recall, claims, containers and the red style compare numbers. | About 40 server-side reads of `PersistentOwnerPlayerId` against 20 of the identity, and the clients compare numbers only (`InventoryPresentation.cs:61,68`). |

### Known bugs the ledger removes

These are bugs in today's code. None gets a fix of its own (the user, 2026-10-21): the ledger
redesign removes them, in PR 1 of the [plan](#plan), and [migration](#migration) covers what they
left in saves.

- **An item withdrawn from a cold container may not be saved.**
  - `RegisterMaterializedColdItem` (`AuthoritativeItemRegistry.cs:729-753`) builds the withdrawn
    item's record without its `PersistentItemId` (and without its authored key).
  - The item's `PersistentWorldItemIdentity` does have the id again: withdrawal calls `LoadItemData`
    on the stored state (`NetworkedColdContainerManager.cs:345-349`), and
    `PersistentWorldItemIdentityPatch.Load` restores it.
  - `Ensure` then returns the existing record (`AuthoritativeItemRegistry.cs:100`), so the record
    never picks the id up.
  - `WorldItemPersistenceSave.ShouldPersist` (`:133-160`) takes the branch for items with no
    persistent id. Its authored-key check fails too, so it skips the item, whether it is in the world
    or in a player's inventory.
- **One item, several ids.** Lost and Found (`NetworkedLostAndFoundManager.cs:325`) and cold
  containers (`NetworkedColdContainerManager.cs:177`) mint record ids of their own.

## Decisions

All were decided with the user on 2026-10-21.

- **One id for life.** Every item a player owns, except scene-authored world items, has one random
  GUID in the save and the server's records for its whole life. The wire keeps the per-session
  `ushort` NetId, so lifelong tracking adds nothing to item packets. Minecraft splits entity ids the
  same way: an int on the wire, a UUID in the save.
  - Uniqueness comes from detecting a duplicate when an item is registered: keep the first, re-mint
    the copy, log it loudly.
  - SteamID plus a counter was set aside. The server mints ids, not players. Owners change while ids
    must not. Players without Steam have no SteamID.
- **Retrieval sends the item to your Lost and Found.** You collect it at any shed. Nothing jumps into
  your hands from across the map.
- **You can retrieve your item from anyone,** even from another player's hand, mid-use. It's yours.
  Taking it back from a friend at the worst moment is part of the fun ("that's half the fun", the
  user), not abuse to design out. The default policy keeps it, whatever permissions add later.
- **Installed gadgets can be retrieved any time, from any car.** Players on that train see it come
  off.
- **A borrower leaving sends your item straight to your Lost and Found,** online or not.
- **Offline counts as far away.** An offline owner's items that aren't with them or installed go to
  their Lost and Found under the standard rules (another player nearby protects them; a grace period).
  That includes items inside train cars and on shelves, which an online owner's distance never
  collects.
- **Nothing expires.** An absent player's items wait however long they're away.
- **Found items aren't yours.** A non-personal item you pick up and keep (a mug, an office bin, a
  spray can) stays unowned. It is saved while you carry it, as today, and isn't listed, retrievable
  or sent to Lost and Found.
- **The list shows only what still exists.** A replacement (the empty tape roll, the empty spool) is
  yours and listed. The spent item disappears from the list, while the ledger records what replaced
  what, for logs and "where did my X go".
- **A destroyed essential is replaced at your next join.** The server gives a new one only when the
  ledger says the old one's life ended, never because it's somewhere else.
- **Job papers belong to the job.** Booklets, overviews and reports aren't owned items. Lost and Found
  still collects a lost booklet for the player it was issued to.
- **Red means its owner can take it back.** Any item another player owns shows red to whoever holds it,
  whether its owner is online or not.

## The model

### What an owned item is

An item with a persistent owner: a player's identity (the Steam-derived player ID, or the install GUID
without Steam; see [server-player-split.md](server-player-split.md#identity)). It becomes owned in one
of these ways:

| How | Owner | Born as |
| --- | --- | --- |
| Shop purchase, by any player (the host's own player included): the server makes the item and registers it before anyone sees it ([shops.md](shops.md#owned-items)) | whoever scanned the line (one shared basket per register) | `Purchased`, detail: shop, transaction id, payer |
| Starter kit, on a player's first join to the career | that player | `StarterKit` |
| An essential or licence paper the server issues because the player has none (see [Joining](#joining-and-rejoining)) | that player | `Issued` |
| Admin `/give` | the target | `Granted` |
| An unowned personal (`BelongsToPlayer`) item first put in a hand or inventory, as today | the taker | `Claimed` |
| A replacement vanilla makes as it uses an item up (empty tape, empty spool, a label's item) | the used item's owner | `Replaced` |
| A gadget the label maker makes | the player who made it | `Made` |
| An owned item found in an old save, while [migrating](#migration) | the owner the save names | `Migrated` |

Not owned: scene-authored world items; non-personal items, even when carried; job booklets, overviews
and reports; shop receipts (paper, server-made and unowned, [shops.md](shops.md#owned-items)); unowned
dynamic items such as stock forks and the paint station's spray can.

### One id for life

**Minting.** An item's GUID is minted once, when the item comes into being on the server: today in
`AuthoritativeItemRegistry.Ensure` through `PersistentWorldItemIdentity.Ensure()`
(`AuthoritativeItemRegistry.cs:117-119`, `PersistentWorldItemIdentity.cs:18-27`). That stays. A
dynamic item has its GUID before it has an owner. The ledger entry is born when it becomes owned, and
it uses the GUID the item already has. Nothing else mints an item id. Specifically:

- **Lost and Found** entries use the item's id as their own (today: a new GUID per collection,
  `NetworkedLostAndFoundManager.cs:325`).
- **Cold-container** records use the item's id as their own (today: a new GUID per deposit,
  `NetworkedColdContainerManager.cs:177`). Withdrawal registers the item under it
  (`RegisterMaterializedColdItem` today drops it). The container shell's own id
  (`ColdContainerIdentity`) is the shell's container id, a separate thing, and stays.
- **Save conversion** keeps deriving a stable id for a converted inventory item
  (`PerPlayerSaveFormat.cs:163-169`), so converting twice gives the same ids.

**Where the id travels.** The id is carried in vanilla's own item save data
(`dvmpPersistentWorldItemId`, `PersistentWorldItemIdentityPatch`), so every store that holds an
item's save data carries it.

| Placement | Who holds the id |
| --- | --- |
| Hand, inventory, belt (any player's) | the server's record. At save, `WorldItemsV1`, keyed by the holder's identity. |
| World, train interior, static parent, attached (EOT lantern), snapped (hanger) | the server's record. At save, `WorldItemsV1`. |
| Cold container | the cold record (key = the item's id). The item's save data inside it. |
| Lost and Found | the Lost and Found record (key = the item's id). Vanilla's Lost and Found storage holds the inert item with its save data. |
| Installed gadget | the server's record (`Installed` on its car, [gadgets.md](gadgets.md#the-installed-gadget-and-its-item)). Vanilla's `Storage_InstalledGadgets` holds its save data, plus `dvmpPersistentOwner`. |
| Client projections | nothing. Clients know NetIds only. |

**Duplicates.** Random GUIDs don't collide in practice. The real risk is one id being copied: a
copied save, a dupe bug, or an object cloned with its identity. `PersistentWorldItemIdentity` is a
serialized field, so `Instantiate` copies it. The ledger refuses a second live item with an id it
already has:

- The first item keeps the id. The copy gets a new GUID and is born `Duplicate`, with the original's id
  in its lineage and the original's owner.
- It is logged as an error (`item.duplicate-persistent-id`, with both NetIds, the prefab, the owner
  and where each one is), because it means a bug or an edited save.
- Lost and Found's existing `LostItemRegistryResult.DuplicatePersistentItemId` becomes the same check.
  Today `Add` returns it only for a record id it just minted (unreachable), and `Restore`'s callers
  ignore it (`NetworkedLostAndFoundManager.cs:194`, `:585`).

**Lineage.** An entry records what it came from and what ended it:

- **Born:** how it was born (the table above), when, by whom, and `from`, the id of the item it
  replaced or duplicated.
- **Ended:** why (an `ItemRemovalReason`, with the change cause's detail), when, and `replacedBy` when
  vanilla made a replacement.

Consumption needs no special case in the ledger. The server runs the vanilla methods that use an item
up inside a *replacement scope*: duct tape's last use, the solder spool's first, the label's
`DestroyOnGadgetUnlink`, all of them server-side in the gadget design
([gadgets.md](gadgets.md#items-that-gadgets-create-and-destroy)). An item destroyed inside the scope ends
with `replacedBy`. An item created inside it is born `Replaced`, owned by the used item's owner.
A money deposit ends its entry with `MoneyDeposited`, and nothing replaces it.

Any other destroy of an owned item ends the entry with its recorded removal cause, often
`DestroyedByGame` ([item-lifecycle.md](item-lifecycle.md#why-an-item-changed)). If that was an
essential, the owner gets a new one at their next join.

### The ledger

One entry per owned item, live or ended, kept by the server:

```text
OwnedItem
  id            item GUID (the key)
  owner         owner identity (GUID): a player now, a company later (see Permissions)
  prefab        prefab name, for the list and for issuing essentials
  claimSlot     the essential's retrieval claim slot in its owner's inventory, or none
  where         see below
  born          reason, time, actor identity, from (id)
  ended         reason, time, cause detail, replacedBy (id); absent while live
  shopStocked   the item still carries vanilla's Restock flag, so it counts against its owner's
                stock of that prefab at the shops
```

**What other systems read.** Shops keeps stock per player, and works it out from the ledger instead
of saving a counter ([shops.md](shops.md#owned-items)). A slot is used for as long as its owner has
a live entry of that prefab with `shopStocked` set. The entry ending, or the flag clearing (a used
garage key), frees the slot, and that is restocking. The ledger gives two things:

- **A count query,** `CountLive(owner, prefab, shopStocked)`.
- **A change event,** raised when an entry is born, ends, or has `shopStocked` change.

The ledger sets `shopStocked` at birth from the item's flag. The server clears it where vanilla
clears the flag. At load, and in migration, it is read from the `Restock` value in the item's stored
state. A replacement (the empty tape roll) is never shop-stocked. Installed gadgets still count, as
they do in vanilla.

Several systems also send an item to its owner's Lost and Found: a departing borrower, retrieval, a
deleted car's gadgets, a purchase made far away. They all go through one entry point,
`SendToLostAndFound(id, reason)`. A purchase can also be born straight into Lost and Found through
it.

`where` is a summary that names the store holding the item, not its pose:

```text
With(holder identity, hand | inventory | belt, slot)
World(parent: none | car GUID | static key)       loose, in a train interior, or on a shelf
Attached(car GUID) | Snapped(parent)
Container(container id)
LostAndFound
Installed(car GUID, gadget UID)
Missing                                            the store that should have held it didn't, at load
```

**Keyed by identity everywhere.** The ledger holds owner and holder identities, never player
numbers. The server's item record (`AuthoritativeItemRegistry.Record`) reads its owner from the ledger:

- `PersistentOwnerIdentity` comes from the ledger.
- `PersistentOwnerPlayerId` stops being stored. It is worked out when needed, from the identity to
  the session currently connected under it, or 0 for none.

Every server check moves to the identity: Lost and Found, recall, claim slots, containers, the
disconnect rules and the first-taker rule. Player numbers stay on the wire only, as the NetId does.
`UnbindPersistentOwner`/`RebindPersistentOwner`, Lost and Found's `UnbindOwner`/`RebindOwner` and its
per-entry `OwnerPlayerId` go away: nothing is bound to a number, so nothing needs re-binding at login.

**What the ledger is and isn't.**

| Thing | Keyed by | Holds | Lives |
| --- | --- | --- | --- |
| Ledger | item GUID | owner, where, life | server, saved |
| Item registry | NetId | runtime placement detail, revision, holder, pose, spatial state | server, while the item has a NetId |
| Stores: `WorldItemsV1`, the cold graph, Lost and Found records, vanilla's installed and Lost and Found storage | item GUID | the item's state (vanilla `ItemSaveData`) and pose | server, saved |
| Vanilla `StorageController` lists | object | what vanilla needs in order to run | every game, made to agree |

The ledger doesn't duplicate state. It is the index that says which store must hold each item, and
whose the item is. Every change of an owned item's owner or `where` goes through the ledger. That
change happens at the same points where the registry already commits a transition: an accepted
transition, a Lost and Found collection or retrieval, a cold deposit or withdrawal, an install or
removal, a destroy. Each logs a change cause today.

**Making vanilla agree.** On the server, the existing storage integration puts each item in
vanilla's list for its placement (`StorageIntegration`). A check in debug builds, and in the
`ledger-*` scenarios, walks the owned items and reports any item that sits in vanilla lists that
disagree with its `where`, or in more than one (`StorageController.ItemInMultipleStorages`). On
clients nothing changes: their lists follow the server's item snapshots, as now.

**In the vanilla save.** The server keeps the vanilla save as its store. The ledger is one more key
in the mod's `Multiplayer` section, next to `Players`, `WorldItemsV1`, `LostAndFound` and
`ColdContainers`:

```json
"OwnedItemsV1": {
  "format": 1,
  "items": [
    { "id": "…", "owner": "…", "prefab": "CommsRadio", "claimSlot": 0,
      "where": { "kind": "With", "holder": "…", "slot": 0 },
      "born": { "reason": "StarterKit", "utc": 0, "by": "…" } },
    { "id": "…", "owner": "…", "prefab": "DuctTape",
      "where": { "kind": "World" },
      "born": { "reason": "Purchased", "utc": 0, "by": "…" },
      "ended": { "reason": "DestroyedByGame", "utc": 0, "replacedBy": "…" } }
  ],
  "players": { "<identity>": { "starterKit": true } }
}
```

- It is written in the same `Server_UpdateInternalData` pass as the stores
  (`NetworkedSaveGameManager.cs:67-105`), after a Lost and Found reconcile, so a store and the ledger
  can't disagree within one save.
- `players.<identity>.starterKit` replaces the in-memory `StarterKitsGiven`.
- Ended entries are kept for lineage, the most recent 100 per owner, and pruned beyond that.
- It is additive. The stores keep their formats, so a build without the ledger can still load the
  save, and it simply drops `OwnedItemsV1`. The next ledger build rebuilds it by migration.

**Loading.** After the stores have imported, before any player logs in, the server checks every
live entry against the stores:

| Found | Result |
| --- | --- |
| In the store its `where` names | kept |
| In exactly one other store | that store wins (it has the state). `where` is corrected and logged: the ledger was stale, for example after a build without the ledger played the save. |
| In more than one store | the copy in the store `where` names is kept, or else the first by priority: Lost and Found, container, installed, a player's possession, world. The others are destroyed and logged as duplicates. |
| Nowhere | `Missing`: kept, shown in the owner's list as lost track of, and checked again at each load. An admin can `/give` a new one. |
| An owned item in a store with no entry | registered, born `Migrated` |

Lost and Found entries bind by the item id inside the inert item's save data, for every owner, online
or not, at load (today by index and prefab, owners online only).

### Joining and rejoining

The server decides what a player has. No game makes its own items any more.

1. **Login.** The identity is verified as today. Nothing is re-bound, since nothing is keyed by number.
2. **Possessions.** Everything whose `where` is `With(this identity)` goes into the player's inventory
   at its saved slot. A hand item comes back in the inventory, as today. These are the player's own
   items and unowned items they carried; [leaving](#leaving) guarantees nobody else's.
   `WorldItemPersistenceManager.OnOwnerConnected` already restores deferred possessions. It now reads
   them from the ledger.
3. **First join to the career** (`players.<identity>.starterKit` unset): the server spawns the
   starter kit owned by the player into their inventory (today's list:
   `SaveGameDataPacketBuilder.cs:52-72`), and sets the marker.
4. **Essentials** (vanilla's `StartingItemsController` list: comms radio, maps, compass, wallet,
   catalogue, …): for each one the player has no live entry of, the server spawns one, owned, into its
   claim slot. "Has" means a live entry anywhere: in Lost and Found, with another player, in a
   container. An ended or never-made one doesn't count. First join is just the case where all are
   missing.
5. **Licence papers:** the same rule for the career's licences. PR 4 checks how licence papers are
   made in multiplayer today before settling this. The rule also runs for everyone online when a
   licence is bought.
6. **The list:** the player's Lost and Found and owned-items lists are sent when asked, as Lost and
   Found is today.

**Claim slots** are assigned by the ledger, unique per owner by construction, using
`EssentialClaimSlotPolicy` when an essential is issued or first stashed. A slot can't clash, so the
runtime repairs go. Migration runs the repair once, for saves that already have clashes.

**In every game, DV's `StartingItemsSafeguard` and `LicensesSafeguard` are off in multiplayer:** on
clients, on a player host and on a dedicated host (which already skips them,
`StartingItemsControllerPatch.cs:17-28`). DV's inventory starts empty in every game, as the host's
already does (`PerPlayerSaveFormat.cs:93`).

**What goes away:**

| Today | Why it existed | Replaced by |
| --- | --- | --- |
| `OwnedItemsSummary` and its two fields in `ClientboundSaveGameDataPacket` | tell DV's safeguards what the player owns | the ledger. `/give` asks the ledger. |
| `StartingItemsClientPatch`, `StartingItemsServerPatch`, `PerPlayerSaveFormat.LocalOwnedItems`/`OwnedInSave` | count owned items as present for the safeguards | safeguards off, server issues |
| `StartingItemsControllerPatch`'s dedicated-only condition | keep the parked body empty | the same patch for every game in multiplayer |
| The starter kit as `PlayerItems` in the join packet (made by the client, then adopted) | client-made kit | server-spawned kit |
| `NetworkServer.StarterKitsGiven` (in memory) | once per session | `players.<identity>.starterKit` (saved) |
| `TryRestoreReturningOwnerItem` and `restoredOriginals` | swap a returning player's fresh copies for their originals, by prefab | no fresh copies |
| `DiscardUnboundLocalDuplicate`, `AdoptUnboundInventoryItemsSoon` | client side of the same | no client-made items |
| `TemporaryClientAdoptionCompatibility`, `TemporaryHostAdoptionCompatibility`, the adoption packets | client-made items reaching the server | removed once the kit, essentials, licence papers and purchases are server-made. PR 5 checks nothing else still adopts. |
| `EnsureDistinctClaimSlots` after transitions and at `ReadyForItems` | repair clashing claim slots | claim slots assigned uniquely, one repair in migration |
| `UnbindPersistentOwner`, `RebindPersistentOwner`, Lost and Found's `UnbindOwner`/`RebindOwner`, pending Lost and Found imports per owner | numbers bound per session | identity everywhere |
| `ServerPlayer.Inventory` and the `"inventory"` it writes per player (`NetworkedSaveGameManager.cs:88-92`) | nothing fills it | removed |
| Most of `InstallGuidRekey`'s per-system re-keys (`InstallGuidRekey.cs:61-74`) | owners in five places | the ledger plus the holder identities and container owners in the stores |

### Leaving

When a player leaves (or their game crashes), the server goes through what they carry:

- **Another player's item** (in hand, inventory or belt) goes straight to its owner's Lost and Found,
  online or not (`BorrowerLeft`). This replaces today's drop where they stood, and it fixes the
  offline-owner gap.
- **Their own items, and unowned items they carry,** stay with them and come back at their next join.
  A held item goes into their inventory, as a save already treats it. If no slot is free it is dropped
  where they stand, as today.
- **Their items elsewhere** fall under [Lost and Found](#lost-and-found-for-every-owned-item): they're
  now an offline owner.

So an offline player's saved possessions are only their own items and unowned items. Migration
enforces that on old saves too.

### Lost and Found for every owned item

Collection keeps today's rules (`LostItemCollectionPolicy`), keyed by identity. An offline owner counts
as far away:

| Owner | Collected from | Protected by |
| --- | --- | --- |
| Online | the world (`World` placement), beyond the owner distance (200 m), unchanged | another player within 100 m; the grace period (10 s) |
| Offline | the world, train interiors, static parents (shelves), attached and snapped placements: anything that isn't with a player, in a container, installed or already in Lost and Found | the same |

- **`unowned-or-unbound` becomes `unowned`.** An owner is never "unbound".
- **Unknown positions still fail safe.** With nobody connected nothing is collected. The first player
  to join starts the sweep for items not near them.
- **A container** goes as one entry with its contents, as today.
- **Collection works for an offline owner.** The record is keyed by identity and nothing is sent until
  they ask for their list. Today a record needs a non-zero owner number (`LostAndFound.cs:140-142`).
- **Every collection** sends the inert item to vanilla's Lost and Found storage on the server, and
  the clients' projections retire (unchanged).
- **Reasons** (`LostItemReason`, byte, on the wire: append, never reorder):
  - kept: `OwnerDistance` (now also for offline owners) and `ContainerCleared` (car deleted);
  - used for the first time: `GadgetDestinationMissing` (gadget PR 1), `PurchasedDeliveryFallback`
    (a purchase born straight into its owner's Lost and Found, because the owner is more than 200 m
    away or offline when it comes due, [shops.md](shops.md#owned-items)), `SaveRecovery` (migration and load
    reconciliation), and `InventoryOverflow` (a possession or issued essential with no free slot);
  - new: `BorrowerLeft` and `RetrievedByOwner`.
- **Job papers** stay outside the ledger. A lost booklet goes to the Lost and Found of the player it
  was issued to: the issue moves from a number (`NetworkedJob.cs:102-113`) to an identity. The job
  system still destroys it when the job ends, and the entry goes with it, as today.

Consequence, worth knowing: log off with your toolbox on the platform or your lantern in your cab, and
both are in your Lost and Found 10 s later unless another player is within 100 m. Your list says so.
An online owner's cab items stay where they are, as today.

### The owned-items list

A Career Manager page, "Owned items", next to the existing Lost Items page
(`LostItemsCareerManagerScreen`, `Client/Career/CareerManagerExtensions.cs:202-319`). It is the
player's own UI. The data is the server's.

- The client asks (`ServerboundOwnedItemsRequestPacket`). The server answers with the player's live
  entries (`ClientboundOwnedItemsPacket`).
- Each row: a per-session handle (not the GUID), the prefab, the display name, where it is, and
  whether it can be retrieved, or why not.
- Where it is, worked out on the server:
  - "with you";
  - "with Bentgear29";
  - "near Harbor (HB), 1.2 km";
  - "in car L-042's interior";
  - "on the end of car L-042" (attached);
  - "installed on car L-042";
  - "in your Toolbox, in your inventory" (containers nest);
  - "in Lost and Found";
  - "lost track of it" (`Missing`).
- Ended items aren't listed.
- Retrieving: `ServerboundOwnedItemRetrievePacket(handle)` and a result packet, as for Lost and Found
  retrieval. A refused retrieval answers with the reason and a fresh list.

### Retrieval

Retrieval moves the item into its owner's Lost and Found (`RetrievedByOwner`), wherever it is. The
owner collects it at a shed through the existing retrieval. The owner asked, so nearby-player
protection and the grace period don't apply.

| Where | What happens |
| --- | --- |
| With you, or in Lost and Found | no button |
| World, train interior, shelf | collected now |
| Attached (EOT lantern), snapped (hanger) | detached and collected |
| Another player's inventory or belt | taken from them: their projection retires, and their log and a message say "Liam took back their lantern" |
| Another player's hand | the same, even mid-use. A tool-use presence on it stops. |
| An offline player's inventory | can't happen: [Leaving](#leaving) sends it to Lost and Found |
| In a container | retrieves the outermost container that isn't with you, with everything in it, as one entry. Containers are private to their owner and their contents are never someone else's, so it's yours. |
| Installed on a car | the gadget server's `Retrieve` removal ([gadgets.md](gadgets.md#owned-items)): the gadget comes off natively (wires cut, as removing it by hand does), every player sees the removal in the change log with reason `gadget-retrieved`, and the item goes to Lost and Found instead of a hand or the world |
| In the middle of a transaction (pending recall, deposit, withdrawal) | refused: "busy, try again" |
| `Missing` | no button |

The essential recall (the red silhouette into the claim slot) stays as it is: the fast path for
essentials. It checks identity instead of the number.

**Red** keeps its meaning, "its owner can take it back", which now applies to every item someone else
owns. `InventoryPresentationPlanner` shows red for an item with any owner but the local player. The
client can't tell "owned by someone not here" from "unowned" by number, so item updates gain one bit
for that (an owner flag beside the owner number). This is a protocol change and moves the fingerprint.

### Permissions

The coming permissions system decides what an actor may do with an item. It plugs in at one server-side
function, `ItemAccess.Decide(actor, action, entry)`, in `Multiplayer.Core`, pure and unit-tested. Every
item request handler calls it, and no client decides.

| Action | Today's rule, the default policy |
| --- | --- |
| take from the world, a train, a shelf | anyone |
| take from another player | nobody (vanilla has no way to) |
| deposit into or withdraw from a container | the container's owner, own items only ([container-storage.md](container-storage.md)) |
| install a gadget, remove someone's gadget | anyone ([gadgets.md](gadgets.md#decisions-for-the-user)) |
| retrieve | the owner |
| recall an essential | the owner |
| give ownership (later) | the owner |

The permissions system replaces the policy, not the call sites: friends who may take your tools, a
company's items any member may retrieve. The gadget server's request checks call the same function.

**Companies** (the planned company model, not designed in a doc yet): the owner is a GUID that can name a company
as well as a player, typed by a prefix as `PlayerIdentity`'s Steam IDs are. What a company's Lost and
Found means (any member's shed) belongs to the companies design.

## Open decisions

- **Giving an item to another player.** Ownership never changes today except by the creation paths
  above. An explicit gift action (from the list, or handing over with a confirm) is still open
  ([item-ownership-open-questions.md](item-ownership-open-questions.md#when-should-ownership-transfer)).
  The ledger supports it: an owner change with a reason.
- **Items made by machines or printers** (outside gadgets): recommended owned by the player who
  operated or paid, born `Made`. Decide when one is synced.
- **Old installed gadgets with no owner** in a save from before gadget PR 1: recommended unowned (they
  stay as they are, and anyone may remove them), rather than giving them to the career owner.
- **Companies:** company-owned items, and whose Lost and Found they go to.

## Plan

Not started. `split/client-server` merged into `beta` on 2026-10-21 (#276), and this work goes on
branches made from `beta`, in small PRs, in order, each from a branch `items/<what>`. Each one:

- runs its scenarios on a player host and a dedicated host
  (`RUNS="<scenario> @player; <scenario> @dedicated"`), plus the item scenarios it could affect:
  `rejoin`, `rejoin-crash`, `inventory-persist`, `host-player-persist`, `save-reload`,
  `borrowed-disconnect`, `lost-and-found`, `recall`, `containers`, `shop`, `owned-items`;
- updates this doc's status, [`item-lifecycle.md`](item-lifecycle.md) and
  [`item-ownership-open-questions.md`](item-ownership-open-questions.md);
- keeps every file under the size limit. `AuthoritativeItemRegistry.cs` is 1,166 lines and must not
  grow, so ownership moves out of it into new files:
  - `Multiplayer.Core/Items/OwnedItems/` (the ledger, its reconcile planner, `ItemAccess`);
  - `Multiplayer/Server/Items/OwnedItems/` (the server's ledger, its persistence, retrieval, issuing);
  - `Multiplayer/Client/Career/OwnedItemsScreen.cs`.

It runs beside the gadget work ([gadgets.md](gadgets.md#pr-plan), PR #273). The two plans meet at
four points, and both docs state them the same way:

- **Gadget PR 1 goes before either track.** The ledger counts installed gadgets as one of its stores,
  and its migration reads `dvmpPersistentOwner`. Both come from gadget PR 1.
- **`AuthoritativeItemRegistry.cs` is shared.** Owned-items PR 1 hooks the ledger into every registry
  commit and moves ownership out of that file. Gadget PR 3a adds the install and remove transitions,
  in a file of their own that calls the registry. The two are never in flight at once.
- **Gadget PR 3a comes after owned-items PR 2** (ownership by identity), so a deleted car's gadgets
  reach an offline owner's Lost and Found.
- **Gadget PRs 4, 5 and 9 come before owned-items PR 7.** Those PRs make tape, spools and labels
  server-side, and PR 7 (lineage) records what they replace. Owned-items PR 8 (retrieving installed
  gadgets) comes after gadget PR 3a.

It also meets the shops work ([shops.md](shops.md#owned-items)):

- **The host's purchases.** Shops PR 3 makes every purchase a server transaction, the host's own
  player's included, and births the item in the ledger.
- **Per-player stock.** Shops' per-player stock (shops PR 6) waits for owned-items PRs 1 and 2. It
  uses the count query and change event of [the ledger](#the-ledger).

A dedicated host has no player of its own and a pair has one client, so "an owner who isn't here" is
a made-up identity: the host spawns an item and gives it to a new GUID, as the `owned-items` scenario
already does. "Another player" is the host's own player, so steps that need two players run on a
player host only, and say so.

### 1. The ledger, in shadow, and one id in every store

The ledger is built and saved, with nothing reading it yet. The stores move to the item's own id in
the same PR. That fixes the [cold-withdrawal bug](#known-bugs-the-ledger-removes) as part of the
redesign, not as a separate fix (decided by the user 2026-10-21).

- One id in every store:
  - `RegisterMaterializedColdItem` takes the item's `PersistentWorldItemIdentity` and authored key,
    so a withdrawn item is saved again.
  - The cold record's id is the item's id, and so is the Lost and Found record's.
  - Lost and Found binds saved entries by the id in the inert item's save data, with today's index
    and prefab as the fallback for old saves.
  - `LostItemRegistry.Restore`'s duplicate result is logged at its callers.
- Core: `OwnedItemLedger` (entries, `where`, born/ended, lineage, duplicate check, claim slots
  unique by construction) and `OwnedItemReconciler` (the load table). Unit tests in
  `Multiplayer.Core.Tests/Items/OwnedItemLedgerTests.cs`.
- Server: the ledger is updated at each registry commit point. `OwnedItemsV1` is saved and loaded.
  Migration is described [below](#migration). The duplicate check is at registration.
- The debug-build check that vanilla's lists agree.
- A dvmp eval helper `Ledger.Dump()`.
- Fixture tests of migration in `Multiplayer.Protocol.Tests` (`SaveFormatConversionTests`'s style):
  every store's owned items come out once, with the right owner and `where`, and migrating twice
  changes nothing.
- Scenario **`ledger-save`** (`[Needs(Need.RestartsGames)]`, both hosts):
  1. The client owns items in its inventory, its hand, the world, a train car's interior, a container
     and Lost and Found. On a player host the host's player owns two as well.
  2. The ledger lists each once, with its owner and `where`, and vanilla's lists agree.
  3. Save and restart: the same ids, owners and `where`.
  4. A server step deletes `OwnedItemsV1` from the save and the games restart: migration rebuilds the
     same ledger.
  5. A server step clones an owned item with `Instantiate`, identity and all: the copy gets a new id
     and is logged as a duplicate, and the original keeps its id and placement.
- Scenario **`item-id-roundtrip`** (`[Needs(Need.RestartsGames)]`, both hosts):
  1. The client claims a personal item (`Items.SpawnPersonal`) and the server notes its persistent
     id.
  2. The client deposits it in a container and withdraws it: same id, ledger `where` back to
     `With(client)`, record persisted (`ShouldPersist`).
  3. Save and restart: the withdrawn item is still there, with that id. This is the known bug's
     check.
  4. With the policy tightened, it is collected into Lost and Found and retrieved: same id, and the
     Lost and Found record's id is that id.
  5. It is collected again and the games save and restart: after the restart the entry is bound to
     an item with that id. The client retrieves it: same id.

### 2. Ownership by identity

The ledger becomes the owner's source. `PersistentOwnerPlayerId` is derived, every server check uses
the identity, and Unbind/Rebind go (see [What goes away](#joining-and-rejoining)).

- The first-taker rule checks the ledger, so an offline owner's item is never taken over.
- Lost and Found binds every entry at load. Cold containers take the owner from the ledger and
  withdraw while the owner is offline.
- The owner bit on the wire and red for every foreign-owned item.
- Scenario **`absent-owner`** (`[Needs(Need.RestartsGames)]`, both hosts):
  1. The host gives a personal item to a made-up identity.
  2. The client picks it up: the owner is still the absent identity, not the client, and the client
     shows it red.
  3. The client drops it. Before a restart a Lost and Found entry is made for the absent identity
     (server step); after the restart it is bound at load, not pending.
  4. `owned-items` still passes, with its explanation updated: there is no number to shift any more.

### 3. Lost and Found for everyone

- Offline counts as far, with the wider placements for offline owners. A borrower leaving sends
  items to their owner's Lost and Found. The new reasons. The departing player's hand item goes into
  their inventory.
- Scenario **`offline-lost-and-found`** (both hosts):
  1. With the policy tightened, the host places items owned by a made-up identity: one in the world,
     one in a car's interior, one on a car's end.
  2. The client stays within the protection distance: nothing is collected.
  3. It walks away: after the grace period all three are in the absent identity's Lost and Found.
  4. The online client's own item in that car's interior is not collected.
- Scenario **`borrowed-disconnect`**, changed:
  - The item goes to the owner's Lost and Found when the borrower leaves.
  - On a dedicated host the owner is a made-up identity, so the scenario drops `Need.PlayerHost` and
    covers the offline-owner case too.

### 4. Server-made starting items

The kit, essentials and licence papers are spawned by the server, the safeguards are off in every
game, and the join-packet fields and the rejoin swap go.

- Scenario **`starting-items`** (`[Needs(Need.RestartsGames)]`, both hosts):
  1. The client's install GUID is set to a new one (allowed with `-mp-allow-unverified`), then
     `pair.Rejoin()`.
  2. The client has the kit and every essential, each once, owned by its new identity, born
     `StarterKit`/`Issued`. No adoption request was sent.
  3. Rejoin: the same ids in the same slots, nothing duplicated.
  4. A server step destroys the comms radio, then rejoin: a new radio with a new id, and the old entry
     ended `DestroyedByGame`.
  5. The radio is given to a made-up identity's Lost and Found instead, then rejoin: no new radio is
     issued.
  6. Cleanup restores the client's GUID.
- `host-player-persist` and `rejoin-crash` also run on a player host.

### 5. No adoption

- With nothing left adopting, `TemporaryClientAdoptionCompatibility`, `TemporaryHostAdoptionCompatibility`
  and their packets are deleted. Shop receipts are client-local today and never adopted, and the
  shops work makes them server-made, so nothing in a shop adopts.
- The host's own player buying through the server, owned at once and born `Purchased`, is now shops
  PR 3 ("the purchase as a server transaction", [shops.md](shops.md#owned-items)), with its
  `shop WHO=host` scenario.
- Scenario: `starting-items` and `rejoin` show that no adoption request is sent. A check in
  `ServerRequests` counts none.

### 6. The list and retrieval

- The packets, the Career Manager page, and retrieval for every placement except installed.
- Scenario **`owned-list`** (both hosts):
  1. The client owns items in its inventory, the world, a car's interior, a container in the world and
     Lost and Found.
  2. Its list shows each once with the right kind of place and only those.
  3. A used-up item (a server step deposits a banknote) isn't listed.
- Scenario **`owned-retrieve`** (both hosts):
  1. The client retrieves the world item, the car-interior item and an item inside a container: each
     reaches its Lost and Found (the container whole), and the list updates.
  2. Player host only: the client gives the host's player its lantern, the host holds it in hand, and
     the client retrieves it. The host's hand is empty, its log names the client, and the lantern is
     in the client's Lost and Found.

### 7. Lineage

After gadget PRs 4, 5 and 9, which make tape, spools and labels server-side.

- Replacement scopes around those server-side swaps, and ended entries for money deposits and
  destroys.
- Scenario **`item-lineage`** (both hosts):
  - a deposited banknote's entry ends `MoneyDeposited`;
  - an owned item destroyed by a server step ends `DestroyedByGame`;
  - a duct tape used up through the gadget server's mount request ends with `replacedBy`, and the
    empty roll is born `Replaced`, owned by the tape's owner;
  - none of the ended ones is listed.

### 8. Retrieving installed gadgets

After gadget PR 3a (install and remove, confirmed by the server).

- Scenario **`gadget-retrieve`** (both hosts):
  1. The client installs its gadget on a car, and the host's player stands in that car on a player
     host.
  2. The client retrieves it from its list: the gadget is removed in every game (change-log reason
     `gadget-retrieved`) and is in the client's Lost and Found, still with its id.

### 9. `ItemAccess`

The one decision point with today's rules, called by every item request handler. No behaviour
change: unit tests for the default policy, and the item scenarios as they are.

## Migration

A save written before PR 1 has no ledger. At its first load with the ledger, the server builds one
from what the stores hold. `PerPlayerSaveFormat`'s version-1 conversion runs first, so an old career
is already per player.

| Store | Owned when | Id | Owner |
| --- | --- | --- | --- |
| `WorldItemsV1` | `ownerIdentity` is set | `persistentItemId` | `ownerIdentity`. A record with only a number (from builds before identities) is unowned, and counted in the log. |
| `LostAndFound` records | always | the inert item's `dvmpPersistentWorldItemId`, else the record's own id (unique) | `ownerIdentity` |
| `ColdContainers` | always | the `dvmpPersistentWorldItemId` in `DetachedState`, else the record's id | `PersistentOwnerIdentity` |
| Vanilla installed gadgets | `dvmpPersistentOwner` is set (gadget PR 1) | `dvmpPersistentWorldItemId` | that |
| Vanilla `Storage_Inventory` | never: it's the mirror for vanilla, and conversion has already read it | | |

Then, once:

- **Items lost to the cold-withdrawal bug.** A withdrawn item that today's builds didn't save is in
  no store, so the ledger can't bring it back from one. Migration does what it can:
  - **Host's player:** for the host's own player, DV's `Storage_Inventory` mirror is still written
    from the live inventory, item save data and id included (`RemoveVanillaStorageDuplicates` with
    `keepInventory`). Migration looks there for an entry whose `dvmpPersistentWorldItemId` no store
    has, and restores it into that player's inventory as `SaveRecovery`.
  - **Other players and the world:** nothing on disk holds those items. Migration logs how many
    cold records were withdrawn (the cold graph's operation log, if it has one) and leaves it at
    that.
  - **PR 1 checks the mirror:** whether it really carries such items, against a save made with
    today's build after a withdrawal.
- **Records with an empty id** in a store (`WorldItemsV1` written by a build with the bug) get the id
  from the item's own save data (`dvmpPersistentWorldItemId` in `state`) when it's there, and are
  re-minted otherwise.
- **Duplicates:** the same id in two stores is resolved by the load table's priority.
- **Borrowed items in a saved possession** (another player's item in someone's saved inventory: the
  offline-owner gap) go to their owner's Lost and Found, `SaveRecovery`.
- **Claim slots:** clashes are resolved with `EssentialClaimSlotPolicy`.
- **Starter kits:** `players.<identity>.starterKit` is set for every identity with a `Players` entry,
  so nobody who has played gets a second kit.

Migration is deterministic. Running it twice gives the same ledger, except that a copied id is
re-minted once and found clean the second time. The migration fixture tests check this. Downgrading
needs nothing: an older build ignores `OwnedItemsV1` and reads the stores, which keep their formats.
