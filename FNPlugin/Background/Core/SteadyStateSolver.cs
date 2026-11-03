using System;
using System.Collections.Generic;

namespace FNPlugin.Background.Core
{
    /// <summary>
    /// Finds the operating point a KSPIE power plant settles at: the waste heat fill fraction f at which the
    /// radiators shed exactly the heat the reactors and generators produce, for a given electric demand.
    /// Formulas follow the loaded KSPIE code (2026-11-03, fork after 8470e204):
    /// - radiator temperature  T = min(MaxTemperature × f^0.75, EnvironmentMax)          (FNRadiator, WasteHeatResourceManager)
    /// - radiated power        Q = (1 − (1 − f)^400) × (T − T_ext)^4 × StefanArea (× 0.5 when disabled)
    /// - vessel radiator temp  Σ min(1, T/MaxTemperature) × max(MaxTemperature) × mass share (FNRadiator.GetAverageRadiatorTemperatureForVessel)
    /// - thermal generator     η = MaxEfficiency × clamp(1 − 0.75 × T_vessel / HotBath)    (FNGenerator.OnFixedUpdateResourceSuppliable;
    ///                         KSPIE stops a generator below a ratio of 0.01 — the model lets η go to 0 smoothly instead,
    ///                         so that the minimum power's heat does not jump away)
    /// - reactor               thermal and charged power both run at the same ratio ρ; fuel is burnt for
    ///                         ρ × (thermal + charged) even if nothing uses the charged part (InterstellarReactor)
    /// - minimum throttle      a reactor runs at least at MinimumThrottle only if it has a generator: the generators
    ///                         draw the reactor's minimum power between them and convert it (FNGenerator.OnPostResourceSuppliable);
    ///                         electricity nobody needs is lost, the converted part is not waste heat
    /// - waste heat            ρ·Thermal (+ ρ·Charged if ChargedProducesWasteHeat) − thermal drawn × η + charged drawn × (1 − η)
    /// Waste heat produced also grows with f (η falls), so radiated − produced can have two roots: the lower one is
    /// where the loaded plant settles (it heats up from cold); above the upper one it runs away to the overheat
    /// threshold. The search therefore scans f upward and takes the first crossing.
    /// Transients (warm-up, overheat shutdown timing) are not modelled: they last seconds to minutes, the model
    /// runs for hours to days of warp.
    /// </summary>
    public static class SteadyStateSolver
    {
        private const int Grid = 32;
        private const int Iterations = 60;

        private enum Outcome { Balanced, CapacityLimited, HeatLimited }

        /// <summary>Inputs cleaned of NaN, infinities and negatives, plus scratch arrays reused by every evaluation.</summary>
        private sealed class Work
        {
            public readonly int ReactorCount, GeneratorCount, RadiatorCount;
            public readonly double[] Thermal, Charged, HotBath, MinThrottle, ChargedWaste;
            public readonly int[] GeneratorReactor;
            public readonly bool[] GeneratorCharged, GeneratorUsesCharged;
            public readonly double[] MaxEfficiency, InputCapacity, MinimumInput;
            public readonly double[] StefanArea, MaxTemperature, EnvironmentMax, External, MassShare, RadiatorFactor;
            public readonly double VesselMaxTemperature;
            public readonly double[] Capacity, ThermalDrawn, ChargedDrawn;
            public readonly bool[] HasGenerator;
            public double TotalCapacity;

