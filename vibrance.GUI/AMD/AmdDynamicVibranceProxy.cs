using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using vibrance.GUI.AMD.vendor;
using vibrance.GUI.common;
using vibrance.GUI.NVIDIA;

namespace vibrance.GUI.AMD
{
    public class AmdDynamicVibranceProxy : IVibranceProxy
    {
        public const int AmdMinLevel = 0;
        public const int AmdMaxLevel = 300;
        public const int AmdDefaultLevel = 100;

        private readonly IAmdAdapter _amdAdapter;
        private List<ApplicationSetting> _applicationSettings;
        private readonly Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> _windowsResolutionSettings;
        private VibranceInfo _vibranceInfo;
        private WinEventHook _hook;
        private static Screen _gameScreen;

        public AmdDynamicVibranceProxy(IAmdAdapter amdAdapter, List<ApplicationSetting> applicationSettings, Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> windowsResolutionSettings)
        {
            _amdAdapter = amdAdapter;
            _applicationSettings = applicationSettings;
            _windowsResolutionSettings = windowsResolutionSettings;

            try
            {
                _vibranceInfo = new VibranceInfo();
                if (amdAdapter.IsAvailable())
                {
                    _vibranceInfo.isInitialized = true;
                    amdAdapter.Init();
                }

                if (_vibranceInfo.isInitialized)
                {
                    _hook = WinEventHook.GetInstance();
                    _hook.WinEventHookHandler += OnWinEventHook;
                }

            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString());
                DialogResult result = MessageBox.Show(NvidiaDynamicVibranceProxy.NvapiErrorInitFailed, "vibranceGUI Error",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
                if (result == DialogResult.OK)
                {
                    Process.Start(NvidiaDynamicVibranceProxy.GuideLink);
                }
            }
        }

        public void SetApplicationSettings(List<ApplicationSetting> refApplicationSettings)
        {
            _applicationSettings = refApplicationSettings;
        }

        public void SetShouldRun(bool shouldRun)
        {
            _vibranceInfo.shouldRun = shouldRun;
        }

        public void SetNeverSwitchResolution(bool neverChangeResolution)
        {
            _vibranceInfo.neverChangeResolution = neverChangeResolution;
        }

        public void SetNeverChangeColorSettings(bool neverChangeColorSettings)
        {
            _vibranceInfo.neverChangeColorSettings = neverChangeColorSettings;
        }

        public void SetWindowsColorSettings(int brightness, int contrast, int gamma)
        {
            _vibranceInfo.userColorSettings.brightness = brightness;
            _vibranceInfo.userColorSettings.contrast = contrast;
            _vibranceInfo.userColorSettings.gamma = gamma;
        }

        public void SetWindowsColorBrightness(int brightness)
        {
            _vibranceInfo.userColorSettings.brightness = brightness;
        }

        public void SetWindowsColorContrast(int contrast)
        {
            _vibranceInfo.userColorSettings.contrast = contrast;
        }

        public void SetWindowsColorGamma(int gamma)
        {
            _vibranceInfo.userColorSettings.gamma = gamma;
        }

        public void SetVibranceWindowsLevel(int vibranceWindowsLevel)
        {
            _vibranceInfo.userVibranceSettingDefault = vibranceWindowsLevel;
            _vibranceInfo.isWindowsLevelKnown = true;
        }

        public void SetVibranceIngameLevel(int vibranceIngameLevel)
        {
            _vibranceInfo.userVibranceSettingActive = vibranceIngameLevel;
        }

        public bool UnloadLibraryEx()
        {
            _hook.RemoveWinEventHook();
            return true;
        }

        public void HandleDvcExit()
        {
            //the gamma ramp is global display driver state, it does not revert when the process exits.
            //it is restored first so that a failing driver call below cannot skip it.
            if (_vibranceInfo.isColorSettingApplied)
            {
                RestoreWindowsColorSettings();
            }

            RestoreWindowsVibranceLevel();
        }

        public void SetAffectPrimaryMonitorOnly(bool affectPrimaryMonitorOnly)
        {
            _vibranceInfo.affectPrimaryMonitorOnly = affectPrimaryMonitorOnly;
        }

        public VibranceInfo GetVibranceInfo()
        {
            return _vibranceInfo;
        }

