using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using FNPlugin.Background.Core;
using FNPlugin.Refinery;
using UnityEngine;

namespace FNPlugin.Background
{
    /// <summary>
    /// KSPIE consumers of electric power (megajoules) on an unloaded vessel, run in Kerbalism's background step by
    /// <see cref="InterstellarBackgroundPower"/> (todo/kspie-rails-warp.md, steps A2 and A3):
    /// - the ISRU refinery (InterstellarRefineryController): its current activity runs its own UpdateFrame on the
    ///   step's <see cref="ResourceLedger"/> (Refinery/RefineryResources), with the power the vessel gives it;
    /// - "recipe" consumers (drill, regolith collector, antimatter and metallic hydrogen factories, antimatter
    ///   collector): the module wrote at save time what it takes and makes per second at full power where the vessel
    ///   is (bg* keys); the step scales that by the power received and by the inputs there are (Core RecipeStep);
    /// - beamed power transmitters (A3): they take their power as loaded (transmission slider × min(capacity, the
    ///   plant's stable supply)) and the step writes the beam they send into their node (nuclear_power, kW; the saved
    ///   solar_power stays, KSPIE scales it by the sun), where KSPIE's BeamedPowerSources reads unloaded transmitters
    ///   for the receivers; the beam generator's wear goes on in its own node.
    /// A consumer that ran moves its own catch-up time (the field its OnStart catches up from) to the step, so that
    /// loading the vessel does not produce the same time twice.
    /// A refinery activity that needs the loaded vessel (scoop and intakes, the reactors' own stores) is "billed only":
    /// its power is drawn in the background and its catch-up time stays; the share of its power it got, averaged over
    /// the steps since that time, becomes the ratio its catch-up produces at on load (lastPowerRatio) — so it produces
    /// what was paid for. A consumer the model can neither run nor bill (unknown activity, unreadable values, a
    /// transmitter whose beam generator is missing, a KSPIE module that takes power but has no background model) makes
    /// the whole vessel's plant keep its own catch-up (<see cref="ConsumerReading.Unmodelled"/>): otherwise its catch-up
    /// on load would produce with power nobody paid for. So does a consumer that throws in a step
    /// (InterstellarBackgroundPower), from that step on.
    /// </summary>
    public static class BackgroundConsumers
    {
        public const string ConsumerMarker = "bgConsumer";
        public const string RefineryKind = "Refinery";
        public const string RecipeKind = "Recipe";
        public const string TransmitterKind = "Transmitter";

        /// <summary>Recipe and transmitter: power at full output, MW.</summary>
        public const string DemandMW = "bgDemandMW";
        /// <summary>Refinery: powerReqMult × baseProduction (TweakScale applied); × the activity's PowerRequirements = MW at full slider.</summary>
        public const string PowerScale = "bgPowerScale";
        public const string Priority = "bgPriority";
        public const string HeatFraction = "bgHeatFraction";
        /// <summary>Recipe: takes ElectricCharge for a power shortage, as the loaded module (ConsumerDemand.TakesBattery).</summary>
        public const string TakesBattery = "bgTakesBattery";
        /// <summary>Recipe: below this share of its power the module does nothing (the drill stops under 15 %).</summary>
        public const string MinimumShare = "bgMinimumShare";
        /// <summary>Recipe: units per second at full power, "Resource=rate;…".</summary>
        public const string Inputs = "bgInputs";
        public const string Outputs = "bgOutputs";
        /// <summary>Name of the module's own catch-up time field.</summary>
        public const string TimeKey = "bgTimeKey";
        /// <summary>Refinery: productionMult × baseProduction (TweakScale applied).</summary>
        public const string ProductionModifier = "bgProductionModifier";
        /// <summary>Refinery: resources its part could not reach through crossfeed when saved, "A;B".</summary>
        public const string Unreachable = "bgUnreachable";
        /// <summary>Transmitter: beam generator efficiency before wear (0..1), the generator's part and module, its wear parameters.</summary>
        public const string BeamEfficiency = "bgBeamEfficiency";
        public const string GeneratorPart = "bgGeneratorPart";
        public const string GeneratorModule = "bgGeneratorModule";
        public const string WearLifeHours = "bgWearLifeHours";
        public const string WearEndLoss = "bgWearEndLoss";
        public const string WearMaxLoss = "bgWearMaxLoss";
        public const string WearHeatDoublings = "bgWearHeatDoublings";
        /// <summary>Transmitter and factories: the power slider (0..1) when the loaded module asks for slider × min(capacity, stable supply) (ConsumerDemand.PlantSlider).</summary>
        public const string PlantSlider = "bgPlantSlider";
        /// <summary>
        /// Billed-only refinery: the catch-up time its average started from, the seconds and the average share of its
        /// power billed since (written into lastPowerRatio for its catch-up).
        /// </summary>
        public const string BilledSince = "bgBilledSince";
        public const string BilledSeconds = "bgBilledSeconds";
        public const string BilledRatio = "bgBilledRatio";

