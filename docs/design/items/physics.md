# Physics

> Status: built in R7 of the [item refactor](../item-refactor.md) (#389-#396, drafts, 2026-10-25), not yet
> proved in a game: that is the end pass (OI-REF-01). It covers an item loose in the world, a car or
> on a shelf: who simulates it, the samples and the settlement, throws, the terrain guard, the fall
> rescue and the pickup range. Items resting in a moving train are [train-wake.md](train-wake.md);
> who is sent an item is [projection.md](projection.md); the record a commit writes is
> [live-state.md](live-state.md). It replaces the legacy [item-spatial-physics.md](../../legacy/items/item-spatial-physics.md).

| Side | Code |
| --- | --- |
| Core | `Multiplayer.Core/Items/Physics/`: `ItemLeaseRules`, `ItemSimulatorChoice`, `ItemSpatialStreamValidator`, `PickupReach`; `ItemReleaseSync.cs` (`ItemPlacementRevision`, `ItemReleaseGrace`, `ItemObjectStateAuthority`, `ItemInteractionWindow`) |
| Shared | `Multiplayer/Shared/Items/Physics/`: `NetworkedItemSpatialManager` (every game's lease table), `ItemTerrainGuard`, `ItemFallRescue`, `TrainItemImpactWitness`, `GrabHandlerItemPatch` |
| Server | `Multiplayer/Server/Items/Physics/`: `ServerItemSpatial`, `FallRescueServer`; `HostItemActor` (`Server/Items/LiveState/`); the pickup check in `NetworkedItemServer.ValidateUpdate` |
| Client | `Multiplayer/Client/Items/Physics/`: `ClientItemSpatial`, `FallRescueClient` |

A dropped or thrown item moves under one machine's physics, its **simulator**, which holds a
**lease** from the server. The simulator sends samples; the server checks and relays them; every
other copy is a kinematic proxy that follows them. When the item comes to rest, the server commits
the resting pose, and every later observer starts from it.

| Packet | Direction | Delivery | To |
| --- | --- | --- | --- |
| `ClientboundItemSpatialLeasePacket` | server to clients | reliable ordered | the simulator and every `ReadyForItems` player who knows the item |
| `ClientboundItemSpatialSamplePacket` | server to clients | unreliable | those who know the item, but the simulator |
| `ClientboundItemSpatialCommitPacket` | server to clients | reliable ordered | the simulator and those who know the item; it carries the move's cause |
| `ServerboundItemSpatialSamplePacket` | simulator to server | unreliable | |
| `ServerboundItemSpatialSettlementPacket` | simulator to server | reliable ordered | |

None goes to the host's own player (`NetworkServer.SendItemSpatial*`): its world is the server's,
and when it simulates, the server's lease is the lease. A settlement is a spatial state with phase
`Settled`. Events are category `item-spatial`.

Two orders: **the authority revision** orders where the item is (placement, holder, owner,
committed pose), and a sample never moves it on; **epoch and sequence** order motion, a new epoch
per lease (`Epochs`) and a strictly newer sequence per sample. A lease gives no right to change
placement, owner, object state, containment or lifecycle.

## Leases

A lease simulates an item loose in the world, a car or on a shelf (`ItemLeaseRules.IsSimulatable`:
`World`, `TrainInterior`, `StaticParent`), from the revision it was granted at. It **holds** while
the item is still in one of those and only its object state (a lantern's flame) has changed since
(`ItemLeaseRules.Holds`, `ItemPlacementRevision.UnchangedSince`).

Every committed change says what it does to the lease (`ItemLeaseRules.AfterChange`, called from
`ServerItemSpatial.OnAuthoritativeTransitionCommitted`):

- **Keeps:** object state only.
- **Begins:** a player lets go of it, a drop or a throw from a hand or an inventory (any throw), or
  the server's own release on a dedicated host. The lease is queued for the releaser and granted the
  next frame if placement is still unchanged (`BeginHostLease`, reason `world-release`); it
  supersedes an older one. Phase `InFlight` for a throw, `Sliding` for a drop.
- **Ends:** anything else, including the server moving the item about the world itself
  (`placement:<placement>`).

Every change that ends a lease ends it **explicitly, at that change** (OI-PHY-02,
`ServerItemSpatial.EndLease`): nothing waits for a stale sample to notice. Besides `AfterChange`,
the ends are a recall (`recalled`, `LiveItems.Inventory`; OI-ESS-11), a Lost and Found collection
(`lost-and-found`), a removed record (`record-removed`), a refused settlement or parent change
(`settlement-refused:<why>`, `parent-change-refused:<why>`; OI-PHY-03, -04) and the server's own
commit ([below](#the-servers-own-commit)). As a backstop, `UpdateHostLeases` ends a lease that no
longer holds (`placement-changed`), for a change that skipped the rest (F40). An end tells the
simulator (`Active = false`) and forgets the committed state.

The host records why each item's last lease ended, `revoked:<reason>` or `settled:<reason>`
(`NetworkedItemSpatialManager.LastLeaseEnd`; `spatialLastLeaseEnd` in the debug snapshot).

**The save** takes a lease's latest pose only while the lease holds (`ItemSaveLoad.RowOf`); else the
record's. An unowned item is saved by its own rule (`ShouldPersistUnowned`) whatever its lease
(OI-PER-11, F25, SL4).

## Who simulates

The server simulates as `HostItemActor.SimulatorId`: its own player's id on a player host, 0
(`HostItemActor.ServerSimulatorId`) on a dedicated host. A lease for simulator 0 is never granted on
a player host.

- **A release** goes to the player who let go, or the server itself for its own release on a
  dedicated host.
- **A transfer** happens when a lease's simulator leaves (`simulator-disconnected`) or sends no
  accepted sample for 3 s (`sample-timeout`, `item.spatial-timeout`). `ItemSimulatorChoice.ForTransfer`
  picks from the candidates who have the item, within 192 m of the last accepted pose (or riding
  its car), not the old simulator: the host's own world first, then the nearest player. The new
  lease (`lease-transfer:<why>`) has a new epoch and the last accepted state as its baseline,
  velocity included. With no candidate, the last accepted pose is committed.
- **A train wake** picks by `ItemSimulatorChoice.ForWake` ([train-wake.md](train-wake.md)).
- **A change of parent** keeps the simulator ([below](#a-change-of-parent-in-flight)).

A candidate (`ServerItemSpatial.Candidates`) is a `ReadyForItems` player whose game has the item:
projected and acknowledged, or, for the host's own player, its object active. On a dedicated host
the server itself is one too, where its world is solid (`ServerWorldCoverage.IsSolidAt`: the cell
loaded, or static ground just under it; OI-PHY-07, F32). Before R7 candidates were players only, and
a thrown item froze in mid-air when its thrower left. A player keeps every item it simulates in its
interest until the lease ends (`ServerItemSpatial.SimulatedBy`).

## Samples and validation

**Sampling** (`NetworkedItemSpatialManager.TrySampleLocalLease`) runs on the simulator, watched or
not: 15 Hz while linear or angular speed is above 1 (relative to the car in a car), else 8 Hz. After
30 s unsettled it publishes `item.spatial-unresolved-motion` once and does nothing more. A
remote client sends its samples (`ClientItemSpatial`); the server takes its own as a client's would
be (`ServerItemSpatial.SampleHostLease`).

**The server checks each one** (`TryAcceptHostSample`): the lease and the item exist; the stream
(`ItemSpatialStreamValidator.Validate`: placement simulatable and unchanged since the lease's
revision, sender and both simulators the same, the same epoch and revision, a newer sequence); the
record still matches; finite values, a quaternion magnitude of 0.5 to 1.5, at most 150 m/s and
100 rad/s; within 192 m of the sender; a displacement of at most 180 m/s × elapsed + 3 m
(parent-local while the parent is unchanged); a parent that exists within 192 m of the sender
(`ValidateParent`); and a settlement says `Settled`. The server's own samples skip the range checks.

Accepted, it is the lease's last accepted state, the server's copy is posed (when it simulates) or
kept a proxy, the item's place in the world index is refreshed (`RefreshHostSpatialPosition`) and
it is relayed. Rejected, it is `item.spatial-sample-rejected`; unless it was only stale or from the
wrong sender, the lease ends by committing the last accepted pose (`sample-rejected:<why>`).

**Observers** (`ApplyObservedPose`) follow the latest target, extrapolated by its velocity for up to
0.15 s, smoothed toward at 10/s, snapping beyond 6 m. An extrapolation that would end under the
terrain when the sample itself is above it is dropped. A player who gets the item's Create while a
lease is open (a late joiner, or one coming into range) gets the lease too (`SendActiveLeasesTo`,
`interest-entry`), and the Create carries the last accepted pose.

## Settlement

After at least 0.25 s, the simulator's item is settled when its body sleeps, stays under 0.05 m/s
and 0.1 rad/s for 0.5 s, or (in a car) holds a car-local pose within 1 cm and 3 degrees for 1 s
under 0.05 m/s. Before it sends that last sample, an item resting on a shelf that vanilla hasn't
parented yet is parented to it (`AdoptRestingShelf`, SH1).

The commit (`CommitHostSettlement`, `LiveItems.TryCommitSpatialPose`) needs the lease to be current
and placement unchanged since its revision. The revision moves on, the placement follows the
sample's parent, the pose and parent are written, the ledger takes the landing
(`ServerLedger.OnSettled`), and a move cause is logged only if it landed somewhere else. The
settler is recorded, so their own regrab made just before the commit still holds (OI-PHY-16,
`ItemPlacementRevision.UnchangedFor`). The server stops and sleeps its body, kinematic if it is in a
car or another machine simulated it; clients that get the commit do the same. A car settlement
registers with the train wake.

Only a reliable, accepted settlement commits the durable pose; a sample never changes placement,
holder or owner (OI-PHY-01).

## A change of parent in flight

A flight whose samples show a new parent (it landed in a car, left one, came onto a shelf) for
`ItemLeaseRules.ParentChangeDwell` (0.25 s) is committed as a change of its own
(`ItemLeaseRules.CommitsParentChange`, `ServerItemSpatial.CommitParentChangeIfDue`; OI-PHY-05,
F33). The record takes the new parent at a new revision, and the flight goes on under a new epoch
with the same simulator: a lease packet with reason `parent-change`, whose revision every client
applies. Before R7, only the settlement fixed the parent, so a save, a Create or interest had the
old one for the rest of the flight.

The commit's window is anyone's (`ItemPlacementRevision.Anyone`): the item is still the same flight,
so a request anyone made from before it still holds (`UnchangedFor`), as a settler's own does after a
settlement. Before the R7 fixes it was the simulator's alone, and a player catching a thrown mug just
as it crossed into a flatcar was refused, the mug snapping back into the thrower's flight. The
settlement that follows keeps the window for its settler, and a parent change carries on the window
before it: a player who settled the item before a wake set it sliding into the next car still holds.

## Throws and release

A drop or a throw is an ordinary item update. The server checks it (`NetworkedItemServer.ValidateUpdate`):
a world release within 64 m of the sender, a car parent that exists within 192 m, a shelf that
resolves within 192 m. A throw carries only its direction (`ThrowDirection`): the thrower keeps its
own velocity, and every other game replays `GrabHandlerItem.Throw(direction)` before its copy
becomes a proxy (`GrabHandlerItemPatch` hands every `Throw` to `NetworkedItem.OnThrow`).

- **A gentle release waits** (`ItemReleaseGrace`): under 2 m/s relative to the player's car it is
  held 0.3 s and sent only if no hand has the item by then, so a VR hand swap isn't a drop.
- **A late acceptance doesn't undo a regrab or a throw** (`ItemSnapshotApplicationPolicy.IsSupersededLocalRelease`,
  `IsSupersededLocalHold`): it counts only as a revision. A lease for a player's own release that
  arrives once the item is back in their hand isn't simulated (`item-locally-possessed`).
- **Only the holder sends object state** (`ItemObjectStateAuthority`), book pages aside. A player
  working an item where it lies (a lantern's knob) has a window while the control is held and 1.5 s
  after (`ItemInteractionWindow`); the server takes such a change within 6 m, as object state only.

## Loose items on trains

A car item's pose and velocity are relative to `trainCar.interior`. It follows its car in the world
index, not a cell, and train motion moves its parent without being resent.

- Parenting uses DV's whole reparenting, `ItemReparentingBase.ParentItemExternal(interior, car.rb,
  null)` (`ApplyAuthoritativeParent`), which sets up `CabItemRigidbody` and `TrainPhysicsLod`.
- `CabItemRigidbody` already reports velocity relative to the train, so `Capture` doesn't subtract
  the car's and `ApplySimulationVelocity` doesn't add it; a shelf's body velocity is subtracted. DV
  keeps a resting cab item awake with small impulses: hence the car-local dwell.
- **A settled car item is kinematic on every game**: the server and every game that got its commit,
  and since R7 a game that gets it already settled, in a Create or a convergence correction
  (`NetworkedItemSpatialManager.HoldSettledTrainItem`; OI-PHY-06, F34). That game also takes its
  settled state as committed, so it can report an impact on it ([train-wake.md](train-wake.md#late-joiners)).

## The terrain guard

A small convex item, such as the mug, can go straight through DV's terrain heightfield in plain
PhysX ([research](../../research/item-terrain-tunnelling.md)). Every game guards the bodies it
simulates (`ItemTerrainGuard.Run`), right after each physics step (`WaitForFixedUpdate`): dynamic,
awake and active. A proxy is kinematic, so only a lease's simulator, or the server for an item with
no lease, acts.

A body above `Terrain.SampleHeight` after the last step and under it now has crossed. The guard puts
it on the surface where it crossed, its lowest bound 2 cm above, and takes away the part of its
velocity going into the ground. It leaves it alone when the segment it moved along hits no
`TerrainCollider`; when the terrain ignores it (`Physics.GetIgnoreCollision`) or the crossing is
within 10 m of a tunnel portal (`TunnelCollisionIgnore`); or when it is grabbed, rides a car, or
moved further than `|v| × 2 × fixedDeltaTime + 0.25 m` (code moved it). The fix happens before the
step is rendered or sampled, so no observer sees it under the ground.

## The server's own commit

`ServerItemSpatial.CommitServerPose` puts a loose item where its object now is, at rest, as a
committed change (OI-PHY-11). The lease ends (and tells its simulator), the epoch moves on, and the
server's state of the item (simulator 0, `Settled`, no velocity) is committed by
`LiveItems.TryCommitSpatialPose`: a new revision, the record takes the pose and the car or shelf it
is in. The move is logged with its cause (`ItemChangeLog.RecordMove`, `LastSpatialCause`), the body
sleeps (kinematic in a car, registered with the train wake at the new revision), and every
game that has the item gets the spatial commit, cause included. A refusal is `item.server-pose-rejected`. `FallRescueServer.PutAt` moves the object first
and calls it: the fall rescue, and a refused bin (`Binning`, OI-VAN-02). Before R7 a rescue wrote
the record's position alone, at no revision, and sent it as convergence.

## Fall rescue

The backstop for what the terrain guard misses (no terrain loaded, a body it doesn't simulate). It
covers every loose item, in the world, on a shelf or in a car (OI-PHY-11, F38).

- **The server** (`FallRescueServer.Scan`) scans once a second: active, ungrabbed items whose record
  is loose (`ItemLeaseRules.IsSimulatable`), not in Lost and Found or waiting for a restore, more than
  1 m under its terrain with nothing but terrain and items within 3 m below
  (`ItemFallRescue.HasFloorUnder`, so a tunnel counts as a floor). It remembers a last safe pose: a
  loose item at rest (kinematic or under 0.5 m/s) above the surface or no more than 0.2 m under it,
  in its car's frame when in a car, and an item in a hand above ground.
- **A remote client** (`FallRescueClient`) scans once a second for dropped or thrown ungrabbed items
  within 200 m of its player, under its terrain with no floor, and reports each (an
  `ItemRequest` with `ReportBelowMap` and the surface height), once per item per 5 s. The server
  acts only if its own copy is under that surface too with no floor under it
  (`HandleBelowMapReport`): the server usually has no terrain loaded around a distant player.
- **The rescue** (`FallRescueServer.Rescue`) puts a car item back into its car, at its last safe
  point there plus 0.1 m, else 1.5 m above the car interior's origin. Anything else goes to its last
  safe pose plus 0.1 m if that is within 50 m horizontally, else 0.5 m above the surface where it
  fell. It is a committed change ([above](#the-servers-own-commit)) with cause
  `RescuedFromBelowMap`, ending any lease (`fell-through-map`).

## Pickup reach

The server takes a pickup of an item lying outside every player and container (in the world, on a
shelf, in a car, attached or snapped to a car: `PickupReach.LiesOut`) only from within 10 m of where
its own copy is (`PickupReach.Within`, `PickupReach.Metres`; OI-PHY-08, F35). Before the R7 fixes the
check took only the places a lease simulates, so an end-of-train lantern on a car 250 m away could
still be taken. The
check is in `NetworkedItemServer.ValidateUpdate`: an update into a hand or an inventory from a
player who doesn't hold the item, after the projection check (`item-not-projected-to-sender`).
Further away it is refused `pickup-out-of-reach`, with the usual correction. Vanilla grabs at about
2 m; 10 m allows for a player's position reaching the server late, and VR reach. A take from a
player's hand or inventory is `ItemAccess`'s ([access.md](access.md)). Before R7 anyone who had
been sent an item could take it from across the map.

## Player and dedicated hosts

| | Player host | Dedicated host |
| --- | --- | --- |
| The server simulates as | its player (`SimulatorId`) | itself, 0 (`ServerSimulatorId`) |
| Its own release | the host's player's lease | the server's own lease; its samples skip the range checks |
| A transfer | the host's player first, if its copy is active | the server itself first, where its world is solid |
| A train wake | the host's player as any player | the server itself last, where its world is solid |
| Spatial packets | never to the host's own player | to every player |
| Its own impact witness | as its player | as the server (`serverWitness`) |

A lease the server simulates has no sample timeout; a refused settlement of one settles at its last
accepted pose, or the lease ends (OI-PHY-04).

## Not built

- Recovery for runaway or never-sleeping motion (OI-PHY-15, paused); a throw onto a moving flatcar
  landing within tolerance on both games (OI-PHY-14, paused). Trajectories aren't frame-identical:
  what is promised is that once motion ends, the server commits the real resting pose and parent.
- OI-PHY-09 (a throw sends its direction only) and OI-PHY-10 (observers' extrapolation) are R8 rows,
  unticked: the code's rules are described above.

## Tests

Core (`Multiplayer.Core.Tests/Items/Physics/`): `ItemLeaseRulesTests`, `ItemSimulatorChoiceTests`,
`ItemSpatialStreamValidatorTests`, `ItemReleaseSyncTests`, `PickupReachTests`.

## Scenarios

In `DevTools/Scenarios/gate.sh` and [testing.md](../../testing.md); every R7 one runs at the end
pass (OI-REF-01), on both host kinds unless it says otherwise.

- `lease-ends` (`MODE=collect`, `refused`, `samples`, `save`): a collection ends a lease in flight,
  a refused settlement ends it, samples don't change the record, and an owned item moved in flight
  past a lease kept open (a probe) saves the record's pose, not the lease's.
- `item-bug-recall-lease` (gate `recall-lease`): the recall ends the lease (`revoked:recalled`).
- `thrower-leaves` (dedicated): the server takes the lease of a thrown item whose thrower quits.
- `late-join-train-item` (`rejoin`, `MODE=convergence`, `MODE=parent-change`): a settled car item
  stays kinematic on a late joiner; a parent change is committed in flight.
- `fall-rescue-interior`, `client-fall-rescue`: rescues into a car, and from a client's report.
- `pickup-range` (and `MODE=attached`, a lantern on a coupler): a pickup from 40 m is refused, one
  beside the item taken.
- `server-throw` (player host; `ACTOR=client` on both), `host-throw-settles` (player host),
  `item-release` (`regrab`, `regrab-late`, `throw-flatcar`, `object-state`, `interact`),
  `train-items`, `shelf-item`.
