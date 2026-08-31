using System;
using System.Collections.Generic;

namespace vibrance.GUI.common.gamefinder
{
    /// <summary>
    /// The reference expectations for StartMenuShortcutSource's filtering, dedupe and confidence
    /// decisions, as literal data - no GUI, no disk, no real .lnk file and no COM. Run by
    /// vibrance.GUI.exe --selftest-shortcuts.
    ///
    /// Every case drives StartMenuShortcutSource.ScanShortcuts (the internal seam that takes
    /// already-known shortcut paths, the same method the real directory walk in ScanRoot calls per
    /// shortcut it finds) against a FakeResolver - a plain Dictionary&lt;string, ShortcutTarget&gt;,
    /// nothing more; a shortcut path simply absent from it IS the "could not read this .lnk"
    /// outcome, so there is no separate flag to simulate that. Several cases use literal file names
    /// and target paths measured against the real Start Menu on the reference machine
    /// (unins000.exe, "Diablo IV Launcher.exe", the Microsoft Office install tree) rather than
    /// invented ones, so a regression here reflects a regression a real player would hit.
    /// </summary>
    public static class StartMenuShortcutSourceFixture
    {
        public static List<string> Run()
        {
            List<FixtureCase> cases = BuildCases();
            List<string> lines = new List<string>();
            lines.Add("vibranceGUI game finder - Start Menu / desktop shortcut source self test");
            lines.Add(string.Empty);

            int passed = 0;
            foreach (FixtureCase fixtureCase in cases)
            {
                string actual = RunCase(fixtureCase);
                bool isPass = string.Equals(actual, fixtureCase.Expected, StringComparison.Ordinal);
                if (isPass)
                    passed++;

                lines.Add(string.Format("[{0}] {1}", isPass ? "PASS" : "FAIL", fixtureCase.Name));
                if (!isPass)
                {
                    lines.Add("      expected: " + fixtureCase.Expected);
                    lines.Add("      actual:   " + actual);
                }
            }

            lines.Add(string.Empty);
            lines.Add(string.Format("PASSED {0}/{1}", passed, cases.Count));
            return lines;
        }

        private static string RunCase(FixtureCase fixtureCase)
        {
            StartMenuShortcutSource source = new StartMenuShortcutSource(fixtureCase.Resolver);
            ScanRecorder recorder = new ScanRecorder(fixtureCase.IsCancelled);
            source.ScanShortcuts(fixtureCase.ShortcutPaths, recorder.CreateContext());
            return recorder.Summarize();
        }

        // ------------------------------------------------------------------ cases

