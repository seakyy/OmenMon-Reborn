  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/

using System;
using OmenMon.Hardware.Bios;
using OmenMon.Hardware.Ec;
using OmenMon.Library;

namespace OmenMon.Hardware.Platform {

#region Interface
    // Defines an interface for interacting with the fan system
    public interface IFanArray {

        public IFan[] Fan { get; }

        // Retrieves or sets the countdown value
        // until automatic settings are restored [s]
        public int GetCountdown();
        public void SetCountdown(int countdown);

        // Retrieves or sets the levels
        // of all fans at the same time
        public byte[] GetLevels();
        public void SetLevels(byte[] levels);

        // Retrieves or sets maximum fan speed
        public bool GetMax();  
        public void SetMax(bool flag);

        // Retrieves or sets manual fan control state
        public bool GetManual();
        public void SetManual(bool flag);

        // Retrieves or sets the current fan mode
        public BiosData.FanMode GetMode();
        public void SetMode(BiosData.FanMode mode);

        // Retrieves the fan off switch status
        // or switches the fan off
        public bool GetOff();
        public void SetOff(bool flag);


    }
#endregion

#region Implementation
    // Implements a mechanism for interacting with the fan system
    public class FanArray : IFanArray {

        // Fan array
        public IFan[] Fan { get; private set; }

        // Stores the countdown platform component
        protected IPlatformReadWriteComponent Countdown;

        // Stores the manual toggle component
        protected IPlatformReadWriteComponent Manual;

        // Per-model manual-mode trigger values (defaults match legacy FanManual.On/.Off).
        // Overridden via PlatformPreset for boards that gate manual control on a
        // non-standard register/value pair (e.g. 8BBE writes 0x11 to 0x59).
        // Initialised in the ctor — declared without inline values so the ctor's
        // assignment is the single source of truth.
        protected byte ManualValueOn;
        protected byte ManualValueOff;

        // When true, SetManual(true) snapshots the current ManualReg byte so
        // SetManual(false) can restore exactly that value instead of writing a
        // hard-coded ManualValueOff. Needed when ManualReg is shared with another
        // piece of firmware state (e.g. 8BBE — EC[0x59] is also the perf-profile
        // selector). Snapshot is invalidated by any explicit ManualValueOff write.
        protected bool ManualRestorePrevious;
        private byte? ManualPreviousValue;

        // Stores the fan mode component
        protected IPlatformReadWriteComponent Mode;

        // Stores the fan on and off switch component
        protected IPlatformReadWriteComponent Switch;

        // Constructs a fan array instance
        public FanArray(
            IFan[] fan,
            IPlatformReadWriteComponent fanCountdown,
            IPlatformReadWriteComponent fanManual,
            IPlatformReadWriteComponent fanMode,
            IPlatformReadWriteComponent fanSwitch,
            byte manualValueOn  = (byte) PlatformData.FanManual.On,
            byte manualValueOff = (byte) PlatformData.FanManual.Off,
            bool manualRestorePrevious = false) {

            this.ManualValueOn        = manualValueOn;
            this.ManualValueOff       = manualValueOff;
            this.ManualRestorePrevious = manualRestorePrevious;

            // Initialize the fan array
            this.Fan = new IFan[PlatformData.FanCount];

            // Define the CPU fan
            this.Fan[0] = fan[0];

            // Define the GPU fan
            this.Fan[1] = fan[1];

            // Define the countdown component
            this.Countdown = fanCountdown;

            // Define the mode component
            this.Manual = fanManual;

            // Define the mode component
            this.Mode = fanMode;

            // Define the switch component
            this.Switch = fanSwitch;

        }

        // Retrieves the countdown value [s]
        // until automatic settings are restored
        public int GetCountdown() {
            this.Countdown.Update();
            return this.Countdown.GetValue();
        }

        // Sets the countdown value [s]
        public void SetCountdown(int countdown) {
            this.Countdown.SetValue(countdown);
        }

