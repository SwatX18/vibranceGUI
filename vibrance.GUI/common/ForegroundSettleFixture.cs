using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using vibrance.GUI.AMD;
using vibrance.GUI.AMD.vendor;
using vibrance.GUI.NVIDIA;

namespace vibrance.GUI.common
{
    /// <summary>
    /// Regression coverage for the foreground settle (the Win key / quick Alt+Tab loop in which a
    /// transient shell window reverted the resolution, focus bounced to the fullscreen game, the
    /// game re-applied, and the shell window grabbed focus again) and for the shell-desktop screen
    /// resolution (a desktop click on a secondary monitor reverted the primary's game resolution).
    ///
    /// Everything runs on fakes: ForegroundSettler's timer (a hand-fired IResolutionAdoptionTimer),
    /// its foreground reader, its clock and its cursor; the display through
    /// ResolutionChangeFixture.FakeDisplayModeDevice; vibrance through a fake NVIDIA device / AMD
    /// adapter. The proxies' real OnWinEventHook is driven by reflection. There must never be a
    /// hardware variant. Gamma is covered through isColorSettingApplied only: the proxies' gamma
    /// device is the real one (no seam), so the baseline map is kept empty and the restore is
    /// observed as the flag flipping, never as a ramp write. Run by
    /// vibrance.GUI.exe --selftest-foreground-settle.
    /// </summary>
    public static class ForegroundSettleFixture
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        private const int WindowsLevel = 44;
        private const int IngameLevel = 60;
        private const string NonGameName = "NotAGameProcess";

        public static List<string> Run()
        {
            Checklist checklist = new Checklist();
            checklist.Lines.Add("vibranceGUI foreground settle self test");
            checklist.Lines.Add(string.Empty);

            try
            {
                RunPolicyChecks(checklist);
                RunResolverChecks(checklist);
                RunNvidiaChecks(checklist);
                RunAmdChecks(checklist);
            }
            finally
            {
                RestoreStatics();
            }

            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add(string.Format("PASSED {0}/{1}", checklist.Passed, checklist.Total));
            return checklist.Lines;
        }

        private static void RestoreStatics()
        {
            ForegroundSettler.Install(null);
            DesktopScreenResolver.IsShellDesktopOverride = null;
            DeviceGammaRampHelper.ResetForTests();
            ResolutionChangeNotifier.ResetForTests();
            ResolutionRestoreHelper.ResetForTests();
            ResolutionRestoreStore.ResetForTests(null);
            ResolutionHelper.ResetForTests();
            VibranceRestoreHelper.ResetForTests();
        }

        // ------------------------------------------------------------------
        // Fakes.
        // ------------------------------------------------------------------

        // Captures the callback and fires on demand. Fire clears the callback BEFORE invoking it,
        // exactly as FormsResolutionAdoptionTimer.OnTick does, so a callback that re-arms is
        // not mistaken for the one that just ran.
        private class FakeTimer : IResolutionAdoptionTimer
        {
            private Action _callback;
            public int LastDelay = -1;
            public int RestartCalls;
            public int CancelCalls;

            public bool Armed { get { return _callback != null; } }

            public void Restart(int delayMs, Action onElapsed)
            {
                RestartCalls++;
                LastDelay = delayMs;
                _callback = onElapsed;
            }

            public void Cancel()
            {
                CancelCalls++;
                _callback = null;
            }

            public void Fire()
            {
                Action callback = _callback;
                _callback = null;
                if (callback != null)
                    callback();
            }
        }

        private class FakeReader : IForegroundWindowReader
        {
            public IntPtr Hwnd;
            public string Name = NonGameName;
            public string Path;
            public bool Ok = true;
            public int Calls;

            public bool TryGetForeground(out IntPtr hWnd, out string processName, out string processImagePath)
            {
                Calls++;
                hWnd = Ok ? Hwnd : IntPtr.Zero;
                processName = Ok ? Name : null;
                processImagePath = Ok ? Path : null;
                return Ok;
            }
        }

        private class FakeNvidiaVibranceDevice : INvidiaVibranceDevice
        {
            private readonly Dictionary<string, IntPtr> _handlesByDeviceName = new Dictionary<string, IntPtr>();
            private readonly Dictionary<IntPtr, int> _levelsByHandle = new Dictionary<IntPtr, int>();
            private int _nextHandle = 1;
            public int SetLevelCalls;

            public int LevelFor(string deviceName)
            {
                int level;
                return _levelsByHandle.TryGetValue(ResolveOrAssign(deviceName), out level) ? level : int.MinValue;
            }

            private IntPtr ResolveOrAssign(string deviceName)
            {
                IntPtr handle;
                if (!_handlesByDeviceName.TryGetValue(deviceName, out handle))
                {
                    handle = new IntPtr(_nextHandle++);
                    _handlesByDeviceName[deviceName] = handle;
                }
                return handle;
            }

            public bool IsWindowActive(ref IntPtr hWnd) { return true; }

            public IntPtr TryResolveDisplayHandle(string deviceName)
            {
                if (string.IsNullOrEmpty(deviceName))
                    return NvidiaDynamicVibranceProxy.InvalidDisplayHandle;
                return ResolveOrAssign(deviceName);
            }

            public bool IsAtLevel(IntPtr displayHandle, int level)
            {
                int current;
                return _levelsByHandle.TryGetValue(displayHandle, out current) && current == level;
            }

            public bool SetLevel(IntPtr displayHandle, int level)
            {
                SetLevelCalls++;
                _levelsByHandle[displayHandle] = level;
                return true;
            }
        }

        private class FakeAmdAdapter : IAmdAdapter
        {
            public readonly List<int> Levels = new List<int>();
            public readonly List<string> Names = new List<string>();

            public void SetSaturationOnAllDisplays(int vibranceLevel) { }

            public bool SetSaturationOnDisplay(int vibranceLevel, string displayName)
            {
                Levels.Add(vibranceLevel);
                Names.Add(displayName);
                return true;
            }

            public bool IsAvailable() { return false; }
            public void Init() { }
            public void Dispose() { }
        }

        // ------------------------------------------------------------------
        // Policy (ForegroundSettler on its own).
        // ------------------------------------------------------------------

        private static ForegroundSettler NewSettler(FakeTimer timer, FakeReader reader, int[] clock)
        {
            return new ForegroundSettler(timer, reader, delegate { return clock[0]; });
        }

