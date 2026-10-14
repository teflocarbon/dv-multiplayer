# Derail Valley Developer and Debug Console Logic

This document catalogues Derail Valley's built-in developer/debug commands and, more importantly,
the game-owned APIs behind commands that are useful to the DVMP runtime harness. It is source
research, not a proposal to expose or depend on the game's terminal parser.

## Availability boundary

The `Dev.*` command family is registered after a scene load only when all of the following are true:

```text
Application.isPlaying
DevUtil.IsDevMachine()
Terminal exists
Terminal.Shell is initialized
```

`RegisterDevCommands` and most command handlers are private. A normal retail installation should
not be expected to expose these commands. The runtime harness should call the underlying public game
APIs from its own debug-only commands rather than spoofing `DevUtil.IsDevMachine`, reflecting into
the terminal, or parsing console text.

The separate attribute-registered `Debug.*` family does not use this same explicit registration
gate in the inspected source, although individual commands still depend on the relevant runtime
singletons being present.

## Train and fixture commands

| Command | Arguments | Underlying behavior | Harness value |
| --- | --- | --- | --- |
| `Dev.LocoStartEngine` | none | Calls `StartupHelper.Startup(PlayerManager.Car)` | Canonical safe-start entry point |
| `Dev.LocoSimulationTimeMultiplier` | multiplier | Sets `simTimeMultiplier` on every active `SimController` | Useful research control; too global for ordinary fixture use |
| `Dev.LocoClampResource` | resource, maximum factor | Resolves the player's car resource container and applies a cap | Source pattern for fixture-local resource supervision |
| `Dev.LocoDepleteResources` | optional resource | Depletes one or all resources on the player's car | Negative tests |
| `Dev.LocoRefillResources` | optional resource | Refills one or all resources on the player's car | Arrangement and recovery |
| `Dev.LocoDamageParts` | optional part | Damages one or all supported damage groups | Negative tests |
| `Dev.LocoRepairParts` | optional part | Repairs one or all supported damage groups | Arrangement and cleanup |
| `Dev.TeleportTrainToTrack` | full display track ID | Resolves the player's trainset and a station track, then moves the consist | Strong source for deterministic track placement |
| `Dev.ListCarsInfo` | optional filter/car ID | Enumerates live train cars and key state | Catalogue/diagnostics reference |
| `Dev.CarDamageAndResourcesInfo` | optional car ID | Reports damage and resources for one car | Assertion/telemetry reference |
| `Dev.TeleportToCar` | car ID | Teleports the player to a selected car | Player arrangement reference |
| `Dev.TeleportToStation` | station ID | Teleports the player to a station | Location arrangement reference |
| `Dev.Explode` | optional car ID | Explodes the current or selected train | Destructive negative-test reference only |

### Confirmed train startup path

`Dev.LocoStartEngine` contains no alternative engine manipulation. It calls
`StartupHelper.Startup(PlayerManager.Car)`. `DV.CommsRadioStartup.OnUse` calls the same helper after
selecting a locomotive with the radio. This convergence makes `StartupHelper` a much stronger
fixture API than either command handler.

### Confirmed consist-to-track algorithm

`Dev.TeleportTrainToTrack` resolves a destination by comparing the requested text with
`railTrack.LogicTrack().ID.FullDisplayID`, obtains all cars in `PlayerManager.Car.trainset`, and runs
the private `Console.MoveCarsCoro`.

`MoveCarsCoro` then:

1. finds cars already fully occupying the destination track;
2. attempts to move those cars toward the track's second kinked point using
   `TrainCarTeleporter.TeleportTrainset`;
3. retries once when any bogie still resolves to another track;
4. chooses the middle point of the destination track's kinked point set;
5. teleports the requested consist there with `TrainCarTeleporter.TeleportTrainset`;
6. verifies every moved bogie resolves to the destination `RailTrack` and logs whether the precise
   placement succeeded.

The harness should not call the private coroutine. It can implement a fixture-owned variant around
the public `TrainCarTeleporter.TeleportTrainset` API, with explicit occupancy policy, fixture IDs,
acknowledgements, and cleanup. The existing method also demonstrates that checking only the
transform is insufficient: final bogie-to-track identity is the real success condition.

