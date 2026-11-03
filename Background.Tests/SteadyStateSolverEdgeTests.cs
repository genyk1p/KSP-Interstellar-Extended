using System;
using System.Linq;
using FNPlugin.Background.Core;
using NUnit.Framework;

namespace Background.Tests
{
    /// <summary>Cases found by the review of 2026-11-03 (radiators hotter than the hot bath, NaN, overheating, limits).</summary>
    [TestFixture]
    public class SteadyStateSolverEdgeTests
    {
        private const double StefanBoltzmann = 5.670374e-8;

        private static VesselSpec Single(double thermalMW, double hotBathK, double radiatorAreaM2, double radiatorMaxK,
            double minimumThrottle = 0, double environmentMaxK = -1)
        {
            var spec = new VesselSpec();
            var reactor = new ReactorSpec { ThermalMW = thermalMW, HotBathK = hotBathK, MinimumThrottle = minimumThrottle };
            reactor.Fuels.Add(new ResourcePerMJ("Fuel", 1e-9));
            spec.Reactors.Add(reactor);
            spec.Generators.Add(new GeneratorSpec { Reactor = 0, Mode = GeneratorMode.Thermal, MaxEfficiency = 0.99 });
            spec.Radiators.Add(new RadiatorSpec
            {
                StefanArea = StefanBoltzmann * radiatorAreaM2 * 1e-6,
                MaxTemperatureK = radiatorMaxK,
                EnvironmentMaxK = environmentMaxK > 0 ? environmentMaxK : radiatorMaxK
            });
            return spec;
        }

        /// <summary>Properties every operating point must have.</summary>
        private static void AssertSane(VesselSpec spec, SteadyState state, double requested)
        {
            Assert.That(double.IsNaN(state.ElectricMW) || double.IsInfinity(state.ElectricMW), Is.False, "electric power finite");
            Assert.That(state.ElectricMW, Is.GreaterThanOrEqualTo(0));
            if (!double.IsNaN(requested) && !double.IsInfinity(requested))
                Assert.That(state.ElectricMW, Is.LessThanOrEqualTo(Math.Max(0, requested) * (1 + 1e-9) + 1e-12));
            Assert.That(state.ElectricMW, Is.EqualTo(state.GeneratorElectricMW.Sum()).Within(1e-9 * Math.Max(1, state.ElectricMW)));
            foreach (var ratio in state.ReactorPowerRatio)
            {
                Assert.That(double.IsNaN(ratio), Is.False, "reactor ratio finite");
                Assert.That(ratio, Is.InRange(0.0, 1.0));
            }
            Assert.That(double.IsNaN(state.WasteHeatFill), Is.False);
            foreach (var rate in SteadyStateSolver.ResourceRates(spec, state).Values)
                Assert.That(double.IsNaN(rate) || double.IsInfinity(rate), Is.False, "resource rates finite");
            if (!state.Overheated)
            {
                var radiated = SteadyStateSolver.Radiated(spec, state.WasteHeatFill);
                Assert.That(radiated, Is.EqualTo(state.WasteHeatMW).Within(1e-5 * Math.Max(1, state.WasteHeatMW)), "heat balance");
            }
        }

        [Test]
        public void RadiatorsHotterThanTheHotBath_UseTheLowerEquilibrium()
        {
            // review: 1000 MW, hot bath 2500 K, a 30 m² radiator rated 3200 K; peak ~21.7 MW near fill 0.72
            var spec = Single(1000, 2500, 30, 3200);
            var state = SteadyStateSolver.Solve(spec, 17.4);

            Assert.That(state.ElectricMW, Is.EqualTo(17.4).Within(1e-9));
            Assert.That(state.HeatLimited, Is.False);
            AssertSane(spec, state, 17.4);
        }

        [Test]
        public void RequestAboveThePeak_RunsAwayToTheOverheatThreshold()
        {
            var spec = Single(1000, 2500, 30, 3200);
            var state = SteadyStateSolver.Solve(spec, 40);

            Assert.That(state.HeatLimited, Is.True);
            Assert.That(state.ElectricMW, Is.LessThan(40));
            Assert.That(state.ElectricMW, Is.GreaterThan(0));
            AssertSane(spec, state, 40);
        }

