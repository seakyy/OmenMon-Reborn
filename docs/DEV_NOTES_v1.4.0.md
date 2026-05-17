# OmenMon-Reborn v1.4.0 — Developer Notes

**Topic:** WinRing0 → PawnIO migration
**Date:** 2026-05-09
**Scope:** kernel-mode access layer only — `Driver/` and one embedded resource

This document is the long-form companion to the v1.4.0 CHANGELOG entry.
It exists so the next person to touch this code (including future-me)
does not have to re-derive any of the reasoning. **Read it end-to-end
before changing anything in `Driver/`.**

---

## 1. Why we did this

WinRing0 is a generic Windows ring-0 driver from Open / Libre Hardware
Monitor that gives user-mode code raw `IN/OUT/RDMSR/WRMSR` and PCI
config access. OmenMon used it for one thing only: byte-granular
`IN`/`OUT` against the embedded controller's command (`0x66`) and data
(`0x62`) ports.

It works, but it has aged badly:

1. **Microsoft Defender flags `WinRing0.sys`** on every install. Older
   bundled versions had a public CVE (CVE-2020-14979) that allowed
   arbitrary kernel-mode reads via crafted IOCTLs from any user. The
   patched version we shipped removed the CVE but still trips heuristics
   because the driver itself is generic — it exposes raw I/O port,
   MSR, PCI, and physical-memory primitives unconditionally to anyone
   who opens its device.
2. **HVCI / Memory Integrity** rejects WinRing0 outright on machines
   where the user has it enabled — the driver is not HVCI-compatible.
3. **Driver self-installation is hostile to modern Windows.** OmenMon
   was extracting `Driver.sys.gz`, dropping the `.sys` next to the
   `.exe`, registering a kernel service via `OpenSCManager` /
   `CreateService` / `StartService`, and tearing it down on exit. Every
   step is a UAC prompt, a Defender prompt, or a HVCI rejection.

