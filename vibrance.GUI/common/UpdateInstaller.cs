using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace vibrance.GUI.common
{
    public enum UpdateApplyResult
    {
        Failed = 0,
        Applied = 1,
        NotWritable = 2
    }

    /// <summary>
    /// Replaces vibranceGUI's own files with the ones out of a release zip.
    ///
    /// **Scope, and why it stops where it does.** This runs only after the user has been asked and
    /// said yes, once, about one specific version. It is not a background auto-updater, and the
    /// difference is the whole point: a bad release then reaches the people who chose to take it,
    /// not everyone at once. v2.9.0 and v2.10.0 shipped an x64 build that could not detect an
    /// NVIDIA GPU, and an unattended updater would have pushed that onto every machine running
    /// a working v2.8.0.
    ///
    /// **What the digest check is worth.** GitHub publishes a SHA-256 per asset and it is verified
    /// here before a single file is touched. That catches a truncated or corrupted download, and a
    /// tampered CDN response - assets come from a different host than the API. It does **not**
    /// protect against a compromised GitHub account, because the hash would be replaced along with
    /// the file. Nothing short of signing the binaries protects against that, and these builds are
    /// unsigned; the README says so plainly rather than implying the check is worth more than it is.
    ///
    /// **The swap.** Windows will not let a running .exe be overwritten, but it will let it be
    /// renamed. So every file is moved aside to a ".old-&lt;timestamp&gt;" name first and the new one
    /// written in its place; if any file fails, everything already moved is moved back, so the
    /// installation is either fully updated or exactly as it was. The leftovers are deleted on the
    /// next startup, once nothing is holding them open.
    /// </summary>
    public static class UpdateInstaller
    {
        /// <summary>
        /// The files a release zip is expected to contain, and the only ones that will be written.
        /// An entry outside this list is ignored rather than trusted - see ExtractPayload.
        /// </summary>
        public static readonly string[] ExpectedFiles = { "vibrance.GUI.exe", "vibrance.GUI.exe.config" };

        public const string BackupPrefix = ".old-";

        /// <summary>
        /// The flag the replacement process is started with. Program.Main uses it to wait for the
        /// outgoing instance to let go of the single-instance mutex instead of telling the user
        /// "you can run vibranceGUI only once at a time" one second after they clicked Update.
        /// </summary>
        public const string UpdatedFlag = "--updated";

        /// <summary>
        /// Picks the asset matching this process's architecture. Name-based because that is what
        /// the release zips encode, and getting it wrong is not a subtle failure: an x86 process
        /// that replaces itself with the x64 build will not start again.
        /// </summary>
        public static ReleaseAsset SelectAsset(IEnumerable<ReleaseAsset> assets, bool is64BitProcess)
        {
            if (assets == null)
            {
                return null;
            }

            string wanted = is64BitProcess ? "x64" : "x86";

            foreach (ReleaseAsset asset in assets)
            {
                if (asset == null || string.IsNullOrEmpty(asset.Name))
                {
                    continue;
                }
                if (!asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // The architecture must be the whole final dash-delimited token, which is how the
                // release zips are named ("vibranceGUI-2.10.3-win-x64.zip").
                //
                // A Contains would be wrong, and not in the way it first looks. The obvious guard
                // - require "x86" and reject "x64" - does not help, because "x86_64" does not
                // contain "x64" at all: it is x,8,6,_,6,4. So a name like
                // "vibranceGUI-win-x86_64.zip" passes a Contains-based check as the 32-bit build
                // and hands a 32-bit process the 64-bit binary, which then will not start. An
                // exact token comparison rejects it, because "x86_64" is not "x86". The fixture
                // pins that case specifically; it is the one that got this wrong first time.
                string stem = Path.GetFileNameWithoutExtension(asset.Name);
                int lastDash = stem.LastIndexOf('-');
                if (lastDash < 0)
                {
                    continue;
                }
                if (string.Equals(stem.Substring(lastDash + 1), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return asset;
                }
            }
            return null;
        }

        /// <summary>
        /// GitHub spells its digest "sha256:&lt;hex&gt;". Returns the lower-case hex, or null for a
        /// missing digest or any algorithm this does not implement - null meaning "nothing to
        /// verify against", which the caller treats as a refusal rather than a pass.
        /// </summary>
        public static string ParseSha256(string digest)
        {
            if (string.IsNullOrEmpty(digest))
            {
                return null;
            }
            string trimmed = digest.Trim();
            const string prefix = "sha256:";
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            string hex = trimmed.Substring(prefix.Length).Trim().ToLowerInvariant();
            if (hex.Length != 64)
            {
                return null;
            }
            foreach (char c in hex)
            {
                bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!isHex)
                {
                    return null;
                }
            }
            return hex;
        }

        public static string ComputeSha256(byte[] content)
        {
            if (content == null)
            {
                return null;
            }
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(content);
                StringBuilder hex = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                {
                    hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                }
                return hex.ToString();
            }
        }

        /// <summary>
        /// True only when a digest was offered AND it matches. An asset with no digest fails here
        /// deliberately: "we could not check" and "we checked and it was fine" must not take the
        /// same branch when the next step is overwriting the program with the bytes in question.
        /// </summary>
        public static bool DigestMatches(byte[] content, string digest)
        {
            string expected = ParseSha256(digest);
            if (expected == null || content == null)
            {
                return false;
            }
            return string.Equals(expected, ComputeSha256(content), StringComparison.Ordinal);
        }

        /// <summary>
        /// Whether this process could actually replace the files in a directory. Answered by
        /// writing a file rather than by reading an ACL: the effective answer depends on the token,
        /// UAC virtualisation, and whatever else is in the way, and the only reliable test is the
        /// operation itself. A user who unzipped into Program Files gets told to move the app or
        /// download manually, never a half-finished install.
        /// </summary>
        public static bool IsDirectoryWritable(string directory)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return false;
            }
            string probe = Path.Combine(directory, "vibranceGUI-write-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write))
                {
                    stream.WriteByte(0);
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(probe))
                    {
                        File.Delete(probe);
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>
        /// Reads the expected files out of a release zip.
        ///
        /// Only the names in ExpectedFiles are taken, and each is matched on its base name with
        /// any directory part discarded. That is the zip-slip guard: an entry called
        /// "..\..\Windows\System32\something.dll" cannot name a file this writes, because the only
        /// paths ever written are Path.Combine(targetDir, one of two literal names).
        /// </summary>
        public static Dictionary<string, byte[]> ExtractPayload(byte[] zipBytes, out string error)
        {
            error = null;
            if (zipBytes == null || zipBytes.Length == 0)
            {
                error = "The download was empty.";
                return null;
            }

            Dictionary<string, byte[]> payload = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (MemoryStream stream = new MemoryStream(zipBytes, false))
                using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        string name = Path.GetFileName(entry.FullName);
                        if (string.IsNullOrEmpty(name) || !IsExpected(name))
                        {
                            continue;
                        }
                        using (Stream entryStream = entry.Open())
                        using (MemoryStream buffer = new MemoryStream())
                        {
                            entryStream.CopyTo(buffer);
                            payload[name] = buffer.ToArray();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                error = "The download could not be read as a zip: " + ex.Message;
                return null;
            }

            // The executable is the update. A zip without it is not one, whatever else it holds.
            if (!payload.ContainsKey(ExpectedFiles[0]))
            {
                error = "The download did not contain " + ExpectedFiles[0] + ".";
                return null;
            }
            return payload;
        }

        private static bool IsExpected(string fileName)
        {
            foreach (string expected in ExpectedFiles)
            {
                if (string.Equals(expected, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        public static string BackupPathFor(string filePath, DateTime whenUtc)
        {
            return filePath + BackupPrefix + whenUtc.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Moves the current files aside and writes the new ones. All or nothing: anything already
        /// moved is moved back if a later file fails, so a failed update leaves a working
        /// installation rather than half of two versions.
        /// </summary>
        public static UpdateApplyResult ApplyPayload(Dictionary<string, byte[]> payload, string targetDirectory,
            DateTime whenUtc, out string error)
        {
            error = null;
            if (payload == null || payload.Count == 0)
            {
                error = "Nothing to install.";
                return UpdateApplyResult.Failed;
            }
            if (!IsDirectoryWritable(targetDirectory))
            {
                error = "vibranceGUI cannot write to its own folder (" + targetDirectory + ").";
                return UpdateApplyResult.NotWritable;
            }

            List<KeyValuePair<string, string>> moved = new List<KeyValuePair<string, string>>();
            List<string> written = new List<string>();
            try
            {
                foreach (KeyValuePair<string, byte[]> file in payload)
                {
                    string destination = Path.Combine(targetDirectory, file.Key);
                    if (File.Exists(destination))
                    {
                        string backup = BackupPathFor(destination, whenUtc);
                        // Move, not Delete: the running .exe is one of these files, and Windows
                        // refuses to delete or overwrite it while it is running but is perfectly
                        // happy to rename it out of the way.
                        File.Move(destination, backup);
                        moved.Add(new KeyValuePair<string, string>(backup, destination));
                    }
                    File.WriteAllBytes(destination, file.Value);
                    written.Add(destination);
                }
                return UpdateApplyResult.Applied;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                RollBack(moved, written);
                return UpdateApplyResult.Failed;
            }
        }

        private static void RollBack(List<KeyValuePair<string, string>> moved, List<string> written)
        {
            foreach (string path in written)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception)
                {
                }
            }
            foreach (KeyValuePair<string, string> pair in moved)
            {
                try
                {
                    if (File.Exists(pair.Key) && !File.Exists(pair.Value))
                    {
                        File.Move(pair.Key, pair.Value);
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>
        /// Deletes the files left behind by a previous update. Called at startup, because that is
        /// the first moment the old executable is no longer running and can actually be removed.
        /// Best effort throughout: a leftover file is untidy, never harmful, and is certainly not
        /// worth failing a startup over.
        /// </summary>
        public static int CleanUpBackups(string directory)
        {
            int deleted = 0;
            try
            {
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                {
                    return 0;
                }
                foreach (string path in Directory.GetFiles(directory, "*" + BackupPrefix + "*"))
                {
                    try
                    {
                        File.Delete(path);
                        deleted++;
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (Exception)
            {
            }
            return deleted;
        }

        /// <summary>
        /// The confirmation text. Deliberately says where the files are going and that the app will
        /// restart, because "yes" here means overwriting the program the user is currently running.
        /// </summary>
        public static string BuildConfirmationText(Version current, Version latest, string targetDirectory)
        {
            StringBuilder text = new StringBuilder();
            text.AppendFormat("vibranceGUI {0} is available. You are running {1}.",
                UpdateCheckPolicy.FormatVersion(latest), UpdateCheckPolicy.FormatVersion(current));
            text.AppendLine();
            text.AppendLine();
            text.AppendLine("Download it and replace this installation now?");
            text.AppendLine();
            text.AppendFormat("Files in {0} will be replaced, and vibranceGUI will restart.", targetDirectory);
            text.AppendLine();
            text.AppendLine("The previous version is kept alongside them until the next start, in case you need it.");
            text.AppendLine();
            text.AppendLine("Yes - download and install now");
            text.Append("No - open the download page in your browser instead");
            return text.ToString();
        }
    }
}
