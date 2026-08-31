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
    // The seam between the vibrance apply/restore logic below and the actual NVIDIA driver, in the
    // same spirit as common\DeviceGammaRampHelper.cs's IGammaDevice. RealNvidiaVibranceDevice
    // (nested at the bottom of NvidiaDynamicVibranceProxy) is the only production implementation;
    // VibranceRestoreFixture supplies a fake that records every (handle, level) it is asked to
    // write, so the apply/restore logic - the part that carried issues #60/#36 (a second monitor
    // reset on every launch), #144 (vibrance surviving the game closing) and #95 (vibrance
    // surviving an alt-tab to another monitor) - can be driven through real cycles, including
    // forced failures, without a GPU.
    internal interface INvidiaVibranceDevice
    {
        // hWnd is ref, matching isWindowActive's own HWND* signature, not IntPtr-by-value: the
        // pre-existing call site ("IntPtr processHandle = e.Handle; ... isWindowActive(ref
        // processHandle); ... Screen.FromHandle(processHandle)") already relies on whatever this
        // native call may write back into the handle it was given determining which screen the
        // rest of that branch resolves to. Taking hWnd by value here would silently drop that and
        // risk changing which screen a restore reasons about - exactly the kind of behaviour this
        // fix is not supposed to touch.
        bool IsWindowActive(ref IntPtr hWnd);

        // getAssociatedNvidiaDisplayHandle. -1 when NvAPI cannot name deviceName's display; never
        // called with a null or empty deviceName.
        int TryResolveDisplayHandle(string deviceName);

        // equalsDVCLevel.
        bool IsAtLevel(int displayHandle, int level);

        // setDVCLevel.
        bool SetLevel(int displayHandle, int level);
    }

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

        // The only production INvidiaVibranceDevice. Not readonly - ResetForTests below swaps it
        // for a fake so VibranceRestoreFixture can drive ApplyGameVibranceLevel/
        // RestoreWindowsVibranceLevel (and, through them, OnWinEventHook itself, which is private
        // static and so reachable by reflection with no instance at all) without ever calling
        // initializeLibrary() or touching a real GPU.
        private static INvidiaVibranceDevice _device = new RealNvidiaVibranceDevice();

        // Suppresses repeat log calls for a display that is failing to resolve or failing to
        // write, on either the apply or the restore path - shares the same one-set-per-device,
        // clear-on-any-success convention as DeviceGammaRampHelper._loggedDeviceFailures, for the
        // same reason: a foreground-change storm against a broken display must not redo a
        // synchronous log write on the UI thread on every single event.
        private static readonly HashSet<string> _loggedDisplayFailures = new HashSet<string>();

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

            // No DVC write here anymore (issue #60/#36). This used to do
            // "enumerateNvidiaDisplayHandle(0)" - an arbitrary display, not necessarily the primary
            // and not the one affectPrimaryMonitorOnly's flag was ever consulted for - then write
            // _vibranceInfo.userVibranceSettingDefault to it via getDVCInfo/setDVCLevel. But
            // SetVibranceWindowsLevel(...) has not been called yet at this point in startup (it
            // runs later, from VibranceGUI.cs's backgroundWorker_DoWork, after that method's own
            // "while (!this.IsHandleCreated) Thread.Sleep(500);" wait), so userVibranceSettingDefault
            // was still VibranceInfo's struct default of 0 - every launch stamped 0 onto whatever
            // display handle 0 happened to name, resetting a second monitor's level even though it
            // was never a game's screen and affectPrimaryMonitorOnly was never asked about it.
            // A foreground event CAN still land on the hook before SetVibranceWindowsLevel runs
            // (the app's own window appearing is one) - RestoreWindowsVibranceLevel's
            // isWindowsLevelKnown guard (see VibranceInfo) makes that a no-op instead of writing
            // the still-unknown level, rather than trying to make it unreachable. The desktop scope
            // then receives the real, saved Windows level on the first non-game foreground event
            // after SetVibranceWindowsLevel has actually run.
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
                if (ProfileToggleHelper.IsSuppressed(applicationSetting.Name))
                {
                    // Toggled off by hotkey (upstream #143). Ignore this foreground event for
                    // this game entirely - deliberately NOT a fall-through to the restore branch
                    // below: the toggle itself already restored this display
                    // (ToggleForegroundProfile), and re-running the work-list restore on every
                    // alt-tab into a suppressed game would reach displays this game never even
                    // touched (VibranceRestoreHelper.ComposeRestoreTargets is scoped to the
                    // whole work-list, not to this one game).
                    //
                    // Returns BEFORE "_gameScreen = screen" below: a suppressed game applies
                    // nothing here, so it must not become the screen a later resolution revert
                    // reasons about.
                    return;
                }

                Screen screen = Screen.FromHandle(e.Handle);
                _gameScreen = screen;

                // A display NvAPI cannot name for this screen no longer aborts resolution and
                // gamma work below - it used to, through an early return here that had nothing to
                // do with either of those features. ApplyGameVibranceLevel resolves the handle
                // itself and simply skips the vibrance write when it can't; only a landed write is
                // ever recorded as owing a restore.
                //
                // affectPrimaryMonitorOnly is deliberately NOT consulted here - the ingame level
                // always targets the screen the game itself is on, never every attached display.
                // The flag only ever scopes the WINDOWS level's restore, in the "else" branch
                // below; today that is documented solely in the checkbox's tooltip, worth stating
                // here too so a later reader does not "fix" this branch to match the restore
                // branch's flag check.
                //
                // Resolved against screen.DeviceName - the game's own screen - not IngameLevel
                // directly (upstream #147, part 2): HdrStateTracker.GetState(screen.DeviceName)
                // only ever changes what gets WRITTEN here; IsAtLevel below still reads back from
                // the real display, so the resolved level and the skip decision can never disagree
                // the way they can on AMD (see AmdDynamicVibranceProxy.ApplyResolvedGameLevel's own
                // comment for that trap).
                int resolvedIngameLevel = HdrVibranceHelper.ResolveIngameLevel(applicationSetting, HdrStateTracker.GetState(screen.DeviceName));
                if (ApplyGameVibranceLevel(_device, screen.DeviceName, resolvedIngameLevel))
                {
                    VibranceRestoreHelper.RecordGameLevelApplied(screen.DeviceName);
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

                if (!_device.IsWindowActive(ref processHandle))
                    return;

                //test if a resolution change is needed
                Screen currentScreen = Screen.FromHandle(processHandle);

                // Deliberately does NOT scope this to currentScreen (issues #95, #144): restoring
                // only "the screen that currently has focus" left a game's monitor saturated after
                // an alt-tab to another monitor while the game was still open, and left it
                // saturated forever after the game exited, if the desktop's foreground never
                // happened to land back on that same screen first. RestoreWindowsVibranceLevel
                // instead restores every display this application actually holds a game level on,
                // plus the primary (see VibranceRestoreHelper.ComposeRestoreTargets), regardless of
                // where the foreground currently is.
                RestoreWindowsVibranceLevel(_device, _vibranceInfo.affectPrimaryMonitorOnly,
                    VibranceRestoreHelper.GetPrimaryDeviceName(), _vibranceInfo.displayHandles,
                    _vibranceInfo.userVibranceSettingDefault, _vibranceInfo.isWindowsLevelKnown);

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

        /// <summary>
        /// Writes ingameLevel to gameDeviceName's NVIDIA display, unless it is already there.
        /// Returns true only when the write actually landed - only then may the caller record the
        /// display as owing a restore, the same rule ApplyGameGammaRamp already follows for the
        /// gamma ramp. Returns true without writing anything when the display is already at
        /// ingameLevel. Does not throw on a resolve failure or a failed write - both are logged
        /// once and simply skipped. The underlying P/Invokes themselves are not guarded here and
        /// can still throw, exactly as they could before this seam existed.
        /// </summary>
        internal static bool ApplyGameVibranceLevel(INvidiaVibranceDevice device, string gameDeviceName, int ingameLevel)
        {
            int displayHandle = device.TryResolveDisplayHandle(gameDeviceName);
            if (displayHandle == -1 || displayHandle == 0)
            {
                // 0 is a null NvDisplayHandle - InitializeProxy already treats 0 the same way for
                // GPU handles above. Never fall back to enumerateNvidiaDisplayHandle(0): that
                // arbitrary-display fallback is exactly what issue #60/#36 was.
                LogDisplayFailureOnce(gameDeviceName, string.Format(
                    "Could not resolve an NVIDIA display handle for screen {0}, skipping its ingame vibrance apply", gameDeviceName));
                return false;
            }

            if (device.IsAtLevel(displayHandle, ingameLevel))
            {
                ClearDisplayFailureLog(gameDeviceName);
                return true;
            }

            if (device.SetLevel(displayHandle, ingameLevel))
            {
                ClearDisplayFailureLog(gameDeviceName);
                return true;
            }

            LogDisplayFailureOnce(gameDeviceName, string.Format(
                "Failed to set the ingame vibrance level for screen {0}", gameDeviceName));
            return false;
        }

        /// <summary>
        /// Writes windowsLevel to every display owing a restore (VibranceRestoreHelper's
        /// work-list), plus the display the Windows Vibrance Level itself owns, and nothing else -
        /// deliberately not scoped to whichever screen currently has focus (see the call site in
        /// OnWinEventHook for why: issues #95 and #144). A no-op while isWindowsLevelKnown is
        /// false (see VibranceInfo) - windowsLevel is meaningless before SetVibranceWindowsLevel
        /// has actually run once, and writing it anyway would re-introduce the arbitrary-0 write
        /// InitializeProxy used to make (issue #60/#36) at one remove. allDisplayHandles is
        /// consulted only when affectPrimaryMonitorOnly is false, in which case this preserves the
        /// pre-existing all-displays behaviour exactly (skipping any -1/0 entry EnumerateDisplayHandles
        /// could still hand back - issue #138's bound only caps the list's length, it does not
        /// filter its contents) and then drops the whole work-list - that branch never wrote
        /// through it in the first place.
        ///
        /// Per display in the affectPrimaryMonitorOnly branch: unresolvable (-1 or 0) leaves the
        /// display on the work-list for a single P/Invoke (TryResolveDisplayHandle) so the next
        /// foreground event retries it. A display that resolves but fails to write costs three
        /// P/Invokes on that same retry - resolve, the IsAtLevel read-back, then SetLevel - not
        /// one; IsAtLevel only saves a call for a display that turns out to already be correct, not
        /// for one that is genuinely failing to write. A display already at windowsLevel is drained
        /// without ever calling SetLevel - a deliberate difference from RestoreCapturedGammaRamps,
        /// which drains its work-list unconditionally: gamma has no cheap way to ask "is this
        /// already correct" before writing, DVC does.
        /// </summary>
        internal static void RestoreWindowsVibranceLevel(INvidiaVibranceDevice device, bool affectPrimaryMonitorOnly,
            string primaryDeviceName, IList<int> allDisplayHandles, int windowsLevel, bool isWindowsLevelKnown)
        {
            if (!isWindowsLevelKnown)
            {
                return;
            }

            if (!affectPrimaryMonitorOnly)
            {
                if (allDisplayHandles != null && !AllDisplaysAtLevel(device, allDisplayHandles, windowsLevel))
                {
                    for (int i = 0; i < allDisplayHandles.Count; i++)
                    {
                        int handle = allDisplayHandles[i];
                        if (handle == -1 || handle == 0)
                        {
                            continue;
                        }
                        device.SetLevel(handle, windowsLevel);
                    }
                }
                VibranceRestoreHelper.ClearAllGameLevelRecords();
                return;
            }

            List<string> targets = VibranceRestoreHelper.ComposeRestoreTargets(true, primaryDeviceName);
            foreach (string deviceName in targets)
            {
                RestoreOneDisplay(device, deviceName, windowsLevel);
            }
        }

        private static bool AllDisplaysAtLevel(INvidiaVibranceDevice device, IList<int> displayHandles, int level)
        {
            for (int i = 0; i < displayHandles.Count; i++)
            {
                int handle = displayHandles[i];
                if (handle == -1 || handle == 0)
                {
                    continue;
                }
                if (!device.IsAtLevel(handle, level))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Restores deviceName to windowsLevel, exactly as before - returns true only once the
        /// level is CONFIRMED landed (already there, or a write just succeeded), false when it is
        /// still owed (unresolvable handle, or a failed write). The pre-existing foreach call
        /// site in RestoreWindowsVibranceLevel ignores this return value, so that path's own
        /// behaviour is unchanged; ToggleForegroundProfile is the new caller that actually reads
        /// it, to decide whether the toggle's suppression flip is safe to make.
        /// </summary>
        private static bool RestoreOneDisplay(INvidiaVibranceDevice device, string deviceName, int windowsLevel)
        {
            int displayHandle = device.TryResolveDisplayHandle(deviceName);
            if (displayHandle == -1 || displayHandle == 0)
            {
                LogDisplayFailureOnce(deviceName, string.Format(
                    "Could not resolve an NVIDIA display handle for screen {0}, its Windows vibrance level restore will retry on the next foreground change", deviceName));
                return false; // stays on the work-list - see the class-level comment above.
            }

            if (device.IsAtLevel(displayHandle, windowsLevel))
            {
                VibranceRestoreHelper.ClearGameLevelRecord(deviceName);
                ClearDisplayFailureLog(deviceName);
                return true;
            }

            if (device.SetLevel(displayHandle, windowsLevel))
            {
                VibranceRestoreHelper.ClearGameLevelRecord(deviceName);
                ClearDisplayFailureLog(deviceName);
                return true;
            }

            LogDisplayFailureOnce(deviceName, string.Format(
                "Failed to restore the Windows vibrance level for screen {0}, it will retry on the next foreground change", deviceName));
            return false;
        }

        /// <summary>
        /// See IVibranceProxy.ToggleForegroundProfile for the full contract. Decide (pure) picks
        /// the direction from our own recorded suppression state, never from a display read-back;
        /// this method is only the write plus the flip. The restore direction goes through
        /// RestoreOneDisplay - never RestoreWindowsVibranceLevel, which would also walk the whole
        /// work-list plus the primary and restore displays this one game never touched.
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
                // Resolved against deviceName - the same screen the write below targets - exactly
                // like OnWinEventHook's own apply branch (upstream #147, part 2).
                int resolvedIngameLevel = HdrVibranceHelper.ResolveIngameLevel(decision.Setting, HdrStateTracker.GetState(deviceName));
                if (!ApplyGameVibranceLevel(_device, deviceName, resolvedIngameLevel))
                {
                    return ProfileToggleResult.WriteFailed;
                }
                VibranceRestoreHelper.RecordGameLevelApplied(deviceName);
                ProfileToggleHelper.SetSuppressed(name, false);
                return ProfileToggleResult.ToggledOn;
            }

            if (!RestoreOneDisplay(_device, deviceName, _vibranceInfo.userVibranceSettingDefault))
            {
                return ProfileToggleResult.WriteFailed;
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
        /// game is still suppressed. The caller (VibranceGUI) only ever reaches this after
        /// confirming a real transition, so "log once per transition" needs no dedup of its own
        /// here - see ApplyGameVibranceLevel's own return value, which is exactly what gates the
        /// log line below.
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

            if (ApplyGameVibranceLevel(_device, deviceName, resolvedIngameLevel))
            {
                VibranceRestoreHelper.RecordGameLevelApplied(deviceName);
                Program.LogSafely(string.Format(
                    "HDR state for {0} is now {1} - re-applied {2}'s ingame vibrance level ({3}).",
                    deviceName, state, setting.Name, resolvedIngameLevel));
            }
        }

        private static void LogDisplayFailureOnce(string deviceName, string message)
        {
            if (_loggedDisplayFailures.Add(deviceName))
            {
                Program.LogSafely(message);
            }
        }

        private static void ClearDisplayFailureLog(string deviceName)
        {
            _loggedDisplayFailures.Remove(deviceName);
        }

        // Exists for VibranceRestoreFixture only - swaps out every static field OnWinEventHook and
        // the apply/restore methods above depend on, so a check can run the real, private static
        // OnWinEventHook (reflection is the only seam into it; it takes no instance) or the
        // Apply/RestoreWindowsVibranceLevel overloads directly, entirely against a fake device and
        // fake settings, with no call to the constructor and so no initializeLibrary() and no real
        // GPU touched. Mirrors DeviceGammaRampHelper.ResetForTests / VibranceRestoreHelper.ResetForTests.
        internal static void ResetForTests(INvidiaVibranceDevice device, VibranceInfo vibranceInfo,
            List<ApplicationSetting> applicationSettings)
        {
            _device = device ?? new RealNvidiaVibranceDevice();
            _vibranceInfo = vibranceInfo;
            _applicationSettings = applicationSettings ?? new List<ApplicationSetting>();
            _gameScreen = null;
            _loggedDisplayFailures.Clear();
            VibranceRestoreHelper.ResetForTests();
            // The toggle hotkey's own suppression state (upstream #143) - reset here too so a
            // fixture check that only calls ResetForTests, without separately remembering to
            // call ProfileToggleHelper.ResetForTests() itself, still starts from a clean slate.
            ProfileToggleHelper.ResetForTests();
        }

        // The production INvidiaVibranceDevice: the four native calls below against the real
        // vibranceDLL.dll, exactly as OnWinEventHook/GetApplicationDisplayHandle called them
        // directly before this seam existed.
        private class RealNvidiaVibranceDevice : INvidiaVibranceDevice
        {
            public bool IsWindowActive(ref IntPtr hWnd)
            {
                return isWindowActive(ref hWnd);
            }

            public int TryResolveDisplayHandle(string deviceName)
            {
                if (string.IsNullOrEmpty(deviceName))
                {
                    return -1;
                }
                // The marshaller (CharSet.Ansi on the DllImport above) copies deviceName into its
                // own native ANSI buffer for the duration of this one call and frees it afterward -
                // there is nothing here for a caller-side GCHandle to protect. The pin this replaces
                // (GCHandle.Alloc(deviceName, GCHandleType.Pinned)) pinned a managed string that the
                // marshaller was never going to touch directly in the first place.
                return getAssociatedNvidiaDisplayHandle(deviceName, deviceName.Length);
            }

            public bool IsAtLevel(int displayHandle, int level)
            {
                return equalsDVCLevel(displayHandle, level);
            }

            public bool SetLevel(int displayHandle, int level)
            {
                return setDVCLevel(displayHandle, level);
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

            // Same restore RestoreWindowsVibranceLevel already does on every non-game foreground
            // event (issue #144: vibrance used to survive the game closing because this used to
            // write only through the hijacked _vibranceInfo.defaultHandle - the game's own display,
            // if the apply branch had ever run - instead of every display actually holding a game
            // level).
            RestoreWindowsVibranceLevel(_device, _vibranceInfo.affectPrimaryMonitorOnly,
                VibranceRestoreHelper.GetPrimaryDeviceName(), _vibranceInfo.displayHandles,
                _vibranceInfo.userVibranceSettingDefault, _vibranceInfo.isWindowsLevelKnown);
        }
    }
}
