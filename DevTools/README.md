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
| `DvmpScenarios/` | Regression scenarios in C#, replacing `Scenarios/` one at a time ([design](../docs/design/scenarios.md)): a net48 exe compiled against the mod, run by `dvmp scenario`. Its `[Step]` methods run inside the games through the agent's `/v1/load` and `/v1/invoke`. |

## Build

```bash
dotnet build DevTools/DvmpAgent/DvmpAgent.csproj -c Debug   # deploys the agent into the game
dotnet build DevTools/DvmpAgent/DvmpAgent.csproj -c Debug -p:DvmpNoDeploy=true   # compile only
dotnet build DevTools/Dvmp/Dvmp.csproj
```

The MCP server is registered in `.mcp.json` at the repo root. Rebuilding `dvmp.exe` while the
MCP server is running fails because the file is locked; reconnect the `dvmp` server afterwards.

## Test install

Test builds don't have to go into the game you play. `DevTools/make-test-install.ps1` makes a copy
of Derail Valley at `%LOCALAPPDATA%\dvmp\test-install` that writes nothing to the real install:
`DerailValley_Data` and `MonoBleedingEdge` are directory junctions to it, the launcher and Unity
Mod Manager's loader are copied, `Multiplayer` and `MultiplayerDevTools` are real copies, and a
`steam_appid.txt` lets Steam start it from outside its folder. No other mod is loaded: tests see
the mod alone, and Unity Mod Manager loads in about 10 s instead of 32 s with 21 mods
([harness-time-budget.md](../docs/research/harness-time-budget.md)). Running it again on an older
test install removes its links to other mods (only the links). For a mod-compatibility pass, make a
separate install with `-AllMods -TestDir ...`, which links every mod in the real install. Then point
the harness and the builds at it:

```bash
export DVMP_DV_DIR="$LOCALAPPDATA/dvmp/test-install"          # dvmp launches and compiles against it
dotnet build Multiplayer/Multiplayer.csproj -c Debug "-p:DvInstallDir=$DVMP_DV_DIR"
dotnet build DevTools/DvmpAgent/DvmpAgent.csproj -c Debug "-p:DvInstallDir=$DVMP_DV_DIR"
```

Windows Firewall keys its rules on the program path, so every install's copied `DerailValley.exe`
shows a "Windows Defender Firewall has blocked some features" prompt on its first launch. Run as
admin, the script adds an inbound allow rule for that exe (Private networks, named
`Derail Valley test (<install folder>)`), first removing any Block rules a Cancel on the prompt left;
running it again adds nothing new. Not run as admin, it prints the `New-NetFirewallRule` command to
paste into an admin PowerShell instead. `-FirewallOnly` adds or repairs the rule for every
`%LOCALAPPDATA%\dvmp\test-install*` install without touching anything else. Pairs on one PC don't
need the rule (they connect over loopback, so Cancel is harmless), but a cloud or remote pair
connecting to a game on this PC does.

dvmp only reuses and quits games it launched (their `-dvmp-role` argument) from the install it is
pointed at. A game you start yourself, or one running from another folder (your own play, a
server for something else), is never taken over or shut down, even by `down --all`.

## Tools

