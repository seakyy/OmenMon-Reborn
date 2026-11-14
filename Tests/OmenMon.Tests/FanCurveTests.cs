//\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System.Collections.Generic;
using OmenMon.Hardware.Platform;

namespace OmenMon.Tests {

    // Behavioural tests for the pure fan-curve logic in Hardware/FanCurve.cs.
    //
    // The headline requirement is PARITY: the editor must resolve temperatures to
    // thresholds exactly the way the fan program does at runtime, otherwise a curve
    // drawn in the GUI would behave differently from the same curve loaded from XML.
    //
    // FanProgram is unreachable from here (the main project targets .NET Framework
    // 4.8), so ReferenceGetTemperatureLevel below is a line-for-line replica of
    // FanProgram.GetTemperatureLevel - including its use of the real
    // List<byte>.BinarySearch - and every parity test drives both implementations
    // over the same inputs.
    public class FanCurveTests {

        // --- Reference implementation: replica of FanProgram.GetTemperatureLevel ---
        private static byte ReferenceGetTemperatureLevel(List<byte> levels, byte temperature) {

            int value;

            if((value = levels.BinarySearch(temperature)) >= 0)

                return levels[value];

            else {

                int index = ~value - 1;
                return levels[index < 0 ? 0 : index];

            }

        }

        private static SortedDictionary<byte, byte[]> Build(params (int temp, int cpu, int gpu)[] points) {

            var level = new SortedDictionary<byte, byte[]>();

            foreach(var p in points)
                level[(byte) p.temp] = new byte[] { (byte) p.cpu, (byte) p.gpu };

            return level;

        }

        // --- Parity ---

        [Fact]
        public void LookupThreshold_MatchesFanProgram_ForEveryTemperature_SimpleCurve() {

            var level = Build((30, 10, 10), (50, 25, 25), (70, 40, 40), (90, 55, 55));
            var thresholds = FanCurve.GetThresholds(level);

            for(int t = 0; t <= 120; t++) {

                byte expected = ReferenceGetTemperatureLevel(thresholds, (byte) t);
                byte actual = FanCurve.LookupThreshold(thresholds, (byte) t);

                Assert.True(expected == actual,
                    "Mismatch at " + t + " C: FanProgram resolves " + expected + ", FanCurve resolves " + actual);

            }

        }

        [Fact]
        public void LookupThreshold_MatchesFanProgram_ForEveryTemperature_SinglePoint() {

            var level = Build((60, 33, 33));
            var thresholds = FanCurve.GetThresholds(level);

            for(int t = 0; t <= 120; t++)
                Assert.Equal(ReferenceGetTemperatureLevel(thresholds, (byte) t), FanCurve.LookupThreshold(thresholds, (byte) t));

        }

        [Fact]
        public void LookupThreshold_MatchesFanProgram_ForEveryTemperature_DenseCurve() {

            // Every threshold from 20 to 99 - stresses exact hits and both step edges
            var level = new SortedDictionary<byte, byte[]>();
            for(int t = 20; t <= 99; t++)
                level[(byte) t] = new byte[] { (byte) (t % 56), (byte) ((t * 3) % 56) };

            var thresholds = FanCurve.GetThresholds(level);

            for(int t = 0; t <= 130; t++)
                Assert.Equal(ReferenceGetTemperatureLevel(thresholds, (byte) t), FanCurve.LookupThreshold(thresholds, (byte) t));

        }

        [Fact]
        public void LookupThreshold_ClampsToLowestPoint_BelowCurve() {

            // Documented clamp: a temperature below the whole curve yields the lowest
            // threshold, i.e. the idle row - never an exception, never row zero by luck
            var level = Build((40, 12, 12), (70, 45, 45));
            var thresholds = FanCurve.GetThresholds(level);

            Assert.Equal((byte) 40, FanCurve.LookupThreshold(thresholds, 0));
            Assert.Equal((byte) 40, FanCurve.LookupThreshold(thresholds, 25));
            Assert.Equal((byte) 40, FanCurve.LookupThreshold(thresholds, 39));

        }

