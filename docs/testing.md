# Multiplayer testing

There are three layers: pure unit tests (`Multiplayer.Core.Tests`), protocol fixture tests against
the built mod (`Multiplayer.Protocol.Tests`), and two real game instances driven by dvmp
(`DevTools/`). The old in-game runtime test system (`Debugging/RuntimeTests`, the
`RuntimeConsole` and the headless `DebugRemoteClient`) was removed in favour of dvmp; what it
covered that still needs a dvmp scenario is listed in `DevTools/Scenarios/PORTING.md`.

## Test layers

### Pure unit tests

`Multiplayer.Core` targets `netstandard2.0` and must not reference Unity, Derail Valley,
LiteNetLib, or a running game process. Production code delegates canonical authority decisions
to this library, so tests exercise the same transition rules used by the host.

```bash
dotnet test Multiplayer.Core.Tests
```

The suite (NUnit, `net8.0`) mirrors the folders of `Multiplayer.Core`:

| Folder | Covers |
| --- | --- |
| `Items/` | Item authority state machine (one canonical item per NetId, one placement and possessor, revisions, owner recall, essential-item claims, stale/non-owner rejection, a 2,000-operation invariant run); wire-state projection and send suppression; inventory claim planning through a mocked `IInventoryView`; storage membership normalisation; remote hand/inventory projection; tracked-value composition and authority filtering; the temporary ID-zero adoption coordinator; Lost and Found; page-book sync; spatial stream validation; train-cab item wake; grabber recovery. |
| `Collections/` | Pending-snapshot queue ordering, duplicate/stale rejection, bounded overflow, full snapshots superseding deltas, deferred drain planning. |
| `Containers/` | Cold-container graph rules (owner-only access, revisions, idempotent deposits, transactional withdrawal, moves, slots, cycle/depth/quota limits) and the bounded binary save codec. |
| `Identity/` | NetId allocation: reserved zero, collisions, release/reuse, exhaustion without wrapping, a 10,000-operation stress run. |
| `Replication/` | Interest entry at 100 m with a three-second removal delay, dirty-vs-full-sync selection and known-item delivery. |
| `Jobs/` | Job booklet issuance index. |
| `Trains/` | Train baseline receipt and the train topology (coupling) state machine. |

Every multiplayer bug fix that changes pure transition behaviour should first gain a failing unit
test here.

### Protocol fixture tests

`Multiplayer.Protocol.Tests` targets .NET Framework 4.8 and runs the real packet serializers in
the built `Multiplayer.dll` against the installed game's Unity value types. It references
`Multiplayer/bin/<configuration>/net48/Multiplayer.dll`, so build the mod first and test with the
same configuration:

```bash
dotnet build Multiplayer/Multiplayer.csproj -c Release
```

```bash
dotnet test Multiplayer.Protocol.Tests -c Release
```

`-c Debug` for both works too. Note that both configurations deploy the mod into the game, and
Release also writes a zip to `releases/` (see [development.md](development.md)). The game's
`Managed` folder defaults to the Steam install path; override it with
`-p:DerailValleyManagedDir=...` if yours is elsewhere.

The fixtures cover item updates (`Dropped`, `Thrown`, `InHand`, `InInventory`, `Attached`,
`Create`, `Destroy`, `ObjectState`, `FullSync`, every tracked-value primitive, complete payload
consumption, envelope authority fields), item recall, spatial and continuous-motion packets,
world-item authority, Lost and Found, cold-container requests and results, job booklets and report
artifacts, and train baseline and topology packets. `ItemPacketFixtureTests` also runs the debug
packet projector self-tests (`Multiplayer/Debugging/DebugPacketProjectorSelfTests.cs`).

### Protocol manifest and fingerprint

Every build of `Multiplayer` runs `Tools/ProtocolManifestGenerator` over the built assembly, writes
`Multiplayer/protocol-manifest.json` (packet hashes, directions and protocol enums) and rebuilds
with it embedded. The manifest's SHA-256 fingerprint covers the packet list, enums and the
informational version, which includes the git commit. The host sends its fingerprint in
`ClientboundLoginResponsePacket`; the client disconnects with "Multiplayer protocol mismatch" if it
differs from its own. Host and client must therefore run builds of the same commit. The debug
packet inspector also uses the manifest to name packets.

### In-game tests with dvmp

Anything that depends on the real game (physics, inventory, trains, joining, saving) is tested in
two real game instances driven by **dvmp** (see `DevTools/README.md`): `dvmp_up` brings up a
joined host and client, `dvmp_eval` runs C# in either game, `dvmp_desync` compares the two
peers' state, `dvmp_soak` drives random item interactions and `dvmp_trace` shows which code moved
or destroyed an item. After changing `Multiplayer`, build Debug and run `dvmp_up` with
`fresh: true` so both games load the new DLL.

