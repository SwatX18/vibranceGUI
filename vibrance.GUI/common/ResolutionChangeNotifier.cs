using System;

namespace vibrance.GUI.common
{
    internal sealed class ResolutionAppliedEventArgs : EventArgs
    {
        public string GameName { get; private set; }
        public string DeviceName { get; private set; }
        public ResolutionModeWrapper Mode { get; private set; }
        public bool Verified { get; private set; }

        public ResolutionAppliedEventArgs(string gameName, string deviceName, ResolutionModeWrapper mode, bool verified)
        {
            GameName = gameName;
            DeviceName = deviceName;
            Mode = mode;
            Verified = verified;
        }
    }

    internal static class ResolutionChangeNotifier
    {
        public static event EventHandler<ResolutionAppliedEventArgs> ResolutionApplied;

        internal static void OnApplied(string gameName, string deviceName, ResolutionModeWrapper mode, ResolutionHelper.ResolutionChangeResult result)
        {
        }

        internal static void OnGameSessionEnded(string gameName)
        {
        }

        internal static string FormatText(ResolutionAppliedEventArgs e)
        {
            return string.Empty;
        }

        internal static void ResetForTests()
        {
            ResolutionApplied = null;
        }
    }
}
