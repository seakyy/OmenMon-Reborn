  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using OmenMon.Hardware.Ec;

namespace OmenMon.Tests {

    // Regression tests for the auto-calibration RPM-encoding heuristics, run on
    // synthetic EC dumps modelled on real boards from the issue tracker. The
    // scanner is compiled directly into this project (see the csproj link) so
    // these tests exercise the exact code the wizard ships.
    public class EcDiffScannerTests {

        // Builds a 256-byte dump with the given byte values planted at offsets.
        private static byte[] Dump(params (byte Offset, byte Value)[] writes) {
            var d = new byte[256];
            foreach(var w in writes)
                d[w.Offset] = w.Value;
            return d;
        }

        // Plants a 16-bit little-endian value.
        private static void PutWord(byte[] d, byte offset, int value) {
            d[offset] = (byte) (value & 0xFF);
            d[offset + 1] = (byte) ((value >> 8) & 0xFF);
        }

        private static List<EcDiffScanner.Sample> SweepLe16(byte cpuReg, int[] cpuRpm, byte? gpuReg = null, int[]? gpuRpm = null) {
            int[] levels = { 0, 30, 70, 100 };
            var samples = new List<EcDiffScanner.Sample>();
            for(int i = 0; i < levels.Length; i++) {
                var d = new byte[256];
                PutWord(d, cpuReg, cpuRpm[i]);
                if(gpuReg.HasValue && gpuRpm != null)
                    PutWord(d, gpuReg.Value, gpuRpm[i]);
                samples.Add(new EcDiffScanner.Sample(levels[i], d));
            }
            return samples;
        }

        [Fact]
        public void DetectsClassicLe16Pair() {
            // Canonical 0xB0/0xB2 layout (2022 Omen, 8DD0 after the KnownBoards fix).
            var samples = SweepLe16(0xB0, new[] { 1200, 2400, 3600, 4800 },
                                    0xB2, new[] { 1100, 2300, 3500, 4700 });
            var result = EcDiffScanner.Scan(samples);

            Assert.NotNull(result.CpuFan);
            Assert.NotNull(result.GpuFan);
            Assert.Equal(0xB0, result.CpuFan!.Offset);
            Assert.Equal(EcDiffScanner.Mode.LittleEndian16, result.CpuFan.Mode);
            Assert.Equal(0xB2, result.GpuFan!.Offset);
        }

        [Fact]
        public void DetectsDirectMultiplier8_8BD4Signature() {
            // 8BD4 (Victus 16 2024): tach mirrors the level registers 0x11/0x14 as byte ×100.
            int[] levels = { 0, 30, 70, 100 };
            byte[][] cpuGpu = {
                new byte[] { 14, 13 },
                new byte[] { 25, 24 },
                new byte[] { 41, 39 },
                new byte[] { 55, 52 },
            };
            var samples = new List<EcDiffScanner.Sample>();
            for(int i = 0; i < levels.Length; i++)
                samples.Add(new EcDiffScanner.Sample(levels[i],
                    Dump((0x11, cpuGpu[i][0]), (0x14, cpuGpu[i][1]))));

            var result = EcDiffScanner.Scan(samples);

            Assert.NotNull(result.CpuFan);
            Assert.NotNull(result.GpuFan);
            Assert.Equal(0x11, result.CpuFan!.Offset);   // CPU = lower offset of the top two
            Assert.Equal(0x14, result.GpuFan!.Offset);
            Assert.Equal(EcDiffScanner.Mode.DirectMultiplier8, result.CpuFan.Mode);
            Assert.Equal(EcDiffScanner.Mode.DirectMultiplier8, result.GpuFan.Mode);
        }

        [Fact]
        public void DetectsPeriodEncoded8_8C77Signature() {
            // 8C77-style period byte: higher = slower, falls as the fans spin up.
            int[] levels = { 0, 30, 70, 100 };
            byte[] period = { 0xB0, 0x90, 0x60, 0x30 };
            var samples = new List<EcDiffScanner.Sample>();
            for(int i = 0; i < levels.Length; i++)
                samples.Add(new EcDiffScanner.Sample(levels[i], Dump((0xD2, period[i]))));

            var result = EcDiffScanner.Scan(samples);

            Assert.NotNull(result.CpuFan);
            Assert.Equal(0xD2, result.CpuFan!.Offset);
            Assert.Equal(EcDiffScanner.Mode.PeriodEncoded8, result.CpuFan.Mode);
        }

