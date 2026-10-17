namespace vibrance.GUI.common.theme
{
    /// <summary>
    /// What the user asked for, which is not the same question as what the window is painted in:
    /// only ThemeResolver.ResolveIsDark answers that, and only Auto ever consults Windows at all.
    ///
    /// The numeric values are written out rather than left implicit because Auto has to be the
    /// zero. A settings file that predates the themeMode key resolves to Auto
    /// (SettingsController.ReadThemeMode), so default(ThemeMode) - what an uninitialised field or
    /// a zeroed struct produces - must mean the same thing as "no preference stored", not Light.
    /// Reordering these members would not corrupt an existing INI, because the persisted form is
    /// the member name and never the number (ThemeResolver.FormatMode/ParseMode); it would change
    /// what a default-constructed value means, which is the part worth pinning here.
    /// </summary>
    public enum ThemeMode
    {
        Auto = 0,
        Light = 1,
        Dark = 2
    }
}
