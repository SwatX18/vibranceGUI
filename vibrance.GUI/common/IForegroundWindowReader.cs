using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace vibrance.GUI.common
{
    /// <summary>
    /// The seam between the toggle hotkey's WM_HOTKEY handler and the real foreground window -
    /// same shape as IHotkeyRegistrar/IDisplayModeDevice/IGammaDevice: RealForegroundWindowReader
    /// (below) is the only production implementation; ProfileToggleFixture never needs a fake of
    /// this one at all, because it reflects into the real OnWinEventHook/ToggleForegroundProfile
    /// with synthetic WinEventHookEventArgs/IntPtr values directly, the same way
    /// VibranceRestoreFixture's N8 already does - unlike the six pre-existing AMD checks in that
    /// file, none of ProfileToggleFixture's checks need a "did the real foreground window change
    /// mid-test" Skip guard, because none of them read GetForegroundWindow() through this
    /// interface at all.
    /// </summary>
    internal interface IForegroundWindowReader
    {
        bool TryGetForeground(out IntPtr hWnd, out string processName, out string processImagePath);
    }

    /// <summary>
    /// The only production IForegroundWindowReader - mirrors WinEventHook.WinEventProc's own
    /// GetForegroundWindow/GetWindowThreadProcessId/PathResolver.TryGetProcessImagePath/
    /// Process.GetProcessById sequence and its same two tolerated exceptions (the process having
    /// already exited between the two calls is not this class's problem to solve, only to not
    /// crash on).
    /// </summary>
    internal class RealForegroundWindowReader : IForegroundWindowReader
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        public bool TryGetForeground(out IntPtr hWnd, out string processName, out string processImagePath)
        {
            hWnd = GetForegroundWindow();
            processName = null;
            processImagePath = null;

            if (hWnd == IntPtr.Zero)
            {
                return false;
            }

            uint processId;
            GetWindowThreadProcessId(hWnd, out processId);

            // Same fallback WinEventHook.WinEventProc already applies: a protected or elevated
            // process simply has no image path, not a failure worth aborting over.
            if (!PathResolver.TryGetProcessImagePath((int)processId, out processImagePath))
            {
                processImagePath = null;
            }

            try
            {
                using (Process p = Process.GetProcessById((int)processId))
                {
                    processName = p.ProcessName;
                }
            }
            catch (InvalidOperationException)
            {
                // The process property is not defined because the process has exited or it does
                // not have an identifier.
                return false;
            }
            catch (ArgumentException)
            {
                // The process specified by processId is not running.
                return false;
            }

            return true;
        }
    }
}
