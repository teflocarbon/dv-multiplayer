# Regression scenarios in C#

Status: design agreed 2026-10-20 (reviewed by a second model; its changes are folded in). Replaces
the bash + `node -e` + C#-string scenarios in `DevTools/Scenarios/` one scenario at a time.

## Why

On 2026-10-20 about half of the day's scenario failures were the harness, not the mod:

| Failure | Cause | What prevents it here |
|---|---|---|
| `cab-controls/run.sh`: a regex lost its backslash | C# and JS inside bash strings | no strings of code: one C# file |
| `server-throw/throw.cs`: a setup misread a pickup | ad hoc result parsing (`field()`) | typed step results |
| `train-rate`: a test train left with its brakes on | each scenario spawns its own way | `Trains.SpawnReleased` in the library, one way |
| #171: players parented to `car.transform` | copies of the boarding code | `Players.Board` in the library only |
| `pit-stop/station.cs`, `train-rate/watch.cs` broke when a type moved | probes compile at run time | compiled against `Multiplayer.dll` at build: a moved type fails the build and CI |
| `host-stall`'s snap check judged the wrong thing | checks as `node` one-liners | `Expect` with the measured value and the limit in its message |

The rest: three languages in one scenario, `field()`/`result()`/`agree()` copied into every
`run.sh`, a Roslyn compile per `dvmp eval`, and runs that say only `FAIL`.

## Shape

```
dvmp scenario run host-stall --session scen-p --arg SPEED=10 --arg LAG=150
        │
        ▼
DvmpScenarios.exe (net48, NUnitLite)         one process per run, outside the games
  HostStall.Run(pair, options)               the orchestrator: steps, waits, checks
        │  HTTP to each game's agent: ensure-loaded(hash), invoke(type, method, args)
        ▼
host game                     client game
  DvmpScenarios.<hash>.dll      the same assembly, loaded once per game process
  HostStall.InGame.Setup(...)   steps run on the main thread, as probes do
```

### Orchestration runs outside the games

`host-stall` freezes the host's main thread on purpose and a dedicated host has no player, so the
code that sequences steps and judges results can't live in either game. Steps run inside a game
through the dev agent, like probes.

### One project, compiled against the mod

`DevTools/DvmpScenarios/DvmpScenarios.csproj` targets `net48` like the mod and references the
`Multiplayer` project and the game's assemblies, publicized with the same Krafs publicizer (Mono
doesn't check member access at run time; the mod itself and every probe rely on that). CI already
builds the mod against dv-gamelibs, so it builds this too: a moved or renamed type fails CI, not a
run. The probe libraries (`DevTools/Dvmp/Probes/lib/*.cs`) are linked in, not copied, so ad hoc
`dvmp eval` and scenarios share one copy of each helper during the migration.

The same assembly is both the orchestrator (run by the exe) and the step code (loaded into the
games). The exe never executes game code: it loads game types only as metadata, for the expression
trees and result types. An `AppDomain.AssemblyResolve` hook finds the game's DLLs in
`DerailValley_Data/Managed` and the mod folder of the install dvmp points at.

### A scenario is one file, two classes

