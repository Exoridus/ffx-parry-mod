# AI Repository Rules — ffx-parry-mod

These rules apply to all AI assistants working in this repository, including Claude, Codex, Gemini, and similar coding agents.

## ffx-kb first

Maintained in ffx-knowledge-base/docs/ffx-kb-first.md and propagated by
`build.cmd sync-kb-rule`. Do not edit the block below; edit it there.

<!-- ffx-kb-first:portable:begin -->
Dialogue, data structures, asset mappings, decompiled code, the state of the
modding community including its repos and tools, earlier forensic attempts
including the failed ones, and general knowledge about the game and its
internals - all of it goes to the `ffx-kb` MCP tools first. Not web search,
not model knowledge, not fresh reverse engineering. FFX-2 included.

Hold an identifier (engine address, ATEL function id, model raw id, a struct
offset like `Chr+0x433`, an event id): use `lookup`. Hold a concept: use
`search` with the words a write-up would use, never a CamelCase symbol - a
symbol query returns zero hits from a store that holds the answer. Name only,
and both came back empty: `grep canonical/` in ffx-knowledge-base.

Research or reverse engineering starts only when the answer is genuinely
missing or marked uncertain, judged on the fields the tools return:

- Nothing in the `findings`, `canonical` and `engine` blocks, and `lookup`
  resolved on no spine.
- `status` is `hypothesis`, `disputed` or `obsolete`; or `confidence` is
  `low`; or `findings(neverVerified: true)` returns the claim. `verified` and
  `strong_evidence` are answers; `probable` is usable but re-check it.
- The claim rests on evidence whose `resolution_status` is neither `resolved`
  nor `symbol`.
- Every hit carries `platform` `switch` or `ffx2`. Those corroborate a PC
  answer and are never one.

**`falsified` is an answer, not a gap.** It means the approach was tested and
disproved, and the record exists precisely so nobody retries it. Never treat it
as a missing answer and never re-derive it: read the claim, pick a different
approach, and if the new approach also fails, write that down too.

**`catalog_sources_not_walked` means "never full-text indexed", not
"unknown".** The same holds for `unreachable_sources`. Those sources are
recorded by path, size and mtime on purpose - the NAS shares, the Ghidra
exports - so the index cannot answer from their content and was never meant to.
Open the file or the NAS path. Reverse engineering something that sits in an
unwalked source is the most expensive way to be told what it already says.

Whatever the research produces goes back in immediately, in the same session,
before the next task - not at session end. In the ffx-knowledge-base checkout:
write `canonical/ffx/findings/curated/<slug>.json`, then `build.cmd
build-findings`, then `build.cmd kb-index --incremental`. Both generators are
required: the findings layer is read from `index.json`, not from `curated/`,
so skipping `build-findings` leaves the finding invisible with no error. A
negative result earns the same write-back as a positive one.
<!-- ffx-kb-first:portable:end -->


## 0) Canonical Parry + Agent Routing Addendum

Canonical gameplay authority order:
1. `.workspace/knowledge-base/FINAL_PARRY_SPEC.md`
2. `.workspace/knowledge-base/parry_interaction_model.md`
3. `.workspace/knowledge-base/TIERED_PENALTY_RATIONALE.md`
4. other active KB files under `.workspace/knowledge-base/`
5. code/tests/logs/docs
6. `.workspace/knowledge-base/archive/` historical references

Non-negotiable parry semantics:
- no hold-to-parry behavior
- no pre-held auto-arm behavior
- no persistent pre-arm/persistent protection model
- fresh R1 press opens window immediately
- hit in window = parry success
- whiff = temporary hard lockout
- tiered anti-spam penalty is secondary and temporary

Active Claude agent set:
- Main: `parry-router`, `parry-reviewer`, `parry-worker-sonnet`, `parry-worker-opus`, `ffx-forensics`
- Optional: `kb-curator`, `combat-design-reviewer`, `session-log-analyst`
- Shared memory: `.claude/agent-memory/MEMORY.md`
- Per-agent memory: `.claude/agent-memory/<agent>/MEMORY.md`
- Legacy agents/memories: `.claude/archive/`

Historical docs are valuable for context and forensics, but they must not silently override canonical design semantics.

## 1) Project Purpose and High-Level Shape

This repository is a Final Fantasy X mod loaded through Fahrenheit.

