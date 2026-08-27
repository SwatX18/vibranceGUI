using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

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
            try
            {
                // Phase A: 20 consecutive attempts, all rejected by CDS_TEST with the same code.
                // The revert direction's bound (10) is exceeded partway through, after which every
                // further attempt is Suppressed and never reaches the device at all - 20 is
                // deliberately more than the bound, to prove going further adds neither a second
                // log line nor a second notification.
                FakeDisplayModeDevice device = new FakeDisplayModeDevice();
                device.SetCurrentMode(deviceName, mismatched);
                for (int i = 0; i < 20; i++)
                {
                    device.QueueResult(deviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadflags);
                    device.QueueResult(deviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadflags);
                    ResolutionHelper.ChangeResolutionEx(device, target, deviceName, true);
                }
                checklist.Check(raised.Count == 1,
                    string.Format("20 consecutive identical failures raise ResolutionChangeFailed exactly once, got {0}", raised.Count));
                checklist.Check(ResolutionHelper.LoggedLineCountForTests == 1,
                    string.Format("20 consecutive identical failures write exactly one log line, got {0}", ResolutionHelper.LoggedLineCountForTests));

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
                device.QueueResult(deviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadmode);
                device.QueueResult(deviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadmode);
                ResolutionHelper.ChangeResolutionEx(device, target, deviceName, true);
                checklist.Check(ResolutionHelper.LoggedLineCountForTests == loggedLinesBeforePhaseB + 1,
                    string.Format("a different failure code, after a real success reopened the device, logs one more line - had {0} log lines before, {1} after",
                        loggedLinesBeforePhaseB, ResolutionHelper.LoggedLineCountForTests));
                checklist.Check(raised.Count == 0,
                    "a single new failure, below the give-up bound, does not raise a second notification");

                // Phase C: another real success, then a REPEAT of phase B's own code (DispChangeBadmode)
                // - still logs again, proving the reopening is not limited to "only a brand new code
                // works once".
                ResolutionHelper.ResolutionChangeResult clearingResult2 = ResolutionHelper.ChangeResolutionEx(device, clearingTarget, deviceName, false);
                checklist.Check(clearingResult2 == ResolutionHelper.ResolutionChangeResult.AlreadyMatching,
                    "the second clearing call is itself a real success (AlreadyMatching), not a test-only reset");

                int loggedLinesBeforePhaseC = ResolutionHelper.LoggedLineCountForTests;
                device.QueueResult(deviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadmode);
                device.QueueResult(deviceName, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadmode);
                ResolutionHelper.ChangeResolutionEx(device, target, deviceName, true);
                checklist.Check(ResolutionHelper.LoggedLineCountForTests == loggedLinesBeforePhaseC + 1,
                    string.Format("a repeated failure code, after another real success, logs again instead of staying silent - had {0} log lines before, {1} after",
                        loggedLinesBeforePhaseC, ResolutionHelper.LoggedLineCountForTests));
            }
            finally
            {
                ResolutionHelper.ResolutionChangeFailed -= handler;
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
        // is readonly - so the List<ResolutionModeWrapper> instance a caller captures for Item2 has
        // to stay the exact same instance across every later refresh, any flag, or that field goes
        // silently stale. Also pins that a device already in the dictionary is never re-enumerated.
        private static void CheckSupportedResolutionListStaysTheInstanceTheFormCaptured(Checklist checklist)
        {
            checklist.Lines.Add("Item2 stays the SAME List<ResolutionModeWrapper> instance across repeated refreshes with mixed preserveCapturedMode flags, and is never re-enumerated once a device has an entry (regression test for _supportedResolutionList - captured once, readonly - silently going stale):");
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

        // R-A. Documents TODAY's behaviour on purpose, and carries the reasoning for why a
        // reviewer-proposed "smarter" fix - skip re-capturing Item1 when the live mode matches a
        // configured ApplicationSetting.ResolutionSettings entry - was REJECTED outright, not
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
        // Adopting a game's foreign mode self-heals the moment that mode goes away (this check);
        // refusing to adopt a user's genuine desktop mode never heals, since nothing else in the
        // program will ever re-capture it. The right future fix keys on DURATION, not value.
        private static void CheckAdoptedForeignModeSelfHealsOnceItIsGone(Checklist checklist)
        {
            checklist.Lines.Add("With preserveCapturedMode false, a refresh adopts whatever mode is live - even a foreign one set with no profile applied - and self-heals back once that foreign mode is gone (today's stated, deliberate behaviour - see this check's own comment for why the 'match a configured ResolutionSettings entry' fix was rejected):");
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
        // detach/reattach instead of the simpler "no previous entry at all" case R1 covers.
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
            // refresher's own retention, not anything ChangeResolutionEx tracks.
            RunRefresh(checklist, device, "the refresh with A reattached never touches the driver",
                dict, lastKnown, new List<string> { deviceA, deviceB }, true, true, onUnreadable);

            checklist.Check(dict.ContainsKey(deviceA) && dict[deviceA].Item1.Equals(desktopA),
                "device A's Item1 is still the desktop mode after reattaching - NOT the game's mode it is actually showing right now");
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

            private static string QueueKey(string deviceName, ChangeDisplaySettingsFlags flags)
            {
                return deviceName + "|" + flags;
            }

            public bool TryGetCurrentMode(string deviceName, out Devmode mode)
            {
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
