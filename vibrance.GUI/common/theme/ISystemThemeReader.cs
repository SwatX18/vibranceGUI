using System;
using Microsoft.Win32;

namespace vibrance.GUI.common.theme
{
    /// <summary>
    /// The seam over "ask Windows whether applications should be light or dark" - the same shape as
    /// IReleaseSource, ILogSink and IHdrStateReader. Implementations must never throw: a theme
    /// lookup that can take down startup is strictly worse than a window painted in the wrong
    /// colours, and this one runs before the main form exists.
    /// </summary>
    public interface ISystemThemeReader
    {
        /// <summary>
        /// true when Windows says applications should be light, false when it says dark, and null
        /// when the question has no answer on this machine. Never throws.
        ///
        /// null is a real, expected outcome and not an error: AppsUseLightTheme is a Windows 10-era
        /// value, absent on the Windows 7 and 8.1 installs this application still runs on, absent
        /// again on a fresh profile that has never opened the personalisation page, and HKCU itself
        /// can be unreadable under policy. Every caller resolves null to
        /// the light theme (ThemeResolver.ResolveIsDark) - the same rule HdrDisplayState.Unknown
        /// follows, where a detection that cannot answer must reproduce the historical behaviour
        /// rather than guess.
        /// </summary>
        bool? TryGetAppsUseLightTheme();
    }

    /// <summary>
    /// The default ISystemThemeReader (see SystemThemeReader._current), and the reason a self-test
    /// run cannot be steered by whatever theme the developer's machine happens to be in.
    ///
    /// This defaults to "I do not know" for exactly the reason ReleaseSource defaults to
    /// OfflineReleaseSource and LogSink defaults to NullLogSink: the self tests in this codebase are
    /// driven by a reflection harness that calls a fixture's Run() directly and never goes through
    /// Program.Main, so anything Main installs is not installed for them. With the registry reader
    /// as the default, every fixture that resolved a theme would read the real HKCU, and the theme
    /// fixture's Auto cases would pass or fail depending on the machine they ran on - the one
    /// property a fixture must never have. Program.Main installs RegistrySystemThemeReader for a
    /// normal run and only for a normal run. Do not "fix" this default to the real one.
    /// </summary>
    public class UnknownSystemThemeReader : ISystemThemeReader
    {
        public bool? TryGetAppsUseLightTheme()
        {
            return null;
        }
    }

    /// <summary>
    /// The only production ISystemThemeReader: one HKCU value, read fresh on every call.
    ///
    /// Nothing is cached, because the value changes while the application runs - the user flips
    /// Windows into dark mode with vibranceGUI sitting in the tray - and a cached first answer is
    /// how an app ends up being the one window on the desktop still in light mode.
    ///
    /// Deliberately not modelled on RegistryController beside it, which is the codebase's other
    /// HKCU reader: it holds one RegistryKey in a field across four methods, opens the key writable
    /// even to read, leaks the handle on one path (D44) and throws NullReferenceException out of its
    /// own finally when OpenSubKey returns null (D5). A using block and a read-only open avoid all
    /// four, and this reader needs no shared state at all.
    /// </summary>
    public class RegistrySystemThemeReader : ISystemThemeReader
    {
        private const string PersonalizeKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize";
        private const string AppsUseLightThemeValue = "AppsUseLightTheme";

        public bool? TryGetAppsUseLightTheme()
        {
            try
            {
                using (RegistryKey personalize = Registry.CurrentUser.OpenSubKey(PersonalizeKey))
                {
                    if (personalize == null)
                    {
                        return null;
                    }

                    object value = personalize.GetValue(AppsUseLightThemeValue);

                    // The value is a REG_DWORD, which arrives boxed as an int. Anything else - a
                    // missing value (null), or a REG_SZ "1" written by hand or by a tweaking tool -
                    // is reported as unknown rather than coerced: Convert.ToBoolean on a string
                    // would throw for "1" and succeed for "True", and Convert.ToInt32 on null
                    // returns 0, which would silently mean "dark" for a value that is not there.
                    if (!(value is int))
                    {
                        return null;
                    }

                    // Documented as 1 = light, 0 = dark. Anything non-zero is treated as light,
                    // since the only value Windows itself writes besides 0 is 1.
                    return ((int)value) != 0;
                }
            }
            catch (Exception)
            {
                // SecurityException, UnauthorizedAccessException and IOException are all reachable
                // here under policy or on a damaged hive. None of them is worth a message box about
                // a colour scheme - unknown resolves to light, which is what the application looked
                // like for its entire history.
                return null;
            }
        }
    }

    /// <summary>
    /// Static injection point, mirroring ReleaseSource.Current and LogSink.Current - a single
    /// swappable static with a public setter, so a check can save what was there, install a fake and
    /// put the original back rather than assuming the default.
    /// </summary>
    public static class SystemThemeReader
    {
        // Defaults to the inert reader, never RegistrySystemThemeReader. See
        // UnknownSystemThemeReader's own comment for why, and do not change it: the entire value of
        // this default is that a fixture asserting on Auto gets the same answer on a build agent as
        // on a developer machine in dark mode.
        private static ISystemThemeReader _current = new UnknownSystemThemeReader();

        public static ISystemThemeReader Current
        {
            get { return _current; }
            set { _current = value ?? new UnknownSystemThemeReader(); }
        }

        public static void ResetForTests()
        {
            _current = new UnknownSystemThemeReader();
        }
    }
}
