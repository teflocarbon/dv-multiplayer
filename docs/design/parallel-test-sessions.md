# Parallel dvmp test sessions

> Status: steps 1-3, 6 and 7 are built (sessions, ports, launch lock, the parallel gate runner,
> lean harness games); see [Results](#results). Three pairs fit on the reference machine. Steps 4
> (MCP), 5 (separate user data) and 8 (docs) are still to do. Findings below were checked against
> the code on 2026-10-14.

## Purpose

dvmp runs one host and one client, and `DevTools/Scenarios/gate.sh` runs every scenario on that
pair one after another (about 50 minutes for 27 runs). Running several independent pairs at once
would let us:

- **split the gate** across pairs, bringing it down to roughly 15-20 minutes; and
- **repeat one scenario on every pair at once** (`ONLY=host-stall REPEAT=6`) to hunt
  intermittent bugs. "6/6 passed" says far more than one lucky run.

The default session must keep behaving exactly as today, so nothing changes until a session is
named.

## Machine budget

Measured per game while the gate was running (Ryzen 9 7950X3D, 63 GB RAM, RTX 4090 24 GB):

| | Per game | Machine | Games that fit |
|---|---|---|---|
| CPU | ~3 cores | 32 threads | ~10 |
| RAM | ~5.6 GB | 63 GB | ~9-11 |
| VRAM | ~5 GB | 24 GB | **~4** |

VRAM is the limit: two pairs fit today, three only if Windows spills video memory into RAM,
which slows the games and upsets timing scenarios. Step 7 attacks this first.

The whole game is only ~4.6 GB on disk (scene files 0.1 GB, shared assets 1.3 GB, resources
2.3 GB, StreamingAssets 0.9 GB), and textures are stored GPU-compressed, so a game's memory is
mostly runtime state rather than world tiles. Expect the texture cap to help VRAM most and
smaller streaming ranges to help less.

## What assumes one session today

**Agent discovery is fine.** Each game writes `%LOCALAPPDATA%\dvmp\agents\agent-{pid}.json`
(`DevTools/DvmpAgent/Discovery.cs`) with its pid, a random agent port, token and full command
line. Nothing collides, and dvmp can read a `-dvmp-session` flag back from the stored command
line without changing the agent.

**dvmp treats every agent as one pair.**
- `Agents.DiscoverAsync` / `ResolveAsync` (`DevTools/Dvmp/Agents.cs`) return every agent, and
  `host`/`client` mean "every host/client".
- `Desync.RunAsync` takes the first host and first client.
- `Session.RunUpAsync` reuses the first healthy host and client, and decides on a client-only
  relaunch with `agents.Count == 1`. Its fallback calls `DownAsync`, which quits **every** agent
  game, including a Debug game you launched by hand.
- `Session` keeps its state in statics (`running`, `stage`, `timeline`), so the MCP server
  tracks one `up` at a time.

**Fixed paths and names.**
- Game logs go to `DataRoot\logs\{role}.log`.
- Desync and soak reports are named by timestamp only, so two pairs can finish in the same
  second. Screenshots already include the pid.
- Window titles are a fixed `DVMP HOST` / `DVMP CLIENT`, and `Arrange` uses fixed positions.
- `Publicizer.cs` writes a shared `target + ".tmp"`, so two dvmp processes can race on it right
  after a mod rebuild.

**Scenarios need no edits** if dvmp reads the session from an environment variable. Every
`run.sh` calls `$D eval/up/down/status` through `D=${DVMP:-...}`, and temp files already use
`mktemp` or `$$`. These calls must become session-scoped inside dvmp:
- `rejoin/run.sh` runs `$D status` and picks the first client;
- `save-reload` and `inventory-persist` run `$D down`, which would kill every pair;
- `rejoin` and `borrowed-disconnect` run `$D up`.

## Dangers

**A second host on a used port joins the wrong pair.** The transport is Steam
(`SteamWorksTransport`). `Start(port)` opens a UDP socket on `AnyIp:port` **and** a Steam relay
socket, and reports running if **either** succeeds. A second host on a port that's already taken
fails the UDP bind, still reports "started", and its client, connecting to `127.0.0.1:port`,
joins the other pair's host. Each session needs its own port, checked free before launch and
checked again once the host is up.

N hosts on one Steam account also each open a relay listen socket on virtual port 0. Loopback
tests don't use relay, so at worst the later ones fail quietly; check the host log the first
time.

**Shared user data can corrupt your real profile.** All games share DV's data in
`%USERPROFILE%\AppData\LocalLow\Altfuture\Derail Valley\`:
- `Users\global.json` and `Users\<user>\userData.json`, which holds the selected session per
  mode and the encryption signature;
- `Users\<user>\Career\<session>\Saves\NNNNN_date.save`;
- `Preferences\<user>.ini`.

Also shared is the mod's `Settings.xml`, which `Settings.Load` writes at every startup, and
which UMM probably also writes at quit.

With parallel hosts:
- each process numbers saves from its own in-memory list, so two hosts write the same file
  name;
- "newest save of the session" becomes whichever pair saved last, which breaks `save-reload`;
- 2N processes may rewrite `userData.json` at once.

**Mod loading touches shared files.** UMM touches shared cache files while loading mods (see
the note in `Session.cs`), and installing the tracer on two games at once collides on Harmony's
temp files. Launches and tracer installs need to be serialised.

## Design

### Session context

- Add a `Sessions` class in dvmp with an `AsyncLocal<string> Current`. It comes from `--session`,
  else `DVMP_SESSION`, else `default`; names match `[A-Za-z0-9_-]{1,16}`.
- `AgentInfo.Session` is parsed from the stored command line (`-dvmp-session`; absent means
  `default`).
- `DiscoverAsync` filters to the current session unless asked for all. Desync, soak, tracer and
  operations then follow automatically.
- `Launch` adds `-dvmp-session` for named sessions only.
- Per-session logs go to `DataRoot\sessions\<s>\logs\{role}.log`; the default keeps today's path.
- Reports get a `-<session>` suffix, and window titles become `DVMP HOST [s]`.
- `up`, `down` and `status` act on the current session; `down --all` and `status --all` cover
  every session.

### Ports

- `DataRoot\sessions\<s>\session.json` stores `{ port, created }`.
- Named sessions take the lowest free port from 7801 under the launch lock. The default session
  keeps `Settings.Port`.
- "Free" means no process has a UDP socket on it in the system's table (`GetExtendedUdpTable`,
  `Win32.UdpPortOwners`). A test bind doesn't work: the game's socket allows address reuse, so
  binding its port succeeds while the game is listening on it.
- **No mod change is needed.** `NetworkLifecycle.StartServer` already uses `serverData.port` for
  multiplayer games, and the host probe (`DevTools/Dvmp/Probes/host.cs`) fills that in. It gains
  a `port` argument and stores the result in `DevContext.State["dvmp.port"]`, which the
  client-only rejoin path in `Session.cs` reads instead of `Settings.Port`.
- After the host is up, its process must own the port in that table. If it doesn't, fail loudly
  instead of letting the client wander to whichever game does.

### Launch lock

A file in `DataRoot\launch.lock`, opened with no sharing. It is held from `Process.Start` until
the agent's discovery file appears, and around tracer installs. A file lock is used rather than a
named mutex, which is tied to one thread and doesn't suit async code. `DownAsync` quits a pair's
games one after the other.

### Separate user data per harness game

DV's `DV.UserManagement.Storage.Implementation.FileSystemStorage` has a `basePath` field and a
`(string basePathOverride)` constructor. The dev agent loads before DV creates its
`UserManager`. So when the game is launched with `-dvmp-userdata DIR`, a Harmony postfix on the
parameterless constructor, installed in `AgentMain.Load`, points the game at `DIR`. Use
`AccessTools.TypeByName`, so the agent needs no new reference. Fallbacks:
`UserManager.inMemoryTestingMode` or `UserManager.StaticReload()`.

**Check first, on an idle pair**, what `basePath` points at and whether `Preferences` lives
under it:

```bash
dvmp eval -t host -u "using DV.UserManagement;" 'return new { b = (UserManager.Instance.storage as DV.UserManagement.Storage.Implementation.FileSystemStorage)?.basePath, u = UserManager.Instance.CurrentUser.UserBasePath, p = DV.UserManagement.Data.User.GetPreferencesPath(UserManager.Instance.CurrentUser), g = UserManager.Instance.CurrentUser.GameDataPath };'
```

dvmp seeds `sessions\<s>\data\{host,client}` from the real profile, reading it only:
- **host:** `global.json`, the user's `userData.json` and `ControlBindings.json`, the
  preferences file if it falls under the base path, and every session folder named "DVMP Test…"
  (optionally only the newest saves plus `lastLoaded_0.bak`);
- **client:** the same profile files without session folders.

Seeding happens only when the folder is missing, or with `up --reseed`, because `save-reload`
and `inventory-persist` rely on saves surviving `down` and `up`. A bonus: every pair starts from
the same world, which is what repeat runs need.

When harness mode is active, clear UMM's `OnSaveGUI` for loaded mods, so harness games never
write `Settings.xml` at quit.

If the storage hook doesn't work, keep `save-reload` and `inventory-persist` exclusive on the
default session, and set `SaveGameManager.Instance.disableAutosave = true` on harness hosts.

### MCP

- Every tool gains an optional `session` parameter, defaulting to the server's `DVMP_SESSION` or
  `default`, applied with `using Sessions.Use(session)`.
- The `Session` statics become a per-session `ConcurrentDictionary`.
- An optional `dvmp_sessions` tool lists sessions with their ports, pids and stages.

### Parallel gate runner

`gate.sh` gains `PAIRS`, `REPEAT=N` and `RUNS`, and keeps `ONLY=`. With `PAIRS` and `REPEAT`
unset, today's serial run is unchanged.

**One queue for every kind of run.** A batch used to need separate invocations: the gate's list
on N pairs, then a scenario with other variables (`jobs ACCEPT=client HANDIN=host`) on one pair,
then the dedicated-host runs with `UP_ARGS=--dedicated`. Each phase waited for the slowest pair
of the one before. Now all of it is one queue:

- `PAIRS` declares pairs by kind: `PAIRS="p:2 d:1"` is player-host sessions `p1`, `p2` and a
  dedicated-host session `d1` (`dvmp up --dedicated`). A plain count (`PAIRS=3`) is player hosts,
  or dedicated ones with the older `UP_ARGS=--dedicated`.
- `RUNS` (a file, or lines separated by `;`) lists the runs instead of the gate's own list. Each
  line is a gate run name, or a scenario with its variables, plus an optional kind and label:
  `jobs ACCEPT=client HANDIN=host`, `host-actions @dedicated`, `rev: jobs ACCEPT=client`. `*`
  is the gate's whole list. Every entry carries its own variables, so the same scenario can be
  queued several ways in one invocation. Labels default to the scenario plus `[VAR=value,...]`
  and `@kind`, or the gate's run name when the variables match one.
- A worker only claims entries that fit its pair. `@kind` is a requirement (the gate exits
  before starting anything if no pair has that kind); otherwise runs go to player hosts, or to
  any pair when there are none. The gate's own list marks `dedicated-admin` as preferring a
  dedicated host.

The steps:

1. Start every pair (`dvmp up --session <s>`, plus `--dedicated` for `d` pairs) in the
   background, and wait. The launch lock staggers mod loading.
2. Expand the runs times `REPEAT`; labels that appear more than once become `name#k`. Each pair
   runs a worker that claims the next entry that fits its pair with an atomic
   `mkdir "$LOG_DIR/.queue/claim/<entry>"`, resets the player positions, runs the scenario and
   writes `$LOG_DIR/<label>.<session>.log` and a one-line result.
3. If a pair is unhealthy after a run (no world loaded), its worker runs `dvmp up` (with the
   pair's kind) to recover it.
4. A worker with nothing left to claim takes its pair down if exclusive runs follow and it
   isn't the pair that runs them, so the pairs still busy get its CPU and memory.
5. Exclusive phase: each exclusive run goes to the first pair that fits it, with every other pair
   down; a pair that was taken down comes back up for it.
6. Summary per run label: passes out of repeats, average and maximum duration, which pair, and
   the first FAIL line. Exit non-zero on any failure, and print `$LOG_DIR`.

`ONLY=x REPEAT=N PAIRS=N` is the "same scenario on every pair at once" mode. A `dvmp gate`
subcommand in C#, with better timeouts and JSON output, could replace the bash runner later.

**Scheduling:**

| Runs | When |
|---|---|
| `fps-drop`, `host-stall`, `player-soak` (and `station-load` if gated) | Always alone: they measure frame timing |
| `manual-recouple-lag`, `uncouple-moving-loco-lag`, `couple-impact` | Parallel, rerun alone on failure: lag is per process, but physics timing matters |
| `save-reload`, `inventory-persist` | Alone until separate user data lands |
| `rejoin`, `rejoin-crash`, `borrowed-disconnect` | Parallel once sessions exist |
| Everything else | Parallel |

### Lean harness games

`HarnessMode` (`DevTools/DvmpAgent/HarnessMode.cs`) already mutes, frame-caps and picks the
lowest quality level for harness-launched games. It doesn't reduce texture memory or world
streaming. Step 7 adds the following, active only when the game was launched by the harness:

- **`QualitySettings.masterTextureLimit = 2`**, set at the main menu before the world loads, so
  textures load at a quarter of their width and height. DV's own dev console has the same
  command (`Graphics_MasterTextureLimit`).
- **A 512 MB `streamingMipmapsMemoryBudget`.** DV uses Unity's texture streaming.
- **Near and far tile ranges** for `WorldStreamingInit.nearScenesStreamer` and
  `farScenesStreamer` (`-dvmp-near-range`, `-dvmp-far-range`, changeable at runtime). Track
  loads from its own scene (`railwayScenePath`), so smaller ranges only drop scenery.
- **Live values in the harness snapshot** (`textureMemoryMb`, streamer ranges and loaded tile
  counts), so the effect can be measured.

**Measured (2026-10-15),** per game at the gate's start position, after a minute's settling:

| | Before | Texture limit 2, 512 MB budget |
|---|---|---|
| VRAM | 4.7-5.0 GB | 2.9-3.0 GB |
| Textures loaded (Unity) | 2,160 MB | 588 MB |
| RAM (working set) | 5.2-5.3 GB | 5.0-5.3 GB |
| Committed memory | 11.3 GB | 9.2 GB |

- **The budget made the biggest difference.** DV sets it to about 16.9 GB, so streamed textures
  were never trimmed: games kept 2.2 GB of textures when Unity only wanted 660 MB.
- **Two pairs used 22.4 GB of the 24 GB card before, and 14.5 GB after.** Three pairs (six
  games) use 20.6 GB of VRAM and leave about 10 GB of RAM free. That's the most this machine
  should run: RAM is now the limit.
- **RAM is mostly the game itself:** about 3.1-3.4 GB of Unity allocations and a 0.9-1.3 GB
  Mono heap per game.
- **The streaming ranges are left off** (`-1`, DV's near 1 and far 2 tiles). Changing them at
  runtime didn't unload any tiles until the player crossed into a new one, and distant scenery is
  a small share of the memory anyway.

## Results

First runs, two pairs (2026-10-15):

- **Full gate: 31 minutes** with `PAIRS=2`, against about 50 on one pair. That's 22 runs shared
  between the pairs, then 5 exclusive runs on p1 (about 15 of the 31 minutes).
- Two runs failed:
  - `hazmat` failed once: the client read 8 spill tiles against the host's 10. It passed 3/3
    on repeat.
  - `explosion` failed 2 times in 4. That was a scenario bug: it compared the host's settled
    wreck with a client snapshot taken before the wreck had settled, and under parallel load the
    client's wreck took longer to settle. Comparing fresh, it passed 4/4.
- `REPEAT` made both calls quick: `ONLY="hazmat explosion" REPEAT=3 PAIRS=2` took 6.5 minutes.
- A second full gate, on the #64 build, also took 31 minutes; 25/27 passed. `hazmat` failed
  once again (the client saw the fire a moment late) and passed 3/3 on repeat, so it's flaky
  under parallel load. `host-stall` failed about 1 run in 3, on both the #64 and #65 builds,
  even when run alone. The scenario sometimes starts the stall before the train is at speed and
  compares positions mid-motion. Once, its cleanup left the train behind, and the save-writing
  runs after it saved that train into the world.

**Deploying a mod build needs every game down.** A running game holds the mod's DLLs, so the
post-build copy (and `DeployStaleBuild` on `up --fresh`) can't replace them while any session
is up. Run `dvmp down --all` before deploying a build. Until then, the sessions still running keep
the old build. That makes an easy before/after: run a scenario on every pair on the old build,
then redeploy and run it again.

## Plan

| Step | Work | Estimate |
|---|---|---|
| 1 | Session context in dvmp: discovery filter, per-session logs, reports, titles, `up`/`down`/`status` | ~0.5 day |
| 2 | Port per session, the host probe `port` argument, the post-launch port check | ~2 h |
| 3 | Launch lock, Publicizer temp name, sequential `down` | ~1-2 h |
| 4 | MCP `session` parameter, per-session state, `dvmp_sessions` | ~2-3 h |
| 5 | Separate user data: the 1 h check, then the agent hook, seeding, `--reseed`, `OnSaveGUI` | ~0.5-1 day |
| 6 | Parallel gate runner: `PAIRS`, `REPEAT`, workers, the exclusive phase, the summary | ~0.5-1 day |
| 7 | Lean harness games, then measure VRAM and RAM with 2 and 3 pairs and document the recommended `PAIRS` | ~2 h + measuring |
| 8 | `DevTools/README.md` and the regression-gate section of [testing.md](../testing.md) | ~1 h |

Steps 1-4 plus 6 already give a safe split gate, as long as save-writing runs stay exclusive.
Step 5 lifts that restriction and protects the real profile. Total: about 3-4 days.
