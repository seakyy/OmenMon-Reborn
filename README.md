# OmenMon-Reborn

> **Welcome to OmenMon-Reborn** — an active fork by **[seakyy](https://github.com/seakyy)**, last updated April 2026.  
> The original OmenMon hardcoded the EC hardware layout for 2023 devices, causing severe issues (fans stuck at 100%, wrong temperatures) on newer and older HP Omen and Victus laptops.  
>
> **OmenMon-Reborn** fixes this with a dynamic, XML-driven model database and a safe **Heuristic Auto-Detector**. If your laptop is unknown, it scans the hardware read-only and configures itself — no coding required.

## What's new in Reborn?

| Feature | Description |
|---------|-------------|
| **Self-Healing** | Unknown HP laptop? The Smart Fallback reads your EC registers safely and writes a working profile to `OmenMon.xml`. |
| **Fan Bug Fixed** | Ghost sensors TNT2–TNT5 that forced fans to 100% are now disabled by default for fan curves (still visible in the GUI). |
| **One-Click Contribute** | Click "Contribute Hardware Data..." in the tray menu to copy your hardware dump to the clipboard and open a GitHub issue. |
| **`-Probe` CLI verb** | `OmenMon.exe -Probe` generates a full WMI + BIOS + EC snapshot as Markdown — ideal for bug reports. |

See [CHANGELOG.md](CHANGELOG.md) for the full release notes, [ARCHITECTURE.md](ARCHITECTURE.md) for design rationale, and [MODIFICATIONS.md](MODIFICATIONS.md) for GPLv3 compliance information.

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
