# Fork of KSP Interstellar Extended

This repository is a fork of **KSP Interstellar Extended** (KSPI-E) by sswelm (FreeThinker) and contributors:
https://github.com/sswelm/KSP-Interstellar-Extended — based on the work of the KSP Interstellar team
(original KSP Interstellar by Fractal_UK). The license is in [License.md](License.md) and
`GameData/WarpPlugin/KSPI_License.txt`, both unmodified.

Fork maintainer: Geny (genyk1p). Work on this fork is done with Claude Code (Anthropic), model Claude Opus 5.5.

## Why

The last release, 1.29.6, is from 2021-11-14. Upstream still merges pull requests (2022–2026), but no release
has been made since, so those fixes never reached players. This fork builds them, keeps a list of known
problems ([KNOWN_ISSUES.md](KNOWN_ISSUES.md)) and adds fixes for a Kerbalism + KSPI-E game.

## Changes from upstream master

Original files are the ones in upstream master; everything changed by the fork is in the git history after
commit `3b495342` (upstream master on 2026-10-31).

- `FNPlugin/WarpPlugin.csproj`: `Propulsion/WaterfallIntegration.cs` added to the build (PR #692 added the file but
  not to the project, so master did not compile); reference to `Waterfall.dll`; FNPlugin targets .NET Framework 4.8,
  because Waterfall is built for 4.8.
- `FNPlugin/Propulsion/WaterfallIntegration.cs`: ported to the current Waterfall API (0.8+: `CustomController`
  became `CustomPushController`, values are set with `ModuleWaterfallFX.SetControllerValue`).
- `build.ps1`: build with the .NET SDK, no Visual Studio needed; `-Deploy` copies into a KSP install.
- Beam emitter wear (`FNPlugin/Beamedpower/BeamGeneratorWear.cs`, `GameData/WarpPlugin/Patches/BeamGeneratorWear.cfg`):
  conversion efficiency drops with the hours run at full power (faster with waste heat), an engineer on EVA replaces
  the emitters. With Kerbalism, a `Reliability` on the generator module; the transmitter stops the beam when the
  generator is disabled by a failure, loaded or unloaded (`MicrowavePowerTransmitter.cs`).
- `GameData/WarpPlugin/Localization/Fields/ru.cfg`: Russian strings for the fork's additions.
- `GameData/WarpPlugin/Plugins/Interstellar.dll` and `GameData/Interstellar_Redist.dll` rebuilt from this source
  (upstream's committed binaries are the 1.29.6 ones). Both must be installed together: the new Interstellar.dll
  uses fields added to Interstellar_Redist after 1.29.6 and fails part loading with an old one.
- `GameData/WarpPlugin/Localization/Parts/ru.cfg`: Russian part descriptions completed (translations missing in 1.29.6 added).

## Merged upstream after 1.29.6 and not released (included here)

| Commit | What |
|---|---|
| 3bd0e62a (#698) | Reactor Control Window in the VAB/SPH fixed and upgraded |
| 6fc34747 (#692) | Waterfall effects for RCS thrusters |
| 59df2c56 (#699) | Thermal jet nozzle fixes |
| 9b83f692 (#700) | MHD generator fix |
| f193417d (#702) | Reactor products for propulsion calculated correctly |
| 8eaefb39 | MiniAVC removed |
| 07cfabc7 | Null reference check (beamed power, issue #720) |
| 8a55a180 (#728) | Localization fixes |
| 8178909e (#734) | Russian localization of tech nodes |
| b10b676e (#741) | Uranium-233 in ISRU; Uranium removed from Thorium fuel modes |
| 85e35d2f (#745) | FNImpactorModule event subscription leak |
| 37d67d7f (#746) | Minmus name typo in resource configs |
| 69166e2b (#747) | UraniumNitride in the Hex Radioactive Container (resource name case) |
| 9b607eab (#748) | WarpPluginSettings typos, wrong parameter name in PluginSettings.cs |
| 3b495342 (#749) | Runaway reactor fuel consumption at high time warp |

## Building

```
powershell -ExecutionPolicy Bypass -File build.ps1           # build
powershell -ExecutionPolicy Bypass -File build.ps1 -Deploy   # build and install into KSP (game closed)
```

The `.csproj` files reference KSP by its default Steam path; change the HintPaths if KSP is elsewhere.

## Redistribution

Per the license: redistributions of the compiled form must say they are based in part on the work of the
KSP Interstellar team, link the original (above), and include the source. A release of this fork under another
name must not use "KSP Interstellar" as its product name (license section 5) except to describe its origin.