```csharp
[Scenario("host-stall", "The host stalls at 2 fps with a train at speed: no coupling, hose or cock is lost, the brake pipe holds, and the peers agree where it stops")]
public sealed class HostStall : Scenario<HostStall.Options>
{
    public sealed class Options
    {
        public int Stall = 30;        // STALL: seconds at about 2 fps
        public float Speed = 8;       // SPEED: m/s
        public int Wagons = 4;
        public bool Ride = true;      // RIDE: the client in the loco's cab
        public int Lag;               // LAG: client receive lag, ms
        public int Hold = 15;         // HOLD: game seconds at speed
    }

    protected override async Task Run(Pair pair, Options o)
    {
        float room = o.Speed * (o.Hold + 4) + 60;
        Train train = await pair.Host.Run(() => Trains.SpawnReleased("LocoDE6", o.Wagons, room));
        pair.Cleanup(() => pair.Host.Run(() => Trains.Delete(train.Cars)));
        if (o.Ride) await pair.Client.Run(() => Players.Board(train.Loco));

        using (await pair.Client.Lag(o.Lag))
        {
            var watchers = pair.Both.Start(() => InGame.Watch(train.Cars, o.Stall + 120));
            var push = pair.Host.Start(() => Trains.Push(train.Loco, train.Dir * o.Speed, o.Hold, 4f));
            Require.AtLeast(await pair.Host.Run(() => InGame.WaitForSpeed(train.Loco, 0.8f * o.Speed, 30)),
                0.8f * o.Speed, "the train's speed before the stall (m/s)");
            Stalled s = await pair.Host.Run(() => InGame.Stall(o.Stall));
            Report.Note($"{s.Frames} frames, host game time {s.GameSeconds}s in {s.RealSeconds}s");
            await push;
            foreach (var (peer, w) in await watchers)
            {
                Expect.Zero(w.WorstUncoupled, $"{peer}: couplings lost");
                Expect.Zero(w.WorstHoses, $"{peer}: hoses off");
                Expect.Zero(w.WorstCocks, $"{peer}: cocks shut");
                Expect.AtLeast(w.MinPipe, 3.5f, $"{peer}: lowest brake pipe pressure (bar)");
            }
        }
        await pair.Host.Run(() => Trains.WaitUntilStopped(train.Loco, 30));
        var final = await pair.Both.Run(() => InGame.LastWagon(train.Cars));
        Expect.AtMost(Peers.Distance(final.Host, final.Client), 1f, "peers apart on the last wagon (m)");
    }

    // ---- runs inside the games
    public static class InGame
    {
        [Step] public static IEnumerator Watch(Step<Watched> step, string[] cars, float seconds) { ... }
        [Step] public static IEnumerator Stall(Step<Stalled> step, int seconds) { ... }
        [Step] public static Abs LastWagon(string[] cars) => Dev.AbsOf(Trains.Find(cars.Last()).transform.position);
    }
}
```

- **What runs where is a compile error, not a convention.** In-game code is in `[Step]` methods
  (in a nested `InGame` class, or the shared library); everything else is the orchestrator. A
  Roslyn analyzer in the project, alongside `Tools/RoleAnalyzer` (DVMP0001), adds two rules: orchestrator
  code may not use `UnityEngine`, `Assembly-CSharp` or `Multiplayer` types except inside the lambda
  passed to `Run`/`Start` (DVMP0101), and a `[Step]` method may not be called directly (DVMP0102).
- **Step calls are typed by expression trees.** `pair.Host.Run(() => InGame.Stall(o.Stall))` never
  calls `Stall` in the exe: `Run` reads the `MethodCallExpression`, evaluates each argument
  (`Expression.Lambda(arg).Compile()()`), and sends the declaring type, the method name, the
  parameter type names (overloads) and the arguments as JSON. Argument and result types are checked
  by the compiler. The body must be one call to a static `[Step]` method; anything else throws at
  the call site with the reason.
- **Step contracts are plain data.** Arguments and results are primitives, strings, arrays and
  records declared in the scenarios project (`Train`, `Watched`, `Abs`), never `TrainCar` or
  `Vector3`: the agent's JSON converters write Unity types but can't read them back
  (`DvmpAgent/Json.cs`), and a game object means nothing in the other process. `Run<T>` rejects
  other types when it builds the call.
- **Steps** are coroutines (`IEnumerator M(Step<T> step, ...)`, result in `step.Result`) or plain
  methods returning `T`. They report notes with `step.Note(...)`, which the run prints under the
  step's line.
- **Parallel steps**: `Start` returns a running step to await later; `pair.Both` runs on host and
  client at once and returns both results with the peer's name. A dedicated host runs host steps as
  usual; `Players.*` steps on a dedicated host fail the call with "no player on a dedicated host".

### Checks

