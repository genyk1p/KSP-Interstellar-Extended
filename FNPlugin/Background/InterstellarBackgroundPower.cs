using System;
using System.Collections.Generic;
using System.Linq;
using FNPlugin.Background.Core;
using FNPlugin.Constants;
using FNPlugin.Resources;
using UnityEngine;

namespace FNPlugin.Background
{
    /// <summary>
    /// Runs the KSPIE power plant of an UNLOADED vessel in Kerbalism's background step (todo/kspie-rails-warp.md,
    /// option A). Kerbalism calls the static BackgroundUpdate of every module class that has one, for every
    /// unloaded vessel, every background step (Kerbalism Background.ProcessApiModule, which also catches and logs
    /// exceptions): the rates returned in resourceChangeRequest are applied over elapsed_s. The method's parameters
    /// use KSP types only, so there is no reference to Kerbalism: without Kerbalism nothing calls it and the reactors
    /// keep their own catch-up on load.
    ///
    /// Added by Patches/BackgroundPower.cfg to every part with a KSPIE reactor; the first such module of a vessel
    /// in a step solves the whole vessel, the others return nothing. On a loaded vessel the module does nothing.
    /// What a step did is written into this module's node of the snapshot (bg* keys, readable in the save and with
    /// the MCP proto_vessel tool); changes of state (fuel ran out, heat limit, overheat, reactors left to the
    /// catch-up, errors) go to KSP.log once each.
    /// </summary>
    public class InterstellarBackgroundPower : PartModule
    {
        private const string Broker = "KSPIE reactors";
        private const string ElectricCharge = "ElectricCharge";

        private sealed class VesselRun
        {
            public ProtoVessel Snapshot;
            public BackgroundSnapshot.VesselReading Reading;
            public double LastStepTime = double.NaN;
            public readonly ElectricDemand Demand = new ElectricDemand();
            public string LastCondition = "";
            public string LastError;
        }

        // One run per vessel and snapshot: a new snapshot (vessel unloaded again, a save loaded) starts afresh, so
        // nothing measured in another history (battery, error state) carries over.
        private static readonly Dictionary<Guid, VesselRun> Runs = new Dictionary<Guid, VesselRun>();

        // ReSharper disable once UnusedMember.Global - called by Kerbalism through reflection
        public static string BackgroundUpdate(Vessel v, ProtoPartSnapshot part_snapshot, ProtoPartModuleSnapshot module_snapshot,
            PartModule proto_part_module, Part proto_part, Dictionary<string, double> availableResources,
            List<KeyValuePair<string, double>> resourceChangeRequest, double elapsed_s)
        {
            if (v == null || v.protoVessel == null || module_snapshot == null || elapsed_s <= 0 || double.IsNaN(elapsed_s) || double.IsInfinity(elapsed_s))
                return Broker;

            if (!Runs.TryGetValue(v.id, out var run) || !ReferenceEquals(run.Snapshot, v.protoVessel))
            {
                run = new VesselRun { Snapshot = v.protoVessel };
                Runs[v.id] = run;
            }

            var now = Planetarium.GetUniversalTime();
            if (run.LastStepTime == now)
                return Broker; // another reactor part of this vessel already ran the step
            run.LastStepTime = now;

            var info = module_snapshot.moduleValues;
            try
            {
                if (run.Reading == null)
                {
                    run.Reading = BackgroundSnapshot.Read(v.protoVessel, PhysicsGlobals.SpaceTemperature);
                    LogReading(v, run.Reading);
                }
                Step(v, run, info, availableResources, resourceChangeRequest, elapsed_s, now);
                if (info.HasValue("bgError")) info.RemoveValue("bgError");
                run.LastError = null;
            }
            catch (Exception e)
            {
                // Nothing was requested and no catch-up time was moved (both happen only at the very end of Step),
                // so the reactors' own catch-up on load still bills this time.
                var message = e.GetType().Name + ": " + e.Message;
                info?.SetValue("bgError", message, true);
                if (message != run.LastError)
                {
                    run.LastError = message;
                    Debug.LogError("[KSPI]: background power of " + v.vesselName + " failed: " + e);
                }
            }
            return Broker;
        }

        private static void LogReading(Vessel v, BackgroundSnapshot.VesselReading reading)
        {
            foreach (var problem in reading.Problems)
                Debug.LogWarning("[KSPI]: background power of " + v.vesselName + ": " + problem);
            foreach (var excluded in reading.Excluded)
                Debug.Log("[KSPI]: background power of " + v.vesselName + ": reactor left to its catch-up on load - " + excluded);
        }

