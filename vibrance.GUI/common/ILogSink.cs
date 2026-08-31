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
    /// The self-test sink Program.cs installs for every --selftest-* flag (see Main), so running
    /// any self test never appends to the real, shared vibranceGUI.log - the file this user's own
    /// configured games' diagnostic history lives in. Deliberately does nothing with either
    /// overload: nothing reads a self-test run's log output back through this sink, unlike the
    /// fixture-owned fakes (e.g. ProfileToggleFixture's RecordingLogSink) built to assert on
    /// specific messages.
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
    /// it, install its own fake, and restore exactly what was there before - not always the real
    /// sink - since Program.cs may already have a self-test sink of its own installed for the
    /// whole run.
    /// </summary>
    internal static class LogSink
    {
        private static ILogSink _current = new RealLogSink();

        internal static ILogSink Current
        {
            get { return _current; }
            set { _current = value ?? new RealLogSink(); }
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
