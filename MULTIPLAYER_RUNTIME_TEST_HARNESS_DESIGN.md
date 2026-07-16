# Multiplayer Runtime Test Harness Design

> Status: debug-only foundation, explicit command catalogue, dashboard coordinator, non-VR
> interaction driver, reusable scenario lifecycle, and the first cold-container quick-move
> round-trip/rejection scenarios implemented. Disposable fixture spawning and broader scenarios
> remain in progress.

> Build policy: the runtime harness, test catalogue, gameplay drivers, mutating HTTP endpoints, and
> in-game test agent must be compiled only in `DEBUG` builds. They must not be present but disabled
> in a release assembly.

## Purpose

The existing pure and protocol tests cover canonical state machines, container graphs, packet
serialization, projection, and other Unity-free behavior. They cannot prove that Derail Valley's
runtime interaction code, Unity presentation, multiplayer authority, and host/client replication
agree during a real gameplay workflow.

Manual two-process testing is no longer sufficient as the inventory, ownership, recall, Lost and
Found, and cold-container systems become more interconnected. The runtime harness will execute
repeatable scenarios inside the real game, coordinate the host and one or more clients through the
combined debug dashboard, and retain only the packets and observability events associated with the
test run.

The harness supplements rather than replaces:

- pure `Multiplayer.Core.Tests` invariant tests;
- `Multiplayer.Protocol.Tests` packet fixtures;
- observability self-tests;
- a smaller manual desktop and VR acceptance pass.

## Existing foundation

The observability system already provides most of the transport and coordination foundation:

- every game process exposes an authenticated loopback HTTP/SSE endpoint;
- live sessions advertise role, player ID, the shared mod/dashboard CalVer build number, endpoint,
  and API token;
- the combined dashboard discovers and merges host/client sessions;
- the dashboard can broadcast authenticated commands to every runtime;
- events from every process use one shared debug protocol;
- replication operations and discontinuities are already correlated across sessions;
- captures retain bounded pre-roll and post-trigger events;
- entity snapshots expose canonical and local Unity state.

The test harness should extend these components rather than create a separate remote-control or
logging system.

## Implemented foundation (2026-07-16)

The first executable slice is now present in debug builds:

- runtime-test DTOs and event scope fields in `Multiplayer.DebugProtocol`;
- authenticated loopback endpoints for capabilities, command submission, status, and cancellation;
- a bounded, serialized `RuntimeTestAgent` queue drained from Unity `Update`;
- one active command per process, timeout/cancellation handling, and retained result snapshots;
- global run/case/phase/step scope enrichment for observability events emitted during a command;
- cached capability discovery that never accesses Unity from the HTTP worker thread;
- structural discovery of the nearest job validator, its `ItemUseTarget`, target collider count,
  and booklet printer spawn anchor;
- an explicit, non-reflective catalogue containing `runtime.self-check`, `player.teleport`,
  `item.pickup`, `item.drop`, and `item.throw`;
- `player.teleport`, which arranges position through `PlayerManager.TeleportPlayer` and verifies the
  absolute position after the game's reactivation frames;
- a non-VR item driver which refreshes the real game raycaster, enters pickup through
  `RequestStartInteraction`, and performs drop/throw with the same production ordering used by
  `GrabberInputHandler`;
- a dashboard-side authenticated HTTP client and coordinator with explicit process, role, or
  player targeting, independent child status polling, cancellation forwarding, and a parent
  barrier which cannot pass while any selected process remains queued or running;
- aggregate per-process status, result, and failure details in the shared response DTO;
- common runtime attribution on every result: shared build number, role, player identity,
  session/process identity, server/client state, and active scene;
- a Runtime tests dashboard tab for catalogue discovery, target selection, JSON parameters,
  execution, cancellation, and per-process results;
- an Actions-style run history backed by an atomic Local AppData journal, with SSE-driven live
  phase/step updates, assertion and cleanup summaries, correlated events, capture artifacts, and
  run-window packet/item-replication timelines with routine packet noise suppressed;
- loopback protocol self-tests covering unauthorized rejection and capability, enqueue, and status
  responses, plus coordinator self-tests covering multi-process barriers and ambiguous-target
  rejection.
- a reusable scenario context with dynamic phase/step scopes, bounded eventual assertions,
  reverse-order resource cleanup, and `FailedDirty` reporting when cleanup cannot be proven;
