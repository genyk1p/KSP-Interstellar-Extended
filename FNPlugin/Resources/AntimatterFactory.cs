using System;
using System.Collections.Generic;
using FNPlugin.Constants;
using FNPlugin.Powermanagement;
using FNPlugin.Refinery;
using KSP.Localization;

namespace FNPlugin.Resources
{
    class AntimatterFactory : ResourceSuppliableModule
    {
        public const string Group = "Antimatter Factory";
        public const string GroupTitle = "Antimatter Factory";

        [KSPField(isPersistant = true)] public double lastActiveTime;
        [KSPField(isPersistant = true)] public double electricalPowerRatio;

        [KSPField(advancedTweakable = true, guiActive = false)] public double _availablePower;
        [KSPField(advancedTweakable = true, guiActive = false)] public double _resourceBarRatio;
        [KSPField(advancedTweakable = true, guiActive = false)] public double _effectiveResourceThrottling;

        [KSPField] public double productionRate;
        [KSPField] public double efficiencyMultiplier = 10;
        [KSPField] public string activateTitle = "#LOC_KSPIE_AntimatterFactory_producePositron";
        [KSPField] public string resourceName = "Positrons";

        [KSPField(groupName = Group, groupDisplayName = GroupTitle, guiActive = true, isPersistant = true), UI_Toggle(disabledText = "#LOC_KSPIE_AntimatterFactory_Off", enabledText = "#LOC_KSPIE_AntimatterFactory_On")]//OffOn
        public bool isActive = false;
        [KSPField(groupName = Group, groupDisplayName = GroupTitle, guiActive = true, isPersistant = true, guiName = "#LOC_KSPIE_AntimatterFactory_powerPecentage"), UI_FloatRange(stepIncrement = 0.5f, maxValue = 100, minValue = 1)]
        public float powerPercentage = 100;

        [KSPField(groupName = Group, groupDisplayName = GroupTitle, guiActive = true, guiName = "#LOC_KSPIE_AntimatterFactory_productionRate")]
        public string productionRateTxt;
        [KSPField(groupName = Group, groupDisplayName = GroupTitle, guiActive = true, guiActiveEditor = true, guiName = "#LOC_KSPIE_AntimatterFactory_MaximumPowercapacity", guiUnits = "#LOC_KSPIE_Reactor_megawattUnit")]//Maximum Power capacity
        public double powerCapacity = 1000;

        private AntimatterGenerator _generator;
        private PartResourceDefinition _antimatterDefinition;
        private string _disabledText;

        public override void OnStart(StartState state)
        {
            _antimatterDefinition = PartResourceLibrary.Instance.GetDefinition(resourceName);
            _generator = new AntimatterGenerator(part, efficiencyMultiplier, _antimatterDefinition);

            productionRate = _generator.ProductionRate;

            if (state == StartState.Editor)
                return;

            _disabledText = Localizer.Format("#LOC_KSPIE_AntimatterFactory_disabled");

            Fields[nameof(isActive)].guiName = Localizer.Format(activateTitle);

            if (!isActive)
                return;

            var deltaTime = Planetarium.GetUniversalTime() - lastActiveTime;

            _generator.Produce(electricalPowerRatio * powerCapacity * deltaTime);
        }

        public override void OnUpdate()
        {
            if (!isActive)
            {
                productionRateTxt = _disabledText;
                return;
            }

            lastActiveTime = Planetarium.GetUniversalTime();

            double antimatterRatePerDay = productionRate * PluginSettings.Config.SecondsInDay;

            if (antimatterRatePerDay > 0.1)
            {
                if (antimatterRatePerDay > 1000)
                    productionRateTxt = (antimatterRatePerDay / 1e3).ToString("0.0000") + " g/" + Localizer.Format("#LOC_KSPIE_AntimatterFactory_perday");//day
                else
                    productionRateTxt = (antimatterRatePerDay).ToString("0.0000") + " mg/" + Localizer.Format("#LOC_KSPIE_AntimatterFactory_perday");//day
            }
            else
            {
                if (antimatterRatePerDay > 0.1e-3)
                    productionRateTxt = (antimatterRatePerDay * 1e3).ToString("0.0000") + " ug/" + Localizer.Format("#LOC_KSPIE_AntimatterFactory_perday");//day
                else
                    productionRateTxt = (antimatterRatePerDay * 1e6).ToString("0.0000") + " ng/" + Localizer.Format("#LOC_KSPIE_AntimatterFactory_perday");//day
            }
        }