        /// <summary>
        /// What the generators cannot give, the consumers that take ElectricCharge for it (ConsumerDemand.TakesBattery)
        /// take from the battery down to this level (InterstellarBackgroundPower) — KSPIE itself keeps the battery at
        /// half with Kerbalism (MegajoulesResourceManager.SupplyPriority).
        /// </summary>
        public const double BatteryFloor = 0.5;

        // ---------------------------------------------------------------- writing (part modules, OnSave)

        /// <summary>
        /// Background values of a recipe consumer; rates are units per second at full power (<paramref name="demandMW"/>).
        /// <paramref name="plantSlider"/> ≥ 0: the module asks for slider × min(capacity, the plant's stable supply)
        /// (<see cref="ConsumerDemand.PlantSlider"/>).
        /// </summary>
        public static void WriteRecipe(ConfigNode node, bool active, double demandMW, int priority, double heatFraction, double minimumShare,
            bool takesBattery, IEnumerable<KeyValuePair<string, double>> inputs, IEnumerable<KeyValuePair<string, double>> outputs, string timeKey,
            double plantSlider = -1)
        {
            if (plantSlider >= 0)
                node.SetValue(PlantSlider, BackgroundSnapshot.Number(plantSlider), true);
            node.SetValue(ConsumerMarker, RecipeKind, true);
            node.SetValue(BackgroundSnapshot.Active, active.ToString(), true);
            node.SetValue(DemandMW, BackgroundSnapshot.Number(demandMW), true);
            node.SetValue(Priority, priority.ToString(CultureInfo.InvariantCulture), true);
            node.SetValue(HeatFraction, BackgroundSnapshot.Number(heatFraction), true);
            node.SetValue(MinimumShare, BackgroundSnapshot.Number(minimumShare), true);
            node.SetValue(TakesBattery, takesBattery.ToString(), true);
            node.SetValue(Inputs, BackgroundSnapshot.FlowList(inputs ?? Enumerable.Empty<KeyValuePair<string, double>>()), true);
            node.SetValue(Outputs, BackgroundSnapshot.FlowList(outputs ?? Enumerable.Empty<KeyValuePair<string, double>>()), true);
            node.SetValue(TimeKey, timeKey, true);
        }

        // ---------------------------------------------------------------- background step

        /// <summary>What every consumer gets in a step.</summary>
        public sealed class StepContext
        {
            public Vessel Vessel;
            public ResourceLedger Ledger;
            public double Elapsed;
            public double Now;
            /// <summary>Waste heat fill of the vessel's plant at its operating point (0 without a modelled reactor).</summary>
            public double WasteHeatFill;
        }

        public abstract class Consumer
        {
            public readonly ConsumerDemand Demand = new ConsumerDemand();
            public string Name;
            public ConfigNode Node;
            public string TimeKey;
            /// <summary>
            /// Its power is drawn in the background but it does not run there; its catch-up time is not moved, so its
            /// own catch-up produces on load (a refinery activity that needs the loaded vessel), at the share of its
            /// power that was billed (<see cref="CommitBilled"/>).
            /// </summary>
            public bool BilledOnly;

