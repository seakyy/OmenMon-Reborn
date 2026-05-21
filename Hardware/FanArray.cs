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
        // Overridden via PlatformPreset for boards that gate manual fan control on a
        // non-standard register/value pair (e.g. 8BBE — Victus 16 R0053NT — writes 0x08
        // to EC[0x06] to engage, 0x48 to release). Set by the ctor — no inline default
        // here so the ctor's assignment is the single source of truth.
        private byte ManualValueOn;
        private byte ManualValueOff;

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
            byte manualValueOff = (byte) PlatformData.FanManual.Off) {

            this.ManualValueOn  = manualValueOn;
            this.ManualValueOff = manualValueOff;

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
            this.Manual.SetValue(flag ? this.ManualValueOn : this.ManualValueOff);
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

        // Checks if the current model has a known firmware issue with 100% fan speed.
        // Returns true for models where SetMax(true), the AutoCal wizard's 100% step,
        // or equivalent commands have been observed driving the EC past a BIOS-internal
        // rate-limiter, locking the fan controller until reboot.
        //
        // The common factor on every reported board has been a physical fan ceiling of
        // ~3600-3800 RPM, well below the headroom the calibration code path assumes.
        //
        //   8C30 — HP Victus 15-fb1000 (2023, AMD)        — issue #32 (NotDarkn)
        //   8D07 — HP Victus 15 sibling of 8C30 layout    — issue #56 (ghend-oss)
        //   8BAD — HP Victus 16 (2024, AMD)               — issue #58 (MartinSalg818)
        //   8E35 — HP Victus 15 (2026 BIOS)               — issue #57 (ClockworkNirvana,
        //          confirmed via raw EC-dump analysis: 70 % → 100 % shows 0 RPM gain on
        //          GPU and a 16 RPM regression on CPU — the same firmware signature.)
        //   8C77 — HP Omen 16-wf1012nl (single-fan SKU)   — issue #50 (stf1o, defensive add.
        //          PeriodEncoded8 reading at EC[0xD2] goes 0xB2 → 0xD0 → 0x99 → 0xEC across
        //          0/30/70/100 % — the 100 % period is slower than idle, same rate-limiter
        //          footprint as the boards above. RPM stays sidecar-supported only because
        //          the <Models> schema is LE16-only and cannot decode PeriodEncoded8.)
        //
        // Call-sites:
        //   App/Cli/CliOpCalibration.cs — AutoCal wizard filters 100% out of its profile.
        //   App/Gui/GuiOp.cs            — GUI gates manual SetMax(true) behind a confirm.
        // Adding a ProductId here propagates to every call-site automatically.
        //
        // The wizard's plateau detector (CliOpCalibration.AutoCalibrate) is the boardless
        // root-cause fix and stops the freeze for unknown boards too; this list stays as
        // defence-in-depth for boards we've already confirmed.
        public static bool HasMaxFanFreeze(string productId) {
            switch(productId) {
                case "8C30":
                case "8D07":
                case "8BAD":
                case "8E35":
                case "8C77":
                    return true;
                default:
                    return false;
            }
        }
#endregion

    }

}
