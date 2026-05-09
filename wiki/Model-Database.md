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
        <!-- Optional manual-mode override fields (omit when defaults are correct):  -->
        <!-- <ManualValueOn>8</ManualValueOn>     value written to ManualReg to engage manual -->
        <!-- <ManualValueOff>72</ManualValueOff>  value written to ManualReg to release manual -->
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
| `ManualValueOn` *(opt.)* | — | 6 | `0x06` | Value written to `ManualReg` to engage manual fan control. Override per-board if the BIOS gates it on a different magic value (e.g. 8BBE wants `0x08` written to `EC[0x06]`). Defaults to `0x06`. |
| `ManualValueOff` *(opt.)* | — | 0 | `0x00` | Value written to `ManualReg` to release manual fan control. Override per-board if the BIOS idles a different value (8BBE idles at `0x48`). Defaults to `0x00`. |

> **Note on RPM word layout:** `FanSpeedReg0` points to the low byte of a little-endian 16-bit word. The high byte is at `FanSpeedReg0 + 1` (`RPM2`, `0xB1`). Same pattern for `FanSpeedReg1` / `RPM4`. This is why the GPU fan uses `RPM3` (`0xB2`) and not `RPM2` — `RPM2` is the CPU high byte.

---

## Known model IDs

The following IDs have been reported in upstream issues. Entries marked ✅ have been verified on hardware; ⚠️ entries are derived from probe data and need owner confirmation that fan controls actually work; ❓ entries are based on issue reports alone and need a `-Probe` dump to confirm register addresses.

| ProductId | Device | Status |
|-----------|--------|--------|
| `8A13` | Omen 16 2022 (early) | ✅ Default layout |
| `8A14` | Omen 16 2022 (b1xxx) | ✅ Default layout |
| `8A4C` | Omen 16 2022 (k0xxx) | ✅ Default layout |
| `8A4D` | Omen 16 2022 refresh | ❓ |
| `8A25` | Victus 16 (2022, d1xxx) | ✅ Default layout (Fan2 unsupported) |
| `8BAB` | Omen 16 (2025) | ✅ 2023+ layout, RPM at `0xE3`/`0xE5` |
| `8BAD` | Omen 17 (2023) | ✅ FanLevel `0x34`/`0x35` |
| `8BBE` | Victus 16 R0053NT (2023) | ⚠️ 2023+ layout, manual gate at `0x06`=`0x08` (issue #19, **needs hardware confirmation**) |
| `8BD4` | Victus 16-S0053NT (2024) | ✅ Pattern C, single shared fan |
| `8C9C` | Victus 16 (2024) | ✅ FanSpeed `0xF1` (×60), confirmed |
| `8D07` | Victus 15 (2024, AMD Ryzen 5 7535HS) | ⚠️ 2022 layout (FanLevel `0x34`/`0x35`, rate `0x2C`/`0x2D`), RPM `0xB0`/`0xB2` (issue #23, **needs gaming-load verification**) |
| `8E71` | Omen 16-am1000 (2026) | ✅ 2023+ layout, RPM at `0xB0`/`0xB2` (issue #22) |
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
