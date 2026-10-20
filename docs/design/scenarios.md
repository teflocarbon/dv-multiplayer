# Regression scenarios in C#

Status: built 2026-10-20 (design reviewed by a second model; its changes are folded in). Replaces
the bash + `node -e` + C#-string scenarios in `DevTools/Scenarios/` one scenario at a time. Ported:
`host-stall`, `cab-reload` (was `cab-controls MODE=cab-reload`), `save-reload`, `rejoin`. What isn't built
yet is listed under [Next](#next).

## Why

On 2026-10-20 about half of the day's scenario failures were the harness, not the mod:

| Failure | Cause | What prevents it here |
|---|---|---|
| `cab-controls/run.sh`: a regex lost its backslash | C# and JS inside bash strings | no strings of code: one C# file |
| `server-throw/throw.cs`: a setup misread a pickup | ad hoc result parsing (`field()`) | typed step results |
| `train-rate`: a test train left with its brakes on | each scenario spawns its own way | `Cars.SpawnTrain` / `Cars.SpawnHeldLoco` in the library, one way each |
| #171: players parented to `car.transform` | copies of the boarding code | `Players.Board` in the library only |
| `pit-stop/station.cs`, `train-rate/watch.cs` broke when a type moved | probes compile at run time | compiled against `Multiplayer.dll` at build: a moved type fails the build and CI |
| `host-stall`'s snap check judged the wrong thing | checks as `node` one-liners | `Expect` with the measured value and the limit in its message |

The rest: three languages in one scenario, `field()`/`result()`/`agree()` copied into every
`run.sh`, a Roslyn compile per `dvmp eval`, and runs that say only `FAIL`.

## Shape

```
dvmp scenario run host-stall --session scen-p --arg SPEED=10 --arg LAG=150
        │  (passes --session, --dvmp and DVMP_DV_DIR on)
        ▼
DvmpScenarios.exe (net48)                    one process per run, outside the games
  HostStall.Run(pair, options)               the orchestrator: steps, checks, notes
        │  HTTP to each game's agent: /v1/load (once per launch), /v1/invoke (each step)
        ▼
host game                          client game
  DvmpScenarios_<id>.dll             the same assembly, renamed, loaded once per game process
  HostStall.InGame.Stall(...)        steps run on the main thread, as probes do
```

### Orchestration runs outside the games

`host-stall` freezes the host's main thread on purpose and a dedicated host has no player, so the
code that sequences steps and judges results can't live in either game. Steps run inside a game
through the dev agent, like probes.

### One project, compiled against the mod

`DevTools/DvmpScenarios/DvmpScenarios.csproj` targets `net48` like the mod and references the built
`Multiplayer.dll` (never building or deploying it: build the mod first) and the game's assemblies,
publicized with the same Krafs publicizer (Mono doesn't check member access at run time; the mod
itself and every probe rely on that). CI builds it after the mod, so a moved or renamed type fails
CI, not a run. The probe libraries (`DevTools/Dvmp/Probes/lib/*.cs`) are linked in, not copied, so
ad hoc `dvmp eval` and scenarios share one copy of each helper during the migration. The agent is a
project reference built in Release, which never installs itself into the game.

The same assembly is both the orchestrator (run by the exe) and the step code (loaded into the
games). The exe never executes game code: it loads game types only as metadata, for the expression
trees and result types. `Program.Resolver` finds the game's DLLs in the install dvmp points at
(`DerailValley_Data/Managed`, the mod folders). One exception: the game's `Newtonsoft.Json.dll`
fails .NET Framework's strong-name check, so the exe ships NuGet's; inside a game the same identity
binds to the game's.

### A scenario is one file

```csharp
[Scenario("host-stall", "The host stalls at about 2 fps with a train at speed: ...")]
[Budget(600)]
public sealed class HostStall : Scenario<HostStall.Options>
{
    public sealed class Options
    {
        public int Stall = 30;        // STALL: seconds at about 2 fps
        public float Speed = 8;       // SPEED: m/s
        public bool Ride = true;      // RIDE: the client in the loco's cab
        public int Lag;               // LAG: the client's receive lag, ms
        ...
    }

    protected override async Task Run(Pair pair, Options o)
    {
        Train train = await pair.Host.Run<Train>(s => Cars.SpawnTrain(s, "LocoDE2", "BoxcarBrown", o.Wagons, clear), 150);
        pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, train.Cars)));
        if (o.Ride) await pair.Client.Run(() => Players.Board(train.Loco));
        using (await pair.Client.Lag(o.Lag))
        {
            var watchers = pair.Both.Start<Watched>(s => InGame.Watch(s, train.Cars, watch), watch + 30);
            var push = pair.Host.Start(() => Cars.Push(train.Loco, train.Dir * o.Speed, o.Hold, 4f), watch + 30);
            Require.AtLeast(await pair.Host.Run<float>(s => Cars.WaitForSpeed(s, train.Loco, 0.8f * o.Speed, 30)),
                0.8f * o.Speed, "the train's speed before the stall (m/s)");
            Stalled stall = await pair.Host.Run<Stalled>(s => InGame.Stall(s, o.Stall), o.Stall + 60);
            ...
        }
        foreach (var (peer, w) in new[] { ("host", hostWatch), ("client", clientWatch) })
            Expect.AtLeast(w.MinPipe, 3.5, $"{peer}: lowest brake pipe pressure (bar)");
        Expect.AtMost(Peers.Horizontal(hostFinal.Final.LastAbs, clientFinal.Final.LastAbs), 1, "peers apart on the last wagon (m)");
    }

    // ---- runs inside the games
    public static class InGame
    {
        [Step] public static IEnumerator Watch(Step<Watched> step, string[] ids, float seconds) { ... step.Result = ...; }
        [Step] public static IEnumerator Stall(Step<Stalled> step, float seconds) { ... }
    }
}
```

- **What runs where is a compile error.** In-game code is in `[Step]` methods, in a nested `InGame`
  class or the shared library; everything else is the orchestrator. `DevTools/DvmpScenariosAnalyzer`
  fails the build in a scenario class's own methods on DVMP0101, a game type or member
  (UnityEngine, Assembly-CSharp, DV.*, Multiplayer, the agent, the probe library `Dvmp.Lib`), and on
  DVMP0102, a `[Step]` called directly. Inside `() => Step(...)` only the step itself is exempt: its
  arguments are evaluated in the exe too. `dvmp scenario check` checks every step's signature.
- **Step calls are typed by expression trees.** `pair.Host.Run<Stalled>(s => InGame.Stall(s, 30))`
  never calls `Stall` in the exe: `Run` reads the `MethodCallExpression`, evaluates each argument
  there, and sends the declaring type, the method name, the parameter types (for overloads) and the
  arguments as JSON (`Framework/StepCall.cs`). Argument and result types are checked by the
  compiler: `Run<Wrong>(s => InGame.Stall(s, 30))` doesn't compile. The body must be one call to a
  static `[Step]` method; anything else throws at the call site with the reason.
- **Steps** are coroutines that take a `Step<T>` first (`IEnumerator Watch(Step<Watched> step, ...)`,
  result in `step.Result`, called as `s => Watch(s, ...)`), coroutines with no result
  (`IEnumerator Board(string car)`, called as `() => Board(id)`), or plain methods returning `T`.
  `step.Note(...)` adds lines the run prints under the step's line.
- **Step contracts are plain data.** Arguments and results are primitives, strings, enums, arrays and
  classes of the scenarios project made of those (`Train`, `Watched`, `Abs`), never `TrainCar` or
  `Vector3`: the agent's JSON converters write Unity types but can't read them back
  (`DvmpAgent/Json.cs`), and a game object means nothing in another process. A call with another
  type throws before anything is sent, and `dvmp scenario check` finds it without games.
- **Parallel steps.** `Start` returns a running step to await later; `pair.Both` runs a step on host
  and client at once and gives both results, as `.Host`/`.Client` or `foreach (var (peer, value) in ...)`.
  A started step still running when the scenario ends is cancelled (`/v1/cancel`).

### Checks

- `Expect.*` records a failure and continues, so a run reports every broken check (as
  `host-stall`'s `bad[]` list did). `Require.*` stops the run. Both print the measured value against
  the limit: `check   FAIL client: lowest brake pipe pressure (bar): 2.8, expected at least 3.5`.
- `Report.Note` for what's reported but doesn't fail the run (`host-stall`'s snaps: the user's rule
  is that it fails only on breakage).
- `pair.Client.LogCursor()` / `LogsSince(cursor, "error")` read a game's log since a point, for
  checks on what it logged (`cab-reload`'s hook errors).

### Shared library

`Library/Library.cs`, on top of the linked probe libraries:

| Step or helper | Does |
|---|---|
| `Cars.SpawnTrain(loco, wagon, n, clear)` | host: a coupled, charged train near the client, every handbrake off, on a stretch with that much clear track (following the switches) |
| `Cars.SpawnHeldLoco(livery)` | host: a loco (and tender) braked and watched until it stops creeping (#159) |
| `Cars.Push`, `Cars.WaitForSpeed`, `Cars.WaitUntilStopped`, `Cars.Delete`, `Cars.Position` | |
| `Players.Board(car)` | the one boarding path: the floor's reparent target, `car.interior` (#171) |
| `Players.TeleportToGround(x, z)` | |
| `Items.Spawn(prefab, near)`, `Pickup`, `Drop`, `Throw(Dir)`, `Held`, `Destroy`, `Existing`, `Position` | test items through DV's real grabber (`Hands`) |
| `Net.SetReceiveLag(ms)`, `peer.Lag(ms)` | Steam's fake receive lag; `Lag` is a scope, undone on failure too |
| `Peers.Distance`, `Peers.Horizontal` | on `Abs` positions (`Dev.Abs`), so each game's floating origin cancels out |
| `Wait.Until(condition, seconds, what)` | in a step: yields until it holds, throws naming `what` |
| `Until.Value(read, done, seconds, what)`, `Until.True` | in the orchestrator: polls a step quietly, one line for the wait |
| `peer.Quit()`, `peer.Kill()`, `pair.Rejoin()`, `pair.Restart(save)` | games leaving and coming back (`[Needs(Need.RestartsGames)]`) |
| `peer.LogCursor()`, `peer.LogsSince(cursor, level)` | a game's log since a point |

Area-specific steps go in that area's file under `Library/` (`Library/Items.cs`, a trains file...),
so ports in different areas never edit the same file.

### Isolation

- **The reset, before every run** (`ScenarioRun.ResetAsync`, `Library/World.cs`): both players to
  where every run starts, empty-handed, test mugs dropped out of inventories and destroyed, the
  wallet topped up to `$MONEY` (default 100000). In process, so gate.sh skips its `teleport_home`
  for C# runs (six `dvmp` calls, 14 to 40 s a run). `--no-reset` skips it.
- `pair.Cleanup(...)` registers cleanup that runs after the scenario, pass or fail, last first.
- **The leak check, after every run** (`ScenarioRun.LeakCheckAsync`). The library's spawn steps
  record every car and item they make; after the cleanups the framework removes any left, and each
  must then be gone from both games. No cab control lease may still be held on either game. A
  failure here is a bug the next run would trip over: a car that can't be deleted, a lease never
  given back. Only the run's own cars count: the world spawns cars while a run goes on
  (`StationLocoSpawner` puts locos at a station a player rides past).

### Restarts inside a scenario

`save-reload`, `host-player-persist` and `rejoin` restart games as the test. `pair.Restart(save)`
quits the pair and starts it again on that save, as `dvmp down` and `dvmp up --save` for the run's
session, keeping the kind of host: a dedicated pair comes back dedicated (bash `save-reload` came
back as a player host). Scenarios that use it declare `[Needs(Need.RestartsGames)]`. `scenario run`
itself never starts a pair: gate.sh owns pairs, as it does for `run.sh`.

### Loading into the games

The agent's `ScenarioSteps` (`/v1/load`, `/v1/invoke`, `/v1/cancel`; local only, never remote):

- **Once per launch.** Before a game's first step the exe asks `/v1/load` with the assembly's
  SHA-256 only; a 404 means upload it. After a restart the games are found again and the assembly
  goes up again.
- **A unique name per build.** The exe renames its own assembly `DvmpScenarios_<id>` (Cecil) before
  uploading, as probes get a unique name per compile, so a game that stays up across rebuilds holds
  one copy of each build, never two assemblies with one name.
- **Stale installs are a harness error.** `/v1/load` gets the MVID of the `Multiplayer.dll` the
  scenarios were built against (the copy beside the exe) and refuses with 409 if the game runs
  another: "rebuild, or `dvmp up --fresh` to deploy the build". Without this, a stale install fails
  halfway through with `MissingMethodException`, counted as a FAIL.
- **Invoke** finds the method by type, name and parameter types (named without assembly names, so
  the renamed copy matches), reads the arguments as JSON, and runs it like a probe: on the main
  thread, nested coroutines flattened, a timeout. The agent knows no scenario types: a coroutine's
  result is the `Result` of its first parameter when that parameter's type is a `Step<>`.
- A blocking step (a stall) blocks `/v1/status` on that game too; the exe doesn't poll status
  during a run.

### Runner

A small hand-written runner (`Framework/Program.cs`, `ScenarioRun.cs`), not NUnit. NUnitLite was
the plan: it would bring `Assert.Multiple`, `Assume`, `TearDown`, timeouts and attachments. Once
the output per step, the exit codes, the options from `--arg` and the artifacts were written, NUnit
was left providing only the soft asserts, which are 60 lines here (`Checks.cs`), behind a second
output format to map back.

- Exit codes: 0 pass, 1 fail, 2 usage (an unknown option is an error: `FOO=1` is a typo, not a
  no-op), 3 harness error (no pair, a stale install, a pair that isn't what `[Needs]` asks).
- `[Budget(seconds)]` (default 600): a run that takes longer fails.
- On a failure the run saves both games' screenshots and log lines since the run started into
  `$LOG_DIR/<scenario>-<time>-<session>/`.

### Output

```
== host-stall  session scen-d  STALL=30 SPEED=10 WAGONS=4 RIDE=1 LAG=150 HOLD=15
   dedicated host; client #38868, host #72880
[  45.6s] host    Cars.SpawnTrain                    ok       44.3s
[  45.6s] note    L-032,CBX392,CBX500,CBX829,CBX665 on Road 35a, 250 m clear: brake pipe charged to 4.9 bar, ...
[  48.6s] host    InGame.Watch                       started
[  79.6s] host    InGame.Stall                       ok       30.5s
...
[ 204.8s] check   ok   client: lowest brake pipe pressure (bar): 5.18, expected at least 3.5
[ 204.8s] note    after the stall: 31.7s jump 5.63 m: derailed False, kinematic False, v 0.0, ...
[ 204.8s] check   ok   peers apart on the last wagon (m): 0.022, expected at most 1
PASS  host-stall  207.1s
```

### Commands

```
dvmp scenario list                     names, descriptions, options with defaults, [Needs]
dvmp scenario check                    without games: unique names, options, every step's signature
dvmp scenario run NAME [--arg K=V]...  one run against the session's pair [--log-dir DIR]
```

Options are the public fields of the scenario's `Options`: `--arg WAGON_BRAKE=0` sets `WagonBrake`.

### gate.sh

Unchanged syntax: the run list, `RUNS`, `PAIRS`, `@dedicated`, `REPEAT` and the summary.

- `run_scenario` runs `run.sh` when the scenario has one, else `dvmp scenario run <scenario>` with
  the run's variables as `--arg`s; exit code 3 is an ERROR.
- A queue with a C# run builds `DvmpScenarios` and runs `dvmp scenario check` before the first run,
  so a scenario that doesn't compile stops the gate at the start.
- A RUNS line can name a C# scenario (`is_cs` reads the `[Scenario("...")]` names from source).

## Migration

- Port by flake rate, keeping each check's meaning. Done: `host-stall` (from #202), `cab-reload`,
  `save-reload`. `train-rate` after #174 merges.
- A ported scenario's `run.sh` and probes are deleted in the same PR; gate runs whichever exists.
- No new `run.sh`. A flaky one is ported, not patched.
- Each port runs in game on both a player host and a dedicated host before its PR.

## Porting a scenario

1. Read the bash scenario: its header (what it checks, its variables), `run.sh`, its probes, and the
   probes it borrows from other scenarios.
2. One file, `DevTools/DvmpScenarios/Scenarios/<Name>.cs`: `[Scenario("<name>", "...")]` with the
   bash name (gate's run list and `ONLY` keep working), the header as the class's summary, the
   variables as `Options` fields with the same defaults, the probes as `[Step]`s in a nested
   `InGame` class with typed results, and `run.sh`'s sequence as `Run`.
3. Keep every check's meaning: the same threshold, the same comparison, the same peer. A check that
   looks wrong is reported in the PR, not changed in the port. Each bash `FAIL` becomes an `Expect`
   (or a `Require` where the rest depends on it); each echoed number a `Report.Note`.
4. Use the library: spawning, boarding, items, lag, the reset. A helper two scenarios need goes in
   the area's library file. A bash wait for a condition becomes `Wait.Until` or `Until.Value`; a wait
   that is the condition under test (a stall, a gap) stays, with a comment saying so.
5. Delete the bash scenario and its probes (and any gate run that only the removed mode had), and
   update its row in docs/testing.md with `(C#)`.
6. `dotnet build DevTools/DvmpScenarios/DvmpScenarios.csproj -c Debug` and `dvmp scenario check`
   without games; then in game on a player host and a dedicated host, through gate.sh
   (`ONLY="<runs>"`), before the PR. A scenario that can't run on one kind declares `[Needs(...)]`.

## Next

- **A fresh copy of the session's baseline save** for scenarios that write saves.
- The waits the first ports kept from bash (`Task.Delay(4000)` after boarding, the 3 s for a stale
  hook pass), each commented, could become `Until`s.

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
  handles, Steam's IPC, sockets), and Windows has no process checkpoint. The in-process reset is the
  cheap form of the same isolation; faster launches are #209.
