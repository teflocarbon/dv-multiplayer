# Development

How to build the mod, test it, and get a change into `beta`.

## Prerequisites

- Windows, Derail Valley with Unity Mod Manager installed, and the .NET SDK (8 or later; the mod
  itself targets .NET Framework 4.8).
- Git Bash (or another bash) for the dvmp scenarios.
- `Directory.Build.targets` at the repo root. Copy the example and set `DvInstallDir` (and
  `UnityInstallDir`) to your install paths, no trailing slashes. Leave `Cert-Thumb` empty unless
  you sign releases. The file is git-ignored.

```bash
cp Directory.Build.targets.EXAMPLE Directory.Build.targets
```

- `MultiplayerEditor.dll`, `UnityChan.dll` and `multiplayer.assetbundle` in `build/` (git-ignored).
  Either copy them from a released zip, or build them: open `MultiplayerAssets/` in Unity
  **2019.4.41f2** and run **Multiplayer > Build Asset Bundle and Scripts**, which writes them to
  `build/`. Redo this only when something under `MultiplayerAssets` changes, then update CI's
  copy (see [CI](#ci)).

## Building

```bash
dotnet build Multiplayer/Multiplayer.csproj -c Debug
```

```bash
dotnet build Multiplayer/Multiplayer.csproj -c Release
```

Every build of `Multiplayer`:

- regenerates `Multiplayer/protocol-manifest.json` with `Tools/ProtocolManifestGenerator` and
  rebuilds with it embedded. Its fingerprint includes the version and git commit, and a client
  refuses a host whose fingerprint differs, so both peers must run a build of the same commit;
- copies the mod, its dependencies, `info.json`, `locale.csv` and `LICENSE` into `build/`;
- runs `post-build.ps1`, which writes the build's version (see [Versions](#versions)) into
  `info.json` and copies `build/` into the game's `Mods/Multiplayer` folder (or the one installed
  folder whose `info.json` has Id `Multiplayer`). **A Release build installs itself into your game
  too**, replacing whatever version you play with.

Release additionally signs the DLLs when `Cert-Thumb` is set and zips `build/` to
`releases/Multiplayer <version>.zip`. Debug builds turn the debug system on by default (see
[observability.md](observability.md)).

The dvmp harness is built separately. The agent's Debug build deploys it to
`Mods/MultiplayerDevTools`. It's marked `MultiplayerCompatibility: Client` (no effect on gameplay),
so the mod check leaves it out: you can keep it enabled when you join or host players who don't
have it.

```bash
dotnet build DevTools/DvmpAgent/DvmpAgent.csproj -c Debug
```

```bash
dotnet build DevTools/Dvmp/Dvmp.csproj
```

## Unit tests

```bash
dotnet test Multiplayer.Core.Tests
```

`Multiplayer.Protocol.Tests` loads `Multiplayer/bin/<configuration>/net48/Multiplayer.dll`, so
build the mod in the same configuration first:

```bash
dotnet test Multiplayer.Protocol.Tests -c Release
```

See [testing.md](testing.md) for what each suite covers.

## Testing in the game

dvmp starts a joined host and client and lets you (or an agent) run C# in either game, take
screenshots and read logs. After changing `Multiplayer`, build Debug, then restart the pair so
both load the new DLL:

```bash
DevTools/Dvmp/bin/Debug/net8.0/dvmp.exe up --fresh
```

The same commands are MCP tools (`dvmp_up`, `dvmp_eval`, `dvmp_logs`, ...) registered in
`.mcp.json`. Full reference: [DevTools/README.md](../DevTools/README.md).

## Regression gate

For a change, run the scenarios for the area it touches, on three pairs; that takes minutes:

```bash
DVMP=DevTools/Dvmp/bin/cli/dvmp.exe PAIRS=3 ONLY="rejoin rejoin-crash" bash DevTools/Scenarios/gate.sh
```

Run the whole gate before a release, or after a change to shared plumbing (the tick,
serialization, the join sequence), in the background: it runs every scenario, back to back on
each pair, which is what catches one feature breaking another, and takes about half an hour. It
exits non-zero if any scenario failed.

`ONLY` takes scenario or run names; `LOG_DIR` sets where per-run logs go. A relaunched game
keeps its previous log as `host.prev.log` or `client.prev.log`, next to the current one. The
scenario list is in [testing.md](testing.md#regression-gate).

## CI

`.github/workflows/ci.yml` runs on every pull request and every push to `beta`:

- **Core tests** (Linux): `Multiplayer.Core.Tests`. Core doesn't reference the game, so this runs
  anywhere, including on pull requests from forks.
- **Build and protocol tests** (Windows): Debug builds of the mod and the dev tools, then
  `Multiplayer.Protocol.Tests`. GitHub doesn't give secrets to pull requests from forks, so those
  skip this job. It uploads the installable mod as an artifact on the run's page, named by its dev
  version, branch or PR, and commit (`Multiplayer 2026.10.16-dev.0842 beta 1a2b3c4d`): the same
  files as a release zip, installed the same way. Builds of `beta` are kept 90 days, pull
  requests' 14. Every player needs the same build: the fingerprint includes the commit, so two
  builds with the same version still won't join each other.

The regression gate isn't in CI: it needs real games. Run it locally before merging.

The build compiles against the game's own DLLs, kept in the private repo
`teflocarbon/dv-gamelibs` (laid out like a game install), and checked out with the
`GAMELIBS_TOKEN` secret: a fine-grained token with read access to that repo's contents. The
public repo must never upload them: no artifacts of `gamelibs/` or of any `bin/` folder, which hold
copies.

- **When the game updates:** copy the same DLLs from the new install into dv-gamelibs, commit, tag
  it `steam-<buildid>` (the `buildid` in `steamapps/appmanifest_588030.acf`), and bump
  `GAMELIBS_REF` in the workflow.
- **When `MultiplayerAssets` changes:** CI fails until the Unity files are rebuilt. Rebuild them,
  copy them to dv-gamelibs' `prebuilt/` (new commit and tag), and update `prebuilt.lock`: their
  SHA-256 hashes and the `MultiplayerAssets` tree they were built from
  (`git rev-parse HEAD:MultiplayerAssets`). CI checks both, so it never builds with Unity files
  that don't match the project.

## Workflow

`beta` is the main branch and is protected by a GitHub ruleset:

- changes only through a pull request (no approval required);
- merge commits only: no squash or rebase merges;
- every commit must be signed;
- no force pushes, no deleting the branch;
- admins may bypass only through pull requests.

Set up commit signing before you start (`git config commit.gpgsign true` with a GPG or SSH key
registered on GitHub), otherwise the PR can't be merged.

Put each feature or fix on its own branch off `beta` and open a PR into `beta`:

```bash
git switch -c fix/short-name beta
```

```bash
gh pr create --base beta
```

Don't use GitHub's stacked-PR flow to land a stack: it rewrites the commits, which drops their
signatures. Merge each PR into `beta` on its own instead.

## Versions

Versions are calendar dates: **`YYYY.M.D.N`**, where N counts the releases built that day
(`2026.10.15.1`, then `2026.10.15.2`). Players can tell at a glance whether they're on the same
build. Compatibility between builds isn't the version's job: the protocol fingerprint (the version
and git commit) decides that, and a client refuses a host whose fingerprint differs.

- **Nothing to bump by hand.** A Release build stamps today's date and the next build number, one
  past the zips already in `Releases/` for today, and `post-build.ps1` writes it into
  `info.json`. Deleting today's zips lets a number be reused.
- **Debug builds are dev builds,** `YYYY.M.D-dev.HHMM` from the build time (`2026.10.16-dev.2014`),
  so one can't be mistaken for a release. The version goes into the built copy of `info.json` (in
  `build/` and the installed mod), never the repo's, which keeps the last release's.
- **`info.json` is the only place the version is kept.** The manifest pass of a Release build
  reuses the first pass's version from `build/release-version.txt`, so the DLL, `info.json`, the
  fingerprint and the zip name agree.
- **`dvmp-build-number.txt`** is the build's time (`2026.10.15.2240`), for telling the harness's
  builds apart. A dev build's version is made from it.
- **There's no update check.** `info.json` has no `Repository` URL, so mod managers don't offer
  other forks' builds as updates. Hand out the zip.

The fork used upstream's `0.1.x` numbering up to 0.1.16.15.

## Releases

- Build Release. It stamps the version, writes `info.json`, puts the zip in `Releases/` and installs
  it into your game.
- Add an entry for that version to `CHANGELOG.md` at the repo root.
- Run the gate, then commit `info.json` and the changelog together.
- Send the zip. Every player needs the same build.
