# World items

> Status: built in R7 of the [item refactor](../item-refactor.md) (#389-#398) and merged into
> `items/refactor`, not yet proved in a game: that is the end pass (OI-REF-01).
> It covers the items the game's scenes make
> (scene-authored items, by stable key), the catalogue two games compare at join, a client's binding of
> them, shelves (static parents) and the station offices' stock. Who is sent which item is
> [projection.md](projection.md); a scene item's save rows are [save-load.md](save-load.md); settling
> onto a shelf is [physics.md](physics.md). With projection.md it replaces the legacy
> [item-world-sync.md](../../legacy/items/item-world-sync.md).

| Side | Code |
| --- | --- |
| Core | `Multiplayer.Core/Items/WorldItems/`: `AuthoredCatalogueKeys`, `StaticParentKeys`, `OfficeStockRules`, `AuthoredHierarchyPolicy` |
| Shared | `Multiplayer/Shared/Items/WorldItems/`: `AuthoredItems`, `AuthoredWorldItemCatalogue`, `WorldItemStableIdentity`, `WorldItemStaticParentRegistry`; the key's capture in `NetworkedItem.Awake` |
| Server | `OfficeStockReplenishment` (`Multiplayer/Server/Items/WorldItems/`); the catalogue handler in `NetworkServer.Items.cs`; `ServerPlayer.RefusedAuthoredKeys`, `TakesAuthored` |
| Client | `AuthoredProjections` (`Multiplayer/Client/Items/WorldItems/`); `ClientItemManager.QuarantineUnboundClientItems`; the catalogue send and answer in `NetworkClient.Items.cs` |

## Scene-authored items

Every game loads the same scenes, so each has its own object for each item the scenes place: a mug
on an office desk, a shop's stock, a fire extinguisher. The server's record of such an item binds
every game's object to it by a **stable key**, the same in every game. Its NetId is the server's, for
the session.

**The key.** A `NetworkedItem` takes its key (`AuthoredItemKey`) in its `Awake`, before DV's own
lifecycle can move the object out of its scene's hierarchy, and only if its scene's name starts with
`game_content_` or its root is `[origin shift content]` (`WorldItemStableIdentity.IsLikelyAuthoredAtAwake`).
The key is the first 128 bits of SHA-256, as 32 hex digits (`WorldItemStableIdentity.Capture`), over:

- the scene, with every `game_content_*` scene named `game_content`;
- the hierarchy path, each node with its sibling index (`HierarchyPath`);
- the prefab name;
- the absolute position and rotation, to the millimetre.

Only the hash is kept. Two flags are taken at the same time: an office slot (a node named `Offices`
on its path, `AuthoredHierarchyPolicy.ContainsNode`) and a shop slot (a `DV.Shops` component above it).
An object made later never gets a key of its own: the key is taken only in `Awake`, by the test
above (a refill takes its slot's, [below](#authored-projections-on-a-client)). It is a **dynamic** item, with a GUID
from the server ([ledger.md](ledger.md)) and a client copy made for each Create
([projection.md](projection.md#on-a-client)).

## The authored catalogue

**Built** once in each game, the first time it's needed (`AuthoredItems.Ensure`), by
`AuthoredWorldItemCatalogue.Rebuild`: every `ItemBase` in a loaded scene whose name starts with
`game`, inactive ones included (`Resources.FindObjectsOfTypeAll`), not a clone, with inventory specs.
On the server, building it registers every item in the interest index.

**Collisions.** Two objects that make one key collide. Which a game finds first is its own, so neither
is bound: the key leaves the catalogue. The catalogue's **baseline** is frozen as built: the count of
every key the scenes have, collided ones too; the collided keys; and a digest over all of them
(`AuthoredCatalogueKeys.Digest`: the first 16 bytes of SHA-256 over the sorted keys). The live entries
change as office props are taken and made again, so the join compares the baseline, never them:
comparing the live map rejected a client that joined while a slot waited to be refilled.

**The join check.** Once a client's world has loaded (`NetworkClient.SyncWorldState`), it quarantines
its items ([below](#authored-projections-on-a-client)) and sends its summary
(`ServerboundWorldItemCataloguePacket`: count, collided keys, digest). The server
(`NetworkServer.OnServerboundWorldItemCataloguePacket`):

1. accepts the catalogue if the count and digest match its own (`AuthoredCatalogueKeys.Match`);
2. refuses, for that player, every key that collides in either game (`AuthoredCatalogueKeys.Refused`),
   kept as `ServerPlayer.RefusedAuthoredKeys`;
3. answers (`ClientboundWorldItemCataloguePacket`): accepted or not, its own count, number of
   collisions (`HostCollisionCount`, not the keys) and digest, a reason, and the refused keys, which
   the client logs.

`ServerPlayer.TakesAuthored` decides whether a player is sent an item: any dynamic item; a scene item
only if their catalogue was accepted and its key isn't refused. Interest asks it of every item
([projection.md](projection.md#interest)). A key that collides in the host's game is refused for
every player; one that collides in a client's only for that client. Before R7 one collision on either
side refused the client's whole catalogue, and it got no scene item all session (F46, OI-SYNC-08).
A catalogue that doesn't match still gets no scene item for the session; dynamic items flow. Both are
logged (`world-item.catalogue-negotiated`).

## Authored projections on a client

A client never makes an item of its own (OI-SYNC-01, #307). Once its world has loaded,
`ClientItemManager.QuarantineUnboundClientItems` takes every item that has no NetId: a scene item goes
**dormant** (no NetId, inactive, no state sent, kept by its key, `DormantAuthoredClientProjections`);
any other is destroyed. One that wakes later unbound (`RegisterUnboundClientItem`) is queued and dealt
with the same way, 16 a frame.

**Binding.** A Create with a key binds this game's object (`AuthoredProjections.TryBind`): it is found in
the catalogue by its key, any other object holding that NetId is retired, and it takes the NetId, is
bound and activated, applies the update and acknowledges `Projected`. A key the catalogue lacks is an
integrity failure (`world-item.authored-binding-missing`) and is acknowledged `Failed`, so the server
sends it again later ([projection.md](projection.md#retries)). A retirement puts the object back to
dormant; it keeps its key for the next Create.

**Detached slots.** When a client applies an office prop going into a hand, an inventory or a tool's
magazine (`AuthoredProjections.DetachForPossession`, from `NetworkedItem.HandleInventoryOrHandState`
and `HandleMagazineState`), its object
lets go of its key and leaves the catalogue, and the key is noted. When the server refills that slot
([Office stock](#office-stock)), only a game that noted the key makes a fresh copy for it, which takes
the key (`AdoptAuthoredItemKey`); every other game binds the dormant object it still has.

**Saved.** A scene item's save is a row by its key: an override when it has moved from where the scene
has it, a tombstone when it's gone. One in a player's inventory becomes a dynamic item before it is
saved there, so the scene doesn't make it again beside it (OI-PER-16; [save-load.md](save-load.md)).

## Shelves

A shelf is a vanilla `ItemStaticParent`: the floor of a shed, a shelf in an office. An item on one has
the placement `StaticParent`, the shelf's key (`WorldParentKey`) and a pose local to it; its home in
the ledger is `Shelf(key)` ([ledger.md](ledger.md)).

**Its key** is its path by names alone, root first, and nothing else (`StaticParentKeys.Material`,
hashed by `WorldItemStableIdentity.CaptureStaticParent`): no scene, no position, no sibling index, and
not the `[origin shift content]` root DV moves streamed content under. Under that root a tile's place
is the order the tiles happened to load in, so before R7 one game's key for a shelf was another's for
nothing, and a moved shelf never resolved (OI-SYNC-09, F47). Keys from before R7 aren't read (G3).

**Resolving a key** (`WorldItemStaticParentRegistry.TryResolve`). The registry keeps the loaded scenes'
shelves by key. Of shelves with one key (two copies of a building, two shelves of one name) the one
meant is the one that puts the item's shelf-local position nearest where the item is
(`StaticParentKeys.Nearest`); each caller passes both. A scene loaded or unloaded since the last scan
rescans them before any lookup, so a tile with a same-keyed shelf is a candidate at once; before the
R7 fixes a key already known never saw it, and resolved to the other building's shelf. Otherwise a
miss rescans them at most once every 2 s (`RebuildSeconds`): before R7 every miss rescanned, and a
key no game had did it at every sample. The server refuses a client's update onto a shelf it can't resolve, or more than 192 m
from the sender (`invalid-or-distant-static-parent`), and a sample or settlement
held to the same distance ([physics.md](physics.md)).

**Adopting the resting shelf** (SH1). Vanilla parents an item to a shelf on its next collision with it
(`ItemReparentingBase.OnCollisionEnter`), about eight frames after a drop, and a dropped mug can sleep
sooner. Before R7 a client's settlement was then captured in the world, and the shelf's parenting
that followed was its game's alone: the server kept the mug where it left the hand. Now the simulator
takes the shelf under an item before its settlement is captured, as vanilla would
(`NetworkedItemSpatialManager.AdoptRestingShelf`): the first thing below the item's colliders, within
0.15 m, if it's part of a shelf.

**The cell index** (SH2). A shelf item is indexed by its cell, as one on the ground
([projection.md](projection.md#cells), OI-SYNC-04), so a late joiner is shown it.

**Its owner's list** says "on a shelf" (`PlaceText`, OI-LIST-21), and its owner may retrieve it to their
Lost and Found as any loose item (`RetrievalRule`, OI-LIST-09; [owned-list.md](owned-list.md)).

## Office stock

A station office's props are stock: each is a slot the server keeps stocked, not one object for ever
(`OfficeStockReplenishment`, run every 0.5 s on the server). A prop is stock
(`AuthoredItems.IsEligibleOfficeProp`) if it is a scene item in an office slot, not a shop slot, not a
player's item or an essential, not snapped, and not a container, a gadget or a job paper.

**A player who could see it protects a place** (`OfficeStockRules.Protects`): every connected player whose
cells hold it. Where an item already lies (a knocked prop, a fork) that is 5×5, as far as interest keeps
items (`InterestCells.KeepRadius`), and the item is also kept while it is in any player's nearby
set, wherever it lies (not their known set: an item made from a container is known to every player and
stays so). Before the R7 fixes only 3×3 was checked there, and a player a cell and a half away still
had the prop when it was reset or cleared in front of them. An empty slot or a prop's place stays 3×3:
what would appear there is new, and new items enter interest at 3×3, so 5×5 kept an office empty for a
player shunting nearby. **A player still loading protects everywhere**: where they will appear isn't known
yet, and before R7 a player joining beside an office watched its props vanish (OI-SYNC-10, F30). On a
player host the host's own player counts; a dedicated host has none. The host's own player's nearby
set never holds a scene item, as it sends no catalogue
([projection.md](projection.md#player-hosted-and-dedicated-servers)), so a knocked prop is kept for it
by the 5×5 check alone (`OfficeStockReplenishment.AnyPlayerInterested`, `AnyPlayerHas`). That is
harmless: a prop that lies in the world or on a shelf is in a nearby set only within those 5×5
cells (`ServerItemManager.RefreshPlayerInterest`), and a fork is a dynamic item, in its nearby set
like anyone's.

1. **Taken.** A prop moved from the world or a shelf into a hand, an inventory or a held tool's
   magazine (vanilla's load by use: a sprayer aimed at a paint can) leaves its slot
   (`OnAuthoritativeTransition`). It loses its key and becomes a dynamic item with a GUID of its own,
   marked a **stock fork** (`LiveItems.PromoteAuthoredToPersistent`); the slot waits. Dropping it back
   by the desk doesn't make it a scene item again. Office props aren't a player's items, so nobody
   becomes a fork's owner by taking it.
2. **Made again.** Once nothing has protected the slot's place for 3 s, a fresh object of the prefab
   takes the key at the slot's pose and is registered (`TryCreateReplacement`). Clients bind it as above.
3. **Knocked.** A prop still in its slot that lies more than 0.25 m or 5° from where the scene has it
   (by its record or its object) is put back after 15 s, when nobody protects where it belongs or where
   it lies, and it isn't moving under a lease or grabbed (`OfficeStockRules.MayReset`). Before R7 only
   its place was checked, and a prop knocked out of the office vanished from beside the player who had
   it (OI-SYNC-13). The reset is one committed spatial pose, carrying its cause and why
   (`ServerItemManager.ResetAuthoredItemToBaseline`; OI-SYNC-18).
4. **Abandoned.** A fork left in the world, on a shelf or in a car is cleared after 30 s with nobody
   protecting where it lies, with the cause `AbandonedStockForkCleared`. Never one that is owned,
   claimed for a slot, in Lost and Found, held or stored, moving under a lease or grabbed
   (`OfficeStockRules.MayClearFork`, OI-SYNC-11): a cleanup is the lowest claim, and the lease and
   grab guards are new in R7. An installed item is never cleared ([projection.md](projection.md#installed-items)).

A fork lying in the world isn't saved, as an unowned item that isn't a player's never is; one a player
carries is saved with them, marked a fork (`SavedItemRow.IsReplenishableStockFork`).

A fork left in a parked cab is cleared after 30 s like any other. Changing that is paused (OI-SYNC-12,
BR LF8, D4).

## Tests

- `Multiplayer.Core.Tests/Items/WorldItems/`: `AuthoredCatalogueKeysTests` (the digest, a match, the
  refused keys), `StaticParentKeysTests` (names only, the nearest of shelves with one key),
  `OfficeStockRulesTests` (a loading player protects everywhere, never a fork with a better claim, a
  knocked prop waits), `AuthoredHierarchyPolicyTests`.
- `Multiplayer.Core.Tests/Items/Projection/WorldItemIndexingTests.cs`: a shelf item by its cell.
- `Multiplayer.Protocol.Tests/WorldItemAuthorityPacketTests.cs`: the catalogue packets.

## Scenarios

In [testing.md](../../testing.md) and `DevTools/Scenarios/gate.sh`. They run at the end pass (OI-REF-01),
on both host kinds unless marked:

- `shelf-item` (default, `RELEASE=throw`): a client's mug on a shed's shelf, seen by a late joiner,
  still there after a restart (the key in each game, OI-SYNC-09), and retrieved (OI-LIST-09);
  `MODE=twin`: a same-keyed shelf far away (a tile loading after the key was known) and one whose
  pivot is at the mug, and each game still resolves the right one;
- `item-bug-client-shelf` (default, `RELEASE=throw`; SH1) and `item-bug-shelf-unseen` (SH2, player host
  only), both required passes; `owned-list-shelf` (player host only) and `owner-offline WHERE=shelf`;
- `office-stock`: `loading` (a loading player keeps a fork, OI-SYNC-10), `MODE=knocked` (OI-SYNC-13) and
  `MODE=knocked-edge` (a player two cells away, whose game still has the prop, keeps it);
- `projection-acks MODE=collision`: a second object of one scene item in the client's game; that key
  refused alone, the catalogue still accepted (OI-SYNC-08);
- `save-homes`: a scene item in a slot through a restart, once.
