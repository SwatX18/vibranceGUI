using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Windows.Forms;
using vibrance.GUI.AMD;
using vibrance.GUI.AMD.vendor;
using vibrance.GUI.NVIDIA;

namespace vibrance.GUI.common
{
    /// <summary>
    /// Regression coverage for upstream #81's startup foreground apply - the last mechanism #137
    /// left open (docs/CODEBASE_GUIDE.md §6.8, blind spot #1): IVibranceProxy.
    /// ApplyStartupForegroundProfile, called exactly once from VibranceGUI.
    /// backgroundWorker_DoWork right after startup finishes reconciling the engine's own state
    /// (_applicationSettings and the Windows level). Run by vibrance.GUI.exe --selftest-startup.
    ///
    /// A new fixture, not an extension of ProfileToggleFixture: that file's fakes
    /// (FakeNvidiaVibranceDevice/FakeAmdAdapter) are private nested classes of that class, and it
    /// has no FakeHdrStateReader at all. Copied here from HdrVibranceFixture instead - per-fixture
    /// fakes are the convention this codebase already follows (HdrVibranceFixture's own copy of
    /// VibranceRestoreFixture's FakeNvidiaVibranceDevice says as much in its own header comment).
    ///
    /// VibranceGUI.ApplyStartupForegroundProfile's own TryGetForeground-false branch (no
    /// foreground window at all, or a foreground process this reader cannot name) lives in
    /// VibranceGUI itself and is not constructible from a fixture with no WinForms message loop
    /// running - the same reason OnToggleHotkeyPressed's and OnHdrRecheckTick's own
    /// TryGetForeground-false branches are never exercised here either. Everything below drives
    /// IVibranceProxy.ApplyStartupForegroundProfile directly, exactly the boundary VibranceGUI
    /// itself calls across.
    /// </summary>
    public static class StartupForegroundFixture
    {
        public static List<string> Run()
        {
            Checklist checklist = new Checklist();
            checklist.Lines.Add("vibranceGUI startup foreground apply self test");
            checklist.Lines.Add(string.Empty);

            RunNvidiaChecks(checklist);
            RunAmdChecks(checklist);

            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add(string.Format("PASSED {0}/{1}", checklist.Passed, checklist.Total));
            return checklist.Lines;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        // ------------------------------------------------------------------
        // NVIDIA - IVibranceProxy.ApplyStartupForegroundProfile is public, so an uninitialized
        // instance (ResetForTests plus FormatterServices.GetUninitializedObject, exactly
        // HdrVibranceFixture's own NewNvidiaInstance) can call it directly, cast to IVibranceProxy.
        // No reflection needed - unlike OnWinEventHook itself, this entry point was never private.
        // ------------------------------------------------------------------

        private static void RunNvidiaChecks(Checklist checklist)
        {
            checklist.Lines.Add("NVIDIA (IVibranceProxy.ApplyStartupForegroundProfile, via ResetForTests + a fake device):");

            CheckNvidiaMatchAppliesGameLevel(checklist);
            CheckNvidiaNoMatchIsATrueNoOp(checklist);
            CheckNvidiaEmptySettingsListIsANoOp(checklist);
            CheckNvidiaSuppressedProfileIsANoOp(checklist);
            CheckNvidiaHdrWritesHdrIngameLevel(checklist);
            CheckNvidiaSdrWritesIngameLevel(checklist);
            CheckNvidiaReturnValueTracksWhetherAnythingMatched(checklist);
            CheckNvidiaDuplicateNameSettingsAppliesFirstMatch(checklist);

            checklist.Lines.Add(string.Empty);
        }

        private static IVibranceProxy NewNvidiaProxy()
        {
            return (IVibranceProxy)FormatterServices.GetUninitializedObject(typeof(NvidiaDynamicVibranceProxy));
        }

        // S1. The happy path: a match routes through the real, private OnWinEventHook via the
        // synthesised WinEventHookEventArgs and writes exactly once.
        private static void CheckNvidiaMatchAppliesGameLevel(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestStartupS1";
            matchingSetting.IngameLevel = 70;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };

            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.neverChangeResolution = true;
            vibranceInfo.neverChangeColorSettings = true;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);

