using System;
using System.Collections.Generic;

namespace vibrance.GUI.common.gamefinder
{
    /// <summary>
    /// All three members are pure: no filesystem, no registry, no Console, no MessageBox, no
    /// static mutable state. Keeping them that way is what lets a throwaway console driver call
    /// them against a real library, and it is a review criterion.
    /// </summary>
    public static class ExecutablePicker
    {
        public static bool IsExcluded(ExecutableCandidate candidate)
        {
            if (candidate == null)
                return true;

            string fileName = candidate.FileName;
            if (string.IsNullOrEmpty(fileName))
                return true;

            foreach (string glob in ExecutableRules.ExcludedFileNameGlobs)
            {
                if (ExecutableRules.MatchesGlob(fileName, glob))
                    return true;
            }

            string relativePath = candidate.RelativePath;
            if (string.IsNullOrEmpty(relativePath))
                return false;

            // Segments are matched on the RELATIVE path only, so a library installed at
            // D:\Redist\Steam is unaffected. The last segment is the file name, which the globs
            // above already own.
            string[] segments = relativePath.Split('\\', '/');
            for (int i = 0; i < segments.Length - 1; i++)
            {
                foreach (string excludedSegment in ExecutableRules.ExcludedDirectorySegments)
                {
                    if (string.Equals(segments[i], excludedSegment, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }

        // Drops excluded candidates, then orders survivors by SizeBytes descending,
        // Depth ascending, RelativePath ascending (StringComparer.OrdinalIgnoreCase).
        public static List<ExecutableCandidate> Rank(IList<ExecutableCandidate> input)
        {
            List<ExecutableCandidate> survivors = new List<ExecutableCandidate>();
            if (input == null)
                return survivors;

            foreach (ExecutableCandidate candidate in input)
            {
                if (IsExcluded(candidate))
                    continue;

                survivors.Add(candidate);
            }

            // List.Sort is unstable, so the third key exists to make the order total and the
            // output identical on every machine.
            survivors.Sort(CompareCandidates);
            return survivors;
        }

        // Rank(input)[0], or null when nothing survives.
        public static ExecutableCandidate Select(IList<ExecutableCandidate> input)
        {
            List<ExecutableCandidate> ranked = Rank(input);
            return ranked.Count == 0 ? null : ranked[0];
        }

        private static int CompareCandidates(ExecutableCandidate left, ExecutableCandidate right)
        {
            int result = right.SizeBytes.CompareTo(left.SizeBytes);
            if (result != 0)
                return result;

            result = left.Depth.CompareTo(right.Depth);
            if (result != 0)
                return result;

            return StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath);
        }
    }
}
