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

        public GuiFormCalibration(IFanArray fans) {
            this.Fans = fans;
            BuildUi();
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

            if(ChkCloseApps.Checked)
                ProcessGuard.PauseHeavyHitters(line => Log(line));

            Cts = new CancellationTokenSource();
            var token = Cts.Token;
            var fans = this.Fans;

            // Run on a thread-pool task so the UI thread stays responsive.
            // Progress is marshalled back via Invoke; we never touch controls from the worker.
            Task.Run(() => {
                try {
                    var outcome = CliOp.AutoCalibrate(
                        progress: (phase, detail, pct) => Invoke((Action) (() => {
                            LblPhase.Text = phase + " — " + detail;
                            PrgOverall.Value = Math.Max(0, Math.Min(100, pct));
                            Log($"[{pct,3}%] {detail}");
                        })),
                        cancel: token,
                        fans: fans);

                    Invoke((Action) (() => Finished(outcome)));
                } catch(Exception ex) {
                    Invoke((Action) (() => {
                        Log("Fatal: " + ex);
                        ResetButtons(success: false);
                    }));
                } finally {
                    if(ChkCloseApps.Checked)
                        ProcessGuard.ResumeHeavyHitters();
                }
            });
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
