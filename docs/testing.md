# Multiplayer testing

The multiplayer implementation now has a Unity-free core test boundary in addition to the
in-game tests run with dvmp.

## Test layers

### Pure unit tests

`Multiplayer.Core` targets `netstandard2.0` and must not reference Unity, Derail Valley,
LiteNetLib, or a running game process. Production code delegates canonical authority decisions
to this library, so tests exercise the same transition rules used by the host.

Run the suite with:

```powershell
dotnet test Multiplayer.Core.Tests\Multiplayer.Core.Tests.csproj
```

The initial item-authority suite verifies:

- one canonical item per nonzero NetId;
- exactly one placement and placement player at a time;
- accepted operations increase the revision exactly once;
- rejected operations leave the canonical state unchanged;
- revisions cannot wrap around;
- pickup by another player never transfers persistent ownership;
- a current possessor blocks another player's mutation;
- owner recall retains the original essential-item claim slot;
- owner recall clears `Dropped` and `Stolen` without changing NetId;
- non-owner and stale-revision operations are rejected.

The suite also covers:

- every pair of `Dropped`, `Thrown`, `InInventory`, `InHand`, `Attached`, and `Removed`
  send-state projections;
- same-state holder changes and attached car/end changes;
- suppression of fields that are not meaningful for the current state;
- FIFO pending-snapshot application;
- duplicate and stale revision rejection while deferred;
- explicit bounded-queue overflow without silent delta eviction;
- full snapshots superseding older deferred deltas;
- legacy revision-zero queue ordering;
- essential-claim preservation, reconstruction, restoration, moved-slot rejection, and occupied-slot rejection through a mocked `IInventoryView`;
- repeated pickup/drop cycles and a deterministic 2,000-operation mixed-transition invariant run.
- recipient completion with zero, one, and multiple clients, exact timeout boundaries, recovery,
  stable multi-client discontinuity selection, and validation rejection;
- temporary compatibility-adoption token/item uniqueness, policy rejection, unknown and duplicate
  result handling, invalid accepted mappings, and host token idempotency scoped per authenticated
  player; remove these tests with the compatibility bridge after all producers become host-created;
- all 16 combinations of inventory/world/lost-and-found/container storage membership normalized
  to one target without silent overlap;
- local hand, local inventory, remote hand, remote inventory, missing-player, and invalid remote
  projection outcomes.
- collision-safe item, train, station, and player NetId allocation, explicit reservations,
  release/reuse, byte exhaustion without wrapping to zero, duplicate-release rejection, and a
  10,000-operation live-ID stress run;
- exact 100 metre interest entry and three-second hysteresis boundaries, known/dirty tick catch-up,
  dirty-vs-full-sync selection, special-item create suppression, and known-item destroy targeting;
- zero-value full syncs, host/client dirty tracked-value composition, server-authoritative filtering,
  duplicate/blank key rejection, unknown-key merge plans, and exhaustive dirty/authority combinations.
- owner-only cold-container authorization, stale revisions, idempotent deposits, transactional
  withdrawal commit/abort, within/between-container moves, occupied and invalid slots, preserved
  mixed ownership, cycle/depth/node/quota limits, detached-state limits, save normalization,
  malformed graph rejection, and versioned bounded binary save round-trips.

### Protocol fixture tests

`Multiplayer.Protocol.Tests` targets .NET Framework 4.8 and exercises the actual production
`ItemUpdateData` serializer/deserializer and debug packet projector against the installed Derail
Valley/Unity value types:

```powershell
dotnet build Multiplayer\Multiplayer.csproj -c Debug
dotnet test Multiplayer.Protocol.Tests\Multiplayer.Protocol.Tests.csproj -c Debug
```

The fixtures cover `Dropped`, `Thrown`, `InHand`, `InInventory`, `Attached`, `Create`, `Destroy`,
`ObjectState`, and `FullSync`; every supported tracked-value primitive; complete payload consumption;
wire-envelope authority fields; unsupported value rejection; and the absence of recursive Unity
properties from decoded JSON. It also round-trips all cold-container browse/mutation request and
result packet shapes, including compact handles and 16-byte operation IDs. The production assembly must be built first because this suite tests
the real mod binary rather than a copied packet schema.

### In-game tests with dvmp

Anything that depends on the real game (physics, inventory, trains, joining, saving) is tested in
two real game instances driven by **dvmp** (see `DevTools/README.md`): `dvmp_up` brings up a
joined host and client, `dvmp_eval` runs C# in either game, `dvmp_desync` compares the two
peers' state, and `dvmp_soak` drives random item interactions.

Repeatable scenarios live in `DevTools/Scenarios/<name>/run.sh` (exit code 0 = pass; each
script's header lists its variables). `DevTools/Scenarios/gate.sh` runs the regression gate
against a running pair (`ONLY="a b"` for a subset) and summarises:

- Items: `train-items` (items in a moving cab; dropping and throwing), `lost-and-found`,
  `recall` (`TAKEN=1`: recall from another player's hands), `containers` (deposit, reopen,
  withdraw a stateful item; another player can't browse it), `rejoin` (`CRASH=1`), `save-reload`,
  `inventory-persist` (a client's inventory and hand across a host save).
- Trains: `manual-uncouple` (`MODE=recouple`, `LAG=`), `uncouple-moving` (`RIDE=1`, `LAST=`),
  `couple-impact` (coupling as the buffers bounce; `WHO=`, `CHAIN=1`, `HITCH=`), `fps-drop`
  (client at low frame rate).
- Jobs: `jobs` (take a job at the validator, deliver, hand the booklet in, bank the payment;
  `ACCEPT=`/`HANDIN=` choose which player does each).
- Also in the gate: `shop` (client purchase and cancel through the host), `borrowed-disconnect`
  (a player leaving with someone else's item), `host-stall` (host at about 2 fps while a braked
  train runs at speed), `client-derail` (a derail on one client only), `player-soak` (both
  players moving about for minutes; their views of each other must not freeze).
- Outside the gate: `station-load` (frame times arriving at a station).

Run the gate after any change to item sync, inventory, persistence, trains or joining. Old
runtime-test coverage that still needs rebuilding as dvmp scenarios is listed in
`DevTools/Scenarios/PORTING.md`.

### Unity adapter and in-game acceptance tests

Unity-facing code remains responsible for projecting an accepted pure state into real objects:
inventory slots, remote hands, storage membership, transforms, rigidbodies, renderers, and scene
lifecycle. These adapters need focused mocks where practical and two-process in-game tests where
base-game behaviour matters.

The pure state machine determines **what is allowed**. Unity adapters determine **how the accepted
state is represented**. Do not duplicate authority rules in an adapter.

## Next high-value extractions

1. Unity adapter tests for storage and remote-hand action execution.
2. Inventory event projection behind a richer mock `IInventoryView`.
3. Scene streaming/cache reconciliation adapters.
4. Rigidbody/throw application adapters with a mock physics surface.

Every multiplayer bug fix that changes pure transition behaviour should first gain a failing unit
test. Unity-only failures should gain an adapter test or a named in-game acceptance scenario and
an observability assertion.
