  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/

using System.Reflection;

// Project version metadata is set dynamically.
// These are just the defaults used for local builds. In CI the AddVersion
// target in OmenMon.csproj rewrites this file: AssemblyVersion is stamped
// with only the first three segments (X.Y.Z, .NET pads to X.Y.Z.0 — kept
// stable to avoid breaking assembly binding), while AssemblyFileVersion
// gets the full four segments including BUILD_NUMBER (auto-incremented by
// .github/workflows/build_bump.yml), so shipped binaries appear as
// X.Y.Z.<build#> in File Explorer / Get-FileHash properties.
[assembly: AssemblyVersion("1.4.5.0")]
[assembly: AssemblyFileVersion("1.4.5.0")]
[assembly: AssemblyInformationalVersion("1.4.5-reborn")]
[assembly: AssemblyMetadata("Timestamp", "Undefined")]