- automatic dashboard capture lifetime for `scenario.*` commands and event queries by test
  run/case/phase/step;
- cross-process cold-container operation correlation using the operation UUID;
- `scenario.cold-container-round-trip`, whose dashboard coordinator creates disposable client-owned
  shell/item fixtures on the host, invokes the real non-VR quick-move method on the client for
  deposit and withdrawal, verifies view revisions, representation retirement, fresh NetId
  materialization, slot, owner, prefab and detached state, then retires both fixtures and verifies
  their client inventory projections are absent;
- `scenario.cold-container-foreign-rejection`, which verifies an existing foreign-owned inventory
  fixture cannot mutate either its source slot or the cold container.

All contracts, routes, settings, the Unity agent, and dashboard client in this slice are enclosed in
`DEBUG` compilation guards. The harness setting also exists only in debug builds.

Current endpoint shape:

```text
GET  /api/runtime-tests/capabilities
POST /api/runtime-tests/commands
GET  /api/runtime-tests/runs/{requestId}
POST /api/runtime-tests/runs/{requestId}/cancel
```

The endpoint shape remains command-oriented. The dashboard coordinator turns one parent command
into explicitly targeted child commands without changing the main-thread queue in each game.

### Initial command parameters and limits

```text
runtime.self-check  {}
player.teleport     {"x": "...", "y": "...", "z": "...",             # absolute position
                     "yaw": "...", "tolerance": "0.5"}                  # optional
item.pickup         {"netId": "123"}
item.drop           {"netId": "123"}                         # netId optional
item.throw          {"netId": "123", "directionX": "0",     # all fields optional
                     "directionY": "0", "directionZ": "1"}
```

The pickup command deliberately does not teleport or aim the player. The requested item must be
the exact handler selected by the production `GrabberRaycasterDV` at execution time. A mismatch is
a failed gameplay action with both requested and raycasted NetIds in the result, rather than a
fallback to force-hold or direct parenting.

The initial item driver is desktop-only. In VR it reports `Unsupported`; it does not pretend that a
desktop grab proves the VRTK path. Drop and throw require a genuinely held networked item. The
coordinator requires an explicit target whenever more than one eligible runtime is connected,
unless the caller explicitly requests all sessions or a unique role/player.

## Architecture

```text
Combined dashboard
  RuntimeTestCoordinator
    host DebugRuntimeTestAgent
    client DebugRuntimeTestAgent
      runtime test catalogue
      gameplay test drivers
      coroutine runner
      assertion helpers
      resource/cleanup ledger
      test-scoped observability
```

The dashboard is the coordinator. Each game process is an independently reporting test agent.
Initially only one test run may be active across the combined dashboard.

## Fundamental fidelity rule

Every runtime test has three phases with different permissions:

> Arrange may use controlled internal state manipulation. Act must enter through the same gameplay
> boundary used by a real player. Assert must be read-only.

Examples:

- arranging a clean item by instantiating its prefab is acceptable;
- testing pickup by directly parenting that item to a hand is not acceptable;
- testing quick-move by calling the real non-VR quick-move handler is acceptable;
- testing a deposit by directly calling the cold graph's `CommitDeposit` is not a runtime test of
  inventory integration;
- cleanup may use controlled internal operations because cleanup itself is not the behavior under
  test.

This prevents the harness from proving that a test helper works while bypassing the Derail Valley
code where the actual regression can occur.

## Action fidelity levels

Every action step records the highest interaction boundary it exercised:

| Fidelity | Meaning |
| --- | --- |
| `DirectState` | Test-only arrangement or cleanup mutation |
| `GameplayMethod` | Real game behavior after physical input has been interpreted |
| `UIController` | The real inventory or screen transaction invoked by the UI |
| `PhysicalInput` | Actual keyboard, mouse, controller, or VR input |
| `ServerAuthority` | An authenticated request validated by the normal host handler |

Most automated desktop scenarios should use `GameplayMethod`, `UIController`, and
`ServerAuthority`. Physical-input automation is deliberately deferred because it is brittle and
adds little coverage once the correct post-input entry point is exercised.

The dashboard must display fidelity beside each step so a passing test does not imply more coverage
than it actually provides.

## Shared debug protocol additions

Add runtime-test contracts to `Multiplayer.DebugProtocol` so the dashboard and game processes use
the same schema:

```text
RuntimeTestDescriptorDto
  testId
  displayName
  category
  risk
  requiredRoles
  requiredCapabilities
  timeoutSeconds

RuntimeTestRunRequestDto
  runId
  testId
  phase
  parameters

RuntimeTestRunStatusDto
  runId
  testId
  status
  currentPhase
  currentStep
  cleanupStatus
  failureReason
  steps

RuntimeTestStepDto
  stepId
  displayName
  role
  fidelity
  status
  expected
  actual
  startedUtc
  completedUtc
```

Suggested authenticated runtime endpoints:

```text
GET  /api/tests
POST /api/tests/prepare
POST /api/tests/execute
POST /api/tests/cancel
GET  /api/tests/runs/{runId}
```

The combined dashboard should expose aggregate equivalents. It must target sessions by role and
player rather than blindly broadcast every phase when a scenario assigns different work to the host
and client.

## Unity main-thread boundary

HTTP requests are handled on worker threads and must never access Unity or Derail Valley objects.
Each runtime agent receives commands into a thread-safe queue. A component attached to the existing
debug runtime drains that queue from `Update` and starts test coroutines on the Unity main thread.

```csharp
internal sealed class DebugRuntimeTestAgent : MonoBehaviour
{
    private readonly ConcurrentQueue<RuntimeTestCommand> commands = new();

    private void Update()
    {
        while (commands.TryDequeue(out RuntimeTestCommand command))
            HandleOnMainThread(command);
    }
}
```

Only the queue and immutable status snapshots cross the thread boundary.

## Runtime test model

```csharp
internal interface IRuntimeTestCase
{
    RuntimeTestDescriptor Descriptor { get; }
    IEnumerator Run(RuntimeTestContext context);
}
```

`RuntimeTestContext` should provide:

- local role, player ID, shared run ID, case ID, phase, and cancellation state;
- named step scopes;
- immediate assertions;
- eventual frame- and network-tick-based assertions;
- gameplay drivers;
- test anchors and world movement helpers;
- entity snapshot and diagnostic attachment helpers;
- a resource ledger for cleanup;
- barrier-ready and phase-complete reporting.

Tests should be registered explicitly in a catalogue. Runtime reflection should not execute an
arbitrary type or method supplied by an HTTP caller.

## Gameplay test drivers

All access to Derail Valley interaction boundaries should be centralized behind an internal API:

```csharp
internal interface IGameplayTestDriver
{
    IEnumerator PickUp(ItemBase item);
    IEnumerator Drop(ItemBase item);
    IEnumerator Throw(ItemBase item, Vector3 direction);
    IEnumerator Equip(int inventorySlot);
    IEnumerator Unequip(int equippedSlot);
    IEnumerator QuickMove(int inventorySlot);
    IEnumerator MoveInventoryItem(int source, int destination);
    IEnumerator Use(ItemBase item, ItemUseTarget target);
    IEnumerator Press(ButtonBase button);
    IEnumerator OpenContainer(ItemContainer container);
    IEnumerator CloseContainer(ItemContainer container);
}
```

These signatures are illustrative. The final adapter methods must follow the decompiled call graph
and invoke the earliest practical method after input interpretation. Private game methods should be
resolved in one adapter with guarded `AccessTools` lookup or a narrowly scoped patch bridge, not
through scattered reflection in every test.

If an entry point cannot be resolved for the current game build, tests requiring it must report
`Unsupported` rather than silently fall back to direct state mutation.

### Relationship to Derail Valley's shipped testing remnants

Derail Valley ships some developer-oriented components but apparently not a complete callable test
runner. The harness may reuse their architectural patterns without depending on unavailable test
scenes or stripped callers:

- `GrabberInteractionHandlerMock` proves the non-VR grabber supports interface-based component
  substitution, but omits production validation and is unsuitable for gameplay-fidelity cases.
- `DV.Testing.TestSceneRig` demonstrates reduced-world fixture scenes, not assertion execution.
- `DV.Booklets.Testing` provides deterministic booklet fixture examples.
- `LocoSim.Implementations.Test` provides tick-sampled simulation diagnostics rather than unit-test
  assertions.
- VR fake controllers/interactables are live telegrab proxies and require the VRTK runtime; they
  are not headless VR mocks.

The debug harness remains our own orchestrated runner. It should call production gameplay
components for end-to-end cases and use substitutes only for explicitly labelled isolated tests.