        [Fact]
        public void LookupThreshold_IsStepwise_NotLinear() {

            // A temp just under the next node keeps the LOWER node's level
            var level = Build((30, 5, 5), (60, 50, 50));

            Assert.Equal(new byte[] { 5, 5 }, FanCurve.LookupLevels(level, 59));
            Assert.Equal(new byte[] { 50, 50 }, FanCurve.LookupLevels(level, 60));
            Assert.Equal(new byte[] { 50, 50 }, FanCurve.LookupLevels(level, 200));

        }

        [Fact]
        public void LookupThreshold_ExactHit_ReturnsThatNode() {

            var level = Build((30, 1, 2), (60, 3, 4), (90, 5, 6));

            Assert.Equal(new byte[] { 1, 2 }, FanCurve.LookupLevels(level, 30));
            Assert.Equal(new byte[] { 3, 4 }, FanCurve.LookupLevels(level, 60));
            Assert.Equal(new byte[] { 5, 6 }, FanCurve.LookupLevels(level, 90));

        }

        [Fact]
        public void LookupThreshold_EmptyCurve_Throws() {

            Assert.Throws<System.ArgumentException>(() =>
                FanCurve.LookupThreshold(new List<byte>(), 50));

            Assert.Null(FanCurve.LookupLevels(new SortedDictionary<byte, byte[]>(), 50));

        }

        // --- Threshold extraction ---

        [Fact]
        public void GetThresholds_ReturnsAscendingOrder() {

            var level = Build((90, 1, 1), (30, 1, 1), (60, 1, 1));

            Assert.Equal(new List<byte> { 30, 60, 90 }, FanCurve.GetThresholds(level));

        }

        // --- Range validation / clamping ---

        [Theory]
        [InlineData(0, true)]
        [InlineData(55, true)]
        [InlineData(99, true)]
        [InlineData(-1, false)]
        [InlineData(100, false)]
        public void IsValidTemperature_Bounds(int temperature, bool expected) {

            Assert.Equal(expected, FanCurve.IsValidTemperature(temperature));

        }

        [Theory]
        [InlineData(0, 55, true)]
        [InlineData(55, 55, true)]
        [InlineData(56, 55, false)]
        [InlineData(-1, 55, false)]
        public void IsValidLevel_Bounds(int level, int levelMax, bool expected) {

            Assert.Equal(expected, FanCurve.IsValidLevel(level, levelMax));

        }

        [Theory]
        [InlineData(-5, 0)]
        [InlineData(50, 50)]
        [InlineData(200, 99)]
        public void ClampTemperature_Clamps(int input, int expected) {

            Assert.Equal(expected, FanCurve.ClampTemperature(input));

        }

        [Theory]
        [InlineData(-5, 55, 0)]
        [InlineData(30, 55, 30)]
        [InlineData(99, 55, 55)]
        [InlineData(-5, 0, 0)]
        public void ClampLevel_Clamps(int input, int levelMax, int expected) {

            Assert.Equal(expected, FanCurve.ClampLevel(input, levelMax));

        }

        // --- Freeze guard ---

        [Fact]
        public void ResolveWritableCeiling_NoRisk_KeepsConfiguredMax() {

            Assert.Equal(55, FanCurve.ResolveWritableCeiling(55, false));

        }

        [Fact]
        public void ResolveWritableCeiling_FreezeRisk_DropsOneStep() {

            // 878A and friends: one step below the configured maximum
            Assert.Equal(54, FanCurve.ResolveWritableCeiling(55, true));

        }

        [Fact]
        public void ResolveWritableCeiling_FreezeRiskAtFloor_DoesNotGoNegative() {

            Assert.Equal(0, FanCurve.ResolveWritableCeiling(0, true));

        }

        [Fact]
        public void ResolveWritableCeiling_InvalidConfiguredMax_FallsBackToDefault() {

            Assert.Equal(FanCurve.LevelMaxDefault, FanCurve.ResolveWritableCeiling(-1, false));

        }

        // --- Point list <-> mapping round trip ---

