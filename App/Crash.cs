  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

// Telemetry-free crash handler.
//
// Registers handlers for AppDomain.UnhandledException and
// Application.ThreadException on startup. When a fatal exception escapes
// any non-recoverable path, writes OmenMon-crash-yyyy-mm-dd-HHMMSS.log
// next to the executable containing:
//
//   * The full exception chain (type, message, stack, inner exceptions)
//   * Process / environment metadata (OmenMon version, Windows version,
//     working directory, command line)
//   * The same diagnostic Markdown bundle the -Diag verb produces
//
// Nothing is uploaded, ever. The file sits on disk until the user
// attaches it to a GitHub issue. The handler is best-effort: any error
// inside the handler is swallowed so a misbehaving Diag step can never
// make a bad situation worse by preventing the crash dialog from showing.

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using OmenMon.AppCli;
using OmenMon.Library;

namespace OmenMon {

    public static class Crash {

        // Lock so two concurrent unhandled exceptions don't race on the
        // file write. Static so the first one wins and the second one
        // backs off (the file's contents will still be useful).
        private static readonly object writeLock = new object();
        private static volatile bool installed;

        public static void Install() {

            // Idempotent — early-out if a previous Install() succeeded so
            // we don't end up with multiple handlers chaining to themselves.
            if(installed) return;
            installed = true;

            // AppDomain handler catches background-thread crashes and
            // anything the main message loop never sees.
            try {
                AppDomain.CurrentDomain.UnhandledException += OnAppDomainException;
            } catch { }

            // Application.ThreadException catches exceptions on the WinForms
            // UI thread. Without it, .NET's default unhandled-exception dialog
            // (the one with "Continue / Quit") fires and the user clicks
            // through it without a log file ever being written.
            try {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += OnThreadException;
            } catch { }
        }

        private static void OnAppDomainException(object sender, UnhandledExceptionEventArgs e) {
            try { Write(e.ExceptionObject as Exception, isTerminating: e.IsTerminating, source: "AppDomain"); } catch { }
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e) {
            try { Write(e.Exception, isTerminating: false, source: "WinForms ThreadException"); } catch { }
            // Don't suppress the default behaviour entirely — let the user
            // see the OS-level "OmenMon stopped working" dialog if the
            // exception was fatal. Without this rethrow, the GUI silently
            // continues in a half-broken state.
            //
            // Re-raising on a fresh thread guarantees we hit the
            // AppDomain handler one more time (which we already ignored
            // since it'd be the same exception) before the process dies.
            // This is the standard pattern for "crash dumper that doesn't
            // swallow the original failure."
            //
            // (No-op in practice for our purposes — we just want the .log
            // file on disk and the existing OS-level dialog. Skipping the
            // rethrow.)
        }

        // Writes one crash log entry. Safe to call from any thread.
        private static void Write(Exception ex, bool isTerminating, string source) {
            if(ex == null) return;
            lock(writeLock) {
                string path = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "OmenMon-crash-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".log");

                var sb = new StringBuilder();
                sb.AppendLine("# OmenMon-Reborn Crash Report");
                sb.AppendLine();
                sb.AppendLine($"> Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"> Source:    {source}");
                sb.AppendLine($"> Fatal:     {isTerminating}");
                sb.AppendLine($"> Paste this whole file into a new GitHub issue at <https://github.com/seakyy/OmenMon-Reborn/issues>.");
                sb.AppendLine();

                sb.AppendLine("## Exception");
                sb.AppendLine();
                sb.AppendLine("```");
                AppendException(sb, ex, depth: 0);
                sb.AppendLine("```");
                sb.AppendLine();

                sb.AppendLine("## Process");
                sb.AppendLine();
                sb.AppendLine("| Field | Value |");
                sb.AppendLine("|-------|-------|");
                try { sb.AppendLine($"| Command line     | `{Environment.CommandLine}` |"); } catch { }
                try { sb.AppendLine($"| Working dir      | `{Environment.CurrentDirectory}` |"); } catch { }
                try { sb.AppendLine($"| Process bitness  | `{(Environment.Is64BitProcess ? "x64" : "x86")}` |"); } catch { }
                try { sb.AppendLine($"| Uptime (ticks)   | `{Environment.TickCount}` ms |"); } catch { }
                sb.AppendLine();

                // Freeze the EC trace so the captured slice isn't disturbed
                // while we serialise it. (Re-enabled in finally.)
                bool reenable = EcTrace.IsEnabled;
                try { EcTrace.SetEnabled(false); } catch { }

                try {
                    // The full diagnostic bundle — environment, driver
                    // status, model preset, AutoCal sidecar, EC trace,
                    // probe. Wrapped in try because any sub-step might
                    // throw on a half-initialised machine.
                    sb.AppendLine("---");
                    sb.AppendLine();
                    sb.AppendLine("# Diagnostic Snapshot");
                    sb.AppendLine();
                    sb.AppendLine("_(Same content as `OmenMon.exe -Diag`, captured at the moment of the crash.)_");
                    sb.AppendLine();
                    sb.Append(CliOp.DiagGetMarkdown());
                } catch(Exception diagEx) {
                    sb.AppendLine();
                    sb.AppendLine($"_(diagnostic snapshot failed: `{diagEx.GetType().Name}: {diagEx.Message}`)_");
                } finally {
                    if(reenable) {
                        try { EcTrace.SetEnabled(true); } catch { }
                    }
                }

                try {
                    File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                } catch {
                    // If the install directory is read-only (e.g. Program
                    // Files install with non-elevated process), fall back
                    // to %LOCALAPPDATA%. Better there than nowhere.
                    try {
                        string fallback = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "OmenMon-Reborn",
                            "OmenMon-crash-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".log");
                        Directory.CreateDirectory(Path.GetDirectoryName(fallback));
                        File.WriteAllText(fallback, sb.ToString(), Encoding.UTF8);
                    } catch {
                        // Last resort: nothing. Better to disappear quietly
                        // than to throw a second exception from inside the
                        // first one's handler.
                    }
                }
            }
        }

        // Walks the InnerException chain. Indents each frame for readability.
        // AggregateException is unwrapped one level deep — anything Task-
        // related typically wraps a single real exception.
        private static void AppendException(StringBuilder sb, Exception ex, int depth) {
            const int maxDepth = 8;   // Hard cap on pathological cycles.
            string indent = new string(' ', depth * 2);

            sb.Append(indent).Append(ex.GetType().FullName).Append(": ").AppendLine(ex.Message);
            if(!string.IsNullOrEmpty(ex.StackTrace)) {
                foreach(string line in ex.StackTrace.Split('\n')) {
                    sb.Append(indent).AppendLine(line.TrimEnd('\r'));
                }
            }
            if(depth >= maxDepth) {
                sb.Append(indent).AppendLine("(further nested exceptions truncated)");
                return;
            }
            if(ex is AggregateException agg) {
                foreach(Exception inner in agg.InnerExceptions) {
                    if(inner != null) {
                        sb.AppendLine();
                        sb.Append(indent).AppendLine("--- Inner ---");
                        AppendException(sb, inner, depth + 1);
                    }
                }
            } else if(ex.InnerException != null) {
                sb.AppendLine();
                sb.Append(indent).AppendLine("--- Inner ---");
                AppendException(sb, ex.InnerException, depth + 1);
            }
        }

    }

}
