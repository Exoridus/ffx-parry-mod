# Localization Strategy

This project uses two localization layers with different purposes:

- `lang/Fahrenheit.Mods.Parry.ParryModule/*.json`
  - UI labels/descriptions for the mod settings panel.
  - Consumed by Fahrenheit mod localization loading.
- `resources/mappings/ffx-mappings.<locale>.json`
  - Game data mappings (commands, monsters, battles, events, etc.).
  - Consumed by this mod's runtime mapping loader.

## Canonical Locale IDs

Canonical IDs used by mapping workflows:

- `us`, `de`, `fr`, `it`, `sp`, `jp`, `ch`, `kr`

## Language File Policy

`lang/` uses one file per language with the same language IDs Fahrenheit expects:

- `en-US.json` (framework-style English)
- `de-DE.json` (framework-style German)

Current localized content is complete for English and German. Additional locales can be added incrementally by creating matching `lang/Fahrenheit.Mods.Parry.ParryModule/<FahrenheitLangId>.json` files.

## Runtime Mapping Fallback

At runtime, mapping lookups are locale-aware:

1. Try preferred locale bundle (for example `ffx-mappings.de.json`)
2. Fall back to `ffx-mappings.us.json`

## Build/Data Pipeline

The legacy `map-import` / `map-build` workflow (previously `build/Build.Data.cs`)
was removed as part of the pipeline-vs-mod boundary cleanup. The canonical
FFX data extraction, parsing, and per-locale JSON generation now lives in
the sibling `ffx-knowledge-base` repo — see
`docs/mods/parry/data-pipeline.md` there for the split.

The `resources/mappings/ffx-mappings.{locale}.json` bundles are the
runtime-loaded format consumed by `ParryModule.DataMapping.cs`. The
canonical producer is `build.cmd build-mod-runtime-bundles` in the
sibling pipeline repo. It reads exclusively from pipeline-owned canonical
outputs (`canonical/ffx/game_data/<family>/<family>_<locale>.json`) and
script text (`canonical/ffx/scripts/text/<locale>/`). Run it after any
pipeline data refresh to regenerate all 8 locale bundles.
