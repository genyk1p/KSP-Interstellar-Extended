using FNPlugin.Beamedpower;
using FNPlugin.Reactors;
using FNPlugin.Resources;
using FNPlugin.Wasteheat;
using KSP.Localization;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace FNPlugin.Powermanagement
{
    internal class WasteHeatResourceManager : ResourceManager
    {
        private readonly Queue<double> _resourceFillFractionQueueShort = new Queue<double>();
        private readonly Queue<double> _resourceFillFractionQueueLong = new Queue<double>();

        public double TemperatureRatio { get; private set; }

        public double RadiatorEfficiency { get; private set; }

        public double AtmosphericMultiplier { get; private set; }

        public WasteHeatResourceManager(Guid overmanagerId, ResourceSuppliableModule pm) : base(overmanagerId, pm, ResourceSettings.Config.WasteHeatInMegawatt, FnResourceFlowTypeEven)
        {
            SetWindowPosition(pm.whx, pm.why, 600, 600);
            TemperatureRatio = 0.0;
            RadiatorEfficiency = 0.0;
        }

        protected override void DoWindowFinal()
        {
            PartModule.whx = (int)WindowPosition.x;
            PartModule.why = (int)WindowPosition.y;
        }

        public override void Update(long counter)
        {
            base.Update(counter);

            if (Vessel == null)
                return;

            AtmosphericMultiplier = Vessel.atmDensity > 0 ? Math.Sqrt(Vessel.atmDensity) : 0;

            _resourceFillFractionQueueLong.Enqueue(ResourceFillFraction);
            if (_resourceFillFractionQueueLong.Count > 200)
                _resourceFillFractionQueueLong.Dequeue();

            _resourceFillFractionQueueShort.Enqueue(_resourceFillFractionQueueLong.Average());
            if (ResourceFillFraction > 0 && ResourceFillFraction < 1)
            {
                _resourceFillFractionQueueShort.Enqueue(ResourceFillFraction);
                if (_resourceFillFractionQueueShort.Count > 4)
                {
                    _resourceFillFractionQueueShort.Dequeue();
                    _resourceFillFractionQueueShort.Dequeue();
                }
            }
            else
            {
                if (_resourceFillFractionQueueShort.Count > 4)
                    _resourceFillFractionQueueShort.Dequeue();
            }
            var stabilizedFillFraction = _resourceFillFractionQueueShort.Average();

            TemperatureRatio = Math.Pow(stabilizedFillFraction, 0.75);
            RadiatorEfficiency = 1.0 - Math.Pow(1.0 - stabilizedFillFraction, 400.0);

            MeasureBalance();
        }

        // ------------------------------------------------------------------------------------------------------------
        // Fork (cooling step 5): the "Vessel heat" window in place of the generic resource window — the heat bar with
        // the marks where KSPIE acts, what heats and what cools by part, the measured balance, what comes next, and
        // the generators' efficiency against the radiators' temperature.

        // Where KSPIE acts on the heat bar, for what this vessel carries (refreshed with the part lists, every 5 s):
        // MicrowavePowerTransmitter throttles from 90 % (fixed in its code); electric beamed power receivers stop at
        // 95 % (BeamedPowerReceiver, fixed); reactors throttle from their own overheatThreshHold (0.95 by default, a gas
        // core from 0.2, see the part configs) and those that can shut down do so at emergencyPowerShutdownFraction
        // (0.99 by default). Marks at the same fill are merged.
        private sealed class Mark
        {
            public double Fill;
            public string Text;
        }

        private readonly List<Mark> _marks = new List<Mark>();
        private double _reactorThrottle = double.NaN;

        private const int MaxLines = 6;
        private const double BalanceSmoothingSeconds = 5;

        private double _balanceUT = double.NaN, _balanceAmount, _balanceCapacity, _balanceMW;
        /// <summary>When the measurement (re)started: until BalanceSmoothingSeconds later the balance is not known yet.</summary>
        private double _balanceSince = double.NaN;
        private double _amount, _capacity;

        private List<FNGenerator> _generators;
        private double _partsUT = double.NaN;

        private GUIStyle _hintLabel, _whiteRight, _yellowRight, _redRight, _greenRight;

        protected override string WindowTitle => Localizer.Format("#LOC_KSPIE_WasteHeat_Title");

        /// <summary>The bar's rate of change, MW (MJ/s), smoothed over a few seconds of game time — the true balance.</summary>
        private void MeasureBalance()
        {
            var capacity = GetTotalResourceCapacity();
            var amount = capacity * ResourceFillFraction;
            var now = Planetarium.GetUniversalTime();

            // a retracted radiator, a docking or a staging changes the capacity and moves the amount with it: start over
            var capacityJumped = Math.Abs(capacity - _balanceCapacity) > 0.01 * Math.Max(capacity, _balanceCapacity);
            if (double.IsNaN(_balanceUT) || now < _balanceUT || capacityJumped || double.IsNaN(_balanceMW) || double.IsInfinity(_balanceMW))
            {
                _balanceSince = now;
                _balanceUT = now;
                _balanceAmount = amount;
                _balanceCapacity = capacity;
                _balanceMW = 0;
            }
            else if (now > _balanceUT)
            {
                var elapsed = now - _balanceUT;
                var instant = (amount - _balanceAmount) / elapsed;
                if (!double.IsNaN(instant) && !double.IsInfinity(instant))
                    _balanceMW += (instant - _balanceMW) * Math.Min(1, elapsed / BalanceSmoothingSeconds);
                _balanceUT = now;
                _balanceAmount = amount;
                _balanceCapacity = capacity;
            }

            _amount = amount;
            _capacity = capacity;
        }

        protected override void DoWindow(int windowId)
        {
            EnsureStyles();
            EnsureOwnStyles();

            if (renderWindow && GUI.Button(new Rect(WindowPosition.width - 20, 2, 18, 18), "x"))
                renderWindow = false;

            GUILayout.Space(2);
            GUILayout.BeginVertical();

            RefreshParts();

            // the bar
            var fill = ResourceFillFraction;
            Row(Localizer.Format("#LOC_KSPIE_WasteHeat_Bar"),
                Localizer.Format("#LOC_KSPIE_WasteHeat_BarValue", (fill * 100).ToString("F0"), Megajoules(_amount), Megajoules(_capacity)),
                FillStyle(fill));
            DrawBar(GUILayoutUtility.GetRect(10, 12, GUILayout.ExpandWidth(true)), fill, _marks, BarColor(fill));
            if (_marks.Count > 0)
                GUILayout.Label(Localizer.Format("#LOC_KSPIE_WasteHeat_Marks",
                    string.Join(" · ", _marks.Select(m => Localizer.Format("#LOC_KSPIE_WasteHeat_MarkItem", (m.Fill * 100).ToString("F0"), m.Text)).ToArray())), _hintLabel);

            // What heats and what cools. KSPIE books a generator's conversion as the generator drawing back the part of
            // its reactor's heat it turned into electricity: netted against the heat of the reactor's part (wherever the
            // generator sits). Every other module that draws waste heat cools: radiators, a thermal nozzle's open-cycle
            // cooling by propellant or intake air.
            var heatEntries = new List<KeyValuePair<object, double>>();
            var coolEntries = new List<KeyValuePair<object, double>>();
            foreach (var producer in Producers)
                heatEntries.Add(new KeyValuePair<object, double>(producer.Key, producer.Value.CurrentSupply));
            foreach (var consumer in Consumers)
            {
                if (consumer.Key is FNGenerator generator)
                    heatEntries.Add(new KeyValuePair<object, double>((object)(generator.AttachedPowerSource as PartModule) ?? generator, -consumer.Value.PowerConsumed));
                else
                    coolEntries.Add(new KeyValuePair<object, double>(consumer.Key, consumer.Value.PowerConsumed));
            }
            var heating = Group(heatEntries);
            var cooling = Group(coolEntries);
            // the totals from all entries: a group whose sum is not positive is not listed, but still counts
            var heatingMW = Math.Max(0, Total(heatEntries));
            var coolingMW = Math.Max(0, Total(coolEntries));

            GUILayout.Space(4);
            Row(Localizer.Format("#LOC_KSPIE_WasteHeat_Heating"), PluginHelper.GetFormattedPowerString(heatingMW), rightBoldLabel);
            Lines(heating);
            GUILayout.Space(4);
            Row(Localizer.Format("#LOC_KSPIE_WasteHeat_Cooling"), PluginHelper.GetFormattedPowerString(coolingMW), rightBoldLabel);
            Lines(cooling);

            // the measured balance and what comes next
            GUILayout.Space(4);
            // after a load, a warp change or a capacity jump the smoothing starts from 0: not "steady" until measured
            var measuring = double.IsNaN(_balanceSince) || Planetarium.GetUniversalTime() - _balanceSince < BalanceSmoothingSeconds;
            var steady = !measuring && Math.Abs(_balanceMW) * 600 < 0.005 * _capacity; // the bar moves less than 0.5 % in 10 minutes
            Row(Localizer.Format("#LOC_KSPIE_WasteHeat_Balance"),
                measuring ? "..." : (_balanceMW > 0 ? "+" : "") + PluginHelper.GetFormattedPowerString(_balanceMW),
                measuring || steady ? _whiteRight : _balanceMW > 0 ? _redRight : _greenRight);
            GUILayout.Label(measuring ? Localizer.Format("#LOC_KSPIE_WasteHeat_Measuring") : Forecast(fill, steady), leftAlignedLabel);

            // generators
            GeneratorLine();

            DoWindowFinal();

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        private string Forecast(double fill, bool steady)
        {
            if (steady)
                return Localizer.Format("#LOC_KSPIE_WasteHeat_Steady", (fill * 100).ToString("F0"));
            if (_balanceMW < 0)
                return Localizer.Format("#LOC_KSPIE_WasteHeat_CoolingDown");

            foreach (var mark in _marks)
            {
                if (fill >= mark.Fill) continue;
                var seconds = (mark.Fill * _capacity - _amount) / _balanceMW;
                return Localizer.Format("#LOC_KSPIE_WasteHeat_ToMark", (mark.Fill * 100).ToString("F0"), Duration(seconds), mark.Text);
            }
            return Localizer.Format("#LOC_KSPIE_WasteHeat_Rising");
        }

        /// <summary>The generators and the marks, from the vessel's parts: every 5 s of game time, not every frame.</summary>
        private void RefreshParts()
        {
            var now = Planetarium.GetUniversalTime();
            if (_generators != null && !double.IsNaN(_partsUT) && Math.Abs(now - _partsUT) <= 5)
                return;
            _partsUT = now;
            _generators = Vessel.FindPartModulesImplementing<FNGenerator>();

            _marks.Clear();
            if (Vessel.FindPartModulesImplementing<BeamedPowerTransmitter>().Count > 0)
                AddMark(0.90, "#LOC_KSPIE_WasteHeat_MarkTransmitters");
            if (Vessel.FindPartModulesImplementing<BeamedPowerReceiver>().Any(r => !r.isThermalReceiver))
                AddMark(0.95, "#LOC_KSPIE_WasteHeat_MarkReceivers");
            double throttle = double.NaN, shutdown = double.NaN;
            foreach (var reactor in Vessel.FindPartModulesImplementing<InterstellarReactor>())
            {
                if (reactor.hasOverheatEffects && reactor.overheatThreshHold > 0 && !(reactor.overheatThreshHold >= throttle))
                    throttle = reactor.overheatThreshHold;
                if (reactor.canShutdown && reactor.emergencyPowerShutdownFraction > 0 && !(reactor.emergencyPowerShutdownFraction >= shutdown))
                    shutdown = reactor.emergencyPowerShutdownFraction;
            }
            _reactorThrottle = throttle;
            if (!double.IsNaN(throttle) && throttle < 1)
                AddMark(throttle, "#LOC_KSPIE_WasteHeat_MarkReactors");
            if (!double.IsNaN(shutdown) && shutdown < 1)
                AddMark(shutdown, "#LOC_KSPIE_WasteHeat_MarkShutdown");
            _marks.Sort((a, b) => a.Fill.CompareTo(b.Fill));
        }

        private void AddMark(double fill, string key)
        {
            var text = Localizer.Format(key);
            var same = _marks.FirstOrDefault(m => Math.Abs(m.Fill - fill) < 0.005);
            if (same != null)
                same.Text += ", " + text;
            else
                _marks.Add(new Mark { Fill = fill, Text = text });
        }

        /// <summary>Yellow from the first mark, red from the reactors' throttle (or 95 %), whichever comes first.</summary>
        private bool Warn(double fill) => _marks.Count > 0 ? fill >= _marks[0].Fill : fill >= 0.9;

        private bool Danger(double fill) => fill >= Math.Min(0.95, double.IsNaN(_reactorThrottle) ? 0.95 : _reactorThrottle);

        private GUIStyle FillStyle(double fill) => Danger(fill) ? _redRight : Warn(fill) ? _yellowRight : _whiteRight;

        private Color BarColor(double fill) => Danger(fill) ? new Color(0.95f, 0.3f, 0.25f, 1f)
            : Warn(fill) ? new Color(0.95f, 0.8f, 0.25f, 1f)
            : new Color(0.3f, 0.75f, 0.35f, 1f);

        private void GeneratorLine()
        {
            double weight = 0, efficiency = 0, coldBath = 0;
            foreach (var generator in _generators)
            {
                if (generator == null || !generator.IsThermalMode || !generator.IsWorking) continue;
                weight += generator.MaxElectricMW;
                efficiency += generator.CurrentEfficiency * generator.MaxElectricMW;
                coldBath = generator.coldBathTempDisplay; // the vessel's average radiator temperature, as the generators see it
            }
            if (!(weight > 0)) return;

            GUILayout.Space(4);
            Row(Localizer.Format("#LOC_KSPIE_WasteHeat_Generators"),
                Localizer.Format("#LOC_KSPIE_WasteHeat_GeneratorsValue", (efficiency / weight * 100).ToString("F0"), coldBath.ToString("F0")),
                _whiteRight);
            GUILayout.Label(Localizer.Format("#LOC_KSPIE_WasteHeat_GeneratorsHint"), _hintLabel);
        }

        /// <summary>Sums by part title (negative entries count), keeps the positive sums, largest first; past MaxLines the rest is one "other" line.</summary>
        private static List<KeyValuePair<string, double>> Group(IEnumerable<KeyValuePair<object, double>> entries)
        {
            var sums = new Dictionary<string, double>();
            var counts = new Dictionary<string, int>();
            var parts = new Dictionary<string, HashSet<Part>>();
            foreach (var entry in entries)
            {
                if (double.IsNaN(entry.Value) || double.IsInfinity(entry.Value)) continue;
                var module = entry.Key as PartModule;
                var title = module != null && module.part != null && module.part.partInfo != null
                    ? module.part.partInfo.title
                    : (entry.Key as IResourceSupplier)?.getResourceManagerDisplayName() ?? (entry.Key as IResourceSuppliable)?.getResourceManagerDisplayName() ?? "?";
                sums.TryGetValue(title, out var sum);
                sums[title] = sum + entry.Value;
                if (module != null && module.part != null)
                {
                    if (!parts.TryGetValue(title, out var set)) parts[title] = set = new HashSet<Part>();
                    set.Add(module.part);
                }
            }
            foreach (var pair in parts) counts[pair.Key] = pair.Value.Count;

            var lines = sums
                .Where(s => s.Value > 5e-7)
                .OrderByDescending(s => s.Value)
                .Select(s => new KeyValuePair<string, double>(counts.TryGetValue(s.Key, out var n) && n > 1 ? n + " × " + s.Key : s.Key, s.Value))
                .ToList();
            if (lines.Count <= MaxLines) return lines;

            var rest = lines.Skip(MaxLines - 1).Sum(l => l.Value);
            lines = lines.Take(MaxLines - 1).ToList();
            lines.Add(new KeyValuePair<string, double>(Localizer.Format("#LOC_KSPIE_WasteHeat_Other"), rest));
            return lines;
        }

        private void Lines(List<KeyValuePair<string, double>> lines)
        {
            foreach (var line in lines)
                Row("   " + line.Key, PluginHelper.GetFormattedPowerString(line.Value), rightAlignedLabel, leftAlignedLabel);
        }

        private void Row(string label, string value, GUIStyle valueStyle, GUIStyle labelStyle = null)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, labelStyle ?? leftBoldLabel, GUILayout.ExpandWidth(true));
            GUILayout.Label(value, valueStyle, GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();
        }

        private static double Total(List<KeyValuePair<object, double>> entries)
        {
            double total = 0;
            foreach (var entry in entries)
                if (!double.IsNaN(entry.Value) && !double.IsInfinity(entry.Value))
                    total += entry.Value;
            return total;
        }

        private static void DrawBar(Rect rect, double fill, List<Mark> marks, Color color)
        {
            if (Event.current.type != EventType.Repaint) return;

            var oldColor = GUI.color;
            GUI.color = new Color(0.12f, 0.12f, 0.12f, 1f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);

            var filled = new Rect(rect.x, rect.y, rect.width * (float)Math.Max(0, Math.Min(1, fill)), rect.height);
            GUI.color = color;
            GUI.DrawTexture(filled, Texture2D.whiteTexture);

            GUI.color = Color.white;
            foreach (var mark in marks)
                GUI.DrawTexture(new Rect(rect.x + rect.width * (float)mark.Fill - 1, rect.y - 2, 2, rect.height + 4), Texture2D.whiteTexture);

            GUI.color = oldColor;
        }

        private void EnsureOwnStyles()
        {
            if (_hintLabel != null) return;

            _hintLabel = new GUIStyle(leftAlignedLabel) { fontSize = 10, wordWrap = true };
            _hintLabel.normal.textColor = new Color(0.7f, 0.7f, 0.7f);
            _whiteRight = new GUIStyle(rightAlignedLabel);
            _yellowRight = new GUIStyle(rightAlignedLabel);
            _yellowRight.normal.textColor = new Color(1f, 0.82f, 0.3f);
            _redRight = new GUIStyle(rightAlignedLabel);
            _redRight.normal.textColor = new Color(1f, 0.4f, 0.35f);
            _greenRight = new GUIStyle(rightAlignedLabel);
            _greenRight.normal.textColor = new Color(0.45f, 0.9f, 0.45f);
        }

        private static string Megajoules(double value) =>
            Math.Max(0, value).ToString("#,0", CultureInfo.InvariantCulture).Replace(",", " ");

        /// <summary>Seconds, minutes or hours of game time.</summary>
        private static string Duration(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
            if (seconds < 90) return Localizer.Format("#LOC_KSPIE_WasteHeat_Seconds", seconds.ToString("F0"));
            if (seconds < 90 * 60) return Localizer.Format("#LOC_KSPIE_WasteHeat_Minutes", (seconds / 60).ToString("F0"));
            var hours = seconds / 3600;
            return Localizer.Format("#LOC_KSPIE_WasteHeat_Hours", hours.ToString(hours < 10 ? "F1" : "F0"));
        }
    }
}
