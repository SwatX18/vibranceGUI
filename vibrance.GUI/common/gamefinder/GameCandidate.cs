using System.Drawing;

namespace vibrance.GUI.common.gamefinder
{
    // Appended to, never renumbered: the Store column is rendered from this per candidate. It is
    // not persisted to the settings file, so a new member costs nothing outside this assembly.
    public enum GameSource
    {
        Steam = 0,
        Epic = 1,
        Ea = 2,             // Electronic Arts, EA Games
        BattleNet = 3,      // Blizzard Entertainment
        Rockstar = 4,       // Rockstar Games
        Ubisoft = 5,        // Ubisoft, Ubisoft Entertainment
        OtherLauncher = 6   // an allowlisted publisher with no store of its own worth naming
    }

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