            public Work(VesselSpec spec)
            {
                ReactorCount = spec.Reactors.Count;
                GeneratorCount = spec.Generators.Count;
                RadiatorCount = spec.Radiators.Count;

                Thermal = new double[ReactorCount];
                Charged = new double[ReactorCount];
                HotBath = new double[ReactorCount];
                MinThrottle = new double[ReactorCount];
                ChargedWaste = new double[ReactorCount];
                for (var r = 0; r < ReactorCount; r++)
                {
                    var reactor = spec.Reactors[r];
                    Thermal[r] = NonNegative(reactor.ThermalMW);
                    Charged[r] = NonNegative(reactor.ChargedMW);
                    HotBath[r] = NonNegative(reactor.HotBathK);
                    MinThrottle[r] = Math.Min(1, NonNegative(reactor.MinimumThrottle));
                    ChargedWaste[r] = reactor.ChargedProducesWasteHeat ? 1 : 0;
                }

                GeneratorReactor = new int[GeneratorCount];
                GeneratorCharged = new bool[GeneratorCount];
                GeneratorUsesCharged = new bool[GeneratorCount];
                MaxEfficiency = new double[GeneratorCount];
                InputCapacity = new double[GeneratorCount];
                MinimumInput = new double[GeneratorCount];
                var shares = new double[GeneratorCount];
                // a thermal generator takes the charged power only when the reactor has no charged particle generator
                // (FNGenerator: UsesChargedPower is written from !IsConnectedToChargedGenerator) — enforced here too,
                // so that two generators never both take the same charged power
                var hasChargedGenerator = new bool[ReactorCount];
                foreach (var generator in spec.Generators)
                    if (generator.Mode == GeneratorMode.Charged && generator.Reactor >= 0 && generator.Reactor < ReactorCount)
                        hasChargedGenerator[generator.Reactor] = true;
                for (var g = 0; g < GeneratorCount; g++)
                {
                    var generator = spec.Generators[g];
                    var r = generator.Reactor;
                    GeneratorReactor[g] = r >= 0 && r < ReactorCount ? r : -1;
                    if (GeneratorReactor[g] < 0) continue;
                    GeneratorCharged[g] = generator.Mode == GeneratorMode.Charged;
                    GeneratorUsesCharged[g] = !GeneratorCharged[g] && generator.UsesChargedPower && !hasChargedGenerator[r];
                    MaxEfficiency[g] = Math.Min(1, NonNegative(generator.MaxEfficiency));
                    shares[g] = NonNegative(generator.Share);

                    var power = GeneratorCharged[g] ? Charged[r] : Thermal[r] + (GeneratorUsesCharged[g] ? Charged[r] : 0);
                    InputCapacity[g] = shares[g] * power;
                    if (generator.LimitedToMinimumThrottle)
                        InputCapacity[g] = Math.Min(InputCapacity[g], MinThrottle[r] * power);
                }
                // Generators share their reactor: together they never draw more than it makes (two generators with
                // a share of 1 each get half each). Thermal and charged power are shared separately.
                var thermalShares = new double[ReactorCount];
                var chargedShares = new double[ReactorCount];
                for (var g = 0; g < GeneratorCount; g++)
                {
                    var r = GeneratorReactor[g];
                    if (r < 0) continue;
                    if (GeneratorCharged[g]) chargedShares[r] += shares[g];
                    else thermalShares[r] += shares[g];
                }
                for (var g = 0; g < GeneratorCount; g++)
                {
                    var r = GeneratorReactor[g];
                    if (r < 0) continue;
                    var sum = GeneratorCharged[g] ? chargedShares[r] : thermalShares[r];
                    if (sum <= 0) continue;
                    if (sum > 1)
                        InputCapacity[g] /= sum;
                    var power = GeneratorCharged[g] ? Charged[r] : Thermal[r] + (GeneratorUsesCharged[g] ? Charged[r] : 0);
                    // the minimum power of the generator's channel (thermal or charged), split between the generators
                    // on that channel: each FNGenerator asks for its whole channel's minimum (OnPostResourceSuppliable)
                    MinimumInput[g] = Math.Min(InputCapacity[g], MinThrottle[r] * power * shares[g] / sum);
                }

                StefanArea = new double[RadiatorCount];
                MaxTemperature = new double[RadiatorCount];
                EnvironmentMax = new double[RadiatorCount];
                External = new double[RadiatorCount];
                MassShare = new double[RadiatorCount];
                RadiatorFactor = new double[RadiatorCount];
                var totalMass = 0.0;
                var defaultExternal = NonNegative(spec.ExternalTemperatureK);
                for (var k = 0; k < RadiatorCount; k++)
                {
                    var radiator = spec.Radiators[k];
                    StefanArea[k] = NonNegative(radiator.StefanArea);
                    MaxTemperature[k] = NonNegative(radiator.MaxTemperatureK);
                    EnvironmentMax[k] = NonNegative(radiator.EnvironmentMaxK);
                    var external = NonNegative(radiator.ExternalK);
                    External[k] = external > 0 ? external : defaultExternal;
                    MassShare[k] = NonNegative(radiator.Mass);
                    RadiatorFactor[k] = radiator.Enabled ? 1 : 0.5;
                    totalMass += MassShare[k];
                    VesselMaxTemperature = Math.Max(VesselMaxTemperature, MaxTemperature[k]);
                }
                for (var k = 0; k < RadiatorCount; k++)
                    MassShare[k] = totalMass > 0 ? MassShare[k] / totalMass : 0;

                Capacity = new double[GeneratorCount];
                ThermalDrawn = new double[ReactorCount];
                ChargedDrawn = new double[ReactorCount];
                HasGenerator = new bool[ReactorCount];
            }
        }

