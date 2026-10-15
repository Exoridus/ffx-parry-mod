# Changelog

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
