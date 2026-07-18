# Multiplayer Item Spatial and Physics Synchronization Design

> Status: implemented through the transient simulation, reliable settlement, lease-transfer, and
> persistence-checkpoint milestones described below. Advanced physical interaction authority and
> runaway/glitched-item recovery remain deliberately deferred.

## Purpose

Define how a host-authoritative item can move through the world after it leaves an inventory or
hand. This is deliberately separate from
[`MULTIPLAYER_ITEM_WORLD_SYNC_DESIGN.md`](MULTIPLAYER_ITEM_WORLD_SYNC_DESIGN.md): that design owns
identity, lifecycle, placement, interest, and persistence, while this design owns transient spatial
motion and the reliable commit of the resulting resting pose.
The cross-feature states and precedence rules consumed by both designs are canonicalized in
[`MULTIPLAYER_ITEM_LIFECYCLE_DESIGN.md`](MULTIPLAYER_ITEM_LIFECYCLE_DESIGN.md).

The immediate bug motivating this work is:

1. A client enters a remote area and receives the correct host-authored item projections.
2. The client picks up and throws an item.
3. The throw and later pickup refer to the correct item identity.
4. The host never learns the item's post-throw resting pose.
5. When the host later enters the area, its projection is still at the original position.

This proves that logical identity synchronization is working while spatial authority is incomplete.

## Current behavior and exact failure

`NetworkedItem.OnThrow` captures:

- absolute release position;
- release rotation; and
- throw direction.

It then marks the logical item state dirty. `GetSnapshot` emits a one-shot `Thrown` state and
deliberately changes the observation baseline to `Dropped`, preventing the next frame from sending a
second transition that would cancel momentum.

That behavior is correct for the transition but incomplete for motion. After the transition:

- Rigidbody movement does not mark the item dirty;
- no periodic position snapshot is produced;
- no sleeping/settled callback commits the final pose;
- `AuthoritativeItemRegistry.Record.Position` remains the release position;
- the host cell index continues to derive from the host Unity transform; and
- a later projection baseline therefore contains the old transform.

The item appears in the client's hand on a later pickup because the pickup sends a new logical
placement transition for the same NetId. The apparent teleport is reconciliation to the correct
identity, not evidence that the host knew the floor position.

## Required outcome

For every materialized item, the host must eventually know a canonical spatial state even when the
host is nowhere near the item and is not locally simulating its Rigidbody.

At minimum, after a client throws or drops an item:

- nearby observers see coherent movement;
- the host accepts a bounded stream of spatial samples from exactly one simulator;
- the item is assigned to the correct world cell as it moves;
- a reliable final resting pose is committed;
- persistence uses that committed pose;
- leaving and re-entering interest reprojects the committed pose; and
- pickup, Lost and Found, containers, saving, and ownership transitions supersede any old motion.

## Separation of concerns

Three different concepts must not share one revision counter or one authority flag.

### Logical authority

The host exclusively owns:

- persistent identity;
- placement (`World`, `PlayerHand`, `PlayerInventory`, `Container`, and so on);
- holder and persistent owner;
- lifecycle and tombstones;
- durable item state; and
- the committed world pose used by interest and persistence.

This remains the existing `AuthoritativeItemRegistry` boundary.

### Simulation authority

One runtime participant temporarily advances a moving Rigidbody. This participant is the
**simulation lease holder**. Holding a simulation lease does not grant permission to change logical
placement, ownership, item state, container membership, or lifecycle.

### Presentation

Every other materialized copy is a non-authoritative proxy. It interpolates received samples and
must not produce collisions that can alter canonical state.

The governing invariant is:

> The host owns truth. One selected machine may calculate motion. Every other Unity object is a
> presentation of the host-accepted spatial stream.

## Why the host cannot always be the physical simulator

The host should simulate an item when it has the relevant scene/terrain region and an active
projection. However, a remote player can interact with an area that is outside the host player's
native item and terrain interest.

Forcing the host to keep every remote Rigidbody and collision environment active would defeat the
world streamer, increase CPU cost with player count, and may be impossible when the required terrain
presentation is not loaded around the host's floating origin.

The practical model is therefore host-controlled delegated simulation:

- the host is always the logical authority;
- the host is the preferred simulator when able;
- otherwise the interacting/nearby client receives a narrow, revocable simulation lease; and
- the host validates, relays, and commits that client's samples.

This is not general client authority. It is comparable to accepting bounded player movement input:
the client supplies observations from a simulation the host cannot currently run, while the host
controls who may submit them and what state may be committed.

## Spatial state model

Add a transient spatial state beside the durable authority record:

```text
ItemSpatialState
  itemNetId
  simulationEpoch
  sampleSequence
  simulatorPlayerId       // 0 means host
  phase                    // Settled, Held, InFlight, Sliding, Attached
  absolutePosition
  rotation
  linearVelocity
  angularVelocity
  worldParentKind
  worldParentNetId
  worldParentKey
  parentLocalPosition
  parentLocalRotation
  lastAcceptedServerTick
  lastCommittedPose
```

`simulationEpoch` changes whenever the host grants, revokes, or transfers the lease. A sample from an
old epoch is rejected even if its sequence number is large.

`sampleSequence` orders transient movement within one epoch. It is independent of the durable
authority revision.

## Revisions and reliability

Do not increment `AuthorityRevision` for every physics sample. At 10-20 samples per second that would
turn a durable concurrency token into a packet counter and make ordinary item transitions stale
constantly.

Use two channels of ordering:

```text
authorityRevision
  reliable logical transitions and committed placement

simulationEpoch + sampleSequence
  transient, sequenced spatial motion
```

A reliable spatial commit increments the authority revision when it changes the canonical pose,
parent, or cell. Intermediate motion samples do not.

## Lifecycle

### Pickup

1. Client requests pickup through the real game interaction path.
2. Host validates projection acknowledgement, range, current authority revision, and placement.
3. Host commits `World -> PlayerHand`.
4. Host terminates any spatial epoch for the item.
5. Old in-flight samples are rejected by epoch/placement mismatch.

While held, the item inherits the player's existing movement replication. It does not require an
independent world-physics stream.

### Drop without impulse

1. Holder sends a logical drop intent containing the release pose and current parent candidate.
2. Host validates and commits `PlayerHand -> World`.
3. Host selects a simulator and opens a new spatial epoch.
4. If the Rigidbody is already stable, the simulator immediately sends a reliable settlement.
5. Host commits the final pose and closes the epoch.

### Throw

1. Holder sends the existing `Thrown` transition plus release pose, rotation, and initial linear and
   angular velocity. Direction alone is not sufficient for exact continuation across different
   masses, frame timing, or item implementations.
2. Host validates the release envelope and commits `PlayerHand -> World/InFlight`.
3. The throwing peer normally retains the simulation lease for the flight.
4. It sends sequenced motion samples while the body moves.
5. Host relays accepted samples to interested observers.
6. When the Rigidbody sleeps or remains below settlement thresholds for a dwell period, the
   simulator sends a reliable settlement proposal.
7. Host validates and commits the final pose, rotation, parent, and cell, then closes the epoch.

### Lease timeout

If samples stop before settlement:

- freeze remote proxies at the last host-accepted pose;
- revoke the epoch;
- prefer a host takeover when the region is available;
- otherwise grant a new lease to an eligible nearby client; and
- if no simulator exists, commit the last accepted pose as a recoverable stopped state and emit an
  invariant warning.

The item must never snap back to its pre-throw catalogue baseline merely because the simulator left.

### Deferred research: runaway or never-sleeping motion

Items can jitter inside colliders, inherit bad velocities, or otherwise remain physically active
indefinitely. `Rigidbody.IsSleeping()` may therefore be insufficient as the only settlement signal.

The correct recovery behavior is intentionally **not decided in this design**. Automatically freezing,
moving, depenetrating, destroying, or sending an item to Lost and Found could conceal a real game
state or introduce worse duplication and placement bugs. This needs targeted Derail Valley research
and runtime observation before an implementation policy is selected.

The initial spatial implementation should make this state observable and bounded at the networking
layer without pretending to resolve it. Record how long the body has remained active, its velocities,
sample rate, parent, recent poses, and whether Unity ever reports it sleeping. Any packet-rate safety
cap must preserve the latest accepted state and emit an explicit unresolved-runaway warning.

## Simulator selection

The host selects in this order:

1. host, when its projection and collision region are active;
2. current holder for a release/throw;
3. closest ready client that already acknowledges the projection and subscribes to the cell;
4. no simulator, using the last committed pose.

Only one lease may exist. Lease transfer increments `simulationEpoch` and includes an authoritative
baseline containing pose and velocities.

Eligibility requires:

- matching protocol and catalogue;
- loaded scene and terrain region;
- projection acknowledgement;
- player proximity to the item;
- item not in a cold/non-world placement; and
- no conflicting logical transition in progress.

## Wire protocol

Use dedicated packets rather than overloading `ItemUpdateData.ItemPosition`. The current item packet
combines logical transition semantics, tracked state, revision reservation, and reliable correction.
High-frequency spatial samples have different ordering and delivery requirements.

