  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using OmenMon.Hardware.Bios;
using OmenMon.Hardware.Ec;
using OmenMon.Library;

namespace OmenMon.Hardware.Platform {

    // Manages the hardware sensors
    public class Platform {

#region Data
        // Last maximum temperature reading
        public byte LastMaxTemperature { get; private set; }

        // Last per-component temperature readings (populated by GetCpu/GpuTemperature)
        public byte LastCpuTemperature { get; private set; }
        public byte LastGpuTemperature { get; private set; }

        // System information
        public ISettings System { get; private set; }

        // Fan sensors and controls
        public IFanArray Fans { get; private set; }

        // Temperature sensor array and which of these values are used
        public IPlatformReadComponent[] Temperature { get; private set; }
        public bool[] TemperatureUse { get; private set; }

        // Resolved per-model register layout. Set once in the ctor (after InitSystem)
        // and shared by InitFans / InitTemperature. Falls back to PlatformPreset.Default
        // for fan-related fields when the product isn't in the model database; for the
        // optional override fields (TempCpuReg/Gpu, ManualValueOn/Off) the preset's own
        // defaults already encode "no override".
        private PlatformPreset Preset;
#endregion

#region Initialization
        // Initializes the class
        public Platform() {

            // Initialize the system settings — must run first so GetProduct() is available.
            InitSystem();

            // Resolve the model preset once and reuse for both fans and temperature.
            string product = this.System.GetProduct();
            this.Preset = Config.Models.ContainsKey(product)
                ? Config.Models[product]
                : PlatformPreset.Default;

            // Initialize the fan controls
            InitFans();

            // Initialize the temperature controls
            InitTemperature();

        }

        // Initializes the fan controls
        private void InitFans() {

            // Use the preset resolved in the ctor.
            string product = this.System.GetProduct();
            PlatformPreset preset = this.Preset;

            // Restore overrides from a previous wizard run, then fall back to a known-good
            // built-in mapping for boards where the legacy tachometer offsets are unreliable
            // (e.g. 8BD4 repurposes 0xB0/0xB2 as temperature sensors). Load() is keyed by
            // ProductId so a sidecar from a different machine is rejected; Prime() fills
            // in any fan the sidecar did not cover. User-run calibrations always outrank
            // built-in defaults on a per-fan basis.
            AutoCal.Load(product);
            AutoCal.Prime(product);

            this.Fans = new FanArray(
                new IFan[] {

                    // CPU fan
                    new Fan(
                        BiosData.FanType.Cpu,
                        new EcComponent(preset.FanLevelReg0,
                            PlatformData.AccessType.Read | PlatformData.AccessType.Write),
                        new EcComponent(preset.FanRateReadReg0,
                            PlatformData.AccessType.Read),
                        new EcComponent(preset.FanRateWriteReg0,
                            PlatformData.AccessType.Write),
                        new EcComponent(preset.FanSpeedReg0,
                            PlatformData.AccessType.Read,
                            PlatformData.DataSize.Word)),

                    // GPU fan (RPM3 not RPM2 is intentional — RPM2 is the CPU high byte)
                    new Fan(
                        BiosData.FanType.Gpu,
                        new EcComponent(preset.FanLevelReg1,
                            PlatformData.AccessType.Read | PlatformData.AccessType.Write),
                        new EcComponent(preset.FanRateReadReg1,
                            PlatformData.AccessType.Read),
                        new EcComponent(preset.FanRateWriteReg1,
                            PlatformData.AccessType.Write),
                        new EcComponent(preset.FanSpeedReg1,
                            PlatformData.AccessType.Read,
                            PlatformData.DataSize.Word)) },

                new EcComponent(preset.CountdownReg,
                    PlatformData.AccessType.Read | PlatformData.AccessType.Write),

                new EcComponent(preset.ManualReg,
                    PlatformData.AccessType.Read | PlatformData.AccessType.Write),

                new EcComponent(preset.ModeReg,
                    PlatformData.AccessType.Read | PlatformData.AccessType.Write),

                new EcComponent(preset.SwitchReg,
                    PlatformData.AccessType.Read | PlatformData.AccessType.Write),

                preset.ManualValueOn,
                preset.ManualValueOff);

        }

        // Initializes the system settings
        private void InitSystem() {
            this.System = new Settings();
        }

