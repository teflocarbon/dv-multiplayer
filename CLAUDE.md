# dv-multiplayer

Multiplayer mod for Derail Valley (Unity 2019.4, Mono, Harmony via Unity Mod Manager).

## Testing in the real game

Use the `dvmp` MCP tools (see `DevTools/README.md`) instead of asking the user to test by hand:

1. `dvmp_up` brings up a joined host + client (reuses a running pair; first start takes a minute or more).
2. `dvmp_eval` with `target: "all"` runs C# in both games; compare peers using `Dev.Abs(...)` positions.
3. `dvmp_screenshot` to see the result, `dvmp_logs` for exceptions.

After changing `Multiplayer` code: build Debug (`dotnet build Multiplayer/Multiplayer.csproj -c Debug`),
then `dvmp_up` with `fresh: true` so both games load the new DLL.

## Unit tests

```bash
dotnet test Multiplayer.Core.Tests
dotnet test Multiplayer.Protocol.Tests -c Release   # references the built Multiplayer.dll of the same configuration
```

## Decompiled game source

The game assemblies are not obfuscated; decompile with ILSpy (`ilspycmd -p`) when you need vanilla behaviour.