        private static List<FixtureCase> BuildCases()
        {
            List<FixtureCase> cases = new List<FixtureCase>();

            cases.Add(BuildSimpleCase(
                "Resolves a legitimate shortcut to a Guessed candidate",
                "C:\\StartMenu\\Notepad++.lnk", "D:\\Notepad++\\notepad++.exe", true,
                "report:Notepad++=D:\\Notepad++\\notepad++.exe[Guessed]"));

            // No entry in the fake map at all - the resolver's stand-in for a corrupt or
            // unreadable .lnk, since IShortcutResolver.Resolve never throws (see its own comment).
            cases.Add(new FixtureCase(
                "Unresolvable shortcut is skipped, not reported",
                Paths("C:\\StartMenu\\Broken.lnk"),
                new FakeResolver(),
                null,
                "skip:could not read shortcut"));

            cases.Add(BuildSimpleCase(
                "A shortcut with no file target (virtual shell item) is skipped",
                "C:\\StartMenu\\Control Panel.lnk", string.Empty, false,
                "skip:shortcut has no file target"));

            cases.Add(BuildSimpleCase(
                "A non-.exe target (a manual, measured pattern: PuTTY Manual.lnk -> putty.chm) is skipped",
                "C:\\StartMenu\\Manual.lnk", "D:\\Docs\\manual.pdf", true,
                "skip:target is not an executable"));

            // unins000.exe is the InnoSetup uninstaller naming convention - measured on the
            // reference machine resolving from "Uninstall ASUS XG-C100C 10G Adapter Driver.lnk".
            // "*uninst*" alone does not catch it (see ExecutableRules' own comment on "unins0*").
            cases.Add(BuildSimpleCase(
                "An InnoSetup uninstaller (unins0*, the shared glob) is skipped",
                "C:\\StartMenu\\Uninstall Foo.lnk", "C:\\Program Files (x86)\\Foo\\unins000.exe", true,
                "skip:excluded executable"));

            // Measured on the reference machine: Diablo IV.lnk resolves to "Diablo IV Launcher.exe",
            // a per-game launcher stub - not "Battle.net.exe", so only the local "*launcher*" glob
            // catches it, never the shared ExecutableRules list.
            cases.Add(BuildSimpleCase(
                "A per-game launcher stub (local launcher glob) is skipped",
                "C:\\StartMenu\\Diablo IV.lnk", "E:\\Battle.net\\Diablo IV\\Diablo IV Launcher.exe", true,
                "skip:launcher executable"));

            cases.Add(BuildSimpleCase(
                "A target under the Windows directory is skipped",
                "C:\\StartMenu\\Registry Editor.lnk", WindowsExecutablePath("regedit.exe"), true,
                "skip:excluded location"));

            // Measured on the reference machine: Excel.lnk, Access.lnk, OneNote.lnk and five more
            // all resolve straight into this tree - 8 of 148 shortcuts, none caught by any filename
            // glob, which is why "Microsoft Office" is checked as a directory segment.
            cases.Add(BuildSimpleCase(
                "A Microsoft Office target (local non-game vendor segment) is skipped",
                "C:\\StartMenu\\Excel.lnk",
                "C:\\Program Files (x86)\\Microsoft Office\\root\\Office16\\EXCEL.EXE", true,
                "skip:excluded location"));

            // The file name here ("Something.exe") matches no glob at all, isolating the shared
            // ExecutableRules.ExcludedDirectorySegments check from the filename checks that run
            // before it - a case built from a name any of the globs would also catch could keep
            // passing after this check was deleted, which would make it unable to fail.
            cases.Add(BuildSimpleCase(
                "A shared excluded directory segment (BattlEye) is skipped",
                "C:\\StartMenu\\Something.lnk", "C:\\Games\\SomeGame\\BattlEye\\Something.exe", true,
                "skip:excluded location"));

            cases.Add(BuildSimpleCase(
                "A shortcut to vibranceGUI's own running executable is skipped",
                "C:\\StartMenu\\vibranceGUI.lnk", StartMenuShortcutSource.GetOwnExecutablePath(), true,
                "skip:vibranceGUI itself"));

            cases.Add(BuildSimpleCase(
                "A target that no longer exists on disk is skipped",
                "C:\\StartMenu\\Old Game.lnk", "C:\\Games\\OldGame\\oldgame.exe", false,
                "skip:named executable not found"));

            cases.Add(BuildDedupeCase());
            cases.Add(BuildCancellationCase());

            cases.Add(BuildSimpleCase(
                "GameName comes from the shortcut's own file name, not the target's",
                "C:\\StartMenu\\My Cool Game.lnk", "C:\\Games\\Weird\\differently_named.exe", true,
                "report:My Cool Game=C:\\Games\\Weird\\differently_named.exe[Guessed]"));

            return cases;
        }

        private static FixtureCase BuildSimpleCase(string name, string shortcutPath,
                                                    string executablePath, bool targetExists,
                                                    string expected)
        {
            FakeResolver resolver = new FakeResolver();
            resolver.Add(shortcutPath, executablePath, targetExists);
            return new FixtureCase(name, Paths(shortcutPath), resolver, null, expected);
        }

        // Two Start Menu shortcuts (a per-user AND an all-users one is unremarkable) that resolve
        // to the SAME executable - only the first may reach the review list; the second must be
        // silently dropped, not reported as a second row and not reported as a skip either
        // (GameFinderScanner's cross-source dedupe uses the same "silent" rule for the same reason).
        private static FixtureCase BuildDedupeCase()
        {
            FakeResolver resolver = new FakeResolver();
            resolver.Add("C:\\StartMenu\\First.lnk", "C:\\Games\\Shared\\game.exe", true);
            resolver.Add("C:\\Desktop\\Second.lnk", "C:\\Games\\Shared\\game.exe", true);

            return new FixtureCase(
                "The same executable reached through a second shortcut is reported once",
                Paths("C:\\StartMenu\\First.lnk", "C:\\Desktop\\Second.lnk"),
                resolver, null,
                "report:First=C:\\Games\\Shared\\game.exe[Guessed]");
        }

