using FNPlugin.Background.Core;
using NUnit.Framework;

namespace Background.Tests
{
    [TestFixture]
    public class ElectricDemandTests
    {
        /// <summary>Kerbalism's step for one vessel: our production and the rest of the vessel's use, clipped to the battery.</summary>
        private static double Step(double amount, double capacity, double produced, double consumed, double elapsed)
        {
            var next = amount + (produced - consumed) * elapsed;
            return next < 0 ? 0 : next > capacity ? capacity : next;
        }

        [Test]
        public void FirstStep_OnlyRefillsTowardsTheTarget()
        {
            var demand = new ElectricDemand();
            Assert.That(demand.Request(amount: 0, capacity: 1000, elapsedSeconds: 10), Is.EqualTo(98).Within(1e-9));
        }

        [Test]
        public void LearnsTheConsumptionAndHoldsTheBatteryBelowFull()
        {
            const double capacity = 1000, consumption = 5, elapsed = 10;
            var demand = new ElectricDemand();
            var amount = 500.0;

            for (var i = 0; i < 50; i++)
            {
                var request = demand.Request(amount, capacity, elapsed);
                demand.Produced(amount, request, elapsed);
                amount = Step(amount, capacity, request, consumption, elapsed);
            }

            Assert.That(demand.ConsumptionPerSecond, Is.EqualTo(consumption).Within(1e-9));
            Assert.That(amount, Is.EqualTo(ElectricDemand.TargetLevel * capacity).Within(1e-6));
            Assert.That(demand.Request(amount, capacity, elapsed), Is.EqualTo(consumption).Within(1e-6),
                "at the target level the generators make exactly what the vessel uses");
        }

        [Test]
        public void ProductionLimitedBelowTheRequest_StillMeasuresTheConsumption()
        {
            const double capacity = 1000, consumption = 50, elapsed = 10, limit = 20;
            var demand = new ElectricDemand();
            var amount = 900.0;

            for (var i = 0; i < 3; i++)
            {
                var produced = System.Math.Min(limit, demand.Request(amount, capacity, elapsed));
                demand.Produced(amount, produced, elapsed);
                amount = Step(amount, capacity, produced, consumption, elapsed);
            }

            demand.Request(amount, capacity, elapsed);
            Assert.That(demand.ConsumptionPerSecond, Is.EqualTo(consumption).Within(1e-9));
        }

        [Test]
        public void SolarSurplus_NoGeneratorPower()
        {
            const double capacity = 1000, elapsed = 10;
            var demand = new ElectricDemand();
            var amount = 970.0;

            for (var i = 0; i < 20; i++)
            {
                var request = demand.Request(amount, capacity, elapsed);
                demand.Produced(amount, request, elapsed);
                amount = Step(amount, capacity, request, consumed: -3, elapsed: elapsed); // panels give 3 EC/s more than used
            }

            Assert.That(demand.Request(amount, capacity, elapsed), Is.EqualTo(0));
        }
    }
}
