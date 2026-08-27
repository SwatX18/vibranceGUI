using System;
using System.Collections.Generic;
using vibrance.GUI.NVIDIA;

namespace vibrance.GUI.common
{
    /// <summary>
    /// What ToggleForegroundProfile actually did - see IVibranceProxy.ToggleForegroundProfile's
    /// own comment for the full contract. Public, not internal, because IVibranceProxy itself is
    /// public and every type any of its members exposes has to be at least as accessible
    /// (VibranceInfo and GraphicsAdapter are both public for the same reason).
    /// </summary>
    public enum ProfileToggleResult
    {
        // No configured profile matches the foreground window at all - a silent no-op.
        NoConfiguredGameInForeground,
        // A profile matched, but userVibranceSettingDefault is not known yet - a silent no-op,
        // exactly as if nothing had matched (see VibranceInfo.isWindowsLevelKnown).
        EngineNotReady,
        // The matched profile is now running its game level again (it was suppressed before this
        // call).
        ToggledOn,
        // The matched profile is now suppressed, forced to the Windows level (it was running
        // normally before this call).
        ToggledOff,
        // A profile matched and was ready, but the write itself failed - suppression state is
        // left exactly as it was; the caller may retry by pressing the hotkey again.
        WriteFailed
    }

    public interface IVibranceProxy
    {
        void SetApplicationSettings(List<ApplicationSetting> refApplicationSettings);
        void SetShouldRun(bool shouldRun);
        void SetVibranceWindowsLevel(int vibranceWindowsLevel);
        void SetVibranceIngameLevel(int vibranceIngameLevel);
        bool UnloadLibraryEx();
        void HandleDvcExit();
        void SetAffectPrimaryMonitorOnly(bool affectPrimaryMonitorOnly);
        VibranceInfo GetVibranceInfo();
        GraphicsAdapter GraphicsAdapter { get; }
        void SetNeverSwitchResolution(bool neverSwitchResolution);
        void SetNeverChangeColorSettings(bool neverChangeColorSettings);
        void SetWindowsColorSettings(int brightness, int contrast, int gamma);

        void SetWindowsColorBrightness(int brightness);
        void SetWindowsColorContrast(int contrast);
        void SetWindowsColorGamma(int gamma);

        /// <summary>
        /// Looks up whichever configured profile currently owns foregroundWindow
        /// (ApplicationSettingMatcher.FindMatch, the same match rule the automatic WinEvent
        /// handler uses) and flips it between its game level and the Windows level - see
        /// ProfileToggleHelper.Decide for the pure decision this method turns into an actual
        /// write. No match, or a profile matched too early for userVibranceSettingDefault to mean
        /// anything yet, is a silent no-op: zero writes, suppression state untouched. The write
        /// happens BEFORE suppression state ever flips - a failed write never leaves the engine
        /// thinking a toggle landed that did not.
        /// </summary>
        ProfileToggleResult ToggleForegroundProfile(IntPtr foregroundWindow, string processName, string processImagePath);
    }
}