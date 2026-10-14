# Multiplayer Train Item Wake System Design

> Status: first production implementation completed on 2026-07-19. Train-only settlement records,
> bounded support propagation, validated impact witnesses, car-motion/derailment wakes, distant
> simulator selection, pending recovery, and the Train Item Lab are implemented. Threshold tuning,
> large-pile soak testing, and destructive live derailment coverage remain validation work rather
> than missing architecture.

## Purpose

Define how loose items resting inside a moving train become temporarily active again after a
physical disturbance, while preserving host authority when the host is far away and cannot locally
simulate or even materialize the train interior.

This design extends, rather than replaces:

- [`MULTIPLAYER_ITEM_SPATIAL_PHYSICS_SYNC_DESIGN.md`](MULTIPLAYER_ITEM_SPATIAL_PHYSICS_SYNC_DESIGN.md),
  which owns simulation leases, transient samples, and reliable settlement;
- [`MULTIPLAYER_ITEM_LIFECYCLE_DESIGN.md`](MULTIPLAYER_ITEM_LIFECYCLE_DESIGN.md), which owns
  logical placement and transition precedence;
- [`MULTIPLAYER_ITEM_WORLD_SYNC_DESIGN.md`](MULTIPLAYER_ITEM_WORLD_SYNC_DESIGN.md), which owns
  identity, interest, and canonical projections; and
- [`MULTIPLAYER_DEBUG_TRAIN_FIXTURE_DESIGN.md`](MULTIPLAYER_DEBUG_TRAIN_FIXTURE_DESIGN.md), which
  owns disposable host-created trains and safe runtime-test control.

The governing distinction is:

> The host owns truth and permission. The selected simulator owns temporary Unity physics.

Host-authoritative must not be interpreted as host-simulated.

## Scope

This system applies to loose `TrainInterior` items that have completed spatial settlement and are
physically parented through Derail Valley's item-reparenting lifecycle. It covers:

- removing or moving an item that supports other settled items;
- an active loose item striking a settled item;
- heavy train acceleration or braking;
- derailment and violent train rotation;
- a train striking the environment or another train with a material jolt;
- bounded propagation through stacks or piles;
- distant-client simulation while the host remains authoritative;
- lease transfer and recovery; and
- deterministic debug arrangements and runtime scenarios.

It deliberately does not cover:

- player-body contact, because players do not physically move loose items in the base game;
- ordinary world-floor item contact beyond the existing spatial system;
- cargo, snapped equipment, installed controls, coupler-mounted items, or container contents;
- continuous full rigidbody simulation by the host for every train item;
- exact deterministic reproduction of chaotic collision outcomes across peers; or
- normal coupling as an automatic whole-interior wake event.

Normal coupling may still produce a wake if the accepted train motion crosses the general jolt
threshold. It does not receive a special low-threshold rule.

## Problem statement

Settled train items are currently made kinematic after a stable train-local pose is committed. This
is intentionally more stable than leaving every loose item under recurring `CabItemRigidbody`
jitter, but kinematic settlement creates new responsibilities:

1. A top box does not fall when its supporting bottom box is removed.
2. A thrown box treats a settled box as an immovable part of the train.
3. Heavy acceleration, braking, impact, or derailment cannot disturb settled items.
4. The host may be outside interest and unable to observe the relevant contacts.
5. Waking every item every frame would be too expensive and would create lease and packet storms.

The solution is an event-driven wake system that temporarily reactivates a validated, bounded set
of items, runs the existing spatial-lease lifecycle, and returns each stable result to kinematic
train-local settlement.

## Required invariants

1. The host is the only authority that changes canonical wake, lease, placement, and settlement
   state.
2. Exactly one simulator owns an active spatial epoch for an item.
3. Host adjudication does not require a loaded host `GameObject`, `Rigidbody`, collider, or train
   interior.
4. A client may report physical evidence but cannot grant itself or another item a canonical wake.
5. Every wake request names a train, source, reason, canonical item identities, and source epoch or
   train-motion revision.
6. An item can be woken only from a current `TrainInterior/Settled` record on the named train.
7. Direct logical transitions such as pickup, inventory, container, Lost and Found, destroy, or
   reparent supersede wake and motion state.
