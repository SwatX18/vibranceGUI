using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace vibrance.GUI.common
{
    /// <summary>
    /// Regression coverage for the CS2 video settings sync (issue #61): Cs2VideoConfigPatcher (pure
    /// text) and Cs2VideoSettingsWriter (file handling) over fake Steam trees built in temp
    /// directories. Never touches the real Steam folder, never starts or looks for a real cs2.exe -
    /// the "is CS2 running" probe is a lambda. Read-only and locked-file scenarios are cleaned up
    /// (attribute cleared, handle closed) before the temp tree is deleted.
    /// Run by vibrance.GUI.exe --selftest-cs2video.
    /// </summary>
    public static class Cs2VideoSettingsFixture
    {
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        public static List<string> Run()
        {
            Checklist checklist = new Checklist();
            checklist.Lines.Add("vibranceGUI CS2 video settings self test");
            checklist.Lines.Add(string.Empty);

            Guard(checklist, "exact patch", CheckExactPatch);
            Guard(checklist, "BOM and line endings", CheckBomAndLineEndings);
            Guard(checklist, "unchanged", CheckUnchanged);
            Guard(checklist, "insert missing keys", CheckInsertMissing);
            Guard(checklist, "refresh pair rule", CheckRefreshPairRule);
            Guard(checklist, "malformed", CheckMalformed);
            Guard(checklist, "nested and duplicate keys", CheckNestedAndDuplicate);
            Guard(checklist, "cs2 running", CheckCs2Running);
            Guard(checklist, "multi-account", CheckMultiAccount);
            Guard(checklist, "discovery failures", CheckDiscoveryFailures);
            Guard(checklist, "backup", CheckBackup);
            Guard(checklist, "IsCs2Executable", CheckIsCs2Executable);
            Guard(checklist, "setting round trip", CheckSettingRoundTrip);
            Guard(checklist, "patcher edge cases", CheckPatcherEdgeCases);
            Guard(checklist, "writer edge cases", CheckWriterEdgeCases);

            // ---- fixture 14: dialog checks ----------------------------------------------------
            Guard(checklist, "dialog", CheckDialog);
            // -----------------------------------------------------------------------------------

            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add(string.Format("PASSED {0}/{1}", checklist.Passed, checklist.Total));
            return checklist.Lines;
        }

        private static void Guard(Checklist c, string name, Action<Checklist> check)
        {
            try
            {
                check(c);
            }
            catch (Exception ex)
            {
                c.Check(false, name + " scenarios threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ---- sample data ---------------------------------------------------------------------

        // The sample from the issue: tabs between tokens, every line ends in nl.
        private static string Sample(string nl, string w, string h, string num, string den)
        {
            return "\"video.cfg\"" + nl +
                   "{" + nl +
                   "\t\"Version\"\t\t\"16\"" + nl +
                   "\t\"VendorID\"\t\t\"4318\"" + nl +
                   "\t\"DeviceID\"\t\t\"11269\"" + nl +
                   "\t\"setting.cpu_level\"\t\t\"3\"" + nl +
                   "\t\"setting.knowndevice\"\t\t\"0\"" + nl +
                   "\t\"setting.defaultres\"\t\t\"" + w + "\"" + nl +
                   "\t\"setting.defaultresheight\"\t\t\"" + h + "\"" + nl +
                   "\t\"setting.refreshrate_numerator\"\t\t\"" + num + "\"" + nl +
                   "\t\"setting.refreshrate_denominator\"\t\t\"" + den + "\"" + nl +
                   "\t\"setting.fullscreen\"\t\t\"1\"" + nl +
                   "\t\"setting.monitor_index\"\t\t\"0\"" + nl +
                   "\t\"setting.aspectratiomode\"\t\t\"0\"" + nl +
                   "}" + nl;
        }

        private static string Crlf { get { return "\r\n"; } }

        private static ResolutionModeWrapper Mode(uint w, uint h, uint hz)
        {
            ResolutionModeWrapper mode = new ResolutionModeWrapper();
            mode.DmPelsWidth = w;
            mode.DmPelsHeight = h;
            mode.DmBitsPerPel = 32;
            mode.DmDisplayFrequency = hz;
            return mode;
        }

        private static int Count(string s, string needle)
        {
            int n = 0;
            int i = 0;
            while ((i = s.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
            {
                n++;
                i += needle.Length;
            }
            return n;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }

        private static byte[] Bytes(string s)
        {
            return Latin1.GetBytes(s);
        }

        // ---- fake steam tree -----------------------------------------------------------------

        private static string NewTempRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "vibrance-cs2video-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static string CfgDir(string root, string account)
        {
            return Path.Combine(Path.Combine(Path.Combine(Path.Combine(root, "userdata"), account), "730"), @"local\cfg");
        }

        private static string WriteVideo(string root, string account, byte[] bytes)
        {
            string dir = CfgDir(root, account);
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, Cs2VideoSettingsWriter.VideoFileName);
            File.WriteAllBytes(file, bytes);
            return file;
        }

        private static Cs2VideoSettingsWriter WriterFor(string root)
        {
            return new Cs2VideoSettingsWriter(() => root, () => false);
        }

        private static void Cleanup(string root)
        {
            try
            {
                foreach (string f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch (Exception) { }
                }
                Directory.Delete(root, true);
            }
            catch (Exception)
            {
            }
        }

        // ---- 1 ------------------------------------------------------------------------------

        private static void CheckExactPatch(Checklist c)
        {
            string input = Sample(Crlf, "1280", "1024", "260002", "1000");
            string expected = Sample(Crlf, "1920", "1080", "144000", "1000");
            Cs2VideoPatchResult r = Cs2VideoConfigPatcher.Patch(input, 1920, 1080, 144);

            c.Check(r.Status == Cs2VideoPatchStatus.Patched, "1920x1080@144 over the 1280x1024@260 sample is Patched (" + r.Status + ")");
            c.Check(r.Content == expected, "patched text equals the sample with only the four value strings replaced");
            c.Check(r.Content != null && BytesEqual(Bytes(r.Content), Bytes(expected)), "patched text is byte-identical to the expected bytes");
            c.Check(r.Content != null && Count(r.Content, "\r\n") == Count(input, "\r\n") && Count(r.Content, "\n") == Count(input, "\n"),
                "CRLF count is unchanged");
            c.Check(r.Content != null && r.Content.EndsWith("}\r\n", StringComparison.Ordinal), "file still ends with \"}\\r\\n\"");
            c.Check(r.Content != null &&
                    r.Content.Contains("\t\"setting.fullscreen\"\t\t\"1\"\r\n") &&
                    r.Content.Contains("\t\"setting.monitor_index\"\t\t\"0\"\r\n") &&
                    r.Content.Contains("\t\"setting.aspectratiomode\"\t\t\"0\"\r\n"),
                "fullscreen, monitor_index and aspectratiomode lines are intact");
        }

        // ---- 2 ------------------------------------------------------------------------------

        private static void CheckBomAndLineEndings(Checklist c)
        {
            string root = NewTempRoot();
            try
            {
                byte[] bom = new byte[] { 0xEF, 0xBB, 0xBF };
                string input = Sample(Crlf, "1280", "1024", "260002", "1000");
                string expected = Sample(Crlf, "1920", "1080", "144000", "1000");

                List<byte> withBom = new List<byte>(bom);
                withBom.AddRange(Bytes(input));
                string file = WriteVideo(root, "1001", withBom.ToArray());
                Cs2VideoSyncResult res = WriterFor(root).Apply(Mode(1920, 1080, 144));

                List<byte> expectedBom = new List<byte>(bom);
                expectedBom.AddRange(Bytes(expected));
                c.Check(res.Status == Cs2VideoSyncStatus.Completed && res.IsFullSuccess, "UTF-8 BOM file: sync completes (" + res.Status + ")");
                c.Check(BytesEqual(File.ReadAllBytes(file), expectedBom.ToArray()), "UTF-8 BOM file: BOM kept, only the values changed");

                string lfRoot = NewTempRoot();
                try
                {
                    string lfFile = WriteVideo(lfRoot, "1001", Bytes(Sample("\n", "1280", "1024", "260002", "1000")));
                    WriterFor(lfRoot).Apply(Mode(1920, 1080, 144));
                    byte[] after = File.ReadAllBytes(lfFile);
                    c.Check(BytesEqual(after, Bytes(Sample("\n", "1920", "1080", "144000", "1000"))), "LF-only file: patched and stays LF-only");
                    c.Check(Array.IndexOf(after, (byte)'\r') < 0, "LF-only file: no CR introduced");
                }
                finally
                {
                    Cleanup(lfRoot);
                }
            }
            finally
            {
                Cleanup(root);
            }
        }

        // ---- 3 ------------------------------------------------------------------------------

        private static void CheckUnchanged(Checklist c)
        {
            string input = Sample(Crlf, "1280", "1024", "260002", "1000");
            Cs2VideoPatchResult r = Cs2VideoConfigPatcher.Patch(input, 1280, 1024, 260);
            c.Check(r.Status == Cs2VideoPatchStatus.Unchanged, "1280x1024@260 over 260002/1000 is Unchanged (" + r.Status + ")");
            c.Check(r.Content == input, "Unchanged content equals the input (260002/1000 kept)");
        }

        // ---- 4 ------------------------------------------------------------------------------

        private static string Minimal(string nl, string indent, string sep, string extra)
        {
            return "\"video.cfg\"" + nl + "{" + nl + indent + "\"Version\"" + sep + "\"16\"" + nl + extra + "}" + nl;
        }

        private static string Line(string indent, string sep, string key, string value, string nl)
        {
            return indent + "\"" + key + "\"" + sep + "\"" + value + "\"" + nl;
        }

        private static void CheckInsertMissing(Checklist c)
        {
            string[] nls = new string[] { "\r\n", "\n" };
            for (int n = 0; n < nls.Length; n++)
            {
                string nl = nls[n];
                string label = n == 0 ? "CRLF" : "LF";
                string indent = n == 0 ? "\t" : "    ";
                string sep = n == 0 ? "\t\t" : "  ";

                // refresh pair missing entirely
                string withRes = Line(indent, sep, "setting.defaultres", "1280", nl) + Line(indent, sep, "setting.defaultresheight", "1024", nl);
                string input = Minimal(nl, indent, sep, withRes);
                string expected = Minimal(nl, indent, sep,
                    Line(indent, sep, "setting.defaultres", "1920", nl) + Line(indent, sep, "setting.defaultresheight", "1080", nl) +
                    Line(indent, sep, "setting.refreshrate_numerator", "144000", nl) + Line(indent, sep, "setting.refreshrate_denominator", "1000", nl));
                Cs2VideoPatchResult r = Cs2VideoConfigPatcher.Patch(input, 1920, 1080, 144);
                c.Check(r.Content == expected, label + ": missing refresh pair is inserted before } in the file's indent, separator and newline");

                // all four missing: canonical order
                string bare = Minimal(nl, indent, sep, string.Empty);
                string expectedAll = Minimal(nl, indent, sep,
                    Line(indent, sep, "setting.defaultres", "1920", nl) + Line(indent, sep, "setting.defaultresheight", "1080", nl) +
                    Line(indent, sep, "setting.refreshrate_numerator", "144000", nl) + Line(indent, sep, "setting.refreshrate_denominator", "1000", nl));
                Cs2VideoPatchResult all = Cs2VideoConfigPatcher.Patch(bare, 1920, 1080, 144);
                c.Check(all.Status == Cs2VideoPatchStatus.Patched && all.Content == expectedAll,
                    label + ": all four keys missing are inserted in order width, height, numerator, denominator");

                // only the numerator present: pair rewritten, denominator inserted
                string half = Minimal(nl, indent, sep, Line(indent, sep, "setting.refreshrate_numerator", "260002", nl));
                string expectedHalf = Minimal(nl, indent, sep,
                    Line(indent, sep, "setting.refreshrate_numerator", "144000", nl) +
                    Line(indent, sep, "setting.defaultres", "1920", nl) + Line(indent, sep, "setting.defaultresheight", "1080", nl) +
                    Line(indent, sep, "setting.refreshrate_denominator", "1000", nl));
                Cs2VideoPatchResult hr = Cs2VideoConfigPatcher.Patch(half, 1920, 1080, 144);
                c.Check(hr.Content == expectedHalf, label + ": a lone refresh key is rewritten and its partner inserted");
            }

            // closing brace sharing a line with a value: a newline is inserted first, and the result is stable
            string inline = "\"video.cfg\"\n{\n\t\"Version\"\t\"16\" }\n";
            Cs2VideoPatchResult il = Cs2VideoConfigPatcher.Patch(inline, 1920, 1080, 144);
            c.Check(il.Status == Cs2VideoPatchStatus.Patched && il.Content != null &&
                    il.Content.IndexOf("\"setting.defaultres\"", StringComparison.Ordinal) > il.Content.IndexOf("\"Version\"", StringComparison.Ordinal) &&
                    il.Content.TrimEnd().EndsWith("}", StringComparison.Ordinal),
                "} on the same line as a value: keys land before it");
            Cs2VideoPatchResult again = il.Content == null ? null : Cs2VideoConfigPatcher.Patch(il.Content, 1920, 1080, 144);
            c.Check(again != null && again.Status == Cs2VideoPatchStatus.Unchanged, "patching the inserted result again is Unchanged");

            // no usable refresh rate: the pair is neither inserted nor rewritten
            string noRate = Cs2VideoConfigPatcher.Patch(Minimal("\n", "\t", "\t\t", string.Empty), 1920, 1080, 0).Content;
            c.Check(noRate != null && noRate.Contains("setting.defaultres\"") && !noRate.Contains("refreshrate"),
                "refreshHz 0 inserts width and height only");
            string keepRate = Cs2VideoConfigPatcher.Patch(Sample(Crlf, "1280", "1024", "260002", "1000"), 1920, 1080, 1).Content;
            c.Check(keepRate == Sample(Crlf, "1920", "1080", "260002", "1000"), "refreshHz 1 leaves an existing refresh pair untouched");
        }

        // ---- 5 ------------------------------------------------------------------------------

        private static void CheckRefreshPairRule(Checklist c)
        {
            string n;
            string d;
            bool keep = Cs2VideoConfigPatcher.ShouldKeepRefreshPair("260002", "1000", 260, out n, out d);
            c.Check(keep && n == "260002" && d == "1000", "260002/1000 is kept for 260 Hz");

            keep = Cs2VideoConfigPatcher.ShouldKeepRefreshPair("260002", "1000", 144, out n, out d);
            c.Check(!keep && n == "144000" && d == "1000", "260002/1000 is rewritten to 144000/1000 for 144 Hz");

            keep = Cs2VideoConfigPatcher.ShouldKeepRefreshPair("144000", "0", 144, out n, out d);
            c.Check(!keep && n == "144000" && d == "1000", "denominator 0 is rewritten");

            keep = Cs2VideoConfigPatcher.ShouldKeepRefreshPair("abc", "1000", 144, out n, out d);
            c.Check(!keep && n == "144000" && d == "1000", "non-numeric numerator is rewritten");

            keep = Cs2VideoConfigPatcher.ShouldKeepRefreshPair(null, null, 144, out n, out d);
            c.Check(!keep && n == "144000" && d == "1000", "missing pair is rewritten");

            keep = Cs2VideoConfigPatcher.ShouldKeepRefreshPair("144000", null, 144, out n, out d);
            c.Check(!keep, "a lone numerator is rewritten");

            keep = Cs2VideoConfigPatcher.ShouldKeepRefreshPair("143999", "1000", 144, out n, out d);
            c.Check(keep && n == "143999", "143999/1000 rounds to 144 and is kept");

            string input = Sample(Crlf, "1280", "1024", "260002", "1000");
            Cs2VideoPatchResult zero = Cs2VideoConfigPatcher.Patch(input, 1280, 1024, 0);
            Cs2VideoPatchResult one = Cs2VideoConfigPatcher.Patch(input, 1280, 1024, 1);
            c.Check(zero.Status == Cs2VideoPatchStatus.Unchanged && one.Status == Cs2VideoPatchStatus.Unchanged,
                "refreshHz 0 and 1 leave both refresh values untouched");
        }

        // ---- 6 ------------------------------------------------------------------------------

        private static void CheckMalformed(Checklist c)
        {
            Dictionary<string, string> cases = new Dictionary<string, string>();
            cases["no root {"] = "\"video.cfg\"\r\n\"a\"\t\"b\"\r\n";
            cases["no closing }"] = "\"video.cfg\"\r\n{\r\n\t\"a\"\t\"b\"\r\n";
            cases["unterminated quote"] = "\"video.cfg\"\r\n{\r\n\t\"a\"\t\"b\r\n}\r\n";
            cases["empty"] = string.Empty;
            cases["unquoted owned value"] = "\"video.cfg\"\r\n{\r\n\t\"setting.defaultres\"\t\t1280\r\n\t\"setting.defaultresheight\"\t\t\"1024\"\r\n}\r\n";

            foreach (KeyValuePair<string, string> kv in cases)
            {
                Cs2VideoPatchResult r = Cs2VideoConfigPatcher.Patch(kv.Value, 1920, 1080, 144);
                c.Check(r.Status == Cs2VideoPatchStatus.Malformed && r.Content == null, "Malformed: " + kv.Key);
            }

            string root = NewTempRoot();
            try
            {
                byte[] original = Bytes(cases["unquoted owned value"]);
                string file = WriteVideo(root, "1001", original);
                Cs2VideoSyncResult res = WriterFor(root).Apply(Mode(1920, 1080, 144));
                c.Check(res.Status == Cs2VideoSyncStatus.Completed && res.Accounts.Count == 1 && res.Accounts[0].Status == Cs2VideoAccountStatus.Malformed,
                    "writer reports a malformed file as Malformed");
                c.Check(BytesEqual(File.ReadAllBytes(file), original), "writer leaves the malformed file's bytes alone");
                c.Check(!File.Exists(file + Cs2VideoSettingsWriter.BackupSuffix) && !File.Exists(file + Cs2VideoSettingsWriter.TempSuffix),
                    "writer makes no backup and leaves no temp for a malformed file");
                c.Check(res.UserMessage == "CS2 video settings: updated 0 of 1 account." + Environment.NewLine +
                        "Account 1001: the file is not in the expected format, not changed.",
                    "malformed message is exact");
            }
            finally
            {
                Cleanup(root);
            }
        }

        // ---- 7 ------------------------------------------------------------------------------

        private static void CheckNestedAndDuplicate(Checklist c)
        {
            string nested = "\"video.cfg\"\r\n{\r\n" +
                            "\t\"Version\"\t\"16\"\r\n" +
                            "\t\"nested\"\r\n\t{\r\n\t\t\"setting.defaultres\"\t\"111\"\r\n\t}\r\n" +
                            "\t\"setting.defaultres\"\t\"1280\"\r\n" +
                            "\t\"setting.defaultresheight\"\t\"1024\"\r\n" +
                            "}\r\n";
            string expectedNested = nested.Replace("\"1280\"", "\"1920\"").Replace("\"1024\"", "\"1080\"");
            Cs2VideoPatchResult r = Cs2VideoConfigPatcher.Patch(nested, 1920, 1080, 0);
            c.Check(r.Status == Cs2VideoPatchStatus.Patched && r.Content == expectedNested,
                "an owned key inside a nested block is ignored; the depth-1 one is patched");

            string nestedOnly = "\"video.cfg\"\r\n{\r\n\t\"nested\"\r\n\t{\r\n\t\t\"setting.defaultres\"\t\"1920\"\r\n\t}\r\n}\r\n";
            Cs2VideoPatchResult n2 = Cs2VideoConfigPatcher.Patch(nestedOnly, 1920, 1080, 0);
            c.Check(n2.Content != null && n2.Content.Contains("\t\t\"setting.defaultres\"\t\"1920\"\r\n\t}\r\n") && Count(n2.Content, "\"setting.defaultres\"") == 2,
                "a nested key does not count as present: the depth-1 key is still inserted");

            string dup = "\"video.cfg\"\r\n{\r\n" +
                         "\t\"setting.defaultres\"\t\"1280\"\r\n" +
                         "\t\"setting.defaultresheight\"\t\"1024\"\r\n" +
                         "\t\"setting.defaultres\"\t\"800\"\r\n" +
                         "}\r\n";
            string expectedDup = "\"video.cfg\"\r\n{\r\n" +
                                 "\t\"setting.defaultres\"\t\"1920\"\r\n" +
                                 "\t\"setting.defaultresheight\"\t\"1080\"\r\n" +
                                 "\t\"setting.defaultres\"\t\"1920\"\r\n" +
                                 "}\r\n";
            Cs2VideoPatchResult d = Cs2VideoConfigPatcher.Patch(dup, 1920, 1080, 0);
            c.Check(d.Content == expectedDup, "a duplicated owned key at depth 1 has both copies replaced");

            string commented = "\"video.cfg\"\r\n{\r\n\t// \"setting.defaultres\" \"1\"\r\n\t\"setting.defaultres\"\t\"1280\"\r\n\t\"setting.defaultresheight\"\t\"1024\"\r\n}\r\n";
            Cs2VideoPatchResult cm = Cs2VideoConfigPatcher.Patch(commented, 1920, 1080, 0);
            c.Check(cm.Content == commented.Replace("\"1280\"", "\"1920\"").Replace("\"1024\"", "\"1080\""),
                "a // comment mentioning an owned key is left alone");
        }

        // ---- 8 ------------------------------------------------------------------------------

        private static void CheckCs2Running(Checklist c)
        {
            string root = NewTempRoot();
            try
            {
                byte[] original = Bytes(Sample(Crlf, "1280", "1024", "260002", "1000"));
                string file = WriteVideo(root, "1001", original);
                Cs2VideoSettingsWriter writer = new Cs2VideoSettingsWriter(() => root, () => true);
                Cs2VideoSyncResult res = writer.Apply(Mode(1920, 1080, 144));

                c.Check(res.Status == Cs2VideoSyncStatus.Cs2Running && !res.IsFullSuccess, "CS2 running: status Cs2Running, not a full success");
                c.Check(res.UserMessage == "CS2 is running; video settings not changed. Close CS2 and save again.", "CS2 running: exact message");
                c.Check(res.Accounts != null && res.Accounts.Count == 0, "CS2 running: no account touched");
                c.Check(BytesEqual(File.ReadAllBytes(file), original), "CS2 running: file bytes unchanged");
                c.Check(Directory.GetFiles(CfgDir(root, "1001")).Length == 1, "CS2 running: no backup or temp file created");
            }
            finally
            {
                Cleanup(root);
            }
        }

        // ---- 9 ------------------------------------------------------------------------------

        private static void CheckMultiAccount(Checklist c)
        {
            string root = NewTempRoot();
            FileStream held = null;
            string readOnlyFile = null;
            try
            {
                byte[] original = Bytes(Sample(Crlf, "1280", "1024", "260002", "1000"));
                string good = WriteVideo(root, "1001", original);
                readOnlyFile = WriteVideo(root, "1002", original);
                string locked = WriteVideo(root, "1003", original);
                string noFileDir = Path.Combine(Path.Combine(root, "userdata"), "1004");
                Directory.CreateDirectory(Path.Combine(noFileDir, "7"));
                Directory.CreateDirectory(Path.Combine(Path.Combine(root, "userdata"), "anonymous"));   // not an account id

                File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly);
                held = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

                Cs2VideoSyncResult res = WriterFor(root).Apply(Mode(1920, 1080, 144));

                c.Check(res.Status == Cs2VideoSyncStatus.Completed && res.AccountCount == 3 && res.SucceededCount == 1 && !res.IsFullSuccess,
                    "3 accounts with a file: 1 succeeded, not a full success (" + res.Status + " " + res.SucceededCount + "/" + res.AccountCount + ")");
                c.Check(res.UserMessage != null && res.UserMessage.StartsWith("CS2 video settings: updated 1 of 3 accounts.", StringComparison.Ordinal),
                    "message starts \"updated 1 of 3 accounts.\"");
                c.Check(res.UserMessage != null &&
                        res.UserMessage.Contains("Account 1002: the file is read-only, not changed.") &&
                        res.UserMessage.Contains("Account 1003: the file is in use or could not be written, not changed."),
                    "message has one exact line per failure");

                Cs2VideoAccountStatus? s1 = Status(res, "1001");
                Cs2VideoAccountStatus? s2 = Status(res, "1002");
                Cs2VideoAccountStatus? s3 = Status(res, "1003");
                c.Check(s1 == Cs2VideoAccountStatus.Updated, "account 1001 Updated (" + s1 + ")");
                c.Check(s2 == Cs2VideoAccountStatus.ReadOnly, "account 1002 ReadOnly (" + s2 + ")");
                c.Check(s3 == Cs2VideoAccountStatus.Locked, "account 1003 Locked (" + s3 + ")");

                c.Check(BytesEqual(File.ReadAllBytes(good), Bytes(Sample(Crlf, "1920", "1080", "144000", "1000"))), "good account is patched");
                c.Check(BytesEqual(File.ReadAllBytes(readOnlyFile), original), "read-only account is byte-identical");
                c.Check((File.GetAttributes(readOnlyFile) & FileAttributes.ReadOnly) != 0, "read-only flag is still set");
                held.Dispose();
                held = null;
                c.Check(BytesEqual(File.ReadAllBytes(locked), original), "locked account is byte-identical");
                c.Check(!File.Exists(good + Cs2VideoSettingsWriter.TempSuffix) &&
                        !File.Exists(readOnlyFile + Cs2VideoSettingsWriter.TempSuffix) &&
                        !File.Exists(locked + Cs2VideoSettingsWriter.TempSuffix),
                    "no temp file left in any account");
                c.Check(!File.Exists(readOnlyFile + Cs2VideoSettingsWriter.BackupSuffix) && !File.Exists(locked + Cs2VideoSettingsWriter.BackupSuffix),
                    "failed accounts got no backup");
                c.Check(!Directory.Exists(Path.Combine(noFileDir, "730")) && Status(res, "1004") == null,
                    "account without a file is not counted and nothing is created for it");
            }
            finally
            {
                if (held != null)
                    held.Dispose();
                Cleanup(root);
            }
        }

        private static Cs2VideoAccountStatus? Status(Cs2VideoSyncResult res, string id)
        {
            foreach (Cs2VideoAccountOutcome o in res.Accounts)
            {
                if (o.AccountId == id)
                    return o.Status;
            }
            return null;
        }

        // ---- 10 -----------------------------------------------------------------------------

        private static void CheckDiscoveryFailures(Checklist c)
        {
            Cs2VideoSyncResult noSteam = new Cs2VideoSettingsWriter(() => null, () => false).Apply(Mode(1920, 1080, 144));
            c.Check(noSteam.Status == Cs2VideoSyncStatus.SteamNotFound, "null Steam root: SteamNotFound");
            c.Check(noSteam.UserMessage == "Steam was not found on this PC; CS2 video settings not changed.", "SteamNotFound message is exact");

            string root = NewTempRoot();
            try
            {
                Cs2VideoSyncResult noUser = WriterFor(root).Apply(Mode(1920, 1080, 144));
                c.Check(noUser.Status == Cs2VideoSyncStatus.NoUserData, "no userdata folder: NoUserData");
                c.Check(noUser.UserMessage == "No Steam accounts were found (no userdata folder); CS2 video settings not changed.", "NoUserData message is exact");

                Directory.CreateDirectory(Path.Combine(Path.Combine(root, "userdata"), "1001"));
                Cs2VideoSyncResult noFile = WriterFor(root).Apply(Mode(1920, 1080, 144));
                c.Check(noFile.Status == Cs2VideoSyncStatus.NoAccountHasFile, "userdata without cs2_video.txt: NoAccountHasFile");
                c.Check(noFile.UserMessage == "No Steam account on this PC has CS2 video settings yet. Start CS2 once, then save again.", "NoAccountHasFile message is exact");
                c.Check(!Directory.Exists(Path.Combine(Path.Combine(Path.Combine(root, "userdata"), "1001"), "730")), "NoAccountHasFile creates nothing");

                Cs2VideoSyncResult noRes = WriterFor(root).Apply(null);
                Cs2VideoSyncResult zeroRes = WriterFor(root).Apply(Mode(0, 1080, 144));
                c.Check(noRes.Status == Cs2VideoSyncStatus.NoResolution && zeroRes.Status == Cs2VideoSyncStatus.NoResolution, "null mode and zero width: NoResolution");
                c.Check(noRes.UserMessage == "No resolution is configured; CS2 video settings not changed.", "NoResolution message is exact");
                c.Check(noRes.Accounts != null && noRes.Accounts.Count == 0, "Accounts is never null");
            }
            finally
            {
                Cleanup(root);
            }
        }

        // ---- 11 -----------------------------------------------------------------------------

        private static void CheckBackup(Checklist c)
        {
            string root = NewTempRoot();
            try
            {
                byte[] original = Bytes(Sample(Crlf, "1280", "1024", "260002", "1000"));
                string file = WriteVideo(root, "1001", original);
                string valveBak = file + ".bak";   // Valve's own cs2_video.txt.bak
                byte[] valveBytes = new byte[] { 1, 2, 3, 250, 251 };
                File.WriteAllBytes(valveBak, valveBytes);
                string bak = file + Cs2VideoSettingsWriter.BackupSuffix;

                Cs2VideoSyncResult first = WriterFor(root).Apply(Mode(1920, 1080, 144));
                c.Check(first.IsFullSuccess && File.Exists(bak) && BytesEqual(File.ReadAllBytes(bak), original),
                    "first write creates .vibrance.bak holding the original bytes");

                Cs2VideoSyncResult second = WriterFor(root).Apply(Mode(2560, 1440, 240));
                c.Check(second.IsFullSuccess && BytesEqual(File.ReadAllBytes(file), Bytes(Sample(Crlf, "2560", "1440", "240000", "1000"))),
                    "second write with different values is applied");
                c.Check(BytesEqual(File.ReadAllBytes(bak), original), "second write leaves the backup bytes unchanged");
                c.Check(BytesEqual(File.ReadAllBytes(valveBak), valveBytes), "Valve's cs2_video.txt.bak is byte-identical after both writes");

                string root2 = NewTempRoot();
                try
                {
                    string file2 = WriteVideo(root2, "1001", original);
                    Cs2VideoSyncResult same = WriterFor(root2).Apply(Mode(1280, 1024, 260));
                    c.Check(same.IsFullSuccess && same.Accounts.Count == 1 && same.Accounts[0].Status == Cs2VideoAccountStatus.AlreadyCurrent,
                        "values already current: AlreadyCurrent counts as success");
                    c.Check(!File.Exists(file2 + Cs2VideoSettingsWriter.BackupSuffix) && !File.Exists(file2 + Cs2VideoSettingsWriter.TempSuffix) &&
                            BytesEqual(File.ReadAllBytes(file2), original),
                        "AlreadyCurrent creates no backup or temp and leaves the bytes alone");
                }
                finally
                {
                    Cleanup(root2);
                }
            }
            finally
            {
                Cleanup(root);
            }
        }

        // ---- 12 -----------------------------------------------------------------------------

        private static void CheckIsCs2Executable(Checklist c)
        {
            c.Check(Cs2VideoSettingsWriter.IsCs2Executable(@"C:\x\CS2.EXE"), "C:\\x\\CS2.EXE is cs2.exe");
            c.Check(!Cs2VideoSettingsWriter.IsCs2Executable(@"C:\x\cs2.exe.bak"), "cs2.exe.bak is not");
            c.Check(!Cs2VideoSettingsWriter.IsCs2Executable(@"C:\x\notcs2.exe"), "notcs2.exe is not");
            c.Check(!Cs2VideoSettingsWriter.IsCs2Executable(null), "null is not");
            c.Check(!Cs2VideoSettingsWriter.IsCs2Executable(string.Empty), "empty string is not");
            c.Check(!Cs2VideoSettingsWriter.IsCs2Executable("C:\\x\\bad|name\0.exe"), "an invalid-path string is not (and does not throw)");
        }

        // ---- 13 -----------------------------------------------------------------------------

        private static void CheckSettingRoundTrip(Checklist c)
        {
            // List<ApplicationSetting>, exactly what SettingsController persists.
            XmlSerializer serializer = new XmlSerializer(typeof(List<ApplicationSetting>));

            List<ApplicationSetting> old;
            using (StringReader reader = new StringReader("<?xml version=\"1.0\" encoding=\"utf-16\"?><ArrayOfApplicationSetting><ApplicationSetting><Name>cs2</Name><IngameLevel>10</IngameLevel></ApplicationSetting></ArrayOfApplicationSetting>"))
            {
                old = (List<ApplicationSetting>)serializer.Deserialize(reader);
            }
            c.Check(old.Count == 1 && !old[0].SyncCs2VideoSettings, "XML without <SyncCs2VideoSettings> deserializes to false");

            ApplicationSetting setting = new ApplicationSetting();
            setting.Name = "cs2";
            setting.FileName = "cs2.exe";
            setting.SyncCs2VideoSettings = true;
            string xml;
            using (StringWriter writer = new StringWriter())
            {
                serializer.Serialize(writer, new List<ApplicationSetting> { setting });
                xml = writer.ToString();
            }
            List<ApplicationSetting> back;
            using (StringReader reader = new StringReader(xml))
            {
                back = (List<ApplicationSetting>)serializer.Deserialize(reader);
            }
            c.Check(back.Count == 1 && back[0].SyncCs2VideoSettings, "SyncCs2VideoSettings = true round-trips through XmlSerializer");
            c.Check(!new ApplicationSetting().SyncCs2VideoSettings, "a new ApplicationSetting defaults to false");
        }

        // ---- 13b: adversarial patcher inputs -------------------------------------------------

        private static void CheckPatcherEdgeCases(Checklist c)
        {
            string head = "\"video.cfg\"\r\n{\r\n";
            string tail = "setting.defaultres\"\t\"1920\"\r\n\t\"setting.defaultresheight\"\t\"1080\"\r\n";

            // an owned key name used as a VALUE is not a key; the real key is still inserted
            string asValue = head + "\t\"foo\"\t\"setting.defaultres\"\r\n\t\"setting.defaultresheight\"\t\"1024\"\r\n}\r\n";
            Cs2VideoPatchResult av = Cs2VideoConfigPatcher.Patch(asValue, 1920, 1080, 0);
            c.Check(av.Content == head + "\t\"foo\"\t\"setting.defaultres\"\r\n\t\"setting.defaultresheight\"\t\"1080\"\r\n\t\"" + "setting.defaultres\"\t\"1920\"\r\n}\r\n",
                "an owned key name sitting in VALUE position is left alone and the real key is inserted");

            // an escaped quote inside a value does not end the string
            string escaped = head + "\t\"name\"\t\"a\\\"b setting.defaultres\"\r\n\t\"setting.defaultres\"\t\"1\"\r\n\t\"setting.defaultresheight\"\t\"2\"\r\n}\r\n";
            Cs2VideoPatchResult es = Cs2VideoConfigPatcher.Patch(escaped, 1920, 1080, 0);
            c.Check(es.Content == head + "\t\"name\"\t\"a\\\"b setting.defaultres\"\r\n\t\"" + tail + "}\r\n",
                "an escaped quote inside another value does not confuse key/value pairing");

            // a trailing comment on the value line, and one after the closing brace
            string commentTail = head + "\t\"setting.defaultres\"\t\"1\" // note\r\n\t\"setting.defaultresheight\"\t\"2\"\r\n} // end";
            Cs2VideoPatchResult ct = Cs2VideoConfigPatcher.Patch(commentTail, 1920, 1080, 0);
            c.Check(ct.Content == head + "\t\"setting.defaultres\"\t\"1920\" // note\r\n\t\"setting.defaultresheight\"\t\"1080\"\r\n} // end",
                "comments after a value and after the closing brace, no trailing newline: only the values change");

            // no trailing newline, keys missing: inserted before } and the file still has no trailing newline
            string noNl = head + "\t\"Version\"\t\"16\"\r\n}";
            Cs2VideoPatchResult nn = Cs2VideoConfigPatcher.Patch(noNl, 1920, 1080, 144);
            c.Check(nn.Content != null && nn.Content.EndsWith("\t\"setting.refreshrate_denominator\"\t\"1000\"\r\n}", StringComparison.Ordinal) &&
                    nn.Content.StartsWith(head + "\t\"Version\"\t\"16\"\r\n", StringComparison.Ordinal),
                "file without a trailing newline: keys inserted before } and no newline appended after it");

            // whitespace-only line in front of }
            string wsLine = head + "\t\"Version\"\t\"16\"\r\n   \r\n}\r\n";
            Cs2VideoPatchResult ws = Cs2VideoConfigPatcher.Patch(wsLine, 1920, 1080, 0);
            c.Check(ws.Content == head + "\t\"Version\"\t\"16\"\r\n   \r\n\t\"setting.defaultres\"\t\"1920\"\r\n\t\"setting.defaultresheight\"\t\"1080\"\r\n}\r\n",
                "a whitespace-only line before } is kept; the keys go on their own lines after it");

            // key name case is matched loosely and the file's spelling is kept
            string caps = head + "\t\"SETTING.DefaultRes\"\t\"1\"\r\n\t\"Setting.DefaultResHeight\"\t\"2\"\r\n}\r\n";
            Cs2VideoPatchResult cp = Cs2VideoConfigPatcher.Patch(caps, 1920, 1080, 0);
            c.Check(cp.Content == head + "\t\"SETTING.DefaultRes\"\t\"1920\"\r\n\t\"Setting.DefaultResHeight\"\t\"1080\"\r\n}\r\n",
                "owned keys are matched case-insensitively and keep the file's spelling");

            // uint extremes: no overflow in Hz * 1000
            Cs2VideoPatchResult big = Cs2VideoConfigPatcher.Patch(head + "}\r\n", 4294967295, 4294967295, 4294967295);
            c.Check(big.Content != null && big.Content.Contains("\"4294967295000\"") && Count(big.Content, "\"4294967295\"") == 2,
                "uint.MaxValue width, height and Hz are written without overflow (4294967295000/1000)");

            // an owned key with a block value is refused, never guessed at
            Cs2VideoPatchResult blockValue = Cs2VideoConfigPatcher.Patch(head + "\t\"setting.defaultres\"\r\n\t{\r\n\t}\r\n}\r\n", 1920, 1080, 0);
            c.Check(blockValue.Status == Cs2VideoPatchStatus.Malformed, "an owned key whose value is a block is Malformed");

            // only the first root block is edited
            string twoRoots = head + "\t\"a\"\t\"b\"\r\n}\r\n\"other\"\r\n{\r\n}\r\n";
            Cs2VideoPatchResult tr = Cs2VideoConfigPatcher.Patch(twoRoots, 1920, 1080, 0);
            c.Check(tr.Content != null && tr.Content.EndsWith("}\r\n\"other\"\r\n{\r\n}\r\n", StringComparison.Ordinal) && Count(tr.Content, "setting.defaultres\"") == 1,
                "content after the root block's closing brace is not touched");

            // BOM bytes read as Latin-1 in front of the root name
            string bom = "ï»¿" + head + "\t\"setting.defaultres\"\t\"1\"\r\n\t\"setting.defaultresheight\"\t\"2\"\r\n}\r\n";
            Cs2VideoPatchResult bm = Cs2VideoConfigPatcher.Patch(bom, 1920, 1080, 0);
            c.Check(bm.Content == "ï»¿" + head + "\t\"" + tail + "}\r\n", "a BOM in front of the root name survives byte for byte");

            // a large file: unrelated pairs stay intact
            StringBuilder big2 = new StringBuilder(head);
            for (int i = 0; i < 20000; i++)
                big2.Append("\t\"key").Append(i).Append("\"\t\t\"v").Append(i).Append("\"\r\n");
            string bigIn = big2.ToString() + "\t\"setting.defaultres\"\t\"1\"\r\n\t\"setting.defaultresheight\"\t\"2\"\r\n}\r\n";
            Cs2VideoPatchResult bg = Cs2VideoConfigPatcher.Patch(bigIn, 1920, 1080, 0);
            c.Check(bg.Content == big2.ToString() + "\t\"" + tail + "}\r\n", "a 20000-pair file is patched correctly");

            // with no refresh rate to apply (0 or 1), a strange refresh entry does not block the resolution sync
            string oddRefresh = head + "\t\"setting.refreshrate_numerator\"\t\t144\r\n\t\"setting.defaultres\"\t\"1\"\r\n\t\"setting.defaultresheight\"\t\"2\"\r\n}\r\n";
            Cs2VideoPatchResult o0 = Cs2VideoConfigPatcher.Patch(oddRefresh, 1920, 1080, 0);
            Cs2VideoPatchResult o1 = Cs2VideoConfigPatcher.Patch(oddRefresh, 1920, 1080, 1);
            Cs2VideoPatchResult o2 = Cs2VideoConfigPatcher.Patch(oddRefresh, 1920, 1080, 2);
            string oddExpected = head + "\t\"setting.refreshrate_numerator\"\t\t144\r\n\t\"" + tail + "}\r\n";
            c.Check(o0.Content == oddExpected && o1.Content == oddExpected, "an unquoted refresh value does not block the size sync at Hz 0 and 1");
            c.Check(o2.Status == Cs2VideoPatchStatus.Malformed, "...but at Hz 2 the same unquoted refresh value is Malformed");

            // an unquoted word in KEY position would put every following pair out of step: refused
            string fourKeys = "\t\"setting.defaultres\"\t\"1\"\r\n\t\"setting.defaultresheight\"\t\"2\"\r\n" +
                              "\t\"setting.refreshrate_numerator\"\t\"3\"\r\n\t\"setting.refreshrate_denominator\"\t\"4\"\r\n";
            string bareKey = head + "\tVersion\t\"16\"\r\n" + fourKeys + "}\r\n";
            Cs2VideoPatchResult bk = Cs2VideoConfigPatcher.Patch(bareKey, 1920, 1080, 144);
            c.Check(bk.Status == Cs2VideoPatchStatus.Malformed && bk.Content == null, "a bare word in key position (Version \"16\") is Malformed, not appended as duplicates");
            string baseLine = head + "#base \"x\"\r\n" + fourKeys + "}\r\n";
            c.Check(Cs2VideoConfigPatcher.Patch(baseLine, 1920, 1080, 144).Status == Cs2VideoPatchStatus.Malformed, "#base \"x\" is Malformed");
            string includeLine = head + "\t#include \"x\"\r\n" + fourKeys + "}\r\n";
            c.Check(Cs2VideoConfigPatcher.Patch(includeLine, 1920, 1080, 0).Status == Cs2VideoPatchStatus.Malformed, "#include \"x\" is Malformed (also at Hz 0)");
            string bareInNested = head + "\t\"nested\"\r\n\t{\r\n\t\tfoo \"bar\"\r\n\t}\r\n" + fourKeys + "}\r\n";
            c.Check(Cs2VideoConfigPatcher.Patch(bareInNested, 1920, 1080, 144).Status == Cs2VideoPatchStatus.Patched, "a bare word inside a nested block is still tolerated");

            // CR-only files keep CR-only line breaks
            string crMin = Minimal("\r", "\t", "\t\t", string.Empty);
            Cs2VideoPatchResult cr = Cs2VideoConfigPatcher.Patch(crMin, 1920, 1080, 144);
            string crExpected = Minimal("\r", "\t", "\t\t",
                Line("\t", "\t\t", "setting.defaultres", "1920", "\r") + Line("\t", "\t\t", "setting.defaultresheight", "1080", "\r") +
                Line("\t", "\t\t", "setting.refreshrate_numerator", "144000", "\r") + Line("\t", "\t\t", "setting.refreshrate_denominator", "1000", "\r"));
            c.Check(cr.Status == Cs2VideoPatchStatus.Patched && cr.Content == crExpected && cr.Content.IndexOf('\n') < 0,
                "CR-only file: keys inserted with CR line breaks, no LF introduced");
            string crComment = "\"video.cfg\"\r{\r\t// note\r\t\"setting.defaultres\"\t\"1\"\r\t\"setting.defaultresheight\"\t\"2\"\r}\r";
            Cs2VideoPatchResult crc = Cs2VideoConfigPatcher.Patch(crComment, 1920, 1080, 0);
            c.Check(crc.Content == crComment.Replace("\"1\"", "\"1920\"").Replace("\"2\"", "\"1080\""),
                "CR-only file: a // comment ends at the CR, the keys after it are patched");
        }

        // ---- 13c: adversarial writer scenarios -----------------------------------------------

        private static void CheckWriterEdgeCases(Checklist c)
        {
            byte[] original = Bytes(Sample(Crlf, "1280", "1024", "260002", "1000"));
            byte[] patched = Bytes(Sample(Crlf, "1920", "1080", "144000", "1000"));

            // folders that are not Steam account ids are never touched, even when they hold a file
            string root = NewTempRoot();
            try
            {
                string good = WriteVideo(root, "1001", original);
                string anon = WriteVideo(root, "anonymous", original);
                string mixed = WriteVideo(root, "12ab", original);
                Cs2VideoSyncResult res = WriterFor(root).Apply(Mode(1920, 1080, 144));
                c.Check(res.Status == Cs2VideoSyncStatus.Completed && res.AccountCount == 1 && res.IsFullSuccess,
                    "only the all-digit account folder is counted (" + res.AccountCount + ")");
                c.Check(BytesEqual(File.ReadAllBytes(good), patched), "the all-digit account is patched");
                c.Check(BytesEqual(File.ReadAllBytes(anon), original) && BytesEqual(File.ReadAllBytes(mixed), original) &&
                        !File.Exists(anon + Cs2VideoSettingsWriter.BackupSuffix) && !File.Exists(mixed + Cs2VideoSettingsWriter.BackupSuffix),
                    "'anonymous' and '12ab' folders holding a cs2_video.txt are left byte-identical with no backup");
            }
            finally
            {
                Cleanup(root);
            }

            // idempotent: saving the same profile twice changes nothing the second time
            root = NewTempRoot();
            try
            {
                string file = WriteVideo(root, "1001", original);
                WriterFor(root).Apply(Mode(1920, 1080, 144));
                Cs2VideoSyncResult again = WriterFor(root).Apply(Mode(1920, 1080, 144));
                c.Check(again.IsFullSuccess && again.Accounts[0].Status == Cs2VideoAccountStatus.AlreadyCurrent && BytesEqual(File.ReadAllBytes(file), patched) &&
                        BytesEqual(File.ReadAllBytes(file + Cs2VideoSettingsWriter.BackupSuffix), original),
                    "a repeated save is AlreadyCurrent and the backup still holds the very first original");
            }
            finally
            {
                Cleanup(root);
            }

            // a backup that already exists (user's own, or from an earlier session) is never overwritten
            root = NewTempRoot();
            try
            {
                string file = WriteVideo(root, "1001", original);
                byte[] precious = new byte[] { 9, 8, 7, 200 };
                File.WriteAllBytes(file + Cs2VideoSettingsWriter.BackupSuffix, precious);
                Cs2VideoSyncResult res = WriterFor(root).Apply(Mode(1920, 1080, 144));
                c.Check(res.IsFullSuccess && BytesEqual(File.ReadAllBytes(file), patched) &&
                        BytesEqual(File.ReadAllBytes(file + Cs2VideoSettingsWriter.BackupSuffix), precious),
                    "a pre-existing .vibrance.bak is not overwritten and the write still happens");
            }
            finally
            {
                Cleanup(root);
            }

            // a stale temp file from a crashed run is replaced, and nothing is left behind
            root = NewTempRoot();
            try
            {
                string file = WriteVideo(root, "1001", original);
                File.WriteAllBytes(file + Cs2VideoSettingsWriter.TempSuffix, new byte[] { 1, 2, 3 });
                Cs2VideoSyncResult res = WriterFor(root).Apply(Mode(1920, 1080, 144));
                c.Check(res.IsFullSuccess && BytesEqual(File.ReadAllBytes(file), patched) && !File.Exists(file + Cs2VideoSettingsWriter.TempSuffix),
                    "a stale .vibrance.tmp does not block the write and is gone afterwards");
            }
            finally
            {
                Cleanup(root);
            }

            // the swap cannot happen (temp path is occupied by a directory): original intact, no exception,
            // the failure is reported, and the next account is still processed
            root = NewTempRoot();
            try
            {
                string blocked = WriteVideo(root, "1001", original);
                string fine = WriteVideo(root, "1002", original);
                Directory.CreateDirectory(blocked + Cs2VideoSettingsWriter.TempSuffix);
                Cs2VideoSyncResult res = null;
                bool threw = false;
                try { res = WriterFor(root).Apply(Mode(1920, 1080, 144)); } catch (Exception) { threw = true; }
                c.Check(!threw && res != null && res.Status == Cs2VideoSyncStatus.Completed, "a write failure does not throw");
                c.Check(res != null && Status(res, "1001").HasValue && Status(res, "1001") != Cs2VideoAccountStatus.Updated &&
                        Status(res, "1001") != Cs2VideoAccountStatus.AlreadyCurrent && res.SucceededCount == 1,
                    "the blocked account is reported as failed, 1 of 2 succeeded");
                c.Check(BytesEqual(File.ReadAllBytes(blocked), original), "the blocked account's original file is intact");
                c.Check(BytesEqual(File.ReadAllBytes(fine), patched), "the account after the failing one is still written");
                c.Check(res != null && res.UserMessage.StartsWith("CS2 video settings: updated 1 of 2 accounts.", StringComparison.Ordinal) &&
                        res.UserMessage.Contains("Account 1001:"), "the message names the failed account");
            }
            finally
            {
                Cleanup(root);
            }

            // File.Replace itself fails (another process holds the file open for reading without FileShare.Delete):
            // the read and the temp write succeed, the swap does not - original intact, temp removed, reported Locked
            root = NewTempRoot();
            FileStream reader = null;
            try
            {
                string file = WriteVideo(root, "1001", original);
                reader = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                Cs2VideoSyncResult res = WriterFor(root).Apply(Mode(1920, 1080, 144));
                c.Check(res.Status == Cs2VideoSyncStatus.Completed && Status(res, "1001") == Cs2VideoAccountStatus.Locked && res.SucceededCount == 0,
                    "a failing File.Replace is reported as Locked (" + Status(res, "1001") + ")");
                c.Check(!File.Exists(file + Cs2VideoSettingsWriter.TempSuffix), "a failing File.Replace leaves no .vibrance.tmp behind");
                reader.Dispose();
                reader = null;
                c.Check(BytesEqual(File.ReadAllBytes(file), original), "a failing File.Replace leaves the original file intact");
            }
            finally
            {
                if (reader != null)
                    reader.Dispose();
                Cleanup(root);
            }

            // probes that throw: running-probe failure means "assume running", Steam lookup failure means "not found"
            root = NewTempRoot();
            try
            {
                string file = WriteVideo(root, "1001", original);
                Cs2VideoSyncResult t1 = new Cs2VideoSettingsWriter(() => root, () => { throw new InvalidOperationException("probe"); }).Apply(Mode(1920, 1080, 144));
                c.Check(t1.Status == Cs2VideoSyncStatus.Cs2Running && BytesEqual(File.ReadAllBytes(file), original) &&
                        !File.Exists(file + Cs2VideoSettingsWriter.BackupSuffix),
                    "a throwing is-CS2-running probe counts as running: nothing written, no backup");
                Cs2VideoSyncResult t2 = new Cs2VideoSettingsWriter(() => { throw new InvalidOperationException("steam"); }, () => false).Apply(Mode(1920, 1080, 144));
                c.Check(t2.Status == Cs2VideoSyncStatus.SteamNotFound && BytesEqual(File.ReadAllBytes(file), original), "a throwing Steam lookup is SteamNotFound");
            }
            finally
            {
                Cleanup(root);
            }

            // empty and whitespace-only files are Malformed, never written, never backed up
            root = NewTempRoot();
            try
            {
                string empty = WriteVideo(root, "1001", new byte[0]);
                string blank = WriteVideo(root, "1002", Bytes("  \r\n\t\r\n"));
                Cs2VideoSyncResult res = WriterFor(root).Apply(Mode(1920, 1080, 144));
                c.Check(Status(res, "1001") == Cs2VideoAccountStatus.Malformed && Status(res, "1002") == Cs2VideoAccountStatus.Malformed &&
                        File.ReadAllBytes(empty).Length == 0 && BytesEqual(File.ReadAllBytes(blank), Bytes("  \r\n\t\r\n")) &&
                        !File.Exists(empty + Cs2VideoSettingsWriter.BackupSuffix) && !File.Exists(blank + Cs2VideoSettingsWriter.BackupSuffix),
                    "empty and whitespace-only files are Malformed, unchanged and not backed up");
            }
            finally
            {
                Cleanup(root);
            }

            // read-only and already at the target: nothing to write, so it is AlreadyCurrent (a success) and the
            // attribute is left alone; a read-only file that needs a change is still ReadOnly
            root = NewTempRoot();
            try
            {
                string file = WriteVideo(root, "1001", patched);
                File.SetAttributes(file, FileAttributes.ReadOnly);
                Cs2VideoSyncResult res = WriterFor(root).Apply(Mode(1920, 1080, 144));
                c.Check(res.IsFullSuccess && Status(res, "1001") == Cs2VideoAccountStatus.AlreadyCurrent,
                    "read-only file already at the target: AlreadyCurrent, counts as success (" + Status(res, "1001") + ")");
                c.Check((File.GetAttributes(file) & FileAttributes.ReadOnly) != 0 && BytesEqual(File.ReadAllBytes(file), patched) &&
                        !File.Exists(file + Cs2VideoSettingsWriter.BackupSuffix),
                    "...the read-only attribute is still set, bytes unchanged, no backup");
                Cs2VideoSyncResult needs = WriterFor(root).Apply(Mode(2560, 1440, 240));
                c.Check(Status(needs, "1001") == Cs2VideoAccountStatus.ReadOnly && !needs.IsFullSuccess && BytesEqual(File.ReadAllBytes(file), patched),
                    "read-only file that needs a change is still ReadOnly and untouched");
            }
            finally
            {
                Cleanup(root);
            }

            // a read-only malformed file is Malformed (the read and patch come first)
            root = NewTempRoot();
            try
            {
                string file = WriteVideo(root, "1001", Bytes("garbage"));
                File.SetAttributes(file, FileAttributes.ReadOnly);
                c.Check(Status(WriterFor(root).Apply(Mode(1920, 1080, 144)), "1001") == Cs2VideoAccountStatus.Malformed, "a read-only malformed file is reported Malformed");
            }
            finally
            {
                Cleanup(root);
            }

            // a bare word in key position: Malformed through the writer, file untouched, no backup or temp
            root = NewTempRoot();
            try
            {
                byte[] bareBytes = Bytes("\"video.cfg\"\r\n{\r\n\tVersion\t\"16\"\r\n\t\"setting.defaultres\"\t\"1\"\r\n\t\"setting.defaultresheight\"\t\"2\"\r\n}\r\n");
                string file = WriteVideo(root, "1001", bareBytes);
                Cs2VideoSyncResult res = WriterFor(root).Apply(Mode(1920, 1080, 144));
                c.Check(Status(res, "1001") == Cs2VideoAccountStatus.Malformed && BytesEqual(File.ReadAllBytes(file), bareBytes) &&
                        !File.Exists(file + Cs2VideoSettingsWriter.BackupSuffix) && !File.Exists(file + Cs2VideoSettingsWriter.TempSuffix),
                    "bare key line through the writer: Malformed, file byte-identical, no backup or temp");
            }
            finally
            {
                Cleanup(root);
            }

            // CR-only file through the writer stays CR-only after an insertion
            root = NewTempRoot();
            try
            {
                string file = WriteVideo(root, "1001", Bytes(Minimal("\r", "\t", "\t\t", string.Empty)));
                Cs2VideoSyncResult res = WriterFor(root).Apply(Mode(1920, 1080, 144));
                byte[] after = File.ReadAllBytes(file);
                c.Check(res.IsFullSuccess && Array.IndexOf(after, (byte)'\n') < 0 && Array.IndexOf(after, (byte)'\r') >= 0 &&
                        Latin1.GetString(after).Contains("\"setting.refreshrate_denominator\"\t\t\"1000\"\r}\r"),
                    "CR-only file stays CR-only after insertion (no LF written)");
            }
            finally
            {
                Cleanup(root);
            }

            // an access error that is not the read-only attribute (here: the backup path is a directory) is Locked, not ReadOnly
            root = NewTempRoot();
            try
            {
                string file = WriteVideo(root, "1001", original);
                Directory.CreateDirectory(file + Cs2VideoSettingsWriter.BackupSuffix);
                Cs2VideoSyncResult res = WriterFor(root).Apply(Mode(1920, 1080, 144));
                Cs2VideoAccountOutcome o = res.Accounts.Count == 1 ? res.Accounts[0] : null;
                c.Check(o != null && o.Status == Cs2VideoAccountStatus.Locked && !string.IsNullOrEmpty(o.Detail) && BytesEqual(File.ReadAllBytes(file), original),
                    "a non-read-only access failure is Locked with the exception message as Detail, file intact (" + (o == null ? "no outcome" : o.Status.ToString()) + ")");
                c.Check(res.UserMessage.Contains("Account 1001: the file is in use or could not be written, not changed.") &&
                        !res.UserMessage.Contains("read-only"),
                    "...and the message does not claim the file is read-only");
            }
            finally
            {
                Cleanup(root);
            }

            // the unexpected-exception backstop: Failed when nothing was processed, Completed when something was
            Cs2VideoSyncResult failed = new FaultingWriter(false).Apply(Mode(1920, 1080, 144));
            c.Check(failed.Status == Cs2VideoSyncStatus.Failed && !failed.IsFullSuccess && failed.Accounts.Count == 0 &&
                    failed.UserMessage == "CS2 video settings could not be updated because of an unexpected error; nothing else was changed.",
                "unexpected exception before any account: Failed, not a full success, exact message (not the no-userdata one)");
            Cs2VideoSyncResult partial = new FaultingWriter(true).Apply(Mode(1920, 1080, 144));
            c.Check(partial.Status == Cs2VideoSyncStatus.Completed && partial.AccountCount == 2 && !partial.IsFullSuccess &&
                    partial.UserMessage.StartsWith("CS2 video settings: updated 1 of 2 accounts.", StringComparison.Ordinal),
                "unexpected exception after accounts were processed: Completed with what was recorded, not a full success");

            // bytes that are not valid UTF-8 (Latin-1 accents, 0x80, 0xFF) elsewhere in the file survive untouched
            root = NewTempRoot();
            try
            {
                string head = "\"video.cfg\"\r\n{\r\n\t\"name\"\t\"caf";
                byte[] odd8 = new byte[] { 0xE9, 0x80, 0xFF, 0xC3 };
                List<byte> inBytes = new List<byte>(Bytes(head));
                inBytes.AddRange(odd8);
                inBytes.AddRange(Bytes("\"\r\n\t\"setting.defaultres\"\t\"1\"\r\n\t\"setting.defaultresheight\"\t\"2\"\r\n}\r\n"));
                List<byte> outBytes = new List<byte>(Bytes(head));
                outBytes.AddRange(odd8);
                outBytes.AddRange(Bytes("\"\r\n\t\"setting.defaultres\"\t\"1920\"\r\n\t\"setting.defaultresheight\"\t\"1080\"\r\n}\r\n"));
                string file = WriteVideo(root, "1001", inBytes.ToArray());
                Cs2VideoSyncResult res = WriterFor(root).Apply(Mode(1920, 1080, 0));
                c.Check(res.IsFullSuccess && BytesEqual(File.ReadAllBytes(file), outBytes.ToArray()),
                    "non-UTF-8 bytes (0xE9 0x80 0xFF 0xC3) in another value come back byte-identical");
            }
            finally
            {
                Cleanup(root);
            }

            // a Steam path with spaces, parentheses and non-ASCII characters
            string odd = Path.Combine(NewTempRoot(), "Steam (x86) é中");
            try
            {
                Directory.CreateDirectory(odd);
                string file = WriteVideo(odd, "1001", original);
                Cs2VideoSyncResult res = WriterFor(odd).Apply(Mode(1920, 1080, 144));
                c.Check(res.IsFullSuccess && BytesEqual(File.ReadAllBytes(file), patched), "a Steam root with spaces, parentheses and non-ASCII characters works");
            }
            finally
            {
                Cleanup(Directory.GetParent(odd).FullName);
            }

            // refresh rate 0 (unknown) and 1 (hardware default marker) through the writer: resolution only
            root = NewTempRoot();
            try
            {
                string file = WriteVideo(root, "1001", original);
                Cs2VideoSyncResult res = WriterFor(root).Apply(Mode(1920, 1080, 1));
                c.Check(res.IsFullSuccess && BytesEqual(File.ReadAllBytes(file), Bytes(Sample(Crlf, "1920", "1080", "260002", "1000"))),
                    "Hz 1 through the writer syncs the size and leaves the refresh pair alone");
            }
            finally
            {
                Cleanup(root);
            }
        }

        private sealed class FaultingWriter : Cs2VideoSettingsWriter
        {
            private readonly bool _recordAccountsFirst;

            internal FaultingWriter(bool recordAccountsFirst) : base(() => null, () => false)
            {
                _recordAccountsFirst = recordAccountsFirst;
            }

            internal override void ApplyCore(ResolutionModeWrapper mode, Cs2VideoSyncResult result)
            {
                if (_recordAccountsFirst)
                {
                    Cs2VideoAccountOutcome a = new Cs2VideoAccountOutcome();
                    a.AccountId = "1001";
                    a.Status = Cs2VideoAccountStatus.Updated;
                    result.Accounts.Add(a);
                    Cs2VideoAccountOutcome b = new Cs2VideoAccountOutcome();
                    b.AccountId = "1002";
                    b.Status = Cs2VideoAccountStatus.Locked;
                    result.Accounts.Add(b);
                }
                throw new InvalidOperationException("fixture fault");
            }
        }

        // ---- 14 -----------------------------------------------------------------------------
        // The real VibranceSettings form, constructed but never shown (the same approach as
        // ResolutionCatalogFixture's dialog checks).

        // Control.Visible is false for every child of a form that was never shown, so read the
        // control's own visible flag (STATE_VISIBLE = 2) instead.
        private static bool OwnVisible(Control control)
        {
            MethodInfo getState = typeof(Control).GetMethod("GetState", BindingFlags.Instance | BindingFlags.NonPublic, null, new Type[] { typeof(int) }, null);
            return (bool)getState.Invoke(control, new object[] { 2 });
        }

        private static VibranceSettings BuildDialog(string filePath, ApplicationSetting setting, out ListView listView)
        {
            listView = new ListView();
            listView.LargeImageList = new ImageList();
            listView.LargeImageList.Images.Add(new Bitmap(4, 4));
            ListViewItem item = new ListViewItem("fixture");
            item.ImageIndex = 0;
            item.Tag = filePath;
            listView.Items.Add(item);
            ResolutionModeWrapper native = Mode(1920, 1080, 60);
            List<ResolutionModeWrapper> modes = new List<ResolutionModeWrapper> { native, Mode(1280, 720, 60) };
            return new VibranceSettings(new ResolutionCatalogFixture.FakeProxy(), 0, 63, 20, item, setting, modes, native, null, null, GraphicsAdapter.Nvidia);
        }

        private static CheckBox Box(VibranceSettings dialog, string field)
        {
            return (CheckBox)typeof(VibranceSettings).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(dialog);
        }

        private static void CheckDialog(Checklist c)
        {
            ListView lv1, lv2, lv3, lv4;

            // cs2.exe: visible, tracks "Change Resolution", saved when ticked
            using (VibranceSettings dialog = BuildDialog("C:\\fixture\\Steam\\cs2.exe", null, out lv1))
            {
                CheckBox sync = Box(dialog, "checkBoxSyncCs2Video");
                CheckBox change = Box(dialog, "checkBoxResolution");
                c.Check(OwnVisible(sync), "cs2.exe profile: the sync checkbox is visible");
                c.Check(!sync.Checked && !sync.Enabled, "new cs2.exe profile: unticked, disabled while Change Resolution is off");
                c.Check(!dialog.GetApplicationSetting().SyncCs2VideoSettings, "unticked saves false");
                change.Checked = true;
                c.Check(sync.Enabled, "ticking Change Resolution enables the sync checkbox");
                sync.Checked = true;
                c.Check(dialog.GetApplicationSetting().SyncCs2VideoSettings, "ticked cs2.exe profile saves SyncCs2VideoSettings = true");
                change.Checked = false;
                c.Check(!sync.Enabled, "unticking Change Resolution disables the sync checkbox again");
                c.Check(sync.Checked && !dialog.GetApplicationSetting().SyncCs2VideoSettings,
                    "ticked sync + unticked Change Resolution (disabled-but-checked) saves false");
                change.Checked = true;
                typeof(VibranceSettings).GetMethod("buttonReset_Click", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(dialog, new object[] { null, EventArgs.Empty });
                c.Check(!sync.Checked && !dialog.GetApplicationSetting().SyncCs2VideoSettings, "Reset values unticks the sync checkbox");
            }
            lv1.Dispose();

            // Case-insensitive basename
            using (VibranceSettings dialog = BuildDialog("C:\\fixture\\CS2.EXE", null, out lv2))
            {
                c.Check(OwnVisible(Box(dialog, "checkBoxSyncCs2Video")), "CS2.EXE (any case) shows the checkbox");
            }
            lv2.Dispose();

            // Not cs2: hidden, and a ticked box is never saved
            using (VibranceSettings dialog = BuildDialog("C:\\fixture\\game.exe", null, out lv3))
            {
                CheckBox sync = Box(dialog, "checkBoxSyncCs2Video");
                c.Check(!OwnVisible(sync), "non-CS2 profile: the sync checkbox is hidden");
                Box(dialog, "checkBoxResolution").Checked = true;
                sync.Checked = true;
                c.Check(!dialog.GetApplicationSetting().SyncCs2VideoSettings, "non-CS2 profile saves false even when the box is ticked");
            }
            lv3.Dispose();

            // A loaded setting shows its stored value; a stale true on a non-cs2 path is not kept
            ApplicationSetting stored = new ApplicationSetting();
            stored.Name = "cs2";
            stored.FileName = "C:\\fixture\\cs2.exe";
            stored.IngameLevel = 30;
            stored.IsResolutionChangeNeeded = true;
            stored.ResolutionSettings = Mode(1920, 1080, 60);
            stored.SyncCs2VideoSettings = true;
            using (VibranceSettings dialog = BuildDialog("C:\\fixture\\cs2.exe", stored, out lv4))
            {
                CheckBox sync = Box(dialog, "checkBoxSyncCs2Video");
                c.Check(sync.Checked && sync.Enabled && OwnVisible(sync), "a loaded SyncCs2VideoSettings = true shows ticked, enabled and visible");
                c.Check(dialog.GetApplicationSetting().SyncCs2VideoSettings, "an untouched loaded true is saved as true");
            }
            lv4.Dispose();

            // "Change executable..." re-evaluates visibility, and a ticked box does not follow the profile to a non-CS2 exe
            ListView lv5;
            using (VibranceSettings dialog = BuildDialog("C:\\fixture\\cs2.exe", null, out lv5))
            {
                CheckBox sync = Box(dialog, "checkBoxSyncCs2Video");
                Box(dialog, "checkBoxResolution").Checked = true;
                sync.Checked = true;
                FieldInfo filePath = typeof(VibranceSettings).GetField("_filePath", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo refresh = typeof(VibranceSettings).GetMethod("updateCs2Profile", BindingFlags.Instance | BindingFlags.NonPublic);

                filePath.SetValue(dialog, "C:\\fixture\\game.exe");
                refresh.Invoke(dialog, null);
                c.Check(!OwnVisible(sync), "changing the executable to a non-CS2 exe hides the checkbox");
                c.Check(!dialog.GetApplicationSetting().SyncCs2VideoSettings, "...and a ticked box is saved as false");

                filePath.SetValue(dialog, "C:\\other\\Cs2.Exe");
                refresh.Invoke(dialog, null);
                c.Check(OwnVisible(sync) && dialog.GetApplicationSetting().SyncCs2VideoSettings, "changing the executable to another cs2.exe shows it again");
            }
            lv5.Dispose();

            // A saved mode the display no longer offers: the box is disabled, the tick is kept, the write is gated
            ListView lv6;
            ApplicationSetting stale = new ApplicationSetting();
            stale.Name = "cs2";
            stale.FileName = "C:\\fixture\\cs2.exe";
            stale.IngameLevel = 30;
            stale.IsResolutionChangeNeeded = true;
            stale.ResolutionSettings = Mode(3840, 2160, 60);
            stale.SyncCs2VideoSettings = true;
            using (VibranceSettings dialog = BuildDialog("C:\\fixture\\cs2.exe", stale, out lv6))
            {
                CheckBox sync = Box(dialog, "checkBoxSyncCs2Video");
                c.Check(!dialog.IsSelectedModeOffered, "a saved mode the display does not offer: IsSelectedModeOffered is false");
                c.Check(sync.Checked && !sync.Enabled, "...the sync checkbox stays ticked but is disabled");
                c.Check(dialog.GetApplicationSetting().SyncCs2VideoSettings, "...and the tick is still persisted so it applies again if the mode returns");
            }
            lv6.Dispose();

            ListView lv7;
            using (VibranceSettings dialog = BuildDialog("C:\\fixture\\cs2.exe", stored, out lv7))
            {
                c.Check(dialog.IsSelectedModeOffered, "an offered saved mode: IsSelectedModeOffered is true");
            }
            lv7.Dispose();
        }

        private class Checklist
        {
            public readonly List<string> Lines = new List<string>();
            public int Passed;
            public int Total;

            public void Check(bool condition, string description)
            {
                Total++;
                if (condition)
                    Passed++;
                Lines.Add(string.Format("[{0}] {1}", condition ? "PASS" : "FAIL", description));
            }
        }
    }
}
