using System;
using System.Windows.Forms;

namespace vibrance.GUI.common
{
    // Production IResolutionAdoptionTimer - see ResolutionAdoptionDebouncer.cs for the interface
    // this implements and why a fixture-testable seam sits in front of it instead of
    // ResolutionAdoptionDebouncer touching System.Windows.Forms.Timer directly.
    //
    // Backed by a single System.Windows.Forms.Timer left Enabled = false whenever nothing is
    // pending, deliberately mirroring the "cost effectively nothing when idle" requirement upstream
    // issue #156 exists to enforce for this permanently-tray-resident app: Windows only holds a
    // native timer resource open while Enabled is true (Timer.Enabled = true calls SetTimer;
    // = false calls KillTimer), so an idle instance of this class - the overwhelmingly common case,
    // since it is only ever armed while a resolution-adoption decision is genuinely pending - costs
    // nothing beyond the small managed Timer object itself. Nothing here polls; the Tick event is
    // the OS telling this class time has passed, not this class asking.
    //
    // _timer is never Dispose()'d anywhere in this class or its one caller (VibranceGUI holds
    // exactly one instance, for its own lifetime). Deliberate, not an oversight: this is a
    // tray-resident Form that only ever goes away at process exit, at which point Windows tears
    // down every native resource this process holds regardless, including whatever SetTimer left
    // open. A Dispose() call here would have nothing meaningful to run before that point in
    // practice, and CleanUp() already stops the timer via ResolutionAdoptionDebouncer.Cancel() (see
    // that method's own comment), which is what actually matters: no further Tick after shutdown
    // begins.
    internal sealed class FormsResolutionAdoptionTimer : IResolutionAdoptionTimer
    {
        private readonly Timer _timer = new Timer();
        private Action _onElapsed;

        internal FormsResolutionAdoptionTimer()
        {
            _timer.Tick += OnTick;
        }

        public void Restart(int delayMs, Action onElapsed)
        {
            // Disabled before Interval is reassigned: a running Timer restarts its own countdown
            // from zero the moment Interval changes regardless, but stopping first keeps that from
            // depending on an implementation detail of System.Windows.Forms.Timer that its own
            // documentation makes no guarantee about.
            //
            // THREAD AFFINITY IS ASSUMED, NOT ENFORCED HERE: System.Windows.Forms.Timer delivers
            // Tick through a hidden window's message loop on whichever thread first sets Enabled =
            // true, and that thread must actually be pumping messages or Tick simply never fires -
            // silently, no exception, nothing to observe. Nothing in this class checks that Restart
            // is being called from such a thread; today's only caller (VibranceGUI.OnDisplaySettingsChanged,
            // itself already marshaled onto the UI thread before it ever reaches
            // ResolutionAdoptionDebouncer) is what actually guarantees it. A future caller arming
            // this from a background thread would create the hidden window there instead, where
            // nothing pumps, and the countdown would simply never elapse.
            _timer.Enabled = false;
            _onElapsed = onElapsed;
            _timer.Interval = delayMs;
            _timer.Enabled = true;
        }

        public void Cancel()
        {
            _timer.Enabled = false;
            // _onElapsed = null here is belt-and-suspenders, not load-bearing: Enabled = false
            // (KillTimer) alone already stops Tick from firing again, so nothing currently depends
            // on this assignment - deleting it would still pass every check in this codebase. Kept
            // anyway so production does not lean entirely on WinForms' own, undocumented handling of
            // a stale timer ID if Enabled's guarantee ever turns out to be less absolute than it
            // looks; a lower-stakes version of the same "trust the framework, but not blindly"
            // caution Restart's own comment states plainly above.
            _onElapsed = null;
        }

        private void OnTick(object sender, EventArgs e)
        {
            // One-shot: disabled and the field cleared BEFORE the callback runs, not after. This is
            // NOT because performRefresh (RebuildWindowsResolutionSettings) can trigger another
            // DisplaySettingsChanged itself - it can't: that method only ever READS the current
            // mode, through WindowsResolutionRefresher.Refresh, and never calls ChangeMode, so it
            // cannot be the source of a re-entrant notification.
            //
            // The real reason is IResolutionAdoptionTimer.Restart's own contract: "a callback may
            // re-arm the timer from inside its own elapse, and the re-arm survives" - a caller
            // OTHER than this one instance's own callback CAN legitimately do this (a second,
            // unrelated DisplaySettingsChanged arriving synchronously while this Tick is still on
            // the stack is not ruled out by anything in this class), and clearing _onElapsed before
            // invoking is what lets that re-arm's own Restart call win instead of being silently
            // wiped by this method's own cleanup running after it. FakeResolutionAdoptionTimer.Elapse
            // models exactly this ordering, and ResolutionChangeFixture pins it as a check
            // (CheckDebounceCallbackMayReArmDuringItsOwnElapse) - but only on that fake. Nothing
            // in this codebase exercises this ordering on the REAL System.Windows.Forms.Timer here,
            // for the same reason nothing exercises RealDisplayModeDevice directly: doing so needs a
            // live message pump, which the fixture must never have (see ResolutionChangeFixture.cs's
            // own header). This method's own correctness rests on inspection, not a check - the same
            // trust boundary RealDisplayModeDevice already carries in this codebase.
            _timer.Enabled = false;
            Action onElapsed = _onElapsed;
            _onElapsed = null;
            if (onElapsed != null)
            {
                onElapsed();
            }
        }
    }
}
