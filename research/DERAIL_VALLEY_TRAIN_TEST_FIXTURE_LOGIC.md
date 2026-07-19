# Derail Valley Train Spawning, Simulation, and Runtime Fixture Logic

This document records established Derail Valley and existing multiplayer behavior relevant to
debug-only train fixtures. The intended fixture system will create disposable host-authoritative
locomotives and consists for item, physics, snapping, EOT, gadget, wiring, and train-control runtime
tests without changing saves or returning through the main menu.

This is a research document. Confirmed runtime and source facts are kept separate from proposed
fixture policy. Generic runtime-test orchestration remains in
[`MULTIPLAYER_RUNTIME_TEST_HARNESS_DESIGN.md`](../MULTIPLAYER_RUNTIME_TEST_HARNESS_DESIGN.md).
The implementation model, transaction boundaries, commands, cleanup contract, and rollout phases
are defined in
[`MULTIPLAYER_DEBUG_TRAIN_FIXTURE_DESIGN.md`](../MULTIPLAYER_DEBUG_TRAIN_FIXTURE_DESIGN.md).
Loose and snapped train items are covered by
[`DERAIL_VALLEY_ITEM_SNAPPING_LOGIC.md`](DERAIL_VALLEY_ITEM_SNAPPING_LOGIC.md), and installed train
gadgets by
[`DERAIL_VALLEY_GADGET_CUSTOMIZATION_LOGIC.md`](DERAIL_VALLEY_GADGET_CUSTOMIZATION_LOGIC.md).
The game's built-in developer and debug commands, along with the underlying APIs worth reusing,
are catalogued in
[`DERAIL_VALLEY_DEV_CONSOLE_LOGIC.md`](DERAIL_VALLEY_DEV_CONSOLE_LOGIC.md).

## Research questions

The fixture implementation needs authoritative answers to the following questions:

1. Which Derail Valley API creates one car or an ordered consist at a precise point on a
   `RailTrack`?
2. Which stable identifiers select a livery, track, direction, and position along the track?
3. Which delayed initialization and coupling phases must complete before a spawned consist is
   usable?
4. What exact diesel simulation state causes an engine to start, stop, or stall?
5. Which ports, fuses, resources, damage values, temperatures, or controllers must be protected to
   sustain a locomotive under load?
6. Where does derailment begin, and what is the earliest fixture-scoped prevention point?
7. How are signals, junctions, routes, and track occupancy represented and restored?
8. Which transform is the correct local-player teleport target for each locomotive interior?
9. What is the complete safe deletion sequence for a coupled, occupied, customized consist?
10. Which existing multiplayer acknowledgements are sufficient to prove every client has created
    or destroyed every fixture car?

## Sources inspected

Existing multiplayer implementation:

- `Multiplayer.Components.Networking.Train.NetworkedCarSpawner`
- `Multiplayer.Components.Networking.Train.NetworkedTrainCar`
- `Multiplayer.Components.Networking.Train.NetworkTrainsetWatcher`
- `Multiplayer.Components.Networking.Train.NetworkedBogie`
- `Multiplayer.Components.Networking.Train.TrainSpeedQueue`
- `Multiplayer.Components.Networking.Train.NetworkedRigidbody`
- `Multiplayer.Components.Networking.World.NetworkedRailTrack`
- `Multiplayer.Components.Networking.World.NetworkedJunction`
- `Multiplayer.Components.TrainComponentLookup`
- `Multiplayer.Networking.Data.Train.TrainsetSpawnPart`
- `Multiplayer.Networking.Data.Train.TrainsetMovementPart`
- `Multiplayer.Networking.Data.Train.BogieData`
- `Multiplayer.Networking.Data.Train.BrakeSystemData`
- `Multiplayer.Patches.Train.CarSpawner_Patch`
- `Multiplayer.Patches.Train.TrainCarPatch`
- `Multiplayer.Patches.Train.SimComponent_Tick_Patch`
- `Multiplayer.Patches.CommsRadio.CommsRadioCarSpawnerPatch`
- `NetworkServer.OnServerboundTrainSpawnRequestPacket`
- `NetworkClient.OnClientboundSpawnTrainSetPacket`
- `NetworkClient.OnClientboundDestroyTrainCarPacket`
- `ServerboundTrainSpawnRequestPacket`
- `ClientboundSpawnTrainSetPacket`
- `ClientboundDestroyTrainCarPacket`
- `ClientboundTrainsetPhysicsPacket`
- `CommonTrainPortsPacket`
- `CommonTrainFusesPacket`
- `ServerboundTrainControlAuthorityPacket`

Derail Valley types already visible through those integration points:

- `CarSpawner`
- `TrainCar`
- `Trainset`
- `TrainCarLivery`
- `Bogie`
- `RailTrack`
- `RailTrackRegistry`
- `Junction`
- `SimController`
- `SimulationFlow`
- `LocoSim.Implementations.SimComponent`
- `LocoSim.Implementations.Fuse`
- `LocoSim.Implementations.DieselEngineDirect`
- `LocoSim.Implementations.DieselEngineDirectDrive`
- `LocoSim.Implementations.DieselEnginePowerSource`
- `LocoSim.Implementations.DirectDriveMechanism`
- `DV.Simulation.Ports.InteractablePortFeeder`
- `DV.Simulation.Ports.TractionPortsFeeder`
- `InteriorControlsManager`
- `BrakeSystem`
- `CommsRadioCarSpawner`

## Executive conclusions

1. The multiplayer mod already possesses the central host-authoritative train replication pipeline
   required by fixtures. A test fixture should call the host's real spawn APIs and allow the
   existing `CarSpawner` patches and train packets to replicate the result.
2. The existing client spawn snapshot is substantially complete: identity, livery, GUID, health,
   restoration state, paint, customization holes, couplings, speed, transform, both bogies, tracks,
   and brake state are already transmitted.
3. Derail Valley track positions are already compactly addressable by a networked `RailTrack` plus
   an index into `RailTrack.GetKinkedPointSet().points`. The request also carries orientation with
   or against the track direction.
4. `NetworkTrainsetWatcher` already walks every `Trainset` on the host and publishes movement for
   moving consists. Runtime fixtures do not need a separate train movement protocol.
5. `SimComponent_Tick_Patch` permits locomotive simulation only on the host. Fixture safety and
   engine-sustain policy must therefore run on the host, not independently on each client.
6. Simulation ports and fuses are already identified and replicated by stable FNV-1a hashes of
   their string IDs. Three distinct diesel implementations have now been identified; fixture
   policy must select behavior from the actual `SimComponent` type rather than assuming every
   diesel stalls in the same way.
7. Train controls already have server-mediated per-port authority. A fixture driver may either
   exercise those real controls or use a clearly labelled host-side arrangement adapter around the
   same simulation ports.
8. Car deletion is already networked through `CarSpawner.PrepareTrainCarForDeleting` and
   `ClientboundDestroyTrainCarPacket`, but occupied-train and coupled-consist cleanup still needs a
   verified transaction and acknowledgement barrier.
9. Junction state is networked. No equivalent signal or route-leasing implementation has yet been
   located in this repository.
10. The train fixture should be an orchestration layer over established behavior, not a second
    train simulation or replication system.

## Existing authoritative spawn path

### Client or radio request

The current comms-radio integration resolves:

```text
livery ID
networked RailTrack ID
point index within RailTrack.GetKinkedPointSet().points
with/against track direction
```

and sends `ServerboundTrainSpawnRequestPacket`.

### Host validation

`NetworkServer.OnServerboundTrainSpawnRequestPacket` currently validates:

1. the requesting player;
2. the `NetworkedRailTrack`;
3. the `TrainCarLivery` and its prefab;
4. the kinked-point index;
5. available physical space using `CarSpawner.GetBoundsOfCar` and
   `CarSpawner.IsThereSpaceForCarOnPoint`;
6. the player's distance from the requested spawn point.

It then derives the direction from the selected point and calls:

```csharp
CarSpawner.Instance.SpawnCarFromRemote(
    livery.prefab,
    railTrack,
    spawnPoint.position,
    forward);
```

The distance check belongs to the gameplay radio request. A debug-only host fixture API should not
pretend to be a nearby player, but it should retain the livery, track, point, and space validation.

### Host replication

`CarSpawner_Patch` observes `SpawnCars`, `SpawnCarFromRemote`, and
`SpawnCarOnClosestTrack`. On the host it calls `SendSpawnTrainset`, which serializes the resulting
cars through `TrainsetSpawnPart.FromTrainCar`.

The existing snapshot contains:

```text
NetworkedTrainCar NetId
livery ID
TrainCar ID
CarGUID
exploded and health state
player-spawned flag
restoration state and related cars
interior and exterior paint
customization holes
front and rear coupling state
forward speed
absolute transform
both bogie track IDs, positions, directions, and derail state
brake-system state
```

This is already suitable for single cars and ordered multi-car consists.

### Client reconstruction

`NetworkedCarSpawner.SpawnCars`:

1. resolves each livery and both bogie tracks;
2. obtains a pooled car or instantiates the livery prefab;
3. restores the car ID and GUID through `InitializeExistingLogicCar`;
4. restores damage, paint, and restoration metadata;
5. assigns the authoritative train NetId;
6. restores transform and bogie placement;
7. fires `CarSpawner.FireCarSpawned`;
8. restores brakes and customization holes;
9. couples cars back-to-front;
10. seeds the client speed queue.

The fixture readiness barrier must wait beyond packet receipt. At minimum it must verify that every
expected NetId exists, `Client_Initialized` is true, both bogies resolve the expected tracks, the
expected `Trainset` exists, and coupling agrees on the host and client.

## Existing movement authority

`NetworkTrainsetWatcher.Server_OnTick` iterates `Trainset.allSets`. For each moving trainset it
publishes a `ClientboundTrainsetPhysicsPacket` containing every car. Ordinary on-rail cars send:

- forward speed;
- train stress;
- both bogie states;
- occasional full transform correction;
- track changes when dirty.

Derailed cars instead send rigidbody snapshots. A full correction is forced after approximately
two seconds without synchronization.

`SimComponent_Tick_Patch` prevents all `LocoSim.Implementations.SimComponent` subclasses from
ticking on clients. The host is therefore the live engine, electrical, pneumatic, and resource
simulation authority.

This is the correct foundation for moving-train item tests: the fixture changes real host controls,
the normal host simulation moves the consist, and the existing train physics stream moves clients.

## Existing control, port, and fuse behavior

`NetworkedTrainCar` discovers its `SimController.SimulationFlow` and registers every port and fuse.
String IDs are converted to stable `uint` values using FNV-1a hashing.

The implementation already:

- records dirty simulation ports;
- records dirty fuse states;
- sends `CommonTrainPortsPacket` and `CommonTrainFusesPacket`;
- applies remote port values through `SimulationFlow`;
- applies fuse state with `Fuse.ChangeState`;
- obtains and releases server authority for interactive interior controls;
- blocks a client's control projection while another player owns that port.

This makes fixture-scoped fuse protection feasible, but the correct hook remains unknown. Repeatedly
forcing every fuse on would hide the trip cause and produce unnecessary network churn. The preferred
solution is to prevent or override trips only for registered fixture locomotives, while logging each
suppressed trip attempt.

`SimulationFlow` is also a useful runtime catalogue. It constructs components in the definition's
`executionOrder`, exposes the ordered components plus all ports and fuses, and provides exact-ID
lookup through `TryGetPort` and `TryGetFuse`. A fixture adapter therefore does not need a hard-coded
Unity hierarchy path to reach the locomotive simulation.

The `DV.Simulation.Ports` types form the Unity-to-LocoSim presentation boundary:

```text
InteractablePortFeeder              player/control input -> simulation port
TractionPortsFeeder                 wheel/traction state -> simulation
WaterDetectorPortFeeder             environment state -> simulation
OilingPointPortFeederReader         bidirectional interaction/projection
AnimatorPortReader                  port -> animation
RotatorPortReader                   port -> transform rotation
LampPortReader / IndicatorPortReader port -> cab presentation
AudioClipPortReader / LayeredAudioPortReader port -> audio presentation
GenericPortReadersController        groups presentation readers
```

This distinction matters for replication: synchronizing the authoritative port value should drive
every correctly configured reader on the client. A moving visual that is not backed by a registered
simulation port is outside the current generic train-port protocol.

`InteriorControlsManager` supplies the higher-fidelity control surface. It discovers actual cab
controls by matching `InteractablePortFeeder.portId` against `BaseControlsOverrider`, exposes them by
`ControlType`, and can operate scrollable controls through `MoveScrollable`. Confirmed relevant
types include:

```text
Throttle
Reverser
TrainBrake
IndBrake
Handbrake
StarterControl
StarterFuse
ElectricsFuse
TractionMotorFuse
FuelCutoff
TrainBrakeCutout
```

This gives the harness two deliberately different levels of operation:

1. a fidelity path that moves the real cab control and exercises the normal DV event chain;
2. an arrangement/safety path that resolves and supervises the underlying port or fuse on the host.

The result of each action must say which path was used.

### Existing startup arrangement helper

`DV.Simulation.Controllers.StartupHelper.Startup(TrainCar)` is the correct baseline fixture
bootstrap. It operates through existing DV control/simulation APIs rather than fabricating an
already-running engine state.

This is not merely a convenient helper inferred from its implementation. Both the game's
locomotive-start comms-radio mode (`DV.CommsRadioStartup.OnUse`) and
`Dev.LocoStartEngine` call this same method. The production interaction and developer workflow
therefore agree on the startup boundary.

For every supported locomotive it:

```text
sets brake cutout to 1
sets handbrake to 0
sets independent brake to 1
fills a compressor-equipped main reservoir to 9 bar
```

For a diesel it additionally:

```text
closes every fuse once
sets the real Starter control to 1
waits up to approximately 6 seconds for EngineOnReader.IsOn
sets Starter back to 0
```

For a steam locomotive it instead fills and ignites the firebox, enables dynamo and air pump, and
issues boiler/oiling/lubricator special requests.

Fixture implications:

- call `StartupHelper.Startup` on the host after simulation and controls are initialized;
- wait on the real `EngineOnReader`, not a fixed delay;
- record startup timeout as an actionable failure;
- do not duplicate its fuse/control setup in the harness;
- treat it as initial arrangement only: it does not set throttle/reverser, maintain fuel, prevent
  later fuse trips, or prevent low-RPM stalls;
- verify the semantic meaning of the brake values on each locomotive before assuming the train is
  free to move.

## Confirmed diesel engine models and stall behavior

Derail Valley does not have one universal diesel engine component. The fetched build contains at
least three implementations with materially different stall rules.

### `DieselEngineDirect`

This is a torque/inertia model. It owns the engine RPM as a simulation port and integrates generated
torque, viscous drag, and the external `loadTorque` into that RPM.

Relevant behavior:

- starter input is passed through `engineStarterFuseRef`;
- starter torque can spin the engine above its start threshold;
- `engineRpmMin` is `40%` of idle RPM;
- the engine remains on only while RPM is above that threshold, fuel exists, intake water remains
  below the hydrolock threshold, and emergency/collision shutdown inputs are clear;
- load torque can physically pull RPM below the threshold and stop the engine;
- empty fuel, hydrolock, a broken engine, emergency stop, and collision stop are separate shutdown
  paths;
- low health can cause misfiring when drivetrain failures are enabled.

This is a genuine mechanical stall. Merely keeping a fuse closed cannot prevent it.

### `DieselEngineDirectDrive`

This implementation couples engine RPM to absolute drive-shaft RPM whenever the transmission is
engaged. In neutral, RPM instead approaches an idle/throttle target.

Its explicit stall rule is:

```text
transmission engaged
engine RPM < 60% of idle RPM
condition persists for more than 0.75 seconds
=> engine off
```

