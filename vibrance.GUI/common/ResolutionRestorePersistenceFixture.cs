using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace vibrance.GUI.common
{
    /// <summary>
    /// Regression coverage for D4's resolution half - the persisted restore that self-heals a
    /// stranded display's resolution after an abnormal exit, the same mechanism §9.8 already gives
    /// vibrance, applied to ResolutionHelper's own DEVMODE state instead. Covers the four pieces
    /// that make it safe: RealResolutionRestoreStore's atomic write/torn-file handling (shared with
    /// vibrance's own store via AtomicXmlFile - Task 1), ResolutionRestoreHelper's write-iff-
    /// changed/delete-iff-drained/clear-iff-confirmed journaling rules (Task 3),
    /// ResolutionHelper.TryRestorePersistedMode's verify gate (Task 4), and
    /// ResolutionRestoreHelper.ReplayPersistedRestore's outer replay loop (Task 5) - all driven
    /// against a fixture-private temp file (never the real %APPDATA%\vibranceGUI\resolutionRestore.xml)
    /// and a fake IDisplayModeDevice (never a real GPU, never a real display write). Run by
    /// vibrance.GUI.exe --selftest-resolution-restore-persistence.
    ///
    /// A separate fixture from VibranceRestorePersistenceFixture and from ResolutionChangeFixture,
    /// deliberately: it is not vibrance (different record, different store, different file), and it
    /// is not the ongoing apply/revert cycle ResolutionChangeFixture already covers exhaustively
    /// (214 checks) - this is specifically the abnormal-exit persistence layered on top of it.
    ///
    /// Two checks need one real, currently attached, uniquely identifiable display to resolve a
    /// real MonitorIdentity id against - MonitorIdentity's own EnumDisplayDevices calls are
    /// read-only, so this never writes a resolution to anything real, but a machine with no
    /// attached display (a bare RDP session with no physical monitor, for instance) has nothing for
    /// them to resolve, so those two are Skipped rather than failed when that happens - the same
    /// accommodation VibranceRestorePersistenceFixture already makes for its own hardware-dependent
    /// checks.
    /// </summary>
    public static class ResolutionRestorePersistenceFixture
    {
        public static List<string> Run()
        {
            Checklist checklist = new Checklist();
            checklist.Lines.Add("vibranceGUI persisted resolution restore self test");
            checklist.Lines.Add(string.Empty);

            string tempDir = Path.Combine(Path.GetTempPath(), "vibranceGUI-resolution-restore-persistence-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                RunStoreChecks(checklist, tempDir);
                RunJournalingChecks(checklist);
                RunVerifyGateChecks(checklist);
                RunReplayChecks(checklist, tempDir);
            }
            finally
            {
                // Never leaves the shared static seams pointed at this fixture's own temp store -
                // mirrors VibranceRestorePersistenceFixture's own end-of-run discipline.
                ResolutionRestoreStore.ResetForTests(null);
                ResolutionRestoreHelper.ResetForTests();
                ResolutionHelper.ResetForTests();
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch (Exception)
                {
                    // Best-effort cleanup of our own temp directory - never fails the self test
                    // over a locked-handle race on Windows.
                }
            }

            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add(string.Format("PASSED {0}/{1}", checklist.Passed, checklist.Total));
            return checklist.Lines;
        }

        // ------------------------------------------------------------------
        // RealResolutionRestoreStore - the atomic write/read/delete seam itself (AtomicXmlFile
        // underneath, shared with RealVibranceRestoreStore - Task 1), pointed at a fixture-private
        // temp file throughout.
        // ------------------------------------------------------------------

        private static void RunStoreChecks(Checklist checklist, string tempDir)
        {
            checklist.Lines.Add("RealResolutionRestoreStore (fixture-private temp path):");

            CheckRoundTripWriteRead(checklist, tempDir);
            CheckTryReadReturnsNullWhenFileNeverWritten(checklist, tempDir);
            CheckTornFileIsDiscardedWithoutThrowing(checklist, tempDir);
            CheckDeleteIsNoOpOnMissingFileAndRemovesAnExistingOne(checklist, tempDir);

            checklist.Lines.Add(string.Empty);
        }

        // RR1. Mutation this guards: any lossy round trip through XmlSerializer - a flattened or
        // dropped nested ResolutionModeWrapper above all, since that is the one shape difference
        // from VibranceRestoreEntry's own (already-covered) round trip.
        private static void CheckRoundTripWriteRead(Checklist checklist, string tempDir)
        {
            string filePath = Path.Combine(tempDir, "roundtrip.xml");
            RealResolutionRestoreStore store = new RealResolutionRestoreStore(filePath);

            ResolutionRestoreRecord written = new ResolutionRestoreRecord();
            written.SchemaVersion = 1;
            written.Vendor = "NVIDIA";
            written.WrittenUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            written.Displays.Add(new ResolutionRestoreEntry
            {
                MonitorId = "TESTONLY-MID-A",
                DeviceNameAtWrite = "\\\\.\\DISPLAY1",
                AppliedMode = BuildTarget(1920, 1080, 32, 60, 0),
                WindowsMode = BuildTarget(2560, 1440, 32, 144, 1)
            });
            store.Write(written);
            ResolutionRestoreRecord readBack = store.TryRead();

            bool roundTripped = readBack != null && readBack.SchemaVersion == 1 && readBack.Vendor == "NVIDIA" &&
                readBack.Displays != null && readBack.Displays.Count == 1 &&
                readBack.Displays[0].MonitorId == "TESTONLY-MID-A" &&
                readBack.Displays[0].DeviceNameAtWrite == "\\\\.\\DISPLAY1" &&
                readBack.Displays[0].AppliedMode != null && readBack.Displays[0].AppliedMode.Equals(BuildTarget(1920, 1080, 32, 60, 0)) &&
                readBack.Displays[0].WindowsMode != null && readBack.Displays[0].WindowsMode.Equals(BuildTarget(2560, 1440, 32, 144, 1));
            checklist.Check(roundTripped,
                "RR1: a written record reads back with the same schema version, vendor and every display entry intact, including both nested ResolutionModeWrapper values in full (all five fields each)");
        }

        // RR2.
        private static void CheckTryReadReturnsNullWhenFileNeverWritten(Checklist checklist, string tempDir)
        {
            string filePath = Path.Combine(tempDir, "never-written.xml");
            RealResolutionRestoreStore store = new RealResolutionRestoreStore(filePath);

            checklist.Check(store.TryRead() == null, "RR2: TryRead() returns null when no file has ever been written at this path");
        }

        // RR3. Mutation this guards: let a torn/unparseable file throw into startup, or leave it in
        // place to keep failing the same parse on every future launch.
        private static void CheckTornFileIsDiscardedWithoutThrowing(Checklist checklist, string tempDir)
        {
            string filePath = Path.Combine(tempDir, "torn.xml");
            File.WriteAllText(filePath, "this is not valid xml <<< it was cut off mid-write");
            RealResolutionRestoreStore store = new RealResolutionRestoreStore(filePath);

            bool threw = false;
            ResolutionRestoreRecord result = null;
            try
            {
                result = store.TryRead();
            }
            catch (Exception)
            {
                threw = true;
            }

            checklist.Check(!threw && result == null,
                "RR3: a torn/unparseable file is discarded silently (null, no throw) - termination mid-write is exactly the scenario this feature exists to recover from, not an edge case");
            checklist.Check(!File.Exists(filePath),
                "RR3: the unparseable file is also deleted, so a future launch does not re-attempt and re-fail the same parse forever");
        }

        // RR4.
        private static void CheckDeleteIsNoOpOnMissingFileAndRemovesAnExistingOne(Checklist checklist, string tempDir)
        {
            string filePath = Path.Combine(tempDir, "delete-target.xml");
            RealResolutionRestoreStore store = new RealResolutionRestoreStore(filePath);

            bool threwOnMissing = false;
            try
            {
                store.Delete();
            }
            catch (Exception)
            {
                threwOnMissing = true;
            }
            checklist.Check(!threwOnMissing, "RR4: Delete() on a path with no file at all does not throw");

            store.Write(new ResolutionRestoreRecord());
            bool existedAfterWrite = File.Exists(filePath);
            store.Delete();
            checklist.Check(existedAfterWrite && !File.Exists(filePath), "RR4: Delete() removes a file that does exist");
        }

        // ------------------------------------------------------------------
        // ResolutionRestoreHelper's journaling rules - Task 3. A RecordingResolutionRestoreStore
        // (an in-memory call counter, not a real file) is enough here: these checks are about HOW
        // MANY TIMES the seam is called, not about file content, which the store checks above
        // already cover.
        // ------------------------------------------------------------------

        private static void RunJournalingChecks(Checklist checklist)
        {
            checklist.Lines.Add("ResolutionRestoreHelper journaling rules (write iff changed, delete iff drained, clear iff confirmed):");

            CheckNewDeviceWritesOnce(checklist);
            CheckSameValuesRepeatDoesNotWriteAgain(checklist);
            CheckDifferentWindowsModeOnSameDeviceWritesAgain(checklist);
            CheckNullWindowsModeIsSkipped(checklist);
            CheckClearingAnUntrackedDeviceDoesNoIo(checklist);
            CheckPartialDrainDoesNoDeleteThenFullDrainDeletesOnce(checklist);
            CheckShouldClearResolutionRestoreRecordOnlyTrueForConfirmedLanding(checklist);
            CheckSuppressedRevertLeavesTheRecordIntact(checklist);

            checklist.Lines.Add(string.Empty);
        }

        // RR5. Mirrors VibranceRestorePersistenceFixture's P5.
        private static void CheckNewDeviceWritesOnce(Checklist checklist)
        {
            ResolutionRestoreHelper.ResetForTests();
            RecordingResolutionRestoreStore store = new RecordingResolutionRestoreStore();
            ResolutionRestoreStore.ResetForTests(store);

            ResolutionRestoreHelper.RecordModeApplied("\\\\.\\DISPLAY_TESTONLY_RR5",
                BuildTarget(1920, 1080, 32, 60, 0), BuildTarget(2560, 1440, 32, 144, 0));

            checklist.Check(store.WriteCallCount == 1,
                string.Format("RR5: applying a mode to a NEW device writes the record exactly once, got {0}", store.WriteCallCount));
        }

        // RR6. The rule 1 check the brief calls out by name: a repeat apply must cost zero further
        // writes. Mutation this guards: reintroducing per-foreground-event I/O (issue #156's own
        // mistake, one layer further down than #156 itself was).
        private static void CheckSameValuesRepeatDoesNotWriteAgain(Checklist checklist)
        {
            ResolutionRestoreHelper.ResetForTests();
            RecordingResolutionRestoreStore store = new RecordingResolutionRestoreStore();
            ResolutionRestoreStore.ResetForTests(store);

            const string deviceName = "\\\\.\\DISPLAY_TESTONLY_RR6";
            ResolutionModeWrapper applied = BuildTarget(1920, 1080, 32, 60, 0);
            ResolutionModeWrapper windows = BuildTarget(2560, 1440, 32, 144, 0);
            ResolutionRestoreHelper.RecordModeApplied(deviceName, applied, windows);
            int afterFirst = store.WriteCallCount;
            // Three more "applies" of the identical (appliedMode, windowsMode) pair - what every
            // alt-tab back into an already-correct, already-focused game produces in production.
            // Fresh ResolutionModeWrapper instances each time, deliberately - the comparison must be
            // by VALUE (Equals), not by reference.
            ResolutionRestoreHelper.RecordModeApplied(deviceName, BuildTarget(1920, 1080, 32, 60, 0), BuildTarget(2560, 1440, 32, 144, 0));
            ResolutionRestoreHelper.RecordModeApplied(deviceName, BuildTarget(1920, 1080, 32, 60, 0), BuildTarget(2560, 1440, 32, 144, 0));
            ResolutionRestoreHelper.RecordModeApplied(deviceName, BuildTarget(1920, 1080, 32, 60, 0), BuildTarget(2560, 1440, 32, 144, 0));

            checklist.Check(afterFirst == 1 && store.WriteCallCount == 1,
                string.Format("RR6: re-applying the SAME (AppliedMode, WindowsMode) pair to an already-tracked device writes nothing further, got {0} total write(s) after 4 applies", store.WriteCallCount));
        }

        // RR7.
        private static void CheckDifferentWindowsModeOnSameDeviceWritesAgain(Checklist checklist)
        {
            ResolutionRestoreHelper.ResetForTests();
            RecordingResolutionRestoreStore store = new RecordingResolutionRestoreStore();
            ResolutionRestoreStore.ResetForTests(store);

            const string deviceName = "\\\\.\\DISPLAY_TESTONLY_RR7";
            ResolutionModeWrapper applied = BuildTarget(1920, 1080, 32, 60, 0);
            ResolutionRestoreHelper.RecordModeApplied(deviceName, applied, BuildTarget(2560, 1440, 32, 144, 0));
            // A WindowsResolutionRefresher.Refresh in between could capture a DIFFERENT desktop mode
            // for the same device (a user changing their own desktop resolution mid-game) - this
            // must still count as "the set changed", even with AppliedMode unchanged.
            ResolutionRestoreHelper.RecordModeApplied(deviceName, applied, BuildTarget(3840, 2160, 32, 60, 0));

            checklist.Check(store.WriteCallCount == 2,
                string.Format("RR7: the SAME device with a DIFFERENT WindowsMode writes again, got {0} write(s)", store.WriteCallCount));
        }

        // RR8. Matches RestoreOnExit's own existing "saved.Item1 == null" guard.
        private static void CheckNullWindowsModeIsSkipped(Checklist checklist)
        {
            ResolutionRestoreHelper.ResetForTests();
            RecordingResolutionRestoreStore store = new RecordingResolutionRestoreStore();
            ResolutionRestoreStore.ResetForTests(store);

            ResolutionRestoreHelper.RecordModeApplied("\\\\.\\DISPLAY_TESTONLY_RR8", BuildTarget(1920, 1080, 32, 60, 0), null);

            checklist.Check(store.WriteCallCount == 0 && ResolutionRestoreHelper.HoldingCount == 0,
                string.Format("RR8: a call with a null WindowsMode is skipped entirely - no write, not tracked - got {0} write(s), HoldingCount={1}", store.WriteCallCount, ResolutionRestoreHelper.HoldingCount));
        }

        // RR9. Mirrors VibranceRestorePersistenceFixture's P9.
        private static void CheckClearingAnUntrackedDeviceDoesNoIo(Checklist checklist)
        {
            ResolutionRestoreHelper.ResetForTests();
            RecordingResolutionRestoreStore store = new RecordingResolutionRestoreStore();
            ResolutionRestoreStore.ResetForTests(store);

            ResolutionRestoreHelper.ClearModeRecord("\\\\.\\DISPLAY_TESTONLY_RR9_NEVER_TRACKED");

            checklist.Check(store.DeleteCallCount == 0,
                "RR9: clearing a device that was never tracked calls Delete() zero times");
        }

        // RR10. The rule 2 check the brief calls out by name: a PARTIAL drain (two tracked devices,
        // one cleared) must do no I/O; only the drain that empties the set may delete.
        private static void CheckPartialDrainDoesNoDeleteThenFullDrainDeletesOnce(Checklist checklist)
        {
            ResolutionRestoreHelper.ResetForTests();
            RecordingResolutionRestoreStore store = new RecordingResolutionRestoreStore();
            ResolutionRestoreStore.ResetForTests(store);

            const string d1 = "\\\\.\\DISPLAY_TESTONLY_RR10_D1";
            const string d2 = "\\\\.\\DISPLAY_TESTONLY_RR10_D2";
            ResolutionRestoreHelper.RecordModeApplied(d1, BuildTarget(1920, 1080, 32, 60, 0), BuildTarget(2560, 1440, 32, 144, 0));
            ResolutionRestoreHelper.RecordModeApplied(d2, BuildTarget(1280, 720, 32, 60, 0), BuildTarget(1920, 1080, 32, 60, 0));

            ResolutionRestoreHelper.ClearModeRecord(d1);
            checklist.Check(store.DeleteCallCount == 0,
                string.Format("RR10: a PARTIAL drain (one of two tracked devices cleared) does no I/O, got {0} Delete() call(s)", store.DeleteCallCount));

            ResolutionRestoreHelper.ClearModeRecord(d2);
            checklist.Check(store.DeleteCallCount == 1,
                string.Format("RR10: the set becoming empty on THIS call deletes the record exactly once, got {0} Delete() call(s)", store.DeleteCallCount));
        }

        // RR11. The predicate rule 4 is built on, pinned directly against every
        // ResolutionChangeResult value - the same reasoning P21 (VibranceRestorePersistenceFixture)
        // gives for testing ShouldJournalGameLevel as a pure function.
        private static void CheckShouldClearResolutionRestoreRecordOnlyTrueForConfirmedLanding(Checklist checklist)
        {
            checklist.Check(ResolutionRestoreHelper.ShouldClearResolutionRestoreRecord(ResolutionHelper.ResolutionChangeResult.Applied),
                "RR11: Applied confirms the revert landed - clears the record");
            checklist.Check(ResolutionRestoreHelper.ShouldClearResolutionRestoreRecord(ResolutionHelper.ResolutionChangeResult.AlreadyMatching),
                "RR11: AlreadyMatching confirms the desktop is already at the restore target - clears the record");
            checklist.Check(!ResolutionRestoreHelper.ShouldClearResolutionRestoreRecord(ResolutionHelper.ResolutionChangeResult.Failed),
                "RR11: Failed never clears the record - nothing landed");
            checklist.Check(!ResolutionRestoreHelper.ShouldClearResolutionRestoreRecord(ResolutionHelper.ResolutionChangeResult.AppliedUnverified),
                "RR11: AppliedUnverified never clears the record - the readback did not confirm it");
            checklist.Check(!ResolutionRestoreHelper.ShouldClearResolutionRestoreRecord(ResolutionHelper.ResolutionChangeResult.Suppressed),
                "RR11 (the trap): Suppressed never clears the record, even though it clears isResolutionChangeApplied - the mode was never actually put back");
        }

        // RR18 in the brief's own numbering scheme (the last of the "cases at minimum" it lists by
        // name): a full, concrete simulation of a proxy's own revert-branch pattern -
        // "if (ShouldClearResolutionRestoreRecord(result)) ClearModeRecord(deviceName);" - proving
        // the record actually SURVIVES a Suppressed result and is actually CLEARED on a subsequent
        // Applied one. Must fail if either the predicate above or a proxy's own call-site guard is
        // ever "simplified" to clear on every revert.
        private static void CheckSuppressedRevertLeavesTheRecordIntact(Checklist checklist)
        {
            ResolutionRestoreHelper.ResetForTests();
            RecordingResolutionRestoreStore store = new RecordingResolutionRestoreStore();
            ResolutionRestoreStore.ResetForTests(store);

            const string deviceName = "\\\\.\\DISPLAY_TESTONLY_RR18";
            ResolutionRestoreHelper.RecordModeApplied(deviceName, BuildTarget(1920, 1080, 32, 60, 0), BuildTarget(2560, 1440, 32, 144, 0));
            checklist.Check(store.WriteCallCount == 1 && ResolutionRestoreHelper.HoldingCount == 1,
                "RR18 setup: the device is tracked and persisted once before the simulated revert");

            // The give-up state - the give-up bound was reached, so ChangeResolutionEx never even
            // touched the driver this time.
            if (ResolutionRestoreHelper.ShouldClearResolutionRestoreRecord(ResolutionHelper.ResolutionChangeResult.Suppressed))
            {
                ResolutionRestoreHelper.ClearModeRecord(deviceName);
            }
            checklist.Check(store.DeleteCallCount == 0 && ResolutionRestoreHelper.HoldingCount == 1,
                string.Format("RR18: a Suppressed revert result leaves the persisted record INTACT - the display is still owed a restore that a future relaunch must retry, got {0} Delete() call(s), HoldingCount={1}", store.DeleteCallCount, ResolutionRestoreHelper.HoldingCount));

            // Now a real success - the record must actually clear, proving RR18 above is not simply
            // a no-op ClearModeRecord path that would "pass" regardless of the guard.
            if (ResolutionRestoreHelper.ShouldClearResolutionRestoreRecord(ResolutionHelper.ResolutionChangeResult.Applied))
            {
                ResolutionRestoreHelper.ClearModeRecord(deviceName);
            }
            checklist.Check(store.DeleteCallCount == 1 && ResolutionRestoreHelper.HoldingCount == 0,
                string.Format("RR18: a subsequent Applied result DOES clear the record, got {0} Delete() call(s), HoldingCount={1}", store.DeleteCallCount, ResolutionRestoreHelper.HoldingCount));
        }

        // ------------------------------------------------------------------
        // ResolutionHelper.TryRestorePersistedMode - Task 4's verify gate. Entirely fake-driven:
        // unlike the replay's outer loop (RunReplayChecks below), this method never touches
        // MonitorIdentity, so all five outcomes are reachable with no real display involved at all.
        // ------------------------------------------------------------------

        private static void RunVerifyGateChecks(Checklist checklist)
        {
            checklist.Lines.Add("ResolutionHelper.TryRestorePersistedMode (verify gate, all five outcomes):");

            ResolutionHelper.ResetForTests();

            CheckGateUnreadable(checklist);
            CheckGateAlreadyCorrect(checklist);
            CheckGateRestored(checklist);
            CheckGateWriteFailed(checklist);
            CheckGateNotOurs(checklist);

            checklist.Lines.Add(string.Empty);
        }

        // RR12 (Unreadable).
        private static void CheckGateUnreadable(Checklist checklist)
        {
            ResolutionHelper.ResetForTests();
            const string deviceName = "DISPLAY_TESTONLY_RR12";
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetUnreadable(deviceName);

            ResolutionHelper.PersistedRestoreOutcome outcome = ResolutionHelper.TryRestorePersistedMode(
                device, deviceName, BuildTarget(1920, 1080, 32, 60, 0), BuildTarget(2560, 1440, 32, 144, 0));

            checklist.Check(outcome == ResolutionHelper.PersistedRestoreOutcome.Unreadable,
                string.Format("RR12: an unreadable current mode returns Unreadable, got {0}", outcome));
            checklist.Check(device.ChangeModeCalls.Count == 0, "RR12: no write is even attempted when the current mode cannot be read");
        }

        // RR13 (AlreadyCorrect) - tested FIRST, so a display already at the restore target is never
        // misreported as changed-by-hand, even in the degenerate case explored by RR13b below.
        private static void CheckGateAlreadyCorrect(Checklist checklist)
        {
            ResolutionHelper.ResetForTests();
            const string deviceName = "DISPLAY_TESTONLY_RR13";
            ResolutionModeWrapper applied = BuildTarget(1920, 1080, 32, 60, 0);
            ResolutionModeWrapper windows = BuildTarget(2560, 1440, 32, 144, 0);
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(deviceName, windows); // already at the Windows mode, not the stale AppliedMode

            ResolutionHelper.PersistedRestoreOutcome outcome = ResolutionHelper.TryRestorePersistedMode(device, deviceName, applied, windows);

            checklist.Check(outcome == ResolutionHelper.PersistedRestoreOutcome.AlreadyCorrect,
                string.Format("RR13: a display already at WindowsMode returns AlreadyCorrect, got {0}", outcome));
            checklist.Check(device.ChangeModeCalls.Count == 0, "RR13: nothing is written when the display is already correct");
        }

        // RR14 (Restored) - "still ours, write lands".
        private static void CheckGateRestored(Checklist checklist)
        {
            ResolutionHelper.ResetForTests();
            const string deviceName = "DISPLAY_TESTONLY_RR14";
            ResolutionModeWrapper applied = BuildTarget(1920, 1080, 32, 60, 0);
            ResolutionModeWrapper windows = BuildTarget(2560, 1440, 32, 144, 0);
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(deviceName, applied); // still exactly at the persisted game mode

            ResolutionHelper.PersistedRestoreOutcome outcome = ResolutionHelper.TryRestorePersistedMode(device, deviceName, applied, windows);

            checklist.Check(outcome == ResolutionHelper.PersistedRestoreOutcome.Restored,
                string.Format("RR14: a display still at AppliedMode is restored to WindowsMode, got {0}", outcome));
            checklist.Check(windows.MatchesAchievedMode(device.GetCurrentMode(deviceName)),
                "RR14: the fake device is now at the Windows mode");
        }

        // RR15 (WriteFailed) - verified still ours, but the revert itself does not land.
        private static void CheckGateWriteFailed(Checklist checklist)
        {
            ResolutionHelper.ResetForTests();
            const string deviceName = "DISPLAY_TESTONLY_RR15";
            ResolutionModeWrapper applied = BuildTarget(1920, 1080, 32, 60, 0);
            ResolutionModeWrapper windows = BuildTarget(2560, 1440, 32, 144, 0);
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(deviceName, applied);
            device.FailNextChange(deviceName);

            ResolutionHelper.PersistedRestoreOutcome outcome = ResolutionHelper.TryRestorePersistedMode(device, deviceName, applied, windows);

            checklist.Check(outcome == ResolutionHelper.PersistedRestoreOutcome.WriteFailed,
                string.Format("RR15: a display still at AppliedMode whose revert fails returns WriteFailed (not just Failed - the entry must be kept), got {0}", outcome));
            checklist.Check(device.ChangeModeCalls.Count > 0, "RR15: a write WAS attempted (the display was verified still ours) even though it did not land");
            checklist.Check(applied.MatchesAchievedMode(device.GetCurrentMode(deviceName)),
                "RR15: the fake device's mode is unchanged by the failed attempt");
        }

        // RR16 (NotOurs) - "the user changed it by hand" - the deliberately strict row: dropped, no
        // write, and the display's own live mode is left exactly as the "user" set it.
        private static void CheckGateNotOurs(Checklist checklist)
        {
            ResolutionHelper.ResetForTests();
            const string deviceName = "DISPLAY_TESTONLY_RR16";
            ResolutionModeWrapper applied = BuildTarget(1920, 1080, 32, 60, 0);
            ResolutionModeWrapper windows = BuildTarget(2560, 1440, 32, 144, 0);
            ResolutionModeWrapper handChanged = BuildTarget(1680, 1050, 32, 75, 0); // neither applied nor windows
            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(deviceName, handChanged);

            ResolutionHelper.PersistedRestoreOutcome outcome = ResolutionHelper.TryRestorePersistedMode(device, deviceName, applied, windows);

            checklist.Check(outcome == ResolutionHelper.PersistedRestoreOutcome.NotOurs,
                string.Format("RR16: a display at neither AppliedMode nor WindowsMode returns NotOurs, got {0}", outcome));
            checklist.Check(device.ChangeModeCalls.Count == 0, "RR16: nothing is written - the display is left ALONE, not forced to either mode");
            checklist.Check(handChanged.MatchesAchievedMode(device.GetCurrentMode(deviceName)), "RR16: the display's own live mode is unchanged by the replay");
        }

        // ------------------------------------------------------------------
        // ResolutionRestoreHelper.ReplayPersistedRestore - Task 5's outer loop: MonitorIdentity
        // resolution, survivor bookkeeping, and the rewrite-or-delete decision. A RealResolutionRestoreStore
        // (fixture-private temp path) throughout, and a fake IDisplayModeDevice.
        // ------------------------------------------------------------------

        private static void RunReplayChecks(Checklist checklist, string tempDir)
        {
            checklist.Lines.Add("ResolutionRestoreHelper.ReplayPersistedRestore (outer loop):");

            CheckReplayDropsAnUnattachedMonitorWithNoWrite(checklist, tempDir);

            string realDeviceName = Screen.AllScreens.Length > 0 ? Screen.AllScreens[0].DeviceName : null;
            string realMonitorId = string.IsNullOrEmpty(realDeviceName) ? null : MonitorIdentity.TryGetMonitorId(realDeviceName);
            if (string.IsNullOrEmpty(realMonitorId))
            {
                checklist.Skip("RR19-RR20: need one real, currently attached, uniquely identifiable display to resolve a MonitorIdentity id against - none was found on this machine");
            }
            else
            {
                CheckReplayKeepsAndRewritesAnUnreadableEntry(checklist, tempDir, realDeviceName, realMonitorId);
                CheckReplayDeletesOnceAnAlreadyCorrectEntrySettles(checklist, tempDir, realDeviceName, realMonitorId);
            }

            checklist.Lines.Add(string.Empty);
        }

        // RR17. The monitor-not-attached row, at the outer-loop level - a fabricated MonitorId that
        // can never resolve on any machine, so this needs no real hardware at all. Mutation this
        // guards: falling back to DeviceNameAtWrite when MonitorId does not resolve, which
        // MonitorIdentity's own header warns can end up naming a DIFFERENT physical panel after an
        // unplug.
        private static void CheckReplayDropsAnUnattachedMonitorWithNoWrite(Checklist checklist, string tempDir)
        {
            string filePath = Path.Combine(tempDir, "rr17-unattached.xml");
            RealResolutionRestoreStore store = new RealResolutionRestoreStore(filePath);
            ResolutionRestoreRecord record = new ResolutionRestoreRecord();
            record.SchemaVersion = 1;
            record.Vendor = "NVIDIA";
            const string fabricatedMonitorId = "\\\\?\\DISPLAY#TESTONLY0#5&aaaaaaa&0&UID9999#{deadbeef-dead-beef-dead-beefdeadbeef}";
            record.Displays.Add(new ResolutionRestoreEntry
            {
                MonitorId = fabricatedMonitorId,
                DeviceNameAtWrite = "\\\\.\\DISPLAY9",
                AppliedMode = BuildTarget(1920, 1080, 32, 60, 0),
                WindowsMode = BuildTarget(2560, 1440, 32, 144, 0)
            });
            store.Write(record);
            ResolutionRestoreStore.ResetForTests(store);

            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            ResolutionRestoreHelper.ReplayPersistedRestore(device);

            checklist.Check(device.ChangeModeCalls.Count == 0 && device.TryGetCurrentModeCalls.Count == 0,
                "RR17: a MonitorId that resolves to no currently attached display is dropped before ever asking the device for its current mode");
            checklist.Check(!File.Exists(filePath),
                "RR17: the record is deleted once processed (nothing survived - a gone display has no live device to retry against)");
        }

        // RR19 (Unreadable, outer loop). Mutation this guards: dropping a kept-not-dropped entry
        // (or deleting the file instead of rewriting it) would silently lose the restore obligation
        // for a display that has simply not finished enumerating yet this boot.
        private static void CheckReplayKeepsAndRewritesAnUnreadableEntry(Checklist checklist, string tempDir, string realDeviceName, string realMonitorId)
        {
            string filePath = Path.Combine(tempDir, "rr19-unreadable.xml");
            RealResolutionRestoreStore store = new RealResolutionRestoreStore(filePath);
            ResolutionModeWrapper applied = BuildTarget(1920, 1080, 32, 60, 0);
            ResolutionModeWrapper windows = BuildTarget(2560, 1440, 32, 144, 0);
            ResolutionRestoreRecord record = new ResolutionRestoreRecord();
            record.SchemaVersion = 1;
            record.Vendor = "NVIDIA";
            record.Displays.Add(new ResolutionRestoreEntry { MonitorId = realMonitorId, DeviceNameAtWrite = realDeviceName, AppliedMode = applied, WindowsMode = windows });
            store.Write(record);
            ResolutionRestoreStore.ResetForTests(store);

            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetUnreadable(realDeviceName); // MonitorIdentity resolves the name for real; the fake device's own read does not

            ResolutionRestoreHelper.ReplayPersistedRestore(device);

            checklist.Check(device.ChangeModeCalls.Count == 0, "RR19 (Unreadable): no write is attempted when the current mode cannot be read");
            checklist.Check(File.Exists(filePath), "RR19: the record survives (rewritten, not deleted) so the next launch retries this entry");

            ResolutionRestoreRecord rewritten = store.TryRead();
            checklist.Check(rewritten != null && rewritten.Displays != null && rewritten.Displays.Count == 1 &&
                rewritten.Displays[0].MonitorId == realMonitorId &&
                rewritten.Displays[0].AppliedMode.Equals(applied) && rewritten.Displays[0].WindowsMode.Equals(windows),
                "RR19: the rewritten record still names this exact entry, unchanged");
        }

        // RR20 (AlreadyCorrect, outer loop) - the common case after a clean exit that also happened
        // to leave a stale record around (ResolutionHelper.RestoreOnExit restores the mode but never
        // clears this file itself - see ResolutionRestoreHelper.ReplayPersistedRestore's own
        // comment for why that is harmless).
        private static void CheckReplayDeletesOnceAnAlreadyCorrectEntrySettles(Checklist checklist, string tempDir, string realDeviceName, string realMonitorId)
        {
            string filePath = Path.Combine(tempDir, "rr20-already-correct.xml");
            RealResolutionRestoreStore store = new RealResolutionRestoreStore(filePath);
            ResolutionModeWrapper applied = BuildTarget(1920, 1080, 32, 60, 0);
            ResolutionModeWrapper windows = BuildTarget(2560, 1440, 32, 144, 0);
            ResolutionRestoreRecord record = new ResolutionRestoreRecord();
            record.SchemaVersion = 1;
            record.Vendor = "NVIDIA";
            record.Displays.Add(new ResolutionRestoreEntry { MonitorId = realMonitorId, DeviceNameAtWrite = realDeviceName, AppliedMode = applied, WindowsMode = windows });
            store.Write(record);
            ResolutionRestoreStore.ResetForTests(store);

            FakeDisplayModeDevice device = new FakeDisplayModeDevice();
            device.SetCurrentMode(realDeviceName, windows); // already settled at the Windows mode

            ResolutionRestoreHelper.ReplayPersistedRestore(device);

            checklist.Check(device.ChangeModeCalls.Count == 0, "RR20 (AlreadyCorrect): nothing is written - the display is already settled");
            checklist.Check(!File.Exists(filePath), "RR20: the record is deleted once processed - nothing survived");
        }

        // ------------------------------------------------------------------
        // Fakes
        // ------------------------------------------------------------------

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

        private static Devmode ToDevmode(ResolutionModeWrapper mode)
        {
            Devmode devmode = new Devmode();
            devmode.dmPelsWidth = mode.DmPelsWidth;
            devmode.dmPelsHeight = mode.DmPelsHeight;
            devmode.dmBitsPerPel = mode.DmBitsPerPel;
            devmode.dmDisplayFrequency = mode.DmDisplayFrequency;
            devmode.dmDisplayFixedOutput = mode.DmDisplayFixedOutput;
            return devmode;
        }

        // A smaller, purpose-built IDisplayModeDevice fake - simpler than ResolutionChangeFixture's
        // own FakeDisplayModeDevice (that one is private to that class and not reachable from here,
        // and models the full CDS_TEST-then-CDS_UPDATEREGISTRY staged sequence with per-flag queued
        // results; this only needs "the next ChangeMode call for this device fails" to reach
        // WriteFailed, since TryRestorePersistedMode's own honourGiveUp:false call either succeeds
        // outright or fails at CDS_TEST before CDS_UPDATEREGISTRY is ever reached).
        private class FakeDisplayModeDevice : IDisplayModeDevice
        {
            private readonly Dictionary<string, Devmode> _currentModes = new Dictionary<string, Devmode>();
            private readonly HashSet<string> _unreadable = new HashSet<string>();
            private readonly HashSet<string> _failNextChange = new HashSet<string>();

            public readonly List<string> ChangeModeCalls = new List<string>();
            public readonly List<string> TryGetCurrentModeCalls = new List<string>();

            public void SetCurrentMode(string deviceName, ResolutionModeWrapper mode)
            {
                _currentModes[deviceName] = ToDevmode(mode);
            }

            public void SetUnreadable(string deviceName)
            {
                _unreadable.Add(deviceName);
            }

            // Self-clearing after one call, mirroring VibranceRestorePersistenceFixture's own
            // FakeNvidiaVibranceDevice.FailNextSetLevel - the CDS_TEST call fails, so
            // CDS_UPDATEREGISTRY is never reached (ResolutionHelper.ChangeResolutionEx's own step
            // 4/5 ordering).
            public void FailNextChange(string deviceName)
            {
                _failNextChange.Add(deviceName);
            }

            public Devmode GetCurrentMode(string deviceName)
            {
                return _currentModes[deviceName];
            }

            public bool TryGetCurrentMode(string deviceName, out Devmode mode)
            {
                TryGetCurrentModeCalls.Add(deviceName);
                if (_unreadable.Contains(deviceName))
                {
                    mode = default(Devmode);
                    return false;
                }
                return _currentModes.TryGetValue(deviceName, out mode);
            }

            public bool TryEnumerateMode(string deviceName, int modeNum, out Devmode mode)
            {
                // Not exercised by anything this fixture drives - TryRestorePersistedMode never
                // enumerates supported modes, only reads/changes the current one.
                mode = default(Devmode);
                return false;
            }

            public DispChange ChangeMode(string deviceName, Devmode mode, ChangeDisplaySettingsFlags flags)
            {
                ChangeModeCalls.Add(deviceName);
                if (_failNextChange.Remove(deviceName))
                {
                    return DispChange.DispChangeFailed;
                }
                if (flags == ChangeDisplaySettingsFlags.CdsUpdateregistry)
                {
                    // Mirrors a real driver: CDS_UPDATEREGISTRY that reports success actually
                    // changes the live mode, which is what ChangeResolutionEx's own post-apply
                    // verification (step 6) reads back afterward.
                    _currentModes[deviceName] = mode;
                }
                return DispChange.DispChangeSuccessful;
            }
        }

        // A call-counting IResolutionRestoreStore - what the journaling checks above need (HOW MANY
        // TIMES Write()/Delete() ran), not real file content, which RealResolutionRestoreStore's own
        // checks already cover. Mirrors VibranceRestorePersistenceFixture's own
        // RecordingVibranceRestoreStore.
        private class RecordingResolutionRestoreStore : IResolutionRestoreStore
        {
            public int WriteCallCount;
            public int DeleteCallCount;
            public ResolutionRestoreRecord LastWritten;

            public ResolutionRestoreRecord TryRead()
            {
                return LastWritten;
            }

            public void Write(ResolutionRestoreRecord record)
            {
                WriteCallCount++;
                LastWritten = record;
            }

            public void Delete()
            {
                DeleteCallCount++;
                LastWritten = null;
            }
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

            public void Skip(string description)
            {
                Lines.Add(string.Format("[SKIP] {0}", description));
            }
        }
    }
}
