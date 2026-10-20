# Harness time budget: where dvmp spends wall-clock time

Status: measured 2026-10-20 from logs already on disk; no games were launched for it. This doc
decides which of three speed-ups is worth building: an in-world reset instead of a restart, faster
launches, and hot-reloading the mod after a build.

**In short:** a fresh host + client pair takes about 3.5 minutes (median 211 s, p90 261 s) from
host process start to a joined client. Only about a third of that is vanilla world loading (36 s on
the host, 33 s on the client). Most of the rest is two things. Booting to the main menu takes 44 s,
half of it loading the 21 installed mods. Then the host idles at its menu for 67 s while dvmp waits
for the client to boot.
In the 94-run gate batch of 2026-10-20 (`q2`), games were loading for 26 % of the pair-time. Restarts
inside the restart scenarios were the largest share (18 %), and the initial `up` was 6 %. Making
launches faster wins the most for the least work. Hot reload wins the least for the most.

## Sources and method

| Source | What it gave |
|---|---|
| Game logs, `%LOCALAPPDATA%\dvmp\logs` and `sessions\*\logs` (278 launches, 2026-10-17 to 10-20) | Each launch's mod load times (UMM's `Loading time`), the mod's timestamped `[Multiplayer]` lines (server start, join steps, loading screen removed), and vanilla's `[Loading] … (step: N, frame: F)` lines |
| The mod's event stream, `Multiplayer.Debug/dvmp-debug-<launch ID>.jsonl` in the Steam install and the three test installs (172 of the 278 launches still have one) | `processStartedUtc`, the mod's start, every `scene.loaded`/`scene.unloaded`, and a frame number on every event. That frame number maps vanilla's frame-stamped `[Loading]` steps onto wall-clock time |
| Gate LOG_DIRs left in other sessions' scratchpads (`…\Temp\claude\*\scratchpad\**\.queue`, 86 of them, 2026-10-14 to 10-20) | `up-<session>.log` (dvmp's own stage timeline for each pair) and `.queue/result/*` (each run's result, seconds and end time) |
| `%LOCALAPPDATA%\dvmp\durations` | The last 5 passing durations of each gate run |
| Decompiled `WorldStreamingInit`, `RenderTextureSystem`, `StationProceduralJobsController` | What vanilla waits for after it logs `[Loading] Done` |

There are no `multiplayer-<launch ID>.log` files: the mod writes only the event stream to
`Multiplayer.Debug`. Its `[Multiplayer]` log lines go to the game log, in UTC. Launch IDs are local
time.

The per-launch tables exclude the `aiexport` session, which runs from a separate install with
other mods. A "fresh pair" is a client launched within 2 minutes of its session's host. A
"client-only relaunch" is any other client launch: a rejoin, or a client brought back to a healthy
host. The parsing scripts are in the shared session notes folder
(`side-session-notes\local_bf463f49-…\harness-time-budget\`).

Caveats:

- Vanilla `[Loading]` steps carry a frame number, not a time, and some load frames last 4 to 8 s.
  So a step's own time is known only to within one frame. Some time can land in the step before
  or after it; the totals are exact.
- Every number was measured under the gate's own contention, with up to 6 games on the machine at
  once, minimised and frame-capped. That is the situation being optimised, so it's the right
  baseline. A single pair on an idle machine is faster.
- The 5-mod launches compared below are from 2026-10-17, an older build of the mod. That comparison
  is confounded and is used only as a bound.

## Per launch

Times in seconds, median and p90. `n` is the number of launches with that phase measured.

| Phase | Player host | Dedicated host | Client |
|---|---|---|---|
| Process start → Multiplayer mod loading (Unity up, UMM running) | 14.5 / 17.2 (94) | 15.8 / 24.5 (20) | 14.6 / 18.5 (148) |
| → all mods loaded (`bootstrap` scene) | 24.6 / 28.4 (61) | 25.0 / 36.7 (19) | 25.7 / 30.1 (92) |
| → main menu scene | 3.7 / 4.4 | 4.0 / 5.5 | 3.8 / 4.6 |
| **Process start → main menu** | **43.6 / 51.6** | **45.4 / 67.6** | **45.1 / 53.9** |
| At the main menu, waiting on dvmp (see [below](#the-host-waits-for-its-clients-boot)) | 67.0 / 125.2 | 82.7 / 127.3 | 69.5 / 81.6 (until join start) |
| World load start → server listening | 4.7 / 5.4 | 4.8 / 6.5 | – |
| World load start → vanilla `[Loading] Done` | 36.0 / 42.2 | 38.7 / 47.2 | 33.0 / 42.0 (join: save data → world loaded) |
| `Done` → `WorldStreamingInit.IsLoaded` → own client out of loading mode (host ready) | 26.6 / 29.7 | 26.1 / 32.8 | – |
| Join: world loaded → left loading mode (the mod's sync) | – | – | 15.0 (world state 0.8, trainsets 7.5 / 11.7, items 6.3 / 11.0) |
| **Join start → joined** | – | – | **49.1 / 65.6** |
| **Process start → ready** (host: own client out of loading mode; client: joined) | **155.7 / 223.4** | **196.1 / 240.4** | **143.6 / 191.3** |
| Client-only relaunch: process start → joined | – | – | 89.0 / 171.8 (43) |
| Host ready → client join start (dvmp polls, compiles and runs the join probe) | – | – | 4.8 / 6.3 |
| Host ready → client joined, fresh pair | 55.1 / 71.4 | 57.3 / 84.5 | – |
| **Fresh pair: host process start → client joined** | **211.3 / 260.7 (81)** | **250.3 / 317.4 (16)** | – |

The server starts listening 5 s into the host's world load, well before the world is loaded. The
client still can't usefully join earlier: the host sends save data only once its server has
loaded, so the client's world load can't overlap the host's.

### What dvmp's own stage timeline says

Stage times from the `up-*.log` files, median seconds, for pairs launched fresh. Gates since
2026-10-19 run on the test install with 21 mods. Older ones ran with 5 (Multiplayer,
MultiplayerDevTools, DVLangHelper, DVCustomCarLoader, C501).

| `dvmp up` stage | 1 pair, ≤10-17 (n 2) | 1 pair, 10-19+ (n 16) | 3 pairs, ≤10-17 (n 15) | 3 pairs, 10-19+ (n 63) |
|---|---|---|---|---|
| launching host → host agent up | 13.2 | 47.7 | 27.4 | 76.7 (p90 154.7) |
| launching client → client agent up (launch lock held) | 14.1 | 45.0 | 33.5 | 80.9 (p90 150.2) |
| host: menu wait + `host` probe | 1.5 | 4.6 | 3.4 | 4.0 |
| host: world + server (`IsLoaded`) | 43.1 | 69.2 | 46.9 | 62.5 |
| client: menu wait + `join` probe | 3.1 | 3.8 | 3.2 | 3.4 |
| client: world | 29.2 | 43.8 | 31.5 | 38.4 |
| client: loading screen | 8.2 | 15.7 | 7.7 | 17.0 |
| **total** | **109** | **243 (p90 325)** | **161 (p90 211)** | **319 (p90 377)** |

The total for one pair more than doubled between 10-17 and 10-19. Most of that is the two "agent
up" stages, which wait for UMM to load every mod. The rest is the world getting heavier on both
sides: custom cars in the car pool, and the mod's own sync.

### Which vanilla systems dominate a world load

Player host, from vanilla's `[Loading]` steps (medians; frames counted from the event stream):

| Step | Seconds | Frames | Bound by |
|---|---|---|---|
| 1 loading start game data (save read, `StartGameData`) | 4.5 | 1 | one long frame |
| 2 initializing vegetation | 3.5 | 1 | one long frame |
| 3–5 terrains, railway layout, railway visuals | 1.5 | 6 | – |
| 6 loading game content (`game_content_w3` scene) | 7.6 | 2 | scene load |
| 7 creating player | 2.0 | 1 | – |
| 8 initializing car pool (~150 cars, custom cars included) | 6.2 | 45 | CPU (137 ms/frame) |
| 9 restoring game state from the save | 1.1 | 3 | – |
| 10 waiting for terrains | 2.8 | 47 | **frame cap** (59 ms/frame) |
| 11–12 WorldStreamer: init, then 34 tile scenes | 6.7 | 65 | frames (97 ms/frame) |
| **`Done` → `IsLoaded` (host only)** | **26.6** | **287** | **frames: one render job per frame** |

- **Scene loading and content (steps 1–7): about 19 s.** These are a handful of very long frames
  (game data, vegetation, the `game_content_w3` scene). They are bound by CPU and IO, and nothing
  in the harness changes them.
- **Car pool and save restore (steps 8–9): 7 s.**
- **Terrain and WorldStreamer (steps 10–12): 9.5 s**, spread over about 110 frames. Both wait for
  streaming to finish frame by frame, so they speed up with frame rate.
- **The wait after `Done`: 26.6 s on every host load, the largest single item.** Vanilla sets
  `IsLoaded` and raises `LoadingFinished` only once three more waits are over: station job
  generation, the weather driver, and `RenderTextureSystem.PendingJobs == 0`.
  `RenderTextureSystem` renders one queued texture per frame (booklet pages, licences, the vehicle
  catalogue, item icons). On a host about 290 are queued, so this wait is about 290 frames. Job
  generation doesn't contribute: it starts later, near stations. The mod's server starts only on
  `LoadingFinished` (`NetworkServer.OnLoaded`), so its own first-tick stall falls inside this
  window. The first `OnTick took N ms` warning per host has a median of 2.1 s and a p90 of 7.9 s
  (n 121), and was 9.1 s in the sample read by hand, at the world-item persistence import.
  A harness host is minimised and capped at 20 fps in the background. Hosts launched with
  `--fps 60` (and a background cap of 60) waited 14.5 s for the same 287 frames, against 26.6 s at
  the default caps.
  A client has nothing queued here (1 frame).
- **Shaders:** the logs show only "Shader Unsupported" warnings. Unity 2019 compiles shaders
  lazily, and no load phase points at shader cost. **Saves** are cheap: the read is part of step 1
  and the restore is step 9, about 1 s.

On a client, the mod's own sync after the world loads takes 15 s, over only about 35 frames at
about 0.5 s per frame: spawning 33 trainsets / 154 cars (7.5 s) and applying items (6.3 s). That's
CPU in the mod, not the frame cap.

### Mod loading

UMM's per-mod `Loading time`, summed: **32.0 s per launch with the 21-mod set** (p90 39.0, n 173),
against 9.8 s with the 5-mod set. The heavy ones are the custom locomotives:

| Mod | Median s |
|---|---|
| S464-730 Dreyfuss | 7.6 |
| C501 | 4.9 |
| Multiplayer | 4.6 (2.9 s of it Harmony patching) |
| FEF-3 | 3.8 |
| UP_HF | 2.1 |
| DVSeasons | 2.0 |
| S484-841, S484-840, A.T.&S.F. 2901 | 1.9, 1.2, 1.0 |
| DVCustomCarLoader, SkinManagerMod | 0.8, 0.7 |
| the other 10 | under 0.4 each |

The mods other than Multiplayer, MultiplayerDevTools and DVLangHelper cost about 22 s per launch
before the main menu. They likely also add world-load time (custom cars in the car pool, DVSeasons'
terrain work), but that can't be separated cleanly from the logs.

### The host waits for its client's boot

`Session.RunUpAsync` launches the host and waits for its dev agent. It then launches the client and
waits for the client's dev agent too (`LaunchAsync`, holding the launch lock), and only then calls
`HostAsync` to load the save. So the host sits at its main menu while the client boots: **67 s
median, 125 s p90**. With three pairs, the launch lock also queues each client behind the other
sessions' launches. The lock exists so that UMM never loads mods in two games at once. That
requires only that the client starts after the host's mods are loaded, which is already true when
`HostAsync` could start.

## Per batch

### How many launches, and why

| Batch | Runs | Pairs | Wall | Launches | Why |
|---|---|---|---|---|---|
| `q2` (2026-10-20, the gate with REPEAT) | 94 | 3 (p1, p2, d1) | 98 min | 35 | 6 initial (`up` after a build); 27 inside runs: `save-reload` ×4 runs × 2 cycles × 2 games = 16, `host-player-persist` ×2 → 4, `rejoin` ×5 → 5 client relaunches, `borrowed-disconnect` ×2 → 2; 2 relaunches of an unhealthy pair between runs |
| `q3` | 35 | 3 | 39 min | 20 | 6 initial; 14 inside runs (`save-reload` 2×4, `inventory-persist` 2, `host-player-persist` 2, `rejoin` 1, `borrowed-disconnect` 1) |
| `q4` | 13 | 3 | 31 min | 6 | initial only |
| edit–build–test loop | 1–3 | 1 | ~5 min | 2 per `up --fresh`, plus any restart in the scenario | the new DLL needs both games restarted: 243 s median (p90 325) for one pair, before a scenario of typically 30–90 s |

Only seven gate runs restart games, and they do it because restarting is what they test:
`save-reload`, `inventory-persist`, `host-player-persist` (`dvmp down` + `up --save`), `rejoin` and
`rejoin-crash` (client), `borrowed-disconnect` (client), and `dedicated-admin` on a dedicated host
(client). Every other run reuses the running pair. Between runs, `teleport_home` puts players back,
clears mugs and tops up money; that is already a small in-world reset.

Across one day (10-20), the four gate sessions (p1, p2, p3, d1) made 138 launches. Those add up to
**6.6 game-hours of loading**, and at least one game was loading for 2.9 h of the 15.7 h span.

### Loading versus scenarios

Pair-time is wall time × pairs. Loading is the union of each launch's process start → ready inside
the batch.

| Share of pair-time | `q2` (94 runs) | `q3` (35 runs) |
|---|---|---|
| Initial `dvmp up` | 1,058 s (6 %) | 986 s (14 %) |
| Restarts inside runs | 3,261 s (18 %) | 1,583 s (22 %) |
| Relaunches of unhealthy pairs | 183 s (1 %) | 0 |
| **Loading, total** | **4,502 s (26 %)** | **2,569 s (36 %)** |
| Scenario work (run time less its restarts) | 6,336 s (36 %) | 2,657 s (38 %) |
| Gate bookkeeping between runs (`teleport_home`, coupler-break log scans, status checks) | 1,559 s (9 %), 18 s per run | 669 s (9 %), 20 s per run |
| Idle: tail imbalance, and the exclusive runs at the end with the other pairs down | 5,239 s (30 %) | 1,170 s (17 %) |
| **Pair-time** | **17,636 s** | **7,065 s** |

On the wall clock, `q2` spent 7 min in the initial `up`, 63 min with three pairs running in
parallel, and 28 min in the exclusive phase (9 runs alone). The ten larger batches checked (back to
2026-10-14) show the same pattern: the gap between runs was 14–40 s per run in each.

## The three savings, ranked

Estimates are per `q2`-sized batch (98 min wall on 3 pairs) and per edit–build–test loop (one
`up --fresh` of a single pair, then a scenario).

### 1. Faster launches: about 2 min per pair-up, 10–15 min per full gate

Every launch pays these costs: initial `up`s, every restart inside a scenario, and every unhealthy
pair.

| Change | Measured cost today | Expected win |
|---|---|---|
| Start the host's world load (`HostAsync`) while the client boots, instead of after the client's agent is up | host idle at menu 67 s median, 125 s p90 | 45 s per fresh pair, up to 80–150 s at 3 pairs, where the client also waits on the lock |
| A lean mod set for the gate install: Multiplayer, MultiplayerDevTools, their dependencies. Run the full 21-mod set separately as a mod-compatibility pass | 32 s of UMM load per launch vs 10 s; "agent up" 46 s vs 14 s, serialised by the launch lock | 22 s per launch directly. A 3-pair `up` drops by roughly 2 min (lock waits shrink with it). Some world-load gain too (custom cars), unmeasured |
| Lift the frame cap while loading (until `IsLoaded` / out of loading mode), then return to the 30/20 background caps | host `Done` → `IsLoaded` is 287 frames: 26.6 s at 30/20, 14.5 s at 60/60 | 12 s per host launch, more uncapped. Terrain/streaming steps 10–12 (about 110 frames) also gain a few seconds on both sides |
| Mod-side: the host's first-tick stall (world-item persistence import), and the client's trainset/item apply | 2.1 s median / 7.9 s p90; 14 s at about 0.5 s per frame | a few seconds each; these are mod performance bugs, worth fixing for players too |

Together these take a fresh pair from about 211 s to an estimated 110–130 s, and a 3-pair `up`
from about 320 s to about 150 s. In `q2` they cut the 4,500 pair-s of loading by roughly 40–45 %:
**about 2,000 pair-s, or 11–15 min of wall time**. In an edit–build–test loop, the 4-minute
`up --fresh` becomes about 2 minutes. The first change touches a few lines of `Session.cs`. The
second is a test-install change, and the decision about mod-compatibility coverage is the user's.
The third is a launch argument and a check in the agent.

### 2. In-world reset instead of a restart: 5–10 min per full gate, mostly not from loading

The gate already avoids restarts between runs. Restarts happen where restarting is the test, plus
1 % of pair-time for unhealthy pairs. So a reset to a baseline saves loading time in only two
ways:

- **Unhealthy-pair relaunches** (`ready()` calling `dvmp up`): 183 pair-s in `q2`, 1 %. A reset that
  repairs a pair in place (back to the menu and reload, or a baseline-state probe) saves most of
  that.
- **In-process reload for the save/reload family.** `save-reload`, `inventory-persist` and
  `host-player-persist` could go back to the main menu and continue the save, instead of quitting
  the processes. That skips the boot, the launch lock and the host's menu wait: about 100 s of each
  211 s pair restart. In `q2` that is about 1,500 of the 3,261 in-run pair-s (8 %), about 8 min of
  wall. It changes what is tested, though: static state survives an in-process reload, and real
  players also quit and relaunch. So keep at least one real process restart per persistence
  scenario.

The bigger win from a reset is in work that isn't loading: the 14–40 s of bookkeeping per run (9 %
of pair-time). The C# scenario framework being designed in parallel (`docs/design/scenarios.md`) could do
the baseline reset, the money top-up and the coupler-break count in one compiled in-process call
instead of six `dvmp.exe` invocations. That would recover most of the 1,559 pair-s in `q2`. The 30 %
idle (tail imbalance and the exclusive phase) is a scheduling question, not a loading one.

### 3. Hot-reloading the mod after a build: about 1–2 min per build, at high cost and risk

A hot reload replaces one `up --fresh` per build: 243 s for one pair, about 320 s at three. In a
batch that is the initial `up`: 6 % of `q2`'s pair-time, 14 % of `q3`'s. It can't skip the world
load, though. The mod builds its registries, networked IDs, item catalogue and train topology while
the world loads, and its server starts on `LoadingFinished`. So even a working hot reload has to
reload the world, about 36 + 27 + 49 s ≈ 110 s per pair. That leaves only the boot as the saving:
about 100 s now, and about 50 s once item 1 is done.

The cost is high:

- Mono can't unload `Multiplayer.dll`.
- PassengerJobs binds to its API, and probes compile against it.
- Harmony patches, MonoBehaviours on live objects, and static state all have to be torn down and
  rebuilt.

It's the lowest priority. Item 1 plus an in-process "back to menu, reload" (item 2) gets most of its
benefit.

## Follow-ups this turned up

- `Session.RunUpAsync`: run `HostAsync` concurrently with the client's `LaunchAsync`.
- The test install's mod set: a lean gate install, and a separate mod-compatibility pass.
- Lift the frame cap during loading in the dev agent.
- The host's first-tick stall at the world-item persistence import (`OnTick took` up to 9 s).
- Event-stream volume: `Multiplayer.Debug` holds 166 GB across the four installs, about 200 MB per
  launch. That is disk churn during every load, and nothing prunes it.
- Logging: the mod's event stream has no "world loaded" or "loading screen removed" event on the
  client, so this analysis needed the game log for those. An event for each would make the next
  measurement a single query.