8. Wake propagation is deduplicated, rate-limited, and bounded in items and graph depth.
9. Genuine sliding, rolling, bouncing, or flight resets settlement dwell.
10. A settled result remains physically parented to the correct DV train receiving-forces body.
11. Settled train items are kinematic on every projection until a host-approved wake or direct
    release makes the chosen simulator dynamic.
12. Clients entering interest receive the latest wake/lease or committed settlement state, never a
    stale pre-disturbance pose.
13. Simulator disconnect, timeout, interest loss, and train unload have explicit recovery paths.
14. Production logic performs no recurring all-items scan.
15. Debug fixtures and tests cannot mutate non-fixture trains or leak items, leases, NetIds, route
    ownership, or safety policy.

## Terminology

### Glued item

A loose item with canonical `TrainInterior/Settled` state whose physical body is kinematic while it
retains the full DV train parent. "Glued" is presentation shorthand, not a new logical placement.

### Active item

A train item with an open spatial simulation lease. Its selected simulator runs a non-kinematic
body and sends train-local motion samples.

### Wake source

The event that may invalidate one or more settled poses:

```text
SupportRemoved
ItemImpact
HeavyAcceleration
HeavyBraking
Derailment
TrainJolt
Administrative
Recovery
```

### Wake seed

The first settled item directly affected by the source. A wake can have multiple seeds.

### Wake set

The complete bounded set approved by the host after validation and optional support propagation.

### Physical witness

A report from the peer currently able to observe contacts or support geometry. A witness is input
to host validation, not authority.

## State model

The wake system is a policy layer around the existing spatial states:

```text
TrainInterior / Settled / Glued
  -> WakeRequested
  -> WakeApproved
  -> TrainInterior / ActiveLease / DynamicSimulator
  -> SettlementProposed
  -> TrainInterior / Settled / Glued
```

An item can leave this flow at any time through a higher-precedence logical transition:

```text
pickup -> PlayerHand
inventory -> PlayerInventory
container -> Container/Cold
lost and found -> LostAndFound
outside train -> World
destroy -> Tombstone
```

`WakeRequested` and `WakeApproved` should not become durable item placements. They are transient
coordinator state correlated by wake operation ID, item NetId, authority revision, and simulation
epoch.

## Implemented production architecture

### `NetworkedTrainItemWakeManager`

Implementation location:

```text
Multiplayer/Components/Networking/World/WorldItems/NetworkedTrainItemWakeManager.cs
```

This manager owns train-specific wake policy and must not be folded into
`NetworkedItemSpatialManager`. It maintains:

- settled item membership by train NetId;
- canonical train-local pose and prefab collision bounds for each glued item;
- pending wake operations and deduplication keys;
- per-car motion history and wake cooldowns;
- per-source rate limits and cascade budgets;
- pending wakes waiting for a simulator; and
- observability snapshots.

It receives lifecycle notifications rather than polling:

```text
OnTrainItemSettled
OnTrainItemLeftSettlement
OnLogicalPlacementChanged
OnSupportItemInteractionStarted
OnItemImpactWitness
OnAcceptedTrainMotion
OnDerailmentChanged
OnSimulatorDisconnected
OnInterestChanged
```

It returns narrow decisions to the existing spatial manager:

```text
ApproveWakeSet
BeginWakeLease
TransferWakeLease
CancelPendingWake
ReconcileToCommittedPose
```

### `NetworkedItemSpatialManager`

The spatial manager remains responsible for:

- epoch and sequence ordering;
- simulator selection integration;
- sample validation and relay;
- settlement proposal and host commit;
- kinematic/dynamic presentation; and
- committed spatial checkpoints.

It should not decide support graphs, acceleration policy, derailment policy, impact thresholds, or
per-car wake budgets.

### Canonical wake records

Host wake records must survive missing Unity representations. A proposed minimal record is:

```csharp
internal sealed class TrainItemWakeRecord
{
    public ushort ItemNetId;
    public uint ItemAuthorityRevision;
    public ushort TrainCarNetId;
    public Vector3 ParentLocalPosition;
    public Quaternion ParentLocalRotation;
    public string PrefabName;
    public Bounds LocalCollisionBounds;
    public bool Settled;
    public uint LastCommittedSpatialEpoch;
    public uint LastWakeRevision;
}
```

