using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using Microsoft.Win32;

namespace vibrance.GUI.common.gamefinder
{
    /// <summary>
    /// Steam root discovery (multi-probe registry + fallbacks), libraryfolders.vdf,
    /// appmanifest_*.acf, the appid denylist, enumerate + pick. Emits
    /// ExecutableConfidence.Guessed.
    /// Listing steamapps\common\ directly is forbidden; it surfaces ghosts left by uninstalls.
    /// </summary>
    public class SteamLibrarySource : IGameLibrarySource
    {
        private const string SourceName = "Steam";

        private const string SteamSubKey = "SOFTWARE\\Valve\\Steam";
        private const string InstallPathValueName = "InstallPath";  // HKLM
        private const string SteamPathValueName = "SteamPath";      // HKCU, written with forward slashes
        private const string SteamFolderName = "Steam";
        private const string DefaultSteamRoot = "C:\\Steam";

        private const string SteamAppsFolderName = "steamapps";
        private const string CommonFolderName = "common";
        private const string LibraryFoldersFileName = "libraryfolders.vdf";
        private const string AppManifestSearchPattern = "appmanifest_*.acf";
        private const string LibraryFoldersRootName = "libraryfolders";
        private const string LibraryPathKeyName = "path";
        private const string AppStateRootName = "AppState";

        // Three of the four fixed skip reasons; the fourth belongs to the Epic source. They are
        // what a user's vibranceGUI.log gets grepped for when they report "my game isn't listed",
        // so they are literals here rather than composed prose.
        private const string SkipInstallDirectoryNotFound = "install directory not found";
        private const string SkipNoExecutableFound = "no executable found";
        private const string SkipExcludedAppId = "excluded app id";

        public string DisplayName
        {
            get { return SourceName; }
        }

        // Cheap by contract: a handful of registry reads and a Directory.Exists on the first
        // candidate root that resolves. It must never enumerate the filesystem.
        public bool IsAvailable()
        {
            return FindSteamRoot() != null;
        }

        public void Scan(GameScanContext context)
        {
            if (context == null)
                return;

            try
            {
                string steamRoot = FindSteamRoot();
                if (steamRoot == null)
                    return;   // Steam is not installed here; finding nothing is not a failure

                List<string> libraries = ReadLibraryPaths(steamRoot, context);

                for (int i = 0; i < libraries.Count; i++)
                {
                    if (context.IsCancelled)
                        return;

                    ScanLibrary(libraries[i], context);
                }
            }
            catch (Exception ex)
            {
                // Scan is contractually forbidden from throwing. Everything below handles its own
                // failures; this is the backstop for the ones it cannot foresee.
                context.ReportError(SourceName, ex);
            }
        }

        // ------------------------------------------------------------------ steam root discovery

        // First candidate that exists and contains a steamapps folder wins. Every probe is
        // individually guarded, so a machine with no Valve key at all falls through to the
        // literal paths rather than failing the whole source.
        private static string FindSteamRoot()
        {
            // The process is x86, so a plain Registry.LocalMachine read is redirected by WOW64
            // into WOW6432Node, which is empty on some machines while the unredirected 64-bit key
            // holds the answer. Both views are read, HKLM before HKCU.
            List<string> candidates = new List<string>();
            candidates.Add(ReadRegistryValue(RegistryHive.LocalMachine, RegistryView.Registry64, InstallPathValueName));
            candidates.Add(ReadRegistryValue(RegistryHive.LocalMachine, RegistryView.Registry32, InstallPathValueName));
            candidates.Add(ReadRegistryValue(RegistryHive.CurrentUser, RegistryView.Registry64, SteamPathValueName));
            candidates.Add(ReadRegistryValue(RegistryHive.CurrentUser, RegistryView.Registry32, SteamPathValueName));
            candidates.Add(CombinePath(Environment.GetEnvironmentVariable("ProgramFiles(x86)"), SteamFolderName));
            candidates.Add(CombinePath(Environment.GetEnvironmentVariable("ProgramFiles"), SteamFolderName));
            candidates.Add(DefaultSteamRoot);

            for (int i = 0; i < candidates.Count; i++)
            {
                string root = NormalizeDirectoryPath(candidates[i]);
                if (root == null)
                    continue;

                string steamApps = CombinePath(root, SteamAppsFolderName);
                if (steamApps != null && Directory.Exists(steamApps))
                    return root;
            }

            return null;
        }

