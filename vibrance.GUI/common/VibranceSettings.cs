using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using System.Xml.Linq;
using System.Drawing;
using System.IO;

namespace vibrance.GUI.common
{
    public partial class VibranceSettings : Form
    {
        private IVibranceProxy _v;
        private GraphicsAdapter _graphicsAdapter;
        private ListViewItem _sender;
        private int _vibranceDefaultValue;
        // The executable this dialog edits. Comes from the list items tag, "Change executable..." replaces it
        private string _filePath;
        // Carried through the dialog untouched, so saving does not erase what the game finder found out
        private string _installDirectory;
        private bool _isExecutableUnconfirmed;
        // The designer text of labelTitle, kept so the title can be rebuilt for another executable
        private string _labelTitlePrefix;
        // Only set when we extracted the icon ourselves, the initial image belongs to the list views image list
        private Image _extractedIconImage;
        // Whether trackBarHdrIngameLevel already holds a value worth keeping - either loaded from
        // a real ApplicationSetting.HdrIngameLevel, or set once already by the user ticking the
        // checkbox in this dialog session. Gates the "start from the current SDR level, not 0"
        // mirroring in checkBoxHdrIngameLevel_CheckedChanged: without it, unchecking and
        // re-checking the box within the same session would clobber a value the user (or the
        // saved profile) already set.
        private bool _hdrLevelHasBeenSet;

        public VibranceSettings(IVibranceProxy v, int minValue, int maxValue, int defaultValue, ListViewItem sender, ApplicationSetting setting, List<ResolutionModeWrapper> supportedResolutionList, GraphicsAdapter graphicsAdapter)
        {
            InitializeComponent();
            this._vibranceDefaultValue = defaultValue;
            this.trackBarIngameLevel.Minimum = minValue;
            this.trackBarIngameLevel.Maximum = maxValue;
            this.trackBarIngameLevel.Value = defaultValue;
            // HDR ingame level trackbar (upstream #147, part 2) shares the SDR one's vendor range -
            // Minimum/Maximum have to be set before Value for the same reason as the three lines
            // above: AMD's Maximum is 300, and TrackBar.Value throws if assigned before the range
            // that admits it exists.
            this.trackBarHdrIngameLevel.Minimum = minValue;
            this.trackBarHdrIngameLevel.Maximum = maxValue;
            this.trackBarHdrIngameLevel.Value = defaultValue;
            this._sender = sender;
            this._graphicsAdapter = graphicsAdapter;
            this._v = v;
            this._filePath = sender.Tag == null ? string.Empty : sender.Tag.ToString();
            this._installDirectory = setting == null ? null : setting.InstallDirectory;
            this._isExecutableUnconfirmed = setting != null && setting.IsExecutableUnconfirmed;
            this._labelTitlePrefix = this.labelTitle.Text;
            labelIngameLevel.Text = TrackbarLabelHelper.ResolveVibranceLabelLevel(_graphicsAdapter, trackBarIngameLevel.Value);
            labelHdrIngameLevel.Text = TrackbarLabelHelper.ResolveVibranceLabelLevel(_graphicsAdapter, trackBarHdrIngameLevel.Value);
            reloadTitle();
            this.pictureBox.Image = this._sender.ListView.LargeImageList.Images[this._sender.ImageIndex];
            this.cBoxResolution.DataSource = supportedResolutionList;

            if(_v.GetVibranceInfo().neverChangeColorSettings)
            {
                this.trackBarBrightness.Enabled = false;
                this.trackBarContrast.Enabled = false;
                this.trackBarGamma.Enabled = false;
            }

            if(_v.GetVibranceInfo().neverChangeResolution)
            {
                this.cBoxResolution.Enabled = false;
                this.checkBoxResolution.Enabled = false;
                this.checkBoxResolution.Checked = false;
            }

            // If the setting is new, we don't need to set the progress bar value
            if (setting != null)
            {
                // Sets the progress bar value to the Ingame Vibrance setting
                // The saved values are clamped, they come from an unvalidated settings file and TrackBar.Value throws outside of its range
                this.trackBarIngameLevel.Value = TrackbarLabelHelper.ClampToTrackBarRange(this.trackBarIngameLevel, setting.IngameLevel);
                this.trackBarBrightness.Value = TrackbarLabelHelper.ClampToTrackBarRange(this.trackBarBrightness, setting.Brightness);
                this.trackBarContrast.Value = TrackbarLabelHelper.ClampToTrackBarRange(this.trackBarContrast, setting.Contrast);
                this.trackBarGamma.Value = TrackbarLabelHelper.ClampToTrackBarRange(this.trackBarGamma, setting.Gamma);
                this.cBoxResolution.SelectedItem = setting.ResolutionSettings;
                this.checkBoxResolution.Checked = setting.IsResolutionChangeNeeded;

                // Separate SDR/HDR vibrance level (upstream #147). HasSeparateHdrLevel
                // distinguishes a real configured level from HdrLevelUnset, the only value a
                // pre-v2.7 profile can ever have (ApplicationSetting.HdrIngameLevel's own
                // comment). Unset starts the HDR trackbar mirroring the just-loaded SDR level
                // rather than 0 - exactly what ticking the checkbox fresh does below - so turning
                // HDR on for the first time always starts from where the game's SDR level already
                // is, never from the bottom of the range.
                bool hasSeparateHdrLevel = HdrVibranceHelper.HasSeparateHdrLevel(setting.HdrIngameLevel);
                this.trackBarHdrIngameLevel.Value = TrackbarLabelHelper.ClampToTrackBarRange(this.trackBarHdrIngameLevel,
                    hasSeparateHdrLevel ? setting.HdrIngameLevel : this.trackBarIngameLevel.Value);
                this.checkBoxHdrIngameLevel.Checked = hasSeparateHdrLevel;
                _hdrLevelHasBeenSet = hasSeparateHdrLevel;

                reloadTrackbarLabels();
            }
            // Outside the block above so a brand new entry (setting == null) also ends up with the
            // trackbar disabled to match the checkbox's own unchecked default, not just whatever
            // Enabled the designer happened to leave it at.
            this.trackBarHdrIngameLevel.Enabled = this.checkBoxHdrIngameLevel.Checked;
        }

