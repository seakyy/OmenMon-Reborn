  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/

using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using OmenMon.External;
using OmenMon.Hardware.Bios;
using OmenMon.Hardware.Ec;
using OmenMon.Hardware.Platform;
using OmenMon.Library;

namespace OmenMon.AppGui {

    // Implements a backend for GUI mode operations
    public class GuiOp {

        // Sensors class reference
        internal Platform Platform;

        // Fan program class reference
        internal FanProgram Program;

        // Parent class reference
        private GuiTray Context;

        // Serialises every multi-step hardware *control* sequence (the background monitor
        // pass AND user-initiated UI handlers) so the fan/thermal/EC-write sequences never
        // interleave across threads. The cross-process Global\Access_EC mutex still
        // serialises raw EC port I/O; this in-process lock serialises the higher-level
        // sequences that release the EC mutex between their individual steps. Always the
        // outermost lock (acquired before any EC mutex), so the ordering is consistent and
        // deadlock-free.
        internal readonly object HardwareLock = new object();

        // True while the main form's constant-speed mode is engaged. Read by the monitor
        // thread, which then maintains the fan countdown (the logic that used to live on
        // the UI thread in GuiFormMain.UpdateFan, gated on RdoFanConst.Checked).
        internal volatile bool ConstantSpeedActive;

        // Flag to indicate if running on full power.
        // Volatile: written by the monitor thread (PowerChange) / AutoConfig and read by the
        // UI thread (AC-flicker passive poll), so it needs a defined publish without a lock.
        private volatile bool _fullPower;
        public bool FullPower { get { return this._fullPower; } private set { this._fullPower = value; } }

        // Thermal panic: true while max temperature is above the configured threshold.
        // Volatile: written by the monitor thread (CheckThermalPanic) and read by the UI
        // thread (tray tooltip / snapshot) and elsewhere.
        private volatile bool _isThermalPanic;
        public bool IsThermalPanic { get { return this._isThermalPanic; } private set { this._isThermalPanic = value; } }

        // Constructs the operation-running class
        public GuiOp(GuiTray context) {

            // Initialize the parent class reference
            this.Context = context;

            // Initialize the BIOS and the Embedded Controller
            Hw.BiosInit();
            Hw.EcInit();

            // Initialize the hardware platform
            this.Platform = new Platform();

            // Initialize the fan program
            this.Program = new FanProgram(this.Platform, FanProgramCallback);

            // Set the full power flag using the multi-source check so a boot that
            // happens to land mid-flicker (issue #70) does not load the alternate
            // fan program by mistake.
            this.FullPower = this.Platform.System.IsFullPowerConfirmed();

        }

        // Shows the about dialog
        public static void About(string title = "", string text = "") {
            (new GuiFormAbout(title, text)).ShowDialog();
        }

        // Automatically applies the configuration on startup
        public void AutoConfig() {

            // Set whether the application should start automatically with Windows
            // (Task Scheduler, not an EC/BIOS call — kept outside the hardware lock)
            Hw.TaskSet(Config.TaskId.Gui, Config.AutoStartup);

            // AutoConfig runs on its own background thread, concurrently with the monitor
            // thread, so its hardware control sequence is serialised under HardwareLock.
            lock(this.HardwareLock) {

                // Apply the default GPU power settings
                this.Platform.System.SetGpuPower(
                    new BiosData.GpuPowerData(
                        (BiosData.GpuPowerLevel)
                            Enum.Parse(typeof(BiosData.GpuPowerLevel), Config.GpuPowerDefault)));

                // Apply the default fan program, or the alternative program if no AC.
                // Re-sample FullPower with the multi-source check at this moment so the
                // startup window between the constructor and AutoConfig running on a
                // background thread cannot land on the alternate program due to a
                // transient AC-flicker (issue #70).
                this.FullPower = this.Platform.System.IsFullPowerConfirmed();
                if(this.FullPower)
                    this.Program.Run(Config.FanProgramDefault);
                else
                    this.Program.Run(Config.FanProgramDefaultAlt, true);

            }

            // Update the main form, if visible (effectively a no-op at startup — the form
            // is not created yet; the periodic snapshot render covers it otherwise)
            if(Context.FormMain != null && Context.FormMain.Visible)
                Context.FormMain.UpdateFanCtl();

        }

