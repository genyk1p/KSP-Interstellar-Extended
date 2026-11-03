using System;
using FNPlugin.Background.Core;
using NUnit.Framework;

namespace Background.Tests
{
    /// <summary>Battery clipped at empty or full, changing consumption, bad numbers (review of 2026-11-03).</summary>
    [TestFixture]
    public class ElectricDemandEdgeTests
    {
        private static double Step(double amount, double capacity, double produced, double consumed, double elapsed)
        {
            var next = amount + (produced - consumed) * elapsed;
            return next < 0 ? 0 : next > capacity ? capacity : next;
        }

        private static double Run(ElectricDemand demand, ref double amount, double capacity, Func<int, double> consumption,
            double elapsed, int steps, double productionLimit = double.MaxValue)
        {
            var produced = 0.0;
            for (var i = 0; i < steps; i++)
            {
                produced = Math.Min(productionLimit, demand.Request(amount, capacity, elapsed));
                demand.Produced(amount, produced, elapsed);
                amount = Step(amount, capacity, produced, consumption(i), elapsed);
            }
            return produced;
        }

        [Test]
        public void EmptyBattery_LearnsTheConsumption()
        {
            // review: one high-warp step can empty the battery before anything was measured
            const double capacity = 2000, elapsed = 250;
            var demand = new ElectricDemand();
            var amount = 1960.0;
            Run(demand, ref amount, capacity, _ => 10, elapsed, 30);

            Assert.That(demand.ConsumptionPerSecond, Is.EqualTo(10).Within(1e-6));
            Assert.That(amount, Is.EqualTo(ElectricDemand.TargetLevel * capacity).Within(1e-3));
        }

        [Test]
        public void ConsumptionRises_FollowsAfterEmptying()
        {
            const double capacity = 1000, elapsed = 100;
            var demand = new ElectricDemand();
            var amount = 500.0;
            Run(demand, ref amount, capacity, i => i < 20 ? 5 : 50, elapsed, 60);

            Assert.That(demand.ConsumptionPerSecond, Is.EqualTo(50).Within(1e-6));
            Assert.That(amount, Is.EqualTo(ElectricDemand.TargetLevel * capacity).Within(1e-3));
        }

        [Test]
        public void ConsumptionDrops_FollowsAfterFilling()
        {
            // review: a solar vessel entering sunlight, a Kerbalism process stopping
            const double capacity = 1000, elapsed = 10;
            var demand = new ElectricDemand();
            var amount = 500.0;
            Run(demand, ref amount, capacity, i => i < 30 ? 50 : 1, elapsed, 60);

            Assert.That(demand.ConsumptionPerSecond, Is.EqualTo(1).Within(1e-6));
            Assert.That(amount, Is.LessThan(capacity));
            Assert.That(demand.Request(amount, capacity, elapsed), Is.EqualTo(1).Within(1e-3));
        }

        [Test]
        public void GeneratorsAtTheirLimit_EstimateDoesNotGrowWithoutEnd()
        {
            const double capacity = 1000, elapsed = 10;
            var demand = new ElectricDemand();
            var amount = 900.0;
            Run(demand, ref amount, capacity, _ => 50, elapsed, 100, productionLimit: 20);

            Assert.That(amount, Is.EqualTo(0));
            Assert.That(demand.ConsumptionPerSecond, Is.EqualTo(50).Within(1e-6),
                "measured before the battery ran empty; an empty battery with the generators at their limit adds nothing");
        }

        [Test]
        public void BadNumbers_NeverReachTheRequest()
        {
            var demand = new ElectricDemand();
            Assert.That(demand.Request(double.NaN, 1000, 10), Is.EqualTo(0));
            Assert.That(demand.Request(500, double.PositiveInfinity, 10), Is.EqualTo(0));
            Assert.That(demand.Request(500, 1000, double.NaN), Is.EqualTo(0));

            demand.Produced(500, double.NaN, 10);
            var request = demand.Request(400, 1000, 10);
            Assert.That(double.IsNaN(request) || double.IsInfinity(request), Is.False);
            demand.Produced(400, request, 10);
            request = demand.Request(450, 1000, 10);
            Assert.That(double.IsNaN(request) || double.IsInfinity(request), Is.False);
        }

        [TestCase(37, 500, 2000)]
        [TestCase(10, 200, 20000)]
        [TestCase(100, 1000, 20000)]
        public void SmallBatteryLongStep_SettlesInsteadOfAlternating(double consumption, double capacity, double elapsed)
        {
            // second review: ×2 / ×½ alone kept alternating empty/full for hundreds of steps
            var demand = new ElectricDemand();
            var amount = capacity / 2;
            var emptySteps = 0;
            double produced = 0, used = 0;
            for (var i = 0; i < 60; i++)
            {
                var request = demand.Request(amount, capacity, elapsed);
                demand.Produced(amount, request, elapsed);
                amount = Step(amount, capacity, request, consumption, elapsed);
                if (amount <= 0) emptySteps++;
                if (i >= 20) { produced += request; used += consumption; }
            }

            Assert.That(emptySteps, Is.LessThanOrEqualTo(12), "steps the vessel spent without electricity");
            Assert.That(demand.ConsumptionPerSecond, Is.EqualTo(consumption).Within(1e-6));
            Assert.That(produced / used, Is.EqualTo(1).Within(0.01), "no fuel burnt for electricity that is thrown away");
        }
    }
}
