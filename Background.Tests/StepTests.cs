using System;
using System.Collections.Generic;
using System.Linq;
using FNPlugin.Background.Core;
using NUnit.Framework;

namespace Background.Tests
{
    /// <summary>
    /// The arithmetic of one background step (Core/StepPlan): who powers what, the heat the plant must shed besides its
    /// own, a recipe consumer's run, and the beamed power each unloaded receiver takes from a transmitter.
    /// </summary>
    public class StepTests
    {
        private static List<ConsumerDemand> Consumer(double demand, double heat) =>
            new List<ConsumerDemand> { new ConsumerDemand { DemandMW = demand, Priority = 4, WasteHeatFraction = heat } };

        // ---------------------------------------------------------------- receivers first, then reactors

        [TestCase(8, 4, 8, 1, 7, 9)]          // receivers cover the battery charge and 3 of 10 MW: reactors 7 MW, heat 5 + 4 at E = 7
        [TestCase(3, 1.5, 3, 1, 12, 6.5)]     // receivers cover part of the battery charge: heat 0.5 x 10 + 1.5 at E = 12
        [TestCase(20, 10, 15, 0.75, 0, 12.5)] // receivers more than enough: reactors nothing, three quarters of their heat
        public void ReceiversServeFirstAndHeatCountsOnce(double receiverMW, double receiverHeat, double used, double share, double reactorRequest, double heatAtRequest)
        {
            var plan = StepPlan.Make(5, Consumer(10, 0.5), receiverMW, receiverHeat, false);
            Assert.That(plan.ReceiverUsedMW, Is.EqualTo(used).Within(1e-12));
            Assert.That(plan.ReceiverShare, Is.EqualTo(share).Within(1e-12));
            Assert.That(plan.ReactorRequestMW, Is.EqualTo(reactorRequest).Within(1e-12));
            Assert.That(plan.ReactorRequestMW + plan.ReceiverUsedMW, Is.EqualTo(15).Within(1e-12), "everything asked is covered once");
            Assert.That(plan.ReactorExtraHeat(plan.ReactorRequestMW), Is.EqualTo(heatAtRequest).Within(1e-12));
            Assert.That(plan.SupplyMW(plan.ReactorRequestMW), Is.EqualTo(15).Within(1e-12));
        }

        [Test]
        public void ReactorsShortOfTheRequestMakeLessConsumerHeat()
        {
            // the battery charge (5) comes first: with only 3 MW from the reactors the consumer gets nothing, no heat
            var plan = StepPlan.Make(5, Consumer(10, 0.5), 0, 0, false);
            Assert.That(plan.ReactorExtraHeat(3), Is.EqualTo(0));
            Assert.That(plan.ReactorExtraHeat(9), Is.EqualTo(2).Within(1e-12));
        }

        [TestCase(double.NaN)]
        [TestCase(-4.0)]
        [TestCase(double.PositiveInfinity)]
        public void BadReceiverPowerCountsAsNone(double receiverMW)
        {
            var plan = StepPlan.Make(5, Consumer(10, 0.5), receiverMW, double.NaN, false);
            Assert.That(plan.ReceiverUsedMW, Is.EqualTo(0));
            Assert.That(plan.ReactorRequestMW, Is.EqualTo(15).Within(1e-12));
            Assert.That(plan.ReactorExtraHeat(15), Is.EqualTo(5).Within(1e-12));
        }

        [Test]
        public void FreePowerAsksNothingForTheConsumers()
        {
            var plan = StepPlan.Make(5, Consumer(10, 0.5), 0, 0, true);
            Assert.That(plan.ReactorRequestMW, Is.EqualTo(5).Within(1e-12), "only the battery charge");
            Assert.That(plan.ReactorExtraHeat(5), Is.EqualTo(0));
        }

        // ---------------------------------------------------------------- recipe consumers

        private static ResourceLedger Ledger(params (string resource, double amount, double capacity)[] stock) =>
            new ResourceLedger(stock.ToDictionary(s => s.resource, s => s.amount), stock.ToDictionary(s => s.resource, s => s.capacity));

        private static double Change(ResourceLedger ledger, string resource) => ledger.Changes.Where(c => c.Key == resource).Sum(c => c.Value);

        private static List<KeyValuePair<string, double>> Rates(params (string resource, double rate)[] rates) =>
            rates.Select(r => new KeyValuePair<string, double>(r.resource, r.rate)).ToList();

