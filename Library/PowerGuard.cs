  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using OmenMon.External;

namespace OmenMon.Library {

    // Detects transient Windows-battery-percentage drops while plugged in — the
    // signature of a torn read from the Smart Battery System path that affected
    // users have reported as a sudden ~100% → ~2% jump under heavy load — and
    // tells Windows to skip the Critical Battery Action (Hibernate / Sleep)
    // for a short hold window. Targets issue #59 and the symptom cluster
    // referenced in #56's comments.
    //
    // The mechanism is SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED).
    // Once asserted, Windows will not initiate a sleep / hibernate transition
    // due to idleness or critical-battery action until either (a) we call
    // SetThreadExecutionState(ES_CONTINUOUS) to release, or (b) the OmenMon
    // process exits. The flag is per-thread; we always call from the GUI tray
    // timer tick (single thread) so the calls are consistent.
    //
    // Behaviour boundaries:
    //   - Only fires when SystemInformation.PowerStatus.PowerLineStatus == Online.
    //     On battery, a real low-battery state must still hibernate normally —
    //     overriding that would risk the user losing unsaved work to a flat
    //     battery, which is strictly worse than the original glitch.
    //   - Requires a *fast* drop (>= BatteryGlitchDropPercent within
    //     BatteryGlitchWindowMs). A slow drain even on AC (~3-5%/min during
    //     heavy gaming with the charger barely keeping up) will never match.
    //   - Hold window is capped at BatteryGlitchHoldMs so we never silently
    //     hold ES_SYSTEM_REQUIRED indefinitely. If glitches keep firing, each
    //     one extends the hold by another window — the flag itself is
    //     idempotent (re-asserting it is harmless).
    //
    // No public state is exposed beyond IsGuardActive() so the GUI/diagnostics
    // can show whether the guard is currently asserting.
    public static class PowerGuard {

#region Internal State
        // The most recent battery reading we used for delta comparison.
        private static int   lastPercent = -1;
        private static DateTime lastTickTime = DateTime.MinValue;

        // Backup of the baseline percent prior to the glitch.
        // Set to -1 when no glitch is active.
        private static int   priorPercent = -1;

        // When != MinValue, ES_SYSTEM_REQUIRED is being held and we should
        // release it at this time (or extend it on the next glitch).
        private static DateTime guardUntil = DateTime.MinValue;

        // First-tick guard. The very first tick has no prior reading to compare
        // against; we record the baseline and bail without firing the detector.
        private static bool initialized = false;

        // First UTC tick at which PowerLineStatus was observed Offline while a
        // glitch hold was being maintained. The AC-offline release path waits
        // for this to exceed Config.AcFlickerHoldMs before tearing down the
        // wake-lock, so a coinciding AC-flicker (issue #70) does not silently
        // defeat the percent-glitch guard (issue #59) the moment they overlap.
        // MinValue means we have not yet seen a sustained AC-offline event.
        private static DateTime acOfflineSinceUtc = DateTime.MinValue;
#endregion

#region Public API
        // Initialize. Resets any cached state so a re-entry (e.g. after sleep/resume
        // cycles or test code) doesn't carry forward a stale "lastPercent" that
        // would make the very next tick look like a giant drop.
        public static void Initialize() {
            lastPercent  = -1;
            lastTickTime = DateTime.MinValue;
            guardUntil   = DateTime.MinValue;
            priorPercent = -1;
            initialized  = false;
            acOfflineSinceUtc = DateTime.MinValue;
        }

