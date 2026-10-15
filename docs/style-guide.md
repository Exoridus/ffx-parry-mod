# Style Guide

This repository uses English-only source and documentation conventions.

## Language

- Use English for code, comments, docs, logs, and commit messages.
- Keep user-facing text localizable via `lang/*.json` where applicable.

## C# Conventions

- Prefer modern .NET/C# patterns and keep methods focused.
- Naming:
  - `PascalCase` for types, methods, properties.
  - `_camelCase` for private fields.
  - `camelCase` for locals and parameters.
- File naming:
  - primary type in `TypeName.cs`,
  - partials in `TypeName.<Domain>.cs`.
- Prefer framework abstractions over raw low-level access.

## Fahrenheit Integration

- Prefer Fahrenheit helpers/functions/hooks/enums before introducing custom wrappers.
- When custom offsets are necessary, keep them centralized in `ExternalMemoryOffsetMap` and typed access layers.

## Repository Layout

- `build/` build orchestration and automation code.
- `src/` runtime mod source.
- `resources/` external media and bundled runtime assets.
- `tests/` automated tests.
- `docs/` contributor and operational documentation.
- `lang/` localization files.
- `resources/mappings/` generated runtime bundles.
- `.workspace/` local/generated workspace data.

## Formatting and Enforcement

- `.editorconfig` defines baseline formatting/style expectations.
- To auto-fix formatting/style issues locally, run:

```cmd
dotnet format Fahrenheit.Mods.Parry.csproj --no-restore
```

After a one-time format-normalization commit, `--verify-no-changes` can be enabled in CI/verify to enforce style strictly.

## Commits

- Conventional Commits are required.
