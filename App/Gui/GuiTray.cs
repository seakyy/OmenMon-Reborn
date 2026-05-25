  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/

using System;
using System.Windows.Forms;
using Microsoft.Win32;
using OmenMon.Hardware.Bios;
using OmenMon.Hardware.Ec;
using OmenMon.Library;

namespace OmenMon.AppGui {

    // Implements an application context running in the background
    // with an icon in the notification (system tray) area
    public class GuiTray : ApplicationContext {

#region Data
        // Stores the first instance's context,
        // so that it can be accessed from elsewhere
        internal static GuiTray Context { get; private set; }

        // Stores the component container for later disposal
        private System.ComponentModel.IContainer Components;

        // Stores the message filter so that it can be removed upon exit
        private IMessageFilter Filter;

        // Stores the main GUI form
        internal GuiFormMain FormMain;

        // Stores the class managing the dynamic notification icon
        internal GuiIcon Icon;

        // Stores the menu
        internal GuiMenu Menu;

        // Stores the notification area icon class
        internal NotifyIcon Notification;

        // Stores the operation-running class
        internal GuiOp Op;

        // Stores the system timer
        private System.Windows.Forms.Timer Timer;

        // Stores the BIOS heartbeat timer
        private System.Windows.Forms.Timer HeartbeatTimer;

        // AC-flicker debounce (issue #70). When a PowerModeChanged StatusChange
        // event fires and Config.AcFlickerGuard is on, we record the UTC time
        // here and defer the actual reaction (fan-program switch, heartbeat
        // enable/disable, main-form refresh) until the next timer tick that
        // arrives after Config.AcFlickerHoldMs. If the line status reverts in
        // the meantime, Op.PowerChange() naturally no-ops because IsFullPower()
        // matches Op.FullPower again. MinValue means no event is pending.
        private DateTime PendingPowerChangeAt = DateTime.MinValue;

        // Origin timestamp of the *first* deferral in a cascade (multi-sample
        // confirmation may re-queue several times). Capped against
        // Config.AcFlickerMaxDeferralMs so a pathological flapper still reaches
        // a decision in bounded time. MinValue means no cascade is active.
        private DateTime PendingPowerChangeOriginAt = DateTime.MinValue;

        // Last PowerLineStatus.Online observed by the passive poll. Used to
        // make the poll edge-triggered (queue only on transitions) so a
        // sustained "Windows says Offline / BIOS says AC" pathology — where
        // Op.FullPower remains true via the multi-source check but IsFullPower
        // remains false — does not create a never-ending queue/confirm/no-op
        // cycle. Defaults to true (the post-AutoConfig assumption) and is
        // re-baselined every tick the poll runs.
        private bool LastPassivePollOnline = true;
        private bool LastPassivePollInitialized = false;

        // Multi-sample confirmation state (issue #70). The end-of-hold confirmation
        // takes one IsFullPowerConfirmed sample per EventTimerTick rather than
        // sleeping between reads, so the WinForms UI thread is never blocked during
        // a confirmation cycle (Copilot review #2 on the v1.4.2 PR). All three are
        // touched only from the UI thread (the timer tick), so no synchronisation is
        // needed. ConfirmInProgress=false means no confirmation is running.
        private bool ConfirmInProgress = false;
        private bool ConfirmExpectedFullPower = false;
        private int ConfirmSamplesRemaining = 0;
        private DateTime ConfirmLastSampleAt = DateTime.MinValue;