`MousePositionHack.TryWarpCursorPosition` is available as an optional Windows-only input-emulation
tier for future pointer tests. It must not be the default because window focus, monitor layout,
DPI, cursor confinement, and external mouse movement make it nondeterministic. The normal harness
continues to invoke the earliest practical post-input game method.

## Entry-point research checklist

Before implementing broad scenarios, document the actual Derail Valley path for each action:

| Action | Questions to resolve |
| --- | --- |
| Pick up | Which non-VR interaction method begins a real grab? Which checks hover, reach, interaction permission, and current grabber state? |
| Drop | Which method performs a normal hand release and inventory silhouette transition? |
| Throw | Which method applies direction/force and fires the item events observed by multiplayer? |
| Equip/unequip | Which `Inventory`/UI calls exactly match slot selection, drag, and hotbar behavior? |
| Drag/drop | What is the first `InventoryUIController` transaction after pointer interpretation? |
| Quick-move | Confirm `InventoryViewNonVR.QuickMoveAction` parameters, validation, and all source/destination branches. |
| Item use | Use live `ItemTriggerEnterTarget` overlap/release where supported. Raycast use requires real cursor aim plus a scoped debug primary-input edge through `InsertItemIntoTargetHandler`; direct handler calls must be labelled lower fidelity. |
| Button use | `ButtonBase.Use()` is the logical gameplay boundary; physical-control tests must separately use `ButtonNonVR` through the grabber. |
| Container access | Which open/close path updates `ItemContainerRegistry.ActiveContainer` and providers in desktop and VR? |
| Player reposition | Use `PlayerManager.TeleportPlayer`, validate the anchor, normalize held/UI state first, and wait through the desktop reactivation frame plus end-of-frame. |
| Vehicle enter/leave | Which methods update player/car occupancy and the network projection consistently? |

For every researched entry point, record:

- declaring type and exact signature;
- desktop, VR, or shared applicability;
- callers and important preconditions;
- events and side effects it produces;
- whether calling it from a coroutine is safe;
- required setup objects;
- cleanup implications;
- game-version assumptions.

The findings may live in a dedicated Derail Valley runtime-interaction research document and feed
the stable gameplay-driver adapter.

## Test-scoped observability

While a test is active, every event published by that process should automatically include:

```text
testRunId
testCaseId
testPhaseId
testStepId
```

Only one active test per process is allowed initially, so the runtime does not need to enlarge
ordinary gameplay packet schemas. Packet send/receive hooks, authority events, item snapshots,
exceptions, and entity events are tagged from the active runtime test scope.

The host and clients receive the same `testRunId`. The dashboard can therefore filter its merged
event store to the exact run and step without searching unrelated traffic.

Each run should automatically start a bounded capture. A failed step should attach:

- the test-filtered event timeline;
- decoded packet summaries;
- relevant replication operations;
- final host and client entity snapshots;
- inventory, storage, authority, Lost-and-Found, or container state as appropriate;
- exceptions and cleanup results;
- the first detected canonical/local/presentation divergence.

## Assertions

Immediate assertions are appropriate only for synchronous invariants. Multiplayer and Unity state
should normally use eventual assertions with a bounded frame or network-tick deadline:

```csharp
yield return context.AssertEventually(
    "Client removed retired representation",
    () => !NetworkedItemManager.Contains(netId),
    timeoutTicks: 30);
```

An eventual assertion records the last observed value on every failed poll without publishing a
high-frequency event for every frame. On timeout it publishes expected, actual, elapsed frames,
elapsed ticks, and the relevant entity IDs.

Avoid arbitrary sleeps except when the game intentionally exposes no observable completion state.

## Three-layer assertions

Runtime scenarios must distinguish:

| Layer | Example |
| --- | --- |
| Canonical authority | Host records the item in a cold container at revision 4 |
| Local game state | Item is absent from inventory, world storage, hands, and other containers |
| Presentation | No Unity object, remote-hand attachment, phantom icon, or silhouette remains |

A workflow does not pass merely because its host transaction succeeded. The cold-container
shift-click regression is the defining example: the logical operation could succeed while vanilla
client code had already projected the source as a dropped world object.

## Dashboard phase barriers

Coordinated scenarios should use explicit phases:

```text
dashboard sends PREPARE phase
host reports READY
client reports READY
dashboard sends EXECUTE phase
host reports COMPLETE
client reports COMPLETE
dashboard advances or fails the run
```

This avoids arbitrary cross-process delays and makes ordering reproducible. Every barrier has a
deadline and reports which role failed to become ready.