        [Fact]
        public void ToPoints_ThenTryBuildLevels_RoundTripsExactly() {

            var original = Build((30, 10, 12), (60, 30, 32), (90, 55, 55));

            var points = FanCurve.ToPoints(original);

            Assert.Equal(3, points.Count);
            Assert.Equal(30, points[0].Temperature);
            Assert.Equal(90, points[2].Temperature);

            Assert.True(FanCurve.TryBuildLevels(points, 55, out var rebuilt, out var error), error);

            Assert.Equal(original.Count, rebuilt.Count);
            foreach(var key in original.Keys) {
                Assert.Equal(original[key][0], rebuilt[key][0]);
                Assert.Equal(original[key][1], rebuilt[key][1]);
            }

        }

        [Fact]
        public void TryBuildLevels_RejectsDuplicateTemperature() {

            var points = new List<FanCurvePoint> {
                new FanCurvePoint(50, 10, 10),
                new FanCurvePoint(50, 20, 20)
            };

            Assert.False(FanCurve.TryBuildLevels(points, 55, out _, out var error));
            Assert.Contains("same temperature", error);

        }

        [Fact]
        public void TryBuildLevels_RejectsEmpty() {

            Assert.False(FanCurve.TryBuildLevels(new List<FanCurvePoint>(), 55, out _, out var error));
            Assert.Contains("at least one point", error);

        }

        [Fact]
        public void TryBuildLevels_RejectsCpuLevelAboveCeiling() {

            // 55 is one above the 54 ceiling imposed on freeze-risk boards
            var points = new List<FanCurvePoint> { new FanCurvePoint(50, 55, 10) };

            Assert.False(FanCurve.TryBuildLevels(points, 54, out _, out var error));
            Assert.Contains("CPU level", error);

        }

        [Fact]
        public void TryBuildLevels_RejectsGpuLevelAboveCeiling() {

            // 55 is one above the 54 ceiling imposed on freeze-risk boards
            var points = new List<FanCurvePoint> { new FanCurvePoint(50, 10, 55) };

            Assert.False(FanCurve.TryBuildLevels(points, 54, out _, out var error));
            Assert.Contains("GPU level", error);

        }

        [Fact]
        public void TryBuildLevels_RejectsOutOfRangeTemperature() {

            var points = new List<FanCurvePoint> { new FanCurvePoint(120, 10, 10) };

            Assert.False(FanCurve.TryBuildLevels(points, 55, out _, out var error));
            Assert.Contains("out of range", error);

        }

        [Fact]
        public void TryBuildLevels_UnsortedInput_IsSortedOnOutput() {

            var points = new List<FanCurvePoint> {
                new FanCurvePoint(90, 3, 3),
                new FanCurvePoint(30, 1, 1),
                new FanCurvePoint(60, 2, 2)
            };

            Assert.True(FanCurve.TryBuildLevels(points, 55, out var level, out _));
            Assert.Equal(new List<byte> { 30, 60, 90 }, new List<byte>(level.Keys));

        }

        // --- Nearest point selection ---

        // Points sit at 40, 70 and 90 C. The midpoint cases (55 between 40 and 70,
        // 80 between 70 and 90) must fall to the LOWER point: FindNearest uses a
        // strict "<" comparison so ties keep the earlier index.
        [Theory]
        [InlineData(0, 0)]
        [InlineData(46, 0)]   // 6 C below 40, 24 C below 70
        [InlineData(56, 1)]   // 16 C above 40, 14 C below 70
        [InlineData(75, 1)]   // 5 C above 70, 15 C below 90
        [InlineData(90, 2)]
        public void FindNearest_PicksClosestPoint(int temperature, int expectedIndex) {

            var points = new List<FanCurvePoint> {
                new FanCurvePoint(40, 1, 1),
                new FanCurvePoint(70, 2, 2),
                new FanCurvePoint(90, 3, 3)
            };

            Assert.Equal(expectedIndex, FanCurve.FindNearest(points, temperature));

        }

        [Fact]
        public void FindNearest_EmptyList_ReturnsMinusOne() {

            Assert.Equal(-1, FanCurve.FindNearest(new List<FanCurvePoint>(), 50));

        }

    }

}