        // Stores the number of ticks elapsed since
        // the last update of a particular category
        internal int UpdateIconTick;
        internal int UpdateMonitorTick;
        internal int UpdateProgramTick;
#endregion

#region Construction & Disposal
        // Constructs the tray notification application context
        public GuiTray () {

            // Retain the context for future use
            if(Context == null)
                Context = this;

            // Initialize the component model container
            this.Components = new System.ComponentModel.Container();

            // Create the notification icon
            this.Notification = new NotifyIcon(this.Components) {
                ContextMenuStrip = new ContextMenuStrip(Components),
                Icon = OmenMon.Resources.IconTray,
                Text = Config.AppName + " " + Config.AppVersion,
                Visible = true
            };

            // Initialize the operation-running class
            this.Op = new GuiOp(Context);

            // Initialize the icon management class
            this.Icon = new GuiIcon(Context);
            Update();

            // Initialize the menu class
            this.Menu = new GuiMenu(Context);

            // Define event handlers
            this.Notification.ContextMenuStrip.Closing += Menu.EventClosing;
            this.Notification.ContextMenuStrip.ItemClicked += Menu.EventItemClicked;
            this.Notification.ContextMenuStrip.Opening += Menu.EventOpening;
            this.Notification.MouseClick += EventIconMouseClick;

            // Add a filter to intercept any custom messages
            this.Filter = new GuiFilter(Context);
            Application.AddMessageFilter(this.Filter);

            // Receive suspend and resume event notifications
            // only if configured to suspend and resume fan program
            if(Config.FanProgramSuspend)
                Gui.RegisterSuspendResumeNotification(this.Op.SuspendResumeCallback);

            // Set up the timer
            this.Timer = new System.Windows.Forms.Timer(Components);
            this.Timer.Interval = Config.GuiTimerInterval;
            this.Timer.Tick += EventTimerTick;
            this.Timer.Enabled = true;

            // Set up the BIOS heartbeat timer if enabled
            if(Config.BiosHeartbeatInterval > 0) {
                this.HeartbeatTimer = new System.Windows.Forms.Timer(Components);
                this.HeartbeatTimer.Interval = Config.BiosHeartbeatInterval;
                this.HeartbeatTimer.Tick += EventHeartbeatTick;
                // Pause immediately if starting on battery (prevents hibernate on battery).
                // Multi-source check so a boot landing mid-flicker (issue #70) doesn't
                // start the heartbeat off and then never re-enable it because no
                // PowerModeChanged ever fires for the recovery.
                this.HeartbeatTimer.Enabled = !Config.BiosHeartbeatPauseOnBattery
                    || this.Op.Platform.System.IsFullPowerConfirmed();
            }

            // Show the main form if requested by the environment variable
            if(Environment.GetEnvironmentVariable(Config.EnvVarSelfName) != null
                && Environment.GetEnvironmentVariable(Config.EnvVarSelfName).Contains(Config.EnvVarSelfValueKey))

                this.Op.KeyHandler(Gui.MessageParam.NoLastParam);

            // Unset the environment variable, so that
            // it does not propagate to child processes
            Environment.SetEnvironmentVariable(Config.EnvVarSelfName, null);

            // Automatically apply settings, if enabled
            if(Config.AutoConfig)
                this.Op.AutoConfigRun();

            // Register the power-mode change event handler
            SystemEvents.PowerModeChanged += EventPowerChange;

            // Battery-glitch hibernation guard — issue #59. Records the
            // baseline Windows-reported battery percentage now so the very
            // next timer tick has something to compare against; nothing
            // detected on the first tick is the correct behaviour.
            PowerGuard.Initialize();

        }

        // Handles component disposal
        protected override void Dispose(bool isDisposing) {

            // Release any held ES_SYSTEM_REQUIRED before the process exits.
            // Windows would drop it on its own at process exit too, but being
            // explicit survives non-graceful shutdown paths (e.g. fatal exception
            // in Update() leading to ExitThreadCore being skipped).
            PowerGuard.Dispose();

            if(isDisposing && this.Components != null)
                this.Components.Dispose();

            // Perform the usual tasks
            base.Dispose(isDisposing);

        }

