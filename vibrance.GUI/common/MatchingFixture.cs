using System;
using System.Collections.Generic;

namespace vibrance.GUI.common
{
    /// <summary>
    /// The reference expectations for foreground matching, as literal data. No GUI, no processes,
    /// no disk. Run by vibrance.GUI.exe --selftest-matching.
    ///
    /// Every failure this covers is silent. A directory that matches too much makes every window
    /// on the desktop look like a game and fires vibrance constantly; a directory that shadows a
    /// name replaces the level the user saved with one they never chose; and a prefix that is not
    /// anchored on a separator makes ...\common\Squad match ...\common\Squad 44. None of them
    /// report anything, so none of them can be caught by using the program - only by asserting.
    /// </summary>
    public static class MatchingFixture
    {
        private const string SteamRoot = @"E:\Steam\steamapps\common";
        private const string SquadDirectory = SteamRoot + @"\Squad";
        private const string Squad44Executable = SteamRoot + @"\Squad 44\Squad44.exe";
        private const string SquadExecutable = SteamRoot + @"\Squad\SquadGame.exe";
        private const string CounterStrikeDirectory = SteamRoot + @"\Counter-Strike Global Offensive";
        private const string Cs2Executable = CounterStrikeDirectory + @"\game\bin\win64\cs2.exe";
        private const string VconsoleExecutable = CounterStrikeDirectory + @"\game\bin\win64\vconsole2.exe";
        private const string BattleNetDirectory = @"E:\Battle.net";
        private const string DiabloDirectory = @"E:\Battle.net\Diablo IV";
        private const string DiabloExecutable = @"E:\Battle.net\Diablo IV\Diablo IV.exe";

        public static List<string> Run()
        {
            Checklist checklist = new Checklist();
            checklist.Lines.Add("vibranceGUI foreground matching self test");
            checklist.Lines.Add(string.Empty);

            CheckContainment(checklist);
            CheckPrecedence(checklist);
            CheckSharedDirectories(checklist);

            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add(string.Format("PASSED {0}/{1}", checklist.Passed, checklist.Total));
            return checklist.Lines;
        }

