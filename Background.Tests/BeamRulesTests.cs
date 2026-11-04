using FNPlugin.Background.Core;
using NUnit.Framework;

namespace Background.Tests
{
    /// <summary>Beamed power transmitter in the background (step A3): worn efficiency, wear rate, beam power.</summary>
    public class BeamRulesTests
    {
        [Test]
        public void WearLowersEfficiencyLinearlyUpToTheCap()
        {
            // diode array: 10 000 h life, 20 % lost at end of life, at most 90 %
            Assert.That(BeamRules.WornEfficiency(0.8, 0, 10000, 0.2, 0.9), Is.EqualTo(0.8));
            Assert.That(BeamRules.WornEfficiency(0.8, 5000, 10000, 0.2, 0.9), Is.EqualTo(0.8 * 0.9).Within(1e-12));
            Assert.That(BeamRules.WornEfficiency(0.8, 10000, 10000, 0.2, 0.9), Is.EqualTo(0.8 * 0.8).Within(1e-12));
            Assert.That(BeamRules.WornEfficiency(0.8, 1e9, 10000, 0.2, 0.9), Is.EqualTo(0.8 * 0.1).Within(1e-12), "capped at maxWearLoss");
        }

        [TestCase(0.0)]
        [TestCase(-5.0)]
        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        public void NoLifeMeansNoWear(double life)
        {
            Assert.That(BeamRules.WornEfficiency(0.8, 5000, life, 0.2, 0.9), Is.EqualTo(0.8));
        }

        [Test]
        public void BadEfficiencyIsClamped()
        {
            Assert.That(BeamRules.WornEfficiency(double.NaN, 0, 0, 0.2, 0.9), Is.EqualTo(0));
            Assert.That(BeamRules.WornEfficiency(1.7, 0, 0, 0.2, 0.9), Is.EqualTo(1));
            Assert.That(BeamRules.WornEfficiency(0.8, double.NaN, 10000, 0.2, 0.9), Is.EqualTo(0.8));
        }

        [Test]
        public void WearRateFollowsPowerAndHeat()
        {
            Assert.That(BeamRules.WearRate(1, 0, 3), Is.EqualTo(1));
            Assert.That(BeamRules.WearRate(0.3, 0, 3), Is.EqualTo(0.3).Within(1e-12));
            Assert.That(BeamRules.WearRate(1, 1, 3), Is.EqualTo(8).Within(1e-12), "x8 at a full waste heat bar");
            Assert.That(BeamRules.WearRate(2, 0.5, 3), Is.EqualTo(System.Math.Pow(2, 1.5)).Within(1e-12), "power fraction capped at 1");
            Assert.That(BeamRules.WearRate(double.NaN, 0.5, 3), Is.EqualTo(0));
            Assert.That(BeamRules.WearRate(1, double.NaN, double.NaN), Is.EqualTo(1));
        }

        [Test]
        public void BeamPowerIsKilowattsAfterEfficiency()
        {
            Assert.That(BeamRules.BeamKilowatts(4500, 0.6), Is.EqualTo(2_700_000).Within(1e-6));
            Assert.That(BeamRules.BeamKilowatts(-1, 0.6), Is.EqualTo(0));
            Assert.That(BeamRules.BeamKilowatts(double.NaN, 0.6), Is.EqualTo(0));
        }
    }
}
