using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace vibrance.GUI.common
{
    /// <summary>
    /// Every outcome ParseSetVibranceLevel below can hand back, syntax only - see that method's
    /// own comment for why the vendor range check (IsVibranceLevelInRange) is a separate pure
    /// function rather than folded in here.
    /// </summary>
    internal enum SetVibranceParseStatus
    {
        /// <summary>--set-vibrance was not present in argv at all - not an error, just "nothing to do".</summary>
        NotRequested,

        /// <summary>--set-vibrance was present with nothing after it, or was the very last token.</summary>
        MissingValue,

        /// <summary>The token right after --set-vibrance did not parse as a base-10 integer.</summary>
        NotANumber,

        /// <summary>
        /// --set-vibrance was present with a syntactically valid integer - Level carries it, still
        /// unchecked against any vendor's real range. See IsVibranceLevelInRange.
        /// </summary>
        Recognized
    }

    /// <summary>
    /// ParseSetVibranceLevel's return value. A struct, not a tuple - no tuples in this codebase
    /// (C# 6 / .NET Framework 4.0), the same reason ApplicationListItemAppearance in VibranceGUI.cs
    /// is one.
    /// </summary>
    internal struct SetVibranceParseResult
    {
        internal SetVibranceParseStatus Status;
        internal int Level;
    }

    /// <summary>
    /// vibranceGUI's command line surface (upstream issue #120) - what argv means, decided as pure
    /// data with no MessageBox, no Mutex, no window handle, so Program.cs's real dispatch and
    /// VibranceGUI's WndProc relay receiver can both stay thin wrappers around exactly what
    /// CliOptionsFixture pins, instead of each re-deriving its own copy of the same decision.
    /// </summary>
    internal static class CliOptions
    {
        internal const string SetVibranceFlag = "--set-vibrance";

        /// <summary>
        /// --help, -h and /? - the three spellings a Windows console user reaches for first.
        /// Checked as a whole-token match, the same way every existing flag in Program.cs already
        /// is (see "-minimized", "--force-nvidia", "--selftest-gpu" and friends) - never a prefix
        /// match, so a future flag that happens to start with one of these three can never
        /// collide with it.
        /// </summary>
        internal static bool IsHelpRequested(string[] args)
        {
            return args != null && (args.Contains("--help") || args.Contains("-h") || args.Contains("/?"));
        }

        /// <summary>
        /// Syntax only: is --set-vibrance present, does a token follow it, and does that token
        /// parse as a base-10 integer? Deliberately knows nothing about NVIDIA's 0-63 or AMD's
        /// 0-300 - whoever calls this either already has the real vendor's bounds in scope
        /// (Program.cs, right where it is about to construct a VibranceGUI for that exact vendor)
        /// or never resolves a vendor at all, because an instance is already running and owns that
        /// decision (a second, short-lived process relayed through VibranceCliRelay instead).
        /// IsVibranceLevelInRange below is the second half, called only once a real min/max is
        /// known. The first --set-vibrance token wins if it appears more than once - argv is
        /// walked left to right via Array.IndexOf, which returns the first match.
        /// </summary>
        internal static SetVibranceParseResult ParseSetVibranceLevel(string[] args)
        {
            SetVibranceParseResult result = new SetVibranceParseResult();

            if (args == null)
            {
                result.Status = SetVibranceParseStatus.NotRequested;
                return result;
            }

            int flagIndex = Array.IndexOf(args, SetVibranceFlag);
            if (flagIndex < 0)
            {
                result.Status = SetVibranceParseStatus.NotRequested;
                return result;
            }

            if (flagIndex + 1 >= args.Length)
            {
                result.Status = SetVibranceParseStatus.MissingValue;
                return result;
            }

            int level;
            if (!int.TryParse(args[flagIndex + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out level))
            {
                result.Status = SetVibranceParseStatus.NotANumber;
                return result;
            }

            result.Status = SetVibranceParseStatus.Recognized;
            result.Level = level;
            return result;
        }

        /// <summary>
        /// The vendor half ParseSetVibranceLevel above deliberately leaves out. Inclusive at both
        /// ends: 0 is a real, legal level on both vendors (fully desaturated), not a sentinel for
        /// "unset" the way HdrVibranceHelper.HdrLevelUnset is - so this must never reject it.
        /// </summary>
        internal static bool IsVibranceLevelInRange(int level, int minLevel, int maxLevel)
        {
            return level >= minLevel && level <= maxLevel;
        }

        /// <summary>
        /// The --help/-h//? MessageBox text. Mentions --selftest-* as a family rather than
        /// enumerating all nine of them - those are a build/regression tool, not something a user
        /// turning vibrance on/off from a batch file (upstream #120's actual ask) needs spelled out
        /// flag by flag. Returned as lines, the same shape every --selftest-* fixture's own Run()
        /// already returns, joined with Environment.NewLine by whichever caller shows it - keeps
        /// this file free of any System.Windows.Forms/System.Environment dependency.
        /// </summary>
        internal static List<string> BuildHelpLines()
        {
            List<string> lines = new List<string>();
            lines.Add("vibranceGUI command line options:");
            lines.Add(string.Empty);
            lines.Add("  --help, -h, /?        Show this help and exit.");
            lines.Add("  -minimized            Start hidden in the system tray - no window shown.");
            lines.Add("  --set-vibrance <n>    Set the Windows-level vibrance (the level used while no");
            lines.Add("                        configured game is in the foreground) and continue.");
            lines.Add("                        If vibranceGUI is already running, hands the request to");
            lines.Add("                        that instance instead of opening a second one.");
            lines.Add("                        Valid range: 0-63 on NVIDIA, 0-300 on AMD.");
            lines.Add("  --force-nvidia        Force NVIDIA GPU detection.");
            lines.Add("  --force-amd           Force AMD GPU detection.");
            lines.Add(string.Empty);
            lines.Add("  --selftest-*          Internal regression checks (e.g. --selftest-gpu). Not");
            lines.Add("                        meant for everyday use.");
            return lines;
        }
    }
}
