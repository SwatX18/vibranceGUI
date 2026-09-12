using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Windows.Forms;
using vibrance.GUI.AMD;
using vibrance.GUI.AMD.vendor;
using vibrance.GUI.NVIDIA;

namespace vibrance.GUI.common
{
    /// <summary>
    /// Regression coverage for the resolution-change fix (upstream #114/#132, both
    /// "Changing the resolution failed: DispChangeBadFlags"): a modal MessageBox raised from
    /// inside the foreground-change callback (ResolutionHelper.cs no longer has a
    /// "using System.Windows.Forms" or any MessageBox call site), a "success" read from the wrong
    /// ChangeDisplaySettingsEx call, dmFields never
    /// declaring the fields actually being changed, and a DmDisplayFixedOutput-inclusive equality
    /// guard that could re-fire a real mode set and registry write on every single foreground
    /// event forever. Everything here runs through IDisplayModeDevice against
    /// FakeDisplayModeDevice below - no GUI, no live display, ever. Run by vibrance.GUI.exe
    /// --selftest-resolution.
    /// </summary>
    public static class ResolutionChangeFixture
    {
        public static List<string> Run()
        {
            Checklist checklist = new Checklist();
            checklist.Lines.Add("vibranceGUI resolution change self test");
            checklist.Lines.Add(string.Empty);

            CheckNoDeferredApply(checklist);
            CheckDmFieldsDeclared(checklist);
            CheckTestGatesRegistryWrite(checklist);
            CheckAuthoritativeResult(checklist);
            CheckPostApplyVerification(checklist);
            CheckFixedOutputFallback(checklist);
            CheckNoRepeatNotification(checklist);
            CheckApplyBound(checklist);
            CheckRevertBound(checklist);
            CheckFullRevertCycle(checklist);
            CheckFailedRevertDoesNotStrandSilently(checklist);
            CheckUnachievableFixedOutputLoopIsBounded(checklist);
            CheckAlreadyMatchingShortCircuits(checklist);
            CheckClearFailureStateDoesNotStraddleDeviceNames(checklist);
            CheckUnreadableCurrentModeNeverCountsTowardGiveUp(checklist);

            // ResolutionHelper.RestoreOnExit coverage (upstream #98) - X1-X11 and X16-X17 drive
            // RestoreOnExit's internal, IDisplayModeDevice-seamed overload directly, exactly like
            // every check above; X12-X15 drive it one layer up, through each vendor proxy's
            // RestoreResolutionOnExit, still against a fake display, never a real one.
            CheckExitRestoreNoOpWhenFlagFalse(checklist);
            CheckExitRestoreNoOpWhenNeverChangeResolution(checklist);
            CheckExitRestoreNoOpWhenDeviceNameMissing(checklist);
            CheckExitRestoreNoOpWhenDeviceAbsentFromDictionary(checklist);
            CheckExitRestoreNoOpWhenDictionaryIsNull(checklist);
            CheckExitRestoreNoOpWhenSavedModeIsNull(checklist);
            CheckExitRestoreAppliesSavedMode(checklist);
            CheckExitRestoreNoOpWhenAlreadyAtSavedMode(checklist);
            CheckExitRestoreBypassesGiveUpSuppression(checklist);
            CheckExitRestoreFailureDoesNotNotifyButDoesLog(checklist);
            CheckExitRestoreUnverifiedApply(checklist);
            CheckExitRestoreNeverRetries(checklist);
            CheckExitRestoreSurvivesADeviceThatThrows(checklist);
            CheckExitRestoreSurvivesAReadThatThrows(checklist);
            CheckNvidiaExitRestoreAppliesSavedModeAndClearsFlag(checklist);
            CheckNvidiaExitRestoreFailureLeavesFlagSet(checklist);
            CheckAmdExitRestoreAppliesSavedModeAndClearsFlag(checklist);
            CheckAmdExitRestoreFailureLeavesFlagSet(checklist);
            CheckExitRestoreNeverTouchesOtherDevices(checklist);

            // WindowsResolutionRefresher.Refresh coverage - R1-R6/R-A below drive the extracted
            // refresh logic directly through IDisplayModeDevice, exactly like every check above;
            // R7/R8 are the ones that specifically exercise the last-known-mode retention fallback
            // for a device that drops out of a refresh and reattaches later.
            CheckRefreshPreservesCapturedModeWhileApplied(checklist);
            CheckRefreshWithNoChangeAppliedRecapturesBoth(checklist);
            CheckRefreshCapturesNewDeviceRegardlessOfFlag(checklist);
            CheckRefreshMutatesTheCallersDictionaryInstance(checklist);
            CheckSupportedResolutionListStaysTheInstanceTheFormCaptured(checklist);
            CheckRefreshNeverReportsUnreadableDeviceOutsideTheInitialBuild(checklist);
            CheckAdoptedForeignModeSelfHealsOnceItIsGone(checklist);
            CheckDetachedDeviceKeepsItsCapturedModeAcrossAReattach(checklist);
            CheckReattachedDeviceReenumeratesItsSupportedModes(checklist);
            CheckStickyEmptySupportedListHealsOnNextSuccessfulEnumeration(checklist);

            // ResolutionAdoptionDebouncer coverage - the duration-based fix that sits one layer
            // above Refresh (see R-A's own comment, immediately above
            // CheckAdoptedForeignModeSelfHealsOnceItIsGone, for why Refresh itself stays
            // unchanged). FakeResolutionAdoptionTimer fires synchronously on command, so - like
            // every check above - none of these ever sleep in real time.
            CheckDebounceDelaysAdoptionUntilTheIntervalElapses(checklist);
            CheckDebounceRestartCollapsesRepeatedChangesIntoOneRefresh(checklist);
            CheckDebounceCallbackMayReArmDuringItsOwnElapse(checklist);
            CheckDebounceNeverLetsATransientForeignModeReachAdoption(checklist);
            CheckDebounceAdoptsAGenuineChangeOnceItHasHeldStable(checklist);
            CheckDebouncePreserveCapturedModeRunsImmediatelyAndCancelsAnyPending(checklist);
            CheckDebounceCancelStopsAPendingCountdown(checklist);
            CheckDebounceIntervalMatchesTheDocumentedValue(checklist);

            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add(string.Format("PASSED {0}/{1}", checklist.Passed, checklist.Total));
            return checklist.Lines;
        }

        // Regression test for the old CDS_UPDATEREGISTRY|CDS_NORESET staged-commit pattern: a
        // success must be exactly CDS_TEST then CDS_UPDATEREGISTRY, both against the real device
        // name, with CDS_NORESET never appearing anywhere.
        private static void CheckNoDeferredApply(Checklist checklist)
        {
            checklist.Lines.Add("A successful change records exactly CDS_TEST then CDS_UPDATEREGISTRY, never CDS_NORESET, never a null device name (regression test for the old staged-commit pattern):");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-RES-NODEFER";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(deviceName, BuildDevmode(1920, 1080, 32, 60, 0));
            ResolutionModeWrapper target = BuildTarget(2560, 1440, 32, 144, 0);

            ResolutionHelper.ResolutionChangeResult result = ResolutionHelper.ChangeResolutionEx(device, target, deviceName, false);

            checklist.Check(result == ResolutionHelper.ResolutionChangeResult.Applied,
                string.Format("ChangeResolutionEx returns Applied, got {0}", result));

            checklist.Check(device.CallLog.Count == 2,
                string.Format("exactly two ChangeMode calls are recorded, got {0}", device.CallLog.Count));

            if (device.CallLog.Count == 2)
            {
                checklist.Check(device.CallLog[0].Flags == ChangeDisplaySettingsFlags.CdsTest,
                    "the first call uses CDS_TEST");
                checklist.Check(device.CallLog[1].Flags == ChangeDisplaySettingsFlags.CdsUpdateregistry,
                    "the second call uses CDS_UPDATEREGISTRY");
            }

            checklist.Check(!device.CallLog.Any(call => (call.Flags & ChangeDisplaySettingsFlags.CdsNoreset) != 0),
                "CDS_NORESET never appears in any recorded call");
            checklist.Check(!device.CallLog.Any(call => call.DeviceName == null),
                "no recorded call has a null device name");
            checklist.Check(device.CallLog.All(call => call.DeviceName == deviceName),
                "every recorded call carries the real device name");

            checklist.Lines.Add(string.Empty);
        }

        // Regression test for "dmFields is never updated": the Devmode ChangeMode is called with
        // must declare the four fields actually being changed, and must keep declaring whatever
        // EnumDisplaySettings already reported (DM_POSITION above all - the only candidate
        // mechanism on file for issue #134, a multi-monitor desktop rearranging itself).
        private static void CheckDmFieldsDeclared(Checklist checklist)
        {
            checklist.Lines.Add("The Devmode passed to ChangeMode declares the four fields being changed and keeps DM_POSITION untouched (regression test for the old 'dmFields never updated' defect):");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-RES-DMFIELDS";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(deviceName, BuildDevmode(1920, 1080, 32, 60, 0));
            ResolutionModeWrapper target = BuildTarget(2560, 1440, 32, 144, 0);

            ResolutionHelper.ChangeResolutionEx(device, target, deviceName, false);

            uint requiredFields = (uint)(DevmodeFields.DmPelsWidth | DevmodeFields.DmPelsHeight |
                DevmodeFields.DmBitsPerPel | DevmodeFields.DmDisplayFrequency);

            checklist.Check(device.CallLog.Count > 0 && device.CallLog.All(call => (call.Mode.dmFields & requiredFields) == requiredFields),
                "DmPelsWidth|DmPelsHeight|DmBitsPerPel|DmDisplayFrequency are set in dmFields on every recorded call");
            checklist.Check(device.CallLog.Count > 0 && device.CallLog.All(call => (call.Mode.dmFields & (uint)DevmodeFields.DmPosition) != 0),
                "the DM_POSITION bit the current mode already carried survives into every recorded call, unmodified");

            checklist.Lines.Add(string.Empty);
        }

        // Regression test for the old unconditional commit call: CDS_TEST failing must gate
        // CDS_UPDATEREGISTRY completely, for every DispChange failure code, not just some of them.
        private static void CheckTestGatesRegistryWrite(Checklist checklist)
        {
            checklist.Lines.Add("CDS_TEST failing gates CDS_UPDATEREGISTRY - never called after a rejected test, for any DispChange failure code (regression test for the old unconditional commit call):");

            DispChange[] failureCodes =
            {
                DispChange.DispChangeRestart,
                DispChange.DispChangeFailed,
                DispChange.DispChangeBadmode,
                DispChange.DispChangeNotupdated,
                DispChange.DispChangeBadflags,
                DispChange.DispChangeBadparam
            };

            bool allGated = true;
            string firstFailureDescription = null;
            foreach (DispChange code in failureCodes)
            {
                ResolutionHelper.ResetForTests();
                string deviceName = "FAKE-RES-GATE-" + code;
                FakeDisplayModeDevice device = new FakeDisplayModeDevice();
                device.SetCurrentMode(deviceName, BuildDevmode(1920, 1080, 32, 60, 0));
                ResolutionModeWrapper target = BuildTarget(2560, 1440, 32, 144, 0);

                // Queued twice - ApplyTargetFields always ORs DM_DISPLAYFIXEDOUTPUT into dmFields
                // (see ResolutionHelper.OwnedFields), so every CDS_TEST failure triggers
                // ChangeResolutionEx's one fixed-output fallback retry. Both attempts have to fail
                // for this check to prove CDS_UPDATEREGISTRY really is unreachable, not merely
                // skipped on the first try.
                device.QueueResult(deviceName, ChangeDisplaySettingsFlags.CdsTest, code);
                device.QueueResult(deviceName, ChangeDisplaySettingsFlags.CdsTest, code);

                ResolutionHelper.ResolutionChangeResult result = ResolutionHelper.ChangeResolutionEx(device, target, deviceName, false);

                bool noRegistryCall = !device.CallLog.Any(call => call.Flags == ChangeDisplaySettingsFlags.CdsUpdateregistry);
                bool returnedFailed = result == ResolutionHelper.ResolutionChangeResult.Failed;

                if (!noRegistryCall || !returnedFailed)
                {
                    allGated = false;
                    firstFailureDescription = string.Format("{0}: returned {1}, CDS_UPDATEREGISTRY called = {2}", code, result, !noRegistryCall);
                    break;
                }
            }

            checklist.Check(allGated, allGated
                ? "all six DispChange failure codes from CDS_TEST return Failed with zero CDS_UPDATEREGISTRY calls"
                : "all six DispChange failure codes from CDS_TEST return Failed with zero CDS_UPDATEREGISTRY calls - " + firstFailureDescription);

            checklist.Lines.Add(string.Empty);
        }

        // Regression test for the old discarded second-call return value: CDS_UPDATEREGISTRY's own
        // result is authoritative. Failed propagates as Failed; Notupdated is not treated as a
        // failure - the mode is verified live regardless - but still returns Applied only once the
        // post-apply readback confirms it, and logs exactly once.
        private static void CheckAuthoritativeResult(Checklist checklist)
        {
            checklist.Lines.Add("CDS_UPDATEREGISTRY's own return code is authoritative - Failed propagates as Failed, Notupdated still verifies and returns Applied (regression test for the old discarded second-call result):");

            ResolutionHelper.ResetForTests();
            const string failDevice = "FAKE-RES-AUTHORITATIVE-FAIL";
            FakeDisplayModeDevice failFakeDevice = new FakeDisplayModeDevice();
            failFakeDevice.SetCurrentMode(failDevice, BuildDevmode(1920, 1080, 32, 60, 0));
            ResolutionModeWrapper failTarget = BuildTarget(2560, 1440, 32, 144, 0);
            failFakeDevice.QueueResult(failDevice, ChangeDisplaySettingsFlags.CdsUpdateregistry, DispChange.DispChangeFailed);

            ResolutionHelper.ResolutionChangeResult failResult = ResolutionHelper.ChangeResolutionEx(failFakeDevice, failTarget, failDevice, false);
            checklist.Check(failResult == ResolutionHelper.ResolutionChangeResult.Failed,
                string.Format("CDS_UPDATEREGISTRY returning DispChangeFailed makes ChangeResolutionEx return Failed, got {0}", failResult));

            ResolutionHelper.ResetForTests();
            const string notupdatedDevice = "FAKE-RES-AUTHORITATIVE-NOTUPDATED";
            FakeDisplayModeDevice notupdatedFakeDevice = new FakeDisplayModeDevice();
            notupdatedFakeDevice.SetCurrentMode(notupdatedDevice, BuildDevmode(1920, 1080, 32, 60, 0));
            ResolutionModeWrapper notupdatedTarget = BuildTarget(2560, 1440, 32, 144, 0);
            notupdatedFakeDevice.QueueResult(notupdatedDevice, ChangeDisplaySettingsFlags.CdsUpdateregistry, DispChange.DispChangeNotupdated);

            ResolutionHelper.ResolutionChangeResult notupdatedResult = ResolutionHelper.ChangeResolutionEx(notupdatedFakeDevice, notupdatedTarget, notupdatedDevice, false);
            checklist.Check(notupdatedResult == ResolutionHelper.ResolutionChangeResult.Applied,
                string.Format("CDS_UPDATEREGISTRY returning DispChangeNotupdated still returns Applied once the readback confirms it, got {0}", notupdatedResult));
            checklist.Check(ResolutionHelper.LoggedLineCountForTests == 1,
                string.Format("exactly one log line is written for the Notupdated case, got {0}", ResolutionHelper.LoggedLineCountForTests));

            checklist.Lines.Add(string.Empty);
        }

        // Regression test for trusting the API's own return code alone: a CDS_UPDATEREGISTRY call
        // that reports success but did not actually change the fake's current mode must not come
        // back as a confirmed Applied - caught by the post-apply readback, which returns the
        // distinct AppliedUnverified rather than a plain Failed (a genuine driver rejection already
        // returns Failed at step 4/5, before ever reaching this readback - conflating the two would
        // make the proxies wrongly discard a change that most likely did land).
        private static void CheckPostApplyVerification(Checklist checklist)
        {
            checklist.Lines.Add("A CDS_UPDATEREGISTRY call that reports success is still verified by reading the mode back - returning AppliedUnverified, not Applied, and not a plain Failed either (regression test for trusting the API's own return code alone):");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-RES-VERIFY";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(deviceName, BuildDevmode(1920, 1080, 32, 60, 0));
            ResolutionModeWrapper target = BuildTarget(2560, 1440, 32, 144, 0);

            // Reports DispChangeSuccessful (the default) but is told not to actually mutate its
            // stored mode for that one call - standing in for a driver that lies about having
            // applied it.
            device.SuppressNextApply(deviceName);

            ResolutionHelper.ResolutionChangeResult result = ResolutionHelper.ChangeResolutionEx(device, target, deviceName, false);
            checklist.Check(result == ResolutionHelper.ResolutionChangeResult.AppliedUnverified,
                string.Format("a reported-successful apply that did not actually change the device's mode returns AppliedUnverified, not Applied and not a plain Failed, got {0}", result));

            checklist.Lines.Add(string.Empty);
        }

        // A CDS_TEST rejection with DM_DISPLAYFIXEDOUTPUT declared retries exactly once, with the
        // bit cleared and the device's own current value restored, then goes on to apply.
        private static void CheckFixedOutputFallback(Checklist checklist)
        {
            checklist.Lines.Add("A CDS_TEST rejection with DM_DISPLAYFIXEDOUTPUT declared retries exactly once with that field dropped, then applies:");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-RES-FIXEDOUTPUT-FALLBACK";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            Devmode current = BuildDevmode(1920, 1080, 32, 60, 0); // device's own DmDisplayFixedOutput is 0 (Default)
            device.SetCurrentMode(deviceName, current);
            ResolutionModeWrapper target = BuildTarget(2560, 1440, 32, 144, (uint)Dmdfo.Center);

            // Only the first CDS_TEST call is queued to fail - the retry, with the bit dropped, is
            // left to default to Successful.
            device.QueueResult(deviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadflags);

            ResolutionHelper.ResolutionChangeResult result = ResolutionHelper.ChangeResolutionEx(device, target, deviceName, false);
            checklist.Check(result == ResolutionHelper.ResolutionChangeResult.Applied,
                string.Format("the retried, successful CDS_TEST is followed through to Applied, got {0}", result));

            List<FakeDisplayModeDevice.RecordedCall> testCalls = device.CallLog.Where(call => call.Flags == ChangeDisplaySettingsFlags.CdsTest).ToList();
            checklist.Check(testCalls.Count == 2,
                string.Format("exactly one retry - two CDS_TEST calls total, got {0}", testCalls.Count));

            if (testCalls.Count == 2)
            {
                checklist.Check((testCalls[0].Mode.dmFields & (uint)DevmodeFields.DmDisplayFixedOutput) != 0 &&
                        testCalls[0].Mode.dmDisplayFixedOutput == (uint)Dmdfo.Center,
                    "the first CDS_TEST call declares DM_DISPLAYFIXEDOUTPUT with the requested (Center) value");
                checklist.Check((testCalls[1].Mode.dmFields & (uint)DevmodeFields.DmDisplayFixedOutput) == 0,
                    "the retried CDS_TEST call has the DM_DISPLAYFIXEDOUTPUT bit cleared from dmFields");
                checklist.Check(testCalls[1].Mode.dmDisplayFixedOutput == current.dmDisplayFixedOutput,
                    "the retried CDS_TEST call's dmDisplayFixedOutput is restored to the device's own current value");
            }

            checklist.Check(device.CallLog.Count(call => call.Flags == ChangeDisplaySettingsFlags.CdsUpdateregistry) == 1,
                "exactly one CDS_UPDATEREGISTRY call follows the successful retry");

            checklist.Lines.Add(string.Empty);
        }

