# Derail Valley Train Synchronization Logic

This document records the current DVMP train synchronization model, the invariants established
during the `0.1.16.x` rolling-stock investigation, and the failure signatures that exposed defects
in that model. It covers train identity and baseline projection, continuous on-rail movement,
derailed rigidbody movement, locomotive simulation authority, cab controls, handbrakes, streamed
interiors, and player proximity.

Related research:

- [`../design/trains.md`](../design/trains.md) — proposed
  host-authoritative protocol, canonical state model, reconciliation rules, and rollout plan;
- [`DERAIL_VALLEY_TRAIN_TEST_FIXTURE_LOGIC.md`](DERAIL_VALLEY_TRAIN_TEST_FIXTURE_LOGIC.md)
- [`DERAIL_VALLEY_VR_ITEM_LOGIC.md`](DERAIL_VALLEY_VR_ITEM_LOGIC.md)
- [`DERAIL_VALLEY_WORLD_ITEM_STREAMING_LOGIC.md`](DERAIL_VALLEY_WORLD_ITEM_STREAMING_LOGIC.md)

## Authority model

The host is authoritative for train existence, identity, topology, simulation, and motion.

```text
host TrainCar / Trainset / SimComponent
    -> spawn or baseline snapshot
    -> continuous physics snapshots
    -> port, fuse, brake, coupling, damage, and customization updates
    -> client projections
```

Clients may operate interactive cab controls after server validation. They do not become train
simulation authorities. The accepted control value is applied to the host simulation and relayed
to the other peers.

`SimComponent_Tick_Patch` prevents locomotive simulation components from ticking on clients. The
host therefore owns traction, engine, electrical, pneumatic, resource, and derived state. Client
locomotive simulation is presentation driven by replicated ports, fuses, brakes, and host motion.

## Initial rolling-stock baseline

The server projects each car using `TrainsetSpawnPart`. A complete part contains:

- authoritative train NetId, car ID, GUID, and livery;
- health, explosion, restoration, paint, and customization state;
- coupling topology;
- forward speed;
- transform and both bogie track positions;
- brake-system state.

Baseline delivery is a transaction:

```text
ClientboundTrainBaselineBeginPacket
zero or more ClientboundSpawnTrainSetPacket packets
ClientboundTrainBaselineCompletePacket
```

The client must not report train-baseline readiness until every declared trainset has been applied
and the completion packet agrees with the transaction. A failed trainset spawn is rolled back so a
partially created consist cannot satisfy readiness.

### LiteNetLib nested array constraint

`TrainsetSpawnPart[]` and `TrainsetMovementPart[]` must use registered nested reference types. In
the affected Mono/LiteNetLib environment, readonly-struct array entries could deserialize as the
correct array length filled with default values. The packet appeared structurally valid while
every NetId and field inside it was zero.

Both train part types are therefore classes. Protocol tests must assert individual entries and
their values, not merely array length.

Failure signature:

```text
packet array length is correct
entry NetId == 0 or other entry fields are defaults
invalid NetworkedTrainCar NetId flood
client stalls during Syncing rolling stock
```

## Continuous on-rail movement

`NetworkTrainsetWatcher` samples every moving host trainset on the network tick. Ordinary on-rail
cars send a `TrainsetMovementPart` containing:

- `Speed` from `TrainCar.GetForwardSpeed()`;
- slow-build-up train stress;
- absolute position-along-track and track identity for both bogies;
- an occasional full car transform correction.

### Critical wire-unit invariant

`TrainCar.GetForwardSpeed()` is expressed in **metres per second**. The wire `Speed` field is also
metres per second. Unity `Rigidbody.velocity` is metres per second, so the client applies this value
directly.

Do not multiply or divide this field by `3.6`. Conversion is only appropriate at an explicitly
named UI or debug boundary that reports kilometres per hour:

```text
displayKph = wireMetresPerSecond * 3.6
wireMetresPerSecond = displayKph / 3.6
```

