using System;
using System.Drawing;

namespace vibrance.GUI.common.theme
{
    /// <summary>
    /// WCAG 2.x relative luminance and contrast ratio over Color, and nothing else.
    ///
    /// This is its own type rather than three private helpers in ThemePalette because the palette
    /// and its fixture have to agree on the arithmetic: every colour in ThemePalette was picked
    /// against these functions, the ratio each pair measures is recorded in that file's comments,
    /// and the theme fixture re-measures all of them here at self-test time. A palette whose
    /// ratios live only in a comment drifts the first time somebody brightens one colour to taste,
    /// and the drift is invisible - this codebase already carries the evidence: the "(Off)" marker
    /// shipped in Color.DarkOrange, which measures 2.33:1 on the ListView's white background,
    /// until someone actually measured it (VibranceGUI.SuppressedForeColor's own comment).
    ///
    /// Alpha is ignored, deliberately. A translucent colour has no contrast ratio of its own -
    /// only its composite over whatever is behind it does, and that is not something this type is
    /// given. Every colour in ThemePalette is fully opaque, so nothing here composites anything.
    /// </summary>
    public static class ThemeContrast
    {
        /// <summary>
        /// WCAG 2.x AA for body text (1.4.3). Named constants rather than two magic numbers at the
        /// bottom of this file so the fixture can assert against the same threshold the palette was
        /// built to, not a second copy of it.
        /// </summary>
        public const double AaTextRatio = 4.5;

        /// <summary>
        /// WCAG 2.x AA for user-interface components and graphical objects (1.4.11) - borders,
        /// separators, focus rings: everything that has to be *seen* rather than *read*.
        /// </summary>
        public const double NonTextRatio = 3.0;

        /// <summary>
        /// Relative luminance, 0.0 (black) to 1.0 (white), per WCAG 2.x: each channel is
        /// linearised out of sRGB's transfer curve first, then weighted 0.2126/0.7152/0.0722.
        /// This is not brightness, lightness or HSL's L - those all rank colours differently, and
        /// a green that looks no brighter than a blue can carry four times its luminance.
        /// </summary>
        public static double RelativeLuminance(Color color)
        {
            return 0.2126 * Linearise(color.R) + 0.7152 * Linearise(color.G) + 0.0722 * Linearise(color.B);
        }

        /// <summary>
        /// The contrast ratio between two colours, 1.0 (identical) to 21.0 (black on white). The
        /// order of the arguments does not matter: the brighter of the two always goes on top, so
        /// Ratio(fg, bg) and Ratio(bg, fg) are the same number. That is a property of the WCAG
        /// definition, not a convenience - contrast is symmetric, and a "foreground" that happens
        /// to be the darker colour must not measure differently from the same pair read the other
        /// way round.
        /// </summary>
        public static double Ratio(Color first, Color second)
        {
            double firstLuminance = RelativeLuminance(first);
            double secondLuminance = RelativeLuminance(second);
            double lighter = Math.Max(firstLuminance, secondLuminance);
            double darker = Math.Min(firstLuminance, secondLuminance);

            return (lighter + 0.05) / (darker + 0.05);
        }

        /// <summary>
        /// Whether this pair is legible as body text at AA. Applied to every text/background pair
        /// ThemePalette defines, including the ones nobody thinks of as text - disabled labels and
        /// the status colours, which is exactly where the old palette failed.
        /// </summary>
        public static bool MeetsAa(Color foreground, Color background)
        {
            return Ratio(foreground, background) >= AaTextRatio;
        }

        /// <summary>
        /// Whether this pair is visible as a non-text element. The looser 3:1 bar is the reason the
        /// card borders in ThemePalette are mid greys rather than the near-invisible #E5E5E5-class
        /// hairlines a modern mock-up would use: at 1.26:1 against white, that hairline is decoration
        /// the user cannot rely on, and the border is what tells one group of controls from the next.
        /// </summary>
        public static bool MeetsNonText(Color foreground, Color background)
        {
            return Ratio(foreground, background) >= NonTextRatio;
        }

        private static double Linearise(byte channel)
        {
            double c = channel / 255.0;

            // 0.03928 is the figure WCAG 2.0 itself publishes; the sRGB specification's own
            // breakpoint is 0.04045, and contrast tools disagree about which to use. For 8-bit
            // channels the argument is empty: the two thresholds pick the same branch for all 256
            // values, because the boundary falls between channel 10 (10/255 = 0.039216, below
            // both) and channel 11 (11/255 = 0.043137, above both). Checked over the full range,
            // not reasoned about - zero values differ. So the WCAG wording is used verbatim and
            // the numbers here match any tool a reviewer measures a colour with.
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
    }
}