Every MCP tool that acts on games takes `session` (CLI: `--session NAME` or `DVMP_SESSION`), so one MCP
server can drive several pairs: p1, p2, d1. Remote games: `dvmp_remote` / `dvmp remote` (see
[Remote debugging](#remote-debugging)).

| MCP tool | CLI | Purpose |
|---|---|---|
| `dvmp_status` | `dvmp status [--all]` | Running games: role, scene, world loaded, fps, absolute player position (`all`: every session's); paired remote games. |
| `dvmp_up` | `dvmp up [--fresh] [--save X]` | Ensure a host + client are running and joined (reuses a healthy pair). A join is done once the client has left loading mode. |
| `dvmp_down` | `dvmp down [--all]` | Quit this session's games (`all`: every session's). Only games dvmp launched (`-dvmp-role`): a game you started yourself is never quit, and `up` never reuses it. |
| `dvmp_eval` | `dvmp eval [-t host\|client\|all] CODE` | Run C# in the game(s); see the tool description for the probe API. |
| `dvmp_screenshot` | `dvmp screenshot [-t ...] [-o DIR]` | What each window shows. |
| `dvmp_logs` | `dvmp logs [-t ...] [-l warning]` | Unity/Multiplayer log lines and exceptions. |
| `dvmp_desync` | `dvmp desync [-n 6] [-i 1]` | Snapshot real Unity state on host and client repeatedly and report divergences that persist (items, cars, money, junctions). Full report in `%LOCALAPPDATA%\dvmp\reports`. |
| `dvmp_soak` | `dvmp soak [-t client\|host\|all] [-n 15] [--seed 1] [--filter JobOverview]` | Seeded bot doing random carry/throw/drop/wander actions, then a desync check. Same seed replays the same choices. |
| `dvmp_probes` | `dvmp probes [-t ...] [--stop all\|ID] [--unpatch OWNER]` | Coroutines and Harmony patches probes left running in each game (with the coroutines' logs); stop or remove them. See [below](#coroutines-and-patches-a-probe-leaves-behind). |
| `dvmp_trace` | `dvmp trace [--ids 12,34]` | Mutation tracer: which code froze/moved/reparented/hid/destroyed an item, with the calling method. `dvmp up --trace` installs it before the world loads. |
| `dvmp_up` `dedicated` | `dvmp up --dedicated` | The host as a dedicated server: no avatar, no player slot. |
| `dvmp_up` `hostOnly` | `dvmp up --host-only --session NAME` | Start (or reuse) only a host; see [Host-only sessions](#host-only-sessions). |
| `dvmp_up` `reseed` | `dvmp up --reseed --session NAME` | Restart a named session with its user data copied afresh from your profile; see [Session user data](#session-user-data). |
| `dvmp_perf` | `dvmp perf [-t ...] [-s 10]` | Frame-time percentiles, tick rate, memory (with the retained Mono heap), awake rigidbodies, cars, items, players, and packets per type per second, over a window. |

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
- Pass `--save`: the launcher's default save is your selected career (a copy: see
  [Session user data](#session-user-data)).
- `dvmp perf` hooks the mod's per-packet debug calls with Harmony prefixes on its first run. They
  count every send and receive whether or not the debug system is on, and stay installed until the
  game quits. The forced GC behind `monoRetained` costs one frame hitch; skip it with
  `-a retained=false` through `dvmp eval -f Probes/perf.cs`.
- `--dedicated` launches the host as a dedicated host (`-mp-dedicated`). It has no player of
  its own: no avatar for the client to see, no slot, not a nearby player.
  A plain `dvmp up` reuses either kind of host; `--dedicated` only reuses a dedicated one.
- Not in the MCP tools yet: they have no `session` argument, so use the CLI.

## Session user data

A named session's games (`--session NAME`, `DVMP_SESSION`, the gate's `p1`, `d1`...) keep DV's user
data in `%LOCALAPPDATA%\dvmp\sessions\NAME\data\host` and `\client` instead of your profile in
`%USERPROFILE%\AppData\LocalLow\Altfuture\Derail Valley`: careers, saves, `userData.json`,
preferences and control bindings. Parallel pairs then never write each other's saves or yours.

- dvmp copies the folders from your profile, which it only reads, the first time a session
  launches: the profile files, the "DVMP..." careers and each mode's selected one, with their
  newest 5 saves. They're kept across `down` and `up`, since `save-reload` and `inventory-persist`
  restart the games and load what they saved. `dvmp up --reseed` copies them again (and restarts
  the games).
- The dev agent points DV's storage at the folder (`-dvmp-userdata`). `up` checks it at the main
  menu, before anything is saved, and fails if a game still uses your profile: an agent older than
  this needs a rebuild. A running game that doesn't use the session's folder (one an older dvmp
  started) is restarted rather than reused.
- Harness games never save mod settings at quit (Unity Mod Manager's `OnSaveGUI` is cleared), so
  `Settings.xml` keeps what you set.
- The default session still uses your profile, as before. `DVMP_SHARED_PROFILE=1` puts named
  sessions back on it too; then give the gate
  `EXCLUSIVE="fps-drop host-stall player-soak save-reload inventory-persist host-player-persist"`. Unity's own analytics cache under the
  profile folder (`Unity\`) isn't DV's storage and still changes.

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

The ported ones are C# classes in `DvmpScenarios/Scenarios/` ([design](../docs/design/scenarios.md));
gate.sh runs them too, with the same variables:

```bash
dotnet build DevTools/DvmpScenarios/DvmpScenarios.csproj -c Debug   # after building Multiplayer
dvmp scenario list                                                  # names, options, defaults
dvmp scenario run host-stall --session p1 --arg SPEED=10 --arg LAG=150
```

A scenario run never starts games: it needs the session's pair up, and exits 0 (pass), 1 (fail),
2 (usage: an unknown option is an error) or 3 (harness: no pair, a stale install, the wrong kind of
host). Steps are compiled once, loaded once per game launch, and run by name; the agent refuses a
build compiled against another `Multiplayer.dll` than the game runs.

```bash
bash DevTools/Scenarios/gate.sh                       # everything
ONLY="rejoin recall" bash DevTools/Scenarios/gate.sh  # by scenario or run name
PAIRS="p:2 d:1" RUNS=batch.txt bash DevTools/Scenarios/gate.sh  # pairs by kind, your own run list
RUNS="train-items WHO=host MODE=throw WHEN=moving" bash DevTools/Scenarios/gate.sh  # one run with variables
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

### Coroutines and patches a probe leaves behind

Probes may run in a game other people are playing in, where anything left running costs them
frames. Everything a probe starts ends with it:

```csharp
// parallel work: stopped when the probe returns, fails or times out
var sampler = ctx.StartCoroutine(Sample(), "sample");
yield return new WaitForSeconds(5);
yield return sampler;                        // or wait for it to end

// a watcher read by later probes: outlives the probe, but only for maxSeconds (default 600, max 3600)
ctx.StartBackground("l010-controls", pc => Watch(pc), maxSeconds: 300);   // Watch can pc.Log(x)
// ...a later probe
DevContext.Background("l010-controls")?.Stop();
```

- Don't call `someBehaviour.StartCoroutine(...)` with a probe's own enumerator. The agent hooks
  `MonoBehaviour.StartCoroutine` (from the first probe on), logs a warning, and stops those with
  the probe as well. A probe's eval result lists what was stopped (`stoppedCoroutines`).
- A probe that times out is stopped with all its coroutines (its `finally` blocks run) and the
  result says so (`timedOut`). Only a probe stuck without yielding can't be stopped: it blocks
  the main thread.
- Starting a background coroutine with the name of a running one replaces it.
- `dvmp probes` (`dvmp_probes`) lists each game's probe coroutines with their logs, recently
  ended ones and why, Harmony patches by owner (any `dvmp.*` owner, or a patch in a probe
  assembly), and the number of probe assemblies loaded (Mono can't unload them; restart the game
  if that number gets large). `--stop all|ID|NAME|dvmp_probe_…` stops coroutines, and
  `--unpatch OWNER` (repeatable; `all` for every probe owner, `dvmp.foo.*` for a prefix) removes
  patches. `dvmp.live.*` (live hotfixes) and `dvmp.agent.*` are never removed; `dvmp.harness.*`
  only when named exactly, and `all` skips them.
- `dvmp status` adds `warnings` while background or ownerless probe coroutines run, or
  probe patches are installed (the tracer's and `dvmp perf`'s count as probe patches).
- Name Harmony owners `dvmp.<tool>.<what>` so they're listed.
- A C# scenario's steps (`/v1/invoke`, [scenarios](../docs/design/scenarios.md)) are tracked the
  same way, under their assembly `DvmpScenarios_<id>`: what a step starts stops with it, and
  `/v1/cancel` stops a step and everything it started.

## Notes

- Games are launched with `-nonvr` (desktop), windowed 960x540. Every launch gets an ID
  (`20261017-021500-p2-host`: time, session, role), passed as `-dvmp-launch`. It names the game's
  log, `<launch ID>.log` in `%LOCALAPPDATA%\dvmp\logs\` (`sessions\NAME\logs\` for a named
  session), and the mod's own log and event stream, `Multiplayer.Debug/multiplayer-<launch ID>.log`.
  Nothing is overwritten; the newest 40 game logs per session are kept. `dvmp status` shows each
  game's `launchId` and `logFile`, and a failed gate run lists the game logs from its start and end.
- Built to run in the background while you use the PC (or play VR): harness-launched games
  start minimised at below-normal priority, are muted, use the lowest graphics quality, and
  are capped at 30 fps (20 fps when not the foreground window). While a game loads (its loading
  screen is up, or its world is streaming in) the cap is 60 fps, since loading steps take a frame
  each; `-dvmp-loading-fps N` changes it, 0 lifts it. This only applies to games
  started with `-dvmp-role`; nothing is written to the game's settings. Override with
  `dvmp up --visible --fps 60 --sound` (or the `background`/`fpsCap`/`sound` tool arguments).
  Probes can change it live via `HarnessMode.FpsCap`, `HarnessMode.LoadingFpsCap`, `HarnessMode.Muted`, etc.
- `dvmp up` starts loading the host's save as soon as the host's agent is up, while the client
  is still booting, rather than after the client's agent is up.
- Real keyboard/mouse are ignored by harness-launched games, so a stray click on a test window
  can't move a player. Press Ctrl+Shift+F5 in a game window to take (or give back) control.
- Screenshots of a minimised game briefly restore its window without taking focus.
- The client joins as `DVMP-Client` with a fixed GUID via in-memory patches, so the shared
  `Settings.xml` is never modified.
- Hosted test sessions skip `LobbyServerManager` entirely: no lobby-server registration,
  public IP lookup, or Steam lobby.
- Agent discovery files (port + token) live in `%LOCALAPPDATA%\dvmp\agents`. The token is
  never returned by any endpoint; requests must also carry a loopback `Host` header.
