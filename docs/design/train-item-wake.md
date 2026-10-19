# Multiplayer Train Item Wake System Design

> Status: the host-side wake system is implemented: settled-item records, bounded support
> propagation, validated impact witnesses, car-motion/derailment wakes, simulator selection and
> pending retry. The Train Item Lab that originally exercised it was removed with the in-game
> runtime test system; no dvmp scenario covers wakes yet. Threshold tuning, large-pile soak testing
> and live derailment coverage remain open.

## Implementation status (audited 2026-10-14)

Checked against the code on `beta`. Where this differs from the design text below, this section
describes what the code does.

Implemented:

- **Coordinator:** `NetworkedTrainItemWakeManager`
  (`Multiplayer/Server/Items/NetworkedTrainItemWakeManager.cs`), host
  only, owned by the server's part of the item manager (`ServerItemManager`). Support propagation is the pure
  `TrainItemWakePlanner` (`Multiplayer.Core/Items/TrainItemWakePlanner.cs`), unit-tested in
  `Multiplayer.Core.Tests/Items/TrainItemWakePlannerTests.cs`.
- **Settled records:** registered when a spatial settlement commits in a train
  (`OnSpatialSettled`) or when the host registers a train-interior item with no open lease
  (`ObserveCanonicalItem`). Registration zeroes the host body's velocity and makes it kinematic.
- **Support removal:** when a settled item leaves `TrainInterior` placement or is removed, the host
  wakes the items resting on it, computed from its own settled records (same car, upward support
  only, 0.12 m vertical tolerance). There is no client overlap/sweep witness.
- **Item impact:** `NetworkedItem.OnCollisionEnter` (`NetworkedItem.TrainWake.cs`) reports a
  collision between an actively leased train item and a settled one on the same car, from the
  source's current simulator only (`NetworkedItemManager.ObserveTrainItemCollision`), as
  `ServerboundItemTrainWakeWitnessPacket` (reliable; the host hands its own to the manager directly). The host
  validates it and wakes the target plus the items it supports.
- **Car motion:** the host samples `TrainCar.rb` every 0.1 s for cars that have settled items and
  wakes the whole car on derailment, violent rotation, a jolt, or heavy acceleration/braking, with a
  1 s cooldown. Position jumps and long gaps reset the baseline instead of counting as motion.
  A car that no longer exists (deleted with items settled in it; DV pools a deleted car with
  whatever is parented to it, so the items are never removed) drops its settled records the first
  time it is sampled.
- **Wake grant:** reuses the spatial lease protocol. `ServerItemSpatial.TryBeginTrainWake`
  opens a new epoch with reason `train-wake:<reason>` and an initial train-local velocity; one
  simulator is chosen for the first item and preferred for the rest of the set.
- **Pending wakes:** if no simulator is eligible or the host has no `NetworkedItem` for the item,
  the wake is retried every 0.5 s and dropped after 10 s (`train-item.wake-expired`); the item
  stays glued at its committed pose.

Not done or different from the design:

- **The Train Item Lab, its `train.item-lab-*` debug commands and the `external.train-item-wake-*`
  scenarios no longer exist.** The in-game runtime test system was removed in f510e8e in favour
  of the dvmp harness ([`../testing.md`](../testing.md),
  [`../../DevTools/README.md`](../../DevTools/README.md)). `DevTools/Scenarios/PORTING.md` lists
  the cab item wake scenario as not yet ported; the `train-items` scenario only checks that a
  dropped or thrown item stays in a moving cab.
- **Truncation:** car-motion wakes take up to 32 items from an unordered set, silently. There is
  no `wake-truncated` event.
- **Bounds** come from the host's colliders at settlement time, falling back to a 0.35 m cube when
  the host has no usable colliders. There is no prefab-bounds catalogue.
- **Recovery:** simulator disconnect or a 3 s sample timeout transfers each item's lease
  separately (`TryTransferLease`), or commits the last accepted pose if nobody can take it. There
  is no island transfer and no pending recovery wake.
- **Abuse handling:** invalid witnesses produce a `train-item.wake-witness-rejected` event only.
  Nothing is sent back to the client, and there are no per-peer counters or witness disabling.
- **Not built:** continuous collision detection for active items, directional (regional) wakes,
  client-predicted ungluing, a per-car dashboard view.
