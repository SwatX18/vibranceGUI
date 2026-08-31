using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace vibrance.GUI.common
{
    /// <summary>
    /// The reference expectations for vibranceGUI's command line surface (upstream issue #120), as
    /// literal data. Run by vibrance.GUI.exe --selftest-cli.
    ///
    /// CliOptions.cs is pure - no MessageBox, no Mutex, no window handle - so every check below
    /// calls it directly, the same way MatchingFixture calls ApplicationSettingMatcher rather than
    /// re-deriving its own copy of the matching rules; a hand-copied mirror of the parsing logic
    /// here could drift from what Program.cs actually runs and this fixture would never notice.
    ///
    /// VibranceCliRelay's production entry points (FindOtherRunningInstanceProcessId, TryRelay)
    /// are deliberately never used below - only FindTopLevelWindowForProcess/TryRelayTo, against
    /// this fixture's OWN process id and a message id it registers fresh for itself every run (see
    /// CheckRelayTransport). Driving the production path here would mean either spawning a second
    /// real vibranceGUI process (risking a genuine collision with an actually-running instance on
    /// a shared machine - exactly the "no real display's vibrance may change" hazard
    /// VibranceRestoreFixture's own header comment already refuses to risk) or faking
    /// Process.GetProcessesByName, which would be the same "fake that simulates Windows messaging"
    /// mistake extended to process enumeration. What CANNOT be covered this way, and is left as an
    /// honest gap like ApplyToggleHotkey's real RegisterHotKey call: VibranceGUI's own WndProc
    /// actually comparing an incoming Message.Msg against SetVibranceLevelMessageId and
    /// dispatching to OnSetVibranceLevelRequested - that needs a real Form with a real vendor
    /// proxy, which no fixture in this codebase constructs (see ResolveListItemAppearances' own
    /// comment on why).
    ///
    /// TestReceiverWindow below is raw RegisterClassEx/CreateWindowEx P/Invoke rather than a
    /// WinForms Form/Control/NativeWindow - an earlier version of this fixture used WinForms
    /// directly and measured NativeWindow.WindowClass.RegisterClass failing with a Win32
    /// ERROR_INVALID_HANDLE registering a CUSTOM window class in at least one real build/test
    /// environment here (reproduced across Form, Control and bare NativeWindow); this raw P/Invoke
    /// path does not hit it. Still real Windows messaging end to end - RegisterClassEx,
    /// CreateWindowEx and SendMessage are exactly what WinForms itself calls under the hood, not a
    /// fake that simulates any of it. The class name TestReceiverWindow registers for ITSELF is
    /// arbitrary now (VibranceCliRelay no longer searches by class name at all, see its own header
    /// comment for why), kept unique only so two fixture runs on the same desktop can never
    /// collide with each other.
    /// </summary>
    public static class CliOptionsFixture
    {
        public static List<string> Run()
        {
            Checklist checklist = new Checklist();
            checklist.Lines.Add("vibranceGUI command line self test");
            checklist.Lines.Add(string.Empty);

            CheckHelpRecognition(checklist);
            CheckSetVibranceParsing(checklist);
            CheckVendorRangeValidation(checklist);
            CheckHelpText(checklist);
            CheckRelayTransport(checklist);

            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add(string.Format("PASSED {0}/{1}", checklist.Passed, checklist.Total));
            return checklist.Lines;
        }

        private static void CheckHelpRecognition(Checklist checklist)
        {
            checklist.Lines.Add("IsHelpRequested - every spelling, and nothing else:");

            checklist.Check(CliOptions.IsHelpRequested(new[] { "--help" }), "--help alone");
            checklist.Check(CliOptions.IsHelpRequested(new[] { "-h" }), "-h alone");
            checklist.Check(CliOptions.IsHelpRequested(new[] { "/?" }), "/? alone");
            checklist.Check(CliOptions.IsHelpRequested(new[] { "-minimized", "--help" }),
                "--help still recognised alongside another flag");
            checklist.Check(!CliOptions.IsHelpRequested(new string[0]), "an empty argv");
            checklist.Check(!CliOptions.IsHelpRequested(null), "a null argv");
            checklist.Check(!CliOptions.IsHelpRequested(new[] { "-minimized" }), "an unrelated flag");
            checklist.Check(!CliOptions.IsHelpRequested(new[] { "--helper" }),
                "a longer flag that merely starts with --help is not a match");
            checklist.Check(!CliOptions.IsHelpRequested(new[] { "-H" }),
                "wrong case does not match - exact tokens only, like every other flag here");
        }

        private static void CheckSetVibranceParsing(Checklist checklist)
        {
            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add("ParseSetVibranceLevel - syntax only, no vendor range yet:");

            checklist.Check(Parse(new string[0]).Status == SetVibranceParseStatus.NotRequested,
                "an empty argv is NotRequested, not an error");
            checklist.Check(Parse(null).Status == SetVibranceParseStatus.NotRequested,
                "a null argv is NotRequested too");
            checklist.Check(Parse(new[] { "-minimized" }).Status == SetVibranceParseStatus.NotRequested,
                "an unrelated flag is NotRequested");
            checklist.Check(
                Parse(new[] { "--set-vibrance-typo", "5" }).Status == SetVibranceParseStatus.NotRequested,
                "an unknown flag that only resembles --set-vibrance does not match it");

            // CheckNoThrow, not Check: this is the one case that reads args[flagIndex + 1] right at
            // the edge of the array ParseSetVibranceLevel's own bounds guard protects - a weakened
            // guard here throws IndexOutOfRangeException instead of returning a status, which would
            // otherwise abort Run() and silently take every check after this one down with it. See
            // Checklist.CheckNoThrow's own comment.
            checklist.CheckNoThrow(delegate { return Parse(new[] { "--set-vibrance" }).Status == SetVibranceParseStatus.MissingValue; },
                "--set-vibrance as the very last token");
            checklist.Check(
                Parse(new[] { "--set-vibrance", "-minimized" }).Status == SetVibranceParseStatus.NotANumber,
                "the next token being another flag is a value that fails to parse, not a missing one");

            checklist.Check(Parse(new[] { "--set-vibrance", "abc" }).Status == SetVibranceParseStatus.NotANumber,
                "a non-numeric value");
            checklist.Check(Parse(new[] { "--set-vibrance", "12.5" }).Status == SetVibranceParseStatus.NotANumber,
                "a decimal value - the level is a whole number on both vendors");
            checklist.Check(Parse(new[] { "--set-vibrance", "" }).Status == SetVibranceParseStatus.NotANumber,
                "an empty string value");
            checklist.Check(Parse(new[] { "--set-vibrance", "1,000" }).Status == SetVibranceParseStatus.NotANumber,
                "a thousands separator is rejected, not silently stripped");

            SetVibranceParseResult recognized = Parse(new[] { "--set-vibrance", "50" });
            checklist.Check(recognized.Status == SetVibranceParseStatus.Recognized && recognized.Level == 50,
                "a well formed value parses to exactly that level");

            SetVibranceParseResult negative = Parse(new[] { "--set-vibrance", "-5" });
            checklist.Check(negative.Status == SetVibranceParseStatus.Recognized && negative.Level == -5,
                "a negative value is syntactically Recognized - IsVibranceLevelInRange rejects it, not this");

            SetVibranceParseResult first = Parse(new[] { "--set-vibrance", "10", "--set-vibrance", "20" });
            checklist.Check(first.Status == SetVibranceParseStatus.Recognized && first.Level == 10,
                "the first --set-vibrance wins when it appears more than once");
        }

        private static void CheckVendorRangeValidation(Checklist checklist)
        {
            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add("IsVibranceLevelInRange - each vendor's real bounds, boundaries included:");

            checklist.Check(CliOptions.IsVibranceLevelInRange(0, 0, 63), "NVIDIA's minimum, 0, is in range");
            checklist.Check(CliOptions.IsVibranceLevelInRange(63, 0, 63), "NVIDIA's maximum, 63, is in range");
            checklist.Check(!CliOptions.IsVibranceLevelInRange(-1, 0, 63), "one below NVIDIA's minimum is not");
            checklist.Check(!CliOptions.IsVibranceLevelInRange(64, 0, 63), "one above NVIDIA's maximum is not");

            checklist.Check(CliOptions.IsVibranceLevelInRange(0, 0, 300), "AMD's minimum, 0, is in range");
            checklist.Check(CliOptions.IsVibranceLevelInRange(300, 0, 300), "AMD's maximum, 300, is in range");
            checklist.Check(!CliOptions.IsVibranceLevelInRange(-1, 0, 300), "one below AMD's minimum is not");
            checklist.Check(!CliOptions.IsVibranceLevelInRange(301, 0, 300), "one above AMD's maximum is not");

            checklist.Check(CliOptions.IsVibranceLevelInRange(150, 0, 300), "a mid-range AMD value");
            checklist.Check(CliOptions.IsVibranceLevelInRange(32, 0, 63), "a mid-range NVIDIA value");
        }

        private static void CheckHelpText(Checklist checklist)
        {
            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add("BuildHelpLines - every real flag gets a mention:");

            string joined = string.Join(" ", CliOptions.BuildHelpLines().ToArray());
            checklist.Check(joined.Contains("--help"), "mentions --help");
            checklist.Check(joined.Contains("-minimized"), "mentions -minimized");
            checklist.Check(joined.Contains(CliOptions.SetVibranceFlag), "mentions --set-vibrance");
            checklist.Check(joined.Contains("--force-nvidia"), "mentions --force-nvidia");
            checklist.Check(joined.Contains("--force-amd"), "mentions --force-amd");
            checklist.Check(joined.Contains("--selftest"), "mentions the --selftest-* family");
            checklist.Check(joined.Contains("0-63") && joined.Contains("0-300"),
                "states both vendors' real ranges, not just one");
        }

        /// <summary>
        /// VibranceCliRelay.FindTopLevelWindowForProcess/TryRelayTo, driven against a real Win32
        /// window this fixture creates and destroys for itself, and this process's own real id -
        /// real Windows messaging, not a fake that simulates it. See this class's own header
        /// comment for why the production process-lookup/relay wrapper is never used here.
        /// </summary>
        private static void CheckRelayTransport(Checklist checklist)
        {
            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add("VibranceCliRelay.FindTopLevelWindowForProcess/TryRelayTo, against a disposable");
            checklist.Lines.Add("test window in this process - real Windows messaging, not a fake:");

            string suffix = Guid.NewGuid().ToString("N");
            int testMessageId = (int)RegisterWindowMessage("vibranceGUI-selftest-cli-message-" + suffix);
            int currentProcessId = Process.GetCurrentProcess().Id;

            checklist.Check(testMessageId != 0, "RegisterWindowMessage returns a nonzero id for a fresh name");

            // 0 is the System Idle Process - documented in PathResolver.TryGetProcessImagePath as
            // never owning a window, and never a real vibranceGUI process id, so this is a
            // deterministic "nothing to find" case regardless of what other windows this test host
            // process happens to already own.
            checklist.Check(VibranceCliRelay.FindTopLevelWindowForProcess(0) == IntPtr.Zero,
                "FindTopLevelWindowForProcess finds nothing for the idle process, which owns no window");
            checklist.Check(!VibranceCliRelay.TryRelayTo(0, testMessageId, 42),
                "TryRelayTo returns false when the target process owns no window");

            TestReceiverWindow receiver = new TestReceiverWindow("vibranceGUI-selftest-cli-" + suffix, testMessageId);
            IntPtr createdHandle = receiver.Handle;
            try
            {
                IntPtr found = VibranceCliRelay.FindTopLevelWindowForProcess(currentProcessId);
                checklist.Check(found == createdHandle,
                    "FindTopLevelWindowForProcess finds this process's own freshly created window");

                bool relayed = VibranceCliRelay.TryRelayTo(currentProcessId, testMessageId, 42);
                checklist.Check(relayed, "TryRelayTo finds the real window by process id and returns true");
                checklist.Check(receiver.LastReceivedLevel == 42, "the level arrives intact as the message's wParam");

                bool relayedZero = VibranceCliRelay.TryRelayTo(currentProcessId, testMessageId, 0);
                checklist.Check(relayedZero && receiver.LastReceivedLevel == 0,
                    "level 0 - a real vendor level, not a sentinel - survives the round trip too");

                checklist.Check(!VibranceCliRelay.TryRelayTo(currentProcessId, 0, 99),
                    "TryRelayTo refuses to relay when messageId is RegisterWindowMessage's own failure sentinel");
            }
            finally
            {
                receiver.Destroy();
            }

            // Compared against createdHandle, captured before Destroy() reset receiver.Handle to
            // IntPtr.Zero - not against receiver.Handle itself, which would trivially equal
            // whatever FindTopLevelWindowForProcess returns for a process with no window left
            // (also IntPtr.Zero), passing this check even if DestroyWindow silently failed to
            // remove the real window. Not asserted as IntPtr.Zero outright: this test host process
            // may own other windows besides the one just destroyed (a console, an IME window), and
            // FindTopLevelWindowForProcess would legitimately find one of those instead. What
            // matters here is only that the destroyed window's own handle is never the answer.
            checklist.Check(VibranceCliRelay.FindTopLevelWindowForProcess(currentProcessId) != createdHandle,
                "and the destroyed window's handle is no longer found for this process");
        }

        private static SetVibranceParseResult Parse(string[] args)
        {
            return CliOptions.ParseSetVibranceLevel(args);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string lpString);

        /// <summary>
        /// The fixture's own throwaway receiver, real but disposable and never sharing an
        /// identifier with the shipping app - see this file's own header comment for why this is
        /// raw RegisterClassEx/CreateWindowEx P/Invoke rather than a WinForms Form/Control with a
        /// CreateParams.ClassName override. WndProcDelegate is kept in a field, not a local: a
        /// delegate passed to native code via Marshal.GetFunctionPointerForDelegate must stay
        /// rooted for as long as native code can still call it, i.e. until DestroyWindow below has
        /// actually run - the GC has no way to know user32.dll is still holding that function
        /// pointer.
        /// </summary>
        private sealed class TestReceiverWindow
        {
            private const uint WsPopup = 0x80000000;

            private readonly string _className;
            private readonly int _messageId;
            private readonly IntPtr _hInstance;
            private readonly WndProcDelegate _wndProc;
            internal int? LastReceivedLevel;
            internal IntPtr Handle { get; private set; }

            internal TestReceiverWindow(string className, int messageId)
            {
                _className = className;
                _messageId = messageId;
                _hInstance = GetModuleHandle(null);
                _wndProc = WndProc;

                WNDCLASSEX wc = new WNDCLASSEX();
                wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX));
                wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc);
                wc.hInstance = _hInstance;
                wc.lpszClassName = _className;
                if (RegisterClassEx(ref wc) == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                // WS_POPUP, no parent and no owner - a real, ownerless top-level window, the same
                // shape FindWindow expects VibranceGUI's own window to be. Never shown: WS_VISIBLE
                // is not set, and FindWindow does not require it.
                Handle = CreateWindowEx(0, _className, "vibranceGUI self test receiver", WsPopup,
                    0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);
                if (Handle == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    UnregisterClass(_className, _hInstance);
                    throw new Win32Exception(error);
                }
            }

            private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
            {
                if (_messageId != 0 && msg == (uint)_messageId)
                {
                    LastReceivedLevel = (int)wParam;
                }
                return DefWindowProc(hWnd, msg, wParam, lParam);
            }

            internal void Destroy()
            {
                if (Handle != IntPtr.Zero)
                {
                    DestroyWindow(Handle);
                    Handle = IntPtr.Zero;
                }
                UnregisterClass(_className, _hInstance);
            }

            private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct WNDCLASSEX
            {
                internal uint cbSize;
                internal uint style;
                internal IntPtr lpfnWndProc;
                internal int cbClsExtra;
                internal int cbWndExtra;
                internal IntPtr hInstance;
                internal IntPtr hIcon;
                internal IntPtr hCursor;
                internal IntPtr hbrBackground;
                internal string lpszMenuName;
                internal string lpszClassName;
                internal IntPtr hIconSm;
            }

            [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

            [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
                uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
                IntPtr hInstance, IntPtr lpParam);

            [DllImport("user32.dll")]
            private static extern bool DestroyWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            private static extern IntPtr GetModuleHandle(string lpModuleName);
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

            /// <summary>
            /// The same contract as Check above, for a condition that reads right up against a
            /// bounds check in the code under test rather than one comfortably inside it - a
            /// regression that weakens that guard should show up here as one [FAIL] line, not as
            /// an unhandled exception that aborts Run() and silently takes every check after this
            /// one down with it, which is a strictly worse diagnostic for whoever hits it next.
            /// </summary>
            public void CheckNoThrow(Func<bool> condition, string description)
            {
                bool result;
                try
                {
                    result = condition();
                }
                catch (Exception ex)
                {
                    result = false;
                    description = string.Format("{0} (threw {1}: {2})", description, ex.GetType().Name, ex.Message);
                }
                Check(result, description);
            }
        }
    }
}