        // Handles exit tasks
        protected override void ExitThreadCore() {

            // The icon has to be removed beforehand,
            // otherwise it will linger in the tray
            if(this.Notification != null)
                this.Notification.Visible = false;

            // Remove the message filter
            Application.RemoveMessageFilter(this.Filter);
            this.Filter = null;

            // Unregister the power-mode change event handler
            SystemEvents.PowerModeChanged -= EventPowerChange;

            // Stop receiving power event notifications
            Gui.UnregisterSuspendResumeNotification();

            // Terminate the fan program, if any
            if(this.Op.Program.IsEnabled)
                this.Op.Program.Terminate();

            // Perform the usual tasks
            base.ExitThreadCore();

        }
#endregion

#region Event Handlers
        // Handles a click event on the notification icon
        private void EventIconMouseClick(object sender, MouseEventArgs e) {

            // Toggle the main GUI form on left click
            // Note: right click is reserved for the context menu
            if(e.Button == MouseButtons.Left)
                ToggleFormMain();

        }

        // Handles a power-mode change event
        private void EventPowerChange(object sender, PowerModeChangedEventArgs e) {

            // Only respond to status change events,
            // which excludes Resume and Suspend
            if(e.Mode != PowerModes.StatusChange)
                return;

            // AC-flicker debounce (issue #70). Some HP Omen / Victus SKUs briefly
            // report AC as disconnected even though the laptop is physically plugged
            // in; reacting immediately causes the AutoConfig path to switch between
            // FanProgramDefault and FanProgramDefaultAlt during the flicker, which
            // mid-game shows up as a visible CPU/power-throttling stutter. With the
            // guard enabled, the actual handler runs from the next EventTimerTick
            // after AcFlickerHoldMs has elapsed; if the line status has reverted by
            // then, Op.PowerChange() naturally no-ops. The heartbeat enable/disable
            // is part of the same deferred handler so a flicker doesn't churn it.
            if(Config.AcFlickerGuard && Config.AcFlickerHoldMs > 0) {
                QueueDeferredPowerChange();
                return;
            }

            ApplyPowerStatusChange();

        }

        // Records (or refreshes) a deferred power-change request. Distinguishes the
        // origin timestamp (used to cap the cascade against
        // Config.AcFlickerMaxDeferralMs) from the per-attempt timestamp (used by
        // EventTimerTick to know when the current hold window elapses).
        private void QueueDeferredPowerChange() {
            DateTime now = DateTime.UtcNow;
            this.PendingPowerChangeAt = now;
            if(this.PendingPowerChangeOriginAt == DateTime.MinValue)
                this.PendingPowerChangeOriginAt = now;
            // A fresh (or refreshed) deferral restarts the hold-then-confirm cycle,
            // so discard any in-flight multi-sample confirmation. Otherwise a new
            // PowerModeChanged event arriving mid-confirmation would leave
            // ConfirmInProgress=true, and the next hold-window expiry would resume
            // sampling against a stale ConfirmExpectedFullPower captured during the
            // previous attempt instead of re-sampling fresh (Copilot re-review on
            // the v1.4.2 PR).
            ResetConfirmState();
        }

        // Resets the multi-sample confirmation state machine.
        private void ResetConfirmState() {
            this.ConfirmInProgress = false;
            this.ConfirmSamplesRemaining = 0;
            this.ConfirmLastSampleAt = DateTime.MinValue;
        }

        // Runs the debounced (or immediate, when AcFlickerGuard is off) reaction
        // to a PowerModeChanged StatusChange event. Kept centralised so the same
        // logic runs whether the deferred path or the immediate path triggered it.
        private void ApplyPowerStatusChange() {

            this.Op.PowerChange();

            // Pause BIOS heartbeat on battery to prevent HP firmware from triggering
            // unexpected hibernate; re-enable it when AC power is restored.
            // IsFullPowerConfirmed is used here so an in-progress flicker that
            // makes it past the confirmation gate (or has the guard disabled)
            // still gets the firmware/charging cross-check before toggling the
            // heartbeat — keeping the heartbeat alive saves users who do see a
            // false AC-offline reading without a corresponding fan-program switch
            // (e.g. AutoConfig is off, no fan program active).
            if(this.HeartbeatTimer != null && Config.BiosHeartbeatPauseOnBattery)
                this.HeartbeatTimer.Enabled = this.Op.Platform.System.IsFullPowerConfirmed();

        }

