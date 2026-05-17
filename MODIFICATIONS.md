# OmenMon-Reborn: Modifications to the Original Work

This document outlines the modifications made to the original `OmenMon` project to comply with Section 5(a) of the GNU General Public License Version 3 (GPLv3).

**Original Work:** OmenMon (Copyright © 2023-2024 Piotr Szczepański)  
**Modified Work:** OmenMon-Reborn (Fork additions © 2026 seakyy)  
**Date of Fork/Modifications:** April 2026  

## Summary of Architectural Changes
The original OmenMon application hardcoded the Embedded Controller (EC) register layout for 2023 HP Omen models, leading to compatibility issues and hardware bugs (e.g., fans stuck at 100%) on newer devices. 

OmenMon-Reborn extracts this logic into a dynamic, configuration-driven model database (`OmenMon.xml`) and introduces a safe, heuristic read-only hardware scanner to auto-detect and configure unknown models.

## Modified Files
The following files from the original codebase were modified by seakyy in April 2026. A prominent copyright notice (`// OmenMon-Reborn additions © 2026 seakyy`) has been added to the header of each substantially modified or newly created file:

### New Files Created
* `Hardware/PlatformPreset.cs`: Defines the data structure for EC register layouts.
* `Hardware/AutoDetector.cs`: Implements the read-only heuristic EC scanner for unknown hardware.
* `App/Cli/CliOpProbe.cs`: Implements the `-Probe` CLI verb and the hardware markdown dump logic.

## Phase 7 — RGB Preset Hotkey (Colour Cycling via Omen Key)

A new `CycleColorPresets()` method was added to `App/Gui/GuiOp.cs`.  When enabled, pressing the Omen key cycles through all `<ColorPresets>` entries defined in `OmenMon.xml` in alphabetical order, wrapping back to the first after the last.  The active preset is detected by comparing the live hardware colour table against the preset list; if no match is found (i.e. a custom colour is set) the cycle starts at the first preset.

Two new XML configuration keys control the feature:
| Key | Default | Meaning |
|-----|---------|---------|
| `KeyToggleColorPreset` | `false` | Enable/disable the feature |
| `KeyToggleColorPresetSilent` | `false` | Suppress the balloon-tip notification |

The feature takes priority over `KeyToggleFanProgram` and `KeyCustomAction` in the Omen-key handler chain.

---

### Modified Files
* **Core Logic & DB:** 
  * `Library/Config.cs` & `Library/ConfigData.cs`: Added XML parsing and serialization for the `<Models>` database; disabled TNT2-TNT5 sensors by default.
  * `Hardware/Platform.cs`: Removed hardcoded `switch` statement for device IDs; implemented dynamic preset loading.
* **Hardware Interfacing:**
  * `Hardware/FanArray.cs`: Modified `SetOff()` to use BIOS calls instead of EC when `FanLevelUseEc` is false.
  * `Driver/Ring0.cs`: Rewritten in v1.4.0 to delegate to PawnIO instead of WinRing0 (see Phase 8 below). Public API preserved.
  * `Library/Os.cs`: Added `GetAvailableRefreshRates` using `EnumDisplaySettings`.
* **User Interface:**
  * `App/Gui/GuiFormMain.cs` & `App/Gui/GuiFormMainInit.cs`: Added startup hook to trigger `AutoDetector` if the device is unknown.
  * `App/Gui/GuiMenu.cs`: Added the "Auto-Calibrate & Diagnose..." tray entry that launches the Auto-Calibration Wizard (`App/Gui/GuiFormCalibration.cs`). The wizard runs an active 4-step fan stress sweep, scans the EC dumps via `Hardware/EcDiffScanner.cs` to discover RPM-tachometer registers (16-bit LE / period-encoded / direct-multiplier), applies the result to the live session through `Library/AutoCal.cs`, persists it to a sidecar XML, and copies a Markdown report to the clipboard for upstream contribution. Replaces the older static "Contribute Hardware Data" dump-and-paste flow.
  * `App/Gui/GuiTray.cs`: Implemented a background heartbeat timer to prevent Performance Control from sleeping.
* **Build System:**
  * `OmenMon.csproj`: Included new `.cs` files in the compilation target. v1.4.0 swaps the embedded resource `OmenMon.Driver.sys.gz` (WinRing0) for `OmenMon.LpcACPIEC.bin` (PawnIO).

## Phase 8 — WinRing0 → PawnIO Migration (v1.4.0)

