# Train wake

> Status: built in R7 of the [item refactor](../item-refactor.md) (#390, #391, drafts, 2026-10-25), not
> yet proved in a game: that is the end pass (OI-REF-01). Thresholds are untuned, and no scenario
> drives a wake yet (OI-PHY-13, paused). It covers loose items resting in a car: why they stay
> still, what wakes them and who simulates them then. The lease, samples and settlement it builds on
> are [physics.md](physics.md). It replaces the legacy [train-item-wake.md](../../legacy/items/train-item-wake.md).

| Side | Code |
| --- | --- |
| Core | `Multiplayer.Core/Items/TrainWake/`: `TrainItemWakePlanner`, `TrainCarMotionRules`; `ItemSimulatorChoice.ForWake` and `.Lacking` (`Core/Items/Physics/`) |
| Server | `TrainItemWakeServer` (`Multiplayer/Server/Items/TrainWake/TrainItemWakeServer.cs`); `ServerItemSpatial.TryBeginTrainWake`, `TrySelectTrainWakeSimulator`; `ServerItemManager.PinInterest` |
| Shared | `TrainItemImpactWitness` (`Shared/Items/Physics/`), `NetworkedItem.TrainWake` (`Shared/Items/LiveState/`), `NetworkedItemSpatialManager.HoldSettledTrainItem` |

`ServerboundItemTrainWakeWitnessPacket` (reliable ordered) is a client's impact witness.
`TrainItemWakeReason`: `SupportRemoved`, `ItemImpact`, `HeavyAcceleration`, `HeavyBraking`,
`TrainJolt`, `ViolentRotation`, `Derailment`.

A loose item resting in a car is **settled**: its settlement committed it `TrainInterior`, parented
to the car's interior and kinematic, so the car carries it and nobody simulates it. Something can
disturb it: the item under it is taken, a thrown item hits it, the train brakes hard or derails. The
server then **wakes** it: it opens a lease again, with a small starting velocity, and the item
settles again through the ordinary lease flow. The server decides every wake from its own records
and its own cars, so it works with no player near the train.

## Settled train items

A settled car item is **kinematic on every game**, with no velocity:

- on the server, at its commit (`ServerItemSpatial.CommitHostSettlement`) and when it registers;
- on every game that got the commit (`ClientItemSpatial.ReceiveSpatialCommit`);
- since R7, on a game that gets it already settled, in a Create or a convergence correction
  (`HoldSettledTrainItem`, from `NetworkedItem.StateApplication`; OI-PHY-06, F34). Before, that game
  made it a live body the car's motion threw about. One with a lease open is left to the lease.

The server keeps a **settled record** for each (`TrainItemWakeServer.RegisterSettlement`): its
revision, car, car-local pose and bounds, and epoch. One is made:

- when a settlement commits in a car (`OnSpatialSettled`);
- when the server registers a car item with no open lease (`ObserveCanonicalItem`, from
  `ServerItemManager.RegisterHostWorldItem`): a loaded save, a restore;
- when the server commits a car item's pose itself (`ServerItemSpatial.CommitServerPose`): a fall
  rescue back into its car, at the new revision.

Bounds are the server's non-trigger colliders in the interior's frame, or a 0.35 m cube.
Registering stops the server's body, makes it kinematic and drops any pending wake for the item. A
car that no longer exists drops its records the next time it is sampled.

## What wakes an item

- **Support removed.** A settled item leaves `TrainInterior` by an accepted change
  (`OnAuthoritativeTransition`), or is destroyed (`OnItemRemoved`): the items resting on it wake.
  The preferred simulator is whoever took it.
- **Item impact.** An item flying in the car hits a settled one ([below](#impact-witnesses)).
- **Car motion.** The car brakes, jolts, turns violently or derails ([below](#car-motion)).

## Impact witnesses

The game that simulates a moving item in a car reports its impacts (`NetworkedItem.OnCollisionEnter`,
`TrainItemImpactWitness.Observe`): a contact with an item this game holds as settled in the same car,
at a relative speed of at least 1.25 m/s or an impulse of at least 0.5. The source must be under a
lease this game simulates. A remote client sends a witness packet; the server's own game hands it
straight over (`ServerItemManager.ReceiveLocalTrainWakeWitness`), as its player on a player host or as
the server itself on a dedicated one.

The server refuses (`TrainItemWakeServer.ValidateWitness`, published as `wake-witness-rejected`, with
nothing sent back):

1. bad ids (`invalid-witness`), a witness id not newer than the sender's last (`stale-witness-id`);
2. non-finite values, a speed over 50 or an impulse over 100 (`invalid-witness-motion`); under the
   thresholds (`impact-below-threshold`);
3. the same sender, source and target within 250 ms (`duplicate-impact-witness`);
4. a sender that isn't the source's simulator at the stated revision and epoch
   (`source-not-current-simulator`), or a source not in that car (`source-not-in-witness-train`,
   `source-canonical-parent-mismatch`);
5. a target that isn't settled in that car at the stated revision (`target-not-current-settled-train-item`);
6. a contact more than 4 m from the target, checked only when the server has the car
   (`contact-outside-target-envelope`).

Accepted, the target and what it supports wake, with 35% of the relative velocity in the interior's
frame, at most 4 m/s; the witness's sender is the preferred simulator.

## Car motion

`TrainItemWakeServer.SampleOccupiedCars` samples each car with settled items every 0.1 s. Its
velocity and angular velocity are measured **in the car's own frame**
(`car.transform.InverseTransformDirection`; OI-PHY-12). A car taking a curve at a steady speed has
the same velocity in its own frame throughout, so the curve's centripetal acceleration, which the
items ride with the car, counts for nothing. Before R7 it was measured in world space, and 3.5 m/s²
is 30 m/s on a 260 m radius: every settled item on a fast train woke on every such curve.

A gap over 0.75 s, or a jump in the car's absolute position beyond `max(5, (v0 + v1) × dt × 2 + 3)` m,
resets the baseline instead of counting (`motion-baseline-reset`). Otherwise
`TrainCarMotionRules.Classify` decides, in priority:

| Reason | When |
| --- | --- |
| `Derailment` | a bogie newly derailed |
| `ViolentRotation` | angular acceleration at least 1.25 rad/s² |
| `TrainJolt` | jolt at least 14 m/s³, with acceleration at least 1.5 m/s² |
| `HeavyBraking` / `HeavyAcceleration` | acceleration at least 3.5 m/s² (braking when against the car's travel above 1 m/s); or a deceleration along its travel of at least 3 m/s² above 1 m/s |

Each car has a 1 s cooldown. Up to 32 of its settled items wake, with 5% of the opposite of the
car's acceleration, in the interior's frame, at most 1.5 m/s, and no preferred simulator.

## Which items a wake reaches

`TrainItemWakePlanner`: the candidates are the settled items in the same car, ordered by their
bottom, then NetId. Support goes upward only: an item is supported when its bottom is within 0.12 m
of the support's top and they overlap by at least 0.01 m on X and Z. Side-by-side clutter doesn't
wake. A removed support's set is what rests on it (`FromRemovedSupport`); an impact's is its target
and what rests on that (`FromSeeds`). At most 32 items, 8 deep.

## The grant

For each item still settled, `ServerItemSpatial.TryBeginTrainWake` needs no open lease
(`item-already-active`), a server object and record (`item-representation-unavailable`), the record
in `TrainInterior` with a car (`item-not-settled-in-train`), and a simulator
(`no-eligible-train-item-simulator`).

The simulator is chosen by `ItemSimulatorChoice.ForWake` from those whose game has the item, riding
the car or within 192 m of it: the preferred player first, then a rider of the car, then the nearest
player, and the server itself last, so a player there sees it first-hand. On a dedicated host the
server itself is a candidate where its world is solid (`ServerWorldCoverage.IsSolidAt`; OI-PHY-07).
The first item's simulator is preferred for the rest of the set.

The lease's baseline is the committed state if still current in the same car, else the record; a new
epoch, phase `Sliding`, the velocity capped at 6 m/s, reason `train-wake:<reason>`. The simulator
applies the velocity. The settled record is removed, and the item runs the ordinary lease flow until
it settles and registers again. A timeout or a disconnect transfers each woken item's lease on its
own.

**Nobody to simulate it.** With no candidate, the players in range who don't have the item yet are
sent it: it is pinned in their interest for 10 s (`ItemSimulatorChoice.Lacking`, riders first, then
nearest; `ServerItemManager.PinInterest`; OI-PHY-12). The wake waits (`wake-pending`) and is retried
every 0.5 s, so it finds one of them once their game has the item. A pending wake older than 10 s
(`PendingWakeSeconds`) is dropped (`wake-expired`) and the item stays settled. The 10 s count from
the first attempt of the trigger that is waiting: a retry keeps it, and a fresh trigger for an item
already waiting (a derailment after a braking) starts its own. Before #397 a retry restarted the
clock, so a wake never expired; #397's first fix let a fresh trigger take the waiting one's clock,
so a derailment, which fires once, could expire untried. So triggers that keep coming (a train
braking hard again and again) keep an item nobody can simulate pending, and pinned, for as long as
they come; that is by design: each is a real disturbance, and the pin is what lets a player take it.
A missing server object waits the same way. Any other refusal, a first attempt or a retry, drops
the wake without an event and keeps the record.

## Late joiners

A player who arrives after an item settled gets it in a Create, already settled. Their game holds it
kinematic in its car and takes its settled state as committed (`HoldSettledTrainItem`), as a game
that saw it settle does. So when they simulate a thrown item that hits it, they can witness the
impact (OI-PHY-12; before R7 a client had a committed state only for items it saw settle). A lease
open when they arrive reaches them with the Create ([physics.md](physics.md#samples-and-validation)),
and a wake nobody can simulate pins its item in their interest if they are in range.

## Events

Category `train-item-wake` ([observability.md](../../observability.md)):
`train-item.settlement-registered`, `support-removed`, `wake-witness-accepted`,
`wake-witness-rejected`, `wake-granted`, `wake-pending`, `wake-expired`, `car-motion-wake`,
`motion-baseline-reset`. The spatial side adds `item.spatial-train-wake-granted` and
`item.spatial-lease-transferred`.

## Not built

- A scenario that drives a wake (OI-PHY-13, paused): the Train Item Lab that did went with the
  in-game test system (f510e8e).
- Tuning the thresholds from captures.

## Tests

Core: `Multiplayer.Core.Tests/Items/TrainWake/TrainItemWakePlannerTests.cs`,
`TrainCarMotionRulesTests.cs`; `Items/Physics/ItemSimulatorChoiceTests.cs` (the wake's choice and
`Lacking`).

## Scenarios

In `DevTools/Scenarios/gate.sh` and [testing.md](../../testing.md); they run at the end pass
(OI-REF-01). None makes a wake happen (OI-PHY-13).

- `late-join-train-item` (`rejoin`, `MODE=convergence`; both host kinds): a settled car item stays
  kinematic at the host's spot on a rejoined client and after a correction, through a push of the
  loco, and the rejoined client holds its committed state. `MODE=parent-change`: a mug thrown onto a
  flatcar is in the car on the server while it flies.
- `train-items` (and `WHO=host MODE=throw WHEN=moving`, player host): an item dropped or thrown in a
  cab stays in it on both games while the train moves.
- `item-release MODE=throw-flatcar`, `fall-rescue-interior`: an item landing on a car stays on it; a
  car item that fell through goes back into its car.