        // Handles a timer tick
        private void EventTimerTick(object sender, EventArgs e) {

            // Perform the updates as scheduled
            Update();

            // Passive AC-state poll (issue #70). SystemEvents.PowerModeChanged is the
            // primary trigger for the debounce, but Windows occasionally drops or
            // coalesces these events — particularly when several fire in quick
            // succession during a flicker — and on those misses OmenMon's view of
            // Op.FullPower can stick to the pre-event value forever. Re-synthesise
            // a deferred change here on edges (Online ↔ Offline transitions of the
            // live PowerLineStatus), provided nothing is already pending. Edge-
            // triggered rather than level-triggered so a sustained "Windows says
            // Offline / BIOS says AC" pathology — where Op.FullPower remains true
            // via the multi-source check while IsFullPower remains false — does
            // not create a never-ending queue/confirm/no-op cycle. The debounce
            // path then confirms or discards it like any other event. One
            // PowerStatus read per tick, no EC / WMI traffic.
            if(Config.AcFlickerGuard
                && Config.AcFlickerHoldMs > 0
                && Config.AcFlickerPassivePoll
                && this.PendingPowerChangeAt == DateTime.MinValue) {
                try {
                    bool nowOnline = this.Op.Platform.System.IsFullPower();
                    if(!this.LastPassivePollInitialized) {
                        this.LastPassivePollOnline = nowOnline;
                        this.LastPassivePollInitialized = true;
                    } else if(nowOnline != this.LastPassivePollOnline) {
                        this.LastPassivePollOnline = nowOnline;
                        // Only queue if the OS-level state diverges from our cached
                        // Op.FullPower view too — an edge that already matches
                        // Op.FullPower is by definition not a missed event.
                        if(nowOnline != this.Op.FullPower)
                            QueueDeferredPowerChange();
                    }
                } catch { }
            }

            // AC-flicker debounce (issue #70). If a PowerModeChanged StatusChange
            // event (or the passive poll above) deferred a power-state reaction,
            // process it once the hold window has elapsed.
            if(this.PendingPowerChangeAt != DateTime.MinValue
                && (DateTime.UtcNow - this.PendingPowerChangeAt).TotalMilliseconds
                    >= Config.AcFlickerHoldMs) {

                ProcessPendingPowerChange();

            }

            // Cheap (no EC traffic, no WMI) — runs every GuiTimerInterval ms.
            // Detects the "battery suddenly drops to <5%" torn-read symptom
            // reported under heavy load on certain SKUs (issue #59) and
            // suppresses Windows' Critical Battery hibernate for a short
            // window. Disabled via Config.BatteryGlitchGuard = false in
            // OmenMon.xml for users who would rather see the OS's reading
            // verbatim.
            PowerGuard.Tick(this);

        }

