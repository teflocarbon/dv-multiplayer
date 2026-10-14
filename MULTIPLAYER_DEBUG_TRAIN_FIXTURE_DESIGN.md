# Multiplayer Debug Train Fixture and Relocation Transaction Design

> Status: core implementation complete; runtime scenario coverage intentionally follows in a
> separate testing pass. Track-aware route planning, signal dispatch, and train-relative loose
> item relocation remain explicit later phases.

> Build boundary: fixture management and commands are `DEBUG` only. The general trainset
> relocation transaction is production networking because it closes an existing replication gap.

## Purpose

The fixture system creates disposable host-authoritative locomotives and consists for runtime tests
without a special save, manual arrangement, or returning to the main menu. It supports deterministic
track placement, cold/ready/moving state, player placement, loose/snapped/installed items, coupling,
rerailing, moving-train physics, observable transactions, and proved cleanup.

Related documents:

- [`MULTIPLAYER_RUNTIME_TEST_HARNESS_DESIGN.md`](MULTIPLAYER_RUNTIME_TEST_HARNESS_DESIGN.md)
- [`research/DERAIL_VALLEY_TRAIN_TEST_FIXTURE_LOGIC.md`](research/DERAIL_VALLEY_TRAIN_TEST_FIXTURE_LOGIC.md)
- [`research/DERAIL_VALLEY_DEV_CONSOLE_LOGIC.md`](research/DERAIL_VALLEY_DEV_CONSOLE_LOGIC.md)
- [`research/DERAIL_VALLEY_ITEM_SNAPPING_LOGIC.md`](research/DERAIL_VALLEY_ITEM_SNAPPING_LOGIC.md)
- [`research/DERAIL_VALLEY_GADGET_CUSTOMIZATION_LOGIC.md`](research/DERAIL_VALLEY_GADGET_CUSTOMIZATION_LOGIC.md)

## Invariants

1. Only the host creates, moves, rerails, controls, or retires fixture cars.
2. Clients never elevate local representations into authority.
3. Cleanup identity is registered before the first mutation.
4. Every car is recorded by fixture ID, run ID, consist index, NetId, car ID, and GUID.
5. A discontinuous move is one ordered trainset transaction, never unrelated per-car updates.
6. Relocation completes only after host commit and every required ready-client result.
7. Physics snapshots cannot race relocation.
8. Cleanup is proved from registries, projections, trainsets, players, and content—not merely an
   event, `Destroy`, or inactive GameObject.
9. Safety policy is fixture-scoped and never globally changes ordinary trains.
10. Release assemblies contain no fixture manager or command surface.

## Architectural split

### Debug-only control plane

```text
dashboard scenario coordinator
  -> host RuntimeTestAgent
    -> DebugTrainFixtureManager
      -> production host train APIs and packets
  -> client observation/local-player commands
  -> cross-process barrier
  -> reverse-order scenario cleanup
```

It owns intent, fixture/run ownership, route leases, safety supervision, scenario commands,
observations, assertions, and orphan cleanup.

### Production replication plane

It owns existing train spawn/physics/control/coupling/rerail/destroy packets and a new atomic
trainset relocation transaction. The relocation primitive is production code because
`ClientboundMoveTrainPacket.IsTeleporting` is currently unimplemented and this is a general
host-authoritative discontinuity, not a test trick.

## Implemented surface (2026-07-18)

The current implementation provides:

- a stable authored/generic track catalogue with DV hierarchy hashes, geometry fingerprints, and
  session `NetworkedRailTrack` mapping hash;
- exact-track multi-car spawning from arbitrary runtime livery IDs, ledger-first tagging, ordered
  coupling, projection observation, and occupied-car-aware verified cleanup;
- fixture-scoped derail blocking, fuse and health restoration, engine restart-on-stall, junction
  leases, a bounded real-control speed controller, emergency overspeed braking, and manual control
  access;
- local-player placement at named car anchors and evacuation back to the world;
- one reliable ordered `ClientboundTrainsetRelocationPacket` carrying the complete committed
  `TrainsetSpawnPart[]`, plus revision ordering, bounded dependency deferral, topology/brake/bogie
  restoration, interior synchronization, interpolation-baseline reset, and client acknowledgement;
- detailed runtime-test events for fixture allocation/spawn/readiness/control/cleanup and
  relocation host commit/client apply/ack;