`Bounds` must come from a stable prefab-bounds catalogue or a previously validated materialized
representation. A remote witness may refine contact points but may not redefine an item's canonical
size without validation.

### Network messages

The implementation reuses the existing spatial lease/grant/state packets and adds one reliable
collision-evidence packet:

```text
Client -> Host: ServerboundItemTrainWakeWitnessPacket
Host -> Simulator/Observers: existing item spatial lease and state packets
```

The wake witness is reliable because it opens authority work. High-frequency motion continues to
use the existing spatial sample protocol. Wake grants are represented by a fresh spatial epoch,
with a `train-wake:*` reason and an initial train-local velocity. This preserves the existing epoch,
simulator, sample-validation, settlement, and late-interest rules rather than adding a parallel
authority protocol. The witness includes source/target revisions and epochs, car identity, network
tick, relative velocity, impulse, and absolute contact position. Coordinator events and debug
snapshots retain the reason, selected simulator, approved item set, cascade truncation, and
rejection evidence without duplicating those fields into a second grant format.

## Host authority with a distant client

The host must be able to adjudicate a wake when:

- the host player is outside item interest;
- the host train interior is unloaded;
- the host item projection is inactive or retired;
- the nearby client is driving a separate train; and
- only that client has colliders capable of observing the event.

The host therefore operates on canonical records and accepted network state. Physical application
is optional presentation work.

### Host responsibilities

The host:

1. verifies the sender is an eligible witness or current simulator;
2. resolves every item and train from canonical identity;
3. verifies current placement, train membership, revision, and settled/lease state;
4. validates spatial plausibility in train-local coordinates;
5. computes or constrains the wake set;
6. chooses the simulator;
7. increments epochs and sends grants;
8. validates motion samples; and
9. commits final settled poses.

### Client responsibilities

The nearby client:

1. observes contact/support evidence that the host cannot observe;
2. sends bounded evidence using canonical NetIds and local poses;
3. waits for or predicts around the host grant according to presentation policy;
4. makes only approved items dynamic;
5. simulates using the existing train-relative item lifecycle; and
6. proposes reliable settlement.

### No eligible simulator

If a wake is valid but nobody can simulate it:

- retain the last committed glued pose;
- record a bounded pending wake on the host;
- do not fabricate a physical outcome;
- retry selection when an eligible train simulator or interested peer appears; and
- expire or reconcile the pending operation under an explicit recovery timeout.

The fallback must be observable. Silent cancellation would make intermittent distance bugs almost
impossible to diagnose.

## Simulator selection

Choose in this order, subject to readiness and representation capability:

1. the peer currently simulating the train;
2. a player occupying the affected car/trainset;
3. the current simulator of the impacting item;
4. the nearest interested ready client with the affected interior loaded;
5. the host if it has a valid active representation; or
6. no simulator, producing a pending wake.

Selection is performed once per wake operation where possible. A stack should normally share one
simulator instead of distributing mutually colliding items across peers.

The selected peer receives no logical ownership. Its authority is restricted to the named item
epochs and motion envelope.

## Wake causes

### Support removal

When a glued item is about to be picked up, moved, stored, destroyed, or reparented, nearby glued
items may lose support.

The locally observing peer performs one layer-filtered overlap or short upward sweep around the
source item's canonical bounds. It reports candidate NetIds, contact points or separation, and
local bounds. The host validates candidates against its canonical records.

Initial implementation policy:

- only candidates on the same train car are considered;
- candidates must overlap horizontally within a configured tolerance;
- their lower bound must be close to the source's upper bound or intersect its expanded bounds;
- the query is capped before transmission;
- the host expands the wake set through a bounded support search; and
- unrelated floor items remain glued.

The interaction itself should not be blocked on a round trip in the first implementation. A top
item may appear unsupported but glued for one network delay until the grant arrives. Client-side
predicted ungluing can be added later if captures show that delay is objectionable; rejected
prediction must reconcile to the committed pose.

### Item impact

Only active dynamic items need impact observation. When an actively leased item collides with a
glued train item, its simulator reports:

