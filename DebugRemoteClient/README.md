# Debug client and replication dashboard

## Recommended: dashboard mode

Run the host and client as normal Derail Valley game instances with the in-process debug system enabled. Then start:

```powershell
Multiplayer.DebugClient.exe --dashboard "<path-to-Multiplayer.Debug>"
```

When the path is omitted, the executable checks the standard Derail Valley Multiplayer mod directory and then the current directory. It discovers every live local host/client process, consumes their loopback SSE streams, and opens the combined dashboard at `http://127.0.0.1:7781/` without joining the game as a peer. The stable origin also preserves dashboard environment fields in browser storage across dashboard restarts.

Starting a replacement dashboard asks the existing dashboard to release port `7781` and performs a non-destructive handoff. Dashboard exit no longer terminates games launched by the Environment tab, and the replacement dashboard adopts live host/client sessions and their readiness state. The explicit **Stop managed processes** action remains the way to terminate processes owned by the current dashboard.

Environment launch configuration is machine-owned rather than browser-owned. The authenticated `GET`/`POST /api/runtime-environment/config` endpoints read and update `%LOCALAPPDATA%\DVMultiplayer\debug-environment.json`; starting an environment also persists the submitted request. Existing browser-only values are migrated to that file once when the updated UI first loads.

Managed host/client windows are minimized without activation by default so Unity cannot capture the
cursor or accidentally move the camera during automated startup. Clear **Minimize without taking
focus** in the Environment page when manual visual inspection is needed.

Use the **Item replication** page to inspect correlated client → host → recipient flows, delivery expectations, missing stages, host interest decisions, and post-apply state differences. Automatic coordinated discontinuity captures can be enabled from that page.

In Debug builds, use the **Runtime tests** page to discover game-native test commands, target one
host/client process explicitly, run or cancel the command, and inspect each process result. The
initial catalogue covers harness self-check, native teleport, and desktop pickup/drop/throw. Item
pickup expects the player to be aimed at the requested network item so the test exercises DV's real
raycast and grab state machine.

The Tests page retains up to 250 parent runs in
`%LOCALAPPDATA%\DVMultiplayer\test-runs`. Its Actions-style history and detail view show live phase
and step progress, process results, assertions, cleanup state, capture files, and test-correlated
events. Updates arrive through the dashboard's existing SSE stream, with status polling only as a
fallback for an active run. Replacing the dashboard preserves completed history; a non-terminal run
found after a dashboard crash is recorded as `FailedDirty` instead of silently disappearing. Each
run also shows non-noisy packet sends/receives and item-replication lifecycle events from the exact
run window; the unfiltered correlated event list remains available below those focused views.

The first scenario commands are `scenario.cold-container-round-trip` and
`scenario.cold-container-foreign-rejection`. They exercise DV's real non-VR quick-move method,
automatically capture all runtimes for the run, and return phase, assertion, resource-cleanup, and
operation-correlation details. The round trip is self-contained: the dashboard asks the host to
create a client-owned crate and item, runs the real quick-move scenario only on that client, and
retires both fixtures afterward even when the gameplay assertion fails. A final client inventory
inspection rejects dead slots or surviving fixture NetIds before `cleanupClean` can be true. Override the default
`ItemContainerCrate` and `lighter` with `containerPrefabName` and `itemPrefabName` parameters.

Scenario discovery is metadata-driven. Each Debug-only `IRuntimeTestScenarioDefinition` supplies
its catalogue descriptor, execution factory, fixture policy, ownership, and cleanup policy. The
runtime registry discovers these classes automatically, and the dashboard selects shared
orchestration from the descriptor rather than hard-coding scenario IDs. Scenarios using an existing
fixture/orchestration policy can be added as a single class under the runtime `Scenarios` folder.

Inventory test arrangement is exposed through `inventory.inspect`, `inventory.prefab-catalog`,
`inventory.fixture-create`, `inventory.fixture-place`, `inventory.fixture-destroy`, and
`inventory.local-place`. Fixture lifecycle operations run on the host and preserve server
authority; local placement uses Derail Valley's inventory/grabber entry points. Inspection includes
dropped, reserved, and locked state so hidden silhouettes cannot be mistaken for ordinary slots.