## Teleportation and test anchors

Player positioning is required for repeatable tests and especially for interest, distance,
streaming, and Lost-and-Found behavior.

The dashboard sends semantic test anchors expressed in absolute world coordinates. Every game
process converts the anchor through its own origin shift:

```text
absolutePosition = localPosition - WorldMover.currentMove
localPosition    = absolutePosition + WorldMover.currentMove
```

Prefer semantic runtime anchors over unlabelled hard-coded coordinates:

```text
Station.GF.Office
Station.SM.YardCenter
Dynamic.HostStart
Dynamic.TestItemOrigin
```

A test anchor should include:

```text
anchorId
world/scene identity
absolute position
rotation
safe radius
optional station or world-object resolver
```

Where possible, resolve anchors from station and world registries and apply a documented safe
offset. Store fixed coordinates only when no stable semantic object exists.

Before declaring the teleport complete, the runtime verifies:

- the correct world and required scenes are loaded;
- `PlayerManager.PlayerTransform` exists;
- the destination has valid ground and is not inside a blocking collider;
- any required car/grabber/interaction state has been released;
- origin shifting and scene streaming have settled;
- the player remains within tolerance for several frames.

Teleportation is test arrangement and may use a controlled direct reposition if no suitable game
API exists. Tests of fast travel itself must instead invoke the real fast-travel gameplay path.

## Desktop and VR capability model

Tests declare required capabilities:

```text
NonVR
VR
Either
Host
Client
MultiplePlayers
DisposableSave
```

The initial harness supports desktop mode. VR-specific physical grabber, controller raycast, hand,
and button behavior remains a manual acceptance surface until a reliable simulated VR runtime is
found. Tests requiring VR report `Skipped: capability unavailable` rather than pretending desktop
interaction proves the VR path.

Shared post-interaction behavior remains automatable in desktop mode, including authority,
ownership, packet validation, storage, Lost and Found, tracked values, and most projection logic.

## Safety and cleanup

### Debug-build-only requirement

The runtime test harness is development instrumentation and must not be included in release builds.
Use compile-time exclusion (`#if DEBUG` and/or Debug-only project item conditions) for:

- the in-game runtime test agent and catalogue;
- gameplay test drivers and private-method bridges;
- test command queues and mutating runtime-test HTTP endpoints;
- test-only teleport, spawn, assertion, and cleanup services;
- any settings or UI capable of enabling runtime tests.

Release builds must not expose a dormant endpoint, hidden setting, command-line switch, or
reflection route that can activate the harness. Shared passive observability remains governed by
its existing release defaults, but no test can be remotely arranged or executed.

The standalone development dashboard may retain its test UI because it is not shipped as runtime
gameplay code. It must handle a release game session that advertises no runtime-test capability.

Mutating runtime tests require:

- the debug system and runtime testing setting to be enabled;
- a loopback-authenticated request;
- a private development session;
- an explicit disposable-save acknowledgement for destructive suites.

Tests are classified as:

```text
ReadOnly
IsolatedMutation
Destructive
```

Every run owns a resource ledger containing at least:

- spawned Unity objects;
- allocated NetIds;
- authority records;
- cold item/container records;
- inventory slots modified;
- Lost-and-Found records;
- event subscriptions and temporary patches;
- original player positions when restoration is supported.

Cleanup is idempotent and runs after pass, failure, cancellation, timeout, or exception. If cleanup
cannot prove that it retired all test resources, the run ends as `FailedDirty`. The dashboard then
blocks additional mutating tests until the world is reloaded or the dirty state is explicitly
cleared.

Do not promise rollback after partially destroying a Unity representation. Test setup and cleanup
should follow the same logical-commit/presentation-finalization principle as cold containers:
quarantine stale presentation, retain diagnostics, and report a dirty environment.

## Initial vertical slice

The first implemented scenario is:

```text
scenario.cold-container-round-trip
```

Suggested steps:

1. Confirm host and client are loaded and advertise compatible builds.
2. Move both players to a safe shared anchor.
3. Ask the host runtime to create a disposable client-owned container shell and compatible item,
   then wait for both authoritative inventory projections on the selected client.
