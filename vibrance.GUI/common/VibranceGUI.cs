using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using vibrance.GUI.common.gamefinder;
using Application = System.Windows.Forms.Application;
using MessageBox = System.Windows.Forms.MessageBox;

namespace vibrance.GUI.common
{
    public partial class VibranceGUI : Form
    {
        private GraphicsAdapter _graphicsAdapter;
        private readonly int _defaultWindowsLevel;
        private readonly int _minTrackBarValue;
        private readonly int _maxTrackBarValue;
        private readonly int _defaultIngameValue;
        private readonly IVibranceProxy _v;
        private IRegistryController _registryController;
        private const string AppName = "vibranceGUI";
        private const string TwitterLink = "https://twitter.com/juvlarN";
        private const string PaypalDonationLink = "https://www.paypal.com/cgi-bin/webscr?cmd=_s-xclick&hosted_button_id=JDQFNKNNEW356";

        private bool _allowVisible;
        private List<ApplicationSetting> _applicationSettings;
        private readonly List<ResolutionModeWrapper> _supportedResolutionList;
        private readonly Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> _windowsResolutionSettings;

        private readonly bool _isForcedExecution;

        // Kept in sync with _applicationSettings by RefreshUnconfirmedCache. The foreground handler runs on
        // the ui thread for every window switch, so it tests this boolean before it looks at anything else
        private bool _hasUnconfirmedEntries;
        private bool _isForegroundConfirmationSubscribed;

        private const string ToolTipExecutableUnconfirmed =
            "Not detected yet. vibranceGUI has not seen this executable in the foreground, so this may be the wrong file. Double-click to change the executable.";

        public VibranceGUI(
            Func<List<ApplicationSetting>, Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>>, IVibranceProxy> getProxy,
            GraphicsAdapter graphicsAdapter,
            int defaultWindowsLevel,
            int minTrackBarValue,
            int maxTrackBarValue,
            int defaultIngameValue,
            bool isForcedExecution)
        {
            _graphicsAdapter = graphicsAdapter;
            _defaultWindowsLevel = defaultWindowsLevel;
            _minTrackBarValue = minTrackBarValue;
            _maxTrackBarValue = maxTrackBarValue;
            _defaultIngameValue = defaultIngameValue;
            _allowVisible = true;
            _isForcedExecution = isForcedExecution;

            InitializeComponent();

            trackBarWindowsLevel.Minimum = minTrackBarValue;
            trackBarWindowsLevel.Maximum = maxTrackBarValue;

            _windowsResolutionSettings = new Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>>();
            foreach (Screen screen in Screen.AllScreens)
            {
                Devmode currentResolutionMode;
                if (ResolutionHelper.GetCurrentResolutionSettings(out currentResolutionMode, screen.DeviceName))
                {
                    List<ResolutionModeWrapper> availableResolutions = ResolutionHelper.EnumerateSupportedResolutionModes(screen.DeviceName);
                    if (screen.Primary)
                    {
                        _supportedResolutionList = availableResolutions;
                    }
                    var tuple = new Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>(new ResolutionModeWrapper(currentResolutionMode), availableResolutions);
                    _windowsResolutionSettings.Add(screen.DeviceName, tuple);
                }
                else
                {
                    MessageBox.Show("Current resolution mode could not be determined. Switching back to your Windows resolution will not work.");
                }
            }
            _applicationSettings = new List<ApplicationSetting>();
            _v = getProxy(_applicationSettings, _windowsResolutionSettings);

            backgroundWorker.WorkerReportsProgress = true;
            settingsBackgroundWorker.WorkerReportsProgress = true;

            backgroundWorker.RunWorkerAsync();
        }

        protected override void SetVisibleCore(bool value)
        {
            if (!_allowVisible)
            {
                value = false;
                if (!this.IsHandleCreated)
                {
                    CreateHandle();
                }
            }
            base.SetVisibleCore(value);
        }

        public void SetAllowVisible(bool value)
        {
            _allowVisible = value;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            SetGuiEnabledFlag(false);
        }

