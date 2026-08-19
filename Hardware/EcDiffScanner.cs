  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using System.Collections.Generic;
using System.Linq;
using OmenMon.Hardware.Ec;

// This file is link-compiled into the .NET 8 test project, whose nullable
// context would otherwise flood the build with annotation warnings.
#nullable disable

namespace OmenMon.Hardware.Ec {

    // Heuristic scanner that compares EC dumps taken at different fan levels
    // and identifies the most likely RPM tachometer registers.
    //
    // Three patterns are recognised:
    //   * 16-bit little-endian RPM (classic Omen layout, e.g. RPM1 at 0xB0/0xB1)
    //   * Period-encoded 8-bit byte  (newer boards: higher value = slower fan)
    //   * Direct-multiplier 8-bit    (HP Victus 2024+ e.g. 8BD4: byte * 100 = RPM)
    //
    // Caller passes an ordered list of samples (low fan level → high fan level).
    // The scanner returns up to two candidates ranked by score (CPU first by offset).
    public static class EcDiffScanner {

#region Types
        // BiosLevelMirror is not produced by the EC scanner — it is reserved for boards
        // (e.g. 8C9C) where no EC offset reflects real fan tachometer output and the only
        // reliable RPM signal is the BIOS-reported fan level × multiplier. Fan.GetSpeed()
        // handles this mode by reading GetLevel() directly; ReadRpm() cannot service it.
        public enum Mode { LittleEndian16, PeriodEncoded8, DirectMultiplier8, BiosLevelMirror }

        public sealed class Candidate {
            public byte Offset;
            public Mode Mode;
            public int Score;
            public int[] Values;            // value at each sample step
            public string Description;
            // Value decoded from the return-to-idle verification dump, or -1 when
            // the caller supplied no verification dump (legacy single-sweep scan).
            public int VerifyValue = -1;

            public override string ToString() {
                return $"0x{Offset:X2} ({Mode}) score={Score} [{string.Join(",", Values)}]";
            }
        }

        public sealed class Sample {
            public int LevelPercent;        // commanded fan rate, 0..100
            public byte[] Memory;           // 256-byte EC dump
            public Sample(int level, byte[] mem) { LevelPercent = level; Memory = mem; }
        }

        public sealed class Result {
            public Candidate CpuFan;        // null if not found
            public Candidate GpuFan;        // null if not found
            public List<Candidate> All = new List<Candidate>();
            // Candidates that looked plausible during the one-way upward sweep but
            // failed the return-to-idle verification — registers that correlate with
            // time (tick counters, charge meters), not with fan load. Kept separate
            // so the calibration report can show them with their verification value.
            public List<Candidate> RejectedByVerification = new List<Candidate>();
            public bool VerificationUsed;
            public List<string> Notes = new List<string>();
            public bool IsPlausible => CpuFan != null;
        }
#endregion

#region Tunables
        // Bytes that change by less than this between idle and max are treated as
        // slow-movers (typically temperature sensors warming up over the test window).
        private const int SlowMoverDelta = 15;

        // Plausible RPM range for laptop fans (rpm).
        private const int RpmMin = 1500;
        private const int RpmMax = 8000;
        private const int RpmIdleCeiling = 1800;   // idle reading should be at or below this

        // Plausible period-byte range. Lower = faster spin.
        private const int PeriodMin = 0x10;
        private const int PeriodMax = 0xC8;
        private const int PeriodMinDelta = 8;

        // Plausible direct-multiplier byte range (Pattern C: byte * 100 = RPM).
        // 15 → 1500 RPM (low idle), 80 → 8000 RPM (top of fan envelope).
        private const int DirectMultByteMin = 2;
        private const int DirectMultByteMax = 80;
        private const int DirectMultIdleCeiling = 22;   // ≤ 2200 RPM at idle
        private const int DirectMultMaxFloor   = 25;   // ≥ 2500 RPM at full
        private const int DirectMultMinDelta   = 10;   // at least 1000 RPM swing

        // Registers known to be set by us during the test — exclude them as candidates.
        private static readonly HashSet<byte> WriteRegisters = new HashSet<byte> {
            0x2C, 0x2D,        // XSS1/XSS2 fan rate write (2022 layout)
            0x34, 0x35,        // SRP1/SRP2 fan level write
            0x3A, 0x3B,        // fan rate write (2023+ layout)
            0x62,              // OMCC manual fan toggle
            0x63,              // XFCD manual countdown
            0xEC,              // FFFF max-fan toggle
            0xF4               // SFAN fan-off toggle
        };
#endregion

#region Public API
        // Note emitted when no fan tachometer candidate survives the heuristics.
        // Exposed as a constant so the Auto-Calibration report can recognise this
        // specific note and suppress it for boards that have a built-in RPM mapping
        // (where a blank scan is expected, not a fault — issue #81).
        public const string NoteNoneDetected =
            "No plausible fan tachometer registers detected. The board may use a polling interval longer than the test window, or the EC may be locked by HP firmware. Please share the raw dumps from the report.";