- impacting and target item NetIds;
- active lease epoch and sequence;
- train car NetId;
- contact point and normal in train-local space;
- relative velocity;
- impulse when Unity supplies a meaningful value; and
- source tick/time.

The host validates that the sender owns the impacting epoch, both items are plausibly co-located,
the target is currently glued, the train matches, and the impact exceeds configured thresholds.

The target then becomes a wake seed. Other supported items may join through the bounded support
search. Minor resting contacts, repeated callbacks from the same collision, and train vibration are
deduplicated.

Fast active items should use continuous collision detection or an equivalent swept test while
their lease is in flight, subject to item type and performance testing.

### Heavy acceleration and braking

Train speed alone never wakes items. The host derives motion from accepted authoritative train
updates:

```text
linearAcceleration = (acceptedVelocityNow - acceptedVelocityPrevious) / dt
angularAcceleration = (acceptedAngularVelocityNow - acceptedAngularVelocityPrevious) / dt
jolt = change in acceleration over dt
```

Measurements should be evaluated in a stable train-local or trainset frame and filtered over a
short window. Teleport, relocation, loading correction, and network interpolation resets must be
identified explicitly and excluded.

Acceleration and braking use separate configurable thresholds because braking is likely to produce
different loose-item behavior. Crossing a threshold can wake:

- the whole affected car interior in the first implementation; or
- a directionally selected region in a later optimization.

Whole-car wake is simpler and safer but must remain bounded by the per-operation item budget.

### Derailment and violent rotation

Explicit derailment state is a high-confidence wake cause. The host should wake all glued items in
affected cars when:

- a bogie or car enters a confirmed derailed state;
- angular acceleration crosses the derailment threshold; or
- an authoritative train relocation policy explicitly requests physical disturbance.

Fixture relocation and ordinary network correction do not implicitly count as derailment.

### Train jolt and external impact

A material train collision may be detected through accepted velocity/acceleration discontinuity,
an existing authoritative damage/collision event, or both. The event should be correlated with the
motion history so a packet correction is not interpreted as a physical impact.

Normal coupling has no special wake rule. If coupling produces a genuinely large accepted jolt, it
passes through the same threshold as any other impact. Threshold tuning must avoid making routine
yard work continually empty locomotive cabs.

## Wake-set construction

The host constructs one wake set per operation:

1. validate direct seeds;
2. add seeds to a queue ordered by canonical NetId;
3. query validated witness edges or canonical bounds for supported neighbours;
4. add unseen eligible neighbours;
5. stop at maximum depth, maximum item count, or operation time budget;
6. select one simulator for the resulting collision island;
7. atomically reserve new item epochs; and
8. publish one grant/batch.

Initial conservative budgets should be configuration constants and telemetry-visible. Suggested
starting points for live tuning are:

```text
maximum wake items per operation: 32
maximum support depth:             8
maximum candidate witness items:   64
same-source deduplication window:   250 ms
whole-car wake cooldown:            1 s
```

These are not protocol constants. Captures from the Train Item Lab should determine final values.

If a pile exceeds a budget, the host wakes the bounded near/contact subset and emits a truncation
event. It must not silently wake an arbitrary unordered subset.

## Settlement and re-gluing

Woken items use the current train-relative spatial lifecycle. They re-glue only after:

- linear motion is low;
- the train-local pose remains inside the configured position/rotation envelope for its dwell; or
- the body reliably sleeps;
- the host accepts a settlement proposal; and
- the canonical commit succeeds for the current authority revision and epoch.

After commit:

- the host stores the train-local pose;
- every projection applies the full DV train parent;
- velocity is zeroed;
- the body becomes kinematic; and
- the wake manager re-registers it in the car's glued-item set.

Wake hysteresis and settlement dwell must be longer than the noise that triggered the original
problem. No wake may immediately re-glue in the same tick.

## Interest, streaming, and unloaded representations

Glued items inherit interest from their train and occupants. Active wake leases additionally force
interest for:

- the selected simulator;
- existing observers of the affected car/train; and
- peers required to acknowledge a logical transition.

The host stores canonical train-local poses and wake/lease metadata even when its Unity projection
is absent. A newly interested peer receives:

- the committed glued pose when settled;
- the latest accepted pose and active lease metadata when moving; or
- the pending-wake baseline if no simulator has yet been selected.