        private static void RunPolicyChecks(Checklist checklist)
        {
            checklist.Lines.Add("ForegroundSettler policy (fake timer, reader, clock):");

            // F8 - no timer: everything is immediate and nothing is ever armed.
            ForegroundSettler immediate = new ForegroundSettler(null, null, null);
            bool deferredRevert = immediate.TryDeferRevert(delegate { });
            bool deferredApply = immediate.TryDeferApply(delegate { });
            immediate.NoteModeChanged();
            immediate.Cancel();
            checklist.Check(!deferredRevert && !deferredApply,
                "F8: a settler with no timer never defers a revert or an apply");
            ForegroundSettler.Install(null);
            checklist.Check(!ForegroundSettler.Shared.TryDeferRevert(delegate { }) && !ForegroundSettler.Shared.TryDeferApply(delegate { }),
                "F8: Install(null) restores the immediate default");

            // F1 (policy half) - a revert arms with the settle delay when no hold is running.
            FakeTimer timer = new FakeTimer();
            FakeReader reader = new FakeReader { Hwnd = new IntPtr(0x1234), Name = NonGameName };
            int[] clock = { 1000 };
            ForegroundSettler settler = NewSettler(timer, reader, clock);
            bool deferred = settler.TryDeferRevert(delegate { });
            checklist.Check(deferred && timer.Armed && timer.LastDelay == 400 && ForegroundSettler.SettleDelayMs == 400,
                string.Format("F1: a revert with no mode change on record arms the timer with 400, got {0}", timer.LastDelay));
            checklist.Check(reader.Calls == 1, string.Format("arming reads the foreground once to remember its hwnd, got {0} reads", reader.Calls));

            // Constants are what the criteria say they are.
            checklist.Check(ForegroundSettler.ModeChangeHoldMs == 1500, "ModeChangeHoldMs is 1500");

            // F10 - Cancel clears the pending callback so a late Fire does nothing.
            int fired = 0;
            timer = new FakeTimer();
            settler = NewSettler(timer, reader, clock);
            settler.TryDeferRevert(delegate (WinEventHookEventArgs e) { fired++; });
            settler.Cancel();
            timer.Fire();
            checklist.Check(!timer.Armed && fired == 0, string.Format("F10: Cancel() clears a pending callback; a later fire runs nothing, ran {0}", fired));
            bool cancelThrew = false;
            try { settler.Cancel(); }
            catch (Exception) { cancelThrew = true; }
            checklist.Check(!cancelThrew && !timer.Armed, "F10: Cancel() with nothing pending is harmless");

            // F6 - apply deferral only inside the hold.
            timer = new FakeTimer();
            clock[0] = 5000;
            settler = NewSettler(timer, reader, clock);
            checklist.Check(!settler.TryDeferApply(delegate { }) && !timer.Armed,
                "F6: with no mode change on record an apply is not deferred");
            settler.NoteModeChanged();
            clock[0] = 5000 + 300;
            bool applyDeferred = settler.TryDeferApply(delegate { });
            checklist.Check(applyDeferred && timer.Armed && timer.LastDelay == 1200,
                string.Format("F6: an apply 300ms after a mode change is deferred by the remaining hold (1200), got {0}/{1}", applyDeferred, timer.LastDelay));
            timer.Cancel();
            clock[0] = 5000 + 1499;
            applyDeferred = settler.TryDeferApply(delegate { });
            checklist.Check(applyDeferred && timer.LastDelay == 400,
                string.Format("F6: an apply 1499ms after a mode change is still deferred, with the 400 floor, got {0}/{1}", applyDeferred, timer.LastDelay));
            timer.Cancel();
            clock[0] = 5000 + 1500;
            checklist.Check(!settler.TryDeferApply(delegate { }) && !timer.Armed,
                "F6: an apply exactly 1500ms after a mode change applies immediately");
            clock[0] = 5000 + 60000;
            checklist.Check(!settler.TryDeferApply(delegate { }) && !timer.Armed,
                "F6: an apply long after a mode change applies immediately");

            // F7 - revert within the hold uses the remaining hold.
            timer = new FakeTimer();
            clock[0] = 9000;
            settler = NewSettler(timer, reader, clock);
            settler.NoteModeChanged();
            clock[0] = 9000 + 200;
            settler.TryDeferRevert(delegate { });
            checklist.Check(timer.LastDelay == 1300, string.Format("F7: a revert 200ms after a mode change waits the remaining hold (1300), got {0}", timer.LastDelay));
            clock[0] = 9000 + 1200;
            settler.TryDeferRevert(delegate { });
            checklist.Check(timer.LastDelay == 400, string.Format("F7: with less than 400 of hold left the settle delay wins, got {0}", timer.LastDelay));
            clock[0] = 9000 + 1500;
            settler.TryDeferRevert(delegate { });
            checklist.Check(timer.LastDelay == 400, string.Format("F7: after the hold a revert is plain 400, got {0}", timer.LastDelay));
            checklist.Check(timer.RestartCalls == 3, "last event wins: each deferral restarts the one timer");

            // tickCount wraparound.
            timer = new FakeTimer();
            clock[0] = int.MaxValue - 100;
            settler = NewSettler(timer, reader, clock);
            settler.NoteModeChanged();
            clock[0] = unchecked(int.MaxValue - 100 + 300);
            settler.TryDeferRevert(delegate { });
            checklist.Check(clock[0] < 0 && timer.LastDelay == 1200,
                string.Format("wraparound: 300ms after a note taken near int.MaxValue the remaining hold is 1200 (tick now {0}), got {1}", clock[0], timer.LastDelay));
            bool wrapApply = settler.TryDeferApply(delegate { });
            checklist.Check(wrapApply, "wraparound: an apply across the wrap is still deferred inside the hold");
            clock[0] = unchecked(int.MaxValue - 100 + 1600);
            checklist.Check(!settler.TryDeferApply(delegate { }), "wraparound: 1600ms after a note taken near int.MaxValue the hold is over");
            // The clock reading earlier than the note is no hold at all, not a huge one.
            timer = new FakeTimer();
            clock[0] = 7000;
            settler = NewSettler(timer, reader, clock);
            settler.NoteModeChanged();
            clock[0] = 6000;
            settler.TryDeferRevert(delegate { });
            checklist.Check(timer.LastDelay == 400 && !settler.TryDeferApply(delegate { }),
                string.Format("a clock reading earlier than the note counts as no hold, got delay {0}", timer.LastDelay));

            // The fire re-reads the live foreground and re-dispatches it.
            timer = new FakeTimer();
            reader = new FakeReader { Hwnd = new IntPtr(0xA1), Name = "armedName", Path = @"C:\a.exe" };
            clock[0] = 1;
            settler = NewSettler(timer, reader, clock);
            Point armedCursor = new Point(10, 20);
            Point laterCursor = new Point(3000, 4000);
            settler.CursorReader = delegate { return armedCursor; };
            List<WinEventHookEventArgs> seen = new List<WinEventHookEventArgs>();
            settler.TryDeferRevert(delegate (WinEventHookEventArgs e) { seen.Add(e); });
            reader.Hwnd = new IntPtr(0xA1);
            reader.Name = "firedName";
            reader.Path = @"C:\b.exe";
            settler.CursorReader = delegate { return laterCursor; };
            timer.Fire();
            checklist.Check(seen.Count == 1 && seen[0].ProcessName == "firedName" && seen[0].ProcessImagePath == @"C:\b.exe" && seen[0].Handle == new IntPtr(0xA1),
                "the fire re-dispatches what the reader reports NOW, not what armed it");
            checklist.Check(seen.Count == 1 && seen[0].CursorPosition == armedCursor,
                "same window at fire as at arm: the cursor captured at arm time is used");

            timer = new FakeTimer();
            reader = new FakeReader { Hwnd = new IntPtr(0xA1) };
            settler = NewSettler(timer, reader, clock);
            settler.CursorReader = delegate { return armedCursor; };
            seen.Clear();
            settler.TryDeferRevert(delegate (WinEventHookEventArgs e) { seen.Add(e); });
            reader.Hwnd = new IntPtr(0xB2);
            settler.CursorReader = delegate { return laterCursor; };
            timer.Fire();
            checklist.Check(seen.Count == 1 && seen[0].Handle == new IntPtr(0xB2) && seen[0].CursorPosition == laterCursor,
                "a different window at fire than at arm: the armed cursor is dropped and the current one used");

            // F5 (policy half) - a failed read at the fire does nothing.
            timer = new FakeTimer();
            reader = new FakeReader { Hwnd = new IntPtr(0xA1) };
            settler = NewSettler(timer, reader, clock);
            seen.Clear();
            settler.TryDeferRevert(delegate (WinEventHookEventArgs e) { seen.Add(e); });
            reader.Ok = false;
            timer.Fire();
            checklist.Check(seen.Count == 0, "F5: a fire whose foreground read fails dispatches nothing (the one retry is checked below)");

            // A reader that fails at ARM time must not stop the deferral (the fire read decides).
            timer = new FakeTimer();
            reader = new FakeReader { Hwnd = new IntPtr(0xA1), Ok = false };
            settler = NewSettler(timer, reader, clock);
            seen.Clear();
            bool armedDespiteFailedRead = settler.TryDeferRevert(delegate (WinEventHookEventArgs e) { seen.Add(e); });
            reader.Ok = true;
            timer.Fire();
            checklist.Check(armedDespiteFailedRead && seen.Count == 1, "a failed read at arm time still defers; the fire read dispatches");

            // A null reader never throws, never dispatches.
            timer = new FakeTimer();
            settler = new ForegroundSettler(timer, null, delegate { return 0; });
            seen.Clear();
            bool threw = false;
            try
            {
                settler.TryDeferRevert(delegate (WinEventHookEventArgs e) { seen.Add(e); });
                timer.Fire();
            }
            catch (Exception) { threw = true; }
            checklist.Check(!threw && seen.Count == 0, "a settler with no reader neither throws nor dispatches");

            // A fire dispatches once only.
            timer = new FakeTimer();
            reader = new FakeReader { Hwnd = new IntPtr(0xA1) };
            settler = NewSettler(timer, reader, clock);
            seen.Clear();
            settler.TryDeferRevert(delegate (WinEventHookEventArgs e) { seen.Add(e); });
            timer.Fire();
            timer.Fire();
            checklist.Check(seen.Count == 1, string.Format("a deferral dispatches exactly once, got {0}", seen.Count));

            // F5 retry - a failed read at the fire re-arms ONCE at the settle delay, so a NULL
            // foreground during a UAC / secure desktop cannot drop the revert.
            timer = new FakeTimer();
            reader = new FakeReader { Hwnd = new IntPtr(0xA1) };
            settler = NewSettler(timer, reader, clock);
            seen.Clear();
            settler.TryDeferRevert(delegate (WinEventHookEventArgs e) { seen.Add(e); });
            reader.Ok = false;
            timer.Fire();
            checklist.Check(seen.Count == 0 && timer.Armed && timer.LastDelay == 400 && timer.RestartCalls == 2,
                string.Format("retry: a failed read at the fire re-arms once at 400, got armed={0} delay={1} restarts={2}", timer.Armed, timer.LastDelay, timer.RestartCalls));
            reader.Ok = true;
            timer.Fire();
            checklist.Check(seen.Count == 1 && !timer.Armed, "retry: when the retry's read succeeds it dispatches the live foreground, once");

            timer = new FakeTimer();
            reader = new FakeReader { Hwnd = new IntPtr(0xA1) };
            settler = NewSettler(timer, reader, clock);
            seen.Clear();
            settler.TryDeferRevert(delegate (WinEventHookEventArgs e) { seen.Add(e); });
            reader.Ok = false;
            timer.Fire();
            timer.Fire();
            checklist.Check(seen.Count == 0 && !timer.Armed && timer.RestartCalls == 2,
                string.Format("retry: when the retry's read fails too nothing more is armed and nothing dispatched, restarts {0}", timer.RestartCalls));
            settler.TryDeferRevert(delegate (WinEventHookEventArgs e) { seen.Add(e); });
            timer.Fire();
            checklist.Check(timer.Armed && timer.RestartCalls == 4,
                "retry: a later event gets its own retry (the allowance is per event, not per settler)");

            timer = new FakeTimer();
            reader = new FakeReader { Hwnd = new IntPtr(0xA1) };
            settler = NewSettler(timer, reader, clock);
            seen.Clear();
            settler.TryDeferRevert(delegate (WinEventHookEventArgs e) { seen.Add(e); });
            reader.Ok = false;
            timer.Fire();
            settler.TryDeferRevert(delegate (WinEventHookEventArgs e) { seen.Add(e); });
            timer.Fire();
            checklist.Check(timer.Armed && timer.RestartCalls == 4,
                "retry: a new event arriving while a retry is pending replaces it and earns its own retry");

            timer = new FakeTimer();
            reader = new FakeReader { Hwnd = new IntPtr(0xA1) };
            settler = NewSettler(timer, reader, clock);
            seen.Clear();
            settler.TryDeferRevert(delegate (WinEventHookEventArgs e) { seen.Add(e); });
            reader.Ok = false;
            timer.Fire();
            settler.Cancel();
            reader.Ok = true;
            timer.Fire();
            checklist.Check(seen.Count == 0 && !timer.Armed, "retry: Cancel() drops a pending retry too");

            // F12 - maximum wait. A trailing debounce that every event under 400ms apart re-arms
            // would postpone a revert forever; each re-arm is clamped so the fire is no later than
            // first arm + MaxSettleWaitMs (1ms floor once the budget is spent).
            checklist.Check(ForegroundSettler.MaxSettleWaitMs == 3000, "MaxSettleWaitMs is 3000");
            timer = new FakeTimer();
            reader = new FakeReader { Hwnd = new IntPtr(0xA1) };
            clock[0] = 20000;
            settler = NewSettler(timer, reader, clock);
            settler.TryDeferRevert(delegate { });
            checklist.Check(timer.LastDelay == 400, string.Format("max wait: the first arm of a chain is plain 400, got {0}", timer.LastDelay));
            clock[0] = 20000 + 300;
            settler.TryDeferRevert(delegate { });
            checklist.Check(timer.LastDelay == 400, string.Format("max wait: an early re-arm is plain 400, got {0}", timer.LastDelay));
            clock[0] = 20000 + 2700;
            settler.TryDeferRevert(delegate { });
            checklist.Check(timer.LastDelay == 300, string.Format("max wait: a re-arm 2700ms into the chain is clamped to the 300 left, got {0}", timer.LastDelay));
            clock[0] = 20000 + 2990;
            settler.TryDeferRevert(delegate { });
            checklist.Check(timer.LastDelay == 10, string.Format("max wait: a re-arm 2990ms into the chain is clamped to 10, got {0}", timer.LastDelay));
            clock[0] = 20000 + 3000;
            settler.TryDeferRevert(delegate { });
            checklist.Check(timer.LastDelay == 1, string.Format("max wait: a re-arm exactly at the budget fires at the 1ms floor, got {0}", timer.LastDelay));
            clock[0] = 20000 + 3500;
            settler.TryDeferRevert(delegate { });
            checklist.Check(timer.LastDelay == 1, string.Format("max wait: a re-arm past the budget still fires at the 1ms floor, got {0}", timer.LastDelay));
            timer.Fire();
            clock[0] = 20000 + 4000;
            settler.TryDeferRevert(delegate { });
            checklist.Check(timer.LastDelay == 400, string.Format("max wait: once the chain has fired the next arm starts a new chain, got {0}", timer.LastDelay));
            clock[0] = 20000 + 6900;
            settler.TryDeferRevert(delegate { });
            checklist.Check(timer.LastDelay == 100, string.Format("max wait: ...and its budget counts from that new chain's first arm (2900ms in, 100 left), got {0}", timer.LastDelay));
            settler.Cancel();
            clock[0] = 20000 + 7000;
            settler.TryDeferRevert(delegate { });
            clock[0] = 20000 + 9900;
            settler.TryDeferRevert(delegate { });
            checklist.Check(timer.LastDelay == 100, string.Format("max wait (control for the Cancel() check below): 2900ms into a fresh chain the delay is 100, got {0}", timer.LastDelay));
            settler.Cancel();
            clock[0] = 20000 + 9910;
            settler.TryDeferRevert(delegate { });
            checklist.Check(timer.LastDelay == 400, string.Format("max wait: Cancel() ends the chain, the next arm is plain 400, got {0}", timer.LastDelay));

            // The clamp also bounds a long hold: a mode change of ours late in a chain must not
            // push the fire past the budget.
            timer = new FakeTimer();
            clock[0] = 50000;
            settler = NewSettler(timer, reader, clock);
            settler.TryDeferRevert(delegate { });
            clock[0] = 50000 + 2800;
            settler.NoteModeChanged();
            settler.TryDeferRevert(delegate { });
            checklist.Check(timer.LastDelay == 200, string.Format("max wait: a 1500 hold armed 2800ms into a chain is clamped to the 200 left, got {0}", timer.LastDelay));

            // Wraparound: the chain start is taken just before Environment.TickCount wraps.
            timer = new FakeTimer();
            clock[0] = int.MaxValue - 500;
            settler = NewSettler(timer, reader, clock);
            settler.TryDeferRevert(delegate { });
            clock[0] = unchecked(int.MaxValue - 500 + 2800);
            settler.TryDeferRevert(delegate { });
            checklist.Check(clock[0] < 0 && timer.LastDelay == 200,
                string.Format("max wait wraparound: 2800ms into a chain started near int.MaxValue the budget left is 200 (tick now {0}), got {1}", clock[0], timer.LastDelay));
            clock[0] = unchecked(int.MaxValue - 500 + 3100);
            settler.TryDeferRevert(delegate { });
            checklist.Check(timer.LastDelay == 1, string.Format("max wait wraparound: past the budget across the wrap fires at the 1ms floor, got {0}", timer.LastDelay));

            // A clock reading earlier than the chain start is not a huge budget.
            timer = new FakeTimer();
            clock[0] = 9000;
            settler = NewSettler(timer, reader, clock);
            settler.TryDeferRevert(delegate { });
            clock[0] = 8000;
            settler.TryDeferRevert(delegate { });
            checklist.Check(timer.LastDelay == 400, string.Format("max wait: a clock reading earlier than the chain start leaves the delay unclamped, got {0}", timer.LastDelay));

            // No clock: no clamp, no throw.
            timer = new FakeTimer();
            settler = new ForegroundSettler(timer, reader, null);
            threw = false;
            try { settler.TryDeferRevert(delegate { }); }
            catch (Exception) { threw = true; }
            checklist.Check(!threw && timer.LastDelay == 400, "max wait: a settler with no clock still arms at 400 and does not throw");

            checklist.Lines.Add(string.Empty);
        }