            IntPtr desktop = GetDesktopWindow();
            string gameDeviceName = Screen.FromHandle(desktop).DeviceName;

            bool applied = NewNvidiaProxy().ApplyStartupForegroundProfile(desktop, "TestStartupS1", null);

            checklist.Check(applied && device.SetLevelCallCount == 1 && device.LevelFor(gameDeviceName) == 70 &&
                VibranceRestoreHelper.HoldingCount == 1,
                string.Format("S1 (NVIDIA match): exactly one SetLevel at IngameLevel (70), HoldingCount 1, returns true, got applied={0} setLevelCalls={1} level={2} HoldingCount={3}",
                    applied, device.SetLevelCallCount, device.LevelFor(gameDeviceName), VibranceRestoreHelper.HoldingCount));
        }

        // S2 - the critical one. Guards a routing bug that reimplements this as "just call the
        // real OnWinEventHook unconditionally" instead of checking FindMatch first: with no
        // configured profile matching, that would fall into OnWinEventHook's own revert branch,
        // and - seeded exactly as a real post-SetVibranceWindowsLevel startup would leave it -
        // would really write the Windows level back over every display in the list.
        // isWindowsLevelKnown true, userVibranceSettingDefault distinct from anything the fake
        // has ever been told, affectPrimaryMonitorOnly false and a seeded, valid displayHandles
        // entry are exactly the ingredients RestoreWindowsVibranceLevel's flag-off branch needs to
        // actually call SetLevel - see that method's own comment.
        private static void CheckNvidiaNoMatchIsATrueNoOp(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            List<ApplicationSetting> settings = new List<ApplicationSetting>();

            IntPtr desktop = GetDesktopWindow();
            string gameDeviceName = Screen.FromHandle(desktop).DeviceName;
            int seededHandle = device.HandleFor(gameDeviceName);

            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.isWindowsLevelKnown = true;
            vibranceInfo.userVibranceSettingDefault = 77;
            vibranceInfo.affectPrimaryMonitorOnly = false;
            vibranceInfo.displayHandles = new List<int> { seededHandle };
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);

            bool applied = NewNvidiaProxy().ApplyStartupForegroundProfile(desktop, "TestStartupS2", null);

