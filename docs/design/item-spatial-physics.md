# Multiplayer Item Spatial and Physics Synchronization Design

> Status: implemented through the transient simulation, reliable settlement, lease-transfer, and
> persistence-checkpoint milestones described below. Advanced physical interaction authority and
> runaway/glitched-item recovery remain deliberately deferred.

## Implementation status (audited 2026-10-14)

- **Implemented:** phases 1-3 below, in
  `Multiplayer/Shared/Items/NetworkedItemSpatialManager.cs` (the leases every game holds, its own
  simulation and a client's side of the stream) and `Multiplayer/Server/Items/ServerItemSpatial.cs`
  (the host granting, validating, transferring and committing leases), with the
  epoch/sequence checks in `Multiplayer.Core/Items/ItemSpatialStreamValidator.cs` and the
  save checkpoint in `Server/Items/WorldItemPersistenceSave.cs`. Loose items on trains (train-local
  samples, settlement and the kinematic freeze) are implemented, and
  [`train-item-wake.md`](train-item-wake.md) covers waking them.
- **Differs, simulator choice:** the player who releases the item (the host included) always gets
  the lease; the host is not preferred at release. On a 3 s sample timeout or simulator
  disconnect the lease goes to the host if its copy is active, else to the nearest client within
  192 m that has acknowledged the item, else the last accepted pose is committed.
- **Differs, sampling:** 15 Hz above 1 m/s or 1 rad/s, otherwise 8 Hz; no extra sample on
  collisions or parent changes; one unreliable packet per item per recipient (not batched),
  sent only to players who know the item. Leases, samples and commits are never sent to the host's
  own player: when the host simulates, the server's lease is the lease (its client dropped them).
- **Not implemented:** explicit lease revocation on Lost-and-Found collection or cold deposit
  (collection advances the revision, so later samples and settlements are rejected; a deposit
  needs the item in hand, which already ends the lease).
- **Fixed (2026-10-19):** in a VR game, an item another player held stayed a live body; see
  "Observer presentation".
- **Added:** an item that falls more than 1 m under the terrain has its lease revoked and is moved
  back to its last safe pose (`ServerItemManager.FallRescue.cs`, 5cf63a0, f0277de). Host
  convergence skips items with a live lease (b601cc6).
- **Added (playtest 2):** a gentle release is held back 0.3 s so a VR hand swap isn't a drop,
  only the holder sends object state, and object-state changes no longer void a lease; see
  "Leaving the hand" below.
- **Runtime tests:** the old in-game scenario system is gone. The dvmp `train-items` scenario
  covers drops and throws in a moving cab (scenarios 13-15 below), `host-throw-settles` a host's
  own throw settling with nothing echoed to its client, and `dvmp soak` throws and
  drops items at random then runs a desync check; see [`../testing.md`](../testing.md). The other
  scenarios below are not automated.
- The "Current behavior and exact failure" section records the bug as it was in July, before
  this layer existed.

## Purpose

Define how a host-authoritative item can move through the world after it leaves an inventory or
hand. This is deliberately separate from
[`item-world-sync.md`](item-world-sync.md): that design owns
identity, lifecycle, placement, interest, and persistence, while this design owns transient spatial
motion and the reliable commit of the resulting resting pose.
The cross-feature states and precedence rules consumed by both designs are canonicalized in
[`item-lifecycle.md`](item-lifecycle.md).
Train-specific reactivation of settled items after support removal, item impacts, hard train
motion, and derailment is specified separately in
[`train-item-wake.md`](train-item-wake.md).

The immediate bug motivating this work is:

1. A client enters a remote area and receives the correct host-authored item projections.
2. The client picks up and throws an item.
3. The throw and later pickup refer to the correct item identity.
4. The host never learns the item's post-throw resting pose.
5. When the host later enters the area, its projection is still at the original position.

This proves that logical identity synchronization is working while spatial authority is incomplete.

## Current behavior and exact failure

Historical (July 2026): this is the behaviour before the spatial layer was built.

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

What another player holds or carries changes only by that player's own packets. The host's copy of
the item mirrors them, and when the host observes a change in it (taken in a hand, a lantern lit),
it applies only the object state, never a placement by the host's own player
(`NetworkedItemManager.TryKeepRemotePossession`). Applying it as the host's change booked the item
to the host's player, and the next update took it out of the player's hand; on a dedicated server
that's the parked service body (playtest 2). `AuthoritativeItemRegistry.KeepRemotePossession` is a
backstop for any other path that records such an item as the host player's.

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

### Leaving the hand

Playtest 2 (VR players, dedicated server) found the steps above undone in four ways: a hand swap
dropped the item, a thrown lantern snapped back to where it was let go, other players saw it jitter,
and one thrown onto a train vanished. What the code does now:

- **A gentle release waits.** A VR hand swap, or grabbing an item again at another spot, lets go
  for a moment first. A release from the hand under 2 m/s (relative to the car the player is on)
  is held back for 0.3 s and sent only if no hand has the item by then; a re-grab inside that
  sends only the hand change (`NetworkedItem.DeferLocalRelease`, `ItemReleaseGrace`). Throws,
  stowing and anything faster go at once. Other players see the item leave the hand 0.3 s late.
- **A late acceptance doesn't undo a re-grab.** If the drop did go out, the host's acceptance can
  arrive after the item is back in a hand. It is taken as a revision only
  (`ItemSnapshotApplicationPolicy.IsSupersededLocalRelease`); the pickup sent since decides. The
  lease granted for that drop isn't simulated on a held item. The player who let go counts as
  having the item in their world, so their pickup of it validates like anyone's.
- **A late hold doesn't undo a throw.** The reverse race: a VR throw lets go before the host's
  acceptance of the hold (or of the last in-hand update letting go sends, with the hand cleared)
  comes back. Applied, that hold became the client's last sent state, so the client sent its drop
  a second time against the hold's revision; the host had already accepted the first drop, refused
  the second as stale, and its correction carried the record's pose, the release point. The client
  applied it, and the item stopped dead mid-flight and fell straight down (playtest, 19 October:
  17 corrections in 27 throws, 10 of them visibly stopping the item in the air). It is now taken as a revision only
  (`ItemSnapshotApplicationPolicy.IsSupersededLocalHold`); the drop sent since decides.
- **Only the holder sends object state.** An item's tracked values (a lantern's flame and wick)
  change on every game it's in, held or not. A client sends object-state-only changes only for an
  item in its own hand or inventory, book pages aside (`ItemObjectStateAuthority.ClientMaySend`).
  The host ignores, without a correction, any it still gets from a non-holder
  (`ItemObjectStateAuthority.HostIgnores`): such a correction carried the record's position,
  which put a thrown item back at its release point, and set the sender's copy off again.
  Placement and position disputes still get a correction.
