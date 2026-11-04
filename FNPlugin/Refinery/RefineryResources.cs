using System.Collections.Generic;
using FNPlugin.Background.Core;
using FNPlugin.Extensions;

namespace FNPlugin.Refinery
{
    /// <summary>
    /// Where a refinery activity reads and moves resources. On a loaded vessel (<see cref="PartRefineryResources"/>)
    /// these are exactly the calls the activities always made on their part. On an unloaded vessel, in Kerbalism's
    /// background step (<see cref="LedgerRefineryResources"/>), they read and change the step's
    /// <see cref="ResourceLedger"/>, so the same activity code runs the refinery in the background
    /// (todo/kspie-rails-warp.md, step A2).
    /// </summary>
    public abstract class RefineryResources
    {
        /// <summary>The vessel the refinery is on (loaded or not).</summary>
        public abstract Vessel Vessel { get; }

        public abstract IEnumerable<PartResource> GetConnectedResources(string resourceName);

        public abstract double RequestResource(string resourceName, double demand);

        public abstract double RequestResource(string resourceName, double demand, ResourceFlowMode flowMode);

        public abstract double RequestResource(int resourceId, double demand, ResourceFlowMode flowMode);

        public abstract void GetResourceMass(PartResourceDefinition definition, out double spareRoomMass, out double maximumMass);

        public abstract double GetResourceSpareCapacity(string resourceName);

        public abstract double GetResourceSpareCapacity(PartResourceDefinition definition);

        public abstract double GetResourceSpareCapacity(PartResourceDefinition definition, ResourceFlowMode flowMode);

        public abstract double GetResourceAvailable(PartResourceDefinition definition);

        public abstract double GetResourceAvailable(PartResourceDefinition definition, ResourceFlowMode flowMode);

        public abstract double GetResourceAvailable(string resourceName, ResourceFlowMode flowMode);
    }

    /// <summary>The refinery's own part on a loaded vessel: the KSP and FNPlugin.Extensions calls, unchanged.</summary>
    public sealed class PartRefineryResources : RefineryResources
    {
        private readonly Part _part;

        public PartRefineryResources(Part part)
        {
            _part = part;
        }

        public override Vessel Vessel => _part.vessel;

        public override IEnumerable<PartResource> GetConnectedResources(string resourceName) => _part.GetConnectedResources(resourceName);

        public override double RequestResource(string resourceName, double demand) => _part.RequestResource(resourceName, demand);

        public override double RequestResource(string resourceName, double demand, ResourceFlowMode flowMode) => _part.RequestResource(resourceName, demand, flowMode);

        public override double RequestResource(int resourceId, double demand, ResourceFlowMode flowMode) => _part.RequestResource(resourceId, demand, flowMode);

        public override void GetResourceMass(PartResourceDefinition definition, out double spareRoomMass, out double maximumMass) =>
            _part.GetResourceMass(definition, out spareRoomMass, out maximumMass);

        public override double GetResourceSpareCapacity(string resourceName) => _part.GetResourceSpareCapacity(resourceName);

        public override double GetResourceSpareCapacity(PartResourceDefinition definition) => _part.GetResourceSpareCapacity(definition);

        public override double GetResourceSpareCapacity(PartResourceDefinition definition, ResourceFlowMode flowMode) => _part.GetResourceSpareCapacity(definition, flowMode);

        public override double GetResourceAvailable(PartResourceDefinition definition) => _part.GetResourceAvailable(definition);

        public override double GetResourceAvailable(PartResourceDefinition definition, ResourceFlowMode flowMode) => _part.GetResourceAvailable(definition, flowMode);

        public override double GetResourceAvailable(string resourceName, ResourceFlowMode flowMode) => _part.GetResourceAvailable(resourceName, flowMode);
    }

    /// <summary>
    /// An unloaded vessel in Kerbalism's background step: the whole vessel's stock as one store per resource, read and
    /// changed through the step's ledger. Crossfeed as in the game: a resource the refinery's part could not reach
    /// through crossfeed when the vessel was saved (<see cref="Unreachable"/>, InterstellarRefineryController) is
    /// empty and room-less for the calls that obey crossfeed — those with a crossfeed-bound flow mode, or with the
    /// resource's own flow mode when that one is crossfeed-bound (LqdWater: STACK_PRIORITY_SEARCH). The vessel-wide
    /// calls (ALL_VESSEL, STAGE_PRIORITY_FLOW, GetConnectedResources over all parts) see everything, as in the game.
    /// </summary>
    public sealed class LedgerRefineryResources : RefineryResources
    {
        private readonly Vessel _vessel;
        private readonly HashSet<string> _unreachable;
        private ResourceLedger _ledger = new ResourceLedger(null, null);

