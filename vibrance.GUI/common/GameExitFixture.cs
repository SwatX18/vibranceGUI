using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using vibrance.GUI.AMD;
using vibrance.GUI.AMD.vendor;
using vibrance.GUI.NVIDIA;

namespace vibrance.GUI.common
{
    /// <summary>
    /// Regression coverage for restore-on-game-exit (the resolution used to revert only once the
    /// user clicked after the game closed, because reverting was driven purely by foreground
    /// events) and for the "resolution changed" notice engine. Everything runs through fakes: the
    /// display through ResolutionChangeFixture.FakeDisplayModeDevice, vibrance through a fake NVIDIA
    /// device / AMD adapter, the exit watcher through FakeGameExitWatcher - the apply events keep
    /// resolution change OFF (so no real mode set ever happens) and the "resolution applied" state
    /// is seeded directly afterwards. There must never be a hardware variant. Run by
    /// vibrance.GUI.exe --selftest-gameexit.
    /// </summary>
    public static class GameExitFixture
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        private const int WindowsLevel = 44;
        private const int IngameLevel = 60;

        public static List<string> Run()
        {
            Checklist checklist = new Checklist();
            checklist.Lines.Add("vibranceGUI game exit watcher self test");
            checklist.Lines.Add(string.Empty);

            try
            {
                RunNotifierChecks(checklist);
                RunRealWatcherChecks(checklist);
                RunRealProcessExitChecks(checklist);
                RunNvidiaChecks(checklist);
                RunAmdChecks(checklist);
            }
            finally
            {
                ResolutionChangeNotifier.ResetForTests();
                ResolutionRestoreHelper.ResetForTests();
                ResolutionRestoreStore.ResetForTests(null);
                ResolutionHelper.ResetForTests();
                VibranceRestoreHelper.ResetForTests();
            }

            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add(string.Format("PASSED {0}/{1}", checklist.Passed, checklist.Total));
            return checklist.Lines;
        }

        // ------------------------------------------------------------------
        // Notifier engine (pure).
        // ------------------------------------------------------------------

        private static void RunNotifierChecks(Checklist checklist)
        {
            checklist.Lines.Add("ResolutionChangeNotifier:");

            CheckFormatText(checklist);
            CheckRaiseRules(checklist);
            CheckUnverifiedFlag(checklist);
            CheckSettingDefaultsAndRoundTrips(checklist);

            checklist.Lines.Add(string.Empty);
        }

        private static string Format(string game, ResolutionModeWrapper mode, bool verified)
        {
            return ResolutionChangeNotifier.FormatText(new ResolutionAppliedEventArgs(game, "\\\\.\\DISPLAY1", mode, verified));
        }

        private static void CheckFormatText(Checklist checklist)
        {
            checklist.Check(Format("cs2", ResolutionChangeFixture.BuildTarget(1280, 960, 32, 144, (uint)Dmdfo.Center), true) == "cs2: 1280 x 960 @ 144 Hz, Center",
                "FormatText: Center reads \"cs2: 1280 x 960 @ 144 Hz, Center\"");
            checklist.Check(Format("cs2", ResolutionChangeFixture.BuildTarget(1280, 960, 32, 144, (uint)Dmdfo.Stretch), true) == "cs2: 1280 x 960 @ 144 Hz, Stretch",
                "FormatText: Stretch reads \"cs2: 1280 x 960 @ 144 Hz, Stretch\"");
            checklist.Check(Format("cs2", ResolutionChangeFixture.BuildTarget(1280, 960, 32, 144, (uint)Dmdfo.Default), true) == "cs2: 1280 x 960 @ 144 Hz, Default (driver)",
                "FormatText: Default reads \"cs2: 1280 x 960 @ 144 Hz, Default (driver)\"");
            checklist.Check(Format("cs2", ResolutionChangeFixture.BuildTarget(1280, 960, 32, 144, (uint)Dmdfo.Center), false) == "cs2: 1280 x 960 @ 144 Hz, Center (unconfirmed)",
                "FormatText: an unverified apply appends \" (unconfirmed)\"");
            checklist.Check(Format("cs2", ResolutionChangeFixture.BuildTarget(1280, 960, 32, 144, (uint)Dmdfo.Default), false) == "cs2: 1280 x 960 @ 144 Hz, Default (driver) (unconfirmed)",
                "FormatText: Default plus unverified reads \"..., Default (driver) (unconfirmed)\"");
        }

        private static void CheckRaiseRules(Checklist checklist)
        {
            ResolutionChangeNotifier.ResetForTests();
            List<ResolutionAppliedEventArgs> raised = new List<ResolutionAppliedEventArgs>();
            ResolutionChangeNotifier.ResolutionApplied += delegate (object sender, ResolutionAppliedEventArgs e) { raised.Add(e); };
            ResolutionModeWrapper mode = ResolutionChangeFixture.BuildTarget(1280, 960, 32, 144, (uint)Dmdfo.Center);
            const string device = "FAKE-NOTIFIER-DEVICE";

            ResolutionChangeNotifier.OnApplied("cs2", device, mode, ResolutionHelper.ResolutionChangeResult.Failed);
            ResolutionChangeNotifier.OnApplied("cs2", device, mode, ResolutionHelper.ResolutionChangeResult.Suppressed);
            ResolutionChangeNotifier.OnApplied("cs2", device, mode, ResolutionHelper.ResolutionChangeResult.AlreadyMatching);
            checklist.Check(raised.Count == 0, string.Format("OnApplied: Failed, Suppressed and AlreadyMatching never raise, got {0}", raised.Count));

            ResolutionChangeNotifier.OnApplied("cs2", device, mode, ResolutionHelper.ResolutionChangeResult.Applied);
            checklist.Check(raised.Count == 1 && raised[0].GameName == "cs2" && raised[0].DeviceName == device && raised[0].Mode == mode,
                "OnApplied: Applied raises once, carrying the game, device and mode");

            ResolutionChangeNotifier.OnApplied("cs2", device, mode, ResolutionHelper.ResolutionChangeResult.Applied);
            ResolutionChangeNotifier.OnApplied("cs2", device, mode, ResolutionHelper.ResolutionChangeResult.AppliedUnverified);
            checklist.Check(raised.Count == 1, string.Format("OnApplied: a second apply of the same game in the same session does not raise again, got {0}", raised.Count));

            ResolutionChangeNotifier.OnApplied("cs2", device, mode, ResolutionHelper.ResolutionChangeResult.Failed);
            ResolutionChangeNotifier.OnGameSessionEnded("cs2");
            ResolutionChangeNotifier.OnApplied("cs2", device, mode, ResolutionHelper.ResolutionChangeResult.Applied);
            checklist.Check(raised.Count == 2, string.Format("OnApplied: once the session has ended the same game raises again, got {0}", raised.Count));

            ResolutionChangeNotifier.OnApplied("dota2", device, mode, ResolutionHelper.ResolutionChangeResult.Applied);
            checklist.Check(raised.Count == 3 && raised[2].GameName == "dota2", string.Format("OnApplied: a different game raises without waiting for a session end, got {0}", raised.Count));

            ResolutionChangeNotifier.OnGameSessionEnded("cs2");
            ResolutionChangeNotifier.OnApplied("dota2", device, mode, ResolutionHelper.ResolutionChangeResult.Applied);
            checklist.Check(raised.Count == 3, string.Format("OnGameSessionEnded: the end of a game that is not the announced one does not re-arm the announced one, got {0}", raised.Count));

            ResolutionChangeNotifier.ResolutionApplied += delegate { throw new InvalidOperationException("subscriber failure"); };
            bool threw = false;
            try
            {
                ResolutionChangeNotifier.OnGameSessionEnded("dota2");
                ResolutionChangeNotifier.OnApplied("dota2", device, mode, ResolutionHelper.ResolutionChangeResult.Applied);
            }
            catch (Exception)
            {
                threw = true;
            }
            checklist.Check(!threw, "OnApplied: a throwing subscriber never propagates into the proxy that called it");

            ResolutionChangeNotifier.ResetForTests();
        }

