# Code layout: server, client and what they share

> Status: proposed (2026-10-19), not started. Today everything that isn't pure logic is in one
> project, `Multiplayer`, and nothing in its layout says whether code runs on the server, on a
> player's game, or both. This plans a split into `Core`, `Shared`, `Server` and `Client`, done
> first as folders and namespaces checked by a test, and only then as assemblies.
>
> It goes with [host-local-session.md](host-local-session.md). That removes the host's loopback
> client, which is the biggest thing tying server code to client code today. This doc is about
> where the code lives.
>
> Counts and line numbers are from `beta` at `6da65843` (2026-10-19).

## Why

- **Nobody can tell what runs where.** `NetworkedTrainCar` is 3,411 lines with 31 `Server_`, 34
  `Client_` and 19 `Common_` methods in one class. `IsHost()` is called about 300 times in 107
  files, 59 of them Harmony patches, and in most of them the role is a branch in the middle of a
  method.
- **The roles leak into each other.** The server's own identity is the client's player id
  (`NetworkServer.SelfId`, `Server/Session/NetworkServer.cs:118`). "Am I applying a
  remote change" is the client's flag even on the server (`NetworkLifecycle.IsProcessingPacket`,
  `Shared/Session/NetworkLifecycle.cs:45`). The bug of 2026-10-19 (the host following its
  own topology snapshot) came from exactly that: server state flowing through client code.
- **The rules that are separate are hard to find.** `Multiplayer.Core` holds the logic that can be
  unit tested without the game, but its name says "shared" and the folder layout of `Multiplayer`
  doesn't point to it.

## The roles

| Role | Runs | Holds |
|---|---|---|
| **Core** | Everywhere; no Unity or game references | The rules: decisions that can be unit tested. Exists today. |
| **Shared** | Everywhere, with Unity and the game | The protocol (packets, data, serialisation), the transport, NetIds and the networked component shells, identity types, debugging, utilities, and the interfaces between the roles |
| **Server** | Where the world is authoritative: a player host or a dedicated host | Handlers, validation and authority; applying changes to the world and broadcasting them; persistence and per-player state; world coverage; chat commands and admin; the dedicated host |
| **Client** | Where someone plays: a client's game, and the host's local player once [host-local-session](host-local-session.md) stage 3 is built | Sending intent; followers, playout and prediction; other players' avatars; chat and player UI; the join flow |
| **Mod** (`Multiplayer`, the entry) | Everywhere | `Load`, settings, locale, the main menu (host and join), the lifecycle that starts the roles, MPAPI registration |

**The rule:** Server and Client never reference each other, and Shared and Core reference neither.
Everything that needs both goes through an interface in Shared, implemented by one role and used
by the other. The host loads both roles; a client's game loads both assemblies but only starts the
client.

**Two senses of "client".** Client code runs in two places, and patches must say which they mean:

- **Remote client only:** a game that isn't the authority. "Send intent and skip vanilla" patches,
  followers, correction and prediction. These must never run on the host.
- **Any local player:** presentation. Avatars of other players, chat, the player list, the career
  and container UI. These run on the host too.

