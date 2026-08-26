using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using vibrance.GUI.common;

namespace vibrance.GUI.NVIDIA
{
    class NvidiaDynamicVibranceProxy : IVibranceProxy
    {
        #region DllImports
        [DllImport(
            "vibranceDLL.dll",
            EntryPoint = "?initializeLibrary@vibrance@vibranceDLL@@QAE_NXZ",
            CallingConvention = CallingConvention.StdCall,
            CharSet = CharSet.Auto)]
        static extern bool initializeLibrary();

        [DllImport(
            "vibranceDLL.dll",
            EntryPoint = "?unloadLibrary@vibrance@vibranceDLL@@QAE_NXZ",
            CallingConvention = CallingConvention.StdCall,
            CharSet = CharSet.Auto)]
        static extern bool unloadLibrary();


        [DllImport(
            "vibranceDLL.dll",
            EntryPoint = "?getActiveOutputs@vibrance@vibranceDLL@@QAEHQAPAH0@Z",
            CallingConvention = CallingConvention.StdCall,
            CharSet = CharSet.Auto)]
        static extern int getActiveOutputs([In, Out] int[] gpuHandles, [In, Out] int[] outputIds);

        [DllImport(
            "vibranceDLL.dll",
            EntryPoint = "?enumeratePhsyicalGPUs@vibrance@vibranceDLL@@QAEXQAPAH@Z",
            CallingConvention = CallingConvention.StdCall,
            CharSet = CharSet.Auto)]
        static extern void enumeratePhsyicalGPUs([In, Out] int[] gpuHandles);

        [DllImport(
            "vibranceDLL.dll",
            EntryPoint = "?getGpuName@vibrance@vibranceDLL@@QAE_NQAPAHPAD@Z",
            CallingConvention = CallingConvention.StdCall,
            CharSet = CharSet.Ansi)]
        static extern bool getGpuName([In, Out] int[] gpuHandles, StringBuilder szName);

        [DllImport(
            "vibranceDLL.dll",
            EntryPoint = "?getDVCInfo@vibrance@vibranceDLL@@QAE_NPAUNV_DISPLAY_DVC_INFO@12@H@Z",
            CallingConvention = CallingConvention.StdCall,
            CharSet = CharSet.Ansi)]
        static extern bool getDVCInfo(ref NvDisplayDvcInfo info, int defaultHandle);

        [DllImport(
            "vibranceDLL.dll",
            EntryPoint = "?enumerateNvidiaDisplayHandle@vibrance@vibranceDLL@@QAEHH@Z",
            CallingConvention = CallingConvention.StdCall,
            CharSet = CharSet.Auto)]
        static extern int enumerateNvidiaDisplayHandle(int index);

        [DllImport(
            "vibranceDLL.dll",
            EntryPoint = "?setDVCLevel@vibrance@vibranceDLL@@QAE_NHH@Z",
            CallingConvention = CallingConvention.StdCall,
            CharSet = CharSet.Auto)]
        static extern bool setDVCLevel([In] int defaultHandle, [In] int level);

        [DllImport(
            "vibranceDLL.dll",
            EntryPoint = "?isWindowActive@vibrance@vibranceDLL@@QAE_NPAPAUHWND__@@@Z",
            CallingConvention = CallingConvention.StdCall,
            CharSet = CharSet.Auto)]
        static extern bool isWindowActive(ref IntPtr hwnd);

        [DllImport(
            "vibranceDLL.dll",
            EntryPoint = "?equalsDVCLevel@vibrance@vibranceDLL@@QAE_NHH@Z",
            CallingConvention = CallingConvention.StdCall,
            CharSet = CharSet.Auto)]
        static extern bool equalsDVCLevel([In] int defaultHandle, [In] int level);

        [DllImport(
            "vibranceDLL.dll",
            EntryPoint = "?getGpuSystemType@vibrance@vibranceDLL@@QAEHPAH@Z",
            CallingConvention = CallingConvention.StdCall,
            CharSet = CharSet.Auto)]
        static extern NvSystemType getGpuSystemType(int gpuHandle);

        [DllImport(
            "vibranceDLL.dll",
            EntryPoint = "?getAssociatedNvidiaDisplayHandle@vibrance@vibranceDLL@@QAEHPBDH@Z",
            CallingConvention = CallingConvention.StdCall,
            CharSet = CharSet.Ansi)]
        static extern int getAssociatedNvidiaDisplayHandle(string deviceName, [In] int length);
        #endregion


