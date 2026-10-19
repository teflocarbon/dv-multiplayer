# DVMP dev harness

Lets an agent (or you) drive real Derail Valley instances: start a joined host + client,
run C# inside either game, take screenshots, and read logs. Debug-only; never shipped.

## Parts

| Path | What it is |
|---|---|
| `DvmpAgent/` | In-game Unity Mod Manager mod (`Mods/MultiplayerDevTools`). Loopback HTTP server, token-authenticated, main-thread dispatcher, screenshot/log capture; opt-in [remote debugging](#remote-debugging) over Tailscale. No dependency on `Multiplayer.dll`. Deployed by Debug builds only; never shipped with the mod. |
| `Dvmp/` | `dvmp.exe`: discovers running agents, compiles C# probes against the game's own Mono libraries with publicized game/mod assemblies, and exposes everything as an MCP server (`dvmp mcp`) and a CLI. |
| `Dvmp/Probes/*.cs` | Probes behind the tools (`host`, `join`, `snapshot`, `soak`, `trace-install`, `trace-show`) compiled and sent into the game at runtime. Edit them without rebuilding the agent. |
| `Scenarios/` | Regression scenarios (`<name>/run.sh` plus its probes) and `gate.sh`, which runs them all. |

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
| `dvmp_up` | `dvmp up [--fresh] [--save X]` | Ensure a host + client are running and joined (reuses a healthy pair). A join is done once the client has left loading mode. |
| `dvmp_down` | `dvmp down` | Quit all agent-enabled games. |
| `dvmp_eval` | `dvmp eval [-t host\|client\|all] CODE` | Run C# in the game(s); see the tool description for the probe API. |
| `dvmp_screenshot` | `dvmp screenshot [-t ...] [-o DIR]` | What each window shows. |
| `dvmp_logs` | `dvmp logs [-t ...] [-l warning]` | Unity/Multiplayer log lines and exceptions. |
| `dvmp_desync` | `dvmp desync [-n 6] [-i 1]` | Snapshot real Unity state on host and client repeatedly and report divergences that persist (items, cars, money, junctions). Full report in `%LOCALAPPDATA%\dvmp\reports`. |
| `dvmp_soak` | `dvmp soak [-t client\|host\|all] [-n 15] [--seed 1] [--filter JobOverview]` | Seeded bot doing random carry/throw/drop/wander actions, then a desync check. Same seed replays the same choices. |
| `dvmp_trace` | `dvmp trace [--ids 12,34]` | Mutation tracer: which code froze/moved/reparented/hid/destroyed an item, with the calling method. `dvmp up --trace` installs it before the world loads. |
| – | `dvmp up --host-only --session NAME` | Start (or reuse) only a host; see [Host-only sessions](#host-only-sessions). |
| – | `dvmp perf [-t ...] [-s 10]` | Frame-time percentiles, tick rate, memory (with the retained Mono heap), awake rigidbodies, cars, items, players, and packets per type per second, over a window. |

## Host-only sessions

A "light host" is a harness host that nobody plays: the first stage of
[dedicated-host.md](../docs/design/dedicated-host.md#recommended-plan). It's also how to compare a
VR host with a desktop one (#78).

```bash
dvmp up --host-only --session light --save "DVMP Test Career 2"   # host only; prints port and password
dvmp up --host-only --dedicated --session light --save "..."      # the same, as a dedicated host (-mp-dedicated)
dvmp up --session light          # optional: join a dvmp client to it (the client-only path)
dvmp perf --session light -s 30  # sample both games
dvmp down --session light
```

- **Named sessions only.** The dev agent also loads in your own Debug game, which is in the default
  session, so host-only refuses that session rather than risk quitting or mistaking it.
- **Its own identity.** The host's player is `DVMP-Host` with a fixed GUID, patched in memory by the
  `host` probe (`playerName`, `playerGuid`). Your own game shares the install's `Settings.xml`, so
  it can join with its normal identity, to `127.0.0.1:<port>`, without colliding with the host.
- **30 fps in the background** (`--bg-fps`, default 20 for pairs), so it stays above the mod's
  24 Hz network tick.
- Pass `--save`: the launcher's default save may be your real career.
- `dvmp perf` hooks the mod's per-packet debug calls with Harmony prefixes on its first run. They
  count every send and receive whether or not the debug system is on, and stay installed until the
  game quits. The forced GC behind `monoRetained` costs one frame hitch; skip it with
  `-a retained=false` through `dvmp eval -f Probes/perf.cs`.
- `--dedicated` launches the host as a dedicated host (`-mp-dedicated`). Its own client is then a
  service identity, not a player: no avatar for the client to see, no slot, not a nearby player.
  A plain `dvmp up` reuses either kind of host; `--dedicated` only reuses a dedicated one.
- Not in the MCP tools yet: they have no `session` argument, so use the CLI.

## Remote debugging

The agent can also be reached from your PC in someone else's game, over Tailscale, to look into
a bug only they hit. Running probes there is arbitrary code execution on their PC, so it takes a
pairing once and their consent every launch. The agent's side is `DvmpAgent/RemoteAccess.cs`.

**Their setup, once.** They share their PC with you from their own tailnet (admin console:
Machines → Share), install `MultiplayerDevTools.zip` with Unity Mod Manager (every build of
`DvmpAgent` writes it to its output folder), and allow Derail Valley through Windows Firewall
when it asks.

```bash
# once: they press "Pair a new debugger" in the mod's settings (Ctrl+F10) and read out the address and code
dvmp remote pair bentgear 100.102.60.80 ABCD-EFGH-JKMN-PQRS
# every launch of their game: they press "Allow a paired debugger" and read out the session code
dvmp remote connect bentgear WXYZ-2345
dvmp eval -t remote:bentgear "Dev.PlayerAbs"     # the MCP tools take target remote:bentgear too
```

- **Pairing** uses a single-use 16-character code that lasts 10 minutes. It gives dvmp a
  long-term secret (in `%LOCALAPPDATA%\dvmp\remotes\NAME.json`), bound to the Tailscale address
  you paired from. They see each pairing in the settings and can remove it.
- **Sessions:** remote debugging is always off when their game starts. Pressing Allow shows a
  fresh 8-character code; requests carry HMAC(secret, code), so neither the pairing nor an old
  code works alone. It turns off when the game closes, after 60 minutes without a request, when
  they press Stop, or after 5 failed attempts.
- **Network:** it listens only on their Tailscale adapter's address (port 7790, or the next free
  one up to 7799, shown in the settings) and accepts only Tailscale addresses. `/v1/quit` isn't
  served remotely.
- **What they see:** a red banner in the game window while it's on (not in a VR headset), the
  recent requests in the settings, and every request in `remote-audit.log` in the mod folder,
  including the source of each probe and the SHA-256 of the assembly that ran.
- `remote:NAME` is never part of `all`, `host` or `client`, so `dvmp up`, `down` and the
  scenarios can't reach their game. `dvmp status` lists paired games without contacting them.
- Probes compile against your game's assemblies and your build of the mod. Use the same
  Multiplayer version they run, or probes that touch changed members fail in their game.

## Regression scenarios

Each scenario is `DevTools/Scenarios/<name>/run.sh`, needs a running pair, and exits non-zero on
failure; its header lists its variables. `gate.sh` runs the whole set against the running pair
and prints PASS/FAIL per run. Run it after any change touching items, joins, ticks or trains.

```bash
bash DevTools/Scenarios/gate.sh                       # everything
ONLY="rejoin recall" bash DevTools/Scenarios/gate.sh  # by scenario or run name
WHO=host MODE=throw WHEN=moving bash DevTools/Scenarios/train-items/run.sh
```

Per-run output goes to `$LOG_DIR` (default a temp directory, printed at the end). Scenarios use
the CLI at `$DVMP` (default `DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe`). The list of scenarios and
what each checks is in [`docs/testing.md`](../docs/testing.md#regression-gate); what the old
runtime tests covered that still needs porting is in `Scenarios/PORTING.md`.

Test saves are separate sessions ("DVMP Test Career", "DVMP Test Career 2"); create more with
`dvmp up --new career --name "..."`.

## Writing probes

Probes (`Probes/*.cs`) and their shared libraries (`Probes/lib/*.cs`: `Hands`, `LocalPlayer`,
`Trains`, `Steps`) are read from source on every call when dvmp runs from the repo, so editing
them needs no rebuild. Only changes to dvmp itself do; while the MCP server holds
`bin/Debug/net8.0/dvmp.exe`, build a CLI copy with
`dotnet build DevTools/Dvmp/Dvmp.csproj -o DevTools/Dvmp/bin/cli` and point scenarios at it with
`DVMP=DevTools/Dvmp/bin/cli/dvmp.exe`.

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

Every probe also gets the shared library in `Dvmp/Probes/lib` (namespace `Dvmp.Lib`):

```csharp
// coroutine: pick up item 286 through DV's real grabber, carry it, drop it
var item = Dev.All<NetworkedItem>().First(n => n.NetId == 286);   // add usings: Multiplayer.Shared.Items
yield return LocalPlayer.GoTo(item);
yield return Hands.Pickup(item.NetId);        // aims + raycast pickup, falls back to force-hold
yield return LocalPlayer.TeleportAbs(Dev.PlayerAbs.Value + new Vector3(4, 0, 0));
yield return Hands.Drop();                    // or Hands.Throw(direction)

// keep going after a failed step
string error = null;
yield return Steps.Try(Hands.Pickup(42), e => error = e.Message);
```

`Hands` takes over the game's input (real keyboard/mouse to that window are ignored) while it
acts; `Hands.ReleaseControl()` hands it back.

- Compare positions across peers with `Dev.Abs(...)`: each process has its own floating origin.
- Private members of game and Multiplayer types are accessible directly.
- `DevContext.State` persists between probes in the same process.

## Notes

- Games are launched with `-nonvr` (desktop), windowed 960x540. Every launch gets an ID
  (`20261017-021500-p2-host`: time, session, role), passed as `-dvmp-launch`. It names the game's
  log, `<launch ID>.log` in `%LOCALAPPDATA%\dvmp\logs\` (`sessions\NAME\logs\` for a named
  session), and the mod's own log and event stream, `Multiplayer.Debug/multiplayer-<launch ID>.log`.
  Nothing is overwritten; the newest 40 game logs per session are kept. `dvmp status` shows each
  game's `launchId` and `logFile`, and a failed gate run lists the game logs from its start and end.
- Built to run in the background while you use the PC (or play VR): harness-launched games
  start minimised at below-normal priority, are muted, use the lowest graphics quality, and
  are capped at 30 fps (20 fps when not the foreground window). This only applies to games
  started with `-dvmp-role`; nothing is written to the game's settings. Override with
  `dvmp up --visible --fps 60 --sound` (or the `background`/`fpsCap`/`sound` tool arguments).
  Probes can change it live via `HarnessMode.FpsCap`, `HarnessMode.Muted`, etc.
- Real keyboard/mouse are ignored by harness-launched games, so a stray click on a test window
  can't move a player. Press Ctrl+Shift+F5 in a game window to take (or give back) control.
- Screenshots of a minimised game briefly restore its window without taking focus.
- The client joins as `DVMP-Client` with a fixed GUID via in-memory patches, so the shared
  `Settings.xml` is never modified.
- Hosted test sessions skip `LobbyServerManager` entirely: no lobby-server registration,
  public IP lookup, or Steam lobby.
- Agent discovery files (port + token) live in `%LOCALAPPDATA%\dvmp\agents`. The token is
  never returned by any endpoint; requests must also carry a loopback `Host` header.