- a corrected `RailTrack.Awake` patch that attaches `NetworkedRailTrack` to the actual
  `RailTrack` instance and avoids duplicate components.

No runtime scenarios are included in this pass by request. The implementation is compile-verified;
runtime validation and scenario files are the next phase.

## Debug components

### `DebugTrainFixtureManager`

Location:

```text
Multiplayer/Debugging/RuntimeTests/TrainFixtures/DebugTrainFixtureManager.cs
```

It exists only on the host, serializes fixture mutations on Unity's main thread, maintains the
ledger, validates definitions, calls real spawn/placement/startup/control/deletion APIs, requests
production relocation transactions, coordinates train content, publishes snapshots, and performs
idempotent cleanup.

It does not parse terminal commands, mutate clients directly, accept client transforms as truth,
silently repair failed assertions, or delete non-fixture trains.

### `DebugTrainFixtureTag`

Every host fixture car receives a debug-only recovery marker:

```text
FixtureId          string
RunId              string
CarIndex           int
Role               string
```

The ledger is primary. Tags let cleanup find a car if spawning throws before every identity is
recorded. Clients identify projections by authoritative NetId.

### `DebugTrainSafetySupervisor`

The manager's fixture-scoped safety loop blocks derail transitions, restores opened fuses and
locomotive/car health, restarts a requested engine only after its engine-on readout falls false,
reasserts leased junction branches, and applies the bounded speed controller. It never forces
velocity or transform every frame. Livery-specific fuel, oil, battery, temperature, and causal
failure telemetry remain an explicit later expansion rather than being reported as protected.

## Fixture definition

```csharp
internal sealed class DebugTrainFixtureDefinition
{
    public Guid FixtureId;
    public string RunId;
    public DebugTrainPlacement Placement;
    public DebugTrainCarDefinition[] Cars;
    public DebugTrainInitialState InitialState;
    public DebugTrainSafetyPolicy Safety;
    public DebugTrainContentPolicy Content;
    public DebugTrainPlayerPlacement[] Players;
}
```

Placement specifies DV's stable `LogicTrack().ID.RailTrackGameObjectID`, point index/distance or
normalized curve position, direction, clearance, optional route lease, and search policy. The host
resolves that string through `RailTrackRegistry`, then uses the resolved track's session-local
`NetworkedRailTrack.NetId` for packets. Automated tests default to `ExactTrackOnly`;
Derail Valley's native 2000 m nearby-track search is nondeterministic unless explicitly requested.

Named authored tracks are the default. A track whose logical `TrackID.IsGeneric()` is true requires
an explicit fixture opt-in and a matching hierarchy-path/curve-geometry fingerprint; otherwise
catalogue resolution fails. Host and clients must report identical `TracksHash` and `JunctionsHash`
before train setup begins. This detects incompatible railway layouts before any shared state is
mutated. They must also match a DVMP catalogue hash of sorted durable-track-key to
`NetworkedRailTrack.NetId` pairs, because equal DV hierarchy hashes do not prove equal session ID
allocation.

Each ordered car specifies livery ID, expected role, orientation, coupling, chain state, brake hose,
cock states, MU cable, and future customization preset. Liveries are resolved from the runtime
catalogue and capability-checked before mutation.

Initial states are:

```text
Cold
ReadyStopped
RunningStopped
Moving
Custom
```

- `Cold` applies the confirmed cold/neutral semantics.
- `ReadyStopped` applies safe spawn brakes/resources without starting.
- `RunningStopped` calls `StartupHelper.Startup` and waits for real engine readiness.
- `Moving` selects supported direction/transmission controls, releases the actual applied brake,
  applies bounded throttle, and waits for a speed window.
- `Custom` is scenario-owned and declares capabilities explicitly.

No preset assumes identical controls across liveries.

## Ledger and lifecycle

The ledger records fixture/run IDs, definition hash, lifecycle, route lease, operation ID,
relocation revision, ordered car identities, players, content, interventions, failure, and cleanup
generation. Each car record includes NetId, car ID/GUID, livery, garage provenance, track, Unity
instance, and retirement state.

States:

```text
Reserved -> Validating -> Spawning -> Projecting -> Stabilizing -> Ready -> Running
Ready/Running -> Relocating -> Ready/Running
any live state -> Stopping -> EvacuatingPlayers -> CleaningContent
  -> RetiringCars -> VerifyingCleanup -> Cleaned
failure -> FailedClean | FailedDirty
```