- The lab's hooks (`ForceTrainItemWake`, `TrainItemWakeSnapshot`, `DebugArrangeSettledTrainItem`
  and the `DebugRequest` wake reason) were removed in #59; they are in git history if a dvmp wake
  scenario wants them back.

## Purpose

Define how loose items resting inside a moving train become temporarily active again after a
physical disturbance, while preserving host authority when the host is far away and cannot locally
simulate or even materialize the train interior.

This design extends, rather than replaces:

- [`item-spatial-physics.md`](item-spatial-physics.md),
  which owns simulation leases, transient samples, and reliable settlement;
- [`item-lifecycle.md`](item-lifecycle.md), which owns
  logical placement and transition precedence;
- [`item-world-sync.md`](item-world-sync.md), which owns
  identity, interest, and canonical projections.

Two-game testing uses the dvmp harness described in [`../testing.md`](../testing.md); its train
helpers (spawn near the players, board, push, delete) live in `DevTools/Dvmp/Probes/lib`.

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
- runtime test scenarios.

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
15. Test scenarios remove the trains and items they create and leak no leases, NetIds, or pending
    wakes.

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
TrainJolt
ViolentRotation
Derailment
```

These are the values of `TrainItemWakeReason`. The design's `Administrative` and `Recovery`
sources were not built.

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
Multiplayer/Server/Items/NetworkedTrainItemWakeManager.cs
```

This manager owns train-specific wake policy and must not be folded into
`NetworkedItemSpatialManager`. It maintains:

- settled item membership by item and by train car NetId;
- train-local pose and local collision bounds for each glued item;
- witness deduplication keys and the last witness ID per player;
- per-car motion history and wake cooldowns;
- pending wakes waiting for a simulator; and
- debug state for the item inspector (`AppendDebugState`).

It is driven by notifications from the server's item manager (`ServerItemManager`) plus one host
`Update` (pending retry and motion sampling):

```text
ObserveCanonicalItem       host registered a train-interior item with no open lease
OnSpatialSettled           spatial settlement committed
OnAuthoritativeTransition  placement changed (leaving TrainInterior wakes supported items)
OnItemRemoved              item destroyed (wakes supported items)
ReceiveImpactWitness       a simulator's witness: a client's packet, or the host's own collision
                           (NetworkedItemManager.ObserveTrainItemCollision)
```

The design's separate derailment, simulator-disconnect and interest notifications were not
built: derailment is read during motion sampling, and disconnect/timeout recovery is the spatial
manager's ordinary lease transfer. The only call into the spatial manager's host side
(`ServerItemSpatial`) is `TryBeginTrainWake`, which selects the simulator and opens the lease.

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

Host wake records must survive missing Unity representations. The implemented record
(`SettledRecord`) is:

```csharp
private sealed class SettledRecord
{
    public ushort ItemNetId;
    public uint AuthorityRevision;
    public ushort TrainCarNetId;
    public Vector3 ParentLocalPosition;
    public Quaternion ParentLocalRotation;
    public Bounds ParentLocalBounds;
    public uint SpatialEpoch;
    public float SettledAt;
}
```

The design called for bounds from a stable prefab-bounds catalogue. The code captures them from
the host's colliders when the item settles, transformed into the car's interior space, and falls
back to a 0.35 m cube when there are none. A remote witness never supplies bounds.

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
authority protocol. The witness includes a per-sender witness ID, source revision and epoch,
target revision, car identity, network tick, relative velocity, impulse, and absolute contact
position. Coordinator events record the reason, selected simulator, wake count and rejection
reason without duplicating those fields into a second grant format; truncation is not recorded.

## Host authority with a distant client

The host must be able to adjudicate a wake when:

- the host player is outside item interest;
- the host train interior is unloaded;
- the host item projection is inactive or retired;
- the nearby client is driving a separate train; and
- only that client has colliders capable of observing the event.

The host therefore operates on canonical records and accepted network state. Physical application
is optional presentation work.

In the code this holds only in part. Witness validation uses canonical records (and the host's
car transform, which always exists), but `TryBeginTrainWake` needs a host `NetworkedItem` for the
item, the host can be the simulator only while that item is active in its scene, and settlement
bounds come from host colliders. A wake the host cannot start for lack of a representation goes
to the pending list.

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