        [Fact]
        public void SingleCandidateLandsOnCpuWithSingleFanNote() {
            // 8BB3-style single-fan SKU: one DirectMultiplier8 tach at 0xF1 (issue #81).
            int[] levels = { 0, 30, 70, 100 };
            byte[] tach = { 15, 28, 41, 55 };
            var samples = new List<EcDiffScanner.Sample>();
            for(int i = 0; i < levels.Length; i++)
                samples.Add(new EcDiffScanner.Sample(levels[i], Dump((0xF1, tach[i]))));

            var result = EcDiffScanner.Scan(samples);

            Assert.NotNull(result.CpuFan);
            Assert.Null(result.GpuFan);
            Assert.Equal(0xF1, result.CpuFan!.Offset);
            Assert.Contains(result.Notes, n => n.Contains("Only one fan tachometer"));
        }

        [Fact]
        public void IgnoresSlowMovingTemperatureSensor() {
            // A temperature sensor warming 40 → 52 °C across the sweep must not be
            // mistaken for a tachometer (delta below the slow-mover threshold).
            int[] levels = { 0, 30, 70, 100 };
            byte[] temp = { 40, 44, 48, 52 };
            var samples = new List<EcDiffScanner.Sample>();
            for(int i = 0; i < levels.Length; i++)
                samples.Add(new EcDiffScanner.Sample(levels[i], Dump((0x57, temp[i]))));

            var result = EcDiffScanner.Scan(samples);

            Assert.Null(result.CpuFan);
            Assert.Empty(result.All);
        }

        [Fact]
        public void IgnoresRegistersTheWizardItselfWrites() {
            // A perfect tach-like pattern at 0x2C (XSS1 — written by the sweep) must
            // be excluded, otherwise the scanner calibrates against its own output.
            int[] levels = { 0, 30, 70, 100 };
            byte[] tach = { 15, 28, 41, 55 };
            var samples = new List<EcDiffScanner.Sample>();
            for(int i = 0; i < levels.Length; i++)
                samples.Add(new EcDiffScanner.Sample(levels[i], Dump((0x2C, tach[i]))));

            var result = EcDiffScanner.Scan(samples);

            Assert.Empty(result.All);
        }

        [Fact]
        public void UpwardSweepAloneCannotRejectCounters_DocumentedLimitation() {
            // A register that increases with TIME (tick counter, charge meter) is
            // indistinguishable from a tachometer in an upward-only sweep — this
            // test documents the false positive the verification pass exists for.
            int[] levels = { 0, 30, 70, 100 };
            byte[] counter = { 5, 15, 25, 35 };
            var samples = new List<EcDiffScanner.Sample>();
            for(int i = 0; i < levels.Length; i++)
                samples.Add(new EcDiffScanner.Sample(levels[i], Dump((0x90, counter[i]))));

            var result = EcDiffScanner.Scan(samples);

            Assert.NotNull(result.CpuFan);          // wrongly detected without verification
            Assert.Equal(0x90, result.CpuFan!.Offset);
            Assert.False(result.VerificationUsed);
        }

        [Fact]
        public void VerificationRejectsCounter() {
            // Same counter as above, but with a return-to-idle dump where the value
            // kept climbing (45 > top-of-sweep 35) — the counter signature.
            int[] levels = { 0, 30, 70, 100 };
            byte[] counter = { 5, 15, 25, 35 };
            var samples = new List<EcDiffScanner.Sample>();
            for(int i = 0; i < levels.Length; i++)
                samples.Add(new EcDiffScanner.Sample(levels[i], Dump((0x90, counter[i]))));
            var verifyDump = Dump((0x90, 45));

            var result = EcDiffScanner.Scan(samples, verifyDump);

            Assert.True(result.VerificationUsed);
            Assert.Null(result.CpuFan);
            Assert.Empty(result.All);
            Assert.Single(result.RejectedByVerification);
            Assert.Equal(0x90, result.RejectedByVerification[0].Offset);
            Assert.Equal(45, result.RejectedByVerification[0].VerifyValue);
            Assert.Contains(result.Notes, n => n.Contains("return-to-idle"));
        }

        [Fact]
        public void VerificationKeepsTrueLe16Tach() {
            // A real tachometer falls back toward idle after the sweep.
            var samples = SweepLe16(0xB0, new[] { 1200, 2400, 3600, 4800 });
            var verifyDump = new byte[256];
            PutWord(verifyDump, 0xB0, 1350);

            var result = EcDiffScanner.Scan(samples, verifyDump);

            Assert.True(result.VerificationUsed);
            Assert.NotNull(result.CpuFan);
            Assert.Equal(0xB0, result.CpuFan!.Offset);
            Assert.Equal(1350, result.CpuFan.VerifyValue);
            Assert.Empty(result.RejectedByVerification);
        }

