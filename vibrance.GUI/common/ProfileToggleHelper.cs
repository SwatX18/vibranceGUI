using System;
using System.Collections.Generic;

namespace vibrance.GUI.common
{
    /// <summary>
    /// What the toggle hotkey should do about the profile currently in the foreground, decided
    /// with no device, no Screen and no OS call at all - see Decide below.
    /// </summary>
    internal enum ProfileToggleAction
    {
        // No configured profile matches the foreground window at all.
        None,
        // This profile is currently suppressed (forced to the Windows level) - the hotkey should
        // write the game level and un-suppress it.
        ApplyGameLevel,
        // This profile is currently running normally - the hotkey should write the Windows level
        // and suppress it.
        RestoreWindowsLevel,
        // A profile matched, but userVibranceSettingDefault is not known yet (SetVibranceWindowsLevel
        // has never run this session) - writing either level would be the arbitrary-0 write
        // issue #60/#36 was.
        EngineNotReady
    }

    internal struct ProfileToggleDecision
    {
        internal ProfileToggleAction Action;
        internal ApplicationSetting Setting;
    }

    /// <summary>
    /// The toggle hotkey's own state and pure decision logic (upstream #143, per-game
    /// suppression). No device, no Screen, no OS call, no I/O - the vendor proxies' own
    /// ToggleForegroundProfile is what turns a ProfileToggleDecision into an actual write.
    /// </summary>
    internal static class ProfileToggleHelper
    {
        // The set of ApplicationSetting.Name values the user has toggled OFF by hotkey. Empty
        // means every profile behaves exactly as it does today. Deliberately a suppression set
        // and not an enablement set, so zero-initialised state is current behaviour - the same
        // polarity lesson VibranceInfo.shouldRun's own comment describes, applied to a set
        // instead of a single flag because this is now a per-game decision, not a global one.
        //
        // Keyed by Name, OrdinalIgnoreCase: Name is already this codebase's identity for a
        // profile - it is what NameMatches compares (ApplicationSettingMatcher.cs:89-94) and
        // what AddProgramsBulk dedupes on (VibranceGUI.cs). Deliberately NOT the
        // ApplicationSetting reference: listApplications_DoubleClick removes and re-adds a NEW
        // object on every edit (VibranceGUI.cs, listApplications_DoubleClick), so a held
        // reference would go stale the next time that game's settings are edited.
        //
        // Everything here runs on the UI thread (WinEvent callbacks and the WM_HOTKEY handler
        // both do), like VibranceRestoreHelper's own work-list beside it - deliberately
        // unsynchronised for the same reason.
        //
        // This class has NO persistence code of any kind - there is no read path to review
        // because there is no I/O here at all. Persistence of the hotkey binding itself lives in
        // SettingsController; suppression state is intentionally NOT persisted (every launch
        // starts with every profile un-suppressed, exactly like isPaused's would-have-been
        // semantics in the discarded design).
        //
        // Deliberately NOT derived from VibranceRestoreHelper's own work-list: that drains on
        // every alt-tab out of a game (see its own class comment), but a suppression must
        // survive exactly that - the whole point is that the automatic restore-on-alt-tab-out
        // keeps happening for a suppressed game (see the "restore branch stays ungated" note in
        // both proxies' OnWinEventHook) while the apply-on-alt-tab-in does not. Two different
        // lifetimes, so two different pieces of state.
        private static readonly HashSet<string> _suppressedProfileNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal static bool IsSuppressed(string name)
        {
            return !string.IsNullOrEmpty(name) && _suppressedProfileNames.Contains(name);
        }

        internal static void SetSuppressed(string name, bool suppressed)
        {
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            if (suppressed)
            {
                _suppressedProfileNames.Add(name);
            }
            else
            {
                _suppressedProfileNames.Remove(name);
            }
        }

        internal static int SuppressedCount
        {
            get { return _suppressedProfileNames.Count; }
        }

        // Exists for ProfileToggleFixture only - production code never needs to blank this out
        // mid-run. Mirrors VibranceRestoreHelper.ResetForTests.
        internal static void ResetForTests()
        {
            _suppressedProfileNames.Clear();
        }

        /// <summary>
        /// The toggle hotkey's whole decision, made with no device, no Screen, no OS call and no
        /// side effect - Decide never mutates _suppressedProfileNames itself; the caller flips it
        /// only after a confirmed write (see ToggleForegroundProfile in both proxies).
        ///
        /// Direction comes from OUR OWN recorded intent (IsSuppressed), never from reading the
        /// display's current level back - a read-back mis-decides the instant the game level and
        /// the Windows level happen to coincide, or an external tool nudges the display between
        /// events.
        ///
        /// Matches with the same overload the automatic WinEvent handlers use
        /// (NvidiaDynamicVibranceProxy.OnWinEventHook), including processImagePath - a directory-
        /// matched profile (no exact Name match) must be just as reachable by the hotkey as by
        /// the automatic path, or a guessed executable is invisible to the toggle even though the
        /// automatic apply already recognises it. Matching on Name alone here is PR #153's bug.
        /// </summary>
        internal static ProfileToggleDecision Decide(List<ApplicationSetting> settings,
            string processName, string processImagePath, bool isWindowsLevelKnown)
        {
            ProfileToggleDecision decision = new ProfileToggleDecision();

            ApplicationSetting setting = ApplicationSettingMatcher.FindMatch(settings, processName, processImagePath);
            if (setting == null)
            {
                decision.Action = ProfileToggleAction.None;
                return decision;
            }

            if (!isWindowsLevelKnown)
            {
                decision.Action = ProfileToggleAction.EngineNotReady;
                return decision;
            }

            decision.Setting = setting;
            decision.Action = IsSuppressed(setting.Name) ? ProfileToggleAction.ApplyGameLevel : ProfileToggleAction.RestoreWindowsLevel;
            return decision;
        }
    }
}
