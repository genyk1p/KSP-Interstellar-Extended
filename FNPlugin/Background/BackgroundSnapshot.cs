using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using FNPlugin.Background.Core;
using UnityEngine;

namespace FNPlugin.Background
{
    /// <summary>
    /// Values the KSPIE power modules write into their save node (OnSave, flight only) for the background model,
    /// and the reader that builds a <see cref="VesselSpec"/> from an unloaded vessel's snapshot.
    /// Every value is what the module itself computed while loaded; the reader only checks and combines them.
    ///
    /// A reactor is either run by the model completely, or left completely to its own catch-up on load
    /// (InterstellarReactor.DoPersistentResourceUpdate): a reactor whose power also goes to something the model does
    /// not run (an MHD generator, an engine, a beamed power transmitter saved before step A3) is left out, so that its
    /// fuel is still billed on load. When the vessel's power is not known completely (<see cref="VesselReading.PowerIncomplete"/>)
    /// and it has KSPIE consumers, InterstellarBackgroundPower leaves the whole plant to its catch-up
    /// (<see cref="VesselReading.LeaveAllToCatchUp"/>).
    /// </summary>
    public static class BackgroundSnapshot
    {
        /// <summary>Format of the bg* keys; a node written by another version is skipped (its vessel stays on the catch-up).</summary>
        public const string CurrentVersion = "1";
        public const string Version = "bgVersion";

        public const string ReactorMarker = "bgReactor";
        public const string GeneratorMarker = "bgGenerator";
        public const string RadiatorMarker = "bgRadiator";

        public const string Active = "bgActive";
        /// <summary>Writing the module's values failed (the exception is the value): the model does not know what it does.</summary>
        public const string SaveError = "bgSaveError";
        /// <summary>A reactor that runs but that the model cannot run (fusion plasma upkeep, no fuel mode): its power is unknown.</summary>
        public const string PowerUnknown = "bgPowerUnknown";

        public const string ThermalMW = "bgThermalMW";
        public const string ChargedMW = "bgChargedMW";
        public const string HotBathK = "bgHotBathK";
        public const string MinimumThrottle = "bgMinimumThrottle";
        public const string ChargedWasteHeat = "bgChargedWasteHeat";
        public const string OverheatFill = "bgOverheatFill";
        public const string Fuels = "bgFuels";
        public const string LocalFuels = "bgLocalFuels";
        public const string LocalProducts = "bgLocalProducts";
        public const string Products = "bgProducts";
        public const string BreedingInputs = "bgBreedingInputs";
        public const string BreedingOutputs = "bgBreedingOutputs";

        public const string ReactorPart = "bgReactorPart";
        public const string Mode = "bgMode";
        public const string Mhd = "bgMHD";
        public const string LimitedToMinimumThrottle = "bgLimitedToMinimumThrottle";
        public const string MaxEfficiency = "bgMaxEfficiency";
        public const string Share = "bgShare";
        public const string UsesCharged = "bgUsesCharged";

        public const string StefanArea = "bgStefanArea";
        public const string MaxTemperature = "bgMaxTemperature";
        public const string EnvironmentMax = "bgEnvironmentMax";
        public const string ExternalK = "bgExternalK";
        public const string Mass = "bgMass";
        public const string Enabled = "bgEnabled";

        /// <summary>The reactor's own catch-up on load (InterstellarReactor.DoPersistentResourceUpdate) starts from this time.</summary>
        public const string ReactorLastActiveTime = "last_active_time";

        /// <summary>Beamed power transmitter classes (BeamedPowerTransmitter and subclasses): their persistent IsEnabled.
        /// Since step A3 a transmitter with background values is a consumer (BackgroundConsumers).</summary>
        private static readonly HashSet<string> TransmitterModules = new HashSet<string>
            { "BeamedPowerTransmitter", "MicrowavePowerTransmitter", "PhasedArrayTransmitter", "BeamedPowerLaserTransmitter" };