1. observes contact evidence that the host cannot observe (support is judged by the host from
   its settled records);
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
impossible to diagnose. The code retries every 0.5 s, expires after 10 s, and publishes
`train-item.wake-pending` and `train-item.wake-expired`.

## Simulator selection

The design proposed this order:

1. the peer currently simulating the train;
2. a player occupying the affected car/trainset;
3. the current simulator of the impacting item;
4. the nearest interested ready client with the affected interior loaded;
5. the host if it has a valid active representation; or
6. no simulator, producing a pending wake.

There is no "peer simulating the train" (the host simulates every train). The implemented
`TrySelectTrainWakeSimulator` considers players that are `ReadyForItems` and either on the car or
within 192 m of the item; a client must know and have acknowledged the item, and the host must
have it active. Among those it prefers the requested player (the impact witness, or the player
who took the supporting item), then a player on the car, then the nearest.

Selection is performed once per wake operation where possible. A stack should normally share one
simulator instead of distributing mutually colliding items across peers. The code passes the
first item's simulator as the preferred player for the rest of the set.

The selected peer receives no logical ownership. Its authority is restricted to the named item
epochs and motion envelope.

## Wake causes

### Support removal

When a glued item is about to be picked up, moved, stored, destroyed, or reparented, nearby glued
items may lose support.

The design had the locally observing peer perform an overlap or short upward sweep and report
candidates. The implementation needs no client evidence: when a settled item's placement leaves
`TrainInterior` (pickup, inventory, container, Lost and Found) or the item is destroyed, the host
runs `TrainItemWakePlanner.FromRemovedSupport` over its own settled records.

Implemented policy:

- only settled items on the same train car are considered;
- candidates must overlap the support horizontally by at least 0.01 m on both axes;
- their lower bound must be within 0.12 m of the support's upper bound;
- support propagates upward only, so side-by-side clutter stays glued;
- the search stops at 32 items or depth 8; and
- unrelated floor items remain glued.

The interaction is not blocked on a round trip. A top item may appear unsupported but glued for
one network delay until the grant arrives. Client-side predicted ungluing has not been built.

### Item impact

Only active dynamic items need impact observation. When an actively leased item collides with a
glued train item, its simulator reports:

- impacting and target item NetIds;
- the source's authority revision and lease epoch, and the target's authority revision;
- train car NetId;
- the first contact point, in absolute (origin-shift-free) world space;
- relative velocity;
- impulse; and
- source network tick.

The design also listed the lease sequence and a train-local contact normal; neither is sent. The
peer only reports when relative speed is at least 1.25 m/s or impulse at least 0.5.

The host rejects the witness unless: the witness ID is newer than the sender's last accepted one;
the vectors are finite and plausible (relative speed up to 50 m/s, impulse up to 100); the impact
crosses the same thresholds; the sender is the source's current simulator at the stated revision
and epoch; both items are on the named car (the target as a settled record at the stated
revision); and the contact point is within 4 m of the target's settled position.

The target then becomes a wake seed. Items it supports join through the bounded support search.
Repeated callbacks for the same sender, source and target within 250 ms are dropped. The woken
items start with 35% of the impact's relative velocity, in train-local space, capped at 4 m/s.

Fast active items should use continuous collision detection or an equivalent swept test while
their lease is in flight, subject to item type and performance testing. This is not built.

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

The implementation samples the host car's rigidbody (the host simulates every train) every 0.1 s,
only for cars that hold settled items, in world space without further filtering. A sample gap over
0.75 s, or a position jump beyond what the two speeds allow, resets the baseline
(`train-item.motion-baseline-reset`) instead of counting as motion.

Acceleration and braking use separate thresholds because braking is likely to produce different
loose-item behavior: 3.5 m/s² for acceleration and 3 m/s² of deceleration along the direction of
travel above 1 m/s. Crossing a threshold can wake:

- the whole affected car interior in the first implementation; or
- a directionally selected region in a later optimization (not built).

Whole-car wake is simpler and safer but must remain bounded by the per-operation item budget. The
woken items get a small train-local kick opposite the acceleration (5% of it, capped at 1.5 m/s).

### Derailment and violent rotation

Explicit derailment state is a high-confidence wake cause. The host should wake all glued items in
affected cars when:

- a bogie or car enters a confirmed derailed state;
- angular acceleration crosses the derailment threshold; or
- an authoritative train relocation policy explicitly requests physical disturbance.

