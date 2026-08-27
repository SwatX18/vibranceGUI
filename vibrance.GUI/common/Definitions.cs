using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace vibrance.GUI.common
{
    [StructLayout(LayoutKind.Sequential)]
    public struct VibranceInfo
    {
        public bool isInitialized;
        public int activeOutput;
        // False until SetVibranceWindowsLevel has actually been called - true from construction
        // through the window between the proxy subscribing OnWinEventHook and VibranceGUI.cs's
        // backgroundWorker_DoWork reaching SetVibranceWindowsLevel (it waits on
        // "while (!this.IsHandleCreated) Thread.Sleep(500);" first). Both vendors' restore paths
        // treat a foreground event landing in that window as a no-op instead of writing
        // userVibranceSettingDefault's still-zero struct default.
        public bool isWindowsLevelKnown;
        public int userVibranceSettingDefault;
        public int userVibranceSettingActive;
        public String szGpuName;
        // Written by SetShouldRun but never read back anywhere in this codebase - dead plumbing,
        // not the toggle hotkey's per-game suppression state. That state is a per-profile set
        // (ProfileToggleHelper._suppressedProfileNames, keyed by ApplicationSetting.Name), not a
        // single global bool - a whole-engine pause was considered and rejected in favour of a
        // per-game toggle, which this single field could never represent correctly.
        public bool shouldRun;
        public int sleepInterval;
        public List<int> displayHandles;
        public bool affectPrimaryMonitorOnly;
        public bool neverChangeResolution;
        public bool neverChangeColorSettings;
        public bool isColorSettingApplied;
        public bool isResolutionChangeApplied;
        public ColorSettings userColorSettings;
        public struct ColorSettings
        {
            public int brightness;
            public int contrast;
            public int gamma;
        }
    }
}
