using System.Drawing;

namespace vibrance.GUI.common.theme
{
    /// <summary>
    /// The 19 colours the redesigned forms are allowed to use, in a light and a dark variant. No
    /// form, control or helper picks a colour of its own: a second copy of "roughly this grey"
    /// somewhere in a Designer file is how a themed application ends up with one panel that never
    /// switches, and a colour that is not in here is a colour nobody ever measured.
    ///
    /// Every pair below was measured with ThemeContrast before it was written down, and the ratio
    /// quoted for each colour is the WORST of the four backgrounds in its own palette
    /// (WindowBackground, SurfaceBackground, ControlBackground, ListBackground), not the flattering
    /// one. Text is held to 4.5:1 (WCAG AA 1.4.3), borders and separators to 3:1 (1.4.11).
    ///
    ///   light                 hex       worst ratio  against
    ///   WindowBackground      #F3F3F3   -            (background)
    ///   SurfaceBackground     #FFFFFF   -            (background)
    ///   CardBorder            #838383   3.42         #F3F3F3
    ///   Separator             #858585   3.33         #F3F3F3
    ///   TextPrimary           #1B1B1B   15.52        #F3F3F3
    ///   TextSecondary         #575757   6.51         #F3F3F3
    ///   TextDisabled          #6B6B6B   4.80         #F3F3F3
    ///   Accent                #0067C0   5.11         #F3F3F3
    ///   AccentHover           #005CAB   6.73         AccentText on it
    ///   AccentPressed         #004E92   8.38         AccentText on it
    ///   AccentText            #FFFFFF   5.67         Accent under it
    ///   ControlBackground     #FFFFFF   -            (background)
    ///   ControlBorder         #767676   4.09         #F3F3F3
    ///   ListBackground        #FFFFFF   -            (background)
    ///   ListText              #1B1B1B   17.22        #FFFFFF
    ///   StatusGood            #107C10   4.84         #F3F3F3
    ///   StatusBad             #C42B1C   5.10         #F3F3F3
    ///   StatusWarning         #8A6100   4.99         #F3F3F3
    ///   StatusSuppressed      #B04800   5.02         #F3F3F3
    ///
    ///   dark                  hex       worst ratio  against
    ///   WindowBackground      #202020   -            (background)
    ///   SurfaceBackground     #2B2B2B   -            (background)
    ///   CardBorder            #8A8A8A   3.66         #333333
    ///   Separator             #818181   3.24         #333333
    ///   TextPrimary           #F2F2F2   11.29        #333333
    ///   TextSecondary         #C8C8C8   7.55         #333333
    ///   TextDisabled          #A0A0A0   4.83         #333333
    ///   Accent                #4CC2FF   6.30         #333333
    ///   AccentHover           #3CB4F5   6.79         AccentText on it
    ///   AccentPressed         #2EA2E3   5.55         AccentText on it
    ///   AccentText            #05253A   7.86         Accent under it
    ///   ControlBackground     #333333   -            (background)
    ///   ControlBorder         #969696   4.27         #333333
    ///   ListBackground        #262626   -            (background)
    ///   ListText              #F2F2F2   13.52        #262626
    ///   StatusGood            #6CCB6C   6.26         #333333
    ///   StatusBad             #FF99A4   6.22         #333333
    ///   StatusWarning         #FFC83D   8.17         #333333
    ///   StatusSuppressed      #FF9A4D   6.01         #333333
    ///
    /// What was deliberately not chosen:
    ///
    /// - Not pure black. A #000000 surface puts 18.76:1 under #F2F2F2 body text, which is more
    ///   contrast than legibility wants - large light-on-black glyphs bloom, and it is the one
    ///   background an OLED panel smears while scrolling. #202020/#2B2B2B is also where Windows 11
    ///   puts its own dark surfaces, so the app reads as native rather than as a terminal.
    /// - Not Color.Control. The old grey (#F0F0F0) is what every unstyled WinForms surface in this
    ///   application has always been; #F3F3F3 windows under #FFFFFF cards are the visible
    ///   difference between "themed" and "the theme failed to load".
    /// - Not #0078D4 in the light palette. The Fluent accent measures 4.53:1 on #FFFFFF, so it
    ///   passes on a white card and then fails at 4.08:1 on the #F3F3F3 window background - which
    ///   is where half the accent-coloured text in a settings dialog actually sits. #0067C0 is the
    ///   same blue, darker, and measures 5.11:1 against the worse of the two.
    /// </summary>
    public class ThemePalette
    {
        /// <summary>
        /// Which of the two this is. Consumers branch on this; nothing compares a colour against a
        /// literal to work out which palette it was handed.
        /// </summary>
        public bool IsDark { get; private set; }