        // ------------------------------------------------------------------
        // DesktopScreenResolver, without a game.
        // ------------------------------------------------------------------

        private static void RunResolverChecks(Checklist checklist)
        {
            checklist.Lines.Add("DesktopScreenResolver (seam and real window classification):");
            try
            {
                IntPtr desktop = GetDesktopWindow();
                IntPtr shell = GetShellWindow();

                checklist.Check(!DesktopScreenResolver.IsShellDesktop(IntPtr.Zero), "IntPtr.Zero is not the shell desktop");
                checklist.Check(!DesktopScreenResolver.IsShellDesktop(desktop), "GetDesktopWindow() (the root, class #32769) is not the shell desktop");
                if (shell != IntPtr.Zero)
                    checklist.Check(DesktopScreenResolver.IsShellDesktop(shell), "GetShellWindow() is the shell desktop");
                else
                    checklist.Skip("no shell window on this machine/session - cannot check GetShellWindow() classification");

                NativeWindow own = new NativeWindow();
                try
                {
                    own.CreateHandle(new CreateParams());
                    checklist.Check(!DesktopScreenResolver.IsShellDesktop(own.Handle), "an ordinary window of this process is not the shell desktop");
                }
                finally
                {
                    own.DestroyHandle();
                }

                // F9 - the seam routes the decision: shell desktop -> FromPoint, otherwise FromHandle.
                List<IntPtr> asked = new List<IntPtr>();
                IntPtr probe = new IntPtr(0x7777);
                DesktopScreenResolver.IsShellDesktopOverride = delegate (IntPtr h) { asked.Add(h); return true; };
                Screen[] screens = Screen.AllScreens;
                Point onLast = Center(screens[screens.Length - 1]);
                Screen viaPoint = DesktopScreenResolver.Resolve(probe, onLast);
                checklist.Check(asked.Count == 1 && asked[0] == probe, "F9: Resolve asks the shell-desktop test about the window it was given");
                checklist.Check(viaPoint.Equals(Screen.FromPoint(onLast)), "F9: a shell desktop resolves to the screen under the cursor");

                DesktopScreenResolver.IsShellDesktopOverride = delegate (IntPtr h) { return false; };
                Screen viaHandle = DesktopScreenResolver.Resolve(desktop, onLast);
                checklist.Check(viaHandle.Equals(Screen.FromHandle(desktop)), "F9: any other window resolves exactly as Screen.FromHandle does, ignoring the cursor");

                if (screens.Length >= 2)
                {
                    Screen primary = Screen.FromHandle(desktop);
                    Screen other = null;
                    foreach (Screen s in screens)
                    {
                        if (!s.Equals(primary)) { other = s; break; }
                    }
                    DesktopScreenResolver.IsShellDesktopOverride = delegate { return true; };
                    Screen desk = DesktopScreenResolver.Resolve(desktop, Center(other));
                    checklist.Check(desk.Equals(other) && !desk.Equals(primary),
                        "F9: a shell desktop with the cursor on a secondary screen resolves to that screen, not the primary FromHandle would pick");
                }
                else
                {
                    checklist.Skip("F9: only one screen here - the cursor-vs-handle difference cannot be observed");
                }
            }
            finally
            {
                DesktopScreenResolver.IsShellDesktopOverride = null;
            }
            checklist.Lines.Add(string.Empty);
        }

