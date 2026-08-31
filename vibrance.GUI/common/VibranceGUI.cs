using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using vibrance.GUI.common.gamefinder;
using Application = System.Windows.Forms.Application;
using MessageBox = System.Windows.Forms.MessageBox;

namespace vibrance.GUI.common
{
    /// <summary>
    /// One application list row's decided appearance - the same three properties
    /// ApplyApplicationListItemAppearance writes onto a ListViewItem (Text/ForeColor/
    /// ToolTipText), plus the Tag (ApplicationSetting.FileName) that says WHICH row it belongs
    /// to. Exists so ResolveListItemAppearances below can hand back a whole repaint decision as
    /// plain data - the same reason ProfileToggleDecision sits beside ProfileToggleHelper in
    /// that file. A small struct rather than a tuple: no tuples in this codebase (C# 6 / .NET
    /// Framework 4.0).
    /// </summary>
    internal struct ApplicationListItemAppearance
    {
        internal string Tag;
        internal string Text;
        internal Color ForeColor;
        internal string ToolTip;
    }

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
        private const string TwitterLink = "https://x.com/swatx18";

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

        // Debounces which SystemEvents.DisplaySettingsChanged notifications are allowed to reach
        // RebuildWindowsResolutionSettings(false) at all - see ResolutionAdoptionDebouncer.cs for
        // the full reasoning (upstream defect: a game changing the resolution itself, with no
        // vibranceGUI profile applied, could get captured as "the user's desktop resolution").
        // Constructed once, here, rather than lazily, so OnDisplaySettingsChanged never has to
        // null-check it.
        private readonly ResolutionAdoptionDebouncer _resolutionAdoptionDebouncer =
            new ResolutionAdoptionDebouncer(new FormsResolutionAdoptionTimer());

        private readonly bool _isForcedExecution;

        // Kept in sync with _applicationSettings by RefreshUnconfirmedCache. The foreground handler runs on
        // the ui thread for every window switch, so it tests this boolean before it looks at anything else
        private bool _hasUnconfirmedEntries;
        private bool _isForegroundConfirmationSubscribed;

        // internal (not private): DescribeListItem's own checks in ProfileToggleFixture compare
        // against these constants directly, rather than hardcoding a second copy of the prose that
        // could quietly drift out of sync with what actually ships.
        internal const string ToolTipExecutableUnconfirmed =
            "Not detected yet. vibranceGUI has not seen this executable in the foreground, so this may be the wrong file. Double-click to change the executable.";
        internal const string UnconfirmedMarkerSuffix = " (?)";

        // The toggle hotkey's own marker (see DescribeListItem below ApplyApplicationListItemAppearance).
        // A live status the user just set by pressing a key, not a data-quality warning like the
        // "(?)" pair above - it gets its own suffix, tooltip and colour rather than reusing
        // GrayText, which already means "this entry is broken", not "this is switched off".
        internal const string SuppressedMarkerSuffix = " (Off)";
        internal const string ToolTipSuppressed =
            "This profile is switched off - vibranceGUI stays at your Windows level while this game is in the foreground. Press the toggle hotkey again to switch it back on.";
        // Color.DarkOrange itself measures 2.33:1 against the ListView's white background (WCAG AA
        // wants 4.5:1; the GrayText this replaces is 5.17:1). This shade measures 4.78:1 and still
        // reads as the same orange. The suffix text carries the state on its own regardless, so
        // this was never information conveyed by colour alone - just worth being legible.
        internal static readonly Color SuppressedForeColor = Color.FromArgb(192, 80, 0);

        // WM_HOTKEY (winuser.h) - WndProc below dispatches on this with wParam ==
        // HotkeyRegistration.HotkeyId, the toggle hotkey's own fixed registration id.
        private const int WmHotkey = 0x0312;

        // RegisterHotKey/WndProc, never a low-level keyboard hook - see HotkeyRegistration's own
        // header comment for why. Constructed with the real registrar; ProfileToggleFixture
        // drives HotkeyRegistration directly against a fake instead of through this form at all.
        private readonly HotkeyRegistration _hotkeyRegistration = new HotkeyRegistration(new RealHotkeyRegistrar());
        private readonly IForegroundWindowReader _foregroundWindowReader = new RealForegroundWindowReader();
        private HotkeyBinding _toggleBinding = HotkeyBinding.None;

        // The checkbox's own state - the binding's presence is deliberately NOT the enable flag
        // here (unlike the discarded global-pause design): a per-game toggle is significant
        // enough, and a mis-hit hotkey costly enough (it suppresses a specific game's profile),
        // that turning it on is its own explicit step. ApplyToggleHotkey only ever registers a
        // real binding when this is true AND _toggleBinding.IsSet.
        private bool _toggleHotkeyEnabled;

        // Guards the one-time balloon ApplyToggleHotkey raises for a registration failure that
        // was not caused by an interactive user action (i.e. the settings-read registration
        // point) - without this, a binding that keeps failing (another application owns it) would
        // re-balloon on every settings reload.
        private bool _hasShownHotkeyFailureBalloon;

        // Set around the ReadVibranceSettings-time "checkBoxToggleHotkeyEnabled.Checked = ..."
        // assignment (below) so its own CheckedChanged handler - when the stored value happens to
        // differ from the designer default and the setter actually raises the event - does not
        // re-persist the exact value it was just given. Not needed for correctness (writing the
        // same value back is harmless), only to avoid an INI write on every single startup.
        private bool _isLoadingToggleHotkeyEnabled;