        // Starts the automatic configuration in another thread
        // so as not to increase the application loading time
        public void AutoConfigRun() {

            // Quiet EC-lock handling on this thread: at logon HP's own services
            // frequently hold Global\Access_EC for a while, and AutoConfig's initial
            // fan-program application hitting that window was the once-per-startup
            // "Failed to acquire embedded controller exclusive lock" box (issue #94).
            // A skipped write self-heals — the monitor re-applies the program curve
            // within one program interval. The flag is thread-static and the thread
            // dedicated, so user-initiated actions on the UI thread stay loud.
            Thread autoConfig = new Thread(() => {
                Hw.EcLockQuiet = true;
                this.AutoConfig();
            });
            autoConfig.IsBackground = true;
            autoConfig.Start();

        }

        // Keeps updating the status as the fan program runs. The program now ticks on the
        // background monitor thread, so the status is handed to the UI thread via the
        // monitor's message queue (drained by the tray timer tick, rendered in
        // GuiTray.HandleProgramStatus) rather than touching the tray icon / main form
        // directly — an off-thread WinForms access would throw. Verbose messages are still
        // silently ignored in GUI mode (run OmenMon -Prog <Name> from the CLI to see them).
        public void FanProgramCallback(FanProgram.Severity severity, string message) {
            if(severity == FanProgram.Severity.Important
                || severity == FanProgram.Severity.Notice)
                Context.Monitor?.EnqueueProgramStatus(severity, message);
        }

        // Cycles through the keyboard color presets defined in OmenMon.xml
        // OmenMon-Reborn additions © 2026 seakyy
        public void CycleColorPresets() {

            // Nothing to do if no presets are defined
            if(Config.ColorPreset.Count == 0)
                return;

            var keys = new System.Collections.Generic.List<string>(Config.ColorPreset.Keys);

            // Determine the currently-active preset name
            string currentPreset = "";
            if(Context.FormMain != null && Context.FormMain.Kbd != null)
                currentPreset = Context.FormMain.Kbd.GetPreset();
            else {
                BiosData.ColorTable colorNow = this.Platform.System.GetKbdColor();
                foreach(string name in Config.ColorPreset.Keys)
                    if(colorNow.Zone[0].Value == Config.ColorPreset[name].Zone[0].Value
                        && colorNow.Zone[1].Value == Config.ColorPreset[name].Zone[1].Value
                        && colorNow.Zone[2].Value == Config.ColorPreset[name].Zone[2].Value
                        && colorNow.Zone[3].Value == Config.ColorPreset[name].Zone[3].Value) {
                        currentPreset = name;
                        break;
                    }
            }

            // Select the next preset, wrapping around to the first
            int idx = keys.IndexOf(currentPreset);
            string nextPreset = keys[(idx + 1) % keys.Count];

            // Apply the preset via GuiKbd when the main form is open,
            // otherwise fall back to a direct BIOS call
            if(Context.FormMain != null && Context.FormMain.Kbd != null) {
                Context.FormMain.Kbd.SetColors(Config.ColorPreset[nextPreset]);
                Context.FormMain.UpdateKbd();
            } else {
                this.Platform.System.SetKbdColor(Config.ColorPreset[nextPreset]);
            }

            // Show a balloon tip unless silent mode is on
            if(!Config.KeyToggleColorPresetSilent)
                Context.ShowBalloonTip(
                    Config.Locale.Get(Config.L_GUI_MENU + Gui.M_SUB + Gui.G_KBD) + ": " + nextPreset);

        }