        /// <summary>The form background - the largest area on screen, and what every card sits on.</summary>
        public Color WindowBackground { get; private set; }

        /// <summary>Raised areas: ThemedCard bodies, and anything that should read as on top.</summary>
        public Color SurfaceBackground { get; private set; }

        /// <summary>ThemedCard's one-pixel frame. Non-text, so 3:1 is the bar it has to clear.</summary>
        public Color CardBorder { get; private set; }

        /// <summary>Horizontal rules inside a card - CardBorder's job, one step quieter.</summary>
        public Color Separator { get; private set; }

        /// <summary>Labels, list text, anything the user has to read.</summary>
        public Color TextPrimary { get; private set; }

        /// <summary>Hints and secondary lines. Still held to 4.5:1, because it is still read.</summary>
        public Color TextSecondary { get; private set; }

        /// <summary>
        /// Disabled labels and greyed controls. Held to 4.5:1 too, which is the one place this
        /// palette is stricter than the platform: Windows 11 draws disabled text in #A2A2A2,
        /// which measures 2.55:1 on white, and WCAG exempts inactive controls outright. In this
        /// application, though, "disabled" routinely carries the reason something cannot be done
        /// - no second monitor, driver not initialised, HDR level not applicable - and an
        /// unreadable explanation is the same as no explanation at all.
        /// </summary>
        public Color TextDisabled { get; private set; }

        /// <summary>
        /// Default-button fill, selection, links. Doubles as a text colour on the two backgrounds,
        /// which is why it is dark enough (light palette) or bright enough (dark palette) to clear
        /// 4.5:1 against them, not only against the AccentText it carries.
        /// </summary>
        public Color Accent { get; private set; }

        /// <summary>Accent under the pointer. See AccentPressed for which direction it moves.</summary>
        public Color AccentHover { get; private set; }

        /// <summary>
        /// Accent while held. Hover and pressed both move AWAY from AccentText - darker in the light
        /// palette, and in the dark palette towards a deeper blue under near-black text - so state
        /// feedback can never cost contrast: all six accent fills clear 5.5:1 against their own
        /// AccentText, and the lowest of them is the dark palette's pressed state at 5.55:1.
        /// </summary>
        public Color AccentPressed { get; private set; }

        /// <summary>The only colour ever drawn on top of Accent/AccentHover/AccentPressed.</summary>
        public Color AccentText { get; private set; }

        /// <summary>Text boxes, combo boxes, numeric fields - the editable surfaces.</summary>
        public Color ControlBackground { get; private set; }

        /// <summary>
        /// The frame around an editable control. Stronger than CardBorder deliberately: a
        /// flat-drawn text box with no visible frame is indistinguishable from a label, and this
        /// border is what tells the user where they are allowed to type.
        /// </summary>
        public Color ControlBorder { get; private set; }

        /// <summary>The application-list ListView background.</summary>
        public Color ListBackground { get; private set; }

        /// <summary>The application-list ListView foreground.</summary>
        public Color ListText { get; private set; }

        /// <summary>"Applied", "detected", "driver initialised" - the good half of a readout.</summary>
        public Color StatusGood { get; private set; }

