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
    /// Runs the KSPIE power plant, its consumers and its beamed power receivers of an UNLOADED vessel in Kerbalism's
    /// background step (todo/kspie-rails-warp.md, option A: reactors A1, consumers A2, beams A3). Kerbalism calls the
    /// static BackgroundUpdate of every module class that has one, for every unloaded vessel, every background step
    /// (Kerbalism Background.ProcessApiModule, which also catches and logs exceptions; it runs one or a few unloaded
    /// vessels per physics tick, each at its own time): the rates returned in resourceChangeRequest are applied over
    /// elapsed_s. The method's parameters use KSP types only, so there is no reference to Kerbalism: without Kerbalism
    /// nothing calls it and the modules keep their own catch-up on load.
    ///
    /// Added by Patches/BackgroundPower.cfg to every part with a KSPIE reactor, consumer, transmitter or receiver; the
    /// first such module of a vessel in a step runs the whole vessel, the others return nothing. On a loaded vessel the
    /// module does nothing. What a step did is written into this module's node of the snapshot (bg* keys, readable in
    /// the save and with the MCP proto_vessel tool); changes of state (fuel ran out, heat limit, overheat, modules left
    /// to their catch-up, errors) go to KSP.log once each.
    ///
    /// Electricity (Core/StepPlan, Core/PowerAllocation): the electric beamed power receivers' megajoules first (free),
    /// then the reactors; the battery charge (ElectricCharge for the rest of the vessel, measured by ElectricDemand) is
    /// served before the consumers, the consumers by KSPIE priority and smallest request first; what the generators
    /// cannot give, the consumers that take ElectricCharge on a loaded vessel take from the battery down to
    /// BackgroundConsumers.BatteryFloor.
    /// A vessel whose power the model does not know completely (BackgroundSnapshot.VesselReading.PowerIncomplete) and
    /// that has consumers, or that has a consumer the model can neither run nor bill, keeps its whole plant on the
    /// modules' own catch-up, as without the model — so does a vessel whose consumer throws in a step, from that step
    /// on. Such a plant's stock ModuleGenerators are left as KSPIE saved them (upstream: Kerbalism's own background
    /// makes their EC while the reactor's catch-up bills its fuel on load).
    /// </summary>
    public class InterstellarBackgroundPower : PartModule
    {
        /// <summary>Name of the rates in Kerbalism's resource brokers (API.ResourceBrokers): reactors and consumers alike.</summary>
        private const string Broker = "KSPIE";
        private const string ElectricCharge = "ElectricCharge";

        private sealed class VesselRun
        {
            public ProtoVessel Snapshot;
            public BackgroundSnapshot.VesselReading Reading;
            public List<BackgroundConsumers.Consumer> Consumers;
            public double LastStepTime = double.NaN;
            public ElectricDemand Demand = new ElectricDemand();
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
                return Broker; // another module of this vessel already ran the step
            run.LastStepTime = now;

            var info = module_snapshot.moduleValues;
            try
            {
                BackgroundBeams.Prepare();
                if (run.Reading == null)
                    ReadVessel(v, run);
                Step(v, run, info, availableResources, resourceChangeRequest, elapsed_s, now);
                if (info.HasValue("bgError")) info.RemoveValue("bgError");
                run.LastError = null;
            }
            catch (Exception e)
            {
                // Nothing was requested and nothing was written into the snapshot but the error (all of it happens at
                // the very end of Step), so the modules' own catch-up on load still bills this time. The battery
                // measurement starts again: the step it was asked for did not happen.
                run.Demand = new ElectricDemand();
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

        /// <summary>Reads the snapshot once per run; the run keeps it only when both readings succeeded.</summary>
        private static void ReadVessel(Vessel v, VesselRun run)
        {
            var reading = BackgroundSnapshot.Read(v.protoVessel, PhysicsGlobals.SpaceTemperature);
            var consumers = BackgroundConsumers.Read(v, v.protoVessel);

            string leaveAll = null;
            if (consumers.Unmodelled.Count > 0)
                leaveAll = "a KSPIE consumer the model can neither run nor bill is on board (" + string.Join("; ", consumers.Unmodelled.ToArray()) + ")";
            else if (reading.PowerIncomplete && consumers.Consumers.Count > 0)
                leaveAll = "the vessel's power is not known completely and it has KSPIE consumers";
            if (leaveAll != null)
            {
                reading.LeaveAllToCatchUp(leaveAll);
                foreach (var consumer in consumers.Consumers)
                    reading.Excluded.Add(consumer.Name + ": left to its catch-up on load with the rest of the plant");
                consumers.Consumers.Clear();
                consumers.BilledOnly.Clear();
            }

            foreach (var problem in reading.Problems)
                Debug.LogWarning("[KSPI]: background power of " + v.vesselName + ": " + problem);
            foreach (var excluded in reading.Excluded)
                Debug.Log("[KSPI]: background power of " + v.vesselName + ": left to its catch-up on load - " + excluded);
            foreach (var billed in consumers.BilledOnly)
                Debug.Log("[KSPI]: background power of " + v.vesselName + ": billed only - " + billed);

            run.Consumers = consumers.Consumers;
            run.Reading = reading;
        }

        private static void Step(Vessel v, VesselRun run, ConfigNode info, Dictionary<string, double> available,
            List<KeyValuePair<string, double>> requests, double elapsed, double now)
        {
            var reading = run.Reading;
            var spec = reading.Spec;
            var consumers = run.Consumers;
            var reactorCount = spec.Reactors.Count;
            var receivers = reading.Receivers;
            if (reactorCount == 0 && consumers.Count == 0 && receivers.Count == 0)
                return;

            // --- demands, on the stock at the start of the step. A consumer that throws leaves the whole plant to its
            // catch-up from this step on (what it would draw is unknown).
            var context = new BackgroundConsumers.StepContext { Vessel = v, Ledger = new ResourceLedger(available, reading.Capacity), Elapsed = elapsed, Now = now };
            foreach (var consumer in consumers)
            {
                try
                {
                    consumer.Prepare(context);
                }
                catch (Exception e)
                {
                    LeaveAllFromNow(v, run, consumer, e);
                    return;
                }
            }
            var demands = consumers.Select(c => c.Demand).ToList();
            var freePower = CheatOptions.InfiniteElectricity;

            available.TryGetValue(ElectricCharge, out var electricAmount);
            reading.Capacity.TryGetValue(ElectricCharge, out var electricCapacity);

            // --- beamed power (A3): what each receiver can take now (BackgroundBeams.Receive never throws). A thermal
            // receiver's power is its pseudo-reactor's thermal power for this step; electric receivers give megajoules.
            var intakes = new List<BackgroundBeams.Intake>();
            var receiverElectricMW = 0.0;
            var receiverHeatMW = 0.0;
            foreach (var receiver in receivers)
            {
                var intake = BackgroundBeams.Receive(receiver);
                intakes.Add(intake);
                if (receiver.Thermal)
                {
                    if (receiver.ReactorIndex >= 0)
                        spec.Reactors[receiver.ReactorIndex].ThermalMW = intake.AvailableMW;
                }
                else
                {
                    receiverElectricMW += intake.AvailableMW;
                    receiverHeatMW += intake.ConversionHeatMW;
                }
            }
            if (receivers.Count > 0)
                BackgroundBeams.ForgetGone();

            var rates = new Dictionary<string, double>();
            var state = default(SteadyState);
            var reactorScale = new double[reactorCount];
            var megajoules = new double[reactorCount];
            string limitingFuel = null;
            var requestedEcPerSecond = 0.0;
            var electricMW = 0.0;
            var breedingScale = 1.0;
            var plan = default(StepPlan.Plan);
            PowerAllocation.Result allocation;
            var hasPlant = reactorCount > 0 || receivers.Any(r => !r.Thermal);

            if (hasPlant)
            {
                requestedEcPerSecond = run.Demand.Request(electricAmount, electricCapacity, elapsed);
                var electricChargeMW = requestedEcPerSecond / GameConstants.ecPerMJ;
                // the transmitter and the factories ask for slider × min(capacity, stable supply) on a loaded vessel
                if (!freePower)
                    CapByStableSupply(spec, demands, receiverElectricMW, receiverHeatMW, electricChargeMW);
                plan = StepPlan.Make(electricChargeMW, demands, receiverElectricMW, receiverHeatMW, freePower);

                if (reactorCount > 0)
                {
                    state = SteadyStateSolver.Solve(spec, plan.ReactorRequestMW, plan.ReactorExtraHeat);
                    for (var r = 0; r < reactorCount; r++)
                        megajoules[r] = SteadyStateSolver.ReactorMegajoulesPerSecond(spec, state, r);
                    limitingFuel = FuelScale(reading, available, megajoules, elapsed, reactorScale);
                    for (var g = 0; g < spec.Generators.Count; g++)
                        electricMW += state.GeneratorElectricMW[g] * reactorScale[spec.Generators[g].Reactor];
                    context.WasteHeatFill = state.WasteHeatFill;
                    ReactorRates(reading, rates, available, megajoules, reactorScale, elapsed, out breedingScale);
                }

                allocation = PowerAllocation.Allocate(plan.SupplyMW(electricMW), electricChargeMW, freePower ? new List<ConsumerDemand>() : demands);
            }
            else
                allocation = PowerAllocation.Allocate(0, 0, demands);

            // What the generators cannot give, the consumers that take ElectricCharge on a loaded vessel (the refinery,
            // small drills, the antimatter collector) take from the battery — here only down to the floor, so that life
            // support and probe cores keep their charge. Kerbalism's solar panels and the like refill it between steps.
            var batteryDrawnMW = 0.0;
            if (!freePower && consumers.Count > 0)
            {
                var batteryMW = Math.Max(0, electricAmount - BackgroundConsumers.BatteryFloor * electricCapacity) / elapsed / GameConstants.ecPerMJ;
                batteryDrawnMW = PowerAllocation.TopUpFromBattery(allocation, demands, batteryMW);
            }
            var electricChargeNetMW = allocation.ElectricChargeMW - batteryDrawnMW;
            if (electricChargeNetMW != 0)
                Add(rates, ElectricCharge, electricChargeNetMW * GameConstants.ecPerMJ);

            // --- consumers, one after another on a ledger that starts from the stock after the reactors' share of the
            // step (fuel they burn is not there for a refinery, products they make are). One that throws leaves the
            // whole plant to its catch-up from this step on: nothing of the step is requested or written.
            var afterReactors = new Dictionary<string, double>(available);
            foreach (var rate in rates)
            {
                afterReactors.TryGetValue(rate.Key, out var amount);
                afterReactors[rate.Key] = Math.Max(0, amount + rate.Value * elapsed);
            }
            var ledger = new ResourceLedger(afterReactors, reading.Capacity);
            context.Ledger = ledger;
            var consumersMW = 0.0;
            var billedMW = 0.0;
            var ran = new List<BackgroundConsumers.Consumer>();
            var billed = new List<KeyValuePair<BackgroundConsumers.Consumer, double>>();
            for (var i = 0; i < consumers.Count; i++)
            {
                var consumer = consumers[i];
                var received = freePower ? consumer.Demand.DemandMW : allocation.ConsumerMW[i];
                if (consumer.BilledOnly)
                {
                    billedMW += received;
                    billed.Add(new KeyValuePair<BackgroundConsumers.Consumer, double>(consumer, received));
                    continue;
                }
                var trial = ledger.Fork();
                try
                {
                    consumer.Run(context, trial, received);
                }
                catch (Exception e)
                {
                    LeaveAllFromNow(v, run, consumer, e);
                    return;
                }
                ledger.Absorb(trial);
                consumersMW += received;
                ran.Add(consumer);
            }
            foreach (var change in ledger.Changes)
                if (change.Value != 0)
                    Add(rates, change.Key, change.Value / elapsed);

            var scale = reactorCount > 0 ? reactorScale.Min() : 1;
            foreach (var rate in rates)
                if (double.IsNaN(rate.Value) || double.IsInfinity(rate.Value))
                    throw new InvalidOperationException("background rate of " + rate.Key + " is " + rate.Value
                        + " (requested " + requestedEcPerSecond + " EC/s, fuel scale " + scale + ")");

            // --- conditions worth a line in KSP.log, once per change
            var condition = reactorCount == 0 ? ""
                : state.Overheated ? "overheated: the radiators cannot cool even the minimum power; the model stops the reactors (in the game they would run hot and the radiators explode)"
                : limitingFuel != null && scale < 1 ? "out of " + limitingFuel
                : state.HeatLimited ? "radiators limit the power" : "";
            if (condition != run.LastCondition)
            {
                run.LastCondition = condition;
                if (condition.Length > 0)
                    Debug.Log("[KSPI]: background power of " + v.vesselName + ": " + condition);
            }

            // --- commit: diagnostics, snapshot changes, catch-up times, then the requests (nothing below can throw)
            var nowText = BackgroundSnapshot.Number(now);
            info.SetValue("bgLastTime", nowText, true);
            info.SetValue("bgElectricMW", BackgroundSnapshot.Number(electricMW), true);
            info.SetValue("bgConsumersMW", BackgroundSnapshot.Number(consumersMW), true);
            info.SetValue("bgBilledOnlyMW", BackgroundSnapshot.Number(billedMW), true);
            info.SetValue("bgBatteryMW", BackgroundSnapshot.Number(batteryDrawnMW), true);
            info.SetValue("bgConsumers", ran.Count.ToString(), true);
            if (reactorCount > 0)
            {
                info.SetValue("bgRequestedMW", BackgroundSnapshot.Number(state.RequestedElectricMW), true);
                info.SetValue("bgWasteHeatFill", BackgroundSnapshot.Number(state.WasteHeatFill), true);
                info.SetValue("bgHeatLimited", state.HeatLimited.ToString(), true);
                info.SetValue("bgOverheated", state.Overheated.ToString(), true);
                info.SetValue("bgFuelScale", BackgroundSnapshot.Number(scale), true);
                info.SetValue("bgBreedingScale", BackgroundSnapshot.Number(breedingScale), true);
            }
            if (hasPlant)
                info.SetValue("bgConsumptionEc", BackgroundSnapshot.Number(run.Demand.ConsumptionPerSecond), true);
            info.SetValue("bgCondition", condition, true);

            var receivedMW = plan?.ReceiverUsedMW ?? 0;
            foreach (var intake in intakes)
                if (intake.Receiver.Thermal && state != null && intake.Receiver.ReactorIndex >= 0)
                    receivedMW += state.ReactorPowerRatio[intake.Receiver.ReactorIndex] * intake.AvailableMW;
            if (receivers.Count > 0)
            {
                info.SetValue("bgReceivedMW", BackgroundSnapshot.Number(receivedMW), true);
                info.SetValue("bgBeamAvailableMW", BackgroundSnapshot.Number(intakes.Sum(i => i.AvailableMW)), true);
            }

            // stock generators of modelled KSPIE generators would make free EC in Kerbalism's own background
            foreach (var stock in reading.StockGenerators)
                stock.SetValue("generatorIsActive", "False", true);

            // the reactors and the consumers that ran were run for this time: their own catch-up on load must start
            // from now (a thermal receiver's pseudo-reactor has no node; billed-only consumers keep theirs)
            foreach (var node in reading.ReactorNodes)
                node?.SetValue(BackgroundSnapshot.ReactorLastActiveTime, nowText, true);
            foreach (var consumer in ran)
                consumer.Commit(context, nowText);
            foreach (var pair in billed)
                pair.Key.CommitBilled(context, pair.Value);
            foreach (var intake in intakes)
                BackgroundBeams.Commit(intake);

            if (hasPlant)
                run.Demand.Produced(electricAmount, allocation.ElectricChargeMW * GameConstants.ecPerMJ, elapsed, batteryDrawnMW * GameConstants.ecPerMJ);
            foreach (var rate in rates)
                if (rate.Value != 0)
                    requests.Add(rate);
        }

        /// <summary>
        /// Caps the demand of the consumers that ask for slider × min(capacity, stable supply) (ConsumerDemand.PlantSlider).
        /// KSPIE's stable supply = the generators' maximum at the radiators' current temperature + the electric receivers,
        /// less the battery charge. Without reactors it is the receivers' alone; with a slider below 1 the generators' part
        /// depends on what those consumers draw (StepPlan.StableSupply finds the steady state, a few solver runs); at
        /// slider 1 the cap would change nothing the plant can deliver, so it is skipped.
        /// </summary>
        private static void CapByStableSupply(VesselSpec spec, List<ConsumerDemand> demands, double receiverElectricMW,
            double receiverHeatMW, double electricChargeMW)
        {
            if (!demands.Any(d => d.PlantSlider >= 0)) return;
            var other = receiverElectricMW - electricChargeMW;
            if (spec.Reactors.Count == 0)
            {
                PowerAllocation.CapByPlant(demands, other);
                return;
            }
            if (!demands.Any(d => d.PlantSlider >= 0 && d.PlantSlider < 1)) return;

            var full = demands.Select(d => d.DemandMW).ToArray();
            void Restore()
            {
                for (var i = 0; i < demands.Count; i++) demands[i].DemandMW = full[i];
            }
            double CapacityAt(double generators)
            {
                Restore();
                PowerAllocation.CapByPlant(demands, generators + other);
                var trial = StepPlan.Make(electricChargeMW, demands, receiverElectricMW, receiverHeatMW, false);
                return SteadyStateSolver.Solve(spec, trial.ReactorRequestMW, trial.ReactorExtraHeat).CapacityMW;
            }
            var stable = StepPlan.StableSupply(CapacityAt, SteadyStateSolver.MaximumElectricMW(spec));
            Restore();
            PowerAllocation.CapByPlant(demands, stable + other);
        }

        /// <summary>
        /// A consumer threw: from this step on the whole plant keeps its own catch-up (nothing of this step is requested
        /// or written; the modules' catch-up times stay where the last good step left them, so their catch-up on load
        /// covers the rest). Lasts until the vessel's run starts afresh (a new snapshot).
        /// </summary>
        private static void LeaveAllFromNow(Vessel v, VesselRun run, BackgroundConsumers.Consumer consumer, Exception e)
        {
            Debug.LogError("[KSPI]: background power of " + v.vesselName + ": consumer " + consumer.Name
                + " failed - from now on the whole plant is left to its catch-up on load: " + e);
            run.Reading.LeaveAllToCatchUp("consumer " + consumer.Name + " failed");
            run.Consumers.Clear();
            run.Demand = new ElectricDemand();
        }

        /// <summary>
        /// Share of its power every reactor can burn fuel for in this step (reactorScale, 0..1); returns the fuel that
        /// limited, if any.
        /// </summary>
        private static string FuelScale(BackgroundSnapshot.VesselReading reading, Dictionary<string, double> available,
            double[] megajoules, double elapsed, double[] reactorScale)
        {
            var spec = reading.Spec;
            var reactorCount = spec.Reactors.Count;
            var infiniteFuel = CheatOptions.InfinitePropellant;
            string limitingFuel = null;

            // A fuel KSPIE keeps inside the reactor (consumeGlobal = false) is a manual refuel in the game: a reactor
            // whose own store is empty stops even with full cargo tanks (Kerbalism still takes that fuel from the
            // whole vessel while the reactor runs).
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
            if (infiniteFuel)
                return null;

            // The vessel's stock of a fuel is shared by the reactors burning it, in proportion to their need; each
            // reactor is then limited by its own scarcest fuel (an empty pebble bed does not stop a molten salt reactor).
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
            return limitingFuel;
        }

        /// <summary>Fuel, fission products and tritium breeding of the reactors, units per second, added to <paramref name="rates"/>.</summary>
        private static void ReactorRates(BackgroundSnapshot.VesselReading reading, Dictionary<string, double> rates,
            Dictionary<string, double> available, double[] megajoules, double[] reactorScale, double elapsed, out double breedingScale)
        {
            var spec = reading.Spec;
            var infiniteFuel = CheatOptions.InfinitePropellant;
            var breedingNeeded = new Dictionary<string, double>();
            var breedingMade = new Dictionary<string, double>();
            for (var r = 0; r < spec.Reactors.Count; r++)
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
            breedingScale = 1.0;
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
