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
#endregion

#region Default
        // Standard 2022/2023 layout (Omen 16 b1xxx/k0xxx, confirmed on i9-12900H/RTX 3070 Ti)
        public static readonly PlatformPreset Default = new PlatformPreset {
            ProductId        = "?",
            DisplayName      = "Default (Omen 16 2022/2023)",
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
#endregion

    }

}
