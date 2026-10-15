<div align="center">

# FFX Parry

### Turn incoming attacks into a timing challenge.

[![Latest](https://img.shields.io/github/v/release/Exoridus/ffx-parry-mod?include_prereleases&style=for-the-badge&logo=github&label=Latest)](https://github.com/Exoridus/ffx-parry-mod/releases)
[![CI](https://img.shields.io/github/actions/workflow/status/Exoridus/ffx-parry-mod/ci.yml?branch=main&style=for-the-badge&logo=githubactions&logoColor=fff&label=CI)](https://github.com/Exoridus/ffx-parry-mod/actions/workflows/ci.yml)
[![Downloads](https://img.shields.io/github/downloads/Exoridus/ffx-parry-mod/total?style=for-the-badge&logo=github)](https://github.com/Exoridus/ffx-parry-mod/releases)

A Fahrenheit mod for **Final Fantasy X HD Remaster on Windows**.

**[Downloads](https://github.com/Exoridus/ffx-parry-mod/releases)** · **[Development builds](https://github.com/Exoridus/ffx-parry-mod/actions/workflows/ci.yml)** · **[Install](#installation)** · **[Settings](#settings)** · **[Report a bug](https://github.com/Exoridus/ffx-parry-mod/issues)**

</div>

> [!IMPORTANT]
> **Experimental alpha12 development.** This branch uses matching local Fahrenheit and SDK packages from the commit in [fahrenheit.release.ref](fahrenheit.release.ref). These packages are development builds, not official-SDK releases. Build and packaging checks do not prove in-game compatibility with Steam build **25501027**. FFX-2 is unsupported.

## What it provides

| Feature | Behavior |
| --- | --- |
| Timed parries | A fresh R1 press, or P on keyboard, opens a short parry window. |
| Difficulty | Easy, Normal and Expert timing presets, selected in Fahrenheit settings. |
| Feedback | Combat feedback, localization and optional session logging. |

## Installation

Requires a compatible Fahrenheit installation. Follow the [upstream prerequisites](https://github.com/fahrenheit-crew/fahrenheit/wiki/Installation-guide): **.NET Runtime 10** and the latest **Visual C++ Redistributable**, each in x86 and x64 versions. The mod runs as x86; a 64-bit runtime alone is insufficient. See the [Fahrenheit compatibility guide](https://github.com/fahrenheit-crew/fahrenheit/wiki/Compatibility-guide).

| Download | Destination | Use |
| --- | --- | --- |
| `fhparry-mod-<tag>.zip` | `GAME_DIR/fahrenheit/mods/` | Add the mod to an existing, matching Fahrenheit installation. |
| `fhparry-full-<tag>.zip` | `GAME_DIR/`, beside `FFX.exe` | Install the mod together with its matching Fahrenheit host. |

### Mod only

1. Download the mod ZIP and its `.sha256` file. For development packages, use the CI artifact and the exact Fahrenheit source pin recorded in its provenance.
2. Close the game. Extract the ZIP into `fahrenheit/mods/`; the resulting DLL must be `fahrenheit/mods/fhparry/fhparry.dll`.
3. Add `fhparry` on its own line to the extensionless `fahrenheit/mods/loadorder` file, if it is absent. Keep existing entries.
4. Launch through Fahrenheit and open **F7** for settings.

Keep the entire `fhparry/` folder together, including its generated manifest, dependency metadata, PDB, language files and resources. GitHub's automatic source-code ZIP is not an installable mod.

### Full package

Extract the full ZIP next to `FFX.exe` and run `start-ffx-with-mods.cmd`. The package includes `fahrenheit/bin/` and a loadorder containing this mod. Back up an existing Fahrenheit installation first; use the mod-only package when keeping other mods and configuration. The runtime is a prerequisite and is not installed automatically.

Compare `Get-FileHash .\downloaded.zip -Algorithm SHA256` with the accompanying `.sha256` file. CI artifacts are download containers: unpack that download first, then install the mod or full ZIP inside it.

To update, close the game and replace the mod's entire folder with the new one. Preserve Fahrenheit's configuration and loadorder. To uninstall, remove the `fhparry` loadorder entry and its folder.

## Settings

Use Fahrenheit's native **F7** settings menu. English and German labels are included. Settings are saved by Fahrenheit; do not edit the shipped localization or manifest files to configure the mod. When updating from the old alpha11 settings layout, keep a backup of your configuration and check your selections after the update.

## Known limitations

Multi-hit handling still uses authored attack metadata; it does not yet track every hit from live observations. Debug overlays and native probes are excluded from Release.

Fahrenheit alpha11 targets the older Steam executable; alpha12 targets the October 2026 executable line. Matching the framework alone does not verify this mod's custom hooks against a new game build. Reports should include the Steam build ID, mod tag, Fahrenheit version/source pin, scene and enabled settings.

## Build from source

Requires **Git and the .NET 10 SDK** selected by [global.json](global.json). The commands below build and validate only the mod, without a game installation or automatic deployment.

```powershell
git clone https://github.com/Exoridus/ffx-parry-mod.git
cd ffx-parry-mod
.\build.cmd mod-build --sdk local --configuration Debug
.\build.cmd mod-verify --sdk local --configuration Release
.\build.cmd mod-pack --sdk local --configuration Release --tag v0.0.0-dev --out-dir .release
```

`local` checks out the committed Fahrenheit pin and builds missing core/SDK packages. The mod builds in an isolated workspace with a package cache separated by package content. SDK-generated metadata and every runtime asset are checked before packaging. The ZIP contains exactly one top-level folder, `fhparry/`.

To also produce the full package, append `--full true` to `mod-pack`. This requires Visual Studio C++ Build Tools, an x86 VC++ toolset, Windows SDK Debugging Tools and integrated vcpkg. The matching Fahrenheit host is built from fresh deployment output. The shipped CMD launcher is Windows glue; maintained build and packaging logic is C#.

For public distribution, use `--sdk official --tag v<project-version>`. That mode restores exclusively from nuget.org into a separate cache and uses the matching official Fahrenheit release for the full package. It fails explicitly while the required official SDK or host is unavailable.

## CI and releases

CI verifies Debug and produces tested Release mod/full development packages. Both archives have SHA256 checksums and provenance recording the SDK origin, source commit and payload hashes. Release uses the same build and package checks.

Maintain the version in the mod `.csproj`, merge to `main`, then run a development rehearsal:

```powershell
gh workflow run release.yml -R Exoridus/ffx-parry-mod -f version=v<project-version> -f sdk=local -f dry_run=true
```

Public releases require `sdk=official` and `dry_run=false`. Only after verification succeeds does the workflow create an experimental prerelease and its tag. Local-SDK packages cannot pass that publication gate. No release is created by a normal push.

## Related projects

[Fahrenheit](https://github.com/fahrenheit-crew/fahrenheit) · [FFX Parry](https://github.com/Exoridus/ffx-parry-mod) · [FFX Quality of Life](https://github.com/Exoridus/ffx-qol-mod) · [FFX FPS Unlock](https://github.com/Exoridus/ffx-fpsunlock-mod)

Final Fantasy X belongs to Square Enix. This is an independent community project. No game executable, extracted game assets or replacement videos are distributed.
