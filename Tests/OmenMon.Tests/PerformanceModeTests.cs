  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using OmenMon.Hardware.Bios;
using Xunit;

namespace OmenMon.Tests {

    public class PerformanceModeTests {

        [Fact]
        public void FanMode_IncludesOmenGamingHubPerformanceOptions() {
            // Confirm Eco and Quiet enum entries exist and map to Quiet thermal mode (0x03)
            Assert.Equal((byte)3, (byte)BiosData.FanMode.Eco);
            Assert.Equal((byte)3, (byte)BiosData.FanMode.Quiet);
            Assert.Equal((byte)3, (byte)BiosData.FanMode.LegacyQuiet);

            // Confirm Default maps to 48 (0x30)
            Assert.Equal((byte)48, (byte)BiosData.FanMode.Default);

            // Confirm Performance maps to 49 (0x31)
            Assert.Equal((byte)49, (byte)BiosData.FanMode.Performance);
        }

        [Theory]
        [InlineData("Eco", BiosData.FanMode.Eco)]
        [InlineData("Quiet", BiosData.FanMode.Quiet)]
        [InlineData("Default", BiosData.FanMode.Default)]
        [InlineData("Performance", BiosData.FanMode.Performance)]
        public void FanMode_EnumParse_SupportsOmenGamingHubModes(string name, BiosData.FanMode expected) {
            BiosData.FanMode parsed = (BiosData.FanMode)Enum.Parse(typeof(BiosData.FanMode), name, true);
            Assert.Equal(expected, parsed);
        }
    }
}
