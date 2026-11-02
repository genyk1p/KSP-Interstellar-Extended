using System;
using System.Linq;
using KSP.Localization;
using UnityEngine;

namespace FNPlugin.Beamedpower
{
    /// <summary>
    /// Wear of the beam emitter (fork addition). Real emitters lose output while they run: high power diode laser bars
    /// lose ~20 % of their output in 10 000-20 000 h of operation, and every 20 K hotter cuts their life roughly tenfold.
    /// Here the conversion efficiency drops with the hours run at full power (an hour at 30 % power counts as 0.3 h),
    /// and the vessel's waste heat makes the hours count faster. An engineer on EVA replaces the emitters.
    ///
    /// Off unless ratedLifetimeHours > 0 (set per generator type by Patches/BeamGeneratorWear.cfg).
    /// Random failures are not modelled here: with Kerbalism, a Reliability module of type BeamGeneratorModule /
    /// IntegratedBeamGenerator disables this module when it breaks, and the transmitter then stops transmitting.
    /// </summary>
    partial class BeamGenerator
    {
        /// <summary>Hours at full power after which the efficiency has dropped by endOfLifeLoss. 0 = no wear.</summary>
        [KSPField] public double ratedLifetimeHours = 0;
        /// <summary>Fraction of the efficiency lost at ratedLifetimeHours (end of life of a laser diode: 20 %).</summary>
        [KSPField] public double endOfLifeLoss = 0.2;
        /// <summary>Wear keeps going past the rated life, up to this fraction of the efficiency lost.</summary>
        [KSPField] public double maxWearLoss = 0.9;
        /// <summary>Wear runs 2^(heatWearDoublings * waste heat fill) times faster: x8 with a full waste heat bar.</summary>
        [KSPField] public double heatWearDoublings = 3;
        /// <summary>Life multiplier when Kerbalism's "high quality" is on for this module.</summary>
        [KSPField] public double qualityLifeMultiplier = 2;
        /// <summary>Kerbal trait that can replace the emitters on EVA.</summary>
        [KSPField] public string repairTrait = "Engineer";

        [KSPField(isPersistant = true)] public double wearHours;
        /// <summary>Full-power hours per hour at the last update: used to add the wear while the vessel was unloaded.</summary>
        [KSPField(isPersistant = true)] public double wearRate;
        [KSPField(isPersistant = true)] public double lastWearTime = -1;

        [KSPField(groupName = GROUP, groupDisplayName = GROUP_TITLE, guiActive = true, guiActiveEditor = true, guiName = "#LOC_KSPIE_BeamGenerator_Wear")]
        public string wearStatus = "";

        private double _baseEfficiencyPercentage = -1;
        private BaseField _qualityField;
        private bool _qualitySearched;
        private float _nextWearStatusUpdate;

        public bool WearEnabled => ratedLifetimeHours > 0;

        public double EffectiveLifetimeHours => ratedLifetimeHours * (HasQuality() ? qualityLifeMultiplier : 1);

        /// <summary>Fraction of the efficiency lost to wear.</summary>
        public double WearLoss => WearEnabled ? Math.Min(maxWearLoss, endOfLifeLoss * wearHours / EffectiveLifetimeHours) : 0;

        /// <summary>Kerbalism's Reliability for this module, if any, and its "quality" toggle.</summary>
        private bool HasQuality()
        {
            if (!_qualitySearched)
            {
                _qualitySearched = true;
                PartModule reliability = part.Modules.Cast<PartModule>().FirstOrDefault(m =>
                    m.moduleName == "Reliability" && m.Fields["type"] != null && (m.Fields["type"].GetValue(m) as string) == moduleName);
                _qualityField = reliability?.Fields["quality"];
                if (_qualityField != null)
                    _qualityHost = reliability;
            }
            return _qualityField != null && _qualityField.GetValue(_qualityHost) is bool q && q;
        }

        private PartModule _qualityHost;

