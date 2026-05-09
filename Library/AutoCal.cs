  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using OmenMon.Hardware.Ec;

namespace OmenMon.Library {

    // Live overrides discovered by the Auto-Calibration Wizard.
    //
    // Lives in OmenMon.Library so the hot read-path in Hardware/Fan.cs can consult
    // it without taking a dependency on the App layer. Populated by CliOpCalibration
    // on a successful scan; read by Fan.GetSpeed() on every refresh tick.
    //
    // Threading: the calibration worker writes overrides from a thread-pool task
    // while Fan.GetSpeed() reads them from the GUI refresh tick. To prevent torn
    // reads (offset from one update + mode from another) the override is published
    // as a single immutable Override reference and swapped atomically. Readers
    // observe either the previous value or the new value, never a mix.
    public static class AutoCal {

        // Immutable per-fan override snapshot — published as a single reference so
        // (offset, mode, multiplier) is always observed as a coherent triple.
        public sealed class Override {
            public readonly byte Reg;
            public readonly EcDiffScanner.Mode Mode;
            public readonly int Multiplier; // 0 = use mode default (e.g. 100 for DirectMultiplier8)
            public Override(byte reg, EcDiffScanner.Mode mode, int multiplier = 0) {
                Reg = reg; Mode = mode; Multiplier = multiplier;
            }
        }

        // Reference assignment is atomic on every CLR target; volatile keeps the
        // store/load from being reordered. No lock needed for read-mostly use.
        private static volatile Override _cpu;
        private static volatile Override _gpu;

        public static bool HasCpu => _cpu != null;
        public static bool HasGpu => _gpu != null;

        public static bool TryGetCpu(out byte reg, out EcDiffScanner.Mode mode, out int multiplier) {
            var snap = _cpu;
            if(snap == null) { reg = 0; mode = default(EcDiffScanner.Mode); multiplier = 0; return false; }
            reg = snap.Reg; mode = snap.Mode; multiplier = snap.Multiplier; return true;
        }

        public static bool TryGetGpu(out byte reg, out EcDiffScanner.Mode mode, out int multiplier) {
            var snap = _gpu;
            if(snap == null) { reg = 0; mode = default(EcDiffScanner.Mode); multiplier = 0; return false; }
            reg = snap.Reg; mode = snap.Mode; multiplier = snap.Multiplier; return true;
        }

        // Publishes a new triple atomically. Pass null to clear that fan's override.
        // Wizard / Load / Prime / Clear all funnel through here so concurrent readers
        // never observe a half-written register/mode/multiplier triple.
        public static void SetCpu(byte? reg, EcDiffScanner.Mode? mode, int multiplier = 0) {
            _cpu = (reg.HasValue && mode.HasValue) ? new Override(reg.Value, mode.Value, multiplier) : null;
        }

        public static void SetGpu(byte? reg, EcDiffScanner.Mode? mode, int multiplier = 0) {
            _gpu = (reg.HasValue && mode.HasValue) ? new Override(reg.Value, mode.Value, multiplier) : null;
        }

        // Reads the current RPM using the override at <offset> in <mode>.
        // <multiplier> overrides the per-mode default for DirectMultiplier8 (0 = use default of 100).
        // Returns -1 if any of the inputs are not valid.
        public static int ReadRpm(byte offset, EcDiffScanner.Mode mode, int multiplier = 0) {
            switch(mode) {
                case EcDiffScanner.Mode.LittleEndian16:
                    return Hw.EcGetWord(offset);
                case EcDiffScanner.Mode.PeriodEncoded8: {
                    int period = Hw.EcGetByte(offset);
                    // RPM = 60 / (period_ticks * tick_seconds). Tick base differs per board;
                    // an empirical 60_000_000 / (period * 256) lands close to observed values
                    // on the boards we've tested. Caller can replace if a better mapping is known.
                    // Return -1 (not 0) so callers can distinguish a bad/uninitialised
                    // read from a genuinely stopped fan and fall back to the preset.
                    if(period <= 0) return -1;
                    return 60_000_000 / (period * 256);
                }
                case EcDiffScanner.Mode.DirectMultiplier8:
                    return Hw.EcGetByte(offset) * (multiplier > 0 ? multiplier : 100);
                default:
                    return -1;
            }
        }

        public static void Clear() {
            _cpu = null;
            _gpu = null;
        }

#region Persistence
        // Path of the sidecar file CliOpCalibration writes after a successful scan.
        // Kept separate from OmenMon.xml so the wizard never has to rewrite the main
        // config and so the user can delete it to undo a calibration without losing
        // anything else. Exposed so the writer (CliOpCalibration) and the reader
        // (this class) can never disagree about the filename.
        public const string SidecarFileName = "OmenMon-AutoCal.xml";

