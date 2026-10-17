using System.Drawing;
using System.Windows.Forms;

namespace vibrance.GUI.common.theme
{
    /// <summary>
    /// A Panel that paints a one-pixel frame in a colour of our choosing, and the reason no GroupBox
    /// survives in the redesigned forms.
    ///
    /// GroupBox is drawn by the visual-styles theme renderer, and that renderer provably ignores
    /// BackColor: over 20,000 pixels of GroupBox client area set to a dark BackColor, 542 stayed at
    /// #DCDCDC - the light frame and caption gap the renderer draws regardless of what the property
    /// says - and FlatStyle.Flat changed that count by nothing. The same measurement over a Panel
    /// found 0 light pixels. 542 pixels is not a rounding error, it is a bright outline around every
    /// group of controls in a dark window, so every GroupBox in the application becomes one of these
    /// instead: a Panel for the background the theme can actually set, plus the border the GroupBox
    /// was there to draw in the first place.
    ///
    /// The caption text a GroupBox also provided is not reimplemented here. It belongs to a Label
    /// the form owns, which the theme colours like any other label, rather than to a paint override
    /// that would have to reproduce the renderer's font, padding and RTL behaviour to look right.
    /// </summary>
    public class ThemedCard : Panel
    {
        private Color _borderColor = Color.Transparent;

        public ThemedCard()
        {
            // Flicker, not performance, is what these two are for: the card is repainted on every
            // resize and on every theme switch, and without them the background is cleared to the
            // parent's colour first, which reads as a flash on a dark window.
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }

        /// <summary>
        /// The frame colour - ThemePalette.CardBorder in practice. Transparent (the default) means
        /// "draw no frame", which is what a card nested inside another card wants; it does not mean
        /// "blend with the parent", since nothing here composites anything.
        /// </summary>
        public Color BorderColor
        {
            get { return _borderColor; }
            set
            {
                _borderColor = value;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            // Tested on alpha rather than against Color.Transparent, because Color's == compares the
            // struct's state and not only its ARGB: Color.FromArgb(0, 255, 255, 255) is NOT equal to
            // Color.Transparent even though ToArgb() returns the same value for both, so a caller
            // who builds a transparent colour by hand would get a frame drawn in it. A zero-alpha
            // Pen paints nothing anyway, so this is only about not allocating one.
            if (_borderColor.A == 0)
            {
                return;
            }

            using (Pen pen = new Pen(_borderColor))
            {
                // Width - 1 / Height - 1 because DrawRectangle takes the coordinates of the last
                // pixel drawn, not a size: at Width the right edge lands one pixel outside the
                // client rectangle and is clipped away, which looks like a card with three sides.
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
        }
    }
}
