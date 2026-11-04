using System.Collections.Generic;
using FNPlugin.Background.Core;
using NUnit.Framework;

namespace Background.Tests
{
    /// <summary>Beamed power received from one transmitter (BeamedPowerReceiver.CalculateInputPower), step A3.</summary>
    public class BeamReceptionTests
    {
        private static readonly List<BeamBand> Bands = new List<BeamBand>
        {
            new BeamBand { MinimumWavelength = 1e-9, MaximumWavelength = 1e-7, ElectricEfficiency = 0.5, ThermalEfficiency = 0.9 },
            new BeamBand { MinimumWavelength = 1e-3, MaximumWavelength = 1e-1, ElectricEfficiency = 0.8, ThermalEfficiency = 0.95 }
        };

        private static List<IncomingBeam> Beam(double wavelength, double mw) => new List<IncomingBeam> { new IncomingBeam { Wavelength = wavelength, PowerMW = mw } };

        [Test]
        public void ElectricReceptionAfterRouteAndBand()
        {
            // 2239 MW beam at 13 nm, route 10 %, plenty left: 223.9 MW at the input, 50 % of it received
            var intake = BeamReception.FromTransmitter(Beam(1.3e-8, 2239), 0.1, double.PositiveInfinity, 5000, Bands, false, false, 1000);
            Assert.That(intake.ReceivedMW, Is.EqualTo(111.95).Within(1e-9));
            Assert.That(intake.InputMW, Is.EqualTo(223.9).Within(1e-9));
            Assert.That(intake.ConversionHeatMW, Is.EqualTo(111.95 * 0.5).Within(1e-9));
        }

        [Test]
        public void ThermalModeUsesThermalEfficiencyAndLittleHeat()
        {
            var intake = BeamReception.FromTransmitter(Beam(1.3e-8, 1000), 1, double.PositiveInfinity, 5000, Bands, false, true, 10000);
            Assert.That(intake.ReceivedMW, Is.EqualTo(900).Within(1e-9));
            Assert.That(intake.ConversionHeatMW, Is.EqualTo(0.05 * 900 * 0.1).Within(1e-9));
        }

        [Test]
        public void LimitedByWhatIsLeftTheRelaysAndTheReceiver()
        {
            Assert.That(BeamReception.FromTransmitter(Beam(1.3e-8, 1000), 1, double.PositiveInfinity, 300, Bands, false, false, 1e9).ReceivedMW,
                Is.EqualTo(150).Within(1e-9), "other receivers took the rest");
            Assert.That(BeamReception.FromTransmitter(Beam(1.3e-8, 1000), 1, 200, 1000, Bands, false, false, 1e9).ReceivedMW,
                Is.EqualTo(100).Within(1e-9), "a relay carries 200 MW");
            Assert.That(BeamReception.FromTransmitter(Beam(1.3e-8, 1000), 1, double.PositiveInfinity, 1000, Bands, false, false, 70).ReceivedMW,
                Is.EqualTo(70).Within(1e-9), "the receiver's own capacity");
        }

        [Test]
        public void CappedReceiverCountsOnlyWhatItTook()
        {
            var capped = BeamReception.FromTransmitter(Beam(1.3e-8, 1000), 1, double.PositiveInfinity, 1000, Bands, false, false, 70);
            Assert.That(capped.ReceivedMW, Is.EqualTo(70).Within(1e-9));
            Assert.That(capped.InputMW, Is.EqualTo(140).Within(1e-9), "70 MW at 50 %: 140 MW of the beam, not 1000");
            Assert.That(capped.ConversionHeatMW, Is.EqualTo(35).Within(1e-9));
            var relay = BeamReception.FromTransmitter(Beam(1.3e-8, 1000), 1, 200, 1000, Bands, false, false, 1e9);
            Assert.That(relay.InputMW, Is.EqualTo(200).Within(1e-9));
        }

        [TestCase(80.0, 1000.0)]          // a percentage passed unconverted: never more than the beam brings
        [TestCase(-0.5, 0.0)]
        [TestCase(double.NaN, 0.0)]
        public void BandEfficiencyOutsideZeroToOneIsClamped(double efficiency, double received)
        {
            var band = new List<BeamBand> { new BeamBand { MinimumWavelength = 1e-9, MaximumWavelength = 1e-7, ElectricEfficiency = efficiency } };
            var intake = BeamReception.FromTransmitter(Beam(1.3e-8, 1000), 1, double.PositiveInfinity, 1000, band, false, false, 1e9);
            Assert.That(intake.ReceivedMW, Is.EqualTo(received).Within(1e-9));
            Assert.That(double.IsNaN(intake.ConversionHeatMW) || double.IsNaN(intake.InputMW), Is.False);
        }

