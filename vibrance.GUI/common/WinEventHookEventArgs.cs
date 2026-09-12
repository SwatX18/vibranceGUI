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
    }
}
