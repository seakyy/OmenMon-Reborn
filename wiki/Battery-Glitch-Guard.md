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
  <Setting name="BatteryGlitchGuard">true</Setting>
  <Setting name="BatteryGlitchDropPercent">30</Setting>
  <Setting name="BatteryGlitchWindowMs">5000</Setting>
  <Setting name="BatteryGlitchHoldMs">30000</Setting>
  <Setting name="BatteryGlitchGuardOnBattery">false</Setting>
  <Setting name="BatteryGlitchGuardDisableTimeout">false</Setting>
  <Setting name="BatteryGlitchGuardHoldAlways">false</Setting>
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

## Related

- [#59](https://github.com/seakyy/OmenMon-Reborn/issues/59) — Original
  report of the battery glitch on `8C30`.
- [#49](https://github.com/seakyy/OmenMon-Reborn/issues/49) — Related
  EC-contention symptom (fan spikes from torn temperature reads). The
  guard does not address this; see the issue thread for environmental
  mitigations.
- [`Library/PowerGuard.cs`](../Library/PowerGuard.cs) — Implementation.
- [`Library/ConfigData.cs`](../Library/ConfigData.cs) — The four
  `BatteryGlitch*` defaults.
