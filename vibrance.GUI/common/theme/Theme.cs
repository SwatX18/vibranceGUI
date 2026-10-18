using System;
using System.Drawing;
using System.Windows.Forms;

namespace vibrance.GUI.common.theme
{
    /// <summary>
    /// What a label's colour MEANS, as opposed to which colour it happens to be right now. A label
    /// is given one of these through Theme.SetTextRole, and Theme.Apply's Label branch resolves it
    /// against whichever palette is in force - which is the entire point of the indirection. The
    /// two palettes share no status colour and cannot: on ThemePalette's own measurements the light
    /// StatusBad #C42B1C collapses to 2.23:1 on the dark control background, and the dark StatusGood
    /// #6CCB6C to 2.02:1 on white. So "this line is bad news" cannot be recorded as a Color and
    /// still be right after the user switches themes.
    ///
    /// Five values: the four semantic roles this application assigns to a Label, and Primary for the
    /// ordinary case a label with no role at all also gets. There is no StatusSuppressed because
    /// that colour only ever lands on a ListViewItem, which is not a Control and so is out of reach
    /// of Apply's walk entirely - VibranceGUI's RefreshApplicationListItemAppearances owns those -
    /// and no Disabled because nothing in the application writes TextDisabled to a label.
    /// </summary>
    public enum ThemeTextRole
    {
        /// <summary>Body text. What a Label carrying no role at all is coloured, as before.</summary>
        Primary = 0,

        /// <summary>Hints and secondary lines. TextSecondary, which the fixture holds to 4.5:1.</summary>
        Secondary,

        /// <summary>"Running!", "Hotkey registered.", "Added 3 games."</summary>
        StatusGood,

        /// <summary>"No modifier: steals the key from the game."</summary>
        StatusWarning,

        /// <summary>"Already in use by another application.", "Closing...".</summary>
        StatusBad
    }

    /// <summary>
    /// The one thing the forms actually talk to: the resolved palette, four fonts, and a recursive
    /// walk that colours an existing control tree in place.
    ///
    /// Static, like LogSink and ReleaseSource, and for the same reason: the first thing painted is
    /// GraphicsAdapterChooser, which Program.Main constructs before a VibranceGUI exists, so there
    /// is nothing to hang an instance off. Program.Main calls Initialize once, ahead of every form
    /// constructor, and every form's constructor ends in Theme.Apply(this).
    ///
    /// Apply deliberately does NOT assign any of the four fonts to any control. The five designer
    /// files are authored at 144 DPI with AutoScaleMode.Dpi and their control bounds are fixed
    /// pixels, so replacing a control's Font reflows text inside a box that does not grow with it -
    /// the way a longer label ends up clipped rather than wrapped. The fonts are exposed for the
    /// layout work that rebuilds those forms and sizes its own controls; nothing here applies them
    /// to controls somebody else laid out.
    /// </summary>
    public static class Theme
    {
        /// <summary>
        /// Multipliers on SystemFonts.MessageBoxFont's point size rather than absolute point sizes,
        /// so the whole ramp follows a user who has raised the system font instead of staying at 9pt
        /// while every other window grows. At the usual 9pt / 12px base that gives a 24px hero and a
        /// 10.8px secondary line - roughly Windows 11's own Subtitle and Caption steps, which is
        /// where the two were aimed.
        /// </summary>
        private const float HeroScale = 2.0f;
        private const float SecondaryScale = 0.9f;

        private static ThemeMode _mode = ThemeMode.Auto;

        // Never null, so no caller has to null-check Current. Light() is the same answer Auto
        // resolves to under the default UnknownSystemThemeReader (ThemeResolver.ResolveIsDark), so
        // this initial value is not a third state - it is what Initialize would have produced had it
        // been called, which matters because a form constructed by a fixture never calls it.
        private static ThemePalette _current = ThemePalette.Light();

        private static Font _baseFont;
        private static Font _captionFont;
        private static Font _heroFont;
        private static Font _secondaryFont;

        /// <summary>The resolved palette - one of ThemePalette.Light()/Dark(), never null.</summary>
        public static ThemePalette Current
        {
            get { return _current; }
        }

        /// <summary>
        /// What the user asked for, which is not the same question as Current.IsDark: Auto can
        /// resolve either way. Persisted by SettingsController.SetThemeMode, so the settings UI
        /// reads this to decide which option is selected, never Current.IsDark.
        /// </summary>
        public static ThemeMode Mode
        {
            get { return _mode; }
        }