            /// <summary>Sets <see cref="Demand"/> for the coming step (the ledger holds the amounts at its start).</summary>
            public abstract void Prepare(StepContext step);

            /// <summary>Runs the step with <paramref name="receivedMW"/> of power on <paramref name="ledger"/>; writes nothing into the snapshot.</summary>
            public abstract void Run(StepContext step, ResourceLedger ledger, double receivedMW);

            /// <summary>
            /// Writes what the step did into the snapshot, once the whole step succeeded: by default the module's own
            /// catch-up time (it was run for this time). Not called for a billed-only consumer.
            /// </summary>
            public virtual void Commit(StepContext step, string nowText)
            {
                if (!string.IsNullOrEmpty(TimeKey))
                    Node.SetValue(TimeKey, nowText, true);
            }

            /// <summary>A billed-only consumer, once the whole step succeeded: <paramref name="receivedMW"/> was billed for it.</summary>
            public virtual void CommitBilled(StepContext step, double receivedMW)
            {
            }
        }

        /// <summary>The consumers of a vessel snapshot.</summary>
        public sealed class ConsumerReading
        {
            public readonly List<Consumer> Consumers = new List<Consumer>();
            /// <summary>Billed-only consumers and why (for the log).</summary>
            public readonly List<string> BilledOnly = new List<string>();
            /// <summary>Consumers the model can neither run nor bill, and why: the vessel's plant keeps its own catch-up.</summary>
            public readonly List<string> Unmodelled = new List<string>();
        }

        /// <summary>
        /// Consumer modules and their persistent "switched on" field: one switched on without background values (saved
        /// before this version, not loaded since) would catch up on load with power nobody paid for.
        /// </summary>
        private static readonly Dictionary<string, string> SwitchedOnField = new Dictionary<string, string>
        {
            { "InterstellarRefineryController", "refinery_is_enabled" },
            { "NuclearRefineryController", "refinery_is_enabled" },
            { "UniversalCrustExtractor", "bIsEnabled" },
            { "RegolithCollector", "bIsEnabled" },
            { "AntimatterFactory", "isActive" },
            { "MetallicHydrogenFactory", "isActive" },
            { "AntimatterCollector", "active" }
        };

        /// <summary>
        /// KSPIE modules that take megajoules when loaded and catch up on load at their saved power ratio, but that have
        /// no background model: switched on, the vessel's plant keeps its own catch-up (which bills their power).
        /// </summary>
        private static readonly Dictionary<string, Func<ConfigNode, bool>> TakesPowerWithoutModel = new Dictionary<string, Func<ConfigNode, bool>>
        {
            // the science lab's catch-up runs only in antimatter mode (ScienceModule.OnStart, active_mode 2)
            { "ScienceModule", node => GetBool(node, "IsEnabled") && node.GetValue("active_mode") == "2" },
            { "ComputerCore", node => GetBool(node, "IsEnabled") },
            { "SolarWindCollector", node => GetBool(node, "bIsEnabled") }
        };

        /// <summary>Reads the consumers of a vessel snapshot.</summary>
        public static ConsumerReading Read(Vessel vessel, ProtoVessel protoVessel)
        {
            var reading = new ConsumerReading();
            foreach (var part in protoVessel.protoPartSnapshots)
            {
                foreach (var module in part.modules)
                {
                    var node = module.moduleValues;
                    if (node == null) continue;
                    var where = part.partName + "/" + module.moduleName;
                    if (TakesPowerWithoutModel.TryGetValue(module.moduleName, out var switchedOn) && switchedOn(node))
                    {
                        reading.Unmodelled.Add(where + ": switched on, takes power but has no background model");
                        continue;
                    }
                    if (node.GetValue(BackgroundSnapshot.Version) != BackgroundSnapshot.CurrentVersion || node.GetValue(ConsumerMarker) == null)
                    {
                        if (SwitchedOnField.TryGetValue(module.moduleName, out var field) && GetBool(node, field))
                            reading.Unmodelled.Add(where + ": switched on, saved without background values (load the vessel once)");
                        continue;
                    }
                    var kind = node.GetValue(ConsumerMarker);
                    if (node.HasValue(BackgroundSnapshot.SaveError))
                    {
                        reading.Unmodelled.Add(where + ": its background values were not saved");
                        continue;
                    }
                    try
                    {
                        if (kind == RefineryKind)
                            RefineryConsumer.Read(vessel, part, node, where, reading);
                        else if (kind == RecipeKind)
                            RecipeConsumer.Read(node, where, reading);
                        else if (kind == TransmitterKind)
                            TransmitterConsumer.Read(protoVessel, node, where, reading);
                    }
                    catch (Exception e)
                    {
                        reading.Unmodelled.Add(where + ": " + e.GetType().Name + ": " + e.Message);
                    }
                }
            }
            return reading;
        }

