using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Windows.Forms;
using vibrance.GUI.common.theme;

namespace vibrance.GUI.common
{
    /// <summary>
    /// The whole decidable surface of the theme system: mode resolution, the spelling a mode is
    /// persisted in, both palettes' completeness, and every contrast ratio those palettes were built
    /// to. Run by vibrance.GUI.exe --selftest-theme.
    ///
    /// Nothing here constructs a Form, calls Theme.Apply, allocates a font, writes a file or reads
    /// HKCU. The ApplyToToolTip section at the end does construct one Component - a ToolTip, which
    /// allocates no window and needs no message loop - and reads back the four properties that
    /// method assigns. What the tip paints is not covered: that happens in a Draw event which only
    /// fires with a real window under a real cursor. That is not an omission - a colour scheme is the one feature whose test is trivially
    /// easy to write against the machine it runs on and worthless the moment it leaves that machine.
    /// SystemThemeReader.Current is the inert UnknownSystemThemeReader for every fixture run
    /// (Program.Main installs the registry reader only when no argument starts with "--selftest",
    /// and the reflection harness that calls Run() directly never enters Main at all), and every
    /// Auto case below installs its own fake on top of that default, so the nine mode/flag
    /// combinations answer the same on a build agent as on a developer machine in dark mode.
    ///
    /// The contrast half is the part worth the most. ThemePalette records a measured ratio beside
    /// every colour it defines - and a comment cannot fail. The evidence that this matters is
    /// already in this codebase: the "(Off)" marker shipped in Color.DarkOrange, which measures
    /// 2.33:1 on the ListView's white background, and nothing caught it for the life of the
    /// application because nobody measured it. So every pair is re-measured here, the ratio is
    /// printed on the line whether it passed or failed - a regression should show how far a colour
    /// moved and not merely that it moved - and ThemeContrast itself is calibrated first, against
    /// three ratios measured outside this codebase, so a checker that is uniformly wrong cannot
    /// certify a palette as legible.
    ///
    /// SettingsController.ReadThemeMode/SetThemeMode are deliberately not driven here: they are
    /// GetPrivateProfileString and WritePrivateProfileString over the real settings file, and the
    /// only decision either one makes is delegated to ThemeResolver.ParseMode/FormatMode, which the
    /// persistence section below covers without touching the disk.
    /// </summary>
    public class ThemeFixture
    {
        /// <summary>
        /// The fixture-owned ISystemThemeReader, in the same spirit as UpdateCheckFixture's
        /// StubReleaseSource. Value is settable after construction because two checks need Windows
        /// to change its mind mid-run - that is the whole point of RefreshFromSystem - and Calls is
        /// counted because a Theme that cached the flag on first read would pass every Initialize
        /// check below and then never notice a WM_SETTINGCHANGE in a real run.
        /// </summary>
        private class FakeSystemThemeReader : ISystemThemeReader
        {
            public bool? Value { get; set; }
            public int Calls { get; private set; }

            public FakeSystemThemeReader(bool? value)
            {
                Value = value;
            }

            public bool? TryGetAppsUseLightTheme()
            {
                Calls++;
                return Value;
            }
        }

        /// <summary>
        /// ApplyToToolTip(null) returns void, so there is nothing for a check to read. This wraps it
        /// into a bool the same way the rest of the file reads a decision: true means it came back
        /// at all, which is the whole claim - a null tip must be ignored, not thrown over.
        /// </summary>
        private static bool SafeApplyToolTip(ToolTip tip)
        {
            Theme.ApplyToToolTip(tip);
            return true;
        }

