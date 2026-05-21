# OmenMon — User Instructions

> For the full GitHub Wiki, see: [wiki/Home.md](wiki/Home.md)

---

## Table of Contents

1. [Quick Start](#quick-start)
2. [Fan Control Modes](#fan-control-modes)
   - [Auto (Default)](#auto-default)
   - [Performance](#performance)
   - [Cool](#cool)
   - [Const (Custom Speed)](#const-custom-speed)
   - [Fan Programs (Temperature Curves)](#fan-programs-temperature-curves)
   - [Max & Off](#max--off)
   - [Legacy Modes](#legacy-modes)
3. [Battery Care](#battery-care)
4. [Keyboard Backlight & RGB](#keyboard-backlight--rgb)
5. [GPU Mode](#gpu-mode)
6. [Auto-Config on Startup](#auto-config-on-startup)
7. [CLI Reference](#cli-reference)
8. [Configuration File (OmenMon.xml)](#configuration-file-omenmonxml)
9. [Known Issues & Fixes](#known-issues--fixes)
10. [Reporting Issues](#reporting-issues)

---

## Quick Start

1. Run `OmenMon.exe` — it appears as a tray icon.
2. Left-click the icon to open the main window.
3. Right-click the icon for the context menu (fan mode, RGB presets, settings).
4. To close completely: right-click → **Exit**.

> OmenMon requires **administrator privileges** to access hardware registers.  
> If the driver fails to load, see [Known Issues](#known-issues--fixes).

---

## Fan Control Modes

### Auto (Default)

Let the BIOS manage fan speed automatically based on the active performance profile.  
Select **Auto** + a fan mode from the dropdown, then click **Set**.

| Fan Mode    | Hex  | Description                                      |
|-------------|------|--------------------------------------------------|
| **Default** | 0x30 | Balanced — matches Windows "Balanced" power plan |
| **Performance** | 0x31 | Faster fans, higher sustained clocks         |
| **Cool**    | 0x50 | Prioritises cooling, slightly lower performance  |

> **Tip:** "Performance" and "Cool" are the two main modes you'll use day-to-day.

### Performance

Activates HP's Performance fan profile. Fans spin faster and the system maintains higher sustained boost clocks. Best for gaming or heavy workloads. Equivalent to OMEN Gaming Hub "Performance" mode.

### Cool

Prioritises temperatures over noise. Fans react faster to temperature spikes but performance may be slightly reduced to keep thermals in check.

### Const (Custom Speed)

Set both fans to a fixed speed level (0–55).

1. Select the **Const** radio button.
2. Use the sliders to set CPU and GPU fan levels.
3. Click **Set**.

> **Notes:**
> - Level 0 with both fans = fans off (use only briefly, not for sustained loads).
> - Level 55 ≈ max hardware speed (~5500 RPM).
> - The BIOS countdown timer resets every ~15 s automatically while Const is active.

### Fan Programs (Temperature Curves)

Fan programs map **temperature → fan level** automatically as you work.

Define programs in `OmenMon.xml`:

```xml
<FanPrograms>
  <Program Name="Gaming">
    <FanMode>Performance</FanMode>
    <GpuPower>Maximum</GpuPower>
    <Level Temperature="0">  <Cpu>20</Cpu><Gpu>20</Gpu></Level>
    <Level Temperature="50"><Cpu>30</Cpu><Gpu>30</Gpu></Level>
    <Level Temperature="70"><Cpu>45</Cpu><Gpu>45</Gpu></Level>
    <Level Temperature="85"><Cpu>55</Cpu><Gpu>55</Gpu></Level>
  </Program>
  <Program Name="Quiet">
    <FanMode>Cool</FanMode>
    <GpuPower>Minimum</GpuPower>
    <Level Temperature="0">  <Cpu>20</Cpu><Gpu>20</Gpu></Level>
    <Level Temperature="60"><Cpu>25</Cpu><Gpu>25</Gpu></Level>
    <Level Temperature="75"><Cpu>35</Cpu><Gpu>35</Gpu></Level>
    <Level Temperature="85"><Cpu>50</Cpu><Gpu>50</Gpu></Level>
  </Program>
</FanPrograms>
```

- `Temperature` = trigger point in °C (max of CPU/GPU/PCH)
- `Cpu` / `Gpu` = fan speed level (0–55)
- `FanMode` = background performance mode while program runs
- `GpuPower` = `Maximum` | `Medium` | `Minimum`

**AutoConfig** can auto-start the default program on launch and switch to an alternate program on battery.

### Max & Off

| Option | Effect |
|--------|--------|
| **Max** | Forces both fans to maximum hardware speed |
| **Off** | Switches fans off — **use with extreme caution**, only when idle/cool |

### Legacy Modes

These are older BIOS values preserved for compatibility. Most users won't need them.

| Mode | Hex | Note |
|------|-----|------|
| LegacyDefault | 0x00 | Very old devices |
| LegacyPerformance | 0x01 | Old performance profile |
| LegacyCool | 0x02 | Old cool profile |
| LegacyQuiet | 0x03 | Old quiet profile |
| LegacyExtreme | 0x04 | Old extreme/boost profile |
| L0–L8 | various | Internal numeric aliases |

> On modern OMEN laptops (2022+) use **Default**, **Performance**, or **Cool** instead.

---

## Battery Care

OmenMon v1.1.0 supports the HP **Adaptive Battery Extender** (80% charge limit) found on newer OMEN/Victus models, as well as the legacy `Cmd 0x24` interface on older models.

Access via **CLI**:

```
OmenMon.exe -BatCare On   # Enable 80% charge limit
OmenMon.exe -BatCare Off  # Charge to 100%
```

> **Note:** Battery Care requires AC power to toggle on some firmware versions.  
> If the BIOS rejects the call, try again while plugged in.

---

## Keyboard Backlight & RGB

- **Toggle backlight**: checkbox in main window, or right-click tray → Keyboard → Backlight On/Off.
- **Color zones**: click a zone on the keyboard image to open the color picker.
- **Presets**: save/load named color presets via the dropdown.
- **Cycle presets** with the OMEN key (configure `KeyToggleColorPreset` in XML).

Four-zone keyboard (most models):
- Zone 0: Right
- Zone 1: Middle
- Zone 2: Left
- Zone 3: WASD

---

## GPU Mode

Right-click tray → **Graphics** to switch between:

| Mode | Description |
|------|-------------|
| Hybrid | Integrated + discrete, power-managed |
| Discrete | Always use dedicated GPU |
| Optimus | nVidia Optimus mode |

> A **reboot is required** for GPU mode changes to take effect.

---

## Auto-Config on Startup

When `AutoConfig = true` in `OmenMon.xml`:

- Applies GPU power settings on launch.
- Starts `FanProgramDefault` on AC power.
- Starts `FanProgramDefaultAlt` on battery.
- Switches programs automatically when AC is plugged/unplugged.

```xml
<AutoConfig>true</AutoConfig>
<FanProgramDefault>Gaming</FanProgramDefault>
<FanProgramDefaultAlt>Quiet</FanProgramDefaultAlt>
<GpuPowerDefault>Maximum</GpuPowerDefault>
```

---

## CLI Reference

Run from Command Prompt (Administrator):

```
OmenMon.exe [mode] [options]
```

| Command | Description |
|---------|-------------|
| `-Gui` | Start in tray mode (default) |
| `-BatCare On\|Off` | Toggle battery care (80% limit) |
| `-FanMode <mode>` | Set fan mode (Default/Performance/Cool/…) |
| `-FanMax On\|Off` | Toggle maximum fan speed |
| `-FanOff On\|Off` | Toggle fans off |
| `-FanLevel <cpu> <gpu>` | Set fan levels (0–55) |
| `-Prog <name>` | Run a named fan program |
| `-GpuPower <level>` | Set GPU power (Maximum/Medium/Minimum) |
| `-Ec` | Open EC monitor |
| `-Probe` | Run heuristic hardware scanner |

---

## Configuration File (OmenMon.xml)

Located in the same folder as `OmenMon.exe`. Key settings:

```xml
<OmenMon>
  <Config>
    <AutoConfig>true</AutoConfig>
    <AutoStartup>true</AutoStartup>

    <!-- BIOS -->
    <BiosErrorReporting>true</BiosErrorReporting>
    <BiosHeartbeatPauseOnBattery>true</BiosHeartbeatPauseOnBattery>

    <!-- Fan countdown -->
    <FanCountdownExtendAlways>false</FanCountdownExtendAlways>
    <FanCountdownExtendInterval>120</FanCountdownExtendInterval>

    <!-- Fan programs -->
    <FanProgramDefault>Gaming</FanProgramDefault>
    <FanProgramDefaultAlt>Quiet</FanProgramDefaultAlt>
    <FanProgramSuspend>true</FanProgramSuspend>

    <!-- GPU power default -->
    <GpuPowerDefault>Maximum</GpuPowerDefault>
  </Config>
</OmenMon>
```

### BiosHeartbeatPauseOnBattery

**New in v1.1.1.** When `true` (default), OmenMon pauses its BIOS heartbeat timer while on battery. This prevents a firmware conflict where HP's battery manager can force an unexpected hibernate after extended battery use. Set to `false` only if you experience fan control issues on battery.

---

## Known Issues & Fixes

### Laptop hibernates unexpectedly on battery

**Fixed in v1.1.1.** The BIOS heartbeat (periodic `GetFanCount()` call every 30 s) kept HP's Performance Control session alive on battery, conflicting with HP's battery manager and triggering forced hibernation. `BiosHeartbeatPauseOnBattery = true` (default) fixes this.

**Workaround for older versions:** Set `BiosHeartbeatInterval = 0` in `OmenMon.xml` to disable the heartbeat entirely.

### "Failed to initialize" / Driver not loading

1. Run OmenMon as Administrator (required for EC access).
2. Check that the PawnIO driver is installed. v1.4.0+ ships with the
   Microsoft-signed PawnIO driver; if installation was interrupted,
   re-run the installer.
3. If Windows Defender quarantined `OmenMon.exe`, see
   [Windows Defender false positive](#windows-defender-false-positive)
   below.

### Fans stuck at max speed after exiting

Switch to **Auto** mode and click **Set** before closing OmenMon. If already closed, open OmenMon again and switch to Auto.

### Fan control only works on battery / not on AC

Some firmware versions require a specific adapter wattage. If you see "AC Power Low" in OmenMon, the adapter may be under-rated (e.g., 200 W instead of 230 W). Use the original or a compatible adapter.

### CPU temperature not displayed

Your device's EC register layout may differ from the default. Run **Auto-Calibrate & Diagnose...** from the tray menu — the wizard sweeps the fans through four speed steps, scans the EC for the registers that behave like tachometers, and applies the correct ones to the live session automatically. The override is saved to `OmenMon-AutoCal.xml` next to the executable and reloaded on the next launch.

### CPU temperature not shown or wrong zone count (RGB)

Known on some OMEN Max 16 / non-standard models. Use `Probe` mode to identify your EC layout. See [wiki/Contributing-Hardware-Data.md](wiki/Contributing-Hardware-Data.md).

### BSOD / Memory integrity conflicts

OmenMon v1.4.0+ uses the Microsoft-signed
[PawnIO](https://pawnio.eu/) driver, which is HVCI-compatible and
should not conflict with Memory Integrity (Core Isolation). If you
experience a BSOD with v1.4.0 or newer:

- Make sure you are not running an older v1.3.x install alongside
  v1.4.0 — the old WinRing0 driver should be uninstalled.
- File the BSOD's bugcheck code (e.g. `DRIVER_VERIFIER_DETECTED_VIOLATION`)
  as a GitHub issue with your model and the output of
  `OmenMon.exe -Diag`.

### Windows Defender false positive

**OmenMon-Reborn v1.4.0+ does not ship its own kernel driver.** It
talks to hardware through the Microsoft-signed PawnIO driver, so the
old `VulnerableDriver:WinNT/WinRing0` warning from v1.3.x is gone.

Heuristic AV engines occasionally still flag low-level hardware tools.
`OmenMon.exe` has been reviewed and **cleared by Microsoft**, and is
also clean on every other engine surveyed by VirusTotal:

- **Microsoft submission ID:** `504e5120-8e6f-46c6-bf5f-da34a0176fca`
- **Microsoft determination:** *"do not meet our criteria for malware
  or potentially unwanted applications. The detection has been removed."*
- **VirusTotal:** 0 / 76 detections —
  [view full report](https://www.virustotal.com/gui/file/21384a329e082b66bf12255c057e84fc8608a44785231ee58a9f0316bcdcf0d1)
  (MAPS-submitted SHA-256 `21384a3…cdcf0d1`)
- **Per-release verification:** Each release ships a `SHA256SUMS.txt`
  asset. Verify your download with
  `Get-FileHash -Algorithm SHA256 <file>` and compare. See
  [SECURITY.md](SECURITY.md#how-to-verify-your-downloaded-binary)
  for the full procedure.

If your local Defender still shows a cached detection, refresh its
definitions:

1. Open Command Prompt **as Administrator**.
2. `cd "C:\Program Files\Windows Defender"`
3. `MpCmdRun.exe -removedefinitions -dynamicsignatures`
4. `MpCmdRun.exe -SignatureUpdate`

Expected output:

```
Starting Dynamic Signature removal.
Done!
…
Signature update finished.
```

After the update completes, the binary will not be flagged on this
machine. See [SECURITY.md](SECURITY.md#windows-defender-false-positives)
for the full background.

**Never download OmenMon from unofficial sources.** Always use the
GitHub Releases page on the official repository.

---

## Reporting Issues

1. Open OmenMon → right-click tray → **Auto-Calibrate & Diagnose...** and run the wizard. It applies the discovered EC registers to the live session and copies a Markdown report (board ID, BIOS born-date, ranked candidates, full 4-step EC sweep) to the clipboard.
2. If the issue is unrelated to fan readings, you can also run `OmenMon.exe -Probe` for a static snapshot.
3. File a bug at: **https://github.com/seakyy/OmenMon-Reborn/issues/new** and paste the wizard's report (or the `-Probe` output) into the issue body.

Include:
- Your HP model (e.g., `HP OMEN 16-b0075ng`)
- Windows version
- OmenMon version
- Steps to reproduce
- Probe output (if relevant)
