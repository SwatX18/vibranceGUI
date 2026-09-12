using System;
using System.Runtime.InteropServices;

namespace vibrance.GUI.common
{
    /// <summary>
    /// The seam between a caller that only has GetForegroundWindow() to go on and the real
    /// foreground window - the toggle hotkey's WM_HOTKEY handler AND VibranceGUI's own startup
    /// foreground apply (upstream #81) both go through this. Same shape as
    /// IHotkeyRegistrar/IDisplayModeDevice/IGammaDevice: RealForegroundWindowReader (below) is
    /// the only production implementation; neither ProfileToggleFixture nor
    /// StartupForegroundFixture needs a fake of this one at all, because both reflect into the
    /// real proxy entry points (OnWinEventHook/ToggleForegroundProfile/
    /// ApplyStartupForegroundProfile) with synthetic WinEventHookEventArgs/IntPtr values
    /// directly, the same way VibranceRestoreFixture's N8 already does - unlike the six
    /// pre-existing AMD checks in that file, none of those checks need a "did the real
    /// foreground window change mid-test" Skip guard, because none of them read
    /// GetForegroundWindow() through this interface at all.
    /// </summary>
    internal interface IForegroundWindowReader
    {
        bool TryGetForeground(out IntPtr hWnd, out string processName, out string processImagePath);
    }

    /// <summary>
    /// The only production IForegroundWindowReader - names the foreground process the same way
    /// WinEventHook.WinEventProc does: GetForegroundWindow/GetWindowThreadProcessId/
    /// PathResolver.TryGetProcessImagePath, then WinEventHook.ResolveProcessName off that same
    /// image path - NOT a separate Process.GetProcessById of our own, which is what this used to
    /// do before it was re-keyed to agree with WinEventProc (see ResolveProcessName's own
    /// comment for why the two used to disagree for an executable renamed in place while still
    /// running - guide §6.8 blind spot #4, OBSERVED). Left to disagree, the startup apply below
    /// would match a renamed executable under one rule and un-apply it on the very first
    /// alt-tab under the other.
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

            // WinEventHook.ResolveProcessName, not a separate GetProcessById of our own - see
            // this class's header comment for why the two used to disagree for an executable
            // renamed in place while still running. Re-keyed on processName == null, exactly
            // WinEventProc's own no-dispatch rule (WinEventHook.cs:228-236): a path that resolved
            // for a process that has since exited still returns true with a correct name, a
            // strict narrowing of what the old try/catch's own two exception cases covered.
            processName = WinEventHook.ResolveProcessName((int)processId, processImagePath);
            if (processName == null)
            {
                return false;
            }

            return true;
        }
    }
}
