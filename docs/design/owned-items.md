# Owned items

> Status: as built, checked against the code on 2026-10-23 (`beta`, with #308–#312 merged). PRs 1a
> to 7 are merged and tested in game on a player host and a dedicated host; see
> [Built in](#built-in). What isn't built is listed under [Not built yet](#not-built-yet). Known
> ways items are lost are catalogued in [item-bug-review.md](../research/item-bug-review.md). The
> rest of an item's life (placement, projection, motion, persistence) is in
> [`item-lifecycle.md`](item-lifecycle.md).

Every item a player owns has one id for its whole life, and the server keeps a ledger of those items:
whose each one is, where it is now, and how its life began and ended. The owned-items list,
retrieval, starting items and the load check are built on the ledger.

The ledger is a follower today. The server's item record (`AuthoritativeItemRegistry.Record`) is
still where an item's owner and placement are written. The ledger is updated from the record each
time the server commits a change, and it is checked against the stores at load. Where the two
disagree, the server logs it (see [The ledger and the records](#the-ledger-and-the-records)).

## Decisions

Decided with the user on 2026-10-21 unless dated otherwise. Each says whether it is built.

- **One id for life.** Every item a player owns, except scene-authored world items, has one random
  GUID in the save and the server's records for its whole life. The wire keeps the per-session
  `ushort` NetId, so the id adds nothing to item packets. A duplicate id is caught when an item
  registers: the first keeps it, the copy is re-minted, and it is logged as an error. The server
  mints ids, not players: SteamID plus a counter was set aside. Built.
- **Retrieval sends the item to your Lost and Found.** You collect it at a shed. Nothing jumps into
  your hands from across the map. Built.
- **You can retrieve your item from anyone,** even from another player's hand, mid-use. Taking it
  back from a friend at the worst moment is "half the fun" (the user), not abuse to design out.
  Built.
- **Installed gadgets can be retrieved any time, from any car.** Players on that train see it come
  off. Not built (PR 8, #313, waits on the gadget work).
- **A borrower leaving sends your item to your Lost and Found,** online or not. Built.
- **Offline counts as far away.** An offline owner's items that aren't with a player, in a
  container, installed or in Lost and Found go to their Lost and Found under the standard rules.
  That includes items in train cars and on shelves, which an online owner's distance never
  collects. Built, except attached and snapped items.
- **Nothing expires.** An absent player's items wait however long they're away. Built.
- **Found items aren't yours.** A non-personal item you pick up and keep (a mug, an office bin, a
  spray can) stays unowned. It is saved while you carry it, and isn't listed, retrievable or sent to
  Lost and Found. Built.
- **Fixtures stay where they belong.** A station fixture (a shop's scanner, and other station
  equipment) belongs to the world, never to a player. Partly built: see [Fixtures](#fixtures).
- **The list shows only what still exists.** A replacement (the empty tape roll) is yours and
  listed. The spent item leaves the list, and the ledger records what replaced what. Built; the
  gadget swaps that make replacements aren't wired yet.
- **A destroyed essential is replaced at your next join,** only when its life ended, never because
  it's somewhere else. Built.
- **Job papers belong to the job.** Booklets, overviews and reports aren't owned items. Lost and
  Found still collects a lost booklet for the player it was issued to. Built.
- **Red means its owner can take it back.** Any item another player owns shows red to whoever holds
  it, online or not. Not built: red marks only another player's recallable essential (see
  [Red](#red)).
- **Essentials stay out of containers** (2026-10-22, item-review L7). Depositing an essential is
  refused (`essential-not-in-containers`), so its claim slot and recall always work. Built.
- **Keep the vanilla save as the server's store** (2026-10-19,
  [server-player-split.md](server-player-split.md#save-format)). The ledger is one more key in the
  mod's `Multiplayer` section, and the stores keep their formats. Built.
- **Companies** (2026-10-20; the company career's design is still to be written): everyday items stay
  player-owned; gadgets, garage keys, loco paint, the wireless MU controller and the loco remote are
  company-owned. Not built: see [Companies](#companies).

## What an owned item is

An item with a persistent owner: a player's identity (the Steam-derived player ID, or the install
GUID without Steam; [server-player-split.md](server-player-split.md#identity)). The ledger records how
each came to be owned (`OwnedItemBirth`, saved by name):

| Birth | How | Where it happens |
| --- | --- | --- |
| `Purchased` | Bought at a shop by any player, the host's own included. The server spawns it on the counter, owned by whoever scanned the line; if they have left, it is delivered unowned. | `ShopDelivery.Spawn` |
| `StarterKit` | The starter kit, once per player per career. | `StartingItemsIssuer` |
| `Issued` | An essential the player has no live one of. | `StartingItemsIssuer` |
| `Granted` | An admin's `/give`. | `AdminItemGrants` |
| `Claimed` | An unowned personal (`BelongsToPlayer`) item first put in a hand or inventory, or given a retrieval claim: the first taker owns it. | `ItemAuthorityStateMachine.Apply` (`MayEstablishPersistentOwner`) |
| `Replaced` | Made by vanilla as it used up an owned item (the empty tape roll). Owned by the used item's owner. | `OwnedItemsServer.RecordReplacement` |
| `Migrated` | Found in a store at load with no ledger entry (a save from before the ledger, or one a build without it played). | `OwnedItemReconciler` |
| `Duplicate` | A second item carrying an id the ledger already had. It gets a new id; `born.from` is the original. | `OwnedItemLedger.Register` |
| `Made` | Reserved for an item a player makes with a tool (a label). Nothing uses it yet. | |

A ledger entry is born at the first committed move of an owned item it doesn't have yet
(`OwnedItemsServer.OnMove`). The birth comes from that move's cause (`BirthFrom`): `Purchased`,
`Granted`, `Issued` and `StarterKit` map across, a restore, withdrawal or retrieval reads as
`Migrated`, and anything else as `Claimed`.

Not owned: scene-authored world items; non-personal items, even when carried; job booklets,
overviews and reports; shop receipts; unowned dynamic items such as stock forks and the paint
station's spray can. Licence papers aren't made at all in multiplayer (`PhysicalLicensePaperPatch`
returns null from every `CreateLicense` while a session runs), so a licence is an entitlement only.

## One id for life

**Minting.** An item's GUID is minted once, when the server first registers it:
`AuthoritativeItemRegistry.Ensure` through `PersistentWorldItemIdentity.Ensure()`. A scene-authored
item has none: it is keyed by its authored key until it is taken, when it becomes a dynamic item
with an id of its own ([item-world-sync.md](item-world-sync.md)). A dynamic item has its id before it
has an owner, and its ledger entry uses that id. Nothing else mints one:

- a Lost and Found entry's id is its item's id;
- a cold-container record's id is its item's id, and withdrawal registers the item under it
  ([container-storage.md](container-storage.md)). The container shell's own id
  (`ColdContainerIdentity`) is a separate thing;
- save conversion derives a stable id for a converted inventory item
  (`PerPlayerSaveFormat`), so converting twice gives the same ids.

**Where the id travels.** The id is in vanilla's own item save data (`dvmpPersistentWorldItemId`,
`PersistentWorldItemIdentityPatch`), so every store that keeps an item's save data keeps its id. An
item with no `ItemSaveData` component (a mug) has no saved state in vanilla's storage, so it comes
back from vanilla's Lost and Found with no id. The mod's own record carries it then: at load the
item takes the id back from its Lost and Found entry (`NetworkedLostAndFoundManager.UseItemIds`), and
a cold record's key is the item's id.

| Placement | Who holds the id |
| --- | --- |
| Hand, inventory, belt (any player's) | the server's record; at save, `WorldItemsV1`, keyed by the holder's identity |
| World, train interior, static parent, attached, snapped | the server's record; at save, `WorldItemsV1` |
| Cold container | the cold record, keyed by the item's id; the item's save data inside it |
| Lost and Found | the Lost and Found entry, keyed by the item's id; vanilla's Lost and Found storage holds the inert item with its save data |
| Installed gadget | the server's record (`Installed` on its car, [gadgets.md](gadgets.md#the-installed-gadget-and-its-item)); vanilla's `Storage_InstalledGadgets` holds its save data and `dvmpPersistentOwner` |
| Client projections | nothing: clients know NetIds only |

**Duplicates.** Random GUIDs don't collide in practice. The risk is one id being copied: a copied
save, a dupe bug, or an object cloned with `Instantiate`, which copies `PersistentWorldItemIdentity`.
Two checks catch it, and both log `item.duplicate-persistent-id`:

- `OwnedItemsServer.ClaimPersistentId`, from `Ensure`, cold withdrawal and Lost and Found's load: an
  item arriving with an id another live item has gets a new GUID. The first keeps its id.
- `OwnedItemLedger.Register`: an id the ledger already has, live or ended, is re-minted, born
  `Duplicate`, and the item is given the new id. An ended id is never reused.

## The ledger

`OwnedItemLedger` (`Multiplayer.Core/Items/OwnedItemLedger.cs`, unit-tested) holds one entry per
owned item, live or ended (`OwnedItemEntry`):

```text
OwnedItem
  id            the item's GUID (the key)
  owner         the owner's identity
  prefab        for the list and for issuing essentials
  claimSlot     an essential's retrieval claim slot in its owner's inventory, or -1
  where         see below
  born          reason, time, by (identity), from (id), detail
  ended         reason (ItemRemovalReason), time, replacedBy (id), detail; null while live
  shopStocked   the item still has vanilla's Restock flag
```

`where` (`OwnedItemWhere`) names the store that must hold the item, not its pose:

```text
With(holder identity, Hand | Inventory | Belt, slot)
World(anchor: none | "car:<car GUID>" | "static:<key>")   loose, in a train interior, on a shelf
Attached("car:<car GUID>")                                   an EOT lantern
Snapped(snap parent)                                         a hanger, a mount
Container(container id)
LostAndFound
Installed("car:<car GUID>", gadget UID)
Missing                                                      no store held it at load
```

The ledger keeps owners and holders as identities, never per-session player numbers.

**Operations.** `Register` (with the duplicate check), `Move`, `End`, `Replace` (end the used item
and register its replacement, owned by the same owner), `Revive` (an ended entry whose item a store
holds after all), `SetShopStocked`, `SetClaimSlot`, `ResolveClaimClashes`, and the starter-kit
markers. Every change raises `Changed` with a copy of the entry. Ended entries are kept for lineage,
the newest 100 per owner (`EndedKeptPerOwner`), and older ones are pruned.

**Queries.** `LiveOwnedBy(owner)`, `LiveWith(holder)`, `OwnerOf(id)`, and
`CountLive(owner, prefab, shopStocked)` for issuing essentials and, later, per-player shop stock
([shops.md](shops.md#owned-items)).

### The ledger and the records

`OwnedItemsServer` (`Multiplayer/Server/Items/OwnedItemsServer.cs`) keeps the server's ledger. It
follows the records:

- `ItemChangeLog.RecordMove` calls `OnMove`: an owned item the ledger doesn't have is registered; one
  it has is moved to `WhereOf(record)`, and an essential's claim slot follows the record's. An owner
  that differs from the entry's is logged ("changed owner outside the ledger"), not applied.
- `ItemChangeLog.RecordRemoval` calls `OnRemoved`. A Lost and Found collection moves the entry to
  `LostAndFound`; a container deposit is moved by `OnDeposited` instead; leaving interest is a
  client's view and changes nothing. Any other removal ends the entry with its removal reason,
  except while the world is unloading or the game is quitting.
- A cold deposit calls `OnDeposited`, which moves the entry to `Container(id)`.
- Nothing moves the ledger before it has loaded (`OwnedItemsServer.Loaded`).

**What reads the ledger:** the owned-items list (`OwnedItemRows`), retrieval (`OwnedItemRetrieval`),
`StartingItemsIssuer`, the starter-kit markers (and `InstallGuidRekey`, which moves a re-keyed
player's entries and marker), and the scenarios. Everything else (Lost and Found, recall, claim slots,
containers, the disconnect rules, the first-taker rule) reads the record.

**Owners and holders are identities** (item-refactor.md, "Identity only"; R1). The record keeps
`Owner` and `Holder` as player identities, and so does everything that decides by them: the Core
state machine, `ItemAccess`, Lost and Found, recall, claim slots, containers and the disconnect
rules. A claim is always its owner's, so it has no player of its own. An offline owner is an
identity with no session: nothing is bound at login or unbound at logout. `PlayerNumbers` turns an
identity into a session number only at the wire and the session edge: when the server sends a
packet, applies a change or asks where a player is. Until R2 an update still carries an owner
number, worked out there: the owner's session number, `ItemUpdateData.OfflineOwnerNumber` (255,
which the player-number allocator reserves) while they're offline, 0 for no owner.

So with an offline owner: the first-taker rule (no owner) doesn't take the item over, nobody else's
recall matches it, a departing borrower's test counts it as someone else's, vanilla's own Lost and
Found path stays blocked, and a client sees it as someone else's. An item in an offline player's
inventory stays held by them, so nobody else takes it out.

**The audit.** `OwnedItemsServer.Audit()` lists every disagreement: a live owned record the ledger
doesn't have or has elsewhere or under another owner, an owned item in vanilla's Lost and Found list
whose record doesn't say Lost and Found, and anything in a dedicated host's own inventory (that
belongs to its parked body and stays empty, #261). An inventory slot change isn't a logged move, so
the audit ignores slots. Scenarios call it; nothing runs it on a timer.

Rules kept from the stage-3 test pass (2026-10-21):

- **Vanilla never moves an owned item.** Vanilla's `RespawnOnDrop` once sent personal items to the
  server's own vanilla Lost and Found while the record still said `World`, and clients were sent the
  item at (0,0,0) (#263). Only the mod's Lost and Found moves an owned item.
- **Inactive is not gone.** DV's train LOD switches off items lying on a parked car. Items are looked
  up by record, among inactive objects too, never by `FindObjectsOfType`.
- **Only an accepted transition moves the ledger.** A refused one leaves it as it leaves the record.

## Saving and loading

**In the vanilla save.** The ledger is `Multiplayer.OwnedItemsV1` (`OwnedItemsSaveCodec`), written in
the `Server_UpdateInternalData` pass after Lost and Found, cold containers and `WorldItemsV1`
(`NetworkedSaveGameManager`). Enums are saved by name, so appending values never shifts a saved one.

```json
"OwnedItemsV1": {
  "format": 1,
  "items": [
    { "id": "…", "owner": "…", "prefab": "ItemRadio", "claimSlot": 35,
      "where": { "kind": "With", "holder": "…", "hold": "Inventory", "slot": 35 },
      "born": { "reason": "Issued", "utc": 0, "by": "…" } },
    { "id": "…", "owner": "…", "prefab": "DuctTape",
      "where": { "kind": "World" },
      "born": { "reason": "Purchased", "utc": 0, "by": "…", "detail": "…" },
      "ended": { "reason": "DestroyedByGame", "utc": 0, "replacedBy": "…" } }
  ],
  "players": { "<identity>": { "starterKit": true } }
}
```

- A hand item is saved as `Inventory`: a save puts held items in the holder's inventory, so the load
  finds it there.
- `players.<identity>.starterKit` is the starter-kit marker.
- Before the ledger has loaded, a save leaves the saved ledger as it was. A format newer than the
  build reads as empty.
- It is additive: a build without the ledger drops `OwnedItemsV1` and reads the stores, and the next
  ledger build rebuilds it by migration.

**Loading** (`OwnedItemsServer.Load`). `HostTick` waits until `WorldItemsV1`, Lost and Found and the
cold containers have imported, before any player logs in. It restores the saved entries (an id saved
twice keeps its first), then `OwnedItemReconciler.Plan` checks every entry against what the stores
hold (`StoreHits`: the records, Lost and Found entries not yet bound, and the cold graph):

| Found | Step | Done |
| --- | --- | --- |
| In the store its `where` names | `Keep` | |
| In another store | `Correct`: the store has the state, so its place wins and the ledger follows, logged | yes |
| In several stores | the copy in the store `where` names is kept, else the first by priority: Lost and Found, container, installed, possession, world. Each other copy is a `DropCopy`. | logged only, not carried out |
| Nowhere | `MarkMissing`: kept as `Missing` and looked for at every load | yes |
| An ended entry's item, in a store | `Revive` where the store has it | yes |
| An owned item with no entry | `Register`, born `Migrated` | yes |
| Another player's item in a saved possession | `RecoverToLostAndFound`, to its owner's Lost and Found (`SaveRecovery`) | logged only, not carried out |

**Migration** is the same plan against an empty ledger, for a save with no `OwnedItemsV1`. It also
sets the starter-kit marker for every identity in `Players`, so nobody who has played gets a second
kit. Running it twice gives the same ledger, except that a copied id is re-minted once.

## Starting items

The server makes each player's starting items (`StartingItemsIssuer`,
`Multiplayer/Server/Items/OwnedItems/`). No game makes its own: DV's `StartingItemsSafeguard` and
`LicensesSafeguard` are off in every multiplayer game, player host and dedicated host included
(`Shared/Career/StartingItemsControllerPatch`), and DV's inventory starts empty.

When a player has loaded (`NetworkServer.CompleteLoading`, which the host's own player reaches too),
the issuer waits up to 60 s for the ledger's load. If it never loads, nothing is made, and the next
join tries again: better none than a second set. Then it makes:

- **each essential** for the career (`AdminItemGrants.StartingItemPrefabs`: comms radio, maps,
  compass, wallet, catalogue, ...) the player has no live item of, into the first free slot, with a
  `Reserved` claim, born `Issued`. "Has" counts a live entry anywhere: with another player, in Lost
  and Found, in a container. An ended or never-made one doesn't. If the ledger has none but a store
  does (`OwnedItemsSummary`), nothing is made and the disagreement is logged.
- **the starter kit** (shovel, lighter, oiler, lantern, flashlight, hanger, duct tape) when the
  player's starter-kit marker is unset, into free slots from 14, born `StarterKit`. The marker is set
  even if one item failed: a kit is given once.

Each item is made as a cold-container withdrawal makes one: at DV's safety position, its state
loaded a frame later, registered with its owner and slot (`RegisterMaterializedColdItem`), and sent
to everyone loaded. A second load while a player's items are still being made doesn't start
another run for them.

## Joining, rejoining and leaving

**Joining.** At login, owner and holder numbers are rebound from the identity (see
[Owner numbers](#the-ledger-and-the-records)). The player's saved possessions are `WorldItemsV1`
records held back until their holder connects, then put in their inventory, a hand item included
(`WorldItemPersistenceManager.OnOwnerConnected`, [item-world-sync.md](item-world-sync.md)). They are
the player's own items and unowned items they carried. When the owner reaches `ReadyForItems`, their
claim slots are checked for clashes (`EnsureDistinctClaimSlots`, "owner-loading"). Then the starting
items are issued.

**Leaving** (`ServerItemManager.DropHeldItemsOfDepartingPlayer`, at disconnect):

- **Another player's item** in their hand or inventory is dropped where they stand and collected at
  once into its owner's Lost and Found (`BorrowerLeft`), owner online or not.
- **Their own item, or an unowned one, in their hand** is dropped where they stand.
- **Their own and unowned items in their inventory** stay keyed to their identity and come back at
  their next join.
- **Their items elsewhere** fall under Lost and Found's rules for an offline owner
  ([lost-and-found.md](lost-and-found.md)).

## Essentials: claim slots and recall

An essential (`InventoryItemSpec.IsEssential`: comms radio, maps, wallet, ...) has a retrieval claim
on one slot of its owner's inventory: `InventoryClaimSlot` and `InventoryClaimFlags` on the record
(`Reserved`, `Locked`, `Dropped`, `Stolen`). While the item is elsewhere, the owner's client shows a
red silhouette in that slot, and clicking it recalls the item.

**Claims follow the owner's moves** (`ItemAuthorityStateMachine.Apply`). The owner placing it sets
the claim; the owner dropping it marks the claim `Dropped`; another player taking it marks it
`Dropped | Reserved | Stolen`; it coming back to the owner's inventory clears `Dropped` and `Stolen`.
Pickup never changes the owner.

**One slot per essential.** A DV slot holds one item, so two essentials claiming one slot left the
second with no silhouette and no recall. The state machine sees one item at a time, so the server
checks all of an owner's claims together (`AuthoritativeItemRegistry.EnsureDistinctClaimSlots`,
policy `EssentialClaimSlotPolicy`):

- after an accepted transition of an essential, a recall and a Lost and Found retrieval;
- when the owner reaches `ReadyForItems`, which repairs a clash a save left.

Of the claims on one slot, the item the owner just put there keeps it, then an item really in the
owner's inventory, then a claim that isn't a silhouette, then the lowest NetId. The others move to
the highest slot no claim holds: DV fills the backpack from the front. The move keeps the item's
revision, updates the Lost and Found entry's copy of the slot, and sends the owner a correction. With
every slot claimed the clash stays rather than drop a claim. The ledger keeps its own copy of the
slot (`SetClaimSlot`, `ResolveClaimClashes`), following the record's.

**Recall** is the fast path back for an essential. It is two-phase: the server prepares it
(`TryPrepareRecall`: the item must be an essential and the requester its owner by number), the
holder's game confirms (`ClientboundItemRecallPreparePacket`, `ServerboundItemRecallPreparedPacket`,
5 s timeout), and the server commits (`TryCommitPreparedRecall`, `ItemMoveReason.Recalled`). An
essential already in Lost and Found is retrieved from there instead. The host's own player recalls
in-process (`ServerItemManager.RequestHostRecall`).

### Red

`InventoryPresentationPlanner` shows an item red when its owner number isn't the local player's
**and** it is recallable (an essential with a retrieval claim). An offline owner's number on the wire
(`ItemUpdateData.OfflineOwnerNumber`, 255) is someone else's, so their essential shows red too. Any
other item someone else owns shows normally.

## The owned-items list

A Career Manager page, **Owned Items**, beside Lost Items in the Multiplayer hub
(`OwnedItemsCareerManagerScreen`). The data is the server's; the page is the player's own UI.

- The client asks (`ServerboundOwnedItemsRequestPacket`); the server answers with parallel rows
  (`ClientboundOwnedItemsPacket`): handle, prefab, display name, where, retrievable. The client keeps
  the last list (`OwnedItemsList`). The host's own player is answered in-process.
- The server builds a row per live entry the player owns (`OwnedItemRows`). Ended entries aren't
  listed. A handle is per session, never the GUID.
- Where it is is worded by `OwnedItemPlaceText` (Core, unit-tested): "in your inventory", "in
  Bentgear29's hand", "on the ground", "in L-042", "on a shelf", "attached to L-042", "fitted in
  place", "in Toolbox", "in Lost and Found", "installed on L-042", "missing". The car's id and the
  container's name come from the server's records.
- The labels are English, like the Lost Items page's.

## Retrieval

Retrieval moves an item into its owner's Lost and Found (`LostItemReason.RetrievedByOwner`) from the
owned-items list. The owner collects it at a shed. The owner asked, so nearby-player protection and
the grace period don't apply. `ServerboundOwnedItemRetrievePacket(handle)` asks;
`ClientboundOwnedItemRetrieveResultPacket` answers, then a fresh list. The rule by place is
`OwnedItemRetrievalRule` (Core); `OwnedItemRetrieval` (server) carries it out and decides each row's
retrievable flag.

| Where | What happens |
| --- | --- |
| With you, in Lost and Found, missing | nothing to do (`nothing-to-retrieve`) |
| On the ground, in a train interior, on a shelf | collected now |
| In a container | the outermost container that isn't with you goes, whole, as one entry: the walk goes up the cold graph to the live shell. One you carry means nothing to do. |
| With another player, hand included | dropped out of their hand or inventory, as a departing borrower's is, then collected. The holder gets a whisper ("Liam took back their Lantern"), and the change log says `TakenFromPlayer` by the owner. |
| Attached, snapped, installed | refused (`not-retrievable-yet`) |
| Mid-recall | refused (`busy-try-again`) |

Every retrieval also asks `ItemAccess` (`Retrieve`: the owner only), and goes through
`OwnedItemsServer.SendToLostAndFound`, the one way into an owner's Lost and Found by item id. That
refuses an item with a player (`item-is-with-a-player`) or with no owner, and does nothing for one
already in Lost and Found.

## Lineage

An entry records how it began (`born`: reason, time, by, `from`) and ended (`ended`: an
`ItemRemovalReason`, time, `replacedBy`, detail). A money deposit ends its entry `MoneyDeposited`
and an unexplained destroy `DestroyedByGame` ([item-change-causes.md](item-change-causes.md)).

A server-side swap that uses an item up calls `OwnedItemsServer.RecordReplacement(used, replacement,
reason)` before it destroys the used item: the replacement becomes the used item's owner's, born
`Replaced` from it, and the used item's entry ends with `replacedBy`. Nothing calls it yet outside
the `item-lineage` scenario: duct tape, spools and labels are wired by gadget PRs 4, 5 and 9
([gadgets.md](gadgets.md#items-that-gadgets-create-and-destroy)).

## Fixtures

A fixture is a scene-authored item that is part of a place: a shop's scanner, and other station
equipment like it. It is never owned and never leaves its place (decided 2026-10-21):

- hand only: no inventory, belt or container;
- carried out of its place's area, the server puts it back on its authored spot;
- when its carrier leaves, and in every save, it goes back to its spot;
- which items are fixtures is a server list, by authored key or prefab. It isn't the office stock
  (mugs, office bins), which a player may keep while carrying it.

Built: the list (`ItemAccessServer.Fixtures`, empty until shops PR 2c fills it,
[shops.md](shops.md)) and the refusal of a stash or a deposit for an item on it. Not built: the
return to its spot.

## Permissions

`ItemAccess.Decide` (`Multiplayer.Core/Items/ItemAccess.cs`, unit-tested) is the one decision point
for what a player may do with an item. `ItemAccessServer.Allows` works out the facts (does the actor
own it, by owner number; is it a fixture; is it an essential) and asks it. The coming permissions
system replaces the policy, not the call sites.

| Action | Default policy | Asked from |
| --- | --- | --- |
| `Stash` (inventory, VR belt) | anyone; a fixture never | `AuthoritativeItemRegistry` (a player's transition into their inventory) |
| `Deposit` | the item's owner; never a fixture or an essential | `NetworkedColdContainerManager` |
| `Retrieve` | the owner | `OwnedItemRetrieval` |
| `Recall` | the owner | not yet: recall checks the owner in the state machine |
| `Withdraw`, `TakeFromWorld` | anyone | not yet: withdrawal has its own owner check ([container-storage.md](container-storage.md)) |
| `TakeFromPlayer` | nobody (vanilla has no way to) | not yet |
| `RemoveGadget` | anyone | not yet (gadget PR 3a) |

## Companies

Decided in outline (2026-10-20), not built. An entry's owner
becomes a player or a company. Everyday items stay player-owned; gadgets, garage keys, loco paint, the
wireless MU controller and the loco remote are company-owned, even while a member carries them. A
company has its own Lost and Found. Retrieval and red follow membership.

## Not built yet

- Installed gadgets: retrieval (PR 8, #313) and the `Installed` place's gadget UID, which
  `WhereOf` leaves 0.
- Attached and snapped items: not swept by Lost and Found for an offline owner, and not retrievable
  (they need detaching first).
- A departing player's own hand item goes into their inventory: today it is dropped where they stand.
- The load check's `DropCopy` and `RecoverToLostAndFound` steps are logged, not carried out.
- Migration's one-off recoveries: an item lost to the old cold-withdrawal bug, and a `WorldItemsV1`
  record with an empty id.
- The ledger as the source of owners: the records still carry them, and the ledger follows.
- Red for every item someone else owns.
- Fixtures returning to their spot.
- The busy check for a container mid-withdrawal.
- `GadgetDestinationMissing` (a gadget whose car is gone at load) and `PurchasedDeliveryFallback`
  (a purchase delivered far away) aren't routed.
- `Made`, the per-player shop stock, companies, and giving an item away
  ([open questions](item-ownership-open-questions.md)).

## Built in

The code cites these PR labels. Each merged PR was tested in game on both host kinds with its
scenarios (1a and 1c to 5 on 2026-10-21/22; 6a, 6b, L7 and 7 together on 2026-10-23).

| PR | What |
| --- | --- |
| 1a (#278) | The ledger and its reconciler in Core |
| 1b (#282) | The ledger on the server, in shadow; one id in every store; the audit |
| 1c (#296) | `ItemAccess`, wired for stash and deposit |
| 2a (#304) | The absent owner number; Lost and Found binds every entry at load |
| 3 (#305) | Lost and Found for an offline owner, `BorrowerLeft`, `SendToLostAndFound` |
| 4 (#306) | The server makes starting items; the safeguards and the rejoin swap are gone |
| 5 (#307) | No client item adoption |
| 6a (#308) | The owned-items list |
| 6b (#310) | Retrieval |
| L7 (#311) | Essentials stay out of containers |
| 7 (#312) | `RecordReplacement` |
| 8 (#313, open) | Retrieving installed gadgets |

## Code

| Where | What |
| --- | --- |
| `Multiplayer.Core/Items/OwnedItemEntry.cs`, `OwnedItemLedger.cs` | The entry, `where`, birth and end; the ledger |
| `Multiplayer.Core/Items/OwnedItemReconciler.cs` | The load check and migration, as a plan |
| `Multiplayer.Core/Items/OwnedItemPlaceText.cs` | The list's wording and `OwnedItemRetrievalRule` |
| `Multiplayer.Core/Items/EssentialClaimSlotPolicy.cs` | Distinct claim slots |
| `Multiplayer.Core/Items/ItemAccess.cs` | The access policy and the fixture list |
| `Multiplayer/Server/Items/OwnedItemsServer.cs`, `OwnedItemsSaveCodec.cs` | The server's ledger, its save, the audit, `SendToLostAndFound`, `RecordReplacement` |
| `Multiplayer/Server/Items/OwnedItems/` | `StartingItemsIssuer`, `OwnedItemRows`, `OwnedItemRetrieval` |
| `Multiplayer/Server/Items/ItemAccessServer.cs` | The facts for `ItemAccess` |
| `Multiplayer/Client/Items/OwnedItemsList.cs`, `Client/Career/OwnedItemsCareerManagerScreen.cs` | The client's list and page |
| Tests | `Multiplayer.Core.Tests/Items/OwnedItem*Tests.cs`, `AbsentOwnerTests`, `LostAndFoundAwayOwnerTests`; `Multiplayer.Protocol.Tests/OwnedItemsSaveCodecTests.cs`; scenarios `ledger-save`, `item-id-roundtrip`, `fixture-no-stash`, `absent-owner`, `offline-lost-and-found`, `borrowed-disconnect`, `starting-items`, `owned-list`, `owned-retrieve`, `deposit-refused`, `item-lineage` ([testing.md](../testing.md)) |