- `Expect.*` records a failure and continues, so a run reports every broken check (as
  `host-stall`'s `bad[]` list did). `Require.*` stops the run. Both print the measured value against
  the limit: `FAIL client: lowest brake pipe pressure (bar): 2.8, expected at least 3.5`.
- They are NUnit's `Assert.Multiple` and `Assume`/`Assert` underneath (see "Runner").
- `Report.Note` for numbers that are reported but don't fail the run (`host-stall`'s snaps: the
  user's rule is that it fails only on breakage).

### Waiting

`Until(condition, timeout, what)` is the only way to wait, in steps and in the orchestrator; a
timeout fails with `what` and the last value seen. Bare `WaitForSeconds` in a step is reserved for
the thing being tested (a stall, a deliberate gap) and says so in a comment.

### Shared library

Linked from `Probes/lib`, plus scenario-only helpers:

| Helper | Does |
|---|---|
| `Trains.SpawnReleased(livery, wagons, clearMetres)` | spawns on a straight with that much clear track, brakes released, charged pipe; returns `Train` |
| `Trains.Delete(cars)`, `Trains.Push`, `Trains.WaitUntilStopped` | as today's lib, typed |
| `Players.Board(car)` | the one boarding path: the floor's reparent target, `car.interior` (#171) |
| `Players.TeleportHome()`, `Players.TeleportToGround(x, z)` | |
| `World.Baseline()` / `World.Reset()` | see "Isolation" |
| `Peers.Distance(Abs, Abs)` | positions from `Dev.Abs`, so each game's floating origin cancels out |

### Isolation

Each run starts from the same state and is checked to leave it:

- `World.Reset()` before a run: players home, hands and inventories emptied of test items, money
  topped up (`MONEY`), cars and items a scenario spawned deleted on the host. One in-process call
  instead of gate's six `dvmp eval`s in `teleport_home` (14 to 40 s per run, docs/research/harness-time-budget.md).
- A leak check after a run, on both peers: no car or item the run spawned is left, nothing is held,
  no control lease is held. A leak fails the run with what leaked, instead of `teleport_home`
  hiding it from the next run.
- Scenarios that write saves load a fresh copy of the session's baseline save, so the save doesn't
  fill up run after run.
- `[Needs(FreshWorld)]` for the few that need a restart; only those pay for a load.

### Restarts inside a scenario

`save-reload`, `host-player-persist` and `rejoin` restart games as the test. The library gives them
`pair.Restart(save)` and `pair.Client.Rejoin()`, which run the same code as `dvmp up`/`down` for the
run's session (the exe calls `dvmp.exe`). Scenarios that use them declare `[Needs(RestartsGames)]`
so gate can schedule them. `scenario run` itself never starts a pair: gate.sh owns pairs, as it does
for `run.sh` today.

### Loading into the games

One agent change: `/v1/load` takes the scenario assembly and its SHA-256 and loads it once per game
process, keyed by hash; `/v1/invoke` calls a `[Step]` method by type, name and parameter types with
JSON arguments, with the same coroutine, timeout and main-thread handling as `/v1/eval`.

- **Ensure loaded.** Before the first step on a game, and whenever that game's `pid` or `launchId`
  changes (a restart), the exe asks `/v1/load` with the hash only; a 404 means upload.
- **A unique name per build.** The exe renames the assembly `DvmpScenarios.<hash>` (Cecil) before
  uploading, as probes get a unique name per compile, so a game that stays up across rebuilds never
  holds two assemblies with one name.
- **Stale installs are a harness error.** The build embeds the MVIDs of the `Multiplayer.dll` and
  `Assembly-CSharp.dll` it compiled against; `/v1/load` compares them with the game's loaded
  modules and refuses on a mismatch: "the game runs a different Multiplayer.dll than the scenarios
  were built against: rebuild, or `dvmp up --fresh`". Without this, a stale install fails halfway
  through with `MissingMethodException`, counted as a FAIL.
- **Cancel.** `/v1/cancel` stops a running step (the `cancelled` flag `/v1/eval` already has). The
  exe cancels steps it started and didn't await when a run ends or fails, so a watcher doesn't
  outlive its run by up to 10 minutes.
- A blocking step (a stall) also blocks `/v1/status` on that game. Gate's health check waits for
  it; the exe doesn't poll status during a run.

### Runner

The exe is NUnitLite: the scenarios are NUnit tests in a self-running console app, so there is no
test host to start (`dotnet test` costs 3 to 5 s a run) and nothing to write for:

| Need | NUnit |
|---|---|
| keep going after a failed check | `Assert.Multiple` (under `Expect`) |
| a pair that can't run this scenario | `Assume` → Inconclusive → exit 3 (harness, not a test result) |
| cleanup that runs on failure | `[TearDown]` (under `pair.Cleanup`) |
| a time budget per run | `[Timeout]` from the scenario's `[Budget]` |
| artifacts | `TestContext.AddTestAttachment`, result XML |
| options | `--testparam:SPEED=10` (from `--arg`) |

`dvmp scenario run` maps its arguments onto that and the exit codes: 0 pass, 1 fail, 3 harness
error (couldn't reach the games, stale install, the pair lacks what `[Needs]` asks). Unknown options
fail the run: `FOO=1` is a typo, not a no-op.

### Output

```
== host-stall  session scen-p (player host)  STALL=30 SPEED=10 LAG=150
[  0.0s] host    Trains.SpawnReleased  ok   6.2s  LocoDE6+4 on #1234, 520 m clear
[  6.2s] client  Players.Board          ok   3.1s
[  9.3s] both    InGame.Watch          started
...
[ 98.4s] check   host: lowest brake pipe pressure (bar)    4.8 ≥ 3.5   ok
[ 98.4s] check   peers apart on the last wagon (m)         0.41 ≤ 1    ok
PASS  host-stall  98.4s
```

On a failure the run saves, into `$LOG_DIR/<run>/`: screenshots of both games, their log lines
since the run started, every step's arguments and result as JSON, and the game log paths.

### Commands

```
dvmp scenario list                     names, descriptions, options with defaults, [Needs]
dvmp scenario check                    build-time checks without games: every scenario loads, every step resolves
dvmp scenario run NAME [--arg K=V]...  one run against the session's pair
```

### gate.sh

Unchanged syntax: the run list, `RUNS`, `PAIRS`, `@dedicated`, `REPEAT` and the summary.

- `run_one` runs `dvmp scenario run <scenario> --arg ...` when the scenario has no `run.sh`, with
  the run's variables as `--arg`s, and reads exit code 3 as ERROR instead of grepping messages.
- The gate builds `DvmpScenarios` once before the first run, so a scenario that doesn't compile
  stops the gate at the start.
- The queue's validation (`[ -f "$S/$name/run.sh" ]`) also accepts names from `dvmp scenario list`.

## Migration

- Port by flake rate, keeping each check's meaning. First: `cab-controls` `MODE=cab-reload`,
  `host-stall` (from #202), `save-reload`; `train-rate` after #174 merges.
- A ported scenario's `run.sh` and probes are deleted in the same PR; gate runs whichever exists.
- No new `run.sh`. A flaky one is ported, not patched.
- Each port runs in game on both a player host and a dedicated host before its PR.

## Considered and not done

- **The orchestrator in dvmp (net8)**, with each scenario's contracts in a file compiled into both
  dvmp and the game assembly: three pieces per scenario, with steps named by strings underneath.
- **The orchestrator inside the host's game**: the host is what `host-stall` freezes, and a dedicated
  host has no player.
- **Unity Test Framework, AltTester, GameDriver, Unium**: they need the game's Unity project or drive
  one process's UI. The work here is multi-process orchestration, which the agent already provides.
- **A custom map (Mapify)**: dropped (docs/research/mapify-test-map.md). The framework doesn't
  assume the map: `[Needs]` can say what a scenario needs from the world.
- **Snapshots of a loaded game's memory**: a game's state isn't only its memory (GPU state, kernel
  handles, Steam's IPC, sockets), and Windows has no process checkpoint. The in-world reset above is
  the cheap form of the same isolation; faster launches are #209.
