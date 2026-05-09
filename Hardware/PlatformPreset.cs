  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using OmenMon.Hardware.Ec;

namespace OmenMon.Hardware.Platform {

    // Defines the EC register layout for a specific device model
    public class PlatformPreset {

#region Data
        public string ProductId;
        public string DisplayName;

        // Fan level (setpoint) registers [krpm] — written to set target speed
        public byte FanLevelReg0;    // SRP1 — CPU fan
        public byte FanLevelReg1;    // SRP2 — GPU fan

        // Fan rate read registers [%] — current duty cycle readback
        public byte FanRateReadReg0;    // XGS1
        public byte FanRateReadReg1;    // XGS2

        // Fan rate write registers [%] — direct percent override
        public byte FanRateWriteReg0;   // XSS1
        public byte FanRateWriteReg1;   // XSS2

        // Fan speed registers (word, little-endian) [rpm]
        public byte FanSpeedReg0;    // RPM1 — CPU fan low byte
        public byte FanSpeedReg1;    // RPM3 — GPU fan low byte

        // Control registers
        public byte CountdownReg;    // XFCD — manual-mode auto-reset countdown [s]
        public byte ManualReg;       // OMCC — manual fan control enable
        public byte ModeReg;         // HPCM — performance mode preset
        public byte SwitchReg;       // SFAN — fan off switch

        // Per-model manual-mode trigger values. The standard OMCC register at 0x62 responds
        // to FanManual.On / .Off (0x06 / 0x00); some 2023+ HP boards ignore the legacy OMCC
        // bit and gate manual fan control on a different register/value pair entirely
        // (e.g. 8BBE — Victus 16 R0053NT — uses EC[0x06] with 0x08 to enable, 0x48 to release;
        // see OmenMon.xml for the full per-board reasoning). Defaults reproduce the legacy
        // enum pair so existing entries keep working without a config change.
        public byte ManualValueOn  = (byte) PlatformData.FanManual.On;
        public byte ManualValueOff = (byte) PlatformData.FanManual.Off;

        // Per-model temperature-sensor overrides. When non-zero, Platform.InitTemperature()
        // remaps the named "CPUT" / "GPTM" sensors to read from these EC offsets instead of
        // the global defaults defined in OmenMon.xml's <Temperature> block. Required for
        // 2024+ HP boards that moved the real CPU/GPU temperature sensors away from the
        // legacy EC[0x57]/EC[0xB7] addresses (e.g. 8C9C — Victus 16-1034NF — exposes the
        // real CPU temp at EC[0xB0] and the GPU hotspot at EC[0xB4]; the legacy addresses
        // either return 0xFF or a heavily-smoothed BIOS package average that lags 10 °C+
        // behind the real die temperature). Default 0 = "no override, use the global
        // <Temperature> config" — leaves every existing model entry unaffected.
        public byte TempCpuReg = 0;
        public byte TempGpuReg = 0;
#endregion

#region Default
        // Standard 2022 layout (Omen 16 b1xxx/k0xxx) — FanLevel at 0x34/0x35 (SRP1/SRP2)
        public static readonly PlatformPreset Default = new PlatformPreset {
            ProductId        = "?",
            DisplayName      = "Default (Omen 16 2022)",
            FanLevelReg0     = (byte) EmbeddedControllerData.Register.SRP1,
            FanLevelReg1     = (byte) EmbeddedControllerData.Register.SRP2,
            FanRateReadReg0  = (byte) EmbeddedControllerData.Register.XGS1,
            FanRateReadReg1  = (byte) EmbeddedControllerData.Register.XGS2,
            FanRateWriteReg0 = (byte) EmbeddedControllerData.Register.XSS1,
            FanRateWriteReg1 = (byte) EmbeddedControllerData.Register.XSS2,
            FanSpeedReg0     = (byte) EmbeddedControllerData.Register.RPM1,
            FanSpeedReg1     = (byte) EmbeddedControllerData.Register.RPM3,
            CountdownReg     = (byte) EmbeddedControllerData.Register.XFCD,
            ManualReg        = (byte) EmbeddedControllerData.Register.OMCC,
            ModeReg          = (byte) EmbeddedControllerData.Register.HPCM,
            SwitchReg        = (byte) EmbeddedControllerData.Register.SFAN
        };

        // 2023+ layout (Victus 16 R0xxx, Omen 16 2023+) — FanLevel at 0x11/0x12, CPUT=0xFF
        // Confirmed on 8BBE, 8BAB, 8C9C via EC probe data (EC[0x11] matches BIOS GetFanLevel)
        // FanSpeedReg0/1 default to 0xB0/0xB2; some models (e.g. 8BAB) use different RPM
        // registers — those must be overridden per-model in OmenMon.xml via FanSpeedReg0/1.
        public static readonly PlatformPreset Default2023 = new PlatformPreset {
            ProductId        = "?",
            DisplayName      = "Default 2023+ (Victus/Omen 2023+)",
            FanLevelReg0     = 17,   // 0x11 — confirmed on 8BBE, 8BAB, 8C9C
            FanLevelReg1     = 18,   // 0x12
            FanRateReadReg0  = (byte) EmbeddedControllerData.Register.XGS1,
            FanRateReadReg1  = (byte) EmbeddedControllerData.Register.XGS2,
            FanRateWriteReg0 = 58,   // 0x3A — confirmed in all manually added 2023+ models
            FanRateWriteReg1 = 59,   // 0x3B
            FanSpeedReg0     = (byte) EmbeddedControllerData.Register.RPM1,
            FanSpeedReg1     = (byte) EmbeddedControllerData.Register.RPM3,
            CountdownReg     = (byte) EmbeddedControllerData.Register.XFCD,
            ManualReg        = (byte) EmbeddedControllerData.Register.OMCC,
            ModeReg          = (byte) EmbeddedControllerData.Register.HPCM,
            SwitchReg        = (byte) EmbeddedControllerData.Register.SFAN
        };
#endregion

    }

}
