# Debug client and replication dashboard

## Recommended: dashboard mode

Run the host and client as normal Derail Valley game instances with the in-process debug system enabled. Then start:

```powershell
Multiplayer.DebugClient.exe --dashboard "<path-to-Multiplayer.Debug>"
```

When the path is omitted, the executable checks the standard Derail Valley Multiplayer mod directory and then the current directory. It discovers every live local host/client process, consumes their loopback SSE streams, and opens a combined random-port dashboard without joining the game as a peer.

Use the **Item replication** page to inspect correlated client → host → recipient flows, delivery expectations, missing stages, host interest decisions, and post-apply state differences. Automatic coordinated discontinuity captures can be enabled from that page.

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
