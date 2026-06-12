  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/

using System;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using OmenMon.Hardware.Bios;
using OmenMon.Hardware.Ec;
using OmenMon.Hardware.Platform;
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

        // Background monitor thread that owns all periodic hardware sampling/control, and
        // publishes the latest sensor snapshot the UI renders from (v1.4.6, issue #98).
        internal GuiMonitor Monitor;

        // Whether the main form is currently visible. Set by the form on show/hide, read
        // by the monitor thread to decide whether to sample the fan/system fields.
        internal volatile bool FormVisible;

        // Stores the class managing the dynamic notification icon
        internal GuiIcon Icon;

        // Stores the menu
        internal GuiMenu Menu;

        // Stores the notification area icon class
        internal NotifyIcon Notification;

        // Stores the operation-running class
        internal GuiOp Op;

        // Stores the system timer (UI-thread only: renders the snapshot, runs the cheap
        // AC-flicker machine, drains the monitor's UI-message queue — never touches the EC/BIOS)
        private System.Windows.Forms.Timer Timer;

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
        // a confirmation cycle (Copilot review #2 on the v1.4.2 PR).
        // ConfirmInProgress=false means no confirmation is running.
        //
        // Thread-safety: every field above (PendingPowerChange*, LastPassivePoll*,
        // and these Confirm* fields) is read and written ONLY from EventTimerTick,
        // i.e. the WinForms UI thread, so none of them need locking. The one event
        // that can fire on a non-UI thread — SystemEvents.PowerModeChanged via
        // EventPowerChange — does not touch them directly; it only raises the
        // Interlocked signal below, which EventTimerTick drains (Copilot re-review
        // #2 on the v1.4.2 PR).
        private bool ConfirmInProgress = false;
        private bool ConfirmExpectedFullPower = false;
        private int ConfirmSamplesRemaining = 0;
        private DateTime ConfirmLastSampleAt = DateTime.MinValue;

        // Thread-safe handoff from EventPowerChange (which is NOT guaranteed to run
        // on the UI thread) to EventTimerTick (which always does). Set to 1 when a
        // PowerModeChanged StatusChange arrives; the timer tick atomically swaps it
        // back to 0 and performs the actual debounce/confirmation bookkeeping, so
        // all of that state stays single-threaded. Accessed only via Interlocked.
        private int PendingPowerEventSignal = 0;

        // Stores the number of ticks elapsed since the last RENDER of a particular
        // category (the fan-program tick counter lives in GuiMonitor now)
        internal int UpdateIconTick;
        internal int UpdateMonitorTick;
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

            // Initialize the background monitor (created here so it exists before anything
            // — AutoConfig, the icon, the first Update() — references it; started at the
            // end of the constructor once the rest of the context is wired up).
            this.Monitor = new GuiMonitor(Context);

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

            // The BIOS heartbeat is now driven by the monitor thread (folded into its
            // periodic pass, paused-on-battery per pass), so there is no separate timer.

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

            // Start the background monitor now that the whole context is wired up. From
            // here on, all periodic EC/BIOS access happens on the monitor thread, never
            // on the UI thread (issue #98).
            this.Monitor.Start();

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

            // Stop the background monitor first so nothing samples/controls the hardware
            // concurrently while we tear down (the fan-program terminate below then runs
            // uncontended on the UI thread).
            if(this.Monitor != null)
                this.Monitor.Stop();

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

            // SystemEvents.PowerModeChanged is NOT guaranteed to fire on the WinForms
            // UI thread, so we must not touch the debounce / confirmation state (or
            // the fan programs / heartbeat timer) from here — doing so would race the
            // timer tick and risk torn DateTime reads on 32-bit (Copilot re-review #2
            // on the v1.4.2 PR). Instead raise a thread-safe signal and let
            // EventTimerTick — always on the UI thread — do all the work: it either
            // queues the AC-flicker debounce (issue #70) or, when the guard is off,
            // applies the change immediately on the next tick. The ≤ GuiTimerInterval
            // pickup latency is negligible against the multi-second hold window.
            Interlocked.Exchange(ref this.PendingPowerEventSignal, 1);

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

            // Defer the fan-program switch (and its EC/BIOS traffic) to the monitor thread
            // so it never runs on the UI thread (issue #98). The heartbeat's
            // pause-on-battery behaviour is now enforced per-pass by the monitor, so there
            // is no timer to toggle here.
            this.Monitor?.RequestPowerApply();

        }

        // Handles a timer tick
        private void EventTimerTick(object sender, EventArgs e) {

            // Perform the updates as scheduled
            Update();

            // Drain any PowerModeChanged StatusChange signal raised (possibly
            // off-thread) by EventPowerChange. Handling it here keeps every
            // debounce / confirmation field single-threaded. Runs before the passive
            // poll so a freshly-queued change sets PendingPowerChangeAt and the poll
            // below then correctly skips (its guard is PendingPowerChangeAt == None).
            if(Interlocked.Exchange(ref this.PendingPowerEventSignal, 0) == 1) {
                if(Config.AcFlickerGuard && Config.AcFlickerHoldMs > 0)
                    // Debounced path (issue #70): defer the reaction until the hold
                    // window elapses; a flicker that reverts by then naturally no-ops.
                    QueueDeferredPowerChange();
                else
                    // Guard disabled — react immediately (the pre-v1.4.2 behaviour),
                    // now on the UI thread instead of the event's arbitrary thread.
                    ApplyPowerStatusChange();
            }

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
            // Samples are taken at most once per timer tick (Config.GuiTimerInterval,
            // 1 s), so AcFlickerConfirmIntervalMs acts as a lower bound that is
            // quantized up to the tick cadence: a value below one tick simply yields
            // one sample per tick; larger values correctly wait the right number of
            // ticks via the >= comparison below. We intentionally do NOT clamp the
            // threshold up to the tick interval — that would add jitter (occasionally
            // skipping a sample) when a tick lands a few ms early. The 1 s cadence is
            // ample for catching a multi-second flicker (Copilot re-review #3 on the
            // v1.4.2 PR).
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

        // Note: the BIOS heartbeat (keeps Performance Control alive) is now issued from
        // the monitor thread's periodic pass — see GuiMonitor.Pass().
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

        // Renders the latest monitor snapshot into the UI and drains any UI work the
        // monitor thread queued. Called periodically by the timer on the UI thread —
        // does NO EC/BIOS I/O (all periodic hardware access lives on the monitor thread
        // now, issue #98), so it can never stall the message pump.
        public void Update() {

            // Show anything the monitor thread asked the UI thread to show (balloon tips,
            // fan-program status) — must happen on the UI thread.
            if(this.Monitor != null)
                this.Monitor.DrainUiMessages();

            // The most recently published sensor snapshot (null until the first sample)
            MonitorSnapshot snap = this.Monitor != null ? this.Monitor.Current : null;

            // Reset the render-cadence tick counters
            if(this.UpdateIconTick >= Config.UpdateIconInterval)
                this.UpdateIconTick = 0;
            if(this.UpdateMonitorTick >= Config.UpdateMonitorInterval)
                this.UpdateMonitorTick = 0;

            // Render the main form from the snapshot, only if visible
            if(this.FormMain != null && this.FormMain.Visible
                && this.UpdateMonitorTick++ == 0 && snap != null) {
                this.FormMain.UpdateFan();
                this.FormMain.UpdateSys();
                this.FormMain.UpdateTmp();
            }

            // Render the notification icon and tray tooltip from the snapshot.
            // Thermal-panic protection no longer lives here — it runs on every monitor
            // pass (GuiMonitor.Pass → Op.CheckThermalPanic) so the safety net is decoupled
            // from UI responsiveness and from the dynamic-icon setting.
            if(this.UpdateIconTick++ == 0) {

                if(snap != null && this.Icon.IsDynamic) {

                    // Update the icon background based on fan mode
                    this.Icon.SetBackground(
                        snap.Mode == BiosData.FanMode.Performance ?
                            GuiIcon.BackgroundType.Warm : GuiIcon.BackgroundType.Cool);

                    // Show temperature in configured unit.
                    // The dynamic icon uses a custom bitmap font — only the localized °C glyph
                    // is guaranteed to render. Omit the suffix when Fahrenheit is active rather
                    // than passing a literal "°F" that may produce garbled characters.
                    int displayTemp = Config.TemperatureUseFahrenheit
                        ? (snap.MaxTemp * 9 / 5) + 32 : snap.MaxTemp;
                    string unitSuffix = Config.TemperatureUseFahrenheit
                        ? string.Empty
                        : Config.Locale.Get(Config.L_UNIT + "Temperature" + Config.LS_CUSTOM_FONT);
                    this.Icon.Update(Conv.GetString((uint) displayTemp, 2, 10) + unitSuffix);

                }

                // Tooltip from the snapshot's cached values — never triggers a hardware read
                SetNotifyText(BuildTrayTooltip(snap));

            }

        }

        // Builds the tray tooltip from a monitor SNAPSHOT — no hardware reads. CPU/GPU
        // temperatures and the panic/program indicators come straight off the snapshot
        // (the snapshot's CpuTemp already applies the BIOS-temperature fallback used on
        // boards where EC CPUT reads 0xFF — 8C9C, 8BBE, …). Fan RPMs are intentionally
        // omitted; they are visible in the main form.
        private string BuildTrayTooltip(MonitorSnapshot snap) {
            try {
                if(snap == null)
                    return Config.AppName + " " + Config.AppVersion;

                int cpuTemp = snap.CpuTemp;
                int gpuTemp = snap.GpuTemp;

                string unit = Config.TemperatureUseFahrenheit ? "°F" : "°C";
                int cpu = Config.TemperatureUseFahrenheit && cpuTemp > 0 ? (cpuTemp * 9 / 5) + 32 : cpuTemp;
                int gpu = Config.TemperatureUseFahrenheit && gpuTemp > 0 ? (gpuTemp * 9 / 5) + 32 : gpuTemp;

                string tip = string.Format("CPU: {0}{1} | GPU: {2}{1}",
                    cpu > 0 ? cpu.ToString() : "--", unit,
                    gpu > 0 ? gpu.ToString() : "--");

                // Append panic or program indicator
                if(snap.IsThermalPanic)
                    tip += Environment.NewLine + "⚠ THERMAL PANIC — fans at MAX";
                else if(snap.ProgramEnabled)
                    tip += Environment.NewLine + Config.Locale.Get(Config.L_PROG)
                        + ": " + snap.ProgramName;

                return tip;
            } catch {
                return Config.AppName + " " + Config.AppVersion;
            }
        }

        // Executes a UI-message the monitor thread queued — always on the UI thread
        // (called from Update() via Monitor.DrainUiMessages()).
        internal void HandleMonitorUiMessage(MonitorUiMessage msg) {
            switch(msg.Kind) {
                case MonitorUiMessage.MessageKind.Balloon:
                    ShowBalloonTip(msg.Text, msg.Title, msg.Icon);
                    break;
                case MonitorUiMessage.MessageKind.ProgramStatus:
                    HandleProgramStatus(msg.Severity, msg.Text);
                    break;
            }
        }

        // Renders a fan-program status update on the UI thread (moved here from
        // GuiOp.FanProgramCallback, which now only enqueues the status). Important →
        // balloon; Notice → main-form status line (if visible) + tray tooltip.
        private void HandleProgramStatus(FanProgram.Severity severity, string message) {

            if(severity == FanProgram.Severity.Important)
                ShowBalloonTip(message);

            else if(severity == FanProgram.Severity.Notice) {

                // Add a prefix if an alternate fan program
                string name = this.Op.Program.IsAlternate ?
                    Config.Locale.Get(Config.L_PROG + "Alt") + " " + this.Op.Program.GetName()
                    : this.Op.Program.GetName();

                // If the main form is available, update the status there
                if(this.FormMain != null && this.FormMain.Visible)
                    this.FormMain.UpdateSysMsg(
                        message.Replace(
                            Config.Locale.Get(Config.L_PROG + "SubMax"),
                            Conv.RTF_SUB1 + Config.Locale.Get(Config.L_PROG + "SubMax") + Conv.RTF_SUBSUP0)
                        + ": " + name);

                // Also put it in the tray icon tooltip
                SetNotifyText(
                    Config.Locale.Get(Config.L_PROG) + ": " + name
                    + " @ " + DateTime.Now.ToString(Config.TimestampFormat)
                    + Environment.NewLine + message);

            }

        }

    }

}
