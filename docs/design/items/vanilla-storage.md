# Vanilla's storage, respawn and bins

> Status: being written in R6 of the [item refactor](../item-refactor.md) (2026-10-25), one section per
> PR, not yet proved in a game: that is the end pass (OI-REF-01). Vanilla's own behaviour is in
> [research/DERAIL_VALLEY_LOST_AND_FOUND_LOGIC.md](../../research/DERAIL_VALLEY_LOST_AND_FOUND_LOGIC.md).

| Side | Code |
| --- | --- |
| Shared | `StorageControllerPatch`, `StorageIntegration`, `RespawnOnDropPatch` (`Multiplayer/Shared/Items/VanillaStorage/`); `StartingItemsControllerPatch` (`Multiplayer/Shared/Career/`) |
| Server | `Binning`, `ItemDumpsterPatch` (`Binning.cs`), `RespawnOnDropRangePatch` (`Multiplayer/Server/Items/VanillaStorage/`) |

Vanilla keeps an item's place in its own storage lists (inventory, world, Lost and Found, item
containers) and moves items on its own: a respawn, a bin, a load's duplicate pass. In multiplayer an
item's place is the server's (the [ledger](ledger.md) and live state): vanilla's lists are a
projection the mod keeps (`StorageIntegration.MoveTo`), and none of vanilla's own moves may decide
where an item is. Every game runs the guards; what is allowed runs on the server.

## Vanilla's Lost and Found list

Nothing of vanilla's puts an item in its Lost and Found list in multiplayer (OI-VAN-06): every
`StorageController.AddItemToLostAndFound` is refused, owned item or not, whoever calls it (a respawn,
a shelf, a snap point, a cleared container, a gadget's load, a shop). Its activation, its
world-to-Lost-and-Found sweeps and its access point are off too. Lost and Found is the server's
([lost-and-found.md](lost-and-found.md)); the mod projects an entry into vanilla's list itself. An
installed gadget whose anchor is gone at load goes to its owner's Lost and Found
(`GadgetDestinationMissing`, OI-LF-22).

## Storage lists

`AddItemToStorageItemList` takes an item out of its old list before it asks whether the new one
takes it, so a refused move left the item in none and unsaved (H3). It is asked first, owned or not,
and a refused move keeps its list. An owned item's insertion skips vanilla's respawn bookkeeping.

## Respawn

Vanilla's `RespawnOnDrop` respawns or destroys an item far from the player: an item that belongs to
the player into Lost and Found, another back to its spawn, one that doesn't respawn destroyed. In
multiplayer (OI-VAN-05) a client never runs it for a networked item, and the server allows only
(`Binning.AllowsRespawnOrDestroy`):

- **a scene's own item nobody owns** back to its spawn, as vanilla does (shop and office stock), a
  move the host's live state sends like any other;
- **a binned item's destroy** ([below](#bins)).

An owned item is never respawned or destroyed by it: its collection is the server's
([lost-and-found.md](lost-and-found.md#ways-in)). "Far" is from every player
(`RespawnOnDropRangePatch`).

## Bins

A dumpster (`ItemDumpster`) takes what falls in and its `RespawnOnDrop` destroys it once every player
is 75 m off (OI-VAN-02, D10). On the server the bin is a request (`ItemDumpsterPatch`,
`Binning.TryBin`): its binner is the item's holder, else the player whose move it was last.

- **Their own item, or one nobody owns,** is the bin's: vanilla's dumpster takes it, Lost and Found
  leaves it alone, and the destroy is the binner's: its removal cause is `Discarded` by them, so an
  owned item's entry ends `Discarded`. An essential is issued again at its owner's next join, as one
  they have none of.
- **Someone else's** is refused: in a hand it just isn't the bin's; lying in the bin it is put down
  beside it, on the binner's side (`ServerItemManager.PutAt`, `BinRefused`), and stays theirs.

A client's dumpster stays vanilla's: its items don't respawn or go there.

## The load

Vanilla's duplicate pass (`StartingItemsController.CombineAndResolveIllegalDuplicates`) keeps one
radio, one wallet, one of each licence in a whole save, and a multiplayer save holds every player's:
it dropped all but one from Lost and Found and the world at each load. It doesn't run in multiplayer
(OI-VAN-01): the lists are joined whole. Nor do its safeguards that add missing starting items and
licences: the server issues them ([inventory.md](inventory.md), essentials).

## Tests

Scenarios ([testing.md](../../testing.md)): `bin-owned`, `duplicate-essentials-load`.
