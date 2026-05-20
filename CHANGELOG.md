# Changelog

All notable changes to this project will be documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [1.4.1-reborn] - 2026-05-18

> **First-week-after-v1.4.0 field-report sweep.** Patch release addressing the cluster of issues (#32 / #56 / #57 / #58) that arrived once the Auto-Calibration Wizard reached users on entry-level Victus 15/16 SKUs whose physical fan ceiling sits below the BIOS rate-limiter's threshold. Two layers of defence: an explicit board safety list (so wizard + GUI both skip the dangerous 100% command on confirmed-vulnerable models) and a generic plateau detector in the wizard (so unknown future SKUs with the same firmware signature abort gracefully and surface a "please report your ProductId" note in the Markdown report).

### Fixed

- **Auto-Calibration Wizard misidentified CPU/GPU RPM on HP Omen (8D87, 2025)** (issue #61, reported by @snowfallhateall). The heuristic picked incorrect registers when run in normal mode. Adding 8D87 to the native database with the actual 16-bit LE RPM registers (0x70/0x9F) overrides these bad wizard guesses.
- **Auto-Calibration Wizard misidentified CPU/GPU RPM on HP Omen 17 (8600, 2019)** (issue #42, reported by @duskw4lker). The heuristic picked period-encoded-looking registers 0x06 and 0xF1 when run in normal mode. Adding 8600 to the native database with the actual 16-bit LE RPM registers (0x45/0x47, which are revealed under stress test load) overrides these bad wizard guesses.
- **EC freeze on 8D07 and 8BAD during the Auto-Calibration Wizard's 100% step** (issues #56 reported by @ghend-oss, #58 reported by @MartinSalg818). The v1.4.0 `FanArray.HasMaxFanFreeze` guard covered only 8C30. Both boards share the same firmware rate-limiter signature — pushing the fans past their ~3600-3800 RPM physical ceiling locks the EC fan controller until reboot. 8D07 and 8BAD are now in the same safety list, so the wizard filters 100% out of its profile and the tray / main-form "Max Fan" toggle requires explicit confirmation before issuing `SetMax(true)`.
- **Auto-Calibration Wizard misidentified CPU RPM at 0x30 on 8E35** (issue #57, reported by @ClockworkNirvana). The scanner locked onto a latched status word that jumped from 30 → 6430 at the first non-zero step and stayed there, scoring it as a perfectly monotonic RPM candidate. 8E35 is now in the native model database (`OmenMon.xml`) with the actual register layout (`0xB0`/`0xB2` LE16, identical to 8D07), so the preset is loaded without ever running the wizard. The board also shows the same 70 % → 100 % plateau as 8C30 / 8D07 / 8BAD — confirmed via raw EC-dump analysis (zero RPM gain on GPU, 16 RPM regression on CPU between commanded levels) — and is in the `HasMaxFanFreeze` list.
- **GPU fan speed follows CPU temperature instead of GPU temperature in custom fan curves** (issue #62, reported by @Bart82). `FanProgram.Update()` called `GetMaxTemperature()` — which returns the single hottest reading across all sensors (typically the CPU) — and used that one value to look up fan levels for **both** fans. The GPU temperature had zero influence on GPU fan speed. The fix adds `GetCpuTemperature()` / `GetGpuTemperature()` to `Platform` and performs two independent curve lookups per tick: the CPU fan level is determined by the CPU temperature, and the GPU fan level is determined by the GPU temperature. If the GPU sensor is unavailable (returns 0), the GPU fan falls back to tracking the CPU temperature — preserving the old behavior as a safe default. `GetMaxTemperature()` still runs each tick (without extra EC reads) to keep thermal-panic and tray-icon logic working.

### Added

- **HP Omen (8D87, 2025) in the native model database** (`OmenMon.xml`). Register layout is the standard 2023+ layout — FanLevel at `0x11`/`0x12`, rate write at `0x3A`/`0x3B`, rate read at `0x2E`/`0x2F`, and 16-bit LE RPM tachometers at `0x70`/`0x9F`.
- **HP Omen 17 (8600, 2019) in the native model database** (`OmenMon.xml`). Register layout uses the classic 2022-style — FanLevel at `0x34`/`0x35`, rate write at `0x2C`/`0x2D`, rate read at `0x2E`/`0x2F`, and 16-bit LE RPM tachometers at `0x45`/`0x47`.
- **Wizard plateau detection** (`App/Cli/CliOpCalibration.cs`). After each commanded fan step the wizard now reads live tachometer RPM via the same `Fan.GetSpeed()` path the GUI uses and tracks it across the run. When both fans show <150 RPM gain compared to the highest prior step and the latest reading is at least 1500 RPM (so the check doesn't fire during the legitimately-small 0 % → 30 % spin-up or on a board whose preset returns zero), the wizard concludes the board has hit its physical fan ceiling and aborts any higher steps still queued — limiting time spent in the rate-limiter danger zone. Generic by design: it catches future SKUs with the same firmware signature without requiring a `HasMaxFanFreeze` list entry. The Markdown report carries the live RPM table and a clear "PLATEAU DETECTED — please report so `<ProductId>` can be added to the safety list" callout above the device section, so maintainers see the symptom immediately when triaging an issue.
- **HP Victus 15 (8E35) in the native model database** (`OmenMon.xml`). Register layout is the canonical Victus 15 2022-style — FanLevel at `0x34`/`0x35`, rate write at `0x2C`/`0x2D`, rate read at `0x2E`/`0x2F`, 16-bit LE RPM tachometers at `0xB0`/`0xB2`. Adding this entry means the preset is correct on first launch and the Auto-Calibration Wizard is no longer needed for routine fan control on this SKU.
- **Battery-glitch hibernation guard** (`Library/PowerGuard.cs`, issue #59 + the cluster of similar reports under #37 / #56 comments). New defensive layer that watches `SystemInformation.PowerStatus` on each GUI timer tick. If Windows reports a drop of >= 30 % battery percentage within 5 s **while the laptop is on AC**, OmenMon treats it as a torn read from the Smart Battery System path and tells Windows to skip the Critical Battery Action via `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)` for the next 30 s, surfacing a balloon tip with the before/after percentages so the user sees what triggered it. The guard is suppressed on battery power — a legitimate low-battery state must still hibernate normally. Configurable in `OmenMon.xml`: `BatteryGlitchGuard` (boolean, default true), `BatteryGlitchDropPercent` (default 30), `BatteryGlitchWindowMs` (default 5000), `BatteryGlitchHoldMs` (default 30000). Set `BatteryGlitchGuard=false` to revert to the OS's verbatim hibernate behaviour. The held ExecutionState is observable via `powercfg /requests` for support diagnostics.

### Deferred

- **HP Omen 8BCD native database entry** (issue #37, reported by @Byteme-dot). Two independent reports against the same `ProductId` disagree on the actual tachometer registers: @Byteme-dot's wizard found CPU 0xE3 PeriodEncoded8 + GPU 0xF1 LE16, while @stf1o's wizard in #51 found 0xDA LE16 with no second fan. HP recycles `ProductId` across SKUs that share a board family but differ in fan topology, so shipping either layout natively would put garbage RPM on the other user's machine. @Byteme-dot's wizard candidate at `0xE3` also fails the "period-encoded values must decrease monotonically with load" invariant — values were 75, 66, 80, 41 across 0/30/70/100 % — which means the wizard locked onto a register that happened to mirror the commanded rate during the sweep, not a real tachometer. Both users' Auto-Calibration sidecars (`OmenMon-AutoCal.xml`) keep their installs working in the meantime. Resolution requires either a sustained-load manual probe from @stf1o or independent confirmation of either layout from a third 8BCD owner.

### Known issues (no actionable code fix yet)

- **Random fan spikes during light browsing on 8DCD** (issue #49, reported by @LightningFang). Diagnosed as an EC polling collision between OmenMon and a concurrent EC consumer — the user confirmed MSI Afterburner was running with an active overclock during the spikes. Mitigation is environmental: close any third-party software that polls the EC at sub-second cadence (HWiNFO sensor polling, Afterburner / RTSS, AIDA64 sensor panel, Armoury Crate, G HUB, iCUE), and stop the `OMEN.CommandCenter.Service` / `OMEN.AI` services from `services.msc` if HP Omen Gaming Hub is installed. The symptom is invisible by the time the user reaches GitHub — `OmenMon.exe -Diag` captured during a spike will show the EC trace's torn-read footprint.

## [1.4.0-reborn] - 2026-05-17

> **Security release + post-PawnIO regression sweep.** OmenMon no longer ships the WinRing0 kernel driver. The kernel-mode access layer has been replaced with [PawnIO](https://pawnio.eu/), whose Microsoft-signed, HVCI-compatible driver does not trigger Windows Defender warnings the way WinRing0 did. Functionality is preserved; the public `Ring0` API is unchanged so `Hardware/Ec.cs` and every other caller stayed put. This release also bundles the user-reported regressions and model-database additions surfaced in the first week of post-PawnIO field testing.

### Fixed (post-PawnIO regressions)

- **GUI crash on open with `BIOS call failed: Unknown response from BIOS: 4`** (Discord report, HP Omen Transcend 14 fb0118TX). The tray icon opened fine but double-clicking it to bring up the main form raised an unhandled `BiosException` and tore the application down. Root cause: `Hardware/Bios.Check` rejected any status code outside `{0, 3, 5}` even though the original author had explicitly noted that codes 1, 4, 6 and 46 had also been observed in the wild but were not understood. Codes 1/4/6/46 are now silently ignored (the call returns instead of throwing) regardless of `Config.BiosErrorReporting`, so the rest of the GUI can come up. Code 3 (`command not available`) keeps its existing behaviour of raising a `BiosException` — only the previously-unclassified codes are downgraded.

- **Auto-Calibration "100%" step undershot real max RPM by ~30% on multiple boards** (issues #40 / #41 / #52, reported by @ghend-oss / @MartinSalg818 / @ethernetme). The wizard kept the EC in manual fan-level mode throughout the sweep, including the final step that called `SetMaxFan(true)`. On 2022/2024 boards the manual flag pins the fans at `Config.FanLevelMax` (~3.4-3.8 kRPM) and the BIOS max-fan command is silently no-op'd — so the wizard's calibration plateau was systematically lower than the fan's physical ceiling. Fix in `App/Cli/CliOpCalibration.cs`: disengage manual mode immediately before `SetMaxFan(true)` on the 100% step, then re-engage it on any subsequent sub-100% step. Boards already covered by the `HasMaxFanFreeze` guard (8C30) are still skipped at 100% — they keep their `<100%` profile and never reach the manual-toggle code path.
- **Fan programs left the GPU fan parked while the CPU fan responded normally** (issue #39, reported by @ghend-oss on 8D07). On boards where a prior `SetOff(true)` is still in the EC's switch register, `SetLevels` succeeds for one fan and silently no-ops for the other — most often presenting as a stuck-off GPU fan while CPU continues to ramp with the fan curve. `Hardware/FanProgram.SetFanLevel` now clears the fan-off latch (only if it is actually set) before each level write, so a program tick reliably reaches both fans. The "Max Fan" latch is deliberately not touched here — clearing it would defeat Thermal Panic's safety override, which is asserted only on the transition into panic and not re-asserted every tick.
- **HP Omen 8DD0 "50000 RPM" after a misfired auto-cal run** (issue #33, reported by @DreamStare0). Adds a built-in `AutoCal.Prime()` mapping for 8DD0 pointing at the canonical 0xB0/0xB2 LE16 tachometers, so the bogus 0x02 / 0x88 PeriodEncoded8 offsets the heuristic occasionally locks onto cannot reach `Fan.GetSpeed()` after the >16-byte distance sanity check in `AutoCal.Load()` discards the bad sidecar.
- **Stale sidecars from v1.3.x kept overriding fixed built-in mappings.** `AutoCal.Load()` previously trusted any sidecar that survived the distance sanity check, so users who had run the wizard on an older release continued to see the broken mapping even after upgrading. `Load()` now also discards a sidecar when it disagrees with a `KnownBoards` entry for the same product — `KnownBoards` is the hand-verified ground truth and only contains products where the wizard heuristic is known to misfire. Real-world effect: 8C9C owners upgrading from v1.3.4 (where the released mapping was `0xF1 × 60` and produced ~120 RPM at audible MAX, issue #28) automatically get the new `BiosLevelMirror` mapping on first launch instead of having to manually delete `OmenMon-AutoCal.xml`.
- **Tray "Max fan" toggle stayed silently off when the fan-off latch was set** (post-PawnIO regression). `App/Gui/GuiMenu.EventActionFanMax` now clears `SetOff(false)` before `SetMax(true)`, matching the main form's existing sequence so the BIOS command actually takes effect.
- **GUI "Constant speed" branch lost the post-write mode-refresh when the user cancelled the 100% safety dialog and fell back to safe levels.** Added the same `SetMode(GetMode())` apply step the normal constant-speed path uses, so the safe levels are latched into the EC instead of being overwritten on the next refresh tick.
- **Build error**: a leftover call to a non-existent `UpdateFanMode()` in `App/Gui/GuiFormMain.cs` (introduced by the 8C30 safety dialog) prevented the project from compiling on a clean checkout. Replaced with `UpdateFanCtl()`, which is the function the surrounding code already uses to refresh fan UI state.
- **`App/Gui/GuiMenu.cs`** now imports `OmenMon.Hardware.Platform` (the namespace `FanArray` lives in). Without it, the 8C30 warning would have failed to compile on first build.

### Added (diagnostics)

- **Telemetry-free crash dumper.** `App/Crash.cs` registers handlers for `AppDomain.UnhandledException` and `Application.ThreadException` at the earliest possible point in `Main()` (before `Config.Initialize` even runs). When any fatal exception escapes, OmenMon writes `OmenMon-crash-yyyy-mm-dd-HHmmss.log` next to the executable containing the full exception chain, process metadata, **and** the same diagnostic bundle the new `-Diag` verb produces. Falls back to `%LOCALAPPDATA%\OmenMon-Reborn\` if the install directory is read-only (Program Files installs without elevation). **Nothing is uploaded** — the file sits on disk until the user chooses to attach it to a GitHub issue.
- **`OmenMon.exe -Diag` CLI verb.** Bundles everything the maintainer needs to triage a report into one Markdown blob: OmenMon version, OS version, PawnIO driver status (`Ring0.GetStatus()`), the resolved model preset (or a "no native preset" notice), the AutoCal sidecar contents, a lock-free EC read/write trace covering the last few minutes of activity, an inventory of any crash logs on disk, and a single-snapshot `-Probe` dump. Writes to `OmenMon-Diag-yyyy-mm-dd-HHmmss.md` next to the executable.
- **Tray menu "Copy Diagnostic Info" entry.** One-click clipboard copy of the same diagnostic bundle, for users who would never open a command prompt but will happily paste into a GitHub issue. Sibling of the existing "Auto-Calibrate & Diagnose…" entry.
- **EC read/write ring buffer (`Library/EcTrace.cs`).** Records the last 1024 EC operations (timestamp, register, value, op kind) into a lock-free circular buffer hooked into `Hardware/Ec.ReadByte` / `WriteByte`. Costs ~16 KiB of resident memory and the price of one `Interlocked.Increment` per EC op. Surfaces through both the crash log and `-Diag` as a compact Markdown table — invaluable for diagnosing intermittent issues like #49 (random fan spikes), where the symptom is invisible by the time the user opens GitHub.

### Added (model database)

- **HP Omen 16-ap0007ns (8D26)** — added with standard 2023+ layout (`FanLevel 0x11/0x12`, `FanRateWrite 0x3A/0x3B`, RPM `0xB0/0xB2` LE16). Auto-Calibration confirmed CPU max ~3.4 kRPM / GPU max ~3.6 kRPM during the wizard sweep; real-load ceiling reaches ~4.9 kRPM via the new manual-mode toggle on the 100% step (issue #52).
- **HP Victus 16 (88EB, 2021)** — earliest Victus 16 generation, standard 2023+ layout, LE16 RPM at 0xB0/0xB2 (CPU max ~3134, GPU max ~3385 RPM). Auto-Calibration confirmed by @deadpoolstark (issue #48).

### Original release notes (PawnIO migration)

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