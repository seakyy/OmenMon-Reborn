  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/

using System;
using System.Threading;
using OmenMon.Driver;
using OmenMon.Library;

namespace OmenMon.Hardware.Ec {

#region Interface
    // Defines an interface for interacting with the Embedded Controller
    public interface IEmbeddedController : IDisposable {

        public bool IsInitialized { get; }

        public void Initialize();
        public void Close();

        // Lock
        public bool Request(int timeout);
        public void Release();

        // Read
        public byte ReadByte(byte register);
        public ushort ReadWord(byte register);

        // Write
        public void WriteByte(byte register, byte value);
        public void WriteWord(byte register, ushort value);

    }
#endregion

    // Implements as much of Embedded Controller functionality as possible
    // without getting into the kernel driver-specific routines
    // Builds up on the Embedded Controller data values and structures defined earlier
    public abstract class EmbeddedControllerAbstract 
        : EmbeddedControllerData, IEmbeddedController {

        public bool IsInitialized { get; protected set; }

        // Global counter of failed waiting to read attempts
        protected int WaitReadFailCount = 0;

#region Abstract Methods
        // Initialization and disposal
        // Implementation is driver-specific
        public abstract void Initialize();
        public abstract void Close();

        // Mutex lock request and release
        // Implementation depends on the EmbeddedControllerMutex class
        public abstract bool Request(int timeout);
        public abstract void Release();

        // Actual driver-specific read and write routines
        protected abstract byte ReadIoPort(Port port);
        protected abstract void WriteIoPort(Port port, byte value);

        // Dispose() is just a wrapper for Close()
        public virtual void Dispose() {
            Close();
        }
#endregion

#region Public Read & Write Methods
        // Wrapper to read a byte from an Embedded Controller register
        public virtual byte ReadByte(byte register) {
            int count = 0;
            byte value = 0;
            while(count < Config.EcRetryLimit) {
                if(ReadByteImpl(register, out value)) {
                    EcTrace.RecordRead(register, value);
                    return value;
                }
                count++;
            }
            return value;
        }

        // Wrapper to read a word (two bytes) from an Embedded Controller register
        public virtual ushort ReadWord(byte register) {
            int count = 0;
            ushort value = 0;
            ushort previous = 0;
            bool havePrevious = false;
            while(count < Config.EcRetryLimit) {
                if(ReadWordImpl(register, out value)) {

                    // A2 (issue #86): ReadWordImpl reads the low and high byte as two
                    // separate EC transactions, each with its own wait logic, so an EC
                    // state change between the bytes (or a single-byte wait failure)
                    // produces a self-inconsistent 16-bit value — directly visible as
                    // implausible RPM spikes. Validate by requiring two consecutive
                    // identical word reads before trusting the result: a genuine reading
                    // is stable across a few microseconds, a torn one is not.
                    if(havePrevious && value == previous)
                        return value;
                    previous = value;
                    havePrevious = true;

                }
                count++;
            }

            // Could not obtain two agreeing reads within the retry budget: return the
            // most recent successful read rather than a value known to be torn.
            return value;
        }

        // Wrapper to write a byte to an Embedded Controller register
        public virtual void WriteByte(byte register, byte value) {
            int count = 0;
            while(count < Config.EcRetryLimit) {
                if(WriteByteImpl(register, value)) {
                    EcTrace.RecordWrite(register, value);
                    return;
                }
                count++;
            }
        }

        // Wrapper to write a word (two bytes) to an Embedded Controller register
        public virtual void WriteWord(byte register, ushort value) {
            int count = 0;
            while(count < Config.EcRetryLimit) {
                if(WriteWordImpl(register, value))
                    return;
                count++;
            }
        }
#endregion

#region Protected Read & Write Implementation Methods
        // Reads a byte from an Embedded Controller register
        protected bool ReadByteImpl(byte register, out byte value) {
            if(WaitWrite()) {
                WriteIoPort(Port.Command, (byte) Command.Read);
                if(WaitWrite()) {
                    WriteIoPort(Port.Data, register);
                    if(WaitWrite() && WaitRead()) {
                        value = ReadIoPort(Port.Data);
                        return true;
                    }
                }
            }
            value = 0;
            return false;
        }

        // Reads a word (two bytes) from an Embedded Controller register
        protected bool ReadWordImpl(byte register, out ushort value) {
            byte result = 0;
            value = 0;
            if(!ReadByteImpl(register, out result))
                return false;
            value = result;
            if(!ReadByteImpl((byte) (register + 1), out result))
                return false;
            value |= (ushort) (result << 8);
            return true;
        }

        // Writes a byte to an Embedded Controller register
        protected bool WriteByteImpl(byte register, byte value) {
            if(WaitWrite()) {
                WriteIoPort(Port.Command, (byte) Command.Write);
                if(WaitWrite()) {
                    WriteIoPort(Port.Data, register);
                    if(WaitWrite()) {
                        WriteIoPort(Port.Data, value);
                        return true;
                    }
                }
            }
            return false;
        }

        // Writes a word (two bytes) to an Embedded Controller register
        protected bool WriteWordImpl(byte register, ushort value) {
            byte high = (byte) (value >> 8);
            byte low = (byte) value;
            if(!WriteByteImpl(register, low))
                return false;
            if(!WriteByteImpl((byte) (register + 1), high))
                return false;
            return true;
        }
#endregion

#region Protected Wait Methods
        // Waits until the Embedded Controller is in a suitable state
        protected bool Wait(Status status, bool isSet) {
            for (int i = 0; i < Config.EcWaitLimit; i++) {
                byte value = ReadIoPort(Port.Command);

                if(isSet)
                    value = (byte) ~value;

                if(((byte) status & value) == 0)
                    return true;

                // Alternatively, a much less legible one-liner:
                // if(((byte) status & (isSet ? (byte) ~value : value)) == 0)
                //     return true;

                // Back off to a 1 ms sleep once the initial fast spin is exhausted, so
                // a busy EC is no longer hammered while the OS/BIOS ACPI driver may be
                // mid-transaction. Relentless port polling raised the odds of an ACPI
                // "Embedded Controller did not respond before timeout" → BIOS panic
                // shutdown (issue #88, reported by @Bart82 on an HP Omen 16-am1001nw). The common
                // case — EC ready within a spin or two — never sleeps, so GUI refresh
                // latency is unchanged; only a contended EC yields the CPU. Set
                // EcWaitSpinCount >= EcWaitLimit to restore the legacy pure-spin.
                //
                // A4 (issue #88): the original #88 fix slept a full 1 ms for every
                // backoff iteration. Because Wait() runs while Global\Access_EC is held,
                // a Word read (up to ~8 Waits) could pin the mutex for far longer than
                // EcMutexTimeout (200 ms), starving the heartbeat / GUI / AutoConfig
                // waiters into ErrEcLock. Now the backoff is graduated: yield the
                // timeslice (Thread.Sleep(0), no fixed delay) for EcWaitYieldCount
                // iterations first so a momentarily-busy EC recovers without burning a
                // millisecond per poll, and only escalate to the 1 ms sleep once even
                // yielding hasn't cleared it. With the defaults (spin 5, yield 10, limit
                // 30) the worst-case hold per Wait drops from 25 ms to 15 ms while still
                // relieving the busy-spin that provoked the ACPI timeout shutdowns.
                if(i >= Config.EcWaitSpinCount)
                    Thread.Sleep(i < Config.EcWaitSpinCount + Config.EcWaitYieldCount ? 0 : 1);
            }
            return false;
        }

        // Waits for a read operation
        protected bool WaitRead() {
            if(Wait(Status.OutFull, true)) {
                WaitReadFailCount = 0;
                return true;
            }

            // A1 (issue #86): the legacy implementation returned **true** once
            // WaitReadFailCount exceeded EcFailLimit (15) even though OutFull was
            // never set — the caller then read the Data port and got a stale/garbage
            // byte. Worse, WaitReadFailCount was only ever reset on success, so the
            // moment the EC was briefly wedged OmenMon latched permanently into the
            // "always-true" mode and reported garbage for minutes. That self-
            // perpetuating fail-limit hack is the most likely root cause of the
            // recurring "RPM = negative / millions" and bad-temperature reports.
            // A sustained wait failure now returns **false**: ReadByteImpl propagates
            // the failure to the retry wrapper (ReadByte/ReadWord), which keeps the
            // last good value instead of fabricating one. The counter is retained as
            // a diagnostic signal (surfaced via EcTrace / -Diag) but no longer gates
            // behaviour.
            WaitReadFailCount++;
            return false;
        }

        // Waits for a write operation
        protected bool WaitWrite() {
            return Wait(Status.InFull, false);
        }
#endregion

    }

#region Driver Implementation
    // Links the abstract Embedded Controller implementation
    // to the low-level routines in the Ring0 kernel driver
    public sealed class EmbeddedController : EmbeddedControllerAbstract, IEmbeddedController {