        [Test]
        public void NoBandForTheWavelengthReceivesNothing()
        {
            var intake = BeamReception.FromTransmitter(Beam(5e-5, 1000), 1, double.PositiveInfinity, 1000, Bands, false, false, 1e9);
            Assert.That(intake.ReceivedMW, Is.EqualTo(0));
            Assert.That(intake.InputMW, Is.EqualTo(0));
        }

        [Test]
        public void SwitchableBandTakesEveryWavelength()
        {
            var single = new List<BeamBand> { Bands[1] };
            var intake = BeamReception.FromTransmitter(Beam(5e-5, 1000), 1, double.PositiveInfinity, 1000, single, true, false, 1e9);
            Assert.That(intake.ReceivedMW, Is.EqualTo(800).Within(1e-9));
        }

        [Test]
        public void SeveralWavelengthsShareTheTransmittersPower()
        {
            var beams = new List<IncomingBeam>
            {
                new IncomingBeam { Wavelength = 1.3e-8, PowerMW = 600 },
                new IncomingBeam { Wavelength = 1e-2, PowerMW = 600 }
            };
            // only 1000 MW left of the transmitter: 600 at 50 %, then 400 at 80 %
            var intake = BeamReception.FromTransmitter(beams, 1, double.PositiveInfinity, 1000, Bands, false, false, 1e9);
            Assert.That(intake.ReceivedMW, Is.EqualTo(300 + 320).Within(1e-9));
            Assert.That(intake.InputMW, Is.EqualTo(1000).Within(1e-9));
        }

        [Test]
        public void BadInputsGiveNothing()
        {
            Assert.That(BeamReception.FromTransmitter(null, 1, double.PositiveInfinity, 1000, Bands, false, false, 1e9).ReceivedMW, Is.EqualTo(0));
            Assert.That(BeamReception.FromTransmitter(Beam(1.3e-8, 1000), 1, double.PositiveInfinity, 1000, new List<BeamBand>(), false, false, 1e9).ReceivedMW, Is.EqualTo(0));
            Assert.That(BeamReception.FromTransmitter(Beam(1.3e-8, double.NaN), 1, double.PositiveInfinity, 1000, Bands, false, false, 1e9).ReceivedMW, Is.EqualTo(0));
            Assert.That(BeamReception.FromTransmitter(Beam(1.3e-8, 1000), double.NaN, double.PositiveInfinity, 1000, Bands, false, false, 1e9).ReceivedMW, Is.EqualTo(0));
            Assert.That(BeamReception.FromTransmitter(Beam(1.3e-8, 1000), 1, double.NaN, 1000, Bands, false, false, 1e9).ReceivedMW, Is.EqualTo(0));
            Assert.That(BeamReception.FromTransmitter(Beam(1.3e-8, 1000), 1, double.PositiveInfinity, -5, Bands, false, false, 1e9).ReceivedMW, Is.EqualTo(0));
        }

        [Test]
        public void ExtraHeatEntersTheHeatBalance()
        {
            var spec = new VesselSpec();
            spec.Reactors.Add(new ReactorSpec { ThermalMW = 100, HotBathK = 3000 });
            spec.Generators.Add(new GeneratorSpec { Reactor = 0, Mode = GeneratorMode.Thermal, MaxEfficiency = 0.5 });
            spec.Radiators.Add(new RadiatorSpec { StefanArea = 5.670374419e-8 * 2000 * 1e-6, MaxTemperatureK = 1500, EnvironmentMaxK = 1500, Mass = 5 });
            var plain = SteadyStateSolver.Solve(spec, 20, 20, 0, 0);
            var heated = SteadyStateSolver.Solve(spec, 20, 20, 0, 30);
            Assert.That(heated.WasteHeatMW - plain.WasteHeatMW, Is.GreaterThan(30 * 0.99));
            Assert.That(heated.WasteHeatFill, Is.GreaterThan(plain.WasteHeatFill));
            Assert.That(SteadyStateSolver.Solve(spec, 20, 20, 0, double.NaN).WasteHeatMW, Is.EqualTo(plain.WasteHeatMW).Within(1e-12));
        }
    }
}
