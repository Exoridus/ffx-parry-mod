# Contributing

## Scope

- This repository is Windows-first for build/deploy workflows.
- Keep automation changes in `build.cmd`, `build/`, and `.github/workflows/`.
- Keep gameplay/runtime changes inside `src/`.

## Language and Style

- Follow the repository style guide: [docs/style-guide.md](docs/style-guide.md).
- Conventional Commits are required for all commit subjects.

## Prerequisites

- Git
- .NET SDK 10.x
- For full builds: Visual Studio Build Tools + `vcpkg integrate install`

Install prerequisites:

```cmd
.\build.cmd install --full
```

## First-Time Setup

```cmd
.\build.cmd setup
.\build.cmd doctor
.\build.cmd verify --config Debug
```

`setup` configures local git hooks and Fahrenheit workspace setup.

## Daily Workflow

```cmd
.\build.cmd lint --config Debug
.\build.cmd verify --config Debug
.\build.cmd build --payload mod --config Debug
.\build.cmd deploy --payload mod --mode merge
```

Optional cleanup:

```cmd
.\build.cmd clean
.\build.cmd clean --full
```

## Commit Conventions

- Use Conventional Commits:
  - `feat: ...`
  - `fix: ...`
  - `docs: ...`
  - `refactor: ...`
  - `build: ...`
  - `ci: ...`
  - `chore: ...`
- Keep each commit focused (one concern per commit where possible).
- Do not mix broad refactors with behavior changes unless inseparable.
- Local `commit-msg` hook blocks invalid commit subjects.
- CI validates commit subjects in PRs.

Helpful command:

```cmd
.\build.cmd commit
.\build.cmd commit --type feat --scope ui --subject "add queue row grouping"
```

## PR Expectations

- Run `.\build.cmd verify --config Debug` before opening/updating PRs.
- Include concise testing notes in the PR description:
  - build config used
  - whether in-game validation was performed
  - any known limitations
- Keep PRs small and reviewable when possible.
- Update docs when command names, workflows, or user-facing behavior change.

## Release Flow

1. Ensure `main` is clean and up to date.
2. Run:

```cmd
.\build.cmd mod-pack --sdk local --tag v0.0.0-dev --out-dir .release --full true
.\build.cmd release-bump --bump patch --repo Exoridus/ffx-parry-mod
git push origin main
gh workflow run release.yml -f version=v1.2.3 -f sdk=local -f dry_run=true
```

`release-bump` updates version/changelog, pins `fahrenheit.release.ref`, and creates the release commit. It does **not** create a tag.

The Release workflow creates the tag only after a green release build, so a failed build never burns a version number -- fix it, push, and dispatch the same version again.

## Local Development Notes

### Data Mapping Directory

The canonical environment variable for the runtime data mapping directory is `FH_PARRY_DATA_MAP_DIR`. Set this to a directory containing locale mapping bundles (e.g. `ffx-mappings.us.json`).

`FHPARRY_DATA_MAP_DIR` is a deprecated alias. It still works but logs a warning at startup. Migrate to `FH_PARRY_DATA_MAP_DIR`.

### `.workspace/` Directory

`.workspace/` is intentional local research infrastructure. It contains local toolchain copies, NAS-backed dataset symlinks, extracted data, logs, private notes, and machine-specific configuration. It is not runtime architecture and not the public source of truth.

Do not commit `.workspace/` content to PRs. Do not modify or clean `.workspace/` contents in automated tooling or refactor PRs. Its contents are machine-specific and may include large datasets or symlinks that do not exist on other machines.