        public GraphicsAdapter GraphicsAdapter { get; } = GraphicsAdapter.Amd;

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        private void OnWinEventHook(object sender, WinEventHookEventArgs e)
        {
            //an empty list still has to reach the restore branch below. Gating the whole handler on
            //Count > 0 stranded vibrance, the resolution and the gamma ramp whenever the last entry was
            //removed while its game held the foreground, with no way back short of restarting.
            ApplicationSetting applicationSetting = _applicationSettings.Count > 0
                ? ApplicationSettingMatcher.FindMatch(_applicationSettings, e.ProcessName, e.ProcessImagePath)
                : null;

            if (applicationSetting != null)
            {
                if (ProfileToggleHelper.IsSuppressed(applicationSetting.Name))
                {
                    // Toggled off by hotkey (upstream #143). Ignore this foreground event for
                    // this game entirely - deliberately NOT a fall-through to the restore branch
                    // below: the toggle itself already restored this display
                    // (ToggleForegroundProfile), and re-running the work-list restore on every
                    // alt-tab into a suppressed game would reach displays this game never even
                    // touched.
                    //
                    // Returns BEFORE "_gameScreen = screen" below: a suppressed game applies
                    // nothing here, so it must not become the screen a later resolution revert
                    // reasons about.
                    return;
                }

                Screen screen = Screen.FromHandle(e.Handle);
                _gameScreen = screen;

                //apply application specific saturation
                //
                // Resolved once against screen.DeviceName - the game's own screen - and passed to
                // ApplyResolvedGameLevel below, which uses that SAME resolved value for both its
                // skip guard and its write (upstream #147, part 2). Resolving here instead of
                // inside ApplyResolvedGameLevel matters only for the affectPrimaryMonitorOnly==false
                // branch there: it has no single device name of its own (it broadcasts one value to
                // every attached display), so the game's own screen is what decides which HDR
                // state that broadcast uses - see ApplyResolvedGameLevel's own comment.
                int resolvedIngameLevel = HdrVibranceHelper.ResolveIngameLevel(applicationSetting, HdrStateTracker.GetState(screen.DeviceName));
                ApplyResolvedGameLevel(resolvedIngameLevel, screen.DeviceName);

                //test if a resolution change is needed
                if (_vibranceInfo.neverChangeResolution == false && applicationSetting.IsResolutionChangeNeeded &&
                    ResolutionHelper.IsResolutionChangeNeeded(screen.DeviceName, applicationSetting.ResolutionSettings) &&
                    _windowsResolutionSettings.ContainsKey(screen.DeviceName) &&
                    _windowsResolutionSettings[screen.DeviceName].Item2.Contains(applicationSetting.ResolutionSettings))
                {
                    ResolutionHelper.ResolutionChangeResult result = ResolutionHelper.ChangeResolutionEx(
                        applicationSetting.ResolutionSettings, screen.DeviceName, false);
                    // AppliedUnverified means CDS_UPDATEREGISTRY itself reported success but the
                    // post-apply readback did not confirm it - see the matching comment in
                    // NvidiaDynamicVibranceProxy's OnWinEventHook.
                    _vibranceInfo.isResolutionChangeApplied =
                        result == ResolutionHelper.ResolutionChangeResult.Applied ||
                        result == ResolutionHelper.ResolutionChangeResult.AppliedUnverified;
                }

                //test if color settings change is needed
                if (_vibranceInfo.neverChangeColorSettings == false && _vibranceInfo.isColorSettingApplied == false &&
                    DeviceGammaRampHelper.IsGammaRampEqualToWindowsValues(_vibranceInfo, applicationSetting) == false)
                {
                    // only true when a baseline is held and the write landed - never claim a ramp is
                    // applied (and so due a restore) when it could not be captured for undo
                    _vibranceInfo.isColorSettingApplied = DeviceGammaRampHelper.ApplyGameGammaRamp(
                        screen, applicationSetting.Brightness, applicationSetting.Contrast, applicationSetting.Gamma);
                }
            }
            else
            {
                IntPtr processHandle = e.Handle;
                if (GetForegroundWindow() != processHandle)
                    return;

                //apply Windows saturation
                RestoreWindowsVibranceLevel();

                //test if a resolution change is needed
                Screen currentScreen = Screen.FromHandle(processHandle);
                if (_vibranceInfo.neverChangeResolution == false && _vibranceInfo.isResolutionChangeApplied == true &&
                    _gameScreen != null && _gameScreen.Equals(currentScreen) &&
                    _windowsResolutionSettings.ContainsKey(currentScreen.DeviceName) &&
                    ResolutionHelper.IsResolutionChangeNeeded(currentScreen.DeviceName, _windowsResolutionSettings[currentScreen.DeviceName].Item1))
                {
                    ResolutionHelper.ResolutionChangeResult result = ResolutionHelper.ChangeResolutionEx(
                        _windowsResolutionSettings[currentScreen.DeviceName].Item1, currentScreen.DeviceName, true);
                    // A failed (or unverified) revert must leave the flag true so the next
                    // foreground event retries it; Suppressed (the give-up state) deliberately
                    // still clears it - see the matching comment in NvidiaDynamicVibranceProxy's
                    // OnWinEventHook.
                    if (result != ResolutionHelper.ResolutionChangeResult.Failed &&
                        result != ResolutionHelper.ResolutionChangeResult.AppliedUnverified)
                        _vibranceInfo.isResolutionChangeApplied = false;
                }

                //apply windows color settings if color settings were previously changed
                if (_vibranceInfo.neverChangeColorSettings == false && _vibranceInfo.isColorSettingApplied == true)
                {
                    RestoreWindowsColorSettings();
                }
            }
        }

