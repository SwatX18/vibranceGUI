using System;
using System.Collections.Generic;

namespace vibrance.GUI.common
{
    /// <summary>
    /// D4's resolution half - VibranceRestoreHelper's counterpart for
    /// %APPDATA%\vibranceGUI\resolutionRestore.xml. Vendor-agnostic, unlike vibrance: the apply/
    /// revert branch that journals through this class lives in each proxy's OnWinEventHook, but
    /// the actual driver work underneath it (ResolutionHelper.ChangeResolutionEx) is a shared
    /// static both proxies drive - there is no INvidiaVibranceDevice-shaped seam restricting this
    /// to one vendor, so AMD users get this restore too, not just NVIDIA.
    ///
    /// The persisted set is _persistedModes, a Dictionary&lt;string, Tuple&lt;ResolutionModeWrapper,
    /// ResolutionModeWrapper&gt;&gt; keyed by device name - (AppliedMode, WindowsMode) per display.
    /// Deliberately ONE dictionary, not VibranceRestoreHelper's two (a HashSet for membership plus
    /// a Dictionary for the last-persisted value): every entry here already carries the full tuple
    /// a persisted record needs, so presence in this dictionary already answers "is this display
    /// tracked" with no second structure required. This is also deliberately NOT the same thing as
    /// _vibranceInfo.isResolutionChangeApplied - that is a single bool per proxy instance, while
    /// this dictionary can hold entries for MULTIPLE displays at once: the apply path
    /// (RecordModeApplied) only ever adds or updates the one device it just changed, never evicts
    /// any other device's entry, so alt-tabbing from a game on one display straight to a game on a
    /// SECOND display - with no revert of the first display in between - leaves both displays
    /// tracked here simultaneously, even though isResolutionChangeApplied itself can only ever
    /// describe one outstanding change at a time. A partial drain (one of several tracked displays
    /// reverted, the rest still owed one) is therefore a state this class has to handle correctly -
    /// see ClearModeRecord's own comment (rule 2) for how.
    ///
    /// Four journaling rules, deliberately close to VibranceRestoreHelper's own three but not
    /// identical:
    ///
    /// Rule 1 (RecordModeApplied) - write iff the persisted set actually changed: a newly added
    /// device, or an already-held device whose AppliedMode or WindowsMode now differs from what was
    /// last persisted for it, compared with ResolutionModeWrapper.Equals (all five fields - the
    /// round-trip comparison the combo box and applicationData.xml already rely on, deliberately
    /// NOT MatchesAchievedMode's four-field gate comparison - see ResolutionHelper.
    /// TryRestorePersistedMode's own header for why the two must stay different). A write always
    /// persists the FULL current set, never a delta - a repeat apply of the same (AppliedMode,
    /// WindowsMode) pair to an already-tracked display, what every alt-tab back into an
    /// already-correct, already-focused game produces, costs zero writes.
    ///
    /// There is deliberately NO batch overload here, unlike VibranceRestoreHelper.
    /// RecordGameLevelsApplied: AMD's vibrance fan-out loops iterate Screen.AllScreens because a
    /// single non-game foreground event can owe a restore to every attached display at once, but
    /// the resolution apply branch (both proxies' OnWinEventHook) only ever targets ONE screen -
    /// Screen.FromHandle(e.Handle), the game's own - so a batch overload here would be dead surface
    /// with no call site that could ever use it.
    ///
    /// Rule 2 (ClearModeRecord) - delete only when the removal has JUST drained the set to empty. A
    /// call naming a device not currently tracked costs zero I/O. A PARTIAL drain (one of several
    /// tracked devices cleared, at least one left) also costs zero I/O - the on-disk record is left
    /// exactly as it was, which can transiently OVER-name a display (naming one already restored)
    /// but can never UNDER-name one (a display still genuinely owed a restore is never silently
    /// dropped from the file by this path): ResolutionHelper.TryRestorePersistedMode's own
    /// AlreadyCorrect/NotOurs rows are exactly what makes an over-named entry harmless on replay -
    /// it is read back, found already settled (or deliberately left alone), and dropped without a
    /// write either way.
    ///
    /// There is deliberately NO ClearAll counterpart here, unlike VibranceRestoreHelper.
    /// ClearAllGameLevelRecords. That method exists because AMD's affectPrimaryMonitorOnly == false
    /// branch restores every display through one all-displays call rather than one at a time, and
    /// its own guard (delete only when the work-list was non-empty BEFORE the clear) protects
    /// against exactly one hazard: an unconditional delete running on the very first non-game
    /// foreground event after startup, before the replay has had any chance to read a pre-crash
    /// record a PREVIOUS session left behind. That hazard is structurally absent here, two ways
    /// over, not merely guarded against: first, resolution has no equivalent "every display at
    /// once" apply/revert path at all - the apply branch targets exactly one screen (see the no-
    /// batch-overload note above), and the revert branch this class's ClearModeRecord call sits
    /// behind is itself gated on _vibranceInfo.isResolutionChangeApplied == true, which is false at
    /// startup (nothing has been applied yet THIS session) and so cannot run before the replay does.
    /// Second, rule 2's own "removed &amp;&amp;" guard already makes a call against an EMPTY set a
    /// no-op regardless, the same protection ClearAllGameLevelRecords' own guard provides by hand.
    /// Do not add an unguarded ClearAll here "for symmetry" with vibrance - there is no call site
    /// that would ever need one, and adding one would just be a second, unnecessary way to get the
    /// guard wrong.
    ///
    /// Rule 4 (documented at each proxy's own revert-branch call site, not here - there is no
    /// single method in this class it lives inside) has no vibrance counterpart at all: clear the
    /// persisted record ONLY when the revert is CONFIRMED to have landed - ChangeResolutionEx
    /// returning Applied or AlreadyMatching, never Suppressed. Suppressed is the trap: the give-up
    /// state clears _vibranceInfo.isResolutionChangeApplied too (see each proxy's own comment,
    /// mirrored from RestoreOnExit's identical reasoning), but the mode was never actually put
    /// back - clearing the persisted record on THAT result would strand the display permanently,
    /// with no record left for a future relaunch to ever retry against.
    /// </summary>
    internal static class ResolutionRestoreHelper
    {
        // (AppliedMode, WindowsMode) per device currently owed a restore - see the class header for
        // why this single dictionary plays both the membership-set and last-persisted-value roles
        // VibranceRestoreHelper splits across two structures. Static and shared by whichever vendor
        // proxy this process constructed, exactly like VibranceRestoreHelper's own statics -
        // everything on this call path (OnWinEventHook, Form1_FormClosing) already runs on the UI
        // thread, so deliberately unsynchronised. Keys are \\.\DISPLAYn, the same namespace
        // VibranceRestoreHelper's own keys use.
        private static readonly Dictionary<string, Tuple<ResolutionModeWrapper, ResolutionModeWrapper>> _persistedModes =
            new Dictionary<string, Tuple<ResolutionModeWrapper, ResolutionModeWrapper>>();

