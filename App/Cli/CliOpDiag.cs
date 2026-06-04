  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

// One-shot diagnostic dump. Bundles everything the maintainer needs to
// triage a bug report — model identity, BIOS capabilities, EC snapshot,
// AutoCal sidecar, recent EC trace, crash log inventory — into a single
// Markdown blob the user can paste into a GitHub issue.
//
// Three entry points:
//   * OmenMon.exe -Diag         CLI verb (this file)
//   * Tray menu "Copy diagnostic info"          (GuiMenu.cs)
//   * Embedded in every crash dump              (Crash.cs)
//
// Telemetry-free: nothing leaves the machine unless the user pastes it
// themselves. No network, no auto-upload, no opt-in flow to chase.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using OmenMon.Driver;
using OmenMon.Hardware.Bios;
using OmenMon.Hardware.Ec;
using OmenMon.Hardware.Platform;
using OmenMon.Library;

namespace OmenMon.AppCli {

    public static partial class CliOp {

#region Public API
        // Builds the full Markdown bundle. Safe to call from any thread; never
        // throws (all sections trap their own exceptions and substitute a row
        // documenting the failure, so a half-broken machine still produces a
        // useful report).
        internal static string DiagGetMarkdown() {
            var sb = new StringBuilder();
            string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            sb.AppendLine("# OmenMon-Reborn Diagnostic Report");
            sb.AppendLine();
            sb.AppendLine($"> Generated: {ts}");
            sb.AppendLine("> Paste this into a GitHub issue at <https://github.com/seakyy/OmenMon-Reborn/issues>.");
            sb.AppendLine();
            DiagEnvironment(sb);
            DiagDriverStatus(sb);
            DiagModelPreset(sb);
            DiagFanState(sb);
            DiagAutoCalSidecar(sb);
            DiagEcTrace(sb);
            DiagCrashLogs(sb);
            DiagHardwareProbe(sb);
            return sb.ToString();
        }

