using System;

namespace vibrance.GUI.common
{
    /// <summary>
    /// Watches the process that owns a game window and reports when it exits. Public (not
    /// internal, as the scaffold contract first had it) only because IVibranceProxy is public and
    /// SetGameExitWatcher exposes this type in its signature.
    /// </summary>
    public interface IGameExitWatcher
    {
        /// <summary>Token greater than 0 when armed, 0 if the process could not be opened.</summary>
        int Watch(IntPtr hWnd);
        void Cancel();
        /// <summary>Raised on the UI thread only, with the token Watch returned.</summary>
        event Action<int> GameExited;
    }

    internal sealed class RealGameExitWatcher : IGameExitWatcher
    {
        private readonly Action<Action> _postToUiThread;

        internal RealGameExitWatcher(Action<Action> postToUiThread)
        {
            _postToUiThread = postToUiThread;
        }

        public event Action<int> GameExited;

        public int Watch(IntPtr hWnd)
        {
            return 0;
        }

        public void Cancel()
        {
        }
    }
}
