using System;
using FNPlugin.Background.Core;
using NUnit.Framework;

namespace Background.Tests
{
    [TestFixture]
    public class SteadyStateSolverTests
    {
        private const double StefanBoltzmann = 5.670374e-8;

        /// <summary>A pebble bed reactor like Lazer1's: 742.5 MW thermal + 3757.5 MW charged, one thermal generator using both.</summary>
        private static VesselSpec PebbleBed(double radiatorAreaM2, double minimumThrottle = 0)
        {
            var spec = new VesselSpec();
            var reactor = new ReactorSpec
            {
                ThermalMW = 742.5,
                ChargedMW = 3757.5,
                HotBathK = 3000,
                MinimumThrottle = minimumThrottle
            };
            reactor.Fuels.Add(new ResourcePerMJ("UraniumNitride", 2.36e-8));
            reactor.Products.Add(new ResourcePerMJ("DepletedFuel", 2.0e-8));
            spec.Reactors.Add(reactor);
            spec.Generators.Add(new GeneratorSpec { Reactor = 0, Mode = GeneratorMode.Thermal, MaxEfficiency = 0.99, UsesChargedPower = true });
            spec.Radiators.Add(new RadiatorSpec
            {
                StefanArea = StefanBoltzmann * radiatorAreaM2 * 1e-6,
                MaxTemperatureK = 1500,
                EnvironmentMaxK = 1500,
                Mass = 5
            });
            return spec;
        }

        private static void AssertHeatBalance(VesselSpec spec, SteadyState state)
        {
            var radiated = SteadyStateSolver.Radiated(spec, state.WasteHeatFill);
            Assert.That(radiated, Is.EqualTo(state.WasteHeatMW).Within(1e-6 * Math.Max(1, state.WasteHeatMW) + 1e-9),
                "radiators shed the produced heat at the equilibrium fill");
        }

        [Test]
        public void NoDemandAndNoMinimumThrottle_ReactorIdle()
        {
            var spec = PebbleBed(radiatorAreaM2: 5000);
            var state = SteadyStateSolver.Solve(spec, 0);

            Assert.That(state.ElectricMW, Is.EqualTo(0));
            Assert.That(state.ReactorPowerRatio[0], Is.EqualTo(0));
            Assert.That(state.WasteHeatMW, Is.EqualTo(0).Within(1e-9));
            Assert.That(state.HeatLimited, Is.False);
            Assert.That(SteadyStateSolver.ResourceRates(spec, state)["UraniumNitride"], Is.EqualTo(0).Within(1e-15));
        }

        [Test]
        public void DemandWithinCapacity_IsDeliveredAndHeatBalances()
        {
            var spec = PebbleBed(radiatorAreaM2: 5000);
            var state = SteadyStateSolver.Solve(spec, 1000);

            Assert.That(state.ElectricMW, Is.EqualTo(1000).Within(1e-9));
            Assert.That(state.HeatLimited, Is.False);
            AssertHeatBalance(spec, state);

            // the generator takes thermal and charged power in the reactor's proportion: input = E / η, waste = input − E
            var efficiency = state.GeneratorEfficiency[0];
            var input = 1000 / efficiency;
            Assert.That(state.ReactorPowerRatio[0], Is.EqualTo(input / 4500).Within(1e-9));
            Assert.That(state.WasteHeatMW, Is.EqualTo(input - 1000).Within(1e-6));

            // fuel is burnt for ρ × (thermal + charged)
            var rates = SteadyStateSolver.ResourceRates(spec, state);
            Assert.That(rates["UraniumNitride"], Is.EqualTo(-state.ReactorPowerRatio[0] * 4500 * 2.36e-8).Within(1e-15));
            Assert.That(rates["DepletedFuel"], Is.EqualTo(state.ReactorPowerRatio[0] * 4500 * 2.0e-8).Within(1e-15));
        }

        [Test]
        public void EfficiencyFollowsTheColdBath()
        {
            var spec = PebbleBed(radiatorAreaM2: 5000);
            var state = SteadyStateSolver.Solve(spec, 1000);

            var coldBath = SteadyStateSolver.VesselRadiatorTemperature(spec, state.WasteHeatFill);
            Assert.That(coldBath, Is.EqualTo(1500 * Math.Pow(state.WasteHeatFill, 0.75)).Within(1e-9));
            Assert.That(state.GeneratorEfficiency[0], Is.EqualTo(0.99 * (1 - 0.75 * coldBath / 3000)).Within(1e-12));
        }

        [Test]
        public void TooFewRadiators_PowerIsCutAtTheOverheatThreshold()
        {
            var spec = PebbleBed(radiatorAreaM2: 300);
            var state = SteadyStateSolver.Solve(spec, 2000);

            Assert.That(state.HeatLimited, Is.True);
            Assert.That(state.ElectricMW, Is.LessThan(2000));
            Assert.That(state.ElectricMW, Is.GreaterThan(0));
            Assert.That(state.WasteHeatFill, Is.EqualTo(0.95).Within(1e-3));
            AssertHeatBalance(spec, state);
        }

        [Test]
        public void MoreRadiators_MorePowerWhenHeatLimited()
        {
            var small = SteadyStateSolver.Solve(PebbleBed(radiatorAreaM2: 300), 2000);
            var large = SteadyStateSolver.Solve(PebbleBed(radiatorAreaM2: 600), 2000);

            Assert.That(large.ElectricMW, Is.GreaterThan(small.ElectricMW));
        }

