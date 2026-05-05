  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System.Collections.Generic;
using OmenMon.Hardware.Ec;

namespace OmenMon.Library {

    // Live overrides discovered by the Auto-Calibration Wizard.
    //
    // Lives in OmenMon.Library so the hot read-path in Hardware/Fan.cs can consult
    // it without taking a dependency on the App layer. Populated by CliOpCalibration
    // on a successful scan; read by Fan.GetSpeed() on every refresh tick.
    public static class AutoCal {

        public static byte? CpuFanReg;
        public static EcDiffScanner.Mode? CpuFanMode;

        public static byte? GpuFanReg;
        public static EcDiffScanner.Mode? GpuFanMode;

        public static bool HasCpu => CpuFanReg.HasValue && CpuFanMode.HasValue;
        public static bool HasGpu => GpuFanReg.HasValue && GpuFanMode.HasValue;

        // Reads the current RPM using the override at <offset> in <mode>.
        // Returns -1 if any of the inputs are not valid.
        public static int ReadRpm(byte offset, EcDiffScanner.Mode mode) {
            switch(mode) {
                case EcDiffScanner.Mode.LittleEndian16:
                    return Hw.EcGetWord(offset);
                case EcDiffScanner.Mode.PeriodEncoded8: {
                    int period = Hw.EcGetByte(offset);
                    // RPM = 60 / (period_ticks * tick_seconds). Tick base differs per board;
                    // an empirical 60_000_000 / (period * 256) lands close to observed values
                    // on the boards we've tested. Caller can replace if a better mapping is known.
                    if(period <= 0) return 0;
                    return 60_000_000 / (period * 256);
                }
                case EcDiffScanner.Mode.DirectMultiplier8:
                    return Hw.EcGetByte(offset) * 100;
                default:
                    return -1;
            }
        }

        public static void Clear() {
            CpuFanReg = null; CpuFanMode = null;
            GpuFanReg = null; GpuFanMode = null;
        }

#region Known Boards
        // Mappings the project has confirmed by hand (probe data + user-reported behaviour).
        // Saves owners of these boards from having to run the wizard themselves and gives
        // the heuristic a known-good reference to compare against.
        //
        // Format: ProductId → (CpuOffset, CpuMode, GpuOffset, GpuMode)
        private struct Mapping {
            public byte CpuReg; public EcDiffScanner.Mode CpuMode;
            public byte GpuReg; public EcDiffScanner.Mode GpuMode;
        }

        private static readonly Dictionary<string, Mapping> KnownBoards =
            new Dictionary<string, Mapping>(System.StringComparer.OrdinalIgnoreCase) {

            // HP Victus 16-S0053NT (8BD4) — probe data shows the legacy RPM tach offsets
            // 0xB0/0xB2 are repurposed as temperature sensors. The actual fan tachometer
            // tracks the level register (EC[0x11] / EC[0x14]) and reads as byte ×100 RPM.
            ["8BD4"] = new Mapping {
                CpuReg = 0x11, CpuMode = EcDiffScanner.Mode.DirectMultiplier8,
                GpuReg = 0x14, GpuMode = EcDiffScanner.Mode.DirectMultiplier8
            },
        };

        // Pre-populates the AutoCal override for a known board. Called from Platform
        // construction; a no-op if the user has already overridden via the wizard or
        // restored from the sidecar XML.
        public static void Prime(string productId) {
            if(string.IsNullOrEmpty(productId)) return;
            if(HasCpu || HasGpu) return;  // user/wizard wins
            if(!KnownBoards.TryGetValue(productId, out var m)) return;
            CpuFanReg = m.CpuReg; CpuFanMode = m.CpuMode;
            GpuFanReg = m.GpuReg; GpuFanMode = m.GpuMode;
        }
#endregion

    }

}
