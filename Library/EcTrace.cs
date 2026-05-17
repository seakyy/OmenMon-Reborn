  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

// Lock-free circular buffer of recent Embedded Controller reads and writes.
// Used by the crash dumper (Crash.cs) and the -Diag CLI verb so that a user's
// bug report includes a few minutes of EC activity preceding the failure —
// invaluable for debugging intermittent issues like #49 (random fan spikes),
// where the symptom is invisible by the time the user opens GitHub.
//
// Threading: a single Interlocked.Increment picks each slot. Readers (the
// crash dumper / Snapshot()) iterate a copy; they may observe a slot mid-
// write but the resulting record is still well-formed because each field is
// either int / byte / long — all atomic on x64. No locks on the hot path.

using System;
using System.Text;
using System.Threading;

namespace OmenMon.Library {

    public static class EcTrace {

        // Capacity sized to capture ~5 minutes of activity at typical refresh
        // rates (1-3 EC ops per second × 60 s × 5 min ≈ 900 ops, rounded up
        // to a power of two for cheap masking). Memory cost is trivial:
        // 1024 × 16 bytes ≈ 16 KiB resident.
        private const int Capacity = 1024;
        private const int Mask = Capacity - 1;

        public enum Op : byte { Read, Write }

        // Single record. Kept compact so the buffer stays cache-friendly.
        public struct Entry {
            public long TicksUtc;   // DateTime.UtcNow.Ticks at write time (0 = empty slot)
            public byte Register;
            public byte Value;
            public Op   Kind;
            public byte Reserved;   // padding, future use
        }

        private static readonly Entry[] buffer = new Entry[Capacity];
        private static int next;    // monotonically increasing slot index

        // Master switch — kept on by default. The crash dumper sets it off
        // before dumping so iteration sees a stable view; -Diag does the
        // same. Re-enabled afterwards.
        private static volatile bool enabled = true;

        public static bool IsEnabled => enabled;
        public static void SetEnabled(bool value) { enabled = value; }

        public static void RecordRead(byte register, byte value) {
            if(!enabled) return;
            int slot = Interlocked.Increment(ref next) & Mask;
            buffer[slot].TicksUtc = DateTime.UtcNow.Ticks;
            buffer[slot].Register = register;
            buffer[slot].Value    = value;
            buffer[slot].Kind     = Op.Read;
        }

        public static void RecordWrite(byte register, byte value) {
            if(!enabled) return;
            int slot = Interlocked.Increment(ref next) & Mask;
            buffer[slot].TicksUtc = DateTime.UtcNow.Ticks;
            buffer[slot].Register = register;
            buffer[slot].Value    = value;
            buffer[slot].Kind     = Op.Write;
        }

        // Returns the entries in chronological order (oldest first). Empty
        // slots (never written) are skipped, so a freshly-started session
        // doesn't pad the dump with placeholder rows.
        public static Entry[] Snapshot() {
            // Capture the current head once so the snapshot is bounded by a
            // single read-modify-write moment, even if more writes happen
            // while we're copying.
            int head = Volatile.Read(ref next);
            int start = head + 1;       // oldest slot is one past the head
            var copy = new Entry[Capacity];
            int written = 0;
            for(int i = 0; i < Capacity; i++) {
                Entry e = buffer[(start + i) & Mask];
                if(e.TicksUtc != 0) copy[written++] = e;
            }
            if(written == Capacity) return copy;
            // Shrink the result so callers don't see trailing empty slots.
            var trimmed = new Entry[written];
            Array.Copy(copy, trimmed, written);
            return trimmed;
        }

        // Markdown-ready dump. Designed to be embedded in a bug report —
        // compact (one line per op), monotonic timestamps relative to the
        // newest entry so the reader can reason about elapsed time without
        // squinting at full ISO dates.
        public static string FormatMarkdown(int maxEntries = 256) {
            Entry[] entries = Snapshot();
            if(entries.Length == 0)
                return "_(EC trace is empty — no activity recorded.)_";

            int from = Math.Max(0, entries.Length - maxEntries);
            long newestTicks = entries[entries.Length - 1].TicksUtc;

            var sb = new StringBuilder();
            sb.AppendLine("```");
            sb.AppendLine("  Δms  Op    Reg   Val");
            for(int i = from; i < entries.Length; i++) {
                Entry e = entries[i];
                long deltaTicks = newestTicks - e.TicksUtc;
                long deltaMs = deltaTicks / TimeSpan.TicksPerMillisecond;
                string deltaStr = deltaMs == 0 ? "    0" : "-" + deltaMs.ToString().PadLeft(4, ' ');
                sb.Append(deltaStr).Append("  ");
                sb.Append(e.Kind == Op.Read ? "R " : "W ").Append("   ");
                sb.Append("0x").Append(e.Register.ToString("X2")).Append("  ");
                sb.Append("0x").Append(e.Value.ToString("X2"));
                sb.AppendLine();
            }
            sb.AppendLine("```");
            sb.Append($"_(showing the last {entries.Length - from} of {entries.Length} recorded operations; Δms relative to newest.)_");
            return sb.ToString();
        }

    }

}
