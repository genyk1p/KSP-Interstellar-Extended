using System.Collections.Generic;

// Steady-state model of the KSPIE power plant of an unloaded vessel (task todo/kspie-rails-warp.md, option A).
// Files under Background/Core reference no KSP or Unity assembly: they are compiled into Interstellar.dll and,
// linked, into the test project Background.Tests (dotnet test), so the model is checked without the game.
// The numbers here come from what the KSPIE part modules computed themselves while loaded (upgrades, TweakScale,
// the player's sliders and the subclasses' own power factors applied) and wrote into the save (see
// Background/BackgroundSnapshot.cs); the model combines them into the vessel's operating point.
namespace FNPlugin.Background.Core
{
    /// <summary>Amount of a real resource per MJ of total reactor energy (fuel consumed or product made).</summary>
    public sealed class ResourcePerMJ
    {
        public string Resource;
        public double UnitsPerMJ;

        public ResourcePerMJ(string resource, double unitsPerMJ)
        {
            Resource = resource;
            UnitsPerMJ = unitsPerMJ;
        }
    }

    public sealed class ReactorSpec
    {
        /// <summary>
        /// Maximum thermal power, MW: InterstellarReactor.MaximumThermalPower (power slider, subclass factors such as
        /// the pebble bed's thermal ratio efficiency or the molten salt reactor's actinide poisoning) with the
        /// momentary gee force and overheat modifiers divided out.
        /// </summary>
        public double ThermalMW;
        /// <summary>Maximum charged particle power, MW (InterstellarReactor.MaximumChargedPower, same treatment).</summary>
        public double ChargedMW;
        /// <summary>Hot bath temperature of thermal generators fed by this reactor, K (InterstellarReactor.HotBathTemperature).</summary>
        public double HotBathK;
        /// <summary>
        /// Minimum power ratio, 0..1 (max of MinimumThrottle and the forced minimum). Only a reactor with a working
        /// generator runs at it: the generator asks for the minimum power (FNGenerator "post" request) and converts it.
        /// </summary>
        public double MinimumThrottle;
        /// <summary>Charged particle power also becomes waste heat in the reactor (chargedPowerProducesWasteheat).</summary>
        public bool ChargedProducesWasteHeat;
        /// <summary>Fill fraction of the vessel waste heat bar at which the reactor starts to throttle (overheatThreshHold).</summary>
        public double OverheatFill = 0.95;
        /// <summary>Fuels and fission products per MJ of total power (ρ × (thermal + charged)).</summary>
        public readonly List<ResourcePerMJ> Fuels = new List<ResourcePerMJ>();
        public readonly List<ResourcePerMJ> Products = new List<ResourcePerMJ>();
        /// <summary>
        /// Tritium breeding per MJ of total power: lithium in, tritium and helium out (InterstellarReactor.BreedTritium,
        /// thermal neutrons). Kept apart from the fuels: it stops on its own when the lithium or the tritium room runs out,
        /// without stopping the reactor.
        /// </summary>
        public readonly List<ResourcePerMJ> BreedingInputs = new List<ResourcePerMJ>();
        public readonly List<ResourcePerMJ> BreedingOutputs = new List<ResourcePerMJ>();

        public double TotalMW => ThermalMW + ChargedMW;
    }

    public enum GeneratorMode
    {
        /// <summary>Heat engine: efficiency drops as the radiators get hotter (FNGenerator, not charged particle mode).</summary>
        Thermal,
        /// <summary>Direct conversion of charged particles at a fixed efficiency (FNGenerator.chargedParticleMode).</summary>
        Charged
    }