        // Runs -Diag from the command line. Writes the report to disk and
        // mirrors a short status line to the console.
        internal static void DiagRun() {
            string content = DiagGetMarkdown();
            string outPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "OmenMon-Diag-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".md");
            try {
                File.WriteAllText(outPath, content, Encoding.UTF8);
                Console.WriteLine("Saved: " + outPath);
                Console.WriteLine();
                Console.WriteLine("Open the file and paste its contents into a new GitHub issue at:");
                Console.WriteLine("  https://github.com/seakyy/OmenMon-Reborn/issues");
            } catch(Exception ex) {
                Console.WriteLine("Warning: could not save file — printing inline instead.");
                Console.WriteLine("  " + ex.GetType().Name + ": " + ex.Message);
                Console.WriteLine();
                Console.WriteLine(content);
            }
        }
#endregion

#region Section: Environment
        private static void DiagEnvironment(StringBuilder sb) {
            sb.AppendLine("## Environment");
            sb.AppendLine();
            sb.AppendLine("| Field | Value |");
            sb.AppendLine("|-------|-------|");

            // OmenMon version stamped at build time
            string version = "?";
            try {
                Assembly asm = typeof(CliOp).Assembly;
                AssemblyInformationalVersionAttribute info = asm
                    .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
                    .Cast<AssemblyInformationalVersionAttribute>()
                    .FirstOrDefault();
                if(info != null) version = info.InformationalVersion;
            } catch { }
            sb.AppendLine($"| OmenMon version | `{version}` |");

            // OS — Environment.OSVersion is good enough; users on Win10 vs Win11
            // see slightly different driver / HVCI behaviour.
            string os = "?";
            try { os = Environment.OSVersion.ToString(); } catch { }
            sb.AppendLine($"| OS              | `{os}` |");

            // 64-bit process / OS — relevant because the WMI BIOS path
            // requires 64-bit on every modern Omen SKU.
            sb.AppendLine($"| Process bitness | `{(Environment.Is64BitProcess ? "x64" : "x86")}` |");
            sb.AppendLine($"| OS bitness      | `{(Environment.Is64BitOperatingSystem ? "x64" : "x86")}` |");

            // CLR — Framework vs Core difference would never apply here
            // (the project targets .NET Framework 4.8), but the exact build
            // sometimes correlates with weird P/Invoke behaviour.
            try {
                sb.AppendLine($"| CLR             | `{Environment.Version}` |");
            } catch { }

            sb.AppendLine();
        }
#endregion

#region Section: PawnIO / Driver status
        private static void DiagDriverStatus(StringBuilder sb) {
            sb.AppendLine("## Kernel Driver (PawnIO)");
            sb.AppendLine();

            // Driver/Ring0 won't have been opened yet when -Diag runs from
            // the CLI before anything touches hardware. Try to open it once
            // so the user's report reflects whether their PawnIO install
            // works for OmenMon at all.
            bool wasOpen = Ring0.IsOpen;
            if(!wasOpen) {
                try { Ring0.Open(); } catch { }
            }
            string status = Ring0.GetStatus();

            sb.AppendLine($"- IsOpen: `{Ring0.IsOpen}`");
            if(!string.IsNullOrWhiteSpace(status)) {
                sb.AppendLine();
                sb.AppendLine("Status log:");
                sb.AppendLine("```");
                sb.Append(status);
                if(!status.EndsWith("\n") && !status.EndsWith("\r"))
                    sb.AppendLine();
                sb.AppendLine("```");
            }
            sb.AppendLine();
        }
#endregion

#region Section: Model database entry
        private static void DiagModelPreset(StringBuilder sb) {
            sb.AppendLine("## Model Database Match");
            sb.AppendLine();
            try {
                string productId = new Settings().GetProduct() ?? "?";
                sb.AppendLine($"- Detected ProductId: `{productId}`");
                if(Config.Models != null && Config.Models.TryGetValue(productId, out PlatformPreset preset)) {
                    sb.AppendLine($"- Native preset: **{preset.DisplayName ?? "(no DisplayName)"}**");
                    sb.AppendLine();
                    sb.AppendLine("| Register | Value (dec) | Value (hex) |");
                    sb.AppendLine("|----------|-------------|-------------|");
                    AppendPresetRow(sb, "FanLevelReg0",     preset.FanLevelReg0);
                    AppendPresetRow(sb, "FanLevelReg1",     preset.FanLevelReg1);
                    AppendPresetRow(sb, "FanRateReadReg0",  preset.FanRateReadReg0);
                    AppendPresetRow(sb, "FanRateReadReg1",  preset.FanRateReadReg1);
                    AppendPresetRow(sb, "FanRateWriteReg0", preset.FanRateWriteReg0);
                    AppendPresetRow(sb, "FanRateWriteReg1", preset.FanRateWriteReg1);
                    AppendPresetRow(sb, "FanSpeedReg0",     preset.FanSpeedReg0);
                    AppendPresetRow(sb, "FanSpeedReg1",     preset.FanSpeedReg1);
                    AppendPresetRow(sb, "CountdownReg",     preset.CountdownReg);
                    AppendPresetRow(sb, "ManualReg",        preset.ManualReg);
                    AppendPresetRow(sb, "ModeReg",          preset.ModeReg);
                    AppendPresetRow(sb, "SwitchReg",        preset.SwitchReg);
                    if(preset.TempCpuReg != 0) AppendPresetRow(sb, "TempCpuReg", preset.TempCpuReg);
                    if(preset.TempGpuReg != 0) AppendPresetRow(sb, "TempGpuReg", preset.TempGpuReg);
                } else {
                    sb.AppendLine("- _No native preset for this ProductId — falling back to global defaults._");
                    sb.AppendLine("- Consider running the Auto-Calibration Wizard or sending the maintainer a `-Probe` dump.");
                }
            } catch(Exception ex) {
                sb.AppendLine($"- Error resolving preset: `{ex.GetType().Name}: {ex.Message}`");
            }
            sb.AppendLine();
        }

