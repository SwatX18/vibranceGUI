using System;
using System.Collections.Generic;

namespace vibrance.GUI.common.gamefinder
{
    /// <summary>
    /// The reference expectations as literal data - five positive cases, two negative. No GUI,
    /// no disk. Run by vibrance.GUI.exe --selftest-gamefinder.
    ///
    /// Every file name, relative path and byte size below was measured on the reference machine
    /// (game-finder-evidence.md, Findings 5, 6, 8 and 9). Sizes are preserved exactly: the
    /// algorithm must win Counter-Strike 2 on exclusion, never by re-tuning a size.
    /// </summary>
    public static class ExecutablePickerFixture
    {
        // A label only. Nothing here is ever opened, so the fixture runs on any machine.
        private const string LibraryRoot = @"E:\Steam\steamapps\common";

        private const string NoSelection = "(none)";

        // One line per case, a FAIL line per mismatch, and a final "PASSED n/7".
        public static List<string> Run()
        {
            List<FixtureCase> cases = BuildCases();
            List<string> lines = new List<string>();
            lines.Add("vibranceGUI game finder - executable picker self test");
            lines.Add(string.Empty);

            int passed = 0;
            foreach (FixtureCase fixtureCase in cases)
            {
                ExecutableCandidate selected = ExecutablePicker.Select(fixtureCase.Candidates);
                string actual = selected == null ? NoSelection : selected.FileName;
                string expected = fixtureCase.ExpectedFileName == null
                    ? NoSelection
                    : fixtureCase.ExpectedFileName;

                bool isPass = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
                if (isPass)
                    passed++;

                lines.Add(string.Format("[{0}] {1} selected={2} expected={3}",
                    isPass ? "PASS" : "FAIL",
                    fixtureCase.InstallFolder.PadRight(34),
                    actual.PadRight(30),
                    expected));
            }

            lines.Add(string.Empty);
            lines.Add(string.Format("PASSED {0}/{1}", passed, cases.Count));
            lines.Add(string.Empty);
            lines.Add("Documented non-case: 'Marathon Server Slam' never reaches the picker. It is an");
            lines.Add("orphan folder left by an uninstall and appears in no appmanifest, which is why the");
            lines.Add("Steam source enumerates appmanifest_*.acf and never lists steamapps\\common\\.");
            return lines;
        }

