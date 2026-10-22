using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace vibrance.GUI.common
{
    // Debounces the foreground-driven resolution apply/revert so a transient shell window cannot
    // churn the display mode.
    //
    // The loop this exists to break: the Win key (or a quick Alt+Tab) hands the foreground to a
    // transient shell window for a few tens of milliseconds. The revert branch used to react to
    // that immediately - a real mode change - which made Windows bounce focus back to the
    // fullscreen game, whose foreground event then applied the game mode - a second mode change -
    // and the shell window grabbed focus again while the display was still re-syncing. Repeat
    // forever. Two rules break it:
    //
    //   1. A REVERT never runs on the foreground event itself. It waits SettleDelayMs and then
    //      re-reads what is ACTUALLY in the foreground; if focus has already bounced back to the
    //      game, the re-dispatched event is an apply (a no-op, the mode is already right) and no
    //      mode change ever happened. The wait is stretched to cover whatever is left of
    //      ModeChangeHoldMs after our own last mode change, because focus churn caused by a mode
    //      set arrives in that window.
    //   2. An APPLY arriving inside ModeChangeHoldMs of a mode change we made is deferred the same
    //      way, instead of stacking a second mode set on top of the first while the display is
    //      still settling.
    //
    // Last event wins: each deferral Restarts the one timer, replacing the pending callback. The
    // callback does not trust the event that armed it - it re-reads the live foreground window -
    // so a superseded event can never act on stale state.
    //
    // Two bounds keep that debounce honest. MaxSettleWaitMs caps a trailing debounce that would
    // otherwise be postponed forever by a stream of non-game events each under SettleDelayMs apart
    // (every re-arm is clamped so the fire is no later than the FIRST arm of the pending chain plus
    // MaxSettleWaitMs). And a fire whose live foreground read fails (a NULL foreground during a UAC
    // or secure desktop, a process Windows will not name) re-arms ONCE at SettleDelayMs instead of
    // silently dropping the revert; if that retry fails too, nothing more is done.
    //
    // The timer is the existing IResolutionAdoptionTimer despite its name (it was written for the
    // resolution-adoption debounce): a one-shot, Restart-replaces-pending, Cancel countdown is all
    // this needs, so the production FormsResolutionAdoptionTimer is reused rather than duplicated.
    //
    // No "using System.Windows.Forms", for the same reason ResolutionAdoptionDebouncer has none:
    // this is policy, reachable from a fixture with a fake timer, a fake reader and a fake clock.
    // A null timer means "immediate": nothing is ever deferred. That is the default Shared, which
    // is what keeps every fixture that drives OnWinEventHook (which just calls HandleForeground) directly (and expects an immediate
    // revert) unchanged - only VibranceGUI installs the real, deferring settler.
    internal sealed class ForegroundSettler
    {
        // Chosen, not measured: long enough for a Win-key flash or the first half of a quick
        // Alt+Tab to hand focus back to the game, short enough that a deliberate switch to another
        // app still reverts without the user noticing a lag.
        internal const int SettleDelayMs = 400;

        // How long after our own mode change focus churn is still attributed to that change.
        internal const int ModeChangeHoldMs = 1500;

        // The longest a pending chain of re-arms may keep a revert/apply waiting, measured from the
        // first arm of the chain. Chosen, not measured: comfortably above the longest legitimate
        // single wait (ModeChangeHoldMs) so a real hold is never cut short on its own, yet a
        // window that spams foreground events cannot starve the revert indefinitely.
        internal const int MaxSettleWaitMs = 3000;

        private static ForegroundSettler _shared = new ForegroundSettler(null, null, null);

        internal static ForegroundSettler Shared
        {
            get { return _shared; }
            private set { _shared = value; }
        }

        // Null restores the immediate default (what a fixture reset wants).
        internal static void Install(ForegroundSettler settler)
        {
            Shared = settler ?? new ForegroundSettler(null, null, null);
        }

        // Fixture seam for the cursor, defaulting to the real one. Not a constructor parameter so
        // the constructor keeps its (timer, reader, tickCount) shape.
        internal Func<Point> CursorReader { get; set; }

        private readonly IResolutionAdoptionTimer _timer;
        private readonly IForegroundWindowReader _reader;
        private readonly Func<int> _tickCount;

        private bool _hasModeChange;
        private int _lastModeChangeTick;

        // First arm of the pending chain (see MaxSettleWaitMs); cleared when the chain fires or is
        // cancelled. A retry after a failed fire-time read is a fresh, unclamped single arm.
        private bool _hasChain;
        private int _chainStartTick;

        // True while the one allowed retry after a failed fire-time foreground read is pending.
        private bool _retryPending;

        // What the pending deferral was armed against. The cursor matters because the shell
        // desktop has no rect that names one monitor (its rect spans every monitor):
        // DesktopScreenResolver picks the monitor from where the click happened, and by the time
        // the timer fires the mouse may have moved on.
        private IntPtr _armedHwnd;
        private Point _armedCursor;

        internal ForegroundSettler(IResolutionAdoptionTimer timerOrNullForImmediate,
            IForegroundWindowReader reader, Func<int> tickCount)
        {
            _timer = timerOrNullForImmediate;
            _reader = reader;
            _tickCount = tickCount;
            CursorReader = ReadRealCursor;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        private static Point ReadRealCursor()
        {
            NativePoint p;
            return GetCursorPos(out p) ? new Point(p.X, p.Y) : Point.Empty;
        }

        // Milliseconds of the hold window still left, 0 when none. Unchecked subtraction so an
        // Environment.TickCount wraparound (every ~49.7 days of uptime) still yields the true
        // small elapsed value; a negative result (clock reading earlier than the note) is treated
        // as no hold rather than a huge one.
        private int HoldRemaining()
        {
            if (!_hasModeChange || _tickCount == null)
            {
                return 0;
            }
            int elapsed = unchecked(_tickCount() - _lastModeChangeTick);
            if (elapsed < 0 || elapsed >= ModeChangeHoldMs)
            {
                return 0;
            }
            return ModeChangeHoldMs - elapsed;
        }

        // The revert branch calls this first. With a timer it ALWAYS defers (true) - returning
        // false only when immediate. The delay is the longer of the settle delay and what is left
        // of the hold after our own last mode change.
        internal bool TryDeferRevert(Action<WinEventHookEventArgs> redispatch)
        {
            if (_timer == null)
            {
                return false;
            }
            Arm(Math.Max(SettleDelayMs, HoldRemaining()), redispatch);
            return true;
        }

        // The apply branch calls this. Only defers while a mode change of ours is still inside
        // its hold window; otherwise false and the caller applies right now.
        internal bool TryDeferApply(Action<WinEventHookEventArgs> redispatch)
        {
            if (_timer == null)
            {
                return false;
            }
            int remaining = HoldRemaining();
            if (remaining <= 0)
            {
                return false;
            }
            Arm(Math.Max(SettleDelayMs, remaining), redispatch);
            return true;
        }

        private void Arm(int delayMs, Action<WinEventHookEventArgs> redispatch)
        {
            IntPtr hwnd = IntPtr.Zero;
            string name;
            string path;
            if (_reader == null || !_reader.TryGetForeground(out hwnd, out name, out path))
            {
                // Nothing nameable in the foreground right now; the fire-time read decides again.
                // The armed hwnd is only a cursor-validity hint, so Zero (matches nothing) is right.
                hwnd = IntPtr.Zero;
            }
            _armedHwnd = hwnd;
            _armedCursor = CursorReader();

            // A new event gets its own retry; only the fire-time read failure consumes it.
            _retryPending = false;
            _timer.Restart(ClampToMaxWait(delayMs), delegate { OnElapsed(redispatch); });
        }

        // Starts the chain on its first arm, then shortens the delay so the fire lands no later
        // than chain start + MaxSettleWaitMs (never below 1ms: a chain already past its budget
        // fires as soon as the timer allows). Unchecked subtraction for the same wraparound reason
        // as HoldRemaining; a negative elapsed (clock reading earlier than the chain start) is
        // treated as 0 rather than as a huge budget.
        private int ClampToMaxWait(int delayMs)
        {
            if (_tickCount == null)
            {
                return delayMs;
            }
            int now = _tickCount();
            if (!_hasChain)
            {
                _hasChain = true;
                _chainStartTick = now;
            }
            int elapsed = unchecked(now - _chainStartTick);
            if (elapsed < 0)
            {
                elapsed = 0;
            }
            int budget = MaxSettleWaitMs - elapsed;
            if (budget < 1)
            {
                budget = 1;
            }
            return Math.Min(delayMs, budget);
        }

        private void OnElapsed(Action<WinEventHookEventArgs> redispatch)
        {
            IntPtr hwnd;
            string processName;
            string imagePath;

            // This chain has fired. Cleared before anything else so a re-arm from inside the
            // redispatch (or the retry below) starts a fresh chain.
            _hasChain = false;

            if (_reader == null || !_reader.TryGetForeground(out hwnd, out processName, out imagePath))
            {
                // Same no-dispatch rule as WinEventProc: an unnameable foreground never reaches
                // the match decision, so it cannot fall into the revert branch. But nothing else
                // re-arms after this fire, so a NULL foreground during a UAC / secure desktop would
                // silently drop the revert: retry ONCE at SettleDelayMs (the armed hwnd/cursor are
                // kept); if that read fails as well, do nothing, as before.
                if (_reader != null && _timer != null && !_retryPending)
                {
                    _retryPending = true;
                    _timer.Restart(SettleDelayMs, delegate { OnElapsed(redispatch); });
                }
                else
                {
                    _retryPending = false;
                }
                return;
            }
            _retryPending = false;

            // The captured cursor is only meaningful for the window it was captured against: if
            // focus has since moved to a different window, the click that chose it is gone.
            Point cursor = hwnd == _armedHwnd ? _armedCursor : CursorReader();

            redispatch(new WinEventHookEventArgs
            {
                Handle = hwnd,
                ProcessName = processName,
                ProcessImagePath = imagePath,
                CursorPosition = cursor
            });
        }

        // After any ChangeResolutionEx that returned Applied/AppliedUnverified, apply or revert.
        internal void NoteModeChanged()
        {
            if (_tickCount == null)
            {
                return;
            }
            _lastModeChangeTick = _tickCount();
            _hasModeChange = true;
        }

        // Stops a pending countdown, as ResolutionAdoptionDebouncer.Cancel does, and forgets the
        // chain and any pending retry. Two callers, one body: the top of a non-deferred apply (an
        // apply going ahead now supersedes a revert still waiting its turn, which would otherwise
        // fire later and undo it) and shutdown. They were separate methods with identical bodies.
        internal void Cancel()
        {
            _hasChain = false;
            _retryPending = false;
            if (_timer != null)
            {
                _timer.Cancel();
            }
        }
    }
}
