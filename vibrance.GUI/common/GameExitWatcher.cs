using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace vibrance.GUI.common
{
    /// <summary>
    /// Watches the process that owns a game window and reports when it exits. Public (not
    /// internal, as the scaffold contract first had it) only because IVibranceProxy is public and
    /// SetGameExitWatcher exposes this type in its signature.
    /// </summary>
    public interface IGameExitWatcher
    {
        /// <summary>Token greater than 0 when armed, 0 if the process could not be opened.</summary>
        int Watch(IntPtr hWnd);
        void Cancel();
        /// <summary>Raised on the UI thread only, with the token Watch returned.</summary>
        event Action<int> GameExited;
    }

    /// <summary>
    /// Production IGameExitWatcher. The revert-on-alt-tab path is driven only by foreground events,
    /// and a fullscreen game closing often raises none (or lands focus on another monitor), so the
    /// resolution stayed at the game's mode until the user clicked something. This watches the game
    /// process itself instead.
    ///
    /// Process.Exited fires on a thread-pool thread; it is marshalled to the UI thread through the
    /// injected postToUiThread before GameExited is raised, because the proxies' state is only ever
    /// touched from the UI thread. Nothing here may throw: an exception escaping a thread-pool
    /// callback would take the whole process down.
    /// </summary>
    internal sealed class RealGameExitWatcher : IGameExitWatcher
    {
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        private readonly Action<Action> _postToUiThread;
        private readonly object _gate = new object();

        private Process _process;
        private uint _watchedPid;
        // The token of the currently armed watch, 0 when nothing is armed. A posted or in-flight
        // Exited for any other token (a replaced or cancelled watch) is ignored against this.
        private int _activeToken;
        private int _nextToken;
        // Bumped by every teardown (Cancel, re-arming another process). An exit already posted to
        // the UI thread captured the generation it was posted under and is dropped there if it moved.
        private int _generation;
        private bool _loggedFailure;

        internal RealGameExitWatcher(Action<Action> postToUiThread)
        {
            _postToUiThread = postToUiThread;
        }

        public event Action<int> GameExited;

        public int Watch(IntPtr hWnd)
        {
            try
            {
                uint pid;
                if (hWnd == IntPtr.Zero || GetWindowThreadProcessId(hWnd, out pid) == 0 || pid == 0)
                {
                    LogFailureOnce("the window has no owning process");
                    return 0;
                }

                Process process;
                int token;
                lock (_gate)
                {
                    if (_activeToken != 0 && _watchedPid == pid)
                    {
                        return _activeToken;
                    }

                    DisposeCurrentLocked();

                    process = Process.GetProcessById((int)pid);
                    token = ++_nextToken;
                    _process = process;
                    _watchedPid = pid;
                    _activeToken = token;
                }

                process.EnableRaisingEvents = true;
                process.Exited += delegate { OnProcessExited(token); };

                // EnableRaisingEvents on a process that is already gone normally raises Exited
                // itself, but do not rely on it - OnProcessExited posts at most once per token.
                // An unreadable HasExited (access denied) counts as "still running": rely on the
                // Exited event rather than reverting the resolution on the spot.
                bool alreadyExited;
                try { alreadyExited = process.HasExited; }
                catch (Exception) { alreadyExited = false; }
                if (alreadyExited)
                {
                    OnProcessExited(token);
                }
                return token;
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    DisposeCurrentLocked();
                }
                LogFailureOnce(ex.Message);
                return 0;
            }
        }

        public void Cancel()
        {
            try
            {
                lock (_gate)
                {
                    DisposeCurrentLocked();
                }
            }
            catch (Exception ex)
            {
                LogFailureOnce(ex.Message);
            }
        }

        // Caller holds _gate. Clearing _activeToken suppresses a not-yet-consumed Exited; bumping
        // _generation suppresses one already posted to the UI thread (see RaiseGameExited).
        private void DisposeCurrentLocked()
        {
            _generation++;
            _activeToken = 0;
            _watchedPid = 0;
            Process process = _process;
            _process = null;
            if (process != null)
            {
                try { process.Dispose(); }
                catch (Exception) { }
            }
        }

        // Thread-pool thread (or the caller's thread for an already-exited process). Never throws.
        private void OnProcessExited(int token)
        {
            try
            {
                int generation;
                lock (_gate)
                {
                    if (_activeToken != token)
                    {
                        return;
                    }
                    // Consumed: a second Exited for the same token (already-exited path plus the
                    // real event) must not post twice.
                    _activeToken = 0;
                    _watchedPid = 0;
                    generation = _generation;
                    Process process = _process;
                    _process = null;
                    if (process != null)
                    {
                        try { process.Dispose(); }
                        catch (Exception) { }
                    }
                }

                _postToUiThread(delegate { RaiseGameExited(token, generation); });
            }
            catch (Exception ex)
            {
                LogFailureOnce(ex.Message);
            }
        }

        private void RaiseGameExited(int token, int generation)
        {
            try
            {
                lock (_gate)
                {
                    if (_generation != generation)
                    {
                        return;
                    }
                }

                Action<int> handler = GameExited;
                if (handler != null)
                {
                    handler(token);
                }
            }
            catch (Exception ex)
            {
                Program.LogSafely(string.Format("Game exit handler failed: {0}", ex));
            }
        }

        private void LogFailureOnce(string reason)
        {
            bool first;
            lock (_gate)
            {
                first = !_loggedFailure;
                _loggedFailure = true;
            }
            if (first)
            {
                Program.LogSafely(string.Format("Could not watch the game process for exit, the resolution will only revert on a foreground change: {0}", reason));
            }
        }
    }
}