        // Regression test for notification/log spam: a run of identical failures logs and notifies
        // exactly once each, not once per attempt; a REAL success (never a ResetForTests() escape
        // hatch - that would make every one of these assertions pass no matter what the code under
        // test does) re-opens both, so a different failure code logs again, and even a REPEAT of an
        // already-seen code logs again too, proving the reopening is not a one-shot "first new code
        // after a success" special case.
        private static void CheckNoRepeatNotification(Checklist checklist)
        {
            checklist.Lines.Add("A run of identical failures logs and notifies exactly once each, not once per attempt - a real success (not a test reset) is what reopens both, for a new failure code or a repeated one (regression test for notification/log spam):");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-RES-NOREPEAT";
            Devmode mismatched = BuildDevmode(1920, 1080, 32, 60, 0);
            ResolutionModeWrapper target = BuildTarget(2560, 1440, 32, 144, 0);
            // Matches `mismatched` on the four controllable fields - driving ChangeResolutionEx
            // with this as the target, while the fake's current mode is still `mismatched`, is a
            // real AlreadyMatching success, usable at any point to clear this device's state.
            ResolutionModeWrapper clearingTarget = BuildTarget(mismatched.dmPelsWidth, mismatched.dmPelsHeight, mismatched.dmBitsPerPel, mismatched.dmDisplayFrequency, 0);

            List<ResolutionFailureEventArgs> raised = new List<ResolutionFailureEventArgs>();
            EventHandler<ResolutionFailureEventArgs> handler = delegate(object sender, ResolutionFailureEventArgs e) { raised.Add(e); };
            ResolutionHelper.ResolutionChangeFailed += handler;
            // Records the actual log message text, not just LoggedLineCountForTests' bare count - a
            // count alone cannot tell "logged the code THIS phase queued" from "logged some OTHER,
            // stale code that merely happened to be new to the dedup set", which is exactly how a
            // previous version of Phase A masked its own bug (see Phase A's own comment below) while
            // every assertion here stayed green throughout.
            ILogSink previousSink = LogSink.Current;
            RecordingLogSink recordingSink = new RecordingLogSink();
            LogSink.Current = recordingSink;
            try
            {
                // Phase A: 20 consecutive attempts, all rejected by CDS_TEST with the same code.
                // The revert direction's bound (10) is exceeded partway through, after which every
                // further attempt is Suppressed and never reaches the device at all - 20 is
                // deliberately more than the bound, to prove going further adds neither a second
                // log line nor a second notification.
                //
                // Queued ONLY for the first 10 attempts - the ones that actually reach the device
                // before the give-up state kicks in - one entry per attempt, not two: target's
                // DmDisplayFixedOutput (0) already matches the fake's own current mode's
                // (mismatched's, also 0), so the fixed-output fallback retry never fires and each
                // attempt makes exactly one CDS_TEST call (see CheckFixedOutputFallback above for the
                // retry itself). Attempts 11-20 are Suppressed before ever touching the device, so
                // queuing anything for them would just leave it stranded. A prior version of this
                // check queued twice per attempt, for all 20 attempts - leaving ~30 stale
                // DispChangeBadflags entries sitting in this device's shared (deviceName, CdsTest)
                // FIFO queue, which Phase B/C's own freshly-queued DispChangeBadmode entries then
                // waited behind: both phases dequeued a stale DispChangeBadflags instead, silently
                // never exercising DispChangeBadmode at all, while the line-count assertions below
                // stayed green regardless, since ClearFailureState's dedup reset does not care WHICH
                // code caused the log line that follows it. Caught by recording the actual message
                // text (recordingSink, below) rather than trusting the count alone - the same class
                // of hazard CheckExitRestoreBypassesGiveUpSuppression (X7, elsewhere in this file)
                // guards against for a device reused past its own give-up state.
                FakeDisplayModeDevice device = new FakeDisplayModeDevice();
                device.SetCurrentMode(deviceName, mismatched);
                for (int i = 0; i < 20; i++)
                {
                    if (i < 10)
                    {
                        device.QueueResult(deviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadflags);
                    }
                    ResolutionHelper.ChangeResolutionEx(device, target, deviceName, true);
                }
                checklist.Check(raised.Count == 1,
                    string.Format("20 consecutive identical failures raise ResolutionChangeFailed exactly once, got {0}", raised.Count));
                checklist.Check(ResolutionHelper.LoggedLineCountForTests == 1,
                    string.Format("20 consecutive identical failures write exactly one log line, got {0}", ResolutionHelper.LoggedLineCountForTests));
                checklist.Check(recordingSink.Messages.Count == 1 && recordingSink.Messages[0].Contains("DispChangeBadflags"),
                    string.Format("that one log line actually names DispChangeBadflags, the code Phase A queued, got {0}",
                        recordingSink.Messages.Count > 0 ? recordingSink.Messages[recordingSink.Messages.Count - 1] : "<none>"));

                // Phase B: a REAL success (AlreadyMatching) clears every suppression this device
                // was carrying - the fake's current mode never actually changes for this call, only
                // the target handed to it does, so this exercises exactly the same "success clears
                // the device" path a real recovered driver would take. Then a DIFFERENT failure
                // code logs one more line, without a second notification (this fresh streak is only
                // 1 failure deep, nowhere near the bound of 10).
                ResolutionHelper.ResolutionChangeResult clearingResult1 = ResolutionHelper.ChangeResolutionEx(device, clearingTarget, deviceName, false);
                checklist.Check(clearingResult1 == ResolutionHelper.ResolutionChangeResult.AlreadyMatching,
                    "the first clearing call is itself a real success (AlreadyMatching), not a test-only reset");

                int loggedLinesBeforePhaseB = ResolutionHelper.LoggedLineCountForTests;
                raised.Clear();
                // Queued once, not twice, for the same reason as Phase A above - and now that
                // Phase A's own queue leaves nothing stranded behind it, this single entry is the
                // one this call actually consumes.
                device.QueueResult(deviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadmode);
                ResolutionHelper.ChangeResolutionEx(device, target, deviceName, true);
                checklist.Check(ResolutionHelper.LoggedLineCountForTests == loggedLinesBeforePhaseB + 1,
                    string.Format("a different failure code, after a real success reopened the device, logs one more line - had {0} log lines before, {1} after",
                        loggedLinesBeforePhaseB, ResolutionHelper.LoggedLineCountForTests));
                checklist.Check(raised.Count == 0,
                    "a single new failure, below the give-up bound, does not raise a second notification");
                checklist.Check(recordingSink.Messages.Count > 0 && recordingSink.Messages[recordingSink.Messages.Count - 1].Contains("DispChangeBadmode"),
                    string.Format("Phase B's new log line actually names DispChangeBadmode, the code IT queued - not a stale DispChangeBadflags left over from Phase A, got {0}",
                        recordingSink.Messages.Count > 0 ? recordingSink.Messages[recordingSink.Messages.Count - 1] : "<none>"));

                // Phase C: another real success, then a REPEAT of phase B's own code (DispChangeBadmode)
                // - still logs again, proving the reopening is not limited to "only a brand new code
                // works once".
                ResolutionHelper.ResolutionChangeResult clearingResult2 = ResolutionHelper.ChangeResolutionEx(device, clearingTarget, deviceName, false);
                checklist.Check(clearingResult2 == ResolutionHelper.ResolutionChangeResult.AlreadyMatching,
                    "the second clearing call is itself a real success (AlreadyMatching), not a test-only reset");

                int loggedLinesBeforePhaseC = ResolutionHelper.LoggedLineCountForTests;
                device.QueueResult(deviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadmode);
                ResolutionHelper.ChangeResolutionEx(device, target, deviceName, true);
                checklist.Check(ResolutionHelper.LoggedLineCountForTests == loggedLinesBeforePhaseC + 1,
                    string.Format("a repeated failure code, after another real success, logs again instead of staying silent - had {0} log lines before, {1} after",
                        loggedLinesBeforePhaseC, ResolutionHelper.LoggedLineCountForTests));
                checklist.Check(recordingSink.Messages.Count > 0 && recordingSink.Messages[recordingSink.Messages.Count - 1].Contains("DispChangeBadmode"),
                    string.Format("Phase C's new log line also names DispChangeBadmode, got {0}",
                        recordingSink.Messages.Count > 0 ? recordingSink.Messages[recordingSink.Messages.Count - 1] : "<none>"));
            }
            finally
            {
                ResolutionHelper.ResolutionChangeFailed -= handler;
                LogSink.Current = previousSink;
            }

            checklist.Lines.Add(string.Empty);
        }

        // The apply direction gives up after 3 consecutive failures - after that, the device is
        // never called again for this (device, target, direction) until something clears it.
        private static void CheckApplyBound(Checklist checklist)
        {
            checklist.Lines.Add("The apply direction gives up after 3 consecutive failures and stops touching the driver entirely (regression test for the give-up bound):");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-RES-APPLYBOUND";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(deviceName, BuildDevmode(1920, 1080, 32, 60, 0));
            ResolutionModeWrapper target = BuildTarget(2560, 1440, 32, 144, 0);

            List<ResolutionFailureEventArgs> raised = new List<ResolutionFailureEventArgs>();
            EventHandler<ResolutionFailureEventArgs> handler = delegate(object sender, ResolutionFailureEventArgs e) { raised.Add(e); };
            ResolutionHelper.ResolutionChangeFailed += handler;
            try
            {
                int callsAfterAttempt3 = -1;
                for (int attempt = 1; attempt <= 20; attempt++)
                {
                    device.QueueResult(deviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadflags);
                    device.QueueResult(deviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadflags);
                    ResolutionHelper.ChangeResolutionEx(device, target, deviceName, false);
                    if (attempt == 3)
                    {
                        callsAfterAttempt3 = device.CallLog.Count;
                    }
                }

                checklist.Check(callsAfterAttempt3 > 0 && device.CallLog.Count == callsAfterAttempt3,
                    string.Format("attempts 4-20 record zero further calls (had {0} after attempt 3, {1} after attempt 20)",
                        callsAfterAttempt3, device.CallLog.Count));
                checklist.Check(raised.Count == 1 && raised[0].DeviceName == deviceName,
                    string.Format("exactly one notification is raised, for this device - count={0}", raised.Count));
            }
            finally
            {
                ResolutionHelper.ResolutionChangeFailed -= handler;
            }

            checklist.Lines.Add(string.Empty);
        }

        // The revert direction is bounded at 10, not 3 - pins the apply/revert asymmetry so it
        // cannot be "simplified" away to a single shared constant.
        private static void CheckRevertBound(Checklist checklist)
        {
            checklist.Lines.Add("The revert direction tolerates 10 consecutive failures, not 3 - pins the apply/revert asymmetry (regression test to stop it being simplified away):");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-RES-REVERTBOUND";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(deviceName, BuildDevmode(1920, 1080, 32, 60, 0));
            ResolutionModeWrapper target = BuildTarget(2560, 1440, 32, 144, 0);

            int callsAfterAttempt3 = -1;
            int callsAfterAttempt9 = -1;
            int callsAfterAttempt10 = -1;
            for (int attempt = 1; attempt <= 20; attempt++)
            {
                device.QueueResult(deviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadflags);
                device.QueueResult(deviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadflags);
                ResolutionHelper.ChangeResolutionEx(device, target, deviceName, true);
                if (attempt == 3) callsAfterAttempt3 = device.CallLog.Count;
                if (attempt == 9) callsAfterAttempt9 = device.CallLog.Count;
                if (attempt == 10) callsAfterAttempt10 = device.CallLog.Count;
            }

            checklist.Check(callsAfterAttempt9 > callsAfterAttempt3,
                string.Format("the device is still being called past attempt 3 on the revert direction (had {0} calls after attempt 3, {1} after attempt 9)",
                    callsAfterAttempt3, callsAfterAttempt9));
            checklist.Check(callsAfterAttempt10 > callsAfterAttempt9,
                string.Format("attempt 10 itself still reaches the device (had {0} calls after attempt 9, {1} after attempt 10)",
                    callsAfterAttempt9, callsAfterAttempt10));
            checklist.Check(device.CallLog.Count == callsAfterAttempt10,
                string.Format("attempts 11-20 record zero further calls ({0} after attempt 10, {1} after attempt 20)",
                    callsAfterAttempt10, device.CallLog.Count));

            checklist.Lines.Add(string.Empty);
        }

        // A full apply-then-revert cycle against a fake device returns it to the original mode, via
        // the same CDS_TEST-then-CDS_UPDATEREGISTRY sequence both ways.
        private static void CheckFullRevertCycle(Checklist checklist)
        {
            checklist.Lines.Add("A full apply-then-revert cycle against a fake device returns it to the original mode, via the same CDS_TEST-then-CDS_UPDATEREGISTRY sequence both ways:");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-RES-FULLCYCLE";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            Devmode original = BuildDevmode(1920, 1080, 32, 60, 0);
            device.SetCurrentMode(deviceName, original);
            ResolutionModeWrapper gameTarget = BuildTarget(2560, 1440, 32, 144, 0);
            ResolutionModeWrapper originalTarget = BuildTarget(original.dmPelsWidth, original.dmPelsHeight, original.dmBitsPerPel, original.dmDisplayFrequency, original.dmDisplayFixedOutput);

            ResolutionHelper.ResolutionChangeResult applyResult = ResolutionHelper.ChangeResolutionEx(device, gameTarget, deviceName, false);
            checklist.Check(applyResult == ResolutionHelper.ResolutionChangeResult.Applied,
                string.Format("the apply half returns Applied, got {0}", applyResult));
            checklist.Check(gameTarget.MatchesAchievedMode(device.GetCurrentMode(deviceName)),
                "the fake is now at the game's mode");

            ResolutionHelper.ResolutionChangeResult revertResult = ResolutionHelper.ChangeResolutionEx(device, originalTarget, deviceName, true);
            checklist.Check(revertResult == ResolutionHelper.ResolutionChangeResult.Applied,
                string.Format("the revert half returns Applied, got {0}", revertResult));
            checklist.Check(originalTarget.MatchesAchievedMode(device.GetCurrentMode(deviceName)),
                "the fake's current mode is back to the original on all four controllable fields");

            checklist.Check(
                device.CallLog.Count(call => call.Flags == ChangeDisplaySettingsFlags.CdsTest) == 2 &&
                device.CallLog.Count(call => call.Flags == ChangeDisplaySettingsFlags.CdsUpdateregistry) == 2,
                "both halves went through exactly one CDS_TEST and one CDS_UPDATEREGISTRY call each");

            checklist.Lines.Add(string.Empty);
        }

        // A revert that fails every single time must never strand the user silently: every attempt
        // still returns Failed (never Suppressed, since 10 attempts never exceeds the revert
        // bound), exactly one notification fires, and it names the device.
        private static void CheckFailedRevertDoesNotStrandSilently(Checklist checklist)
        {
            checklist.Lines.Add("A revert that fails every single time still returns Failed on every attempt, notifies exactly once, and names the device (regression test for a silently stranded desktop resolution):");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-RES-STRANDED";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(deviceName, BuildDevmode(1920, 1080, 32, 60, 0));
            ResolutionModeWrapper target = BuildTarget(2560, 1440, 32, 144, 0);

            List<ResolutionFailureEventArgs> raised = new List<ResolutionFailureEventArgs>();
            EventHandler<ResolutionFailureEventArgs> handler = delegate(object sender, ResolutionFailureEventArgs e) { raised.Add(e); };
            ResolutionHelper.ResolutionChangeFailed += handler;
            try
            {
                bool allFailed = true;
                for (int attempt = 1; attempt <= 10; attempt++)
                {
                    device.QueueResult(deviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadflags);
                    device.QueueResult(deviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadflags);
                    ResolutionHelper.ResolutionChangeResult result = ResolutionHelper.ChangeResolutionEx(device, target, deviceName, true);
                    allFailed = allFailed && result == ResolutionHelper.ResolutionChangeResult.Failed;
                }

                checklist.Check(allFailed,
                    "every one of the 10 attempts returns Failed, not Suppressed - the revert direction never gives up silently within its own bound");
                checklist.Check(raised.Count == 1 && raised[0].IsRevert && raised[0].DeviceName == deviceName,
                    string.Format("exactly one notification is raised, for a revert, naming device {0}", deviceName));
                checklist.Check(ResolutionHelper.LoggedLineCountForTests >= 1,
                    "at least one log line documents the failure");
            }
            finally
            {
                ResolutionHelper.ResolutionChangeFailed -= handler;
            }

            checklist.Lines.Add(string.Empty);
        }

        // Regression test for #132's "it keeps on saying that": a driver that silently pins
        // DmDisplayFixedOutput away from whatever was requested must not re-fire a real mode set
        // and registry write forever - IsResolutionChangeNeeded has to settle to false once the
        // four controllable fields genuinely converge, and the total number of driver calls across
        // many simulated foreground events has to stay small.
        private static void CheckUnachievableFixedOutputLoopIsBounded(Checklist checklist)
        {
            checklist.Lines.Add("A driver that silently pins DmDisplayFixedOutput away from the requested value does not re-fire a mode change forever (regression test for #132's 'it keeps on saying that'):");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-RES-PINNEDFIXEDOUTPUT";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(deviceName, BuildDevmode(1920, 1080, 32, 60, 0));
            device.PinFixedOutputTo(deviceName, 0);
            ResolutionModeWrapper target = BuildTarget(2560, 1440, 32, 144, (uint)Dmdfo.Center);

            bool everFalse = false;
            for (int i = 0; i < 20; i++)
            {
                if (ResolutionHelper.IsResolutionChangeNeeded(device, deviceName, target))
                {
                    ResolutionHelper.ChangeResolutionEx(device, target, deviceName, false);
                }
                else
                {
                    everFalse = true;
                }
            }

            checklist.Check(everFalse,
                "IsResolutionChangeNeeded returns false once the four controllable fields match, even though DmDisplayFixedOutput never reaches the requested value");
            checklist.Check(target.MatchesAchievedMode(device.GetCurrentMode(deviceName)),
                "the four controllable fields did genuinely converge to the target");
            checklist.Check(device.CallLog.Count <= 3,
                string.Format("total ChangeMode calls across all 20 simulated events is at most 3, got {0}", device.CallLog.Count));

            checklist.Lines.Add(string.Empty);
        }

        // AlreadyMatching must short-circuit before ever touching the driver.
        private static void CheckAlreadyMatchingShortCircuits(Checklist checklist)
        {
            checklist.Lines.Add("A target that already matches the current mode returns AlreadyMatching without calling ChangeMode at all:");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-RES-ALREADYMATCHING";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(deviceName, BuildDevmode(2560, 1440, 32, 144, 0));
            ResolutionModeWrapper target = BuildTarget(2560, 1440, 32, 144, 0);

            ResolutionHelper.ResolutionChangeResult result = ResolutionHelper.ChangeResolutionEx(device, target, deviceName, false);

            checklist.Check(result == ResolutionHelper.ResolutionChangeResult.AlreadyMatching,
                string.Format("ChangeResolutionEx returns AlreadyMatching, got {0}", result));
            checklist.Check(device.CallLog.Count == 0,
                string.Format("zero ChangeMode calls are recorded, got {0}", device.CallLog.Count));

            checklist.Lines.Add(string.Empty);
        }

