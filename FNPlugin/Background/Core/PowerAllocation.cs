using System;
using System.Collections.Generic;

namespace FNPlugin.Background.Core
{
    /// <summary>A KSPIE consumer of electric power (megajoules) on an unloaded vessel: refinery, drill, factory, transmitter.</summary>
    public sealed class ConsumerDemand
    {
        /// <summary>Power the consumer asks for, MW (its maximum at the player's power slider).</summary>
        public double DemandMW;
        /// <summary>KSPIE power priority (getPowerPriority): lower is served first.</summary>
        public int Priority;
        /// <summary>Fraction of the power it receives that it turns into waste heat (the metallic hydrogen factory: 0.999).</summary>
        public double WasteHeatFraction;
        /// <summary>
        /// Takes ElectricCharge from the battery for what the megajoule supply cannot give, as the loaded module does
        /// (the refinery and the antimatter collector always, the drill under 5 MW, the regolith collector under 2 MW;
        /// the factories and the beam transmitter never).
        /// </summary>
        public bool TakesBattery;
        /// <summary>
        /// The loaded module asks for slider × min(its capacity, the plant's stable supply) — the beam transmitter and
        /// the antimatter and metallic hydrogen factories (GetAvailableStableSupply): this is the slider (0..1), and
        /// <see cref="DemandMW"/> = slider × capacity is capped at slider × stable supply in the step
        /// (<see cref="PowerAllocation.CapByPlant"/>). Negative: not capped (the refinery asks for slider × capacity).
        /// </summary>
        public double PlantSlider = -1;
    }

    /// <summary>
    /// How the electricity of an unloaded vessel is shared, as KSPIE shares megajoules on a loaded one
    /// (ResourceManager.Update with MegajoulesResourceManager): the battery charge (ElectricCharge for the rest of the
    /// vessel) comes first — KSPIE converts megajoules to ElectricCharge at priorities 0 and 1, before any consumer of
    /// priority 2 or more —, then the consumers by priority, and within a priority the smallest request first, each
    /// served in full while power lasts (FnResourceFlowTypeSmallestFirst: consumers sorted by PowerDistributionPair,
    /// each gets min(supply left, its request)).
    /// </summary>
    public static class PowerAllocation
    {
        public sealed class Result
        {
            /// <summary>Electric power turned into ElectricCharge, MW.</summary>
            public double ElectricChargeMW;
            /// <summary>Power each consumer receives, MW (same order as the consumers given).</summary>
            public double[] ConsumerMW;
        }

        /// <summary>Indices of the consumers in serving order: priority, then the smaller request, then the given order.</summary>
        public static int[] ServingOrder(IList<ConsumerDemand> consumers)
        {
            var count = consumers?.Count ?? 0;
            var order = new int[count];
            for (var i = 0; i < count; i++) order[i] = i;
            Array.Sort(order, (a, b) =>
            {
                var ca = consumers[a];
                var cb = consumers[b];
                if (ca == null || cb == null) // nulls last
                    return ca == null ? (cb == null ? a.CompareTo(b) : 1) : -1;
                var byPriority = ca.Priority.CompareTo(cb.Priority);
                if (byPriority != 0) return byPriority;
                var byDemand = Clean(ca.DemandMW).CompareTo(Clean(cb.DemandMW));
                return byDemand != 0 ? byDemand : a.CompareTo(b);
            });
            return order;
        }

        /// <summary>
        /// Caps the demand of the consumers that scale with the plant (<see cref="ConsumerDemand.PlantSlider"/> ≥ 0) at
        /// slider × <paramref name="stableSupplyMW"/>: the most the vessel's generators and electric receivers can give,
        /// less the battery charge (KSPIE: StableResourceSupply − ResourceDemandHighPriority).
        /// </summary>
        public static void CapByPlant(IList<ConsumerDemand> consumers, double stableSupplyMW)
        {
            if (consumers == null) return;
            var stable = Clean(stableSupplyMW);
            foreach (var consumer in consumers)
                if (consumer != null && consumer.PlantSlider >= 0)
                    consumer.DemandMW = Math.Min(Clean(consumer.DemandMW), Math.Min(1, consumer.PlantSlider) * stable);
        }

        public static Result Allocate(double availableMW, double electricChargeRequestMW, IList<ConsumerDemand> consumers)
        {
            var count = consumers?.Count ?? 0;
            var result = new Result { ConsumerMW = new double[count] };
            var left = Clean(availableMW);

            result.ElectricChargeMW = Math.Min(left, Clean(electricChargeRequestMW));
            left -= result.ElectricChargeMW;
            if (count == 0) return result;

            foreach (var i in ServingOrder(consumers))
            {
                if (left <= 0) break;
                if (consumers[i] == null) continue;
                var served = Math.Min(left, Clean(consumers[i].DemandMW));
                result.ConsumerMW[i] = served;
                left = Math.Max(0, left - served);
            }
            return result;
        }

        /// <summary>
        /// Waste heat the consumers make when <paramref name="availableMW"/> is shared as <see cref="Allocate"/> does, MW:
        /// what the solver adds to the plant's heat for the power it delivers.
        /// </summary>
        public static double ConsumerHeat(double availableMW, double electricChargeRequestMW, IList<ConsumerDemand> consumers)
        {
            var result = Allocate(availableMW, electricChargeRequestMW, consumers);
            var heat = 0.0;
            for (var i = 0; i < result.ConsumerMW.Length; i++)
                if (consumers[i] != null)
                    heat += result.ConsumerMW[i] * Clamp01(consumers[i].WasteHeatFraction);
            return heat;
        }

        /// <summary>
        /// What the generators could not give, the consumers that take ElectricCharge for it (TakesBattery) get from
        /// <paramref name="batteryMW"/> in the same serving order; added to <paramref name="allocation"/>. Returns the
        /// total taken from the battery, MW.
        /// </summary>
        public static double TopUpFromBattery(Result allocation, IList<ConsumerDemand> consumers, double batteryMW)
        {
            var left = Clean(batteryMW);
            var drawn = 0.0;
            if (allocation == null || consumers == null) return 0;
            foreach (var i in ServingOrder(consumers))
            {
                if (left <= 0) break;
                var consumer = consumers[i];
                if (consumer == null || !consumer.TakesBattery || i >= allocation.ConsumerMW.Length) continue;
                var shortfall = Math.Max(0, Clean(consumer.DemandMW) - allocation.ConsumerMW[i]);
                var taken = Math.Min(left, shortfall);
                if (taken <= 0) continue;
                allocation.ConsumerMW[i] += taken;
                left = Math.Max(0, left - taken);
                drawn += taken;
            }
            return drawn;
        }

        /// <summary>Total demand of the consumers, MW.</summary>
        public static double TotalDemand(IList<ConsumerDemand> consumers)
        {
            var total = 0.0;
            if (consumers == null) return total;
            foreach (var consumer in consumers)
                if (consumer != null) total += Clean(consumer.DemandMW);
            return total;
        }

        private static double Clean(double value) => double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? 0 : value;

        private static double Clamp01(double value) => double.IsNaN(value) ? 0 : value < 0 ? 0 : value > 1 ? 1 : value;
    }
}
