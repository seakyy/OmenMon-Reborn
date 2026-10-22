  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using System.Collections.Generic;

// The main project compiles with nullable reference types disabled, but this file is
// also linked into the .NET 8 test project, which enables them. Declaring the state
// explicitly keeps the warning output identical in both places instead of having the
// nullable-annotated signatures leak into one build only.
#nullable disable

namespace OmenMon.Hardware.Platform {

#region Data
    // A single node of a fan curve: one temperature threshold together with the fan
    // level each fan should run at once that threshold is reached
    public struct FanCurvePoint {

        public int Temperature;
        public int Cpu;
        public int Gpu;

        public FanCurvePoint(int temperature, int cpu, int gpu) {
            this.Temperature = temperature;
            this.Cpu = cpu;
            this.Gpu = gpu;
        }

        public override string ToString() {
            return this.Temperature + " C -> " + this.Cpu + " / " + this.Gpu;
        }

    }
#endregion

#region Logic
    // Pure fan-curve mathematics.
    //
    // Deliberately references nothing but System and System.Collections.Generic so it
    // can be compiled straight into the .NET 8 test project (the main project is
    // .NET Framework 4.8 and cannot be referenced from it). Keep it pure: no Config,
    // no FanProgram, no hardware access. If this file ever needs a dependency, the
    // test link in OmenMon.Tests.csproj breaks.
    public static class FanCurve {

        // Temperature domain accepted by the configuration schema [deg C]
        public const int TemperatureMin = 0;
        public const int TemperatureMax = 99;   // = ConfigData.MaxBelievableTemperature

        // Fan level domain [decimal, not hex]
        public const int LevelMin = 0;
        public const int LevelMaxDefault = 55;  // = ConfigData.FanLevelMax (default)

        // Mirrors List<byte>.BinarySearch(value) exactly, including the bitwise
        // complement convention: a non-negative result is an index hit, a negative
        // result is the bitwise complement of the insertion point.
        //
        // List<T>.BinarySearch is used by FanProgram.GetTemperatureLevel, so this has
        // to behave identically for the interpolated result to match byte for byte.
        private static int BinarySearch(IList<byte> items, byte value) {

            int lo = 0;
            int hi = items.Count - 1;

            while(lo <= hi) {

                int mid = lo + ((hi - lo) >> 1);
                byte midValue = items[mid];

                if(midValue == value)
                    return mid;

                if(midValue < value)
                    lo = mid + 1;
                else
                    hi = mid - 1;

            }

            return ~lo;

        }

        // Returns the temperature threshold that governs the given temperature.
        //
        // This is a step lookup (floor), not a linear interpolation: the curve holds
        // the level of the largest threshold that is not above the temperature, and
        // falls back to the lowest defined threshold for anything below it.
        //
        // Reproduces FanProgram.GetTemperatureLevel exactly.
        public static byte LookupThreshold(IList<byte> thresholds, byte temperature) {

            if(thresholds == null || thresholds.Count == 0)
                throw new ArgumentException("The fan curve has no temperature thresholds", "thresholds");

            int value = BinarySearch(thresholds, temperature);

            // Exact hit: the threshold itself governs
            if(value >= 0)
                return thresholds[value];

            // Otherwise step down to the next lower threshold, clamped to the lowest
            // one so a temperature below the entire curve never goes out of bounds
            int index = ~value - 1;
            return thresholds[index < 0 ? 0 : index];

        }

        // Returns the sorted threshold list for a level mapping.
        // SortedDictionary enumerates its keys in ascending order, which is exactly
        // the order FanProgram relies on when it builds its look-up list.
        public static List<byte> GetThresholds(SortedDictionary<byte, byte[]> level) {

            List<byte> thresholds = new List<byte>();

            if(level == null)
                return thresholds;

            foreach(KeyValuePair<byte, byte[]> pair in level)
                thresholds.Add(pair.Key);

            return thresholds;

        }

        // Resolves the fan level pair that applies at the given temperature.
        // Returns null when the curve is empty or malformed.
        public static byte[] LookupLevels(SortedDictionary<byte, byte[]> level, byte temperature) {

            if(level == null || level.Count == 0)
                return null;

            byte threshold = LookupThreshold(GetThresholds(level), temperature);

            if(!level.TryGetValue(threshold, out byte[] levels) || levels == null || levels.Length < 2)
                return null;

            return new byte[] { levels[0], levels[1] };

        }

