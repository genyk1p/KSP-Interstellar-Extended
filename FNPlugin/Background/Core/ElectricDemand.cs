using System;

namespace FNPlugin.Background.Core
{
    /// <summary>
    /// Electric power an unloaded vessel needs from its KSPIE generators in one background step.
    /// Kerbalism runs the rest of the vessel (life support, probe cores, antennas, solar panels) in the same step
    /// and does not tell its consumption to a background module, so it is measured from the battery: what the
    /// vessel lost between two steps beyond what we produced is the rest of the vessel's net consumption
    /// (negative when solar panels produce more than the vessel uses).
    /// The battery is held just below full: on a full battery the excess is thrown away by Kerbalism and the
    /// battery change no longer shows the consumption.
    /// When Kerbalism clipped the battery at empty or full, the change only bounds the consumption from below or
    /// from above. The bounds are kept and the estimate bisects between them (or overshoots the one bound known so
    /// far, ×4 up / ×½ down), so that within a few steps a step ends inside the battery and measures the consumption exactly.
    /// That matters when the battery is small against consumption × step: otherwise the estimate would alternate
    /// between an empty battery (consumers starving) and a full one (fuel burnt for thrown-away electricity).
    /// Create a new instance whenever the vessel's snapshot changes (load, unload, game load): a measurement
    /// across a load would compare batteries of two different histories.
    /// </summary>
    public sealed class ElectricDemand
    {
        /// <summary>Battery level the generators hold, 0..1.</summary>
        public const double TargetLevel = 0.98;

        private bool _hasPrevious;
        private double _previousAmount;
        private double _previousProductionPerSecond;
        private double _previousElapsed;
        private bool _previousLimited;
        private double _lastRequest;
        private double _lowerBound = double.NegativeInfinity;
        private double _upperBound = double.PositiveInfinity;

        /// <summary>Net consumption of the rest of the vessel as last measured or estimated, EC/s (0 before the first step).</summary>
        public double ConsumptionPerSecond { get; private set; }

        /// <summary>
        /// ElectricCharge per second to produce in the coming step of <paramref name="elapsedSeconds"/>.
        /// <paramref name="amount"/> and <paramref name="capacity"/> are the vessel's ElectricCharge now (flow-enabled
        /// storage only, as Kerbalism counts it).
        /// </summary>
        public double Request(double amount, double capacity, double elapsedSeconds)
        {
            if (!IsFinite(amount) || !IsFinite(capacity) || !IsFinite(elapsedSeconds) || elapsedSeconds <= 0 || capacity <= 0)
                return 0;
            amount = Math.Max(0, Math.Min(capacity, amount));

            if (_hasPrevious && _previousElapsed > 0)
            {
                if (amount > 0 && amount < capacity * (1 - 1e-9))
                {
                    // amount = previous + (our production − rest of the vessel) × elapsed: an exact measurement;
                    // the old bounds may belong to a consumption that has changed since, they are dropped
                    ConsumptionPerSecond = (_previousAmount - amount) / _previousElapsed + _previousProductionPerSecond;
                    _lowerBound = double.NegativeInfinity;
                    _upperBound = double.PositiveInfinity;
                }
                else if (amount <= 0)
                {
                    // ran empty: the vessel used at least everything we made plus what was stored
                    _lowerBound = Math.Max(_lowerBound, _previousProductionPerSecond + _previousAmount / _previousElapsed);
                    if (_upperBound < _lowerBound) _upperBound = double.PositiveInfinity; // consumption rose since
                    if (_previousLimited)
                        // the generators gave all they could: more would not help, the bound is all there is to know
                        ConsumptionPerSecond = Math.Max(ConsumptionPerSecond, _lowerBound);
                    else
                        ConsumptionPerSecond = double.IsPositiveInfinity(_upperBound)
                            ? 4 * Math.Max(Math.Max(ConsumptionPerSecond, _lowerBound), 1e-6)
                            : Between(_lowerBound, _upperBound);
                }
                else
                {
                    // ran full: the vessel used at most what we made minus what filled the battery
                    _upperBound = Math.Min(_upperBound, _previousProductionPerSecond - (capacity - _previousAmount) / _previousElapsed);
                    if (_lowerBound > _upperBound) _lowerBound = double.NegativeInfinity; // consumption fell since
                    if (double.IsNegativeInfinity(_lowerBound))
                    {
                        var bounded = Math.Min(ConsumptionPerSecond, _upperBound);
                        ConsumptionPerSecond = bounded > 0 ? bounded / 2 : bounded;
                    }
                    else
                        ConsumptionPerSecond = Between(_lowerBound, _upperBound);
                }
                if (!IsFinite(ConsumptionPerSecond))
                    ConsumptionPerSecond = 0;
            }

            var refill = (TargetLevel * capacity - amount) / elapsedSeconds;
            _lastRequest = Math.Max(0, ConsumptionPerSecond + refill);
            return _lastRequest;
        }

        /// <summary>Remembers what was really produced this step (after the fuel and heat limits).</summary>
        public void Produced(double amountAtStart, double productionPerSecond, double elapsedSeconds)
        {
            if (!IsFinite(amountAtStart) || !IsFinite(productionPerSecond) || !IsFinite(elapsedSeconds) || elapsedSeconds <= 0)
            {
                _hasPrevious = false;
                return;
            }
            _hasPrevious = true;
            _previousAmount = Math.Max(0, amountAtStart);
            _previousProductionPerSecond = Math.Max(0, productionPerSecond);
            _previousElapsed = elapsedSeconds;
            _previousLimited = _previousProductionPerSecond < _lastRequest * (1 - 1e-9);
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        /// <summary>Middle of the bounds: geometric while they differ by orders of magnitude (fewer steps to close in), else arithmetic.</summary>
        private static double Between(double lower, double upper)
        {
            return lower > 0 && upper > 4 * lower ? Math.Sqrt(lower * upper) : (lower + upper) / 2;
        }
    }
}
