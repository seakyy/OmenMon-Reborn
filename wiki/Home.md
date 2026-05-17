# OmenMon-Reborn — Wiki

**OmenMon-Reborn** is a fork of [OmenMon](https://github.com/OmenMon/OmenMon) by Piotr Szczepański.  
Fork maintained by [@seakyy](https://github.com/seakyy). Current release: **v1.4.0-reborn** (2026-05-17).

The primary goal of this fork is to replace the hardcoded 2023 EC register layout with a dynamic, XML-driven model database, and to make unknown devices self-configuring through a safe read-only heuristic scan.

---

## User Documentation

**New to OmenMon? Start here:** [INSTRUCTION.md](../INSTRUCTION.md) — covers fan modes, fan programs, battery care, RGB, CLI, and known issues.

---

## Developer Pages

| Page | What it covers |
|------|----------------|
| [Architecture](Architecture) | High-level overview of the codebase and how the four phases connect |
| [Model Database](Model-Database) | XML schema, `PlatformPreset` fields, how to add a new device |
| [Auto-Detection](Auto-Detection) | How the heuristic scanner works, startup flow, screenshots |
| [Contributing Hardware Data](Contributing-Hardware-Data) | The Auto-Calibration Wizard: stress-sweep flow, register heuristics, sidecar persistence, upstream report |

---

## Quick orientation

```
Hardware/
  PlatformPreset.cs     — data class: one instance per device model
  AutoDetector.cs       — read-only EC heuristic scan (startup auto-detect)
  EcDiffScanner.cs      — diff-scan heuristic used by the calibration wizard
  Platform.cs           — InitFans() uses Config.Models + AutoCal.Load()/Prime()
  Fan.cs                — GetSpeed() prefers AutoCal override over preset register

Library/
  Config.cs             — Load() / Save() / SaveModel()
  ConfigData.cs         — Config.Models dictionary + XML constants
  AutoCal.cs            — live RPM-register override + sidecar XML + known-board map

App/Cli/
  CliOpProbe.cs         — -Probe verb + ProbeGetMarkdown() (static dump)
  CliOpCalibration.cs   — Auto-Calibration Wizard orchestrator + Markdown report

App/Gui/
  GuiFormMain.cs        — CheckUnknownModel() on first Shown event
  GuiMenu.cs            — "Auto-Calibrate & Diagnose..." tray item
  GuiFormCalibration.cs — modal wizard dialog (progress, cancel, FormClosing guard)
```

The `OmenMon.xml` file next to the EXE drives everything at runtime. If a device's `ProductId` is not in `<Models>`, `PlatformPreset.Default` is used as the fallback — the same confirmed layout as a 2022/2023 Omen 16 (i9-12900H / RTX 3070 Ti).