        /// <summary>Body text - the same family and size WinForms would have used anyway.</summary>
        public static Font BaseFont
        {
            get { EnsureFonts(); return _baseFont; }
        }

        /// <summary>
        /// Card headings. Bold at the base size rather than a size larger: the cards are only a few
        /// hundred pixels wide and a heading a step up in size costs a line of height each, where
        /// weight alone separates the heading from its contents for free. This is what Windows 11
        /// calls "body strong".
        /// </summary>
        public static Font CaptionFont
        {
            get { EnsureFonts(); return _captionFont; }
        }

        /// <summary>The Windows/ingame vibrance number, the one value read from across a desk.</summary>
        public static Font HeroFont
        {
            get { EnsureFonts(); return _heroFont; }
        }

        /// <summary>
        /// Hints and secondary lines, paired with ThemePalette.TextSecondary. Small, but still held
        /// to the palette's 4.5:1 - for the reason written on TextDisabled, that a quiet line in
        /// this application usually carries the reason something is unavailable.
        /// </summary>
        public static Font SecondaryFont
        {
            get { EnsureFonts(); return _secondaryFont; }
        }

        /// <summary>
        /// Called once from Program.Main with the INI's stored mode, before any form is constructed.
        /// The Windows flag is read here and only here on the startup path, through
        /// SystemThemeReader.Current - which is the inert UnknownSystemThemeReader unless Main
        /// installed the registry one, so a self-test run resolves Auto to light on every machine.
        /// </summary>
        public static void Initialize(ThemeMode mode)
        {
            _mode = mode;
            _current = Resolve(mode);
        }

        /// <summary>
        /// The settings UI's entry point. Returns true when IsDark actually flipped, which is the
        /// only case worth a repaint: switching Auto to Light on a machine whose Windows is already
        /// light changes the persisted mode and nothing on screen, and the caller can skip walking
        /// five control trees for it.
        /// </summary>
        public static bool SetMode(ThemeMode mode)
        {
            bool wasDark = _current.IsDark;
            _mode = mode;
            _current = Resolve(mode);
            return _current.IsDark != wasDark;
        }

        /// <summary>
        /// Re-reads the Windows flag - for a WM_SETTINGCHANGE, or a poll noticing the user flipped
        /// Windows into dark mode while vibranceGUI sat in the tray. RegistrySystemThemeReader
        /// caches nothing, so this always sees the current value.
        ///
        /// Returns true only when IsDark flipped, and leaves _current pointing at the OLD instance
        /// when it did not. That is not an optimisation for the palette's sake - the two factory
        /// methods are cheap - it is so this can be called repeatedly without the caller having to
        /// work out for itself whether anything changed. The palettes are immutable and
        /// value-identical for a given IsDark, so keeping the old instance is indistinguishable from
        /// swapping in a fresh one, minus the repaint the caller would then think it owed the user.
        /// </summary>
        public static bool RefreshFromSystem()
        {
            ThemePalette resolved = Resolve(_mode);
            if (resolved.IsDark == _current.IsDark)
            {
                return false;
            }
            _current = resolved;
            return true;
        }

        /// <summary>
        /// Colours an existing control tree in place, recursively.
        ///
        /// THE FOREGROUND RULE, which outranks every visual concern in this file: WinEventHook's
        /// constructor (WinEventHook.cs, SetWinEventHook with idProcess 0 and idThread 0) hooks
        /// EVENT_SYSTEM_FOREGROUND for EVERY process on the machine, not only for games. If
        /// vibranceGUI's own window takes the foreground, that hook fires, the matcher looks for a
        /// configured game in the newly-foreground window, finds vibranceGUI instead, and reverts
        /// the user's vibrance - mid-match, in the middle of a round. So nothing in this file, and
        /// nothing it calls, may ever Activate, Show, ShowDialog, BringToFront, Focus, Select,
        /// SetForegroundWindow, SetWindowPos, Application.DoEvents, set Form.Visible = true, or
        /// recreate a control handle. Set properties, then Invalidate(true), inside
        /// SuspendLayout/ResumeLayout - that is the whole permitted vocabulary, and the two
        /// properties that would break the last of those rules (TextBox.BorderStyle,
        /// TrackBar.TickStyle) are gated on IsHandleCreated below rather than written blind.
        ///
        /// The entire body is wrapped in a catch-all. A theme is decoration; a colour that could not
        /// be assigned must never be the reason vibranceGUI fails to start, and this runs as the
        /// last statement of five different constructors, where an escaping exception takes the
        /// whole form down with it.
        /// </summary>
        public static void Apply(Control root)
        {
            if (root == null)
            {
                return;
            }

            try
            {
                ApplyRecursive(root);

                // Once, at the root, with invalidateChildren: true - rather than per control on the
                // way down, which would queue one WM_PAINT per node for the same repaint. A no-op on
                // the constructor path, since Control.Invalidate does nothing before the handle
                // exists, and that is exactly right: there is nothing on screen to repaint yet, and
                // the first paint will use the colours just assigned.
                root.Invalidate(true);
            }
            catch (Exception ex)
            {
                // Logged through the same facade as the rest of the codebase. No dependency cycle to
                // worry about: this is one assembly, VibranceGUI.Log forwards to LogSink.Current,
                // and that default is NullLogSink - so a fixture that reaches this line writes
                // nothing under %APPDATA% and a normal run gets the real log entry.
                VibranceGUI.Log(ex);
            }
        }

