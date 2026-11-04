using System;
using System.Collections.Generic;

namespace FNPlugin.Background.Core
{
    /// <summary>A receiver bandwidth converter: the wavelengths it takes and its efficiencies (0..1) in electric and thermal mode.</summary>
    public sealed class BeamBand
    {
        public double MinimumWavelength;
        public double MaximumWavelength;
        public double ElectricEfficiency;
        public double ThermalEfficiency;
    }

    /// <summary>One wavelength a transmitter sends: power at the transmitter, MW (KSPIE keeps kW: nuclear + solar power / 1000).</summary>
    public sealed class IncomingBeam
    {
        public double Wavelength;
        public double PowerMW;
    }

    /// <summary>What a receiver takes from one transmitter.</summary>
    public struct BeamIntake
    {
        /// <summary>Power after the receiver's band efficiency, MW (KSPIE satPower, summed into total_beamed_power).</summary>
        public double ReceivedMW;
        /// <summary>
        /// Power at the receiver's input, before its efficiency, MW (KSPIE AvailablePower, shown in the receiver's window).
        /// What counts against the transmitter for the other receivers is <see cref="ReceivedMW"/>, as KSPIE counts it.
        /// </summary>
        public double InputMW;
        /// <summary>Waste heat of the conversion at full use, MW.</summary>
        public double ConversionHeatMW;
    }

    /// <summary>
    /// Beamed power received from one transmitter, as BeamedPowerReceiver.CalculateInputPower computes it (step A3):
    /// for every wavelength the transmitter sends, the receiver's band for it, the route efficiency, the relays'
    /// capacity and what is left of the transmitter's power after the other receivers.
    /// </summary>
    public static class BeamReception
    {
        /// <param name="beams">The transmitter's wavelengths (SupportedTransmitWavelengths).</param>
        /// <param name="routeEfficiency">Efficiency of the best route (distance, facing, atmosphere; KSPIE MicrowaveRoute).</param>
        /// <param name="relayCapacityMW">Smallest relay capacity on the route, MW; +∞ for a direct link.</param>
        /// <param name="remainingMW">Transmitter power left for this receiver, MW (network capacity − other receivers' use).</param>
        /// <param name="bands">The receiver's bands; with <paramref name="singleBand"/> the first is used for every wavelength.</param>
        /// <param name="thermalMode">Thermal or electric efficiencies.</param>
        /// <param name="beamCapacityMW">What the receiver takes of one beam at most, MW (min(capacity × consumption %, capacity × heat efficiency)).</param>
        public static BeamIntake FromTransmitter(IList<IncomingBeam> beams, double routeEfficiency, double relayCapacityMW, double remainingMW,
            IList<BeamBand> bands, bool singleBand, bool thermalMode, double beamCapacityMW)
        {
            var intake = new BeamIntake();
            if (beams == null || bands == null || bands.Count == 0) return intake;
            var remaining = Clean(remainingMW);
            var route = Clean(routeEfficiency);
            var relayCapacity = double.IsNaN(relayCapacityMW) || relayCapacityMW < 0 ? 0 : relayCapacityMW;
            var cap = Clean(beamCapacityMW);

            foreach (var beam in beams)
            {
                if (beam == null) continue;
                var band = singleBand ? bands[0] : Find(bands, beam.Wavelength);
                if (band == null) continue;

                var maximumRoutePower = Clean(beam.PowerMW) * route;
                var remainingInBeam = Math.Min(remaining, maximumRoutePower);
                if (!(remainingInBeam > 0)) continue;

                var beamNetworkPower = Math.Min(remainingInBeam, relayCapacity);
                remaining = Math.Max(0, remaining - beamNetworkPower);

                var efficiency = Clamp01(thermalMode ? band.ThermalEfficiency : band.ElectricEfficiency);
                var received = Math.Min(cap, beamNetworkPower * efficiency);
                intake.ReceivedMW += received;
                intake.ConversionHeatMW += (thermalMode ? 0.05 : 1) * received * (1 - efficiency);
                intake.InputMW += received > 0 && efficiency > 0 ? received / efficiency : 0;
            }
            return intake;
        }

        private static BeamBand Find(IList<BeamBand> bands, double wavelength)
        {
            foreach (var band in bands)
                if (band != null && wavelength >= band.MinimumWavelength && wavelength <= band.MaximumWavelength)
                    return band;
            return null;
        }

        private static double Clean(double value) => double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? 0 : value;

        private static double Clamp01(double value) => double.IsNaN(value) ? 0 : value < 0 ? 0 : value > 1 ? 1 : value;
    }
}