        internal static bool TryGet(ConfigNode node, string key, out double value)
        {
            return double.TryParse(node.GetValue(key), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        internal static bool GetBool(ConfigNode node, string key) => bool.TryParse(node.GetValue(key), out var value) && value;

        /// <summary>"Resource=rate;…" (rate ≥ 0); false when an entry is unreadable.</summary>
        private static bool TryParseRates(string text, out List<KeyValuePair<string, double>> rates)
        {
            rates = new List<KeyValuePair<string, double>>();
            if (string.IsNullOrEmpty(text)) return true;
            foreach (var entry in text.Split(';'))
            {
                var pair = entry.Split('=');
                if (pair.Length != 2 || !double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var rate)
                    || double.IsNaN(rate) || double.IsInfinity(rate) || rate < 0)
                    return false;
                if (rate > 0) rates.Add(new KeyValuePair<string, double>(pair[0], rate));
            }
            return true;
        }

        // ---------------------------------------------------------------- refinery

        private sealed class RefineryConsumer : Consumer
        {
            /// <summary>Activities that need the loaded vessel: the scoop and intakes (air, sea water), the reactors' own stores (reprocessing).</summary>
            private static readonly HashSet<string> LoadedOnly = new HashSet<string> { "AtmosphereProcessor", "OceanProcessor", "NuclearFuelReprocessor" };

            private static Dictionary<string, Type> _byClass;
            private static Dictionary<string, Type> _byActivityName;

            private RefineryActivity _activity;
            private LedgerRefineryResources _resources;
            private double _powerScale;
            private double _fullMW;
            private double _requestedRatio;
            private double _productionModifier;
            private bool _overflow;
            private double _receivedRatio;

            public static void Read(Vessel vessel, ProtoPartSnapshot part, ConfigNode node, string where, ConsumerReading reading)
            {
                // an empty activity name: the loaded refinery found no activity, it does not restart (OnStart) — off
                if (!GetBool(node, "refinery_is_enabled") || string.IsNullOrEmpty(node.GetValue("lastActivityName"))) return;

                var type = FindActivity(node.GetValue("lastClassName"), node.GetValue("lastActivityName"));
                if (type == null)
                {
                    reading.Unmodelled.Add(where + ": unknown refinery activity '" + node.GetValue("lastClassName") + "' / '" + node.GetValue("lastActivityName") + "'");
                    return;
                }
                var prefab = part.partInfo?.partPrefab;
                if (!TryGet(node, PowerScale, out var powerScale) || !TryGet(node, ProductionModifier, out var productionModifier) || prefab == null)
                {
                    reading.Unmodelled.Add(where + ": missing or unreadable refinery values");
                    return;
                }

                var consumer = new RefineryConsumer
                {
                    Name = where + " (" + type.Name + ")",
                    Node = node,
                    TimeKey = "lastActiveTime",
                    BilledOnly = LoadedOnly.Contains(type.Name),
                    _resources = new LedgerRefineryResources(vessel,
                        (node.GetValue(Unreachable) ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)),
                    _powerScale = Math.Max(0, powerScale),
                    _productionModifier = Math.Max(0, productionModifier),
                    _overflow = GetBool(node, "lastOverflowSettings")
                };
                consumer._requestedRatio = TryGet(node, "powerPercentage", out var percentage) ? Math.Max(0, Math.Min(100, percentage)) / 100 : 1;
                consumer.Demand.Priority = TryGet(node, Priority, out var priority) ? (int)priority : 4;
                consumer.Demand.TakesBattery = true; // InterstellarRefineryController.FixedUpdate takes ElectricCharge for the shortage
                consumer._activity = (RefineryActivity)Activator.CreateInstance(type);
                consumer._activity.InitializeForBackground(prefab, consumer._resources);
                reading.Consumers.Add(consumer);
                if (consumer.BilledOnly)
                    reading.BilledOnly.Add(consumer.Name + ": needs the loaded vessel - produces on load, its power is drawn in the background");
            }

            /// <summary>Maximum power of the activity at full slider, MW (InterstellarRefineryController.currentPowerReq).</summary>
            private double FullPowerMW => _powerScale * _activity.PowerRequirements;

            public override void Prepare(StepContext step)
            {
                _fullMW = FullPowerMW;
                Demand.DemandMW = _fullMW * _requestedRatio;
            }

            public override void Run(StepContext step, ResourceLedger ledger, double receivedMW)
            {
                // as InterstellarRefineryController.FixedUpdate
                _receivedRatio = _fullMW > 0 ? receivedMW / _fullMW : 0;
                _resources.Ledger = ledger;
                // some activities read their stock only here (the Haber process: available hydrogen and nitrogen, room
                // for ammonia); on a loaded vessel the refinery window calls it every frame
                _activity.HasActivityRequirements();
                _activity.UpdateFrame(_requestedRatio * _receivedRatio * _productionModifier, _requestedRatio * _receivedRatio,
                    _requestedRatio * _productionModifier, _overflow, step.Elapsed);
            }

            public override void Commit(StepContext step, string nowText)
            {
                // the refinery's own catch-up on load runs at this ratio
                Node.SetValue("lastPowerRatio", BackgroundSnapshot.Number(_receivedRatio), true);
                base.Commit(step, nowText);
            }

            /// <summary>
            /// The catch-up on load produces for all the time since lastActiveTime at lastPowerRatio (InterstellarRefinery
            /// OnStart): that ratio becomes the share of its power billed, averaged over the steps since that time.
            /// </summary>
            public override void CommitBilled(StepContext step, double receivedMW)
            {
                var share = _fullMW > 0 ? Math.Max(0, receivedMW) / _fullMW : 0;
                var since = Node.GetValue(TimeKey) ?? "";
                double seconds = 0, average = 0;
                if (Node.GetValue(BilledSince) == since)
                {
                    TryGet(Node, BilledSeconds, out seconds);
                    TryGet(Node, BilledRatio, out average);
                }
                average = StepPlan.TimeAverage(average, seconds, share, step.Elapsed);
                Node.SetValue(BilledSince, since, true);
                Node.SetValue(BilledSeconds, BackgroundSnapshot.Number(Math.Max(0, seconds) + step.Elapsed), true);
                Node.SetValue(BilledRatio, BackgroundSnapshot.Number(average), true);
                Node.SetValue("lastPowerRatio", BackgroundSnapshot.Number(average), true);
            }

            private static Type FindActivity(string className, string activityName)
            {
                if (_byClass == null)
                {
                    Type[] types;
                    try
                    {
                        types = typeof(RefineryActivity).Assembly.GetTypes();
                    }
                    catch (ReflectionTypeLoadException e)
                    {
                        types = e.Types.Where(t => t != null).ToArray();
                    }
                    var byClass = new Dictionary<string, Type>();
                    var byActivityName = new Dictionary<string, Type>();
                    foreach (var type in types)
                    {
                        if (type.IsAbstract || !typeof(RefineryActivity).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) == null)
                            continue;
                        byClass[type.Name] = type;
                        try
                        {
                            var name = ((RefineryActivity)Activator.CreateInstance(type)).ActivityName;
                            if (!string.IsNullOrEmpty(name)) byActivityName[name] = type;
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning("[KSPI]: background refinery: cannot create " + type.Name + ": " + e.Message);
                        }
                    }
                    _byActivityName = byActivityName;
                    _byClass = byClass;
                }
                if (!string.IsNullOrEmpty(className) && _byClass.TryGetValue(className, out var byClassType)) return byClassType;
                if (!string.IsNullOrEmpty(activityName) && _byActivityName.TryGetValue(activityName, out var byName)) return byName;
                return null;
            }
        }

