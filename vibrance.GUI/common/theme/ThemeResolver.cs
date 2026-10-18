using System;

namespace vibrance.GUI.common.theme
{
    /// <summary>
    /// Everything about choosing a theme that can be decided without a registry, a settings file or
    /// a window - which is all of the parts worth testing. Every input is a parameter, exactly like
    /// UpdateCheckPolicy: the Windows flag arrives as a bool?, never read from HKCU in here, so a
    /// fixture can drive all nine mode/flag combinations without touching the machine's own theme.
    /// </summary>
    public static class ThemeResolver
    {
        /// <summary>
        /// The one answer the forms actually consume: dark, or not dark.
        ///
        /// An explicit Light or Dark always wins and never consults appsUseLightTheme - a user who
        /// picked dark expects dark, including on a machine whose Windows is light. Auto follows the
        /// flag, and a flag with no value resolves to LIGHT rather than dark. That direction is
        /// deliberate and is the same rule HdrDisplayState.Unknown follows: null means the lookup
        /// failed (Windows 7, a policy-locked HKCU, or the inert default reader), and a failed
        /// lookup must reproduce what every version of this application before the redesign looked
        /// like. Defaulting the unknown case to dark would repaint the whole UI on exactly the
        /// machines where the code cannot tell whether that is what the user wanted.
        /// </summary>
        public static bool ResolveIsDark(ThemeMode mode, bool? appsUseLightTheme)
        {
            if (mode == ThemeMode.Dark)
            {
                return true;
            }
            if (mode == ThemeMode.Light)
            {
                return false;
            }

            // Auto, and anything that is not one of the three members either (a cast int, a value
            // read back from a future settings file) - treated as Auto, matching ParseMode below.
            if (!appsUseLightTheme.HasValue)
            {
                return false;
            }
            return !appsUseLightTheme.Value;
        }

        /// <summary>
        /// The INI value read back, by member name, case-insensitively. Anything this does not
        /// recognise - empty, null, whitespace, "true", "Blue", a number - is Auto, which is also
        /// the default for a settings file that has never heard of the key, so a typo in a
        /// hand-edited INI lands on the default rather than on the other explicit mode.
        ///
        /// The numeric forms ("0", "1", "2") are deliberately NOT accepted even though they would be
        /// unambiguous, because FormatMode never writes them: accepting a form nothing produces
        /// means the reader has to keep working for a writer that does not exist.
        /// </summary>
        public static ThemeMode ParseMode(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return ThemeMode.Auto;
            }

            string trimmed = value.Trim();
            if (string.Equals(trimmed, ThemeMode.Light.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return ThemeMode.Light;
            }
            if (string.Equals(trimmed, ThemeMode.Dark.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return ThemeMode.Dark;
            }
            return ThemeMode.Auto;
        }

        /// <summary>
        /// The form written to the INI: the member name, so the file stays readable and a reordered
        /// enum cannot change what an existing file means.
        ///
        /// Only the three real members can come out of here; a value that is none of them (which an
        /// int cast can produce) is written as Auto rather than as its number. That is the same rule
        /// SettingsController.SetGraphicsAdapterPreference applies to the vendor key - the key can
        /// never be turned into a value the reader would have to guess about. ParseMode would map
        /// "42" to Auto anyway, so this costs nothing and keeps the file honest.
        /// </summary>
        public static string FormatMode(ThemeMode mode)
        {
            if (mode == ThemeMode.Light)
            {
                return ThemeMode.Light.ToString();
            }
            if (mode == ThemeMode.Dark)
            {
                return ThemeMode.Dark.ToString();
            }
            return ThemeMode.Auto.ToString();
        }
    }
}
