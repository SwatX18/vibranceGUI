﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Security;

namespace vibrance.GUI.common.gamefinder
{
    public class ExecutableCandidate
    {
        public string FullPath { get; set; }
        public string FileName { get; set; }     // "cs2.exe", with extension
        public string RelativePath { get; set; } // "game\bin\win64\cs2.exe", relative to install dir
        public long   SizeBytes { get; set; }
        public int    Depth { get; set; }        // separators in RelativePath; 0 = directly in install dir
    }

    /// <summary>
    /// The only type in this folder that enumerates the filesystem.
    /// </summary>
    public static class ExecutableEnumerator
    {
        public const int MaxDepth = 4;

        // Per game. When the cap bites it keeps the LARGEST executables, never the ones the walk
        // happened to reach first. The picker only ever sees what this returns, so truncating by
        // directory-walk order could drop the biggest file and make the correct answer
        // unreachable, with nothing thrown and nothing logged.
        public const int MaxExecutables = 500;

        private const string ExecutableExtension = ".exe";

        // How far the working set is allowed to run ahead of the cap before it is trimmed back.
        // Bounds memory without running the trim after every single file.
        private const int WorkingSetLimit = MaxExecutables * 2;

        // One entry of the breadth-first queue: a directory to list, the relative prefix every
        // file found in it carries, and the depth of that directory below the install directory.
        // The prefix is carried rather than derived, so no path arithmetic is done against an
        // install directory that may or may not end in a separator.
        private class PendingDirectory
        {
            public PendingDirectory(string fullPath, string relativePrefix, int depth)
            {
                this.FullPath = fullPath;
                this.RelativePrefix = relativePrefix;
                this.Depth = depth;
            }

            public readonly string FullPath;
            public readonly string RelativePrefix; // "" for the install directory itself, else "a\b\"
            public readonly int Depth;             // 0 for the install directory itself
        }

        // Breadth-first, depth-capped. Skips directories named in
        // ExecutableRules.ExcludedDirectorySegments. Catches UnauthorizedAccessException /
        // DirectoryNotFoundException / IOException per directory and continues.
        // Returns an empty list, never null.
        // isCancelled MAY be null: treated as never-cancelled, so a throwaway driver needs no ceremony.
        public static List<ExecutableCandidate> Enumerate(string installDirectory, Func<bool> isCancelled)
        {
            List<ExecutableCandidate> results = new List<ExecutableCandidate>();
            if (string.IsNullOrEmpty(installDirectory))
                return results;

            Queue<PendingDirectory> pending = new Queue<PendingDirectory>();
            pending.Enqueue(new PendingDirectory(installDirectory, string.Empty, 0));

            while (pending.Count > 0)
            {
                // Once per directory, not once per game: the longest uninterruptible unit has to
                // stay a single directory listing for Cancel to stop the scan inside a second.
                if (IsCancelled(isCancelled))
                {
                    TrimToLargest(results);
                    return results;
                }

                PendingDirectory directory = pending.Dequeue();

                CollectExecutables(directory, results);

                // The install directory is depth 0, and the children of a directory are listed
                // only while the depth of that directory is below the cap, so the deepest
                // reachable file sits at Depth == MaxDepth. This is exactly the reference
                // implementation: depth = r[len(gamedir):].count(os.sep), pruned at depth >= 4.
                if (directory.Depth < MaxDepth)
                    EnqueueSubdirectories(directory, pending);
            }

            TrimToLargest(results);
            return results;
        }

        // Directory.GetFiles throws on the whole call rather than per entry, so the try has to sit
        // around the listing itself: one unreadable directory skips that directory and nothing else.
        private static void CollectExecutables(PendingDirectory directory, List<ExecutableCandidate> results)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(directory.FullPath, "*.exe");
            }
            catch (UnauthorizedAccessException) { return; }
            catch (PathTooLongException)        { return; }
            catch (DirectoryNotFoundException)  { return; }
            catch (IOException)                 { return; }
            catch (SecurityException)           { return; }
            catch (ArgumentException)           { return; }

