namespace vibrance.GUI.common.gamefinder
{
    /// <summary>
    /// The source-abstraction seam. A third store is an implementation of this and nothing else.
    /// Scan must never throw; every failure goes to GameScanContext.ReportError and the source
    /// returns. It must check context.IsCancelled at least once per game and once per directory it
    /// lists, must never set GameCandidate.IsAlreadyAdded, and must never reference WinForms or
    /// ApplicationSetting.
    /// </summary>
    public interface IGameLibrarySource
    {
        string DisplayName { get; }   // "Steam", "Epic Games"; shown in the Store column
        bool IsAvailable();           // cheap probe; must not enumerate the filesystem
        void Scan(GameScanContext context);
    }
}
