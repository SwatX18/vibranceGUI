using System;
using System.Collections.Generic;
using System.IO;

namespace vibrance.GUI.common
{
    /// <summary>
    /// Decides which ApplicationSetting, if any, the process in the foreground belongs to.
    ///
    /// The executable name alone is not enough. For a Steam title the game finder has to guess
    /// which of the executables in the install folder is the game, and a wrong guess meant the
    /// profile silently never activated - no error, no clue. A wrong guess still sits in the right
    /// folder though, so a process running from under the install directory is the same game
    /// either way; and so is the launcher or the anti cheat shim that Windows reports in the
    /// foreground instead of the game itself.
    ///
    /// The two rules are deliberately not OR'd per setting. A name match is exact and is what the
    /// user typed or confirmed; a directory match is an inference. Testing them together would let
    /// whichever entry came first in the list win, and the list order is not stable - saving the
    /// settings dialog removes an entry and appends it. An entry the finder guessed wrong could
    /// then shadow the user's own entry for the same game, silently, and only after an unrelated
    /// edit. So: every name is tried first, and the directories only if no name matched.
    ///
    /// Runs on the ui thread inside the foreground callback: string comparisons over a handful of
    /// settings, no allocation, no disk access.
    /// </summary>
    internal static class ApplicationSettingMatcher
    {
        /// <summary>
        /// The setting the foreground process belongs to, or null.
        /// An exact name match anywhere in the list beats a directory match anywhere in the list.
        /// Among directories the longest match wins, so an entry for ...\Battle.net\Diablo IV is
        /// not shadowed by one for ...\Battle.net, which the registry really does hand out as an
        /// install location of its own.
        /// </summary>
        /// <param name="processImagePath">
        /// The full image path of the foreground process, or null when Windows would not give it
        /// out - an elevated or protected game. Null simply leaves the second pass with nothing to
        /// match, and the name pass, which is all the old build ever did, still stands.
        /// </param>
        public static ApplicationSetting FindMatch(List<ApplicationSetting> settings, string processName,
            string processImagePath)
        {
            return FindMatch(settings, processName, processImagePath, null);
        }

        public static ApplicationSetting FindMatch(List<ApplicationSetting> settings, string processName,
            string processImagePath, Func<ApplicationSetting, bool> filter)
        {
            if (settings == null)
            {
                return null;
            }

            for (int i = 0; i < settings.Count; i++)
            {
                ApplicationSetting setting = settings[i];
                if ((filter == null || (setting != null && filter(setting))) && NameMatches(setting, processName))
                {
                    return setting;
                }
            }

            ApplicationSetting best = null;
            int bestLength = 0;
            for (int i = 0; i < settings.Count; i++)
            {
                ApplicationSetting setting = settings[i];
                if (filter != null && (setting == null || !filter(setting)))
                {
                    continue;
                }

                int length = MatchedDirectoryLength(setting, processImagePath);
                if (length > bestLength)
                {
                    best = setting;
                    bestLength = length;
                }
            }

            return best;
        }

        /// <summary>
        /// Today's rule, unchanged and still exact: an entry added by hand carries no install
        /// directory and matches on nothing else.
        /// </summary>
        public static bool NameMatches(ApplicationSetting setting, string processName)
        {
            return setting != null &&
                   !string.IsNullOrEmpty(processName) &&
                   string.Equals(setting.Name, processName, StringComparison.OrdinalIgnoreCase);
        }

        public static bool DirectoryMatches(ApplicationSetting setting, string processImagePath)
        {
            return MatchedDirectoryLength(setting, processImagePath) > 0;
        }

        /// <summary>
        /// True when the image path names a file below the directory. A prefix test, never a
        /// substring one, and the directory is compared as if it ended in a separator: without
        /// that, an install directory of ...\common\Squad also matches ...\common\Squad 44\.
        /// </summary>
        public static bool IsUnderDirectory(string directory, string imagePath)
        {
            return MatchedLength(directory, imagePath) > 0;
        }