        private static double NonNegative(double value) => double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? 0 : value;

        public static SteadyState Solve(VesselSpec spec, double requestedElectricMW)
        {
            if (spec == null) throw new ArgumentNullException(nameof(spec));

            var work = new Work(spec);
            var requested = NonNegative(requestedElectricMW);
            var state = NewState(spec, requested);
            var maxFill = MaximumFill(spec);

            // KSPIE generators make nothing on a vessel without radiators, and without a generator nothing asks
            // a reactor for power
            if (work.RadiatorCount == 0 || work.GeneratorCount == 0)
                return state;

            // never ask for more than the generators can make at all (coldest radiators, highest efficiency)
            Evaluate(work, 0, 0, state);
            requested = Math.Min(requested, work.TotalCapacity);

            // the reactors' minimum power alone cannot be cooled: the model stops them (see SteadyState.Overheated)
            if (Find(work, 0, maxFill, state) != Outcome.Balanced)
            {
                Shutdown(state);
                state.Overheated = true;
                state.HeatLimited = requested > 0;
                state.WasteHeatFill = maxFill;
                return state;
            }

            if (Find(work, requested, maxFill, state) == Outcome.Balanced)
                return state;

            // the largest power that still has an equilibrium below the overheat threshold
            double low = 0, high = requested;
            for (var i = 0; i < Iterations; i++)
            {
                var mid = (low + high) / 2;
                if (Find(work, mid, maxFill, state) == Outcome.Balanced) low = mid; else high = mid;
            }

            double delivered;
            var heatLimited = Find(work, high, maxFill, state) == Outcome.HeatLimited;
            if (heatLimited)
            {
                // Past that power the plant heats up to the overheat threshold, where the reactors throttle
                // themselves: what is delivered is the power whose heat the radiators shed at that fill.
                double hotLow = 0, hotHigh = requested;
                for (var i = 0; i < Iterations; i++)
                {
                    var mid = (hotLow + hotHigh) / 2;
                    if (Evaluate(work, mid, maxFill, state) && Radiated(work, maxFill) - state.WasteHeatMW >= 0) hotLow = mid; else hotHigh = mid;
                }
                delivered = hotLow;
            }
            else
                delivered = low; // the generators' capacity is the limit

            if (Find(work, delivered, maxFill, state) != Outcome.Balanced)
            {
                Evaluate(work, delivered, maxFill, state);
                state.WasteHeatFill = maxFill;
            }
            state.RequestedElectricMW = NonNegative(requestedElectricMW);
            state.HeatLimited = heatLimited;
            return state;
        }

        /// <summary>Units per second of every fuel (negative) and product (positive) at the given operating point (breeding excluded).</summary>
        public static Dictionary<string, double> ResourceRates(VesselSpec spec, SteadyState state)
        {
            var rates = new Dictionary<string, double>();
            for (var r = 0; r < spec.Reactors.Count; r++)
            {
                var megajoulesPerSecond = ReactorMegajoulesPerSecond(spec, state, r);
                foreach (var fuel in spec.Reactors[r].Fuels)
                    Add(rates, fuel.Resource, -megajoulesPerSecond * NonNegative(fuel.UnitsPerMJ));
                foreach (var product in spec.Reactors[r].Products)
                    Add(rates, product.Resource, megajoulesPerSecond * NonNegative(product.UnitsPerMJ));
            }
            return rates;
        }

        /// <summary>Total power (thermal + charged) the reactor runs at, MW = MJ/s: what its fuel is burnt for.</summary>
        public static double ReactorMegajoulesPerSecond(VesselSpec spec, SteadyState state, int reactor)
        {
            var spec1 = spec.Reactors[reactor];
            return state.ReactorPowerRatio[reactor] * (NonNegative(spec1.ThermalMW) + NonNegative(spec1.ChargedMW));
        }