        // Rule 2 on its own: is this image path below this install directory?
        private static void CheckContainment(Checklist checklist)
        {
            checklist.Lines.Add("Install directory containment - a prefix anchored on a separator:");

            checklist.Check(!Contains(SquadDirectory, Squad44Executable),
                @"...\Squad does not match ...\Squad 44\Squad44.exe");
            checklist.Check(!Contains(SquadDirectory + @"\", Squad44Executable),
                @"...\Squad\ stored with a separator does not match it either");
            checklist.Check(Contains(SquadDirectory, SquadExecutable),
                @"...\Squad matches ...\Squad\SquadGame.exe");
            checklist.Check(Contains(SquadDirectory + @"\", SquadExecutable),
                @"...\Squad\ matches it too, a stored trailing separator changes nothing");
            checklist.Check(Contains(CounterStrikeDirectory, Cs2Executable),
                "a match survives any depth of subfolder");
            checklist.Check(Contains(CounterStrikeDirectory.ToUpperInvariant(), Cs2Executable.ToLowerInvariant()),
                "case is ignored, as Windows paths require");
            checklist.Check(!Contains(SquadDirectory, SquadDirectory),
                "a directory is not a file below itself");
            checklist.Check(!Contains(SquadDirectory, @"D:\backup\E\Steam\steamapps\common\Squad\SquadGame.exe"),
                "a substring that is not a prefix does not match");

            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add("Directories that must match nothing rather than everything:");

            checklist.Check(!Contains(null, Cs2Executable), "a null install directory");
            checklist.Check(!Contains(string.Empty, Cs2Executable), "an empty install directory");
            checklist.Check(!Contains("   ", Cs2Executable), "a whitespace install directory");
            checklist.Check(!Contains(@"C:\", @"C:\Windows\explorer.exe"), @"the drive root C:\");
            checklist.Check(!Contains("C:", @"C:\Windows\explorer.exe"), "the drive without its root");
            checklist.Check(!Contains(@"\\server\share", @"\\server\share\Game\game.exe"),
                @"the share root \\server\share");
            checklist.Check(Contains(@"\\server\share\Game", @"\\server\share\Game\game.exe"),
                @"but a folder below the share matches normally");
            checklist.Check(!Contains(SquadDirectory, null),
                "an image path Windows would not hand out matches no directory");
        }

        // Rule 1 against rule 2. An exact name is what the user typed or confirmed; a directory is
        // an inference, and must never outrank one.
        private static void CheckPrecedence(Checklist checklist)
        {
            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add("Precedence - an exact name beats a directory wherever either sits in the list:");

            ApplicationSetting guessed = Setting("vconsole2", CounterStrikeDirectory);
            ApplicationSetting byHand = Setting("cs2", null);

            checklist.Check(FindMatch(List(guessed, byHand), "cs2", Cs2Executable) == byHand,
                "the hand added entry wins even though the guessed one comes first");
            checklist.Check(FindMatch(List(byHand, guessed), "cs2", Cs2Executable) == byHand,
                "and wins in the other order, so saving a dialog cannot change the winner");
            checklist.Check(FindMatch(List(guessed), "vconsole2", VconsoleExecutable) == guessed,
                "a wrongly guessed executable still matches by its directory, which is the point");
            checklist.Check(FindMatch(List(guessed), "start_protected_game",
                    CounterStrikeDirectory + @"\game\bin\win64\start_protected_game.exe") == guessed,
                "so does an anti cheat shim under the same directory");
            checklist.Check(FindMatch(List(guessed), "cs2", null) == null,
                "with no image path and no matching name, nothing matches");

            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add("Among directories the longest match wins:");

            ApplicationSetting launcher = Setting("Battle.net", BattleNetDirectory);
            ApplicationSetting diablo = Setting("Diablo IV Launcher", DiabloDirectory);

            checklist.Check(FindMatch(List(launcher, diablo), "Diablo IV", DiabloExecutable) == diablo,
                "the game beats its launcher's parent directory, listed first");
            checklist.Check(FindMatch(List(diablo, launcher), "Diablo IV", DiabloExecutable) == diablo,
                "and listed second - both are real registry InstallLocation values");
            checklist.Check(FindMatch(List(launcher, diablo), "Battle.net", @"E:\Battle.net\Battle.net.exe") == launcher,
                "a file directly under the parent still matches the parent");

            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add("Name matching, unchanged, for entries that carry no install directory:");

            ApplicationSetting manual = Setting("SquadGame", null);
            checklist.Check(FindMatch(List(manual), "SquadGame", @"C:\Elsewhere\SquadGame.exe") == manual,
                "a manually added entry matches on its name alone");
            checklist.Check(FindMatch(List(manual), "squadgame", null) == manual,
                "case insensitively, and with no image path at all - the elevated game case");
            checklist.Check(FindMatch(List(manual), "explorer", @"C:\Windows\explorer.exe") == null,
                "and matches nothing else");
            checklist.Check(FindMatch(List(Setting("ghost", string.Empty)), "explorer", @"C:\Windows\explorer.exe") == null,
                "an entry with a blank install directory matches no foreground process");
            checklist.Check(FindMatch(new List<ApplicationSetting>(), "explorer", @"C:\Windows\explorer.exe") == null,
                "an empty settings list matches nothing");
            checklist.Check(FindMatch(null, "explorer", @"C:\Windows\explorer.exe") == null,
                "and neither does a null one");

            ApplicationSetting unconfirmed = Setting("vconsole2", CounterStrikeDirectory);
            unconfirmed.IsExecutableUnconfirmed = true;
            checklist.Check(ApplicationSettingMatcher.FindMatch(List(byHand, unconfirmed), "cs2", Cs2Executable,
                    OnlyUnconfirmed) == unconfirmed,
                "the filtered overload skips confirmed entries, as the (?) marker needs");
        }

        // Checked when a setting is saved, never on the foreground path.
        private static void CheckSharedDirectories(Checklist checklist)
        {
            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add("Directories too broad to store as a game's install directory:");

            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            checklist.Check(ApplicationSettingMatcher.IsSharedProgramDirectory(programFilesX86),
                "Program Files (x86) is rejected: " + programFilesX86);
            checklist.Check(ApplicationSettingMatcher.IsSharedProgramDirectory(programFilesX86 + @"\"),
                "and is still rejected with a trailing separator");
            checklist.Check(!ApplicationSettingMatcher.IsSharedProgramDirectory(programFilesX86 + @"\Some Game"),
                "a game installed below it is kept");
            checklist.Check(!ApplicationSettingMatcher.IsSharedProgramDirectory(CounterStrikeDirectory),
                "an ordinary Steam install directory is kept");
            checklist.Check(!ApplicationSettingMatcher.IsSharedProgramDirectory(null),
                "a null directory is not reported as shared, it is simply absent");
        }

        private static readonly Func<ApplicationSetting, bool> OnlyUnconfirmed =
            delegate(ApplicationSetting setting) { return setting.IsExecutableUnconfirmed; };

        private static bool Contains(string directory, string imagePath)
        {
            return ApplicationSettingMatcher.IsUnderDirectory(directory, imagePath);
        }

        private static ApplicationSetting FindMatch(List<ApplicationSetting> settings, string processName,
            string processImagePath)
        {
            return ApplicationSettingMatcher.FindMatch(settings, processName, processImagePath);
        }

        private static List<ApplicationSetting> List(params ApplicationSetting[] settings)
        {
            return new List<ApplicationSetting>(settings);
        }

        private static ApplicationSetting Setting(string name, string installDirectory)
        {
            ApplicationSetting setting = new ApplicationSetting();
            setting.Name = name;
            setting.InstallDirectory = installDirectory;
            return setting;
        }

        private class Checklist
        {
            public readonly List<string> Lines = new List<string>();
            public int Passed;
            public int Total;

            public void Check(bool condition, string description)
            {
                Total++;
                if (condition)
                    Passed++;
                Lines.Add(string.Format("[{0}] {1}", condition ? "PASS" : "FAIL", description));
            }
        }
    }
}
