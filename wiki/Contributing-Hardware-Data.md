# Auto-Calibration Wizard (Contributing Hardware Data)

OmenMon-Reborn ships an active **Auto-Calibration Wizard** for users with unsupported devices or shuffled EC layouts. Unlike the older "copy a hex dump and wait for someone to add a `<Model>` entry" flow it replaced, the wizard discovers the correct fan-tachometer registers itself, applies them to the live session immediately, and persists the result so it survives a restart. The Markdown report it generates is still useful for upstream contribution — but your fans, RPM readings, and curves work straight away.

> **Heads up:** the wizard physically runs your fans up to 100 % for ~12 seconds. Save your work, expect noise, keep the laptop on a hard surface.

---

## When to run it

Run the wizard if any of these apply:

- Fan RPM in the main window reads `0` or a clearly wrong value while the fans are audibly spinning.
- The startup [Auto-Detector](Auto-Detection) prompt told you it could not confirm the register layout.
- You're on a 2024+ HP Victus / Omen board and OmenMon's RPM differs from HP OMEN Gaming Hub.
- You're contributing a brand-new model to the project.

You do **not** need to run it on a model already listed in `OmenMon.xml`'s `<Models>` block — those are pre-confirmed.

---

## User flow

### Step 1 — Open the tray menu

Right-click the OmenMon tray icon. The **"Auto-Calibrate & Diagnose..."** item sits between the Settings submenu and the main window toggle:

![Auto-Calibrate & Diagnose menu item](images/contribute-menu-item.png)

### Step 2 — Start the wizard

The wizard window opens with a short warning, two opt-in checkboxes, and a Start button:

- **Throttle background apps** — drops the priority of well-known CPU/GPU hogs (OBS, ffmpeg, Premiere, Photoshop, Blender, Unity, IDEs, Defender's `MsMpEng`, …) for the duration of the run, then restores them. The wizard never closes user windows — losing unsaved state for a 60-second sweep is not a fair trade.
- **Open the GitHub new-issue page when done** — convenience for upstream contribution.

Click **Start**. The wizard takes over fan control with a 600 s firmware auto-restore countdown — even if OmenMon crashes mid-test, your laptop will revert to automatic fan control on its own within ten minutes.

### Step 3 — Watch the sweep

The wizard runs a 4-step profile: **0 % → 30 % → 70 % → 100 %**, with a 12-second settle window per step (fans need time to physically reach the commanded RPM before the EC tachometer reading stabilises). A live progress bar and scrolling log show what's happening:

```
[  5%] Setting fans to 0%…
[ 10%] Settling at 0%… 12 s
…
[ 95%] Reading EC at 100%…
[ 96%] Analysing register diffs…
[ 98%] Applying detected registers to live session…
[100%] Calibration complete.
```

You can hit **Cancel** at any time. Closing the window via the X button cancels cleanly too — the FormClosing handler waits for the worker to exit before disposing the dialog, so there's no ghost background task continuing the sweep without a UI.

### Step 4 — Result and report

On success the wizard:

1. Publishes the discovered registers to `Library.AutoCal`. The next refresh tick of `Fan.GetSpeed()` reads from those — your RPM display becomes correct immediately, no restart required.
2. Writes `OmenMon-AutoCal.xml` next to the executable. `AutoCal.Load()` reads it back during `Platform.InitFans()` on the next launch, so the override is durable.
3. Saves a full Markdown report as `OmenMon-Calibration-<timestamp>.md` and copies it to your clipboard.
4. (Optional) Opens the GitHub new-issue page so you can paste the report and contribute the layout for inclusion in `OmenMon.xml`'s `<Models>` block.

---

## What the heuristic looks for

The scanner (`Hardware/EcDiffScanner.cs`) compares the four EC dumps and recognises three RPM encodings:

| Pattern | Description | Example |
|---------|-------------|---------|
| **A — 16-bit Little-Endian** | Classic Omen layout. The pair `(r, r+1)` reads as a ushort that idles low and rises monotonically into the 1500–8000 RPM band. | `0xB0 / 0xB1` on most 2022/2023 boards |
| **B — Period-Encoded 8-bit** | Single byte that **falls** monotonically with load — higher byte = slower spin. RPM is computed from the period. | Some 2023+ Omen revisions |
| **C — Direct-Multiplier 8-bit** | Single byte that **rises** monotonically with load. RPM = byte × 100. | HP Victus 16-S0053NT (8BD4) and similar 2024+ boards |

Every candidate has to clear a monotonic-direction check across all four sweep steps before it's accepted, so a single noisy reading cannot poison the result. Static bytes, slow-movers (Δ < 15 — typical temperature sensors warming up over the test window), and OmenMon's own write registers (XSS1/2, SRP1/2, 0x3A/B, OMCC, XFCD, FFFF, SFAN) are excluded up front.

---

## What the report contains

The Markdown report has four sections:

**Device** — Product ID (the key for the `<Model>` entry), BIOS born-date, EC-read method, and the profile that was run.

**Scan Results** — Picked CPU and GPU registers with their pattern, and a collapsible `<details>` table of all ranked candidates with their scores and per-step values. This makes the maintainer's job trivial.

**Raw EC Dumps** — Full 16×16 hex grid for each of the four sweep steps. Even if the heuristic missed something on your board, the raw data is enough for a maintainer to identify the right registers by hand and either patch the heuristic or hard-code the layout.

**Sidecar XML** — Not literally in the report, but `OmenMon-AutoCal.xml` is the same data in a machine-readable form. Attach it to the issue if you want.

---

## Recovering from a bad calibration

The wizard is conservative — it only accepts candidates that monotonically track the commanded fan level — but if it picks the wrong register on your board:

1. Quit OmenMon.
2. Delete `OmenMon-AutoCal.xml` next to the executable.
3. Restart OmenMon. The override is gone; you're back to whatever `OmenMon.xml`'s `<Model>` entry (or `PlatformPreset.Default`) specifies.

Re-run the wizard if you want to try again — `ApplyToLiveSession()` calls `AutoCal.Clear()` before publishing each new run, so a partial scan can never leave a stale override on the other fan.

---

## Reviewing a submission (maintainers)

When a user opens an issue with a calibration report, the relevant fields for adding a `<Model>` entry are:

1. **Product ID** from the Device section — becomes the `ProductId` attribute.
2. **Picked CPU / GPU registers + Pattern** from Scan Results — for Pattern A you can drop straight into `FanSpeedReg0/1`. For Pattern B/C, also add the board to `AutoCal.KnownBoards` in `Library/AutoCal.cs` so users get correct readings out of the box without running the wizard.
3. **Raw EC dumps** — cross-reference with the [known register addresses](Model-Database#xml-schema) for fan-level / fan-rate / control registers.
4. **GetFanCount / GetFanType** from the user's earlier `-Probe` output (if attached) — confirms one-fan vs. two-fan SKU.

Add the `<Model>` block to the bundled `OmenMon.xml` and open a pull request. See [Model Database](Model-Database) for the schema.

---

## CLI-only static dump

If you specifically want the older static-snapshot flow (no fans spun up, no live-session changes), the CLI verb still exists:

```
OmenMon.exe -Probe
```

It writes a single-snapshot Markdown dump (or two snapshots 5 s apart, with a delta table) without changing any state. Useful for triaging issues unrelated to fan readings.

---

## Code location

| File | Role |
|------|------|
| `App/Gui/GuiMenu.cs` | `EventActionAutoCalibrate()` — tray menu wiring |
| `App/Gui/GuiFormCalibration.cs` | Modal wizard dialog, FormClosing guard, SafeInvoke marshal |
| `App/Gui/GuiCalibrationProcessGuard.cs` | Best-effort priority drop for noisy background apps |
| `App/Cli/CliOpCalibration.cs` | `AutoCalibrate()` orchestrator + Markdown report builder |
| `Hardware/EcDiffScanner.cs` | Pattern A / B / C heuristic |
| `Library/AutoCal.cs` | Live override state + sidecar `Load()` / persistence + known-board `Prime()` |
| `Hardware/Fan.cs` | `GetSpeed()` consults `AutoCal` first, falls back to preset |
| `Hardware/Platform.cs` | `InitFans()` calls `AutoCal.Load()` then `AutoCal.Prime(product)` |
