using System;

namespace FNPlugin.Background.Core
{
    /// <summary>
    /// The player's rule for cryogenic tanks (todo/cooling-unification.md, decision 2), shared by the loaded cryostat,
    /// its Kerbalism background step and the VAB planner. No KSP types: unit-tested in Background.Tests.
    ///   - without cooling a tank loses <c>rate</c> % of what it holds per hour (exponential decay);
    ///   - cooling costs <see cref="CostFactor"/> × rate × amount / 1000 EC/s and stops the loss entirely;
    ///   - a step with less electric charge than the cooling needs cools that share and boils the rest.
    /// Rates come per part and resource from Patches/CryostatBoiloff.cfg; 0 = not cryogenic (no loss, no cost).
    /// </summary>
    public static class CryostatRule
    {
        /// <summary>EC/s per 1000 units for each %/h of boiloff (the player's 1.4).</summary>
        public const double CostFactor = 1.4;

        /// <summary>Stored amount at or below which the tank counts as empty: no cooling, no boiloff.</summary>
        public const double EmptyAmount = 0.0000001;

        /// <summary>Cooling cost of a tank holding <paramref name="amount"/>, EC/s.</summary>
        public static double CoolingEcRate(double ratePercentPerHour, double amount)
        {
            if (!(ratePercentPerHour > 0) || !(amount > EmptyAmount) || double.IsInfinity(amount) || double.IsInfinity(ratePercentPerHour))
                return 0;
            return CostFactor * ratePercentPerHour * amount / 1000;
        }

        /// <summary>Boiloff rate of an uncooled tank right now, units/s.</summary>
        public static double BoiloffPerSecond(double ratePercentPerHour, double amount)
        {
            if (!(ratePercentPerHour > 0) || !(amount > EmptyAmount) || double.IsInfinity(amount) || double.IsInfinity(ratePercentPerHour))
                return 0;
            return amount * ratePercentPerHour / 100 / 3600;
        }

        /// <summary>
        /// Units lost by an uncooled tank over <paramref name="seconds"/>: exponential, so a long background step never
        /// takes more than the tank holds.
        /// </summary>
        public static double BoiloffAmount(double ratePercentPerHour, double amount, double seconds)
        {
            if (!(seconds > 0) || double.IsInfinity(seconds))
                return 0;
            double perSecond = BoiloffPerSecond(ratePercentPerHour, amount);
            if (perSecond <= 0)
                return 0;
            return amount * (1 - Math.Exp(-ratePercentPerHour / 100 / 3600 * seconds));
        }

        public struct Step
        {
            /// <summary>Electric charge to draw, EC/s over the step.</summary>
            public double EcRate;

            /// <summary>Units the tank loses this step.</summary>
            public double Boiled;

            /// <summary>Share of the step the tank was cooled: 1 fully cooled; 0 cooling off, no charge or nothing to cool.</summary>
            public double CooledShare;

            /// <summary>Full cooling cost this step, EC/s (EcRate and CooledShare are derived from it).</summary>
            public double FullCost;
        }

        /// <summary>
        /// Seconds of cooling the stored charge must cover for full cooling when Kerbalism bills the step. Kerbalism nets
        /// the step's consumption against its production when it syncs, so stored charge is not the budget of a long
        /// step: its own cryo tanks keep cooling while the battery holds one second of the cost, and so does this rule
        /// (the same rule on every tank). A real shortage is the low-battery guard's business (CryoCooling).
        /// </summary>
        public const double KerbalismReserveSeconds = 1;

        /// <summary>
        /// One step of one tank. <paramref name="availableEc"/> — charge the vessel can give this tank: stored charge
        /// left after the earlier tanks of the step (<see cref="ChargeTally"/>), or what a stock request actually
        /// returned. Full cooling needs <paramref name="reserveSeconds"/> (capped at the step) of the cost; less cools
        /// that share and boils the rest. NaN or −∞ counts as no charge, +∞ as plenty.
        /// </summary>
        public static Step Advance(bool coolingOn, double ratePercentPerHour, double amount, double availableEc, double seconds,
            double reserveSeconds)
        {
            var step = new Step { FullCost = CoolingEcRate(ratePercentPerHour, amount) };
            if (step.FullCost <= 0 || !(seconds > 0) || double.IsInfinity(seconds))
                return step;
            double share = 0;
            if (coolingOn)
            {
                double need = step.FullCost * Math.Min(seconds, reserveSeconds > 0 ? reserveSeconds : seconds);
                double have = double.IsPositiveInfinity(availableEc) ? need
                    : double.IsNaN(availableEc) || double.IsInfinity(availableEc) ? 0 : Math.Max(0, availableEc);
                share = have >= need ? 1 : have / need;
            }
            step.CooledShare = share;
            step.EcRate = step.FullCost * share;
            step.Boiled = share >= 1 ? 0 : BoiloffAmount(ratePercentPerHour, amount, seconds) * (1 - share);
            return step;
        }
    }

    /// <summary>
    /// Stored charge already claimed by cryostats in the current Kerbalism step of one vessel. Kerbalism hands every
    /// module the vessel's charge as it was at the start of the step, so without this tally each tank would count the
    /// same charge as its own. Kerbalism steps one vessel at a time, each vessel once per step; a new vessel or a new
    /// time starts a new tally. (The shared dictionary Kerbalism passes is not written: other modules read it.)
    /// </summary>
    public sealed class ChargeTally
    {
        private Guid _vessel;
        private double _time = double.NaN;
        private double _claimed;

        /// <summary>Stored charge left for the next tank of this vessel's step.</summary>
        public double Available(Guid vessel, double time, double stored)
        {
            if (vessel != _vessel || !(time == _time))
            {
                _vessel = vessel;
                _time = time;
                _claimed = 0;
            }
            return stored - _claimed;
        }

        /// <summary>A tank of the current step took this much of the stored charge.</summary>
        public void Claim(double ec)
        {
            if (ec > 0 && !double.IsInfinity(ec))
                _claimed += ec;
        }
    }
}
