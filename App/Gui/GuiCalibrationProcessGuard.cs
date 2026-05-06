  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace OmenMon.AppGui {

    // Best-effort throttle for noisy background apps during the calibration run.
    //
    // We deliberately do NOT close the user's windows — losing unsaved state for the
    // sake of a 60-second sensor sweep is a bad trade. Instead we drop the process
    // priority of well-known CPU/GPU hogs while the test runs, then restore it.
    // The list is intentionally conservative: web browsers, IDEs, and discord-style
    // chat clients aren't on it because they're typically what the user is reading
    // these instructions in.
    internal static class ProcessGuard {

        private static readonly string[] HeavyHitterNames = {
            "obs64", "obs32",
            "Spotify",
            "vlc",
            "HandBrake",
            "ffmpeg", "x264", "x265",
            "Adobe Premiere Pro", "Adobe Media Encoder", "AfterFX",
            "Photoshop",
            "blender",
            "Unity", "UnrealEditor",
            "Code", "devenv", "rider64", "idea64",
            "ollama", "lm-studio",
            "MsMpEng",  // Defender — full scans cause obvious EC noise
        };

        private sealed class Saved {
            // Holding the Process instance preserves its native handle for the
            // duration of the suspend window. Without this, if the throttled
            // process exits during the ~1-minute sweep, Windows may recycle its
            // PID and ResumeHeavyHitters() would apply our saved priority to an
            // unrelated process. We additionally check StartTime on restore as
            // a belt-and-braces guard in case the OS recycles a handle too.
            public Process Process;
            public int Pid;
            public DateTime StartTime;
            public ProcessPriorityClass PriorityClass;
        }

        private static readonly List<Saved> Suspended = new List<Saved>();

        public static void PauseHeavyHitters(Action<string> log) {
            DisposeAndClear();
            foreach(var name in HeavyHitterNames) {
                Process[] hits;
                try { hits = Process.GetProcessesByName(name); }
                catch { continue; }

                foreach(var p in hits) {
                    bool kept = false;
                    try {
                        var entry = new Saved {
                            Process = p,
                            Pid = p.Id,
                            StartTime = p.StartTime,
                            PriorityClass = p.PriorityClass
                        };
                        p.PriorityClass = ProcessPriorityClass.Idle;
                        Suspended.Add(entry);
                        kept = true;
                        log?.Invoke($"Throttled background process: {name} (pid {p.Id})");
                    } catch {
                        // Permission denied is fine — we move on.
                    }
                    if(!kept) {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
        }

        public static void ResumeHeavyHitters() {
            foreach(var s in Suspended) {
                try {
                    // The held Process instance keeps the original handle alive,
                    // so we operate on the same OS-level process we throttled even
                    // if its PID has since been reused by another exe.
                    if(s.Process != null && !s.Process.HasExited
                        && s.Process.StartTime == s.StartTime) {
                        s.Process.PriorityClass = s.PriorityClass;
                    }
                } catch { }
            }
            DisposeAndClear();
        }

        private static void DisposeAndClear() {
            foreach(var s in Suspended) {
                try { s.Process?.Dispose(); } catch { }
            }
            Suspended.Clear();
        }

    }
}