        [Test]
        public void GeneratorNearItsEfficiencyLimit_MinimumHeatDoesNotVanish()
        {
            // review: hot bath 1600 K with 4500 K radiators — the generator's efficiency can reach 0
            var spec = Single(1000, 1600, 50, 4500, minimumThrottle: 0.1);
            foreach (var request in new[] { 0.0, 10, 100, 1000 })
            {
                var state = SteadyStateSolver.Solve(spec, request);
                AssertSane(spec, state, request);
                if (state.Overheated)
                    Assert.That(state.ReactorPowerRatio[0], Is.EqualTo(0));
                else
                    Assert.That(state.ReactorPowerRatio[0], Is.GreaterThanOrEqualTo(0.1 - 1e-12), "the minimum power is burnt");
            }
        }

        [Test]
        public void MinimumPowerCannotBeCooled_EmergencyShutdown()
        {
            var spec = Single(4500, 3000, 1, 1500, minimumThrottle: 0.1);
            var state = SteadyStateSolver.Solve(spec, 1000);

            Assert.That(state.Overheated, Is.True);
            Assert.That(state.HeatLimited, Is.True);
            Assert.That(state.ElectricMW, Is.EqualTo(0));
            Assert.That(state.ReactorPowerRatio[0], Is.EqualTo(0));
            Assert.That(SteadyStateSolver.ResourceRates(spec, state)["Fuel"], Is.EqualTo(0), "a shut down reactor burns nothing");
        }

        [Test]
        public void EnvironmentLimitedRadiator_IsHeatLimited()
        {
            var spec = Single(4500, 3000, 300, 1500, environmentMaxK: 1000);
            var state = SteadyStateSolver.Solve(spec, 2000);

            Assert.That(state.ElectricMW, Is.LessThan(2000));
            Assert.That(state.HeatLimited, Is.True);
            AssertSane(spec, state, 2000);
        }

        [Test]
        public void DisabledRadiator_ShedsHalf()
        {
            var spec = Single(1000, 3000, 100, 1500);
            var on = SteadyStateSolver.Radiated(spec, 0.5);
            spec.Radiators[0].Enabled = false;
            Assert.That(SteadyStateSolver.Radiated(spec, 0.5), Is.EqualTo(on / 2).Within(1e-12));
        }

        [TestCase("ThermalMW")]
        [TestCase("ChargedMW")]
        [TestCase("HotBathK")]
        [TestCase("MinimumThrottle")]
        [TestCase("MaxEfficiency")]
        [TestCase("Share")]
        [TestCase("StefanArea")]
        [TestCase("MaxTemperatureK")]
        [TestCase("EnvironmentMaxK")]
        [TestCase("ExternalK")]
        [TestCase("Requested")]
        public void NotANumberOrInfinity_GivesFiniteOutputs(string field)
        {
            foreach (var bad in new[] { double.NaN, double.PositiveInfinity, -5 })
            {
                var spec = Single(1000, 3000, 300, 1500, minimumThrottle: 0.05);
                spec.Reactors[0].ChargedMW = 200;
                var requested = 100.0;
                switch (field)
                {
                    case "ThermalMW": spec.Reactors[0].ThermalMW = bad; break;
                    case "ChargedMW": spec.Reactors[0].ChargedMW = bad; break;
                    case "HotBathK": spec.Reactors[0].HotBathK = bad; break;
                    case "MinimumThrottle": spec.Reactors[0].MinimumThrottle = bad; break;
                    case "MaxEfficiency": spec.Generators[0].MaxEfficiency = bad; break;
                    case "Share": spec.Generators[0].Share = bad; break;
                    case "StefanArea": spec.Radiators[0].StefanArea = bad; break;
                    case "MaxTemperatureK": spec.Radiators[0].MaxTemperatureK = bad; break;
                    case "EnvironmentMaxK": spec.Radiators[0].EnvironmentMaxK = bad; break;
                    case "ExternalK": spec.Radiators[0].ExternalK = bad; break;
                    case "Requested": requested = bad; break;
                }
                var state = SteadyStateSolver.Solve(spec, requested);
                AssertSane(spec, state, requested);
            }
        }

        [Test]
        public void HugeRequest_DeliversTheGeneratorsMaximum()
        {
            var spec = Single(1000, 3000, 1e5, 1500);
            var state = SteadyStateSolver.Solve(spec, 1e30);

            Assert.That(state.ElectricMW, Is.GreaterThan(0));
            Assert.That(state.ReactorPowerRatio[0], Is.EqualTo(1).Within(1e-6));
            AssertSane(spec, state, 1e30);
        }

