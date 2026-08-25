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

        public VibranceSettings(IVibranceProxy v, int minValue, int maxValue, int defaultValue, ListViewItem sender, ApplicationSetting setting, List<ResolutionModeWrapper> supportedResolutionList, GraphicsAdapter graphicsAdapter)
        {
            InitializeComponent();
            this._vibranceDefaultValue = defaultValue;
            this.trackBarIngameLevel.Minimum = minValue;
            this.trackBarIngameLevel.Maximum = maxValue;
            this.trackBarIngameLevel.Value = defaultValue;
            this._sender = sender;
            this._graphicsAdapter = graphicsAdapter;
            this._v = v;
            this._filePath = sender.Tag == null ? string.Empty : sender.Tag.ToString();
            this._installDirectory = setting == null ? null : setting.InstallDirectory;
            this._isExecutableUnconfirmed = setting != null && setting.IsExecutableUnconfirmed;
            this._labelTitlePrefix = this.labelTitle.Text;
            labelIngameLevel.Text = TrackbarLabelHelper.ResolveVibranceLabelLevel(_graphicsAdapter, trackBarIngameLevel.Value);
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
                reloadTrackbarLabels();
            }
        }

        private void trackBarIngameLevel_Scroll(object sender, EventArgs e)
        {
            _v.SetVibranceIngameLevel(trackBarIngameLevel.Value);
            labelIngameLevel.Text = TrackbarLabelHelper.ResolveVibranceLabelLevel(_graphicsAdapter, trackBarIngameLevel.Value);
            validateIngameValues();
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
            // Never the display text of the list item: it may carry the "(?)" marker of an unconfirmed executable,
            // and Name is the key which gets compared against the process name of the foreground window
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

            reloadTrackbarLabels();
        }

        private void reloadTrackbarLabels()
        {
            // Fake a scroll event, to reload the label which tells the percentage
            trackBarIngameLevel_Scroll(null, null);
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
