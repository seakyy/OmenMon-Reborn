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
            public string BiosBornDate;
            public List<EcDiffScanner.Sample> Samples = new List<EcDiffScanner.Sample>();
            // Per-step live tachometer reading taken at the end of each settle window,
            // independent of the EC-dump heuristic. Lets the Markdown report show the
            // fans' RPM trajectory and lets the plateau detector below decide whether
            // to skip a higher step. Empty if the wizard never reached the first step.
            public List<LiveSpeedSample> LiveSpeeds = new List<LiveSpeedSample>();
            // Set when the wizard observed two consecutive steps with little or no
            // RPM gain on both fans — the board has hit its physical fan ceiling and
            // the next higher step would risk the BIOS rate-limiter freeze documented
            // on 8C30 / 8D07 / 8BAD. Plateau exit is treated as a successful run, not
            // a failure: the samples we did collect are still scanned and reported.
            public bool PlateauReached;
            public int PlateauStepPercent;     // commanded level at which plateau was observed
            public string PlateauNote;         // human-readable explanation for the report
            // Non-null when the sidecar XML could not be written (e.g. installed under
            // C:\Program Files without admin rights). The session keeps its in-memory
            // override but a restart will lose it — surfaced in the Markdown report so
            // the user knows persistence didn't happen.
            public string SidecarError;
        }

        // Per-step tachometer snapshot taken via the live IFan.GetSpeed() path, so the
        // report shows what the running session would have displayed at each commanded
        // level. CpuRpm / GpuRpm may be 0 when the read failed (unknown board, EC mutex
        // collision, …) — the plateau detector treats 0 as "unknown", not "plateaued".
        public sealed class LiveSpeedSample {
            public int LevelPercent;
            public int CpuRpm;
            public int GpuRpm;
            public LiveSpeedSample(int level, int cpu, int gpu) {
                LevelPercent = level; CpuRpm = cpu; GpuRpm = gpu;
            }
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
            IFanArray fans = null,
            FanProgram program = null) {

            var outcome = new CalibrationOutcome();
            profile = profile ?? DefaultProfile;
            progress = progress ?? ((p, d, pct) => { });

            // Filter out 100% step for models with known firmware freeze at max fan speed.
            // 8C30 (HP Victus 15-fb1000 2023 AMD) hits an EC controller freeze when the
            // BIOS internal rate-limiter is triggered at 100%, requiring a restart to recover.
            string productId = SafeProductId();
            if(FanArray.HasMaxFanFreeze(productId)) {
                profile = profile.Where(p => p < 100).ToArray();
                if(profile.Length == 0) {
                    outcome.FailureReason = $"Model {productId} has a known 100% fan freeze issue and the profile contains only 100% steps. Calibration aborted.";
                    return outcome;
                }
            }

            // Snapshot prior fan state so we can restore it no matter how this exits.
            byte[] priorLevels = null;
            bool priorMax = false;
            bool priorManual = false;
            bool priorOff = false;
            int priorCountdown = 0;
            bool priorCountdownCaptured = false;

            // If the tray's fan program is running, suspend it for the duration of
            // the sweep — otherwise its periodic Update() ticks will overwrite our
            // commanded 0/30/70/100 % steps and the EC snapshots will reflect the
            // program's adjustments instead of ours, making the heuristic unreliable.
            bool programWasSuspendedByUs = false;

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
                outcome.BiosBornDate = SafeBiosBornDate();

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
                try {
                    priorCountdown = fans.GetCountdown();
                    priorCountdownCaptured = true;
                } catch { }

                if(program != null) {
                    try {
                        if(program.IsEnabled && !program.IsSuspended) {
                            program.Suspend();
                            programWasSuspendedByUs = true;
                        }
                    } catch { }
                }

                progress("init", "Engaging manual fan control…", 2);
                fans.SetManual(true);
                // Clear the global fan-off latch — if the user had 'Fan Off' selected,
                // SetLevels/SetMax below would silently no-op and the wizard would
                // collect flat EC dumps and either fail or calibrate against bogus data.
                try { fans.SetOff(false); } catch { }
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

                    // Live tachometer reading via the session's normal Fan.GetSpeed() path.
                    // On boards with a correct preset (most known ProductIds) this gives a
                    // real RPM number; on unknown boards it may return zero or junk — the
                    // plateau detector handles that defensively by treating 0 as unknown.
                    int liveCpu = TryReadLiveSpeed(fans, 0);
                    int liveGpu = TryReadLiveSpeed(fans, 1);
                    outcome.LiveSpeeds.Add(new LiveSpeedSample(level, liveCpu, liveGpu));

                    // Plateau check. Always runs once we have at least two readings,
                    // because the freeze documented on 8C30 / 8D07 / 8BAD is triggered
                    // *by* the 100 % command itself — predicting it before issuing 100 %
                    // would require an intermediate probe step that is itself unsafe on
                    // boards whose physical ceiling sits below 85 %. So the check fires
                    // *after* each step:
                    //
                    //   - If a higher step is still queued, abort and skip it (saves the
                    //     user from a redundant or risky next step on a fan that is no
                    //     longer responding).
                    //   - If we're already at the last step, mark the plateau anyway so
                    //     the Markdown report tells the user / maintainer the run hit a
                    //     physical ceiling and the ProductId should be added to the
                    //     HasMaxFanFreeze list. The faulty 100 % sample is left in
                    //     outcome.Samples on purpose — the EcDiffScanner needs every
                    //     sample to score candidates, and discarding it would skew the
                    //     heuristic.
                    if(DetectPlateau(outcome.LiveSpeeds, out string plateauNote)) {
                        outcome.PlateauReached = true;
                        outcome.PlateauStepPercent = level;
                        outcome.PlateauNote = plateauNote;
                        bool haveHigherStepsQueued = false;
                        for(int j = i + 1; j < totalSteps; j++) {
                            if(profile[j] > level) { haveHigherStepsQueued = true; break; }
                        }
                        if(haveHigherStepsQueued) {
                            progress($"step{level}%", "Physical fan ceiling reached — skipping higher steps to avoid EC freeze.", basePct + 8);
                            // Try to gently leave the EC in a benign state — clear MaxFan
                            // in case any partial command lingered, then return to manual
                            // control so the finally block's restore path has a known
                            // baseline. Both calls are best-effort — on the boards where
                            // the EC fan controller has frozen, neither will recover the
                            // fans (user has to reboot), but they at least keep the rest
                            // of the wizard's teardown sequence well-defined.
                            try { fans.SetMax(false); } catch { }
                            try { fans.SetManual(true); } catch { }
                            break;
                        }
                    }
                }

                progress("scan", "Analysing register diffs…", 96);
                outcome.Scan = EcDiffScanner.Scan(outcome.Samples);

                if(outcome.Scan.IsPlausible) {
                    progress("apply", "Applying detected registers to live session…", 98);
                    outcome.SidecarError = ApplyToLiveSession(outcome.Scan, outcome.ProductId);
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
                // 100 % fans because something blew up halfway through. Order matters:
                // SetLevels must run before SetMax / SetOff, otherwise it would
                // overwrite the just-restored max/off mode and leave the user in a
                // different fan state than they had before the run.
                if(fans != null) {
                    if(priorLevels != null && !priorMax && !priorOff) {
                        try { fans.SetLevels(priorLevels); }             catch { }
                    }
                    try { fans.SetMax(priorMax); }                       catch { }
                    try { fans.SetOff(priorOff); }                       catch { }
                    try { fans.SetManual(priorManual); }                 catch { }
                    // Hard-coding SetCountdown(0) here would overwrite a non-zero
                    // user countdown (constant-speed mode, an active fan program)
                    // and silently change their state after the wizard exits. Only
                    // restore if we actually managed to read the prior value;
                    // otherwise leave the EC alone — the firmware's own auto-restore
                    // will end manual mode at the natural ten-minute mark.
                    if(priorCountdownCaptured) {
                        try { fans.SetCountdown(priorCountdown); }       catch { }
                    }
                }

                if(programWasSuspendedByUs && program != null) {
                    try { program.Resume(); } catch { }
                }
            }
        }