        // Initializes the temperature controls
        private void InitTemperature() {

            // Per-model temperature-register overrides come from the preset resolved in
            // the ctor. 2024+ boards that moved CPU/GPU temp sensors to non-legacy EC
            // offsets (e.g. 8C9C: CPU at EC[0xB0], GPU at EC[0xB4]) declare TempCpuReg /
            // TempGpuReg there; for everyone else the values are 0 and we fall through
            // to the global <Temperature> config.
            PlatformPreset preset = this.Preset;

            // Set up the temperature sensor array based on the configuration data
            this.Temperature = new IPlatformReadComponent[Config.TemperatureSensor.Count];
            this.TemperatureUse = new bool[Config.TemperatureSensor.Count];

            // Populate the temperature sensor array
            int i = 0;
            foreach(string name in Config.TemperatureSensor.Keys) {

                // Set whether the sensor can be used for maximum temperature
                this.TemperatureUse[i] = Config.TemperatureSensor[name].Use;

                // Process each sensor loaded from the configuration
                switch(Config.TemperatureSensor[name].Source) {

                    // Add an Embedded Controller sensor. When the model preset declares
                    // a TempCpuReg / TempGpuReg override, remap the named CPUT / GPTM
                    // sensors to that address but keep the original sensor name so the
                    // GUI / tray tooltip lookups (which match on "CPUT" / "GPTM") still
                    // pick them up. Braces around the case body keep the local variables
                    // out of the switch's outer scope.
                    case PlatformData.LinkType.EmbeddedController: {
                        byte register = Config.TemperatureSensor[name].Register;
                        bool overridden = false;
                        if(name == "CPUT" && preset.TempCpuReg != 0) {
                            register = preset.TempCpuReg;
                            overridden = true;
                        } else if(name == "GPTM" && preset.TempGpuReg != 0) {
                            register = preset.TempGpuReg;
                            overridden = true;
                        }
                        EcComponent comp = new EcComponent(
                            register,
                            Config.MaxBelievableTemperature);
                        if(overridden)
                            comp.SetName(name); // preserve "CPUT" / "GPTM" instead of the
                                                // auto-derived enum name (e.g. "RPM1")
                        this.Temperature[i++] = comp;
                        break;
                    }

                    // Add a WMI BIOS sensor
                    case PlatformData.LinkType.WmiBios:
                        this.Temperature[i++] =
                            new WmiBiosTemperatureComponent(Config.MaxBelievableTemperature);
                        break;

                }

            }

        }
#endregion

#region Information Retrieval
        // Obtains the maximum value from the platform temperature array
        public byte GetMaxTemperature(bool forceUpdate = false) {

            // Update the platform temperature readings first
            // if forced to do so
            if(forceUpdate)
                UpdateTemperature(true);

            // Reset the state
            this.LastMaxTemperature = 0;
            byte value;

            // Iterate through the platform temperature array
            for(int i = 0; i < this.Temperature.Length; i++)

                // Obtain the reading from each temperature sensor
                // If the value is higher than the current candidate
                if(this.TemperatureUse[i] // Ignore certain sensors
                    && (value = (byte) this.Temperature[i].GetValue())
                        > this.LastMaxTemperature)

                    // Update the candidate
                    this.LastMaxTemperature = value;

            // Return the result
            return this.LastMaxTemperature;

        }

        // Obtains the CPU temperature from the CPUT sensor (or BIOS fallback).
        // On 2023+ boards where EC 0x57 overlaps firmware data and returns 0xFF
        // (filtered by MaxBelievableTemperature → 0), the WMI BIOS sensor is
        // used as a proxy — same logic the tray tooltip already applies.
        public byte GetCpuTemperature(bool forceUpdate = false) {

            if(forceUpdate)
                UpdateTemperature(false);

            byte cpu = 0, bios = 0;
            for(int i = 0; i < this.Temperature.Length; i++) {
                string name = this.Temperature[i].GetName();
                byte val = (byte) this.Temperature[i].GetValue();
                if(name == "CPUT" && val > 0) cpu = val;
                else if(name == "BIOS" && val > 0) bios = val;
            }

            // Fallback: BIOS sensor is the only valid CPU-temp proxy on boards
            // where CPUT reads 0xFF (8C9C, 8BBE, and similar 2023+ models)
            this.LastCpuTemperature = cpu > 0 ? cpu : bios;
            return this.LastCpuTemperature;

        }

        // Obtains the GPU temperature from the GPTM sensor.
        // Returns 0 if no valid GPU temperature sensor is available;
        // callers should fall back to the CPU temperature in that case.
        public byte GetGpuTemperature(bool forceUpdate = false) {

            if(forceUpdate)
                UpdateTemperature(false);

            byte gpu = 0;
            for(int i = 0; i < this.Temperature.Length; i++) {
                string name = this.Temperature[i].GetName();
                byte val = (byte) this.Temperature[i].GetValue();
                if(name == "GPTM" && val > 0) gpu = val;
            }

            this.LastGpuTemperature = gpu;
            return this.LastGpuTemperature;

        }
#endregion

#region Updates
        // Updates everything
        public void UpdateAll() {
            UpdateFans();
            UpdateSystem();
            UpdateTemperature();
        }

        // Updates the fan readings
        public void UpdateFans() {
            // Fan readings updated at retrieval time
        }

        // Updates the system settings
        public void UpdateSystem() {
            // System settings updated either only once
            // during initialization, or at retrieval time
        }

        // Updates the temperature readings
        public void UpdateTemperature(bool onlyUsed = false) {
            for(int i = 0; i < Temperature.Length; i++)
                if(!onlyUsed || this.TemperatureUse[i])
                    this.Temperature[i].Update();
        }
#endregion

    }

}
