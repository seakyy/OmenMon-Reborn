  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/

// PawnIO replaces the WinRing0 kernel driver.
// PawnIO ships a Microsoft-signed kernel driver and a user-mode
// library (PawnIOLib.dll) that loads compiled Pawn modules into the
// kernel. OmenMon's Pawn module is Resources/OmenMon.p; the compiled
// blob (OmenMon.bin) is embedded as a resource.

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace OmenMon.Driver {

    // Thin wrapper around PawnIOLib.dll. Loads the library by absolute
    // path so we don't depend on PATH being set up by the PawnIO
    // installer; afterwards [DllImport] resolves to the same module.
    public static class PawnIo {

        // Function names exported by the loaded PawnIO module. We use the
        // official signed module "LpcACPIEC" from
        // https://github.com/namazso/PawnIO.Modules — it whitelists ports
        // 0x62/0x66 (ACPI EC), which is exactly what OmenMon's EC code
        // talks to. Custom modules require namazso to sign them, so we
        // ride on the official one to avoid Defender warnings (the whole
        // point of moving away from WinRing0).
        public const string FnReadIoPortByte  = "ioctl_pio_read";
        public const string FnWriteIoPortByte = "ioctl_pio_write";

        private const string DllName = "PawnIOLib.dll";
        private const string EmbeddedBlobName = "OmenMon.LpcACPIEC.bin";
        private const string SideBySideBlobName = "LpcACPIEC.bin";

        private static IntPtr handle = IntPtr.Zero;
        private static readonly StringBuilder log = new();

        public static bool IsOpen => handle != IntPtr.Zero;
        public static string GetStatus() => log.ToString();

#region P/Invoke
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string fileName);

        // PawnIO C ABI. All functions return HRESULT (0 = S_OK).
        [DllImport(DllName, ExactSpelling = true)]
        private static extern int pawnio_version(out uint version);

        [DllImport(DllName, ExactSpelling = true)]
        private static extern int pawnio_open(out IntPtr handle);

        [DllImport(DllName, ExactSpelling = true)]
        private static extern int pawnio_load(IntPtr handle, byte[] blob, IntPtr size);

        [DllImport(DllName, ExactSpelling = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
        private static extern int pawnio_execute(
            IntPtr handle,
            string name,
            ulong[] inArray,  IntPtr inSize,
            ulong[] outArray, IntPtr outSize,
            out IntPtr returnSize);

        [DllImport(DllName, ExactSpelling = true)]
        private static extern int pawnio_close(IntPtr handle);
#endregion

#region Lifecycle
        public static void Open() {
            log.Length = 0;
            if(handle != IntPtr.Zero)
                return;

            // Locate and pre-load PawnIOLib.dll. Without this, [DllImport]
            // would only succeed if the PawnIO directory is on PATH.
            string libPath = LocatePawnIoLib();
            if(libPath == null) {
                log.AppendLine("PawnIOLib.dll not found.");
                log.AppendLine("Install PawnIO from https://pawnio.eu/ and restart OmenMon.");
                return;
            }
            if(LoadLibraryW(libPath) == IntPtr.Zero) {
                log.Append("Failed to load PawnIOLib.dll from: ").AppendLine(libPath);
                return;
            }

            int hr;
            try {
                hr = pawnio_open(out handle);
            } catch(Exception e) {
                log.Append("pawnio_open threw: ").AppendLine(e.Message);
                handle = IntPtr.Zero;
                return;
            }
            if(hr != 0 || handle == IntPtr.Zero) {
                log.Append($"pawnio_open returned 0x{hr:X8}").AppendLine();
                handle = IntPtr.Zero;
                return;
            }

            byte[] blob = LoadModuleBlob();
            if(blob == null) {
                log.AppendLine("OmenMon.bin not found.");
                log.AppendLine("Run Resources\\build-pawn.cmd to compile the Pawn module,");
                log.AppendLine("or place OmenMon.bin next to OmenMon.exe.");
                pawnio_close(handle);
                handle = IntPtr.Zero;
                return;
            }

            hr = pawnio_load(handle, blob, (IntPtr) blob.Length);
            if(hr != 0) {
                log.Append($"pawnio_load returned 0x{hr:X8}").AppendLine();
                pawnio_close(handle);
                handle = IntPtr.Zero;
            }
        }

        public static void Close() {
            if(handle != IntPtr.Zero) {
                try { pawnio_close(handle); } catch { }
                handle = IntPtr.Zero;
            }
        }
#endregion

#region Execute
        // Calls a Pawn-side public function by name. Returns true on success.
        // outArray may be null if the caller doesn't need a return payload.
        public static bool Execute(string fn, ulong[] inArray, ulong[] outArray) {
            if(handle == IntPtr.Zero)
                return false;

            inArray  ??= Array.Empty<ulong>();
            outArray ??= Array.Empty<ulong>();

            try {
                int hr = pawnio_execute(
                    handle, fn,
                    inArray,  (IntPtr) inArray.Length,
                    outArray, (IntPtr) outArray.Length,
                    out _);
                return hr == 0;
            } catch {
                return false;
            }
        }
#endregion

#region Helpers
        private static string LocatePawnIoLib() {
            // Registry hint set by the PawnIO installer (best effort).
            try {
                using RegistryKey key = RegistryKey
                    .OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .OpenSubKey(@"SOFTWARE\PawnIO");
                if(key?.GetValue("InstallDir") is string dir) {
                    string p = Path.Combine(dir, DllName);
                    if(File.Exists(p)) return p;
                }
            } catch { }

            string[] candidates = {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),    "PawnIO", DllName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "PawnIO", DllName),
            };
            foreach(string c in candidates)
                if(!string.IsNullOrEmpty(c) && File.Exists(c))
                    return c;
            return null;
        }

        // Loads the compiled Pawn blob. Filesystem copy next to the .exe wins
        // over the embedded resource so developers can iterate without
        // rebuilding the C# project.
        private static byte[] LoadModuleBlob() {
            try {
                string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if(!string.IsNullOrEmpty(exeDir)) {
                    string side = Path.Combine(exeDir, SideBySideBlobName);
                    if(File.Exists(side))
                        return File.ReadAllBytes(side);
                }
            } catch { }

            try {
                using Stream stream = typeof(PawnIo).Assembly
                    .GetManifestResourceStream(EmbeddedBlobName);
                if(stream == null)
                    return null;
                using MemoryStream ms = new();
                stream.CopyTo(ms);
                return ms.ToArray();
            } catch {
                return null;
            }
        }
#endregion

    }

}