        public static List<string> Run()
        {
            List<string> lines = new List<string>();
            int passed = 0;
            int total = 0;

            Action<string, bool> check = (label, ok) =>
            {
                total++;
                if (ok)
                    passed++;
                lines.Add(string.Format("[{0}] {1}", ok ? "PASS" : "FAIL", label));
            };

            lines.Add("Mode resolution (criteria 2 and 3) - all nine combinations, none of them read from HKCU:");

            ThemeMode[] modes = new ThemeMode[] { ThemeMode.Auto, ThemeMode.Light, ThemeMode.Dark };
            bool?[] flags = new bool?[] { true, false, null };

            // Rows are modes, columns are the Windows flag, and the cell is "is dark". The two rows
            // that hold a single value are the criterion: an explicit Light or Dark must ignore
            // appsUseLightTheme in every column, because a user who picked dark expects dark on a
            // light Windows. The Auto row's third column is the other one that matters - an ABSENT
            // answer resolves LIGHT, which is what this application looked like for its entire
            // history. Defaulting the unknown case to dark would repaint the whole UI on exactly the
            // machines (Windows 7, a policy-locked HKCU) where the code cannot tell whether that is
            // what the user wanted.
            bool[,] expectedDark = new bool[,]
            {
                { false, true, false },
                { false, false, false },
                { true, true, true }
            };

            for (int m = 0; m < modes.Length; m++)
            {
                for (int f = 0; f < flags.Length; f++)
                {
                    bool resolved = ThemeResolver.ResolveIsDark(modes[m], flags[f]);
                    check(string.Format("{0} + appsUseLightTheme={1} resolves {2} (expected {3})",
                        modes[m].ToString().PadRight(5),
                        DescribeFlag(flags[f]).PadRight(7),
                        resolved ? "dark " : "light",
                        expectedDark[m, f] ? "dark" : "light"),
                        resolved == expectedDark[m, f]);
                }
            }

            // A value that is none of the three members is reachable without anyone writing a cast:
            // an int cast, a zeroed struct, or a reflected write. Resolving such a value as dark -
            // which is what a bare "if (mode != Light) return true" does - turns a settings file
            // this build does not understand into a fully repainted UI. The expectation here is
            // Auto's own answer rather than a literal, so these rows cannot disagree with the table.
            foreach (bool? flag in flags)
            {
                bool resolved = ThemeResolver.ResolveIsDark((ThemeMode)42, flag);
                bool asAuto = ThemeResolver.ResolveIsDark(ThemeMode.Auto, flag);
                check(string.Format("a mode outside the enum + appsUseLightTheme={0} resolves as Auto does ({1})",
                    DescribeFlag(flag).PadRight(7), asAuto ? "dark" : "light"),
                    resolved == asAuto);
            }

            lines.Add(string.Empty);
            lines.Add("How a mode survives the INI (criterion 8) - the only form either half writes or reads:");

            foreach (ThemeMode mode in modes)
            {
                string written = ThemeResolver.FormatMode(mode);
                check(string.Format("FormatMode({0}) writes \"{1}\" and ParseMode reads the same mode back",
                    mode, written),
                    written == mode.ToString() && ThemeResolver.ParseMode(written) == mode);
            }

            // The enum's zero has to be Auto, not Light: SettingsController.ReadThemeMode hands back
            // Auto for a settings file that has never heard of the themeMode key, and a
            // default-constructed ThemeMode has to mean the same thing as "no preference stored" or
            // two paths into the same state disagree about what the user asked for.
            check("default(ThemeMode) is Auto, the same answer a settings file with no themeMode key gives",
                default(ThemeMode) == ThemeMode.Auto && (int)ThemeMode.Auto == 0);

            check("FormatMode of a value outside the enum writes \"Auto\", never its number",
                ThemeResolver.FormatMode((ThemeMode)42) == "Auto");
            check("\"light\" and \"DARK\" parse case-insensitively - a hand-edited INI is not case sensitive",
                ThemeResolver.ParseMode("light") == ThemeMode.Light &&
                ThemeResolver.ParseMode("DARK") == ThemeMode.Dark);
            check("surrounding whitespace and a stray newline are trimmed rather than treated as garbage",
                ThemeResolver.ParseMode("  Light ") == ThemeMode.Light &&
                ThemeResolver.ParseMode("\r\nDark\r\n") == ThemeMode.Dark &&
                ThemeResolver.ParseMode(" auto ") == ThemeMode.Auto);

            // Every one of these has to land on Auto, and none of them may throw: this runs on the
            // startup path before any form exists, so an exception out of here is an application
            // that does not start because of a typo in a colour preference. "0"/"1"/"2" are in the
            // list on purpose - they are unambiguous and are still rejected, because FormatMode
            // never writes them, and a reader that accepts a form nothing produces has to keep
            // working for a writer that does not exist.
            string[,] garbage = new string[,]
            {
                { null, "null" },
                { "", "empty" },
                { "   ", "whitespace" },
                { "\t", "a tab" },
                { "0", "\"0\"" },
                { "1", "\"1\"" },
                { "2", "\"2\"" },
                { "true", "\"true\"" },
                { "Blue", "\"Blue\"" },
                { "LiGhTs", "\"LiGhTs\"" },
                { "dArkness", "\"dArkness\"" },
                { "Light Dark", "\"Light Dark\"" },
                { "Ligh", "\"Ligh\" (truncated)" },
                { "Light;Dark", "\"Light;Dark\"" },
                { "Ðark", "a non-ASCII lookalike" },
                { "ライト", "katakana" },
                { new string('x', 4096), "4096 characters" }
            };

            for (int g = 0; g < garbage.GetLength(0); g++)
            {
                // Seeded with Light rather than Auto so a swallowed exception cannot look like a
                // pass: if ParseMode throws, this check fails on both counts rather than on neither.
                ThemeMode parsed = ThemeMode.Light;
                bool threw = false;
                try
                {
                    parsed = ThemeResolver.ParseMode(garbage[g, 0]);
                }
                catch (Exception)
                {
                    threw = true;
                }
                check(string.Format("ParseMode({0}) is Auto{1}",
                    garbage[g, 1].PadRight(24),
                    threw ? " - THREW" : string.Format(" (got {0})", parsed)),
                    !threw && parsed == ThemeMode.Auto);
            }

            lines.Add(string.Empty);
            lines.Add("Palette completeness (criterion 4) - enumerated by reflection, so a 20th colour is covered:");

            List<PropertyInfo> colourProperties = ColourProperties();
            ThemePalette light = ThemePalette.Light();
            ThemePalette dark = ThemePalette.Dark();

            // Both directions matter. Reflecting the properties means a colour ADDED later is
            // completeness-checked without this file being edited; asserting the count is what
            // catches a colour REMOVED, which reflection alone would silently stop covering - and a
            // removed palette colour means some control has gone back to picking its own.
            check(string.Format("ThemePalette declares 19 Color properties (found {0})", colourProperties.Count),
                colourProperties.Count == 19);

            string lightGap = FirstUnusable(colourProperties, light);
            check(lightGap == null
                ? "every colour in the light palette is assigned and fully opaque"
                : "light palette: " + lightGap,
                lightGap == null);
            string darkGap = FirstUnusable(colourProperties, dark);
            check(darkGap == null
                ? "every colour in the dark palette is assigned and fully opaque"
                : "dark palette: " + darkGap,
                darkGap == null);

            check("Light() says it is light and Dark() says it is dark, so no consumer has to compare colours",
                !light.IsDark && dark.IsDark);

            // The palettes being immutable and value-identical for a given IsDark is not a
            // curiosity: RefreshFromSystem leaves Current pointing at the OLD instance when nothing
            // flipped, and that is only indistinguishable from swapping in a fresh one while this
            // holds.
            check("two Light() instances are value-identical, which is what lets RefreshFromSystem keep the old one",
                ValueIdentical(colourProperties, ThemePalette.Light(), ThemePalette.Light()) &&
                ValueIdentical(colourProperties, ThemePalette.Dark(), ThemePalette.Dark()));

            // Every dark colour has to be a fresh choice rather than a light one reused.
            // ThemePalette's own comment records why borrowing cannot work in either direction - the
            // light StatusGood #107C10 measures 2.35:1 on #333333, the dark #6CCB6C measures 2.02:1
            // on white - so a value appearing in both palettes is a colour somebody forgot to
            // invert, and it will be unreadable in one of the two themes.
            string shared = FirstSharedValue(colourProperties, light, dark);
            check(shared == null
                ? "no colour value appears in both palettes - nothing was carried over uninverted"
                : "carried over uninverted: " + shared,
                shared == null);

            check(string.Format("light layers: window {0} sits under the brighter surface {1}",
                Hex(light.WindowBackground), Hex(light.SurfaceBackground)),
                ThemeContrast.RelativeLuminance(light.WindowBackground) <
                ThemeContrast.RelativeLuminance(light.SurfaceBackground));
            check(string.Format("dark layers get lighter as they come forward: {0} < {1} < {2}",
                Hex(dark.WindowBackground), Hex(dark.SurfaceBackground), Hex(dark.ControlBackground)),
                ThemeContrast.RelativeLuminance(dark.WindowBackground) <
                ThemeContrast.RelativeLuminance(dark.SurfaceBackground) &&
                ThemeContrast.RelativeLuminance(dark.SurfaceBackground) <
                ThemeContrast.RelativeLuminance(dark.ControlBackground));
            // A "dark" palette whose lightest surface is still lighter than the light palette's
            // darkest one is not a dark theme, however well each individual ratio measures.
            check("every dark background is darker than every light background",
                ThemeContrast.RelativeLuminance(dark.ControlBackground) <
                ThemeContrast.RelativeLuminance(light.WindowBackground));

            lines.Add(string.Empty);
            lines.Add("Calibrating the ruler before measuring with it - figures taken outside this codebase:");

            Color white = Color.FromArgb(0xFF, 0xFF, 0xFF);
            Color black = Color.FromArgb(0x00, 0x00, 0x00);

            // Three ratios from outside this repository. Without them every contrast check below
            // would be self-consistent and could still be uniformly wrong: a Linearise() with the
            // gamma exponent mistyped, or luminance weights in the wrong channel order, passes a
            // palette that was measured with the same broken function it was chosen against. The
            // third figure is the one already written down in VibranceGUI.cs's own comment for the
            // shipping #C05000 marker.
            CheckRatio(check, "Color.Red on white", Color.Red, white, 3.998);
            CheckRatio(check, "Color.DarkOrange on white", Color.DarkOrange, white, 2.332);
            CheckRatio(check, "#C05000 on white - the figure VibranceGUI.cs records",
                Color.FromArgb(0xC0, 0x50, 0x00), white, 4.785);
            CheckRatio(check, "black on white, the top of the scale", black, white, 21.0);
            CheckRatio(check, "white on white, the bottom of the scale", white, white, 1.0);

            check(string.Format(CultureInfo.InvariantCulture,
                "Ratio is symmetric - {0:0.0000} read either way round, as the WCAG definition requires",
                ThemeContrast.Ratio(Color.Red, white)),
                ThemeContrast.Ratio(Color.Red, white) == ThemeContrast.Ratio(white, Color.Red) &&
                ThemeContrast.Ratio(light.TextPrimary, light.WindowBackground) ==
                ThemeContrast.Ratio(light.WindowBackground, light.TextPrimary));

            // Weighted 0.2126/0.7152/0.0722, not averaged: a pure channel linearises to exactly 1.0,
            // so each of these IS its own weight. This is the check that fails if the weights are
            // swapped between channels or replaced with (R+G+B)/3 - both of which still produce a
            // plausible-looking number for every grey in the palette, and greys are most of it.
            check(string.Format(CultureInfo.InvariantCulture,
                "luminance is channel-weighted: red {0:0.0000}, green {1:0.0000}, blue {2:0.0000}",
                ThemeContrast.RelativeLuminance(Color.FromArgb(0xFF, 0x00, 0x00)),
                ThemeContrast.RelativeLuminance(Color.FromArgb(0x00, 0xFF, 0x00)),
                ThemeContrast.RelativeLuminance(Color.FromArgb(0x00, 0x00, 0xFF))),
                Math.Abs(ThemeContrast.RelativeLuminance(Color.FromArgb(0xFF, 0x00, 0x00)) - 0.2126) < 0.000001 &&
                Math.Abs(ThemeContrast.RelativeLuminance(Color.FromArgb(0x00, 0xFF, 0x00)) - 0.7152) < 0.000001 &&
                Math.Abs(ThemeContrast.RelativeLuminance(Color.FromArgb(0x00, 0x00, 0xFF)) - 0.0722) < 0.000001);
            check("white linearises to 1.0 and black to 0.0",
                Math.Abs(ThemeContrast.RelativeLuminance(white) - 1.0) < 0.000001 &&
                Math.Abs(ThemeContrast.RelativeLuminance(black)) < 0.000001);

            check(string.Format(CultureInfo.InvariantCulture,
                "the two thresholds are the WCAG ones: {0:0.0} for text, {1:0.0} for non-text",
                ThemeContrast.AaTextRatio, ThemeContrast.NonTextRatio),
                ThemeContrast.AaTextRatio == 4.5 && ThemeContrast.NonTextRatio == 3.0);
            // The two predicates must not turn out to be the same predicate. Color.Red on white sits
            // between the two bars at 3.998:1, so it is the one pair that can tell them apart.
            check("MeetsAa and MeetsNonText apply different bars: Color.Red on white fails as text, passes as a border",
                !ThemeContrast.MeetsAa(Color.Red, white) && ThemeContrast.MeetsNonText(Color.Red, white));

            // ThemeContrast's Linearise uses WCAG 2.0's published 0.03928 breakpoint where the sRGB
            // specification says 0.04045, and its comment claims the choice cannot matter for 8-bit
            // channels. Checked over all 256 values rather than trusted, because if it were ever
            // wrong it would be wrong only for the very darkest colours - which is exactly where the
            // dark palette's backgrounds live.
            bool breakpointAgrees = true;
            for (int channel = 0; channel < 256; channel++)
            {
                double c = channel / 255.0;
                if ((c <= 0.03928) != (c <= 0.04045))
                {
                    breakpointAgrees = false;
                }
            }
            check("WCAG's 0.03928 breakpoint and sRGB's 0.04045 pick the same branch for all 256 channel values",
                breakpointAgrees);

            lines.Add(string.Empty);
            lines.Add("Light palette contrast (criterion 5) - every ratio re-measured, not read from a comment:");
            AppendContrastChecks(check, "light", light);

            lines.Add(string.Empty);
            lines.Add("Dark palette contrast (criterion 5) - the same matrix, worst case on ControlBackground:");
            AppendContrastChecks(check, "dark", dark);

            lines.Add(string.Empty);
            lines.Add("The light palette repairs shipping defects rather than carrying them over (criterion 7):");

            Color shippedSuppressed = Color.FromArgb(0xC0, 0x50, 0x00);

            // These three are not hypothetical bad choices. Color.Red and Color.DarkOrange are the
            // colours this application draws its status markers in today, and #C05000 is
            // VibranceGUI.SuppressedForeColor. Asserting that the OLD colours FAIL is half of each
            // check - it is what stops this section quietly passing if somebody ever "simplifies"
            // the palette back to the named Colors.
            check(string.Format(CultureInfo.InvariantCulture,
                "Color.Red, today's failure marker, fails AA on both light backgrounds: {0:0.000}:1 on {1}, {2:0.000}:1 on {3}",
                ThemeContrast.Ratio(Color.Red, white), Hex(white),
                ThemeContrast.Ratio(Color.Red, light.WindowBackground), Hex(light.WindowBackground)),
                !ThemeContrast.MeetsAa(Color.Red, white) &&
                !ThemeContrast.MeetsAa(Color.Red, light.WindowBackground));
            check(string.Format(CultureInfo.InvariantCulture,
                "light StatusBad {0} beats it on both at {1:0.000}:1 and {2:0.000}:1, and clears AA",
                Hex(light.StatusBad),
                ThemeContrast.Ratio(light.StatusBad, white),
                ThemeContrast.Ratio(light.StatusBad, light.WindowBackground)),
                ThemeContrast.Ratio(light.StatusBad, white) > ThemeContrast.Ratio(Color.Red, white) &&
                ThemeContrast.Ratio(light.StatusBad, light.WindowBackground) >
                    ThemeContrast.Ratio(Color.Red, light.WindowBackground) &&
                ThemeContrast.MeetsAa(light.StatusBad, white) &&
                ThemeContrast.MeetsAa(light.StatusBad, light.WindowBackground));

            check(string.Format(CultureInfo.InvariantCulture,
                "Color.DarkOrange, today's \"(Off)\" marker, fails AA on white at {0:0.000}:1",
                ThemeContrast.Ratio(Color.DarkOrange, white)),
                !ThemeContrast.MeetsAa(Color.DarkOrange, white));
            check(string.Format(CultureInfo.InvariantCulture,
                "light StatusWarning {0} beats it at {1:0.000}:1 on white and {2:0.000}:1 on the window",
                Hex(light.StatusWarning),
                ThemeContrast.Ratio(light.StatusWarning, white),
                ThemeContrast.Ratio(light.StatusWarning, light.WindowBackground)),
                ThemeContrast.Ratio(light.StatusWarning, white) > ThemeContrast.Ratio(Color.DarkOrange, white) &&
                ThemeContrast.MeetsAa(light.StatusWarning, white) &&
                ThemeContrast.MeetsAa(light.StatusWarning, light.WindowBackground));

            // The subtler half of the same criterion. #C05000 passes on white, which is the only
            // background it was ever drawn on, so it was never a defect where it shipped. It becomes
            // one the moment the same role is also painted over the window background - which is
            // exactly what the redesign does with it.
            check(string.Format(CultureInfo.InvariantCulture,
                "#C05000 passes on white at {0:0.000}:1 but FAILS on the window background at {1:0.000}:1",
                ThemeContrast.Ratio(shippedSuppressed, white),
                ThemeContrast.Ratio(shippedSuppressed, light.WindowBackground)),
                ThemeContrast.MeetsAa(shippedSuppressed, white) &&
                !ThemeContrast.MeetsAa(shippedSuppressed, light.WindowBackground));
            check(string.Format(CultureInfo.InvariantCulture,
                "light StatusSuppressed {0} clears AA on both at {1:0.000}:1 and {2:0.000}:1",
                Hex(light.StatusSuppressed),
                ThemeContrast.Ratio(light.StatusSuppressed, white),
                ThemeContrast.Ratio(light.StatusSuppressed, light.WindowBackground)),
                ThemeContrast.MeetsAa(light.StatusSuppressed, white) &&
                ThemeContrast.MeetsAa(light.StatusSuppressed, light.WindowBackground) &&
                ThemeContrast.Ratio(light.StatusSuppressed, light.WindowBackground) >
                    ThemeContrast.Ratio(shippedSuppressed, light.WindowBackground));

            lines.Add(string.Empty);
            lines.Add("The seam - why an Auto check answers the same here as on a build agent:");

            // The same property UpdateCheckFixture asserts of ReleaseSource.Current, for the same
            // reason: Program.Main installs RegistrySystemThemeReader only when no argument starts
            // with "--selftest", and a reflection harness never enters Main at all, so a fixture run
            // sees the inert default. If that default were ever "fixed" to the registry reader, the
            // Auto rows in this file would start reporting whatever theme this machine is in.
            check("the reader a fixture run sees is the inert one, not the registry reader",
                SystemThemeReader.Current is UnknownSystemThemeReader);
            check("the inert reader reports \"I do not know\" rather than guessing a theme",
                new UnknownSystemThemeReader().TryGetAppsUseLightTheme() == null);

            ISystemThemeReader savedReader = SystemThemeReader.Current;
            try
            {
                SystemThemeReader.Current = null;
                check("assigning null falls back to the inert reader rather than NullReferencing inside Resolve",
                    SystemThemeReader.Current is UnknownSystemThemeReader);

                FakeSystemThemeReader fake = new FakeSystemThemeReader(true);
                SystemThemeReader.Current = fake;

                Theme.Initialize(ThemeMode.Auto);
                check("Auto with Windows in light mode resolves the light palette",
                    !Theme.Current.IsDark && Theme.Mode == ThemeMode.Auto);
                check("and the reader really was consulted - a flag cached at class load would not be",
                    fake.Calls > 0);

                fake.Value = false;
                Theme.Initialize(ThemeMode.Auto);
                check("Auto with Windows in dark mode resolves the dark palette",
                    Theme.Current.IsDark && Theme.Mode == ThemeMode.Auto);

                fake.Value = null;
                Theme.Initialize(ThemeMode.Auto);
                check("Auto with no answer at all resolves LIGHT - what every build before the redesign looked like",
                    !Theme.Current.IsDark);

                fake.Value = true;
                Theme.Initialize(ThemeMode.Dark);
                check("an explicit Dark ignores a light Windows",
                    Theme.Current.IsDark && Theme.Mode == ThemeMode.Dark);

                fake.Value = false;
                Theme.Initialize(ThemeMode.Light);
                check("an explicit Light ignores a dark Windows",
                    !Theme.Current.IsDark && Theme.Mode == ThemeMode.Light);

                check("Current is never null, whichever way it resolved", Theme.Current != null);

                lines.Add(string.Empty);
                lines.Add("Theme's own state - what the settings UI persists, and what earns a repaint:");

                fake.Value = true;
                Theme.Initialize(ThemeMode.Light);
                check("SetMode reports true when IsDark actually flipped - the only case worth walking five control trees",
                    Theme.SetMode(ThemeMode.Dark) && Theme.Current.IsDark && Theme.Mode == ThemeMode.Dark);
                check("SetMode to the same resolved theme reports false",
                    !Theme.SetMode(ThemeMode.Dark) && Theme.Current.IsDark);

                bool darkToAuto = Theme.SetMode(ThemeMode.Auto);
                check("Dark to Auto on a light Windows is a flip back to light",
                    darkToAuto && !Theme.Current.IsDark && Theme.Mode == ThemeMode.Auto);

                // The case a naive "return whether anything changed" gets wrong in the other
                // direction: nothing on screen changes, so the return is rightly false, but the MODE
                // still has to change - or the settings UI shows Auto selected after the user picked
                // Light, and the INI keeps following Windows.
                bool autoToLight = Theme.SetMode(ThemeMode.Light);
                check("Auto to Light on a light Windows repaints nothing and still records the mode",
                    !autoToLight && Theme.Mode == ThemeMode.Light && !Theme.Current.IsDark);

                fake.Value = true;
                Theme.Initialize(ThemeMode.Auto);
                check("RefreshFromSystem with the Windows flag unchanged reports false, so a poll costs nothing",
                    !Theme.RefreshFromSystem() && !Theme.Current.IsDark);

                fake.Value = false;
                check("Windows flipped to dark while the app sat in the tray: true, and Current follows",
                    Theme.RefreshFromSystem() && Theme.Current.IsDark);
                check("a second call with nothing further changed reports false again",
                    !Theme.RefreshFromSystem() && Theme.Current.IsDark);

                fake.Value = true;
                check("and flipping back to light is another true",
                    Theme.RefreshFromSystem() && !Theme.Current.IsDark);

                Theme.Initialize(ThemeMode.Light);
                fake.Value = false;
                check("under an explicit Light, a Windows flip is ignored: false, and it stays light",
                    !Theme.RefreshFromSystem() && !Theme.Current.IsDark && Theme.Mode == ThemeMode.Light);

                Theme.Initialize(ThemeMode.Dark);
                fake.Value = true;
                check("under an explicit Dark, the same: false, and it stays dark",
                    !Theme.RefreshFromSystem() && Theme.Current.IsDark && Theme.Mode == ThemeMode.Dark);

                lines.Add(string.Empty);
                lines.Add("ApplyToToolTip - the one component the control walk cannot reach:");

                // A ToolTip is a Component, so constructing one allocates no window and needs no
                // message loop: everything below is property state, which is the only part of a
                // tooltip that is decidable without a human looking at one. What the tip actually
                // paints goes through DrawBackground/DrawBorder/DrawText inside a Draw event that
                // only fires with a real window under the cursor, and no check here claims to cover
                // that.
                Theme.Initialize(ThemeMode.Dark);
                using (ToolTip tip = new ToolTip())
                {
                    tip.IsBalloon = true;
                    Theme.ApplyToToolTip(tip);
                    check("OwnerDraw goes true - without it BackColor and ForeColor are ignored outright",
                        tip.OwnerDraw);
                    check("IsBalloon goes false, because OwnerDraw is ignored while it is true (the documented trade)",
                        !tip.IsBalloon);
                    check(string.Format("dark BackColor is the palette's ControlBackground (#{0:X2}{1:X2}{2:X2})",
                            tip.BackColor.R, tip.BackColor.G, tip.BackColor.B),
                        tip.BackColor == ThemePalette.Dark().ControlBackground);
                    check(string.Format("dark ForeColor is the palette's TextPrimary (#{0:X2}{1:X2}{2:X2})",
                            tip.ForeColor.R, tip.ForeColor.G, tip.ForeColor.B),
                        tip.ForeColor == ThemePalette.Dark().TextPrimary);
                    check(string.Format("and that pair measures {0:N2}:1, which is the AA floor this palette was built to",
                            ThemeContrast.Ratio(tip.ForeColor, tip.BackColor)),
                        ThemeContrast.MeetsAa(tip.ForeColor, tip.BackColor));

                    // Re-applied, as a live theme switch does. The colours have to follow, and the
                    // Draw subscription must not stack - which is why ApplyToToolTip removes before
                    // it adds. Counted through a probe handler rather than by reflecting over the
                    // event's invocation list: the ToolTip.Draw backing field is private, and what
                    // matters is how many handlers actually run, not how many are stored.
                    int probeCalls = 0;
                    DrawToolTipEventHandler probe = (sender, e) => probeCalls++;
                    tip.Draw += probe;
                    Theme.Initialize(ThemeMode.Light);
                    Theme.ApplyToToolTip(tip);
                    Theme.ApplyToToolTip(tip);
                    check("a re-apply follows the new palette to light",
                        tip.BackColor == ThemePalette.Light().ControlBackground
                            && tip.ForeColor == ThemePalette.Light().TextPrimary);
                    check("the light pair measures AA too",
                        ThemeContrast.MeetsAa(tip.ForeColor, tip.BackColor));
                    check("the fixture's own probe handler survived three applies, so -= only removed ours",
                        probeCalls == 0);
                    tip.Draw -= probe;
                }

                check("a null ToolTip is tolerated, like Apply's own null root",
                    SafeApplyToolTip(null));
            }
            finally
            {
                // Both statics are process-wide, and this fixture shares its process with fifteen
                // others under the harness. Restored in a finally rather than after the last check
                // so a throw out of any of them cannot leave a fake reader installed for whatever
                // runs next.
                SystemThemeReader.Current = savedReader;
                Theme.ResetForTests();
            }

            check("ResetForTests leaves Theme at Auto on the light palette",
                Theme.Mode == ThemeMode.Auto && Theme.Current != null && !Theme.Current.IsDark);
            check("the inert reader is back in place, so nothing after this fixture reads HKCU",
                SystemThemeReader.Current is UnknownSystemThemeReader &&
                SystemThemeReader.Current.TryGetAppsUseLightTheme() == null);

            lines.Add(string.Empty);
            lines.Add(string.Format("PASSED {0}/{1}", passed, total));
            lines.Add(string.Empty);
            lines.Add("No check here opens a window, applies a theme to a control, allocates a font, writes a");
            lines.Add("file or reads the registry: every ratio is arithmetic over two literals, and every Auto");
            lines.Add("case is driven through a fake reader - so this answers the same on a build agent as on a");
            lines.Add("machine running Windows in dark mode.");
            return lines;
        }

