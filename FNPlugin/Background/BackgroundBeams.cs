using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using FNPlugin.Background.Core;
using FNPlugin.Beamedpower;
using UnityEngine;

namespace FNPlugin.Beamedpower
{
    /// <summary>
    /// A receiver without a part transform (unloaded vessel, background step A3) gives its orientation in world space
    /// for InterstellarBeamedPowerHelper.ComputeFacingFactor.
    /// </summary>
    public interface IBeamedPowerReceiverOrientation
    {
        Vector3d Up { get; }
        Vector3d Forward { get; }
        Vector3d Right { get; }
    }
}

namespace FNPlugin.Background
{
    /// <summary>
    /// Beamed power received by UNLOADED vessels in Kerbalism's background step (todo/kspie-rails-warp.md, step A3).
    /// A receiver's routes are found by KSPIE's own InterstellarBeamedPowerHelper.GetConnectedTransmitters against the
    /// transmitters KSPIE's BeamedPowerSources knows (loaded, and unloaded ones read from their snapshots — whose beam
    /// the background transmitter consumers keep current), with the receiver's saved geometry and its orientation from
    /// the snapshot. What each unloaded receiver last received from a transmitter counts against it for the others
    /// (<see cref="Usage"/>; Kerbalism runs unloaded vessels one at a time, each at its own time), as loaded receivers
    /// count each other (GetEnumeratedPowerFromSatelliteForAllLoadedVessels, which is added too).
    /// A thermal mode receiver is a fuel-less reactor for the model (its generators attach to it by part); an electric
    /// mode one gives megajoules directly.
    /// </summary>
    public static class BackgroundBeams
    {
        public const string ReceiverMarker = "bgReceiver";
        public const string Modelled = "bgModelled";
        public const string ReceivesBeams = "bgReceivesBeams";
        public const string ThermalMode = "bgThermalMode";
        public const string CapacityMW = "bgCapacityMW";
        public const string BeamCapacityMW = "bgBeamCapacityMW";
        public const string ElectricFactor = "bgElectricFactor";
        public const string MinimumRatio = "bgMinimumRatio";
        public const string Diameter = "bgDiameter";
        public const string ApertureMultiplier = "bgApertureMultiplier";
        public const string ReceiverType = "bgReceiverType";
        public const string MinimumWavelength = "bgMinimumWavelength";
        public const string MaximumWavelength = "bgMaximumWavelength";
        public const string HighSpeedAtmosphereFactor = "bgHighSpeedAtmosphereFactor";
        public const string FacingThreshold = "bgFacingThreshold";
        public const string FacingSurfaceExponent = "bgFacingSurfaceExponent";
        public const string FacingEfficiencyExponent = "bgFacingEfficiencyExponent";
        public const string SpotsizeNormalizationExponent = "bgSpotsizeNormalizationExponent";
        public const string CanBeActiveInAtmosphere = "bgCanBeActiveInAtmosphere";
        /// <summary>"min:max:electric%:thermal%;…"; a single entry when the band is switched in the editor (used for every wavelength).</summary>
        public const string Bands = "bgBands";
        public const string SingleBand = "bgSingleBand";

        public static string WriteBands(IEnumerable<double[]> bands)
        {
            var sb = new StringBuilder();
            foreach (var band in bands)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append(string.Join(":", band.Select(BackgroundSnapshot.Number).ToArray()));
            }
            return sb.ToString();
        }

        /// <summary>A modelled receiver of an unloaded vessel, read from its snapshot.</summary>
        public sealed class Receiver : IBeamedPowerReceiver, IBeamedPowerReceiverOrientation
        {
            public string Name;
            public ProtoPartSnapshot PartSnapshot;
            /// <summary>Index of its module in the part (a part may carry two receiver modules).</summary>
            public int ModuleIndex;
            public bool Thermal;
            public bool SingleBand;
            public double Capacity, BeamCapacity, ElectricFactorValue, Minimum, HotBath;
            public readonly List<BeamBand> BandList = new List<BeamBand>();
            /// <summary>Index of its pseudo-reactor in the vessel spec (thermal mode), else −1.</summary>
            public int ReactorIndex = -1;