        /// <summary>
        /// The write OnWinEventHook's apply branch and RecheckForegroundHdrLevel both need - the
        /// single source both call, so the "don't rewrite the game level onto its own Windows
        /// default" guard and the actual write can never drift apart between the two call sites
        /// (upstream #147, part 2's most likely defect in this class). Deliberately compares
        /// userVibranceSettingDefault against resolvedLevel, never against the raw
        /// ApplicationSetting.IngameLevel it was resolved from: comparing against the raw value
        /// here would leave the guard answering a question about the SDR level while the write
        /// beside it answers a question about the HDR level, and the two would only happen to
        /// agree by coincidence. That coincidence is not rare - it is this feature's single most
        /// natural configuration: Windows level 100, IngameLevel 100, HdrIngameLevel 40, HDR on -
        /// "100 != 100" is false, so a guard still comparing against IngameLevel would skip the
        /// write and the configured HDR level would never apply, with nothing logged to say why.
        ///
        /// affectPrimaryMonitorOnly==false has no single device name of its own - it broadcasts
        /// resolvedLevel to every attached display via SetSaturationOnAllDisplays. deviceName here
        /// is still the GAME's own screen (the caller resolves once against it before reaching this
        /// method - see OnWinEventHook's own comment), so a second monitor in a different HDR state
        /// gets the game screen's own answer, not its own - the same "one value per game" scope
        /// IngameLevel itself already had before this feature existed. Documented explicitly here,
        /// not left implicit, because a reviewer could reasonably expect a per-display resolve
        /// instead - see HdrVibranceHelper.ResolveIngameLevel's own PR 2 note for the same call.
        ///
        /// Returns true only when this call actually attempted a write (the guard did not skip
        /// it) - RecheckForegroundHdrLevel uses that to decide whether its own log line is
        /// warranted; OnWinEventHook's apply branch does not need the return value at all, exactly
        /// as it never checked SetSaturationOnDisplay/OnAllDisplays' own bool return before this
        /// PR (see RestoreWindowsVibranceLevel's comment on this class not having a read-back).
        /// </summary>
        private bool ApplyResolvedGameLevel(int resolvedLevel, string deviceName)
        {
            if (_vibranceInfo.userVibranceSettingDefault == resolvedLevel)
            {
                return false;
            }

            if (_vibranceInfo.affectPrimaryMonitorOnly)
            {
                _amdAdapter.SetSaturationOnDisplay(resolvedLevel, deviceName);
                // Only the game's own screen was written - that is the only display owing a
                // restore.
                VibranceRestoreHelper.RecordGameLevelApplied(deviceName);
            }
            else
            {
                _amdAdapter.SetSaturationOnAllDisplays(resolvedLevel);
                // This branch really did write every attached display, not just the game's own -
                // unlike NVIDIA's equivalent, IAmdAdapter has no per-display read-back to confirm
                // any of them individually, so every currently attached display is recorded as
                // owing a restore.
                foreach (Screen attachedScreen in Screen.AllScreens)
                {
                    VibranceRestoreHelper.RecordGameLevelApplied(attachedScreen.DeviceName);
                }
            }
            return true;
        }

