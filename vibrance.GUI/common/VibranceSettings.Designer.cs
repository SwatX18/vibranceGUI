namespace vibrance.GUI.common
{
    partial class VibranceSettings
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(VibranceSettings));
            this.toolTip = new System.Windows.Forms.ToolTip(this.components);
            this.groupBox2 = new vibrance.GUI.common.theme.ThemedCard();
            this.labelIngameLevelCaption = new System.Windows.Forms.Label();
            this.separatorIngameLevel = new vibrance.GUI.common.theme.ThemedCard();
            this.trackBarIngameLevel = new System.Windows.Forms.TrackBar();
            this.labelIngameLevel = new System.Windows.Forms.Label();
            this.checkBoxHdrIngameLevel = new System.Windows.Forms.CheckBox();
            this.trackBarHdrIngameLevel = new System.Windows.Forms.TrackBar();
            this.labelHdrIngameLevel = new System.Windows.Forms.Label();
            this.buttonSave = new System.Windows.Forms.Button();
            this.labelTitle = new System.Windows.Forms.Label();
            this.pictureBox = new System.Windows.Forms.PictureBox();
            this.cBoxResolution = new System.Windows.Forms.ComboBox();
            this.groupBox1 = new vibrance.GUI.common.theme.ThemedCard();
            this.labelResolutionCaption = new System.Windows.Forms.Label();
            this.separatorResolution = new vibrance.GUI.common.theme.ThemedCard();
            this.labelResolution = new System.Windows.Forms.Label();
            this.checkBoxResolution = new System.Windows.Forms.CheckBox();
            this.checkBoxStarResolution = new System.Windows.Forms.CheckBox();
            this.labelRefreshRate = new System.Windows.Forms.Label();
            this.cBoxRefreshRate = new System.Windows.Forms.ComboBox();
            this.labelScaling = new System.Windows.Forms.Label();
            this.cBoxScaling = new System.Windows.Forms.ComboBox();
            this.labelResolutionUnavailable = new System.Windows.Forms.Label();
            this.checkBoxSyncCs2Video = new System.Windows.Forms.CheckBox();
            this.groupBoxBrightness = new vibrance.GUI.common.theme.ThemedCard();
            this.labelBrightnessCaption = new System.Windows.Forms.Label();
            this.separatorBrightness = new vibrance.GUI.common.theme.ThemedCard();
            this.trackBarBrightness = new System.Windows.Forms.TrackBar();
            this.labelBrightness = new System.Windows.Forms.Label();
            this.groupBoxContrast = new vibrance.GUI.common.theme.ThemedCard();
            this.labelContrastCaption = new System.Windows.Forms.Label();
            this.separatorContrast = new vibrance.GUI.common.theme.ThemedCard();
            this.trackBarContrast = new System.Windows.Forms.TrackBar();
            this.labelContrast = new System.Windows.Forms.Label();
            this.groupBoxGamma = new vibrance.GUI.common.theme.ThemedCard();
            this.labelGammaCaption = new System.Windows.Forms.Label();
            this.separatorGamma = new vibrance.GUI.common.theme.ThemedCard();
            this.trackBarGamma = new System.Windows.Forms.TrackBar();
            this.labelGamma = new System.Windows.Forms.Label();
            this.buttonReset = new System.Windows.Forms.Button();
            this.labelValidation = new System.Windows.Forms.Label();
            this.buttonChangeExecutable = new System.Windows.Forms.Button();
            this.groupBox2.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.trackBarIngameLevel)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.trackBarHdrIngameLevel)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox)).BeginInit();
            this.groupBox1.SuspendLayout();
            this.groupBoxBrightness.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.trackBarBrightness)).BeginInit();
            this.groupBoxContrast.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.trackBarContrast)).BeginInit();
            this.groupBoxGamma.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.trackBarGamma)).BeginInit();
            this.SuspendLayout();
            // The five cards in this file are ThemedCards rather than GroupBoxes because the
            // visual-styles renderer draws a GroupBox's frame and caption gap from its own
            // colour table and ignores BackColor - measured at 542 of 20,000 client pixels
            // staying #DCDCDC under a dark BackColor, with FlatStyle.Flat changing that by
            // nothing (the measurement is written up on ThemedCard). The field names stay
            // exactly as the designer generated them - groupBox2, groupBox1, groupBoxBrightness
            // and the rest - for the minimal-diff reason recorded on the cards in
            // VibranceGUI.Designer.cs: every one is referenced by name from VibranceSettings.cs,
            // and a rename would bury a type change inside it.
            //
            // The caption a GroupBox drew for itself becomes a Label, and the top edge of its
            // frame a one-pixel rule, per card. Both have to fit in the band the GroupBox's own
            // caption used to occupy, because no existing child's Location moves in this file -
            // which is why the caption is a fixed 24 units high with MiddleLeft alignment
            // instead of AutoSize. 24 units holds Theme.CaptionFont's 24-unit line box exactly
            // (rendered and measured: ink rows 4..21 of 24, gjpqy descenders included), and
            // groupBox1 is the binding case at the other end - its labelResolution starts at
            // y=28 - so the rule sits at y=27 in all five cards to keep them identical.
            //
            // The rule is a one-pixel ThemedCard and not the Label it looks like it should be:
            // Theme.Apply's Label branch ends in ResetBackColor(), so a Label given
            // ThemePalette.Separator would be handed the card's own SurfaceBackground on the
            // way past and vanish. A ThemedCard's border is the one hairline that walk paints,
            // in CardBorder (#838383 light / #8A8A8A dark) rather than the Separator the
            // palette exposes for exactly this job (#858585 / #818181) - two and nine values
            // apart in one channel. Separator stays unreachable until the walk grows a branch
            // that recognises a separator.
            // 
            // groupBox2
            //
            this.groupBox2.Controls.Add(this.trackBarIngameLevel);
            this.groupBox2.Controls.Add(this.labelIngameLevel);
            this.groupBox2.Controls.Add(this.checkBoxHdrIngameLevel);
            this.groupBox2.Controls.Add(this.trackBarHdrIngameLevel);
            this.groupBox2.Controls.Add(this.labelHdrIngameLevel);
            this.groupBox2.Controls.Add(this.labelIngameLevelCaption);
            this.groupBox2.Controls.Add(this.separatorIngameLevel);
            this.groupBox2.Location = new System.Drawing.Point(18, 97);
            this.groupBox2.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.groupBox2.Name = "groupBox2";
            this.groupBox2.Padding = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.groupBox2.Size = new System.Drawing.Size(369, 210);
            this.groupBox2.TabIndex = 13;
            this.groupBox2.TabStop = false;
            //
            // labelIngameLevelCaption
            //
            this.labelIngameLevelCaption.AutoSize = false;
            this.labelIngameLevelCaption.Font = vibrance.GUI.common.theme.Theme.CaptionFont;
            this.labelIngameLevelCaption.Location = new System.Drawing.Point(8, 2);
            this.labelIngameLevelCaption.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelIngameLevelCaption.Name = "labelIngameLevelCaption";
            this.labelIngameLevelCaption.Size = new System.Drawing.Size(353, 24);
            this.labelIngameLevelCaption.Text = "Ingame Vibrance Level";
            this.labelIngameLevelCaption.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            //
            // separatorIngameLevel
            //
            this.separatorIngameLevel.Location = new System.Drawing.Point(8, 27);
            this.separatorIngameLevel.Name = "separatorIngameLevel";
            this.separatorIngameLevel.Size = new System.Drawing.Size(353, 1);
            //
            // trackBarIngameLevel
            //
            this.trackBarIngameLevel.Location = new System.Drawing.Point(24, 35);
            this.trackBarIngameLevel.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.trackBarIngameLevel.Maximum = 63;
            this.trackBarIngameLevel.Name = "trackBarIngameLevel";
            this.trackBarIngameLevel.Size = new System.Drawing.Size(196, 69);
            this.trackBarIngameLevel.TabIndex = 9;
            // TickStyle here and not through Theme.Apply, which can only write it before a
            // handle exists: TrackBar.TickStyle sets the TBS_* style bits and its setter calls
            // RecreateHandle, which the foreground rule quoted in Theme.Apply forbids outright.
            // In InitializeComponent it is free, and it turns the IsHandleCreated gate in
            // Theme.cs into belt-and-braces rather than a live limitation. The ticks go because
            // the common control draws them in a fixed system colour that is invisible on a
            // dark surface, leaving noise around the slider that nobody can read.
            this.trackBarIngameLevel.TickStyle = System.Windows.Forms.TickStyle.None;
            this.trackBarIngameLevel.Scroll += new System.EventHandler(this.trackBarIngameLevel_Scroll);
            //
            // labelIngameLevel
            //
            this.labelIngameLevel.AutoSize = true;
            this.labelIngameLevel.Location = new System.Drawing.Point(224, 40);
            this.labelIngameLevel.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelIngameLevel.Name = "labelIngameLevel";
            this.labelIngameLevel.Size = new System.Drawing.Size(41, 20);
            this.labelIngameLevel.TabIndex = 10;
            this.labelIngameLevel.Text = "50%";
            //
            // checkBoxHdrIngameLevel
            //
            this.checkBoxHdrIngameLevel.AutoSize = true;
            this.checkBoxHdrIngameLevel.Location = new System.Drawing.Point(24, 108);
            this.checkBoxHdrIngameLevel.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.checkBoxHdrIngameLevel.Name = "checkBoxHdrIngameLevel";
            this.checkBoxHdrIngameLevel.Size = new System.Drawing.Size(184, 24);
            this.checkBoxHdrIngameLevel.TabIndex = 11;
            this.checkBoxHdrIngameLevel.Text = "Use a different level in HDR";
            this.checkBoxHdrIngameLevel.UseVisualStyleBackColor = true;
            this.checkBoxHdrIngameLevel.CheckedChanged += new System.EventHandler(this.checkBoxHdrIngameLevel_CheckedChanged);
            //
            // trackBarHdrIngameLevel
            //
            this.trackBarHdrIngameLevel.Enabled = false;
            this.trackBarHdrIngameLevel.Location = new System.Drawing.Point(24, 136);
            this.trackBarHdrIngameLevel.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.trackBarHdrIngameLevel.Maximum = 63;
            this.trackBarHdrIngameLevel.Name = "trackBarHdrIngameLevel";
            this.trackBarHdrIngameLevel.Size = new System.Drawing.Size(196, 69);
            this.trackBarHdrIngameLevel.TabIndex = 12;
            this.trackBarHdrIngameLevel.TickStyle = System.Windows.Forms.TickStyle.None;
            this.trackBarHdrIngameLevel.Scroll += new System.EventHandler(this.trackBarHdrIngameLevel_Scroll);
            //
            // labelHdrIngameLevel
            //
            this.labelHdrIngameLevel.AutoSize = true;
            this.labelHdrIngameLevel.Location = new System.Drawing.Point(224, 141);
            this.labelHdrIngameLevel.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelHdrIngameLevel.Name = "labelHdrIngameLevel";
            this.labelHdrIngameLevel.Size = new System.Drawing.Size(41, 20);
            this.labelHdrIngameLevel.TabIndex = 13;
            this.labelHdrIngameLevel.Text = "50%";
            //
            // buttonSave
            // 
            this.buttonSave.Location = new System.Drawing.Point(247, 976);
            this.buttonSave.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.buttonSave.Name = "buttonSave";
            this.buttonSave.Size = new System.Drawing.Size(140, 37);
            this.buttonSave.TabIndex = 14;
            this.buttonSave.Text = "Save";
            this.buttonSave.UseVisualStyleBackColor = true;
            this.buttonSave.Click += new System.EventHandler(this.buttonSave_Click);
            // 
            // labelTitle
            // 
            this.labelTitle.AutoSize = true;
            this.labelTitle.Location = new System.Drawing.Point(99, 14);
            this.labelTitle.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            // Widened from 225 for the form font set below: the executable name is one
            // unbreakable token, so a narrower cap buys nothing but wrapped lines, and lines
            // here grow downwards into groupBox2 at y=97. 288 is all the room there is - x=99
            // in a 405-wide form, less the 18-unit right margin - and it holds the longest
            // name measured (277 units) on the three lines that fit above the card rather
            // than the four that do not.
            this.labelTitle.MaximumSize = new System.Drawing.Size(288, 0);
            this.labelTitle.Name = "labelTitle";
            this.labelTitle.Size = new System.Drawing.Size(95, 20);
            this.labelTitle.TabIndex = 15;
            this.labelTitle.Text = "Settings for ";
            // 
            // pictureBox
            // 
            this.pictureBox.Location = new System.Drawing.Point(18, 14);
            this.pictureBox.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.pictureBox.Name = "pictureBox";
            this.pictureBox.Size = new System.Drawing.Size(72, 74);
            this.pictureBox.TabIndex = 16;
            this.pictureBox.TabStop = false;
            // 
            // cBoxResolution
            // 
            this.cBoxResolution.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cBoxResolution.DropDownWidth = 349;
            this.cBoxResolution.Enabled = false;
            this.cBoxResolution.FormattingEnabled = true;
            this.cBoxResolution.Location = new System.Drawing.Point(9, 88);
            this.cBoxResolution.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.cBoxResolution.Name = "cBoxResolution";
            this.cBoxResolution.Size = new System.Drawing.Size(255, 28);
            this.cBoxResolution.TabIndex = 2;
            this.cBoxResolution.SelectionChangeCommitted += new System.EventHandler(this.cBoxResolution_SelectionChangeCommitted);
            // 
            // groupBox1
            // 
            this.groupBox1.Controls.Add(this.labelResolution);
            this.groupBox1.Controls.Add(this.checkBoxResolution);
            this.groupBox1.Controls.Add(this.cBoxResolution);
            this.groupBox1.Controls.Add(this.checkBoxStarResolution);
            this.groupBox1.Controls.Add(this.labelRefreshRate);
            this.groupBox1.Controls.Add(this.cBoxRefreshRate);
            this.groupBox1.Controls.Add(this.labelScaling);
            this.groupBox1.Controls.Add(this.cBoxScaling);
            this.groupBox1.Controls.Add(this.labelResolutionUnavailable);
            this.groupBox1.Controls.Add(this.checkBoxSyncCs2Video);
            this.groupBox1.Controls.Add(this.labelResolutionCaption);
            this.groupBox1.Controls.Add(this.separatorResolution);
            this.groupBox1.Location = new System.Drawing.Point(18, 680);
            this.groupBox1.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.groupBox1.Name = "groupBox1";
            this.groupBox1.Padding = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.groupBox1.Size = new System.Drawing.Size(369, 201);
            this.groupBox1.TabIndex = 19;
            this.groupBox1.TabStop = false;
            //
            // labelResolutionCaption
            //
            this.labelResolutionCaption.AutoSize = false;
            this.labelResolutionCaption.Font = vibrance.GUI.common.theme.Theme.CaptionFont;
            this.labelResolutionCaption.Location = new System.Drawing.Point(8, 2);
            this.labelResolutionCaption.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelResolutionCaption.Name = "labelResolutionCaption";
            this.labelResolutionCaption.Size = new System.Drawing.Size(353, 24);
            this.labelResolutionCaption.Text = "Ingame Resolution";
            this.labelResolutionCaption.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            //
            // separatorResolution
            //
            this.separatorResolution.Location = new System.Drawing.Point(8, 27);
            this.separatorResolution.Name = "separatorResolution";
            this.separatorResolution.Size = new System.Drawing.Size(353, 1);
            // 
            // labelResolution
            // 
            this.labelResolution.AutoSize = true;
            this.labelResolution.Font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F, System.Drawing.FontStyle.Italic, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.labelResolution.Location = new System.Drawing.Point(6, 28);
            this.labelResolution.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelResolution.Name = "labelResolution";
            this.labelResolution.Size = new System.Drawing.Size(360, 20);
            this.labelResolution.TabIndex = 0;
            this.labelResolution.Text = "For (Borderless) Windowed Mode players only!";
            // 
            // checkBoxResolution
            // 
            this.checkBoxResolution.AutoSize = true;
            this.checkBoxResolution.Location = new System.Drawing.Point(9, 52);
            this.checkBoxResolution.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.checkBoxResolution.Name = "checkBoxResolution";
            this.checkBoxResolution.Size = new System.Drawing.Size(271, 24);
            this.checkBoxResolution.TabIndex = 1;
            this.checkBoxResolution.Text = "Change Resolution when Ingame";
            this.checkBoxResolution.UseVisualStyleBackColor = true;
            this.checkBoxResolution.CheckedChanged += new System.EventHandler(this.checkBoxResolution_CheckedChanged);
            //
            // checkBoxStarResolution
            //
            this.checkBoxStarResolution.AutoSize = true;
            this.checkBoxStarResolution.Enabled = false;
            this.checkBoxStarResolution.Location = new System.Drawing.Point(272, 90);
            this.checkBoxStarResolution.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.checkBoxStarResolution.Name = "checkBoxStarResolution";
            this.checkBoxStarResolution.Size = new System.Drawing.Size(84, 24);
            this.checkBoxStarResolution.TabIndex = 3;
            this.checkBoxStarResolution.Text = "Starred";
            this.checkBoxStarResolution.UseVisualStyleBackColor = true;
            this.checkBoxStarResolution.CheckedChanged += new System.EventHandler(this.checkBoxStarResolution_CheckedChanged);
            //
            // labelRefreshRate
            //
            this.labelRefreshRate.AutoSize = true;
            this.labelRefreshRate.Location = new System.Drawing.Point(9, 133);
            this.labelRefreshRate.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelRefreshRate.Name = "labelRefreshRate";
            this.labelRefreshRate.Size = new System.Drawing.Size(88, 20);
            this.labelRefreshRate.TabIndex = 4;
            this.labelRefreshRate.Text = "Refresh rate";
            //
            // cBoxRefreshRate
            //
            this.cBoxRefreshRate.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cBoxRefreshRate.Enabled = false;
            this.cBoxRefreshRate.FormattingEnabled = true;
            this.cBoxRefreshRate.Location = new System.Drawing.Point(100, 128);
            this.cBoxRefreshRate.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.cBoxRefreshRate.Name = "cBoxRefreshRate";
            this.cBoxRefreshRate.Size = new System.Drawing.Size(95, 28);
            this.cBoxRefreshRate.TabIndex = 5;
            this.cBoxRefreshRate.SelectionChangeCommitted += new System.EventHandler(this.cBoxRefreshRate_SelectionChangeCommitted);
            //
            // labelScaling
            //
            this.labelScaling.AutoSize = true;
            this.labelScaling.Location = new System.Drawing.Point(205, 133);
            this.labelScaling.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelScaling.Name = "labelScaling";
            this.labelScaling.Size = new System.Drawing.Size(60, 20);
            this.labelScaling.TabIndex = 6;
            this.labelScaling.Text = "Scaling";
            //
            // cBoxScaling
            //
            this.cBoxScaling.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cBoxScaling.Enabled = false;
            this.cBoxScaling.FormattingEnabled = true;
            this.cBoxScaling.Location = new System.Drawing.Point(268, 128);
            this.cBoxScaling.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.cBoxScaling.Name = "cBoxScaling";
            this.cBoxScaling.Size = new System.Drawing.Size(92, 28);
            this.cBoxScaling.TabIndex = 7;
            this.toolTip.SetToolTip(this.cBoxScaling, "Default = let the driver decide (it may keep a previous Center/Stretch choice for this resolution). Center/Stretch force that scaling. Where scaling happens (GPU or display) is set in the main window.");
            this.cBoxScaling.SelectionChangeCommitted += new System.EventHandler(this.cBoxScaling_SelectionChangeCommitted);
            //
            // checkBoxSyncCs2Video
            //
            this.checkBoxSyncCs2Video.AutoSize = true;
            this.checkBoxSyncCs2Video.Enabled = false;
            this.checkBoxSyncCs2Video.Location = new System.Drawing.Point(9, 164);
            this.checkBoxSyncCs2Video.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.checkBoxSyncCs2Video.Name = "checkBoxSyncCs2Video";
            this.checkBoxSyncCs2Video.Size = new System.Drawing.Size(340, 24);
            this.checkBoxSyncCs2Video.TabIndex = 9;
            this.checkBoxSyncCs2Video.Text = "Also set this resolution in CS2's video settings";
            this.toolTip.SetToolTip(this.checkBoxSyncCs2Video, "Writes size and refresh rate to cs2_video.txt for every Steam account when you press Save. Steam Cloud may overwrite the file.");
            this.checkBoxSyncCs2Video.UseVisualStyleBackColor = true;
            this.checkBoxSyncCs2Video.Visible = false;
            //
            // labelResolutionUnavailable
            //
            this.labelResolutionUnavailable.AutoSize = true;
            // The role, not the colour. Firebrick (#B22222) was measured against this branch's own
            // ThemeContrast after the merge and fails WCAG AA on all four dark backgrounds - 1.89:1
            // on ControlBackground, 2.12 on Surface, 2.27 on List, 2.44 on Window - so it could not
            // have stayed either way. But the colour was not the first thing to go wrong: a Label
            // carrying no role is repainted TextPrimary by the Theme.Apply at the end of this
            // dialog's constructor, which turned "(not offered by this display)" into ordinary body
            // text in BOTH themes (measured: #1B1B1B light, #F2F2F2 dark). StatusBad resolves to
            // #C42B1C at 5.66:1 light and #FF99A4 at 6.22:1 dark. Same trap as labelSkipped,
            // labelNote and labelDdu - see Theme.cs's Label branch.
            vibrance.GUI.common.theme.Theme.SetTextRole(this.labelResolutionUnavailable, vibrance.GUI.common.theme.ThemeTextRole.StatusBad);
            this.labelResolutionUnavailable.Location = new System.Drawing.Point(9, 122);
            this.labelResolutionUnavailable.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelResolutionUnavailable.MaximumSize = new System.Drawing.Size(349, 0);
            this.labelResolutionUnavailable.Name = "labelResolutionUnavailable";
            this.labelResolutionUnavailable.Size = new System.Drawing.Size(349, 40);
            this.labelResolutionUnavailable.TabIndex = 8;
            this.labelResolutionUnavailable.Text = "This display doesn't offer the saved mode. Save keeps it; pick a resolution to replace it.";
            this.labelResolutionUnavailable.Visible = false;
            // 
            // groupBoxBrightness
            // 
            this.groupBoxBrightness.Controls.Add(this.trackBarBrightness);
            this.groupBoxBrightness.Controls.Add(this.labelBrightness);
            this.groupBoxBrightness.Controls.Add(this.labelBrightnessCaption);
            this.groupBoxBrightness.Controls.Add(this.separatorBrightness);
            this.groupBoxBrightness.Location = new System.Drawing.Point(18, 317);
            this.groupBoxBrightness.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.groupBoxBrightness.Name = "groupBoxBrightness";
            this.groupBoxBrightness.Padding = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.groupBoxBrightness.Size = new System.Drawing.Size(369, 111);
            this.groupBoxBrightness.TabIndex = 21;
            this.groupBoxBrightness.TabStop = false;
            //
            // labelBrightnessCaption
            //
            this.labelBrightnessCaption.AutoSize = false;
            this.labelBrightnessCaption.Font = vibrance.GUI.common.theme.Theme.CaptionFont;
            this.labelBrightnessCaption.Location = new System.Drawing.Point(8, 2);
            this.labelBrightnessCaption.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelBrightnessCaption.Name = "labelBrightnessCaption";
            this.labelBrightnessCaption.Size = new System.Drawing.Size(353, 24);
            this.labelBrightnessCaption.Text = "Ingame Brightness (EXPERIMENTAL)";
            this.labelBrightnessCaption.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            //
            // separatorBrightness
            //
            this.separatorBrightness.Location = new System.Drawing.Point(8, 27);
            this.separatorBrightness.Name = "separatorBrightness";
            this.separatorBrightness.Size = new System.Drawing.Size(353, 1);
            // 
            // trackBarBrightness
            // 
            this.trackBarBrightness.Location = new System.Drawing.Point(24, 35);
            this.trackBarBrightness.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.trackBarBrightness.Maximum = 100;
            this.trackBarBrightness.Name = "trackBarBrightness";
            this.trackBarBrightness.Size = new System.Drawing.Size(196, 69);
            this.trackBarBrightness.TabIndex = 9;
            this.trackBarBrightness.TickStyle = System.Windows.Forms.TickStyle.None;
            this.trackBarBrightness.Value = 50;
            this.trackBarBrightness.Scroll += new System.EventHandler(this.trackBarBrightness_Scroll);
            // 
            // labelBrightness
            // 
            this.labelBrightness.AutoSize = true;
            this.labelBrightness.Location = new System.Drawing.Point(224, 40);
            this.labelBrightness.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelBrightness.Name = "labelBrightness";
            this.labelBrightness.Size = new System.Drawing.Size(41, 20);
            this.labelBrightness.TabIndex = 10;
            this.labelBrightness.Text = "50%";
            // 
            // groupBoxContrast
            // 
            this.groupBoxContrast.Controls.Add(this.trackBarContrast);
            this.groupBoxContrast.Controls.Add(this.labelContrast);
            this.groupBoxContrast.Controls.Add(this.labelContrastCaption);
            this.groupBoxContrast.Controls.Add(this.separatorContrast);
            this.groupBoxContrast.Location = new System.Drawing.Point(18, 438);
            this.groupBoxContrast.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.groupBoxContrast.Name = "groupBoxContrast";
            this.groupBoxContrast.Padding = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.groupBoxContrast.Size = new System.Drawing.Size(369, 111);
            this.groupBoxContrast.TabIndex = 22;
            this.groupBoxContrast.TabStop = false;
            //
            // labelContrastCaption
            //
            this.labelContrastCaption.AutoSize = false;
            this.labelContrastCaption.Font = vibrance.GUI.common.theme.Theme.CaptionFont;
            this.labelContrastCaption.Location = new System.Drawing.Point(8, 2);
            this.labelContrastCaption.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelContrastCaption.Name = "labelContrastCaption";
            this.labelContrastCaption.Size = new System.Drawing.Size(353, 24);
            this.labelContrastCaption.Text = "Ingame Contrast (EXPERIMENTAL)";
            this.labelContrastCaption.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            //
            // separatorContrast
            //
            this.separatorContrast.Location = new System.Drawing.Point(8, 27);
            this.separatorContrast.Name = "separatorContrast";
            this.separatorContrast.Size = new System.Drawing.Size(353, 1);
            // 
            // trackBarContrast
            // 
            this.trackBarContrast.Location = new System.Drawing.Point(24, 35);
            this.trackBarContrast.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.trackBarContrast.Maximum = 100;
            this.trackBarContrast.Name = "trackBarContrast";
            this.trackBarContrast.Size = new System.Drawing.Size(196, 69);
            this.trackBarContrast.TabIndex = 9;
            this.trackBarContrast.TickStyle = System.Windows.Forms.TickStyle.None;
            this.trackBarContrast.Value = 50;
            this.trackBarContrast.Scroll += new System.EventHandler(this.trackBarContrast_Scroll);
            // 
            // labelContrast
            // 
            this.labelContrast.AutoSize = true;
            this.labelContrast.Location = new System.Drawing.Point(224, 40);
            this.labelContrast.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelContrast.Name = "labelContrast";
            this.labelContrast.Size = new System.Drawing.Size(41, 20);
            this.labelContrast.TabIndex = 10;
            this.labelContrast.Text = "50%";
            // 
            // groupBoxGamma
            // 
            this.groupBoxGamma.Controls.Add(this.trackBarGamma);
            this.groupBoxGamma.Controls.Add(this.labelGamma);
            this.groupBoxGamma.Controls.Add(this.labelGammaCaption);
            this.groupBoxGamma.Controls.Add(this.separatorGamma);
            this.groupBoxGamma.Location = new System.Drawing.Point(18, 559);
            this.groupBoxGamma.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.groupBoxGamma.Name = "groupBoxGamma";
            this.groupBoxGamma.Padding = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.groupBoxGamma.Size = new System.Drawing.Size(369, 111);
            this.groupBoxGamma.TabIndex = 23;
            this.groupBoxGamma.TabStop = false;
            //
            // labelGammaCaption
            //
            this.labelGammaCaption.AutoSize = false;
            this.labelGammaCaption.Font = vibrance.GUI.common.theme.Theme.CaptionFont;
            this.labelGammaCaption.Location = new System.Drawing.Point(8, 2);
            this.labelGammaCaption.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelGammaCaption.Name = "labelGammaCaption";
            this.labelGammaCaption.Size = new System.Drawing.Size(353, 24);
            this.labelGammaCaption.Text = "Ingame Gamma (EXPERIMENTAL)";
            this.labelGammaCaption.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            //
            // separatorGamma
            //
            this.separatorGamma.Location = new System.Drawing.Point(8, 27);
            this.separatorGamma.Name = "separatorGamma";
            this.separatorGamma.Size = new System.Drawing.Size(353, 1);
            // 
            // trackBarGamma
            // 
            this.trackBarGamma.Location = new System.Drawing.Point(24, 35);
            this.trackBarGamma.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.trackBarGamma.Maximum = 240;
            this.trackBarGamma.Minimum = 30;
            this.trackBarGamma.Name = "trackBarGamma";
            this.trackBarGamma.Size = new System.Drawing.Size(196, 69);
            this.trackBarGamma.TabIndex = 9;
            this.trackBarGamma.TickStyle = System.Windows.Forms.TickStyle.None;
            this.trackBarGamma.Value = 100;
            this.trackBarGamma.Scroll += new System.EventHandler(this.trackBarGamma_Scroll);
            // 
            // labelGamma
            // 
            this.labelGamma.AutoSize = true;
            this.labelGamma.Location = new System.Drawing.Point(224, 40);
            this.labelGamma.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelGamma.Name = "labelGamma";
            this.labelGamma.Size = new System.Drawing.Size(18, 20);
            this.labelGamma.TabIndex = 10;
            this.labelGamma.Text = "1";
            // 
            // buttonReset
            // 
            this.buttonReset.Location = new System.Drawing.Point(18, 976);
            this.buttonReset.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.buttonReset.Name = "buttonReset";
            this.buttonReset.Size = new System.Drawing.Size(140, 37);
            this.buttonReset.TabIndex = 24;
            this.buttonReset.Text = "Reset values";
            this.buttonReset.UseVisualStyleBackColor = true;
            this.buttonReset.Click += new System.EventHandler(this.buttonReset_Click);
            // 
            // labelValidation
            // 
            this.labelValidation.AutoSize = true;
            this.labelValidation.Font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            // No colour here: validateIngameValues in VibranceSettings.cs is the single owner,
            // because which colour this label needs is what the validation decides - StatusBad when
            // the ingame values match Windows, StatusGood when they do not - and it runs after the
            // Theme.Apply at the end of the constructor. The Theme.Current.StatusBad this replaced
            // was overwritten by that walk anyway, and the label has no text to colour until the
            // first validation writes one.
            this.labelValidation.Location = new System.Drawing.Point(14, 886);
            this.labelValidation.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelValidation.Name = "labelValidation";
            this.labelValidation.Size = new System.Drawing.Size(0, 20);
            this.labelValidation.TabIndex = 20;
            // 
            // buttonChangeExecutable
            // 
            this.buttonChangeExecutable.Location = new System.Drawing.Point(18, 929);
            this.buttonChangeExecutable.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.buttonChangeExecutable.Name = "buttonChangeExecutable";
            this.buttonChangeExecutable.Size = new System.Drawing.Size(369, 37);
            this.buttonChangeExecutable.TabIndex = 25;
            this.buttonChangeExecutable.Text = "Change executable...";
            this.buttonChangeExecutable.UseVisualStyleBackColor = true;
            this.buttonChangeExecutable.Click += new System.EventHandler(this.buttonChangeExecutable_Click);
            // 
            // VibranceSettings
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(144F, 144F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi;
            this.ClientSize = new System.Drawing.Size(405, 1027);
            // Set here and not by Theme.Apply, which deliberately assigns no font to anything:
            // a font swap on a control whose bounds are fixed pixels reflows its text inside a
            // box that does not grow with it, so the walk would clip text on any form nobody
            // had re-measured. This one has been re-measured - every AutoSize=false control in
            // it is wider than TextRenderer says its text needs at Theme.BaseFont - so it owns
            // the font. Safe under AutoScaleMode.Dpi in particular: Dpi scales control bounds
            // from the 144-unit authored grid and never from the font, so this moves nothing.
            this.Font = vibrance.GUI.common.theme.Theme.BaseFont;
            this.Controls.Add(this.buttonChangeExecutable);
            this.Controls.Add(this.labelValidation);
            this.Controls.Add(this.buttonReset);
            this.Controls.Add(this.groupBoxGamma);
            this.Controls.Add(this.groupBoxContrast);
            this.Controls.Add(this.groupBoxBrightness);
            this.Controls.Add(this.groupBox1);
            this.Controls.Add(this.pictureBox);
            this.Controls.Add(this.labelTitle);
            this.Controls.Add(this.buttonSave);
            this.Controls.Add(this.groupBox2);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.MaximizeBox = false;
            this.Name = "VibranceSettings";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "vibranceGUI";
            this.groupBox2.ResumeLayout(false);
            this.groupBox2.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.trackBarIngameLevel)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.trackBarHdrIngameLevel)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox)).EndInit();
            this.groupBox1.ResumeLayout(false);
            this.groupBox1.PerformLayout();
            this.groupBoxBrightness.ResumeLayout(false);
            this.groupBoxBrightness.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.trackBarBrightness)).EndInit();
            this.groupBoxContrast.ResumeLayout(false);
            this.groupBoxContrast.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.trackBarContrast)).EndInit();
            this.groupBoxGamma.ResumeLayout(false);
            this.groupBoxGamma.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.trackBarGamma)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private vibrance.GUI.common.theme.ThemedCard groupBox2;
        private System.Windows.Forms.Label labelIngameLevelCaption;
        private vibrance.GUI.common.theme.ThemedCard separatorIngameLevel;
        private System.Windows.Forms.TrackBar trackBarIngameLevel;
        private System.Windows.Forms.Label labelIngameLevel;
        private System.Windows.Forms.CheckBox checkBoxHdrIngameLevel;
        private System.Windows.Forms.TrackBar trackBarHdrIngameLevel;
        private System.Windows.Forms.Label labelHdrIngameLevel;
        private System.Windows.Forms.Button buttonSave;
        private System.Windows.Forms.Label labelTitle;
        private System.Windows.Forms.PictureBox pictureBox;
        private System.Windows.Forms.ComboBox cBoxResolution;
        private System.Windows.Forms.ToolTip toolTip;
        private vibrance.GUI.common.theme.ThemedCard groupBox1;
        private System.Windows.Forms.Label labelResolutionCaption;
        private vibrance.GUI.common.theme.ThemedCard separatorResolution;
        private System.Windows.Forms.CheckBox checkBoxResolution;
        private System.Windows.Forms.CheckBox checkBoxSyncCs2Video;
        private System.Windows.Forms.Label labelResolution;
        private vibrance.GUI.common.theme.ThemedCard groupBoxBrightness;
        private System.Windows.Forms.Label labelBrightnessCaption;
        private vibrance.GUI.common.theme.ThemedCard separatorBrightness;
        private System.Windows.Forms.TrackBar trackBarBrightness;
        private System.Windows.Forms.Label labelBrightness;
        private vibrance.GUI.common.theme.ThemedCard groupBoxContrast;
        private System.Windows.Forms.Label labelContrastCaption;
        private vibrance.GUI.common.theme.ThemedCard separatorContrast;
        private System.Windows.Forms.TrackBar trackBarContrast;
        private System.Windows.Forms.Label labelContrast;
        private vibrance.GUI.common.theme.ThemedCard groupBoxGamma;
        private System.Windows.Forms.Label labelGammaCaption;
        private vibrance.GUI.common.theme.ThemedCard separatorGamma;
        private System.Windows.Forms.TrackBar trackBarGamma;
        private System.Windows.Forms.Label labelGamma;
        private System.Windows.Forms.Button buttonReset;
        private System.Windows.Forms.Label labelValidation;
        private System.Windows.Forms.Button buttonChangeExecutable;
        private System.Windows.Forms.CheckBox checkBoxStarResolution;
        private System.Windows.Forms.Label labelRefreshRate;
        private System.Windows.Forms.ComboBox cBoxRefreshRate;
        private System.Windows.Forms.Label labelScaling;
        private System.Windows.Forms.ComboBox cBoxScaling;
        private System.Windows.Forms.Label labelResolutionUnavailable;
    }
}