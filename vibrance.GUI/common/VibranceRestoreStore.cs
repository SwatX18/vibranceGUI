using System;
using System.IO;
using vibrance.GUI.AMD.vendor.utils;

namespace vibrance.GUI.common
{
    /// <summary>
    /// The seam over vibranceRestore.xml - the exact shape ILogSink.cs already uses, for the same
    /// reason (see that file's own header comment): VibranceRestoreHelper is a static class
    /// reached only from PRIVATE STATIC proxy methods (NvidiaDynamicVibranceProxy.OnWinEventHook
    /// and its AMD counterpart), so there is no constructor anywhere on that call path to inject a
    /// dependency into. RealVibranceRestoreStore (below) is the only production implementation;
    /// NullVibranceRestoreStore is the default, so a headless fixture run or a --selftest-* run
    /// that never reaches Program.Main gets the same protection LogSink.Current already gives
    /// vibranceGUI.log: it can never touch a real user's %APPDATA%\vibranceGUI\vibranceRestore.xml
    /// just by existing. Program.Main only ever swaps in the real store for a normal run - see its
    /// own call site, right beside the equivalent LogSink.ResetForTests(new RealLogSink()) line.
    /// </summary>
    internal interface IVibranceRestoreStore
    {
        /// <summary>
        /// The persisted record, or null when there is none, or when the file could not be parsed
        /// - a torn write (the process was killed mid-write) must read back exactly like "no
        /// record", never throw into startup: the failure this whole feature exists to recover
        /// from is abnormal termination, so termination mid-write is squarely in scope, not an
        /// edge case. A file that fails to parse is also deleted as part of this call - see
        /// RealVibranceRestoreStore's own comment for why leaving it in place would help nobody.
        /// </summary>
        VibranceRestoreRecord TryRead();

        /// <summary>
        /// Replaces the persisted record with record, atomically - see RealVibranceRestoreStore's
        /// own comment for the write-temp-then-replace mechanics. Best-effort: a write that fails
        /// (permissions, disk full, a concurrent AV scan holding the file) is logged once and
        /// otherwise swallowed, never thrown into a caller that is, in every production call site,
        /// running synchronously inside a WinEvent foreground callback.
        /// </summary>
        void Write(VibranceRestoreRecord record);

        /// <summary>
        /// Removes the persisted record, if any. A no-op, not an error, when there is nothing to
        /// remove.
        /// </summary>
        void Delete();
    }

    /// <summary>
    /// The only production IVibranceRestoreStore - one file,
    /// %APPDATA%\vibranceGUI\vibranceRestore.xml by default, resolved through
    /// CommonUtils.GetVibrance_GUI_AppDataPath() exactly as SettingsController's own two files are
    /// (see that class's _fileName/_fileNameApplicationSettings). The internal constructor taking
    /// an explicit path exists only for VibranceRestorePersistenceFixture, mirroring
    /// SettingsController's own internal(string, string) constructor: a check can point this at a
    /// fixture-private temp file and drive a real round trip through real XmlSerializer/File calls
    /// without ever touching the user's actual AppData folder.
    /// </summary>
    internal class RealVibranceRestoreStore : IVibranceRestoreStore
    {
        private readonly string _filePath;

        public RealVibranceRestoreStore()
            : this(Path.Combine(CommonUtils.GetVibrance_GUI_AppDataPath(), "vibranceRestore.xml"))
        {
        }

        internal RealVibranceRestoreStore(string filePath)
        {
            _filePath = filePath;
        }

        // The atomic write-temp-then-File.Replace mechanics and the parse-or-discard read used to
        // live here directly; both now live in AtomicXmlFile, shared with RealResolutionRestoreStore
        // (D4's resolution half) rather than duplicated a second time - see that class's own header
        // for the full reasoning. Behaviour is unchanged: TryRead/Write/Delete below do exactly what
        // they always did, through the shared implementation.
        public VibranceRestoreRecord TryRead()
        {
            return AtomicXmlFile.TryRead<VibranceRestoreRecord>(_filePath);
        }

        public void Write(VibranceRestoreRecord record)
        {
            try
            {
                AtomicXmlFile.Write(_filePath, record);
            }
            catch (Exception ex)
            {
                // Best-effort: a write that fails (permissions, disk full, a concurrent AV scan
                // holding the file) is logged once and otherwise swallowed, never thrown into a
                // caller that is, in every production call site, running synchronously inside a
                // WinEvent foreground callback.
                Program.LogSafely("Failed to persist the vibrance restore record: " + ex.Message);
            }
        }

        public void Delete()
        {
            AtomicXmlFile.TryDeleteQuietly(_filePath);
        }
    }

    /// <summary>
    /// The default IVibranceRestoreStore (see VibranceRestoreStore.Current below) - mirrors
    /// NullLogSink exactly, and exists for the same reason: anything that never reaches
    /// Program.Main - most importantly a reflection harness calling a fixture's Run() directly,
    /// which is how every self test in this codebase actually gets run - must not touch the real,
    /// shared %APPDATA%\vibranceGUI\vibranceRestore.xml just because VibranceRestoreHelper ran.
    /// TryRead answering null here is indistinguishable from "no record exists", which is exactly
    /// the safe, do-nothing answer a fixture run should get.
    /// </summary>
    internal class NullVibranceRestoreStore : IVibranceRestoreStore
    {
        public VibranceRestoreRecord TryRead()
        {
            return null;
        }

        public void Write(VibranceRestoreRecord record)
        {
        }

        public void Delete()
        {
        }
    }

    /// <summary>
    /// Static injection point - mirrors LogSink exactly: a single settable static, defaulting to
    /// the Null implementation, that Program.Main swaps to the real one for a normal run only (see
    /// that call site, placed right beside LogSink.ResetForTests(new RealLogSink())).
    /// </summary>
    internal static class VibranceRestoreStore
    {
        private static IVibranceRestoreStore _current = new NullVibranceRestoreStore();

        internal static IVibranceRestoreStore Current
        {
            get { return _current; }
            set { _current = value ?? new NullVibranceRestoreStore(); }
        }

        internal static void ResetForTests(IVibranceRestoreStore store)
        {
            Current = store;
        }
    }
}
