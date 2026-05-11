# Changelog

All notable changes to this project will be documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

### Fixed

- **Fan programs could appear "stuck" and fans could plateau around ~3600 RPM on some boards (e.g. 8D07) after prior Off/Max actions.** Root cause: `FanProgram` wrote target levels without clearing the global `Fan Off` / `Max Fan` latches first. On affected firmware, a latched max mode can cap RPM lower than the board's level-based ceiling, and a latched off mode makes level writes ineffective. `FanProgram.SetFanLevel()` now explicitly clears both latches before applying per-level targets.

## [1.4.0-reborn] - 2026-05-09

> **Security release.** OmenMon no longer ships the WinRing0 kernel driver. The kernel-mode access layer has been replaced with [PawnIO](https://pawnio.eu/), whose Microsoft-signed, HVCI-compatible driver does not trigger Windows Defender warnings the way WinRing0 did. Functionality is preserved; the public `Ring0` API is unchanged so `Hardware/Ec.cs` and every other caller stayed put.

### Changed

- **WinRing0 driver removed in favour of PawnIO.** The whole rationale: WinRing0.sys is well-known to AV/EDR (CVE-2020-14979 in older bundled versions, plus an arbitrary-MSR-write surface) and Defender flags it on every install — a worsening UX. PawnIO instead ships a single Microsoft-signed driver shared across applications; modules are sandboxed Pawn bytecode that the driver verifies against the maintainer's RSA-2048 public key before loading. From the user's perspective: install PawnIO once from <https://pawnio.eu/> and the Defender prompt is gone.

- **Kernel-mode I/O is now mediated by `Driver/PawnIo.cs`** — a thin user-mode wrapper around `PawnIOLib.dll` that locates the library via `HKLM\SOFTWARE\PawnIO\InstallDir` (with a Program-Files fallback), opens a PawnIO executor handle, and loads the embedded module blob via `pawnio_load`. Each kernel operation is invoked by name through `pawnio_execute`.

- **`Driver/Ring0.cs` rewritten to delegate to PawnIO** while keeping its public surface (`Open` / `Close` / `IsOpen` / `GetStatus` / `ReadIoPort` / `WriteIoPort` / `GetPciAddress` etc.) byte-compatible with v1.3.x. `Hardware/Ec.cs` and every existing caller compile and run unchanged. The MSR / PCI-config / physical-memory methods OmenMon never actually called are now no-op stubs (kept on the surface for source compatibility with anything outside this codebase that might link against `Ring0`).

### Added

- **Model support: HP Victus 15-fb1000 (2023, AMD / 8C30)** — added to the built-in model database (issue #32, reported by @NotDarkn). Register layout is identical to 8D07: FanLevel at `0x34`/`0x35`, rate write at `0x2C`/`0x2D`, rate read at `0x2E`/`0x2F`, 16-bit LE RPM tachometers at `0xB0`/`0xB2` (CPU max ~3776 RPM, GPU max ~3623 RPM — confirmed by the Auto-Calibration Wizard). Known firmware quirk: pushing the fan rate to 100% locks the EC fan controller until the next restart; normal fan-program operation (≤70%) is unaffected.

- **`Resources/LpcACPIEC.bin`** — the official, namazso-signed PawnIO module ([source](https://github.com/namazso/PawnIO.Modules/blob/main/LpcACPIEC.p)) is bundled as embedded resource `OmenMon.LpcACPIEC.bin`. It exposes `ioctl_pio_read` / `ioctl_pio_write` restricted to ACPI EC ports `0x62` (data) and `0x66` (command) — exactly the two ports OmenMon's EC handshake (`Hardware/Ec.cs` `ReadByteImpl` / `WriteByteImpl`) talks to. The mutex name LpcACPIEC expects (`\BaseNamedObjects\Access_EC`) coincides with OmenMon's existing `Config.LockPathEc` (`Global\Access_EC`), so coordination with HP Omen Gaming Hub continues to work.

- **`Resources/PAWN_BUILD.md`** — module rotation guide. When namazso publishes a new PawnIO.Modules release, drop the new `LpcACPIEC.bin` into `Resources/` and rebuild — no code changes needed.

- **Dev notes (`docs/DEV_NOTES_v1.4.0.md`)** — full architecture write-up: why we use someone else's signed module rather than ship a custom one, what the wire format of `pawnio_load` looks like, how to extend OmenMon to additional kernel operations later, and what to do when the upstream module file rotates.

### Removed

- `Driver/Driver.cs` — the WinRing0 service-management plumbing (extract `.sys.gz`, register kernel service, IOCTL marshalling) is no longer needed.

- `Resources/Driver.sys.gz` — the embedded WinRing0 kernel driver. OmenMon does not extract or install any `.sys` file at runtime any more; the signed PawnIO driver is installed by its own MSI on the user's machine.

- The `IOCTL_OLS_*` constants and `Kernel32.DeviceIoControl` P/Invoke in `External/Kernel.cs` are now unused but left in place — they're harmless dead code and removing them brings no functional benefit.

### End-user upgrade path from 1.3.x

1. Uninstall OmenMon 1.3.x as usual (or just overwrite the binaries — there is no installer to migrate).
2. Install PawnIO once from <https://pawnio.eu/> (signed MSI, no Defender warning).
3. Run `OmenMon.exe` as administrator. You will not be asked to allow a kernel driver any more — the existing PawnIO.sys handles every EC read/write.
4. Existing `OmenMon.xml`, `OmenMon-AutoCal.xml`, and fan-program XMLs are forward-compatible — the migration touches only the `Driver/` layer.

## [1.3.4-reborn] - 2026-05-09

### Fixed

- **HP Omen (8DD0) — "50k RPM" GUI display after a wizard re-run** (issue #26, reported by @DreamStare0). The Auto-Calibration Wizard misfired on this user's second run because the EC dump came back shifted by one byte (a transient EC-read alignment glitch — row 0 started with `00 30 80 16 1A 31` instead of `00 80 16 1A 31`), so the wizard locked onto `EC[0xB3]` (CPU) and `EC[0xD9]` (GPU) and persisted those bogus offsets to `OmenMon-AutoCal.xml`. On the next launch OmenMon happily read 16-bit values from those addresses — `EC[0xD9]` happens to contain memory noise that decodes as ~50000 RPM. Two-part fix:

  1. **Native database entry for 8DD0** added with the standard 2023+ register layout (`FanSpeedReg0/1 = 0xB0 / 0xB2`), confirmed against the user's first wizard run on 2026-05-07 (CPU max ~3789, GPU max ~3654 RPM). Owners get correct readings out of the box without the wizard.

  2. **Sanity check in `AutoCal.Load()`** for sidecar files. Across every shipped board layout the CPU and GPU fan tachometer offsets sit within a few bytes of each other (0 for shared single-fan SKUs, 2 for canonical `0xB0`/`0xB2`, 3 for 8BD4's `0x11`/`0x14`). When the persisted offsets are >16 bytes apart **and** a native model entry exists for this board, the file is treated as a wizard misfire and deleted, so the next launch falls through to the native database / `Prime()` mapping. Boards without a native entry still trust the sidecar — they have nothing else to fall back to.

## [1.3.3-reborn] - 2026-05-09

> **Mixed-confidence release.** The 8C9C fix is hardware-validated (eyzinox's HWInfo cross-reference confirmed `EC[0xB0]` matches the live CPU die temperature within ~1 °C). The 8BBE and 8D07 fixes are derived from EC-dump analysis only — no live confirmation yet — and are marked ⚠️ in the wiki accordingly. Issue authors are asked to test and report back so the next hotfix can either lift those to ✅ or correct course.

### Fixed

- **HP Victus 16-1034NF (8C9C) — CPU/GPU temperature sensors remapped** (issue #16, reported by @eyzinox). v1.3.1 added native 8C9C support but kept reading temperature from the legacy `EC[0x57]` (CPUT, returns `0xFF`) and `EC[0xB7]` (GPTM) addresses, plus the WMI BIOS sensor at `EC[0xB2]`. None of those return the live die temperature on this board: the WMI reading is a heavily-smoothed package average that lags 10 °C+ behind reality (eyzinox saw HWInfo report 84 °C while WMI was stuck at 73 °C). Confirmed against eyzinox's hot probe that the real CPU temp lives at `EC[0xB0]` (`0x53` = 83 °C, matching HWInfo's 84 °C within polling jitter) and the GPU hotspot at `EC[0xB4]` (`0x58` = 88 °C). The 8C9C entry now declares `TempCpuReg=176` / `TempGpuReg=180` to remap the named CPUT / GPTM sensors to those addresses. Fan RPM display remains best-effort via `EC[0xF1] × 60` (the wizard-detected register actually mirrors the commanded rate %, not a real tach pulse) — documented in the XML comment.

- **HP Victus 15 (8D07) — corrected register layout** (issue #23 follow-up, reported by @ghend-oss). The v1.3.2 entry copied the 2023+ default (FanLevel at `0x11`/`0x12`, rate write at `0x3A`/`0x3B`), but re-checking ghend-oss's EC dumps showed those addresses stay constant on this board — the live FanLevel pair is `0x34`/`0x35` and the live rate-write pair is `0x2C`/`0x2D` (classic 2022 layout). Also corrected the display name from "HP Omen 16 (2026)" to "HP Victus 15 (2024, AMD)" — the 8D07 board is the AMD Ryzen 5 7535HS + RX 6550M Victus 15, not an Omen. Caveat: the 0x2C/0x2D ramp observed during the wizard sweep may have come from the BIOS WMI path rather than from EC writes — if so, switching the FanRateWrite address won't on its own raise the 3789 RPM peak ghend-oss saw. Awaiting a re-run of the calibration wizard under sustained gaming load to confirm.

- **HP Victus 16 R0053NT (8BBE) — manual fan control gate identified at `EC[0x06]`** (issue #19, reported by @yunusemreyl). Re-reading all six of yunusemreyl's hardware probes pinned the actual manual-mode gate at `EC[0x06]`, not `EC[0x59]` as a first pass suggested:

  | Mode | `EC[0x06]` | `EC[0x59]` | Fans |
  |------|-----------|------------|------|
  | Fan Max (Omen Hub)        | `0x08` | `0x30` | spin to max |
  | Custom Fan (Omen Hub)     | `0x08` | `0x11` | manual override |
  | Eco / Balanced / Auto     | `0x48` | `0x30` | BIOS auto |
  | Performance               | `0x48` | `0x31` | BIOS auto |

  `EC[0x59]` is just the BIOS *profile* selector (Custom=`0x11`, Performance=`0x31`, everything-else=`0x30`) — it does **not** gate fan control: Fan Max with `0x59=0x30` still drives fans to mechanical max. The real gate is `EC[0x06]`: `0x08` when manual override is active (Fan Max **and** Custom Fan modes both set this), `0x48` when the BIOS is doing automatic control. The legacy OMCC at `0x62` stays at `0x16` on every probe regardless of fan state, so the original v1.2.x `ManualReg=98` mapping never engaged anything on this BIOS. The 8BBE entry now declares `ManualReg=6 / ManualValueOn=8 / ManualValueOff=72`. Needs hardware confirmation by yunusemreyl — instructions in the OmenMon.xml comment.

### Added

- **Per-model manual-trigger overrides** in the `<Model>` schema: `ManualValueOn` / `ManualValueOff` are values written to `ManualReg` to engage / release manual fan control. Default to `FanManual.On` / `.Off` (`0x06` / `0x00`), so existing entries keep working unchanged. Plumbed through `PlatformPreset` → `Platform.cs` → `FanArray` constructor and used by `Get/SetManual()`.

- **Per-model temperature-sensor overrides** in the `<Model>` schema: `TempCpuReg` / `TempGpuReg` remap the named CPUT / GPTM sensors to a non-default EC offset, for boards that moved the real CPU/GPU temp registers away from the legacy `0x57` / `0xB7` addresses (e.g. 8C9C above). `Platform.InitTemperature()` swaps the address while preserving the `"CPUT"` / `"GPTM"` sensor name, so the GUI-form display, tray tooltip and Thermal-Panic logic all keep matching them by name.

  All four optional fields are only persisted on save when they differ from the defaults — existing model entries round-trip cleanly without picking up new noise.

## [1.3.2-reborn] - 2026-05-09

### Added

- **Model support: HP Omen 16 (2026, 8D07)** — added to the built-in model database. Confirmed via Auto-Calibration Wizard reports (issue #23): standard 2023+ register layout, 16-bit LE RPM tachometers at `0xB0`/`0xB2`. Owners get correct fan controls and RPM readings out of the box without running the wizard.
- **Model support: HP Omen 16-am1000 (2026, 8E71)** — added to the built-in model database. Confirmed via Auto-Calibration Wizard report (issue #22): standard 2023+ register layout, 16-bit LE RPM tachometers at `0xB0`/`0xB2`.

### Fixed

- **Omen Key now toggles the main window again** (issue #21). The original OmenMon used an inter-process message to show/hide the GUI when the Omen key was pressed; Reborn's strict single-instance lock silently dropped that message because no IPC handler had been wired up yet. Added an explicit `ToggleGui` window-message that is broadcast both from the `-run Key` task path and from any second-instance launch carrying the Omen Key environment marker. Users who have configured `KeyToggleColorPreset`, `KeyToggleFanProgram`, or `KeyCustomActionEnabled` keep the original `Key` IPC path so their custom action still fires; users with no key action configured get a reliable show/hide toggle.

## [1.3.1-reborn] - 2026-05-06

### Fixed
- **Model Support:** Native support for HP Victus 16 (8C9C). Fixed the "5500 RPM ghost fans" bug by implementing a dynamic 60x multiplier for `DirectMultiplier8` sensors in `AutoCal.cs` and `Fan.cs`. Corrected temperature registers to `0xB0` (CPU) and `0xB4` (GPU).

## [1.3.0-reborn] - 2026-05-05

### Added

- **Auto-Calibration Wizard:** Replaces the old manual "Contribute Hardware Data" flow on boards where HP has shuffled the EC register layout (Victus 2024+, post-8BD4 generation). Open the tray menu → **Auto-Calibrate & Diagnose…** to launch a guided 4-step stress sweep (0 → 30 → 70 → 100 % fan rate, ~12 s settle each). At every step OmenMon snapshots all 256 EC bytes, runs a heuristic diff scanner across the samples, and identifies the registers that behave like fan tachometers.

- **Heuristic scanner with three RPM-format detectors** (`Hardware/EcDiffScanner.cs`):
  - **Pattern A (16-bit Little-Endian):** classic Omen layout — `(r, r+1)` ushort that idles low and rises monotonically into the 1.5–8 krpm window.
  - **Pattern B (Period-Encoded 8-bit):** newer boards — single byte that **falls** monotonically with load (higher value = slower fan).
  - **Pattern C (Direct Multiplier 8-bit):** HP Victus 2024+ (e.g. 8BD4) — single byte that **rises** monotonically with load and where `byte * 100 = RPM`. Filters: byte ∈ \[10, 80\], idle ≤ 22, max ≥ 25, swing ≥ 10.
  - Every candidate must clear a monotonic-direction score across all sweep steps before being accepted, so a single noisy reading can't poison the result. Static bytes, slow-movers (Δ < 15, typical temperature sensors), and OmenMon's own write registers (XSS1/2, SRP1/2, 0x3A/B, OMCC, XFCD, FFFF, SFAN) are excluded up front.

- **Live read-path override:** On a successful scan, `Fan.GetSpeed()` immediately switches to the discovered registers + mode for the current session — no restart required. Pattern-specific math (×100, period, or 16-bit LE) is applied before the value reaches the UI. Persisted to a sidecar `OmenMon-AutoCal.xml` so the override survives a restart without the wizard touching the main `OmenMon.xml`.

- **Markdown report generator:** Wizard output is auto-copied to the clipboard, saved as `OmenMon-Calibration-<timestamp>.md` next to the executable, and (opt-in) opens the GitHub new-issue page. Report includes WMI baseboard, BIOS born-date, the full 4-step EC sweep as 16×16 hex grids, ranked candidates with scores, and the picked CPU/GPU registers.

- **Background-app priority guard:** Optional checkbox in the wizard dialog drops the priority of well-known CPU/GPU hogs (OBS, ffmpeg, Premiere, Photoshop, Blender, Unity, IDEs, MsMpEng, Spotify, VLC, ollama) for the duration of the test, then restores it. **Does not close user windows** — losing unsaved state for a 60-second sensor sweep is not a fair trade.

- **Safety guard-rails:** Wizard engages manual fan control with a 600 s firmware auto-restore countdown so the system self-recovers even on a hard crash mid-test. Prior fan state (levels / max / off / manual) is captured up front and restored in `finally{}` regardless of how the run exits. Cancellation honoured on every 1 s tick.

- **Model support: HP Victus 16-S0053NT (8BD4)** — added to the built-in model database and to `AutoCal.Prime()` known-board mappings. On this board the legacy tachometer offsets `0xB0/0xB2` have been repurposed as temperature sensors; the actual fan RPM tracks the level register (`EC[0x11]` for CPU, `EC[0x14]` for GPU mirror) decoded as Pattern C (`byte × 100`). Owners get correct readings out of the box without running the wizard.

### Changed

- Tray menu item "Contribute Hardware Data..." renamed to **"Auto-Calibrate & Diagnose..."**, now routed to the wizard. Old menu identifier preserved so existing config doesn't break.

## [1.2.1-reborn] - 2026-05-05 (Hotfix)

### Fixed

- **Hibernate regression on AC and battery:** v1.2.0 introduced two new hardware-access patterns that caused the same BIOS interference as the original v1.1.0 hibernate bug:
  1. `BuildTrayTooltip()` called `Fan.GetSpeed()` via WinRing0 every 3 seconds, even when neither the main form nor the dynamic icon was active. This added unsanctioned EC reads that had not existed previously.
  2. `CheckThermalPanic()` called `Platform.Fans.SetMax(true)` via BIOS Cmd 0x27, which — like the heartbeat `GetFanCount()` — can trigger HP firmware's power-management safeguards and force hibernate.
  Both calls now only happen when the dynamic icon is active (same hardware-access budget as v1.1.x).

- **`ThermalPanicEnabled` default changed to `false`** (was `true`). Thermal Panic is an opt-in feature. Enabling it with `ThermalPanicEnabled=true` in `OmenMon.xml` also requires `GuiDynamicIcon=true` so that sensor readings are already being taken. Without the dynamic icon, no panic check runs.

- **Tray tooltip** now shows only CPU/GPU temperatures from cached values — fan RPM is intentionally omitted to avoid WinRing0 EC reads outside the dynamic-icon hardware-access budget.
- **Tray tooltip CPU temp fallback:** On devices where the EC CPUT register (0x57) overlaps with firmware data and returns 0xFF (8C9C, 8BBE, and similar 2023+ models), the tray tooltip now falls back to the WMI BIOS temperature sensor instead of showing `--`.
- **Tooltip crash fix:** `SetNotifyText` now clamps the tooltip to 127 characters before calling `Os.SetNotifyIconText`, preventing an `ArgumentOutOfRangeException` crash when a long fan program name causes the string to exceed the OS limit.
- **Thermal Panic stuck-fans fix:** If `GuiDynamicIcon` is disabled while Thermal Panic is active, the stuck state is now cleared immediately (fans restored from max).
- **Config validation:** `ThermalPanicEnabled` is automatically disabled when `ThermalPanicTemperature` is 0 or `ThermalPanicHysteresis` ≥ `ThermalPanicTemperature`, preventing fans from being stuck at max due to nonsensical config values.

### Added

- **Model support: HP Omen 17 (2023, 8BAD)** — EC register layout confirmed via probe data (FanLevel at 0x34/0x35, matching BIOS GetFanLevel CPU=0x0D GPU=0x0D).
- **Model support: HP Victus 16 (2022, d1xxx / 8A25)** — added with standard register layout (FanRate confirmed at 0x2E/0x2F); note that Fan2 is unsupported on this model.

## [1.2.0-reborn] - 2026-05-04

### Added

- **Thermal Panic Mode:** When the maximum sensor temperature reaches the configured threshold (default 90 °C), OmenMon instantly forces both fans to maximum speed and shows a balloon alert. Fans are restored automatically once the temperature drops 5 °C below the threshold (configurable hysteresis). Configurable via `ThermalPanicEnabled`, `ThermalPanicTemperature`, `ThermalPanicHysteresis` in `OmenMon.xml`.

- **Fahrenheit display toggle:** Temperatures can now be shown in °F instead of °C. Toggle via **Settings → Show temperature in °F** in the tray menu. Applies to the main window sensor readouts, the dynamic tray icon, and the tray tooltip. Saved to `OmenMon.xml` (`TemperatureUseFahrenheit`).

- **Fan Profile Export / Import:** Share fan curves with the community. In the tray **Fan** submenu, click **"Export \<Name\>..."** to save any defined fan program as a portable `*.xml` file. Click **Import Fan Profile...** to load a shared curve — it is immediately added to the Fan menu and saved. Exported files use the same `<Program>` schema as `OmenMon.xml`, so they're human-readable and easy to tweak.

- **Rich tray tooltip (Dual-Temp):** The tray icon tooltip now shows `CPU: 78°C | GPU: 72°C` from cached sensor values, even when no fan program is running. During Thermal Panic the tooltip adds `⚠ THERMAL PANIC — fans at MAX`. Respects the °C/°F setting. Fan RPM is omitted to avoid extra WinRing0 EC reads per tick.

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
