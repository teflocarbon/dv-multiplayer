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
| `Shops/` | The shop catalogue fingerprint checked at join (order within and between shops, extra items, name boundaries, a fixed expected hash), the shelf seed pick (deterministic, in range, spread over sessions and shops), and the register's basket (lines per item and owner, revisions, what is owed, checkout and change, trimming lines). |
| `World/` | The track-graph fingerprint checked at join: order independence, renumbered, extra or renamed tracks, a fixed expected hash. |

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

To test without deploying (while a release is installed for a playtest), compile only and point
the tests at the compiled assembly:

```bash
dotnet build Multiplayer/Multiplayer.csproj -c Debug -t:Compile
```

```bash
dotnet test Multiplayer.Protocol.Tests -c Debug -p:MultiplayerAssemblyPath=$PWD/Multiplayer/obj/Debug/net48/Multiplayer.dll
```

The fixtures cover item updates (`Dropped`, `Thrown`, `InHand`, `InInventory`, `Attached`,
`Create`, `Destroy`, `ObjectState`, `FullSync`, every tracked-value primitive, complete payload
consumption, envelope authority fields), item recall, spatial and continuous-motion packets,
world-item authority, Lost and Found, cold-container requests and results, job booklets and report
artifacts, and train baseline and topology packets. `ItemPacketFixtureTests` also runs the debug
packet projector self-tests (`Multiplayer/Shared/Debugging/DebugPacketProjectorSelfTests.cs`).

`CodeLayoutTests` keeps the code split by role: every file under `Multiplayer/` sits in `Shared/`,
`Server/`, `Client/` or `Menus/` with its folder's namespace, and in the built DLL Server and Client
don't reference each other and Shared references neither, apart from the references listed in
`CodeLayout/role-references.allowed.txt`, which may only shrink. See
[design/code-layout.md](design/code-layout.md#the-boundary-test).

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
players to the same spot and topping the host's wallet up to $100,000 (`MONEY`; 0 leaves it)
before each run, and prints PASS/FAIL per run. It exits non-zero if anything failed. Run it after any change touching items, joins, ticks or trains.

```bash
bash DevTools/Scenarios/gate.sh
```

```bash
ONLY="rejoin recall" bash DevTools/Scenarios/gate.sh
```

A run that logged a coupler break (an error on either peer) is flagged with `[N coupler break(s) logged]`; normal play never breaks one.

**Two kinds of scenario, during the migration.** A scenario is either bash
(`DevTools/Scenarios/<name>/run.sh` and its probes) or C#: a class in
`DevTools/DvmpScenarios/Scenarios/`, run by `dvmp scenario run NAME` ([design](design/scenarios.md)).
The gate runs both the same way: a run's variables become the C# scenario's `--arg`s, and its exit
code 3 is an ERROR. When a run needs a C# scenario, the gate first builds `DevTools/DvmpScenarios`
(against the built `Multiplayer.dll`: build the mod first) and runs `dvmp scenario check`, so one
that doesn't compile stops the gate before the first run. `dvmp scenario list` shows the C#
scenarios with their options. Ported so far: `host-stall`, `cab-reload` (was `cab-controls
MODE=cab-reload`), `save-reload` and `rejoin`. A failed C# run saves both games' screenshots and log lines
into `$LOG_DIR/<scenario>-<time>-<session>/`.

`ONLY` takes scenario or run names. Each run's output goes to `$LOG_DIR/<run>.log` (default a new
temp directory, printed at the end). `DVMP` points at the CLI (default
`DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe`).

**Parallel runs.** `PAIRS` starts several host+client pairs (dvmp sessions) and every pair takes
runs from one queue until it's empty ([design](design/parallel-test-sessions.md#parallel-gate-runner)).
`PAIRS=3` is three player-host pairs (`p1`..`p3`); `PAIRS="p:2 d:1"` is two player-host pairs
and one dedicated-host pair (`d1`, started with `dvmp up --dedicated`). `REPEAT=N` queues every
run N times.

`RUNS` replaces the gate's list with your own, as a file or as lines separated by `;`, so one
invocation can mix runs that used to need separate ones:

```bash
PAIRS="p:2 d:1" RUNS=batch.txt bash DevTools/Scenarios/gate.sh
```

```text
# batch.txt: one run per line
*                                  every run of the gate's list
derail                             a run of the gate's list, by name
jobs ACCEPT=client HANDIN=host     a scenario with exactly these variables
host-actions @dedicated            only on a dedicated-host pair (@player, @dedicated, @any)
jobs-reverse: jobs ACCEPT=client   "label:" names the run in the logs and summary
cab-controls LOCOS="LocoS282A LocoDE6"
```

Runs start longest first, so a long one never starts last while the other pairs sit idle. The gate
keeps each run's last 5 passing durations in `%LOCALAPPDATA%\dvmp\durations\<run>` (`DURATIONS`
overrides the folder); runs with none recorded yet start first.

A pair runs nothing until its host and client both answer with a world loaded (`READY_WAIT`
seconds, then one `dvmp up` and as long again). A run that failed because dvmp couldn't reach its
games ("no agent matches", "no game is running in session") is reported as `ERROR`, not `FAIL`,
and retried once on a ready pair. The summary counts labels with a FAIL apart from those with
only harness ERRORs, and the gate exits non-zero on either. The serial gate marks such runs ERROR
too, but doesn't retry them.

A run marked `@kind` only goes to that kind of pair, and the gate refuses to start if `PAIRS` has
none. Other runs go to player-host pairs, or to any pair when there are only dedicated ones. The
gate's `dedicated-admin` prefers a dedicated pair. The frame-timing runs in `EXCLUSIVE` (`fps-drop`,
`host-stall`, `player-soak`) run alone at the end, each on the first pair that fits it with every
other pair down. Runs that write saves run in parallel: each pair's games keep their own copy of
DV's user data ([DevTools/README.md](../DevTools/README.md#session-user-data)), so they never
write each other's saves or your profile.