        private static Point Center(Screen s)
        {
            return new Point(s.Bounds.Left + s.Bounds.Width / 2, s.Bounds.Top + s.Bounds.Height / 2);
        }

        // ------------------------------------------------------------------
        // Scenarios shared by both vendors.
        // ------------------------------------------------------------------

        private abstract class Scenario
        {
            public readonly string GameName;
            public readonly FakeTimer Timer = new FakeTimer();
            public readonly FakeReader Reader = new FakeReader();
            public readonly int[] Clock = { 100000 };
            public readonly ResolutionChangeFixture.FakeDisplayModeDevice Modes = new ResolutionChangeFixture.FakeDisplayModeDevice();
            public readonly ResolutionModeWrapper DesktopTarget = ResolutionChangeFixture.BuildTarget(1920, 1080, 32, 60, (uint)Dmdfo.Default);
            public readonly ResolutionModeWrapper GameTarget = ResolutionChangeFixture.BuildTarget(2560, 1440, 32, 144, (uint)Dmdfo.Default);
            public IntPtr Hwnd;
            public string GameDevice;
            public Point Cursor;
            public ForegroundSettler Settler;

            protected Scenario(string gameName)
            {
                GameName = gameName;
                ResolutionHelper.ResetForTests();
                ResolutionChangeNotifier.ResetForTests();
                ResolutionRestoreHelper.ResetForTests();
                ResolutionRestoreStore.ResetForTests(null);
                VibranceRestoreHelper.ResetForTests();
                DeviceGammaRampHelper.ResetForTests();
                DesktopScreenResolver.IsShellDesktopOverride = null;
            }

