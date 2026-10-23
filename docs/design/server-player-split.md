# Server and players: one server, any number of players

> Status: being built (2026-10-17). This is the target model for how a host and its players fit
> together.
> - **Steps 1-3 of the [plan](#plan):** starting items, the host's own player stored per player,
>   and the one-time conversion. They're written (branch `dev/player-state`) and tested on one
>   pair. They key players by the Steam-derived ID from step 0.
> - **Step 0, [identity by SteamID](#identity):** agreed (2026-10-17), built and tested on one
>   Steam account (a verified login to a dedicated host, and the re-key). A refused ticket from
>   another account is still untested.
> - **The dedicated-host pieces** in [dedicated-host.md](dedicated-host.md) are its "no local
>   player" case.

## Implementation status (2026-10-17)

- **Step 0a, the Steam identity: built and tested (2026-10-17), as far as one Steam account allows.**
  - On a dvmp pair (`-mp-allow-unverified`) the host's player had the install GUID, the client
    its `...c11e`, and every persistence scenario passed, `dedicated-admin` too.
  - **Verified:** a dedicated host started with `dvmp up --dedicated --verified` (no
    `-mp-allow-unverified`). Its own client is the service identity, so the dvmp client, on the
    same Steam account, logs in as the owner, as a server's owner would from their own game.
    - The ticket (`GetAuthSessionTicket(default)`) passed `BeginAuthSession`, and
      `OnValidateAuthTicketResponse` came back `OK`.
    - The client was admitted as the Steam-derived ID, and was an admin as the owner with
      `Admins` empty.
  - Not tested: a refused ticket and the kick on a late rejection, which need another account.
  - `PlayerIdentity` turns a SteamID64 into a player ID and back, parses typed IDs (a SteamID64,
    or a GUID) and gives the local player's ID and the owner's (`-mp-owner`, or the local one).
  - The login packet carries the claimed SteamID64, a session ticket and, from the host's own
    client only, a per-process host token. It still carries the install GUID, for 0b.
  - `PlayerAuthentication` on the server:
    - the host token admits the host's own player, or the service identity on a dedicated host;
    - with `-mp-allow-unverified`, everyone else gets the GUID they claim;
    - otherwise a ticket is required and checked with `BeginAuthSession`, and a negative
      `OnValidateAuthTicketResponse` kicks the player;
    - the session ends on disconnect, and a reconnect replaces the old one.
  - A refused login gets `dr/auth_failed`.
  - `Settings.Admins` (SteamID64s, or GUIDs) replaces `AdminGuids`, and the dedicated host's owner
    is `CareerOwner.Id`.
  - dvmp starts every host with `-mp-allow-unverified`, so the gate doesn't exercise verified
    logins.

- **Step 0b, re-keying old install GUIDs: built and tested** (2026-10-17). On a new career, the
  dvmp client got 15 items under `...c11e` on an ordinary host; at its first verified login to a
  dedicated host on that career, 31 records (item records' owner and placement identities, and
  `Players`) moved to its Steam ID, none were left under `...c11e`, and all 15 items were in the
  registry under the new ID. Four of them came back on the client wrongly, from a dedicated-host
  bug that happened without any re-key too, since fixed (dedicated-host.md, Implementation status).
  `InstallGuidRekey` runs at login, before the player's saved items are given back:
  - It runs when the player's ID is a Steam one, the install GUID they sent differs, and the
    career has data for that GUID and none for the Steam ID.
  - It rewrites the save as loaded: the `Players` key, item records' owner and placement
    identities, Lost and Found owners, the saved ledger (`OwnedItemsV1`: owners, holders, who caused
    each entry, starter kits; R1) and the cold-container snapshot (decoded, re-keyed and encoded
    again). A system that imports later then finds the new ID.
  - It also re-keys what's already imported: the item registry, Lost and Found (restored and
    pending entries), the cold-container graph and the ledger's owners, holders and starter-kit
    marker (R1).
  - The gate reaches it through the `rekey` scenario, which makes the host resolve the client's
    next login to a Steam-form ID, since `-mp-allow-unverified` keeps GUIDs. A verified dvmp
    pair (above) does it for real.
- **Step 1, starting items: built and tested.**
  - The host sends what a joining player already owns: prefab and inventory slot, from live
    records, unrestored Lost and Found entries and cold containers (`OwnedItemsSummary`, two new
    fields in `ClientboundSaveGameDataPacket`).
  - The client counts them as present when DV's starting-item and licence safeguards check what's
    missing (`StartingItemsClientPatch`, a postfix on `CombineAndResolveIllegalDuplicates`,
    whose result only those safeguards use).
  - A player host does the same from its own save (`StartingItemsServerPatch`). A dedicated host
    skips both safeguards, so its parked body stays empty.
  - `rejoin`, `rejoin-crash` and `inventory-persist` passed. After a restart, the client's inventory
    held each item once.
  - This was narrower than "the server gives starting items": a genuinely missing item was still made
    by the player's own game, as in single player, and then adopted.
  - Replaced by owned-items PR 4: the server makes the starter kit and essentials, both safeguards
    are off in every game, and the two patches and packet fields are gone.
- **Steps 2 and 3, the host's own player by player ID and the one-time conversion: built and
  tested** (2026-10-17, one pair: `host-player-persist`, `rejoin`, `rejoin-crash`, `save-reload`,
  `inventory-persist`, `borrowed-disconnect`, `lost-and-found`, `recall`, `shop`).
  - The test found that an item a player found in the world and carried (a mug) wasn't saved:
    the save only kept dynamic items with DV's `BelongsToPlayer`, which only bought items,
    licences, money and starting items have. Clients had always lost them at a host restart; the
    host used to keep them through DV's own inventory list. Now anything in a player's inventory
    or hand is saved, as vanilla's inventory keeps everything (`ShouldPersist`).
  - Not tested yet: converting an old career (`career-convert`), and a dedicated host. All of it is in `PerPlayerSaveFormat`, called when hosting starts
  (`SaveGameManager_FindStartGameData_Patch`), before DV places the player or loads an item.
  - **Conversion:** a save without the marker `Multiplayer.PlayerStateFormat = 2` has its
    `Player_position`, rotation and `Storage_Inventory` turned into `Players.<owner>` and item
    records owned by the owner's player ID. The owner is the Steam account the game runs under, or
    `-mp-owner` (`CareerOwner.Id`). Then the marker is set. Converting the same save twice
    changes nothing the second time (an inventory item without a persistent ID gets one derived
    from the owner, its place in the inventory and its prefab, not a random one), and so does
    re-keying: `SaveFormatConversionTests` (`Multiplayer.Protocol.Tests`) checks both lose and
    duplicate nothing. This is the JSON only; `career-convert` in a real game is still to do.
  - **Each load:**
    - a player host's own state saved under the install's GUID moves to its Steam ID, once
      (`InstallGuidRekey.RekeySave`); other players' moves at their login;
    - the local player spawns at its ID's saved position (not on a dedicated host);
    - DV's inventory list is emptied for the load, since items come back from the records at the
      local player's login;
    - the local player's owned items, read from the save, feed the safeguards.
  - **Saving:**
    - `IsPlayerPossession` (was `IsRemotePlayerPossession`) treats the host's own player like
      anyone, resolving its GUID from the runtime player ID when the record lacks one;
    - `Players.<GUID>` includes the host's own player, from its local transform;
    - DV's inventory list is no longer stripped of mod-managed items, so it stays the copy for
      vanilla DV (`RemoveVanillaStorageDuplicates(keepInventory)`).
  - **Single player:** the mod runs it as a one-player server, so it uses the same format.
  - **Still to build:** the fingerprint that would re-convert a career played in vanilla in
    between.
  - **New scenario `host-player-persist`:** the host stashes an item, saves, both games restart,
    and the host must get its inventory back once, at the same place, with the save in the new
    format and the vanilla copy kept.

## The idea

Two roles that today are tangled together:

- **The server** owns the world. It loads the career and runs trains, junctions, jobs, weather and
  time, items lying in the world, and the career's shared progress: money, licences, garages and
  debt. It holds every player's state, keyed by their [player ID](#identity). It keeps the world
  loaded and simulated wherever players are. It has no state of its own as a player.
- **A player** is someone with an identity (their player ID, from their Steam account) and their
  own state: where they are, their inventory and what they're holding. Every player is a client of
  the server.

How the two roles combine in each kind of game:

| Kind of game | What it runs |
|---|---|
| **Player host** (hosting from your game) | A client and the server in one process. You play with graphics like anyone else. Your player is an ordinary client of the server running alongside it, and is stored and restored the same way as everyone else's. |
| **Dedicated host** | The same server with nobody logged in locally ([dedicated-host.md](dedicated-host.md)). |
| **Single player** | The mod already runs this as a one-player server, so it follows the same model. |

A career then isn't tied to how it's hosted. You can host it, a dedicated host can run it while you
join, and a friend can host it, and you're the same player each time.

## Identity

A player is their Steam account. That makes "you're the same player wherever the career is hosted"
true across PCs, and it makes admin rights and bans mean something, because the server verifies the
identity instead of trusting it.

**Why not today's GUID:**
- The mod's `Settings.Guid` is per install, not secret and not verified. Under this model a GUID
  *is* a player (inventory, position, admin rights), so anyone who copied a settings file could be
  that player.
- The same person on another PC would also be a new player.

**The player ID is a GUID derived from the SteamID64.** It's a fixed prefix plus the 64-bit ID, and
it can be turned back into the SteamID. Every GUID-typed field and save key stays as it is; only
where the ID comes from changes. That covers:
- `ServerPlayer.Guid`;
- the item registry's `PersistentOwnerIdentity` and `PlacementPlayerIdentity`;
- saved records' `OwnerIdentity`;
- Lost and Found's and cold containers' owner strings;
- `Players.<id>` and `StarterKitsGiven`.

No 64-bit number passes through JSON as a number, so nothing is mangled by a double. Where a person
types an ID (admins, `-mp-owner`), it's the SteamID64 as a string.

**Logging in.** DV ships Facepunch.Steamworks (`Facepunch.Steamworks.Win64.dll`), which the mod's
transport already uses.
1. **The client sends:**
   - its claimed SteamID64;
   - a session ticket (`SteamUser.GetAuthSessionTicket`);
   - its old install GUID, needed only for re-keying below.
2. **The server:**
   - calls `SteamUser.BeginAuthSession(ticket, steamId)`, and on OK admits the player under the
     Steam-derived ID;
   - kicks them if the final `SteamUser.OnValidateAuthTicketResponse` is negative. That response
     also carries the owning account; for Family Sharing the server keys on the player, not the
     owner;
   - calls `EndAuthSession` on disconnect.
3. **Re-keying old data, once per player:** if the save has data for the player's old install GUID
   and none for their Steam-derived ID, the first verified login moves it to the Steam ID.
   - That covers `Players`, item records, Lost and Found and cold containers.
   - After that install GUIDs aren't used for that player again.

**The edges:**
- **The host's own player:** no ticket. It's the loopback client in the same process, so the
  server trusts it, as it trusts `IsHost(player)` today. Its ID comes from the Steam account the
  game runs under.
- **No service identity** (since host-local-session stage 3): a dedicated host has no player of its
  own, so its owner's client on the same PC, sharing its Steam account, is the only player with it.
- **The owner:** on a dedicated host, the owner is the Steam account running the server. That's
  verified, which a guessable GUID isn't. It decides who the one-time conversion gives the old host
  player's state to, and who is an admin without being listed. `-mp-owner <SteamID64>` overrides it
  for a career copied to another PC.
- **Admins:** a new `Admins` setting with SteamID64s replaces `AdminGuids` (built).
- **No Steam:** the game runs without Steam, and so does single player. The local player then falls
  back to the install GUID, and the next run with Steam re-keys it to the Steam ID as above.
- **Two games on one Steam account:**
  - **What collides:** the test harness runs a host player and a client on the same account, so
    they'd have the same ID.
  - **The fix:** hosts started by dvmp get `-mp-allow-unverified`. There, every client is
    admitted under the GUID it claims, ticket or not, as before. The host's own player is keyed
    by the install's GUID, so the harness can still set it, and the dvmp client keeps its fixed
    `…c11e` GUID.
  - It's off by default and should never be used on a public server.
- **Re-keying risk:** an old install GUID isn't secret, so someone who learned it could in principle
  claim that player's old data with their own Steam account. That only works until the owner's own
  first verified login. GUIDs aren't sent to other clients, so the risk is small.
- **Open question:** a host using `LiteNetLibTransport` without Steam on the server couldn't call
  `BeginAuthSession`. It would need the Steam Web API and a publisher key. Not designed for yet.

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
| **Admin** | "Host only" means the host's own player. | Admins are identities (built: `ChatManager.IsAdmin`): the host's own player, a dedicated host's owner, and the `Settings.Admins` list of SteamID64s, all verified Steam accounts from step 0a. |

The shared career progress (money, licences, garages, debt, jobs) is already the server's: one
shared wallet, broadcast to everyone ([world-sync.md](../world-sync.md)). That doesn't change.
Per-player money and licences would be a separate feature.

## Save format

**Version 2, per player.**

- **Each player's state:** every player's position is under `Multiplayer.Players.<player ID>`,
  and their inventory is item records owned by that ID. The host's own player and single player
  are no different. From step 0 the ID is the Steam-derived one, so there's one conversion (version
  1 to 2), not a later GUID-to-Steam one as well.
