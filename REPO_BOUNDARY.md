# Repo Boundary — ffx-parry-mod ↔ ffx-knowledge-base

This document describes the intended split between this repository and the
sibling `ffx-knowledge-base` repo.

## This repo (ffx-parry-mod) owns

- **Runtime mod code** under `src/` — Fahrenheit-hosted parry logic, ATEL
  patches, damage redirection, UI hooks, audio, etc.
- **Mod resources, lang files, mappings** under `resources/`, `lang/`,
  `resources/mappings/` — everything that ships as part of the loaded mod.
- **Mod-build orchestration** — the Fahrenheit runtime clone bootstrap
  (`build/Build.proj`), mod compilation, mod release/versioning (`build.cmd
  build`, `build.cmd release-*`, `build.cmd commit-prep`).
- **Mod governance** — `AGENTS.md`, imported by the one-line `CLAUDE.md`.
- **Mod runtime config** — `GameDir` (where to install the built mod),
  `AutoDeploy`, `DeployPreservePaths`, build-mode defaults.
- **Deployed knowledge packs as read-only consumption** under
  `.workspace/knowledge-base/ffx-pipeline/`. These are published here by
  the pipeline repo's `deploy` workflow, not regenerated locally.

## The pipeline repo (ffx-knowledge-base) owns

- **Extraction, parsing, and analysis** of FFX binary game data and the
  decompilation snapshots.
- **Canonical outputs** (symbols, functions, callgraph, game data,
  crossrefs, community findings).
- **Discord processing** (export via DiscordChatExporter, OCR enrichment,
  refs extraction, finding crossrefs).
- **Pipeline tooling** under `ffx-knowledge-base/tools/`:
  FFXDataParser, VBFTool, DiscordChatExporter, Tesseract, Ghidra.
- **Compact knowledge packs** under `packs/ffx/` — the deployable output.
- **All pipeline config** in `ffx-knowledge-base/config.json`.

## Clear rules

1. **Pipeline workflows run from the pipeline repo.** If you want to run
   `data-parse`, `discord-sync`, `crossref-*`, `extract-*`, or similar,
   `cd ../ffx-knowledge-base` and run `build.cmd <workflow>` there.
   This repo's `build.cmd` only handles mod-build concerns.
2. **Deploy is pulled from the pipeline repo.** To refresh the packs under
   `.workspace/knowledge-base/ffx-pipeline/`, go to the pipeline repo and
   run `build.cmd deploy`. Do not regenerate packs locally here.
3. **Pipeline settings belong in the pipeline repo's `config.json`.**
   Discord tokens, Vision API keys, FFX data paths — these live in the
   pipeline repo, not in `.workspace/config.local.json`.

## Current state

`build.cmd` exposes mod workflows only. Research tooling runs in the pipeline repository. Cleanup removes generated mod build outputs and leaves research inputs and local settings alone.

## Active `.workspace/` contents

The following are mod-owned and still live:

- `.workspace/fahrenheit/` — cloned Fahrenheit runtime (mod build dependency)
- `.workspace/knowledge-base/` — deployed packs + mod design docs
- `.workspace/config.local.json` — mod build settings (GameDir, AutoDeploy, etc.)
- `.workspace/external/` — reference repos (if present)
- `.workspace/logs/` — transient mod debug logs

## Runtime mappings (`resources/mappings/`)

The mod ships locale-specific mapping bundles that it loads at runtime for
command/monster/battle/event name resolution. These are **not** knowledge-base
artifacts — they are a runtime-consumed, mod-specific bundle format.

**This repo now only contains `resources/mappings/`.** The old
`mappings/source/` tree was deleted once the pipeline-side producer moved
off of it.

- **`resources/mappings/ffx-mappings.{locale}.json`** — **runtime-required.**
  Loaded by `src/.../ParryModule.DataMapping.cs` via `LoadFromDirectories()`,
  packaged by Fahrenheit.Sdk's standard resources rule and verified by
  `build/Build.Quality.cs` smoke checks.
  These files stay here — they are mod-owned (consumed at runtime) but
  produced in the pipeline repo.

### Regeneration path

The canonical producer is `build.cmd build-mod-runtime-bundles` in the
sibling `ffx-knowledge-base` repo. Run it after any pipeline data
refresh:

```
cd ../ffx-knowledge-base
build.cmd build-mod-runtime-bundles
```

The workflow reads exclusively from pipeline-owned canonical outputs:

| Runtime domain  | Pipeline input                                           |
|-----------------|----------------------------------------------------------|
| `Commands`      | `game_data/items/`, `game_data/commands/`, `game_data/monster_abilities/` |
| `AutoAbilities` | `game_data/gear_abilities/`                              |
| `KeyItems`      | `game_data/key_items/`                                   |
| `Monsters`      | `game_data/monsters/`                                    |
| `Battles`       | `scripts/text/<locale>/battles.json`                     |
| `Events`        | `scripts/text/<locale>/events.json`                      |

Paths are relative to `canonical/ffx/` in the pipeline repo, and each
game-data directory holds one `<family>_<locale>.json` per locale. The
`Battles`/`Events` inputs are no longer frozen snapshots: `build.cmd
extract-script-text` produces all 8 locales under `canonical/ffx/scripts/`.

Do not hand-edit the runtime bundles in `resources/mappings/`. Run the
generator instead.