Regression signature from an incorrect divide-by-3.6 conversion:

```text
host speed approximately 70 km/h
client speed approximately 20 km/h
client repeatedly jumps forward toward host position
```

The inverse-looking observation can occur when an already divergent local simulation is being
corrected. Diagnose the actual wire and rigidbody values rather than inferring units from a single
speedometer observation.

### Client application

The client applies a movement part in this order:

1. reject stale host ticks;
2. wake an optimized car when the host reports movement;
3. apply an occasional full transform correction when error exceeds the threshold;
4. apply the authoritative metres-per-second velocity to the car and non-derailed bogies;
5. apply host stress;
6. apply each bogie's track and absolute position-along-track;
7. project the timestamped bogie sample from its host tick to the current network tick using the
   same authoritative metres-per-second speed.

The prediction step must not use an independently simulated client speed. Using the speed carried
with the same host snapshot prevents subscription order, client traction state, frame rate, or a
previous correction from feeding back into the projection.

Removing prediction entirely makes the absolute bogie samples visibly step at network-tick
frequency. Predicting with the wrong speed makes each subsequent snapshot pull the car backward or
forward. Smooth movement requires both the absolute host anchor and correctly unitized prediction.

### Full corrections

The host periodically includes the absolute car transform, currently after approximately two
seconds without a full synchronization. The client only moves to that transform when positional
error exceeds the configured threshold, then clears pending movement queues and resets train
stress.

Repeated full corrections are a symptom, not a smoothing mechanism. If a car needs them
continuously, compare:

- host and client rigidbody forward velocity in metres per second;
- host and client bogie track IDs, spans, and directions;
- received and applied network ticks;
- the correction distance before every `MovePosition`;
- client optimization/awake state;
- derail state.

## Derailed movement

Derailed cars use `RigidbodySnapshot` rather than on-track bogie projection. The client wakes the
car, marks it derailed, and applies the authoritative rigidbody snapshot. Do not run on-track span
prediction for a derailed car.

## Cab controls and simulation state

Cab controls backed by `SimulationFlow` ports are globally replicated train state. Their lifetime
is independent of whether any player's cabin interior is currently loaded.

Client-operated path:

```text
client grabs/changes control
    -> requests per-port authority
    -> sends CommonTrainPortsPacket reliable-ordered
    -> host validates player car/proximity and CONTROL port type
    -> host process applies value to authoritative SimulationFlow
    -> server relays accepted value to other peers
    -> host simulation produces derived state
```

Consequences:

- a client may operate a locomotive while the host is elsewhere;
- when the host later enters, the host should see the already authoritative port values;
- non-control state remains host-produced;
- lights can remain correct while train movement is broken because port/fuse replication and
  physics replication are separate paths.

### Streamed cabin interiors

Physical levers and controls are streamed presentation objects. The underlying `SimulationFlow`
can exist while `TrainCar.loadedInterior` does not.

The initial full-state request may complete before a cabin and its physical controls exist. After
`InteriorControlsManager` finishes initializing and DVMP hooks the controls, the client requests
another authoritative full train-state refresh. This replays all ports, fuses, and the handbrake
onto the newly loaded presentation.

Without that post-interior refresh, a prefab-default lever can disagree visually with the already
correct simulation value. The default-spawn train with an initially engaged handbrake exposed this
race; newly spawned trains whose initial handbrake happened to match the prefab default did not.

## Handbrake synchronization

The handbrake is replicated separately from generic simulation ports through
`CommonHandbrakePositionPacket`. The raw `BrakeSystem.handbrakePosition` is transmitted without
polarity conversion and applied using `BrakeSystem.SetHandbrakePosition`.

Initial `BrakeSystemData` also contains the handbrake position. The post-interior authoritative
refresh is required so an initially engaged handbrake is projected after the physical control
exists.

If handbrakes disagree again, distinguish:

- authoritative `brakeSystem.handbrakePosition` disagreement;
- physical lever presentation disagreement;
- stale initial baseline versus a failed live update;
- a livery-specific control orientation.

## Player proximity and VR

Network polling and ticks use real time and continue while the pause menu changes game time scale.
Pause is not expected to stop train replication.

Approaching a train can wake additional car, interior, collision, and presentation physics. A
latent velocity or prediction error therefore becomes much more violent when another player enters
the train's active physics area. Proximity-induced worsening is evidence that two projections are
fighting, not proof that proximity itself is the authoritative cause.

VR makes small positional corrections and camera-relative train motion substantially more visible.
The train motion protocol is shared by desktop and VR; do not create a VR-specific movement path.
VR-specific player tracking is separate and must not wait for `CustomFirstPersonController`, which
may be absent in VR.

## Confirmed failure signatures

| Symptom | Likely boundary |
|---|---|
| Stuck at `Syncing rolling stock` plus invalid NetIds | nested train-part array deserialization or incomplete baseline transaction |
| One player frozen while that player moves locally | player tracker lifecycle/transmission, especially VR |
| Lights sync but speed does not | physics stream failure; control-port stream is separate |
| Host 70 km/h, client 20 km/h, repeated forward jumps | metres-per-second value incorrectly divided by 3.6 |
| Regular pebble-like judder | bogie samples hard-stepped without prediction, or prediction velocity disagrees |
| Large periodic jumps | full transform corrections masking continuous divergence |
| Shaking becomes worse when another player approaches | dormant projection conflict exposed by fully awake physics |
| Default-engaged handbrake visually reversed after entering | state arrived before streamed physical control; request post-interior replay |
| Client couples cars but host drives one away | server relayed a client-local topology mutation without applying it to the listen-host simulation |
| Coupler/hose disagreement followed by brake desync | mechanical and pneumatic topology diverged; brake simulation is then solving different train lines |

## Coupler and brake-line topology authority

Couplers, air hoses, and angle cocks are simulation topology, not cosmetic interactions. A client
may optimistically run the native interaction locally, but the authenticated request must be
applied to the listen host before it is published to remote clients.

The previous implementation validated `CommonCouplerInteractionPacket` and forwarded it to peers
without explicitly applying it on the server side. It also treated hose connection, hose
disconnection, and cock changes as blind relays. This allowed the requesting client to construct a
coupled consist while the host continued simulating two independent trainsets. Subsequent host
motion corrections produced severe client-side stretching/jumping, and mismatched hose/cock
topology caused the peers to calculate different brake-pipe behavior.

The live path now follows this ordering:

```text
client native interaction
-> authenticated reliable request
-> host validates the selected car/end
-> host applies the mechanical or pneumatic mutation under echo suppression
-> host publishes the accepted operation to remote clients
-> UI topology commits are echoed idempotently to the requester as acknowledgement/repair
```

Host application uses an explicit topology-application scope because the server and listen client
share the same Unity objects. Relying on the client network manager's `IsProcessingPacket` flag is
insufficient while executing a server handler; without the scope, host application would trigger
the Harmony observation patches and recursively emit another request.

UI couple/uncouple receipt is idempotent. Repeating an accepted operation does not couple an
already-correct pair again, and a coupler attached to the wrong endpoint is detached before the
canonical pair is applied. Manual chain dragging and screw animations remain origin-local while
the host applies and relays their semantic transitions.

## Native coupling, hose, and MU sequencing

Current-build dnSpy inspection establishes the following native behavior.

### `Coupler.CoupleTo`

`CoupleTo(other, playAudio, viaChainInteraction)` is synchronous, but its intermediate events do
not represent a settled topology transaction:

1. it returns `null` immediately if either endpoint already has a mechanical partner;
2. it assigns both reciprocal `coupledTo` fields;
3. it creates/configures the joint;
4. if the resulting rigid joint is null, it returns `null` **without clearing either reciprocal
   `coupledTo` field**;
