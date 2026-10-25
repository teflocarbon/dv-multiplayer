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
| [item-overhaul-checklist.md](item-overhaul-checklist.md) | Every requirement of the item overhaul, one row each, ticked as it's built. Every item PR lists the IDs it closes and ticks them (merged code plus a proving scenario on both host kinds) |

## How each system syncs

| Doc | What it covers |
|---|---|
| [world-sync.md](world-sync.md) | Time, weather and sleep, difficulty, money, licences and debt, shops, service stations, junctions, explosions, hazmat, the sandbox cargo loader |
| [design/track-graph-check.md](design/track-graph-check.md) | The join check that host and client numbered their tracks, junctions and turntables the same way |
| [jobs.md](jobs.md) | Job generation, taking jobs, booklets and reports, validation and hand-in, save and rejoin |
| [design/trains.md](design/trains.md) | Train sync: canonical topology (couplers, hoses, cocks, MU cables), control commits, motion and corrections |
| [design/items/README.md](design/items/README.md) | **Start here for items.** The item system as the [refactor](design/item-refactor.md) built it (R0-R8): the guarantee, the three names (ledger, live state, saving and loading), the homes, and one code folder and doc per subsystem. The old item docs are under [Legacy](#legacy) |
| [design/items/wire.md](design/items/wire.md) | The item wire (R2): naming items (NetId, one handle space, never a GUID) and players (a holder by number; each player's relation to an item, never its owner; the claim only to its claimant), the per-recipient update, one request and one result with a remembered request id, one-step recall, one list, the refusal codes |
| [design/items/ledger.md](design/items/ledger.md) | The ledger (R3): every owned item's owner and exact home, commits first, births and lineage, cold and live homes (D12), the one-home check, the re-key merge, and the ownership questions still open |
| [design/items/live-state.md](design/items/live-state.md) | Live state (R3): the record of every item now, the state machine and the order of a transition, revisions, data-only records, and what live state owns beside the ledger |
| [design/items/inventory.md](design/items/inventory.md) | Inventories (R3): an exact slot for every item, slot moves and swaps as requests, the optimistic client, a leaver's inventory going cold and back, data only on the server for a remote player's inventory |
| [design/items/essentials.md](design/items/essentials.md) | Essentials and starting items (R3): the issuer, claim slots from the ledger, recall, the silhouette |
| [design/items/owned-list.md](design/items/owned-list.md) | The owned-items list and retrieval (R3): rows from the ledger, the place text, what retrieval does from each home |
| [design/items/access.md](design/items/access.md) | `ItemAccess` (R3): the one place that decides what a player may do with an item, each action's rule, and where the server asks |
| [design/items/change-causes.md](design/items/change-causes.md) | Why an item moved or went away (R3): the causes, how the server names them, and what goes on the wire |
| [design/items/save-load.md](design/items/save-load.md) | Saving and loading (R4): one key, `Multiplayer.Items`, each fact saved once and each row standing alone; the load before any request and its plan; rows that won't load (`Unloadable`, `SaveRecovery`); old saves start items fresh; vanilla's lists as a write-only mirror |
| [design/items/containers.md](design/items/containers.md) | Containers (R5): a stored item's home is its container's slot and a container's id its shell's item id; who keeps a box, from the ledger; deposit, withdrawal and moves as ledger changes that roll back |
| [design/items/lost-and-found.md](design/items/lost-and-found.md) | Lost and Found (R6): owned by the ledger, every way in, a deleted car's items (owned ones to Lost and Found, unowned ones beside the track) |
| [design/items/vanilla-storage.md](design/items/vanilla-storage.md) | Vanilla's storage, respawn and bins (R6): vanilla's Lost and Found list refused, respawn only for unowned scene items and bins, binning your own or nobody's (`Discarded`), the load's duplicate pass off |
| [design/items/projection.md](design/items/projection.md) | Projection (R7): interest cells and what each player is sent, deferred projections, retirement and its cause, acknowledgements (Projected, Retired, Failed) and retries |
| [design/items/world-items.md](design/items/world-items.md) | World items (R7): scene-authored items and the authored catalogue (collisions refused per key), shelves, office stock |
| [design/items/physics.md](design/items/physics.md) | Physics (R7): leases and who simulates, samples and settlement, loose items on trains, the fall rescue, the terrain guard, pickup reach |
| [design/items/train-wake.md](design/items/train-wake.md) | Train wake (R7): settled train items glued to their car, impact witnesses, car motion in the car's frame |
| [design/items/behaviours.md](design/items/behaviours.md) | Item behaviours (R8): the values each item type syncs (flashlight, lantern, lighter, shovel, page book, money) and who sends them, working an item you don't hold, magazines, the grabber's recovery |
| [design/shops.md](design/shops.md) | Shops as a server transaction: shared registers and shelves, the cash rules and the scanner are built; prediction, delivery to the owner, receipts, station fixtures and per-player stock are planned |

## Legacy

Docs that describe a system before it was rebuilt, kept as history of how it behaved, each with a banner naming its replacement. The old item docs moved here on 2026-10-24, before their replacements: each is replaced under `design/items/` by the refactor PR named.

| Doc | What it covers |
|---|---|
| [legacy/items/item-lifecycle.md](legacy/items/item-lifecycle.md) | Legacy, replaced in R3 by [live-state.md](design/items/live-state.md) and [essentials.md](design/items/essentials.md): An item's whole life in short, the record's facts and placements, the state machine, cleanup precedence and the shared rules, with a link to each item system's doc |
| [legacy/items/owned-items.md](legacy/items/owned-items.md) | Legacy, replaced in R3 by [ledger.md](design/items/ledger.md), [inventory.md](design/items/inventory.md), [essentials.md](design/items/essentials.md), [owned-list.md](design/items/owned-list.md) and [access.md](design/items/access.md), and in R6 by [lost-and-found.md](design/items/lost-and-found.md): Ownership and the owned-items ledger: one id for life, starting items, joining and leaving, essentials' claim slots and recall, the owned-items list and retrieval, lineage, `ItemAccess` and fixtures, and the decisions with their dates |
| [legacy/items/lost-and-found.md](legacy/items/lost-and-found.md) | Legacy, replaced in R6 by [lost-and-found.md](design/items/lost-and-found.md): Lost and Found: what is collected and when, entries, retrieval, saving and loading |
| [legacy/items/container-storage.md](legacy/items/container-storage.md) | Legacy, replaced in R5 by [containers.md](design/items/containers.md): Cold containers: contents as server records, browse, deposit, withdraw, saving |
| [legacy/items/item-world-sync.md](legacy/items/item-world-sync.md) | Legacy, replaced in R7 by [projection.md](design/items/projection.md) and [world-items.md](design/items/world-items.md): Scene-authored items and the catalogue, cells and interest, projection to clients, convergence, anchored placements, the VR belt and office stock |
| [legacy/items/item-spatial-physics.md](legacy/items/item-spatial-physics.md) | Legacy, replaced in R7 by [physics.md](design/items/physics.md): Thrown and dropped items: simulation leases, sampling, settlement, the terrain guard and fall rescue |
| [legacy/items/train-item-wake.md](legacy/items/train-item-wake.md) | Legacy, replaced in R7 by [train-wake.md](design/items/train-wake.md): Items resting in moving trains, and the server waking them |
| [legacy/items/item-persistence.md](legacy/items/item-persistence.md) | Legacy, replaced in R4 by [save-load.md](design/items/save-load.md): Which store saves each item, `WorldItemsV1`, and how a load rebuilds them |
| [legacy/items/item-change-causes.md](legacy/items/item-change-causes.md) | Legacy, replaced in R3 by [change-causes.md](design/items/change-causes.md): Why an item moved or went away: the recorded causes, on the server and clients |
| [legacy/items/item-ownership-open-questions.md](legacy/items/item-ownership-open-questions.md) | Legacy, replaced in R3 by [ledger.md](design/items/ledger.md#open): The item-ownership questions still open |
| [legacy/items/item-sync-review.md](legacy/items/item-sync-review.md) | Historical: the July 2026 review of item sync, with each finding's status then |

## Planned

| Doc | What it covers |
|---|---|
| [design/item-refactor.md](design/item-refactor.md) | The item refactor: every owned item in exactly one saved home, the ledger deciding owner and home, live state and saving and loading following it, identity instead of session numbers, the slot for every inventory item, one save key, the redesigned item packets, every vanilla path, every known way an item is lost, one folder per subsystem with the rename map, and the PR order with the checklist rows each closes |
| [design/item-state.md](design/item-state.md) | Generic item state (designed 2026-10-21, not started; its place in the refactor not yet decided): vanilla save data in every `Create`, item controls mirrored, and a short family of save keys per item type for the rest; replaces the per-item tracked-value patches |
| [design/server-player-split.md](design/server-player-split.md) | The target model: one server that owns the world, and players who are all its clients, whether the server runs in a player's game or on a dedicated host. Per-player saves, the one-time conversion of old careers, and world coverage for every player |
| [design/multi-anchor-streaming.md](design/multi-anchor-streaming.md) | Research: loading the world (scenery tiles, terrain, origin) around several far-apart players, what lives in which streamed scene, the exact patch points, costs and the measurements still needed |
| [design/code-layout.md](design/code-layout.md) | Splitting `Multiplayer` into Shared, Server and Client (with `Multiplayer.Core` for the rules): the roles, where each folder goes, the boundary test, and the steps from namespaces to assemblies |
| [design/host-local-session.md](design/host-local-session.md) | Removing the host's loopback client in stages while keeping one process: the host player's requests go straight to the server's handlers, then a local player session replaces the client; what moves, the dedicated host without a service identity, and the scenarios for each stage |
| [design/gadgets.md](design/gadgets.md) | Syncing installable gadgets: the server owns installs, removals, mounts, holes, wiring, soldering and gadget state; requests, the per-anchor change log, `Installed` item placement, persistence in the vanilla save, dedicated hosts, late joiners, rollback, and the PR plan with its scenarios |
| [design/dedicated-host.md](design/dedicated-host.md) | The full game as a host with no local player (`-mp-dedicated`, partly built): the investigation, the service identity, the world following a player, running unattended, and what's left |

## Tooling

| Doc | What it covers |
|---|---|
| [design/scenarios.md](design/scenarios.md) | Regression scenarios as C#: one file per scenario, compiled against the mod, its steps run in the games through the dev agent and its checks in a runner outside them; replaces the bash scenarios one at a time |
| [design/parallel-test-sessions.md](design/parallel-test-sessions.md) | Planned: several dvmp host+client pairs at once, to split the gate and repeat scenarios |
| [research/harness-time-budget.md](research/harness-time-budget.md) | Where dvmp spends wall-clock time: each launch's phases, what dominates a world load, loading against scenario time in a gate batch, and what a faster launch, an in-world reset or a hot reload would save |

Each design doc starts with a status line and has a dated "Implementation status" section that says
what is built, what is partial and what is not done. The item docs are written as built instead, each
with "Not built" and "Known issues" sections. Update them when you change how a system syncs.

## Vanilla game research

[research/](research/) holds notes on how the unmodded game behaves: interaction, inventory and
storage, item save and load, Lost and Found, PageBook, job validation and reports, the career
manager, gadgets, train sync and test fixtures, VR items and the dev console. They were written from
the decompiled game and describe Derail Valley itself, not the mod.
[research/mapify-test-map.md](research/mapify-test-map.md) is the exception: how Mapify Continued
replaces the world, where it collides with the mod, and whether a test map is worth it.
[research/item-bug-review.md](research/item-bug-review.md) is a bug catalogue, not vanilla research:
every known way a player loses an item, with the `item-bug-*` scenario that proves it and the PR
that fixes it.
[research/item-terrain-tunnelling.md](research/item-terrain-tunnelling.md) was measured in game
rather than read from the code: small items such as the mug fall straight through DV's terrain in
plain PhysX, and vanilla doesn't catch them. The mod's terrain guard does.
[research/cold-inventory-sizing.md](research/cold-inventory-sizing.md) sizes decision D14 (inventories
with no objects, the client's included) against vanilla's inventory and the mod's code, with a PR plan.

## Generated

`index.md`, `mpapi/` and `wiki-output/` are the MultiplayerAPI reference generated by the
MultiplayerAPI build. They are not tracked in git.
