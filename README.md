# OmenMon-Reborn

<p align="center">
  <a href="https://github.com/seakyy/OmenMon-Reborn/releases/latest"><img src="https://img.shields.io/github/v/release/seakyy/OmenMon-Reborn?color=brightgreen&label=Latest%20Release" alt="Latest release"></a>
  <a href="https://github.com/seakyy/OmenMon-Reborn/releases"><img src="https://img.shields.io/github/downloads/seakyy/OmenMon-Reborn/total?color=blue&label=Downloads" alt="Downloads"></a>
  <a href="https://github.com/seakyy/OmenMon-Reborn/stargazers"><img src="https://img.shields.io/github/stars/seakyy/OmenMon-Reborn?style=social" alt="Stars"></a>
  <a href="SECURITY.md#windows-defender-false-positives"><img src="https://img.shields.io/badge/Microsoft%20Defender-Cleared-brightgreen?logo=windows" alt="Microsoft Defender cleared"></a>
  <a href="https://www.virustotal.com/gui/file/21384a329e082b66bf12255c057e84fc8608a44785231ee58a9f0316bcdcf0d1"><img src="https://img.shields.io/badge/VirusTotal-0%20%2F%2071-brightgreen?logo=virustotal" alt="VirusTotal 0/71"></a>
</p>

<p align="center">
  <a href="https://buymeacoffee.com/seakyy"><img src="https://img.shields.io/badge/Buy%20me%20a%20coffee-seakyy-FFDD00?style=for-the-badge&logo=buy-me-a-coffee&logoColor=black" alt="Buy Me a Coffee"></a>
</p>