        // Regression test for ClearFailureState's separator-guarded prefix match: without a
        // separator character that cannot appear in a device name, a success on
        // "FAKE-RES-PREFIX1" would ALSO match every stored key that actually belongs to
        // "FAKE-RES-PREFIX10" - a real string prefix, just not the intended one - and wipe that
        // other device's failure state too.
        private static void CheckClearFailureStateDoesNotStraddleDeviceNames(Checklist checklist)
        {
            checklist.Lines.Add("A success on one device does not clear failure state belonging to a different device whose name is a string-prefix of it (regression test for ClearFailureState's key separator):");
            ResolutionHelper.ResetForTests();

            const string shortDeviceName = "FAKE-RES-PREFIX1";
            const string longDeviceName = "FAKE-RES-PREFIX10"; // shortDeviceName is a literal string prefix of this
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(shortDeviceName, BuildDevmode(1920, 1080, 32, 60, 0));
            device.SetCurrentMode(longDeviceName, BuildDevmode(1920, 1080, 32, 60, 0));
            ResolutionModeWrapper target = BuildTarget(2560, 1440, 32, 144, 0);

            List<ResolutionFailureEventArgs> raised = new List<ResolutionFailureEventArgs>();
            EventHandler<ResolutionFailureEventArgs> handler = delegate(object sender, ResolutionFailureEventArgs e) { raised.Add(e); };
            ResolutionHelper.ResolutionChangeFailed += handler;
            try
            {
                // The long device name fails twice (the apply bound is 3) - one more failure would
                // give up.
                for (int i = 0; i < 2; i++)
                {
                    device.QueueResult(longDeviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadflags);
                    device.QueueResult(longDeviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadflags);
                    ResolutionHelper.ChangeResolutionEx(device, target, longDeviceName, false);
                }

                // The short device name succeeds (AlreadyMatching) - must clear only its own state.
                ResolutionModeWrapper shortMatchingTarget = BuildTarget(1920, 1080, 32, 60, 0);
                ResolutionHelper.ResolutionChangeResult shortResult = ResolutionHelper.ChangeResolutionEx(device, shortMatchingTarget, shortDeviceName, false);
                checklist.Check(shortResult == ResolutionHelper.ResolutionChangeResult.AlreadyMatching,
                    "the short device name's clearing call is itself a real success");

                // If the long device name's count had been wiped by the short device name's
                // success, it would take 3 MORE failures (a fresh streak) to give up - not 1.
                device.QueueResult(longDeviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadflags);
                device.QueueResult(longDeviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadflags);
                ResolutionHelper.ChangeResolutionEx(device, target, longDeviceName, false);

                checklist.Check(raised.Count == 1 && raised[0].DeviceName == longDeviceName,
                    string.Format("the long device name's failure streak survived the short device name's success - one more failure (its 3rd) reaches the apply bound and gives up, got {0} notification(s)", raised.Count));
            }
            finally
            {
                ResolutionHelper.ResolutionChangeFailed -= handler;
            }

            checklist.Lines.Add(string.Empty);
        }

        // Regression test for step 1 (an unreadable current mode) being its own failure category,
        // separate from a rejected mode change: it must return Failed every time and never be
        // counted toward the give-up bound, however many times it recurs - a subtle distinction to
        // preserve, since routing it through the same accounting as a real rejection would silently
        // start giving up on (and Suppressing) a device this class never even tried to change.
        private static void CheckUnreadableCurrentModeNeverCountsTowardGiveUp(Checklist checklist)
        {
            checklist.Lines.Add("An unreadable current mode returns Failed every time and never counts toward the give-up bound (regression test for step 1's separate, uncounted failure category):");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-RES-UNREADABLE";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            // Deliberately never calls SetCurrentMode - TryGetCurrentMode fails for every call.
            ResolutionModeWrapper target = BuildTarget(2560, 1440, 32, 144, 0);

            List<ResolutionFailureEventArgs> raised = new List<ResolutionFailureEventArgs>();
            EventHandler<ResolutionFailureEventArgs> handler = delegate(object sender, ResolutionFailureEventArgs e) { raised.Add(e); };
            ResolutionHelper.ResolutionChangeFailed += handler;
            try
            {
                bool allFailed = true;
                for (int attempt = 1; attempt <= 20; attempt++)
                {
                    ResolutionHelper.ResolutionChangeResult result = ResolutionHelper.ChangeResolutionEx(device, target, deviceName, false);
                    allFailed = allFailed && result == ResolutionHelper.ResolutionChangeResult.Failed;
                }

                checklist.Check(allFailed,
                    "all 20 attempts return Failed, never Suppressed - an unreadable current mode is never counted toward the apply bound (3), however many times it recurs");
                checklist.Check(device.CallLog.Count == 0,
                    "TryGetCurrentMode failing means ChangeMode (CDS_TEST/CDS_UPDATEREGISTRY) is never even reached");
                checklist.Check(raised.Count == 0,
                    "an unreadable current mode never raises ResolutionChangeFailed - it is a different failure category from a rejected mode change");
                checklist.Check(ResolutionHelper.LoggedLineCountForTests == 1,
                    string.Format("still deduped to exactly one log line across all 20 attempts, got {0}", ResolutionHelper.LoggedLineCountForTests));
            }
            finally
            {
                ResolutionHelper.ResolutionChangeFailed -= handler;
            }

            checklist.Lines.Add(string.Empty);
        }

        // ------------------------------------------------------------------
        // ResolutionHelper.RestoreOnExit coverage (X1-X17, upstream #98).
        // ------------------------------------------------------------------

        // Builds a windowsResolutionSettings dictionary with a single entry for deviceName - the
        // shape RestoreOnExit's own guards 3/4 walk (see that method's comment). Item2 (the
        // supported-mode list) is never consulted by RestoreOnExit itself, so an empty list is
        // always enough here.
        private static Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> BuildExitSettings(
            string deviceName, ResolutionModeWrapper savedMode)
        {
            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> settings =
                new Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>>();
            settings[deviceName] = new Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>(savedMode, new List<ResolutionModeWrapper>());
            return settings;
        }

        // X1 - guard 1, the isResolutionChangeApplied half: nothing vibranceGUI itself changed, so
        // nothing to undo.
        private static void CheckExitRestoreNoOpWhenFlagFalse(Checklist checklist)
        {
            checklist.Lines.Add("X1: RestoreOnExit is a no-op when isResolutionChangeApplied is false - nothing vibranceGUI itself changed to undo:");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-EXIT-X1";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(deviceName, BuildDevmode(2560, 1440, 32, 144, 0));
            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> settings =
                BuildExitSettings(deviceName, BuildTarget(1920, 1080, 32, 60, 0));

            ResolutionHelper.ExitRestoreResult result = ResolutionHelper.RestoreOnExit(device, settings, deviceName, false, false);

            checklist.Check(result == ResolutionHelper.ExitRestoreResult.NothingToRestore,
                string.Format("X1: returns NothingToRestore, got {0}", result));
            checklist.Check(device.CallLog.Count == 0,
                string.Format("X1: zero ChangeMode calls, got {0}", device.CallLog.Count));

            checklist.Lines.Add(string.Empty);
        }

        // X2 - guard 1, the neverChangeResolution half: the user's opt-out wins even though
        // isResolutionChangeApplied is true.
        private static void CheckExitRestoreNoOpWhenNeverChangeResolution(Checklist checklist)
        {
            checklist.Lines.Add("X2: RestoreOnExit is a no-op when neverChangeResolution is true, even though isResolutionChangeApplied is true:");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-EXIT-X2";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(deviceName, BuildDevmode(2560, 1440, 32, 144, 0));
            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> settings =
                BuildExitSettings(deviceName, BuildTarget(1920, 1080, 32, 60, 0));

            ResolutionHelper.ExitRestoreResult result = ResolutionHelper.RestoreOnExit(device, settings, deviceName, true, true);

            checklist.Check(result == ResolutionHelper.ExitRestoreResult.NothingToRestore,
                string.Format("X2: returns NothingToRestore, got {0}", result));
            checklist.Check(device.CallLog.Count == 0,
                string.Format("X2: zero ChangeMode calls, got {0}", device.CallLog.Count));

            checklist.Lines.Add(string.Empty);
        }

        // X3 - guard 2, the one NothingToRestore guard that logs: a null or empty device name means
        // vibranceGUI believes it changed something but has lost track of which screen, which is a
        // real "we owe the user a restore and cannot deliver it" case, not an ordinary no-op. Both
        // the null and the empty-string case log their own line - neither goes through
        // _loggedFailures, so there is no dedup between them to prove absent.
        private static void CheckExitRestoreNoOpWhenDeviceNameMissing(Checklist checklist)
        {
            checklist.Lines.Add("X3: RestoreOnExit is a no-op for a null or empty game device name, and logs once per attempt (the one NothingToRestore guard worth a log line):");
            ResolutionHelper.ResetForTests();

            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> settings =
                new Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>>();

            ResolutionHelper.ExitRestoreResult nullResult = ResolutionHelper.RestoreOnExit(device, settings, null, false, true);
            checklist.Check(nullResult == ResolutionHelper.ExitRestoreResult.NothingToRestore,
                string.Format("X3: a null device name returns NothingToRestore, got {0}", nullResult));
            checklist.Check(ResolutionHelper.LoggedLineCountForTests == 1,
                string.Format("X3: a null device name logs exactly once, got {0} lines", ResolutionHelper.LoggedLineCountForTests));

            ResolutionHelper.ExitRestoreResult emptyResult = ResolutionHelper.RestoreOnExit(device, settings, string.Empty, false, true);
            checklist.Check(emptyResult == ResolutionHelper.ExitRestoreResult.NothingToRestore,
                string.Format("X3: an empty device name returns NothingToRestore, got {0}", emptyResult));
            checklist.Check(ResolutionHelper.LoggedLineCountForTests == 2,
                string.Format("X3: the empty-name attempt logs its own line too, not deduped against the null-name attempt, got {0} lines total", ResolutionHelper.LoggedLineCountForTests));

            checklist.Check(device.CallLog.Count == 0,
                string.Format("X3: zero ChangeMode calls across both attempts, got {0}", device.CallLog.Count));

            checklist.Lines.Add(string.Empty);
        }

        // X4 - guards 3/4, the ordinary case: the game's own device name is simply absent from the
        // dictionary (never populated, or the device dropped out of a later refresh).
        private static void CheckExitRestoreNoOpWhenDeviceAbsentFromDictionary(Checklist checklist)
        {
            checklist.Lines.Add("X4: RestoreOnExit is a no-op when the game's device name has no entry in windowsResolutionSettings at all:");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-EXIT-X4";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(deviceName, BuildDevmode(2560, 1440, 32, 144, 0));
            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> settings =
                new Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>>();

            ResolutionHelper.ExitRestoreResult result = ResolutionHelper.RestoreOnExit(device, settings, deviceName, false, true);

            checklist.Check(result == ResolutionHelper.ExitRestoreResult.NothingToRestore,
                string.Format("X4: returns NothingToRestore, got {0}", result));
            checklist.Check(device.CallLog.Count == 0,
                string.Format("X4: zero ChangeMode calls, got {0}", device.CallLog.Count));

            checklist.Lines.Add(string.Empty);
        }

        // Extra - guard 3's other half: RestoreOnExit tolerates a null dictionary itself, not just a
        // missing entry inside one (VibranceGUI's own field is never actually null in production, but
        // the guard exists in the source and is cheap to pin directly).
        private static void CheckExitRestoreNoOpWhenDictionaryIsNull(Checklist checklist)
        {
            checklist.Lines.Add("Extra: RestoreOnExit tolerates a null windowsResolutionSettings dictionary itself, not just a missing entry:");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-EXIT-X4B";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(deviceName, BuildDevmode(2560, 1440, 32, 144, 0));

            ResolutionHelper.ExitRestoreResult result = ResolutionHelper.RestoreOnExit(device, null, deviceName, false, true);

            checklist.Check(result == ResolutionHelper.ExitRestoreResult.NothingToRestore,
                string.Format("a null dictionary returns NothingToRestore, got {0}", result));
            checklist.Check(device.CallLog.Count == 0,
                string.Format("zero ChangeMode calls, got {0}", device.CallLog.Count));

            checklist.Lines.Add(string.Empty);
        }

        // Extra - guard 4's other half: a dictionary entry whose Item1 is itself null (the device is
        // known but no Windows mode was ever successfully captured for it).
        private static void CheckExitRestoreNoOpWhenSavedModeIsNull(Checklist checklist)
        {
            checklist.Lines.Add("Extra: RestoreOnExit tolerates a dictionary entry whose Item1 is itself null:");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-EXIT-X4C";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(deviceName, BuildDevmode(2560, 1440, 32, 144, 0));
            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> settings =
                BuildExitSettings(deviceName, null);

            ResolutionHelper.ExitRestoreResult result = ResolutionHelper.RestoreOnExit(device, settings, deviceName, false, true);

            checklist.Check(result == ResolutionHelper.ExitRestoreResult.NothingToRestore,
                string.Format("a null Item1 returns NothingToRestore, got {0}", result));
            checklist.Check(device.CallLog.Count == 0,
                string.Format("zero ChangeMode calls, got {0}", device.CallLog.Count));

            checklist.Lines.Add(string.Empty);
        }

        // X5. The happy path: a real apply (via ChangeResolutionEx, exactly as OnWinEventHook's own
        // apply branch would drive it) puts the game's mode live, then RestoreOnExit sends the
        // desktop mode back through exactly one CDS_TEST and one CDS_UPDATEREGISTRY.
        private static void CheckExitRestoreAppliesSavedMode(Checklist checklist)
        {
            checklist.Lines.Add("X5: after a real apply put the game's mode live, RestoreOnExit sends the desktop mode back through exactly one CDS_TEST and one CDS_UPDATEREGISTRY, and returns Restored:");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-EXIT-X5";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(deviceName, BuildDevmode(1920, 1080, 32, 60, 0));
            ResolutionModeWrapper desktopTarget = BuildTarget(1920, 1080, 32, 60, 0);
            ResolutionModeWrapper gameTarget = BuildTarget(2560, 1440, 32, 144, 0);

            ResolutionHelper.ResolutionChangeResult applyResult = ResolutionHelper.ChangeResolutionEx(device, gameTarget, deviceName, false);
            checklist.Check(applyResult == ResolutionHelper.ResolutionChangeResult.Applied,
                string.Format("X5: the setup apply returns Applied, got {0}", applyResult));

            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> settings =
                BuildExitSettings(deviceName, desktopTarget);

            int callsBeforeExit = device.CallLog.Count;
            ResolutionHelper.ExitRestoreResult result = ResolutionHelper.RestoreOnExit(device, settings, deviceName, false, true);

            checklist.Check(result == ResolutionHelper.ExitRestoreResult.Restored,
                string.Format("X5: returns Restored, got {0}", result));
            checklist.Check(desktopTarget.MatchesAchievedMode(device.GetCurrentMode(deviceName)),
                "X5: the fake device is genuinely back at the desktop mode (Item1)");

            List<FakeDisplayModeDevice.RecordedCall> exitCalls = device.CallLog.GetRange(callsBeforeExit, device.CallLog.Count - callsBeforeExit);
            int testCalls = exitCalls.Count(call => call.Flags == ChangeDisplaySettingsFlags.CdsTest);
            int registryCalls = exitCalls.Count(call => call.Flags == ChangeDisplaySettingsFlags.CdsUpdateregistry);
            checklist.Check(testCalls == 1 && registryCalls == 1,
                string.Format("X5: the exit restore itself is exactly one CDS_TEST and one CDS_UPDATEREGISTRY, got {0} test / {1} registry calls", testCalls, registryCalls));

            checklist.Lines.Add(string.Empty);
        }

        // X6 - guard 5: the saved mode already matches what is live (the game's own exit, or a
        // debounced refresh, already brought the desktop back) - IsResolutionChangeNeeded's own
        // guard, never reaching the driver at all.
        private static void CheckExitRestoreNoOpWhenAlreadyAtSavedMode(Checklist checklist)
        {
            checklist.Lines.Add("X6: RestoreOnExit is a no-op when the fake device is already at the saved mode:");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-EXIT-X6";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(deviceName, BuildDevmode(1920, 1080, 32, 60, 0));
            ResolutionModeWrapper desktopTarget = BuildTarget(1920, 1080, 32, 60, 0);
            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> settings =
                BuildExitSettings(deviceName, desktopTarget);

            ResolutionHelper.ExitRestoreResult result = ResolutionHelper.RestoreOnExit(device, settings, deviceName, false, true);

            checklist.Check(result == ResolutionHelper.ExitRestoreResult.NothingToRestore,
                string.Format("X6: returns NothingToRestore, got {0}", result));
            checklist.Check(device.CallLog.Count == 0,
                string.Format("X6: zero ChangeMode calls, got {0}", device.CallLog.Count));

            checklist.Lines.Add(string.Empty);
        }

        // X7 - the critical one. Drives a key into the give-up (Suppressed) state through the
        // NORMAL, honourGiveUp:true path (RevertFailureBound consecutive failures), confirms that
        // directly, then proves RestoreOnExit still reaches the driver for one last attempt and can
        // restore successfully. Without honourGiveUp actually being wired through to the suppression
        // guard, this would come back Suppressed-via-NothingToRestore-looking-like-Failed instead -
        // this is the check that would catch the bypass silently being dead code.
        private static void CheckExitRestoreBypassesGiveUpSuppression(Checklist checklist)
        {
            checklist.Lines.Add("X7 (the critical one): once a revert key has given up through the normal path, RestoreOnExit still reaches the driver for one last attempt and restores successfully - proves honourGiveUp:false is actually wired, not dead code:");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-EXIT-X7";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(deviceName, BuildDevmode(2560, 1440, 32, 144, 0));
            ResolutionModeWrapper desktopTarget = BuildTarget(1920, 1080, 32, 60, 0);

            // RevertFailureBound (ResolutionHelper.cs) is 10 - exactly enough consecutive failures to
            // give up, no more. Queued ONCE per attempt, not twice like CheckApplyBound/
            // CheckRevertBound above - those tests discard their device once the loop ends, but this
            // device is reused past the give-up state, by RestoreOnExit itself below, and desktopTarget's
            // DmDisplayFixedOutput (0) matches the fake's own current mode's (0), so the fixed-output
            // fallback retry never fires and exactly one CDS_TEST call is made per attempt. A leftover
            // second queued failure would sit unconsumed until RestoreOnExit's own CDS_TEST call
            // dequeues it, silently turning "reaches the driver" into "reaches the driver and fails" -
            // exactly the false negative this check exists to rule out.
            for (int attempt = 1; attempt <= 10; attempt++)
            {
                device.QueueResult(deviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadflags);
                ResolutionHelper.ChangeResolutionEx(device, desktopTarget, deviceName, true);
            }

            int callsBeforeProbe = device.CallLog.Count;
            ResolutionHelper.ResolutionChangeResult suppressedProbe = ResolutionHelper.ChangeResolutionEx(device, desktopTarget, deviceName, true);
            checklist.Check(suppressedProbe == ResolutionHelper.ResolutionChangeResult.Suppressed,
                string.Format("X7: the key is genuinely in the give-up state before RestoreOnExit runs, got {0}", suppressedProbe));
            checklist.Check(device.CallLog.Count == callsBeforeProbe,
                "X7: the Suppressed probe itself touches the driver zero times");

            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> settings =
                BuildExitSettings(deviceName, desktopTarget);

            int callsBeforeExit = device.CallLog.Count;
            ResolutionHelper.ExitRestoreResult result = ResolutionHelper.RestoreOnExit(device, settings, deviceName, false, true);

            checklist.Check(result == ResolutionHelper.ExitRestoreResult.Restored,
                string.Format("X7: RestoreOnExit still reaches the driver and restores despite the give-up state, got {0}", result));
            checklist.Check(device.CallLog.Count > callsBeforeExit,
                "X7: RestoreOnExit actually called ChangeMode - the give-up state did not silently no-op it");
            checklist.Check(desktopTarget.MatchesAchievedMode(device.GetCurrentMode(deviceName)),
                "X7: the fake device's current mode is genuinely back at the desktop target");

            checklist.Lines.Add(string.Empty);
        }