        private static void AppendPresetRow(StringBuilder sb, string name, byte value) {
            sb.AppendLine($"| {name} | {value} | 0x{value:X2} |");
        }
#endregion

#region Section: Live fan telemetry
        // Live per-fan readout (issue #49 — "debug fans"). The single most common
        // fan bug report is "RPM looks wrong / fan won't move", and triaging it
        // previously meant cross-referencing the raw EC dump against the preset by
        // hand. This section reads each fan's BIOS level, duty-cycle rate, the
        // resolved live speed, AND the exact register/mode/multiplier Fan.GetSpeed()
        // used to produce that speed — so a bogus RPM is immediately attributable to
        // a wrong tachometer register or decode mode. Read-only: GetLevel/GetRate/
        // GetSpeed never write to the EC.
        private static void DiagFanState(StringBuilder sb) {
            sb.AppendLine("## Live Fan Telemetry");
            sb.AppendLine();
            try {
                var platform = new Platform();
                var fans = platform.Fans;

                sb.AppendLine($"- Mode: `{fans.GetMode()}`, Manual: `{fans.GetManual()}`, Max: `{fans.GetMax()}`, Off: `{fans.GetOff()}`, Countdown: `{fans.GetCountdown()}` s");
                sb.AppendLine();
                sb.AppendLine("| Fan | Level [krpm] | Rate [%] | Speed [rpm] | RPM source |");
                sb.AppendLine("|-----|--------------|----------|-------------|------------|");

                foreach(var fan in fans.Fan) {
                    string type = fan.GetFanType().ToString();
                    string level = TryFan(() => fan.GetLevel().ToString());
                    string rate  = TryFan(() => fan.GetRate().ToString());
                    string speed = TryFan(() => fan.GetSpeed().ToString());

                    // Describe where the RPM came from, mirroring Fan.GetSpeed()'s
                    // resolution order (AutoCal override → preset EC speed register).
                    string source = "preset EC speed register";
                    try {
                        byte reg = 0;
                        EcDiffScanner.Mode mode = default(EcDiffScanner.Mode);
                        int mul = 0;
                        bool have =
                            fan.GetFanType() == BiosData.FanType.Cpu ? AutoCal.TryGetCpu(out reg, out mode, out mul) :
                            fan.GetFanType() == BiosData.FanType.Gpu ? AutoCal.TryGetGpu(out reg, out mode, out mul) :
                            false;
                        if(have)
                            source = mode == EcDiffScanner.Mode.BiosLevelMirror
                                ? $"AutoCal BiosLevelMirror ×{(mul > 0 ? mul : 100)}"
                                : $"AutoCal reg=0x{reg:X2}, mode={mode}, ×{mul}";
                    } catch { }

                    sb.AppendLine($"| {type} | {level} | {rate} | {speed} | {source} |");
                }

                string product = TryFan(() => new Settings().GetProduct());
                if(AutoCal.IsKnownBoard(product))
                    sb.AppendLine($"\n- `{product}` has a built-in RPM mapping (`AutoCal.KnownBoards`) — a blank EcDiffScanner result on this board is expected, not a fault.");

            } catch(Exception ex) {
                sb.AppendLine($"- Error reading fan telemetry: `{ex.GetType().Name}: {ex.Message}`");
            }
            sb.AppendLine();
        }