        public LedgerRefineryResources(Vessel vessel, IEnumerable<string> unreachable)
        {
            _vessel = vessel;
            _unreachable = new HashSet<string>(unreachable ?? new string[0]);
        }

        /// <summary>The ledger of the step being run (a new one every step).</summary>
        public ResourceLedger Ledger
        {
            get => _ledger;
            set => _ledger = value ?? new ResourceLedger(null, null);
        }

        /// <summary>Resources the refinery's part cannot reach through crossfeed.</summary>
        public IEnumerable<string> Unreachable => _unreachable;

        public override Vessel Vessel => _vessel;

        /// <summary>Whether this flow mode needs a crossfeed path from the part (KSP's stack and no-flow modes).</summary>
        public static bool IsCrossfeedBound(ResourceFlowMode mode) =>
            mode == ResourceFlowMode.NO_FLOW || mode == ResourceFlowMode.STACK_PRIORITY_SEARCH
            || mode == ResourceFlowMode.STAGE_STACK_FLOW || mode == ResourceFlowMode.STAGE_STACK_FLOW_BALANCE;

        private bool Blocked(string resourceName, ResourceFlowMode mode)
        {
            if (resourceName == null || !_unreachable.Contains(resourceName)) return false;
            if (mode == ResourceFlowMode.NULL)
                mode = PartResourceLibrary.Instance.GetDefinition(resourceName)?.resourceFlowMode ?? ResourceFlowMode.ALL_VESSEL;
            return IsCrossfeedBound(mode);
        }

        private double Request(string resourceName, double demand, ResourceFlowMode mode) =>
            Blocked(resourceName, mode) ? 0 : _ledger.Request(resourceName, demand);

        private double Room(string resourceName, ResourceFlowMode mode) =>
            Blocked(resourceName, mode) ? 0 : _ledger.Room(resourceName);

        private double Amount(string resourceName, ResourceFlowMode mode) =>
            Blocked(resourceName, mode) ? 0 : _ledger.Amount(resourceName);

        public override IEnumerable<PartResource> GetConnectedResources(string resourceName)
        {
            // FNPlugin.Extensions.GetConnectedResources: every part of the vessel, no crossfeed
            var capacity = _ledger.Capacity(resourceName);
            if (capacity <= 0)
                yield break;
            // a stand-in store (PartResource needs no part for its fields); _flowState directly: the property's
            // setter fires a game event
            yield return new PartResource((Part)null)
            {
                resourceName = resourceName,
                amount = _ledger.Amount(resourceName),
                maxAmount = capacity,
                _flowState = true
            };
        }

        public override double RequestResource(string resourceName, double demand) => Request(resourceName, demand, ResourceFlowMode.NULL);

        public override double RequestResource(string resourceName, double demand, ResourceFlowMode flowMode) => Request(resourceName, demand, flowMode);

        public override double RequestResource(int resourceId, double demand, ResourceFlowMode flowMode) =>
            Request(PartResourceLibrary.Instance.GetDefinition(resourceId)?.name, demand, flowMode);

        public override void GetResourceMass(PartResourceDefinition definition, out double spareRoomMass, out double maximumMass)
        {
            // as PartExtensions.GetResourceMass (GetConnectedResourceTotals with the resource's own flow mode;
            // density through decimal)
            var density = (double)(decimal)definition.density;
            var blocked = Blocked(definition.name, ResourceFlowMode.NULL);
            maximumMass = blocked ? 0 : _ledger.Capacity(definition.name) * density;
            spareRoomMass = blocked ? 0 : _ledger.Room(definition.name) * density;
        }

        public override double GetResourceSpareCapacity(string resourceName) => Room(resourceName, ResourceFlowMode.NULL);

        public override double GetResourceSpareCapacity(PartResourceDefinition definition) => Room(definition?.name, ResourceFlowMode.NULL);

        public override double GetResourceSpareCapacity(PartResourceDefinition definition, ResourceFlowMode flowMode) => Room(definition?.name, flowMode);

        // PartExtensions.GetResourceAvailable(definition): STAGE_PRIORITY_FLOW, vessel-wide
        public override double GetResourceAvailable(PartResourceDefinition definition) => _ledger.Amount(definition?.name);

        public override double GetResourceAvailable(PartResourceDefinition definition, ResourceFlowMode flowMode) => Amount(definition?.name, flowMode);

        public override double GetResourceAvailable(string resourceName, ResourceFlowMode flowMode) => Amount(resourceName, flowMode);
    }
}