        // X8. The X7 give-up state, but the exit attempt itself also fails: still returns Failed
        // (never Suppressed - the check that would ever return it is skipped), and still writes at
        // least one log line, proving the RestoreOnExit-specific log call is not itself swallowed by
        // _loggedFailures' dedup against a code already logged during the game session.
        //
        // raised.Count == 0 below is NOT because nothing is listening - the handler stays attached
        // on purpose, so a stray raise would be caught. It is zero because _notifiedFailures already
        // holds this exact (device, target) key from the give-up loop's own notification above (the
        // 10th attempt reaching RevertFailureBound) - RecordFailureAccounting's own dedup
        // (_notifiedFailures.Add(...) returning false) suppresses a second raise for the same key,
        // exit attempt or not, exactly as it would for two failures anywhere in the same streak. The
        // real, structural guarantee that a user never sees a give-up balloon during shutdown is
        // VibranceGUI.CleanUp detaching ResolutionChangeFailed (-=) BEFORE calling HandleDvcExit, not
        // this dedup - a FIRST give-up for a (device, target) that had never failed before exit WOULD
        // still raise here. Production relies on CleanUp's own unsubscribe for that, not on this
        // coincidence of state - see VibranceGUI.cs, CleanUp's own top-of-method comment.
        private static void CheckExitRestoreFailureDoesNotNotifyButDoesLog(Checklist checklist)
        {
            checklist.Lines.Add("X8: with the same give-up state, a RestoreOnExit attempt that also fails returns Failed, raises zero ResolutionChangeFailed notifications, and still writes at least one log line for the final attempt:");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-EXIT-X8";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(deviceName, BuildDevmode(2560, 1440, 32, 144, 0));
            ResolutionModeWrapper desktopTarget = BuildTarget(1920, 1080, 32, 60, 0);

            // Queued once per attempt - see CheckExitRestoreBypassesGiveUpSuppression's (X7) own
            // comment for why this device's shared queue makes that the correct count, not two.
            for (int attempt = 1; attempt <= 10; attempt++)
            {
                device.QueueResult(deviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadflags);
                ResolutionHelper.ChangeResolutionEx(device, desktopTarget, deviceName, true);
            }

            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> settings =
                BuildExitSettings(deviceName, desktopTarget);

            List<ResolutionFailureEventArgs> raised = new List<ResolutionFailureEventArgs>();
            EventHandler<ResolutionFailureEventArgs> handler = delegate(object sender, ResolutionFailureEventArgs e) { raised.Add(e); };
            ResolutionHelper.ResolutionChangeFailed += handler;
            int loggedBefore = ResolutionHelper.LoggedLineCountForTests;
            ResolutionHelper.ExitRestoreResult result;
            try
            {
                // The exit attempt itself is also forced to fail - queued the same defensive-twice
                // way every other forced-failure check above does.
                device.QueueResult(deviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadflags);
                device.QueueResult(deviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadflags);
                result = ResolutionHelper.RestoreOnExit(device, settings, deviceName, false, true);
            }
            finally
            {
                ResolutionHelper.ResolutionChangeFailed -= handler;
            }

            checklist.Check(result == ResolutionHelper.ExitRestoreResult.Failed,
                string.Format("X8: a failing exit attempt returns Failed, got {0}", result));
            checklist.Check(raised.Count == 0,
                string.Format("X8: zero ResolutionChangeFailed notifications from the exit attempt itself, got {0}", raised.Count));
            checklist.Check(ResolutionHelper.LoggedLineCountForTests >= loggedBefore + 1,
                string.Format("X8: at least one new log line documents the exit attempt, had {0} before, {1} after", loggedBefore, ResolutionHelper.LoggedLineCountForTests));

            checklist.Lines.Add(string.Empty);
        }

        // X9. CDS_UPDATEREGISTRY reports success but the readback does not confirm it - RestoreOnExit
        // maps ChangeResolutionEx's AppliedUnverified to Unverified, through exactly one CDS_TEST and
        // one CDS_UPDATEREGISTRY call (no retry - step 6's readback mismatch is not a CDS_TEST
        // rejection, so the fixed-output fallback never enters into it).
        private static void CheckExitRestoreUnverifiedApply(Checklist checklist)
        {
            checklist.Lines.Add("X9: a RestoreOnExit attempt whose CDS_UPDATEREGISTRY reports success but the readback does not confirm it returns Unverified, via exactly one CDS_TEST and one CDS_UPDATEREGISTRY call:");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-EXIT-X9";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(deviceName, BuildDevmode(2560, 1440, 32, 144, 0));
            ResolutionModeWrapper desktopTarget = BuildTarget(1920, 1080, 32, 60, 0);
            device.SuppressNextApply(deviceName);

            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> settings =
                BuildExitSettings(deviceName, desktopTarget);

            ResolutionHelper.ExitRestoreResult result = ResolutionHelper.RestoreOnExit(device, settings, deviceName, false, true);

            checklist.Check(result == ResolutionHelper.ExitRestoreResult.Unverified,
                string.Format("X9: returns Unverified, got {0}", result));
            checklist.Check(device.CallLog.Count == 2,
                string.Format("X9: exactly two ChangeMode calls, got {0}", device.CallLog.Count));

            checklist.Lines.Add(string.Empty);
        }

        // X10. A hard CDS_TEST rejection makes RestoreOnExit try at most twice (CDS_TEST itself, plus
        // the one fixed-output fallback retry every ChangeResolutionEx call is entitled to - see
        // CheckFixedOutputFallback above) and never reach CDS_UPDATEREGISTRY at all - pins "one
        // attempt, no retry loop" at the RestoreOnExit level the same way CheckApplyBound/
        // CheckRevertBound already pin it at the ChangeResolutionEx level.
        private static void CheckExitRestoreNeverRetries(Checklist checklist)
        {
            checklist.Lines.Add("X10: a hard CDS_TEST rejection makes RestoreOnExit try at most twice and never reach CDS_UPDATEREGISTRY at all - pins 'one attempt, no retry loop':");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-EXIT-X10";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(deviceName, BuildDevmode(2560, 1440, 32, 144, 0));
            ResolutionModeWrapper desktopTarget = BuildTarget(1920, 1080, 32, 60, 0);
            device.QueueResult(deviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadflags);
            device.QueueResult(deviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadflags);

            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> settings =
                BuildExitSettings(deviceName, desktopTarget);

            ResolutionHelper.ExitRestoreResult result = ResolutionHelper.RestoreOnExit(device, settings, deviceName, false, true);

            int testCalls = device.CallLog.Count(call => call.Flags == ChangeDisplaySettingsFlags.CdsTest);
            int registryCalls = device.CallLog.Count(call => call.Flags == ChangeDisplaySettingsFlags.CdsUpdateregistry);
            checklist.Check(result == ResolutionHelper.ExitRestoreResult.Failed,
                string.Format("X10: returns Failed, got {0}", result));
            checklist.Check(testCalls <= 2,
                string.Format("X10: at most two CDS_TEST calls (no retry loop), got {0}", testCalls));
            checklist.Check(registryCalls == 0,
                string.Format("X10: zero CDS_UPDATEREGISTRY calls - CDS_TEST failing gates it completely, got {0}", registryCalls));

            checklist.Lines.Add(string.Empty);
        }

        // X11. A device whose ChangeMode throws (a driver-level exception, not a returned failure
        // code) must not propagate out of RestoreOnExit - this alone would be survivable with a
        // try/catch around just the ChangeResolutionEx call. The wider reason RestoreOnExit wraps
        // its ENTIRE body instead - IsResolutionChangeNeeded (guard 5) also reaches the device,
        // through TryGetCurrentMode, so a device broken enough to throw on a READ, not just a write,
        // must be survivable too - is what X17 below proves; this check alone does not exercise that
        // wider claim.
        private static void CheckExitRestoreSurvivesADeviceThatThrows(Checklist checklist)
        {
            checklist.Lines.Add("X11: a device whose ChangeMode throws makes RestoreOnExit return Failed instead of propagating, and still logs exactly once:");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-EXIT-X11";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(deviceName, BuildDevmode(2560, 1440, 32, 144, 0));
            ResolutionModeWrapper desktopTarget = BuildTarget(1920, 1080, 32, 60, 0);
            device.ThrowOnChangeMode(deviceName);

            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> settings =
                BuildExitSettings(deviceName, desktopTarget);

            int loggedBefore = ResolutionHelper.LoggedLineCountForTests;
            ResolutionHelper.ExitRestoreResult result = ResolutionHelper.RestoreOnExit(device, settings, deviceName, false, true);

            checklist.Check(result == ResolutionHelper.ExitRestoreResult.Failed,
                string.Format("X11: a throwing device is caught and returns Failed, not propagated, got {0}", result));
            checklist.Check(ResolutionHelper.LoggedLineCountForTests == loggedBefore + 1,
                string.Format("X11: exactly one log line documents the exception, had {0} before, {1} after", loggedBefore, ResolutionHelper.LoggedLineCountForTests));

            checklist.Lines.Add(string.Empty);
        }

        // X17. The read-side mirror of X11: a device whose TryGetCurrentMode itself throws - reached
        // through IsResolutionChangeNeeded (guard 5), BEFORE ChangeResolutionEx is ever called, let
        // alone ChangeMode - must also not propagate out of RestoreOnExit. This is the check that
        // actually exercises "the whole body, not just the ChangeResolutionEx call, is wrapped in
        // try/catch": X11 alone throws only from ChangeMode, which a narrower try/catch around just
        // that one call would have caught exactly as well, so X11 on its own never proved the wider
        // wrap was needed.
        private static void CheckExitRestoreSurvivesAReadThatThrows(Checklist checklist)
        {
            checklist.Lines.Add("X17: a device whose TryGetCurrentMode itself throws (reached through IsResolutionChangeNeeded, before ChangeResolutionEx is ever called) makes RestoreOnExit return Failed instead of propagating, and still logs exactly once:");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-EXIT-X17";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            ResolutionModeWrapper desktopTarget = BuildTarget(1920, 1080, 32, 60, 0);
            device.ThrowOnGetCurrentMode(deviceName);

            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> settings =
                BuildExitSettings(deviceName, desktopTarget);

            int loggedBefore = ResolutionHelper.LoggedLineCountForTests;
            ResolutionHelper.ExitRestoreResult result = ResolutionHelper.RestoreOnExit(device, settings, deviceName, false, true);

            checklist.Check(result == ResolutionHelper.ExitRestoreResult.Failed,
                string.Format("X17: a device whose current-mode read throws is caught and returns Failed, not propagated, got {0}", result));
            checklist.Check(ResolutionHelper.LoggedLineCountForTests == loggedBefore + 1,
                string.Format("X17: exactly one log line documents the exception, had {0} before, {1} after", loggedBefore, ResolutionHelper.LoggedLineCountForTests));
            checklist.Check(device.CallLog.Count == 0,
                string.Format("X17: TryGetCurrentMode throwing means ChangeMode (CDS_TEST/CDS_UPDATEREGISTRY) is never even reached, got {0}", device.CallLog.Count));

            checklist.Lines.Add(string.Empty);
        }

        // Reflection seam for _windowsResolutionSettings/_gameScreen (NVIDIA) and _gameScreen (AMD) -
        // neither proxy exposes a production setter reachable without a live game event, the same
        // justification VibranceRestoreFixture's own N11/A6 give for reflecting into _gameScreen.
        // NVIDIA's ResetForTests seam (used by X12/X13 below) already covers _vibranceInfo/
        // _applicationSettings/_device, but was never extended to _windowsResolutionSettings - it
        // does not need to be, for anything ResetForTests' own callers use it for, so this stays a
        // narrow, fixture-local seam rather than growing the production method's surface.
        private static void SetNvidiaWindowsResolutionSettings(Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> settings)
        {
            FieldInfo field = typeof(NvidiaDynamicVibranceProxy).GetField("_windowsResolutionSettings", BindingFlags.NonPublic | BindingFlags.Static);
            field.SetValue(null, settings);
        }

        private static void SetNvidiaGameScreen(Screen screen)
        {
            FieldInfo field = typeof(NvidiaDynamicVibranceProxy).GetField("_gameScreen", BindingFlags.NonPublic | BindingFlags.Static);
            field.SetValue(null, screen);
        }

        private static void SetAmdGameScreen(Screen screen)
        {
            FieldInfo field = typeof(AmdDynamicVibranceProxy).GetField("_gameScreen", BindingFlags.NonPublic | BindingFlags.Static);
            field.SetValue(null, screen);
        }

        // VibranceInfo is a struct (Definitions.cs), so GetVibranceInfo() hands back a COPY - fine to
        // read (X12-X15 all do), but "GetVibranceInfo().isResolutionChangeApplied = true" would try
        // to mutate that throwaway copy and the compiler rightly refuses (CS1612). Setting a field
        // has to go through the proxy's own storage instead: NvidiaDynamicVibranceProxy already
        // exposes that as ResetForTests (its _vibranceInfo is static, so ResetForTests's assignment
        // - copying the caller's local struct in - is enough on its own); AmdDynamicVibranceProxy's
        // own copy is a private INSTANCE field with no equivalent seam, so this reflects into it
        // directly, the same class of seam SetNvidiaGameScreen/SetAmdGameScreen above already use.
        private static void SetAmdVibranceInfo(AmdDynamicVibranceProxy proxy, VibranceInfo vibranceInfo)
        {
            FieldInfo field = typeof(AmdDynamicVibranceProxy).GetField("_vibranceInfo", BindingFlags.NonPublic | BindingFlags.Instance);
            field.SetValue(proxy, vibranceInfo);
        }

        // A fresh, uninitialized NVIDIA proxy instance purely so GetVibranceInfo() (an instance
        // method over a static field) can be called at all - mirrors StartupForegroundFixture's own
        // NewNvidiaProxy exactly, including the FormatterServices.GetUninitializedObject seam (no
        // constructor, so no initializeLibrary() and no real GPU touched). Used by X12/X13 to read
        // _vibranceInfo's CURRENT value after RestoreResolutionOnExit has run - the local
        // VibranceInfo those checks built for ResetForTests is a struct, so it is its own,
        // independent copy from the moment ResetForTests assigns it into the static field; only a
        // fresh read through GetVibranceInfo() sees what RestoreResolutionOnExit actually did to it.
        private static IVibranceProxy NewNvidiaProxy()
        {
            return (IVibranceProxy)FormatterServices.GetUninitializedObject(typeof(NvidiaDynamicVibranceProxy));
        }

        // X12 (NVIDIA). HandleDvcExit's own new last statement, RestoreResolutionOnExit, restores the
        // saved desktop mode through a fake display (never _device, never a real GPU) and clears
        // isResolutionChangeApplied once the restore lands. gameDeviceName is the real desktop
        // screen's own DeviceName - not a synthetic "FAKE-..." string - purely so _gameScreen (a real
        // Screen; it has no public constructor) and the dictionary key agree; the DISPLAY device
        // itself is still the fake, never touched for real.
        private static void CheckNvidiaExitRestoreAppliesSavedModeAndClearsFlag(Checklist checklist)
        {
            checklist.Lines.Add("X12 (NVIDIA): RestoreResolutionOnExit restores the saved desktop mode through a fake display and clears isResolutionChangeApplied:");
            ResolutionHelper.ResetForTests();

            Screen currentScreen = Screen.FromHandle(GetDesktopWindow());
            string gameDeviceName = currentScreen.DeviceName;

            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(gameDeviceName, BuildDevmode(2560, 1440, 32, 144, 0));
            ResolutionModeWrapper desktopTarget = BuildTarget(1920, 1080, 32, 60, 0);

            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.neverChangeResolution = false;
            vibranceInfo.isResolutionChangeApplied = true;
            NvidiaDynamicVibranceProxy.ResetForTests(null, vibranceInfo, new List<ApplicationSetting>());
            SetNvidiaWindowsResolutionSettings(BuildExitSettings(gameDeviceName, desktopTarget));
            SetNvidiaGameScreen(currentScreen);

            ResolutionHelper.ExitRestoreResult result = NvidiaDynamicVibranceProxy.RestoreResolutionOnExit(device);

            checklist.Check(result == ResolutionHelper.ExitRestoreResult.Restored,
                string.Format("X12: returns Restored, got {0}", result));
            checklist.Check(desktopTarget.MatchesAchievedMode(device.GetCurrentMode(gameDeviceName)),
                "X12: the fake device is back at the saved desktop mode");
            checklist.Check(!NewNvidiaProxy().GetVibranceInfo().isResolutionChangeApplied,
                "X12: isResolutionChangeApplied is cleared once the restore lands");

            checklist.Lines.Add(string.Empty);
        }

        // X13 (NVIDIA). The X12 mirror: a forced driver failure returns Failed and leaves
        // isResolutionChangeApplied exactly as it was - never claiming a restore that did not land.
        private static void CheckNvidiaExitRestoreFailureLeavesFlagSet(Checklist checklist)
        {
            checklist.Lines.Add("X13 (NVIDIA): a RestoreResolutionOnExit attempt that fails against the driver returns Failed and leaves isResolutionChangeApplied true:");
            ResolutionHelper.ResetForTests();

            Screen currentScreen = Screen.FromHandle(GetDesktopWindow());
            string gameDeviceName = currentScreen.DeviceName;

            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(gameDeviceName, BuildDevmode(2560, 1440, 32, 144, 0));
            ResolutionModeWrapper desktopTarget = BuildTarget(1920, 1080, 32, 60, 0);
            device.QueueResult(gameDeviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadflags);
            device.QueueResult(gameDeviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadflags);

            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.neverChangeResolution = false;
            vibranceInfo.isResolutionChangeApplied = true;
            NvidiaDynamicVibranceProxy.ResetForTests(null, vibranceInfo, new List<ApplicationSetting>());
            SetNvidiaWindowsResolutionSettings(BuildExitSettings(gameDeviceName, desktopTarget));
            SetNvidiaGameScreen(currentScreen);

            ResolutionHelper.ExitRestoreResult result = NvidiaDynamicVibranceProxy.RestoreResolutionOnExit(device);

            checklist.Check(result == ResolutionHelper.ExitRestoreResult.Failed,
                string.Format("X13: returns Failed, got {0}", result));
            checklist.Check(NewNvidiaProxy().GetVibranceInfo().isResolutionChangeApplied,
                "X13: isResolutionChangeApplied is still true after a failed restore");

            checklist.Lines.Add(string.Empty);
        }