        /// <summary>
        /// The whole contrast matrix for one palette, emitted through the caller's own counter so
        /// both palettes land in one PASSED total.
        ///
        /// Every pair is checked through MeetsAa/MeetsNonText - the same predicates the palette was
        /// built against, not a second copy of 4.5 - and the measured ratio is printed on the line
        /// whether it passed or not. That is the difference between a failure that says "somebody
        /// brightened TextDisabled by two steps" and one that says only "TextDisabled is wrong now".
        ///
        /// Text colours are measured against all three backgrounds rather than the one they most
        /// often sit on, because that is the mistake the palette's own notes record: Fluent's
        /// #0078D4 passes on a white card at 4.53:1 and fails at 4.08:1 on the window background,
        /// which is where half the accent-coloured text in a settings dialog actually is.
        /// </summary>
        private static void AppendContrastChecks(Action<string, bool> check, string paletteName, ThemePalette palette)
        {
            Action<string, Color, string, Color> aa = (foregroundName, foreground, backgroundName, background) =>
            {
                check(DescribePair(paletteName, foregroundName, foreground, backgroundName, background,
                    ThemeContrast.AaTextRatio), ThemeContrast.MeetsAa(foreground, background));
            };
            Action<string, Color, string, Color> nonText = (foregroundName, foreground, backgroundName, background) =>
            {
                check(DescribePair(paletteName, foregroundName, foreground, backgroundName, background,
                    ThemeContrast.NonTextRatio), ThemeContrast.MeetsNonText(foreground, background));
            };

            string[] backgroundNames = new string[] { "WindowBackground", "SurfaceBackground", "ControlBackground" };
            Color[] backgrounds = new Color[]
            {
                palette.WindowBackground, palette.SurfaceBackground, palette.ControlBackground
            };

            for (int b = 0; b < backgrounds.Length; b++)
            {
                aa("TextPrimary", palette.TextPrimary, backgroundNames[b], backgrounds[b]);
                aa("TextSecondary", palette.TextSecondary, backgroundNames[b], backgrounds[b]);
                // Held to the text bar deliberately, and this is the one place the palette is
                // stricter than Windows itself: "disabled" in this application usually carries the
                // reason something cannot be done, and an unreadable explanation is no explanation.
                aa("TextDisabled", palette.TextDisabled, backgroundNames[b], backgrounds[b]);
                // Accent doubles as a text colour - links, the vibrance readout - so it is held to
                // the text bar on every background, not only against the AccentText it carries.
                aa("Accent", palette.Accent, backgroundNames[b], backgrounds[b]);
                // The status colours are read, so they are text, whatever they mark. All four are
                // measured on all three surfaces because the redesign paints them on more than the
                // one white ListView row the old markers lived in.
                aa("StatusGood", palette.StatusGood, backgroundNames[b], backgrounds[b]);
                aa("StatusBad", palette.StatusBad, backgroundNames[b], backgrounds[b]);
                aa("StatusWarning", palette.StatusWarning, backgroundNames[b], backgrounds[b]);
                aa("StatusSuppressed", palette.StatusSuppressed, backgroundNames[b], backgrounds[b]);
            }

            aa("ListText", palette.ListText, "ListBackground", palette.ListBackground);
            aa("StatusSuppressed", palette.StatusSuppressed, "ListBackground", palette.ListBackground);

            // All three accent fills, not only the resting one. Hover and pressed are where a
            // designer reaches for "a bit brighter" and takes the contrast with the text on top of
            // it down without noticing, because nobody screenshots a button mid-click.
            aa("AccentText", palette.AccentText, "Accent", palette.Accent);
            aa("AccentText", palette.AccentText, "AccentHover", palette.AccentHover);
            aa("AccentText", palette.AccentText, "AccentPressed", palette.AccentPressed);

            for (int b = 0; b < backgrounds.Length; b++)
            {
                // 3:1, the non-text bar, and the reason these are mid greys rather than the
                // #E5E5E5-class hairline a modern mock-up would use: at 1.26:1 on white that
                // hairline is decoration, and the border is what separates one group of controls
                // from the next.
                nonText("CardBorder", palette.CardBorder, backgroundNames[b], backgrounds[b]);
                nonText("Separator", palette.Separator, backgroundNames[b], backgrounds[b]);
                nonText("ControlBorder", palette.ControlBorder, backgroundNames[b], backgrounds[b]);
            }
        }