        // <verificationDump> is an optional 256-byte EC dump taken AFTER the sweep,
        // with the fans commanded back to the lowest profile level and given time to
        // settle. A one-way upward sweep cannot distinguish a fan tachometer from a
        // register that merely increases with time (tick counters, charge meters):
        // both rise monotonically across the samples. The verification dump provides
        // the discriminator — a true tachometer has moved back toward its idle
        // reading by then, while a time-correlated register kept going in the sweep
        // direction. Pass null to skip (legacy behaviour, e.g. after a plateau abort
        // where the EC fan controller may no longer be responding to commands).
        public static Result Scan(IList<Sample> samples, byte[] verificationDump = null) {
            if(samples == null || samples.Count < 2)
                throw new ArgumentException("Need at least two samples (idle + max).");
            foreach(var s in samples)
                if(s.Memory == null || s.Memory.Length != 256)
                    throw new ArgumentException("Each sample must be a 256-byte EC dump.");
            if(verificationDump != null && verificationDump.Length != 256)
                throw new ArgumentException("The verification dump must be a 256-byte EC dump.");

            var result = new Result();
            var ordered = samples.OrderBy(s => s.LevelPercent).ToList();

            var hits = new List<Candidate>();
            ScanLittleEndian16(ordered, hits);
            ScanPeriodEncoded(ordered, hits);
            ScanDirectMultiplier(ordered, hits);

            // Resolve overlap: a 16-bit hit at r dominates 8-bit hits at r and r+1.
            hits = Deduplicate(hits);

            // Return-to-idle verification — must run before the top-two pick so a
            // rejected counter can't occupy a fan slot ahead of the real tachometer.
            if(verificationDump != null) {
                result.VerificationUsed = true;
                var survivors = new List<Candidate>(hits.Count);
                foreach(var h in hits) {
                    if(VerifyAgainstReturnDump(h, verificationDump))
                        survivors.Add(h);
                    else
                        result.RejectedByVerification.Add(h);
                }
                hits = survivors;
                if(result.RejectedByVerification.Count > 0)
                    result.Notes.Add(
                        result.RejectedByVerification.Count + " candidate(s) rejected by the return-to-idle verification pass: "
                        + "their value did not move back toward the idle reading after the fans were commanded back down — "
                        + "the signature of a register that correlates with time (timer/counter), not with fan load.");
            }

            hits.Sort((a, b) => b.Score.CompareTo(a.Score));
            result.All = hits;

            // Pick top two candidates — CPU is the lower offset of the two top scorers
            // (HP wires CPU fan tach below the GPU tach on every board we've seen).
            var top = hits.Take(2).OrderBy(c => c.Offset).ToList();
            if(top.Count >= 1) result.CpuFan = top[0];
            if(top.Count >= 2) result.GpuFan = top[1];

            if(top.Count == 0)
                result.Notes.Add(NoteNoneDetected);
            else if(top.Count == 1)
                result.Notes.Add("Only one fan tachometer detected — second fan may be physically absent (single-fan SKU) or wired through a different bus.");

            return result;
        }
#endregion

#region Heuristics
        private static void ScanLittleEndian16(List<Sample> samples, List<Candidate> hits) {
            int n = samples.Count;
            for(int r = 0; r < 255; r++) {
                if(WriteRegisters.Contains((byte) r) || WriteRegisters.Contains((byte) (r + 1)))
                    continue;

                var values = new int[n];
                for(int i = 0; i < n; i++)
                    values[i] = samples[i].Memory[r] | (samples[i].Memory[r + 1] << 8);

                int min = values.Min(), max = values.Max();
                if(max == min) continue;                       // static
                if(max < RpmMin || max > RpmMax) continue;     // not a plausible RPM
                if(values[0] > RpmIdleCeiling) continue;       // idle reading too high
                if(max - min < 500) continue;                  // delta too small for a tach

                int score = MonotonicScore(values, ascending: true, RpmInversionTolerance);
                if(score <= 0) continue;
                score += (max - min) / 100;                    // bigger swing → higher confidence

                hits.Add(new Candidate {
                    Offset = (byte) r,
                    Mode = Mode.LittleEndian16,
                    Score = score,
                    Values = values,
                    Description = $"16-bit LE RPM, idle={values[0]}, max={values[n - 1]}"
                });
            }
        }

        private static void ScanPeriodEncoded(List<Sample> samples, List<Candidate> hits) {
            int n = samples.Count;
            for(int r = 0; r < 256; r++) {
                if(WriteRegisters.Contains((byte) r)) continue;

                var values = new int[n];
                for(int i = 0; i < n; i++)
                    values[i] = samples[i].Memory[r];

                int min = values.Min(), max = values.Max();
                if(max - min < SlowMoverDelta) continue;       // static or slow-mover (likely a temp sensor)
                if(max - min < PeriodMinDelta) continue;
                if(min < PeriodMin || max > PeriodMax) continue;
                if(values[0] < values[n - 1]) continue;        // period-encoded must DECREASE with load

                int score = MonotonicScore(values, ascending: false, ByteInversionTolerance);
                if(score <= 0) continue;
                score += (max - min);

                hits.Add(new Candidate {
                    Offset = (byte) r,
                    Mode = Mode.PeriodEncoded8,
                    Score = score,
                    Values = values,
                    Description = $"period-encoded byte, idle=0x{values[0]:X2}, max=0x{values[n - 1]:X2}"
                });
            }
        }