        // Runs the multi-sample confirmation at the end of a debounce hold and
        // either applies the change, re-defers, or force-applies once the cascade
        // ceiling (Config.AcFlickerMaxDeferralMs) is hit. Confirmation samples are
        // taken one per timer tick (never via Thread.Sleep) so the WinForms UI
        // thread is never blocked during a confirmation cycle (Copilot review #2 on
        // the v1.4.2 PR). Called every tick while a change is pending and the hold
        // window has elapsed, which is what drives the per-tick sampling. Pulled out
        // of the tick handler so the control flow stays readable.
        private void ProcessPendingPowerChange() {

            DateTime now = DateTime.UtcNow;
            bool cascadeExpired = this.PendingPowerChangeOriginAt != DateTime.MinValue
                && (now - this.PendingPowerChangeOriginAt).TotalMilliseconds
                    >= Config.AcFlickerMaxDeferralMs;

            // Bypass the confirmation gate if the cascade has already exceeded its
            // safety ceiling — a pathological flapper would otherwise re-defer
            // indefinitely, starving the fan-program / heartbeat update the user
            // actually wants to land.
            if(cascadeExpired) {
                ApplyConfirmedPowerChange();
                return;
            }

            int samples = Math.Max(1, Config.AcFlickerConfirmSamples);
            int gapMs   = Math.Max(0, Config.AcFlickerConfirmIntervalMs);

            if(!this.ConfirmInProgress) {
                // First sample establishes the AC state every later sample must match.
                this.ConfirmExpectedFullPower = this.Op.Platform.System.IsFullPowerConfirmed();
                this.ConfirmSamplesRemaining  = samples - 1;
                this.ConfirmLastSampleAt      = now;
                this.ConfirmInProgress        = true;
            } else if((now - this.ConfirmLastSampleAt).TotalMilliseconds >= gapMs) {
                // A single dissenter aborts the cycle and re-defers for another full
                // hold window — correct, because an in-progress flicker (Online →
                // Offline → Online or vice versa) inherently disagrees across
                // sequential samples and acting on it would reproduce the original bug.
                if(this.Op.Platform.System.IsFullPowerConfirmed() != this.ConfirmExpectedFullPower) {
                    // QueueDeferredPowerChange() resets the confirmation state for us.
                    QueueDeferredPowerChange();
                    return;
                }
                this.ConfirmSamplesRemaining--;
                this.ConfirmLastSampleAt = now;
            }

            // Every sample agreed (or single-sample mode) — apply.
            if(this.ConfirmSamplesRemaining <= 0)
                ApplyConfirmedPowerChange();

        }

        // Clears the pending + confirmation markers and applies the deferred change.
        // Markers are cleared *before* applying so EventPowerChange callbacks raised
        // by the apply itself (or by an OS state change racing the apply) start a
        // fresh cascade rather than extending this one.
        private void ApplyConfirmedPowerChange() {
            this.PendingPowerChangeAt = DateTime.MinValue;
            this.PendingPowerChangeOriginAt = DateTime.MinValue;
            ResetConfirmState();
            ApplyPowerStatusChange();
        }

        // Handles the BIOS heartbeat tick — keeps Performance Control alive
        private void EventHeartbeatTick(object sender, EventArgs e) {
            try { BiosCtl.Instance.GetFanCount(); } catch { }
        }
#endregion

#region Visual Methods
        // Brings the already-running application instance to the user's attention
        public void BringFocus() {

            // Show a balloon notification
            ShowBalloonTip(Config.Locale.Get(Config.L_GUI + "AlreadyRunning"));

            // Show the main GUI form
            ShowFormMain();

        }

        // Sets the notification icon tooltip text
        public void SetNotifyText(string text = "") {

            // Clamp to the OS hard limit to prevent ArgumentOutOfRangeException
            if(text.Length >= Os.NOTIFY_ICON_TEXT_MAXLEN)
                text = text.Substring(0, Os.NOTIFY_ICON_TEXT_MAXLEN - 4) + "...";

            // Use reflection to bypass the 64-character limit
            Os.SetNotifyIconText(Context.Notification, text);

        }

        // Shows a balloon tip above the notification area icon
        public void ShowBalloonTip(string message, string title = null, ToolTipIcon icon = ToolTipIcon.None) {

            // Show the notification only if the duration is not set to 0
            if(Config.GuiTipDuration > 0) {

                // Populate the data from the parameters
                this.Notification.BalloonTipIcon = icon;
                this.Notification.BalloonTipText = message;
                this.Notification.BalloonTipClicked += Menu.EventActionShowFormMain;

                // Also change the title if passed as a parameter
                if(title != null)
                    this.Notification.BalloonTipTitle = title;

                // Show the tip for a specified duration
                this.Notification.ShowBalloonTip(Config.GuiTipDuration);

            }

        }

