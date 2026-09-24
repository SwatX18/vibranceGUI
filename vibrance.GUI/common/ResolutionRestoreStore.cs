using System;
using System.IO;
using vibrance.GUI.AMD.vendor.utils;

namespace vibrance.GUI.common
{
    /// <summary>
    /// The seam over resolutionRestore.xml - the exact shape IVibranceRestoreStore.cs already
    /// uses, for the same reason (see that file's own header comment): ResolutionRestoreHelper is
    /// a static class reached only from PRIVATE STATIC proxy methods (NvidiaDynamicVibranceProxy.
    /// OnWinEventHook and its AMD counterpart), so there is no constructor anywhere on that call
    /// path to inject a dependency into. RealResolutionRestoreStore (below) is the only production
    /// implementation; NullResolutionRestoreStore is the default, so a headless fixture run or a
    /// --selftest-* run that never reaches Program.Main gets the same protection LogSink.Current/
    /// VibranceRestoreStore.Current already give their own files: it can never touch a real user's
    /// %APPDATA%\vibranceGUI\resolutionRestore.xml just by existing. Program.Main only ever swaps
    /// in the real store for a normal run - see its own call site, right beside the equivalent
    /// VibranceRestoreStore.ResetForTests(new RealVibranceRestoreStore()) line.
    /// </summary>
    internal interface IResolutionRestoreStore
    {
        /// <summary>
        /// The persisted record, or null when there is none, or when the file could not be parsed
        /// - see AtomicXmlFile.TryRead's own comment for why a torn write must read back exactly
        /// like "no record", never throw into startup.
        /// </summary>
        ResolutionRestoreRecord TryRead();

        /// <summary>
        /// Replaces the persisted record with record, atomically - see AtomicXmlFile.Write's own
        /// comment for the write-temp-then-replace mechanics. Best-effort: a write that fails
        /// (permissions, disk full, a concurrent AV scan holding the file) is logged once and
        /// otherwise swallowed, never thrown into a caller that is, in every production call site,
        /// running synchronously inside a WinEvent foreground callback.
        /// </summary>
        void Write(ResolutionRestoreRecord record);

        /// <summary>
        /// Removes the persisted record, if any. A no-op, not an error, when there is nothing to
        /// remove.
        /// </summary>
        void Delete();
    }

    /// <summary>
    /// The only production IResolutionRestoreStore - one file,
    /// %APPDATA%\vibranceGUI\resolutionRestore.xml by default, resolved through
    /// CommonUtils.GetVibrance_GUI_AppDataPath() exactly as RealVibranceRestoreStore's own file is,
    /// and deliberately a DIFFERENT file from vibranceRestore.xml - see ResolutionRestoreRecord's
    /// own header for why. The internal constructor taking an explicit path exists only for a
    /// fixture, mirroring RealVibranceRestoreStore's own internal(string) constructor: a check can
    /// point this at a fixture-private temp file and drive a real round trip through real
    /// XmlSerializer/File calls without ever touching the user's actual AppData folder.
    /// </summary>
    internal class RealResolutionRestoreStore : IResolutionRestoreStore
    {
        private readonly string _filePath;

        public RealResolutionRestoreStore()
            : this(Path.Combine(CommonUtils.GetVibrance_GUI_AppDataPath(), "resolutionRestore.xml"))
        {
        }

        internal RealResolutionRestoreStore(string filePath)
        {
            _filePath = filePath;
        }

        public ResolutionRestoreRecord TryRead()
        {
            return AtomicXmlFile.TryRead<ResolutionRestoreRecord>(_filePath);
        }

        public void Write(ResolutionRestoreRecord record)
        {
            try
            {
                AtomicXmlFile.Write(_filePath, record);
            }
            catch (Exception ex)
            {
                Program.LogSafely("Failed to persist the resolution restore record: " + ex.Message);
            }
        }

        public void Delete()
        {
            AtomicXmlFile.TryDeleteQuietly(_filePath);
        }
    }

    /// <summary>
    /// The default IResolutionRestoreStore (see ResolutionRestoreStore.Current below) - mirrors
    /// NullVibranceRestoreStore exactly, and exists for the same reason: anything that never
    /// reaches Program.Main - most importantly a reflection harness calling a fixture's Run()
    /// directly, which is how every self test in this codebase actually gets run - must not touch
    /// the real, shared %APPDATA%\vibranceGUI\resolutionRestore.xml just because
    /// ResolutionRestoreHelper ran. TryRead answering null here is indistinguishable from "no
    /// record exists", which is exactly the safe, do-nothing answer a fixture run should get.
    /// </summary>
    internal class NullResolutionRestoreStore : IResolutionRestoreStore
    {
        public ResolutionRestoreRecord TryRead()
        {
            return null;
        }

        public void Write(ResolutionRestoreRecord record)
        {
        }

        public void Delete()
        {
        }
    }

    /// <summary>
    /// Static injection point - mirrors VibranceRestoreStore exactly: a single settable static,
    /// defaulting to the Null implementation, that Program.Main swaps to the real one for a normal
    /// run only (see that call site, placed right beside VibranceRestoreStore's own swap).
    /// </summary>
    internal static class ResolutionRestoreStore
    {
        private static IResolutionRestoreStore _current = new NullResolutionRestoreStore();

        internal static IResolutionRestoreStore Current
        {
            get { return _current; }
            set { _current = value ?? new NullResolutionRestoreStore(); }
        }

        internal static void ResetForTests(IResolutionRestoreStore store)
        {
            Current = store;
        }
    }
}