        // Same restore this proxy now runs from both its OnWinEventHook restore branch and
        // HandleDvcExit, in place of the old unconditional SetSaturationOnAllDisplays(...) both
        // used to call regardless of affectPrimaryMonitorOnly (issue #60/#36 on AMD: the flag was
        // never consulted here, so a second monitor's level was stomped on every restore and every
        // exit even with the checkbox on).
        private void RestoreWindowsVibranceLevel()
        {
            // A no-op before SetVibranceWindowsLevel has actually run once - see VibranceInfo's
            // isWindowsLevelKnown comment and NvidiaDynamicVibranceProxy's matching guard.
            if (!_vibranceInfo.isWindowsLevelKnown)
            {
                return;
            }

            if (!_vibranceInfo.affectPrimaryMonitorOnly)
            {
                _amdAdapter.SetSaturationOnAllDisplays(_vibranceInfo.userVibranceSettingDefault);
                VibranceRestoreHelper.ClearAllGameLevelRecords();
                return;
            }

            // This restore path still has no read-back to confirm a write landed, unlike NVIDIA's
            // equalsDVCLevel/setDVCLevel pair - so, unlike NvidiaDynamicVibranceProxy's
            // RestoreOneDisplay, every target here is written unconditionally and cleared
            // unconditionally, unable to tell "already correct" from "just fixed" or to retry a
            // failure that has no way to be observed here. That is no longer true of
            // IAmdAdapter.SetSaturationOnDisplay itself (upstream #143 gave it a real ADL_OK-based
            // bool return) - it is just that THIS call site, deliberately, still ignores it: doing
            // otherwise would make this drain conditionally, changing behaviour the pre-existing
            // A1-A6 checks in VibranceRestoreFixture pin. ToggleForegroundProfile below is the one
            // call site that actually reads the new return value.
            List<string> targets = VibranceRestoreHelper.ComposeRestoreTargets(true, VibranceRestoreHelper.GetPrimaryDeviceName());
            foreach (string deviceName in targets)
            {
                _amdAdapter.SetSaturationOnDisplay(_vibranceInfo.userVibranceSettingDefault, deviceName);
                VibranceRestoreHelper.ClearGameLevelRecord(deviceName);
            }
        }

        /// <summary>
        /// See IVibranceProxy.ToggleForegroundProfile for the full contract. Decide (pure) picks
        /// the direction from our own recorded suppression state, never from a display read-back;
        /// this method is only the write plus the flip. Unlike RestoreWindowsVibranceLevel above,
        /// this DOES read IAmdAdapter.SetSaturationOnDisplay's new bool return - the toggle path
        /// is the one place a false success genuinely matters, since flipping suppression on a
        /// write that never landed would strand the game at whatever level it was already at
        /// while telling the engine (and the user) the opposite.
        ///
        /// Branches on affectPrimaryMonitorOnly, mirroring OnWinEventHook's own apply branch
        /// above - unlike NVIDIA, the AMD apply is NOT single-display with the flag off (the
        /// DEFAULT): it writes every attached screen via SetSaturationOnAllDisplays and records
        /// all of them. A toggle that only ever touched deviceName would write one display back
        /// to the Windows level while every other monitor stayed at the game's saturation - with
        /// the balloon claiming the profile was restored - for as long as the user stays in the
        /// suppressed game, since the suppression gate returns early on every later event.
        /// </summary>
        public ProfileToggleResult ToggleForegroundProfile(IntPtr foregroundWindow, string processName, string processImagePath)
        {
            ProfileToggleDecision decision = ProfileToggleHelper.Decide(
                _applicationSettings, processName, processImagePath, _vibranceInfo.isWindowsLevelKnown);

            if (decision.Action == ProfileToggleAction.None)
            {
                return ProfileToggleResult.NoConfiguredGameInForeground;
            }
            if (decision.Action == ProfileToggleAction.EngineNotReady)
            {
                return ProfileToggleResult.EngineNotReady;
            }

            string deviceName = Screen.FromHandle(foregroundWindow).DeviceName;
            string name = decision.Setting.Name;

            if (decision.Action == ProfileToggleAction.ApplyGameLevel)
            {
                // Resolved once against deviceName - the game's own screen - and reused by both
                // branches below, mirroring ApplyResolvedGameLevel's own reasoning for the
                // automatic apply branch (upstream #147, part 2).
                int resolvedIngameLevel = HdrVibranceHelper.ResolveIngameLevel(decision.Setting, HdrStateTracker.GetState(deviceName));
                if (_vibranceInfo.affectPrimaryMonitorOnly)
                {
                    if (!_amdAdapter.SetSaturationOnDisplay(resolvedIngameLevel, deviceName))
                    {
                        return ProfileToggleResult.WriteFailed;
                    }
                    // Only the game's own screen was written - that is the only display owing a
                    // restore.
                    VibranceRestoreHelper.RecordGameLevelApplied(deviceName);
                }
                else
                {
                    // The identical write SetSaturationOnAllDisplays makes internally
                    // (AmdAdapter32/64.cs: "SetSaturationOnDisplay(vibranceLevel, null)"), but
                    // through the named-display overload so the new ADL_OK-based bool return
                    // survives for this method to actually check - see its own header comment.
                    if (!_amdAdapter.SetSaturationOnDisplay(resolvedIngameLevel, null))
                    {
                        return ProfileToggleResult.WriteFailed;
                    }
                    // This really did write every attached display, not just the game's own -
                    // every one of them is recorded as owing a restore, mirroring the automatic
                    // apply branch above.
                    foreach (Screen attachedScreen in Screen.AllScreens)
                    {
                        VibranceRestoreHelper.RecordGameLevelApplied(attachedScreen.DeviceName);
                    }
                }
                ProfileToggleHelper.SetSuppressed(name, false);
                return ProfileToggleResult.ToggledOn;
            }

            if (_vibranceInfo.affectPrimaryMonitorOnly)
            {
                if (!_amdAdapter.SetSaturationOnDisplay(_vibranceInfo.userVibranceSettingDefault, deviceName))
                {
                    return ProfileToggleResult.WriteFailed;
                }
                VibranceRestoreHelper.ClearGameLevelRecord(deviceName);
            }
            else
            {
                if (!_amdAdapter.SetSaturationOnDisplay(_vibranceInfo.userVibranceSettingDefault, null))
                {
                    return ProfileToggleResult.WriteFailed;
                }
                VibranceRestoreHelper.ClearAllGameLevelRecords();
            }
            ProfileToggleHelper.SetSuppressed(name, true);
            return ProfileToggleResult.ToggledOff;
        }