### Lease grant (reliable, host to simulator)

```text
itemNetId
authorityRevision
simulationEpoch
absolutePosition
rotation
linearVelocity
angularVelocity
parent anchor
serverTick
```

### Motion sample (sequenced/unreliable, simulator to host)

```text
itemNetId
simulationEpoch
sampleSequence
clientTick
absolutePosition
compressed rotation
linearVelocity
angularVelocity
sleeping flag
```

### Relayed sample (sequenced/unreliable, host to observers)

The accepted sample plus a host tick. Persistent UUIDs are not sent; the session NetId is sufficient.

### Settlement proposal (reliable, simulator to host)

The final sample, parent candidate, contact/sleep duration, and last sequence number.

### Spatial commit (reliable, host to observers)

```text
itemNetId
newAuthorityRevision
simulationEpoch
committed absolute/local pose
committed parent anchor
committed cell
```

This packet is the durable result. It is also the baseline used for a player entering interest later.

## Sampling policy

Start conservatively:

- 15 Hz while airborne or moving quickly;
- 8 Hz while rolling/sliding slowly;
- immediate sample after a major collision or parent change;
- reliable settlement after sleep or a low-motion dwell period;
- no samples while settled, held, cold, contained, or outside all observer interest.

Send only when transform/velocity deltas exceed thresholds, with a maximum silence interval while in
motion. Packet rates can be tuned from captures after correctness is established.

For multiple moving items, batch samples per recipient and per network tick. Do not create one
reliable packet stream per Rigidbody.

## Host validation

The host cannot reproduce every remote collision, but it can reject impossible or unauthorized
motion.

Validate every sample against:

- authenticated sender equals current lease holder;
- matching simulation epoch;
- monotonically newer sequence;
- current logical placement is a simulated world placement;
- finite position, rotation, and velocities;
- elapsed-time displacement envelope;
- configurable maximum linear/angular speed;
- configurable acceleration/teleport envelope;
- item remains reasonably close to the simulator player;
- parent anchors resolve and are close enough;
- cell transitions are contiguous with the accepted trajectory; and
- no newer pickup, container, Lost and Found, destruction, or snap transition exists.

For train-anchored samples, displacement and velocity checks operate in parent-local space and also
validate the anchor's current train identity. The train's own world movement is not charged against
the item's motion envelope.

Suspicious samples should revoke the lease and reconcile to the last accepted state. They must not
silently mutate ownership or persistence.

## Observer presentation

Non-simulating peers should not run an independent authoritative Rigidbody trajectory. Otherwise
small collision and frame-time differences will diverge immediately.

For ordinary observers:

- keep the body kinematic or otherwise prevent it from driving canonical contacts;
- buffer a small number of accepted samples;
- interpolate position and rotation at a short delay;
- extrapolate briefly using velocities when one sample is late;
- snap only beyond a generous error threshold or on a reliable spatial commit; and
- restore normal local interaction physics only after the host grants pickup or simulation authority.

Visual collision effects may still run locally, but they cannot produce outgoing item movement.

## Host representation and canonical records

A remote item may be inactive on the host. Host acceptance must therefore update canonical data
without depending on the host Rigidbody or active GameObject.

The host should:

- update `ItemSpatialState` for every accepted sample;
- update the authoritative record's committed pose only on reliable commit (plus a recoverable
  latest-accepted pose for crash diagnostics);
- update cell membership from canonical spatial data, not only `item.transform.position`;
- optionally move an inactive host projection to the accepted/committed pose without activating it;
- never require remote terrain to be loaded merely to remember a transform; and
- use the committed record when constructing a projection for a newly interested player.

This is an important change from the current `RefreshHostWorldItem`, which derives cell membership
from the host Unity transform.

## Floating origin and anchors

All wire and canonical world positions are absolute:

```csharp
absolute = unityPosition - WorldMover.currentMove;
unity = absolute + WorldMover.currentMove;
```

Train interiors and stable static parents use local pose plus their existing stable anchor identity.
An item moving with a train should not emit world-space samples merely because its absolute position
changes with the parent. Transitioning between loose world motion and an anchor is a reliable spatial
commit.

## Loose items on trains

Derail Valley already has the necessary distinction. `ItemReparentingBase` reparents loose items to a
train interior, and current item snapshots detect a `TrainCar` parent and encode:

- `ItemWorldParentKind.TrainInterior`;
- the train car's session NetId;
- position relative to `trainCar.interior`; and
- rotation relative to that interior.

Vanilla persistence makes the same distinction through `TrainCar.Resolve` and `CarGUID`. The
multiplayer world-item design already treats `TrainInterior` as a durable placement anchor.