        // ---------------------------------------------------------------- recipe consumers

        private sealed class RecipeConsumer : Consumer
        {
            private double _demandMW;
            private double _minimumShare;
            private List<KeyValuePair<string, double>> _inputs;
            private List<KeyValuePair<string, double>> _outputs;

            public static void Read(ConfigNode node, string where, ConsumerReading reading)
            {
                // off, or unable to work where it is (the drill not landed or extended): runs neither loaded nor here
                if (!GetBool(node, BackgroundSnapshot.Active)) return;
                var timeKey = node.GetValue(BackgroundConsumers.TimeKey);
                if (!TryGet(node, DemandMW, out var demand) || string.IsNullOrEmpty(timeKey)
                    || !TryParseRates(node.GetValue(Inputs), out var inputs) || !TryParseRates(node.GetValue(Outputs), out var outputs))
                {
                    reading.Unmodelled.Add(where + ": missing or unreadable consumer values");
                    return;
                }
                var consumer = new RecipeConsumer
                {
                    Name = where,
                    Node = node,
                    TimeKey = timeKey,
                    _demandMW = Math.Max(0, demand),
                    _minimumShare = TryGet(node, MinimumShare, out var minimum) ? Math.Max(0, Math.Min(1, minimum)) : 0,
                    _inputs = inputs,
                    _outputs = outputs
                };
                consumer.Demand.Priority = TryGet(node, Priority, out var priority) ? (int)priority : 2;
                consumer.Demand.WasteHeatFraction = TryGet(node, HeatFraction, out var heat) ? Math.Max(0, Math.Min(1, heat)) : 0;
                consumer.Demand.TakesBattery = GetBool(node, TakesBattery);
                consumer.Demand.PlantSlider = TryGet(node, PlantSlider, out var slider) ? Math.Max(0, Math.Min(1, slider)) : -1;
                reading.Consumers.Add(consumer);
            }