        /// <summary>
        /// See IVibranceProxy.RecheckForegroundHdrLevel for the full contract (upstream #147, part
        /// 2's re-check path). Consults ProfileToggleHelper.IsSuppressed the same way the apply
        /// branch of OnWinEventHook does - a suppressed profile has already been forced to the
        /// Windows level and owes nothing here; re-applying its HDR level on top of that would
        /// undo the toggle hotkey's own effect the next time HDR state happens to change while the
        /// game is still suppressed. The write itself goes through the exact same
        /// ApplyResolvedGameLevel the automatic apply branch uses, so its return value is what
        /// gates the log line below - "log once per transition" needs no dedup of its own here,
        /// since the caller (VibranceGUI) only ever reaches this after confirming a real
        /// transition.
        /// </summary>
        public void RecheckForegroundHdrLevel(IntPtr foregroundWindow, string processName, string processImagePath)
        {
            if (!_vibranceInfo.isWindowsLevelKnown)
            {
                return;
            }

            ApplicationSetting setting = ApplicationSettingMatcher.FindMatch(_applicationSettings, processName, processImagePath);
            if (setting == null || ProfileToggleHelper.IsSuppressed(setting.Name))
            {
                return;
            }

            string deviceName = Screen.FromHandle(foregroundWindow).DeviceName;
            HdrDisplayState state = HdrStateTracker.GetState(deviceName);
            int resolvedIngameLevel = HdrVibranceHelper.ResolveIngameLevel(setting, state);

            if (ApplyResolvedGameLevel(resolvedIngameLevel, deviceName))
            {
                Program.LogSafely(string.Format(
                    "HDR state for {0} is now {1} - re-applied {2}'s ingame vibrance level ({3}).",
                    deviceName, state, setting.Name, resolvedIngameLevel));
            }
        }

        private void RestoreWindowsColorSettings()
        {
            //restores every screen whose gamma ramp this application actually captured a baseline
            //for, composing the user's brightness/contrast/gamma on top of that baseline instead of
            //stamping the identity ramp over it. The baseline itself persists across sessions -
            //dropping it on every restore was itself a defect (B1): the next apply would re-capture
            //this application's own restore output as if it were the true calibration, and a
            //non-neutral Windows slider setting compounded further away from it on every alt-tab
            //out of a game. See DeviceGammaRampHelper's _capturedGammaRamps/_lastWrittenGammaRamps
            //for how a hot-plugged monitor or an external color change is still detected safely.
            DeviceGammaRampHelper.RestoreCapturedGammaRamps(
                _vibranceInfo.userColorSettings.brightness,
                _vibranceInfo.userColorSettings.contrast,
                _vibranceInfo.userColorSettings.gamma);
            _vibranceInfo.isColorSettingApplied = false;
        }
    }
}