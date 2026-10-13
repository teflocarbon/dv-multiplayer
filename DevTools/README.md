# DVMP dev harness

Lets an agent (or you) drive real Derail Valley instances: start a joined host + client,
run C# inside either game, take screenshots, and read logs. Debug-only; never shipped.

## Parts

| Path | What it is |
|---|---|
| `DvmpAgent/` | In-game Unity Mod Manager mod (`Mods/MultiplayerDevTools`). Loopback HTTP server, token-authenticated, main-thread dispatcher, screenshot/log capture. No dependency on `Multiplayer.dll`. Deployed by Debug builds only. |
| `Dvmp/` | `dvmp.exe`: discovers running agents, compiles C# probes against the game's own Mono libraries with publicized game/mod assemblies, and exposes everything as an MCP server (`dvmp mcp`) and a CLI. |
| `Dvmp/Probes/*.cs` | Orchestration probes (`host`, `join`) compiled and sent into the game at runtime. Edit them without rebuilding the agent. |

## Build

```bash
dotnet build DevTools/DvmpAgent/DvmpAgent.csproj -c Debug   # deploys the agent into the game
dotnet build DevTools/Dvmp/Dvmp.csproj
```

The MCP server is registered in `.mcp.json` at the repo root. Rebuilding `dvmp.exe` while the
MCP server is running fails because the file is locked; reconnect the `dvmp` server afterwards.

## Tools

| MCP tool | CLI | Purpose |
|---|---|---|
| `dvmp_status` | `dvmp status` | Running games: role, scene, world loaded, fps, absolute player position. |
| `dvmp_up` | `dvmp up [--fresh] [--save X]` | Ensure a host + client are running and joined (reuses a healthy pair). |
| `dvmp_down` | `dvmp down` | Quit all agent-enabled games. |
| `dvmp_eval` | `dvmp eval [-t host\|client\|all] CODE` | Run C# in the game(s); see the tool description for the probe API. |
| `dvmp_screenshot` | `dvmp screenshot [-t ...] [-o DIR]` | What each window shows. |
| `dvmp_logs` | `dvmp logs [-t ...] [-l warning]` | Unity/Multiplayer log lines and exceptions. |

## Writing probes

```csharp
// expression
Dev.All<TrainCar>().Count

// body
var car = Dev.Near<TrainCar>(50).FirstOrDefault();
return new { car = car?.ID, abs = car == null ? (Vector3?)null : Dev.Abs(car.transform.position) };

// coroutine: spans frames; report with ctx.Result
var start = Dev.PlayerAbs;
yield return new WaitForSeconds(2);
ctx.Result = new { moved = Vector3.Distance(start.Value, Dev.PlayerAbs.Value) };
```

- Compare positions across peers with `Dev.Abs(...)`: each process has its own floating origin.
- Private members of game and Multiplayer types are accessible directly.
- `DevContext.State` persists between probes in the same process.

## Notes

- Games are launched with `-nonvr` (desktop), windowed 960x540, logs in
  `%LOCALAPPDATA%\dvmp\logs\{host,client}.log`.
- Built to run in the background while you use the PC (or play VR): harness-launched games
  start minimised at below-normal priority, are muted, use the lowest graphics quality, and
  are capped at 30 fps (20 fps when not the foreground window). This only applies to games
  started with `-dvmp-role`; nothing is written to the game's settings. Override with
  `dvmp up --visible --fps 60 --sound` (or the `background`/`fpsCap`/`sound` tool arguments).
  Probes can change it live via `HarnessMode.FpsCap`, `HarnessMode.Muted`, etc.
- Screenshots of a minimised game briefly restore its window without taking focus.
- The client joins as `DVMP-Client` with a fixed GUID via in-memory patches, so the shared
  `Settings.xml` is never modified.
- Hosted test sessions skip `LobbyServerManager` entirely: no lobby-server registration,
  public IP lookup, or Steam lobby.
- Agent discovery files (port + token) live in `%LOCALAPPDATA%\dvmp\agents`. The token is
  never returned by any endpoint; requests must also carry a loopback `Host` header.