At very low vehicle speed, selecting a direction and applying power can therefore drag the engine
below its sustainable RPM. The component separately handles reverse-direction loading, throttle
stress, retarder load, overspeed heating, fuel/oil use, health, emergency stop, collision stop, and
broken state.

This is the strongest explanation found so far for the user-visible "start it and immediately max
the controls" stall, but the locomotive livery using this component still needs to be identified at
runtime.

The decompiled overheating-timer branch also appears suspicious: it resets the timer when
`DrivetrainFailuresAllowed` is true and advances toward shutdown only when it is false. That is the
opposite of the surrounding damage policy. Treat this as a source anomaly to reproduce at runtime,
not as a fixture contract or a confirmed intended behavior.

### `DieselEnginePowerSource`

This is a power-source/rotor-load model. Fuel injection raises RPM, engine drag continuously removes
RPM, and `loadOnRotorReader` changes the RPM gain available from fuel.

Its explicit low-RPM rule is:

```text
engine RPM < 50% of idle RPM
=> engine off
```

It also stops for internal, emergency, or collision shutdown requests; empty fuel; timed
overheating; and probabilistic severe-health failure. The last two health-related behaviors depend
on drivetrain-failure policy, but low-RPM stall and explicit shutdown inputs do not.

### `DirectDriveMechanism`

`DirectDriveMechanism` is not the engine state owner. It converts power and engine braking into
output torque using engine RPM, throttle, reverser, and neutral state. It is important to propulsion
but it is not the place where a stalled/running transition is decided.

### Consequence for fixture safety

Setting `SimGameParams.DrivetrainFailuresAllowed` to false is insufficient as an anti-stall policy.
It can suppress configured damage/random-failure behavior, but it does not remove:

- direct mechanical RPM collapse;
- the direct-drive 60%-idle timer;
- the power-source 50%-idle cutoff;
- fuel starvation;
- emergency, collision, or internal shutdown inputs.

The fixture implementation should resolve the concrete diesel component and report:

```text
engine model
engine-on readout
RPM and idle/max thresholds
starter fuse/input
fuel and oil availability
shutdown inputs
load or drive-shaft state
last inferred shutdown cause
```

The default test profile should fail on an unexplained stall. A separate `SustainedMotion` profile
may prevent known fixture hazards and restart after a logged intervention, but must never conceal an
engine failure in a scenario whose purpose is to test locomotive behavior.

## Locomotive synchronization findings from fixture research

Train-fixture research doubles as a replication audit. Findings belong here even when they do not
block fixture creation.

Confirmed current behavior:

- clients do not tick `SimComponent`; host port/fuse replication is therefore the only live client
  projection of engine, electrical, pneumatic, and resource state;
- the host subscribes to all simulation port changes, while clients originate only control-port
  changes;
- `Server_DirtyAllState` queues every port and fuse, providing the basis for an initial/full state;
- ordinary state ports are delta-filtered at `0.001`, while controls are dirtied on every accepted
  control update;
- fuse replication contains only stable fuse ID and boolean state;
- applying a packet chooses `ExternalValueUpdate` for `EXTERNAL_IN` ports and direct `Value` update
  for other ports.

Audit questions to answer with train fixtures:

1. Does a late-joining or newly interested client receive every engine port and fuse before its cab
   UI and effects become visible?
2. Are port packets applied in a safe order relative to car creation, interior loading, control
   authority, and fuse packets?
3. Does delta-filtered RPM/temperature/resource state appear smooth enough, or should selected
   presentation ports use interpolation rather than more bandwidth?
4. Can engine-off, stall, starter, and fuse transitions be lost when several changes occur inside
   one network tick?
5. Does a client temporarily project locally generated control state before the host validates and
   echoes it?
6. Are simulation ports that affect train-relative item behavior available before moving-train item
   scenarios begin?

Every train scenario should capture the host component state and corresponding client port/fuse
projection under one correlation ID. That will turn any useful sync discovery made during this
expedition into a reproducible regression instead of an anecdote.

### Modded locomotive capability audit

A modded locomotive with unsynchronized opening windows is evidence of a capability gap, not proof
that every window needs a bespoke packet. Vanilla door/window controls are expected to be ordinary
control ports and are explicitly allowed by the server's proximity validation even when the player
is not registered as being inside the car.

For each unsynchronized modded control, inspect this chain:

```text
physical ControlImplBase
  -> InteractablePortFeeder.portId
  -> SimulationFlow port exists
  -> port valueType == CONTROL
  -> NetworkedTrainCar subscribed to the port
  -> client request accepted by host proximity/authority validation
  -> host relayed canonical value
  -> client-side Animator/Rotator/other PortReader consumes that same port
```

Likely failure classes include:

- the window is a local `ControlImplBase` animation with no simulation port;
- the feeder references a port absent from the modded `SimConnectionDefinition`;
- the port is classified as state rather than control, so a client cannot author it;
- the mod constructs or replaces its simulation flow after `NetworkedTrainCar` subscribed;
- its custom control is absent from `InteriorControlsManager`, preventing authority/grab lifecycle
  hooks even though the raw port may exist;
- host and client have incompatible prefab/connection definitions.

The runtime train catalogue should publish these as explicit capabilities and warnings. Supporting
modded trains should be data-driven where possible; unknown custom components should be reported,
not silently treated as a standard diesel.

CustomCarLoader compatibility is valuable but is not part of the first train-fixture milestone.
The first milestone should catalogue CCL liveries and report their capabilities, then test vanilla
doors/windows. Full CCL control support comes afterward, using a concrete unsupported-control report
instead of speculative special cases.

### Presentation interpolation policy

Do not interpolate every simulation port. Port values include continuous measurements, notched
controls, binary switches, momentary pulses, fuse-like state, audio triggers, and values used by
client presentation logic. Treating all of them as continuous would delay switches, replay pulses,
and create incorrect intermediate states.

Keep the authoritative replicated port value exact. If smoothing is needed, apply it only to a
presentation target, selected using explicit semantics:

```text
continuous AnimatorPortReader / RotatorPortReader target -> eligible
continuous gauge/indicator presentation                  -> eligible
door/window transform presentation                       -> eligible after testing
lamp, audio, fuse, boolean switch, momentary input        -> never interpolate
notched throttle/reverser/brake control                   -> preserve exact notches
simulation inputs used by host authority                  -> never client-interpolate
```

`PortValueType` alone is not sufficient to decide this. The catalogue should combine port type,
reader type, control specification, and an explicit per-port capability/profile. Any smoothing must
also avoid feeding intermediate presentation values back into the client-to-host control path.

## Runtime livery and engine discovery

There does not need to be a handwritten livery-to-engine manifest. Derail Valley already exposes the
registered livery catalogue through `Globals.G.Types.Liveries`, which includes mod-added liveries.

The current runtime catalogue resolves the vanilla diesel question decisively:

| Livery | Prefab | Engine | Drivetrain |
| --- | --- | --- | --- |
| `LocoDE2` | `LocoDE2` | `DieselEngineDirectDefinition` | traction generator + traction-motor set |
| `LocoDE6` | `LocoDE6` | `DieselEngineDirectDefinition` | traction generator + traction-motor set |
| `LocoDH4` | `LocoDH4` | `DieselEngineDirectDefinition` | hydraulic transmission |
| `LocoDM3` | `LocoDM3` | `DieselEngineDirectDefinition` | hydraulic/manual transmission chain |
| `LocoDM1U` | `LocoDM1U` | `DieselEngineDirectDefinition` | hydraulic/manual transmission chain |

No currently registered livery in the captured catalogue uses
`DieselEngineDirectDriveDefinition` or `DieselEnginePowerSourceDefinition`. Those implementations
remain important compatibility knowledge for future game content and modded liveries, but the
initial vanilla fixture implementation can target the confirmed `DieselEngineDirect` behavior.

Related exceptions are also explicit in the catalogue:

- `LocoDE6Slug` has no engine and receives external slug power while owning a traction-motor set;
- `LocoMicroshunter` is battery-electric and contains `BatteryDefinition`, a voltage regulator,
  and a traction-motor set;
