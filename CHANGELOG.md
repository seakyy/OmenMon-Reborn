# Changelog

All notable changes to this project will be documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [1.1.0] - 2026-05-01

### Added
- **RGB Preset Cycling:** The Omen key can now be used as a hotkey to seamlessly cycle through all `<ColorPresets>` defined in the `OmenMon.xml` without opening the GUI.
- **Smart Color Detection:** The `CycleColorPresets()` logic automatically detects the active hardware colors and wraps around the preset list sequentially.
- **New XML Flags:** Added `<KeyToggleColorPreset>` to enable the new RGB feature and `<KeyToggleColorPresetSilent>` to optionally suppress the balloon tip notification.

### Changed
- **Hotkey Priority:** RGB preset cycling now takes highest precedence in the `KeyHandler` chain over Fan Program toggling and Custom Actions.
- **Safer Startup Defaults:** `<AutoConfig>` is now set to `false` by default to prevent silent crashes during Windows startup when WMI/EC services are not fully initialized yet.
- **Optimized Power Profile:** The `<GpuPower>` for the default "Power" program and `<GpuPowerDefault>` have been changed from `Maximum` to `Default` (Base Power). This prevents the system from automatically overriding the user's choice and jumping to "Cool" mode, ensuring maximum fan speeds with balanced heat generation.

## [1.0.0-reborn] - 2026-04-30

### Added
- **Dynamic Model Database:** HP Omen/Victus models are no longer hardcoded in C#. The EC register layout is now dynamically loaded from the `<Models>` section in `OmenMon.xml`.
- **Smart Fallback (Auto-Detector):** If an unknown laptop is detected, OmenMon prompts to run a 100% safe, read-only hardware scan to determine the correct EC registers automatically.
- **Community Contribution Button:** Added "Contribute Hardware Data..." to the tray menu. It securely copies a Markdown-formatted hardware diagnostic to the clipboard and opens the GitHub issue page.
- **Probe CLI Verb:** Added `OmenMon.exe -Probe` to generate a comprehensive WMI, BIOS, and EC state snapshot for debugging.
- **Heartbeat Timer:** Added an optional BIOS heartbeat timer (`BiosHeartbeatInterval`) to keep HP Performance Control awake on 2022+ devices (cherry-picked from upstream PR #69).
- **Refresh Rates:** Added `Os.GetAvailableRefreshRates` using `EnumDisplaySettings` (cherry-picked from upstream PR #69).

### Changed
- **Disabled TNT Sensors by Default:** The sensors `TNT2` through `TNT5` and `BIOS` are now set to `Use="false"` by default in the fan curve logic. They remain visible in the GUI but no longer falsely force fans to 100% when reporting erratic 84°C/97°C values.
- **Error Reporting:** Improved the WinRing0 driver error message to inform users about potential conflicts with Windows Memory Integrity (HVCI).

### Fixed
- **Fan Stuck at Max Issue:** Modified `FanArray.SetOff()` to use BIOS calls instead of EC registers when `FanLevelUseEc` is disabled, fixing fan control bugs on specific 2022/2023 models.
