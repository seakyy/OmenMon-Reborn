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

            // Confirm Boot maps to 1 (0x01) for Thermal Policy V1 boards (issue #230)
            Assert.Equal((byte)1, (byte)BiosData.FanMode.Boot);

            // Confirm Default maps to 48 (0x30)
            Assert.Equal((byte)48, (byte)BiosData.FanMode.Default);

            // Confirm Custom maps to 99 (0x63)
            Assert.Equal((byte)99, (byte)BiosData.FanMode.Custom);
        }

        [Theory]
        [InlineData("Eco", BiosData.FanMode.Eco)]
        [InlineData("Quiet", BiosData.FanMode.Quiet)]
        [InlineData("Boot", BiosData.FanMode.Boot)]
        [InlineData("Default", BiosData.FanMode.Default)]
        [InlineData("Performance", BiosData.FanMode.Performance)]
        [InlineData("Custom", BiosData.FanMode.Custom)]
        public void FanMode_EnumParse_SupportsOmenGamingHubModes(string name, BiosData.FanMode expected) {
            BiosData.FanMode parsed = (BiosData.FanMode)Enum.Parse(typeof(BiosData.FanMode), name, true);
            Assert.Equal(expected, parsed);
        }

        [Fact]
        public void FanTable_Capacity_Is42Levels() {
            var table = new BiosData.FanTable();
            Assert.Equal(42, table.Level.Length);
            Assert.Equal(128, System.Runtime.InteropServices.Marshal.SizeOf(typeof(BiosData.FanTable)));
        }
    }
}
