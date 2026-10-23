# Item refactor

> Status: plan (2026-10-23); R0 and R1 built on `items/refactor`, R2 ([wire.md](items/wire.md)) in #339, 2026-10-24. Line numbers are from `beta` at
> `04bfe277`. Decisions are the user's, through the coordinator, dated in [Decisions](#decisions);
> all are answered. Every requirement is a row in
> [item-overhaul-checklist.md](../item-overhaul-checklist.md), and this plan names the rows each PR
> closes. The current item docs ([item-lifecycle.md](../legacy/items/item-lifecycle.md) and the docs it links) are
> legacy (L1): evidence of today's behaviour, not design. The new docs are written from scratch
> ([Docs](#docs)).

The owned-items work (PRs 1a–7, #278–#312, and the fixes #324 and #326) added a ledger of owned
items, but it was narrowed PR by PR. The ledger follows the server's item records instead of
deciding anything, ownership is saved twice, owners and holders are still per-session numbers with
identities bolted on, and an ordinary item's inventory slot isn't saved at all. This plan rebuilds
the item system around one rule, with clean names and one folder per subsystem. Gadgets, shops and
item review are paused while it runs: this plan builds only the seam installed gadgets need, and
marks every other workstream's rows paused, not removed.

## Goals

From the user, 2026-10-23:

- **(a) Store the items.**
- **(b) Give them back on reload:** in the player's inventory in **the same slots**, Lost and Found
  saved, their items still in their cold containers.
- **(c) Items can't get lost,** especially essentials. "There are way too many ways for items to
  technically get lost in the game, hence the design of the ledger."
- No admin restore tool and no audit log, unless one is the only way to meet (a)–(c). Nothing below
  needs either: the [invariant check](#the-invariant-check) is a check that fails loudly, not a log
  of events, and `ItemChangeLog` stays what it is today, the debug event stream.

## The guarantee

> Every owned item is, at every moment, in exactly one known home. That home is saved, and the item
> comes back there on reload. Anything that would leave it nowhere sends it to its owner's Lost and
> Found.

The homes:

| Home | Names | Example |
| --- | --- | --- |
| `Slot(holder, slot)` | an exact inventory slot of a player, by identity (the VR belt is slots 33–35) | every inventory item, not just essentials |
| `Hand(holder, hand)` | a player's hand, by identity | a lantern being carried |
| `World` | loose, with its pose | a toolbox on the platform |
| `TrainInterior(car)` | loose in a car, by the car's GUID, with its car-local pose | a mug in a cab |
| `Shelf(key)` | on a static parent, by its key | an item on a station shelf |
| `Container(container, slot)` | in a cold container, by the container's id | a flashlight in a toolbox |
| `LostAndFound` | its owner's Lost and Found, with the reason and time | a lantern left 300 m away |
| `Installed(anchor, gadget uid)` | a gadget on a car (by its GUID) or on the world (vanilla's `WorldCustomization`, key `:global:`) (the [seam](#the-gadget-seam) only) | a bolted plate; a mod's trackside gadget |
| `Attached(car, front)` | an EOT lantern on a car's end | |
| `Snapped(parent)` | a hanger, a mount | |

Gadgets install on a car or on the world (G5), so `Installed` names one of the two. The world
anchor is never missing; a car can be.

`Hand` is a home while the player is connected. A save writes a hand item to a free inventory
slot of its holder (as today), so a saved home is never `Hand`.

**Cold and live homes** (D12). A home is cold when nobody is using it: an offline player's
inventory and claim slots, Lost and Found, and a container. An item in a cold home is data only,
its saved state plus its ledger entry if it's owned, and has no object on any game, so no vanilla
path, sweep or bug can move or destroy it. Every other home is live: the item is an object people
can see and touch.

- **Going cold:** when a holder leaves, each item in their inventory (and, after D5's free slot,
  their hand) goes cold in one ledger change: the server takes its state through `ItemSaveData`
  and the object is destroyed on every game. This replaces today's "keep the originals" on the
  server (`ServerItemManager.PlayerItems`). Lost and Found and containers already work this way.
- **Going live:** when an owner joins, each cold item in their inventory is made from its state in
  its exact slot before their inventory is shown; a retrieval or a withdrawal makes the item in
  the slot or hand the server chose.
- **Every inventory is server data,** online or not. A connected player's inventory objects are
  a view of the server's. The client is optimistic, as game servers usually are: a stash, a slot
  move, a hotbar switch (slot to hand), a drop or a pick-up shows at once, with no wait, and goes
  to the server as a request (R2's one request, one result). The server commits it to the ledger
  in its own order and confirms; only a refusal rolls the client back to the server's view. The
  client never changes an inventory without telling the server, so the server never learns of one
  after the fact.
- **No duplicate on the server** (OI-COLD-06): an item in a connected remote player's inventory is
  data only on the server, like a cold item; that player's game holds the only object. The server
  keeps objects only for live homes it shows (the world, cars, shelves) and the host's own player.
- **Cold inventories, client included** (D14, user): further than the bullets above, a
  player's inventory holds no objects on their own game either. The client draws it from the
  server's rows, as for a cold container, and an item becomes an object only when it is taken
  out to a hand or the world (made at once from its row, predicted, confirmed by the server).
  Where it is built is decided after a sizing of vanilla's inventory (OI-COLD-09 to -11).

## Decisions

| # | Decision | Date |
| --- | --- | --- |
| G1 | The goals above; no admin restore tool, no audit log | user, 2026-10-23 |
| G2 | The guarantee, the homes, and the three names below | user, 2026-10-23 |
| G3 | **No migration.** Existing careers may break and start fresh. Migration rows and converters for old item data are dropped (noted in the checklist, rows kept) | user, 2026-10-23 |
| G4 | Save keys and packet classes may change | user, 2026-10-23 |
| G5 | **Gadgets install on vehicles and on the world** (`WorldCustomization`), because some mods need world installs; not on the player house, the Lost and Found shed or the paint station | user, 2026-10-21 (vehicles only); world added 2026-10-23 |
| G6 | Keep the vanilla save as the server's store, for other mods' save data | user, 2026-10-19 |
| G7 | Ended ledger entries: the newest 100 per owner are kept | user, 2026-10-21 (built, #278) |
| D1 | **An old save** loads the world and the career (money, licences, trains, jobs) and starts every player's items fresh: the old item keys are ignored and dropped at the next save, everyone gets a new starter kit and essentials, and the server logs once what it dropped | user, 2026-10-23 |
| D2 | **Vanilla's item lists** are a write-only mirror, never read in multiplayer: the host's own player's inventory goes into `Storage_Inventory`, so the career still opens in single player with the host's things; nothing managed goes into `Storage_World` or `Storage_LostAndFound`: Lost and Found's items are saved only in the mod's key, like every other item | user, 2026-10-23 |
| D3 | **Red for every item someone else owns,** online or not. The wire tells each player its relation to an item (mine, someone else's, nobody's), never an owner number. Closes OI-ESS-12 | user, 2026-10-23 |
| D4 | The rows [outside this refactor](#rows-outside-this-refactor) are marked paused | user, 2026-10-23 |
| D5 | Built here, as decided earlier: a leaving player's own or unowned hand item goes into a free slot (OI-JOIN-06); job papers are never owned or in the ledger, and a lost booklet goes to the Lost and Found of the player it was issued to, by identity (OI-ID-12, OI-LF-17); the load check carries out what it only logs today (OI-PER-06, -07) | user, 2026-10-23 (decided 2026-10-21) |
| D6 | Of the 18 doc-only findings, 7 are real gaps built to the old design's rule (OI-CON-08, OI-SYNC-09, OI-SYNC-10, OI-PHY-05, OI-PHY-11, OI-PHY-12, OI-GAD-06); the other 11 are rewritten to the code's rule (OI-LF-18, OI-LF-19, OI-CON-10, OI-SYNC-02, -03, -05, -06, OI-PHY-09, -10, OI-LED-14, OI-PER-10) | user, 2026-10-23 |
| D7 | **The invariant check** fails loudly in Debug. In Release it logs the error and applies the guarantee: an owned item outside its ledger home is put there, else into its owner's Lost and Found | user, 2026-10-23 |
| D8 | **An integration branch,** `items/refactor`, off `beta` | user, 2026-10-23 |
| D9 | **An item whose saved state won't load** is made fresh from its prefab, with the same id, in its owner's Lost and Found (`SaveRecovery`), and an error is logged: its own state (a lighter's fuel) is lost, the item isn't. If the prefab itself is gone (an item mod removed), the entry stays `Unloadable`, its saved row kept verbatim and retried at each load, so putting the mod back brings it back. `Missing` goes | user, 2026-10-23 |
| D10 | **Binning** destroys the item, as in vanilla, and ends its entry `Discarded`, by the player who binned it; an essential is issued again at its owner's next join; only your own or unowned items can be binned (someone else's is refused and drops by the bin) | user, 2026-10-23 |
| D11 | **Items from other mods** (custom_item_mod and the item packs it loads) will be built like vanilla items (their prefabs spawn on every game, each item's own saved state reaches clients, a client joins only with the same item mods), **paused for after the refactor** (the user, later the same day). Mod-specific compatibility (Railway Brake Shoe) isn't planned: the user is talking to that mod's developer | user, 2026-10-23 |
| D12 | **Cold and live homes.** An item whose home nobody is using is cold: data only (its state, and its ledger entry if owned), with no object on any game. Cold homes: an offline player's inventory and claim slots, Lost and Found, a container. A leaving holder's inventory goes cold, and a joining owner's is made from its state in its exact slots. **Every inventory is server data:** a connected player's inventory objects are a view. The client is optimistic: it shows a stash, slot move, hotbar switch, drop or pick-up at once and sends it as a request; the server commits it to the ledger in its own order and confirms, and only a refusal rolls the client back. On the server, an item in a connected remote player's inventory is data only too (no duplicate object); that player's game holds the only object (OI-COLD-06) | user, 2026-10-24 |
| D14 | **Cold inventories, like cold containers.** A player's inventory holds no game objects on any game, their own included: it is rows on the server (item, exact slot, state), and the client draws its inventory, hotbar and essential outlines from those rows, as the cold container UI does for a box. Taking an item out (to the hand, a drop, a use) makes its object from its row at once on that player's game (predicted, so it never waits) and the server confirms or rolls back; putting it away destroys the object and it is a row again. Only items in hands and in the world are objects. This goes further than D12's online half, where the player's game kept its inventory objects as a view | user, 2026-10-23 ("ColdInventories like we have with ColdContainers"), recorded 2026-10-24 |
| L1 | **The current item docs are legacy.** They are evidence of today's behaviour, never a template or settled design; new docs are written from scratch, one per subsystem under `docs/design/items/`, and the legacy docs are in `docs/legacy/items/` (moved 2026-10-24, [Docs](#docs)) | user, 2026-10-23 |
| C1 | Cold containers start fresh (D1), and their new save must make the failures behind F18 (an empty graph written over a save the import refused), the depth check and quarantine impossible by design, not by a stopgap | user, 2026-10-23 |

## Three names

Used in code, docs and this plan, and nothing else for these things:

| Name | Today | What it is |
| --- | --- | --- |
| **ledger** | `OwnedItemLedger`, `OwnedItemsServer` | Owned items only. It alone decides each one's **owner** and **home**. Keyed by the item's GUID, saved. |
| **live state** | `AuthoritativeItemRegistry.Record` | Every item, owned or not, right now: NetId, pose, physics, parent, the holder this second, what each client is shown, revisions. For an owned item it follows the ledger for owner and home, and carries no owner or essential claim of its own. For an unowned item its placement is the item's home. |
| **saving and loading** | `WorldItemPersistence*`, `OwnedItemsSaveCodec`, the stores' own save code | Writes the vanilla save and reads it back. It holds no opinions: what it writes is the ledger and the live state, and what it reads it hands to them. |

**On a conflict, the ledger wins on owner and home, and live state on pose and physics.**

## What's wrong today

Both of the problems the user named are real; checked in the code on `04bfe277`.

### 1. Ordinary items don't keep their inventory slot

- **Live state keeps a slot only for an essential's claim.** The record's one slot field is
  `InventoryClaimSlot` (`AuthoritativeItemRegistry.cs:36`). Every accepted transition clears it for
  anything that isn't an essential (`:261`: `retrievalEligible ? snapshot.InventoryClaimSlot : -1`),
  and clients send a slot only for essentials (`NetworkedItem.StateApplication.cs:904-907`).
  Three paths write a slot for an ordinary item into the same field (a Lost and Found retrieval
  `AuthoritativeItemRegistry.cs:684`, a container withdrawal `NetworkedColdContainerManager.cs:382`, the starting items
  `StartingItemsIssuer.cs:251-254`), and it lasts until the item's next transition.
- **`WorldItemsV1` saves only `claimSlot`** (`WorldItemPersistenceRecord.cs:21`, `:60`).
- **Restore fills the backpack from the front.** With no claim slot, `HandleInventoryOrHandState`
  calls `InventoryIntegration.AddActive(gameObject)` with no slot
  (`NetworkedItem.StateApplication.cs:677`), which is DV's `AddItemToInventory(item, false)`, the
  first free slot (`InventoryIntegration.cs:138`). `AddActive` takes a preferred slot; no caller
  passes one.
- **Vanilla had the slot.** `Storage_Inventory`'s `inventorySlotIndex` is per item, essential or not.
  The mod reads it once, in the old-career conversion (`PerPlayerSaveFormat.cs:145`), and empties
  that list at every load (`:85`). Records never had an ordinary item's slot, so a client's items
  never kept theirs. What e6e7ac31 (2026-10-17) took away was the host's own: before it, DV restored
  the host's inventory from `Storage_Inventory` by `inventorySlotIndex`; since, the host's items come
  back through the records like everyone's.
- **The ledger doesn't have it either.** `OwnedItemWhere.Slot` exists (`OwnedItemEntry.cs:80`) and is
  saved (`OwnedItemsSaveCodec.cs:64`), but `WhereOf` fills it from `InventoryClaimSlot`
  (`OwnedItemsServer.cs:312-313`), so it is -1 for an ordinary item, and nothing reads it to place
  an item. The audit ignores slots (`OwnedItemsServer.cs:244`).
- **A slot move inside the inventory isn't a change at all:** it is never sent to the server and
  never logged.

### 2. Session numbers next to identities

- The record keeps the owner, the holder and the claim's player as per-session `byte` numbers
  (`PersistentOwnerPlayerId`, `PlacementPlayerId`, `InventoryClaimPlayerId`, from a8675db1,
  2026-07-14, when host and client were one game), with identities added beside two of them
  (`PersistentOwnerIdentity`, `PlacementPlayerIdentity`, e7563656, 2026-07-18). Lost and Found's
  entries keep `OwnerPlayerId` and `OwnerIdentity`, the cold graph keeps string identities, and the
  Core state machine, `ItemAccess`, `LostItemCollectionPolicy` and `InventoryPresentation` decide by
  number.
- Outside tests there are 138 reads of `PersistentOwnerPlayerId`, 64 of `PlacementPlayerId`, 65 of
  `InventoryClaimPlayerId` and 30 of Lost and Found's `OwnerPlayerId`. The decisions among them:
  the state machine's possessor, first-taker and recall rules (`ItemAuthorityStateMachine.cs:143-223,
  242-259`), Lost and Found's eligibility (`LostAndFound.cs:66, 74`), red
  (`InventoryPresentation.cs:61, 68`), access (`ItemAccessServer.cs:27-28`), interest
  (`ServerItemManager.Interest.cs:158-167`), the logout rules (`ServerItemManager.PlayerItems.cs:100,
  138`), the containers (`NetworkedColdContainerManager.cs:120, 296-311, 553-562`), the issuer, the
  grants, shop delivery and the replenishment.
- So the numbers need binding: `RebindPersistentOwner` and Lost and Found's `RebindOwner` at login
  (`NetworkServer.cs:942-944`), `UnbindPersistentOwner` (owner to 255, holder and claim to 0) and
  `UnbindOwner` at logout (`ServerPlayer.cs:40`). An offline owner's items looked unowned until
  #304 reserved 255 for them.
- What still goes wrong with an absent owner: a deposit's record falls back to the depositor's
  identity when the number is 255 (`NetworkedColdContainerManager.cs:184-185`); an offline owner's
  container shell isn't bound to them (`:549-555`); Lost and Found's unbind skips an entry with an
  empty identity, which keeps a number the next player to join may be given (`LostAndFound.cs:299`);
  and Lost and Found still saves a claim's session number nobody reads
  (`NetworkedLostAndFoundManager.cs:1004`).

### 3. The ledger follows, and ownership is saved twice

- The record commits a change and the ledger follows it (`ItemChangeLog.RecordMove` →
  `OwnedItemsServer.OnMove`, `:82-100`). An owner change the ledger didn't make is only logged.
  Everything but the list, retrieval and the issuer reads the record (OI-LED-05 to -07).
- The owner is saved in `WorldItemsV1.ownerIdentity`, `ColdStoredItemRecord.PersistentOwnerIdentity`,
  `LostItemRecord.OwnerIdentity`, an installed gadget's `dvmpPersistentOwner`, and
  `OwnedItemsV1`. An item's state is saved by whichever store holds it, so the load check has to
  work out which copy wins, and two of its steps (`DropCopy`, `RecoverToLostAndFound`) are only
  logged (OI-PER-06, -07).
- The rejoin reads held-back `WorldItemsV1` records, not the ledger (OI-JOIN-02); the issuer falls
  back to `OwnedItemsSummary` (OI-START-04); the claim slots are repaired at runtime (OI-ESS-01).

## The target model

### The ledger

`ItemLedger` (Core, today `OwnedItemLedger`) holds one `LedgerEntry` per owned item, live or ended:

```text
LedgerEntry
  id          the item's GUID, for life (the key)
  owner       OwnerRef: a player's identity (a company later: the type has room, nothing else)
  prefab
  home        Home, the table above, exact
  claim       an essential's reserved slot in its owner's inventory, or none
  born        reason, time, by (identity), from (id), detail
  ended       reason, time, replacedBy (id), detail; none while live
  shopStocked vanilla's Restock flag (kept; shops' use of it is paused)
```

- **Every owned item's home is exact.** `Slot(holder, slot)` names the slot for every inventory item,
  so (b) is the ledger's own data, not a guess at load. An essential's `claim` is the slot it comes
  back to; its home is wherever it is. Two live entries never share one holder's slot, and two
  claims of one owner never share a slot: the ledger refuses a change that breaks either, so the
  runtime claim repair (`EnsureDistinctClaimSlots`) goes.
- **A change to an owned item is a ledger transaction first.** A request (a stash, a drop, a slot
  move, a deposit, a collection, a retrieval, a recall, an install) is decided by the server's
  rules, then committed as one `LedgerChange` (from home, to home, owner, cause). The ledger
  refuses a change whose `from` isn't the entry's home, so a stale or doubled request changes
  nothing. Only then does live state apply it and the clients hear of it. Today the order is the
  other way round: the record commits and the ledger follows (`ItemChangeLog.RecordMove` →
  `OwnedItemsServer.OnMove`), and an owner change outside the ledger is only logged.
- **Ownership changes only through the ledger,** with a reason: born (`Purchased`, `StarterKit`,
  `Issued`, `Granted`, `Claimed`, `Replaced`, `Duplicate`; `Made` reserved), and, later, a gift.
  `Migrated` goes with migration (G3).
- **No `Missing`** (D9). At load every entry's item is made in its home from its saved
  state; one that can't be is recovered to Lost and Found or kept `Unloadable`.
- The ledger is pure Core, unit-tested, and knows nothing of NetIds, sessions or Unity.

### Live state

`LiveItems` (server, today `AuthoritativeItemRegistry`) keeps a `LiveItem` per item with a live
object; a cold item (D12) has none, and its state is kept beside the ledger until it goes live. A
live item has: NetId, revision, pose, physics lease, world parent, the holder this second (a connected
session), the claim flags a client shows, and per-player projection. For an owned item:

- its owner is `ledger.OwnerOf(id)`, read, never stored;
- its placement is the ledger home's, written only by applying a `LedgerChange`;
- an essential's claim slot is the ledger's.

`PersistentOwnerPlayerId`, `PersistentOwnerIdentity`, `PlacementPlayerId`, `InventoryClaimPlayerId`
and `InventoryClaimSlot` leave the record. The holder this second is the session, found from the
home's identity when it's applied; nothing stores a player number.

An unowned item has no ledger entry: its live placement is its home (a found mug in a slot is
`Slot(holder, slot)` in live state), and saving writes it from there. The invariant check covers
owned items; the slot check (one item per slot) covers both.

### Saving and loading

One key, `Multiplayer.Items`, written in one pass and read in one pass (G4):

```json
"Items": {
  "format": 1,
  "ledger":   [ { "id": "…", "owner": "…", "prefab": "…", "home": { … }, "claim": 35, "born": { … } } ],
  "states":   { "<id>": { "prefab": "…", "pose": { … }, "state": "<the item's ItemSaveData JSON>" } },
  "unowned":  [ { "id": "…", "home": { … }, "pose": { … }, "state": "…" } ],
  "authored": [ { "key": "…", "override": { … } }, { "key": "…", "destroyed": true } ],
  "containers": [ { "id": "…", "item": "<the shell's item id>", "capacity": 12 } ],
  "lostAndFound": [ { "id": "…", "reason": "OwnerDistance", "utc": 0 } ],
  "players":  { "<identity>": { "starterKit": true } }
}
```

- **Each item's state is saved once,** under its id, in `states` (owned) or `unowned`. The owner is
  saved once, in `ledger`. A container's contents are items whose home is `Container(id, slot)`,
  so a container is just its capacity and its shell; a Lost and Found entry is a reason and a time.
  Nothing else saves an owner, a slot or a state. That removes ownership saved twice
  (`WorldItemsV1.ownerIdentity`, `ColdStoredItemRecord.PersistentOwnerIdentity`,
  `LostItemRecord.OwnerIdentity`, `dvmpPersistentOwner`, and the ledger), and it removes the load
  check's "in several stores" case by construction (OI-PER-06 becomes a property of the format).
  The one exception is an installed gadget: vanilla's gadget system rebuilds it from
  `Storage_InstalledGadgets`, so its state stays there, and the mod's key holds only its ledger
  entry (`Installed(anchor, uid)`); its owner is no longer written into that state.
- **Rows stand alone** (C1). A row that won't load is kept verbatim and written back unchanged
  (OI-PER-15, F18); one bad row never empties the rest. There is no whole-graph blob to refuse, and
  nesting depth is checked when a deposit is decided (OI-CON-15), so a save can't hold a deeper
  graph.
- **Loading** runs once, before anyone logs in: read `ledger`, then make each live entry's item in
  its home from its state if the home is live (an item in a cold home stays data until it goes
  live, D12) (a train-interior item when its car exists, else Lost and Found:
  OI-PER-12), then the unowned items, then the authored overrides. No player-facing request is
  served until it has run (OI-CON-05, F19).
- **Old saves** (D1): a save without `Multiplayer.Items` starts items fresh. `WorldItemsV1`,
  `OwnedItemsV1`, `LostAndFound` and `ColdContainers` are ignored, logged once, and not written
  again.
- **Vanilla's lists** (D2): `Storage_Inventory` gets the host's own player's inventory, slot for
  slot, as a mirror; `Storage_World` gets nothing managed; `Storage_LostAndFound` gets nothing.
  Multiplayer never reads them. Each item's `ItemSaveData` stays the serializer of its own state:
  the mod wraps it, never reimplements it.
- **When:** as today, in the postfix of `SaveGameManager.UpdateInternalData` on the server, for
  saves, autosaves, joins' save packets and disconnects. The ledger and live state are read
  together, with no tick between, so the save is one consistent picture.

### Identity only

Ownership and holding are identities: `OwnerRef` and the holder's identity in a home. A session
player number exists only at the wire and session edge:

- the server finds a connected player's session from their identity when it applies a change, sends
  a packet, or asks where a player is;
- a packet names a holder by session number (clients know players only by that), and never names an
  owner (D3: the relation to the recipient instead);
- nothing binds or re-binds at login or logout: `UnbindPersistentOwner`, `RebindPersistentOwner`,
  Lost and Found's `UnbindOwner`/`RebindOwner`, its per-entry `OwnerPlayerId`, and
  `ItemTransitionCommand.AbsentOwnerId` (255) go. An offline owner is an identity with no session.

The Core state machine (`ItemAuthorityStateMachine`) takes identities instead of bytes, and so do
`ItemAccess`, `LostItemCollectionPolicy`, the recall and the first-taker rule.

### The wire

Today's item protocol (checked 2026-10-23): one update shape that every client gets the same
(`ItemUpdateData`, in `CommonItemUpdatePacket` both ways and `CommonItemsBulkUpdatePacket`), with
five player numbers in it (`PersistentOwnerPlayerId`, `InventoryClaimPlayerId`, `PlayerId`,
`OriginatingPlayerId`, the cause's `PlayerId`); and seven request flows that each correlate,
version and refuse differently:

| Flow | Correlation | Expected revision | Refusal |
| --- | --- | --- | --- |
| A move (stash, drop, throw, take) | none: the client sends an `ItemUpdateData` | `AuthorityRevision` | a correction, no result |
| Recall | none; the server makes an operation id for prepare / prepared / result | yes | string |
| Lost and Found retrieve | `RequestId` + Lost handle | yes | string |
| Owned retrieve | `RequestId` + owned handle | no | string |
| Container browse / mutation | `RequestId`, and a client-made GUID `OperationId` | two | status byte and string |
| Money deposit, below-map, wake witness, projection ack | no reply | | |

Items are named three ways (NetId; a handle from one of three separate handle spaces for Lost and
Found, owned items and containers; a container slot plus handle), lists are parallel arrays, the
Lost and Found snapshot sends NetIds beside its handles, and there's dead weight: the bulk packet's
commented-out compression and serverbound path, the `ItemPosition` flag (it writes nothing),
`ClientAdoption`, fields no client reads (`BaseRevision`, `PreparedRevision`, `Generation`,
`StateVersions`, `ChildItemCounts`, `HasMore`), and a stale `manifest-enum-verify.json` at the root.
No item's GUID reaches a client today, which stays the rule.

The new protocol:

- **Naming an item.** A NetId for an item with a live object. An `ItemHandle` (`uint`, one space for
  the session, mapped to the GUID on the server only) for anything a client sees only as a row: a
  Lost and Found entry, an owned-items row, a container slot. A GUID is never on the wire
  (OI-ID-07): a protocol test fails if an item packet has a `Guid` field.
- **Naming a player.** A holder by session number, the one place a number stays: clients know
  players only by it. An owner never: each player is told its **relation** to the item (`Mine`,
  `SomeoneElses`, `Nobodys`, D3), and only the owner is told the claim (slot and flags). The cause's
  actor stays a session number and a name, for display.
- **One update.** `ClientboundItemUpdatesPacket`: a list of `ItemUpdate` (Create, Change, Destroy),
  each with NetId, revision, cause, the home as the client needs it (`World` and pose,
  `TrainInterior(car NetId)` and car-local pose, `Shelf(key)`, `Hand(holder, hand)`,
  `Slot(holder, slot)`, `Attached(car NetId, front)`, `Snapped(parent)`, `Installed(car NetId or world, uid)`;
  an item going into a container or Lost and Found is a Destroy with its cause), relation, claim
  (owner only), prefab and authored key on Create, and object state. Built per recipient (relation
  and claim differ), sent by interest. It replaces both of today's update packets.
- **One request, one result.** `ServerboundItemRequestPacket`: a `RequestId` (per client, counting
  up), the item (NetId or handle), the expected revision, and an action with its arguments: `Take`,
  `Stash(slot)`, `MoveSlot(slot)`, `Drop(pose)`, `Throw(pose, direction)`, `Recall(slot)`,
  `Retrieve`, `CollectLost(slot)`, `Deposit(container, slot)`, `Withdraw(slot)`,
  `MoveInContainer(container, slot)`, `DepositMoney`, `ReportBelowMap(height)`. The action enum is
  append-only. `ClientboundItemResultPacket`: the `RequestId`, accepted or a refusal (a byte enum,
  append-only, plus a detail string), and the revision it made. What changed arrives as updates. The
  server remembers each session's recent results, so a repeated `RequestId` gets the same answer and
  does nothing twice (F21, OI-CON-07). A client's own move is a request, never an update: today's
  serverbound `CommonItemUpdatePacket` goes.
- **Recall is one step,** like retrieval from a hand: the server commits it and the holder's game is
  sent the update that empties their hand. The prepare / prepared / result exchange and its 5 s
  timeout go.
- **One list.** `ClientboundItemListPacket` (Lost and Found, owned items, a container's view) with
  rows as structs (`ItemRow`: handle, prefab, display name, where, flags, slot), not parallel arrays.
  `ServerboundItemListRequestPacket` asks for one.
- **Unchanged:** the spatial packets (lease, sample, settlement, commit; the simulator is a session
  number at the edge), the wake witness, the projection acknowledgement, the authored catalogue
  (OI-SYNC-08 makes a collision refuse only the colliding keys). Gadget, shop and job packets are
  their workstreams'; a job booklet's `IssuedToPlayerId` becomes the identity on the server
  (OI-LF-17), and stays a number on the wire.
- **Removed:** `CommonItemUpdatePacket`, `CommonItemsBulkUpdatePacket`, the recall packets, the four
  Lost and Found / owned-items packets, the two container packets, `ServerboundMoneyItemDepositPacket`,
  `ItemPosition`, `ClientAdoption`, `ReturnedToRejoiningOwner`, and the root
  `manifest-enum-verify.json`. The protocol fingerprint moves once, in the wire PR.

Every new packet gets a fixture test in `Multiplayer.Protocol.Tests` (as `ItemPacketFixtureTests`
does today) and a debug projector.

### The invariant check

`HomeInvariant` (Core, pure) takes the ledger and what the server observes, and returns every
violation:

1. every live entry's item is in its home, and only there: one live object (or one stored row, for
   a container or Lost and Found) carries its id;
2. live state agrees with the home (placement, holder identity, slot, car, container);
3. no owned item exists without a live entry, and no ended entry's item exists;
4. one item per holder slot, owned and unowned together, and one claim per owner slot;
5. vanilla's `StorageController` lists agree: an item is in the list its home implies and no other
   (`ItemInMultipleStorages`), nothing is in a dedicated host's own inventory, and no owned item is in
   vanilla's Lost and Found list unless its home is `LostAndFound`.

It runs at load (after loading), before every save, and in Debug builds every 5 s. In Debug a
violation logs an error and a debug event (`item.invariant`) and fails the scenario that is
running: the scenario runner calls it after every run and fails the run on any violation
(OI-LED-09). In Release (D7) it logs the error and puts the item in its ledger home, else in its
owner's Lost and Found. Core unit tests cover each rule and a fuzz run of random ledger changes
(OI-LED-04: a refused change leaves the ledger as it was).

### One path for every host

The host's own player and a dedicated host take the same path, with no special cases (E5):

- the host's own player's requests go through the same request handlers as a client's (today
  `LocalPlayerSession.Items` reaches most of them in-process; recall and a few others still branch
  on the host);
- the issuer, the claim rules, the rejoin and the save treat the host's player as any player, keyed
  by identity (`ReadyForItems` stops being skipped for it: `NetworkServer.cs:1062-1066`);
- a dedicated host's own vanilla inventory stays empty (the invariant check, rule 5).

Every behaviour PR proves its rows on both host kinds.

### The gadget seam

Only what installed gadgets need, nothing of their features (paused):

- the `Installed(anchor, gadget uid)` home, the anchor a car or the world, with the real UID
  (OI-GAD-04, OI-GAD-10);
- an install and a removal are ledger changes like any other; the gadget server calls the ledger,
  the ledger doesn't call the gadget server;
- an installed gadget's owner is the ledger's: `dvmpPersistentOwner` goes (it saved the owner a
  second time);
- at load an `Installed` entry whose car is gone goes to its owner's Lost and Found
  (`GadgetDestinationMissing`, OI-LF-22); at runtime a deleted car does the same (as #314 does);
  a world-installed gadget has no car to lose, and is saved and restored like a car's;
- the item code never destroys an installed item and logs a refused destroy (OI-GAD-06, D6).

The paused gadget drafts (#313–#320) rebase onto the new names after the rename PR; the
[rename map](#rename-map) is their guide.

### Mod items

Other mods add items, and some need them to work in multiplayer (D11). custom_item_mod is the common
one: it loads item packs (UMM mods with an `item_info.json`), names each item's prefab
`custom_item_mod/<name>/prefab` and patches `Resources.Load` to resolve that name. Its gadgets have
no car requirement and no soldering, which is why world installs matter (G5). Paused for after the refactor (D11); when it
resumes, it builds the item layer, nothing mod-specific:

- **Prefabs from mods** (OI-MOD-01): the server spawns, saves and restores a mod item by its
  prefab name like a vanilla item, and every client builds it from the same name. A name a game
  can't resolve is logged and kept `Unloadable` (D9), never dropped. Today both lookups read only
  vanilla's item list, so a custom item can't be built on a client or restored at load (and is
  then erased at the next save); the lookup falls back to `Resources.Load`, as vanilla's load does.
- **Each item's own state** (OI-MOD-02): an item's `ItemSaveData` JSON, where mods keep their
  state, goes with the item when a client first sees it and again when the server's copy changes,
  applied through vanilla's `ItemSaveDataLoaded`. The server's copy is the truth; a client's goes up
  with the request that changes the item (a placement, a drop).
- **The same item mods on join** (OI-MOD-03): `ModCompatibilityManager` already refuses a client
  missing a mod the server has; custom_item_mod is registered `All`, and a scenario proves a client
  without an item pack is refused with its name.

Shop stock from custom_item_mod (OI-MOD-04) waits for shops, its gadgets (OI-MOD-05) for gadgets.
Railway Brake Shoe, which builds on custom_item_mod, isn't planned here (the user is talking to its
developer).
Testing needs an item pack in the test install; test games otherwise load only Multiplayer and the
dev tools.

## Every vanilla path

Every path in DV that creates, moves or destroys an item, checked against the research docs and a
decompile of `Assembly-CSharp` and `DV.Inventory` (2026-10-23). The rule for each: **blocked**
(vanilla's path doesn't run in multiplayer), or **routed** (it becomes a request or a server step
that commits a ledger change, so the home moves with it). "Managed" below means the item has a
ledger entry, not `BelongsToPlayer`: vanilla flips that flag in the dumpster, shop, gadget and
withdrawal paths, so nothing decides on it after R3.

| Path | Vanilla | Today | Target | PR |
| --- | --- | --- | --- | --- |
| `StorageController` lists | `AddItemToStorageItemList` takes the item out of its old list, then checks `acceptsNonEssential`; a refused move leaves it in none (H3) | `StorageControllerPatch.cs:32-69` checks first for an owned item; unowned items still hit H3 | Blocked as a decision maker for every item: the lists follow the item's home (`StorageIntegration`), never the other way; rule 5 of the invariant checks they agree | R3 |
| Vanilla Lost and Found sweeps and the shed | `MoveItemsFromWorldToLostAndFound`, `ForceSummonAllWorldItemsToLostAndFound`, `RequestLostAndFoundItemActivation`, the shed's access point, `ItemTransformControllerLostAndFound.ActivateItems` | blocked while a session runs (`StorageControllerPatch.cs:91-132`) | Blocked (as today); the server's collection is a ledger change to `LostAndFound` | kept; R3 |
| Other `AddItemToLostAndFound` callers | `StorageItemTransformController`, `ItemSnapState.ToggleInteraction`, `ItemContainer.Clear`, `GadgetItem` load with a missing anchor, `GlobalShopController.InstantiatePurchasedItems` | suppressed for owned items (`:71-89`), vanilla for unowned; a gadget with a missing anchor is left nowhere or in vanilla's list | Blocked for every item; each caller that matters is routed: a gadget with no car → its owner's Lost and Found (`GadgetDestinationMissing`), a container's clear → its contents stay in their container rows, shop staging → the shop's own delivery (paused) | R4, R6 |
| `RespawnOnDrop` | player-owned → vanilla Lost and Found; else reset to spawn or destroy | skipped for owned items (`RespawnOnDropPatch.cs:18-55`); "near" means any player (`RespawnOnDropRangePatch.cs`); an unowned `BelongsToPlayer` item can sit in vanilla's list while its record says World | Blocked for owned items (the server's collection decides); for unowned items only the reset-to-spawn of authored stock runs, as a server step; never vanilla's list | R6 |
| Safeguards | `StartingItemsSafeguard`, `LicensesSafeguard`, and `CombineAndResolveIllegalDuplicates`, which drops "duplicate" radios, wallets and licences, from the Lost and Found list first | the two safeguards are off (`StartingItemsControllerPatch.cs:17-23`); **`CombineAndResolveIllegalDuplicates` still runs on the host at load** (new finding, from the code) | All three blocked in multiplayer; essentials and the kit are the issuer's | R6 |
| Vanilla save and load | five storage lists; `InitializeInventory` places by slot (a collision leaves the loser in no slot, H2); `AddItemToInventoryFallback` writes to the Lost and Found list | vanilla's inventory is loaded empty (`PerPlayerSaveFormat.cs:85`); owned rows are stripped from vanilla's world list; vanilla's Lost and Found and installed lists still hold those items' state | Read nothing in multiplayer; write the mirror only (D2); every item from `Multiplayer.Items` | R4 |
| `AItemContainer` | nested containers by id; `ItemContainer.Clear` sends contents to Lost and Found | cold containers: contents are server records; the UI is routed (`ColdContainerUiPatch.cs`); the shell has its own id (`ColdContainerIdentityPatch.cs`) | Contents are items whose home is `Container(id, slot)`, the id is the shell's item id, deposit and withdrawal are requests | R5 |
| Car deletion | `DeleteCar`, `DeleteTrainCars`: items on the car's LOD go back to the world (`TrainItemActivityHandler.UnregisterAndActivateItems`), gadgets are forced off at their pose | `CarDeleteLostAndFoundPatch.cs:15-29` → `CollectFromDeletedCar`: owned interior items to Lost and Found, anything else destroyed (LF2); attached and snapped items not handled | Routed: an owned item on or in the car (interior, attached, snapped, installed) → its owner's Lost and Found; an unowned one → the world beside the track, as vanilla (a fixture to its spot, paused) | R6 |
| `DeleteTrainCarsFromTrack` | the same coroutine; no vanilla caller found | not patched (LF5) | Patched like the others | R6 |
| Job papers | `BookletCreator` makes overviews, booklets and reports; load reprints booklets into the local inventory; the office bin and job end destroy them | registered on the server (`BookletCreatorPatch.cs`); a dedicated host doesn't reprint (`JobBookletRecoveryPatch.cs`); never saved; first taker owns one (F7) | Never owned, never in the ledger (D5); a lost booklet → the Lost and Found of the player it was issued to, by identity (OI-LF-17) | R6 |
| Consumables | duct tape (`Inventory.DestroyItem` and an empty roll), the solder spool swap, the label maker and `DestroyOnGadgetUnlink`, `PaintSprayer.Apply` (an empty can), `Padlock.RemoveKey` (a used garage key) | duct tape, solder and labels run on the client: the server keeps the item and the client's replacement has no NetId; the sprayer isn't handled; only the host can use a key (`KeyUsePatch.cs`) | Routed through one server step, `UseUp(item, replacement)`: the server destroys the used item, makes the replacement, and calls `RecordReplacement` so the entry ends and the replacement is born `Replaced`. R6 wires the sprayer and the key; the gadget tools (tape, solder, labels) call it when gadgets resume (OI-LIN-02 to -04, paused) | R6 |
| Bins and dumpsters | `ItemDumpster` clears `BelongsToPlayer`, takes the item off the world list, and `RespawnOnDrop` destroys it at 75 m | not handled: an owned binned item's destroy is suppressed and Lost and Found refuses it (new finding, from the code): it lies there for ever | Routed: the bin is a request; the server destroys your own or an unowned item and ends an owned one's entry `Discarded` (D10); someone else's is refused and drops by the bin | R6 |
| Dev console | `Dev.SpawnItem`, `Dev.SpawnMoney`, `Debug.DestroyAllItems`, `Debug.DestroyBuggedObjects`, `Debug.DeleteCarsAndJobsSaveData` | not patched; a client's spawn vanishes (no adoption); a host's spawn is unowned; destroys on the host go out as "destroyed by game" | Routed: a spawn is a server step (unowned, as an admin `/give` without a target); a destroy ends the entry with the cause `DevConsole`; on a client both are refused with a message | R6 |
| Money | `WalletAccelerationReaction` → `Coin.MakeCoin` and `TrySpend`; `AddItemToInventory` destroys `ShouldDestroyOnUse` money | a note is deposited with the server first (`MoneyPatch.cs:44-53`), which ends the entry `MoneyDeposited`; a client's coin flick is local and the coin vanishes | The deposit is a request (`DepositMoney`); the coin flick is paused with shops (OI-SHOP-09) | R2, R6 |
| Shops | purchases staged in Lost and Found, then put on the counter; receipts; restock on destroy | the server puts purchases on the counter, owned by the buyer, unowned if they left (`ShopDelivery.cs:20-51`) | Paused (D4): the seam is that a purchase is born owned through the ledger, as today | paused |
| Snapping | `ItemSnapPointBase`; the item stays in the world list; coupling or chain moves unsnap it; a coupler snap saves the car's GUID | coupler snaps go out as `Attached`; `SnappedAttachment` is never assigned; neither is saved by the mod | `Attached(car, front)` and `Snapped(parent)` homes, saved; an unsnap is a ledger change to `World`; a missing car at load → the owner's Lost and Found | R3, R4, R6 |
| Fall rescue, tunnelling, the grabber | vanilla has no rescue | the terrain guard catches tunnelling; fall rescue covers `World` items only and commits no revision; `GrabberRecoveryPatch` moves no item | Fall rescue is a committed change for every place (OI-PHY-11, D6) | R7 |
| Licence papers, grants | `BookletCreator.CreateLicense` | no papers in multiplayer (`PhysicalLicensePaperPatch.cs`); a grant adds to the inventory | Kept; a grant is born `Granted` in a slot or, with none free, the owner's Lost and Found (the issuer's rule) | R6 |
| World streaming | `ItemDisabler` deactivates, never destroys | client projections retired client-side; unowned office stock forks destroyed after 30 s | Kept (inactive is not gone); office stock in a cab paused (OI-SYNC-12) | — |
| Inventory hazards H1–H8 | a swap leaves a stale reserved map (H1); a load slot collision (H2); H3; slot and lock confusion (H4–H6); a destroyed item never purged (H7); an item saved once per list (H8); `ItemLocationForcer` drops and purges on fast travel | only H3 is handled (owned items); the issuer avoids part of H2 | Slots are the ledger's and live state's (R3): a client's DV inventory is a view the server corrects; H7 and the fast-travel drop are routed as requests | R3, R6 |
| Leaving | | another player's item → its owner's Lost and Found; your own hand item dropped (SL2); an item borrowed across a restart stays with the borrower (SL1) | A hand item → a free slot (D5); another's item → its owner's Lost and Found, at the leave and at load | R4, R6 |

## Every known way an owned item is lost today

From the code on `04bfe277`, [item-bug-review.md](../research/item-bug-review.md) (ids E, SL, LF, D),
item review's triage of #322's 49 findings (F*n*), and the checklist. "Lost" includes left in no
slot, duplicated, misplaced so its owner can't find it, and hidden.

| # | How | Effect | Where | Closed by |
| --- | --- | --- | --- | --- |
| 1 | An ordinary item's slot isn't saved; a reload refills the backpack from the front | misplaced (b) | problem 1 above | R3 (slot as home), R4 (saved, restored) |
| 2 | A cold-container save the import refuses is overwritten by an empty graph at the next save (F18) | **every stored item of every player** | `NetworkedColdContainerManager.cs:59-66, 581-605` | R4 (rows stand alone; nothing writes over an unread row), R5 |
| 3 | A deposit made before the containers' import is wiped by it (F19) | stored items | `NetworkedColdContainerManager.cs:43-49, 71-258` | R4 (no request before the load) |
| 4 | A restored shell mints a new container id before its state loads (F23) | a whole box orphaned | `ColdContainerIdentity.cs:11-22` | R5 (the container id is the shell's item id) |
| 5 | A half-retired deposit is saved both in the box and as a possession (F17) | duplicate | `ColdContainerGraph.cs:542-556` | R4 (one state per item), R5 |
| 6 | A failed withdrawal leaves the ledger moved (F12) | ledger wrong until the next load | `NetworkedColdContainerManager.cs:391-424` | R3 (ledger change first, rolled back as one), R5 |
| 7 | A repeated container operation id isn't idempotent (F21) | latent loss | `NetworkedColdContainerManager.cs:197-239` | R2 (remembered `RequestId`) |
| 8 | A withdrawal trusts the client's slot; into a full inventory the item is in no slot (LF3) | in no slot | `NetworkedColdContainerManager.cs:265, 382` | R5 (server's slot), R6 (every full-inventory route) |
| 9 | A Lost and Found retrieval or a recall into a full inventory: the client counts a refused claim as placed (OI-START-10) | in no slot | `NetworkedItem.StateApplication.cs:673-679` | R6 |
| 10 | A silhouette that arrives after an ordinary item took its slot is refused for good (E1) | essential hidden | `InventoryIntegration.cs:141-171` | R3 (the ledger owns the slot; the client makes room) |
| 11 | A client restores another owner's claim onto its own essential's slot (E2b) | in no slot | `StateApplication.cs:814-821` | R2 (claims go only to their owner), R3 |
| 12 | Claim repair aims at the VR belt and ranks by a slot the server may have wrong (E4) | recall impossible | `EssentialClaimSlotPolicy.cs:82-86` | R3 (the repair goes) |
| 13 | The host's own player's claim clashes are never repaired (E5) | essential hidden | `NetworkServer.cs:1062-1066` | R3 (no repair; one path for every player) |
| 14 | An item borrowed when the server restarts is saved under the borrower and released only to them (SL1) | owner never gets it | `WorldItemPersistenceSave.cs:64` | R4 (another player's item in a saved hand or slot goes to its owner's Lost and Found) |
| 15 | A leaver's own hand item is dropped where they stand, off its train, under a lease; an unowned one isn't saved (SL2, F9) | misplaced; a found item lost | `ServerItemManager.PlayerItems.cs:102-128` | R6 (into a free slot, D5) |
| 16 | A scene-authored item in an inventory survives one restart, not two (SL3) | lost | `AuthoritativeItemRegistry.cs:147` | R4 |
| 17 | A saved row that fails to restore once is erased at the next save (SL5) | lost (offline players' too) | `WorldItemPersistenceRestore.cs:29-45, 129` | R4 (rows stand alone; `Unloadable`, D9) |
| 18 | An essential recalled out of a moving cab keeps its lease; the save writes the lease's place (SL4) | displaced | `AuthoritativeItemRegistry.cs:572` | R7 |
| 19 | A lease's item is saved whatever the rules say; a refused host settlement never ends its lease; a Lost and Found collection mid-lease keeps it (F25, F39, F40) | duplicates | `WorldItemPersistenceSave.cs:40-43`, `ServerItemSpatial.cs:62-72`, `AuthoritativeItemRegistry.cs:602-636` | R7 |
| 20 | An unowned item in a deleted car is destroyed (LF2) | lost (a found item; the shop scanner) | `NetworkedLostAndFoundManager.cs:288-303` | R6 |
| 21 | `CarSpawner.DeleteTrainCarsFromTrack` skips the Lost and Found patch (LF5) | lost if anything calls it | `CarDeleteLostAndFoundPatch.cs:15-29` | R6 |
| 22 | A dynamic owned `Attached` item (a bought EOT lantern) isn't saved or managed (F26) | owner lost at reload | `WorldItemPersistenceSave.cs:155-159` | R4 (the `Attached` home is saved) |
| 23 | An offline owner's attached and snapped items are never collected or retrievable (OI-LF-04, OI-LIST-14) | stuck | `LostAndFound.cs` places | R6 |
| 24 | A train-interior item is restored at once; a missing car has no recovery (F27) | misplaced | `WorldItemPersistenceRestore.cs:98, 186, 196-201` | R4 |
| 25 | An item settled on a shelf leaves every client's interest (F42) | invisible until a reload | `ServerItemManager.Interest.cs:143-208` | R7 |
| 26 | A settled train item arrives live (dynamic) on a late joiner (F34) | misplaced on the client | `NetworkedItem.StateApplication.cs:128-133` | R7 |
| 27 | A job booklet becomes owned by its first taker, and `Missing` at every load | listed, then missing | `ItemAuthorityStateMachine.cs:174-186` | R6 (D5) |
| 28 | An owned item no store holds at load becomes `Missing` for ever | lost track of | `OwnedItemReconciler.cs:134-139` | R4 (D9) |
| 29 | An install GUID re-keyed to a Steam id skips the ledger (F3) | list empty, retrieval refused | `InstallGuidRekey.cs:61-73` | R1 |
| 30 | Absent-owner gaps: a deposit's record takes the depositor's identity, an offline owner's shell isn't bound, a Lost and Found entry with no identity keeps a number the next player gets | wrong owner | problem 2 above | R1 |
| 31 | A dedicated host collects a rejoined owner's ground item just after a restart (D1); a player in a cab doesn't count as near (OI-JOIN-11) | collected early | `NetworkedLostAndFoundManager.cs:727-730` | R6 |
| 32 | A cab item stays in vanilla's Lost and Found list after a load (D2) | duplicate risk | `WorldItemPersistenceRestore.cs:64-115` | R4 (vanilla's lists are a mirror; invariant rule 5) |
| 33 | A dedicated host's save-reload gains an item or two (OI-PER-17) | duplicate | unexplained | R4 (one state per item; the invariant check names the copy) |
| 34 | A thrown item freezes mid-air on a dedicated host if its thrower leaves (F32) | stuck in the air | `ServerItemSpatial.cs:186-188` | R7 |
| 35 | A lost projection Create blocks re-entry for 5 s (F45) | hidden briefly | `ClientItemManager.Projection.cs:70-73` | R7 |
| 36 | Vanilla's inventory hazards (a stale reserved map after a swap, load slot collisions, a refused storage move leaving an item in none: H1–H8) | in no slot | `docs/research/item-bug-review.md` | R3 (slots are the ledger's), R6 |
| 37 | A wallet coin flick vanishes on the client since #307 (OI-SHOP-09) | money lost | `Client/Items/MoneyPatch.cs` | **paused with shops** (D4); R6 keeps the deposit path whole |
| 38 | Office stock left in a parked cab is destroyed after 30 s (LF8) | unowned, by design | `AuthoredWorldItemReplenishmentManager.cs:26` | **paused** (D4, needs a decision; not owned) |
| 39 | Vanilla's `CombineAndResolveIllegalDuplicates` still runs on the host at load and drops "duplicate" radios and wallets, from the Lost and Found list first, which the mod's Lost and Found import binds to (new, from the code) | an essential in Lost and Found | `StartingItemsControllerPatch.cs:17-23` leaves it on; `NetworkedLostAndFoundManager.cs:497-521` | R6 (blocked), R4 (nothing is loaded from vanilla's lists) |
| 40 | An owned item put in a bin: its destroy is suppressed and Lost and Found refuses it, so it lies in the dumpster for ever and its entry never ends (new, from the code) | stuck, uncollectable | `RespawnOnDropPatch.cs:18-55`, `NetworkedLostAndFoundManager.cs:306` | R6 (D10: destroyed, entry ends `Discarded`) |
| 41 | A consumable used up on a client (duct tape, solder, a label) is destroyed only there; the server keeps it and the client's replacement has no NetId | desync; the replacement exists on one game | gadget tools; `OwnedItemsServer.RecordReplacement` has no caller | R6 builds `UseUp`; the gadget tools are **paused** (OI-LIN-02 to -04) |
| 42 | A purchase whose buyer has left is delivered unowned | owner lost | `ShopDelivery.cs:20-51` | **paused with shops** (OI-SHOP-04) |
| 43 | An `Installed` gadget whose car is gone at load is left nowhere (vanilla's missing-anchor path is suppressed for owned items) | lost | `StorageControllerPatch.cs:71-89` | R4 (OI-LF-22) |

Fixed already and kept fixed by the regression set: E2a (#326), E10 (#324), E3, E7, E8, E9 (#306,
#307), LF1, LF4, LF6 (#304), LF7 (#311).

## Code layout

One folder per subsystem inside each side's `Items` folder, the namespace its folder's (the rule
`CodeLayoutTests.EveryFileIsInARoleFolderWithItsNamespace` and the analyzer's DVMP0002 already
enforce under `Multiplayer/`), and tests mirroring the folders. A side has only the subsystems it
has code for.

| Subsystem | What | Core | Server | Client | Shared |
| --- | --- | --- | --- | --- | --- |
| `Ledger` | owners and homes of owned items; the invariant | ✓ | ✓ | | |
| `LiveState` | every item now: the record, the state machine, the item component | ✓ | ✓ | ✓ | ✓ |
| `SaveLoad` | `Multiplayer.Items`, item identity in `ItemSaveData` | ✓ | ✓ | | |
| `Inventory` | slots, the belt, claims as a client shows them | ✓ | ✓ | ✓ | ✓ |
| `Essentials` | starting items, the issuer, claim rules | ✓ | ✓ | | |
| `LostAndFound` | collection, entries, retrieval | ✓ | ✓ | ✓ | |
| `Containers` | cold containers | ✓ | ✓ | ✓ | |
| `OwnedList` | the owned-items page and retrieval | ✓ | ✓ | ✓ | |
| `Access` | `ItemAccess`, fixtures | ✓ | ✓ | | |
| `WorldItems` | scene-authored items, the catalogue, static parents, office stock | ✓ | ✓ | | ✓ |
| `Projection` | interest and what each client is shown | ✓ | ✓ | ✓ | |
| `Physics` | leases, samples, settlement, throws, fall rescue, the terrain guard | ✓ | ✓ | ✓ | ✓ |
| `TrainWake` | items in moving trains | ✓ | ✓ | | ✓ |
| `ChangeCauses` | why an item moved or went away | ✓ | ✓ | ✓ | ✓ |
| `VanillaStorage` | the guards that keep DV's own storage, Lost and Found and respawn from moving an item | ✓ | ✓ | | ✓ |
| `Behaviours` | one item type's own sync: flashlight, lantern, lighter, shovel, page book, money, the grabber | ✓ | ✓ | ✓ | ✓ |

Recall has no folder of its own: it is a ledger change (`Inventory` on the server). The packets stay
in `Shared/Protocol`: the item packets move to `Shared/Protocol/Packets/Items/` in the wire PR, when
they are replaced (a packet's wire id hashes its full name, so moving one changes the fingerprint),
and the data stays in `Shared/Protocol/Data/Items/`.

**Partials stay together.** A partial class can't span namespaces, so `NetworkedItem.*`,
`NetworkedItemManager.*`, `ServerItemManager.*`, `ClientItemManager.*` and the persistence partials
move as whole classes in the rename PR, into the subsystem most of their code belongs to. The
behaviour PR that touches a part that belongs elsewhere (a fall-rescue or recall part) makes it a
class of its own in its own folder. That's noted in the map as "split in Rn".

### Rename map

The rename PR (R0) does exactly this, and nothing else. A row with "→ R*n*" also changes or goes
in that later PR. Paths are under each project's `Items/` unless they say otherwise.

**`Multiplayer.Core/Items/`** (namespace `Multiplayer.Core.Items.<Subsystem>`):

| Today | Becomes |
| --- | --- |
| `OwnedItemLedger.cs` (`OwnedItemLedger`, `OwnedItemChange`, `OwnedItemClaimMove`, `OwnedItemBirthRequest`, `OwnedItemRegistration*`) | `Ledger/ItemLedger.cs` (`ItemLedger`, `LedgerChange`, `ClaimMove`, `BirthRequest`, `Registration*`) |
| `OwnedItemEntry.cs` (`OwnedItemEntry`, `OwnedItemWhere`, `OwnedItemPlace`, `OwnedItemHold`, `OwnedItemBirth`, `OwnedItemBorn`, `OwnedItemEnded`) | `Ledger/LedgerEntry.cs` (`LedgerEntry`, `Home`, `HomeKind`, `HoldKind`, `BirthReason`, `Birth`, `End`). R3 makes `Home` exact and drops `HoldKind` and `Missing` |
| (new, R3) | `Ledger/HomeInvariant.cs`, `Ledger/OwnerRef.cs` |
| `OwnedItemReconciler.cs` | `SaveLoad/LoadReconciler.cs` → R4 replaces it with `SaveLoad/ItemLoadPlanner.cs` (no stores to reconcile, no migration) |
| `ItemAuthorityStateMachine.cs` (`ItemAuthorityStateMachine`, `ItemAuthorityState`, `ItemTransitionCommand`, `ItemRecallCommand`, `ItemAuthorityResult`, `ItemAuthorityStore`, `AuthorityPlacement`, `AuthorityClaimFlags`) | `LiveState/ItemStateMachine.cs` (`ItemStateMachine`, `LiveItemState`, `ItemTransition`, `RecallCommand`, `TransitionResult`, `LiveItemStore`, `LivePlacement`, `ClaimFlags`). R1 takes identities, not bytes |
| `ItemOutboundRevisionPipeline.cs`, `ItemSnapshotApplicationPolicy.cs`, `ItemStateObservationPolicy.cs` | `LiveState/` (names unchanged) |
| `InventoryClaimPlanner.cs`, `InventoryPresentation.cs`, `BeltItemPolicy.cs` | `Inventory/SlotClaimPlanner.cs`, `Inventory/InventoryPresentation.cs`, `Inventory/BeltSlots.cs` |
| `EssentialClaimSlotPolicy.cs` | `Essentials/ClaimSlotPolicy.cs` → goes in R3 (the ledger assigns claims) |
| `LostAndFound.cs` (433 lines, nine types) | `LostAndFound/LostItemCollectionPolicy.cs`, `LostItemRegistry.cs`, `LostItemRetrievalPlanner.cs`, `LostItemEntryReconciler.cs`, `LostItemTypes.cs` (one file per type group, code unchanged) |
| `../Containers/ColdContainerGraph.cs`, `ColdContainerSnapshotCodec.cs` | `Containers/ContainerGraph.cs` (`ContainerGraph`, `ContainerRecord`, `StoredItemRecord`, ...), `Containers/ContainerSnapshotCodec.cs` → R4 replaces the codec with rows |
| `OwnedItemPlaceText.cs` (`OwnedItemPlaceText`, `OwnedItemRetrievalRule`, `OwnedItemPlaceFacts`) | `OwnedList/PlaceText.cs` (`PlaceText`, `RetrievalRule`, `PlaceFacts`) |
| `ItemAccess.cs` | `Access/ItemAccess.cs` |
| `AuthoredHierarchyPolicy.cs` | `WorldItems/` |
| `ItemPossessionInterestPolicy.cs`, `RemoteItemProjectionPlanner.cs`, `ItemWireStateProjection.cs` | `Projection/` |
| `ItemReleaseSync.cs`, `ItemSpatialStreamValidator.cs` | `Physics/` |
| `TrainItemWakePlanner.cs` | `TrainWake/` |
| `ItemChangeReasons.cs` | `ChangeCauses/` |
| `StorageTransitionPlanner.cs` | `VanillaStorage/` |
| `GrabberRecovery.cs`, `PageBookSyncPlanner.cs`, `TrackedStateComposer.cs` | `Behaviours/` |

`Multiplayer.Core.Tests/Items/` mirrors it: each `<Type>Tests.cs` moves beside its type's folder
(`Ledger/ItemLedgerTests.cs`, `LiveState/ItemStateMachineTests.cs`, ...); `Containers/` moves under
`Items/Containers/`. `AbsentOwnerTests` goes in R1 (there's no absent number), its cases rewritten as
identity cases in `ItemStateMachineTests`.

**`Multiplayer/Server/Items/`** (namespace `Multiplayer.Server.Items.<Subsystem>`):

| Today | Becomes |
| --- | --- |
| `AuthoritativeItemRegistry.cs` (`AuthoritativeItemRegistry`, `Record`) | `LiveState/LiveItems.cs` (`LiveItems`, `LiveItem`). R1 and R3 take owner, holder number and claim out; the Lost and Found and claim-repair parts go to their subsystems in R3 |
| `NetworkedItemServer.cs`, `NetworkedItemServerPart.cs`, `HostItemActor.cs` | `LiveState/` (names unchanged) |
| `OwnedItemsServer.cs` | `Ledger/ServerLedger.cs`; its save part moves to `SaveLoad` in R4, its audit becomes the invariant check in R3 |
| `OwnedItemsSaveCodec.cs` | `SaveLoad/LedgerSaveCodec.cs` → R4: `SaveLoad/ItemsSaveCodec.cs` |
| `WorldItemPersistenceManager.cs`, `…Save.cs`, `…Restore.cs` (one partial class) | `SaveLoad/ItemSaveLoad.cs`, `ItemSaveLoad.Save.cs`, `ItemSaveLoad.Restore.cs` (`ItemSaveLoad`) |
| `WorldItemPersistenceRecord.cs` | `SaveLoad/SavedItemRow.cs` |
| `PersistentWorldItemIdentity.cs`, `PersistentWorldItemIdentityPatch.cs` | `SaveLoad/ItemIdentity.cs`, `SaveLoad/ItemIdentityPatch.cs` (`dvmpPersistentOwner` goes in R4) |
| `OwnedItems/StartingItemsIssuer.cs`, `OwnedItemsSummary.cs` | `Essentials/StartingItemsIssuer.cs`, `Essentials/OwnedItemsSummary.cs` → goes in R3 (OI-START-04) |
| `OwnedItems/OwnedItemRows.cs`, `OwnedItems/OwnedItemRetrieval.cs` | `OwnedList/OwnedItemRows.cs`, `OwnedList/Retrieval.cs` |
| `NetworkedLostAndFoundManager.cs` (1,038 lines) | `LostAndFound/LostAndFoundServer.cs`; its save part moves to `SaveLoad` in R4 |
| `../Trains/CarDeleteLostAndFoundPatch.cs` | `LostAndFound/CarDeleteLostAndFoundPatch.cs` |
| `NetworkedColdContainerManager.cs`, `ColdContainerIdentity.cs`, `ColdContainerIdentityPatch.cs` | `Containers/ContainerServer.cs`, `Containers/ContainerIdentity.cs`, `Containers/ContainerIdentityPatch.cs` (the identity goes in R5: a container's id is its shell's item id) |
| `ServerItemManager.cs`, `.Convergence.cs`, `.Interest.cs`, `.WorldIndex.cs`, `.PlayerItems.cs`, `.FallRescue.cs` (one partial class) | `Projection/ServerItemManager*.cs`. Split in R3: `.PlayerItems` (recall, a leaver's items) → `Inventory/PlayerItems.cs`; in R7: `.FallRescue` → `Physics/FallRescueServer.cs` |
| `WorldItemCellCoord.cs` | `Projection/` |
| `ServerItemSpatial.cs` | `Physics/` |
| `NetworkedTrainItemWakeManager.cs` | `TrainWake/TrainItemWakeServer.cs` |
| `ItemAccessServer.cs` | `Access/` |
| `ItemChangeLog.cs` | `ChangeCauses/` |
| `AuthoredWorldItemReplenishmentManager.cs` | `WorldItems/OfficeStockReplenishment.cs` |
| `InventoryMembershipPatch.cs` | `Inventory/` |
| `RespawnOnDropRangePatch.cs` | `VanillaStorage/` |
| `GrabberRecoveryPatch.cs` | `Behaviours/` |

Outside `Items/`, unchanged in place: `Server/Session/NetworkServer.Items.cs` (a partial of
`NetworkServer`), `Server/Career/{NetworkedSaveGameManager, PerPlayerSaveFormat, InstallGuidRekey}.cs`
(career code that calls the item subsystems; `PerPlayerSaveFormat`'s old-career item conversion goes
in R4, G3), `Server/Chat/AdminItemGrants.cs`.

**`Multiplayer/Client/Items/`:**

| Today | Becomes |
| --- | --- |
| `NetworkedItemClient.cs` | `LiveState/` |
| `ClientItemManager.cs`, `.Compatibility.cs`, `.Projection.cs`, `.WorldIntegration.cs`, `.FallRescue.cs` | `Projection/ClientItemManager*.cs`. Split in R7: `.FallRescue` → `Physics/`; `.WorldIntegration` → `WorldItems/` |
| `ClientItemSpatial.cs` | `Physics/` |
| `ColdContainerUiIntegration.cs`, `ColdContainerUiPatch.cs` | `Containers/ContainerUi.cs`, `Containers/ContainerUiPatch.cs` |
| `InventoryOwnershipUiPatch.cs` | `Inventory/` |
| `LostAndFoundExchange.cs`, `LostAndFoundList.cs` | `LostAndFound/` |
| `OwnedItemsList.cs` | `OwnedList/` |
| `ClientItemChangeLog.cs` | `ChangeCauses/` |
| `MoneyPatch.cs` | `Behaviours/` |
| `RemoteControllerModulePatch.cs` | `Client/Trains/` (it isn't item code) |

**`Multiplayer/Shared/Items/`:**

| Today | Becomes |
| --- | --- |
| `NetworkedItem.cs`, `.Snapshots.cs`, `.StateApplication.cs`, `.TrainParent.cs`, `.TrainWake.cs`, `.VrHand.cs` | `LiveState/NetworkedItem*.cs` (one partial class) |
| `NetworkedItemManager.cs`, `.FallRescue.cs`, `.TerrainGuard.cs`, `.WorldIntegration.cs`, `.ProjectionAndCompatibility.cs` | `LiveState/NetworkedItemManager*.cs`. Split in R7: fall rescue and the terrain guard → `Physics/`, world integration → `WorldItems/` |
| `ItemBasePatch.cs`, `IItemClient.cs`, `IItemServer.cs`, `IItemManagerClientPart.cs`, `IItemManagerServerPart.cs`, `INetworkedItemClientPart.cs`, `INetworkedItemServerPart.cs` | `LiveState/` |
| `InventoryIntegration.cs`, `InventoryItemStatePatch.cs` | `Inventory/` |
| `StorageControllerPatch.cs`, `StorageIntegration.cs`, `RespawnOnDropPatch.cs` | `VanillaStorage/` |
| `AuthoredWorldItemCatalogue.cs`, `WorldItemStableIdentity.cs`, `WorldItemStaticParentRegistry.cs` | `WorldItems/` |
| `NetworkedItemSpatialManager.cs`, `GrabHandlerItemPatch.cs` | `Physics/` |
| `ItemChangeCauses.cs` | `ChangeCauses/` |
| `FlashlightPatch.cs`, `LanternPatch.cs`, `LighterPatch.cs`, `ShovelPatch.cs`, `NetworkedPageBookState.cs`, `PageBookPatch.cs`, `ItemInteractionPatch.cs` | `Behaviours/` |

**`Multiplayer/Shared/Protocol/Data/Items/`:** stays. `CustomizationHoleData` (a trainset's drilled
holes) moves to `Shared/Protocol/Data/Trains/`; `ItemPositionData` (job paperwork only) to
`Data/Jobs/`; `TrackedValue` (runtime, not wire data) to `Shared/Items/Behaviours/`. R2 replaces
`ItemUpdateData` and adds the new data types.

**`Multiplayer.Protocol.Tests`:** `ItemPacketFixtureTests.cs`, `LostAndFoundPacketTests.cs`,
`OwnedItemsSaveCodecTests.cs` move to `Items/` (namespace `Multiplayer.Protocol.Tests.Items`), the
codec test renamed `LedgerSaveCodecTests`. `SaveFormatConversionTests` loses its item cases in R4
(G3).

**File sizes** stay under the ~1,500-line limit: the largest item files are
`AuthoritativeItemRegistry.cs` (1,209), `NetworkedLostAndFoundManager.cs` (1,038),
`NetworkedItem.StateApplication.cs` (944) and `ColdContainerGraph.cs` (850). R1 and R3 shrink the
first two (owners, claims and the repair leave; the save parts move to `SaveLoad`); a behaviour PR
that would push a file past the limit splits it.

### DevTools

The rename PR updates DevTools in the same commit. What a build catches: the scenario libraries and
scenarios (`DevTools/DvmpScenarios`, compiled against `Multiplayer.dll`, `using` lines and `nameof`).
What it doesn't, because it is compiled only when it runs or is a string:

| Where | What names the old code |
| --- | --- |
| `DevTools/Dvmp/Tracer.cs:10`, `Session.cs:395`, `McpTools.cs:198` | the `using Multiplayer.Server.Items; using Multiplayer.Shared.Items; …` string for probes and `dvmp_eval` |
| `DevTools/Dvmp/Probes/trace-install.cs`, `snapshot.cs`, `perf.cs`, `soak.cs`, `lib/Hands.cs` | `NetworkedItem`, `ItemState`, `AuthoritativeItemRegistry.TryGet` (`snapshot.cs:90`) |
| `DevTools/Dvmp/Desync.cs:146-165` | `ItemPlacementKind` and `ItemState` member names as strings |
| `DevTools/DvmpScenarios/Library/OwnedItems.cs:140` | `GetType("Multiplayer.Server.Items.PersistentWorldItemIdentity")` |
| `DevTools/DvmpScenarios/Library/ItemSteps.cs:255`, with `Scenarios/Containers.cs:51` and `Scenarios/LostAndFound.cs:56` | packet types by name (they change again in R2) |
| `DevTools/Scenarios/gate.sh:171` | `Multiplayer.Shared.Items.NetworkedItem` in an eval string |
| `DevTools/README.md:241`, the `docs/` paths (22 docs, 10 code comments name an `Items/` path) | paths and usings |

R0 proves the probes by running each once (`dvmp eval --session nogame -f <probe>` compiles one
without a game), and `dvmp_trace` on one item. The tracer's assembly filter
(`trace-install.cs:94`) needs no change while item code stays in `Multiplayer.dll`.

The scenario libraries follow the subsystems: `Library/OwnedItems.cs` → `Library/Ledger.cs`
(`Ledger.Entries`, `Ledger.Dump()`, OI-LED-13), `Library/Ownership.cs` reads owners from the ledger
after R1, and a new `Library/Invariant.cs` runs the check after every scenario (R3). Scenario names
(what `ONLY=` takes) don't change, so the gate lists and `testing.md` keep working.

The debug overlay's item, Lost and Found and container panels (`DebugOverlayController.cs:410-550,
1116`), `EntityDebugRegistry.ItemState` (`:288-350`) and `DebugPacketProjectors` (`:114-190`) move
with the rename (compile-checked) and gain, in R3, the item's ledger home beside its live placement,
and invariant violations as a filter.

## Order of work

All of it lands on **`items/refactor`** (D8), an integration branch off `beta`, protected like
`split/client-server` was. Each PR merges into it when its scenarios pass on both host kinds; the
branch merges into `beta` once, when every in-scope checklist row is ticked. Players see one career
break (D1), not one per PR, and `beta` stays playable meanwhile. `beta` is merged into
`items/refactor` as it moves, through the ccd sync, not by rebasing.

Each behaviour PR names the checklist rows it closes in its description and ticks them in the same
PR, with the code and the scenario (or Core test) that proves each on a player host and a dedicated
host. A row a PR can't close stays unticked, and anything a PR cuts becomes a new row (asked first).
The full mapping of every row, with the done rows each PR proves again, is in
[the checklist](../item-overhaul-checklist.md#the-refactor).

**The item regression set**, run in full after R4 on a player host (OI-REF-02) and at the end on both host kinds, before the beta merge (OI-REF-01). Each PR runs only its own new or changed scenarios, once, on a player host (user, 2026-10-24):
`rejoin`, `recall`, `containers`, `lost-and-found`, `item-id-roundtrip`, `ledger-save`,
`fixture-no-stash`, `absent-owner`, `offline-lost-and-found`, `owner-offline`, `car-delete-owner`,
`lost-and-found-many-owners`, `offline-sweep-mid-save`, `rejoin-mid-sweep`, `deposit-refused`,
`starting-items`, `owned-list`, `owned-retrieve`, `item-lineage`, `borrowed-disconnect`,
`inventory-persist`, `host-player-persist` (p), `save-reload`, `remote-held-item`, `owned-items`,
`item-release` (its modes), `host-throw-settles` (p), `server-throw`, `train-items`,
`client-fall-rescue`, `full-inventory-essential`, `lost-essential-rejoin`; the item code other
workstreams lean on: `gadget-save`, `gadget-join`, `shop`, `shop-restock`, `booklet-summon`,
`overview-pickup`, `jobs-roundtrip`; and the known bugs, which are expected to fail until their PR:
`item-bug-recall-lease`, `item-bug-car-delete-unowned`, `item-bug-essential-slot-rejoin`, and
`item-bug-container-save-refused` (F18, from item review's stood-down branch, below).

**Item review's F18 branch.** `items/container-save-refused` (ea596377, no PR) keeps a cold-container
save the server can't import and writes it back unchanged, refuses every container operation with
`container-save-unreadable`, and adds the scenario `item-bug-container-save-refused` (passing p+d,
still marked `[KnownBug("F18")]`). The refactor doesn't merge its fix: R4 replaces the container
save with rows that stand alone (C1), so the whole-graph failure can't happen. R0 brings its
scenario onto `items/refactor` (renamed with everything else) as a known bug, and R4 makes it a
required pass against the new format, with a bad row standing in for a bad graph. Its own "not
done" items are rows already: the depth check at deposit (OI-CON-15, R5) and quarantining a bad
record (OI-CON-04, R4).

| PR | What | Closes (checklist) | New and changed scenarios |
| --- | --- | --- | --- |
| **R0** Rename | The [rename map](#rename-map), DevTools and the paths in docs that stay, in two commits: moves and namespace lines, then type renames; then `docs/design/items/README.md` and `item-sync-review.md` to legacy. No behaviour change, no packet moved | none (OI-LAY-01, the layout, ticks when R8's docs match) | none. The regression set and the known bugs give the same results before and after, on both host kinds, and every probe and `dvmp_trace` runs |
| **R1** Identity only | Owners, holders and claims by identity in Core, the server and the client; the state machine, `ItemAccess`, Lost and Found, containers, recall and the logout rules decide by identity. Unbind/rebind and the absent number go. On the wire an owner number is still sent, worked out at the edge, until R2 | OI-LED-06, -07, OI-ESS-05, OI-ACC-08, OI-LF-06, OI-JOIN-09, OI-ID-08, OI-CON-08; re-proves OI-JOIN-03 (rewritten: an offline owner is an identity with no session), OI-LF-05 | `absent-owner` and `owner-offline` check identities, not 255; a new `rekey` scenario (an install GUID re-keyed to a Steam id keeps its list, retrieval and essentials) |
| **R2** The wire | [The wire](#the-wire): one update built per recipient with the relation and the owner-only claim, one request and one result with a remembered `RequestId`, one list, one handle space, one-step recall, the dead packets and fields gone; red for every item someone else owns (D3). The fingerprint moves | OI-ID-07, OI-ESS-12, OI-ESS-10, OI-CON-07, OI-SYNC-19, OI-SYNC-20, OI-LIST-17, OI-LIST-22 | a protocol test that no item packet carries a `Guid`; fixture tests for every new packet; `recall` (one step, both `TAKEN` modes); a `red` scenario (an absent owner's mug, the client's own mug, a found mug); `item-request-repeat` (a repeated withdrawal does nothing twice) |
| **R3** The ledger decides | [The ledger](#the-ledger) and [live state](#live-state): every owned item's change is a ledger change first; exact homes, a slot for every inventory item, the slot move as a request; claims assigned by the ledger and the runtime repair gone; the invariant check in Core, at runtime and after every scenario; `ServerItemManager.PlayerItems` becomes `Inventory/PlayerItems`; cold and live homes (D12): a leaver's inventory goes cold, and every inventory change is a request the ledger commits first | OI-LED-04, -05, -08, -09, -10, -13, -15, OI-ID-05, -06, -18, OI-ESS-01, -02, -03, -04, -08, -09, OI-START-04, -06, -08, -09, OI-ACC-05, -07, OI-LF-08, -20, OI-GAD-04, OI-SLOT-01, -03, OI-LIST-12, -20, -21, OI-LIN-07, OI-ID-13; re-proves the `[x]` rows of identity, essentials, the list and retrieval, lineage and access; OI-COLD-01, -02, -04, -05, -06; OI-ID-19; OI-SYNC-21 | `Library/Invariant.cs` after every run; a new `inventory-slots` scenario (a client and the host's player put items in chosen slots, move two, rejoin: same slots); `item-bug-essential-slot-rejoin` becomes a required pass; Core `ItemLedgerTests` and `HomeInvariantTests` (each rule, a fuzz run) |
| **R4** Saving and loading | [Saving and loading](#saving-and-loading): `Multiplayer.Items`, one state per item, rows that stand alone, the load before any request, old saves (D1), the vanilla mirror (D2), `Unloadable` (D9), a train-interior item waiting for its car, the rejoin from the ledger, `ServerPlayer.Inventory` gone, `dvmpPersistentOwner` gone, an installed gadget whose car is gone; a joining owner's cold inventory made in its exact slots (D12) | OI-PER-01, -03, -04, -06, -07, -09, -10, -12, -13, -14, -15, -16, -17, -18, -19, -20, -21, -22, -23, OI-SLOT-02, OI-JOIN-01, -02, -05, -07, -08, OI-LED-16, OI-LF-16, -21, -22, OI-CON-04, -05, OI-START-01, OI-GAD-01, -02, OI-ID-02, -03, -04; OI-COLD-03 | `ledger-save` and `item-id-roundtrip` against the new key; `item-bug-container-save-refused` a required pass; `inventory-slots RESTART=1`; a new `old-save` scenario (a save with only `WorldItemsV1` loads, starts items fresh, logs once); `unloadable-item` (a row whose prefab is missing survives two saves; a corrupt state comes back fresh in Lost and Found); `save-reload CYCLES=3` on a dedicated host gains nothing (OI-PER-17) |
| **R5** Containers on the ledger | A container's contents are items whose home is `Container(id, slot)`; the container's id is its shell's item id; depth checked at deposit; no request before the load; retirement and a failed withdrawal as ledger changes that roll back; the withdrawal takes the server's slot; records go with their container; deposit and withdrawal updates by interest | OI-CON-02, -03, -06, -09, -11, -12, -13, -15, OI-ACC-04, OI-LIST-18; re-proves OI-CON-01, OI-LIST-10, OI-LF-07, OI-ESS-07 | `containers` with a nested box across a restart; `container-depth` (a ninth level is refused at deposit); `container-early` (a deposit before the load is refused, not wiped); `withdraw-full` (a withdrawal into a full inventory lands in a slot or the owner's Lost and Found) |
| **R6** Every vanilla path | [Every vanilla path](#every-vanilla-path) through the ledger: car deletion (both paths), a leaver's hand item, job papers, attached and snapped items, every route into a full inventory, vanilla's inventory hazards, consumables, money, the dev console | OI-LF-02, -04, -10, -11, -17, OI-ID-12, OI-JOIN-06, -10, -11, OI-START-07, -10, OI-ESS-13, OI-LIST-06, -14, OI-LIN-05, OI-VAN-01 to -06; re-proves OI-LF-01, -03, -09, -12, -13, -14, OI-JOIN-04; OI-COLD-07 | `item-bug-car-delete-unowned` becomes a required pass; `leave-holding` (own, unowned and borrowed hand items at a quit and a crash); `attached-offline` (an absent owner's EOT lantern and hanger go to Lost and Found, detached); `booklet-not-owned`; `full-inventory-routes` (a Lost and Found retrieval, a withdrawal and a recall into a full inventory); `money-deposit`; `bin-owned`; `use-up` (a spray can and a garage key); `duplicate-essentials-load` (two players' radios in Lost and Found across a restart) |
| **R7** Leases, physics and projection | Every change that ends a lease ends it explicitly; a refused settlement ends its lease; a lease's item isn't saved unless the rules say so; a settled train item stays kinematic on a late joiner; a dedicated host takes a lease; a shelf item stays in interest; a lost projection Create is retried; the 7 real-gap rows to the old rule (D6); the fall-rescue and world-integration partials become classes | OI-ESS-11, OI-PHY-01, -02, -03, -04, -05, -06, -07, -08, -11, -12, OI-PER-11, OI-SYNC-01, -04, -07, -08, -09, -10, -11, -13, -17, -18, OI-LIST-09, OI-GAD-06 | `item-bug-recall-lease` becomes a required pass; `shelf-item` (put on a shelf, seen by a late joiner, retrieved, survives a restart); `thrower-leaves` (dedicated: a thrown item settles after its thrower quits); `late-join-train-item`; `fall-rescue-interior`; `pickup-range` |
| **R8** Docs | `items/behaviours.md`, a check of the whole new doc set against the code, this plan marked built ([Docs](#docs)); proof for the 11 rows rewritten to the code's rule (D6) | OI-LED-14, OI-LF-18, -19, OI-CON-10, OI-SYNC-02, -03, -05, -06, OI-PHY-09, -10, OI-LAY-01 | the scenarios the rewritten rows need (named in each row) |

Then `items/refactor` merges into `beta`, after a full gate run on both host kinds and a playtest.

**What R3 and R4 depend on.** R1 before R2 (the wire drops numbers the server no longer has), R2
before R3 (a slot move needs a request), R3 before R4 (the save writes the ledger's homes), R4
before R5 (containers' save is rows). R6 and R7 need R3, and may run beside R4 and R5 on their own
branches off `items/refactor`, since they touch other files; R8 is last.

## Docs

**Every current item doc is legacy** (the user, 2026-10-23, L1): `owned-items.md`,
`item-lifecycle.md`, `item-persistence.md`, `lost-and-found.md`, `container-storage.md`,
`item-world-sync.md`, `item-spatial-physics.md`, `train-item-wake.md`, `item-change-causes.md`,
`item-ownership-open-questions.md` and `item-sync-review.md`. They describe what exists today, and
the refactor reads them only as evidence of current behaviour, never as a template or as settled
design. Nothing is carried over from them by editing: the new docs are written from scratch for the
new model.

**Still valid, not legacy:** the vanilla research (`docs/research/DERAIL_VALLEY_ITEM_*` and the
other `DERAIL_VALLEY_*` docs: they are about the game, not our code), the known-loss catalogue
(`docs/research/item-bug-review.md`), the checklist, and this plan.

**The new doc set** mirrors the code layout: `docs/design/items/`, one short overview and one doc
per subsystem. Each is written by the PR that builds its subsystem, as that PR's as-built account,
and says only what the code does then.

| New doc | What it holds | Written in | Replaces (in `docs/legacy/items/`) |
| --- | --- | --- | --- |
| `items/README.md` | The overview: the guarantee, the three names, the homes, the subsystem table with each doc and code folder, the rule for conflicts. Short | R0 (the layout and names; the model's parts say "planned, R*n*" until built) | — |
| `items/wire.md` | Naming items and players on the wire, the update, the request and result, the list, the refusal codes | R2 | — |
| `items/ledger.md` | The entry, homes, cold and live homes (D12: what is cold, going cold, going live, no duplicate on the server), owners (`OwnerRef`), births and ends, ledger changes, the invariant check | R3 | `owned-items.md`, `item-ownership-open-questions.md` (the questions still open move to the ledger doc's "Open" section, re-asked, not copied) |
| `items/live-state.md` | The live item, the state machine, revisions, what follows the ledger | R3 | `item-lifecycle.md` |
| `items/inventory.md` | Slots for every item, the belt, slot moves, every inventory as server data with an optimistic client (the move shown at once, sent as a request, committed or rolled back), a leaver's inventory going cold and made in its exact slots at join, how a client shows claims and red | R3 | — |
| `items/essentials.md` | Starting items, the issuer, claims, recall | R3 | — |
| `items/owned-list.md` | The owned-items page and retrieval | R3 | — |
| `items/access.md` | `ItemAccess` and fixtures (the fixture rules marked paused with shops) | R3 | — |
| `items/change-causes.md` | Why an item moved or went away, on the server and clients | R3 | `item-change-causes.md` |
| `items/save-load.md` | `Multiplayer.Items`, loading, cold items left as data at load (OI-COLD-03), old saves, the vanilla mirror, `Unloadable` | R4 | `item-persistence.md` |
| `items/containers.md` | Containers as homes, deposit, withdrawal, depth, the UI | R5 | `container-storage.md` |
| `items/lost-and-found.md` | Collection, entries, retrieval, every route into Lost and Found | R6 | `lost-and-found.md` |
| `items/vanilla-storage.md` | Every vanilla path, blocked or routed, and the guards | R6 | — |
| `items/projection.md` | Interest, what each client is shown, convergence | R7 | `item-world-sync.md` (its projection half) |
| `items/world-items.md` | Scene-authored items, the catalogue, shelves, office stock | R7 | `item-world-sync.md` (with `projection.md`) |
| `items/physics.md` | Leases, samples, settlement, throws, fall rescue, the terrain guard | R7 | `item-spatial-physics.md` |
| `items/train-wake.md` | Items in moving trains | R7 | `train-item-wake.md` |
| `items/behaviours.md` | Each item type's own sync | R8 | — |

**The legacy docs are in `docs/legacy/items/`** (the user, 2026-10-24: moved before their
replacements, so nobody takes them for the current design). Each has a banner at the top: "Legacy:
describes the item system before the refactor (names as on `beta` 04bfe277) ... Replaced by
`items/…` (R*n*)." `item-sync-review.md` (historical, no replacement) moved in R0. The PR that writes
a replacement turns its banner's name into a link to it and lists the new doc in `docs/README.md`
under "How each system syncs". The checklist's row sources point at the legacy paths.

**R0 doesn't edit the legacy docs:** they keep today's class and file names, which the
[rename map](#rename-map) translates. R0 updates paths only in docs that stay (`architecture.md`,
`testing.md`, `development.md`, `observability.md`, `DevTools/README.md`).

**R8** writes `behaviours.md`, checks every new doc against the code (each fact in one place, no
"planned" left), turns this plan's status line to "built" with links to the new docs, and ticks
OI-LAY-01. `docs/README.md`, `architecture.md`, `testing.md` (every new scenario) and
`observability.md` (the invariant event, the overlay) follow in the PR that changes what they
describe.

## Rows outside this refactor

Marked `paused: <workstream>` in the checklist (D4), kept and unticked, for when that work resumes:

- **Shops:** OI-SHOP-02 to -10; OI-FIX-02 to -06 (fixtures are shops 2c); OI-ACC-09 (the
  permissions system). OI-SHOP-01 stays done.
- **Gadgets:** OI-GAD-03, -05, -07, -08, -09, -11; OI-LIST-13, -15, -16; OI-LIN-02, -03, -04; OI-ACC-06; OI-GAD-12.
  The seam rows OI-GAD-04, -06, -10 and OI-LF-22 are in (R3, R7, R3, R4).
- **Companies:** OI-ID-14, -15. The ledger's owner is an `OwnerRef` with room for a company; nothing
  else.
- **Physics and wake fidelity, no item lost:** OI-PHY-13, -14, -15; OI-SYNC-14, -15, -16; OI-CON-14.
  OI-PHY-05 and -12 are in (D6), and so are OI-PHY-09 and -10 (rewritten to the code's rule) and
  OI-GAD-06: the user's D4 didn't pause them.
- **The list's wording:** OI-LIST-04, -05, -07. The list's data moves to the new ledger; its words
  don't change.
- **Mod items:** OI-MOD-01 to -03 (D11); OI-MOD-04 also waits for shops, OI-MOD-05 for gadgets.
- **Decisions not about loss:** OI-ID-16 (gifts), OI-ID-17 (made items), OI-SYNC-12 (office stock in
  a cab, unowned).

Marked `not needed: fresh items` (G3, D1): OI-MIG-01 to -07, OI-PER-02 and OI-ESS-16; OI-LED-11
(`Missing`) is `not needed: replaced by OI-LED-16` (D9). The done rows whose rule goes with the old
stores (OI-LED-02, OI-PER-05, OI-PER-08, the `Migrated` birth in OI-LIN-07) are rewritten in R4.

## Risks

- **A big rename hides real changes.** R0 is moves and renames only, in two commits, reviewed with
  rename detection on, and proved by identical scenario results before and after.
- **Paused drafts rot.** The gadget drafts (#313–#320) and `items/container-save-refused` will
  conflict with R0. The rename map is their guide; they rebase when their work resumes, not before.
- **Strings break silently.** The [DevTools](#devtools) list names every string and runtime-compiled
  reference; R0 runs each probe once.
- **One career break.** D1 drops everyone's items once, when `items/refactor` reaches `beta`. The
  release notes say so, and a career's money, licences, trains and jobs survive.
- **The ledger first changes timing.** A request now commits the ledger before live state and
  before the clients hear of it, where today the record commits first. R3 keeps one code path
  for the order (`LedgerChange` → live state → updates) and the regression set runs with `LAG=250`
  for the request-heavy scenarios (`recall`, `containers`, `owned-retrieve`).
- **Per-recipient updates cost more.** R2 builds an update per recipient where today one is
  broadcast; `dvmp_perf` before and after on `save-reload` and a soak, and the bulk send keeps
  batching per recipient.