Replaces the WinRing0 kernel driver with [PawnIO](https://pawnio.eu/), whose Microsoft-signed driver no longer triggers Windows Defender warnings. The migration is invisible to higher layers: `Driver/Ring0.cs` keeps its original public API and `Hardware/Ec.cs` is unchanged.

### New Files Created

* `Driver/PawnIo.cs`: User-mode wrapper around `PawnIOLib.dll`. Locates the library via the registry / Program Files, pre-loads it via `LoadLibraryW`, opens a PawnIO executor handle, loads the embedded module blob, and exposes a `PawnIo.Execute(name, in[], out[])` entry point used by `Ring0.cs`.
* `Resources/LpcACPIEC.bin`: The official namazso-signed PawnIO module from [PawnIO.Modules releases](https://github.com/namazso/PawnIO.Modules/releases). Embedded as resource `OmenMon.LpcACPIEC.bin`. Provides byte-granular ACPI EC port I/O (ports `0x62` / `0x66` only). LGPL-2.1-or-later, redistribution allowed unmodified — see `Resources/PAWN_BUILD.md` for module rotation procedure.
* `Resources/PAWN_BUILD.md`: Operational documentation for the embedded PawnIO module (where it comes from, how to update it, troubleshooting).
* `docs/DEV_NOTES_v1.4.0.md`: Full architecture and rotation notes for the PawnIO migration.

### Removed Files

* `Driver/Driver.cs`: WinRing0 service installation / kernel-driver IOCTL plumbing — obsolete.
* `Resources/Driver.sys.gz`: Compressed WinRing0 kernel driver — obsolete; the user installs the signed PawnIO driver themselves.

## Phase 9 — Post-PawnIO Regression Sweep (v1.4.0)

The first week of v1.4.0 field testing surfaced four GUI / fan-control regressions and one hardware compatibility crash that don't belong to the PawnIO migration itself but were uncovered by it. They are bundled into the same v1.4.0 release rather than a 1.4.1 because the migration and these fixes are inseparable from a user's perspective (the migration is what made the regressions visible).

### Modified Files

* `App/Cli/CliOpCalibration.cs`: `ApplyLevel` now disengages BIOS manual mode immediately before calling `SetMaxFan(true)` on the 100% step, then re-engages it on any subsequent sub-100% step. Without this, manual mode pins the fans at `Config.FanLevelMax` (~3.4-3.8 kRPM) and the wizard's calibration plateau is systematically ~30% lower than the fan's physical ceiling (issues #40 / #41 / #52).
* `App/Gui/GuiFormMain.cs`: Replaced a non-existent `UpdateFanMode()` call (introduced by the 8C30 safety dialog and breaking the build on a clean checkout) with the existing `UpdateFanCtl()`. Also added the missing `SetMode(GetMode())` mode-refresh step in the "user cancelled max → fall back to safe levels" branch, matching the normal constant-speed path.
* `App/Gui/GuiMenu.cs`: Added missing `using OmenMon.Hardware.Platform;` for the `FanArray.HasMaxFanFreeze` reference (compile-blocking on a clean checkout). Added `SetOff(false)` before `SetMax(true)` in the tray "Max fan" toggle so the BIOS honours the command when the fan-off latch is set.
* `Hardware/FanProgram.cs`: `SetFanLevel` clears a stale `Fan Off` latch (only when actually set) before writing levels, fixing the GPU fan getting stuck off on some boards while CPU continues to ramp with the curve (issue #39, 8D07). The global `Max Fan` latch is intentionally *not* touched here — clearing it would defeat Thermal Panic's safety override, which is asserted only on the transition into panic and not re-asserted every tick.
* `Hardware/Bios.cs`: `Check()` no longer throws on BIOS return codes 1, 4, 6 and 46. The original author had noted these codes "were also observed but their exact meaning is not understood"; every reported instance has been a benign "command not supported on this platform" — most recently an Omen Transcend 14 (fb0118TX) crashing the GUI on open with `BIOS call failed: Unknown response from BIOS: 4`. The unknown codes are now treated the same as code 3 (command not available): soft-fail, let the rest of the GUI come up.
* `Library/AutoCal.cs`: Added a built-in `KnownBoards` mapping for 8DD0 (LE16 RPM at 0xB0 / 0xB2). The Auto-Calibration heuristic occasionally locks onto bogus PeriodEncoded8 offsets on this board (issue #33's "50k RPM" display); the existing >16-byte sidecar distance sanity check now falls through to this known-good layout.
* `OmenMon.xml`: Added native model database entries for `8D26` (HP Omen 16-ap0007ns, 2026) and `88EB` (HP Victus 16, 2021). Both use the standard 2023+ layout, confirmed via the Auto-Calibration Wizard (issues #52, #48).

### New Files Created

* `.github/workflows/draft-release.yml`: Tag-driven automation. When `v*.*.*` is pushed, extracts the matching `## [version]` / `## [version-reborn]` section from `CHANGELOG.md` and creates a *draft* GitHub release with those notes. The maintainer attaches the locally-built `OmenMon.exe` and publishes manually — at which point the existing `release.yml` builds + zips + attaches the public archive. Idempotent (re-runs refresh the draft body instead of duplicating).