- **Working an item where it lies.** A player may still change a set-down item: turn a lantern's
  knob, light it with their lighter, flip a mounted gadget's switch. A value changing can't tell
  that from drift (a lantern's knob is a physics body in every game), so the player's own action
  opens a window: grabbing or using one of the item's controls (`ControlImplBase.FireGrabbed`,
  `FireUngrabbed`, `Use`), or lighting a lantern with a flame they hold (`IgnitableUse.HandleUse`,
  `Igniter.IgniteNow`, then `Lantern.Ignite`), all in `ItemInteractionPatch`. It's open while the
  control is held and 1.5 s after (`ItemInteractionWindow`). Changes inside it go out as
  object state marked `ItemTransitionReason.ClientInteraction`. The host takes a marked change
  from a player whose game has the item (it acknowledged it) within 6 m of it, for an item in the
  world, on a car or attached, as object state only: the record keeps its own pose
  (`AuthoritativeItemRegistry.KeepRecordedPose`). Unmarked changes, and every change from older
  builds, are still ignored. A client doesn't re-apply the host's echo of its own object-state
  change (the knob may still be turning), and a received wick also sets the lantern's knob, which
  the wick follows, unless the local player is turning it.
- **Object state doesn't void a lease.** Every accepted change advances the authority revision,
  object state included, and the host's own copy of a flying lantern changes its flame. The lease,
  its samples and its settlement were tied to the exact revision, so one flicker on the host
  refused the landing and the record stayed at the release point; the thrower's copy was later
  converged back there. The record now keeps the run of revisions at its tip that changed only
  object state (`ItemAuthorityState.ObjectStateRunBase`/`Top`, `ItemPlacementRevision`). A lease,
  a pending lease grant, a lease transfer and a spatial commit stay good while the item's
  placement is unchanged since their revision, and so does a client transition made against a
  revision inside the run (a pickup that crossed the host's flame change). Any other revision
  change ends the run. What counts as a placement change is `ItemUpdateData.ChangesPlacement`
  (create, item state or position). The registry used to test the update type against
  `FullSync`, which contains `ObjectState`, so every object-state change ended the run and booked
  the item to the acting player. On the host, that was the host's own player: each battery tick of
  the server's copy of a carried flashlight logged "recorded as the server's player's", and the
  holder's next change (their "off", a stash on the belt) arrived stale and was refused
  (playtest 2026-10-19).

The resting report the tracker asked for ("the thrower's game reports where the item comes to
rest, car parent included") is the existing settlement: the releaser holds the lease and proposes
the resting pose with its train or static parent. It was being refused, not missing. The
`item-release` scenario covers these (`MODE=regrab`, `regrab-late`, `throw-flatcar`,
`object-state`, `interact`).

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

The code gives the lease to the releasing player first (step 2), whoever it is, and applies steps
1, 3 and 4 only when the lease has to move on a timeout or disconnect.

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

As implemented: 15 Hz above 1 m/s or 1 rad/s, 8 Hz otherwise, sent at that interval without delta
thresholds or collision-triggered samples. Settlement is proposed after at least 0.25 s when the
body sleeps or stays under 0.05 m/s and 0.1 rad/s for 0.5 s. Samples are unreliable, one packet
per item; they are not batched.

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

An item another player holds is posed on their avatar every frame, so its body is kinematic with
no velocity, and its colliders are off, until the avatar lets it go
(`NetworkedPlayer.SetHeldItemBody`). Its release makes it dynamic again with no velocity, and the
world state applied with it supplies any throw. Only a desktop game's items have a
`GrabHandlerItem`; a VR game's items grab through VRTK and have none. Making the body kinematic
through `GrabHandlerItem.TogglePhysics` alone therefore did nothing in VR: there another player's
held radio fell under gravity with its colliders off between hand poses, 30 m a frame at
1840 m/s, and kept that velocity into their inventory (2026-10-19).

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

Accepted samples and commits update the cell index through `RefreshHostSpatialPosition`;
`RefreshHostWorldItem`, which reads the host Unity transform, is still used for ordinary
transitions.

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
train's world velocity. Long-running train-local motion must not be mistaken for settlement merely
because the train itself is moving.

DV can keep a physically stationary cab item awake with small recurring angular-velocity impulses.
For train interiors, settlement therefore also accepts a one-second dwell inside a tight train-local
pose envelope (one centimetre and three degrees) while linear motion remains low. After the host
commits that pose, the item remains physically parented and becomes kinematic until a later release
starts a new simulation lease. This freezes only proven train-local jitter; genuine sliding, rolling,
or bouncing continues to reset the pose dwell.

Train parenting must use Derail Valley's complete item-reparenting operation, not only
`Transform.SetParent`. Specifically, `ItemReparentingBase.ParentItemExternal` must receive
`trainCar.interior` and the train's canonical `trainCar.rb`. That call also configures
`CabItemRigidbody.SetupTrainReceivingForces(trainCar.rb)` and registers the item with
`TrainPhysicsLod`. Omitting the Rigidbody argument makes the item look parented while its physics
still behaves as world-relative; using `GetComponent<Rigidbody>()` on the car is not equivalent to
using DV's `TrainCar.rb` reference.

For an item physically parented through `CabItemRigidbody`, DV already exposes the item's residual
linear and angular motion relative to the receiving train. Capture converts those body velocities
to interior-local space without subtracting `trainCar.rb` velocity, and application converts them
back without adding it. Subtracting the train body makes a stationary cab item appear to move
backward at train speed forever. A settled item inherits movement through its train parent and must
not receive the train's world velocity again through item replication.

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
from the old epoch are rejected. (The code does not revoke the lease; collection advances the
authority revision, so later samples and settlements fail the revision check.) Distance/grace checks use the latest accepted/committed absolute
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
- joints, hoses, wires, and compound bodies;
- collision damage or gameplay effects; and
- VR-specific handoff/prediction behavior.

Items riding unsnapped on moving trains were on this list; they are now handled by train-interior
placement (above) and [`train-item-wake.md`](train-item-wake.md).

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

In-game scenarios run through the dvmp harness
([`../../DevTools/README.md`](../../DevTools/README.md)); each owns the fixtures it creates. Only
13-15 are automated (`DevTools/Scenarios/train-items`).

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
basket until Derail Valley's behavior and appropriate recovery semantics are understood. The one
exception is an item that has fallen through the terrain: its lease is revoked and it is put back
at its last safe pose.
