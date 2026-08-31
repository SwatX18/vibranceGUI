using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace vibrance.GUI.common
{
    /// <summary>
    /// The single-instance-to-single-instance channel a second vibranceGUI process's own
    /// --set-vibrance uses to hand its request to the already-running instance, instead of
    /// Program.cs showing "You can run vibranceGUI only once at a time!" the way every other
    /// second launch still does.
    ///
    /// A window message, not a named pipe and not the INI file being written and watched: the app
    /// already owns a WndProc for WM_HOTKEY (see VibranceGUI.WndProc), so receiving this costs
    /// nothing extra while idle - no listener thread, no poll - which matters because this is a
    /// permanently-resident tray utility and upstream #156 was a real performance bug from doing
    /// too much work too often on exactly this kind of process. RegisterWindowMessage, not a bare
    /// application-defined WM_ constant, so SetVibranceLevelMessageId cannot collide with
    /// WM_HOTKEY, or with some other unrelated process's own custom message on the same desktop -
    /// this is exactly the scenario RegisterWindowMessage exists for.
    ///
    /// The target window is found by enumerating top-level windows and matching the OWNING
    /// PROCESS, not by a custom window class name. An earlier version gave VibranceGUI's own
    /// CreateParams a fixed ClassName so FindWindow could search by it - dropped after
    /// NativeWindow.WindowClass.RegisterClass measured unreliable (Win32 ERROR_INVALID_HANDLE)
    /// registering a custom class in at least one real build/test environment here, reproduced
    /// across Form, Control and bare NativeWindow. If VibranceGUI's own real window ever hit that,
    /// its handle would never be created and the app would not start at all - a total regression
    /// in exchange for a convenience flag, and one no fixture here could have caught, since none
    /// constructs a real Form. Enumerating by process removes the need for VibranceGUI to register
    /// anything unusual in the first place: it creates its window exactly as it always has.
    ///
    /// FindTopLevelWindowForProcess/TryRelayTo are the seam CliOptionsFixture drives directly,
    /// against a real (if throwaway) top-level window and its own process id - never against a
    /// second real vibranceGUI process, which FindOtherRunningInstanceProcessId/TryRelay below
    /// would have to actually launch to exercise for real. A fixture run while a real vibranceGUI
    /// happens to be running on the same desktop must never be able to reach into it and change a
    /// real display's vibrance; spawning a second copy of the real exe just to drive that path in a
    /// test risks exactly that (a genuine collision with a real running instance from a shared
    /// machine, which is precisely the case the mutex - and this whole feature - exists around).
    /// FindOtherRunningInstanceProcessId/TryRelay are the production glue that supplies a real
    /// process name/path - untested for that reason, the same honest gap ApplyToggleHotkey's real
    /// RegisterHotKey call leaves for ProfileToggleFixture.
    /// </summary>
    internal static class VibranceCliRelay
    {
        // RegisterWindowMessage's own contract: 0 means the call failed. Every other WM_xxxx value
        // below WM_APP could already mean something else on this desktop - WM_NULL is literally 0
        // - so both TryRelayTo below and VibranceGUI.WndProc check for exactly this before ever
        // comparing a real Message.Msg against it, rather than let a failed registration silently
        // start misfiring on unrelated messages.
        internal static readonly int SetVibranceLevelMessageId =
            (int)RegisterWindowMessage("vibranceGUI~SetVibranceLevel");

        /// <summary>
        /// The first top-level window EnumWindows reports as owned by processId, or IntPtr.Zero
        /// when none exists (including "that process is not running at all"). Deliberately not
        /// filtered to WS_VISIBLE windows only: the whole point of --set-vibrance is reaching an
        /// instance sitting invisibly in the tray under -minimized (see SetVisibleCore, which
        /// forces the window invisible without ever destroying its handle) - filtering on
        /// visibility here would silently break the exact case upstream #120 asks for.
        /// </summary>
        internal static IntPtr FindTopLevelWindowForProcess(int processId)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                uint windowProcessId;
                GetWindowThreadProcessId(hWnd, out windowProcessId);
                if (windowProcessId == (uint)processId)
                {
                    found = hWnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>
        /// Finds processId's top-level window and sends it messageId with level as wParam. Returns
        /// false - never throws - when messageId is the "registration failed" sentinel or no
        /// matching window exists right now (including "that process is not running at all", the
        /// case Program.cs falls back to its own "only once" dialog for). SendMessage, not
        /// PostMessage: the caller is a short-lived process about to exit anyway, so blocking until
        /// the receiver's WndProc has actually processed the request costs it nothing, and is
        /// exactly the "blocking wait is fine, a poll is not" tradeoff this whole mechanism is
        /// built around - see this class's own header comment.
        /// </summary>
        internal static bool TryRelayTo(int processId, int messageId, int level)
        {
            if (messageId == 0)
            {
                return false;
            }

            IntPtr targetWindow = FindTopLevelWindowForProcess(processId);
            if (targetWindow == IntPtr.Zero)
            {
                return false;
            }

            SendMessage(targetWindow, messageId, (IntPtr)level, IntPtr.Zero);
            return true;
        }

        /// <summary>
        /// The other running vibranceGUI's process id, found by matching every process sharing this
        /// one's name against this one's own executable path (via PathResolver.TryGetProcessImagePath
        /// - the same x86-safe, query-only lookup ProcessExplorer already uses instead of
        /// Process.MainModule.FileName, which crashes reading a 64 bit process from this always-x86
        /// build), excluding this process itself. Null covers "no other instance is running" and
        /// "its path could not be read" alike - both mean TryRelay has nothing to relay to.
        /// </summary>
        private static int? FindOtherRunningInstanceProcessId()
        {
            Process current = Process.GetCurrentProcess();
            string currentImagePath;
            if (!PathResolver.TryGetProcessImagePath(current.Id, out currentImagePath) || string.IsNullOrEmpty(currentImagePath))
            {
                return null;
            }

            Process[] candidates = Process.GetProcessesByName(current.ProcessName);
            try
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    Process candidate = candidates[i];
                    if (candidate.Id == current.Id)
                    {
                        continue;
                    }

                    string candidateImagePath;
                    if (PathResolver.TryGetProcessImagePath(candidate.Id, out candidateImagePath) &&
                        string.Equals(candidateImagePath, currentImagePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate.Id;
                    }
                }
            }
            finally
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    candidates[i].Dispose();
                }
            }

            return null;
        }

        /// <summary>
        /// Program.cs's actual entry point into this class - the real running instance, found by
        /// process identity, and the real message id. See this class's own header comment for why
        /// CliOptionsFixture drives TryRelayTo/FindTopLevelWindowForProcess directly instead of
        /// this wrapper.
        /// </summary>
        internal static bool TryRelay(int level)
        {
            int? targetProcessId = FindOtherRunningInstanceProcessId();
            if (!targetProcessId.HasValue)
            {
                return false;
            }
            return TryRelayTo(targetProcessId.Value, SetVibranceLevelMessageId, level);
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