        private static string ReadRegistryValue(RegistryHive hive, RegistryView view, string valueName)
        {
            RegistryKey baseKey = null;
            RegistryKey subKey = null;

            try
            {
                baseKey = RegistryKey.OpenBaseKey(hive, view);
                subKey = baseKey.OpenSubKey(SteamSubKey);
                if (subKey == null)
                    return null;

                return subKey.GetValue(valueName) as string;
            }
            catch (Exception)
            {
                // A 32-bit OS rejects RegistryView.Registry64 and a locked-down machine rejects
                // the read outright. Either way the next probe gets its turn.
                return null;
            }
            finally
            {
                if (subKey != null)
                    subKey.Close();
                if (baseKey != null)
                    baseKey.Close();
            }
        }

        // ------------------------------------------------------------------ libraries

        // The install root is always a library. Current Steam versions list it in
        // libraryfolders.vdf as entry "0" as well, but older files listed only the ADDITIONAL
        // libraries, and the file may be missing or unreadable altogether.
        private static List<string> ReadLibraryPaths(string steamRoot, GameScanContext context)
        {
            List<string> libraries = new List<string>();
            AddLibrary(libraries, steamRoot);

            string libraryFoldersPath =
                CombinePath(CombinePath(steamRoot, SteamAppsFolderName), LibraryFoldersFileName);
            if (libraryFoldersPath == null || !File.Exists(libraryFoldersPath))
                return libraries;

            string text;
            try
            {
                text = File.ReadAllText(libraryFoldersPath);
            }
            catch (Exception ex)
            {
                // Unreadable: the install root on its own still yields every game in the main
                // library, which is the one the user almost certainly meant.
                context.ReportError(SourceName, ex);
                return libraries;
            }

            VdfNode document = VdfTextReader.Parse(text);
            VdfNode folders = document.FindChild(LibraryFoldersRootName) ?? document;

            foreach (VdfNode entry in folders.Children)
            {
                if (entry == null)
                    continue;

                if (entry.Value != null)
                {
                    // Legacy shape: "0" "E:\\Steam". Bookkeeping keys such as TimeNextStatsReport
                    // and ContentStatsID sit beside those and are string-valued too, so only
                    // numerically named entries count as a library.
                    if (IsLibraryIndex(entry.Name))
                        AddLibrary(libraries, entry.Value);
                }
                else
                {
                    // Modern shape: "0" { "path" "E:\\Steam" "apps" { ... } }.
                    AddLibrary(libraries, entry.GetValue(LibraryPathKeyName));
                }
            }

            return libraries;
        }