        // Launches when the Omen key has been pressed
        public void KeyHandler(Gui.MessageParam lastParam) {

            // Cycle color presets (takes priority over all other key actions)
            if(Config.KeyToggleColorPreset) {

                // Serialise the keyboard-colour read/write with the monitor thread
                lock(this.HardwareLock)
                    CycleColorPresets();

            // If Omen key is set
            // to toggle fan program
            } else if(Config.KeyToggleFanProgram) {

                // Show the form on first press
                // if configured to do so and not already shown
                if(Config.KeyToggleFanProgramShowGuiFirst &&
                    (Context.FormMain == null || !Context.FormMain.Visible))
                    Context.ShowFormMain();

                else {

                    // Fan-program control runs under HardwareLock to serialise with the
                    // background monitor's program tick.
                    lock(this.HardwareLock) {

                        // Configured to cycle
                        // through all fan programs
                        if(Config.KeyToggleFanProgramCycleAll) {

                            // Default to the first fan program
                            string next = Config.FanProgram.Keys[0];

                            // If a program is running,
                            // cycle to the next one, if exists
                            if(this.Program.IsEnabled)
                                try {
                                    next = Config.FanProgram.Keys[
                                        Config.FanProgram.IndexOfKey(this.Program.GetName()) + 1];
                                } catch { }

                            // Run the next fan program
                            this.Program.Run(next);

                        // Configured to toggle
                        // default fan program on and off
                        } else {

                            // Terminate a program, if there is one running
                            if(this.Program.IsEnabled)
                                this.Program.Terminate();

                            // Run the default program, if no program running
                            else
                                this.Program.Run(Config.FanProgramDefault);

                        }

                    }

                    // Update the main form fan controls (if main form is being shown),
                    // from a freshly published snapshot so the change is reflected at once
                    if(Context.FormMain != null && Context.FormMain.Visible) {
                        Context.Monitor?.SampleNow();
                        Context.FormMain.UpdateFanCtl();
                    }

                    // Otherwise, show a balloon tip notification
                    // unless configured to toggle programs silently
                    else if(!Config.KeyToggleFanProgramSilent)
                        this.FanProgramCallback(
                            FanProgram.Severity.Important,
                            this.Program.IsEnabled ?
                                Config.Locale.Get(Config.L_PROG) + ": " + this.Program.GetName()
                                : Config.Locale.Get(Config.L_PROG + "End"));

                }

            // If Omen key action is set
            // to trigger a custom action
            } else if(Config.KeyCustomActionEnabled) {

                // Launch the action
                Process customAction = new Process();
                customAction.StartInfo.FileName = Config.KeyCustomActionExecCmd;
                customAction.StartInfo.Arguments = Config.KeyCustomActionExecArgs;
                customAction.StartInfo.UseShellExecute = false; // Required for environment change
                customAction.StartInfo.WindowStyle = Config.KeyCustomActionMinimized ?
                    ProcessWindowStyle.Minimized : ProcessWindowStyle.Normal;
                customAction.Start();

            } else {

                // Just toggle the main form
                Context.ToggleFormMain();

            }

        }

        // Silently clears the Thermal Panic state and restores fan control.
        // Used when the dynamic icon is disabled while panic is active — no balloon tip is shown
        // because the trigger was a config change, not a temperature drop.
        public void ClearThermalPanic() {
            if(IsThermalPanic) {
                IsThermalPanic = false;
                Platform.Fans.SetMax(false);
            }
        }

