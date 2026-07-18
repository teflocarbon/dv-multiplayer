# Multiplayer testing

The multiplayer implementation now has a Unity-free core test boundary in addition to the
existing observability self-tests and in-game acceptance tests.

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

### Cold-container in-game acceptance pass

Run this with host and client before treating the implementation as release-ready:

1. Put ordinary items into every container type from both host and client inventories; close and
   reopen each container and verify no contained Unity items remain active while closed.
2. Move items between slots and between nested folder/registrator/briefcase/toolbox/crate views.
   Confirm incompatible moves, full slots, cycles, and stale simultaneous actions are rejected.
3. Nest to the configured depth boundary and verify the next level is rejected without losing any
   shell or child item.
4. Withdraw on host and client. Verify exactly one new NetId appears, the item enters the correct
   inventory, and the cold edge disappears only after materialization succeeds.
5. Deposit and withdraw an item owned by the other player. Verify its persistent owner never changes
   and the row is red for the container owner.
6. Put a populated container in Lost and Found, retrieve only its shell, then open it and verify the
   same direct contents and revisions remain.
7. Save/reload with nested populated containers in inventory, world, and Lost and Found. Verify UUIDs,
   slots, detached state, owners, and child edges survive without duplicate physical objects.
8. Disconnect/reconnect the client while a container is open and repeat one mutation; verify stale
   UI subscriptions do not intercept an ordinary or foreign-owned container.
9. Exercise both desktop drag/drop and VR container controls.
10. Inspect the F12 Containers page and confirm every mutation advances only the affected graph
    revisions and never exposes persistent UUIDs in routine packet payloads.

### Observability component tests

The existing debug-client self-test covers protocol contracts, packet projection, and
replication-recipient completion:

```powershell
DebugRemoteClient\bin\Debug\net48\Multiplayer.DebugClient.exe --self-test
```

These cases should gradually move into normal test projects as their production components are
extracted from the executable assembly.

### Debug-only runtime tests

Build and deploy the mod in `Debug`, start the host and client with the debug system and runtime
test harness enabled, then launch the combined dashboard. Its **Runtime tests** page discovers the
catalogue from each live process and requires an explicit target before executing a command.

The initial catalogue contains:

- `runtime.self-check`;
- `player.teleport` using absolute-coordinate parameters;
- `item.pickup` using `{"netId":"123"}`;
- `item.drop` with an optional NetId assertion;
- `item.throw` with an optional NetId and optional `directionX/Y/Z`;
- `inventory.inspect`, which reports every local slot including dropped/reserved silhouettes and
  distinguishes a normal visible slot from internal inventory membership;
- `inventory.prefab-catalog`, with optional `filter` and `limit` parameters;
- `inventory.fixture-create`, `inventory.fixture-place`, and `inventory.fixture-destroy`, which
  are host-only, authority-backed operations for disposable test items;
- `inventory.local-place`, which moves an existing local representation to `inventory`, `hand`, or
  `world` through DV's inventory and grabber methods;
- `scenario.cold-container-round-trip`, which makes the dashboard create a client-owned crate and
  compatible item on the host, waits for their client inventory projections, invokes DV's real
  desktop quick-move path in both directions, checks NetId retirement/materialization, slot, owner,
  prefab, detached state and graph-backed view revisions, then retires both disposable fixtures and
  verifies the client has no surviving fixture NetIds or dead inventory slots;
- `scenario.cold-container-foreign-rejection`, which auto-selects a compatible foreign-owned item
  created by the host in the acting player's inventory and verifies the server rejection leaves the
  item slot, representation, container revision, and row count unchanged.

### Cold-container runtime regression suite

Run every `scenario.cold-container-*` entry against the client target. Each scenario owns its host
fixtures, captures both processes under one run ID, and must finish with `cleanupClean=true`. The
dashboard coordinator then verifies that neither process retains a fixture-tagged inventory item or
Unity representation.

