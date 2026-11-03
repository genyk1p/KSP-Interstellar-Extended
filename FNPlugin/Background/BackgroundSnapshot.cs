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
    /// not run (beamed power, an MHD generator, an engine) is left out, so that its fuel is still billed on load.
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

        /// <summary>Beamed power transmitter classes (BeamedPowerTransmitter and subclasses): their persistent IsEnabled.</summary>
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
                write(node);
            }
            catch (Exception e)
            {
                node.SetValue(Active, "False", true);
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
            /// <summary>Unreadable values (for the log).</summary>
            public readonly List<string> Problems = new List<string>();
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

                    if (TransmitterModules.Contains(module.moduleName) && GetBool(node, "IsEnabled"))
                        transmitting = true;

                    if (node.GetValue(Version) != CurrentVersion || !GetBool(node, Active)) continue;

                    if (node.HasValue(ReactorMarker))
                    {
                        if (reactorByPart.ContainsKey(part.flightID)) continue; // one reactor per part
                        if (!TryGet(node, ThermalMW, out var thermal) || !TryGet(node, ChargedMW, out var charged) || !TryGet(node, HotBathK, out var hotBath))
                        {
                            reading.Problems.Add(where + ": missing or unreadable reactor power");
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
                    continue; // fed by something the model does not run (beamed power receiver, unknown source)
                if (GetBool(node, Mhd))
                {
                    reactor.Exclusion = reactor.Exclusion ?? "an MHD generator";
                    continue;
                }
                if (!TryGet(node, MaxEfficiency, out var efficiency))
                {
                    reading.Problems.Add(where + ": missing or unreadable generator efficiency");
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
                var exclusion = transmitting ? "a beamed power transmitter is on (beam not in the background yet)"
                    : reactor.Exclusion ?? (reactor.HasGenerator ? null : "no generator (its power goes elsewhere, e.g. an engine)");
                if (exclusion != null)
                {
                    reading.Excluded.Add(reactor.Part.partName + ": " + exclusion);
                    continue;
                }
                index[reactor] = reading.Spec.Reactors.Count;
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