Host code must not call `GetComponent`, inspect a host collider, or resolve `TrainCar.interior` as a
precondition for canonical validation. Those operations belong only to peers materializing a
presentation.

## Failure and recovery

### Simulator disconnect or timeout

The host freezes acceptance for the old epoch, chooses a replacement simulator for the entire wake
island where possible, increments epochs, and transfers the latest accepted baselines. If no
replacement exists, it retains the latest safe canonical poses and records a pending recovery wake.

### Item leaves the train

An active item that validly exits the interior changes parent at an authoritative boundary:

```text
TrainInterior -> World
```

The host validates the proposed boundary and continues or replaces the spatial epoch under world
motion rules. It must not remain attached to a departing train by a stale glued record.

### Logical transition races

Pickup, inventory, container, destruction, Lost and Found, and authored reset revoke pending wake
and active spatial work before their logical commit. Later stale witnesses or samples are rejected
by authority revision and epoch.

### Invalid witness

Reject the operation, preserve the committed poses, increment abuse/diagnostic counters, and send a
bounded rejection reason. Repeated invalid reports can disable that peer as a physical witness
without disconnecting it from ordinary observation.

## Performance design

The production system must be event-driven.

### Allowed recurring work

- One compact motion-history sample per train car that currently contains glued items.
- Existing active spatial-lease sampling for dynamic items.
- Bounded expiry/cooldown processing for pending wake operations.

### Forbidden recurring work

- Scanning every networked item every `Update` or `FixedUpdate`.
- Maintaining a permanent all-contact graph for glued items.
- Running physics overlap queries for every glued item each frame.
- Giving every glued item a collision-reporting component that produces resting-contact traffic.
- Broadcasting per-item wake packets when one bounded batch suffices.

### Indexes

Maintain:

```text
trainCarNetId -> glued item NetIds
itemNetId     -> canonical wake record
wakeKey       -> pending/deduplication record
trainCarNetId -> accepted motion history and cooldowns
```

Use pooled collections for transient candidate queues and wake sets. Clear them after each
operation; do not retain Unity collider references in canonical records.

### Network budgets

- Batch wake grants per train and operation.
- Send reliable control state only at wake/grant/reject/settle boundaries.
- Reuse sequenced transient spatial samples for motion.
- Rate-limit witnesses by sender, train, source item, and time window.
- Suppress duplicate collision callbacks from the same impacting epoch and target.
- Publish counts and truncated summaries instead of enormous diagnostic payloads.

## Security and validation

Even in a cooperative game, a malformed client must not wake arbitrary global items or acquire
unbounded simulation authority. The host validates:

- authenticated sender and readiness;
- train simulation/occupancy/witness eligibility;
- current item authority revisions;
- current train-parent identity;
- current settled or active-lease state;
- source lease epoch and sequence for impacts;
- canonical local-bounds proximity;
- finite positions, rotations, velocities, impulses, and time deltas;
- configured source thresholds;
- operation item/depth/rate budgets; and
- absence of a higher-precedence logical transition.

The host computes final wake membership. A client-provided candidate list is never accepted as an
opaque authoritative set.

## Observability

Required production/debug events include:

```text
train-item.wake-witness-received
train-item.wake-witness-rejected
train-item.wake-requested
train-item.wake-approved
train-item.wake-truncated
train-item.wake-pending-simulator
train-item.wake-grant-applied
train-item.wake-lease-transferred
train-item.wake-reconciled
train-item.glued
train-item.unglued
train-item.motion-threshold-crossed
train-item.support-candidates-observed
```

Each event should carry, where applicable:

- wake operation ID and reason;
- train car NetId;
- source/instigator item NetId;
- witness and simulator player IDs;
- item count, cascade depth, and truncation status;
- authority revisions and epochs;
- acceleration, braking, angular acceleration, jolt, relative velocity, or impulse;
- validation/rejection reason;
- host representation availability; and
- elapsed time from witness to grant and grant to final settlement.

Dashboard state should show per car:

- glued item count;
- active wake item count;
- pending wake count;
- current simulator;
- latest accepted motion measurements;
- wake cooldown;
- most recent reason; and
- rate-limit/truncation counters.

