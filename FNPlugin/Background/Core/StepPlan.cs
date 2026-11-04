using System;
using System.Collections.Generic;

namespace FNPlugin.Background.Core
{
    /// <summary>
    /// Who powers what in one background step of a vessel (InterstellarBackgroundPower.Step): the electric beamed power
    /// receivers' megajoules are free and used first — the battery charge, then the consumers —, the reactors make the
    /// rest; the waste heat the plant must shed besides its own is the consumers' heat for the power they get (served as
    /// PowerAllocation does) and the receivers' conversion heat in proportion to their use.
    /// </summary>
    public static class StepPlan
    {
        public sealed class Plan
        {
            /// <summary>Electric power the receivers give, MW, and its share of what they could give.</summary>
            public double ReceiverUsedMW, ReceiverShare;
            /// <summary>Electric power asked of the reactors, MW.</summary>
            public double ReactorRequestMW;
            /// <summary>Heat made elsewhere when the reactors deliver a given MW (for SteadyStateSolver.Solve).</summary>
            public Func<double, double> ReactorExtraHeat;
            /// <summary>Total power to share (receivers' used + what the reactors deliver), MW.</summary>
            public double SupplyMW(double reactorElectricMW) => ReceiverUsedMW + Clean(reactorElectricMW);
        }

        /// <param name="electricChargeMW">The battery charge the rest of the vessel needs, MW (ElectricDemand / 1000).</param>
        /// <param name="consumers">The consumers' demands.</param>
        /// <param name="receiverMW">What the electric receivers can give, MW.</param>
        /// <param name="receiverHeatMW">Their conversion heat at full use, MW.</param>
        /// <param name="freePower">The infinite electricity cheat: the consumers get their power from nowhere.</param>
        public static Plan Make(double electricChargeMW, IList<ConsumerDemand> consumers, double receiverMW, double receiverHeatMW, bool freePower)
        {
            var charge = Clean(electricChargeMW);
            var demand = freePower ? 0 : PowerAllocation.TotalDemand(consumers);
            var receivers = Clean(receiverMW);
            var used = Math.Min(receivers, charge + demand);
            var share = receivers > 0 ? used / receivers : 0;
            var receiverHeat = Clean(receiverHeatMW) * share;
            return new Plan
            {
                ReceiverUsedMW = used,
                ReceiverShare = share,
                ReactorRequestMW = Math.Max(0, charge + demand - used),
                ReactorExtraHeat = freePower
                    ? (Func<double, double>)(electric => receiverHeat)
                    : electric => PowerAllocation.ConsumerHeat(Clean(electric) + used, charge, consumers) + receiverHeat
            };
        }

        /// <summary>
        /// KSPIE's stable supply on a loaded vessel is the generators' maximum at the radiators' current temperature, and
        /// the consumers that scale with it (ConsumerDemand.PlantSlider) ask for slider × it: the steady state is the
        /// capacity c at which the plant, with those consumers asking for slider × c, settles at a temperature where its
        /// capacity is c. <paramref name="capacityAt"/>(c) = the generators' capacity at that operating point; it falls as
        /// c grows (more power, hotter radiators), so the root of capacityAt(c) − c is found between 0 and
        /// <paramref name="maximum"/> (the coldest radiators) by regula falsi (Illinois), to a relative
        /// <paramref name="tolerance"/> or after <paramref name="iterations"/> calls.
        /// </summary>
        public static double StableSupply(Func<double, double> capacityAt, double maximum, double tolerance = 0.005, int iterations = 12)
        {
            if (capacityAt == null) throw new ArgumentNullException(nameof(capacityAt));
            double lo = 0, hi = Clean(maximum);
            var hLo = Clean(capacityAt(lo));
            if (hLo <= 0 || hi <= 0) return 0;
            var hHi = Clean(capacityAt(hi)) - hi;
            if (hHi >= 0) return hi;

            var c = lo;
            var side = 0;
            for (var i = 0; i < iterations; i++)
            {
                c = (lo * hHi - hi * hLo) / (hHi - hLo);
                if (double.IsNaN(c) || c <= lo || c >= hi) c = (lo + hi) / 2;
                var h = Clean(capacityAt(c)) - c;
                if (Math.Abs(h) <= tolerance * c) return c;
                if (h > 0)
                {
                    lo = c;
                    hLo = h;
                    if (side == 1) hHi /= 2;
                    side = 1;
                }
                else
                {
                    hi = c;
                    hHi = h;
                    if (side == -1) hLo /= 2;
                    side = -1;
                }
            }
            return c;
        }

