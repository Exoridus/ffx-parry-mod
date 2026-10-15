# Automation Overview

`build.cmd` is the project lifecycle entrypoint.

_Auto-generated from build workflow metadata. Do not edit manually; run `.\build.cmd docs-sync`._

Quick command discovery:
- `.\build.cmd help`
- `.\build.cmd -h <workflow>`
- Bool parameters support both `--flag` and `--no-flag`.
- Global verbosity: `--verbosity|-v quiet|minimal|normal|detailed|diagnostic` (default: `normal`).
- Recommended escalation: `quiet` -> `normal` -> `detailed` -> `diagnostic`.
- Global config path: `--config-path` (shorthand: `-c`).
- Common shorthand: `-c <config-path>`, `-n` (`--dry-run`).
- Agent guidance: use `--verbosity quiet` for routine `.\build.cmd verify`, `.\build.cmd build`, and `.\build.cmd deploy`.

## Core Workflows

- `.\build.cmd setup`
  - Primary local onboarding: prerequisites, config/GameDir repair, hooks, workspace setup, and auto-deploy setup.
  - Examples:
  - `.\build.cmd setup`

- `.\build.cmd clean`
  - Remove generated mod and host build artifacts. Add --purge --yes to also remove local release output.
  - Parameters:
  - --purge (optional, requires --yes).
  - --yes (required with --purge).
  - --dry-run (optional).
  - Examples:
  - `.\build.cmd clean`
  - `.\build.cmd clean --dry-run`
  - `.\build.cmd clean --purge --yes`

- `.\build.cmd auto-deploy`
  - Configure automatic post-build deployment.
  - Parameters:
  - --game-dir <path> (optional).
  - --refresh-game-dir (optional).
  - Examples:
  - `.\build.cmd auto-deploy`
  - `.\build.cmd auto-deploy --game-dir "C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY FFX&FFX-2 HD Remaster"`

- `.\build.cmd doctor`
  - Diagnose local toolchain and environment state.
  - Parameters:
  - --full (optional).
  - Examples:
  - `.\build.cmd doctor`
  - `.\build.cmd doctor --full`

- `.\build.cmd format`
  - Apply code formatting/style fixes using dotnet format.
  - Examples:
  - `.\build.cmd format`

- `.\build.cmd lint`
  - Run fast lint/compile checks for build, mod, and tests projects.
  - Parameters:
  - --configuration Debug|Release (optional).
  - --config-path <path-to-config.local.json> (optional, shorthand: -c).
  - Examples:
  - `.\build.cmd lint`

- `.\build.cmd smoke`
  - Run quick sanity checks against a full build.
  - Parameters:
  - --configuration Debug|Release (optional).
  - --config-path <path-to-config.local.json> (optional, shorthand: -c).
  - Examples:
  - `.\build.cmd smoke`

- `.\build.cmd verify`
  - Run local validation without deploy side effects.
  - Parameters:
  - --configuration Debug|Release (optional).
  - --config-path <path-to-config.local.json> (optional, shorthand: -c).
  - --repo owner/repo (optional).
  - Examples:
  - `.\build.cmd verify`

- `.\build.cmd build`
  - Build full Fahrenheit payload.
  - Parameters:
  - --configuration Debug|Release (optional).
  - --config-path <path-to-config.local.json> (optional, shorthand: -c).
  - --auto-deploy or --no-auto-deploy (optional).
  - --dry-run (optional).
  - Examples:
  - `.\build.cmd build`
  - `.\build.cmd build --configuration Release`

- `.\build.cmd deploy`
  - Deploy full build artifacts into GameDir.
  - Parameters:
  - --game-dir <path> (optional).
  - --refresh-game-dir (optional).
  - --configuration Debug|Release (optional).
  - --config-path <path-to-config.local.json> (optional, shorthand: -c).
  - --dry-run (optional).
  - Examples:
  - `.\build.cmd deploy`

- `.\build.cmd start`
  - Launch the game via deployed Fahrenheit stage0 loader.
  - Parameters:
  - --game-dir <path> (optional).
  - --refresh-game-dir (optional).
  - --elevated (optional).
  - Examples:
  - `.\build.cmd start`

## Distribution Workflows

- `.\build.cmd mod-build`
  - Build only the mod with isolated SDK feeds; never deploy.
  - Parameters:
  - --sdk local|official
  - --configuration Debug|Release
  - Examples:
  - `.\build.cmd mod-build`

- `.\build.cmd mod-verify`
  - Validate the SDK payload and run runtime/package tests.
  - Parameters:
  - --sdk local|official
  - --configuration Debug|Release
  - Examples:
  - `.\build.cmd mod-verify`

- `.\build.cmd mod-pack`
  - Create a mod ZIP rooted at the mod ID, SHA256 and provenance; --full also builds/packages the matching host.
  - Parameters:
  - --sdk local|official
  - --tag vX.Y.Z[-suffix]
  - --out-dir path
  - --full
  - Examples:
  - `.\build.cmd mod-pack --full`

## Release Workflows

- `.\build.cmd release-bump`
  - Bump version and create the release commit. The tag is created by the Release workflow after a green build.
  - Parameters:
  - --bump patch|minor|major (optional).
  - Examples:
  - `.\build.cmd release-bump`

- `.\build.cmd release-ready`
  - Run release preflight.
  - Parameters:
  - --configuration Debug|Release (optional).
  - --config-path <path-to-config.local.json> (optional, shorthand: -c).
  - --repo owner/repo (optional).
  - Examples:
  - `.\build.cmd release-ready`

- `.\build.cmd release-pack`
  - Build, test and package Release mod/full archives using the shared distribution workflow.
  - Parameters:
  - --tag vX.Y.Z (required).
  - --sdk local|official (optional).
  - --out-dir <path> (optional).
  - Examples:
  - `.\build.cmd release-pack --tag v0.0.1`

- `.\build.cmd release-check-assets`
  - Check the packaged ZIPs against MAX_FULL_ZIP_MB (default 200) and MAX_MOD_ZIP_MB (default 64).
  - Parameters:
  - --tag vX.Y.Z (required).
  - --out-dir <path> (optional).
  - --mod-id <id> (optional).
  - Examples:
  - `.\build.cmd release-check-assets --tag v0.0.1`

- `.\build.cmd release-notes`
  - Generate release-notes markdown/text for a tag.
  - Parameters:
  - --tag vX.Y.Z (required).
  - --repo owner/repo (required).
  - --out <path> (optional).
  - Examples:
  - `.\build.cmd release-notes --tag v0.0.1 --repo owner/repo`

## Commit Workflows

- `.\build.cmd commit`
  - Create a Conventional Commit.
  - Parameters:
  - --type feat|fix|... (optional).
  - --scope <scope> (optional).
  - --subject "message" (required in non-interactive mode).
  - --breaking (optional).
  - Examples:
  - `.\build.cmd commit`

- `.\build.cmd commit-check`
  - Validate a commit message string, a commit message file, or a git commit range.
  - Parameters:
  - --message "..." (optional).
  - --commit-file <path> (optional).
  - --range <BASE..HEAD> (optional).
  - Examples:
  - `.\build.cmd commit-check --message "feat: x"`
  - `.\build.cmd commit-check --range origin/main..HEAD`

## Utility Workflows

- `.\build.cmd docs-sync`
  - Regenerate docs/automation.md from workflow metadata.
  - Examples:
  - `.\build.cmd docs-sync`

