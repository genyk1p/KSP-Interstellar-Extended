using System;
using System.Collections.Generic;
using System.Linq;
using FNPlugin.Background.Core;
using NUnit.Framework;

namespace Background.Tests
{
    /// <summary>
    /// The pieces of step A2 that need no KSP: the step's resource ledger, the power split between the battery and
    /// the consumers, and the consumers' waste heat in the plant's heat balance.
    /// </summary>
    public class ConsumerTests
    {
        private const double StefanBoltzmann = 5.670374419e-8;

        private static ResourceLedger Ledger(params (string resource, double amount, double capacity)[] stock)
        {
            return new ResourceLedger(stock.ToDictionary(s => s.resource, s => s.amount), stock.ToDictionary(s => s.resource, s => s.capacity));
        }

        private static double Change(ResourceLedger ledger, string resource) =>
            ledger.Changes.Where(c => c.Key == resource).Sum(c => c.Value);

        // ---------------------------------------------------------------- ledger

        [Test]
        public void LedgerTakesAtMostWhatIsThereAndStoresAtMostWhatFits()
        {
            var ledger = Ledger(("Water", 10, 100), ("Hydrogen", 95, 100));
            Assert.That(ledger.Request("Water", 4), Is.EqualTo(4));
            Assert.That(ledger.Request("Water", 50), Is.EqualTo(6), "only what is left");
            Assert.That(ledger.Request("Water", 1), Is.EqualTo(0));
            Assert.That(ledger.Request("Hydrogen", -20), Is.EqualTo(-5), "only the room; the rest is lost as overflow");
            Assert.That(Change(ledger, "Water"), Is.EqualTo(-10));
            Assert.That(Change(ledger, "Hydrogen"), Is.EqualTo(5));
            Assert.That(ledger.Amount("Water"), Is.EqualTo(0));
            Assert.That(ledger.Room("Hydrogen"), Is.EqualTo(0));
        }

        [Test]
        public void LedgerWithoutStorageStoresNothing()
        {
            var ledger = Ledger(("Water", 10, 100));
            Assert.That(ledger.Store("Oxygen", 5), Is.EqualTo(0));
            Assert.That(ledger.Capacity("Oxygen"), Is.EqualTo(0));
            Assert.That(ledger.Take("Oxygen", 5), Is.EqualTo(0));
            Assert.That(ledger.Changes, Is.Empty);
        }

        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(double.NegativeInfinity)]
        [TestCase(0.0)]
        public void LedgerIgnoresBadRequests(double demand)
        {
            var ledger = Ledger(("Water", 10, 100));
            Assert.That(ledger.Request("Water", demand), Is.EqualTo(0));
            Assert.That(ledger.Request(null, 1), Is.EqualTo(0));
            Assert.That(ledger.Changes.All(c => c.Value == 0), Is.True);
        }

        [Test]
        public void LedgerCleansBadStartValues()
        {
            var ledger = new ResourceLedger(new Dictionary<string, double> { ["A"] = double.NaN, ["B"] = -5, ["C"] = 150 },
                new Dictionary<string, double> { ["A"] = 10, ["B"] = 10, ["C"] = 100 });
            Assert.That(ledger.Amount("A"), Is.EqualTo(0));
            Assert.That(ledger.Amount("B"), Is.EqualTo(0));
            Assert.That(ledger.Room("C"), Is.EqualTo(0), "more than the capacity (Kerbalism counts storage the snapshot reading may not): no room, not negative");
            Assert.That(ledger.Take("C", 200), Is.EqualTo(150));
        }

        [Test]
        public void ForkedLedgerReachesTheParentOnlyWhenAbsorbed()
        {
            var ledger = Ledger(("Water", 10, 100), ("Oxygen", 0, 100));
            ledger.Take("Water", 2);

            var failed = ledger.Fork();
            Assert.That(failed.Amount("Water"), Is.EqualTo(8), "starts from what the parent has now");
            failed.Take("Water", 8);
            failed.Store("Oxygen", 50);
            // dropped: nothing of it reaches the parent
            Assert.That(ledger.Amount("Water"), Is.EqualTo(8));
            Assert.That(Change(ledger, "Oxygen"), Is.EqualTo(0));

            var done = ledger.Fork();
            done.Take("Water", 3);
            done.Store("Oxygen", 7);
            ledger.Absorb(done);
            Assert.That(ledger.Amount("Water"), Is.EqualTo(5));
            Assert.That(Change(ledger, "Water"), Is.EqualTo(-5));
            Assert.That(Change(ledger, "Oxygen"), Is.EqualTo(7));
        }

        // ---------------------------------------------------------------- power split

