using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace vibrance.GUI.common
{
    /// <summary>
    /// Regression coverage for the gamma restore fix (issue #128): restore used to synthesise an
    /// identity ramp from the user's brightness/contrast/gamma sliders and stamp it over whatever
    /// was really on the monitor - an ICC profile, f.lux, hardware calibration - destroying it.
    /// Split into two halves on purpose. The pure half - CalculateLUT/ComposeGammaRamp/
    /// IsPlausibleGammaRamp math, no display access at all - is what --selftest-gamma runs and what
    /// a reviewer is expected to run; it is safe anywhere, any time. The hardware half writes a
    /// probe ramp to a real monitor and is only reachable through --selftest-gamma-display, never
    /// through --selftest-gamma or the rest of the regression suite. It cannot live in
    /// StabilityFixture, which documents "no GUI, no live GPU driver" as a hard constraint and
    /// forces SetNeverChangeColorSettings(true) specifically so it can never reach a real screen -
    /// a hardware round trip here would violate that contract.
    /// </summary>
    public static class GammaRestoreFixture
    {
        public static List<string> Run()
        {
            Checklist checklist = new Checklist();
            checklist.Lines.Add("vibranceGUI gamma restore self test (pure half)");
            checklist.Lines.Add(string.Empty);

            RunPureChecks(checklist);

            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add(string.Format("PASSED {0}/{1}", checklist.Passed, checklist.Total));
            return checklist.Lines;
        }

        public static List<string> RunWithDisplay()
        {
            Checklist checklist = new Checklist();
            checklist.Lines.Add("vibranceGUI gamma restore self test (pure half + hardware round trip)");
            checklist.Lines.Add(string.Empty);

            RunPureChecks(checklist);
            RunHardwareChecks(checklist);

            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add(string.Format("PASSED {0}/{1}", checklist.Passed, checklist.Total));
            return checklist.Lines;
        }

        // ------------------------------------------------------------------
        // Pure half - no display access.
        // ------------------------------------------------------------------

        private static void RunPureChecks(Checklist checklist)
        {
            CheckIdentityLut(checklist);
            CheckComposeIsIdentityOnNeutralSliders(checklist);
            CheckComposeIsSourceLutOnIdentityBaseline(checklist);
            CheckComposePreservesBlueReduction(checklist);
            CheckPlausibilityGuards(checklist);
            CheckRepeatedComposeIsNotIdempotent(checklist);
            CheckDeviceSeamMultiCycleBaselineStability(checklist);
            CheckDeviceSeamSurvivesFailedRestoreWrite(checklist);
            CheckDeviceSeamReBaselinesOnExternalChange(checklist);
            CheckDeviceSeamApplyFailuresLeaveNoTrace(checklist);
            CheckDeviceSeamRestoreScopingAcrossDevices(checklist);
        }

        // Neutral sliders (50/50/100 on the 0-100 scale the UI uses) have to reduce to the
        // identity ramp - this is the algebraic fact the whole fix rests on: composing on top of
        // an identity source LUT is what makes RestoreCapturedGammaRamps reproduce the captured
        // baseline bit for bit.
        private static void CheckIdentityLut(Checklist checklist)
        {
            checklist.Lines.Add("CalculateLUT(0.5, 0.5, 1.0) is the identity ramp:");

            ushort[] lut = DeviceGammaRampHelper.CalculateLUT(0.5, 0.5, 1.0);
            int firstMismatch;
            bool isIdentity = TryFindFirstIdentityMismatch(lut, out firstMismatch);

            checklist.Check(isIdentity, isIdentity
                ? "CalculateLUT(0.5,0.5,1.0)[i] == i*257 for all 256 i"
                : string.Format("CalculateLUT(0.5,0.5,1.0)[i] == i*257 for all 256 i - first mismatch at i={0}, expected {1}, got {2}",
                    firstMismatch, (ushort)(firstMismatch * 257), lut[firstMismatch]));

            checklist.Lines.Add(string.Empty);
        }

        // Invariant 1 from the design: sourceLut == CalculateLUT(0.5,0.5,1.0) => result == baseline,
        // bit for bit. Neutral sliders must replay the baseline exactly, or a user with a real ICC
        // profile would see it altered every time a game merely exits.
        private static void CheckComposeIsIdentityOnNeutralSliders(Checklist checklist)
        {
            checklist.Lines.Add("Compose with neutral sliders (0.5,0.5,1.0) reproduces the baseline bit for bit:");

            DeviceGammaRampHelper.GammaRamp blueReduced = BuildBlueReducedGammaRamp();
            DeviceGammaRampHelper.GammaRamp composed = DeviceGammaRampHelper.ComposeGammaRamp(
                blueReduced, DeviceGammaRampHelper.CalculateLUT(0.5, 0.5, 1.0));

            string mismatch;
            bool matches = !TryDescribeGammaRampMismatch(blueReduced, composed, out mismatch);
            checklist.Check(matches, matches
                ? "ComposeGammaRamp(blueReduced, CalculateLUT(0.5,0.5,1.0)) == blueReduced across all 768 entries"
                : "ComposeGammaRamp(blueReduced, CalculateLUT(0.5,0.5,1.0)) == blueReduced across all 768 entries - " + mismatch);

            checklist.Lines.Add(string.Empty);
        }

        // Invariant 2 from the design: baseline[j] == j*257 (identity) => result == sourceLut, bit
        // for bit. An uncalibrated user - no baseline captured is ever anything but identity in
        // practice - must see exactly the curve their sliders asked for, with no attenuation from
        // the compose step.
        private static void CheckComposeIsSourceLutOnIdentityBaseline(Checklist checklist)
        {
            checklist.Lines.Add("Compose on an identity baseline reproduces the source LUT bit for bit:");

            DeviceGammaRampHelper.GammaRamp identity = BuildIdentityGammaRamp();
            ushort[] lut = DeviceGammaRampHelper.CalculateLUT(0.6, 0.4, 1.2);
            DeviceGammaRampHelper.GammaRamp expected = new DeviceGammaRampHelper.GammaRamp(
                (ushort[])lut.Clone(), (ushort[])lut.Clone(), (ushort[])lut.Clone());
            DeviceGammaRampHelper.GammaRamp composed = DeviceGammaRampHelper.ComposeGammaRamp(identity, lut);

            string mismatch;
            bool matches = !TryDescribeGammaRampMismatch(expected, composed, out mismatch);
            checklist.Check(matches, matches
                ? "ComposeGammaRamp(identity, CalculateLUT(0.6,0.4,1.2)) == CalculateLUT(0.6,0.4,1.2) across all 768 entries"
                : "ComposeGammaRamp(identity, CalculateLUT(0.6,0.4,1.2)) == CalculateLUT(0.6,0.4,1.2) across all 768 entries - " + mismatch);

            checklist.Lines.Add(string.Empty);
        }

        // A calibration that dims one channel relative to the others must still be visible through
        // the compose step - proves ComposeGammaRamp is not silently collapsing back to the
        // sourceLut for every baseline, only for an identity one.
        private static void CheckComposePreservesBlueReduction(Checklist checklist)
        {
            checklist.Lines.Add("Composing on a blue-reduced baseline keeps blue below red across the mid range:");

            DeviceGammaRampHelper.GammaRamp blueReduced = BuildBlueReducedGammaRamp();
            DeviceGammaRampHelper.GammaRamp composed = DeviceGammaRampHelper.ComposeGammaRamp(
                blueReduced, DeviceGammaRampHelper.CalculateLUT(0.6, 0.4, 1.2));

            bool holds = true;
            int firstViolation = -1;
            // Kept away from both extremes, where the composed curve can clip to the same rail on
            // every channel and a "<" comparison would be a coin flip rather than a real check.
            for (int i = 32; i < 224; i++)
            {
                if (!(composed.Blue[i] < composed.Red[i]))
                {
                    holds = false;
                    firstViolation = i;
                    break;
                }
            }

            checklist.Check(holds, holds
                ? "Blue[i] < Red[i] for i in [32,224)"
                : string.Format("Blue[i] < Red[i] for i in [32,224) - first violation at i={0}: Blue={1}, Red={2}",
                    firstViolation, composed.Blue[firstViolation], composed.Red[firstViolation]));

            checklist.Lines.Add(string.Empty);
        }

        // Pins the trap the design called out by name: new GammaRamp() does not run the
        // all-optional-parameter constructor, so all three channels are null - if a future
        // refactor reintroduces "new GammaRamp()" as an undo baseline, this fails loudly instead
        // of throwing a NullReferenceException three calls later inside ComposeGammaRamp.
        private static void CheckPlausibilityGuards(Checklist checklist)
        {
            checklist.Lines.Add("IsPlausibleGammaRamp guards against ramps that cannot be a genuine capture:");

            DeviceGammaRampHelper.GammaRamp defaultConstructed = new DeviceGammaRampHelper.GammaRamp();
            checklist.Check(!DeviceGammaRampHelper.IsPlausibleGammaRamp(defaultConstructed),
                "rejects new GammaRamp() - the implicit parameterless struct constructor leaves all three channels null");

            DeviceGammaRampHelper.GammaRamp allZero = new DeviceGammaRampHelper.GammaRamp(
                new ushort[DeviceGammaRampHelper.GAMMA_RAMP_SIZE],
                new ushort[DeviceGammaRampHelper.GAMMA_RAMP_SIZE],
                new ushort[DeviceGammaRampHelper.GAMMA_RAMP_SIZE]);
            checklist.Check(!DeviceGammaRampHelper.IsPlausibleGammaRamp(allZero),
                "rejects an all-zero ramp");

            DeviceGammaRampHelper.GammaRamp oneZeroChannel = BuildIdentityGammaRamp();
            oneZeroChannel.Blue = new ushort[DeviceGammaRampHelper.GAMMA_RAMP_SIZE];
            checklist.Check(!DeviceGammaRampHelper.IsPlausibleGammaRamp(oneZeroChannel),
                "rejects a ramp with one all-zero channel");

            DeviceGammaRampHelper.GammaRamp wrongLength = BuildIdentityGammaRamp();
            wrongLength.Red = new ushort[128];
            checklist.Check(!DeviceGammaRampHelper.IsPlausibleGammaRamp(wrongLength),
                "rejects a ramp with a 128-entry channel");

            checklist.Check(DeviceGammaRampHelper.IsPlausibleGammaRamp(BuildIdentityGammaRamp()),
                "accepts an identity ramp");

            checklist.Check(DeviceGammaRampHelper.IsPlausibleGammaRamp(BuildBlueReducedGammaRamp()),
                "accepts a blue-reduced ramp");

            checklist.Lines.Add(string.Empty);
        }

        // The arithmetic precondition both B1 and S1 depend on: repeatedly composing with the same
        // non-neutral LUT is not idempotent, so re-baselining from a prior compose output (rather
        // than holding the true baseline constant) necessarily drifts. Pure - no device seam needed
        // - kept from this fixture's first version; the checks below it replaced two checks that
        // looked like regression tests but could not fail (see their own git history / review
        // notes): they hard-coded the same baseline as the compose input on every cycle, which
        // assumes exactly the invariant B1 broke, so reverting B1 left them passing.
        private static void CheckRepeatedComposeIsNotIdempotent(Checklist checklist)
        {
            checklist.Lines.Add("Repeated composition with a non-neutral LUT is not idempotent (arithmetic precondition for B1/S1):");

            DeviceGammaRampHelper.GammaRamp baseline = BuildBlueReducedGammaRamp();
            ushort[] nonNeutralLut = DeviceGammaRampHelper.CalculateLUT(0.6, 0.4, 1.2);

            DeviceGammaRampHelper.GammaRamp composedOnce = DeviceGammaRampHelper.ComposeGammaRamp(baseline, nonNeutralLut);
            DeviceGammaRampHelper.GammaRamp composedTwice = DeviceGammaRampHelper.ComposeGammaRamp(composedOnce, nonNeutralLut);
            bool notIdempotent = !GammaRampsBitIdentical(composedOnce, composedTwice);
            checklist.Check(notIdempotent,
                "compose(compose(B, u), u) != compose(B, u) for a non-neutral u - this is exactly why re-baselining from a prior compose output drifts");

            checklist.Lines.Add(string.Empty);
        }

        // Drives the REAL ApplyGameGammaRamp/RestoreCapturedGammaRamps through the IGammaDevice seam
        // against a fake monitor - not a reimplementation of their logic, the actual internal
        // overloads under test. Runs several full apply/restore cycles with non-neutral sliders
        // throughout and asserts the fake monitor is back to its ORIGINAL content - read fresh at
        // the very start and never hard-coded anywhere in this method - bit for bit at the end.
        // Reverting the B1 fix (dropping the captured baseline on every restore) makes this fail:
        // each cycle would re-capture the prior restore's own output as the new "baseline", and per
        // the check above that is not idempotent, so the final content would drift.
        private static void CheckDeviceSeamMultiCycleBaselineStability(Checklist checklist)
        {
            checklist.Lines.Add("N full apply/restore cycles against a fake device leave the true baseline undisturbed (regression test for B1):");
            DeviceGammaRampHelper.ResetForTests();

            const string deviceName = "FAKE-B1-CYCLE";
            FakeGammaDevice device = new FakeGammaDevice();
            DeviceGammaRampHelper.GammaRamp original = BuildBlueReducedGammaRamp();
            device.SetMonitor(deviceName, original);

            const int cycles = 5;
            bool allApplied = true;
            for (int i = 0; i < cycles; i++)
            {
                allApplied = DeviceGammaRampHelper.ApplyGameGammaRamp(device, deviceName, 60, 40, 120) && allApplied;
                DeviceGammaRampHelper.RestoreCapturedGammaRamps(device, 60, 50, 100);
            }
            checklist.Check(allApplied, string.Format("all {0} cycles' ApplyGameGammaRamp calls returned true", cycles));

            // One final cycle with NEUTRAL sliders - the true baseline must come back exactly,
            // however many non-neutral cycles ran before it.
            DeviceGammaRampHelper.ApplyGameGammaRamp(device, deviceName, 60, 40, 120);
            DeviceGammaRampHelper.RestoreCapturedGammaRamps(device, 50, 50, 100);

            string mismatch;
            bool matches = !TryDescribeGammaRampMismatch(original, device.GetMonitor(deviceName), out mismatch);
            checklist.Check(matches, matches
                ? string.Format("after {0} non-neutral cycles, a final neutral restore reproduces the original device content bit for bit", cycles)
                : string.Format("after {0} non-neutral cycles, a final neutral restore reproduces the original device content bit for bit - {1}", cycles, mismatch));

            checklist.Lines.Add(string.Empty);
        }

        // Regression test for S1: a restore whose write fails must not corrupt the baseline for
        // every later apply on that device. Reverting the S1 fix (dropping _lastWrittenGammaRamps
        // on a failed or unconfirmed write) makes this fail: the next ApplyGameGammaRamp would see
        // no record of what was last written, treat the device's current content - this
        // application's own still-live game ramp, since the restore that would have overwritten it
        // just failed - as the new "true" baseline, and a neutral Windows slider setting (the
        // default) would then reproduce that poisoned baseline exactly, forever, with nothing to
        // reveal the mistake.
        private static void CheckDeviceSeamSurvivesFailedRestoreWrite(Checklist checklist)
        {
            checklist.Lines.Add("A failed restore write does not poison the baseline for later cycles (regression test for S1):");
            DeviceGammaRampHelper.ResetForTests();

            const string deviceName = "FAKE-S1-RESTORE-FAIL";
            FakeGammaDevice device = new FakeGammaDevice();
            DeviceGammaRampHelper.GammaRamp original = BuildBlueReducedGammaRamp();
            device.SetMonitor(deviceName, original);

            bool applied1 = DeviceGammaRampHelper.ApplyGameGammaRamp(device, deviceName, 60, 40, 120);

            // The restore that follows fails outright - nothing changes on the fake device, exactly
            // like CreateDC failing on a real one.
            device.FailNextWrite(deviceName);
            DeviceGammaRampHelper.RestoreCapturedGammaRamps(device, 50, 50, 100);

            // The device still holds the (unrestored) game ramp from the first apply - the fake
            // never mutated it, since the write failed.
            DeviceGammaRampHelper.GammaRamp gameRamp = device.GetMonitor(deviceName);

            bool applied2 = DeviceGammaRampHelper.ApplyGameGammaRamp(device, deviceName, 60, 40, 120);
            checklist.Check(applied1 && applied2, "both ApplyGameGammaRamp calls returned true");

            // Now a restore that succeeds, with neutral sliders - must reproduce the ORIGINAL
            // baseline, not the game ramp the device happened to be holding when the previous
            // restore failed.
            DeviceGammaRampHelper.RestoreCapturedGammaRamps(device, 50, 50, 100);
            DeviceGammaRampHelper.GammaRamp final = device.GetMonitor(deviceName);

            string mismatch;
            bool matchesOriginal = !TryDescribeGammaRampMismatch(original, final, out mismatch);
            checklist.Check(matchesOriginal, matchesOriginal
                ? "after a failed restore write, a later successful restore still reproduces the true baseline bit for bit"
                : "after a failed restore write, a later successful restore still reproduces the true baseline bit for bit - " + mismatch);

            bool stillDiffersFromGameRamp = !GammaRampsBitIdentical(gameRamp, final);
            checklist.Check(stillDiffersFromGameRamp,
                "the final restore is NOT equal to the game ramp the device was left holding after the failed write (the baseline was not poisoned)");

            checklist.Lines.Add(string.Empty);
        }

        // The flip side of the S1 test above: a device whose content genuinely changed underneath
        // this application - a different panel hot-plugged onto the same DeviceName, an ICC profile
        // or f.lux change - must be re-baselined from the new content, not left composing on top of
        // a baseline that is no longer what is really on the monitor.
        private static void CheckDeviceSeamReBaselinesOnExternalChange(Checklist checklist)
        {
            checklist.Lines.Add("A device whose content changed externally is re-baselined from the new content:");
            DeviceGammaRampHelper.ResetForTests();

            const string deviceName = "FAKE-HOTPLUG";
            FakeGammaDevice device = new FakeGammaDevice();
            device.SetMonitor(deviceName, BuildBlueReducedGammaRamp());

            DeviceGammaRampHelper.ApplyGameGammaRamp(device, deviceName, 60, 40, 120);
            DeviceGammaRampHelper.RestoreCapturedGammaRamps(device, 50, 50, 100);

            // Simulate a different panel now answering to the same DeviceName - direct write,
            // bypassing this application's own Write path entirely, exactly as a hot-plug or an
            // external color change would.
            DeviceGammaRampHelper.GammaRamp newTrueBaseline = BuildIdentityGammaRamp();
            device.SetMonitor(deviceName, newTrueBaseline);

            DeviceGammaRampHelper.ApplyGameGammaRamp(device, deviceName, 60, 40, 120);
            DeviceGammaRampHelper.RestoreCapturedGammaRamps(device, 50, 50, 100);

            string mismatch;
            bool matchesNewBaseline = !TryDescribeGammaRampMismatch(newTrueBaseline, device.GetMonitor(deviceName), out mismatch);
            checklist.Check(matchesNewBaseline, matchesNewBaseline
                ? "after an external content change, a later neutral restore reproduces the NEW true baseline bit for bit, not the old one"
                : "after an external content change, a later neutral restore reproduces the NEW true baseline bit for bit, not the old one - " + mismatch);

            checklist.Lines.Add(string.Empty);
        }

        // A failed apply - whether the read itself fails, or the write does on the very first
        // capture for a device - must not leave behind any state that corrupts a later, successful
        // attempt.
        private static void CheckDeviceSeamApplyFailuresLeaveNoTrace(Checklist checklist)
        {
            checklist.Lines.Add("A failed apply leaves no trace for a later successful one:");
            DeviceGammaRampHelper.ResetForTests();

            // Scenario A: the read itself fails on the very first attempt for this device.
            const string readFailDevice = "FAKE-READFAIL";
            FakeGammaDevice readFailFakeDevice = new FakeGammaDevice();
            DeviceGammaRampHelper.GammaRamp readFailBaseline = BuildBlueReducedGammaRamp();
            readFailFakeDevice.SetMonitor(readFailDevice, readFailBaseline);
            readFailFakeDevice.FailNextRead(readFailDevice);

            bool firstReadApplyFailed = !DeviceGammaRampHelper.ApplyGameGammaRamp(readFailFakeDevice, readFailDevice, 60, 40, 120);
            bool secondReadApplySucceeded = DeviceGammaRampHelper.ApplyGameGammaRamp(readFailFakeDevice, readFailDevice, 60, 40, 120);
            DeviceGammaRampHelper.RestoreCapturedGammaRamps(readFailFakeDevice, 50, 50, 100);

            string readFailMismatch;
            bool readFailMatches = !TryDescribeGammaRampMismatch(readFailBaseline, readFailFakeDevice.GetMonitor(readFailDevice), out readFailMismatch);
            bool scenarioAOk = firstReadApplyFailed && secondReadApplySucceeded && readFailMatches;
            checklist.Check(scenarioAOk, scenarioAOk
                ? "a failed read on the first attempt does not stop a later attempt from capturing correctly and restoring the true baseline"
                : string.Format("a failed read on the first attempt does not stop a later attempt from capturing correctly and restoring the true baseline - firstFailed={0}, secondSucceeded={1}, restoreMatched={2}{3}",
                    firstReadApplyFailed, secondReadApplySucceeded, readFailMatches, readFailMismatch == null ? string.Empty : " (" + readFailMismatch + ")"));

            // Scenario B: the write fails on the very first capture for this device (capturedNow ==
            // true at the point of failure) - the tentative capture must be rolled back, not left
            // behind to confuse a later, successful attempt.
            const string writeFailDevice = "FAKE-WRITEFAIL-FIRSTCAPTURE";
            FakeGammaDevice writeFailFakeDevice = new FakeGammaDevice();
            DeviceGammaRampHelper.GammaRamp writeFailBaseline = BuildBlueReducedGammaRamp();
            writeFailFakeDevice.SetMonitor(writeFailDevice, writeFailBaseline);
            writeFailFakeDevice.FailNextWrite(writeFailDevice);

            bool firstWriteApplyFailed = !DeviceGammaRampHelper.ApplyGameGammaRamp(writeFailFakeDevice, writeFailDevice, 60, 40, 120);
            bool secondWriteApplySucceeded = DeviceGammaRampHelper.ApplyGameGammaRamp(writeFailFakeDevice, writeFailDevice, 60, 40, 120);
            DeviceGammaRampHelper.RestoreCapturedGammaRamps(writeFailFakeDevice, 50, 50, 100);

            string writeFailMismatch;
            bool writeFailMatches = !TryDescribeGammaRampMismatch(writeFailBaseline, writeFailFakeDevice.GetMonitor(writeFailDevice), out writeFailMismatch);
            bool scenarioBOk = firstWriteApplyFailed && secondWriteApplySucceeded && writeFailMatches;
            checklist.Check(scenarioBOk, scenarioBOk
                ? "a failed write on the first (capturing) attempt does not stop a later attempt from capturing correctly and restoring the true baseline"
                : string.Format("a failed write on the first (capturing) attempt does not stop a later attempt from capturing correctly and restoring the true baseline - firstFailed={0}, secondSucceeded={1}, restoreMatched={2}{3}",
                    firstWriteApplyFailed, secondWriteApplySucceeded, writeFailMatches, writeFailMismatch == null ? string.Empty : " (" + writeFailMismatch + ")"));

            checklist.Lines.Add(string.Empty);
        }

        // Regression test for S2: RestoreCapturedGammaRamps must only touch devices this
        // application currently holds a game ramp on, not every device it has ever captured a
        // baseline for. Two fake devices, a game applied only to the first; the second's content is
        // changed externally (f.lux at sunset, say) while the first's game is still running, and a
        // restore triggered by the first device's game exiting must leave the second untouched.
        private static void CheckDeviceSeamRestoreScopingAcrossDevices(Checklist checklist)
        {
            checklist.Lines.Add("Restore only touches devices currently holding a game ramp, not every device ever baselined (regression test for S2):");
            DeviceGammaRampHelper.ResetForTests();

            const string device1Name = "FAKE-SCOPE-D1";
            const string device2Name = "FAKE-SCOPE-D2";
            FakeGammaDevice device = new FakeGammaDevice();

            DeviceGammaRampHelper.GammaRamp trueBaseline1 = BuildBlueReducedGammaRamp();
            DeviceGammaRampHelper.GammaRamp trueBaseline2 = BuildIdentityGammaRamp();
            device.SetMonitor(device1Name, trueBaseline1);
            device.SetMonitor(device2Name, trueBaseline2);

            // Both devices get a game applied and then correctly restored, establishing both as
            // previously-captured baselines.
            DeviceGammaRampHelper.ApplyGameGammaRamp(device, device1Name, 60, 40, 120);
            DeviceGammaRampHelper.ApplyGameGammaRamp(device, device2Name, 60, 40, 120);
            DeviceGammaRampHelper.RestoreCapturedGammaRamps(device, 50, 50, 100);

            // Only device 1 gets a game applied this round - device 2 is not touched by this
            // application at all here.
            DeviceGammaRampHelper.ApplyGameGammaRamp(device, device1Name, 60, 40, 120);

            // Device 2 changes externally while device 1's game is still running - f.lux shifting
            // it at sunset, independent of vibranceGUI entirely.
            DeviceGammaRampHelper.GammaRamp externalChangeOnDevice2 = BuildIdentityGammaRamp();
            externalChangeOnDevice2.Blue[200] = 12345; // deliberately distinct from trueBaseline2's identity value there
            device.SetMonitor(device2Name, externalChangeOnDevice2);

            // Device 1's game exits - only device 1 should be restored.
            DeviceGammaRampHelper.RestoreCapturedGammaRamps(device, 50, 50, 100);

            string mismatch1;
            bool device1Restored = !TryDescribeGammaRampMismatch(trueBaseline1, device.GetMonitor(device1Name), out mismatch1);
            checklist.Check(device1Restored, device1Restored
                ? "the device that actually had a game exit is restored to its true baseline"
                : "the device that actually had a game exit is restored to its true baseline - " + mismatch1);

            bool device2Untouched = GammaRampsBitIdentical(externalChangeOnDevice2, device.GetMonitor(device2Name));
            checklist.Check(device2Untouched,
                "the other device, never applied to in this round, is left exactly as its external change set it - not stomped back to its old captured baseline");

            checklist.Lines.Add(string.Empty);
        }

        // ------------------------------------------------------------------
        // Hardware half - writes to a real monitor. Only reachable via --selftest-gamma-display.
        // ------------------------------------------------------------------

        private static void RunHardwareChecks(Checklist checklist)
        {
            checklist.Lines.Add("Hardware round trip (writes to a real display, always restored):");

            // Picking the target has to happen before the confirmation dialog below, since the
            // dialog names the exact DeviceName about to be written - nothing is written by
            // picking it, so this does not violate "no write before the user has agreed".
            Screen target = PickHardwareTarget(checklist);
            if (target == null)
            {
                checklist.Lines.Add(string.Empty);
                return;
            }

            DialogResult confirmation = MessageBox.Show(
                string.Format(
                    "This self test is about to write a probe gamma ramp to display {0} and then " +
                    "restore it.\n\nIf it fails partway through, that display can be left dark or " +
                    "discoloured until you log off and back on - logging off and back on resets the " +
                    "gamma ramp.\n\nContinue?",
                    target.DeviceName),
                "vibranceGUI gamma restore hardware self test",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (confirmation != DialogResult.Yes)
            {
                checklist.Skip(string.Format("hardware round trip on {0} - declined by the user", target.DeviceName));
                checklist.Lines.Add(string.Empty);
                return;
            }

            // Never proceed without a known-good undo: if the current ramp cannot be read back, or
            // does not look plausible, there is nothing safe to restore to and the hardware half
            // must not touch this display at all.
            DeviceGammaRampHelper.GammaRamp original;
            if (!DeviceGammaRampHelper.TryGetGammaRamp(target, out original) ||
                !DeviceGammaRampHelper.IsPlausibleGammaRamp(original))
            {
                checklist.Skip(string.Format(
                    "hardware round trip on {0} - could not read a plausible original ramp to guarantee an undo",
                    target.DeviceName));
                checklist.Lines.Add(string.Empty);
                return;
            }

            try
            {
                RunHardwareBody(checklist, target);
            }
            finally
            {
                RestoreOriginal(checklist, target, original);
            }

            checklist.Lines.Add(string.Empty);
        }

        // First non-primary screen, so a mistake here cannot black out the display the user is
        // actually watching this dialog on. Only falls back to PrimaryScreen when there is no
        // other display to prefer.
        private static Screen PickHardwareTarget(Checklist checklist)
        {
            Screen[] allScreens = Screen.AllScreens;
            if (allScreens.Length == 0)
            {
                checklist.Skip("hardware round trip - Screen.AllScreens reported no displays");
                return null;
            }

            if (allScreens.Length == 1)
            {
                checklist.Lines.Add(string.Format(
                    "Only one display is attached ({0}); using it because there is no non-primary display to prefer.",
                    allScreens[0].DeviceName));
                return allScreens[0];
            }

            foreach (Screen screen in allScreens)
            {
                if (!screen.Primary)
                {
                    checklist.Lines.Add(string.Format(
                        "{0} displays attached; using the non-primary display {1} so a mistake here cannot black out the primary display.",
                        allScreens.Length, screen.DeviceName));
                    return screen;
                }
            }

            // Windows only ever marks one display Primary, so with more than one screen attached a
            // non-primary entry always exists above - unreachable in practice.
            checklist.Lines.Add(string.Format(
                "No non-primary display found among {0} screens; falling back to PrimaryScreen ({1}).",
                allScreens.Length, Screen.PrimaryScreen.DeviceName));
            return Screen.PrimaryScreen;
        }

        private static void RunHardwareBody(Checklist checklist, Screen target)
        {
            DeviceGammaRampHelper.GammaRamp blueReduced = BuildBlueReducedGammaRamp();

            // Step 5: write the probe ramp directly - deliberately bypassing DeviceGammaRampHelper's
            // own composition - so the code under test is then exercised against a known, verified
            // ground truth rather than against a ramp it wrote itself. A driver that clamps or
            // rejects this is Windows GDI gamma clamping (HKLM\...\ICM\GdiIcmGammaRange), not a
            // defect here - stop the hardware half rather than "fixing" ComposeGammaRamp to chase it.
            if (!RawWriteGammaRamp(target.DeviceName, blueReduced))
            {
                checklist.Skip(string.Format(
                    "hardware round trip on {0} - the display driver rejected the probe ramp outright",
                    target.DeviceName));
                return;
            }

            DeviceGammaRampHelper.GammaRamp readBackProbe;
            string probeMismatch;
            if (!DeviceGammaRampHelper.TryGetGammaRamp(target, out readBackProbe) ||
                TryDescribeGammaRampMismatch(blueReduced, readBackProbe, out probeMismatch))
            {
                checklist.Skip(string.Format(
                    "hardware round trip on {0} - the display driver clamped or rejected the probe ramp",
                    target.DeviceName));
                return;
            }

            // Step 6.
            bool applied = DeviceGammaRampHelper.ApplyGameGammaRamp(target, 60, 40, 120);
            checklist.Check(applied, "ApplyGameGammaRamp(target, 60, 40, 120) returns true (a baseline was captured and the write landed)");
            if (!applied)
            {
                return;
            }

            DeviceGammaRampHelper.GammaRamp afterApply;
            if (!DeviceGammaRampHelper.TryGetGammaRamp(target, out afterApply))
            {
                checklist.Check(false, "read back the ramp after ApplyGameGammaRamp(60,40,120)");
                return;
            }

            DeviceGammaRampHelper.GammaRamp expectedAfterApply = DeviceGammaRampHelper.ComposeGammaRamp(
                blueReduced, DeviceGammaRampHelper.CalculateLUT(0.6, 0.4, 1.2));
            string applyMismatch;
            bool appliedMatches = !TryDescribeGammaRampMismatch(expectedAfterApply, afterApply, out applyMismatch);
            checklist.Check(appliedMatches, appliedMatches
                ? "readback after ApplyGameGammaRamp(60,40,120) == ComposeGammaRamp(blueReduced, CalculateLUT(0.6,0.4,1.2))"
                : "readback after ApplyGameGammaRamp(60,40,120) == ComposeGammaRamp(blueReduced, CalculateLUT(0.6,0.4,1.2)) - " + applyMismatch);

            bool stillDiffersFromBlueReduced = !GammaRampsBitIdentical(blueReduced, afterApply);
            checklist.Check(stillDiffersFromBlueReduced,
                "readback after apply is NOT equal to blueReduced (the ingame level actually changed something)");

            // Step 7 - the headline assertion.
            DeviceGammaRampHelper.RestoreCapturedGammaRamps(50, 50, 100);
            DeviceGammaRampHelper.GammaRamp afterRestore;
            if (!DeviceGammaRampHelper.TryGetGammaRamp(target, out afterRestore))
            {
                checklist.Check(false, "read back the ramp after RestoreCapturedGammaRamps(50,50,100)");
                return;
            }

            string restoreMismatch;
            bool restoredExactly = !TryDescribeGammaRampMismatch(blueReduced, afterRestore, out restoreMismatch);
            checklist.Check(restoredExactly, restoredExactly
                ? "RestoreCapturedGammaRamps(50,50,100) reproduces blueReduced bit for bit across all 768 entries"
                : "RestoreCapturedGammaRamps(50,50,100) reproduces blueReduced bit for bit across all 768 entries - " + restoreMismatch);

            // Step 8 - the sliders still work after a restore.
            bool appliedAgain = DeviceGammaRampHelper.ApplyGameGammaRamp(target, 60, 40, 120);
            checklist.Check(appliedAgain, "a second ApplyGameGammaRamp(target, 60, 40, 120) returns true");
            if (!appliedAgain)
            {
                return;
            }

            DeviceGammaRampHelper.RestoreCapturedGammaRamps(55, 50, 100);
            DeviceGammaRampHelper.GammaRamp afterSecondRestore;
            if (!DeviceGammaRampHelper.TryGetGammaRamp(target, out afterSecondRestore))
            {
                checklist.Check(false, "read back the ramp after RestoreCapturedGammaRamps(55,50,100)");
                return;
            }

            DeviceGammaRampHelper.GammaRamp expectedAfterSecondRestore = DeviceGammaRampHelper.ComposeGammaRamp(
                blueReduced, DeviceGammaRampHelper.CalculateLUT(0.55, 0.5, 1.0));
            string secondRestoreMismatch;
            bool secondRestoreMatches = !TryDescribeGammaRampMismatch(expectedAfterSecondRestore, afterSecondRestore, out secondRestoreMismatch);
            checklist.Check(secondRestoreMatches, secondRestoreMatches
                ? "RestoreCapturedGammaRamps(55,50,100) == ComposeGammaRamp(blueReduced, CalculateLUT(0.55,0.5,1.0))"
                : "RestoreCapturedGammaRamps(55,50,100) == ComposeGammaRamp(blueReduced, CalculateLUT(0.55,0.5,1.0)) - " + secondRestoreMismatch);

            bool secondRestoreDiffersFromBlueReduced = !GammaRampsBitIdentical(blueReduced, afterSecondRestore);
            checklist.Check(secondRestoreDiffersFromBlueReduced,
                "RestoreCapturedGammaRamps(55,50,100) result is NOT equal to blueReduced (the sliders still have an effect)");

            // Step 9 - a second restore call with nothing left captured must be a no-op.
            DeviceGammaRampHelper.RestoreCapturedGammaRamps(1, 2, 3);
            DeviceGammaRampHelper.GammaRamp afterNoopRestore;
            if (!DeviceGammaRampHelper.TryGetGammaRamp(target, out afterNoopRestore))
            {
                checklist.Check(false, "read back the ramp after the no-op RestoreCapturedGammaRamps call");
                return;
            }

            string noopMismatch;
            bool noopHeld = !TryDescribeGammaRampMismatch(afterSecondRestore, afterNoopRestore, out noopMismatch);
            checklist.Check(noopHeld, noopHeld
                ? "a second RestoreCapturedGammaRamps call after the dictionary is empty changes nothing on the display"
                : "a second RestoreCapturedGammaRamps call after the dictionary is empty changes nothing on the display - " + noopMismatch);
        }

        // Writes original back unconditionally, verifies by reading it back, retries once on
        // mismatch, and if it still mismatches tells the user directly rather than leaving them to
        // discover a wrong display on their own. Must not be able to throw past itself - a
        // half-restored display with a swallowed exception is worse than one reported honestly.
        private static void RestoreOriginal(Checklist checklist, Screen target, DeviceGammaRampHelper.GammaRamp original)
        {
            try
            {
                RawWriteGammaRamp(target.DeviceName, original);

                DeviceGammaRampHelper.GammaRamp readBack;
                bool ok = DeviceGammaRampHelper.TryGetGammaRamp(target, out readBack);
                bool matches = ok && GammaRampsBitIdentical(original, readBack);

                if (!matches)
                {
                    RawWriteGammaRamp(target.DeviceName, original);
                    ok = DeviceGammaRampHelper.TryGetGammaRamp(target, out readBack);
                    matches = ok && GammaRampsBitIdentical(original, readBack);
                }

                if (matches)
                {
                    checklist.Lines.Add(string.Format("Restored the original gamma ramp on {0}.", target.DeviceName));
                }
                else
                {
                    MessageBox.Show(
                        string.Format(
                            "vibranceGUI could not confirm that {0}'s original gamma ramp was restored " +
                            "after this self test.\n\nPlease log off and back on to reset the display's " +
                            "gamma ramp to normal.",
                            target.DeviceName),
                        "vibranceGUI gamma restore hardware self test",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    checklist.Lines.Add(string.Format(
                        "[ERROR] could not confirm {0}'s original gamma ramp was restored - see the dialog shown",
                        target.DeviceName));
                }
            }
            catch (Exception ex)
            {
                try
                {
                    MessageBox.Show(
                        string.Format(
                            "vibranceGUI hit an unexpected error while restoring {0}'s original gamma ramp: " +
                            "{1}\n\nPlease log off and back on to reset the display's gamma ramp to normal.",
                            target.DeviceName, ex.Message),
                        "vibranceGUI gamma restore hardware self test",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch (Exception)
                {
                    // Even showing the dialog must not be allowed to throw past this finally block.
                }
            }
        }

        // ------------------------------------------------------------------
        // Shared helpers.
        // ------------------------------------------------------------------

        private static ushort[] BuildIdentityChannel()
        {
            ushort[] channel = new ushort[DeviceGammaRampHelper.GAMMA_RAMP_SIZE];
            for (int i = 0; i < channel.Length; i++)
            {
                channel[i] = (ushort)(i * 257);
            }
            return channel;
        }

        private static DeviceGammaRampHelper.GammaRamp BuildIdentityGammaRamp()
        {
            return new DeviceGammaRampHelper.GammaRamp(BuildIdentityChannel(), BuildIdentityChannel(), BuildIdentityChannel());
        }

        // Red[i] = Green[i] = i*257 (identity), Blue[i] = i*257*85/100 - asymmetric per channel, so
        // CalculateLUT, which always writes the same curve to all three channels, can never produce
        // this on its own. Stands in for a real ICC profile or hardware calibration that reduces
        // blue.
        private static DeviceGammaRampHelper.GammaRamp BuildBlueReducedGammaRamp()
        {
            ushort[] blue = new ushort[DeviceGammaRampHelper.GAMMA_RAMP_SIZE];
            for (int i = 0; i < blue.Length; i++)
            {
                blue[i] = (ushort)(i * 257 * 85 / 100);
            }
            return new DeviceGammaRampHelper.GammaRamp(BuildIdentityChannel(), BuildIdentityChannel(), blue);
        }

        private static bool TryFindFirstIdentityMismatch(ushort[] channel, out int index)
        {
            for (int i = 0; i < channel.Length; i++)
            {
                if (channel[i] != (ushort)(i * 257))
                {
                    index = i;
                    return false;
                }
            }
            index = -1;
            return true;
        }

        private static bool TryFindMismatch(ushort[] expected, ushort[] actual, out int index)
        {
            index = -1;
            if (expected == null || actual == null || expected.Length != actual.Length)
            {
                return true;
            }
            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i] != actual[i])
                {
                    index = i;
                    return true;
                }
            }
            return false;
        }

        // Reports the first differing index rather than just false, per the design's explicit ask.
        private static bool TryDescribeGammaRampMismatch(DeviceGammaRampHelper.GammaRamp expected, DeviceGammaRampHelper.GammaRamp actual, out string description)
        {
            int index;
            if (TryFindMismatch(expected.Red, actual.Red, out index))
            {
                description = index >= 0
                    ? string.Format("Red[{0}] differs: expected {1}, got {2}", index, expected.Red[index], actual.Red[index])
                    : "Red channel length differs";
                return true;
            }
            if (TryFindMismatch(expected.Green, actual.Green, out index))
            {
                description = index >= 0
                    ? string.Format("Green[{0}] differs: expected {1}, got {2}", index, expected.Green[index], actual.Green[index])
                    : "Green channel length differs";
                return true;
            }
            if (TryFindMismatch(expected.Blue, actual.Blue, out index))
            {
                description = index >= 0
                    ? string.Format("Blue[{0}] differs: expected {1}, got {2}", index, expected.Blue[index], actual.Blue[index])
                    : "Blue channel length differs";
                return true;
            }
            description = null;
            return false;
        }

        private static bool GammaRampsBitIdentical(DeviceGammaRampHelper.GammaRamp expected, DeviceGammaRampHelper.GammaRamp actual)
        {
            string description;
            return !TryDescribeGammaRampMismatch(expected, actual, out description);
        }

        // A raw write, deliberately independent of DeviceGammaRampHelper's own CreateDC/SetDeviceGammaRamp
        // path, so the hardware half can establish ground truth (step 5) and put back exactly what
        // was really on the display (the finally block) without going through the composition logic
        // that is itself under test.
        private static bool RawWriteGammaRamp(string deviceName, DeviceGammaRampHelper.GammaRamp ramp)
        {
            IntPtr hdc = RawGdi.CreateDC(deviceName, null, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                return RawGdi.SetDeviceGammaRamp(hdc, ref ramp);
            }
            finally
            {
                RawGdi.DeleteDC(hdc);
            }
        }

        // Stands in for real monitors: an in-memory dictionary keyed by (fake) DeviceName, so the
        // session logic in DeviceGammaRampHelper - the part that let both B1 and S1 through review
        // and QA - can be driven through real apply/restore cycles, including forced failures,
        // without a display. Device names used by the checks above are chosen to never collide with
        // a real Screen.DeviceName ("\\.\DISPLAYn"), since DeviceGammaRampHelper's own dictionaries
        // are static and would otherwise be shared with any real hardware use in the same process.
        private class FakeGammaDevice : IGammaDevice
        {
            private readonly Dictionary<string, DeviceGammaRampHelper.GammaRamp> _monitors = new Dictionary<string, DeviceGammaRampHelper.GammaRamp>();
            private readonly HashSet<string> _failNextRead = new HashSet<string>();
            private readonly HashSet<string> _failNextWrite = new HashSet<string>();

            // Primes or externally changes a monitor's content directly, bypassing Write - stands
            // in for hot-plugging a different panel onto the same DeviceName, or an external color
            // change (an ICC profile switch, f.lux) landing mid-session. Stores a clone, matching
            // Write below - see TryRead for why.
            public void SetMonitor(string deviceName, DeviceGammaRampHelper.GammaRamp ramp)
            {
                _monitors[deviceName] = CloneGammaRamp(ramp);
            }

            public DeviceGammaRampHelper.GammaRamp GetMonitor(string deviceName)
            {
                return CloneGammaRamp(_monitors[deviceName]);
            }

            public void FailNextRead(string deviceName)
            {
                _failNextRead.Add(deviceName);
            }

            public void FailNextWrite(string deviceName)
            {
                _failNextWrite.Add(deviceName);
            }

            public bool TryRead(string deviceName, out DeviceGammaRampHelper.GammaRamp ramp)
            {
                if (_failNextRead.Remove(deviceName))
                {
                    ramp = FreshZeroedGammaRamp();
                    return false;
                }
                DeviceGammaRampHelper.GammaRamp stored;
                if (!_monitors.TryGetValue(deviceName, out stored))
                {
                    // Mirrors RealGammaDevice.TryRead, which always assigns freshly allocated,
                    // zeroed arrays before a possible failure - never a struct with null channels.
                    // A fake that is less safe than production here would be the wrong thing to
                    // learn from.
                    ramp = FreshZeroedGammaRamp();
                    return false;
                }
                // Cloned, not aliased - a caller that captures this as a baseline must not see it
                // change later just because this fake's own stored copy did (SetMonitor, or a later
                // Write), matching TryGetGammaRamp's documented "owned exclusively by the caller".
                ramp = CloneGammaRamp(stored);
                return true;
            }

            public bool Write(string deviceName, DeviceGammaRampHelper.GammaRamp ramp)
            {
                if (_failNextWrite.Remove(deviceName))
                {
                    return false;
                }
                // Stores a clone so a caller mutating its own copy afterward (ComposeGammaRamp's
                // result is never reused by its caller in practice, but nothing here should rely on
                // that) cannot reach back into this fake's state.
                _monitors[deviceName] = CloneGammaRamp(ramp);
                return true;
            }

            private static DeviceGammaRampHelper.GammaRamp CloneGammaRamp(DeviceGammaRampHelper.GammaRamp ramp)
            {
                return new DeviceGammaRampHelper.GammaRamp((ushort[])ramp.Red.Clone(), (ushort[])ramp.Green.Clone(), (ushort[])ramp.Blue.Clone());
            }

            private static DeviceGammaRampHelper.GammaRamp FreshZeroedGammaRamp()
            {
                return new DeviceGammaRampHelper.GammaRamp(
                    new ushort[DeviceGammaRampHelper.GAMMA_RAMP_SIZE],
                    new ushort[DeviceGammaRampHelper.GAMMA_RAMP_SIZE],
                    new ushort[DeviceGammaRampHelper.GAMMA_RAMP_SIZE]);
            }
        }

        private static class RawGdi
        {
            [DllImport("gdi32.dll")]
            public static extern IntPtr CreateDC(string lpszDriver, string lpszDevice, string lpszOutput, IntPtr lpInitData);

            [DllImport("gdi32.dll", EntryPoint = "DeleteDC")]
            public static extern bool DeleteDC([In] IntPtr hdc);

            [DllImport("gdi32.dll")]
            public static extern bool SetDeviceGammaRamp(IntPtr hDC, ref DeviceGammaRampHelper.GammaRamp lpRamp);
        }

        private class Checklist
        {
            public readonly List<string> Lines = new List<string>();
            public int Passed;
            public int Total;

            public void Check(bool condition, string description)
            {
                Total++;
                if (condition)
                    Passed++;
                Lines.Add(string.Format("[{0}] {1}", condition ? "PASS" : "FAIL", description));
            }

            // Deliberately not counted in Total/Passed - the same convention StabilityFixture uses
            // for a check whose precondition (a plausible original ramp, or the user's consent) was
            // never met, so it proves nothing either way and must not be folded into PASSED n/m.
            public void Skip(string description)
            {
                Lines.Add(string.Format("[SKIP] {0}", description));
            }
        }
    }
}
