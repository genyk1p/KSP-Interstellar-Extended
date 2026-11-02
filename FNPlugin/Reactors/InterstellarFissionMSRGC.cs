using System;
using System.Collections.Generic;
using System.Linq;
using FNPlugin.Extensions;
using FNPlugin.Resources;
using FNPlugin.Storage;
using FNPlugin.Wasteheat;
using KSP.Localization;
using UnityEngine;

namespace FNPlugin.Reactors
{
    [KSPModule("Nuclear Salt Water Reactor")]
    class InterstellarNSWR : InterstellarFissionMSRGC { }

    [KSPModule("Nuclear Thermal Reactor")]
    class InterstellarFissionNTR : InterstellarFissionMSRGC { }

    [KSPModule("Fission Reactor")]
    class InterstellarFissionReactor : InterstellarFissionMSRGC { }

    [KSPModule("Molten Salt Reactor")]
    class InterstellarMoltenSaltReactor : InterstellarFissionMSRGC { }

    [KSPModule("Fission Reactor")]
    class InterstellarFissionMSRGC : InterstellarReactor, IFNNuclearFuelReprocessable
    {
        [KSPField] public double actinidesModifer = 1;
        [KSPField] public double temperatureThrottleExponent = 0.5;
        [KSPField] public double minimumTemperature = 0;
        [KSPField] public double poisonsProcessingSpeedMult = 0.05;
        [KSPField] public bool canDumpActinides = false;
        [KSPField] public double reactorMainFuelMaxAmount;

        private BaseEvent _manualRestartEvent;
        private PartResourceDefinition _actinideDefinition;
        private PartResourceDefinition _protactiniumDefinition;
        private double reactorMainFuelDensityInTon;
        // fuel mass (t) of the core when the current fuel mode has no fuel capacity at all (reactorMainFuelMaxAmount × density)
        private double _fallbackFuelMassCapacity;

        private readonly List<FNResourceTransfer> _managedTransferableActinideStores = new List<FNResourceTransfer>();
        private readonly List<FNResourceTransfer> _managedTransferableProtactiniumStores = new List<FNResourceTransfer>();

        // Properties
        public override bool IsFuelNeutronRich => !CurrentFuelMode.Aneutronic;
        public override bool IsNuclear => true;

        public double WasteToReprocess => part.Resources.Contains(ResourceSettings.Config.Actinides) ? part.Resources[ResourceSettings.Config.Actinides].amount : 0;

        [KSPEvent(groupName = Group, groupDisplayName = GroupTitle, guiName = "#LOC_KSPIE_FissionMSRGC_Dump_Actinides", guiActiveEditor = false, guiActive = true)]
        public void DumpActinides()
        {
            var actinides = part.Resources.Get(_actinideDefinition.id);
            if (actinides == null)
            {
                Debug.LogError("[KSPI]: actinides not found on " + part.partInfo.title);
                return;
            }

            var uranium233 = part.Resources[ResourceSettings.Config.Uranium233];
            if (uranium233 == null)
            {
                Debug.LogError("[KSPI]: uranium-233 not found on " + part.partInfo.title);
                return;
            }

            actinides.amount = 0;
            uranium233.amount = Math.Max(0, uranium233.amount - actinides.maxAmount);

            var message = Localizer.Format("#LOC_Dumped_Actinides");
            ScreenMessages.PostScreenMessage(message, 20.0f, ScreenMessageStyle.UPPER_CENTER);
            Debug.Log("[KSPI]: " + message);
        }

        [KSPEvent(groupName = Group, groupDisplayName = GroupTitle, guiName = "#LOC_KSPIE_FissionMSRGC_SwapFuel", externalToEVAOnly = true, guiActiveUnfocused = true, guiActive = false, unfocusedRange = 3.5f)]//Swap Fuel
        public void SwapFuelMode()
        {
            var actinides = part.Resources[ResourceSettings.Config.Actinides];
            if (actinides == null) return;
            if (actinides.amount > 0.01)
            {
                PostSwapMessage(Localizer.Format("#LOC_KSPIE_FissionMSRGC_SwapFuel_Actinides"));
                return;
            }

            // move the current fuel out of the core into the vessel's tanks
            DefaultCurrentFuel();

            var fuelMassCapacity = CurrentFuelMassCapacity();
            var fuelWithoutRoom = FuelLeftWithoutRoom(fuelModes[NextFuelModeIndex()], fuelMassCapacity);
            if (fuelWithoutRoom != null)
            {
                PostSwapMessage(Localizer.Format("#LOC_KSPIE_FissionMSRGC_SwapFuel_NoRoom", fuelWithoutRoom.Definition?.displayName ?? fuelWithoutRoom.ResourceName));
                return;
            }

            DisableResources();
            SwitchFuelType();
            EnableResources(fuelMassCapacity);
            Refuel();
        }

