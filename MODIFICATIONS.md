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

### Modified Files
* **Core Logic & DB:** 
  * `Library/Config.cs` & `Library/ConfigData.cs`: Added XML parsing and serialization for the `<Models>` database; disabled TNT2-TNT5 sensors by default.
  * `Hardware/Platform.cs`: Removed hardcoded `switch` statement for device IDs; implemented dynamic preset loading.
* **Hardware Interfacing:**
  * `Hardware/FanArray.cs`: Modified `SetOff()` to use BIOS calls instead of EC when `FanLevelUseEc` is false.
  * `Driver/Driver.cs` & `Driver/Ring0.cs`: Added HVCI (Memory Integrity) hints to EC initialization error messages.
  * `Library/Os.cs`: Added `GetAvailableRefreshRates` using `EnumDisplaySettings`.
* **User Interface:**
  * `App/Gui/GuiFormMain.cs` & `App/Gui/GuiFormMainInit.cs`: Added startup hook to trigger `AutoDetector` if the device is unknown.
  * `App/Gui/GuiMenu.cs`: Added "Contribute Hardware Data" button to copy markdown dumps to the clipboard.
  * `App/Gui/GuiTray.cs`: Implemented a background heartbeat timer to prevent Performance Control from sleeping.
* **Build System:**
  * `OmenMon.csproj`: Included new `.cs` files in the compilation target.