        [Test]
        public void RecipeScalesByPowerAndInput()
        {
            // metallic hydrogen factory: 10 LH2/s in, 1 MH/s out at full power; half the power for 100 s, 300 LH2 there
            var ledger = Ledger(("LqdHydrogen", 300, 1000), ("MtlHydrogen", 0, 1000));
            var scale = RecipeStep.Run(ledger, Rates(("LqdHydrogen", 10)), Rates(("MtlHydrogen", 1)), 0.5, 0, 100);
            Assert.That(scale, Is.EqualTo(0.3).Within(1e-12), "500 LH2 wanted, 300 there");
            Assert.That(Change(ledger, "LqdHydrogen"), Is.EqualTo(-300).Within(1e-9));
            Assert.That(Change(ledger, "MtlHydrogen"), Is.EqualTo(30).Within(1e-9));
        }

        [Test]
        public void RecipeOutputBeyondTheRoomIsLost()
        {
            var ledger = Ledger(("LqdHydrogen", 300, 1000), ("MtlHydrogen", 990, 1000));
            RecipeStep.Run(ledger, Rates(("LqdHydrogen", 10)), Rates(("MtlHydrogen", 1)), 0.5, 0, 100);
            Assert.That(Change(ledger, "LqdHydrogen"), Is.EqualTo(-300).Within(1e-9), "the input is used as by the loaded factory");
            Assert.That(Change(ledger, "MtlHydrogen"), Is.EqualTo(10).Within(1e-9));
        }

        [Test]
        public void RecipeBelowItsMinimumShareDoesNothing()
        {
            var ledger = Ledger(("Ore", 0, 1000));
            Assert.That(RecipeStep.Run(ledger, null, Rates(("Ore", 1)), 0.1, 0.15, 100), Is.EqualTo(0), "the drill stops under 15 %");
            Assert.That(ledger.Changes.All(c => c.Value == 0), Is.True);
            Assert.That(RecipeStep.Run(ledger, null, Rates(("Ore", 1)), 0.15, 0.15, 100), Is.EqualTo(0.15).Within(1e-12));
        }

        [Test]
        public void RecipeIsLimitedByItsScarcestInput()
        {
            var ledger = Ledger(("A", 1000, 1000), ("B", 50, 1000), ("C", 0, 1000));
            RecipeStep.Run(ledger, Rates(("A", 1), ("B", 1)), Rates(("C", 1)), 1, 0, 100);
            Assert.That(Change(ledger, "A"), Is.EqualTo(-50).Within(1e-9));
            Assert.That(Change(ledger, "B"), Is.EqualTo(-50).Within(1e-9));
            Assert.That(Change(ledger, "C"), Is.EqualTo(50).Within(1e-9));
        }

        [TestCase(double.NaN)]
        [TestCase(-1.0)]
        [TestCase(0.0)]
        public void RecipeWithoutPowerDoesNothing(double share)
        {
            var ledger = Ledger(("Ore", 0, 1000));
            Assert.That(RecipeStep.Run(ledger, null, Rates(("Ore", 1)), share, 0, 100), Is.EqualTo(0));
            Assert.That(ledger.Changes.All(c => c.Value == 0), Is.True);
        }

        [Test]
        public void SecondConsumerStoresOnlyInTheRoomLeft()
        {
            var ledger = new ResourceLedger(new Dictionary<string, double> { ["Oxygen"] = 0 }, new Dictionary<string, double> { ["Oxygen"] = 100 });
            var first = ledger.Fork();
            first.Store("Oxygen", 70);
            ledger.Absorb(first);
            var second = ledger.Fork();
            Assert.That(second.Store("Oxygen", 50), Is.EqualTo(30));
            ledger.Absorb(second);
            Assert.That(ledger.Changes.Single().Value, Is.EqualTo(100));
        }

        // ---------------------------------------------------------------- beamed power shared between receivers

        private static readonly Guid TransmitterA = Guid.NewGuid();
        private static readonly Guid TransmitterB = Guid.NewGuid();

        private static IEnumerable<KeyValuePair<Guid, double>> Took(params (Guid transmitter, double mw)[] took) =>
            took.Select(t => new KeyValuePair<Guid, double>(t.transmitter, t.mw));

