# Item spatial physics

> Status: as built, checked against the code on 2026-10-23 (`items/lineage`). Built 2026-07 to
> 2026-10 (#12 onwards). Waking items that rest in a moving train is in
> [`train-item-wake.md`](train-item-wake.md); interest and projection are in
> [`item-world-sync.md`](item-world-sync.md).

A thrown or dropped item moves under one machine's physics: the **simulator**, which holds a
**simulation lease** from the server. It sends motion samples; the server checks them and relays
them; every other copy is a kinematic proxy. When the item comes to rest, the server commits the
resting pose, and every later observer starts from it.

> The server owns truth. One selected machine calculates motion. Every other Unity object presents
> the server-accepted stream.

## Two kinds of ordering

- **`AuthorityRevision`** orders logical changes: placement, holder, owner, committed pose. Motion
  samples never advance it. A spatial commit does.
- **Epoch and sequence** order motion. Each lease has a new epoch; each sample has a strictly newer
  sequence within it.

Holding a lease gives no right to change placement, owner, item state, containment or lifecycle.
Object state (a lantern's flame) changing during a flight doesn't void the lease: a lease, its
samples and its commit stay good while placement is unchanged since their revision
(`ItemPlacementRevision`, the object-state run; see [Leaving the hand](#leaving-the-hand)).

## Pieces

| Where | What |
| --- | --- |
| `Shared/Items/NetworkedItemSpatialManager.cs` | Every game's lease table (`Leases`, `CommittedStates`, `Epochs` on the server), local sampling, observer presentation |
| `Server/Items/ServerItemSpatial.cs` | The server: grant, validate, relay, transfer, commit |
| `Client/Items/ClientItemSpatial.cs` | A remote client: apply leases, samples and commits, send its own samples |
| `Multiplayer.Core/Items/ItemSpatialStreamValidator.cs` | The ordering gate (pure) |
| `Server/Items/HostItemActor.cs` | Who the server simulates as |
| `Shared/Protocol/Data/Items/ItemSpatialStateData.cs` | The wire state: NetId, revision, epoch, sequence, tick, simulator, phase (`Settled`, `InFlight`, `Sliding`), absolute pose, velocities, parent kind, NetId and key, parent-local pose, sleeping |

Paths are under `Multiplayer/`. The server simulates as `HostItemActor.SimulatorId`: its own
player's id on a player host, 0 (`ItemTransitionCommand.ServerActorId`) on a dedicated host. Actor
0 may move items about the world but never into a hand or inventory, and never becomes an owner.

| Packet | Direction | Delivery | To |
| --- | --- | --- | --- |
| `ClientboundItemSpatialLeasePacket` | server to clients | reliable ordered | the simulator and every `ReadyForItems` player who knows the item |
| `ClientboundItemSpatialSamplePacket` | server to clients | unreliable | knowers except the simulator |
| `ClientboundItemSpatialCommitPacket` | server to clients | reliable ordered | the simulator and knowers |
| `ServerboundItemSpatialSamplePacket` | simulator to server | unreliable | |
| `ServerboundItemSpatialSettlementPacket` | simulator to server | reliable ordered | |

None goes to the host's own player: when the server simulates, its lease is the lease. A
settlement is an ordinary spatial state with phase `Settled`.

## A flight, in order

1. **Release.** A drop or throw is an ordinary item update. The server checks it
   (`NetworkedItemServer`): a world release within 64 m of the sender, a train parent that exists
   within 192 m, a static parent that resolves within 192 m. A drop or throw with a train or static
   parent becomes `TrainInterior` or `StaticParent`. A throw carries only its direction
   (`ThrowDirection`): the thrower keeps its own velocity, and observers replay
   `GrabHandlerItem.Throw(direction)` before turning into proxies.
2. **Lease queued.** When an item enters `World`, `TrainInterior` or `StaticParent` from a hand or
   inventory, or thrown, `OnAuthoritativeTransitionCommitted` queues a lease for the releasing
   player (the server itself, on a dedicated host's own release). Phase `InFlight` for a throw,
   `Sliding` for a drop. Any placement out of the world revokes the lease
   (`logical-placement-changed`).
3. **Lease granted.** Next frame, if placement is unchanged since then, `BeginHostLease` revokes
   any older lease, bumps the epoch, takes the baseline from the server's transform and sends the
   lease (`world-release`). A non-simulator's copy becomes a kinematic proxy at the baseline.
4. **The client applies it.** An older epoch is ignored. A lease for its own release is skipped if
   the item is back in its hand (`item-locally-possessed`). A lease for an item not created yet
   waits. Only a transferred lease or a train wake gives the new simulator the baseline velocity.
5. **Sampling** (`TrySampleLocalLease`). 15 Hz when linear or angular speed is above 1
   (train-relative in a train), else 8 Hz. The simulator samples whether anyone watches or not.
   After at least 0.25 s, it is settled when the body sleeps, or stays under 0.05 m/s and 0.1 rad/s
   for 0.5 s, or (in a train) holds a train-local pose within 1 cm and 3 degrees for 1 s at under
   0.05 m/s. The settled sample is the last. After 30 s unsettled it publishes
   `item.spatial-unresolved-motion` once and does nothing else.
6. **The server checks each sample** (`TryAcceptHostSample`): the lease and item exist; the stream
   validator (placement still simulatable and unchanged since the lease's revision; sender, lease
   simulator and sample simulator the same; same epoch and revision; newer sequence); finite values,
   quaternion magnitude 0.5–1.5, at most 150 m/s and 100 rad/s; within 192 m of the sender (not for
   the server's own); a displacement of at most 180 m/s × elapsed + 3 m, parent-local while the
   parent is unchanged; a parent that resolves within 192 m of the sender; a settlement must say
   `Settled`.
7. **Accepted:** stored as the last accepted state, the server's copy posed (when it simulates) or
   kept a proxy, the item's cell re-indexed, and the sample relayed. **Rejected:** published as
   `item.spatial-sample-rejected`; unless it was only stale or from the wrong sender, the lease ends
   by committing the last accepted pose (`sample-rejected:<reason>`).
8. **Observers** (`ApplyObservedPose`): the latest target, extrapolated by its velocity for up to
   0.15 s (never past the terrain surface), smoothed toward at rate 10/s, snapping at 6 m. There is
   no sample buffer and no delay.
9. **Commit** (`CommitHostSettlement`, `AuthoritativeItemRegistry.TryCommitSpatialPose`): the lease
   must be current and placement unchanged since its revision. The revision advances, the placement
   is set from the sample's parent kind, the pose and parent are written, and a move cause is logged
   only if it landed somewhere else. The server zeroes and sleeps the body, and makes it kinematic if
   it is in a train or another machine simulated it. Clients that get the commit do the same. A
   train settlement registers with the train wake ([train-item-wake.md](train-item-wake.md)).
10. **Timeout and disconnect.** For a lease another machine simulates, no accepted sample for 3 s
    publishes `item.spatial-timeout`; a disconnect too. Both try `TryTransferLease`: a
    `ReadyForItems` player other than the old simulator, within 192 m of the last accepted pose,
    the host's own player first if its copy is active, a client only if it knows and acknowledged
    the item. The new lease has a new epoch and the last accepted pose as baseline
    (`lease-transfer:<why>`). With no candidate, the last accepted pose is committed. Leases the
    server simulates have no timeout.
11. **Interest entry.** A player who gets the item's Create while a lease is open gets the lease too
    (`interest-entry`), and the Create carries the last accepted pose.
12. **Other exits.** A missing representation revokes the lease. An authored prop reset commits its
    baseline. A save during a flight saves the last accepted pose. A Lost and Found collection
    doesn't revoke it: the revision advances, so later samples and the settlement fail.

A parent change during a flight (into or out of a train) is neither committed nor a new epoch:
samples carry whatever parent the simulator sees, the displacement check uses absolute positions
across the change, and only the final settlement fixes the placement. `ValidateParent` checks only
that the car exists within 192 m of the sender.

## Loose items on trains

A train-interior item's pose and velocity are relative to `trainCar.interior`. It doesn't change
cells as the train moves: its interest comes from the car's occupants and the car's position
([item-world-sync.md](item-world-sync.md)). Train motion moves the parent; item replication doesn't
resend it.

- Parenting uses DV's whole reparenting operation, `ItemReparentingBase.ParentItemExternal(interior,
  car.rb, null)`, not only `Transform.SetParent`. It also sets up
  `CabItemRigidbody.SetupTrainReceivingForces` and registers the item with `TrainPhysicsLod`.
  Passing `GetComponent<Rigidbody>()` instead of `TrainCar.rb` makes the item look parented while its
  physics stays world-relative.
- `CabItemRigidbody` already reports the item's velocity relative to the train, so capture converts
  it to interior space without subtracting the car's velocity, and application doesn't add it.
  Subtracting it made a resting cab item move backward at train speed forever. A static parent's
  body velocity is subtracted.
- DV keeps a resting cab item awake with small impulses, so a train item also settles on the 1 s,
  1 cm, 3 degree dwell. After the commit it stays parented and kinematic until a release or a wake
  starts a new lease.

### Landing on the terrain

A small item with a convex mesh collider, such as the mug, can go straight through DV's terrain
heightfield in plain PhysX ([research](../research/item-terrain-tunnelling.md)): beside the track,
about a quarter to a half of mugs thrown at 10 m/s. So every game guards the bodies it simulates
(`NetworkedItemManager.TerrainGuard.cs`), right after each physics step (`WaitForFixedUpdate`):
dynamic, awake, active and not held. A proxy is kinematic, so only a lease's simulator, or the
server for a world item with no lease, acts.

An item whose body was above `Terrain.SampleHeight` after the last step and is under it now has
crossed it. The guard puts it on the surface where it crossed, its lowest bound 2 cm above, and takes
away the part of its velocity going into the ground. It's left alone when:

- the segment it moved along doesn't hit a `TerrainCollider` (the rendered heightmap is only a
  pre-filter);
- the terrain collider ignores it (`Physics.GetIgnoreCollision`), or the crossing is within 10 m of a
  tunnel portal's trigger (`TunnelCollisionIgnore`). DV's tunnel holes are only visual: what enters
  a portal ignores the terrain on purpose;
- it is grabbed, rides a train, or moved further than `|v| × 2 × fixedDeltaTime + 0.25 m` (code moved
  it).

The correction happens before the step is rendered or sampled, so observers never see the item under
the ground. An observer's extrapolation that would end under the terrain when the sample itself is
above it is dropped.

### Fall rescue

The backstop for what the guard misses.

- **The server** (`ServerItemManager.FallRescue.cs`) scans once a second: active, ungrabbed `World`
  items, not in Lost and Found or waiting for a restore, more than 1 m under their terrain with no
  floor within 3 m below (`HasFloorUnder`: anything but terrain and items, so tunnels count). It
  remembers a last safe pose for a resting item within 0.2 m of the surface at under 0.5 m/s, and for
  a held item above ground.
- **A remote client** (`ClientItemManager.FallRescue.cs`) scans once a second for any dropped or
  thrown ungrabbed item within 200 m of its player under its terrain with no floor, and reports it
  (`ServerboundItemBelowMapPacket`, once per item per 5 s). The server acts only if its own copy is
  under the surface too.
- **Rescue** (`RescueFallenItem`) revokes any lease (`fell-through-map`) and moves the item to its last
  safe pose plus 0.1 m if that is within 50 m horizontally, else 0.5 m above the surface where it
  fell. It writes the record's pose directly (no revision change, no spatial commit), logs
  `RescuedFromBelowMap`, and sends every knower a convergence snapshot with that cause.

Train-interior and static-parent items aren't rescued.

## Leaving the hand

Playtest 2 (VR players, dedicated server) found a hand swap dropping the item, a thrown lantern
snapping back to where it was let go, jitter, and an item thrown onto a train vanishing. The rules
since (`Multiplayer.Core/Items/ItemReleaseSync.cs`, `ItemSnapshotApplicationPolicy.cs`):

- **A gentle release waits.** A release under 2 m/s (relative to the player's car) is held 0.3 s and
  sent only if no hand has the item by then; a re-grab inside that sends only the hand change
  (`ItemReleaseGrace`). Throws, stowing and anything faster go at once. Other players see the item
  leave the hand 0.3 s late.
- **A late acceptance doesn't undo a re-grab.** A drop's acceptance arriving after the item is back
  in a hand counts only as a revision (`IsSupersededLocalRelease`); the lease for that drop isn't
  simulated on a held item. The player who let go counts as having the item in their world, so their
  pickup validates like anyone's.
- **A late hold doesn't undo a throw.** A hold's acceptance arriving after a VR throw counts only as a
  revision (`IsSupersededLocalHold`). Applied, it made the client send its drop twice; the second
  was refused as stale, and the correction stopped the item dead mid-flight (playtest 2026-10-19:
  17 corrections in 27 throws).
- **Only the holder sends object state** (`ItemObjectStateAuthority`), book pages aside. The server
  ignores, without a correction, object state from a non-holder: a correction carried the record's
  pose and put a thrown item back at its release point.
- **Working an item where it lies.** Grabbing or using one of the item's controls, or lighting a
  lantern with a held flame (`ItemInteractionPatch`), opens a window while the control is held and
  1.5 s after (`ItemInteractionWindow`). Object state changed inside it goes out marked
  `ItemTransitionReason.ClientInteraction`, and the server takes it from a player whose game has the
  item, within 6 m, as object state only (the record keeps its pose). A client doesn't re-apply the
  server's echo of its own change, and a received wick sets the lantern's knob unless the local
  player is turning it.
- **Object state doesn't void a lease.** The record keeps the run of revisions at its tip that
  changed only object state (`ObjectStateRunBase`/`Top`). What counts as a placement change is
  `ItemUpdateData.ChangesPlacement` (create, item state or position).

## Held items in other games

An item another player holds is posed on their avatar every frame, so its body is kinematic with no
velocity and its colliders are off until the avatar lets go (`NetworkedPlayer.SetHeldItemBody`). A
VR game's items grab through VRTK and have no `GrabHandlerItem`, so making the body kinematic through
`GrabHandlerItem.TogglePhysics` alone did nothing there: another player's held radio fell 30 m a
frame between hand poses (2026-10-19).

## Not built

- Recovery for runaway or never-sleeping motion. It is observed after 30 s, nothing more: freezing,
  moving or collecting the item could hide a real state or cause worse bugs. Needs research first.
- Explicit lease revocation on Lost and Found collection.
- Shared collision authority: trajectories aren't frame-identical. What is promised is that once
  motion ends, the server commits the real resting pose and parent, and every later observer starts
  from it.

## Known issues

From the code, not run in game (2026-10-23). The bug review tracks them.

- A settled train item arrives as a live, non-kinematic body on a player who gets it later in a
  Create, or who applies a convergence correction: `HandleDroppedOrThrownState` makes the body
  dynamic. Only the server and peers that got the commit keep it kinematic.
- A dedicated host never takes a transferred lease: candidates are players only.
- There is no pickup range check; only the projection acknowledgement gates a pickup.
- A lease the server simulates whose settlement is refused never ends (no timeout for server leases).
- A lease whose placement changed without a transition (a Lost and Found collection mid-flight)
  times out, fails to transfer and fails to commit, every frame.
- A static-parent item drops out of every player's interest after a sample or a commit (see
  [item-world-sync.md](item-world-sync.md#known-issues)).

## Tests

`ItemSpatialStreamValidatorTests`, `ItemReleaseSyncTests`, `ItemSnapshotApplicationPolicyTests`;
scenarios `train-items`, `host-throw-settles`, `server-throw`, `item-release`, `client-fall-rescue`,
and `dvmp soak` ([testing.md](../testing.md)).
