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

        // Backup of the baseline percent prior to the glitch.
        // Set to -1 when no glitch is active.
        private static int   priorPercent = -1;

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
            priorPercent = -1;
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

            bool isGlitch = false;

            // Glitch detection state machine.
            // If we are already in an active glitch state (priorPercent != -1),
            // we track relative to that saved baseline.
            if(priorPercent != -1) {
                int dropFromPrior = priorPercent - percent;
                bool hasRebounded = dropFromPrior < Config.BatteryGlitchDropPercent;
                // Safety timeout of 60 seconds. If the drop is real (e.g. charging failed),
                // we must eventually release the guard to allow Windows to hibernate.
                bool hasTimedOut = (now - lastTickTime).TotalSeconds > 60;

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
                int drop = lastPercent - percent;
                isGlitch =
                    power.PowerLineStatus == PowerLineStatus.Online
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
            return guardUntil != DateTime.MinValue && DateTime.UtcNow < guardUntil;
        }
#endregion

#region Internals
        private static void AssertGuard(AppGui.GuiTray tray, int priorPct, int glitchPct) {
            // SetThreadExecutionState returns the previous ExecutionState on
            // success, or 0 on failure.
            // BUGFIX (v1.4.1-reborn-fix): Removed the check for (result == None).
            // On the very first call, the previous state of the thread is None (0),
            // which caused the method to return early without setting guardUntil,
            // while the OS actually held the state. This caused a permanent leak
            // of the wake lock. Now we call and immediately proceed to establish
            // guardUntil.
            try {
                Kernel32.SetThreadExecutionState(
                    Kernel32.ExecutionState.Continuous | Kernel32.ExecutionState.SystemRequired);
            } catch {
                return;
            }

            // Extend (or start) the hold window. now + hold is the absolute UTC
            // time at which the next Tick() will release.
            guardUntil = DateTime.UtcNow.AddMilliseconds(Config.BatteryGlitchHoldMs);

            // User-facing balloon tip. The Tick() gate (IsGuardActive) ensures
            // AssertGuard fires at most once per hold window, so a sustained
            // glitch event produces a single tip, not one per second.
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