            for (int i = 0; i < files.Length; i++)
            {
                string fullPath = files[i];

                // A three-character extension in a search pattern also matches every longer
                // extension beginning with it, so "*.exe" hands back "foo.exe_old" as well. The
                // reference implementation filters on the real extension; matching that here keeps
                // the two agreeing on what was even a candidate.
                if (!string.Equals(Path.GetExtension(fullPath), ExecutableExtension,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                long sizeBytes;
                try
                {
                    sizeBytes = new FileInfo(fullPath).Length;
                }
                catch (UnauthorizedAccessException) { continue; }
                catch (PathTooLongException)        { continue; }
                catch (IOException)                 { continue; }
                catch (SecurityException)           { continue; }
                catch (ArgumentException)           { continue; }

                string fileName = Path.GetFileName(fullPath);

                ExecutableCandidate candidate = new ExecutableCandidate();
                candidate.FullPath = fullPath;
                candidate.FileName = fileName;
                candidate.RelativePath = directory.RelativePrefix + fileName;
                candidate.SizeBytes = sizeBytes;
                candidate.Depth = directory.Depth;
                results.Add(candidate);

                if (results.Count >= WorkingSetLimit)
                    TrimToLargest(results);
            }
        }

        // Cuts the working set back to MaxExecutables, keeping the largest files. The order is the
        // picker's own total order, duplicated here rather than shared because ExecutablePicker is
        // pure and knows nothing about the walk: size descending, then shallowest, then relative
        // path, so which candidates survive a cut is deterministic on every machine.
        private static void TrimToLargest(List<ExecutableCandidate> results)
        {
            if (results.Count <= MaxExecutables)
                return;

            results.Sort(CompareBySizeThenDepthThenPath);
            results.RemoveRange(MaxExecutables, results.Count - MaxExecutables);
        }

        private static int CompareBySizeThenDepthThenPath(ExecutableCandidate left, ExecutableCandidate right)
        {
            int result = right.SizeBytes.CompareTo(left.SizeBytes);
            if (result != 0)
                return result;

            result = left.Depth.CompareTo(right.Depth);
            if (result != 0)
                return result;

            return StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath);
        }

        private static void EnqueueSubdirectories(PendingDirectory directory, Queue<PendingDirectory> pending)
        {
            string[] subdirectories;
            try
            {
                subdirectories = Directory.GetDirectories(directory.FullPath);
            }
            catch (UnauthorizedAccessException) { return; }
            catch (PathTooLongException)        { return; }
            catch (DirectoryNotFoundException)  { return; }
            catch (IOException)                 { return; }
            catch (SecurityException)           { return; }
            catch (ArgumentException)           { return; }

            for (int i = 0; i < subdirectories.Length; i++)
            {
                string name = Path.GetFileName(subdirectories[i]);
                if (string.IsNullOrEmpty(name) || IsExcludedDirectory(name))
                    continue;

                pending.Enqueue(new PendingDirectory(
                    subdirectories[i],
                    directory.RelativePrefix + name + Path.DirectorySeparatorChar,
                    directory.Depth + 1));
            }
        }

        // The skip list keeps the walk off _CommonRedist and friends entirely. It cannot change
        // which executable is picked: ExecutablePicker.IsExcluded rejects the same files again by
        // relative-path segment, so this is a cost measure, not a correctness one.
        private static bool IsExcludedDirectory(string directoryName)
        {
            string[] excludedSegments = ExecutableRules.ExcludedDirectorySegments;
            if (excludedSegments == null)
                return false;

            for (int i = 0; i < excludedSegments.Length; i++)
            {
                if (string.Equals(directoryName, excludedSegments[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool IsCancelled(Func<bool> isCancelled)
        {
            return isCancelled != null && isCancelled();
        }
    }
}
