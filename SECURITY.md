# Security Policy

## Supported Versions

Only the **latest released version** of OmenMon-Reborn receives security
updates. Older releases are not patched — please upgrade before reporting
any issue.

| Version          | Supported          |
|------------------|--------------------|
| `1.4.x-reborn`   | :white_check_mark: |
| `< 1.4.0-reborn` | :x:                |

## Reporting a Vulnerability

OmenMon-Reborn interfaces with kernel-mode drivers and HP's low-level
Embedded Controller, so security issues here can have real impact —
arbitrary EC writes, kernel-mode read primitives, privilege escalation
chains. **Please do not report security vulnerabilities through public
GitHub issues, discussions, or pull requests.**

### How to report privately

You have two options:

1. **GitHub private advisory** (preferred): use the repository's
   ["Report a vulnerability"](https://github.com/seakyy/OmenMon-Reborn/security/advisories/new)
   button under the Security tab. This creates a private channel between
   you and the maintainer that GitHub can later convert into a published
   advisory once a fix is out.
2. **Discord**: DM **`vsun`** (`835146076016607323`). Please include the
   word "security" in your first message so it's not lost in the general
   support flow.

### What to include in your report

A useful report contains, at minimum:

- The OmenMon-Reborn version (`OmenMon.exe -h` shows it, or check the
  tray menu's "About" entry).
- Your Windows version (`winver`) and whether HVCI / Memory Integrity is
  on.
- Whether PawnIO is installed and what version (`OmenMon.exe -Diag`
  output covers all of this in one paste — see below).
- A description of the issue, ideally with a reproducer.
- The impact you believe it has (information disclosure, privilege
  escalation, denial of service, etc.).

For non-trivial reports, running `OmenMon.exe -Diag` and attaching the
resulting `OmenMon-Diag-*.md` file gives the maintainer everything
needed to triage without back-and-forth.

### What to expect

- Acknowledgement within a few days (this is a hobby project — I don't
  promise 24-hour response times, but I do read every message).
- A coordinated disclosure timeline once severity is established.
- Credit in the resulting release notes if you'd like it (anonymous
  reports are also welcome).

### Out of scope

Things that are **not** security issues for this project:

- Windows Defender flagging older `1.3.x` releases — that's the WinRing0
  driver, and it's fixed in `1.4.0`+ by switching to the Microsoft-signed
  PawnIO driver. Just upgrade.
- Bugs that require an already-administrative attacker to exploit — they
  already have everything OmenMon could give them.
- Crashes that produce a stack trace but no exploitable state. Please
  file those as regular bug reports.

## Scope

This policy covers the `OmenMon-Reborn` codebase and the embedded
`Resources/LpcACPIEC.bin` PawnIO module redistribution. Issues in the
upstream PawnIO driver itself should be reported to
[namazso/PawnIO](https://github.com/namazso/PawnIO/security) directly.
