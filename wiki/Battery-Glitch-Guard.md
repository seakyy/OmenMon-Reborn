# Battery-Glitch Hibernation Guard

> Added in **v1.4.1-reborn**. Tracking issue: [#59](https://github.com/seakyy/OmenMon-Reborn/issues/59).

## What the guard does

Some HP Victus / Omen SKUs (notably `8C30` and the boards in the same
firmware cohort) occasionally report a wildly wrong battery percentage to
Windows under heavy load — the most common symptom is the battery
indicator briefly dropping from ~100 % to `<5 %` even though the laptop is
plugged in. When Windows' **Critical battery action** is set to
**Hibernate** (the factory default on AC), that single bad reading is
enough to put the machine to sleep mid-game, mid-VR session, mid-screen-share.

OmenMon-Reborn ships a small defensive guard that watches the OS-reported
battery percentage on the tray's 1 s timer tick. When it sees a drop big
enough and fast enough to be a torn read **while the laptop is on AC**, it
tells Windows to skip the Critical Battery Action for a short hold window
via the standard [`SetThreadExecutionState`][stes] API with
`ES_CONTINUOUS | ES_SYSTEM_REQUIRED`. A balloon tip appears in the tray
showing the before / after percentages so you know it fired.

[stes]: https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-setthreadexecutionstate

The guard is **suppressed on battery power** — a real low-battery state
must still hibernate normally; overriding that would risk you losing
unsaved work to a flat battery.

## How it decides "this is a glitch, not a real drain"

All four conditions must hold simultaneously:

| Condition | Why |
|-----------|-----|
| `PowerLineStatus == Online` (on AC) | Real drains on battery are allowed to hibernate |
| Previous reading was at or above `BatteryGlitchDropPercent` | Avoids matching "5 % → 2 %", which is a normal drain you'd want to act on |
| Drop ≥ `BatteryGlitchDropPercent` (default **30 %**) | A real laptop battery doesn't drain 30 % in 5 seconds |
| Drop happened within `BatteryGlitchWindowMs` (default **5 s**) | A slow drift even on a struggling charger never matches |

If all four match, the guard:

1. Calls `SetThreadExecutionState(Continuous | SystemRequired)` — Windows
   stops considering idle-sleep / critical-battery hibernate for the
   thread that called it.
2. Shows the tray balloon tip.
3. Sets a hold-until timestamp (`now + BatteryGlitchHoldMs`, default 30 s).
4. Keeps `lastPercent` at the **pre-glitch** value, not the glitched one —
   otherwise the next tick would see a giant *increase* when the reading
   rebounds and start tracking from a bogus baseline.

After the hold window expires (or you turn the feature off), the guard
calls `SetThreadExecutionState(Continuous)` alone to release.

## Default configuration

```xml
<!-- OmenMon.xml -->
<Config>
  <BatteryGlitchGuard>true</BatteryGlitchGuard>
  <BatteryGlitchDropPercent>30</BatteryGlitchDropPercent>
  <BatteryGlitchWindowMs>5000</BatteryGlitchWindowMs>
  <BatteryGlitchHoldMs>30000</BatteryGlitchHoldMs>
  <BatteryGlitchGuardOnBattery>false</BatteryGlitchGuardOnBattery>
  <BatteryGlitchGuardDisableTimeout>false</BatteryGlitchGuardDisableTimeout>
  <BatteryGlitchGuardHoldAlways>false</BatteryGlitchGuardHoldAlways>
</Config>
```

| Key | Default | Meaning |
|-----|--------:|---------|
| `BatteryGlitchGuard` | `true` | Master switch. `false` reverts to the OS's verbatim hibernate behaviour. |
| `BatteryGlitchDropPercent` | `30` | Minimum percentage drop to flag as a glitch. Raise to reduce false positives. |
| `BatteryGlitchWindowMs` | `5000` | The drop must happen within this many milliseconds (consecutive ticks). |
| `BatteryGlitchHoldMs` | `30000` | How long Windows is told to skip sleep/hibernate after a detection. |
| `BatteryGlitchGuardOnBattery` | `false` | Run glitch guard on battery power (by default, the guard is only active on AC). |
| `BatteryGlitchGuardDisableTimeout` | `false` | Disable the 60-second safety timeout for sustained glitches (allows infinite extension during prolonged glitches). |
| `BatteryGlitchGuardHoldAlways` | `false` | Permanently assert `ES_SYSTEM_REQUIRED` (wake-lock) while OmenMon is running to block sleep/hibernation completely. |

## When you might want to tune it

### Symptom: you see the balloon tip but you're sure your battery really did drop

→ Raise `BatteryGlitchDropPercent` to e.g. `40`. The guard will only fire
on bigger drops. Note this is mutually exclusive with the situation where
your laptop drained 30 % in 5 seconds on AC — that doesn't physically
happen with modern Li-Ion cells, so the default is conservative by design.

### Symptom: you don't see the balloon tip but you still get unexpected hibernates

Could be one of:

1. **You're on battery, not AC.** The guard deliberately doesn't engage
   on battery. Plug in and re-test.
2. **The drop is below the threshold.** Lower
   `BatteryGlitchDropPercent` to e.g. `20`. The window is per-tick, so
   smaller drops would only match if they happen very fast.
3. **The hibernate isn't a critical-battery trigger.** Windows can
   hibernate for other reasons (idle timeout, lid switch, manual). The
   guard only covers the critical-battery path. Check
   `powercfg /requests` while OmenMon is running — under "SYSTEM" you
   should see `[PROCESS] OmenMon.exe` listed if the guard is currently
   active.

### You'd rather see Windows' raw behaviour

→ Set `BatteryGlitchGuard` to `false`. The guard will release any
currently-held state on the next tick and stop polling.

### Symptom: balloon tip is too noisy when the glitch repeats during a long load

Each detection only shows one tip — the guard doesn't re-fire while a
hold window is active. So if you see multiple tips, you're seeing
multiple distinct glitch events, not chatter. If that's still too much,
set `Config.GuiTipDuration` to `0` to silence all tray tips globally
(this also silences the other balloon tips OmenMon shows, so consider
whether that's what you want).

## Verifying the guard is active

Open an **admin** PowerShell / cmd and run:

```powershell
powercfg /requests
```

While the guard is asserting, you should see something like:

```
SYSTEM:
[PROCESS] \Device\HarddiskVolumeN\Users\...\OmenMon.exe
```

When the hold window expires the entry disappears. If you never see it
even though you got a balloon tip, your environment may be denying
power-policy calls — the guard catches and ignores P/Invoke failures.
File an issue with `OmenMon.exe -Diag` output if you hit that.

## What this guard does **not** do

- **It does not "fix" the underlying EC torn-read.** That's a firmware
  issue between the Windows battery driver and the HP Smart Battery
  System path. OmenMon doesn't read or write the battery register
  itself (`XBCH` at `0x96` is defined as an enum constant only and is
  never accessed in code) — it only watches `SystemInformation.PowerStatus`
  for the symptom and acts on the OS side.
- **It does not prevent legitimate hibernate.** If your battery is
  actually low while on battery power, Windows will still hibernate. If
  you press the power button to manually sleep, Windows will still sleep.
- **It does not survive an OmenMon crash.** The held `ES_SYSTEM_REQUIRED`
  flag is released by Windows automatically when the OmenMon process
  exits — there's no permanent change to your power configuration. If
  OmenMon is force-killed mid-glitch, the worst case is up to
  `BatteryGlitchHoldMs` of extra "no hibernate" after the kill — at the
  defaults, 30 seconds.

## If the guard misbehaves

The guard wraps every API call in a try / catch and swallows failures
silently — there's no scenario where the guard itself can crash OmenMon.
But if you're seeing unexpected behaviour and want to debug:

1. **Capture `-Diag` output during the symptom**:
   ```cmd
   OmenMon.exe -Diag
   ```
   Attach the resulting `OmenMon-Diag-*.md` to your issue.
2. **Capture `powercfg /requests` output** in the same admin shell
   right after a glitch fires.
3. **Note the timing**: was AC plugged in? Were you actively gaming?
   What did the balloon tip say (before / after percentages)?

File issues at <https://github.com/seakyy/OmenMon-Reborn/issues>.

---

# Companion guard: AC-flicker debounce

> Added in **v1.4.2-reborn**. Tracking issue: [#70](https://github.com/seakyy/OmenMon-Reborn/issues/70).

The battery-glitch guard above watches the OS-reported *battery
percentage* and only fires on big, fast drops. A separate symptom on
some HP Omen / Victus SKUs is the OS reporting `PowerLineStatus` as
`Offline` for a few seconds even though the laptop is physically
plugged in — visible in `powercfg /batteryreport` as transient AC-out
events that no human action caused. Before v1.4.2 OmenMon reacted to
each of these events immediately: with `AutoConfig=true` and a fan
program active, it switched from `FanProgramDefault` (Power) to
`FanProgramDefaultAlt` (typically Silent, which caps the system to Base
Power), producing visible CPU/power-throttling stutter mid-game. When AC
came back ~2 s later it switched back.

The AC-flicker guard defers the reaction. On a `PowerModeChanged`
StatusChange event, OmenMon records the timestamp and waits
`AcFlickerHoldMs` (default **8 s**) before running the actual fan-program
switch and BIOS-heartbeat toggle. If the line status has reverted by
then — i.e. `IsFullPower()` matches the value before the event — nothing
happens. Legitimate unplug-stay-unplugged actions still take effect,
just delayed by the hold window.

## Defaults

```xml
<!-- OmenMon.xml -->
<Config>
  <AcFlickerGuard>true</AcFlickerGuard>
  <AcFlickerHoldMs>8000</AcFlickerHoldMs>
</Config>
```

| Key | Default | Meaning |
|-----|--------:|---------|
| `AcFlickerGuard` | `true` | Master switch. `false` reverts to the immediate-switch behaviour from earlier builds. |
| `AcFlickerHoldMs` | `8000` | Milliseconds the new line-status must hold before OmenMon applies the corresponding fan-program / heartbeat change. Range `0..60000`. `0` is equivalent to `AcFlickerGuard=false`. |

## Tuning

- **Your real unplug-to-Silent transition feels too laggy**: lower
  `AcFlickerHoldMs` to e.g. `4000`. Anything shorter risks catching the
  tail of a 2–5 s flicker.
- **You still see mid-game stutters at the default hold**: raise to
  `12000` and re-test. If the stutter persists, capture `-Diag` during
  one — the EC trace will show whether a fan-program switch actually
  fired or whether something else is throttling.
- **You want immediate behaviour back**: set `AcFlickerGuard=false`.

## What this guard does **not** do

- It does not stop the OS-level flicker — that's a firmware / power-stack
  issue between the EC, the ACPI battery driver, and Windows. OmenMon
  only changes how OmenMon reacts to it.
- It does not interact with the percent-based guard above. The two run
  side by side: the AC-flicker guard handles `PowerLineStatus`
  transitions, the battery-glitch guard handles `BatteryLifePercent`
  drops.

## Related

- [#70](https://github.com/seakyy/OmenMon-Reborn/issues/70) — Original
  report on a Victus 16 SKU during gaming.
- [#59](https://github.com/seakyy/OmenMon-Reborn/issues/59) — Original
  report of the percent-based battery glitch on `8C30`.
- [#49](https://github.com/seakyy/OmenMon-Reborn/issues/49) — Related
  EC-contention symptom (fan spikes from torn temperature reads).
  Neither guard addresses this; see the issue thread for environmental
  mitigations.
- [`App/Gui/GuiTray.cs`](../App/Gui/GuiTray.cs) — `EventPowerChange` and
  `EventTimerTick` host the AC-flicker debounce.
- [`Library/PowerGuard.cs`](../Library/PowerGuard.cs) — Percent-based
  guard implementation.
- [`Library/ConfigData.cs`](../Library/ConfigData.cs) — `AcFlicker*`
  and `BatteryGlitch*` defaults.
