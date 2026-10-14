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
  **2019.4.40f1** and run **Multiplayer > Build Asset Bundle and Scripts**, which writes them to
  `build/`. Redo this only when something under `MultiplayerAssets` changes.

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
- runs `post-build.ps1`, which writes the version from `<InformationalVersion>` in
  `Multiplayer/Multiplayer.csproj` into `info.json` and copies `build/` into the game's
  `Mods/Multiplayer` folder (or the one installed folder whose `info.json` has Id `Multiplayer`).

Release additionally signs the DLLs when `Cert-Thumb` is set and zips `build/` to
`releases/Multiplayer <version>.zip`. Debug builds turn the debug system on by default (see
[observability.md](observability.md)).

The dvmp harness is built separately. The agent's Debug build deploys it to
`Mods/MultiplayerDevTools`:

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

Run the gate against a running pair for any change touching items, joins, ticks or trains. It
exits non-zero if any scenario failed.

```bash
bash DevTools/Scenarios/gate.sh
```

`ONLY="rejoin recall"` runs a subset; `LOG_DIR` sets where per-run logs go. The scenario list is
in [testing.md](testing.md#regression-gate).

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

## Releases

- Bump `<InformationalVersion>` in `Multiplayer/Multiplayer.csproj`; the next build writes it into
  `info.json`, which ships with the mod. Commit both.
- Add an entry for the version to `CHANGELOG.md` at the repo root.
- Build Release and run the gate. The zip is in `releases/`.
