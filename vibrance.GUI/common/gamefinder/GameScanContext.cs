using System;

namespace vibrance.GUI.common.gamefinder
{
    /// <summary>
    /// The callback bundle handed to a source. Keeps sources free of any BackgroundWorker or
    /// WinForms reference.
    /// </summary>
    public class GameScanContext
    {
        private readonly Func<bool> _isCancelled;
        private readonly Action<GameCandidate> _report;
        private readonly Action<string, string> _reportSkipped;
        private readonly Action<string, Exception> _reportError;

        // Every delegate is optional. A null isCancelled means never cancelled, matching
        // ExecutableEnumerator.Enumerate; a null reporting delegate makes that report a no-op.
        // A throwaway console driver therefore needs no ceremony to construct one of these.
        public GameScanContext(Func<bool> isCancelled,
                               Action<GameCandidate> report,
                               Action<string, string> reportSkipped,   // gameName, reason
                               Action<string, Exception> reportError)
        {
            _isCancelled = isCancelled;
            _report = report;
            _reportSkipped = reportSkipped;
            _reportError = reportError;
        }

        public bool IsCancelled
        {
            get { return _isCancelled != null && _isCancelled(); }
        }

        public void Report(GameCandidate candidate)
        {
            if (_report == null || candidate == null)
                return;

            _report(candidate);
        }

        // Skips and errors are logged by the callback, and VibranceGUI.Log opens the log file with
        // File.AppendText, which throws IOException when the UI thread is writing to it at the same
        // moment. A scan is never allowed to die of a failed diagnostic, so both swallow.
        public void ReportSkipped(string gameName, string reason)
        {
            if (_reportSkipped == null)
                return;

            try
            {
                _reportSkipped(gameName, reason);
            }
            catch (Exception)
            {
            }
        }

        public void ReportError(string sourceName, Exception ex)
        {
            if (_reportError == null)
                return;

            try
            {
                _reportError(sourceName, ex);
            }
            catch (Exception)
            {
            }
        }
    }
}
