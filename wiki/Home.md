# OmenMon-Reborn — Developer Wiki

**OmenMon-Reborn** is a fork of [OmenMon](https://github.com/OmenMon/OmenMon) by Piotr Szczepański.  
Fork maintained by [@seakyy](https://github.com/seakyy). Current release: **1.0.0-reborn** (2026-04-30).

The primary goal of this fork is to replace the hardcoded 2023 EC register layout with a dynamic, XML-driven model database, and to make unknown devices self-configuring through a safe read-only heuristic scan.

---

## Pages

| Page | What it covers |
|------|----------------|
| [Architecture](Architecture) | High-level overview of the codebase and how the four phases connect |
| [Model Database](Model-Database) | XML schema, `PlatformPreset` fields, how to add a new device |
| [Auto-Detection](Auto-Detection) | How the heuristic scanner works, startup flow, screenshots |
| [Contributing Hardware Data](Contributing-Hardware-Data) | The tray menu "Contribute" feature, clipboard flow, screenshots |

---

## Quick orientation

```
Hardware/
  PlatformPreset.cs     — data class: one instance per device model
  AutoDetector.cs       — read-only EC heuristic scan
  Platform.cs           — InitFans() uses Config.Models lookup

Library/
  Config.cs             — Load() / Save() / SaveModel()
  ConfigData.cs         — Config.Models dictionary + XML constants

App/Cli/
  CliOpProbe.cs         — -Probe verb + ProbeGetMarkdown() used by GUI

App/Gui/
  GuiFormMain.cs        — CheckUnknownModel() on first Shown event
  GuiMenu.cs            — "Contribute Hardware Data..." tray item
```

The `OmenMon.xml` file next to the EXE drives everything at runtime. If a device's `ProductId` is not in `<Models>`, `PlatformPreset.Default` is used as the fallback — the same confirmed layout as a 2022/2023 Omen 16 (i9-12900H / RTX 3070 Ti).