            // IBeamedPowerReceiver, for KSPIE's routing
            public int ReceiverType { get; set; }
            public double Diameter { get; set; }
            public double ApertureMultiplier { get; set; }
            public double MinimumWavelength { get; set; }
            public double MaximumWavelength { get; set; }
            public double HighSpeedAtmosphereFactor { get; set; }
            public double FacingThreshold { get; set; }
            public double FacingSurfaceExponent { get; set; }
            public double FacingEfficiencyExponent { get; set; }
            public double SpotsizeNormalizationExponent { get; set; }
            public bool CanBeActiveInAtmosphere { get; set; }
            public Vessel Vessel { get; set; }
            public Part Part => PartSnapshot?.partInfo?.partPrefab;

            // orientation: the vessel's world rotation (KSP keeps it for unloaded vessels) × the part's rotation in the vessel
            private Quaternion Rotation => (Vessel != null ? Vessel.transform.rotation : Quaternion.identity) * (PartSnapshot != null ? PartSnapshot.rotation : Quaternion.identity);
            public Vector3d Up => Rotation * Vector3.up;
            public Vector3d Forward => Rotation * Vector3.forward;
            public Vector3d Right => Rotation * Vector3.right;
        }

        private static bool Finite(string text, out double value) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value) && !double.IsInfinity(value);

        /// <summary>Reads a receiver node; null when it is not in beamed power mode or its values are unreadable (problems gets why).</summary>
        public static Receiver Read(Vessel vessel, ProtoPartSnapshot part, ConfigNode node, string where, List<string> problems)
        {
            if (!BackgroundConsumers.GetBool(node, ReceivesBeams)) return null;
            if (!BackgroundConsumers.TryGet(node, CapacityMW, out var capacity) || !BackgroundConsumers.TryGet(node, BeamCapacityMW, out var beamCapacity)
                || !BackgroundConsumers.TryGet(node, Diameter, out var diameter))
            {
                problems.Add(where + ": missing or unreadable receiver values");
                return null;
            }
            double Get(string key, double fallback) => BackgroundConsumers.TryGet(node, key, out var value) ? value : fallback;
            var receiver = new Receiver
            {
                Name = where,
                PartSnapshot = part,
                ModuleIndex = part.modules.FindIndex(module => module.moduleValues == node),
                Vessel = vessel,
                Thermal = BackgroundConsumers.GetBool(node, ThermalMode),
                SingleBand = BackgroundConsumers.GetBool(node, SingleBand),
                Capacity = Math.Max(0, capacity),
                BeamCapacity = Math.Max(0, beamCapacity),
                ElectricFactorValue = Math.Max(0, Math.Min(1, Get(ElectricFactor, 1))),
                Minimum = Math.Max(0, Math.Min(1, Get(MinimumRatio, 0))),
                HotBath = Get(BackgroundSnapshot.HotBathK, 3000),
                ReceiverType = (int)Get(ReceiverType, 0),
                Diameter = diameter,
                ApertureMultiplier = Get(ApertureMultiplier, 1),
                MinimumWavelength = Get(MinimumWavelength, 0),
                MaximumWavelength = Get(MaximumWavelength, 1),
                HighSpeedAtmosphereFactor = Get(HighSpeedAtmosphereFactor, 0),
                FacingThreshold = Get(FacingThreshold, 0),
                FacingSurfaceExponent = Get(FacingSurfaceExponent, 1),
                FacingEfficiencyExponent = Get(FacingEfficiencyExponent, 0.1),
                SpotsizeNormalizationExponent = Get(SpotsizeNormalizationExponent, 1),
                CanBeActiveInAtmosphere = BackgroundConsumers.GetBool(node, CanBeActiveInAtmosphere)
            };
            foreach (var entry in (node.GetValue(Bands) ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var values = entry.Split(':');
                if (values.Length == 4 && Finite(values[0], out var min) && Finite(values[1], out var max)
                    && Finite(values[2], out var electric) && Finite(values[3], out var thermal))
                    receiver.BandList.Add(new BeamBand { MinimumWavelength = min, MaximumWavelength = max, ElectricEfficiency = electric / 100, ThermalEfficiency = thermal / 100 });
                else
                    problems.Add(where + ": unreadable band '" + entry + "'");
            }
            return receiver;
        }

        // ---------------------------------------------------------------- what transmitters give to unloaded receivers

        /// <summary>A receiver: its vessel, part and module.</summary>
        private static KeyValuePair<Guid, long> Key(Receiver receiver) =>
            new KeyValuePair<Guid, long>(receiver.Vessel.id,
                ((long)(receiver.PartSnapshot != null ? receiver.PartSnapshot.flightID : 0) << 16) | (long)(receiver.ModuleIndex & 0xFFFF));

        /// <summary>
        /// The last intake of every receiver computed with KSPIE's transmitter lists ready, from a step that succeeded:
        /// it stands in on the first frames of a scene, before KSPIE has filled them (Kerbalism's first steps after a
        /// scene load, which may cover the time spent elsewhere, come before that). Kept across scene loads (a vessel's
        /// run, and its Receiver, are new then; entering flight loads the game anew, so this is not keyed by the Game
        /// object); emptied when a save of another folder is loaded.
        /// </summary>
        private static readonly Dictionary<KeyValuePair<Guid, long>, Intake> LastIntakes = new Dictionary<KeyValuePair<Guid, long>, Intake>();

        /// <summary>What each unloaded receiver took from each transmitter at its last step (Core/StepPlan BeamUsage).</summary>
        private static readonly BeamUsage<KeyValuePair<Guid, long>> Usage = new BeamUsage<KeyValuePair<Guid, long>>();

        private static readonly HashSet<KeyValuePair<Guid, long>> FailureLogged = new HashSet<KeyValuePair<Guid, long>>();

        /// <summary>A receiver of another vessel counts against a transmitter while that vessel exists and is unloaded (loaded ones KSPIE counts itself).</summary>
        private static bool CountsAsUnloaded(KeyValuePair<Guid, long> receiver)
        {
            var vessel = FlightGlobals.FindVessel(receiver.Key);
            return vessel != null && !vessel.loaded;
        }

        /// <summary>What a receiver can take this step.</summary>
        public sealed class Intake
        {
            public Receiver Receiver;
            /// <summary>Sum over transmitters of the power after the receiver's band efficiency (KSPIE total_beamed_power), MW.</summary>
            public double ReceivedMW;
            /// <summary>Conversion heat at full use, MW.</summary>
            public double ConversionHeatMW;
            /// <summary>Received power per transmitter vessel, MW (what counts against it for the other receivers).</summary>
            public readonly List<KeyValuePair<Guid, double>> ReceivedByTransmitter = new List<KeyValuePair<Guid, double>>();
            /// <summary>Computed now from KSPIE's lists (not the last intake standing in): <see cref="Commit"/> records it.</summary>
            public bool Fresh;

            /// <summary>Thermal MW (thermal mode) or electric MW (electric mode) the receiver can give the vessel.</summary>
            public double AvailableMW => Receiver.Thermal
                ? Math.Min(Receiver.Capacity, ReceivedMW)
                : Math.Min(Receiver.Capacity, ReceivedMW * Receiver.ElectricFactorValue);

            public Intake CopyFor(Receiver receiver)
            {
                var copy = new Intake { Receiver = receiver, ReceivedMW = ReceivedMW, ConversionHeatMW = ConversionHeatMW };
                copy.ReceivedByTransmitter.AddRange(ReceivedByTransmitter);
                return copy;
            }
        }

        private static string _saveFolder;

        /// <summary>
        /// Called at every background step: lets KSPIE read unloaded vessels in any scene (it set its snapshot readers
        /// only in flight, GameEventSubscriber), and forgets the intakes of another game when a save of another folder
        /// is loaded (within one save the entries are replaced at every step and filtered by <see cref="CountsAsUnloaded"/>).
        /// </summary>
        public static void Prepare()
        {
            if (BeamedPowerSources.getVesselMicrowavePersistanceForProtoVesselCallback == null)
                BeamedPowerSources.getVesselMicrowavePersistanceForProtoVesselCallback = BeamedPowerTransmitter.GetVesselMicrowavePersistenceForProtoVessel;
            if (BeamedPowerSources.getVesselRelayPersistanceForProtoVesselCallback == null)
                BeamedPowerSources.getVesselRelayPersistanceForProtoVesselCallback = BeamedPowerTransmitter.GetVesselRelayPersistenceForProtoVessel;
            if (BeamedPowerSources.getVesselMicrowavePersistanceForVesselCallback == null)
                BeamedPowerSources.getVesselMicrowavePersistanceForVesselCallback = BeamedPowerTransmitter.GetVesselMicrowavePersistenceForVessel;
            if (BeamedPowerSources.getVesselRelayPersistenceForVesselCallback == null)
                BeamedPowerSources.getVesselRelayPersistenceForVesselCallback = BeamedPowerTransmitter.GetVesselRelayPersistenceForVessel;

            if (_saveFolder == HighLogic.SaveFolder) return;
            _saveFolder = HighLogic.SaveFolder;
            LastIntakes.Clear();
            Usage.ForgetWhere(receiver => true);
            FailureLogged.Clear();
        }

        /// <summary>
        /// What the receiver can take now. Never throws: when the routes cannot be computed (KSPIE's lists not ready on
        /// the first frames of a scene, an error) the last intake stands in, or nothing; an error is logged once per receiver.
        /// KSPIE's lists are refreshed in the space center and tracking station too (fork, BeamedPowerSources).
        /// Records nothing: what it takes counts against the transmitters once the step succeeded (<see cref="Commit"/>).
        /// </summary>
        public static Intake Receive(Receiver receiver)
        {
            var empty = new Intake { Receiver = receiver };
            if (receiver.BandList.Count == 0 || receiver.Vessel == null)
                return empty;
            var key = Key(receiver);
            if (BeamedPowerSources.instance == null || !BeamedPowerSources.instance.Initialized)
                return LastIntakes.TryGetValue(key, out var stored) ? stored.CopyFor(receiver) : empty;

            try
            {
                var intake = new Intake { Receiver = receiver };
                foreach (var connected in InterstellarBeamedPowerHelper.GetConnectedTransmitters(receiver))
                {
                    var transmitter = connected.Key;
                    var route = connected.Value.Key;
                    var relays = connected.Value.Value;
                    if (transmitter?.Vessel == null || route == null) continue;

                    var transmitPower = transmitter.getAvailablePowerInMW();
                    var relayCapacity = relays != null && relays.Count > 0 ? relays.Min(m => m.PowerCapacity) : double.PositiveInfinity;
                    var networkCapacity = Math.Min(transmitPower, relayCapacity);
                    var usedByOthers = Usage.UsedByOthers(transmitter.Vessel.id, key, CountsAsUnloaded)
                        + BeamedPowerReceiver.GetEnumeratedPowerFromSatelliteForAllLoadedVessels(transmitter, null);
                    var remaining = Math.Max(0, networkCapacity - usedByOthers);

                    var beams = transmitter.SupportedTransmitWavelengths
                        .Select(b => new IncomingBeam { Wavelength = b.wavelength, PowerMW = (b.nuclearPower + b.solarPower) * 0.001 })
                        .ToList();
                    var fromTransmitter = BeamReception.FromTransmitter(beams, route.Efficiency, relayCapacity, remaining,
                        receiver.BandList, receiver.SingleBand, receiver.Thermal, receiver.BeamCapacity);
                    if (!(fromTransmitter.ReceivedMW > 0)) continue;

                    intake.ReceivedMW += fromTransmitter.ReceivedMW;
                    intake.ConversionHeatMW += fromTransmitter.ConversionHeatMW;
                    intake.ReceivedByTransmitter.Add(new KeyValuePair<Guid, double>(transmitter.Vessel.id, fromTransmitter.ReceivedMW));
                }
                intake.Fresh = true;
                FailureLogged.Remove(key);
                return intake;
            }
            catch (Exception e)
            {
                if (FailureLogged.Add(key))
                    Debug.LogError("[KSPI]: background beamed power of " + receiver.Vessel.vesselName + " / " + receiver.Name
                        + " failed, last intake used: " + e);
                return LastIntakes.TryGetValue(key, out var stored) ? stored.CopyFor(receiver) : empty;
            }
        }

        /// <summary>
        /// The step succeeded: a fresh intake counts against its transmitters for the other unloaded receivers and stands
        /// in later while KSPIE's lists are not ready.
        /// </summary>
        public static void Commit(Intake intake)
        {
            if (intake == null || !intake.Fresh || intake.Receiver?.Vessel == null) return;
            var key = Key(intake.Receiver);
            Usage.Set(key, intake.ReceivedByTransmitter);
            LastIntakes[key] = intake;
        }

        /// <summary>Forgets the intake of receivers whose vessel no longer exists (recovered, destroyed) or is loaded now.</summary>
        public static void ForgetGone()
        {
            Usage.ForgetWhere(receiver => !CountsAsUnloaded(receiver));
        }
    }
}
