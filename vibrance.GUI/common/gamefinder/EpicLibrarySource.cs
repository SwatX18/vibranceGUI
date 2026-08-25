using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace vibrance.GUI.common.gamefinder
{
    /// <summary>
    /// %ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item; reads DisplayName,
    /// InstallLocation and LaunchExecutable. Emits ExecutableConfidence.Known.
    /// Epic is the only source that is TOLD its executable instead of inferring one, which is the
    /// whole reason it exists in v1. It therefore never runs ExecutableEnumerator or
    /// ExecutablePicker: a LaunchExecutable that is absent, or that names a file which is not on
    /// disk, is a skip. Falling back to a ranked guess would quietly erase the Known/Guessed
    /// distinction the review window reports to the user.
    /// </summary>
    public class EpicLibrarySource : IGameLibrarySource
    {
        private const string SourceName = "Epic Games";

        // The launcher writes its manifests under the machine-wide application data folder, not a
        // per-user one, so every account on the machine sees the same set.
        private const string ManifestsSubPath = "Epic\\EpicGamesLauncher\\Data\\Manifests";
        private const string ProgramDataVariableName = "ProgramData";
        private const string DefaultProgramDataPath = "C:\\ProgramData";
        private const string ManifestSearchPattern = "*.item";

        // The four manifest keys this source reads. SimpleJsonReader keys its dictionary
        // OrdinalIgnoreCase, so the casing here is documentation rather than a requirement.
        private const string DisplayNameKey = "DisplayName";
        private const string InstallLocationKey = "InstallLocation";
        private const string LaunchExecutableKey = "LaunchExecutable";
        private const string AppNameKey = "AppName";

        // Two of the four fixed skip reasons. "launch executable missing" belongs to this source
        // alone. They are what a user's vibranceGUI.log gets grepped for when they report "my game
        // isn't listed", so they are literals here rather than composed prose.
        private const string SkipInstallDirectoryNotFound = "install directory not found";
        private const string SkipLaunchExecutableMissing = "launch executable missing";

        public string DisplayName
        {
            get { return SourceName; }
        }

        // Cheap by contract: at most three Directory.Exists calls on a path built from an
        // environment lookup. It never lists a directory, and on a machine without Epic - which
        // includes the machine this source was written on - it is one failed Exists and done.
        public bool IsAvailable()
        {
            return FindManifestsDirectory() != null;
        }

        public void Scan(GameScanContext context)
        {
            if (context == null)
                return;

            try
            {
                string manifestsDirectory = FindManifestsDirectory();
                if (manifestsDirectory == null)
                    return;   // Epic is not installed here; finding nothing is not a failure

                string[] manifestPaths;
                try
                {
                    manifestPaths = Directory.GetFiles(manifestsDirectory, ManifestSearchPattern);
                }
                catch (Exception ex)
                {
                    context.ReportError(SourceName, ex);
                    return;
                }

                // GetFiles order is filesystem-dependent; sorting makes the reported order the
                // same on every machine, which is what makes a scan comparable against the last.
                Array.Sort(manifestPaths, StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < manifestPaths.Length; i++)
                {
                    if (context.IsCancelled)
                        return;

                    ScanManifest(manifestPaths[i], context);
                }
            }
            catch (Exception ex)
            {
                // Scan is contractually forbidden from throwing. Everything below handles its own
                // failures; this is the backstop for the ones it cannot foresee.
                context.ReportError(SourceName, ex);
            }
        }

        // ------------------------------------------------------------------ manifests directory

        // First candidate root that has the manifests folder under it wins. CommonApplicationData
        // is the supported way to reach C:\ProgramData and honours a relocated profile; the
        // environment variable and the literal are there because GetFolderPath can hand back an
        // empty string on a machine whose shell folders are damaged.
        private static string FindManifestsDirectory()
        {
            List<string> roots = new List<string>();
            roots.Add(TryGetCommonApplicationData());
            roots.Add(Environment.GetEnvironmentVariable(ProgramDataVariableName));
            roots.Add(DefaultProgramDataPath);

            for (int i = 0; i < roots.Count; i++)
            {
                string manifestsDirectory = CombinePath(roots[i], ManifestsSubPath);
                if (manifestsDirectory == null)
                    continue;

                try
                {
                    if (Directory.Exists(manifestsDirectory))
                        return manifestsDirectory;
                }
                catch (Exception)
                {
                    // A path this process may not even probe. The next root gets its turn.
                }
            }

            return null;
        }

        private static string TryGetCommonApplicationData()
        {
            try
            {
                return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ one game

        private void ScanManifest(string manifestPath, GameScanContext context)
        {
            try
            {
                Dictionary<string, string> manifest = ReadManifest(manifestPath);

                string appName = GetTrimmedValue(manifest, AppNameKey);

                // GameName is the store's own display name, never the install directory
                // (Finding 3). AppName is a last resort that exists only so a skip can name the
                // thing it skipped; it is Epic's internal app id and reads poorly, but a row that
                // reaches the user always has a real DisplayName behind it or is not reported.
                string gameName = GetTrimmedValue(manifest, DisplayNameKey);
                if (gameName.Length == 0)
                    gameName = appName;
                if (gameName.Length == 0)
                    return;   // neither a display name nor an app id: nothing usable to report

                string installDirectory =
                    NormalizeDirectoryPath(GetTrimmedValue(manifest, InstallLocationKey));
                if (installDirectory == null || !Directory.Exists(installDirectory))
                {
                    // The manifest outlived the install: Epic leaves the .item behind when a game
                    // is removed from a drive that is no longer mounted.
                    context.ReportSkipped(gameName, SkipInstallDirectoryNotFound);
                    return;
                }

                string launchExecutable = GetTrimmedValue(manifest, LaunchExecutableKey);
                if (launchExecutable.Length == 0)
                {
                    context.ReportSkipped(gameName, SkipLaunchExecutableMissing);
                    return;
                }

                string executablePath = ResolveExecutablePath(installDirectory, launchExecutable);
                if (executablePath == null || !File.Exists(executablePath))
                {
                    // The store told us a file and the file is not there. Ranking the folder for a
                    // replacement would turn a Known row into a Guessed one wearing a Known label.
                    context.ReportSkipped(gameName, SkipLaunchExecutableMissing);
                    return;
                }

                GameCandidate candidate = new GameCandidate();
                candidate.Source = GameSource.Epic;
                candidate.SourceAppId = appName;
                candidate.GameName = gameName;
                candidate.InstallDirectory = installDirectory;
                candidate.ExecutablePath = executablePath;
                // The manifest named the executable outright, so this row is not a guess and the
                // main list gives it no "(?)" marker.
                candidate.Confidence = ExecutableConfidence.Known;
                candidate.Icon = TryExtractIcon(executablePath);

                context.Report(candidate);
            }
            catch (Exception ex)
            {
                // A malformed or unreadable manifest costs one game, never the rest of the
                // library. SimpleJsonReader never throws, so what lands here is I/O.
                context.ReportError(SourceName, ex);
            }
        }

        private static Dictionary<string, string> ReadManifest(string manifestPath)
        {
            // ReadTopLevelStrings takes text, not a path, and tolerates anything: a truncated or
            // garbage .item yields a partial or empty dictionary rather than an exception, and
            // that dictionary then fails the DisplayName or LaunchExecutable guard like any other
            // incomplete manifest.
            return SimpleJsonReader.ReadTopLevelStrings(File.ReadAllText(manifestPath));
        }

        private static string GetTrimmedValue(Dictionary<string, string> manifest, string key)
        {
            string value;
            if (!manifest.TryGetValue(key, out value) || value == null)
                return string.Empty;

            return value.Trim();
        }

        // LaunchExecutable is documented as relative to InstallLocation and is written with
        // either separator, commonly with a subdirectory:
        // "FortniteGame/Binaries/Win64/FortniteClient-Win64-Shipping.exe".
        private static string ResolveExecutablePath(string installDirectory, string launchExecutable)
        {
            string relative = launchExecutable.Replace('/', Path.DirectorySeparatorChar);

            // A leading separator makes Path.Combine discard the install directory and resolve
            // against the root of the current drive, which is how a relative path silently becomes
            // the wrong absolute one.
            relative = relative.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (relative.Length == 0)
                return null;

            string combined = CombinePath(installDirectory, relative);
            if (combined == null)
                return null;

            try
            {
                // Canonical form matters beyond tidiness: GameFinderScanner de-duplicates by
                // ExecutablePath with OrdinalIgnoreCase, so a path carrying ".." or a stray
                // separator would compare unequal to the same file reached from Steam.
                return Path.GetFullPath(combined);
            }
            catch (Exception)
            {
                return null;
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

        // ------------------------------------------------------------------ paths

        // InstallLocation comes out of a file this application did not write, and it is shown to
        // the user and stored as ApplicationSetting.InstallDirectory, which section 3.1 defines as
        // a full path with no trailing separator.
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

        // Path.Combine throws on invalid path characters, and both halves of every combination
        // here originate outside this application.
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
