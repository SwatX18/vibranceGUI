using System;
using System.Collections.Generic;

namespace vibrance.GUI.common.gamefinder
{
    /// <summary>
    /// Owns the ordered source list, runs each, de-duplicates by executable path
    /// (StringComparison.OrdinalIgnoreCase) and honours cancellation between sources.
    /// Source order is priority order.
    /// </summary>
    public class GameFinderScanner
    {
        private readonly List<IGameLibrarySource> _sources;

        public GameFinderScanner(List<IGameLibrarySource> sources)
        {
            _sources = sources == null ? new List<IGameLibrarySource>() : sources;
        }

        // The single registration point for a new store: Steam, then Epic.
        public static List<IGameLibrarySource> CreateDefaultSources()
        {
            List<IGameLibrarySource> sources = new List<IGameLibrarySource>();
            sources.Add(new SteamLibrarySource());
            sources.Add(new EpicLibrarySource());
            return sources;
        }

        public void Scan(GameScanContext context)
        {
            if (context == null)
                return;

            // Source order is priority order, so the first source to report an executable path
            // keeps it and every later one is dropped.
            HashSet<string> reportedExecutablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < _sources.Count; i++)
            {
                if (context.IsCancelled)
                    return;

                IGameLibrarySource source = _sources[i];
                if (source == null)
                    continue;

                string sourceName = GetDisplayName(source);

                bool available;
                try
                {
                    available = source.IsAvailable();
                }
                catch (Exception ex)
                {
                    context.ReportError(sourceName, ex);
                    continue;
                }

                if (!available || context.IsCancelled)
                    continue;

                try
                {
                    source.Scan(CreateDeduplicatingContext(context, reportedExecutablePaths));
                }
                catch (Exception ex)
                {
                    // A source is contractually forbidden from throwing. One that does anyway must
                    // not take the remaining sources down with it.
                    context.ReportError(sourceName, ex);
                }
            }
        }

        // De-duplication belongs to the scanner rather than to any source, so a source only ever
        // has to report what it found. The wrapper forwards everything else untouched.
        private static GameScanContext CreateDeduplicatingContext(GameScanContext outer,
                                                                  HashSet<string> reportedExecutablePaths)
        {
            return new GameScanContext(
                delegate { return outer.IsCancelled; },
                delegate(GameCandidate candidate)
                {
                    if (candidate == null || string.IsNullOrEmpty(candidate.ExecutablePath))
                        return;

                    if (!reportedExecutablePaths.Add(candidate.ExecutablePath))
                        return;

                    outer.Report(candidate);
                },
                outer.ReportSkipped,
                outer.ReportError);
        }

        // DisplayName is a property on an implementation the scanner does not own, and it is only
        // ever wanted here to label a failure that has already happened.
        private static string GetDisplayName(IGameLibrarySource source)
        {
            try
            {
                string displayName = source.DisplayName;
                if (!string.IsNullOrEmpty(displayName))
                    return displayName;
            }
            catch (Exception)
            {
            }

            return source.GetType().Name;
        }
    }
}