### Ledger-first rule

1. Allocate fixture ID and register cleanup in `RuntimeTestScenarioContext`.
2. Create the `Reserved` record.
3. Validate track, liveries, space, route, capabilities, and evacuation anchor.
4. Enter `Spawning`.
5. Spawn and immediately tag/record each returned car.
6. Project, stabilize, and verify.
7. Enter `Ready` only after all barriers pass.

Thus partial spawning is still discoverable and cleanable.

## Spawn transaction

Use a dedicated host adapter around general `CarSpawner` and `SendSpawnTrainset`; do not use the
economic work-train request.

```text
validate and lease exact track space
spawn ordered cars and record identities
apply precise track placement and brake data
apply coupling/hose/cock/MU topology
send one trainset projection
wait for host initialization
wait for every client projection through the debug barrier
apply initial-state preset
publish Ready
```

The existing spawn packet has no production ack. The debug coordinator supplies the stronger test
barrier: host returns ordered NetIds; every client runs `train.fixture.observe`; each verifies NetId,
livery, ID/GUID, track, order, and topology; failure triggers cleanup. This tests the real production
spawn path without adding debug semantics to normal spawn packets.

## Atomic trainset relocation

### Why per-car move is insufficient

Native `TeleportTrainset` uncouples all cars, preserves orientation, disconnects MU, finds contiguous
space, moves every car, synchronizes every interior physics frame, synchronizes Unity transforms,
and reconstructs topology. Independent car packets expose half-moved consists and race physics,
coupling, and loose content.

### Production packets

```csharp
public sealed class ClientboundTrainsetRelocationPacket
{
    public string OperationId;
    public uint Revision;
    public ushort RootNetId;
    public uint HostTick;
    public TrainsetSpawnPart[] Cars;
}

public sealed class ServerboundTrainsetRelocationAckPacket
{
    public Guid OperationId;
    public uint Revision;
    public ushort RootNetId;
    public byte Status;
    public string ReasonCode;
    public uint AppliedHash;
}
```

This is authoritative replication, not consensus. A client cannot veto host state; its ack drives
diagnosis and targeted recovery.

Per-car payload:

```text
NetId and consist index
absolute position and rotation
front/rear bogie track NetId, distance, and direction
interior-physics presence
```

`TrainsetSpawnPart` already records transforms, both bogies, brake state, coupler endpoints, chain
state/tightness, hose, cocks, prevent-auto-couple state, health, paint, and customization holes. The
relocation packet deliberately reuses that proven serializer rather than introducing a second
nearly-identical train snapshot schema. Payload state is captured after host commit, not inferred
from the requested destination. MU restoration remains a later extension because it is not part of
`TrainsetSpawnPart` today.

### Revision rules

The host increments a relocation revision for every affected trainset. Clients retain the highest
applied revision:

- lower: `StaleIgnored`;
- same operation/revision: idempotent success;
- same revision with another operation: protocol error and full-sync request;
- higher: apply atomically.

Operation ID correlates observability/acks; revision orders state.

### Host sequence

```text
validate authority, cars, topology, track, space, and occupancy
lock all car NetIds/trainset against control, coupling, deletion, relocation, and physics send
capture transforms, bogies, topology, velocities, controls, and content cohort
pause physics publication and freeze loose authoritative train content
apply exact-track native placement
capture committed result and increment revision
reset host physics queues at the discontinuity tick
send reliable-ordered relocation packet
resume physics publication from committed state
wait for required acks/debug observation barrier
```

States are `Created`, `Validating`, `Locked`, `Capturing`, `ApplyingHost`, `HostCommitted`,
`Broadcasting`, `AwaitingAcks`, `Completed`, `FailedBeforeCommit`, `FailedAfterCommit`,
`Compensating`, and `Poisoned`.

### Client sequence

The whole apply runs on Unity's main thread under packet-processing scope:

```text
validate revision and every car/track dependency
defer the whole transaction if dependencies are pending, with bounded timeout
lock local trainset and discard/suspend older physics entries
uncouple affected endpoints and disconnect MU
apply every car/bogie state in consist order
call TrainCarInteriorPhysics.SyncPosition for each relevant car
call Physics.SyncTransforms once
restore chain/hose/cock/MU topology
restore/reproject loose content after interior frames are coherent
reset interpolation and physics baselines to HostTick
compute state hash, unlock, acknowledge
```

