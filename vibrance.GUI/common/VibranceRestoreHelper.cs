using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace vibrance.GUI.common
{
    /// <summary>
    /// The vibrance path's counterpart to DeviceGammaRampHelper's restore work-list
    /// (_devicesHoldingGameRamp). Neither vendor proxy previously kept any record of which
    /// display(s) it had actually written a GAME vibrance level to - NVIDIA hijacked a single
    /// "defaultHandle" field that a later game overwrote and a resolution-scoped restore never
    /// touched (issue #144), and the restore itself was gated on "is the currently focused screen
    /// the one the game used" instead of "which screen(s) actually need restoring" (issue #95).
    /// Both proxies share this one work-list because exactly one vendor proxy is ever constructed
    /// per process (see Program.cs's vendor selection) - there is never a question of which
    /// proxy's records these are.
    ///
    /// D4's abnormal-exit half (a Task Manager kill, crash or logoff never reaches CleanUp(), so a
    /// game level can stay stranded on a display forever) hangs its persistence off this exact
    /// work-list, journaled through VibranceRestoreStore.Current - see the three rules on
    /// RecordGameLevelsApplied/ClearGameLevelRecord/ClearAllGameLevelRecords below for when a
    /// write or delete actually happens, and NvidiaDynamicVibranceProxy.
    /// ReplayPersistedVibranceRestore for the startup side that reads it back. Persistence itself
    /// is vendor-agnostic (both proxies journal through the exact same methods below) even though
    /// only the NVIDIA replay ever reads a record back - see that method's own header for why.
    /// </summary>
    internal static class VibranceRestoreHelper
    {
        // Screen.DeviceName of every display this application has written a GAME vibrance level to
        // and has not yet written the Windows level back to. Same role and rule as
        // DeviceGammaRampHelper._devicesHoldingGameRamp: added only when a write actually landed,
        // drained by restore. Static and shared by whichever vendor proxy this process constructed -
        // exactly one is ever built. Everything runs on the UI thread (proxy construction,
        // WINEVENT_OUTOFCONTEXT callbacks, Form1_FormClosing), so deliberately unsynchronised, like
        // the _gameScreen/_vibranceInfo statics beside it. Keys are \\.\DISPLAYn, a namespace the OS
        // bounds.
        private static readonly HashSet<string> _displaysHoldingGameLevel = new HashSet<string>();

        // The level last known to be persisted for each key currently in _displaysHoldingGameLevel
        // - the in-memory mirror of what VibranceRestoreStore.Current.Write last wrote (or would
        // have written) for that display, so RecordGameLevelsApplied can tell "the set actually
        // changed" (rule 1 below) apart from a repeat apply of the same level a foreground-change
        // storm produces on every alt-tab back into the same already-correct game. Only ever
        // populated by the level-aware overloads below - a display added through the original,
        // level-less RecordGameLevelApplied(string) (every existing fixture call site, none of
        // them exercising persistence) simply never gets an entry here, and is silently skipped by
        // PersistCurrentWorkList when building a record, same as a display MonitorIdentity cannot
        // resolve.
        private static readonly Dictionary<string, int> _persistedLevels = new Dictionary<string, int>();

        // Diagnostic-only tag stamped into VibranceRestoreRecord.Vendor - set once by whichever
        // vendor proxy this process actually constructed (NvidiaDynamicVibranceProxy or
        // AmdDynamicVibranceProxy), mirroring _device's own "exactly one proxy per process" shape.
        // Never read back to decide anything on replay - see VibranceRestoreRecord.Vendor's own
        // comment.
        internal static string VendorTag = "Unknown";

        /// <summary>
        /// Marks deviceName as owing a restore to the Windows vibrance level. A no-op for null or
        /// empty - never adds a key that ComposeRestoreTargets or a foreach over the set could not
        /// meaningfully act on.
        ///
        /// Level-less on purpose, and left exactly as it always was: every production call site now
        /// uses the level-aware overload below instead (so its write is actually journaled), and
        /// this overload survives only because the whole existing fixture suite drives the in-
        /// session work-list through it directly, exercising ComposeRestoreTargets/HoldingCount
        /// with no interest in persistence at all - changing its signature would mean editing every
        /// one of those pre-existing call sites for no behavioural gain.
        /// </summary>
        internal static void RecordGameLevelApplied(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName))
            {
                return;
            }
            _displaysHoldingGameLevel.Add(deviceName);
        }

        /// <summary>
        /// The level-aware single-display overload every production apply call site uses (both
        /// vendors' automatic apply branch, HDR re-check and the toggle hotkey's "on" direction) -
        /// same in-memory effect as RecordGameLevelApplied(string) above, plus a journaled write
        /// when the persisted set actually changed. Thin wrapper over the batch overload below so
        /// the "did the set change" comparison and the actual Write() call exist in exactly one
        /// place.
        /// </summary>
        internal static void RecordGameLevelApplied(string deviceName, int level)
        {
            if (string.IsNullOrEmpty(deviceName))
            {
                return;
            }
            RecordGameLevelsApplied(new[] { deviceName }, level);
        }

        /// <summary>
        /// The batch overload AMD's two "affectPrimaryMonitorOnly == false" fan-out loops use
        /// (AmdDynamicVibranceProxy.cs, both foreach (Screen in Screen.AllScreens) sites) - ONE
        /// journaled write for the whole set of displays a single game-focus transition actually
        /// touched, not one write per display. Steady state (the same game staying focused, the
        /// same level re-applied on every alt-tab back into it) must cost zero writes here: rule 1
        /// below is what makes that true, and issue #156 is exactly the "per-foreground-event I/O"
        /// mistake this must not reintroduce, one layer further down than #156 itself was.
        ///
        /// Journaling rule 1 - write iff the set actually changed: a name newly added to the work-
        /// list, or an already-held name whose level differs from what was last persisted for it
        /// (an HDR transition can produce exactly that - the same display, a different resolved
        /// level, no display added or removed). A write here always persists the FULL current work-
        /// list (every entry in _displaysHoldingGameLevel that also has a known level), never a
        /// delta - VibranceRestoreStore.Write already replaces the whole file atomically, so there
        /// is no cheaper partial-write form to reach for.
        /// </summary>
        internal static void RecordGameLevelsApplied(IEnumerable<string> deviceNames, int level)
        {
            if (deviceNames == null)
            {
                return;
            }

            bool changed = false;
            bool anyName = false;
            foreach (string deviceName in deviceNames)
            {
                if (string.IsNullOrEmpty(deviceName))
                {
                    continue;
                }
                anyName = true;
                _displaysHoldingGameLevel.Add(deviceName);

                int previousLevel;
                if (!_persistedLevels.TryGetValue(deviceName, out previousLevel) || previousLevel != level)
                {
                    changed = true;
                }
                _persistedLevels[deviceName] = level;
            }

            if (!anyName)
            {
                return;
            }

            if (changed)
            {
                PersistCurrentWorkList();
            }
        }

        /// <summary>
        /// Drops deviceName from the work-list, normally once its restore has actually landed (or
        /// is confirmed unnecessary via a read-back). A no-op for null, empty, or a name not
        /// currently held.
        ///
        /// Journaling rule 2 - delete only when the work-list has JUST become empty: "removed"
        /// below is true only when deviceName was actually present, so a call that names a
        /// display never on the list (ComposeRestoreTargets unconditionally appends the primary,
        /// so RestoreOneDisplay/its AMD counterpart call this for the primary on EVERY non-game
        /// foreground event, work-list entry or not) costs zero I/O, not a redundant Delete() call
        /// on every single alt-tab to the desktop. A partial drain (this call removes one of
        /// several held entries) also does no I/O - the on-disk record is left exactly as it was,
        /// which can transiently OVER-name displays (naming one already restored) but can never
        /// UNDER-name one (a display still genuinely owed a restore is never silently dropped from
        /// the file by this path) - see PersistCurrentWorkList and the replay's own verify gate,
        /// which is exactly what makes an over-named entry harmless: it is read back, found already
        /// correct, and dropped without a write.
        /// </summary>
        internal static void ClearGameLevelRecord(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName))
            {
                return;
            }
            bool removed = _displaysHoldingGameLevel.Remove(deviceName);
            _persistedLevels.Remove(deviceName);

            if (removed && _displaysHoldingGameLevel.Count == 0)
            {
                VibranceRestoreStore.Current.Delete();
            }
        }

        /// <summary>
        /// The "affectPrimaryMonitorOnly == false" exit: that path restores every display through
        /// its own all-displays call, not one at a time through ComposeRestoreTargets, so the
        /// work-list is cleared in one step by whoever just did that restore.
        ///
        /// Journaling rule 3 - guard the delete on Count > 0 BEFORE clearing, not merely "the work-
        /// list is now empty" (every call ends with it empty - Clear() guarantees that): this is a
        /// correctness guard, not an optimisation. Unguarded, AMD's affectPrimaryMonitorOnly ==
        /// false branch calls this on EVERY non-game foreground event, including the very first one
        /// after startup - before NvidiaDynamicVibranceProxy.ReplayPersistedVibranceRestore (or,
        /// for AMD itself, nothing - see that method's own header) has ever had a chance to read a
        /// pre-crash record left by a PREVIOUS session. Deleting unconditionally here would erase
        /// that record before the replay ever ran, and the feature would test green (every fixture
        /// check drives ClearAllGameLevelRecords with a populated work-list already) while failing
        /// silently in exactly the abnormal-exit scenario it exists for.
        /// </summary>
        internal static void ClearAllGameLevelRecords()
        {
            bool hadEntries = _displaysHoldingGameLevel.Count > 0;
            _displaysHoldingGameLevel.Clear();
            _persistedLevels.Clear();

            if (hadEntries)
            {
                VibranceRestoreStore.Current.Delete();
            }
        }

        internal static int HoldingCount
        {
            get { return _displaysHoldingGameLevel.Count; }
        }

        /// <summary>
        /// The set of displays a restore must visit: every display currently on the work-list,
        /// plus - only when affectPrimaryMonitorOnly is true - the primary display, appended once,
        /// even if it is not itself on the work-list (the Windows Vibrance Level always owns the
        /// primary; it does not need to have been "applied to" first to be due a restore). Never
        /// null, never contains a null or empty entry, never contains a duplicate.
        ///
        /// When affectPrimaryMonitorOnly is false the primary is deliberately NOT appended - that
        /// scope is the pre-existing "every enumerated display handle" restore, which the caller
        /// drives through its own list, not through this one. The caller must call
        /// ClearAllGameLevelRecords() itself after that restore; this method does not mutate
        /// anything, so it does not do that on the caller's behalf when the flag is false.
        /// </summary>
        internal static List<string> ComposeRestoreTargets(bool affectPrimaryMonitorOnly, string primaryDeviceName)
        {
            List<string> targets = new List<string>(_displaysHoldingGameLevel);
            if (affectPrimaryMonitorOnly && !string.IsNullOrEmpty(primaryDeviceName) && !targets.Contains(primaryDeviceName))
            {
                targets.Add(primaryDeviceName);
            }
            return targets;
        }

        /// <summary>
        /// Screen.PrimaryScreen.DeviceName, or null when there is no primary screen (not reachable
        /// on a real machine, but Screen.PrimaryScreen is itself documented as nullable).
        /// </summary>
        internal static string GetPrimaryDeviceName()
        {
            Screen primary = Screen.PrimaryScreen;
            return primary == null ? null : primary.DeviceName;
        }

        /// <summary>
        /// Builds a VibranceRestoreRecord from the current work-list and writes it through
        /// VibranceRestoreStore.Current - the one place either journaling call site above actually
        /// reaches the store. Only entries this process can both (a) recall a persisted level for
        /// and (b) durably identify are included: a display added through the level-less
        /// RecordGameLevelApplied(string) overload (fixture-only in practice - see that overload's
        /// own comment) has no entry in _persistedLevels and is silently skipped, and a display
        /// MonitorIdentity.TryGetMonitorId cannot resolve for (a virtual/mirror driver, most
        /// likely) is skipped the same way - neither omission is an error, and neither prevents the
        /// REST of the work-list from being persisted correctly. An empty result is still written
        /// (as a record with zero Displays) rather than suppressed, so a caller never has to
        /// special-case "nothing to persist" - the next drain-to-empty deletes it exactly the same
        /// way either way.
        /// </summary>
        private static void PersistCurrentWorkList()
        {
            VibranceRestoreRecord record = new VibranceRestoreRecord();
            record.SchemaVersion = 1;
            record.Vendor = VendorTag;
            record.WrittenUtc = DateTime.UtcNow;

            foreach (string deviceName in _displaysHoldingGameLevel)
            {
                int level;
                if (!_persistedLevels.TryGetValue(deviceName, out level))
                {
                    continue;
                }

                string monitorId = MonitorIdentity.TryGetMonitorId(deviceName);
                if (string.IsNullOrEmpty(monitorId))
                {
                    continue;
                }

                VibranceRestoreEntry entry = new VibranceRestoreEntry();
                entry.MonitorId = monitorId;
                entry.DeviceNameAtWrite = deviceName;
                entry.AppliedLevel = level;
                record.Displays.Add(entry);
            }

            VibranceRestoreStore.Current.Write(record);
        }

        // Exists for VibranceRestoreFixture only - production code never needs to blank this
        // out mid-run. Mirrors DeviceGammaRampHelper.ResetForTests, which exists for the same
        // reason. Deliberately does NOT touch VibranceRestoreStore.Current - that is a separate
        // seam with its own ResetForTests, exactly as LogSink.Current is reset independently of
        // whatever state VibranceGUI.Log's own callers hold.
        internal static void ResetForTests()
        {
            _displaysHoldingGameLevel.Clear();
            _persistedLevels.Clear();
            VendorTag = "Unknown";
        }
    }
}