Inspection of `TrainCarTeleporter.TeleportTrainset` confirms that it performs considerably more
than transform movement: it preserves consist orientation, disconnects couplers and MU cables,
searches for contiguous space using car-sized collision boxes, moves every car to a track, calls
`TrainCarInteriorPhysics.SyncPosition`, synchronizes Unity transforms, and then reconstructs the
coupling/MU topology. See the train-fixture research document for the full contract and wrapper
requirements.

## Player, world, and streaming commands

| Command | Arguments | Behavior or useful API |
| --- | --- | --- |
| `Dev.MarkPlayer` | optional slot 0-9 | Saves absolute position, orientation, time, and weather to a debug mark file |
| `Dev.RecallPlayer` | optional slot 0-9 | Restores a mark through `PlayerManager.TeleportPlayer` and also changes time/weather |
| `Dev.SetNearLoaderRange` | load, unload range | Changes near world-streaming ranges |
| `Dev.SetFarLoaderRange` | load, unload range | Changes far world-streaming ranges |
| `Dev.OriginShiftRange` | optional range | Reads or changes the origin-shift threshold |
| `Dev.OriginShiftNow` | none | Temporarily reduces the threshold to force an origin shift |
| `Dev.AdvanceTime` | seconds | Advances game time |
| `Debug.TeleportToPoint` | absolute x, z | Teleports to an absolute map point |
| `Debug.TeleportToPointNormalized` | normalized x, z | Teleports using normalized map coordinates |
| `Debug.PrintPlayerPosition` | none | Prints the player's current world coordinates |
| `Debug.TrainOptimizationStatus` | none | Reports sleeping versus awake train cars |
| `Debug.LoadingAlertsToggle` | none | Toggles world-loading and car-spawning notifications |

The mark/recall commands are not suitable as-is for isolated scenarios because they persist files
and alter time/weather. The harness's existing direct teleport command remains the cleaner design.

## Item and inventory commands

| Command | Arguments | Purpose |
| --- | --- | --- |
| `Dev.SpawnItem` | optional prefab and count | Spawns item prefabs near the player |
| `Dev.SpawnMoney` | amount | Instantiates banknotes under `WorldMover.OriginShiftParent` |
| `Debug.SleepAllItems` | none | Forces all item/prop rigidbodies to sleep |
| `Debug.DestroyAllItems` | none | Destroys all props/items and is explicitly game-breaking |
| `Debug.DestroyBuggedObjects` | none | Destroys objects with invalid/NaN positions |
| `Debug.InventoryEventDebug` | none | Toggles inventory event logging |

These global destroy/sleep commands are useful clues but are not valid scenario cleanup. Runtime
tests must continue tracking and deleting only fixture-owned representations across world,
inventory, hand, cold storage, and server records.

## Save-control commands

| Command | Arguments | Behavior |
| --- | --- | --- |
| `Debug.AutoSavingEnabled` | optional boolean/int | Reads state or writes `SaveGameManager.disableAutosave` |
| `Debug.AutoSaveNow` | none | Calls `SaveGameManager.Save(SaveType.Auto, null, true)` |
| `Debug.QuickSaveNow` | none | Calls `SaveGameManager.Save(SaveType.Quick, null, true)` |
| `Debug.ManualSaveNow` | none | Calls `SaveGameManager.Save(SaveType.Manual, null, true)` |

The autosave command confirms there is a direct in-game control:

```text
disableAutosave = requestedValue <= 0
```

For a configured clean testing baseline, the debug harness should set
`SaveGameManager.disableAutosave = true` after the selected manual save is loaded, expose that state
in readiness, and restore the previous value when relinquishing automation control. This prevents a
failed fixture from contaminating the very save used to start the next run. It does not replace the
existing exact-manual-save selection and verification requirements.

## Quick Tutorial commands

The `Dev.QT.*` prefix means **Quick Tutorial**, not quality test or an internal unit-test suite.
These commands invoke `QuickTutorialHost` and `QuickTutorialFactory`:

```text
Dev.QT.StartLoco
Dev.QT.StartCoupling
Dev.QT.StartRerailing
Dev.QT.StartDebt
Dev.QT.StartManual
Dev.QT.StartQT
Dev.QT.StartHandcar
Dev.QT.StartClear
Dev.QT.Abort
```

They are still valuable research because the tutorial factories arrange locomotives, coupling,
rerailing, and handcars through game-owned workflows. They should be inspected for reusable spawn,
track-selection, and cleanup entry points, but not treated as pre-existing automated tests.

