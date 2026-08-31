using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using vibrance.GUI.common.gamefinder;

namespace vibrance.GUI.common
{
    public partial class GameFinder : Form
    {
        /// <summary>
        /// Runtime matching is by basename only (NvidiaDynamicVibranceProxy), so a second entry whose
        /// basename is already in use can never activate. Adding it produces a profile that is dead on
        /// arrival, so such a row is shown as "Already added (name in use)" and cannot be checked.
        /// Set to false to veto that second rule and match on the full executable path only.
        /// </summary>
        private static readonly bool TreatNameCollisionAsAlreadyAdded = true;

        // The listView is laid out at the designer's 144 DPI; WinForms scales the control but not the
        // column widths, so the columns are added in Load and scaled by hand. These are 144 DPI units.
        private const int DesignListViewWidth = 1434;
        private const int DesignColumnGame = 390;
        private const int DesignColumnStore = 105;
        private const int DesignColumnExecutable = 390;
        private const int DesignColumnLocation = 300;
        private const int DesignColumnStatus = 210;
        private const int DesignIconSize = 24;

        // Snapshots of the caller's settings, taken once on the UI thread. The scan worker never
        // touches them, and nothing here ever holds on to an ApplicationSetting.
        private readonly HashSet<string> _existingFileNames;
        private readonly HashSet<string> _existingNames;

        private readonly List<GameCandidate> _selectedCandidates = new List<GameCandidate>();

        private int _foundCount;
        private int _skippedCount;
        private int _alreadyAddedCount;
        private int _errorCount;

        private bool _suspendCountRefresh;
        private bool _closeRequested;
        private DialogResult _pendingDialogResult = DialogResult.Cancel;

        // existingSettings is read ONLY in the constructor, on the UI thread, into a snapshot.
        // The scan worker never touches it.
        public GameFinder(List<ApplicationSetting> existingSettings)
        {
            InitializeComponent();

            _existingFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (existingSettings != null)
            {
                foreach (ApplicationSetting setting in existingSettings)
                {
                    if (setting == null)
                    {
                        continue;
                    }
                    if (!string.IsNullOrEmpty(setting.FileName))
                    {
                        _existingFileNames.Add(setting.FileName);
                    }
                    if (!string.IsNullOrEmpty(setting.Name))
                    {
                        _existingNames.Add(setting.Name);
                    }
                }
            }
        }

        // Valid only after ShowDialog() returned DialogResult.OK.
        // Contains only rows the user left checked; never an already-added row.
        public List<GameCandidate> GetSelectedCandidates()
        {
            return new List<GameCandidate>(_selectedCandidates);
        }

        private void GameFinder_Load(object sender, EventArgs e)
        {
            SetupColumns();
            StartScan();
        }

        /// <summary>
        /// Adds the columns and sizes the icons for the DPI the form was actually scaled to. Runs in
        /// Load rather than in the constructor because AutoScaleMode.Dpi resizes the controls after
        /// the constructor has returned.
        /// </summary>
        private void SetupColumns()
        {
            float scale = 1f;
            if (listViewGames.Width > 0)
            {
                scale = listViewGames.Width / (float)DesignListViewWidth;
            }

            // ImageSize must be set while the list is still empty; assigning it clears the images.
            int iconSize = ScaleValue(DesignIconSize, scale);
            iconList.ImageSize = new Size(iconSize, iconSize);

            listViewGames.Columns.Add("Game", ScaleValue(DesignColumnGame, scale), HorizontalAlignment.Left);
            listViewGames.Columns.Add("Store", ScaleValue(DesignColumnStore, scale), HorizontalAlignment.Left);
            listViewGames.Columns.Add("Executable", ScaleValue(DesignColumnExecutable, scale), HorizontalAlignment.Left);
            listViewGames.Columns.Add("Location", ScaleValue(DesignColumnLocation, scale), HorizontalAlignment.Left);
            listViewGames.Columns.Add("Status", ScaleValue(DesignColumnStatus, scale), HorizontalAlignment.Left);
        }