        [Test]
        public void DemandAboveGeneratorCapacity_LimitedByCapacityNotHeat()
        {
            var spec = PebbleBed(radiatorAreaM2: 1e6);
            var state = SteadyStateSolver.Solve(spec, 1e5);

            Assert.That(state.HeatLimited, Is.False);
            Assert.That(state.ReactorPowerRatio[0], Is.EqualTo(1).Within(1e-6));
            Assert.That(state.ElectricMW, Is.EqualTo(state.GeneratorEfficiency[0] * 4500).Within(1e-3));
            AssertHeatBalance(spec, state);
        }

        [Test]
        public void MinimumThrottle_BurnsFuelWithoutDemand()
        {
            var spec = PebbleBed(radiatorAreaM2: 5000, minimumThrottle: 0.1);
            var state = SteadyStateSolver.Solve(spec, 0);

            Assert.That(state.ElectricMW, Is.EqualTo(0));
            Assert.That(state.ReactorPowerRatio[0], Is.EqualTo(0.1).Within(1e-12));
            // the generator draws the minimum power (450 MW, thermal and charged) and converts it; only the loss is heat
            Assert.That(state.WasteHeatMW, Is.EqualTo(450 * (1 - state.GeneratorEfficiency[0])).Within(1e-6));
            AssertHeatBalance(spec, state);
            Assert.That(SteadyStateSolver.ResourceRates(spec, state)["UraniumNitride"], Is.EqualTo(-0.1 * 4500 * 2.36e-8).Within(1e-15));
        }

        [Test]
        public void MinimumThrottle_WithoutGenerator_ReactorIdles()
        {
            var spec = PebbleBed(radiatorAreaM2: 5000, minimumThrottle: 0.1);
            spec.Generators.Clear();
            var state = SteadyStateSolver.Solve(spec, 0);

            Assert.That(state.ReactorPowerRatio[0], Is.EqualTo(0));
            Assert.That(state.WasteHeatMW, Is.EqualTo(0).Within(1e-12));
        }

        [Test]
        public void MoltenSaltReactorLikeScaut_SmallDemandBurnsTheMinimum()
        {
            // Scaut, loaded 2026-11-03: 1012.47 MW thermal, minimum throttle 6 %, thermoelectric generator 0.99,
            // demand 5 MW → the reactor ran at 6 % (60.75 MW) and the generator at ~74 % efficiency
            var spec = new VesselSpec();
            spec.Reactors.Add(new ReactorSpec { ThermalMW = 1012.47, HotBathK = 3200, MinimumThrottle = 0.06 });
            spec.Generators.Add(new GeneratorSpec { Reactor = 0, Mode = GeneratorMode.Thermal, MaxEfficiency = 0.99, Share = 1 });
            spec.Radiators.Add(new RadiatorSpec { StefanArea = StefanBoltzmann * 3000 * 1e-6, MaxTemperatureK = 1500, EnvironmentMaxK = 1500, Mass = 10 });

            var state = SteadyStateSolver.Solve(spec, 5);

            Assert.That(state.ElectricMW, Is.EqualTo(5).Within(1e-9));
            Assert.That(state.ReactorPowerRatio[0], Is.EqualTo(0.06).Within(1e-12));
            AssertHeatBalance(spec, state);
        }

        [Test]
        public void ChargedGenerator_FixedEfficiencyAndWaste()
        {
            var spec = new VesselSpec();
            spec.Reactors.Add(new ReactorSpec { ThermalMW = 100, ChargedMW = 900, HotBathK = 3000 });
            spec.Generators.Add(new GeneratorSpec { Reactor = 0, Mode = GeneratorMode.Charged, MaxEfficiency = 0.8 });
            spec.Radiators.Add(new RadiatorSpec { StefanArea = StefanBoltzmann * 5000 * 1e-6, MaxTemperatureK = 1500, EnvironmentMaxK = 1500 });

            var state = SteadyStateSolver.Solve(spec, 400);

            Assert.That(state.ElectricMW, Is.EqualTo(400).Within(1e-9));
            Assert.That(state.GeneratorEfficiency[0], Is.EqualTo(0.8));
            // charged drawn 500 MW → ρ = 500/900; the thermal part runs at the same ratio and is all waste
            var ratio = 500.0 / 900;
            Assert.That(state.ReactorPowerRatio[0], Is.EqualTo(ratio).Within(1e-9));
            Assert.That(state.WasteHeatMW, Is.EqualTo(500 * 0.2 + ratio * 100).Within(1e-6));
            AssertHeatBalance(spec, state);
        }

        [Test]
        public void NoRadiators_NoElectricity()
        {
            var spec = PebbleBed(radiatorAreaM2: 5000);
            spec.Radiators.Clear();
            var state = SteadyStateSolver.Solve(spec, 1000);

            Assert.That(state.ElectricMW, Is.EqualTo(0));
            Assert.That(state.RequestedElectricMW, Is.EqualTo(1000));
        }

        [Test]
        public void TwoGenerators_ShareTheDemandByCapacity()
        {
            var spec = PebbleBed(radiatorAreaM2: 5000);
            spec.Generators[0].Share = 0.5;
            spec.Generators.Add(new GeneratorSpec { Reactor = 0, Mode = GeneratorMode.Thermal, MaxEfficiency = 0.99, UsesChargedPower = true, Share = 0.5 });
            var state = SteadyStateSolver.Solve(spec, 1000);

            Assert.That(state.GeneratorElectricMW[0], Is.EqualTo(500).Within(1e-9));
            Assert.That(state.GeneratorElectricMW[1], Is.EqualTo(500).Within(1e-9));
            AssertHeatBalance(spec, state);
        }
    }
}
