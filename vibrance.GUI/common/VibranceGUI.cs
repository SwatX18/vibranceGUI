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
using Microsoft.Win32;
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

        // Last captured Windows mode per device, INCLUDING devices no longer attached - the fallback
        // WindowsResolutionRefresher needs when a monitor that vanished during one refresh comes back
        // during a later one while a game's resolution change is still applied. Deliberately holds only
        // the mode (five uints), never the supported-mode list, so a device this never forgets costs a
        // single small object; the OS's own \\.\DISPLAYn namespace bounds how many there can ever be.
        // Note: \\.\DISPLAYn identifies a PORT, not a monitor's identity, so unplugging the monitor
        // that was there and plugging a different one into the same port while preserveCapturedMode
        // is true can hand the new monitor the old one's retained mode - harm bounded, since a
        // revert only ever targets _gameScreen's own device and CDS_TEST would reject a mode the new
        // monitor cannot actually support.
        private readonly Dictionary<string, ResolutionModeWrapper> _lastKnownWindowsModes =
            new Dictionary<string, ResolutionModeWrapper>();

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
            RebuildWindowsResolutionSettings(true);

            // _supportedResolutionList is readonly, so it can only be assigned from inside a
            // constructor body - not from a method the constructor merely calls, even a private
            // one - which is why this is pulled back out of RebuildWindowsResolutionSettings
            // (shared with the refresh path below) instead of living inside it. Equivalent to the
            // old "if (screen.Primary) { _supportedResolutionList = availableResolutions; }": if
            // the primary screen's own read failed, it never made it into the dictionary either,
            // and this is left null exactly as it was before.
            Screen primaryScreen = Screen.PrimaryScreen;
            Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>> primaryEntry;
            if (primaryScreen != null && _windowsResolutionSettings.TryGetValue(primaryScreen.DeviceName, out primaryEntry))
            {
                _supportedResolutionList = primaryEntry.Item2;
            }

            // Subscribed here - after _windowsResolutionSettings exists, before getProxy hands it
            // to the vendor proxy - and unsubscribed in CleanUp(). ResolutionChangeFailed lets the
            // resolution-change fix (see ResolutionHelper.ChangeResolutionEx) report a give-up
            // without ever showing a MessageBox from inside the WinEvent callback thread.
            // SystemEvents.DisplaySettingsChanged keeps _windowsResolutionSettings from going stale
            // when the user changes their desktop resolution directly in Windows - unsubscribing it
            // is mandatory (not just good practice): SystemEvents holds a strong reference to this
            // handler on its own dedicated thread, and leaving it subscribed leaks this form and can
            // fault at shutdown.
            ResolutionHelper.ResolutionChangeFailed += OnResolutionChangeFailed;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

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
            finally
            {
                // In a finally, not just after the try: these must run even if the block above
                // throws. SystemEvents.DisplaySettingsChanged above all - see the ctor's own
                // comment for why leaving it subscribed leaks this form and can fault at shutdown.
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                ResolutionHelper.ResolutionChangeFailed -= OnResolutionChangeFailed;
            }
        }

        /// <summary>
        /// (Re)populates _windowsResolutionSettings from the currently attached screens by
        /// delegating to WindowsResolutionRefresher.Refresh, which mutates the existing Dictionary
        /// instance in place (Clear() then re-add) rather than replacing it - both proxies hold a
        /// reference to this very instance (NVIDIA's is static), so only in-place mutation is
        /// visible to them. See WindowsResolutionRefresher.cs for the full extraction, including
        /// the single most dangerous line in the resolution-change fix (preserving Item1 while a
        /// game's own resolution change is applied) and the retained-last-known-mode fallback for
        /// a device that reattaches after dropping out of a refresh. Shared by the constructor
        /// (showFailureDialog: true) and OnDisplaySettingsChanged below (showFailureDialog: false)
        /// - see that method for why the dialog must never fire from the refresh path.
        /// </summary>
        private void RebuildWindowsResolutionSettings(bool showFailureDialog)
        {
            bool preserveCapturedMode = _v != null && _v.GetVibranceInfo().isResolutionChangeApplied;

            List<string> attachedDeviceNames = new List<string>();
            foreach (Screen screen in Screen.AllScreens)
            {
                attachedDeviceNames.Add(screen.DeviceName);
            }

            WindowsResolutionRefresher.Refresh(
                _windowsResolutionSettings,
                _lastKnownWindowsModes,
                attachedDeviceNames,
                preserveCapturedMode,
                showFailureDialog,
                ShowResolutionReadFailureDialog);
        }

        // The text is unchanged from the inline MessageBox.Show call this replaces - the
        // deviceName parameter is deliberately unused in the message itself. It exists so
        // WindowsResolutionRefresher (and ResolutionChangeFixture, which drives it with no
        // MessageBox anywhere in the process) can assert *which* device's unreadable current mode
        // triggered the callback, without this dialog starting to name devices it never has before.
        private static void ShowResolutionReadFailureDialog(string deviceName)
        {
            MessageBox.Show("Current resolution mode could not be determined. Switching back to your Windows resolution will not work.");
        }

        /// <summary>
        /// Keeps _windowsResolutionSettings current when the user changes their desktop resolution
        /// (or a monitor is hot-plugged) outside of vibranceGUI itself - without this, the revert
        /// path drags the desktop back to whatever mode was active at startup, with every API call
        /// still reporting success.
        /// </summary>
        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            // Raised on SystemEvents' own dedicated thread, not necessarily the UI thread that owns
            // this form and that OnWinEventHook's callbacks arrive through - marshal onto it before
            // touching _windowsResolutionSettings, which that hook handler reads with no locking of
            // its own. InvokeRequired is NOT sufficient on its own: it returns false whenever the
            // form has no window handle yet (Control.InvokeRequired falls through to
            // FindMarshalingControl(), which returns false if !IsHandleCreated) - and the handle
            // genuinely does not exist for the whole span of the constructor's NvAPI/ADL
            // initialisation after this handler is subscribed (backgroundWorker_DoWork busy-waits
            // on !IsHandleCreated), which is exactly when a SystemEvents notification is likely at
            // autostart, as monitors settle. Without the explicit IsHandleCreated check this method
            // would run its dictionary mutation directly on the SystemEvents thread in that window.
            // IsDisposed also guards the symmetric case at shutdown: CleanUp()'s unsubscribe cannot
            // cover a notification already in flight, which could otherwise find the handle
            // destroyed (same wrong-thread mutation) or the form disposed (BeginInvoke throwing
            // ObjectDisposedException on the SystemEvents thread, with nothing there to catch it).
            if (this.IsDisposed || !this.IsHandleCreated)
            {
                return;
            }
            if (this.InvokeRequired)
            {
                this.BeginInvoke((MethodInvoker)delegate { OnDisplaySettingsChanged(sender, e); });
                return;
            }

            // showFailureDialog: false - a MessageBox popping up on every hot-plug or resolution
            // change, potentially over a fullscreen game, is exactly the modal-on-the-callback-
            // thread mistake this whole fix removes. The constructor's own one-time build above
            // still shows it once, at startup, where the user is looking at the window and can act
            // on it immediately.
            RebuildWindowsResolutionSettings(false);
        }

        /// <summary>
        /// Reports a resolution change ChangeResolutionEx has given up on, via a balloon tip instead
        /// of the modal MessageBox the pre-fix code raised from inside the WinEvent callback thread
        /// (see ResolutionHelper.cs). ResolutionHelper only ever raises this once it has given up -
        /// never on a single transient failure - so there is no "still retrying" wording here; see
        /// ResolutionHelper.RecordFailure.
        /// </summary>
        private void OnResolutionChangeFailed(object sender, ResolutionFailureEventArgs e)
        {
            // Same reasoning as OnDisplaySettingsChanged above - this is also raised from
            // ResolutionHelper's own call stack, which for the WinEvent-driven cases below runs on
            // the UI thread already, but ResolutionHelper offers no guarantee of that in general.
            if (this.IsDisposed || !this.IsHandleCreated)
            {
                return;
            }
            if (this.InvokeRequired)
            {
                this.BeginInvoke((MethodInvoker)delegate { OnResolutionChangeFailed(sender, e); });
                return;
            }

            this.notifyIcon.BalloonTipIcon = ToolTipIcon.Warning;
            // The desktop is now stuck at the game's resolution (revert) or the game never got its
            // requested resolution (apply) with nothing else in the program that will ever retry
            // it, so this has to name the device and, for a revert, point the user at where they
            // can fix it themselves.
            this.notifyIcon.BalloonTipText = e.IsRevert
                ? string.Format("vibranceGUI could not switch display {0} back to your Windows resolution and has stopped trying. Check Windows Display settings.", e.DeviceName)
                : string.Format("vibranceGUI could not change display {0} to this game's resolution and has stopped trying.", e.DeviceName);
            this.notifyIcon.ShowBalloonTip(250);
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
                //the constructor above knows nothing about these two, they have to be assigned afterwards.
                //The install directory is resolved through junctions here and only here - Steam libraries
                //are junctioned often enough that a stored D:\Games\... would never prefix match the path
                //Windows reports for the running game, and the foreground callback is no place for io
                string installDirectory = PathResolver.ResolveFinalDirectoryPath(candidate.InstallDirectory);
                //an installer is free to write "C:\Program Files (x86)" as its InstallLocation, and storing
                //that would match nearly every program on the machine. Dropping it costs this entry its
                //directory match and leaves it matching by name, which is what it did before this feature
                setting.InstallDirectory = ApplicationSettingMatcher.IsSharedProgramDirectory(installDirectory)
                    ? null
                    : installDirectory;
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

        //held in a field rather than written at the call site, so that the foreground callback does not
        //allocate a delegate on every window switch
        private static readonly Func<ApplicationSetting, bool> UnconfirmedOnly =
            delegate(ApplicationSetting setting) { return setting.IsExecutableUnconfirmed; };

        /// <summary>
        /// Clears the marker of a guessed executable the first time the game it belongs to really shows up
        /// in the foreground. Runs on the ui thread inside the foreground callback, at the moment a game
        /// goes fullscreen, so the common path is one boolean test and the save is left to the debounced
        /// worker.
        /// The same match rule as the proxies use, deliberately: the marker warns that a guessed
        /// executable might never activate the profile, and a process running from under the install
        /// directory is proof that it does - whichever of the folder's executables Windows put in front.
        /// </summary>
        private void OnForegroundChangedConfirmExecutable(object sender, WinEventHookEventArgs e)
        {
            if (!_hasUnconfirmedEntries)
            {
                return;
            }

            ApplicationSetting setting = ApplicationSettingMatcher.FindMatch(
                _applicationSettings, e.ProcessName, e.ProcessImagePath, UnconfirmedOnly);
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
            //ListView raises DoubleClick for the whole control, empty space included, where
            //SelectedItems is empty and the indexer below would throw ArgumentOutOfRangeException.
            if (this.listApplications.SelectedItems.Count == 0)
                return;

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