        private static void Add(Dictionary<string, double> rates, string resource, double rate)
        {
            rates.TryGetValue(resource, out var current);
            rates[resource] = current + rate;
        }

        private static SteadyState NewState(VesselSpec spec, double requested)
        {
            return new SteadyState
            {
                RequestedElectricMW = requested,
                ReactorPowerRatio = new double[spec.Reactors.Count],
                GeneratorElectricMW = new double[spec.Generators.Count],
                GeneratorEfficiency = new double[spec.Generators.Count]
            };
        }

        private static void Shutdown(SteadyState state)
        {
            state.ElectricMW = 0;
            state.WasteHeatMW = 0;
            Array.Clear(state.ReactorPowerRatio, 0, state.ReactorPowerRatio.Length);
            Array.Clear(state.GeneratorElectricMW, 0, state.GeneratorElectricMW.Length);
        }

        private static double MaximumFill(VesselSpec spec)
        {
            var fill = 1.0;
            foreach (var reactor in spec.Reactors)
            {
                var threshold = reactor.OverheatFill;
                if (!double.IsNaN(threshold) && threshold > 0)
                    fill = Math.Min(fill, threshold);
            }
            return Math.Max(0.01, fill);
        }

        /// <summary>
        /// The lowest fill at which the radiators shed the heat of <paramref name="electricMW"/> (state set there),
        /// or why there is none: the generators cannot make the power before the heat balances, or the heat still
        /// builds up at the overheat threshold.
        /// </summary>
        private static Outcome Find(Work work, double electricMW, double maxFill, SteadyState state)
        {
            if (!Evaluate(work, electricMW, 0, state))
                return Outcome.CapacityLimited;
            if (Radiated(work, 0) - state.WasteHeatMW >= 0)
            {
                state.WasteHeatFill = 0;
                return Outcome.Balanced;
            }

            var previous = 0.0;
            for (var i = 1; i <= Grid; i++)
            {
                var fill = maxFill * i / Grid;
                if (Evaluate(work, electricMW, fill, state) && Radiated(work, fill) - state.WasteHeatMW < 0)
                {
                    previous = fill; // heat still building up
                    continue;
                }

                // first crossing (balanced, or the generators cannot deliver any more) lies in (previous, fill]
                double low = previous, high = fill;
                for (var j = 0; j < Iterations; j++)
                {
                    var mid = (low + high) / 2;
                    if (!Evaluate(work, electricMW, mid, state) || Radiated(work, mid) - state.WasteHeatMW >= 0) high = mid; else low = mid;
                }
                if (!Evaluate(work, electricMW, high, state))
                    return Outcome.CapacityLimited;
                state.WasteHeatFill = high;
                return Outcome.Balanced;
            }
            return Outcome.HeatLimited;
        }