        // Checks temperature and activates or deactivates Thermal Panic Mode
        public void CheckThermalPanic(byte maxTemp) {

            // If panic is currently active but the feature has been disabled (e.g. user toggled
            // it off at runtime or the dynamic icon was turned off), clear the stuck state so
            // fans are not left at max indefinitely.
            if(!Config.ThermalPanicEnabled) {
                if(IsThermalPanic) {
                    IsThermalPanic = false;
                    Platform.Fans.SetMax(false);
                }
                return;
            }

            if(!IsThermalPanic && maxTemp >= Config.ThermalPanicTemperature) {

                IsThermalPanic = true;
                Platform.Fans.SetMax(true);

                string tempStr = Config.TemperatureUseFahrenheit
                    ? ((maxTemp * 9 / 5) + 32) + "°F (" + maxTemp + "°C)"
                    : maxTemp + "°C";
                // Runs on the monitor thread → hand the balloon to the UI thread via the queue
                Context.Monitor?.EnqueueBalloon(
                    "⚠ " + tempStr + " — both fans forced to maximum!",
                    "OmenMon — Thermal Panic",
                    ToolTipIcon.Warning);

            } else if(IsThermalPanic) {

                // Guard against Temperature=0 underflow; clamp hysteresis below threshold.
                int maxHysteresis = Config.ThermalPanicTemperature == 0
                    ? 0 : Config.ThermalPanicTemperature - 1;
                int offThreshold = Config.ThermalPanicTemperature
                    - Math.Min((int)Config.ThermalPanicHysteresis, maxHysteresis);

                // Use <= so temperature normalized at exactly (threshold - hysteresis) clears
                if(maxTemp <= offThreshold) {

                    IsThermalPanic = false;
                    Platform.Fans.SetMax(false);

                    string tempStr = Config.TemperatureUseFahrenheit
                        ? ((maxTemp * 9 / 5) + 32) + "°F (" + maxTemp + "°C)"
                        : maxTemp + "°C";
                    // Runs on the monitor thread → hand the balloon to the UI thread via the queue
                    Context.Monitor?.EnqueueBalloon(
                        "Temperature normalized (" + tempStr + ") — fan control restored.",
                        "OmenMon — Thermal Panic",
                        ToolTipIcon.Info);

                }

            }

        }

        // Responds to power-mode status change events
        public void PowerChange() {

            // Use the multi-source check so a residual AC-flicker that escapes
            // the GuiTray confirmation gate (issue #70) does not flip the fan
            // program after the gate has decided to act. If the firmware or the
            // charging-flag still report AC, we treat the line-status reading
            // as noise and leave the program untouched.
            bool live = this.Platform.System.IsFullPowerConfirmed();
            bool changed = this.FullPower != live;

            // Always refresh the cached state so the passive poll in
            // GuiTray.EventTimerTick (which compares against Op.FullPower) does
            // not re-queue the same deferred change every tick when AutoConfig
            // is off or no fan program is active.
            this.FullPower = live;

            // Only if a fan program is active, if configured to do so,
            // and if the power state actually changed from the last-recorded
            if(changed && Config.AutoConfig && this.Program.IsEnabled) {

                // Apply the default fan program,
                // or the alternative program if no AC
                if(this.FullPower)
                    this.Program.Run(Config.FanProgramDefault);
                else
                    this.Program.Run(Config.FanProgramDefaultAlt, true);

            }

            // The system-info panel reflects the new power state via the next snapshot
            // render (this method now runs on the monitor thread under HardwareLock, so it
            // must not touch the form directly).

        }

        // Responds to the system entering and resuming from low-power state events
        public uint SuspendResumeCallback(IntPtr context, uint type, IntPtr setting) {

            // System is resuming from suspend
            if(type == PowrProf.PBT_APMRESUMEAUTOMATIC)

                // Resume the fan program
                this.Program.Resume();

            // System is about to be suspended
            // and a fan program is running
            else if(type == PowrProf.PBT_APMSUSPEND)

                // Suspend the fan program
                this.Program.Suspend();

            return 0;

        }

        // Shows a modal "are you sure?" dialog for models whose firmware
        // freezes at 100% fan speed (FanArray.HasMaxFanFreeze), with
        // consistent wording / title across every entry point that can
        // request maximum fans (tray menu, main-form slider, anything else
        // added later). Returns true if the caller should proceed, false
        // if the user declined or no warning was needed.
        //
        // Centralised here so the message stays in sync — previously the
        // dialog text lived in two GUI files and any tweak to the wording
        // had to be made in both.
        public static bool ConfirmMaxFanIfRisky(string productId) {

            if(!FanArray.HasMaxFanFreeze(productId))
                return true;

            DialogResult result = MessageBox.Show(
                $"Warning: Model {productId} has a known firmware issue where setting fans to 100% can cause an unrecoverable EC freeze requiring a restart.\n\n" +
                "Do you want to proceed anyway?",
                "Fan Safety Warning",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            return result == DialogResult.Yes;

        }

    }

}
