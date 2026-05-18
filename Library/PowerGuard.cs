  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
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

        // When != MinValue, ES_SYSTEM_REQUIRED is being held and we should
        // release it at this time (or extend it on the next glitch).
        private static DateTime guardUntil = DateTime.MinValue;

        // First-tick guard. The very first tick has no prior reading to compare
        // against; we record the baseline and bail without firing the detector.
        private static bool initialized = false;
#endregion

#region Public API
        // Initialize. Resets any cached state so a re-entry (e.g. after sleep/resume
        // cycles or test code) doesn't carry forward a stale "lastPercent" that
        // would make the very next tick look like a giant drop.
        public static void Initialize() {
            lastPercent  = -1;
            lastTickTime = DateTime.MinValue;
            guardUntil   = DateTime.MinValue;
            initialized  = false;
        }

        // Called from GuiTray's 1-second timer. Cheap — three field reads
        // and a SystemInformation.PowerStatus lookup; no EC traffic, no WMI.
        // The optional `tray` reference is used to surface a balloon tip when
        // we trip; passing null suppresses the UI (useful for tests).
        public static void Tick(AppGui.GuiTray tray) {
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

            // Glitch detection. All four conditions must hold:
            //   1. AC connected (battery state on battery is allowed to drop).
            //   2. Previous reading was reasonably charged (> drop threshold),
            //      otherwise "100% → 2%" would also match "5% → 2%" which is
            //      a legitimate normal drain.
            //   3. Drop exceeds the configured threshold.
            //   4. Drop happened within the configured time window.
            int drop = lastPercent - percent;
            bool isGlitch =
                power.PowerLineStatus == PowerLineStatus.Online
                && lastPercent       >= Config.BatteryGlitchDropPercent
                && drop              >= Config.BatteryGlitchDropPercent
                && (now - lastTickTime).TotalMilliseconds <= Config.BatteryGlitchWindowMs;

            if(isGlitch) {
                AssertGuard(tray, lastPercent, percent);
                // Deliberately do NOT update lastPercent to the glitched value —
                // otherwise the next tick (when the reading rebounds to normal)
                // would see a giant *increase* and we'd start tracking from a
                // bogus baseline. Keep the pre-glitch baseline until the reading
                // stabilises (logical "trust the higher of the two").
            } else {
                lastPercent  = percent;
                lastTickTime = now;
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
            return guardUntil != DateTime.MinValue && DateTime.UtcNow < guardUntil;
        }
#endregion

#region Internals
        private static void AssertGuard(AppGui.GuiTray tray, int priorPct, int glitchPct) {
            // Swallow any P/Invoke failure — some locked-down environments deny
            // power-policy calls, and there's nothing we can do about it from
            // user-mode anyway. The held ExecutionState is visible via
            // `powercfg /requests` for support diagnostics if needed.
            try {
                Kernel32.SetThreadExecutionState(
                    Kernel32.ExecutionState.Continuous | Kernel32.ExecutionState.SystemRequired);
            } catch { }

            // Extend (or start) the hold window. now + hold is the absolute UTC
            // time at which the next Tick() will release.
            guardUntil = DateTime.UtcNow.AddMilliseconds(Config.BatteryGlitchHoldMs);

            // User-facing balloon tip. AssertGuard only fires on detected
            // glitches (not on each tick during the hold window), so this
            // doesn't spam — at most one tip per glitch event.
            if(tray != null) {
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