        // --- Validation helpers ---

        public static bool IsValidTemperature(int temperature) {

            return temperature >= TemperatureMin && temperature <= TemperatureMax;

        }

        public static bool IsValidLevel(int level, int levelMax) {

            return level >= LevelMin && level <= levelMax;

        }

        public static int ClampTemperature(int temperature) {

            if(temperature < TemperatureMin) return TemperatureMin;
            if(temperature > TemperatureMax) return TemperatureMax;

            return temperature;

        }

        public static int ClampLevel(int level, int levelMax) {

            if(levelMax < LevelMin) levelMax = LevelMaxDefault;
            if(level < LevelMin) return LevelMin;
            if(level > levelMax) return levelMax;

            return level;

        }

        // Effective highest fan level the editor is allowed to write on this machine.
        //
        // On boards in FanArray.HasMaxFanFreeze (878A among them) driving the fans to
        // the very top of the range has been observed to latch the EC into a state
        // that only a power cycle clears. The caller passes freezeRisk = true for
        // those boards and the ceiling drops one step below the configured maximum;
        // an explicit, confirmed override can still restore the full range.
        public static int ResolveWritableCeiling(int configuredMax, bool freezeRisk) {

            if(configuredMax < LevelMin)
                configuredMax = LevelMaxDefault;

            if(!freezeRisk)
                return configuredMax;

            int ceiling = configuredMax - 1;

            return ceiling < LevelMin ? LevelMin : ceiling;

        }

        // --- Point list <-> level mapping conversion ---

        // Flattens the configuration mapping into an ordered point list for editing
        public static List<FanCurvePoint> ToPoints(SortedDictionary<byte, byte[]> level) {

            List<FanCurvePoint> points = new List<FanCurvePoint>();

            if(level == null)
                return points;

            foreach(KeyValuePair<byte, byte[]> pair in level) {

                if(pair.Value == null || pair.Value.Length < 2)
                    continue;

                points.Add(new FanCurvePoint(pair.Key, pair.Value[0], pair.Value[1]));

            }

            return points;

        }

        // Rebuilds the configuration mapping from an edited point list.
        // Rejects - rather than silently collapses - duplicate temperatures, invalid
        // ranges and an empty curve; the caller surfaces the error string.
        public static bool TryBuildLevels(
            IList<FanCurvePoint> points,
            int levelMax,
            out SortedDictionary<byte, byte[]> level,
            out string error) {

            level = new SortedDictionary<byte, byte[]>();
            error = "";

            if(points == null || points.Count == 0) {

                error = "The curve needs at least one point.";
                return false;

            }

            for(int i = 0; i < points.Count; i++) {

                FanCurvePoint point = points[i];

                if(!IsValidTemperature(point.Temperature)) {

                    error = "Temperature " + point.Temperature + " C is out of range.";
                    return false;

                }

                if(!IsValidLevel(point.Cpu, levelMax)) {

                    error = "CPU level " + point.Cpu + " is out of range (0.." + levelMax + ").";
                    return false;

                }

                if(!IsValidLevel(point.Gpu, levelMax)) {

                    error = "GPU level " + point.Gpu + " is out of range (0.." + levelMax + ").";
                    return false;

                }

                byte key = (byte) point.Temperature;

                if(level.ContainsKey(key)) {

                    error = "Two points share the same temperature (" + point.Temperature + " C).";
                    return false;

                }

                level[key] = new byte[] { (byte) point.Cpu, (byte) point.Gpu };

            }

            return true;

        }

        // Returns the index of the point whose temperature is closest to the given
        // one, or -1 when the list is empty
        public static int FindNearest(IList<FanCurvePoint> points, int temperature) {

            if(points == null || points.Count == 0)
                return -1;

            int best = 0;
            int bestDistance = Math.Abs(points[0].Temperature - temperature);

            for(int i = 1; i < points.Count; i++) {

                int distance = Math.Abs(points[i].Temperature - temperature);

                if(distance < bestDistance) {

                    best = i;
                    bestDistance = distance;

                }

            }

            return best;

        }

    }
#endregion

}