Ack statuses include `Applied`, `AppliedIdempotently`, `StaleIgnored`,
`DeferredDependencyTimeout`, `MissingCar`, `MissingTrack`, `TopologyMismatch`,
`InteriorSyncFailed`, `ApplyException`, and `HashMismatch`.

### Failure and recovery

Missing/failed ack does not roll back host truth. The host marks that peer stale, sends a targeted
full trainset recreation/snapshot, then waits for the debug observation barrier. A fixture fails if
recovery cannot be proved.

Host rollback is attempted only when host apply fails before a valid committed result. It uses the
captured before-state. Failed rollback marks the fixture `FailedDirty/Poisoned` and forces cleanup.

## Physics and train content ordering

Relocation is a discontinuity barrier. Clients discard older physics, reset speed/interpolation
queues to the commit tick, and never interpolate across the move.

Before moving, the host captures loose items supported by affected interiors:

```text
item NetId and support-car NetId
interior-relative pose
linear/angular velocity relative to train frame
sleeping state
```

It freezes the cohort, moves the train, restores relative state in the new interior frame, then
emits the normal authoritative item discontinuity after train commit. Clients apply that item state
only after synchronizing the train interior. Snapped/gadget content retains logical attachment;
inventory/cold rows remain non-spatial.

The first version may reject unsupported loose non-fixture content, but must report it rather than
leaving it behind silently.

## Startup, movement, and safety

`SetNeutralState` is used only for an explicit cold reset. Since it may propagate through couplers,
the manager proves all affected cars are fixture-owned or scopes/restores propagation.

`SetBrakesOnSpawn` establishes safe stationary state. The manager records which concrete brake was
applied. `StartupHelper.Startup` is the canonical engine-start path. Movement then uses real
`BaseControlsOverrider` controls: neutral throttle/dynamic brake, direction/transmission selection,
release the applied brake, bounded throttle, and a speed-window assertion.

Safety policy fields include derail prevention, resource maintenance, required-fuse protection,
damage-failure protection, LOD retention, route lease, maximum speed, and duration.

Rules:

- maintain only fixture-car resources;
- use causal fuse readouts;
- control a `FuseController` source port instead of fighting its fuse each tick;
- never globally alter `DrivetrainFailuresAllowed` for player trains;
- prevent derail at the earliest fixture-specific decision point once researched;
- fail route departure or overspeed;
- never auto-rerail during an item-physics assertion.

### Optional automatic speed controller

A debug-only `DebugTrainSpeedController` may drive the real locomotive controls while the fixture
owns a valid route lease. It never writes rigidbody velocity or train transforms. Its first profile
uses a fixed target and maximum speed, bounded throttle steps, service/dynamic braking, and an
emergency stop on lease loss, unexpected occupancy, route departure, or control failure.

The route catalogue can additionally derive a forward speed profile from the same information used
by `DV.Signs.SignPlacer`: ordered `RailTrack` curves, normalized curve positions, curvature radius,
grade, junction boundaries, and connected-track end conditions. This allows later curve-aware and
gradient-aware targets without treating visible sign GameObjects as authority. Track speed values,
braking distance, consist mass, and exact stopping remain later controller phases.

Controller states are `Idle`, `Starting`, `Accelerating`, `Cruising`, `Braking`, `Stopped`, and
`EmergencyStop`. Telemetry records target/actual speed, active track and curve position, upcoming
profile constraint, throttle, dynamic brake, train brake, reverser, and the reason for every state
transition.

## Player placement

Each process owns its local player, so placement remains a per-process runtime command. The host
result exposes anchors such as `car:{index}:cab`, `interior-center`, front/rear platform, and a world
evacuation point. Each runtime resolves the car by NetId and uses native player teleport. Readiness
proves `PlayerManager.Car`, character parenting, and expected NetId agree.

Each process evacuates its local player before host retirement. Host cleanup also evacuates its
local player automatically and refuses to delete any car still containing a remote
`NetworkedPlayer`; this converts a missed cross-process evacuation barrier into `FailedDirty`
instead of deleting a car underneath somebody.

## Debug command surface

```text
train.fixture-catalog
train.track-catalog
train.track-nearest
train.fixture-location-catalog
train.fixture-teleport-location
train.fixture-create
train.fixture-status
train.fixture-observe
train.fixture-acquire-route
train.fixture-set-control
train.fixture-start
train.fixture-stop
train.fixture-relocate
train.fixture-place-player
train.fixture-evacuate-player
train.fixture-cleanup
train.fixture-cleanup-orphans
```