5. it raises `Coupled` independently on both endpoints, catching listener exceptions;
6. it calls `Trainset.Merge`;
7. for non-chain coupling it sets chain presentation, connects the air hose, then opens both
   cocks;
8. it returns the other car.

Consequences:

- A `Coupled` observer runs before trainset membership, hose state, and cock state are final.
- A non-null `coupledTo` pair is not alone proof that native coupling succeeded; the joint and
  derived trainset must also satisfy postconditions.
- The multiplayer host adapter must detect the joint-creation failure path and roll back the
  partial reciprocal pointers through a safe native cleanup path before publishing state.
- Listener exceptions do not abort the native operation because DV catches them, so multiplayer
  observation must validate final state rather than assume every listener ran.

For non-chain coupling, `ConnectAirHose` runs before the cocks open. `HoseAndCock.Connect` therefore
establishes the reciprocal hose first; opening the first cock does not merge the brake sets while
the other remains closed, and opening the second cock performs `Brakeset.Merge`.

### `Coupler.Uncouple`

`Uncouple` clears the initiating endpoint, recursively clears the reciprocal endpoint, and calls
`Trainset.Split` once from the initiating call. Its pneumatic behavior depends on the reason:

- ordinary non-chain uncoupling closes both cocks, disconnects the hose, and parks both chains;
- broken coupling disconnects the hose and changes a tight chain to dangling;
- chain-interaction uncoupling can remove the mechanical relationship while leaving hose/cock
  state intact.

The reciprocal invocation performs its hose/cock/state work before the initiating call reaches
`Trainset.Split`. `Uncoupled` events are raised after the local tail work, once per endpoint, but
the two callbacks still occur at different points in the outer call.

This proves mechanical coupling and pneumatic hose connection are separate topology domains. A
canonical model must not automatically erase a hose merely because mechanical coupling is absent;
the requested uncoupling mode and final native state determine the result.

### `HoseAndCock`

`HoseAndCock.Connect` rejects null, self, and an already-connected endpoint, then assigns both
`connectedTo` fields synchronously. It fires the hose-connection event and merges the two
`Brakeset` instances only when both cocks are open.

`Disconnect` fires its event, splits the brake sets when both cocks are open, then clears both
reciprocal pointers. `SetCock` merges or splits brake sets when the opposite connected cock is open,
then raises `CockChanged`. Thus:

- hose reciprocity can be verified immediately after `Connect`/`Disconnect` returns;
- brake-line membership changes synchronously through the native methods;
- direct writes to `cockOpen` or `connectedTo` would bypass required brake-network work and events;
- canonical reconciliation must call `Connect`, `Disconnect`, and `SetCock` (through the coupler
  wrappers where presentation events are also required), then read back postconditions.

### Multiple-unit module

`MultipleUnitModule` owns distinct front and rear `MultipleUnitCable` objects. Its automatic helper
requires the two supplied couplers to be mechanically partnered, both cars to support MU, the
general MU licence to be acquired, and both selected cables to be free before connecting them.

The save-restoration coroutine waits 0.7 seconds and chooses the mechanically coupled endpoint or,
if none exists, the first nearby coupler. MU control propagation follows reciprocal cable
`connectedTo` references and accounts for reversed connection orientation. This confirms that MU
must name its exact partner endpoint in canonical state; a front/rear connected boolean is
insufficient for repair.

`MultipleUnitCable.Connect` rejects null, self, and occupied endpoints, then assigns both reciprocal
`connectedTo` fields. Before raising any connection event it mutates controls on **both** MU cars:

- throttle, dynamic brake, sander: `0`;
- reverser: `0.5`;
- front and rear headlights: `0.4`;
- train brake: the greater of the two current values is applied to the lower side;
- independent brake: the greater of the two current values is applied to the lower side.

It then raises the static `AnyConnectionChanged` callback followed by both endpoint callbacks. Only
the endpoint callbacks are exception-contained; an exception during the earlier control changes
can leave reciprocal cable pointers established without completing events.

