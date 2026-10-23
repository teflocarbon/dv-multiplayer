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

- **PR 1a, the ledger in Core: merged (#278, 2026-10-21).**
  - Built: `OwnedItemLedger`, `OwnedItemEntry` and `OwnedItemReconciler` in `Multiplayer.Core/Items/`.
  - Tests: 32 tests in `Multiplayer.Core.Tests/Items/OwnedItem*Tests.cs`.
- **PR 1b, the ledger on the server: built and tested in game on a player host and a dedicated host
  (#282, 2026-10-21).** It is in shadow: it follows the records, is checked against the stores at
  load, and is saved, but nothing reads it.
  - `OwnedItemsServer` and `OwnedItemsSaveCodec` (`Multiplayer/Server/Items/`).
  - The ledger follows the moves and removals `ItemChangeLog` records, and cold deposits.
  - Its load check waits for all three stores to have loaded. In shadow, a step that would move an
    item (dropping a copy, recovering another player's item) is logged, not carried out.
  - An item that registers with an id another live item has is re-minted
    (`OwnedItemsServer.ClaimPersistentId`, from `AuthoritativeItemRegistry.Ensure` and cold
    withdrawal).
  - Cold and Lost and Found records use the item's own id. A withdrawn item keeps its id, which
    fixes the known bug.
  - Saved Lost and Found entries take their item's id at load, and bind by it.
  - `OwnedItemsServer.Audit()` is the check that the ledger, the records and vanilla's storage agree.
    Scenarios call it; nothing runs it on a timer.
  - Tests:
    - the save format in `Multiplayer.Protocol.Tests/OwnedItemsSaveCodecTests.cs`;
    - migration in Core's `OwnedItemReconcilerTests`;
    - scenarios `item-id-roundtrip` and `ledger-save`, both passing on both host kinds.
  - The runs found four bugs, fixed in the same PR (see [Known bugs](#known-bugs-the-ledger-removes)).
- **PR 1c, `ItemAccess`: built and tested in game on both host kinds (2026-10-21).**
  - Core: `ItemAccess.Decide` and `ItemFixtureList` (`Multiplayer.Core/Items/ItemAccess.cs`, 12 tests).
  - Server: `ItemAccessServer`, with `ItemAccessServer.Fixtures` empty.
  - Wired: a player's transition into their inventory (the VR belt included) and a cold-container
    deposit. The deposit's ownership rule moved into the policy, with the same reason string.
    Withdraw, retrieve, recall and gadget removal are in the policy and get wired by the PRs that
    change them.
  - Scenario: `fixture-no-stash`. It passes on both host kinds, alongside `item-release`, `recall`,
    `containers`, `inventory-persist` and `lost-and-found`.
  - The runs found three gaps, fixed in the same PR:
    - On a dedicated host, the server's observation path committed a stash with no actor. The
      check now runs after the state machine accepts, with the holder as the actor.
    - A client skipped a host correction of its own hold as superseded. It now never skips one.
    - Applying the correction left the item in DV's inventory. A refused stash now drops at the
      player's feet.
- **PR 2a, an offline owner keeps their items: built, and tested in game with PR 3 on both host kinds (2026-10-22).** The absent
  owner number, and Lost and Found binding every entry at load (see [2a](#2a-identity-on-the-server)).
  Core tests: `AbsentOwnerTests`. Scenario: `absent-owner`.
- **PR 3, Lost and Found for everyone: built, and tested in game with 2a on both host kinds (2026-10-22); the failures left are [older bugs](#found-in-testing-not-the-ledgers).** An offline owner
  counts as far, a departing borrower sends items home, `SendToLostAndFound` (see
  [3](#3-lost-and-found-for-everyone)). Core tests: `LostAndFoundAwayOwnerTests`. Scenarios:
  `offline-lost-and-found`, `borrowed-disconnect`, and for 2a and 3 together `owner-offline`,
  `car-delete-owner` (item-review L1), `lost-and-found-many-owners`, `offline-sweep-mid-save`,
  `rejoin-mid-sweep` and `deposit-refused`, with their variants ([testing.md](../testing.md)).
- **PR 4, server-made starting items: built, and tested in game on both host kinds (2026-10-22).** The server makes
  the starter kit and missing essentials, the safeguards are off everywhere, and the join fields
  and the rejoin swap are gone (see [4](#4-server-made-starting-items)). Scenario:
  `starting-items`.
- **PR 5, no adoption: built, and tested in game with PR 4 on both host kinds (2026-10-22).** Client item adoption is deleted;
  nothing legitimate still used it (see [5](#5-no-adoption)).
- **PR 6a, the owned-items list: built (2026-10-22), not yet run in game.** The packets, the
  server's rows from the ledger, and an Owned Items page in the career manager (see
  [6](#6-the-list-and-retrieval)). Scenario: `owned-list`.
- **PR 6b, retrieval: built (2026-10-22), not yet run in game.** From the owned-items list into Lost
  and Found: lying items, containers whole, and items with another player (see
  [6](#6-the-list-and-retrieval)). Scenario: `owned-retrieve`.
- **Everything else: not started.** See the [plan](#plan).

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

Found in game by the PR 1b scenarios (2026-10-21) and fixed there:

- **An owned item in the world survived one restart but not two.** `ShouldPersist` kept a world
  item only with vanilla's `BelongsToPlayer`. A restored item is a fresh prefab copy, and a mug's
  prefab doesn't have that flag, so the save after the restart dropped it. Now an item with an owner
  is kept, and restoring an owned item sets the flag again.
- **A mug in vanilla's Lost and Found lost its id at load.** See
  [Where the id travels](#one-id-for-life).
- **A pending Lost and Found entry looked like a second copy.** Until its owner joins, the entry's
  inert item has a record of its own that says World. The load check takes the entry as the item's
  place.
- **A just-joined owner's items on the ground went to Lost and Found seconds after they loaded.**
  Lost and Found took a player's position before their game had sent one: it reads as the origin.
  It also took their first positions, which can be from before the game put them at their saved
  spot. Now an owner's position counts once they have been loaded for a grace period, and a nearby
  player's once they have sent one. It showed on a dedicated host, where no host player stands near
  to protect the items.

### Found in testing, not the ledger's

The 2a and 3 batches (2026-10-21/22) turned these up. Each predates the ledger work; the new
scenarios are what show them.

- **LF-inert (fixed in 6a).** When an item is collected into Lost and Found, every client that knew
  it keeps an inactive inert copy (`ApplyLostAndFoundProjection`), which only a Destroy ends. The
  host's item kept its collection as its last transition, so its final Destroy, when the server
  later destroyed it, said `LostAndFoundCollection` too, and each client took it for another
  collection and kept its copy for good. (The Destroy did reach them: Lost and Found's interest pass
  puts the item back in every player's known items.) Now the final Destroy never carries a
  projection's reason, and a client forgets its tombstone for a destroyed item. It showed as the
  leak check's "client: items the run spawned that couldn't be destroyed" in `owned-list`,
  `owned-retrieve`, `offline-lost-and-found` and `car-delete-owner` (online, absent).
- **CAB-POS.** On a dedicated host, a client standing in a loco's cab doesn't count as near an item
  20 m away. The nearby-player protection collected an absent owner's ground mug 4 s after the
  client boarded (`offline-lost-and-found` @dedicated). On a player host the host's own player
  covers it. Suspected: `ServerPlayer.AbsoluteWorldPosition` while `CarId` is set. Needs a probe.
- **SR+.** `save-reload` on a dedicated host has one or two more items after the second load. Which
  ones isn't recorded yet.
- **COIN.** A client's wallet coin flick was adopted, and picking the coin up credited money on the
  host from nothing (read in the code). PR 5 ends adoption, so the coin vanishes; the flick itself
  should go to the server (shops, money).

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
- **Fixtures stay where they belong.** A station fixture, a scene item that is part of a place
  (a shop's scanner, and other station equipment), belongs to the world, never to a player
  (the shops work found the gap: a player who logged out holding a scanner kept it, and that shop had
  none). See [Fixtures](#fixtures).
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

The exception is an item with no `ItemSaveData` component, such as a mug. Vanilla saves no state for
it (`StorageSerializer` calls `SaveItemData` only when the component exists), so a mug in vanilla's
Lost and Found storage comes back with no id. The mod's own record for it then carries the id: the
Lost and Found entry, or a cold record, which has the item's id as its key. At load the item takes
the id back from its entry (found in game 2026-10-21 by `item-id-roundtrip`).

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
serialized field, so `Instantiate` copies it. The ledger refuses a second item with an id it already
has, live or ended, since an id is never reused:

- The first item keeps the id. The copy gets a new GUID and is born `Duplicate`, with the original's id
  in its lineage. It keeps the owner it was registered with, which is normally the original's.
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
  owner         owner identity (GUID): a player, or a company ([Companies](#companies))
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

The stage-3 test pass (2026-10-21) showed where vanilla gets behind the server's back. The ledger
keeps four rules because of it:

- **Vanilla never moves an owned item.**
  - Vanilla's `RespawnOnDrop` sent personal items to the server's own vanilla Lost and Found while
    the record still said `World`, and clients were sent the item at (0,0,0) (#263). Only the
    ledger's Lost and Found moves an owned item.
  - The check reports an owned item in vanilla's Lost and Found list whose `where` isn't
    `LostAndFound`, and any item at all in the server's own vanilla inventory on a dedicated host:
    there, that inventory belongs to the parked body and must stay empty. Vanilla's job load had
    printed a booklet into it (#261).
- **Inactive is not gone.**
  - DV's train LOD switches off items lying on a parked car, and a lookup of active objects missed
    them (the `booklet-summon` failure).
  - The ledger and the load check never infer an item's absence from its object being inactive. They
    look items up by id among inactive objects too, and by record, not by `FindObjectsOfType`.
- **Only an accepted transition moves the ledger.**
  - A refused one (the `regrab-late` race, `stale-authority-revision`) leaves the ledger as it was,
    as it leaves the record.
  - The ledger is updated where the registry commits, never from what a game observed.
- **Unowned personal items are a gap to close, not a state to keep.** #263 hit items no one owned
  yet: purchases, grants, `Items.SpawnPersonal`. Purchases and grants are born owned now (shops
  PR 2, `/give`), so the first-taker rule remains only for a world-authored personal item.

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
| An ended entry's item, in a store | it lives again where the store has it, and is logged: a build without the ledger played the save |
| Another player's item in a saved possession | sent to its owner's Lost and Found (`SaveRecovery`). Nobody is connected at load, so it breaks the rule that a player carries off only their own and unowned items. |
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
5. **Licence papers:** none. Multiplayer doesn't make them at all (`PhysicalLicensePaperPatch`, found
   in PR 4), so a licence is an entitlement only.
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

### Fixtures

A fixture is a scene-authored item that is part of a place: a shop's scanner, and other station
equipment like it. It is never owned and never leaves its place (decided by the user 2026-10-21).

- **Hand only.** A fixture can't go into an inventory, a belt or a container. A player may carry it
  in their hand, within its place.
- **It can't leave its place.** Carried out of its place's area (the shop it belongs to), the server
  takes it from the hand and puts it back on its authored spot. A fixture never reaches Lost and
  Found.
- **Leaving and saving put it back.** When its carrier leaves, and in every save, a fixture goes back
  to its authored spot. It is saved as part of the scene, never as anyone's possession.
- **Which items are fixtures** is a list the server keeps, by authored key or prefab, starting with
  the shop scanner. The shops work defines which station items belong on it
  ([shops.md](shops.md#owned-items)). It isn't the office stock (mugs, office bins), which a player
  may take away and keep while carrying it.
- **Where it plugs in:** the server's item checks refuse a stash, belt or deposit of a fixture
  (`ItemAccess`, [Permissions](#permissions)), and the item manager watches where carried fixtures
  are.

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
  - used for the first time:
    - `GadgetDestinationMissing`: an installed gadget whose car is gone at load. Gadget PR 1 leaves
      it on vanilla's path, and owned-items PR 3 routes it.
    - `PurchasedDeliveryFallback`: a purchase born straight into its owner's Lost and Found, because
      the owner is more than 200 m away or offline when it comes due
      ([shops.md](shops.md#owned-items)).
    - `SaveRecovery`: migration and load reconciliation.
    - `InventoryOverflow`: a possession or issued essential with no free slot.
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

### Companies

The user decided on a company career (2026-10-21, [company-career.md](company-career.md)). A company
is a persistent group sharing money, licences and owned locos, and each player also has a personal
wallet. For owned items:

- **Owner kinds.** An entry's owner is a player or a company. The owner field gets a kind beside the
  GUID, and a company is keyed by a stable company id.
- **Which items.** Everyday items stay player-owned: tools, lights, cassettes, containers and the
  like. Gadgets, garage keys, loco paint, the wireless MU controller and the loco remote are
  company-owned, even while a member carries them.
- **A company Lost and Found (new).** A company item a member has with them when they leave the
  company goes there, and so do company items the standard rules collect.
- **Retrieval and red follow membership.** The company retrieves its items, so "the owner can
  retrieve an installed gadget any time" becomes "the company can", subject to company permissions
  later (`ItemAccess`). Red marks an item the holder's company doesn't own.

## Open decisions

- **Giving an item to another player.** Ownership never changes today except by the creation paths
  above. An explicit gift action (from the list, or handing over with a confirm) is still open
  ([item-ownership-open-questions.md](item-ownership-open-questions.md#when-should-ownership-transfer)).
  The ledger supports it: an owner change with a reason.
- **Items made by machines or printers** (outside gadgets): recommended owned by the player who
  operated or paid, born `Made`. Decide when one is synced.
- **Old installed gadgets with no owner** in a save from before gadget PR 1: recommended unowned (they
  stay as they are, and anyone may remove them), rather than giving them to the career owner.
- **Companies:** decided in outline ([Companies](#companies)); the details are company-career.md's.

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
  - `Multiplayer.Core/Items/OwnedItem*.cs` (the ledger, its reconcile planner, `ItemAccess`), flat
    like the rest of Core's `Items`;
  - `Multiplayer/Server/Items/OwnedItems/` (the server's ledger, its persistence, retrieval, issuing);
  - `Multiplayer/Client/Career/OwnedItemsScreen.cs`.

It runs beside the gadget work ([gadgets.md](gadgets.md#pr-plan), PR #273). The two plans meet at
four points, and both docs state them the same way:

- **Gadget PR 1 goes before owned-items PR 1b.** The ledger counts installed gadgets as one of its
  stores, and its migration reads `dvmpPersistentOwner`. Both come from gadget PR 1. PR 1a (pure
  Core) touches neither track and goes ahead now.
- **`AuthoritativeItemRegistry.cs` is shared.** Owned-items PR 1b hooks the ledger into every registry
  commit and moves ownership out of that file. Gadget PR 3a adds the install and remove transitions,
  in a file of their own that calls the registry. The two are never in flight at once.
- **Gadget PR 3a comes after owned-items PR 2a** (ownership by identity), so a deleted car's gadgets
  reach an offline owner's Lost and Found.
- **Gadget PRs 4, 5 and 9 come before owned-items PR 7.** Those PRs make tape, spools and labels
  server-side, and PR 7 (lineage) records what they replace. Owned-items PR 8 (retrieving installed
  gadgets) comes after gadget PR 3a.

It also meets the shops work ([shops.md](shops.md#owned-items)):

- **The host's purchases.** Shops PR 2 ("The register on the server") makes every purchase a
  server transaction, the host's own player's included, and births the item in the ledger.
- **Per-player stock.** Shops' per-player stock (shops PR 6) waits for owned-items PRs 1 and 2. It
  uses the count query and change event of [the ledger](#the-ledger).

The plan's scenarios cover issue #242's item scenarios. That issue asks that each is written with its
fix, so the failing run proves the fix:

| #242 | Here |
| --- | --- |
| Inventory saving, for the host's player and a client, both host kinds | `ledger-save`, `item-id-roundtrip` (PR 1b), `starting-items` (PR 4) |
| Essentials can't be lost from the inventory | `starting-items` (PR 4: a destroyed essential is issued again; nothing duplicates on rejoin), `offline-lost-and-found` and `borrowed-disconnect` (PR 3) |
| Essentials are always retrievable | `owned-retrieve` (PR 6), from every place in the [retrieval table](#retrieval) |

Throwing items onto trains (#242's first item, and #148) isn't the ledger's. It stays with the item
release and train wake work.

A dedicated host has no player of its own and a pair has one client, so "an owner who isn't here" is
a made-up identity: the host spawns an item and gives it to a new GUID, as the `owned-items` scenario
already does. "Another player" is the host's own player, so steps that need two players run on a
player host only, and say so.

### 1. The ledger, in shadow, and one id in every store

The ledger is built and saved, with nothing reading it yet. The stores move to the item's own id in
the same work. That fixes the [cold-withdrawal bug](#known-bugs-the-ledger-removes) as part of the
redesign, not as a separate fix (decided by the user 2026-10-21). It is two PRs.

#### 1a. The ledger in Core

Pure logic and unit tests, no game. It touches nothing the gadget or shops work does.

- `OwnedItemEntry.cs`: the entry, `OwnedItemWhere`, born and ended.
- `OwnedItemLedger.cs`:
  - registration with the duplicate check (an id taken, live or ended, is re-minted);
  - moves, ends, replacements, revival;
  - claim slots unique by construction, and the one-off clash repair for old saves;
  - `shopStocked` with `CountLive`, and the `Changed` event;
  - pruning of ended entries;
  - the starter-kit markers;
  - restoring a save.
- `OwnedItemReconciler.cs`: the [load table](#the-ledger) as a plan of steps. Migration is the same
  plan run against an empty ledger.
- Tests: `Multiplayer.Core.Tests/Items/OwnedItemLedgerTests.cs` and `OwnedItemReconcilerTests.cs`.

#### 1b. The ledger on the server

After gadget PR 1, which touches the same server files.

- One id in every store:
  - `RegisterMaterializedColdItem` takes the item's `PersistentWorldItemIdentity` and authored key,
    so a withdrawn item is saved again.
  - The cold record's id is the item's id, and so is the Lost and Found record's.
  - Lost and Found binds saved entries by the id in the inert item's save data, with today's index
    and prefab as the fallback for old saves.
  - `LostItemRegistry.Restore`'s duplicate result is logged at its callers.
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

Two PRs, so the protocol change is one small PR of its own.

#### 2a. Identity on the server

An offline owner's items stay owned. Gadget PR 3a waits for this one.

As built (2026-10-21), the change is narrower than the first plan, which had the owner number
stop being stored and every check move to the identity:

- **The absent number.** The owner number stays the per-session player number, as the wire and the
  Core state machine use it. An owner who isn't connected has
  `ItemTransitionCommand.AbsentOwnerId` (255), which the player-number allocator reserves, so it is
  never a player's. Before, they had 0, which every check reads as "unowned".
- **Where it is set:** at disconnect (`UnbindPersistentOwner`, and Lost and Found's `UnbindOwner`),
  and when a save restores an owner who isn't here (`OwnedItemsServer.OwnerNumber`, used by
  WorldItemsV1, Lost and Found and installed gadgets).
- **What follows from it:**
  - The first-taker rule (owner 0) never takes over an offline owner's item.
  - Nobody else's recall matches it.
  - A departing borrower's test ("someone else's") counts it.
  - Vanilla's own Lost and Found path stays blocked for it.
  - A client sees it as someone else's.
- **Lost and Found binds every entry at load,** with the absent number when the owner isn't here,
  and rebinds it to their session's number when they join. Before, an entry waited, unbound, for
  its owner. A claim takes the owner's number now, not the one saved with the entry.
- **Unchanged:** Lost and Found still doesn't collect an offline owner's items. The owner's position
  is unknown, so they stay protected until PR 3. Cold-container withdrawal still refuses an offline
  owner's item.
- **A withdrawal takes its owner from the cold record's identity,** not from a number worked out a
  frame earlier (item-review's L6). If the withdrawing player leaves in the frame the item wakes,
  the withdrawal is aborted (`withdraw-actor-left`) and the item stays in the box. Before, it became
  an inventory item with nobody holding it and an empty owner.
- **Tests:** Core unit tests (`AbsentOwnerTests`).
- Scenario **`absent-owner`** (`[Needs(Need.RestartsGames)]`, both hosts):
  1. The host gives a personal item to a made-up identity.
  2. The client picks it up: the owner is still the absent identity, not the client.
  3. A server step puts it in the absent player's Lost and Found. After a save and restart, the
     entry is bound to the mug at load, with the absent number, not left pending.
  4. `owned-items` still passes.

#### 2b. Red for every item someone else owns

- The "owned by someone not here" bit in item updates, and `InventoryPresentationPlanner` showing red
  for any owner but the local player. The protocol fingerprint moves.
- `absent-owner` gains: the client shows the absent identity's item red. On a player host the host's
  item shows red in the client's hand.

### 3. Lost and Found for everyone

As built (2026-10-21):

- **Offline counts as far.**
  - In `LostItemCollectionPolicy`, an owner with the [absent number](#2a-identity-on-the-server)
    skips the owner-distance checks.
  - Their items are collected from train interiors and shelves as well as the world, under the
    usual nearby-player protection and grace period.
  - With nobody connected, nothing is collected (player positions unknown).
  - `Collect` lets such an item leave a train or a shelf.
- **A departing borrower** drops someone else's item where they stood, as before, and it is
  collected into its owner's Lost and Found at once (`LostItemReason.BorrowerLeft`), whether the
  owner is online or not.
- **`OwnedItemsServer.SendToLostAndFound(id, reason, detail)`** is the one entry point by id, for
  shops' delivery and later retrieval. It refuses an item that is with a player.
- **Left for later PRs:**
  - Attached and snapped items (an EOT lantern, a hanger) aren't swept yet: collecting them needs a
    detach first.
  - A departing player's own hand item is still dropped where they stood. It should go into their
    inventory, as a save already treats it.
  - `GadgetDestinationMissing` (an installed gadget whose car is gone at load) isn't routed yet. It
    needs gadget sync to tell the item code about the missing car.
- **Tests:** Core unit tests (`LostAndFoundAwayOwnerTests`).
- Scenario **`offline-lost-and-found`** (both hosts):
  1. The host gives two mugs to a made-up identity. The client carries one onto the ground and one
     into a loco's cab, and its own third mug into the cab.
  2. With the client nearby, nothing is collected.
  3. It walks away: both absent player's mugs are in their Lost and Found, under their own ids.
  4. The online client's own cab mug stays in the cab.
- Scenario **`borrowed-disconnect`**, changed: the item must be in the owner's Lost and Found
  after the borrower leaves.

### 4. Server-made starting items

The kit and essentials are spawned by the server, the safeguards are off in every game, and the
join-packet fields and the rejoin swap go.

As built (2026-10-21):

- **`StartingItemsIssuer`** (`Server/Items/OwnedItems/`) runs when a player has loaded
  (`NetworkServer.CompleteLoading`, so the host's own player too, which skips `ReadyForItems`). It
  waits for the ledger's load check, then makes:
  - each of DV's essentials for the career (`AdminItemGrants.StartingItemPrefabs`) the player owns
    no live item of, by the ledger or the stores (`OwnedItemsSummary`), into a free slot with a
    Reserved claim. Born `Issued`.
  - the starter kit (shovel, lighter, oiler, lantern, flashlight, hanger, duct tape), when the
    ledger's saved starter-kit marker is unset, into free slots from 14. Born `StarterKit`. Then the
    marker is set. A migrated save marks every saved player, as before.
  - Each item is made as a cold-container withdrawal makes one
    (`RegisterMaterializedColdItem`, which now takes the move cause): at DV's safety position, its
    state a frame later, registered with owner and slot, and sent to everyone loaded.
  - If the ledger never loads, nothing is made and the next join tries again: better none than a
    second set.
- **Off:** `StartingItemsSafeguard` and `LicensesSafeguard` in every multiplayer game
  (`Shared/Career/StartingItemsControllerPatch`). The client's DV inventory starts empty.
- **Gone:**
  - `PlayerItems`, `OwnedItemPrefabs` and `OwnedItemSlots` from `ClientboundSaveGameDataPacket`, and
    `PlayerItemSaveData`;
  - `StartingItemsClientPatch`, `StartingItemsServerPatch`, `PerPlayerSaveFormat.LocalOwnedItems` and
    `OwnedInSave`, and `IItemServer.OwnedItems`;
  - `NetworkServer.StarterKitsGiven` (`InstallGuidRekey` re-keys the ledger's marker);
  - the rejoin swap: `TryRestoreReturningOwnerItem`, `restoredOriginals` and
    `DiscardUnboundLocalDuplicate`.
- **Licence papers:** none are made in multiplayer today. `PhysicalLicensePaperPatch` returns null
  from all four `CreateLicense` overloads while a session runs, and Lost and Found destroys saved
  ones. So none are issued; licences stay entitlements.
- **Not yet:** claim-slot repair still runs (`EnsureDistinctClaimSlots`). Item-review's E3, E4,
  E5 and E9 stay open until the batch shows what's left.
- Scenario **`starting-items`** (`[Needs(Need.RestartsGames)]`, both hosts). A pair's client keeps
  its identity (the GUID can't be changed from a scenario), so a first join is the client's
  starter-kit marker forgotten:
  1. The client has every essential once. After a rejoin, the same ids.
  2. A server step destroys the comms radio, then rejoin: a new radio with a new id, born `Issued`,
     in the inventory, and the old entry has ended.
  3. The radio is collected into the client's own Lost and Found, then rejoin: no new radio. The
     client retrieves it.
  4. The marker is forgotten, then rejoin: the seven kit items, born `StarterKit`, in the client's
     inventory, and the marker is set again.
- `host-player-persist` and `rejoin-crash` also run on a player host.

### 5. No adoption

- With nothing left adopting, `TemporaryClientAdoptionCompatibility`, `TemporaryHostAdoptionCompatibility`
  and their packets are deleted. Shop receipts are client-local today and never adopted, and the
  shops work makes them server-made, so nothing in a shop adopts.
- The host's own player buying through the server, owned at once and born `Purchased`, is now shops
  PR 2 ("The register on the server", [shops.md](shops.md#owned-items)), with its
  `shop WHO=host` scenario.
- Scenario: `starting-items` and `rejoin` show that no adoption request is sent. A check in
  `ServerRequests` counts none.

As built (2026-10-22):

- **What still adopted, checked first** (the producers a client or a player host's game makes
  locally):
  - Purchases, change and refunds, admin grants, jobs' documents, Lost and Found, containers and
    installed gadgets are made by the server.
  - Receipts are client-local and aren't player property, so a client destroys its own.
  - The host's own player never adopted: its items are server items as they're made.
  - Two producers were left, both harmful:
    - the wallet coin flick. Vanilla `Coin.MakeCoin`, and money is player property. Adopted and picked
      up, it credited $1 on the host, from nothing (by reading the code; not tried in game).
    - DV's developer console spawns.

    Without adoption both vanish on the client. `/give` replaces the console.
  - Vanilla's used-up replacements (an empty tape roll, a spool, a paint can, a label) aren't player
    property, so a client destroyed them before adoption saw them. That is unchanged; gadget PRs 4, 5
    and 9 make them server-side.
- **Deleted:**
  - `TemporaryClientAdoptionCompatibility` and `TemporaryHostAdoptionCompatibility`;
  - both adoption packets and `ItemAdoptionData`;
  - `AdoptionCoordinator` and `HostAdoptionRegistry`, with their tests;
  - the client's request, result and 10-second inventory sweep;
  - the server's `TryAdoptClientItem` and `ServerInitialiseAdoptedItem`.
- **Unbound client items:** an unbound client item is now an authored scene projection (kept
  dormant) or destroyed.
- **Kept:** `ItemTransitionReason.ClientAdoption` and `ItemMoveReason.Adopted`, unused, so the members
  after them keep their numbers on the wire.

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

Split in two, as small PRs: **6a the list**, **6b retrieval**.

6a as built (2026-10-22):

- **Packets:** `ServerboundOwnedItemsRequestPacket` and `ClientboundOwnedItemsPacket`. The answer
  holds parallel rows: handle, prefab, display name, where, retrievable. Retrievable is false until
  6b.
- **The server builds the rows** (`OwnedItemRows`, `Server/Items/OwnedItems/`) from the ledger's
  live entries for the player.
  - Ended entries aren't listed.
  - An item keeps its handle for the session.
  - Where it is is worded by `OwnedItemPlaceText` (Core, unit-tested): "in your inventory", "in
    Bentgear29's hand", "on the ground", "in L-042", "on a shelf", "in Toolbox", "in Lost and
    Found", "installed on L-042". The car's id and the container's name come from the server's
    records.
  - The host's own player is answered in-process.
- **The client** keeps the last list (`OwnedItemsList`), and the career manager's Multiplayer hub
  has an **Owned Items** page beside Lost Items. It's a list only until 6b.
- **Scenario `owned-list`:** step 3 uses a destroyed mug, not a banknote; money is PR 7's.
- **Strings:** the page's labels are English, like the Lost Items page's. Localising both pages is
  left for later.

6b as built (2026-10-22):

- **Packets:** `ServerboundOwnedItemRetrievePacket(handle)` and `ClientboundOwnedItemRetrieveResultPacket`.
  The server answers with the result, then a fresh list. The host's own player is handled in-process.
- **`OwnedItemRetrieval`** (`Server/Items/OwnedItems/`) follows the [retrieval](#retrieval) table, with
  the rule by place in Core (`OwnedItemRetrievalRule`, unit-tested). It decides each row's
  retrievable flag too.
  - **Collected now:** the ground, a train's cab, a shelf. Reason `RetrievedByOwner`, with no
    nearby-player protection or grace.
  - **In a container:** the outermost container that isn't with the owner goes, whole: the walk goes
    up the cold graph to the live shell. One the owner carries means nothing to do.
  - **With another player, hand included:** dropped out of their hand or inventory as a departing
    borrower's are, then collected. The holder gets a whisper ("Liam took back their Lantern"), and
    the change log names the owner.
  - **Refused:**
    - nothing to do: with you, in Lost and Found, or missing;
    - not yet: attached and snapped items (detaching first), and installed gadgets (PR 8);
    - busy, try again: mid-recall;
    - `ItemAccess`'s Retrieve refusals.
- **The Owned Items page:** Confirm retrieves the selected row when it can go, and the status line
  says what happened.
- **Not yet:** detaching attached and snapped items, and the busy check for a container
  mid-withdrawal.

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

Moved up: it is PR 1c, right after 1b, because shops PR 2c (station
[fixtures](#fixtures)) plugs into it, and the shop scanner gap is in play today.

- **The one decision point,** `ItemAccess.Decide(actor, action, item)`, pure in `Multiplayer.Core`
  with unit tests. It covers these actions: stash, belt, deposit, withdraw, take from the world,
  take from a player, remove a gadget, retrieve, recall.
- **Called from the server's existing request checks.** Its default policy is today's rules, so no
  behaviour changes.
- **A fixture list, empty to start.** `Decide` refuses stash, belt and deposit for an item on the
  list. Shops PR 2c fills it and adds the return to its spot.
- **Tests:** unit tests for the default policy and the fixture refusal, and the item scenarios as
  they are.

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