        /// <summary>"Failed", "not found", "driver missing".</summary>
        public Color StatusBad { get; private set; }

        /// <summary>"Not detected yet", "may be the wrong executable" - this app's "(?)" marker.</summary>
        public Color StatusWarning { get; private set; }

        /// <summary>"Switched off by the toggle hotkey" - this app's "(Off)" marker.</summary>
        public Color StatusSuppressed { get; private set; }

        // Built only through Light()/Dark(). A public constructor taking 19 colours positionally is
        // a silent colour swap waiting to happen, and there is no third palette to construct:
        // ThemeResolver turns every mode, Auto included, into one of these two.
        private ThemePalette()
        {
        }

        /// <summary>
        /// The light palette. #F3F3F3 window, #FFFFFF cards - Windows 11's own light layering,
        /// where the raised surface is the brighter one. That is the opposite way round from Dark()
        /// below, which is why the two share no colour values at all.
        /// </summary>
        public static ThemePalette Light()
        {
            ThemePalette palette = new ThemePalette();

            palette.IsDark = false;

            palette.WindowBackground = Color.FromArgb(0xF3, 0xF3, 0xF3);
            palette.SurfaceBackground = Color.FromArgb(0xFF, 0xFF, 0xFF);

            // Mid greys, not hairlines. #E5E5E5 is what a modern mock-up frames a card in, and it
            // measures 1.26:1 on white: decoration the user cannot rely on, when the border is what
            // separates one group of controls from the next. These clear 3:1 against the window
            // background, the darker and therefore worse of the two light surfaces.
            palette.CardBorder = Color.FromArgb(0x83, 0x83, 0x83);
            palette.Separator = Color.FromArgb(0x85, 0x85, 0x85);

            palette.TextPrimary = Color.FromArgb(0x1B, 0x1B, 0x1B);
            palette.TextSecondary = Color.FromArgb(0x57, 0x57, 0x57);
            palette.TextDisabled = Color.FromArgb(0x6B, 0x6B, 0x6B);

            palette.Accent = Color.FromArgb(0x00, 0x67, 0xC0);
            palette.AccentHover = Color.FromArgb(0x00, 0x5C, 0xAB);
            palette.AccentPressed = Color.FromArgb(0x00, 0x4E, 0x92);
            palette.AccentText = Color.FromArgb(0xFF, 0xFF, 0xFF);

            palette.ControlBackground = Color.FromArgb(0xFF, 0xFF, 0xFF);
            palette.ControlBorder = Color.FromArgb(0x76, 0x76, 0x76);

            palette.ListBackground = Color.FromArgb(0xFF, 0xFF, 0xFF);
            palette.ListText = Color.FromArgb(0x1B, 0x1B, 0x1B);

            // The status colours are the reason ThemeContrast exists, because the obvious choices
            // are all illegible and none of them look it:
            //   Color.Red        4.00:1 on #FFFFFF, 3.60:1 on #F3F3F3 - fails AA on both, by a
            //                    margin far too small for anyone to catch by eye.
            //   Color.DarkOrange 2.33:1 on #FFFFFF - and this is not hypothetical: it is the value
            //                    the "(Off)" marker shipped in (VibranceGUI.SuppressedForeColor).
            //   Color.Green      5.14:1 on #FFFFFF but 4.63:1 on #F3F3F3 - it barely survives being
            //                    moved off a white card, with no margin left for a darker window.
            // Each of the four below is that hue taken down until the WORSE of the two light
            // backgrounds clears 4.5:1. Measured ratios are quoted #F3F3F3 first, #FFFFFF second.
            palette.StatusGood = Color.FromArgb(0x10, 0x7C, 0x10);       // 4.84:1 / 5.37:1
            palette.StatusBad = Color.FromArgb(0xC4, 0x2B, 0x1C);        // 5.10:1 / 5.66:1
            palette.StatusWarning = Color.FromArgb(0x8A, 0x61, 0x00);    // 4.99:1 / 5.54:1
            // #C05000, the orange SuppressedForeColor shipped as, measures 4.78:1 - on the
            // ListView's white background, which was the only background it was ever drawn on. A
            // palette colour does not get that luxury: the same role now also paints over #F3F3F3,
            // where #C05000 measures 4.31:1 and fails. Darker, same burnt orange, 5.02:1 there.
            palette.StatusSuppressed = Color.FromArgb(0xB0, 0x48, 0x00); // 5.02:1 / 5.57:1

            return palette;
        }