`MultipleUnitCable.Disconnect` clears both reciprocal pointers first, then raises the static and
endpoint callbacks. It does not restore the controls changed during connection.

Consequences:

- MU connection is a cross-domain topology-and-control transaction.
- Host application must suppress both MU topology echoes and control-port echoes while calling the
  native method.
- The committed result must include the resulting control revision/state as well as MU endpoint
  state; broadcasting only `CommonMuConnectedPacket` cannot repair a peer that missed the native
  control side effects.
- MU success is verified from both reciprocal cable pointers and the resulting canonical controls,
  not from callbacks alone.

### `Trainset.Merge` and `Trainset.Split`

Both methods synchronously rebuild trainset objects and fire `TrainsetsChanged` only after assigning
every affected car's new `trainset` reference and `indexInTrainset`.

`Merge(a, b)` reverses each source list based on the supplied cars' indices, appends the second list
to the first, destroys both old set registrations, creates one new set, rewrites all memberships,
then raises `TrainsetsChanged(true)`. It assumes that `a` and `b` are the correct end cars of two
different trainsets. It contains no guard against the same trainset, a non-end car, or an invalid
endpoint pairing.

`Split(carA, carB)` orders the arguments by index, divides the existing list at `carB`'s index,
creates two new sets, rewrites memberships, then raises `TrainsetsChanged(false)`. It assumes the
cars are in the same trainset and form the adjacent coupled boundary. It does not validate those
preconditions itself.

Therefore server coupling validation must reject:

- endpoints whose cars are already in the same mechanically connected trainset;
- an endpoint that is not endmost in its source trainset;
- uncoupling endpoints that are not reciprocal partners, not in the same trainset, or not adjacent
  in trainset order.

After the native call returns, the adapter can synchronously verify list ordering, membership,
indices, and reciprocal endpoints. `TrainsetsChanged` is a useful dirty/audit signal, but because
it contains only a `merged` boolean and no identities or revision, it cannot acknowledge a network
transaction.

### `CouplerLogic` wrappers

`CouplerLogic.Uncouple` captures the opposite endpoint, delegates synchronously to
`Coupler.Uncouple`, records analytics, and optionally applies a full handbrake to the detached
partner car afterward. The optional handbrake is another control mutation that must be included in
the resulting host control state.

The couple helpers may optionally release handbrakes on every car that belonged to the approached
trainset before the merge. They decide apparent success using `IsCoupled`, which is insufficient
for the `CoupleTo` joint-creation partial-failure path. The multiplayer adapter must use stronger
postconditions rather than copying that test.

### Brake-set topology and pressure

`Brakeset.Merge(a, b)` mirrors `Trainset.Merge`: it reverses source lists based on the supplied
members, appends them, creates a replacement set, rewrites every `BrakeSystem.brakeset` and index,
then raises `BrakesetsChanged(true)`. It does not validate that the supplied brake systems are
endmost members of different sets.

The merged pipe pressure is a volume-weighted value:

```text
mergedPressure = lerp(a.pressure, b.pressure, b.volume / (a.volume + b.volume))
```

`Brakeset.Split` divides the list at the second member, creates two replacement sets, and gives
both the old shared pipe pressure. It then rewrites all memberships and raises
`BrakesetsChanged(false)`. It assumes same-set adjacent members and does not validate them.

Therefore brake-pipe pressure is **brake-line state**, not independent car state. A connected
brakeset has one authoritative `pipePressure`; each car's `brakePipePressure` is a smoothed readout
updated later by `BrakeSystem.UpdateReadouts`. Sending/applying the readout independently per car
can make packet order choose the final value of a shared client brakeset.

Canonical replication should address the brake-line component produced by a topology revision,
send its shared pipe pressure once, and keep main/aux/control reservoirs and cylinder/readout state
as per-car values. A topology merge must publish the host's volume-weighted result immediately;
clients cannot derive it reliably from potentially divergent pre-merge presentation values.

