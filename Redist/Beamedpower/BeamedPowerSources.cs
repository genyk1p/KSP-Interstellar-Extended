using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FNPlugin.Beamedpower
{
    public delegate IVesselMicrowavePersistence GetVesselMicrowavePersistanceForProtoVessel(Vessel vessel);
    public delegate IVesselRelayPersistence GetVesselRelayPersistanceForProtoVessel(Vessel vessel);
    public delegate IVesselMicrowavePersistence GetVesselMicrowavePersistanceForVessel(Vessel vessel);
    public delegate IVesselRelayPersistence GetVesselRelayPersistenceForVessel(Vessel vessel);

    // Fork: in every game scene (flight, space center, tracking station), one instance per scene, lists starting empty.
    // Upstream it lived in flight only with DontDestroyOnLoad and once=false: an instance piled up per flight scene,
    // and outside flight the last one kept the previous scene's (destroyed) vessels — while Kerbalism runs unloaded
    // vessels in the space center and tracking station too (background beamed power, todo/kspie-rails-warp.md A3).
    [KSPAddon(KSPAddon.Startup.AllGameScenes, false)]
    public class BeamedPowerSources : MonoBehaviour
    {
        public Dictionary<Vessel, IVesselMicrowavePersistence> globalTransmitters = new Dictionary<Vessel, IVesselMicrowavePersistence>();
        public Dictionary<Vessel, IVesselRelayPersistence> globalRelays = new Dictionary<Vessel, IVesselRelayPersistence>();

        public static GetVesselMicrowavePersistanceForProtoVessel getVesselMicrowavePersistanceForProtoVesselCallback;
        public static GetVesselRelayPersistanceForProtoVessel getVesselRelayPersistanceForProtoVesselCallback;
        public static GetVesselMicrowavePersistanceForVessel getVesselMicrowavePersistanceForVesselCallback;
        public static GetVesselRelayPersistenceForVessel getVesselRelayPersistenceForVesselCallback;

        public static BeamedPowerSources instance
        {
            get;
            private set;
        }

        void Start()
        {
            instance = this;
            Debug.Log("[KSPI]: MicrowaveSources initialized");
        }

        int counter = -1;
        bool initialized = false;

        /// <summary>
        /// Fork: whether the first pass over all vessels of this scene is done. Before it the lists are empty; the
        /// background receivers (FNPlugin Background/BackgroundBeams) keep their last intake meanwhile.
        /// </summary>
        public bool Initialized => initialized;

        public void calculateTransmitters()
        {
            // fork: the snapshot readers are set by FNPlugin (GameEventSubscriber in flight, the background model
            // elsewhere); without them unloaded vessels would be skipped and the lists taken for complete
            if (getVesselMicrowavePersistanceForProtoVesselCallback == null || getVesselRelayPersistanceForProtoVesselCallback == null)
                return;

            // fork: vessels gone from the game without becoming DEAD in FlightGlobals.Vessels (recovered or terminated in
            // the tracking station, merged by docking) would stay listed with their last power, and reading a destroyed
            // vessel's transform throws in the receivers' routing
            PruneGone(globalTransmitters);
            PruneGone(globalRelays);

            counter++;

            if (counter > FlightGlobals.Vessels.Count)
                counter = 0;

            //foreach (var vessel in FlightGlobals.Vessels)
            for (int i = 0; i < FlightGlobals.Vessels.Count; i++ )
            {
                var vessel = FlightGlobals.Vessels[i];

                // first check if vessel is dead
                if (vessel.state == Vessel.State.DEAD)
                {
                    if (globalTransmitters.ContainsKey(vessel))
                    {
                        globalTransmitters.Remove(vessel);
                        globalRelays.Remove(vessel);
                        Debug.Log("[KSPI]: Unregisted Transmitter for vessel " + vessel.name + " " + vessel.id + " because is was destroyed!");
                    }
                    continue;
                }

                // if vessel is offloaded on rails, parse file system
                if (!vessel.loaded)
                {
                    if (initialized && i != counter)
                        continue;

                    if (getVesselMicrowavePersistanceForProtoVesselCallback != null)
                    {
                        // add if vessel can act as a transmitter or relay
                        var trans_pers = getVesselMicrowavePersistanceForProtoVesselCallback(vessel);

                        var hasAnyPower = trans_pers.getAvailablePowerInKW() > 0.001;
                        if (trans_pers.IsActive && hasAnyPower)
                        {
                            if (!globalTransmitters.ContainsKey(vessel))
                            {
                                Debug.Log("[KSPI]: Added unloaded Transmitter for vessel " + vessel.name);
                            }
                            globalTransmitters[vessel] = trans_pers;
                        }
                        else
                        {
                            if (globalTransmitters.Remove(vessel))
                            {
                                if (!trans_pers.IsActive && !hasAnyPower)
                                    Debug.Log("[KSPI]: Unregisted unloaded Transmitter for vessel " + vessel.name + " " + vessel.id + " because transmitter is not active and has no power!");
                                else if (!trans_pers.IsActive)
                                    Debug.Log("[KSPI]: Unregisted unloaded Transmitter for vessel " + vessel.name + " " + vessel.id + " because transmitter is not active!");
                                else if (!hasAnyPower)
                                    Debug.Log("[KSPI]: Unregisted unloaded Transmitter for vessel " + vessel.name + " " + vessel.id + " because transmitter is has no power");
                            }
                        }
                    }

                    if (getVesselRelayPersistanceForProtoVesselCallback != null)
                    {
                        // only add if vessel can act as a relay
                        var relayPower = getVesselRelayPersistanceForProtoVesselCallback(vessel);

                        if (relayPower.IsActive)
                            globalRelays[vessel] = relayPower;
                        else
                            globalRelays.Remove(vessel);
                    }

                    continue;
                }

                // if vessel is loaded
                if (vessel.FindPartModulesImplementing<IMicrowavePowerTransmitter>().Any())
                {
                    if (getVesselMicrowavePersistanceForVesselCallback != null)
                    {
                        // add if vessel can act as a transmitter or relay
                        var transmitterPower = getVesselMicrowavePersistanceForVesselCallback(vessel);

                        if (transmitterPower != null && transmitterPower.IsActive && transmitterPower.getAvailablePowerInKW() > 0.001)
                        {
                            if (!globalTransmitters.ContainsKey(vessel))
                            {
                                Debug.Log("[KSPI]: Added loaded Transmitter for vessel " + vessel.name + " " + vessel.id);
                            }
                            globalTransmitters[vessel] = transmitterPower;
                        }
                        else
                            globalTransmitters.Remove(vessel);

                        // only add if vessel can act as a relay otherwise remove
                        var relayPower = getVesselRelayPersistenceForVesselCallback(vessel);

                        if (relayPower != null && relayPower.IsActive)
                            globalRelays[vessel] = relayPower;
                        else
                            globalRelays.Remove(vessel);
                    }
                }
            }
            initialized = true;

        }

        private readonly HashSet<Vessel> _present = new HashSet<Vessel>();
        private readonly List<Vessel> _gone = new List<Vessel>();

        /// <summary>Fork: removes vessels destroyed or no longer in FlightGlobals.Vessels.</summary>
        private void PruneGone<T>(Dictionary<Vessel, T> list)
        {
            if (list.Count == 0) return;
            _present.Clear();
            foreach (var vessel in FlightGlobals.Vessels)
                _present.Add(vessel);
            _gone.Clear();
            foreach (var vessel in list.Keys)
                if (vessel == null || !_present.Contains(vessel))
                    _gone.Add(vessel);
            foreach (var vessel in _gone)
            {
                list.Remove(vessel);
                Debug.Log("[KSPI]: Unregistered beamed power vessel that is gone from the game");
            }
        }

        void Update()
        {
            // not in the editor (no vessels); in the space center and tracking station every vessel is unloaded and is
            // read from its snapshot
            if (HighLogic.LoadedSceneIsFlight || HighLogic.LoadedScene == GameScenes.SPACECENTER || HighLogic.LoadedScene == GameScenes.TRACKSTATION)
                calculateTransmitters();
        }
    }
}
