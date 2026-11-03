using System;
using FNPlugin.Background.Core;
using NUnit.Framework;

namespace Background.Tests
{
    /// <summary>The player's cryo rule (todo/cooling-unification.md): cost 1.4 × rate × amount / 1000, loss rate %/h.</summary>
    public class CryostatRuleTests
    {
        [Test]
        public void CostIsThePlayersFormula()
        {
            // 1000 units of LH2 at 0.05 %/h: 1.4 × 0.05 × 1000 / 1000 = 0.07 EC/s
            Assert.That(CryostatRule.CoolingEcRate(0.05, 1000), Is.EqualTo(0.07).Within(1e-12));
            Assert.That(CryostatRule.CoolingEcRate(2.0, 3000), Is.EqualTo(8.4).Within(1e-12));
        }

        [TestCase(0, 1000)]
        [TestCase(0.05, 0)]
        [TestCase(0.05, 1e-8)]
        [TestCase(-1, 1000)]
        [TestCase(double.NaN, 1000)]
        [TestCase(0.05, double.NaN)]
        [TestCase(0.05, double.PositiveInfinity)]
        public void NothingToCoolCostsNothingAndLosesNothing(double rate, double amount)
        {
            Assert.That(CryostatRule.CoolingEcRate(rate, amount), Is.EqualTo(0));
            Assert.That(CryostatRule.BoiloffPerSecond(rate, amount), Is.EqualTo(0));
            Assert.That(CryostatRule.BoiloffAmount(rate, amount, 3600), Is.EqualTo(0));
            var step = CryostatRule.Advance(false, rate, amount, 1e9, 3600, CryostatRule.KerbalismReserveSeconds);
            Assert.That(step.Boiled, Is.EqualTo(0));
            Assert.That(step.EcRate, Is.EqualTo(0));
        }

        [Test]
        public void UncooledTankLosesTheRatePerHour()
        {
            // 0.2 %/h of 2266 over an hour ≈ 4.53 (the Thor tank of the CryoCooling test)
            Assert.That(CryostatRule.BoiloffAmount(0.2, 2266, 3600), Is.EqualTo(2266 * (1 - Math.Exp(-0.002))).Within(1e-9));
            Assert.That(CryostatRule.BoiloffAmount(0.2, 2266, 3600), Is.EqualTo(4.53).Within(0.01));
        }

        [Test]
        public void LongStepNeverTakesMoreThanTheTankHolds()
        {
            Assert.That(CryostatRule.BoiloffAmount(48, 1000, 1e9), Is.LessThanOrEqualTo(1000));
            Assert.That(CryostatRule.BoiloffAmount(48, 1000, 1e9), Is.GreaterThan(999.999));
        }

        [Test]
        public void ManySmallStepsMatchOneLongStep()
        {
            double amount = 5000, step = 0.02, seconds = 0;
            while (seconds < 3600)
            {
                amount -= CryostatRule.BoiloffAmount(2.0, amount, step);
                seconds += step;
            }
            Assert.That(amount, Is.EqualTo(5000 - CryostatRule.BoiloffAmount(2.0, 5000, seconds)).Within(1e-6));
        }

        private const double Kerbalism = CryostatRule.KerbalismReserveSeconds;

        [Test]
        public void EnoughChargeCoolsFully()
        {
            var s = CryostatRule.Advance(true, 0.05, 1000, 1000, 10, Kerbalism);
            Assert.That(s.CooledShare, Is.EqualTo(1));
            Assert.That(s.EcRate, Is.EqualTo(0.07).Within(1e-12));
            Assert.That(s.Boiled, Is.EqualTo(0));
            Assert.That(s.FullCost, Is.EqualTo(0.07).Within(1e-12));
        }

        [Test]
        public void OffTankBoilsAndDrawsNothing()
        {
            var s = CryostatRule.Advance(false, 0.05, 1000, 1000, 3600, Kerbalism);
            Assert.That(s.CooledShare, Is.EqualTo(0));
            Assert.That(s.EcRate, Is.EqualTo(0));
            Assert.That(s.Boiled, Is.EqualTo(CryostatRule.BoiloffAmount(0.05, 1000, 3600)).Within(1e-12));
            Assert.That(s.FullCost, Is.EqualTo(0.07).Within(1e-12));
        }

        [Test]
        public void LongKerbalismStepWithSmallBatteryStillCools()
        {
            // 10 000 s at 0.07 EC/s is 700 EC, the battery holds 1 EC: Kerbalism nets the step against production, so
            // like its own cryo tanks one second of cost in store is enough (a real shortage is the guard's business)
            var s = CryostatRule.Advance(true, 0.05, 1000, 1, 10000, Kerbalism);
            Assert.That(s.CooledShare, Is.EqualTo(1));
            Assert.That(s.Boiled, Is.EqualTo(0));
        }

        [Test]
        public void ChargeBelowOneSecondCoolsItsShareAndBoilsTheRest()
        {
            // one second of cooling is 0.07 EC; 0.035 EC stored → half cooled, half the loss
            var s = CryostatRule.Advance(true, 0.05, 1000, 0.035, 100, Kerbalism);
            Assert.That(s.CooledShare, Is.EqualTo(0.5).Within(1e-12));
            Assert.That(s.EcRate, Is.EqualTo(0.035).Within(1e-12));
            Assert.That(s.Boiled, Is.EqualTo(CryostatRule.BoiloffAmount(0.05, 1000, 100) * 0.5).Within(1e-12));
        }

