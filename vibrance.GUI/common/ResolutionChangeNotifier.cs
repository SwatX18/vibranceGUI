using System;
using System.Globalization;

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

    /// <summary>
    /// Tells the UI that vibranceGUI just changed a game's resolution. Raised at most once per game
    /// session - the proxies call OnApplied on every apply, and an alt-tab back into the same game
    /// re-runs the apply branch, which must not pop a second notification for a change the user was
    /// already told about. A session ends through OnGameSessionEnded (the revert, or the game
    /// exiting); a different game also starts a fresh one. Everything runs on the UI thread, where
    /// the proxies' event handlers run, so no locking is needed.
    /// </summary>
    internal static class ResolutionChangeNotifier
    {
        public static event EventHandler<ResolutionAppliedEventArgs> ResolutionApplied;

        // The game whose change was already announced this session, or null when none is.
        private static string _announcedGame;

        internal static void OnApplied(string gameName, string deviceName, ResolutionModeWrapper mode, ResolutionHelper.ResolutionChangeResult result)
        {
            // Failed/Suppressed changed nothing, and AlreadyMatching means vibranceGUI did not
            // change anything this time - none of them is news to the user.
            if (result != ResolutionHelper.ResolutionChangeResult.Applied &&
                result != ResolutionHelper.ResolutionChangeResult.AppliedUnverified)
            {
                return;
            }

            if (_announcedGame != null && string.Equals(_announcedGame, gameName ?? string.Empty, StringComparison.Ordinal))
            {
                return;
            }
            _announcedGame = gameName ?? string.Empty;

            EventHandler<ResolutionAppliedEventArgs> handler = ResolutionApplied;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(null, new ResolutionAppliedEventArgs(gameName, deviceName, mode,
                    result == ResolutionHelper.ResolutionChangeResult.Applied));
            }
            catch (Exception ex)
            {
                // A misbehaving subscriber must never break the proxy's apply branch it is called from.
                Program.LogSafely(string.Format("Resolution change notification failed: {0}", ex));
            }
        }

        internal static void OnGameSessionEnded(string gameName)
        {
            // Ignores the end of a game other than the one announced (game A closing after game B
            // was applied must not re-arm B's notification); a null name means "whatever was tracked".
            if (gameName == null || _announcedGame == null ||
                string.Equals(_announcedGame, gameName, StringComparison.Ordinal))
            {
                _announcedGame = null;
            }
        }

        /// <summary>
        /// "cs2: 1280 x 960 @ 144 Hz, Center". Default (the driver's own scaling, which vibranceGUI
        /// does not touch) reads "Default (driver)"; " (unconfirmed)" is appended when the driver
        /// accepted the change but the read-back did not confirm it. The bit depth of
        /// ResolutionModeWrapper.ToString is left out on purpose - it is noise in a balloon tip.
        /// </summary>
        internal static string FormatText(ResolutionAppliedEventArgs e)
        {
            ResolutionModeWrapper mode = e.Mode;
            string scaling;
            switch (mode != null ? mode.DmDisplayFixedOutput : (uint)Dmdfo.Default)
            {
                case (uint)Dmdfo.Center:
                    scaling = "Center";
                    break;
                case (uint)Dmdfo.Stretch:
                    scaling = "Stretch";
                    break;
                default:
                    scaling = "Default (driver)";
                    break;
            }

            string text = mode == null
                ? string.Empty
                : string.Format(CultureInfo.InvariantCulture, "{0} x {1} @ {2} Hz, {3}",
                    mode.DmPelsWidth, mode.DmPelsHeight, mode.DmDisplayFrequency, scaling);

            if (!string.IsNullOrEmpty(e.GameName))
            {
                text = e.GameName + ": " + text;
            }
            if (!e.Verified)
            {
                text += " (unconfirmed)";
            }
            return text;
        }

        internal static void ResetForTests()
        {
            ResolutionApplied = null;
            _announcedGame = null;
        }
    }
}
