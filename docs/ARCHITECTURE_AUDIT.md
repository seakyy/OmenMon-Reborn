# OmenMon-Reborn — Architecture Audit & v1.5 Blueprint

*Audited 2026-06-12 against `release/v1.4.6` (33206ae): 74 C# files, 22,368 lines,
plus the full issue tracker (70 Reborn issues, 121 upstream OmenMon issues).*

This document answers two questions the issue tracker keeps raising:

1. **Why does this project generate so many issues?**
2. **What would a structurally better version look like — and what must it not lose?**

---

## 1. Where the issues actually come from

Classifying all 70 Reborn issues by root cause (issues can match more than one):

| Class | Count | Share | Examples | Verdict |
|---|---|---|---|---|
| Model coverage reports ("add my board") | ~40 | ~57 % | #2 #5 #8 #9 #13 #22 #32 #37 #51 #57 #61 #67 #68 #72 #76 #77 #82 #85 #87 #90 #92 #95 #99 #100 | **Not a defect.** This is the support pipeline working as designed (#14 solicits these). No rewrite removes them; only cheaper onboarding does. |
| Fan behavior wrong on a specific board (RPM caps, 50k readings, curve follows wrong sensor, calibration misfires) | ~15 | ~21 % | #26 #28 #33 #34 #39 #40 #41 #52 #58 #62 #65 #66 #74 #83 #97 | **Structural.** Per-board *behavior* variance is encoded in C#, so every new quirk costs a code change + release (§2.1). |
| Windows power-policy interactions | 4 | ~6 % | #59 (torn battery read → hibernate), #70 (AC flicker), #88 (dup of #59), #103 (Modern Standby display-off) | **Environmental.** Each fixed by a point-defense guard; the guards now interact (§2.4). |
| "Feature does nothing on my laptop" | ~4 | ~6 % | #79 (GPU power no-op), #86, #23 | **Structural.** Silent degradation hides capability mismatches (§2.2). |
| Threading / UI responsiveness | 1 | | #98 | Fixed in v1.4.6 by `GuiMonitor` (§3). |
| Regressions from prior fixes | 2 | | #101 (caused by the #76 fix), #56 | Cost of point-fixing under time pressure without a regression-test net. |
| Defender false positives | 1 | | #93 | Heuristic FP on the exe; the *driver* cluster died with PawnIO (§3). |

Context that matters: **55 of 70 Reborn issues are closed.** Upstream OmenMon — same
codebase ancestry, unmaintained — sits at 95 open / 26 closed. The fork's fix
velocity is fine. The problem is the **cost per issue**: most fixes require C#
changes, hardware-specific review, and a full release, because of the structural
points below.

## 2. What is structurally wrong (with evidence)

### 2.1 Per-model knowledge is split across data *and* nine C# files

The design intent is "new hardware = XML entry" (`OmenMon.xml` `<Models>`). In
practice the XML schema can only express *register addresses*, not *behavior*, so
behavioral variance leaks into code:

| Location | What it encodes | Boards |
|---|---|---|
| `OmenMon.xml` `<Models>` | Register addresses (the intended path) | 24 models |
| `Library/AutoCal.cs` `KnownBoards` (~line 350) | RPM register maps + encodings the heuristic can't discover | 10 boards (8D87, 8BD4, 8DD0, 8C9C, 8BB3, 8BA9, 8D41, 8A3E, …) |
| `Hardware/FanArray.cs:249` `HasMaxFanFreeze` | 100 %-fan EC-freeze blacklist | 6 boards (8C30, 8D07, 8BAD, 8E35, 8C77, 88F4) |
| `Hardware/EcDiffScanner.cs:19-27` | RPM *encoding* taxonomy (LE16, byte×100 "DirectMultiplier8", period-encoded, BIOS-level mirror) | taxonomy, 4 encodings |
| `Hardware/Platform.cs:243-246`, `App/Gui/GuiMonitor.cs:285` | CPU-temp source policy (CPUT stuck / 0xFF boards) | 8D87, 8C9C, 8BBE |
| `Hardware/PlatformPreset.cs:42-87` | Manual-mode switch value pairs, default layouts | 8BBE et al. |
| `App/Gui/GuiMenu.cs:360, :514` | Board-conditional UI workarounds | 8BCD (#77), 8C30 (#79) |
| `App/Cli/CliOpCalibration.cs:48-251` | Freeze-aware calibration paths | 8C30 family |

The smoking gun is written in the code itself — `FanArray.cs:239` on 8C77:
*"RPM stays sidecar-supported only because the `<Models>` schema is LE16-only and
cannot decode PeriodEncoded8."* The schema ran out of expressiveness, and every
quirk since has become a C# release.

**Consequence:** a new quirky board (≈1 every 1–2 weeks judging by the tracker)
costs maintainer code, review, and a release. With behavior expressible in XML, it
would cost an XML entry the *reporter themselves* can test, because a side-by-side
`OmenMon.xml` already overrides the shipped one.

### 2.2 Failure philosophy: swallow-and-degrade — correct for boot, wrong for features

153 `catch`-swallow sites across 35 files. For Driver/Config init this is
deliberate and right (the app must start on unknown hardware). But the same
philosophy applied to *features* means an unsupported operation does nothing
silently: #79 ("Base Power… Does Nothing"), #23, #86 are all users discovering a
capability mismatch by clicking a control that can't work on their board. Nothing
in the UI says so, so it gets filed as a bug and triaged by hand.

### 2.3 Calibration discovers hardware by writing to it

`AutoCal` + the wizard sweep fan registers experimentally. Misfire history: #26
(8DD0 locking onto wrong offsets 38 bytes apart), #40, #65/#74 (70 % cap), and the
8C30-class freeze itself is *triggered by* the wizard's 100 % step. The mitigations
are now layered and good — plateau detector (`CliOpCalibration.cs:48`), 16-byte
sidecar distance guard + `KnownBoards` override (`AutoCal.cs:119-160`),
`HasMaxFanFreeze` defence-in-depth — but the architecture is still write-first,
observe-second on unknown firmware.

### 2.4 Windows power policy is fought with four independent state machines

`PowerGuard` (371 lines, #59), AC-flicker debounce (#70), BIOS-heartbeat pause on
battery, and `DisplayOffKeepAwake` (#103) are each born from one incident, each
correct, and already cross-coupled: the AC-flicker hold window deliberately
overrides the glitch guard's AC-only invariant (`PowerGuard.cs:141-202`). Every new
Windows power behavior risks an interaction bug among them because there is no
single owner of "what power state are we in and what are we suppressing."

### 2.5 Config plumbing is 3-place mechanical duplication

87 static fields in `ConfigData.cs` (616 lines), each needing a matching `GetBool/
GetWord` in `Config.Load` and `SetBool/SetUInt` in `Config.Save` (966 lines), plus
XML documentation. Forgetting one of the three is silent (today's
`DisplayOffKeepAwake` required edits in exactly those three places plus
`OmenMon.xml`). This is drift waiting to happen.

### 2.6 GUI monoliths

`GuiMenu.cs` 1,251 lines, `GuiFormMain.cs` 1,073, `GuiFormMainInit.cs` 777,
`GuiTray.cs` 716. Threading was retrofitted (v1.4.5's correctness sweep caused
#98's UI stalls; v1.4.6's `GuiMonitor` fixed it properly). The monoliths make every
UI change a high-blast-radius edit.

### 2.7 The constant you cannot refactor away

This app drives **undocumented, reverse-engineered EC/BIOS firmware across ~60
SKUs spanning 2018–2026, with no spec, as Administrator, where a wrong byte can
require a reboot** (8C30). A meaningful share of the tracker is the unavoidable tax
of that mission. The goal is not zero issues; it is making each issue cheap.

## 3. What the fork already fixed wholesale — do not regress this

| Upstream pathology (still open there) | Reborn mechanism | Killed |
|---|---|---|
| WinRing0 CVE-2020-14979: Defender flags, BSODs, "won't start" (upstream #115 #110 #107 #112 #28) | PawnIO migration (v1.4.0), MS-signed module | whole class |
| Forced hibernate / battery <5 % (upstream #58 #79) | `PowerGuard` glitch detector | #59 #88 |
| 2023+ controls need OGH running (upstream #37, 37 comments) | BIOS heartbeat | class |
| Fans stuck / erratic after Modern Standby (upstream #32) | heartbeat + monitor resume paths; #103 keep-awake | mostly |
| Un-triageable reports | `-Diag` / `-Probe` Markdown, EcTrace ring buffer, crash logs | triage cost ↓ |
| UI stalls behind EC mutex | `GuiMonitor` snapshot actor (#98) | class |

## 4. Verdict on "rewrite it from scratch"

**A from-scratch rewrite would make the issue count worse, not better.** The
codebase's awkward parts are load-bearing: ~2 years of incident-derived guards
whose *reasons* live in comments and CHANGELOG, not in any spec. A rewrite that
doesn't carry every row of the ledger below re-opens the corresponding issues:

| Guard | Protects against | Lives at |
|---|---|---|
| `HasMaxFanFreeze` + plateau detector | EC lock-until-reboot at 100 % fan (#32 #56 #57 #58 #50) | `FanArray.cs:249`, `CliOpCalibration.cs:48` |
| Sidecar 16-byte distance + `KnownBoards` | AutoCal locking onto garbage registers (#26 #71) | `AutoCal.cs:119-160, 350+` |
| CPU temp = max(CPUT, WMI BIOS) | stuck/overlapping CPUT pinning fan curve (#97, 8C9C 0xFF) | `Platform.cs:243` |
| `PowerGuard` + last-error dance | torn battery read → forced hibernate (#59 #88) | `PowerGuard.cs` |
| AC-flicker debounce + multi-source confirm | phantom unplug swapping fan programs mid-game (#70) | `PowerGuard.cs:141`, GuiOp |
| BIOS heartbeat (+ pause on battery) | 2023+ models reverting to firmware control | Hardware/BiosCtl, config |
| `EcLockQuiet` paths + timeout counter | modal error storms under OGH mutex contention (#94 #96) | `Library/Hw.cs` |
| Std-handle check in `Cli.Initialize` | CLI silent (#101) vs `-Diag > file` crash (#76) — both directions | `App/Cli/Cli.cs` |
| GPU-temp absent → don't follow CPU (#66), GPU fan sensor split (#62) | wrong-sensor fan curves | Platform/FanProgram |
| TNT2–TNT5 disabled by default | phantom 84/97 °C ramping fans (upstream #97 #93) | `OmenMon.xml` |
| `DisplayOffKeepAwake` holder thread | Modern Standby sleeping on display-off (#103) | `Library/Os.cs:99` |

Each rewrite-ported behavior must port its guard **with a test naming the issue
number**, or the rewrite is a regression machine.

## 5. The better version: target architecture for v1.5

The redesign (WinUI shell, G-Helper-style) should sit on a restructured core. The
structure below removes the issue *generators* identified in §2 while keeping every
ledger row. All of it is UI-framework-agnostic — build it under WinForms, let the
WinUI shell consume it.

### 5.1 DeviceProfile: all model variance becomes data

One declarative record per board, sourced from an extended `OmenMon.xml` schema;
`PlatformPreset.Default/Default2023` remain as the two built-in fallbacks;
`KnownBoards` and `HasMaxFanFreeze` migrate into shipped XML:

```xml
<Model ProductId="8C77" DisplayName="HP Omen 16-wf1xxx">
    <!-- existing register elements stay as-is -->
    <RpmEncoding>PeriodEncoded8</RpmEncoding>   <!-- LE16 | DirectMultiplier8 | PeriodEncoded8 | BiosLevelMirror -->
    <MaxFanLevelSafe>90</MaxFanLevelSafe>        <!-- replaces HasMaxFanFreeze blacklist -->
    <Capabilities GpuPower="false" PerfModes="true" KbdBacklight="zones4" />
    <TempPolicy>MaxOfCputAndWmi</TempPolicy>
</Model>
```

Wins: a new quirky board becomes an XML pull request; reporters self-test via the
side-by-side XML override that already exists; `ModelDatabaseTests` extends
naturally to validate the new fields. **This is the single highest-leverage change
in the project** — it converts the dominant issue class from "maintainer codes a
fix" to "reporter contributes data."

### 5.2 HardwareActor: finish what GuiMonitor started

v1.4.6 proved the pattern (snapshot publication, UI never blocks on EC). Finish it:
*all* hardware traffic — including user actions, fan-program ticks, calibration —
flows through one prioritized command queue on the monitor thread (user action >
program tick > sampling). `GuiOp.HardwareLock` disappears; so does the residual
class of lock-ordering bugs.

### 5.3 Capability-gated UI: degrade visibly

Controls bind to `DeviceProfile.Capabilities`. Unsupported ⇒ disabled with a
tooltip ("Not supported on 8C30 — see wiki"). Converts the #79/#86 class of
reports into self-answering UI. Cheap to do even in the current WinForms GUI.

### 5.4 Calibration v2: read first, write with consent

Phase A (read-only): drive fans through the *BIOS* path at safe levels, observe EC
deltas, match against the four known RPM encodings (`EcDiffScanner` already
implements the taxonomy). Phase B (write probes) only where Phase A is ambiguous
*and* the profile doesn't forbid it, one register at a time, restore-in-`finally`.
Targets the residual AutoCal-misfire class.

### 5.5 PowerPolicy: one state machine

Merge PowerGuard, AC-flicker, heartbeat-pause, display-off keep-awake into a
single component with explicit states (OnAc, OnBattery, AcFlickerHold,
GlitchHold, DisplayOffAwake, …). Each existing guard becomes a transition rule
with its issue number attached. New Windows behaviors get added as rules, not new
parallel state machines.

### 5.6 Config registry

One declarative table (name, type, default, bounds, doc string) drives load, save,
`-Diag` dump, and XML doc generation. 87 fields × 3 hand-written places → 87 rows
in one place. Add a reflection test: every `ConfigData` field must appear in the
registry.

### 5.7 Performance modes (#102) — confirmed feasible on the existing surface

The BIOS surface already wraps most of what OGH's ECO/Quiet/Default do:
`BiosData.FanMode` carries the full HP thermal-policy set (`Quiet=0x03`,
`Default=0x30`, `Performance=0x31`, `Cool=0x50`, L-levels — sourced from
`HP.Omen.Core.Common.PowerControl.PerformanceMode`), GPU power package is wrapped
(`Hw.BiosSetStruct(GpuPowerData)`), refresh-rate switching exists
(`Os.SetRefreshRate`). Missing: the CPU PL1/PL2 write path — verify command IDs
against OGH's shipped `HP.Omen.Core` assemblies per generation, then gate the whole
feature behind `Capabilities PerfModes` per §5.1. Ship as: mode = {thermal policy +
GPU TGP preset + optional 60 Hz + fan program}.

## 6. Sequencing (do not big-bang this)

**v1.4.7 — internal, low risk, no hardware-behavior change:**
1. Config registry (§5.6) + reflection test.
2. Capability flags parsed from XML, defaulting to exactly today's behavior;
   UI greys out + tooltips (§5.3).
3. GitHub issue forms requiring ProductId + `-Diag` (shipped with this audit).
4. Guard-ledger tests: for each §4 row that is testable without hardware, a unit
   test naming the issue number.

**v1.5.0 — structural, with the UI rework:**
5. DeviceProfile schema + migrate `KnownBoards`/`HasMaxFanFreeze` to shipped XML
   (§5.1). Keep the C# lists for one release as a cross-check that XML and code
   agree, then delete.
6. HardwareActor command queue (§5.2).
7. Calibration v2 (§5.4).
8. PowerPolicy state machine (§5.5).
9. Performance modes (§5.7) + WinUI shell.

Rules for every step: port the guard with its test; prefer read-only probes; a red
`ModelDatabaseTests` blocks release; hardware-touching changes get field-verified
by the original issue reporters before the issue is closed (the Awaiting
Confirmation label already implements this).

## 7. Disposition of the 15 currently-open issues

| # | Class | Action |
|---|---|---|
| #103 | Modern Standby display-off | Fixed in 33206ae (v1.4.6); ask @Bart82 to confirm |
| #102 | Perf modes feature | v1.5 per §5.7; request `-Probe` now for command verification |
| #101 | CLI silent | Fixed in v1.4.6; close on release |
| #100 | 898C report | Title/auto-report ProductId conflict (898C vs 8C9C) — do **not** touch shipped 8C9C mapping without `-Diag` confirmation; reporter's sidecar covers them |
| #99 | 88ED | Shipped in v1.4.6; close on release |
| #98 | UI stalls | Fixed in v1.4.6 (`GuiMonitor`); close on release |
| #97 | 8D87 temp stuck | Max-policy shipped in v1.4.6; proper `TempCpuReg` needs reporter's under-load probe |
| #96 | 8A3E | Read-only mapping shipped in v1.4.6; close on release |
| #95 | 8787 report | Wiki row done; needs calibration data → XML entry |
| #94 | EC lock errors | Quiet paths shipped in v1.4.6; close on release |
| #85 #77 #51 | Model reports | Awaiting reporter data (Pending label) — issue forms will prevent this stall pattern for future reports |
| #14 | Probe how-to | Keep pinned; link from new issue forms |

---

*Method note: line counts via `git ls-files`, special-case inventory via grep over
ProductIds, issue classification by hand from the tracker. Numbers will drift;
re-run the greps before citing this document in 2027.*