        [Test]
        public void ReactorUsageRatio_CapsTheGenerator()
        {
            // antimatter reactor: a generator may use 1 % of the thermal power (ThermalEnergyEfficiency 0.01)
            var spec = Single(1000, 3000, 1e5, 1500);
            spec.Generators[0].Share = 0.01;
            var state = SteadyStateSolver.Solve(spec, 1000);

            Assert.That(state.ElectricMW, Is.LessThanOrEqualTo(0.01 * 1000 * 0.99 + 1e-9));
            Assert.That(state.ReactorPowerRatio[0], Is.LessThanOrEqualTo(0.01 + 1e-9));
        }

        [Test]
        public void EngineIntegratedGenerator_LimitedToTheMinimumPower()
        {
            var spec = Single(1000, 3000, 1e5, 1500, minimumThrottle: 0.05);
            spec.Generators[0].LimitedToMinimumThrottle = true;
            var state = SteadyStateSolver.Solve(spec, 1000);

            Assert.That(state.ReactorPowerRatio[0], Is.EqualTo(0.05).Within(1e-9));
            Assert.That(state.ElectricMW, Is.LessThanOrEqualTo(0.05 * 1000 + 1e-9));
        }

        [Test]
        public void ThermalAndChargedGeneratorsOnOneReactor_RatioIsTheLargerDraw()
        {
            var spec = new VesselSpec();
            spec.Reactors.Add(new ReactorSpec { ThermalMW = 300, ChargedMW = 700, HotBathK = 3000 });
            spec.Reactors[0].Fuels.Add(new ResourcePerMJ("Fuel", 1e-9));
            spec.Generators.Add(new GeneratorSpec { Reactor = 0, Mode = GeneratorMode.Thermal, MaxEfficiency = 0.6 });
            spec.Generators.Add(new GeneratorSpec { Reactor = 0, Mode = GeneratorMode.Charged, MaxEfficiency = 0.85 });
            spec.Radiators.Add(new RadiatorSpec { StefanArea = StefanBoltzmann * 1e4 * 1e-6, MaxTemperatureK = 1500, EnvironmentMaxK = 1500 });

            var state = SteadyStateSolver.Solve(spec, 300);
            AssertSane(spec, state, 300);

            var thermalDrawn = state.GeneratorElectricMW[0] / state.GeneratorEfficiency[0];
            var chargedDrawn = state.GeneratorElectricMW[1] / state.GeneratorEfficiency[1];
            Assert.That(state.ReactorPowerRatio[0], Is.EqualTo(Math.Max(thermalDrawn / 300, chargedDrawn / 700)).Within(1e-9));
            Assert.That(SteadyStateSolver.ResourceRates(spec, state)["Fuel"], Is.EqualTo(-state.ReactorPowerRatio[0] * 1000 * 1e-9).Within(1e-18));
        }

        [Test]
        public void TwoReactors_GeneratorsListedInOtherOrder_SharedFuelSums()
        {
            var spec = new VesselSpec();
            foreach (var power in new[] { 400.0, 800.0 })
            {
                var reactor = new ReactorSpec { ThermalMW = power, HotBathK = 3000, MinimumThrottle = 0.1 };
                reactor.Fuels.Add(new ResourcePerMJ("Fuel", 1e-9));
                spec.Reactors.Add(reactor);
            }
            spec.Generators.Add(new GeneratorSpec { Reactor = 1, Mode = GeneratorMode.Thermal, MaxEfficiency = 0.9 });
            spec.Generators.Add(new GeneratorSpec { Reactor = 0, Mode = GeneratorMode.Thermal, MaxEfficiency = 0.9 });
            spec.Radiators.Add(new RadiatorSpec { StefanArea = StefanBoltzmann * 1e4 * 1e-6, MaxTemperatureK = 1500, EnvironmentMaxK = 1500 });

            var state = SteadyStateSolver.Solve(spec, 300);
            AssertSane(spec, state, 300);

            var fuel = SteadyStateSolver.ResourceRates(spec, state)["Fuel"];
            var expected = -(state.ReactorPowerRatio[0] * 400 + state.ReactorPowerRatio[1] * 800) * 1e-9;
            Assert.That(fuel, Is.EqualTo(expected).Within(1e-18));
            Assert.That(state.ReactorPowerRatio[0], Is.GreaterThanOrEqualTo(0.1));
            Assert.That(state.ReactorPowerRatio[1], Is.GreaterThanOrEqualTo(0.1));
        }