**Identity by SteamID** ([design/server-player-split.md](design/server-player-split.md#identity)):
- Hosts verify each player's Steam auth ticket. The harness runs a host player and a client on one
  Steam account, which would give them the same ID.
- So hosts started by dvmp get `-mp-allow-unverified`, which admits every client under the GUID it
  claims, and keys the host's own player by the install's GUID (the dvmp client keeps its fixed `…c11e`).
- The gate exercises the unverified path only. `dvmp up --fresh --dedicated --verified` starts a
  host that checks tickets: a dedicated host has no player of its own, so the dvmp
  client can log in verified as the owner on the one account. A refused ticket needs a second
  account.

Dedicated-host pairs (`PAIRS=d:N`, or the older `UP_ARGS=--dedicated` with `PAIRS=N`) run the
scenarios against dedicated hosts ([design/dedicated-host.md](design/dedicated-host.md)). Only the client-driven ones mean anything
there, for example `cab-controls manual-uncouple lost-and-found shop rejoin car-fire`. A dedicated
host has no player, so `WHO=host` runs, `recall TAKEN=1`, `jobs ACCEPT=host` and
`borrowed-disconnect` (its default `OWNER=host`) don't apply.

| Scenario | Gate runs | Checks |
| --- | --- | --- |
| `host-throw-settles` (C#) | default (`PREFAB=Mug`); player host only | The host throws an item itself: it settles at the same spot and revision in both games with no lease left open, the host's copy's revision never goes back, and no item packet reaches the host's own player (its session would drop it; the host-follower family, [host-local-session.md](design/host-local-session.md) stage 1). |
| `server-throw` (C#) | default (`ACTOR=host`), player host only; `ACTOR=client` | The host throws an item itself on a player's behalf, as the job re-roll throws a binned overview back out: a mug dropped in a loco cab is thrown out into the world while its drop's lease is still open. The transition indexes it in its world cell at once (no interest retirement and re-creation at rest on the client), the throw keeps its force when the actor is the host's own player, and it lands at the same spot on both peers well away from where it was thrown, no lease open. Where it lands, beside the track, the mug often goes straight through the terrain in vanilla physics ([research](research/item-terrain-tunnelling.md)): the terrain guard must put it back on the surface, so the host's copy never goes under the ground and is never rescued. |
| `train-items` (C#) | default; `WHO=host MODE=throw WHEN=moving` (player host only) | A mug dropped or thrown in a loco cab stays in the cab, attached to the loco at the same spot, on both peers while the host pushes the train. |
| `item-release` (C#) | default (`regrab`); `MODE=regrab-late`, `MODE=throw-flatcar`, `MODE=object-state`, `MODE=interact` | An item that leaves a hand: let go and taken again the next frame (a VR hand swap), it stays in the hand and no drop reaches the server; taken again a second later while the server's copy of the lantern changes, the pickup is still taken; thrown from the ground onto a flatcar, it lands on the car at the same spot on both peers and stays there as the car moves; a client's copy of a lantern lying in the world changing on its own sends nothing, but the client turning its knob is taken by the server, knob included. Each fails if the server sent a rejection correction for the item. |
| `lost-and-found` (C#) | default; `WHO=host` | A client's personal item left behind is collected into Lost and Found by the host and retrieved into the client's inventory. `WHO=host`: the host's own item and retrieval, whose requests the server must handle in-process. |
| `recall` (C#) | default; `TAKEN=1` | A dropped essential item is recalled into its reserved slot, in one step: one request, one result ([wire.md](design/items/wire.md#recall)); `TAKEN=1` recalls it out of the host's hands. If earlier runs left the client's essential in its Lost and Found or the world, it is retrieved or recalled first. |
| `red` (C#) | default (`PREFAB=Mug`) | Red for every item someone else owns ([wire.md](design/items/wire.md#naming-a-player), D3): in the client's inventory a mug owned by a player who isn't here is red, its own and a found one aren't. Each player is told its relation (the client `SomeoneElses`, `Mine`, `Nobodys`; on a player host the host's player is told the client's mug is `SomeoneElses`), and only the claimant the claim. |
| `item-request-repeat` (C#) | default | A repeated request does nothing twice ([wire.md](design/items/wire.md#one-request-one-result), F21): the client withdraws its mug from its toolbox with one request sent twice in a frame and once after its answer. One answer to the two, the same answer to the late repeat, one mug in its inventory; a new request for the same row is refused. |
| `containers` (C#) | default; `CONTAINER=ItemContainerBriefcase PREFAB=Flashlight`; `WHO=host`; `UNOWNED=1` | Client deposits into its container, reopens it (contents from the host) and withdraws a stateful item; the host can't browse it. `WHO=host`: the host's own container, whose requests the server must handle in-process. `UNOWNED=1`: the container is nobody's, and the player holding it opens it and stays its holder, still unowned (OI-CON-08). |
| `item-id-roundtrip` (C#) | default (restarts both games twice) | A mug keeps one id through a container, a save and restart, Lost and Found and another restart, and the owned-items ledger follows it. The first restart checks the bug where a withdrawn item wasn't saved ([owned-items.md](legacy/items/owned-items.md#one-id-for-life)). |
| `ledger-save` (C#) | default (restarts both games twice) | The owned-items ledger lists the client's items (Lost and Found, inventory, hand, ground, a loco's cab) and, on a player host, the host's, and keeps each in its exact home through two saves and restarts in `Multiplayer.Items` ([save-load.md](design/items/save-load.md)); a hand item comes back in a slot. A clone made with its identity copied gets an id of its own. |
| `old-save` (C#) | default (restarts both games twice) | A save from before `Multiplayer.Items` (D1, [save-load.md](design/items/save-load.md#old-saves)): its old item keys aren't read, the load says so once, items start fresh, and the next save writes only `Items`. |
| `unloadable-item` (C#) | default (restarts both games twice) | Rows that won't load (D9): a mug whose prefab is gone is `Unloadable` with its rows written back as saved, and a lantern put in its old slot keeps it; a flashlight whose state won't load comes back fresh in the client's Lost and Found (`SaveRecovery`). |
| `item-bug-container-save-refused` (C#) | default (restarts both games once) | F18, made impossible by R4: a container row the load refuses stands alone. Containers are refused until the load has run (OI-CON-05); a mug saved past its toolbox's end goes to the client's Lost and Found (`SaveRecovery`), the flashlight stays in the toolbox, and the next save holds both. |
| `fixture-no-stash` (C#) | default (`PREFAB=Mug`) | With the mug marked as a station fixture for the run, the server refuses it into the client's inventory and the client doesn't keep it; unmarked, a mug stashes as before ([owned-items.md](legacy/items/owned-items.md#fixtures), `ItemAccess`). |
| `absent-owner` (C#) | default (restarts both games once); `WHO=host` (player host only); `PREFAB=Flashlight` | A personal mug owned by a player who isn't here keeps that owner when the client (or the host's player) picks it up and drops it (an identity with no session, not the taker), and its Lost and Found entry is bound at load after a restart without its owner ([owned-items.md](legacy/items/owned-items.md#the-ledger-and-the-records)). |
| `offline-lost-and-found` (C#) | default | A player who isn't here owns two mugs, one on the ground and one in a loco's cab: nothing is collected while the client is near, and both go to their Lost and Found once it walks away. The online client's own cab mug stays in the cab ([owned-items.md](legacy/items/lost-and-found.md#what-gets-collected)). |
| `owner-offline` (C#) | default (`LEAVE=quit`); `LEAVE=crash`; `WHERE=cab` (rejoins the client once) | A client that leaves keeps the mug it left (its identity, now one with no session, not nobody). On a player host, once nobody is near, the mug goes to its Lost and Found under its own id, and not into vanilla's list behind the server's back (item-review L4). On a dedicated host nobody is connected then, so it waits where it is. After the rejoin the client, its identity in session again, retrieves it. Its stashed mug is back in its inventory, still its own ([owned-items.md](legacy/items/lost-and-found.md#what-gets-collected)). |
| `rekey` (C#) | default (rejoins the client twice) | The host resolves the client's next login to a Steam-form ID with its install GUID as the old one, as a verified login has (`InstallGuidRekey`). Under the new ID the client keeps its owned-items list, ledger entries, essentials (none issued again), starter kit and Lost and Found; nothing is left under the install GUID; its stashed mug is in its inventory and it retrieves the lost one. `MERGE=true`: the Steam ID already has a mug in the slot the stashed mug uses, so the two identities merge (OI-ID-19): the Steam ID keeps its mug there and the stashed one goes to its Lost and Found, nothing skipped or left behind ([ledger.md](design/items/ledger.md)). The run moves the data back to the install GUID afterwards ([server-player-split.md](design/server-player-split.md)). |
| `car-delete-owner` (C#) | default (`OWNER=offline`, rejoins the client once); `OWNER=online`; `OWNER=absent` | A loco deleted with a mug in its cab sends the mug to its owner's Lost and Found: the client's, whether it is connected or gone at the delete, or that of a player who was never here. Before, the delete destroyed an offline owner's mug (item-review L1). |
| `lost-and-found-many-owners` (C#) | default (restarts both games once) | Mugs of the client, the host's player (player host) and two absent players are collected. Each owner's Lost and Found grows by exactly their mugs. After a save and restart each mug is back once, in its own owner's Lost and Found, bound at load, its owner in session or not as before. The ledger agrees, with no audit problems. |
| `offline-sweep-mid-save` (C#) | default (restarts both games once) | The host saves over and over while Lost and Found sweeps up an absent player's mugs, and the games restart on the last save. Each mug comes back once, theirs, either in the world or in their Lost and Found under its own id, with no ledger audit problems. The sweep then finishes. |
| `rejoin-mid-sweep` (C#) | default (`LEAVE=quit`); `LEAVE=crash` (rejoins the client once) | A client leaves mugs on the ground and rejoins at once, while they are swept into Lost and Found. Each mug ends up once, still its own with its new number, in the world or in its Lost and Found. The ledger agrees, and the client retrieves a collected one. |
| `deposit-refused` (C#) | default (`OWNER=absent`); `OWNER=host` (player host only); `OWNER=essential` | The client can't deposit someone else's mug into its own toolbox (`deposit-item-not-owned`). The mug stays in its inventory, its owner's. The client's own mug deposits ([owned-items.md](legacy/items/owned-items.md#permissions)). `OWNER=essential`: the client's own essential, from its inventory, is refused too (`essential-not-in-containers`, item-review L7) and stays there. |
| `starting-items` (C#) | default (rejoins the client four times) | The server makes the client's starting items ([owned-items.md](legacy/items/owned-items.md#starting-items)): every essential once, the same ids after a rejoin; a destroyed comms radio made again under a new id, born `Issued`; one in the client's Lost and Found counts as had; with its starter-kit marker forgotten, the seven kit items come at the rejoin, born `StarterKit`, and the marker is set again. |
| `owned-list` (C#) | default | The client's owned-items list ([owned-items.md](legacy/items/owned-items.md#retrieval), PR 6a): its mugs in its inventory, on the ground, in a loco's cab, in Lost and Found and in its toolbox are each listed once, worded by where they are (the cab one names the loco), and a destroyed one isn't. The rows the client receives match the server's. |
| `owned-retrieve` (C#) | default; its host-hand part runs on a player host only | Retrieval from the owned-items list (PR 6b): the client's ground, cab and boxed mugs go to its Lost and Found (for the boxed one, the whole toolbox, the mug still inside), retrieving one from its own inventory is refused (`nothing-to-retrieve`), and on a player host its lantern is taken back out of the host's hand into its Lost and Found. Every retrieval, a refused one too, is followed by a fresh owned-items list ([wire.md](design/items/wire.md#one-list)). |
| `inventory-slots` (C#) | default; `WHO=host` (player host only, no rejoin); `RESTART=1` (a save and restart instead of the rejoin; with `WHO=host`, the host's own player) | Every inventory item has an exact slot on the server ([inventory.md](design/items/inventory.md)): the client's items go into chosen slots, two are moved and swapped as `MoveSlot` requests, the radio leaves its claim for the belt and comes back, and a move into the reserved slot is refused. After a rejoin everything is in the same slots on the server, in the ledger and on the client. |
| `hotbar-cycle` (C#) | default (every vanilla item a client stands in); `PREFABS=Lantern,Mug`; `WHO=host` (player host only); `MAX_MS=100` | Cold inventories on the client (D14, [inventory.md](design/items/inventory.md)): each item is picked up, stashed into a free hotbar slot through the hotbar (`GrabberStashingHandler.StashToggle`, as the key does), taken out and stashed again. It's a stand-in with no object while stored, on a client and (`WHO=host`) for the host's own player; taken out, its object is there in that frame with the saved state it went in with, and the frame stays within `MAX_MS`. The server and ledger have it in the slot. Items vanilla won't take into an inventory are noted. |
| `takeout-refused` (C#) | default (`PREFAB=Lantern`) | The server refuses a client's take-out (D14): the host refuses the take or drop request a take-out of a stored lantern makes, to the hand and as a drop, each once without a correction (rolled back from the client's pending take-out) and once with one. Each time the object goes and the stand-in is back in its slot, which the server and ledger still have; unrefused, the take-out goes through. |
| `stash-state` (C#) | default (rejoins the client once); `PREFABS=lighter`; `MAGAZINES=0`; `WHO=host` (saves and restarts both; player host only) | Saved state through the inventory (D14, OI-COLD-13): a lighter with its lid open, duct tape with two uses gone, a lantern with its wick turned down, and each vanilla magazine loaded with its ammo (a paint can, a spool) are stashed; the stand-in has the state the object had, a take-out has it in its first frame (with the ammo inside) and still after a second in the hand (the server's copy, made afresh, doesn't overwrite the holder's), and after a rejoin the row brings it down again. A loaded magazine stays an object in its slot with its ammo (OI-CON-16), checked on the host; a client's are only noted (known bug CN1). |
| `join-rows` (C#) | default (rejoins the client once; `PREFABS=Lantern,Mug,lighter`); `WHO=host` (saves and restarts both; player host only) | A rejoined client draws its inventory from rows (D14): every stashed item is a stand-in in the slot it left in, with no object made for it in the new game (no Create, no make from its row, no take-out, from the client's debug events), and the server and ledger agree. |
| `cold-homes` (C#) | default (rejoins the client once) | Cold and live homes (D12, [inventory.md](design/items/inventory.md)): a lantern in the client's inventory is data on the server, with no object there; a move while it's data makes it again, moves it and lets it go; a move the server refuses (a stale revision) rolls back on the client. When the client leaves holding a mug, the lantern is a cold row in its slot and the mug one in a free slot, the ledger's homes unchanged; at the rejoin both come back in those slots. |
| `ledger-rows` (C#) | default; its access and retrieval parts run on a player host only | The ledger's rows a game proves ([ledger.md](design/items/ledger.md)): the one-home check with an item's object inactive; `Ledger.Dump`; a copy re-minted, logged (`item.duplicate-persistent-id`) and born `Duplicate` from the original; an admin give born `Granted`; the issuer making nothing while the ledger isn't loaded and trying again after; and on a player host: someone else's recall, retrieval and take refused by `ItemAccess`, `SendToLostAndFound` refusing an item with a player, the host's own starting items, the owned list's hand and another player's inventory, and retrieval from another player's inventory into Lost and Found (`RetrievedByOwner`). |
| `ledger-repair` (C#) | default; the host's own slot on a player host only | The one-home repair a Release build makes (D7, [ledger.md](design/items/ledger.md)), called directly after breaking the ledger on purpose: a mug on the ground goes into the client's slot; one in its inventory, home Lost and Found, is collected; one in Lost and Found, home the client's slot, is recalled; one in its inventory, home the ground, goes to Lost and Found; one on the ground, home an offline player's slot, becomes a cold row; on a player host, one goes into the host's own slot, and from there into the client's. The one-home check holds after each. |
| `save-homes` (C#) | default (restarts both games twice) | The homes a save keeps that `ledger-save` doesn't ([save-load.md](design/items/save-load.md), "Loading"): the client carries an absent player's mug (borrowed when the server saves, BR SL1), an unowned mug and a scene-authored item (one that isn't office or shop stock, which the scene stocks again once taken: the nearest is a fire extinguisher), has an EOT lantern of its own (given as the admin grant gives one: shops are paused) on a flatbed's coupler, and a mug of its own in a loco's cab and a gadget of its own installed on it; the save is written without the loco in vanilla's cars, as a car that didn't load leaves it. After the restart the borrowed mug is in its owner's Lost and Found (`SaveRecovery`), the cab mug in the client's (`SaveRecovery`) and the gadget too (`GadgetDestinationMissing`); the unowned mug and the scene item are back in their slots, once (the scene doesn't make the item again), and the lantern is attached to the flatbed, the client's, through a second save and restart. |
| `leave-full-inventory` (C#) | default (rejoins the client five times) | No free slot ([inventory.md](design/items/inventory.md), "Leaving", "Joining"). The client's slots off the belt are full and its compass is gone: at the rejoin the new one goes to its Lost and Found (`InventoryOverflow`), not onto the belt. With the belt full too, it leaves holding a lantern of its own (to its Lost and Found, `InventoryOverflow`) and, next, a mug nobody owns (at its feet, no cold row); an unowned mug in a slot is a cold row and comes back there. It leaves with an absent player's mug in a slot: it goes to the owner's Lost and Found (`BorrowerLeft`), is no row of the client's, and the next save has it there. Last, while it's away with every slot full, one cold mug of its own loses its slot as an identity merge leaves one (OI-ID-19, the only game path to a cold row with no slot): at the rejoin it goes to its Lost and Found by way of its feet (`PlayerItems.GoLive`, `InventoryOverflow`), and nothing is left in no slot. |
| `owned-list-shelf` (C#) | default (player host only: the host's own player); `RELEASE=throw` | An owned mug dropped on a shelf, the floor of the nearest shed (an `ItemStaticParent`): the server has it on that shelf, the ledger's home is `Shelf`, and the owner's row reads "on a shelf". A client's drop is `item-bug-client-shelf` (SH1); `SEEN=true` also checks the client sees it there, `item-bug-shelf-unseen` (SH2). |
| `item-lineage` (C#) | default | Lineage (PR 7): a server step replaces the client's mug with a lantern, as a server-side swap would. The lantern is the client's, born Replaced from the mug, and the mug's entry ends replacedBy it. The client's list has the lantern, not the mug. A plainly destroyed mug ends DestroyedByGame with no replacement. |
| `client-fall-rescue` (C#) | default (`PREFAB=Mug`); also on a dedicated host | An item under the map next to a client (the host's copy 4 m under the client's ground, with the server's own scan turned off) is reported by the client's game and rescued by the server; both games end with it on the ground at the same spot (#37). |
| `manual-uncouple` (C#) | `N=3`; `MODE=recouple N=3 LAG=150` | Client uncouples by hand (cocks, hoses, screw, chain); both peers agree and the wagon stays when the host drives off. `recouple` couples again by hand and the wagon must follow. |
| `host-chain-screw` (C#) | default (player host only) | The host's own player works a coupling (the host-follower family, stage 1): the host uncouples a DE2 and a wagon by hand and couples them again, loosens and tightens the screw twice, and the mod's screw postfix runs on the other car's chain (its Start path). On both games the cars are coupled in one trainset with the chain tight and the same chain states, every chain's screw button and knob takes input, and on the host neither car's end is left marked in use by its server half. |
| `host-coupling-tools` (C#) | default (`LOCO=LocoDE2`); player host only | The host's own player works a coupling between two DE2s: the MU cable on and off, both cocks shut, the hose parted and joined, the cocks opened, then vanilla's coupling menu uncouples and couples, plain and advanced. After each, within `SETTLE` s, the client has the same coupling (coupled, hose, both cocks, MU, chain tight), and a menu couple ends tight. The server commits these by watching its own world (stage 3: a host with no client reports nothing). |
| `uncouple-moving` (C#) | default; `LAST=LocoDE2 LAG=150 SPEED=10` | Host uncouples the last car on the move and shoves back into it; the client sees it roll free and agrees on where it ends up and whether it is coupled. |
| `couple-impact` (C#) | `WAGON_BRAKE=0 LAG=150 SPEEDS=3,5`; `CHAIN=1 WAGON_BRAKE=0 SPEEDS=2,4` | Coupling while the buffers bounce after a hard hit; both peers agree whether the cars are coupled and the wagon follows (or stays) when the host pulls away. |
| `coupler-desync` (C#) | `LAG=150` | The client has a loco and wagon coupled by the chain (no hoses) that the host has apart, and the host pulls the loco away. The client's cars never move or spin far beyond the host's, and both peers end up uncoupled with the wagon where the host has it. |
| `car-pool-reuse` (C#) | default | The host deletes cars and spawns cars of the same livery in the same frame, so DV's car pool reuses the objects. Every car stays networked on both peers, and each new car gets a fresh net ID that matches on the client (#69). |
| `car-delete` (C#) | default | The host bulk-deletes a loaded consist the way the yard's cleanup does and deletes each car again in the same frame. No trainset is left holding a deleted car, and DV's bulk delete doesn't throw (#84). |
| `radio-tools` (C#) | default, and the scenario `radio-tools-host` (player host only) | The comms radio's car tools by a player through the game's own radio modes, the host's own player too (its requests handled in-process): a DM3 spawned near that player is deleted with the radio's request and is gone on both games; a work-train summon on a track that doesn't exist (sent by the client in both runs: the radio refuses it before sending) is answered rather than left to time out; one where the DM3 stood is answered Success and the loco is on both games. |
| `cab-controls` (C#) | default (`LOCOS=` picks the locos, modded ones too); the scenarios `cab-controls-authority` (player host only) and `cab-controls-no-grab` | On a DE2 and a DM3, every cab control that feeds a sim port is hooked on the client; flipping every toggle switch with `Use()` (the VR path, no grab) reaches the host and so does flipping back; a grabbed wipers turn on the DE2 reaches the host; no control lease is left held (#73). With `LOCOS="LocoS282A LocoDE6"`: valves, the cylinder cocks and the lubricator (S282) and windows (DE6) set with no grab and no `Use()`, as VR and clicks reach the mod, reach the host and set back. `cab-controls-authority`: both players in a DE2's cab, the client holds the wipers and turns them, the host's cab has them blocked, can't take them and shows the client's value; then the host holds them (its request handled in-process) and turns them, and the client's cab has them blocked, can't take them and shows the host's value; no lease is left held. `cab-controls-no-grab` (#159): an S282A held still with its tender, both players in its cab (only the client on a dedicated host); the client sets the compressor, dynamo, lubricator and cylinder-cock valves with no grab again and again, the cab copies and the outside ones (`STEPS=`), and each change reaches the host; stepped off out of reach, its turn of the outside valves goes back to the host's value and the host keeps it; back aboard, its next change reaches the host; the loco hasn't moved. Were `cab-controls MODE=authority` and `MODE=no-grab-sequence`. |
| `cab-reload` (C#) | default (`LOCOS=`), run `cab-controls-reload` | The client in each of `LOCOS`' cabs (default S282A, DE2, DM3): the cab reloads and a lever grabbed half a second later is hooked and takes its lease (required on a cab with a control the old ready check waited 2 s for; the run lists which cabs have one); the cab left unloaded 2.5 s logs no "failed to load an interior" (logged on every such unload before 2026-10-20); three quick reloads, each unloaded mid-hook, leave no hook exceptions and every control hooked. Was `cab-controls MODE=cab-reload`. |
| `settle-window` (C#) | default | A car-body control the client sets with no grab 0.3 s after a host resync lands (inside the 1.5 s settle window) reaches the host and survives the next resync; reloading the car's interior 4 times asks for at most one resync per car per 10 s. `LOCO=LocoDE6 PORT=windowL1` uses a cab window (playtest 2: oiling caps reopened). |
| `compressor-signal` (C#) | default | A DE6 with its engine running and an empty main reservoir builds air on host and client after its compressor activation signal is set to 0, first on the client and then on the host: the host keeps it matched to the brake system (playtest 2: a DE6 with no brakes). |
| `stoker-coal` (C#) | default | Stoker feed sends no AddCoal or TenderCoal from either game, and one shovelful on the client, into the firebox or out of the tender, sends exactly one. With a Custom Car Loader stoker loco installed (`STOKER=auto`) its real stoker runs; otherwise the firebox's coal input and the tender's coal pile are written the way a stoker writes them, on an S282 (playtest 2: stoker fireboxes overfull; two coal-pile packets a tick per stoker tender). |
| `fps-drop` (C#) | default | The client's frame rate is cut to 6 FPS while riding a moving train; its train stays close to the host's and recovers. |
| `host-stall` (C#) | `SPEED=10 LAG=150` | The host stalls at about 2 FPS with a train at speed; no coupling, hose or cock is lost, the brake pipe holds and both agree where it stops. Only breakage fails: the client's cars snapping, during the stall or after it, is printed as a note (worst step and when) and never fails the run. The train is spawned with enough clear track on the side it's pushed to (following the switches) and handbraked after the push, so it never hits anything or runs away down a grade. |
| `derail` (C#) | `pileup` with `SPEED=25 K=2 N=4 MIN_DERAILED=1 LAG=150` | A deliberate derailment: two rows on Steel Mill's B yard are rammed at 25 m/s. Every car must match (derailed, coupled neighbours, position); derailed cars must face the same way (within 10 degrees) and hold still on the client once stopped. Fails as invalid if nothing derailed on the host. |
| `client-derail` (C#) | default | A car derailed on the client only is put back on the host's track, still coupled, without touching the host's train. |
| `derail-drag` (C#) | default (`SPEED=6` m/s, `DRAG_SECONDS=15`, `RATIO=3`, `FLOOR=0.3`) | A derailed car dragged between on-rail cars (the host derails the middle of three and pulls by the loco) follows as smoothly on the client as on the host: the gap at its coupling to the loco changes no faster than `RATIO` times the host's (RMS, at least `FLOOR` m/s), and no coupling breaks. The 2026-10-19 playtest judder. |
| `rerail` (C#) | default (`WHO=client`), `WHO=host` as `rerail-host`, `WHO=host-radio` as `rerail-host-radio` | The host derails a DE2 beside the client and shoves it off the track; at rest, it's rerailed 12 m along its track, by the client's comms radio (a request to the host), by the host itself, or by the host's player's radio (its request handled in-process; player host only). For 8 s from 2.5 s after the rerail the client's car stays within 0.5 m of the host's and under 0.5 m/s, and the host's doesn't move (playtest 2's FEF-3 slid off and was flung). |
| `far-derail` (C#) | default | The client goes to a station 4-8 km from the host's body (its player, or a dedicated host's parked body). The host derails a tank car next to the client, and the car must come to rest on the ground on the host, not frozen in mid-air, with its whole cell (Near and Far tiles, terrain) loaded on the host and the client's copy within 1.5 m. |
| `far-derail-embankment` (C#) | default | The client goes to a station 4-8 km from the host's body, and the host must have terrain and the Far scenery tile under the client's player. The host derails a tank car there and moves it off the track, 3 m above its terrain. It must land and come to rest on the host, not frozen in mid-air, with the client's copy within 1.5 m. |
| `dedicated-admin` (C#) | default (dedicated host only: a harness error on a player host) | A non-admin's `/save` does nothing; made an admin, `/save` saves, and `/payfees` is refused when the wallet is short; then the client's fees-screen request pays them once, cheapest first until the insurance quota, and insurance clears the rest. The host saves and pauses when the last player leaves, and unpauses when they rejoin. Sets the test save's wallet. |
| `explosion` (C#) | default | A client can't set off loco or tank-car explosions; a host explosion leaves both peers with the same burnt car, derailments and positions. |
| `car-fire` (C#) | default | A client's comms radio damages a gasoline tank until it leaks on both peers, its lighter lights the leak, and the tank burns and explodes on both. A tank the host lights burns for the client too (#74). |
| `hazmat` (C#) | default | Spilled and burning hazmat tiles match on both peers; a client's own spill does nothing. The games are read one after the other while a fire spreads, so a mismatch is read again for up to 4 s before it fails. |
| `rejoin` (C#) | default; `CRASH=1` | A client quits (or is killed) holding an item; the host keeps its items (as cold rows, D12), and the client gets exactly one of each back on rejoin. |
| `rejoin-autocouple` (C#) | default; `PARKED_CHAIN=0` | A loco spawned beside a parked car auto-couples to it, the parked car's chain switched on (so vanilla hooks both chains on); both peers agree on the coupling, chains included, and after a client rejoin the train baseline applies and they still agree (#285). |
| `save-reload` (C#) | `CYCLES=2` | Repeated host save and reload: items don't multiply, fall from the sky or drift into Lost and Found. Each cycle restarts the pair as the same kind of host (the bash version came back as a player host on a dedicated pair). |
| `inventory-persist` (C#) | default | A client's inventory item survives a host save and restart of both games, bound to the host, once. |
| `gadget-save` (C#) | default | An installed gadget stays installed on its car across a host save and restart: once, owned by the client, not in Lost and Found, never a loose item on the client. One installed on the ground stays installed on the world (`Installed(:global:)`, OI-GAD-10), the client's too, and a deleted car's gadget comes off still its owner's. |
| `gadget-join` (C#) | default | A player joining gets every gadget installed on the trains as the server has it: the server builds a loco with a simple gadget, a switch and light wired and soldered, a bolted plate with a gadget mounted on it and a hole from a removed plate; after the client rejoins, its gadgets match the server's (UIDs, pose, power, soldering, wires, mount points, mount, settings, holes), each installed once. |
| `gadget-settings` (C#) | default | A setting changed on an installed gadget reaches every game: the server puts a switch wired to a light and an amp limiter on a car and the client rejoins; the client, riding the car, turns the switch on and the server's switch and light follow; the server turns the amp limiter and the client's follows; the client, 40 m away, turns the switch off and the server refuses it, putting the client's back. Also run with `LAG=250` (gate: `gadget-settings-lag`). |
| `gadget-buttons` (C#) | default | Installed gadgets' buttons and running state are the same in every game: the server spawns a DE2 by the client with a distance tracker, an ATS, a defect detector, an alternating controller wired to two lights and a beacon wired to a switch, all soldered and powered, and the client rejoins. The client starts and acknowledges the distance tracker and the server's follows; the server turns the ATS interval and the client's countdown follows; the server derails the loco and the client's snooze snoozes the server's detector; the client's alternating controller phase follows the server's; the beacon shows the same angle in both games (within 45°, a tick or two of clock). Also run with `LAG=250` (gate: `gadget-buttons-lag`). |
| `gadget-knobs` (C#) | default | Gadget knobs take leases like cab levers: the server puts a switch wired to a light and an amp limiter on a car and the client rejoins and rides it. The client grabs the switch's knob: the server records it as the holder, and its turn reaches the server's switch; letting go frees it. On a player host the host's player takes the knob: the client's knob is blocked and its HUD change is refused and put back, until the host's player lets go. With nobody holding it, a HUD change goes through. Amp limiter modes 99 and 2.5 are refused and change nothing in either game. Also run with `LAG=250` (gate: `gadget-knobs-lag`). |
| `gadget-ats` (C#) | default | A server-run ATS wins over the player holding the throttle: the server puts a soldered ATS on a DE2 by the client and powers it; the client rejoins, rides it and holds the throttle at 0.6. The server pushes the loco and lets the ATS run out: the client's throttle lease is taken, the server's throttle steps down and its train brake up in steps (no cab loaded on the server), the client's controls follow and it no longer holds the throttle. The client's acknowledge resets the server's countdown. |
| `gadget-antislip` (C#) | default | A server-run anti-slip eases the throttle and sands: the server puts a soldered anti-slip on a DE2 by the client and powers it; the client rejoins, rides it and holds the throttle at 0.8. A server step makes the wheels slip for 3 s: the client's throttle lease is taken, the server's throttle steps down, its sander runs and its anti-slip is active; the client's anti-slip lamp and sander follow. |
| `gadget-dpu` (C#) | default | Wireless MU through DPUs: the server puts a soldered DPU on two DE2s (one by the client transmitting on channel 3, one by the host's player receiving on it) and powers both; the client rejoins, rides the transmitter and opens its throttle to 0.7. The receiver's throttle follows on the server and in the client's game, and is blocked in the client's game. |
| `gadget-control-panel` (C#) | default | A control panel drives its car through the server: the server puts a soldered panel in a DE2 by the client and powers it; the client rejoins and rides it. Three pushes of the panel's throttle joystick move the server's throttle up by steps (not to full) and the client's follows. On a player host, while the host's player holds the throttle lever, the joystick does nothing. With a 50 % fault chance both games fault on the same clock steps. |
| `host-player-persist` (C#) | default (player host only) | The host's own player is stored by GUID like any player: the host stashes a mug, saves, both games restart, and its whole inventory comes back once at the same place. The save is in the per-player format, with the host's items as records and DV's own inventory list kept for vanilla. |
| `borrowed-disconnect` (C#) | default (`OWNER=host`, player host only); `LEAVE=crash` (player host only); `OWNER=absent`; `OWNER=absent HOLD=hand`; `OWNER=absent LEAVE=crash` | A client leaving with someone else's item (the host's, or that of a player who isn't here) sends it straight to its owner's Lost and Found, still theirs, from its inventory or its hand, whether it quits or crashes (owned-items.md, "Leaving"). |
| `host-actions` (C#) | default (`WHO=client`); `WHO=host` as `host-actions-host` (player host only). `MODE=junction`, `turntable`, `fast-travel` or `sleep` runs one action | One player throws the junction nearest it, turns the nearest turntable half round, fast-travels and sleeps. After each, both games agree: the same branch, switched exactly once on each game; the same turntable rotation; each clock skipped forward once; the same shared sleep cooldown. The host logs no warning about a relayed junction, turntable or time skip reaching its own client. |
| `server-world-changes` (C#) | default; player host and dedicated | A junction and a turntable the server's own code moves, with nobody's request behind it (what the AI add-on does on a dedicated host), reach the client once ([world-sync.md](world-sync.md), junctions and turntables): the server switches the junction nearest the client to another branch and turns the nearest turntable half round by setting its target; the client's junction is on the same branch having switched exactly once, the host's switched once, and the client's turntable has the host's rotation. Both are put back afterwards. |
| `pit-stop` (C#) | default (`WHO=client`); `WHO=host` as `pit-stop-host` (player host only); `MODE=yank` as `pit-stop-yank` (`WHO=client` only) | Both players at a service station. One pulls a resource lever and lets it go, then picks up a plug and puts it back home. The other game shows the lever held by another player while it's pulled and free after. The server records the plug as held by that player and then by nobody, and the client shows the same. With `MODE=yank`: the client walks off holding the plug; its avatar carries it on the host until the hose yanks it out of the hand (the server's rope physics): the server has nobody holding it, the client's plug is moved by the yank the host sent, and both games agree whether it ended up snapped home. Every run starts from the plug at home and held by nobody: if it isn't (a yank run before left it on the ground), the server docks it home and tells the client. |
| `host-register-empty-basket` (C#) | default (player host only) | A service station's register (with modules) holding $50 with an empty basket, both players at it. The host pays $20 in: neither game's register changes, the host's wallet keeps the money, and its request finishes on the server's answer (under 1.5 s, not the timeout). The host presses Buy, then the client: both are refused, and both games keep the $50 (a refused buy or a refusal used to empty a register on one side: the host-follower family, [design/host-local-session.md](design/host-local-session.md)). |
| `host-session` | default; `MODE=chat`, `prefs` or `position` runs one part (player host only) | The host's own player served in-process ([design/host-local-session.md](design/host-local-session.md), stage 2). A chat line and a `/w` whisper from the host reach the client; the host changes its character model and the server and the client's avatar of the host follow (restored after); the host jumps into a DE2's cab from 60 m and takes a lever in the same frame, and gets it (the server reads its position from its tracker right before handling its requests), with the server's copy of the host where its body is and no lease left. |
| `local-session` (C#) | default; player host and dedicated | The host's own player in stage 3 of [design/host-local-session.md](design/host-local-session.md): on a player host it is player 1, an in-process session with a `LocalPeer` and no client of its own, its loading screen is down and its chat window built; a dedicated host has no player, no session and no chat window, and its first client is player 1. Either way each game has an avatar for the other's player and none for its own. |
| `host-chat` (C#) | default; player host and dedicated | What the server sends the host's own player reaches it ([design/host-local-session.md](design/host-local-session.md), stage 3). On a player host: the client's chat line and its `/w` whisper show in the host's chat, the host's `/help` is answered to the host alone, and the host's `/crew` on itself shows in its own player list, on the client's avatar of the host and in the client's player list (cleared afterwards); the host's player list has the client's row with its ping, and its avatar of the client has a name tag. On a dedicated host: the client's `/help` is answered. The other direction (the host's chat reaching the client) is `host-session`. |
| `mpapi-roundtrip` (C#) | default; player host and dedicated | A mod's own packets through MPAPI, registered and sent as a mod does: the host's broadcast reaches the client's handler and the host's own client handler; the client's packet and the host's own reach the server's handler with the right sender. The host's client API is its in-process session's (serialised both ways, as from the network). On a dedicated host only the client's side is checked. |
| `shop` (C#) | default; `WHO=host` (player host); `LAG=250` | A player picks up the scanner, scans, pays from the shared wallet and presses Buy: the wallet pays once, the item has one NetId on both peers and is owned by the buyer, born `Purchased`, the stock drops, both registers end empty; a paid and cancelled basket's cash is one banknote on both. |
| `shop-basket` (C#) | default; `LAG=250` | The shared basket: after the client's scan the server's and client's registers match (units, cash, revision); on a player host the host's player scans a second item and the client buys both, each owned by whoever scanned it (on a dedicated host the client scans both). |
| `shop-restock` (C#) | default | The client buys an item and puts it through a toolbox (deposit, withdraw): the host's stock stays down. Destroying the withdrawn copy on the host then restocks it. |
| `shop-scanner` (C#) | default | The client takes the shop's scanner and points it at a shelf item: the server has its aim; on a player host the host's game shows the client's laser and the scanner's screen naming the item, clears it when the client points at nothing, and hides the laser when the client lets go. Needs the scanner at the shop (a fresh career, until shops PR 2c). |
| `shop-cash` (C#) | default; `LEAVE=false` | Needs a career save. The client pays into a shop register without buying: the save counts the till's cash as the wallet's; walking 30 m away returns it to the wallet on both games, the basket stays; a minute 300 m away empties the basket. |
| `shop-licence` (C#) | default | Needs a save without the MU licence. A client sees the wireless MU controller as not for sale and its scan is refused; once the host acquires the licence the client sees it for sale and its scan goes in. |
| `shop-predict` (C#) | default (`LAG=250`) | Every packet to the client delayed 250 ms: the client's scan is on its register within 3 frames and still there once the server has answered; a scan without the scanner in hand, which the server refuses, shows at once and rolls back within 2 s. |
| `shop-layout` (C#) | default | Both players at the shop: every shelf display at a shop within 1 km of the client is in the same place on host and client, within 1 cm. |
| `jobs` (C#) | `ACCEPT=host HANDIN=client` | A job taken, delivered, handed in and paid across both players; both agree at every step and the wallet goes up once. |
| `host-job-train-coupled` (C#) | default (player host only) | The host's job trains stay coupled. FM's job generator spawns fresh jobs, the host takes one, a shunter rolls onto its train and the host couples it, then the host loads the loco's cab 3 times and boards it. For 60 s the train's coupling stays as it was on both games and they agree, and the host's own client is never sent a train packet (the "cargo containers randomly decouple" family: a snapshot replied to the host's cab load uncoupled its consist). `HOLD`, `RELOADS`, `LOCO`, `STATION`. |
| `overview-pickup` (C#) | default (`LAG=250`) | Job overviews generate with both players at the office, and the host nudges each one the client was sent before its acknowledgement arrives. Every overview the client has is acknowledged on the server, and the client can pick three up (playtest 2: refused overview pickups). |
| `job-reroll` (C#) | default (`WHO=client`); `WHO=host`, player host only | A player drops an offered job's overview in the office bin with nobody on its cars: on both peers the job expires, its overview and cars are gone at once, and the station offers a replacement, the same jobs on both (up to three jobs are binned, since vanilla's generator can find none). Then the client boards a car of another job and the player bins its overview: the paper flies back out of the bin and lands at the same spot on both peers, and the job and its cars stay. |
| `booklet-summon` (C#) | default (`WHO=client`); `WHO=host` as `booklet-summon-host` (player host only); `STATION=FM` | The validator's reprint button ([jobs.md](jobs.md), "Reprint"): a player takes a transport or empty-haul job at the office, carries its booklet 60 m away and leaves it, comes back and presses reprint. On both games the booklet is back at the printer within 15 s with the same NetId, and the job has as many booklets as before (moved, not printed again). The job is abandoned afterwards. |
| `station-loco-spawn` (C#) | default (`WHO=client`); `WHO=host` as `station-loco-spawn-host` (player host only) | A station's loco spawner spawns when a player comes near, decided on the server from every player's position: the host picks a spawner nobody is near (one with no loco on its track if there is one, else it clears the track), a player walks to its spawn track, and within 20 s the host's track has cars, the client has the same ones, and 5 s later no second set has come. The spawned cars are deleted afterwards. |
| `remote-held-item` (C#) | default; meant for `UP_ARGS=--dedicated` | A client picks up a lantern, lights it and puts it in its inventory; after each step the host records it in the client's hand or inventory, never the server's own player's (playtest 2: lanterns vanished from VR hands on a dedicated host). |
| `owned-items` (C#) | default | An essential item owned by an absent player whose saved number is now the client's doesn't count as the client's, and `/give` gives the client one (playtest 2: a lost radio blocked `/give` and DV's safeguard). |
| `sandbox-cargo` (C#) | default | Loading and unloading cargo with the comms radio's sandbox loader shows the same cargo on both peers. |
| `player-soak` (C#) | `DURATION=120` | Both players move about, travel and ride a train; their views of each other never stay off by more than 3 m for more than 5 s. A dedicated host has no player: it pushes the loco and watches, and only its view of the client is judged. |

Outside the gate: `station-load` (C#; frame times on both peers while players arrive at a far
station; fails on a frame over `MAX_MS`, default 1000).

### GNS pairs

Cloud test pods (RunPod, the game under Wine) can't run Steam: Steam blocks sign-ins from data
centres, and allows one playing session per account. So a **Debug build** of the mod can run on
Valve's open-source [GameNetworkingSockets](https://github.com/ValveSoftware/GameNetworkingSockets)
(GNS), the library Steam's own sockets come from, instead of Steam: `dvmp up --transport gns`
(MCP: `transport: "gns"`) launches both games with `-dvmp-transport gns`. A game on GNS never
starts Steam (DV handles it as Steam not running), so a GNS pair on this PC runs as a pod does:
players keyed by their install GUIDs (`-mp-allow-unverified`), joined by IP, and no Steam name,
avatar, lobby or invite.

**What matches Steam.** `GnsTransport` (`Multiplayer/Shared/Transport/Gns/`) makes the same calls
as `SteamWorksTransport` and the Facepunch classes beneath it: the same delivery mapping
(`SteamWorksTransport.ToSendType`: every reliable method goes on the one reliable ordered stream),
messages of up to 512 KB, unreliable messages fragmented, the same socket settings (send buffer,
send rates, 20 s timeout, fake loss and lag) and the same ping (round trip, once a second). GNS
v1.6.1 has the interface versions DV's Steamworks SDK uses (`SteamNetworkingSockets012`,
`SteamNetworkingUtils004`).

**What differs.**
- No relay: a GNS host listens on its UDP port only, and a join by SteamID is refused (logged).
- Steam runs its sockets in the Steam client, built from Valve's private copy of this code; the
  open-source release can be ahead of or behind it on fixes. GNS 1.5 and 1.6 (2026) brought four
  years of Steam's fixes to the open-source tree, so 1.6.1 is as close as a public build gets.
- Connections are encrypted with Windows' BCrypt and self-signed certificates instead of
  Steam-issued ones, so the peer's identity isn't authenticated (test pairs don't need it).
- GNS's warnings reach the game log as `GNS: ...`; Steam's sockets log inside the Steam client.
  A joining client logs `recv queue overflow` while the world streams in, at 1000 messages or
  1 MB queued: GNS drops what doesn't fit unacknowledged, so the host sends it again and nothing
  is lost. Those limits (`RecvBufferMessages`, `RecvBufferSize`) are the same defaults in Steam's
  sockets.

**Runs that stay on Steam pairs.** GNS pairs add capacity for the item and world scenarios; they
don't replace these. A result that differs between GNS and Steam is checked on Steam.
- Train-sync and coupling baselines.
- Join, rejoin, auth (#205) and lobbies.
- `LAG=` runs: the gate refuses them on a GNS pair, and `Net.SetReceiveLag` throws there.
- Soak and performance (`fps-drop`, `host-stall`, `player-soak`, `dvmp perf` baselines).
- `--verified` (dvmp refuses it with `--transport gns`: there is no Steam ticket).

**In the gate.** `TRANSPORT=gns` (or `UP_ARGS="--transport gns"`) starts the gate's pairs on GNS.
Every run's line and the summary name the transport its pair ran on (`dvmp status` reports each
game's), and a gate run against an already running pair records that pair's. A plain `dvmp up`
keeps a running pair's transport, so a scenario that rejoins its client or restarts the pair stays
on GNS.

```bash
TRANSPORT=gns PAIRS="p:1 d:1" ONLY="train-items recall" bash DevTools/Scenarios/gate.sh
```

**Setup, once per machine.** `DevTools/Gns/build-gns.ps1` builds the DLL from pinned sources
(GNS v1.6.1 and the vcpkg baseline it names, for protobuf) with Visual Studio 2022's C++ tools,
in about 15 minutes. It is one self-contained `GameNetworkingSockets.dll` (static C runtime and
protobuf, BCrypt crypto) that needs nothing else on a PC or under Wine. It also checks that the
struct offsets the P/Invoke code reads match the headers (`layout-check.cpp`). A Debug build of
Multiplayer then installs the DLL beside the mod; `dvmp up --transport gns` refuses to start
without it.

**Never in a release.** The transport is compiled only into Debug builds (Multiplayer.csproj
removes `Shared/Transport/Gns/` from every other configuration, and `TransportSwitch` reads the
switch under `#if DEBUG`), so a release ignores `-dvmp-transport`. post-build.ps1 never puts the
DLL in `build/` (which a release zips): a Debug build copies it straight into the game, and a
release deletes any copy left in `build/` or the game's mod folder, and fails if its zip holds one.

### Known item bugs (expected to fail)

Scenarios that prove a catalogued item bug ([research/item-bug-review.md](research/item-bug-review.md)).
Each class carries `[KnownBug("<id>")]`: `dvmp scenario list` and the run's header say it is
expected to fail. The gate runs them too (since OI-REF-02); run one, or put it in `ONLY`/`RUNS`,
and its result is never a regression: `dvmp scenario run` exits 4 and prints `KNOWN` when it fails as
expected, and exits 5 and prints `FIXED?` when it passes. The gate shows those results per run and
in its summary (`(1 KNOWN bug, failing as expected)`, and for `FIXED?` the scenarios whose
`[KnownBug]` to remove), and neither makes the gate fail. The PR that fixes a bug deletes the
attribute; from then on the scenario must pass.

| Scenario | Bug | What it does |
| --- | --- | --- |
| `item-bug-full-inventory-essential` (C#) | E2a (fixed; in the gate as `full-inventory-essential`) | The host destroys the client's compass and the client fills every inventory slot with mugs, then rejoins. The server issues a new compass, which must end up in a slot or in the client's Lost and Found (it goes to Lost and Found). |
| `item-bug-dropped-essential-rejoin` (C#) | E10 (fixed; in the gate as `lost-essential-rejoin`) | The client drops its comms radio and walks 300 m off with nobody near it, so the server collects it into the client's Lost and Found; the client has its silhouette. After a rejoin the silhouette must still be in its slot, and recall the radio into it. `LOST=0` keeps the radio in the world and rejoins `REJOINS` times (E1, holds). |
| `item-bug-recall-lease` (C#) | SL4 (holds) | The client drops its radio in a moving loco's cab and recalls it: no physics lease may be left, and after a save and restart it must be in the inventory. Passes since R3 (OI-REF-02, 2026-10-24: a run with the lease present ended with none). A run where the radio settled before the recall has no lease to test, and says so. |
| `item-bug-car-delete-unowned` (C#) | LF2 | An unowned mug in a loco's cab when the loco is deleted must stay in the world, as in vanilla. Today the server destroys it. |
| `item-bug-essential-slot-rejoin` (C#) | E6 (holds) | The player moves an essential to another slot and an ordinary item into its old one, then rejoins (`RESTART=1`: the host saves and both restart; `WHO=host`: the host's own player, with `RESTART=1`). Every essential must come back in a slot of its own. Passes today; kept as a regression check. |
| `item-bug-booklet-restart` (C#) | OI-COLD-15 (R4C) | The client takes a job at FM and stashes its booklet; the host saves and both games restart. The booklet must be back in the client's inventory, in its slot, and in no one else's: a carried booklet is a row saved with its holder, and vanilla's load doesn't reprint it (R4C C7). |
| `item-bug-magazine-ammo` (C#) | CN1 (R5, OI-CON-16) | The client loads each vanilla magazine (a boombox's cassette, a sprayer's paint can, a soldering tool's spool), picks the tool up and holds it five seconds (`HOLD`): the ammo must still be in it. Today the client reports it dropped at its next observation and loses it out of the magazine. |
| `lost-and-found-duplicate-id` (C#) | LF9 | OI-ID-06: a mug of the client's is in its Lost and Found, and a second mug's record is given its id and collected (no game path makes such a copy: the ledger re-mints one, OI-ID-05). Lost and Found refuses it (`DuplicatePersistentItemId`), logs it, and leaves the second mug on the ground. Today the rollback of the refused collection moves the first mug's ledger home back to the world, and the one-home check fails. |
| `item-bug-client-shelf` (C#) | SH1 | `owned-list-shelf` with the client: it drops (`RELEASE=throw`: throws) a mug of its own on the floor of the nearest shed. Its game has it on the shelf, but the client's lease races vanilla's parenting, and when the lease ends first the server keeps the mug in the world where it left the hand: never on the shelf, never listed "on a shelf". When the lease outlives the parenting, the run fails on SH2 instead: the client's own object leaves its interest and doesn't come back. |
| `item-bug-shelf-unseen` (C#) | SH2 | player host only. `owned-list-shelf` with `SEEN=true`: the host's own player drops a mug on the shed's floor, the server has it on the shelf, and the client, brought beside it, never gets it. |

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