        private static void CheckUnverifiedFlag(Checklist checklist)
        {
            ResolutionChangeNotifier.ResetForTests();
            List<ResolutionAppliedEventArgs> raised = new List<ResolutionAppliedEventArgs>();
            ResolutionChangeNotifier.ResolutionApplied += delegate (object sender, ResolutionAppliedEventArgs e) { raised.Add(e); };
            ResolutionModeWrapper mode = ResolutionChangeFixture.BuildTarget(1280, 960, 32, 144, (uint)Dmdfo.Default);

            ResolutionChangeNotifier.OnApplied("cs2", "FAKE-NOTIFIER-DEVICE", mode, ResolutionHelper.ResolutionChangeResult.AppliedUnverified);
            checklist.Check(raised.Count == 1 && !raised[0].Verified, "OnApplied: AppliedUnverified raises with Verified == false");
            ResolutionChangeNotifier.OnGameSessionEnded("cs2");
            ResolutionChangeNotifier.OnApplied("cs2", "FAKE-NOTIFIER-DEVICE", mode, ResolutionHelper.ResolutionChangeResult.Applied);
            checklist.Check(raised.Count == 2 && raised[1].Verified, "OnApplied: Applied raises with Verified == true");

            ResolutionChangeNotifier.ResetForTests();
        }

        private static void CheckSettingDefaultsAndRoundTrips(Checklist checklist)
        {
            string tempIni = Path.Combine(Path.GetTempPath(), "vibranceGUI-gameexit-selftest-" + Guid.NewGuid().ToString("N") + ".ini");
            string tempXml = Path.Combine(Path.GetTempPath(), "vibranceGUI-gameexit-selftest-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                SettingsController controller = new SettingsController(tempIni, tempXml);
                checklist.Check(controller.ReadResolutionChangeNotificationEnabled(),
                    "resolutionChangeNotification defaults to true when the INI does not exist");

                controller.SetVibranceSetting("someOtherKey", "someOtherValue");
                checklist.Check(controller.ReadResolutionChangeNotificationEnabled(),
                    "resolutionChangeNotification defaults to true when the INI exists but the key does not");

                // Judged on the read-back only, not on Set...'s bool: SetVibranceSetting derives that
                // from Marshal.GetLastWin32Error() after a P/Invoke that never sets SetLastError, so
                // it can report a stale error left by an earlier missing-key read even though the
                // write landed (an existing SettingsController quirk, out of scope here).
                controller.SetResolutionChangeNotificationEnabled(false);
                bool readOff = controller.ReadResolutionChangeNotificationEnabled();
                checklist.Check(!readOff,
                    string.Format("resolutionChangeNotification false round-trips through the INI, read back {0}", readOff));

                controller.SetResolutionChangeNotificationEnabled(true);
                bool readOn = controller.ReadResolutionChangeNotificationEnabled();
                checklist.Check(readOn,
                    string.Format("resolutionChangeNotification true round-trips through the INI, read back {0}", readOn));
            }
            finally
            {
                DeleteFileIfExists(tempIni);
                DeleteFileIfExists(tempXml);
            }
        }