        // Retrieves the levels of all fans at the same time
        public byte[] GetLevels() {
            return Hw.BiosGet(Hw.Bios.GetFanLevel);
        }

        // Sets the levels of all fans at the same time
        public void SetLevels(byte[] levels) {

            // Set manual fan mode, if needed
            if(Config.FanLevelNeedManual)
                this.SetManual(true);

            // Depending on the configuration setting,
            // use either the BIOS or the EC to set levels
            if(Config.FanLevelUseEc) {

                // Try to set the speed for each fan individually
                for(int i = 0; i < levels.Length; i++)
                    this.Fan[i].SetLevel(levels[i]);

            } else {
                try {

                    // Make a WMI BIOS call to set the level of both fans
                    Hw.BiosSet(Hw.Bios.SetFanLevel, levels);

                } catch {

                    // It has been reported on some models the settings
                    // take effect anyway, despite a BIOS error returned

                    // Thus, silently ignore if the call failed

                    // Regardless of the Config.BiosErrorReporting value,
                    // status is always checked, and reported in CLI mode

                }
            }
        }

        // Retrieves the manual fan speed toggle status
        public bool GetManual() {
            return this.Manual.GetValue() == this.ManualValueOn;
        }

        // Sets the manual fan speed toggle status
        public void SetManual(bool flag) {
            if(flag) {
                // Snapshot the current ManualReg byte before we override it, when the
                // model declares ManualReg shared with other firmware state (e.g. 8BBE
                // 0x59 = perf-profile selector). Avoids silently rewriting the user's
                // chosen profile on the matching SetManual(false). Skip the snapshot
                // if manual is already engaged — otherwise we'd overwrite a valid
                // pre-engage snapshot with our own ManualValueOn byte.
                if(this.ManualRestorePrevious) {
                    this.Manual.Update();
                    byte current = (byte) this.Manual.GetValue();
                    if(current != this.ManualValueOn)
                        this.ManualPreviousValue = current;
                }
                this.Manual.SetValue(this.ManualValueOn);
            } else {
                byte off = (this.ManualRestorePrevious && this.ManualPreviousValue.HasValue)
                    ? this.ManualPreviousValue.Value
                    : this.ManualValueOff;
                this.Manual.SetValue(off);
                this.ManualPreviousValue = null;
            }
        }

        // Retrieves the maximum fan speed status
        public bool GetMax() {
            return Hw.BiosGet<bool>(Hw.Bios.GetMaxFan);
        }

        // Sets the maximum fan speed status
        public void SetMax(bool flag) {
            Hw.BiosSet(Hw.Bios.SetMaxFan, flag);
        }

        // Retrieves the current fan mode
        public BiosData.FanMode GetMode() {
            this.Mode.Update();
            return (BiosData.FanMode) this.Mode.GetValue();
        }

        // Sets the current fan mode
        public void SetMode(BiosData.FanMode mode) {
            Hw.BiosSet<BiosData.FanMode>(Hw.Bios.SetFanMode, mode);
            // Note: WMI BIOS call preferred over this.Mode.SetValue((byte) mode);
        }

        // Retrieves the fan off switch status
        public bool GetOff() {
            this.Switch.Update();
            return ((PlatformData.FanSwitch) this.Switch.GetValue()) == PlatformData.FanSwitch.Off;
        }

        // Switches the fan off or back on
        public void SetOff(bool flag) {
            if(flag && !Config.FanLevelUseEc) {
                try {

                    // Use a BIOS call consistent with SetLevels when in BIOS fan level mode
                    Hw.BiosSet(Hw.Bios.SetFanLevel, new byte[] { 0, 0 });

                } catch {

                    // Settings may take effect anyway despite a BIOS error (see SetLevels)

                }
            } else {
                this.Switch.SetValue(flag ?
                    (int) PlatformData.FanSwitch.Off : (int) PlatformData.FanSwitch.On);
            }
        }
#endregion

    }

}