## Train Item Lab

The debug UI needs a reusable Train Item Lab built on the existing debug train fixture. The lab is
both a manual physics playground and the arrangement engine for automated scenarios.

### Fixture presets

```text
SingleItem
TwoItemStack
ThreeItemTower
FloorGrid
ClutteredCab
ProjectileLane
LargeSoakLoad
```

Every spawned item is registered before mutation with fixture/run ownership, prefab, item NetId,
train NetId, local pose, intended support group, and cleanup token.

### Manual controls

The debug menu should support:

- create/delete the lab train;
- choose car/livery, item prefab, count, and preset;
- spawn one item at a train-local pose;
- settle/glue all fixture items;
- remove or pick up a selected support item;
- launch a fixture item at a target or stack;
- choose launch direction and speed;
- apply gentle acceleration;
- apply heavy acceleration or braking;
- apply a controlled train jolt;
- request fixture derailment under explicit safety policy;
- wake a selected item or the whole fixture interior;
- display support candidates and the approved wake set;
- display per-item parent, glued/active state, epoch, simulator, and local pose; and
- run/prove cleanup.

Projectile launch should create or acquire the item through the normal authoritative item lifecycle,
open a real spatial epoch, and then apply the declared initial motion on its selected simulator. It
must not teleport an unregistered Rigidbody and call that a network test.

### Debug commands

Implemented commands:

```text
train.item-lab-status
train.item-lab-arrange
train.item-lab-wake
train.item-lab-throw-at
train.item-lab-wait-cycle
train.item-lab-index-status
train.item-lab-index-add-look
train.item-lab-index-add-nearby
train.item-lab-index-remove
train.item-lab-index-clear
train.item-lab-throw-from-view
```

These IDs match the dashboard runtime catalogue. A human can place items normally, add the item
under the reticle one at a time or index
all nearby settled items on one car, inspect the resulting NetIds/revisions, and clear stale or
unwanted entries. `train.item-lab-throw-from-view` moves a selected indexed projectile to the host
camera launch point as debug arrangement, then opens a real production spatial wake lease with
velocity along the live view direction. The motion, collision witnesses, host adjudication, and
settlement are therefore production paths.

The automated scenarios currently proving the implementation are:

- `external.train-item-wake-support-removal`;
- `external.train-item-wake-client-impact`; and
- `external.train-item-wake-manual-lab`.

All command code is `DEBUG`-only. Fixture scenarios retain explicit run identity and ledger-first
cleanup; a manually constructed lab index is session-local and automatically prunes missing item
representations.

## Automated scenario suite

Assertions should target authority and lifecycle invariants, not exact chaotic landing coordinates.

### Core contact scenarios

1. Two-box stack: removing the bottom wakes the top, which falls and re-glues.
2. Three-box tower: removing the bottom produces one bounded wake island with no duplicate leases.
3. Projectile: a thrown box wakes the struck settled box and any validated supported items.
4. Below-threshold impact: minor contact does not wake the target.
5. Unrelated nearby item remains glued during a support wake.
6. Repeated impact callbacks produce one wake operation.

### Train-motion scenarios

1. Constant high train speed does not wake settled items.
2. Gentle acceleration and braking remain below threshold.
3. Heavy acceleration wakes the expected car interior.
4. Heavy braking wakes the expected car interior.
5. Normal coupling does not wake items unless the general jolt threshold is crossed.
6. Controlled external impact produces one jolt wake.
7. Confirmed derailment wakes affected cars.
8. Network relocation/teleport does not masquerade as physical acceleration.

### Distant-host scenarios

1. Host is teleported beyond train/item interest while a client occupies its own fixture train.
2. Host has no loaded interior representation for the affected train.
3. Client removes support; host approves identities and wake set from canonical records.
4. Client launches an item; host validates the impact epoch and grants target wakes.
5. Client accelerates/brakes; host derives thresholds from accepted train motion.
6. A second observer enters interest during active motion and receives the current epoch.
7. A second observer enters after settlement and receives glued committed poses.
8. Simulator disconnects during a falling stack and the wake island transfers or enters explicit
   pending recovery.

### Performance and soak scenarios

