using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace vibrance.GUI.common
{
    // The seam between the state/failure-handling logic below and the actual display driver -
    // mirrors IGammaDevice (DeviceGammaRampHelper.cs). That class's own comment describes the same
    // shape: a fake standing in for the device seam is what let GammaRestoreFixture drive the
    // session logic behind two real, already-fixed defects ("B1"/"S1") through full apply/restore
    // cycles, including forced failures, without a display. RealDisplayModeDevice (bottom of this
    // file) is the only production implementation; ResolutionChangeFixture supplies a fake so the
    // retry/bound/notification state a foreground-change storm can drive through it is exercised
    // via real apply/revert cycles, including forced failures at every step, without ever touching
    // a real display.
    internal interface IDisplayModeDevice
    {
        bool TryGetCurrentMode(string deviceName, out Devmode mode);
        bool TryEnumerateMode(string deviceName, int modeNum, out Devmode mode);

        // Devmode is a value type and is taken here BY VALUE, deliberately not "ref" - a fake
        // implementation has to be able to record exactly what it was handed (dmFields above all)
        // without the caller's own copy changing underneath it afterward. RealDisplayModeDevice
        // copies to a local before taking "ref" of THAT, for the P/Invoke, which needs "ref".
        DispChange ChangeMode(string deviceName, Devmode mode, ChangeDisplaySettingsFlags flags);
    }

    // Raised at most once per (device, target) while a failure streak is ongoing - specifically at
    // the moment ChangeResolutionEx gives up and starts returning Suppressed, not on every
    // individual failed attempt. A user does not need a balloon for a single transient
    // ChangeDisplaySettingsEx failure that resolves itself on the next foreground switch; they do
    // need one the moment vibranceGUI has stopped trying, especially on the revert side, where
    // giving up means the desktop is stuck at the game's resolution until they act. There is
    // deliberately no "IsGivingUp" flag here - a give-up is the ONLY reason this event is ever
    // raised (see RecordFailure below), so a field that would always read true carries no
    // information; if a future change makes this event fire on a non-give-up failure too, add the
    // flag back then, with a real false case to go with it.
    public class ResolutionFailureEventArgs : EventArgs
    {
        public string DeviceName { get; private set; }
        public ResolutionModeWrapper Target { get; private set; }
        public DispChange FailureCode { get; private set; }
        public bool IsRevert { get; private set; }

        internal ResolutionFailureEventArgs(string deviceName, ResolutionModeWrapper target, DispChange failureCode, bool isRevert)
        {
            DeviceName = deviceName;
            Target = target;
            FailureCode = failureCode;
            IsRevert = isRevert;
        }
    }

    class ResolutionHelper
    {
        // What a single ChangeResolutionEx call actually did - deliberately not a bool.
        // AlreadyMatching and Suppressed both mean "nothing was sent to the driver this time", for
        // two different reasons a caller needs to tell apart: AlreadyMatching means the mode is
        // already right, Suppressed means this (device, target, direction) has failed too many
        // times in a row and is being left alone until something (a success, or ResetForTests)
        // clears it. AppliedUnverified is its own case, not a flavour of Failed: it means
        // CDS_UPDATEREGISTRY itself reported success (or Notupdated) but the post-apply readback
        // did not confirm it - the mode most likely DID change, so a caller (the proxies) should
        // keep treating it as applied for the purpose of a later revert attempt, rather than
        // writing off a change that plausibly landed. Nested (not a sibling top-level type) so
        // callers write ResolutionHelper.ResolutionChangeResult.Applied, matching
        // ResolutionChangeResult's own callers throughout the proxies and the fixture.
        public enum ResolutionChangeResult
        {
            Applied,
            AppliedUnverified,
            AlreadyMatching,
            Failed,
            Suppressed
        }

        // What RestoreOnExit's single, uninterruptible attempt actually did - deliberately NOT
        // ResolutionChangeResult, and not just a bool either. AlreadyMatching and Suppressed both
        // mean "nothing was sent to the driver this time", for two different reasons an ONGOING
        // caller needs to tell apart (see ResolutionChangeResult above) - but RestoreOnExit has no
        // next foreground event to hand either of those distinctions off to, so a caller here would
        // have nothing left to act on even if it kept them. What a caller here CAN act on - is the
        // desktop believed restored, or not - is what this collapses down to instead. Nested for the
        // same reason ResolutionChangeResult is: callers write
        // ResolutionHelper.ExitRestoreResult.Restored, matching ResolutionChangeResult's own callers.
        public enum ExitRestoreResult
        {
            NothingToRestore,  // a guard declined - see RestoreOnExit's own guard order
            Restored,          // ChangeResolutionEx returned Applied or AlreadyMatching
            Unverified,        // ChangeResolutionEx returned AppliedUnverified
            Failed             // ChangeResolutionEx returned Failed (or Suppressed - see RestoreOnExit), or threw
        }

        private const int EnumCurrentSettings = -1;

        // How many consecutive failures ChangeResolutionEx tolerates for one (device, target,
        // direction) before it stops calling the driver at all and starts returning Suppressed.
        // Apply and revert use different bounds on purpose. Giving up on an apply only leaves the
        // user at their own Windows resolution - the side it is safe to fail toward. Giving up on
        // a revert leaves the desktop stuck at the GAME's resolution, with no other code path that
        // will ever try again, so it is worth trying far longer before accepting that outcome.
        private const int ApplyFailureBound = 3;
        private const int RevertFailureBound = 10;

        // Bits ChangeResolutionEx is willing to declare in dmFields on top of whatever
        // EnumDisplaySettings already reported for the current mode - see ApplyTargetFields, which
        // ORs this in rather than overwriting dmFields outright. Dropping bits EnumDisplaySettings
        // already set - DM_POSITION above all - would risk a multi-monitor desktop rearranging
        // itself the next time a mode change runs. Not a confirmed mechanism for any specific
        // upstream report (the pre-fix code never touched dmFields at all, so this was never a
        // live cause of anything filed) - just the correctness this class owes every DEVMODE it
        // hands to ChangeDisplaySettingsEx, going forward.
        private const DevmodeFields OwnedFields = DevmodeFields.DmPelsWidth | DevmodeFields.DmPelsHeight |
            DevmodeFields.DmBitsPerPel | DevmodeFields.DmDisplayFrequency | DevmodeFields.DmDisplayFixedOutput;

        [DllImport("user32.dll")]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref Devmode devMode);

        [DllImport("user32.dll")]
        private static extern DispChange ChangeDisplaySettingsEx(
            string lpszDeviceName,
            ref Devmode lpDevMode,
            IntPtr hwnd,
            ChangeDisplaySettingsFlags dwflags,
            IntPtr lParam);

        // The only production IDisplayModeDevice. Everything below is exercised against this by
        // the public, hardware-touching overloads; ResolutionChangeFixture drives the internal
        // overloads with its own fake instead.
        private static readonly IDisplayModeDevice _realDevice = new RealDisplayModeDevice();

        // The seam WindowsResolutionRefresher's public overload delegates through, mirroring how
        // ChangeResolutionEx/IsResolutionChangeNeeded already expose _realDevice to their own
        // internal overloads - so the refresher's fixture-facing overload is the only one that
        // ever needs to know a fake exists.
        internal static IDisplayModeDevice RealDevice { get { return _realDevice; } }

        public static event EventHandler<ResolutionFailureEventArgs> ResolutionChangeFailed;

        // Consecutive-failure counts, keyed by device + target + direction (BuildFailureKey below)
        // - direction is part of the key because apply and revert are bounded differently (see
        // ApplyFailureBound/RevertFailureBound) and a device can legitimately be mid-streak on one
        // direction while the other has never failed at all.
        private static readonly Dictionary<string, int> _consecutiveFailures = new Dictionary<string, int>();

        // Suppresses repeat log lines for a (device, DispChange) pair that keeps failing the same
        // way - without this, a foreground-change storm against a broken device would write a line
        // to vibranceGUI.log on every single event. Narrower than DeviceGammaRampHelper's
        // equivalent set on purpose: a device that starts failing with a DIFFERENT code is new
        // diagnostic information and still gets its own line, not just a repeat of the first one.
        private static readonly HashSet<string> _loggedFailures = new HashSet<string>();

        // One ResolutionChangeFailed raise per (device, target) while a give-up streak is ongoing -
        // see the class-level comment on ResolutionFailureEventArgs for why the raise itself is
        // deferred to the moment of giving up rather than fired on the first failure.
        private static readonly HashSet<string> _notifiedFailures = new HashSet<string>();

        // Test-only counter of distinct log lines this class has actually written - incremented from
        // two sources: every time the _loggedFailures dedup check below passes (the ongoing
        // apply/revert path), and unconditionally by every non-NothingToRestore RestoreOnExit outcome
        // (deliberately outside that dedup - see RestoreOnExit's own comment for why a repeat of an
        // already-logged failure code must still get its own line at exit). A real log write has no
        // return value or other observable signal, and ResolutionChangeFixture's design asks it to
        // assert exact log-line counts - this is the seam that makes that possible without reading the
        // real, shared vibranceGUI.log file. Reset by ResetForTests().
        internal static int LoggedLineCountForTests;

        /// <summary>
        /// Reads the current mode for lpszDeviceName. False (no exception) is EnumDisplaySettings's
        /// own way of reporting failure - see RealDisplayModeDevice.
        /// </summary>
        public static bool GetCurrentResolutionSettings(out Devmode mode, string lpszDeviceName)
        {
            return _realDevice.TryGetCurrentMode(lpszDeviceName, out mode);
        }

        public static List<ResolutionModeWrapper> EnumerateSupportedResolutionModes()
        {
            return EnumerateSupportedResolutionModes(null);
        }

        public static List<ResolutionModeWrapper> EnumerateSupportedResolutionModes(string deviceName)
        {
            return EnumerateSupportedResolutionModes(_realDevice, deviceName);
        }

        // The seam ResolutionChangeFixture/WindowsResolutionRefresher drive directly - see
        // EnumerateSupportedResolutionModes's public overload above for why.
        internal static List<ResolutionModeWrapper> EnumerateSupportedResolutionModes(IDisplayModeDevice device, string deviceName)
        {
            List<ResolutionModeWrapper> resolutionList = new List<ResolutionModeWrapper>();
            Devmode mode;
            int index = 0;
            while (device.TryEnumerateMode(deviceName, index++, out mode))
            {
                resolutionList.Add(new ResolutionModeWrapper(mode));
            }
            return resolutionList;
        }

        /// <summary>
        /// True when deviceName's current mode does not yet match target on the four fields
        /// ChangeResolutionEx actually controls and verifies - see
        /// ResolutionModeWrapper.MatchesAchievedMode for why DmDisplayFixedOutput is deliberately
        /// excluded from that comparison, and why that is what stops a "(Center)" mode selection
        /// from re-firing a real mode change on every single foreground event forever.
        /// </summary>
        public static bool IsResolutionChangeNeeded(string deviceName, ResolutionModeWrapper target)
        {
            return IsResolutionChangeNeeded(_realDevice, deviceName, target);
        }

        internal static bool IsResolutionChangeNeeded(IDisplayModeDevice device, string deviceName, ResolutionModeWrapper target)
        {
            if (target == null)
            {
                return false;
            }
            Devmode currentMode;
            if (!device.TryGetCurrentMode(deviceName, out currentMode))
            {
                // Nothing to compare against - matches the pre-fix callers, which treated an
                // unreadable current mode as "no change needed" rather than forcing one blind.
                return false;
            }
            return !target.MatchesAchievedMode(currentMode);
        }

        /// <summary>
        /// Clears every recorded failure, log-suppression and notification-suppression entry, and
        /// the log-line counter - for test isolation only. Deliberately does not touch
        /// ResolutionChangeFailed's subscriber list: production code (VibranceGUI) subscribes once
        /// for the life of the form, and ResolutionChangeFixture's own checks install and remove
        /// their own handler around each check instead of relying on this to detach one for them.
        /// </summary>
        internal static void ResetForTests()
        {
            _consecutiveFailures.Clear();
            _loggedFailures.Clear();
            _notifiedFailures.Clear();
            LoggedLineCountForTests = 0;
        }

        /// <summary>
        /// Drives a real display mode change (or revert) against deviceName toward target. Never
        /// shows a MessageBox and never blocks - see the internal overload below for the exact
        /// sequence and for why CDS_TEST-then-CDS_UPDATEREGISTRY replaced the old
        /// CDS_UPDATEREGISTRY|CDS_NORESET staged-commit pattern.
        /// </summary>
        public static ResolutionChangeResult ChangeResolutionEx(ResolutionModeWrapper target, string deviceName, bool isRevert)
        {
            return ChangeResolutionEx(_realDevice, target, deviceName, isRevert);
        }

        // The seam ResolutionChangeFixture drives directly - see ChangeResolutionEx's public
        // overload above for why. Delegates to the 5-arg overload below with honourGiveUp: true -
        // every call site through this overload (both proxies' apply/revert calls, every fixture
        // call above this line) needs the give-up suppression honoured normally; RestoreOnExit below
        // is the one caller that goes straight to the 5-arg overload instead, with false.
        internal static ResolutionChangeResult ChangeResolutionEx(IDisplayModeDevice device, ResolutionModeWrapper target, string deviceName, bool isRevert)
        {
            return ChangeResolutionEx(device, target, deviceName, isRevert, true);
        }

        // CDS_TEST (validate only) is tried first and CDS_UPDATEREGISTRY (apply AND persist) is
        // tried only once that passes - never CDS_UPDATEREGISTRY|CDS_NORESET followed by a second,
        // separate commit call. That old two-call pattern left a reachable state where the first
        // call could fail (or write a mode that never actually gets confirmed) while the second,
        // unconditional commit call ran anyway with its own return value discarded - there is no
        // window here where the registry can hold a mode that was never actually applied, because
        // CDS_UPDATEREGISTRY both applies AND persists in one authoritative call. The trade-off:
        // CDS_UPDATEREGISTRY gets no 15-second revert-if-unconfirmed safety net the way an
        // interactive Windows Settings resolution change does, which is exactly why CDS_TEST runs
        // first - a mode the driver would reject is caught before anything is written at all.
        //
        // honourGiveUp is false for exactly one caller, RestoreOnExit below, which needs to bypass an
        // already-suppressed key for one last attempt at shutdown - see RestoreOnExit's own comment
        // for the full justification, and the suppression guard just below for why that bypass is
        // safe. Private, not internal, because unlike every other overload in this class it is not a
        // seam ResolutionChangeFixture needs to drive on its own - the fixture reaches it, when it
        // needs to, through RestoreOnExit's own internal overload instead.
        private static ResolutionChangeResult ChangeResolutionEx(IDisplayModeDevice device, ResolutionModeWrapper target, string deviceName, bool isRevert, bool honourGiveUp)
        {
            if (target == null)
            {
                // Defensive only - every real call site already gates on IsResolutionChangeNeeded,
                // which itself returns false for a null target, so this is never reached through
                // NvidiaDynamicVibranceProxy/AmdDynamicVibranceProxy.
                return ResolutionChangeResult.Failed;
            }

            int bound = isRevert ? RevertFailureBound : ApplyFailureBound;

            // Nothing recorded anywhere means nothing to suppress - the steady state on a healthy
            // machine, since ClearFailureState removes only the succeeding device's own keys (never
            // the whole dictionary - see below), and a device that has never failed never adds any
            // to begin with. Checked before building a key at all: BuildFailureKey formats five
            // uints and concatenates three strings, and the hottest path through this method
            // (AlreadyMatching, on a healthy machine with nothing recorded at all) had to pay for it
            // before this guard existed. A device already mid-streak still pays that cost on every
            // attempt, suppressed or not - this only short-circuits when the dictionary is empty.
            //
            // honourGiveUp short-circuits this whole guard when false, so a suppressed key still
            // reaches the driver for one attempt - see RestoreOnExit's own comment for why that is
            // safe specifically at exit, when this whole guard exists to stop a foreground-change
            // storm from re-running a doomed mode set forever.
            if (honourGiveUp && _consecutiveFailures.Count > 0)
            {
                int priorFailures;
                if (_consecutiveFailures.TryGetValue(BuildFailureKey(deviceName, target, isRevert), out priorFailures) &&
                    priorFailures >= bound)
                {
                    // Already given up on this exact (device, target, direction) - do not touch the
                    // driver again until something clears it. In practice that means a success on a
                    // DIFFERENT target/direction for the same device (ClearFailureState clears the
                    // whole device, see below) - once THIS key is suppressed, the driver is never
                    // called for it again through the normal (honourGiveUp: true) path above, so it
                    // can no longer produce a success of its own. In production, short of
                    // ResetForTests() (test-only), the only other way out is RestoreOnExit's
                    // honourGiveUp: false bypass below, which gets exactly one more attempt at
                    // vibranceGUI's own shutdown. This is what keeps a persistently failing revert
                    // from re-running the same doomed mode set on every single foreground event
                    // forever, for as long as vibranceGUI keeps running.
                    return ResolutionChangeResult.Suppressed;
                }
            }

            // Step 1.
            Devmode currentMode;
            if (!device.TryGetCurrentMode(deviceName, out currentMode))
            {
                // Deliberately does not touch _consecutiveFailures/_notifiedFailures - an unreadable
                // current mode is a different failure category from a rejected mode change, and is
                // not counted toward the give-up bound. Still deduped so a persistently unreadable
                // device logs once, not on every foreground event.
                if (_loggedFailures.Add(DeviceKey(deviceName, "read-current-mode")))
                {
                    LoggedLineCountForTests++;
                    Program.LogSafely(string.Format("Failed to read the current resolution for screen {0}, refusing to change it", deviceName));
                }
                return ResolutionChangeResult.Failed;
            }

            // Step 2.
            if (target.MatchesAchievedMode(currentMode))
            {
                ClearFailureState(deviceName);
                return ResolutionChangeResult.AlreadyMatching;
            }

            // Step 3. Never mutates currentMode - it is still needed below (both as "the device's
            // own value" for the fixed-output fallback, and to build the failure key it already
            // contributed to above).
            Devmode desiredMode = currentMode;
            ApplyTargetFields(ref desiredMode, target);

            // Step 4.
            DispChange testResult = device.ChangeMode(deviceName, desiredMode, ChangeDisplaySettingsFlags.CdsTest);
            if (testResult != DispChange.DispChangeSuccessful &&
                (desiredMode.dmFields & (uint)DevmodeFields.DmDisplayFixedOutput) != 0 &&
                target.DmDisplayFixedOutput != currentMode.dmDisplayFixedOutput)
            {
                // A driver that cannot honour the requested scaling/centering behaviour may reject
                // CDS_TEST outright rather than silently ignoring the field - retried exactly once,
                // with the bit dropped and the device's OWN current value restored, so a scaling
                // preference the driver cannot honour does not also block the four fields it can.
                // OwnedFields declares DM_DISPLAYFIXEDOUTPUT unconditionally (see its own comment),
                // so without the value-differs check above this retry would fire on every single
                // rejection, including the common case where the requested value already matches
                // the device's own - dropping the bit there changes nothing observable, so it is
                // never worth a second driver call.
                desiredMode.dmFields &= ~(uint)DevmodeFields.DmDisplayFixedOutput;
                desiredMode.dmDisplayFixedOutput = currentMode.dmDisplayFixedOutput;
                testResult = device.ChangeMode(deviceName, desiredMode, ChangeDisplaySettingsFlags.CdsTest);
            }

            if (testResult != DispChange.DispChangeSuccessful)
            {
                return RecordFailure(deviceName, target, isRevert, bound, testResult);
            }

            // Step 5.
            DispChange applyResult = device.ChangeMode(deviceName, desiredMode, ChangeDisplaySettingsFlags.CdsUpdateregistry);
            if (applyResult == DispChange.DispChangeNotupdated)
            {
                // Nothing was written to the registry, but the mode is live regardless - not a
                // failure, just worth a diagnostic line the first time it happens for this device.
                if (_loggedFailures.Add(DeviceKey(deviceName, applyResult.ToString())))
                {
                    LoggedLineCountForTests++;
                    Program.LogSafely(string.Format(
                        "Resolution change for screen {0} reported no update was written to the registry; treating the mode as live and verifying it", deviceName));
                }
            }
            else if (applyResult != DispChange.DispChangeSuccessful)
            {
                return RecordFailure(deviceName, target, isRevert, bound, applyResult);
            }

            // Step 6. CDS_TEST passing is not proof the mode actually took - confirmed by reading
            // it back, the same way a caller would have to if this class offered no confirmation
            // of its own.
            Devmode achievedMode;
            bool readAchievedMode = device.TryGetCurrentMode(deviceName, out achievedMode);
            if (!readAchievedMode || !target.MatchesAchievedMode(achievedMode))
            {
                // CDS_UPDATEREGISTRY itself reported success (or Notupdated) to get here - a
                // genuine driver rejection already returned Failed above, at step 4 or step 5, and
                // never reaches this point. So this is NOT the same failure category: the mode most
                // likely did change, and a caller that treated it as a plain Failed (clearing
                // isResolutionChangeApplied) would strand the desktop while telling the user the
                // opposite of what happened. AppliedUnverified keeps the same failure-count/log/
                // notify accounting - a driver that never confirms is still worth eventually giving
                // up on - but returns a result the proxies keep treating as applied. See
                // RecordUnverifiedApply for the log line, which reports the achieved-vs-target
                // values instead of a synthetic DispChange code that would misrepresent this as a
                // driver rejection.
                return RecordUnverifiedApply(deviceName, target, isRevert, bound, readAchievedMode, achievedMode);
            }

            ClearFailureState(deviceName);
            return ResolutionChangeResult.Applied;
        }

        // The last-chance resolution restore at vibranceGUI's own shutdown (upstream #98). Called
        // from each proxy's HandleDvcExit (via RestoreResolutionOnExit, passing RealDevice), never
        // from OnWinEventHook. Deliberately has no separate public, hardware-touching overload the
        // way ChangeResolutionEx/IsResolutionChangeNeeded do - those two are called directly by both
        // proxies with no explicit device (OnWinEventHook's apply/revert branches), so their public
        // overloads have real production callers to be symmetric with. RestoreOnExit has exactly one
        // production call shape - a proxy handing in RealDevice through RestoreResolutionOnExit -
        // so a second, device-less overload here would be dead surface no caller has ever needed;
        // ResolutionChangeFixture drives this internal overload directly instead, the same pattern
        // used wherever a class in this file has no genuine device-less production caller.
        //
        // Guard order matters, and is deliberately cheapest-first: each guard below returns
        // NothingToRestore before the next one is even evaluated, so a normal exit (the common case -
        // most exits happen with no game in the foreground at all) never pays for a dictionary lookup
        // once guard 1 alone has already ruled a restore out.
        //   1. the user opted out, or vibranceGUI's own apply never actually landed - nothing to undo
        //   2. gameDeviceName is null/empty - vibranceGUI believes it changed something but has lost
        //      track of which screen; logged once, since unlike every other guard here this IS a real
        //      "we owe the user a restore and cannot deliver it" case, not an ordinary no-op
        //   3/4. no saved Windows mode recorded for that device at all (a stale/never-populated
        //      dictionary, or an entry with no Item1 - see WindowsResolutionRefresher.Refresh for how
        //      Item1 is normally populated)
        //   5. the saved mode already matches what is live - nothing left to send to the driver
        //
        // The whole body, not just the ChangeResolutionEx call below, is wrapped in one try/catch:
        // IsResolutionChangeNeeded also P/Invokes EnumDisplaySettings (through device.TryGetCurrentMode),
        // so a driver that throws on a READ, not just on a write, must not escape into FormClosing.
        // Program.LogSafely already swallows whatever it is handed, matching every other catch site in
        // this class.
        //
        // Exactly one ChangeResolutionEx call, honourGiveUp: false - no loop, no retry, no sleep, no
        // timer. Bypassing the give-up suppression is deliberate and safe specifically here: this IS
        // the "no other code path will ever try again" case the revert bound's own comment
        // (ApplyFailureBound/RevertFailureBound above) warns about, so declining to even try because
        // an earlier, unrelated foreground event gave up would strand the desktop for good with a
        // guard that was only ever meant to stop an infinite RETRY, not to rule out a single final
        // attempt at shutdown. The hazard that guard protects against - re-running a doomed mode set
        // on every foreground event forever - is structurally absent here: there is no next
        // foreground event once vibranceGUI itself is exiting. And the common cause of a failing
        // revert, a fullscreen-exclusive game still holding the mode, has usually already let go of it
        // by the time exit runs - same driver, different world state.
        //
        // Logs every outcome except NothingToRestore under its own line (format "Restoring the
        // resolution for screen {0} at exit: {1}"), deliberately NOT through
        // RecordFailure/RecordUnverifiedApply's own _loggedFailures dedup - a Failed or
        // AppliedUnverified result here can easily repeat a code _loggedFailures already logged once
        // during the game session (and so would otherwise stay silent), but this is the one attempt
        // whose outcome the user most needs to see, not one dedup should be allowed to swallow.
        internal static ExitRestoreResult RestoreOnExit(IDisplayModeDevice device,
            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> windowsResolutionSettings,
            string gameDeviceName, bool neverChangeResolution, bool isResolutionChangeApplied)
        {
            try
            {
                if (neverChangeResolution || !isResolutionChangeApplied)
                {
                    return ExitRestoreResult.NothingToRestore;
                }

                if (string.IsNullOrEmpty(gameDeviceName))
                {
                    Program.LogSafely("Restoring the resolution at exit: vibranceGUI applied a resolution change but lost track of which screen to restore it on");
                    LoggedLineCountForTests++;
                    return ExitRestoreResult.NothingToRestore;
                }

                if (windowsResolutionSettings == null)
                {
                    return ExitRestoreResult.NothingToRestore;
                }

                Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>> saved;
                if (!windowsResolutionSettings.TryGetValue(gameDeviceName, out saved) || saved.Item1 == null)
                {
                    return ExitRestoreResult.NothingToRestore;
                }

                if (!IsResolutionChangeNeeded(device, gameDeviceName, saved.Item1))
                {
                    return ExitRestoreResult.NothingToRestore;
                }

                ResolutionChangeResult result = ChangeResolutionEx(device, saved.Item1, gameDeviceName, true, false);
                ExitRestoreResult exitResult;
                switch (result)
                {
                    case ResolutionChangeResult.Applied:
                    case ResolutionChangeResult.AlreadyMatching:
                        exitResult = ExitRestoreResult.Restored;
                        break;
                    case ResolutionChangeResult.AppliedUnverified:
                        exitResult = ExitRestoreResult.Unverified;
                        break;
                    case ResolutionChangeResult.Failed:
                        exitResult = ExitRestoreResult.Failed;
                        break;
                    default:
                        // Suppressed - unreachable with honourGiveUp false (the guard that would ever
                        // return it is skipped entirely above), but handled explicitly rather than
                        // left to an implicit fall-through in case that ever changes.
                        exitResult = ExitRestoreResult.Failed;
                        break;
                }

                Program.LogSafely(string.Format("Restoring the resolution for screen {0} at exit: {1}", gameDeviceName, exitResult));
                LoggedLineCountForTests++;
                return exitResult;
            }
            catch (Exception ex)
            {
                Program.LogSafely(string.Format("Restoring the resolution for screen {0} at exit: {1}", gameDeviceName, ex));
                LoggedLineCountForTests++;
                return ExitRestoreResult.Failed;
            }
        }

        // OR's target's four controllable fields' bits into whatever dmFields EnumDisplaySettings
        // already returned for the current mode - never overwrites dmFields outright. See
        // OwnedFields for why DM_POSITION (and anything else already set) must survive untouched.
        private static void ApplyTargetFields(ref Devmode mode, ResolutionModeWrapper target)
        {
            mode.dmPelsWidth = target.DmPelsWidth;
            mode.dmPelsHeight = target.DmPelsHeight;
            mode.dmBitsPerPel = target.DmBitsPerPel;
            mode.dmDisplayFrequency = target.DmDisplayFrequency;
            mode.dmDisplayFixedOutput = target.DmDisplayFixedOutput;
            mode.dmFields |= (uint)OwnedFields;
        }

        // Step 7 from the design: increments the consecutive-failure count for this exact
        // (device, target, direction), logs at most once per (device, dedup key), and raises
        // ResolutionChangeFailed at most once per (device, target) - specifically at the attempt
        // where the count first reaches the bound. Below the bound, the failure is logged (if new)
        // but nothing is raised - a single transient rejection is not worth a balloon tip; only
        // genuinely giving up is (which is also why ResolutionFailureEventArgs carries no
        // "IsGivingUp" flag - a give-up is the only reason this is ever raised).
        //
        // Shared by RecordFailure (a genuine driver rejection, step 4/5) and RecordUnverifiedApply
        // (step 6 - CDS_UPDATEREGISTRY itself reported success, but the readback did not confirm
        // it), which differ only in the dedup key, the log message and the event's FailureCode -
        // and, at the call sites, in the ResolutionChangeResult returned around this accounting.
        private static void RecordFailureAccounting(string deviceName, ResolutionModeWrapper target, bool isRevert, int bound,
            string logDedupSuffix, string logMessage, DispChange eventFailureCode)
        {
            string failureKey = BuildFailureKey(deviceName, target, isRevert);
            int count;
            _consecutiveFailures.TryGetValue(failureKey, out count);
            count++;
            _consecutiveFailures[failureKey] = count;

            if (_loggedFailures.Add(DeviceKey(deviceName, logDedupSuffix)))
            {
                LoggedLineCountForTests++;
                Program.LogSafely(logMessage);
            }

            bool isGivingUp = count >= bound;
            if (isGivingUp && _notifiedFailures.Add(DeviceKey(deviceName, DescribeTarget(target))))
            {
                EventHandler<ResolutionFailureEventArgs> handler = ResolutionChangeFailed;
                if (handler != null)
                {
                    handler(null, new ResolutionFailureEventArgs(deviceName, target, eventFailureCode, isRevert));
                }
            }
        }

        // A genuine driver rejection at step 4 (CDS_TEST) or step 5 (CDS_UPDATEREGISTRY) - code is
        // the real DispChange the driver returned, used both as the log's dedup key and in the
        // message, so a device that starts failing with a DIFFERENT code still gets its own line.
        private static ResolutionChangeResult RecordFailure(string deviceName, ResolutionModeWrapper target, bool isRevert, int bound, DispChange code)
        {
            RecordFailureAccounting(deviceName, target, isRevert, bound,
                code.ToString(),
                string.Format("{0} the resolution for screen {1} failed: {2}", isRevert ? "Restoring" : "Changing", deviceName, code),
                code);
            return ResolutionChangeResult.Failed;
        }

        // Step 6's readback did not confirm a CDS_UPDATEREGISTRY call that itself reported success
        // (or Notupdated) - shares RecordFailure's counting/notification machinery (a driver that
        // never confirms is still worth eventually giving up on), but under its own dedup key, so
        // it never collides with a genuine DispChangeFailed logged elsewhere for the same device,
        // and with a log line that reports the achieved-vs-target values instead of a synthetic
        // DispChange code that would misrepresent this as a driver rejection. readAchievedMode
        // distinguishes "read the wrong mode back" from "couldn't even read it this time" in that
        // message; achievedMode is only meaningful when readAchievedMode is true.
        private static ResolutionChangeResult RecordUnverifiedApply(string deviceName, ResolutionModeWrapper target, bool isRevert, int bound,
            bool readAchievedMode, Devmode achievedMode)
        {
            string message = readAchievedMode
                ? string.Format(
                    "{0} the resolution for screen {1} reported success but a readback did not confirm it: achieved {2}x{3}x{4}bpp@{5}Hz, target {6}x{7}x{8}bpp@{9}Hz",
                    isRevert ? "Restoring" : "Changing", deviceName,
                    achievedMode.dmPelsWidth, achievedMode.dmPelsHeight, achievedMode.dmBitsPerPel, achievedMode.dmDisplayFrequency,
                    target.DmPelsWidth, target.DmPelsHeight, target.DmBitsPerPel, target.DmDisplayFrequency)
                : string.Format(
                    "{0} the resolution for screen {1} reported success but the mode could not be read back afterward to confirm it",
                    isRevert ? "Restoring" : "Changing", deviceName);

            RecordFailureAccounting(deviceName, target, isRevert, bound, "readback-mismatch", message, DispChange.DispChangeFailed);
            return ResolutionChangeResult.AppliedUnverified;
        }

        // Drops every recorded failure/log/notification entry for deviceName - across every
        // target and direction, not just the one that just succeeded. Once a device is confirmed
        // working, its entire prior failure history stops being useful information; keeping it
        // around would only make a later, unrelated failure on the same device look like a
        // continuation of an old streak it has nothing to do with.
        private static void ClearFailureState(string deviceName)
        {
            string prefix = deviceName + KeySeparator;

            List<string> staleFailureKeys = new List<string>();
            foreach (string key in _consecutiveFailures.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    staleFailureKeys.Add(key);
                }
            }
            foreach (string key in staleFailureKeys)
            {
                _consecutiveFailures.Remove(key);
            }

            _loggedFailures.RemoveWhere(delegate(string key) { return key.StartsWith(prefix, StringComparison.Ordinal); });
            _notifiedFailures.RemoveWhere(delegate(string key) { return key.StartsWith(prefix, StringComparison.Ordinal); });
        }

        // A control character (U+0001) - cannot appear in a device name or in the numeric/boolean
        // descriptions built below, so prefix matching in ClearFailureState can never straddle two
        // different device names (e.g. "DISPLAY1" is never a prefix-match for a key that actually
        // belongs to "DISPLAY10").
        private const string KeySeparator = "\u0001";

        private static string DeviceKey(string deviceName, string suffix)
        {
            return deviceName + KeySeparator + suffix;
        }

        private static string BuildFailureKey(string deviceName, ResolutionModeWrapper target, bool isRevert)
        {
            return DeviceKey(deviceName, DescribeTarget(target) + KeySeparator + (isRevert ? "revert" : "apply"));
        }

        private static string DescribeTarget(ResolutionModeWrapper target)
        {
            return string.Format("{0}x{1}x{2}@{3}#{4}",
                target.DmPelsWidth, target.DmPelsHeight, target.DmBitsPerPel,
                target.DmDisplayFrequency, target.DmDisplayFixedOutput);
        }

        // The production IDisplayModeDevice: EnumDisplaySettings/ChangeDisplaySettingsEx against a
        // real display, exactly as the old ChangeResolutionEx called them directly before this
        // seam existed.
        private class RealDisplayModeDevice : IDisplayModeDevice
        {
            public bool TryGetCurrentMode(string deviceName, out Devmode mode)
            {
                return TryEnumerateMode(deviceName, EnumCurrentSettings, out mode);
            }

            public bool TryEnumerateMode(string deviceName, int modeNum, out Devmode mode)
            {
                mode = new Devmode();
                mode.dmSize = (ushort)Marshal.SizeOf(mode);
                return EnumDisplaySettings(deviceName, modeNum, ref mode);
            }

            public DispChange ChangeMode(string deviceName, Devmode mode, ChangeDisplaySettingsFlags flags)
            {
                // The interface takes Devmode by value (see IDisplayModeDevice) - copied to a local
                // here purely because the P/Invoke itself needs "ref".
                Devmode localMode = mode;
                return ChangeDisplaySettingsEx(deviceName, ref localMode, IntPtr.Zero, flags, IntPtr.Zero);
            }
        }
    }

    public enum DispChange : int
    {
        DispChangeSuccessful = 0,
        DispChangeRestart = 1,
        DispChangeFailed = -1,
        DispChangeBadmode = -2,
        DispChangeNotupdated = -3,
        DispChangeBadflags = -4,
        DispChangeBadparam = -5
    };

    public enum Dmdfo : int
    {
        Default = 0,
        Stretch = 1,
        Center = 2
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct Devmode
    {
        // You can define the following constant
        // but OUTSIDE the structure because you know
        // that size and layout of the structure
        // is very important
        // CCHDEVICENAME = 32 = 0x50
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        // In addition you can define the last character array
        // as following:
        //[MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        //public Char[] dmDeviceName;

        // After the 32-bytes array
        [MarshalAs(UnmanagedType.U2)]
        public UInt16 dmSpecVersion;

        [MarshalAs(UnmanagedType.U2)]
        public UInt16 dmDriverVersion;

        [MarshalAs(UnmanagedType.U2)]
        public UInt16 dmSize;

        [MarshalAs(UnmanagedType.U2)]
        public UInt16 dmDriverExtra;

        [MarshalAs(UnmanagedType.U4)]
        public UInt32 dmFields;

        public Pointl dmPosition;

        [MarshalAs(UnmanagedType.U4)]
        public UInt32 dmDisplayOrientation;

        [MarshalAs(UnmanagedType.U4)]
        public UInt32 dmDisplayFixedOutput;

        [MarshalAs(UnmanagedType.I2)]
        public Int16 dmColor;

        [MarshalAs(UnmanagedType.I2)]
        public Int16 dmDuplex;

        [MarshalAs(UnmanagedType.I2)]
        public Int16 dmYResolution;

        [MarshalAs(UnmanagedType.I2)]
        public Int16 dmTTOption;

        [MarshalAs(UnmanagedType.I2)]
        public Int16 dmCollate;

        // CCHDEVICENAME = 32 = 0x50
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        // Also can be defined as
        //[MarshalAs(UnmanagedType.ByValArray,
        //    SizeConst = 32, ArraySubType = UnmanagedType.U1)]
        //public Byte[] dmFormName;

        [MarshalAs(UnmanagedType.U2)]
        public UInt16 dmLogPixels;

        [MarshalAs(UnmanagedType.U4)]
        public UInt32 dmBitsPerPel;

        [MarshalAs(UnmanagedType.U4)]
        public UInt32 dmPelsWidth;

        [MarshalAs(UnmanagedType.U4)]
        public UInt32 dmPelsHeight;

        [MarshalAs(UnmanagedType.U4)]
        public UInt32 dmDisplayFlags;

        [MarshalAs(UnmanagedType.U4)]
        public UInt32 dmDisplayFrequency;

        [MarshalAs(UnmanagedType.U4)]
        public UInt32 dmICMMethod;

        [MarshalAs(UnmanagedType.U4)]
        public UInt32 dmICMIntent;

        [MarshalAs(UnmanagedType.U4)]
        public UInt32 dmMediaType;

        [MarshalAs(UnmanagedType.U4)]
        public UInt32 dmDitherType;

        [MarshalAs(UnmanagedType.U4)]
        public UInt32 dmReserved1;

        [MarshalAs(UnmanagedType.U4)]
        public UInt32 dmReserved2;

        [MarshalAs(UnmanagedType.U4)]
        public UInt32 dmPanningWidth;

        [MarshalAs(UnmanagedType.U4)]
        public UInt32 dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Pointl
    {
        [MarshalAs(UnmanagedType.I4)]
        public int x;
        [MarshalAs(UnmanagedType.I4)]
        public int y;
    }

    [Flags()]
    public enum ChangeDisplaySettingsFlags : uint
    {
        CdsNone = 0,
        CdsUpdateregistry = 0x00000001,
        CdsTest = 0x00000002,
        CdsFullscreen = 0x00000004,
        CdsGlobal = 0x00000008,
        CdsSetPrimary = 0x00000010,
        CdsVideoparameters = 0x00000020,
        CdsEnableUnsafeModes = 0x00000100,
        CdsDisableUnsafeModes = 0x00000200,
        CdsReset = 0x40000000,
        CdsResetEx = 0x20000000,
        CdsNoreset = 0x10000000
    }

    // The DEVMODE dmFields bits ChangeResolutionEx cares about - a small, named subset of the
    // full DM_* constant set (winuser.h), added alongside ChangeDisplaySettingsFlags rather than
    // reusing raw hex so ApplyTargetFields' mask reads as what it is. Values are the real Win32
    // DM_* constants, unchanged.
    [Flags]
    public enum DevmodeFields : uint
    {
        DmPosition = 0x20,
        DmDisplayOrientation = 0x80,
        DmBitsPerPel = 0x40000,
        DmPelsWidth = 0x80000,
        DmPelsHeight = 0x100000,
        DmDisplayFlags = 0x200000,
        DmDisplayFrequency = 0x400000,
        DmDisplayFixedOutput = 0x20000000
    }
}