The code wakes the car when any bogie's `HasDerailed` turns on (`Derailment`) or angular
acceleration reaches 1.25 rad/s² (`ViolentRotation`). There is no relocation-requested wake.
Relocation and network correction do not count as derailment.

### Train jolt and external impact

A material train collision may be detected through accepted velocity/acceleration discontinuity,
an existing authoritative damage/collision event, or both. The event should be correlated with the
motion history so a packet correction is not interpreted as a physical impact. The code uses the
motion samples only: a jolt (change in acceleration) of at least 14 m/s³ while acceleration is at
least 1.5 m/s² is a `TrainJolt`.

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

The code does steps 1–5 in `TrainItemWakePlanner` (without a time budget) and step 6 by reusing
the first item's simulator. Epochs are then reserved and granted one item at a time: each woken
item gets its own `ClientboundItemSpatialLeasePacket`, not one batch.

Initial conservative budgets should be configuration constants and telemetry-visible. The code
uses these constants in `NetworkedTrainItemWakeManager`:

```text
maximum wake items per operation: 32
maximum support depth:             8
same-source deduplication window:   250 ms
whole-car wake cooldown:            1 s
pending wake lifetime:              10 s
```

The design's 64-item witness candidate cap does not apply: witnesses carry one target. These are
not protocol constants. Captures from dvmp scenarios should determine final values.

If a pile exceeds a budget, the host wakes the bounded near/contact subset and emits a truncation
event. It must not silently wake an arbitrary unordered subset. Support and impact wakes are
bounded in planner order (lowest first, then NetId), but no truncation event is emitted, and
whole-car motion wakes take the first 32 items of an unordered set.

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

The first two are implemented: the spatial manager sends open leases to a peer when items enter
its interest (reason `interest-entry`). Pending wakes are host-only state; a new observer sees
the glued committed pose.

Host code must not call `GetComponent`, inspect a host collider, or resolve `TrainCar.interior` as a
precondition for canonical validation. Those operations belong only to peers materializing a
presentation. Validation follows this, but settlement bounds and wake grants still use the host
representation (see "Host authority with a distant client").

## Failure and recovery

### Simulator disconnect or timeout

The host freezes acceptance for the old epoch, chooses a replacement simulator for the entire wake
island where possible, increments epochs, and transfers the latest accepted baselines. If no
replacement exists, it retains the latest safe canonical poses and records a pending recovery wake.

Implemented instead: the spatial manager's generic recovery. On disconnect, or when a simulator
sends no accepted sample for 3 s, each item's lease is transferred separately to the nearest
eligible peer within 192 m (`TryTransferLease`, reason `lease-transfer:*`). If there is none, the
host commits the last accepted pose as settled. Wake islands are not kept together and no pending
recovery wake is recorded.

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

The code rejects the witness, keeps the committed poses and publishes
`train-item.wake-witness-rejected` with the reason on the host. It sends nothing back, keeps no
counters, and never disables a witness.

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

The code keeps the first, second and fourth indexes; pending wakes are keyed by item and witness
deduplication by `sender:source:target`. Wake sets are built with LINQ and arrays rather than
pooled collections. No collider references are retained.

### Network budgets

- Batch wake grants per train and operation.
- Send reliable control state only at wake/grant/reject/settle boundaries.
- Reuse sequenced transient spatial samples for motion.
- Rate-limit witnesses by sender, train, source item, and time window.
- Suppress duplicate collision callbacks from the same impacting epoch and target.
- Publish counts and truncated summaries instead of enormous diagnostic payloads.

Not met in the code: grants are one lease packet per item, and witness limiting is the 250 ms
`sender:source:target` deduplication plus increasing witness IDs, with no per-train or per-sender
rate limit.

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
opaque authoritative set. (Clients never send one: a witness names one target.)

The impact witness checks are listed under "Item impact". Not checked: sender loading state, lease
sample sequence, time deltas, and rate budgets. Proximity is a 4 m radius around
the target's settled position rather than a bounds test.

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

The implemented events are published on the host under the `train-item-wake` debug category (see
[`../observability.md`](../observability.md)):

```text
train-item.settlement-registered
train-item.support-removed
train-item.wake-witness-accepted
train-item.wake-witness-rejected
train-item.wake-granted
train-item.wake-pending
train-item.wake-expired
train-item.car-motion-wake
train-item.motion-baseline-reset
```

