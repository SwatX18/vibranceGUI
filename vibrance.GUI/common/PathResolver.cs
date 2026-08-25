using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace vibrance.GUI.common
{
    /// <summary>
    /// The two path questions this application has to put to Windows directly: which executable is
    /// a process running, and where does a directory really live once junctions are followed.
    ///
    /// Both calls are Vista and newer. Neither method ever throws. A process that is protected or
    /// exited a moment ago, and a directory that cannot be opened, are ordinary answers on a busy
    /// machine rather than errors worth logging - the callers degrade instead. On an operating
    /// system old enough to be missing the exports the marshaller throws on the first call, which
    /// is caught here for the same reason.
    /// </summary>
    internal static class PathResolver
    {
        //the whole point of the limited variant: it is granted for processes that refuse
        //PROCESS_ALL_ACCESS, which is what Process.Handle asks for - elevated games above all
        private const uint ProcessQueryLimitedInformation = 0x1000;

        private const uint FileShareAll = 0x00000001 | 0x00000002 | 0x00000004;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;

        private const int MaxPathLength = 1024;

        private const string ExtendedLengthPrefix = @"\\?\";
        private const string ExtendedLengthUncPrefix = @"\\?\UNC\";

        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] StringBuilder imagePath, ref int size);

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(IntPtr file, [Out] StringBuilder filePath, uint filePathLength, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>
        /// The fully qualified executable path of a running process.
        ///
        /// QueryFullProcessImageName reads the image path out of the kernel's own process
        /// structure, so a 32 bit caller - which vibranceGUI always is - gets the right answer for
        /// a 64 bit target, and it needs nothing beyond query access to do it. What it replaces,
        /// GetModuleFileNameEx reached through Process.Handle, needed PROCESS_ALL_ACCESS and threw
        /// a Win32Exception for roughly half the processes on a Windows 11 desktop.
        /// </summary>
        /// <returns>false, without throwing, whenever Windows declines to answer</returns>
        public static bool TryGetProcessImagePath(int processId, out string imagePath)
        {
            imagePath = null;

            //0 is the idle process and there are no negative ids; neither ever owns a window
            if (processId <= 0)
            {
                return false;
            }

            IntPtr processHandle = IntPtr.Zero;
            try
            {
                processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
                if (processHandle == IntPtr.Zero)
                {
                    //protected, or exited between the window event and this call. Both are normal
                    return false;
                }

                StringBuilder buffer = new StringBuilder(MaxPathLength);
                int size = buffer.Capacity;
                if (!QueryFullProcessImageName(processHandle, 0, buffer, ref size) || buffer.Length == 0)
                {
                    return false;
                }

                imagePath = buffer.ToString();
                return true;
            }
            catch (Exception)
            {
                //pre-Vista, where the export does not exist and the first call throws
                return false;
            }
            finally
            {
                if (processHandle != IntPtr.Zero)
                {
                    CloseHandle(processHandle);
                }
            }
        }

        /// <summary>
        /// Resolves a directory through junctions and symbolic links, which Steam libraries very
        /// often are, so that a stored install directory is the path Windows will report for a
        /// process running under it.
        ///
        /// Returns the argument unchanged when it cannot be resolved: an unresolved directory
        /// still matches on the many machines that have no junction in the way, an empty one
        /// matches nothing anywhere.
        /// </summary>
        public static string ResolveFinalDirectoryPath(string directory)
        {
            if (string.IsNullOrEmpty(directory))
            {
                return directory;
            }

            IntPtr directoryHandle = InvalidHandleValue;
            try
            {
                //FILE_FLAG_BACKUP_SEMANTICS is what lets CreateFile open a directory at all. No
                //access right is asked for, because this only ever asks the object manager for a name
                directoryHandle = CreateFile(directory, 0, FileShareAll, IntPtr.Zero, OpenExisting,
                    FileFlagBackupSemantics, IntPtr.Zero);
                if (directoryHandle == InvalidHandleValue)
                {
                    return directory;
                }

                StringBuilder buffer = new StringBuilder(MaxPathLength);
                uint length = GetFinalPathNameByHandle(directoryHandle, buffer, (uint)buffer.Capacity, 0);

                //0 is failure, anything at or above the capacity is "your buffer was too small"
                if (length == 0 || length >= buffer.Capacity)
                {
                    return directory;
                }

                return NormalizeFinalPath(buffer.ToString());
            }
            catch (Exception)
            {
                return directory;
            }
            finally
            {
                //unconditionally, and never after an early return: Playnite's Paths.GetFinalPathName
                //returns before its CloseHandle for a unc path and leaks the handle on every call
                if (directoryHandle != InvalidHandleValue)
                {
                    CloseHandle(directoryHandle);
                }
            }
        }

        private static string NormalizeFinalPath(string path)
        {
            if (path.StartsWith(ExtendedLengthUncPrefix, StringComparison.Ordinal))
            {
                //\\?\UNC\server\share is the same place as \\server\share
                path = @"\\" + path.Substring(ExtendedLengthUncPrefix.Length);
            }
            else if (path.StartsWith(ExtendedLengthPrefix, StringComparison.Ordinal))
            {
                path = path.Substring(ExtendedLengthPrefix.Length);
            }

            //an install directory is stored without a trailing separator. "C:\" is three characters
            //and its separator belongs to the root, so it is left alone
            if (path.Length > 3)
            {
                path = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            return path.Length == 0 ? null : path;
        }
    }
}
