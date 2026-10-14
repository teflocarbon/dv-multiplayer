# Multiplayer Train Synchronization Design

> Status: canonical topology and control authority implementation complete in the working tree;
> build and two-process runtime verification remain. Motion epochs and quantitative correction
> telemetry remain the next independent stage.

## Implementation status (audited 2026-10-14)

Checked against the code on `fix/multiplayer-stability`. Where this differs from the design text
below, this section describes what the code does.

- **Runtime verification:** done for topology. The dvmp gate (`DevTools/Scenarios/gate.sh`) runs
  `manual-uncouple` (hand uncouple/recouple with lag), `uncouple-moving`, `couple-impact` (coupling
  while the buffers bounce, by menu or chain) and `fps-drop`. `host-stall` covers a host frame-rate
  collapse.
- **Stages 0–3:** implemented. The relay-only MU, hose and cock handlers are gone (their `Common*`
  packet classes remain as unreferenced code).
- **Stage 4:** control commit/ack is implemented for handbrake, fuses, release and ports. The
  thresholded simulation snapshot with `simulationRevision` was **not** built: brake state uses the
  brake-state packet (0.02 bar threshold, 2 s heartbeat, topology-revision gating), and host
  simulation port values ride on control commits.
- **Stage 5:** not started. There is no `motionEpoch`; only a >5 m snap clears a client's queues,
  rerail doesn't reset them, and `SendTrainsetRelocation` has no caller. No correction or bandwidth
  telemetry. Motion smoothing that did land: separate speed/correction easing with an impact step
  (`TrainSpeedQueue`), the network tick advancing by elapsed time at any frame rate
  (`NetworkLifecycle`), and full syncs ignoring a just-spawned car's pooled rigidbody.
- **Stage 6:** partly done. Protocol mismatch is rejected at connect. Still present: the chain
  drag/screw path through `CommonCouplerInteractionPacket` mutates the host with a 2 s timed
  suppression, and the client still subscribes the legacy `Common*` control receivers.
- **Client coupler joints** no longer break on clients (`CouplerBreakPatch`); a break that still
  happens locally is undone from the host's topology instead of reported.
- **Not yet built:** periodic topology digest audit, wrap-safe revision comparisons, control lease
  expiry other than disconnect/release, the perf counters in "Performance budget".

## Goal

Make the host the only authority for rolling-stock identity, topology, simulation, and motion while
allowing a nearby client to operate a train without waiting for the host player to be present.
Every accepted client action must become host state first. Every peer must then be able to converge
from a canonical snapshot even if it missed an interaction, loaded an interior late, left interest,
joined late, or predicted the action incorrectly.

The base-game investigation and confirmed failure signatures are maintained in
[`research/DERAIL_VALLEY_TRAIN_SYNCHRONIZATION_LOGIC.md`](research/DERAIL_VALLEY_TRAIN_SYNCHRONIZATION_LOGIC.md).
Fixture behavior is maintained in
[`research/DERAIL_VALLEY_TRAIN_TEST_FIXTURE_LOGIC.md`](research/DERAIL_VALLEY_TRAIN_TEST_FIXTURE_LOGIC.md).

## Core invariant

> The host's canonical train records are truth. A `TrainCar`, `Trainset`, coupler, hose, cab
> control, brake component, or rigidbody on any process is a projection of those records. A local
> Unity interaction may predict presentation, but it cannot commit shared state.

This is the same boundary that made world-item synchronization recoverable: identity and revisions
outlive individual Unity events and projections.

## Why the current event model is insufficient

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
| Motion | Host tick, bogie anchors, m/s velocity, derail snapshots | Needs measured correction budgets and explicit relocation/derail epochs everywhere |
| Couplers | Revisioned host command, invariant validation, native postcondition and canonical repair | Remove legacy transient compatibility paths after runtime soak |
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
packet types.

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
coupler, or MU connector. Missing components are valid data, not lookup failures.

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

### Interaction versus truth

Dragging a chain, hand pose, audio, and a screw animation are ephemeral presentation. They may use
unreliable or origin-excluding messages and may be dropped. Attached/parked/dangling/tight state is
canonical when it affects later interaction and is included in endpoint state.

Long Unity coroutines must not define the authority transaction. The host can animate toward a
committed state, but the canonical postcondition is recorded synchronously or at an explicit
bounded completion point. A fixed two-second echo-suppression timer is an interim containment
mechanism, not the final design.

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

## Brake and locomotive simulation

The host alone ticks locomotive and pneumatic simulation. Clients display snapshots and never feed
locally integrated pressure, traction, wheelslip, or resource values back as truth.

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

Native hose events run before brake-set merge/split and are not exception-contained. The host
adapter catches exceptions, then checks reciprocal hose pointers, brake-set membership/order, and
line pressure before deciding whether to commit or repair. A Unity callback is never proof of
brake-line convergence.

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

## Loading-state delivery

Every broadcast helper must enforce its declared minimum loading state. A helper that accepts
`minimumLoadState` but does not check it can deliver topology to a peer before rolling-stock
baseline readiness and create an unrecoverable ordering race.

Canonical snapshots received before object readiness are retained in a bounded pending map keyed
by epoch/revision. They are not dropped and are not applied to a guessed prefab.

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

The dashboard should expose for each live train:

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

### Stage 3: MU topology (implemented)

- move MU cables into the same endpoint transaction model;
- remove relay-only MU handlers after compatibility transition.

### Stage 4: controls and brakes (implemented; runtime verification pending)

- convert handbrake, release, fuses, and ports to host commit/ack;
- add revisioned, thresholded brake simulation snapshots and heartbeat;
- verify topology-revision gating for pressure state.

### Stage 5: motion quality

- add motion epochs and correction telemetry;
- enforce quantitative smoothness and bandwidth budgets across frame rates and VR.

### Stage 6: delete legacy authority paths

- remove common mutation packets and timed suppression;
- retain only canonical command/snapshot paths and explicitly transient animation messages;
- make protocol-version mismatch fail during connection rather than partially interoperate.

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