        // Called from GuiTray's 1-second timer. Cheap — three field reads
        // and a SystemInformation.PowerStatus lookup; no EC traffic, no WMI.
        // The optional `tray` reference is used to surface a balloon tip when
        // we trip; passing null suppresses the UI (useful for tests).
        public static void Tick(OmenMon.AppGui.GuiTray tray) {

            // Permanent-hold mode is an independent "block sleep/hibernate while
            // OmenMon is running" switch — it does NOT require the percent-glitch
            // guard (BatteryGlitchGuard) to be enabled, so it is evaluated first.
            // Setting HoldAlways=true with BatteryGlitchGuard=false previously did
            // nothing because the !BatteryGlitchGuard early-return ran first
            // (Copilot re-review on the v1.4.2 PR). Only latch guardUntil to
            // MaxValue once AssertGuard confirms the SetThreadExecutionState call
            // actually landed, so a P/Invoke failure can't leave our bookkeeping
            // ahead of the OS state (Copilot review #3 on the v1.4.2 PR).
            if(Config.BatteryGlitchGuardHoldAlways) {
                if(guardUntil != DateTime.MaxValue && AssertGuard(tray, -1, -1))
                    guardUntil = DateTime.MaxValue;
                return;
            }

            // HoldAlways was previously active but was just turned off — release the
            // permanent latch immediately (whether or not BatteryGlitchGuard is on).
            if(guardUntil == DateTime.MaxValue)
                ReleaseGuardIfHeld();

            if(!Config.BatteryGlitchGuard) {
                // Feature disabled — make sure we never hold ES_SYSTEM_REQUIRED.
                ReleaseGuardIfHeld();
                return;
            }

            PowerStatus power;
            try { power = SystemInformation.PowerStatus; }
            catch { return; }  // very rare, but never let a status read throw

            DateTime now = DateTime.UtcNow;

            // BatteryLifePercent returns a float 0.0..1.0; convert once to int
            // percent so the delta arithmetic and config comparisons share units.
            int percent = (int) Math.Round(power.BatteryLifePercent * 100f);

            // Bookkeeping for the first tick: just record baseline and return.
            if(!initialized || lastPercent < 0) {
                lastPercent  = percent;
                lastTickTime = now;
                initialized  = true;
                return;
            }

            // Release a previously-held guard once the hold window expires.
            // Done before glitch detection so a glitch during the hold window
            // properly extends the timer rather than fighting the release.
            if(guardUntil != DateTime.MinValue && now >= guardUntil)
                ReleaseGuardIfHeld();

            bool isGlitch = false;

            // Hard invariant: the guard is AC-only. If AC is unplugged at any point —
            // including mid-glitch — release the held wake-lock so a genuine critical-
            // battery transition can hibernate. Overridden if Config.BatteryGlitchGuardOnBattery
            // is true. Pre-v1.4.2 this ran the moment PowerLineStatus flipped to Offline,
            // which silently defeated the percent-glitch guard whenever an AC-flicker
            // coincided with a percent torn-read (the same SKUs that hit one symptom
            // hit the other — issues #59 / #70). Now we compute an "effectively on AC"
            // signal that overrides PowerLineStatus during the AC-flicker hold window
            // and on multi-source disagreement, so a brief flicker no longer tears
            // down the wake-lock and the glitch state machine keeps running normally.
            bool effectivelyOnAc = power.PowerLineStatus == PowerLineStatus.Online;
            if(!effectivelyOnAc && !Config.BatteryGlitchGuardOnBattery) {

                // First Offline reading after a stretch of Online — start the timer.
                if(acOfflineSinceUtc == DateTime.MinValue)
                    acOfflineSinceUtc = now;

                // While the AC-flicker guard is enabled and within its hold window,
                // suspend the AC-only invariant. Multi-source IsFullPowerConfirmed
                // gives the firmware (smart adapter) and the charging-flag a chance
                // to override a misbehaving PowerLineStatus.
                bool flickerHoldActive = Config.AcFlickerGuard
                    && Config.AcFlickerHoldMs > 0
                    && (now - acOfflineSinceUtc).TotalMilliseconds < Config.AcFlickerHoldMs;

                // Only consult the firmware/charging cross-check while it can still
                // change the outcome — i.e. outside the hold window (inside it,
                // flickerHoldActive already forces effectivelyOnAc=true below) AND
                // while the guard is actually engaged (holding the wake-lock, or
                // tracking a glitch). On a plain sustained unplug priorPercent is -1
                // and the guard is inactive, so we trust PowerLineStatus and never
                // poll the BIOS adapter-status query once per second for the entire
                // battery session (Copilot review #2 on the v1.4.2 PR).
                bool stillOnAcByOtherSources = false;
                if(!flickerHoldActive
                    && (priorPercent != -1 || IsGuardActive())
                    && tray != null && tray.Op != null && tray.Op.Platform != null) {
                    try { stillOnAcByOtherSources = tray.Op.Platform.System.IsFullPowerConfirmed(); }
                    catch { }
                }

                if(flickerHoldActive || stillOnAcByOtherSources) {
                    // Treat as if still on AC — fall through to glitch detection so a
                    // percent torn-read coinciding with the flicker is still caught.
                    effectivelyOnAc = true;
                } else {
                    // Sustained AC-offline (or guard disabled) — release and bail.
                    if(priorPercent != -1 || IsGuardActive()) {
                        ReleaseGuardIfHeld();
                        priorPercent = -1;
                    }
                    lastPercent  = percent;
                    lastTickTime = now;
                    return;
                }
            } else if(power.PowerLineStatus == PowerLineStatus.Online) {
                // AC reported back — clear the AC-offline timer so the next dip starts
                // fresh. (Note: when BatteryGlitchGuardOnBattery is true and we're on
                // battery, leaving acOfflineSinceUtc as-is is harmless because the
                // flicker-hold branch above is gated on !BatteryGlitchGuardOnBattery.)
                acOfflineSinceUtc = DateTime.MinValue;
            }

            // Glitch detection state machine.
            // If we are already in an active glitch state (priorPercent != -1),
            // we track relative to that saved baseline.
            if(priorPercent != -1) {
                int dropFromPrior = priorPercent - percent;
                bool hasRebounded = dropFromPrior < Config.BatteryGlitchDropPercent;
                // Safety timeout of 60 seconds. If the drop is real (e.g. charging failed),
                // we must eventually release the guard to allow Windows to hibernate.
                // Overridden if Config.BatteryGlitchGuardDisableTimeout is true.
                bool hasTimedOut = !Config.BatteryGlitchGuardDisableTimeout && (now - lastTickTime).TotalSeconds > 60;

                if(hasRebounded || hasTimedOut) {
                    // Reset to normal operation.
                    priorPercent = -1;
                    lastPercent = percent;
                    lastTickTime = now;
                    isGlitch = false;
                } else {
                    // Glitch is sustained. Keep the guard active.
                    isGlitch = true;
                    // Freeze lastPercent at the priorPercent baseline so we don't
                    // overwrite our reference with glitched readings.
                    lastPercent = priorPercent;
                }
            } else {
                // Normal mode: check if a new drop matches glitch criteria.
                // effectivelyOnAc folds the AC-flicker / multi-source overrides into
                // the existing "AC-only" gate so a torn percent read during a flicker
                // is still flagged as a glitch.
                int drop = lastPercent - percent;
                isGlitch =
                    (Config.BatteryGlitchGuardOnBattery || effectivelyOnAc)
                    && lastPercent       >= Config.BatteryGlitchDropPercent
                    && drop              >= Config.BatteryGlitchDropPercent
                    && (now - lastTickTime).TotalMilliseconds <= Config.BatteryGlitchWindowMs;

                if(isGlitch) {
                    // Back up the healthy pre-glitch baseline.
                    priorPercent = lastPercent;
                }
            }

            if(isGlitch) {
                if(!IsGuardActive()) {
                    // Call the API to assert the sleep/hibernate blocker.
                    AssertGuard(tray, lastPercent, percent);
                } else {
                    // Sustained or repeating glitch: continuously extend the guard
                    // to prevent it from expiring while the reading is still glitched.
                    guardUntil = now.AddMilliseconds(Config.BatteryGlitchHoldMs);
                }
            } else {
                // If not glitched, track normally.
                if(priorPercent == -1) {
                    lastPercent  = percent;
                    lastTickTime = now;
                }
            }
        }

