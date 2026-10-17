# Server and players: one server, any number of players

> Status: planned (2026-10-17). This is the target model for how a host and its players fit together.
> Nothing in it is built yet beyond the dedicated-host pieces in
> [dedicated-host.md](dedicated-host.md), which are the "no local player" case of it. The
> [plan](#plan) says what to build first.

## The idea

Two roles that today are tangled together:

- **The server** owns the world. It loads the career and runs trains, junctions, jobs, weather and
  time, items lying in the world, and the career's shared progress: money, licences, garages and
  debt. It holds every player's state, keyed by their GUID. It keeps the world loaded and simulated
  wherever players are. It has no state of its own as a player.
- **A player** is someone with an identity (their GUID) and their own state: where they are, their
  inventory and what they're holding. Every player is a client of the server.

How the two roles combine in each kind of game:

| Kind of game | What it runs |
|---|---|
| **Player host** (hosting from your game) | A client and the server in one process. You play with graphics like anyone else. Your player is an ordinary client of the server running alongside it, and is stored and restored the same way as everyone else's. |
| **Dedicated host** | The same server with nobody logged in locally ([dedicated-host.md](dedicated-host.md)). |
| **Single player** | The mod already runs this as a one-player server, so it follows the same model. |

A career then isn't tied to how it's hosted. You can host it, a dedicated host can run it while you
join, and a friend can host it, and you're the same player each time.

## How far today's code is from it

The connection already has this shape. A host starts a `NetworkServer` and then a loopback
`NetworkClient` that logs in like anyone else, with its own `ServerPlayer` (`NetworkLifecycle.StartServer`,
see [architecture.md](../architecture.md#entry-point-and-lifecycle)). What breaks the split is
that the host's own player is special in several places:

| Where | Today | Target |
|---|---|---|
| **Saving its state** | Its position and inventory go into DV's single-player fields (`Player_position`, `Storage_Inventory`). Its items are left out of the mod's item records ("vanilla saves those", `WorldItemPersistenceSave.cs`). | Saved like any player's: `Players.<GUID>` for position, item records owned by its GUID for inventory (`WorldItemsV1`). |
| **Loading its state** | DV puts the inventory straight into the local player's hands as the world loads (`StartingItemsController.AddStartingItemsCoro`). | The server restores it at that player's login, as it does for clients (`AuthoritativeItemRegistry.RebindPersistentOwner`, `WorldItemPersistenceManager.OnOwnerConnected`). |
| **Starting items** | Each game's DV adds missing starting items (comms radio, maps, compass, wallet, catalogue, ...) to its local inventory while loading (`StartingItemsController.StartingItemsSafeguard`). A client therefore gets fresh copies before the server hands back its saved ones; that was the duplicate behind a rejoin failure on 2026-10-17. | The server decides. A player new to the career gets the starting items from the server. A returning player gets theirs back. No game adds its own copies in multiplayer. |
| **Logic shortcuts** | About 300 `IsHost()` uses. Some bypass checks for the host's own player: control range (`NetworkServer.cs:2406`), topology range (`:2533`), port authority (`NetworkedTrainCar.cs:1072`). The host's own changes go through vanilla directly. | Changes the local player makes still go through the server, which validates them like anyone's. Shortcuts stay only where the server and that client share a process, and only for transport (no packet round trip), not for skipping validation. |
| **World coverage** | DV loads scenery and terrain, and gives cars their near physics LOD, around the one local player. A client far from the host gets frozen derailments and far-LOD cars on the host. | The server keeps the world loaded and at full physics detail around every player: the local one if any, and every remote one. |
| **Admin** | "Host only" means the host's own player. | Admins are identities: the install's own player and `Settings.AdminGuids` (built: `ChatManager.IsAdmin`). |

The shared career progress (money, licences, garages, debt, jobs) is already the server's: one
shared wallet, broadcast to everyone ([world-sync.md](../world-sync.md)). That doesn't change.
Per-player money and licences would be a separate feature.

## Save format

**Version 2, per player.**

- **Each player's state:** every player's position is under `Multiplayer.Players.<GUID>`, and
  their inventory is item records owned by that GUID. The host's own player and single player are
  no different.
- **A format marker** in the `Multiplayer` section says the save is version 2.
- **DV's own fields become a mirror.** `Player_position` and `Storage_Inventory` are written from
  the install's own player, so the career still opens in vanilla DV without the mod. The mod
  writes them but never reads them, except for the conversion below. On a dedicated host the
  install's player is the empty parked body, so the mirror is empty.

**Converting a version-1 save, once.** Saves from today's build, and careers last played in
vanilla, have the host player's state only in DV's fields. The first time the new build opens
one:

1. `Player_position` and rotation go into `Players.<owner GUID>`.
2. Each `Storage_Inventory` entry becomes an item record owned by that GUID, with placement "in
   inventory, slot N" and its state.
3. The marker is set.

Other players' entries are already in place, so they're untouched. After that the conversion has
nothing to do.

**Two edge cases:**

- **Whose GUID.** It's the owner of the install that opens the career. That's you when your own
  game, or a dedicated host on your own install, opens it. A career copied to another server PC
  first needs `-mp-owner <GUID>`, or opening it once on your own PC first.
- **Played in vanilla in between.** The mod stores a fingerprint of the mirror it last wrote. If
  DV's fields no longer match it, vanilla has played the career since, and the conversion runs
  again from DV's fields.

## Loading and joining

1. **The server loads the world.** Cars, items in the world, jobs, the shared career and every
   player's saved record come from the save.
2. **The local player object** that DV needs (see [dedicated-host.md](dedicated-host.md#the-local-player-must-exist-even-if-nobody-plays-it))
   starts empty. On a dedicated host it stays an empty parked body. On a player host it's you, and
   it's filled by the next step.
3. **Every player logs in,** the local one included, through the existing join flow
   ([architecture.md](../architecture.md#join-flow)). The server binds that GUID's records: items
   come back into their inventory and hands, and they spawn at their saved position.
4. **Starting items:** a GUID the career has never seen gets the starting items from the server,
   as part of its first join. That replaces both today's `StarterKitsGiven` kit and each game's own
   safeguard.

For the local player of a player host, step 3 is a loopback login. It costs no network, but it
follows the same rules. The local player's DV inventory is filled from the server's records rather
than straight from the save.

## World coverage around every player

These are needed on a player host as much as on a dedicated one:

- **Physics LOD (built):** every multiplayer server adds every player's position to DV's
  physics-LOD reference points (`DistanceLodSystemPatch`).
- **Track ballast everywhere (built):** every multiplayer server loads every Near scenery tile
  (`Components/Networking/ServerWorldCoverage.cs`): the ballast along all the track, bridge decks
  and tunnel portals. Measured: 36 s in the background after a load, about +76 MB, no frame cost.
- **The derail freeze (built):** DV keeps a derailed car kinematic until its Near and Far tiles and
  terrain cell are all loaded. On a server it may also move where there's static ground under both
  bogies (`ServerWorldCoverage.MayMove`, `Patches/Train/DerailedTrainCarIsKinematicHandlerPatch.cs`).
  DV only re-checks on streaming changes, so the server also re-checks every derailed car four
  times a second: a car that slides off the ballast where no terrain is loaded is frozen where it
  is rather than falling through the map. With terrain around derailed cars (below), that's only
  for the second or so before the terrain under it loads.
  - A client keeps a derailed car kinematic, at the host's pose, wherever the client itself has no
    world under it (`NetworkedTrainCar.UpdateDerailedKinematic`).
  - Scenarios: `far-derail`, `far-derail-embankment` ([testing.md](../testing.md#regression-gate)).
- **Scenery and terrain streaming:** today one anchor follows the one local player, or, on a
  dedicated host, the longest-connected player (`DedicatedHostAnchor`). The target is the world
  loaded around every player: [multi-anchor-streaming.md](multi-anchor-streaming.md). In short:
  - **Terrain alone isn't enough.** Derailed cars and items rest on more than terrain:
    - track ballast, bridge decks and tunnel portals are in the Near scenery tiles;
    - platforms, buildings, roads and tunnel walls are in the Far tiles.
  - **Scenery (built):** every Near tile is loaded (above). Far tiles are loaded around every
    remote player (3×3 tiles of 1024 m) and every derailed car (the tiles within 64 m of it), at
    most 40 extra: `Components/Networking/ServerSceneryCoverage.cs` and
    `Patches/World/StreamerPatch.cs`, see
    [multi-anchor-streaming.md §1.4](multi-anchor-streaming.md#14-one-streamer-loading-the-union-of-boxes-feasible-two-patches).
    They render like any tile; whether that costs a player host anything is still to measure.
  - **Terrain (built):** every multiplayer server also loads terrain colliders around every remote
    player (DV's 5×5 box of 512 m cells) and every derailed car (the cells within 64 m of it):
    `Components/Networking/ServerTerrainCoverage.cs`, `Patches/World/TerrainGridPatch.cs`, and
    [multi-anchor-streaming.md §2.3](multi-anchor-streaming.md#23-the-multi-anchor-change). At
    most 100 extra cells, about 1.6 MB each; the memory per extra player is still to measure.
  - **Cost:** about 60-200 MB per extra player all told.
  - **Rejected:** heightmap ground proxies and leasing derailments to clients.
- **The origin shift** stays single. It's centred on the box around all players, so precision is
  about 1 mm anywhere on the map.

## What stays specific to a dedicated host

These are about there being no local player at all, so they stay in [dedicated-host.md](dedicated-host.md):

- the service identity (the host's own client isn't a player), auto-host from the command line, and
  reduced rendering;
- pausing when nobody is online (a player host has a player);
- the watchdog, heartbeat, stop request and save lock.

Timed saves and backup copies could become server-wide, since a player host benefits from them
too.

## Plan

| Step | Work | Size |
|---|---|---|
| 1 | **Starting items from the server.** In multiplayer, games don't add their own starting items. The server gives them to players new to the career, and returning players get theirs back. This fixes today's client duplicate on its own. | Small to medium |
| 2 | **The host's own player stored per GUID.** Save its position and items like any player's. Restore them at its loopback login. Its DV inventory starts empty in multiplayer. | Medium |
| 3 | **Format marker and version-1 conversion,** with the vanilla mirror and its fingerprint. `-mp-owner`. | Medium |
| 4 | **Physics LOD for every player on every host.** | Small |
| 5 | **Multi-anchor streaming** ([multi-anchor-streaming.md](multi-anchor-streaming.md#73-stages)): first a second dvmp client or a debug "virtual player", and the two deciding measurements (whole-map scenery, what a derailed car lands on today). Then the terrain and streamer patches, then items, then the origin policy. | Medium (was thought large) |
| 6 | **Audit of the host-player shortcuts:** the local player's changes are validated like anyone's. Done gradually, area by area. | Ongoing |

Steps 1-3 change how every multiplayer save stores players, and step 4 changes normal hosting, so
each needs the regression gate's item, join and persistence scenarios:

- `rejoin` and `rejoin-crash`
- `save-reload` and `inventory-persist`
- `borrowed-disconnect`, `lost-and-found` and `recall`
- `shop`

Each should run on both a player host and a dedicated host (`UP_ARGS=--dedicated`).

Steps 1-3 also need two new scenarios:

- **`host-player-persist`:** the host's own player saves and reloads its inventory and position
  through its GUID.
- **`career-convert`:** a version-1 save converts once, with nothing duplicated or lost, and a
  second load doesn't convert again.

## Risks

- **Item duplication or loss in real careers** during the conversion. Mitigations: it's
  idempotent behind the marker; DV keeps the old save files, so nothing is overwritten; dedicated
  hosts keep backup copies; and the scenarios above.
- **Vanilla compatibility:** the mirror must stay correct, or a career opened without the mod loses
  the player's inventory.
- **Ordering at load:** the local player's DV inventory has to stay empty until its login, and
  vanilla code that runs during the load and expects the local inventory (licence and starting-item
  safeguards, job booklets) has to cope.
- **Other mods** that read or write the local player's inventory through vanilla at load.