        [Test]
        public void BatteryChargeFirstThenByPrioritySmallestRequestFirst()
        {
            var consumers = new List<ConsumerDemand>
            {
                new ConsumerDemand { DemandMW = 4, Priority = 4 },
                new ConsumerDemand { DemandMW = 3, Priority = 2 },
                new ConsumerDemand { DemandMW = 6, Priority = 4 }
            };
            var result = PowerAllocation.Allocate(10, 2, consumers);
            Assert.That(result.ElectricChargeMW, Is.EqualTo(2));
            Assert.That(result.ConsumerMW[1], Is.EqualTo(3), "priority 2 before 4");
            // 5 MW left for priority 4: the smaller request (4) in full, the rest to the larger (KSPIE smallest first)
            Assert.That(result.ConsumerMW[0], Is.EqualTo(4).Within(1e-12));
            Assert.That(result.ConsumerMW[2], Is.EqualTo(1).Within(1e-12));
        }

        [Test]
        public void LowerPriorityGetsNothingAfterAPartialPriority()
        {
            var consumers = new List<ConsumerDemand>
            {
                new ConsumerDemand { DemandMW = 7.1, Priority = 2 },
                new ConsumerDemand { DemandMW = 6.9, Priority = 2 },
                new ConsumerDemand { DemandMW = 1, Priority = 4 }
            };
            var result = PowerAllocation.Allocate(3.9, 0, consumers);
            Assert.That(result.ConsumerMW[2], Is.EqualTo(0));
            Assert.That(result.ConsumerMW[1], Is.EqualTo(3.9).Within(1e-12), "6.9 is the smaller request");
            Assert.That(result.ConsumerMW[0], Is.EqualTo(0));
            Assert.That(result.ConsumerMW.All(mw => mw >= 0), Is.True);
        }

        [Test]
        public void ConsumerHeatFollowsWhoGetsThePower()
        {
            // a transmitter (priority 2, 30 % heat) and a heat-free consumer (priority 4), 100 MW each, 100 MW to share
            var transmitterFirst = new List<ConsumerDemand>
            {
                new ConsumerDemand { DemandMW = 100, Priority = 2, WasteHeatFraction = 0.3 },
                new ConsumerDemand { DemandMW = 100, Priority = 4 }
            };
            Assert.That(PowerAllocation.ConsumerHeat(100, 0, transmitterFirst), Is.EqualTo(30).Within(1e-12));
            var heatFreeFirst = new List<ConsumerDemand>
            {
                new ConsumerDemand { DemandMW = 100, Priority = 4, WasteHeatFraction = 0.3 },
                new ConsumerDemand { DemandMW = 100, Priority = 2 }
            };
            Assert.That(PowerAllocation.ConsumerHeat(100, 0, heatFreeFirst), Is.EqualTo(0));
            Assert.That(PowerAllocation.ConsumerHeat(150, 20, heatFreeFirst), Is.EqualTo(0.3 * 30).Within(1e-12), "battery charge first, then 100 + 30");
            var overOne = new List<ConsumerDemand> { new ConsumerDemand { DemandMW = 10, WasteHeatFraction = 7 } };
            Assert.That(PowerAllocation.ConsumerHeat(10, 0, overOne), Is.EqualTo(10), "a fraction above 1 counts as 1");
        }

        [Test]
        public void BatteryTopsUpOnlyConsumersThatTakeElectricCharge()
        {
            // battery 8000 / 10000 EC, floor 50 %, a 10 s step: 3000 EC = 0.3 MW above the floor
            var batteryMW = (8000 - 0.5 * 10000) / 10.0 / 1000;
            var consumers = new List<ConsumerDemand>
            {
                new ConsumerDemand { DemandMW = 1, Priority = 2, TakesBattery = true },
                new ConsumerDemand { DemandMW = 2, Priority = 4, TakesBattery = true },
                new ConsumerDemand { DemandMW = 5, Priority = 1, TakesBattery = false }
            };
            var allocation = new PowerAllocation.Result { ConsumerMW = new[] { 0.9, 0, 0 } };
            var drawn = PowerAllocation.TopUpFromBattery(allocation, consumers, batteryMW);
            Assert.That(drawn, Is.EqualTo(0.3).Within(1e-12));
            Assert.That(allocation.ConsumerMW[0], Is.EqualTo(1).Within(1e-12));
            Assert.That(allocation.ConsumerMW[1], Is.EqualTo(0.2).Within(1e-12));
            Assert.That(allocation.ConsumerMW[2], Is.EqualTo(0), "a factory or transmitter never takes ElectricCharge");
            Assert.That(PowerAllocation.TopUpFromBattery(allocation, consumers, 0), Is.EqualTo(0));
            Assert.That(PowerAllocation.TopUpFromBattery(allocation, consumers, double.NaN), Is.EqualTo(0));
        }