            checklist.Check(!applied && device.SetLevelCallCount == 0 && VibranceRestoreHelper.HoldingCount == 0,
                string.Format("S2 (NVIDIA no match - the critical one): zero device calls and HoldingCount 0, even though a naive \"just call OnWinEventHook\" implementation was set up here to really write, got applied={0} setLevelCalls={1} HoldingCount={2}",
                    applied, device.SetLevelCallCount, VibranceRestoreHelper.HoldingCount));
        }

        // S3. An empty settings list is a "no match" too (ApplicationSettingMatcher.FindMatch
        // treats null and empty identically) - pinned separately from S2, whose whole point is a
        // non-empty list that simply does not name this process.
        private static void CheckNvidiaEmptySettingsListIsANoOp(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            NvidiaDynamicVibranceProxy.ResetForTests(device, new VibranceInfo(), new List<ApplicationSetting>());

            bool applied = NewNvidiaProxy().ApplyStartupForegroundProfile(GetDesktopWindow(), "TestStartupS3", null);

            checklist.Check(!applied && device.SetLevelCallCount == 0,
                string.Format("S3 (NVIDIA, empty settings list): zero device calls, returns false, got applied={0} setLevelCalls={1}",
                    applied, device.SetLevelCallCount));
        }

        // S4. A match currently suppressed by the toggle hotkey (upstream #143) must still route
        // through OnWinEventHook - never treated the same as "no match" here, with a second copy
        // of the suppression gate. OnWinEventHook's own gate is what makes this a no-op; pins that
        // ApplyStartupForegroundProfile decides only "does a profile match", never "is it
        // currently active".
        private static void CheckNvidiaSuppressedProfileIsANoOp(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestStartupS4";
            matchingSetting.IngameLevel = 70;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };

            NvidiaDynamicVibranceProxy.ResetForTests(device, new VibranceInfo(), settings);
            ProfileToggleHelper.SetSuppressed("TestStartupS4", true);

            bool applied = NewNvidiaProxy().ApplyStartupForegroundProfile(GetDesktopWindow(), "TestStartupS4", null);

            checklist.Check(applied && device.SetLevelCallCount == 0,
                string.Format("S4 (NVIDIA, suppressed match): a match exists so this routes through and returns true, but OnWinEventHook's own suppression gate makes zero device calls, got applied={0} setLevelCalls={1}",
                    applied, device.SetLevelCallCount));

            ProfileToggleHelper.ResetForTests();
        }

        // S5. Routes through the resolved HDR level exactly like a real foreground event would -
        // HdrVibranceHelper.ResolveIngameLevel runs inside the real OnWinEventHook this drives,
        // never reimplemented here (see this fixture's own header comment; HdrVibranceFixture's W1
        // is the direct-OnWinEventHook equivalent of this same guard).
        private static void CheckNvidiaHdrWritesHdrIngameLevel(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestStartupS5";
            matchingSetting.IngameLevel = 60;
            matchingSetting.HdrIngameLevel = 20;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };

            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.neverChangeResolution = true;
            vibranceInfo.neverChangeColorSettings = true;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);

            IntPtr desktop = GetDesktopWindow();
            string gameDeviceName = Screen.FromHandle(desktop).DeviceName;
            SetHdrState(gameDeviceName, HdrDisplayState.Hdr);

            bool applied = NewNvidiaProxy().ApplyStartupForegroundProfile(desktop, "TestStartupS5", null);

            checklist.Check(applied && device.LevelFor(gameDeviceName) == 20, string.Format(
                "S5 (NVIDIA, Hdr): writes HdrIngameLevel (20), not the raw IngameLevel (60), got applied={0} level={1}",
                applied, device.LevelFor(gameDeviceName)));

            HdrStateTracker.ResetForTests(null);
        }

        // S6. The Sdr mirror of S5 - still the raw IngameLevel, proving this did not silently
        // start always writing the HDR level regardless of display state.
        private static void CheckNvidiaSdrWritesIngameLevel(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestStartupS6";
            matchingSetting.IngameLevel = 60;
            matchingSetting.HdrIngameLevel = 20;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };

            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.neverChangeResolution = true;
            vibranceInfo.neverChangeColorSettings = true;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);

            IntPtr desktop = GetDesktopWindow();
            string gameDeviceName = Screen.FromHandle(desktop).DeviceName;
            SetHdrState(gameDeviceName, HdrDisplayState.Sdr);

            bool applied = NewNvidiaProxy().ApplyStartupForegroundProfile(desktop, "TestStartupS6", null);

            checklist.Check(applied && device.LevelFor(gameDeviceName) == 60, string.Format(
                "S6 (NVIDIA, Sdr): writes the raw IngameLevel (60), not HdrIngameLevel (20), got applied={0} level={1}",
                applied, device.LevelFor(gameDeviceName)));

            HdrStateTracker.ResetForTests(null);
        }

        // S7. The return value pinned on its own, independent of any device call-count assertion -
        // a future refactor that keeps every write correct but forgets to propagate
        // ApplyStartupForegroundProfile's own return value would still be caught here.
        private static void CheckNvidiaReturnValueTracksWhetherAnythingMatched(Checklist checklist)
        {
            FakeNvidiaVibranceDevice matchDevice = new FakeNvidiaVibranceDevice();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestStartupS7Match";
            matchingSetting.IngameLevel = 60;
            List<ApplicationSetting> matchSettings = new List<ApplicationSetting> { matchingSetting };
            VibranceInfo matchVibranceInfo = new VibranceInfo();
            matchVibranceInfo.neverChangeResolution = true;
            matchVibranceInfo.neverChangeColorSettings = true;
            NvidiaDynamicVibranceProxy.ResetForTests(matchDevice, matchVibranceInfo, matchSettings);
            bool appliedWithMatch = NewNvidiaProxy().ApplyStartupForegroundProfile(GetDesktopWindow(), "TestStartupS7Match", null);

            FakeNvidiaVibranceDevice noMatchDevice = new FakeNvidiaVibranceDevice();
            NvidiaDynamicVibranceProxy.ResetForTests(noMatchDevice, new VibranceInfo(), new List<ApplicationSetting>());
            bool appliedWithNoMatch = NewNvidiaProxy().ApplyStartupForegroundProfile(GetDesktopWindow(), "TestStartupS7NoMatch", null);

            checklist.Check(appliedWithMatch && !appliedWithNoMatch, string.Format(
                "S7: the return value tracks whether a profile actually matched, checked apart from any call-count assertion, got appliedWithMatch={0} appliedWithNoMatch={1}",
                appliedWithMatch, appliedWithNoMatch));
        }

        // S11 (QA addition). Two entries in the same settings list name the same foreground
        // process - not exercised by S1-S10, which never put more than one entry in the list.
        // ApplicationSettingMatcher.FindMatch's own first-pass loop returns on the first exact
        // name match it finds (ApplicationSettingMatcher.cs), so this pins that
        // ApplyStartupForegroundProfile inherits that same deterministic "first entry in list
        // order wins" rule at its own boundary, rather than silently blending the two or throwing
        // on the ambiguity - the same way a real foreground event through OnWinEventHook already
        // would, since this drives that exact function.
        private static void CheckNvidiaDuplicateNameSettingsAppliesFirstMatch(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            ApplicationSetting firstSetting = new ApplicationSetting();
            firstSetting.Name = "TestStartupS11";
            firstSetting.IngameLevel = 33;
            ApplicationSetting secondSetting = new ApplicationSetting();
            secondSetting.Name = "TestStartupS11";
            secondSetting.IngameLevel = 88;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { firstSetting, secondSetting };

            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.neverChangeResolution = true;
            vibranceInfo.neverChangeColorSettings = true;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);

            IntPtr desktop = GetDesktopWindow();
            string gameDeviceName = Screen.FromHandle(desktop).DeviceName;

            bool applied = NewNvidiaProxy().ApplyStartupForegroundProfile(desktop, "TestStartupS11", null);

            checklist.Check(applied && device.SetLevelCallCount == 1 && device.LevelFor(gameDeviceName) == 33,
                string.Format("S11 (NVIDIA, two settings name the same process): applies the FIRST entry's IngameLevel (33), never the second's (88) and never both, got applied={0} setLevelCalls={1} level={2}",
                    applied, device.SetLevelCallCount, device.LevelFor(gameDeviceName)));
        }

        // Installs a fake IHdrStateReader that reports deviceName as state (and, for Hdr,
        // capable) and nothing else - mirrors HdrVibranceFixture.SetHdrState exactly; this file
        // keeps its own copy for the same per-fixture-fakes reason as FakeHdrStateReader itself.
        private static void SetHdrState(string deviceName, HdrDisplayState state)
        {
            FakeHdrStateReader fake = new FakeHdrStateReader();
            HdrDisplayInfo info = new HdrDisplayInfo();
            info.DeviceName = deviceName;
            info.State = state;
            info.IsHdrCapable = state == HdrDisplayState.Hdr;
            fake.NextResult = new List<HdrDisplayInfo> { info };
            HdrStateTracker.ResetForTests(fake);
        }

        // ------------------------------------------------------------------
        // AMD - AmdDynamicVibranceProxy is an ordinary instance, built through the same
        // BuildAmdProxy(adapter, settings) every other fixture uses (ProfileToggleFixture.cs:
        // ~1010-1018, HdrVibranceFixture's own copy): FakeAmdAdapter.IsAvailable() is false, so
        // the constructor never reaches "_hook.WinEventHookHandler += OnWinEventHook" and no real
        // SetWinEventHook is ever installed.
        // ------------------------------------------------------------------

        private static void RunAmdChecks(Checklist checklist)
        {
            checklist.Lines.Add("AMD (IVibranceProxy.ApplyStartupForegroundProfile, via BuildAmdProxy + a fake adapter):");

            CheckAmdMatchAppliesGameLevel(checklist);
            CheckAmdNoMatchIsATrueNoOp(checklist);
            CheckAmdHdrWritesHdrIngameLevel(checklist);

            checklist.Lines.Add(string.Empty);
        }

        private static AmdDynamicVibranceProxy BuildAmdProxy(FakeAmdAdapter adapter, List<ApplicationSetting> settings)
        {
            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> windowsResolutionSettings =
                new Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>>();
            AmdDynamicVibranceProxy proxy = new AmdDynamicVibranceProxy(adapter, settings, windowsResolutionSettings);
            proxy.SetNeverChangeColorSettings(true);
            proxy.SetNeverSwitchResolution(true);
            return proxy;
        }

        // S8. The happy path, AMD's counterpart of S1. affectPrimaryMonitorOnly true for
        // determinism (ProfileToggleFixture.cs:718-720's own convention) - this proves the
        // narrow, single-display overload is used, not the wide one every AMD apply reaches with
        // the flag at its actual default (false).
        private static void CheckAmdMatchAppliesGameLevel(Checklist checklist)
        {
            VibranceRestoreHelper.ResetForTests();
            FakeAmdAdapter adapter = new FakeAmdAdapter();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestStartupS8";
            matchingSetting.IngameLevel = 70;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };
            AmdDynamicVibranceProxy proxy = BuildAmdProxy(adapter, settings);
            proxy.SetAffectPrimaryMonitorOnly(true);
            proxy.SetVibranceWindowsLevel(50);

            IntPtr desktop = GetDesktopWindow();
            string gameDeviceName = Screen.FromHandle(desktop).DeviceName;

            bool applied = proxy.ApplyStartupForegroundProfile(desktop, "TestStartupS8", null);

            checklist.Check(applied && adapter.SetSaturationOnDisplayNames.Count == 1 &&
                adapter.SetSaturationOnDisplayNames[0] == gameDeviceName && adapter.SetSaturationOnDisplayLevels[0] == 70 &&
                adapter.SetSaturationOnAllDisplaysCallCount == 0,
                string.Format("S8 (AMD match): exactly one SetSaturationOnDisplay on the game's own screen (IngameLevel 70), SetSaturationOnAllDisplaysCallCount 0, returns true, got applied={0} perDisplayCalls={1} allDisplaysCalls={2}",
                    applied, adapter.SetSaturationOnDisplayNames.Count, adapter.SetSaturationOnAllDisplaysCallCount));

            VibranceRestoreHelper.ResetForTests();
        }

        // S9 - AMD's critical one, the mirror of S2. AmdDynamicVibranceProxy.OnWinEventHook's own
        // revert branch - the ONLY branch a routing bug here could fall into with no match - gates
        // on "GetForegroundWindow() == e.Handle" BEFORE it ever reaches RestoreWindowsVibranceLevel
        // (AmdDynamicVibranceProxy.cs, just above the restore call), so hWnd has to be the real
        // current foreground window for a naive "just call OnWinEventHook unconditionally"
        // implementation to actually reach the write this check exists to catch. Dependent on the
        // real foreground window and guarded with a before/after check + Skip rather than a
        // production-only seam, exactly like VibranceRestoreFixture's own AMD checks (A1-A6) and
        // their shared header comment.
        private static void CheckAmdNoMatchIsATrueNoOp(Checklist checklist)
        {
            VibranceRestoreHelper.ResetForTests();
            FakeAmdAdapter adapter = new FakeAmdAdapter();
            List<ApplicationSetting> settings = new List<ApplicationSetting>();
            AmdDynamicVibranceProxy proxy = BuildAmdProxy(adapter, settings);
            proxy.SetAffectPrimaryMonitorOnly(true);
            proxy.SetVibranceWindowsLevel(50);

            IntPtr foregroundBefore = AmdDynamicVibranceProxy.GetForegroundWindow();

            bool applied = proxy.ApplyStartupForegroundProfile(foregroundBefore, "TestStartupS9", null);

            if (AmdDynamicVibranceProxy.GetForegroundWindow() != foregroundBefore)
            {
                checklist.Skip("S9: AMD, no match - the foreground window changed mid test, its precondition was destroyed by a real focus change");
                return;
            }

            checklist.Check(!applied && adapter.SetSaturationOnAllDisplaysCallCount == 0 && adapter.SetSaturationOnDisplayNames.Count == 0 &&
                VibranceRestoreHelper.HoldingCount == 0,
                string.Format("S9 (AMD no match - the critical one): zero adapter calls of either kind and HoldingCount 0, even though a naive \"just call OnWinEventHook\" implementation was set up here to fall into the revert branch and write, got applied={0} allDisplaysCalls={1} perDisplayCalls={2} HoldingCount={3}",
                    applied, adapter.SetSaturationOnAllDisplaysCallCount, adapter.SetSaturationOnDisplayNames.Count, VibranceRestoreHelper.HoldingCount));

            VibranceRestoreHelper.ResetForTests();
        }

        // S10. AMD's HDR guard trap, reached through ApplyStartupForegroundProfile instead of a
        // direct OnWinEventHook reflection call - mirrors HdrVibranceFixture's A1 exactly:
        // Windows==IngameLevel==100, HdrIngameLevel=40, display Hdr. A write-only fix that leaves
        // ApplyResolvedGameLevel's own guard comparing against the raw IngameLevel would read
        // "100 != 100" and skip the write entirely, bypassing HdrVibranceHelper.ResolveIngameLevel.
        private static void CheckAmdHdrWritesHdrIngameLevel(Checklist checklist)
        {
            VibranceRestoreHelper.ResetForTests();
            FakeAmdAdapter adapter = new FakeAmdAdapter();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestStartupS10";
            matchingSetting.IngameLevel = 100;
            matchingSetting.HdrIngameLevel = 40;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };
            AmdDynamicVibranceProxy proxy = BuildAmdProxy(adapter, settings);
            proxy.SetAffectPrimaryMonitorOnly(true);
            proxy.SetVibranceWindowsLevel(100);

            IntPtr desktop = GetDesktopWindow();
            string gameDeviceName = Screen.FromHandle(desktop).DeviceName;
            SetHdrState(gameDeviceName, HdrDisplayState.Hdr);

            bool applied = proxy.ApplyStartupForegroundProfile(desktop, "TestStartupS10", null);

            int writtenLevel = adapter.SetSaturationOnDisplayLevels.Count > 0
                ? adapter.SetSaturationOnDisplayLevels[adapter.SetSaturationOnDisplayLevels.Count - 1] : int.MinValue;
            checklist.Check(applied && adapter.SetSaturationOnDisplayLevels.Count == 1 && writtenLevel == 40, string.Format(
                "S10 (AMD, Hdr guard trap): Windows==IngameLevel==100, HdrIngameLevel=40 - writes the resolved HdrIngameLevel (40), never skipped by a guard still comparing against the raw IngameLevel, got applied={0} writeCount={1} lastLevel={2}",
                applied, adapter.SetSaturationOnDisplayLevels.Count, writtenLevel));

            HdrStateTracker.ResetForTests(null);
            VibranceRestoreHelper.ResetForTests();
        }

        // Mirrors HdrVibranceFixture.FakeNvidiaVibranceDevice (itself mirroring
        // VibranceRestoreFixture's own copy), with one addition this file's checks need that
        // neither of those did: SetLevelCallCount, a plain per-instance write count, so a "zero
        // device calls" assertion (S2, S3, S4) does not have to infer it from LevelFor alone.
        private class FakeNvidiaVibranceDevice : INvidiaVibranceDevice
        {
            private readonly Dictionary<string, int> _handlesByDeviceName = new Dictionary<string, int>();
            private readonly Dictionary<int, int> _levelsByHandle = new Dictionary<int, int>();
            private int _nextHandle = 1;

            public int SetLevelCallCount;

            public int HandleFor(string deviceName)
            {
                return ResolveOrAssign(deviceName);
            }

            // The level this fake currently believes deviceName's display is at, or int.MinValue
            // if nothing has ever been written to it.
            public int LevelFor(string deviceName)
            {
                int level;
                return _levelsByHandle.TryGetValue(ResolveOrAssign(deviceName), out level) ? level : int.MinValue;
            }

            private int ResolveOrAssign(string deviceName)
            {
                int handle;
                if (!_handlesByDeviceName.TryGetValue(deviceName, out handle))
                {
                    handle = _nextHandle++;
                    _handlesByDeviceName[deviceName] = handle;
                }
                return handle;
            }

            public bool IsWindowActive(ref IntPtr hWnd)
            {
                return true;
            }

            public int TryResolveDisplayHandle(string deviceName)
            {
                if (string.IsNullOrEmpty(deviceName))
                {
                    return -1;
                }
                return ResolveOrAssign(deviceName);
            }

            public bool IsAtLevel(int displayHandle, int level)
            {
                int current;
                return _levelsByHandle.TryGetValue(displayHandle, out current) && current == level;
            }

            public bool SetLevel(int displayHandle, int level)
            {
                SetLevelCallCount++;
                _levelsByHandle[displayHandle] = level;
                return true;
            }
        }

        // Everything IAmdAdapter exposes, none of it touching real hardware - mirrors
        // HdrVibranceFixture.FakeAmdAdapter (itself trimmed from VibranceRestoreFixture/
        // StabilityFixture's own copies).
        private class FakeAmdAdapter : IAmdAdapter
        {
            public int SetSaturationOnAllDisplaysCallCount;

            public readonly List<int> SetSaturationOnDisplayLevels = new List<int>();
            public readonly List<string> SetSaturationOnDisplayNames = new List<string>();

            public void SetSaturationOnAllDisplays(int vibranceLevel)
            {
                SetSaturationOnAllDisplaysCallCount++;
            }

            public bool SetSaturationOnDisplay(int vibranceLevel, string displayName)
            {
                SetSaturationOnDisplayLevels.Add(vibranceLevel);
                SetSaturationOnDisplayNames.Add(displayName);
                return true;
            }

            public bool IsAvailable()
            {
                return false;
            }

            public void Init()
            {
            }

            public void Dispose()
            {
            }
        }

        // Mirrors HdrVibranceFixture.FakeHdrStateReader exactly - the seam SetHdrState above
        // installs to stand in for "this display is currently in HDR" with no real display
        // involved.
        private class FakeHdrStateReader : IHdrStateReader
        {
            public List<HdrDisplayInfo> NextResult = new List<HdrDisplayInfo>();
            public bool IsAvailable { get; set; }

            public FakeHdrStateReader()
            {
                IsAvailable = true;
            }

            public List<HdrDisplayInfo> ReadAll()
            {
                return NextResult;
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

            // Deliberately not counted in Total/Passed - see StabilityFixture.Checklist.Skip for
            // the convention this follows.
            public void Skip(string description)
            {
                Lines.Add(string.Format("[SKIP] {0}", description));
            }
        }
    }
}
