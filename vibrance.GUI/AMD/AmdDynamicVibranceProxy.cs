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

            _amdAdapter.SetSaturationOnAllDisplays(_vibranceInfo.userVibranceSettingDefault);
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
                Screen screen = Screen.FromHandle(e.Handle);
                _gameScreen = screen;

                //apply application specific saturation
                if (_vibranceInfo.userVibranceSettingDefault != applicationSetting.IngameLevel)
                {
                    if (_vibranceInfo.affectPrimaryMonitorOnly)
                    {
                        _amdAdapter.SetSaturationOnDisplay(applicationSetting.IngameLevel, screen.DeviceName);
                    }
                    else
                    {
                        _amdAdapter.SetSaturationOnAllDisplays(applicationSetting.IngameLevel);
                    }
                }

                //test if a resolution change is needed
                if (_vibranceInfo.neverChangeResolution == false && applicationSetting.IsResolutionChangeNeeded &&
                    IsResolutionChangeNeeded(screen, applicationSetting.ResolutionSettings) &&
                    _windowsResolutionSettings.ContainsKey(screen.DeviceName) &&
                    _windowsResolutionSettings[screen.DeviceName].Item2.Contains(applicationSetting.ResolutionSettings))
                {
                    PerformResolutionChange(screen, applicationSetting.ResolutionSettings);
                    _vibranceInfo.isResolutionChangeApplied = true;
                }

                //test if color settings change is needed
                if (_vibranceInfo.neverChangeColorSettings == false && _vibranceInfo.isColorSettingApplied == false &&
                    DeviceGammaRampHelper.IsGammaRampEqualToWindowsValues(_vibranceInfo, applicationSetting) == false)
                {
                    DeviceGammaRampHelper.SetGammaRamp(screen, brightness: applicationSetting.Brightness, contrast: applicationSetting.Contrast, gamma: applicationSetting.Gamma);
                    _vibranceInfo.isColorSettingApplied = true;
                }
            }
            else
            {
                IntPtr processHandle = e.Handle;
                if (GetForegroundWindow() != processHandle)
                    return;

                //apply Windows saturation
                _amdAdapter.SetSaturationOnAllDisplays(_vibranceInfo.userVibranceSettingDefault);

                //test if a resolution change is needed
                Screen currentScreen = Screen.FromHandle(processHandle);
                if (_vibranceInfo.neverChangeResolution == false && _vibranceInfo.isResolutionChangeApplied == true &&
                    _gameScreen != null && _gameScreen.Equals(currentScreen) && 
                    _windowsResolutionSettings.ContainsKey(currentScreen.DeviceName) &&
                    IsResolutionChangeNeeded(currentScreen, _windowsResolutionSettings[currentScreen.DeviceName].Item1))
                {
                    PerformResolutionChange(currentScreen, _windowsResolutionSettings[currentScreen.DeviceName].Item1);
                    _vibranceInfo.isResolutionChangeApplied = false;
                }

                //apply windows color settings if color settings were previously changed
                if (_vibranceInfo.neverChangeColorSettings == false && _vibranceInfo.isColorSettingApplied == true)
                {
                    RestoreWindowsColorSettings();
                }
            }
        }

        private static bool IsResolutionChangeNeeded(Screen screen, ResolutionModeWrapper resolutionSettings)
        {
            Devmode mode;
            if (resolutionSettings != null && ResolutionHelper.GetCurrentResolutionSettings(out mode, screen.DeviceName) && !resolutionSettings.Equals(mode))
            {
                return true;
            }
            return false;
        }

        private static void PerformResolutionChange(Screen screen, ResolutionModeWrapper resolutionSettings)
        {
            ResolutionHelper.ChangeResolutionEx(resolutionSettings, screen.DeviceName);
        }

        private void RestoreWindowsColorSettings()
        {
            //the gamma ramp is only ever applied to the game screen, restoring every screen would overwrite color settings this application never touched
            if (_gameScreen != null)
            {
                DeviceGammaRampHelper.SetGammaRamp(_gameScreen, brightness: _vibranceInfo.userColorSettings.brightness, contrast: _vibranceInfo.userColorSettings.contrast, gamma: _vibranceInfo.userColorSettings.gamma);
            }
            else
            {
                Screen.AllScreens.ToList().ForEach(screen => DeviceGammaRampHelper.SetGammaRamp(screen, brightness: _vibranceInfo.userColorSettings.brightness, contrast: _vibranceInfo.userColorSettings.contrast, gamma: _vibranceInfo.userColorSettings.gamma));
            }
            _vibranceInfo.isColorSettingApplied = false;
        }
    }
}