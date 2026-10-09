using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using vibrance.GUI.common.gamefinder;

namespace vibrance.GUI.common
{
    // File half of the "sync CS2 video settings" option (issue #61): writes a game profile's
    // resolution and refresh rate into every Steam account's cs2_video.txt, so CS2 starts at the
    // mode vibranceGUI is about to set rather than fighting it. Cs2VideoConfigPatcher does the text
    // edit; this class finds the files and writes them safely.
    //
    // Safety rules, in order of how much they matter:
    //   - never while cs2.exe runs (the game rewrites the file on exit and would undo or corrupt us);
    //   - never create a file: an account without 730\local\cfg\cs2_video.txt has never run CS2;
    //   - never clear a read-only attribute - the user set it on purpose to pin the file;
    //   - one backup per file (cs2_video.txt.vibrance.bak), made before the first write and never
    //     overwritten, so it always holds the user's original. cs2_video.txt.bak is Valve's own
    //     and is never read, written or deleted;
    //   - the new content goes to a temp file and File.Replace swaps it in, so a failure leaves the
    //     old file intact. No delete+move fallback: if Replace fails the account is reported, not risked.
    // Apply never throws; per-account failures are reported in the result.

    internal enum Cs2VideoAccountStatus
    {
        Updated,
        AlreadyCurrent,
        ReadOnly,
        Locked,
        Malformed
    }

    internal sealed class Cs2VideoAccountOutcome
    {
        internal string AccountId { get; set; }
        internal string FilePath { get; set; }
        internal Cs2VideoAccountStatus Status { get; set; }
        internal string Detail { get; set; }

        internal bool IsSuccess
        {
            get { return Status == Cs2VideoAccountStatus.Updated || Status == Cs2VideoAccountStatus.AlreadyCurrent; }
        }
    }

    internal enum Cs2VideoSyncStatus
    {
        Completed,
        Cs2Running,
        NoResolution,
        SteamNotFound,
        NoUserData,
        NoAccountHasFile,
        Failed
    }

    internal sealed class Cs2VideoSyncResult
    {
        private readonly List<Cs2VideoAccountOutcome> _accounts = new List<Cs2VideoAccountOutcome>();

        internal Cs2VideoSyncStatus Status { get; set; }

        // Never null.
        internal List<Cs2VideoAccountOutcome> Accounts { get { return _accounts; } }