        private void Form1_Resize(object sender, EventArgs e)
        {
            if (this.WindowState == FormWindowState.Minimized)
            {
                //this.notifyIcon.Visible = true;
                //this.notifyIcon.BalloonTipText = "Running minimized... Like the program? Consider donating!";
                //this.notifyIcon.ShowBalloonTip(250);
                this.Hide();
            }
        }

        private void backgroundWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            int vibranceWindowsLevel = _defaultWindowsLevel;
            bool affectPrimaryMonitorOnly = false;
            bool neverSwitchResolution = false;
            bool neverChangeColorSettings = false;
            int brightnessWindowsLevel = 50;
            int contrastWindowsLevel = 50;
            int gammaWindowsLevel = 100;

            while (!this.IsHandleCreated)
            {
                Thread.Sleep(500);
            }

            if (this.InvokeRequired)
            {
                this.Invoke((MethodInvoker)delegate
                {
                    ReadVibranceSettings(out vibranceWindowsLevel, out affectPrimaryMonitorOnly, out neverSwitchResolution, out neverChangeColorSettings, out brightnessWindowsLevel, out contrastWindowsLevel, out gammaWindowsLevel);
                });
            }
            else
            {
                ReadVibranceSettings(out vibranceWindowsLevel, out affectPrimaryMonitorOnly, out neverSwitchResolution, out neverChangeColorSettings, out brightnessWindowsLevel, out contrastWindowsLevel, out gammaWindowsLevel);
            }

            if (_v.GetVibranceInfo().isInitialized)
            {
                backgroundWorker.ReportProgress(1);

                SetGuiEnabledFlag(true);

                _v.SetApplicationSettings(_applicationSettings);
                _v.SetShouldRun(true);
                _v.SetVibranceWindowsLevel(vibranceWindowsLevel);
                _v.SetAffectPrimaryMonitorOnly(affectPrimaryMonitorOnly);
                _v.SetNeverSwitchResolution(neverSwitchResolution);
                _v.SetNeverChangeColorSettings(neverChangeColorSettings);
                _v.SetWindowsColorSettings(brightnessWindowsLevel, contrastWindowsLevel, gammaWindowsLevel);
            }
        }

        private void Form1_Shown(object sender, EventArgs e)
        {
            if (_v != null && _v.GetVibranceInfo().isInitialized)
            {
                SetGuiEnabledFlag(true);
            }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            CleanUp();
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void trackBarWindowsLevel_Scroll(object sender, EventArgs e)
        {
            _v.SetVibranceWindowsLevel(trackBarWindowsLevel.Value);
            labelWindowsLevel.Text = TrackbarLabelHelper.ResolveVibranceLabelLevel(_graphicsAdapter, trackBarWindowsLevel.Value);
            if (!settingsBackgroundWorker.IsBusy)
            {
                settingsBackgroundWorker.RunWorkerAsync();
            }
        }

        private void trackBarBrightness_Scroll(object sender, EventArgs e)
        {
            _v.SetWindowsColorBrightness(trackBarBrightness.Value);
            labelBrightness.Text = TrackbarLabelHelper.ResolveBrightnessLabelLevel(trackBarBrightness.Value);
            if (!settingsBackgroundWorker.IsBusy)
            {
                settingsBackgroundWorker.RunWorkerAsync();
            }
        }


        private void trackBarContrast_Scroll(object sender, EventArgs e)
        {
            _v.SetWindowsColorContrast(trackBarContrast.Value);
            labelContrast.Text = TrackbarLabelHelper.ResolveContrastLabelLevel(trackBarContrast.Value);
            if (!settingsBackgroundWorker.IsBusy)
            {
                settingsBackgroundWorker.RunWorkerAsync();
            }
        }
        private void trackBarGamma_Scroll(object sender, EventArgs e)
        {
            _v.SetWindowsColorGamma(trackBarGamma.Value);
            labelGamma.Text = TrackbarLabelHelper.ResolveGammaLabelLevel(trackBarGamma.Value);
            if (!settingsBackgroundWorker.IsBusy)
            {
                settingsBackgroundWorker.RunWorkerAsync();
            }
        }

        private void settingsBackgroundWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            Thread.Sleep(5000);
            ForceSaveVibranceSettings();
        }

