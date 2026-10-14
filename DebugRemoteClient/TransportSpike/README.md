# Transport spike

This is the mandatory gate before building the debug client. It starts a second process using the
same `SteamWorksTransport` source as the mod, sends a real login packet, and waits up to 20 seconds
for the host's `ClientboundLoginResponsePacket`.

1. Copy `transport-spike.example.json` to `transport-spike.local.json`.
2. Set `BuildVersion` and `Mods` to exactly match the hosted game. The server compares mod IDs, so
   include every required host mod ID. Do not commit the local profile.
3. Start a LAN-hosted Derail Valley multiplayer game.
4. Build and run:

```powershell
dotnet build Debug/TransportSpike/Multiplayer.TransportSpike.csproj
Debug/TransportSpike/bin/Debug/net48/Multiplayer.TransportSpike.exe Debug/TransportSpike/transport-spike.local.json
```

`SUCCESS` means the second process established a Steam transport peer and decoded the host's login
response. A rejection still proves transport viability, but the profile must be corrected before
testing the debug client's loading sequence. The spike prints the Steam client identity, ownership,
managed-wrapper version, and native/app-ID file checks before it connects; include that diagnostic
block with any connection failure report.

The executable requires Steam to be running and uses the local Derail Valley managed assemblies.
Override `DerailValleyManagedDir` and `DerailValleyPluginDir` during build if the game is installed
somewhere else.
