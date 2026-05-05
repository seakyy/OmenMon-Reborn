  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using OmenMon.Hardware.Bios;
using OmenMon.Hardware.Ec;
using OmenMon.Hardware.Platform;
using OmenMon.Library;

namespace OmenMon.AppCli {

    // Auto-Calibration Wizard.
    //
    // Drives the fans through a series of speed steps, takes an EC dump at each
    // step, runs the EcDiffScanner heuristic, applies any plausible result to
    // the running session, and produces a Markdown report for upstream.
    public static partial class CliOp {

#region Public API
        // Progress callback signature. Phase is a short tag ("baseline", "step50%", …),
        // detail is a free-form message, and percent is 0..100 for the overall run.
        public delegate void CalibrationProgress(string phase, string detail, int percent);

        public sealed class CalibrationOutcome {
            public bool Success;
            public string FailureReason;
            public EcDiffScanner.Result Scan;
            public string Markdown;            // ready to paste into a GitHub issue
            public string ProductId;
            public string BiosVersion;
            public List<EcDiffScanner.Sample> Samples = new List<EcDiffScanner.Sample>();
        }

        // Default profile: idle, two intermediate steps, full speed.
        // Multiple steps make the heuristic far more robust than a binary idle/max diff,
        // because each candidate must move monotonically with the commanded level.
        private static readonly int[] DefaultProfile = { 0, 30, 70, 100 };

        // Settle time per step. Fans need ~10 s to physically reach the commanded RPM
        // before the EC tachometer reading stabilises; 12 s gives a small safety margin.
        private const int StepSettleMs = 12000;

        internal static CalibrationOutcome AutoCalibrate(
            CalibrationProgress progress = null,
            CancellationToken cancel = default(CancellationToken),
            int[] profile = null,
            IFanArray fans = null) {

            var outcome = new CalibrationOutcome();
            profile = profile ?? DefaultProfile;
            progress = progress ?? ((p, d, pct) => { });

            // Snapshot prior fan state so we can restore it no matter how this exits.
            byte[] priorLevels = null;
            bool priorMax = false;
            bool priorManual = false;
            bool priorOff = false;

            try {
                if(Hw.Bios == null) Hw.Bios = Hw.BiosInterface();
                if(Hw.Ec == null)   Hw.Ec   = Hw.EcInterface();

                if(Hw.Bios == null || !Hw.Bios.IsInitialized) {
                    outcome.FailureReason = "BIOS interface unavailable. Run as administrator and retry.";
                    return outcome;
                }
                if(Hw.Ec == null || !Hw.Ec.IsInitialized) {
                    outcome.FailureReason = "Embedded Controller unavailable. The kernel driver failed to load — verify HVCI / Memory Integrity is off and retry.";
                    return outcome;
                }

                outcome.ProductId   = SafeProductId();
                outcome.BiosVersion = SafeBiosVersion();

                // Use the caller-supplied fan array (the GUI passes its live one). When
                // invoked from the CLI we build a fresh Platform to get a working handle —
                // this exercises exactly the same control surface the GUI uses day-to-day.
                if(fans == null)
                    fans = new Platform().Fans;
                if(fans == null) {
                    outcome.FailureReason = "Could not obtain fan-array handle.";
                    return outcome;
                }

                try { priorLevels = fans.GetLevels(); }   catch { }
                try { priorMax    = fans.GetMax(); }      catch { }
                try { priorManual = fans.GetManual(); }   catch { }
                try { priorOff    = fans.GetOff(); }      catch { }

                progress("init", "Engaging manual fan control…", 2);
                fans.SetManual(true);
                fans.SetCountdown(600);  // 10 minutes — well over our worst-case run time

                int totalSteps = profile.Length;
                for(int i = 0; i < totalSteps; i++) {
                    cancel.ThrowIfCancellationRequested();
                    int level = profile[i];
                    int basePct = 5 + (90 * i) / totalSteps;

                    progress($"step{level}%", $"Setting fans to {level}%…", basePct);
                    ApplyLevel(fans, level);

                    // Settle window — split into 1 s ticks so we can honour cancellation
                    // and feed live progress back to the UI.
                    int ticks = StepSettleMs / 1000;
                    for(int t = 0; t < ticks; t++) {
                        cancel.ThrowIfCancellationRequested();
                        Thread.Sleep(1000);
                        progress($"step{level}%", $"Settling at {level}%… {ticks - t} s", basePct + (5 * t) / ticks);
                    }

                    progress($"step{level}%", $"Reading EC at {level}%…", basePct + 6);
                    byte[] dump = SnapshotEc();
                    outcome.Samples.Add(new EcDiffScanner.Sample(level, dump));
                }

                progress("scan", "Analysing register diffs…", 96);
                outcome.Scan = EcDiffScanner.Scan(outcome.Samples);

                if(outcome.Scan.IsPlausible) {
                    progress("apply", "Applying detected registers to live session…", 98);
                    ApplyToLiveSession(outcome.Scan);
                }

                outcome.Markdown = BuildReport(outcome);
                outcome.Success = true;
                progress("done", "Calibration complete.", 100);
                return outcome;

            } catch(OperationCanceledException) {
                outcome.FailureReason = "Cancelled by user.";
                return outcome;
            } catch(Exception ex) {
                outcome.FailureReason = ex.GetType().Name + ": " + ex.Message;
                return outcome;
            } finally {
                // Best-effort restore — we never want to leave the user stuck on
                // 100 % fans because something blew up halfway through.
                if(fans != null) {
                    try { fans.SetMax(priorMax); }                       catch { }
                    try { fans.SetOff(priorOff); }                       catch { }
                    if(priorLevels != null) {
                        try { fans.SetLevels(priorLevels); }             catch { }
                    }
                    try { fans.SetManual(priorManual); }                 catch { }
                    try { fans.SetCountdown(0); }                        catch { }
                }
            }
        }
#endregion

#region Helpers
        private static void ApplyLevel(IFanArray fans, int percent) {
            if(percent >= 100) {
                fans.SetMax(true);
                return;
            }
            fans.SetMax(false);
            // Translate % to krpm. Most HP fans top out near 5.5 krpm; this gives us
            // a coarse-but-distinct level per profile step, which is all the heuristic needs.
            byte krpm = (byte) Math.Round(percent * 5.5 / 100.0);
            try { fans.SetLevels(new byte[] { krpm, krpm }); } catch { }
            // Also drive the rate registers so we cover both 2022 and 2023+ layouts.
            try { fans.Fan[0].SetRate((byte) percent); } catch { }
            try { fans.Fan[1].SetRate((byte) percent); } catch { }
        }

