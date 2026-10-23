# Host local session: the host's player without a loopback client

> Status (2026-10-20): stage 1 built and released, stage 2 built on `split/client-server`, stage 3
> built on `split/stage-3`: a host runs no client of its own. Not yet tested in the game. See
> [Implementation status](#implementation-status).
>
> Line numbers in the sections below are from `beta` at `6da65843` (2026-10-19).

## The three stages

This is the core of the client/server split. One process stays: the host's game runs the server
and its own player. Today that player is a **loopback `NetworkClient`**, a client of its own
server sharing the server's world. That one fact causes a whole class of bugs, from two directions:

- **The host follows itself.** Anything the server sends "to a client" can reach the host's own
  client, and client code then applies the server's view onto the world the server owns. The
  cargo containers that uncoupled at random were this: the host's client asked for a train sync
  when a car's interior loaded, the server answered with a snapshot to the host's own peer, and the
  host rolled its own consist back. A coupler fallback that rolled the host's chain back, a register
  rejection that emptied a shared register and item lease echoes that replaced the server's own
  lease were the same shape.
- **The host is checked like a stranger.** The host's own requests go out as packets and come back
  through the server's handlers, validated like a remote player's, for example a lever grab
  refused because the server's copy of the host's position was a packet late.

The stages remove that class step by step:

| Stage | What it does | What it fixes | Status |
|---|---|---|---|
| **1. The gate** | `HostOwnClient` classifies every packet a client handles. On the host, the ones that carry the host's own state are dropped before its client applies them. A protocol test fails on any packet that isn't classified. | The host following itself, for every packet known today. It's a gate, so it has to be kept up as packets are added. | **Built**, #117, in release 2026.10.19.5; **removed** with the client in stage 3 |
| **2. Direct requests** | The host player's requests call the server's handlers in-process, with the host's own `ServerPlayer` (`HandleLocal`, `LocalRequestQueue`), instead of going out through its client. The server stops sending the host's client anything but other players' presentation. | The host checked like a stranger: no stale positions, no round trips, no refusals meant for remote players. | **Built** on `split/client-server` (below) |
| **3. No client on the host** | `StartServer` no longer starts a `NetworkClient` on the host. A local player session, the host's player as a session of the server in the same process, takes its place behind the seams stage 2 added (`ILocalPlayerSession`, the `I<Area>Client` interfaces). A client game keeps `NetworkClient` as it is. | The whole class, structurally: with no client on the host there is nothing to follow the host's state and nothing to gate. Stage 1's gate is removed with it. | **Built** on `split/stage-3` (below), not yet tested in the game |

The dedicated host is the same server with no local player session at all (see
[The dedicated host](#the-dedicated-host)).

## Why

On 2026-10-19 the host's own client applied a topology snapshot that the server had sent for
followers, and uncoupled 29 trains in one session (see "The host's own player" in
[architecture.md](../architecture.md#the-hosts-own-player), and
[trains.md](trains.md)). The cause wasn't one bad send. It's the shape: the host's game is the
server's world, and it also runs a client that the server addresses like any other, so every
packet a client handles is a chance for the authority to follow itself.

`HostOwnClient` (stage 1) closes that at the point where packets are applied. It's a list, though:
every new packet has to be classified, and the loopback still carries the host's own simulation
and the host player's requests, so the host's world depends on a network round trip to itself.

**Why not two processes.** A full separation, a headless server process plus the player's game as
an ordinary client, was considered and rejected for listen hosts:

- **Memory.** DV can't run truly headless (see
  [dedicated-host.md §1](dedicated-host.md#1-running-without-graphics)). A second game is about
  4-5.5 GB of working set ([dedicated-host.md, Memory and CPU per instance](dedicated-host.md#memory-and-cpu-per-instance)),
  for every player who hosts.
- **Physics.** The host player would see the world as a client does: followers, playout delay and
  corrections, instead of the authoritative simulation it drives today.
- **It already exists as an option.** A dedicated host plus the owner's own game joining it is
  exactly that setup ([dedicated-host.md](dedicated-host.md)). Players who want it can run it.

So the separation happens inside one process: the server owns the world, and the host's player is
a local session that talks to the server by method calls, not packets.

**How this relates to [server-player-split.md](server-player-split.md).** That doc says the host's
player is "an ordinary client of the server". This doc keeps what that means for state and rules
(stored per player ID, restored at its login, validated like anyone's) and drops the transport:
"shortcuts stay only where the server and that client share a process, and only for transport (no
packet round trip), not for skipping validation" (its table, "Logic shortcuts") is what stage 2
builds.

## What the host's own client does today

`NetworkLifecycle.StartServer` starts the server and then a client connected to loopback
(`Shared/Session/NetworkLifecycle.cs:146-185`, client at `:178`). That client does five
different jobs, and each stage has to move each of them somewhere:

1. **The host player's requests**, as any player makes them (chat, pit stops, the comms radio).
2. **The host's own simulation**, committed and broadcast through the server: every dirty sim port,
   fuse and handbrake of every car (`NetworkedTrainCar.Common_OnTick`, `:1878-1885`).
3. **Applying other players' relayed changes to the host's world**: junctions, turntables and time
   skips. The server relays them without applying them; the host's client is what applies them.
4. **Presenting other players**: avatars, ping, chat, preferences, the loading screen and RPC
   answers.
5. **Identity.** `NetworkServer.SelfId` is the client's `PlayerId`
   (`Server/Session/NetworkServer.cs:118`), `SelfPeer` is looked up from it (`:106-114`),
   `IsHost(player)` compares against it (`NetworkLifecycle.cs:57-60`), and
   `NetworkLifecycle.IsProcessingPacket` is the client's flag (`NetworkLifecycle.cs:45`), which on
   the host also means "applying a relayed change".

### Packets the host's client applies and ignores (stage 1)

`Client/Session/HostOwnClient.cs` lists every
packet a client handles. `NetworkClient.Subscribe` registers each handler through `Handle`, which
drops the ignored ones when `IsHost()`; a protocol test (`HostOwnClientPacketTests`) fails on an
unlisted `Clientbound*` packet.

| | Packets (`HostOwnClient.cs`) |
|---|---|
| **Ignored** (`:23-96`) | Join steps the host skips (load state info, game params, save data, railway state, customisations, tick sync); weather, sleep, hazmat, shop stock, money, licences, garages, debt; all rolling stock (spawns, baseline, topology snapshots, destroy, derail cause, rerail, move, relocation, physics, brake state, control commits, coupler interaction, paint, restoration, cargo, explosions, fire, health, warehouse, windows); jobs; items (bulk and single updates, adoption, recall, catalogue, spatial lease, sample and commit); pit stops, plugs, the pit stop bulk update, cash registers, generic switches |
| **Applied** (`:101-124`) | Login response, server loading, disconnect, remove loading screen, RPC response; ping, player joined, player disconnect, position, preferences, chat; time advance, junction, turntable; control-authority updates; Lost and Found snapshot and retrieve result, container view and mutation result |

The applied list is the inventory of jobs 3 and 4. Stage 2 empties job 3 out of it, and stage 3
removes the list with the client.

### Requests the host sends through its own client

Every `NetworkLifecycle.Instance.Client.Send*` (and `Request*`) call site, and whether the host
reaches it. "In-process already" means the host has its own branch that calls server code
directly; those are the pattern for stage 2.

**Player and session**

| Call site | Sends | On the host |
|---|---|---|
| `Client/Players/LocalPlayerTrackerBase.cs:170` | `ServerboundPlayerPositionPacket`, every tick it moved | Sent (no guard). A dedicated host's parked body has no tracker. |
| `Client/Session/NetworkClient.cs:290-302` | `ServerboundPlayerPreferenceUpdatePacket` when the character model setting changes | Sent |
| `Client/UI/ChatGUI.cs:228` | `CommonChatPacket` | Sent |
| `NetworkClient.cs:756-762` | `ServerboundLoadStateUpdatePacket`: the host skips to `ReadyForWorldState` | Sent once at login |

**Train controls**

| Call site | Sends | On the host |
|---|---|---|
| `Shared/Trains/NetworkedTrainCar.cs:2103` (`Common_SendPorts`, `:2035`) | `CommonTrainPortsPacket` | Sent every tick with dirty ports. The host sends every dirty port that isn't locally fed or a handcar handle (`:2042-2044`), which includes simulation feedback, not just the host player's levers. |
| `NetworkedTrainCar.cs:2204` (`Common_SendFuses`) | `CommonTrainFusesPacket` | Sent, same rule |
| `NetworkedTrainCar.cs:1993` | `CommonHandbrakePositionPacket` | Sent; `Client_LocalPlayerCanTouch` is always true on the host (`:1972-1973`) |
| `NetworkedTrainCar.cs:2244` | `CommonBrakeCylinderReleasePacket` | Sent |
| `NetworkedTrainCar.cs:3229`, `:3353` | `ServerboundTrainControlAuthorityPacket` (take and release a lever) | Sent. Releases flush the ports first because both share `ReliableOrdered` (`:3347-3349`). |
| `NetworkedTrainCar.cs:3113` | `ServerboundFireboxIgnitePacket` (match in a firebox) | Sent; the server ignores it from the host |
| `NetworkedTrainCar.cs:749`, `:879`, `:2794`, `:2868`, `:3182` | `ServerboundTrainSyncRequestPacket` | **Not sent:** `SendTrainSyncRequest` returns on the host (`NetworkClient.cs:2610-2614`, stage 1) |
| `NetworkedTrainCar.cs:3158`, `:3167`, `:3194`; `Client/Trains/CouplerChainInteractionPatch.cs:30`; `CouplerPatch.cs:39`, `:67`; `HoseAndCockPatch.cs:30`; `MultipleUnitCablePatch.cs:18`, `:35` | Topology commands (couple, uncouple, chain, hoses, cocks, MU) | **In-process already:** `SendTrainTopologyCommand` calls `Server.CommitObservedTrainTopology()` on the host (`NetworkClient.cs:2394-2398`). Only the coupler-interaction fallback (`Start`) went as `CommonCouplerInteractionPacket` (`NetworkClient.cs:2265-2272`); stage 1 stops that on the host (`:2257-2261`). |
| `NetworkedTrainCar.cs:2224-2225` | Paint | **In-process already** (`Server.SendPaintThemeChange`) |
| `NetworkedTrainCar.cs:2876`, `Shared/Trains/NetworkTrainsetWatcher.cs:323`, `:348`, `Client/Trains/CouplerBreakPatch.cs:69` | `RestoreCanonicalTrainTopology` (client topology state, may request a topology sync) | **Not run:** returns on the host (`NetworkClient.cs:2470-2474`, stage 1) |

**World**

| Call site | Sends | On the host |
|---|---|---|
| `Shared/World/NetworkedJunction.cs:83` | `CommonChangeJunctionPacket` | Sent for every switch on the host's world (`initialised` is true on the host, `:59`, `:65`), whoever or whatever threw it |
| `Shared/World/NetworkedTurntable.cs:83` | `CommonRotateTurntablePacket` | Sent from the tick whenever the target rotation changed |
| `Client/World/TimeAdvancePatch.cs:44` | `ServerboundTimeAdvancePacket` | Sent when the host sleeps or fast-travels with players online; the host advances its own time at once (`:45`) |
| `Shared/World/NetworkedGenericSwitch.cs:125-128` | Generic switch | **In-process already** (`Server.SendGenericSwitchState`) |
| `Shared/World/HazmatSync.cs:300`, `Shared/Trains/CargoFirePatch.cs:139` | Hazmat and cargo ignite | Clients only (`HazmatTileManagerPatch.cs:56`, `CargoFirePatch.cs:170`, `:185`) |

**Stations, shops and the career**

| Call site | Sends | On the host |
|---|---|---|
| `Shared/Stations/NetworkedPitStopStation.cs:742`, `:758`, `:777`, `:802`, `:825`, `:846`, `:875` | `CommonPitStopInteractionPacket` | Sent by the host player; the server validates it, then skips re-applying it for `SelfId` (`NetworkedPitStopStation.cs:313`) |
| `Shared/Stations/NetworkedPluggableObject.cs:159` | `CommonPitStopPlugInteractionPacket` (every `LateUpdate` while not processing as host) | Sent; the server skips re-applying it for `SelfId` (`NetworkedPluggableObject.cs:232`) |
| `Shared/Stations/NetworkedCashRegisterWithModules.cs:401` | `CommonCashRegisterWithModulesActionPacket` (`AddCash`) | Sent: `CashRegisterBasePatch.AddCash` has no host guard (`Client/Stations/CashRegisterBasePatch.cs:21-47`), and the coroutine waits `RPC_Timeout`. `Buy` and `Cancel` run vanilla on the host (`CashRegisterWithModulesPatch.cs:37`, `:88`). |
| `Client/Trains/CommsRadioCarDeleterPatch.cs:36` | `ServerboundTrainDeleteRequestPacket` | Sent unless the host plays alone (`:21`) |
| `Client/Trains/RerailControllerPatch.cs:50` | `ServerboundTrainRerailRequestPacket` | Sent unless the host plays alone (`:32`) |
| `Client/Trains/CommsRadioCrewVehiclePatch.cs:47-91` | `ServerboundWorkTrainRequestPacket` with an RPC ticket | Sent unless the host plays alone (`:24`); the answer is a `ClientboundRpcResponsePacket` that `RpcManager.ResolveTicket` resolves (`NetworkClient.cs:836-840`) |
| `Client/Career/CareerManagerExtensions.cs:217`, `:244` | Lost and Found list and retrieve | Sent; answers come back as events on the client (`LostItemRetrieveCompleted`, `:214`) |
| `Client/Items/Containers/ContainerUi.cs:127`, `:136`, `:192`, `:231`, `:249`, `:297` | Container view and mutations | Sent; answers come back as `ContainerViewReceived` and `ContainerMutationCompleted` (`:268-269`) |
| `Client/Jobs/JobValidatorPatch.cs:75-84` | Booklet summon | **In-process already:** the host calls `JobBookletSummonCoordinator.TryStart` with its own `ServerPlayer` |
| `JobValidatorPatch.cs:116-121`, `Client/Jobs/WarehouseMachineControllerPatch.cs:90`, `:101`, `CommsRadioCarSpawnerPatch.cs:30`, `CareerManagerFeesScreenPatch.cs:17`, `CareerManagerLicensePayingScreenPatch.cs:12`, `GlobalShopControllerPatch.cs:18`, `Client/Items/Behaviours/MoneyPatch.cs:48`, `CashRegisterBasePatch.cs:39`, `:56`, `CommsRadioCargoLoaderPatch.cs:25`, `CommsRadioDamagePatch.cs:20` | Validation, warehouse, car spawner, fees, licences, shop | The host runs vanilla (its game is the authority) |

**Items.** The host's item paths are in-process already: `NetworkedItem` sends only when not
`processingAsHost` (`Shared/Items/LiveState/NetworkedItem.cs:772-774`), the spatial manager
accepts the host's own samples and settlements directly
(`Shared/Items/Physics/NetworkedItemSpatialManager.cs:793-830`), the wake witness and recall branch on
`IsHost()` (`Server/Items/TrainWake/TrainItemWakeServer.cs:214`,
`NetworkedItemManager.ProjectionAndCompatibility.cs:270-283`), and fall rescue has a host scan
(`NetworkedItemManager.FallRescue.cs:61-64`). They find the host's `ServerPlayer` by `SelfId`.

### What else reads the host's client

| What | Where |
|---|---|
| `Client.PlayerId` as "the local player" (items, spatial leases, wake, inventory) | `NetworkedItem.cs:329`, `:669`; `NetworkedItem.Snapshots.cs:379-430`, `:630`, `:861`; `NetworkedItem.VrHand.cs:82`; `NetworkedItemManager.ProjectionAndCompatibility.cs:272`; `NetworkedItemSpatialManager.cs:326`, `:349`, `:400`, `:424`, `:735`; `TrainItemWakeServer.cs:190`; `Shared/Items/Inventory/InventoryIntegration.cs:98` |
| `ClientPlayerManager` (remote avatars) | Created by the client (`NetworkClient.cs:202`); fed by joined, disconnect, position, preferences and ping packets (`:842-896`). Read by `NetworkedItem.StateApplication.cs:46`, `:97`, `:250`, `:527`, `:643`, `:861` (held items on avatar hands), `NetworkedPluggableObject.cs:598`, `:628` (plugs in a hand), `NetworkedWorldMap.cs:18-36`, `:112`, `PlayerListGUI.cs:75`, `ChatGUI.cs:259`, and MPAPI (`Client/Api/ClientAPIProvider.cs:32-33`, `:73-91`) |
| Chat UI | Built by `OnClientboundRemoveLoadingScreen` (`NetworkClient.cs:998-1010`); messages arrive through `OnCommonChatPacket` (`:1598-1601`) |
| Ping | `Client.Ping` in `PlayerListGUI.cs:85`, `TrainPortPresenter.cs:364`, the radio's sound delays (`CommsRadioCarDeleterPatch.cs:45`, `RerailControllerPatch.cs:64`), and `RPC_Timeout` (`NetworkClient.cs:184`; `NetworkedCashRegisterWithModules.cs:364`, `:384`, `:406`; `CommsRadioCrewVehiclePatch.cs:48`) |
| RPC | `RpcManager` (`Client/Session/RpcManager.cs:25-46`) issues tickets; only `OnClientboundRpcResponsePacket` resolves them |
| Loading | `DisplayLoadingInfo_Start_Patch` unhooks DV's own loading-finished handler (`Client/Session/DisplayLoadingInfoPatch.cs:8-11`), so only `RemoveLoadingScreen` (`NetworkClient.cs:975-1011`) brings the host's loading screen down |
| Other | `IsProcessingPacket` (`NetworkLifecycle.cs:45`); `DebugRuntime.SetRole` (`NetworkLifecycle.cs:172`, `NetworkClient.cs:756`); network stats (`NetworkLifecycle.cs:124`); MPAPI's NetId lookups (`NetworkManager.cs:67`, only the client's `Start` reaches it); the station loco spawner waits for `Client != null` (`Server/Trains/StationLocoSpawnerPatch.cs:28`); `LocoRestorationControllerPatch.cs:58` reads `Client.LoadingState`; `RemoteDispatchPatch.cs:57` reads `ClientPlayerWrappers`; career and container UI events (`CareerManagerExtensions.cs:214`, `ContainerUi.cs:268-269`) |

### How the server treats the host's own requests

`IsHost(player)` is the only test (`NetworkLifecycle.cs:57-60`), and it means two different
things that stage 2 separates:

- **The host's simulation.** Ports, fuses, the handbrake and brake-cylinder release come from the
  authoritative world, so the server takes them without a lease or range check and doesn't apply
  them again (it has them already). Topology is the same idea, done in-process
  (`CommitObservedTrainTopology`).
- **The host's player.** Chat, the radio, pit stops, registers and levers are a person's
  requests. Most handlers have no host branch and validate the host like anyone, including range
  checks against `player.WorldPosition`, which for the host comes only from its own position
  packets (`NetworkServer.cs:2081`).

The server also stops some state from reaching the host's client on its own side:
`SelfPeer` is excluded from most broadcasts (`excludeSelf` in `SendPacketToAll`,
`NetworkServer.cs:671`, `:691`, `:742`, `:755`, and `SelfPeer` passed as the excluded peer at
`:885-1203`), and `CullingManager` never lists the host among a station's active players
(`Server/Players/CullingManager.cs:87`). Sends that do reach the host's client today:
player disconnect (`NetworkServer.cs:591-599`), ping (`:635`), the `/time` chat command's time skip (`:843-845`),
preferences (`:1218`), server chat messages (`:1572`), control-authority updates (`:1275`,
`:1277`), item spatial leases, samples and commits (`:1382-1433`), and every direct reply to a
request the host made.

## Stage 1: the gate (built)

Described above: `HostOwnClient` drops what the host's client must never apply, and the requests
that only fetch state the host already has (`SendTrainSyncRequest`, the topology sync,
`RestoreCanonicalTrainTopology`, the coupler-interaction fallback) return on the host. It stays
in place through stage 2 as the safety net, and goes with the client in stage 3.

## Stage 2: the host's requests go straight to the server

### The shape

- **The host keeps one `ServerPlayer`**, created at its loopback login as today, and the server
  keeps a reference to it as `Server.LocalPlayer` instead of finding it through `SelfId`.
- **Each handler is split** into the packet adapter and the work:
  `OnXPacket(packet, peer) => if (TryGetServerPlayer(peer, out p)) HandleX(packet, p)`. The packet
  classes stay as the request types, so there's one validation path and no new data types.
- **The host submits instead of sending.** Each host call site above calls
  `Server.Local.Submit(packet)` (or a typed method) instead of `Client.Send*`. Submissions go on a
  queue that the server drains where it handles received packets, so:
  - the timing doesn't change (a loopback packet was handled at the next poll too);
  - the order between requests doesn't change (authority requests and port commits rely on
    `ReliableOrdered` order, `NetworkedTrainCar.cs:3347-3349`);
  - nothing re-enters vanilla code from inside the Harmony patch that raised it (a junction's
    `Switched` event, a plug's `LateUpdate`).

  A type can be made synchronous later where that's shown to be safe and useful (cash registers,
  the work-train answer).
- **The host's simulation commits without a player.** Ports, fuses, handbrake and brake release
  from `Common_OnTick` aren't requests: they call `Server.CommitHostControls(car, ...)`, which does
  what the host branch of each handler does now (capture the values, bump the revision, broadcast
  the commit) with no lease, range or operation-id bookkeeping.
- **The server applies what it only relayed.** Junction, turntable and time-skip packets from
  remote players are applied to the server's world by the server, then relayed to the other
  players. Today the host's client applies them (`NetworkClient.cs:1013-1016`, `:1077-1089`).
  - Echo suppression: `NetworkedJunction.Junction_Switched` (`NetworkedJunction.cs:80`), the turntable tick and
    `TimeAdvancePatch` (`TimeAdvancePatch.cs:33`) return while `IsProcessingPacket`, which is the client's flag. The
    server needs its own apply scope (`NetworkLifecycle.IsApplyingRemote`, set by the server while
    it applies and by the client while it applies), and those three read it.
- **Replies to the local player are presentation only.** `SendPacket` to `LocalPlayer` delivers
  only the types in the stage 1 "applied" list (minus junction, turntable and time skip, which
  the server no longer sends it); anything else is dropped with a warning, so the rule moves from
  "the host's client drops it on receipt" to "the server never sends it". Rejections the host
  ignores today (control commits, pit stops, plugs, registers, coupler `NoAction`) stop being
  sent.

### What's built for it, and how to move a request

The shared pieces (built, on `split/client-server`):

| Piece | Where | What |
|---|---|---|
| `LocalRequestQueue<TPlayer>` | `Multiplayer.Core/Session/` (tested) | Handlers per request type; requests queued in order and handled at the server's poll; a handler that throws doesn't stop the rest; no player, nothing handled |
| `ILocalServer.Submit(request)` | `Shared/Session/ILocalServer.cs`, reached as `NetworkLifecycle.Instance.LocalServer` | How Shared and Client code hand a request to the server without referencing it. Null when this game isn't the server |
| `ILocalPlayerSession` | `Shared/Session/ILocalPlayerSession.cs`, reached as `NetworkLifecycle.Instance.LocalSession` | The player at this keyboard for Shared and role code: its id, display name and ping, the other players' avatars and their connect, disconnect and preference events, and chat. `NetworkClient` implements it now; the host's local session will in stage 3 (see [A seam first](#a-seam-first)) |
| `NetworkRole.LocalPlayerId` | `Shared/Session/NetworkRole.cs` | The one local player id, `LocalSession.PlayerId`, the same on a host and a client (it replaced `IsHost() ? ItemServer.LocalPlayerId : LocalSession.PlayerId`) |
| Area interfaces | `Shared/<Area>/I<Area>Server.cs`, `I<Area>Client.cs` | When an area needs more, it declares its own interface, implemented by its partial `NetworkServer.<Area>.cs` / `NetworkClient.<Area>.cs` and read with `LocalServer as I<Area>Server` / `LocalSession as I<Area>Client` |
| `NetworkServer.LocalPlayer`, `HandleLocal<T>`, the drain | `Server/Session/NetworkServer.LocalPlayer.cs` | The host's `ServerPlayer`; registering an in-process handler; draining the queue after each `PollEvents` |
| `NetworkServer.ApplyingRemoteChange()` | the same file | Scope for the server applying another player's change; `NetworkLifecycle.IsProcessingPacket` is true inside it, so existing echo checks hold |
| The send hook | `NetworkClient.SendPacketToServer` | Offers every request to `LocalServer.Submit` first: once the server handles a type in-process, every host call site for it goes direct, with no change at the call site |

To move a request in-process, in the area's `NetworkServer.<Area>.cs`:

1. Split the packet handler: `OnXPacket(packet, peer)` looks up the player and calls
   `HandleX(packet, player)`, which does the work. Check every `IsHost(player)` in it: does it mean
   the host's simulation (keep, or move to a no-player path like `CommitHostControls`) or the host's
   player (validate it like anyone's)?
2. Register it: implement the area's partial method,
   `partial void RegisterLocalTrains() { HandleLocal<XPacket>(HandleX); }`. Each area has its own
   (`RegisterLocalTrains`, `Items`, `World`, `Stations`, `Jobs`, `Career`, `Session`), so areas
   never edit the same lines.
3. That's all for the call sites: `NetworkClient.SendPacketToServer` now submits it. Requests
   sent through `SendNetSerializablePacketToServer` (`CommonItemsBulkUpdatePacket`,
   `CommonPitStopPlugInteractionPacket`, MPAPI's external packets) aren't hooked: submit those at
   the call site, `NetworkLifecycle.Instance.LocalServer?.Submit(packet) == true`, or add a hook.
4. Replies: the handler's replies to `player.Peer` still reach the host's own client. Keep the
   ones that are presentation, stop sending the ones `HostOwnClient` ignores.
5. Requests that rely on each other's order move together: a request handled in-process can
   overtake one still on loopback. Control authority, ports, fuses and the handbrake are one set.
6. Run the area's scenarios on one pair (see [Scenarios](#scenarios)).

### Each request, today and direct

| Request | Handler | Today, for `IsHost(player)` | Stage 2 |
|---|---|---|---|
| Ports, fuses | `OnCommonTrainPortsPacket` `NetworkServer.cs:2424`, fuses `:2470` | Lease bypass (`NetworkedTrainCar.Server_MayWritePorts`, `NetworkedTrainCar.cs:1391`), range bypass (`ValidateControlEnvelope`, `NetworkServer.cs:2497`), skips validating and re-applying (`:2439-2446`), stamps the commit with the packet's tick (`:2455-2456`). An accepted commit goes to everyone but the host (`SendControlCommit`, `:2536-2543`); a rejection goes back to the host. | `Server.CommitHostControls(car, ids, values, tick)` from `Common_SendPorts`/`Common_SendFuses`, with the current tick. No rejection path to deliver. |
| Handbrake, brake-cylinder release | `NetworkServer.cs:2325`, `:2299` | Range bypass, skips setting the value (`:2334-2339`, `:2310`), commit as above (`:2342`, `:2322`) | Same call as ports. |
| Control authority (grab and release a lever) | `NetworkServer.cs:2460` → `NetworkedTrainCar.Server_ReceiveAuthorityRequest` (`NetworkedTrainCar.cs:1800`) | **No host branch:** the range check (`:1810`) uses the host's last position packet, so a late one can deny the host player a lever. Grants block the lever for everyone else (`:1822`); releases go to all, the host included (`NetworkServer.cs:1275`). | `car.Server_ReceiveAuthorityRequest(port, LocalPlayer, take)` through the queue. Same validation; the position is read from the local tracker (below), so it's never stale. Authority updates to the local player stay (they block the host's controls). |
| Topology (couple, uncouple, chain, hoses, cocks, MU) | `NetworkServer.cs:2638` | `IsInRange` is true for the host (`:2647`), but the host never sends: `SendTrainTopologyCommand` commits in-process (`NetworkClient.cs:2394-2398`). | Unchanged; it's already direct. |
| Coupler interaction fallback | `NetworkServer.cs:2233` | **No host branch:** runs `Common_ReceiveCouplerInteraction` on the host's world for the host's own packet (`:2253-2254`), a second apply. Stage 1 stops sending it. | Removed on the host. |
| Train and topology sync requests | `NetworkServer.cs:2610`, `:2762` | No host branch: dirties and rebroadcasts the whole car, snapshot back to the host. Stage 1 stops sending them. | Removed on the host. |
| Firebox ignite, add coal, tender coal | `NetworkServer.cs:2408`, `:2368`, `:2388` | Ignored from the host (`:2416`, `:2380`, `:2400`). Only firebox ignite is still sent (`NetworkedTrainCar.cs:3113`). | Not sent on the host. |
| Player position | `NetworkServer.cs:2070` | Stores tracking, car and posture (`:2081-2083`), relays to the other players (`:2087-2094`). | The server reads the local player's tracker each tick (`LocalPlayer.UpdateFromTracker()`), merges it the same way and relays it. The host's `WorldPosition` is then current for every range check. |
| Preferences | `NetworkServer.cs:2097` | Stores the character, sends to all including the host (`:1218`). | `Server.UpdatePreferences(LocalPlayer, prefs)` from `Settings.OnSettingsUpdated`; the echo is presentation. |
| Chat and commands | `NetworkServer.cs:3181` → `ChatManager.ProcessMessage` (`ChatManager.cs:232`) | Admin by `IsHost(player)` (`:299`), can't be kicked (`:728`). Plain chat goes to everyone but the sender (`:282` → `NetworkServer.cs:1570`); server messages to all (`:1572`). | `ChatManager.ProcessMessage(text, LocalPlayer)`; `ChatGUI` already shows the host its own line (`ChatGUI.cs:206`, `:211`). Server messages and whispers to the host are presentation. |
| Fast travel and sleep | `NetworkServer.cs:2118`, sleep `:2148` | Fast travel: relayed to everyone but the sender (`:2133-2142`); the host already advanced locally (`TimeAdvancePatch.cs:45`). Host sleep skips the cooldown check, relays and shares the wake time (`NetworkServer.cs:2157-2162`). **A remote player's** skip isn't applied by the server; the host's client applies the relay. | Host: `Server.OnLocalTimeAdvance(seconds, sleep)` does the same relay. Remote: the server calls `TimeAdvance.AdvanceTime` itself in the apply scope, then relays. The `/time` command (`NetworkServer.cs:843-845`) applies directly instead of sending to all. |
| Junction, turntable | `NetworkServer.cs:2223`, `:2228` | Relay to everyone but the sender. The host's own changes (every switch on its world, `NetworkedJunction.cs:83`; the turntable tick, `NetworkedTurntable.cs:83`) go out through its client; remote changes reach the host's world only through its client. | Host: `Server.BroadcastJunction/Turntable(...)`. Remote: the server switches the junction or turns the turntable in the apply scope, then relays. |
| Pit stop | `NetworkServer.cs:3195` → `NetworkedPitStopStation.ProcessInteractionPacketAsHost` (`NetworkedPitStopStation.cs:303`) | `ValidateInteraction` is always true (`:242-246`); skips re-applying for `SelfId` (`:313`); relays to the station's active players, never the host. | `station.ProcessInteractionPacketAsHost(packet, LocalPlayer)`. The `SelfId` test becomes `player == LocalPlayer`. |
| Plug | `NetworkServer.cs:3211` → `NetworkedPluggableObject.ProcessInteractionPacketAsHost` (`NetworkedPluggableObject.cs:176`) | Skips re-applying for `SelfId` (`:232`); sent every `LateUpdate` (`:151-160`). | `plug.ProcessInteractionPacketAsHost(packet, LocalPlayer)`. Cheaper than a packet a frame. |
| Cash register (`AddCash` on a module register) | `NetworkServer.cs:3898` → `Server_ProcessCashRegisterAction` (`NetworkedCashRegisterWithModules.cs:143`) | No host branch; reach check (`:149`) on the host's position. Success goes to the station's active players (`:224`), so never to the host, whose coroutine waits out `RPC_Timeout` (`:401-417`). A failure goes back to the host (`:226-234`), where it emptied the register's modules before stage 1. | Direct call returning the result; the coroutine finishes on it. |
| Car delete, rerail | `NetworkServer.cs:2770`, `:2818` | No host branch; the host's world is changed by the handler, broadcasts exclude `SelfPeer` (`:913`, `:1106`). | `HandleTrainDelete/Rerail(packet, LocalPlayer)`. |
| Work train (RPC) | `NetworkServer.cs:2895` | No host branch; range check (`:2966-2971`). The answer is `SendRpcResponse(peer)` (`:813-825`) back over loopback. Unknown player, track or livery return with no answer (`:2901-2916`), so the ticket times out. | The handler returns the `SpawnResponse`; the radio patch resolves its ticket with it (`RpcManager.ResolveTicket`). Every path returns an answer. |
| Lost and Found list and retrieve | `NetworkServer.cs:3744`, `:3751` | No host branch; answers to the sender (`:3748`, `:3768`, `:3779`). | Direct; the answer raises the same client events (`LostItemRetrieveCompleted`) on the local session. |
| Container view and mutations | `NetworkServer.cs:3785`, `:3799` | No host branch; answers to the sender (`:3795`, `:3852`, `:3859`). | Direct; raises `ContainerViewReceived`/`ContainerMutationCompleted`. |
| Items, booklet summon, wake witness, recall | (various) | Already in-process with the host's `ServerPlayer`: see Items above, `JobValidatorPatch.cs:77-79`, `NetworkedItemManager.ServerInterest.cs:241`. | Use `LocalPlayer` instead of looking up `SelfId`. |

### What the server still sends the host's client after stage 2

Only the other players: joined, disconnect, position, ping, preferences, chat and server
messages; control-authority updates; the loading flow (`RemoveLoadingScreen`). Item spatial sends
(`NetworkServer.cs:1382-1433`) skip the local player, as most item sends already do
(`NetworkedItemManager.ServerInterest.cs:68`, `:113`, `:279`). The stage 1 "applied" list is then
exactly the presentation list stage 3 moves.

## Stage 3: no client on the host

> As built (see [Implementation status](#implementation-status)), one thing differs from this plan:
> the host's player keeps a peer. Its `ServerPlayer.Peer` is a `LocalPeer`, an in-process peer whose
> sends the session reads a poll later through a processor that subscribes only presentation, so
> `SelfPeer`, `excludeSelf` and every reply to `player.Peer` work unchanged instead of each send site
> calling the session. The other players' avatars are the server's (`NetworkLifecycle.Avatars`), fed
> from its own events on any host.

`StartServer` stops starting a client (`NetworkLifecycle.cs:178`). A `LocalPlayerSession` takes
its place: the host's player as a session of the server in the same process. A client game keeps
`NetworkClient` exactly as it is.

### A seam first

About sixty places read `NetworkLifecycle.Instance.Client` for "the local player" (the table
above). Before stage 3, route them through one interface, implemented by `NetworkClient` on a
client and by `LocalPlayerSession` on the host. This changes no behaviour.

**Built** (2026-10-20) as `NetworkLifecycle.LocalSession` (`ILocalPlayerSession`; not
`LocalPlayer`, which is the server's `ServerPlayer` for the host): `PlayerId`, `DisplayName`,
`Ping`, `RemotePlayers` and `TryGetRemotePlayer`, the `RemotePlayerConnected`/`Disconnected`/
`PrefsUpdated` events, and `SendChat`. The player list, chat, the world map's player markers, the
radio's sound delays, the port presenter's ping and `NetworkServer.SelfId` read it; Shared item,
station and world code did already. Requests go through `ILocalServer.Submit` (or the client's
send hook), not the session. What it leaves on `NetworkLifecycle.Instance.Client`:

- **Area requests and their answers** (Lost and Found, containers, the radio's requests, train
  controls, `RPC_Timeout`, `LoadingState`): each area's `I<Area>Client`, in stage 3 by area.
- **Remote-client-only code** (followers, "send and skip vanilla" patches): it keeps the client,
  which a client's game still has.
- **The player's position** (`LocalPlayerTrackerBase`): read by the server from the tracker on
  the host (stage 2), sent by the client on a client.
- **MPAPI's client API and `RemoteDispatchPatch`**: stage 3.

### What moves

| Today on the host's client | Moves to |
|---|---|
| **Login** (`NetworkClient.Start`, host token, `NetworkClient.cs:223-261`; server: `PlayerAuthentication.cs:63-69`, `NetworkServer.cs:1595-1729`) | `Server.AddLocalPlayer(identity)`: the same steps without a peer. The record binding must run exactly as at login (`InstallGuidRekey.OnLogin`, `LiveItems.RebindPersistentOwner`, Lost and Found and world items, `NetworkServer.cs:1715-1718`), or `host-player-persist` regresses. Keep the player-id allocation the same: ids are bytes in item records and in topology operation ids (`NetworkClient.cs:2419`). |
| **`SelfId`, `SelfPeer`, `IsHost(player)`** (`NetworkServer.cs:105-118`, `NetworkLifecycle.cs:57-60`) | `SelfId` becomes `LocalPlayer?.PlayerId`, `IsHost(player)` becomes `player == LocalPlayer`. `SelfPeer` goes: with no peer for the local player, `peers` never holds it, so every `excludeSelf` and `SelfPeer` exclusion is simply true. The other `SelfPeer` and `SelfId` references (items, spatial leases, pit stops, plugs, containers, culling, the booklet summon) move to `LocalPlayer`. |
| **Loading flow.** The host's client skips to `ReadyForWorldState` (`NetworkClient.cs:758-763`); the server completes it (`NetworkServer.cs:1835-1842`), announces it (`:2040`, `:2043`), sends it the existing players (`:2046-2063`) and `RemoveLoadingScreen` (`:2065`), which is the only thing that brings the host's loading screen down (`DisplayLoadingInfoPatch.cs:8-11`, `NetworkClient.cs:975-992`). If the save hasn't loaded, the host waits in `joinQueue` (`NetworkServer.cs:1816-1824`). | `LocalPlayerSession.OnServerLoaded()`, called where the server would have answered `ReadyForWorldState`: run the same completion (announce, `PlayerReady`), then call the loading-screen code from `OnClientboundRemoveLoadingScreen` directly. Also fire `PlayerConnected`, which never fires for the host today (`NetworkServer.cs:1801`; MPAPI's `OnPlayerConnected`, `Server/Api/ServerAPIProvider.cs:149`): decide whether that's a fix or a change mods notice. |
| **`ClientPlayerManager`** (`NetworkClient.cs:202`, fed at `:842-896`) | Owned by the session and fed by the server's own events (a player completes loading, moves, changes preferences, leaves) instead of packets. Everything that reads avatars keeps working through the seam. |
| **Ping list** (`OnClientboundPingUpdatePacket`, `:893-896`; the host's own ping, `PlayerListGUI.cs:85`) | The server already has each peer's latency (`OnNetworkLatencyUpdate`, `NetworkServer.cs:623-635`); the session reads it. The host's own ping is 0, and the server sends clients the host's ping as 0 or not at all. |
| **Chat** (`ChatGUI` built on `RemoveLoadingScreen`, `NetworkClient.cs:998-1010`; `OnCommonChatPacket`, `:1598-1601`; `ChatGUI.SendChat`, `ChatGUI.cs:228`) | The session builds the chat UI at the end of loading (not on a dedicated host) and receives the server's chat calls directly; sending is `ChatManager.ProcessMessage(text, LocalPlayer)`. |
| **Player preferences** (`NetworkClient.OnSettingsUpdated`, `NetworkClient.cs:290-302`) | The session subscribes to `Settings.OnSettingsUpdated` and calls the server. |
| **Position** (`LocalPlayerTrackerBase.cs:170`) | Read by the server from the tracker (stage 2). |
| **RPC** (`RpcManager`, resolved only by `OnClientboundRpcResponsePacket`) | Resolved locally by the stage 2 direct answer. |
| **`IsProcessingPacket`** (`NetworkLifecycle.cs:45`) | The apply scope from stage 2. |
| **Career and container UI events** (`CareerManagerExtensions.cs:214`, `ContainerUi.cs:268-269`) | Raised by the session. |
| **Odds and ends**: `DebugRuntime.SetRole` (`NetworkClient.cs:756`); network stats (`NetworkLifecycle.cs:124`); MPAPI NetId initialisation, which only the client's `Start` reaches (`NetworkManager.cs:67`); the station loco spawner waiting for `Client != null` (`StationLocoSpawnerPatch.cs:28`); `LocoRestorationControllerPatch.cs:58` (`Client.LoadingState`); `RemoteDispatchPatch.cs:57`; `TrainPortPresenter.cs:364` and the radio's sound delays (`Client.Ping`); `RPC_Timeout` users; `NetworkClient.Stop` restoring the session (`NetworkClient.cs:274-278`, skipped on the host) | Each to the session or the server; the NetId initialisation into `NetworkServer.Start`. |
| **MPAPI's client API** on the host (`Client/Api/ClientAPIProvider.cs`: `PlayerId`, `Players`, `PlayerCount`, `Ping`, `RegisterPacket`, `SendPacketToServer`) | Backed by the session. A mod's clientbound handlers must still run on the host for broadcasts the host's client gets today, and its `SendPacketToServer` from the host must reach its server handler in-process. Deliver external packets to the session by serialising and handling them locally, so mods see the same packets they do now. |
| **dvmp probes and scenario scripts** that read the host's `Client` (`DevTools/Dvmp/Probes/snapshot.cs`, `DevTools/DvmpAgent/AgentServer.cs`, scripts in `borrowed-disconnect`, `lost-and-found`, `rejoin`, `settle-window`, `station-load`) | Through the seam. |

Single player runs the same way (a one-player server), and gets cheaper: no loopback connection
at all.

## The dedicated host

> Built in stage 3: a dedicated host has no player, no session and no service identity (no
> `IsServiceHost`, no `ServiceGuid` login, no host token; `ServerPlayers` is `ServerPlayers`), and
> its console commands run as a console sender (`ServerPlayer.CreateConsole`). What follows was the
> plan, written when it still had them.

Today a dedicated host keeps the loopback client as a **service identity**: its `ServerPlayer` is
`IsServiceHost` (`Server/Session/ServerPlayer.cs:44`), logs in with `Settings.ServiceGuid`,
isn't counted in `ServerPlayers` (`NetworkServer.cs:100-103`) and completes loading silently
(`:2015-2021`). The reason given in [dedicated-host.md](dedicated-host.md#keep-the-loopback-client-it-isnt-only-a-player)
is that the loopback carries the host's simulation and applies relayed state. Those are jobs 2 and
3, which stages 2 and 3 take off it.

- **Stage 2.** Nothing changes for a dedicated host except that its simulation commits through
  `CommitHostControls` and relayed junctions, turntables and time skips are applied by the server.
  The service identity still logs in, but nothing it does needs a player: it has no tracker, no
  chat UI and no requests. `IsServiceHost` stays the test for "not a player".
- **Stage 3.** A dedicated host is the server with **no** `LocalPlayerSession`. There's no
  service identity, no `ServiceGuid` login and no host token for it: `ServerPlayers` becomes
  `ServerPlayers`, and `IsServiceHost` and the loading special case go. `IsDedicatedHost` stays for
  what's about having nobody at the keyboard (the parked body, reduced rendering, the empty-server
  pause, the watchdog). `IsHostPlayingAlone` becomes "there's a local player and it's the only
  one". The owner's own game keeps joining as an ordinary client, as now.
- `ServiceGuid` may still be worth keeping as the identity of "the server" in logs and records it
  writes, but nothing should be owned by it.

## Risks

- **Timing.** The host's requests and commits have always been a poll late. A synchronous call
  would change orders that nobody has written down (a junction switched inside its own event, a
  plug's `LateUpdate`, a port flushed before an authority release). The submit queue keeps the
  old timing; move a type to synchronous only with a scenario covering it.
- **Echo loops.** Once the server applies junction, turntable and time-skip changes, the code that
  sends them on must see the apply scope, or the host sends each one straight back. A loop here
  shows up as a junction flapping or a time skip applied twice.
- **Validation the host never had.** Routing the host's player through the same handlers exposes
  checks it skipped by accident (range checks on a fresh position will now pass where a stale one
  failed, and vice versa). That's the point
  ([server-player-split.md, step 6](server-player-split.md#plan)), but do it per request type, not
  all at once.
- **Two senses of "host".** About 300 `IsHost()` calls mean "this game is the server", and 20
  `IsHost(player)` calls mean "this is the host's player". Stage 2 has to decide, at each
  `IsHost(player)`, whether it means the simulation or the player.
- **Mods.** MPAPI mods that register client packets or read `IClient` on the host, and that rely
  on `PlayerConnected` never firing for the host, see a change in stage 3.
- **The stage 1 list going stale.** Until stage 3, a new packet still has to be classified in
  `HostOwnClient`; the protocol test keeps catching that.
- **Loading.** The host's loading screen, chat UI, `PlayerReady` and the record binding all hang
  off the loopback login today. Missing one in stage 3 leaves the host on a loading screen or
  without its items.

## Order

1. Stage 1 (done, #117).
2. The seam (`NetworkLifecycle.LocalPlayer`), no behaviour change.
3. Stage 2, one area at a time, each its own PR:
   1. relays: the server applies junction, turntable and time-skip changes (the apply scope);
   2. the host's simulation commits (`CommitHostControls`);
   3. position read from the tracker;
   4. the remaining requests through `Server.Local.Submit`, in the order of the table;
   5. replies to the local player restricted to presentation.
4. Stage 3: `LocalPlayerSession`, then removing the client from `StartServer`, then the dedicated
   host without a service identity.

## Scenarios

Run each change on a player host and on a dedicated host (`UP_ARGS=--dedicated`); see
[testing.md](../testing.md#regression-gate).

| Change | Covered by | Not covered yet |
|---|---|---|
| Relays applied by the server | none | **New:** a client throws a junction and turns a turntable near the host, and fast-travels and sleeps; the host's world matches and nothing is applied twice. The reverse, from the host, too. |
| The host's simulation commits | `compressor-signal` (the host sets a port), `settle-window` (host resyncs), `uncouple-moving`, `host-stall`, `couple-impact` (the host drives), `stoker-coal` | |
| Control authority | `cab-controls` (from the client) | **New:** the host grabs a lever the client holds, and the client one the host holds. |
| Topology | `manual-uncouple`, `uncouple-moving`, `couple-impact`, `coupler-desync`, `host-stall` | |
| Position from the tracker | `player-soak` (both players' views of each other), `host-session` (`MODE=position`: the host takes a lever the frame it reaches the cab) | |
| Chat | `dedicated-admin`, `owned-items` (commands from a client), `host-session` (`MODE=chat`: a line and a whisper from the host) | |
| Pit stop, plug, module register | `shop` (a client's purchase, not the host's) | **New:** the host refuels at a pit stop with a client watching; the host pays at a module register. |
| Car delete, rerail, work train | `rerail-host` (`WHO=host`), `car-delete` (bulk delete in code, not the radio) | **New:** the host deletes a car and summons a work train with the radio while a client is online. |
| Lost and Found, containers | `lost-and-found`, `containers` (from the client) | The host's own career-manager screen and container. |
| Stage 3 loading and identity | `host-player-persist`, `rejoin`, `rejoin-crash`, `save-reload`, `inventory-persist`, `borrowed-disconnect` | The host's loading screen coming down, its chat UI, and `PlayerConnected`, checked directly. |
| Stage 3 avatars on the host | `player-soak`, `remote-held-item`, `item-release`, `train-items`, `recall` (`TAKEN=1`), `overview-pickup` | |
| Dedicated host without a service identity | the gate with `UP_ARGS=--dedicated`, `dedicated-admin`, `far-derail`, `remote-held-item` | |

The new runs fit one scenario, `host-actions`, with a `MODE` per action.

## Implementation status

As of 2026-10-20, on `split/client-server` (not yet in `beta`):

- **Stage 1: built and released** (#117, release 2026.10.19.5). Its in-game checks (the host's job
  trains stay coupled, the host's throw settles with no echo, an empty-basket register keeps its
  contents, the host's chain screw, other players' junction and time changes reach the host) are
  being written as scenarios, so they guard stage 2 now and prove stage 3 later.
- **Stage 2: mostly built.** The shared machinery (#123, #124) and, per area, the host's requests
  now handled in-process:
  - **Trains:** the host's own controls committed directly, lever authority (#125); the radio's car
    delete, rerail and work-train requests, every work-train request answered (#129).
  - **Items:** Lost and Found and container requests (#139); the server's side of the item
    managers in `Server/Items` (#133, #135, #137).
  - **World:** junctions, turntables, fast travel and sleep: the server applies other players'
    changes itself and the host's own go in-process (#126).
  - **Stations:** pit stops and plugs (#130); the host's module register answered in-process (#138).
  - **Jobs and career:** the host's booklet summon (#127).
  - **Session:** chat and commands, preferences (in-process handlers, `RegisterLocalSession` in
    `NetworkServer.cs`); the host's position read from its tracker: `ILocalPlayerTracker`
    (`Shared/Session/`), implemented by `LocalPlayerTrackerBase`, sampled by
    `NetworkServer.UpdateLocalPlayerFromTracker()` at each poll right before the host's requests
    are drained, merged and relayed as a position packet is. The host's tracker no longer sends,
    and the trains' `CurrentWorldPosition` workaround is gone: range checks use `WorldPosition`.
    The host's fast travel and sleep go through `Server/World/TimeAdvanceServerPatch`, which reads
    the server's own `FastTravelAdvancesTime`; `Client/World/TimeAdvancePatch` is remote-client
    only, so `NetworkServer` no longer sets it. Scenario: `host-session`.
- **Stage 3: built** on `split/stage-3`, not yet tested in the game: `StartServer` starts no
  `NetworkClient`. Built, in order (until the last, behind a `-mp-local-session` switch):
  - the server knows its own player itself (`NetworkServer.LocalPlayer`, set at admission;
    `AdmitPlayer` and `CompleteLoading` shared by every login), #221;
  - the other players' avatars are `NetworkLifecycle.Avatars` (`Shared/Players/PlayerAvatars`) on
    every game in a session, a dedicated host's too, dressed by the client side
    (`IAvatarPresentation`); `IsSessionRunning` beside a narrower `IsClientRunning`, #222;
  - the host's player as `Client/Session/LocalPlayerSession`, admitted by
    `NetworkServer.AddLocalPlayer` as the server starts (player 1, the login's record binding,
    `PlayerConnected` now fires for it). Its server reaches it through a `LocalPeer`, an in-process
    peer: what the server sends the host's player is read a poll later by the session, which
    subscribes only presentation packets (loading screen, chat, RPC answers, its own crew name;
    the areas add theirs in `LocalPlayerSession.<Area>.cs`), so nothing can make the host follow its
    own state. The server keeps the avatars itself and leaves the host's player out of the presence
    packets. A dedicated host has no player and leaves its loading screen itself. Scenario:
    `local-session` (C#), #223;
  - MPAPI's client API on the host is the session's (`IClientApiSession`): mods' packets reach it and
    leave it serialised, through the `LocalPeer` and `ILocalServer.ReceiveFromLocalPlayer`. Scenario:
    `mpapi-roundtrip` (C#), #224;
  - a dedicated host's console commands run with a console sender (`ServerPlayer.CreateConsole`,
    never in the player list), #225;
  - each area's half of the session, `LocalPlayerSession.<Area>.cs`, and its host call sites off
    `NetworkClient`: items #228, #232; players #229; world, stations and jobs #226, #227, #230, #234;
    trains #231, #233;
  - the switch-over: the switch, the loopback path, the host token, the service identity
    (`IsServiceHost`, `Settings.ServiceGuid`; `ServerPlayers` is now `ServerPlayers`) and stage 1's
    `HostOwnClient` with its protocol test are gone. A dedicated host has no player at all; its first
    client is player 1.

  Not done: the in-game test pass (each PR's In-game line, on a player host and a dedicated host),
  trains' removal of the host branches left in `NetworkClient.Trains.cs` (s3-trains), and the optional
  sweep of `SelfId`/`SelfPeer` to `LocalPlayer` in the server.
