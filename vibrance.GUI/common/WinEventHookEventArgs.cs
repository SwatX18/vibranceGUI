using System;

namespace vibrance.GUI.common
{
    class WinEventHookEventArgs : EventArgs
    {
        public string ProcessName { get; set; }
        // Full path of the executable behind the window, or null when Windows would not hand it out
        // - a protected or elevated process. Consumers fall back to ProcessName when it is null.
        public string ProcessImagePath { get; set; }
        public IntPtr Handle { get; set; }
        // Where the pointer was when the event was raised. The shell desktop has no rect that names
        // one monitor (it spans all of them), so DesktopScreenResolver needs this to say which monitor a desktop click was on.
        public System.Drawing.Point CursorPosition { get; set; }
    }
}