        // X14 (AMD). The X12 equivalent - AmdDynamicVibranceProxy.RestoreResolutionOnExit is an
        // instance method over instance state (_windowsResolutionSettings, _vibranceInfo), so no
        // reflection is needed for either of those; only _gameScreen (static on AMD too - see
        // AmdDynamicVibranceProxy's own field comment) needs it, exactly as VibranceRestoreFixture's
        // A6 already establishes.
        private static void CheckAmdExitRestoreAppliesSavedModeAndClearsFlag(Checklist checklist)
        {
            checklist.Lines.Add("X14 (AMD): RestoreResolutionOnExit restores the saved desktop mode through a fake display and clears isResolutionChangeApplied:");
            ResolutionHelper.ResetForTests();
            VibranceRestoreHelper.ResetForTests();

            Screen currentScreen = Screen.FromHandle(GetDesktopWindow());
            string gameDeviceName = currentScreen.DeviceName;

            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(gameDeviceName, BuildDevmode(2560, 1440, 32, 144, 0));
            ResolutionModeWrapper desktopTarget = BuildTarget(1920, 1080, 32, 60, 0);
            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> settings =
                BuildExitSettings(gameDeviceName, desktopTarget);

            AmdDynamicVibranceProxy proxy = new AmdDynamicVibranceProxy(new FakeAmdAdapter(), new List<ApplicationSetting>(), settings);
            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.neverChangeResolution = false;
            vibranceInfo.isResolutionChangeApplied = true;
            SetAmdVibranceInfo(proxy, vibranceInfo);
            SetAmdGameScreen(currentScreen);

            ResolutionHelper.ExitRestoreResult result = proxy.RestoreResolutionOnExit(device);

            checklist.Check(result == ResolutionHelper.ExitRestoreResult.Restored,
                string.Format("X14: returns Restored, got {0}", result));
            checklist.Check(desktopTarget.MatchesAchievedMode(device.GetCurrentMode(gameDeviceName)),
                "X14: the fake device is back at the saved desktop mode");
            checklist.Check(!proxy.GetVibranceInfo().isResolutionChangeApplied,
                "X14: isResolutionChangeApplied is cleared once the restore lands");

            checklist.Lines.Add(string.Empty);
        }

        // X15 (AMD). The X13 equivalent.
        private static void CheckAmdExitRestoreFailureLeavesFlagSet(Checklist checklist)
        {
            checklist.Lines.Add("X15 (AMD): a RestoreResolutionOnExit attempt that fails against the driver returns Failed and leaves isResolutionChangeApplied true:");
            ResolutionHelper.ResetForTests();
            VibranceRestoreHelper.ResetForTests();

            Screen currentScreen = Screen.FromHandle(GetDesktopWindow());
            string gameDeviceName = currentScreen.DeviceName;

            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(gameDeviceName, BuildDevmode(2560, 1440, 32, 144, 0));
            ResolutionModeWrapper desktopTarget = BuildTarget(1920, 1080, 32, 60, 0);
            device.QueueResult(gameDeviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadflags);
            device.QueueResult(gameDeviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadflags);
            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> settings =
                BuildExitSettings(gameDeviceName, desktopTarget);

            AmdDynamicVibranceProxy proxy = new AmdDynamicVibranceProxy(new FakeAmdAdapter(), new List<ApplicationSetting>(), settings);
            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.neverChangeResolution = false;
            vibranceInfo.isResolutionChangeApplied = true;
            SetAmdVibranceInfo(proxy, vibranceInfo);
            SetAmdGameScreen(currentScreen);

            ResolutionHelper.ExitRestoreResult result = proxy.RestoreResolutionOnExit(device);

            checklist.Check(result == ResolutionHelper.ExitRestoreResult.Failed,
                string.Format("X15: returns Failed, got {0}", result));
            checklist.Check(proxy.GetVibranceInfo().isResolutionChangeApplied,
                "X15: isResolutionChangeApplied is still true after a failed restore");

            checklist.Lines.Add(string.Empty);
        }

