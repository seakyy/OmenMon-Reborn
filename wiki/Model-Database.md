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
        <!-- Optional override fields (omit each one when the global default is correct): -->
        <!-- <ManualValueOn>8</ManualValueOn>     value written to ManualReg to engage manual  -->
        <!-- <ManualValueOff>72</ManualValueOff>  value written to ManualReg to release manual -->
        <!-- <TempCpuReg>176</TempCpuReg>         remap the "CPUT" sensor to a custom EC offset -->
        <!-- <TempGpuReg>180</TempGpuReg>         remap the "GPTM" sensor to a custom EC offset -->
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
| `TempCpuReg` *(opt.)* | — | 0 | `0x00` | Remap the named `CPUT` temperature sensor to read from this EC offset instead of the global `<Temperature>` config (which uses `0x57`). Use when the board moved the real CPU temp away from `EC[0x57]` — e.g. 8C9C exposes it at `EC[0xB0]`. `0` = no override. |
| `TempGpuReg` *(opt.)* | — | 0 | `0x00` | Remap the named `GPTM` sensor to a custom EC offset (default global is `0xB7`). Use when the board moved the real GPU temp / hotspot away from the legacy address — e.g. 8C9C exposes it at `EC[0xB4]`. `0` = no override. |

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
| `8C9C` | Victus 16-1034NF (2024) | ✅ CPU temp `0xB0` / GPU temp `0xB4` (HWInfo-validated, issue #16); RPM display sourced from BIOS `GetFanLevel × 100` (issue #28 — EC[0xF1] only tracked during the calibration sweep, read as ~0x02 under real OEM control) |
| `8C30` | Victus 15-fb1000 (2023, AMD) | ⚠️ 2022 layout (FanLevel `0x34`/`0x35`, rate `0x2C`/`0x2D`), RPM `0xB0`/`0xB2` (issue #32); **note:** firmware freezes the fan controller when rate is pushed to 100% — restart required to recover |
| `8D07` | Victus 15 (2024, AMD Ryzen 5 7535HS) | ⚠️ 2022 layout (FanLevel `0x34`/`0x35`, rate `0x2C`/`0x2D`), RPM `0xB0`/`0xB2` (issue #23, **needs gaming-load verification**) |
| `8E71` | Omen 16-am1000 (2026) | ✅ 2023+ layout, RPM at `0xB0`/`0xB2` (issue #22) |
| `8DD0` | Omen (2025) | ✅ 2023+ layout, RPM at `0xB0`/`0xB2` (issue #26, false-positive auto-cal mitigated via built-in `AutoCal.Prime` in v1.4.0 — issue #33) |
| `8D26` | Omen 16-ap0007ns (2026) | ✅ 2023+ layout, RPM at `0xB0`/`0xB2` (issue #52) |
| `88EB` | Victus 16 (2021) | ✅ 2023+ layout, RPM at `0xB0`/`0xB2` (issue #48) |
| `8D41` | Omen Max 16 (2025) | ✅ RPM at `0x5C`/`0x9F` (16-bit LE; CPU ≈5760, GPU ≈6533 RPM at 100%). Issues #87/#90 — graduated from the read-only `AutoCal` mapping to a native entry in v1.4.5. Fan **control** registers are the legacy defaults (identical to the unknown-model fallback) and remain **unverified** on this 2025 "Max" layout; post a `-Diag` if the slider / manual mode has no effect. |
| `8BA9` | Omen 16-wd0xxx (2024) — single-fan SKU | ⚠️ Read-only RPM mapping at `EC[0xF1]` (16-bit LE; idle ≈1793, max ≈5125 RPM), confirmed against the issue #92 Auto-Calibration sweep (@M1918IIBAR). No GPU fan detected (single-fan chassis). Shipped as a read-only `AutoCal` mapping, **not** a native `<Model>` entry — fan **control** registers are unverified on this board, so control stays on the safe auto-detector fallback. Post a `-Diag` if the slider / manual mode has no effect. |
| `8C77` | Omen 16-wf1012nl (2024) — single-fan SKU | ⚠️ Sidecar-resolved via the Auto-Calibration Wizard (issue #50). CPU fan at `EC[0xD2]` (`PeriodEncoded8`, idle ≈ `0xB2`, max ≈ `0x11`). No GPU fan detected — likely a physically single-fan chassis, not a missed register. Not yet added to the shipped native database: waiting on a second `8C77` owner to confirm the layout is consistent across the SKU (HP recycles product IDs across regional variants). Owners can install OmenMon as normal — the wizard's `OmenMon-AutoCal.xml` keeps the install working out of the box. |
| `8A3E` | Victus 15 fb0102la | ❓ |
| `8748` | Omen 17 cb1046nr (2021) | ❓ |
| `88FE` | Omen 17 ck0xxx (2020) | ❓ |
| `88EC` | Victus 16 e0008nt (2021) | ❓ |
| `8574` | Omen 15 dc1xxx (2018) | ❓ |
| `878D` | Envy 15 ep0003nl (2020) | ❓ |

To add a confirmed entry, open a pull request editing the default `OmenMon.xml` with the new `<Model>` block and attach the `-Probe` Markdown output as evidence.

### Pending field reports (awaiting hardware data)

These SKUs were reported in v1.4.5 issues but **cannot ship a native `<Model>`
entry yet** — no confirmed Product ID + register dump is available, and HP
recycles Product IDs across regional/CPU variants, so guessing a register map
risks garbage RPM or a fan-controller lock on someone else's machine. They are
**already usable today**: the read-only [Auto-Detector](Auto-Detection) picks a
safe layout on launch, and the **Auto-Calibrate & Diagnose…** wizard resolves
the tachometers into a local `OmenMon-AutoCal.xml`. To get a board promoted to
the shipped database, attach the `OmenMon.exe -Diag` output (now including the
Live Fan Telemetry table, issue #49) to the relevant issue.

| Reported as | Issue | Needed to ship a native entry |
|-------------|-------|-------------------------------|
| HP Omen 16-wd0004nw | #51, #75 | `-Diag` Product ID + Auto-Calibration report (0/30/70/100 % sweep) confirming the tachometer registers. **Update:** a `wd0xxx` board reporting Product ID `8BA9` (#92) supplied exactly this data and is now mapped read-only (see the `8BA9` row above). A `wd0004nw` owner should confirm whether their `-Diag` also shows `8BA9` — if so, this row is covered. |
| HP (Victus/Omen) xd0020ax | #77 | `-Diag` Product ID + `-Probe` dump confirming fan-level / RPM register layout |
| HP Omen, AMD Ryzen 9 7945HS SKU | #85 | `-Diag` Product ID + sustained-load Auto-Calibration report (a prior 7945HS-class report, #76/#85 on `8BCA`, gave conflicting tach layouts across SKUs — a third corroborating dump is needed before shipping) |

---

## Auto-saved entries

When the [Auto-Detector](Auto-Detection) confirms the default layout for an unknown device, it writes the entry to the user's local `OmenMon.xml` automatically. Those entries are **not** in the compiled-in defaults — they only exist on that machine until someone submits a PR to add them to the shipped XML.