        private void trackBarIngameLevel_Scroll(object sender, EventArgs e)
        {
            _v.SetVibranceIngameLevel(trackBarIngameLevel.Value);
            labelIngameLevel.Text = TrackbarLabelHelper.ResolveVibranceLabelLevel(_graphicsAdapter, trackBarIngameLevel.Value);
            validateIngameValues();
        }

        // Deliberately does NOT call _v.SetVibranceIngameLevel - that call belongs solely to the
        // SDR trackbar above (trackBarIngameLevel_Scroll). There is no vendor-proxy hook for a
        // distinct "HDR preview" value, and this dialog never applies a live device write from
        // either trackbar regardless of which one moves; only Save persists to the settings file,
        // and the eventual write happens later, through the resolved level each proxy's own apply
        // site now reads (upstream #147, part 2).
        private void trackBarHdrIngameLevel_Scroll(object sender, EventArgs e)
        {
            labelHdrIngameLevel.Text = TrackbarLabelHelper.ResolveVibranceLabelLevel(_graphicsAdapter, trackBarHdrIngameLevel.Value);
        }

        private void checkBoxHdrIngameLevel_CheckedChanged(object sender, EventArgs e)
        {
            this.trackBarHdrIngameLevel.Enabled = this.checkBoxHdrIngameLevel.Checked;
            if (this.checkBoxHdrIngameLevel.Checked && !_hdrLevelHasBeenSet)
            {
                // First time this profile's HDR level is turned on - never loaded a real value and
                // never checked before in this dialog session - so it starts from the SDR level
                // already on screen, not from the bottom of the trackbar's range. A later
                // uncheck/re-check within the same session leaves whatever the user set alone (see
                // _hdrLevelHasBeenSet's own comment).
                this.trackBarHdrIngameLevel.Value = TrackbarLabelHelper.ClampToTrackBarRange(this.trackBarHdrIngameLevel, this.trackBarIngameLevel.Value);
                _hdrLevelHasBeenSet = true;
                trackBarHdrIngameLevel_Scroll(null, null);
            }
        }

        private void trackBarBrightness_Scroll(object sender, EventArgs e)
        {
            labelBrightness.Text = TrackbarLabelHelper.ResolveBrightnessLabelLevel(trackBarBrightness.Value);
            validateIngameValues();
        }

        private void trackBarContrast_Scroll(object sender, EventArgs e)
        {
            labelContrast.Text = TrackbarLabelHelper.ResolveContrastLabelLevel(trackBarContrast.Value);
            validateIngameValues();
        }

        private void trackBarGamma_Scroll(object sender, EventArgs e)
        {
            labelGamma.Text = TrackbarLabelHelper.ResolveGammaLabelLevel(trackBarGamma.Value);
            validateIngameValues();
        }

        private void buttonSave_Click(object sender, EventArgs e)
        {
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        public ApplicationSetting GetApplicationSetting()
        {
            ApplicationSetting setting = new ApplicationSetting(resolveApplicationName(), _filePath, this.trackBarIngameLevel.Value,
                (ResolutionModeWrapper)this.cBoxResolution.SelectedItem, this.checkBoxResolution.Checked,
                this.trackBarBrightness.Value, this.trackBarContrast.Value, this.trackBarGamma.Value);
            // The constructor above knows nothing about these two, they have to be assigned afterwards
            setting.InstallDirectory = _installDirectory;
            setting.IsExecutableUnconfirmed = _isExecutableUnconfirmed;
            // HdrLevelUnset unless the checkbox is actually ticked - an unticked box must never
            // leave behind whatever value the trackbar happens to be showing. HasSeparateHdrLevel
            // would otherwise treat that stale value as a real configured level the next time this
            // profile is read (see HdrVibranceHelper.ResolveIngameLevel).
            setting.HdrIngameLevel = this.checkBoxHdrIngameLevel.Checked
                ? this.trackBarHdrIngameLevel.Value
                : HdrVibranceHelper.HdrLevelUnset;
            return setting;
        }

        private void buttonChangeExecutable_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "Select the executable of this game";
                dialog.Filter = "Executable files (*.exe)|*.exe";
                dialog.CheckFileExists = true;
                string initialDirectory = resolveInitialDirectory();
                if (initialDirectory != null)
                {
                    dialog.InitialDirectory = initialDirectory;
                }

                // Cancelling leaves the dialog completely untouched
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                _filePath = dialog.FileName;
                // The user just told us which executable it is, so it is not a guess of the game finder anymore
                _isExecutableUnconfirmed = false;
                // An executable outside the stored folder means this entry is no longer that game. Keeping
                // the old install directory would leave it matching the old game with the new game's profile
                if (!ApplicationSettingMatcher.IsUnderDirectory(_installDirectory, _filePath))
                {
                    _installDirectory = null;
                }
                reloadTitle();
                reloadIcon();
            }
        }

