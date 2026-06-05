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

                // Fan-level plausibility at each layout's setpoint register. A real fan
                // level is small (krpm setpoint, 0–55); 0xFF means "BIOS auto / not this
                // layout" and anything larger than ~55 is not a level.
                byte fanLevel2022 = ec[PlatformPreset.Default.FanLevelReg0];     // 0x34
                byte fanLevel2023 = ec[PlatformPreset.Default2023.FanLevelReg0]; // 0x11
                bool level2022Plausible = fanLevel2022 <= 55;
                bool level2023Plausible = fanLevel2023 <= 55;

                // #37: a plausible, non-zero 16-bit LE tachometer at 0xB0 is a strong
                // positive signal but must not be *required* — fans can be idle (0 RPM)
                // at detection time, and some boards site the tach elsewhere. Treat 0 and
                // the 0xFFFF "register absent" sentinel as "no signal" rather than valid.
                bool rpm1Strong = rpm1 > 0 && rpm1 <= 7000;

                // Layout A — standard 2022 (Omen 16 b1xxx/k0xxx): FanLevel at 0x34/0x35.
                // Signal: CPUT at 0x57 returns a plausible CPU temperature (20–95 °C).
                // #37: the mandatory rpm1Valid requirement is dropped — CPUT in 20–95
                // already excludes the 2023+ 0xFF case, and demanding a live RPM made the
                // detector misfire to the 2023 layout on 2022 boards whose tach sits off
                // 0xB0 or whose fans were idle. A plausible 2022 fan level is required
                // instead so we don't claim the layout on a board that clearly isn't it.
                if(cput >= 20 && cput <= 95 && level2022Plausible)
                    return FromTemplate(productId, PlatformPreset.Default, "2022 layout");

                // Layout B — 2023+ (Victus/Omen 16 2023+): FanLevel at 0x11/0x12.
                // Signal: CPUT at 0x57 = 0xFF (overlaps firmware string data on these models)
                // AND EC[FanLevelReg0] holds a plausible fan level (0–55).
                // RPM is NOT checked here: some 2023+ models (e.g. 8BAB) have RPM registers
                // at a different address than 0xB0/0xB1, so rpm1Strong would be false even
                // when the layout is correct. cput==0xFF + fanLevel range is discriminating enough.
                if((cput == 0xFF || cput == 0x0F) && level2023Plausible)
                    return FromTemplate(productId, PlatformPreset.Default2023, "2023+ layout");

                // Layout C — CPUT ambiguous (neither a plausible temperature nor the 0xFF
                // sentinel, e.g. a board that parks 0x57 at an odd constant). #37: rather
                // than give up and force the global default, disambiguate purely on which
                // setpoint register holds a plausible fan level. The 2022 and 2023 level
                // registers (0x34 vs 0x11) are far apart, so it is rare for both to look
                // plausible at once; require an *exclusive* match to stay conservative, and
                // prefer the layout that also shows a live tach at 0xB0 when both qualify.
                if(level2022Plausible != level2023Plausible) {
                    if(level2022Plausible)
                        return FromTemplate(productId, PlatformPreset.Default, "2022 layout (fan-level inferred)");
                    return FromTemplate(productId, PlatformPreset.Default2023, "2023+ layout (fan-level inferred)");
                }
                if(level2022Plausible && level2023Plausible && rpm1Strong)
                    // Both ambiguous but a real RPM is present: the legacy 0xB0 tach is the
                    // 2022/early-Victus signature, so lean 2022.
                    return FromTemplate(productId, PlatformPreset.Default, "2022 layout (tach inferred)");

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
