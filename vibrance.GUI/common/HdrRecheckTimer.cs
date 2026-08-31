using System;
using System.Windows.Forms;

namespace vibrance.GUI.common
{
    // The seam VibranceGUI drives its upstream #147 part 2 HDR re-check poll through - the same
    // interface/fake-timer shape as IResolutionAdoptionTimer/FormsResolutionAdoptionTimer
    // (ResolutionAdoptionDebouncer.cs), but RECURRING rather than one-shot: this poll has to keep
    // ticking for as long as any game level is held anywhere (VibranceRestoreHelper.HoldingCount
    // > 0), not fire once and stop, because nothing here can tell in advance WHEN - or whether at
    // all - Windows' own HDR state will flip under a game already in the foreground. See
    // VibranceGUI.OnHdrRecheckTick for the cheap idle gate that keeps a tick's own cost down to
    // two branches and no P/Invoke while HoldingCount is 0 (upstream #156) - that gate, not this
    // class stopping and starting the native timer, is what keeps this permanently-tray-resident
    // app's UI thread cheap while nothing is being managed.
    internal interface IHdrRecheckTimer
    {
        // Starts firing onTick every intervalMs, indefinitely, until Stop() is called. Calling
        // this again while already running restarts the interval cleanly - mirrors
        // IResolutionAdoptionTimer.Restart's own "disabled before Interval is reassigned"
        // ordering, for the same reason (see FormsResolutionAdoptionTimer.Restart's comment).
        void Start(int intervalMs, Action onTick);

        // Stops ticking. A no-op if not running.
        void Stop();
    }

    // Backed by a single System.Windows.Forms.Timer, started once (VibranceGUI's constructor
    // calls Start exactly once, like _resolutionAdoptionDebouncer's own construction) and left
    // running for the app's whole lifetime - unlike FormsResolutionAdoptionTimer, this is NOT
    // re-armed as one-shot per event: nothing here knows in advance when Windows' own HDR state
    // will change, so the only way to notice it at all is to keep asking periodically. What
    // actually keeps this cheap while idle is OnHdrRecheckTick's own gate on
    // VibranceRestoreHelper.HoldingCount, not this class stopping and starting SetTimer - see
    // that method's own comment for why upstream #156 makes that distinction load-bearing.
    internal sealed class FormsHdrRecheckTimer : IHdrRecheckTimer
    {
        private readonly Timer _timer = new Timer();
        private Action _onTick;

        internal FormsHdrRecheckTimer()
        {
            _timer.Tick += OnTick;
        }

        public void Start(int intervalMs, Action onTick)
        {
            // Disabled before Interval is reassigned - see FormsResolutionAdoptionTimer.Restart's
            // own comment for why this ordering is kept explicit rather than relied upon.
            _timer.Enabled = false;
            _onTick = onTick;
            _timer.Interval = intervalMs;
            _timer.Enabled = true;
        }

        public void Stop()
        {
            _timer.Enabled = false;
            _onTick = null;
        }

        // Deliberately does NOT disable the timer before invoking the callback (unlike
        // FormsResolutionAdoptionTimer.OnTick's own one-shot cleanup) - this timer is meant to
        // keep ticking on its own fixed interval regardless of what OnHdrRecheckTick decides to do
        // with any single tick, so there is nothing here for a callback to "re-arm".
        private void OnTick(object sender, EventArgs e)
        {
            Action onTick = _onTick;
            if (onTick != null)
            {
                onTick();
            }
        }
    }
}
