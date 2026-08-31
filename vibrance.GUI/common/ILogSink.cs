using System;
using System.IO;

namespace vibrance.GUI.common
{
    /// <summary>
    /// The seam over vibranceGUI's one log sink - same shape as IForegroundWindowReader/
    /// IHdrStateReader: RealLogSink (below) is the only production implementation, hardcoded to
    /// the same %APPDATA%\vibranceGUI\vibranceGUI.log path and the same "Log Entry : &lt;time&gt;
    /// &lt;date&gt;" / "-------------------------------" format VibranceGUI.Log(string)/
    /// Log(Exception) always wrote directly, byte for byte. VibranceGUI.Log itself stays the
    /// facade every one of this codebase's existing call sites keeps calling - only its two
    /// method bodies now delegate to LogSink.Current instead of opening the file themselves, so a
    /// fixture can substitute a fake here (LogSink.Current, LogSink.ResetForTests) and assert on
    /// exactly what got logged, instead of appending to the real, shared, unbounded
    /// vibranceGUI.log file the way every self-test run used to.
    /// </summary>
    internal interface ILogSink
    {
        void Write(string message);
        void Write(Exception ex);
    }

    /// <summary>
    /// The only production ILogSink. Both bodies are copied verbatim from the pre-seam
    /// VibranceGUI.Log(string)/Log(Exception) - path, format and append semantics are byte
    /// identical, so existing logs are appended to, never replaced or reformatted.
    /// </summary>
    internal class RealLogSink : ILogSink
    {
        public void Write(Exception ex)
        {
            using (StreamWriter w = File.AppendText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vibranceGUI\\vibranceGUI.log")))
            {
                w.Write("\r\nLog Entry : ");
                w.WriteLine("{0} {1}", DateTime.Now.ToLongTimeString(),
                    DateTime.Now.ToLongDateString());
                w.WriteLine("Exception Found:\nType: {0}", ex.GetType().FullName);
                w.WriteLine("Message: {0}", ex.Message);
                w.WriteLine("Source: {0}", ex.Source);
                w.WriteLine("Stacktrace: {0}", ex.StackTrace);
                w.WriteLine("Exception String: {0}", ex.ToString());

                w.WriteLine("-------------------------------");
            }
        }

        public void Write(string message)
        {
            using (StreamWriter w = File.AppendText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vibranceGUI\\vibranceGUI.log")))
            {
                w.Write("\r\nLog Entry : ");
                w.WriteLine("{0} {1}", DateTime.Now.ToLongTimeString(),
                    DateTime.Now.ToLongDateString());
                w.WriteLine(message);
                w.WriteLine("-------------------------------");
            }
        }
    }

    /// <summary>
    /// The default ILogSink (see LogSink._current below), so anything that never reaches
    /// Program.Main - a reflection harness that calls a fixture's Run() directly, a future tool,
    /// a fixture invoked on its own - is silent by default and never appends to the real, shared
    /// vibranceGUI.log, the file this user's own configured games' diagnostic history lives in.
    /// Program.Main only overrides this with RealLogSink for a normal (non --selftest-*) run; see
    /// Main for why the old approach - installing NullLogSink per --selftest-* flag instead - left
    /// exactly that harness unprotected. Deliberately does nothing with either overload: nothing
    /// reads a self-test run's log output back through this sink, unlike the fixture-owned fakes
    /// (e.g. ProfileToggleFixture's RecordingLogSink) built to assert on specific messages.
    /// </summary>
    internal class NullLogSink : ILogSink
    {
        public void Write(string message)
        {
        }

        public void Write(Exception ex)
        {
        }
    }

    /// <summary>
    /// Static injection point - mirrors HdrStateTracker's settable _reader/ResetForTests (a single
    /// swappable static), not DeviceGammaRampHelper's per-call IGammaDevice parameter, because
    /// VibranceGUI.Log's own signature is the existing-call-site contract this seam must not
    /// disturb. Current has a public setter (unlike HdrStateTracker's reader) so a check can save
    /// it, install its own fake, and restore exactly what was there before - not always
    /// RealLogSink - since Current defaults to NullLogSink and Program.Main only ever swaps in
    /// RealLogSink for a normal run (see below), so a previously-saved sink is just as often
    /// NullLogSink itself.
    /// </summary>
    internal static class LogSink
    {
        // Defaults to NullLogSink, not RealLogSink. Anything that never runs through
        // Program.Main - most importantly a reflection harness that calls a fixture's Run()
        // directly, which is how every self test in this codebase actually gets run, not through
        // the --selftest-* command line Main parses - gets this default untouched. A prior version
        // of this seam defaulted to RealLogSink and relied on Main installing NullLogSink for
        // every --selftest-* flag; that only ever protected Main's own entry point; a harness that
        // bypasses Main bypassed that guard too; measured cost: the real, shared
        // %APPDATA%\vibranceGUI\vibranceGUI.log grew by over 35,000 bytes across one such suite
        // run, with settings-fixture parse-failure lines mixed into a user's own diagnostic
        // history. The tradeoff this direction accepts: if Program.Main's own RealLogSink install
        // below were ever removed, or Main itself bypassed for what is actually a normal run,
        // production would log nothing. That is the safer failure - an empty log, or a bug report
        // with nothing to attach, is visible and checkable - where the old default silently wrote
        // to a user's disk and nobody noticed until someone measured the byte count. Do not "fix"
        // this back to RealLogSink.
        private static ILogSink _current = new NullLogSink();

        internal static ILogSink Current
        {
            get { return _current; }
            set { _current = value ?? new NullLogSink(); }
        }

        /// <summary>
        /// Installs sink as the target every VibranceGUI.Log call writes through for the rest of
        /// this process, or the real, file-backed sink when sink is null.
        /// </summary>
        internal static void ResetForTests(ILogSink sink)
        {
            Current = sink;
        }
    }
}