        // Shows the main GUI form
        public void ShowFormMain() {

            // Set up the form first if it hasn't been created yet
            if(this.FormMain == null)
                this.FormMain = new GuiFormMain();

            // Show the form if not visible.
            // Show() must come before ShowToFront() — calling Win32 ShowWindow on a
            // not-yet-shown form prevents WinForms from setting WS_EX_APPWINDOW,
            // which is the extended style that makes the taskbar button appear.
            if(!this.FormMain.Visible)
                this.FormMain.Show();

            // Bring to the front only when needed, to avoid flicker from forcing
            // the window through an unnecessary minimize/restore/show sequence.
            bool shouldBringToFront = this.FormMain.WindowState == FormWindowState.Minimized
                || !this.FormMain.ContainsFocus;
            if(shouldBringToFront)
                Gui.ShowToFront(this.FormMain.Handle);

            // Briefly set to show in front of everything
            this.FormMain.TopMost = true;

            // Activate it
            this.FormMain.Activate();

            // Note: all this is in order to bring the application into focus
            // even if started from a background process (the Task Scheduler)

            // Reset the top-most state, unless set to remain on top
            this.FormMain.TopMost = Config.GuiStayOnTop;

        }

        // Toggles the main GUI form
        public void ToggleFormMain() {

                // Show the form if it's not visible already
                if(this.FormMain == null || !this.FormMain.Visible)
                    ShowFormMain();

                // Hide the form if it's visible
                else
                    this.FormMain.Hide();

        }
#endregion

        // Performs update operations as scheduled
        // This method is called periodically by a timer event
        public void Update() {

            // Reset the tick counters
            if(this.UpdateIconTick >= Config.UpdateIconInterval)
                this.UpdateIconTick = 0;
            if(this.UpdateMonitorTick >= Config.UpdateMonitorInterval)
                this.UpdateMonitorTick = 0;
            if(this.UpdateProgramTick >= Config.UpdateProgramInterval)
                this.UpdateProgramTick = 0;

            // Update the fan program or extend the countdown
            if(this.UpdateProgramTick++ == 0) {

                // Update the program, if active
                if(this.Op.Program.IsEnabled)
                    this.Op.Program.Update();

                // Alternatively, update any non-zero countdown
                // depending on the configuration settings
                else if(Config.FanCountdownExtendAlways)
                    this.Op.Program.UpdateCountdown(false, true);

            }

            // Update the main form, only if visible
            if(this.FormMain != null && this.FormMain.Visible && this.UpdateMonitorTick++ == 0) {
                this.FormMain.UpdateFan();
                this.FormMain.UpdateSys();
                this.FormMain.UpdateTmp();
            }

            // Update the notification icon and tray tooltip
            if(this.UpdateIconTick++ == 0) {

                // Only force a fresh EC temperature read when the dynamic icon is active
                // (same behavior as v1.1.x — avoids spurious hardware reads that can
                // interfere with HP firmware power-management and cause forced hibernate)
                if(this.Icon.IsDynamic) {

                    bool needForcedUpdate =
                        (this.FormMain == null || !this.FormMain.Visible)
                        && (!this.Op.Program.IsEnabled || this.UpdateProgramTick != 1);

                    byte maxTemp = this.Op.Platform.GetMaxTemperature(needForcedUpdate);

                    // Thermal panic — only runs when we have a fresh, hardware-verified reading
                    this.Op.CheckThermalPanic(maxTemp);

                    // Update the icon background based on fan mode
                    this.Icon.SetBackground(
                        this.Op.Platform.Fans.GetMode() == BiosData.FanMode.Performance ?
                            GuiIcon.BackgroundType.Warm : GuiIcon.BackgroundType.Cool);

                    // Show temperature in configured unit.
                    // The dynamic icon uses a custom bitmap font — only the localized °C glyph
                    // is guaranteed to render. Omit the suffix when Fahrenheit is active rather
                    // than passing a literal "°F" that may produce garbled characters.
                    int displayTemp = Config.TemperatureUseFahrenheit
                        ? (maxTemp * 9 / 5) + 32 : maxTemp;
                    string unitSuffix = Config.TemperatureUseFahrenheit
                        ? string.Empty
                        : Config.Locale.Get(Config.L_UNIT + "Temperature" + Config.LS_CUSTOM_FONT);
                    this.Icon.Update(Conv.GetString((uint) displayTemp, 2, 10) + unitSuffix);

                } else if(this.Op.IsThermalPanic) {

                    // Dynamic icon was disabled while panic was active: silently restore fans.
                    // ClearThermalPanic() skips the "Temperature normalized" balloon — the user
                    // turned off the icon deliberately, not because the temperature dropped.
                    this.Op.ClearThermalPanic();

                }

                // Tooltip: use only values already cached by the icon or form update —
                // never triggers additional hardware reads on its own
                SetNotifyText(BuildTrayTooltip());

            }

        }