        private static void DeleteFileIfExists(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception)
            {
                // Best-effort temp file cleanup only - never let this mask a check's own result.
            }
        }

        // ------------------------------------------------------------------
        // RealGameExitWatcher - only what can be proven without a second process's window.
        // ------------------------------------------------------------------

        private static void RunRealWatcherChecks(Checklist checklist)
        {
            checklist.Lines.Add("RealGameExitWatcher:");

            int posted = 0;
            RealGameExitWatcher watcher = new RealGameExitWatcher(delegate (Action a) { posted++; a(); });
            int raised = 0;
            watcher.GameExited += delegate { raised++; };

            bool threw = false;
            int zeroToken = -1;
            try
            {
                zeroToken = watcher.Watch(IntPtr.Zero);
            }
            catch (Exception)
            {
                threw = true;
            }
            checklist.Check(!threw && zeroToken == 0, string.Format("Watch(IntPtr.Zero) returns 0 and never throws, got {0}", zeroToken));

            NativeWindow window = new NativeWindow();
            try
            {
                window.CreateHandle(new CreateParams());
                // A window of THIS process: it is alive for the whole run, so it can be armed and
                // re-armed but never exits.
                int first = watcher.Watch(window.Handle);
                int again = watcher.Watch(window.Handle);
                checklist.Check(first > 0, string.Format("Watch on a live process's window returns a token greater than 0, got {0}", first));
                checklist.Check(again == first, string.Format("Watching the same process again returns the existing token without re-arming, got {0} then {1}", first, again));

                watcher.Cancel();
                int afterCancel = watcher.Watch(window.Handle);
                checklist.Check(afterCancel > first, string.Format("Cancel disposes the watch, so the next Watch arms a fresh, higher token, got {0} then {1}", first, afterCancel));
                watcher.Cancel();
            }
            finally
            {
                window.DestroyHandle();
            }

            int protectedToken = -1;
            threw = false;
            try
            {
                // The desktop window belongs to a process this one usually cannot open for
                // notification - either outcome is fine, an exception is not.
                protectedToken = watcher.Watch(GetDesktopWindow());
                watcher.Cancel();
            }
            catch (Exception)
            {
                threw = true;
            }
            checklist.Check(!threw && protectedToken >= 0, "Watch on a window whose process may not be openable never throws");
            checklist.Check(posted == 0 && raised == 0, string.Format("nothing exited, so nothing was posted or raised (posted={0}, raised={1})", posted, raised));

            checklist.Lines.Add(string.Empty);
        }

        // ------------------------------------------------------------------
        // RealGameExitWatcher against REAL child processes that own a window: proves the actual
        // Process.Exited -> postToUiThread -> GameExited path. The children are throwaway
        // PowerShell WinForms windows (notepad.exe on Windows 11 is a launcher stub whose window
        // belongs to a different process, so it cannot be used). Nothing here touches the display.
        // ------------------------------------------------------------------

        private const int ChildWindowTimeoutMs = 20000;
        private const int QuietPeriodMs = 1500;
        private const string ChildTitle = "vibrance-exit-probe";

        private static Process StartWindowedChild()
        {
            string shell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            ProcessStartInfo info = new ProcessStartInfo(shell,
                "-NoProfile -NonInteractive -Command \"Add-Type -AssemblyName System.Windows.Forms; " +
                "$f = New-Object System.Windows.Forms.Form; $f.Text = '" + ChildTitle + "'; " +
                "[System.Windows.Forms.Application]::Run($f)\"");
            info.UseShellExecute = false;
            info.CreateNoWindow = false;
            Process child = Process.Start(info);
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < ChildWindowTimeoutMs)
            {
                child.Refresh();
                if (child.HasExited)
                    break;
                if (child.MainWindowHandle != IntPtr.Zero && child.MainWindowTitle == ChildTitle)
                    return child;
                System.Threading.Thread.Sleep(50);
            }
            KillQuietly(child);
            return null;
        }

        private static void KillQuietly(Process child)
        {
            if (child == null)
                return;
            try
            {
                if (!child.HasExited)
                {
                    child.Kill();
                    child.WaitForExit(5000);
                }
            }
            catch (Exception)
            {
                // Already gone.
            }
            try { child.Dispose(); }
            catch (Exception) { }
        }

        private static void RunRealProcessExitChecks(Checklist checklist)
        {
            checklist.Lines.Add("RealGameExitWatcher with real child processes:");

            Process a = null;
            Process b = null;
            try
            {
                a = StartWindowedChild();
                if (a == null)
                {
                    checklist.Check(false, "could not start a windowed child process to watch (test environment problem)");
                    return;
                }

                // 1. Kill -> exactly one GameExited carrying the token Watch returned.
                object gate = new object();
                List<int> raised = new List<int>();
                int posted = 0;
                int postedOnThread = -1;
                System.Threading.ManualResetEvent got = new System.Threading.ManualResetEvent(false);
                RealGameExitWatcher watcher = new RealGameExitWatcher(delegate (Action act)
                {
                    lock (gate) { posted++; postedOnThread = System.Threading.Thread.CurrentThread.ManagedThreadId; }
                    act();
                });
                watcher.GameExited += delegate (int token)
                {
                    lock (gate) { raised.Add(token); }
                    got.Set();
                };

                int tokenA = watcher.Watch(a.MainWindowHandle);
                int tokenA2 = watcher.Watch(a.MainWindowHandle);
                checklist.Check(tokenA > 0 && tokenA2 == tokenA, string.Format("real window: Watch arms a token and re-watching returns it, got {0} then {1}", tokenA, tokenA2));
                System.Threading.Thread.Sleep(300);
                lock (gate)
                {
                    checklist.Check(posted == 0 && raised.Count == 0, "no exit is posted while the game process is alive");
                }

                int testThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                a.Kill();
                bool signalled = got.WaitOne(10000);
                System.Threading.Thread.Sleep(QuietPeriodMs);
                lock (gate)
                {
                    checklist.Check(signalled, "killing the watched process raises GameExited");
                    checklist.Check(raised.Count == 1 && raised[0] == tokenA, string.Format("GameExited fired exactly once with Watch's token {0}, got [{1}]", tokenA, string.Join(",", raised)));
                    checklist.Check(posted == 1, string.Format("the exit went through postToUiThread exactly once, posted={0}", posted));
                    checklist.Check(postedOnThread != testThread, "Process.Exited arrives off the calling thread, which is why it must be posted (thread-pool thread)");
                }
                KillQuietly(a);
                a = null;

                // 2. Cancel before the kill -> no event, ever.
                a = StartWindowedChild();
                if (a == null)
                {
                    checklist.Check(false, "could not start second windowed child (test environment problem)");
                    return;
                }
                int cancelPosted = 0;
                int cancelRaised = 0;
                RealGameExitWatcher cancelWatcher = new RealGameExitWatcher(delegate (Action act) { cancelPosted++; act(); });
                cancelWatcher.GameExited += delegate { cancelRaised++; };
                int cancelToken = cancelWatcher.Watch(a.MainWindowHandle);
                cancelWatcher.Cancel();
                a.Kill();
                a.WaitForExit(10000);
                System.Threading.Thread.Sleep(QuietPeriodMs);
                checklist.Check(cancelToken > 0 && cancelPosted == 0 && cancelRaised == 0,
                    string.Format("Cancel before the kill: no post and no GameExited (token={0}, posted={1}, raised={2})", cancelToken, cancelPosted, cancelRaised));
                KillQuietly(a);
                a = null;

                // 3. Replacing the watch: the replaced process exiting must be ignored, the new one reported.
                a = StartWindowedChild();
                b = StartWindowedChild();
                if (a == null || b == null)
                {
                    checklist.Check(false, "could not start two windowed children (test environment problem)");
                    return;
                }
                List<int> replaceRaised = new List<int>();
                object replaceGate = new object();
                System.Threading.ManualResetEvent replaceGot = new System.Threading.ManualResetEvent(false);
                RealGameExitWatcher replaceWatcher = new RealGameExitWatcher(delegate (Action act) { act(); });
                replaceWatcher.GameExited += delegate (int token) { lock (replaceGate) { replaceRaised.Add(token); } replaceGot.Set(); };
                int tokenFirst = replaceWatcher.Watch(a.MainWindowHandle);
                int tokenSecond = replaceWatcher.Watch(b.MainWindowHandle);
                checklist.Check(tokenFirst > 0 && tokenSecond > tokenFirst, string.Format("watching a different process re-arms with a higher token, got {0} then {1}", tokenFirst, tokenSecond));
                a.Kill();
                a.WaitForExit(10000);
                System.Threading.Thread.Sleep(QuietPeriodMs);
                lock (replaceGate)
                {
                    checklist.Check(replaceRaised.Count == 0, "the replaced (old) process exiting raises nothing");
                }
                b.Kill();
                bool secondSignalled = replaceGot.WaitOne(10000);
                System.Threading.Thread.Sleep(QuietPeriodMs);
                lock (replaceGate)
                {
                    checklist.Check(secondSignalled && replaceRaised.Count == 1 && replaceRaised[0] == tokenSecond,
                        string.Format("the current process exiting raises exactly once with the second token {0}, got [{1}]", tokenSecond, string.Join(",", replaceRaised)));
                }
                KillQuietly(a);
                KillQuietly(b);
                a = null;
                b = null;

                // 4. A post delegate that throws must not take the process down (this callback runs
                // on a thread-pool thread; an escaped exception would crash the whole run).
                a = StartWindowedChild();
                if (a == null)
                {
                    checklist.Check(false, "could not start windowed child for the throwing-post check (test environment problem)");
                    return;
                }
                System.Threading.ManualResetEvent throwerRan = new System.Threading.ManualResetEvent(false);
                RealGameExitWatcher throwingWatcher = new RealGameExitWatcher(delegate (Action act)
                {
                    throwerRan.Set();
                    throw new InvalidOperationException("post failed (form disposed)");
                });
                throwingWatcher.GameExited += delegate { };
                throwingWatcher.Watch(a.MainWindowHandle);
                a.Kill();
                bool ran = throwerRan.WaitOne(10000);
                System.Threading.Thread.Sleep(QuietPeriodMs);
                checklist.Check(ran, "a throwing postToUiThread was invoked and did not crash the process (still running to report this)");
                KillQuietly(a);
                a = null;

                // 5. The window is gone by the time Watch runs (process already exited): it must
                // not throw and must not invent an event. GetWindowThreadProcessId fails for a
                // destroyed window, so the "already exited" branch inside Watch is only reachable
                // in a sub-millisecond race and cannot be driven deterministically from here.
                a = StartWindowedChild();
                if (a == null)
                {
                    checklist.Check(false, "could not start windowed child for the exited-before-Watch check (test environment problem)");
                    return;
                }
                IntPtr deadHandle = a.MainWindowHandle;
                a.Kill();
                a.WaitForExit(10000);
                int deadPosted = 0;
                int deadRaised = 0;
                RealGameExitWatcher deadWatcher = new RealGameExitWatcher(delegate (Action act) { deadPosted++; act(); });
                deadWatcher.GameExited += delegate { deadRaised++; };
                int deadToken = -1;
                bool deadThrew = false;
                try { deadToken = deadWatcher.Watch(deadHandle); }
                catch (Exception) { deadThrew = true; }
                System.Threading.Thread.Sleep(300);
                checklist.Check(!deadThrew && deadToken == 0 && deadPosted == 0 && deadRaised == 0,
                    string.Format("Watch on the window of an already-exited process returns 0 without throwing (token={0}, threw={1}, posted={2}, raised={3})", deadToken, deadThrew, deadPosted, deadRaised));

                // 6. Cancel AFTER the exit was already posted to the UI thread but BEFORE the UI
                // thread ran it (the window CleanUp() can land in: the form is closing with an exit
                // callback still queued). A cancelled watch must not raise GameExited.
                a = StartWindowedChild();
                if (a == null)
                {
                    checklist.Check(false, "could not start windowed child for the cancel-after-post check (test environment problem)");
                    return;
                }
                Action queued = null;
                System.Threading.ManualResetEvent queuedSignal = new System.Threading.ManualResetEvent(false);
                RealGameExitWatcher lateWatcher = new RealGameExitWatcher(delegate (Action act) { queued = act; queuedSignal.Set(); });
                int lateRaised = 0;
                lateWatcher.GameExited += delegate { lateRaised++; };
                lateWatcher.Watch(a.MainWindowHandle);
                a.Kill();
                bool wasQueued = queuedSignal.WaitOne(10000);
                lateWatcher.Cancel();
                if (queued != null)
                    queued();
                checklist.Check(wasQueued && lateRaised == 0,
                    string.Format("Cancel after the exit was posted but before the UI thread ran it suppresses GameExited (queued={0}, raised={1})", wasQueued, lateRaised));
                KillQuietly(a);
                a = null;
            }
            finally
            {
                KillQuietly(a);
                KillQuietly(b);
            }

            checklist.Lines.Add(string.Empty);
        }

        // ------------------------------------------------------------------
        // Fakes.
        // ------------------------------------------------------------------

        private class FakeGameExitWatcher : IGameExitWatcher
        {
            public readonly List<IntPtr> WatchedHandles = new List<IntPtr>();
            public int CancelCount;
            public bool AlwaysNewToken;
            public bool ReturnZero;
            public int LastToken;
            private IntPtr _lastHandle;

            public event Action<int> GameExited;

            public int Watch(IntPtr hWnd)
            {
                WatchedHandles.Add(hWnd);
                if (ReturnZero)
                {
                    return 0;
                }
                // Mirrors RealGameExitWatcher: the same process again returns the existing token.
                if (!AlwaysNewToken && LastToken > 0 && _lastHandle == hWnd)
                {
                    return LastToken;
                }
                _lastHandle = hWnd;
                LastToken++;
                return LastToken;
            }

            public void Cancel()
            {
                CancelCount++;
            }

            public void Raise(int token)
            {
                Action<int> handler = GameExited;
                if (handler != null)
                {
                    handler(token);
                }
            }

            public bool HasSubscriber
            {
                get { return GameExited != null; }
            }
        }

        private class FakeNvidiaVibranceDevice : INvidiaVibranceDevice
        {
            private readonly Dictionary<string, IntPtr> _handlesByDeviceName = new Dictionary<string, IntPtr>();
            private readonly Dictionary<IntPtr, int> _levelsByHandle = new Dictionary<IntPtr, int>();
            private int _nextHandle = 1;

            public int IsWindowActiveCalls;

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

            public bool IsWindowActive(ref IntPtr hWnd)
            {
                IsWindowActiveCalls++;
                return true;
            }

            public IntPtr TryResolveDisplayHandle(string deviceName)
            {
                if (string.IsNullOrEmpty(deviceName))
                {
                    return NvidiaDynamicVibranceProxy.InvalidDisplayHandle;
                }
                return ResolveOrAssign(deviceName);
            }

            public bool IsAtLevel(IntPtr displayHandle, int level)
            {
                int current;
                return _levelsByHandle.TryGetValue(displayHandle, out current) && current == level;
            }

            public bool SetLevel(IntPtr displayHandle, int level)
            {
                _levelsByHandle[displayHandle] = level;
                return true;
            }
        }

        private class FakeAmdAdapter : IAmdAdapter
        {
            public readonly List<int> SetSaturationOnDisplayLevels = new List<int>();
            public readonly List<string> SetSaturationOnDisplayNames = new List<string>();

            public void SetSaturationOnAllDisplays(int vibranceLevel)
            {
            }

            public bool SetSaturationOnDisplay(int vibranceLevel, string displayName)
            {
                SetSaturationOnDisplayLevels.Add(vibranceLevel);
                SetSaturationOnDisplayNames.Add(displayName);
                return true;
            }

            // False so AmdDynamicVibranceProxy's constructor never calls Init() or installs a real
            // WinEventHook - the events below are pushed in by reflection instead.
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

        // ------------------------------------------------------------------
        // Scenario plumbing shared by both vendors.
        // ------------------------------------------------------------------

        private abstract class Scenario
        {
            public readonly IntPtr Desktop = GetDesktopWindow();
            public readonly string GameDevice;
            public readonly string GameName;
            public readonly FakeGameExitWatcher Watcher = new FakeGameExitWatcher();
            public readonly ResolutionChangeFixture.FakeDisplayModeDevice Modes = new ResolutionChangeFixture.FakeDisplayModeDevice();
            public readonly ResolutionModeWrapper DesktopTarget = ResolutionChangeFixture.BuildTarget(1920, 1080, 32, 60, (uint)Dmdfo.Default);
            public readonly ResolutionModeWrapper GameTarget = ResolutionChangeFixture.BuildTarget(2560, 1440, 32, 144, (uint)Dmdfo.Default);

            protected Scenario(string gameName)
            {
                GameName = gameName;
                GameDevice = Screen.FromHandle(Desktop).DeviceName;

                ResolutionHelper.ResetForTests();
                ResolutionChangeNotifier.ResetForTests();
                ResolutionRestoreHelper.ResetForTests();
                ResolutionRestoreStore.ResetForTests(null);
                VibranceRestoreHelper.ResetForTests();
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
                return setting;
            }

            protected WinEventHookEventArgs BuildEventArgs(string processName)
            {
                return new WinEventHookEventArgs { Handle = Desktop, ProcessName = processName, ProcessImagePath = null };
            }

            public void PutGameModeOnTheFakeDisplay()
            {
                Modes.SetCurrentMode(GameDevice, ResolutionChangeFixture.BuildDevmode(2560, 1440, 32, 144, 0));
                ResolutionRestoreHelper.RecordModeApplied(GameDevice, GameTarget, DesktopTarget);
            }

            public bool DesktopRestored
            {
                get { return DesktopTarget.MatchesAchievedMode(Modes.GetCurrentMode(GameDevice)); }
            }

            public int ModeCalls
            {
                get { return Modes.CallLog.Count; }
            }

            public abstract void ApplyEvent();
            // A foreground event for a non-game window on the game's screen; false when the
            // machine cannot provide one (AMD reads the real foreground window).
            public abstract bool ForegroundEvent();
            public abstract void SeedResolutionApplied();
            public abstract bool IsResolutionApplied { get; }
            public abstract int ObservedVibranceLevelForGameDevice { get; }
            public abstract int VibranceWriteCount { get; }
            public abstract ResolutionHelper.ResolutionChangeResult? Revert();
            public abstract void DetachWatcher();
        }

        // ------------------------------------------------------------------
        // NVIDIA.
        // ------------------------------------------------------------------

        private class NvidiaScenario : Scenario
        {
            public readonly FakeNvidiaVibranceDevice Device = new FakeNvidiaVibranceDevice();
            private readonly MethodInfo _onWinEventHook = typeof(NvidiaDynamicVibranceProxy).GetMethod("OnWinEventHook", BindingFlags.NonPublic | BindingFlags.Static);
            private readonly FieldInfo _vibranceInfoField = typeof(NvidiaDynamicVibranceProxy).GetField("_vibranceInfo", BindingFlags.NonPublic | BindingFlags.Static);
            private readonly FieldInfo _windowsSettingsField = typeof(NvidiaDynamicVibranceProxy).GetField("_windowsResolutionSettings", BindingFlags.NonPublic | BindingFlags.Static);
            private readonly MethodInfo _revert = typeof(NvidiaDynamicVibranceProxy).GetMethod("RevertGameResolution", BindingFlags.NonPublic | BindingFlags.Static);

            public NvidiaScenario(string gameName) : base(gameName)
            {
                VibranceInfo info = new VibranceInfo();
                info.affectPrimaryMonitorOnly = true;
                info.neverChangeResolution = true;
                info.neverChangeColorSettings = true;
                info.isWindowsLevelKnown = true;
                info.userVibranceSettingDefault = WindowsLevel;
                info.displayHandles = new List<IntPtr>();
                NvidiaDynamicVibranceProxy.ResetForTests(Device, info, new List<ApplicationSetting> { BuildApplicationSetting(IngameLevel) });
                NvidiaDynamicVibranceProxy.SetGameExitWatcherForTests(Watcher, Modes);
                _windowsSettingsField.SetValue(null, BuildWindowsSettings());
            }

            public override void ApplyEvent()
            {
                _onWinEventHook.Invoke(null, new object[] { null, BuildEventArgs(GameName) });
            }

            public override bool ForegroundEvent()
            {
                _onWinEventHook.Invoke(null, new object[] { null, BuildEventArgs("NotAGameProcess") });
                return true;
            }

            public override void SeedResolutionApplied()
            {
                VibranceInfo info = (VibranceInfo)_vibranceInfoField.GetValue(null);
                info.neverChangeResolution = false;
                info.isResolutionChangeApplied = true;
                _vibranceInfoField.SetValue(null, info);
                PutGameModeOnTheFakeDisplay();
            }

            public override bool IsResolutionApplied
            {
                get { return ((VibranceInfo)_vibranceInfoField.GetValue(null)).isResolutionChangeApplied; }
            }

            public override int ObservedVibranceLevelForGameDevice
            {
                get { return Device.LevelFor(GameDevice); }
            }

            public override int VibranceWriteCount
            {
                get { return 0; }
            }

            public override ResolutionHelper.ResolutionChangeResult? Revert()
            {
                return (ResolutionHelper.ResolutionChangeResult?)_revert.Invoke(null, new object[] { Modes, GameDevice });
            }

            public override void DetachWatcher()
            {
                NvidiaDynamicVibranceProxy.SetGameExitWatcherForTests(null, Modes);
            }
        }

        private static void RunNvidiaChecks(Checklist checklist)
        {
            checklist.Lines.Add("NVIDIA (real OnWinEventHook/OnGameExited via reflection, fake display, fake device, fake watcher):");

            NvidiaScenario scenario = new NvidiaScenario("TestGameExitNv");
            scenario.ApplyEvent();
            checklist.Check(scenario.Watcher.WatchedHandles.Count == 1 && scenario.Watcher.WatchedHandles[0] == scenario.Desktop,
                "N1: a matched apply event arms a watch for that event's hwnd");
            checklist.Check(scenario.Device.LevelFor(scenario.GameDevice) == IngameLevel,
                "N1: the apply wrote the ingame vibrance level to the game's screen");

            scenario.SeedResolutionApplied();
            int windowActiveBefore = scenario.Device.IsWindowActiveCalls;
            scenario.Watcher.Raise(scenario.Watcher.LastToken);
            checklist.Check(scenario.DesktopRestored, "N2: the game exiting reverts the game screen's resolution with no foreground event at all");
            checklist.Check(!scenario.IsResolutionApplied, "N2: the exit revert clears isResolutionChangeApplied");
            checklist.Check(ResolutionRestoreHelper.HoldingCount == 0, "N2: the exit revert clears the persisted restore record");
            checklist.Check(scenario.Device.LevelFor(scenario.GameDevice) == WindowsLevel,
                string.Format("N2: the exit restores the Windows vibrance level through the device, got {0}", scenario.Device.LevelFor(scenario.GameDevice)));
            checklist.Check(scenario.Device.IsWindowActiveCalls == windowActiveBefore,
                "N2: the exit path never consults the foreground/window-active gate");

            // Stale token: game A's watch (token 1) is superseded by game B's apply (token 2).
            scenario = new NvidiaScenario("TestGameExitNv");
            scenario.Watcher.AlwaysNewToken = true;
            scenario.ApplyEvent();
            int tokenA = scenario.Watcher.LastToken;
            scenario.ApplyEvent();
            int tokenB = scenario.Watcher.LastToken;
            scenario.SeedResolutionApplied();
            scenario.Watcher.Raise(tokenA);
            checklist.Check(tokenA != tokenB && !scenario.DesktopRestored && scenario.IsResolutionApplied && scenario.ModeCalls == 0,
                "N3: an exit carrying a stale token is a no-op - no revert, flag untouched, no driver call");
            scenario.Watcher.Raise(tokenB);
            checklist.Check(scenario.DesktopRestored && !scenario.IsResolutionApplied, "N3: the current token's exit does revert");

            // Exit after a normal foreground revert: the foreground branch calls exactly this method.
            scenario = new NvidiaScenario("TestGameExitNv");
            scenario.ApplyEvent();
            scenario.SeedResolutionApplied();
            ResolutionHelper.ResolutionChangeResult? foreground = scenario.Revert();
            int callsAfterForegroundRevert = scenario.ModeCalls;
            scenario.Watcher.Raise(scenario.Watcher.LastToken);
            checklist.Check(foreground == ResolutionHelper.ResolutionChangeResult.Applied && callsAfterForegroundRevert > 0 && scenario.ModeCalls == callsAfterForegroundRevert,
                string.Format("N4: an exit after the resolution was already reverted makes no further ChangeMode call, got {0} then {1}", callsAfterForegroundRevert, scenario.ModeCalls));

            // A failed revert on exit leaves the flag (and the record) set.
            scenario = new NvidiaScenario("TestGameExitNv");
            scenario.ApplyEvent();
            scenario.SeedResolutionApplied();
            scenario.Modes.QueueResult(scenario.GameDevice, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadflags);
            scenario.Watcher.Raise(scenario.Watcher.LastToken);
            checklist.Check(scenario.IsResolutionApplied && !scenario.DesktopRestored && ResolutionRestoreHelper.HoldingCount == 1,
                "N5: a revert the driver rejects on exit leaves isResolutionChangeApplied and the restore record in place");
            checklist.Check(scenario.Device.LevelFor(scenario.GameDevice) == WindowsLevel,
                "N5: the vibrance restore still happens when the resolution revert fails");

            // The exit ends the notification session, so the same game announces again next time.
            scenario = new NvidiaScenario("TestGameExitNv");
            int announced = 0;
            ResolutionChangeNotifier.ResolutionApplied += delegate { announced++; };
            scenario.ApplyEvent();
            ResolutionChangeNotifier.OnApplied(scenario.GameName, scenario.GameDevice, scenario.GameTarget, ResolutionHelper.ResolutionChangeResult.Applied);
            ResolutionChangeNotifier.OnApplied(scenario.GameName, scenario.GameDevice, scenario.GameTarget, ResolutionHelper.ResolutionChangeResult.Applied);
            scenario.Watcher.Raise(scenario.Watcher.LastToken);
            ResolutionChangeNotifier.OnApplied(scenario.GameName, scenario.GameDevice, scenario.GameTarget, ResolutionHelper.ResolutionChangeResult.Applied);
            checklist.Check(announced == 2, string.Format("N6: the game exit ends the notification session (announce, repeat suppressed, exit, announce again), got {0}", announced));
            ResolutionChangeNotifier.ResetForTests();

            // A watcher that could not arm (token 0) never triggers a revert; a replaced watcher is unsubscribed.
            scenario = new NvidiaScenario("TestGameExitNv");
            scenario.ApplyEvent();
            scenario.SeedResolutionApplied();
            scenario.Watcher.Raise(0);
            checklist.Check(scenario.IsResolutionApplied && scenario.ModeCalls == 0, "N7: a token of 0 (watch could not be armed) is ignored");
            scenario.DetachWatcher();
            scenario.Watcher.Raise(scenario.Watcher.LastToken);
            checklist.Check(!scenario.Watcher.HasSubscriber && scenario.IsResolutionApplied && scenario.ModeCalls == 0,
                "N7: replacing the watcher unsubscribes the previous one, so its later exit does nothing");

            CheckForegroundRevertKeepsSession(checklist, delegate { return new NvidiaScenario("TestGameExitNv"); }, "N8");

            NvidiaDynamicVibranceProxy.ResetForTests(null, new VibranceInfo(), new List<ApplicationSetting>());
            checklist.Lines.Add(string.Empty);
        }


        // The alt-tab cycle: apply, foreground revert, apply again. With an exit watch armed the
        // notification session must survive the foreground revert (only the game exiting ends it),
        // or every alt-tab back into the game would announce the same change again.
        private static void CheckForegroundRevertKeepsSession(Checklist checklist, Func<Scenario> create, string prefix)
        {
            Scenario scenario = create();
            int announced = 0;
            ResolutionChangeNotifier.ResolutionApplied += delegate { announced++; };
            scenario.ApplyEvent();
            scenario.SeedResolutionApplied();
            ResolutionChangeNotifier.OnApplied(scenario.GameName, scenario.GameDevice, scenario.GameTarget, ResolutionHelper.ResolutionChangeResult.Applied);
            bool ran = scenario.ForegroundEvent();
            if (!ran || !scenario.DesktopRestored)
            {
                checklist.Skip(prefix + ": alt-tab cycle - no foreground window on the game's screen to drive the real foreground branch");
                ResolutionChangeNotifier.ResetForTests();
                return;
            }
            ResolutionChangeNotifier.OnApplied(scenario.GameName, scenario.GameDevice, scenario.GameTarget, ResolutionHelper.ResolutionChangeResult.Applied);
            checklist.Check(announced == 1,
                string.Format("{0}: apply, foreground revert, re-apply with an exit watch armed raises exactly one notification, got {1}", prefix, announced));
            ResolutionChangeNotifier.ResetForTests();

            // No watcher armed (token 0): the foreground revert is the only end of the session.
            scenario = create();
            scenario.Watcher.ReturnZero = true;
            announced = 0;
            ResolutionChangeNotifier.ResolutionApplied += delegate { announced++; };
            scenario.ApplyEvent();
            scenario.SeedResolutionApplied();
            ResolutionChangeNotifier.OnApplied(scenario.GameName, scenario.GameDevice, scenario.GameTarget, ResolutionHelper.ResolutionChangeResult.Applied);
            ran = scenario.ForegroundEvent();
            if (!ran || !scenario.DesktopRestored)
            {
                checklist.Skip(prefix + ": alt-tab cycle without a watcher - no foreground window on the game's screen");
                ResolutionChangeNotifier.ResetForTests();
                return;
            }
            ResolutionChangeNotifier.OnApplied(scenario.GameName, scenario.GameDevice, scenario.GameTarget, ResolutionHelper.ResolutionChangeResult.Applied);
            checklist.Check(announced == 2,
                string.Format("{0}: with no watch armed (token 0) the foreground revert still ends the session, so the re-apply announces again, got {1}", prefix, announced));
            ResolutionChangeNotifier.ResetForTests();
        }

        // ------------------------------------------------------------------
        // AMD.
        // ------------------------------------------------------------------

        private class AmdScenario : Scenario
        {
            public readonly FakeAmdAdapter Adapter = new FakeAmdAdapter();
            public readonly AmdDynamicVibranceProxy Proxy;
            private readonly MethodInfo _onWinEventHook = typeof(AmdDynamicVibranceProxy).GetMethod("OnWinEventHook", BindingFlags.NonPublic | BindingFlags.Instance);
            private readonly FieldInfo _vibranceInfoField = typeof(AmdDynamicVibranceProxy).GetField("_vibranceInfo", BindingFlags.NonPublic | BindingFlags.Instance);

            public AmdScenario(string gameName) : base(gameName)
            {
                Proxy = new AmdDynamicVibranceProxy(Adapter, new List<ApplicationSetting> { BuildApplicationSetting(250) }, BuildWindowsSettings());
                Proxy.SetAffectPrimaryMonitorOnly(true);
                Proxy.SetVibranceWindowsLevel(WindowsLevel);
                Proxy.SetNeverChangeColorSettings(true);
                Proxy.SetNeverSwitchResolution(true);
                Proxy.SetGameExitWatcherForTests(Watcher, Modes);
            }

            public override void ApplyEvent()
            {
                _onWinEventHook.Invoke(Proxy, new object[] { null, BuildEventArgs(GameName) });
            }

            public override bool ForegroundEvent()
            {
                IntPtr foreground = AmdDynamicVibranceProxy.GetForegroundWindow();
                if (foreground == IntPtr.Zero || Screen.FromHandle(foreground).DeviceName != GameDevice)
                {
                    return false;
                }
                _onWinEventHook.Invoke(Proxy, new object[] { null, new WinEventHookEventArgs { Handle = foreground, ProcessName = "NotAGameProcess", ProcessImagePath = null } });
                // The real foreground window can change under a run; the revert only counts if it ran.
                return AmdDynamicVibranceProxy.GetForegroundWindow() == foreground;
            }

            public override void SeedResolutionApplied()
            {
                VibranceInfo info = Proxy.GetVibranceInfo();
                info.neverChangeResolution = false;
                info.isResolutionChangeApplied = true;
                _vibranceInfoField.SetValue(Proxy, info);
                PutGameModeOnTheFakeDisplay();
            }

            public override bool IsResolutionApplied
            {
                get { return Proxy.GetVibranceInfo().isResolutionChangeApplied; }
            }

            public override int ObservedVibranceLevelForGameDevice
            {
                get
                {
                    for (int i = Adapter.SetSaturationOnDisplayNames.Count - 1; i >= 0; i--)
                    {
                        if (Adapter.SetSaturationOnDisplayNames[i] == GameDevice)
                            return Adapter.SetSaturationOnDisplayLevels[i];
                    }
                    return int.MinValue;
                }
            }

            public override int VibranceWriteCount
            {
                get { return Adapter.SetSaturationOnDisplayNames.Count; }
            }

            public override ResolutionHelper.ResolutionChangeResult? Revert()
            {
                return Proxy.RevertGameResolution(Modes, GameDevice);
            }

            public override void DetachWatcher()
            {
                Proxy.SetGameExitWatcherForTests(null, Modes);
            }
        }

        private static void RunAmdChecks(Checklist checklist)
        {
            checklist.Lines.Add("AMD (real OnWinEventHook via reflection, fake display, fake adapter, fake watcher):");

            AmdScenario scenario = new AmdScenario("TestGameExitAmd");
            scenario.ApplyEvent();
            checklist.Check(scenario.Watcher.WatchedHandles.Count == 1 && scenario.Watcher.WatchedHandles[0] == scenario.Desktop,
                "A1: a matched apply event arms a watch for that event's hwnd");
            checklist.Check(scenario.ObservedVibranceLevelForGameDevice == 250,
                "A1: the apply wrote the ingame vibrance level to the game's screen");

            scenario.SeedResolutionApplied();
            scenario.Watcher.Raise(scenario.Watcher.LastToken);
            checklist.Check(scenario.DesktopRestored, "A2: the game exiting reverts the game screen's resolution with no foreground event at all");
            checklist.Check(!scenario.IsResolutionApplied, "A2: the exit revert clears isResolutionChangeApplied");
            checklist.Check(ResolutionRestoreHelper.HoldingCount == 0, "A2: the exit revert clears the persisted restore record");
            checklist.Check(scenario.ObservedVibranceLevelForGameDevice == WindowsLevel,
                string.Format("A2: the exit restores the Windows vibrance level, got {0}", scenario.ObservedVibranceLevelForGameDevice));

            scenario = new AmdScenario("TestGameExitAmd");
            scenario.Watcher.AlwaysNewToken = true;
            scenario.ApplyEvent();
            int tokenA = scenario.Watcher.LastToken;
            scenario.ApplyEvent();
            int tokenB = scenario.Watcher.LastToken;
            scenario.SeedResolutionApplied();
            int writesBefore = scenario.VibranceWriteCount;
            scenario.Watcher.Raise(tokenA);
            checklist.Check(tokenA != tokenB && !scenario.DesktopRestored && scenario.IsResolutionApplied && scenario.ModeCalls == 0 && scenario.VibranceWriteCount == writesBefore,
                "A3: an exit carrying a stale token is a no-op - no revert, flag untouched, no vibrance write");
            scenario.Watcher.Raise(tokenB);
            checklist.Check(scenario.DesktopRestored && !scenario.IsResolutionApplied, "A3: the current token's exit does revert");

            scenario = new AmdScenario("TestGameExitAmd");
            scenario.ApplyEvent();
            scenario.SeedResolutionApplied();
            ResolutionHelper.ResolutionChangeResult? foreground = scenario.Revert();
            int callsAfterForegroundRevert = scenario.ModeCalls;
            scenario.Watcher.Raise(scenario.Watcher.LastToken);
            checklist.Check(foreground == ResolutionHelper.ResolutionChangeResult.Applied && callsAfterForegroundRevert > 0 && scenario.ModeCalls == callsAfterForegroundRevert,
                string.Format("A4: an exit after the resolution was already reverted makes no further ChangeMode call, got {0} then {1}", callsAfterForegroundRevert, scenario.ModeCalls));

            scenario = new AmdScenario("TestGameExitAmd");
            scenario.ApplyEvent();
            scenario.SeedResolutionApplied();
            scenario.Modes.QueueResult(scenario.GameDevice, ChangeDisplaySettingsFlags.CdsTest, DispChange.DispChangeBadflags);
            scenario.Watcher.Raise(scenario.Watcher.LastToken);
            checklist.Check(scenario.IsResolutionApplied && !scenario.DesktopRestored && ResolutionRestoreHelper.HoldingCount == 1,
                "A5: a revert the driver rejects on exit leaves isResolutionChangeApplied and the restore record in place");
            checklist.Check(scenario.ObservedVibranceLevelForGameDevice == WindowsLevel,
                "A5: the vibrance restore still happens when the resolution revert fails");

            scenario = new AmdScenario("TestGameExitAmd");
            int announced = 0;
            ResolutionChangeNotifier.ResolutionApplied += delegate { announced++; };
            scenario.ApplyEvent();
            ResolutionChangeNotifier.OnApplied(scenario.GameName, scenario.GameDevice, scenario.GameTarget, ResolutionHelper.ResolutionChangeResult.Applied);
            ResolutionChangeNotifier.OnApplied(scenario.GameName, scenario.GameDevice, scenario.GameTarget, ResolutionHelper.ResolutionChangeResult.Applied);
            scenario.Watcher.Raise(scenario.Watcher.LastToken);
            ResolutionChangeNotifier.OnApplied(scenario.GameName, scenario.GameDevice, scenario.GameTarget, ResolutionHelper.ResolutionChangeResult.Applied);
            checklist.Check(announced == 2, string.Format("A6: the game exit ends the notification session (announce, repeat suppressed, exit, announce again), got {0}", announced));
            ResolutionChangeNotifier.ResetForTests();

            scenario = new AmdScenario("TestGameExitAmd");
            scenario.ApplyEvent();
            scenario.SeedResolutionApplied();
            scenario.Watcher.Raise(0);
            checklist.Check(scenario.IsResolutionApplied && scenario.ModeCalls == 0, "A7: a token of 0 (watch could not be armed) is ignored");
            scenario.DetachWatcher();
            scenario.Watcher.Raise(scenario.Watcher.LastToken);
            checklist.Check(!scenario.Watcher.HasSubscriber && scenario.IsResolutionApplied && scenario.ModeCalls == 0,
                "A7: replacing the watcher unsubscribes the previous one, so its later exit does nothing");

            CheckForegroundRevertKeepsSession(checklist, delegate { return new AmdScenario("TestGameExitAmd"); }, "A8");

            checklist.Lines.Add(string.Empty);
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