    public sealed class GeneratorSpec
    {
        /// <summary>Index of the reactor in VesselSpec.Reactors the generator is attached to.</summary>
        public int Reactor;
        public GeneratorMode Mode;
        /// <summary>FNGenerator.maxEfficiency (upgrades applied).</summary>
        public double MaxEfficiency;
        /// <summary>
        /// Fraction of the reactor power this generator may draw: CapacityRatio × min(1, PowerControlRatio) × the
        /// reactor's usage ratio for this kind of generator (ThermalEnergyEfficiency or ChargedParticleEnergyEfficiency;
        /// 1 for fission, 0.01 for antimatter thermal power).
        /// </summary>
        public double Share = 1;
        /// <summary>
        /// A thermal generator also converts the reactor's charged particle power when the reactor has no charged
        /// particle generator (checked in game on Lazer1: a thermoelectric generator received 742 MW thermal and
        /// 3758 MW charged of a 4500 MW pebble bed reactor).
        /// </summary>
        public bool UsesChargedPower;
        /// <summary>An engine's integrated generator: never draws more than the reactor's minimum power (FNGenerator.isLimitedByMinThrottle).</summary>
        public bool LimitedToMinimumThrottle;
    }

    public sealed class RadiatorSpec
    {
        /// <summary>Stefan-Boltzmann constant × effective area × 1e-6, MW/K⁴ (FNRadiator._stefanArea).</summary>
        public double StefanArea;
        /// <summary>FNRadiator.MaxRadiatorTemperature, K.</summary>
        public double MaxTemperatureK;
        /// <summary>Limit set by the environment (FNRadiator.maxCurrentRadiatorTemperature), K.</summary>
        public double EnvironmentMaxK;
        /// <summary>
        /// Temperature the radiator radiates against, K: max(atmosphere temperature × min(1, √density), space temperature)
        /// as FNRadiator uses it; 0 = VesselSpec.ExternalTemperatureK. Convection in an atmosphere is not modelled.
        /// </summary>
        public double ExternalK;
        /// <summary>Part mass, t: weight of the radiator in the vessel's average radiator temperature.</summary>
        public double Mass = 1;
        /// <summary>A retracted or disabled radiator dissipates half (FNRadiator: radiatorIsEnabled false).</summary>
        public bool Enabled = true;
    }

    public sealed class VesselSpec
    {
        public readonly List<ReactorSpec> Reactors = new List<ReactorSpec>();
        public readonly List<GeneratorSpec> Generators = new List<GeneratorSpec>();
        public readonly List<RadiatorSpec> Radiators = new List<RadiatorSpec>();
        /// <summary>Temperature a radiator without its own ExternalK radiates against, K (PhysicsGlobals.SpaceTemperature).</summary>
        public double ExternalTemperatureK = 4;
    }

    /// <summary>Operating point the vessel settles at for a given electric demand.</summary>
    public sealed class SteadyState
    {
        /// <summary>Electric power delivered, MW (×1000 = ElectricCharge per second).</summary>
        public double ElectricMW;
        /// <summary>Electric power asked for, MW.</summary>
        public double RequestedElectricMW;
        /// <summary>Fill fraction of the waste heat bar at equilibrium.</summary>
        public double WasteHeatFill;
        /// <summary>Waste heat produced (= radiated at equilibrium), MW.</summary>
        public double WasteHeatMW;
        /// <summary>
        /// Less than requested is delivered because the radiators cannot shed more heat: the plant runs at the
        /// overheat threshold (in the game the reactor's overheat modifier throttles it there, down to 75 %).
        /// </summary>
        public bool HeatLimited;
        /// <summary>
        /// Even the reactors' minimum power cannot be cooled. In the game the reactor keeps running hot (overheat
        /// modifier ≥ 0.75 at a full waste heat bar) until the radiators explode; the model stops the reactors
        /// instead (conservative): nothing runs, nothing is burnt, nothing is delivered.
        /// </summary>
        public bool Overheated;
        /// <summary>Power ratio of every reactor, 0..1 (same order as VesselSpec.Reactors).</summary>
        public double[] ReactorPowerRatio;
        /// <summary>Electric power of every generator, MW (same order as VesselSpec.Generators).</summary>
        public double[] GeneratorElectricMW;
        /// <summary>Efficiency of every generator at equilibrium.</summary>
        public double[] GeneratorEfficiency;
    }
}
