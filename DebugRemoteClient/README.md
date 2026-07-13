# Debug client

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
