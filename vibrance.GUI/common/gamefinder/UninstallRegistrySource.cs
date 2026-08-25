using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace vibrance.GUI.common.gamefinder
{
    /// <summary>
    /// The Windows Uninstall registry, filtered to a publisher allowlist. One source covers EA,
    /// Battle.net, Rockstar and every other allowlisted publisher, because each of those entries
    /// names its executable outright in DisplayIcon. Emits ExecutableConfidence.Known.
    /// Generic Uninstall scanning is too noisy to be a game finder - it catches every piece of
    /// software on the machine. The publisher allowlist is what makes it precise: 57 distinct
    /// publishers on the machine this was measured on narrow to 9 entries, and the launcher and
    /// installer entries among those are removed by the name of the executable they point at.
    /// Like EpicLibrarySource this source is TOLD its executable and never infers one: a
    /// DisplayIcon that is absent, that names something other than an .exe, or that names a file
    /// which is not on disk, is a skip. Falling back to ExecutablePicker would produce a row
    /// labelled "From store" whose executable was actually a guess, which is the one failure the
    /// user has no way to detect.
    /// </summary>
    public class UninstallRegistrySource : IGameLibrarySource
    {
        private const string SourceName = "Installed games";

        private const string UninstallSubKey = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall";

        private const string DisplayNameValueName = "DisplayName";
        private const string PublisherValueName = "Publisher";
        private const string DisplayIconValueName = "DisplayIcon";
        private const string InstallLocationValueName = "InstallLocation";
        private const string SystemComponentValueName = "SystemComponent";

        private const string ExecutableExtension = ".exe";

        // This source's fixed skip reasons, alongside the four the Steam and Epic sources own.
        // They are what a user's vibranceGUI.log gets grepped for when they report "my game isn't
        // listed", so they are literals here rather than composed prose.
        private const string SkipSystemComponent = "system component";
        private const string SkipNoExecutableNamed = "no executable named";
        private const string SkipExecutableNotFound = "named executable not found";
        private const string SkipExcludedExecutable = "excluded executable";
        private const string SkipLauncherExecutable = "launcher executable";

        // The maintained artefact of this source, and the only place the list lives. Matched
        // case-insensitively for exact equality against the trimmed Publisher value.
        // Valve is deliberately absent: Steam games come from appmanifests, and Valve's only
        // Uninstall entry is the Steam client itself.
        private static readonly string[] AllowedPublishers =
        {
            "Electronic Arts", "EA Games", "Blizzard Entertainment", "Rockstar Games",
            "Ubisoft", "Ubisoft Entertainment", "CD PROJEKT RED", "Bethesda Softworks",
            "Bethesda Game Studios", "Square Enix", "Warner Bros. Games", "2K Games",
            "Paradox Interactive", "Devolver Digital"
        };

        // Every allowlisted publisher also ships the launcher its games run under, and each of
        // those has an Uninstall entry of its own pointing at a real executable that passes every
        // other test. A launcher in the watch list is worse than a missing game: its profile
        // activates the moment the user opens the store, not the game.
        private static readonly string[] LauncherFileNames =
        {
            "Battle.net.exe", "Launcher.exe", "EADesktop.exe", "EpicGamesLauncher.exe",
            "Steam.exe", "upc.exe", "GalaxyClient.exe"
        };

        // HKLM\SOFTWARE is registry-redirected, so its 64-bit and 32-bit views hold different sets
        // of entries and both have to be read - this process is x86, so relying on WOW64
        // redirection would only ever reach the 32-bit one, and on the machine this was measured
        // on the EA app's hidden entry lives in the 64-bit view alone.
        // HKCU\SOFTWARE is NOT redirected: opening it in both views would enumerate the same
        // entries twice. Registry64 against a 32-bit OS falls back to the only view there is.
        private static readonly RegistryRoot[] Roots =
        {
            new RegistryRoot(RegistryHive.LocalMachine, RegistryView.Registry64),
            new RegistryRoot(RegistryHive.LocalMachine, RegistryView.Registry32),
            new RegistryRoot(RegistryHive.CurrentUser, RegistryView.Registry64)
        };

        private class RegistryRoot
        {
            public readonly RegistryHive Hive;
            public readonly RegistryView View;

            public RegistryRoot(RegistryHive hive, RegistryView view)
            {
                Hive = hive;
                View = view;
            }
        }

        public string DisplayName
        {
            get { return SourceName; }
        }

        // Cheap by contract: it opens the Uninstall key and stops at the first root that has one,
        // which on Windows is always the first. It never enumerates the subkeys - that is the
        // expensive half and it belongs to Scan.
        public bool IsAvailable()
        {
            for (int i = 0; i < Roots.Length; i++)
            {
                RegistryKey baseKey = null;
                RegistryKey uninstallKey = null;

                try
                {
                    baseKey = RegistryKey.OpenBaseKey(Roots[i].Hive, Roots[i].View);
                    uninstallKey = baseKey.OpenSubKey(UninstallSubKey);
                    if (uninstallKey != null)
                        return true;
                }
                catch (Exception)
                {
                    // A locked-down machine can refuse the open outright. The next root gets its
                    // turn; only a machine where none of the three opens is unavailable.
                }
                finally
                {
                    if (uninstallKey != null)
                        uninstallKey.Close();
                    if (baseKey != null)
                        baseKey.Close();
                }
            }

            return false;
        }

        public void Scan(GameScanContext context)
        {
            if (context == null)
                return;

            try
            {
                // Held across the roots, not per root: an installer that writes the same game to
                // more than one of them would otherwise have it reported twice. GameFinderScanner
                // de-duplicates between sources; this is the same job within one.
                HashSet<string> reportedExecutablePaths =
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < Roots.Length; i++)
                {
                    if (context.IsCancelled)
                        return;

                    ScanRoot(Roots[i], reportedExecutablePaths, context);
                }
            }
            catch (Exception ex)
            {
                // Scan is contractually forbidden from throwing. Everything below handles its own
                // failures; this is the backstop for the ones it cannot foresee.
                context.ReportError(SourceName, ex);
            }
        }

        // ------------------------------------------------------------------ one root

        private void ScanRoot(RegistryRoot root, HashSet<string> reportedExecutablePaths,
                              GameScanContext context)
        {
            RegistryKey baseKey = null;
            RegistryKey uninstallKey = null;

            try
            {
                try
                {
                    baseKey = RegistryKey.OpenBaseKey(root.Hive, root.View);
                    uninstallKey = baseKey.OpenSubKey(UninstallSubKey);
                }
                catch (Exception)
                {
                    // Not reported as an error: a root this process may not read is a fact about
                    // the machine, not a failure of the scan, and the other roots still run.
                    return;
                }

                if (uninstallKey == null)
                    return;

                string[] entryNames;
                try
                {
                    entryNames = uninstallKey.GetSubKeyNames();
                }
                catch (Exception ex)
                {
                    context.ReportError(SourceName, ex);
                    return;
                }

                // GetSubKeyNames order is registry-internal; sorting makes the reported order the
                // same on every machine, which is what makes a scan comparable against the last.
                Array.Sort(entryNames, StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < entryNames.Length; i++)
                {
                    if (context.IsCancelled)
                        return;

                    ScanEntry(uninstallKey, entryNames[i], reportedExecutablePaths, context);
                }
            }
            finally
            {
                if (uninstallKey != null)
                    uninstallKey.Close();
                if (baseKey != null)
                    baseKey.Close();
            }
        }

        // ------------------------------------------------------------------ one entry

        private void ScanEntry(RegistryKey uninstallKey, string entryName,
                               HashSet<string> reportedExecutablePaths, GameScanContext context)
        {
            RegistryKey entry = null;

            try
            {
                entry = uninstallKey.OpenSubKey(entryName);
                if (entry == null)
                    return;   // uninstalled between the enumeration and now

                // The publisher test comes first and costs one string read. Everything after it
                // touches the disk, and this is a key with hundreds of entries under it: on the
                // machine this was measured on, 359 entries across the roots reach 9.
                string publisher = ReadTrimmedValue(entry, PublisherValueName);
                if (!IsAllowedPublisher(publisher))
                    return;   // not a skip: an allowlist miss is the normal case, not a near miss

                // GameName is the store's own display name and carries whatever Unicode the
                // publisher put in it - the trademark sign in Battlefield's name reaches the row
                // label unmangled. The Uninstall key name is a last resort that exists only so a
                // skip can name the thing it skipped; it is usually a GUID and reads poorly.
                string gameName = ReadTrimmedValue(entry, DisplayNameValueName);
                if (gameName.Length == 0)
                    gameName = entryName;
                if (gameName.Length == 0)
                    return;

                if (IsSystemComponent(entry))
                {
                    context.ReportSkipped(gameName, SkipSystemComponent);
                    return;
                }

                string executablePath =
                    ResolveDisplayIcon(ReadTrimmedValue(entry, DisplayIconValueName));
                if (executablePath == null)
                {
                    // The store named nothing usable. Ranking the install folder for a replacement
                    // would turn a Known row into a Guessed one wearing a Known label.
                    context.ReportSkipped(gameName, SkipNoExecutableNamed);
                    return;
                }

                // Both name tests run before the disk is touched: they are the ones that remove
                // the launchers and installers, and those files do exist, so File.Exists would
                // pass them through and the skip would then report a less honest reason.
                string fileName = SafeGetFileName(executablePath);
                if (IsExcludedFileName(fileName))
                {
                    context.ReportSkipped(gameName, SkipExcludedExecutable);
                    return;
                }

                if (IsLauncherFileName(fileName))
                {
                    context.ReportSkipped(gameName, SkipLauncherExecutable);
                    return;
                }

                if (!File.Exists(executablePath))
                {
                    // The entry outlived the install, or DisplayIcon names a drive that is not
                    // mounted at the moment.
                    context.ReportSkipped(gameName, SkipExecutableNotFound);
                    return;
                }

                if (!reportedExecutablePaths.Add(executablePath))
                    return;   // the same game reached under a second root

                GameCandidate candidate = new GameCandidate();
                candidate.Source = MapPublisherToSource(publisher);
                candidate.SourceAppId = entryName;
                candidate.GameName = gameName;
                candidate.InstallDirectory =
                    ResolveInstallDirectory(ReadTrimmedValue(entry, InstallLocationValueName),
                                            executablePath);
                candidate.ExecutablePath = executablePath;
                // DisplayIcon named the executable outright, so this row is not a guess and the
                // main list gives it no "(?)" marker.
                candidate.Confidence = ExecutableConfidence.Known;
                candidate.Icon = TryExtractIcon(executablePath);

                context.Report(candidate);
            }
            catch (Exception ex)
            {
                // An unreadable or corrupt entry costs one game, never the rest of the root.
                context.ReportError(SourceName, ex);
            }
            finally
            {
                if (entry != null)
                    entry.Close();
            }
        }

        private static string ReadTrimmedValue(RegistryKey entry, string valueName)
        {
            // GetValue expands a REG_EXPAND_SZ by default, which is how an entry written as
            // "%ProgramFiles%\..." arrives here as a path that can be probed.
            string value = entry.GetValue(valueName) as string;
            return value == null ? string.Empty : value.Trim();
        }

        // ------------------------------------------------------------------ the filters

        private static bool IsAllowedPublisher(string publisher)
        {
            if (publisher.Length == 0)
                return false;

            for (int i = 0; i < AllowedPublishers.Length; i++)
            {
                if (string.Equals(publisher, AllowedPublishers[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        // SystemComponent = 1 hides an entry from Programs and Features. Nothing a user installs
        // in order to play it sets that; the EA app's own entry does.
        private static bool IsSystemComponent(RegistryKey entry)
        {
            object value = entry.GetValue(SystemComponentValueName);
            if (value == null)
                return false;

            try
            {
                // REG_DWORD arrives as int, but the value is written by installers and a string
                // "1" is not unheard of. A REG_BINARY throws and is treated as not set.
                return Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The same globs the Steam picker filters with, not a second copy of them. "*install*"
        // alone removes EAappInstaller.exe and uninstallRGSCRedistributable.exe.
        private static bool IsExcludedFileName(string fileName)
        {
            if (fileName.Length == 0)
                return false;

            foreach (string glob in ExecutableRules.ExcludedFileNameGlobs)
            {
                if (ExecutableRules.MatchesGlob(fileName, glob))
                    return true;
            }

            return false;
        }

        private static bool IsLauncherFileName(string fileName)
        {
            for (int i = 0; i < LauncherFileNames.Length; i++)
            {
                if (string.Equals(fileName, LauncherFileNames[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        // The Store column is rendered per candidate, so the publisher decides what the user reads
        // in it. Anything allowlisted that is not one of the four named launchers says "Installed",
        // which is true and claims nothing further.
        private static GameSource MapPublisherToSource(string publisher)
        {
            if (string.Equals(publisher, "Electronic Arts", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(publisher, "EA Games", StringComparison.OrdinalIgnoreCase))
                return GameSource.Ea;

            if (string.Equals(publisher, "Blizzard Entertainment", StringComparison.OrdinalIgnoreCase))
                return GameSource.BattleNet;

            if (string.Equals(publisher, "Rockstar Games", StringComparison.OrdinalIgnoreCase))
                return GameSource.Rockstar;

            if (string.Equals(publisher, "Ubisoft", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(publisher, "Ubisoft Entertainment", StringComparison.OrdinalIgnoreCase))
                return GameSource.Ubisoft;

            return GameSource.OtherLauncher;
        }

        // ------------------------------------------------------------------ DisplayIcon

        // DisplayIcon is a shell icon reference, not a path. It was measured on this machine both
        // bare (Diablo IV) and wrapped in double quotes (RDR2, BF6, FC26), on entries sitting
        // beside each other, and it may carry a trailing ",<index>" naming which icon inside the
        // file to use (EAappInstaller.exe,0). Returns null for anything that is not a rooted path
        // to an .exe; the caller turns that into a skip and never into a guess.
        private static string ResolveDisplayIcon(string displayIcon)
        {
            if (displayIcon.Length == 0)
                return null;

            // Unquote, strip the index, then unquote again: the index is written both inside the
            // quotes and after them, so one pass in either order alone leaves one form intact.
            string path = StripSurroundingQuotes(StripIconIndex(StripSurroundingQuotes(displayIcon)));
            if (path.Length == 0)
                return null;

            // A .ico, a .dll or the uninstaller's own resource is not something this source can
            // start, and there is nothing else in the entry that names the game's executable.
            if (!path.EndsWith(ExecutableExtension, StringComparison.OrdinalIgnoreCase))
                return null;

            // Path.GetFullPath resolves a relative path against the current directory, which would
            // quietly manufacture an absolute path to a file that has nothing to do with the game.
            // Every DisplayIcon the shell can use is rooted.
            if (!IsRootedPath(path))
                return null;

            try
            {
                // Canonical form matters beyond tidiness: GameFinderScanner de-duplicates by
                // ExecutablePath with OrdinalIgnoreCase, so a path carrying ".." or a stray
                // separator would compare unequal to the same file reached from another source.
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string StripSurroundingQuotes(string value)
        {
            string trimmed = value.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"')
                return trimmed.Substring(1, trimmed.Length - 2).Trim();

            return trimmed;
        }

        // Only a comma followed by an optional sign and then digits to the end of the string is an
        // icon index. A directory name is allowed to contain a comma, so stripping from the last
        // comma unconditionally would truncate such a path into one that does not exist - and this
        // source reports what DisplayIcon names or nothing at all, so that would be a lost game.
        private static string StripIconIndex(string path)
        {
            int comma = path.LastIndexOf(',');
            if (comma < 0)
                return path;

            int index = comma + 1;
            if (index < path.Length && (path[index] == '-' || path[index] == '+'))
                index++;

            if (index >= path.Length)
                return path;   // a trailing comma, or a bare sign: not an index

            for (int i = index; i < path.Length; i++)
            {
                if (!char.IsDigit(path[i]))
                    return path;
            }

            return path.Substring(0, comma);
        }

        private static bool IsRootedPath(string path)
        {
            try
            {
                return Path.IsPathRooted(path);
            }
            catch (ArgumentException)
            {
                // Invalid path characters. Path.GetFullPath would throw on the same string.
                return false;
            }
        }

        // ------------------------------------------------------------------ paths and icons

        // InstallLocation is optional and was measured both empty (the EA app entries, the Rockstar
        // redistributable) and carrying a trailing separator (E:\EA Games\Battlefield 6\). It is
        // persisted as ApplicationSetting.InstallDirectory, which section 3.1 defines as a full
        // path with no trailing separator; when the entry gives none that is usable, the folder the
        // store's own executable sits in is the honest answer rather than a blank.
        private static string ResolveInstallDirectory(string installLocation, string executablePath)
        {
            string normalized = NormalizeDirectoryPath(installLocation);
            if (normalized != null && Directory.Exists(normalized))
                return normalized;

            try
            {
                return Path.GetDirectoryName(executablePath);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string NormalizeDirectoryPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;

            try
            {
                string fullPath = Path.GetFullPath(path.Trim());

                // "C:\" is three characters and its separator is part of the root, not a trailer.
                if (fullPath.Length > 3)
                    fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                return fullPath.Length == 0 ? null : fullPath;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string SafeGetFileName(string path)
        {
            try
            {
                return Path.GetFileName(path) ?? string.Empty;
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
        }

        // Extracted on the worker thread. ExtractAssociatedIcon throws on a path it dislikes and
        // returns null often enough that both have to be survivable: a missing icon must never
        // cost the user the game.
        private static Icon TryExtractIcon(string executablePath)
        {
            try
            {
                return Icon.ExtractAssociatedIcon(executablePath);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