        public const int NvapiMaxPhysicalGpus = 64;

        // Each physical GPU can drive more than one display, so the bound below (issue #138) scales
        // by a display-per-GPU headroom - not a quoted nvapi.h constant: no copy of nvapi.h is
        // vendored in this repo, so the exact ceiling NvAPI itself uses cannot be confirmed here.
        // A bound that must never truncate a real display should over-approximate rather than try
        // to match that ceiling exactly.
        public const int NvapiAdvancedDisplayHeads = 4;

        // The ceiling EnumerateDisplayHandles() (below) loops up to. NvapiMaxPhysicalGpus is
        // already trusted to size the GPU handle arrays in InitializeProxy(), so deriving this
        // bound from it is internally consistent with the rest of the class.
        //
        // Pre-fix (issue #138): with no NVIDIA GPU present the prebuilt vibranceDLL.dll never
        // returned -1, so the loop spun forever. InitializeProxy() never returned in that state, so
        // isInitialized was never set and the constructor never reached the OnWinEventHook
        // subscription below - nothing ever "walked" the growing list. In this x86 process it
        // instead ran the unbounded List<int> out of address space, throwing OutOfMemoryException,
        // which the constructor's catch (Exception) block turns into the "failed to initialize"
        // dialog.
        public const int NvapiMaxDisplays = NvapiMaxPhysicalGpus * NvapiAdvancedDisplayHeads;

        public const int NvapiMaxLevel = 63;
        public const int NvapiDefaultLevel = 0;

        public const string NvapiErrorInitFailed = "VibranceProxy failed to initialize! Press Ok to open the vibranceGUI Steam Guide in your browser. " +
            "Scroll down to section \"Troubleshooting, Errors, Q&A\".";
        public const string NvapiErrorSystypeUnsupported = "VibranceProxy detected that you are running a Laptop with integrated NVIDIA card. " +
            "NVIDIA Laptops are not supported because their NVIDIA drivers do not contain Digital Vibrance! " +
            "You are missing the Digital Vibrance option in your NVIDIA Control Panel. VibranceGUI can not run on your system.";
        public const string NvapiErrorSystypeUnknown = "VibranceProxy failed to initialize! Graphics card system type (Desktop / Laptop) is unknown!";
        public const string GuideLink = "https://vibrancegui.com/vibrance/guide";

        private static VibranceInfo _vibranceInfo;
        private static List<ApplicationSetting> _applicationSettings;
        private static Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> _windowsResolutionSettings;
        private WinEventHook _hook;
        private static Screen _gameScreen;

        public NvidiaDynamicVibranceProxy(List<ApplicationSetting> savedApplicationSettings, Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> currentWindowsResolutionSettings)
        {
            try
            {
                _applicationSettings = savedApplicationSettings;
                _windowsResolutionSettings = currentWindowsResolutionSettings;
                _vibranceInfo = new VibranceInfo();
                if (initializeLibrary())
                {
                    InitializeProxy();
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
                    Process.Start(GuideLink);
                }                
            }
        }