        // Full sidecar path next to the running executable. Single source of truth
        // for both the load and save paths.
        public static string SidecarPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SidecarFileName);

        // Loads any persisted overrides written by a previous calibration run.
        // Returns true if at least one override was restored.
        //
        // currentProductId is the WMI baseboard product of the running machine. The
        // sidecar must declare a matching ProductId or it is rejected — otherwise an
        // 8BD4 calibration carried over to a different laptop (USB stick, OneDrive
        // sync, swapped install) would happily apply 8BD4 register offsets to a
        // 2022 Omen and read garbage. A board mismatch deletes the file so the next
        // wizard run starts clean.
        //
        // Called from Platform construction so the registers discovered last session
        // take effect on the next launch without the user having to re-run the wizard.
        public static bool Load(string currentProductId) {
            string path = SidecarPath;
            if(!File.Exists(path)) return false;

            try {
                // Harden the parser against XXE / external-entity expansion. The
                // sidecar is technically user-writable on disk and we have no reason
                // to honour DTDs or fetch external resources from it.
                var settings = new XmlReaderSettings {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                };
                var doc = new XmlDocument { XmlResolver = null };
                using(var stream = File.OpenRead(path))
                using(var reader = XmlReader.Create(stream, settings)) {
                    doc.Load(reader);
                }

                string fileProductId = doc.DocumentElement?.GetAttribute("ProductId");

                // A transient WMI failure on startup turns the current product ID into
                // "?" (see Settings.GetProduct()). Treat that as "unknown, skip this run"
                // rather than "foreign machine, nuke the sidecar" — otherwise one bad
                // WMI read would permanently erase an otherwise valid calibration.
                bool currentIsUnknown =
                    string.IsNullOrEmpty(currentProductId)
                    || currentProductId == "?";

                if(currentIsUnknown) {
                    return false;
                }

                if(string.IsNullOrEmpty(fileProductId)
                    || !string.Equals(fileProductId, currentProductId, StringComparison.OrdinalIgnoreCase)) {
                    // File belongs to a different machine — discard it so we don't
                    // poison this board's read-path with foreign offsets.
                    try { File.Delete(path); } catch { }
                    return false;
                }

                XmlNode cpu = doc.SelectSingleNode("/AutoCalibration/CpuFan");
                XmlNode gpu = doc.SelectSingleNode("/AutoCalibration/GpuFan");
                // Hoist the out-vars so the sanity check below can read cReg / gReg
                // without tripping C#'s definite-assignment analysis (it can't tie the
                // haveCpu / haveGpu booleans to TryParseEntry's success path).
                byte cReg = 0;
                EcDiffScanner.Mode cMode = EcDiffScanner.Mode.LittleEndian16;
                byte gReg = 0;
                EcDiffScanner.Mode gMode = EcDiffScanner.Mode.LittleEndian16;
                bool haveCpu = cpu != null && TryParseEntry(cpu, out cReg, out cMode);
                bool haveGpu = gpu != null && TryParseEntry(gpu, out gReg, out gMode);

                // Sanity check: across every shipped board layout the CPU and GPU fan
                // tachometer offsets sit within a few bytes of each other (0 for shared
                // single-fan SKUs, 2 for the canonical 0xB0/0xB2 pair, 3 for 8BD4's
                // 0x11/0x14, never more). When the persisted offsets are >16 bytes apart
                // *and* a native model entry already exists for this board, that's a
                // strong signal the wizard misfired on a transient EC-read glitch
                // (issue #26: 8DD0 sidecar locked onto 0xB3 / 0xD9 = 38 bytes apart from
                // a 1-byte-shifted dump, which then displayed as "50k RPM" until the
                // sidecar was deleted). Discard the file in that case so the next launch
                // falls through to the native database / Prime() mapping. Boards without
                // a native entry still trust the sidecar — they have nothing else to fall
                // back to.
                bool hasNative = !currentIsUnknown
                    && Config.Models != null
                    && Config.Models.ContainsKey(currentProductId);
                if(hasNative && haveCpu && haveGpu) {
                    int distance = Math.Abs((int) cReg - (int) gReg);
                    if(distance > 16) {
                        try { File.Delete(path); } catch { }
                        return false;
                    }
                }

                bool anyApplied = false;
                if(haveCpu) {
                    SetCpu(cReg, cMode);
                    anyApplied = true;
                }
                if(haveGpu) {
                    SetGpu(gReg, gMode);
                    anyApplied = true;
                }

                return anyApplied;
            } catch {
                // Corrupt sidecar — better to fall through to the model preset / known-board
                // mapping than to crash on startup. The next successful run will overwrite it.
                return false;
            }
        }