        /// <summary>
        /// Time-weighted average of a share: <paramref name="average"/> kept over <paramref name="seconds"/>, then
        /// <paramref name="share"/> for <paramref name="elapsed"/> (a billed-only consumer's power ratio over the time
        /// its catch-up will cover). NaN, infinite and negative inputs count as 0.
        /// </summary>
        public static double TimeAverage(double average, double seconds, double share, double elapsed)
        {
            var before = Clean(seconds);
            var step = Clean(elapsed);
            var total = before + step;
            return total > 0 ? (Clean(average) * before + Clean(share) * step) / total : Clean(share);
        }

        private static double Clean(double value) => double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? 0 : value;
    }

    /// <summary>
    /// A "recipe" consumer's step (drill, regolith collector, factories, antimatter collector): what it takes and makes
    /// per second at full power, scaled by the share of its power it received and by the inputs there are.
    /// </summary>
    public static class RecipeStep
    {
        /// <summary>Runs the recipe on <paramref name="ledger"/>; returns the scale it ran at (0..1).</summary>
        public static double Run(ResourceLedger ledger, IList<KeyValuePair<string, double>> inputs, IList<KeyValuePair<string, double>> outputs,
            double share, double minimumShare, double elapsedSeconds)
        {
            if (ledger == null || !(elapsedSeconds > 0) || double.IsInfinity(elapsedSeconds)) return 0;
            var scale = double.IsNaN(share) ? 0 : Math.Max(0, Math.Min(1, share));
            if (scale <= 0 || scale < minimumShare) return 0;

            if (inputs != null)
                foreach (var input in inputs)
                {
                    var need = input.Value * scale * elapsedSeconds;
                    if (need > 0)
                        scale = Math.Min(scale, scale * ledger.Amount(input.Key) / need);
                }
            if (!(scale > 0)) return 0;

            if (inputs != null)
                foreach (var input in inputs)
                    ledger.Take(input.Key, input.Value * scale * elapsedSeconds);
            if (outputs != null)
                foreach (var output in outputs)
                    ledger.Store(output.Key, output.Value * scale * elapsedSeconds);
            return scale;
        }
    }

    /// <summary>
    /// What each unloaded receiver takes from each transmitter, as it last computed it. Kerbalism runs unloaded vessels
    /// one at a time, each at its own time, so a receiver counts the others' last intake against the transmitter — as a
    /// loaded receiver counts the other loaded ones by their current received power
    /// (BeamedPowerReceiver.GetEnumeratedPowerFromSatelliteForAllLoadedVessels: CurrentRecievedPower, after efficiency).
    /// </summary>
    public sealed class BeamUsage<TReceiver>
    {
        private readonly Dictionary<TReceiver, Dictionary<Guid, double>> _byReceiver = new Dictionary<TReceiver, Dictionary<Guid, double>>();

        /// <summary>Received power of the other receivers from <paramref name="transmitter"/>, MW; <paramref name="counts"/> says which receivers still count.</summary>
        public double UsedByOthers(Guid transmitter, TReceiver self, Func<TReceiver, bool> counts)
        {
            var used = 0.0;
            foreach (var entry in _byReceiver)
            {
                if (EqualityComparer<TReceiver>.Default.Equals(entry.Key, self)) continue;
                if (counts != null && !counts(entry.Key)) continue;
                if (entry.Value.TryGetValue(transmitter, out var mw) && mw > 0 && !double.IsInfinity(mw))
                    used += mw;
            }
            return used;
        }

        /// <summary>Replaces the receiver's intake (MW received per transmitter).</summary>
        public void Set(TReceiver receiver, IEnumerable<KeyValuePair<Guid, double>> received)
        {
            var map = new Dictionary<Guid, double>();
            if (received != null)
                foreach (var entry in received)
                {
                    if (double.IsNaN(entry.Value) || double.IsInfinity(entry.Value) || entry.Value <= 0) continue;
                    map.TryGetValue(entry.Key, out var sum);
                    map[entry.Key] = sum + entry.Value;
                }
            _byReceiver[receiver] = map;
        }

        public void Forget(TReceiver receiver) => _byReceiver.Remove(receiver);

        /// <summary>Drops the receivers <paramref name="gone"/> says no longer exist.</summary>
        public void ForgetWhere(Func<TReceiver, bool> gone)
        {
            if (gone == null) return;
            var dead = new List<TReceiver>();
            foreach (var receiver in _byReceiver.Keys)
                if (gone(receiver)) dead.Add(receiver);
            foreach (var receiver in dead) _byReceiver.Remove(receiver);
        }
    }
}