Physics synchronization should build on this instead of interpreting world-space velocity literally:

```text
World item
  pose and velocity are absolute/world-relative
  participates in 128 metre item cells

TrainInterior item
  pose is relative to the train interior
  motion thresholds use velocity relative to the train
  inherits interest from the train and nearby occupants
  does not change item cells merely because the train moves
```

A cup resting on a locomotive can therefore be spatially settled even while its absolute position is
changing rapidly. Its committed state is a stable local pose anchored to the train. Existing train
replication moves the parent; item replication does not resend that inherited movement.

If an item is still sliding, rolling, or bouncing inside the train, its simulator sends local-space
samples under a train-anchored spatial epoch. Settlement uses relative linear/angular motion, not the
train's world velocity. Never-settling train-local motion remains part of the deferred runaway-item
research rather than receiving a speculative recovery rule.

Train parenting must use Derail Valley's complete item-reparenting operation, not only
`Transform.SetParent`. Specifically, `ItemReparentingBase.ParentItemExternal` must receive
`trainCar.interior` and the train's canonical `trainCar.rb`. That call also configures
`CabItemRigidbody.SetupTrainReceivingForces(trainCar.rb)` and registers the item with
`TrainPhysicsLod`. Omitting the Rigidbody argument makes the item look parented while its physics
still behaves as world-relative; using `GetComponent<Rigidbody>()` on the car is not equivalent to
using DV's `TrainCar.rb` reference.

Captured train-local linear and angular velocities subtract `trainCar.rb` motion before conversion
to interior-local space. Applying a train-local state adds that same body motion exactly once. A
settled item inherits movement through its train parent and must not receive the train's world
velocity again through item replication.

Parent changes are authoritative boundaries:

1. DV reports/reparents the item beneath a train interior.
2. The simulator proposes the train anchor and local pose.
3. The host validates that the car exists, the player and item are nearby, and the local pose is
   plausible.
4. The host reliably commits `World -> TrainInterior` and opens or settles a train-local epoch.
5. If the item leaves the train, the host commits `TrainInterior -> World` using the derived absolute
   pose and begins an ordinary world epoch.

An item merely touching the exterior must not be permanently anchored based on one collision packet.
The host follows DV's actual resolved parent and requires a stable/plausible train relationship. A
snapped coupler item and an installed gadget remain their existing explicit placement types; they are
not downgraded to loose `TrainInterior` items.

## Interaction with existing systems

### Interest streaming

Transient samples go only to recipients currently interested in the item. Cell membership follows
the latest host-accepted position while moving so a trajectory can cross a cell boundary. A player
entering interest receives a reliable baseline at the latest accepted or committed pose, never the
authored default.

### Persistence

Normal saves use the last committed pose. Before a host shutdown/save boundary, moving items should
either:

- receive a short settlement request; or
- persist a valid transient checkpoint containing pose and velocities.

Loading a transient checkpoint can conservatively materialize it as settled at that pose for the
first implementation. Preserving motion across restart is optional later.

### Lost and Found

Collection revokes the simulation lease before committing `LostAndFound`. All later spatial samples
from the old epoch are rejected. Distance/grace checks use the latest accepted/committed absolute
pose rather than a stale host transform.

### Cold containers

Deposit revokes the lease and requires a reliable logical commit before retiring the representation.
Withdrawal creates a settled spatial state or inventory/hand placement. Cold records never receive
physics samples.

### Inventory and recall

Pickup/recall wins over motion only after the host accepts its logical transition. The accepted
transition closes the epoch, preventing a late settlement from dropping the recalled item back into
the world.

### Snapping and trains

Snap and parent changes are reliable logical/spatial commits, not inferred indefinitely from
high-frequency poses. A simulator may propose an anchor, but the host validates occupancy,
compatibility, and stable identity before committing it.

## Implementation status

### Phase 1: reliable final-pose synchronization — implemented

Solve the reported persistence/return bug before attempting smooth shared physics:

- detect Rigidbody sleep or a low-motion dwell on the throwing client;
- send a reliable settlement proposal;
- validate and commit position, rotation, parent, and cell on the host;
- relay a reliable correction/commit;
- build future projection baselines from the committed record; and
- add timeout fallback to the last reported pose.

The durable authority revision advances only when the host accepts the settlement. Projection,
interest, persistence, Lost and Found distance checks, and later pickups therefore consume the same
canonical resting pose.

### Phase 2: transient movement stream — implemented