The Debug-only **Environment** page can launch the pair as well. Supply `DerailValley.exe`, optional
working/role arguments, and local port/password settings. A deterministic baseline can be selected
by game mode, `ISaveGame.UID`, and an optional exact-name safeguard; leaving all three blank retains
the legacy latest-save fallback. The selected save identity is shown in environment status. It
waits until the server is fully ready, then starts and connects the client. Cursor-protected
launches carry a debug-only flag handled inside the game so Unity cannot lock or hide the desktop
cursor; the dashboard does not disable or manipulate the Windows game windows, and the ordinary
game remains unchanged. The dashboard exposes
authenticated `GET status`, `POST start`, and `POST stop` operations under
`/api/runtime-environment/`; stop affects only processes launched by that dashboard run.

## Automation API and control client

The debug dashboard exposes `GET /api/automation` for API discovery and authenticated endpoints for
sessions, machine configuration, environment lifecycle, runtime commands, captures, and filtered
event queries. Multiplayer builds mint a shared CalVer build number (`yyyy.MM.dd.HHmm`) in
`build/dvmp-build-number.txt`; both the mod and dashboard embed it as `DVMPBuildNumber`. The
discovery response, dashboard header, runtime sessions, and test attribution therefore identify the
same source build. This is separate from the mod's `0.1.15.0` semantic version, which remains the
network/mod-loader compatibility version. Use the deterministic PowerShell client instead of
constructing requests manually:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\DebugRemoteClient\Tools\dvmp-harness.ps1 status
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\DebugRemoteClient\Tools\dvmp-harness.ps1 environment-start -TimeoutSeconds 300
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\DebugRemoteClient\Tools\dvmp-harness.ps1 environment-wait -TimeoutSeconds 300
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\DebugRemoteClient\Tools\dvmp-harness.ps1 capabilities
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\DebugRemoteClient\Tools\dvmp-harness.ps1 runs
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\DebugRemoteClient\Tools\dvmp-harness.ps1 run -TestId runtime.self-check -TargetSessionId <session-id>
```

After rebuilding dashboard code, `restart-dashboard` builds to a versioned directory under
`%LOCALAPPDATA%\DVMultiplayer\dashboard-builds`, launches the replacement, and waits for the fixed
port to transfer to its new process. The existing dashboard remains available if the build fails,
and a successful handoff does not close the running host or client games.

Run contract, fingerprint, event-identity, and synthetic replication-flow tests with:

```powershell
Multiplayer.DebugClient.exe --self-test
```

## Legacy protocol-client mode

Enable **Enable Debug Loopback Client** in the host mod's advanced settings, then host a game. The
server listens only on `127.0.0.1` and defaults to port `7778`; normal Steam clients are unaffected.

Build the mod first so `Multiplayer/bin/Debug/net48/Multiplayer.dll` exists. Then copy
`debug-client.example.json` to `debug-client.local.json`, fill in the same login/build/mod data as
the host, and run:

```powershell
dotnet build Debug/Multiplayer.DebugClient.csproj
Debug/bin/Debug/net48/Multiplayer.DebugClient.exe Debug/debug-client.local.json
```

The client starts a local trace UI at `http://127.0.0.1:7780/` by default. It presents each packet
as a compact row with search, direction/status/type filters, and expandable decoded metadata/raw
payload. Set `EnableWebUi` to `false` or change `WebUiPort` in the local profile if needed.

The console dynamically subscribes to built-in clientbound/common packet classes in the mod assembly.
Commands: `/status`, `/ui`, `/follow-host`, `/follow <name>`, `/teleport x y z`, `/offset right up
forward`, `/raw true|false`, `/verbose true|false`, `/quit`.

This mode predates the in-process observability system. It remains useful for protocol experiments, but it cannot observe how real client Unity objects apply packets and is not the recommended item-sync debugging workflow.