        // The following three statements ensure the class can be instantiated only once
        private static readonly EmbeddedController instance = new EmbeddedController();

        private EmbeddedController() { }

        public static EmbeddedController Instance {
            get { return instance; }
        }

        // Serialises Initialize()/Close() on the singleton. The IsInitialized
        // check-then-set in both methods was previously unsynchronised even though
        // reads originate from the GUI tick, the AutoConfig background thread, the
        // Omen-key handler and the heartbeat tick — so an EcInterface() init could
        // race a parallel Close() and dispose the Ring0 driver out from under an
        // in-flight read (use-after-close). The cross-process EC mutex only serialises
        // port I/O during EcExec, not lifecycle, so this in-process lock closes the gap
        // (B1).
        private readonly object lifecycleLock = new object();

        // Initializes the kernel driver and creates a lock on the Embedded Controller
        public override void Initialize() {
            lock(this.lifecycleLock) {
                if(!this.IsInitialized) {
                    Ring0.Open();
                    if(Ring0.IsOpen) {
                        this.IsInitialized = true;
                        EmbeddedControllerMutex.Open();
                    } else {
                        // Report driver installation failure details
                        App.Error(Ring0.GetStatus());
                    }
                }
            }
        }

        // Closes the kernel driver and clears the Embedded Controller lock
        public override void Close() {
            lock(this.lifecycleLock) {
                if(this.IsInitialized) {
                    this.IsInitialized = false;
                    try {
                        EmbeddedControllerMutex.Close();
                    } catch {
                    }
                    try {
                        Ring0.Close();
                    } catch {
                    }
                }
            }
        }

        // Requests a lock on the Embedded Controller
        public override bool Request(int timeout) {
            return EmbeddedControllerMutex.Wait(timeout);
        }

        // Releases a lock on the Embedded Controller
        public override void Release() {
            EmbeddedControllerMutex.Release();
        }

        // Wrapper for the I/O port read routine in the kernel driver
        protected override byte ReadIoPort(Port port) {
            return Ring0.ReadIoPort((uint) port);
        }

        // Wrapper for the I/O port write routine in the kernel driver
        protected override void WriteIoPort(Port port, byte value) {
            Ring0.WriteIoPort((uint) port, value);
        }

    }
#endregion

}