        /// <summary>
        /// A ToolTip's two colours, for the same reason ApplyThemeToThisWindow passes the
        /// ContextMenuStrip to Apply by hand: a ToolTip is a Component and not a Control, so it sits
        /// in the form's `components` container and no walk over Controls can ever reach one. Both
        /// forms that own a ToolTip call this beside their Theme.Apply.
        ///
        /// OwnerDraw has to go true for BackColor and ForeColor to be read at all - assigned alone
        /// they are ignored, and the tip renders in SystemColors.Info regardless, which is the state
        /// this method exists to fix: a light tip over a dark window.
        ///
        /// IsBalloon has to go FALSE for the same reason, and that is the one visible trade this
        /// method makes rather than a detail. OwnerDraw is documented as ignored while IsBalloon is
        /// true, so the balloon shape and a themed tip are mutually exclusive: there is no setting
        /// that keeps the rounded balloon and lets us colour it. Both ToolTips in this application
        /// were authored with IsBalloon = true, so a dark theme costs them that shape. A square tip
        /// in the palette's own colours was judged the better of the two, because the balloon's
        /// light body is unreadable rather than merely unfamiliar - but it is a judgement, it
        /// applies in light mode too, and reverting it is one line here.
        ///
        /// The handler is removed before it is added. This runs again on every live theme switch,
        /// and += alone would stack a second, third and fourth identical subscription onto the same
        /// ToolTip - each one painting over the last, for as long as the window is open.
        /// </summary>
        public static void ApplyToToolTip(ToolTip tip)
        {
            if (tip == null)
            {
                return;
            }

            try
            {
                ThemePalette palette = Current;
                tip.IsBalloon = false;
                tip.OwnerDraw = true;
                tip.BackColor = palette.ControlBackground;
                tip.ForeColor = palette.TextPrimary;
                tip.Draw -= DrawThemedToolTip;
                tip.Draw += DrawThemedToolTip;
            }
            catch (Exception ex)
            {
                // Same contract as Apply above: a tooltip colour is never worth a failed startup.
                VibranceGUI.Log(ex);
            }
        }

