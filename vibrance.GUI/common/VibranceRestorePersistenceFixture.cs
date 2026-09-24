using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using vibrance.GUI.NVIDIA;

namespace vibrance.GUI.common
{
    /// <summary>
    /// Regression coverage for D4's abnormal-exit half (upstream #95's shape: a game on a NON-
    /// PRIMARY display, killed via Task Manager, never gets that display restored - see docs/
    /// CODEBASE_GUIDE.md's D4 entry). Covers the three pieces that make the persisted restore
    /// safe: RealVibranceRestoreStore's atomic write/torn-file handling (Task 3's seam),
    /// VibranceRestoreHelper's write-iff-changed/delete-iff-drained journaling rules (Task 4), and
    /// NvidiaDynamicVibranceProxy.ReplayPersistedVibranceRestore's verify gate (Task 5) - all
    /// driven against a fixture-private temp file (never the real
    /// %APPDATA%\vibranceGUI\vibranceRestore.xml) and a fake INvidiaVibranceDevice (never a real
    /// GPU or a real display write), the same FakeNvidiaVibranceDevice pattern
    /// VibranceRestoreFixture/HdrVibranceFixture/ProfileToggleFixture/StartupForegroundFixture
    /// already use. Run by vibrance.GUI.exe --selftest-restore-persistence.
    ///
    /// Three checks (the three verify-gate outcomes) need one real, currently attached, uniquely
    /// identifiable display to resolve a real MonitorIdentity id against - MonitorIdentity's own
    /// EnumDisplayDevices calls are read-only, so this never writes a resolution, gamma ramp or
    /// vibrance level to anything real, but a machine with no attached display (a bare RDP session
    /// with no physical monitor, for instance) has nothing for them to resolve, so those three are
    /// Skipped rather than failed when that happens - the same accommodation VibranceRestoreFixture
    /// already makes for its own hardware-dependent AMD checks.
    /// </summary>
    public static class VibranceRestorePersistenceFixture
    {
        public static List<string> Run()
        {
            Checklist checklist = new Checklist();
            checklist.Lines.Add("vibranceGUI persisted vibrance restore self test");
            checklist.Lines.Add(string.Empty);

            string tempDir = Path.Combine(Path.GetTempPath(), "vibranceGUI-restore-persistence-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                RunStoreChecks(checklist, tempDir);
                RunJournalingChecks(checklist);
                RunReplayGateChecks(checklist, tempDir);
            }
            finally
            {
                // Never leaves the shared static seams pointed at this fixture's own temp store -
                // mirrors VibranceRestoreFixture's own ResetForTests-per-check discipline, just
                // done once at the very end too, so nothing here can bleed into a later fixture run
                // in the same process.
                VibranceRestoreStore.ResetForTests(null);
                VibranceRestoreHelper.ResetForTests();
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
        // RealVibranceRestoreStore - the atomic write/read/delete seam itself, pointed at a
        // fixture-private temp file throughout. Task 3.
        // ------------------------------------------------------------------

        private static void RunStoreChecks(Checklist checklist, string tempDir)
        {
            checklist.Lines.Add("RealVibranceRestoreStore (fixture-private temp path):");

            CheckRoundTripWriteRead(checklist, tempDir);
            CheckTryReadReturnsNullWhenFileNeverWritten(checklist, tempDir);
            CheckTornFileIsDiscardedWithoutThrowing(checklist, tempDir);
            CheckDeleteIsNoOpOnMissingFileAndRemovesAnExistingOne(checklist, tempDir);

            checklist.Lines.Add(string.Empty);
        }

        // P1. Mutation this guards: any lossy round trip through XmlSerializer (a property that
        // does not survive serialisation, or an atomic-write bug that swaps in the wrong temp
        // file).
        private static void CheckRoundTripWriteRead(Checklist checklist, string tempDir)
        {
            string filePath = Path.Combine(tempDir, "roundtrip.xml");
            RealVibranceRestoreStore store = new RealVibranceRestoreStore(filePath);

            VibranceRestoreRecord written = new VibranceRestoreRecord();
            written.SchemaVersion = 1;
            written.Vendor = "NVIDIA";
            written.WrittenUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            written.Displays.Add(new VibranceRestoreEntry { MonitorId = "TESTONLY-MID-A", DeviceNameAtWrite = "\\\\.\\DISPLAY1", AppliedLevel = 40 });
            written.Displays.Add(new VibranceRestoreEntry { MonitorId = "TESTONLY-MID-B", DeviceNameAtWrite = "\\\\.\\DISPLAY2", AppliedLevel = 12 });

            store.Write(written);
            VibranceRestoreRecord readBack = store.TryRead();

            bool roundTripped = readBack != null && readBack.SchemaVersion == 1 &&
                readBack.Vendor == "NVIDIA" && readBack.Displays != null && readBack.Displays.Count == 2 &&
                readBack.Displays[0].MonitorId == "TESTONLY-MID-A" && readBack.Displays[0].DeviceNameAtWrite == "\\\\.\\DISPLAY1" && readBack.Displays[0].AppliedLevel == 40 &&
                readBack.Displays[1].MonitorId == "TESTONLY-MID-B" && readBack.Displays[1].DeviceNameAtWrite == "\\\\.\\DISPLAY2" && readBack.Displays[1].AppliedLevel == 12;
            checklist.Check(roundTripped, "P1: a written record reads back with the same schema version, vendor and every display entry intact");
        }

        // P2.
        private static void CheckTryReadReturnsNullWhenFileNeverWritten(Checklist checklist, string tempDir)
        {
            string filePath = Path.Combine(tempDir, "never-written.xml");
            RealVibranceRestoreStore store = new RealVibranceRestoreStore(filePath);

            checklist.Check(store.TryRead() == null, "P2: TryRead() returns null when no file has ever been written at this path");
        }

        // P3. Mutation this guards: let a torn/unparseable file throw into startup, or leave it in
        // place to keep failing the same parse on every future launch.
        private static void CheckTornFileIsDiscardedWithoutThrowing(Checklist checklist, string tempDir)
        {
            string filePath = Path.Combine(tempDir, "torn.xml");
            File.WriteAllText(filePath, "this is not valid xml <<< it was cut off mid-write");
            RealVibranceRestoreStore store = new RealVibranceRestoreStore(filePath);

            bool threw = false;
            VibranceRestoreRecord result = null;
            try
            {
                result = store.TryRead();
            }
            catch (Exception)
            {
                threw = true;
            }

            checklist.Check(!threw && result == null,
                "P3: a torn/unparseable file is discarded silently (null, no throw) - the failure being fixed IS abnormal termination, so termination mid-write is in scope, not an edge case");
            checklist.Check(!File.Exists(filePath),
                "P3: the unparseable file is also deleted, so a future launch does not re-attempt and re-fail the same parse forever");
        }

        // P4.
        private static void CheckDeleteIsNoOpOnMissingFileAndRemovesAnExistingOne(Checklist checklist, string tempDir)
        {
            string filePath = Path.Combine(tempDir, "delete-target.xml");
            RealVibranceRestoreStore store = new RealVibranceRestoreStore(filePath);

            bool threwOnMissing = false;
            try
            {
                store.Delete();
            }
            catch (Exception)
            {
                threwOnMissing = true;
            }
            checklist.Check(!threwOnMissing, "P4: Delete() on a path with no file at all does not throw");

            store.Write(new VibranceRestoreRecord());
            bool existedAfterWrite = File.Exists(filePath);
            store.Delete();
            checklist.Check(existedAfterWrite && !File.Exists(filePath), "P4: Delete() removes a file that does exist");
        }

        // ------------------------------------------------------------------
        // VibranceRestoreHelper's journaling rules - Task 4. A RecordingVibranceRestoreStore (an
        // in-memory call counter, not a real file) is enough here: these checks are about HOW
        // MANY TIMES the seam is called, not about file content, which the store checks above
        // already cover.
        // ------------------------------------------------------------------

        private static void RunJournalingChecks(Checklist checklist)
        {
            checklist.Lines.Add("VibranceRestoreHelper journaling rules (write iff changed, delete iff drained):");

            CheckNewDisplayWritesOnce(checklist);
            CheckSameLevelRepeatDoesNotWriteAgain(checklist);
            CheckDifferentLevelOnSameDisplayWritesAgain(checklist);
            CheckBatchOverloadWritesOnceForNDisplays(checklist);
            CheckClearingAnUntrackedDisplayDoesNoIo(checklist);
            CheckPartialDrainDoesNoDeleteThenFullDrainDeletesOnce(checklist);
            CheckClearAllGuardsAgainstAnAlreadyEmptyWorkList(checklist);
            CheckShouldJournalGameLevelGuardsAnEqualLevel(checklist);

            checklist.Lines.Add(string.Empty);
        }

        // P21. NvidiaDynamicVibranceProxy.ShouldJournalGameLevel guards NVIDIA's three journal call
        // sites (OnWinEventHook's apply branch, ToggleForegroundProfile, RecheckForegroundHdrLevel)
        // - mirrors AmdDynamicVibranceProxy.ApplyResolvedGameLevel's own pre-existing guard
        // ("if (_vibranceInfo.userVibranceSettingDefault == resolvedLevel) return false;"). Driven
        // directly as a pure function against a fake device/VibranceInfo, the same way
        // VibranceRestoreFixture already drives ApplyGameVibranceLevel/RestoreWindowsVibranceLevel
        // rather than only indirectly through OnWinEventHook.
        private static void CheckShouldJournalGameLevelGuardsAnEqualLevel(Checklist checklist)
        {
            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.userVibranceSettingDefault = 50;
            NvidiaDynamicVibranceProxy.ResetForTests(new FakeNvidiaVibranceDevice(), vibranceInfo, new List<ApplicationSetting>());

            checklist.Check(!NvidiaDynamicVibranceProxy.ShouldJournalGameLevel(50),
                "P21: an ingame level EQUAL to the current Windows default is not worth journaling - it carries no restore obligation, so persisting it would only ever produce a no-op replay entry");
            checklist.Check(NvidiaDynamicVibranceProxy.ShouldJournalGameLevel(70),
                "P21: an ingame level DIFFERENT from the current Windows default is journaled normally");
        }

        // P5.
        private static void CheckNewDisplayWritesOnce(Checklist checklist)
        {
            VibranceRestoreHelper.ResetForTests();
            RecordingVibranceRestoreStore store = new RecordingVibranceRestoreStore();
            VibranceRestoreStore.ResetForTests(store);

            VibranceRestoreHelper.RecordGameLevelApplied("\\\\.\\DISPLAY_TESTONLY_P5", 40);

            checklist.Check(store.WriteCallCount == 1,
                string.Format("P5: applying a game level to a NEW display writes the record exactly once, got {0}", store.WriteCallCount));
        }

        // P6. Mutation this guards: reintroducing per-foreground-event I/O (issue #156's own
        // mistake, one layer further down than #156 itself was) by writing on every apply
        // regardless of whether anything actually changed.
        private static void CheckSameLevelRepeatDoesNotWriteAgain(Checklist checklist)
        {
            VibranceRestoreHelper.ResetForTests();
            RecordingVibranceRestoreStore store = new RecordingVibranceRestoreStore();
            VibranceRestoreStore.ResetForTests(store);

            const string deviceName = "\\\\.\\DISPLAY_TESTONLY_P6";
            VibranceRestoreHelper.RecordGameLevelApplied(deviceName, 40);
            int afterFirst = store.WriteCallCount;
            // Three more "applies" of the identical level - what every alt-tab back into an
            // already-correct, already-focused game produces in production.
            VibranceRestoreHelper.RecordGameLevelApplied(deviceName, 40);
            VibranceRestoreHelper.RecordGameLevelApplied(deviceName, 40);
            VibranceRestoreHelper.RecordGameLevelApplied(deviceName, 40);

            checklist.Check(afterFirst == 1 && store.WriteCallCount == 1,
                string.Format("P6: re-applying the SAME level to an already-tracked display writes nothing further, got {0} total write(s) after 4 applies", store.WriteCallCount));
        }

        // P7.
        private static void CheckDifferentLevelOnSameDisplayWritesAgain(Checklist checklist)
        {
            VibranceRestoreHelper.ResetForTests();
            RecordingVibranceRestoreStore store = new RecordingVibranceRestoreStore();
            VibranceRestoreStore.ResetForTests(store);

            const string deviceName = "\\\\.\\DISPLAY_TESTONLY_P7";
            VibranceRestoreHelper.RecordGameLevelApplied(deviceName, 40);
            // An HDR transition can resolve a different level for the SAME display with no display
            // added or removed - this must still count as "the set changed".
            VibranceRestoreHelper.RecordGameLevelApplied(deviceName, 25);

            checklist.Check(store.WriteCallCount == 2,
                string.Format("P7: the SAME display at a DIFFERENT level (an HDR transition can produce this) writes again, got {0} write(s)", store.WriteCallCount));
        }

        // P8. The explicit case the brief calls out: AmdDynamicVibranceProxy's two
        // foreach (Screen in Screen.AllScreens) fan-out loops must cost one write, not one per
        // display.
        private static void CheckBatchOverloadWritesOnceForNDisplays(Checklist checklist)
        {
            VibranceRestoreHelper.ResetForTests();
            RecordingVibranceRestoreStore store = new RecordingVibranceRestoreStore();
            VibranceRestoreStore.ResetForTests(store);

            List<string> deviceNames = new List<string>
            {
                "\\\\.\\DISPLAY_TESTONLY_P8_A",
                "\\\\.\\DISPLAY_TESTONLY_P8_B",
                "\\\\.\\DISPLAY_TESTONLY_P8_C"
            };
            VibranceRestoreHelper.RecordGameLevelsApplied(deviceNames, 60);

            checklist.Check(store.WriteCallCount == 1,
                string.Format("P8: the batch overload writes the record exactly ONCE for {0} displays, not once per display, got {1} write(s)", deviceNames.Count, store.WriteCallCount));
            checklist.Check(VibranceRestoreHelper.HoldingCount == deviceNames.Count,
                string.Format("P8: all {0} displays are still tracked on the in-memory work-list, got HoldingCount={1}", deviceNames.Count, VibranceRestoreHelper.HoldingCount));
        }

        // P9. ComposeRestoreTargets unconditionally appends the primary (see its own comment), so
        // RestoreOneDisplay/its AMD counterpart call ClearGameLevelRecord for the primary on EVERY
        // non-game foreground event even when nothing was ever owed on it - that must cost zero I/O.
        private static void CheckClearingAnUntrackedDisplayDoesNoIo(Checklist checklist)
        {
            VibranceRestoreHelper.ResetForTests();
            RecordingVibranceRestoreStore store = new RecordingVibranceRestoreStore();
            VibranceRestoreStore.ResetForTests(store);

            VibranceRestoreHelper.ClearGameLevelRecord("\\\\.\\DISPLAY_TESTONLY_P9_NEVER_TRACKED");

            checklist.Check(store.DeleteCallCount == 0,
                "P9: clearing a display that was never on the work-list (e.g. the primary on a quiet restore) calls Delete() zero times");
        }

        // P10.
        private static void CheckPartialDrainDoesNoDeleteThenFullDrainDeletesOnce(Checklist checklist)
        {
            VibranceRestoreHelper.ResetForTests();
            RecordingVibranceRestoreStore store = new RecordingVibranceRestoreStore();
            VibranceRestoreStore.ResetForTests(store);

            const string d1 = "\\\\.\\DISPLAY_TESTONLY_P10_D1";
            const string d2 = "\\\\.\\DISPLAY_TESTONLY_P10_D2";
            VibranceRestoreHelper.RecordGameLevelApplied(d1, 10);
            VibranceRestoreHelper.RecordGameLevelApplied(d2, 20);

            VibranceRestoreHelper.ClearGameLevelRecord(d1);
            checklist.Check(store.DeleteCallCount == 0,
                string.Format("P10: a PARTIAL drain (one of two tracked displays restored) does no I/O, got {0} Delete() call(s)", store.DeleteCallCount));

            VibranceRestoreHelper.ClearGameLevelRecord(d2);
            checklist.Check(store.DeleteCallCount == 1,
                string.Format("P10: the work-list becoming empty on THIS call deletes the record exactly once, got {0} Delete() call(s)", store.DeleteCallCount));
        }

        // P11. The check the brief calls out by name: must fail if the guard is removed.
        // Mutation this guards: drop the "hadEntries" check from ClearAllGameLevelRecords so it
        // deletes unconditionally. AMD's affectPrimaryMonitorOnly == false branch calls this
        // method on EVERY non-game foreground event, including the very first one after startup -
        // BEFORE NvidiaDynamicVibranceProxy.ReplayPersistedVibranceRestore has had any chance to
        // read a pre-crash record a PREVIOUS session left behind. An unguarded delete here would
        // erase that record before the replay ever ran, defeating the whole feature silently -
        // every OTHER check in this fixture would still pass.
        private static void CheckClearAllGuardsAgainstAnAlreadyEmptyWorkList(Checklist checklist)
        {
            VibranceRestoreHelper.ResetForTests();
            RecordingVibranceRestoreStore store = new RecordingVibranceRestoreStore();
            VibranceRestoreStore.ResetForTests(store);

            VibranceRestoreHelper.ClearAllGameLevelRecords(); // Count is already 0 - must NOT delete
            checklist.Check(store.DeleteCallCount == 0,
                string.Format("P11: ClearAllGameLevelRecords() with an EMPTY work-list calls Delete() zero times (a record from a previous session must survive to be replayed), got {0}", store.DeleteCallCount));

            VibranceRestoreHelper.RecordGameLevelApplied("\\\\.\\DISPLAY_TESTONLY_P11", 50);
            VibranceRestoreHelper.ClearAllGameLevelRecords(); // Count was 1 - must delete exactly once
            checklist.Check(store.DeleteCallCount == 1,
                string.Format("P11: ClearAllGameLevelRecords() with a NON-EMPTY work-list deletes the record exactly once, got {0}", store.DeleteCallCount));
        }

        // ------------------------------------------------------------------
        // NvidiaDynamicVibranceProxy.ReplayPersistedVibranceRestore's verify gate - Task 5. A real
        // RealVibranceRestoreStore (fixture-private temp path) throughout, and a fake
        // INvidiaVibranceDevice - no live GPU, no real display write.
        // ------------------------------------------------------------------

        private static void RunReplayGateChecks(Checklist checklist, string tempDir)
        {
            checklist.Lines.Add("NvidiaDynamicVibranceProxy.ReplayPersistedVibranceRestore (verify gate):");

            // The replay's own Vendor gate compares a record's Vendor against
            // VibranceRestoreHelper.VendorTag - THIS process's own vendor, normally stamped once by
            // whichever proxy's constructor actually ran (see that field's own comment). This
            // fixture never constructs a real proxy (no GPU, by design - see this file's own
            // header), so nothing ever sets it; RunJournalingChecks above resets it to "Unknown" on
            // every one of its own VibranceRestoreHelper.ResetForTests() calls, which is what this
            // section's own records - all stamped "NVIDIA" by BuildNvidiaRecord - need to match
            // against instead, to exercise the SAME comparison a real NVIDIA session would make.
            VibranceRestoreHelper.VendorTag = "NVIDIA";

            CheckReplayIsNoOpBeforeWindowsLevelKnown(checklist, tempDir);
            CheckReplayDropsAnUnattachedMonitorWithNoWrite(checklist, tempDir);
            CheckReplayDiscardsARecordFromTheOtherVendor(checklist, tempDir);

            string realDeviceName = Screen.AllScreens.Length > 0 ? Screen.AllScreens[0].DeviceName : null;
            string realMonitorId = string.IsNullOrEmpty(realDeviceName) ? null : MonitorIdentity.TryGetMonitorId(realDeviceName);
            if (string.IsNullOrEmpty(realMonitorId))
            {
                checklist.Skip("P14-P20: the five verify-gate outcomes need one real, currently attached, uniquely identifiable display to resolve a MonitorIdentity id against - none was found on this machine");
            }
            else
            {
                CheckReplayRestoresAnEntryStillAtItsAppliedLevel(checklist, tempDir, realDeviceName, realMonitorId);
                CheckReplayLeavesAnAlreadyCorrectEntryAlone(checklist, tempDir, realDeviceName, realMonitorId);
                CheckReplayLeavesAHandChangedEntryAlone(checklist, tempDir, realDeviceName, realMonitorId);
                CheckReplayKeepsAndRewritesAnUnreadableEntry(checklist, tempDir, realDeviceName, realMonitorId);
                CheckReplayKeepsAndRewritesAFailedWrite(checklist, tempDir, realDeviceName, realMonitorId);
            }

            // P20 needs a SECOND real, identifiable display (see its own header for why a
            // fabricated MonitorId cannot stand in for it) - Skipped on a single-monitor machine.
            string secondDeviceName = null;
            string secondMonitorId = null;
            for (int i = 0; i < Screen.AllScreens.Length; i++)
            {
                if (string.Equals(Screen.AllScreens[i].DeviceName, realDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string candidateId = MonitorIdentity.TryGetMonitorId(Screen.AllScreens[i].DeviceName);
                if (!string.IsNullOrEmpty(candidateId))
                {
                    secondDeviceName = Screen.AllScreens[i].DeviceName;
                    secondMonitorId = candidateId;
                    break;
                }
            }
            if (string.IsNullOrEmpty(secondMonitorId))
            {
                checklist.Skip("P20: needs a SECOND real, identifiable display (one settled entry, one unreadable entry, in the same replay) - only one was found on this machine");
            }
            else
            {
                CheckReplayRewritesWithOnlySurvivorsOnPartialSuccess(checklist, tempDir, realDeviceName, realMonitorId, secondDeviceName, secondMonitorId);
            }

            checklist.Lines.Add(string.Empty);
        }

        // Every check below needs a record whose Vendor the replay's own wholesale-discard gate
        // (P17) will accept - "NVIDIA" - so it is built here once rather than repeated at every
        // call site.
        private static VibranceRestoreRecord BuildNvidiaRecord()
        {
            VibranceRestoreRecord record = new VibranceRestoreRecord();
            record.SchemaVersion = 1;
            record.Vendor = "NVIDIA";
            record.WrittenUtc = DateTime.UtcNow;
            return record;
        }

        // P12. Mutation this guards: drop the isWindowsLevelKnown guard, reopening the same
        // arbitrary-write window RestoreWindowsVibranceLevel's own isWindowsLevelKnown guard
        // closes (see NvidiaDynamicVibranceProxy's N12 check for that sibling).
        private static void CheckReplayIsNoOpBeforeWindowsLevelKnown(Checklist checklist, string tempDir)
        {
            string filePath = Path.Combine(tempDir, "p12-not-known.xml");
            RealVibranceRestoreStore store = new RealVibranceRestoreStore(filePath);
            VibranceRestoreRecord record = BuildNvidiaRecord();
            record.Displays.Add(new VibranceRestoreEntry { MonitorId = "TESTONLY-P12", DeviceNameAtWrite = "\\\\.\\DISPLAY1", AppliedLevel = 40 });
            store.Write(record);
            VibranceRestoreStore.ResetForTests(store);

            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            NvidiaDynamicVibranceProxy.ReplayPersistedVibranceRestore(device, 30, false);

            checklist.Check(device.SetLevelCalls.Count == 0,
                "P12: the replay is a no-op while isWindowsLevelKnown is false, even with a real record on disk");
            checklist.Check(File.Exists(filePath),
                "P12: the record is left on disk untouched (not read, not deleted) when isWindowsLevelKnown is false, so a later real launch can still replay it");
        }

        // P13. The "monitor not attached" row of the decision table - discarded before the device
        // is ever touched (never falls back to DeviceNameAtWrite - see MonitorIdentity's own
        // header for why that field can end up naming a different physical panel after an
        // unplug). Needs no real hardware at all, since the fabricated MonitorId below can never
        // resolve on any machine.
        private static void CheckReplayDropsAnUnattachedMonitorWithNoWrite(Checklist checklist, string tempDir)
        {
            string filePath = Path.Combine(tempDir, "p13-unattached.xml");
            RealVibranceRestoreStore store = new RealVibranceRestoreStore(filePath);
            VibranceRestoreRecord record = BuildNvidiaRecord();
            const string fabricatedMonitorId = "\\\\?\\DISPLAY#TESTONLY0#5&aaaaaaa&0&UID9999#{deadbeef-dead-beef-dead-beefdeadbeef}";
            record.Displays.Add(new VibranceRestoreEntry { MonitorId = fabricatedMonitorId, DeviceNameAtWrite = "\\\\.\\DISPLAY9", AppliedLevel = 40 });
            store.Write(record);
            VibranceRestoreStore.ResetForTests(store);

            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            NvidiaDynamicVibranceProxy.ReplayPersistedVibranceRestore(device, 30, true);

            checklist.Check(device.SetLevelCalls.Count == 0 && device.ResolvedDeviceNames.Count == 0,
                "P13: a MonitorId that resolves to no currently attached display is dropped before ever asking the device for a display handle");
            checklist.Check(!File.Exists(filePath),
                "P13: the record is deleted once processed (nothing survived - a gone display has no live device to retry against)");
        }

        // P17. Mutation this guards: drop the Vendor check, letting an AMD-range AppliedLevel
        // (0-300) be compared against a live NVIDIA display (0-63) - could coincidentally collide
        // with a valid NVIDIA level and misreport the display's state.
        private static void CheckReplayDiscardsARecordFromTheOtherVendor(Checklist checklist, string tempDir)
        {
            string filePath = Path.Combine(tempDir, "p17-wrong-vendor.xml");
            RealVibranceRestoreStore store = new RealVibranceRestoreStore(filePath);
            VibranceRestoreRecord record = new VibranceRestoreRecord();
            record.SchemaVersion = 1;
            record.Vendor = "AMD";
            record.Displays.Add(new VibranceRestoreEntry { MonitorId = "TESTONLY-P17", DeviceNameAtWrite = "\\\\.\\DISPLAY1", AppliedLevel = 150 });
            store.Write(record);
            VibranceRestoreStore.ResetForTests(store);

            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            NvidiaDynamicVibranceProxy.ReplayPersistedVibranceRestore(device, 30, true);

            checklist.Check(device.SetLevelCalls.Count == 0 && device.ResolvedDeviceNames.Count == 0,
                "P17: a record stamped with a DIFFERENT vendor is discarded wholesale, before any entry (and so before MonitorIdentity or the device) is ever touched");
            checklist.Check(!File.Exists(filePath),
                "P17: the mismatched-vendor record is deleted, not left on disk to be re-read and re-discarded on every future launch");
        }

        // P14. "still ours, write lands" - restored unconditionally.
        private static void CheckReplayRestoresAnEntryStillAtItsAppliedLevel(Checklist checklist, string tempDir, string realDeviceName, string realMonitorId)
        {
            const int appliedLevel = 45;
            const int windowsLevel = 20;
            string filePath = Path.Combine(tempDir, "p14-still-ours.xml");
            RealVibranceRestoreStore store = new RealVibranceRestoreStore(filePath);
            VibranceRestoreRecord record = BuildNvidiaRecord();
            record.Displays.Add(new VibranceRestoreEntry { MonitorId = realMonitorId, DeviceNameAtWrite = realDeviceName, AppliedLevel = appliedLevel });
            store.Write(record);
            VibranceRestoreStore.ResetForTests(store);

            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            device.SeedLevel(realDeviceName, appliedLevel);

            NvidiaDynamicVibranceProxy.ReplayPersistedVibranceRestore(device, windowsLevel, true);

            checklist.Check(device.SetLevelCalls.Count == 1 && device.IsAtLevel(device.HandleFor(realDeviceName), windowsLevel),
                "P14 (Restored): a display still exactly at its persisted AppliedLevel is restored to the current Windows level unconditionally");
            checklist.Check(!File.Exists(filePath), "P14: the record is deleted once processed - nothing survived");
        }

        // P15. "already correct" - AlreadyCorrect, tested BEFORE the AppliedLevel check.
        private static void CheckReplayLeavesAnAlreadyCorrectEntryAlone(Checklist checklist, string tempDir, string realDeviceName, string realMonitorId)
        {
            const int appliedLevel = 45;
            const int windowsLevel = 20;
            string filePath = Path.Combine(tempDir, "p15-already-correct.xml");
            RealVibranceRestoreStore store = new RealVibranceRestoreStore(filePath);
            VibranceRestoreRecord record = BuildNvidiaRecord();
            record.Displays.Add(new VibranceRestoreEntry { MonitorId = realMonitorId, DeviceNameAtWrite = realDeviceName, AppliedLevel = appliedLevel });
            store.Write(record);
            VibranceRestoreStore.ResetForTests(store);

            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            device.SeedLevel(realDeviceName, windowsLevel); // already at the Windows level, not the stale AppliedLevel

            NvidiaDynamicVibranceProxy.ReplayPersistedVibranceRestore(device, windowsLevel, true);

            checklist.Check(device.SetLevelCalls.Count == 0,
                "P15 (AlreadyCorrect): a display already at the current Windows level is left alone - nothing to do, and no write");
            checklist.Check(!File.Exists(filePath), "P15: the record is deleted once processed - nothing survived");
        }

        // P16. "the user changed it by hand" - NotOurs, the deliberately strict row: dropped, no
        // write, and the display's own live level is left exactly as the "user" set it.
        private static void CheckReplayLeavesAHandChangedEntryAlone(Checklist checklist, string tempDir, string realDeviceName, string realMonitorId)
        {
            const int appliedLevel = 45;
            const int windowsLevel = 20;
            const int handChangedLevel = 33; // neither appliedLevel nor windowsLevel
            string filePath = Path.Combine(tempDir, "p16-hand-changed.xml");
            RealVibranceRestoreStore store = new RealVibranceRestoreStore(filePath);
            VibranceRestoreRecord record = BuildNvidiaRecord();
            record.Displays.Add(new VibranceRestoreEntry { MonitorId = realMonitorId, DeviceNameAtWrite = realDeviceName, AppliedLevel = appliedLevel });
            store.Write(record);
            VibranceRestoreStore.ResetForTests(store);

            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            device.SeedLevel(realDeviceName, handChangedLevel);

            NvidiaDynamicVibranceProxy.ReplayPersistedVibranceRestore(device, windowsLevel, true);

            checklist.Check(device.SetLevelCalls.Count == 0,
                "P16 (NotOurs): a display at neither the persisted AppliedLevel nor the current Windows level is left ALONE, not forced to either one");
            checklist.Check(device.IsAtLevel(device.HandleFor(realDeviceName), handChangedLevel),
                "P16: the display's own live level is unchanged by the replay");
            checklist.Check(!File.Exists(filePath), "P16: the record is deleted once processed - nothing survived, even though this entry itself was left alone");
        }

        // P18 (Unreadable). Mutation this guards: dropping a kept-not-dropped entry (or deleting
        // the file instead of rewriting it) would silently lose the restore obligation for a
        // display that has simply not finished enumerating yet this boot.
        private static void CheckReplayKeepsAndRewritesAnUnreadableEntry(Checklist checklist, string tempDir, string realDeviceName, string realMonitorId)
        {
            const int appliedLevel = 45;
            const int windowsLevel = 20;
            string filePath = Path.Combine(tempDir, "p18-unreadable.xml");
            RealVibranceRestoreStore store = new RealVibranceRestoreStore(filePath);
            VibranceRestoreRecord record = BuildNvidiaRecord();
            record.Displays.Add(new VibranceRestoreEntry { MonitorId = realMonitorId, DeviceNameAtWrite = realDeviceName, AppliedLevel = appliedLevel });
            store.Write(record);
            VibranceRestoreStore.ResetForTests(store);

            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            device.SetUnresolvable(realDeviceName); // MonitorIdentity resolves the name for real; NVIDIA's own handle lookup does not

            NvidiaDynamicVibranceProxy.ReplayPersistedVibranceRestore(device, windowsLevel, true);

            checklist.Check(device.SetLevelCalls.Count == 0,
                "P18 (Unreadable): no write is attempted when the display handle itself cannot be resolved");
            checklist.Check(File.Exists(filePath),
                "P18: the record survives (rewritten, not deleted) so the next launch retries this entry");

            VibranceRestoreRecord rewritten = store.TryRead();
            checklist.Check(rewritten != null && rewritten.Displays != null && rewritten.Displays.Count == 1 &&
                rewritten.Displays[0].MonitorId == realMonitorId && rewritten.Displays[0].AppliedLevel == appliedLevel,
                "P18: the rewritten record still names this exact entry (same MonitorId, same AppliedLevel), unchanged");
        }

        // P19 (WriteFailed). Mutation this guards: the same silent-loss failure mode as P18, for
        // the OTHER retryable outcome - a display verified still ours, whose write simply did not
        // land this launch.
        private static void CheckReplayKeepsAndRewritesAFailedWrite(Checklist checklist, string tempDir, string realDeviceName, string realMonitorId)
        {
            const int appliedLevel = 45;
            const int windowsLevel = 20;
            string filePath = Path.Combine(tempDir, "p19-write-failed.xml");
            RealVibranceRestoreStore store = new RealVibranceRestoreStore(filePath);
            VibranceRestoreRecord record = BuildNvidiaRecord();
            record.Displays.Add(new VibranceRestoreEntry { MonitorId = realMonitorId, DeviceNameAtWrite = realDeviceName, AppliedLevel = appliedLevel });
            store.Write(record);
            VibranceRestoreStore.ResetForTests(store);

            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            device.SeedLevel(realDeviceName, appliedLevel); // verified still ours...
            device.FailNextSetLevel(realDeviceName);        // ...but the write itself fails

            NvidiaDynamicVibranceProxy.ReplayPersistedVibranceRestore(device, windowsLevel, true);

            checklist.Check(device.SetLevelCalls.Count == 1,
                "P19 (WriteFailed): a write IS attempted (the display was verified still at AppliedLevel) even though it does not land");
            checklist.Check(File.Exists(filePath),
                "P19: the record survives (rewritten, not deleted) so the next launch retries this entry");

            VibranceRestoreRecord rewritten = store.TryRead();
            checklist.Check(rewritten != null && rewritten.Displays != null && rewritten.Displays.Count == 1 &&
                rewritten.Displays[0].AppliedLevel == appliedLevel,
                "P19: the rewritten record still names this exact entry, unchanged");
        }

        // P20. Two entries naming two DIFFERENT real displays, mixed outcomes in the SAME replay:
        // one settles (AlreadyCorrect) and must be dropped, the other's handle cannot be resolved
        // (Unreadable) and must survive - the rewritten record must contain EXACTLY the survivor,
        // not both and not neither. A fabricated, never-resolving MonitorId cannot stand in for
        // the second display here: MonitorIdentity would discard it before the device is ever
        // touched (P13's case), never reaching Unreadable at all - this needs a real, resolvable
        // second monitor id whose HANDLE the fake device is told to refuse instead.
        private static void CheckReplayRewritesWithOnlySurvivorsOnPartialSuccess(Checklist checklist, string tempDir,
            string firstDeviceName, string firstMonitorId, string secondDeviceName, string secondMonitorId)
        {
            const int windowsLevel = 20;
            string filePath = Path.Combine(tempDir, "p20-partial-survival.xml");
            RealVibranceRestoreStore store = new RealVibranceRestoreStore(filePath);
            VibranceRestoreRecord record = BuildNvidiaRecord();
            record.Displays.Add(new VibranceRestoreEntry { MonitorId = firstMonitorId, DeviceNameAtWrite = firstDeviceName, AppliedLevel = 45 });
            record.Displays.Add(new VibranceRestoreEntry { MonitorId = secondMonitorId, DeviceNameAtWrite = secondDeviceName, AppliedLevel = 50 });
            store.Write(record);
            VibranceRestoreStore.ResetForTests(store);

            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            device.SeedLevel(firstDeviceName, windowsLevel); // AlreadyCorrect - settles, drops
            device.SetUnresolvable(secondDeviceName);        // Unreadable - survives

            NvidiaDynamicVibranceProxy.ReplayPersistedVibranceRestore(device, windowsLevel, true);

            checklist.Check(device.SetLevelCalls.Count == 0,
                "P20: the AlreadyCorrect entry writes nothing, and the Unreadable one is never in a position to");
            checklist.Check(File.Exists(filePath),
                "P20: the record survives - the second entry's handle could not be resolved this launch");

            VibranceRestoreRecord rewritten = store.TryRead();
            checklist.Check(rewritten != null && rewritten.Displays != null && rewritten.Displays.Count == 1 &&
                rewritten.Displays[0].MonitorId == secondMonitorId,
                "P20: the rewritten record contains EXACTLY the surviving entry - the settled one is gone, the unreadable one remains");
        }

        // ------------------------------------------------------------------
        // Fakes
        // ------------------------------------------------------------------

        // Everything INvidiaVibranceDevice exposes, none of it touching a real GPU or a real
        // display - a smaller, purpose-built copy of VibranceRestoreFixture's own
        // FakeNvidiaVibranceDevice (that one is private to that class and not reachable from here).
        private class FakeNvidiaVibranceDevice : INvidiaVibranceDevice
        {
            private readonly Dictionary<string, IntPtr> _handlesByDeviceName = new Dictionary<string, IntPtr>();
            private readonly Dictionary<IntPtr, int> _levelsByHandle = new Dictionary<IntPtr, int>();
            private readonly HashSet<string> _unresolvable = new HashSet<string>();
            private readonly HashSet<IntPtr> _failNextSetLevel = new HashSet<IntPtr>();
            private int _nextHandle = 1;

            public readonly List<IntPtr> SetLevelCalls = new List<IntPtr>();
            public readonly List<string> ResolvedDeviceNames = new List<string>();

            public void SeedLevel(string deviceName, int level)
            {
                _levelsByHandle[ResolveOrAssign(deviceName)] = level;
            }

            // P18 (Unreadable): the device NAME resolves (Windows knows this monitor is attached -
            // MonitorIdentity would have found it for real), but NVIDIA's own handle resolution does
            // not - a driver-not-ready condition, distinct from "monitor not attached" (P13), which
            // never reaches the device at all.
            public void SetUnresolvable(string deviceName)
            {
                _unresolvable.Add(deviceName);
            }

            // P19 (WriteFailed): the display is verified still at AppliedLevel, but the write itself
            // does not land - self-clearing after one call, mirroring VibranceRestoreFixture's own
            // FakeNvidiaVibranceDevice.FailNextSetLevel.
            public void FailNextSetLevel(string deviceName)
            {
                _failNextSetLevel.Add(ResolveOrAssign(deviceName));
            }

            public IntPtr HandleFor(string deviceName)
            {
                return ResolveOrAssign(deviceName);
            }

            private IntPtr ResolveOrAssign(string deviceName)
            {
                IntPtr handle;
                if (!_handlesByDeviceName.TryGetValue(deviceName, out handle))
                {
                    handle = new IntPtr(_nextHandle++);
                    _handlesByDeviceName[deviceName] = handle;
                }
                return handle;
            }

            public bool IsWindowActive(ref IntPtr hWnd)
            {
                return true;
            }

            public IntPtr TryResolveDisplayHandle(string deviceName)
            {
                ResolvedDeviceNames.Add(deviceName);
                if (string.IsNullOrEmpty(deviceName) || _unresolvable.Contains(deviceName))
                {
                    return NvidiaDynamicVibranceProxy.InvalidDisplayHandle;
                }
                return ResolveOrAssign(deviceName);
            }

            public bool IsAtLevel(IntPtr displayHandle, int level)
            {
                int current;
                return _levelsByHandle.TryGetValue(displayHandle, out current) && current == level;
            }

            public bool SetLevel(IntPtr displayHandle, int level)
            {
                SetLevelCalls.Add(displayHandle);
                if (_failNextSetLevel.Remove(displayHandle))
                {
                    return false;
                }
                _levelsByHandle[displayHandle] = level;
                return true;
            }
        }

        // A call-counting IVibranceRestoreStore - what the journaling checks above need (HOW MANY
        // TIMES Write()/Delete() ran), not real file content, which RealVibranceRestoreStore's own
        // checks already cover. TryRead simply hands back whatever was last written, in case a
        // future check needs it; nothing above currently does.
        private class RecordingVibranceRestoreStore : IVibranceRestoreStore
        {
            public int WriteCallCount;
            public int DeleteCallCount;
            public VibranceRestoreRecord LastWritten;

            public VibranceRestoreRecord TryRead()
            {
                return LastWritten;
            }

            public void Write(VibranceRestoreRecord record)
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
