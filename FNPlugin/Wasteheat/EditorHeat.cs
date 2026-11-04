using FNPlugin.Background.Core;
using FNPlugin.Beamedpower;
using FNPlugin.Powermanagement;
using FNPlugin.Propulsion;
using FNPlugin.Reactors;
using FNPlugin.Storage;
using KSP.Localization;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace FNPlugin.Wasteheat
{
    /// <summary>
    /// Fork (cooling step 5): the vessel in the editor as the background model sees a vessel in flight
    /// (Background/Core: VesselSpec, SteadyStateSolver) — the same reactors, generators and radiators, so the editor
    /// heat window (VABThermalUI) shows the operating point the vessel settles at in flight and on rails warp. The
    /// values are those the modules write for the model when saved in flight (InterstellarReactor and FNGenerator
    /// SaveBackgroundValues; FNRadiator.EditorSpec for a chosen place), every reactor taken as running. What the model
    /// leaves out is listed in Result.NotCounted — fusion reactors are estimated without their plasma upkeep (the
    /// background does not run them at all: their vessel stays on its own catch-up).
    /// </summary>
    internal static class EditorHeat
    {
        /// <summary>What stops the plant from giving more.</summary>
        internal enum Limit
        {
            /// <summary>
            /// The radiators shed no more here: the heat bar reaches the overheat threshold, or they are already at
            /// their temperature limit (an atmosphere caps it) so a fuller bar sheds no more (or not even the minimum
            /// power can be cooled).
            /// </summary>
            Radiators,
            /// <summary>The generators make all they can at that temperature: reactors or generators at full power.</summary>
            Plant,
            /// <summary>
            /// Past this power the radiators warm up and the generators lose efficiency faster than the power grows:
            /// no equilibrium is left below the overheat threshold, the loaded plant runs away (SteadyStateSolver
            /// delivers the largest balanced power, the game slides towards almost no electricity).
            /// </summary>
            Efficiency
        }

        internal sealed class Result
        {
            public VesselSpec Spec;
            /// <summary>The operating point at the chosen load, and at the most it can keep up.</summary>
            public SteadyState State, MaxState;
            /// <summary>
            /// The most the generators make at all (coldest radiators): the upper bound asked of the solver for the limit.
            /// Thermal generators never reach it (their efficiency falls as the radiators warm); charged particle ones can.
            /// </summary>
            public double MaximumElectricMW;
            /// <summary>The most it keeps up in equilibrium (the power limit), and what was asked: load × that.</summary>
            public double SustainableMW, RequestedElectricMW;
            /// <summary>What stops it at the power limit.</summary>
            public Limit MaxLimit;
            /// <summary>Reactor power ratio, weighted by reactor power.</summary>
            public double ReactorPowerRatio;
            /// <summary>Generator efficiency, weighted by the electricity each makes.</summary>
            public double GeneratorEfficiency;
            /// <summary>The vessel's average radiator temperature at the operating point, as the generators see it.</summary>
            public double RadiatorTemperatureK;
            public readonly SortedSet<string> NotCounted = new SortedSet<string>();
        }

        /// <summary>The vessel's cryogenic tanks, full: what cooling them costs and what they boil without it.</summary>
        internal sealed class Cryo
        {
            /// <summary>Cooling all of them, EC/s.</summary>
            public double CoolingEcPerSecond;
            /// <summary>Boiloff without cooling, units per hour, by resource display name.</summary>
            public readonly SortedDictionary<string, double> BoiloffPerHour = new SortedDictionary<string, double>();
            public int Tanks, TanksOff;
        }

        /// <summary>
        /// Full tanks under the player's rule: KSPIE cryostats (CryostatRule, per resource) and CryoTanks' ModuleCryoTank
        /// (as Kerbalism bills it, CryoTankResourceSim.EstimateCoolingEcRate: (module CoolingCost + Σ CoolingCost of the
        /// fuels present) × Σ their amounts / 1000, whatever their boiloff rate; CryoCooling's patch sets each fuel's
        /// CoolingCost to 1.4 × its BoiloffRate). Read from the part config by name, no reference to CryoTanks.
        /// </summary>
        public static Cryo EvaluateCryo(IList<Part> parts)
        {
            var cryo = new Cryo();
            foreach (var part in parts)
            {
                var anyCryostat = false;
                var cryostatOn = false;
                foreach (var cryostat in part.FindModulesImplementing<FNModuleCryostat>())
                {
                    var rate = cryostat.EditorRulePercentPerHour;
                    var resource = rate > 0 ? part.Resources.Get(cryostat.resourceName) : null;
                    if (resource == null || !(resource.maxAmount > 0)) continue;
                    anyCryostat = true;
                    cryostatOn |= !cryostat.isDisabled;
                    cryo.CoolingEcPerSecond += CryostatRule.CoolingEcRate(rate, resource.maxAmount);
                    AddBoiloff(cryo, resource, rate * 0.01 * resource.maxAmount);
                }
                if (anyCryostat)
                {
                    cryo.Tanks++;
                    if (!cryostatOn) cryo.TanksOff++;
                    continue;
                }

                var tank = part.Modules.Contains("ModuleCryoTank") ? part.Modules["ModuleCryoTank"] : null;
                var config = tank != null ? CryoTankConfig(part) : null;
                if (config == null) continue;
                float.TryParse(config.GetValue("CoolingCost"), NumberStyles.Float, CultureInfo.InvariantCulture, out var moduleCost);
                double costs = moduleCost, amount = 0;
                var present = false;
                foreach (var fuel in config.GetNodes("BOILOFFCONFIG"))
                {
                    var resource = part.Resources.Get(fuel.GetValue("FuelName") ?? "");
                    if (resource == null || !(resource.maxAmount > 0)) continue;
                    float.TryParse(fuel.GetValue("CoolingCost"), NumberStyles.Float, CultureInfo.InvariantCulture, out var cost);
                    float.TryParse(fuel.GetValue("BoiloffRate"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate);
                    present = true;
                    costs += cost;
                    amount += resource.maxAmount;
                    if (rate > 0)
                        AddBoiloff(cryo, resource, rate * 0.01 * resource.maxAmount);
                }
                if (!present) continue;
                cryo.CoolingEcPerSecond += costs > 0 ? costs * amount * 0.001 : 0;
                cryo.Tanks++;
                var enabled = tank.Fields["CoolingEnabled"];
                if (enabled == null)
                {
                    if (!_coolingFieldWarned)
                    {
                        _coolingFieldWarned = true;
                        UnityEngine.Debug.LogWarning("[KSPI]: EditorHeat: ModuleCryoTank has no CoolingEnabled field (CryoTanks changed?): tanks with cooling off are not counted");
                    }
                }
                else if (!(enabled.GetValue(tank) is bool on && on))
                    cryo.TanksOff++;
            }
            return cryo;
        }

        private static bool _coolingFieldWarned;

        private static void AddBoiloff(Cryo cryo, PartResource resource, double unitsPerHour)
        {
            var name = resource.info != null && !string.IsNullOrEmpty(resource.info.displayName) ? resource.info.displayName : resource.resourceName;
            cryo.BoiloffPerHour.TryGetValue(name, out var sum);
            cryo.BoiloffPerHour[name] = sum + unitsPerHour;
        }

        /// <summary>The part's first ModuleCryoTank config node (Kerbalism bills the first one too).</summary>
        private static ConfigNode CryoTankConfig(Part part)
        {
            var config = part.partInfo != null ? part.partInfo.partConfig : null;
            if (config == null) return null;
            foreach (var module in config.GetNodes("MODULE"))
                if (module.GetValue("name") == "ModuleCryoTank")
                    return module;
            return null;
        }

        /// <param name="body">Null for space.</param>
        /// <param name="altitude">Above sea level, m (used only in an atmosphere).</param>
        /// <param name="load">Share of the most the plant keeps up in equilibrium that is asked of it, 0..1.</param>
        public static Result Evaluate(IList<Part> parts, CelestialBody body, double altitude, double load)
        {
            var result = new Result();
            var spec = new VesselSpec { ExternalTemperatureK = PhysicsGlobals.SpaceTemperature };
            var reactorIndex = new Dictionary<IFNPowerSource, int>();

            foreach (var part in parts)
            {
                foreach (var reactor in part.FindModulesImplementing<InterstellarReactor>())
                {
                    // as InterstellarReactor.SaveBackgroundValues
                    var momentary = reactor.geeForceModifier * reactor.overheatModifier;
                    var thermal = momentary > 1e-6 ? reactor.MaximumThermalPower / momentary * (1 - reactor.lostThermalPowerRatio) : 0;
                    var charged = momentary > 1e-6 ? reactor.MaximumChargedPower / momentary * (1 - reactor.lostChargedPowerRatio) : 0;
                    if (!(thermal + charged > 0)) continue;
                    if (reactor.NeedsPlasmaUpkeep)
                        result.NotCounted.Add(Localizer.Format("#LOC_KSPIE_EditorHeat_PlasmaUpkeep"));

                    reactorIndex[reactor] = spec.Reactors.Count;
                    spec.Reactors.Add(new ReactorSpec
                    {
                        ThermalMW = thermal,
                        ChargedMW = charged,
                        HotBathK = reactor.HotBathTemperature,
                        MinimumThrottle = Math.Max(reactor.MinimumThrottle, reactor.ForcedMinimumThrottleRatio),
                        ChargedProducesWasteHeat = reactor.chargedPowerProducesWasteheat,
                        OverheatFill = reactor.hasOverheatEffects ? reactor.overheatThreshHold : 1
                    });
                }

                if (part.FindModuleImplementing<BeamedPowerReceiver>() != null)
                    result.NotCounted.Add(Localizer.Format("#LOC_KSPIE_EditorHeat_Receivers"));
                if (part.FindModuleImplementing<BeamedPowerTransmitter>() != null)
                    result.NotCounted.Add(Localizer.Format("#LOC_KSPIE_EditorHeat_Transmitters"));
                if (part.FindModuleImplementing<ThermalEngineController>() != null || part.FindModuleImplementing<InterstellarEngineController>() != null
                    || part.FindModuleImplementing<ElectricEngineControllerFX>() != null)
                    result.NotCounted.Add(Localizer.Format("#LOC_KSPIE_EditorHeat_Engines"));
            }

            var chargedGenerators = new HashSet<int>();
            foreach (var part in parts)
            {
                foreach (var generator in part.FindModulesImplementing<FNGenerator>())
                {
                    var source = generator.AttachedPowerSource;
                    if (!generator.IsEnabled)
                    {
                        result.NotCounted.Add(Localizer.Format("#LOC_KSPIE_EditorHeat_GeneratorsOff"));
                        continue;
                    }
                    if (source == null)
                    {
                        result.NotCounted.Add(Localizer.Format("#LOC_KSPIE_EditorHeat_GeneratorsUnattached"));
                        continue;
                    }
                    if (!reactorIndex.TryGetValue(source, out var index))
                    {
                        // a thermal beamed power receiver, or a reactor with no power
                        result.NotCounted.Add(Localizer.Format("#LOC_KSPIE_EditorHeat_GeneratorsOtherSource"));
                        continue;
                    }
                    // MHD runs on plasma temperature, which the model does not have (the background leaves such a reactor alone too)
                    if (generator.isMHD)
                    {
                        result.NotCounted.Add(Localizer.Format("#LOC_KSPIE_EditorHeat_Mhd"));
                        continue;
                    }

                    // as FNGenerator.SaveBackgroundValues
                    var chargedMode = generator.chargedParticleMode;
                    var usage = chargedMode ? source.ChargedParticleEnergyEfficiency : source.ThermalEnergyEfficiency;
                    if (chargedMode) chargedGenerators.Add(index);
                    spec.Generators.Add(new GeneratorSpec
                    {
                        Reactor = index,
                        Mode = chargedMode ? GeneratorMode.Charged : GeneratorMode.Thermal,
                        MaxEfficiency = generator.MaxEfficiency,
                        Share = generator.CapacityRatio * Math.Min(1, generator.PowerControlRatio) * usage,
                        LimitedToMinimumThrottle = generator.isLimitedByMinThrottle
                    });
                }
            }
            // a thermal generator also converts the charged power of a reactor that has no charged particle generator
            foreach (var generator in spec.Generators)
                generator.UsesChargedPower = generator.Mode == GeneratorMode.Thermal && !chargedGenerators.Contains(generator.Reactor);

            foreach (var part in parts)
                foreach (var radiator in part.FindModulesImplementing<FNRadiator>())
                {
                    var radiatorSpec = radiator.EditorSpec(body, altitude);
                    if (radiatorSpec != null)
                        spec.Radiators.Add(radiatorSpec);
                }

            result.Spec = spec;
            result.MaximumElectricMW = SteadyStateSolver.MaximumElectricMW(spec);
            var max = result.MaxState = SteadyStateSolver.Solve(spec, result.MaximumElectricMW);
            result.SustainableMW = max.Overheated ? 0 : max.ElectricMW;
            var threshold = 1.0;
            foreach (var reactor in spec.Reactors)
                if (reactor.OverheatFill > 0) threshold = Math.Min(threshold, reactor.OverheatFill);
            // radiators near their temperature cap (an atmosphere: 1200 K at sea level) shed a few % more at most: that
            // is the radiator limit (in space a fuller bar sheds tens of times more)
            var fullerBarShedsMore = SteadyStateSolver.Radiated(spec, threshold) > 1.10 * SteadyStateSolver.Radiated(spec, max.WasteHeatFill);
            result.MaxLimit = max.Overheated || max.HeatLimited || !fullerBarShedsMore ? Limit.Radiators
                : max.ElectricMW >= 0.98 * max.CapacityMW ? Limit.Plant
                : Limit.Efficiency;
            result.RequestedElectricMW = Math.Max(0, Math.Min(1, load)) * result.SustainableMW;
            var state = result.State = SteadyStateSolver.Solve(spec, result.RequestedElectricMW);

            double power = 0, ratio = 0;
            for (var i = 0; i < spec.Reactors.Count; i++)
            {
                power += spec.Reactors[i].TotalMW;
                ratio += spec.Reactors[i].TotalMW * state.ReactorPowerRatio[i];
            }
            result.ReactorPowerRatio = power > 0 ? ratio / power : 0;

            double electric = 0, efficiency = 0;
            for (var g = 0; g < spec.Generators.Count; g++)
            {
                electric += state.GeneratorElectricMW[g];
                efficiency += state.GeneratorElectricMW[g] * state.GeneratorEfficiency[g];
            }
            result.GeneratorEfficiency = electric > 0 ? efficiency / electric : 0;

            result.RadiatorTemperatureK = SteadyStateSolver.VesselRadiatorTemperature(spec, state.WasteHeatFill);
            return result;
        }
    }
}