- steam locomotives use boiler/firebox/reciprocating-engine components;
- ordinary freight cars generally have no `SimController`;
- the caboose does have a simulation flow, including many external controls and independent fuses,
  despite not being a locomotive.
Each `TrainCarLivery` supplies its prefab, and that prefab's serialized `SimController` points to its
`SimConnectionDefinition`.

The catalogue can inspect, without spawning:

```text
TrainCarLivery.id
TrainCarLivery.prefab
prefab SimController
SimController.connectionsDefinition
connectionsDefinition.executionOrder[].GetType()
connectionsDefinition connections and port-reference connections
controlsOverrider / portsOverrider presence
port feeder and reader component inventory
```

The concrete definition types in `executionOrder` reveal whether the prefab uses
`DieselEngineDirectDefinition`, `DieselEngineDirectDriveDefinition`,
`DieselEnginePowerSourceDefinition`, or an unknown/modded definition. After spawning, the catalogue
should confirm the result against `SimulationFlow.OrderedSimComps`; the live flow is authoritative
if a mod changes the prefab during initialization.

This catalogue should return capability information rather than just an engine label:

```text
canSpawn
hasSimulationFlow
engineModel
canStartThroughKnownControl
canSetThrottleAndReverser
requiredFuseIds
networkedControlPorts
unmappedInteractiveControls
unknownSimulationComponents
```

That gives fixtures a clean rejection such as `unsupported-engine-model` or
`unmapped-interactive-control` instead of timing out halfway through a scenario.

## Proposed debug-only fixture boundary

The proposed API is host-only and excluded from release builds:

```text
TrainFixtureRequest
  fixtureId
  trackAnchorId
  direction
  cars[]
    liveryId
    role: Locomotive | Wagon | Caboose | AccessoryTestCar
  initialControls
    engineRunning
    reverser
    throttle
    independentBrake
    trainBrake
    handbrakesReleased
  safetyProfile
    preventDerailment
    protectFuses
    sustainEngine
    preventDamage
    protectedRouteId
  playerPlacements
    hostAnchor
    clientAnchor
```

The result should return the fixture ID, ordered train NetIds, ordered car IDs/GUIDs, resulting
`Trainset` identity, resolved track identity, and every safety intervention.

### Fixture registry

Every spawned car must be registered independently of Unity components:

```text
fixture ID -> ordered car NetIds
fixture ID -> car IDs and GUIDs
fixture ID -> track/route lease
fixture ID -> original junction and signal state
fixture ID -> safety policy
fixture ID -> spawned item/gadget descendants
```

As with item fixtures, cleanup must not depend exclusively on a marker component surviving until
the final phase.

## Proposed safety policy

Safety is scoped to fixture-owned cars. It must never change ordinary player trains globally.

### Fuse protection

Desired invariant:

> A protected fixture locomotive cannot lose required propulsion or control power because a fuse
> trips.

`Fuse` itself is intentionally minimal: it stores an ID, boolean state, off value, and
`StateUpdated` event. `ChangeState(bool)` does not record whether a transition was manual,
automatic, or network-applied. `ProcessInput` simply returns the configured off value while open.

Known direct callers of `Fuse.ChangeState` are:

```text
TrainCarCustomizerBase.PopFuse
StartupHelper.Startup
InteractableFuseFeeder.OnControlChange
SlugModule.OnPowerFuseChange
FuseReference.ChangeState
IndependentFuses.SetSaveStateData
```

This already confirms that an unconditional anti-trip patch would be wrong: customization,
startup, manual interaction, slug behavior, reference forwarding, and save restoration all share
the same setter. `StartupHelper.Startup(TrainCar)` is also a promising existing arrangement entry
point for fixtures and should be inspected before implementing a custom startup sequence.

`InteractableFuseFeeder` confirms the manual path. It mirrors fuse changes into a
`ControlImplBase`, and user control changes call `Fuse.ChangeState(newValue > 0.5)`. It contains no
automatic trip logic.

Known callers of `FuseReference.ChangeState` identify the likely automatic trip layer:

```text
Battery.Tick
FuseController.Tick
TractionGenerator.SimulateDamage
TractionMotor.Tick
TractionMotorSet.SimulateDamage
```

`FuseController.Tick` is now confirmed as a different category from the damage paths. It computes:

```text
overThreshold = controllingPort.Value > setThreshold
desiredFuseState = isActiveWhenOverThreshold ? overThreshold : !overThreshold
if desiredFuseState != currentFuseState:
    fuseRef.ChangeState(desiredFuseState)
```

It is therefore a continuous port-to-fuse state controller, not a one-shot fault or overload trip.
Forcing its fuse closed would be unstable: the controller would reopen it on the next simulation
tick whenever the controlling port still requests the inactive state. A fixture must arrange or
override the source port, or explicitly suppress this controller only for a fixture-owned car.

These components, rather than `Fuse`, should be inspected to distinguish depleted battery,
electrical overload, generator damage, and traction-motor damage. A fixture safety policy can then
suppress a specific failure source or fail with its actual cause instead of reopening an anonymous
fuse after the fact.

The inspected callers provide the following concrete trip causes:

- `Battery.Tick` opens its power fuse when charge is empty or the requested load causes the battery
  voltage equation's discriminant to become non-positive. This path is not guarded by
  `DrivetrainFailuresAllowed`; a fixture must maintain charge and avoid voltage collapse.
- `TractionGenerator.SimulateDamage` opens its fuse on generator overcurrent when drivetrain
  failures are allowed.
- `TractionMotor.Tick` opens an individual motor fuse after its overheat failure timer expires.
- `TractionMotorSet.SimulateDamage` can open the set fuse for an invalid negative-resistance
  circuit, timed overheating, or powered water exposure when drivetrain failures are allowed.
- `SlugTractionMotor` contains no fuse-trip behavior of its own; it consumes externally supplied
  torque/current and smooths traction-motor RPM.

The generator and motor implementations also pulse dedicated readout ports when they trip, such as
overcurrent and overheat fuse-off readouts. Fixture telemetry should capture those ports. They are
the best available causal signal and are substantially more useful than observing only that a fuse
became open.

Consequences:

- the fuse object cannot explain why it tripped;
- a protection patch at `Fuse.ChangeState` would need external fixture-car context;
- restoring the fuse after `StateUpdated` is observably later and may allow one bad simulation tick;
- protecting every fuse indiscriminately could hide an invalid electrical setup.

Still required from dnSpy/runtime inspection:

- the concrete fuse IDs required by each test locomotive;
- whether circuit breakers exist outside this class;
- whether a pre-transition guard can associate a `Fuse` instance with its fixture-owned
  `SimulationFlow` without a global scan.

`DrivetrainFailuresAllowed` appears to be a shared game parameter, not a fixture-local switch. The
harness must not disable it globally for ordinary trains. Prefer fixture-scoped prevention at the
specific failure source, with resource maintenance for the battery path that ignores this flag.

### Derail protection

Desired invariant:

> A protected fixture car remains on its expected track and bogie path throughout the scenario.

The current multiplayer code observes `Bogie.HasDerailed`, calls `Bogie.Derail` while applying
remote state, and uses `TrainCar.Derail` for complete cars. The original derail decision and stress
threshold have not yet been identified. The earliest decision point should be guarded, rather than
derailing and repeatedly rerailing the car.

Unexpected track departure must stop and fail the fixture. It must not silently teleport the train
back onto the railway while an item-physics assertion is running.

### Engine sustain

Desired invariant:

> Once intentionally started, a protected diesel continues running while the requested direction,
> released brakes, and throttle demand remain valid.

This policy may maintain fuel/resources and suppress fixture-specific shutdown causes. It must not
force the train's velocity or transform each frame. Real traction, braking, gradients, coupler
forces, and train-relative physics must continue to operate.

