  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using OmenMon.Hardware.Ec;
using OmenMon.Library;

namespace OmenMon.Hardware.Platform {

    // Safely detects EC register layout for unknown device models via read-only heuristics
    public static class AutoDetector {

        // Reads all 256 EC registers safely (read-only, no writes to hardware)
        private static byte[] DumpEc() {
            byte[] data = new byte[256];
            try {
                for(int r = 0; r < 256; r++)
                    data[r] = Hw.EcGetByte((byte) r);
            } catch { }
            return data;
        }

        // Heuristically matches the EC dump against the known standard 2022+ layout.
        // Returns a configured preset if the standard layout is detected, null otherwise.
        // Never writes to any EC register.
        public static PlatformPreset DetectHeuristic(string productId) {
            try {
                byte[] ec = DumpEc();

                byte cput    = ec[(byte) EmbeddedControllerData.Register.CPUT];
                byte rpm1Lo  = ec[(byte) EmbeddedControllerData.Register.RPM1];
                byte rpm1Hi  = ec[(byte) EmbeddedControllerData.Register.RPM2];
                int  rpm1    = (rpm1Hi << 8) | rpm1Lo;

                // CPUT at 0x57 must be a plausible CPU temperature and RPM1 at 0xB0 must be in fan range
                bool cputValid = cput >= 20 && cput <= 95;
                bool rpm1Valid = rpm1 >= 0 && rpm1 <= 7000;

                if(cputValid && rpm1Valid)
                    return new PlatformPreset {
                        ProductId        = productId,
                        DisplayName      = $"Auto-detected ({productId})",
                        FanLevelReg0     = PlatformPreset.Default.FanLevelReg0,
                        FanLevelReg1     = PlatformPreset.Default.FanLevelReg1,
                        FanRateReadReg0  = PlatformPreset.Default.FanRateReadReg0,
                        FanRateReadReg1  = PlatformPreset.Default.FanRateReadReg1,
                        FanRateWriteReg0 = PlatformPreset.Default.FanRateWriteReg0,
                        FanRateWriteReg1 = PlatformPreset.Default.FanRateWriteReg1,
                        FanSpeedReg0     = PlatformPreset.Default.FanSpeedReg0,
                        FanSpeedReg1     = PlatformPreset.Default.FanSpeedReg1,
                        CountdownReg     = PlatformPreset.Default.CountdownReg,
                        ManualReg        = PlatformPreset.Default.ManualReg,
                        ModeReg          = PlatformPreset.Default.ModeReg,
                        SwitchReg        = PlatformPreset.Default.SwitchReg
                    };
            } catch { }

            return null;
        }

    }

}
