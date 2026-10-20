# Multiplayer Train Synchronization Design

> Status: canonical topology (stages 0–3) and control commits (stage 4, without the simulation
> snapshot) are implemented on `beta` and covered by dvmp scenarios. Motion is host-authoritative
> and steered rather than snapped, but motion epochs and correction telemetry (stage 5) and the
> removal of the remaining legacy paths (stage 6) are not done.

## Implementation status (audited 2026-10-14)

Checked against the code on `beta`. Where this differs from the design text below, this section
describes what the code does. Paths are under `Multiplayer/` unless stated.

**Topology (stages 1–3): implemented.**

- The pure model and state machine are `Multiplayer.Core/Trains/TrainTopologyModel.cs`
  (endpoints, capabilities, reciprocal/cycle validation, commands, result codes), tested in
  `Multiplayer.Core.Tests/Trains/TrainTopologyStateMachineTests.cs`.
- The host's canonical record is `TrainTopologyAuthority`, native mutation and read-back are
  `TrainTopologyNativeAdapter`, and the client side is `TrainTopologyReconciler` (in
  `Server/Trains/`, `Shared/Trains/` and `Client/Trains/`).
- Clients send `ServerboundTrainTopologyCommandPacket` for every coupler, hose, cock, MU and chain
  placement change: by hand, from the coupling menu and loco cabs
  (`Client/Trains/UICouplingHelperPatch.cs`), the remote controller
  (`Client/Items/RemoteControllerModulePatch.cs`) and the in-cab decoupler
  (`Client/Trains/DecouplerDeviceLogicPatch.cs`). Remote and decoupler commands set
  `ViaRemote`, which skips the player-distance check but not the 4 m coupler-gap check.
- The host's own changes, and native changes nobody commanded (spawn auto-couple, joint breaks,
  derail side effects), are observed and committed on the next tick (`CommitObservedTrainTopology`).
  Clients no longer auto-couple spawned cars themselves (477a94c); they wait for the host's
  snapshot. Nor do they run the steam tender auto-couplers (vanilla's
  `SteamTenderAutoCoupleMechanism` and Custom Car Loader's `CarAutoCouplerInternal`,
  `Client/Trains/TenderAutoCouplerPatch.cs`): those couple with the hose and cocks a few seconds
  after a spawn, rerail or uncouple, which a client sent the host as its player's commands.
- Every snapshot carries the **whole** graph, every brake line and every car's control revision,
  not only the affected closure. Accepted commands are broadcast; rejections go to the requester.