        // Pattern C: 8-bit byte that rises monotonically with fan load and where
        // byte * 100 lands in the plausible RPM range. Seen on HP Victus 2024+ (e.g. 8BD4).
        private static void ScanDirectMultiplier(List<Sample> samples, List<Candidate> hits) {
            int n = samples.Count;
            for(int r = 0; r < 256; r++) {
                if(WriteRegisters.Contains((byte) r)) continue;

                var values = new int[n];
                for(int i = 0; i < n; i++)
                    values[i] = samples[i].Memory[r];

                int min = values.Min(), max = values.Max();
                if(max - min < DirectMultMinDelta) continue;
                if(min < DirectMultByteMin || max > DirectMultByteMax) continue;
                if(values[0] > DirectMultIdleCeiling) continue;     // idle reading too high
                if(values[n - 1] < DirectMultMaxFloor) continue;    // top step never reached real RPM
                if(values[n - 1] <= values[0]) continue;            // must rise with load

                int score = MonotonicScore(values, ascending: true, ByteInversionTolerance);
                if(score <= 0) continue;
                // Score the implied RPM swing in the same "hundreds of RPM" unit
                // as the 16-bit pass (which divides by 100). DirectMultiplier values
                // are bytes that decode to RPM via ×100, so (max-min) is already in
                // the right unit — no further scaling needed.
                score += (max - min);

                hits.Add(new Candidate {
                    Offset = (byte) r,
                    Mode = Mode.DirectMultiplier8,
                    Score = score,
                    Values = values,
                    Description = $"8-bit ×100 RPM, idle={values[0] * 100} rpm, max={values[n - 1] * 100} rpm"
                });
            }
        }

        // Returns a positive score for a sequence that moves consistently in the
        // expected direction. Allows one minor inversion to tolerate sensor jitter.
        //
        // The inversion tolerance must match the value scale: 16-bit RPM values can
        // jitter by tens of RPM between samples and still be the same monotonic
        // signal, but 8-bit byte encodings (period / direct-multiplier) move in
        // single-digit steps and a tolerance of 50 would silently swallow real
        // backward steps — admitting noise registers as plausible candidates.
        // Each caller passes the tolerance appropriate to its unit.
        private static int MonotonicScore(int[] values, bool ascending, int inversionTolerance) {
            int inversions = 0, runs = 0;
            for(int i = 1; i < values.Length; i++) {
                int d = values[i] - values[i - 1];
                if(ascending) {
                    if(d < -inversionTolerance) inversions++;
                    else if(d > 0) runs++;
                } else {
                    if(d > inversionTolerance) inversions++;
                    else if(d < 0) runs++;
                }
            }
            if(inversions > 1) return 0;
            return 10 * runs - 5 * inversions;
        }

        // Tolerances picked to absorb expected sensor jitter without masking real
        // backward steps. RpmTolerance is in raw RPM; ByteTolerance is in raw byte
        // units (so for DirectMultiplier8 it lets ±200 RPM slip through).
        private const int RpmInversionTolerance = 50;
        private const int ByteInversionTolerance = 2;

        // Decides whether a candidate survives the return-to-idle verification.
        // Rejection requires the verification value to still be at or beyond the
        // top-of-sweep reading (within the per-mode jitter tolerance): a counter
        // always lands there because it kept advancing after the sweep, while even
        // a fan that is slow to spin down has dropped at least somewhat after the
        // settle window. Records the decoded value on the candidate either way so
        // the calibration report can show the evidence.
        private static bool VerifyAgainstReturnDump(Candidate c, byte[] dump) {
            int top = c.Values[c.Values.Length - 1];
            switch(c.Mode) {
                case Mode.LittleEndian16:
                    c.VerifyValue = dump[c.Offset] | (dump[c.Offset + 1] << 8);
                    return c.VerifyValue < top - RpmInversionTolerance;
                case Mode.DirectMultiplier8:
                    c.VerifyValue = dump[c.Offset];
                    return c.VerifyValue < top - ByteInversionTolerance;
                case Mode.PeriodEncoded8:
                    // Period encoding falls with load, so after returning to idle the
                    // value must have risen back above the top-of-sweep (lowest) reading.
                    c.VerifyValue = dump[c.Offset];
                    return c.VerifyValue > top + ByteInversionTolerance;
                default:
                    // BiosLevelMirror is never produced by the scanner (see Mode docs).
                    return true;
            }
        }

        private static List<Candidate> Deduplicate(List<Candidate> hits) {
            var le16 = hits.Where(h => h.Mode == Mode.LittleEndian16).ToList();
            var occupied = new HashSet<byte>();
            foreach(var h in le16) {
                occupied.Add(h.Offset);
                occupied.Add((byte) (h.Offset + 1));
            }
            return hits
                .Where(h => h.Mode == Mode.LittleEndian16 || !occupied.Contains(h.Offset))
                .ToList();
        }
#endregion

    }

}