        [Test]
        public void ReceiversCountEachOthersLastIntake()
        {
            var usage = new BeamUsage<string>();
            usage.Set("one", Took((TransmitterA, 30), (TransmitterB, 5)));
            usage.Set("two", Took((TransmitterA, 20)));
            Assert.That(usage.UsedByOthers(TransmitterA, "three", null), Is.EqualTo(50));
            Assert.That(usage.UsedByOthers(TransmitterA, "one", null), Is.EqualTo(20), "not its own intake");
            Assert.That(usage.UsedByOthers(TransmitterB, "two", null), Is.EqualTo(5));

            usage.Set("one", Took((TransmitterA, 10)));
            Assert.That(usage.UsedByOthers(TransmitterA, "three", null), Is.EqualTo(30), "a new intake replaces the old one");
            Assert.That(usage.UsedByOthers(TransmitterB, "three", null), Is.EqualTo(0));
        }

        [Test]
        public void GoneOrLoadedReceiversNoLongerCount()
        {
            var usage = new BeamUsage<string>();
            usage.Set("unloaded", Took((TransmitterA, 30)));
            usage.Set("loaded", Took((TransmitterA, 20)));
            Assert.That(usage.UsedByOthers(TransmitterA, "self", r => r != "loaded"), Is.EqualTo(30), "loaded ones KSPIE counts itself");
            usage.ForgetWhere(r => r == "unloaded");
            Assert.That(usage.UsedByOthers(TransmitterA, "self", null), Is.EqualTo(20));
        }

        [Test]
        public void BadIntakesAreNotCounted()
        {
            var usage = new BeamUsage<string>();
            usage.Set("one", Took((TransmitterA, double.NaN), (TransmitterA, double.PositiveInfinity), (TransmitterA, -5), (TransmitterA, 7)));
            Assert.That(usage.UsedByOthers(TransmitterA, "two", null), Is.EqualTo(7));
            usage.Set("one", null);
            Assert.That(usage.UsedByOthers(TransmitterA, "two", null), Is.EqualTo(0));
        }

        // ---------------------------------------------------------------- the battery measurement with consumers drawing

        [Test]
        public void BatteryDrawDoesNotMarkTheGeneratorsAsLimited()
        {
            var demand = new ElectricDemand();
            var request = demand.Request(5000, 10000, 1);
            Assert.That(request, Is.EqualTo(4800).Within(1e-9));
            demand.Produced(5000, 4800, 1, 2000); // gave all that was asked; consumers drew 2000 EC/s on top
            demand.Request(0, 10000, 1);          // ran empty
            // not limited: overshoot x4 of the lower bound 4800 - 2000 + 5000 = 7800
            Assert.That(demand.ConsumptionPerSecond, Is.EqualTo(4 * 7800).Within(1e-6));
        }

        // ---------------------------------------------------------------- requests sized by the plant (transmitter, factories)

        [Test]
        public void SliderConsumersAskForTheSliderOfWhatThePlantCanGive()
        {
            // capacity 5000 MW at 50 %, the plant gives at most 3000: loaded the transmitter asks for 1500, not 2500
            var transmitter = new ConsumerDemand { DemandMW = 2500, PlantSlider = 0.5 };
            var refinery = new ConsumerDemand { DemandMW = 2500 };
            PowerAllocation.CapByPlant(new List<ConsumerDemand> { transmitter, refinery }, 3000);
            Assert.That(transmitter.DemandMW, Is.EqualTo(1500).Within(1e-9));
            Assert.That(refinery.DemandMW, Is.EqualTo(2500), "the refinery asks for slider x capacity whatever the plant");

            var small = new ConsumerDemand { DemandMW = 2500, PlantSlider = 0.5 };
            PowerAllocation.CapByPlant(new List<ConsumerDemand> { small }, 10000);
            Assert.That(small.DemandMW, Is.EqualTo(2500), "a plant stronger than the capacity changes nothing");

            var unknown = new ConsumerDemand { DemandMW = 2500, PlantSlider = 0.5 };
            PowerAllocation.CapByPlant(new List<ConsumerDemand> { unknown, null }, double.NaN);
            Assert.That(unknown.DemandMW, Is.EqualTo(0));
        }

