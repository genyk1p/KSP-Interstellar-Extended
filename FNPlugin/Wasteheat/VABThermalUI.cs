using FNPlugin.Background.Core;
using KSP.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FNPlugin.Wasteheat
{
    /// <summary>
    /// The editor "Heat" window (KSPIE toolbar button in the VAB/SPH). Fork (cooling step 5): rewritten on the background
    /// model (EditorHeat → SteadyStateSolver) in place of the author's own estimate, so the editor shows what the vessel
    /// settles at in flight and on rails warp. In: where (space, or an atmosphere the player has reached at an
    /// altitude) and the load (share of the power limit: the most the plant keeps up in equilibrium). Out: the power
    /// limit and what sets it (EditorHeat.Limit); at the chosen load electricity, efficiency, reactor power, heat bar,
    /// waste heat and a verdict; what the model leaves out; the cryogenic tanks, full (EditorHeat.EvaluateCryo).
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class VABThermalUI : MonoBehaviour
    {
        public static bool RenderWindow { get; set; }

        private const int WindowId = 825462;
        private const int WindowWidth = 440;
        private const int LabelWidth = 190;

        private Rect _windowPosition = new Rect(500, 300, WindowWidth, 100);

        private List<CelestialBody> _places; // null = space, then the reached bodies with an atmosphere
        private int _place;
        private float _altitudeKm;
        private float _loadPercent = 100;

        private EditorHeat.Result _result;
        private EditorHeat.Cryo _cryo;
        private bool _heatFailed, _cryoFailed;
        private float _nextUpdate;
        private static readonly HashSet<string> LoggedErrors = new HashSet<string>();

        private GUIStyle _bold, _label, _right, _center, _hint, _green, _yellow, _red;

        public void Start()
        {
            if (PluginHelper.usingToolbar)
                RenderWindow = false;
        }

        public void Update()
        {
            if (!RenderWindow || EditorLogic.fetch == null || EditorLogic.fetch.ship == null)
                return;
            if (Time.unscaledTime < _nextUpdate)
                return;
            _nextUpdate = Time.unscaledTime + 0.5f;

            // apart: a part that breaks one estimate leaves the other; a failure says so in the window and in the log
            var parts = EditorLogic.fetch.ship.parts;
            try
            {
                _result = EditorHeat.Evaluate(parts, SelectedBody(), _altitudeKm * 1000.0, _loadPercent / 100.0);
                _heatFailed = false;
            }
            catch (Exception e)
            {
                _result = null;
                _heatFailed = true;
                LogOnce("heat", e);
            }
            try
            {
                _cryo = EditorHeat.EvaluateCryo(parts);
                _cryoFailed = false;
            }
            catch (Exception e)
            {
                _cryo = null;
                _cryoFailed = true;
                LogOnce("cryo tank", e);
            }
        }

        /// <summary>Once per kind of failure and game session (the estimate reruns twice a second).</summary>
        private static void LogOnce(string estimate, Exception e)
        {
            var key = estimate + " " + e.GetType().FullName + " " + e.TargetSite?.Name;
            if (LoggedErrors.Count >= 20 || !LoggedErrors.Add(key)) return;
            Debug.LogError("[KSPI]: VABThermalUI: the " + estimate + " estimate failed (further errors of this kind are not logged): " + e);
        }

        protected void OnGUI()
        {
            if (RenderWindow)
                _windowPosition = GUILayout.Window(WindowId, _windowPosition, Window, Localizer.Format("#LOC_KSPIE_EditorHeat_Title"));
        }

        private void Window(int windowId)
        {
            EnsureStyles();

            if (GUI.Button(new Rect(_windowPosition.width - 20, 2, 18, 18), "x"))
                RenderWindow = false;

            GUILayout.BeginVertical();

            // one place for the whole pass: the buttons change _place, the rows that depend on it change next frame
            // (changing them within the pass makes Unity throw "Mismatched LayoutGroup" into the log)
            var places = Places();
            var body = places[Math.Min(_place, places.Count - 1)];

            PlaceRow(places, body);
            LoadRow();

            GUILayout.Space(6);
            var result = _result;
            var hasPlant = result != null && result.Spec.Reactors.Count > 0 && result.Spec.Generators.Count > 0 && result.MaximumElectricMW > 0;
            if (_heatFailed)
                GUILayout.Label(Localizer.Format("#LOC_KSPIE_EditorHeat_Failed"), _red);
            else if (result == null)
                GUILayout.Label(Localizer.Format("#LOC_KSPIE_EditorHeat_NoData"), _hint);
            else if (!hasPlant)
                GUILayout.Label(Localizer.Format(result.Spec.Radiators.Count == 0 && result.Spec.Generators.Count > 0
                    ? "#LOC_KSPIE_EditorHeat_NoRadiators" : "#LOC_KSPIE_EditorHeat_NoPlant"), _label);
            else
                Results(result);

            // also without a plant: it says why there is nothing to estimate (MHD only, a beamed power source...)
            if (result != null && result.NotCounted.Count > 0)
            {
                GUILayout.Space(4);
                GUILayout.Label(Localizer.Format("#LOC_KSPIE_EditorHeat_NotCounted", string.Join(", ", result.NotCounted.ToArray())), _hint);
            }
            if (hasPlant && body != null)
                GUILayout.Label(Localizer.Format("#LOC_KSPIE_EditorHeat_NoConvection"), _hint);

            if (_cryoFailed)
                GUILayout.Label(Localizer.Format("#LOC_KSPIE_EditorHeat_CryoFailed"), _red);
            else
                CryoBlock(_cryo);

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        private void PlaceRow(List<CelestialBody> places, CelestialBody body)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Localizer.Format("#LOC_KSPIE_EditorHeat_Where"), _bold, GUILayout.Width(LabelWidth));
            if (GUILayout.Button("<", GUILayout.Width(24)))
                _place = (_place + places.Count - 1) % places.Count;
            GUILayout.Label(body == null ? Localizer.Format("#LOC_KSPIE_EditorHeat_Space") : Localizer.Format("#LOC_KSPIE_EditorHeat_Atmosphere", Localizer.Format("<<1>>", body.displayName)), _center, GUILayout.ExpandWidth(true));
            if (GUILayout.Button(">", GUILayout.Width(24)))
                _place = (_place + 1) % places.Count;
            GUILayout.EndHorizontal();

            if (body == null) return;
            var topKm = (float)(body.atmosphereDepth / 1000.0);
            _altitudeKm = Mathf.Clamp(_altitudeKm, 0, topKm);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Localizer.Format("#LOC_KSPIE_EditorHeat_Altitude", _altitudeKm.ToString("F0")), _label, GUILayout.Width(LabelWidth));
            _altitudeKm = Mathf.Round(GUILayout.HorizontalSlider(_altitudeKm, 0, topKm, GUILayout.ExpandWidth(true)));
            GUILayout.EndHorizontal();
        }

        private void LoadRow()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Localizer.Format("#LOC_KSPIE_EditorHeat_Load", _loadPercent.ToString("F0")), _bold, GUILayout.Width(LabelWidth));
            _loadPercent = Mathf.Round(GUILayout.HorizontalSlider(_loadPercent, 0, 100, GUILayout.ExpandWidth(true)) / 5) * 5;
            GUILayout.EndHorizontal();
            GUILayout.Label(Localizer.Format("#LOC_KSPIE_EditorHeat_LoadHint"), _hint);
        }

        private void Results(EditorHeat.Result result)
        {
            var state = result.State;
            var fill = state.WasteHeatFill;

            // the most it keeps up, and what stops it there
            Row(Localizer.Format("#LOC_KSPIE_EditorHeat_Sustainable"), Power(result.SustainableMW), _right);
            string limit;
            switch (result.MaxLimit)
            {
                case EditorHeat.Limit.Radiators:
                    limit = Localizer.Format("#LOC_KSPIE_EditorHeat_LimitRadiators");
                    break;
                case EditorHeat.Limit.Plant:
                    limit = Localizer.Format("#LOC_KSPIE_EditorHeat_LimitPlant");
                    break;
                default:
                    limit = Localizer.Format("#LOC_KSPIE_EditorHeat_LimitEfficiency",
                        SteadyStateSolver.VesselRadiatorTemperature(result.Spec, result.MaxState.WasteHeatFill).ToString("F0"));
                    break;
            }
            GUILayout.Label(limit, _hint);

            // the chosen load
            GUILayout.Space(4);
            GUILayout.Label(Localizer.Format("#LOC_KSPIE_EditorHeat_AtLoad", _loadPercent.ToString("F0")), _bold);
            Row(Localizer.Format("#LOC_KSPIE_EditorHeat_Electricity"), Power(state.ElectricMW), _right);
            Row(Localizer.Format("#LOC_KSPIE_EditorHeat_Efficiency"),
                Localizer.Format("#LOC_KSPIE_EditorHeat_EfficiencyValue", (result.GeneratorEfficiency * 100).ToString("F0"), result.RadiatorTemperatureK.ToString("F0")), _right);
            Row(Localizer.Format("#LOC_KSPIE_EditorHeat_Reactors"), (result.ReactorPowerRatio * 100).ToString("F0") + " %", _right);
            Row(Localizer.Format("#LOC_KSPIE_EditorHeat_Bar"), (fill * 100).ToString("F0") + " %", fill >= 0.95 ? _red : fill >= 0.9 ? _yellow : _right);
            Row(Localizer.Format("#LOC_KSPIE_EditorHeat_WasteHeat"), Power(state.WasteHeatMW), _right);

            GUILayout.Space(4);
            string verdict;
            GUIStyle style;
            if (state.Overheated)
            {
                verdict = Localizer.Format("#LOC_KSPIE_EditorHeat_Overheated");
                style = _red;
            }
            else if (state.HeatLimited)
            {
                verdict = Localizer.Format("#LOC_KSPIE_EditorHeat_HeatLimited", (fill * 100).ToString("F0"));
                style = _red;
            }
            else if (result.RequestedElectricMW >= 0.95 * result.SustainableMW)
            {
                verdict = Localizer.Format("#LOC_KSPIE_EditorHeat_AtMost");
                style = _yellow;
            }
            else if (fill >= 0.9)
            {
                verdict = Localizer.Format("#LOC_KSPIE_EditorHeat_Edge", (fill * 100).ToString("F0"));
                style = _yellow;
            }
            else
            {
                verdict = Localizer.Format("#LOC_KSPIE_EditorHeat_Enough", (fill * 100).ToString("F0"));
                style = _green;
            }
            GUILayout.Label(verdict, style);
            GUILayout.Label(Localizer.Format("#LOC_KSPIE_WasteHeat_GeneratorsHint"), _hint);
        }

        /// <summary>The cryogenic tanks, full: cooling cost, boiloff without it, and how many have cooling off.</summary>
        private void CryoBlock(EditorHeat.Cryo cryo)
        {
            if (cryo == null || cryo.Tanks == 0) return;

            GUILayout.Space(6);
            GUILayout.Label(Localizer.Format("#LOC_KSPIE_EditorHeat_Cryo", cryo.Tanks), _bold);
            Row(Localizer.Format("#LOC_KSPIE_EditorHeat_CryoCooling"), Localizer.Format("#LOC_KSPIE_EditorHeat_CryoCoolingValue", cryo.CoolingEcPerSecond.ToString("0.###")), _right);
            var boiloff = string.Join(", ", cryo.BoiloffPerHour.Select(b => Localizer.Format("#LOC_KSPIE_EditorHeat_CryoBoiloffItem", b.Key, b.Value.ToString("0.##"))).ToArray());
            Row(Localizer.Format("#LOC_KSPIE_EditorHeat_CryoBoiloff"), boiloff, _right);
            if (cryo.TanksOff > 0)
                GUILayout.Label(Localizer.Format("#LOC_KSPIE_EditorHeat_CryoOff", cryo.TanksOff, cryo.Tanks), _yellow);
        }

        private void Row(string label, string value, GUIStyle valueStyle)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _bold, GUILayout.Width(LabelWidth));
            GUILayout.Label(value, valueStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
        }

        private static string Power(double megawatts) => PluginHelper.GetFormattedPowerString(megawatts);

        private CelestialBody SelectedBody()
        {
            var places = Places();
            return places[Math.Min(_place, places.Count - 1)];
        }

        /// <summary>Space, then the bodies with an atmosphere the player has reached (no spoilers of undiscovered worlds).</summary>
        private List<CelestialBody> Places()
        {
            if (_places != null) return _places;

            // built aside and kept only when complete: a failure is retried, not cached half-done
            var places = new List<CelestialBody> { null };
            var bodies = FlightGlobals.Bodies ?? new List<CelestialBody>();
            // not the star: Kopernicus gives the sun an "atmosphere"
            var sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            foreach (var body in bodies.Where(b => b != null && b.atmosphere && !b.isStar && b != sun))
            {
                var tree = ProgressTracking.Instance != null ? ProgressTracking.Instance.GetBodyTree(body) : null;
                var reached = HighLogic.CurrentGame != null && HighLogic.CurrentGame.Mode == Game.Modes.SANDBOX
                    || body.isHomeWorld
                    || tree != null && tree.IsReached;
                if (reached) places.Add(body);
            }
            return _places = places;
        }

        private void EnsureStyles()
        {
            if (_bold != null) return;

            _label = new GUIStyle(GUI.skin.label) { font = PluginHelper.MainFont, wordWrap = true };
            _bold = new GUIStyle(_label) { fontStyle = FontStyle.Bold, wordWrap = false };
            _right = new GUIStyle(_label) { wordWrap = false };
            _center = new GUIStyle(_label) { alignment = TextAnchor.MiddleCenter, wordWrap = false };
            _hint = new GUIStyle(_label) { fontSize = 10 };
            _hint.normal.textColor = new Color(0.7f, 0.7f, 0.7f);
            _green = new GUIStyle(_label);
            _green.normal.textColor = new Color(0.45f, 0.9f, 0.45f);
            _yellow = new GUIStyle(_label);
            _yellow.normal.textColor = new Color(1f, 0.82f, 0.3f);
            _red = new GUIStyle(_label);
            _red.normal.textColor = new Color(1f, 0.4f, 0.35f);
        }
    }
}