        public static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>"Resource=unitsPerMJ;Resource=unitsPerMJ" (resource names contain no '=' or ';').</summary>
        public static string FlowList(IEnumerable<KeyValuePair<string, double>> flows)
        {
            var sb = new StringBuilder();
            foreach (var flow in flows)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append(flow.Key).Append('=').Append(Number(flow.Value));
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- writing (part modules, OnSave)

        /// <summary>The bg* values of a module node, kept from OnLoad so that a save before the module ran can rewrite them.</summary>
        public static ConfigNode CopyBackgroundValues(ConfigNode node)
        {
            if (node == null || !node.HasValue(Version)) return null;
            var copy = new ConfigNode();
            foreach (ConfigNode.Value value in node.values)
                if (value.name.StartsWith("bg", StringComparison.Ordinal))
                    copy.AddValue(value.name, value.value);
            return copy;
        }

        /// <summary>
        /// Writes the module's background values. Until the module has run in this flight (<paramref name="ready"/>),
        /// its fields may not be set up yet (a generator not yet attached to its reactor): the values loaded with the
        /// vessel are written back instead. An exception must not reach KSP: it would abort saving the whole vessel.
        /// </summary>
        public static void Save(PartModule module, ConfigNode node, bool ready, ConfigNode loaded, Action<ConfigNode> write, ref bool errorLogged)
        {
            try
            {
                if (!ready && loaded != null)
                {
                    foreach (ConfigNode.Value value in loaded.values)
                        node.SetValue(value.name, value.value, true);
                    return;
                }
                node.SetValue(Version, CurrentVersion, true);
                if (node.HasValue(SaveError)) node.RemoveValue(SaveError);
                write(node);
            }
            catch (Exception e)
            {
                node.SetValue(Active, "False", true);
                node.SetValue(SaveError, e.GetType().Name + ": " + e.Message, true);
                if (errorLogged) return;
                errorLogged = true;
                Debug.LogError("[KSPI]: background values not saved for " + module.part?.partInfo?.name + "/" + module.GetType().Name + ": " + e);
            }
        }

        // ---------------------------------------------------------------- reading (background step)

        /// <summary>Result of reading a vessel snapshot.</summary>
        public sealed class VesselReading
        {
            public readonly VesselSpec Spec = new VesselSpec();
            /// <summary>Per reactor in Spec.Reactors: its module node (catch-up time moved forward), its part, fuels kept inside it.</summary>
            public readonly List<ConfigNode> ReactorNodes = new List<ConfigNode>();
            public readonly List<ProtoPartSnapshot> ReactorParts = new List<ProtoPartSnapshot>();
            public readonly List<List<string>> ReactorLocalFuels = new List<List<string>>();
            public readonly List<List<string>> ReactorLocalProducts = new List<List<string>>();
            /// <summary>
            /// Stock ModuleGenerator nodes on parts whose KSPIE generator the model runs. FNGenerator switches the part's
            /// stock generator on while it runs (generatorIsActive is saved), and Kerbalism's own background then makes
            /// that generator's config output for free (2000 EC/s on the liquid metal reactor, 7e8 EC/s on a warp core):
            /// the step switches them off; FNGenerator switches them back on after the vessel loads.
            /// </summary>
            public readonly List<ConfigNode> StockGenerators = new List<ConfigNode>();
            /// <summary>Capacity of every resource in flow-enabled storage (Kerbalism counts only those).</summary>
            public readonly Dictionary<string, double> Capacity = new Dictionary<string, double>();
            /// <summary>Reactors left to their own catch-up, and why (for the log).</summary>
            public readonly List<string> Excluded = new List<string>();
            /// <summary>
            /// The vessel may get electric power the model does not run: a reactor with a generator left to its catch-up
            /// (MHD, Kerbalism process), a reactor that runs but cannot be modelled (fusion), a beamed power receiver the
            /// model does not run (solar thermal, slave or master, saved before step A3), a generator fed by an unknown
            /// source, or a module whose values could not be written or read. With KSPIE consumers on board,
            /// InterstellarBackgroundPower then leaves the whole plant to its catch-up (consumers included).
            /// </summary>
            public bool PowerIncomplete;
            /// <summary>Unreadable values (for the log).</summary>
            public readonly List<string> Problems = new List<string>();
            /// <summary>
            /// Beamed power receivers the model runs (step A3). A thermal mode one is also a fuel-less pseudo-reactor in
            /// Spec.Reactors (ReactorIndex; its ReactorNodes entry is null), so that its generators attach to it.
            /// </summary>
            public readonly List<BackgroundBeams.Receiver> Receivers = new List<BackgroundBeams.Receiver>();

            /// <summary>
            /// Leaves every reactor and receiver to its own catch-up on load, as without the model: the vessel's power
            /// is not known completely and consumers would otherwise run on part of it, or produce on load with power
            /// nobody paid for.
            /// </summary>
            public void LeaveAllToCatchUp(string reason)
            {
                foreach (var part in ReactorParts)
                    Excluded.Add(part.partName + ": " + reason);
                Spec.Reactors.Clear();
                Spec.Generators.Clear();
                ReactorNodes.Clear();
                ReactorParts.Clear();
                ReactorLocalFuels.Clear();
                ReactorLocalProducts.Clear();
                StockGenerators.Clear();
                Receivers.Clear();
            }
        }

        private sealed class ReactorEntry
        {
            public ProtoPartSnapshot Part;
            public ConfigNode Node;
            public ReactorSpec Spec;
            public List<string> LocalFuels;
            public List<string> LocalProducts;
            public bool HasGenerator;
            public string Exclusion;
            /// <summary>A thermal mode beamed power receiver standing in for a reactor (no fuel, no catch-up time).</summary>
            public BackgroundBeams.Receiver Receiver;
        }

        private static bool TryGet(ConfigNode node, string key, out double value)
        {
            var text = node.GetValue(key);
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static double Get(ConfigNode node, string key, double fallback)
        {
            return TryGet(node, key, out var value) ? value : fallback;
        }

        private static bool GetBool(ConfigNode node, string key) => bool.TryParse(node.GetValue(key), out var value) && value;

        private static List<ResourcePerMJ> ParseFlowList(string text, string where, List<string> problems)
        {
            var list = new List<ResourcePerMJ>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (var entry in text.Split(';'))
            {
                var pair = entry.Split('=');
                if (pair.Length == 2 && double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var perMJ)
                    && !double.IsNaN(perMJ) && !double.IsInfinity(perMJ) && perMJ >= 0)
                {
                    if (perMJ > 0)
                        list.Add(new ResourcePerMJ(pair[0], perMJ));
                }
                else
                    problems.Add(where + ": unreadable entry '" + entry + "'");
            }
            return list;
        }

        public static VesselReading Read(ProtoVessel protoVessel, double spaceTemperature)
        {
            var reading = new VesselReading();
            reading.Spec.ExternalTemperatureK = spaceTemperature;
            var reactors = new List<ReactorEntry>();
            var reactorByPart = new Dictionary<uint, ReactorEntry>();
            var generators = new List<KeyValuePair<ConfigNode, ProtoPartSnapshot>>();
            // parts whose reactor or receiver is off (or receives no beams): their generators get nothing, nothing to model
            var offSources = new HashSet<uint>();
            var transmitting = false;

            foreach (var part in protoVessel.protoPartSnapshots)
            {
                foreach (var resource in part.resources)
                    if (resource.flowState)
                    {
                        reading.Capacity.TryGetValue(resource.resourceName, out var capacity);
                        reading.Capacity[resource.resourceName] = capacity + resource.maxAmount;
                    }

                foreach (var module in part.modules)
                {
                    var node = module.moduleValues;
                    if (node == null) continue;
                    var where = part.partName + "/" + module.moduleName;

                    // an active transmitter the model does not run yet (saved before step A3: no transmitter
                    // values) still takes power the model would not bill: its vessel's reactors keep their catch-up
                    if (TransmitterModules.Contains(module.moduleName) && GetBool(node, "IsEnabled")
                        && !(node.GetValue(Version) == CurrentVersion && node.GetValue(BackgroundConsumers.ConsumerMarker) == BackgroundConsumers.TransmitterKind))
                        transmitting = true;
                    // BeamedPowerReceiver and its subclasses (…PowerReceiver, …PowerReceiverDish, …PowerReceiverPanel)
                    // saved before step A3 (no receiver values): the model does not know what it gives
                    if (module.moduleName.Contains("PowerReceiver") && GetBool(node, "receiverIsEnabled")
                        && !(node.GetValue(Version) == CurrentVersion && node.HasValue(BackgroundBeams.ReceiverMarker)))
                        reading.PowerIncomplete = true;

                    if (node.GetValue(Version) != CurrentVersion) continue;
                    if (node.HasValue(SaveError))
                    {
                        reading.Problems.Add(where + ": its background values were not saved (" + node.GetValue(SaveError) + ")");
                        reading.PowerIncomplete = true;
                        continue;
                    }
                    if (!GetBool(node, Active))
                    {
                        if (node.HasValue(ReactorMarker) && GetBool(node, PowerUnknown))
                        {
                            reading.Excluded.Add(part.partName + ": runs, but the model cannot run it (fusion plasma upkeep or no fuel mode)");
                            reading.PowerIncomplete = true;
                        }
                        else if (node.HasValue(ReactorMarker) && !node.HasValue(PowerUnknown) && GetBool(node, "IsEnabled"))
                        {
                            // saved by the step A1 build, which wrote no PowerUnknown: a running reactor it could not run
                            reading.Excluded.Add(part.partName + ": runs, saved before the model knew whether it can run it (load the vessel once)");
                            reading.PowerIncomplete = true;
                        }
                        else if (node.HasValue(ReactorMarker) || node.HasValue(BackgroundBeams.ReceiverMarker))
                            offSources.Add(part.flightID);
                        continue;
                    }

                    if (node.HasValue(BackgroundBeams.ReceiverMarker))
                    {
                        // solar thermal input, thermal receiver slaves and masters: not modelled
                        if (!GetBool(node, BackgroundBeams.Modelled))
                        {
                            reading.Excluded.Add(part.partName + ": beamed power receiver the model does not run (solar thermal, slave or master)");
                            reading.PowerIncomplete = true;
                            continue;
                        }
                        var problems = reading.Problems.Count;
                        var receiver = BackgroundBeams.Read(protoVessel.vesselRef, part, node, where, reading.Problems);
                        if (receiver == null)
                        {
                            if (reading.Problems.Count > problems)
                                reading.PowerIncomplete = true; // unreadable
                            else
                                offSources.Add(part.flightID); // not in beamed power mode
                            continue;
                        }
                        if (receiver.BandList.Count == 0)
                            reading.Problems.Add(where + ": no wavelength band saved");
                        if (reading.Problems.Count > problems)
                        {
                            // a band it could not read (or none): the model would give less than the receiver does
                            reading.PowerIncomplete = true;
                            continue;
                        }
                        if (receiver.Thermal && reactorByPart.ContainsKey(part.flightID))
                        {
                            // its pseudo-reactor would share the part with another one (generators attach by part)
                            reading.Problems.Add(where + ": thermal receiver on a part that has a reactor or another thermal receiver");
                            reading.PowerIncomplete = true;
                            continue;
                        }
                        reading.Receivers.Add(receiver);
                        if (!receiver.Thermal) continue;
                        var receiverEntry = new ReactorEntry
                        {
                            Part = part,
                            Node = null,
                            Spec = new ReactorSpec { ThermalMW = 0, ChargedMW = 0, HotBathK = receiver.HotBath, MinimumThrottle = receiver.Minimum },
                            LocalFuels = new List<string>(),
                            LocalProducts = new List<string>(),
                            Receiver = receiver
                        };
                        reactors.Add(receiverEntry);
                        reactorByPart[part.flightID] = receiverEntry;
                    }
                    else if (node.HasValue(ReactorMarker))
                    {
                        if (reactorByPart.ContainsKey(part.flightID))
                        {
                            // one reactor per part: generators attach to a reactor by its part
                            reading.Problems.Add(where + ": a second reactor or thermal receiver on the part");
                            reading.PowerIncomplete = true;
                            continue;
                        }
                        if (!TryGet(node, ThermalMW, out var thermal) || !TryGet(node, ChargedMW, out var charged) || !TryGet(node, HotBathK, out var hotBath))
                        {
                            reading.Problems.Add(where + ": missing or unreadable reactor power");
                            reading.PowerIncomplete = true;
                            continue;
                        }
                        var spec = new ReactorSpec
                        {
                            ThermalMW = thermal,
                            ChargedMW = charged,
                            HotBathK = hotBath,
                            MinimumThrottle = Get(node, MinimumThrottle, 0),
                            ChargedProducesWasteHeat = GetBool(node, ChargedWasteHeat),
                            OverheatFill = Get(node, OverheatFill, 0.95)
                        };
                        spec.Fuels.AddRange(ParseFlowList(node.GetValue(Fuels), where, reading.Problems));
                        spec.Products.AddRange(ParseFlowList(node.GetValue(Products), where, reading.Problems));
                        spec.BreedingInputs.AddRange(ParseFlowList(node.GetValue(BreedingInputs), where, reading.Problems));
                        spec.BreedingOutputs.AddRange(ParseFlowList(node.GetValue(BreedingOutputs), where, reading.Problems));

                        var entry = new ReactorEntry
                        {
                            Part = part,
                            Node = node,
                            Spec = spec,
                            LocalFuels = new List<string>((node.GetValue(LocalFuels) ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)),
                            LocalProducts = new List<string>((node.GetValue(LocalProducts) ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                        };
                        // a Kerbalism process already burns this fuel mode (InterstellarReactor skips its own catch-up then)
                        var variant = node.GetValue("fuel_mode_variant");
                        if (!string.IsNullOrEmpty(variant) && part.resources.Exists(r => r.resourceName == "_" + variant))
                            entry.Exclusion = "a Kerbalism process runs its fuel";
                        reactors.Add(entry);
                        reactorByPart[part.flightID] = entry;
                    }
                    else if (node.HasValue(GeneratorMarker))
                        generators.Add(new KeyValuePair<ConfigNode, ProtoPartSnapshot>(node, part));
                    else if (node.HasValue(RadiatorMarker))
                    {
                        if (!TryGet(node, StefanArea, out var area) || !TryGet(node, MaxTemperature, out var maxTemperature) || !TryGet(node, EnvironmentMax, out var environmentMax))
                        {
                            // a radiator missing from the model makes the plant hotter, never gives free power
                            reading.Problems.Add(where + ": missing or unreadable radiator values");
                            continue;
                        }
                        reading.Spec.Radiators.Add(new RadiatorSpec
                        {
                            StefanArea = area,
                            MaxTemperatureK = maxTemperature,
                            EnvironmentMaxK = environmentMax,
                            ExternalK = Get(node, ExternalK, 0),
                            Mass = Get(node, Mass, 1),
                            Enabled = GetBool(node, Enabled)
                        });
                    }
                }
            }

            // Generators: which reactor feeds which; MHD generators (not modelled) leave their reactor to the catch-up.
            var generatorSpecs = new List<KeyValuePair<ReactorEntry, GeneratorSpec>>();
            var generatorParts = new Dictionary<GeneratorSpec, ProtoPartSnapshot>();
            foreach (var pair in generators)
            {
                var node = pair.Key;
                var where = pair.Value.partName + "/generator";
                if (!uint.TryParse(node.GetValue(ReactorPart), out var reactorPart) || !reactorByPart.TryGetValue(reactorPart, out var reactor))
                {
                    // its reactor or receiver is off: nothing to model; otherwise its power comes from something the
                    // model does not run (an older save of the source, an unknown source)
                    if (!offSources.Contains(reactorPart))
                    {
                        reading.Problems.Add(where + ": fed by part " + node.GetValue(ReactorPart) + ", which the model does not run");
                        reading.PowerIncomplete = true;
                    }
                    continue;
                }
                if (GetBool(node, Mhd))
                {
                    reactor.Exclusion = reactor.Exclusion ?? "an MHD generator";
                    reactor.HasGenerator = true; // its power reaches the vessel: PowerIncomplete
                    continue;
                }
                if (!TryGet(node, MaxEfficiency, out var efficiency))
                {
                    reading.Problems.Add(where + ": missing or unreadable generator efficiency");
                    reading.PowerIncomplete = true;
                    continue;
                }
                reactor.HasGenerator = true;
                var generatorSpec = new GeneratorSpec
                {
                    Mode = node.GetValue(Mode) == nameof(GeneratorMode.Charged) ? GeneratorMode.Charged : GeneratorMode.Thermal,
                    MaxEfficiency = efficiency,
                    Share = Get(node, Share, 1),
                    UsesChargedPower = GetBool(node, UsesCharged),
                    LimitedToMinimumThrottle = GetBool(node, LimitedToMinimumThrottle)
                };
                generatorSpecs.Add(new KeyValuePair<ReactorEntry, GeneratorSpec>(reactor, generatorSpec));
                generatorParts[generatorSpec] = pair.Value;
            }

            var index = new Dictionary<ReactorEntry, int>();
            foreach (var reactor in reactors)
            {
                if (reactor.Receiver != null)
                {
                    // a thermal receiver without a generator gives nothing the model uses: left out silently; with a
                    // transmitter saved before step A3 on board, its power counts as unknown like the reactors' below.
                    // Left out, it takes nothing from the transmitters either.
                    if (transmitting && reactor.HasGenerator) reading.PowerIncomplete = true;
                    if (!reactor.HasGenerator || transmitting)
                    {
                        reading.Receivers.Remove(reactor.Receiver);
                        continue;
                    }
                }
                var exclusion = transmitting ? "a beamed power transmitter is on without background values (load the vessel once)"
                    : reactor.Exclusion ?? (reactor.HasGenerator ? null : "no generator (its power goes elsewhere, e.g. an engine)");
                if (exclusion != null)
                {
                    reading.Excluded.Add(reactor.Part.partName + ": " + exclusion);
                    if (reactor.HasGenerator || transmitting)
                        reading.PowerIncomplete = true;
                    continue;
                }
                index[reactor] = reading.Spec.Reactors.Count;
                if (reactor.Receiver != null)
                    reactor.Receiver.ReactorIndex = reading.Spec.Reactors.Count;
                reading.Spec.Reactors.Add(reactor.Spec);
                reading.ReactorNodes.Add(reactor.Node);
                reading.ReactorParts.Add(reactor.Part);
                reading.ReactorLocalFuels.Add(reactor.LocalFuels);
                reading.ReactorLocalProducts.Add(reactor.LocalProducts);
            }
            foreach (var pair in generatorSpecs)
            {
                if (!index.TryGetValue(pair.Key, out var reactorIndex)) continue;
                pair.Value.Reactor = reactorIndex;
                reading.Spec.Generators.Add(pair.Value);
                foreach (var module in generatorParts[pair.Value].modules)
                    if (module.moduleName == "ModuleGenerator" && module.moduleValues != null && !reading.StockGenerators.Contains(module.moduleValues))
                        reading.StockGenerators.Add(module.moduleValues);
            }

            return reading;
        }
    }
}
