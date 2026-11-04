using System;
using System.Collections.Generic;

namespace FNPlugin.Background.Core
{
    /// <summary>
    /// The stock of an unloaded vessel during one Kerbalism background step, as the KSPIE consumers (refinery
    /// activities, drills, factories) see it: the amounts Kerbalism had at the start of the step, the capacities of
    /// the vessel's flow-enabled storage, and what the consumers already took or stored in this step. Requests follow
    /// KSP's Part.RequestResource: a positive demand takes at most what is there, a negative one stores at most what
    /// fits (the rest is lost, as overflow is in the game). <see cref="Changes"/> are the totals to hand to Kerbalism
    /// (divided by the step length: rates).
    /// Several consumers of one vessel run one after another on the same ledger, so the second sees what the first left.
    /// </summary>
    public sealed class ResourceLedger
    {
        private readonly Dictionary<string, double> _amount = new Dictionary<string, double>();
        private readonly IDictionary<string, double> _start;
        private readonly IDictionary<string, double> _capacity;
        private readonly Dictionary<string, double> _changes = new Dictionary<string, double>();

        /// <param name="amounts">Amounts at the start of the step (Kerbalism's availableResources); not modified.</param>
        /// <param name="capacities">Capacity of every resource; a resource missing here has no room.</param>
        public ResourceLedger(IDictionary<string, double> amounts, IDictionary<string, double> capacities)
        {
            _start = amounts ?? new Dictionary<string, double>();
            _capacity = capacities ?? new Dictionary<string, double>();
        }

        public IEnumerable<KeyValuePair<string, double>> Changes => _changes;

        /// <summary>
        /// A ledger starting from this one's current amounts, for one consumer: <see cref="Absorb"/> its changes when
        /// the consumer finished, drop it when it failed (nothing it did half-way reaches the vessel).
        /// </summary>
        public ResourceLedger Fork()
        {
            var amounts = new Dictionary<string, double>(_start);
            foreach (var amount in _amount)
                amounts[amount.Key] = amount.Value;
            return new ResourceLedger(amounts, _capacity);
        }

        public void Absorb(ResourceLedger child)
        {
            if (child == null) return;
            foreach (var change in child._changes)
                if (change.Value != 0)
                    Change(change.Key, change.Value);
        }

        public double Amount(string resource)
        {
            if (resource == null) return 0;
            if (_amount.TryGetValue(resource, out var amount)) return amount;
            _start.TryGetValue(resource, out amount);
            amount = Clean(amount);
            _amount[resource] = amount;
            return amount;
        }

        public double Capacity(string resource)
        {
            if (resource == null || !_capacity.TryGetValue(resource, out var capacity)) return 0;
            // a stock above the capacity (Kerbalism counts storage the snapshot reading may not) is room-less, not negative
            return Math.Max(Clean(capacity), Amount(resource));
        }

        public double Room(string resource) => Math.Max(0, Capacity(resource) - Amount(resource));

        /// <summary>KSP convention: demand &gt; 0 takes (returns what was taken), demand &lt; 0 stores (returns minus what was stored).</summary>
        public double Request(string resource, double demand)
        {
            if (resource == null || double.IsNaN(demand) || double.IsInfinity(demand) || demand == 0) return 0;
            return demand > 0 ? Take(resource, demand) : -Store(resource, -demand);
        }

        public double Take(string resource, double units)
        {
            if (resource == null || !(units > 0) || double.IsInfinity(units)) return 0;
            var taken = Math.Min(units, Amount(resource));
            if (taken <= 0) return 0;
            Change(resource, -taken);
            return taken;
        }

        public double Store(string resource, double units)
        {
            if (resource == null || !(units > 0) || double.IsInfinity(units)) return 0;
            var stored = Math.Min(units, Room(resource));
            if (stored <= 0) return 0;
            Change(resource, stored);
            return stored;
        }

        private void Change(string resource, double delta)
        {
            _amount[resource] = Math.Max(0, Amount(resource) + delta);
            _changes.TryGetValue(resource, out var total);
            _changes[resource] = total + delta;
        }

        private static double Clean(double value) => double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? 0 : value;
    }
}