The spatial manager adds `item.spatial-train-wake-granted` and `item.spatial-lease-transferred`.
They carry the car and item NetIds, reason, simulator, wake count, motion vectors and rejection
reason where relevant; there are no operation IDs, cascade depth, truncation status or timings.

Dashboard state should show per car (not built; the combined dashboard was removed in f510e8e and
the in-game inspector shows only per-item wake state):

- glued item count;
- active wake item count;
- pending wake count;
- current simulator;
- latest accepted motion measurements;
- wake cooldown;
- most recent reason; and
- rate-limit/truncation counters.

## Train Item Lab (removed)

The first implementation was exercised by a Train Item Lab: in-game debug commands
(`train.item-lab-*`) built on the old debug train fixture that arranged stacks and piles in a
fixture train, indexed items, threw a projectile from the host camera through a real wake lease,
and ran the `external.train-item-wake-*` dashboard scenarios. All of it was removed with the
in-game runtime test system in f510e8e. Only the uncalled host hooks listed under "Implementation
status" remain.

Its replacement is a dvmp scenario (`DevTools/Scenarios/<name>/run.sh`, see
[`../testing.md`](../testing.md) and [`../../DevTools/README.md`](../../DevTools/README.md)),
listed as not yet ported in `DevTools/Scenarios/PORTING.md`. The rules that still apply to it:
spawn the train and items through the normal authoritative paths (the probe library's `Trains`
helpers spawn, board, push and delete cars), make a projectile move through a real spatial lease
rather than by teleporting an unregistered rigidbody, and remove everything the scenario created.

## Automated scenario suite

This is the target suite for the dvmp port. None of it is automated today: the old dashboard
scenarios were removed, `train-items` covers only dropping and throwing an item into a cab, and
the planner's support propagation and budgets are covered by `TrainItemWakePlannerTests`.

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

1. Host is teleported beyond train/item interest while a client occupies its own train.
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
5. Repeated settle/wake cycles leak no leases, records, or test objects.
6. Cleanup proves zero test items, trains, pending wakes, and active epochs.

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
- decouple wake validation from host Unity representations (partial: grants and bounds still use
  the host representation);
- implement one host-approved administrative wake (`ForceWake`, removed in #59 with the other
  lab hooks); and
- prove direct wake and re-glue on host and distant client.

### Phase 2: Train Item Lab — completed, then removed

- implement deterministic item arrangements and ledger-first cleanup;
- add manual stock, launch, remove-support, wake, status, and cleanup controls;
- reuse the lab from dashboard external scenarios; and
- establish performance baselines.

Removed in f510e8e with the in-game runtime test system. A dvmp replacement is still to be
written.

### Phase 3: support removal — implemented; runtime test removed

- implement bounded witness overlap/sweep (replaced by a host-side search over settled records);
- validate candidates from canonical bounds;
- construct deterministic wake islands;
- add two-box, tower, unrelated-item, and distant-host tests (planner unit tests remain; the
  runtime scenarios were removed).

### Phase 4: item impacts — implemented; runtime test removed

- observe collisions only on active leased items;
- add impact witness validation and deduplication;
- add projectile and below-threshold tests (removed with the lab); and
- tune continuous collision policy (not done).

### Phase 5: train motion and derailment — implemented; live tuning pending

- derive filtered acceleration/braking/angular acceleration/jolt from accepted train state
  (unfiltered 10 Hz host samples);
- exclude relocation and correction discontinuities;
- implement whole-car wakes with cooldown;
- add derailment and coupling discrimination (coupling has no special case); and
- run distant-host and soak coverage (not done).

### Phase 6: recovery and hardening — partly implemented

- transfer whole wake islands on simulator loss (not done: per-item lease transfer);
- support pending wakes without eligible simulators (done: 0.5 s retry, 10 s expiry);
- tune budgets and thresholds from captures (not done);
- add abuse/rate-limit handling (deduplication and witness ordering only); and
- complete persistence and late-interest audits (not done).

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
10. dvmp scenarios can arrange, disturb, inspect, and clean representative piles without manual
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

These values must be tuned through captures and dvmp scenarios. They must not be guessed into
the wire protocol or silently changed by individual clients.