        private static bool TryParseEntry(XmlNode node, out byte reg, out EcDiffScanner.Mode mode) {
            reg = 0; mode = EcDiffScanner.Mode.LittleEndian16;
            string offset = node.Attributes?["offset"]?.Value;
            string modeStr = node.Attributes?["mode"]?.Value;
            if(string.IsNullOrEmpty(offset) || string.IsNullOrEmpty(modeStr)) return false;

            // Accept either "0xB0" or "176"
            int parsed;
            if(offset.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
                if(!int.TryParse(offset.Substring(2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed)) return false;
            } else {
                if(!int.TryParse(offset, out parsed)) return false;
            }
            if(parsed < 0 || parsed > 0xFF) return false;

            // Reject numeric mode strings ("123") and require an exact named-member
            // match. Enum.TryParse otherwise accepts any int that fits the underlying
            // type, which would set HasCpu/HasGpu and block Prime() from filling in
            // known-board mappings while ReadRpm() silently fell back to -1.
            modeStr = modeStr.Trim();
            if(modeStr.Length == 0) return false;
            if(char.IsDigit(modeStr[0]) || modeStr[0] == '-' || modeStr[0] == '+')
                return false;
            if(!Enum.TryParse(modeStr, ignoreCase: true, out EcDiffScanner.Mode parsedMode))
                return false;
            if(!Enum.IsDefined(typeof(EcDiffScanner.Mode), parsedMode))
                return false;

            reg = (byte) parsed;
            mode = parsedMode;
            return true;
        }
#endregion

#region Known Boards
        // Mappings the project has confirmed by hand (probe data + user-reported behaviour).
        // Saves owners of these boards from having to run the wizard themselves and gives
        // the heuristic a known-good reference to compare against.
        //
        // Format: ProductId → (CpuOffset, CpuMode, CpuMul, GpuOffset, GpuMode, GpuMul)
        // CpuMul/GpuMul: override the per-mode default multiplier (0 = use default).
        private struct Mapping {
            public byte CpuReg; public EcDiffScanner.Mode CpuMode; public int CpuMul;
            public byte GpuReg; public EcDiffScanner.Mode GpuMode; public int GpuMul;
        }

        private static readonly Dictionary<string, Mapping> KnownBoards =
            new Dictionary<string, Mapping>(System.StringComparer.OrdinalIgnoreCase) {

            // HP Victus 16-S0053NT (8BD4) — probe data shows the legacy RPM tach offsets
            // 0xB0/0xB2 are repurposed as temperature sensors. The actual fan tachometer
            // tracks the level register (EC[0x11] / EC[0x14]) and reads as byte ×100 RPM.
            ["8BD4"] = new Mapping {
                CpuReg = 0x11, CpuMode = EcDiffScanner.Mode.DirectMultiplier8, CpuMul = 0,
                GpuReg = 0x14, GpuMode = EcDiffScanner.Mode.DirectMultiplier8, GpuMul = 0,
            },

            // HP Victus 16 (8C9C, 1034NF, 2024) — single shared tachometer at EC[0xF1].
            // Byte value × 60 = RPM (e.g. 0x5C = 92 → 5520 RPM at full load, confirmed via
            // probe dumps across 0 / 30 / 70 / 100 % fan profiles). Both CPU and GPU fans
            // report through the same register; 0xB0/0xB2 are temperature sensors, not RPM.
            ["8C9C"] = new Mapping {
                CpuReg = 0xF1, CpuMode = EcDiffScanner.Mode.DirectMultiplier8, CpuMul = 60,
                GpuReg = 0xF1, GpuMode = EcDiffScanner.Mode.DirectMultiplier8, GpuMul = 60,
            },
        };

        // Pre-populates AutoCal overrides for a known board, *per fan*. Called from
        // Platform construction after Load(). A user-restored CPU override outranks
        // the built-in CPU mapping, but if the GPU side wasn't restored — say, the
        // wizard found only one fan, or the sidecar was hand-edited — the built-in
        // GPU mapping still fills in. Without per-fan resolution, boards like 8BD4
        // (which depend on Prime() for non-legacy tach registers) would fall back to
        // the placeholder FanSpeedReg* in OmenMon.xml and report garbage on the
        // un-restored fan.
        public static void Prime(string productId) {
            if(string.IsNullOrEmpty(productId)) return;
            if(!KnownBoards.TryGetValue(productId, out var m)) return;
            if(!HasCpu) SetCpu(m.CpuReg, m.CpuMode, m.CpuMul);
            if(!HasGpu) SetGpu(m.GpuReg, m.GpuMode, m.GpuMul);
        }
#endregion

    }

}
