using System;

namespace FNPlugin.Background.Core
{
    /// <summary>
    /// Beamed power transmitter on an unloaded vessel (step A3), as the loaded transmitter and beam generator compute
    /// it (BeamedPowerTransmitter.FixedUpdate, BeamGenerator wear — fork addition, Beamedpower/BeamGeneratorWear.cs).
    /// </summary>
    public static class BeamRules
    {
        /// <summary>
        /// Efficiency of a worn beam generator: base × (1 − min(maxLoss, endLoss × hours / life)); no wear when the
        /// life is 0 (wear off for this generator type).
        /// </summary>
        public static double WornEfficiency(double baseEfficiency, double wearHours, double lifeHours, double endOfLifeLoss, double maxWearLoss)
        {
            var efficiency = Clamp01(baseEfficiency);
            if (!(lifeHours > 0) || double.IsInfinity(lifeHours) || !(wearHours > 0) || double.IsInfinity(wearHours))
                return efficiency;
            var loss = Math.Min(Clamp01(maxWearLoss), Math.Max(0, Clean(endOfLifeLoss)) * wearHours / lifeHours);
            return efficiency * (1 - Clamp01(loss));
        }

        /// <summary>Full-power hours per hour: power fraction × 2^(doublings × waste heat fill) (BeamGenerator.AddWear).</summary>
        public static double WearRate(double powerFraction, double wasteHeatFill, double heatWearDoublings)
        {
            var rate = Clamp01(powerFraction) * Math.Pow(2, Clean(heatWearDoublings) * Clamp01(wasteHeatFill));
            return double.IsNaN(rate) || double.IsInfinity(rate) ? 0 : rate;
        }

        /// <summary>Beam power the transmitter sends, in kW as KSPIE keeps it (nuclear_power): 1000 × efficiency × MW received.</summary>
        public static double BeamKilowatts(double receivedMW, double efficiency) => 1000 * Clean(receivedMW) * Clamp01(efficiency);

        private static double Clean(double value) => double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? 0 : value;

        private static double Clamp01(double value) => double.IsNaN(value) ? 0 : value < 0 ? 0 : value > 1 ? 1 : value;
    }
}
