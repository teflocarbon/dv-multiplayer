# Architecture overview

Start here. This page explains how the mod is put together, how host authority works end to end,
and how a player joins. The detailed design for each system lives in the documents listed under
[Where to go next](#where-to-go-next).

The code is the source of truth. File paths below are relative to the repository root.

## Projects

`Multiplayer.sln` contains these projects:

| Project | Purpose |
|---|---|
| `Multiplayer/` | The Unity Mod Manager mod (net48). Harmony patches, networked components, managers, packets, transports, UI. Ships as `Multiplayer.dll`. |
| `Multiplayer.Core/` | netstandard2.0 library with no Unity or game references. Decision logic that can be unit tested: `Items/ItemAuthorityStateMachine.cs`, `Items/LostAndFound.cs`, `Containers/ColdContainerGraph.cs`, `Trains/TrainTopologyModel.cs`, `Identity/NetIdAllocator.cs`, interest and replication helpers. |
| `Multiplayer.Core.Tests/` | NUnit tests for `Multiplayer.Core` (net8.0). `dotnet test Multiplayer.Core.Tests`. |
| `Multiplayer.Protocol.Tests/` | Packet round-trip and fixture tests against the built `Multiplayer.dll` and the game's assemblies. `dotnet test Multiplayer.Protocol.Tests -c Release`. |
| `Multiplayer.DebugProtocol/` | Debug event contracts, event store, capture manager and the loopback HTTP/SSE server with its embedded web UI. Used by `Multiplayer/Debugging`. See [observability.md](observability.md). |
| `MultiplayerAPI/` | Public API (`MPAPI` namespace) for other mods: client/server/player interfaces, custom packets, NetId lookup. A post-build step runs `xmldoc2md` to generate [`docs/mpapi`](mpapi/multiplayerapi.md) and [`docs/index.md`](index.md). |
| `MultiplayerAPI Tests/` | A sample mod that exercises the API. |
| `DevTools/DvmpAgent/` | In-game agent mod for the dvmp harness (Debug only, never shipped). |

### Inside `Multiplayer/`

The folder (and namespace) says where code runs. See
[design/code-layout.md](design/code-layout.md):

| Folder | Namespace | What |
|---|---|---|
| `Shared/` | `Multiplayer.Shared.*` | What every game needs: the protocol (packets, data, serialisation), the transport, the session base and lifecycle, the networked components and their wiring, debugging, utilities |
| `Server/` | `Multiplayer.Server.*` | The authority: `NetworkServer` and its handlers, saves and per-player state, chat commands, world coverage, the dedicated host, and patches that run vanilla only on the server |
| `Client/` | `Multiplayer.Client.*` | A player's game: `NetworkClient`, the join, other players' avatars, UI, followers and presenters, and patches that send intent |
| `Menus/` | `Multiplayer.Menus.*` | The front end: main menu, hosting and joining, the server browser |
| root | `Multiplayer` | `Multiplayer.cs` (the UMM entry), `Settings`, `Locale` |

Inside each, folders are by system: `Session`, `Players`, `Trains`, `Items`, `World`, `Stations`,
`Jobs`, `Career`, and a few more. Server and Client don't reference each other, and Shared
references neither. A file that still refers to both roles sits in Shared until it's split.

Outside the solution:

| Path | Purpose |
|---|---|
| `DevTools/Dvmp/` | `dvmp.exe`, the CLI and MCP server that starts a host + client pair, runs C# inside the games, screenshots, reads logs, detects desyncs. See `DevTools/README.md`. |
| `DevTools/Scenarios/` | Two-game regression scenarios; `gate.sh` runs them all. |
| `Tools/ProtocolManifestGenerator/` | Built as a dependency of `Multiplayer`. Writes the protocol manifest after each build (see [Login](#1-login)). |
| `MultiplayerAssets/` | Unity project. Produces `MultiplayerEditor.dll`, `UnityChan.dll` and `multiplayer.assetbundle`, which go in `build/` (gitignored) and are referenced from there. Build them in Unity or copy them from a release; see [development.md](development.md). |
| `Lobby Servers/` | Server-browser backends (PHP and Rust) and `RestAPI.md`. |

There is no separate debug client any more: the old `Multiplayer.DebugClient` was removed and
two-game testing goes through dvmp.

## Runtime structure

### Entry point and lifecycle

`Multiplayer/Multiplayer.cs` is the UMM entry point. `Load` checks the MultiplayerAPI version,
runs `harmony.PatchAll()` (plus `SimComponent_Tick_Patch`, which is patched by hand), loads the
asset bundle, creates the session object (`MultiplayerSession.Create`) and registers the API provider.

The session object is a `DontDestroyOnLoad` game object with two parts.
`Multiplayer/MultiplayerSession.cs`, the composition root in the mod, owns the current
`NetworkServer` and `NetworkClient`: it starts and stops them, registers them with MPAPI, wires up
the dedicated host, the player list and the network stats. `Multiplayer/Shared/Session/NetworkLifecycle.cs`
runs the network tick and answers the role queries (`IsHost()`, `IsServerRunning`,
`IsDedicatedHost`, `IsProcessingPacket`, `LocalServer`, `LocalSession`), naming no role: the
root attaches the server and client to it as their Shared parts. Role code reaches its own role as
`NetworkServer.Instance` or `NetworkClient.Instance`; whether a `ServerPlayer` is the host's is
the server's `NetworkLifecycle.Instance.IsHost(player)` extension. Hosting calls `StartServer`,
which starts a `NetworkServer` and, unless this is a dedicated host, the host's own player: a
`LocalPlayerSession` (`Client/Session`), a session of the server in the same process. **The host
is also a player, but it runs no client**: the server admits its player as it starts
(`NetworkServer.AddLocalPlayer`, player 1, the same record binding as a login) and keeps it as
`NetworkServer.LocalPlayer`. Single-player games run the same way with a one-player limit. A remote
player's game runs a `NetworkClient` and joins over the network.

A **dedicated host** (`-mp-dedicated`, `Server/Dedicated/DedicatedHost.cs`) has no player at all:
no session, no avatar, no slot. Its console commands run as a console sender
(`ServerPlayer.CreateConsole`). Code that means "the host plays alone, so vanilla is right" uses
`NetworkLifecycle.IsHostPlayingAlone`, never `PlayerCount == 1`. See
[design/dedicated-host.md](design/dedicated-host.md).

The planned target is to separate the server from its players fully: the host's own player
stored and restored by GUID like everyone else's, and the world kept loaded around every player.
See [design/server-player-split.md](design/server-player-split.md).

To ask what this game is, use `NetworkRole` (`Shared/Session/NetworkRole.cs`):

- `IsServer`: this game is the authority (a player host, a dedicated host, or single player). The
  same as `NetworkLifecycle.Instance.IsHost()`, which most code still calls.
- `IsRemoteClient`: this game plays on another game's server. Never true on a host.
- `HasLocalPlayer`: someone plays at this keyboard (a client, or a host that isn't dedicated).

Server-side code must validate through `ServerPlayer`, never through the client's own idea of who
it is.

### The host's own player

The host's game is the server's world, so **the host's player never follows the host's state**.
A packet that carries the host's state for players to follow (a topology snapshot, a car spawn,
an item correction) would make the authority apply its own state to itself: a copy at best, an
old one at worst. On 2026-10-19 that split 29 trains in one session: each car the host spawned
asked for a train sync from the host's then loopback client, the reply was a snapshot from before
the spawn's auto-couple, and applying it uncoupled the new consist on the host; every client
followed.

Since [host-local-session](design/host-local-session.md) stage 3 there's no client on the host to do
that:

- **Requests** from the host's player go to the server in-process: `LocalPlayerSession.Submit`
  hands them to `ILocalServer.Submit`, handled at the server's next poll with the host's
  `ServerPlayer` as the sender, through the same work as the packet handlers. The host's own
  simulation (ports, fuses, topology it observes) is committed by the server directly.
- **What the server sends the host's player** goes to its `LocalPeer` (`Shared/Transport`), an
  in-process peer: every reply to `player.Peer` and every broadcast that doesn't exclude self. The
  session reads it a poll later with its own packet processor, which subscribes **only
  presentation**: the loading screen, chat, RPC answers, its own crew name, and what each area's
  `LocalPlayerSession.<Area>.cs` adds (control-authority updates, Lost and Found and container
  answers, MPAPI mods' packets). Anything else is dropped and logged once by type
  (`LocalPlayerSession.Drop`), so the server's state for its followers can't be applied to its own
  world.
- **Other players' avatars** (`NetworkLifecycle.Avatars`) are kept by the server itself, on a player
  host and a dedicated host alike; the host's player isn't sent the presence packets.

When adding a clientbound packet: if the host's player should show it, subscribe it in its area's
`LocalPlayerSession.<Area>.cs`; otherwise do nothing, and if the server sends it to the host's
player anyway, the session drops it.

### Managers

- `Shared/Session/NetworkManager.cs`: shared base. Owns the LiteNetLib `NetPacketProcessor`, the transport(s),
  `SendPacket` helpers and the receive path. A handler that throws is logged and swallowed so it
  can't abort the rest of the receive batch.
- `Server/Session/NetworkServer.cs`: login, loading states, every serverbound handler, broadcasts.
  `ServerPlayer` (`Server/Session/ServerPlayer.cs`) holds per-player server state: loading
  state, position, known/nearby items, shop escrow, held train traffic, a server-side inventory.
- `Client/Session/NetworkClient.cs`: login response, the join coroutine (`SyncWorldState`), every
  clientbound handler. Observer mode (`Settings.ObserverMode`) blocks the mutation packets in
  `ObserverMutationPacketTypes` at the send boundary.
- `Server/Chat/ChatManager.cs`, `Server/Session/LobbyServerManager.cs` (public server listing),
  `Menus/ServerBrowser/ServerBrowserClient.cs`, `Shared/Players/PlayerAvatars.cs` (other players' avatars, `NetworkLifecycle.Avatars`).

### Transports

`Multiplayer/Shared/Transport/`. `ITransport` abstracts the socket library.

- `SteamworksTransport.cs` is the transport in use (`NetworkManager` constructs it). A server
  opens both a normal UDP socket on the configured port and a Steam relay socket. A client
  connects natively by address and port, or through the relay by SteamID (port < 0). The login
  packet travels as the connection request payload.
- Delivery methods are LiteNetLib's enum, mapped onto Steam send types in `SteamPeer.Send`:
  `ReliableOrdered`, `ReliableUnordered` and `ReliableSequenced` become `Reliable`;
  `Unreliable` and `Sequenced` become `Unreliable`. So **`Sequenced` gives no ordering on
  Steam**; anything sent that way must tolerate reordering (train and player samples carry ticks).
- `LiteNetLibTransport.cs` is an alternative transport that nothing uses at the moment (the
  `new LiteNetLibTransport()` line in `NetworkManager` is commented out).

### Packets

Packets live in `Multiplayer/Shared/Protocol/Packets/` and are named by direction:

| Prefix | Direction |
|---|---|
| `Clientbound*` | host to client |
| `Serverbound*` | client to host (requests and intent) |
| `Common*` | both ways, usually a client request that the host relays or echoes |
| `Unconnected*` | server browser pings |

They are plain classes serialized by `NetPacketProcessor`; custom field serializers are
registered in `Shared/Protocol/Serialization/PacketSerializationRegistry.cs`. Handlers are subscribed
in `NetworkServer.Subscribe()` and `NetworkClient.Subscribe()`. Mods add packets through
`RegisterExternalPacket` via MPAPI.

### Networked components and NetIds

`Multiplayer/Shared/{Trains,Items,World,Stations,Jobs,Players}` hold the `Networked*` components that
Harmony patches attach to game objects (for example `Junction_Awake_Patch` adds
`NetworkedJunction`). Most derive from `Shared/Session/IdMonoBehaviour.cs`, which gives each a
`NetId` from a per-type `NetIdAllocator` (`Multiplayer.Core/Identity`).

- `IsIdServerAuthoritative => true` (`NetworkedTrainCar`, `NetworkedItem`, `NetworkedJob`,
  `NetworkedTask`): only the host allocates; clients get the id in a spawn or create packet.
- `false` (junctions, turntables, rail tracks, stations, pit stops, cash registers, pluggables,
  warehouse controllers): every peer allocates locally as the scene loads, which relies on the
  same objects waking in the same order on every peer. For tracks, junctions and turntables the
  host checks this at join ([design/track-graph-check.md](design/track-graph-check.md)).

`Multiplayer/Shared/Api/NetIdProvider.cs` maps game types (`TrainCar`, `Junction`, `Job`, `ItemBase`, ...)
to their NetId lookups for MPAPI users.

### Harmony patches

Patches sit next to the code they serve, in the role they belong to: `Server/` for authority logic
and "only the server runs this vanilla code", `Client/` for sending intent and holding a client's
copy, `Shared/` for wiring components and behaviour the same everywhere. Every patch is applied in
every game and checks its role itself (mostly `IsHost()` today, `NetworkRole` from now on). The
common shapes:

- **Client prefix sends intent and skips vanilla.** On a client the prefix sends a
  `Serverbound*` request and returns `false`, so the local game doesn't commit the change.
  Example: `CommsRadioCarDeleterPatch.OnUse_Prefix` sends `ServerboundTrainDeleteRequestPacket`.
- **Host postfix (or event hook) broadcasts.** When the change happens on the host, through its
  own player or a handled request, a postfix or a subscribed game event sends the result to
  everyone.
- **Client prefix suppresses simulation the host owns.** `SimComponent_Tick_Patch` stops all
  locomotive simulation on clients; `HazmatTileManagerPatch` stops tile evolution;
  `TrainCarExplosionForcePatch` stops explosion forces; `SaveGameManager.SaveAllowed` is false.
- **Apply flags.** While a client applies host state it sets a flag (for example
  `ExplosionPlayback.ApplyingHostExplosion`, `HazmatClient.ApplyingHostTiles`) so its own patches let
  the vanilla code through instead of turning it back into a request.

## Host authority

The host's game is the only place shared state is decided. The rules:

1. **Clients send intent, not results.** A client asks to couple, buy, ignite, pick up. It may
   play local presentation (a sound, an animation) but doesn't commit shared state.
2. **The host validates.** It resolves the sender from the transport peer (never from a field in
   the packet), then checks what applies: range to the target, ownership or placement of an
   item, game settings such as comms-radio cheat mode, escrowed money, revision preconditions.
3. **The host commits** by changing its own game objects or canonical records.
4. **The host broadcasts canonical state** to everyone, usually including the requester.
   A rejection is answered with the canonical state, so it doubles as a correction.
5. **Clients apply** what they receive, and **never simulate what the host owns** (locomotive
   simulation, hazmat tiles, explosions, cargo reactions).

```text
 client                               host                                   other clients
 ------                               ----                                   -------------
 player acts
 prefix: send Serverbound* intent ──▶ resolve ServerPlayer from peer
 (vanilla skipped; maybe a sound)     validate (range, owner, settings, money, revision)
                                       ├─ reject ─▶ canonical state back to sender
                                       └─ commit on host game objects / records
 apply canonical state ◀────────────── broadcast Clientbound* ─────────────────▶ apply
```

Concrete cases:

- **Items.** `Server/Items/AuthoritativeItemRegistry.cs` holds one host record per
  item: placement, holder, persistent owner, inventory claim, revision. A client's
  `CommonItemUpdatePacket` goes through `NetworkedItem.Server_ReceiveItemUpdate` and
  `AuthoritativeItemRegistry.TryApplyTransition`, which runs `ItemAuthorityStateMachine` from
  `Multiplayer.Core`. An item update can't create an item: the server makes every item it
  keeps, and client item adoption is gone (owned-items PR 5). A rejected change is answered with a
  canonical correction. Physics of a thrown or dropped item may be leased to one client, which
  sends samples and a settlement the host validates. See
  [design/item-lifecycle.md](design/item-lifecycle.md) and
  [design/item-spatial-physics.md](design/item-spatial-physics.md).
- **Train topology.** `ServerboundTrainTopologyCommandPacket` (couple, uncouple, hoses, cocks,
  MU) is checked for range (the player within 8 m of the primary car, measured to the line
  between its couplers, unless using a remote; and the two couplers actually close), executed by
  `TrainTopologyAuthority`, and the
  resulting `ClientboundTrainTopologySnapshotPacket` with its revision is broadcast; a rejection
  sends the snapshot back to the requester only. See [design/trains.md](design/trains.md).
- **Shops and money.** Money is the host's wallet; `MoneyChanged` on the host broadcasts the
  balance. Cash a client puts into a shop register arrives as `ServerboundShopPaymentPacket` and
  is held in `ServerPlayer.ShopCashInRegisters`. `ServerboundShopPurchasePacket` is accepted only
  if that escrow covers the price, the item is in stock and the player is within 50 m; otherwise
  the escrow is refunded and the stock resent. The host spawns the purchase owned by the buyer.
  Depositing a money item is refused if the note is in a container, the Lost and Found, or
  someone else's hands.
- **Explosions and hazmat.** `Shared/Trains/ExplosionSync.cs`: clients never start an explosion
  or apply its force; the host sends which car exploded and clients play effects only, while the
  resulting train motion arrives through train sync. `Server/World/HazmatServer.cs`:
  the host sends changed hazmat tiles every half second; a client lighting a tile sends
  `ServerboundHazmatIgnitePacket`, accepted within 40 m of the player.

Not everything is validated yet. Some `Common*` packets are still relayed as received; for
example `HandleJunction` applies and rebroadcasts junction changes without checks. Check the
handler before assuming a path is authoritative.

## Join flow

### 1. Login

A client connects with `ServerboundClientLoginPacket` as the connection payload.
`NetworkServer.OnServerboundClientLoginPacket` checks, in order:

1. the GUID parses;
2. the password matches `Settings.Password`;
3. the game build matches the host's (`BuildVersionString`);
4. if the same GUID still has an open session (a crash-reconnect before the old one timed out),
   that session is retired first so it can't later unbind the new session's items;
5. the username is made unique;
6. the server isn't full;
7. the client's mod list passes `ModCompatibilityManager.ValidateClientMods`.

Failures reject the request with a `ClientboundLoginResponsePacket` carrying a locale key (and
missing/extra mods). On accept the host creates the `ServerPlayer`, rebinds items the player
persistently owns (`AuthoritativeItemRegistry.RebindPersistentOwner`, plus Lost and Found and
saved world items waiting for this owner), and replies with the player id and its **protocol
fingerprint**.

The fingerprint comes from `Tools/ProtocolManifestGenerator`, which runs after every build of
`Multiplayer`, hashes the packet list, the protocol enums and the build version (including the
commit), writes `protocol-manifest.json` and embeds it in `Multiplayer.dll` in a second build
pass (`ProtocolManifestProvider`). The **client** compares the host's fingerprint with its own
and disconnects on any difference, so host and client must run the exact same build.

### 2. Loading states

After login the client walks `PlayerLoadingState` (`Shared/Protocol/Data/PlayerLoadingState.cs`) by
sending `ServerboundLoadStateUpdatePacket`; the server answers each step in
`OnServerboundLoadStateUpdatePacket`. A client that reports a state at or below its current one
is kicked. Broadcasts carry a minimum loading state, so a player only receives live traffic for a
system once it has that system's baseline.

The host's own player has no join: `AddLocalPlayer` takes it straight to `ReadyForWorldState`
(held in the join queue until the save has loaded), which the server treats as `Complete`. See
[design/host-local-session.md](design/host-local-session.md).

```mermaid
sequenceDiagram
    participant C as Client
    participant H as Host
    C->>H: ReadyForGameData
    Note over H: held in gameDataQueue until the host's save has loaded
    H->>C: GameParams, SaveGameData (money, licences, garages, position, starter kit on first join)
    Note over C: load scene, quarantine local items, send item catalogue, wait for mods
    C->>H: ReadyForWorldState (+ track-graph hash)
    Note over H: joinQueue + ServerLoading if not loaded; kick on a track-graph mismatch; else peer joins broadcasts
    H->>C: weather, sleep state, shop stock, junctions/turntables, switches, customisation holes
    C->>H: ReadyForTrainSets
    H->>C: BaselineBegin, SpawnTrainSet x N, topology snapshot, BaselineComplete
    Note over H: live train traffic held for this player
    C->>H: ReadyForItems (after baseline + topology applied)
    Note over H: held train traffic flushed; item streaming starts on the item tick
    C->>H: ReadyForJobs
    H->>C: active jobs per station
    C->>H: ReadyForTiles
    H->>C: all hazmat tiles
    C->>H: Complete
    H->>C: existing players, RemoveLoadingScreen
    H-->>C: (others) PlayerJoined
```

Notes on the steps:

- **Game data waits for the host.** Before the host's save is loaded its difficulty is still the
  defaults, so `ReadyForGameData` requests are queued in `gameDataQueue` and answered from
  `NetworkServer.OnLoaded`. `ClientboundSaveGameDataPacket.CreatePacket` reads the player's saved
  position; a GUID with no saved player data gets a starter kit once per host session
  (`StarterKitsGiven`). The client builds `StartGameData_ServerSave` from it and loads the scene;
  clients never load their own save.
- **World state** runs from `NetworkClient.SyncWorldState` once `WorldStreamingInit.LoadingFinished`
  fires. It times out with a join failure if junction/turntable state doesn't arrive.
- **Train baseline.** The host serialises every trainset first and rejects the whole baseline if
  any can't be represented. It doesn't advance the player's loading state; reliable train
  packets broadcast meanwhile are stored in `ServerPlayer.PendingTrainTraffic` and delivered when
  the client moves past `ReadyForTrainSets`. The client waits up to 45 s for the baseline and the
  canonical topology, then disconnects with a reason if either is missing.
- **Items** have no snapshot step: from `ReadyForItems` the item manager's tick creates items as
  they enter the player's interest. A rejoining player's inventory comes back the same way: the
  host kept the records, and they're bound to the player at login.
- **Loading screen.** `DisplayLoadingInfoPatch` unhooks the loading screen from
  `WorldStreamingInit.LoadingFinished`, so it stays up through the sync. `RemoveLoadingScreen` takes
  it down, and the client then makes sure it has left loading mode
  (`LoadingScreenManager.FinishLoading()`), even if the loading screen object is gone. In VR that's
  what turns the gloves and the teleport pointer back on (#72). The VR loading view (`VrLoading`)
  fades into the world as soon as its progress reaches 1, and the client reports 100% before the
  sync is done (on "Complete", with the host's items and jobs still arriving, and when a mod clears
  its ready block). `VrLoadingProgressPatch` holds that progress below 1, so only
  `FinishLoading()` ends it, in the frame the loading screen and its camera are destroyed. Before,
  a VR player was in the world while the desktop still showed the loading screen, with one eye
  upside down.
- `ReadyForCustomizers` exists in the enum but the client never sends it. The client waits a
  fixed 0.25–0.5 s between the later states; there is no completion acknowledgement for items.

## Tick and sync rates

`NetworkLifecycle.PollEvents` is a coroutine running at `TICK_RATE = 24`. Each iteration it
advances `Tick`, invokes every `OnTick` subscriber separately (one throwing doesn't stop the
others), then polls the client and server transports. Packets are therefore received and
handled at tick rate, not every frame.

Below 24 fps a frame covers several tick intervals, so `Tick` advances by the number of
intervals that elapsed (up to 48 at once) to stay in step with real time; after a longer stall
such as loading it carries on from the current time. Sample ages and timeouts are counted in
ticks, so this matters for train and item smoothing. Clients realign to the host: whenever the
host gets a latency update for a peer (about once a second on Steam) it sends
`ClientboundTickSyncPacket`, and the client sets its tick to the host's plus the one-way latency.

What is sent when (host side):

| System | Where | Rate |
|---|---|---|
| Train motion | `Shared/Trains/NetworkTrainsetWatcher.cs` | Each tick, one `ClientboundTrainsetPhysicsPacket` per trainset that has a moving car, or whose cars haven't synced for 2 s (a full sync with position and rotation). Skipped while teleporting. Reliable if bogie tracks changed, otherwise unreliable. Speed is scaled by the host's measured simulation rate so clients don't run ahead of a slow host. |
| Train topology | `NetworkServer.OnTick` | Observed host-side changes committed once per tick and broadcast if the revision changed. |
| Brakes | `NetworkedTrainCar` | Thresholded with a heartbeat; see [design/trains.md](design/trains.md). |
| Items | `NetworkedItemManager` | Per tick, per ready player, for items in interest. |
| Hazmat tiles | `HazmatServer.Tick` | Every 12 ticks (0.5 s), changed tiles only. |
| Weather | `NetworkServer.OnTick` | Every 30 s, plus on join. |
| Player position | `LocalPlayerTrackerBase` (client) | From `OnTick`, relayed by the host to complete players as `Sequenced`. |

Clients interpolate train motion (`TrainSpeedQueue`, `TickedQueue`) and run no locomotive
simulation.

## Save, disconnect and rejoin

Only the host saves; `SaveGameManager.SaveAllowed` returns false on clients. A postfix on
`SaveGameManager.UpdateInternalData` calls `NetworkedSaveGameManager.Server_UpdateInternalData`,
which writes a `Multiplayer` section into the host's save:

- `Players.<guid>`: position, rotation and server-side inventory of each remote player that has
  finished loading;
- Lost and Found, cold container contents, and world item records (`WorldItemsV1`).

On load, items saved in a player's hand or inventory wait until that player connects
(`WorldItemPersistenceManager.OnOwnerConnected`).

When a player disconnects (`NetworkServer.OnPeerDisconnected`) the host, with each step guarded
so one failure doesn't skip the rest: cancels their pending item recalls, releases item physics
leases, drops what they held and borrowed inventory items owned by others, clears their train
control operations, updates the in-memory save with their data, removes them, tells the others,
and runs `PlayerDisconnected` subscribers (cars release control leases there). Their own items
stay as host records and are rebound on rejoin.

A client that crashes and reconnects before its old connection times out is matched by GUID at
login and the stale session is retired first (see [Login](#1-login)). When the host stops it
sends `ClientboundDisconnectPacket` to every remote peer; a client that loses its connection
stops networking and returns to the main menu.

The regression scenarios for this are `rejoin` (including `CRASH=1`), `save-reload`,
`inventory-persist` and `borrowed-disconnect` in `DevTools/Scenarios`.

## Where to go next

| Document | What it covers |
|---|---|
| [world-sync.md](world-sync.md) | World state outside trains and items: junctions, turntables, weather, time and sleep, shops, hazmat, explosions. |
| [jobs.md](jobs.md) | Job generation, booklets, validation and reports. |
| [design/trains.md](design/trains.md) | Train authority layers, topology commands and snapshots, controls, brakes, motion, baseline. |
| [design/train-item-wake.md](design/train-item-wake.md) | Loose items riding on trains: gluing, waking on impacts and acceleration, settlement. |
| [design/item-lifecycle.md](design/item-lifecycle.md) | Start here for items: an item's life, its placements, the state machine and shared rules, and the doc for each item system. |
| [design/owned-items.md](design/owned-items.md) | Ownership and the owned-items ledger, starting items, joining and leaving, recall, the owned-items list and retrieval. |
| [design/lost-and-found.md](design/lost-and-found.md) | Lost and Found: collection, entries, retrieval, saving. |
| [design/item-world-sync.md](design/item-world-sync.md) | Item catalogue, interest streaming, projection to clients, placement anchors, office stock. |
| [design/item-spatial-physics.md](design/item-spatial-physics.md) | Thrown and dropped item physics: leases, samples, settlement, validation. |
| [design/container-storage.md](design/container-storage.md) | Cold containers: contents as server records, browse, deposit, withdraw. |
| [design/item-persistence.md](design/item-persistence.md) | Where each item is saved, and loading. |
| [design/item-ownership-open-questions.md](design/item-ownership-open-questions.md) | The ownership questions still open. |
| [design/item-sync-review.md](design/item-sync-review.md) | Historical: the July 2026 review of item sync. |
| [testing.md](testing.md) | Unit, protocol and in-game (dvmp) test layers. |
| [observability.md](observability.md) | Debug events, overlay, packet inspector, HTTP firehose, captures. |
| [development.md](development.md) | Building, deploying and the development workflow. |
| [research/](research/) | Notes on vanilla Derail Valley behaviour (items, storage, trains, jobs, UI) from the decompiled game. |
| [mpapi/](mpapi/multiplayerapi.md) | Generated MultiplayerAPI reference. |
