> Legacy: describes the item system before the refactor (names as on `beta` 04bfe277). Read it only as evidence of how the code behaves today, not as design. Replaced by [items/train-wake.md](../../design/items/train-wake.md) (R7). The item docs start at [items/README.md](../../design/items/README.md).

# Train item wake

> Status: as built, checked against the code on 2026-10-23 (`items/lineage`). Thresholds are untuned
> and no dvmp scenario covers a wake yet. The lease, sampling and settlement it builds on are in
> [`item-spatial-physics.md`](item-spatial-physics.md).

A loose item resting in a moving train is **glued**: its settlement committed it in `TrainInterior`,
parented to the car's interior and kinematic, so the train carries it and nobody simulates it
("glued" is shorthand, not a placement). Something can disturb it: the item under it is taken, a
thrown item hits it, the train brakes hard or derails. The server then **wakes** it: it opens a
simulation lease again, on a player's game, with a small starting velocity, and the item settles
and glues again through the ordinary lease flow.

The server decides every wake from its own records, so it works while no player is near the
train and with no physics of its own running there.

## Pieces

| Where | What |
| --- | --- |
| `Multiplayer/Server/Items/NetworkedTrainItemWakeManager.cs` | The coordinator, server only, owned by `ServerItemManager`: settled records, car motion, pending wakes, witness checks |
| `Multiplayer.Core/Items/TrainItemWakePlanner.cs` | Which items a wake reaches (pure, unit-tested) |
| `Multiplayer/Server/Items/ServerItemSpatial.cs` | `TryBeginTrainWake`, `TrySelectTrainWakeSimulator` |
| `Multiplayer/Shared/Items/NetworkedItem.TrainWake.cs`, `NetworkedItemManager.WorldIntegration.cs` | Impact witnesses, on every game |
| `ServerboundItemTrainWakeWitnessPacket` | A client's impact witness (reliable ordered) |

`TrainItemWakeReason`: `SupportRemoved`, `ItemImpact`, `HeavyAcceleration`, `HeavyBraking`,
`TrainJolt`, `ViolentRotation`, `Derailment`.

## Settled records

A settled record holds the item's revision, car, parent-local pose and bounds, and epoch
(`RegisterSettlement`). One is made:

- when a spatial settlement commits in a train (`OnSpatialSettled`);
- when the server registers a train-interior item with no open lease (`ObserveCanonicalItem`, from
  `RegisterHostWorldItem`): a loaded save, a restore.

Bounds are the server's non-trigger colliders in interior space, or a 0.35 m cube. Registering zeroes
the server's body and makes it kinematic, and drops any pending wake for the item. A car that no
longer exists (DV pools a deleted car with what is parented to it) drops its records the next time it
is sampled.

## What wakes an item

**Support removed.** A settled item leaves `TrainInterior` by an accepted transition, or is destroyed:
the items resting on it wake. The preferred simulator is whoever took it.

**Item impact.** `NetworkedItem.OnCollisionEnter` reports, on the game that simulates the moving
item, a contact between a leased train item and an item that game saw settle in the same car, at a
relative speed of at least 1.25 m/s or an impulse of at least 0.5. The server's own witness goes
straight to the manager; a client's is a packet. `ValidateWitness` refuses: bad ids; a witness id not
newer than the sender's last; non-finite values, speed over 50, impulse over 100; under the
thresholds; the same sender, source and target within 250 ms; a sender that isn't the source's
current simulator at the stated revision and epoch; a source not in that car; a target that isn't
settled at the stated revision in that car; a contact more than 4 m from the target (checked only
when the server has the car). Accepted, the target and what it supports wake with 35% of the relative
velocity, at most 4 m/s.

**Car motion.** `SampleOccupiedCars` samples `car.rb` every 0.1 s, in world space, for cars with
settled items. A gap over 0.75 s, or a jump beyond `max(5, (v0 + v1) × dt × 2 + 3)` m, resets the
baseline instead of counting. In priority:

| Reason | When |
| --- | --- |
| `Derailment` | a bogie newly derailed |
| `ViolentRotation` | angular acceleration at least 1.25 rad/s² |
| `TrainJolt` | jolt at least 14 m/s³ with acceleration at least 1.5 m/s² |
| `HeavyBraking` / `HeavyAcceleration` | acceleration at least 3.5 m/s² (braking when against travel above 1 m/s), or deceleration along travel of at least 3 m/s² above 1 m/s |

Each car has a 1 s cooldown. Up to 32 of the car's settled items wake, in set order, with 5% of the
negative acceleration in interior space, at most 1.5 m/s.

## Which items a wake reaches

`TrainItemWakePlanner`: candidates are settled items on the same car, ordered by their bottom, then
NetId. Support goes upward only: an item is supported when its bottom is within 0.12 m of the
support's top and they overlap by at least 0.01 m on X and Z. At most 32 items, 8 deep.

## The grant

For each item still settled, `ServerItemSpatial.TryBeginTrainWake`: no open lease, the record in
`TrainInterior` with a car, and a simulator from `TrySelectTrainWakeSimulator`: a `ReadyForItems`
player on the car or within 192 m who knows and acknowledged the item (the host's own player needs it
active). The preferred player first, then players on the car, then the nearest. The first item's
simulator is preferred for the rest.

The lease's baseline is the committed state if still current, else the record; a new epoch, phase
`Sliding`, the velocity capped at 6 m/s, reason `train-wake:<reason>`. The simulator applies the
velocity. The settled record is removed, and the item runs the ordinary lease flow until it settles
and registers again. Timeout and disconnect transfer each item's lease on its own.

With no eligible simulator, or no server object for the item, the wake waits: retried every 0.5 s,
dropped after 10 s (`train-item.wake-expired`), and the item stays glued. Any other refusal
(`item-already-active`, `item-not-settled-in-train`, `host-required`) drops the wake without an event
and keeps the record.

A dedicated host never simulates a wake: it isn't a player.

## Events

Category `train-item-wake` ([observability.md](../../observability.md)):
`train-item.settlement-registered`, `support-removed`, `wake-witness-accepted`,
`wake-witness-rejected`, `wake-granted`, `wake-pending`, `wake-expired`, `car-motion-wake`,
`motion-baseline-reset`. The spatial manager adds `item.spatial-train-wake-granted` and
`item.spatial-lease-transferred`. An invalid witness is only an event: nothing goes back to the
client.

## Not built

- A dvmp scenario for wakes. The Train Item Lab that exercised it was removed with the in-game test
  system (f510e8e); its hooks are in git history (#59). `train-items` only checks that a dropped or
  thrown item stays in a moving cab.
- Tuning from captures; a `wake-truncated` event when 32 is hit; a prefab-bounds catalogue.
- Moving a woken pile's leases as one; directional wakes; client prediction of ungluing; continuous
  collision detection for active items.
- Forcing interest for a wake's simulator: a client must already know the item.

## Known issues

From the code, not run in game (2026-10-23):

- A player who gets a glued item later (a Create) or applies a convergence correction has a live,
  non-kinematic copy ([item-spatial-physics.md](item-spatial-physics.md#known-issues)).
- A client has a committed state only for items it saw settle, so a late joiner's throws can't wake
  items that settled before it arrived.
- Car motion is measured in world space, so centripetal acceleration on a curve counts: 3.5 m/s² is
  30 m/s on a 260 m radius.