A small `NetworkRole` in Shared replaces the role half of today's `IsHost()`: `IsServer` (this
game is the authority), `HasLocalPlayer` (someone plays here), `IsRemoteClient` (playing on
someone else's server). `IsHost()` keeps meaning `IsServer` until its callers move. Built:
`Shared/Session/NetworkRole.cs` (it moves to Shared with the rest).

### Assemblies

| Assembly | References | Notes |
|---|---|---|
| `Multiplayer.Core` | nothing | Already a project, netstandard2.0, shipped as `Multiplayer.Core.dll` (`Multiplayer/Multiplayer.csproj:164`) |
| `Multiplayer.Shared` | Core, MPAPI, DebugProtocol, the game | net48 |
| `Multiplayer.Server` | Shared, Core | net48 |
| `Multiplayer.Client` | Shared, Core | net48 |
| `Multiplayer` | all of the above | The UMM entry (`info.json` `EntryMethod`), and still `Multiplayer.dll` |

**Keep the name `Multiplayer.Core`?** It's what that project is called in tests, docs and the
build. Renaming it (`Multiplayer.Rules`) would make its meaning plainer but touches all of those
for no behaviour. Keep it, and say what it is in its own README and in
[architecture.md](../architecture.md#projects): the rules, with no Unity references.

## What `Multiplayer.Core` holds today

| Folder | What |
|---|---|
| `Items/` (22 files) | Item authority state machine, adoption, change reasons, Lost and Found rules, essential claim slots, belt items, release sync, inventory claims and presentation, snapshot application and wire projection, spatial stream validation, train-item wake planning, page books, storage transitions |
| `Containers/` | Cold-container graph and its save codec |
| `Identity/` | NetId allocation |
| `Replication/` | Interest membership |
| `Trains/` | Train topology model, baseline receipt, control-port writers, playout clock, delay and track, change reasons, handcar pumpers, the unused-car delete policy |
| `Jobs/` | Job booklet issuance |
| `Collections/` | Pending-snapshot queue and its drain planner |
| `Diagnostics/` | `ChangeCause`: why something changed, shared by trains and items |

All of it has tests in `Multiplayer.Core.Tests`. Moving more decisions here, out of the big
components, is the other half of making them small.

## What `Multiplayer` holds today

471 files, 67,500 lines. A first pass sorted each file by whether it refers to server things
(`NetworkLifecycle.Instance.Server`, `Server_` methods, `ServerPlayer`, `NetworkServer`) or client
things (`NetworkLifecycle.Instance.Client`, `Client_` methods, `NetworkClient`,
`ClientPlayerManager`), and whether it branches on the role:

| Pass | Files | Lines | Meaning |
|---|---|---|---|
| Neither, no role branch | 306 | 22,500 | Mostly Shared (124 of them are packets) or the mod's front end |
| Server only | 56 | 13,900 | Server, give or take a file |
| Client only | 39 | 6,900 | Client, but many are patches that also run on the host (the host sends through its own client today) |
| Branches on the role, no references | 45 | 4,200 | Mostly patches: split into a server patch and a client patch |
| Both | 25 | 20,100 | Has to be split before it can move |

This is a keyword count, not a review: it puts files in the right neighbourhood, and the move
itself checks each one.

### Folder by folder

Paths are under `Multiplayer/`.

| Today | Goes to | Notes |
|---|---|---|
| `Multiplayer.cs`, `Settings.cs` | Mod | `Settings` mixes the player's options with the server's (`[Header("Server")]`, `Admins`, `Settings.cs:52-67`); later, a server-settings section the Server reads through an interface |
| `Locale.cs`, `ModCompatibility/`, `Models/` | Shared (locale, mod lists), Client (player models) | |
| `API/` | `APIProvider`, `ModCompatibilityManager`: Mod. `ServerAPIProvider`, `ServerPlayerWrapper`: Server. `ClientAPIProvider`, `ClientPlayerWrapper`: Client. `NetIdProvider`, `ExternalSerializablePacketWrapper`: Shared | |
| `Shared/Session/IdMonoBehaviour.cs`, `TrainComponentLookup.cs` | Shared | |
| `Components/MainMenu/`, `Components/UI/{GridView,ServerBrowser,Settings}/`, `Components/Util/` | Mod | The front end: host and join |
| `Components/UI/CareerManager/` | Client | |
| `Shared/Session/NetworkLifecycle.cs` | Mod, with the tick clock in Shared | It owns the server and the client; everything reads `Tick` and `OnTick` |
| `Components/Networking/{DedicatedHost,DedicatedHostAnchor,DedicatedHostSaveLock,Server*Coverage}.cs` | Server | |
| `Components/Networking/{RpcManager,TickedQueue}.cs` | Client (`RpcManager` issues and resolves tickets), Shared | |
| `Components/Networking/Jobs/` | `JobBookletSummonCoordinator`, `NetworkedTask`: Server. `NetworkedJob`: split. Lookups: Shared | |
| `Components/Networking/Player/` | Client: trackers, `NetworkedWorldMap`. **`NetworkedPlayer`: see Decisions** | |
| `Components/Networking/Train/` | `TrainTopologyAuthority`, `NetworkedLocoRestorationController`: Server. `TrainPortPresenter`, `TrainTopologyReconciler`, `NetworkedCarSpawner`: Client. Lookups, `TrainTopologyNativeAdapter`, `NetworkedBogie`, `NetworkedRigidbody`: Shared. **`NetworkedTrainCar`, `NetworkTrainsetWatcher`, `TrainSpeedQueue`, `TrainChangeCauses`: split** | |
| `Components/Networking/UI/` | Client (`NetworkStatsGui`: Shared) | |
| `Components/Networking/World/` (items) | `AuthoritativeItemRegistry`, `NetworkedItem.Server`, `NetworkedItemManager.{ServerInterest,Convergence}`, `NetworkedLostAndFoundManager`, `OwnedItemsSummary`, `Containers/NetworkedColdContainerManager`, `WorldItems/{Persistence*,AuthoredWorldItemReplenishmentManager}`: Server. `NetworkedItem.{StateApplication,VrHand}`, `NetworkedItemManager.ClientProjection`: Client. Identities and records: Shared. **`NetworkedItem`, `NetworkedItem.Snapshots`, `NetworkedItemManager.{ProjectionAndCompatibility,WorldIntegration,FallRescue}`, `WorldItems/{NetworkedItemSpatialManager,NetworkedTrainItemWakeManager}`: split** | Items are already split into role partials; the split here is finishing that |
| `Components/Networking/World/` (stations and world) | `NetworkedJunction`, `NetworkedTurntable`: Client today, Server after [host-local-session](host-local-session.md) stage 2 makes the server apply them. **`NetworkedPitStopStation`, `NetworkedPluggableObject`, `NetworkedCashRegisterWithModules`, `NetworkedGenericSwitch`, `NetworkedStationController`, `HazmatSync`, `NetworkedPageBookState`: split** | Each has its server processing (`ProcessInteractionPacketAsHost`, `Server_Process*`) next to its client side |
| `Components/SaveGame/` | `NetworkedSaveGameManager`, `InstallGuidRekey`, `PerPlayerSaveFormat`, `NetworkedCareerManagerDebtController`: Server. `Client_GameSession`, `StartGameData_ServerSave`: Client | |
| `Debugging/` | Shared (`DebugDiagnostics`' server half: Server) | |
| `Integrations/` | `ColdContainerUiIntegration`: Client. `InventoryIntegration`: split. Seasons, storage: Shared | |
| `Networking/Data/`, `Networking/Packets/`, `Networking/Serialization/`, `Networking/TransportLayers/` | Shared, except `ServerPlayer.cs`: Server | `PlayerIdentity` is used by both (login and authentication): Shared, once its server reference goes |
| `Shared/Session/NetworkManager.cs` | Shared | |
| `Networking/Managers/Server/` | Server | `NetworkServer.cs` (3,934 lines) could also split by area (trains, items, world, career) while it moves |
| `Networking/Managers/Client/` | Client | `NetworkClient.cs` (3,079 lines), likewise |
| `Patches/` | Per file: see Patches | |
| `Utils/` | Shared. `DvExtensions`: split (`AnyPlayerSqrMag` is the server's) | |

### Patches

The 127 patch files fall into four kinds:

- **Server patches.** Vanilla behaviour the authority changes or observes: job generation range,
  car spawning and deleting, save hooks, world coverage, explosions. Gate on `IsServer`.
- **Remote-client patches.** "Send intent and skip vanilla" (the comms radio on a client, the
  career manager's fees and licence screens, coal and ignite forwards), and holding a client's
  copy to the host's state. Gate on `IsRemoteClient`.
- **Presentation patches.** Loading screen, pause menu, key bindings, UI. Gate on
  `HasLocalPlayer` or nothing.
- **Both, in one method.** Today's usual shape: `if (IsHost()) { ... } else { send }`. Split it
  into a server patch and a remote-client patch on the same vanilla method. Harmony runs both, and
  each returns early when it isn't its role.

Patches that send through the host's own client today (`TimeAdvancePatch`, the radio's delete,
rerail and work-train patches, `CashRegisterBasePatch`, the coupler patches) become a
remote-client patch plus a server patch that calls the server directly, which is
[host-local-session](host-local-session.md) stage 2.

## Decisions to make

- **Other players' avatars are the server's too.** On the host, a remote player's
  `NetworkedPlayer` isn't only presentation: items in their hands are attached to it, and server
  logic counts who is on a car (`NetworkedTrainCar.HasPlayers`) or holding a plug through it (see
  [dedicated-host.md, Keep the loopback client](dedicated-host.md#keep-the-loopback-client-it-isnt-only-a-player)).
  Proposal: a player's **presence** in the world (body, hands, the car they're on) is Shared and
  owned by the server where there is one; the model, name tag and world-map marker are Client.
- **The lifecycle.** `NetworkLifecycle` owns both roles and the tick, and about everything reads
  it. It stays in the mod as the composition root; Shared gets the tick clock and `NetworkRole`;
  the roles reach each other only through interfaces it hands them.
- **One `Settings` or two.** Server settings (name, visibility, port, admins, password) could be
  their own class the Server reads. Not needed for the boundary; worth doing when it moves.

## Mechanics of splitting assemblies

- **Partial classes can't span assemblies.** A role-split partial (`NetworkedItem.Server.cs`) is
  fine while everything is one project. Before assemblies, each split component becomes a shell in
  Shared (NetId, lookups, events, the native adapter) with a server part and a client part
  attached to the same game object. That's the real work for `NetworkedTrainCar` and the station
  components.
- **Harmony.** `harmony.PatchAll()` patches only the calling assembly (`Multiplayer.cs:115`); the
  entry calls it once per role assembly.
- **The build.** The post-build copy list (`Multiplayer.csproj:164`) gains the new DLLs, and the
  release zip and the dvmp deploy follow it.
- **The protocol manifest.** `Tools/ProtocolManifestGenerator/Program.cs:21` loads one assembly to
  hash the packet list; it loads Shared, where the packets are, and the fingerprint is embedded
  where `ProtocolManifestProvider` lives.
- **Tests and tooling.** `Multiplayer.Protocol.Tests` references the built `Multiplayer.dll` by
  path; it references Shared (and Server and Client where a test needs them). `DevTools/DvmpAgent`
  reads the mod by reflection, and 73 files under `DevTools/` name `Multiplayer.*` namespaces
  (scenario scripts, probes): the namespace move updates them in the same commit.
- **MPAPI** is its own project and unaffected. Mods that reach into `Multiplayer.dll` by
  reflection, not through MPAPI, would break on a namespace change; there's no way to find those
  but to look at the mods players use.

## The boundary test

Before any assembly exists, a test in `Multiplayer.Protocol.Tests` reads the built
`Multiplayer.dll` with Mono.Cecil and checks every type in the role namespaces:

- `Multiplayer.Server.*` refers to nothing in `Multiplayer.Client.*`, and the reverse;
- `Multiplayer.Shared.*` refers to neither.

Violations that exist when the namespaces move go in an allow-list file, one line per
type-to-type reference. The test fails on a new violation and on an allow-list line that no longer
happens (so the list only shrinks). The list's length is the measure of how far the split has to
go, and moving to assemblies is mechanical once it's empty.

Built as `Multiplayer.Protocol.Tests/CodeLayoutTests.cs`, two tests:

- **`EveryFileIsInARoleFolderWithItsNamespace`** reads the source: every file under
  `Multiplayer/` is in `Shared/`, `Server/`, `Client/` or `Menus/` (or is `Multiplayer.cs`,
  `Settings.cs` or `Locale.cs` at the root) and declares the namespace of its folder.
- **`RolesOnlyReferenceWhatTheyMay`** reads the built DLL: base types, interfaces, fields,
  properties, events, method signatures, locals, every type, method and field an instruction
  names, and `typeof` in attributes, each counted against the outermost type. Server ↛ Client,
  Client ↛ Server, Shared ↛ either, and nothing but the root and `Menus` ↛ `Menus`. The root
  namespace may be referenced by anything for now (it moves into Shared when the entry point is
  split from the logging and settings it holds).

The allow-list is `Multiplayer.Protocol.Tests/CodeLayout/role-references.allowed.txt`. After a
split removes references, delete their lines, or rewrite the file with
`MULTIPLAYER_WRITE_ROLE_ALLOWLIST=1 dotnet test Multiplayer.Protocol.Tests` and check the diff
only removes lines. Both tests run in CI's required "Build and protocol tests" job.

## Steps

1. **Land what's in flight.** The release branch and the open fix branches; a namespace move
   conflicts with every edit to a moved file that isn't merged yet. Git follows renames across a
   merge when the file is still mostly the same, which a move plus a namespace line keeps, but
   fewer branches open means fewer surprises.
2. **`NetworkRole`**, built on `IsHost()` for now (the other way round once its callers move), and
   replacing the hand-copied `IsClient` helpers. Small, no behaviour change.
3. **The move.** One PR, two commits:
   1. move files into `Multiplayer/Shared/`, `Multiplayer/Server/`, `Multiplayer/Client/` and the
      mod's root, change namespaces to match, update `DevTools/`; no logic changes. Files that
      need splitting go where most of their code belongs.
   2. the boundary test and its allow-list.
4. **Split, one area per PR,** shrinking the allow-list: [host-local-session](host-local-session.md)
   stage 2 (the host's requests and simulation commits), then trains (`NetworkedTrainCar` into
   shell, server and client), items (finish the role partials), stations, patches.
5. **Assemblies,** once the allow-list is empty: new projects, the build changes above,
   `PatchAll` per assembly.

Steps 2-3 are mechanical and change no behaviour; the regression gate on one pair is enough. Each PR in step 4 runs the scenarios for its area (listed per stage in
[host-local-session.md](host-local-session.md#scenarios)).

## Risks

- **A big rename hides real changes.** Keep the move commit to moves and namespace lines only, so
  `git log --follow` and blame still work, and review it with rename detection on.
- **Reflection by name.** The dvmp agent and any mod reading types by name break on a namespace
  change. The move updates `DevTools/`; external mods can't be checked from here.
- **Splitting the big components changes timing.** `NetworkedTrainCar`'s server and client halves
  share fields and run in one `Update` today. Splitting them into separate components can reorder
  their updates. Keep one driver that calls both halves in today's order.
- **The allow-list becomes permanent.** It only helps if it shrinks; the test failing on a stale
  line keeps it honest.

## Implementation status (2026-10-19)

- **CI is required.** Both jobs ("Core tests", "Build and protocol tests") are required status
  checks on `beta`, so the boundary test, once it exists, blocks a merge.
- **Step 2, `NetworkRole`: built.** `Shared/Session/NetworkRole.cs`. The four hand-copied
  "playing on someone else's server" helpers (`HazmatSync.IsClient`, `ExplosionSync.IsClient` and
  the private ones in `CommsRadioCargoLoaderPatch` and `CommsRadioDamagePatch`) now use
  `NetworkRole.IsRemoteClient`. `CouplerBreakPatch.IsClient` is left: it tests
  `Client != null` rather than a running client, so it isn't quite the same question.
- **Grouping inside each role: by system** (agreed 2026-10-19): `Server/Trains/`,
  `Client/Items/`, `Shared/Protocol/`..., with patches next to the code they serve.
- **Step 3, the move: done.** `Tools/CodeLayout/layout.js` decides where each file goes (its
  rules are the record of each decision) and writes `moves.tsv` and a grouped `moves.md`;
  `apply.js` moved the 469 files, gave each the namespace of its folder and rewrote `using`
  lines, qualified names, dvmp `--usings` strings and doc paths. The folder table above was the
  plan; `moves.md` is where things went. Differences from the plan:
  - Components that exist on every game (junctions, turntables, pit stops, plugs, registers,
    station controllers, `NetworkedPlayer`) are in Shared, not in one role.
  - The `NetworkedItem` and `NetworkedItemManager` partials are all in `Shared/Items`, their
    `.Server` and `.ClientProjection` parts too: partials can't span namespaces. Splitting them
    into classes moves those parts out.
  - The root namespace `Multiplayer` keeps `Multiplayer.cs` (UMM's entry point is
    `Multiplayer.Multiplayer.Load`), `Settings` and `Locale`; the front end is `Menus/`.
  - Patches that send through the host's own client today (couplers, hoses, MU cables, the comms
    radio) are in `Client/`; [host-local-session](host-local-session.md) stage 2 splits them.
  Two files had no namespace and now do (`PacketCompression`, `ServerBrowserClient`, which is
  entirely commented out), and nine declared one that didn't match their folder.
- **Step 3's second half, the checks: built** (see [The boundary test](#the-boundary-test)). The
  allow-list starts at 146 references: Shared → Server 80, Shared → Client 31, Client → Server
  13, Server → Client 7, and 15 into `Menus` from elsewhere. The most come from
  `NetworkLifecycle`, `NetworkedItemManager`, `NetworkedItem`, `NetworkClient` and
  `NetworkedTrainCar`.
- Steps 4-5: not started.