[PawnIO](https://pawnio.eu/) solves all three at once:

- **One signed driver, shared.** The maintainer (namazso) keeps a
  single Microsoft-signed, HVCI-compatible kernel driver. End-users
  install it once via an MSI; any application can use it after that.
  Defender doesn't prompt because the driver is properly attested.
- **Sandbox the dangerous bits.** Instead of exposing raw `IN`/`OUT`
  to user-mode, PawnIO runs sandboxed Pawn bytecode in the kernel.
  Each module is a small Pawn program that decides what subset of
  hardware it exposes — e.g. *"only ports 0x62 and 0x66, byte access
  only"*. User-mode talks to a module by calling its named exports
  through `pawnio_execute`.
- **Modules are signature-verified.** The driver only loads modules
  whose RSA-2048 signature can be verified against the maintainer's
  embedded public key. This is what keeps the architecture safe —
  unrestricted ring-0 power isn't exposed to *every* application that
  installs PawnIO; it's only exposed by modules the maintainer has
  reviewed and signed.

That last property is the one with the biggest impact on us, see §3.

---

## 2. Architecture map

```
┌────────────────────────────────────────────────────────────┐
│  OmenMon (user mode)                                        │
│                                                             │
│  Hardware/Ec.cs       (unchanged)                           │
│      │                                                      │
│      │  ReadIoPort(0x66) / WriteIoPort(0x62, x) / …          │
│      ▼                                                      │
│  Driver/Ring0.cs      (rewritten — same public API)         │
│      │                                                      │
│      │  PawnIo.Execute("ioctl_pio_read",  [port], [&val])    │
│      │  PawnIo.Execute("ioctl_pio_write", [port, val], [])   │
│      ▼                                                      │
│  Driver/PawnIo.cs     (new — wraps PawnIOLib.dll)           │
│      │                                                      │
│      │  pawnio_open / pawnio_load / pawnio_execute            │
│      ▼                                                      │
│  C:\Program Files\PawnIO\PawnIOLib.dll                      │
│      │                                                      │
│      │  IOCTL_PIO_LOAD_BINARY     (the embedded blob)         │
│      │  IOCTL_PIO_EXECUTE_FN      (per call)                  │
│      ▼                                                      │
├────────────────────────────────────────────────────────────┤
│  C:\Windows\System32\drivers\PawnIO.sys   (signed)          │
│                                                             │
│  vm_load_binary_internal()                                   │
│      │  parse [4-byte sig_len][sig][AMX]                      │
│      │  SHA256(amx) → verify against namazso's RSA pubkey     │
│      │  if OK → load AMX into kernel-side Pawn VM             │
│      ▼                                                      │
│  Pawn VM runs LpcACPIEC                                       │
│      │  ioctl_pio_read(port)  → guards port ∈ {0x62,0x66}    │
│      │                          → io_in_byte intrinsic        │
│      │  ioctl_pio_write(port, val) → same guard, io_out_byte  │
└────────────────────────────────────────────────────────────┘
```

### Files involved

| File                        | Status     | Notes |
|-----------------------------|------------|-------|
| `Driver/Ring0.cs`           | Rewritten  | Public API preserved. Internally calls `PawnIo`. MSR/PCI/Memory methods are no-op stubs. |
| `Driver/PawnIo.cs`          | New        | Wraps `PawnIOLib.dll`. Single static class — singleton handle, embedded resource loader, library-locator. |
| `Driver/Driver.cs`          | **Deleted**| WinRing0 SCM/IOCTL plumbing — obsolete. |
| `Resources/Driver.sys.gz`   | **Deleted**| WinRing0 kernel driver — obsolete. |
| `Resources/LpcACPIEC.bin`   | New        | Official signed PawnIO module. Embedded as `OmenMon.LpcACPIEC.bin`. |
| `Resources/PAWN_BUILD.md`   | New        | End-user / packager docs. |
| `External/Kernel.cs`        | Unchanged  | `IOCTL_OLS_*` constants and `DeviceIoControl` P/Invoke are now dead but harmless. Left in place for diff minimality. |
| `External/AdvApi.cs`        | Unchanged  | Still used by `Library/Os.cs` (service start/stop helpers). |
| `Hardware/Ec.cs`            | Unchanged  | Calls `Ring0.ReadIoPort` / `WriteIoPort` exactly as before. |
| `Hardware/EcMutex.cs`       | Unchanged  | Mutex name `Global\Access_EC` (`Config.LockPathEc`) is the same one LpcACPIEC expects. |
| `OmenMon.csproj`            | Modified   | `Compile`: `Driver\Driver.cs` → `Driver\PawnIo.cs`. `EmbeddedResource`: `Driver.sys.gz` → `LpcACPIEC.bin` with `Condition="Exists(...)"` so the build works even if a developer wipes the bin temporarily. |
| `.gitignore`                | Unchanged  | (Earlier draft added pawn-tools/ entries; reverted because the WSL build pipeline is no longer used — see §3.) |

### Data flow for a single EC byte read

The handshake `Hardware/Ec.cs`'s `ReadByteImpl` performs is:

1. `WaitWrite()` — poll port `0x66` until `InFull` is clear
2. `WriteIoPort(Port.Command, Command.Read)` — `OUT 0x66, 0x80`
3. `WaitWrite()` — poll again
4. `WriteIoPort(Port.Data, register)` — `OUT 0x62, <reg>`
5. `WaitWrite()` then `WaitRead()`
6. `value = ReadIoPort(Port.Data)` — `IN 0x62`

Each `Read/WriteIoPort` round-trips through:

- `Ring0.ReadIoPort/WriteIoPort` (one-line forwarders)
- `PawnIo.Execute("ioctl_pio_read"/"ioctl_pio_write", in[], out[])`
- `pawnio_execute` IOCTL → kernel-side Pawn VM
- LpcACPIEC's `is_port_allowed(port)` guard (allows only `0x62` and `0x66`)
- `io_in_byte` / `io_out_byte` Pawn intrinsic (kernel-mode `IN AL, DX` / `OUT DX, AL`)

This is heavier per call than WinRing0's single IOCTL, but EC accesses
are infrequent enough (a handful per second) that the latency is
invisible.

---

## 3. Why we use someone else's signed module

The most important architectural decision in this migration is that
**we do not ship a custom Pawn module.** We bundle the upstream
`LpcACPIEC.bin` as-is.

Concretely, the production `PawnIO.sys` driver enforces signature
verification in `vm_load_binary_internal` (file
`PawnIO/src/vm.cpp`). The wire format passed to `pawnio_load` is:

```
[ 4 bytes: sig_len (little-endian uint32) ]
[ sig_len bytes: PKCS#1 v1.5 RSA-2048 signature over SHA256(AMX) ]
[ remaining bytes: the AMX blob output by pawncc ]
```

The driver computes `SHA256(amx)`, then walks an array of *trusted
public keys* compiled into the driver and tries to verify the
signature against each in turn. Currently there is exactly one trusted
key: namazso's RSA-2048 public key from 2023. If verification fails,
`vm_load_binary_internal` returns `STATUS_INVALID_PARAMETER` →
`HRESULT 0x80070057`. **There is no developer-bypass and no per-machine
trust-store you can extend.**

A `PAWNIO_UNRESTRICTED` compile-time flag exists in the source that
turns the check into a pass-through. There is also an "unsigned"
build of the driver (`PawnIO_unsigned.pdb` is present in the install
directory, hinting at a parallel build). But the unsigned variant is
**not** Microsoft-signed — installing it requires test-signing mode or
DSE-disable, which would re-introduce the exact problem we're solving
(Defender warnings, HVCI rejection). It's a non-starter for OmenMon
end-users.

So our practical options were:

1. **Ship a custom Pawn module.** Rejected: would require namazso to
   sign every release of OmenMon, which neither scales nor is realistic
   to assume.
2. **Use the unrestricted driver.** Rejected: defeats the whole point.
3. **Find an existing signed module that already exposes everything we
   need.** ✅ — that's what we did.

[`LpcACPIEC`](https://github.com/namazso/PawnIO.Modules/blob/main/LpcACPIEC.p)
is exactly the module we need:

```pawn
DEFINE_IOCTL_SIZED(ioctl_pio_read, 1, 1) {
    new port = in[0] & 0xFFFF;
    if (!is_port_allowed(port)) return STATUS_ACCESS_DENIED;
    out[0] = io_in_byte(port);
    return STATUS_SUCCESS;
}
DEFINE_IOCTL_SIZED(ioctl_pio_write, 2, 0) {
    new port = in[0] & 0xFFFF;
    if (!is_port_allowed(port)) return STATUS_ACCESS_DENIED;
    io_out_byte(port, in[1]);
    return STATUS_SUCCESS;
}
is_port_allowed(port) { return port == 0x62 || port == 0x66; }
```

The `is_port_allowed` guard is exactly the set of ports OmenMon's EC
handshake uses, and the function names are stable across releases (the
module is part of the official module catalogue). We embed the signed
`.bin` straight into `OmenMon.exe`.

### Trade-off acknowledgement

This *does* cost us flexibility compared to the original WinRing0
setup, where we could have called any port / MSR / PCI register from
user-mode at will. Concretely:

- We **cannot** add new low-level kernel operations by editing source.
- Any future need that goes beyond byte I/O on `0x62` / `0x66` requires
  loading **an additional** signed module (see §6).
- Dead code in `Ring0.cs`'s MSR / PCI / Memory stubs is now actually
  dead — it returns `false` / `0`. Anything inside this codebase that
  depends on those returning real values would silently degrade. As
  of v1.4.0 nothing does (audited the whole tree before the
  migration), and the stubs are kept solely for source compatibility
  with anything outside the codebase that links against `Ring0`.

---

## 4. The `LpcACPIEC.bin` file — provenance and rotation

### Where it comes from

[PawnIO.Modules releases](https://github.com/namazso/PawnIO.Modules/releases)
publishes a `release_X.Y.Z.zip` containing every official module
already signed and ready to be loaded. The version we shipped in
v1.4.0 is from `release_0_2_6.zip` (released 2026-05-05). Provenance
chain:

```
   namazso (private RSA-2048 key)
       │  signs SHA256(LpcACPIEC.amx)
       ▼
   release_0_2_6.zip / LpcACPIEC.bin
       │  bundled into OmenMon.exe as embedded resource
       ▼
   OmenMon.LpcACPIEC.bin  →  PawnIo.cs reads this at runtime
```

The exact byte layout you'll see if you `xxd` the file:

- Bytes `0..3`: `00 02 00 00` (little-endian sig_len = `0x200` = 512).
- Bytes `4..515`: 512-byte RSA-2048 PKCS#1 v1.5 signature.
- Bytes `516..end`: the AMX bytecode (Pawn 4.1 output, `-C64 -;+ -(+ -p`).

PawnIO.sys reads this exact layout in `vm_load_binary_internal`.

### When to rotate

Rotate the `LpcACPIEC.bin` file when **any** of the following happens:

| Event | Action |
|-------|--------|
| `namazso/PawnIO.Modules` publishes a new `release_X.Y.Z.zip` | Drop in the new `LpcACPIEC.bin` from the zip and rebuild OmenMon. |
| The trusted-pubkey list in `PawnIO.sys` changes (new namazso key) and the user has updated PawnIO past that boundary | Same — grab the freshly signed module from the matching modules release. The pubkey is pinned at driver compile time, so a driver and module pair must come from compatible release ranges. |
| User's PawnIO install is older than v0.2.6 and rejects newer modules with `STATUS_INVALID_IMAGE_FORMAT` | Tell user to update PawnIO from <https://pawnio.eu/>. |
| Microsoft revokes the namazso driver signing certificate | Wait for namazso to re-sign and re-release `PawnIO.sys`. There's nothing OmenMon can do until then. |

### How to rotate

```cmd
cd Resources
curl -L -o release.zip https://github.com/namazso/PawnIO.Modules/releases/download/<TAG>/<filename>.zip
unzip -o release.zip LpcACPIEC.bin
del release.zip
msbuild ../OmenMon.csproj -p:Configuration=Release
```

The new `OmenMon.exe` will embed the rotated module. **Verify** that
the embedded resource is present after the build:

```powershell
[Reflection.Assembly]::LoadFrom('Bin\OmenMon.exe').GetManifestResourceNames()
# Expect to see: OmenMon.LpcACPIEC.bin
```

### What "the cert" is, exactly

There are **two** signing layers in the PawnIO ecosystem; don't
conflate them:

1. **Driver code-signing certificate** (Microsoft → namazso): used to
   sign `PawnIO.sys` so Windows accepts it as a kernel driver. This is
   what stops Defender from prompting. We don't manage this — it's
   part of the PawnIO MSI install. If Microsoft revokes it, every
   PawnIO user is affected, not just OmenMon.

2. **Module signing key** (namazso's RSA-2048): used to sign
   `LpcACPIEC.amx` so `PawnIO.sys` agrees to load it in the kernel.
   Pubkey is hard-coded into the driver source
   (`k_pubkey_namazso_2023[]` in `PawnIO/src/vm.cpp`). We don't manage
   this either — we just consume the already-signed `.bin`.

Both keys live with the upstream maintainer. **OmenMon never signs
anything.** That's the deliberate choice that lets v1.4.0 be a one-
shot security upgrade rather than an ongoing signing operation.

---

## 5. PawnIOLib.dll discovery — `PawnIo.cs` internals

The DLL is **not** in `PATH`; the PawnIO MSI installs it to
`C:\Program Files\PawnIO\PawnIOLib.dll` and registers nothing else. To
keep `[DllImport("PawnIOLib.dll")]` working, `PawnIo.Open()` uses a
manual pre-load:

1. Look up `HKLM\SOFTWARE\PawnIO\InstallDir` (best-effort — the MSI
   sets this; older versions may not).
2. If that misses, try `%ProgramFiles%\PawnIO\PawnIOLib.dll` and
   `%ProgramFiles(x86)%\PawnIO\PawnIOLib.dll`.
3. Call `LoadLibraryW(<full-path>)` once. Subsequent
   `[DllImport("PawnIOLib.dll", ...)]` resolutions find the already-
   loaded module by base name.

This pattern keeps the P/Invoke declarations clean (no
`SetDllDirectory` games, no manual `GetProcAddress` boilerplate).

### Module-blob loading order

`PawnIo.Open()` looks for the AMX blob in this order:

1. `OmenMon.exe`'s directory contains a file called
   `LpcACPIEC.bin` → load that. **(Developer override.)** Useful for
   iterating on a different module without rebuilding the C# project.
2. Embedded resource `OmenMon.LpcACPIEC.bin` → load that.
   (Production path.)
3. Neither exists → fail with a clear status message and surface it
   via `Ring0.GetStatus()` so `App.Error()` can show it to the user.

### Constants and where they're used

```csharp
public const string FnReadIoPortByte  = "ioctl_pio_read";
public const string FnWriteIoPortByte = "ioctl_pio_write";

private const string DllName            = "PawnIOLib.dll";
private const string EmbeddedBlobName   = "OmenMon.LpcACPIEC.bin";
private const string SideBySideBlobName = "LpcACPIEC.bin";
```

If you swap to a different module (different filename, different
exported function names), update **all five** constants. Forgetting
the embedded-resource logical name in the .csproj is the most common
breakage; it's checked in the build by `GetManifestResourceNames()` in
the verification command above.

---

## 6. Extending OmenMon with new kernel-mode operations

If a future feature needs something LpcACPIEC doesn't expose (anything
other than byte I/O on `0x62` / `0x66`), the procedure is:

1. **Pick the right official module.** Browse
   <https://github.com/namazso/PawnIO.Modules>. Frequently relevant
   ones:

   | Need | Module |
   |------|--------|
   | Read/write Intel MSRs | `IntelMSR.bin` |
   | Read/write AMD MSRs (Zen / Zen2 / Zen3) | `AMDFamily17.bin` |
   | Generic LPC ports (Super-I/O config 0x2E/0x4E) | `LpcIO.bin` |
   | Intel SMBus (I801) | `SmbusI801.bin` |
   | Intel MCHBAR (memory controller hub) | `IntelMCHBAR.bin` |
   | Dell SMM mailbox | `DellSMM.bin` |

2. **Drop the `.bin` into `Resources/`** alongside `LpcACPIEC.bin`.

3. **Add it as an embedded resource** in `OmenMon.csproj`:

   ```xml
   <EmbeddedResource Include="Resources\IntelMSR.bin" Condition="Exists('Resources\IntelMSR.bin')">
     <LogicalName>OmenMon.IntelMSR.bin</LogicalName>
   </EmbeddedResource>
   ```

4. **Allocate a second handle** in `Driver/PawnIo.cs`. Each call to
   `pawnio_open` returns a separate executor handle, and `pawnio_load`
   can only load one module per handle (returns
   `STATUS_ALREADY_INITIALIZED` if you try twice). The current
   singleton approach has to grow into a small dictionary keyed by
   module name. Sketch:

   ```csharp
   private static readonly Dictionary<string, IntPtr> handles = new();

   public static bool LoadModule(string moduleName, byte[] blob) {
       if (handles.ContainsKey(moduleName)) return true;
       if (pawnio_open(out IntPtr h) != 0 || h == IntPtr.Zero) return false;
       if (pawnio_load(h, blob, (IntPtr)blob.Length) != 0) {
           pawnio_close(h);
           return false;
       }
       handles[moduleName] = h;
       return true;
   }

   public static bool Execute(string moduleName, string fn, ulong[] inArr, ulong[] outArr) {
       if (!handles.TryGetValue(moduleName, out IntPtr h)) return false;
       /* … same pawnio_execute call as today … */
   }
   ```

5. **Add `Fn<Whatever>` constants** for the new module's exports and a
   wrapper in `Ring0.cs` (or a sibling class) that calls
   `PawnIo.Execute("IntelMSR", PawnIo.FnReadMsr, inArr, outArr)`.

6. **Test once with the user's hardware** and rotate `Resources/<the
   bin>` whenever PawnIO.Modules releases a new version.

A custom Pawn module written in-house will **not** load — see §3.

---

## 7. Testing protocol when changing anything in `Driver/`

Mandatory before tagging a release:

1. **`PawnIOLib.dll` discovery — DLL absent path.** Temporarily rename
   the install directory (`C:\Program Files\PawnIO` →
   `C:\Program Files\PawnIO_renamed`), launch `OmenMon.exe`, expect
   the GUI to surface "PawnIOLib.dll not found. Install PawnIO from
   https://pawnio.eu/ and restart OmenMon." Restore the directory.
2. **`PawnIOLib.dll` discovery — registry hint absent.** Delete
   `HKLM\SOFTWARE\PawnIO` (back it up first — `reg export HKLM\SOFTWARE\PawnIO pawnio.reg`
   if it exists). Launch. Expect success via the Program-Files
   fallback. Restore the registry.
3. **Embedded blob path.** Build OmenMon **without** a side-by-side
   `LpcACPIEC.bin`. Confirm the resource is present in the output
   `.exe` and that `Ring0.IsOpen` becomes `true`.
4. **Side-by-side override path.** Drop a different `LpcACPIEC.bin`
   next to `OmenMon.exe` (e.g. an obviously corrupted blob). Expect
   `pawnio_load` to fail with `0x80070057`. Remove the file. Expect
   success on the next run via the embedded resource.
5. **EC handshake smoke test.** Launch the GUI, watch the main form's
   sensor readouts populate (CPUT / GPTM should show plausible values,
   fan RPMs in the kRPM range). If any read returns `0xFF` or `0`, the
   module probably isn't loading even though `Ring0.IsOpen` is `true`
   — check `Ring0.GetStatus()`.
6. **Coexistence with HP Omen Gaming Hub.** Launch HP's tool, then
   OmenMon. Both should function — they share the
   `Global\Access_EC` mutex. If OmenMon hangs trying to acquire it,
   the mutex name has drifted (rare; HP doesn't change it).
7. **HVCI on.** Enable Memory Integrity, reboot, run OmenMon. Whole
   migration purpose is that this works without any further user
   action — confirm.

---

## 8. Glossary

| Term | Meaning |
|------|---------|
| AMX  | Pawn Abstract Machine eXecutor — the bytecode format `pawncc` produces. PawnIO loads an AMX blob into a kernel-side Pawn VM. |
| EC   | Embedded Controller — the small microcontroller on the motherboard that handles fan control, battery management, and lid switches. Talks to the CPU via I/O ports `0x62` (data) and `0x66` (command). |
| HVCI | Hypervisor-Protected Code Integrity (a.k.a. Memory Integrity in Windows Settings). When on, the kernel runs in a VM and only loads drivers that have been built without unsafe constructs. WinRing0 fails this check; PawnIO passes. |
| Pawn | The scripting language the modules are written in (CompuPhase). Same lineage as SourcePawn / SA-MP. |
| PawnIO | The kernel driver + user-mode library that hosts signed Pawn modules in kernel mode and exposes them via IOCTLs. |
| Module / blob | A signed `[sig_len][signature][AMX]` triple. Equivalent terms in this document. |
| `pawnio_execute` | The IOCTL OmenMon calls dozens of times per second to read or write a single EC byte. |

---

## 9. Files to look at, in order, if something breaks

1. `Driver/PawnIo.cs` — first place anything user-mode goes wrong.
2. `Resources/PAWN_BUILD.md` — end-user troubleshooting (mostly about
   missing PawnIO install).
3. `Driver/Ring0.cs` — only one-line forwarders; rarely the problem.
4. `Hardware/Ec.cs` — unchanged in v1.4.0 but always worth re-reading
   to remind yourself of the 4-step EC handshake.
5. PawnIO upstream:
   - <https://github.com/namazso/PawnIO/blob/master/PawnIO/src/vm.cpp> — module load + signature check
   - <https://github.com/namazso/PawnIO/blob/master/PawnIOLib/PawnIOLib.cpp> — user-mode API
   - <https://github.com/namazso/PawnIO.Modules/blob/main/LpcACPIEC.p> — the module we ship