        private static List<FixtureCase> BuildCases()
        {
            List<FixtureCase> cases = new List<FixtureCase>();

            // Age of Empires IV - EssenceEditor.exe is the decoy the "*editor*" glob removes;
            // GPUBurner.exe survives and loses on size, as intended.
            cases.Add(new FixtureCase("Age of Empires IV", "RelicCardinal.exe",
                Candidate("Age of Empires IV", @"RelicCardinal.exe", 144752932L),
                Candidate("Age of Empires IV", @"GPUBurner.exe", 51616064L),
                Candidate("Age of Empires IV", @"EssenceEditor.exe", 10231608L),
                Candidate("Age of Empires IV", @"WebClient.exe", 2365952L),
                Candidate("Age of Empires IV", @"BsSndRpt64.exe", 389648L),
                Candidate("Age of Empires IV", @"BsSndRpt.exe", 356880L),
                Candidate("Age of Empires IV", @"BugSplatHD64.exe", 336400L)));

            // Arc Raiders - the winner sits at depth 3 and a same-named 9 MB stub sits at depth 0,
            // so this case also covers the depth tie-break never firing on a size difference.
            cases.Add(new FixtureCase("Arc Raiders", "PioneerGame.exe",
                Candidate("Arc Raiders", @"PioneerGame\Binaries\Win64\PioneerGame.exe", 284946288L),
                Candidate("Arc Raiders", @"Engine\Extras\Redist\en-us\UEPrereqSetup_x64.exe", 50444088L),
                Candidate("Arc Raiders", @"Engine\Binaries\Win64\CrashReportClient.exe", 24645488L),
                Candidate("Arc Raiders", @"Installers\AntiCheatInstaller.exe", 13223576L),
                Candidate("Arc Raiders", @"PioneerGame.exe", 8956272L),
                Candidate("Arc Raiders", @"Engine\Binaries\Win64\EpicWebHelper.exe", 4100272L)));

            // Counter-Strike 2 - the case the whole exclusion set exists for. vconsole2.exe is
            // larger than cs2.exe; only the "vconsole*" glob makes this pass.
            cases.Add(new FixtureCase("Counter-Strike Global Offensive", "cs2.exe",
                Candidate("Counter-Strike Global Offensive", @"game\bin\win64\vconsole2.exe", 5029528L),
                Candidate("Counter-Strike Global Offensive", @"game\bin\win64\cs2.exe", 2967704L),
                Candidate("Counter-Strike Global Offensive", @"game\csgo\bin\legacy\csgo_legacy_app.exe", 1728360L)));

            cases.Add(new FixtureCase("PUBG", "TslGame.exe",
                Candidate("PUBG", @"TslGame\Binaries\Win64\TslGame.exe", 232187776L),
                Candidate("PUBG", @"TslGame\Binaries\Win64\TslGame_ZK.exe", 50483016L),
                Candidate("PUBG", @"TslGame\Binaries\Win64\BattlEye\BEService_x64.exe", 18991400L),
                Candidate("PUBG", @"TslGame\Binaries\Win64\ExecPubg.exe", 8671688L),
                Candidate("PUBG", @"TslGame\Binaries\Win64\iigw\iigw_server.exe", 5837272L),
                Candidate("PUBG", @"TslGame\Binaries\ThirdParty\WinMTR\WinMTR.exe", 4575192L),
                Candidate("PUBG", @"Engine\Binaries\Win64\UnrealCEFSubProcess_3071.exe", 3997728L),
                Candidate("PUBG", @"TslGame\Binaries\Win64\TslGame_BE.exe", 1127200L),
                Candidate("PUBG", @"TslGame\Binaries\ThirdParty\BroCrashReporter\BroCrashReporter.exe", 216216L)));

            // Squad - squad_launcher.exe and start_protected_game.exe are byte-identical in size;
            // the second is excluded, the first survives and loses on size.
            cases.Add(new FixtureCase("Squad", "SquadGame-Win64-Shipping.exe",
                Candidate("Squad", @"SquadGame\Binaries\Win64\SquadGame-Win64-Shipping.exe", 228896768L),
                Candidate("Squad", @"Engine\Binaries\Win64\CrashReportClient.exe", 26635776L),
                Candidate("Squad", @"Engine\Extras\Redist\en-us\vc_redist.x64.exe", 25635768L),
                Candidate("Squad", @"Engine\Extras\Redist\en-us\vc_redist.arm64.exe", 11722336L),
                Candidate("Squad", @"Engine\Binaries\Win64\EpicWebHelper.exe", 4861440L),
                Candidate("Squad", @"squad_launcher.exe", 3975920L),
                Candidate("Squad", @"start_protected_game.exe", 3975920L),
                Candidate("Squad", @"EasyAntiCheat\EasyAntiCheat_EOS_Setup.exe", 959216L)));

            // Negative: the manifest exists but the content was never downloaded. Zero executables.
            cases.Add(new FixtureCase("Hell Let Loose - Vietnam Playtest", null));

            // Negative: 14 executables, every one a redistributable. Filtered by the globs alone,
            // independently of the appid denylist - belt and braces, keep both.
            cases.Add(new FixtureCase("Steamworks Shared", null,
                Candidate("Steamworks Shared", @"_CommonRedist\DotNet\3.5 Client Profile\DotNetFx35Client.exe", 267963920L),
                Candidate("Steamworks Shared", @"_CommonRedist\vcredist\2022\VC_redist.x64.exe", 18731856L),
                Candidate("Steamworks Shared", @"_CommonRedist\vcredist\2015\vc_redist.x64.exe", 15301888L),
                Candidate("Steamworks Shared", @"_CommonRedist\vcredist\2017\vc_redist.x64.exe", 15261400L),
                Candidate("Steamworks Shared", @"_CommonRedist\vcredist\2019\VC_redist.x64.exe", 14882584L),
                Candidate("Steamworks Shared", @"_CommonRedist\vcredist\2015\vc_redist.x86.exe", 14456872L),
                Candidate("Steamworks Shared", @"_CommonRedist\vcredist\2017\vc_redist.x86.exe", 14401656L),
                Candidate("Steamworks Shared", @"_CommonRedist\vcredist\2019\VC_redist.x86.exe", 14328440L),
                Candidate("Steamworks Shared", @"_CommonRedist\vcredist\2013\vcredist_x64.exe", 7194312L),
                Candidate("Steamworks Shared", @"_CommonRedist\vcredist\2012\vcredist_x64.exe", 7186992L),
                Candidate("Steamworks Shared", @"_CommonRedist\vcredist\2022\VC_redist.x86.exe", 6941536L),
                Candidate("Steamworks Shared", @"_CommonRedist\vcredist\2012\vcredist_x86.exe", 6554576L),
                Candidate("Steamworks Shared", @"_CommonRedist\vcredist\2013\vcredist_x86.exe", 6503984L),
                Candidate("Steamworks Shared", @"_CommonRedist\DirectX\Jun2010\DXSETUP.exe", 517976L)));

            return cases;
        }

        // Mirrors what ExecutableEnumerator produces from a real walk: Depth is the number of
        // separators in RelativePath, so a file directly in the install directory is depth 0.
        private static ExecutableCandidate Candidate(string installFolder, string relativePath, long sizeBytes)
        {
            ExecutableCandidate candidate = new ExecutableCandidate();
            candidate.RelativePath = relativePath;
            candidate.FileName = relativePath.Substring(relativePath.LastIndexOf('\\') + 1);
            candidate.FullPath = LibraryRoot + "\\" + installFolder + "\\" + relativePath;
            candidate.SizeBytes = sizeBytes;
            candidate.Depth = CountSeparators(relativePath);
            return candidate;
        }

        private static int CountSeparators(string relativePath)
        {
            int count = 0;
            for (int i = 0; i < relativePath.Length; i++)
            {
                if (relativePath[i] == '\\')
                    count++;
            }
            return count;
        }

        private class FixtureCase
        {
            public FixtureCase(string installFolder, string expectedFileName, params ExecutableCandidate[] candidates)
            {
                this.InstallFolder = installFolder;
                this.ExpectedFileName = expectedFileName;
                this.Candidates = new List<ExecutableCandidate>(candidates);
            }

            public string InstallFolder { get; private set; }

            // null means the case expects Select(...) to return null.
            public string ExpectedFileName { get; private set; }

            public List<ExecutableCandidate> Candidates { get; private set; }
        }
    }
}
