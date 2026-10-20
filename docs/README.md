# Documentation

Docs for people working on the mod. Players want the [README](../README.md) and the
[changelog](../CHANGELOG.md).

Start with [architecture.md](architecture.md). It explains how the mod is put together and how the
host-authority model works end to end, then points at the rest.

## Overview

| Doc | What it covers |
|---|---|
| [architecture.md](architecture.md) | Projects, runtime structure, host authority, join flow, tick rates, save and rejoin |
| [development.md](development.md) | Building, tests, testing in game with dvmp, the regression gate, branches and pull requests |
| [testing.md](testing.md) | Unit and protocol tests, the protocol fingerprint, and every dvmp scenario in the gate |
| [observability.md](observability.md) | In-game debug overlay, structured events, traces and the debug HTTP API |
| [playtest-tracker.md](playtest-tracker.md) | The working list from playtests: what broke, what's fixed, what's open |

## How each system syncs

| Doc | What it covers |
|---|---|
| [world-sync.md](world-sync.md) | Time, weather and sleep, difficulty, money, licences and debt, shops, service stations, junctions, explosions, hazmat, the sandbox cargo loader |
| [jobs.md](jobs.md) | Job generation, taking jobs, booklets and reports, validation and hand-in, save and rejoin |
| [design/trains.md](design/trains.md) | Train sync: canonical topology (couplers, hoses, cocks, MU cables), control commits, motion and corrections |
| [design/train-item-wake.md](design/train-item-wake.md) | Items inside moving trains: settlement and the host-side wake simulation |
| [design/item-lifecycle.md](design/item-lifecycle.md) | The canonical item lifecycle: identity, ownership, placement, Lost and Found, persistence |
| [design/item-world-sync.md](design/item-world-sync.md) | Scene-authored items, cell-based interest and world item streaming |
| [design/item-spatial-physics.md](design/item-spatial-physics.md) | Thrown and dropped items: simulation leases, sampling and settlement |
| [design/container-storage.md](design/container-storage.md) | Cold container storage on the host |
| [design/item-ownership-open-questions.md](design/item-ownership-open-questions.md) | Ownership rules that are decided, and the game-design questions that are still open |
| [design/item-sync-review.md](design/item-sync-review.md) | The July review of item sync, with the status of each finding |

## Planned

| Doc | What it covers |
|---|---|
| [design/server-player-split.md](design/server-player-split.md) | The target model: one server that owns the world, and players who are all its clients, whether the server runs in a player's game or on a dedicated host. Per-player saves, the one-time conversion of old careers, and world coverage for every player |
| [design/multi-anchor-streaming.md](design/multi-anchor-streaming.md) | Research: loading the world (scenery tiles, terrain, origin) around several far-apart players, what lives in which streamed scene, the exact patch points, costs and the measurements still needed |
| [design/code-layout.md](design/code-layout.md) | Splitting `Multiplayer` into Shared, Server and Client (with `Multiplayer.Core` for the rules): the roles, where each folder goes, the boundary test, and the steps from namespaces to assemblies |
| [design/host-local-session.md](design/host-local-session.md) | Removing the host's loopback client in stages while keeping one process: the host player's requests go straight to the server's handlers, then a local player session replaces the client; what moves, the dedicated host without a service identity, and the scenarios for each stage |
| [design/dedicated-host.md](design/dedicated-host.md) | The full game as a host with no local player (`-mp-dedicated`, partly built): the investigation, the service identity, the world following a player, running unattended, and what's left |

## Tooling

| Doc | What it covers |
|---|---|
| [design/scenarios.md](design/scenarios.md) | Regression scenarios as C#: one file per scenario, compiled against the mod, its steps run in the games through the dev agent and its checks in a runner outside them; replaces the bash scenarios one at a time |
| [design/parallel-test-sessions.md](design/parallel-test-sessions.md) | Planned: several dvmp host+client pairs at once, to split the gate and repeat scenarios |
| [research/harness-time-budget.md](research/harness-time-budget.md) | Where dvmp spends wall-clock time: each launch's phases, what dominates a world load, loading against scenario time in a gate batch, and what a faster launch, an in-world reset or a hot reload would save |

Each design doc starts with a status line and has a dated "Implementation status" section that says
what is built, what is partial and what is not done. Update it when you change how a system syncs.

## Vanilla game research

[research/](research/) holds notes on how the unmodded game behaves: interaction, inventory and
storage, item save and load, Lost and Found, PageBook, job validation and reports, the career
manager, gadgets, train sync and test fixtures, VR items and the dev console. They were written from
the decompiled game and describe Derail Valley itself, not the mod.
[research/mapify-test-map.md](research/mapify-test-map.md) is the exception: how Mapify Continued
replaces the world, where it collides with the mod, and whether a test map is worth it.

## Generated

`index.md`, `mpapi/` and `wiki-output/` are the MultiplayerAPI reference generated by the
MultiplayerAPI build. They are not tracked in git.