            protected Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> BuildWindowsSettings()
            {
                Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> settings =
                    new Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>>();
                settings[GameDevice] = Tuple.Create(DesktopTarget, new List<ResolutionModeWrapper> { DesktopTarget, GameTarget });
                return settings;
            }

            protected ApplicationSetting BuildApplicationSetting(int ingameLevel)
            {
                ApplicationSetting setting = new ApplicationSetting();
                setting.Name = GameName;
                setting.IngameLevel = ingameLevel;
                // Only reached when a check clears neverChangeResolution (F11); every other scenario
                // keeps it set, so the apply branch never looks at these.
                setting.IsResolutionChangeNeeded = true;
                setting.ResolutionSettings = GameTarget;
                return setting;
            }

            // Installs the deferring settler. Done AFTER the proxy is built (NVIDIA's ResetForTests
            // reinstalls the immediate default).
            public void UseTimer()
            {
                Settler = new ForegroundSettler(Timer, Reader, delegate { return Clock[0]; });
                Settler.CursorReader = delegate { return Cursor; };
                ForegroundSettler.Install(Settler);
                Reader.Hwnd = Hwnd;
            }

            public void UseNoTimer()
            {
                ForegroundSettler.Install(null);
            }

            public void PutGameModeOnTheFakeDisplay()
            {
                // The apply branch (neverChangeResolution cleared by SeedApplied) asks
                // ResolutionHelper.IsResolutionChangeNeeded about the display, which reads the REAL
                // one unless overridden: the apply would try a real mode change (and fail, clearing
                // isResolutionChangeApplied) on any monitor whose real mode differs from the fake
                // game mode. Answering from the fake display makes the outcome independent of the machine.
                ResolutionHelper.DeviceOverrideForTests = Modes;
                Modes.SetCurrentMode(GameDevice, ResolutionChangeFixture.BuildDevmode(2560, 1440, 32, 144, 0));
                ResolutionRestoreHelper.RecordModeApplied(GameDevice, GameTarget, DesktopTarget);
            }

            public bool GameModeLanded
            {
                get { return GameTarget.MatchesAchievedMode(Modes.GetCurrentMode(GameDevice)); }
            }

            public bool DesktopRestored
            {
                get { return DesktopTarget.MatchesAchievedMode(Modes.GetCurrentMode(GameDevice)); }
            }

            // Calls that actually WRITE a mode (CDS_TEST probes excluded).
            public int ModeWrites
            {
                get
                {
                    int n = 0;
                    foreach (ResolutionChangeFixture.FakeDisplayModeDevice.RecordedCall call in Modes.CallLog)
                    {
                        if ((call.Flags & ChangeDisplaySettingsFlags.CdsUpdateregistry) != 0)
                            n++;
                    }
                    return n;
                }
            }

            public void Game() { Reader.Name = GameName; Raise(GameName); }
            public void NonGame() { Reader.Name = NonGameName; Raise(NonGameName); }

            public abstract void Raise(string processName);
            // Game resolution and gamma flags as if the apply had landed one (a real mode set /
            // gamma write never happens in a fixture).
            public abstract void SeedApplied();
            public abstract void SetNeverChangeResolution(bool value);
            public abstract bool IsResolutionApplied { get; }
            public abstract bool IsColorApplied { get; }
            public abstract int Level { get; }
            public abstract int Writes { get; }
            public abstract void Dispose();
        }

        private class NvidiaScenario : Scenario
        {
            public readonly FakeNvidiaVibranceDevice Device = new FakeNvidiaVibranceDevice();
            private readonly MethodInfo _onWinEventHook = typeof(NvidiaDynamicVibranceProxy).GetMethod("OnWinEventHook", BindingFlags.NonPublic | BindingFlags.Static);
            private readonly FieldInfo _vibranceInfoField = typeof(NvidiaDynamicVibranceProxy).GetField("_vibranceInfo", BindingFlags.NonPublic | BindingFlags.Static);
            private readonly FieldInfo _windowsSettingsField = typeof(NvidiaDynamicVibranceProxy).GetField("_windowsResolutionSettings", BindingFlags.NonPublic | BindingFlags.Static);

            public NvidiaScenario(string gameName) : base(gameName)
            {
                Hwnd = GetDesktopWindow();
                GameDevice = Screen.FromHandle(Hwnd).DeviceName;

                VibranceInfo info = new VibranceInfo();
                info.affectPrimaryMonitorOnly = true;
                info.neverChangeResolution = true;
                info.neverChangeColorSettings = true;
                info.isWindowsLevelKnown = true;
                info.userVibranceSettingDefault = WindowsLevel;
                info.displayHandles = new List<IntPtr>();
                NvidiaDynamicVibranceProxy.ResetForTests(Device, info, new List<ApplicationSetting> { BuildApplicationSetting(IngameLevel) });
                NvidiaDynamicVibranceProxy.SetGameExitWatcherForTests(null, Modes);
                _windowsSettingsField.SetValue(null, BuildWindowsSettings());
            }