        private void InitializeProxy()
        {
            int[] gpuHandles = new int[NvapiMaxPhysicalGpus];
            int[] outputIds = new int[NvapiMaxPhysicalGpus];
            enumeratePhsyicalGPUs(gpuHandles);

            foreach (int gpuHandle in gpuHandles)
            {
                if(gpuHandle != 0)
                {
                    NvSystemType systemType = getGpuSystemType(gpuHandle);
                    if (systemType == NvSystemType.NvSystemTypeUnknown)
                    {
                        MessageBox.Show(NvidiaDynamicVibranceProxy.NvapiErrorSystypeUnknown, "vibranceGUI Error", 
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        _vibranceInfo.isInitialized = false; 
                        return;
                    }
                }
            }

            EnumerateDisplayHandles();

            _vibranceInfo.activeOutput = getActiveOutputs(gpuHandles, outputIds);
            StringBuilder buffer = new StringBuilder(64);
            char[] sz = new char[64];
            getGpuName(gpuHandles, buffer);
            _vibranceInfo.szGpuName = buffer.ToString();
            _vibranceInfo.defaultHandle = enumerateNvidiaDisplayHandle(0);

            NvDisplayDvcInfo info = new NvDisplayDvcInfo();
            if (getDVCInfo(ref info, _vibranceInfo.defaultHandle))
            {
                if (info.currentLevel != _vibranceInfo.userVibranceSettingDefault)
                {
                    setDVCLevel(_vibranceInfo.defaultHandle, _vibranceInfo.userVibranceSettingDefault);
                }
            }

            _vibranceInfo.isInitialized = true;
        }

        private static void OnWinEventHook(object sender, WinEventHookEventArgs e)
        {
            //an empty list still has to reach the restore branch below. Gating the whole handler on
            //Count > 0 stranded vibrance, the resolution and the gamma ramp whenever the last entry was
            //removed while its game held the foreground, with no way back short of restarting.
            ApplicationSetting applicationSetting = _applicationSettings.Count > 0
                ? ApplicationSettingMatcher.FindMatch(_applicationSettings, e.ProcessName, e.ProcessImagePath)
                : null;

            if (applicationSetting != null)
            {                  
                int displayHandle = GetApplicationDisplayHandle(e.Handle);
                if (displayHandle == -1) 
                {
                    return;
                }

                Screen screen = Screen.FromHandle(e.Handle);
                _gameScreen = screen;

                //test if digital vibrance change is needed
                if (!equalsDVCLevel(displayHandle, applicationSetting.IngameLevel))
                {
                    _vibranceInfo.defaultHandle = displayHandle;
                    setDVCLevel(_vibranceInfo.defaultHandle, applicationSetting.IngameLevel);
                }

                //test if a resolution change is needed
                if (_vibranceInfo.neverChangeResolution == false && applicationSetting.IsResolutionChangeNeeded &&
                    ResolutionHelper.IsResolutionChangeNeeded(screen.DeviceName, applicationSetting.ResolutionSettings) &&
                    _windowsResolutionSettings.ContainsKey(screen.DeviceName) &&
                    _windowsResolutionSettings[screen.DeviceName].Item2.Contains(applicationSetting.ResolutionSettings))
                {
                    ResolutionHelper.ResolutionChangeResult result = ResolutionHelper.ChangeResolutionEx(
                        applicationSetting.ResolutionSettings, screen.DeviceName, false);
                    // AppliedUnverified means CDS_UPDATEREGISTRY itself reported success but the
                    // post-apply readback did not confirm it - the mode most likely DID change, so
                    // this still counts as applied for the purpose of a later revert attempt.
                    // Treating it as "not applied" would both strand the desktop at whatever this
                    // change actually produced AND tell the user the opposite of what happened.
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

                if (!isWindowActive(ref processHandle))
                    return;
                
                //test if a resolution change is needed
                Screen currentScreen = Screen.FromHandle(processHandle);

                //test if changing the vibrance value is needed
                if (_vibranceInfo.affectPrimaryMonitorOnly && !equalsDVCLevel(_vibranceInfo.defaultHandle, _vibranceInfo.userVibranceSettingDefault) &&
                    (_gameScreen == null || _gameScreen.DeviceName.Equals(currentScreen.DeviceName)))
                {
                    setDVCLevel(_vibranceInfo.defaultHandle, _vibranceInfo.userVibranceSettingDefault);
                }
                else if (!_vibranceInfo.affectPrimaryMonitorOnly && !_vibranceInfo.displayHandles.TrueForAll(handle => equalsDVCLevel(handle, _vibranceInfo.userVibranceSettingDefault)))
                {
                    _vibranceInfo.displayHandles.ForEach(handle => setDVCLevel(handle, _vibranceInfo.userVibranceSettingDefault));
                }

                if (_vibranceInfo.neverChangeResolution == false && _vibranceInfo.isResolutionChangeApplied == true &&
                    _gameScreen != null && _gameScreen.Equals(currentScreen) &&
                    _windowsResolutionSettings.ContainsKey(currentScreen.DeviceName) &&
                    ResolutionHelper.IsResolutionChangeNeeded(currentScreen.DeviceName, _windowsResolutionSettings[currentScreen.DeviceName].Item1))
                {
                    ResolutionHelper.ResolutionChangeResult result = ResolutionHelper.ChangeResolutionEx(
                        _windowsResolutionSettings[currentScreen.DeviceName].Item1, currentScreen.DeviceName, true);
                    // A failed (or unverified) revert must leave the flag true, or the next
                    // foreground event would never retry it - AppliedUnverified here means the
                    // revert's own CDS_UPDATEREGISTRY reported success but the readback did not
                    // confirm the desktop is really back, so it is treated the same as Failed:
                    // still worth another attempt. Suppressed (the give-up state) deliberately
                    // still clears it: once ChangeResolutionEx has stopped calling the driver at
                    // all, holding this true would retry forever with the device call skipped
                    // every time.
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

        private static void RestoreWindowsColorSettings()
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

        private void EnumerateDisplayHandles()
        {
            _vibranceInfo.displayHandles = EnumerateDisplayHandles(enumerateNvidiaDisplayHandle);
        }

        // The loop body on its own, taking the enumerator as a delegate instead of calling the
        // P/Invoke directly, so StabilityFixture can drive it with a stub and cover the bound and
        // the dedupe without the real DLL.
        //
        // Bounded at NvapiMaxDisplays (issue #138): the prebuilt vibranceDLL.dll never returns -1
        // when no NVIDIA GPU is present, so an unbounded loop here spun forever. A legitimate
        // enumeration can never reach NvapiMaxDisplays, so the bound never cuts off real displays.
        //
        // Deduped: a driver stuck returning the same handle repeatedly would otherwise fill the
        // list with copies of it, each one then getting its own setDVCLevel call on every restore
        // (OnWinEventHook's displayHandles.ForEach(...) below). That is a latent cost on the
        // restore path now that the loop above is bounded - it did not cause #138: pre-fix, the
        // unbounded loop kept InitializeProxy() from ever returning, so OnWinEventHook was never
        // even subscribed and the restore path could not run regardless of duplicates.
        //
        // Always returns an allocated (possibly empty) list, never null: OnWinEventHook calls
        // TrueForAll/ForEach on _vibranceInfo.displayHandles unconditionally on the restore path.
        internal static List<int> EnumerateDisplayHandles(Func<int, int> enumerateDisplayHandle)
        {
            List<int> displayHandles = new List<int>();
            for (int i = 0; i < NvapiMaxDisplays; i++)
            {
                int displayHandle = enumerateDisplayHandle(i);
                if (displayHandle == -1)
                    break;

                if (!displayHandles.Contains(displayHandle))
                    displayHandles.Add(displayHandle);
            }
            return displayHandles;
        }

        private static int GetApplicationDisplayHandle(IntPtr hWnd)
        {
            if (hWnd != IntPtr.Zero)
            {
                Screen primaryScreen = System.Windows.Forms.Screen.FromHandle(hWnd);
                if (primaryScreen != null)
                {
                    string deviceName = primaryScreen.DeviceName;
                    GCHandle handle = GCHandle.Alloc(deviceName, GCHandleType.Pinned);
                    int id = getAssociatedNvidiaDisplayHandle(deviceName, deviceName.Length);
                    handle.Free();

                    return id;
                }
            }
            return -1;
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

        public void SetSleepInterval(int interval)
        {
            _vibranceInfo.sleepInterval = interval;
        }

        public void HandleDvc()
        {
        }

        public void SetAffectPrimaryMonitorOnly(bool affectPrimaryMonitorOnly)
        {
            _vibranceInfo.affectPrimaryMonitorOnly = affectPrimaryMonitorOnly;
        }

        public VibranceInfo GetVibranceInfo()
        {
            return _vibranceInfo;
        }

        public GraphicsAdapter GraphicsAdapter { get; } = GraphicsAdapter.Nvidia;

        public bool UnloadLibraryEx()
        {
            _hook.RemoveWinEventHook();
            return unloadLibrary();
        }

        public void HandleDvcExit()
        {
            //the gamma ramp is global display driver state, it does not revert when the process exits.
            //it is restored first so that a failing driver call below cannot skip it.
            if (_vibranceInfo.isColorSettingApplied)
            {
                RestoreWindowsColorSettings();
            }

            if (_vibranceInfo.affectPrimaryMonitorOnly)
            {
                setDVCLevel(_vibranceInfo.defaultHandle, _vibranceInfo.userVibranceSettingDefault);
            }
            else if (!_vibranceInfo.displayHandles.TrueForAll(handle => equalsDVCLevel(handle, _vibranceInfo.userVibranceSettingDefault)))
                _vibranceInfo.displayHandles.ForEach(handle => setDVCLevel(handle, _vibranceInfo.userVibranceSettingDefault));
        }
    }
}