        private static void AddLibrary(List<string> libraries, string path)
        {
            string normalized = NormalizeDirectoryPath(path);
            if (normalized == null)
                return;

            // The main library is reached twice on a current install: once as the root, once as
            // entry "0" of the vdf. Scanning it twice would report every game twice.
            for (int i = 0; i < libraries.Count; i++)
            {
                if (string.Equals(libraries[i], normalized, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            libraries.Add(normalized);
        }

        private static bool IsLibraryIndex(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            for (int i = 0; i < name.Length; i++)
            {
                if (!char.IsDigit(name[i]))
                    return false;
            }

            return true;
        }

        private void ScanLibrary(string libraryPath, GameScanContext context)
        {
            string steamAppsPath = CombinePath(libraryPath, SteamAppsFolderName);
            if (steamAppsPath == null || !Directory.Exists(steamAppsPath))
                return;   // a library on a drive that is not mounted at the moment

            string[] manifestPaths;
            try
            {
                // appmanifest_*.acf, and never a listing of steamapps\common\: that surfaces the
                // ghost folders an uninstall leaves behind (evidence Finding 9).
                manifestPaths = Directory.GetFiles(steamAppsPath, AppManifestSearchPattern);
            }
            catch (Exception ex)
            {
                context.ReportError(SourceName, ex);
                return;
            }

            // GetFiles order is filesystem-dependent; sorting makes the reported order the same
            // on every machine, which is what makes a scan comparable against the last one.
            Array.Sort(manifestPaths, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < manifestPaths.Length; i++)
            {
                if (context.IsCancelled)
                    return;

                ScanManifest(manifestPaths[i], steamAppsPath, context);
            }
        }

        // ------------------------------------------------------------------ one game

        private void ScanManifest(string manifestPath, string steamAppsPath, GameScanContext context)
        {
            try
            {
                VdfNode appState = ReadAppState(manifestPath);

                string appId = GetTrimmedValue(appState, "appid");
                if (appId.Length == 0)
                    appId = AppIdFromManifestFileName(manifestPath);

                string installDirectoryName = GetTrimmedValue(appState, "installdir");

                // GameName is the store's own display name, never the install directory
                // (Finding 3: Counter-Strike 2 installs into "Counter-Strike Global Offensive").
                // The install directory is a last resort that exists only so that a skip can name
                // the thing it skipped.
                string gameName = GetTrimmedValue(appState, "name");
                if (gameName.Length == 0)
                    gameName = installDirectoryName;
                if (gameName.Length == 0)
                    return;   // neither a name nor an installdir: nothing usable to report

                // Checked before any disk access, so 228980 costs one string comparison rather
                // than a walk of the redistributables it installs.
                if (IsExcludedAppId(appId))
                {
                    context.ReportSkipped(gameName, SkipExcludedAppId);
                    return;
                }

                string installDirectory =
                    CombinePath(CombinePath(steamAppsPath, CommonFolderName), installDirectoryName);
                if (installDirectory == null || !Directory.Exists(installDirectory))
                {
                    context.ReportSkipped(gameName, SkipInstallDirectoryNotFound);
                    return;
                }

                List<ExecutableCandidate> executables =
                    ExecutableEnumerator.Enumerate(installDirectory, delegate { return context.IsCancelled; });

                // A cancelled walk hands back whatever it had reached, which is not a measurement
                // of the game. Turning that into a skip or a guess would be a lie.
                if (context.IsCancelled)
                    return;

                ExecutableCandidate selected = ExecutablePicker.Select(executables);
                if (selected == null)
                {
                    context.ReportSkipped(gameName, SkipNoExecutableFound);
                    return;
                }

                GameCandidate candidate = new GameCandidate();
                candidate.Source = GameSource.Steam;
                candidate.SourceAppId = appId;
                candidate.GameName = gameName;
                candidate.InstallDirectory = installDirectory;
                candidate.ExecutablePath = selected.FullPath;
                // Steam exposes the launch executable nowhere convenient (Finding 7), so every
                // row this source produces is ranked out of the folder and marked as a guess.
                candidate.Confidence = ExecutableConfidence.Guessed;
                candidate.Icon = TryExtractIcon(selected.FullPath);

                context.Report(candidate);
            }
            catch (Exception ex)
            {
                // A malformed or unreadable manifest costs one game, never the rest of the
                // library. ExecutableEnumerator swallows its own per-directory failures, but
                // relying on that from out here would be bad manners.
                context.ReportError(SourceName, ex);
            }
        }

        private static VdfNode ReadAppState(string manifestPath)
        {
            string text = File.ReadAllText(manifestPath);
            VdfNode document = VdfTextReader.Parse(text);

            // A manifest is always wrapped in "AppState" { }. Tolerate one that is not rather
            // than losing the game over a missing wrapper.
            return document.FindChild(AppStateRootName) ?? document;
        }

        private static string GetTrimmedValue(VdfNode node, string name)
        {
            string value = node.GetValue(name);
            return value == null ? string.Empty : value.Trim();
        }

        // "appmanifest_730.acf" -> "730". Only reached when the manifest carries no appid of its
        // own, in which case the denylist still has to be able to match it.
        private static string AppIdFromManifestFileName(string manifestPath)
        {
            string fileName = Path.GetFileNameWithoutExtension(manifestPath);
            if (string.IsNullOrEmpty(fileName))
                return string.Empty;

            int underscore = fileName.IndexOf('_');
            return underscore < 0 ? string.Empty : fileName.Substring(underscore + 1);
        }

        // ExcludedSteamAppIds is string[]: appids are compared as strings and never parsed.
        private static bool IsExcludedAppId(string appId)
        {
            if (string.IsNullOrEmpty(appId))
                return false;

            foreach (string excludedAppId in ExecutableRules.ExcludedSteamAppIds)
            {
                if (string.Equals(appId, excludedAppId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
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

        // ------------------------------------------------------------------ paths

        // HKCU\SOFTWARE\Valve\Steam\SteamPath is written with forward slashes ("e:/steam"), and a
        // hand-edited libraryfolders.vdf may carry a trailing separator. Both have to end up in
        // one canonical form, because the result is compared for de-duplication and shown to the
        // user as InstallDirectory.
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

        // Path.Combine throws on invalid path characters, and installdir comes out of a file this
        // application did not write.
        private static string CombinePath(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
                return null;

            try
            {
                return Path.Combine(left, right);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }
}
