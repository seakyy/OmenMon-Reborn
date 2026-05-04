  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using System.IO;
using System.Text;
using System.Threading;
using OmenMon.Hardware.Bios;
using OmenMon.Hardware.Ec;
using OmenMon.Hardware.Platform;
using OmenMon.Library;

namespace OmenMon.AppCli {

    // Implements the main operation loop in the application's CLI mode
    // This part covers the hardware probe / diagnostic dump routine
    public static partial class CliOp {

#region Probe
        // Builds and returns the full probe report as a Markdown string.
        // Pass includeEcDiff=false for a fast single-snapshot report (no 5 s wait).
        internal static string ProbeGetMarkdown(bool includeEcDiff = true) {
            var sb = new StringBuilder();
            string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            ProbeLine(sb, "# OmenMon-Reborn Hardware Probe");
            ProbeLine(sb, "");
            ProbeLine(sb, $"> Generated: {ts}  ");
            ProbeLine(sb, "> Paste this output into a GitHub issue to contribute model database entries.");
            ProbeLine(sb, "");
            ProbeBaseboard(sb);
            ProbeBios(sb);
            ProbeEc(sb, includeEcDiff);
            return sb.ToString();
        }

        // Runs a full hardware probe and writes the result as Markdown to a file
        internal static void ProbeRun() {
            string content = ProbeGetMarkdown(includeEcDiff: true);
            string outPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "OmenMon-Probe-" + DateTime.Now.ToString("yyyy-MM-dd") + ".md");
            try {
                File.WriteAllText(outPath, content, Encoding.UTF8);
                Console.WriteLine();
                Console.WriteLine("Saved: " + outPath);
            } catch(Exception ex) {
                Console.WriteLine("Warning: could not save file: " + ex.Message);
            }
        }

        // Outputs a line to both the StringBuilder and the console
        private static void ProbeLine(StringBuilder sb, string text) {
            sb.AppendLine(text);
            Console.WriteLine(text);
        }
#endregion

#region Probe — WMI Baseboard
        // Dumps WMI baseboard fields that identify the device model
        private static void ProbeBaseboard(StringBuilder sb) {

            ProbeLine(sb, "## WMI Baseboard");
            ProbeLine(sb, "");
            ProbeLine(sb, "| Field | Value |");
            ProbeLine(sb, "|-------|-------|");

            try {
                var s = new Settings();
                ProbeLine(sb, $"| Manufacturer | `{s.GetManufacturer()}` |");
                ProbeLine(sb, $"| Product      | `{s.GetProduct()}` |");
                ProbeLine(sb, $"| Serial       | `{s.GetSerial()}` |");
                ProbeLine(sb, $"| Version      | `{s.GetVersion()}` |");
            } catch(Exception ex) {
                ProbeLine(sb, $"| Error | `{ex.GetType().Name}: {ex.Message}` |");
            }

            ProbeLine(sb, "");

        }
#endregion

#region Probe — BIOS
        // Calls every read-only BiosCtl method and records the value or error
        private static void ProbeBios(StringBuilder sb) {

            ProbeLine(sb, "## BIOS Capabilities");
            ProbeLine(sb, "");
            ProbeLine(sb, "| Call | Value | Detail |");
            ProbeLine(sb, "|------|-------|--------|");

            if(Hw.Bios == null)
                Hw.Bios = Hw.BiosInterface();

            if(Hw.Bios == null || !Hw.Bios.IsInitialized) {
                ProbeLine(sb, "| Init | FAILED | Cannot establish WMI session |");
                ProbeLine(sb, "");
                return;
            }

            ProbeBiosRow(sb, "GetBornDate",
                () => ($"`{Hw.Bios.GetBornDate()}`", ""));

            ProbeBiosRow(sb, "GetKbdType", () => {
                BiosData.KbdType v = Hw.Bios.GetKbdType();
                return ($"`0x{(byte) v:X2}`", v.ToString());
            });

            ProbeBiosRow(sb, "HasBacklight",
                () => ($"`{Hw.Bios.HasBacklight()}`", ""));

            ProbeBiosRow(sb, "GetAdapter", () => {
                BiosData.AdapterStatus v = Hw.Bios.GetAdapter();
                return ($"`0x{(byte) v:X2}`", v.ToString());
            });

            ProbeBiosRow(sb, "GetGpuMode", () => {
                BiosData.GpuMode v = Hw.Bios.GetGpuMode();
                return ($"`0x{(byte) v:X2}`", v.ToString());
            });

            ProbeBiosRow(sb, "GetGpuPower", () => {
                BiosData.GpuPowerData v = Hw.Bios.GetGpuPower();
                return ($"`CustomTgp={v.CustomTgp} Ppab={v.Ppab} DState={v.DState}`", "");
            });

            ProbeBiosRow(sb, "GetFanCount",
                () => ($"`0x{Hw.Bios.GetFanCount():X2}`", ""));

            ProbeBiosRow(sb, "GetFanType", () => {
                byte v = Hw.Bios.GetFanType();
                string f1 = Enum.GetName(typeof(BiosData.FanType), (byte) (v & 0x0F)) ?? "?";
                string f2 = Enum.GetName(typeof(BiosData.FanType), (byte) (v >> 4)) ?? "?";
                return ($"`0x{v:X2}`", $"Fan1={f1} Fan2={f2}");
            });

            ProbeBiosRow(sb, "GetFanLevel", () => {
                byte[] v = Hw.Bios.GetFanLevel();
                string val = (v != null && v.Length >= 2) ? $"CPU=`0x{v[0]:X2}` GPU=`0x{v[1]:X2}`" : "N/A";
                return (val, "");
            });

            ProbeBiosRow(sb, "GetTemperature",
                () => ($"`{Hw.Bios.GetTemperature()} °C`", ""));

            ProbeBiosRow(sb, "GetMaxFan",
                () => ($"`{Hw.Bios.GetMaxFan()}`", ""));

            ProbeBiosRow(sb, "GetThrottling", () => {
                BiosData.Throttling v = Hw.Bios.GetThrottling();
                return ($"`0x{(byte) v:X2}`", v.ToString());
            });

            ProbeBiosRow(sb, "GetSystem", () => {
                BiosData.SystemData v = Hw.Bios.GetSystem();
                return (
                    $"`ThermalPolicy={v.ThermalPolicy} GpuModeSwitch={v.GpuModeSwitch}`",
                    $"CpuPL4={v.DefaultCpuPowerLimit4}W BiosOc={v.BiosOc} SupportFlags=`0x{(byte) v.SupportFlags:X2}`");
            });

            // These three always fail silently on devices that don't support them;
            // 0x00 may indicate unsupported rather than an actual zero value
            ProbeBiosRow(sb, "HasOverclock",
                () => ($"`0x{Hw.Bios.HasOverclock():X2}`", "0x00 = unsupported or failed"));

            ProbeBiosRow(sb, "HasMemoryOverclock",
                () => ($"`0x{Hw.Bios.HasMemoryOverclock():X2}`", "0x00 = unsupported or failed"));

            ProbeBiosRow(sb, "HasUndervoltBios",
                () => ($"`0x{Hw.Bios.HasUndervoltBios():X2}`", "0x00 = unsupported or failed"));

            ProbeLine(sb, "");

        }