        [KSPEvent(groupName = Group, groupDisplayName = GroupTitle, guiName = "#LOC_KSPIE_FissionMSRGC_SwapFuel", guiActiveEditor = true, guiActive = false)]//Swap Fuel
        public void EditorSwapFuel()
        {
            if (fuelModes.Count == 1)
                return;

            var fuelMassCapacity = CurrentFuelMassCapacity();
            DisableResources();
            SwitchFuelType();
            EnableResources(fuelMassCapacity);

            var modesAvailable = CheckFuelModes();
            // Hide Switch Mode button if there iss only one mode for the selected fuel type available
            Events[nameof(SwitchMode)].guiActiveEditor = Events[nameof(SwitchMode)].guiActive = Events[nameof(SwitchMode)].guiActiveUnfocused = modesAvailable > 1;
        }

        [KSPEvent(groupName = Group, groupDisplayName = GroupTitle, guiName = "#LOC_KSPIE_FissionMSRGC_SwitchMode", guiActiveEditor = true, guiActiveUnfocused = true, guiActive = true)]//Switch Mode
        public void SwitchMode()
        {
            var startFirstFuelType = CurrentFuelMode.Variants.First().ReactorFuels.First();

            // repeat until found same or different fuel-mode with same kind of primary fuel
            ReactorFuel currentFirstFuelType;
            do
            {
                fuel_mode++;
                if (fuel_mode >= fuelModes.Count)
                    fuel_mode = 0;

                fuelmode_index = fuel_mode;
                CurrentFuelMode = fuelModes[fuel_mode];
                currentFirstFuelType = CurrentFuelMode.Variants.First().ReactorFuels.First();
            }
            while (currentFirstFuelType.ResourceName != startFirstFuelType.ResourceName);

            fuelModeStr = CurrentFuelMode.ModeGUIName;

            int modesAvailable = CheckFuelModes();
            // Hide Switch Mode button if there is only one mode for the selected fuel type available
            Events[nameof(SwitchMode)].guiActiveEditor = Events[nameof(SwitchMode)].guiActive = Events[nameof(SwitchMode)].guiActiveUnfocused = modesAvailable > 1;
        }

        [KSPEvent(groupName = Group, groupDisplayName = GroupTitle, guiName = "#LOC_KSPIE_FissionMSRGC_ManualRestart", externalToEVAOnly = true, guiActiveUnfocused = true, unfocusedRange = 3.5f)]//Manual Restart
        public void ManualRestart()
        {
            // verify any of the fuel types has at least 50% availability inside the reactor
            if (CurrentFuelMode.Variants.Any(variant => variant.ReactorFuels.All(fuel => GetLocalResourceRatio(fuel) > 0.5)))
                IsEnabled = true;
        }

        [KSPEvent(groupName = Group, groupDisplayName = GroupTitle, guiName = "#LOC_KSPIE_FissionMSRGC_ManualShutdown", externalToEVAOnly = true, guiActiveUnfocused = true, unfocusedRange = 3.5f)]//Manual Shutdown
        public void ManualShutdown()
        {
            IsEnabled = false;
        }

        [KSPEvent(groupName = Group, groupDisplayName = GroupTitle, guiName = "#LOC_KSPIE_FissionMSRGC_Refuel", externalToEVAOnly = true, guiActiveUnfocused = true, unfocusedRange = 3.5f)]//Refuel
        public void Refuel()
        {
            foreach (ReactorFuel fuel in CurrentFuelMode.Variants.First().ReactorFuels)
            {
                // avoid exceptions, just in case
                if (!part.Resources.Contains(fuel.ResourceName) || !part.Resources.Contains(ResourceSettings.Config.Actinides)) return;

                var fuelReactor = part.Resources[fuel.ResourceName];
                var actinidesReactor = part.Resources[ResourceSettings.Config.Actinides];
                var fuelResources = part.vessel.parts.SelectMany(p => p.Resources.Where(r => r.resourceName == fuel.ResourceName && r != fuelReactor)).ToList();

                double spareCapacityForFuel = fuelReactor.maxAmount - actinidesReactor.amount - fuelReactor.amount;
                fuelResources.ForEach(res =>
                {
                    double resourceAvailable = res.amount;
                    double resourceAdded = Math.Min(resourceAvailable, spareCapacityForFuel);
                    fuelReactor.amount += resourceAdded;
                    res.amount -= resourceAdded;
                    spareCapacityForFuel -= resourceAdded;
                });
            }
        }