        /// <summary>
        /// The dark palette. #202020 window under #2B2B2B cards, with #333333 for editable
        /// controls: the layers get LIGHTER as they come forward, the inverse of Light() above, and
        /// the reason ControlBackground rather than WindowBackground is the worst case every
        /// light-on-dark ratio in this file is quoted against.
        /// </summary>
        public static ThemePalette Dark()
        {
            ThemePalette palette = new ThemePalette();

            palette.IsDark = true;

            palette.WindowBackground = Color.FromArgb(0x20, 0x20, 0x20);
            palette.SurfaceBackground = Color.FromArgb(0x2B, 0x2B, 0x2B);

            palette.CardBorder = Color.FromArgb(0x8A, 0x8A, 0x8A);
            palette.Separator = Color.FromArgb(0x81, 0x81, 0x81);

            palette.TextPrimary = Color.FromArgb(0xF2, 0xF2, 0xF2);
            palette.TextSecondary = Color.FromArgb(0xC8, 0xC8, 0xC8);
            palette.TextDisabled = Color.FromArgb(0xA0, 0xA0, 0xA0);

            // Light blue carrying near-black text, the way Windows 11 inverts its accent for dark
            // mode - the same #0078D4 hue, not a different colour. A dark-blue fill under white
            // text would be barely brighter than the #2B2B2B card it sits on, and a primary button
            // that does not stand out from its own card has stopped being a primary button.
            palette.Accent = Color.FromArgb(0x4C, 0xC2, 0xFF);
            palette.AccentHover = Color.FromArgb(0x3C, 0xB4, 0xF5);
            palette.AccentPressed = Color.FromArgb(0x2E, 0xA2, 0xE3);
            palette.AccentText = Color.FromArgb(0x05, 0x25, 0x3A);

            palette.ControlBackground = Color.FromArgb(0x33, 0x33, 0x33);
            palette.ControlBorder = Color.FromArgb(0x96, 0x96, 0x96);

            palette.ListBackground = Color.FromArgb(0x26, 0x26, 0x26);
            palette.ListText = Color.FromArgb(0xF2, 0xF2, 0xF2);

            // Inverted, not reused. Every light status colour is a dark saturated shade chosen to
            // survive a white background, and on #333333 they collapse - the light StatusGood
            // #107C10 measures 2.35:1 there and the light StatusBad #C42B1C 2.23:1 - a dark red on
            // a dark grey is not "poor", it is unreadable. It does not work in the other
            // direction either, so neither palette can borrow from the other: the dark StatusGood
            // #6CCB6C measures 2.02:1 on white. These are the pastel counterparts, quoted against
            // #333333 first (the worst dark background) and the #2B2B2B card second.
            palette.StatusGood = Color.FromArgb(0x6C, 0xCB, 0x6C);       // 6.26:1 / 7.01:1
            palette.StatusBad = Color.FromArgb(0xFF, 0x99, 0xA4);        // 6.22:1 / 6.97:1
            palette.StatusWarning = Color.FromArgb(0xFF, 0xC8, 0x3D);    // 8.17:1 / 9.16:1
            palette.StatusSuppressed = Color.FromArgb(0xFF, 0x9A, 0x4D); // 6.01:1 / 6.73:1

            return palette;
        }
    }
}