        // Release any held guard. Safe to call repeatedly. The GUI calls this
        // from its disposal path so OmenMon's exit doesn't leave Windows
        // holding ES_SYSTEM_REQUIRED — the OS releases it on process exit
        // anyway, but being explicit is cheap and survives weird shutdown paths.
        public static void Dispose() {
            ReleaseGuardIfHeld();
        }

        public static bool IsGuardActive() {
            // Reflects the real wake-lock state, not config intent. HoldAlways
            // latches guardUntil to MaxValue only after AssertGuard confirms
            // success, and the transient-glitch path sets a finite future
            // guardUntil. Keying off guardUntil (rather than the config flags)
            // means a failed SetThreadExecutionState is never reported as an
            // active guard (Copilot review #2 on the v1.4.2 PR).
            if(guardUntil == DateTime.MaxValue)
                return true;
            return guardUntil != DateTime.MinValue && DateTime.UtcNow < guardUntil;
        }
#endregion

#region Internals
        // Asserts the ES_CONTINUOUS | ES_SYSTEM_REQUIRED wake-lock.
        // Returns true if the SetThreadExecutionState call succeeded, false on
        // P/Invoke failure. Callers must use the return value to decide whether
        // to advance internal state — without that check the transient-glitch
        // hold window or the HoldAlways permanent-hold latch could record state
        // that diverges from what the OS is actually enforcing.
        //
        // Failure detection follows the Win32 contract documented at
        // https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-setthreadexecutionstate:
        // the API returns the previous ExecutionState on success and 0 on
        // failure. A return value of 0 is ambiguous because the previous state
        // can legitimately be None (0) on the first call after process start.
        // We disambiguate via Marshal.GetLastWin32Error() — the P/Invoke is
        // declared SetLastError=true exactly so callers can do this. A non-zero
        // Win32 error means a real failure; a 0 error code with a 0 return
        // value means success with previous state None.
        //
        // Win32 APIs are not guaranteed to *clear* last-error on success, so we
        // must reset it to 0 immediately before the call (via Kernel32.SetLastError)
        // — otherwise a stale non-zero value left by an earlier P/Invoke would be
        // misread as a failure on the legitimate "previous state was None" path.
        // (Copilot review #2 on the second round of v1.4.2 PR review.)
        //
        // History: an earlier revision returned early on (result == None),
        // which collapsed the legitimate first-call case into "failure" and
        // permanently leaked the wake-lock (the OS accepted the request but
        // our bookkeeping skipped guardUntil). The cleared-then-checked Win32-error
        // path here restores correct success detection without that regression.
        // (Copilot review #1 on the second round of v1.4.2 PR review.)
        private static bool AssertGuard(OmenMon.AppGui.GuiTray tray, int priorPct, int glitchPct) {
            Kernel32.ExecutionState previous;
            try {
                // Clear last-error first so GetLastWin32Error() below reflects only
                // the SetThreadExecutionState call, not a stale value.
                Kernel32.SetLastError(0);
                previous = Kernel32.SetThreadExecutionState(
                    Kernel32.ExecutionState.Continuous | Kernel32.ExecutionState.SystemRequired);
            } catch {
                return false;
            }

            // 0 return is ambiguous — check the (freshly-cleared) Win32 error to
            // distinguish genuine failure from "previous state was None".
            if(previous == Kernel32.ExecutionState.None
                && Marshal.GetLastWin32Error() != 0)
                return false;

            // Extend (or start) the hold window. now + hold is the absolute UTC
            // time at which the next Tick() will release. HoldAlways callers
            // overwrite this with DateTime.MaxValue immediately after, but only
            // if we report success — so the latch can't get ahead of the OS state.
            guardUntil = DateTime.UtcNow.AddMilliseconds(Config.BatteryGlitchHoldMs);

            // User-facing balloon tip. The Tick() gate (IsGuardActive) ensures
            // AssertGuard fires at most once per hold window, so a sustained
            // glitch event produces a single tip, not one per second.
            if(tray != null && priorPct >= 0) {
                try {
                    tray.ShowBalloonTip(
                        "Detected an implausible battery reading (" + priorPct
                        + " % → " + glitchPct + " % while plugged in). "
                        + "Suppressing Windows hibernate for "
                        + (Config.BatteryGlitchHoldMs / 1000)
                        + " s to prevent unexpected shutdown.",
                        "OmenMon: battery glitch guard",
                        ToolTipIcon.Warning);
                } catch { }
            }

            return true;
        }

        private static void ReleaseGuardIfHeld() {
            if(guardUntil == DateTime.MinValue) return;
            try {
                // ES_CONTINUOUS alone (without SystemRequired) clears the
                // maintained state set in AssertGuard.
                Kernel32.SetThreadExecutionState(Kernel32.ExecutionState.Continuous);
            } catch { }
            guardUntil = DateTime.MinValue;
        }
#endregion

    }

}