### Brake event ordering and partial failure

`HoseAndCock.Connect` assigns reciprocal hose pointers, calls
`Brakeset.FireHoseConnectionChanged`, and only then conditionally calls `Brakeset.Merge`.
`FireHoseConnectionChanged` invokes subscribers without exception containment. A throwing listener
can therefore leave reciprocal hose pointers connected while the two cars remain in separate brake
sets.

`HoseAndCock.Disconnect` similarly fires the hose event before splitting the brake set and clearing
the reciprocal pointers. A throwing listener can prevent the disconnect. `BrakesetsChanged` also
has no exception containment, although it runs after memberships have been rewritten. Native
adapter rules follow:

- wrap native calls, but never infer failure solely from an exception;
- inspect hose reciprocity, brake-set membership/order, and shared pressure afterward;
- repair a connected-hose/separate-brakeset partial state when both cocks are open;
- repair a disconnected-hose/shared-brakeset partial state;
- treat hose/brakeset callbacks as dirty diagnostics, never commit boundaries.

### Brake tick and discrete controls

`Brakeset.TickAll(dt)` iterates every brake set. Each set determines atmospheric leakage from its
endmost cocks, balances connected main reservoirs, ticks every member `BrakeSystem`, and derives
aggregate handbrake/cutout flags. `BrakeSystem.Tick` mutates the shared line pressure, reservoirs,
cylinder pressure, braking force, angle-cock pressurization, heat, and smoothed readouts using
`dt`. Only the host may execute this simulation as authority; different frame rates must not create
independent client truth.

`BrakeSystem.SetHandbrakePosition` clamps to `[0,1]`, writes the field, then raises
`HandbrakePositionChanged` even when the value did not change. The event is not exception-contained.
Canonical idempotent application should compare the accepted value before calling it, suppress the
resulting observation, and read the clamped value back.

`ReleaseBrakeCylinderPressure` raises `BrakeCylinderReleased` **before** setting
`controlReservoirPressureReleased = true`. A throwing listener can prevent the requested state
change entirely. Host command handling must verify that postcondition rather than accepting event
observation as success.

`BrakeSystem.SetBrakePipePressure` refuses to operate on a brakeset containing multiple cars. It
cannot be used as the general connected-line correction API; line reconciliation must deliberately
apply the canonical shared `Brakeset.pipePressure` and separately update presentation readouts as
needed.

## Required regression coverage

### Canonical synchronization implementation (2026-07-19)

The working tree now contains the detached endpoint state machine, revisioned topology commands,
host native transaction adapter, client snapshot reconciler, shared brake-line snapshots, and
revisioned commits for ports, fuses, handbrake, and cylinder release. Rolling-stock baseline,
ordinary spawn, deletion, relocation, topology resync, and interior re-entry all carry or trigger
canonical state. Native automatic coupling is committed on the following host tick because the
base-game `Coupled` event precedes `Trainset.Merge` and hose/cock completion.

Topology revision reads are deliberately O(1). Full graph capture occurs only for an observed
mutation, baseline, or explicit resync; brake heartbeats must never perform a world-wide topology
scan. Ordinary topology commits omit full cab controls, while baseline/resync includes them and MU
transactions include only affected cars.

Release compilation and the detached core/protocol suites pass. Two-process runtime verification
is still required before this architecture can be called proven, so the runtime matrix below
remains the acceptance gate rather than an implied claim of complete in-game validation.

### Derailed motion and topology transaction boundary (2026-07-20)

A live 0.1.16.6 failure demonstrated why correct car transforms are not sufficient. The host had
physically separated most of a derailed consist while the client retained native coupler joints.
Host rigidbody snapshots then moved independent cars while the client's stale joints tried to keep
them together, producing violent corrections despite both peers eventually showing cars in the
same broad locations. Manually removing the stale client joint immediately stopped that conflict.

Client derailment application must therefore be a packet-level transaction:

- transition every affected car to the native derailed state first;
- enqueue every authoritative rigidbody sample;
- only after the complete packet, reapply the last committed host topology once;
- request a fresh topology snapshot if that post-derail reconciliation fails;
- replay topology and brake-line membership only, never stale bundled cab controls;
- interpolate/extrapolate derailed position and rotation between host samples instead of applying
  packet-rate `MovePosition`/`MoveRotation` jumps.

This does not make the client a derailment authority. The client predicts presentation between
samples; host pose, velocity, angular velocity, coupler graph, hose graph, and brake-line
membership remain canonical.

### Coupler joint ownership and chain FSM (2026-07-20)

`Coupler.CoupleTo` is direction-sensitive: it creates `rigidCJ` on the receiving `Coupler`, and
`SetChainTight` silently returns when invoked on an endpoint without that joint. A topology
reconciler must therefore preserve which endpoint owns the attached chain state; choosing the
`CoupleTo` caller by NetId can reproduce the correct partner and hose graph while leaving the
chain endpoint permanently `Parked`. That single mismatch prevents the revision from committing,
which in turn makes every later client command stale and every brake heartbeat wait for topology.

When rebuilding a canonical pair, invoke `CoupleTo` on the endpoint whose desired state is
`AttachedLoose` or `AttachedTight`. If the pair is already mechanically reciprocal but its joint
lives on the opposite endpoint, uncouple without cascading the hose, then rebuild with the
canonical owner before applying chain tightness. Resync requests caused by one pending revision
must be coalesced; per-car brake heartbeats must not create a reliable-packet feedback loop.

Existing automated coverage proves:

- all spawn and movement array entries survive LiteNetLib round-trip serialization;
- movement NetIds, speed, position, and multiple entries remain intact;
- train baseline begin/complete transaction behavior;
- failed baseline application cannot commit partial readiness.

Still required:

1. A two-process scenario that drives a train through several steady speeds and compares host and
   client rigidbody forward velocity in metres per second and displayed km/h.
2. A correction-quality assertion that records maximum and RMS bogie/car correction distance,
   rather than accepting eventual position agreement after visible jumps.
3. The same scenario with deliberately different desktop frame rates.
4. A VR passenger scenario at steady speed, acceleration, braking, and pause/unpause.
5. Host-far/client-in-cab control changes followed by host cabin entry and port-by-port comparison.
6. A default-spawn locomotive whose handbrake begins engaged, verified before and after interior
   unload/reload.
7. A host-approach scenario that compares correction metrics while the host is far, nearby, and
   aboard.
8. Coupled multi-car and derail transitions using the same metrics.
9. Client-initiated coupling where both peers immediately agree on both endpoint identities,
   trainset membership, chain state, hose connection, both cock states, and brake-pipe pressure.
10. Coupling rejection and wrong-endpoint recovery without stretching, teleporting, or leaving a
    one-sided coupler/hose relationship.
11. A high-energy multi-car derailment where several host couplers break in one physics interval;
    assert matching endpoint graphs after every committed revision and bounded visual correction.

Passing only an eventual-position or scenario-completion assertion is insufficient. A client can
arrive at the correct position through repeated visible teleports while the test reports success.

## Implementation invariants

Keep these rules explicit in code reviews and future refactors:

1. The host remains the only locomotive and train-motion authority.
2. Train baseline transactions are atomic from the client's readiness perspective.
3. Registered nested train-part arrays use reference types and entry-level protocol tests.
4. On-rail wire speed is metres per second end-to-end.
5. Kilometres-per-hour conversion occurs only at named display/debug boundaries.
6. Bogie prediction uses the speed from the same authoritative snapshot.
7. Full transform correction is recovery, not normal locomotion.
8. Cab interiors are presentation; simulation ports survive their unload.
9. Newly loaded interiors request authoritative state after controls initialize.
10. Tests measure smoothness and cross-peer instantaneous state, not only final convergence.