        public override double MaximumThermalPower
        {
            get
            {
                if (!HighLogic.LoadedSceneIsFlight)
                    return base.MaximumThermalPower;

                if (CheatOptions.UnbreakableJoints)
                {
                    actinidesModifer = 1;
                    return base.MaximumThermalPower;
                }

                double poisonCurAmount = 0;
                double poisonMaxAmount = 0;

                var actinidesResource = _actinideDefinition != null
                    ? part.Resources.Get(_actinideDefinition.id)
                    : part.Resources[ResourceSettings.Config.Actinides];

                if (actinidesResource != null && actinidesResource.amount.IsNotInfinityOrNaN())
                {
                    poisonCurAmount += actinidesResource.amount;
                    poisonMaxAmount += actinidesResource.maxAmount;
                }

                var protectResource = _protactiniumDefinition != null
                    ? part.Resources.Get(_protactiniumDefinition.id)
                    : part.Resources[ResourceSettings.Config.Protactinium233];

                if (protectResource != null && protectResource.amount.IsNotInfinityOrNaN())
                {
                    poisonCurAmount += protectResource.amount;
                    poisonMaxAmount += protectResource.maxAmount;
                }

                if (poisonMaxAmount <= 0)
                    return base.MaximumThermalPower;

                var fuelActinideMassRatio = 1 - poisonCurAmount / poisonMaxAmount;

                actinidesModifer = Math.Pow(fuelActinideMassRatio * fuelActinideMassRatio, CurrentFuelMode.ReactionRatePowerMultiplier);

                return base.MaximumThermalPower * actinidesModifer;
            }
        }

        protected override void WindowReactorStatusSpecificOverride()
        {
            PrintToGuiLayout(Localizer.Format("#LOC_KSPIE_FissionMSRGC_Actinides_Poisoning"), (100 - actinidesModifer * 100).ToString("0.000000") + "%", boldStyle, textStyle);
        }

        public override double CoreTemperature
        {
            get
            {
                if (CheatOptions.IgnoreMaxTemperature || !HighLogic.LoadedSceneIsFlight || isupgraded || !(powerPercent >= minThrottle * 100))
                    return actinidesModifer * base.CoreTemperature;

                var baseCoreTemperature = base.CoreTemperature;

                double tempScale;
                if (minimumTemperature > 0)
                    tempScale = minimumTemperature;
                else if (vessel != null && FNRadiator.HasRadiatorsForVessel(vessel))
                    tempScale = FNRadiator.GetAverageMaximumRadiatorTemperatureForVessel(vessel);
                else
                    tempScale = baseCoreTemperature / 2;

                var tempDiff = (baseCoreTemperature - tempScale) * Math.Pow(powerPercent / 100, temperatureThrottleExponent);
                return Math.Min(tempScale + tempDiff, actinidesModifer * baseCoreTemperature);
            }
        }

        public override double MaxCoreTemperature => actinidesModifer * base.CoreTemperature;

        public override void OnUpdate()
        {
            Events[nameof(ManualShutdown)].active = Events[nameof(ManualShutdown)].guiActiveUnfocused = IsEnabled;
            Events[nameof(Refuel)].active = Events[nameof(Refuel)].guiActiveUnfocused = !IsEnabled && !ongoingDecay;
            Events[nameof(Refuel)].guiName = "Refuel " + (CurrentFuelMode != null ? CurrentFuelMode.ModeGUIName : "");
            Events[nameof(SwapFuelMode)].active = Events[nameof(SwapFuelMode)].guiActiveUnfocused = fuelModes.Count > 1 && !IsEnabled && !ongoingDecay;
            Events[nameof(SwapFuelMode)].guiActive = Events[nameof(SwapFuelMode)].guiActiveUnfocused = fuelModes.Count > 1;
            Events[nameof(SwitchMode)].guiActiveEditor = Events[nameof(SwitchMode)].guiActive = Events[nameof(SwitchMode)].guiActiveUnfocused = CheckFuelModes() > 1;
            Events[nameof(EditorSwapFuel)].guiActiveEditor = fuelModes.Count > 1;
            Events[nameof(DumpActinides)].guiActive = canDumpActinides;

            base.OnUpdate();
        }