- A client couples (or uncouples) its own cars straight away and sends the command; if the host
  refuses it, the client puts the host's topology back when the refusal arrives, or once the player
  lets go of the chain. Over a laggy connection the player can walk off before then, so a refused
  couple looks like the cars came apart by themselves. A common refusal is `OutOfRange`: the host's
  copy of the player (more than a car length from the primary car's centre) or of the cars
  (couplers more than 4 m apart) lags the client's.
- The host logs every refused command with the player, the cars and ends, and why (for
  `OutOfRange`, the distance it measured), and every accepted couple
  (`NetworkServer.LogTopologyCommand`). The refusal sent back carries the same detail
  (`ClientboundTrainTopologySnapshotPacket.Detail`), so the player's own log says why too.
- The relay-only MU, hose, cock and couple/uncouple handlers and their `Common*` packets are
  gone (removed in #59).

**Why a train changed: recorded causes.** Uncouples, derails and car removals each record a
`ChangeCause` (`Multiplayer.Core/Diagnostics/ChangeCause.cs`: a reason, the acting player, the
tick and free-text detail) with a reason from `Multiplayer.Core/Trains/TrainChangeReasons.cs`
(`UncoupleReason`, `DerailReason`, `CarRemovalReason`). Each is logged in one form ("Uncoupled
CFF066 (Rear) from CFF439 (Front) on the host: CarRemoved by Bentgear29 (CFF439 PlayerDeleted)"),
published as a `train.uncoupled` / `train.derailed` / `train.car-removed` debug event, and kept:
`NetworkedTrainCar.LastFrontUncouple`, `LastRearUncouple` and `LastDerail`, and
`TrainChangeCauses.RecentRemovals` for cars that are gone. In a 2026-10-19 playtest a train
"came apart" because players deleted derailed cars from the middle of it, and the cars had been
derailed by a player's sandbox comms radio; both took matching log timings to find.

- Code that knows the cause opens a scope (`TrainChangeCauses.Scope`): the server's topology
  command (`PlayerCommand`, or `Remote` for `ViaRemote`), delete (`PlayerDeleted`,
  `ReturnedToGarage`), rerail (`CarRerailed`) and sandbox radio (`CommsRadio`) handlers, the
  authority's rollback (`HostRolledBack`), and on clients the destroy packet and the snapshot-driven
  derail (`FollowingHost`). Otherwise the patches infer it: joint breaks (`JointBroke`, or
  `JointBrokeDerailed` where vanilla has weakened the joint to 1.35 MN), vanilla's derail messages
  (`Stress`, `Explosion`, `EndOfTrack`, `MissingTrack`, `LoadedDerailed`), and the vanilla callers
  (`SafetyDerailer`, the unused-car deleter, the job generator, loco restoration, save loading, the
  host player's own chain, radio and remote). Anything else is `Unknown` with the calling methods
  as its detail, which names the case to add (`Shared/Trains/CouplerUncoupleLogPatch.cs`,
  `DerailCausePatch.cs`, `CarSpawnerPatch.cs`).
- The host sends its causes: uncouples ride on the next broadcast topology snapshot
  (`Uncouples`), removals on `ClientboundDestroyTrainCarPacket.Cause`, derails in
  `ClientboundTrainDerailCausePacket`. A client applying the host's topology records the host's
  reason for each coupling it takes apart, `FollowingHost` when it has none, and `CoupleRefused`
  (with the refusal's code and detail) when it is undoing its own refused change.
- Breaks are also logged as errors by `CouplerBreakPatch`, with the joint force.
- All of it is in the normal log, so a train that came apart can be traced from the host's log
  alone, without the debug system (off in Release builds unless the dev tools mod is installed).

**Join baseline: implemented.** The host sends `ClientboundTrainBaselineBeginPacket`, one numbered
spawn packet per trainset, a topology snapshot tagged with the baseline ID, then
`ClientboundTrainBaselineCompletePacket` (`Server/Session/NetworkServer.cs`). The
client admits them through `Multiplayer.Core/Trains/TrainBaselineReceipt.cs` and only advances to
`ReadyForItems` once the receipt is complete and the topology applied; otherwise it disconnects
with an error after 45 s. Reliable train broadcasts during that window are held per player and
delivered afterwards (`HoldForTrainBaseline`; 5a46e9a), not dropped. There is no completion
digest; the manifest is counts only.

**Clients never report a coupling break; they follow the host's topology.**

- Coupler joints break at vanilla's 300 MN / 1e8 N·m on host and client
  (`Client/Trains/CouplerBreakPatch.cs`). That's about a hundred times a real coupler, so normal
  play never breaks one. Every break is logged as an error with the joint force, both cars'
  speeds, the coupler gap, derailment and (on a client) what the host has at that end, plus a
  `train.coupler-broken` debug event. The gate flags any run that logged one.
- A break on a client is undone from the last host topology instead of reported, so a hitch can't
  disconnect the hose and vent the brake pipe (345783c).
- Client joints used to be unbreakable. When a client had two cars coupled that the host had apart,
  their corrections pulled them apart against a joint that couldn't give, and the cars went wild
  (#64).
- A client coupling the host doesn't have, with hoses, splits a brake line, and the host's brake
  state updates notice that within a couple of seconds and resync the topology. A chain-only
  coupling leaves the brake lines alone. So a client that keeps receiving physics packets for a
  trainset with a different number of cars than its own has drifted from the host's couplings,
  and after a second it reapplies the host's topology
  (`TrainsetPhysicsFollower.NoteSetMismatch`).
- Before this, a chain-only client coupling and a host pulling away at 2 m/s threw the client's
  cars about at up to 140 m/s and 500°/s. Now the client uncouples after 1.4 s, its cars peak at
  3.4 m/s, and the wagon ends within 0.02 m of the host's.
- Once joined, a client doesn't couple two cars whose couplers are more than 2 m apart
  (`TrainTopologyNativeAdapter.MaxCoupleDistance`). Vanilla would make a joint as long as the gap
  and reel the cars in, which flings them. The rest of the snapshot is applied, and the snapshot
  stays pending and is retried (couplings only) until the corrections have brought the cars
  together. The host never defers, and neither does the join baseline.
- Scenario: `coupler-desync` couples a loco and wagon by the chain on the client only, then pulls
  the loco away on the host.

**Car identity survives DV's car pool (#69).** A deleted car of a pooled livery goes back to
DV's pool, and `GetFromPool` hands out the most recently returned object first. `NetworkedTrainCar`
destroys itself when its car is disabled, but Unity only does that at the end of the frame. So a
delete and a spawn of the same livery in one frame (on the host, or in one packet batch on a
client) used to give the new car the old, doomed component: the host announced it with the old
net ID, the client rejected that ID as taken, and both ended up with an unnetworked car. Every
topology snapshot then waited for that car, so couplings stopped syncing for the session. Now
`OnDisable` frees the net ID at once and marks the component retired, and `TrainCarPatch`
replaces a retired component when the pool hands the car out. Scenario: `car-pool-reuse`.

A car deleted twice (DV's slow bulk delete, `DeleteTrainCarsFromTrack`, still holding a car that
`DeleteCar` then deleted) threw inside the bulk delete's coroutine, which stopped with cars
switched off but never deleted. They stayed in their trainsets, unnetworked, and every join failed
on them (#84). `CarSpawnerPatch` now skips a car that's already deleted (no logic car), and the join
baseline skips a trainset whose cars are all deleted. Scenario: `car-delete`.

**Controls (stage 4): implemented, without the simulation snapshot.** Handbrake, brake-cylinder
release, fuses and ports are requests carrying an operation ID and expected control revision; the
host applies them and answers with `ClientboundTrainControlCommitPacket`. The thresholded
`ClientboundTrainSimulationSnapshotPacket` with `simulationRevision` was not built: brake state uses
`ClientboundBrakeStateUpdatePacket` (0.02 bar threshold, 2 s heartbeat, topology-revision gating
and a brake-line member digest).

A client only sends a cab control's port while it holds that control's lease, so every control has
to be hooked when the cab loads (`NetworkedTrainCarClient.HookInteriorControls`). The port comes from the controls
overrider or, failing that, the control's `InteractablePortFeeder`, and a second pass hooks
feeder-driven controls the `InteriorControlsManager` doesn't list (the DE2's cab fan). A control
that feeds several ports (the CCL C Class's bright switch feeds the front and rear headlights)
leases and releases them together. Each control is hooked once its implementation exists, which
vanilla adds as the cab is made (`ControlSpec.Awake`), so within a frame or two of the cab's
controls manager starting, at any frame rate. One that takes longer is hooked when it's ready,
for as long as that cab stays loaded, and logged after 2 s. Until 2026-10-20 a control with no port
(a handbrake, a fuse) never counted as ready, so every cab with one waited the whole 2 s before
hooking anything: a lever grabbed in that window took no lease. A cab unloaded meanwhile is
left alone (its hook pass threw). An unhooked control still syncs through the no-lease path below,
but takes no lease and isn't blocked while another player holds it. The lease is
taken on `Grabbed` (levers, knobs, flat-screen buttons), on hover-scrolling, and on `Used`: VR
buttons and toggle switches, and keyboard presses, work through `Use()` and never raise `Grabbed`.
A `Used` lease is handed back 0.5 s later, once the change has been sent. Fuses, the handbrake and
brake-cylinder release have their own packets. #73 was the DE2's wipers, fan and lights never
syncing from clients, and every button and switch a VR client pressed being dropped.

The controls on the car body are hooked the same way when its external interactables load
(`HookExternalControls`, from `ExternalInteractableLoaded`; an exploded loco's come through the same
event), and unhooked when they unload. They load near the car, while the cab loads only once a
player is on it, and never on a dedicated server. There are more of them than the name suggests
(the prefabs, 2026-10-19): doors, windows, hatches and curtains on most cars, but also the S282's,
S060's, FEF-3's, S464's and S484's compressor valve, dynamo, lubricator and oiling-point doors, the
DE6's starter and primer, the 2901's bell, blowdown, cylinder cocks and dynamo, the S484's mars
light, and a handcar's handle. Handbrake feeders are left to the handbrake sync. Unhooked, a grab on
one took no lease, so its change went only as an unleased one (below), after the settle window,
and nothing stopped another game's copy writing the port: a handcar never moved, since the host's
own `HandcarBarController` wrote the handle as let go every frame. A control in the cab's controls
manager as well (a handcar's handle) is hooked once. Several controls can feed one port (an
FEF-3's two lubricator buttons): a lease blocks them all.

A handcar's handle takes no lease, so any number of players can pump at once. Vanilla's
`HandcarDrive` has one handle, `BAR_EXT_IN` (where it is) and `ENGAGED_EXT_IN` (whether it's held,
which `HandcarBarController` writes every frame), so each game's two handle ports are its own
player's: no game applies another's, and the host doesn't send its own. A client sends `ENGAGED`
whenever it changes and `BAR` while it's held (with `ENGAGED`, as taking hold may not move it). The
host keeps each player's handle (`HandcarPumpers`, filled by `NetworkedTrainCarServer.ApplyPlayerPorts`) and a
postfix on `HandcarDrive.Tick` (`HandcarDrivePatch`) adds each held handle's stroke, worked out as
vanilla's, to the torque vanilla made from the host's own player's handle. The total is capped at
two players' most, so two players pumping in time go faster than one, one pumping against the other
slows the car, and a third adds nothing. A pumper is dropped when they let go, leave, or get further
from the car than its length.

A few vanilla controllers write a control's port every frame from their own game's input, whoever
is using the control: `WhistleRopeController` (the rope's tension, into its dummy lever). Every game
runs them, so while a player on another game uses the control, this game's write it back as let
go. The rule: such a writer is off while another player holds the lease on a port it writes
(`LocalInputWriters`, toggled in `NetworkedTrainCarClient.ReceiveAuthorityUpdate`; the host gets the lease updates
too). `HandcarBarController` writes whether the handle is held the same way, but that port is each
game's own player's (above), so it stays on. Other controllers write only
on their own game's input, which a lease covers, or from the car's own state (`TractionPortsFeeder`,
`BoilerSimController`'s boiler angle, `DamageController`'s health ports), which are local values and
not a player's.

Custom Car Loader has its own writers of the first kind, which the vanilla class names miss. Its
pullable rope (`PullableRopeBase`: the C class's horn, and the C-16's, FEF-3 Modification's, S464's and
S484's) is the control itself: every frame it requests its own rope's length as the value, and it
ignores `SetValue`. So another player's value reaching a game with nobody pulling lasted one frame,
and the idle rope wrote it back as 0: the host's cut a client's horn, and a client's cut the host's
in its own cab. It can't be switched off, as its `OnDisable` writes 0 and snaps the rope back, so a
prefix on its `Update` skips it while another player holds the lease
(`NetworkedTrainCarClient.IsHeldByAnotherPlayer`, kept in `ReceiveAuthorityUpdate` beside the
writers). CCL's `WhistleDistanceControllerInternal` writes a dummy control as `WhistleRopeController`
does and is skipped the same way, starting again from 0 (no known car uses it, so whatever its
player grabs isn't known to take the dummy's lease). Both are in
`Client/Compatibility/CustomCarLoaderRopes.cs`, patched by type name once `CCL.Importer` loads.

One more of the first kind isn't a port: a handcar's handbrake springs off by itself. Vanilla
`HandcarHandbrakeController` lowers it every frame unless its own game's player is holding it
(grabbed, or hover- or keyboard-scrolled), and every game with the handcar's handbrake loaded runs
it. So the host's copy, or another client's, undid a player holding it on. Handbrakes take no lease,
so the handbrake sync stands in for one (`HandcarHandbrakeControllerPatch`). A game leaves its
spring-back off for a second after another player's handbrake change reaches it: the host on a
client's request, a client on a commit for another player's operation (its own commits don't
count). A player holding the handbrake on resends its position every 0.25 s, so that stays true
while they hold it. Once they let go, their own game springs it back and sends the changes, which
keep the others off until it's down. If they drop out while holding it, the others spring it back a
second later.

Some controls never raise `Grabbed` or `Used`: VR valves and cylinder cocks, and controls nothing
hooks (one on a car's body prefab rather than its cab or car-body controls, say). A client sends a change to one of those that nobody holds
the lease on (`NetworkedTrainCarClient.GetUnleasedChange`): the port differs from the host's last value by more than
0.02, and that value isn't still settling (1.5 s after the host set it; a change in that window
waits and goes if it still differs). A change its player can't make, out of reach of the car (the
host's distance, a car's length from its centre) or on a control another player holds, isn't sent:
the control goes back to the host's value, logged the first time for each port (and
`train.control-put-back` in the event stream). Before, it was dropped and stayed on that game
only, for good: in #159 a client left behind by a rolling S282 kept turning its valves, which the
host never had (`cab-controls MODE=no-grab-sequence`).

Only ports a player drives go that way. Being typed CONTROL isn't enough: DV and mods set several
CONTROL ports from the simulation, usually from a control's own port (`horn.HORN` from the horn
control, the DM3's `gearInputA.GEAR` from its gear lever, the headlight decoders). Playtest 2 had a
client's copy of the DE6's compressor signal overwrite the host's, and the loco never built air.
`ControlPortWriters` (Multiplayer.Core) sorts each car's ports into player-driven, simulation and
host-kept. The player-driven ones come from the livery's prefabs (`InteractablePortScan`): the car
body, the cab, the car-body controls, and the exploded versions of the last two. In them it takes
the ports of every `InteractablePortFeeder` (the base controls such as the throttle take their
values from these), `OilingPointPortFeederReader` (an oiler can), the `NotchedPort*Input`
keyboard inputs and `HandcarBarController`, plus any control hooked when a cab loads. Custom Car
Loader builds its cars' controls from the same DV components when it imports them, so the scan
covers them, and every game, a dedicated server included, gets the same set without loading the
cab.

A CCL control has a port only if its car's author placed a feeder proxy on it, and older cars
have doors, windows and seats with none (the C class's cab doors, its twenty engine-room doors, two
sliding windows and wall seat; the S464-730's tender toolboxes; the Santa Fe 2901's tender doors and
hatches). Those moved on one game only, and the save never kept them. `CustomCarLoaderPorts`
(Shared/Compatibility) wires them the way DV wires its own doors, as CCL builds each car and before
any spawns: a `GenericControlDefinition` (`saveState` on) in the livery's simulation, and an
`InteractablePortFeeder` on the control writing its `EXT_IN`, listed in its prefab's
`InteractablePortFeedersController`. Only a lever, puller, knob, switch, wheel or CCL screw that has
no feeder of any kind, isn't driven by another control (CCL's constant and ratchet feeders, a
whistle's dummy) and carries nothing outside a list of harmless components gets one; anything
unknown leaves it alone, with a warning naming it. The component's name comes from the control's
path in its prefab (`AddedControlIds`, Multiplayer.Core: `mpControl_<cab|ext|body>_<path>`), so
every game builds the same ports and the save finds them again; renaming one loses its saved state.
From there the scan and the cab and car-body hooks take them like any other control. A save from
before has no state for them, which vanilla would log as an error per control per car: they start
where the prefab places them instead. A car with no simulation at all can't be given ports; that's
logged.

A client sends only player-driven ports; the host takes only those, or one the sender holds
the lease on. The compressor's activation signal is host-kept: clients never send it, and the host
keeps it matched to `brakeSystem.compressorActivationSignal`, since DV writes it only when that
decision changes (`NetworkedTrainCarServer.KeepCompressorSignal`).

**Motion: host-authoritative and steered; stage 5 not started.**

- Clients steer on-rail cars towards the host's bogie positions every physics step instead of
  snapping to a latency-old pose (`TrainSpeedQueue`, `NetworkedBogie`; 5e07a51). Speed and
  correction are eased separately, but a step in the host's speed above 1.5 m/s (an impact) is
  followed at once (72db159).
- The host scales the speed it sends by its simulation rate, so clients don't run ahead and snap
  back after a host stall (`NetworkTrainsetWatcher.UpdateSimulationRate`; 7deb3b9). The rate is
  `Time.deltaTime` (which DV caps at 1/12 s) over `Time.unscaledDeltaTime`, averaged over about a
  second. Measured per tick, every ordinary host hitch read as a one-tick dip to ~0.6, and clients
  followed the dipped speed as an impact: their trains' speed swung by 5-15 km/h every second.
- The network tick advances by elapsed real time, so it keeps step below 24 fps
  (`Shared/Session/NetworkLifecycle.cs`; 274c250).
- Full syncs use the transform instead of the rigidbody while a just-spawned car's body is still
  at its pooled position (c2d0d5f).
- Stopped cars settle where the host's stopped (#61), including after pile-ups under lag (#63: single
  cars are placed kinematically, coupled ones driven; see "What the code does"). The host sends a set reliably on the tick it
  stops; clients hold a car the host reports as stopped at the host's position without touching
  its rigidbody unless it is more than 5 cm off, so idle cars sleep like the host's instead of
  being woken and nudged by every resync.
- A car derailed on a client only is rerailed onto the host's track and pose, its motion queues
  are cleared, and the host topology is reapplied (`NetworkedTrainCarClient.RerailToHost`;
  0903b06).
- A car derailed for 30 s can be rerailed even if it never stops moving
  (`Shared/Trains/TrainCarRerailPatch.cs`).
- A host rerail or move-to-track resets everything on the client that follows the host's samples,
  before and after vanilla relocates the car, and the client then asks for a full sync; the host
  sends one reliably anyway once its own relocation is done. The rigidbody queue only steers a
  derailed car. Before, a rerailed car slid off at 1.5 m/s and was snapped back and flung, forever
  (playtest 2; see "What the code does").
- Explosions are the host's: clients play the host's explosions but never apply explosion forces
  to trains (`Server/Trains/ExplosionServerPatch.cs`, `Client/Trains/ExplosionClientPatch.cs`; see [`../world-sync.md`](../world-sync.md)).
- Deleting a car steps any player on it off first, so players are no longer destroyed with it
  (`Shared/Trains/CarDeletePlayerSafetyPatch.cs`); the items lying in it go to Lost and Found on the
  host after that (`Server/Trains/CarDeleteLostAndFoundPatch.cs`).
- Not done: there is no `motionEpoch`. A full sync more than 5 m off (`POSITION_UPDATE_THRESHOLD`),
  a derail, a rerail (host or client-only) and a host move-to-track are the only things that clear
  a client's motion queues. `NetworkServer.SendTrainsetRelocation` has no caller, so the
  relocation/ack path is unused. There is no correction or bandwidth telemetry.

**Stage 6: partly done.** Protocol mismatch is rejected at connect, and the legacy `Common*`
control receivers were removed in #59. Still present: `CommonCouplerInteractionPacket` remains for
interaction flags not mapped to a topology command; its host handler mutates native state under a
2 s timed observation suppression.

**Not built:** periodic topology digest audit, wrap-safe revision comparisons, control lease expiry
other than disconnect/release, the per-subsystem counters in "Performance budget", the per-train
dashboard.

**Runtime coverage** (dvmp gate, `DevTools/Scenarios/gate.sh`; see
[`../testing.md`](../testing.md)): `manual-uncouple` (hand uncouple/recouple, with lag),
`uncouple-moving`, `couple-impact` (coupling while the buffers bounce, by menu or chain),
`fps-drop`, `host-stall`, `client-derail`, `rerail`, `explosion`.

**Known open issues:**

- Clients occasionally see a snap of about 5.8 m at about 10 m/s (#44). It is intermittent; the
  5 m full-sync threshold is the only path that moves an on-rail car that far at once. The likely
  cause is the sample age being capped at 0.5 s when projecting the host's pose.
- The coupling "bounce" from real play is at least partly #61: a car pushed by a client's loco
  before the host's samples arrive was launched locally, and the correction then inherited that
  speed. Takeovers now start from the host's speed.

## Goal

Make the host the only authority for rolling-stock identity, topology, simulation, and motion while
allowing a nearby client to operate a train without waiting for the host player to be present.
Every accepted client action must become host state first. Every peer must then be able to converge
from a canonical snapshot even if it missed an interaction, loaded an interior late, left interest,
joined late, or predicted the action incorrectly.

The base-game investigation and confirmed failure signatures are maintained in
[`../research/DERAIL_VALLEY_TRAIN_SYNCHRONIZATION_LOGIC.md`](../research/DERAIL_VALLEY_TRAIN_SYNCHRONIZATION_LOGIC.md).
Native spawn and simulation facts gathered for the old in-game train fixture are in
[`../research/DERAIL_VALLEY_TRAIN_TEST_FIXTURE_LOGIC.md`](../research/DERAIL_VALLEY_TRAIN_TEST_FIXTURE_LOGIC.md);
the fixture itself was removed with the in-game runtime test system (f510e8e), and train tests now
use the dvmp harness ([`../testing.md`](../testing.md),
[`../../DevTools/README.md`](../../DevTools/README.md)).

## Core invariant

> The host's canonical train records are truth. A `TrainCar`, `Trainset`, coupler, hose, cab
> control, brake component, or rigidbody on any process is a projection of those records. A local
> Unity interaction may predict presentation, but it cannot commit shared state.

This is the same boundary that made world-item synchronization recoverable: identity and revisions
outlive individual Unity events and projections.

## Why the current event model is insufficient

This section describes the event model the design replaced. Topology no longer works this way;
the remaining `Common*` traffic is listed under "Implementation status".

Several existing live packets describe an action that has already happened locally:

```text
client changes Unity object
-> Common...Packet
-> server validates or blindly relays
-> other peers replay the Unity action
```

That model has no general answer for:

- the listen host not applying the relayed action;
- a peer missing or receiving an action before its object is ready;
- an optimistic client action being rejected;
- a coroutine completing differently on two frame rates;
- one endpoint changing while its reciprocal endpoint does not;
- a late joiner or returning-interest client needing current state rather than event history;
- an operation being duplicated, reordered, or retried;
- detecting that native simulation mutated topology without a multiplayer interaction packet.

The observed client-coupled/host-uncoupled incident is the expected result: the client joined two
cars, the host continued moving two trainsets, motion correction stretched the client projection,
and the two brake lines then solved different pneumatic networks.

### Current audit summary

| Area | Current useful foundation | Remaining authority debt |
|---|---|---|
| Spawn | Atomic begin/parts/complete baseline plus revisioned endpoint/control snapshot | Runtime stress coverage for concurrent spawn/delete remains |
| Motion | Host tick, bogie anchors steered to, simulation-rate-scaled m/s speed, derail snapshots, client-only derail rerail | Needs measured correction budgets and explicit relocation/derail epochs everywhere; intermittent 5.8 m client snap |
| Couplers | Revisioned host command, invariant validation, native postcondition and canonical repair; dvmp coverage | Remove the residual `CommonCouplerInteractionPacket` path and its timed suppression |
| Hoses/cocks | Reciprocal endpoint records and brake-line reconciliation | Add packet-loss runtime scenario |
| MU cables | Canonical endpoint transaction with affected control snapshots | Expand compatible-locomotive runtime matrix |
| Handbrake | Revisioned host commit and rejection correction | Runtime test default-spawn/interior reload |
| Brake release | Revisioned host commit with native postcondition | Runtime rejection/duplicate test |
| Ports | Leased, ordered host commits with canonical accepted values | Add bandwidth/coalescing telemetry |
| Fuses | Revisioned host commit and corrective values | Runtime stale-revision test |
| Brake pressure | Shared brake-line identity/digest, thresholds and heartbeat | Quantitative bandwidth/divergence budgets |
| Cabin controls | Post-interior refresh repairs late presentation | Presentation refresh must remain separate from simulation authority |

The `Common...Packet` name is itself a warning for mutations: direction should communicate intent.
Client requests and host snapshots have different trust and retry semantics and should be different
packet types. Control requests still travel in the old `CommonTrainPortsPacket`,
`CommonTrainFusesPacket`, `CommonHandbrakePositionPacket` and `CommonBrakeCylinderReleasePacket`
classes (now carrying an operation ID and expected revision); only the host's answer has its own
type.

## Authority layers

Train synchronization is divided into five layers. A feature must belong to exactly one primary
layer even when it consumes state from another.

| Layer | Host owns | Client may do | Delivery character |
|---|---|---|---|
| Identity/lifecycle | car NetId, GUID, livery, spawn, delete, restore | request permitted actions | reliable transactional baseline |
| Topology | mechanical, pneumatic and MU endpoint graph | request or locally predict an interaction | reliable ordered canonical transactions |
| Controls | accepted port, fuse, handbrake and release values | request while authorized | reliable ordered commit/ack; coalesced where safe |
| Simulation | traction/resources/brake pressures/derived state | presentation only | thresholded snapshots plus heartbeat |
| Motion | track spans, speed, transform, derail state | interpolate/predict presentation | sequenced snapshots plus reliable epoch changes |

Player presence does not transfer simulation authority. A client alone in a remote locomotive can
hold narrowly scoped control authority; the resulting mutation still executes in the host process.

In the code, motion is sent unreliably (reliably when a bogie changed track) and has no epochs,
and simulation is the brake-state packet only; see "Motion" and "Brake and locomotive simulation".

### The host's own player

The host runs no client (host-local-session stage 3): its player is a `LocalPlayerSession` in the
host's world, so it is never a follower of any layer. What the host sends players to follow (spawns,
deletes, topology snapshots, physics, brake state, control commits, relocations, rerails, cargo,
damage, paint, restoration) isn't presented by that session, which drops it if it comes (see "The
host's own player" in [architecture.md](../architecture.md#the-hosts-own-player)). There is no
`TrainTopologyReconciler` on the host, no baseline and no sync request. Couplings made in the
host's world reach clients by the server observing them (`CommitObservedTrainTopology`):
couples and uncouples from the couplers' events, chains settling (hooked on, parked, left hanging, screwed tight or loose)
from their `StateChanged` (`NetworkedTrainCarServer`), and hoses, cocks, MU cables and the coupling
menu from patches on the vanilla calls (`Server/Trains/TopologyObservationPatch.cs`). The client
patches that report these send only from a remote client. Before stage 3 the host's loopback client
reported its player's as topology commands, which the server turned into the same commit.

This matters because the reconciler makes the world match a snapshot: run on the host against an
old one, it takes the canonical graph apart. On 2026-10-19 each car the host spawned asked for a
train sync from the host's client at start-up; the reply was a snapshot from before the spawn's
auto-couple, which uncoupled every new consist on the host, and every client followed (29 trains
in one session).

The host's own control changes, its simulation's and its player's levers alike, aren't requests: `Common_SendPorts`, `Common_SendFuses`, the handbrake and the brake-cylinder release
call `NetworkServer.CommitHostControls`, which commits the values read that tick, stamped with that
tick, and broadcasts them with `excludeSelf`. There's no lease, range or operation check (they come
from the server's world) and no rejection. The operation id has player byte 0, so clients still see
a committed handbrake change as one. The brake-cylinder release is committed from vanilla's event,
which fires before the release is set and is cleared at the next brake update. A remote player's
release is applied in the server's apply scope so the host's event doesn't commit it again. Before
2026-10-20 these went over loopback, and the server read the values a poll later than the tick it
stamped on them.

The host's player's lever leases are requests like any player's
(`ServerboundTrainControlAuthorityPacket`), handled in-process (`RegisterLocalTrains`), with the
range check against the host player's own transform rather than its last position packet. They
keep their order with the commits: a release flushes the ports, committing them directly, before
it's queued, and the host's commits take no lease, so a grant needs no order with them.

The comms radio's car delete, rerail and work-train summon are requests too, the host player's
handled in-process like the lever leases (`HandleTrainDelete`, `HandleTrainRerail`,
`HandleWorkTrain`), validated like anyone's; the work train's range check reads the host player's
own position too. Every work-train request is answered (`SpawnResponse`, "in use" for a refusal with
no reason of its own); the host player's answer goes back through its peer to whatever holds the
radio's RPC ticket. The host doesn't send a firebox ignite: its fire is lit in its own world.

A player makes these requests through `ITrainClient` (`TrainRoles.Client`): on a client it's
`NetworkClient`, which sends them; on a player host it's the host's `LocalPlayerSession`
(`LocalPlayerSession.Trains.cs`), which hands the same packets to the server in-process and
subscribes the lease updates its cab needs (`ClientboundTrainControlAuthorityUpdatePacket`), the
one train packet the host's player is sent. A dedicated host has no session, so its cars have no
local-player half.

### Where the code is, by role

The client/server split ([code-layout.md](code-layout.md)) puts each role's train code in its own
folder. `NetworkedTrainCar` (Shared) is the car: its identity and lookups, the port and fuse map,
dirty tracking and the tick. On the server it has a server half, `NetworkedTrainCarServer`
(Server/Trains): control leases (the rules are `ControlLeases`, in Core), coupler-interaction locks, validating players' changes, the
host's own control commits and everything the car sends players (brake state, cargo, health,
topology observations). The car makes it from the server (`ITrainServer.CreateCarPart`, reached as
`TrainRoles.Server`) and calls it where that code always ran: the end of `Start`, `OnDisable`, and
each tick after its own sends (`ITrainCarServerPart`). Server code reaches it as `car.Server()`.
The trainset motion the host samples is `NetworkTrainsetWatcher` (Server/Trains); clients follow it
in `TrainsetPhysicsFollower` (Client/Trains). The checks a player's control change passes before
the host applies it (in range, nobody else's lease, not stale unless it's the next of that player's
ordered operations on the car) are `ControlCommitEnvelope`, in Core, which the server's
`ValidateControlEnvelope` maps to the commit's result.

The local player's half is `NetworkedTrainCarClient` (Client/Trains), on every game someone plays
at, the host's too: hooking the cab and external controls for leases, the player's control
changes and chain interactions, and on a client, following the host (played-out and predicted
port values, motion, derail and rerail, brake state, commits). The car makes it from the local
session (`ITrainClient.CreateCarPart`, `TrainRoles.Client` = `LocalSession as ITrainClient`) and
calls it through `ITrainCarClientPart` at the end of `Start`, in `OnDisable`, from its sends, and
when the host's port values land. Client code reaches it as `car.Client()`. On a player host
the host's `LocalPlayerSession` implements `ITrainClient` and makes the
same part with `followsHost` false: it hooks the host player's controls and tracks its chain drags,
but follows nobody and reports no coupling (the server observes those). A dedicated host has none.

The car's own changes (dirty ports, fuses, handbrake, brake release, paint) go through one of the
two, its `Role` (`ITrainCarRole`, both parts implement it): the server's half on the server, which
commits them as the authority's, or the local player's on a client, which asks the host for them.
The role picks which changes are this game's to send (the server: every port its simulation sets,
less the locally fed ones and the handcar handle; a client: the controls players drive, and fuses
and handbrakes only in reach or by remote) and takes the host's port values as they land, so the car
doesn't ask which game it is on. On the host its player's half is still consulted for leases
(`IsBlocked`) and for the host values being written (`WritingHostValues`); the server's half marks
the car initialised (`Client_Initialized`: it follows nobody).

What stays in the car is what both roles run: dirty tracking and the sends' common part, applying
a coupler interaction (`Common_ReceiveCouplerInteraction`, the host for a player's request and a
client for another player's), paint, and the handbrake-held-remotely window.

## Stable identities

### Car identity

Runtime packets use the host-assigned `ushort` car NetId. Baselines also carry car GUID and livery.
NetIds are session identities and must never be used as saved identities.

### Endpoint identity

Every attachable end is addressed by the value pair:

```text
TrainEndpointId
  carNetId: ushort
  end: Front | Rear
```

Do not infer the other endpoint from trainset list order. Coupling, hose, and MU state are graph
relationships and must name both endpoints explicitly.

### Revisions and epochs

Use different counters for different consistency domains:

- `baselineEpoch`: changes when a client begins a new rolling-stock baseline transaction;
- `topologyRevision`: host-monotonic revision for an atomic topology commit;
- `controlRevision`: per-car monotonic revision for committed control state;
- `simulationRevision`: per-car monotonic revision for reliable simulation baselines;
- `motionEpoch`: per-car discontinuity counter for spawn, relocation, rerail, or derail mode changes;
- `hostTick`: timestamp for continuous prediction within an epoch.

Counters are unsigned and compared with wrap-safe arithmetic. A revision is not a timestamp and a
host tick is not permission to overwrite a newer topology transaction.

Implemented: `BaselineId` (per join), `TopologyRevision` (host-global), `ControlRevision` (per
car) and the network tick. `simulationRevision` and `motionEpoch` do not exist. Comparisons are
plain `<`/`<=`, not wrap-safe; the host's topology revision stops at `uint.MaxValue` and the state
machine then rejects commands with `RevisionExhausted`.

## Canonical topology model

Mechanical, pneumatic, and electrical attachments are related but not interchangeable. Each
endpoint record contains:

```text
TrainEndpointState
  endpoint
  mechanicalPartner: TrainEndpointId?
  chainState
  preventAutoCouple
  hosePartner: TrainEndpointId?
  cockOpen
  muPartner: TrainEndpointId?
  capabilities
```

`capabilities` records whether the livery/end actually has a chain coupler, hose/cock, automatic
coupler, or MU connector. Missing components are valid data, not lookup failures. The implemented
`TrainEndpointCapabilities` flags are `Mechanical`, `Hose`, `Cock` and `MultipleUnit`; chain state
is one of `Parked`, `Dangling`, `BeingDragged`, `AttachedLoose`, `AttachedTight`, `Disabled`.

### Topology invariants

After every host commit and client reconciliation:

1. A mechanical relationship is reciprocal: `A.partner == B` implies `B.partner == A`.
2. A hose relationship is reciprocal and both endpoints support hoses.
3. An MU relationship is reciprocal and both endpoints support compatible MU connectors.
4. An endpoint has at most one partner in each attachment domain.
5. No endpoint is partnered with itself.
6. A relationship cannot name a missing, retired, or different-baseline car.
7. A cock is local endpoint state; it does not become open merely because a hose connects.
8. Trainset membership is derived from committed mechanical topology, never accepted as a
   competing authority.
9. Brake-line connectivity is derived from committed hose/cock topology.
10. Applying the same canonical transaction twice is a no-op.
11. Mechanical coupling cannot connect two endpoints already in the same mechanically connected
    component; loops are rejected.
12. A merge joins endmost cars of two different trainsets, and a split operates only on reciprocal,
    adjacent partners in one trainset.

Whether DV permits a hose or MU link without a mechanical coupling is a native capability rule.
Current native research proves that chain-interaction uncoupling can leave a hose connected after
the mechanical relationship is removed. The canonical model must therefore represent that state.
MU auto-coupling requires mechanical coupling, while save restoration may target a nearby coupler;
the validator must preserve the intended native interaction semantics rather than derive MU state
from trainset membership.

### Atomic transaction

An operation affecting two endpoints is committed as one transaction:

```text
ClientboundTrainTopologySnapshotPacket
  baselineEpoch
  topologyRevision
  operationId
  result: Accepted | Rejected | Corrective | Baseline
  reasonCode
  endpointStates[]
```

`endpointStates` includes the full affected closure, not only the endpoint the player touched. For
a simple couple this is both ends. If repairing an inconsistent graph detaches old partners, those
old partners are included too.

The implemented packet is simpler and larger. It carries `BaselineId` (non-zero only for the join
baseline), `TopologyRevision`, `OperationId`, `Result` (a `TrainTopologyResultCode`, which doubles
as the reason), every endpoint of every car, every brake line's anchor, member digest and pipe
pressure, every car's control revision, and full control states for cars whose MU link changed.
Topology changes rarely, so the whole graph is sent rather than a closure.

The packet carries final state, not instructions such as "call `CoupleTo`". The client reconciler
computes the minimal native operations required to reach that state under outbound-observation
suppression.

## Client intent and host commit

Topology input uses a serverbound request:

```text
ServerboundTrainTopologyCommandPacket
  operationId
  kind: Couple | Uncouple | ConnectHose | DisconnectHose |
        SetCock | ConnectMu | DisconnectMu | SetChainPresentation
  primaryEndpoint
  secondaryEndpoint?
  expectedTopologyRevision
  requestedValue?
  interactionContext
```

The processing order is mandatory:

```text
client predicts optional presentation
-> server authenticates player and packet shape
-> server resolves endpoints and capabilities
-> server validates proximity or an explicit remote-control capability
-> server compares expected revision
-> server validates topology preconditions
-> host mutates native DV objects under observation suppression
-> host reads the resulting native state back
-> host verifies invariants and postconditions
-> host commits the canonical transaction/revision
-> host broadcasts the snapshot, including to the requester
```

If validation or the native mutation fails, the host sends `Rejected` with the canonical endpoint
closure. Rejection is therefore also a correction. Silence is never an acknowledgement.

`operationId` is unique per client session. The server keeps a bounded result cache so retrying a
request returns the same committed result instead of executing it twice.

As implemented (`NetworkServer.OnServerboundTrainTopologyCommandPacket`,
`TrainTopologyAuthority.Execute`):

- Operation IDs carry the player ID in the top byte and must increase per player; a repeat
  returns the cached result (last 512 operations), an older ID is rejected as `StaleRevision`.
- The client's `ExpectedRevision` is not compared. Commands are reliable and ordered per player,
  and unrelated native changes can advance the global revision while a command is in flight, so
  the host validates each new command against its current graph instead.
- Range: a client must be within one car length of the primary car (skipped for `ViaRemote`), and
  for a two-endpoint command the two couplers must be within 4 m. There is no other authority
  check (`runtimeAuthorized` is always true).
- On a native failure or invalid read-back the host reconciles back to the pre-command graph and
  answers `NativeMutationFailed`. MU commands also commit a control revision for the affected
  locomotives and include their control state.
- The client sends nothing for the listen host: the host's own changes are observed and committed
  on the next tick. Identical commands raised twice in one frame by native chain callbacks are
  coalesced before sending.

### Interaction versus truth

Dragging a chain, hand pose, audio, and a screw animation are ephemeral presentation. They may use
unreliable or origin-excluding messages and may be dropped. Attached/parked/dangling/tight state is
canonical when it affects later interaction and is included in endpoint state.

Long Unity coroutines must not define the authority transaction. The host can animate toward a
committed state, but the canonical postcondition is recorded synchronously or at an explicit
bounded completion point. A fixed two-second echo-suppression timer is an interim containment
mechanism, not the final design.

Chain dragging is now local-only: the client sends nothing while a chain is dragged and commits
the final placement (`SetChainState`, or `Uncouple` with the final chain state if it was coupled)
when it is dropped. Topology snapshots that arrive mid-drag wait until the chain is let go. Screw
tighten/loosen is a `SetChainState` command. The two-second timer
(`NetworkedTrainCar.SuppressAuthoritativeTopologyObservations`) survives only in the host handler
for the residual `CommonCouplerInteractionPacket`.

## Reconciliation

The client topology reconciler is a dedicated component, separate from input patches and UI-event
replay. For each snapshot it:

1. rejects a different/stale baseline epoch;
2. ignores a topology revision already applied;
3. retains the snapshot if any named car or required native component is not ready;
4. gates interaction on affected endpoints;
5. disconnects relationships not present in the canonical closure;
6. establishes canonical mechanical relationships;
7. establishes canonical hoses and MU links;
8. applies chain, auto-couple, and cock values;
9. verifies reciprocal postconditions from the resulting native objects;
10. records the revision and emits an acknowledgement/diagnostic result;
11. releases interaction gates.

The order prevents a hose or MU cable being restored against the wrong mechanical partner. All
native mutations execute inside a scoped suppression token that lasts for the reconciliation
operation itself. Any coroutine continuation carries an explicit operation token; it does not rely
on a global timeout.

Native `Coupler.CoupleTo` has a partial-failure hazard: it assigns both reciprocal `coupledTo`
fields before creating the joint, then returns `null` without rolling those fields back if joint
creation fails. The adapter must check the joint and derived trainset postconditions and perform a
bounded native rollback before committing. It must not publish the apparently reciprocal pointers
as successful canonical state.

The native `Coupled` callbacks run before `Trainset.Merge`, hose connection, and cock opening.
Those callbacks are dirty observations only; they cannot finalize the transaction. Hose/cock
methods synchronously update reciprocal hose fields and merge/split `Brakeset`, so reconciliation
must use those methods and verify their postconditions rather than assigning fields directly.

`Trainset.Merge` and `Split` synchronously rewrite all car memberships before firing
`TrainsetsChanged`, but do not validate their arguments. The host must validate different
components/endmost cars before merge and reciprocal adjacent cars before split. The event carries
no affected identities and is therefore a dirty/audit signal, not an acknowledgement.

If application cannot complete before a bounded readiness deadline, the client reports a visible
projection failure with the endpoint identities and revision. It then requests a fresh topology
snapshot; it does not invent a default disconnected state.

What `TrainTopologyReconciler` does: it rejects a snapshot from another baseline, ignores one with
an older revision (an equal revision is reapplied, which is a no-op), validates the shape and
invariants, and keeps the snapshot pending if a named car is not initialised, a native step fails
or the local player is dragging a chain. Pending snapshots are retried every 50 ms for 15 s, then
the client logs a warning and sends `ServerboundTrainTopologySyncRequestPacket` (during the join
baseline it fails the join instead). Native application runs in
`TrainTopologyNativeAdapter.TryReconcile` in the order above, inside a scoped
`BeginAuthoritativeTopologyApplication` token, then verifies every endpoint, the joints and
trainset adjacency, and the brake-line pressures. No interaction gate is taken and no
acknowledgement is sent; the failure is a log warning, not a visible report.

After local physics changes couplers without a host packet (a client-only derail, a local
coupler break), the client reapplies the last snapshot's endpoints and brake lines
(`RestoreCanonicalTrainTopology`) without its bundled control states, which may be older than
later control commits.

The host's adapter handles the `CoupleTo` hazard above by checking for a joint and for adjacent
membership in one trainset after mutation, and reconciling back to the pre-command graph on
failure.

## Baseline, interest, and repair

Rolling stock is normally globally relevant because moving cars and trainsets can affect any
player. Interior presentation may stream locally, but car identity, topology, motion, and
simulation do not disappear when an interior unloads.

The initial rolling-stock transaction must contain one topology revision and a self-consistent
endpoint graph. Client readiness is reported only after:

- every declared car exists;
- every endpoint state has reconciled;
- derived trainsets match the canonical mechanical graph;
- initial brake state is applied;
- the baseline completion digest matches.

Repair mechanisms:

- targeted snapshot after every accepted/rejected topology command;
- targeted snapshot when a native host observer detects an uncommitted topology change;
- full topology snapshot on join, resync request, and baseline restart;
- low-frequency host audit of reciprocal invariants and a compact topology digest;
- immediate full correction when a client-reported digest disagrees.

Do not continuously broadcast the full graph. Topology changes rarely; event-triggered canonical
snapshots plus digests provide recovery without packet spam.

Implemented: the join baseline described under "Implementation status" (readiness requires every
declared trainset applied and the baseline topology snapshot reconciled, including its brake
lines; the completion manifest is counts, not a digest); a snapshot after every command; a
snapshot on the tick after the host observes a native change; a full snapshot on a client's
`ServerboundTrainTopologySyncRequestPacket` or `ServerboundTrainSyncRequestPacket` (the host's player
sends neither). Each of these
is the whole graph, not a targeted closure. Not implemented: the periodic host audit and
client-reported digests.

## Controls

Controls are requested values, not client-authored simulation state.

```text
ServerboundTrainControlCommandPacket
  operationId
  carNetId
  controlId
  expectedControlRevision
  value
```

The server verifies:

- finite/range-valid value;
- the control exists and is client-operable;
- the player holds the scoped control lease or has a permitted atomic interaction;
- the player's authoritative position is close enough, unless the control is explicitly remote;
- the request does not conflict with relocation, deletion, or baseline transition.

The host applies the value to `SimulationFlow`/native brake component, reads back its accepted
value, increments the car control revision, and broadcasts a commit to every peer including the
requester. Rejection carries the canonical value. Handbrake, fuses, brake-cylinder release, and
generic ports follow this same contract rather than blind common-packet relay.

MU cable connection is also a control producer. Native `MultipleUnitCable.Connect` establishes
reciprocal cable pointers and then resets/synchronizes throttle, dynamic brake, reverser, sander,
headlights, train brake, and independent brake on both locomotives before raising connection
events. Its host transaction must suppress topology and control observations together, then commit
both the MU endpoint snapshot and resulting control revisions. A topology-only MU acknowledgement
is incomplete.

Continuous controls may be coalesced: keep only the latest unsent value for a `(car, control)` at a
bounded rate, while grab/release and discrete switches remain reliable boundaries. The final value
on release is always reliable and acknowledged.

Control leases prevent two users fighting a lever; they do not make the client a simulation
authority. Leases expire on disconnect, timeout, car deletion, relocation, and loss of eligibility.

As implemented (`NetworkServer.ValidateControlEnvelope` and the `OnCommon*` control handlers):

- Requests are the `Common*` control packets with an operation ID and expected control revision;
  every peer, the listen host included, sends its changes this way. Ports are sent once per tick
  from a dirty set, which is the only coalescing.
- The player must be within one car length of the car (skipped for a remote handbrake). Ports
  also need the port's lease (`ServerboundTrainControlAuthorityPacket`); handbrake, fuses and
  release do not.
- A request whose expected revision is stale is still accepted if its operation ID is newer than
  that player's last one for the car, so a stream of lever moves is not rejected by its own
  commits.
- The host applies the value, reads it back, and answers with `ClientboundTrainControlCommitPacket`
  (`Accepted`, `StaleRevision`, `AuthorityDenied`, `OutOfRange`, `InvalidValue`,
  `UnknownControl`) carrying the accepted values. Accepted commits go to everyone, rejections to
  the requester.
- Leases are released by the client or on disconnect; there is no timeout or eligibility expiry.
- MU connect/disconnect commits control revisions for both locomotives in the same topology
  snapshot, with their control state.

## Brake and locomotive simulation

The host alone ticks locomotive and pneumatic simulation. Clients display snapshots and never feed
locally integrated pressure, traction, wheelslip, or resource values back as truth.

The exception is the ports vanilla writes every frame on every game from that game's own car:
the three `TractionPortsFeeder` writes (forward speed, wheel rpm and wheel speed km/h, which the
speedometers and steam cylinder-cock and blowback effects read) and a steam boiler's tilt
(`BoilerSimController`, read only by the boiler simulation). `SimController.Update` runs these on
clients too; only `SimComponent.Tick` is skipped. The host doesn't send these ports and a client
drops them if an older host does (`NetworkedTrainCar.locallyFedPorts`). Applying the host's too gave
the port two writers: the needle showed the host's value on each frame a packet arrived and the
client's on the next, up to 25 km/h apart at speed. A client's wheel speed doesn't include wheelslip
the host's simulation produces.

Brake replication has two distinct inputs:

- topology/control: reliable canonical hose, cock, handbrake, brake-valve and release state;
- derived simulation: host-produced reservoir, pipe and cylinder pressures, temperature and
  overheat state.

Derived values use thresholded dirty snapshots with a low-frequency heartbeat:

```text
ClientboundTrainSimulationSnapshotPacket
  carNetId
  simulationRevision
  topologyRevision
  hostTick
  dirtyMask
  quantized fields...
```

The topology revision lets a client discard or defer a pressure snapshot computed for a brake-line
graph it has not applied. Thresholds are field-specific; a heartbeat repairs lost unreliable
updates and proves liveness. Exact rates and quantization must be selected from captures, then
recorded as protocol constants and performance tests rather than scattered magic numbers.

Not built as designed. The host sends `ClientboundBrakeStateUpdatePacket` per car (reliable
ordered) when main reservoir, pipe or cylinder pressure moves by 0.02 or more, when reservoir or
overheat state is dirty, or every 2 s as a heartbeat (`NetworkedTrainCarServer.SendBrakeStates`).
It carries the host's topology revision, the car's brake-line anchor and member digest, the three
pressures and the heat state. A client drops it and asks for a topology resync if the revision is
newer than its own; if the line membership doesn't match it applies the per-car fields, skips the
pipe pressure and asks for a resync. There is no `simulationRevision`,
dirty mask or quantization, and no other locomotive simulation state is snapshotted this way.

Brake-pipe pressure is line state. Native `Brakeset.Merge` produces a volume-weighted shared
pressure, while `Split` copies the previous shared pressure into both new lines. A topology revision
therefore defines one or more brake-line components, each with one canonical pipe pressure:

```text
ClientboundBrakeLineSnapshotPacket
  topologyRevision
  lineAnchor
  memberDigest
  hostTick
  pipePressure
```

`lineAnchor` deterministically identifies the component within that topology revision; it is not a
persistent brakeset ID. Main/aux/control reservoirs, cylinder state, heat, and smoothed readouts
remain per-car simulation fields. A merge/split topology commit includes immediate brake-line
snapshots so clients do not derive pressure from stale local pre-operation values.

There is no separate brake-line packet: `BrakeLineStateData` (anchor car, anchor end, member
digest, pipe pressure) rides in every topology snapshot and, per car, in the brake-state packet.

Native hose events run before brake-set merge/split and are not exception-contained. The host
adapter catches exceptions, then checks reciprocal hose pointers, brake-set membership/order, and
line pressure before deciding whether to commit or repair. A Unity callback is never proof of
brake-line convergence.

## Showing the simulation on clients

Clients don't tick the simulation: `SimComponent_Tick_Patch` blocks every `SimComponent.Tick`
off the host. What a client shows of it, its gauges, sounds, particles and animations, is the
host's port values, read by DV's port readers (`LayeredAudioPortReader`, `IndicatorPortReader`,
`AnimatorPortReader`, the particle readers) whenever a port's value changes. Those values go out
at the network tick (24 Hz) in the control commits, land unevenly, and used to be applied as they
landed. So whatever a player's control drives through the simulation came back a round trip late,
in steps: at 110 ms a VR player pulling the FEF-3's whistle cord heard it go straight to full with
no taper, and another player heard that player's whistle stutter. Levers themselves are fine: they
move locally.

`TrainPortPresenter` (one per car, on clients) shows the outputs two ways instead. Physics stays
the host's: torque, motion, pressures, resources. This is only what a client shows of them.

**Prediction, for the player driving.** Outputs that are a function of controls and slowly
changing host state are worked out on the client, every frame, while the local player is driving
the train: they've changed one of its cars' controls in the last 4 s, or hold a control's lease
(`NetworkedTrainCarClient.IsLocalPlayerDrivingTrain`). For vanilla components with no other state,
their own `Tick` runs (`SimComponent_Tick_Patch.LocalTick` lets it through); the rest are copies
of the part of `Tick` that sets the output. The host's values for those ports are kept but not
shown, and take over again, played out, once the player stops driving. The audit (DV.Simulation,
checked against the live S282, FEF-3/S464, DE2 and DE6 graphs on 2026-10-19):

| Output | Inputs | Predicted |
|---|---|---|
| Whistle, `exhaust.WHISTLE_FLOW_NORMALIZED` (`SteamExhaust`) | `whistle.EXT_IN`, `boiler.PRESSURE` | Yes: `Min(InverseLerp(1, 2, pressure), control)`. The rest of `SteamExhaust` (draught, steam use, exhaust flow) is the host's |
| Horn, `horn.HORN` (`Horn`), and its smoothed control `hornControl.CONTROL` (`GenericControl`) | the horn control, its fuse | Yes, both components' `Tick` |
| Bells, `bell.BELL_NORMALIZED` (`SteamBell`, `ElectricBell`) | the bell control, boiler pressure or a fuse | Yes, `Tick` (on at once, eased off) |
| Dynamo, `dynamo.DYNAMO_FLOW_NORMALIZED` (`Dynamo`) | the dynamo control, boiler pressure | Yes, `Tick` |
| Sanders, `sander.SAND_FLOW`, `SAND_COEF` (`Sander`) | the sander control, sand left, a fuse | Yes, without taking the sand (the host's) |
| Reverser readout, `reverser.REVERSER` (`Reverser`) | the reverser control | Yes, `Tick` |
| Smoothed controls: `GenericControl`, `SmoothedOutput` (`cabFan.OUTPUT`, `lubricatorControlSmoothing.OUTPUT`) | their control, a fuse | Yes, `Tick` |
| A handcar's crank, `handcarDrive.CURRENT_POSITION` and `DIRECTION` (`HandcarDrive`) | the wheel RPM (the client's own traction feeder), the gear ratio, and where the crank was | Moved on from the wheel RPM every frame, turning at the ends, and eased toward the host's position brought forward by its age (its tick's distance behind the clock, plus half the ping). Replaces PR #91's `HandcarCrankSmoothingPatch`. Making the crank follow the player's hand instead was tried: it took away the resistance you pump against and the car stopped |
| Lamps (`LampLogic`) | a control or a readout | Already local: they update on a port change, not on `Tick` |
| Blowdown, `boiler.BLOWDOWN_FLOW_NORMALIZED` | the blowdown control, the vessel's pressure and water | Could be: `Boiler` reads its vessel, not the `PRESSURE` port. Played out for now |
| Injector, `boiler.INJECTOR_FLOW_NORMALIZED` | the injector control, gated on tender water, a broken boiler and a full one | Could be, with a gate the client can't fully see. Played out for now |
| Cylinder cocks, `steamEngine.CYLINDER_COCK_FLOW_NORMALIZED` | the control times the cylinders' admission pressure, internal to the engine | No: the host's |
| Engine RPM, amps, traction, boiler and brake pressures, temperatures, water level, exhaust and chuffs, crank rotation, safety valve, compressor production, fuel, oil, sand and water levels, damage | the host's simulation | No: played out |

All of it runs once a frame before the car's simulation controllers (`SimControllerUpdatePatch`, a
prefix on `SimController.Update`), so a `HandcarController` draws the crank predicted that frame.

**Playout, for everyone else.** Every other output port the host sends (typed `READONLY_OUT` or
`OUT`), and every control a player drives (`EXTERNAL_IN`, player-driven by `ControlPortWriters`:
another player's doors, windows, levers and valves, the whistle cord), is stamped with the host's
tick and shown a little behind the newest value, interpolated between ticks (`PlayoutTrack`,
Multiplayer.Core). A control's port moves the control with it, through its `InteractablePortFeeder`;
vanilla levers and pullers jump straight to the value they're given (`AcceptSetValue`), so as they
landed another player's door swung in uneven 24 Hz steps:

- The commit carries the tick (`ClientboundTrainControlCommitPacket.Tick`). The host's own values
  are stamped with the tick its local client read them at (`CommonTrainPortsPacket.Tick`), since
  that packet reaches the server a frame or so later; a player's are stamped when the host takes
  them.
- `PlayoutClock` measures the link from the commits' arrivals: each gives the offset between the
  client's clock and the host's ticks; the lowest recent offset is the latency, and how far
  arrivals run above it is the jitter (a peak that decays by 10 ms a second). An observer's delay
  is one tick plus the jitter (with 15 % headroom), at least two ticks (83 ms) and at most
  `Max Readout Delay` (300 ms by default). One tick is so the value after the one shown has
  arrived by the time it's needed.
- The delay is per car (`PlayoutDelay`) and zero while the local player drives the train, so the
  driver sees the host's values as they land, as before. It grows by a quarter of real time (the
  values play at 75 % speed for a moment) and shrinks at double speed, so time never jumps back.
- The host sends a port only when it moved by 0.001 (a `STATE` port when it changes at all), so a
  long gap between samples can be a port that sat still and then moved: past four ticks the older
  value holds until the tick before the newer one.
- Being typed `STATE` doesn't make a port discrete: bells, the dynamo, cylinder cocks, the safety
  valve and the crank's rotation are all `STATE`, next to lamp states and chuff pulses. `GENERIC`,
  `CONTROL`, `STATE` and `DAMAGE` ports blend, but a change of more than 0.5 in one sample is a
  step (a crank's wrap from 1 to 0, a lamp switching); physical quantities always blend. A step
  passed over within one frame (a one-tick chuff pulse on a slow frame) is still written, in order.
- A port something on the client writes itself (a feeder or a controller, or `LampLogic`) is found
  by its value differing from what the playout last wrote, and from then on shown as it lands.
- A control is the local player's while they hold its lease or have a change to it not yet
  committed (`NetworkedTrainCarClient.HoldsPort`): never played out over their hand; the host's values for it are
  shown as they land, as before (a pending change's are dropped, `WithoutPending`). One that changes
  between the playout's writes was moved by the local player: its history restarts at their value.
- The playout's own writes aren't the local player's: `Common_OnPortUpdated` ignores them
  (`TrainPortPresenter.Writing`), as it does a packet's, so they're never sent, never read as an
  unleased change (a control differing from the host's last value), and don't count as driving.
- A resync (a control state without a tick) is shown as it lands and restarts that port's history.

**Not done:**
- The host applies a remote player's control as it arrives, so the uneven arrivals of that
  player's 24 Hz sends are baked into the host's own outputs (the whistle a friend at 110 ms pulls
  steps unevenly on the host itself). Playout on observers makes those outputs continuous, not
  even. Evening them out would need the host to play remote players' continuous controls out on
  the sender's tick, a delay of a tick or two on that player's physics.
- Ports a client's own game writes every frame (`TractionPortsFeeder`'s speed and wheel RPM,
  `BoilerSimController`'s boiler angle) are `EXTERNAL_IN`, so the playout leaves them alone, but the
  host also sends them and the two writers alternate (PR #92 stops that for the traction ports).

Settings (advanced): `Smooth Train Readouts`, `Max Readout Delay (ms)` and `Predict Train Sounds`,
all read live, for comparing with and without in a game.

## Motion

Existing on-rail motion remains host-authoritative:

- wire speed is metres per second;
- bogie anchors and track identity come from the host;
- prediction uses the speed from the same host snapshot;
- clients do not tick locomotive simulation;
- derailed cars use authoritative rigidbody snapshots.

Every motion sample carries `motionEpoch`. Spawn, teleport, relocation, rerail, derail-mode change,
or a discontinuous track correction increments the epoch and clears client interpolation queues.
Packets from an older epoch are ignored even if their host tick arrives later.

Full transform corrections are recovery. Metrics must prove they are rare; repeated correction is
not accepted as smooth synchronization.

### What the code does

Host (`NetworkTrainsetWatcher`): every network tick (24 Hz, advanced by elapsed real time so it
keeps step below 24 fps) each moving trainset is sent as one `ClientboundTrainsetPhysicsPacket`,
unreliable unless a bogie changed track. On-rail cars carry speed, slow stress and both bogies'
track and span; derailed cars carry a rigidbody snapshot. Every 2 s a car also carries its full
position and rotation, even when stationary. The speed is `GetForwardSpeed()` multiplied by the
host's simulation rate (game time per real second, averaged over about a second), so a host that drops
to a few frames a second doesn't send speeds its world isn't making. A full sync of a car whose
rigidbody is still more than 5 m from its transform (a just-spawned car whose body is at its
pooled position) sends the transform instead. A set counts as moving while any car is not
vanilla's `isStationary` (moved under 1 cm in the last second). On the tick a set stops, it is sent
once more as a full sync, reliably, so clients learn exactly where it stopped; after that only the
2 s full sync goes out.

Client on-rail cars (`TrainSpeedQueue`, `NetworkedBogie`): every physics step the car runs at the
host's speed plus a correction proportional to how far the host's bogies are ahead along the
track, projected to now (gain 2/s, capped at 3 m/s plus 25% of the speed, 2 cm deadband). The
host speed eases at up to 8 m/s² and the correction at up to 4 m/s², except that a speed step
over 1.5 m/s (an impact) is taken at once. A bogie on a different track from the host's is set
onto the host's track unless it is just crossing to the adjacent one. A car taken over from local
physics starts at the host's speed with no correction (its local motion never happened on the
host). If moving samples stop arriving for 0.5 s the car is handed back to local physics at the
host's last speed, without the correction term.

A sample under 0.01 m/s (vanilla's stationary threshold) means the host's car is stopped, and the
client holds its car there (`TrainSpeedQueue.Hold`); it is followed again only above 0.05 m/s, so a
car crawling around the threshold doesn't flip between modes. A held sample is not projected, and
goes stale only after 10 s (the host resyncs every car every 2 s; an older one is for something the
host no longer has). Within 5 cm the rigidbody is not touched: writing a velocity wakes the car, and an
awake, unbraked car creeps and wakes every car on its track (vanilla's `TrainsOptimizer`). Further
off for more than 0.5 s (so a local loco pushing the car isn't fought before the host's samples
arrive):

- **A single car is placed** (#63): made kinematic and slid along the track to the host's spans at
  0.5 m/s (1 m/s while more than 1 m off), moving its bogies' travellers with `MoveToSpan` and
  refreshing their `point1`/`point2` as `Bogie.SetTrack` does (vanilla advances a traveller by the
  bogie's movement since those points; left stale, the first dynamic step threw the bogie). Velocity
  corrections of cars resting buffer to buffer after a pile-up shoved each other off their places
  for minutes; placed cars can't be knocked and end at the host's poses, which don't overlap.
- **A placed car touching other stopped cars is parked**, left kinematic: handed back to physics, the
  compressed buffer springs it rests against pushed it and its neighbour apart again. It is released
  when its host car moves or a touching car follows a moving host car; an untouched car is released
  and sleeps.
- **A coupled car is driven** back by velocity at 0.2-0.5 m/s, which the coupler joints carry along,
  then stopped. "Coupled" is the host's trainset (the set the host sent the car with): right after a
  coupling impact the client applies the coupling a moment later, and placing the car alone as the
  joint formed held the consist metres off. A coupled car that can't be moved in 10 s is left for
  10 s.

A moving car touching a held one follows the host's speed only, with no position correction, so it
doesn't shove the held car off its place.

Full-sync positions are a safety net. The host pose is projected forward by the sample's age
(capped at 0.5 s); if the car is more than 5 m from it (`POSITION_UPDATE_THRESHOLD` in
`NetworkedTrainCar`), the body and its bogies are moved there and stopped, its rotation is left
alone, and the motion queues and train stress are reset. `isKinematic` is only written when it
changes.

Derailment: a rigidbody snapshot for an on-rail client car clears its bogie samples (they are for
the rails it left), calls `TrainCar.Derail()` once, then interpolates the snapshots
(`NetworkedRigidbody`, only while the car is derailed); afterwards the client reapplies the host's topology, because native
derailment changes couplers. The rigidbody target is extrapolated from the host tick of its sample
(up to 0.5 s), the same time base as the on-rail cars' bogie samples. A dynamic car is steered by
its velocity, every physics step: the host's velocity plus 4/s of the way to the extrapolated pose
(6/s for its rotation), clamped to 1.5 m/s (45 degrees/s), as the on-rail cars are steered by
theirs (`TrainSpeedQueue`). One at rest where the host has it is left alone, so it sleeps. Moving it
there with `MovePosition` every step was a teleport its coupler joints then answered: derail-drag
measured the gap at its coupling changing 6 times as fast as on the host (2026-10-20). A
kinematic car is still placed. Before, the target's age was
counted from when the sample was processed and the velocity was replaced on every sample: a
derailed flatbed dragged at 30 km/h between on-rail cars was aimed ~0.3 m behind them and jolted
24 times a second, carrying ~12 times the host's coupler force and juddering the whole consist
(playtest, 2026-10-19). A derail also softens the car's couplings, as in vanilla
(`TrainCar.UpdateCouplerJoints`): they stretch on a 1 MN/m spring and break at 1.35 MN, on host
and client. Clients used to skip this (4997bb3, from before client breaks were undone), so a
derailed car's couplings were stiff here but stretched on the host, and our coupler fought the
host's pose. A car derailed on a client but on the rails on the host is rerailed
onto the host's track at the host's pose (asking for a full sync if the sample lacks one), its
queues are cleared, and the topology is reapplied once the rerail finishes
(`NetworkedTrainCarClient.RerailToHost`). `TrainCarRerailPatch` lets any derailed car be rerailed after 30 s even if
it never drops below vanilla's 1 m/s limit. Explosions never push client trains; the host's
result arrives through this sync (see [`../world-sync.md`](../world-sync.md)).

Host rerail and move-to-track (`ClientboundRerailTrainPacket`, `ClientboundMoveTrainPacket`;
`NetworkedTrainCarClient.RerailFromHost`, `MoveToTrackFromHost`): the client clears the
speed, rigidbody and bogie queues and makes the body dynamic (a derailed car at rest is kinematic,
and vanilla's rerail needs a dynamic one), lets vanilla relocate the car (a coroutine: one physics
step, `IsTeleporting`), ignores samples meanwhile, clears the queues again and asks the host for a
full sync (`ServerboundTrainSyncRequestPacket`). The host, once its own relocation is done, sends
the car's pose and both bogies' tracks and spans reliably on the next tick
(`NetworkedTrainCarServer.ResyncAfterRelocation`, `ResyncPose`; `DirtyAllState` does the same).
In playtest 2 none of this was reset: the rigidbody queue kept sliding the rerailed FEF-3 towards
its derailed pose at its 1.5 m/s limit, the speed queue placed it back towards the host's spans,
and each 2 s full sync found it over 5 m off and snapped it, flinging the body off its bogies
(5-7 m/s, spinning), every ~1.3 s. Scenario `rerail`.

There is no `motionEpoch`; other stale samples are discarded only by tick order. The relocation packet with
its acknowledgement exists on the client, but the host never sends it.

Open issues: an intermittent ~5.8 m snap at ~10 m/s on clients (#44). Vanilla jitter can keep
idle yard cars above the 1 cm/s stationary line on the host, so they are sent every tick; clients
now hold them quietly, but the bandwidth remains. A host-side "settled" rule with hysteresis for
sync (not physics) would fix that.

## Loading-state delivery

Every broadcast helper must enforce its declared minimum loading state. A helper that accepts
`minimumLoadState` but does not check it can deliver topology to a peer before rolling-stock
baseline readiness and create an unrecoverable ordering race.

Canonical snapshots received before object readiness are retained in a bounded pending map keyed
by epoch/revision. They are not dropped and are not applied to a guessed prefab.

Implemented: `SendPacketToAll` skips players below the minimum loading state, and reliable train
broadcasts for a player whose train baseline is in flight are held (up to 20,000) and delivered
once they acknowledge it (`HoldForTrainBaseline`, `FlushHeldTrainTraffic`). On the client the
reconciler keeps only the latest unapplied snapshot, not a map.

## Performance budget

The architecture avoids replacing event spam with snapshot spam:

- topology: event-driven reliable transactions; compact digest heartbeat;
- discrete controls: reliable commits only when values change;
- continuous controls: per-control coalescing and reliable final value;
- simulation: dirty-mask thresholding plus heartbeat;
- motion: fixed network tick, unreliable latest-state delivery, periodic recovery anchor;
- interiors: no global physical-control objects; bind presentation on local load;
- audits: incremental dirty endpoint/car sets, not full-scene scans per frame.

Instrumentation must report packets/sec and bytes/sec by train subsystem, dirty-set size, pending
reconciliation count, correction distance, rejected commands, and digest mismatches. Performance
regressions are test failures, not dashboard impressions.

None of these per-subsystem counters exist yet. The general packet and traffic monitor in
[`../observability.md`](../observability.md) is the only measurement. Topology has no digest
heartbeat, and every topology snapshot is the full graph.

## Failure handling and observability

Every topology/control command result has a stable reason code such as:

```text
Accepted
UnknownCar
UnsupportedEndpoint
OutOfRange
AuthorityDenied
StaleRevision
InvalidRelationship
NativeMutationFailed
BaselineChanged
```

Structured events include operation ID, player, endpoint/car IDs, expected and actual revisions,
pre-state, requested transition, post-state, validation result, and reconciliation duration.
High-frequency motion/simulation logging is sampled or aggregated; it must never flood the log.

Implemented codes: topology commands use `TrainTopologyResultCode` (`Accepted`, `UnknownEndpoint`,
`UnsupportedEndpoint`, `StaleRevision`, `RevisionExhausted`, `InvalidRelationship`,
`EndpointOccupied`, `WouldCreateCycle`, `NotConnected`, `InvariantViolation`,
`NativeMutationFailed`, `AuthorityDenied`, `OutOfRange`, `BaselineChanged`; the last is never
produced), controls use the `TrainControlCommitResult` codes listed under "Controls". Events in
the `train` debug category include `train.topology-command-result` (player, operation, kind,
endpoints, result, revisions), `train.topology-snapshot-committed`,
`train.topology-snapshot-applied`, `train.control-commit-applied`, and sampled
`train.physics-received`. There is no pre/post state or reconciliation duration.

The dashboard should expose for each live train (not built; the combined dashboard was removed in
f510e8e):

- cars and derived trainset membership;
- both endpoint records per car;
- topology/control/simulation/motion revisions;
- hose/cock/MU graph;
- brake pressure comparison by peer;
- last command and rejection;
- correction-rate and smoothness metrics;
- targeted resync and evacuate/stop/delete controls.

## Required test matrix

### Pure protocol/state-machine tests

1. Every topology packet round-trips multiple endpoint records without default entries.
2. Couple/uncouple produces reciprocal endpoints atomically.
3. Hose and MU relationships cannot become one-sided.
4. A stale command is rejected with current canonical state.
5. Replaying an operation ID or snapshot is idempotent.
6. Repairing a wrong partner includes and clears the old endpoint closure.
7. Revision and tick comparisons work across unsigned wrap.
8. Missing capabilities are handled without null dereferences.

`Multiplayer.Core.Tests/Trains/TrainTopologyStateMachineTests.cs`,
`TrainBaselineReceiptTests.cs` and `Multiplayer.Protocol.Tests/TrainTopologyPacketTests.cs` /
`TrainBaselinePacketTests.cs` cover 1–5 and 8 in part. There is no wrap test (7; comparisons are
not wrap-safe) and no wrong-partner repair test (6).

### Two-process runtime scenarios

1. Client couples two cars; both peers agree immediately on endpoints and trainset membership.
2. Client uncouples under load; both peers split the same trainset without stretching.
3. Connect/disconnect hose and toggle both cocks; brake-line topology and pressures converge.
4. Connect/disconnect MU and verify compatible locomotive controls across peers.
5. Reject distant, impossible, and stale operations and roll back optimistic presentation.
6. Host far away, client operates a cab, then host enters after the interior was unloaded.
7. Both players leave a consist and return after interest/interior retirement.
8. Join in progress while a coupled, charged brake line is moving.
9. Default-spawn train with engaged handbrake before and after interior load.
10. Deliberately different host/client frame rates and pause-menu states.
11. Packet loss/reordering simulation for motion/simulation; reliable topology still converges.
12. Derail, rerail, relocation, deletion, and disconnect during an outstanding command.

Tests assert instantaneous graph/state agreement and visible quality, not only eventual scenario
completion. Motion scenarios record maximum/RMS correction, correction frequency, speed error, and
bytes/sec.

dvmp scenarios in `DevTools/Scenarios/` (all in the gate) cover part of this: `couple-impact` and
`manual-uncouple MODE=recouple` (1, with lag), `uncouple-moving` (2), `manual-uncouple` (3: hand
uncouple through cocks, hoses and chain), `fps-drop` and `host-stall` (10: low client and host
frame rates), `client-derail` (12: client-only derail), `rerail` (12: host rerail, requested by a
client or done by the host). The others have no scenario. Scenarios
check agreement and position error; they don't record bytes/sec.

## Native research status

The required topology adapter bodies have now been inspected: coupler couple/uncouple, coupler
logic wrappers, trainset merge/split, hose/cock connect/disconnect, brakeset merge/split/tick,
handbrake and cylinder release, and MU cable/module behavior. Their sequencing and partial-failure
windows are reflected in this design and the linked research document.

Additional dnSpy work should now be driven by a concrete adapter or test question rather than broad
assembly exploration. The canonical wire rule remains unchanged: peers exchange final revisioned
state, not Unity event history.

## Staged implementation plan

### Stage 0: containment (complete)

- apply accepted coupler/hose/cock actions on the listen host;
- suppress recursive observation;
- make UI couple/uncouple receipt idempotent;
- retain diagnostics for known desync signatures.

This reduces immediate damage but remains semantic event replay.

### Stage 1: detached topology core (implemented)

- introduce endpoint IDs, capabilities, canonical endpoint records, revisions, and invariants in
  code that does not depend on Unity;
- implement command validation, affected-closure calculation, and idempotent transition tests;
- add packet codec tests before runtime integration.

### Stage 2: canonical coupler/hose/cock snapshots (implemented)

- add serverbound commands and clientbound snapshots;
- implement host native adapter and client reconciler;
- include snapshot in baseline/resync;
- keep old interaction packets only for transient presentation during migration.

Runtime-verified by the dvmp coupling scenarios. Menu, cab, remote-controller and decoupler
coupling were moved onto the command path afterwards (eae970d, 5af4fc4, 74c449a).

### Stage 3: MU topology (implemented)

- move MU cables into the same endpoint transaction model;
- remove relay-only MU handlers after compatibility transition.

### Stage 4: controls and brakes (partly implemented)

- convert handbrake, release, fuses, and ports to host commit/ack (done);
- add revisioned, thresholded brake simulation snapshots and heartbeat (thresholds and heartbeat
  done in the brake-state packet; no simulation revision);
- verify topology-revision gating for pressure state (done).

### Stage 5: motion quality (not started as designed)

- add motion epochs and correction telemetry (not done);
- enforce quantitative smoothness and bandwidth budgets across frame rates and VR (not done).

Motion smoothing that landed outside this plan is listed under "Implementation status" and
"Motion": bogie steering, impact following, simulation-rate-scaled speed, the real-time network
tick, the pooled-rigidbody guard and the client-only derail rerail. `fps-drop` and `host-stall`
assert a maximum position error.

### Stage 6: delete legacy authority paths (partly done)

- remove common mutation packets and timed suppression (not done: `CommonCouplerInteractionPacket`
  and the `Common*` control classes remain, and the client still subscribes their receivers);
- retain only canonical command/snapshot paths and explicitly transient animation messages;
- make protocol-version mismatch fail during connection rather than partially interoperate (done).

## Definition of done

Train synchronization is item-grade only when:

1. every shared train mutation has one named host-owned canonical record;
2. clients send intent and always receive accepted or corrective canonical state;
3. topology transactions preserve reciprocal invariants atomically;
4. late load, re-entry, retry, duplication, rejection, and resync converge without event history;
5. host simulation is independent of which players are nearby or in VR;
6. tests prove graph agreement, pressure/control agreement, smoothness, and bandwidth;
7. the dashboard can explain a divergence using revisions and reason codes;
8. no legacy relay-only mutation path can bypass the authority model.