        [Test]
        public void PlentyOfPowerServesEveryoneFully()
        {
            var consumers = new List<ConsumerDemand> { new ConsumerDemand { DemandMW = 4, Priority = 4 }, new ConsumerDemand { DemandMW = 3, Priority = 2 } };
            var result = PowerAllocation.Allocate(100, 5, consumers);
            Assert.That(result.ElectricChargeMW, Is.EqualTo(5));
            Assert.That(result.ConsumerMW, Is.EqualTo(new[] { 4.0, 3.0 }));
        }

        [Test]
        public void ShortPowerGoesToTheBatteryFirst()
        {
            var consumers = new List<ConsumerDemand> { new ConsumerDemand { DemandMW = 4, Priority = 2 } };
            var result = PowerAllocation.Allocate(1.5, 2, consumers);
            Assert.That(result.ElectricChargeMW, Is.EqualTo(1.5));
            Assert.That(result.ConsumerMW[0], Is.EqualTo(0));
        }

        [TestCase(double.NaN)]
        [TestCase(-3.0)]
        [TestCase(double.NegativeInfinity)]
        public void BadPowerGivesNothing(double available)
        {
            var consumers = new List<ConsumerDemand> { new ConsumerDemand { DemandMW = 4 } };
            var result = PowerAllocation.Allocate(available, 2, consumers);
            Assert.That(result.ElectricChargeMW, Is.EqualTo(0));
            Assert.That(result.ConsumerMW[0], Is.EqualTo(0));
        }

        [Test]
        public void NoConsumersAndNullEntriesAreFine()
        {
            Assert.That(PowerAllocation.Allocate(5, 1, null).ElectricChargeMW, Is.EqualTo(1));
            var consumers = new List<ConsumerDemand> { null, new ConsumerDemand { DemandMW = double.NaN }, new ConsumerDemand { DemandMW = 2 } };
            var result = PowerAllocation.Allocate(5, 1, consumers);
            Assert.That(result.ConsumerMW, Is.EqualTo(new[] { 0.0, 0.0, 2.0 }));
            Assert.That(PowerAllocation.TotalDemand(consumers), Is.EqualTo(2));
        }

        // ---------------------------------------------------------------- battery measurement with consumers

        [Test]
        public void BatteryDrawOfConsumersIsNotTakenForTheVesselsConsumption()
        {
            var demand = new ElectricDemand();
            demand.Request(5000, 10000, 1);
            // generators gave 100 EC/s, the refinery took 150 EC/s from the battery, the rest of the vessel used 10 EC/s
            demand.Produced(5000, 100, 1, 150);
            demand.Request(5000 + 100 - 150 - 10, 10000, 1);
            Assert.That(demand.ConsumptionPerSecond, Is.EqualTo(10).Within(1e-9));
        }

        // ---------------------------------------------------------------- consumer heat in the plant

        private static VesselSpec Plant(double radiatorAreaM2)
        {
            var spec = new VesselSpec();
            spec.Reactors.Add(new ReactorSpec { ThermalMW = 100, ChargedMW = 0, HotBathK = 3000 });
            spec.Generators.Add(new GeneratorSpec { Reactor = 0, Mode = GeneratorMode.Thermal, MaxEfficiency = 0.5 });
            spec.Radiators.Add(new RadiatorSpec
            {
                StefanArea = StefanBoltzmann * radiatorAreaM2 * 1e-6,
                MaxTemperatureK = 1500,
                EnvironmentMaxK = 1500,
                Mass = 5
            });
            return spec;
        }

        [Test]
        public void ConsumerHeatWarmsThePlant()
        {
            var spec = Plant(2000);
            var cold = SteadyStateSolver.Solve(spec, 20, 5, 0);
            var hot = SteadyStateSolver.Solve(spec, 20, 5, 0.999);
            Assert.That(cold.ElectricMW, Is.EqualTo(20).Within(1e-9));
            Assert.That(hot.ElectricMW, Is.EqualTo(20).Within(1e-9));
            // the 15 MW above the heat-free 5 MW come back as heat
            Assert.That(hot.WasteHeatMW - cold.WasteHeatMW, Is.GreaterThan(0.999 * 15 * 0.99));
            Assert.That(hot.WasteHeatFill, Is.GreaterThan(cold.WasteHeatFill));
            Assert.That(SteadyStateSolver.Radiated(spec, hot.WasteHeatFill), Is.EqualTo(hot.WasteHeatMW).Within(1e-6 * hot.WasteHeatMW));
        }

