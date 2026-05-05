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
            public int Pid;
            public ProcessPriorityClass PriorityClass;
        }

        private static readonly List<Saved> Suspended = new List<Saved>();

        public static void PauseHeavyHitters(Action<string> log) {
            Suspended.Clear();
            foreach(var name in HeavyHitterNames) {
                Process[] hits;
                try { hits = Process.GetProcessesByName(name); }
                catch { continue; }

                foreach(var p in hits) {
                    try {
                        Suspended.Add(new Saved { Pid = p.Id, PriorityClass = p.PriorityClass });
                        p.PriorityClass = ProcessPriorityClass.Idle;
                        log?.Invoke($"Throttled background process: {name} (pid {p.Id})");
                    } catch {
                        // Permission denied is fine — we move on.
                    }
                }
            }
        }

        public static void ResumeHeavyHitters() {
            foreach(var s in Suspended) {
                try {
                    using(var p = Process.GetProcessById(s.Pid))
                        p.PriorityClass = s.PriorityClass;
                } catch { }
            }
            Suspended.Clear();
        }

    }
}