The low-RPM stall rules are now confirmed above. What remains is mapping each locomotive livery to
its concrete engine component and identifying the exact ports/definitions used by that prefab.
Fixture telemetry should distinguish a physical RPM stall from explicit shutdown, fuel starvation,
damage, or fuse loss before applying any sustain intervention.

### Confirmed sign placement and curve-derived speed model

`DV.Signs.SignPlacer` does not begin with arbitrary world coordinates. `GetTrackSigns` operates on
an actual `RailTrack` (the component beside the placer, or every eligible runtime track), then uses
the track's `BezierCurve`. Each private `SignData` retains:

- the placement curve;
- normalized curve parameter `placementT`;
- travel-direction flip;
- and, through `placementCurve.GetComponent<RailTrack>()`, the owning track.

Position and rotation are derived with `BezierCurve.GetPointAt(t)` and `GetTangentAt(t)`. The final
pole is shifted sideways, parented beneath the origin-shift container, and contains generated sign
meshes plus `SignDebug` text. The generator destroys itself at startup, and the generated sign does
not retain the original `SignData`, curve, `t`, or track reference. Consequently the fixture system
should query the track graph directly rather than attempting to reconstruct authority from visible
sign GameObjects.

The placer already contains a useful deterministic speed-profile algorithm. It approximates a
track curve into arcs, groups them into minimum-length segments, records minimum curvature radius,
derives a stepped speed value from that radius, smooths small adjacent differences, accounts for
the next track after a junction, and emits advance speed/junction/track-end signs. It also derives
grade from the vertical displacement between the segment's curve endpoints. This is suitable as a
reference for a debug automatic-speed controller and route preview, although the implementation
should own a public route-profile model rather than reflect private nested `SignPlacer` types.

`SignType.TrackID` proves only that a Track ID sign prefab/display type exists. The inspected
`Sign`, `SignGenerator`, and `SignPlacer` paths do not populate it. `RailTrack` additionally declares
`TRACK_ID_GO_NAME = "[track id]"`, which strongly indicates that authored ID signs are baked child
objects rather than products of the runtime sign generator.

The actual identity model is already established elsewhere. DV exposes the durable logical string
as `railTrack.LogicTrack().ID.RailTrackGameObjectID`, and `RailTrackRegistry.Instance.GetTrackWithName`
resolves that string back to the runtime track. Existing job serialization uses this representation.
DVMP's `NetworkedRailTrack` adds a session-local `ushort NetId`, bidirectional runtime lookup, and
the compact identity used by spawn, bogie, rerail, and move packets. Scenario definitions and named
anchors should therefore persist the DV logical string; the authoritative host resolves it and
uses the current `NetworkedRailTrack.NetId` only on the wire.

`RailTrackRegistryBase.RailTracks` and `.Junctions` are the authoritative runtime catalogues used by
`RailTrack` itself for closest-track and graph operations. A catalogue command can enumerate these
without searching arbitrary scene objects. The remaining runtime validation is to prove logical
strings and session NetIds agree on every process and to detect duplicate/missing registrations.

`RailTrackRegistryBase.GetTrackWithName` performs an exact lookup against `RailTrack.name`; it is
declared on the base class, which is why searching only the derived `RailTrackRegistry` did not show
it. `RailTrackRegistry` builds bidirectional `Track <-> RailTrack` dictionaries. Code that cannot
see the external `LogicTrack()` extension can use `RailTrackRegistry.RailTrackToLogicTrack` directly.

Authored IDs are parsed from the track GameObject name by `TryGenerateTrackIdFromName`. Matching
names create a structured `DV.Logic.Job.TrackID` containing yard, sub-yard, order, and track type;
its `RailTrackGameObjectID` recreates the exact authored GameObject name, while `FullDisplayID`
produces the human-readable yard sign value. Names which do not match receive a generic ID from
`IdGenerator`. Generic IDs may depend on discovery order and are not accepted as durable fixture
anchors without an additional path/geometry fingerprint.

The registry exposes two valuable compatibility fingerprints: `TracksHash` and `JunctionsHash`.
They hash the ordered hierarchy paths beneath `[railway]`. Before running a multi-process train
fixture, the dashboard should require matching host/client hashes. A catalogue row should include
logical/full display ID, exact GameObject name, generic flag, session NetId, hierarchy path, span,
point count, absolute endpoints, connected tracks/junctions, and a geometry signature. Named tracks
use the exact GameObject name as their primary durable key; generic tracks use path plus geometry
and fail closed if validation is ambiguous.

Matching DV hashes do not alone prove DVMP assigned the same session NetId to each track. The
runtime catalogue therefore also computes a sorted logical-key-to-NetId mapping hash and compares
it across host and clients. A mismatch blocks fixture execution and reports the first divergent
track instead of allowing a packet to resolve to the wrong railway segment.

### Route protection

A protected route should snapshot and restore all shared railway state it mutates. It may need to:

- reserve a known test segment or loop;
- align every junction on that route;
- keep relevant signals permissive;
- prevent generated traffic from occupying the route;
- verify the fixture train remains on the leased track sequence;
- restore junction, signal, and traffic state during cleanup.

Junction synchronization already exists through `NetworkedJunction`. Signal classes, dispatcher
ownership, occupancy, and route APIs remain research targets.

## Confirmed trainset teleport behavior

`TrainCarTeleporter.TeleportTrainset` is the game-owned placement implementation used by fast travel
and developer workflows. It accepts an ordered list of existing cars, a local-world target, and an
optional request to force every car into the regular direction.

Its full sequence is significant:

1. rejects concurrent teleport, missing tracks, empty input, null cars, and derailed cars;
2. records each car's orientation within the current consist unless regular direction is forced;
3. uncouples every car and disconnects supported multiple-unit cables;
4. waits for a fixed update;
5. searches nearby `RailTrack` candidates, beginning at `0.1 m` and expanding by `5 m` up to
   `2000 m`;
6. uses `CarSpawner.FindValidPointInOneDirectionForCarStartingFromIndex` plus temporary
   `Train_Big_Collider` boxes matching every car's bounds to find a contiguous, non-overlapping
   placement for the entire consist;
7. calls `TrainCar.MoveToTrack` for each car;
8. calls `TrainCarInteriorPhysics.SyncPosition()` for every car that has interior physics;
9. calls `Physics.SyncTransforms()`;
10. fires the global `TeleportSuccessfulEventBeforeCoupling` event;
11. recouples the ordered cars and reconnects supported multiple-unit cables;
12. clears the global `isTeleportingTrain` guard.

This gives the fixture implementation a reliable game-owned geometry/placement mechanism, but its
contract is weaker than a test API:

- it returns only an `IEnumerator`, not a success result;
- its success event fires before recoupling and is global rather than operation-correlated;
- failure is reported through logs;
- the expanding search may place the train on a nearby track other than the requested one;
- there is no `try/finally` around the global teleport guard;
- it deliberately destroys and reconstructs the consist's coupling/MU topology.

The harness should wrap it with a fixture operation ID, pre/post snapshots, timeout, final
bogie-track assertions, coupling/MU assertions, and a guaranteed cleanup/failure path. A fixed-track
fixture may still prefer its own placement search constrained to the leased track.

### Interior-physics implication

The explicit `TrainCarInteriorPhysics.SyncPosition()` call immediately after `MoveToTrack` is
important to both fixture placement and multiplayer item physics. It confirms that moving a train
car and its bogies does not automatically make the interior physics reference frame coherent.

Any DVMP train teleport, correction, fixture placement, or hard reconciliation that can move a car
discontinuously must audit whether it also synchronizes `TrainCarInteriorPhysics`. Otherwise loose
items in the cab may be simulated against the old interior frame, producing exactly the observed
ejection, jitter, and train-relative disagreement. This is a sync lead, not proof that ordinary
continuous train movement should call it every frame.

## Quick Tutorial factory as a fixture-stress blueprint

`QuickTutorialFactory` is not an automated-test framework, but it encodes the game's own definition
of a valid locomotive workflow. Its patterns are directly useful when designing scenarios.

### Locomotive readiness and driving

The diesel tutorial checks or maintains:

- player is in the locomotive;
- car is enabled, on rails, nearly stationary, and on an acceptable grade;
- damage is within limits;
- fuel/oil/electric charge remain available;
- the train remains close enough and in the expected LOD;
- controls are discovered through `InteriorControlsManager`;
- tutorial conditions can observe `BaseControlsOverrider` values where available;
- engine state, fuses, ports, resources, brake pressure, RPM, amperage, temperatures, and speed are
  checked through dedicated steps/conditions.

Its representative diesel movement sequence is a good first fixture stress test:

```text
reset locomotive
establish diesel prerequisites
neutral reverser; zero throttle and dynamic brake
start engine and build reservoir pressure
apply and verify braking
select forward; release handbrake and active brake
select required gearbox controls where present
apply modest throttle
wait for speed above 1 m/s
return throttle to zero
apply train brake
wait for speed below 1 m/s
return reverser to neutral
```

The fixture harness should implement this as explicit host-authoritative actions and assertions,
not by launching a tutorial that expects UI prompts and human acknowledgement.

The inspected step classes make this separation explicit:

- `LocoControlOverrideStep` does **not** change a control. It reads an
  `OverridableBaseControl.Value` and completes when the value enters its configured range. The
  special `float.MinValue` sentinel is treated as unsupported/already complete. For positive target
  ranges, its optional timeout can also allow completion after the control has first become active.
  This is useful assertion logic, not an automation entry point.
- `LocoResetStep` is an actual one-shot mutation. It calls
  `BaseControlsOverrider.SetNeutralState()`, then uses `ControlImplBase.SetValue(..., Default)` to
  set electrics, starter, and traction-motor fuse controls to zero, along with cab light, starter,
  and both gearbox controls when present. It then drops its overrider reference so it runs only
  once.

Consequently, `LocoResetStep` means “put the locomotive into the tutorial's cold/neutral starting
state,” not “make the locomotive ready to drive.” It is useful for cold-start and startup-sequence
tests, but it is the opposite of `StartupHelper.Startup` for a moving-train fixture.

### Coupling stress

`CouplingTutorial` supplies a realistic coupling checklist. It selects separate-car couplers within
`0.6 m`, keeps both cars/couplers at full LOD, validates both cars remain enabled and railed, then
exercises:

```text
chain placement
coupler tightening
brake-hose connection
both angle cocks opened
optional multiple-unit cable connection between locomotives
```

Those stages are ideal future multiplayer assertions because they cover car proximity, coupler
state, hoses, valves, MU cables, LOD transitions, simulation ports, and replication together.

### Rerailing, spawning, and deletion

The tutorial factory exposes additional constrained services:

- `SingleRailRerailService` limits rerailing to a supplied or closest track;
- `InsideColliderRerailService` can limit the destination region;
- `SingleRailCrewSpawnService` limits crew-vehicle spawning to a track;
- `SingleCarDeletionService` constrains the clear-car tutorial to one selected car;
- `KeepTrainLODService` and `KeepCouplersLODService` prevent important train/coupler objects from
  optimizing away during an interaction.

These service boundaries are worth inspecting for stable APIs, particularly fixture-scoped car
deletion and deterministic spawning. The tutorial objects themselves should not become required
fixture infrastructure.

Inspection shows that these three services do not execute their named operation. They temporarily
constrain an already-active comms-radio mode:

```text
SingleRailCrewSpawnService -> CommsRadioCrewVehicle.SingleAllowedTrack
SingleRailRerailService    -> RerailController.SingleAllowedTrack
SingleCarDeletionService   -> CommsRadioCarDeleter.SingleAllowedCar
```

`StartService` assigns the permitted target and `StopService` clears it; `UpdateService` is empty.
This is an excellent isolation mechanism for high-fidelity radio interaction tests, but not the
fixture arrangement API. Direct setup/cleanup still needs the underlying confirm/spawn/rerail/delete
methods used by those radio modes.

## Confirmed radio operation boundaries

The underlying comms-radio implementations now expose the concrete game actions beneath those
tutorial constraints.

### Crew-vehicle spawn

`CommsRadioCrewVehicle` builds its selectable catalogue from `CarSpawner.vehiclesWithoutGarage`
plus liveries from unlocked crew-vehicle garages that have a matching `GarageCarSpawner`. It is a
work/crew-vehicle flow, not a general all-liveries train spawner.

The confirm path:

1. validates funds and rejects a selected garage car when it contains cargo;
2. removes the summon price from the local inventory;
3. chooses the destination point's forward or reverse direction;
4. calls
   `CarSpawner.SpawnCrewVehicle(livery, destinationTrack, point.position, forward, garageSpawner)`;
5. raises `CarSummoned(spawnedCar)`;
6. clears the radio selection state.

Destination selection uses the selected livery prefab's `TrainCar.Bounds`, track equidistant
points, `CarSpawner.FindClosestValidPointForCarStartingFromIndex`, and
`CarSpawner.IsBoxOverlapping`. A `SingleAllowedTrack` bypasses the normal nearby-track catalogue but
does not bypass livery availability, cargo, placement, or money rules.

Harness conclusions:

- `SpawnCrewVehicle` is a valid high-fidelity entry point for crew-vehicle scenarios;
- it may return/reposition a garage-backed car rather than always create a disposable new car;
- fixture metadata must record whether a returned car came from a garage and restore it correctly;
- the general locomotive/carriage fixture should continue using the host's broader authoritative
  car-spawn pipeline rather than pretending every livery is a crew vehicle;
- a spawn scenario should wait for the returned `TrainCar`, its network identity, simulation
  initialization, and every client projection rather than treating `CarSummoned` as full readiness.

### Rerail

`RerailController` only selects cars that are derailed, effectively stationary, and report
`IsRerailAllowed`. It searches tracks within expanding radii, or uses `SingleAllowedTrack`, then
finds a car-sized valid point and rejects overlap, the player's current car, invalid rerail state,
or insufficient funds.

The actual mutation is:

```text
localWorldPosition = rerailPointWorldAbsPosition + WorldMover.currentMove
car.Rerail(rerailTrack, localWorldPosition, rerailPointWorldForward)
```

The inspected ordering appears to contain a callback defect: `ClearFlags()` sets `carToRerail` to
null before `CarRerailed(carToRerail)` is invoked. Consumers should therefore expect the event
argument to be null in this build and verify the originally captured car directly by its fixture ID,
bogie tracks, and `derailed` state.

For fixture safety, ordinary unexpected derailment should still fail the scenario. This direct
rerail boundary is appropriate for explicit rerailing tests and cleanup recovery, not for silently
masking a derail during item-physics assertions.

### Car deletion

`CommsRadioCarDeleter` rejects the player's current car and cars marked `preventDelete`. Depending
on game difficulty it may reject derailed cars. Its confirmation path also charges money and
expires an available job or abandons an in-progress job attached to the selected car.

The physical retirement path branches:

```text
if HomeGarageReference has a garage spawner:
    garageSpawner.ReturnCarHome(car)
else:
    CarSpawner.DeleteCar(car)
    UnusedTrainCarDeleter.ClearInvalidCarReferencesAfterManualDelete()
    if the car still exists:
        deactivate car GameObject and interior GameObject
```

The fixture harness must bypass the economic/job UI layer while preserving the relevant retirement
branch. Fixture cars should never have jobs, must never be `PlayerManager.Car` during cleanup, and
must record whether they are garage-backed. Cleanup completion requires observing host registry,
trainset, logic-car, network projection, and client destruction—not merely receiving `CarDeleted` or
seeing the GameObject become inactive.

## Confirmed neutral and spawn-brake behavior

`BaseControlsOverrider.SetNeutralState` is a strong but destructive arrangement operation. It first
applies every livery-configured `neutralStateSetter` directly through
`Port.ExternalValueUpdate(value)`, then applies the following controls when present:

```text
PowerOff          = 1
Starter           = 0
Throttle          = 0
Train brake       = 0
Brake cutout      = 0
Independent brake = 0
Dynamic brake     = 0
Handbrake         = 1
Reverser          = 0.5
Sander            = 0
```