        /// <summary>Called when the configuration sets the efficiency: keeps the unworn value and applies the wear.</summary>
        private void SetBaseEfficiency(double percentage)
        {
            _baseEfficiencyPercentage = percentage;
            ApplyWear(true);
        }

        private void ApplyWear(bool forceStatus)
        {
            if (_baseEfficiencyPercentage >= 0)
                efficiencyPercentage = _baseEfficiencyPercentage * (1 - WearLoss);
            if (forceStatus || Time.time >= _nextWearStatusUpdate)
            {
                _nextWearStatusUpdate = Time.time + 1f;
                UpdateWearStatus();
            }
        }

        private void UpdateWearStatus()
        {
            Fields[nameof(wearStatus)].guiActive = WearEnabled;
            Fields[nameof(wearStatus)].guiActiveEditor = WearEnabled;
            if (!WearEnabled)
                return;
            if (HighLogic.LoadedSceneIsEditor || wearHours <= 0)
                wearStatus = Localizer.Format("#LOC_KSPIE_BeamGenerator_WearLife", EffectiveLifetimeHours.ToString("N0"), (endOfLifeLoss * 100).ToString("0"));
            else
                wearStatus = Localizer.Format("#LOC_KSPIE_BeamGenerator_WearState", (WearLoss * 100).ToString("0.0"), wearHours.ToString("N0"), EffectiveLifetimeHours.ToString("N0"));
        }

        /// <summary>
        /// Called by the transmitter every physics frame in flight.
        /// powerFraction: transmitted power / capacity (0 when not transmitting); wasteheatRatio: waste heat bar fill.
        /// </summary>
        public void AddWear(double deltaTime, double powerFraction, double wasteheatRatio)
        {
            if (!WearEnabled)
                return;
            double rate = Clamp01(powerFraction) * Math.Pow(2, heatWearDoublings * Clamp01(wasteheatRatio));
            if (double.IsNaN(rate) || double.IsInfinity(rate))
                rate = 0;
            wearHours += deltaTime / 3600 * rate;
            wearRate = rate;
            lastWearTime = Planetarium.GetUniversalTime();
            ApplyWear(false);
        }

        /// <summary>Wear for the time the vessel was not loaded, at the rate it had when it was saved.</summary>
        private void CatchUpWear()
        {
            if (!WearEnabled || !HighLogic.LoadedSceneIsFlight)
                return;
            double now = Planetarium.GetUniversalTime();
            if (lastWearTime > 0 && now > lastWearTime && wearRate > 0)
                wearHours += (now - lastWearTime) / 3600 * wearRate;
            lastWearTime = now;
        }

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        [KSPEvent(guiActive = false, guiActiveUnfocused = true, externalToEVAOnly = true, unfocusedRange = 4f, guiName = "#LOC_KSPIE_BeamGenerator_ReplaceEmitters")]
        public void ReplaceEmitters()
        {
            ProtoCrewMember kerbal = FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.isEVA
                ? FlightGlobals.ActiveVessel.GetVesselCrew().FirstOrDefault()
                : null;
            // trait is the config name ("Engineer"); experienceTrait.TypeName / Title are localized ("Инженер")
            if (kerbal == null || kerbal.trait != repairTrait)
            {
                ScreenMessages.PostScreenMessage(Localizer.Format("#LOC_KSPIE_BeamGenerator_NeedEngineer"), 5f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }
            double lostBefore = WearLoss;
            wearHours = 0;
            ApplyWear(true);
            ScreenMessages.PostScreenMessage(Localizer.Format("#LOC_KSPIE_BeamGenerator_EmittersReplaced", kerbal.name, (lostBefore * 100).ToString("0.0")),
                5f, ScreenMessageStyle.UPPER_CENTER);
        }

        private void UpdateWearEvents()
        {
            BaseEvent replace = Events[nameof(ReplaceEmitters)];
            if (replace != null)
                replace.guiActiveUnfocused = WearEnabled && wearHours > 0;
        }
    }
}