        public override void OnStart(StartState state)
        {
            Debug.Log("[KSPI]: OnStart MSRGC " + part.name);

            _actinideDefinition = PartResourceLibrary.Instance.GetDefinition(ResourceSettings.Config.Actinides);
            _protactiniumDefinition = PartResourceLibrary.Instance.GetDefinition(ResourceSettings.Config.Protactinium233);

            // legacy rule to make old systems function
            var legacyThorium = part.Resources[ResourceSettings.Config.ThoriumTetraflouride];
            if (legacyThorium != null && legacyThorium.amount > 0)
            {
                var uranium233 = part.Resources[ResourceSettings.Config.Uranium233];
                if (uranium233 != null && uranium233.amount <= 0)
                {
                    uranium233.maxAmount = Math.Max(legacyThorium.maxAmount, uranium233.maxAmount);
                    uranium233.amount = uranium233.maxAmount;
                }
            }

            // start as normal
                base.OnStart(state);

            if (part.vessel != null)
            {
                _managedTransferableActinideStores.AddRange(
                    part.vessel.FindPartModulesImplementing<FNResourceTransfer>()
                        .Where(m => m.resourceName == _actinideDefinition.displayName));

                _managedTransferableProtactiniumStores.AddRange(
                    part.vessel.FindPartModulesImplementing<FNResourceTransfer>()
                        .Where(m => m.resourceName == _protactiniumDefinition.displayName));
            }

            fuelModeStr = CurrentFuelMode.ModeGUIName;

            _manualRestartEvent = Events[nameof(ManualRestart)];

            Events[nameof(DumpActinides)].guiActive = canDumpActinides;
            Events[nameof(SwitchMode)].guiActiveEditor = Events[nameof(SwitchMode)].guiActive = Events[nameof(SwitchMode)].guiActiveUnfocused = CheckFuelModes() > 1;
            Events[nameof(SwapFuelMode)].guiActive = Events[nameof(SwapFuelMode)].guiActiveUnfocused = fuelModes.Count > 1;
            Events[nameof(EditorSwapFuel)].guiActiveEditor = fuelModes.Count > 1;

            // Upstream stopped here when the current mode had run out of a fuel — exactly when a fuel swap is wanted —
            // leaving reactorMainFuelDensityInTon = 0 and the missing fuel resources unadded, so every later swap
            // gave the new fuels zero capacity (KSPIE #696, #678).
            DetermineFuelCapacityReference();

            foreach (var fuelMode in fuelModes)
            {
                foreach (var reactorFuel in fuelMode.Variants.First().ReactorFuels)
                {
                    var resource = part.Resources.Get(reactorFuel.ResourceName);
                    if (resource == null)
                        // non-tweakable resources
                        part.Resources.Add(reactorFuel.ResourceName, 0, 0, true, false, false, true, 0);
                }
            }

            // The current mode has no fuel capacity at all (only modes with the fuel stored in the reactor itself): a reactor
            // left 0/0 by the old swap code, or a part whose default mode does not match the fuel it carries (KANDL:
            // default Uranium-238 TWR, carries Plutonium-238). Prefer a mode the part has fuel tanks for; only if there is
            // none, restore capacity — without adding fuel, also in the VAB.
            if (CurrentFuelMode.Variants.First().ReactorFuels.All(fuel => !fuel.ConsumeGlobal)
                && CurrentFuelMassCapacity(withFallback: false) <= 0)
            {
                var modeWithTanks = fuelModes.FirstOrDefault(mode => mode.Variants.First().ReactorFuels
                    .All(fuel => part.Resources.Get(fuel.ResourceName)?.maxAmount > 0));

                if (modeWithTanks != null)
                {
                    Debug.Log("[KSPI]: " + part.name + " fuel mode " + CurrentFuelMode.ModeGUIName + " has no fuel capacity, switching to " + modeWithTanks.ModeGUIName);
                    CurrentFuelMode = modeWithTanks;
                    fuelModeStr = CurrentFuelMode.ModeGUIName;
                }
                else if (_fallbackFuelMassCapacity > 0)
                {
                    Debug.Log("[KSPI]: " + part.name + " fuel mode " + CurrentFuelMode.ModeGUIName + " has no fuel capacity, restoring " + _fallbackFuelMassCapacity.ToString("0.###") + " t");
                    EnableResources(_fallbackFuelMassCapacity, fillInEditor: false);
                }
            }
        }