            public override void Raise(string processName)
            {
                _onWinEventHook.Invoke(null, new object[] { null, new WinEventHookEventArgs { Handle = Hwnd, ProcessName = processName, ProcessImagePath = null, CursorPosition = Cursor } });
            }

            public override void SetNeverChangeResolution(bool value)
            {
                VibranceInfo info = (VibranceInfo)_vibranceInfoField.GetValue(null);
                info.neverChangeResolution = value;
                _vibranceInfoField.SetValue(null, info);
            }

            public override void SeedApplied()
            {
                VibranceInfo info = (VibranceInfo)_vibranceInfoField.GetValue(null);
                info.neverChangeResolution = false;
                info.isResolutionChangeApplied = true;
                info.neverChangeColorSettings = false;
                info.isColorSettingApplied = true;
                _vibranceInfoField.SetValue(null, info);
                PutGameModeOnTheFakeDisplay();
            }

            public override bool IsResolutionApplied { get { return ((VibranceInfo)_vibranceInfoField.GetValue(null)).isResolutionChangeApplied; } }
            public override bool IsColorApplied { get { return ((VibranceInfo)_vibranceInfoField.GetValue(null)).isColorSettingApplied; } }
            public override int Level { get { return Device.LevelFor(GameDevice); } }
            public override int Writes { get { return Device.SetLevelCalls; } }

            public override void Dispose()
            {
                NvidiaDynamicVibranceProxy.ResetForTests(null, new VibranceInfo(), new List<ApplicationSetting>());
            }
        }

        private class AmdScenario : Scenario
        {
            public readonly FakeAmdAdapter Adapter = new FakeAmdAdapter();
            public AmdDynamicVibranceProxy Proxy;
            private readonly MethodInfo _onWinEventHook = typeof(AmdDynamicVibranceProxy).GetMethod("OnWinEventHook", BindingFlags.NonPublic | BindingFlags.Instance);
            private readonly FieldInfo _vibranceInfoField = typeof(AmdDynamicVibranceProxy).GetField("_vibranceInfo", BindingFlags.NonPublic | BindingFlags.Instance);

            public AmdScenario(string gameName) : base(gameName)
            {
                Hwnd = GetDesktopWindow();
                GameDevice = Screen.FromHandle(Hwnd).DeviceName;
                Proxy = new AmdDynamicVibranceProxy(Adapter, new List<ApplicationSetting> { BuildApplicationSetting(250) }, BuildWindowsSettings());
                Proxy.SetAffectPrimaryMonitorOnly(true);
                Proxy.SetVibranceWindowsLevel(WindowsLevel);
                Proxy.SetNeverChangeColorSettings(true);
                Proxy.SetNeverSwitchResolution(true);
                Proxy.SetGameExitWatcherForTests(null, Modes);
                // The revert branch bails unless the event's window is in front; answer that with the
                // event's own (fake) window rather than whatever is really in front on this machine.
                Proxy.SetForegroundWindowForTests(delegate { return Hwnd; });
            }

            public override void SetNeverChangeResolution(bool value)
            {
                Proxy.SetNeverSwitchResolution(value);
            }

            public override void Raise(string processName)
            {
                _onWinEventHook.Invoke(Proxy, new object[] { null, new WinEventHookEventArgs { Handle = Hwnd, ProcessName = processName, ProcessImagePath = null, CursorPosition = Cursor } });
            }

            public override void SeedApplied()
            {
                VibranceInfo info = Proxy.GetVibranceInfo();
                info.neverChangeResolution = false;
                info.isResolutionChangeApplied = true;
                info.neverChangeColorSettings = false;
                info.isColorSettingApplied = true;
                _vibranceInfoField.SetValue(Proxy, info);
                PutGameModeOnTheFakeDisplay();
            }

            public override bool IsResolutionApplied { get { return Proxy.GetVibranceInfo().isResolutionChangeApplied; } }
            public override bool IsColorApplied { get { return Proxy.GetVibranceInfo().isColorSettingApplied; } }

            public override int Level
            {
                get
                {
                    for (int i = Adapter.Names.Count - 1; i >= 0; i--)
                    {
                        if (Adapter.Names[i] == GameDevice)
                            return Adapter.Levels[i];
                    }
                    return int.MinValue;
                }
            }

            public override int Writes { get { return Adapter.Names.Count; } }

            public override void Dispose() { }
        }

        // ------------------------------------------------------------------
        // The checks, written once and run for each vendor.
        // ------------------------------------------------------------------

        private static void RunNvidiaChecks(Checklist checklist)
        {
            checklist.Lines.Add("NVIDIA (real OnWinEventHook via reflection, fake display, fake device, fake timer):");
            RunVendorChecks(checklist, delegate (string name) { return new NvidiaScenario(name); }, "N", IngameLevel);
            NvidiaDynamicVibranceProxy.ResetForTests(null, new VibranceInfo(), new List<ApplicationSetting>());
            ResolutionHelper.DeviceOverrideForTests = null;
            checklist.Lines.Add(string.Empty);
        }

        private static void RunAmdChecks(Checklist checklist)
        {
            checklist.Lines.Add("AMD (real OnWinEventHook via reflection, fake display, fake adapter, fake timer):");
            try
            {
                RunVendorChecks(checklist, delegate (string name) { return new AmdScenario(name); }, "A", 250);
            }
            finally
            {
                ForegroundSettler.Install(null);
                DesktopScreenResolver.IsShellDesktopOverride = null;
                ResolutionHelper.DeviceOverrideForTests = null;
            }
            checklist.Lines.Add(string.Empty);
        }