        /// <summary>
        /// The number of characters of the setting's install directory that the image path matched,
        /// which is how the longest match is picked, or 0 for no match.
        /// </summary>
        public static int MatchedDirectoryLength(ApplicationSetting setting, string processImagePath)
        {
            return setting == null ? 0 : MatchedLength(setting.InstallDirectory, processImagePath);
        }

        /// <summary>
        /// Directories that hold every application on the machine rather than one game. Stored as
        /// an install directory, any of them would match nearly every process that goes foreground.
        /// Uninstall registry entries are free to name one: InstallLocation is whatever the
        /// installer wrote there. Checked once when a setting is saved, never on the foreground
        /// path, and an entry that hits this keeps working by name.
        /// </summary>
        public static bool IsSharedProgramDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            //ProgramFiles reads as "Program Files (x86)" in a 32 bit process, which vibranceGUI
            //always is, so the 64 bit one has to be asked for by its environment variable
            if (IsSameDirectory(directory, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) ||
                IsSameDirectory(directory, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)) ||
                IsSameDirectory(directory, Environment.GetEnvironmentVariable("ProgramW6432")))
            {
                return true;
            }

            string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return IsSameDirectory(directory, localApplicationData) ||
                   (!string.IsNullOrEmpty(localApplicationData) &&
                    IsSameDirectory(directory, localApplicationData + Path.DirectorySeparatorChar + "Programs"));
        }

        /// <summary>
        /// Path equality for directories: case insensitive, and blind to a trailing separator.
        /// </summary>
        public static bool IsSameDirectory(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            {
                return false;
            }

            int leftLength = TrimmedLength(left);
            int rightLength = TrimmedLength(right);
            return leftLength > 0 &&
                   leftLength == rightLength &&
                   string.Compare(left, 0, right, 0, leftLength, StringComparison.OrdinalIgnoreCase) == 0;
        }

        private static int MatchedLength(string directory, string imagePath)
        {
            //a blank install directory has to match nothing. Every manually added entry has one,
            //and matching everything here would fire vibrance on every window the user touches
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrEmpty(imagePath))
            {
                return 0;
            }

            int length = TrimmedLength(directory);

            //the "C:" left of "C:\" would prefix match every process on the drive. A registry
            //InstallLocation is allowed to say exactly that, so this is reachable, and it is the
            //blank directory failure wearing a different hat
            if (IsRootOnly(directory, length))
            {
                return 0;
            }

            //the character where the directory's separator would have been has to be one, or
            //...\Squad still matches ...\Squad 44\game.exe
            if (imagePath.Length <= length || !IsSeparator(imagePath[length]))
            {
                return 0;
            }

            return string.Compare(imagePath, 0, directory, 0, length, StringComparison.OrdinalIgnoreCase) == 0
                ? length
                : 0;
        }

        private static int TrimmedLength(string directory)
        {
            int length = directory.Length;
            while (length > 0 && IsSeparator(directory[length - 1]))
            {
                length--;
            }

            return length;
        }

        private static bool IsRootOnly(string directory, int length)
        {
            //a unc path opens with two separators of its own, so the first separator after them
            //ends the server name and only a second one puts anything below the share. Counting
            //from index 2 alone would read the share root \\server\share as a usable directory
            if (length >= 2 && IsSeparator(directory[0]) && IsSeparator(directory[1]))
            {
                int separators = 0;
                for (int i = 2; i < length; i++)
                {
                    if (IsSeparator(directory[i]) && ++separators == 2)
                    {
                        return false;
                    }
                }

                return true;
            }

            //index 2 steps over "C:", so what is looked for is a separator below the drive root
            for (int i = 2; i < length; i++)
            {
                if (IsSeparator(directory[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsSeparator(char value)
        {
            return value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;
        }
    }
}
