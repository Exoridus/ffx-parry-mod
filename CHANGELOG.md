# Changelog

## v0.5.4 (2026-10-25)

Same mod as v0.5.3, whose release was published without its packages: the repository's immutable releases rejected the uploads. Releases are now assembled as drafts and published afterwards.

## v0.5.3 (2026-10-25)

- Read the current event name once per event instead of on every main loop pass; the repeated eventid.bin reads competed with the game's loader and stretched scene transitions.
- Call MsExeInputCue with its real signature (actor id and mode, returning a value) so the hook no longer corrupts the stack.
- Refuse to load on any executable other than Steam build 25501027, checked by SHA-256, instead of hooking unknown code.
- Release from the official Fahrenheit alpha12 SDK on version tag pushes.
- Restore the README banner with new artwork; move internal research notes to ffx-knowledge-base.

## v0.5.2 (2026-10-15)

Development build for the pinned Fahrenheit alpha12 source and matching local SDK. Official-SDK publication remains blocked until the corresponding upstream packages are available.

- Adopt SDK-generated manifests, module settings providers and localization conventions.
- Preserve stable Easy/Normal/Expert setting values and exclude diagnostic probes from Release.
- Move runtime mappings to resources/mappings and remove the duplicate default bundle.
- Consolidate C# build automation and remove retired PowerShell entry points and duplicate configuration.
- Produce validated mod-only and full ZIPs with checksums and build provenance through aligned CI and release workflows.
- Rename repository references to ffx-parry-mod and replace the README with current install and development instructions.

Game compatibility with Steam build 25501027 has not been verified in game. Live per-hit tracking for multi-hit attacks remains incomplete.

## [v0.5.1](https://github.com/Exoridus/ffx-parry-mod/releases/tag/v0.5.1) (2026-08-17)
[Full Changelog](https://github.com/Exoridus/ffx-parry-mod/compare/v0.5.0...v0.5.1) | [Previous Releases](https://github.com/Exoridus/ffx-parry-mod/releases)

- fix(setup): discover MSBuild via vswhere instead of PATH only ([f93c2a2](https://github.com/Exoridus/ffx-parry-mod/commit/f93c2a21c3b38d24e68795be41bcb135f423ca18))
- build(fahrenheit): re-pin to 0a90818 (18 commits, ImGui 3.x) ([7e5f198](https://github.com/Exoridus/ffx-parry-mod/commit/7e5f198869b76706af753469e95ebea463f2f997))