        /// <summary>
        /// Rule 1 - see the class header for the full rule. Called only once a resolution change
        /// has actually landed (the caller's own isResolutionChangeApplied just became true) -
        /// never claim a restore obligation for a change that was never confirmed applied.
        ///
        /// windowsMode == null is skipped, matching ResolutionHelper.RestoreOnExit's own existing
        /// "saved.Item1 == null" guard: a device this application has no captured Windows mode for
        /// yet has nothing meaningful to restore TO, so there is nothing worth persisting until a
        /// later WindowsResolutionRefresher.Refresh captures one. appliedMode == null is a
        /// defensive-only guard - every real call site already gates on
        /// ResolutionHelper.IsResolutionChangeNeeded, which itself returns false for a null target,
        /// so applicationSetting.ResolutionSettings is never null by the time this is reached in
        /// production.
        /// </summary>
        internal static void RecordModeApplied(string deviceName, ResolutionModeWrapper appliedMode, ResolutionModeWrapper windowsMode)
        {
            if (string.IsNullOrEmpty(deviceName) || appliedMode == null || windowsMode == null)
            {
                return;
            }

            bool changed;
            Tuple<ResolutionModeWrapper, ResolutionModeWrapper> previous;
            if (!_persistedModes.TryGetValue(deviceName, out previous))
            {
                changed = true;
            }
            else
            {
                // ResolutionModeWrapper.Equals - all five fields, the round-trip comparison, NOT
                // MatchesAchievedMode's four-field gate comparison. See the class header for why
                // that distinction matters here.
                changed = !previous.Item1.Equals(appliedMode) || !previous.Item2.Equals(windowsMode);
            }

            _persistedModes[deviceName] = new Tuple<ResolutionModeWrapper, ResolutionModeWrapper>(appliedMode, windowsMode);

            if (changed)
            {
                PersistCurrentSet();
            }
        }

        /// <summary>
        /// Rule 2 - see the class header for the full rule, including why there is no ClearAll
        /// counterpart beside this.
        /// </summary>
        internal static void ClearModeRecord(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName))
            {
                return;
            }