Repeatable scenarios live in `DevTools/Scenarios/<name>/run.sh` (exit code 0 = pass; each
script's header lists its variables). They need a running pair (`dvmp up`).

### Regression gate

`DevTools/Scenarios/gate.sh` runs every scenario below against a running pair, resetting both
players to the same spot before each run, and prints PASS/FAIL per run. It exits non-zero if
anything failed. Run it after any change touching items, joins, ticks or trains.

```bash
bash DevTools/Scenarios/gate.sh
```

```bash
ONLY="rejoin recall" bash DevTools/Scenarios/gate.sh
```

A run that logged a coupler break (an error on either peer) is flagged with `[N coupler break(s) logged]`; normal play never breaks one.

`ONLY` takes scenario or run names. Each run's output goes to `$LOG_DIR/<run>.log` (default a new
temp directory, printed at the end). `DVMP` points at the CLI (default
`DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe`).

**Identity by SteamID** ([design/server-player-split.md](design/server-player-split.md#identity)):
- Hosts verify each player's Steam auth ticket. The harness runs a host player and a client on one
  Steam account, which would give them the same ID.
- So hosts started by dvmp get `-mp-allow-unverified`, which admits every client under the GUID it
  claims, and keys the host's own player by the install's GUID (the dvmp client keeps its fixed `…c11e`).
- The gate exercises the unverified path only. `dvmp up --fresh --dedicated --verified` starts a
  host that checks tickets: a dedicated host's own client is the service identity, so the dvmp
  client can log in verified as the owner on the one account. A refused ticket needs a second
  account.

`UP_ARGS=--dedicated` (with `PAIRS`) runs the scenarios against dedicated hosts
([design/dedicated-host.md](design/dedicated-host.md)). Only the client-driven ones mean anything
there, for example `cab-controls manual-uncouple lost-and-found shop rejoin car-fire`. A dedicated
host has no player, so `WHO=host` runs, `recall TAKEN=1`, `jobs ACCEPT=host` and
`borrowed-disconnect` don't apply.

| Scenario | Gate runs | Checks |
| --- | --- | --- |
| `train-items` | default; `WHO=host MODE=throw WHEN=moving` | A mug dropped or thrown in a loco cab stays in the cab, attached to the loco at the same spot, on both peers while the host pushes the train. |
| `lost-and-found` | default | A client's personal item left behind is collected into Lost and Found by the host and retrieved into the client's inventory. |
| `recall` | default; `TAKEN=1` | A dropped essential item is recalled into its reserved slot; `TAKEN=1` recalls it out of the host's hands. |
| `containers` | default; `CONTAINER=ItemContainerBriefcase PREFAB=Flashlight` | Client deposits into its container, reopens it (contents from the host) and withdraws a stateful item; the host can't browse it. |
| `manual-uncouple` | `N=3`; `MODE=recouple N=3 LAG=150` | Client uncouples by hand (cocks, hoses, screw, chain); both peers agree and the wagon stays when the host drives off. `recouple` couples again by hand and the wagon must follow. |
| `uncouple-moving` | default; `LAST=LocoDE2 LAG=150 SPEED=10` | Host uncouples the last car on the move and shoves back into it; the client sees it roll free and agrees on where it ends up and whether it is coupled. |
| `couple-impact` | `WAGON_BRAKE=0 LAG=150 SPEEDS=3,5`; `CHAIN=1 WAGON_BRAKE=0 SPEEDS=2,4` | Coupling while the buffers bounce after a hard hit; both peers agree whether the cars are coupled and the wagon follows (or stays) when the host pulls away. |
| `coupler-desync` | `LAG=150` | The client has a loco and wagon coupled by the chain (no hoses) that the host has apart, and the host pulls the loco away. The client's cars never move or spin far beyond the host's, and both peers end up uncoupled with the wagon where the host has it. |
| `car-pool-reuse` | default | The host deletes cars and spawns cars of the same livery in the same frame, so DV's car pool reuses the objects. Every car stays networked on both peers, and each new car gets a fresh net ID that matches on the client (#69). |
| `car-delete` | default | The host bulk-deletes a loaded consist the way the yard's cleanup does and deletes each car again in the same frame. No trainset is left holding a deleted car, and DV's bulk delete doesn't throw (#84). |
| `cab-controls` | default (`LOCOS=` picks the locos, modded ones too) | On a DE2 and a DM3, every cab control that feeds a sim port is hooked on the client; flipping every toggle switch with `Use()` (the VR path, no grab) reaches the host and so does flipping back; a grabbed wipers turn on the DE2 reaches the host; no control lease is left held (#73). |
| `fps-drop` | default | The client's frame rate is cut to 6 FPS while riding a moving train; its train stays close to the host's and recovers. |
| `host-stall` | `SPEED=10 LAG=150` | The host stalls at about 2 FPS with a train at speed; no coupling, hose or cock is lost, the brake pipe holds and both agree where it stops. The train is spawned with enough clear track on the side it's pushed to (following the switches) and handbraked after the push, so it never hits anything or runs away down a grade. |
| `derail` | `pileup` with `SPEED=25 K=2 N=4 MIN_DERAILED=1 LAG=150` | A deliberate derailment: two rows on Steel Mill's B yard are rammed at 25 m/s. Every car must match (derailed, coupled neighbours, position); derailed cars must face the same way (within 10 degrees) and hold still on the client once stopped. Fails as invalid if nothing derailed on the host. |
| `client-derail` | default | A car derailed on the client only is put back on the host's track, still coupled, without touching the host's train. |
| `far-derail` | default (skips unless the host is dedicated) | The client goes to a station 4-8 km from a dedicated host's parked body. The host derails a tank car next to it, and the car must come to rest on the ground on the host, not frozen in mid-air, with the client's copy within 1.5 m. |
| `dedicated-admin` | default (skips unless the host is dedicated) | A non-admin's `/save` does nothing; made an admin, `/save` saves, and `/payfees` is refused when the wallet is short, then pays every fee once. The host saves and pauses when the last player leaves, and unpauses when they rejoin. Sets the test save's wallet. |
| `explosion` | default | A client can't set off loco or tank-car explosions; a host explosion leaves both peers with the same burnt car, derailments and positions. |
| `car-fire` | default | A client's comms radio damages a gasoline tank until it leaks on both peers, its lighter lights the leak, and the tank burns and explodes on both. A tank the host lights burns for the client too (#74). |
| `hazmat` | default | Spilled and burning hazmat tiles match on both peers; a client's own spill does nothing. |
| `rejoin` | default; `CRASH=1` | A client quits (or is killed) holding an item; the host keeps its items and drops the held one, and the client gets exactly one of each back on rejoin. |
| `save-reload` | `CYCLES=2` | Repeated host save and reload: items don't multiply, fall from the sky or drift into Lost and Found. |
| `inventory-persist` | default | A client's inventory item survives a host save and restart of both games, bound to the host, once. |
| `host-player-persist` | default (skips on a dedicated host) | The host's own player is stored by GUID like any player: the host stashes a mug, saves, both games restart, and its whole inventory comes back once at the same place. The save is in the per-player format, with the host's items as records and DV's own inventory list kept for vanilla. |
| `borrowed-disconnect` | default | A client leaving with the host's item puts it down in the world, still the host's. |
| `shop` | default | A client purchase is paid once from the shared wallet, exists with one NetId on both peers and drops the stock; a cancelled basket refunds without a local banknote. |
| `jobs` | `ACCEPT=host HANDIN=client` | A job taken, delivered, handed in and paid across both players; both agree at every step and the wallet goes up once. |
| `sandbox-cargo` | default | Loading and unloading cargo with the comms radio's sandbox loader shows the same cargo on both peers. |
| `player-soak` | `DURATION=120` | Both players move about, travel and ride a train; their views of each other never stay off by more than 3 m for more than 5 s. |

Outside the gate: `station-load` (frame times on both peers while players arrive at a far
station; fails on a frame over `MAX_MS`, default 1000).

### Unity adapter and in-game acceptance tests

Unity-facing code remains responsible for projecting an accepted pure state into real objects:
inventory slots, remote hands, storage membership, transforms, rigidbodies, renderers, and scene
lifecycle. These adapters need focused mocks where practical and two-process in-game tests where
base-game behaviour matters.

The pure state machine determines **what is allowed**. Unity adapters determine **how the accepted
state is represented**. Do not duplicate authority rules in an adapter.

Unity-only failures should gain an adapter test or a dvmp scenario, plus an observability event
that shows the failure (see [observability.md](observability.md)).

## Next high-value extractions

1. Unity adapter tests for storage and remote-hand action execution.
2. Inventory event projection behind a richer mock `IInventoryView`.
3. Scene streaming/cache reconciliation adapters.
4. Rigidbody/throw application adapters with a mock physics surface.
