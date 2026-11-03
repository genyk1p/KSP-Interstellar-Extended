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
- Cryostat with Kerbalism (`FNPlugin/Storage/FNModuleCryostat.cs`): an empty tank, or one switched to another resource,
  no longer reports boiloff. Kerbalism's boiloff process is vessel-wide, so that boiloff drained the other, cooled tanks
  (KNOWN_ISSUES L1). The Kerbalism cooling variables are zeroed whenever the tank is not being cooled (empty, cooling
  off, switched): upstream left them at the last value and the "Cryo Cooling" process kept drawing electric charge.
- `lethalDistance = 0` on the chemical engines H.A.L.B.E.R.D. and both methane engines: they shut down near crewed
  vessels with the fusion default of 2000 m (upstream #717).
- Methane pyrolysis is CH4 => C + 2 H2 without oxygen (was a copy of partial oxidation, upstream #724); partial methane
  oxidation uses the mass fractions of 2 CH4 + O2 => 2 CO + 4 H2 (the products were 0.76 of the consumed mass).
- `Patches/B9PartSwitch/CryoTanksFuelTankSwitcher.cfg`: the KSPIE switcher replaces CryoTanks' own one on LH2 tanks
  (the CryoTanks hydrogen tanks, two magnetic nozzles) and had dropped the LH2/Ox, methane and methalox fuels CryoTanks
  offers (upstream #737, #668); they are appended — methane as KSPIE's LqdMethane tank type, methalox with CryoTanks'
  LMOx, only when CryoTanks is installed. Methalox is also appended to the KSPIE LF/Ox switcher (FL-C1000, MEMLander).
- Kerbal profession checks by the trait config name (`ProtoCrewMember.trait`), not the localized title: science module
  crew factor, ignitor reload, emitter replacement (KNOWN_ISSUES L10).
- Fission reactor fuel swap (`FNPlugin/Reactors/InterstellarFissionMSRGC.cs`, molten salt, liquid metal and the other
  `InterstellarFissionMSRGC` reactors): the new fuels were sized from the main fuel of the mode the reactor was loaded
  in, and `OnStart` skipped that setup when the mode had run out of a fuel, leaving its density at 0, so after a swap
  every fuel tank of the reactor was 0/0 for good (upstream #696, #678). A swap now keeps the fuel mass of the core;
  it is refused with a message while actinides are in the core or when fuel left in the core has no room in the
  vessel's tanks and the next mode would not hold it (also fuel of the next mode already in the core above its new
  capacity); on load, a mode with no fuel capacity switches to a mode the part has tanks for (KANDL defaults to a
  uranium mode but carries Pu-238), otherwise its capacity is restored without adding fuel.
  The fuel mode is restored by name, not by its index in the tech-dependent mode list (upstream #670).
- Background power with Kerbalism (`FNPlugin/Background/`, `GameData/WarpPlugin/Patches/BackgroundPower.cfg`): an
  unloaded vessel's reactors, generators and radiators run in Kerbalism's background step at their steady operating
  point instead of not at all. Upstream, an unloaded KSPIE vessel made no ElectricCharge (Kerbalism only saw its
  consumption) and the reactor billed all the fuel at once when the vessel was loaded again. The reactor, generator
  and radiator modules write what they computed while loaded into the save (`bg*` keys, flight only);
  `InterstellarBackgroundPower` (added to every reactor part, only with Kerbalism) finds the heat balance (radiator
  temperature, generator efficiency, minimum throttle) for the vessel's electric demand, burns the fuel, makes the
  products, breeds tritium and moves the reactor's own catch-up time forward. The electric demand of the rest of the
  vessel is measured from the battery between steps (held at 98%). The model (`Background/Core`) references no KSP
  assembly and is tested by `Background.Tests` (`dotnet test`). A vessel gets it after one load with this version.
  A reactor is run by the model completely or not at all: reactors whose power also goes where the model does not
  follow keep their own catch-up on load — fusion reactors (plasma upkeep power), reactors with an MHD generator or
  no generator (engines), every reactor of a vessel with a beamed power transmitter on, and fuel modes run by a
  Kerbalism process. The stock ModuleGenerator that FNGenerator switches on is switched off in the snapshot of a
  modelled generator's part: Kerbalism's own background made its config output for free (2000 EC/s on the liquid
  metal reactor) on top of the reactor. Not modelled: ISRU and other MJ consumers, convection in an atmosphere,
  radioactive decay of stored resources; a fuel or product kept inside the reactor (consumeGlobal / produceGlobal =
  false) stops the reactor or is dropped when the reactor's own store is empty or full, but Kerbalism takes and puts
  them in any tank of the vessel.
- `InterstellarReactor.OnStart`: the power slider's minimum is set only when the control is a slider. In game a
  molten salt reactor had `UI_Label` controls there (cause not found); the NullReferenceException aborted OnStart and
  the reactor threw on every FixedUpdate.
- `build.ps1 -Deploy -NoBuild`: install the last build only (used by the MCP tool restart_game). The deploy installs
  `GameData/WarpPlugin` and the two DLLs only; InterstellarFuelSwitch and PhotonSail in the game are the CKAN ones. It
  copies over the installed WarpPlugin and never deletes: a file removed or renamed in the fork stays in the game
  (e.g. `Parts/FuelTank/CrashPad`) until deleted by hand.
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
