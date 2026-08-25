using System.Drawing;

namespace vibrance.GUI.common.gamefinder
{
    public enum GameSource { Steam = 0, Epic = 1 }

    public enum ExecutableConfidence
    {
        Known   = 0,   // the store told us the executable (Epic LaunchExecutable)
        Guessed = 1    // ranked out of the install folder (Steam)
    }

    public class GameCandidate
    {
        public GameSource Source { get; set; }
        public string SourceAppId { get; set; }       // "730", or the Epic AppName
        public string GameName { get; set; }          // store display name; NEVER the installdir
        public string InstallDirectory { get; set; }  // full path, no trailing separator
        public string ExecutablePath { get; set; }    // full path; NEVER null
        public ExecutableConfidence Confidence { get; set; }
        public Icon Icon { get; set; }                // may be null; extracted on the worker thread
        public bool IsAlreadyAdded { get; set; }      // set by GameFinder, never by a source
        public string AlreadyAddedReason { get; set; }

        public GameCandidate() { }
    }
}