        // Wraps a single BIOS probe call in error handling and emits a table row
        private static void ProbeBiosRow(StringBuilder sb, string name, Func<(string val, string detail)> fn) {
            try {
                var (val, detail) = fn();
                ProbeLine(sb, $"| `{name}` | {val} | {detail} |");
            } catch(Exception ex) {
                ProbeLine(sb, $"| `{name}` | Error | `{ex.GetType().Name}: {ex.Message}` |");
            }
        }
#endregion

#region Probe — EC
        // Takes EC snapshot(s) and emits register data; includeDiff=true waits 5 s for a second snapshot
        private static void ProbeEc(StringBuilder sb, bool includeDiff = true) {

            if(Hw.Ec == null)
                Hw.Ec = Hw.EcInterface();

            if(Hw.Ec == null || !Hw.Ec.IsInitialized) {
                ProbeLine(sb, "## EC Registers");
                ProbeLine(sb, "");
                ProbeLine(sb, "EC init failed — driver not loaded, access denied, or HVCI (Memory Integrity) active.");
                ProbeLine(sb, "");
                return;
            }

            ProbeLine(sb, "## EC Registers — Snapshot 1");
            ProbeLine(sb, "");
            byte[] snap1 = ProbeEcSnapshot();
            ProbeEcGrid(sb, snap1);
            ProbeLine(sb, "");

            if(!includeDiff)
                return;

            Console.Write("Waiting 5 s for second EC snapshot");
            for(int t = 0; t < 5; t++) { Thread.Sleep(1000); Console.Write("."); }
            Console.WriteLine();

            ProbeLine(sb, "## EC Registers — Snapshot 2");
            ProbeLine(sb, "");
            byte[] snap2 = ProbeEcSnapshot();
            ProbeEcGrid(sb, snap2);
            ProbeLine(sb, "");

            ProbeLine(sb, "## EC Registers — Delta (registers that changed)");
            ProbeLine(sb, "");
            ProbeLine(sb, "| Register | Name | Snap1 | Snap2 |");
            ProbeLine(sb, "|----------|------|-------|-------|");

            bool anyChanged = false;
            for(int r = 0; r < 256; r++) {
                if(snap1[r] != snap2[r]) {
                    anyChanged = true;
                    string rname = "—";
                    try {
                        rname = Enum.GetName(typeof(EmbeddedControllerData.Register), (byte) r) ?? "—";
                    } catch { }
                    ProbeLine(sb, $"| `0x{r:X2}` | {rname} | `0x{snap1[r]:X2}` | `0x{snap2[r]:X2}` |");
                }
            }

            if(!anyChanged)
                ProbeLine(sb, "_No registers changed. Verify the driver is working and the system is not completely idle._");

            ProbeLine(sb, "");

        }

        // Reads all 256 EC registers into a byte array
        private static byte[] ProbeEcSnapshot() {
            byte[] snap = new byte[256];
            for(int r = 0; r < 256; r++)
                snap[r] = Hw.EcGetByte((byte) r);
            return snap;
        }

        // Renders all 256 bytes as a 16×16 hex grid in Markdown code block format
        private static void ProbeEcGrid(StringBuilder sb, byte[] data) {
            ProbeLine(sb, "```");
            ProbeLine(sb, "0x _0 _1 _2 _3 _4 _5 _6 _7 _8 _9 _a _b _c _d _e _f");
            for(int hi = 0; hi <= 0xF0; hi += 0x10) {
                var row = new StringBuilder();
                row.Append(((byte) (hi >> 4)).ToString("x"));
                row.Append("_ ");
                for(int lo = 0; lo <= 0xF; lo++) {
                    row.Append(data[hi | lo].ToString("X2"));
                    if(lo < 0xF) row.Append(" ");
                }
                ProbeLine(sb, row.ToString());
            }
            ProbeLine(sb, "```");
        }
#endregion

    }

}
