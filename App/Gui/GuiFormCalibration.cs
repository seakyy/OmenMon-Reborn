  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using OmenMon.AppCli;
using OmenMon.Hardware.Platform;
using OmenMon.Library;

namespace OmenMon.AppGui {

    // Modal Auto-Calibration Wizard form.
    //
    // Owns the lifetime of the background calibration task: it spins it up,
    // streams progress into the UI, lets the user cancel cleanly, and on success
    // copies the Markdown report to the clipboard / saves it to disk / opens
    // the GitHub issue page.
    public class GuiFormCalibration : Form {

#region Components
        private Label LblHeader;
        private Label LblPhase;
        private Label LblWarning;
        private ProgressBar PrgOverall;
        private TextBox TxtLog;
        private Button BtnStart;
        private Button BtnCancel;
        private Button BtnClose;
        private Button BtnCopyAgain;
        private CheckBox ChkCloseApps;
        private CheckBox ChkOpenIssue;
#endregion

        private CancellationTokenSource Cts;
        private CliOp.CalibrationOutcome LastOutcome;
        private readonly IFanArray Fans;
        private readonly FanProgram Program;

        // Worker handle so FormClosing can wait for a clean exit before the form is
        // disposed. Without this the worker keeps Invoke()-ing into a destroyed handle
        // and the fan sweep continues with no UI to cancel it.
        private Task WorkerTask;

        // Set to true the moment the form starts tearing down. The worker checks this
        // before every UI marshal — once the form is closing we stop touching controls
        // even though the cancellation token may take a tick or two to be observed.
        private volatile bool IsClosingDown;

        public GuiFormCalibration(IFanArray fans, FanProgram program = null) {
            this.Fans = fans;
            this.Program = program;
            BuildUi();
            this.FormClosing += OnFormClosing;
        }

#region UI Construction
        private void BuildUi() {
            this.Text = "Auto-Calibrate & Diagnose";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MinimizeBox = false;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.ClientSize = new Size(560, 420);

            LblHeader = new Label {
                Text = "OmenMon Auto-Calibration Wizard",
                Font = new Font("Segoe UI", 12F, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(12, 10)
            };

            LblWarning = new Label {
                Text =
                    "This will run your fans through several speed steps (including 100 %) for about a minute.\r\n" +
                    "Save your work, expect noise, and keep the laptop on a hard surface.\r\n\r\n" +
                    "OmenMon will read the Embedded Controller at each step and figure out\r\n" +
                    "which registers report fan RPM on this specific board.",
                Location = new Point(14, 38),
                Size = new Size(534, 80)
            };

            ChkCloseApps = new CheckBox {
                Text = "Pause background apps that compete for the CPU/GPU during the test",
                Location = new Point(14, 122),
                Size = new Size(534, 22),
                Checked = true
            };

            ChkOpenIssue = new CheckBox {
                Text = "When done, open the GitHub new-issue page so I can paste the report",
                Location = new Point(14, 144),
                Size = new Size(534, 22),
                Checked = true
            };

            LblPhase = new Label {
                Text = "Ready.",
                Location = new Point(14, 176),
                Size = new Size(534, 20),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold)
            };

            PrgOverall = new ProgressBar {
                Location = new Point(14, 200),
                Size = new Size(534, 20),
                Minimum = 0,
                Maximum = 100
            };

            TxtLog = new TextBox {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Location = new Point(14, 230),
                Size = new Size(534, 138),
                Font = new Font("Consolas", 8.5F)
            };

            BtnStart = new Button {
                Text = "Start",
                Location = new Point(14, 380),
                Size = new Size(110, 28)
            };
            BtnStart.Click += (s, e) => Start();

            BtnCancel = new Button {
                Text = "Cancel",
                Location = new Point(130, 380),
                Size = new Size(110, 28),
                Enabled = false
            };
            BtnCancel.Click += (s, e) => { Cts?.Cancel(); Log("Cancellation requested…"); };

            BtnCopyAgain = new Button {
                Text = "Copy report",
                Location = new Point(308, 380),
                Size = new Size(120, 28),
                Enabled = false
            };
            BtnCopyAgain.Click += (s, e) => {
                if(LastOutcome?.Markdown != null) {
                    try { Clipboard.SetText(LastOutcome.Markdown); Log("Report copied to clipboard."); }
                    catch(Exception ex) { Log("Clipboard error: " + ex.Message); }
                }
            };

            BtnClose = new Button {
                Text = "Close",
                Location = new Point(438, 380),
                Size = new Size(110, 28),
                DialogResult = DialogResult.Cancel
            };
            this.CancelButton = BtnClose;

            this.Controls.AddRange(new Control[] {
                LblHeader, LblWarning, ChkCloseApps, ChkOpenIssue,
                LblPhase, PrgOverall, TxtLog,
                BtnStart, BtnCancel, BtnCopyAgain, BtnClose
            });
        }
#endregion

#region Run Loop
        private void Start() {
            BtnStart.Enabled = false;
            BtnCancel.Enabled = true;
            BtnClose.Enabled = false;
            ChkCloseApps.Enabled = false;
            ChkOpenIssue.Enabled = false;
            PrgOverall.Value = 0;
            TxtLog.Clear();

            // Snapshot all checkbox state on the UI thread before the worker spins up.
            // The worker's finally{} runs on a thread-pool thread and must not touch
            // WinForms controls — doing so throws InvalidOperationException, which would
            // skip ResumeHeavyHitters() and leave throttled processes pinned at Idle.
            bool throttle = ChkCloseApps.Checked;

            if(throttle)
                ProcessGuard.PauseHeavyHitters(line => Log(line));

            Cts = new CancellationTokenSource();
            var token = Cts.Token;
            var fans = this.Fans;
            var program = this.Program;

            // Run on a thread-pool task so the UI thread stays responsive. Progress is
            // marshalled back via Invoke; we never touch controls from the worker. Every
            // marshal is gated on IsClosingDown so a user dismissing the form mid-run
            // doesn't trip an ObjectDisposedException on the (now-destroyed) handle.
            WorkerTask = Task.Run(() => {
                try {
                    var outcome = CliOp.AutoCalibrate(
                        progress: (phase, detail, pct) => SafeInvoke(() => {
                            LblPhase.Text = phase + " — " + detail;
                            PrgOverall.Value = Math.Max(0, Math.Min(100, pct));
                            Log($"[{pct,3}%] {detail}");
                        }),
                        cancel: token,
                        fans: fans,
                        program: program);

                    SafeInvoke(() => Finished(outcome));
                } catch(Exception ex) {
                    SafeInvoke(() => {
                        Log("Fatal: " + ex);
                        ResetButtons(success: false);
                    });
                } finally {
                    if(throttle)
                        ProcessGuard.ResumeHeavyHitters();
                }
            });
        }