            public override void Prepare(StepContext step)
            {
                // a module with nothing to work on asks for no power (the metallic hydrogen factory checks its input first)
                var starved = _inputs.Any(input => step.Ledger.Amount(input.Key) <= 0);
                Demand.DemandMW = starved ? 0 : _demandMW;
            }

            public override void Run(StepContext step, ResourceLedger ledger, double receivedMW)
            {
                if (Demand.DemandMW <= 0 && _demandMW > 0) return;
                var share = _demandMW > 0 ? receivedMW / _demandMW : 1;
                RecipeStep.Run(ledger, _inputs, _outputs, share, _minimumShare, step.Elapsed);
            }
        }

        // ---------------------------------------------------------------- beamed power transmitter

        private sealed class TransmitterConsumer : Consumer
        {
            private ConfigNode _generator;
            private double _capacityMW;
            private double _demandMW;
            private double _baseEfficiency;
            private double _lifeHours, _endLoss, _maxLoss, _heatDoublings;
            private double _efficiency;
            private bool _working;
            private double _receivedMW;
            private double _elapsed;
            private double _fill;

            public static void Read(ProtoVessel protoVessel, ConfigNode node, string where, ConsumerReading reading)
            {
                // off, a relay, or its beam generator broken when saved: no beam (FixedUpdate saved nuclear_power 0)
                if (!GetBool(node, BackgroundSnapshot.Active)) return;
                if (!TryGet(node, DemandMW, out var demand) || !TryGet(node, BeamEfficiency, out var efficiency)
                    || !uint.TryParse(node.GetValue(GeneratorPart), out var generatorPart))
                {
                    reading.Unmodelled.Add(where + ": missing or unreadable transmitter values");
                    return;
                }
                var generatorModule = node.GetValue(GeneratorModule);
                ConfigNode generator = null;
                foreach (var part in protoVessel.protoPartSnapshots)
                {
                    if (part.flightID != generatorPart) continue;
                    foreach (var module in part.modules)
                        if (module.moduleName == generatorModule && module.moduleValues != null)
                        {
                            generator = module.moduleValues;
                            break;
                        }
                    break;
                }
                if (generator == null)
                {
                    reading.Unmodelled.Add(where + ": beam generator " + generatorModule + " on part " + generatorPart + " not found");
                    return;
                }

                var consumer = new TransmitterConsumer
                {
                    Name = where,
                    Node = node,
                    _generator = generator,
                    _capacityMW = TryGet(node, "power_capacity", out var capacity) ? Math.Max(0, capacity) : 0,
                    _demandMW = Math.Max(0, demand),
                    _baseEfficiency = Math.Max(0, Math.Min(1, efficiency)),
                    _lifeHours = TryGet(node, WearLifeHours, out var life) ? life : 0,
                    _endLoss = TryGet(node, WearEndLoss, out var endLoss) ? endLoss : 0.2,
                    _maxLoss = TryGet(node, WearMaxLoss, out var maxLoss) ? maxLoss : 0.9,
                    _heatDoublings = TryGet(node, WearHeatDoublings, out var doublings) ? doublings : 3
                };
                consumer.Demand.Priority = TryGet(node, Priority, out var priority) ? (int)priority : 2;
                consumer.Demand.TakesBattery = false; // the transmitter takes megajoules only
                consumer.Demand.PlantSlider = TryGet(node, PlantSlider, out var slider) ? Math.Max(0, Math.Min(1, slider)) : -1;
                reading.Consumers.Add(consumer);
            }