- **A format marker** in the `Multiplayer` section says the save is version 2.
- **DV's own fields become a mirror.** `Player_position` and `Storage_Inventory` are written from
  the install's own player, so the career still opens in vanilla DV without the mod. The mod
  writes them but never reads them, except for the conversion below. On a dedicated host the
  install's player is the empty parked body, so the mirror is empty.

**Converting a version-1 save, once.** Saves from today's build, and careers last played in
vanilla, have the host player's state only in DV's fields. The first time the new build opens
one:

1. `Player_position` and rotation go into `Players.<owner's player ID>`.
2. Each `Storage_Inventory` entry becomes an item record owned by that ID, with placement "in
   inventory, slot N" and its state.
3. The marker is set.

Other players' entries are already in place, so they're untouched. After that the conversion has
nothing to do.

**Two edge cases:**

- **Whose state it is.** It's the owner's: the Steam account the game opening the career runs
  under. That's you when your own game, or a dedicated host on your own account, opens it. A career
  copied to a server PC running under another account needs `-mp-owner <SteamID64>`.
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
   ([architecture.md](../architecture.md#join-flow)). The server binds that player ID's records: items
   come back into their inventory and hands, and they spawn at their saved position.
4. **Starting items:** the player's game adds only the starting items and licence papers the
   server says they don't already own. A player new to the career gets a full set, a returning
   player only what's missing (built in step 1; see Implementation status).

For the local player of a player host, step 3 is a loopback login. It costs no network, but it
follows the same rules. The local player's DV inventory is filled from the server's records rather
than straight from the save.

## World coverage around every player

These are needed on a player host as much as on a dedicated one:

- **Physics LOD (built):** every multiplayer server adds every player's position to DV's
  physics-LOD reference points (`DistanceLodSystemPatch`).
- **Track ballast everywhere (built):** every multiplayer server loads every Near scenery tile
  (`Server/Coverage/ServerWorldCoverage.cs`): the ballast along all the track, bridge decks
  and tunnel portals. Measured: 36 s in the background after a load, about +76 MB, no frame cost.
- **The derail freeze (built):** DV keeps a derailed car kinematic until its Near and Far tiles and
  terrain cell are all loaded. On a server it may also move where there's static ground under both
  bogies (`ServerWorldCoverage.MayMove`, `Server/Coverage/DerailedTrainCarIsKinematicHandlerPatch.cs`).
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
    most 40 extra: `Server/Coverage/ServerSceneryCoverage.cs` and
    `Server/Coverage/StreamerPatch.cs`, see
    [multi-anchor-streaming.md §1.4](multi-anchor-streaming.md#14-one-streamer-loading-the-union-of-boxes-feasible-two-patches).
    They render like any tile; whether that costs a player host anything is still to measure.
  - **Terrain (built):** every multiplayer server also loads terrain colliders around every remote
    player (DV's 5×5 box of 512 m cells) and every derailed car (the cells within 64 m of it):
    `Server/Coverage/ServerTerrainCoverage.cs`, `Server/Coverage/TerrainGridPatch.cs`, and
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
| 0 | **Identity by SteamID** ([Identity](#identity)): the Steam-derived player ID; tickets in the login packet, checked with `BeginAuthSession`; re-keying old install GUIDs on the first verified login; the reserved service ID; `Admins` and `-mp-owner` by SteamID64; `-mp-allow-unverified` for the harness. Steps 2-3 then use the player ID instead of `Settings.GetGuid()`. | Medium |
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

Step 0 needs scenarios for the identity itself, on a host without `-mp-allow-unverified`. Those
need a second Steam account, so most of it can only be checked by hand:
- a verified login keys the player by their Steam ID;
- a bad or missing ticket is refused;
- a save keyed by an old install GUID re-keys once on the first verified login.

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
