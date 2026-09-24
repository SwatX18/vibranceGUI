using System;
using System.IO;
using System.Xml;
using System.Xml.Serialization;
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

        public VibranceRestoreRecord TryRead()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return null;
                }

                using (XmlReader reader = XmlReader.Create(_filePath))
                {
                    XmlSerializer serializer = new XmlSerializer(typeof(VibranceRestoreRecord));
                    return (VibranceRestoreRecord)serializer.Deserialize(reader);
                }
            }
            catch (Exception)
            {
                // Torn or unparseable - most likely a write this same feature interrupted by the
                // very abnormal exit it exists to recover from (see Write's own atomic-write
                // comment for the narrow window that can still leave a torn file: the temp file
                // itself, mid-write). A bad record must never throw into startup, and it can never
                // become readable on a later run either, so there is nothing to gain by leaving it
                // in place - discard it now rather than have every future launch re-attempt and
                // re-fail the same parse forever.
                TryDeleteQuietly();
                return null;
            }
        }

        public void Write(VibranceRestoreRecord record)
        {
            try
            {
                string tempPath = _filePath + ".tmp";
                using (XmlWriter writer = XmlWriter.Create(tempPath))
                {
                    XmlSerializer serializer = new XmlSerializer(typeof(VibranceRestoreRecord));
                    serializer.Serialize(writer, record);
                    writer.Flush();
                }

                // Atomic swap: File.Replace is a single filesystem transaction, so a process
                // killed anywhere from here onward leaves either the OLD file untouched or the NEW
                // one fully in place - never a half-written vibranceRestore.xml. The only file that
                // CAN be left torn by a kill is the ".tmp" one above, while the Serialize call
                // itself is still running - TryRead's own try/catch (plus its delete-on-failure) is
                // what makes that safe: File.Replace never even runs in that case, so the real file
                // this method is responsible for is never the one left torn.
                // File.Replace requires an existing destination; delete-then-move covers the first
                // write ever made for a given install, when there is nothing yet to replace.
                if (File.Exists(_filePath))
                {
                    File.Replace(tempPath, _filePath, null);
                }
                else
                {
                    // File.Move alone would throw IOException if a file appeared at _filePath
                    // between the File.Exists check above and here (another thread's write, in
                    // principle - this app is single-instance, so not expected in practice); the
                    // delete first makes this branch safe to fall into either way.
                    File.Delete(_filePath);
                    File.Move(tempPath, _filePath);
                }
            }
            catch (Exception ex)
            {
                Program.LogSafely("Failed to persist the vibrance restore record: " + ex.Message);
            }
        }

        public void Delete()
        {
            TryDeleteQuietly();
        }

        private void TryDeleteQuietly()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    File.Delete(_filePath);
                }
            }
            catch (Exception)
            {
                // Best-effort. A leftover file here is inert, not actively harmful: the replay
                // only ever trusts what it reads on the ONE launch it runs, and a stale record can
                // only over-name displays, never under-name them - see VibranceRestoreHelper's own
                // journaling-rules comment for why that is safe.
            }
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