        [Test]
        public void ConsumerHeatLimitsThePowerExactly()
        {
            // radiators that limit the plant even without consumer heat
            var spec = Plant(60);
            var cold = SteadyStateSolver.Solve(spec, 40, 0, 0);
            var hot = SteadyStateSolver.Solve(spec, 40, 0, 1);
            Assert.That(cold.HeatLimited && hot.HeatLimited, Is.True);
            Assert.That(hot.WasteHeatMW, Is.EqualTo(cold.WasteHeatMW).Within(1e-9), "both at the overheat threshold");
            var lossPerMW = cold.WasteHeatMW / cold.ElectricMW; // generator loss per MW at that temperature
            Assert.That(hot.ElectricMW, Is.EqualTo(cold.WasteHeatMW / (lossPerMW + 1)).Within(1e-6));
        }

        [Test]
        public void NoConsumerHeatWhenThePowerIsBelowTheHeatFreePart()
        {
            var spec = Plant(60);
            var plain = SteadyStateSolver.Solve(spec, 40);
            var withConsumers = SteadyStateSolver.Solve(spec, 40, 30, 1);
            Assert.That(plain.HeatLimited, Is.True);
            Assert.That(withConsumers.ElectricMW, Is.EqualTo(plain.ElectricMW).Within(1e-9), "all of the delivered power is under the heat-free 30 MW");
        }

        private static VesselSpec ChargedPlant()
        {
            // a charged particle generator: its efficiency does not depend on the radiators, heats add up exactly
            var spec = new VesselSpec();
            spec.Reactors.Add(new ReactorSpec { ChargedMW = 100, HotBathK = 3000 });
            spec.Generators.Add(new GeneratorSpec { Reactor = 0, Mode = GeneratorMode.Charged, MaxEfficiency = 0.8 });
            spec.Radiators.Add(new RadiatorSpec { StefanArea = StefanBoltzmann * 2000 * 1e-6, MaxTemperatureK = 1500, EnvironmentMaxK = 1500, Mass = 5 });
            return spec;
        }

        [Test]
        public void ConsumerAndExtraHeatAddExactly()
        {
            var plant = ChargedPlant();
            Assert.That(SteadyStateSolver.Solve(plant, 20, 5, 0).WasteHeatMW, Is.EqualTo(5).Within(1e-9), "generator loss 25 x 0.2");
            Assert.That(SteadyStateSolver.Solve(plant, 20, 5, 0.999).WasteHeatMW, Is.EqualTo(5 + 0.999 * 15).Within(1e-9));
            Assert.That(SteadyStateSolver.Solve(plant, 20, 5, 0, 30).WasteHeatMW, Is.EqualTo(35).Within(1e-9));
            Assert.That(SteadyStateSolver.Solve(plant, 20, e => 2 * e).WasteHeatMW, Is.EqualTo(5 + 40).Within(1e-9), "heat as a function of the delivered power");
            Assert.That(SteadyStateSolver.Solve(plant, 20, e => double.NaN).WasteHeatMW, Is.EqualTo(5).Within(1e-9), "NaN heat counts as none");
        }

        [Test]
        public void NoConsumerHeatIsTheOldSolve()
        {
            var spec = Plant(2000);
            var plain = SteadyStateSolver.Solve(spec, 20);
            var withConsumers = SteadyStateSolver.Solve(spec, 20, 20, 0.7);
            Assert.That(withConsumers.WasteHeatMW, Is.EqualTo(plain.WasteHeatMW).Within(1e-12), "all of it heat-free (ElectricCharge)");
            Assert.That(withConsumers.WasteHeatFill, Is.EqualTo(plain.WasteHeatFill).Within(1e-12));
        }

        [TestCase(double.NaN, 0.5, 0.0, 0.5)]
        [TestCase(-1.0, 0.5, 0.0, 0.5)]
        [TestCase(5.0, double.NaN, 5.0, 0.0)]
        [TestCase(5.0, -2.0, 5.0, 0.0)]
        [TestCase(5.0, 7.0, 5.0, 1.0)]
        public void BadHeatArgumentsAreCleaned(double heatFree, double fraction, double cleanHeatFree, double cleanFraction)
        {
            var state = SteadyStateSolver.Solve(Plant(2000), 20, heatFree, fraction);
            var clean = SteadyStateSolver.Solve(Plant(2000), 20, cleanHeatFree, cleanFraction);
            Assert.That(state.WasteHeatMW, Is.EqualTo(clean.WasteHeatMW).Within(1e-12));
            Assert.That(state.WasteHeatFill, Is.EqualTo(clean.WasteHeatFill).Within(1e-12));
            Assert.That(state.ElectricMW, Is.GreaterThan(0));
        }
    }
}