        private static string TryFan(Func<string> f) {
            try { return f() ?? "?"; } catch(Exception ex) { return "err:" + ex.GetType().Name; }
        }
#endregion

#region Section: AutoCal sidecar
        private static void DiagAutoCalSidecar(StringBuilder sb) {
            sb.AppendLine("## Auto-Calibration Override");
            sb.AppendLine();
            sb.AppendLine($"- HasCpu: `{AutoCal.HasCpu}`, HasGpu: `{AutoCal.HasGpu}`");
            if(AutoCal.HasCpu && AutoCal.TryGetCpu(out byte cReg, out var cMode, out int cMul))
                sb.AppendLine($"- CPU override: reg=`0x{cReg:X2}`, mode=`{cMode}`, multiplier=`{cMul}`");
            if(AutoCal.HasGpu && AutoCal.TryGetGpu(out byte gReg, out var gMode, out int gMul))
                sb.AppendLine($"- GPU override: reg=`0x{gReg:X2}`, mode=`{gMode}`, multiplier=`{gMul}`");

            string path = AutoCal.SidecarPath;
            if(File.Exists(path)) {
                sb.AppendLine();
                sb.AppendLine($"Sidecar file (`{AutoCal.SidecarFileName}`):");
                sb.AppendLine("```xml");
                try { sb.AppendLine(File.ReadAllText(path).Trim()); }
                catch(Exception ex) { sb.AppendLine($"(read error: {ex.Message})"); }
                sb.AppendLine("```");
            } else {
                sb.AppendLine($"- Sidecar file not present (`{path}`).");
            }
            sb.AppendLine();
        }
#endregion

#region Section: EC trace
        private static void DiagEcTrace(StringBuilder sb) {
            sb.AppendLine("## Recent EC Activity");
            sb.AppendLine();
            sb.AppendLine("Lock-free ring buffer of the last few minutes of EC reads / writes — useful for spotting register-access patterns around a misbehaviour.");
            sb.AppendLine();
            sb.AppendLine(EcTrace.FormatMarkdown(maxEntries: 192));
            sb.AppendLine();
        }
#endregion

#region Section: Crash log inventory
        // Lists any OmenMon-crash-*.log files in the install directory so the
        // user knows there's something to attach to the issue beyond this
        // report. Doesn't inline the crash bodies — they can be large and
        // contain stack traces with locale-dependent strings; attaching them
        // as separate files is cleaner for the maintainer.
        private static void DiagCrashLogs(StringBuilder sb) {
            sb.AppendLine("## Crash Logs");
            sb.AppendLine();
            try {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                var logs = new DirectoryInfo(dir)
                    .GetFiles("OmenMon-crash-*.log")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(10)
                    .ToArray();
                if(logs.Length == 0) {
                    sb.AppendLine("- _No crash logs found. Nothing to attach._");
                } else {
                    sb.AppendLine($"Found {logs.Length} crash log file(s) in `{dir}`. **Attach these to your issue.**");
                    sb.AppendLine();
                    sb.AppendLine("| File | Modified (UTC) | Size |");
                    sb.AppendLine("|------|----------------|------|");
                    foreach(var f in logs)
                        sb.AppendLine($"| `{f.Name}` | {f.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss} | {f.Length} B |");
                }
            } catch(Exception ex) {
                sb.AppendLine($"- Error enumerating crash logs: `{ex.GetType().Name}: {ex.Message}`");
            }
            sb.AppendLine();
        }
#endregion

#region Section: Embedded hardware probe
        // Reuses the existing -Probe Markdown generator so the diag report
        // contains the same BIOS / EC table dump the maintainer already
        // knows how to read. ProbeGetMarkdown(false) skips the 5 s EC diff
        // pass so -Diag stays snappy.
        private static void DiagHardwareProbe(StringBuilder sb) {
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("## Hardware Probe (single snapshot)");
            sb.AppendLine();
            try {
                string probe = ProbeGetMarkdown(includeEcDiff: false);
                // ProbeGetMarkdown emits its own top-level header — strip it
                // so the diag report has a clean section hierarchy.
                int firstSection = probe.IndexOf("## ");
                if(firstSection > 0)
                    probe = probe.Substring(firstSection);
                sb.Append(probe);
            } catch(Exception ex) {
                sb.AppendLine($"_Probe failed: `{ex.GetType().Name}: {ex.Message}`_");
            }
        }
#endregion

    }

}