The factory inspection confirms this value. It contains game-authored readiness conditions for
on-rails state, speed, grade, damage, resources, range, and LOD; reusable control/fuse/port/engine
steps; a complete start-drive-stop diesel sequence; and detailed chain, coupler, brake-hose,
angle-cock, and MU-cable workflows. It also uses constrained services for one-track spawning,
rerailing, and single-car deletion. The harness should borrow these invariants and underlying APIs,
not launch the human-facing tutorial phases.

The inspected wrappers are deliberately narrower than their names initially suggest:

- `LocoControlOverrideStep` observes a control range; it does not set the control;
- `LocoResetStep` performs a real cold/neutral reset and explicitly turns several fuse/control
  values off, so it is not a ready-to-drive helper;
- the `SingleRail*` and `SingleCarDeletionService` classes only set `SingleAllowedTrack` or
  `SingleAllowedCar` on the corresponding comms-radio mode for the service lifetime.

These are useful fidelity-test constraints and assertion patterns. The actual fixture actions must
come from `StartupHelper`, control `Set` methods, `TrainCarTeleporter`, and the underlying radio-mode
operation methods.

Those underlying paths are now confirmed:

- crew-vehicle confirmation calls `CarSpawner.SpawnCrewVehicle` with the selected livery, track,
  point, direction, and optional garage spawner;
- rerail confirmation calls `TrainCar.Rerail` with an origin-shift-corrected local position;
- deletion either returns a garage-backed car through `GarageCarSpawner.ReturnCarHome` or calls
  `CarSpawner.DeleteCar`, clears invalid manual-delete references, and deactivates any surviving car
  and interior objects;
- `BaseControlsOverrider.SetNeutralState` performs a cold/safe control reset and may propagate
  through a consist; it is not interchangeable with `StartupHelper.Startup`.

The radio handlers also include money, garage, cargo, job, difficulty, current-player-car, overlap,
and availability policy. Fixture arrangement should call the correct underlying game action on the
host with fixture-scoped validation, rather than driving the economic UI state machine unless the
scenario explicitly tests that UI workflow.

DVMP already patches these confirmations and sends work-train, rerail, and deletion requests to the
host. That is the correct authority direction, but the current RPCs are gameplay/economy workflows,
not fixture transactions. The server-side audit found incomplete rejection responses/refunds in the
work-train request, incomplete authoritative rerail validation, some deletion predicates enforced
only by the UI path, and unresolved actor-wallet attribution through the host's inventory singleton.
The train-fixture research document records the detailed gaps.

The current `ClientboundMoveTrainPacket` also declines `IsTeleporting` packets. Consequently,
`TrainCarTeleporter.TeleportTrainset` cannot yet serve as a complete multiplayer fixture move without
a new client apply/acknowledgement sequence.

## Other registered developer commands

The inspected source also registers commands for money, debts, jobs, licenses, feature flags,
rendering, terrain, environment sound, light-data rebuilds, comms-radio cheat mode, player speed,
time scale, bed/time testing, garage unlocks, and cab-light controls. They should be catalogued in
detail only when a runtime scenario needs the corresponding subsystem.

Especially relevant future entries include:

- `Dev.JobCompleteNextTask` and `Dev.RefreshJobsAtTheStation` for job/world-item scenarios;
- `Dev.EnableCommsRadioCheatMode` for radio-driven interaction research;
- `Debug.ToggleTrackIndicators` for visual track-ID debugging;
- `Debug.Telemetry` and `Debug.SaveTelemetry` for derail/physics diagnosis;
- `Physics.Toggle` as a diagnostic only, never as a normal fixture arrangement step;
- `Debug.RestartScene` and `Debug.ReturnToMainMenu`, which are not safe substitutes for clean
  host/client process restarts.

## Next source targets

The highest-value follow-up inspections are:

1. `CarSpawner.SpawnCrewVehicle` and the broader existing DVMP fixture-capable car-spawn path;
2. `CarSpawner.DeleteCar`, `GarageCarSpawner.ReturnCarHome`, and registry cleanup behavior;
3. the concrete control `Set` implementations and their event/network propagation;
4. the exact implementations behind `Dev.TeleportToCar` and `Dev.TeleportToStation` for reliable
   player placement and cab/interior anchors;
5. `ResourceContainerController` refill/clamp methods for fixture-local resource sustainment.