        // Marshals an action onto the UI thread, but only while the form is alive.
        // Once IsClosingDown is set or the handle is gone, drops the call silently.
        private void SafeInvoke(Action action) {
            if(IsClosingDown || IsDisposed || !IsHandleCreated) return;
            try {
                Invoke(action);
            } catch(ObjectDisposedException) {
                // Form was disposed between the gate check above and the actual Invoke —
                // benign; the FormClosing handler is responsible for cleanup.
            } catch(InvalidOperationException) {
                // Handle was destroyed concurrently — same situation, same handling.
            }
        }

        // Cancels any in-flight calibration cleanly and waits for the worker to
        // exit before the form's handle is destroyed. Without this the worker
        // keeps invoking into a disposed form and the fan sweep continues
        // headless — which is exactly the "ghost background task" failure mode.
        private void OnFormClosing(object sender, FormClosingEventArgs e) {
            if(WorkerTask == null || WorkerTask.IsCompleted) return;

            // Tell the worker to stand down on the next 1 s tick, and stop accepting
            // any further UI marshals from progress callbacks.
            IsClosingDown = true;
            try { Cts?.Cancel(); } catch { }

            // Wait up to 20 s for the worker to clean up — long enough to cover the
            // worst-case "between two 12 s settle ticks" window. We do this on the UI
            // thread (which is fine because the worker no longer needs Invoke), and
            // pump messages so the form doesn't appear frozen during the wait.
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while(!WorkerTask.IsCompleted && DateTime.UtcNow < deadline) {
                Application.DoEvents();
                System.Threading.Thread.Sleep(50);
            }

            // If the worker still hasn't stopped, refuse to close. Destroying the
            // form's handle while the sweep is mid-step would leave the fans driven
            // by a headless task with no UI to observe or cancel it. Surface a
            // "still stopping" hint so the user knows the dialog hasn't frozen.
            if(!WorkerTask.IsCompleted) {
                e.Cancel = true;
                try {
                    LblPhase.Text = "Still stopping calibration… please wait.";
                } catch { }
            }
        }

        private void Finished(CliOp.CalibrationOutcome outcome) {
            LastOutcome = outcome;
            if(!outcome.Success) {
                Log("Calibration failed: " + outcome.FailureReason);
                ResetButtons(success: false);
                return;
            }

            Log("");
            Log("=== RESULT ===");
            if(outcome.Scan?.CpuFan != null)
                Log($"CPU fan: 0x{outcome.Scan.CpuFan.Offset:X2} ({outcome.Scan.CpuFan.Mode})");
            else
                Log("CPU fan: not detected");
            if(outcome.Scan?.GpuFan != null)
                Log($"GPU fan: 0x{outcome.Scan.GpuFan.Offset:X2} ({outcome.Scan.GpuFan.Mode})");

            try { Clipboard.SetText(outcome.Markdown); Log("Report copied to clipboard."); }
            catch(Exception ex) { Log("Clipboard error: " + ex.Message); }

            try { string p = CliOp.SaveCalibrationReport(outcome.Markdown); Log("Saved: " + p); }
            catch(Exception ex) { Log("Save error: " + ex.Message); }

            if(ChkOpenIssue.Checked) {
                try { Process.Start("https://github.com/seakyy/OmenMon-Reborn/issues/new"); }
                catch { }
            }

            MessageBox.Show(this,
                outcome.Scan != null && outcome.Scan.IsPlausible
                    ? "Calibration succeeded. The report is on your clipboard — paste it into the GitHub issue."
                    : "Calibration finished but no fan registers were confidently identified. The full dump is on your clipboard; please open an issue with the contents.",
                Config.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);

            ResetButtons(success: true);
        }

        private void ResetButtons(bool success) {
            BtnStart.Enabled = !success;
            BtnCancel.Enabled = false;
            BtnClose.Enabled = true;
            BtnCopyAgain.Enabled = success && LastOutcome?.Markdown != null;
            ChkCloseApps.Enabled = !success;
            ChkOpenIssue.Enabled = !success;
        }

        private void Log(string line) {
            if(InvokeRequired) { Invoke((Action) (() => Log(line))); return; }
            TxtLog.AppendText(line + Environment.NewLine);
        }
#endregion

    }
}