        private static byte[] SnapshotEc() {
            byte[] snap = new byte[256];
            for(int r = 0; r < 256; r++)
                snap[r] = Hw.EcGetByte((byte) r);
            return snap;
        }

        private static string SafeProductId() {
            try { return new Settings().GetProduct() ?? "?"; }
            catch { return "?"; }
        }

        private static string SafeBiosVersion() {
            // Use the BIOS born-date as the identifying string — same field the existing
            // Probe report uses. Avoids pulling in System.Management for one query.
            try { return Hw.Bios?.GetBornDate() ?? "?"; }
            catch { return "?"; }
        }
#endregion

#region Apply to Live Session
        // Publishes the scan result to OmenMon.Library.AutoCal — read by Fan.GetSpeed()
        // on every refresh tick — and persists it to a sidecar XML so the override
        // survives a restart without us touching the main OmenMon.xml.
        private static void ApplyToLiveSession(EcDiffScanner.Result scan) {
            if(scan.CpuFan != null) {
                AutoCal.CpuFanReg  = scan.CpuFan.Offset;
                AutoCal.CpuFanMode = scan.CpuFan.Mode;
            }
            if(scan.GpuFan != null) {
                AutoCal.GpuFanReg  = scan.GpuFan.Offset;
                AutoCal.GpuFanMode = scan.GpuFan.Mode;
            }

            try {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "OmenMon-AutoCal.xml");
                var sb = new StringBuilder();
                sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
                sb.AppendLine("<AutoCalibration>");
                if(scan.CpuFan != null)
                    sb.AppendLine($"  <CpuFan offset=\"0x{scan.CpuFan.Offset:X2}\" mode=\"{scan.CpuFan.Mode}\" />");
                if(scan.GpuFan != null)
                    sb.AppendLine($"  <GpuFan offset=\"0x{scan.GpuFan.Offset:X2}\" mode=\"{scan.GpuFan.Mode}\" />");
                sb.AppendLine("</AutoCalibration>");
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            } catch { }
        }
#endregion

#region Markdown Report
        private static string BuildReport(CalibrationOutcome o) {
            var sb = new StringBuilder();
            string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            sb.AppendLine("# OmenMon Auto-Calibration Report");
            sb.AppendLine();
            sb.AppendLine($"> Generated: {ts}");
            sb.AppendLine("> Paste this into a new GitHub issue at https://github.com/seakyy/OmenMon-Reborn/issues to add your board to the model database.");
            sb.AppendLine();

            sb.AppendLine("## Device");
            sb.AppendLine();
            sb.AppendLine($"- **Product ID:** `{o.ProductId}`");
            sb.AppendLine($"- **BIOS Version:** `{o.BiosVersion}`");
            sb.AppendLine($"- **EC Read Method:** ACPI/Omen kernel driver");
            sb.AppendLine($"- **Profile:** {string.Join(" → ", o.Samples.Select(s => s.LevelPercent + "%"))}");
            sb.AppendLine();

            sb.AppendLine("## Scan Results");
            sb.AppendLine();
            if(o.Scan == null) {
                sb.AppendLine("_Scan did not complete._");
            } else {
                AppendCandidate(sb, "CPU", o.Scan.CpuFan);
                AppendCandidate(sb, "GPU", o.Scan.GpuFan);
                if(o.Scan.Notes.Count > 0) {
                    sb.AppendLine();
                    foreach(var note in o.Scan.Notes)
                        sb.AppendLine("> " + note);
                }
                if(o.Scan.All.Count > 0) {
                    sb.AppendLine();
                    sb.AppendLine("<details><summary>All ranked candidates</summary>");
                    sb.AppendLine();
                    sb.AppendLine("| Offset | Mode | Score | Values |");
                    sb.AppendLine("|--------|------|-------|--------|");
                    foreach(var c in o.Scan.All)
                        sb.AppendLine($"| `0x{c.Offset:X2}` | {c.Mode} | {c.Score} | `{string.Join(", ", c.Values)}` |");
                    sb.AppendLine();
                    sb.AppendLine("</details>");
                }
            }

            sb.AppendLine();
            sb.AppendLine("## Raw EC Dumps");
            sb.AppendLine();
            foreach(var sample in o.Samples) {
                sb.AppendLine($"### Fan @ {sample.LevelPercent}%");
                sb.AppendLine();
                sb.AppendLine("```");
                sb.AppendLine("0x _0 _1 _2 _3 _4 _5 _6 _7 _8 _9 _a _b _c _d _e _f");
                for(int hi = 0; hi <= 0xF0; hi += 0x10) {
                    var row = new StringBuilder();
                    row.Append(((byte) (hi >> 4)).ToString("x"));
                    row.Append("_ ");
                    for(int lo = 0; lo <= 0xF; lo++) {
                        row.Append(sample.Memory[hi | lo].ToString("X2"));
                        if(lo < 0xF) row.Append(' ');
                    }
                    sb.AppendLine(row.ToString());
                }
                sb.AppendLine("```");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static void AppendCandidate(StringBuilder sb, string label, EcDiffScanner.Candidate c) {
            if(c == null) {
                sb.AppendLine($"- **{label} Fan Register:** _not detected_");
                return;
            }
            sb.AppendLine($"- **{label} Fan Register:** `0x{c.Offset:X2}` (Mode: `{c.Mode}`) — {c.Description}");
        }

        // Persist the report next to the executable so the user has a copy even if
        // they accidentally clear the clipboard before pasting into the issue.
        internal static string SaveCalibrationReport(string markdown) {
            string path = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "OmenMon-Calibration-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".md");
            File.WriteAllText(path, markdown, Encoding.UTF8);
            return path;
        }
#endregion

    }

}
