using System;
using System.Diagnostics;

namespace vibrance.GUI.common
{
    class WinEventHookEventArgs : EventArgs
    {
        public uint ProcessId { get; set; }
        public Process Process { get; set; }
        public string WindowText { get; set; }
        public string ProcessName { get; set; }
        // Full path of the executable behind the window, or null when Windows would not hand it out
        // - a protected or elevated process. Consumers fall back to ProcessName when it is null.
        public string ProcessImagePath { get; set; }
        public string MainWindowTitle { get; set; }
        public IntPtr Handle { get; set; }
    }
}
