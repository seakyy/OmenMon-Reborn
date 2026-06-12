  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System.Text.RegularExpressions;

namespace OmenMon.Tests {

    // Source-level tripwires for the incident-derived guards catalogued in
    // docs/ARCHITECTURE_AUDIT.md §4 ("guard ledger"), plus a drift check for the
    // 3-place config plumbing. These tests read the checked-in source files as
    // text — they cannot validate behaviour (the main project targets .NET
    // Framework 4.8 and cannot be referenced from here), but they make removing
    // a guard a CONSCIOUS act that fails CI with the original issue number,
    // instead of something a refactor can drop silently.
    public class GuardLedgerTests {

        private static string RepoRoot { get; } = FindRepoRoot();

        private static string FindRepoRoot() {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while(current is not null) {
                if(File.Exists(Path.Combine(current.FullName, "OmenMon.xml")))
                    return current.FullName;
                current = current.Parent;
            }
            throw new FileNotFoundException("Unable to locate the repository root (OmenMon.xml) from test output.");
        }

        private static string Source(string relativePath) {
            return File.ReadAllText(Path.Combine(RepoRoot, relativePath));
        }

        // Boards confirmed to hit the BIOS rate-limiter EC freeze at 100 % fan
        // (lock-until-reboot). Removing one re-exposes its users to the freeze:
        // 8C30 #32, 8D07 #56, 8BAD #58, 8E35 #57, 8C77 #50, 88F4 #67.
        [Theory]
        [InlineData("8C30")]
        [InlineData("8D07")]
        [InlineData("8BAD")]
        [InlineData("8E35")]
        [InlineData("8C77")]
        [InlineData("88F4")]
        public void MaxFanFreezeList_StillContains(string productId) {
            var source = Source(Path.Combine("Hardware", "FanArray.cs"));
            Assert.Matches(
                new Regex("case\\s+\"" + productId + "\"\\s*:"),
                source);
        }

        // Boards whose RPM mapping cannot be discovered by the heuristic scanner
        // and ships as a curated KnownBoards entry. Removing one regresses that
        // board to garbage RPM readings (the issue numbers live next to each
        // entry in Library/AutoCal.cs).
        [Theory]
        [InlineData("8600")]
        [InlineData("8D87")]
        [InlineData("8BD4")]
        [InlineData("8DD0")]
        [InlineData("8C9C")]
        [InlineData("8BB3")]
        [InlineData("8BA9")]
        [InlineData("8D41")]
        [InlineData("8A3E")]
        public void KnownBoards_StillContains(string productId) {
            var source = Source(Path.Combine("Library", "AutoCal.cs"));
            Assert.Contains("[\"" + productId + "\"]", source);
        }

        // The shipped OmenMon.xml must keep documenting the settings users are
        // told to toggle in issue threads (e.g. #103's DisplayOffKeepAwake).
        [Theory]
        [InlineData("DisplayOffKeepAwake")]
        [InlineData("BatteryGlitchGuard")]
        [InlineData("AcFlickerGuard")]
        [InlineData("ThermalPanicEnabled")]
        public void ShippedXml_DocumentsSetting(string element) {
            var xml = Source("OmenMon.xml");
            Assert.Contains("<" + element + ">", xml);
        }

        // ConfigData fields are intentionally code-only when they hold runtime
        // identity/constants rather than user settings. Everything else must be
        // wired into Config.cs (load and/or save) — forgetting that plumbing is
        // silent at runtime: the setting "exists" but never persists, which is
        // exactly the drift this test exists to catch. If you add a genuinely
        // code-only field, add it here WITH a reason.
        private static readonly HashSet<string> CodeOnlySettings = new() {
            "AppName",                  // assembly identity, not a setting
            "AppVersion",               // assembly identity
            "AppProcessId",             // runtime value
            "BiosHeartbeatInterval",    // tuning constant, deliberately not user-facing
            "EnvVarSelfName",           // process-relaunch plumbing
            "GuiColorKbdBacklightOff",  // UI constant
            "LockNameMux",              // kernel object name, must never vary per-user
            "PathTemp",                 // runtime-resolved path
            "OnlyOnceFileExt",          // marker-file plumbing
            "OnlyOncePath",             // marker-file plumbing
            "TaskRunPath",              // scheduled-task plumbing
        };

        [Fact]
        public void EveryConfigField_IsWiredIntoConfigLoadSave_OrDeclaredCodeOnly() {
            var configData = Source(Path.Combine("Library", "ConfigData.cs"));
            var config = Source(Path.Combine("Library", "Config.cs"));

            var fields = Regex.Matches(
                    configData,
                    @"^\s*public static (?:bool|int|uint|byte|ushort|string)\s+(\w+)\s*=",
                    RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value)
                .ToList();

            // Sanity: the parse itself must keep finding a plausible field count,
            // otherwise a formatting change could silently blind this test.
            Assert.True(fields.Count >= 60,
                $"Only {fields.Count} ConfigData fields parsed — the field-declaration regex no longer matches the file's style.");

            var unwired = fields
                .Where(f => !CodeOnlySettings.Contains(f))
                .Where(f => !Regex.IsMatch(config, @"\b" + Regex.Escape(f) + @"\b"))
                .ToList();

            Assert.True(unwired.Count == 0,
                "ConfigData fields with no load/save plumbing in Config.cs (wire them or declare them code-only here): "
                + string.Join(", ", unwired));
        }

        // The EC scanner must stay pure (no App/driver dependencies) — it is
        // link-compiled into this test project, and the calibration regression
        // tests die the day someone couples it to the runtime. Cheap proxy:
        // forbid the using-directives that would pull those layers in.
        [Fact]
        public void EcDiffScanner_StaysDependencyFree() {
            var source = Source(Path.Combine("Hardware", "EcDiffScanner.cs"));
            Assert.DoesNotContain("using OmenMon.Library", source);
            Assert.DoesNotContain("using OmenMon.Driver", source);
            Assert.DoesNotContain("using OmenMon.AppGui", source);
            Assert.DoesNotContain("using OmenMon.AppCli", source);
        }

    }

}