        private static void RunVendorChecks(Checklist checklist, Func<string, Scenario> create, string p, int ingame)
        {
            string game = "TestForegroundSettle" + p;

            // A scenario with a game applied (no timer involvement - nothing is on record yet) and
            // the resolution/gamma flags seeded, then the deferring settler installed.
            Func<Scenario> armedGame = delegate
            {
                Scenario s = create(game);
                s.UseTimer();
                s.Game();
                s.SeedApplied();
                return s;
            };

            Scenario scenario = armedGame();

            // Baseline sanity: the apply happened and was synchronous (Timer saw no deferral).
            checklist.Check(scenario.Level == ingame && !scenario.Timer.Armed && scenario.Timer.RestartCalls == 0,
                string.Format("{0}0: a first game event applies at once (no mode change on record), level {1}", p, scenario.Level));

            // F1
            int writes = scenario.Writes;
            int modeCalls = scenario.Modes.CallLog.Count;
            scenario.NonGame();
            checklist.Check(scenario.Timer.Armed && scenario.Timer.LastDelay == 400 && scenario.Timer.RestartCalls == 1,
                string.Format("{0}-F1: a non-game event arms the timer with 400, got {1} (restarts {2})", p, scenario.Timer.LastDelay, scenario.Timer.RestartCalls));
            checklist.Check(scenario.Writes == writes && scenario.Level == ingame && scenario.Modes.CallLog.Count == modeCalls && scenario.IsResolutionApplied && scenario.IsColorApplied,
                string.Format("{0}-F1: no vibrance write, no display call, resolution and gamma still applied before the timer fires", p));

            // F3
            scenario.Reader.Name = NonGameName;
            scenario.Timer.Fire();
            checklist.Check(scenario.Level == WindowsLevel && scenario.Writes > writes,
                string.Format("{0}-F3: the fire restores the Windows vibrance level, got {1}", p, scenario.Level));
            checklist.Check(scenario.DesktopRestored && !scenario.IsResolutionApplied && scenario.ModeWrites == 1,
                string.Format("{0}-F3: the fire reverts the resolution exactly once (mode writes {1})", p, scenario.ModeWrites));
            checklist.Check(!scenario.IsColorApplied, p + "-F3: the fire runs the gamma restore (isColorSettingApplied cleared)");
            checklist.Check(!scenario.Timer.Armed && scenario.Timer.RestartCalls == 1, p + "-F3: the re-dispatched event is settled - it does not arm the timer again");
            int afterWrites = scenario.Writes;
            int afterModeWrites = scenario.ModeWrites;
            scenario.Timer.Fire();
            checklist.Check(scenario.Writes == afterWrites && scenario.ModeWrites == afterModeWrites,
                p + "-F3: a second fire has nothing pending and changes nothing (revert is once only)");

            // End to end loop breaker: the revert is a mode change, so the game event it provokes
            // is deferred (F6), and a non-game event inside the hold waits it out (F7).
            writes = scenario.Writes;
            scenario.Clock[0] += 300;
            scenario.Game();
            checklist.Check(scenario.Timer.Armed && scenario.Timer.LastDelay == 1200 && scenario.Level == WindowsLevel && scenario.Writes == writes,
                string.Format("{0}-F6: a game event 300ms after our own revert is deferred by the remaining hold (1200) and writes nothing, delay {1}", p, scenario.Timer.LastDelay));
            scenario.Reader.Name = game;
            scenario.Timer.Fire();
            checklist.Check(scenario.Level == ingame, string.Format("{0}-F6: when the deferred apply fires with the game in front it applies, level {1}", p, scenario.Level));
            scenario.Dispose();

            // F7 end to end.
            scenario = armedGame();
            scenario.NonGame();
            scenario.Timer.Fire();
            scenario.Clock[0] += 200;
            scenario.NonGame();
            checklist.Check(scenario.Timer.LastDelay == 1300 && scenario.Timer.LastDelay > ForegroundSettler.SettleDelayMs,
                string.Format("{0}-F7: a non-game event 200ms after our own revert waits the remaining hold (1300 > 400), got {1}", p, scenario.Timer.LastDelay));
            scenario.Dispose();

            // F6, the other half: after the hold an apply is immediate.
            scenario = armedGame();
            scenario.NonGame();
            scenario.Timer.Fire();
            scenario.Clock[0] += ForegroundSettler.ModeChangeHoldMs;
            int restarts = scenario.Timer.RestartCalls;
            scenario.Game();
            checklist.Check(scenario.Level == ingame && scenario.Timer.RestartCalls == restarts && !scenario.Timer.Armed,
                p + "-F6: 1500ms after our own revert a game event applies immediately");
            scenario.Dispose();

            // F2: Win key / Alt+Tab bounce.
            scenario = armedGame();
            writes = scenario.Writes;
            modeCalls = scenario.Modes.CallLog.Count;
            scenario.NonGame();
            checklist.Check(scenario.Timer.Armed, p + "-F2: (setup) the transient event armed a revert");
            scenario.Game();
            checklist.Check(!scenario.Timer.Armed, p + "-F2: a game event before the fire cancels the pending revert");
            scenario.Timer.Fire();
            checklist.Check(scenario.Level == ingame && scenario.Modes.CallLog.Count == modeCalls && scenario.IsResolutionApplied && scenario.IsColorApplied,
                p + "-F2: a late fire after the bounce changes neither vibrance, resolution nor gamma");
            scenario.Dispose();

            // F4: focus bounced back to the game by the time the timer fires.
            scenario = armedGame();
            modeCalls = scenario.Modes.CallLog.Count;
            scenario.NonGame();
            scenario.Reader.Name = game;
            scenario.Timer.Fire();
            checklist.Check(scenario.Level == ingame && scenario.IsResolutionApplied && scenario.IsColorApplied && scenario.Modes.CallLog.Count == modeCalls,
                p + "-F4: a fire with the game back in front runs the apply branch - no revert, no display call");
            int restartsF4 = scenario.Timer.RestartCalls;
            scenario.Game();
            checklist.Check(!scenario.Timer.Armed && scenario.Timer.RestartCalls == restartsF4,
                p + "-F4: the bounce noted no mode change (the next game event is not held)");
            scenario.Dispose();

            // F5: the foreground cannot be read at the fire.
            scenario = armedGame();
            writes = scenario.Writes;
            modeCalls = scenario.Modes.CallLog.Count;
            scenario.NonGame();
            scenario.Reader.Ok = false;
            scenario.Timer.Fire();
            checklist.Check(scenario.Level == ingame && scenario.Writes == writes && scenario.Modes.CallLog.Count == modeCalls && scenario.IsResolutionApplied && scenario.IsColorApplied,
                p + "-F5: a fire with TryGetForeground false does nothing at all");

            // The retry: nothing else re-arms after that fire, so it re-arms itself once.
            checklist.Check(scenario.Timer.Armed && scenario.Timer.LastDelay == ForegroundSettler.SettleDelayMs && scenario.Timer.RestartCalls == 2,
                string.Format("{0}-F5: the failed read re-arms once at {1}, got armed={2} delay={3} restarts={4}", p, ForegroundSettler.SettleDelayMs, scenario.Timer.Armed, scenario.Timer.LastDelay, scenario.Timer.RestartCalls));
            scenario.Reader.Ok = true;
            scenario.Timer.Fire();
            checklist.Check(scenario.DesktopRestored && !scenario.IsResolutionApplied && scenario.ModeWrites == 1 && scenario.Level == WindowsLevel,
                p + "-F5: when the retry's read succeeds the revert that would have been dropped runs");
            scenario.Dispose();

            // F5, both reads fail: one retry only, then nothing.
            scenario = armedGame();
            modeCalls = scenario.Modes.CallLog.Count;
            scenario.NonGame();
            scenario.Reader.Ok = false;
            scenario.Timer.Fire();
            scenario.Timer.Fire();
            checklist.Check(!scenario.Timer.Armed && scenario.Timer.RestartCalls == 2 && scenario.Level == ingame && scenario.Modes.CallLog.Count == modeCalls && scenario.IsResolutionApplied,
                string.Format("{0}-F5: when the retry's read fails too nothing more is armed or done (restarts {1})", p, scenario.Timer.RestartCalls));
            scenario.Dispose();

            // Last event wins: two transient events, one revert.
            scenario = armedGame();
            scenario.NonGame();
            scenario.NonGame();
            checklist.Check(scenario.Timer.RestartCalls == 2, p + "-last-wins: each non-game event restarts the one timer");
            scenario.Timer.Fire();
            checklist.Check(scenario.DesktopRestored && scenario.ModeWrites == 1, p + "-last-wins: two transient events produce exactly one revert");
            scenario.Dispose();

            // F12: a stream of non-game events 300ms apart (each under the 400 settle delay) must not
            // postpone the revert past the maximum wait, measured from the first of them.
            scenario = armedGame();
            int start = scenario.Clock[0];
            bool bounded = true;
            int lastDelay = 0;
            for (int i = 0; i <= ForegroundSettler.MaxSettleWaitMs / 300; i++)
            {
                scenario.NonGame();
                int elapsed = scenario.Clock[0] - start;
                lastDelay = scenario.Timer.LastDelay;
                if (elapsed < ForegroundSettler.MaxSettleWaitMs && elapsed + lastDelay > ForegroundSettler.MaxSettleWaitMs)
                    bounded = false;
                scenario.Clock[0] += 300;
            }
            checklist.Check(bounded && lastDelay == 1,
                string.Format("{0}-F12: ten events 300ms apart never schedule the fire past first arm + {1}; the one at {1} fires at the 1ms floor, last delay {2}", p, ForegroundSettler.MaxSettleWaitMs, lastDelay));
            scenario.Timer.Fire();
            checklist.Check(scenario.DesktopRestored && !scenario.IsResolutionApplied,
                p + "-F12: the revert that the stream was postponing runs");
            scenario.Dispose();

            // F10 end to end: shutdown cancel.
            scenario = armedGame();
            modeCalls = scenario.Modes.CallLog.Count;
            scenario.NonGame();
            ForegroundSettler.Shared.Cancel();
            scenario.Timer.Fire();
            checklist.Check(!scenario.Timer.Armed && scenario.Modes.CallLog.Count == modeCalls && scenario.IsResolutionApplied && scenario.Level == ingame,
                p + "-F10: Cancel() after a deferral means the revert never runs");
            scenario.Dispose();

            // F11: the APPLY is a mode change too. Every scenario above sets neverChangeResolution, so
            // the apply-side NoteModeChanged() was never reached; here the apply really changes the
            // mode (through the fake display, via ResolutionHelper.DeviceOverrideForTests) and the next
            // non-game event must wait out the hold instead of the plain settle delay.
            scenario = create(game);
            try
            {
                scenario.UseTimer();
                scenario.SetNeverChangeResolution(false);
                scenario.Modes.SetCurrentMode(scenario.GameDevice, ResolutionChangeFixture.BuildDevmode(1920, 1080, 32, 60, 0));
                ResolutionHelper.DeviceOverrideForTests = scenario.Modes;
                int before = scenario.ModeWrites;
                scenario.Game();
                checklist.Check(scenario.ModeWrites == before + 1 && scenario.IsResolutionApplied && scenario.GameModeLanded && !scenario.Timer.Armed,
                    string.Format("{0}-F11: (setup) the apply changed the mode through the fake display, mode writes {1}", p, scenario.ModeWrites - before));
                scenario.Clock[0] += 300;
                scenario.NonGame();
                checklist.Check(scenario.Timer.Armed && scenario.Timer.LastDelay == 1200,
                    string.Format("{0}-F11: a non-game event 300ms after our own APPLY waits the remaining hold (1200), got {1}", p, scenario.Timer.LastDelay));
            }
            finally
            {
                ResolutionHelper.DeviceOverrideForTests = null;
            }
            scenario.Dispose();

            // F8 end to end: no timer installed -> the revert is immediate, on the raw event.
            scenario = create(game);
            scenario.UseNoTimer();
            scenario.Reader.Hwnd = scenario.Hwnd;
            scenario.Game();
            scenario.SeedApplied();
            scenario.NonGame();
            checklist.Check(scenario.Level == WindowsLevel && scenario.DesktopRestored && !scenario.IsResolutionApplied && !scenario.IsColorApplied && !scenario.Timer.Armed,
                p + "-F8: with no timer installed a non-game event reverts vibrance, resolution and gamma synchronously");
            scenario.Dispose();

            // F9 end to end: the shell desktop on a secondary screen.
            Screen[] screens = Screen.AllScreens;
            if (screens.Length < 2)
            {
                checklist.Skip(p + "-F9: only one screen on this machine - a desktop click on a secondary screen cannot be reproduced");
                return;
            }

            scenario = armedGame();
            Screen gameScreen = Screen.FromHandle(scenario.Hwnd);
            Screen secondary = null;
            foreach (Screen s in screens)
            {
                if (!s.Equals(gameScreen)) { secondary = s; break; }
            }
            IntPtr desktopHwnd = scenario.Hwnd;
            DesktopScreenResolver.IsShellDesktopOverride = delegate (IntPtr h) { return h == desktopHwnd; };
            try
            {
                // Cursor on the secondary at the click.
                scenario.Cursor = Center(secondary);
                scenario.NonGame();
                modeCalls = scenario.Modes.CallLog.Count;
                scenario.Timer.Fire();
                checklist.Check(scenario.IsResolutionApplied && scenario.Modes.CallLog.Count == modeCalls && !scenario.DesktopRestored,
                    p + "-F9: the shell desktop clicked on a secondary screen does not revert the game screen's resolution");
                scenario.Dispose();

                // The mouse moves on to the game's screen after the click, before the timer fires:
                // the cursor captured at the click still decides.
                scenario = armedGame();
                // Scenario's constructor clears the seam, so it is set again for each scenario.
                DesktopScreenResolver.IsShellDesktopOverride = delegate (IntPtr h) { return h == desktopHwnd; };
                scenario.Cursor = Center(secondary);
                scenario.NonGame();
                modeCalls = scenario.Modes.CallLog.Count;
                scenario.Cursor = Center(gameScreen);
                scenario.Timer.Fire();
                checklist.Check(scenario.IsResolutionApplied && scenario.Modes.CallLog.Count == modeCalls,
                    p + "-F9: the cursor is taken when the event arrives, not when the timer fires");
                scenario.Dispose();

                // Control: the same desktop with the cursor ON the game's screen does revert, so the
                // checks above are not passing merely because nothing ever reverts.
                scenario = armedGame();
                DesktopScreenResolver.IsShellDesktopOverride = delegate (IntPtr h) { return h == desktopHwnd; };
                scenario.Cursor = Center(gameScreen);
                scenario.NonGame();
                scenario.Timer.Fire();
                checklist.Check(!scenario.IsResolutionApplied && scenario.DesktopRestored,
                    p + "-F9: control - the shell desktop with the cursor on the game's own screen does revert");
                scenario.Dispose();
            }
            finally
            {
                DesktopScreenResolver.IsShellDesktopOverride = null;
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

            public void Skip(string description)
            {
                Lines.Add(string.Format("[SKIP] {0}", description));
            }
        }
    }
}