        // Builds the tray tooltip from CACHED sensor values only — no hardware reads.
        // Shows CPU/GPU temperatures (cached by the last GetMaxTemperature or fan-program tick)
        // plus panic/program status. Fan RPMs are intentionally omitted to avoid calling
        // GetSpeed() (a WinRing0 EC read) a second time per tick while the form is already open.
        private string BuildTrayTooltip() {
            try {
                // Temperature: use LastMaxTemperature (set by GetMaxTemperature or fan program)
                // GetValue() on each sensor returns the last Update()-cached result — no EC access
                int cpuTemp = 0, gpuTemp = 0, biosTemp = 0;
                for(int i = 0; i < this.Op.Platform.Temperature.Length; i++) {
                    string name = this.Op.Platform.Temperature[i].GetName();
                    int val = this.Op.Platform.Temperature[i].GetValue();
                    if(name == "CPUT" && val > 0) cpuTemp = val;
                    else if(name == "GPTM" && val > 0) gpuTemp = val;
                    else if(name == "BIOS" && val > 0) biosTemp = val;
                }
                // On devices where EC CPUT register (0x57) overlaps with firmware data and
                // returns 0xFF (filtered out by MaxBelievableTemperature), fall back to the
                // WMI BIOS temperature — the only valid CPU-temp proxy on those models (8C9C, 8BBE, …)
                if(cpuTemp == 0 && biosTemp > 0) cpuTemp = biosTemp;

                string unit = Config.TemperatureUseFahrenheit ? "°F" : "°C";
                int cpu = Config.TemperatureUseFahrenheit && cpuTemp > 0 ? (cpuTemp * 9 / 5) + 32 : cpuTemp;
                int gpu = Config.TemperatureUseFahrenheit && gpuTemp > 0 ? (gpuTemp * 9 / 5) + 32 : gpuTemp;

                // Fan RPMs are visible in the main form; omitting them here avoids calling
                // GetSpeed() (WinRing0 EC read) a second time per tick while the form is open.
                string tip = string.Format("CPU: {0}{1} | GPU: {2}{1}",
                    cpu > 0 ? cpu.ToString() : "--", unit,
                    gpu > 0 ? gpu.ToString() : "--");

                // Append panic or program indicator
                if(this.Op.IsThermalPanic)
                    tip += Environment.NewLine + "⚠ THERMAL PANIC — fans at MAX";
                else if(this.Op.Program.IsEnabled)
                    tip += Environment.NewLine + Config.Locale.Get(Config.L_PROG)
                        + ": " + this.Op.Program.GetName();

                return tip;
            } catch {
                return Config.AppName + " " + Config.AppVersion;
            }
        }

    }

}