- introduce lease/epoch/sequence state;
- send adaptive position, rotation, and velocity samples;
- relay only to interested observers;
- interpolate non-simulating proxies; and
- update moving cell membership from canonical spatial state.

Samples use the unreliable transport with application-level epoch and sequence ordering. This is
intentional: connection-wide sequenced delivery could allow a busy item to suppress a sample for a
different item. The host bounds speed, displacement, simulator distance, parent validity, revision,
epoch, and sequence before accepting a sample.

### Phase 3: lease transfer and recovery — implemented

- host takeover when possible;
- transfer between clients when the current simulator disconnects or leaves interest;
- save-boundary transient checkpoints;
- instrumentation for stalled or never-settling motion; and
- abuse/rate diagnostics.

On timeout or simulator disconnect, the host transfers the lease to an eligible nearby participant,
preferring an active host projection. If no simulator is eligible, it commits the last accepted pose
as a stopped recoverable state. A save made during motion checkpoints the latest host-accepted pose
and loads it conservatively as settled.

### Phase 4: advanced physical interactions — deferred

Treat these as later, separately tested expansions:

- piles and many simultaneous contacts;
- player pushing without pickup;
- doors, drawers, and non-item rigidbodies;
- items riding unsnapped on moving trains;
- joints, hoses, wires, and compound bodies;
- collision damage or gameplay effects; and
- VR-specific handoff/prediction behavior.

## Observability

Add item-spatial events with the existing scenario correlation support:

```text
item.spatial-lease-granted
item.spatial-lease-revoked
item.spatial-lease-transferred
item.spatial-lease-applied
item.spatial-sample-sent
item.spatial-sample-accepted
item.spatial-sample-rejected
item.spatial-sample-relayed
item.spatial-settlement-proposed
item.spatial-commit-accepted
item.spatial-commit-applied
item.spatial-timeout
item.spatial-unresolved-motion
item.spatial-cell-changed
item.spatial-reconciled
```

Dashboard item state should show simulator, epoch, last sequence, last sample age, velocities,
accepted pose, committed pose, committed cell, and rejection reason.

## Runtime test suite

Each scenario remains one file and owns all created fixtures.

Initial scenarios:

1. Client throws a remote authored item; host later enters and sees the settled pose.
2. Host throws; client observes the same settled pose.
3. Two observers see one simulation stream and converge after settlement.
4. Thrown item crosses a 128 metre boundary and reprojects from its new cell.
5. Simulator disconnects in flight; item remains recoverable at the last accepted pose.
6. Old-epoch samples after pickup are rejected.
7. Late settlement after Lost and Found collection is rejected.
8. Late settlement after cold-container deposit is rejected.
9. Save/reload restores the committed post-throw pose, not the authored default.
10. Floating-origin shifts during motion preserve the same absolute pose.
11. Invalid teleport, NaN, excessive speed, wrong simulator, stale sequence, and stale epoch are
    rejected without mutation.
12. Repeated throws do not leak leases, projections, fixtures, NetIds, or pending samples.
13. A resting loose item follows a moving train without producing world-motion traffic or changing
    item cells.
14. An item sliding inside a train synchronizes local motion and settles at the same train-relative
    pose for host and client.
15. An item leaving a train commits one `TrainInterior -> World` transition at the derived absolute
    pose.

Later transient-stream scenarios should assert interpolation error and packet-rate bounds without
requiring frame-identical trajectories.

## Decisions to validate during implementation

- Exact sleep and low-motion dwell thresholds for DV item masses.
- Whether `Rigidbody.Sleep` is reliable for every item or needs sampled fallback.
- Best LiteNetLib delivery mode for batched sequenced samples.
- Rotation and velocity compression precision.
- Whether inactive host projections can safely have transforms updated immediately.
- Which collisions require gameplay authority rather than visual approximation.
- How loose items riding unsnapped on trains transition between world simulation and train anchors.
- How DV itself handles never-sleeping, out-of-bounds, penetrated, or explosively moving items.
- Whether runaway recovery should freeze, roll back, relocate, defer, or use an existing vanilla
  recovery path; no behavior is selected yet.

## Implemented correctness boundary

The implemented result does not promise frame-identical trajectories or general shared collision
authority. It promises this:

> Once physical motion ends, the host reliably commits the actual resulting pose, cell, and parent,
> and every later observer starts from that same canonical result.

Runaway or glitched items are currently observed after 30 seconds with an explicit
`item.spatial-unresolved-motion` warning. The network stream remains rate-bounded, but the mod does
not freeze, relocate, destroy, depenetrate, or collect the item. That policy remains in the research
basket until Derail Valley's behavior and appropriate recovery semantics are understood.