        [Fact]
        public void VerificationKeepsSlowSpinDownTach() {
            // A fan still spinning down (well below top, above idle) must survive —
            // rejection requires the value to have NOT dropped at all.
            var samples = SweepLe16(0xB0, new[] { 1200, 2400, 3600, 4800 });
            var verifyDump = new byte[256];
            PutWord(verifyDump, 0xB0, 3900);

            var result = EcDiffScanner.Scan(samples, verifyDump);

            Assert.NotNull(result.CpuFan);
            Assert.Empty(result.RejectedByVerification);
        }

        [Fact]
        public void VerificationKeepsPeriodEncodedTach() {
            // Period byte rises back toward its (high) idle value after the sweep.
            int[] levels = { 0, 30, 70, 100 };
            byte[] period = { 0xB0, 0x90, 0x60, 0x30 };
            var samples = new List<EcDiffScanner.Sample>();
            for(int i = 0; i < levels.Length; i++)
                samples.Add(new EcDiffScanner.Sample(levels[i], Dump((0xD2, period[i]))));
            var verifyDump = Dump((0xD2, 0xA8));

            var result = EcDiffScanner.Scan(samples, verifyDump);

            Assert.NotNull(result.CpuFan);
            Assert.Equal(0xD2, result.CpuFan!.Offset);
            Assert.Empty(result.RejectedByVerification);
        }

        [Fact]
        public void VerificationRejectsDownCounter() {
            // A register falling with time mimics period encoding on the way up,
            // but keeps falling after the fans are commanded back down.
            int[] levels = { 0, 30, 70, 100 };
            byte[] downCounter = { 0xC0, 0x9A, 0x74, 0x4E };
            var samples = new List<EcDiffScanner.Sample>();
            for(int i = 0; i < levels.Length; i++)
                samples.Add(new EcDiffScanner.Sample(levels[i], Dump((0xD2, downCounter[i]))));
            var verifyDump = Dump((0xD2, 0x28));

            var result = EcDiffScanner.Scan(samples, verifyDump);

            Assert.Null(result.CpuFan);
            Assert.Single(result.RejectedByVerification);
        }

        [Fact]
        public void VerificationSeparatesCounterFromRealTach() {
            // The payoff case: a counter and a real tach both present. Without
            // verification the counter pollutes the top-two pick; with it, only
            // the real tachometer remains.
            int[] levels = { 0, 30, 70, 100 };
            int[] rpm = { 1200, 2400, 3600, 4800 };
            byte[] counter = { 5, 15, 25, 35 };
            var samples = new List<EcDiffScanner.Sample>();
            for(int i = 0; i < levels.Length; i++) {
                var d = Dump((0x90, counter[i]));
                PutWord(d, 0xB0, rpm[i]);
                samples.Add(new EcDiffScanner.Sample(levels[i], d));
            }
            var verifyDump = Dump((0x90, 45));
            PutWord(verifyDump, 0xB0, 1400);

            var unverified = EcDiffScanner.Scan(samples);
            var verified = EcDiffScanner.Scan(samples, verifyDump);

            // Without verification the counter occupies a fan slot (CPU = lower offset 0x90).
            Assert.NotNull(unverified.GpuFan);
            Assert.Equal(0x90, unverified.CpuFan!.Offset);

            // With verification the real tach is the sole candidate, on the CPU slot.
            Assert.NotNull(verified.CpuFan);
            Assert.Equal(0xB0, verified.CpuFan!.Offset);
            Assert.Null(verified.GpuFan);
            Assert.Single(verified.RejectedByVerification);
        }

        [Fact]
        public void ScanValidatesItsInputs() {
            var one = new List<EcDiffScanner.Sample> { new EcDiffScanner.Sample(0, new byte[256]) };
            Assert.Throws<ArgumentException>(() => EcDiffScanner.Scan(one));

            var badSize = new List<EcDiffScanner.Sample> {
                new EcDiffScanner.Sample(0, new byte[256]),
                new EcDiffScanner.Sample(100, new byte[128]),
            };
            Assert.Throws<ArgumentException>(() => EcDiffScanner.Scan(badSize));

            var ok = new List<EcDiffScanner.Sample> {
                new EcDiffScanner.Sample(0, new byte[256]),
                new EcDiffScanner.Sample(100, new byte[256]),
            };
            Assert.Throws<ArgumentException>(() => EcDiffScanner.Scan(ok, new byte[64]));
        }

    }

}
