using System;
using System.Linq;
using FNPlugin.Constants;
using FNPlugin.Extensions;
using FNPlugin.Resources;
using KSP.Localization;
using UnityEngine;

namespace FNPlugin.Refinery.Activity
{
    /// <summary>
    /// Methane pyrolysis: CH4 => C + 2 H2, heat only, no oxygen.
    /// Fork: upstream was a copy of PartialMethaneOxidation (took oxygen, made carbon monoxide, lost a quarter of the mass;
    /// upstream issue #724). Solid carbon goes to the CRP resource "Carbon" if the vessel stores it; like every product of
    /// the refinery it needs room unless overflow is allowed.
    /// </summary>
    [KSPModule("ISRU Methane Pyrolysis")]
    class MethanePyrolysis : RefineryActivity
    {
        public MethanePyrolysis()
        {
            ActivityName = "Methane Pyrolysis";
            Formula = "CH<size=7>4</size> => C + 2H<size=7>2</size>";
            PowerRequirements = PluginSettings.Config.BaseELCPowerConsumption;
            EnergyPerTon = PluginSettings.Config.ElectrolysisEnergyPerTon;
        }

        private const string CarbonResourceName = "Carbon";

        // molar masses: CH4 16.043, C 12.011, H2 2.016
        private const double CarbonMassByFraction = 12.011 / 16.043;
        private const double HydrogenMassByFraction = 2 * 2.016 / 16.043;

        private double _carbonDensity;
        private double _methaneDensity;
        private double _hydrogenDensity;

        private double _methaneConsumptionRate;
        private double _hydrogenProductionRate;
        private double _carbonProductionRate;

        private string _carbonResourceName;
        private string _methaneResourceName;
        private string _hydrogenResourceName;

        private double _maxCapacityCarbonMass;
        private double _maxCapacityHydrogenMass;
        private double _maxCapacityMethaneMass;

        private double _availableMethaneMass;
        private double _spareRoomHydrogenMass;
        private double _spareRoomCarbonMass;

        public override string Status => string.Copy(_status);
        public override RefineryType RefineryType => RefineryType.Heating;

        public override bool HasActivityRequirements()
        {
            if (_carbonResourceName == null || _hydrogenResourceName == null || _methaneResourceName == null)
                return false;

            return _part.GetConnectedResources(_methaneResourceName).Any(rs => rs.amount > 0);
        }

        public override void Initialize(Part localPart, InterstellarRefineryController controller)
        {
            base.Initialize(localPart, controller);

            _hydrogenResourceName = ResourceSettings.Config.HydrogenGas;
            _methaneResourceName = ResourceSettings.Config.MethaneGas;

            _hydrogenDensity = PartResourceLibrary.Instance.GetDefinition(_hydrogenResourceName).density;
            _methaneDensity = PartResourceLibrary.Instance.GetDefinition(_methaneResourceName).density;

            var carbon = PartResourceLibrary.Instance.GetDefinition(CarbonResourceName);
            if (carbon == null)
            {
                Debug.LogWarning("[KSPI]: MethanePyrolysis: no resource " + CarbonResourceName + " (Community Resource Pack), pyrolysis disabled");
                return;
            }
            _carbonResourceName = carbon.name;
            _carbonDensity = carbon.density;
        }

        public override void UpdateFrame(double rateMultiplier, double powerFraction, double productionModifier, bool allowOverflow, double fixedDeltaTime, bool isStartup = false)
        {
            _current_power = PowerRequirements * rateMultiplier;
            _current_rate = CurrentPower / EnergyPerTon;

            if (_carbonResourceName == null)
            {
                _methaneConsumptionRate = _hydrogenProductionRate = _carbonProductionRate = 0;
                UpdateStatusMessage();
                return;
            }

            // only unlocked tanks: RequestResource below skips locked ones, so free room counted in a locked tank would
            // let methane be consumed with the product lost
            var partsThatContainCarbon = _part.GetConnectedResources(_carbonResourceName).Where(r => r.flowState).ToList();
            var partsThatContainHydrogen = _part.GetConnectedResources(_hydrogenResourceName).Where(r => r.flowState).ToList();
            var partsThatContainMethane = _part.GetConnectedResources(_methaneResourceName).Where(r => r.flowState).ToList();

            _maxCapacityCarbonMass = partsThatContainCarbon.Sum(p => p.maxAmount) * _carbonDensity;
            _maxCapacityHydrogenMass = partsThatContainHydrogen.Sum(p => p.maxAmount) * _hydrogenDensity;
            _maxCapacityMethaneMass = partsThatContainMethane.Sum(p => p.maxAmount) * _methaneDensity;

            _availableMethaneMass = partsThatContainMethane.Sum(r => r.amount) * _methaneDensity;

            _spareRoomCarbonMass = partsThatContainCarbon.Sum(r => r.maxAmount - r.amount) * _carbonDensity;
            _spareRoomHydrogenMass = partsThatContainHydrogen.Sum(r => r.maxAmount - r.amount) * _hydrogenDensity;

            // methane (tons) this step may decompose: limited by power and by the methane on board
            var fixedMethaneMass = Math.Min(_current_rate * fixedDeltaTime, _availableMethaneMass);

            if (fixedMethaneMass > 0 && fixedDeltaTime > 0)
            {
                var fixedMaxCarbonMass = fixedMethaneMass * CarbonMassByFraction;
                var fixedMaxHydrogenMass = fixedMethaneMass * HydrogenMassByFraction;

                var fixedPossibleCarbonMass = allowOverflow ? fixedMaxCarbonMass : Math.Min(_spareRoomCarbonMass, fixedMaxCarbonMass);
                var fixedPossibleHydrogenMass = allowOverflow ? fixedMaxHydrogenMass : Math.Min(_spareRoomHydrogenMass, fixedMaxHydrogenMass);

                var storageRatio = Math.Min(fixedPossibleCarbonMass / fixedMaxCarbonMass, fixedPossibleHydrogenMass / fixedMaxHydrogenMass);

                var consumedMethaneMass = _part.RequestResource(_methaneResourceName, storageRatio * fixedMethaneMass / _methaneDensity, ResourceFlowMode.ALL_VESSEL) * _methaneDensity;
                _methaneConsumptionRate = consumedMethaneMass / fixedDeltaTime;

                _carbonProductionRate = -_part.RequestResource(_carbonResourceName, -consumedMethaneMass * CarbonMassByFraction / _carbonDensity) / fixedDeltaTime * _carbonDensity;
                _hydrogenProductionRate = -_part.RequestResource(_hydrogenResourceName, -consumedMethaneMass * HydrogenMassByFraction / _hydrogenDensity) / fixedDeltaTime * _hydrogenDensity;
            }
            else
            {
                _methaneConsumptionRate = 0;
                _hydrogenProductionRate = 0;
                _carbonProductionRate = 0;
            }
            UpdateStatusMessage();
        }

