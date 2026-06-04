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
                case EcDiffScanner.Mode.BiosLevelMirror:
                    // Cannot be serviced from the EC alone — Fan.GetSpeed() handles this
                    // mode by reading the BIOS-reported fan level directly.
                    return -1;
                default:
                    return -1;
            }
        }

        public static void Clear() {
            _cpu = null;
            _gpu = null;
        }

        // Mirror-collision detector (issue #83, reported by @MartinSalg818 on 8BAD).
        // Returns true when this board has a native preset that drives the CPU and GPU
        // fans from DISTINCT tachometer registers, yet the currently-published overrides
        // make both fans resolve to the SAME register — the condition under which the two
        // fan readouts mirror each other.
        //
        // The classic trigger is a single-fan calibration scan: EcDiffScanner hands its
        // sole candidate to CpuFan, and if that candidate is actually the GPU's tach
        // register the CPU override then points at it while the GPU side, having no
        // override, falls back to the preset's (same) register. Both fans then read the
        // GPU's RPM.
        //
        // Compares the *effective* register each fan will read: the published override if
        // present, otherwise the preset register Fan.GetSpeed() falls back to. Three classes
        // of board are exempt because a shared register is legitimate there, not a misfire:
        //   * boards with no native entry (no ground truth to compare against),
        //   * boards whose preset already shares one register between both fans (single-fan
        //     SKUs such as 8BB3, FanSpeedReg0 == FanSpeedReg1), and
        //   * BiosLevelMirror overrides, whose 0 register is a sentinel rather than a real
        //     EC offset (e.g. 8C9C).
        internal static bool CollidesWithNativePreset(string productId) {
            if(string.IsNullOrEmpty(productId) || Config.Models == null) return false;
            if(!Config.Models.TryGetValue(productId, out var preset)) return false;
            if(preset.FanSpeedReg0 == preset.FanSpeedReg1) return false;

            bool haveCpu = TryGetCpu(out byte cReg, out EcDiffScanner.Mode cMode, out _);
            bool haveGpu = TryGetGpu(out byte gReg, out EcDiffScanner.Mode gMode, out _);
            if(haveCpu && cMode == EcDiffScanner.Mode.BiosLevelMirror) return false;
            if(haveGpu && gMode == EcDiffScanner.Mode.BiosLevelMirror) return false;

            byte effectiveCpu = haveCpu ? cReg : preset.FanSpeedReg0;
            byte effectiveGpu = haveGpu ? gReg : preset.FanSpeedReg1;
            return effectiveCpu == effectiveGpu;
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

                // Override-conflict check: when this product is in KnownBoards
                // (hand-verified ground truth — added precisely because the
                // wizard heuristic gets the layout wrong on this hardware) and
                // the persisted sidecar disagrees with that ground truth,
                // discard the sidecar instead of carrying a known-broken
                // mapping forward across upgrades. Example: 8C9C's previous
                // released mapping was (0xF1, DirectMultiplier8 ×60) and is
                // now (BiosLevelMirror); without this guard, users who ran
                // the wizard on the old version would keep getting bogus RPM
                // (~120 RPM at audible MAX) until they manually deleted the
                // sidecar file (issue #28 follow-up).
                if(KnownBoards.TryGetValue(currentProductId, out Mapping known)) {
                    bool cpuMismatch = haveCpu
                        && (cReg != known.CpuReg || cMode != known.CpuMode);
                    bool gpuMismatch = haveGpu
                        && (gReg != known.GpuReg || gMode != known.GpuMode);
                    if(cpuMismatch || gpuMismatch) {
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

                // Mirror-collision self-heal (issue #83): if the overrides we just
                // restored collapse a distinct-register board's two fans onto the same
                // tachometer — a single-fan scan that locked onto the other fan's
                // register — discard the sidecar and fall back to the verified native
                // preset so the CPU and GPU readouts stop mirroring. Evaluated after
                // publishing so it weighs the exact effective registers Fan.GetSpeed()
                // will use; Platform calls Prime() next to refill any fan still on the
                // preset.
                if(anyApplied && CollidesWithNativePreset(currentProductId)) {
                    Clear();
                    try { File.Delete(path); } catch { }
                    return false;
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
            // Single physical fan: the GPU fan row should mirror the CPU reading rather
            // than fall back to the (wrongly-decoded) preset FanSpeedReg1 (#81).
            public bool SingleFan;
        }

        private static readonly Dictionary<string, Mapping> KnownBoards =
            new Dictionary<string, Mapping>(System.StringComparer.OrdinalIgnoreCase) {

            // HP Omen (8600, 2019) — the auto-calibration heuristic locks onto
            // period-encoded-looking registers (0x06 / 0xF1) when run on default
            // mode. Under stress test/game load, the correct 16-bit LE tachometers
            // are revealed at 0x45 / 0x47 (CPU max ~6700, GPU max ~10000 RPM).
            ["8600"] = new Mapping {
                CpuReg = 0x45, CpuMode = EcDiffScanner.Mode.LittleEndian16, CpuMul = 0,
                GpuReg = 0x47, GpuMode = EcDiffScanner.Mode.LittleEndian16, GpuMul = 0,
            },

            // HP Omen (8D87, 2025) — 16-bit LE RPM tachometers at 0x70 / 0x9F (CPU max ~6000, GPU max ~5550 RPM).
            // Standard 2023+ register layout.
            ["8D87"] = new Mapping {
                CpuReg = 0x70, CpuMode = EcDiffScanner.Mode.LittleEndian16, CpuMul = 0,
                GpuReg = 0x9F, GpuMode = EcDiffScanner.Mode.LittleEndian16, GpuMul = 0,
            },

            // HP Victus 16-S0053NT (8BD4) — probe data shows the legacy RPM tach offsets
            // 0xB0/0xB2 are repurposed as temperature sensors. The actual fan tachometer
            // tracks the level register (EC[0x11] / EC[0x14]) and reads as byte ×100 RPM.
            ["8BD4"] = new Mapping {
                CpuReg = 0x11, CpuMode = EcDiffScanner.Mode.DirectMultiplier8, CpuMul = 0,
                GpuReg = 0x14, GpuMode = EcDiffScanner.Mode.DirectMultiplier8, GpuMul = 0,
            },

            // HP Omen (8DD0, 2025) — the auto-calibration heuristic locks onto two
            // unrelated period-encoded-looking offsets (0x02 / 0x88) on this board
            // and produces nonsensical RPM (issue #33: "50k RPM"). Manual probes
            // across idle / medium / max load consistently show real 16-bit LE
            // tachometers at the canonical 0xB0 / 0xB2 pair (CPU ~3.2 kRPM idle,
            // ~5.4 kRPM max; GPU ~3.3 / ~5.2). Pin Prime() to that layout so the
            // built-in mapping outranks the auto-cal's bad guess after the bogus
            // sidecar has been rejected by the >16-byte distance sanity check.
            ["8DD0"] = new Mapping {
                CpuReg = 0xB0, CpuMode = EcDiffScanner.Mode.LittleEndian16, CpuMul = 0,
                GpuReg = 0xB2, GpuMode = EcDiffScanner.Mode.LittleEndian16, GpuMul = 0,
            },

            // HP Victus 16 (8C9C, 1034NF, 2024) — no reliable EC tachometer.
            // The previous EC[0xF1] × 60 mapping only tracked during OmenMon-driven
            // calibration sweeps (where 0xF1 happened to mirror the wizard's commanded
            // step). In real-world operation — fans driven by the OEM (Omen Gaming Hub
            // / BIOS thermal loop) — EC[0xF1] reads as ~0x02 even at audible MAX, which
            // produced obviously wrong RPM (issue #28: OGH visibly at 5800 RPM, OmenMon
            // showed ~120 RPM). Switch to BiosLevelMirror: the BIOS-reported fan level
            // already returns 58/61 ≈ 5800/6100 RPM at OGH MAX, matching the OGH display
            // exactly. CpuMul/GpuMul = 100 because the BIOS level is in units of 100 RPM.
            ["8C9C"] = new Mapping {
                CpuReg = 0, CpuMode = EcDiffScanner.Mode.BiosLevelMirror, CpuMul = 100,
                GpuReg = 0, GpuMode = EcDiffScanner.Mode.BiosLevelMirror, GpuMul = 100,
            },

            // HP Victus 16 (8BB3, 2024, AMD) — issue #64.
            // Single-fan SKU. CPU fan register at 0xF1 (DirectMultiplier8 mode).
            ["8BB3"] = new Mapping {
                CpuReg = 0xF1, CpuMode = EcDiffScanner.Mode.DirectMultiplier8, CpuMul = 0,
                GpuReg = 0, GpuMode = default(EcDiffScanner.Mode), GpuMul = 0,
                SingleFan = true,
            },

            // HP Omen Max 16 (8D41, 2025) — issue #87, reported by @Keith1341.
            // The wizard's live-RPM column read blank because Fan.GetSpeed() fell back
            // to the default 0xB0/0xB2 preset (wrong for this 2025 "Omen Max" layout),
            // but the EcDiffScanner candidates and the raw EC dumps agree on 16-bit LE
            // tachometers at 0x5C (CPU) / 0x9F (GPU). Decoded from the report's 100% step:
            // EC[0x5C..0x5D] = 80 16 → 0x1680 = 5760 RPM (CPU) and EC[0x9F..0xA0] = 85 19
            // → 0x1985 = 6533 RPM (GPU), matching the reported maxima exactly; the 0% step
            // reads 0/0. Read-only mapping only — no fan-control registers are committed
            // for this new layout (a wrong ManualReg/ModeReg could lock the EC), so this
            // fixes the RPM display without risking the 100%-fan freeze class of bug.
            ["8D41"] = new Mapping {
                CpuReg = 0x5C, CpuMode = EcDiffScanner.Mode.LittleEndian16, CpuMul = 0,
                GpuReg = 0x9F, GpuMode = EcDiffScanner.Mode.LittleEndian16, GpuMul = 0,
            },

            // HP OMEN 17 ck1000nw (8A18, 2022) — issue #84, reported by @xenon205.
            // The native <Model> entry already pins FanSpeedReg0/1 = 0xB0/0xB2, but a
            // stale or foreign AutoCal sidecar (or a single-fan rescan) could override
            // that with a wrong register and reintroduce the post-calibration "garbage
            // RPM → fan lock / hibernation" symptom the user reported. Pinning the
            // confirmed 16-bit LE tachometers here too means Load()'s collision
            // self-heal always has a verified built-in mapping to fall back to, and a
            // wizard-free install reads correct RPM out of the box. Decoded from the
            // user's report (0/30/70/100% sweep): CPU 0/1476/3218/3422, GPU
            // 0/1448/3115/3363 — matching the native preset exactly.
            ["8A18"] = new Mapping {
                CpuReg = 0xB0, CpuMode = EcDiffScanner.Mode.LittleEndian16, CpuMul = 0,
                GpuReg = 0xB2, GpuMode = EcDiffScanner.Mode.LittleEndian16, GpuMul = 0,
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
            if(!HasCpu && (m.CpuReg != 0 || m.CpuMode == EcDiffScanner.Mode.BiosLevelMirror)) SetCpu(m.CpuReg, m.CpuMode, m.CpuMul);
            if(!HasGpu && (m.GpuReg != 0 || m.GpuMode == EcDiffScanner.Mode.BiosLevelMirror)) SetGpu(m.GpuReg, m.GpuMode, m.GpuMul);

            // #81 (reported by @jpcaldwell30 on 8BB3): single-fan SKUs expose one physical
            // fan, but the GUI/preset still carries a GPU fan whose FanSpeedReg1 points at
            // the same register decoded the wrong way (a DirectMultiplier8 byte read as an
            // LE16 word), so the GPU row showed garbage. When the board is flagged single-fan
            // and the GPU side has no override of its own, mirror the resolved CPU mapping
            // onto the GPU so both rows report the one real fan's RPM. CollidesWithNativePreset()
            // exempts single-fan presets (FanSpeedReg0 == FanSpeedReg1), so this mirror is not
            // mistaken for a #83 mis-detection.
            if(m.SingleFan && !HasGpu && TryGetCpu(out byte cReg, out var cMode, out int cMul))
                SetGpu(cReg, cMode, cMul);
        }

        // Reports whether a board has a built-in RPM mapping in KnownBoards.
        // Used by the Auto-Calibration report to reassure the user when the
        // EcDiffScanner heuristic detects nothing on boards that don't expose a
        // 16-bit LE tachometer (e.g. 8BB3 — single-fan DirectMultiplier8 at 0xF1,
        // 8C9C — BiosLevelMirror): RPM is already handled natively, so the scan
        // returning no candidate is expected, not a malfunction (issue #81).
        public static bool IsKnownBoard(string productId) {
            return !string.IsNullOrEmpty(productId) && KnownBoards.ContainsKey(productId);
        }
#endregion

    }

}