        // Reference volume of the core: reactorMainFuelMaxAmount units of a fuel with density reactorMainFuelDensityInTon.
        // Fuel swaps use the fuel mass of the current mode (CurrentFuelMassCapacity); this reference is only the fallback
        // when the current mode has no capacity at all, and the scale of actinide reprocessing (ReprocessFuel).
        private void DetermineFuelCapacityReference()
        {
            var currentVariant = CurrentFuelMode.Variants.FirstOrDefault(m => m.FuelRatio > 0) ?? CurrentFuelMode.Variants.First();
            var firstReactorFuel = currentVariant.ReactorFuels.First();
            var initialReactorFuel = part.Resources.Get(firstReactorFuel.ResourceName);

            if (reactorMainFuelMaxAmount > 0)
            {
                // set in the part config (and scaled by TweakScale): counted in the main fuel of the default fuel mode,
                // not of the mode the reactor happens to be in now
                reactorMainFuelDensityInTon = fuelModes.First().Variants.First().ReactorFuels.First().DensityInTon;
            }
            else if (initialReactorFuel != null && initialReactorFuel.maxAmount > 0)
            {
                reactorMainFuelMaxAmount = initialReactorFuel.maxAmount;
                reactorMainFuelDensityInTon = firstReactorFuel.DensityInTon;
            }
            else
            {
                // The largest load among the fuels of all modes, on this part, else in the part config (a reactor left 0/0).
                // Upstream took the densest resource of any kind: Kerbalism's Shielding or the Actinides product won.
                // Computed once here: a later TweakScale rescale in the VAB does not reach this fallback.
                var fuelNames = new HashSet<string>(fuelModes.SelectMany(mode => mode.Variants.First().ReactorFuels).Select(fuel => fuel.ResourceName));
                var mainFuel = LargestFuelLoad(part.Resources, fuelNames) ?? LargestFuelLoad(part.partInfo?.partPrefab?.Resources, fuelNames);
                if (mainFuel != null)
                {
                    reactorMainFuelMaxAmount = mainFuel.maxAmount;
                    reactorMainFuelDensityInTon = mainFuel.info.density;
                }
                else
                {
                    reactorMainFuelMaxAmount = part.mass * 100;
                    reactorMainFuelDensityInTon = firstReactorFuel.DensityInTon;
                }
            }

            _fallbackFuelMassCapacity = reactorMainFuelMaxAmount * reactorMainFuelDensityInTon;
        }

        private static PartResource LargestFuelLoad(PartResourceList resources, HashSet<string> fuelNames)
        {
            return resources?.Where(r => fuelNames.Contains(r.resourceName) && r.maxAmount > 0 && r.info.density > 0)
                .OrderByDescending(r => r.maxAmount * r.info.density).FirstOrDefault();
        }

        // Fuel mass (t) the core holds in the current mode: capacity × density summed over the mode's fuels. A swap gives
        // the new mode the same mass, split by fuel usage, so capacities neither drift with the mode the reactor was loaded
        // in nor vanish (KSPIE #678, #696).
        private double CurrentFuelMassCapacity(bool withFallback = true)
        {
            var tons = CurrentFuelMode.Variants.First().ReactorFuels.Sum(fuel =>
            {
                var resource = part.Resources.Get(fuel.ResourceName);
                return resource != null && resource.maxAmount > 0 ? resource.maxAmount * fuel.DensityInTon : 0;
            });

            return tons > 0 || !withFallback ? tons : _fallbackFuelMassCapacity;
        }

        // A fuel in the core that the next mode would not hold: a current fuel the vessel's tanks had no room for after
        // DefaultCurrentFuel, or a fuel of the next mode already in the core (beryllium pumped into the molten salt
        // reactor's config tank) above its new capacity. Swapping would leave it in a tank with less capacity than fuel,
        // and Refuel would push the excess into other tanks past their capacity (KSPIE #696).
        private ReactorFuel FuelLeftWithoutRoom(ReactorFuelType nextFuelMode, double fuelMassCapacity)
        {
            var nextFuels = nextFuelMode.Variants.First().ReactorFuels;
            var nextTotalTonsFuelUsePerMj = nextFuels.Sum(m => m.TonsFuelUsePerMj);
            var currentFuels = CurrentFuelMode.Variants.First().ReactorFuels;
            var fuelsToCheck = currentFuels.Concat(nextFuels.Where(next => currentFuels.All(current => current.ResourceName != next.ResourceName)));

            foreach (var fuel in fuelsToCheck)
            {
                var amountLeft = GetLocalResourceAmount(fuel);
                if (amountLeft < 0.001)
                    continue;

                var nextFuel = nextFuels.FirstOrDefault(m => m.ResourceName == fuel.ResourceName);
                var nextCapacity = nextFuel == null || nextTotalTonsFuelUsePerMj <= 0 || nextFuel.DensityInTon <= 0
                    ? 0
                    : fuelMassCapacity * (nextFuel.TonsFuelUsePerMj / nextTotalTonsFuelUsePerMj) / nextFuel.DensityInTon;

                if (amountLeft > nextCapacity)
                    return fuel;
            }

            return null;
        }