| Scenario | Runtime invariant |
| --- | --- |
| `scenario.cold-container-round-trip` | A normal quick-move deposit retires the physical NetId; withdrawal allocates a fresh NetId and restores slot, owner, prefab, and detached state. |
| `scenario.cold-container-stateful-round-trip` | Item-specific save data survives cold serialization and materialization. |
| `scenario.cold-container-reopen-round-trip` | A cold row remains authoritative while the physical container UI is closed and reopened. |
| `scenario.cold-container-move-within` | Moving a cold row changes its slot and revision without materializing it. |
| `scenario.cold-container-stale-revision` | A stale mutation is rejected without graph damage, followed by a successful fresh-revision withdrawal. |
| `scenario.cold-container-invalid-withdraw-slot` | An invalid inventory destination is rejected without losing the cold record, followed by recovery to the original slot. |
| `scenario.cold-container-incompatible-rejection` | Runtime-derived DV compatibility rejects an invalid item and leaves both projections unchanged. |
| `scenario.cold-container-repeated-round-trip` | Repeated deposit/withdraw cycles preserve identity data, advance revisions, and never immediately reuse a retired NetId. |
| `scenario.cold-container-foreign-rejection` | A player cannot deposit an item owned by another player. |
| `scenario.cold-container-private-browse-rejection` | Possession of another player's shell does not disclose its cold rows or activate a private view. |
| `scenario.cold-container-nested-cycle-guard` | A Folder can become a cold child of a Registrator, child browsing works, recursive insertion is rejected by compatibility/cycle guards, and the original edge survives. |

The nested runtime case intentionally accepts either `Incompatible` or `CycleDetected` for the
reverse edge. DV's strict container hierarchy normally rejects a wider parent before the graph's
cycle check. `ColdContainerGraphTests.NestedMove_CannotPlaceContainerInsideItsDescendant` exercises
the exact `CycleDetected` branch with an allow-all compatibility policy. Snapshot encoding,
malformed-load rejection, quotas, depth, detached-state budgets, operation replay, and transactional
graph behavior remain covered by `Multiplayer.Core.Tests`; packet contracts remain covered by
`Multiplayer.Protocol.Tests`.

### Lost-and-Found runtime regression suite

Run every `scenario.lost-and-found-*` entry against the client target. These scenarios use the
single-item fixture orchestration: the dashboard creates exactly one tagged item, waits for its
client inventory projection, captures the host/client network window, and retires that item from
inventory, world, or virtual Lost and Found during unconditional cleanup. Every run must finish
with `cleanupClean=true` and no tagged Unity representation, inventory slot, or registry entry.

| Scenario | Runtime invariant |
| --- | --- |
| `scenario.lost-and-found-automatic-round-trip` | A personal item dropped beyond both players and left past the grace period is collected, privately listed with a compact handle, then restored through the real client/server retrieval API to its original slot and owner. |
| `scenario.lost-and-found-stale-revision-recovery` | A stale revision is rejected without consuming the record; a current-revision retry succeeds. |
| `scenario.lost-and-found-full-inventory-recovery` | Server-side slot planning rejects full-inventory evidence without losing the item, then accepts a valid retry. |
| `scenario.lost-and-found-essential-star-recall` | The base-game essential-item Return/star route accepts a stale silhouette projection and revives the exact reserved slot. |
| `scenario.lost-and-found-repeated-round-trip` | Two collection/retrieval cycles advance authority revisions, allocate fresh compact handles, preserve ownership, and leave no stale list entry. |
| `scenario.lost-and-found-owner-nearby-protection` | A personal world item within the owner-protection radius remains materialized beyond the collection grace period. |
| `scenario.lost-and-found-other-player-nearby-protection` | The owner can leave the area while another player near the item prevents collection. |
| `scenario.lost-and-found-container-shell-round-trip` | A player-owned storage-container shell is collected and retrieved without losing its runtime identity or ownership. |
| `scenario.lost-and-found-nonpersonal-exclusion` | Ordinary world junk is never promoted into a player's virtual Lost and Found. |
| `scenario.lost-and-found-private-owner-list` | A collected item owned by the host disappears from the acting client's interest without being disclosed in that client's list. |
| `scenario.lost-and-found-unknown-handle-rejection` | An unknown compact handle is rejected and cannot mutate an unrelated inventory fixture. |

The deterministic `LostAndFoundTests` suite separately covers every policy branch, exact distance
and grace boundaries, unknown positions, interaction/container exclusions, license exclusion,
registry identity and privacy indexes, durable UUID/compact-handle separation, stale canonical
invalidation, silhouette precedence, slot fallback, full inventory, wrong owner, stale revision,
and unavailable inventory. `LostAndFoundPacketTests` verifies list, request-evidence, and result
contracts independently of Unity.

### Adding runtime scenarios

Runtime scenarios are self-registering Debug-only classes. Add one file under
`Multiplayer/Debugging/RuntimeTests/Scenarios`, implement `IRuntimeTestScenarioDefinition`, and
provide its `RuntimeTestDescriptorDto` plus `Execute` method. The registry discovers implementations
from the Multiplayer assembly, adds their descriptors to the runtime catalogue, and dispatches their
execution factories. No catalogue or runtime-agent switch edit is required.

