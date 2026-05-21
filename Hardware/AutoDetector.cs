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
        // delegates to Hw.EcDump to perform the dump atomically under a single lock
        private static byte[] DumpEc() {
            return Hw.EcDump();
        }

        // Heuristically matches the EC dump against known register layouts.
        // Tries two layouts in order; returns the first match or null if neither fits.
        // Never writes to any EC register.
        public static PlatformPreset DetectHeuristic(string productId) {
            try {
                byte[] ec = DumpEc();

                byte cput   = ec[(byte) EmbeddedControllerData.Register.CPUT]; // 0x57
                byte rpm1Lo = ec[(byte) EmbeddedControllerData.Register.RPM1]; // 0xB0
                byte rpm1Hi = ec[(byte) EmbeddedControllerData.Register.RPM2]; // 0xB1
                int  rpm1   = (rpm1Hi << 8) | rpm1Lo;
                bool rpm1Valid = rpm1 >= 0 && rpm1 <= 7000;

                // Layout A — standard 2022 (Omen 16 b1xxx/k0xxx): FanLevel at 0x34/0x35.
                // Signal: CPUT at 0x57 returns a plausible CPU temperature (20–95 °C).
                if(cput >= 20 && cput <= 95 && rpm1Valid)
                    return FromTemplate(productId, PlatformPreset.Default, "2022 layout");

                // Layout B — 2023+ (Victus/Omen 16 2023+): FanLevel at 0x11/0x12.
                // Signal: CPUT at 0x57 = 0xFF (overlaps firmware string data on these models)
                // AND EC[FanLevelReg0] holds a plausible fan level (0–55).
                // RPM is NOT checked here: some 2023+ models (e.g. 8BAB) have RPM registers
                // at a different address than 0xB0/0xB1, so rpm1Valid would be false even
                // when the layout is correct. cput==0xFF + fanLevel range is discriminating enough.
                if(cput == 0xFF) {
                    byte fanLevel = ec[PlatformPreset.Default2023.FanLevelReg0];
                    if(fanLevel <= 55)
                        return FromTemplate(productId, PlatformPreset.Default2023, "2023+ layout");
                }

            } catch { }

            return null;
        }

        // Copies a preset template, stamping in the detected productId and displayName
        private static PlatformPreset FromTemplate(string productId, PlatformPreset t, string label) {
            return new PlatformPreset {
                ProductId        = productId,
                DisplayName      = $"Auto-detected {label} ({productId})",
                FanLevelReg0     = t.FanLevelReg0,
                FanLevelReg1     = t.FanLevelReg1,
                FanRateReadReg0  = t.FanRateReadReg0,
                FanRateReadReg1  = t.FanRateReadReg1,
                FanRateWriteReg0 = t.FanRateWriteReg0,
                FanRateWriteReg1 = t.FanRateWriteReg1,
                FanSpeedReg0     = t.FanSpeedReg0,
                FanSpeedReg1     = t.FanSpeedReg1,
                CountdownReg     = t.CountdownReg,
                ManualReg        = t.ManualReg,
                ModeReg          = t.ModeReg,
                SwitchReg        = t.SwitchReg
            };
        }

    }

}
