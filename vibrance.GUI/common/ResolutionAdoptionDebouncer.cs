using System;

namespace vibrance.GUI.common
{
    // A NARROWING, not a fix, for the resolution-snapshot defect: a game that changes the display
    // mode itself, with no vibranceGUI profile applied, could get captured as "the user's desktop
    // resolution" - see ResolutionChangeFixture.CheckAdoptedForeignModeSelfHealsOnceItIsGone for the
    // four reasons a value-based fix (matching against a configured
    // ApplicationSetting.ResolutionSettings entry) was REJECTED outright, not merely postponed.
    // Reason 4 there is the one this class narrows, and ONLY for a mode that does not outlast
    // DebounceIntervalMs: at the instant a bare DisplaySettingsChanged notification arrives, "a game
    // changed the mode with no profile applied" and "the user's own genuine desktop mode happens to
    // equal some configured entry" are still OBSERVATIONALLY IDENTICAL, and reason 4 still binds -
    // nothing available at that instant tells them apart. Elapsed stability tells them apart only
    // for the shape of the problem this class actually closes: since a foreign mode that does not
    // outlast DebounceIntervalMs is gone before any capture can run, while a genuine desktop change
    // is still there.
    //
    // THE RESIDUAL EXPOSURE THIS DOES NOT CLOSE: a game that holds its own foreign mode for longer
    // than DebounceIntervalMs - the dominant shape in practice, since exclusive fullscreen at the
    // game's own resolution typically lasts the whole session, not 2 seconds - is still adopted
    // exactly as it was adopted before this class existed, the moment DisplaySettingsChanged fires
    // and the debounce elapses with that mode still live. It still self-heals once that mode goes
    // away and another notification's debounce elapses, per
    // CheckAdoptedForeignModeSelfHealsOnceItIsGone - this class changes nothing about that. What
    // this class actually removes is adoption of foreign modes that DON'T outlast the interval:
    // startup/exit mode flaps, alt-tab restore/re-set pairs, launcher and anti-cheat mode sets, and
    // the window at game exit where the dying mode is still live before Windows restores the
    // desktop. Real, worth fixing, and NOT the same claim as "a game's mode is never adopted".
    //
    // ALSO NOT COVERED: the constructor's own initial build (VibranceGUI.cs, RebuildWindowsResolutionSettings(true))
    // is called directly, never through this class, and always resolves preserveCapturedMode to
    // false regardless of reality - _v is not assigned until after that call returns, so
    // "_v != null && ..." reads false unconditionally at that point. It cannot be debounced anyway:
    // its result seeds the readonly _supportedResolutionList and is handed to the vendor proxy in
    // the same constructor, both of which need a value before the constructor returns, not
    // DebounceIntervalMs later. Restarting vibranceGUI while a game already holds a foreign mode
    // captures that mode immediately, same as on master before this class existed - unchanged, not a
    // regression this class introduces.
    //
    // THE ACCEPTED COST IS SHARPER THAN "BRIEFLY LATE": if a countdown armed while
    // preserveCapturedMode was false is still pending when a profiled game's OWN apply sets
    // isResolutionChangeApplied - whether that pending countdown later elapses on its own, or is
    // cancelled outright by the preserveCapturedMode==true branch below - RebuildWindowsResolutionSettings
    // re-reads the flag as true at whichever moment it actually runs and preserves the OLD,
    // pre-change Item1 (the single most dangerous line in WindowsResolutionRefresher.Refresh, working
    // exactly as designed). The user's genuine change is not "2 seconds late" in that case - it is
    // DROPPED: Item1 stays at the pre-change value for the entire game session, and the game's own
    // exit-revert then actively drags the REAL desktop back to that stale value, since the revert
    // path applies exactly what Item1 holds. The desktop only ends up matching Item1 again because
    // the revert forced it there, not because the user's choice survived. Narrow (it needs a genuine
    // resolution change and a profiled game launch inside the same DebounceIntervalMs window to
    // coincide) and self-consistent (Item1 and the live mode are never OUT of sync with each other
    // once this resolves), but real, and worth naming rather than filing under "briefly lagging".
    //
    // This class owns exactly the flap-shaped part of the wait: on OnDisplaySettingsChanged, it does
    // not let a live-read capture (WindowsResolutionRefresher.Refresh, reached through VibranceGUI's
    // own RebuildWindowsResolutionSettings) run immediately. It arms a one-shot countdown instead,
    // and only lets the capture run once DebounceIntervalMs has passed with no further
    // DisplaySettingsChanged arriving in between. WindowsResolutionRefresher.Refresh itself is
    // deliberately UNCHANGED by this fix - it still adopts whatever is live the instant it actually
    // runs, exactly as CheckAdoptedForeignModeSelfHealsOnceItIsGone documents (see the residual
    // exposure above); what changed is which live reads this class allows to reach it at all, and
    // only for those that don't survive the wait.
    //
    // No "using System.Windows.Forms" here, for the same reason WindowsResolutionRefresher.cs has
    // none: this is debounce POLICY, not UI, and has to stay reachable from
    // ResolutionChangeFixture with no message loop, no Form and no real elapsed time anywhere -
    // IResolutionAdoptionTimer below is the seam that makes that possible. VibranceGUI wires this
    // class up against FormsResolutionAdoptionTimer (a thin System.Windows.Forms.Timer wrapper) in
    // production; the fixture drives it against a fake that fires synchronously on command.
    internal sealed class ResolutionAdoptionDebouncer
    {
        // Chosen, not measured - this repository's constraints rule out timing a real game's mode
        // flap on this machine (see the header comment of this branch's tests: no real display may
        // change resolution). The binding consideration is NOT how long a driver's own mode-set
        // takes to settle - CDS_UPDATEREGISTRY is synchronous and that is well under a second
        // regardless, settled or not, by the time DisplaySettingsChanged even fires. It is outlasting
        // a game's OWN mode flap (this class's header: startup/exit flaps, alt-tab restore/re-set
        // pairs, launcher and anti-cheat mode sets) while staying well under the length of a
        // coincidence between a user's own genuine resolution change and a profiled game launch,
        // which is what bounds the accepted cost above (the DROPPED-change paragraph in this class's
        // header, and the preserveCapturedMode==true branch below). 2000ms is this branch's chosen
        // balance between those two ends, not a measurement of either one. A single named constant,
        // not a magic number scattered at call sites, specifically so a future measurement on real
        // hardware has exactly one place to correct.
        //
        // A sustained DisplaySettingsChanged storm - notifications arriving faster than
        // DebounceIntervalMs, indefinitely - starves this countdown completely: Restart keeps
        // resetting the clock, it never elapses, and _windowsResolutionSettings simply stops being
        // refreshed for as long as the storm continues. This fails SAFE, not silent-and-wrong: every
        // consumer of that dictionary (NvidiaDynamicVibranceProxy/AmdDynamicVibranceProxy) already
        // gates on ContainsKey before acting on an entry, so a starved refresh means "no resolution
        // switching attempted for this device right now", never a switch driven by a stale or wrong
        // captured mode.
        internal const int DebounceIntervalMs = 2000;

