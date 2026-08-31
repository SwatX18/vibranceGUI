using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace vibrance.GUI.common.gamefinder
{
    /// <summary>
    /// Start Menu (per-user and all-users Programs trees) and desktop (per-user and public) .lnk
    /// shortcuts, resolved to the file they point at. Every other source here needs a game to have
    /// registered itself with something - an appmanifest, an Epic manifest, an Uninstall entry. A
    /// cracked copy, a repack, a DRM-free build copied between machines, or a portable install
    /// registers none of that; frequently the ONLY trace it leaves on the system is the shortcut
    /// its own installer (or the user, by hand) dropped in one of these four places, and that
    /// shortcut names the exact file the player actually launches. This source exists for that
    /// population. It does not, and must not, filter on where a game came from - see the filters
    /// below, which are all about what the shortcut names, never who published it.
    ///
    /// Four roots, not two: Environment.SpecialFolder.Programs/CommonPrograms cover the Start
    /// Menu, but the desktop is deliberately included too - for exactly the unregistered-game
    /// population this source targets, a desktop shortcut is often the only one that exists at
    /// all (no measured example on the reference machine, which has none, but the brief calling
    /// this out explicitly and the zero cost of scanning two more Directory.Exists-cheap roots
    /// both argue for including it).
    ///
    /// Resolution is IShellLink via ComImport, not the documented .lnk binary format, because
    /// CLSID_ShellLink is registered ThreadingModel=Both (confirmed via
    /// HKCR\CLSID\{00021401-0000-0000-C000-000000000046}\InprocServer32 on the reference machine)
    /// - CoCreateInstance succeeds directly on an MTA thread with no apartment-marshaling proxy,
    /// which is what matters here: GameFinder's scan runs inside BackgroundWorker.DoWork, and
    /// BackgroundWorker schedules that callback on a ThreadPool thread, which is MTA (nothing in
    /// this application's Main calls Thread.SetApartmentState or otherwise opts a worker thread
    /// into STA). This was proved, not assumed: a standalone harness spun an explicit
    /// ApartmentState.MTA thread with no CoInitialize of its own, called CoCreateInstance on this
    /// CLSID, loaded a real shortcut through IPersistFile and read its target back through
    /// IShellLinkW.GetPath, and it resolved correctly (D:\Notepad++\notepad++.exe). See
    /// RealShortcutResolver.Resolve's own comment for what a "Both" threading model buys over
    /// "Apartment" (STA-only) here.
    ///
    /// Confidence is always Guessed, deliberately, never Known. A shortcut names exactly one file
    /// - there is no ranking involved the way Steam's install-folder walk needs one - which is the
    /// argument for Known. But nothing curates a shortcut the way a store's own manifest or the
    /// Uninstall registry's publisher allowlist does: measured on the reference machine, real
    /// Start Menu shortcuts resolve to per-game launcher stubs (Diablo IV.lnk -> "Diablo IV
    /// Launcher.exe", not the game itself), to store client launchers (Steam.lnk -> Steam.exe), to
    /// InnoSetup uninstallers (unins000.exe - see ExecutableRules' "unins0*"), and in two cases to
    /// nothing resolvable as a file at all (a virtual shell item, and what appears to be an
    /// MSIX/UWP package shortcut - both covered below). A wrong Known row is unmarked bad data the
    /// user has no way to detect from the review list alone; a wrong Guessed row costs only a
    /// "(?)" marker and a "Best guess" status that were already true. See GameFinder.BuildToolTip,
    /// which used to describe every Guessed row as a Steam-specific ranking; it was generalised
    /// alongside this source so a shortcut-sourced Guessed row no longer reads "Steam does not
    /// say...".
    ///
    /// Registered last in GameFinderScanner.CreateDefaultSources: the scanner keeps whichever
    /// source reports an ExecutablePath FIRST, and Steam/Epic/the Uninstall registry all carry
    /// richer metadata (a real store name, an install directory the store itself named) than a
    /// bare shortcut ever can, so this source is only ever meant to contribute an executable none
    /// of them already found.
    ///
    /// Filtering is the load-bearing half of this source - a Start Menu tree is mostly not games:
    /// uninstallers, manuals, web links, readme files, Office, drivers, the app's own shortcut.
    /// Applied in order, cheapest first: ExecutableRules.ExcludedFileNameGlobs (shared - setup,
    /// install, uninst, unins0*, updater, ...) and LauncherFileNameGlobs (local to this source -
    /// see that field's own comment for why it cannot be the shared list) are filename checks and
    /// cost nothing; ExecutableRules.ExcludedDirectorySegments plus the local
    /// NonGameVendorSegments (Common Files, Microsoft Office, Windows Kits, WindowsApps - Office
    /// alone contributed 8 of the 148 shortcuts measured on the reference machine, none caught by
    /// any filename glob) and the %WINDIR% prefix check together removed roughly 35 more; a
    /// self-reference check drops a shortcut that resolves to this running process's own .exe
    /// (StartMenuShortcutSourceFixture documents why nothing else here would ever catch that); and
    /// File.Exists is the last, most expensive check, same ordering principle UninstallRegistrySource
    /// already uses. Everything a filter removes is reported through context.ReportSkipped with a
    /// reason, never dropped silently - same established pattern, same reason: it is what a user's
    /// vibranceGUI.log gets grepped for.
    ///
    /// Shortcut resolution sits behind IShortcutResolver (see below) so
    /// StartMenuShortcutSourceFixture can drive every filtering, dedupe and confidence decision
    /// with a fake, in-memory mapping from shortcut path to ShortcutTarget - no real .lnk file, no
    /// COM, no disk - the same seam-and-fake shape as IGammaDevice, IHotkeyRegistrar and
    /// IHdrStateReader elsewhere in this codebase. The directory WALK that finds .lnk paths in the
    /// first place is not behind a seam; reading the real Start Menu is cheap, read-only, and
    /// explicitly fine to exercise directly.
    /// </summary>
    public class StartMenuShortcutSource : IGameLibrarySource
    {
        private const string SourceName = "Start Menu shortcuts";

        private const string ShortcutSearchPattern = "*.lnk";
        private const string ExecutableExtension = ".exe";

        // Defensive only - real Start Menu trees measured on the reference machine never exceed
        // depth 2 (root -> vendor folder -> shortcut). This exists purely so a pathological
        // junction/symlink loop cannot make the walk run away; it is not expected to ever bite.
        private const int MaxDepth = 6;

        // This source's fixed skip reasons - what a user's vibranceGUI.log gets grepped for when
        // they report "my game isn't listed", same convention the other three sources use.
        private const string SkipUnresolvable = "could not read shortcut";
        private const string SkipNoTarget = "shortcut has no file target";
        private const string SkipNotExecutable = "target is not an executable";
        private const string SkipExcludedExecutable = "excluded executable";
        private const string SkipLauncherExecutable = "launcher executable";
        private const string SkipExcludedLocation = "excluded location";
        private const string SkipSelfReference = "vibranceGUI itself";
        private const string SkipExecutableNotFound = "named executable not found";

        private static readonly string[] EmptyPaths = new string[0];
        private static readonly char[] PathSeparators =
            { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

        // Local to this source, deliberately NOT merged into ExecutableRules or reusing
        // UninstallRegistrySource.LauncherFileNames: a shortcut resolves to a different set of
        // literal file names than a store's own DisplayIcon does. Measured on the reference
        // machine: Battle.net.lnk -> "Battle.net Launcher.exe" (not "Battle.net.exe"), Diablo
        // IV.lnk -> "Diablo IV Launcher.exe" (a per-game stub, in neither list), Rockstar Games
        // Launcher.lnk -> "LauncherPatcher.exe". Kept out of the shared ExecutableRules list
        // because "*launcher*" would also apply to ExecutablePicker's ranking of files already
        // inside a game's own Steam install folder, where a legitimately playable executable could
        // coincidentally contain "launcher" in its name - that risk does not exist here, where
        // every candidate is a single named file, never a ranked pick among several.
        private static readonly string[] LauncherFileNameGlobs =
        {
            "*launcher*", "steam.exe", "battle.net.exe", "eadesktop.exe", "upc.exe", "galaxyclient.exe"
        };

        // Known non-game vendor trees a shortcut can legitimately resolve into, matched as an
        // exact directory-path segment (same matching style as ExecutableRules.
        // ExcludedDirectorySegments, which this list sits alongside but does not join - Office,
        // Windows Kits and WindowsApps have no bearing on a game's own Steam/Epic install folder,
        // so adding them to the shared list would only ever cost something there for no benefit
        // here). "Microsoft Office" alone accounted for 8 of 148 shortcuts measured on the
        // reference machine (Access, Excel, OneNote, Outlook, PowerPoint, Publisher, Word, Sticky
        // Notes), none of them caught by any filename glob.
        private static readonly string[] NonGameVendorSegments =
        {
            "Common Files", "Microsoft Office", "Windows Kits", "WindowsApps"
        };

        private readonly IShortcutResolver _resolver;

        public StartMenuShortcutSource() : this(null)
        {
        }

        // Internal so StartMenuShortcutSourceFixture can substitute a fake resolver. A null
        // resolver (the public constructor's path) gets the real, COM-backed one.
        internal StartMenuShortcutSource(IShortcutResolver resolver)
        {
            _resolver = resolver == null ? new RealShortcutResolver() : resolver;
        }

        public string DisplayName
        {
            get { return SourceName; }
        }

        // Cheap by contract: up to four Directory.Exists calls on paths GetFolderPath already
        // resolved. Never lists a directory - that is Scan's job.
        public bool IsAvailable()
        {
            string[] roots = GetRoots();
            for (int i = 0; i < roots.Length; i++)
            {
                try
                {
                    if (Directory.Exists(roots[i]))
                        return true;
                }
                catch (Exception)
                {
                    // A root this process may not even probe. The next one gets its turn.
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
                // Held across all four roots, not per root: the same executable is commonly
                // reachable from more than one of them (a per-user AND an all-users shortcut to
                // the same install is unremarkable). GameFinderScanner de-duplicates between
                // sources; this is the same job within this one, same reasoning
                // UninstallRegistrySource's own per-source set uses.
                HashSet<string> reportedExecutablePaths =
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                string[] roots = GetRoots();
                for (int i = 0; i < roots.Length; i++)
                {
                    if (context.IsCancelled)
                        return;

                    ScanRoot(roots[i], reportedExecutablePaths, context);
                }
            }
            catch (Exception ex)
            {
                // Scan is contractually forbidden from throwing. Everything below handles its own
                // failures; this is the backstop for the ones it cannot foresee.
                context.ReportError(SourceName, ex);
            }
        }

        // ------------------------------------------------------------------ roots

        private static string[] GetRoots()
        {
            List<string> roots = new List<string>();
            AddRootIfPresent(roots, Environment.SpecialFolder.Programs);
            AddRootIfPresent(roots, Environment.SpecialFolder.CommonPrograms);
            AddRootIfPresent(roots, Environment.SpecialFolder.DesktopDirectory);
            AddRootIfPresent(roots, Environment.SpecialFolder.CommonDesktopDirectory);
            return roots.ToArray();
        }

        private static void AddRootIfPresent(List<string> roots, Environment.SpecialFolder folder)
        {
            try
            {
                string path = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(path))
                    roots.Add(path);
            }
            catch (Exception)
            {
                // A shell folder GetFolderPath cannot resolve. The other roots still get scanned.
            }
        }

        // ------------------------------------------------------------------ the walk

        // One entry of the breadth-first queue: a directory still to list, and its depth below the
        // root (0 for the root itself). Deliberately not the same PendingDirectory
        // ExecutableEnumerator uses - that one also carries a relative-path prefix this source has
        // no use for, since a shortcut's GameName comes from its own file name, never from where it
        // sits in the tree.
        private class PendingDirectory
        {
            public PendingDirectory(string fullPath, int depth)
            {
                FullPath = fullPath;
                Depth = depth;
            }

            public readonly string FullPath;
            public readonly int Depth;
        }

        private void ScanRoot(string root, HashSet<string> reportedExecutablePaths, GameScanContext context)
        {
            try
            {
                if (!Directory.Exists(root))
                    return;
            }
            catch (Exception)
            {
                return;
            }

            Queue<PendingDirectory> pending = new Queue<PendingDirectory>();
            pending.Enqueue(new PendingDirectory(root, 0));

            while (pending.Count > 0)
            {
                // Once per directory, matching the contract every source here follows.
                if (context.IsCancelled)
                    return;

                PendingDirectory directory = pending.Dequeue();

                string[] shortcutPaths = SafeGetFiles(directory.FullPath, ShortcutSearchPattern);

                // GetFiles order is filesystem-dependent; sorting makes the reported order the
                // same on every machine, which is what makes a scan comparable against the last.
                Array.Sort(shortcutPaths, StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < shortcutPaths.Length; i++)
                {
                    if (context.IsCancelled)
                        return;

                    ScanShortcut(shortcutPaths[i], reportedExecutablePaths, context);
                }

                if (directory.Depth < MaxDepth)
                {
                    string[] subdirectories = SafeGetDirectories(directory.FullPath);
                    for (int i = 0; i < subdirectories.Length; i++)
                    {
                        pending.Enqueue(new PendingDirectory(subdirectories[i], directory.Depth + 1));
                    }
                }
            }
        }

        private static string[] SafeGetFiles(string directoryPath, string searchPattern)
        {
            try
            {
                return Directory.GetFiles(directoryPath, searchPattern);
            }
            catch (UnauthorizedAccessException) { return EmptyPaths; }
            catch (PathTooLongException)        { return EmptyPaths; }
            catch (DirectoryNotFoundException)  { return EmptyPaths; }
            catch (IOException)                 { return EmptyPaths; }
            catch (SecurityException)           { return EmptyPaths; }
            catch (ArgumentException)           { return EmptyPaths; }
        }

        private static string[] SafeGetDirectories(string directoryPath)
        {
            try
            {
                return Directory.GetDirectories(directoryPath);
            }
            catch (UnauthorizedAccessException) { return EmptyPaths; }
            catch (PathTooLongException)        { return EmptyPaths; }
            catch (DirectoryNotFoundException)  { return EmptyPaths; }
            catch (IOException)                 { return EmptyPaths; }
            catch (SecurityException)           { return EmptyPaths; }
            catch (ArgumentException)           { return EmptyPaths; }
        }

        // ------------------------------------------------------------------ one shortcut

        // Internal, and taking the resolved shortcut PATHS rather than walking to find them, so
        // StartMenuShortcutSourceFixture can drive this directly against a fake resolver with a
        // handful of made-up .lnk paths - no real Start Menu, no real disk. This is the same
        // method ScanRoot calls per real shortcut it lists; the fixture is exercising this exact
        // code, not a parallel copy of it.
        internal void ScanShortcuts(List<string> shortcutPaths, GameScanContext context)
        {
            if (shortcutPaths == null || context == null)
                return;

            HashSet<string> reportedExecutablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < shortcutPaths.Count; i++)
            {
                if (context.IsCancelled)
                    return;

                ScanShortcut(shortcutPaths[i], reportedExecutablePaths, context);
            }
        }

        private void ScanShortcut(string shortcutPath, HashSet<string> reportedExecutablePaths,
                                  GameScanContext context)
        {
            // GameName is the shortcut's own file name, exactly what Explorer shows for it in the
            // Start Menu - unlike Steam/Epic/the Uninstall registry, there is no separate "display
            // name" field to prefer over it.
            string gameName = SafeGetFileNameWithoutExtension(shortcutPath);
            if (gameName.Length == 0)
                gameName = shortcutPath;

            try
            {
                ShortcutTarget target = _resolver.Resolve(shortcutPath);
                if (target == null)
                {
                    context.ReportSkipped(gameName, SkipUnresolvable);
                    return;
                }

                string rawTarget = target.ExecutablePath == null ? string.Empty : target.ExecutablePath.Trim();
                if (rawTarget.Length == 0)
                {
                    // A virtual shell item (Control Panel, Run) or, measured on the reference
                    // machine, what appears to be an MSIX/UWP package shortcut - IShellLinkW.GetPath
                    // has no file path to hand back for either. Neither is a bug in the resolver.
                    context.ReportSkipped(gameName, SkipNoTarget);
                    return;
                }

                string executablePath = NormalizeExecutableTarget(rawTarget);
                if (executablePath == null)
                {
                    // Not an .exe (a .url, a document, an .msc snap-in, a help file - all measured
                    // on the reference machine), or not an absolute path.
                    context.ReportSkipped(gameName, SkipNotExecutable);
                    return;
                }

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

                if (IsExcludedLocation(executablePath))
                {
                    context.ReportSkipped(gameName, SkipExcludedLocation);
                    return;
                }

                if (IsOwnExecutable(executablePath))
                {
                    context.ReportSkipped(gameName, SkipSelfReference);
                    return;
                }

                if (!target.TargetExists)
                {
                    // The shortcut outlived the install, or names a drive that is not mounted at
                    // the moment.
                    context.ReportSkipped(gameName, SkipExecutableNotFound);
                    return;
                }

                if (!reportedExecutablePaths.Add(executablePath))
                    return;   // the same executable reached through a second shortcut

                GameCandidate candidate = new GameCandidate();
                candidate.Source = GameSource.Shortcut;
                candidate.SourceAppId = shortcutPath;
                candidate.GameName = gameName;
                candidate.InstallDirectory = SafeGetDirectoryName(executablePath);
                candidate.ExecutablePath = executablePath;
                // See this class's own header comment for why every row from this source is
                // Guessed, never Known.
                candidate.Confidence = ExecutableConfidence.Guessed;
                candidate.Icon = TryExtractIcon(executablePath);

                context.Report(candidate);
            }
            catch (Exception ex)
            {
                // An unreadable or corrupt shortcut costs one game, never the rest of the tree.
                context.ReportError(SourceName, ex);
            }
        }

        // ------------------------------------------------------------------ target normalisation

        // IShellLinkW.GetPath hands back a clean string - no surrounding quotes, no trailing icon
        // index the way UninstallRegistrySource's DisplayIcon can carry - so this is simpler than
        // ResolveDisplayIcon: verify it is an absolute path to an .exe and canonicalise it.
        private static string NormalizeExecutableTarget(string rawPath)
        {
            if (rawPath.Length == 0)
                return null;

            if (!rawPath.EndsWith(ExecutableExtension, StringComparison.OrdinalIgnoreCase))
                return null;

            if (!IsRootedPath(rawPath))
                return null;

            try
            {
                // Canonical form matters beyond tidiness: GameFinderScanner de-duplicates by
                // ExecutablePath with OrdinalIgnoreCase, so a path carrying ".." or a stray
                // separator would compare unequal to the same file reached from another source.
                return Path.GetFullPath(rawPath);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsRootedPath(string path)
        {
            try
            {
                return Path.IsPathRooted(path);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------ the filters

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
            if (fileName.Length == 0)
                return false;

            foreach (string glob in LauncherFileNameGlobs)
            {
                if (ExecutableRules.MatchesGlob(fileName, glob))
                    return true;
            }

            return false;
        }

        // %WINDIR% (no real game ever installs there), every segment of
        // ExecutableRules.ExcludedDirectorySegments (shared, same matching style
        // ExecutablePicker.IsExcluded already uses on a relative path - here applied to every
        // segment of the target's own absolute directory, since a shortcut has no "install
        // directory" of its own to measure a relative path against), and NonGameVendorSegments
        // (local - see that field's own comment).
        private static bool IsExcludedLocation(string executablePath)
        {
            string directory = SafeGetDirectoryName(executablePath);
            if (directory.Length == 0)
                return false;

            if (IsUnderWindowsDirectory(directory))
                return true;

            string[] segments = directory.Split(PathSeparators);
            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];
                if (segment.Length == 0)
                    continue;

                foreach (string excluded in ExecutableRules.ExcludedDirectorySegments)
                {
                    if (string.Equals(segment, excluded, StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                foreach (string excluded in NonGameVendorSegments)
                {
                    if (string.Equals(segment, excluded, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }

        private static bool IsUnderWindowsDirectory(string directory)
        {
            string windowsDirectory = GetWindowsDirectory();
            if (windowsDirectory.Length == 0)
                return false;

            string trimmed = windowsDirectory.TrimEnd(PathSeparators);
            if (string.Equals(directory, trimmed, StringComparison.OrdinalIgnoreCase))
                return true;

            string prefix = trimmed + Path.DirectorySeparatorChar;
            return directory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetWindowsDirectory()
        {
            try
            {
                string path = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (!string.IsNullOrEmpty(path))
                    return path;
            }
            catch (Exception)
            {
            }

            string fromEnvironment = Environment.GetEnvironmentVariable("windir");
            return string.IsNullOrEmpty(fromEnvironment) ? string.Empty : fromEnvironment;
        }

        // A shortcut pointing at vibranceGUI's own running executable is the one self-reference
        // risk unique to this source: Steam/Epic only ever walk their own store's install trees,
        // and the Uninstall registry's publisher allowlist would never admit this application's
        // own entry even if it had one. An unconstrained Start Menu/desktop walk has no such
        // built-in immunity, so it is checked for explicitly.
        private static bool IsOwnExecutable(string executablePath)
        {
            string ownPath = GetOwnExecutablePath();
            return ownPath != null && string.Equals(executablePath, ownPath, StringComparison.OrdinalIgnoreCase);
        }

        // Assembly.GetExecutingAssembly, not WinForms' Application.ExecutablePath - this class
        // must never reference WinForms (IGameLibrarySource's contract) - and not
        // GetEntryAssembly, which returns null whenever there is no managed entry point: that
        // is exactly the case when a fixture is driven by reflection from another host, which
        // is how this suite is usually run, and it made the self-reference check report
        // differently depending on how it was invoked. GetExecutingAssembly is this assembly,
        // which is the executable, and is never null. Internal, not private:
        // StartMenuShortcutSourceFixture calls this directly to build its self-reference case, so
        // the fake target it feeds through FakeResolver is provably the same value IsOwnExecutable
        // itself compares against, in the same process.
        internal static string GetOwnExecutablePath()
        {
            try
            {
                Assembly ownAssembly = Assembly.GetExecutingAssembly();
                if (ownAssembly == null)
                    return null;

                string location = ownAssembly.Location;
                return string.IsNullOrEmpty(location) ? null : Path.GetFullPath(location);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ paths and icons

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

        private static string SafeGetFileNameWithoutExtension(string path)
        {
            try
            {
                return Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
        }

        private static string SafeGetDirectoryName(string path)
        {
            try
            {
                return Path.GetDirectoryName(path) ?? string.Empty;
            }
            catch (Exception)
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

    /// <summary>
    /// What one .lnk resolves to. ExecutablePath is never null (empty for a shortcut that reads
    /// fine but targets a virtual shell item or an MSIX/UWP package, neither of which IShellLinkW
    /// can express as a file path - see StartMenuShortcutSource's header comment). TargetExists is
    /// decided at resolve time, same "the file might not be there any more" concern
    /// UninstallRegistrySource's File.Exists(executablePath) check covers, folded in here rather
    /// than left to a second call because the fixture's fake resolver needs to control it directly
    /// with no real disk to check against.
    /// </summary>
    internal class ShortcutTarget
    {
        public string ExecutablePath { get; set; }
        public bool TargetExists { get; set; }
    }

    /// <summary>
    /// The seam between StartMenuShortcutSource and the real COM shell-link resolution below -
    /// same shape as IHdrStateReader/IGammaDevice/IHotkeyRegistrar elsewhere in this codebase:
    /// RealShortcutResolver is the only production implementation, and
    /// StartMenuShortcutSourceFixture supplies a fake so every filtering, dedupe and confidence
    /// decision can be driven from a plain Dictionary&lt;string, ShortcutTarget&gt; with no real
    /// .lnk file, no COM and no disk.
    /// </summary>
    internal interface IShortcutResolver
    {
        /// <summary>
        /// Resolves one .lnk to what it points at. NEVER throws. Returns null only when the
        /// shortcut itself could not be read at all (corrupt file, no permission, any other COM
        /// failure) - the caller reports that as "could not read shortcut". A shortcut that reads
        /// fine but names no file (see ShortcutTarget's own comment) still returns a
        /// ShortcutTarget, just one with an empty ExecutablePath.
        /// </summary>
        ShortcutTarget Resolve(string shortcutPath);
    }

    /// <summary>
    /// The only production IShortcutResolver. CoCreateInstance's the shell's own ShellLink object
    /// and reads it back through IPersistFile + IShellLinkW - see StartMenuShortcutSource's header
    /// comment for why this is safe to call from the MTA thread BackgroundWorker actually schedules
    /// this on, and for the empirical proof behind that claim.
    /// </summary>
    internal class RealShortcutResolver : IShortcutResolver
    {
        // MAX_PATH. Shell shortcuts predate long-path support and IShellLinkW.GetPath's classic
        // overload (used here) is specified against it; every target measured on the reference
        // machine fit comfortably inside it.
        private const int MaxTargetPathLength = 260;

        private const uint StgmRead = 0;

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")] // CLSID_ShellLink
        private class ShellLinkCoClass
        {
        }

        // Trimmed to the one method this resolver calls. COM interop dispatches by vtable slot
        // index counting from 0 right after IUnknown's own three (QueryInterface/AddRef/Release),
        // and GetPath is the very first method IShellLinkW declares after those - so a ComImport
        // interface containing only GetPath is a safe, correctly-aligned PREFIX of the real
        // vtable; nothing declared after the last member anyone calls is ever dispatched through,
        // so the other ~18 members of the real interface (SetPath, GetDescription, GetHotkey, ...)
        // can simply be omitted. Verified against this exact GUID from an explicit MTA thread with
        // this exact trimmed declaration before this file was written (resolved
        // Notepad++.lnk -> D:\Notepad++\notepad++.exe correctly).
        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")] // IID_IShellLinkW
        private interface IShellLinkW
        {
            void GetPath(StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        }

        // Same trimming principle, but Load is not the first member: IPersistFile extends
        // IPersist (GetClassID) and adds IsDirty before Load, so both have to stay declared, in
        // order, even though neither is ever called here - omitting either would shift every call
        // after it onto the wrong vtable slot.
        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("0000010b-0000-0000-C000-000000000046")] // IID_IPersistFile
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig]
            int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        }

        public ShortcutTarget Resolve(string shortcutPath)
        {
            if (string.IsNullOrEmpty(shortcutPath))
                return null;

            object comObject = null;
            try
            {
                // CLSID_ShellLink is registered ThreadingModel=Both (confirmed via
                // HKCR\CLSID\{00021401-...}\InprocServer32 on the reference machine), so this
                // CoCreateInstance succeeds directly on whatever apartment the calling thread
                // already has - no marshaled proxy, no hidden STA host thread the way "Apartment"
                // (STA-only) would need on an MTA caller. BackgroundWorker.DoWork runs on a
                // ThreadPool thread, which is MTA, and that combination was proved against a real
                // shortcut before this was written - see this class's own header comment.
                comObject = new ShellLinkCoClass();

                IPersistFile persistFile = (IPersistFile)comObject;
                persistFile.Load(shortcutPath, StgmRead);

                IShellLinkW shellLink = (IShellLinkW)comObject;
                StringBuilder buffer = new StringBuilder(MaxTargetPathLength);
                shellLink.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);

                string executablePath = buffer.ToString();

                ShortcutTarget target = new ShortcutTarget();
                target.ExecutablePath = executablePath;
                target.TargetExists = executablePath.Length > 0 && SafeFileExists(executablePath);
                return target;
            }
            catch (Exception)
            {
                // A corrupt .lnk, one this process has no rights to read, or any other COM
                // failure - the caller reports "could not read shortcut" and moves on.
                return null;
            }
            finally
            {
                if (comObject != null && Marshal.IsComObject(comObject))
                    Marshal.ReleaseComObject(comObject);
            }
        }

        private static bool SafeFileExists(string path)
        {
            try
            {
                return File.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