        private static int ScaleValue(int designValue, float scale)
        {
            int value = (int)Math.Round(designValue * scale);
            return value < 1 ? 1 : value;
        }

        private void StartScan()
        {
            if (backgroundWorker.IsBusy)
            {
                return;
            }

            listViewGames.BeginUpdate();
            try
            {
                listViewGames.Items.Clear();
                iconList.Images.Clear();
            }
            finally
            {
                listViewGames.EndUpdate();
            }

            _selectedCandidates.Clear();
            _foundCount = 0;
            _skippedCount = 0;
            _alreadyAddedCount = 0;
            _errorCount = 0;

            SetScanRunningUi(true);
            labelProgress.Text = "Scanning your game libraries...";
            RefreshCounts();

            backgroundWorker.RunWorkerAsync();
        }

        private void SetScanRunningUi(bool running)
        {
            buttonRescan.Enabled = !running;
            buttonRescan.Text = running ? "Scanning..." : "Rescan";
        }

        #region worker thread

        private void backgroundWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            GameScanContext context = new GameScanContext(
                WorkerIsCancelled, WorkerReportCandidate, WorkerReportSkipped, WorkerReportError);

            GameFinderScanner scanner = new GameFinderScanner(GameFinderScanner.CreateDefaultSources());
            scanner.Scan(context);

            if (backgroundWorker.CancellationPending)
            {
                e.Cancel = true;
            }
        }

        private bool WorkerIsCancelled()
        {
            return backgroundWorker.CancellationPending;
        }

        private void WorkerReportCandidate(GameCandidate candidate)
        {
            if (candidate == null || backgroundWorker.CancellationPending)
            {
                return;
            }

            if (candidate.Icon == null)
            {
                candidate.Icon = TryExtractIcon(candidate.ExecutablePath);
            }

            backgroundWorker.ReportProgress(0, candidate);
        }

        private void WorkerReportSkipped(string gameName, string reason)
        {
            SafeLog(string.Format("Game finder skipped \"{0}\": {1}", gameName, reason));
            backgroundWorker.ReportProgress(0, new ScanNotice(false));
        }

        private void WorkerReportError(string sourceName, Exception ex)
        {
            SafeLog(string.Format("Game finder error in source \"{0}\"", sourceName));
            if (ex != null)
            {
                SafeLog(ex);
            }
            backgroundWorker.ReportProgress(0, new ScanNotice(true));
        }