        internal int SucceededCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _accounts.Count; i++)
                {
                    if (_accounts[i].IsSuccess)
                        n++;
                }
                return n;
            }
        }

        internal int AccountCount { get { return _accounts.Count; } }

        internal bool IsFullSuccess
        {
            get { return Status == Cs2VideoSyncStatus.Completed && SucceededCount == AccountCount; }
        }

        internal string UserMessage { get; set; }
    }

    internal class Cs2VideoSettingsWriter
    {
        internal const string VideoFileName = "cs2_video.txt";
        internal const string BackupSuffix = ".vibrance.bak";
        internal const string TempSuffix = ".vibrance.tmp";
        internal const string Cs2ProcessName = "cs2";

        private const string UserDataFolderName = "userdata";
        private const string CfgRelativePath = @"730\local\cfg";

        private readonly Func<string> _findSteamRoot;
        private readonly Func<bool> _isCs2Running;

        internal Cs2VideoSettingsWriter(Func<string> findSteamRoot, Func<bool> isCs2Running)
        {
            _findSteamRoot = findSteamRoot;
            _isCs2Running = isCs2Running;
        }

        internal static Cs2VideoSettingsWriter CreateDefault()
        {
            return new Cs2VideoSettingsWriter(SteamLibrarySource.FindSteamRoot, IsCs2ProcessRunning);
        }

        private static bool IsCs2ProcessRunning()
        {
            Process[] processes = Process.GetProcessesByName(Cs2ProcessName);
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                for (int i = 0; i < processes.Length; i++)
                    processes[i].Dispose();
            }
        }

        // Is this profile's executable CS2? Basename only, case-insensitive; "cs2.exe.bak" and
        // "notcs2.exe" are not it. (The game-exit restore does not use this: it works from the
        // process short name, see PathResolver.GetProcessNameFromImagePath.)
        internal static bool IsCs2Executable(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return false;
            try
            {
                return string.Equals(Path.GetFileName(filePath), Cs2ProcessName + ".exe", StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        internal Cs2VideoSyncResult Apply(ResolutionModeWrapper mode)
        {
            Cs2VideoSyncResult result = new Cs2VideoSyncResult();
            try
            {
                ApplyCore(mode, result);
            }
            catch (Exception ex)
            {
                // Backstop: every step below already guards itself. Reaching here with accounts
                // already processed would misreport them, so keep what was recorded and say so.
                if (result.Accounts.Count == 0)
                {
                    result.Status = Cs2VideoSyncStatus.Failed;
                    result.UserMessage = Messages.Failed;
                }
                else
                {
                    result.Status = Cs2VideoSyncStatus.Completed;
                    result.UserMessage = BuildCompletedMessage(result);
                }
                Debug.WriteLine("Cs2VideoSettingsWriter.Apply backstop: " + ex.Message);
            }
            return result;
        }

        // internal virtual only so the fixture can drive the backstop above.
        internal virtual void ApplyCore(ResolutionModeWrapper mode, Cs2VideoSyncResult result)
        {
            if (mode == null || mode.DmPelsWidth == 0 || mode.DmPelsHeight == 0)
            {
                Finish(result, Cs2VideoSyncStatus.NoResolution, Messages.NoResolution);
                return;
            }

            bool running;
            try
            {
                running = _isCs2Running != null && _isCs2Running();
            }
            catch (Exception)
            {
                running = true;   // cannot tell: the safe answer is to touch nothing
            }
            if (running)
            {
                Finish(result, Cs2VideoSyncStatus.Cs2Running, Messages.Cs2Running);
                return;
            }

            string root = null;
            try
            {
                root = _findSteamRoot == null ? null : _findSteamRoot();
            }
            catch (Exception)
            {
            }
            if (string.IsNullOrEmpty(root))
            {
                Finish(result, Cs2VideoSyncStatus.SteamNotFound, Messages.SteamNotFound);
                return;
            }

            string[] accountDirs;
            try
            {
                string userData = Path.Combine(root, UserDataFolderName);
                if (!Directory.Exists(userData))
                {
                    Finish(result, Cs2VideoSyncStatus.NoUserData, Messages.NoUserData);
                    return;
                }
                accountDirs = Directory.GetDirectories(userData);
            }
            catch (Exception)
            {
                Finish(result, Cs2VideoSyncStatus.NoUserData, Messages.NoUserData);
                return;
            }
            Array.Sort(accountDirs, StringComparer.OrdinalIgnoreCase);

            List<string[]> targets = new List<string[]>();   // {accountId, filePath}
            for (int i = 0; i < accountDirs.Length; i++)
            {
                string id = Path.GetFileName(accountDirs[i]);
                if (!IsAllDigits(id))
                    continue;
                string file = Path.Combine(Path.Combine(accountDirs[i], CfgRelativePath), VideoFileName);
                bool exists;
                try { exists = File.Exists(file); } catch (Exception) { exists = false; }
                if (exists)
                    targets.Add(new string[] { id, file });
            }

            if (targets.Count == 0)
            {
                Finish(result, Cs2VideoSyncStatus.NoAccountHasFile, Messages.NoAccountHasFile);
                return;
            }

            for (int i = 0; i < targets.Count; i++)
                result.Accounts.Add(PatchAccount(targets[i][0], targets[i][1], mode));

            Finish(result, Cs2VideoSyncStatus.Completed, BuildCompletedMessage(result));
        }

        private static void Finish(Cs2VideoSyncResult result, Cs2VideoSyncStatus status, string message)
        {
            result.Status = status;
            result.UserMessage = message;
        }

        private static bool IsAllDigits(string s)
        {
            if (string.IsNullOrEmpty(s))
                return false;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] < '0' || s[i] > '9')
                    return false;
            }
            return true;
        }

        private static Cs2VideoAccountOutcome PatchAccount(string accountId, string file, ResolutionModeWrapper mode)
        {
            Cs2VideoAccountOutcome outcome = new Cs2VideoAccountOutcome();
            outcome.AccountId = accountId;
            outcome.FilePath = file;

            string temp = file + TempSuffix;
            try
            {
                bool readOnly = (File.GetAttributes(file) & FileAttributes.ReadOnly) != 0;

                // Latin-1 maps every byte to exactly one char and back, so BOMs, odd bytes and line
                // endings survive the patch untouched.
                Encoding latin1 = Encoding.GetEncoding(28591);
                string text = latin1.GetString(File.ReadAllBytes(file));

                Cs2VideoPatchResult patch = Cs2VideoConfigPatcher.Patch(text, mode.DmPelsWidth, mode.DmPelsHeight,
                    mode.DmDisplayFrequency);

                if (patch.Status == Cs2VideoPatchStatus.Malformed)
                {
                    outcome.Status = Cs2VideoAccountStatus.Malformed;
                    return outcome;
                }
                if (patch.Status == Cs2VideoPatchStatus.Unchanged)
                {
                    // Nothing to write, so a pinned (read-only) file that already holds the target is fine.
                    outcome.Status = Cs2VideoAccountStatus.AlreadyCurrent;
                    return outcome;
                }

                // A write is needed. The read-only attribute is never cleared: ReadOnly is reserved for it.
                if (readOnly)
                {
                    outcome.Status = Cs2VideoAccountStatus.ReadOnly;
                    outcome.Detail = "read-only attribute is set";
                    return outcome;
                }

                string backup = file + BackupSuffix;
                if (!File.Exists(backup))
                {
                    try
                    {
                        File.Copy(file, backup, false);
                    }
                    catch (IOException ex)
                    {
                        // Lost a race with another writer of the backup: it exists now, which is
                        // all we wanted. Any other IOException means no backup, so no write.
                        if (!File.Exists(backup))
                        {
                            outcome.Status = Cs2VideoAccountStatus.Locked;
                            outcome.Detail = ex.Message;
                            return outcome;
                        }
                    }
                }

                if (File.Exists(temp))
                    File.Delete(temp);
                File.WriteAllBytes(temp, latin1.GetBytes(patch.Content));
                File.Replace(temp, file, null);

                outcome.Status = Cs2VideoAccountStatus.Updated;
                return outcome;
            }
            catch (Exception ex)
            {
                DeleteQuietly(temp);
                // An access error that is not the read-only attribute (ACL, denied backup copy, ...) is reported
                // as Locked: "in use or could not be written" is accurate, the Detail keeps the real reason.
                outcome.Status = Cs2VideoAccountStatus.Locked;
                outcome.Detail = ex.Message;
                return outcome;
            }
        }

        private static void DeleteQuietly(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception)
            {
            }
        }

        private static string BuildCompletedMessage(Cs2VideoSyncResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("CS2 video settings: updated ").Append(result.SucceededCount).Append(" of ")
              .Append(result.AccountCount).Append(result.AccountCount == 1 ? " account." : " accounts.");

            for (int i = 0; i < result.Accounts.Count; i++)
            {
                Cs2VideoAccountOutcome a = result.Accounts[i];
                string reason;
                switch (a.Status)
                {
                    case Cs2VideoAccountStatus.ReadOnly:
                        reason = "the file is read-only";
                        break;
                    case Cs2VideoAccountStatus.Locked:
                        reason = "the file is in use or could not be written";
                        break;
                    case Cs2VideoAccountStatus.Malformed:
                        reason = "the file is not in the expected format";
                        break;
                    default:
                        continue;
                }
                sb.Append(Environment.NewLine).Append("Account ").Append(a.AccountId).Append(": ")
                  .Append(reason).Append(", not changed.");
            }
            return sb.ToString();
        }

        private static class Messages
        {
            internal const string Cs2Running = "CS2 is running; video settings not changed. Close CS2 and save again.";
            internal const string NoResolution = "No resolution is configured; CS2 video settings not changed.";
            internal const string SteamNotFound = "Steam was not found on this PC; CS2 video settings not changed.";
            internal const string NoUserData = "No Steam accounts were found (no userdata folder); CS2 video settings not changed.";
            internal const string Failed = "CS2 video settings could not be updated because of an unexpected error; nothing else was changed.";
            internal const string NoAccountHasFile = "No Steam account on this PC has CS2 video settings yet. Start CS2 once, then save again.";
        }
    }
}