        [Test]
        public void StockStepNeedsTheWholeStepDrawn()
        {
            // stock requests: the charge passed is what was actually drawn for the step — half of 7 EC → half cooled
            var s = CryostatRule.Advance(true, 0.05, 1000, 3.5, 100, 100);
            Assert.That(s.CooledShare, Is.EqualTo(0.5).Within(1e-12));
            Assert.That(s.Boiled, Is.EqualTo(CryostatRule.BoiloffAmount(0.05, 1000, 100) * 0.5).Within(1e-12));
        }

        [Test]
        public void ShortStepNeedsOnlyItsOwnLength()
        {
            // a 0.02 s physics step needs 0.0014 EC, not a whole second's worth
            var s = CryostatRule.Advance(true, 0.05, 1000, 0.0014, 0.02, Kerbalism);
            Assert.That(s.CooledShare, Is.EqualTo(1).Within(1e-9));
        }

        [Test]
        public void PlentyOfChargeIsFullCooling()
        {
            var s = CryostatRule.Advance(true, 0.05, 1000, double.PositiveInfinity, 100, Kerbalism);
            Assert.That(s.CooledShare, Is.EqualTo(1));
            Assert.That(s.Boiled, Is.EqualTo(0));
        }

        [TestCase(0)]
        [TestCase(-5)]
        [TestCase(double.NaN)]
        [TestCase(double.NegativeInfinity)]
        public void NoChargeIsNoCooling(double available)
        {
            var s = CryostatRule.Advance(true, 0.05, 1000, available, 100, Kerbalism);
            Assert.That(s.CooledShare, Is.EqualTo(0));
            Assert.That(s.EcRate, Is.EqualTo(0));
            Assert.That(s.Boiled, Is.GreaterThan(0));
        }

        [Test]
        public void LongStepWithPartialChargeNeverTakesMoreThanTheTankHolds()
        {
            var s = CryostatRule.Advance(true, 48, 1000, 0.1, 1e9, Kerbalism);
            Assert.That(s.Boiled, Is.LessThanOrEqualTo(1000));
            Assert.That(s.Boiled, Is.GreaterThan(0));
        }

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        public void BadStepDoesNothing(double seconds)
        {
            var s = CryostatRule.Advance(true, 0.05, 1000, 1000, seconds, Kerbalism);
            Assert.That(s.EcRate, Is.EqualTo(0));
            Assert.That(s.Boiled, Is.EqualTo(0));
        }

        // ---------------------------------------------------------------- ChargeTally

        [Test]
        public void TanksOfOneStepShareTheStoredCharge()
        {
            var tally = new ChargeTally();
            var v = Guid.NewGuid();
            Assert.That(tally.Available(v, 100, 10), Is.EqualTo(10));
            tally.Claim(7);
            Assert.That(tally.Available(v, 100, 10), Is.EqualTo(3), "the second tank gets what the first left");
            tally.Claim(3);
            Assert.That(tally.Available(v, 100, 10), Is.EqualTo(0));
        }

        [Test]
        public void NewVesselStartsANewTally()
        {
            var tally = new ChargeTally();
            tally.Available(Guid.NewGuid(), 100, 10);
            tally.Claim(7);
            Assert.That(tally.Available(Guid.NewGuid(), 100, 10), Is.EqualTo(10));
        }

        [Test]
        public void NewStepStartsANewTally()
        {
            var tally = new ChargeTally();
            var v = Guid.NewGuid();
            tally.Available(v, 100, 10);
            tally.Claim(7);
            Assert.That(tally.Available(v, 100.02, 10), Is.EqualTo(10));
        }

        [TestCase(-1)]
        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        public void BadClaimIsIgnored(double ec)
        {
            var tally = new ChargeTally();
            var v = Guid.NewGuid();
            tally.Available(v, 100, 10);
            tally.Claim(ec);
            Assert.That(tally.Available(v, 100, 10), Is.EqualTo(10));
        }

        [Test]
        public void TwoTanksWithEnoughForOneSecondEachAreBothCooled()
        {
            // the tally claims one second of cost per tank: a 1 EC battery cools two tanks of 0.4 EC/s for a long step
            var tally = new ChargeTally();
            var v = Guid.NewGuid();
            var a = CryostatRule.Advance(true, 0.4, 714.2857142857143, tally.Available(v, 5, 1), 1000, Kerbalism);
            tally.Claim(a.EcRate * Math.Min(1000, Kerbalism));
            var b = CryostatRule.Advance(true, 0.4, 714.2857142857143, tally.Available(v, 5, 1), 1000, Kerbalism);
            tally.Claim(b.EcRate * Math.Min(1000, Kerbalism));
            Assert.That(a.CooledShare, Is.EqualTo(1));
            Assert.That(b.CooledShare, Is.EqualTo(1));
            // 1 − 0.4 − 0.4 = 0.2 EC left: a third tank gets half a second's worth
            var c = CryostatRule.Advance(true, 0.4, 714.2857142857143, tally.Available(v, 5, 1), 1000, Kerbalism);
            Assert.That(c.CooledShare, Is.EqualTo(0.5).Within(1e-9), "a third tank finds the store claimed");
        }
    }
}