        private void ForceSaveVibranceSettings()
        {
            int windowsLevel = 0;
            bool affectPrimaryMonitorOnly = true;
            bool neverSwitchResolution = true;
            bool neverChangeColorSettings = true;
            int brightnessWindowsLevel = 50;
            int contrastWindowsLevel = 50;
            int gammaWindowsLevel = 100;
            this.Invoke((MethodInvoker)delegate
            {
                windowsLevel = trackBarWindowsLevel.Value;
                affectPrimaryMonitorOnly = checkBoxPrimaryMonitorOnly.Checked;
                neverSwitchResolution = checkBoxNeverChangeResolutions.Checked;
                neverChangeColorSettings = checkBoxNeverChangeColorSettings.Checked;
                brightnessWindowsLevel = trackBarBrightness.Value;
                contrastWindowsLevel = trackBarContrast.Value;
                gammaWindowsLevel = trackBarGamma.Value;
            });
            SaveVibranceSettings(windowsLevel, affectPrimaryMonitorOnly, neverSwitchResolution, neverChangeColorSettings, brightnessWindowsLevel, contrastWindowsLevel, gammaWindowsLevel);
        }

        private void backgroundWorker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            if (e.ProgressPercentage == 1)
            {
                this.statusLabel.Text = "Running!";
                this.statusLabel.ForeColor = Color.Green;
            }
            else if (e.ProgressPercentage == 2)
            {
                this.statusLabel.Text = $"NVAPI Unloaded: {e.UserState}";
            }
        }

        private void settingsBackgroundWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
        }

        private void notifyIcon_MouseClick(object sender, MouseEventArgs e)
        {
            _allowVisible = true;
            this.Show();

            this.WindowState = FormWindowState.Normal;
            this.Visible = true;

            this.Refresh();
            this.ShowInTaskbar = true;
        }

        private void checkBoxPrimaryMonitorOnly_CheckedChanged(object sender, EventArgs e)
        {
            if (this._v == null)
            {
                return;
            }

            this._v.SetAffectPrimaryMonitorOnly(this.checkBoxPrimaryMonitorOnly.Checked);
            if (!this.settingsBackgroundWorker.IsBusy)
            {
                this.settingsBackgroundWorker.RunWorkerAsync();
            }
        }

        private void checkBoxNeverChangeResolutions_CheckedChanged(object sender, EventArgs e)
        {
            if (this._v == null)
            {
                return;
            }

            this._v.SetNeverSwitchResolution(this.checkBoxNeverChangeResolutions.Checked);
            if (!this.settingsBackgroundWorker.IsBusy)
            {
                this.settingsBackgroundWorker.RunWorkerAsync();
            }
        }

        private void checkBoxAutostart_CheckedChanged(object sender, EventArgs e)
        {
            RegistryController autostartController = new RegistryController();
            if (this.checkBoxAutostart.Checked)
            {
                string pathToExe = "\"" + Application.ExecutablePath + "\" -minimized";
                if (_isForcedExecution)
                {
                    pathToExe += string.Format(" --force-{0}", _graphicsAdapter.ToString().ToLower());
                }

                if (!autostartController.IsProgramRegistered(AppName))
                {
                    this.notifyIcon.BalloonTipText = autostartController.RegisterProgram(AppName, pathToExe)
                        ? "Registered to Autostart!"
                        : "Registering to Autostart failed!";
                }
                else if (!autostartController.IsStartupPathUnchanged(AppName, pathToExe))
                {
                    this.notifyIcon.BalloonTipText = autostartController.RegisterProgram(AppName, pathToExe)
                        ? "Updated Autostart Path!"
                        : "Updating Autostart Path failed!";
                }
                else
                {
                    return;
                }
            }
            else
            {
                this.notifyIcon.BalloonTipText = autostartController.UnregisterProgram(AppName)
                    ? "Unregistered from Autostart!"
                    : "Unregistering from Autostart failed!";
            }

            notifyIcon.ShowBalloonTip(250);
        }


        private void checkBoxNeverChangeColorSettings_CheckedChanged(object sender, EventArgs e)
        {
            if (this._v == null)
            {
                return;
            }

            this._v.SetNeverChangeColorSettings(this.checkBoxNeverChangeColorSettings.Checked);

            trackBarBrightness.Enabled = !this.checkBoxNeverChangeColorSettings.Checked;
            trackBarContrast.Enabled = !this.checkBoxNeverChangeColorSettings.Checked;
            trackBarGamma.Enabled = !this.checkBoxNeverChangeColorSettings.Checked;

            if (!this.settingsBackgroundWorker.IsBusy)
            {
                this.settingsBackgroundWorker.RunWorkerAsync();
            }
        }

        private void twitterToolStripTextBox_Click(object sender, EventArgs e)
        {
            Process.Start(TwitterLink);
        }

        private void linkLabelTwitter_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            Process.Start(TwitterLink);
        }

        private void SetGuiEnabledFlag(bool flag)
        {
            this.Invoke((MethodInvoker)delegate
            {
                this.trackBarWindowsLevel.Enabled = flag;
                this.checkBoxAutostart.Enabled = flag;
                this.checkBoxPrimaryMonitorOnly.Enabled = flag;
                this.buttonAddProgram.Enabled = flag;
                this.buttonProcessExplorer.Enabled = flag;
                this.buttonFindGames.Enabled = flag;
                this.buttonRemoveProgram.Enabled = flag;
                this.checkBoxNeverChangeResolutions.Enabled = flag;
                this.checkBoxNeverChangeColorSettings.Enabled = flag;
            });
        }

        private void CleanUp()
        {
            try
            {
                this.statusLabel.Text = "Closing...";
                this.statusLabel.ForeColor = Color.Red;
                this.Update();
                if (_v != null && _v.GetVibranceInfo().isInitialized)
                {
                    _v.HandleDvcExit();
                    _v.SetShouldRun(false);
                    _v.UnloadLibraryEx();
                }
            }
            catch (Exception ex)
            {
                Log(ex);
            }
        }

        public static void Log(Exception ex)
        {
            using (StreamWriter w = File.AppendText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vibranceGUI\\vibranceGUI.log")))
            {
                w.Write("\r\nLog Entry : ");
                w.WriteLine("{0} {1}", DateTime.Now.ToLongTimeString(),
                    DateTime.Now.ToLongDateString());
                w.WriteLine("Exception Found:\nType: {0}", ex.GetType().FullName);
                w.WriteLine("Message: {0}", ex.Message);
                w.WriteLine("Source: {0}", ex.Source);
                w.WriteLine("Stacktrace: {0}", ex.StackTrace);
                w.WriteLine("Exception String: {0}", ex.ToString());

                w.WriteLine("-------------------------------");
            }
        }

        public static void Log(string msg)
        {
            using (StreamWriter w = File.AppendText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vibranceGUI\\vibranceGUI.log")))
            {
                w.Write("\r\nLog Entry : ");
                w.WriteLine("{0} {1}", DateTime.Now.ToLongTimeString(),
                    DateTime.Now.ToLongDateString());
                w.WriteLine(msg);
                w.WriteLine("-------------------------------");
            }
        }

        private void ReadVibranceSettings(out int vibranceWindowsLevel, out bool affectPrimaryMonitorOnly, out bool neverSwitchResolution,
            out bool neverChangeColorSettings, out int brightnessWindowsLevel, out int contrastWindowsLevel, out int gammaWindowsLevel)
        {
            _registryController = new RegistryController();
            this.checkBoxAutostart.Checked = _registryController.IsProgramRegistered(AppName);

            SettingsController settingsController = new SettingsController();
            settingsController.ReadVibranceSettings(_v.GraphicsAdapter, out vibranceWindowsLevel, out affectPrimaryMonitorOnly, out neverSwitchResolution,
                out neverChangeColorSettings, out _applicationSettings, out brightnessWindowsLevel, out contrastWindowsLevel, out gammaWindowsLevel);

            if (this.IsHandleCreated)
            {
                //no null check needed, SettingsController will always return matching values.
                labelWindowsLevel.Text = TrackbarLabelHelper.ResolveVibranceLabelLevel(_graphicsAdapter, vibranceWindowsLevel);

                trackBarWindowsLevel.Value = vibranceWindowsLevel;

                //the saved color settings have to be restored before the checkboxes are set, their CheckedChanged handlers trigger a save of these trackbars.
                //the clamped values are written back so that the caller hands the proxy exactly what the user interface shows.
                brightnessWindowsLevel = TrackbarLabelHelper.ClampToTrackBarRange(trackBarBrightness, brightnessWindowsLevel);
                trackBarBrightness.Value = brightnessWindowsLevel;
                labelBrightness.Text = TrackbarLabelHelper.ResolveBrightnessLabelLevel(brightnessWindowsLevel);
                contrastWindowsLevel = TrackbarLabelHelper.ClampToTrackBarRange(trackBarContrast, contrastWindowsLevel);
                trackBarContrast.Value = contrastWindowsLevel;
                labelContrast.Text = TrackbarLabelHelper.ResolveContrastLabelLevel(contrastWindowsLevel);
                gammaWindowsLevel = TrackbarLabelHelper.ClampToTrackBarRange(trackBarGamma, gammaWindowsLevel);
                trackBarGamma.Value = gammaWindowsLevel;
                labelGamma.Text = TrackbarLabelHelper.ResolveGammaLabelLevel(gammaWindowsLevel);

                checkBoxPrimaryMonitorOnly.Checked = affectPrimaryMonitorOnly;
                checkBoxNeverChangeResolutions.Checked = neverSwitchResolution;
                checkBoxNeverChangeColorSettings.Checked = neverChangeColorSettings;
                foreach (ApplicationSetting application in _applicationSettings.ToList())
                {
                    if (!File.Exists(application.FileName))
                    {
                        _applicationSettings.Remove(application);
                        continue;
                    }

                    InitializeApplicationList();

                    Icon icon = Icon.ExtractAssociatedIcon(application.FileName);
                    if (icon != null)
                    {
                        ApplyApplicationListItemAppearance(AddApplicationListItem(application.FileName, icon, application.Name), application);
                    }
                }
            }

            //the vendor proxy owns the singleton, it created it on this thread while it was initializing.
            //without the guard a failed proxy would make the form install a second hook of its own
            if (_v != null && _v.GetVibranceInfo().isInitialized && !_isForegroundConfirmationSubscribed)
            {
                WinEventHook.GetInstance().WinEventHookHandler += OnForegroundChangedConfirmExecutable;
                _isForegroundConfirmationSubscribed = true;
            }
            RefreshUnconfirmedCache();
        }

        private void SaveVibranceSettings(int windowsLevel, bool affectPrimaryMonitorOnly, bool neverSwitchResolution, bool neverChangeColorSettings, int brightnessWindowsLevel, int contrastWindowsLevel, int gammaWindowsLevel)
        {
            SettingsController settingsController = new SettingsController();

            settingsController.SetVibranceSettings(
                windowsLevel.ToString(),
                affectPrimaryMonitorOnly.ToString(),
                neverSwitchResolution.ToString(),
                neverChangeColorSettings.ToString(),
                _applicationSettings,
                brightnessWindowsLevel.ToString(),
                contrastWindowsLevel.ToString(),
                gammaWindowsLevel.ToString()
            );
        }

        private void buttonPaypal_Click(object sender, EventArgs e)
        {
            Process.Start(PaypalDonationLink);
        }

        private void buttonAddProgram_Click(object sender, EventArgs e)
        {
            InitializeApplicationList();

            OpenFileDialog fileDialog = new OpenFileDialog();
            DialogResult result = fileDialog.ShowDialog();
            if (result == DialogResult.OK && fileDialog.CheckFileExists && fileDialog.SafeFileName != null
                && _applicationSettings.FirstOrDefault(x => x.FileName.ToLower() == fileDialog.FileName.ToLower()) == null)
            {
                Icon icon = Icon.ExtractAssociatedIcon(fileDialog.FileName);
                if (icon != null)
                {
                    ProcessExplorerEntry processExplorerEntry = new ProcessExplorerEntry(fileDialog.FileName, icon, Path.GetFileNameWithoutExtension(fileDialog.FileName));
                    AddProgramIntern(processExplorerEntry);
                }
            }
        }

        public void AddProgramExtern(ProcessExplorerEntry processExplorerEntry)
        {
            if (this.InvokeRequired)
            {
                this.Invoke((MethodInvoker)delegate
                {
                    AddProgramIntern(processExplorerEntry);
                });
            }
            else
            {
                AddProgramIntern(processExplorerEntry);
            }
        }

        private void AddProgramIntern(ProcessExplorerEntry processExplorerEntry)
        {
            InitializeApplicationList();

            if (!File.Exists(processExplorerEntry.Path) || _applicationSettings.FirstOrDefault(x => x.FileName.ToLower() == processExplorerEntry.Path.ToLower()) != null)
            {
                this.listApplications.SelectedIndices.Clear();
                return;
            }

            Icon icon = processExplorerEntry.Icon;
            string path = processExplorerEntry.Path;
            if (icon != null)
            {
                ListViewItem lvi = AddApplicationListItem(path, icon, Path.GetFileNameWithoutExtension(path));
                this.listApplications.SelectedIndices.Clear();
                lvi.Selected = true;
                listApplications_DoubleClick(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// The only place in the program which adds to the image list of the application list. The image
        /// index of an item is the position it is inserted at, so the icon and the item have to be added
        /// together and in this order: LargeImageList.Images.Add first, then ImageIndex read from
        /// Items.Count before Items.Add pushes it up. Appending at the end never disturbs an existing
        /// index, which is what makes bulk adding safe and keeps the fixup loop of
        /// buttonRemoveProgram_Click correct.
        /// </summary>
        private ListViewItem AddApplicationListItem(string fileName, Icon icon, string text)
        {
            InitializeApplicationList();

            this.listApplications.LargeImageList.Images.Add(icon);
            ListViewItem lvi = new ListViewItem(text);
            lvi.ImageIndex = this.listApplications.Items.Count;
            lvi.Tag = fileName;
            this.listApplications.Items.Add(lvi);
            return lvi;
        }

        /// <summary>
        /// Adds every game the game finder returned in one go. Opens no dialog, so the ApplicationSetting
        /// is built here instead of by VibranceSettings, and the whole batch is saved once at the end.
        /// Ui thread only.
        /// </summary>
        public void AddProgramsBulk(List<GameCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return;
            }

            InitializeApplicationList();

            int addedCount = 0;
            foreach (GameCandidate candidate in candidates)
            {
                if (candidate == null || string.IsNullOrEmpty(candidate.ExecutablePath))
                {
                    continue;
                }

                string path = candidate.ExecutablePath;
                //an entry the File.Exists sweep of ReadVibranceSettings would drop on the next start is
                //not worth adding in the first place
                if (!File.Exists(path))
                {
                    continue;
                }

                if (_applicationSettings.FirstOrDefault(x => string.Equals(x.FileName, path, StringComparison.OrdinalIgnoreCase)) != null)
                {
                    continue;
                }

                string name = Path.GetFileNameWithoutExtension(path);
                //a second entry with the same name could never activate, the runtime matches on the name alone
                if (_applicationSettings.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) != null)
                {
                    continue;
                }

                Icon icon = ResolveCandidateIcon(candidate);
                if (icon == null)
                {
                    continue;
                }

                //the maximum, not _defaultIngameValue: the vendor default is the neutral value on both
                //vendors, so a whole bulk added library would apply no visible change at all and would be
                //indistinguishable from a broken feature - and from a wrong executable guess, which is
                //exactly what the "(?)" marker exists to make visible. One double-click lowers it per game
                ApplicationSetting setting = new ApplicationSetting(name, path, _maxTrackBarValue, null, false, 50, 50, 100);
                //the constructor above knows nothing about these two, they have to be assigned afterwards
                setting.InstallDirectory = candidate.InstallDirectory;
                setting.IsExecutableUnconfirmed = candidate.Confidence == ExecutableConfidence.Guessed;

                //by mutation, never by reassignment: the proxy holds this very list instance
                _applicationSettings.Add(setting);
                ApplyApplicationListItemAppearance(AddApplicationListItem(setting.FileName, icon, setting.Name), setting);
                addedCount++;
            }

            RefreshUnconfirmedCache();
            this.listApplications.SelectedIndices.Clear();
            ForceSaveVibranceSettings();
            SetFindGamesStatus(addedCount);
        }

        private Icon ResolveCandidateIcon(GameCandidate candidate)
        {
            if (candidate.Icon != null)
            {
                return candidate.Icon;
            }

            try
            {
                //the finder extracts the icon on its worker thread, but extraction is allowed to fail there
                return Icon.ExtractAssociatedIcon(candidate.ExecutablePath);
            }
            catch (Exception ex)
            {
                //one unreadable executable must not take the rest of the batch down with it
                Log(ex);
                return null;
            }
        }

        /// <summary>
        /// Tells the user what the game finder added. The finder window is already gone at this point, so
        /// this is the only place the message can appear.
        /// </summary>
        private void SetFindGamesStatus(int addedCount)
        {
            this.labelFindGamesStatus.Text = addedCount > 0
                ? string.Format("Added {0} game{1}. Double-click a game to set its ingame vibrance level.", addedCount, addedCount == 1 ? "" : "s")
                : "No games were added.";
        }

        /// <summary>
        /// Renders the marker of an executable the game finder guessed. Only the display text is decorated,
        /// never ApplicationSetting.Name, which is the key the foreground process name is compared against.
        /// </summary>
        private void ApplyApplicationListItemAppearance(ListViewItem lvi, ApplicationSetting setting)
        {
            if (lvi == null || setting == null)
            {
                return;
            }

            if (setting.IsExecutableUnconfirmed)
            {
                lvi.Text = setting.Name + " (?)";
                lvi.ForeColor = SystemColors.GrayText;
                lvi.ToolTipText = ToolTipExecutableUnconfirmed;
            }
            else
            {
                lvi.Text = setting.Name;
                lvi.ForeColor = SystemColors.WindowText;
                lvi.ToolTipText = string.Empty;
            }
        }

        private ListViewItem FindApplicationListItem(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return null;
            }

            foreach (ListViewItem lvi in this.listApplications.Items)
            {
                if (lvi.Tag != null && string.Equals(lvi.Tag.ToString(), fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return lvi;
                }
            }

            return null;
        }

        private void RefreshUnconfirmedCache()
        {
            _hasUnconfirmedEntries = _applicationSettings != null &&
                _applicationSettings.Exists(x => x != null && x.IsExecutableUnconfirmed);
        }

        private void RemoveSettingByFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return;
            }

            _applicationSettings.RemoveAll(x => x != null && string.Equals(x.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Clears the marker of a guessed executable the first time that executable really shows up in the
        /// foreground. Runs on the ui thread inside the foreground callback, at the moment a game goes
        /// fullscreen, so the common path is one boolean test and the save is left to the debounced worker.
        /// </summary>
        private void OnForegroundChangedConfirmExecutable(object sender, WinEventHookEventArgs e)
        {
            if (!_hasUnconfirmedEntries)
            {
                return;
            }

            ApplicationSetting setting = _applicationSettings.FirstOrDefault(
                x => x.IsExecutableUnconfirmed &&
                     string.Equals(x.Name, e.ProcessName, StringComparison.OrdinalIgnoreCase));
            if (setting == null)
            {
                return;
            }

            setting.IsExecutableUnconfirmed = false;
            ListViewItem lvi = FindApplicationListItem(setting.FileName);
            if (lvi != null)
            {
                ApplyApplicationListItemAppearance(lvi, setting);
            }
            RefreshUnconfirmedCache();

            //no ForceSaveVibranceSettings here, serializing the whole file inside the foreground callback is
            //exactly what must not happen. Losing the flag on a crash costs one more "(?)" on the next start
            if (!settingsBackgroundWorker.IsBusy)
            {
                settingsBackgroundWorker.RunWorkerAsync();
            }
        }

        private void InitializeApplicationList()
        {
            if (this.listApplications.LargeImageList == null)
            {
                ImageList imageList = new ImageList();
                imageList.ImageSize = new Size(48, 48);
                imageList.ColorDepth = ColorDepth.Depth32Bit;
                this.listApplications.LargeImageList = imageList;
                ListViewItem_SetSpacing(this.listApplications, 48 + 24, 48 + 6 + 16);
            }
        }

        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public int MakeLong(short lowPart, short highPart)
        {
            return (int)(((ushort)lowPart) | (uint)(highPart << 16));
        }

        public void ListViewItem_SetSpacing(ListView listview, short leftPadding, short topPadding)
        {
            const int LVM_FIRST = 0x1000;
            const int LVM_SETICONSPACING = LVM_FIRST + 53;
            SendMessage(listview.Handle, LVM_SETICONSPACING, IntPtr.Zero, (IntPtr)MakeLong(leftPadding, topPadding));
        }

        private void listApplications_DoubleClick(object sender, EventArgs e)
        {
            ListViewItem selectedItem = this.listApplications.SelectedItems[0];
            if (selectedItem != null)
            {
                //captured before the dialog runs, "Change executable..." is allowed to replace the path
                string originalFileName = selectedItem.Tag.ToString();
                ApplicationSetting actualSetting = _applicationSettings.FirstOrDefault(x => x.FileName == originalFileName);
                VibranceSettings settingsWindow = new VibranceSettings(_v, _minTrackBarValue, _maxTrackBarValue, _defaultIngameValue, selectedItem, actualSetting, _supportedResolutionList, _graphicsAdapter);
                DialogResult result = settingsWindow.ShowDialog();
                if (result == DialogResult.OK)
                {
                    ApplicationSetting newSetting = settingsWindow.GetApplicationSetting();
                    RemoveSettingByFileName(originalFileName);
                    RemoveSettingByFileName(newSetting.FileName);
                    _applicationSettings.Add(newSetting);
                    if (!string.Equals(originalFileName, newSetting.FileName, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedItem.Tag = newSetting.FileName;
                        ReplaceApplicationListItemIcon(selectedItem, newSetting.FileName);
                    }
                    ApplyApplicationListItemAppearance(selectedItem, newSetting);
                    RefreshUnconfirmedCache();
                    ForceSaveVibranceSettings();
                }
                else if (actualSetting == null)
                {
                    removeApplicationListItem(selectedItem);
                }
            }
        }

        /// <summary>
        /// Repaints the icon of an item whose executable was changed. Uses the indexer of the image
        /// collection on purpose: RemoveAt plus Add would shift every following image index and silently
        /// break both the removal and the icon of the per-game dialog.
        /// </summary>
        private void ReplaceApplicationListItemIcon(ListViewItem lvi, string fileName)
        {
            try
            {
                Icon icon = Icon.ExtractAssociatedIcon(fileName);
                if (icon != null)
                {
                    this.listApplications.LargeImageList.Images[lvi.ImageIndex] = icon.ToBitmap();
                }
            }
            catch (Exception ex)
            {
                //the executable is already saved, keeping the old icon is the only thing left to get wrong
                Log(ex);
            }
        }

        private void buttonRemoveProgram_Click(object sender, EventArgs e)
        {
            foreach (ListViewItem eachItem in listApplications.SelectedItems)
            {
                for (int i = eachItem.Index + 1; i < listApplications.Items.Count; i++)
                    listApplications.Items[i].ImageIndex--;

                removeApplicationListItem(eachItem);
                _applicationSettings.Remove(_applicationSettings.FirstOrDefault(x => x.FileName.Equals(eachItem.Tag.ToString())));
            }

            RefreshUnconfirmedCache();
            ForceSaveVibranceSettings();
        }

        private void removeApplicationListItem(ListViewItem item)
        {
            Image img = this.listApplications.LargeImageList.Images[item.ImageIndex];
            this.listApplications.LargeImageList.Images.RemoveAt(item.ImageIndex);
            img.Dispose();
            this.listApplications.Items.Remove(item);
        }

        private void buttonProcessExplorer_Click(object sender, EventArgs e)
        {
            ProcessExplorer ex = new ProcessExplorer(this);
            ex.Show();
        }

        private void buttonFindGames_Click(object sender, EventArgs e)
        {
            //modal on purpose, the finder holds its close back while its scan is still running
            using (GameFinder finder = new GameFinder(_applicationSettings))
            {
                if (finder.ShowDialog(this) == DialogResult.OK)
                {
                    AddProgramsBulk(finder.GetSelectedCandidates());
                }
            }
        }
    }
}