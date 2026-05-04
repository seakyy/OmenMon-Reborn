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
                // Pause immediately if starting on battery (prevents hibernate on battery)
                this.HeartbeatTimer.Enabled = !Config.BiosHeartbeatPauseOnBattery
                    || this.Op.Platform.System.IsFullPower();
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

        }

        // Handles component disposal
        protected override void Dispose(bool isDisposing) {

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
            if(e.Mode == PowerModes.StatusChange) {
                this.Op.PowerChange();

                // Pause BIOS heartbeat on battery to prevent HP firmware from triggering
                // unexpected hibernate; re-enable it when AC power is restored
                if(this.HeartbeatTimer != null && Config.BiosHeartbeatPauseOnBattery)
                    this.HeartbeatTimer.Enabled = this.Op.Platform.System.IsFullPower();
            }

        }

        // Handles a timer tick
        private void EventTimerTick(object sender, EventArgs e) {

            // Perform the updates as scheduled
            Update();

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

            // Show the form if not visible
            if(!this.FormMain.Visible) {
                Gui.ShowToFront(this.FormMain.Handle);
                this.FormMain.Show();
                }

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
                int cpuTemp = 0, gpuTemp = 0;
                for(int i = 0; i < this.Op.Platform.Temperature.Length; i++) {
                    string name = this.Op.Platform.Temperature[i].GetName();
                    int val = this.Op.Platform.Temperature[i].GetValue();
                    if(name == "CPUT" && val > 0) cpuTemp = val;
                    else if(name == "GPTM" && val > 0) gpuTemp = val;
                }

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