        /// <summary>
        /// Icon.ExtractAssociatedIcon throws on paths it cannot read and returns null for some others.
        /// A game without a picture is still a game, so every failure here is swallowed.
        /// </summary>
        private static Icon TryExtractIcon(string executablePath)
        {
            try
            {
                if (string.IsNullOrEmpty(executablePath) || !File.Exists(executablePath))
                {
                    return null;
                }
                return Icon.ExtractAssociatedIcon(executablePath);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// VibranceGUI.Log opens the log file with File.AppendText, which throws when the UI thread is
        /// writing at the same moment. A lost log line must never abort a scan.
        /// </summary>
        private static void SafeLog(string message)
        {
            try
            {
                VibranceGUI.Log(message);
            }
            catch (Exception)
            {
            }
        }

        private static void SafeLog(Exception ex)
        {
            try
            {
                VibranceGUI.Log(ex);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// A skip or an error travelling from the worker to the UI thread through ReportProgress.
        /// </summary>
        private class ScanNotice
        {
            private readonly bool _isError;

            public ScanNotice(bool isError)
            {
                _isError = isError;
            }

            public bool IsError
            {
                get { return _isError; }
            }
        }

        #endregion

        #region ui thread

        private void backgroundWorker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            if (IsDisposed || Disposing)
            {
                return;
            }

            GameCandidate candidate = e.UserState as GameCandidate;
            if (candidate != null)
            {
                AddCandidateRow(candidate);
                return;
            }

            ScanNotice notice = e.UserState as ScanNotice;
            if (notice == null)
            {
                return;
            }

            if (notice.IsError)
            {
                _errorCount++;
            }
            else
            {
                _skippedCount++;
            }
            RefreshCounts();
        }

        private void backgroundWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            SetScanRunningUi(false);

            if (e.Error != null)
            {
                SafeLog(e.Error);
                labelProgress.Text = "The scan failed. See vibranceGUI.log for the details.";
            }
            else if (e.Cancelled || _closeRequested)
            {
                labelProgress.Text = "Scan cancelled. " + BuildSummary();
            }
            else if (_foundCount == 0)
            {
                // Deliberately does not enumerate sources by name - that list has already gone
                // stale once (this used to say "Only Steam and Epic", years after EA, Battle.net,
                // Rockstar, Ubisoft and the Uninstall registry all shipped). A general description
                // cannot go stale the same way a specific one inevitably does as sources are added.
                labelProgress.Text = "No games found. vibranceGUI scans installed games and Start Menu/desktop shortcuts.";
            }
            else
            {
                labelProgress.Text = BuildSummary();
            }

            RefreshCounts();

            if (_closeRequested)
            {
                // The close attempt that started the cancellation was vetoed in FormClosing, which
                // resets DialogResult to None. Restore what the user actually asked for and re-close.
                this.DialogResult = _pendingDialogResult;
                Close();
            }
        }

        private string BuildSummary()
        {
            string summary = string.Format("Found {0} {1}", _foundCount, _foundCount == 1 ? "game" : "games");
            if (_skippedCount > 0)
            {
                summary += string.Format(" ({0} skipped)", _skippedCount);
            }
            summary += ".";

            if (_alreadyAddedCount > 0)
            {
                summary += string.Format(" {0} already added.", _alreadyAddedCount);
            }
            if (_errorCount > 0)
            {
                summary += string.Format(" {0} {1}, see vibranceGUI.log.",
                    _errorCount, _errorCount == 1 ? "error" : "errors");
            }
            return summary;
        }

        private void AddCandidateRow(GameCandidate candidate)
        {
            MarkAlreadyAdded(candidate);

            ListViewItem lvi = new ListViewItem(candidate.GameName, AddIcon(candidate.Icon));
            lvi.Tag = candidate;
            lvi.SubItems.Add(GetStoreName(candidate.Source));
            lvi.SubItems.Add(GetExecutableText(candidate));
            lvi.SubItems.Add(candidate.InstallDirectory ?? string.Empty);
            lvi.SubItems.Add(GetStatusText(candidate));
            lvi.ToolTipText = BuildToolTip(candidate);

            if (candidate.IsAlreadyAdded)
            {
                lvi.ForeColor = SystemColors.GrayText;
                _alreadyAddedCount++;
            }
            else
            {
                // Pre-check BEFORE the item joins the ListView: a detached item raises no ItemCheck, so
                // the already-added guard in listViewGames_ItemCheck cannot undo the default selection.
                lvi.Checked = true;
            }

            _foundCount++;
            listViewGames.Items.Add(lvi);
            RefreshCounts();
        }

        /// <summary>
        /// Returns the ImageList index for the candidate's icon, or -1 for a row that will be drawn
        /// without one.
        /// </summary>
        private int AddIcon(Icon icon)
        {
            if (icon == null)
            {
                return -1;
            }
            try
            {
                iconList.Images.Add(icon);
                return iconList.Images.Count - 1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>
        /// Both definitions of "already added", computed against the constructor snapshot: an exact
        /// executable path match, and a basename collision with an existing entry's Name.
        /// </summary>
        private void MarkAlreadyAdded(GameCandidate candidate)
        {
            candidate.IsAlreadyAdded = false;
            candidate.AlreadyAddedReason = null;

            if (!string.IsNullOrEmpty(candidate.ExecutablePath) &&
                _existingFileNames.Contains(candidate.ExecutablePath))
            {
                candidate.IsAlreadyAdded = true;
                candidate.AlreadyAddedReason = "Already added";
                return;
            }

            if (!TreatNameCollisionAsAlreadyAdded)
            {
                return;
            }

            string baseName = SafeGetFileNameWithoutExtension(candidate.ExecutablePath);
            if (baseName.Length > 0 && _existingNames.Contains(baseName))
            {
                candidate.IsAlreadyAdded = true;
                candidate.AlreadyAddedReason = "Already added (name in use)";
            }
        }

        private static string GetStoreName(GameSource source)
        {
            switch (source)
            {
                case GameSource.Epic:
                    return "Epic Games";
                case GameSource.Ea:
                    return "EA";
                case GameSource.BattleNet:
                    return "Battle.net";
                case GameSource.Rockstar:
                    return "Rockstar";
                case GameSource.Ubisoft:
                    return "Ubisoft";
                case GameSource.OtherLauncher:
                    // The Uninstall registry knows the publisher, not the store the user bought it
                    // from. "Installed" says only what was actually observed.
                    return "Installed";
                case GameSource.Shortcut:
                    // Not a store at all - a Start Menu or desktop .lnk. "Shortcut" says only
                    // what was actually observed, same reasoning as "Installed" above.
                    return "Shortcut";
                default:
                    return "Steam";
            }
        }

        /// <summary>
        /// The executable filename, with the same "(?)" marker the main window uses for an entry whose
        /// executable was inferred rather than named by the store.
        /// </summary>
        private static string GetExecutableText(GameCandidate candidate)
        {
            string fileName = SafeGetFileName(candidate.ExecutablePath);
            return candidate.Confidence == ExecutableConfidence.Guessed ? fileName + " (?)" : fileName;
        }

        private static string GetStatusText(GameCandidate candidate)
        {
            if (candidate.IsAlreadyAdded)
            {
                return string.IsNullOrEmpty(candidate.AlreadyAddedReason)
                    ? "Already added"
                    : candidate.AlreadyAddedReason;
            }
            return candidate.Confidence == ExecutableConfidence.Guessed ? "Best guess" : "From store";
        }

        private static string BuildToolTip(GameCandidate candidate)
        {
            string tip = candidate.GameName + Environment.NewLine + candidate.ExecutablePath;

            if (candidate.IsAlreadyAdded)
            {
                tip += Environment.NewLine + "This game is already in your list.";
            }
            else if (candidate.Confidence == ExecutableConfidence.Guessed)
            {
                // Was Steam-specific text until StartMenuShortcutSource also started reporting
                // Guessed rows: a shortcut names one exact file, but nothing curates it the way a
                // store's own metadata does, so it is no more certain than Steam's ranked guess.
                tip += Environment.NewLine
                    + "This source does not say for certain which file starts this game, so this "
                    + "is the most likely one. You can change it later from the game's settings.";
            }
            return tip;
        }

        private static string SafeGetFileName(string path)
        {
            try
            {
                return string.IsNullOrEmpty(path) ? string.Empty : Path.GetFileName(path);
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
        }

        private static string SafeGetFileNameWithoutExtension(string path)
        {
            try
            {
                return string.IsNullOrEmpty(path) ? string.Empty : Path.GetFileNameWithoutExtension(path);
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
        }

        private void RefreshCounts()
        {
            if (_suspendCountRefresh)
            {
                return;
            }

            int checkedCount = listViewGames.CheckedItems.Count;

            labelCounts.Text = string.Format("{0} of {1} selected", checkedCount, listViewGames.Items.Count);
            buttonAddSelected.Text = string.Format("Add selected ({0})", checkedCount);
            buttonAddSelected.Enabled = checkedCount > 0;

            labelSkipped.Text = _skippedCount == 0
                ? string.Empty
                : string.Format("{0} {1} skipped - see vibranceGUI.log for the reason.",
                    _skippedCount, _skippedCount == 1 ? "entry" : "entries");

            if (backgroundWorker.IsBusy && !backgroundWorker.CancellationPending)
            {
                labelProgress.Text = string.Format("Scanning your game libraries... {0} found so far.", _foundCount);
            }
        }

        /// <summary>
        /// WinForms cannot disable a single item's checkbox, so an already-added row vetoes the change
        /// instead. This also covers "Select all", which goes through the same event.
        /// </summary>
        private void listViewGames_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            GameCandidate candidate = listViewGames.Items[e.Index].Tag as GameCandidate;
            if (candidate != null && candidate.IsAlreadyAdded)
            {
                e.NewValue = e.CurrentValue;
            }
        }

        private void listViewGames_ItemChecked(object sender, ItemCheckedEventArgs e)
        {
            RefreshCounts();
        }

        private void buttonSelectAll_Click(object sender, EventArgs e)
        {
            SetAllChecked(true);
        }

        private void buttonSelectNone_Click(object sender, EventArgs e)
        {
            SetAllChecked(false);
        }

        private void SetAllChecked(bool value)
        {
            _suspendCountRefresh = true;
            listViewGames.BeginUpdate();
            try
            {
                foreach (ListViewItem lvi in listViewGames.Items)
                {
                    lvi.Checked = value;
                }
            }
            finally
            {
                listViewGames.EndUpdate();
                _suspendCountRefresh = false;
            }
            RefreshCounts();
        }

        private void buttonRescan_Click(object sender, EventArgs e)
        {
            StartScan();
        }

        private void buttonAddSelected_Click(object sender, EventArgs e)
        {
            BuildSelection();

            // Setting DialogResult ends the modal loop through FormClosing, which holds the close back
            // while a scan is still running. No per-game dialog is opened here: the caller receives the
            // whole selection at once from GetSelectedCandidates().
            this.DialogResult = DialogResult.OK;
            Close();
        }

        private void BuildSelection()
        {
            _selectedCandidates.Clear();
            foreach (ListViewItem lvi in listViewGames.CheckedItems)
            {
                GameCandidate candidate = lvi.Tag as GameCandidate;
                if (candidate == null || candidate.IsAlreadyAdded)
                {
                    continue;
                }
                _selectedCandidates.Add(candidate);
            }
        }

        private void buttonCancel_Click(object sender, EventArgs e)
        {
            // While a scan runs, Cancel stops the scan and leaves the window open with what was found
            // so far; otherwise it closes the window.
            if (backgroundWorker.IsBusy)
            {
                CancelScan();
                return;
            }

            this.DialogResult = DialogResult.Cancel;
            Close();
        }

        private void CancelScan()
        {
            if (!backgroundWorker.CancellationPending)
            {
                backgroundWorker.CancelAsync();
            }
            labelProgress.Text = "Cancelling the scan...";
        }

        /// <summary>
        /// A BackgroundWorker still running when the form disposes fires ReportProgress into a disposed
        /// control, so the first close attempt during a scan only asks the worker to stop; the close is
        /// repeated from RunWorkerCompleted.
        /// </summary>
        private void GameFinder_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!backgroundWorker.IsBusy)
            {
                return;
            }

            if (_closeRequested)
            {
                // Second attempt: the worker is not stopping and the user is insisting. Let the window
                // go, but detach the callbacks first so a late report cannot reach a disposed control.
                backgroundWorker.ProgressChanged -= backgroundWorker_ProgressChanged;
                backgroundWorker.RunWorkerCompleted -= backgroundWorker_RunWorkerCompleted;
                return;
            }

            _pendingDialogResult = this.DialogResult == DialogResult.None
                ? DialogResult.Cancel
                : this.DialogResult;
            _closeRequested = true;
            e.Cancel = true;
            CancelScan();
        }

        #endregion
    }
}
