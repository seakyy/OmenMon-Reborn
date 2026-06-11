# Changelog

All notable changes to this project will be documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [1.4.6-reborn] - 2026-06-12

> **UI responsiveness restored + CLI restored + temperature-policy fix.**
> v1.4.5's threading/safety sweep serialised hardware access correctly but left
> the periodic EC/BIOS traffic on the WinForms UI thread, so a contended EC
> mutex stalled the tray menu and window dragging for seconds (#98). v1.4.6
> moves **all** periodic hardware access onto a dedicated background monitor
> thread that publishes immutable sensor snapshots for the UI to render — the
> message pump never waits on the EC again. On top of that: the CLI works again
> (#101), a stuck EC temperature byte can no longer pin the fan curve (#97),
> recoverable EC-lock timeouts no longer raise modal error boxes (#94), and the
> model database gains 88ED and 8A3E.

### Fixed

- **CLI produced no output for any argument (#101, reported by @David112x).**
  The redirection guard added to `Cli.Initialize()` in v1.4.2 (for the issue #76
  crash) early-returned whenever `Console.IsOutputRedirected` was true — but a
  GUI-subsystem process launched from cmd/PowerShell *without* redirection has
  NULL standard handles, which .NET also reports as "redirected". The console
  was therefore never attached and every CLI invocation (`-Bios`, `-Ec`,
  `-Usage`, …) wrote into the void. The guard now only skips attaching when a
  *real* non-console handle exists (`Kernel32.GetStdHandle`), so plain CLI use
  prints again while `-Diag > file.md` style redirection keeps working.
- **Sluggish tray menu and "teleporting" window drag since v1.4.5 (#98,
  reported by @snowfallhateall).** New `GuiMonitor` background thread owns all
  periodic hardware work — sensor sampling, the fan-program tick, thermal-panic
  checks, constant-speed countdown maintenance, and the BIOS heartbeat — and
  publishes an immutable `MonitorSnapshot` the UI thread renders lock-free. The
  tray menu and the main form no longer perform any EC/BIOS I/O on the UI
  thread during passive operation: opening the tray menu now requests one fresh
  sample off-thread and renders from the snapshot, instead of issuing EC reads
  (`GetMode`/`GetMax`/`GetOff`) that could block seconds behind the
  `Global\Access_EC` mutex. User-initiated control actions are serialised with
  the monitor through a single in-process hardware lock; balloon tips and
  fan-program status updates cross back to the UI thread via a message queue.
- **CPU temperature stuck at a constant reading pinned the fan curve (#97,
  reported by @snowfallhateall, 8D87).** On 8D87 the legacy CPUT register
  `0x57` lands on firmware string data and reads a permanent 52 (ASCII `'4'`).
  `Platform.GetCpuTemperature()` trusted CPUT whenever it was non-zero, so fan
  programs followed the 52 °C curve row while the die overheated. The CPU
  temperature is now the **higher** of CPUT and the WMI BIOS sensor — covering
  both known failure shapes (CPUT stuck non-zero, and CPUT reading 0xFF→0 as on
  8C9C/8BBE) with one policy that can only ever err towards more cooling.
- **"Failed to acquire embedded controller exclusive lock" error boxes (#94,
  reported by @DreamStare0, also #96).** EC-mutex timeouts on the periodic /
  recoverable paths (background monitor, AutoConfig startup thread, the
  Auto-Calibration worker) now degrade to a skipped operation that retries next
  tick, instead of a modal error box — the once-per-startup box (HP's own
  services hold `Global\Access_EC` at logon) and the repeated boxes during
  calibration sweeps are gone. User-initiated actions stay loud. Timeouts are
  counted and surfaced as a new "EC lock timeouts this session" row in `-Diag`
  so contention still shows up in field reports.
- **Calibration pause race.** `GuiMonitor.Pause()` could return while one last
  monitor pass was still about to start; the paused check now happens inside
  the pass gate, so after `Pause()` returns the wizard owns the hardware alone.

### Added

- **HP Victus 16-e0xxx (88ED, 2022) native model entry (#99, reported by
  @robbert1978).** Classic 2022 layout (level `0x34`/`0x35`, rate write
  `0x2C`/`0x2D`), 16-bit LE tachometers at `0xB0`/`0xB2` — exactly the preset
  the auto-detector chose on the reporter's machine, field-tested and now
  pinned natively.
- **HP Victus 15-fb0102la (8A3E, 2023) read-only RPM mapping (#96, reported by
  @David112x).** Confirmed 16-bit LE tachometers at `0xB0`/`0xB2` (CPU idle
  ≈435 — never fully stops — max ≈5404; GPU 0…≈5195 RPM), added to
  `AutoCal.KnownBoards`; fan control stays on the default 2022 layout the
  calibration sweep already drove successfully.
- **`-Diag`: EC lock timeout counter** in the Kernel Driver section (issue #94
  forensics).
- **Model coverage notes (wiki):** HP Omen 15-en0037AX (8787, #95) confirmed on
  the default 2022 layout — the probe's `GetFanLevel` "Unknown response from
  BIOS: 45" is expected on the 2020 generation and harmless; Victus 16-s1084AX
  (#100) documented as a pending second hardware variant behind `8C9C` (real
  tachometers at `0xD6`/`0xD8`) awaiting a `-Diag` Product ID confirmation
  before any shipped-mapping change.

## [1.4.5-reborn] - 2026-06-04

> **EC read-path hardening + field-report sweep.** This release attacks the
> root cause of the recurring "RPM = negative / millions" and bad-temperature
> reports (#86) at the Embedded Controller read path itself, rather than per
> board: a failed read now fails *cleanly* instead of returning a garbage byte,
> 16-bit reads are validated against torn high/low bytes, the monitoring tick
> batches its reads under a single mutex hold, and the #88 busy-wait backoff is
> graduated so it can no longer pin the EC mutex past its timeout. Plus a
> lifecycle-lock and a memory-model fix on the threading side, a null-guard for
> non-Optimus systems, a thermal-panic safety fix, a more robust auto-detector,
> live fan telemetry in `-Diag`, and a batch of model-database / calibration
> updates.

### Fixed

- **Garbage bytes after the EC wait fail-limit (A1, issue #86).** `WaitRead()`
  returned `true` unconditionally once `WaitReadFailCount` exceeded `EcFailLimit`
  (15) — even though `OutFull` was never set — and the caller then read a
  stale/garbage Data byte. Because the counter only reset on success, a briefly
  wedged EC latched OmenMon permanently into the garbage path for minutes. This
  is the most likely common root cause of the recurring "RPM = negative /
  millions" and bad-temperature reports. A sustained wait-failure now returns
  `false`, so the retry wrapper keeps the last good value instead of fabricating
  one.
- **Torn 16-bit reads (A2, issue #86).** `ReadWordImpl` reads the low and high
  byte as two separate EC transactions, so an EC state change between them yields
  a self-inconsistent word (implausible RPM). `ReadWord` now requires two
  consecutive identical reads before trusting the value, falling back to the most
  recent successful read.
- **Per-sensor EC lock churn on the monitoring path (A3, issue #86).** Each
  `EcComponent.Update()` opened the driver and took/released the cross-process
  `Global\Access_EC` mutex on its own, so a monitor tick churned the lock once per
  sensor and widened the collision window with the kernel ACPI EC driver. New
  `Hw.EcExecBatch()` holds the EC open and the (reentrant) mutex for the whole
  tick; `Platform.UpdateTemperature()` reads the full sensor array inside one
  batch.
- **EC wait backoff could exceed the mutex timeout (A4, issue #88).** The #88
  backoff slept a full 1 ms per iteration while holding `Global\Access_EC`, so a
  Word read could pin the mutex past `EcMutexTimeout` (200 ms) and starve other
  waiters into `ErrEcLock`. `Wait()` now yields the timeslice (`Sleep(0)`) for a
  configurable `EcWaitYieldCount` (default 10) iterations before escalating to the
  1 ms sleep, cutting the worst-case per-`Wait` hold from 25 ms to 15 ms.
- **EC lifecycle race (B1).** `Initialize()`/`Close()` checked and set
  `IsInitialized` without synchronisation while reads ran from the GUI tick,
  AutoConfig thread, key handler and heartbeat — a use-after-close window. Both
  now run under a per-singleton lifecycle lock.
- **`gpuTempObserved` data race (B2).** The sticky observed-GPU-temperature flag
  is now `volatile` so its first-non-zero transition publishes deterministically.
- **`NullReferenceException` in `NvMuxGetState()` on non-Optimus systems (C1).**
  The mux registry key only exists on NV-Optimus laptops; the method now returns
  `NvMuxState?` and yields `null` when the key/value is absent instead of
  dereferencing it.
- **Thermal-panic protection was tied to the dynamic tray icon (C3).** The
  forced temperature read and `CheckThermalPanic()` lived inside the
  `if(Icon.IsDynamic)` branch, so turning the dynamic icon off silently disabled
  over-temperature protection. Thermal panic now runs on every icon tick whenever
  enabled (or a latched panic needs clearing), independent of icon mode, while
  still skipping the EC read when neither the icon nor panic needs it.
- **8A18 (OMEN 17 ck1000nw) post-calibration fan lock (issue #84).** The
  confirmed `0xB0`/`0xB2` tachometers are now pinned in `AutoCal.KnownBoards` too,
  so `Load()`'s collision self-heal always has a verified built-in mapping to fall
  back to and a stale/foreign sidecar can no longer relock the fans.
- **GPU fan row showed garbage on single-fan boards (issue #81, 8BB3).** A
  `Mapping.SingleFan` flag now makes `Prime()` mirror the resolved CPU mapping onto
  the GPU when the board is single-fan and the GPU has no override, so both rows
  report the one real fan instead of decoding `0xF1` as a word.

### Added

- **More robust EC layout auto-detection (issue #37).** The 2022 match no longer
  *requires* a live tachometer (idle fans / off-`0xB0` tachs made it misfire to the
  2023 layout); a plausible fan level at the layout's setpoint register is required
  instead, and a fan-level fallback tier now handles boards whose `CPUT` is neither
  a valid temperature nor the `0xFF` sentinel. Read-only and conservative.
- **Live Fan Telemetry section in `-Diag` (issue #49).** Per-fan BIOS level,
  duty-cycle rate, resolved live speed, and the exact register/mode/multiplier
  `Fan.GetSpeed()` used — so a wrong RPM is immediately traceable to the register.
- **Native HP Omen Max 16 (8D41, 2025) entry (issues #87, #90).** Graduated from
  the read-only `AutoCal` RPM mapping to a full `<Model>` entry with the confirmed
  16-bit LE tachometers at `0x5C` (CPU) / `0x9F` (GPU). Fan-control registers are
  the legacy defaults the unknown-model fallback already applied (non-regressive
  for control) and remain flagged unverified pending an owner `-Diag`.
- **Omen 16-am1001nw coverage (issue #31).** Documented as covered by the existing
  `8E71` (Omen 16-am1xxx) entry; `@Bart82`'s board is the same `#88` EC-timeout
  reporter and benefits from the A4 backoff fix.
- **`EcWaitYieldCount` configuration knob** in `OmenMon.xml` (default 10), wired
  through `Config`/`ConfigData`, plus documentation of the EC-timing knobs and an
  RPM/temperature troubleshooting section (issue #14).
- **HP OMEN 16 wd0xxx (8BA9, 2024) RPM read mapping** (`Library/AutoCal.cs`
  `KnownBoards`, issue #92 reported by @M1918IIBAR). The Auto-Calibration scan found
  a single 16-bit LE tachometer at `0xF1` and no GPU fan. The raw EC dumps confirm it:
  `EC[0xF1..0xF2]` decodes to `0x0701` = 1793 RPM at 0% (idle) and `0x1405` = 5125 RPM
  at 100% (max), matching the reported idle/max exactly and rising monotonically across
  the 0/30/70/100% steps — LE16, not the `DirectMultiplier8` single-byte read 8BB3 uses
  (which would decode the 100% step as 0x05 × 100 = 500 RPM). Added as a **read-only**
  `KnownBoards` mapping flagged `SingleFan`, so RPM now displays correctly out of the box
  and the GPU row mirrors the one real fan instead of decoding `0xF1` as a second word
  (#81 pattern). No `<Model>` entry / fan-control registers are committed for this board —
  a wrong `ManualReg`/`ModeReg` could lock the EC — so fan *control* stays on the
  auto-detector's safe legacy fallback pending a confirmed register map.

### Deferred (need confirmed data before a code change is safe)

- **HP Omen 16-wd0004nw (#51 model / #75 calibration), xd0020ax (#77), and the
  AMD Ryzen 9 7945HS SKU (#85).** No confirmed Product ID + register dump is
  available, and HP recycles Product IDs across regional/CPU variants — a prior
  7945HS-class report (#76/#85 on `8BCA`) already gave conflicting tachometer
  layouts. Shipping a guessed register map risks garbage RPM or a fan-controller
  lock on other owners' machines. All are usable today via the read-only
  auto-detector + the Auto-Calibration wizard; documented under "Pending field
  reports" in `wiki/Model-Database.md` with exactly the `-Diag`/calibration data
  needed to promote each to the shipped database.

## [1.4.4-reborn] - 2026-06-02

> **Post-v1.4.3 field-report sweep.** A patch closing the non-pending issues that arrived after v1.4.3: a new board for the native model database, a tray-menu fan-control gap that left fans pinned at maximum after switching to an automatic mode, a calibration self-heal for a CPU fan reading that mirrored the GPU after a single-fan scan, a misleading Auto-Calibration report on boards whose RPM is already handled by a built-in mapping, an EC busy-wait backoff that stops OmenMon hammering the controller while the OS/BIOS holds it (a suspected cause of ACPI-timeout shutdowns), and a read-only RPM mapping for the 2025 Omen Max 16. No hardware-control behaviour changes beyond releasing Max Fan when the user picks a fan mode.

### Fixed

- **Occasional whole-system shutdowns while OmenMon is running** (issue #88, reported by @Bart82 on an HP Omen 16-am1001nw). The user's event log showed the classic footprint of EC contention: ACPI logging "Embedded Controller did not respond before timeout", followed by a clean `winlogon.exe` shutdown (reason `0x500ff`) and a BIOS panic shutdown. `EmbeddedControllerAbstract.Wait()` busy-spun the EC status port up to `EcWaitLimit` (30) iterations with **no delay** between reads — the original author had even left a `// Thread.Sleep(1)` hint in the source. OmenMon can take the `Global\Access_EC` mutex to coordinate with other userspace tools (HP Omen Gaming Hub), but the **kernel ACPI EC driver is not gated by that mutex**, so a tight, back-to-back status poll can collide with an in-flight OS/BIOS EC transaction and leave the controller wedged long enough for ACPI to time out. `Wait()` now backs off to a 1 ms `Thread.Sleep` once an initial fast spin is exhausted (new `EcWaitSpinCount`, default **5**), so a contended EC yields the CPU to the OS instead of being hammered. The common case — EC ready within a spin or two — never sleeps, so GUI refresh latency is unchanged; only a genuinely busy controller pays the millisecond. Configurable in `OmenMon.xml`: set `EcWaitSpinCount` to a value `>= EcWaitLimit` to restore the previous pure busy-spin. This reduces the collision window from userspace but cannot fully serialise against the kernel ACPI driver; users still seeing shutdowns should also close other EC consumers (Omen Gaming Hub / `OMEN.CommandCenter.Service`, HWiNFO, Afterburner/RTSS, AIDA64) as noted under the v1.4.1 known-issues.

- **Model-preset lookups were case-sensitive while every other ProductId comparison was case-insensitive** (Copilot review on PR #89). `Config.Models` was a default `Dictionary<string, …>` (ordinal/case-sensitive keys), but `AutoCal.KnownBoards` and `AutoCal.Load()`'s product-ID compare both use `OrdinalIgnoreCase`, and the running board's ID comes straight from WMI (`Settings.GetProduct()`), whose casing is not guaranteed. A lower-/mixed-case WMI product would have missed its native preset in `AutoCal.CollidesWithNativePreset()`, silently disabling the #83 mirror-collision self-heal. `Config.Models` is now constructed with `StringComparer.OrdinalIgnoreCase` so all preset lookups behave consistently. Shipped `<Models>` keys are uppercase hex by convention and remain unique under case-folding; authors adding entries must keep keys unique (two keys differing only by case would now overwrite each other on load).

- **Real-time CPU fan reading mirrored the GPU fan after an Auto-Calibration run** (issue #83, reported by @MartinSalg818 on 8BAD). On a board whose native preset drives the two fans from distinct tachometer registers (8BAD: CPU `0xB0`, GPU `0xB2`), an Auto-Calibration scan that detected only **one** fan could make both readouts mirror: `EcDiffScanner` hands its sole candidate to `CpuFan`, and when that candidate is actually the GPU's register the persisted sidecar overrode the CPU read to `0xB2` while the GPU side, having no override, fell back to the preset's (same) `0xB2` — so both the CPU and GPU readouts showed the GPU's RPM. New `AutoCal.CollidesWithNativePreset()` compares the *effective* register each fan will read (the sidecar override if present, otherwise the preset register `Fan.GetSpeed()` falls back to) and, on a board whose built-in preset uses distinct CPU/GPU registers, treats a collapse-to-one-register as the mis-detection it is. `AutoCal.Load()` self-heals on the next launch by discarding the offending sidecar and falling back to the verified preset; the wizard's apply step (`ApplyToLiveSession`) refuses to persist such a sidecar in the first place, keeps the built-in mapping, and records a "detected register not applied" note in the report. Single-fan SKUs whose preset legitimately shares one register (8BB3, `FanSpeedReg0 == FanSpeedReg1`) and `BiosLevelMirror` boards whose 0 register is a sentinel (8C9C) are exempt, and a benign single-fan sidecar that pins the *correct* register is still honoured — only a CPU↔GPU register collision is rejected. Read-path only: no EC writes change, the fallback target is the same verified preset the model already ships.

- **Fans stayed pinned at maximum after switching to an automatic mode from the tray menu** (issue #77, reported by @AbhinavGenos on 8BCD). "Max Fan" is a separate BIOS toggle (`SetMaxFan`) from the fan **mode** (`SetFanMode`), and the tray fan-mode submenu's `EventActionFanMode` only called `SetMode()` — so choosing *Auto/Default* (or any mode) while Max Fan was engaged left the fans running at maximum. The main-window "Automatic" path already released Max Fan before switching (`GuiFormMain`), so this only affected the tray submenu. `EventActionFanMode` now releases a manually-engaged Max Fan when the user selects a mode, then re-asserts the mode so the fans return to the automatic curve. The release runs even when the chosen mode equals the current one — Max Fan can sit on top of any mode (the "already in Default, toggled Max Fan, now click Default to go back" case, where `SetMode` alone is a no-op). An active Thermal Panic is left untouched: it is a safety feature with its own max-fan state machine that would simply re-assert on the next hot tick. The change only ever *lowers* fan speed, so it cannot trigger the 100%-fan EC-freeze quirk on `FanArray.HasMaxFanFreeze` boards.
- **Auto-Calibration report raised a false "no plausible registers / share your dumps" alarm on boards with a built-in RPM mapping** (issue #81, reported by @jpcaldwell30 on 8BB3). The `EcDiffScanner` heuristic only recognises 16-bit LE tachometers, so single-fan boards that report RPM via `DirectMultiplier8` (8BB3 at `EC[0xF1]`) or `BiosLevelMirror` (8C9C) always scanned blank — even though their live RPM is already correct from `AutoCal.KnownBoards`. The wizard now detects a `KnownBoards` match (`AutoCal.IsKnownBoard`) and, when the scan finds nothing, prints an "this is expected, RPM is already handled natively" note instead of the alarming default text. RPM behaviour is unchanged; this is a report-clarity fix only.

### Added

- **HP Omen Max 16 (8D41, 2025) RPM read mapping** (`Library/AutoCal.cs` `KnownBoards`, issue #87 reported by @Keith1341). The Auto-Calibration report showed an empty live-RPM column at every step even though the scanner ranked candidate registers — because `Fan.GetSpeed()` fell back to the default `0xB0`/`0xB2` preset, which is wrong for this 2025 "Omen Max" layout. The raw EC dumps confirm 16-bit LE tachometers at `0x5C` (CPU) / `0x9F` (GPU): the 100 % step decodes `EC[0x5C..0x5D] = 80 16` → 5760 RPM (CPU) and `EC[0x9F..0xA0] = 85 19` → 6533 RPM (GPU), matching the report's detected maxima, with the 0 % step reading 0/0. Added as a **read-only** `KnownBoards` mapping (primed at startup via `AutoCal.Prime()`), so RPM now displays correctly without running the wizard. No `<Model>` entry / fan-control registers are committed for this new layout — a wrong `ManualReg`/`ModeReg` could lock the EC, so fan *control* on 8D41 still needs a confirmed register map before it ships.

- **HP Omen (8A14, 2021) in the native model database** (`OmenMon.xml`, issue #82 reported by @BinaryRider). Confirmed via the user's Auto-Calibration report: the raw EC dumps decode exactly to the reported live RPM for a 16-bit LE tach at `0xB0`/`0xB2` — at 100 % `EC[0xB0..B1]=0A 0F` → 3850 RPM (CPU) and `EC[0xB2..B3]=D4 0E` → 3796 RPM (GPU), matching the report's CPU max=3850 / GPU max=3796, with the idle step reading 0/0. `FanLevel 0x34/0x35` tracks the commanded step and `FanRate 0x2C-0x2F` mirrors the percent, i.e. the canonical 2022 layout (identical register set to the 8A18 / 8A42 entries). The mode/switch/manual/countdown registers are inferred from the 2022 template and mirror what v1.4.1 auto-detection already applied for this ProductId, so the entry is non-regressive.

### Deferred (need confirmed data before a code change is safe)

- **HP Omen 16 (8BCA) native database entry** (issues #76 reported by @simoliguori, #85 reported by @rajarawshed). Two reports against the same `ProductId` disagree completely on the tachometer layout: #76's wizard found CPU `0xD2` (PeriodEncoded8) + GPU `0xF0` (LE16), while #85's found CPU `0x06` (DirectMultiplier8) + GPU `0xD8` (LE16) and its live-RPM column read a flat ~58 RPM (a misdetection, not a real tach). As with the deferred 8BCD entry, HP recycles `ProductId` across SKUs with different fan topologies, so shipping either layout would put garbage RPM on the other user's machine. Both users' Auto-Calibration sidecars keep their installs working; resolution needs a sustained-load manual probe or a third corroborating report.
- **HP Victus 15-fa0xxx fan controls / garbage RPM** (issue #86, reported by @max472829, i5-12th-gen / RTX 3050). The "RPM reads as negatives or millions" symptom means the active preset's tachometer registers are wrong for this board, and the slider-extremes-do-nothing report needs a reproducible `-Diag` + ProductId to localise (no Auto-Calibration report was attached). Left open pending that data rather than guessing at registers for an unidentified board.
- **8A18 post-calibration fan lock / hibernation** (issue #84, reported by @xenon205). 8A18 is already in the native database (since v1.4.2) with verified `0xB0`/`0xB2` tachometers, and the wizard teardown's rate-register release (v1.4.3, #74 follow-up) plus the configurable hibernation guard (`BatteryGlitchGuardHoldAlways`, v1.4.2) already target the reported symptoms — so no new code lands here; the report is a confirmation request that those existing fixes carry into v1.4.4.

## [1.4.3-reborn] - 2026-05-27

> **Auto-Calibration clarity + crash patch.** A field-report sweep (#74 / #76 plus a direct e-mail report on 88D2) showed the Auto-Calibration Wizard was *working as designed* on several boards but *communicating badly* — a deliberately-skipped 100% step read as "stuck at 70%", and a benign top-step RPM match raised a scary "PLATEAU DETECTED — add this board to the freeze list" alarm on healthy hardware. This release fixes the messaging, relabels two misidentified info fields, makes the wizard teardown release the fan **rate** registers (not just the level registers), and hardens the CLI exit path against an "invalid handle" crash. No new features; no hardware-control behaviour changes beyond the teardown completion.

### Fixed

- **CLI crash `Exception: Nieprawidłowe dojście` / "invalid handle" after a console command** (issue #76, reported by @simoliguori). `Cli.RestorePrompt()` — the cosmetic "make the command prompt reappear" hack run on the first-instance CLI path (and from `App.Exit()`) — read `Console.CursorTop`, which calls `GetConsoleScreenBufferInfo` under the hood and throws a `WinIOError` when standard output is not a real console screen buffer (output redirected to a file/pipe, or the process launched without an inheritable console). That exception escaped to `Main()` and surfaced as the crash dialog in the screenshot. `RestorePrompt()` now wraps its console access in a best-effort `try/catch` (consistent with the codebase's graceful-degradation style) — when there is no usable console buffer there is simply nothing to restore, and the work the command already did is unaffected.
- **Auto-Calibration report read as "stuck at 70%" on max-fan-freeze boards** (issue #74, reported by @MartinSalg818, 8BAD). On a board in `FanArray.HasMaxFanFreeze`, the wizard intentionally filters the 100% step out of its profile (commanding 100% locks the EC until reboot on these models — the safety behaviour added in v1.4.1). But the report just showed `Profile: 0% → 30% → 70%` with no explanation, so a truncated sweep looked like a malfunction. The wizard now records that the omission was deliberate (`CalibrationOutcome.MaxStepSkippedForFreeze`) and the report carries a clear "**100 % step intentionally skipped** — `<ProductId>` is on the known freeze list, this is expected and not a malfunction" note above the Device block.
- **False "PLATEAU DETECTED at 100 %" alarm on healthy boards** (direct e-mail report, 88D2 HP Omen 15). The wizard's plateau detector fired on the *final* step too, where it could not prevent anything (the 100% command had already run) and where "top step ≈ previous step" is the normal case on boards whose manual fan-level scale already reaches the physical ceiling. The result was a prominent "the board is at its physical fan ceiling — please add `88D2` to `FanArray.HasMaxFanFreeze`" call-out on a board that is *not* freeze-prone (the owner confirmed the fans audibly spin faster at manual max). Plateau detection now only raises that alarm — and the safety-list recommendation — when a higher step was actually **skipped** (an actionable, freeze-preventing plateau). A no-gain reading on the last step is recorded as a neutral, informational note that explicitly does **not** recommend a safety-list addition, and points out the one case worth a closer look (a register mirroring the commanded level rather than a true tachometer).
- **Auto-Calibration report labelled the BIOS born-date as "BIOS Build Date"** (issue #74). The value comes from `GetBornDate()` — the factory manufacture/"born" date — not the firmware build or version. The mislabel led the reporter to flag `20240904` as wrong against their actual BIOS build (`20260421`). The report now reads **BIOS Born Date** with an inline "(factory manufacture date — not the firmware build/version)" clarification.
- **Auto-Calibration Wizard could leave the fans pinned at the last commanded step after running** (issue #74, reported by @MartinSalg818, 8BAD — follow-up to the v1.4.2 #65 fix). `ApplyLevel()` drives both the fan **level** registers and the per-fan **rate** registers on every sub-100% step, but the v1.4.2 teardown only released the level registers (writing `0xFF, 0xFF`). On boards whose EC keeps driving the fans from the rate register after manual mode is released, the stale rate write left the fans stuck at the last step ("locks at 70%"). The wizard now captures each fan's rate register before the sweep and restores it in the teardown `finally{}`, symmetric with the existing level restore. Best-effort: a genuinely frozen EC ignores the restore writes and still requires a reboot, but a board that merely had a sticky rate register now recovers cleanly.
- **GUI system-info line showed a bare CPU PL4 wattage that read as the power-adapter rating** (issue #74). The main window printed `GetDefaultCpuPowerLimit4()` (the CPU Power Limit 4, in watts) as an unlabelled "`200W`" immediately before the AC-adapter status, so the reporter read it as a "200 W power brick" and flagged it against their real 330 W HP adapter. The wattage is now prefixed with a **`CPU PL4`** label so it is no longer mistaken for the charger rating. (OmenMon does not read or display the AC-adapter's wattage — HP's smart-adapter BIOS call only returns a sufficient/insufficient status, not a rating.)
- **GPU power options ("Base Power / Extra Power / Extra Power with Boost") silently did nothing on the HP Victus 15-fb1xxx** (issue #79, reported by @NotDarkn, 8C30). The tray GPU-power menu issued the BIOS `0x22` write unconditionally; on entry-level SKUs whose GPU has no custom-TGP / PPAB support the firmware accepts the call but ignores it, so the menu just snapped back to Base Power with no feedback. `GuiMenu.EventActionGpuPower` now reads the live GPU power state back after writing it (the `SetGpuPower` path already waits `GpuPowerSetInterval` so the read reflects what the firmware stored) and, when the requested preset was not applied, shows a balloon explaining the GPU likely doesn't support custom TGP / PPAB — instead of leaving the selection looking broken. OmenMon cannot make unsupported hardware honour cTGP/PPAB, so this converts a silent no-op into a clear explanation rather than claiming to enable a missing capability.

## [1.4.2-reborn] - 2026-05-25

### Fixed

- **Random fan-profile switches mid-game caused by brief AC dropouts** (issue #70, reported by @MartinSalg818; #59 reported by @NotDarkn confirms the cluster). Some HP Omen / Victus laptops occasionally report `PowerLineStatus` as `Offline` for a few seconds even though the laptop is physically plugged in (confirmed in the user's `powercfg /batteryreport` against AC adapter telemetry). Before this fix, `SystemEvents.PowerModeChanged` → `GuiTray.EventPowerChange` → `GuiOp.PowerChange()` reacted immediately and — when `AutoConfig=true` and a fan program was active — switched from `FanProgramDefault` (Power) to `FanProgramDefaultAlt` (Silent → caps the system to Base Power), producing visible CPU/power-throttling stutters during gameplay until AC was reported back ~2 s later. The same handler also flipped the BIOS heartbeat enable/disable state. The fix is a five-layer defence:
  1. **AC-flicker debounce.** On a `StatusChange` event the actual reaction (fan-program switch + heartbeat toggle + main-form refresh) is deferred to the next `EventTimerTick` that arrives after `AcFlickerHoldMs` (default **10000 ms** — bumped from the first-pass 8 s after re-reading the field reports, several of which describe ~10 s flickers).
  2. **Multi-sample confirmation.** When the hold window elapses the deferred handler now reads `IsFullPowerConfirmed` `AcFlickerConfirmSamples` times (default **3**) with `AcFlickerConfirmIntervalMs` (default **250 ms**) between reads and only acts if every sample agrees. A dissenter re-queues the deferral; the cascade caps at `AcFlickerMaxDeferralMs` (default **60000 ms**) so a pathological flapper still reaches a decision in bounded time.
  3. **Multi-source AC check (`Settings.IsFullPowerConfirmed`).** Cross-references three independent signals — Windows `PowerLineStatus`, `BatteryChargeStatus.Charging`, and the HP firmware smart-adapter query (BIOS Cmd Legacy `0x0F`) — and treats AC as connected when any of them confirm it. The BIOS call is gated so it only runs when both Windows signals report battery, keeping the common-case cost identical to `IsFullPower`. Used by the new confirmation gate, the passive poll, the heartbeat-resume path, and `Op.PowerChange`.
  4. **Passive AC-state poll.** The GUI timer tick now checks `PowerLineStatus` and synthesises a deferred change when the live state diverges from `Op.FullPower` without a `PowerModeChanged` event already in flight, recovering from cases where Windows drops or coalesces the event during rapid-fire flickers. Controlled by `AcFlickerPassivePoll` (default `true`).
  5. **`PowerGuard` AC-offline release debounce.** The percent-glitch guard's "release wake-lock on AC offline" branch (which existed to ensure a genuine critical-battery state could still hibernate) used to fire the instant `PowerLineStatus` flipped to `Offline`, silently defeating the guard whenever an AC flicker coincided with a percent torn-read (#59-style symptom on the same SKUs). The release is now gated on `AcFlickerHoldMs` of sustained `Offline` *and* an `IsFullPowerConfirmed` cross-check. Inside that window the percent-glitch state machine keeps running normally so a coinciding torn percent read is still detected.

  Configurable in `OmenMon.xml`: `AcFlickerGuard` (boolean, default `true`), `AcFlickerHoldMs` (default `10000`, range `0..60000`), `AcFlickerConfirmSamples` (default `3`, range `1..20`), `AcFlickerConfirmIntervalMs` (default `250`, range `10..2000`), `AcFlickerMaxDeferralMs` (default `60000`, range `1..60000`), `AcFlickerPassivePoll` (boolean, default `true`). Set `AcFlickerGuard=false` or `AcFlickerHoldMs=0` to restore the immediate-switch behaviour from earlier builds. The guard is independent of the existing `BatteryGlitchGuard` (which targets a different symptom — a torn battery-percent read while still on AC) and the two run side by side; the `PowerGuard` change above is the bridge that makes them coexist correctly when both fire at once.
- **Auto-Calibration Wizard locked fans at ~3500 RPM after running** (issue #65, reported by @MartinSalg818). In the wizard teardown `finally` cleanup block, if the system was on automatic BIOS control before running calibration, OmenMon now writes `0xFF, 0xFF` to clear custom levels and release control back to the BIOS.
- **GPU fan curves reacted to CPU temperature when GPU is idle/off** (issue #66, reported by @Bart82). Gated the CPU temperature fallback in `FanProgram.Update()` using a new helper `Platform.HasObservedGpuTemperature()`, which returns true once GPTM has produced a single non-zero reading since startup. When a physical GPU is present but powered off (0 °C), the flag stays latched true from earlier samples and the GPU fan stays at idle instead of ramping up under CPU load; on boards that genuinely have no GPU temp sensor the flag never latches and the CPU-temp fallback runs as in v1.4.1. (Initial v1.4.2 PR proposed a `HasGpuTemperatureSensor()` helper that inspected the configured sensor list, but the default global `<Temperature>` config always contains GPTM regardless of hardware, so the check evaluated true everywhere — Copilot review #1 on the v1.4.2 PR; corrected before release.)
- **`PowerGuard.AssertGuard` could leave internal state ahead of OS state** (Copilot review #3 on the first v1.4.2 PR pass + Copilot review #1 on the second pass). Two bugs in the same code path: (1) when the underlying `SetThreadExecutionState` P/Invoke threw, the original `BatteryGlitchGuardHoldAlways` branch still wrote `guardUntil = DateTime.MaxValue`, so `IsGuardActive()` would have returned true permanently while the OS never received the wake-lock request; and (2) `SetThreadExecutionState` actually signals failure via its return value + `GetLastWin32Error()` rather than by throwing, so the first-pass fix that only caught exceptions still rubber-stamped failed calls as successful. `AssertGuard` now returns `bool` reflecting *real* success: it captures the `ExecutionState` return value, and when it is `None` (`0`) disambiguates a genuine failure from the legitimate "previous state was None on the first call after process start" case via `Marshal.GetLastWin32Error()`. The P/Invoke in `External/Kernel.cs` was already declared `SetLastError=true` for exactly this purpose. HoldAlways and transient-glitch latches both only advance on a confirmed success. Round-3 re-review hardened this further: (a) the last-error is now explicitly cleared via a new `Kernel32.SetLastError(0)` P/Invoke immediately before the call, because Windows does not guarantee clearing last-error on success and a stale value would otherwise misread the "previous state was None" path as a failure; (b) `BatteryGlitchGuardHoldAlways` is now evaluated **before** the `BatteryGlitchGuard` master-switch early-return, so it works as an independent "block sleep/hibernate while OmenMon runs" toggle even when the percent-glitch guard is disabled; and (c) `IsGuardActive()` now keys off `guardUntil` rather than the config flags, so a failed `SetThreadExecutionState` is never reported as an active guard.
- **Auto-Calibration Wizard failed to detect CPU fan on low idle speeds on 8BB3** (issue #64, reported by @jpcaldwell30). Lowered `DirectMultByteMin` threshold in `EcDiffScanner.cs` from 10 to 2 to support low fan idle speeds (e.g. 300 RPM / 0x03). Also fixed the 2023+ Layout B heuristic check in `AutoDetector.cs` to allow `cput == 0x0F` (observed on AMD-based 8BB3).

### Added

- **HP Victus 16 (8BB3, 2024, AMD) in the native model database** (`OmenMon.xml`). Stamped CPU fan speed register at `0xF1` (DirectMultiplier8) and mapped it natively in `AutoCal.KnownBoards`.
- **OMEN by HP Gaming Laptop 16z-n000 (8A42, 2022) in the native model database** (`OmenMon.xml`, issue #68 reported by @GGoose). Confirmed via the user's `-Diag` report: AutoDetector matched the canonical 2022 layout — CPUT plausible at `0x57`, 16-bit LE tach at `0xB0`/`0xB2` — and BIOS reports `GetFanCount=2`, `GetGpuMode=Optimus`, `Fan1=Cpu / Fan2=Gpu`. GPTM at `0xB7` flips between `0x00` and ~`0x27` (39 °C) as the discrete GPU parks under Optimus, so the new `HasObservedGpuTemperature` sticky-flag from this release correctly latches once the dGPU produces a single non-zero sample. The remaining 2022-template registers (`FanLevel 0x34/0x35`, `FanRateWrite 0x2C/0x2D`, `Mode 0x95`, `Switch 0xF4`, etc.) are inferred from the AutoDetector template and mirror the values v1.4.1's auto-detection has been applying for this ProductId, so the entry is non-regressive — it just trades "Auto-detected" for a named native preset and removes the per-startup detection latency. XML comment documents the caveat that sibling 8A4C/8A4D use the newer `0x3A/0x3B` rate-write pair in case a real 8A42 owner later reports custom-rate writes have no effect.
- **HP OMEN 17 ck1000nw (8A18, 2022) in the native model database** (`OmenMon.xml`, issue #72 reported by @xenon205). Confirmed via the user's Auto-Calibration report: the raw EC dumps at 0/30/70/100 % decode exactly to the reported live RPM (CPU `0/1476/3218/3422`, GPU `0/1448/3115/3363`) for a 16-bit LE tach at `0xB0`/`0xB2`, and `FanLevel 0x34/0x35` + `FanRate 0x2C-0x2F` track the fan setting across all four steps — i.e. the canonical 2022 layout (identical register set to the 8A42 entry above). BIOS reports `GetFanCount=2`. The mode/switch/manual/countdown registers are inferred from the 2022 template and mirror what v1.4.1 auto-detection already applied for this ProductId, so the entry is non-regressive. As with 8A42, the XML comment notes the `0x2C/0x2D` → `0x3A/0x3B` rate-write caveat for the 8A4C/8A4D family in case a real owner reports custom-rate writes have no effect.
- **HP Victus 16 (88F4, 2022) in the native model database** (`OmenMon.xml`) and safety list (`FanArray.HasMaxFanFreeze`). Added to safety list due to physical fan ceiling rate-limiter, with CPU/GPU speed registers overridden to 0x2E/0xB0.
- **Configurable hibernation guard settings** (`Library/PowerGuard.cs`). Added three new XML configuration parameters to customize the battery-glitch guard or completely prevent Windows sleep/hibernation:
  - `BatteryGlitchGuardOnBattery` (boolean, default `false`): Enables the glitch guard to run even on battery power.
  - `BatteryGlitchGuardDisableTimeout` (boolean, default `false`): Disables the 60-second safety timeout for sustained glitches, preventing the wake-lock from releasing during long loads.
  - `BatteryGlitchGuardHoldAlways` (boolean, default `false`): Permanently asserts `ES_SYSTEM_REQUIRED` (wake-lock) while OmenMon is running to block sleep/hibernation completely.
- **AC-flicker debounce settings** (`App/Gui/GuiTray.cs`, `Library/PowerGuard.cs`, `Hardware/Settings.cs`, issues #70 / #59). Six XML configuration parameters governing the five-layer flicker fix described in the Fixed section above:
  - `AcFlickerGuard` (boolean, default `true`): Master switch; set `false` to react immediately to every `PowerModeChanged StatusChange` event (the pre-v1.4.2 behaviour).
  - `AcFlickerHoldMs` (integer ms, default `10000`, range `0..60000`): How long the new line-status must remain stable before OmenMon applies the corresponding fan-program / heartbeat change. `0` is equivalent to `AcFlickerGuard=false`. Also gates the `PowerGuard` AC-offline release.
  - `AcFlickerConfirmSamples` (integer, default `3`, range `1..20`): Number of consecutive samples the deferred handler reads at the end of the hold window — every sample must agree before the change applies. `1` disables multi-sample confirmation (single read, the first-pass v1.4.2 behaviour).
  - `AcFlickerConfirmIntervalMs` (integer ms, default `250`, range `10..2000`): Gap between confirmation samples.
  - `AcFlickerMaxDeferralMs` (integer ms, default `60000`, range `1..60000`): Safety ceiling on cascaded re-deferrals when multi-sample confirmation keeps disagreeing. Once exceeded the change applies on the next read regardless of confirmation.
  - `AcFlickerPassivePoll` (boolean, default `true`): Synthesises a deferred change from the GUI timer tick when `PowerLineStatus` diverges from `Op.FullPower` without a `PowerModeChanged` event — recovers from cases where Windows drops or coalesces the event during rapid-fire flickers.
- **`Settings.IsFullPowerConfirmed`** (`Hardware/Settings.cs`). New multi-source AC-presence check used by the AC-flicker debounce, the passive poll, the heartbeat-resume path, `Op.PowerChange`, the `AutoConfig` startup path, and the `PowerGuard` AC-offline release. Cross-references Windows `PowerLineStatus`, `BatteryChargeStatus.Charging`, and the HP firmware smart-adapter query (BIOS Cmd Legacy `0x0F`, returning `MeetsRequirement` / `BelowRequirement` / `BatteryPower` / ...) and treats AC as connected if any source confirms it. The BIOS call is gated to fire only when both Windows signals report battery, so the common-case cost is identical to the existing `IsFullPower`.

## [1.4.1-reborn] - 2026-05-18

> **First-week-after-v1.4.0 field-report sweep.** Patch release addressing the cluster of issues (#32 / #50 / #56 / #57 / #58) that arrived once the Auto-Calibration Wizard reached users on entry-level Victus 15/16 SKUs whose physical fan ceiling sits below the BIOS rate-limiter's threshold. Two layers of defence: an explicit board safety list (so wizard + GUI both skip the dangerous 100% command on confirmed-vulnerable models) and a generic plateau detector in the wizard (so unknown future SKUs with the same firmware signature abort gracefully and surface a "please report your ProductId" note in the Markdown report).

### Fixed

- **Defensive 100 %-fan safety entry for HP Omen 16-wf1012nl (8C77)** (issue #50, reported by @stf1o). Adding 8C77 to the `FanArray.HasMaxFanFreeze` list. The wizard's plateau detector already protects unknown boards, but stf1o's raw EC dumps show the unmistakable rate-limiter footprint at `EC[0xD2]` (PeriodEncoded8): the period byte goes `0xB2 → 0xD0 → 0x99 → 0xEC` across the 0/30/70/100 % sweep — the 100 % reading is *slower than idle*, matching the same firmware signature as 8C30 / 8D07 / 8BAD / 8E35. Wizard now filters 100 % out of the profile and the tray "Max Fan" toggle requires explicit confirmation on this board. **Not** added to `<Models>` in `OmenMon.xml`: the existing `OmenMon-AutoCal.xml` sidecar already drives the RPM read at `EC[0xD2]` correctly using PeriodEncoded8 decoding, but the shipped `<Model>` schema only supports 16-bit Little-Endian RPM tachometers — adding a native entry would silently override the sidecar with a junk LE16 read (`EC[0xD2..0xD3]` ≈ 64 000 "RPM"). Sidecar-supported is the right answer here until the model schema grows a mode field.
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