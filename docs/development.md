# Development Guide

## Prerequisites

Required (all local development):
- Windows
- Git
- .NET SDK 10.x (pinned by `global.json`)

Required for native Fahrenheit builds:
- Visual Studio Build Tools (or Visual Studio) with:
  - `.NET desktop development`
  - `Desktop development with C++`
- `vcpkg integrate install`

## Quick Start

```bash
.\build.cmd setup
.\build.cmd doctor
.\build.cmd verify --configuration Debug --verbosity quiet
```

## Core Workflows

Discover workflows:

```bash
.\build.cmd help
.\build.cmd -h <workflow>
```

Bool flags use this style:
- `--flag` to enable
- `--no-flag` to disable

Verbosity defaults to `normal`.
- Explicit level: `--verbosity|-v quiet|minimal|normal|detailed|diagnostic`
- Escalation path: `quiet` -> `normal` -> `detailed` -> `diagnostic`
- Agent guidance: use `--verbosity quiet` for routine `.\build.cmd verify`, `.\build.cmd build`, and `.\build.cmd deploy` runs.
- Global config path: `--config-path` (shorthand: `-c`)
- Common shorthand: `-c <config-path>`, `-n` for `--dry-run`, `-h` for help.

Quality:

```bash
.\build.cmd format
.\build.cmd doctor [--full]
.\build.cmd lint [--configuration Debug|Release] [--config-path .\path\to\config.local.json]
.\build.cmd smoke [--configuration Debug|Release] [--config-path .\path\to\config.local.json]
.\build.cmd verify [--configuration Debug|Release] [--config-path .\path\to\config.local.json] [--repo owner/repo]
.\build.cmd clean [--dry-run]
.\build.cmd clean --purge --yes
```

Build and deploy:

```bash
.\build.cmd build [--configuration Debug|Release] [--config-path .\path\to\config.local.json] [--auto-deploy|--no-auto-deploy]
.\build.cmd deploy [--game-dir "C:\\Path\\To\\Game"] [--configuration Debug|Release] [--config-path .\path\to\config.local.json]
.\build.cmd auto-deploy [--game-dir "C:\\Path\\To\\Game"]
.\build.cmd start [--game-dir "C:\\Path\\To\\Game"] [--elevated|--no-elevated]
```

Research, Ghidra, game-data extraction and community sync are maintained in the sibling `ffx-knowledge-base` repository. See [Data Pipeline](data-pipeline.md).

## Parry Timing Determinism Checks

```bash
dotnet test tests/Parry.Tests/Parry.Tests.csproj -c Debug
```

The test suite includes simulation-time pacing checks for 1x/2x/4x-equivalent deltas and variable frame pacing.

## Build configuration

The root `NuGet.config` is the only package-source configuration. Package source mapping limits the local feeds to Fahrenheit packages, so public build dependencies restore before those feeds exist. Run `build.cmd setup` to create matching local Fahrenheit packages before evaluating the mod project.

`global.json` stays in the root because the .NET SDK discovers it there. The project metadata in `Fahrenheit.Mods.Parry.csproj` is the source for the SDK-generated deployment manifest; no handwritten root manifest is required.

Build helpers are C#: settings migration runs inside the build assembly, and `PrepareNativeDependencies.cs` runs as a .NET 10 file-based app from `build/Build.proj`. Batch files remain thin Windows entrypoints.

## Local Config

Local build and tooling settings are stored in `.workspace/config.local.json`.

Canonical schema keys are strict and case-sensitive when written by the build tooling:

- `Configuration` (`Debug` or `Release`)
- `GameDir` (game install directory containing `FFX.exe`)
- `AutoDeploy` (`true`/`false`/`null`; `null` prompts once in interactive mode)
- `DeployPreservePaths` (array of deploy-preserved paths under `<GameDir>\fahrenheit` and/or absolute paths)

Retired research settings are ignored and preserved on save. New configurations only need the build and deploy keys above.

`.\build.cmd build` can override deploy behavior per run:

- `--auto-deploy` forces deploy for this run even if `AutoDeploy` is `false`.
- `--no-auto-deploy` disables deploy for this run even if `AutoDeploy` is `true`.

Build and deploy workflows always operate on full payloads to reduce stale artifact issues.
Deploy behavior mirrors artifact state into the selected deploy target and preserves entries from `DeployPreservePaths`.

Use `--dry-run` with `build` or `deploy` to preview sync actions without writing files.

## Commit Workflows

```bash
.\build.cmd commit
.\build.cmd commit --type feat --scope ui --subject "add timeline row grouping"
.\build.cmd commit-check --message "feat: add timeline row grouping"
.\build.cmd commit-check --range origin/main..HEAD
```

## Release Workflows

```bash
.\build.cmd mod-pack --sdk local --tag v0.0.0-dev --out-dir .release --full true
.\build.cmd release-bump --bump patch --repo Exoridus/ffx-parry-mod
git push origin main
gh workflow run release.yml -f version=v1.2.3 -f sdk=local -f dry_run=true
```

`release-bump` updates project metadata/changelog, pins `fahrenheit.release.ref`, and creates the release commit. It does **not** create a tag.

The tag is created by the Release workflow after a green release build, so a
failed build never burns a version number: fix the problem, push again, and
dispatch the same version. Add `-f dry_run=true` to build and package without
publishing anything.

## CI/CD Summary

- CI verifies the Debug mod payload and tests, then builds tested Release mod-only/full development packages.
- Parry commit subjects are checked on pull requests.
- Release validates the requested version and tag, runs Debug verification and packages Release using the same C# entry points.
- Local SDK packages are restricted to dry-runs. Public prereleases require the official SDK and matching official host.
- Both ZIPs have SHA256 checksums and provenance. A successful publish job creates the tag after verification.

## References

- Workflow map: `docs/automation.md`
- Data pipeline: `docs/data-pipeline.md`
- Localization strategy: `docs/localization.md`
- Pointer/hook primer: `docs/pointers-hooks-guide.md`
- Local config schema: `docs/workspace-config.local.schema.json`