        /// <summary>
        /// Operating point at a given fill fraction: generator efficiencies, power split, reactor ratios, waste heat.
        /// False when the generators cannot deliver <paramref name="electricMW"/> at this temperature.
        /// </summary>
        private static bool Evaluate(Work work, double electricMW, double fill, SteadyState state)
        {
            var coldBath = VesselRadiatorTemperature(work, fill);
            work.TotalCapacity = 0;

            for (var g = 0; g < work.GeneratorCount; g++)
            {
                var r = work.GeneratorReactor[g];
                double efficiency = 0;
                if (r >= 0)
                {
                    efficiency = work.GeneratorCharged[g]
                        ? work.MaxEfficiency[g]
                        : work.HotBath[r] > 0 ? work.MaxEfficiency[g] * Math.Max(0, Math.Min(1, 1 - 0.75 * coldBath / work.HotBath[r])) : 0;
                }
                state.GeneratorEfficiency[g] = efficiency;
                work.Capacity[g] = efficiency * work.InputCapacity[g];
                work.TotalCapacity += work.Capacity[g];
            }

            if (electricMW > work.TotalCapacity * (1 + 1e-12))
                return false;

            Array.Clear(work.ThermalDrawn, 0, work.ReactorCount);
            Array.Clear(work.ChargedDrawn, 0, work.ReactorCount);
            Array.Clear(work.HasGenerator, 0, work.ReactorCount);
            var waste = 0.0;

            for (var g = 0; g < work.GeneratorCount; g++)
            {
                var r = work.GeneratorReactor[g];
                state.GeneratorElectricMW[g] = 0;
                if (r < 0 || work.InputCapacity[g] <= 0) continue;
                work.HasGenerator[r] = true;

                // demand split by capacity: generator g makes E × capacity_g / Σcapacity, i.e. it draws
                // E × input capacity_g / Σcapacity — written without dividing by its efficiency, so a generator whose
                // efficiency falls to 0 fades out smoothly instead of its heat vanishing in one jump
                var efficiency = state.GeneratorEfficiency[g];
                var demandInput = work.TotalCapacity > 0 ? electricMW * work.InputCapacity[g] / work.TotalCapacity : 0;
                state.GeneratorElectricMW[g] = efficiency * demandInput;

                var input = Math.Max(demandInput, work.MinimumInput[g]);
                if (input <= 0) continue;

                double thermal, charged;
                if (work.GeneratorCharged[g])
                {
                    thermal = 0;
                    charged = input;
                }
                else if (work.GeneratorUsesCharged[g] && work.Thermal[r] + work.Charged[r] > 0)
                {
                    // drawn in the reactor's own thermal : charged proportion (FNGenerator plans thermal by ThermalPowerRatio)
                    var total = work.Thermal[r] + work.Charged[r];
                    thermal = input * work.Thermal[r] / total;
                    charged = input * work.Charged[r] / total;
                }
                else
                {
                    thermal = input;
                    charged = 0;
                }

                work.ThermalDrawn[r] += thermal;
                work.ChargedDrawn[r] += charged;
                waste += work.GeneratorCharged[g]
                    ? charged * (1 - efficiency)
                    : -thermal * efficiency + charged * (1 - efficiency);
            }

            for (var r = 0; r < work.ReactorCount; r++)
            {
                // without a generator nothing asks for the minimum power: the reactor idles
                var ratio = work.HasGenerator[r] ? work.MinThrottle[r] : 0;
                if (work.Thermal[r] > 0) ratio = Math.Max(ratio, work.ThermalDrawn[r] / work.Thermal[r]);
                if (work.Charged[r] > 0) ratio = Math.Max(ratio, work.ChargedDrawn[r] / work.Charged[r]);
                ratio = Math.Min(1, ratio);
                state.ReactorPowerRatio[r] = ratio;
                waste += ratio * work.Thermal[r] + work.ChargedWaste[r] * ratio * work.Charged[r];
            }

            state.ElectricMW = electricMW;
            state.WasteHeatMW = Math.Max(0, waste);
            return true;
        }

        private static double RadiatorTemperature(Work work, int k, double fill)
        {
            return Math.Min(work.MaxTemperature[k] * Math.Pow(Math.Max(0, fill), 0.75), work.EnvironmentMax[k]);
        }

        private static double VesselRadiatorTemperature(Work work, double fill)
        {
            var temperature = 0.0;
            for (var k = 0; k < work.RadiatorCount; k++)
            {
                if (work.MaxTemperature[k] <= 0) continue;
                var ratio = Math.Min(1, RadiatorTemperature(work, k, fill) / work.MaxTemperature[k]);
                temperature += ratio * work.VesselMaxTemperature * work.MassShare[k];
            }
            return temperature;
        }

        private static double Radiated(Work work, double fill)
        {
            var radiatorEfficiency = 1 - Math.Pow(1 - Math.Max(0, Math.Min(1, fill)), 400);
            var total = 0.0;
            for (var k = 0; k < work.RadiatorCount; k++)
            {
                var deltaT = Math.Max(0, RadiatorTemperature(work, k, fill) - work.External[k]);
                total += radiatorEfficiency * deltaT * deltaT * deltaT * deltaT * work.StefanArea[k] * work.RadiatorFactor[k];
            }
            return total;
        }

        /// <summary>FNRadiator.GetAverageRadiatorTemperatureForVessel at fill <paramref name="fill"/>.</summary>
        public static double VesselRadiatorTemperature(VesselSpec spec, double fill) => VesselRadiatorTemperature(new Work(spec), fill);

        /// <summary>Heat all radiators shed at fill <paramref name="fill"/>, MW.</summary>
        public static double Radiated(VesselSpec spec, double fill) => Radiated(new Work(spec), fill);
    }
}