#endregion

#region Helpers
        private static void ApplyLevel(IFanArray fans, int percent) {
            if(percent >= 100) {
                // BIOS SetMaxFan only honours the command when the EC is not in
                // manual fan-level mode — otherwise the prior SetLevels write
                // pins the fans at Config.FanLevelMax (~3.4-3.8 kRPM) and the
                // sweep's "100 %" step undershoots the board's real ceiling by
                // ~30 % (issues #40, #41, #52). Release manual control briefly
                // so BIOS thermal can drive the fans to their physical limit;
                // we re-engage manual on the next sub-100 % step automatically
                // via SetLevels' Config.FanLevelNeedManual path.
                try { fans.SetManual(false); } catch { }
                fans.SetMax(true);
                return;
            }
            // Coming back down from a prior 100 % step: clear BIOS max-fan
            // first, then re-engage manual control so SetLevels takes effect.
            fans.SetMax(false);
            try { fans.SetManual(true); } catch { }
            // SetLevels takes the same units as the GUI trackbars: integer steps
            // up to Config.FanLevelMax (default 55, i.e. units of 100 RPM →
            // ~5.5k RPM full scale). The previous "percent * 5.5 / 100" produced
            // values 0–6 instead of 0–55, so the sweep barely moved the fans and
            // the EC diff was too small to reliably pick a tach register out of
            // the noise. Clamp to the configured maximum and convert through it.
            int clamped = Math.Max(0, Math.Min(100, percent));
            int level = (int) Math.Round(clamped * Config.FanLevelMax / 100.0);
            level = Math.Max(0, Math.Min(Config.FanLevelMax, level));
            byte fanLevel = (byte) level;
            try { fans.SetLevels(new byte[] { fanLevel, fanLevel }); } catch { }
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

        // Best-effort live tachometer read. Returns 0 if the call throws (no preset,
        // EC mutex collision, …) — the plateau detector treats 0 as "unknown" rather
        // than "plateaued", so a flaky read never causes a false plateau abort.
        private static int TryReadLiveSpeed(IFanArray fans, int fanIndex) {
            try {
                if(fans == null || fans.Fan == null || fanIndex < 0 || fanIndex >= fans.Fan.Length)
                    return 0;
                int rpm = fans.Fan[fanIndex].GetSpeed();
                return rpm > 0 ? rpm : 0;
            } catch {
                return 0;
            }
        }

        // RPM delta between adjacent steps below which we consider a fan to have
        // stopped responding. Picked above EcDiffScanner.RpmInversionTolerance (50)
        // so normal sample-to-sample jitter doesn't trip the abort, but well below
        // the ~1500-RPM gain a healthy fan produces between 70 % and 100 %.
        private const int PlateauRpmDelta = 150;

        // Minimum RPM the fans must have actually reached for plateau detection to
        // matter. Below this we're either still spinning up from idle (legitimately
        // small delta on 0 % → 30 %) or reading garbage from a wrong preset — neither
        // is a freeze risk worth aborting for.
        private const int PlateauMinRpm = 1500;

        // True when BOTH fans show <PlateauRpmDelta gain compared to the highest
        // prior commanded level, and the most recent reading is at least PlateauMinRpm
        // (so we don't trip on "fans haven't started yet" or "wrong preset returns 0").
        //
        // The "highest prior commanded level" choice is deliberate: comparing to the
        // immediately-previous step would miss the case where the user runs an extended
        // profile (0, 30, 50, 70) and the curve goes 0 → 30 (gain) → 50 (gain) → 70
        // (no gain). The detector picks the prior step with the largest level so it
        // always asks "did pushing the commanded fan rate higher actually do anything?"
        private static bool DetectPlateau(List<LiveSpeedSample> live, out string note) {
            note = null;
            if(live == null || live.Count < 2) return false;

            var current = live[live.Count - 1];
            if(current.CpuRpm < PlateauMinRpm && current.GpuRpm < PlateauMinRpm)
                return false;

            // Find the highest commanded level below current — that's our baseline.
            LiveSpeedSample prior = null;
            for(int k = 0; k < live.Count - 1; k++) {
                if(live[k].LevelPercent >= current.LevelPercent) continue;
                if(prior == null || live[k].LevelPercent > prior.LevelPercent)
                    prior = live[k];
            }
            if(prior == null) return false;

            int dCpu = current.CpuRpm - prior.CpuRpm;
            int dGpu = current.GpuRpm - prior.GpuRpm;

            // Both fans must plateau. If only one stops responding the other usually
            // indicates the curve still has headroom (asymmetric fan envelopes — common
            // on dual-fan systems with one larger heatsink), and aborting would discard
            // a legitimate higher-step measurement.
            if(dCpu >= PlateauRpmDelta || dGpu >= PlateauRpmDelta) return false;

            note = $"Live RPM at {current.LevelPercent} % matched {prior.LevelPercent} % within {PlateauRpmDelta} RPM "
                 + $"(CPU {prior.CpuRpm} → {current.CpuRpm}, GPU {prior.GpuRpm} → {current.GpuRpm}). "
                 + "The board appears to be at its physical fan ceiling; skipped the 100 % step to avoid "
                 + "the BIOS rate-limiter freeze observed on 8C30 / 8D07 / 8BAD.";
            return true;
        }

        private static string SafeProductId() {
            try { return new Settings().GetProduct() ?? "?"; }
            catch { return "?"; }
        }

        private static string SafeBiosBornDate() {
            // BIOS born-date (yyyymmdd, e.g. 20240625) — the same field the existing Probe
            // report uses, and reliably available without pulling in System.Management.
            // Reported as "BIOS Build Date" in the Markdown so maintainers don't mistake
            // it for an SMBIOS firmware version string.
            try { return Hw.Bios?.GetBornDate() ?? "?"; }
            catch { return "?"; }
        }
#endregion

#region Apply to Live Session
        // Publishes the scan result to OmenMon.Library.AutoCal — read by Fan.GetSpeed()
        // on every refresh tick — and persists it to a sidecar XML so the override
        // survives a restart without us touching the main OmenMon.xml.
        // Returns null on success, or a human-readable error string if the sidecar
        // write failed (typically ACL/UAC on a Program Files install). The in-memory
        // overrides are still applied either way — only persistence is at risk.
        private static string ApplyToLiveSession(EcDiffScanner.Result scan, string productId) {
            // Wipe any prior override (from a previous wizard run, the sidecar XML, or a
            // known-board prime) before publishing this run's results. Otherwise a scan
            // that finds only the CPU fan would leave a stale GPU mapping in place.
            // Then re-Prime() with the known-board defaults so a partial scan (one fan
            // detected) still leaves the *other* fan with a valid mapping instead of
            // falling back to the placeholder FanSpeedReg* in OmenMon.xml. The scan's
            // own results are written *after* Prime() so they always outrank the
            // built-in defaults on the fans that were actually detected.
            AutoCal.Clear();
            AutoCal.Prime(productId);

            // Each SetCpu/SetGpu call is an atomic reference swap, so the
            // GUI refresh tick reading concurrently never observes a half-written
            // (offset, mode) pair — it sees either the prior value or the new one.
            if(scan.CpuFan != null)
                AutoCal.SetCpu(scan.CpuFan.Offset, scan.CpuFan.Mode);
            if(scan.GpuFan != null)
                AutoCal.SetGpu(scan.GpuFan.Offset, scan.GpuFan.Mode);

            try {
                // Single source of truth for the sidecar path — same constant the
                // reader uses, so a rename can never cause a write/read mismatch.
                string path = AutoCal.SidecarPath;
                var sb = new StringBuilder();
                // ProductId is stamped into the root element so AutoCal.Load() can reject
                // a sidecar that came from a different machine (USB-stick installs, OneDrive
                // sync, swapped drives). XML-encode it defensively even though baseboard IDs
                // are alphanumeric in every sample we've seen — never trust an external string.
                string safeId = System.Security.SecurityElement.Escape(productId ?? "");
                sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
                sb.AppendLine($"<AutoCalibration ProductId=\"{safeId}\">");
                if(scan.CpuFan != null)
                    sb.AppendLine($"  <CpuFan offset=\"0x{scan.CpuFan.Offset:X2}\" mode=\"{scan.CpuFan.Mode}\" />");
                if(scan.GpuFan != null)
                    sb.AppendLine($"  <GpuFan offset=\"0x{scan.GpuFan.Offset:X2}\" mode=\"{scan.GpuFan.Mode}\" />");
                sb.AppendLine("</AutoCalibration>");
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                return null;
            } catch(UnauthorizedAccessException ex) {
                return "Access denied writing " + AutoCal.SidecarFileName + ": " + ex.Message;
            } catch(IOException ex) {
                return "I/O error writing " + AutoCal.SidecarFileName + ": " + ex.Message;
            } catch(Exception ex) {
                return ex.GetType().Name + " writing " + AutoCal.SidecarFileName + ": " + ex.Message;
            }
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

            // Hoist persistence failures to the top of the report. The session keeps
            // its in-memory override but a restart will lose it — users on a
            // C:\Program Files install without elevated rights are the most likely
            // to hit this and the symptom (next launch reverts to garbage RPM) is
            // confusing without the explicit heads-up here.
            if(!string.IsNullOrEmpty(o.SidecarError)) {
                sb.AppendLine("> **WARNING:** Could not save `" + AutoCal.SidecarFileName + "` — " + o.SidecarError + ".");
                sb.AppendLine("> Your calibration is active for this session but **will not survive a restart**.");
                sb.AppendLine("> Re-run OmenMon as Administrator, or move the install out of `C:\\Program Files`, then run the wizard again.");
                sb.AppendLine();
            }

            // Hoist plateau detection above the Device block so a maintainer reading
            // the issue immediately sees the wizard short-circuited and *why*. The
            // request is to add the ProductId to the safety list so future runs skip
            // 100 % proactively (the wizard already aborted *this* run, but the user's
            // tray menu / main-form max-fan toggle is still unguarded for them).
            if(o.PlateauReached) {
                sb.AppendLine("> **PLATEAU DETECTED at " + o.PlateauStepPercent + " %.** The wizard skipped the 100 % step.");
                if(!string.IsNullOrEmpty(o.PlateauNote))
                    sb.AppendLine("> " + o.PlateauNote);
                sb.AppendLine("> Please report this so `" + o.ProductId + "` can be added to `FanArray.HasMaxFanFreeze` for full protection.");
                sb.AppendLine();
            }

            sb.AppendLine("## Device");
            sb.AppendLine();
            sb.AppendLine($"- **Product ID:** `{o.ProductId}`");
            sb.AppendLine($"- **BIOS Build Date:** `{o.BiosBornDate}`");
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

            // Live tachometer readings taken at the end of each settle window via the
            // same Fan.GetSpeed() path the GUI uses for the main-form readouts. Useful
            // cross-check against the EcDiffScanner's diff-based register pick: if the
            // live RPM trajectory disagrees with the scanner's chosen offset, the preset
            // (or the scan) is wrong. Suppressed when no readings were captured (early
            // failure before the first step completed).
            if(o.LiveSpeeds != null && o.LiveSpeeds.Count > 0) {
                sb.AppendLine();
                sb.AppendLine("## Live RPM at Each Step");
                sb.AppendLine();
                sb.AppendLine("| Step | CPU RPM | GPU RPM |");
                sb.AppendLine("|-----:|--------:|--------:|");
                foreach(var s in o.LiveSpeeds) {
                    string cpu = s.CpuRpm > 0 ? s.CpuRpm.ToString() : "—";
                    string gpu = s.GpuRpm > 0 ? s.GpuRpm.ToString() : "—";
                    sb.AppendLine($"| {s.LevelPercent} % | {cpu} | {gpu} |");
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