        // Three shortcuts, all independently reportable; cancellation is asserted true starting
        // from the SECOND check ScanShortcuts makes (the loop checks IsCancelled once before each
        // shortcut, matching every other source's "once per game" contract) - so GameA is
        // processed and GameB/GameC must never be reached at all, proving the check actually
        // aborts the loop rather than merely being read.
        private static FixtureCase BuildCancellationCase()
        {
            FakeResolver resolver = new FakeResolver();
            resolver.Add("C:\\StartMenu\\GameA.lnk", "C:\\Games\\A\\a.exe", true);
            resolver.Add("C:\\StartMenu\\GameB.lnk", "C:\\Games\\B\\b.exe", true);
            resolver.Add("C:\\StartMenu\\GameC.lnk", "C:\\Games\\C\\c.exe", true);

            CancelAfter cancelAfter = new CancelAfter(1);

            return new FixtureCase(
                "Cancellation stops the scan before later shortcuts are processed",
                Paths("C:\\StartMenu\\GameA.lnk", "C:\\StartMenu\\GameB.lnk", "C:\\StartMenu\\GameC.lnk"),
                resolver, new Func<bool>(cancelAfter.IsCancelled),
                "report:GameA=C:\\Games\\A\\a.exe[Guessed]");
        }

        private static string WindowsExecutablePath(string fileName)
        {
            string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return System.IO.Path.Combine(windowsDirectory, fileName);
        }

        private static List<string> Paths(params string[] shortcutPaths)
        {
            return new List<string>(shortcutPaths);
        }

        // ------------------------------------------------------------------ harness

        // "The fake must stay a simple mapping" - a Dictionary and nothing else. A shortcut path
        // absent from it is exactly what a corrupt or unreadable .lnk looks like to
        // StartMenuShortcutSource: IShortcutResolver.Resolve returning null.
        private class FakeResolver : IShortcutResolver
        {
            private readonly Dictionary<string, ShortcutTarget> _targets =
                new Dictionary<string, ShortcutTarget>(StringComparer.OrdinalIgnoreCase);

            public void Add(string shortcutPath, string executablePath, bool targetExists)
            {
                ShortcutTarget target = new ShortcutTarget();
                target.ExecutablePath = executablePath;
                target.TargetExists = targetExists;
                _targets[shortcutPath] = target;
            }

            public ShortcutTarget Resolve(string shortcutPath)
            {
                ShortcutTarget target;
                return _targets.TryGetValue(shortcutPath, out target) ? target : null;
            }
        }

        // Returns false exactly once per allowedChecks, then true forever - so the Nth
        // context.IsCancelled read (N = allowedChecks + 1) is the first one to see cancellation.
        private class CancelAfter
        {
            private readonly int _allowedChecks;
            private int _checkCount;

            public CancelAfter(int allowedChecks)
            {
                _allowedChecks = allowedChecks;
            }

            public bool IsCancelled()
            {
                _checkCount++;
                return _checkCount > _allowedChecks;
            }
        }

        // Captures everything a GameScanContext handed to StartMenuShortcutSource would report,
        // skip or error, and reduces it to one comparable line - same "one string, one comparison"
        // shape ExecutablePickerFixture already uses for Select(...).
        private class ScanRecorder
        {
            private readonly Func<bool> _isCancelled;
            private readonly List<string> _parts = new List<string>();

            public ScanRecorder(Func<bool> isCancelled)
            {
                _isCancelled = isCancelled;
            }

            public GameScanContext CreateContext()
            {
                return new GameScanContext(_isCancelled, OnReport, OnSkipped, OnError);
            }

            public string Summarize()
            {
                return _parts.Count == 0 ? "(nothing)" : string.Join("; ", _parts.ToArray());
            }

            private void OnReport(GameCandidate candidate)
            {
                _parts.Add(string.Format("report:{0}={1}[{2}]",
                    candidate.GameName, candidate.ExecutablePath, candidate.Confidence));
            }

            private void OnSkipped(string gameName, string reason)
            {
                _parts.Add("skip:" + reason);
            }

            private void OnError(string sourceName, Exception ex)
            {
                _parts.Add("error:" + sourceName);
            }
        }

        private class FixtureCase
        {
            public FixtureCase(string name, List<string> shortcutPaths, IShortcutResolver resolver,
                               Func<bool> isCancelled, string expected)
            {
                Name = name;
                ShortcutPaths = shortcutPaths;
                Resolver = resolver;
                IsCancelled = isCancelled;
                Expected = expected;
            }

            public readonly string Name;
            public readonly List<string> ShortcutPaths;
            public readonly IShortcutResolver Resolver;
            public readonly Func<bool> IsCancelled;
            public readonly string Expected;
        }
    }
}