        // X16. Second-monitor isolation - RestoreOnExit touches ONLY the game's own device, never
        // sweeps every key in windowsResolutionSettings restoring anything that differs from its
        // saved mode. This matters beyond "that's just what the guard order does" - the architect
        // explicitly rejected a sweeping design: while a resolution change is applied,
        // preserveCapturedMode pins Item1 for EVERY device, not just the game's own (see
        // WindowsResolutionRefresher.Refresh's own "single most dangerous line" comment), so a sweep
        // would drag a second monitor the user hand-reconfigured mid-session back to whatever stale
        // mode happened to be pinned for it. Nothing else in this file pins that rejection - without
        // this check, a future refactor could reintroduce the sweep and every other X-case here
        // would still pass, since none of them populate the dictionary with a second device at all.
        // The other device's saved entry is deliberately built to DIFFER from its own live mode
        // (never the reverse) - if it matched, IsResolutionChangeNeeded would report "nothing to do"
        // regardless of whether a sweep bug existed, masking the very defect this exists to catch,
        // the same class of false-negative Phase A's stale queue used to hide in
        // CheckNoRepeatNotification above.
        private static void CheckExitRestoreNeverTouchesOtherDevices(Checklist checklist)
        {
            checklist.Lines.Add("X16: RestoreOnExit touches only the game's own device - a second monitor's hand-set mode, deliberately recorded as needing a change of its own, is left byte-for-byte untouched and never reaches ChangeMode at all:");
            ResolutionHelper.ResetForTests();

            const string gameDeviceName = "FAKE-EXIT-X16-GAME";
            const string otherDeviceName = "FAKE-EXIT-X16-OTHER";

            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(gameDeviceName, BuildDevmode(2560, 1440, 32, 144, 0));
            ResolutionModeWrapper gameDesktopTarget = BuildTarget(1920, 1080, 32, 60, 0);

            // The other monitor's own live mode - what the user actually set it to, by hand, mid
            // session - and its dictionary entry, deliberately a DIFFERENT mode, standing in for
            // whatever Item1 a preserveCapturedMode-pinned refresh left behind while the game's own
            // change was outstanding. If a sweep existed, this mismatch is exactly what would make it
            // fire.
            Devmode otherLiveMode = BuildDevmode(3840, 2160, 32, 120, 0);
            device.SetCurrentMode(otherDeviceName, otherLiveMode);
            ResolutionModeWrapper otherSavedTarget = BuildTarget(1920, 1080, 32, 60, 0);

            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> settings =
                new Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>>();
            settings[gameDeviceName] = new Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>(gameDesktopTarget, new List<ResolutionModeWrapper>());
            settings[otherDeviceName] = new Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>(otherSavedTarget, new List<ResolutionModeWrapper>());

            ResolutionHelper.ExitRestoreResult result = ResolutionHelper.RestoreOnExit(device, settings, gameDeviceName, false, true);

            checklist.Check(result == ResolutionHelper.ExitRestoreResult.Restored,
                string.Format("X16: the game's own device is still restored, got {0}", result));
            checklist.Check(gameDesktopTarget.MatchesAchievedMode(device.GetCurrentMode(gameDeviceName)),
                "X16: the game's own device is genuinely back at its saved mode");
            checklist.Check(otherLiveMode.Equals(device.GetCurrentMode(otherDeviceName)),
                "X16: the other device's live mode is byte-for-byte unchanged, even though its own dictionary entry differs from it");
            checklist.Check(!device.CallLog.Any(call => call.DeviceName == otherDeviceName),
                "X16: zero ChangeMode calls ever name the other device");

            checklist.Lines.Add(string.Empty);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        // ------------------------------------------------------------------
        // WindowsResolutionRefresher.Refresh coverage (R1-R8/R-A).
        // ------------------------------------------------------------------

        // Runs one Refresh call and folds the R-safety assertion into it: a refresh - any flag
        // combination, any scenario - must never call ChangeMode, in either direction. Measured as
        // a delta around this one call (not the whole check), so a check that also drives a real
        // ChangeResolutionEx apply/revert directly (R1, R7) still gets a meaningful, narrow
        // assertion instead of one that would trivially fail for an unrelated reason.
        private static void RunRefresh(Checklist checklist, FakeDisplayModeDevice device, string safetyDescription,
            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> windowsResolutionSettings,
            Dictionary<string, ResolutionModeWrapper> lastKnownWindowsModes,
            IList<string> attachedDeviceNames, bool preserveCapturedMode, bool reportUnreadableDevices, Action<string> onUnreadableDevice)
        {
            int callsBefore = device.CallLog.Count;
            WindowsResolutionRefresher.Refresh(device, windowsResolutionSettings, lastKnownWindowsModes, attachedDeviceNames,
                preserveCapturedMode, reportUnreadableDevices, onUnreadableDevice);
            checklist.Check(device.CallLog.Count == callsBefore, safetyDescription);
        }

        // R1. The revert path compares against dict[deviceName].Item1, so this is the check that
        // directly catches "the refresh captured the GAME's mode instead of the desktop's" - the
        // exact danger WindowsResolutionRefresher.Refresh's own top-of-method comment describes. A
        // REAL ChangeResolutionEx apply (not a hand-built Devmode) puts the game's mode live first,
        // so this exercises the actual interleaving: a refresh landing while a resolution change
        // is genuinely in effect.
        private static void CheckRefreshPreservesCapturedModeWhileApplied(Checklist checklist)
        {
            checklist.Lines.Add("A refresh with preserveCapturedMode true leaves Item1 at the desktop mode while a real apply is live, IsResolutionChangeNeeded against it is still true, and a revert driven from it actually restores the desktop - not a no-op that still reports success (regression test for the single most dangerous line in the resolution-change fix):");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-REFRESH-R1";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            Devmode desktop = BuildDevmode(1920, 1080, 32, 60, 0);
            device.SetCurrentMode(deviceName, desktop);

            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> dict =
                new Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>>();
            Dictionary<string, ResolutionModeWrapper> lastKnown = new Dictionary<string, ResolutionModeWrapper>();
            List<string> attached = new List<string> { deviceName };
            List<string> unreadable = new List<string>();
            Action<string> onUnreadable = delegate(string name) { unreadable.Add(name); };

            RunRefresh(checklist, device, "the initial refresh never touches the driver",
                dict, lastKnown, attached, false, true, onUnreadable);
            checklist.Check(dict.ContainsKey(deviceName) && dict[deviceName].Item1.Equals(desktop),
                "the initial refresh captures the desktop mode into Item1");

            ResolutionModeWrapper gameTarget = BuildTarget(2560, 1440, 32, 144, 0);
            ResolutionHelper.ResolutionChangeResult applyResult = ResolutionHelper.ChangeResolutionEx(device, gameTarget, deviceName, false);
            checklist.Check(applyResult == ResolutionHelper.ResolutionChangeResult.Applied,
                string.Format("the real apply that puts the game's mode live returns Applied, got {0}", applyResult));

            RunRefresh(checklist, device, "the refresh while the apply is live never touches the driver",
                dict, lastKnown, attached, true, true, onUnreadable);

            checklist.Check(dict[deviceName].Item1.Equals(desktop),
                "Item1 is still the desktop mode after a refresh with preserveCapturedMode true - NOT the game's mode the device is actually showing");
            checklist.Check(ResolutionHelper.IsResolutionChangeNeeded(device, deviceName, dict[deviceName].Item1),
                "IsResolutionChangeNeeded against dict[deviceName].Item1 is still true - the game's mode is still live and differs from the captured desktop mode");

            ResolutionHelper.ResolutionChangeResult revertResult = ResolutionHelper.ChangeResolutionEx(device, dict[deviceName].Item1, deviceName, true);
            checklist.Check(revertResult == ResolutionHelper.ResolutionChangeResult.Applied,
                string.Format("a revert driven from dict[deviceName].Item1 returns Applied, not a no-op success, got {0}", revertResult));
            checklist.Check(dict[deviceName].Item1.MatchesAchievedMode(device.GetCurrentMode(deviceName)),
                "the fake's current mode is genuinely back at the captured desktop mode after the revert - this is the assertion that catches a revert that silently became a no-op");

            checklist.Check(unreadable.Count == 0, "no unreadable-device callback fired across either refresh");

            checklist.Lines.Add(string.Empty);
        }

        // R2. With preserveCapturedMode false (no game resolution change outstanding), a refresh
        // re-captures Item1 from a live read every time - picking up a mode the user (or Windows
        // itself) changed directly, outside of vibranceGUI, exactly as the constructor's own
        // OnDisplaySettingsChanged-driven refreshes are meant to.
        private static void CheckRefreshWithNoChangeAppliedRecapturesBoth(Checklist checklist)
        {
            checklist.Lines.Add("A refresh with preserveCapturedMode false re-captures Item1 from a live read, picking up a mode changed directly in Windows outside of vibranceGUI:");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-REFRESH-R2";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            Devmode original = BuildDevmode(1920, 1080, 32, 60, 0);
            device.SetCurrentMode(deviceName, original);

            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> dict =
                new Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>>();
            Dictionary<string, ResolutionModeWrapper> lastKnown = new Dictionary<string, ResolutionModeWrapper>();
            List<string> attached = new List<string> { deviceName };
            List<string> unreadable = new List<string>();
            Action<string> onUnreadable = delegate(string name) { unreadable.Add(name); };

            RunRefresh(checklist, device, "the initial refresh never touches the driver",
                dict, lastKnown, attached, false, true, onUnreadable);
            checklist.Check(dict[deviceName].Item1.Equals(original),
                "the initial refresh captures the original mode into Item1");

            Devmode changedByUser = BuildDevmode(3840, 2160, 32, 60, 0);
            device.SetCurrentMode(deviceName, changedByUser);

            RunRefresh(checklist, device, "the second refresh never touches the driver",
                dict, lastKnown, attached, false, true, onUnreadable);

            checklist.Check(dict[deviceName].Item1.Equals(changedByUser),
                "Item1 becomes the newly live mode after a refresh with preserveCapturedMode false");
            checklist.Check(unreadable.Count == 0, "no unreadable-device callback fired across either refresh");

            checklist.Lines.Add(string.Empty);
        }

        // R3. A device absent from both windowsResolutionSettings AND lastKnownWindowsModes gets a
        // fresh live Item1 and a fresh Item2 even when preserveCapturedMode is true - it cannot be
        // the screen currently running the game, since that one is already recorded (see the
        // extraction's own top-of-method comment).
        private static void CheckRefreshCapturesNewDeviceRegardlessOfFlag(Checklist checklist)
        {
            checklist.Lines.Add("A device absent from both the dictionary and the last-known map still gets a fresh live Item1 and a fresh Item2 with preserveCapturedMode true - it cannot be the screen currently running the game, since that one is already recorded:");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-REFRESH-R3";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            Devmode live = BuildDevmode(2560, 1440, 32, 144, 0);
            device.SetCurrentMode(deviceName, live);
            device.SetSupportedModes(deviceName, new List<Devmode> { live, BuildDevmode(1920, 1080, 32, 60, 0) });

            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> dict =
                new Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>>();
            Dictionary<string, ResolutionModeWrapper> lastKnown = new Dictionary<string, ResolutionModeWrapper>();
            List<string> attached = new List<string> { deviceName };
            List<string> unreadable = new List<string>();
            Action<string> onUnreadable = delegate(string name) { unreadable.Add(name); };

            RunRefresh(checklist, device, "the refresh never touches the driver",
                dict, lastKnown, attached, true, true, onUnreadable);

            checklist.Check(dict.ContainsKey(deviceName) && dict[deviceName].Item1.Equals(live),
                "a brand new device gets a live-read Item1 even though preserveCapturedMode is true");
            checklist.Check(dict.ContainsKey(deviceName) && dict[deviceName].Item2.Count == 2,
                string.Format("a brand new device gets a freshly enumerated Item2, got {0} entries",
                    dict.ContainsKey(deviceName) ? dict[deviceName].Item2.Count : -1));
            checklist.Check(unreadable.Count == 0, "no unreadable-device callback fired");

            checklist.Lines.Add(string.Empty);
        }

        // R4. Regression test for the frozen-snapshot defect (D58) this whole seam exists to
        // prevent: Refresh must mutate the CALLER'S Dictionary instance in place, not build and
        // hand back a new one - a reference captured before the call (what NVIDIA's static field
        // holds) has to see every later change through that SAME instance.
        private static void CheckRefreshMutatesTheCallersDictionaryInstance(Checklist checklist)
        {
            checklist.Lines.Add("Refresh mutates the caller's Dictionary instance in place - a reference captured before the call (what NVIDIA's static field holds) sees the new Item1 after, and sees a device dropped from attachedDeviceNames disappear, both through that SAME instance (regression test for the frozen-snapshot defect, D58):");
            ResolutionHelper.ResetForTests();

            const string keptDevice = "FAKE-REFRESH-R4-KEPT";
            const string removedDevice = "FAKE-REFRESH-R4-REMOVED";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(keptDevice, BuildDevmode(1920, 1080, 32, 60, 0));
            device.SetCurrentMode(removedDevice, BuildDevmode(1280, 720, 32, 60, 0));

            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> dict =
                new Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>>();
            Dictionary<string, ResolutionModeWrapper> lastKnown = new Dictionary<string, ResolutionModeWrapper>();
            List<string> unreadable = new List<string>();
            Action<string> onUnreadable = delegate(string name) { unreadable.Add(name); };

            RunRefresh(checklist, device, "the first refresh never touches the driver",
                dict, lastKnown, new List<string> { keptDevice, removedDevice }, false, true, onUnreadable);

            // The reference itself - not a copy - is what a real caller holds. Deliberately NOT
            // asserted via ReferenceEquals(capturedReference, dict): that would be true here no
            // matter what Refresh's body does, since windowsResolutionSettings is not a "ref"
            // parameter - the language guarantees capturedReference and dict name the same object
            // regardless of whether the method mutates it, replaces its contents, or does nothing
            // at all. A check built on ReferenceEquals alone here is a check that cannot fail. The
            // assertions below instead look for a VALUE only a real Clear()-then-re-add against the
            // live instance could produce.
            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> capturedReference = dict;

            Devmode keptChanged = BuildDevmode(3840, 2160, 32, 60, 0);
            device.SetCurrentMode(keptDevice, keptChanged);

            RunRefresh(checklist, device, "the second refresh never touches the driver",
                dict, lastKnown, new List<string> { keptDevice }, false, true, onUnreadable);

            checklist.Check(capturedReference.ContainsKey(keptDevice) && capturedReference[keptDevice].Item1.Equals(keptChanged),
                "the reference captured before the second refresh sees the new Item1 through the SAME instance");
            checklist.Check(!capturedReference.ContainsKey(removedDevice),
                "the reference captured before the second refresh sees the removed device disappear through the SAME instance");
            checklist.Check(unreadable.Count == 0, "no unreadable-device callback fired");

            checklist.Lines.Add(string.Empty);
        }

        // R5. _supportedResolutionList (VibranceGUI.cs) is captured ONCE, in the constructor, and
        // is readonly - so, for a device that stays attached, the List<ResolutionModeWrapper>
        // instance a caller captures for Item2 has to stay the exact same instance across every
        // later refresh, any flag, or that field goes silently stale. Also pins that a device
        // already in the dictionary is never re-enumerated. (A device that DETACHES and
        // reattaches is the deliberate exception - see R8, which documents Item2 being
        // re-enumerated from scratch in exactly that case.)
        private static void CheckSupportedResolutionListStaysTheInstanceTheFormCaptured(Checklist checklist)
        {
            checklist.Lines.Add("Item2 stays the SAME List<ResolutionModeWrapper> instance across repeated refreshes with mixed preserveCapturedMode flags, for a device that stays attached, and is never re-enumerated once a device has an entry (regression test for _supportedResolutionList - captured once, readonly - silently going stale):");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-REFRESH-R5";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            Devmode live = BuildDevmode(1920, 1080, 32, 60, 0);
            device.SetCurrentMode(deviceName, live);
            List<Devmode> table = new List<Devmode> { live, BuildDevmode(2560, 1440, 32, 144, 0) };
            device.SetSupportedModes(deviceName, table);

            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> dict =
                new Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>>();
            Dictionary<string, ResolutionModeWrapper> lastKnown = new Dictionary<string, ResolutionModeWrapper>();
            List<string> attached = new List<string> { deviceName };
            List<string> unreadable = new List<string>();
            Action<string> onUnreadable = delegate(string name) { unreadable.Add(name); };

            RunRefresh(checklist, device, "the initial refresh never touches the driver",
                dict, lastKnown, attached, false, true, onUnreadable);

            // Captured exactly the way VibranceGUI's constructor captures _supportedResolutionList:
            // once, right after the initial build, held nowhere else.
            List<ResolutionModeWrapper> capturedList = dict[deviceName].Item2;
            int enumerateCallsAfterFirstRefresh;
            device.EnumerateCallCounts.TryGetValue(deviceName, out enumerateCallsAfterFirstRefresh);

            RunRefresh(checklist, device, "the second refresh (preserve=true) never touches the driver",
                dict, lastKnown, attached, true, true, onUnreadable);
            RunRefresh(checklist, device, "the third refresh (preserve=false) never touches the driver",
                dict, lastKnown, attached, false, true, onUnreadable);
            RunRefresh(checklist, device, "the fourth refresh (preserve=true) never touches the driver",
                dict, lastKnown, attached, true, true, onUnreadable);

            checklist.Check(ReferenceEquals(dict[deviceName].Item2, capturedList),
                "Item2 is still the exact same List<ResolutionModeWrapper> instance after three more refreshes with mixed flags");
            checklist.Check(capturedList.Count == 2,
                string.Format("the captured list is non-empty, got {0} entries", capturedList.Count));

            bool matchesFakeTable = capturedList.Count == table.Count;
            for (int i = 0; matchesFakeTable && i < table.Count; i++)
            {
                matchesFakeTable = capturedList[i].Equals(table[i]);
            }
            checklist.Check(matchesFakeTable, "the captured list's contents equal the fake's supported-mode table");

            int enumerateCallsAfterAll;
            device.EnumerateCallCounts.TryGetValue(deviceName, out enumerateCallsAfterAll);
            checklist.Check(enumerateCallsAfterAll == enumerateCallsAfterFirstRefresh,
                string.Format("EnumerateCallCounts does not increase across the three later refreshes - had {0} calls after the first refresh, {1} after the fourth",
                    enumerateCallsAfterFirstRefresh, enumerateCallsAfterAll));
            checklist.Check(unreadable.Count == 0, "no unreadable-device callback fired across any refresh");

            checklist.Lines.Add(string.Empty);
        }

        // R6. The unreadable-device callback is the seam behind the constructor's one-time
        // MessageBox (showFailureDialog: true) - it must never fire outside of an actually-attempted
        // live read: not when reportUnreadableDevices is false, not for a readable device, and not
        // for a device whose entry is retained via an existing dictionary entry without ever
        // attempting a live read at all (preserveCapturedMode true, hasExisting true) - the OTHER
        // half of the regression this whole fix targets: a hot-plug/resolution-change refresh must
        // never pop the constructor's own dialog (see OnDisplaySettingsChanged, VibranceGUI.cs).
        private static void CheckRefreshNeverReportsUnreadableDeviceOutsideTheInitialBuild(Checklist checklist)
        {
            checklist.Lines.Add("The unreadable-device callback fires only when reportUnreadableDevices is true AND a live read is actually attempted - never when an existing entry means the live read is skipped entirely (regression test for the constructor's one-time dialog leaking onto the SystemEvents refresh path):");
            ResolutionHelper.ResetForTests();

            // Case 1: unreadable device, reportUnreadableDevices false - zero callbacks, no entry.
            {
                const string deviceName = "FAKE-REFRESH-R6-CASE1";
                FakeDisplayModeDevice device = new FakeDisplayModeDevice();
                // Deliberately never calls SetCurrentMode - TryGetCurrentMode fails for it.
                Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> dict =
                    new Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>>();
                Dictionary<string, ResolutionModeWrapper> lastKnown = new Dictionary<string, ResolutionModeWrapper>();
                List<string> unreadable = new List<string>();
                Action<string> onUnreadable = delegate(string name) { unreadable.Add(name); };

                RunRefresh(checklist, device, "case 1's refresh never touches the driver",
                    dict, lastKnown, new List<string> { deviceName }, false, false, onUnreadable);

                checklist.Check(unreadable.Count == 0, "case 1 (reportUnreadableDevices false): zero callbacks");
                checklist.Check(!dict.ContainsKey(deviceName), "case 1: no entry is added for the unreadable device");
            }

            // Case 2: unreadable device, reportUnreadableDevices true - exactly one callback,
            // naming the device.
            {
                const string deviceName = "FAKE-REFRESH-R6-CASE2";
                FakeDisplayModeDevice device = new FakeDisplayModeDevice();
                Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> dict =
                    new Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>>();
                Dictionary<string, ResolutionModeWrapper> lastKnown = new Dictionary<string, ResolutionModeWrapper>();
                List<string> unreadable = new List<string>();
                Action<string> onUnreadable = delegate(string name) { unreadable.Add(name); };

                RunRefresh(checklist, device, "case 2's refresh never touches the driver",
                    dict, lastKnown, new List<string> { deviceName }, false, true, onUnreadable);

                checklist.Check(unreadable.Count == 1 && unreadable[0] == deviceName,
                    string.Format("case 2 (reportUnreadableDevices true): exactly one callback naming {0}, got [{1}]",
                        deviceName, string.Join(",", unreadable.ToArray())));
                checklist.Check(!dict.ContainsKey(deviceName), "case 2: still no entry for a device that could not be read");
            }

            // Case 3: readable device, reportUnreadableDevices true - zero callbacks.
            {
                const string deviceName = "FAKE-REFRESH-R6-CASE3";
                FakeDisplayModeDevice device = new FakeDisplayModeDevice();
                device.SetCurrentMode(deviceName, BuildDevmode(1920, 1080, 32, 60, 0));
                Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> dict =
                    new Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>>();
                Dictionary<string, ResolutionModeWrapper> lastKnown = new Dictionary<string, ResolutionModeWrapper>();
                List<string> unreadable = new List<string>();
                Action<string> onUnreadable = delegate(string name) { unreadable.Add(name); };

                RunRefresh(checklist, device, "case 3's refresh never touches the driver",
                    dict, lastKnown, new List<string> { deviceName }, false, true, onUnreadable);

                checklist.Check(unreadable.Count == 0, "case 3 (a readable device): zero callbacks");
                checklist.Check(dict.ContainsKey(deviceName), "case 3: a readable device still gets its entry");
            }

            // Case 4: a device that already has a dictionary entry, then goes unreadable live, then
            // is refreshed again with preserveCapturedMode true - the existing Item1 is reused
            // without ever attempting a live read, so the entry survives and the callback never
            // fires, even though a live read would fail if it were ever attempted.
            {
                const string deviceName = "FAKE-REFRESH-R6-CASE4";
                FakeDisplayModeDevice device = new FakeDisplayModeDevice();
                Devmode original = BuildDevmode(1920, 1080, 32, 60, 0);
                device.SetCurrentMode(deviceName, original);
                Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> dict =
                    new Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>>();
                Dictionary<string, ResolutionModeWrapper> lastKnown = new Dictionary<string, ResolutionModeWrapper>();
                List<string> unreadable = new List<string>();
                Action<string> onUnreadable = delegate(string name) { unreadable.Add(name); };

                RunRefresh(checklist, device, "case 4's first refresh never touches the driver",
                    dict, lastKnown, new List<string> { deviceName }, false, true, onUnreadable);

                device.SetUnreadable(deviceName);

                RunRefresh(checklist, device, "case 4's second refresh never touches the driver",
                    dict, lastKnown, new List<string> { deviceName }, true, true, onUnreadable);

                checklist.Check(dict.ContainsKey(deviceName) && dict[deviceName].Item1.Equals(original),
                    "case 4: the entry survives with its original Item1, even though a live read would now fail");
                checklist.Check(unreadable.Count == 0,
                    "case 4: zero callbacks - preserveCapturedMode true with an existing entry never attempts the live read at all");
            }

            checklist.Lines.Add(string.Empty);
        }

        // R-A. Documents WindowsResolutionRefresher.Refresh's OWN contract, which the duration-
        // based fix (ResolutionAdoptionDebouncer.cs) deliberately leaves UNCHANGED, and carries the
        // reasoning for why a value-based fix - skip re-capturing Item1 when the live mode matches
        // a configured ApplicationSetting.ResolutionSettings entry - was REJECTED outright, not
        // merely postponed:
        //   1. A user can legitimately configure a game at the mode their desktop already runs at.
        //      That fix would then make their OWN desktop mode permanently un-capturable, and
        //      every future revert would drag them to a stale mode while still reporting success.
        //   2. ResolutionSettings is populated on an ApplicationSetting regardless of whether the
        //      resolution feature is even switched on for that entry, so the fix would also match
        //      entries where it is off.
        //   3. Neither available comparison actually works: Equals includes DmDisplayFixedOutput,
        //      which drivers are free to silently pin away from what was requested on readback
        //      (see ResolutionModeWrapper.MatchesAchievedMode's own comment) - and
        //      MatchesAchievedMode widens the false-positive surface to every mode that merely
        //      shares the four OTHER fields with a configured entry.
        //   4. At refresh time, "a game changed the resolution itself with no profile applied" and
        //      "the user's own genuine desktop mode happens to equal a configured entry" are
        //      OBSERVATIONALLY IDENTICAL: both are a bare DisplaySettingsChanged plus a live mode
        //      that differs from the last capture. Nothing available at refresh time can tell them
        //      apart.
        // These four reasons still bind, and still rule out a value-based fix inside Refresh
        // itself. The fix that landed instead keys on DURATION, not value, and lives ONE LAYER UP
        // from Refresh - in ResolutionAdoptionDebouncer, which decides which DisplaySettingsChanged
        // notifications are even allowed to reach a Refresh call in the first place (see
        // VibranceGUI.OnDisplaySettingsChanged). Refresh, driven directly the way every check in
        // this file drives it, still adopts whatever is live the instant it runs, and still
        // self-heals the moment a foreign mode goes away and another Refresh runs, exactly as
        // this check has always documented - that remains correct and load-bearing: once the
        // debouncer's own countdown finally does let a Refresh call through, Refresh must still
        // capture whatever is live AT THAT LATER MOMENT with no special-casing of its own. What
        // changed is that in production, a foreign mode which never survives DebounceIntervalMs
        // now never gets a Refresh call to adopt it in the first place - see
        // CheckDebounceNeverLetsATransientForeignModeReachAdoption and
        // CheckDebounceAdoptsAGenuineChangeOnceItHasHeldStable below for that end-to-end guarantee,
        // which this check cannot express since it never goes anywhere near
        // ResolutionAdoptionDebouncer.
        private static void CheckAdoptedForeignModeSelfHealsOnceItIsGone(Checklist checklist)
        {
            checklist.Lines.Add("WindowsResolutionRefresher.Refresh itself is unchanged by the duration-based fix: with preserveCapturedMode false, a refresh STILL adopts whatever mode is live the instant it actually runs - even a foreign one set with no profile applied - and still self-heals back once that foreign mode is gone and another refresh runs (see this check's own comment for why a value-based fix inside Refresh was rejected, and why the real fix instead lives one layer up, in ResolutionAdoptionDebouncer, gating which DisplaySettingsChanged notifications ever reach a Refresh call at all):");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-REFRESH-RA";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            Devmode desktop = BuildDevmode(1920, 1080, 32, 60, 0);
            device.SetCurrentMode(deviceName, desktop);

            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> dict =
                new Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>>();
            Dictionary<string, ResolutionModeWrapper> lastKnown = new Dictionary<string, ResolutionModeWrapper>();
            List<string> attached = new List<string> { deviceName };
            List<string> unreadable = new List<string>();
            Action<string> onUnreadable = delegate(string name) { unreadable.Add(name); };

            RunRefresh(checklist, device, "the initial refresh never touches the driver",
                dict, lastKnown, attached, false, true, onUnreadable);
            checklist.Check(dict[deviceName].Item1.Equals(desktop), "the initial refresh captures the desktop mode");

            // A game (or anything else) changed the live mode without going through
            // ChangeResolutionEx at all - in reality SystemEvents would still fire
            // DisplaySettingsChanged for this, driving a refresh with preserveCapturedMode false
            // (isResolutionChangeApplied is only true while vibranceGUI's OWN apply is outstanding).
            Devmode foreign = BuildDevmode(1280, 720, 32, 60, 0);
            device.SetCurrentMode(deviceName, foreign);

            RunRefresh(checklist, device, "the second refresh never touches the driver",
                dict, lastKnown, attached, false, true, onUnreadable);
            checklist.Check(dict[deviceName].Item1.Equals(foreign),
                "Item1 adopts the foreign mode - today's behaviour, deliberately not special-cased against configured ApplicationSetting.ResolutionSettings entries (see this check's own comment)");

            device.SetCurrentMode(deviceName, desktop);

            RunRefresh(checklist, device, "the third refresh never touches the driver",
                dict, lastKnown, attached, false, true, onUnreadable);
            checklist.Check(dict[deviceName].Item1.Equals(desktop),
                "Item1 self-heals back to the desktop mode once the foreign mode is gone");

            checklist.Check(unreadable.Count == 0, "no unreadable-device callback fired across any refresh");

            checklist.Lines.Add(string.Empty);
        }

        // R7 - Task 6, the point of this whole exercise. A device that drops out of
        // attachedDeviceNames mid-apply (a real hot-unplug, a docking-station reattach, a driver
        // resetting the adapter) and reattaches later must keep the desktop mode it had BEFORE it
        // dropped out, even though it spent an intervening refresh completely absent from the
        // dictionary and so has no "existing" entry for the reattach refresh to reuse Item1 from.
        // Without the last-known-mode fallback, the reattach refresh falls through to a live read -
        // which, with the game's own resolution change still applied to this very device, reads
        // the GAME's mode, not the desktop's - exactly the danger
        // WindowsResolutionRefresher.Refresh's own comment describes, just reached through a
        // detach/reattach instead of the simpler "no previous entry at all" case R1 covers. Also
        // makes device A momentarily unreadable right at the reattach refresh, to reach the state
        // that is genuinely new here: attached + live-read-fails + absent from the dictionary +
        // present in lastKnownWindowsModes + preserveCapturedMode true. R6 case 4's unreadable
        // device always has hasExisting true (an existing Item1 to reuse, live read never even
        // attempted for a different reason); this is the only check that reaches the fallback
        // branch itself with a live read that would have failed. The behaviour here is a strict
        // improvement over pre-seam code: that device previously got no entry at all in this
        // situation, so the proxies' ContainsKey guard blocked the revert outright and the desktop
        // stayed at the game's resolution with no self-heal; now the revert can run.
        private static void CheckDetachedDeviceKeepsItsCapturedModeAcrossAReattach(Checklist checklist)
        {
            checklist.Lines.Add("A device that drops out of attachedDeviceNames while a real apply is live, then reattaches, keeps its desktop Item1 via the last-known-mode fallback - even though it spent an intervening refresh completely absent from the dictionary - and a revert through it still restores the desktop; the detached device has zero entry, and zero leakage, while it is gone (regression test for a monitor cable bounce or GPU hot-plug losing the desktop mode of a device a game's resolution change is still applied to):");
            ResolutionHelper.ResetForTests();

            const string deviceA = "FAKE-REFRESH-R7-A";
            const string deviceB = "FAKE-REFRESH-R7-B";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            Devmode desktopA = BuildDevmode(1920, 1080, 32, 60, 0);
            Devmode desktopB = BuildDevmode(2560, 1440, 32, 144, 0);
            device.SetCurrentMode(deviceA, desktopA);
            device.SetCurrentMode(deviceB, desktopB);

            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> dict =
                new Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>>();
            Dictionary<string, ResolutionModeWrapper> lastKnown = new Dictionary<string, ResolutionModeWrapper>();
            List<string> unreadable = new List<string>();
            Action<string> onUnreadable = delegate(string name) { unreadable.Add(name); };

            RunRefresh(checklist, device, "the initial refresh never touches the driver",
                dict, lastKnown, new List<string> { deviceA, deviceB }, false, true, onUnreadable);

            ResolutionModeWrapper gameTarget = BuildTarget(3840, 2160, 32, 60, 0);
            ResolutionHelper.ResolutionChangeResult applyResult = ResolutionHelper.ChangeResolutionEx(device, gameTarget, deviceA, false);
            checklist.Check(applyResult == ResolutionHelper.ResolutionChangeResult.Applied,
                string.Format("the real apply on device A returns Applied, got {0}", applyResult));

            // Device A's link drops - a real hot-unplug/dock event, not the game exiting - so the
            // next refresh's attachedDeviceNames omits it entirely, exactly the way
            // Screen.AllScreens would after Windows stops reporting the monitor.
            RunRefresh(checklist, device, "the refresh with A detached never touches the driver",
                dict, lastKnown, new List<string> { deviceB }, true, true, onUnreadable);
            checklist.Check(!dict.ContainsKey(deviceA),
                "device A has no entry at all while detached - retention must never leak a detached device into the proxies' own view of windowsResolutionSettings");

            // Device A reattaches - still not through ChangeResolutionEx, so this exercises the
            // refresher's own retention, not anything ChangeResolutionEx tracks. Also made
            // unreadable right up to the moment of this refresh - the genuinely new reachable state
            // this fallback introduces is attached + live-read-fails + absent from the dictionary +
            // present in lastKnownWindowsModes + preserveCapturedMode true, which nothing else in
            // this fixture (R6 case 4 has hasExisting true; every other unreadable-device case has
            // no retained mode to fall back to) reaches. Restored to readable immediately after the
            // refresh - this fixture models the live read failing at the moment of THIS refresh, not
            // the device staying broken forever, and the revert below still needs a readable device.
            Devmode currentModeBeforeUnreadable = device.GetCurrentMode(deviceA);
            device.SetUnreadable(deviceA);

            RunRefresh(checklist, device, "the refresh with A reattached (and momentarily unreadable) never touches the driver",
                dict, lastKnown, new List<string> { deviceA, deviceB }, true, true, onUnreadable);

            device.SetCurrentMode(deviceA, currentModeBeforeUnreadable);

            checklist.Check(dict.ContainsKey(deviceA) && dict[deviceA].Item1.Equals(desktopA),
                "device A's Item1 is still the desktop mode after reattaching - NOT the game's mode it is actually showing right now, and retained even though a live read at that exact moment would have failed");
            checklist.Check(unreadable.Count == 0,
                "the retained mode satisfies capturedMode before a live read is ever attempted, so the unreadable-device callback never fires for this refresh either");
            checklist.Check(ResolutionHelper.IsResolutionChangeNeeded(device, deviceA, dict[deviceA].Item1),
                "IsResolutionChangeNeeded against the retained Item1 is still true - the game's mode is still live on device A");

            ResolutionHelper.ResolutionChangeResult revertResult = ResolutionHelper.ChangeResolutionEx(device, dict[deviceA].Item1, deviceA, true);
            checklist.Check(revertResult == ResolutionHelper.ResolutionChangeResult.Applied,
                string.Format("a revert driven from the retained Item1 returns Applied, got {0}", revertResult));
            checklist.Check(dict[deviceA].Item1.MatchesAchievedMode(device.GetCurrentMode(deviceA)),
                "the fake is genuinely back at device A's desktop mode after the revert - this is the assertion that catches a revert that silently became a no-op");

            checklist.Check(unreadable.Count == 0, "no unreadable-device callback fired across any refresh");

            checklist.Lines.Add(string.Empty);
        }

        // R8 - Task 6. The retained mode is the ONLY thing carried across a detach/reattach cycle -
        // the supported-mode list is not, and cannot be: a detached device has no dictionary entry
        // at all (R7), so there is no old List<ResolutionModeWrapper> instance left for the
        // reattach refresh to reuse, and Item2 has to be re-enumerated from scratch, picking up
        // whatever the device reports now (a real reattach through a different port/adapter, or
        // after a driver update, can genuinely offer a different mode list than before). Also pins
        // that the fallback is gated strictly on preserveCapturedMode: with it false, a reattached
        // device's Item1 is read fresh, never pulled from the retained last-known mode.
        private static void CheckReattachedDeviceReenumeratesItsSupportedModes(Checklist checklist)
        {
            checklist.Lines.Add("A device that reattaches after detaching gets its Item2 re-enumerated from scratch, reflecting a changed supported-mode table, with EnumerateCallCounts increasing accordingly; with preserveCapturedMode false the reattached device's Item1 is read fresh, never pulled from the retained last-known mode:");
            ResolutionHelper.ResetForTests();

            const string deviceA = "FAKE-REFRESH-R8-A";
            const string deviceB = "FAKE-REFRESH-R8-B";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            Devmode originalModeA = BuildDevmode(1920, 1080, 32, 60, 0);
            device.SetCurrentMode(deviceA, originalModeA);
            device.SetCurrentMode(deviceB, BuildDevmode(2560, 1440, 32, 144, 0));
            List<Devmode> originalTableA = new List<Devmode> { originalModeA };
            device.SetSupportedModes(deviceA, originalTableA);

            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> dict =
                new Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>>();
            Dictionary<string, ResolutionModeWrapper> lastKnown = new Dictionary<string, ResolutionModeWrapper>();
            List<string> unreadable = new List<string>();
            Action<string> onUnreadable = delegate(string name) { unreadable.Add(name); };

            RunRefresh(checklist, device, "the initial refresh never touches the driver",
                dict, lastKnown, new List<string> { deviceA, deviceB }, false, true, onUnreadable);
            int enumerateCallsBeforeDetach;
            device.EnumerateCallCounts.TryGetValue(deviceA, out enumerateCallsBeforeDetach);

            RunRefresh(checklist, device, "the refresh with A detached never touches the driver",
                dict, lastKnown, new List<string> { deviceB }, true, true, onUnreadable);

            // The driver's own supported-mode table AND live mode for A change while it is
            // detached.
            List<Devmode> newTableA = new List<Devmode> { originalModeA, BuildDevmode(3840, 2160, 32, 60, 0) };
            device.SetSupportedModes(deviceA, newTableA);
            Devmode liveModeAOnReattach = BuildDevmode(1280, 720, 32, 60, 0);
            device.SetCurrentMode(deviceA, liveModeAOnReattach);

            RunRefresh(checklist, device, "the refresh with A reattached (preserve=false) never touches the driver",
                dict, lastKnown, new List<string> { deviceA, deviceB }, false, true, onUnreadable);

            checklist.Check(dict.ContainsKey(deviceA) && dict[deviceA].Item2.Count == newTableA.Count,
                string.Format("device A's Item2 reflects the new table after reattaching, got {0} entries, expected {1}",
                    dict.ContainsKey(deviceA) ? dict[deviceA].Item2.Count : -1, newTableA.Count));
            int enumerateCallsAfterReattach;
            device.EnumerateCallCounts.TryGetValue(deviceA, out enumerateCallsAfterReattach);
            checklist.Check(enumerateCallsAfterReattach > enumerateCallsBeforeDetach,
                string.Format("EnumerateCallCounts[A] increased after reattaching - {0} calls before detaching, {1} after reattaching",
                    enumerateCallsBeforeDetach, enumerateCallsAfterReattach));
            checklist.Check(dict[deviceA].Item1.Equals(liveModeAOnReattach),
                "with preserveCapturedMode false, the reattached device's Item1 is read fresh from the live device, not pulled from the retained last-known mode");

            checklist.Check(unreadable.Count == 0, "no unreadable-device callback fired across any refresh");

            checklist.Lines.Add(string.Empty);
        }

        // Regression test for a review finding on this seam (N5): a device whose CURRENT MODE is
        // perfectly readable but whose SUPPORTED-MODE enumeration happens to return nothing on one
        // refresh - EnumDisplaySettings failing partway through, or simply not yet ready - gets an
        // entry with a correct Item1 and an EMPTY Item2. Because Item2 is then reused unconditionally
        // for any device the dictionary already has an entry for, that empty list would otherwise
        // stick for the rest of the session: every later refresh keeps reusing the SAME empty
        // instance, and both proxies gate a resolution apply on Item2.Contains(target)
        // (NvidiaDynamicVibranceProxy.cs, AmdDynamicVibranceProxy.cs), so resolution switching for
        // that device silently stops working until the process restarts or the device detaches and
        // reattaches (R8). The revert path is unaffected - it only reads Item1 - so this fails safe,
        // never stranding the desktop; it's an apply-side regression, not a revert-side one.
        private static void CheckStickyEmptySupportedListHealsOnNextSuccessfulEnumeration(Checklist checklist)
        {
            checklist.Lines.Add("A device whose supported-mode enumeration comes back empty on one refresh - its current mode was still perfectly readable - heals the moment a later refresh's enumeration succeeds, rather than reusing that empty Item2 for the rest of the session (regression test for N5 - an empty Item2 silently and permanently disabling resolution switching for an otherwise-healthy device):");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-REFRESH-N5";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            Devmode live = BuildDevmode(1920, 1080, 32, 60, 0);
            device.SetCurrentMode(deviceName, live);
            // Deliberately never calls SetSupportedModes before the first refresh -
            // TryEnumerateMode returns false on its very first call, exactly like
            // EnumDisplaySettings failing for this device's mode enumeration at that moment (its
            // CURRENT mode, set above, is unaffected - a real EnumDisplaySettings(deviceName, -1, ...)
            // and EnumDisplaySettings(deviceName, 0, ...) can fail independently of each other).
            // EnumerateSupportedResolutionModes returns an empty list for this.

            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> dict =
                new Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>>();
            Dictionary<string, ResolutionModeWrapper> lastKnown = new Dictionary<string, ResolutionModeWrapper>();
            List<string> attached = new List<string> { deviceName };
            List<string> unreadable = new List<string>();
            Action<string> onUnreadable = delegate(string name) { unreadable.Add(name); };

            RunRefresh(checklist, device, "the initial refresh never touches the driver",
                dict, lastKnown, attached, false, true, onUnreadable);
            checklist.Check(dict.ContainsKey(deviceName) && dict[deviceName].Item2.Count == 0,
                "the initial refresh's Item2 is empty - the enumeration genuinely returned nothing this one time, setting up the scenario");

            // The device recovers - a later EnumDisplaySettings-style enumeration would succeed now.
            List<Devmode> table = new List<Devmode> { live, BuildDevmode(2560, 1440, 32, 144, 0) };
            device.SetSupportedModes(deviceName, table);

            RunRefresh(checklist, device, "the second refresh never touches the driver",
                dict, lastKnown, attached, false, true, onUnreadable);

            checklist.Check(dict.ContainsKey(deviceName) && dict[deviceName].Item2.Count == table.Count,
                string.Format("Item2 heals to the newly-enumerated, non-empty table once the device recovers, got {0} entries, expected {1} - an empty Item2 must never stick for the rest of the session",
                    dict.ContainsKey(deviceName) ? dict[deviceName].Item2.Count : -1, table.Count));

            int enumerateCallsAfterHealing;
            device.EnumerateCallCounts.TryGetValue(deviceName, out enumerateCallsAfterHealing);

            // A third refresh, with the table now non-empty, must go back to reusing the SAME
            // instance rather than re-enumerating forever - the empty-Item2 exception is not a
            // general "always re-enumerate" regression of its own.
            RunRefresh(checklist, device, "the third refresh never touches the driver",
                dict, lastKnown, attached, true, true, onUnreadable);

            List<ResolutionModeWrapper> healedList = dict[deviceName].Item2;
            int enumerateCallsAfterThirdRefresh;
            device.EnumerateCallCounts.TryGetValue(deviceName, out enumerateCallsAfterThirdRefresh);
            checklist.Check(enumerateCallsAfterThirdRefresh == enumerateCallsAfterHealing,
                string.Format("once Item2 is non-empty, a later refresh goes back to reusing it rather than re-enumerating - had {0} calls right after healing, {1} after one more refresh",
                    enumerateCallsAfterHealing, enumerateCallsAfterThirdRefresh));
            checklist.Check(ReferenceEquals(dict[deviceName].Item2, healedList),
                "the healed, non-empty Item2 is then reused as the SAME instance, exactly like any other non-empty Item2 (R5)");

            checklist.Check(unreadable.Count == 0,
                "no unreadable-device callback fired across any refresh - the device's CURRENT MODE was always readable; only its supported-mode enumeration was ever empty");

            checklist.Lines.Add(string.Empty);
        }

        // ------------------------------------------------------------------
        // ResolutionAdoptionDebouncer coverage (see ResolutionAdoptionDebouncer.cs and R-A's own
        // comment, above CheckAdoptedForeignModeSelfHealsOnceItIsGone, for the full reasoning).
        // FakeResolutionAdoptionTimer below fires synchronously on command - Elapse() - so, like
        // every check above it, none of these ever sleep in real time.
        // ------------------------------------------------------------------

        // The basic shape: preserveCapturedMode false arms a countdown instead of running its
        // callback immediately, and the callback runs exactly once that countdown elapses.
        private static void CheckDebounceDelaysAdoptionUntilTheIntervalElapses(Checklist checklist)
        {
            checklist.Lines.Add("ResolutionAdoptionDebouncer.OnDisplaySettingsChanged(preserveCapturedMode: false, ...) arms a countdown instead of running its callback immediately, and the callback runs exactly once the countdown elapses:");

            FakeResolutionAdoptionTimer timer = new FakeResolutionAdoptionTimer();
            ResolutionAdoptionDebouncer debouncer = new ResolutionAdoptionDebouncer(timer);
            int refreshCallCount = 0;
            Action refresh = delegate { refreshCallCount++; };

            debouncer.OnDisplaySettingsChanged(false, refresh);

            checklist.Check(timer.RestartCallCount == 1,
                string.Format("Restart is called exactly once, got {0}", timer.RestartCallCount));
            checklist.Check(refreshCallCount == 0,
                "the refresh callback has not run yet - preserveCapturedMode false never runs it synchronously");
            checklist.Check(timer.LastDelayMs == ResolutionAdoptionDebouncer.DebounceIntervalMs,
                string.Format("Restart is armed with the class' own DebounceIntervalMs ({0}ms), got {1}ms",
                    ResolutionAdoptionDebouncer.DebounceIntervalMs, timer.LastDelayMs));

            timer.Elapse();

            checklist.Check(refreshCallCount == 1,
                string.Format("the refresh callback runs exactly once the countdown elapses, got {0} calls", refreshCallCount));

            checklist.Lines.Add(string.Empty);
        }

        // A game switching between two resolutions of its own, or a user cycling through several
        // desktop resolutions in a row, must reset the clock rather than stack a second pending
        // refresh alongside the first - see ResolutionAdoptionDebouncer.OnDisplaySettingsChanged's
        // own "Restart, not start only if nothing is pending" comment.
        private static void CheckDebounceRestartCollapsesRepeatedChangesIntoOneRefresh(Checklist checklist)
        {
            checklist.Lines.Add("A second (and third) DisplaySettingsChanged arriving before the first countdown elapses restarts the clock rather than stacking a second pending refresh - only one refresh call ever runs, no matter how many notifications fire while nothing has elapsed yet:");

            FakeResolutionAdoptionTimer timer = new FakeResolutionAdoptionTimer();
            ResolutionAdoptionDebouncer debouncer = new ResolutionAdoptionDebouncer(timer);
            int refreshCallCount = 0;
            Action refresh = delegate { refreshCallCount++; };

            debouncer.OnDisplaySettingsChanged(false, refresh);
            debouncer.OnDisplaySettingsChanged(false, refresh);
            debouncer.OnDisplaySettingsChanged(false, refresh);

            checklist.Check(timer.RestartCallCount == 3,
                string.Format("Restart is called once per notification (3), got {0}", timer.RestartCallCount));
            checklist.Check(refreshCallCount == 0, "no refresh has run yet after three notifications, none elapsed");

            timer.Elapse();

            checklist.Check(refreshCallCount == 1,
                string.Format("elapsing the single, repeatedly-restarted countdown runs the refresh exactly once, got {0}", refreshCallCount));

            checklist.Lines.Add(string.Empty);
        }

        // Pins a contract of IResolutionAdoptionTimer itself, not just of ResolutionAdoptionDebouncer's
        // own logic: a callback invoked from Elapse() may call OnDisplaySettingsChanged again -
        // re-arming the SAME timer instance from inside its own elapse - and that re-arm must
        // survive, not be silently wiped by whatever cleanup Elapse() still does after invoking the
        // callback. FakeResolutionAdoptionTimer.Elapse() models this by clearing its pending
        // callback BEFORE invoking it (see that class' own comment), exactly the ordering
        // FormsResolutionAdoptionTimer.OnTick uses for the same reason, on the real
        // System.Windows.Forms.Timer - see that method's own comment for why nothing in this
        // codebase can pin this ordering on the REAL timer the way this check pins it on the fake.
        private static void CheckDebounceCallbackMayReArmDuringItsOwnElapse(Checklist checklist)
        {
            checklist.Lines.Add("A callback invoked from Elapse() may itself call OnDisplaySettingsChanged again, re-arming the timer from inside its own elapse - and that re-arm survives Elapse() returning, rather than being wiped by cleanup Elapse() still performs after invoking the callback:");

            FakeResolutionAdoptionTimer timer = new FakeResolutionAdoptionTimer();
            ResolutionAdoptionDebouncer debouncer = new ResolutionAdoptionDebouncer(timer);
            int refreshCallCount = 0;
            Action refresh = null;
            refresh = delegate
            {
                refreshCallCount++;
                // Re-arms from inside its own elapse - this is the scenario this check exists for.
                debouncer.OnDisplaySettingsChanged(false, refresh);
            };

            debouncer.OnDisplaySettingsChanged(false, refresh);
            timer.Elapse();

            checklist.Check(refreshCallCount == 1,
                string.Format("the callback runs exactly once from this single Elapse() call, got {0}", refreshCallCount));
            checklist.Check(timer.IsPending,
                "the re-arm made from inside the callback survives - a new countdown is pending immediately after Elapse() returns, not wiped by Elapse()'s own post-invoke cleanup");

            checklist.Lines.Add(string.Empty);
        }

        // The end-to-end guarantee R-A's own check cannot express, since it drives
        // WindowsResolutionRefresher.Refresh directly and never goes anywhere near
        // ResolutionAdoptionDebouncer: a foreign mode set with no profile applied, that reverts
        // again before the debounce interval elapses (a game's own transient resolution change),
        // never reaches Refresh at all - Item1 is never overwritten with it, not even
        // momentarily, because Refresh is simply never called while the foreign mode is live.
        private static void CheckDebounceNeverLetsATransientForeignModeReachAdoption(Checklist checklist)
        {
            checklist.Lines.Add("A foreign mode set with no profile applied, that reverts again before the debounce interval elapses (a game's own transient resolution change), never reaches WindowsResolutionRefresher.Refresh at all - Item1 is never overwritten with it, not even momentarily (regression test for the defect this branch fixes):");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-DEBOUNCE-TRANSIENT";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            Devmode desktop = BuildDevmode(1920, 1080, 32, 60, 0);
            device.SetCurrentMode(deviceName, desktop);

            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> dict =
                new Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>>();
            Dictionary<string, ResolutionModeWrapper> lastKnown = new Dictionary<string, ResolutionModeWrapper>();
            List<string> attached = new List<string> { deviceName };

            FakeResolutionAdoptionTimer timer = new FakeResolutionAdoptionTimer();
            ResolutionAdoptionDebouncer debouncer = new ResolutionAdoptionDebouncer(timer);
            int refreshCallCount = 0;
            Action refresh = delegate
            {
                refreshCallCount++;
                WindowsResolutionRefresher.Refresh(device, dict, lastKnown, attached, false, true, null);
            };

            // The initial capture is deliberately NOT routed through the debouncer - exactly like
            // VibranceGUI's own constructor, which calls RebuildWindowsResolutionSettings(true)
            // directly, undebounced: a brand new device has no prior capture to protect, so there
            // is nothing yet for a countdown to guard against.
            refresh();
            checklist.Check(dict[deviceName].Item1.Equals(desktop), "the initial (undebounced) capture gets the desktop mode");

            // A game (or anything else) changes the live mode with no profile applied - in reality
            // this fires SystemEvents.DisplaySettingsChanged, routed through the debouncer exactly
            // as VibranceGUI.OnDisplaySettingsChanged does.
            Devmode foreign = BuildDevmode(1280, 720, 32, 60, 0);
            device.SetCurrentMode(deviceName, foreign);
            debouncer.OnDisplaySettingsChanged(false, refresh);

            checklist.Check(dict[deviceName].Item1.Equals(desktop),
                "Item1 is untouched immediately after the foreign mode appears - the countdown has not elapsed yet");
            checklist.Check(refreshCallCount == 1, "Refresh has not run a second time yet");

            // The foreign mode goes away again - the game exited fullscreen, or exited outright -
            // before the debounce interval elapsed. This also fires DisplaySettingsChanged.
            device.SetCurrentMode(deviceName, desktop);
            debouncer.OnDisplaySettingsChanged(false, refresh);

            checklist.Check(dict[deviceName].Item1.Equals(desktop), "Item1 is still untouched - still nothing has elapsed");
            checklist.Check(refreshCallCount == 1, "still no second Refresh call - the second notification only restarted the countdown");

            // Only now does the countdown elapse - and by now the live mode is back to the
            // desktop's own, never the foreign one.
            timer.Elapse();

            checklist.Check(refreshCallCount == 2,
                string.Format("elapsing runs Refresh exactly once more, got {0} total calls", refreshCallCount));
            checklist.Check(dict[deviceName].Item1.Equals(desktop),
                "Item1 is (re)captured as the desktop mode - the foreign mode was NEVER adopted, not even momentarily, because Refresh never ran while it was live");
            checklist.Check(device.CallLog.Count == 0, "none of this ever touches the driver - every call above is a live-mode read, never a ChangeMode");

            checklist.Lines.Add(string.Empty);
        }

        // The other half of the same guarantee: a genuine, LASTING desktop change - the user
        // actually changing their resolution, with nothing reverting it before the interval
        // elapses - IS adopted, once it has proven itself stable for the full debounce interval.
        // Reason 4 in R-A's own comment is exactly why this has to work this way: at notification
        // time there is nothing to distinguish this case from the transient one
        // CheckDebounceNeverLetsATransientForeignModeReachAdoption covers - only elapsed stability
        // does. "Adopt late, never refuse" (this fixture's own file header reasoning, and the
        // asymmetry the task that produced this file was built around) is why a brief delay before
        // Item1 catches up, rather than never adopting at all, is the accepted cost.
        private static void CheckDebounceAdoptsAGenuineChangeOnceItHasHeldStable(Checklist checklist)
        {
            checklist.Lines.Add("A genuine desktop resolution change that is still live once the debounce interval elapses IS adopted into Item1, even though - immediately after the change, before the interval elapses - it reads exactly like the transient case above:");
            ResolutionHelper.ResetForTests();

            const string deviceName = "FAKE-DEBOUNCE-GENUINE";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            Devmode original = BuildDevmode(1920, 1080, 32, 60, 0);
            device.SetCurrentMode(deviceName, original);

            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> dict =
                new Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>>();
            Dictionary<string, ResolutionModeWrapper> lastKnown = new Dictionary<string, ResolutionModeWrapper>();
            List<string> attached = new List<string> { deviceName };

            FakeResolutionAdoptionTimer timer = new FakeResolutionAdoptionTimer();
            ResolutionAdoptionDebouncer debouncer = new ResolutionAdoptionDebouncer(timer);
            Action refresh = delegate
            {
                WindowsResolutionRefresher.Refresh(device, dict, lastKnown, attached, false, true, null);
            };

            refresh();
            checklist.Check(dict[deviceName].Item1.Equals(original), "the initial (undebounced) capture gets the original mode");

            // The user genuinely changes their desktop resolution - nothing reverts this before
            // the interval below elapses.
            Devmode changed = BuildDevmode(2560, 1440, 32, 144, 0);
            device.SetCurrentMode(deviceName, changed);
            debouncer.OnDisplaySettingsChanged(false, refresh);

            checklist.Check(dict[deviceName].Item1.Equals(original),
                "Item1 still reads the OLD mode immediately after the change - not yet adopted, exactly like the transient case, since nothing at notification time can tell them apart");

            timer.Elapse();

            checklist.Check(dict[deviceName].Item1.Equals(changed),
                "Item1 adopts the new mode once the debounce interval has elapsed with nothing having reverted it");
            checklist.Check(device.CallLog.Count == 0, "none of this ever touches the driver - every call above is a live-mode read, never a ChangeMode");

            checklist.Lines.Add(string.Empty);
        }

        // preserveCapturedMode true means a vibranceGUI apply is currently outstanding - Refresh
        // never performs a live read for an existing device in that state at all (Item1 is
        // preserved unconditionally - see Refresh's own top-of-method comment), so there is no
        // adoption risk to debounce against, and the refresh must run immediately: a real apply in
        // progress needs Item2/the reattach fallback (R7/R8 above) kept current right now, not
        // delayed behind a countdown guarding against a risk that cannot occur in this state. Also
        // proves a countdown armed from an earlier, since-superseded notification (back when
        // preserveCapturedMode was still false) is cancelled, not left to fire later on top of this
        // immediate one.
        private static void CheckDebouncePreserveCapturedModeRunsImmediatelyAndCancelsAnyPending(Checklist checklist)
        {
            checklist.Lines.Add("ResolutionAdoptionDebouncer.OnDisplaySettingsChanged(preserveCapturedMode: true, ...) runs its callback immediately, with no countdown armed, and cancels any countdown already pending from an earlier, since-superseded notification:");

            FakeResolutionAdoptionTimer timer = new FakeResolutionAdoptionTimer();
            ResolutionAdoptionDebouncer debouncer = new ResolutionAdoptionDebouncer(timer);
            int refreshCallCount = 0;
            Action refresh = delegate { refreshCallCount++; };

            // A countdown from an earlier notification, back when no profile was applied yet, is
            // still pending.
            debouncer.OnDisplaySettingsChanged(false, refresh);
            checklist.Check(timer.IsPending, "a countdown is pending after the first (preserveCapturedMode false) notification");

            debouncer.OnDisplaySettingsChanged(true, refresh);

            checklist.Check(refreshCallCount == 1,
                string.Format("the refresh callback runs immediately and exactly once, got {0} calls", refreshCallCount));
            checklist.Check(!timer.IsPending,
                "the earlier pending countdown is cancelled, not left to fire later on top of the immediate refresh");
            checklist.Check(timer.CancelCallCount == 1,
                string.Format("Cancel is called exactly once, got {0}", timer.CancelCallCount));

            checklist.Lines.Add(string.Empty);
        }

        // ResolutionAdoptionDebouncer.Cancel() (the public passthrough, not the internal
        // preserveCapturedMode=true branch CheckDebouncePreserveCapturedModeRunsImmediatelyAndCancelsAnyPending
        // already covers) is what VibranceGUI.CleanUp calls, in its own finally block, alongside
        // unsubscribing SystemEvents.DisplaySettingsChanged - see that call site's own comment for
        // why: a countdown already armed keeps ticking down independent of SystemEvents, so
        // unsubscribing the event alone would not stop one already in flight from firing after the
        // form is disposed.
        private static void CheckDebounceCancelStopsAPendingCountdown(Checklist checklist)
        {
            checklist.Lines.Add("ResolutionAdoptionDebouncer.Cancel() stops a pending countdown outright - the callback never runs even once the interval would otherwise have elapsed (regression test for VibranceGUI.CleanUp's own shutdown-time call to this method):");

            FakeResolutionAdoptionTimer timer = new FakeResolutionAdoptionTimer();
            ResolutionAdoptionDebouncer debouncer = new ResolutionAdoptionDebouncer(timer);
            int refreshCallCount = 0;
            Action refresh = delegate { refreshCallCount++; };

            debouncer.OnDisplaySettingsChanged(false, refresh);
            checklist.Check(timer.IsPending, "a countdown is pending before Cancel() is called");

            debouncer.Cancel();

            checklist.Check(!timer.IsPending, "no countdown is pending after Cancel()");

            timer.Elapse();

            checklist.Check(refreshCallCount == 0,
                string.Format("the refresh callback never runs - Cancel() stopped it before the interval elapsed, got {0} calls", refreshCallCount));

            checklist.Lines.Add(string.Empty);
        }

        // Independently pins the documented interval's actual value - not merely that the class
        // passes its own constant through wherever it is used
        // (CheckDebounceDelaysAdoptionUntilTheIntervalElapses already proves that), but that the
        // constant itself IS what this branch's own report says it is. A change to
        // DebounceIntervalMs that nobody meant to make is caught here without anyone needing to
        // remember to cross-check it against a number written down elsewhere.
        private static void CheckDebounceIntervalMatchesTheDocumentedValue(Checklist checklist)
        {
            checklist.Lines.Add("ResolutionAdoptionDebouncer.DebounceIntervalMs is 2000ms - this branch's chosen (not measured; see the constant's own comment for why) debounce interval:");

            checklist.Check(ResolutionAdoptionDebouncer.DebounceIntervalMs == 2000,
                string.Format("DebounceIntervalMs is 2000, got {0}", ResolutionAdoptionDebouncer.DebounceIntervalMs));

            checklist.Lines.Add(string.Empty);
        }

        // ------------------------------------------------------------------
        // Shared helpers.
        // ------------------------------------------------------------------

        // Stands in for what a real EnumDisplaySettings call returns for the current mode.
        // dmPosition is set to a non-zero, distinctive value and DM_POSITION is the only bit
        // declared here - deliberately NOT the four fields ChangeResolutionEx itself controls, so
        // CheckDmFieldsDeclared can tell "ApplyTargetFields actually declared these" from "they
        // happened to already be set on the current mode" (a real EnumDisplaySettings result would
        // typically carry both, but folding them into this baseline too would make that check
        // unable to fail no matter what ApplyTargetFields does - see CheckDmFieldsDeclared).
        private static Devmode BuildDevmode(uint width, uint height, uint bpp, uint freq, uint fixedOutput)
        {
            Devmode mode = new Devmode();
            mode.dmSize = (ushort)Marshal.SizeOf(mode);
            mode.dmPosition = new Pointl();
            mode.dmPosition.x = 100;
            mode.dmPosition.y = 200;
            mode.dmPelsWidth = width;
            mode.dmPelsHeight = height;
            mode.dmBitsPerPel = bpp;
            mode.dmDisplayFrequency = freq;
            mode.dmDisplayFixedOutput = fixedOutput;
            mode.dmFields = (uint)DevmodeFields.DmPosition;
            return mode;
        }

        private static ResolutionModeWrapper BuildTarget(uint width, uint height, uint bpp, uint freq, uint fixedOutput)
        {
            ResolutionModeWrapper target = new ResolutionModeWrapper();
            target.DmPelsWidth = width;
            target.DmPelsHeight = height;
            target.DmBitsPerPel = bpp;
            target.DmDisplayFrequency = freq;
            target.DmDisplayFixedOutput = fixedOutput;
            return target;
        }

        // Stands in for a real display: an in-memory current Devmode per fake device name, plus a
        // per-(device,flags) one-shot queue of forced ChangeMode results and a full call log in
        // order. Device names used by the checks above are chosen to never collide with a real
        // Screen.DeviceName ("\\.\DISPLAYn"), since ResolutionHelper's own failure-tracking
        // dictionaries are static and would otherwise be shared with any real hardware use in the
        // same process - the same note GammaRestoreFixture.cs carries for its own fake.
        private class FakeDisplayModeDevice : IDisplayModeDevice
        {
            public struct RecordedCall
            {
                public readonly string DeviceName;
                public readonly ChangeDisplaySettingsFlags Flags;
                public readonly Devmode Mode;

                public RecordedCall(string deviceName, ChangeDisplaySettingsFlags flags, Devmode mode)
                {
                    DeviceName = deviceName;
                    Flags = flags;
                    Mode = mode;
                }
            }

            private readonly Dictionary<string, Devmode> _currentModes = new Dictionary<string, Devmode>();
            private readonly Dictionary<string, List<Devmode>> _modeTables = new Dictionary<string, List<Devmode>>();
            private readonly Dictionary<string, Queue<DispChange>> _queuedResults = new Dictionary<string, Queue<DispChange>>();
            private readonly Dictionary<string, uint> _pinnedFixedOutput = new Dictionary<string, uint>();
            private readonly HashSet<string> _suppressNextApply = new HashSet<string>();

            public readonly List<RecordedCall> CallLog = new List<RecordedCall>();

            // Per-device count of TryEnumerateMode calls this fake has actually answered (whether
            // or not a table was ever set for that name) - the seam CheckSupportedResolutionListStaysTheInstanceTheFormCaptured
            // and CheckReattachedDeviceReenumeratesItsSupportedModes need to prove
            // EnumerateSupportedResolutionModes was (or was not) re-invoked, since the returned
            // List<ResolutionModeWrapper> alone cannot distinguish "reused the same instance" from
            // "re-enumerated into contents that happen to be equal".
            public readonly Dictionary<string, int> EnumerateCallCounts = new Dictionary<string, int>();

            public void SetCurrentMode(string deviceName, Devmode mode)
            {
                _currentModes[deviceName] = mode;
            }

            public Devmode GetCurrentMode(string deviceName)
            {
                return _currentModes[deviceName];
            }

            // Stands in for a device whose current mode has become unreadable - a monitor that
            // just dropped, or a driver in a bad state. Named separately from SetCurrentMode's
            // absence (never calling it at all) purely so a check can make the transition explicit
            // for a device that WAS readable a moment ago.
            public void SetUnreadable(string deviceName)
            {
                _currentModes.Remove(deviceName);
            }

            // The table TryEnumerateMode walks for deviceName - _modeTables was declared but never
            // written before this fixture's own R1-R8, which is why every EnumerateSupportedResolutionModes
            // call before them always returned an empty list without a single check noticing.
            public void SetSupportedModes(string deviceName, List<Devmode> modes)
            {
                _modeTables[deviceName] = modes;
            }

            // Forces the NEXT ChangeMode call for (deviceName, flags) to return result instead of
            // the default DispChangeSuccessful - one-shot, first in first out, so a check that
            // needs the same (deviceName, flags) pair to fail more than once queues it that many
            // times.
            public void QueueResult(string deviceName, ChangeDisplaySettingsFlags flags, DispChange result)
            {
                string key = QueueKey(deviceName, flags);
                Queue<DispChange> queue;
                if (!_queuedResults.TryGetValue(key, out queue))
                {
                    queue = new Queue<DispChange>();
                    _queuedResults[key] = queue;
                }
                queue.Enqueue(result);
            }

            // Stands in for a driver that silently ignores the requested DmDisplayFixedOutput and
            // always reports back its own value instead - CheckUnachievableFixedOutputLoopIsBounded's
            // scenario. Applies to every CDS_UPDATEREGISTRY call that actually lands, not a
            // one-shot.
            public void PinFixedOutputTo(string deviceName, uint value)
            {
                _pinnedFixedOutput[deviceName] = value;
            }

            // One-shot: the next applying CDS_UPDATEREGISTRY call for deviceName is still recorded
            // and still returns whatever result was queued (or Successful by default), but does
            // NOT mutate the stored current mode - standing in for a driver that reports success
            // without the mode actually having changed, for CheckPostApplyVerification.
            public void SuppressNextApply(string deviceName)
            {
                _suppressNextApply.Add(deviceName);
            }

            // Stands in for a driver that throws instead of returning a failure DispChange -
            // CheckExitRestoreSurvivesADeviceThatThrows's scenario (X11). Not one-shot, unlike
            // SuppressNextApply above: RestoreOnExit's own single attempt never gets a second call
            // that could consume a one-shot flag.
            private readonly HashSet<string> _throwOnChangeMode = new HashSet<string>();

            public void ThrowOnChangeMode(string deviceName)
            {
                _throwOnChangeMode.Add(deviceName);
            }

            // The READ-side mirror of ThrowOnChangeMode - stands in for a device broken enough to
            // throw on TryGetCurrentMode itself, not just on a write. RestoreOnExit's guard 5
            // (IsResolutionChangeNeeded) reaches this before ChangeMode is ever called, which is the
            // whole reason RestoreOnExit wraps its ENTIRE body in try/catch instead of just the
            // ChangeResolutionEx line - CheckExitRestoreSurvivesAReadThatThrows (X17) is what proves
            // that wider wrap actually matters, rather than merely being wrapped and never exercised
            // for that specific reason.
            private readonly HashSet<string> _throwOnGetCurrentMode = new HashSet<string>();

            public void ThrowOnGetCurrentMode(string deviceName)
            {
                _throwOnGetCurrentMode.Add(deviceName);
            }

            private static string QueueKey(string deviceName, ChangeDisplaySettingsFlags flags)
            {
                return deviceName + "|" + flags;
            }

            public bool TryGetCurrentMode(string deviceName, out Devmode mode)
            {
                if (_throwOnGetCurrentMode.Contains(deviceName))
                {
                    throw new InvalidOperationException("FakeDisplayModeDevice.TryGetCurrentMode forced to throw for " + deviceName);
                }
                return _currentModes.TryGetValue(deviceName, out mode);
            }

            public bool TryEnumerateMode(string deviceName, int modeNum, out Devmode mode)
            {
                int count;
                EnumerateCallCounts.TryGetValue(deviceName, out count);
                EnumerateCallCounts[deviceName] = count + 1;

                List<Devmode> table;
                if (_modeTables.TryGetValue(deviceName, out table) && modeNum >= 0 && modeNum < table.Count)
                {
                    mode = table[modeNum];
                    return true;
                }
                mode = new Devmode();
                return false;
            }

            public DispChange ChangeMode(string deviceName, Devmode mode, ChangeDisplaySettingsFlags flags)
            {
                if (_throwOnChangeMode.Contains(deviceName))
                {
                    throw new InvalidOperationException("FakeDisplayModeDevice.ChangeMode forced to throw for " + deviceName);
                }

                CallLog.Add(new RecordedCall(deviceName, flags, mode));

                DispChange result = DispChange.DispChangeSuccessful;
                string key = QueueKey(deviceName, flags);
                Queue<DispChange> queue;
                if (_queuedResults.TryGetValue(key, out queue) && queue.Count > 0)
                {
                    result = queue.Dequeue();
                }

                bool wouldApply = (flags & ChangeDisplaySettingsFlags.CdsUpdateregistry) != 0 &&
                    (result == DispChange.DispChangeSuccessful || result == DispChange.DispChangeNotupdated);

                if (wouldApply && !_suppressNextApply.Remove(deviceName))
                {
                    Devmode applied = mode;
                    uint pinned;
                    if (_pinnedFixedOutput.TryGetValue(deviceName, out pinned))
                    {
                        applied.dmDisplayFixedOutput = pinned;
                    }
                    _currentModes[deviceName] = applied;
                }

                return result;
            }
        }

        // The seam the ResolutionAdoptionDebouncer checks above drive - see
        // ResolutionAdoptionDebouncer.cs's own IResolutionAdoptionTimer comment for the contract
        // this stands in for. Elapse() fires the most recently armed callback synchronously, on
        // the calling thread, on command - never a real Thread.Sleep or Application.DoEvents
        // anywhere, which is what keeps every check above running exactly as instantly as the ones
        // driving WindowsResolutionRefresher.Refresh directly. Mirrors FakeDisplayModeDevice's own
        // call-count bookkeeping (CallLog, EnumerateCallCounts) rather than merely tracking the
        // most recent call, since "was this called the right number of times" is exactly what
        // several of those checks need to prove.
        private class FakeResolutionAdoptionTimer : IResolutionAdoptionTimer
        {
            public int RestartCallCount;
            public int CancelCallCount;
            public int LastDelayMs;

            private Action _pending;

            public bool IsPending
            {
                get { return _pending != null; }
            }

            public void Restart(int delayMs, Action onElapsed)
            {
                RestartCallCount++;
                LastDelayMs = delayMs;
                // Replaces whatever was pending, exactly like the real
                // System.Windows.Forms.Timer-backed implementation does by resetting Interval -
                // never stacks a second pending callback alongside the first.
                _pending = onElapsed;
            }

            public void Cancel()
            {
                CancelCallCount++;
                _pending = null;
            }

            // _pending is read into a local and cleared to null BEFORE pending() runs, deliberately
            // not after - mirrors FormsResolutionAdoptionTimer.OnTick's own ordering, for the same
            // reason (see that method's comment): pending() is allowed to call Restart again,
            // re-arming this very timer from inside its own elapse, and clearing _pending first is
            // what lets that re-arm's assignment stick rather than being wiped by a
            // "_pending = null" that would otherwise still be waiting to run AFTER pending()
            // returns. CheckDebounceCallbackMayReArmDuringItsOwnElapse pins exactly this ordering -
            // swapping these two statements is the mutation that check exists to catch.
            public void Elapse()
            {
                Action pending = _pending;
                _pending = null;
                if (pending != null)
                {
                    pending();
                }
            }
        }

        // Minimal enough for the AMD exit-restore checks (X14/X15) above, which never touch the
        // adapter at all - only ResolutionHelper's own IDisplayModeDevice seam - but IsAvailable()
        // must still return false so AmdDynamicVibranceProxy's constructor never calls Init() or
        // installs a real WinEventHook, mirroring every other fixture's own minimal copy of this same
        // fake (e.g. StartupForegroundFixture.FakeAmdAdapter).
        private class FakeAmdAdapter : IAmdAdapter
        {
            public void SetSaturationOnAllDisplays(int vibranceLevel)
            {
            }

            public bool SetSaturationOnDisplay(int vibranceLevel, string displayName)
            {
                return true;
            }

            public bool IsAvailable()
            {
                return false;
            }

            public void Init()
            {
            }

            public void Dispose()
            {
            }
        }

        // CheckNoRepeatNotification's own log-content probe - mirrors ProfileToggleFixture's
        // RecordingLogSink exactly (per-fixture fakes are this codebase's convention; see that
        // class's own header comment). Records the exact string Program.LogSafely/VibranceGUI.Log
        // was called with, never touching the real vibranceGUI.log, so a check can assert on WHICH
        // failure code actually got logged - not just how many lines - the distinction
        // CheckNoRepeatNotification's own comment explains was needed to catch its prior bug.
        private class RecordingLogSink : ILogSink
        {
            public readonly List<string> Messages = new List<string>();

            public void Write(string message)
            {
                Messages.Add(message);
            }

            public void Write(Exception ex)
            {
                Messages.Add(ex.ToString());
            }
        }

        private class Checklist
        {
            public readonly List<string> Lines = new List<string>();
            public int Passed;
            public int Total;

            // No Skip() here, unlike GammaRestoreFixture's Checklist - that one exists for its
            // hardware half's unmet preconditions (no display attached, user declined, etc.). This
            // fixture has no hardware half, by design, and must never gain one (see the SAFETY
            // section of the design this fixture implements) - so there is no path that would ever
            // need to skip a check instead of running it.
            public void Check(bool condition, string description)
            {
                Total++;
                if (condition)
                    Passed++;
                Lines.Add(string.Format("[{0}] {1}", condition ? "PASS" : "FAIL", description));
            }
        }
    }
}