        public override void UpdateGUI()
        {
            base.UpdateGUI();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Localizer.Format("#LOC_KSPIE_MethanePyrolysis_Power"), _bold_label, GUILayout.Width(labelWidth));//"Power"
            GUILayout.Label(PluginHelper.GetFormattedPowerString(CurrentPower) + "/" + PluginHelper.GetFormattedPowerString(PowerRequirements), _value_label, GUILayout.Width(valueWidth));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Localizer.Format("#LOC_KSPIE_MethanePyrolysis_MethaneAvailable"), _bold_label, GUILayout.Width(labelWidth));//"Methane Available"
            GUILayout.Label(_availableMethaneMass.ToString("0.0000") + " mT / " + _maxCapacityMethaneMass.ToString("0.0000") + " mT", _value_label, GUILayout.Width(valueWidth));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Localizer.Format("#LOC_KSPIE_MethanePyrolysis_ConsumptionRate"), _bold_label, GUILayout.Width(labelWidth));//"Methane Consumption Rate"
            GUILayout.Label((_methaneConsumptionRate * GameConstants.SECONDS_IN_HOUR).ToString("0.000") + " mT/hour", _value_label, GUILayout.Width(valueWidth));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Localizer.Format("#LOC_KSPIE_MethanePyrolysis_HydrogenStorage"), _bold_label, GUILayout.Width(labelWidth));//"Hydrogen Storage"
            GUILayout.Label(_spareRoomHydrogenMass.ToString("0.0000") + " mT / " + _maxCapacityHydrogenMass.ToString("0.0000") + " mT", _value_label, GUILayout.Width(valueWidth));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Localizer.Format("#LOC_KSPIE_MethanePyrolysis_HydrogenProductionRate"), _bold_label, GUILayout.Width(labelWidth));//"Hydrogen Production Rate"
            GUILayout.Label((_hydrogenProductionRate * GameConstants.SECONDS_IN_HOUR).ToString("0.000") + " mT/hour", _value_label, GUILayout.Width(valueWidth));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Localizer.Format("#LOC_KSPIE_MethanePyrolysis_CarbonStorage"), _bold_label, GUILayout.Width(labelWidth));//"Carbon Storage"
            GUILayout.Label(_spareRoomCarbonMass.ToString("0.0000") + " mT / " + _maxCapacityCarbonMass.ToString("0.0000") + " mT", _value_label, GUILayout.Width(valueWidth));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Localizer.Format("#LOC_KSPIE_MethanePyrolysis_CarbonProductionRate"), _bold_label, GUILayout.Width(labelWidth));//"Carbon Production Rate"
            GUILayout.Label((_carbonProductionRate * GameConstants.SECONDS_IN_HOUR).ToString("0.000") + " mT/hour", _value_label, GUILayout.Width(valueWidth));
            GUILayout.EndHorizontal();
        }

        private void UpdateStatusMessage()
        {
            if (_hydrogenProductionRate > 0)
                _status = Localizer.Format("#LOC_KSPIE_MethanePyrolysis_Statumsg1");//"Methane Pyrolysis Ongoing"
            else if (CurrentPower <= 0.01*PowerRequirements)
                _status = Localizer.Format("#LOC_KSPIE_MethanePyrolysis_Statumsg2");//"Insufficient Power"
            else
                _status = Localizer.Format("#LOC_KSPIE_MethanePyrolysis_Statumsg3");//"Insufficient Storage"
        }

        public override void PrintMissingResources()
        {
            if (!_part.GetConnectedResources(_methaneResourceName).Any(rs => rs.amount > 0))
                ScreenMessages.PostScreenMessage(Localizer.Format("#LOC_KSPIE_MethanePyrolysis_PostMsg") +" " + _methaneResourceName, 3.0f, ScreenMessageStyle.UPPER_CENTER);//Missing
        }
    }
}