1. Stock a train with increasing item counts and measure idle CPU, allocations, and packet rate.
2. Prove idle glued items produce no per-item recurring physics query or network traffic.
3. Wake a maximum-sized pile and prove item/depth budgets.
4. Repeated jolts respect cooldown and deduplication.
5. Repeated settle/wake cycles leak no leases, records, pooled collections, or fixture objects.
6. Cleanup proves zero fixture items, trains, pending wakes, and active epochs.

### Common assertions

```text
hostAuthoritativeWake
expectedWakeSet
unrelatedItemsRemainGlued
singleSimulatorPerItem
singleWakeOperation
cascadeBounded
correctTrainParent
activeEpochsCurrent
samplesAccepted
allExpectedItemsSettled
allExpectedItemsGlued
hostClientCanonicalAgreement
lateObserverAgreement
finiteState
cleanupClean
```

## Implementation phases

### Phase 0: document and instrumentation — completed

- finalize invariants and packet semantics;
- expose current glued/lease state by train;
- record accepted train-motion measurements; and
- add observability without changing wake behavior.

### Phase 1: central coordinator and registry — completed

- introduce `NetworkedTrainItemWakeManager`;
- register/unregister glued items from settlement and logical transitions;
- decouple wake validation from host Unity representations;
- implement one host-approved administrative wake; and
- prove direct wake and re-glue on host and distant client.

### Phase 2: Train Item Lab — completed

- implement deterministic item arrangements and ledger-first cleanup;
- add manual stock, launch, remove-support, wake, status, and cleanup controls;
- reuse the lab from dashboard external scenarios; and
- establish performance baselines.

### Phase 3: support removal — completed

- implement bounded witness overlap/sweep;
- validate candidates from canonical bounds;
- construct deterministic wake islands;
- add two-box, tower, unrelated-item, and distant-host tests.

### Phase 4: item impacts — completed

- observe collisions only on active leased items;
- add impact witness validation and deduplication;
- add projectile and below-threshold tests; and
- tune continuous collision policy.

### Phase 5: train motion and derailment — implemented; live tuning pending

- derive filtered acceleration/braking/angular acceleration/jolt from accepted train state;
- exclude relocation and correction discontinuities;
- implement whole-car wakes with cooldown;
- add derailment and coupling discrimination; and
- run distant-host and soak coverage.

### Phase 6: recovery and hardening — implemented; soak coverage pending

- transfer whole wake islands on simulator loss;
- support pending wakes without eligible simulators;
- tune budgets and thresholds from captures;
- add abuse/rate-limit handling; and
- complete persistence and late-interest audits.

## Acceptance criteria

The system is complete when:

1. A settled stack reacts correctly when support is removed.
2. A meaningful active-item impact wakes the struck train items.
3. Constant train speed and ordinary jitter do not wake glued items.
4. Heavy acceleration, braking, impact, and derailment wake items under documented policies.
5. The host can approve, validate, and commit every case while outside interest and without loaded
   physical representations.
6. Exactly one simulator runs each active item epoch.
7. Items re-settle, retain the correct train parent, and become kinematic again.
8. Late observers and persistence consume the latest committed canonical poses.
9. Idle cost scales with active cars, not all items, and active work obeys explicit budgets.
10. The Train Item Lab can arrange, disturb, inspect, and clean representative piles without manual
    save preparation.
11. Automated host/client/distant-host scenarios pass with clean teardown.
12. No invalid witness, stale epoch, disconnect, or interest transition can create duplicate
    authority or an unbounded wake cascade.

## Open tuning questions

- Final acceleration, braking, angular acceleration, jolt, impact, and coupling thresholds.
- Whether whole-car acceleration wakes should become directionally regional after profiling.
- Whether support removal needs client prediction to hide host round-trip delay.
- Which item types require continuous collision detection during active leases.
- How canonical prefab bounds should be generated, versioned, and compared across peers.
- Whether derailment should wake one car, a connected trainset region, or the whole trainset.
- How long a pending wake remains meaningful when no simulator exists.
- Whether very large piles should remain partially glued after budget truncation or enter a
  conservative whole-car recovery mode.

These values must be tuned through captures and the Train Item Lab. They must not be guessed into
the wire protocol or silently changed by individual clients.
