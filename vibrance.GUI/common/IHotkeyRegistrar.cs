using System;
using System.Runtime.InteropServices;

namespace vibrance.GUI.common
{
    /// <summary>
    /// What Register actually did. Not a bool: a caller needs to tell "another application
    /// already owns this exact key combination" apart from every other failure, since only the
    /// first one is worth a different message to the user (and, at Apply's own call site, a
    /// retry without MOD_NOREPEAT is worth attempting for the other case - see
    /// HotkeyRegistration.Apply).
    /// </summary>
    internal enum HotkeyRegistrationResult
    {
        // No binding was configured at all - Register was never even called.
        NotConfigured,
        Registered,
        AlreadyOwnedByAnotherApplication,
        Failed
    }

    /// <summary>
    /// The seam between HotkeyRegistration's lifecycle/retry logic and the real
    /// RegisterHotKey/UnregisterHotKey Win32 calls - same shape as IDisplayModeDevice
    /// (ResolutionHelper.cs) and IGammaDevice (DeviceGammaRampHelper.cs): RealHotkeyRegistrar
    /// (below) is the only production implementation; ProfileToggleFixture drives
    /// HotkeyRegistration entirely against its own fake, so the regression suite never calls
    /// RegisterHotKey for real - see that fixture's own header comment.
    /// </summary>
    internal interface IHotkeyRegistrar
    {
        HotkeyRegistrationResult Register(IntPtr hWnd, int id, uint modifiers, uint virtualKey);
        void Unregister(IntPtr hWnd, int id);
    }

    /// <summary>
    /// The only production IHotkeyRegistrar - RegisterHotKey/UnregisterHotKey against a real
    /// window handle.
    /// </summary>
    internal class RealHotkeyRegistrar : IHotkeyRegistrar
    {
        // ERROR_HOTKEY_ALREADY_REGISTERED (winerror.h) - another process already owns this exact
        // (modifiers, virtualKey) combination.
        private const int ErrorHotkeyAlreadyRegistered = 1409;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public HotkeyRegistrationResult Register(IntPtr hWnd, int id, uint modifiers, uint virtualKey)
        {
            bool succeeded = RegisterHotKey(hWnd, id, modifiers, virtualKey);
            // Captured on the line immediately after the call, before anything else can
            // overwrite the thread's last error - same discipline as Program.cs's
            // adapterDetectionWin32Error capture.
            int win32Error = Marshal.GetLastWin32Error();

            if (succeeded)
            {
                return HotkeyRegistrationResult.Registered;
            }
            if (win32Error == ErrorHotkeyAlreadyRegistered)
            {
                return HotkeyRegistrationResult.AlreadyOwnedByAnotherApplication;
            }
            return HotkeyRegistrationResult.Failed;
        }

        public void Unregister(IntPtr hWnd, int id)
        {
            UnregisterHotKey(hWnd, id);
        }
    }
}