        private static void PostSwapMessage(string message)
        {
            ScreenMessages.PostScreenMessage(message, 8f, ScreenMessageStyle.UPPER_CENTER);
            Debug.Log("[KSPI]: " + message);
        }

        public override void OnFixedUpdate()
        {
            // if reactor is overloaded with actinides, stop functioning
            if (IsEnabled && part.Resources.Contains(ResourceSettings.Config.Actinides))
            {
                if (part.Resources[ResourceSettings.Config.Actinides].amount >= part.Resources[ResourceSettings.Config.Actinides].maxAmount)
                {
                    part.Resources[ResourceSettings.Config.Actinides].amount = part.Resources[ResourceSettings.Config.Actinides].maxAmount;
                    IsEnabled = false;
                }
            }
            base.OnFixedUpdate();
        }

        public override bool shouldScaleDownJetISP()
        {
            return true;
        }

        public double ReprocessFuel(double rate, double deltaTime, double productionModifier, Part processor)
        {
            if (!hasStarted)
                OnStart(StartState.None);

            double poisonsAmount = 0;

            var localActinides = part.Resources.Get(_actinideDefinition.id);
            if (localActinides != null)
                poisonsAmount += localActinides.amount;

            var localProtactinium = part.Resources.Get(_protactiniumDefinition.id); ;
            if (localProtactinium != null)
                poisonsAmount += localProtactinium.amount;

            if (poisonsAmount <= 0)
                return 0;

            if (reactorMainFuelMaxAmount <= 0)
                reactorMainFuelMaxAmount = part.mass * 100;

            var poisonsProcessingSpeed = poisonsProcessingSpeedMult * productionModifier * deltaTime * (poisonsAmount / reactorMainFuelMaxAmount);
            var effectivePoisonsCapacity = Math.Min(rate, poisonsProcessingSpeed);

            double massPoisonDecrease = 0;
            if (localActinides != null)
            {
                var actinidesCapacity = effectivePoisonsCapacity * (localActinides.amount / poisonsAmount);
                var processorActinides = processor.Resources.Get(_actinideDefinition.id);
                if (processorActinides != null)
                {
                    var availableStorage = processorActinides.maxAmount - processorActinides.amount;
                    var shortage = actinidesCapacity - availableStorage;

                    if (shortage > 0)
                    {
                        var storedAmount = StoreToTransferableTank(_managedTransferableActinideStores, _actinideDefinition, shortage);
                        localActinides.amount = localActinides.amount - storedAmount;
                        massPoisonDecrease += storedAmount * localActinides.info.density;
                    }

                    var storedActinides = Math.Min(availableStorage, actinidesCapacity);
                    processorActinides.amount = processorActinides.amount + storedActinides;

                    massPoisonDecrease += storedActinides * localActinides.info.density;
                    localActinides.amount = Math.Max(localActinides.amount - storedActinides, 0);
                }
            }

            if (localProtactinium != null)
            {
                var protactiniumCapacity = effectivePoisonsCapacity * (localProtactinium.amount / poisonsAmount);
                var processorProtactinium = processor.Resources.Get(_protactiniumDefinition.id);
                if (processorProtactinium != null)
                {
                    var availableStorage = processorProtactinium.maxAmount - processorProtactinium.amount;
                    var shortage = protactiniumCapacity - availableStorage;

                    if (shortage > 0)
                    {
                        var storedAmount = StoreToTransferableTank(_managedTransferableProtactiniumStores, _protactiniumDefinition, shortage);
                        localProtactinium.amount = localProtactinium.amount - storedAmount;
                        massPoisonDecrease += storedAmount * localProtactinium.info.density;
                    }

                    var storedProtactinium = Math.Min(processorProtactinium.maxAmount - processorProtactinium.amount, protactiniumCapacity);
                    processorProtactinium.amount = processorProtactinium.amount + storedProtactinium;

                    massPoisonDecrease += storedProtactinium * localProtactinium.info.density;
                    localProtactinium.amount = Math.Max(localProtactinium.amount - storedProtactinium, 0);
                }
            }

            if (massPoisonDecrease == 0)
                return effectivePoisonsCapacity;

            var reactorFuels = CurrentFuelVariant?.ReactorFuels;
            if (reactorFuels == null)
                return effectivePoisonsCapacity;

            var reactorProducts = CurrentFuelVariant.ReactorProducts;

            var totalProductMassUsagePerMw = reactorProducts.Sum(fuel => fuel.TonsProductUsePerMj * fuelUsePerMJMult);
            var totalPoisonMassUsagePerMw = reactorProducts
                .Where(m => m.ResourceName == ResourceSettings.Config.Actinides || m.ResourceName == "Protactinium-233")
                .Sum(fuel => fuel.TonsProductUsePerMj * fuelUsePerMJMult);

            var poisonRatio = totalPoisonMassUsagePerMw / totalProductMassUsagePerMw;

            var totalProcessingMass = poisonRatio > 0 ? massPoisonDecrease / poisonRatio : 0;
            var totalFuelMassUsagePerMw = reactorFuels.Sum(fuel => fuel.TonsFuelUsePerMj * fuelUsePerMJMult);

            foreach (var reactorFuel in reactorFuels)
            {
                var processorFuelResource = processor.Resources[reactorFuel.ResourceName];
                if (processorFuelResource == null || processorFuelResource.amount <= 0)
                    continue;

                var reactorFuelResource = part.Resources[reactorFuel.ResourceName];
                if (reactorFuelResource == null)
                    continue;

                var availableReactorStorage = reactorFuelResource.maxAmount - reactorFuelResource.amount;
                if (availableReactorStorage <= 0)
                    continue;

                var powerFraction = totalFuelMassUsagePerMw > 0 ? reactorFuel.TonsFuelUsePerMj * fuelUsePerMJMult / totalFuelMassUsagePerMw : 1;
                var requiredFuelAmount = Math.Min(availableReactorStorage, totalProcessingMass * powerFraction / reactorFuelResource.info.density);

                var availableFuelAmount = Math.Min(requiredFuelAmount, processorFuelResource.amount);
                processorFuelResource.amount -= availableFuelAmount;

                reactorFuelResource.amount = reactorFuelResource.amount + availableFuelAmount;
            }

            return effectivePoisonsCapacity;
        }

