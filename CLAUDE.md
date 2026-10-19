# dv-multiplayer

Multiplayer mod for Derail Valley (Unity 2019.4, Mono, Harmony via Unity Mod Manager).

## Testing in the real game

You can run and inspect the real game yourself: real Derail Valley instances with a dev agent, driven by
the `dvmp` MCP tools or the same commands on the CLI (`DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe <cmd>`;
build it from your worktree if the MCP server is older than your branch). Use them instead of asking the
user to test by hand. Details: `DevTools/README.md`. Ask the user before launching games, and before more
than one pair.

- **Bring games up:** `dvmp_up` starts or reuses a joined host + client. Pairs run side by side in named
  sessions (`session`: p1, d1, ...; CLI `--session` or `DVMP_SESSION`), each with its own user data.
  `dedicated: true` makes the host a dedicated server (no player); `hostOnly` starts just a host. Every
  change must work on both a player-hosted and a dedicated server, so test both.
- **Look and change things inside the games:** `dvmp_eval` compiles C# and runs it on Unity's main thread
  in the host, the client or both (`target`). All game and mod members are public to it; compare peers
  with `Dev.Abs(...)`; a `class` probe can Harmony-patch a method to watch a code path live.
  `dvmp_logs` for exceptions, `dvmp_screenshot`, `dvmp_trace` (what code moved an item), `dvmp_desync`
  (persistent host/client differences), `dvmp_perf` (frame times, packets per type). The mod's debug
  event stream is `dvmp-debug-<launch id>.jsonl` beside each game log.
- **Someone else's game:** `dvmp_remote` pairs with a player's game over Tailscale; after that every
  tool takes `target: "remote:NAME"`.
- **Regression scenarios:** `DevTools/Scenarios/<name>/run.sh` (docs/testing.md lists them), run by
  `DevTools/Scenarios/gate.sh` with `ONLY="..."`, or `RUNS=` and `PAIRS="p:2 d:1"` for several pairs
  and dedicated hosts at once. Run only the scenarios for what you changed, not the whole suite.
  A probe compiles before dvmp looks for a game, so `dvmp eval --session nogame -f probe.cs` checks it
  compiles without launching anything.

**Builds and the game install.** A Debug build of `Multiplayer` installs itself into the game, and every
pair runs from that one install: after building, `dvmp_up` with `fresh: true` loads it. To only compile,
set the environment variable `ConfigurationName=NoDeploy` (not `-p:`). Building `DevTools/DvmpAgent`
installs the agent regardless. Don't build Release (see Versions).

**Test in the test install, not the user's game.** `DevTools/make-test-install.ps1` makes a copy of the
game at `%LOCALAPPDATA%\dvmp\test-install` (junctions, so it's small). Set `DVMP_DV_DIR` to it and build
with `-p:DvInstallDir=<it>`, so test builds and test games never touch the install the user plays from
(DevTools/README.md, "Test install"). Shut down only the sessions you started (`dvmp down --session
NAME`): the user may have their own games open.

## Unit tests

```bash
dotnet test Multiplayer.Core.Tests
dotnet test Multiplayer.Protocol.Tests -c Release   # references the built Multiplayer.dll of the same configuration
```

## Docs

`docs/README.md` indexes the docs. Start with `docs/architecture.md`; the per-system designs are in
`docs/design/`, and notes on vanilla game behaviour are in `docs/research/`. Update the relevant
doc when you change how a system syncs.

## Workflow

`beta` is protected: work on a branch off `beta` and merge through a pull request (merge commits
only, signed commits). See `docs/development.md`.

**Work in your own worktree, never in the main checkout.** The main checkout (the repository folder
itself, not `.claude/worktrees/...`) is the user's: don't switch its branch, commit, stash, pull or
build in it unless the user asks you to. Do your work in your session's worktree (or create one with
`git worktree add`), and leave other sessions' worktrees alone.

**Name your branch for what it does.** A session's worktree branch starts with a generated name
(`claude/<adjective>-<name>-<hex>`); before your first commit, rename it with
`git branch -m <area>/<what>`, e.g. `trains/cab-hook-timeout`, `items/fall-rescue`,
`docs/three-stages`, `fix/derail-judder`. Base it on the branch the work targets (the integration
branch you were told about, else `beta`).

## Versions

Calendar versions, `YYYY.M.D.N`: a Release build stamps the version itself and writes it into
`info.json`, the only place it's kept. Don't hand-edit a version anywhere. A Release build also
installs itself into the game, replacing the version the user plays: don't build Release just to
test something. See "Versions" in `docs/development.md`.

## Decompiled game source

The game assemblies are not obfuscated; decompile with ILSpy (`ilspycmd -p`) when you need vanilla behaviour.