4. Record the item's current inventory slot and detached state.
5. Open the crate through the real container-access path.
6. Invoke the real desktop quick-move gameplay method.
7. Assert the item never transitions through world placement.
8. Assert exactly one cold record exists in the requested/free destination slot.
9. Assert the runtime representation and NetId retire on every interested process.
10. Withdraw through the real container UI path.
11. Assert exactly one materialized item reaches the requested inventory slot.
12. Assert ownership, detached state, and graph revisions remain valid.
13. Close the container, asynchronously recover any committed cold item after a failed assertion,
    and have the host retire both disposable fixtures.

The second implemented scenario is:

```text
scenario.cold-container-foreign-rejection
```

Its defining invariant is:

> A rejected operation produces no source-side mutation.

The foreign item must remain in the same inventory slot, must not receive a dropped/world
projection, and must not create a cold edge.

## Initial inventory scenario backlog

After the vertical slice, add separate host-acting and client-acting cases for:

- pickup, drop, throw, equip, and unequip;
- rapid hotbar switching and rejected stale transitions;
- owner and non-owner pickup of protected and ordinary items;
- recall from world, remote hand, remote inventory, and Lost and Found;
- silhouette creation, preservation, transfer, and removal;
- Lost-and-Found distance collection with no nearby possessor;
- no collection while another player holds or inventories the item;
- retrieval through the Career Manager and inventory star action;
- disconnect while holding another player's item;
- cold-container deposit, withdrawal, move, rejection, full capacity, and incompatible type;
- drag/drop and quick-move as distinct entry points;
- populated container shell entering and leaving Lost and Found;
- save/reload of inventory, claims, Lost and Found, and cold contents.

Process restart, reconnect, and host-save loading now have dashboard process-lifecycle support.
Save/reload scenario assertions and clean-save provisioning remain future scenario work.

## Future train scenarios

The same coordinator can eventually support train tests by adding train-specific drivers and
assertions:

- spawn a known consist at a semantic track anchor;
- verify host/client car identity and ordering;
- couple and uncouple through real gameplay methods;
- connect/disconnect hoses and MU cables;
- manipulate controls and assert authoritative/projection values;
- move through an interest boundary;
- derail/rerail or delete under controlled destructive-test conditions.

Train tests should reuse the same run IDs, barriers, captures, resource ledger, eventual assertions,
and canonical/local/presentation distinction.

## Implementation order

1. Complete and document Derail Valley gameplay entry-point research.
2. Add runtime-test DTOs and self-tests to `Multiplayer.DebugProtocol`.
3. Add authenticated runtime test endpoints with a main-thread command queue.
4. Add the dashboard coordinator, role targeting, phase barriers, and test page.
5. Add active test-scope enrichment to observability and packet events.
6. Implement assertion, timeout, capability, resource-ledger, and cleanup infrastructure.
7. Implement teleport anchors and safe repositioning.
8. Implement the cold-container owned quick-move vertical slice.
9. Add its foreign-owner rejection counterpart.
10. Expand through inventory and Lost-and-Found scenarios before beginning train automation.

## Success criteria

The first harness milestone is complete when one dashboard action can run the owned quick-move
round trip across a host and client and produce:

- deterministic role/phase coordination;
- gameplay-boundary actions rather than direct graph mutation;
- canonical, local, and presentation assertions on both processes;
- a test-only packet and observability timeline;
- automatic failure artifacts identifying the first divergence;
- verified cleanup with no remaining object, NetId, authority record, or cold edge.

## Automated environment bootstrap (implemented 2026-07-16)

The debug dashboard now launches the host, correlates its debug session by operating-system PID,
invokes Derail Valley's native Continue action with multiplayer hosting configured, and waits for
the save, starting items, loopback client, and listening server. Only after that barrier passes does
it launch the second process, connect it through `NetworkLifecycle.StartClient`, and wait for
`PlayerLoadingState.Complete` before reporting Ready.

Authenticated debug-only `start`, `status`, `stop`, and configuration endpoints live under
`/api/runtime-environment/`. Launch configuration is machine-owned at
`%LOCALAPPDATA%\DVMultiplayer\debug-environment.json`, so the dashboard UI and external automation
use the same settings. A replacement dashboard adopts existing runtime sessions without owning or
terminating their game processes. The host currently selects the profile's latest existing save;
explicit profile/save selection and disposable test saves are deferred.

The external automation surface is versioned through `GET /api/automation`. The repository control
client can inspect sessions, start and wait for the environment, execute and await runtime commands,
capture and query correlated events, and restart the dashboard. Dashboard restart uses a versioned
Local AppData build followed by fixed-port handoff, avoiding executable locks and preserving the
live host/client environment.