Mutations target only the host. Clients observe or place their local player. Results include normal
runtime attribution, fixture/operation IDs, lifecycle, ordered car identities, relocation revision,
and cleanup state. Scenario definitions remain one test per file and share a
`TrainFixtureRuntimeScenarioDriver`.

Example create input:

```json
{
  "fixtureId": "moving-item-consist",
  "trackId": "SM-A1S",
  "pointIndex": 120,
  "withTrackDirection": true,
  "liveries": "LocoDE2,CabooseRed",
  "roles": "Locomotive,TestCar",
  "preventDerailment": true,
  "protectFuses": true,
  "sustainEngine": true,
  "maximumSpeedKph": 45
}
```

`train.fixture-create` and `train.fixture-relocate` also accept a named `locationId`. A named
location resolves the track ID, point index, direction, generic-track opt-in, and geometry hash as
one validated unit. `train.fixture-teleport-location` moves the local player to that location's
separate trackside safety anchor.

## Cleanup

Cleanup is idempotent and generation-numbered:

```text
PREPARE: reject actions, stop supervisor, finish/cancel relocation
STOP: zero propulsion, apply safe brake, wait bounded stationary state
EVACUATE: move players to world anchor and prove no fixture parenting
CLEAR CONTENT: retire fixture items/gadgets; restore non-fixture content or fail dirty
RETIRE CARS: ReturnCarHome or host CarSpawner.DeleteCar; project reliable destroys
RESTORE SHARED: release route/junction/signal/LOD/safety changes
VERIFY: no tags, live ledger cars, NetIds, IDs/GUIDs, trainsets, logic cars,
        player parents, content, leases, or client projections remain
```

Only verification produces `Cleaned`. Disconnected clients do not block host-world cleanup, but a
later attach must prove no orphan projection remains.

On debug initialization and before each train scenario, orphan recovery scans live fixture tags,
nonterminal ledger records, runtime resources, route leases, and fixture content. It deletes only
objects with a verifiable fixture ID—never all player-spawned cars or all cars of a livery.

## Observability

Every event carries run, fixture, operation, phase, and relevant NetIds. Suggested events:

```text
train-fixture.reserved / car-spawned / projection-observed / ready
train-fixture.control-requested / safety-intervention
train-relocation.locked / host-committed / packet-sent / client-applied / ack-received
train-relocation.recovery-requested
train-fixture.cleanup-started / car-retired / cleanup-verified / failed-dirty
```

Dashboard details show ordered consist identity, host/client projection matrix, tracks/bogies,
relocation/acks, engine/resources/fuses/damage, players/content, scoped train/item packets, and
cleanup proof.

## Initial scenarios

1. Spawn one DE2, observe it on host/client, and prove complete cleanup.
2. Spawn DE2 plus caboose and verify ordered topology/cleanup.
3. Cold reset, native startup, reservoir readiness, and clean stop.
4. Drive above 1 m/s, stop below 1 m/s, and compare host/client state.
5. Relocate a stationary consist atomically.
6. Relocate with one loose fixture item and preserve interior-relative pose.
7. Move continuously with a loose item resting in the cab.
8. Explicit derail/rerail with neutral-state assertions.
9. Chain, hose, cocks, and optional MU replication.
10. Force failure at every lifecycle phase and prove cleanup.

The first scenario is incomplete until repeated runs leave zero cars, projections, leases, or
fixture content.

## Remaining implementation phases

1. **Runtime scenarios:** one-car and multi-car creation, projection barriers, player placement,
   startup/movement, relocation, cancellation/failure injection, and repeated cleanup proof.
2. **Train-relative content:** capture/freeze/restore and item ordering; loose/snapped/gadget/EOT.
3. **Route planning and dispatch:** derive connected track routes, lease signals as well as
   junctions, verify occupancy, and add curve/grade-aware speed profiles.
4. **Safety expansion:** livery-specific resource, temperature, and causal-failure sustain policies
   beyond the existing fuse/health/startup/derail protections.
5. **Broader coverage:** DE6, manual/hydraulic, slug, battery, steam, carriages, gadgets, wiring,
   windows/doors, and modded capability reports.

Deferred: runaway/glitched-item recovery, general port interpolation, arbitrary modded support
without discovery, route implementation before dispatcher research, and automated VR interaction.
