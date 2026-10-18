namespace vibrance.GUI.common
{
    partial class VibranceGUI
    {
        /// <summary>
        /// Erforderliche Designervariable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Verwendete Ressourcen bereinigen.
        /// </summary>
        /// <param name="disposing">True, wenn verwaltete Ressourcen gelöscht werden sollen; andernfalls False.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Vom Windows Form-Designer generierter Code

        /// <summary>
        /// Erforderliche Methode für die Designerunterstützung.
        /// Der Inhalt der Methode darf nicht mit dem Code-Editor geändert werden.
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(VibranceGUI));
            this.notifyIcon = new System.Windows.Forms.NotifyIcon(this.components);
            this.contextMenuStrip = new System.Windows.Forms.ContextMenuStrip(this.components);
            this.twitterToolStripTextBox = new System.Windows.Forms.ToolStripMenuItem();
            this.exitToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.backgroundWorker = new System.ComponentModel.BackgroundWorker();
            this.groupBoxVibrance = new vibrance.GUI.common.theme.ThemedCard();
            this.labelVibranceCaption = new System.Windows.Forms.Label();
            this.separatorVibrance = new vibrance.GUI.common.theme.ThemedCard();
            this.labelWindowsLevel = new System.Windows.Forms.Label();
            this.trackBarWindowsLevel = new System.Windows.Forms.TrackBar();
            this.groupBoxSettings = new vibrance.GUI.common.theme.ThemedCard();
            this.labelSettingsCaption = new System.Windows.Forms.Label();
            this.separatorSettings = new vibrance.GUI.common.theme.ThemedCard();
            this.checkBoxAutostart = new System.Windows.Forms.CheckBox();
            this.checkBoxPrimaryMonitorOnly = new System.Windows.Forms.CheckBox();
            this.checkBoxNeverChangeResolutions = new System.Windows.Forms.CheckBox();
            this.checkBoxUpdateCheck = new System.Windows.Forms.CheckBox();
            this.checkBoxToggleHotkeyEnabled = new System.Windows.Forms.CheckBox();
            this.labelToggleHotkey = new System.Windows.Forms.Label();
            this.textBoxToggleHotkey = new System.Windows.Forms.TextBox();
            this.buttonClearToggleHotkey = new System.Windows.Forms.Button();
            this.labelToggleHotkeyStatus = new System.Windows.Forms.Label();
            this.labelScaling = new System.Windows.Forms.Label();
            this.checkBoxScalingGpu = new System.Windows.Forms.CheckBox();
            this.checkBoxScalingDisplay = new System.Windows.Forms.CheckBox();
            this.checkBoxNotifyResolution = new System.Windows.Forms.CheckBox();
            this.labelAppearanceCaption = new System.Windows.Forms.Label();
            this.radioButtonThemeAuto = new System.Windows.Forms.RadioButton();
            this.radioButtonThemeLight = new System.Windows.Forms.RadioButton();
            this.radioButtonThemeDark = new System.Windows.Forms.RadioButton();
            this.groupBoxColorSettings = new vibrance.GUI.common.theme.ThemedCard();
            this.labelColorSettingsCaption = new System.Windows.Forms.Label();
            this.separatorColorSettings = new vibrance.GUI.common.theme.ThemedCard();
            this.checkBoxNeverChangeColorSettings = new System.Windows.Forms.CheckBox();
            this.labelBrightnessCaption = new System.Windows.Forms.Label();
            this.trackBarBrightness = new System.Windows.Forms.TrackBar();
            this.labelBrightness = new System.Windows.Forms.Label();
            this.labelContrastCaption = new System.Windows.Forms.Label();
            this.trackBarContrast = new System.Windows.Forms.TrackBar();
            this.labelContrast = new System.Windows.Forms.Label();
            this.labelGammaCaption = new System.Windows.Forms.Label();
            this.trackBarGamma = new System.Windows.Forms.TrackBar();
            this.labelGamma = new System.Windows.Forms.Label();
            this.groupBox5 = new vibrance.GUI.common.theme.ThemedCard();
            this.labelProgramsCaption = new System.Windows.Forms.Label();
            this.separatorPrograms = new vibrance.GUI.common.theme.ThemedCard();
            this.buttonProcessExplorer = new System.Windows.Forms.Button();
            this.buttonAddProgram = new System.Windows.Forms.Button();
            this.buttonRemoveProgram = new System.Windows.Forms.Button();
            this.buttonFindGames = new System.Windows.Forms.Button();
            this.listApplications = new System.Windows.Forms.ListView();
            this.observerStatusLabel = new System.Windows.Forms.Label();
            this.statusLabel = new System.Windows.Forms.Label();
            this.labelFindGamesStatus = new System.Windows.Forms.Label();
            this.settingsBackgroundWorker = new System.ComponentModel.BackgroundWorker();
            this.toolTip = new System.Windows.Forms.ToolTip(this.components);
            this.contextMenuStrip.SuspendLayout();
            this.groupBoxVibrance.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.trackBarWindowsLevel)).BeginInit();
            this.groupBoxSettings.SuspendLayout();
            this.groupBoxColorSettings.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.trackBarBrightness)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.trackBarContrast)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.trackBarGamma)).BeginInit();
            this.groupBox5.SuspendLayout();
            this.SuspendLayout();
            //
            // notifyIcon
            //
            this.notifyIcon.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Info;
            this.notifyIcon.BalloonTipText = "Running minimized...";
            this.notifyIcon.BalloonTipTitle = "vibranceGUI";
            this.notifyIcon.ContextMenuStrip = this.contextMenuStrip;
            this.notifyIcon.Icon = ((System.Drawing.Icon)(resources.GetObject("notifyIcon.Icon")));
            this.notifyIcon.Text = "vibranceGUI";
            this.notifyIcon.Visible = true;
            this.notifyIcon.MouseClick += new System.Windows.Forms.MouseEventHandler(this.notifyIcon_MouseClick);
            this.notifyIcon.BalloonTipClicked += new System.EventHandler(this.notifyIcon_BalloonTipClicked);
            this.notifyIcon.BalloonTipClosed += new System.EventHandler(this.notifyIcon_BalloonTipClosed);
            //
            // contextMenuStrip
            //
            this.contextMenuStrip.ImageScalingSize = new System.Drawing.Size(24, 24);
            this.contextMenuStrip.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.twitterToolStripTextBox,
            this.exitToolStripMenuItem});
            this.contextMenuStrip.Name = "contextMenuStrip";
            this.contextMenuStrip.Size = new System.Drawing.Size(303, 68);
            this.contextMenuStrip.Text = "Vibrance Control";
            //
            // twitterToolStripTextBox
            //
            // No ForeColor on this item, and none may be added. It shipped with #0645AD
            // hardcoded, which measures 8.53:1 against the light menu surface (#FFFFFF) and 1.66:1
            // against the dark one (#2B2B2B) - effectively invisible, and invisible only in the
            // theme that did not exist when the colour was picked. The theme walk could not correct
            // it either: ToolStripItem.ForeColor returns the item's own value and falls back to the
            // owning strip only when that value is Color.Empty, so a colour set here is unreachable
            // from the strip. Theme.Apply's ToolStrip branch now assigns every item's ForeColor -
            // this one the palette's Accent, at 7.06:1 dark and 5.67:1 light - and recognises the
            // link by its Text being a URL, which is the same address twitterToolStripTextBox_Click
            // opens. This row matters more than it looks: the redesign removed labelTwitter and
            // linkLabelTwitter from the window body, so it is now the only route to that link.
            this.twitterToolStripTextBox.Font = new System.Drawing.Font("Segoe UI", 9F, ((System.Drawing.FontStyle)((System.Drawing.FontStyle.Italic | System.Drawing.FontStyle.Underline))));
            this.twitterToolStripTextBox.Image = ((System.Drawing.Image)(resources.GetObject("twitterToolStripTextBox.Image")));
            this.twitterToolStripTextBox.Name = "twitterToolStripTextBox";
            this.twitterToolStripTextBox.Size = new System.Drawing.Size(302, 32);
            this.twitterToolStripTextBox.Text = "https://x.com/swatx18";
            this.twitterToolStripTextBox.Click += new System.EventHandler(this.twitterToolStripTextBox_Click);
            //
            // exitToolStripMenuItem
            //
            this.exitToolStripMenuItem.Image = ((System.Drawing.Image)(resources.GetObject("exitToolStripMenuItem.Image")));
            this.exitToolStripMenuItem.Name = "exitToolStripMenuItem";
            this.exitToolStripMenuItem.Size = new System.Drawing.Size(302, 32);
            this.exitToolStripMenuItem.Text = "Exit";
            this.exitToolStripMenuItem.Click += new System.EventHandler(this.exitToolStripMenuItem_Click);
            //
            // backgroundWorker
            //
            this.backgroundWorker.WorkerReportsProgress = true;
            this.backgroundWorker.DoWork += new System.ComponentModel.DoWorkEventHandler(this.backgroundWorker_DoWork);
            this.backgroundWorker.ProgressChanged += new System.ComponentModel.ProgressChangedEventHandler(this.backgroundWorker_ProgressChanged);
            // The four cards below - groupBoxVibrance, groupBoxSettings, groupBoxColorSettings and
            // groupBox5 - are ThemedCards, and the nested GroupBoxes that used to sit inside three
            // of them (groupBox3, groupBox8, groupBox9, groupBox10) are gone entirely, their
            // trackbars and labels re-parented onto the card that contained them. The reason a
            // GroupBox cannot survive is measured and is written up on ThemedCard: the
            // visual-styles renderer draws its frame and caption gap from its own colour table and
            // ignores BackColor, leaving 542 of 20,000 client pixels at #DCDCDC under a dark
            // background. Nesting made that worse rather than better - two frames per slider.
            //
            // groupBoxSettings, groupBoxColorSettings and groupBox5 keep their generated field
            // names for a minimal-diff reason: each is referenced by name from VibranceGUI.cs,
            // and renaming them would bury a type change inside a rename. Each card's caption is a Label and the rule under it a one-pixel
            // ThemedCard, the pattern documented at length in VibranceSettings.Designer.cs -
            // including why the rule is not the Label it looks like it should be (Theme.Apply's
            // Label branch ends in ResetBackColor, which would hand a Label separator the card's
            // own background and make it disappear).
            //
            // Every coordinate in this method is in the 144-DPI units the file is authored in
            // (AutoScaleDimensions below), on a 12-unit spacing grid: cards at x=24 and 732 wide,
            // card contents inset 18 from the card's own edges.
            //
            // groupBoxVibrance
            //
            this.groupBoxVibrance.Controls.Add(this.labelVibranceCaption);
            this.groupBoxVibrance.Controls.Add(this.separatorVibrance);
            this.groupBoxVibrance.Controls.Add(this.labelWindowsLevel);
            this.groupBoxVibrance.Controls.Add(this.trackBarWindowsLevel);
            this.groupBoxVibrance.Location = new System.Drawing.Point(24, 18);
            this.groupBoxVibrance.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.groupBoxVibrance.Name = "groupBoxVibrance";
            this.groupBoxVibrance.Size = new System.Drawing.Size(732, 108);
            this.groupBoxVibrance.TabIndex = 12;
            this.groupBoxVibrance.TabStop = false;
            //
            // labelVibranceCaption
            //
            this.labelVibranceCaption.AutoSize = false;
            this.labelVibranceCaption.Font = vibrance.GUI.common.theme.Theme.CaptionFont;
            this.labelVibranceCaption.Location = new System.Drawing.Point(18, 12);
            this.labelVibranceCaption.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelVibranceCaption.Name = "labelVibranceCaption";
            this.labelVibranceCaption.Size = new System.Drawing.Size(696, 26);
            this.labelVibranceCaption.Text = "Windows Vibrance Level";
            this.labelVibranceCaption.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            //
            // separatorVibrance
            //
            this.separatorVibrance.Location = new System.Drawing.Point(18, 48);
            this.separatorVibrance.Name = "separatorVibrance";
            this.separatorVibrance.Size = new System.Drawing.Size(696, 1);
            //
            // labelWindowsLevel
            //
            // The one number in the window meant to be read from across a desk, so Theme.HeroFont
            // (twice the base point size) and not the base font. That is what sets the three sizes
            // here and they are all measured, not chosen: "100%" rendered in HeroFont needs 103
            // units of width, so the 78 that the three colour-setting values below get would have
            // taken 25 units off its right-hand side. Height is the easier half - the ink is only
            // 26 rows and MiddleRight centres it, so 28 would not in fact have clipped - but 40
            // gives the largest text in the window somewhere to sit. 120x40 holds it with room,
            // its right edge lands on 720 - the same right edge as the three colour-setting values
            // below - and 40 units of height centre it on the trackbar's 36: the box runs 52..92
            // against the slider's 54..90, sharing a centre line on 72, and starts below the rule at
            // y=48 that it would otherwise have painted its own background across for 114 units.
            //
            // The cost is paid by trackBarWindowsLevel, which is 390 wide here where the three
            // colour trackbars are 420: a 420-wide slider at x=198 ends at 618, and 618 plus a
            // 12-unit gap plus 120 overruns the card's 714-unit content edge. A slider 30 units
            // shorter is a far smaller loss than a vibrance reading with its last digit cut off.
            this.labelWindowsLevel.AutoSize = false;
            this.labelWindowsLevel.Font = vibrance.GUI.common.theme.Theme.HeroFont;
            this.labelWindowsLevel.Location = new System.Drawing.Point(600, 52);
            this.labelWindowsLevel.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelWindowsLevel.Name = "labelWindowsLevel";
            this.labelWindowsLevel.Size = new System.Drawing.Size(120, 40);
            this.labelWindowsLevel.TabIndex = 1;
            this.labelWindowsLevel.Text = "50%";
            this.labelWindowsLevel.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            //
            // trackBarWindowsLevel
            //
            // AutoSize false so the 36-unit Height below is honoured at all: a TrackBar left on its
            // default AutoSize overrides whatever height the designer wrote with one it computes
            // from its font and tick style, which is where the 69-unit trackbars this layout
            // replaced came from.
            //
            // TickStyle here rather than through Theme.Apply, which can only write it before a
            // handle exists: the property maps to the TBS_* window style bits and its setter calls
            // RecreateHandle, forbidden at runtime by the foreground rule quoted in Theme.Apply.
            // In InitializeComponent it is free, and it makes the IsHandleCreated gate in Theme.cs
            // belt-and-braces rather than a live limitation. The ticks go because the common
            // control draws them in a fixed system colour that is invisible on a dark surface.
            this.trackBarWindowsLevel.AutoSize = false;
            this.trackBarWindowsLevel.Location = new System.Drawing.Point(198, 54);
            this.trackBarWindowsLevel.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.trackBarWindowsLevel.Maximum = 63;
            this.trackBarWindowsLevel.Name = "trackBarWindowsLevel";
            this.trackBarWindowsLevel.Size = new System.Drawing.Size(390, 36);
            this.trackBarWindowsLevel.TabIndex = 0;
            this.trackBarWindowsLevel.TickStyle = System.Windows.Forms.TickStyle.None;
            this.trackBarWindowsLevel.Scroll += new System.EventHandler(this.trackBarWindowsLevel_Scroll);
            //
            // groupBoxSettings
            //
            // Two columns, at x=18 and x=378 inside the card. The left column's widest member is
            // checkBoxNeverChangeResolutions, which measures 314 units at Theme.BaseFont and so
            // ends at 332 - 46 clear of the right column, which is the check this layout was built
            // around rather than the one it happened to pass.
            //
            // The appearance row is a third row across the full width at y=240 rather than a
            // fourth entry in either column, and that is forced: 332 to 378 is 46 units, and a
            // caption plus three radio buttons need something over 480. The left column also
            // already ends at y=204 and the right at y=228 (labelToggleHotkeyStatus wraps to two
            // lines), so a full-width row below both is the only place left in this card that does
            // not overlap something. It costs 36 units of card height - 240..264 for the row, then
            // the same 18-unit bottom inset every card here keeps - which is why this card is 282
            // tall where it was 246, and why every Location below it in this file moved down by 36.
            this.groupBoxSettings.Controls.Add(this.labelSettingsCaption);
            this.groupBoxSettings.Controls.Add(this.separatorSettings);
            this.groupBoxSettings.Controls.Add(this.checkBoxAutostart);
            this.groupBoxSettings.Controls.Add(this.checkBoxPrimaryMonitorOnly);
            this.groupBoxSettings.Controls.Add(this.checkBoxNeverChangeResolutions);
            this.groupBoxSettings.Controls.Add(this.checkBoxUpdateCheck);
            this.groupBoxSettings.Controls.Add(this.checkBoxToggleHotkeyEnabled);
            this.groupBoxSettings.Controls.Add(this.labelToggleHotkey);
            this.groupBoxSettings.Controls.Add(this.textBoxToggleHotkey);
            this.groupBoxSettings.Controls.Add(this.buttonClearToggleHotkey);
            this.groupBoxSettings.Controls.Add(this.labelToggleHotkeyStatus);
            this.groupBoxSettings.Controls.Add(this.labelAppearanceCaption);
            this.groupBoxSettings.Controls.Add(this.radioButtonThemeAuto);
            this.groupBoxSettings.Controls.Add(this.radioButtonThemeLight);
            this.groupBoxSettings.Controls.Add(this.radioButtonThemeDark);
            this.groupBoxSettings.Controls.Add(this.labelScaling);
            this.groupBoxSettings.Controls.Add(this.checkBoxScalingGpu);
            this.groupBoxSettings.Controls.Add(this.checkBoxScalingDisplay);
            this.groupBoxSettings.Controls.Add(this.checkBoxNotifyResolution);
            this.groupBoxSettings.Location = new System.Drawing.Point(24, 138);
            this.groupBoxSettings.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.groupBoxSettings.Name = "groupBoxSettings";
            // 324, not the 282 the appearance row left behind: #59's scaling trio and resolution
            // notice need a row of their own, and ONE row is all the height budget there is. The
            // window is born with groupBoxColorSettings collapsed, 149 units shorter than
            // ClientSize, and that collapsed height is what has to fit a 1080p screen at 150%
            // scaling - where an authored unit is a pixel and the taskbar leaves ~1032. One row
            // takes the collapsed height from 969 to 1011; two would take it to 1053 and overflow.
            // See ApplyColorSettingsCollapsed in VibranceGUI.cs, which reads these bounds live
            // rather than hardcoding the 149, so growing the card needs no change there.
            this.groupBoxSettings.Size = new System.Drawing.Size(732, 324);
            this.groupBoxSettings.TabIndex = 15;
            this.groupBoxSettings.TabStop = false;
            //
            // labelSettingsCaption
            //
            this.labelSettingsCaption.AutoSize = false;
            this.labelSettingsCaption.Font = vibrance.GUI.common.theme.Theme.CaptionFont;
            this.labelSettingsCaption.Location = new System.Drawing.Point(18, 12);
            this.labelSettingsCaption.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelSettingsCaption.Name = "labelSettingsCaption";
            this.labelSettingsCaption.Size = new System.Drawing.Size(696, 26);
            this.labelSettingsCaption.Text = "Settings";
            this.labelSettingsCaption.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            //
            // separatorSettings
            //
            this.separatorSettings.Location = new System.Drawing.Point(18, 48);
            this.separatorSettings.Name = "separatorSettings";
            this.separatorSettings.Size = new System.Drawing.Size(696, 1);
            //
            // checkBoxAutostart
            //
            this.checkBoxAutostart.AutoSize = true;
            this.checkBoxAutostart.Location = new System.Drawing.Point(18, 54);
            this.checkBoxAutostart.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.checkBoxAutostart.Name = "checkBoxAutostart";
            this.checkBoxAutostart.Size = new System.Drawing.Size(194, 24);
            this.checkBoxAutostart.TabIndex = 8;
            this.checkBoxAutostart.Text = "Autostart vibranceGUI";
            this.checkBoxAutostart.UseVisualStyleBackColor = true;
            this.checkBoxAutostart.CheckedChanged += new System.EventHandler(this.checkBoxAutostart_CheckedChanged);
            //
            // checkBoxPrimaryMonitorOnly
            //
            this.checkBoxPrimaryMonitorOnly.AutoSize = true;
            this.checkBoxPrimaryMonitorOnly.Location = new System.Drawing.Point(18, 96);
            this.checkBoxPrimaryMonitorOnly.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.checkBoxPrimaryMonitorOnly.Name = "checkBoxPrimaryMonitorOnly";
            this.checkBoxPrimaryMonitorOnly.Size = new System.Drawing.Size(223, 24);
            this.checkBoxPrimaryMonitorOnly.TabIndex = 15;
            this.checkBoxPrimaryMonitorOnly.Text = "Affect Primary Monitor only";
            this.toolTip.SetToolTip(this.checkBoxPrimaryMonitorOnly, "When checking this, VibranceGUI will only change vibrance values on your primary " +
        "monitor and the monitor the game is running on - no others.");
            this.checkBoxPrimaryMonitorOnly.UseVisualStyleBackColor = true;
            this.checkBoxPrimaryMonitorOnly.CheckedChanged += new System.EventHandler(this.checkBoxPrimaryMonitorOnly_CheckedChanged);
            //
            // checkBoxNeverChangeResolutions
            //
            this.checkBoxNeverChangeResolutions.AutoSize = true;
            this.checkBoxNeverChangeResolutions.Checked = true;
            this.checkBoxNeverChangeResolutions.CheckState = System.Windows.Forms.CheckState.Checked;
            this.checkBoxNeverChangeResolutions.Location = new System.Drawing.Point(18, 138);
            this.checkBoxNeverChangeResolutions.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.checkBoxNeverChangeResolutions.Name = "checkBoxNeverChangeResolutions";
            this.checkBoxNeverChangeResolutions.Size = new System.Drawing.Size(282, 24);
            this.checkBoxNeverChangeResolutions.TabIndex = 16;
            this.checkBoxNeverChangeResolutions.Text = "Never change Windows resolutions";
            this.toolTip.SetToolTip(this.checkBoxNeverChangeResolutions, resources.GetString("checkBoxNeverChangeResolutions.ToolTip"));
            this.checkBoxNeverChangeResolutions.UseVisualStyleBackColor = true;
            this.checkBoxNeverChangeResolutions.CheckedChanged += new System.EventHandler(this.checkBoxNeverChangeResolutions_CheckedChanged);
            //
            // checkBoxUpdateCheck
            //
            this.checkBoxUpdateCheck.AutoSize = true;
            this.checkBoxUpdateCheck.Location = new System.Drawing.Point(18, 180);
            this.checkBoxUpdateCheck.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.checkBoxUpdateCheck.Name = "checkBoxUpdateCheck";
            this.checkBoxUpdateCheck.Size = new System.Drawing.Size(280, 24);
            this.checkBoxUpdateCheck.TabIndex = 24;
            this.checkBoxUpdateCheck.Text = "Check for a new version on startup";
            this.toolTip.SetToolTip(this.checkBoxUpdateCheck, "When checked, vibranceGUI asks GitHub for the newest release when it starts, at m" +
        "ost once every 12 hours, and shows a tray notification if there is one. Nothing i" +
        "s downloaded or installed automatically, and no information about you is sent.");
            this.checkBoxUpdateCheck.UseVisualStyleBackColor = true;
            this.checkBoxUpdateCheck.CheckedChanged += new System.EventHandler(this.checkBoxUpdateCheck_CheckedChanged);
            //
            // checkBoxToggleHotkeyEnabled
            //
            this.checkBoxToggleHotkeyEnabled.AutoSize = true;
            this.checkBoxToggleHotkeyEnabled.Location = new System.Drawing.Point(378, 54);
            this.checkBoxToggleHotkeyEnabled.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.checkBoxToggleHotkeyEnabled.Name = "checkBoxToggleHotkeyEnabled";
            this.checkBoxToggleHotkeyEnabled.Size = new System.Drawing.Size(248, 24);
            this.checkBoxToggleHotkeyEnabled.TabIndex = 20;
            this.checkBoxToggleHotkeyEnabled.Text = "Toggle game profile by hotkey";
            this.toolTip.SetToolTip(this.checkBoxToggleHotkeyEnabled, "When checked, the key combination below toggles the foreground game\'s profile b" +
        "etween its game level and your Windows level.");
            this.checkBoxToggleHotkeyEnabled.UseVisualStyleBackColor = true;
            this.checkBoxToggleHotkeyEnabled.CheckedChanged += new System.EventHandler(this.checkBoxToggleHotkeyEnabled_CheckedChanged);
            //
            // labelToggleHotkey
            //
            this.labelToggleHotkey.AutoSize = true;
            this.labelToggleHotkey.Location = new System.Drawing.Point(378, 96);
            this.labelToggleHotkey.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelToggleHotkey.Name = "labelToggleHotkey";
            this.labelToggleHotkey.Size = new System.Drawing.Size(153, 20);
            this.labelToggleHotkey.TabIndex = 21;
            this.labelToggleHotkey.Text = "Toggle profile hotkey:";
            //
            // textBoxToggleHotkey
            //
            // BorderStyle in the designer for the same reason as TickStyle above, and it is the same
            // trap: TextBoxBase.BorderStyle calls RecreateHandle, so Theme.Apply can only write it
            // on the constructor path and skips it on a live theme switch. Fixed3D - the designer
            // default - is a sunken light bevel the system draws itself, and it stays light over a
            // dark control background, so FixedSingle is what makes a dark text box look deliberate
            // rather than broken. Written here, it is true from the first paint in either theme.
            this.textBoxToggleHotkey.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            this.textBoxToggleHotkey.Location = new System.Drawing.Point(378, 126);
            this.textBoxToggleHotkey.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.textBoxToggleHotkey.Name = "textBoxToggleHotkey";
            this.textBoxToggleHotkey.ReadOnly = true;
            this.textBoxToggleHotkey.Size = new System.Drawing.Size(270, 39);
            this.textBoxToggleHotkey.TabIndex = 22;
            this.toolTip.SetToolTip(this.textBoxToggleHotkey, "Click here, then press a key combination to bind a global hotkey that toggles t" +
        "he foreground game\'s profile between its game level and your Windows level. A b" +
        "are key with no modifier is legal but steals it from the game system-wide.");
            this.textBoxToggleHotkey.Enter += new System.EventHandler(this.textBoxToggleHotkey_Enter);
            this.textBoxToggleHotkey.KeyDown += new System.Windows.Forms.KeyEventHandler(this.textBoxToggleHotkey_KeyDown);
            this.textBoxToggleHotkey.Leave += new System.EventHandler(this.textBoxToggleHotkey_Leave);
            //
            // buttonClearToggleHotkey
            //
            this.buttonClearToggleHotkey.Location = new System.Drawing.Point(666, 126);
            this.buttonClearToggleHotkey.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.buttonClearToggleHotkey.Name = "buttonClearToggleHotkey";
            this.buttonClearToggleHotkey.Size = new System.Drawing.Size(54, 39);
            this.buttonClearToggleHotkey.TabIndex = 23;
            this.buttonClearToggleHotkey.Text = "X";
            this.toolTip.SetToolTip(this.buttonClearToggleHotkey, "Clears the toggle hotkey binding.");
            this.buttonClearToggleHotkey.UseVisualStyleBackColor = true;
            this.buttonClearToggleHotkey.Click += new System.EventHandler(this.buttonClearToggleHotkey_Click);
            //
            // labelToggleHotkeyStatus
            //
            this.labelToggleHotkeyStatus.AutoSize = true;
            this.labelToggleHotkeyStatus.Location = new System.Drawing.Point(378, 174);
            this.labelToggleHotkeyStatus.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            // A non-zero MaximumSize.Width makes an AutoSize label WRAP instead of growing past
            // it - defense against the longest status string ("No modifier: steals the key from
            // the game.") clipping past groupBoxSettings' own right edge (this label starts at
            // x=378 in a 732-wide card, leaving 354px before the border). The numbers moved with
            // the card; the reason did not. 342 keeps the card's own 12-unit right margin, and
            // that string measures 355 units unwrapped, so it does wrap - to two lines, 54 units
            // tall, ending at y=228 inside a card 282 tall - and it is that wrapped second line,
            // not the left column's last checkbox at y=204, that the appearance row below clears.
            this.labelToggleHotkeyStatus.MaximumSize = new System.Drawing.Size(342, 0);
            this.labelToggleHotkeyStatus.Name = "labelToggleHotkeyStatus";
            this.labelToggleHotkeyStatus.Size = new System.Drawing.Size(0, 20);
            this.labelToggleHotkeyStatus.TabIndex = 24;
            this.labelToggleHotkeyStatus.Text = "";
            //
            // labelAppearanceCaption
            //
            // AutoSize TRUE, and the comment that used to sit here argued for false. It was wrong,
            // and it was wrong about the measurement too: it put "Appearance:" at 77 units, where
            // TextRenderer.MeasureText against SystemFonts.MessageBoxFont gives 73 device px at
            // 96 DPI, i.e. 110 authored units. Width was never the problem - 150 authored is 100
            // device px against 73 of text.
            //
            // HEIGHT was. At 24 authored units the box is 16 device px and the text is 15, so
            // MiddleLeft centring left half a pixel above and below and the descenders of "pp"
            // were sliced off. It shipped as "Appearanc" and was caught by screenshotting the
            // running app, not by any check - there is no automated test that can see a clipped
            // glyph. AutoSize=true makes the control size itself to whatever the user's
            // MessageBoxFont actually measures, which is also what labelToggleHotkey (the other
            // field caption in this card) has always done and why it never had this bug.
            //
            // The Size below is dead while AutoSize is true. It is left at a value that would
            // actually hold the text rather than deleted, so that flipping AutoSize back off
            // does not silently reintroduce the clip.
            this.labelAppearanceCaption.AutoSize = true;
            this.labelAppearanceCaption.Location = new System.Drawing.Point(18, 240);
            this.labelAppearanceCaption.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelAppearanceCaption.Name = "labelAppearanceCaption";
            this.labelAppearanceCaption.Size = new System.Drawing.Size(170, 28);
            this.labelAppearanceCaption.Text = "Appearance:";
            this.labelAppearanceCaption.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            //
            // radioButtonThemeAuto
            //
            // Three radio buttons rather than a ComboBox, and the reason is the theme itself: a
            // ComboBox cannot be coloured past its own frame and drop button without owner-drawing
            // it (Theme.cs's ComboBox branch measures what is left light and says why it stops
            // there), while a RadioButton's glyph is handled by the FlatStyle switch that branch's
            // neighbour already makes. Three options also read without being opened, which is worth
            // more here than the 200-odd units a closed ComboBox would have saved.
            //
            // All three are AutoSize, so each is as wide as its own text needs at whatever
            // SystemFonts.MessageBoxFont the machine has, and they are pitched 120 units apart
            // rather than packed: the widest of "Auto", "Light" and "Dark" measures about 82 units
            // including the glyph and its padding, so there are ~38 units of slack per gap before
            // two of them could ever touch, and the last one ends near 502 against the card's own
            // 714-unit content edge. They are the only RadioButtons in this card, so WinForms'
            // automatic grouping by parent container is the whole group logic needed - and it is
            // also what gives the row standard keyboard behaviour: one tab stop for the three, with
            // the arrow keys moving between them.
            //
            // Auto is Checked here, ahead of the CheckedChanged wiring below so the assignment
            // raises nothing, because ThemeMode.Auto is the zero value a settings file with no
            // themeMode key resolves to (see ThemeMode's own comment). VibranceGUI's constructor
            // then checks whichever radio the stored mode actually names.
            this.radioButtonThemeAuto.AutoSize = true;
            this.radioButtonThemeAuto.Checked = true;
            this.radioButtonThemeAuto.Location = new System.Drawing.Point(180, 240);
            this.radioButtonThemeAuto.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.radioButtonThemeAuto.Name = "radioButtonThemeAuto";
            this.radioButtonThemeAuto.Size = new System.Drawing.Size(82, 24);
            this.radioButtonThemeAuto.TabIndex = 25;
            this.radioButtonThemeAuto.TabStop = true;
            this.radioButtonThemeAuto.Text = "Auto";
            this.toolTip.SetToolTip(this.radioButtonThemeAuto, "Follows the light or dark setting Windows itself is using, and changes with it whi" +
        "le vibranceGUI is running.");
            this.radioButtonThemeAuto.UseVisualStyleBackColor = true;
            this.radioButtonThemeAuto.CheckedChanged += new System.EventHandler(this.radioButtonThemeMode_CheckedChanged);
            //
            // radioButtonThemeLight
            //
            this.radioButtonThemeLight.AutoSize = true;
            this.radioButtonThemeLight.Location = new System.Drawing.Point(300, 240);
            this.radioButtonThemeLight.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.radioButtonThemeLight.Name = "radioButtonThemeLight";
            this.radioButtonThemeLight.Size = new System.Drawing.Size(82, 24);
            this.radioButtonThemeLight.TabIndex = 26;
            this.radioButtonThemeLight.Text = "Light";
            this.toolTip.SetToolTip(this.radioButtonThemeLight, "Always use the light theme, whatever Windows is set to.");
            this.radioButtonThemeLight.UseVisualStyleBackColor = true;
            this.radioButtonThemeLight.CheckedChanged += new System.EventHandler(this.radioButtonThemeMode_CheckedChanged);
            //
            // radioButtonThemeDark
            //
            this.radioButtonThemeDark.AutoSize = true;
            this.radioButtonThemeDark.Location = new System.Drawing.Point(420, 240);
            this.radioButtonThemeDark.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.radioButtonThemeDark.Name = "radioButtonThemeDark";
            this.radioButtonThemeDark.Size = new System.Drawing.Size(82, 24);
            this.radioButtonThemeDark.TabIndex = 27;
            this.radioButtonThemeDark.Text = "Dark";
            this.toolTip.SetToolTip(this.radioButtonThemeDark, "Always use the dark theme, whatever Windows is set to.");
            this.radioButtonThemeDark.UseVisualStyleBackColor = true;
            this.radioButtonThemeDark.CheckedChanged += new System.EventHandler(this.radioButtonThemeMode_CheckedChanged);
            //
            // labelScaling
            //
            // The scaling trio sits left of x=378 and the resolution notice right of it, which is
            // the reverse of what the two-column grid would suggest - and it is forced, not a
            // preference. The trio needs 358 units (18 to 376) and there are only 354 to the right
            // of the 378 column, so the trio cannot go there; the notice is 290 and fits either
            // side. Putting it right leaves 64 units of margin at the card's edge for a user whose
            // SystemFonts.MessageBoxFont measures wider than the 9pt these numbers were taken at,
            // which is the same slack the appearance radios above were given.
            this.labelScaling.AutoSize = true;
            this.labelScaling.Location = new System.Drawing.Point(18, 291);
            this.labelScaling.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelScaling.Name = "labelScaling";
            this.labelScaling.Size = new System.Drawing.Size(178, 20);
            this.labelScaling.TabIndex = 25;
            this.labelScaling.Text = "Scale (primary display):";
            this.toolTip.SetToolTip(this.labelScaling, "Where NVIDIA performs scaling for the primary display (same as NVIDIA Control Panel > Adjust desktop size and position > Perform scaling on). Read from the driver; disabled while a game resolution is applied.");
            //
            // checkBoxScalingGpu
            //
            // 288 against the label's 291: the label is 20 units tall and the checkbox 24, so the
            // three units of offset are what puts their text on the same baseline.
            this.checkBoxScalingGpu.AutoCheck = false;
            this.checkBoxScalingGpu.AutoSize = true;
            this.checkBoxScalingGpu.Enabled = false;
            this.checkBoxScalingGpu.Location = new System.Drawing.Point(204, 288);
            this.checkBoxScalingGpu.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.checkBoxScalingGpu.Name = "checkBoxScalingGpu";
            this.checkBoxScalingGpu.Size = new System.Drawing.Size(66, 24);
            this.checkBoxScalingGpu.TabIndex = 26;
            this.checkBoxScalingGpu.Text = "GPU";
            this.toolTip.SetToolTip(this.checkBoxScalingGpu, "Where NVIDIA performs scaling for the primary display (same as NVIDIA Control Panel > Adjust desktop size and position > Perform scaling on). Read from the driver; disabled while a game resolution is applied.");
            this.checkBoxScalingGpu.UseVisualStyleBackColor = true;
            this.checkBoxScalingGpu.Click += new System.EventHandler(this.checkBoxScalingGpu_Click);
            //
            // checkBoxScalingDisplay
            //
            this.checkBoxScalingDisplay.AutoCheck = false;
            this.checkBoxScalingDisplay.AutoSize = true;
            this.checkBoxScalingDisplay.Enabled = false;
            this.checkBoxScalingDisplay.Location = new System.Drawing.Point(288, 288);
            this.checkBoxScalingDisplay.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.checkBoxScalingDisplay.Name = "checkBoxScalingDisplay";
            this.checkBoxScalingDisplay.Size = new System.Drawing.Size(88, 24);
            this.checkBoxScalingDisplay.TabIndex = 27;
            this.checkBoxScalingDisplay.Text = "Display";
            this.toolTip.SetToolTip(this.checkBoxScalingDisplay, "Where NVIDIA performs scaling for the primary display (same as NVIDIA Control Panel > Adjust desktop size and position > Perform scaling on). Read from the driver; disabled while a game resolution is applied.");
            this.checkBoxScalingDisplay.UseVisualStyleBackColor = true;
            this.checkBoxScalingDisplay.Click += new System.EventHandler(this.checkBoxScalingDisplay_Click);
            //
            // checkBoxNotifyResolution
            //
            this.checkBoxNotifyResolution.AutoSize = true;
            this.checkBoxNotifyResolution.Location = new System.Drawing.Point(378, 288);
            this.checkBoxNotifyResolution.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.checkBoxNotifyResolution.Name = "checkBoxNotifyResolution";
            this.checkBoxNotifyResolution.Size = new System.Drawing.Size(290, 24);
            this.checkBoxNotifyResolution.TabIndex = 28;
            this.checkBoxNotifyResolution.Text = "Notify when a game's resolution is changed";
            this.toolTip.SetToolTip(this.checkBoxNotifyResolution, "Shows a short Windows notification naming the game, resolution and scaling. Windows may hold it back while a fullscreen game is running (Do not disturb / Focus Assist).");
            this.checkBoxNotifyResolution.UseVisualStyleBackColor = true;
            this.checkBoxNotifyResolution.CheckedChanged += new System.EventHandler(this.checkBoxNotifyResolution_CheckedChanged);
            //
            // groupBoxColorSettings
            //
            // Three slider rows at y=102, 150 and 198, each one a caption at x=18, a trackbar at
            // x=198 and a value at x=642. The captions read "Brightness", "Contrast" and "Gamma"
            // where the GroupBoxes they replace said "Windows Brightness" and so on: the longer
            // form measures 172 units against the 174 there are before the trackbar starts, which
            // is no margin at all for a user whose SystemFonts.MessageBoxFont is a point larger
            // than the 9 these numbers were measured at. The card's own caption and the checkbox
            // under it both say what these three belong to.
            this.groupBoxColorSettings.Controls.Add(this.labelColorSettingsCaption);
            this.groupBoxColorSettings.Controls.Add(this.separatorColorSettings);
            this.groupBoxColorSettings.Controls.Add(this.checkBoxNeverChangeColorSettings);
            this.groupBoxColorSettings.Controls.Add(this.labelBrightnessCaption);
            this.groupBoxColorSettings.Controls.Add(this.trackBarBrightness);
            this.groupBoxColorSettings.Controls.Add(this.labelBrightness);
            this.groupBoxColorSettings.Controls.Add(this.labelContrastCaption);
            this.groupBoxColorSettings.Controls.Add(this.trackBarContrast);
            this.groupBoxColorSettings.Controls.Add(this.labelContrast);
            this.groupBoxColorSettings.Controls.Add(this.labelGammaCaption);
            this.groupBoxColorSettings.Controls.Add(this.trackBarGamma);
            this.groupBoxColorSettings.Controls.Add(this.labelGamma);
            this.groupBoxColorSettings.Location = new System.Drawing.Point(24, 474);
            this.groupBoxColorSettings.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.groupBoxColorSettings.Name = "groupBoxColorSettings";
            this.groupBoxColorSettings.Size = new System.Drawing.Size(732, 252);
            this.groupBoxColorSettings.TabIndex = 18;
            this.groupBoxColorSettings.TabStop = false;
            //
            // labelColorSettingsCaption
            //
            this.labelColorSettingsCaption.AutoSize = false;
            this.labelColorSettingsCaption.Font = vibrance.GUI.common.theme.Theme.CaptionFont;
            this.labelColorSettingsCaption.Location = new System.Drawing.Point(18, 12);
            this.labelColorSettingsCaption.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelColorSettingsCaption.Name = "labelColorSettingsCaption";
            this.labelColorSettingsCaption.Size = new System.Drawing.Size(696, 26);
            this.labelColorSettingsCaption.Text = "Color Settings (EXPERIMENTAL)";
            this.labelColorSettingsCaption.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            //
            // separatorColorSettings
            //
            this.separatorColorSettings.Location = new System.Drawing.Point(18, 48);
            this.separatorColorSettings.Name = "separatorColorSettings";
            this.separatorColorSettings.Size = new System.Drawing.Size(696, 1);
            //
            // checkBoxNeverChangeColorSettings
            //
            this.checkBoxNeverChangeColorSettings.AutoSize = true;
            this.checkBoxNeverChangeColorSettings.Checked = true;
            this.checkBoxNeverChangeColorSettings.CheckState = System.Windows.Forms.CheckState.Checked;
            this.checkBoxNeverChangeColorSettings.Location = new System.Drawing.Point(18, 54);
            this.checkBoxNeverChangeColorSettings.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.checkBoxNeverChangeColorSettings.Name = "checkBoxNeverChangeColorSettings";
            // The one authored Size in this file that has to be right. Every other AutoSize
            // control here carries whatever the designer last wrote and the layout engine
            // overwrites it on the first pass, but ApplyColorSettingsCollapsed reads this
            // checkbox's Bottom to work out how far to fold the card, and can be called from the
            // constructor - before any layout pass, when the authored value is all there is.
            // 254x31 is what TextRenderer gives this text at Theme.BaseFont, so the pre-layout
            // and post-layout answers agree and a fold followed by an unfold is exact. Re-measure
            // it if the text ever changes; being wrong costs a few units of card padding on the
            // first fold and nothing after that.
            this.checkBoxNeverChangeColorSettings.Size = new System.Drawing.Size(254, 31);
            this.checkBoxNeverChangeColorSettings.TabIndex = 17;
            this.checkBoxNeverChangeColorSettings.Text = "Never change color settings";
            this.toolTip.SetToolTip(this.checkBoxNeverChangeColorSettings, "When checking this, VibranceGUI will never change the color settings (brightness," +
        " contrast, gamma) on any of your monitors.");
            this.checkBoxNeverChangeColorSettings.UseVisualStyleBackColor = true;
            this.checkBoxNeverChangeColorSettings.CheckedChanged += new System.EventHandler(this.checkBoxNeverChangeColorSettings_CheckedChanged);
            //
            // labelBrightnessCaption
            //
            this.labelBrightnessCaption.AutoSize = false;
            this.labelBrightnessCaption.Location = new System.Drawing.Point(18, 108);
            this.labelBrightnessCaption.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelBrightnessCaption.Name = "labelBrightnessCaption";
            this.labelBrightnessCaption.Size = new System.Drawing.Size(174, 26);
            this.labelBrightnessCaption.Text = "Brightness";
            this.labelBrightnessCaption.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            //
            // trackBarBrightness
            //
            this.trackBarBrightness.AutoSize = false;
            this.trackBarBrightness.Enabled = false;
            this.trackBarBrightness.Location = new System.Drawing.Point(198, 102);
            this.trackBarBrightness.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.trackBarBrightness.Maximum = 100;
            this.trackBarBrightness.Name = "trackBarBrightness";
            this.trackBarBrightness.Size = new System.Drawing.Size(420, 36);
            this.trackBarBrightness.TabIndex = 0;
            this.trackBarBrightness.TickStyle = System.Windows.Forms.TickStyle.None;
            this.trackBarBrightness.Value = 50;
            this.trackBarBrightness.Scroll += new System.EventHandler(this.trackBarBrightness_Scroll);
            //
            // labelBrightness
            //
            this.labelBrightness.AutoSize = false;
            this.labelBrightness.Location = new System.Drawing.Point(642, 108);
            this.labelBrightness.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelBrightness.Name = "labelBrightness";
            this.labelBrightness.Size = new System.Drawing.Size(78, 28);
            this.labelBrightness.TabIndex = 1;
            this.labelBrightness.Text = "50%";
            this.labelBrightness.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            //
            // labelContrastCaption
            //
            this.labelContrastCaption.AutoSize = false;
            this.labelContrastCaption.Location = new System.Drawing.Point(18, 156);
            this.labelContrastCaption.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelContrastCaption.Name = "labelContrastCaption";
            this.labelContrastCaption.Size = new System.Drawing.Size(174, 26);
            this.labelContrastCaption.Text = "Contrast";
            this.labelContrastCaption.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            //
            // trackBarContrast
            //
            this.trackBarContrast.AutoSize = false;
            this.trackBarContrast.Enabled = false;
            this.trackBarContrast.Location = new System.Drawing.Point(198, 150);
            this.trackBarContrast.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.trackBarContrast.Maximum = 100;
            this.trackBarContrast.Name = "trackBarContrast";
            this.trackBarContrast.Size = new System.Drawing.Size(420, 36);
            this.trackBarContrast.TabIndex = 0;
            this.trackBarContrast.TickStyle = System.Windows.Forms.TickStyle.None;
            this.trackBarContrast.Value = 50;
            this.trackBarContrast.Scroll += new System.EventHandler(this.trackBarContrast_Scroll);
            //
            // labelContrast
            //
            this.labelContrast.AutoSize = false;
            this.labelContrast.Location = new System.Drawing.Point(642, 156);
            this.labelContrast.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelContrast.Name = "labelContrast";
            this.labelContrast.Size = new System.Drawing.Size(78, 28);
            this.labelContrast.TabIndex = 1;
            this.labelContrast.Text = "50%";
            this.labelContrast.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            //
            // labelGammaCaption
            //
            this.labelGammaCaption.AutoSize = false;
            this.labelGammaCaption.Location = new System.Drawing.Point(18, 204);
            this.labelGammaCaption.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelGammaCaption.Name = "labelGammaCaption";
            this.labelGammaCaption.Size = new System.Drawing.Size(174, 26);
            this.labelGammaCaption.Text = "Gamma";
            this.labelGammaCaption.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            //
            // trackBarGamma
            //
            this.trackBarGamma.AutoSize = false;
            this.trackBarGamma.Enabled = false;
            this.trackBarGamma.Location = new System.Drawing.Point(198, 198);
            this.trackBarGamma.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.trackBarGamma.Maximum = 240;
            this.trackBarGamma.Minimum = 30;
            this.trackBarGamma.Name = "trackBarGamma";
            this.trackBarGamma.Size = new System.Drawing.Size(420, 36);
            this.trackBarGamma.TabIndex = 0;
            this.trackBarGamma.TickStyle = System.Windows.Forms.TickStyle.None;
            this.trackBarGamma.Value = 100;
            this.trackBarGamma.Scroll += new System.EventHandler(this.trackBarGamma_Scroll);
            //
            // labelGamma
            //
            this.labelGamma.AutoSize = false;
            this.labelGamma.Location = new System.Drawing.Point(642, 204);
            this.labelGamma.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelGamma.Name = "labelGamma";
            this.labelGamma.Size = new System.Drawing.Size(78, 28);
            this.labelGamma.TabIndex = 1;
            this.labelGamma.Text = "1";
            this.labelGamma.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            //
            // groupBox5
            //
            this.groupBox5.Controls.Add(this.labelProgramsCaption);
            this.groupBox5.Controls.Add(this.separatorPrograms);
            this.groupBox5.Controls.Add(this.buttonProcessExplorer);
            this.groupBox5.Controls.Add(this.buttonAddProgram);
            this.groupBox5.Controls.Add(this.buttonRemoveProgram);
            this.groupBox5.Controls.Add(this.buttonFindGames);
            this.groupBox5.Controls.Add(this.listApplications);
            this.groupBox5.Location = new System.Drawing.Point(24, 738);
            this.groupBox5.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.groupBox5.Name = "groupBox5";
            this.groupBox5.Size = new System.Drawing.Size(732, 374);
            this.groupBox5.TabIndex = 19;
            this.groupBox5.TabStop = false;
            //
            // labelProgramsCaption
            //
            this.labelProgramsCaption.AutoSize = false;
            this.labelProgramsCaption.Font = vibrance.GUI.common.theme.Theme.CaptionFont;
            this.labelProgramsCaption.Location = new System.Drawing.Point(18, 12);
            this.labelProgramsCaption.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelProgramsCaption.Name = "labelProgramsCaption";
            this.labelProgramsCaption.Size = new System.Drawing.Size(696, 26);
            this.labelProgramsCaption.Text = "Program Settings";
            this.labelProgramsCaption.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            //
            // separatorPrograms
            //
            this.separatorPrograms.Location = new System.Drawing.Point(18, 48);
            this.separatorPrograms.Name = "separatorPrograms";
            this.separatorPrograms.Size = new System.Drawing.Size(696, 1);
            //
            // buttonProcessExplorer
            //
            this.buttonProcessExplorer.Location = new System.Drawing.Point(18, 54);
            this.buttonProcessExplorer.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.buttonProcessExplorer.Name = "buttonProcessExplorer";
            this.buttonProcessExplorer.Size = new System.Drawing.Size(150, 45);
            this.buttonProcessExplorer.TabIndex = 3;
            this.buttonProcessExplorer.Text = "Add";
            this.buttonProcessExplorer.UseVisualStyleBackColor = true;
            this.buttonProcessExplorer.Click += new System.EventHandler(this.buttonProcessExplorer_Click);
            //
            // buttonAddProgram
            //
            this.buttonAddProgram.Location = new System.Drawing.Point(180, 54);
            this.buttonAddProgram.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.buttonAddProgram.Name = "buttonAddProgram";
            this.buttonAddProgram.Size = new System.Drawing.Size(198, 45);
            this.buttonAddProgram.TabIndex = 0;
            this.buttonAddProgram.Text = "Add manually";
            this.buttonAddProgram.UseVisualStyleBackColor = true;
            this.buttonAddProgram.Click += new System.EventHandler(this.buttonAddProgram_Click);
            //
            // buttonRemoveProgram
            //
            this.buttonRemoveProgram.Location = new System.Drawing.Point(390, 54);
            this.buttonRemoveProgram.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.buttonRemoveProgram.Name = "buttonRemoveProgram";
            this.buttonRemoveProgram.Size = new System.Drawing.Size(150, 45);
            this.buttonRemoveProgram.TabIndex = 2;
            this.buttonRemoveProgram.Text = "Remove";
            this.buttonRemoveProgram.UseVisualStyleBackColor = true;
            this.buttonRemoveProgram.Click += new System.EventHandler(this.buttonRemoveProgram_Click);
            //
            // buttonFindGames
            //
            this.buttonFindGames.Location = new System.Drawing.Point(546, 54);
            this.buttonFindGames.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.buttonFindGames.Name = "buttonFindGames";
            this.buttonFindGames.Size = new System.Drawing.Size(168, 45);
            this.buttonFindGames.TabIndex = 4;
            this.buttonFindGames.Text = "Find games";
            this.toolTip.SetToolTip(this.buttonFindGames, "Searches your Steam and Epic Games libraries for installed games and lets you add " +
        "several of them at once.");
            this.buttonFindGames.UseVisualStyleBackColor = true;
            this.buttonFindGames.Click += new System.EventHandler(this.buttonFindGames_Click);
            //
            // listApplications
            //
            this.listApplications.HideSelection = false;
            this.listApplications.Location = new System.Drawing.Point(18, 114);
            this.listApplications.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.listApplications.Name = "listApplications";
            this.listApplications.ShowItemToolTips = true;
            this.listApplications.Size = new System.Drawing.Size(696, 240);
            this.listApplications.TabIndex = 1;
            this.listApplications.UseCompatibleStateImageBehavior = false;
            this.listApplications.DoubleClick += new System.EventHandler(this.listApplications_DoubleClick);
            //
            // observerStatusLabel
            //
            // The footer, one row, at x=24 / 240 / 450. The three labels measure 143 ("Observer
            // status: "), up to 191 ("NVAPI Unloaded: " plus a version) and 583 ("Added 12 games.
            // Double-click a game to set its ingame vibrance level.") at Theme.BaseFont: the first
            // two clear the column after them by 73 and 19 units, and the third is the one that
            // does not fit at all, which labelFindGamesStatus below handles with an ellipsis.
            //
            // There was a second row here, carrying an "@swatx18 on X" label and its link - 485
            // units that could not share this one with a 583-unit status. Both are gone from the
            // window body and the link lives in the tray menu alone now (twitterToolStripTextBox
            // above, themed with the rest of the strip). That row cost 42 units of window height,
            // and those 42 are the difference between the collapsed window fitting on a 1080p
            // screen at 150% scaling and standing about 11 pixels too tall for it.
            this.observerStatusLabel.AutoSize = true;
            this.observerStatusLabel.Location = new System.Drawing.Point(24, 1124);
            this.observerStatusLabel.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.observerStatusLabel.Name = "observerStatusLabel";
            this.observerStatusLabel.Size = new System.Drawing.Size(129, 20);
            this.observerStatusLabel.TabIndex = 13;
            this.observerStatusLabel.Text = "Observer status: ";
            //
            // statusLabel
            //
            this.statusLabel.AutoSize = true;
            this.statusLabel.Location = new System.Drawing.Point(240, 1124);
            this.statusLabel.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.statusLabel.Name = "statusLabel";
            this.statusLabel.Size = new System.Drawing.Size(90, 20);
            this.statusLabel.TabIndex = 14;
            this.statusLabel.Text = "Initializing...";
            //
            // labelFindGamesStatus
            //
            // The only label in this file that is allowed to lose text, and the only one that can
            // afford to: it is 583 units of transient confirmation in the 306 left at the right end
            // of the status row, so AutoSize goes false and AutoEllipsis on. That cuts it after
            // roughly "Added 12 games. Double-click a", which keeps the part that is news - how
            // many were added - and SetFindGamesStatus in VibranceGUI.cs hangs the whole string off
            // the tooltip so the instruction is one hover away. AutoSize true instead would have
            // run 277 units past the form's own edge and simply been cut off there, with no
            // tooltip and no ellipsis to say so.
            this.labelFindGamesStatus.AutoEllipsis = true;
            this.labelFindGamesStatus.AutoSize = false;
            // The role, not the colour. Theme.Apply resolves ThemeTextRole.StatusGood against the
            // live palette here and again on every theme switch, which leaves SetFindGamesStatus in
            // VibranceGUI.cs owning the text and the tooltip alone. The Theme.Current.StatusGood
            // this replaced did nothing: the walk at the end of the constructor overwrote every
            // Label with TextPrimary a moment later.
            vibrance.GUI.common.theme.Theme.SetTextRole(this.labelFindGamesStatus, vibrance.GUI.common.theme.ThemeTextRole.StatusGood);
            this.labelFindGamesStatus.Location = new System.Drawing.Point(450, 1124);
            this.labelFindGamesStatus.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelFindGamesStatus.Name = "labelFindGamesStatus";
            this.labelFindGamesStatus.Size = new System.Drawing.Size(306, 28);
            this.labelFindGamesStatus.TabIndex = 20;
            this.labelFindGamesStatus.Text = "";
            this.labelFindGamesStatus.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            //
            // settingsBackgroundWorker
            //
            this.settingsBackgroundWorker.DoWork += new System.ComponentModel.DoWorkEventHandler(this.settingsBackgroundWorker_DoWork);
            this.settingsBackgroundWorker.RunWorkerCompleted += new System.ComponentModel.RunWorkerCompletedEventHandler(this.settingsBackgroundWorker_RunWorkerCompleted);
            //
            // toolTip
            //
            this.toolTip.AutoPopDelay = 5000;
            this.toolTip.InitialDelay = 100;
            this.toolTip.IsBalloon = true;
            this.toolTip.ReshowDelay = 100;
            //
            // VibranceGUI
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(144F, 144F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi;
            // 1160 is the expanded height: 1112 where groupBox5 ends, the status row at 1124, its
            // 28-unit label and an 8-unit bottom margin. It is 42 more than the 1118 this form was
            // authored at, because groupBoxSettings grew by one row to take #59's scaling trio and
            // resolution notice (see that card's own Size). VibranceGUI's constructor collapses
            // groupBoxColorSettings before the handle exists, so the window is normally born 149
            // units shorter than this, at 1011 - and that is the number that has to fit a 1080p
            // screen at 150% scaling, where the DPI is 144, AutoScaleDimensions below is 144, the
            // scale factor is therefore 1.0 and an authored unit is a pixel. 1011 plus the caption
            // against the ~1032 the taskbar leaves: 21 units of room, which is why the scaling
            // controls had to fit in one row rather than two. See ApplyColorSettingsCollapsed.
            this.ClientSize = new System.Drawing.Size(780, 1160);
            // Set here and not by Theme.Apply, which deliberately assigns no font to anything: a
            // font swap on a control whose bounds are fixed pixels reflows its text inside a box
            // that does not grow with it, so the walk would clip text on any form nobody had
            // re-measured. This one has been re-measured - every AutoSize=false control in it is
            // wider than TextRenderer says its text needs at Theme.BaseFont, and the widest
            // AutoSize one (checkBoxNeverChangeResolutions, 314 units) has been given room for the
            // 318 the layout notes allow it - so it owns the font. Safe under AutoScaleMode.Dpi in
            // particular: Dpi scales control bounds from the 144-unit authored grid and never from
            // the font, so this assignment moves nothing.
            this.Font = vibrance.GUI.common.theme.Theme.BaseFont;
            // Order is z-order, not tab order (TabIndex above owns that): the first control added
            // is the topmost. statusLabel goes in ahead of labelFindGamesStatus deliberately, so a
            // long "NVAPI Unloaded: ..." running past x=450 is drawn over the find-games label
            // rather than erased by its opaque background.
            this.Controls.Add(this.statusLabel);
            this.Controls.Add(this.observerStatusLabel);
            this.Controls.Add(this.labelFindGamesStatus);
            this.Controls.Add(this.groupBoxVibrance);
            this.Controls.Add(this.groupBoxSettings);
            this.Controls.Add(this.groupBoxColorSettings);
            this.Controls.Add(this.groupBox5);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.MaximizeBox = false;
            this.Name = "VibranceGUI";
            this.Text = "vibranceGUI";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.Form1_FormClosing);
            this.Load += new System.EventHandler(this.Form1_Load);
            this.Shown += new System.EventHandler(this.Form1_Shown);
            this.Resize += new System.EventHandler(this.Form1_Resize);
            this.contextMenuStrip.ResumeLayout(false);
            this.groupBoxVibrance.ResumeLayout(false);
            this.groupBoxVibrance.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.trackBarWindowsLevel)).EndInit();
            this.groupBoxSettings.ResumeLayout(false);
            this.groupBoxSettings.PerformLayout();
            this.groupBoxColorSettings.ResumeLayout(false);
            this.groupBoxColorSettings.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.trackBarBrightness)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.trackBarContrast)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.trackBarGamma)).EndInit();
            this.groupBox5.ResumeLayout(false);
            this.groupBox5.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.NotifyIcon notifyIcon;
        private System.Windows.Forms.ContextMenuStrip contextMenuStrip;
        private System.Windows.Forms.ToolStripMenuItem twitterToolStripTextBox;
        private System.Windows.Forms.ToolStripMenuItem exitToolStripMenuItem;
        private System.ComponentModel.BackgroundWorker backgroundWorker;
        private vibrance.GUI.common.theme.ThemedCard groupBoxVibrance;
        private System.Windows.Forms.Label labelVibranceCaption;
        private vibrance.GUI.common.theme.ThemedCard separatorVibrance;
        private System.Windows.Forms.Label labelWindowsLevel;
        private System.Windows.Forms.TrackBar trackBarWindowsLevel;
        private vibrance.GUI.common.theme.ThemedCard groupBoxSettings;
        private System.Windows.Forms.Label labelSettingsCaption;
        private vibrance.GUI.common.theme.ThemedCard separatorSettings;
        private System.Windows.Forms.CheckBox checkBoxAutostart;
        private System.Windows.Forms.CheckBox checkBoxPrimaryMonitorOnly;
        private System.Windows.Forms.CheckBox checkBoxNeverChangeResolutions;
        private System.Windows.Forms.CheckBox checkBoxUpdateCheck;
        private System.Windows.Forms.CheckBox checkBoxToggleHotkeyEnabled;
        private System.Windows.Forms.Label labelToggleHotkey;
        private System.Windows.Forms.TextBox textBoxToggleHotkey;
        private System.Windows.Forms.Button buttonClearToggleHotkey;
        private System.Windows.Forms.Label labelToggleHotkeyStatus;
        private System.Windows.Forms.Label labelAppearanceCaption;
        private System.Windows.Forms.RadioButton radioButtonThemeAuto;
        private System.Windows.Forms.RadioButton radioButtonThemeLight;
        private System.Windows.Forms.RadioButton radioButtonThemeDark;
        private vibrance.GUI.common.theme.ThemedCard groupBoxColorSettings;
        private System.Windows.Forms.Label labelColorSettingsCaption;
        private vibrance.GUI.common.theme.ThemedCard separatorColorSettings;
        private System.Windows.Forms.CheckBox checkBoxNeverChangeColorSettings;
        private System.Windows.Forms.Label labelBrightnessCaption;
        private System.Windows.Forms.TrackBar trackBarBrightness;
        private System.Windows.Forms.Label labelBrightness;
        private System.Windows.Forms.Label labelContrastCaption;
        private System.Windows.Forms.TrackBar trackBarContrast;
        private System.Windows.Forms.Label labelContrast;
        private System.Windows.Forms.Label labelGammaCaption;
        private System.Windows.Forms.TrackBar trackBarGamma;
        private System.Windows.Forms.Label labelGamma;
        private vibrance.GUI.common.theme.ThemedCard groupBox5;
        private System.Windows.Forms.Label labelProgramsCaption;
        private vibrance.GUI.common.theme.ThemedCard separatorPrograms;
        private System.Windows.Forms.Button buttonProcessExplorer;
        private System.Windows.Forms.Button buttonAddProgram;
        private System.Windows.Forms.Button buttonRemoveProgram;
        private System.Windows.Forms.Button buttonFindGames;
        private System.Windows.Forms.ListView listApplications;
        private System.Windows.Forms.Label observerStatusLabel;
        private System.Windows.Forms.Label statusLabel;
        private System.Windows.Forms.Label labelFindGamesStatus;
        private System.ComponentModel.BackgroundWorker settingsBackgroundWorker;
        private System.Windows.Forms.ToolTip toolTip;
        private System.Windows.Forms.Label labelScaling;
        private System.Windows.Forms.CheckBox checkBoxScalingGpu;
        private System.Windows.Forms.CheckBox checkBoxScalingDisplay;
        private System.Windows.Forms.CheckBox checkBoxNotifyResolution;
    }
}