It contains:
- runtime mod code in `src/`
- shipped runtime assets in `resources/`, `lang/`, and `resources/mappings/`
- build and automation code in `build/`
- contributor and operational documentation in `docs/`
- CI/CD and repository automation in `.github/`
- local development, research, extracted data, logs, tools, and generated workspace content in `.workspace/`

Treat `.workspace/` as development infrastructure, not as runtime architecture and not as the public source of truth.

## 2) Repository Structure Rules

Use these boundaries consistently:

- `src/` → runtime mod source code only
- `resources/` → shipped media and runtime assets
- `lang/` → shipped localization resources
- `resources/mappings/` -> generated runtime mapping assets
- `build/` → build orchestration, automation, release, and setup
- `tests/` → automated tests and validation helpers
- `docs/` → contributor-facing and operational documentation
- `.github/` → CI/CD and repository automation
- `.workspace/` → local-only workspace data, generated artifacts, external clones, research outputs, logs, tools, and machine-specific content

Do not move local research or temporary outputs into runtime or shipped paths.

## 3) Runtime vs Local Workspace Rules

Only a subset of the repository is runtime-critical and shipped.

Runtime-critical:
- `src/`
- `resources/`
- `lang/`
- runtime-consumed mapping bundles in `resources/mappings/`

Local/development-only:
- `.workspace/`
- most research artifacts
- extracted raw datasets
- temporary outputs
- local notes
- machine-specific configuration

Do not treat `.workspace/` content as production-safe by default.
Do not make release logic depend on undocumented local-only workspace state unless explicitly intended and clearly documented.

## 4) Build and Workflow Contract

- Use `.\build.cmd` as the canonical command surface.
- Use `.\build.cmd help` and `.\build.cmd -h <workflow>` before introducing or changing workflow logic.
- Do not add parallel wrapper commands unless explicitly requested.
- Keep `verify` non-destructive.
- Prefer stable defaults and opt-in for risky behavior.
- Use Windows-style command examples in docs and assistant responses: `.\build.cmd ...`

## 5) Core Engineering Principles

- Prefer stable, reversible changes over broad speculative rewrites.
- Preserve runtime behavior unless behavior change is explicitly requested.
- Keep behavior deterministic where feasible.
- Use one clear control path instead of multiple hidden paths.
- Prefer minimal production surface area.
- Prefer narrow diffs and focused changes over repo-wide churn.
- State assumptions, tradeoffs, and risks explicitly.
- Mark speculative ideas as speculative.

## 6) Refactoring and Architecture Rules

- Prefer incremental restructuring over big-bang rewrites.
- Do not introduce new architectural layers unless they remove a concrete recurring pain.
- Do not split code into additional projects or assemblies without strong justification.
- Do not introduce dependency injection frameworks unless clearly justified.
- Do not add interfaces unless multiple implementations, test seams, or true boundary clarity require them.
- Avoid cargo-cult clean architecture and premature abstraction.
- Do not mix naming cleanup, architecture changes, and runtime behavior changes in one refactor unless explicitly requested.
- Keep PR-sized changes small, reviewable, and independently verifiable.
- When proposing a refactor, identify:
  - target boundary
  - touched files/areas
  - expected payoff
  - risk level
  - validation method
  - rollback path

## 7) Runtime Boundary Rules

Keep these concerns clearly separated inside runtime code:

- gameplay/parry/combat behavior
- Fahrenheit integration and hooks
- memory access foundations and offset definitions
- mappings and runtime lookup data
- settings and configuration
- overlays, diagnostics, and debug tooling
- session logging and observability

Avoid mixing low-level memory details directly into gameplay logic.
Avoid mixing experimental research code into active runtime paths.

## 8) Fahrenheit-First Integration Rules

- Prefer Fahrenheit helpers, hooks, enums, and abstractions before introducing custom low-level access.
- Reuse existing Fahrenheit functionality when available instead of duplicating wrappers.
- If custom offsets are required:
  - centralize them in `ExternalMemoryOffsetMap*`
  - keep typed access through dedicated memory registry/location types
  - avoid scattering raw addresses across gameplay code

## 9) Memory and Offset Rules

These concepts have distinct roles and should remain distinct:

- `MemoryLocation` → immutable descriptor of one memory target
- `MemoryRegistry` → named grouping of memory locations by domain
- `MemoryCandidates` → staging area for uncertain or externally mined offsets
- `ExternalMemoryOffsetMap*` → canonical raw offset definitions

