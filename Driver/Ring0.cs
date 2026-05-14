  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/

// The class name and public surface are kept for backwards compatibility
// with the rest of the codebase. The implementation now delegates to
// PawnIO instead of WinRing0; see Driver/PawnIo.cs.
//
// At runtime we load the official, namazso-signed LpcACPIEC PawnIO
// module (Resources/LpcACPIEC.bin). It exposes byte-granular I/O port
// access restricted to the ACPI EC ports (0x62 + 0x66) — exactly what
// Hardware/Ec.cs uses. MSR / PCI config / physical memory are not used
// anywhere in OmenMon, so those public methods are kept as no-op stubs
// for binary compatibility with anything outside this codebase that
// might reference them.

namespace OmenMon.Driver {

    public static class Ring0 {

        [System.ThreadStatic]
        private static ulong[] _readIoIn;
        [System.ThreadStatic]
        private static ulong[] _readIoOut;
        [System.ThreadStatic]
        private static bool _readIoBusy;
        [System.ThreadStatic]
        private static ulong[] _writeIoIn;
        [System.ThreadStatic]
        private static bool _writeIoBusy;

        public static bool IsOpen => PawnIo.IsOpen;

        public static void Open()  => PawnIo.Open();
        public static void Close() => PawnIo.Close();

        public static string GetStatus() => PawnIo.GetStatus();

#region Input/Output (I/O) Port Operations
        public static byte ReadIoPort(uint port) {
            if(_readIoBusy) {
                ulong[] nestedIn  = { port };
                ulong[] nestedOut = new ulong[1];
                if(!PawnIo.Execute(PawnIo.FnReadIoPortByte, nestedIn, nestedOut))
                    return 0;
                return (byte) (nestedOut[0] & 0xFF);
            }

            _readIoBusy = true;
            try {
                ulong[] inArr  = _readIoIn ?? (_readIoIn = new ulong[1]);
                ulong[] outArr = _readIoOut ?? (_readIoOut = new ulong[1]);
                inArr[0] = port;
                if(!PawnIo.Execute(PawnIo.FnReadIoPortByte, inArr, outArr))
                    return 0;
                return (byte) (outArr[0] & 0xFF);
            }
            finally { _readIoBusy = false; }
        }

        public static void WriteIoPort(uint port, byte value) {
            if(_writeIoBusy) {
                ulong[] nestedIn = { port, value };
                PawnIo.Execute(PawnIo.FnWriteIoPortByte, nestedIn, null);
                return;
            }

            _writeIoBusy = true;
            try {
                ulong[] inArr = _writeIoIn ?? (_writeIoIn = new ulong[2]);
                inArr[0] = port;
                inArr[1] = value;
                PawnIo.Execute(PawnIo.FnWriteIoPortByte, inArr, null);
            }
            finally { _writeIoBusy = false; }
        }
#endregion

#region Stubs for unused operations
        // OmenMon's Hardware layer never calls into MSR / PCI / Memory.
        // Kept as no-ops so the public Ring0 surface stays source-
        // compatible for any external callers. To enable any of these,
        // load an additional PawnIO module that exposes them (e.g.
        // IntelMSR.bin) — they would need a second PawnIo handle and
        // a second set of Fn... constants.

        public static bool ReadMsr(uint index, out uint eax, out uint edx) {
            eax = 0; edx = 0;
            return false;
        }

        public static bool WriteMsr(uint index, uint eax, uint edx) => false;

        public static uint GetPciAddress(byte bus, byte device, byte function) {
            return (uint) (((bus & 0xFF) << 8) | ((device & 0x1F) << 3) | (function & 7));
        }

        public static bool ReadPciConfig(uint pciAddress, uint regAddress, out uint value) {
            value = 0;
            return false;
        }

        public static bool WritePciConfig(uint pciAddress, uint regAddress, uint value) => false;

        public static bool ReadMemory<T>(ulong address, ref T buffer) => false;
        public static bool ReadMemory<T>(ulong address, ref T[] buffer) => false;
#endregion

    }

}