            bool removed = _persistedModes.Remove(deviceName);
            if (removed && _persistedModes.Count == 0)
            {
                ResolutionRestoreStore.Current.Delete();
            }
        }

        internal static int HoldingCount
        {
            get { return _persistedModes.Count; }
        }

        /// <summary>
        /// Rule 4 (see the class header) as a pure, directly testable predicate, mirroring
        /// NvidiaDynamicVibranceProxy.ShouldJournalGameLevel's own reason for existing as a
        /// standalone method: true only for the two ResolutionChangeResult values that mean a
        /// revert actually landed (Applied, AlreadyMatching). Both proxies' OnWinEventHook revert
        /// branches call this instead of inlining the same condition twice, so there is exactly one
        /// place rule 4's decision lives, and a fixture can pin it directly with no need to drive
        /// the real OnWinEventHook through reflection.
        ///
        /// Suppressed is the one result this must NEVER return true for, even though the caller's
        /// own isResolutionChangeApplied flag is cleared on that same result (the give-up state) -
        /// see the class header for why: the mode was never actually put back, so clearing the
        /// persisted record here would strand the display permanently. This is the row most likely
        /// to be "simplified" back to "clear whenever the flag is cleared" by a future edit; the
        /// explicit switch below (rather than "result != Failed &amp;&amp; result !=
        /// AppliedUnverified", the flag's OWN condition) is deliberate, so the two decisions cannot
        /// silently drift back into each other.
        /// </summary>
        internal static bool ShouldClearResolutionRestoreRecord(ResolutionHelper.ResolutionChangeResult result)
        {
            switch (result)
            {
                case ResolutionHelper.ResolutionChangeResult.Applied:
                case ResolutionHelper.ResolutionChangeResult.AlreadyMatching:
                    return true;
                default:
                    // Failed, AppliedUnverified, Suppressed - none of them mean the mode is
                    // actually back.
                    return false;
            }
        }

        /// <summary>
        /// Builds a ResolutionRestoreRecord from the current persisted set and writes it through
        /// ResolutionRestoreStore.Current - the one place RecordModeApplied actually reaches the
        /// store. A device MonitorIdentity.TryGetMonitorId cannot resolve for (a virtual/mirror
        /// driver, most likely) is skipped - not an error, and does not prevent the REST of the set
        /// from being persisted correctly, exactly like VibranceRestoreHelper's own
        /// PersistCurrentWorkList.
        ///
        /// Vendor is stamped from VibranceRestoreHelper.VendorTag - the SAME tag vibrance's own
        /// record uses, not a second, independently-maintained one (see ResolutionRestoreRecord.
        /// Vendor's own comment for why this field is diagnostic only here, unlike vibrance's).
        /// </summary>
        private static void PersistCurrentSet()
        {
            ResolutionRestoreRecord record = new ResolutionRestoreRecord();
            record.SchemaVersion = 1;
            record.Vendor = VibranceRestoreHelper.VendorTag;
            record.WrittenUtc = DateTime.UtcNow;

            foreach (KeyValuePair<string, Tuple<ResolutionModeWrapper, ResolutionModeWrapper>> pair in _persistedModes)
            {
                string monitorId = MonitorIdentity.TryGetMonitorId(pair.Key);
                if (string.IsNullOrEmpty(monitorId))
                {
                    continue;
                }

                ResolutionRestoreEntry entry = new ResolutionRestoreEntry();
                entry.MonitorId = monitorId;
                entry.DeviceNameAtWrite = pair.Key;
                entry.AppliedMode = pair.Value.Item1;
                entry.WindowsMode = pair.Value.Item2;
                record.Displays.Add(entry);
            }

            ResolutionRestoreStore.Current.Write(record);
        }

        /// <summary>
        /// D4's resolution replay - the startup half. See VibranceGUI.
        /// ReplayPersistedResolutionRestore for the full call-site contract (why it has to run
        /// where it does, and the mandatory Invoke marshal). Delegates to the internal overload
        /// below against ResolutionHelper.RealDevice, the only production IDisplayModeDevice - a
        /// fixture drives that overload directly instead, against a fake.
        /// </summary>
        internal static void ReplayPersistedRestore()
        {
            ReplayPersistedRestore(ResolutionHelper.RealDevice);
        }

        /// <summary>
        /// The testable body behind ReplayPersistedRestore() above. Reads the record once via
        /// ResolutionRestoreStore.Current.TryRead(), resolves each entry's CURRENT device name
        /// through MonitorIdentity (never the stale, diagnostic-only DeviceNameAtWrite - see
        /// ResolutionRestoreEntry's own comment), and reduces every entry to a
        /// ResolutionHelper.PersistedRestoreOutcome via ResolutionHelper.TryRestorePersistedMode.
        ///
        /// Unlike NvidiaDynamicVibranceProxy.ReplayPersistedVibranceRestore, there is NO Vendor
        /// gate here at all - no record is ever discarded wholesale based on which proxy wrote it.
        /// See ResolutionRestoreRecord.Vendor's own comment for why that gate is unnecessary (and
        /// actively wrong to copy across) for Devmode-shaped entries.
        ///
        /// Entries that settle (AlreadyCorrect, Restored) or that this gate deliberately declines
        /// to touch (NotOurs) are dropped; entries that could not be resolved to a live device or
        /// whose write did not land (Unreadable, WriteFailed) are KEPT. If anything survives, the
        /// record is REWRITTEN with just the survivors; only when nothing survives is the file
        /// deleted. Deliberately does NOT touch this class's own _persistedModes set - a display
        /// this replay restores was never tracked THIS session to begin with (the entry came from a
        /// PREVIOUS session's crash), and a display this replay leaves alone (the user changed it
        /// by hand) must not be added to a set that would make the very next matching apply/revert
        /// treat it as already covered.
        /// </summary>
        internal static void ReplayPersistedRestore(IDisplayModeDevice device)
        {
            ResolutionRestoreRecord record = ResolutionRestoreStore.Current.TryRead();
            if (record == null || record.Displays == null || record.Displays.Count == 0)
            {
                return;
            }

            List<ResolutionRestoreEntry> survivors = new List<ResolutionRestoreEntry>();
            foreach (ResolutionRestoreEntry entry in record.Displays)
            {
                if (entry == null || string.IsNullOrEmpty(entry.MonitorId))
                {
                    continue;
                }

                // Resolved through MonitorIdentity ONLY - never entry.DeviceNameAtWrite (see that
                // property's own comment). A monitor that does not currently resolve is discarded
                // here, before the device is ever touched - there is no live device to retry
                // against, so there is nothing worth keeping.
                string deviceName = MonitorIdentity.TryResolveDeviceName(entry.MonitorId);
                if (string.IsNullOrEmpty(deviceName))
                {
                    continue;
                }

                ResolutionHelper.PersistedRestoreOutcome outcome = ResolutionHelper.TryRestorePersistedMode(
                    device, deviceName, entry.AppliedMode, entry.WindowsMode);
                switch (outcome)
                {
                    case ResolutionHelper.PersistedRestoreOutcome.Restored:
                        Program.LogSafely(string.Format(
                            "Restored a stranded resolution on {0} back to its Windows mode (a previous session never reached CleanUp).", deviceName));
                        break;
                    case ResolutionHelper.PersistedRestoreOutcome.NotOurs:
                        Program.LogSafely(string.Format(
                            "Left the resolution on {0} alone on the persisted resolution restore replay: it is at neither its persisted game mode nor its Windows mode, so it was changed by hand (or by something else) since the last session.", deviceName));
                        break;
                    case ResolutionHelper.PersistedRestoreOutcome.Unreadable:
                        Program.LogSafely(string.Format(
                            "Could not read the current mode for {0} during the persisted resolution restore replay - its entry is kept for the next launch to retry.", deviceName));
                        survivors.Add(entry);
                        break;
                    case ResolutionHelper.PersistedRestoreOutcome.WriteFailed:
                        Program.LogSafely(string.Format(
                            "Failed to restore the Windows resolution for {0} during the persisted resolution restore replay - its entry is kept for the next launch to retry.", deviceName));
                        survivors.Add(entry);
                        break;
                    // AlreadyCorrect: nothing to do and nothing worth logging - the common case
                    // after a NORMAL exit that also happened to leave a stale record around (see
                    // ResolutionHelper.RestoreOnExit, which restores the resolution on a clean exit
                    // but never clears this record itself - AlreadyCorrect here is what makes that
                    // harmless).
                }
            }

            if (survivors.Count == 0)
            {
                ResolutionRestoreStore.Current.Delete();
            }
            else
            {
                ResolutionRestoreRecord rewritten = new ResolutionRestoreRecord();
                rewritten.SchemaVersion = record.SchemaVersion;
                rewritten.Vendor = record.Vendor;
                rewritten.WrittenUtc = DateTime.UtcNow;
                rewritten.Displays = survivors;
                ResolutionRestoreStore.Current.Write(rewritten);
            }
        }

        // Exists for a fixture only - production code never needs to blank this out mid-run.
        // Mirrors VibranceRestoreHelper.ResetForTests, which exists for the same reason.
        // Deliberately does NOT touch ResolutionRestoreStore.Current - that is a separate seam with
        // its own ResetForTests, exactly as VibranceRestoreStore.Current is reset independently of
        // VibranceRestoreHelper's own state.
        internal static void ResetForTests()
        {
            _persistedModes.Clear();
        }
    }
}
