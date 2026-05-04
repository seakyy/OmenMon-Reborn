# Changelog

All notable changes to this project will be documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [1.2.1-reborn] - 2026-05-04 (Hotfix)

### Fixed

- **Hibernate regression on AC and battery:** v1.2.0 introduced two new hardware-access patterns that caused the same BIOS interference as the original v1.1.0 hibernate bug:
  1. `BuildTrayTooltip()` called `Fan.GetSpeed()` via WinRing0 every 3 seconds, even when neither the main form nor the dynamic icon was active. This added unsanctioned EC reads that had not existed previously.
  2. `CheckThermalPanic()` called `Platform.Fans.SetMax(true)` via BIOS Cmd 0x27, which — like the heartbeat `GetFanCount()` — can trigger HP firmware's power-management safeguards and force hibernate.
  Both calls now only happen when the dynamic icon is active (same hardware-access budget as v1.1.x).

- **`ThermalPanicEnabled` default changed to `false`** (was `true`). Thermal Panic is an opt-in feature. Enabling it with `ThermalPanicEnabled=true` in `OmenMon.xml` also requires `GuiDynamicIcon=true` so that sensor readings are already being taken. Without the dynamic icon, no panic check runs.

- **Tray tooltip fan RPM** is now only read when the main form is already visible and polling the EC — no additional hardware access when the window is hidden.

## [1.2.0-reborn] - 2026-05-04

### Added

- **Thermal Panic Mode:** When the maximum sensor temperature reaches the configured threshold (default 90 °C), OmenMon instantly forces both fans to maximum speed and shows a balloon alert. Fans are restored automatically once the temperature drops 5 °C below the threshold (configurable hysteresis). Configurable via `ThermalPanicEnabled`, `ThermalPanicTemperature`, `ThermalPanicHysteresis` in `OmenMon.xml`.

- **Fahrenheit display toggle:** Temperatures can now be shown in °F instead of °C. Toggle via **Settings → Show temperature in °F** in the tray menu. Applies to the main window sensor readouts, the dynamic tray icon, and the tray tooltip. Saved to `OmenMon.xml` (`TemperatureUseFahrenheit`).

- **Fan Profile Export / Import:** Share fan curves with the community. In the tray **Fan** submenu, click **"Export \<Name\>..."** to save any defined fan program as a portable `*.xml` file. Click **Import Fan Profile...** to load a shared curve — it is immediately added to the Fan menu and saved. Exported files use the same `<Program>` schema as `OmenMon.xml`, so they're human-readable and easy to tweak.

- **Rich tray tooltip (Dual-Temp):** The tray icon tooltip now always shows `CPU: 78°C | GPU: 72°C | Fan: 3200/3100 RPM`, even when no fan program is running and the dynamic icon is disabled. During Thermal Panic the tooltip adds `⚠ THERMAL PANIC — fans at MAX`. Respects the °C/°F setting.

### Changed

- Thermal Panic check now runs on every icon-update tick (every ~3 s) regardless of whether the main window is open.
- The tray tooltip is now updated on every icon-update tick instead of only during fan program callbacks.

## [1.1.1-reborn] - 2026-05-04

### Fixed
- **Unexpected Hibernate on Battery (Critical):** OmenMon's BIOS heartbeat timer (`GetFanCount()` every 30 s) kept HP's "Performance Control" session alive on battery power. On certain HP OMEN/Victus firmware revisions this conflicts with the BIOS battery manager and causes the system to force hibernate without warning after extended battery use. The heartbeat is now automatically paused when on battery and resumed when AC is restored.

### Added
- **`BiosHeartbeatPauseOnBattery` config option:** Controls whether the heartbeat pauses on battery (default: `true`). Set to `false` in `OmenMon.xml` only if you experience fan control issues while on battery.
- **`INSTRUCTION.md`:** Comprehensive user guide covering all fan modes (including legacy modes), fan programs, battery care, keyboard RGB, CLI reference, configuration, and known issues.

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