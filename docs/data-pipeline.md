# Data Pipeline Guide

The mod ships locale-specific mapping bundles under `resources/mappings/` that
are loaded at runtime to resolve command / monster / battle / event display
text. The extraction and parsing of FFX game data that ultimately produces
that reference data is **no longer owned by this repo**. See
[REPO_BOUNDARY.md](../REPO_BOUNDARY.md) for the ownership split.

## Where the data lives now

All FFX game-data extraction, parsing, and analysis lives in the sibling
pipeline repo: `../ffx-knowledge-base`. That repo owns:

- Raw VBF extraction (`build.cmd data-extract`)
- FFXDataParser invocation (`build.cmd data-parse`, `data-parse-all`,
  `run-dataparser-commands`, `run-dataparser-scripts`)
- Canonical localized JSONs under
  `ffx-knowledge-base/canonical/ffx/game_data/`, one directory per family
  and one file per locale: `commands/commands_<locale>.json` (plus
  `commands/commands_mechanics.json`), `monsters/monsters_<locale>.json`,
  `gear_abilities/gear_abilities_<locale>.json`,
  `items/items_<locale>.json`, `key_items/key_items_<locale>.json`,
  `monster_abilities/monster_abilities_<locale>.json`, `weapon_names.json`.
- Crossrefs (`canonical/ffx/game_data/crossref/`), scripts
  (`canonical/ffx/scripts/`, with per-locale script text under
  `canonical/ffx/scripts/text/<locale>/`), community findings
  (`canonical/ffx/community/`), and packs (`packs/ffx/`).

See `ffx-knowledge-base/README.md` for the full workflow list and
example queries.

## Runtime mapping bundles (this repo)

This repo still owns the runtime bundle format under `resources/mappings/`:

- `resources/mappings/ffx-mappings.{locale}.json` — per-locale bundle
  (`us`, `de`, `fr`, `it`, `sp`, `jp`, `ch`, `kr`)

These bundles combine commands, auto-abilities, key items, monsters, battles,
and events into a single loadable file per locale. They are shipped with the
mod by Fahrenheit.Sdk's standard `resources/**` packaging rule.

They are loaded at runtime by `ParryModule.DataMapping.cs` for display-name
resolution in the overlay and debug UI.

## Bundle regeneration

The canonical producer is `build.cmd build-mod-runtime-bundles` in the
sibling pipeline repo:

```
cd ../ffx-knowledge-base
build.cmd build-mod-runtime-bundles
# optionally: build.cmd build-mod-runtime-bundles --dry-run
```

The workflow reads exclusively from pipeline-owned canonical outputs and
writes `resources/mappings/ffx-mappings.<locale>.json` for all 8 locales,
plus `ffx-mappings.provenance.json`.

Input sources per runtime-bundle domain:

| Runtime domain  | Pipeline input                                           |
|-----------------|----------------------------------------------------------|
| `Commands`      | `canonical/ffx/game_data/items/`, `commands/`, `monster_abilities/` |
| `AutoAbilities` | `canonical/ffx/game_data/gear_abilities/`                |
| `KeyItems`      | `canonical/ffx/game_data/key_items/`                     |
| `Monsters`      | `canonical/ffx/game_data/monsters/`                      |
| `Battles`       | `canonical/ffx/scripts/text/<locale>/battles.json`       |
| `Events`        | `canonical/ffx/scripts/text/<locale>/events.json`        |

Every file in that table is a per-locale file named `<family>_<locale>.json`
inside its family directory. The `Battles`/`Events` inputs are no longer
frozen snapshots: `build.cmd extract-script-text` produces all 8 locales
under `canonical/ffx/scripts/text/`.

Do not hand-edit `resources/mappings/` directly. Run the generator instead.

The old `mappings/source/` tree that previously lived in this repo was
deleted once the pipeline producer moved onto canonical outputs.

## Notes

- Data extraction/parsing is not required to build or run the mod; the
  runtime bundles in `resources/mappings/` are sufficient.
- Mod releases consume the pre-generated JSON bundles; no re-parsing is
  performed at build time.