Depending on `propagateNeutralStateToFront` and `propagateNeutralStateToRear`, it recursively calls
`SetNeutralState` on coupled cars in those directions. A reentrancy flag prevents immediate cycles.
The harness must snapshot the entire affected consist and must not call this when it intends to
preserve a running engine or neighboring player train.

`BaseControlsOverrider.Init` subscribes this method to `TrainCar.OnRerailed`. A successful rerail can
therefore neutralize the rerolled car—and, depending on the propagation flags, coupled neighbors—as
part of the normal game event chain. Rerail scenarios must assert the resulting control/engine/brake
state and restart deliberately if later phases expect movement.

`SetBrakesOnSpawn` is a separate safety operation:

- for a coupled brakeset, it respects an already strongly applied handbrake; otherwise it forces
  `4.5 bar` cylinder pressure and `6 bar` control-reservoir pressure across the brakeset, marks
  related overriders to skip duplicate load setup, and sets the train brake to `1`;
- for an uncoupled car with handbrake at least `0.75`, it clears cylinder pressure;
- otherwise it forces `4.5 bar` cylinder pressure and sets independent brake to `1`.

This clarifies fixture sequencing: spawn and stabilize first, apply the game's spawn-brake setup,
then call `StartupHelper.Startup`, and finally release whichever brake the concrete locomotive
supports only when the movement phase begins.

## Existing DVMP authority routing

DVMP already patches all three multiplayer radio confirmations so clients do not directly perform
the vanilla mutation:

```text
CommsRadioCrewVehicle ConfirmSummon
    -> ServerboundWorkTrainRequestPacket
    -> host validates/resolves
    -> host SpawnCrewVehicle

RerailController ConfirmRerail
    -> ServerboundTrainRerailRequestPacket
    -> host TrainCar.Rerail
    -> TrainCar.Rerail patch broadcasts ClientboundRerailTrainPacket

CommsRadioCarDeleter ConfirmDelete
    -> ServerboundTrainDeleteRequestPacket
    -> host ReturnCarHome or CarSpawner.DeleteCar
    -> PrepareTrainCarForDeleting patch broadcasts ClientboundDestroyTrainCarPacket
```

The host's loopback/single-player case is allowed to run the vanilla path. Remote confirmations are
suppressed locally and routed through the existing client/server connection. Client rerail apply
adds the receiving process's `WorldMover.currentMove`; the host broadcast stores the absolute
position by subtracting its own current move. Packet-processing guards prevent the applied client
rerail from being emitted again.

### Work-train request behavior

The server resolves the player, network track, livery, kinked-point index, available track space,
player-to-point distance, direction, garage source, existing car, stationary/teleporting state,
recent visitation, active jobs, network identity, and player occupancy. A newly created car is sent
through `SendSpawnTrainset`; an existing work train is repositioned by the game path.

The current handler is not yet a transaction suitable for automated fixtures:

- a garage summon price is removed before index, space, range, and in-use checks complete;
- several rejection branches return without sending the RPC response, leaving the requesting
  client to time out;
- failure after charging does not visibly refund the charge;
- money removal uses the host runtime's `Inventory.Instance`; actor-specific attribution must be
  verified before assuming the requesting player's wallet was charged;
- the handler is constrained to work-train semantics and does not create an arbitrary consist.

The fixture API should have a separate debug-only server operation with no economy, an explicit
result for every branch, operation correlation, and ownership of every spawned car. It may reuse the
same livery/track/space validators and `SendSpawnTrainset` projection path.

### Rerail request behavior

The server currently resolves the actor, car NetId, and track NetId, computes/removes the rerail
price, and calls `TrainCar.Rerail`. The inspected server handler does not repeat several predicates
enforced by the radio UI/client path, including `IsRerailAllowed`, player occupancy/current-car
protection, destination overlap, actor range, and whether the supplied position and direction
actually belong to the requested track.

Client validation is not a server trust boundary. These checks should be made authoritative before
the same path is exposed to fixture commands. A fixture-only rerail operation can intentionally
bypass economy/difficulty, but must still validate fixture ownership, leased track, placement,
occupancy, and operation state.

The existing `RerailControllerPatch.PlayerSoundsLater` also clears the controller flags before its
delayed coroutine tries to use `carToRerail`, mirroring the vanilla null-event ordering problem.
Audio should capture the car/position before cleanup or avoid the stale controller reference.

### Delete request behavior

The server rejects unknown and occupied network cars, charges the calculated price, handles an
attached job, then returns a garage car or deletes the car and clears invalid references. The
inspected handler does not repeat every vanilla selection predicate, notably `preventDelete` and
the difficulty restriction for clearing derailed cars. Money attribution through the host
`Inventory.Instance` likewise needs verification.

Fixture deletion should not use this economic request verb. It should require a fixture ownership
token, evacuate players, detach fixture content, choose the correct garage/non-garage retirement
branch, and wait for the reliable destroy projection and registry cleanup.

### Existing-train teleport gap

DVMP's `ClientboundMoveTrainPacket` contains an `IsTeleporting` flag, but the current client handler
logs `teleport not implemented` and does nothing when that flag is true. The ordinary
`MoveToTrackWithCarUncouple` case is implemented and host-broadcast, but
`TrainCarTeleporter.TeleportTrainset` uses `MoveToTrack` plus its own uncouple/recouple and interior
physics sequence.

This means the native teleporter cannot simply be called on the host and assumed to reproduce a
complete existing consist move on clients. The fixture implementation needs a correlated
host-authoritative trainset-teleport operation containing ordered cars, orientations, destination
tracks/points, coupling and MU topology, plus client-side `MoveToTrack`,
`TrainCarInteriorPhysics.SyncPosition`, `Physics.SyncTransforms`, recoupling, and acknowledgement.
Alternatively, disposable fixtures can be destroyed and freshly projected at the destination, but
that would not test continuity of existing train content and identities.

## Player placement

The client does not need to spawn near the fixture. After train creation:

1. the host waits for its authoritative cars and consist;
2. the dashboard waits for the client to create the same NetIds;
3. each process resolves the same locomotive by NetId;
4. each local runtime agent teleports its own player to a named transform beneath that
   `TrainCar.interior`;
5. the test verifies `PlayerManager.Car` and character reparenting agree with the expected car.

The existing `player.teleport` command already uses Derail Valley's native teleport path, including
train-interior reparenting. Cab placement must resolve authored interior geometry; whole-car bounds
do not describe where a player can stand.

### Live DE2 cab-anchor findings

Runtime inspection of the same fixture projection on host and client confirmed:

- `TrainCar.interior` is a separately managed physics root and is not guaranteed to be a descendant
  of the exterior `TrainCar` GameObject. Interior component searches must start at
  `TrainCar.interior`.
- the active `[walkable]` interior object owns `DV.CharacterReparentTarget`;
- `CharacterReparentTarget.target` is exactly `LocoDE2(Clone) [interior]`;
- `TrainCar.Resolve` returns the same locomotive for the interior transform, `[walkable]` transform,
  and reparent target;
- both `PlayerTeleportNonVR` and `PlayerTeleportVR` sit behind `APlayerTeleport`, so
  `PlayerManager.TeleportPlayer` remains the correct shared boundary;
- the DE2 authored `Train_Walkable` floor is a `BoxCollider` named `area_cab` with a footprint of
  approximately `2.86 x 1.8857 m` and top-centre interior-local position
  `(0, 1.556, -0.414)`;
- teleporting both local players to `(0, 1.581, -0.414)` through `[walkable]` synchronously produced
  `PlayerManager.Car == L-046`, exact expected interior-local positions, and the same result on host
  and client.

The debug fixture therefore resolves a per-livery authored walkable surface, places the player at
its top-centre with a small clearance, and passes the `CharacterReparentTarget` transform into the
native teleport call. A per-livery local coordinate is retained only as a fallback. Unknown or
modded locomotives may use a scored cab/floor walkable-surface fallback, but the result must be
reported and the final player position validated against the selected footprint.