        // background (Background/BackgroundConsumers, todo/kspie-rails-warp.md A2): values kept from load until the
        // factory has run in this flight
        private ConfigNode _loadedBackgroundValues;
        private bool _backgroundReady;
        private bool _backgroundErrorLogged;

        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);
            _loadedBackgroundValues = Background.BackgroundSnapshot.CopyBackgroundValues(node);
        }

        public override void OnSave(ConfigNode node)
        {
            base.OnSave(node);
            if (HighLogic.LoadedSceneIsFlight)
                Background.BackgroundSnapshot.Save(this, node, _backgroundReady, _loadedBackgroundValues, SaveBackgroundValues, ref _backgroundErrorLogged);
        }

        /// <summary>Power at the slider and what it makes per second (AntimatterGenerator.Produce).</summary>
        private void SaveBackgroundValues(ConfigNode node)
        {
            var demand = powerPercentage * 0.01 * powerCapacity;
            var outputs = new List<KeyValuePair<string, double>>();
            // ReferenceEquals: the generator is a PartModule made with new, which Unity's == reports as null
            if (!ReferenceEquals(_generator, null) && _antimatterDefinition != null && _antimatterDefinition.density > 0)
                outputs.Add(new KeyValuePair<string, double>(_antimatterDefinition.name,
                    demand * 1E6 / GameConstants.lightSpeedSquared / 2000 / _antimatterDefinition.density * _generator.Efficiency));
            // megajoules only, no ElectricCharge (FixedUpdate: ConsumeFnResourcePerSecond)
            // FixedUpdate asks for powerPercentage × min(powerCapacity, stable supply)
            Background.BackgroundConsumers.WriteRecipe(node, isActive && outputs.Count > 0, demand, getPowerPriority(), 0, 0, false,
                null, outputs, nameof(lastActiveTime), powerPercentage * 0.01);
        }

        public void FixedUpdate()
        {
            if (HighLogic.LoadedSceneIsFlight)
                _backgroundReady = true;

            if (!isActive)
                return;

            _availablePower = GetAvailableStableSupply(ResourceSettings.Config.ElectricPowerInMegawatt);
            _resourceBarRatio = GetResourceBarRatio(ResourceSettings.Config.ElectricPowerInMegawatt);
            _effectiveResourceThrottling = _resourceBarRatio > 1d/3d ? 1 : _resourceBarRatio * 3;

            var energyRequestedInMegajoulesPerSecond = powerPercentage * 0.01 * Math.Min(powerCapacity, _effectiveResourceThrottling * _availablePower);

            var energyProvidedInMegajoulesPerSecond = CheatOptions.InfiniteElectricity
                ? energyRequestedInMegajoulesPerSecond
                : ConsumeFnResourcePerSecond(energyRequestedInMegajoulesPerSecond, ResourceSettings.Config.ElectricPowerInMegawatt);

            electricalPowerRatio = energyRequestedInMegajoulesPerSecond > 0 ? energyProvidedInMegajoulesPerSecond / energyRequestedInMegajoulesPerSecond : 0;

            _generator.Produce(energyProvidedInMegajoulesPerSecond * (double)(decimal)Math.Round(TimeWarp.fixedDeltaTime, 7));

            productionRate = _generator.ProductionRate;
        }
    }
}