        private static void Step(Vessel v, VesselRun run, ConfigNode info, Dictionary<string, double> available,
            List<KeyValuePair<string, double>> requests, double elapsed, double now)
        {
            var reading = run.Reading;
            var spec = reading.Spec;
            if (spec.Reactors.Count == 0)
                return;

            available.TryGetValue(ElectricCharge, out var electricAmount);
            reading.Capacity.TryGetValue(ElectricCharge, out var electricCapacity);
            var requestedEcPerSecond = run.Demand.Request(electricAmount, electricCapacity, elapsed);
            var state = SteadyStateSolver.Solve(spec, requestedEcPerSecond / GameConstants.ecPerMJ);
            var infiniteFuel = CheatOptions.InfinitePropellant;

            var reactorCount = spec.Reactors.Count;
            var megajoules = new double[reactorCount];
            for (var r = 0; r < reactorCount; r++)
                megajoules[r] = SteadyStateSolver.ReactorMegajoulesPerSecond(spec, state, r);

            // --- fuel. A fuel KSPIE keeps inside the reactor (consumeGlobal = false) is a manual refuel in the game:
            // a reactor whose own store is empty stops even with full cargo tanks (Kerbalism still takes that fuel
            // from the whole vessel while the reactor runs).
            var reactorScale = new double[reactorCount];
            string limitingFuel = null;
            for (var r = 0; r < reactorCount; r++)
            {
                reactorScale[r] = 1;
                if (infiniteFuel) continue;
                foreach (var local in reading.ReactorLocalFuels[r])
                    if (OwnAmount(reading.ReactorParts[r], local) <= 1e-9)
                    {
                        reactorScale[r] = 0;
                        limitingFuel = local + " inside the reactor";
                    }
            }

            // The vessel's stock of a fuel is shared by the reactors burning it, in proportion to their need; each
            // reactor is then limited by its own scarcest fuel (an empty pebble bed does not stop a molten salt reactor).
            if (!infiniteFuel)
            {
                var fuelNeeded = new Dictionary<string, double>();
                for (var r = 0; r < reactorCount; r++)
                    if (reactorScale[r] > 0)
                        foreach (var fuel in spec.Reactors[r].Fuels)
                            Add(fuelNeeded, fuel.Resource, megajoules[r] * fuel.UnitsPerMJ * elapsed);
                for (var r = 0; r < reactorCount; r++)
                {
                    if (reactorScale[r] <= 0) continue;
                    foreach (var fuel in spec.Reactors[r].Fuels)
                    {
                        if (!fuelNeeded.TryGetValue(fuel.Resource, out var need) || need <= 0) continue;
                        available.TryGetValue(fuel.Resource, out var amount);
                        var share = Math.Min(1, Math.Max(0, amount) / need);
                        if (share < reactorScale[r])
                        {
                            reactorScale[r] = share;
                            limitingFuel = fuel.Resource;
                        }
                    }
                }
            }

            // stock generators of modelled KSPIE generators would make free EC in Kerbalism's own background
            foreach (var stock in reading.StockGenerators)
                stock.SetValue("generatorIsActive", "False", true);

            // --- rates for Kerbalism
            var rates = new Dictionary<string, double>();
            var electricMW = 0.0;
            for (var g = 0; g < spec.Generators.Count; g++)
                electricMW += state.GeneratorElectricMW[g] * reactorScale[spec.Generators[g].Reactor];
            if (electricMW > 0)
                Add(rates, ElectricCharge, electricMW * GameConstants.ecPerMJ);

            var breedingNeeded = new Dictionary<string, double>();
            var breedingMade = new Dictionary<string, double>();
            for (var r = 0; r < reactorCount; r++)
            {
                var power = megajoules[r] * reactorScale[r];
                var reactor = spec.Reactors[r];
                if (!infiniteFuel)
                    foreach (var fuel in reactor.Fuels)
                        Add(rates, fuel.Resource, -power * fuel.UnitsPerMJ);
                foreach (var product in reactor.Products)
                {
                    // a product KSPIE keeps inside the reactor (produceGlobal = false) is lost when the reactor has
                    // no store for it or the store is full (ProduceReactorProduct); Kerbalism may put it in any tank
                    if (reading.ReactorLocalProducts[r].Contains(product.Resource) && OwnRoom(reading.ReactorParts[r], product.Resource) <= 1e-9)
                        continue;
                    Add(rates, product.Resource, power * product.UnitsPerMJ);
                }
                foreach (var input in reactor.BreedingInputs)
                    Add(breedingNeeded, input.Resource, power * input.UnitsPerMJ);
                foreach (var output in reactor.BreedingOutputs)
                    Add(breedingMade, output.Resource, power * output.UnitsPerMJ);
            }

            // tritium breeding stops when the lithium runs out or the tritium has no room (InterstellarReactor.BreedTritium);
            // the room left after this step's fission products (they make tritium too)
            var breedingScale = 1.0;
            foreach (var need in breedingNeeded)
            {
                if (need.Value <= 0) continue;
                available.TryGetValue(need.Key, out var amount);
                breedingScale = Math.Min(breedingScale, Math.Max(0, amount) / (need.Value * elapsed));
            }
            foreach (var made in breedingMade)
            {
                if (made.Value <= 0 || made.Key == ResourceSettings.Config.Helium4Gas) continue;
                available.TryGetValue(made.Key, out var amount);
                reading.Capacity.TryGetValue(made.Key, out var capacity);
                rates.TryGetValue(made.Key, out var fromProducts);
                var room = capacity - amount - Math.Max(0, fromProducts) * elapsed;
                breedingScale = Math.Min(breedingScale, Math.Max(0, room) / (made.Value * elapsed));
            }
            foreach (var need in breedingNeeded)
                Add(rates, need.Key, -need.Value * breedingScale);
            foreach (var made in breedingMade)
                Add(rates, made.Key, made.Value * breedingScale);

            var scale = reactorCount > 0 ? reactorScale.Min() : 1;
            foreach (var rate in rates)
                if (double.IsNaN(rate.Value) || double.IsInfinity(rate.Value))
                    throw new InvalidOperationException("background rate of " + rate.Key + " is " + rate.Value
                        + " (requested " + requestedEcPerSecond + " EC/s, fuel scale " + scale + ")");

            // --- conditions worth a line in KSP.log, once per change
            var condition = state.Overheated ? "overheated: the radiators cannot cool even the minimum power; the model stops the reactors (in the game they would run hot and the radiators explode)"
                : limitingFuel != null && scale < 1 ? "out of " + limitingFuel
                : state.HeatLimited ? "radiators limit the power" : "";
            if (condition != run.LastCondition)
            {
                run.LastCondition = condition;
                if (condition.Length > 0)
                    Debug.Log("[KSPI]: background power of " + v.vesselName + ": " + condition);
            }

            // --- commit: diagnostics, catch-up time, then the requests (nothing below can throw)
            var nowText = BackgroundSnapshot.Number(now);
            info.SetValue("bgLastTime", nowText, true);
            info.SetValue("bgElectricMW", BackgroundSnapshot.Number(electricMW), true);
            info.SetValue("bgRequestedMW", BackgroundSnapshot.Number(state.RequestedElectricMW), true);
            info.SetValue("bgWasteHeatFill", BackgroundSnapshot.Number(state.WasteHeatFill), true);
            info.SetValue("bgHeatLimited", state.HeatLimited.ToString(), true);
            info.SetValue("bgOverheated", state.Overheated.ToString(), true);
            info.SetValue("bgFuelScale", BackgroundSnapshot.Number(scale), true);
            info.SetValue("bgBreedingScale", BackgroundSnapshot.Number(breedingScale), true);
            info.SetValue("bgConsumptionEc", BackgroundSnapshot.Number(run.Demand.ConsumptionPerSecond), true);
            info.SetValue("bgCondition", condition, true);

            // the reactors were run for this time: their own catch-up on load must start from now
            foreach (var node in reading.ReactorNodes)
                node.SetValue(BackgroundSnapshot.ReactorLastActiveTime, nowText, true);

            run.Demand.Produced(electricAmount, electricMW * GameConstants.ecPerMJ, elapsed);
            foreach (var rate in rates)
                if (rate.Value != 0)
                    requests.Add(rate);
        }

        private static double OwnAmount(ProtoPartSnapshot part, string resource) =>
            part.resources.Where(p => p.resourceName == resource).Sum(p => p.amount);

        private static double OwnRoom(ProtoPartSnapshot part, string resource) =>
            part.resources.Where(p => p.resourceName == resource).Sum(p => p.maxAmount - p.amount);

        private static void Add(Dictionary<string, double> rates, string resource, double rate)
        {
            rates.TryGetValue(resource, out var current);
            rates[resource] = current + rate;
        }
    }
}