        [Test]
        public void MaximumElectricPowerIsTheGeneratorsCapacity()
        {
            var spec = new VesselSpec();
            spec.Reactors.Add(new ReactorSpec { ThermalMW = 100, HotBathK = 3000 });
            spec.Generators.Add(new GeneratorSpec { Reactor = 0, Mode = GeneratorMode.Thermal, MaxEfficiency = 0.5 });
            spec.Radiators.Add(new RadiatorSpec { StefanArea = 5.670374419e-8 * 2000 * 1e-6, MaxTemperatureK = 1500, EnvironmentMaxK = 1500, Mass = 5 });
            var maximum = SteadyStateSolver.MaximumElectricMW(spec);
            Assert.That(maximum, Is.GreaterThan(0).And.LessThanOrEqualTo(50));
            Assert.That(maximum, Is.GreaterThanOrEqualTo(SteadyStateSolver.Solve(spec, 1e9).ElectricMW - 1e-9), "never less than what is delivered");

            spec.Radiators.Clear();
            Assert.That(SteadyStateSolver.MaximumElectricMW(spec), Is.EqualTo(0), "no radiators: KSPIE generators make nothing");
        }

        [Test]
        public void StableSupplyIsTheCapacityTheSliderConsumersSettleAt()
        {
            // capacity 100 MW cold, falling by 2 MW per MW drawn; a 20 % consumer: c = 100 - 2 x 0.2 c -> 71.43
            var stable = StepPlan.StableSupply(c => 100 - 2 * 0.2 * c, 100);
            Assert.That(stable, Is.EqualTo(100 / 1.4).Within(100 / 1.4 * 0.006));
            Assert.That(StepPlan.StableSupply(c => 50, 100), Is.EqualTo(50).Within(0.3), "capacity that does not depend on the draw");
            Assert.That(StepPlan.StableSupply(c => 200, 100), Is.EqualTo(100), "never above the coldest capacity");
            Assert.That(StepPlan.StableSupply(c => 0, 100), Is.EqualTo(0));
            Assert.That(StepPlan.StableSupply(c => double.NaN, 100), Is.EqualTo(0));
        }

        [Test]
        public void TransmitterAtASliderTakesItsShareOfTheHotPlant()
        {
            // the test_beam_tx plant (mousefissionkspi, 493 MW thermal, generator 0.7, small radiator): its capacity at
            // the coldest radiators is far above what it gives at its working temperature, where KSPIE measures it
            var spec = new VesselSpec();
            spec.Reactors.Add(new ReactorSpec { ThermalMW = 493.18, HotBathK = 1693.76, MinimumThrottle = 0.0025 });
            spec.Generators.Add(new GeneratorSpec { Reactor = 0, Mode = GeneratorMode.Thermal, MaxEfficiency = 0.7 });
            spec.Radiators.Add(new RadiatorSpec { StefanArea = 1.0266729830751359E-12, MaxTemperatureK = 3200, EnvironmentMaxK = 3200, ExternalK = 4, Mass = 6 });
            var transmitter = new ConsumerDemand { DemandMW = 800, PlantSlider = 0.2, Priority = 2, WasteHeatFraction = 0.06 };
            var demands = new List<ConsumerDemand> { transmitter };
            double CapacityAt(double c)
            {
                transmitter.DemandMW = 800;
                PowerAllocation.CapByPlant(demands, c);
                var plan = StepPlan.Make(0, demands, 0, 0, false);
                return SteadyStateSolver.Solve(spec, plan.ReactorRequestMW, plan.ReactorExtraHeat).CapacityMW;
            }
            var maximum = SteadyStateSolver.MaximumElectricMW(spec);
            var stable = StepPlan.StableSupply(CapacityAt, maximum);
            Assert.That(stable, Is.LessThan(maximum / 2), "the working temperature, not the coldest");
            Assert.That(CapacityAt(stable), Is.EqualTo(stable).Within(stable * 0.01), "a steady state");
            Assert.That(transmitter.DemandMW, Is.EqualTo(0.2 * stable).Within(stable * 0.01));
        }

        // ---------------------------------------------------------------- billed-only power ratio

        [Test]
        public void BilledShareIsAveragedOverTime()
        {
            Assert.That(StepPlan.TimeAverage(0.8, 100, 0, 300), Is.EqualTo(0.2).Within(1e-12), "fuel ran out after a quarter of the time");
            Assert.That(StepPlan.TimeAverage(0.3, 0, 0.6, 50), Is.EqualTo(0.6).Within(1e-12), "the first step");
            Assert.That(StepPlan.TimeAverage(0.5, 100, 0.5, 0), Is.EqualTo(0.5).Within(1e-12));
            Assert.That(StepPlan.TimeAverage(double.NaN, 100, 1, 100), Is.EqualTo(0.5).Within(1e-12));
            Assert.That(StepPlan.TimeAverage(0.5, double.NaN, 1, 100), Is.EqualTo(1).Within(1e-12));
        }
    }
}