        /// <summary>
        /// One line per measured pair: both role names, both hex values, the ratio, and the bar it
        /// had to clear. InvariantCulture so the printed figure matches the ratios written down in
        /// ThemePalette's comments on a machine whose decimal separator is a comma.
        /// </summary>
        private static string DescribePair(string paletteName, string foregroundName, Color foreground,
            string backgroundName, Color background, double floor)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0} {1} {2} on {3} {4}  {5,6:0.00}:1  needs {6:0.0}:1",
                paletteName.PadRight(5),
                foregroundName.PadRight(16),
                Hex(foreground),
                backgroundName.PadRight(17),
                Hex(background),
                ThemeContrast.Ratio(foreground, background),
                floor);
        }

        /// <summary>
        /// An externally measured ratio, to within a hundredth. The tolerance is there because these
        /// figures come from contrast tools that round to three decimals, not because the arithmetic
        /// is expected to drift: a genuine difference in the transfer curve or in the channel
        /// weights moves these by far more than 0.01.
        /// </summary>
        private static void CheckRatio(Action<string, bool> check, string label, Color foreground, Color background, double expected)
        {
            double measured = ThemeContrast.Ratio(foreground, background);
            check(string.Format(CultureInfo.InvariantCulture, "{0}: measured {1:0.000}:1, expected {2:0.000}:1",
                label, measured, expected),
                Math.Abs(measured - expected) <= 0.01);
        }

        /// <summary>
        /// Every Color-typed property on ThemePalette, by reflection rather than from a list kept in
        /// this file: a colour added to the palette next year is then completeness-checked without
        /// anyone remembering to edit the fixture. The contrast matrix above still has to name its
        /// own pairs, because only a person can say which background a new colour is drawn on.
        /// </summary>
        private static List<PropertyInfo> ColourProperties()
        {
            List<PropertyInfo> colourProperties = new List<PropertyInfo>();
            foreach (PropertyInfo property in typeof(ThemePalette).GetProperties())
            {
                if (property.PropertyType == typeof(Color))
                {
                    colourProperties.Add(property);
                }
            }
            return colourProperties;
        }

        /// <summary>
        /// The first colour in this palette that could not be drawn with, or null if there is none.
        /// Returns a description rather than a bool so the failing line names the property: with 19
        /// of them, "a colour is missing" is most of the way to no information at all.
        /// </summary>
        private static string FirstUnusable(List<PropertyInfo> colourProperties, ThemePalette palette)
        {
            foreach (PropertyInfo property in colourProperties)
            {
                Color value = (Color)property.GetValue(palette, null);

                // Color.Empty and default(Color) are the same value - ARGB all zero - and it is what
                // a property the factory method forgot to assign holds. It is not the same as opaque
                // black, which has A=255, so a deliberate #000000 still passes here.
                if (value.IsEmpty || value.ToArgb() == 0)
                {
                    return property.Name + " is unassigned (Color.Empty) - the factory method skipped it";
                }

                // A translucent palette colour is worse than a wrong one: ThemeContrast ignores
                // alpha, so every ratio measured for it below would be a number about a colour that
                // is never actually painted.
                if (value.A != 255)
                {
                    return string.Format("{0} is translucent (A={1}), so any ratio measured for it is fiction",
                        property.Name, value.A);
                }
            }
            return null;
        }

        private static bool ValueIdentical(List<PropertyInfo> colourProperties, ThemePalette first, ThemePalette second)
        {
            if (first.IsDark != second.IsDark)
            {
                return false;
            }
            foreach (PropertyInfo property in colourProperties)
            {
                if (((Color)property.GetValue(first, null)).ToArgb() !=
                    ((Color)property.GetValue(second, null)).ToArgb())
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// The first colour value the two palettes have in common, named on both sides, or null when
        /// they share none.
        /// </summary>
        private static string FirstSharedValue(List<PropertyInfo> colourProperties, ThemePalette light, ThemePalette dark)
        {
            Dictionary<int, string> darkValues = new Dictionary<int, string>();
            foreach (PropertyInfo property in colourProperties)
            {
                int argb = ((Color)property.GetValue(dark, null)).ToArgb();
                if (!darkValues.ContainsKey(argb))
                {
                    darkValues[argb] = property.Name;
                }
            }
            foreach (PropertyInfo property in colourProperties)
            {
                Color value = (Color)property.GetValue(light, null);
                if (darkValues.ContainsKey(value.ToArgb()))
                {
                    return string.Format("light {0} and dark {1} are both {2}",
                        property.Name, darkValues[value.ToArgb()], Hex(value));
                }
            }
            return null;
        }

        private static string DescribeFlag(bool? appsUseLightTheme)
        {
            if (!appsUseLightTheme.HasValue)
            {
                return "null";
            }
            return appsUseLightTheme.Value ? "true" : "false";
        }

        private static string Hex(Color color)
        {
            return string.Format("#{0:X2}{1:X2}{2:X2}", color.R, color.G, color.B);
        }
    }
}