        /// <summary>
        /// The three calls OwnerDraw hands us, in the only order that works: background, then the
        /// border over its edge, then the text over both. DrawBackground and DrawText read the
        /// ToolTip's own BackColor and ForeColor - the pair ApplyToToolTip just assigned - so the
        /// palette reaches the tip through the component rather than being captured here, and a
        /// theme switch that re-runs ApplyToToolTip needs no new handler.
        ///
        /// WordBreak because the longest tip in this application is the NVIDIA scaling explanation
        /// at over 200 characters; without it the tip renders as one line running off the screen.
        /// </summary>
        private static void DrawThemedToolTip(object sender, DrawToolTipEventArgs e)
        {
            e.DrawBackground();
            e.DrawBorder();
            e.DrawText(TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }

        /// <summary>
        /// Records what a label's colour means, and assigns that colour from the palette in force.
        ///
        /// Two jobs in one call, deliberately. The assignment is for the caller: a status line that
        /// changes while the form is live has to show its new colour now, and Apply will not run
        /// again until the theme changes. The record is for Apply: the role outlives this call, so
        /// when the theme does change, the walk re-resolves the same role against the new palette
        /// instead of flattening the label to TextPrimary.
        ///
        /// The role is kept in Control.Tag, because there is nowhere else to hang per-control state
        /// on a stock Label without subclassing it, and Tag is free on every Control in this
        /// application: every other Tag it reads or writes belongs to a ListViewItem (the four lists'
        /// items, and ListItemAppearance's own Tag field), and a ListViewItem is not a Control.
        /// ReadTextRole type-checks what it finds rather than casting it, so a control whose Tag is
        /// later used for something else reads back as Primary and is coloured like any other label
        /// - it does not throw out of the middle of Apply's walk.
        ///
        /// Nothing here is on the forbidden list in Apply's comment: assigning ForeColor invalidates
        /// the control by itself, which is the whole repaint this owes and all it is allowed.
        /// </summary>
        // Label, not Control, and that is load-bearing rather than tidiness. Only the Label
        // branch of Apply reads the role back; CheckBox, RadioButton and Button assign
        // TextPrimary directly. So SetTextRole(someCheckBox, StatusBad) would colour it once
        // and have the very next Apply flatten it - which is exactly the bug this enum was
        // introduced to fix, reintroduced for a different control type. Narrowing the
        // parameter makes that a compile error instead of a silent repaint. Widen it only
        // together with reading the role in those other branches.
        public static void SetTextRole(Label control, ThemeTextRole role)
        {
            if (control == null)
            {
                return;
            }

            control.Tag = role;
            control.ForeColor = ResolveTextRole(role, _current);
        }

        /// <summary>
        /// Back to the startup state: Auto, the light palette, no fonts allocated.
        ///
        /// Deliberately does not consult SystemThemeReader.Current, even though Auto would normally
        /// be resolved through it. A fixture resets both statics and must not have to care which
        /// order it does that in - resolving here would make this one's result depend on whether the
        /// reader had already been put back to UnknownSystemThemeReader.
        ///
        /// This is also the only place the four fonts are ever disposed, and it is safe precisely
        /// because Apply never assigns a font to a control: no live control can be holding one, so
        /// there is no disposed handle to fault on the next paint. In a normal run the fonts are
        /// built once and live for the process, which is four GDI+ handles and is the point - a font
        /// rebuilt per form would instead be four handles leaked per form.
        /// </summary>
        public static void ResetForTests()
        {
            _mode = ThemeMode.Auto;
            _current = ThemePalette.Light();
            DisposeFonts();
        }

        private static ThemePalette Resolve(ThemeMode mode)
        {
            bool isDark = ThemeResolver.ResolveIsDark(mode, SystemThemeReader.Current.TryGetAppsUseLightTheme());
            return isDark ? ThemePalette.Dark() : ThemePalette.Light();
        }

        /// <summary>
        /// Built on first read, not in Initialize, so a fixture that only checks palette resolution
        /// never allocates a GDI+ font.
        ///
        /// Derived from SystemFonts.MessageBoxFont, never from a hardcoded "Segoe UI". That property
        /// is Segoe UI 9pt on everything from Windows 10 on and degrades to Tahoma 8pt on the older
        /// installs this application still runs on; a hardcoded family that is missing does not fail
        /// loudly, GDI+ substitutes Microsoft Sans Serif silently, and the window ends up looking
        /// like a 2001 dialog with nothing anywhere to explain why.
        ///
        /// MessageBoxFont hands back a new Font the caller owns, so it is disposed here once its
        /// family name and point size have been copied out. The name, not the FontFamily object, so
        /// there is no question left about whether disposing the source invalidates it.
        /// </summary>
        private static void EnsureFonts()
        {
            if (_baseFont != null)
            {
                return;
            }

            Font source = SystemFonts.MessageBoxFont;

            // Documented as able to return null when the metric is unavailable. Control.DefaultFont
            // is the framework's own fallback for exactly that, and is a shared instance - hence the
            // flag, because disposing it below would break every control in the process.
            bool sourceIsOwned = source != null;
            if (source == null)
            {
                source = Control.DefaultFont;
            }

            try
            {
                string family = source.Name;
                float size = source.SizeInPoints;

                // All four built into locals and published together, so a throw on the third leaves
                // every field null and the next read retries. Publishing as they are constructed
                // would set _baseFont, fail, and then satisfy the early-out above forever - three
                // null fonts that no later call would ever rebuild.
                Font baseFont = new Font(family, size, FontStyle.Regular, GraphicsUnit.Point);
                Font captionFont = new Font(family, size, FontStyle.Bold, GraphicsUnit.Point);
                Font heroFont = new Font(family, size * HeroScale, FontStyle.Regular, GraphicsUnit.Point);
                Font secondaryFont = new Font(family, size * SecondaryScale, FontStyle.Regular, GraphicsUnit.Point);

                _captionFont = captionFont;
                _heroFont = heroFont;
                _secondaryFont = secondaryFont;
                _baseFont = baseFont;
            }
            finally
            {
                if (sourceIsOwned)
                {
                    source.Dispose();
                }
            }
        }

        private static void DisposeFonts()
        {
            DisposeFont(ref _baseFont);
            DisposeFont(ref _captionFont);
            DisposeFont(ref _heroFont);
            DisposeFont(ref _secondaryFont);
        }

        private static void DisposeFont(ref Font font)
        {
            if (font != null)
            {
                font.Dispose();
                font = null;
            }
        }

        /// <summary>
        /// SuspendLayout per control rather than once at the root, because WinForms layout
        /// suspension is per-control and is not inherited: suspending the form leaves a card's own
        /// layout engine live, and FlatStyle on an AutoSize Button inside it then runs a layout pass
        /// through that card for every button. try/finally around the descent because an unbalanced
        /// SuspendLayout does not throw, it silently freezes that container's layout for the rest of
        /// the process - a far worse outcome than the wrong colour Apply's catch-all is there for.
        ///
        /// ResumeLayout(false), the same overload the designer files themselves use: no layout pass
        /// is owed here. Nothing below changes a control's bounds, and the two properties that could
        /// affect a preferred size are not written once a handle exists.
        /// </summary>
        private static void ApplyRecursive(Control control)
        {
            control.SuspendLayout();
            try
            {
                ApplyToControl(control);

                foreach (Control child in control.Controls)
                {
                    ApplyRecursive(child);
                }

                // A ContextMenuStrip is not in any Controls collection, so the walk above can never
                // reach one; a control's own ContextMenuStrip property is the only route there is.
                // Note what this still misses: the tray menu (VibranceGUI.Designer.cs) hangs off
                // NotifyIcon.ContextMenuStrip, and a NotifyIcon is a Component, not a Control - it
                // is not reachable from any walk over a control tree, and has to be handed to Apply
                // by the form that owns it.
                //
                // Not followed off a ToolStrip, which closes the only cycle this walk could have: a
                // ContextMenuStrip is itself a ToolStrip, so a menu whose own ContextMenuStrip
                // pointed back at itself would recurse until the stack ran out - and a
                // StackOverflowException is the one failure Apply's catch-all cannot absorb, because
                // the CLR does not let anything catch it. Nothing in this application sets that up,
                // but "a colour scheme can kill the process" is not a risk worth carrying for a
                // property no ToolStrip has a reason to use.
                if (!(control is ToolStrip) && control.ContextMenuStrip != null)
                {
                    ApplyRecursive(control.ContextMenuStrip);
                }
            }
            finally
            {
                control.ResumeLayout(false);
            }
        }

        /// <summary>
        /// One control, no recursion. Ordered most-derived first, because every test here is an
        /// "is": ThemedCard before Panel, and LinkLabel before Label, are not stylistic - they are
        /// what decides which branch a card and a link actually land in.
        /// </summary>
        private static void ApplyToControl(Control control)
        {
            ThemePalette palette = _current;

            Form form = control as Form;
            if (form != null)
            {
                form.BackColor = palette.WindowBackground;
                form.ForeColor = palette.TextPrimary;
                return;
            }

            ThemedCard card = control as ThemedCard;
            if (card != null)
            {
                card.BackColor = palette.SurfaceBackground;
                card.BorderColor = palette.CardBorder;
                return;
            }

            // MenuStrip, ContextMenuStrip and ToolStrip in one branch - all three derive from
            // ToolStrip and want the same two colours, plus one pass over the items.
            //
            // BackColor DOES reach the drop-down surface, measured on this application's own tray
            // menu: a 240x60 ContextMenuStrip rendered with DrawToBitmap has 8256 of its 14400
            // pixels light (57.3%) at the designer defaults and 1266 of 14400 (8.8%) once BackColor
            // and ForeColor are assigned here. What is still missing is a custom ToolStripRenderer -
            // the default ToolStripProfessionalRenderer draws the image margin, the hover highlight
            // and the separators from its own ColorTable, and nothing assignable here reaches any of
            // them, so the residual 1266 pixels were not chased past that. It stays a knowing gap
            // rather than an oversight: a renderer subclass plus a ProfessionalColorTable is a lot of
            // surface to own for a two-item tray menu. Worth revisiting if a real menu bar appears.
            //
            // That the surface really does go dark is exactly why ApplyToToolStrip below has to
            // assign every item's ForeColor too. The tray link shipped with #0645AD hardcoded on the
            // item in the designer file, which measures 8.53:1 against the light strip (#FFFFFF) and
            // 1.66:1 against the dark one (#2B2B2B): effectively invisible, and invisible only in
            // the theme that did not exist when that colour was chosen.
            ToolStrip strip = control as ToolStrip;
            if (strip != null)
            {
                ApplyToToolStrip(strip, palette);
                return;
            }

            // GroupBox is deliberately left exactly as the designer left it, and no GroupBox should
            // survive the layout work - every one of them becomes a ThemedCard plus a Label. The
            // reason is measured and is written up on ThemedCard: the visual-styles renderer draws
            // the frame and caption gap regardless of BackColor, so of 20,000 pixels of client area
            // set to a dark background, 542 stayed #DCDCDC, and FlatStyle.Flat changed that count by
            // nothing. Setting BackColor here would therefore not darken a GroupBox; it would only
            // hide those 542 bright pixels behind a property that looks like it worked.
            if (control is GroupBox)
            {
                return;
            }

            Panel panel = control as Panel;
            if (panel != null)
            {
                panel.BackColor = palette.SurfaceBackground;
                return;
            }

            Button button = control as Button;
            if (button != null)
            {
                // UseVisualStyleBackColor has to go false as well as FlatStyle going Flat. Left
                // true, the button is painted by the visual-styles renderer, which draws the
                // system's own light gradient and never reads BackColor - the same trap GroupBox
                // above cannot be dug out of.
                button.FlatStyle = FlatStyle.Flat;
                button.UseVisualStyleBackColor = false;
                button.BackColor = palette.ControlBackground;
                button.ForeColor = palette.TextPrimary;
                button.FlatAppearance.BorderColor = palette.ControlBorder;
                button.FlatAppearance.MouseOverBackColor = palette.AccentHover;
                button.FlatAppearance.MouseDownBackColor = palette.AccentPressed;
                return;
            }

            CheckBox checkBox = control as CheckBox;
            if (checkBox != null)
            {
                checkBox.FlatStyle = GlyphFlatStyle(palette);
                ResetToParentBackground(checkBox);
                checkBox.ForeColor = palette.TextPrimary;
                return;
            }

            RadioButton radioButton = control as RadioButton;
            if (radioButton != null)
            {
                radioButton.FlatStyle = GlyphFlatStyle(palette);
                ResetToParentBackground(radioButton);
                radioButton.ForeColor = palette.TextPrimary;
                return;
            }

            TextBox textBox = control as TextBox;
            if (textBox != null)
            {
                textBox.BackColor = palette.ControlBackground;
                textBox.ForeColor = palette.TextPrimary;

                // The one property in this file that has to be gated on the handle. Fixed3D - the
                // designer default - is drawn by the system as a sunken light bevel and stays light
                // over a dark control background, so FixedSingle is what makes a dark text box look
                // deliberate rather than broken. But TextBoxBase.BorderStyle calls RecreateHandle(),
                // which the foreground rule in Apply's comment forbids outright. RecreateHandle is
                // itself a no-op before the handle exists, so on the only path that matters - Apply
                // as the last line of a constructor, before the form is ever shown - this is a plain
                // property write and always runs. A later re-apply for a live theme switch skips it
                // and leaves the bevel: a light frame around a dark box is a cosmetic wart, and
                // tearing down and rebuilding a window while a game may be in the foreground is not
                // a trade worth making for it.
                if (!textBox.IsHandleCreated && textBox.BorderStyle != BorderStyle.FixedSingle)
                {
                    textBox.BorderStyle = BorderStyle.FixedSingle;
                }
                return;
            }

            ComboBox comboBox = control as ComboBox;
            if (comboBox != null)
            {
                comboBox.BackColor = palette.ControlBackground;
                comboBox.ForeColor = palette.TextPrimary;
                comboBox.FlatStyle = FlatStyle.Flat;

                // DropDownStyle is NOT touched, and must not be. The editable DropDown the
                // resolution picker already uses leaves 1023 of 4893 pixels light under the dark
                // palette - the frame and the drop button, which the system draws itself; the text
                // area does take BackColor. Switching to DropDownList to unlock DrawMode.OwnerDraw
                // measured 3968 of 4893 light, four times worse, because a DropDownList paints its
                // whole face through the system renderer. It would also recreate the handle
                // (ComboBox.DropDownStyle calls RecreateHandle), which the foreground rule forbids,
                // and it would change the control's behaviour - the list becomes read-only - for a
                // theme's benefit. 1023 pixels of system chrome is the accepted cost here.
                return;
            }

            ListView listView = control as ListView;
            if (listView != null)
            {
                // Column headers are not included, and cannot be from here: the header is a child
                // window the common control owns, it ignores the ListView's colours, and the only
                // way at it is OwnerDraw plus a DrawColumnHeader handler on each of the four lists -
                // which belongs to those forms, not to a generic walk.
                listView.BackColor = palette.ListBackground;
                listView.ForeColor = palette.ListText;
                return;
            }

            TrackBar trackBar = control as TrackBar;
            if (trackBar != null)
            {
                trackBar.BackColor = palette.SurfaceBackground;

                // Gated for the same reason as TextBox.BorderStyle above: TrackBar.TickStyle maps to
                // the TBS_* window style bits, and its setter calls RecreateHandle(). The ticks go
                // because the common control draws them in a fixed system colour that is invisible
                // on a dark surface, and a row of ticks nobody can see is only noise around the
                // slider. Left alone on a live switch, which costs nothing either way.
                if (!trackBar.IsHandleCreated && trackBar.TickStyle != TickStyle.None)
                {
                    trackBar.TickStyle = TickStyle.None;
                }
                return;
            }

            LinkLabel linkLabel = control as LinkLabel;
            if (linkLabel != null)
            {
                // All three link states get the same Accent. Visited especially: LinkLabel's default
                // visited purple is picked for a document, and the two links here (the Twitter link
                // and the driver-uninstaller link) are buttons that happen to look like links - a
                // user who clicked one last week should not find it a different colour from the one
                // beside it. ActiveLinkColor matching is the same argument.
                linkLabel.LinkColor = palette.Accent;
                linkLabel.ActiveLinkColor = palette.Accent;
                linkLabel.VisitedLinkColor = palette.Accent;
                ResetToParentBackground(linkLabel);
                return;
            }

            Label label = control as Label;
            if (label != null)
            {
                // TextPrimary is the default here, not the rule. This branch used to assign it to
                // every Label unconditionally, and that quietly destroyed every semantic label
                // colour in the application: "Running!" came back as body text after a theme
                // switch, a hotkey conflict stopped being red, and the three secondary hints
                // (labelSkipped, labelNote, labelDdu) were overwritten before they were ever
                // painted - by the Theme.Apply at the end of the very constructor whose designer
                // had just coloured them. Resolving the role the label carries is what survives
                // both: see SetTextRole for how a role gets there, and ResolveTextRole for the one
                // place a role becomes a colour.
                label.ForeColor = ResolveTextRole(ReadTextRole(label), palette);

                // Cleared rather than set, so the label takes whatever surface it is actually on -
                // WindowBackground on the form, SurfaceBackground inside a card - without this
                // branch having to know which. A designer file that pinned a label's BackColor to
                // Control is exactly the case this undoes.
                ResetToParentBackground(label);
                return;
            }

            // Everything else is left alone on purpose, PictureBox included: the single PictureBox in
            // the application shows an extracted game icon on a BackColor that is already
            // Transparent, so it inherits the surface behind it with nothing assigned here.
        }

        /// <summary>
        /// The strip's own two colours, then every item's ForeColor explicitly.
        ///
        /// The per-item pass is not redundant with strip.ForeColor, and that is the trap this branch
        /// was in: ToolStripItem.ForeColor returns the item's OWN value and only walks up to the
        /// owning strip when that value is Color.Empty. A colour hardcoded on an item in a designer
        /// file is therefore unreachable from the strip - there is no property on the parent that
        /// clears it - so assigning every item is the only way the theme wins.
        ///
        /// The link item is identified by its Text being a URL, not by field name. That is what
        /// makes the row a link in the first place: twitterToolStripTextBox's Text IS the address
        /// its Click handler opens (VibranceGUI.TwitterLink, the same string), and the test keeps
        /// working through the rename that a name comparison in this file would silently turn into
        /// body text. Both ways of missing fail safe - a miss is TextPrimary, which the palette
        /// holds to 11.29:1 dark and 15.52:1 light against the worst background in its own palette,
        /// never an unreadable colour. The hit is Accent: 7.06:1 on the dark strip (#4CC2FF on
        /// #2B2B2B), 5.67:1 on the light one (#0067C0 on #FFFFFF), against the 1.66:1 the hardcoded
        /// #0645AD managed in dark.
        ///
        /// Recurses through a drop-down item's own items, so the day a submenu appears in this
        /// application it is not silently missed. Gated on HasDropDownItems rather than reading
        /// DropDownItems straight off, because that property creates the ToolStripDropDown on
        /// demand: walking it blind would allocate a drop-down for every plain item in the strip.
        /// This cannot recurse for ever the way ApplyRecursive's ContextMenuStrip note warns about -
        /// a ToolStripItem has one Owner and joining a collection removes it from the previous one,
        /// so what this walks is a tree.
        /// </summary>
        private static void ApplyToToolStrip(ToolStrip strip, ThemePalette palette)
        {
            strip.BackColor = palette.SurfaceBackground;
            strip.ForeColor = palette.TextPrimary;

            foreach (ToolStripItem item in strip.Items)
            {
                item.ForeColor = IsLinkText(item.Text) ? palette.Accent : palette.TextPrimary;

                ToolStripDropDownItem dropDownItem = item as ToolStripDropDownItem;
                if (dropDownItem != null && dropDownItem.HasDropDownItems)
                {
                    ApplyToToolStrip(dropDownItem.DropDown, palette);
                }
            }
        }

        /// <summary>
        /// Whether a menu row's label is itself a web address. Ordinal, because a scheme prefix is
        /// not prose and this must not have a different answer on a different machine's culture; and
        /// case-insensitive, because "HTTPS://" is the same address and the same link.
        /// </summary>
        private static bool IsLinkText(string text)
        {
            return text != null &&
                   (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    text.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The role SetTextRole left on a control, or Primary when there is none.
        ///
        /// Type-checked rather than cast, because Tag is a public object property that anything may
        /// write: a Tag holding a filename has to read as "no role", not as an InvalidCastException
        /// halfway through a control tree Apply has half-coloured.
        /// </summary>
        private static ThemeTextRole ReadTextRole(Control control)
        {
            return control.Tag is ThemeTextRole ? (ThemeTextRole)control.Tag : ThemeTextRole.Primary;
        }

        /// <summary>
        /// The one place a role becomes a colour. Every semantic label colour in the application
        /// arrives here - the designer files and the runtime writers all go through SetTextRole, and
        /// Apply's Label branch re-resolves the same role on a switch - so there is exactly one
        /// answer to "what colour is a bad status", per palette. Two answers is what the bug was.
        ///
        /// Primary is the default case rather than a case of its own, so a role added to the enum
        /// and forgotten here still paints readable body text.
        /// </summary>
        private static Color ResolveTextRole(ThemeTextRole role, ThemePalette palette)
        {
            switch (role)
            {
                case ThemeTextRole.Secondary:
                    return palette.TextSecondary;
                case ThemeTextRole.StatusGood:
                    return palette.StatusGood;
                case ThemeTextRole.StatusWarning:
                    return palette.StatusWarning;
                case ThemeTextRole.StatusBad:
                    return palette.StatusBad;
                default:
                    return palette.TextPrimary;
            }
        }

        /// <summary>
        /// Flat in the dark palette, Standard in the light one, and it is the glyph that decides.
        /// Measured: the check mark renders #8F8F8F under FlatStyle.Flat and #F3F3F3 under Standard.
        /// A #8F8F8F tick is legible on a dark control face and near-invisible on a light one, and
        /// #F3F3F3 is the other way round - so a single FlatStyle for both themes loses the tick in
        /// one of them. Flat is also what lets BackColor reach the box at all in dark, which is why
        /// this is not simply Standard everywhere.
        ///
        /// Neither value is FlatStyle.System, which matters more than it looks: System is the only
        /// value whose assignment flips ControlStyles.UserPaint in ButtonBase.UpdateOwnerDraw and so
        /// recreates the handle. Moving between Flat and Standard leaves OwnerDraw where it was, so
        /// this property, unlike BorderStyle and TickStyle above, needs no handle gate.
        /// </summary>
        private static FlatStyle GlyphFlatStyle(ThemePalette palette)
        {
            return palette.IsDark ? FlatStyle.Flat : FlatStyle.Standard;
        }

        /// <summary>
        /// Makes a control inherit its parent's background, which is what "transparent to the
        /// parent" means for everything here: every surface in this theme is fully opaque, so
        /// inheriting the parent's colour paints byte-identical pixels to compositing over it.
        ///
        /// ResetBackColor() rather than BackColor = Color.Transparent, because the assignment can
        /// throw. Control's setter rejects any colour with alpha below 255 on a control that has not
        /// got ControlStyles.SupportsTransparentBackColor, and on a ButtonBase that style follows
        /// FlatStyle - so a check box left at FlatStyle.System, which a hand-edited designer file can
        /// produce, would take an ArgumentException straight into Apply's catch-all and abandon the
        /// rest of the walk. Clearing the property cannot fail.
        /// </summary>
        private static void ResetToParentBackground(Control control)
        {
            control.ResetBackColor();
        }
    }
}
