# PawnIO module

OmenMon talks to its hardware (the HP Omen Embedded Controller) via
[PawnIO](https://pawnio.eu/) instead of WinRing0. PawnIO ships a
Microsoft-signed kernel driver, so Windows Defender does not flag it.

## How it works

The production PawnIO driver only loads modules signed with the
maintainer's RSA-2048 key. To stay compatible with the unmodified,
signed driver — which is the whole point of moving away from WinRing0
— OmenMon uses the official, namazso-signed module
[`LpcACPIEC`](https://github.com/namazso/PawnIO.Modules/blob/main/LpcACPIEC.p).
That module exposes byte-granular I/O port read/write restricted to
the ACPI EC ports `0x62` and `0x66`, which is exactly what
`Hardware/Ec.cs` talks to.

The signed `LpcACPIEC.bin` lives in this folder and is embedded by the
`.csproj` as resource `OmenMon.LpcACPIEC.bin`. `Driver/PawnIo.cs` calls
its exports `ioctl_pio_read` / `ioctl_pio_write`.

## End-user setup

1. Install PawnIO from <https://pawnio.eu/>. The MSI puts
   `PawnIOLib.dll` in `C:\Program Files\PawnIO` and registers the
   signed `PawnIO.sys` kernel driver.
2. Run `OmenMon.exe` as administrator.

## Updating the embedded module

When namazso publishes a new release of `PawnIO.Modules`:

1. Download the `release_X_Y_Z.zip` from
   <https://github.com/namazso/PawnIO.Modules/releases>.
2. Replace `Resources/LpcACPIEC.bin` with the new one from the zip.
3. Rebuild OmenMon.

## Adding new kernel-mode operations

Custom Pawn modules can't be loaded into the production driver — they
would need namazso to sign them. So extending OmenMon's kernel-mode
surface means picking up an additional officially-signed module that
exposes the operation you need:

| Need               | Use module                              |
|--------------------|-----------------------------------------|
| MSR read/write     | `IntelMSR.bin` / `AMDFamily17.bin` etc. |
| Generic LPC ports  | `LpcIO.bin`                             |
| PCI config         | `IntelMCHBAR.bin`, `SmbusI801.bin`, …   |
| Dell SMM           | `DellSMM.bin`                           |

The same `Driver/PawnIo.cs` pattern works: open a second handle, load
the additional module, add `Fn<Whatever>` constants pointing at its
exports, and call `PawnIo.Execute`.

If you genuinely need a custom Pawn module signed with your own key,
you'd have to use the unrestricted PawnIO driver build — but that
defeats the migration's purpose because it isn't Microsoft-signed and
will trigger Defender just like WinRing0 did.

## Troubleshooting

- **`PawnIOLib.dll not found`** at runtime → PawnIO is not installed.
  Install from <https://pawnio.eu/>.
- **`pawnio_load returned 0x80070057`** → your `LpcACPIEC.bin` is
  missing, corrupted, or out of date. Re-download it from
  [PawnIO.Modules releases](https://github.com/namazso/PawnIO.Modules/releases).
  If you're an end user, place `LpcACPIEC.bin` next to `OmenMon.exe`
  so `Driver/PawnIo.cs` can load it as a side-by-side override. If
  you're updating the embedded copy in-source, replace
  `Resources/LpcACPIEC.bin` and rebuild OmenMon.
- **EC operations time out** → another process holds the
  `Global\Access_EC` mutex. Close HP Omen Gaming Hub and similar
  vendor utilities while running OmenMon.