## Cleanup transaction

Proposed cleanup order:

```text
PREPARE
- stop new fixture actions
- release test control authorities
- stop propulsion and apply brakes
- identify every fixture car and descendant item/gadget

EVACUATE
- move host and client players to a verified world anchor
- verify no NetworkedPlayer remains parented to a fixture car

CLEAR CONTENT
- clean loose fixture items
- remove fixture-installed gadgets and snapped accessories
- restore or retire any deliberately persistent test records

DESTROY CONSIST
- uncouple only if CarSpawner deletion requires it
- call the host's real deletion path for every car
- allow existing destroy packets to retire client cars

RESTORE SHARED STATE
- restore junctions, signals, traffic, and safety patches
- release the route lease

VERIFY
- no fixture NetId, car ID, GUID, Trainset, player parent, route lease,
  item, gadget, or customization projection remains
```

Whether cars must be deleted front-to-back, back-to-front, or as a whole trainset remains to be
verified.

## Candidate world anchors already measured

These are origin-shift-corrected absolute positions collected through `player.position`:

| Anchor ID | Absolute position | Intended use |
|---|---:|---|
| `steel-mill-office` | `(7914.864, 131.722, 7348.921)` | Office loading and replenishment |
| `training-area` | `(8383.932, 129.853, 7912.168)` | General open-space fixtures |
| `food-factory-office` | `(9485.978, 119.319, 13610.322)` | Distant office and interest |
| `food-factory-shop` | `(9518.493, 119.319, 13425.689)` | Shop authority research |
| `mountain` | `(8376.890, 506.293, 10689.776)` | Extreme distance and elevation |
| `harbour-office-yard` | `(13084.369, 113.082, 3543.470)` | Large flat yard and train tests |

These positions are player safety/observation anchors, not yet verified rail spawn anchors. Train
anchors additionally require a stable track ID, point index or position-along-track, direction,
clearance envelope, and safe player evacuation position.

### Verified rail fixture anchors

| Location ID | Track / point | Player anchor | Validation |
|---|---|---:|---|
| `steel-mill-north-test-track` | `[Y]*[#Y]*[#S-550-#T]` / `457`, with track direction | `(8217.779, 129.884, 7729.957)` | Generic track; geometry hash `15844e45f342676bf9fac0d360d8eae61cc99099f3deae2e1ad40e3198bc47b5`; 316.451 m span; point span 228.501 m; away from signals and reported clear |

The named runtime location stores the generic-track opt-in and geometry hash together. A changed
DV railway layout therefore fails location resolution instead of silently spawning on a different
generic track. The player anchor faces the selected track point at yaw `128.5` degrees.

## Required dnSpy fetches

### Fetch 1: authoritative spawning and deletion

Please provide the full classes or methods for:

- `CarSpawner`
  - `SpawnCars`
  - `BaseSpawn`
  - `SpawnCarFromRemote`
  - `SpawnCarOnClosestTrack`
  - `GetFromPool`
  - `DeleteCar`
  - `PrepareTrainCarForDeleting`
  - `GetBoundsOfCar`
  - `IsThereSpaceForCarOnPoint`
- `CommsRadioCarSpawner`
- `Trainset`, especially construction, merge/split, `firstCar`, `lastCar`, and car ordering
- the delayed `AutoCouple` implementation called after multi-car spawning
- `TrainCar.InitializeExistingLogicCar`
- `TrainCarLivery` and the collection used to enumerate spawnable liveries

Also include dnSpy “Used By” results for `CarSpawner.SpawnCars`, `DeleteCar`, and
`PrepareTrainCarForDeleting`.

### Fetch 2: diesel start, stall, and sustain logic

Search the LocoSim assembly and Assembly-CSharp for these strings and fields:

```text
stall
stalled
engine running
engineRunning
engineOn
engine RPM
EngineRPM
starter
fuel
overload
overcurrent
shutdown
traction current
temperature
```

Received and recorded:

- `SimController` and `SimulationFlow`;
- `SimComponent` and `Fuse`;
- `DieselEngineDirect`;
- `DieselEngineDirectDrive`;
- `DieselEnginePowerSource`;
- `DirectDriveMechanism`;
- `InteriorControlsManager` and its `ControlType` registration.
- `StartupHelper.Startup` and `DieselStarterCoro`;
- `InteractableFuseFeeder`;
- `FuseController.Tick`;
- `TrainCarTeleporter.TeleportTrainset` and `TeleportTrainNew`;
- relevant `QuickTutorialFactory` locomotive, coupling, rerailing, spawning, and deletion flows;
- `CommsRadioCrewVehicle`, `RerailController`, and `CommsRadioCarDeleter` action paths;
- `BaseControlsOverrider.SetNeutralState` and `SetBrakesOnSpawn`;
- the direct caller inventories for `Fuse.ChangeState` and `FuseReference.ChangeState`.

Still required:

- concrete starter, fuel-cutoff, emergency-stop, and collision-stop feeder/controller classes;
- the full connection definitions or runtime port IDs for at least DE2 and one mechanical diesel;
- a UnityExplorer dump of all `SimComponent` concrete types, port IDs, fuse IDs, and discovered
  controls on those locomotives.

### Fetch 3: derailment

Please provide:

- `TrainCar.Derail` and every caller;
- `Bogie.Derail` and every caller;
- the component that accumulates `slowBuildUpStress`;
- wheel/track force or curvature checks that decide to derail;
- derail-related settings or difficulty multipliers;
- `TrainCar.Rerail` and `MoveToTrackWithCarUncouple`;
- any `IsDerailed`, `HasDerailed`, or derail-immunity flags.

The goal is to find the decision boundary before the physical teardown begins.

### Fetch 4: tracks, signals, and route protection

Please provide:

- `RailTrack`
  - `GetKinkedPointSet`
  - point/position conversion methods
  - connected-track and junction accessors
- `RailTrackRegistry`
- `TrackID`
- `Junction`, including `Switch` and occupancy restrictions
- the complete signal type inventory and the main signal controller classes
- signal aspect/state setters and their callers
- dispatcher, route, occupancy, and reserved-track classes
- any method used by AI trains or jobs to reserve a route or align junctions

Search strings that may locate the signal subsystem:

```text
signal aspect
SignalAspect
permissive
red signal
route reserved
track occupied
block occupied
dispatcher
```

### Fetch 5: cab/player attachment

Please provide:

- `TrainCar.interior` initialization and loading/unloading behavior;
- `PlayerManager.SetCar` and every caller;
- `CharacterReparenting` train behavior;
- `TrainCar.Resolve`;
- locomotive entrance, cab spawn, seat, or fast-travel anchor classes;
- any standard transform used when teleporting a player into a locomotive.

## Runtime inspection still required

For at least one DE2 and one carriage, capture:

- livery ID, prefab name, car ID, GUID, and network NetId;
- `Trainset` ordering and coupler direction;
- bogie track objects, network track IDs, position along track, and track direction;
- complete `SimComponent` type list;
- complete simulation port and fuse ID list;
- interior controls and their control types/port IDs;
- `TrainCar.interior` hierarchy and plausible standing anchors;
- behavior of the engine when started and immediately commanded to maximum throttle;
- deletion behavior while coupled, moving, occupied, carrying loose items, and holding gadgets.

## Initial implementation sequence after research

1. Add a read-only track/livery/train runtime catalogue.
2. Add a host-only single-car fixture spawn command.
3. Add client projection acknowledgement and fixture cleanup verification.
4. Add ordered multi-car consist creation and coupling verification.
5. Add per-process teleport-to-car-interior commands.
6. Add fixture-scoped derail and fuse protection.
7. Add a diesel control adapter and engine sustain supervisor.
8. Add route leasing and railway-state restoration.
9. Add stationary train item/gadget scenarios.
10. Add moving train-relative item scenarios.
11. Add actual comms-radio and locomotive-remote scenarios as higher-fidelity tests.