            public override void Prepare(StepContext step)
            {
                // a generator broken while unloaded (Kerbalism Reliability: isEnabled = False) makes no beam and takes
                // no power, as MicrowavePowerTransmitter.FixedUpdate
                _working = !bool.TryParse(_generator.GetValue("isEnabled"), out var enabled) || enabled;
                TryGet(_generator, "wearHours", out var wearHours);
                _efficiency = BeamRules.WornEfficiency(_baseEfficiency, wearHours, _lifeHours, _endLoss, _maxLoss);
                Demand.DemandMW = _working ? _demandMW : 0;
                Demand.WasteHeatFraction = 1 - _efficiency;
            }

            public override void Run(StepContext step, ResourceLedger ledger, double receivedMW)
            {
                _receivedMW = Demand.DemandMW > 0 ? Math.Max(0, receivedMW) : 0;
                _elapsed = step.Elapsed;
                _fill = step.WasteHeatFill;
            }

            public override void Commit(StepContext step, string nowText)
            {
                // the beam KSPIE's receivers read from an unloaded transmitter (VesselMicrowavePersistence: solar_power
                // scaled by the sun, + nuclear_power). As FixedUpdate: the saved solar_power (the solar cells' power
                // through the beam, which KSPIE beams even without megajoules from them under Kerbalism) stays, and
                // nuclear_power is the beam of the power received less it. A broken beam generator beams nothing.
                if (_working)
                {
                    TryGet(Node, "solar_power", out var solar);
                    Node.SetValue("nuclear_power", BackgroundSnapshot.Number(Math.Max(0, BeamRules.BeamKilowatts(_receivedMW, _efficiency) - Math.Max(0, solar))), true);
                }
                else
                {
                    Node.SetValue("nuclear_power", "0", true);
                    Node.SetValue("solar_power", "0", true);
                }

                // wear for the step, and the rate the generator's own catch-up would use from now
                if (_lifeHours > 0)
                {
                    var rate = BeamRules.WearRate(_capacityMW > 0 ? _receivedMW / _capacityMW : 0, _fill, _heatDoublings);
                    TryGet(_generator, "wearHours", out var wearHours);
                    _generator.SetValue("wearHours", BackgroundSnapshot.Number(Math.Max(0, wearHours) + _elapsed / 3600 * rate), true);
                    _generator.SetValue("wearRate", BackgroundSnapshot.Number(rate), true);
                    _generator.SetValue("lastWearTime", nowText, true);
                }
            }
        }
    }
}