        // Same one-set-per-key dedup convention as NvidiaDynamicVibranceProxy's own
        // _loggedDisplayFailures/LogDisplayFailureOnce - a no-op toggle press (no configured game
        // in the foreground, or the engine is not ready yet) logs once per distinct process name,
        // not once per press, so leaning on the key (even with MOD_NOREPEAT, which only throttles
        // WM_HOTKEY's own repeat rate, not repeated presses) cannot spam vibranceGUI.log.
        private readonly HashSet<string> _loggedNoOpTogglePresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

        /// <summary>
        /// The first of the toggle hotkey's four registration points - see HotkeyRegistration's
        /// own header comment and ApplyToggleHotkey below for the rest. The handle exists here
        /// even under "-minimized" (SetVisibleCore above calls CreateHandle() when !_allowVisible),
        /// but the binding itself is never set yet at this point (ReadVibranceSettings has not run)
        /// - this call always returns NotConfigured without ever reaching RegisterHotKey.
        /// </summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyToggleHotkey(false);
        }

        /// <summary>
        /// One of the three layers that guarantee the toggle hotkey is unregistered - see
        /// HotkeyRegistration's own header comment. Runs before the handle is actually destroyed,
        /// which is what lets Release() unregister against the still-valid handle it cached at
        /// registration time (see Release's own comment on why it never reads a fresh one).
        /// </summary>
        protected override void OnHandleDestroyed(EventArgs e)
        {
            _hotkeyRegistration.Release();
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey && m.WParam == (IntPtr)HotkeyRegistration.HotkeyId)
            {
                OnToggleHotkeyPressed();
            }
            base.WndProc(ref m);
        }

        /// <summary>
        /// Closes the real gap textBoxToggleHotkey's own Enter/Leave pair leaves open, measured
        /// directly rather than assumed: Leave does NOT fire when another window takes activation
        /// while the textbox still has focus, nor on Hide() (the minimise-to-tray path), nor on
        /// WindowState = Minimized, nor on Close() with the textbox focused - only a focus change
        /// to a SIBLING control raises it. Concretely: open settings, click the capture box to
        /// look at the current binding (Enter releases it), then alt-tab away or minimise to tray
        /// - without this override the hotkey stays unregistered for the rest of the session,
        /// silently, while labelToggleHotkeyStatus keeps claiming "Hotkey registered.".
        /// ApplyToggleHotkey(false), not (true): a deactivating form must not write the inline
        /// status label - showInline is for a live edit, and this form is not being edited when
        /// something else takes activation out from under it.
        /// </summary>
        protected override void OnDeactivate(EventArgs e)
        {
            if (ShouldReleaseHotkeyOnFocusTransition(this.ActiveControl, this.textBoxToggleHotkey))
            {
                ApplyToggleHotkey(false);
            }
            base.OnDeactivate(e);
        }

        /// <summary>
        /// The other half of OnDeactivate above - also measured directly: after deactivate then
        /// reactivate (or Hide() then Show()), textBoxToggleHotkey.Enter does NOT fire again,
        /// because ActiveControl never actually changed. Re-applying on deactivation without
        /// re-releasing here on activation would leave the hotkey live while the capture box has
        /// focus, reopening the exact rebinding defect (PR #153's third one) the Enter/Leave pair
        /// exists to fix in the first place.
        /// </summary>
        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (ShouldReleaseHotkeyOnFocusTransition(this.ActiveControl, this.textBoxToggleHotkey))
            {
                _hotkeyRegistration.Release();
            }
        }

        /// <summary>
        /// The condition both OnDeactivate and OnActivated above share, pulled out so
        /// ProfileToggleFixture can call it directly (same assembly, internal) rather than
        /// reflecting into a live Form - which this codebase deliberately never constructs in a
        /// self test (VibranceGUI's own constructor calls getProxy(...), touching a real vendor
        /// proxy). Pure: no WinForms focus system involved, just the one comparison both
        /// overrides need to agree on.
        /// </summary>
        internal static bool ShouldReleaseHotkeyOnFocusTransition(Control activeControl, Control toggleHotkeyTextBox)
        {
            return activeControl == toggleHotkeyTextBox;
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
                this.checkBoxToggleHotkeyEnabled.Enabled = flag;
                // AND'd with the checkbox's own state, not just flag alone - otherwise this
                // would force the capture controls back on even while the user has left the
                // checkbox unchecked (see checkBoxToggleHotkeyEnabled_CheckedChanged).
                this.textBoxToggleHotkey.Enabled = flag && _toggleHotkeyEnabled;
                this.buttonClearToggleHotkey.Enabled = flag && _toggleHotkeyEnabled;
            });
        }

        // ------------------------------------------------------------------
        // Toggle hotkey (upstream #143) - a global RegisterHotKey binding that flips the
        // foreground game's profile between its game level and the Windows level. See
        // HotkeyRegistration/HotkeyBinding/IHotkeyRegistrar for the seams this wiring drives, and
        // ProfileToggleFixture for their regression coverage.
        // ------------------------------------------------------------------

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);

        private const int VkLWin = 0x5B;
        private const int VkRWin = 0x5C;

        // KeyEventArgs exposes Control/Alt/Shift directly (e.Control/e.Alt/e.Shift) but has no
        // equivalent for the Windows key - Control.ModifierKeys does not cover it either. This is
        // the same "high bit of GetKeyState" read Windows itself uses to answer "is this key down
        // right now", scoped to just the two Win virtual-key codes.
        private static bool IsWinKeyDown()
        {
            return (GetKeyState(VkLWin) & 0x8000) != 0 || (GetKeyState(VkRWin) & 0x8000) != 0;
        }

        /// <summary>
        /// Applies _toggleBinding against the form's own handle - see the class-level comment
        /// above for the four points this is called from, and HotkeyRegistration.Apply for the
        /// release-then-register contract underneath it. showInline routes a non-Registered
        /// result to the settings-window status label (a live edit) instead of a one-time tray
        /// balloon (an unattended registration point, e.g. the settings-read call site).
        /// </summary>
        private HotkeyRegistrationResult ApplyToggleHotkey(bool showInline)
        {
            // _v.GetVibranceInfo().isInitialized settles once, during the proxy's own
            // constructor, and never flips back - a proxy that failed to initialize (bad driver,
            // etc.) never will. Registering a hotkey the engine can never act on would still
            // intercept that key combination system-wide, stealing it from a game that might
            // legitimately want it, for a feature that can only ever no-op (see
            // OnToggleHotkeyPressed's own guard) - not worth it just because a binding happens to
            // be configured.
            if (this.IsDisposed || !this.IsHandleCreated || _v == null || !_v.GetVibranceInfo().isInitialized)
            {
                return HotkeyRegistrationResult.NotConfigured;
            }

            // The checkbox gates registration, not just the presence of a saved binding - a
            // binding can be fully configured (and shown in the textbox) while the checkbox is
            // still unchecked, and must register nothing until the user turns it on.
            // HotkeyRegistration.EffectiveBinding is the real gate, called from here rather than
            // inlined, so a fixture that cannot instantiate this Form still reaches the actual
            // expression production code runs, not a copy of it.
            HotkeyBinding effective = HotkeyRegistration.EffectiveBinding(_toggleHotkeyEnabled, _toggleBinding);
            HotkeyRegistrationResult result = _hotkeyRegistration.Apply(this.Handle, effective);

            if (showInline)
            {
                ApplyToggleHotkeyStatusLabel(result);
            }
            else if ((result == HotkeyRegistrationResult.AlreadyOwnedByAnotherApplication ||
                result == HotkeyRegistrationResult.Failed) && !_hasShownHotkeyFailureBalloon)
            {
                _hasShownHotkeyFailureBalloon = true;
                this.notifyIcon.BalloonTipIcon = ToolTipIcon.Warning;
                this.notifyIcon.BalloonTipText = result == HotkeyRegistrationResult.AlreadyOwnedByAnotherApplication
                    ? string.Format("Could not register the toggle hotkey ({0}) - it is already in use by another application.", HotkeyBindingParser.Format(_toggleBinding))
                    : string.Format("Could not register the toggle hotkey ({0}).", HotkeyBindingParser.Format(_toggleBinding));
                this.notifyIcon.ShowBalloonTip(250);
            }

            return result;
        }

        /// <summary>
        /// The inline, synchronous feedback ApplyToggleHotkey(showInline: true) shows next to the
        /// textbox. A successfully registered binding with no real modifier bit set (only
        /// MOD_NOREPEAT, which is never user-visible - see HotkeyBindingParser) still shows a
        /// warning instead of the plain success text: it is a legal binding, but one that steals
        /// the key from the game system-wide the moment it is bound.
        /// </summary>
        private void ApplyToggleHotkeyStatusLabel(HotkeyRegistrationResult result)
        {
            switch (result)
            {
                case HotkeyRegistrationResult.NotConfigured:
                    this.labelToggleHotkeyStatus.ForeColor = SystemColors.ControlText;
                    this.labelToggleHotkeyStatus.Text = string.Empty;
                    return;
                case HotkeyRegistrationResult.AlreadyOwnedByAnotherApplication:
                    this.labelToggleHotkeyStatus.ForeColor = Color.Red;
                    this.labelToggleHotkeyStatus.Text = "Already in use by another application.";
                    return;
                case HotkeyRegistrationResult.Failed:
                    this.labelToggleHotkeyStatus.ForeColor = Color.Red;
                    this.labelToggleHotkeyStatus.Text = "Could not register this hotkey.";
                    return;
            }

            if ((_toggleBinding.Modifiers & ~HotkeyBindingParser.ModNoRepeat) == 0)
            {
                this.labelToggleHotkeyStatus.ForeColor = Color.DarkOrange;
                this.labelToggleHotkeyStatus.Text = "No modifier: steals the key from the game.";
                return;
            }

            this.labelToggleHotkeyStatus.ForeColor = Color.Green;
            this.labelToggleHotkeyStatus.Text = "Hotkey registered.";
        }

        /// <summary>
        /// Persists _toggleBinding's canonical text on its own, single-key write - deliberately
        /// not routed through ForceSaveVibranceSettings/SaveVibranceSettings' debounced,
        /// 8-parameter round trip, which this feature has nothing to do with.
        /// </summary>
        private void SaveToggleHotkeySetting()
        {
            try
            {
                new SettingsController().SetToggleHotkey(HotkeyBindingParser.Format(_toggleBinding));
            }
            catch (Exception ex)
            {
                Log(ex);
            }
        }

        /// <summary>
        /// Persists the checkbox's own checked state - the same single-key write shape as
        /// SaveToggleHotkeySetting beside it, and for the same reason not routed through
        /// ForceSaveVibranceSettings/SaveVibranceSettings.
        /// </summary>
        private void SaveToggleHotkeyEnabledSetting()
        {
            try
            {
                new SettingsController().SetToggleHotkeyEnabled(_toggleHotkeyEnabled);
            }
            catch (Exception ex)
            {
                Log(ex);
            }
        }

        /// <summary>
        /// Disables (never hides) the capture controls when unchecked - PR #153 hides them
        /// instead, which makes the layout jump and conceals the parked key combination from a
        /// user who might just want to glance at what is currently bound.
        /// </summary>
        private void checkBoxToggleHotkeyEnabled_CheckedChanged(object sender, EventArgs e)
        {
            _toggleHotkeyEnabled = this.checkBoxToggleHotkeyEnabled.Checked;
            this.textBoxToggleHotkey.Enabled = _toggleHotkeyEnabled;
            this.buttonClearToggleHotkey.Enabled = _toggleHotkeyEnabled;

            if (_isLoadingToggleHotkeyEnabled)
            {
                // ReadVibranceSettings' own explicit ApplyToggleHotkey(false) call is what applies
                // this on load - see its comment. Saving here too would just write back the exact
                // value this handler was given, on every single startup.
                return;
            }

            SaveToggleHotkeyEnabledSetting();
            ApplyToggleHotkey(true);
        }

        // Releases the live registration the moment the textbox gains focus, so the CURRENT
        // binding stops intercepting keystrokes meant for this field - closes PR #153's third
        // defect (you could not rebind to anything containing the key combination already bound,
        // because pressing it fired WM_HOTKEY - and toggled the engine - instead of the textbox's
        // own KeyDown).
        private void textBoxToggleHotkey_Enter(object sender, EventArgs e)
        {
            _hotkeyRegistration.Release();
        }

        // The other half of the same fix: re-applies _toggleBinding on the way out, whether or
        // not KeyDown below ever actually changed it - if the user just clicked in and back out
        // again with no key pressed, Enter's Release() above would otherwise leave the ORIGINAL
        // binding unregistered with nothing left to restore it.
        private void textBoxToggleHotkey_Leave(object sender, EventArgs e)
        {
            ApplyToggleHotkey(true);
        }

        private void textBoxToggleHotkey_KeyDown(object sender, KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;

            Keys keyCode = e.KeyCode;
            // A bare modifier press (Ctrl/Alt/Shift/Win alone, before the real key follows) is
            // not a complete binding yet - wait for the key that follows it instead of parsing
            // "Ctrl" on its own.
            if (keyCode == Keys.ControlKey || keyCode == Keys.Menu || keyCode == Keys.ShiftKey ||
                keyCode == Keys.LWin || keyCode == Keys.RWin)
            {
                return;
            }

            List<string> parts = new List<string>();
            if (e.Control) parts.Add("Ctrl");
            if (e.Alt) parts.Add("Alt");
            if (e.Shift) parts.Add("Shift");
            if (IsWinKeyDown()) parts.Add("Win");
            parts.Add(keyCode.ToString());

            HotkeyBinding parsedBinding;
            if (!HotkeyBindingParser.TryParse(string.Join("+", parts.ToArray()), out parsedBinding))
            {
                // A token this handler itself just built (a real Keys.KeyCode name) should never
                // fail to parse - defensive only. Leaves the field showing whatever was bound
                // before, rather than clearing a working binding over a key this widget cannot
                // recognise.
                return;
            }

            _toggleBinding = parsedBinding;
            this.textBoxToggleHotkey.Text = HotkeyBindingParser.Format(_toggleBinding);
            SaveToggleHotkeySetting();
            ApplyToggleHotkey(true);
        }

        private void buttonClearToggleHotkey_Click(object sender, EventArgs e)
        {
            _toggleBinding = HotkeyBinding.None;
            this.textBoxToggleHotkey.Text = string.Empty;
            SaveToggleHotkeySetting();
            ApplyToggleHotkey(true);
        }

        /// <summary>
        /// The WM_HOTKEY handler WndProc dispatches to. Guarded the same way the trackbar/checkbox
        /// handlers above are (see e.g. checkBoxPrimaryMonitorOnly_CheckedChanged): _v can be null,
        /// or not yet initialized, for the whole span between the handle existing and
        /// backgroundWorker_DoWork actually finishing - a hotkey press in that window is a no-op,
        /// not a null-reference crash. A failed foreground read (_foregroundWindowReader) is the
        /// same kind of no-op - nothing to name in a log line, so nothing is logged for it either.
        /// </summary>
        private void OnToggleHotkeyPressed()
        {
            if (_v == null || !_v.GetVibranceInfo().isInitialized)
            {
                return;
            }

            IntPtr hWnd;
            string processName;
            string processImagePath;
            if (!_foregroundWindowReader.TryGetForeground(out hWnd, out processName, out processImagePath))
            {
                return;
            }

            ProfileToggleResult result = _v.ToggleForegroundProfile(hWnd, processName, processImagePath);
            RefreshToggledListItemAppearance(result, processName, processImagePath);
            ApplyProfileToggleFeedback(result, processName, hWnd);
        }

        /// <summary>
        /// Keeps the marker DescribeListItem draws in sync with a CONFIRMED toggle -
        /// ToggledOn/ToggledOff only, the two outcomes where ProfileToggleHelper.SetSuppressed
        /// actually ran (see both proxies' own ToggleForegroundProfile; WriteFailed/None/
        /// EngineNotReady never flip it, so nothing needs redrawing for them).
        ///
        /// Runs unconditionally here, whether or not the window is visible right now -
        /// ListViewItem.Text/ForeColor/ToolTipText are ordinary properties that keep their value
        /// while hidden, so applying it now is what makes the marker already correct by the time
        /// notifyIcon_MouseClick later reveals the window. Deliberately the only refresh seam:
        /// a second one on the show path would only be able to drift out of sync with this one.
        ///
        /// FindMatch is re-run with the exact (processName, processImagePath) ToggleForegroundProfile's
        /// own Decide just used. Usually the identical _applicationSettings reference, so it
        /// resolves to the identical setting - the one exception is the narrow startup window
        /// between ReadVibranceSettings reassigning this field via "out" and SetApplicationSettings
        /// handing the proxy that same new list, where the proxy's own Decide still runs against
        /// the constructor's original (empty) list. That never actually mismatches the setting
        /// resolved here: an empty list can only make Decide return None, and
        /// ShouldRefreshListItemForToggleResult below already excludes that outcome, so this
        /// method never gets past its own gate while the two references disagree.
        ///
        /// The repaint itself is keyed by Name, not by the one setting FindMatch happened to
        /// resolve: ProfileToggleHelper's suppression set is keyed by Name (see its own comment),
        /// so a single hotkey press can suppress every ApplicationSetting that shares that Name at
        /// once - two entries whose executables merely happen to share a bare file name, e.g.
        /// D:\A\game.exe and D:\B\game.exe (both "game" via VibranceSettings.
        /// resolveApplicationName), toggle together. ResolveListItemAppearances below is the
        /// whole decision - which of those rows actually has a live ListViewItem AND what each
        /// one should become - so what is left here is deliberately mechanical: collect the tags
        /// currently on screen (the one input a fixture cannot supply, see
        /// GetApplicationListItemTags), hand them off with the toggled Name, then for each
        /// appearance that comes back, look the row up by its Tag and assign the three properties
        /// already decided for it. A row whose tag was not on screen never appears in that list at
        /// all - see ResolveListItemAppearances' own comment for why that is not an error: the
        /// item picks up the current suppression state on its own the moment
        /// ApplyApplicationListItemAppearance creates it, because that method reads
        /// ProfileToggleHelper.IsSuppressed fresh every time rather than from a snapshot. The null
        /// check below is a defensive mirror of that same fact (the ListView could only fall out
        /// of sync with the tag set gathered a few lines above via reentrancy, which nothing on
        /// this UI-thread-only path does), not a second policy.
        /// </summary>
        private void RefreshToggledListItemAppearance(ProfileToggleResult result, string processName, string processImagePath)
        {
            if (!ShouldRefreshListItemForToggleResult(result))
            {
                return;
            }

            ApplicationSetting setting = ApplicationSettingMatcher.FindMatch(_applicationSettings, processName, processImagePath);
            if (setting == null)
            {
                return;
            }

            List<ApplicationListItemAppearance> appearances =
                ResolveListItemAppearances(_applicationSettings, setting.Name, GetApplicationListItemTags());
            for (int i = 0; i < appearances.Count; i++)
            {
                ApplicationListItemAppearance appearance = appearances[i];
                ListViewItem lvi = FindApplicationListItem(appearance.Tag);
                if (lvi != null)
                {
                    lvi.Text = appearance.Text;
                    lvi.ForeColor = appearance.ForeColor;
                    lvi.ToolTipText = appearance.ToolTip;
                }
            }
        }

        /// <summary>
        /// The set of tags (ApplicationSetting.FileName) currently on screen in listApplications -
        /// the one input ResolveListItemAppearances needs that a fixture cannot supply on its own,
        /// because it comes from a live ListView.Items and this codebase has no Form-free way to
        /// build one (VibranceGUI's own constructor calls getProxy(...)). Mirrors
        /// FindApplicationListItem's own comparison exactly - same null guard on Tag, same
        /// StringComparison.OrdinalIgnoreCase (via StringComparer.OrdinalIgnoreCase here) - so the
        /// two can never disagree about which tags exist.
        /// </summary>
        private HashSet<string> GetApplicationListItemTags()
        {
            HashSet<string> tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ListViewItem lvi in this.listApplications.Items)
            {
                if (lvi.Tag != null)
                {
                    tags.Add(lvi.Tag.ToString());
                }
            }

            return tags;
        }

        /// <summary>
        /// The decision behind RefreshToggledListItemAppearance's repaint: every ApplicationSetting
        /// in settings whose Name matches name, compared through ProfileToggleHelper.NameComparer -
        /// the exact comparer the suppression set itself is keyed by, so this can never select a
        /// different set of rows than the set of profiles whose suppression state the hotkey just
        /// changed. See ProfileToggleHelper's own field comment for why two settings CAN share one
        /// Name, and why that makes this a list rather than a single match.
        ///
        /// No device, no Screen, no ListView - a List&lt;ApplicationSetting&gt; in, the matching
        /// subset out, so ProfileToggleFixture can pin this without constructing a real Form (its
        /// constructor calls getProxy(...)), the same reason DescribeListItem above is a pure
        /// static rather than inlined into ApplyApplicationListItemAppearance.
        /// </summary>
        internal static List<ApplicationSetting> FindApplicationSettingsByName(List<ApplicationSetting> settings, string name)
        {
            List<ApplicationSetting> matches = new List<ApplicationSetting>();
            if (settings == null || string.IsNullOrEmpty(name))
            {
                return matches;
            }

            for (int i = 0; i < settings.Count; i++)
            {
                ApplicationSetting setting = settings[i];
                if (setting != null && ProfileToggleHelper.NameComparer.Equals(setting.Name, name))
                {
                    matches.Add(setting);
                }
            }

            return matches;
        }

        /// <summary>
        /// The whole repaint decision behind RefreshToggledListItemAppearance - not just which
        /// settings share toggledName, but, for each one, whether a live row exists for it AT ALL
        /// and, if so, exactly what that row should become. availableTags is the set of tags
        /// (ApplicationSetting.FileName) currently on screen - the same identity
        /// FindApplicationListItem itself looks items up by (see that method and
        /// AddApplicationListItem, which sets lvi.Tag = fileName) - so a setting that matches by
        /// Name but has no ListViewItem yet produces NO entry, rather than a placeholder: this is
        /// the lvi == null case RefreshToggledListItemAppearance used to test inline, now decided
        /// here instead. A row that does have a live item gets the same three properties
        /// ApplyApplicationListItemAppearance would write, computed through the same
        /// DescribeListItem this class already uses for a freshly created item - not a second,
        /// hand-copied version of that decision.
        ///
        /// No device, no Screen, no ListView - a List&lt;ApplicationSetting&gt;, a name and a set
        /// of tags in, the appearances out - so ProfileToggleFixture can pin the real repaint
        /// decision (which rows change AND what each one becomes) without constructing a real
        /// Form. What is deliberately NOT covered by pinning this method alone is availableTags
        /// itself, which has to come from a live listApplications.Items (see
        /// GetApplicationListItemTags) - and the two-line lookup-then-assign in
        /// RefreshToggledListItemAppearance that turns a returned Tag back into a ListViewItem.
        /// Neither of those is a decision; both are exercised for real only by constructing an
        /// actual Form, which nothing in this fixture does.
        /// </summary>
        internal static List<ApplicationListItemAppearance> ResolveListItemAppearances(
            List<ApplicationSetting> settings, string toggledName, HashSet<string> availableTags)
        {
            List<ApplicationListItemAppearance> appearances = new List<ApplicationListItemAppearance>();
            List<ApplicationSetting> toRepaint = FindApplicationSettingsByName(settings, toggledName);
            for (int i = 0; i < toRepaint.Count; i++)
            {
                ApplicationSetting matched = toRepaint[i];
                if (string.IsNullOrEmpty(matched.FileName) || availableTags == null || !availableTags.Contains(matched.FileName))
                {
                    continue;
                }

                ApplicationListItemAppearance appearance = new ApplicationListItemAppearance();
                appearance.Tag = matched.FileName;
                DescribeListItem(matched.Name, matched.IsExecutableUnconfirmed, ProfileToggleHelper.IsSuppressed(matched.Name),
                    out appearance.Text, out appearance.ForeColor, out appearance.ToolTip);
                appearances.Add(appearance);
            }

            return appearances;
        }

        /// <summary>
        /// The gate RefreshToggledListItemAppearance opens on - pulled out, same reason as
        /// DescribeListItem above, so ProfileToggleFixture pins the real condition instead of a
        /// hand-copied mirror of it. True only for the two outcomes that actually flip
        /// ProfileToggleHelper's suppression set (see both proxies' own ToggleForegroundProfile).
        /// </summary>
        internal static bool ShouldRefreshListItemForToggleResult(ProfileToggleResult result)
        {
            return result == ProfileToggleResult.ToggledOn || result == ProfileToggleResult.ToggledOff;
        }

        /// <summary>
        /// Everything ToggleForegroundProfile's single return value drives, in one place - the
        /// tray presentation derives from this one function, not a second .ico that does not
        /// exist. A no-op result (no configured game in the foreground, or the engine is not
        /// ready yet) is deliberately silent - no balloon, no sound - so a hotkey pressed while
        /// browsing the desktop does not interrupt anything; it is still logged once per distinct
        /// process name, so "why didn't my hotkey do anything" is answerable from the log without
        /// needing a UI signal that would otherwise fire on every ordinary alt-tab. foregroundWindow
        /// is resolved to a device name only in the WriteFailed case, which is the only one that
        /// needs it - Screen.FromHandle is a real Win32 call, not worth paying on every no-op
        /// press (by far the most common outcome: everything that is not a configured game).
        /// </summary>
        private void ApplyProfileToggleFeedback(ProfileToggleResult result, string processName, IntPtr foregroundWindow)
        {
            switch (result)
            {
                case ProfileToggleResult.NoConfiguredGameInForeground:
                    LogNoOpToggleOnce(processName, string.Format(
                        "Toggle hotkey pressed while \"{0}\" was in the foreground, which has no configured profile - ignored.", processName));
                    return;
                case ProfileToggleResult.EngineNotReady:
                    LogNoOpToggleOnce(processName, string.Format(
                        "Toggle hotkey pressed while \"{0}\" was in the foreground, but vibranceGUI has not finished starting up yet - ignored.", processName));
                    return;
                case ProfileToggleResult.WriteFailed:
                    string deviceName = Screen.FromHandle(foregroundWindow).DeviceName;
                    this.notifyIcon.BalloonTipIcon = ToolTipIcon.Warning;
                    this.notifyIcon.BalloonTipText = string.Format("Could not toggle \"{0}\"'s profile on display {1}.", processName, deviceName);
                    this.notifyIcon.ShowBalloonTip(250);
                    return;
            }

            bool toggledOn = result == ProfileToggleResult.ToggledOn;

            // Deliberately does NOT write notifyIcon.Text: a per-game "vibranceGUI - X OFF" tray
            // tooltip has nothing that ever resets it, so it would keep asserting X's state long
            // after X exits (or after a different game takes the foreground). The balloon below
            // is the right place for "X just toggled" - it is transient by nature, so it cannot
            // go stale the way a durable tray tooltip would.
            this.notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
            this.notifyIcon.BalloonTipText = toggledOn
                ? string.Format("\"{0}\"'s profile is running again.", processName)
                : string.Format("\"{0}\"'s profile is suppressed - back at your Windows level until toggled again.", processName);
            this.notifyIcon.ShowBalloonTip(250);

            // Ship it, on, no setting - SystemSounds respects whatever sound scheme (including
            // "No Sounds") the user already has picked in Windows, so the opt-out already exists
            // at OS level. Plays asynchronously, never blocks WndProc.
            //
            // NOT Exclamation/Asterisk: checked against this machine's actual sound scheme via
            // the registry (HKCU\AppEvents\Schemes\Apps\.Default\<Event>\.Current) rather than
            // assumed, and SystemAsterisk and SystemExclamation both resolve to the exact same
            // file ("Windows Background.wav") there - two "distinct" sounds that would actually
            // be identical, exactly the failure mode to check for before shipping this. Hand
            // resolves to a different file ("Windows Foreground.wav") on the same machine, so
            // Hand/Asterisk is the pair actually used here. Re-verify on the machine this ships
            // to if the two ever sound the same again - schemes vary.
            if (toggledOn)
            {
                SystemSounds.Asterisk.Play();
            }
            else
            {
                SystemSounds.Hand.Play();
            }
        }

        private void LogNoOpToggleOnce(string processName, string message)
        {
            string key = processName ?? string.Empty;
            if (_loggedNoOpTogglePresses.Add(key))
            {
                Program.LogSafely(message);
            }
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
                // Unsubscribing SystemEvents above stops any NEW DisplaySettingsChanged from
                // reaching the debouncer, but a countdown it already armed keeps ticking down on
                // its own regardless - this stops one already in flight from firing after the form
                // is disposed (see ResolutionAdoptionDebouncer.Cancel's own comment).
                _resolutionAdoptionDebouncer.Cancel();
                // One of the three layers that guarantee the toggle hotkey is unregistered - see
                // HotkeyRegistration's own header comment. Idempotent: OnHandleDestroyed (below,
                // via the form's own Dispose chain) may already have done this.
                _hotkeyRegistration.Release();
            }
        }

        // Single source of truth for "is a vibranceGUI apply currently outstanding" - both
        // RebuildWindowsResolutionSettings and OnDisplaySettingsChanged need this exact same read
        // of _v, at two different call sites, and a hand-copied second expression is exactly what
        // lets the two silently drift apart if only one is ever updated (a review nitpick on this
        // branch, before the duplication was extracted). _v is null until the constructor's
        // getProxy call returns, well after RebuildWindowsResolutionSettings(true)'s own initial
        // call - this property already handles that the same way the old inline expression did.
        private bool IsResolutionChangeCurrentlyApplied
        {
            get { return _v != null && _v.GetVibranceInfo().isResolutionChangeApplied; }
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
            bool preserveCapturedMode = IsResolutionChangeCurrentlyApplied;

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
            //
            // Routed through _resolutionAdoptionDebouncer rather than called directly - see
            // ResolutionAdoptionDebouncer.cs for why: an immediate call here is exactly what let a
            // game's own resolution change (fired with no vibranceGUI profile applied) get captured
            // as "the user's desktop resolution". IsResolutionChangeCurrentlyApplied is read fresh,
            // right here, for this specific notification - it is what the debouncer uses to decide
            // whether this particular refresh needs debouncing at all, or can run immediately.
            bool preserveCapturedMode = IsResolutionChangeCurrentlyApplied;
            _resolutionAdoptionDebouncer.OnDisplaySettingsChanged(preserveCapturedMode, delegate
            {
                // Re-checked here, not just by the caller above: this may run later, from the
                // debounce timer's own callback, well after this OnDisplaySettingsChanged call has
                // returned - CleanUp() cancels any pending countdown in its own finally block, but a
                // Tick already queued on the UI thread's message loop the instant CleanUp runs could
                // still reach here, exactly the race IsDisposed/IsHandleCreated already guard
                // against at the top of this method.
                if (this.IsDisposed || !this.IsHandleCreated)
                {
                    return;
                }
                RebuildWindowsResolutionSettings(false);
            });
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

        // Both overloads now delegate to LogSink.Current instead of opening
        // %APPDATA%\vibranceGUI\vibranceGUI.log directly - see ILogSink.cs. This facade's own
        // signature is unchanged, so none of the existing VibranceGUI.Log call sites across the
        // rest of this codebase need to change; only RealLogSink (the default LogSink.Current)
        // still touches that file, byte for byte identically to what this method used to do
        // itself.
        public static void Log(Exception ex)
        {
            LogSink.Current.Write(ex);
        }

        public static void Log(string msg)
        {
            LogSink.Current.Write(msg);
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

                // The real registration point for the toggle hotkey - OnHandleCreated's own call
                // always finds _toggleBinding still at HotkeyBinding.None (this is the first time
                // it is ever read from disk). backgroundWorker_DoWork busy-waits on
                // "!IsHandleCreated" and only tests InvokeRequired afterward (see its own comment),
                // so the handle is guaranteed to already exist on every path that reaches here -
                // the InvokeRequired-without-a-handle trap this.IsHandleCreated guards elsewhere in
                // this class cannot bite in this block.
                HotkeyBinding parsedToggleBinding;
                _toggleBinding = HotkeyBindingParser.TryParse(settingsController.ReadToggleHotkey(), out parsedToggleBinding)
                    ? parsedToggleBinding
                    : HotkeyBinding.None;
                textBoxToggleHotkey.Text = HotkeyBindingParser.Format(_toggleBinding);

                _toggleHotkeyEnabled = settingsController.ReadToggleHotkeyEnabled();
                // Setting Checked to a value equal to its current (designer-default, unchecked)
                // value does not raise CheckedChanged at all - the explicit ApplyToggleHotkey(false)
                // call below is what actually applies it on that common path, not this line. When
                // the stored value IS true, the setter does raise it - _isLoadingToggleHotkeyEnabled
                // is what keeps that from writing the same value straight back to the INI.
                _isLoadingToggleHotkeyEnabled = true;
                checkBoxToggleHotkeyEnabled.Checked = _toggleHotkeyEnabled;
                _isLoadingToggleHotkeyEnabled = false;
                textBoxToggleHotkey.Enabled = _toggleHotkeyEnabled;
                buttonClearToggleHotkey.Enabled = _toggleHotkeyEnabled;
                ApplyToggleHotkey(false);

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
        /// Renders both markers an application list item can carry. Only the display text is
        /// decorated, never ApplicationSetting.Name, which is the key the foreground process name
        /// is compared against. Reads ProfileToggleHelper.IsSuppressed fresh on every call, never
        /// from a cached flag - see RefreshToggledListItemAppearance's own comment for why that is
        /// what makes an item created AFTER a suppression already correct, with no separate
        /// refresh needed.
        /// </summary>
        private void ApplyApplicationListItemAppearance(ListViewItem lvi, ApplicationSetting setting)
        {
            if (lvi == null || setting == null)
            {
                return;
            }

            string text;
            Color foreColor;
            string toolTip;
            DescribeListItem(setting.Name, setting.IsExecutableUnconfirmed, ProfileToggleHelper.IsSuppressed(setting.Name),
                out text, out foreColor, out toolTip);

            lvi.Text = text;
            lvi.ForeColor = foreColor;
            lvi.ToolTipText = toolTip;
        }

        /// <summary>
        /// The pure decision behind an application list item's marker - name suffix, colour and
        /// tooltip - given both states an item can be in at once. Pulled out of
        /// ApplyApplicationListItemAppearance, the one place that ever writes ListViewItem.Text/
        /// ForeColor/ToolTipText, for the same reason HotkeyRegistration.EffectiveBinding and
        /// ClearSuppressionIfNameChanged below are pulled out of their own callers: a fixture
        /// cannot construct a real Form (the constructor calls getProxy(...)), so a check against
        /// a hand-copied mirror of this logic would not be pinning the real gate at all.
        ///
        /// isUnconfirmed and isSuppressed are orthogonal - a guessed executable can be switched
        /// off, or a confirmed one can be - so a doubly-marked item shows BOTH suffixes and BOTH
        /// tooltip paragraphs; neither ever silently wins over the other. Suppression is the live
        /// fact ("this is what the profile is doing RIGHT NOW") and takes the colour when both
        /// apply - GrayText already means "this entry might be broken", the wrong message to give
        /// a profile the user just switched off on purpose.
        /// </summary>
        internal static void DescribeListItem(string name, bool isUnconfirmed, bool isSuppressed,
            out string text, out Color foreColor, out string toolTip)
        {
            text = name ?? string.Empty;
            List<string> toolTipParts = new List<string>();

            if (isSuppressed)
            {
                text += SuppressedMarkerSuffix;
                toolTipParts.Add(ToolTipSuppressed);
            }

            if (isUnconfirmed)
            {
                text += UnconfirmedMarkerSuffix;
                toolTipParts.Add(ToolTipExecutableUnconfirmed);
            }

            foreColor = isSuppressed
                ? SuppressedForeColor
                : (isUnconfirmed ? SystemColors.GrayText : SystemColors.WindowText);
            toolTip = string.Join(Environment.NewLine + Environment.NewLine, toolTipParts.ToArray());
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
                    // "Change executable..." (or any edit that changes Name - Name is
                    // Path.GetFileNameWithoutExtension of the executable, VibranceSettings.
                    // resolveApplicationName) moves this profile off the Name the toggle hotkey's
                    // suppression set is keyed by.
                    ClearSuppressionIfNameChanged(actualSetting, newSetting.Name);
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
                ApplicationSetting removedSetting = _applicationSettings.FirstOrDefault(x => x.FileName.Equals(eachItem.Tag.ToString()));
                if (removedSetting != null)
                {
                    ClearSuppressionIfNameChanged(removedSetting, null);
                    _applicationSettings.Remove(removedSetting);
                }
            }

            RefreshUnconfirmedCache();
            ForceSaveVibranceSettings();
        }

        /// <summary>
        /// Clears any toggle-hotkey suppression recorded under oldSetting's own Name whenever
        /// this profile is removed outright (newName: null) or edited such that Name no longer
        /// matches (e.g. "Change executable..."). Without this, a stale suppression under the OLD
        /// Name would silently apply to whatever unrelated profile happens to get that Name later
        /// (e.g. two different games both shipping a "launcher.exe") - with no action from that
        /// user and nothing in the UI explaining why it starts at the Windows level instead of
        /// its own. Extracted so ProfileToggleFixture can call it directly (same assembly,
        /// internal) instead of reflecting into either private UI handler.
        /// </summary>
        internal static void ClearSuppressionIfNameChanged(ApplicationSetting oldSetting, string newName)
        {
            if (oldSetting != null && !string.Equals(oldSetting.Name, newName, StringComparison.OrdinalIgnoreCase))
            {
                ProfileToggleHelper.SetSuppressed(oldSetting.Name, false);
            }
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