> **Welcome to OmenMon-Reborn** — an active fork by **[seakyy](https://github.com/seakyy)**, last updated May 2026.  
> The original OmenMon hardcoded the EC hardware layout for 2023 devices, causing severe issues (fans stuck at 100%, wrong temperatures) on newer and older HP Omen and Victus laptops.  
>
> **OmenMon-Reborn** fixes this with a dynamic, XML-driven model database and a safe **Heuristic Auto-Detector**. If your laptop is unknown, it scans the hardware read-only and configures itself — no coding required.

## What's new in Reborn?

| Feature | Description |
|---------|-------------|
| **Self-Healing** | Unknown HP laptop? The Smart Fallback reads your EC registers safely and writes a working profile to `OmenMon.xml`. |
| **Fan Bug Fixed** | Ghost sensors TNT2–TNT5 that forced fans to 100% are now disabled by default for fan curves (still visible in the GUI). |
| **Auto-Calibration Wizard** | Click **"Auto-Calibrate & Diagnose..."** in the tray menu. OmenMon runs a guided 4-step fan stress sweep, scans the EC for fan-tachometer registers (16-bit LE, period-encoded, or direct-multiplier), applies the discovered layout to the live session, and copies a Markdown report to the clipboard for upstream contribution. The override survives a restart via a sidecar XML. |
| **`-Probe` CLI verb** | `OmenMon.exe -Probe` generates a full WMI + BIOS + EC snapshot as Markdown — ideal for bug reports. |
| **Fan Curve Editor** | A tray menu item (**Fan → Fan Curve Editor...**) for editing multi-point fan curves on a graph, instead of writing `<FanProgram>` entries into `OmenMon.xml` by hand. |

See [CHANGELOG.md](CHANGELOG.md) for the full release notes, [ARCHITECTURE.md](ARCHITECTURE.md) for design rationale, and [MODIFICATIONS.md](MODIFICATIONS.md) for GPLv3 compliance information.

## Fan Curve Editor

OmenMon applies one fan level (0–55) or one thermal mode at a time. If you want the fan to step up gradually as the laptop heats up, you have to write a `<FanProgram>` entry into `OmenMon.xml` yourself. This is a small dialog for doing that with a mouse instead.

Open it from the tray menu: **Fan → Fan Curve Editor...**

- Drag a point on the graph to change the temperature where the fan steps up. Click on empty space to add a point, press `Delete` to remove one, and use the arrow keys to nudge the selected point by 1 °C or 1 level.
- Each point has its own CPU and GPU level, since the two fans are driven separately.
- The line is drawn as steps, not a slope, because that is how the embedded controller behaves: the fan holds one level until the next threshold is reached. A dashed line shows the current CPU and GPU temperatures.
- Ticking **Run preview** applies the curve to the hardware while you edit. Unticking it, or closing the dialog, puts the previous program back.
- Curves are saved as normal `<FanProgram>` entries, so they also show up in the tray menu and are exported with the rest of your configuration. A profile that is still set as the default, or the last one left, cannot be deleted.
- On boards where setting the maximum fan level can lock the EC up (listed in `FanArray.HasMaxFanFreeze`), the editor stops one level short and shows a warning.

For anyone reading the code:

| File | What it does |
|------|--------------|
| `Hardware/FanCurve.cs` | The curve maths: lookup, clamping, and converting to and from the level list. Behaves the same as the existing `FanProgram.GetTemperatureLevel`. |
| `App/Gui/GuiFanCurveCanvas.cs` | Draws the graph and handles mouse and keyboard editing. |
| `App/Gui/GuiFormFanCurve.cs` | The dialog itself: profiles, preview, live read-out. |
| `Tests/OmenMon.Tests/FanCurveTests.cs` | Tests for the curve maths, mostly checking that the lookup matches the old code. |

The dialog only takes `HardwareLock` around the individual start and stop calls, so monitoring keeps running while it is open.

## Differences from upstream OmenMon

Most of these exist because of issues reported for specific laptop models. Reborn is not a rewrite; it is the same program with the hardware tables moved out into XML and a few tools added around them.

| Area | Upstream OmenMon | OmenMon-Reborn |
|------|------------------|----------------|
| **Hardware layout** | EC register layout written for the models the authors had. | The layout comes from `OmenMon.xml`, so boards can be added without touching the code. Unknown boards are detected read-only and given a starting profile. |
| **Fan control** | Fan levels and thermal modes. | The same, plus the fan curve editor described above. |
| **Ghost sensors** | TNT2–TNT5 could drive the fans to 100 %. | Left out of fan curves by default; still shown in the GUI. |
| **Diagnostics** | Register inspection by hand. | An auto-calibration wizard that walks through a fan sweep and tries to find the tachometer registers, plus `OmenMon.exe -Probe` for a WMI + BIOS + EC dump to attach to a bug report. |
| **Telemetry sharing** | None. | Optional named-pipe server (`\\.\pipe\OmenMon_FanData`) with JSON fan RPM, so another program can read the fans without opening a second EC session. |
| **Performance modes** | Legacy L0–L8 levels. | The OMEN Gaming Hub presets (ECO / Quiet / Default / Performance / Custom), still with the old levels kept for existing configs. |
| **Model support** | The models the authors had. | Whatever has been reported and tested since. Several boards still need more testing. |

---

<p align="center"><a href="https://omenmon.github.io/">Project Page &amp; Documentation</a> • <a href="https://omenmon.github.io/build">How to Build</a> • <a href="https://omenmon.github.io/build#history">Version History</a> • <a href="https://github.com/OmenMon/Localization">Translations</a> • <a href="https://github.com/OmenMon/OmenMon/releases/latest">Download ⭳</a></p>

![OmenMon graphical mode overview](https://omenmon.github.io/pic/gui-overview.png)

**OmenMon** is a lightweight application that interacts with the Embedded Controller (EC) and WMI BIOS routines of an _HP Omen_ laptop in order to access hardware settings, in particular to [query temperature sensors](https://omenmon.github.io/gui#temperature) and dynamically [adjust fan speeds](https://omenmon.github.io/gui#fan-control). It also helps you pick your favorite [keyboard backlight colors](https://omenmon.github.io/gui#keyboard) and put the [_Omen_ key](https://omenmon.github.io/config#key) to better use.

**OmenMon** endeavors to replace all the useful functionality of the _Omen Hub_ (a.k.a. _Omen Control Center_), the laptop manufacturer's application, without any of its numerous anti-features. It does not connect to the network at all, does not have advertising, built-in store, social-media integration and whatnot. It does only what you expect it to do and nothing else.

**OmenMon** is designed to run with minimal resource overhead. It comes with a clear and compact [graphical interface](https://omenmon.github.io/gui), offering a great degree of [configurability](https://omenmon.github.io/config) while also featuring an extensive [command-line mode](https://omenmon.github.io/cli) where various BIOS and EC read and write operations can be performed manually. 

Most features are specific to _HP_ devices with a compatible BIOS interface exposed by the `ACPI\PNP0C14` driver but command-line [Embedded Controller operations](https://omenmon.github.io/cli#ec) should work on all laptops.

## License

**OmenMon** Copyright © 2023-2024 [Piotr Szczepański](https://piotr.szczepanski.name/)  
**OmenMon-Reborn** modifications Copyright © 2026 [seakyy](https://github.com/seakyy)

This application is _free software_: you can redistribute it and/or modify it under the terms of the [GNU General Public License Version 3](https://www.gnu.org/licenses/gpl-3.0.html#license-text) as published by the [Free Software Foundation](https://www.fsf.org/). The full text of the license is available as `LICENSE.md` in this repository or on the [Web page](https://omenmon.github.io/more#license).

**OmenMon** builds upon the work of several other projects. Please see the [acknowledgements](https://omenmon.github.io/more#acknowledgements) for details.

_This software is not affiliated with or endorsed by HP. Any brand names are used for informational purposes only._

## Related Projects

* [XML Translator](https://github.com/Initsnow/xmltranslator) by **[@Initsnow](https://github.com/Initsnow)** – for [translating OmenMon](https://github.com/OmenMon/Localization) into other languages
* [HP Omen Sequencer Keyboard Lights Control Utility](https://github.com/slysherz/lights-for-omen-sequencer) by **[@slysherz](https://github.com/slysherz/)**
