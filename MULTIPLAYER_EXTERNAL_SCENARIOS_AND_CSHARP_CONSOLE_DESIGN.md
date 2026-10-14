# External Runtime Scenarios and C# Console

## Problem

Runtime scenarios currently live in `Multiplayer.dll`. Changing an assertion, wait,
fixture sequence, or cleanup step therefore requires rebuilding the mod and fully
restarting both Derail Valley processes. That makes exploratory debugging slow and
turns small test corrections into expensive multiplayer environment cycles.

The runtime harness must instead have two clearly separated layers:

- the in-game agent owns stable, game-specific primitives that must run on Unity's
  main thread;
- the external DebugClient owns scenario definitions, sequencing, assertions,
  retries, correlation, reporting, and cleanup policy.

The game is an execution target, not the home of the test suite.

## Target architecture

```text
Dashboard / CLI / Codex skill
            |
            v
DebugClient scenario catalogue (hot reloaded, one scenario per file)
            |
            +-- arrange / act / assert / always-cleanup orchestration
            +-- host/client target selection and variable capture
            +-- run journal, SSE progress, packet/event correlation
            |
            v
Stable in-game command API
            |
            +-- typed gameplay primitives (preferred for regression tests)
            +-- externally compiled C# execution (exploration and rapid probes)
            |
            v
Unity main thread in host or client process
```

## C# console bridge

`runtime.csharp` is a dashboard-owned command. The DebugClient compiles an
expression or method body using the .NET Framework C# compiler and references the
installed Derail Valley assemblies plus the exact current `Multiplayer.dll`.

The dashboard replaces the source with:

- a generated assembly;
- its SHA-256 digest;
- a generated entry type;
- non-compiler arguments supplied by the caller.

The game exposes only `runtime.csharp-execute`. It validates the payload size and
hash, loads the assembly, finds the exact public static entry method, and invokes it
on the runtime-test agent's Unity main thread. It supports an `IEnumerator` return
for frame-based probes and returns structured output through the normal run result.

The public `RuntimeCSharpConsoleContext` is the stable ABI. It supplies run identity,
arguments, output, a result slot, and a process-local state dictionary. This makes
short investigative sequences possible without rebuilding either application.

Example:

```powershell
DebugRemoteClient/Tools/dvmp-harness.ps1 console `
  -TargetRole host `
  -Code 'PlayerManager.PlayerTransform.position - WorldMover.currentMove'
```

For statements or coroutines, use `-CodeMode body` and set or return a result:

```csharp
context.Write(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
context.Result = PlayerManager.PlayerTransform.position - WorldMover.currentMove;
return null;
```

### Security and lifecycle constraints

- The bridge exists only in `DEBUG` builds and is reachable only through the
  token-authenticated loopback debug API.
- It is intentionally labelled unrestricted debug code. It is not a sandbox and
  must never ship in release builds.
- Assemblies loaded into the .NET Framework game AppDomain cannot be unloaded.
  Snippets therefore receive unique content-derived type names and should remain
  small. A very long exploratory session may eventually warrant a game restart.
- The compiler stays outside the game. No UnityExplorer code or `mcs` binary is
  copied into DVMP; UnityExplorer was used only as architectural research.

## External scenario files

Scenario definitions belong under `DebugRemoteClient/Scenarios`, with exactly one
scenario per file. The catalogue should rescan on capabilities requests and use a
file watcher to invalidate its cache. A malformed file is reported as a catalogue
diagnostic and must not hide valid scenarios.

The declarative format owns:

- metadata (`testId`, display name, category, timeout, required capabilities);
- ordered arrange/act/assert steps;
- a target selector for host, client, selected session, or all processes;
- calls to stable primitive command IDs;
- parameter interpolation from scenario inputs and captured prior results;
- explicit assertions over structured result paths;
- retries and projection barriers;
- an unconditional cleanup section with independent cleanup assertions.

For unusual orchestration, a DebugClient-side scenario plug-in may implement the
same external scenario interface. It must not require a `Multiplayer.dll` rebuild.

## Migration strategy

This is an incremental boundary change, not a rewrite of working game primitives.

1. Keep all existing primitive commands in the game.
2. Land the external C# console bridge and use it for rapid research/prototyping.
3. Land the hot-reloaded external scenario catalogue and generic step runner.
4. Move simple read-only and train lifecycle scenarios first.
5. Extract reusable external fixture transactions for inventory, cold containers,
   lost-and-found, and train consists.
6. Move each existing in-game scenario to one external file, retaining the old
   definition until the external version passes equivalence testing.
7. Remove `RuntimeTestScenarioRegistry` after the last migration.

At the end state, adding or editing a scenario requires only saving its external
file. Changing DebugClient orchestration requires at most the existing dashboard
handoff restart, which preserves the running host and client. Only adding a new
game primitive requires rebuilding and restarting the mod.

## Quota-efficient development loop

The default iteration loop becomes:

1. use `runtime.csharp` for one targeted observation;
2. promote proven operations into a typed primitive only when repeatability needs it;
3. edit a single external scenario file;
4. run only that scenario until stable;
5. run the affected suite once;
6. run the complete suite only at the deployment gate.

This removes repeated game boot cycles and avoids spending model/tool time on full
suite runs while the behavior under investigation is still changing.