        private readonly IResolutionAdoptionTimer _timer;

        internal ResolutionAdoptionDebouncer(IResolutionAdoptionTimer timer)
        {
            _timer = timer;
        }

        // Called for every SystemEvents.DisplaySettingsChanged VibranceGUI receives, already
        // marshaled onto the UI thread exactly like RebuildWindowsResolutionSettings itself always
        // was. preserveCapturedMode mirrors RebuildWindowsResolutionSettings' own flag
        // (isResolutionChangeApplied) and must be read by the caller fresh, at THIS notification's
        // arrival - not cached from an earlier one - since it can flip in either direction between
        // one DisplaySettingsChanged and the next.
        //
        // performRefresh is never invoked more than once per call to this method, and is invoked
        // either synchronously, right here (preserveCapturedMode true), or later, from the timer's
        // own elapsed callback (preserveCapturedMode false) - never both. It is deliberately a bare
        // Action, not a captured "mode at this instant" value: whichever call actually runs it, it
        // must re-read whatever is live AT THAT TIME (RebuildWindowsResolutionSettings already
        // works this way - it has no notion of "the mode this call was scheduled for"), which is
        // exactly what lets a later, unrelated DisplaySettingsChanged supersede an earlier pending
        // one for free, just by being the one whose Restart call's callback is the one still
        // pending when the timer elapses.
        internal void OnDisplaySettingsChanged(bool preserveCapturedMode, Action performRefresh)
        {
            if (preserveCapturedMode)
            {
                // While a vibranceGUI apply is outstanding, WindowsResolutionRefresher.Refresh
                // never performs a live read for a device it already has an entry for at all -
                // Item1 is preserved unconditionally (see Refresh's own top-of-method comment, "the
                // single most dangerous line in the resolution-change fix"). There is therefore no
                // foreign-mode-adoption risk to debounce against in this state, and a real apply in
                // progress needs Item2 (the supported-mode list) and the detached-device
                // last-known-mode fallback kept current right now, not delayed behind a countdown
                // that exists to guard against a risk that cannot occur while this flag is true.
                //
                // Cancels rather than lets a stale countdown from an earlier, since-superseded
                // DisplaySettingsChanged (back when preserveCapturedMode was still false) run to
                // completion later - purely to avoid a redundant extra refresh call once that
                // countdown would otherwise have elapsed on top of the immediate one performed here.
                // Cancelling here is NOT what causes this class's header's "THE ACCEPTED COST IS
                // SHARPER THAN 'BRIEFLY LATE'" scenario, and NOT cancelling would not have prevented
                // it either: whether that earlier countdown is cancelled here or left to fire later,
                // by the time ANY refresh actually runs with preserveCapturedMode reading true - this
                // call included - RebuildWindowsResolutionSettings preserves the OLD Item1
                // unconditionally. The user's genuine change, if one was pending, is already dropped
                // the moment this branch is reached at all; this Cancel() call only decides whether a
                // second, equally-preserving refresh also runs pointlessly later.
                _timer.Cancel();
                performRefresh();
                return;
            }

            // Restart, not "start only if nothing is pending": a second DisplaySettingsChanged
            // arriving before the first countdown elapses - a game switching between two
            // resolutions of its own, or a user cycling through several desktop resolutions in a
            // row - resets the clock rather than stacking a second pending callback alongside the
            // first. That is what turns this into "wait for the mode to hold still", not "adopt
            // whichever mode happened to survive DebounceIntervalMs by coincidence, even if
            // something else changed again moments later".
            _timer.Restart(DebounceIntervalMs, performRefresh);
        }

        // Stops a pending countdown without running its callback - VibranceGUI's own CleanUp calls
        // this alongside its SystemEvents.DisplaySettingsChanged unsubscription, for the same
        // reason: a countdown already armed keeps ticking down on its own, independent of
        // SystemEvents, so unsubscribing the event alone does not stop one already in flight from
        // firing after the form is disposed.
        internal void Cancel()
        {
            _timer.Cancel();
        }
    }

    // The seam ResolutionAdoptionDebouncer drives - see that class's own header comment for why it
    // exists and why this interface carries no System.Windows.Forms dependency of its own.
    internal interface IResolutionAdoptionTimer
    {
        // (Re)arms a ONE-SHOT countdown of delayMs from now, discarding whatever countdown (and
        // callback) was already pending, if any - never stacks a second one alongside it. Fires
        // onElapsed at most once per call to Restart, and never again afterward unless Restart or
        // Cancel is called again first.
        void Restart(int delayMs, Action onElapsed);

        // Cancels a pending countdown, if any, without invoking its callback. A no-op when nothing
        // is pending.
        void Cancel();
    }
}