        private string resolveInitialDirectory()
        {
            try
            {
                // The install directory the game finder stored, so the picker opens inside the game
                if (!string.IsNullOrEmpty(_installDirectory) && Directory.Exists(_installDirectory))
                {
                    return _installDirectory;
                }

                if (!string.IsNullOrEmpty(_filePath))
                {
                    string directory = Path.GetDirectoryName(_filePath);
                    if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                    {
                        return directory;
                    }
                }
            }
            catch (ArgumentException)
            {
                // A settings file may hold a path which is not valid anymore, let the dialog decide where to open
            }

            return null;
        }

        private void reloadTitle()
        {
            this.labelTitle.Text = _labelTitlePrefix + $@"""{resolveApplicationName()}""";
        }

        private void reloadIcon()
        {
            Image previousImage = _extractedIconImage;
            try
            {
                Icon icon = Icon.ExtractAssociatedIcon(_filePath);
                if (icon == null)
                {
                    return;
                }

                using (Bitmap extracted = icon.ToBitmap())
                {
                    // The same size the application list uses, so the icon does not suddenly shrink
                    _extractedIconImage = new Bitmap(extracted, new Size(48, 48));
                }
                this.pictureBox.Image = _extractedIconImage;
            }
            catch (Exception)
            {
                // Keeping the previous icon is the only sane fallback, the executable itself is already selected
                return;
            }

            // Only images this dialog created may be disposed, the initial one belongs to the list views image list
            if (previousImage != null)
            {
                previousImage.Dispose();
            }
        }

        private string resolveApplicationName()
        {
            // Never the display text of the list item: it may carry the "(?)" marker of an unconfirmed
            // executable and/or the "(Off)" marker of a hotkey-suppressed profile (see
            // VibranceGUI.DescribeListItem), and Name is the key which gets compared against the
            // process name of the foreground window
            return Path.GetFileNameWithoutExtension(_filePath);
        }

        private void checkBoxResolution_CheckedChanged(object sender, EventArgs e)
        {
            this.cBoxResolution.Enabled = this.checkBoxResolution.Checked;
        }

        private void buttonReset_Click(object sender, EventArgs e)
        {
            this.trackBarIngameLevel.Value = this._vibranceDefaultValue;
            this.trackBarBrightness.Value = 50;
            this.trackBarContrast.Value = 50;
            this.trackBarGamma.Value = 100;
            this.checkBoxResolution.Checked = false;
            this.cBoxResolution.SelectedIndex = 0;

            // Reset clears any separate HDR level back to "none configured" (HdrLevelUnset, via
            // GetApplicationSetting reading the unchecked checkbox) rather than leaving a stale
            // trackbar value a later re-check would silently resurrect.
            this.checkBoxHdrIngameLevel.Checked = false;
            this.trackBarHdrIngameLevel.Enabled = false;
            this.trackBarHdrIngameLevel.Value = this._vibranceDefaultValue;
            _hdrLevelHasBeenSet = false;

            reloadTrackbarLabels();
        }

        private void reloadTrackbarLabels()
        {
            // Fake a scroll event, to reload the label which tells the percentage
            trackBarIngameLevel_Scroll(null, null);
            trackBarHdrIngameLevel_Scroll(null, null);
            trackBarBrightness_Scroll(null, null);
            trackBarContrast_Scroll(null, null);
            trackBarGamma_Scroll(null, null);
        }

        private void validateIngameValues()
        {
            if(matchesWindowsValues())
            {
                labelValidation.ForeColor = Color.Red;
                labelValidation.Text = "⚠️ Ingame settings match your Windows settings!" + Environment.NewLine + "No color change will happen.";
            }
            else
            {
                labelValidation.ForeColor = Color.Green;
                labelValidation.Text = "Validation of ingame settings was successful";
            }
        }

        private bool matchesWindowsValues()
        {
            VibranceInfo vibranceInfo = _v.GetVibranceInfo();
            return vibranceInfo.userVibranceSettingDefault == trackBarIngameLevel.Value &&
                vibranceInfo.userColorSettings.brightness == trackBarBrightness.Value &&
                vibranceInfo.userColorSettings.contrast == trackBarContrast.Value &&
                vibranceInfo.userColorSettings.gamma == trackBarGamma.Value;
        }
    }
}
