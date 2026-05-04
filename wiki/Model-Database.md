# Model Database

The model database maps a device's WMI baseboard `Product` string (a 4-character hex ID, e.g. `8A14`) to a set of EC register addresses. It lives in the `<Models>` section of `OmenMon.xml` and is loaded into `Config.Models` at startup.

---

## Finding your Product ID

Run the following from an elevated command prompt or PowerShell:

```powershell
(Get-WmiObject Win32_BaseBoard).Product
```

Or use OmenMon itself:

```
OmenMon.exe -Bios
```

The `Product` field in the output is the ID you need.

---

## XML schema

Add a `<Model>` element inside `<Config><Models>`:

```xml
<OmenMon>
  <Config>
    <Models>
      <Model ProductId="8A14" DisplayName="Omen 16 2022 (b1xxx, i9-12900H)">
        <FanLevelReg0>52</FanLevelReg0>
        <FanLevelReg1>53</FanLevelReg1>
        <FanRateReadReg0>46</FanRateReadReg0>
        <FanRateReadReg1>47</FanRateReadReg1>
        <FanRateWriteReg0>44</FanRateWriteReg0>
        <FanRateWriteReg1>45</FanRateWriteReg1>
        <FanSpeedReg0>176</FanSpeedReg0>
        <FanSpeedReg1>178</FanSpeedReg1>
        <CountdownReg>99</CountdownReg>
        <ManualReg>98</ManualReg>
        <ModeReg>149</ModeReg>
        <SwitchReg>244</SwitchReg>
      </Model>
    </Models>
  </Config>
</OmenMon>
```

All register values are **decimal** integers (0–255). The table below shows the mapping to named EC registers:

| XML element | EC name | Decimal | Hex | Purpose |
|-------------|---------|---------|-----|---------|
| `FanLevelReg0` | SRP1 | 52 | `0x34` | CPU fan setpoint [krpm] |
| `FanLevelReg1` | SRP2 | 53 | `0x35` | GPU fan setpoint [krpm] |
| `FanRateReadReg0` | XGS1 | 46 | `0x2E` | CPU fan duty cycle readback [%] |
| `FanRateReadReg1` | XGS2 | 47 | `0x2F` | GPU fan duty cycle readback [%] |
| `FanRateWriteReg0` | XSS1 | 44 | `0x2C` | CPU fan duty cycle write [%] |
| `FanRateWriteReg1` | XSS2 | 45 | `0x2D` | GPU fan duty cycle write [%] |
| `FanSpeedReg0` | RPM1 | 176 | `0xB0` | CPU fan speed low byte [rpm, word] |
| `FanSpeedReg1` | RPM3 | 178 | `0xB2` | GPU fan speed low byte [rpm, word] |
| `CountdownReg` | XFCD | 99 | `0x63` | Manual-mode auto-reset countdown [s] |
| `ManualReg` | OMCC | 98 | `0x62` | Manual fan control enable |
| `ModeReg` | HPCM | 149 | `0x95` | Performance mode preset |
| `SwitchReg` | SFAN | 244 | `0xF4` | Fan off switch |

> **Note on RPM word layout:** `FanSpeedReg0` points to the low byte of a little-endian 16-bit word. The high byte is at `FanSpeedReg0 + 1` (`RPM2`, `0xB1`). Same pattern for `FanSpeedReg1` / `RPM4`. This is why the GPU fan uses `RPM3` (`0xB2`) and not `RPM2` — `RPM2` is the CPU high byte.

---

## Known model IDs

The following IDs have been reported in upstream issues. Entries marked ✅ have been verified; entries marked ❓ are based on issue reports and need a `-Probe` dump to confirm correct register addresses.

| ProductId | Device | Status |
|-----------|--------|--------|
| `8A13` | Omen 16 2022 (early) | ✅ Default layout |
| `8A14` | Omen 16 2022 (b1xxx) | ✅ Default layout |
| `8A4C` | Omen 16 2022 (k0xxx) | ✅ Default layout |
| `8A4D` | Omen 16 2022 refresh | ❓ |
| `8C9C` | Victus 16 s1023dx (2024) | ❓ |
| `8A3E` | Victus 15 fb0102la | ❓ |
| `8748` | Omen 17 cb1046nr (2021) | ❓ |
| `88FE` | Omen 17 ck0xxx (2020) | ❓ |
| `88EC` | Victus 16 e0008nt (2021) | ❓ |
| `8574` | Omen 15 dc1xxx (2018) | ❓ |
| `878D` | Envy 15 ep0003nl (2020) | ❓ |

To add a confirmed entry, open a pull request editing the default `OmenMon.xml` with the new `<Model>` block and attach the `-Probe` Markdown output as evidence.

---

## Auto-saved entries

When the [Auto-Detector](Auto-Detection) confirms the default layout for an unknown device, it writes the entry to the user's local `OmenMon.xml` automatically. Those entries are **not** in the compiled-in defaults — they only exist on that machine until someone submits a PR to add them to the shipped XML.