The descriptor is also the dashboard's orchestration contract. Set `IsScenario`,
`ScenarioOrchestration`, `FixturePolicy`, `CleanupPolicy`, and `FixtureItemOwnership`; the dashboard
uses those typed fields to arrange the host/client fixtures, run the selected client step, capture
the run, and verify cleanup. It does not recognize individual scenario IDs. Scenarios that share an
existing orchestration and fixture policy are therefore one-file additions. A genuinely new fixture
shape or orchestration adds one reusable coordinator policy, after which scenarios using it remain
one-file additions.

Use `RuntimeTestScenarioContext` and `RuntimeTestScenarioRunner` inside the execution method so
assertions, resource tracking, cleanup, phases, and `FailedDirty` reporting stay consistent. Keep
feature-specific mechanics in a shared driver when multiple scenario definitions exercise the same
system, as the cold-container definitions do.

For pickup, manually arrange the player so the normal desktop crosshair is aimed at the requested
item. The test refreshes DV's real raycaster and fails on a target mismatch; it does not force the
item into the hand. Drop and throw require a genuinely held networked item. VR reports
`Unsupported` until a VRTK-specific driver exists.

The round-trip scenario is self-contained when invoked through the dashboard. It accepts optional
`containerPrefabName` and `itemPrefabName` parameters (defaulting to `ItemContainerCrate` and
`lighter`); the dashboard creates both fixtures on the host for the selected client and supplies
their NetIds to the client-only gameplay step. A direct process-level invocation can still accept
`shellNetId` and `itemNetId` for lower-level diagnosis. The foreign rejection case uses a disposable
host-owned item held by the selected client. `assertDetachedState` defaults to `true` for the owned
round trip.

Fixture creation requires `prefabName` and accepts `ownerPlayerId`, `holderPlayerId`, `placement`,
and optional `slot` or absolute `x/y/z`. Keeping owner and holder different creates the deliberate
foreign-possession setup needed by rejection tests. Creation returns a fixture token; authoritative
placement and destruction reject ordinary save items that were not created by this runtime agent.
For exact local slot arrangement, follow the host create/placement command with
`inventory.local-place` on the holder's process. These commands exist only in Debug builds.

Scenario commands automatically begin a coordinated dashboard capture using their run ID. The
self-contained round trip reports every host arrangement, client action, host cleanup command, and
final client inventory verification as
a process stage. Results contain phase records, timed assertions, resource-ledger state, operation
UUIDs, fixture NetIds, `cleanupClean`, and capture paths. Capture filenames include the run ID so
repeating a scenario within the same game session never overwrites an earlier run's artifacts.
Use an operation UUID to correlate the acting client request/result with the host authority event,
or query `/api/events/query` by `testRunId`, `testCaseId`, `testPhaseId`, or `testStepId`. A scenario
that cannot verify cleanup ends as `FailedDirty` rather than an ordinary failure.

The dashboard command is a parent barrier over its selected process or processes. Inspect the
per-process results and test-scoped event stream before treating a pass as a multiplayer scenario
pass. Every command result includes a `runtime` block containing the shared CalVer build number,
role, player ID/name, session and process IDs, server/client state, and active scene so copied
failure JSON remains attributable. The build number identifies the exact mod/dashboard source
build; the semantic mod version remains the separate compatibility identity.
These routes, DTOs, agents, drivers, settings, and embedded test UI are excluded from Release builds.

The dashboard journals the complete parent result for the latest 250 runs as atomic JSON files in
`%LOCALAPPDATA%\DVMultiplayer\test-runs`. `GET /api/runtime-tests/runs` returns summaries for the
history panel and `GET /api/runtime-tests/runs/{requestId}` returns either the live coordinator
snapshot or the persisted result. `runtime-test.run-updated` is emitted over the normal SSE stream
whenever status, phase, step, error, or process status changes. The Tests panel uses that event for
live progress while preserving text selection and scroll position. Its Network activity section
separates packet wire activity from item-replication lifecycle events, and constrains both to the
run's start/completion window. High-frequency and routine background packets are hidden from the
focused packet list but remain available in All correlated events and the capture artifacts.

The dashboard's **Environment** page automates the two-instance prerequisite. Enter the game
executable and connection settings, then choose **Launch host + client**. The supervisor waits for
host save load, starting items, loopback connection, and listening server before it starts the
client. Treat the environment as usable only once its stage is `Ready` and the host/client
readiness snapshots report completed network loading and loaded starting items.
Managed windows launch minimized without activation by default, preventing cursor capture and
accidental camera input while Unity continues running in the background.

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