        private double StoreToTransferableTank(IEnumerable<FNResourceTransfer> managedTransferableResources, PartResourceDefinition resourceDefinition,  double shortageDecayProductAmount)
        {
            double initialShortage = shortageDecayProductAmount;

            var tanksWithAvailableStorage = managedTransferableResources
                .Where(m => m.AvailableStorage > 0)
                .OrderByDescending(m => m.transferPriority).ToList();

            if (!tanksWithAvailableStorage.Any())
            {
                return -part.RequestResource(resourceDefinition.id, -shortageDecayProductAmount, ResourceFlowMode.STACK_PRIORITY_SEARCH);
            }

            foreach (var fnResourceTransfer in tanksWithAvailableStorage)
            {
                var unrestrictedNewAmount = fnResourceTransfer.PartResource.amount + shortageDecayProductAmount;
                shortageDecayProductAmount = Math.Max(0, unrestrictedNewAmount - fnResourceTransfer.PartResource.maxAmount);
                fnResourceTransfer.PartResource.amount = Math.Min(fnResourceTransfer.PartResource.maxAmount, unrestrictedNewAmount);

                if (shortageDecayProductAmount <= 0)
                    break;
            }

            return initialShortage - shortageDecayProductAmount;
        }

        // This Methods loads the correct fuel mode
        public override void SetDefaultFuelMode()
        {
            if (fuelModes == null)
            {
                Debug.Log("[KSPI]: MSRC SetDefaultFuelMode - load fuel modes");
                fuelModes = GetReactorFuelModes();
            }

            // By name first: the index shifts when the list of available modes differs from when it was saved, e.g. a
            // mode locked behind a tech node in career (KSPIE #670). The fusion reactors restore by name the same way.
            var savedFuelMode = string.IsNullOrEmpty(fuel_mode_name) ? null : fuelModes.FirstOrDefault(m => m.ModeGUIName == fuel_mode_name);
            CurrentFuelMode = savedFuelMode ?? (fuel_mode >= 0 && fuel_mode < fuelModes.Count ? fuelModes[fuel_mode] : fuelModes.FirstOrDefault());
        }