        [Test]
        public void RandomVessels_Invariants()
        {
            var random = new Random(20261103);
            for (var i = 0; i < 800; i++)
            {
                var spec = new VesselSpec();
                var reactors = random.Next(1, 4);
                for (var r = 0; r < reactors; r++)
                {
                    var reactor = new ReactorSpec
                    {
                        ThermalMW = Math.Pow(10, random.NextDouble() * 4),
                        ChargedMW = random.NextDouble() < 0.4 ? Math.Pow(10, random.NextDouble() * 4) : 0,
                        HotBathK = 1200 + random.NextDouble() * 3500,
                        MinimumThrottle = random.NextDouble() < 0.5 ? random.NextDouble() * 0.2 : 0,
                        ChargedProducesWasteHeat = random.NextDouble() < 0.2
                    };
                    reactor.Fuels.Add(new ResourcePerMJ("Fuel", 1e-9));
                    spec.Reactors.Add(reactor);
                }
                var generators = random.Next(0, 4);
                for (var g = 0; g < generators; g++)
                {
                    var charged = random.NextDouble() < 0.25;
                    spec.Generators.Add(new GeneratorSpec
                    {
                        Reactor = random.Next(reactors),
                        Mode = charged ? GeneratorMode.Charged : GeneratorMode.Thermal,
                        MaxEfficiency = 0.3 + random.NextDouble() * 0.69,
                        Share = random.NextDouble() < 0.2 ? random.NextDouble() : 1,
                        UsesChargedPower = !charged && random.NextDouble() < 0.5
                    });
                }
                var radiators = random.Next(0, 4);
                for (var k = 0; k < radiators; k++)
                {
                    var max = 1200 + random.NextDouble() * 3300;
                    spec.Radiators.Add(new RadiatorSpec
                    {
                        StefanArea = StefanBoltzmann * Math.Pow(10, random.NextDouble() * 4) * 1e-6,
                        MaxTemperatureK = max,
                        EnvironmentMaxK = random.NextDouble() < 0.2 ? max * random.NextDouble() : max,
                        Mass = 0.5 + random.NextDouble() * 5,
                        Enabled = random.NextDouble() < 0.9
                    });
                }

                var request = Math.Pow(10, random.NextDouble() * 4) - 1;
                var state = SteadyStateSolver.Solve(spec, request);
                AssertSane(spec, state, request);

                // a smaller request that the plant can deliver is delivered in full
                if (!state.HeatLimited && !state.Overheated && state.ElectricMW > 0)
                {
                    var half = SteadyStateSolver.Solve(spec, state.ElectricMW / 2);
                    Assert.That(half.ElectricMW, Is.EqualTo(state.ElectricMW / 2).Within(1e-6 * state.ElectricMW), "case " + i);
                }
            }
        }

        [Test]
        public void ThermalAndChargedGenerators_EachDrawsItsChannelsMinimum()
        {
            // second review: the minimum power was split across both channels
            var spec = new VesselSpec();
            spec.Reactors.Add(new ReactorSpec { ThermalMW = 300, ChargedMW = 700, HotBathK = 3000, MinimumThrottle = 0.1 });
            spec.Generators.Add(new GeneratorSpec { Reactor = 0, Mode = GeneratorMode.Thermal, MaxEfficiency = 0.6 });
            spec.Generators.Add(new GeneratorSpec { Reactor = 0, Mode = GeneratorMode.Charged, MaxEfficiency = 0.85 });
            spec.Radiators.Add(new RadiatorSpec { StefanArea = StefanBoltzmann * 1e4 * 1e-6, MaxTemperatureK = 1500, EnvironmentMaxK = 1500 });

            var state = SteadyStateSolver.Solve(spec, 0);
            AssertSane(spec, state, 0);

            // thermal generator converts 30 MW (0.1 × 300), charged generator 70 MW (0.1 × 700): only losses are heat
            var expectedWaste = 30 * (1 - state.GeneratorEfficiency[0]) + 70 * (1 - 0.85);
            Assert.That(state.ReactorPowerRatio[0], Is.EqualTo(0.1).Within(1e-12));
            Assert.That(state.WasteHeatMW, Is.EqualTo(expectedWaste).Within(1e-6));
        }
    }
}
