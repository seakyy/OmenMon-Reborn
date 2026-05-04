# Architecture

This page explains the structural changes introduced in OmenMon-Reborn 1.0.0 relative to the upstream `0.61.1` codebase.

---

## The original problem

`Hardware/Platform.cs` contained a hardcoded `switch` on the WMI baseboard `Product` string:

```csharp
switch(this.System.GetProduct()) {
    case "8A13":
    case "8A14":
    default:
        // build FanArray with 2022/2023 register addresses
        break;
}
```

Every unrecognised device hit `default` and silently inherited the 2023 register layout. When those addresses were wrong for the actual hardware — different fan setpoint registers, different RPM word locations — the result ranged from incorrect temperature readings to fans permanently locked at full speed. This is the root cause behind GitHub issues #57, #66, #81, #92, #122, and several others.

---

## Phase 3 — Dynamic model database

Three new components replace the `switch`:

### `Hardware/PlatformPreset.cs`

A plain data class that holds every EC register address the fan subsystem needs:

| Field | Register | Default value |
|-------|----------|---------------|
| `FanLevelReg0 / 1` | SRP1 / SRP2 | `0x34` / `0x35` |
| `FanRateReadReg0 / 1` | XGS1 / XGS2 | `0x2E` / `0x2F` |
| `FanRateWriteReg0 / 1` | XSS1 / XSS2 | `0x2C` / `0x2D` |
| `FanSpeedReg0 / 1` | RPM1 / RPM3 | `0xB0` / `0xB2` |
| `CountdownReg` | XFCD | `0x63` |
| `ManualReg` | OMCC | `0x62` |
| `ModeReg` | HPCM | `0x95` |
| `SwitchReg` | SFAN | `0xF4` |

`PlatformPreset.Default` holds those confirmed values and is always available as a compile-time fallback.

### `Config.Models` (`Library/ConfigData.cs`)

```csharp
public static Dictionary<string, PlatformPreset> Models =
    new Dictionary<string, PlatformPreset>();
```

Populated at startup from the `<Models>` block in `OmenMon.xml`. `Config.SaveModel()` adds a preset to the dictionary and calls `Config.Save()` to persist it.

### `Platform.InitFans()` (`Hardware/Platform.cs`)

```csharp
string product = this.System.GetProduct();
PlatformPreset preset = Config.Models.ContainsKey(product)
    ? Config.Models[product]
    : PlatformPreset.Default;
```

All `FanArray` and `EcComponent` objects are constructed from the preset's fields. The hardcoded `switch` is gone.

---

## Phase 4 — Bug fixes

**TNT sensor isolation** — `TNT2`–`TNT5` (EC `0x47`–`0x4B`) are set to `Use = false` by default in `ConfigData.TemperatureSensor`. On many devices these registers report constant 84 °C or 97 °C, which drives fan programs to maximum. They remain declared and visible in the GUI; only their contribution to the max-temperature fan trigger is suppressed.

**HVCI hint** — When the WinRing0 driver fails to load, the error path in `Driver/Ring0.cs` and the EC section of `CliOpProbe.cs` both emit the phrase *"HVCI (Memory Integrity) active"* to direct users to the correct Windows Security setting.

---

## Phase 5 — Heuristic auto-detection

See the [Auto-Detection](Auto-Detection) page for the user-facing flow.

`Hardware/AutoDetector.DetectHeuristic(productId)` performs a full 256-byte read-only EC dump and checks two invariants that hold on any standard 2022+ Omen layout:

| Check | Register | Condition |
|-------|----------|-----------|
| CPU temperature readable | `CPUT` — `0x57` | 20 °C ≤ value ≤ 95 °C |
| Fan RPM plausible | `RPM1` — `0xB0`/`0xB1` (word) | 0 ≤ value ≤ 7000 |

If both pass, the device is mapped to `PlatformPreset.Default` under its own `ProductId`. If either fails, `null` is returned and the user is directed to file a report via the Contribute feature.

The check fires exactly once per session, wired to `GuiFormMain.Shown` and unsubscribed immediately after.

---

## Phase 6 — Probe reuse for the GUI

`CliOpProbe.cs` was originally CLI-only. The key change is `ProbeGetMarkdown(bool includeEcDiff)` — an `internal static` method that returns the Markdown string without file or console side-effects.

- **CLI** calls `ProbeRun()` → `ProbeGetMarkdown(includeEcDiff: true)` → writes to file (includes 5 s EC diff wait)
- **GUI** calls `ProbeGetMarkdown(includeEcDiff: false)` → copies string to clipboard (instant, no wait)

The application itself never initiates a network connection. `Process.Start(url)` hands off to the OS default browser — the outbound HTTP request is the browser's, not OmenMon's.