        private void DisableResources()
        {
            var firstVariant = CurrentFuelMode.Variants.First();

            foreach (var fuel in firstVariant.ReactorFuels)
            {
                var resource = part.Resources.Get(fuel.ResourceName);

                if (resource == null)
                    continue;

                resource.maxAmount = 0;

                if (!HighLogic.LoadedSceneIsEditor)
                    continue;

                resource.amount = 0;
                resource.isTweakable = false;
            }
        }

        // Size the fuels of the current mode to hold fuelMassCapacity tons together, split by fuel usage; in the VAB also
        // fill them (a swap there gives a full load, as upstream), unless fillInEditor is false.
        private void EnableResources(double fuelMassCapacity, bool fillInEditor = true)
        {
            // calculate total
            var firstVariant = CurrentFuelMode.Variants.First();

            var totalTonsFuelUsePerMj = firstVariant.ReactorFuels.Sum(m => m.TonsFuelUsePerMj);

            foreach (ReactorFuel fuel in firstVariant.ReactorFuels)
            {
                var resource = part.Resources.Get(fuel.ResourceName);
                if (resource == null || totalTonsFuelUsePerMj <= 0 || fuel.DensityInTon <= 0) continue;

                var weightedAmount = fuelMassCapacity * (fuel.TonsFuelUsePerMj / totalTonsFuelUsePerMj) / fuel.DensityInTon;

                resource.maxAmount = weightedAmount;

                if (!HighLogic.LoadedSceneIsEditor)
                    continue;

                resource.isTweakable = true;
                if (fillInEditor)
                    resource.amount = weightedAmount;
            }

            UpdatePartActionWindow();
        }

        private void UpdatePartActionWindow()
        {
            var window = FindObjectsOfType<UIPartActionWindow>().FirstOrDefault(w => w.part == part);
            if (window == null) return;

            foreach (UIPartActionWindow actionWindow in FindObjectsOfType<UIPartActionWindow>())
            {
                if (window.part != part) continue;
                actionWindow.ClearList();
                actionWindow.displayDirty = true;
            }
        }

        private void SwitchFuelType()
        {
            fuel_mode = NextFuelModeIndex();
            CurrentFuelMode = fuelModes[fuel_mode];
            fuelModeStr = CurrentFuelMode.ModeGUIName;
        }

        // next fuel mode with a different main fuel (back to the current one if there is none)
        private int NextFuelModeIndex()
        {
            var startFirstFuelType = CurrentFuelMode.Variants.First().ReactorFuels.First();
            var start = Math.Max(0, fuelModes.IndexOf(CurrentFuelMode));
            var index = start;
            do
            {
                index++;
                if (index >= fuelModes.Count)
                    index = 0;
            }
            while (fuelModes[index].Variants.First().ReactorFuels.First().ResourceName == startFirstFuelType.ResourceName && index != start);

            return index;
        }

        private void DefaultCurrentFuel()
        {
            foreach (ReactorFuel fuel in CurrentFuelMode.Variants.First().ReactorFuels)
            {
                var fuelReactor = part.Resources[fuel.ResourceName];
                var swapResourceList = part.vessel.parts.SelectMany(p => p.Resources.Where(r => r.resourceName == fuel.ResourceName && r != fuelReactor)).ToList();

                swapResourceList.ForEach(res =>
                {
                    double fuelAdded = Math.Min(fuelReactor.amount, res.maxAmount - res.amount);
                    fuelReactor.amount -= fuelAdded;
                    res.amount += fuelAdded;
                });
            }
        }


        // Returns the number of fuel-modes available for the currently selected fuel-type
        public int CheckFuelModes()
        {
            var modesAvailable = 0;
            var fuelMode = CurrentFuelMode.Variants.First();
            var fuelName = fuelMode.ReactorFuels.First().FuelName;
            foreach (var reactorFuelType in fuelModes)
            {
                var currentFuelMode = reactorFuelType.Variants.First();
                var reactorFuel = currentFuelMode.ReactorFuels.First();
                if (reactorFuel.FuelName == fuelName)
                    modesAvailable++;
            }

            return modesAvailable;
        }

        public override void Update()
        {
            base.Update();

            if (_manualRestartEvent != null)
                _manualRestartEvent.externalToEVAOnly = !CheatOptions.NonStrictAttachmentOrientation;
        }
    }
}
