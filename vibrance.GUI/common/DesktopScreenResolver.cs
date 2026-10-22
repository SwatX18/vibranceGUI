using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace vibrance.GUI.common
{
    // Which monitor a foreground window is "on", for the revert gate.
    //
    // Screen.FromHandle works for a normal app, but the shell desktop (Progman, or a WorkerW the
    // shell parents the wallpaper to) has a rect that spans the WHOLE virtual desktop, every
    // monitor at once, so it does not name the one that was clicked. Measured on the dev box:
    // GetShellWindow() is Progman with rect -1920,0,4480,1440. MonitorFromWindow picks the monitor
    // with the largest overlap, which for a rect like that is the PRIMARY. Clicking the desktop
    // on the secondary monitor therefore looked like "focus is on the primary", which equals the
    // game's screen, and the primary's game resolution was reverted. For the shell desktop the
    // only honest answer to "which monitor" is where the pointer is.
    internal static class DesktopScreenResolver
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        // Fixture seam, null in production: lets a check call a window "the shell desktop"
        // without a real Progman.
        internal static Func<IntPtr, bool> IsShellDesktopOverride;

        private static bool _loggedClass;

        internal static bool IsShellDesktop(IntPtr hwnd)
        {
            Func<IntPtr, bool> over = IsShellDesktopOverride;
            if (over != null)
            {
                return over(hwnd);
            }
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            IntPtr shell = GetShellWindow();
            if (shell != IntPtr.Zero && hwnd == shell)
            {
                return true;
            }

            StringBuilder cls = new StringBuilder(64);
            if (GetClassName(hwnd, cls, cls.Capacity) == 0)
            {
                return false;
            }
            string className = cls.ToString();
            if (className != "Progman" && className != "WorkerW")
            {
                return false;
            }

            // The class name alone is not enough: any app can register a window called WorkerW.
            // Owned by the shell's own process is what makes it the desktop.
            if (shell == IntPtr.Zero)
            {
                return false;
            }
            uint shellPid;
            uint pid;
            GetWindowThreadProcessId(shell, out shellPid);
            GetWindowThreadProcessId(hwnd, out pid);
            if (shellPid == 0 || pid != shellPid)
            {
                return false;
            }

            if (!_loggedClass)
            {
                _loggedClass = true;
                Program.LogSafely(string.Format("Foreground window of class {0} treated as the shell desktop; resolving its monitor from the cursor.", className));
            }
            return true;
        }

        internal static Screen Resolve(IntPtr hwnd, Point cursor)
        {
            return IsShellDesktop(hwnd) ? Screen.FromPoint(cursor) : Screen.FromHandle(hwnd);
        }
    }
}