Rules:
- keep uncertain offsets staged until validated
- do not present candidates as production-safe facts
- do not leak raw addresses into gameplay behavior code
- promote offsets from candidate/staging to trusted runtime usage only after explicit validation

## 10) Risky Changes Policy

Risky changes include anything touching:
- runtime hooks
- memory interaction
- startup flow
- low-level integration
- active combat behavior
- release packaging behavior

For risky changes:
- gate risky logic behind explicit opt-in where appropriate
- keep risky paths disabled by default unless explicitly requested otherwise
- define expected success and rollback conditions before implementation
- avoid repeated blind attempts
- when iteration is not converging, stop churn, return to a stable baseline, and report one clear next step

## 11) C#/.NET Style Rules

Prefer standard modern C#/.NET patterns that fit this repository:

- file-scoped namespaces
- nullable-aware code
- clear small methods with focused responsibility
- explicit state transitions over implicit side effects
- one primary type per file
- filename matches primary type
- related partials use `TypeName.<Domain>.cs`

Naming conventions:
- `PascalCase` for types, methods, properties, and public constants
- `_camelCase` for private instance fields
- `camelCase` for locals and parameters

For touched code:
- resolve warnings introduced by the change
- remove dead fields, stale toggles, and temporary probe code after validation
- prefer concrete, readable implementations over clever indirection

## 12) Tests and Verification Rules

Use realistic verification, not fake ceremony.

Prefer automated checks for:
- deterministic logic
- config validation
- mapping validation
- schema validation
- package content assertions
- build/release assertions
- smoke checks
- golden-file tests where appropriate

Do not force artificial unit-test seams into tightly runtime-coupled code unless there is clear payoff.

After edits:
- run the smallest relevant validation first
- then run broader verification only if justified by the scope

## 13) CI/CD and Release Rules

- Conventional Commits are required.
- Keep release tags immutable; fix forward with a new version.
- Keep release outputs deterministic.
- Attach checksums when supported.
- Ensure required checks pass before merge or release.
- Add guardrails against accidentally shipping local, research, or generated workspace-only content.

## 14) Documentation Rules

- Keep docs aligned with real workflows and implemented parameters.
- Remove stale docs when behavior is removed or changed.
- Prefer concise operational docs over research-heavy narrative in public-facing files.
- Keep public docs focused on what contributors and users actually need.

## 14a) Text Encoding, Diacritics, and Shipped Strings

- Source comments, API documentation, `docs/`, commit messages and PR text are
  English and use ASCII punctuation. That bans the typographic variants - em
  dash, en dash, curly quotes, ellipsis - and nothing beyond them.
- **Letters are never transliterated.** A letter carrying a diacritic is a
  letter, not punctuation: `während`, `für`, `Zügen`, `abschließt`. Never
  `waehrend`, `fuer`, `Zuegen`, `abschliesst`. This applies to any language,
  including names and cited titles.
- `lang/*.json` is shipped product text and is exempt from the English rule
  entirely. Each locale is written in its own natural orthography; German uses
  ä, ö, ü, Ä, Ö, Ü and ß. `ae`/`oe`/`ue` in a shipped string is a defect.
- **Every character in `lang/*.json` must be U+00FF or below.** Fahrenheit
  builds its ImGui font with `io.Fonts.GetGlyphRangesDefault()`
  (`core/framework/imguihelp.cs`), which covers Basic Latin plus Latin-1
  Supplement only. Umlauts and ß are inside that range and render correctly;
  an em dash (U+2014), a curly quote or an ellipsis character is outside it and
  draws as a missing glyph in the settings panel. Use a comma, a plain hyphen
  or three periods instead.
- Identifiers stay ASCII in the other direction: setting ids, JSON keys, file
  names, C# symbols and CLI flags, because the loader and the toolchain key on
  them.

## 15) Assistant Working Style Rules

- Prefer concrete edits over abstract recommendations.
- If blocked, report the exact blocker and the best fallback.
- If repository visibility is incomplete, say what is known, what is inferred, and what is conditional.
- Do not flatter.
- Do not recommend overengineering.
- Do not assume enterprise patterns are automatically appropriate for a modding project.
- Keep runtime logic separate from research artifacts and local-only infrastructure.

## 16) Default Priorities

Unless explicitly overridden:
1. stability and reproducibility
2. correctness and determinism
3. runtime safety
4. code clarity and maintainability
5. contributor friendliness
6. optional enhancements
