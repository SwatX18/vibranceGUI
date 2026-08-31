using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Windows.Forms;
using vibrance.GUI.AMD;
using vibrance.GUI.AMD.vendor;
using vibrance.GUI.NVIDIA;

namespace vibrance.GUI.common
{
    /// <summary>
    /// Regression coverage for the toggle hotkey feature (upstream #143, per-game suppression):
    /// HotkeyBindingParser's parse/format round trip, HotkeyRegistration's release-then-register
    /// lifecycle (R3 in particular - pins the release-then-register ordering across a handle
    /// change, see HotkeyRegistration.Release's own comment for why the handle is cached rather
    /// than read fresh), ProfileToggleHelper.Decide's pure decision, the per-game suppression gate both
    /// vendor proxies' OnWinEventHook now open with, ToggleForegroundProfile's actual write, and
    /// the settings round trip. No GUI, no live GPU driver, and - unlike every check here that
    /// reflects into a real OnWinEventHook/ToggleForegroundProfile - this fixture NEVER calls the
    /// real RegisterHotKey and must never grow a hardware variant: IHotkeyRegistrar is driven
    /// exclusively through FakeHotkeyRegistrar below, exactly as VibranceRestoreFixture never
    /// touches a real GPU and ResolutionChangeFixture never touches a real display. Run by
    /// vibrance.GUI.exe --selftest-profiletoggle.
    /// </summary>
    public static class ProfileToggleFixture
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        public static List<string> Run()
        {
            Checklist checklist = new Checklist();
            checklist.Lines.Add("vibranceGUI toggle hotkey self test");
            checklist.Lines.Add(string.Empty);

            RunParseFormatChecks(checklist);
            RunRegistrationChecks(checklist);
            RunDecideChecks(checklist);
            RunToggleEffectChecks(checklist);
            RunSuppressionGateChecks(checklist);
            RunSuppressionCleanupChecks(checklist);
            RunListItemMarkerChecks(checklist);
            RunSettingsChecks(checklist);
            RunLoggingChecks(checklist);

            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add(string.Format("PASSED {0}/{1}", checklist.Passed, checklist.Total));
            return checklist.Lines;
        }

        // ------------------------------------------------------------------
        // HotkeyBindingParser - pure, no registrar, no proxy. Unchanged from the discarded global-
        // pause design; kept verbatim per the architect's instruction that this half survives.
        // ------------------------------------------------------------------

        private static void RunParseFormatChecks(Checklist checklist)
        {
            checklist.Lines.Add("HotkeyBindingParser.TryParse/Format (pure):");

            CheckParseBasic(checklist);
            CheckRoundTrip(checklist);
            CheckInvalidInputsRejected(checklist);
            CheckUnrecognisedTokenRejected(checklist);
            CheckNumericTokenRejected(checklist);
            CheckFormatNone(checklist);
            CheckCaseInsensitiveParseNormalisedFormat(checklist);
            CheckNoRepeatNeverFormatted(checklist);

            checklist.Lines.Add(string.Empty);
        }

        // H1. Mutation this guards: drop "| ModNoRepeat" from TryParse.
        private static void CheckParseBasic(Checklist checklist)
        {
            HotkeyBinding binding;
            bool ok = HotkeyBindingParser.TryParse("Ctrl+Alt+F9", out binding);
            const uint expectedModifiers = 0x0002 /* MOD_CONTROL */ | 0x0001 /* MOD_ALT */ | 0x4000 /* MOD_NOREPEAT */;
            const uint expectedVirtualKey = 0x78; // VK_F9

            checklist.Check(ok && binding.IsSet && binding.Modifiers == expectedModifiers && binding.VirtualKey == expectedVirtualKey,
                string.Format("H1: \"Ctrl+Alt+F9\" parses to Modifiers=0x{0:X} (MOD_CONTROL|MOD_ALT|MOD_NOREPEAT), VirtualKey=0x{1:X} (VK_F9), got ok={2} Modifiers=0x{3:X} VirtualKey=0x{4:X}",
                    expectedModifiers, expectedVirtualKey, ok, binding.Modifiers, binding.VirtualKey));
        }

        // H2. Mutation this guards: emit the modifiers in TryParse's own read order instead of a
        // fixed Ctrl/Alt/Shift/Win order in Format.
        private static readonly string[] CanonicalStrings =
        {
            "F9",
            "Ctrl+F9",
            "Alt+F5",
            "Shift+F12",
            "Win+D1",
            "Ctrl+Alt+Shift+Win+F9"
        };

        private static void CheckRoundTrip(Checklist checklist)
        {
            bool allRoundTrip = true;
            string firstMismatch = null;
            foreach (string canonical in CanonicalStrings)
            {
                HotkeyBinding binding;
                bool parsed = HotkeyBindingParser.TryParse(canonical, out binding);
                string formatted = HotkeyBindingParser.Format(binding);
                if (!parsed || formatted != canonical)
                {
                    allRoundTrip = false;
                    firstMismatch = string.Format("\"{0}\" -> parsed={1}, formatted=\"{2}\"", canonical, parsed, formatted);
                    break;
                }
            }

            checklist.Check(allRoundTrip, allRoundTrip
                ? "H2: Format(TryParse(s)) == s over all 6 canonical strings"
                : "H2: Format(TryParse(s)) == s over all 6 canonical strings - first mismatch: " + firstMismatch);
        }

        // H3. Mutation this guards: drop the "no key at all" guard, letting "Ctrl" parse as a
        // legal binding.
        private static void CheckInvalidInputsRejected(Checklist checklist)
        {
            HotkeyBinding binding;
            bool emptyOk = HotkeyBindingParser.TryParse("", out binding);
            bool nullOk = HotkeyBindingParser.TryParse(null, out binding);
            bool ctrlOnlyOk = HotkeyBindingParser.TryParse("Ctrl", out binding);
            bool trailingSeparatorOk = HotkeyBindingParser.TryParse("Ctrl+", out binding);

            checklist.Check(!emptyOk && !nullOk && !ctrlOnlyOk && !trailingSeparatorOk,
                string.Format("H3: \"\", null, \"Ctrl\", \"Ctrl+\" all fail to parse, got empty={0} null={1} ctrlOnly={2} trailingSeparator={3}",
                    emptyOk, nullOk, ctrlOnlyOk, trailingSeparatorOk));
        }

        // H4. Mutation this guards: silently drop an unrecognised token instead of failing.
        private static void CheckUnrecognisedTokenRejected(Checklist checklist)
        {
            HotkeyBinding binding;
            bool ok = HotkeyBindingParser.TryParse("Ctrl+Zzz+F9", out binding);

            checklist.Check(!ok, string.Format(
                "H4: \"Ctrl+Zzz+F9\" fails to parse - an unrecognised token is an error, never silently dropped, got {0}", ok));
        }

        // H5. Mutation this guards: drop the numeric-token guard, letting Enum.TryParse<Keys>
        // turn "1" into Keys.LButton.
        private static void CheckNumericTokenRejected(Checklist checklist)
        {
            HotkeyBinding numericBinding;
            bool numericOk = HotkeyBindingParser.TryParse("Ctrl+1", out numericBinding);

            HotkeyBinding namedDigitBinding;
            bool namedDigitOk = HotkeyBindingParser.TryParse("Ctrl+D1", out namedDigitBinding);

            checklist.Check(!numericOk,
                string.Format("H5a: \"Ctrl+1\" fails to parse - a purely numeric token is never a valid key name on its own, got {0}", numericOk));
            checklist.Check(namedDigitOk && namedDigitBinding.VirtualKey == 0x31,
                string.Format("H5b: \"Ctrl+D1\" parses with VirtualKey=0x31 (VK_1), got ok={0} VirtualKey=0x{1:X}", namedDigitOk, namedDigitBinding.VirtualKey));
        }

        // H6. Mutation this guards: return a non-empty placeholder for an unset binding instead
        // of "".
        private static void CheckFormatNone(Checklist checklist)
        {
            string formatted = HotkeyBindingParser.Format(HotkeyBinding.None);
            checklist.Check(formatted == string.Empty, string.Format("H6: Format(HotkeyBinding.None) == \"\", got \"{0}\"", formatted));
        }

        // H7. Mutation this guards: compare tokens with StringComparison.Ordinal (case-sensitive)
        // instead of OrdinalIgnoreCase.
        private static void CheckCaseInsensitiveParseNormalisedFormat(Checklist checklist)
        {
            HotkeyBinding binding;
            bool ok = HotkeyBindingParser.TryParse("ctrl+alt+f9", out binding);
            string formatted = HotkeyBindingParser.Format(binding);

            checklist.Check(ok && formatted == "Ctrl+Alt+F9",
                string.Format("H7: \"ctrl+alt+f9\" parses and formats back as the normalised \"Ctrl+Alt+F9\", got ok={0} formatted=\"{1}\"", ok, formatted));
        }

        // H8. Mutation this guards: add MOD_NOREPEAT to the list of modifier parts Format emits.
        private static void CheckNoRepeatNeverFormatted(Checklist checklist)
        {
            HotkeyBinding binding;
            HotkeyBindingParser.TryParse("Ctrl+F9", out binding);
            bool noRepeatSetInternally = (binding.Modifiers & HotkeyBindingParser.ModNoRepeat) != 0;
            string formatted = HotkeyBindingParser.Format(binding);

            checklist.Check(noRepeatSetInternally && formatted == "Ctrl+F9",
                string.Format("H8: MOD_NOREPEAT is set internally (Modifiers=0x{0:X}) but the formatted text is exactly \"Ctrl+F9\", got \"{1}\"",
                    binding.Modifiers, formatted));
        }

        // ------------------------------------------------------------------
        // HotkeyRegistration - against FakeHotkeyRegistrar, never RegisterHotKey. R1-R8 unchanged
        // from the discarded design; R9/R10 are new, covering the checkbox-gated registration
        // expression VibranceGUI.ApplyToggleHotkey now uses.
        // ------------------------------------------------------------------

        private static void RunRegistrationChecks(Checklist checklist)
        {
            checklist.Lines.Add("HotkeyRegistration.Apply/Release (via FakeHotkeyRegistrar, never the real RegisterHotKey):");

            CheckApplyNoneMakesNoRegisterCall(checklist);
            CheckApplyValidBindingRegistersOnce(checklist);
            CheckApplyOnNewHandleReleasesThePriorOne(checklist);
            CheckApplyPropagatesAlreadyOwned(checklist);
            CheckReleaseAfterFailedApplyMakesNoUnregisterCall(checklist);
            CheckReleaseOnFreshInstanceMakesNoCalls(checklist);
            CheckApplyNoneAfterSuccessReleasesWithoutRegistering(checklist);
            CheckModNoRepeatCompatibilityRetry(checklist);
            CheckCheckboxOffSuppressesRegistrationOfAValidBinding(checklist);
            CheckCheckboxOnWithNoBindingMakesNoCall(checklist);
            CheckShouldReleaseHotkeyOnFocusTransition(checklist);

            checklist.Lines.Add(string.Empty);
        }

        private static HotkeyBinding ParseOrThrow(string canonicalText)
        {
            HotkeyBinding binding;
            if (!HotkeyBindingParser.TryParse(canonicalText, out binding))
            {
                throw new InvalidOperationException("Fixture setup error: \"" + canonicalText + "\" failed to parse");
            }
            return binding;
        }

        // R1. Mutation this guards: call _registrar.Register before checking binding.IsSet.
        private static void CheckApplyNoneMakesNoRegisterCall(Checklist checklist)
        {
            FakeHotkeyRegistrar registrar = new FakeHotkeyRegistrar();
            HotkeyRegistration registration = new HotkeyRegistration(registrar);

            HotkeyRegistrationResult result = registration.Apply((IntPtr)1, HotkeyBinding.None);

            checklist.Check(result == HotkeyRegistrationResult.NotConfigured && registrar.RegisterCalls.Count == 0,
                string.Format("R1: Apply(h, None) returns NotConfigured with zero Register calls, got result={0} registerCalls={1}",
                    result, registrar.RegisterCalls.Count));
        }

        // R2 (PIN - not regression evidence, the ordinary successful path).
        private static void CheckApplyValidBindingRegistersOnce(Checklist checklist)
        {
            FakeHotkeyRegistrar registrar = new FakeHotkeyRegistrar();
            HotkeyRegistration registration = new HotkeyRegistration(registrar);
            HotkeyBinding binding = ParseOrThrow("Ctrl+Alt+F9");

            HotkeyRegistrationResult result = registration.Apply((IntPtr)1, binding);

            checklist.Check(result == HotkeyRegistrationResult.Registered && registrar.RegisterCalls.Count == 1 && registration.IsRegistered,
                string.Format("R2 (pin): Apply(h, validBinding) makes exactly one Register call and returns Registered, got result={0} registerCalls={1} isRegistered={2}",
                    result, registrar.RegisterCalls.Count, registration.IsRegistered));
        }

        // R3, the highest-value check in this file. Mutation this guards: have Release() (called
        // from inside Apply) read a fresh "hWnd" parameter instead of the handle cached at
        // registration time. Pins the release-then-register ordering across a handle change -
        // see HotkeyRegistration.Release's own comment for why caching it is the defensive
        // choice regardless of whether anything in this codebase currently recreates the handle.
        private static void CheckApplyOnNewHandleReleasesThePriorOne(Checklist checklist)
        {
            FakeHotkeyRegistrar registrar = new FakeHotkeyRegistrar();
            HotkeyRegistration registration = new HotkeyRegistration(registrar);
            IntPtr h1 = (IntPtr)111;
            IntPtr h2 = (IntPtr)222;
            HotkeyBinding a = ParseOrThrow("Ctrl+F9");
            HotkeyBinding b = ParseOrThrow("Alt+F10");

            registration.Apply(h1, a);
            registration.Apply(h2, b);

            bool sequenceMatches = registrar.RegisterCalls.Count == 2 &&
                registrar.RegisterCalls[0].HWnd == h1 &&
                registrar.RegisterCalls[1].HWnd == h2 &&
                registrar.UnregisterCalls.Count == 1 &&
                registrar.UnregisterCalls[0] == h1;

            checklist.Check(sequenceMatches, string.Format(
                "R3: Apply(h1,a) then Apply(h2,b) records Register(h1), Unregister(h1), Register(h2) in that order - the release-then-register ordering across a handle change - got {0} Register call(s) on [{1}], {2} Unregister call(s) on [{3}]",
                registrar.RegisterCalls.Count, DescribeHandles(registrar.RegisterCalls),
                registrar.UnregisterCalls.Count, DescribeHandles(registrar.UnregisterCalls)));
        }

        private static string DescribeHandles(List<FakeHotkeyRegistrar.RegisterCall> calls)
        {
            List<string> parts = new List<string>();
            foreach (FakeHotkeyRegistrar.RegisterCall call in calls)
            {
                parts.Add(call.HWnd.ToString());
            }
            return string.Join(",", parts.ToArray());
        }

        private static string DescribeHandles(List<IntPtr> handles)
        {
            List<string> parts = new List<string>();
            foreach (IntPtr handle in handles)
            {
                parts.Add(handle.ToString());
            }
            return string.Join(",", parts.ToArray());
        }

        // R4. Mutation this guards: treat AlreadyOwnedByAnotherApplication the same as Registered.
        private static void CheckApplyPropagatesAlreadyOwned(Checklist checklist)
        {
            FakeHotkeyRegistrar registrar = new FakeHotkeyRegistrar();
            registrar.QueueResult(HotkeyRegistrationResult.AlreadyOwnedByAnotherApplication);
            HotkeyRegistration registration = new HotkeyRegistration(registrar);
            HotkeyBinding binding = ParseOrThrow("Ctrl+F9");

            HotkeyRegistrationResult result = registration.Apply((IntPtr)1, binding);

            checklist.Check(result == HotkeyRegistrationResult.AlreadyOwnedByAnotherApplication && !registration.IsRegistered,
                string.Format("R4: AlreadyOwnedByAnotherApplication propagates and IsRegistered stays false, got result={0} isRegistered={1}",
                    result, registration.IsRegistered));
        }

        // R5. Mutation this guards: drop Release's "if (!_isRegistered) return;" guard.
        private static void CheckReleaseAfterFailedApplyMakesNoUnregisterCall(Checklist checklist)
        {
            FakeHotkeyRegistrar registrar = new FakeHotkeyRegistrar();
            registrar.QueueResult(HotkeyRegistrationResult.AlreadyOwnedByAnotherApplication);
            HotkeyRegistration registration = new HotkeyRegistration(registrar);
            registration.Apply((IntPtr)1, ParseOrThrow("Ctrl+F9"));

            registration.Release();

            checklist.Check(registrar.UnregisterCalls.Count == 0,
                string.Format("R5: Release() after a failed Apply makes zero Unregister calls, got {0}", registrar.UnregisterCalls.Count));
        }

        // R6. Same guard as R5, exercised on a completely untouched instance.
        private static void CheckReleaseOnFreshInstanceMakesNoCalls(Checklist checklist)
        {
            FakeHotkeyRegistrar registrar = new FakeHotkeyRegistrar();
            HotkeyRegistration registration = new HotkeyRegistration(registrar);

            registration.Release();

            checklist.Check(registrar.RegisterCalls.Count == 0 && registrar.UnregisterCalls.Count == 0,
                string.Format("R6: Release() on a fresh instance makes zero calls, got register={0} unregister={1}",
                    registrar.RegisterCalls.Count, registrar.UnregisterCalls.Count));
        }

        // R7. Mutation this guards: still call Register with a zeroed binding when applying None.
        private static void CheckApplyNoneAfterSuccessReleasesWithoutRegistering(Checklist checklist)
        {
            FakeHotkeyRegistrar registrar = new FakeHotkeyRegistrar();
            HotkeyRegistration registration = new HotkeyRegistration(registrar);
            IntPtr h = (IntPtr)1;
            registration.Apply(h, ParseOrThrow("Ctrl+F9"));
            int registerCallsBefore = registrar.RegisterCalls.Count;

            HotkeyRegistrationResult result = registration.Apply(h, HotkeyBinding.None);

            checklist.Check(result == HotkeyRegistrationResult.NotConfigured &&
                registrar.UnregisterCalls.Count == 1 &&
                registrar.RegisterCalls.Count == registerCallsBefore,
                string.Format("R7: Apply(h, None) after a success makes one Unregister call and zero new Register calls, got result={0} unregisterCalls={1} newRegisterCalls={2}",
                    result, registrar.UnregisterCalls.Count, registrar.RegisterCalls.Count - registerCallsBefore));
        }

        // R8. Mutation this guards: drop the MOD_NOREPEAT compatibility retry entirely.
        private static void CheckModNoRepeatCompatibilityRetry(Checklist checklist)
        {
            FakeHotkeyRegistrar registrar = new FakeHotkeyRegistrar();
            registrar.QueueResult(HotkeyRegistrationResult.Failed);
            HotkeyRegistration registration = new HotkeyRegistration(registrar);
            HotkeyBinding binding = ParseOrThrow("Ctrl+F9");

            HotkeyRegistrationResult result = registration.Apply((IntPtr)1, binding);

            bool secondCallDroppedNoRepeat = registrar.RegisterCalls.Count == 2 &&
                (registrar.RegisterCalls[0].Modifiers & HotkeyBindingParser.ModNoRepeat) != 0 &&
                (registrar.RegisterCalls[1].Modifiers & HotkeyBindingParser.ModNoRepeat) == 0;

            checklist.Check(result == HotkeyRegistrationResult.Registered && secondCallDroppedNoRepeat,
                string.Format("R8: a Failed first Register call (MOD_NOREPEAT set) retries exactly once more without MOD_NOREPEAT and succeeds, got result={0} registerCalls={1}",
                    result, registrar.RegisterCalls.Count));
        }

        // R9. Mutation this guards: gate registration on binding.IsSet alone, ignoring "enabled" -
        // the checkbox-off state VibranceGUI.ApplyToggleHotkey must respect. Drives the REAL
        // production gate, HotkeyRegistration.EffectiveBinding - not a fixture-local copy of it;
        // VibranceGUI.ApplyToggleHotkey calls this exact static method, so a mutation here is
        // visible to the actual code path, not just to a mirror of it.
        private static void CheckCheckboxOffSuppressesRegistrationOfAValidBinding(Checklist checklist)
        {
            FakeHotkeyRegistrar registrar = new FakeHotkeyRegistrar();
            HotkeyRegistration registration = new HotkeyRegistration(registrar);
            HotkeyBinding validBinding = ParseOrThrow("Ctrl+Alt+F9");

            HotkeyBinding effective = HotkeyRegistration.EffectiveBinding(false, validBinding);
            HotkeyRegistrationResult result = registration.Apply((IntPtr)1, effective);

            checklist.Check(result == HotkeyRegistrationResult.NotConfigured && registrar.RegisterCalls.Count == 0,
                string.Format("R9: checkbox off + a valid saved combination makes zero Register calls, got result={0} registerCalls={1}",
                    result, registrar.RegisterCalls.Count));
        }

        // R10. Mutation this guards: throw (e.g. dereferencing a field on HotkeyBinding.None
        // incorrectly) instead of cleanly returning NotConfigured when enabled but unbound. Same
        // real-gate note as R9 above.
        private static void CheckCheckboxOnWithNoBindingMakesNoCall(Checklist checklist)
        {
            FakeHotkeyRegistrar registrar = new FakeHotkeyRegistrar();
            HotkeyRegistration registration = new HotkeyRegistration(registrar);

            bool threw = false;
            HotkeyRegistrationResult result = HotkeyRegistrationResult.Failed;
            try
            {
                HotkeyBinding effective = HotkeyRegistration.EffectiveBinding(true, HotkeyBinding.None);
                result = registration.Apply((IntPtr)1, effective);
            }
            catch (Exception)
            {
                threw = true;
            }

            checklist.Check(!threw && result == HotkeyRegistrationResult.NotConfigured && registrar.RegisterCalls.Count == 0,
                string.Format("R10: checkbox on + no combination makes zero calls and never throws, got threw={0} result={1} registerCalls={2}",
                    threw, result, registrar.RegisterCalls.Count));
        }

        // B1. Drives VibranceGUI.ShouldReleaseHotkeyOnFocusTransition directly (same assembly,
        // internal - no reflection needed) rather than a real Form: VibranceGUI's own constructor
        // calls getProxy(...), touching a real vendor proxy, and FormatterServices.
        // GetUninitializedObject is not safe for a Form-derived type (WinForms/Control internal
        // state that only the real constructor sets up). This is the condition
        // OnDeactivate/OnActivated both call before touching the hotkey registration - a
        // real Form correctly wiring WM_ACTIVATE to those two overrides is a WinForms framework
        // fact, not something this harness can verify headlessly, but the DECISION they both make
        // is real production code and is what this pins.
        //
        // Mutation this guards: invert the comparison (or hardcode true/false) so OnDeactivate/
        // OnActivated stop distinguishing "the capture box has focus" from "anything else does".
        private static void CheckShouldReleaseHotkeyOnFocusTransition(Checklist checklist)
        {
            TextBox captureBox = new TextBox();
            TextBox otherControl = new TextBox();

            bool trueForTheCaptureBoxItself = VibranceGUI.ShouldReleaseHotkeyOnFocusTransition(captureBox, captureBox);
            bool falseForADifferentControl = !VibranceGUI.ShouldReleaseHotkeyOnFocusTransition(otherControl, captureBox);
            bool falseForNoActiveControlAtAll = !VibranceGUI.ShouldReleaseHotkeyOnFocusTransition(null, captureBox);

            checklist.Check(trueForTheCaptureBoxItself && falseForADifferentControl && falseForNoActiveControlAtAll,
                string.Format("B1: ShouldReleaseHotkeyOnFocusTransition is true only when the capture box itself is the active control - got captureBox={0}, otherControl={1}, null={2}",
                    trueForTheCaptureBoxItself, !falseForADifferentControl, !falseForNoActiveControlAtAll));
        }

        // ------------------------------------------------------------------
        // ProfileToggleHelper.Decide - pure, no device, no Screen, no OS call.
        // ------------------------------------------------------------------

        private static void RunDecideChecks(Checklist checklist)
        {
            checklist.Lines.Add("ProfileToggleHelper.Decide (pure):");

            CheckDecideMatchesByInstallDirectory(checklist);
            CheckDecideNoMatch(checklist);
            CheckDecideEmptyAndNullLists(checklist);
            CheckDecideEngineNotReady(checklist);
            CheckDecideDirectionBothWays(checklist);
            CheckDecideMutatesNothing(checklist);
            CheckDecideSuppressionIsCaseInsensitive(checklist);
            CheckDecideNameBeatsLongerDirectoryMatch(checklist);

            checklist.Lines.Add(string.Empty);
        }

        // D1. Mutation this guards: match on setting.Name alone, ignoring processImagePath - PR
        // #153's bug. A profile the game finder only ever confirmed by install directory (a
        // guessed executable, or a launcher/anti-cheat shim in the foreground instead of the game
        // itself) must be just as reachable by the hotkey as by the automatic apply path.
        private static void CheckDecideMatchesByInstallDirectory(Checklist checklist)
        {
            ApplicationSetting setting = new ApplicationSetting();
            setting.Name = "RealGameExeName";
            setting.InstallDirectory = "C:\\Games\\SomeGame";
            setting.IngameLevel = 40;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { setting };

            ProfileToggleDecision decision = ProfileToggleHelper.Decide(
                settings, "launcher", "C:\\Games\\SomeGame\\bin\\launcher.exe", true);

            checklist.Check(decision.Action == ProfileToggleAction.RestoreWindowsLevel && decision.Setting == setting,
                string.Format("D1: a process whose name does not match but whose image path sits under the setting's InstallDirectory is still found, got Action={0}",
                    decision.Action));
        }

        // D2. Mutation this guards: return something other than None (e.g. throw, or default to
        // the first setting) when nothing matches at all.
        private static void CheckDecideNoMatch(Checklist checklist)
        {
            ApplicationSetting setting = new ApplicationSetting();
            setting.Name = "SomeGame";
            List<ApplicationSetting> settings = new List<ApplicationSetting> { setting };

            ProfileToggleDecision decision = ProfileToggleHelper.Decide(settings, "UnrelatedProcess", null, true);

            checklist.Check(decision.Action == ProfileToggleAction.None,
                string.Format("D2: no match at all -> None, got {0}", decision.Action));
        }

        // D3. Mutation this guards: dereference the list without a null check.
        private static void CheckDecideEmptyAndNullLists(Checklist checklist)
        {
            bool threw = false;
            ProfileToggleAction emptyAction = ProfileToggleAction.None;
            ProfileToggleAction nullAction = ProfileToggleAction.None;
            try
            {
                emptyAction = ProfileToggleHelper.Decide(new List<ApplicationSetting>(), "Anything", null, true).Action;
                nullAction = ProfileToggleHelper.Decide(null, "Anything", null, true).Action;
            }
            catch (Exception)
            {
                threw = true;
            }

            checklist.Check(!threw && emptyAction == ProfileToggleAction.None && nullAction == ProfileToggleAction.None,
                string.Format("D3: an empty list and a null list both yield None with no throw, got threw={0} emptyAction={1} nullAction={2}",
                    threw, emptyAction, nullAction));
        }

        // D4. Mutation this guards: skip the isWindowsLevelKnown check and fall through to a
        // real Action even when the Windows level is still the struct default 0 - reopens
        // issue #60/#36 through the toggle's own door.
        private static void CheckDecideEngineNotReady(Checklist checklist)
        {
            ApplicationSetting setting = new ApplicationSetting();
            setting.Name = "SomeGame";
            List<ApplicationSetting> settings = new List<ApplicationSetting> { setting };

            ProfileToggleDecision decision = ProfileToggleHelper.Decide(settings, "SomeGame", null, false);

            checklist.Check(decision.Action == ProfileToggleAction.EngineNotReady,
                string.Format("D4: a clean Name match with isWindowsLevelKnown false -> EngineNotReady, not a write action, got {0}", decision.Action));
        }

        // D5. Mutation this guards: invert (or hard-code) the direction rule.
        private static void CheckDecideDirectionBothWays(Checklist checklist)
        {
            ProfileToggleHelper.ResetForTests();
            ApplicationSetting setting = new ApplicationSetting();
            setting.Name = "SomeGameD5";
            List<ApplicationSetting> settings = new List<ApplicationSetting> { setting };

            ProfileToggleDecision notSuppressed = ProfileToggleHelper.Decide(settings, "SomeGameD5", null, true);
            ProfileToggleHelper.SetSuppressed("SomeGameD5", true);
            ProfileToggleDecision suppressed = ProfileToggleHelper.Decide(settings, "SomeGameD5", null, true);

            checklist.Check(notSuppressed.Action == ProfileToggleAction.RestoreWindowsLevel && suppressed.Action == ProfileToggleAction.ApplyGameLevel,
                string.Format("D5: not suppressed -> RestoreWindowsLevel, suppressed -> ApplyGameLevel, got notSuppressed={0} suppressed={1}",
                    notSuppressed.Action, suppressed.Action));

            ProfileToggleHelper.ResetForTests();
        }

        // D6. Mutation this guards: have Decide itself call SetSuppressed (or otherwise mutate
        // state) instead of leaving the flip to the caller, after a confirmed write.
        private static void CheckDecideMutatesNothing(Checklist checklist)
        {
            ProfileToggleHelper.ResetForTests();
            ApplicationSetting setting = new ApplicationSetting();
            setting.Name = "SomeGameD6";
            List<ApplicationSetting> settings = new List<ApplicationSetting> { setting };

            int countBefore = ProfileToggleHelper.SuppressedCount;
            ProfileToggleAction first = ProfileToggleHelper.Decide(settings, "SomeGameD6", null, true).Action;
            ProfileToggleAction second = ProfileToggleHelper.Decide(settings, "SomeGameD6", null, true).Action;
            ProfileToggleAction third = ProfileToggleHelper.Decide(settings, "SomeGameD6", null, true).Action;
            int countAfter = ProfileToggleHelper.SuppressedCount;

            checklist.Check(countBefore == countAfter && first == second && second == third,
                string.Format("D6: three identical Decide calls leave SuppressedCount unchanged ({0} -> {1}) and return the same Action every time ({2},{3},{4})",
                    countBefore, countAfter, first, second, third));
        }

        // D7. Mutation this guards: compare suppressed names with StringComparison.Ordinal.
        private static void CheckDecideSuppressionIsCaseInsensitive(Checklist checklist)
        {
            ProfileToggleHelper.ResetForTests();
            ApplicationSetting setting = new ApplicationSetting();
            setting.Name = "SomeGameD7";
            List<ApplicationSetting> settings = new List<ApplicationSetting> { setting };

            ProfileToggleHelper.SetSuppressed("somegamed7", true);
            ProfileToggleDecision decision = ProfileToggleHelper.Decide(settings, "SomeGameD7", null, true);

            checklist.Check(decision.Action == ProfileToggleAction.ApplyGameLevel,
                string.Format("D7: suppressing \"somegamed7\" (different case) still suppresses the setting named \"SomeGameD7\", got {0}", decision.Action));

            ProfileToggleHelper.ResetForTests();
        }

        // D8 (PIN). A Name match beats a longer directory match belonging to a DIFFERENT setting -
        // proves Decide's pass-through of ApplicationSettingMatcher.FindMatch's own two-pass rule,
        // not a Decide-specific behaviour.
        private static void CheckDecideNameBeatsLongerDirectoryMatch(Checklist checklist)
        {
            ApplicationSetting nameMatch = new ApplicationSetting();
            nameMatch.Name = "TargetProcess";
            nameMatch.InstallDirectory = null;

            ApplicationSetting directoryMatch = new ApplicationSetting();
            directoryMatch.Name = "SomeOtherName";
            directoryMatch.InstallDirectory = "C:\\Games\\SomeGame\\A\\Much\\Longer\\Nested\\Directory";

            List<ApplicationSetting> settings = new List<ApplicationSetting> { directoryMatch, nameMatch };

            ProfileToggleDecision decision = ProfileToggleHelper.Decide(
                settings, "TargetProcess", "C:\\Games\\SomeGame\\A\\Much\\Longer\\Nested\\Directory\\TargetProcess.exe", true);

            checklist.Check(decision.Setting == nameMatch,
                "D8 (pin): an exact Name match wins over a longer InstallDirectory match belonging to a different setting");
        }

        // ------------------------------------------------------------------
        // ToggleForegroundProfile's actual write - both vendors, via ResetForTests/fake
        // adapters/GetUninitializedObject. NVIDIA's ToggleForegroundProfile is an instance method
        // purely because IVibranceProxy requires one; it (like OnWinEventHook) touches only this
        // class's own static state, so GetUninitializedObject hands back a target to invoke it
        // against without ever running the real constructor (which would call vibranceDLL.dll's
        // initializeLibrary()). AMD's own state is per-instance, so its checks construct a real
        // proxy around a fake IAmdAdapter instead (IAmdAdapter.IsAvailable() returning false keeps
        // the constructor from installing a real, process-lifetime SetWinEventHook).
        // ------------------------------------------------------------------

        private static void RunToggleEffectChecks(Checklist checklist)
        {
            checklist.Lines.Add("ToggleForegroundProfile's write (both vendors, via fakes):");

            CheckNvidiaToggleWritesOnlyTheGamesOwnDisplay(checklist);
            CheckAmdToggleNeverWidensToAllDisplays(checklist);
            CheckNvidiaToggleNoMatchMakesNoCallsOrStateChange(checklist);
            CheckAmdToggleNoMatchMakesNoCallsOrStateChange(checklist);
            CheckNvidiaToggleOnWriteFailureLeavesStateUnchanged(checklist);
            CheckAmdToggleOnWriteFailureLeavesStateUnchanged(checklist);
            CheckNvidiaToggleOffWriteFailureLeavesStateUnchanged(checklist);
            CheckAmdToggleOffWriteFailureLeavesStateUnchanged(checklist);
            CheckNvidiaToggleOffThenOnRoundTrip(checklist);
            CheckNvidiaToggleOffOfNeverAppliedGameStillSucceeds(checklist);
            CheckAmdToggleOffRespectsWideMode(checklist);
            CheckAmdToggleOnRespectsWideMode(checklist);

            checklist.Lines.Add(string.Empty);
        }

        private static object NewNvidiaInstance()
        {
            return FormatterServices.GetUninitializedObject(typeof(NvidiaDynamicVibranceProxy));
        }

        private static ProfileToggleResult InvokeNvidiaToggle(IntPtr hWnd, string processName, string processImagePath)
        {
            MethodInfo m = typeof(NvidiaDynamicVibranceProxy).GetMethod("ToggleForegroundProfile");
            return (ProfileToggleResult)m.Invoke(NewNvidiaInstance(), new object[] { hWnd, processName, processImagePath });
        }

        // T1, the check that matters most here. The work-list is seeded with BOTH dGame and
        // dOther as GENUINELY held (VibranceRestoreHelper.RecordGameLevelApplied for both) -
        // seeding dOther as a real work-list entry, not an unused decoy, is what makes the
        // widening mutation below actually produce a second write; a decoy never on the work-list
        // would leave that wrong implementation passing by accident.
        //
        // Mutation this guards: route the toggle's restore write through
        // RestoreWindowsVibranceLevel instead of RestoreOneDisplay - the former walks the WHOLE
        // work-list plus the primary, so it would write dOther too.
        private static void CheckNvidiaToggleWritesOnlyTheGamesOwnDisplay(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestGameT1";
            matchingSetting.IngameLevel = 50;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };

            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.isWindowsLevelKnown = true;
            vibranceInfo.userVibranceSettingDefault = 30;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);

            IntPtr desktop = GetDesktopWindow();
            string dGame = Screen.FromHandle(desktop).DeviceName;
            const string dOther = "\\\\.\\DISPLAY_TESTONLY_T1_OTHER";
            VibranceRestoreHelper.RecordGameLevelApplied(dGame);
            VibranceRestoreHelper.RecordGameLevelApplied(dOther);

            ProfileToggleResult result = InvokeNvidiaToggle(desktop, "TestGameT1", null);

            bool witness1 = device.SetLevelCalls.Count == 1 && device.SetLevelCalls[0] == device.HandleFor(dGame);
            bool witness2 = device.ResolvedDeviceNames.Count == 1 && device.ResolvedDeviceNames[0] == dGame;
            bool witness3 = VibranceRestoreHelper.HoldingCount == 1;

            checklist.Check(result == ProfileToggleResult.ToggledOff && witness1 && witness2 && witness3,
                string.Format("T1: toggling off writes ONLY the game's own display (1 SetLevel call on dGame, 1 resolved device, dOther left on the work-list -> HoldingCount 1), got result={0} SetLevelCalls={1} ResolvedDeviceNames={2} HoldingCount={3}",
                    result, device.SetLevelCalls.Count, device.ResolvedDeviceNames.Count, VibranceRestoreHelper.HoldingCount));
        }

        // T2, AMD's counterpart. Mutation this guards: route the toggle's restore write through
        // SetSaturationOnAllDisplays (the "likeliest wrong implementation") instead of
        // SetSaturationOnDisplay(vibranceLevel, deviceName).
        private static void CheckAmdToggleNeverWidensToAllDisplays(Checklist checklist)
        {
            FakeAmdAdapter adapter = new FakeAmdAdapter();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestGameT2";
            matchingSetting.IngameLevel = 200;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };
            AmdDynamicVibranceProxy proxy = BuildAmdProxy(adapter, settings);
            proxy.SetVibranceWindowsLevel(90);
            // Narrow mode - B2 (off)/(on) below cover the wide-mode branch this proxy also has to
            // respect; this check is specifically about the narrow, single-display overload.
            proxy.SetAffectPrimaryMonitorOnly(true);

            IntPtr desktop = GetDesktopWindow();
            string dGame = Screen.FromHandle(desktop).DeviceName;

            ProfileToggleResult result = proxy.ToggleForegroundProfile(desktop, "TestGameT2", null);

            checklist.Check(result == ProfileToggleResult.ToggledOff &&
                adapter.SetSaturationOnAllDisplaysCallCount == 0 &&
                adapter.SetSaturationOnDisplayNames.Count == 1 && adapter.SetSaturationOnDisplayNames[0] == dGame,
                string.Format("T2: toggling off an AMD profile never calls SetSaturationOnAllDisplays (got {0} call(s)) and writes SetSaturationOnDisplay exactly once, to the game's own display, got result={1} perDisplayCalls={2}",
                    adapter.SetSaturationOnAllDisplaysCallCount, result, adapter.SetSaturationOnDisplayNames.Count));
        }

        // T3n. Mutation this guards: fall through to some write even when Decide returned None.
        private static void CheckNvidiaToggleNoMatchMakesNoCallsOrStateChange(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.isWindowsLevelKnown = true;
            vibranceInfo.userVibranceSettingDefault = 30;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, new List<ApplicationSetting>());

            ProfileToggleResult result = InvokeNvidiaToggle(GetDesktopWindow(), "SomeOtherProcessT3", null);

            checklist.Check(result == ProfileToggleResult.NoConfiguredGameInForeground && device.TotalCallCount == 0 && ProfileToggleHelper.SuppressedCount == 0,
                string.Format("T3n: no configured game in the foreground makes zero device calls and zero suppression-state change, got result={0} calls={1} suppressedCount={2}",
                    result, device.TotalCallCount, ProfileToggleHelper.SuppressedCount));
        }

        // T3a, AMD's counterpart.
        private static void CheckAmdToggleNoMatchMakesNoCallsOrStateChange(Checklist checklist)
        {
            ProfileToggleHelper.ResetForTests();
            FakeAmdAdapter adapter = new FakeAmdAdapter();
            AmdDynamicVibranceProxy proxy = BuildAmdProxy(adapter, new List<ApplicationSetting>());
            proxy.SetVibranceWindowsLevel(90);

            ProfileToggleResult result = proxy.ToggleForegroundProfile(GetDesktopWindow(), "SomeOtherProcessT3", null);

            checklist.Check(result == ProfileToggleResult.NoConfiguredGameInForeground &&
                adapter.SetSaturationOnAllDisplaysCallCount == 0 && adapter.SetSaturationOnDisplayNames.Count == 0 &&
                ProfileToggleHelper.SuppressedCount == 0,
                string.Format("T3a: no configured game in the foreground makes zero adapter calls and zero suppression-state change, got result={0} allCalls={1} perDisplayCalls={2} suppressedCount={3}",
                    result, adapter.SetSaturationOnAllDisplaysCallCount, adapter.SetSaturationOnDisplayNames.Count, ProfileToggleHelper.SuppressedCount));
        }

        // T4n (ON-path failure). The game is already suppressed; toggling it back on fails to
        // write - must report WriteFailed and leave it suppressed. Mutation this guards: flip
        // suppression before checking the write's own return value.
        private static void CheckNvidiaToggleOnWriteFailureLeavesStateUnchanged(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestGameT4";
            matchingSetting.IngameLevel = 50;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };
            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.isWindowsLevelKnown = true;
            vibranceInfo.userVibranceSettingDefault = 30;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);
            ProfileToggleHelper.SetSuppressed("TestGameT4", true);

            IntPtr desktop = GetDesktopWindow();
            string dGame = Screen.FromHandle(desktop).DeviceName;
            device.FailNextSetLevel(dGame);

            ProfileToggleResult result = InvokeNvidiaToggle(desktop, "TestGameT4", null);

            checklist.Check(result == ProfileToggleResult.WriteFailed && ProfileToggleHelper.IsSuppressed("TestGameT4"),
                string.Format("T4n: a failed game-level write on the ON path returns WriteFailed and leaves the profile suppressed, got result={0} isSuppressed={1}",
                    result, ProfileToggleHelper.IsSuppressed("TestGameT4")));

            ProfileToggleHelper.ResetForTests();
        }

        // T4a, AMD's counterpart.
        private static void CheckAmdToggleOnWriteFailureLeavesStateUnchanged(Checklist checklist)
        {
            ProfileToggleHelper.ResetForTests();
            FakeAmdAdapter adapter = new FakeAmdAdapter();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestGameT4a";
            matchingSetting.IngameLevel = 210;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };
            AmdDynamicVibranceProxy proxy = BuildAmdProxy(adapter, settings);
            proxy.SetVibranceWindowsLevel(90);
            // Narrow mode - B2 (on) already covers the wide-mode write; this check is about the
            // write-failure/state-preservation contract, not which overload gets called, so it is
            // pinned to the mode whose failure injection targets a specific display name.
            proxy.SetAffectPrimaryMonitorOnly(true);
            ProfileToggleHelper.SetSuppressed("TestGameT4a", true);

            IntPtr desktop = GetDesktopWindow();
            string dGame = Screen.FromHandle(desktop).DeviceName;
            adapter.FailNextSetSaturationOnDisplay(dGame);

            ProfileToggleResult result = proxy.ToggleForegroundProfile(desktop, "TestGameT4a", null);

            checklist.Check(result == ProfileToggleResult.WriteFailed && ProfileToggleHelper.IsSuppressed("TestGameT4a"),
                string.Format("T4a: a failed game-level write on the ON path returns WriteFailed and leaves the profile suppressed, got result={0} isSuppressed={1}",
                    result, ProfileToggleHelper.IsSuppressed("TestGameT4a")));

            ProfileToggleHelper.ResetForTests();
        }

        // T5n (OFF-path failure). The game is running normally; toggling it off fails to write -
        // must report WriteFailed and leave it un-suppressed.
        private static void CheckNvidiaToggleOffWriteFailureLeavesStateUnchanged(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestGameT5";
            matchingSetting.IngameLevel = 50;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };
            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.isWindowsLevelKnown = true;
            vibranceInfo.userVibranceSettingDefault = 30;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);

            IntPtr desktop = GetDesktopWindow();
            string dGame = Screen.FromHandle(desktop).DeviceName;
            device.FailNextSetLevel(dGame);

            ProfileToggleResult result = InvokeNvidiaToggle(desktop, "TestGameT5", null);

            checklist.Check(result == ProfileToggleResult.WriteFailed && !ProfileToggleHelper.IsSuppressed("TestGameT5"),
                string.Format("T5n: a failed Windows-level write on the OFF path returns WriteFailed and leaves the profile NOT suppressed, got result={0} isSuppressed={1}",
                    result, ProfileToggleHelper.IsSuppressed("TestGameT5")));
        }

        // T5a, AMD's counterpart.
        private static void CheckAmdToggleOffWriteFailureLeavesStateUnchanged(Checklist checklist)
        {
            ProfileToggleHelper.ResetForTests();
            FakeAmdAdapter adapter = new FakeAmdAdapter();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestGameT5a";
            matchingSetting.IngameLevel = 210;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };
            AmdDynamicVibranceProxy proxy = BuildAmdProxy(adapter, settings);
            proxy.SetVibranceWindowsLevel(90);
            // Narrow mode - see the matching comment in CheckAmdToggleOnWriteFailureLeavesStateUnchanged.
            proxy.SetAffectPrimaryMonitorOnly(true);

            IntPtr desktop = GetDesktopWindow();
            string dGame = Screen.FromHandle(desktop).DeviceName;
            adapter.FailNextSetSaturationOnDisplay(dGame);

            ProfileToggleResult result = proxy.ToggleForegroundProfile(desktop, "TestGameT5a", null);

            checklist.Check(result == ProfileToggleResult.WriteFailed && !ProfileToggleHelper.IsSuppressed("TestGameT5a"),
                string.Format("T5a: a failed Windows-level write on the OFF path returns WriteFailed and leaves the profile NOT suppressed, got result={0} isSuppressed={1}",
                    result, ProfileToggleHelper.IsSuppressed("TestGameT5a")));

            ProfileToggleHelper.ResetForTests();
        }

        // T6. A full off-then-on round trip: two writes, two different levels, ends un-suppressed.
        private static void CheckNvidiaToggleOffThenOnRoundTrip(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestGameT6";
            matchingSetting.IngameLevel = 55;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };
            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.isWindowsLevelKnown = true;
            vibranceInfo.userVibranceSettingDefault = 20;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);

            IntPtr desktop = GetDesktopWindow();

            ProfileToggleResult offResult = InvokeNvidiaToggle(desktop, "TestGameT6", null);
            ProfileToggleResult onResult = InvokeNvidiaToggle(desktop, "TestGameT6", null);

            checklist.Check(offResult == ProfileToggleResult.ToggledOff && onResult == ProfileToggleResult.ToggledOn && !ProfileToggleHelper.IsSuppressed("TestGameT6"),
                string.Format("T6: off-then-on returns ToggledOff then ToggledOn and ends un-suppressed, got off={0} on={1} isSuppressed={2}",
                    offResult, onResult, ProfileToggleHelper.IsSuppressed("TestGameT6")));

            bool sawBothLevels = device.SetLevelCalls.Count == 2;
            checklist.Check(sawBothLevels,
                string.Format("T6: exactly two SetLevel calls landed across the round trip (one per direction), got {0}", device.SetLevelCalls.Count));
        }

        // T8. A game that was suppressed WITHOUT ever having its game level actually applied
        // (SetLevel never called for it before) still toggles off successfully via the read-back
        // alone, when the display already happens to be at the Windows level.
        private static void CheckNvidiaToggleOffOfNeverAppliedGameStillSucceeds(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestGameT8";
            matchingSetting.IngameLevel = 50;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };
            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.isWindowsLevelKnown = true;
            vibranceInfo.userVibranceSettingDefault = 30;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);

            IntPtr desktop = GetDesktopWindow();
            string dGame = Screen.FromHandle(desktop).DeviceName;
            device.SeedLevel(dGame, 30); // already at the Windows level - as if never actually applied to.

            ProfileToggleResult result = InvokeNvidiaToggle(desktop, "TestGameT8", null);

            checklist.Check(result == ProfileToggleResult.ToggledOff && device.SetLevelCalls.Count == 0 && ProfileToggleHelper.IsSuppressed("TestGameT8"),
                string.Format("T8: toggling off a display already at the Windows level makes zero SetLevel calls (confirmed by read-back alone) but still reports ToggledOff and suppresses, got result={0} setLevelCalls={1} isSuppressed={2}",
                    result, device.SetLevelCalls.Count, ProfileToggleHelper.IsSuppressed("TestGameT8")));
        }

        // B2 (off). AMD's apply is NOT single-display with affectPrimaryMonitorOnly false (the
        // DEFAULT) - unlike NVIDIA, it writes every attached screen (OnWinEventHook's own apply
        // branch above). Mutation this guards: ignore the flag in the OFF direction and always
        // write/clear only the one named display. Witnessed two ways: the adapter call itself
        // must be the WIDE overload (SetSaturationOnDisplay(level, null), never
        // SetSaturationOnAllDisplays - so the bool return still gets checked), and a work-list
        // entry for a display OTHER than the resolved one must still be cleared - the narrow
        // ClearGameLevelRecord(deviceName) would leave it behind, only the wide
        // ClearAllGameLevelRecords() drains it.
        private static void CheckAmdToggleOffRespectsWideMode(Checklist checklist)
        {
            ProfileToggleHelper.ResetForTests();
            VibranceRestoreHelper.ResetForTests();
            FakeAmdAdapter adapter = new FakeAmdAdapter();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestGameB2Off";
            matchingSetting.IngameLevel = 220;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };
            AmdDynamicVibranceProxy proxy = BuildAmdProxy(adapter, settings);
            // affectPrimaryMonitorOnly left at the struct default (false) - AMD's actual default,
            // and the wide mode this check means to exercise.
            proxy.SetVibranceWindowsLevel(90);

            const string otherDevice = "\\\\.\\DISPLAY_TESTONLY_B2OFF_OTHER";
            VibranceRestoreHelper.RecordGameLevelApplied(otherDevice);

            IntPtr desktop = GetDesktopWindow();
            ProfileToggleResult result = proxy.ToggleForegroundProfile(desktop, "TestGameB2Off", null);

            bool usedWideOverload = adapter.SetSaturationOnAllDisplaysCallCount == 0 &&
                adapter.SetSaturationOnDisplayNames.Count == 1 && adapter.SetSaturationOnDisplayNames[0] == null &&
                adapter.SetSaturationOnDisplayLevels[0] == 90;

            checklist.Check(result == ProfileToggleResult.ToggledOff && usedWideOverload && VibranceRestoreHelper.HoldingCount == 0,
                string.Format("B2 (off): with affectPrimaryMonitorOnly false, toggling off calls SetSaturationOnDisplay(90, null) - never SetSaturationOnAllDisplays - and drains the WHOLE work-list (a display never named in the call), got result={0} allDisplaysCalls={1} perDisplayCalls={2} lastName={3} HoldingCount={4}",
                    result, adapter.SetSaturationOnAllDisplaysCallCount, adapter.SetSaturationOnDisplayNames.Count,
                    adapter.SetSaturationOnDisplayNames.Count > 0 ? (adapter.SetSaturationOnDisplayNames[0] ?? "null") : "(none)",
                    VibranceRestoreHelper.HoldingCount));

            ProfileToggleHelper.ResetForTests();
            VibranceRestoreHelper.ResetForTests();
        }

        // B2 (on). The mirror-image defect: toggling a suppressed game back ON with the flag
        // false must write (and record as owing a restore) every attached display, exactly like
        // the automatic apply branch does - not just the screen the toggle happened to resolve
        // foregroundWindow to. Witnessed via the real Screen.AllScreens count, so this passes
        // regardless of how many monitors are actually attached to the machine running it.
        private static void CheckAmdToggleOnRespectsWideMode(Checklist checklist)
        {
            ProfileToggleHelper.ResetForTests();
            VibranceRestoreHelper.ResetForTests();
            FakeAmdAdapter adapter = new FakeAmdAdapter();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestGameB2On";
            matchingSetting.IngameLevel = 230;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };
            AmdDynamicVibranceProxy proxy = BuildAmdProxy(adapter, settings);
            proxy.SetVibranceWindowsLevel(90);
            ProfileToggleHelper.SetSuppressed("TestGameB2On", true);

            IntPtr desktop = GetDesktopWindow();
            ProfileToggleResult result = proxy.ToggleForegroundProfile(desktop, "TestGameB2On", null);

            bool usedWideOverload = adapter.SetSaturationOnAllDisplaysCallCount == 0 &&
                adapter.SetSaturationOnDisplayNames.Count == 1 && adapter.SetSaturationOnDisplayNames[0] == null &&
                adapter.SetSaturationOnDisplayLevels[0] == 230;
            int expectedHoldingCount = Screen.AllScreens.Length;

            checklist.Check(result == ProfileToggleResult.ToggledOn && usedWideOverload && VibranceRestoreHelper.HoldingCount == expectedHoldingCount,
                string.Format("B2 (on): with affectPrimaryMonitorOnly false, toggling on calls SetSaturationOnDisplay(230, null) - never SetSaturationOnAllDisplays - and records EVERY attached screen as owing a restore ({0} on this machine), got result={1} allDisplaysCalls={2} perDisplayCalls={3} lastName={4} HoldingCount={5}",
                    expectedHoldingCount, result, adapter.SetSaturationOnAllDisplaysCallCount, adapter.SetSaturationOnDisplayNames.Count,
                    adapter.SetSaturationOnDisplayNames.Count > 0 ? (adapter.SetSaturationOnDisplayNames[0] ?? "null") : "(none)",
                    VibranceRestoreHelper.HoldingCount));

            ProfileToggleHelper.ResetForTests();
            VibranceRestoreHelper.ResetForTests();
        }

        private static AmdDynamicVibranceProxy BuildAmdProxy(FakeAmdAdapter adapter, List<ApplicationSetting> settings)
        {
            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> windowsResolutionSettings =
                new Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>>();
            AmdDynamicVibranceProxy proxy = new AmdDynamicVibranceProxy(adapter, settings, windowsResolutionSettings);
            proxy.SetNeverChangeColorSettings(true);
            proxy.SetNeverSwitchResolution(true);
            return proxy;
        }

        // ------------------------------------------------------------------
        // Suppression gate - the real, private OnWinEventHook by reflection, both vendors.
        // ------------------------------------------------------------------

        private static void RunSuppressionGateChecks(Checklist checklist)
        {
            checklist.Lines.Add("Per-game suppression gate at the top of the apply branch (real OnWinEventHook via reflection, both vendors):");

            CheckNvidiaSuppressedGameMakesNoCalls(checklist);
            CheckNvidiaGameScreenUnchangedWhenSuppressed(checklist);
            CheckNvidiaDifferentUnsuppressedGameStillApplies(checklist);
            CheckNvidiaRestoreBranchStillRunsWhileSomethingIsSuppressed(checklist);
            CheckNvidiaNotSuppressedAppliesNormally(checklist);
            CheckAmdSuppressedGameMakesNoCalls(checklist);

            checklist.Lines.Add(string.Empty);
        }

        // ------------------------------------------------------------------
        // Stranded-suppression cleanup - VibranceGUI.ClearSuppressionIfNameChanged, the real
        // production method both the "remove program" and "Change executable..." call sites now
        // route through (same assembly, internal - no reflection needed). Repro this closes: a
        // game A gets suppressed, its profile is later removed (or edited so its Name changes),
        // and a brand new, UNRELATED profile that happens to resolve to the same Name would
        // otherwise silently start suppressed with no action from that user.
        // ------------------------------------------------------------------

        private static void RunSuppressionCleanupChecks(Checklist checklist)
        {
            checklist.Lines.Add("Stranded-suppression cleanup (VibranceGUI.ClearSuppressionIfNameChanged, real method, no reflection):");

            CheckClearSuppressionOnRemoval(checklist);
            CheckClearSuppressionOnRename(checklist);
            CheckClearSuppressionLeavesADifferentSuppressionAlone(checklist);

            checklist.Lines.Add(string.Empty);
        }

        // Mutation this guards: drop the removal call site's use of ClearSuppressionIfNameChanged
        // entirely (newName: null models "this profile no longer exists at all").
        private static void CheckClearSuppressionOnRemoval(Checklist checklist)
        {
            ProfileToggleHelper.ResetForTests();

            ApplicationSetting removedSetting = new ApplicationSetting();
            removedSetting.Name = "TestGameCleanupRemoved";
            ProfileToggleHelper.SetSuppressed("TestGameCleanupRemoved", true);

            VibranceGUI.ClearSuppressionIfNameChanged(removedSetting, null);

            checklist.Check(!ProfileToggleHelper.IsSuppressed("TestGameCleanupRemoved"),
                "Cleanup-1: removing a suppressed profile (newName: null) clears its suppression - a later, unrelated profile that resolves to the same Name must not silently inherit it");

            ProfileToggleHelper.ResetForTests();
        }

        // Mutation this guards: compare FileName instead of Name (or skip the comparison and
        // never clear at all) when deciding whether "Change executable..." actually moved this
        // profile off its old suppression key.
        private static void CheckClearSuppressionOnRename(Checklist checklist)
        {
            ProfileToggleHelper.ResetForTests();

            ApplicationSetting oldSetting = new ApplicationSetting();
            oldSetting.Name = "TestGameCleanupOldName";
            ProfileToggleHelper.SetSuppressed("TestGameCleanupOldName", true);

            VibranceGUI.ClearSuppressionIfNameChanged(oldSetting, "TestGameCleanupNewName");

            checklist.Check(!ProfileToggleHelper.IsSuppressed("TestGameCleanupOldName"),
                "Cleanup-2: \"Change executable...\" moving a suppressed profile to a new Name clears the suppression recorded under the OLD Name");

            ProfileToggleHelper.ResetForTests();
        }

        // Mutation this guards: clear suppression unconditionally (or key it on something other
        // than Name), wiping out an unrelated profile's own, legitimate suppression.
        private static void CheckClearSuppressionLeavesADifferentSuppressionAlone(Checklist checklist)
        {
            ProfileToggleHelper.ResetForTests();

            ApplicationSetting oldSetting = new ApplicationSetting();
            oldSetting.Name = "TestGameCleanupA";
            ProfileToggleHelper.SetSuppressed("TestGameCleanupA", true);
            ProfileToggleHelper.SetSuppressed("TestGameCleanupB", true);

            VibranceGUI.ClearSuppressionIfNameChanged(oldSetting, "TestGameCleanupANewName");

            checklist.Check(!ProfileToggleHelper.IsSuppressed("TestGameCleanupA") && ProfileToggleHelper.IsSuppressed("TestGameCleanupB"),
                "Cleanup-3: clearing profile A's suppression on rename leaves a DIFFERENT profile B's own suppression untouched");

            ProfileToggleHelper.ResetForTests();
        }

        // ------------------------------------------------------------------
        // Application list marker - VibranceGUI.DescribeListItem (the pure decision
        // ApplyApplicationListItemAppearance turns into ListViewItem.Text/ForeColor/ToolTipText),
        // VibranceGUI.ShouldRefreshListItemForToggleResult (the gate
        // RefreshToggledListItemAppearance opens on), and VibranceGUI.FindApplicationSettingsByName
        // (the decision behind that same method's repaint - which of possibly SEVERAL settings
        // sharing one Name need their row redrawn, see that method's own comment for why two
        // settings can share a Name at all). All three are internal statics called directly - no
        // reflection, no Form: ApplyApplicationListItemAppearance and
        // RefreshToggledListItemAppearance themselves need a real ListView on a real Form
        // (VibranceGUI's own constructor calls getProxy(...)), which is exactly why the decision
        // each one makes is pulled out into something a fixture CAN reach - the real gate, not a
        // hand-copied mirror of it.
        // ------------------------------------------------------------------

        private static void RunListItemMarkerChecks(Checklist checklist)
        {
            checklist.Lines.Add("Application list marker (VibranceGUI.DescribeListItem / ShouldRefreshListItemForToggleResult / FindApplicationSettingsByName, real methods, no reflection):");

            CheckDescribeListItemNeitherFlag(checklist);
            CheckDescribeListItemUnconfirmedOnly(checklist);
            CheckDescribeListItemSuppressedOnly(checklist);
            CheckDescribeListItemBothFlagsComposeBothSuffixes(checklist);
            CheckDescribeListItemBothFlagsSuppressionWinsTheColour(checklist);
            CheckDescribeListItemBothFlagsComposeBothTooltipParagraphs(checklist);
            CheckDescribeListItemSuppressedColourIsNotGrayTextOrWindowText(checklist);
            CheckDescribeListItemNullNameDoesNotThrow(checklist);
            CheckMarkerSuffixesAreDistinct(checklist);
            CheckShouldRefreshOnlyOnConfirmedToggles(checklist);
            CheckFindApplicationSettingsByNameReturnsOnlyTheMatchingOne(checklist);
            CheckFindApplicationSettingsByNameReturnsEveryProfileSharingTheName(checklist);
            CheckFindApplicationSettingsByNameExcludesADifferentName(checklist);
            CheckFindApplicationSettingsByNameIsCaseInsensitive(checklist);
            CheckFindApplicationSettingsByNameOnNullSettingsReturnsEmpty(checklist);
            CheckFindApplicationSettingsByNameOnNullOrEmptyNameReturnsEmpty(checklist);
            CheckFindApplicationSettingsByNameSkipsNullEntries(checklist);
            CheckResolveListItemAppearancesReturnsEveryProfileSharingTheName(checklist);
            CheckResolveListItemAppearancesOmitsARowWithNoLiveItem(checklist);
            CheckResolveListItemAppearancesContentMatchesDescribeListItem(checklist);
            CheckResolveListItemAppearancesReflectsSuppressionState(checklist);
            CheckResolveListItemAppearancesOnNullAvailableTagsReturnsEmpty(checklist);
            CheckResolveListItemAppearancesOnNullSettingsReturnsEmpty(checklist);
            CheckResolveListItemAppearancesSkipsNullFileName(checklist);

            checklist.Lines.Add(string.Empty);
        }

        // M1. Mutation this guards: append a suffix (or change the colour/tooltip) even when
        // neither flag is set.
        private static void CheckDescribeListItemNeitherFlag(Checklist checklist)
        {
            string text;
            Color foreColor;
            string toolTip;
            VibranceGUI.DescribeListItem("TestMarkerGame", false, false, out text, out foreColor, out toolTip);

            checklist.Check(text == "TestMarkerGame" && foreColor == SystemColors.WindowText && toolTip == string.Empty,
                string.Format("M1: neither flag set -> unchanged name, WindowText, empty tooltip, got text=\"{0}\" foreColor={1} toolTip=\"{2}\"",
                    text, foreColor, toolTip));
        }

        // M2. Mutation this guards: drop the "(?)" suffix, use the wrong colour, or lose the
        // unconfirmed tooltip - the pre-existing single-marker behaviour PR #14's own review left
        // untouched.
        private static void CheckDescribeListItemUnconfirmedOnly(Checklist checklist)
        {
            string text;
            Color foreColor;
            string toolTip;
            VibranceGUI.DescribeListItem("TestMarkerGame", true, false, out text, out foreColor, out toolTip);

            bool ok = text == "TestMarkerGame" + VibranceGUI.UnconfirmedMarkerSuffix &&
                      foreColor == SystemColors.GrayText &&
                      toolTip == VibranceGUI.ToolTipExecutableUnconfirmed;
            checklist.Check(ok,
                string.Format("M2: unconfirmed only -> \"{0}\", GrayText, the unconfirmed tooltip verbatim, got text=\"{1}\" foreColor={2} toolTip=\"{3}\"",
                    "TestMarkerGame" + VibranceGUI.UnconfirmedMarkerSuffix, text, foreColor, toolTip));
        }

        // M3. Mutation this guards: drop the suppressed suffix, reuse GrayText/WindowText instead
        // of the dedicated colour, or lose the suppressed tooltip.
        private static void CheckDescribeListItemSuppressedOnly(Checklist checklist)
        {
            string text;
            Color foreColor;
            string toolTip;
            VibranceGUI.DescribeListItem("TestMarkerGame", false, true, out text, out foreColor, out toolTip);

            bool ok = text == "TestMarkerGame" + VibranceGUI.SuppressedMarkerSuffix &&
                      foreColor == VibranceGUI.SuppressedForeColor &&
                      toolTip == VibranceGUI.ToolTipSuppressed;
            checklist.Check(ok,
                string.Format("M3: suppressed only -> \"{0}\", the dedicated suppressed colour, the suppressed tooltip verbatim, got text=\"{1}\" foreColor={2} toolTip=\"{3}\"",
                    "TestMarkerGame" + VibranceGUI.SuppressedMarkerSuffix, text, foreColor, toolTip));
        }

        // M4. The doubly-marked case - both flags true. Mutation this guards: an if/else (or any
        // other structure) that lets one marker silently win over the other instead of composing,
        // or gets the suffix order backwards.
        private static void CheckDescribeListItemBothFlagsComposeBothSuffixes(Checklist checklist)
        {
            string text;
            Color foreColor;
            string toolTip;
            VibranceGUI.DescribeListItem("TestMarkerGame", true, true, out text, out foreColor, out toolTip);

            string expected = "TestMarkerGame" + VibranceGUI.SuppressedMarkerSuffix + VibranceGUI.UnconfirmedMarkerSuffix;
            checklist.Check(text == expected,
                string.Format("M4: both flags set -> BOTH suffixes present, suppressed first then unconfirmed (\"{0}\"), got text=\"{1}\"",
                    expected, text));
        }

        // M5. Mutation this guards: both flags true still resolving to GrayText (an if/else that
        // checks isUnconfirmed before isSuppressed) instead of the suppressed colour - exactly the
        // "reads as broken, not switched off" failure the architect flagged.
        private static void CheckDescribeListItemBothFlagsSuppressionWinsTheColour(Checklist checklist)
        {
            string text;
            Color foreColor;
            string toolTip;
            VibranceGUI.DescribeListItem("TestMarkerGame", true, true, out text, out foreColor, out toolTip);

            checklist.Check(foreColor == VibranceGUI.SuppressedForeColor,
                string.Format("M5: both flags set -> the suppressed colour wins (not GrayText), got foreColor={0}", foreColor));
        }

        // M6. Mutation this guards: only one tooltip paragraph survives when both flags are set
        // (e.g. an if/else-if instead of two independent ifs), or the paragraphs come out in the
        // wrong order.
        private static void CheckDescribeListItemBothFlagsComposeBothTooltipParagraphs(Checklist checklist)
        {
            string text;
            Color foreColor;
            string toolTip;
            VibranceGUI.DescribeListItem("TestMarkerGame", true, true, out text, out foreColor, out toolTip);

            int suppressedIndex = toolTip.IndexOf(VibranceGUI.ToolTipSuppressed, StringComparison.Ordinal);
            int unconfirmedIndex = toolTip.IndexOf(VibranceGUI.ToolTipExecutableUnconfirmed, StringComparison.Ordinal);
            bool ok = suppressedIndex >= 0 && unconfirmedIndex >= 0 && suppressedIndex < unconfirmedIndex;

            checklist.Check(ok,
                string.Format("M6: both flags set -> tooltip contains BOTH paragraphs, suppressed before unconfirmed, got suppressedIndex={0} unconfirmedIndex={1} toolTip=\"{2}\"",
                    suppressedIndex, unconfirmedIndex, toolTip));
        }

        // M7. Sanity check on the colour choice itself. Mutation this guards: SuppressedForeColor
        // defined as (or reassigned to) SystemColors.GrayText or SystemColors.WindowText, silently
        // undoing the "not GrayText" decision without any of M1-M6 necessarily catching it (M1/M2
        // pass GrayText/WindowText legitimately in their OWN branch; only comparing the constant
        // against both catches it being aliased to either).
        private static void CheckDescribeListItemSuppressedColourIsNotGrayTextOrWindowText(Checklist checklist)
        {
            checklist.Check(VibranceGUI.SuppressedForeColor != SystemColors.GrayText && VibranceGUI.SuppressedForeColor != SystemColors.WindowText,
                string.Format("M7: the suppressed marker's own colour is neither GrayText nor WindowText, got {0}", VibranceGUI.SuppressedForeColor));
        }

        // M8. A null name never throws (C#'s null-tolerant "+=" already makes that true whether or
        // not the guard exists - the suffix checks above exercise that path). What the "name ??
        // string.Empty" guard alone is responsible for is the case where NEITHER suffix ever runs:
        // without it, text would come back null instead of string.Empty. Mutation this guards:
        // drop the guard (just "text = name;"), which this case - and only this case - can tell
        // apart from the real code, since every other DescribeListItem check always appends at
        // least one suffix.
        private static void CheckDescribeListItemNullNameDoesNotThrow(Checklist checklist)
        {
            bool threw = false;
            string text = null;
            try
            {
                Color foreColor;
                string toolTip;
                VibranceGUI.DescribeListItem(null, false, false, out text, out foreColor, out toolTip);
            }
            catch (Exception)
            {
                threw = true;
            }

            checklist.Check(!threw && text == string.Empty,
                string.Format("M8: a null name with neither flag set resolves to string.Empty, not null (lvi.Text = null is the failure this avoids), got threw={0} text={1}",
                    threw, text == null ? "null" : "\"" + text + "\""));
        }

        // M9. Same independent-anchor technique as M7, applied to the two suffixes instead of the
        // colour. M2/M3/M4/M8 all compare the composed text against VibranceGUI.SuppressedMarkerSuffix/
        // UnconfirmedMarkerSuffix directly, so aliasing either constant to the OTHER constant's own
        // value reads back as a match on both sides and nothing above would notice - the "(Off)"
        // marker would render identically to "(?)" (or vice versa), silently erasing the distinction
        // this whole feature exists to draw. Mutation this guards: SuppressedMarkerSuffix set equal
        // to UnconfirmedMarkerSuffix's value, or UnconfirmedMarkerSuffix set equal to
        // SuppressedMarkerSuffix's value - proven separately below, they are different edits.
        // Both suffixes are const, so this comparison is folded to a literal true at compile
        // time: M9 has force only because the fixture compiles in the SAME assembly as
        // VibranceGUI.cs, which internal access already requires. Split this fixture into its
        // own assembly and M9 would freeze the values from its last build and go stale against
        // a rebuilt VibranceGUI - the classic cross-assembly const trap.
        private static void CheckMarkerSuffixesAreDistinct(Checklist checklist)
        {
            checklist.Check(VibranceGUI.SuppressedMarkerSuffix != VibranceGUI.UnconfirmedMarkerSuffix,
                string.Format("M9: the suppressed and unconfirmed suffixes are not the same text, got SuppressedMarkerSuffix=\"{0}\" UnconfirmedMarkerSuffix=\"{1}\"",
                    VibranceGUI.SuppressedMarkerSuffix, VibranceGUI.UnconfirmedMarkerSuffix));
        }

        // N1-N3. Mutation this guards: RefreshToggledListItemAppearance's gate refreshing on every
        // result (including WriteFailed/None/EngineNotReady, none of which ever flip
        // ProfileToggleHelper's suppression set - see both proxies' own ToggleForegroundProfile),
        // or not refreshing on one of the two toggled outcomes.
        private static void CheckShouldRefreshOnlyOnConfirmedToggles(Checklist checklist)
        {
            bool onOk = VibranceGUI.ShouldRefreshListItemForToggleResult(ProfileToggleResult.ToggledOn);
            bool offOk = VibranceGUI.ShouldRefreshListItemForToggleResult(ProfileToggleResult.ToggledOff);
            bool noneOk = !VibranceGUI.ShouldRefreshListItemForToggleResult(ProfileToggleResult.NoConfiguredGameInForeground);
            bool engineNotReadyOk = !VibranceGUI.ShouldRefreshListItemForToggleResult(ProfileToggleResult.EngineNotReady);
            bool writeFailedOk = !VibranceGUI.ShouldRefreshListItemForToggleResult(ProfileToggleResult.WriteFailed);

            checklist.Check(onOk && offOk && noneOk && engineNotReadyOk && writeFailedOk,
                string.Format("N1-N3: refresh fires for ToggledOn/ToggledOff only, never for NoConfiguredGameInForeground/EngineNotReady/WriteFailed, got ToggledOn={0} ToggledOff={1} NoConfiguredGameInForeground={2} EngineNotReady={3} WriteFailed={4}",
                    onOk, offOk, noneOk, engineNotReadyOk, writeFailedOk));
        }

        // O1-O7. VibranceGUI.FindApplicationSettingsByName - the decision behind
        // RefreshToggledListItemAppearance's repaint. Two ApplicationSetting entries CAN share one
        // Name: Name is Path.GetFileNameWithoutExtension of whatever executable the user picked
        // (VibranceSettings.resolveApplicationName), and nothing stops two installs - a demo and
        // the full game, two store copies, two unrelated games - from producing the same bare file
        // name while their FileName (the full path) stays distinct. ProfileToggleHelper's
        // suppression set is keyed by that Name, so one hotkey press suppresses every entry that
        // shares it; this function is what turns "the one setting ApplicationSettingMatcher.
        // FindMatch resolved" into "every row whose suppression state just changed together with
        // it" - the fix for the defect where only the resolved row repainted and any other
        // same-Name row went stale.

        // O1. The common case: one setting has the toggled Name, a second has a different one.
        // Mutation this guards: returning every setting regardless of Name (e.g. the guard clause
        // deleted), or comparing the wrong field (FileName instead of Name).
        private static void CheckFindApplicationSettingsByNameReturnsOnlyTheMatchingOne(Checklist checklist)
        {
            ApplicationSetting target = new ApplicationSetting { Name = "game", FileName = @"D:\A\game.exe" };
            ApplicationSetting other = new ApplicationSetting { Name = "otherGame", FileName = @"D:\B\otherGame.exe" };
            List<ApplicationSetting> settings = new List<ApplicationSetting> { target, other };

            List<ApplicationSetting> result = VibranceGUI.FindApplicationSettingsByName(settings, "game");

            checklist.Check(result.Count == 1 && result[0] == target,
                string.Format("O1: only the setting whose Name matches is returned, got count={0} containsTarget={1}",
                    result.Count, result.Contains(target)));
        }

        // O2. The defect itself: TWO settings share the toggled Name (distinct FileName, as two
        // installs of a same-named executable would be) - both must come back, not just the one
        // ApplicationSettingMatcher.FindMatch happened to resolve. Mutation this guards: an early
        // "return" the moment one match is found instead of continuing the loop - exactly the bug
        // RefreshToggledListItemAppearance used to have via FindMatch's own single-result contract.
        private static void CheckFindApplicationSettingsByNameReturnsEveryProfileSharingTheName(Checklist checklist)
        {
            ApplicationSetting first = new ApplicationSetting { Name = "game", FileName = @"D:\A\game.exe" };
            ApplicationSetting second = new ApplicationSetting { Name = "game", FileName = @"D:\B\game.exe" };
            List<ApplicationSetting> settings = new List<ApplicationSetting> { first, second };

            List<ApplicationSetting> result = VibranceGUI.FindApplicationSettingsByName(settings, "game");

            checklist.Check(result.Count == 2 && result.Contains(first) && result.Contains(second),
                string.Format("O2: two settings sharing one Name (distinct FileName) both come back, got count={0} containsFirst={1} containsSecond={2}",
                    result.Count, result.Contains(first), result.Contains(second)));
        }

        // O3. Companion to O2 - a third, unrelated setting must NOT be swept in alongside the two
        // that share the toggled Name. Mutation this guards: the loop's condition dropped
        // entirely (returning the whole list unfiltered) - undetectable by O2 alone, since O2's
        // two settings legitimately share a Name and a "return everything" bug would still pass
        // O1 if settings only ever held two entries, but not once a third, non-matching one is
        // added to the same list.
        private static void CheckFindApplicationSettingsByNameExcludesADifferentName(Checklist checklist)
        {
            ApplicationSetting first = new ApplicationSetting { Name = "game", FileName = @"D:\A\game.exe" };
            ApplicationSetting second = new ApplicationSetting { Name = "game", FileName = @"D:\B\game.exe" };
            ApplicationSetting unrelated = new ApplicationSetting { Name = "otherGame", FileName = @"D:\C\otherGame.exe" };
            List<ApplicationSetting> settings = new List<ApplicationSetting> { first, second, unrelated };

            List<ApplicationSetting> result = VibranceGUI.FindApplicationSettingsByName(settings, "game");

            checklist.Check(result.Count == 2 && !result.Contains(unrelated),
                string.Format("O3: a differently-named setting in the same list is excluded, got count={0} containsUnrelated={1}",
                    result.Count, result.Contains(unrelated)));
        }

        // O4. The comparison must be case-insensitive, exactly like ProfileToggleHelper's own
        // suppression set (NameMatches, ApplicationSettingMatcher.cs:89-94) - and, per
        // ProfileToggleHelper.NameComparer's own comment, derived from that SAME comparer rather
        // than a second, independent one. Mutation this guards: comparing with
        // StringComparison.Ordinal (or plain "==") instead of ProfileToggleHelper.NameComparer.
        private static void CheckFindApplicationSettingsByNameIsCaseInsensitive(Checklist checklist)
        {
            ApplicationSetting setting = new ApplicationSetting { Name = "Game", FileName = @"D:\A\Game.exe" };
            List<ApplicationSetting> settings = new List<ApplicationSetting> { setting };

            List<ApplicationSetting> result = VibranceGUI.FindApplicationSettingsByName(settings, "GAME");

            checklist.Check(result.Count == 1 && result[0] == setting,
                string.Format("O4: Name comparison is case-insensitive (stored \"Game\", toggled \"GAME\"), got count={0}", result.Count));
        }

        // O5. A null settings list (the empty-startup-list window RefreshToggledListItemAppearance's
        // own comment describes) never throws and yields an empty, non-null list rather than null -
        // the caller's for loop would NullReferenceException on a null return.
        private static void CheckFindApplicationSettingsByNameOnNullSettingsReturnsEmpty(Checklist checklist)
        {
            bool threw = false;
            List<ApplicationSetting> result = null;
            try
            {
                result = VibranceGUI.FindApplicationSettingsByName(null, "game");
            }
            catch (Exception)
            {
                threw = true;
            }

            checklist.Check(!threw && result != null && result.Count == 0,
                string.Format("O5: a null settings list never throws and returns an empty (not null) list, got threw={0} result={1}",
                    threw, result == null ? "null" : "count=" + result.Count));
        }

        // O6. A null or empty toggled name (IsSuppressed itself already refuses both - see
        // ProfileToggleHelper.IsSuppressed) must not match a setting whose own Name happens to be
        // null or empty, and must not throw doing the comparison.
        private static void CheckFindApplicationSettingsByNameOnNullOrEmptyNameReturnsEmpty(Checklist checklist)
        {
            ApplicationSetting blankNamed = new ApplicationSetting { Name = null, FileName = @"D:\A\blank.exe" };
            List<ApplicationSetting> settings = new List<ApplicationSetting> { blankNamed };

            bool threwOnNull = false;
            bool threwOnEmpty = false;
            List<ApplicationSetting> resultForNull = null;
            List<ApplicationSetting> resultForEmpty = null;
            try
            {
                resultForNull = VibranceGUI.FindApplicationSettingsByName(settings, null);
            }
            catch (Exception)
            {
                threwOnNull = true;
            }

            try
            {
                resultForEmpty = VibranceGUI.FindApplicationSettingsByName(settings, string.Empty);
            }
            catch (Exception)
            {
                threwOnEmpty = true;
            }

            checklist.Check(!threwOnNull && !threwOnEmpty && resultForNull.Count == 0 && resultForEmpty.Count == 0,
                string.Format("O6: a null or empty toggled name never throws and never matches a null-Name setting, got threwOnNull={0} threwOnEmpty={1} countForNull={2} countForEmpty={3}",
                    threwOnNull, threwOnEmpty, resultForNull == null ? -1 : resultForNull.Count, resultForEmpty == null ? -1 : resultForEmpty.Count));
        }

        // O7. Defensive: a null entry inside the settings list (never produced by
        // SettingsController today, but ApplicationSettingMatcher.FindMatch itself already guards
        // the same way - see its own filter null-check) must be skipped, not dereferenced.
        private static void CheckFindApplicationSettingsByNameSkipsNullEntries(Checklist checklist)
        {
            ApplicationSetting target = new ApplicationSetting { Name = "game", FileName = @"D:\A\game.exe" };
            List<ApplicationSetting> settings = new List<ApplicationSetting> { null, target };

            bool threw = false;
            List<ApplicationSetting> result = null;
            try
            {
                result = VibranceGUI.FindApplicationSettingsByName(settings, "game");
            }
            catch (Exception)
            {
                threw = true;
            }

            checklist.Check(!threw && result != null && result.Count == 1 && result[0] == target,
                string.Format("O7: a null entry in the settings list is skipped, not dereferenced, got threw={0} count={1}",
                    threw, result == null ? -1 : result.Count));
        }

        // P1-P7. VibranceGUI.ResolveListItemAppearances - the whole repaint decision behind
        // RefreshToggledListItemAppearance, not just which settings share a Name (O1-O7 above)
        // but, for each one, whether a live row exists for it at all and, if so, exactly what
        // that row becomes. This is the wiring FindApplicationSettingsByName and DescribeListItem
        // were never exercised through TOGETHER before this method existed: reverting
        // RefreshToggledListItemAppearance's own loop to repaint only the single FindMatch result
        // used to pass every check in this file, because nothing called the combination the real
        // bug lived in. Called directly here, the same real method
        // RefreshToggledListItemAppearance calls - no reflection, no Form.

        // P1. Two settings share the toggled Name (distinct FileName) and BOTH have a live row
        // (both tags present in availableTags) - both must come back, not just the one
        // ApplicationSettingMatcher.FindMatch would have resolved. Mutation this guards: an early
        // return the moment one appearance is produced, or resolving only toRepaint[0].
        private static void CheckResolveListItemAppearancesReturnsEveryProfileSharingTheName(Checklist checklist)
        {
            ApplicationSetting first = new ApplicationSetting { Name = "game", FileName = @"D:\A\game.exe" };
            ApplicationSetting second = new ApplicationSetting { Name = "game", FileName = @"D:\B\game.exe" };
            List<ApplicationSetting> settings = new List<ApplicationSetting> { first, second };
            HashSet<string> tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { first.FileName, second.FileName };

            List<ApplicationListItemAppearance> result = VibranceGUI.ResolveListItemAppearances(settings, "game", tags);

            bool ok = result.Count == 2 &&
                      result.Exists(a => a.Tag == first.FileName) &&
                      result.Exists(a => a.Tag == second.FileName);
            checklist.Check(ok,
                string.Format("P1: two settings sharing one Name, both with a live row, both produce an appearance, got count={0}", result.Count));
        }

        // P2. Companion to P1: a setting whose Name matches but whose tag is NOT among
        // availableTags (no ListViewItem for it yet) produces NO entry - this is the lvi == null
        // case RefreshToggledListItemAppearance used to test inline, decided here instead now.
        // Mutation this guards: dropping the availableTags check entirely, which would hand
        // RefreshToggledListItemAppearance a Tag that FindApplicationListItem can never resolve -
        // harmless there only because of ITS OWN separate null check, which is not what this
        // check pins.
        private static void CheckResolveListItemAppearancesOmitsARowWithNoLiveItem(Checklist checklist)
        {
            ApplicationSetting onScreen = new ApplicationSetting { Name = "game", FileName = @"D:\A\game.exe" };
            ApplicationSetting notYetCreated = new ApplicationSetting { Name = "game", FileName = @"D:\B\game.exe" };
            List<ApplicationSetting> settings = new List<ApplicationSetting> { onScreen, notYetCreated };
            HashSet<string> tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { onScreen.FileName };

            List<ApplicationListItemAppearance> result = VibranceGUI.ResolveListItemAppearances(settings, "game", tags);

            checklist.Check(result.Count == 1 && result[0].Tag == onScreen.FileName,
                string.Format("P2: a setting matching by Name but absent from availableTags produces no entry, got count={0}", result.Count));
        }

        // P3. The content of a produced appearance must be exactly what DescribeListItem itself
        // computes for that setting's own flags - not a hand-copied mirror of M1-M9's logic.
        // Mutation this guards: passing the wrong isUnconfirmed/isSuppressed pair into
        // DescribeListItem, or composing Text/ForeColor/ToolTip some other way than that call.
        private static void CheckResolveListItemAppearancesContentMatchesDescribeListItem(Checklist checklist)
        {
            ApplicationSetting setting = new ApplicationSetting { Name = "TestResolveGame", FileName = @"D:\A\TestResolveGame.exe", IsExecutableUnconfirmed = true };
            List<ApplicationSetting> settings = new List<ApplicationSetting> { setting };
            HashSet<string> tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { setting.FileName };

            ProfileToggleHelper.SetSuppressed("TestResolveGame", false);
            List<ApplicationListItemAppearance> result = VibranceGUI.ResolveListItemAppearances(settings, "TestResolveGame", tags);

            string expectedText;
            Color expectedColor;
            string expectedToolTip;
            VibranceGUI.DescribeListItem("TestResolveGame", true, false, out expectedText, out expectedColor, out expectedToolTip);

            bool ok = result.Count == 1 &&
                      result[0].Text == expectedText &&
                      result[0].ForeColor == expectedColor &&
                      result[0].ToolTip == expectedToolTip;
            checklist.Check(ok,
                string.Format("P3: an appearance's Text/ForeColor/ToolTip match DescribeListItem's own decision for that setting's flags, got text=\"{0}\" foreColor={1}",
                    result.Count > 0 ? result[0].Text : "<none>", result.Count > 0 ? result[0].ForeColor.ToString() : "<none>"));

            ProfileToggleHelper.ResetForTests();
        }

        // P4. A suppressed profile's appearance must differ from the same profile unsuppressed -
        // exercising ResolveListItemAppearances' own call to ProfileToggleHelper.IsSuppressed
        // (read fresh, never cached) rather than a snapshot taken once outside the loop. Mutation
        // this guards: hardcoding isSuppressed (either literal) into the DescribeListItem call.
        private static void CheckResolveListItemAppearancesReflectsSuppressionState(Checklist checklist)
        {
            ApplicationSetting setting = new ApplicationSetting { Name = "TestResolveSuppressGame", FileName = @"D:\A\TestResolveSuppressGame.exe" };
            List<ApplicationSetting> settings = new List<ApplicationSetting> { setting };
            HashSet<string> tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { setting.FileName };

            ProfileToggleHelper.SetSuppressed("TestResolveSuppressGame", false);
            List<ApplicationListItemAppearance> unsuppressed = VibranceGUI.ResolveListItemAppearances(settings, "TestResolveSuppressGame", tags);

            ProfileToggleHelper.SetSuppressed("TestResolveSuppressGame", true);
            List<ApplicationListItemAppearance> suppressed = VibranceGUI.ResolveListItemAppearances(settings, "TestResolveSuppressGame", tags);

            bool ok = unsuppressed.Count == 1 && suppressed.Count == 1 &&
                      unsuppressed[0].Text != suppressed[0].Text &&
                      unsuppressed[0].ForeColor != suppressed[0].ForeColor;
            checklist.Check(ok,
                string.Format("P4: a suppressed profile's appearance differs from the same profile unsuppressed, got unsuppressedText=\"{0}\" suppressedText=\"{1}\"",
                    unsuppressed.Count > 0 ? unsuppressed[0].Text : "<none>", suppressed.Count > 0 ? suppressed[0].Text : "<none>"));

            ProfileToggleHelper.ResetForTests();
        }

        // P5. A null availableTags never throws and yields no appearances - GetApplicationListItemTags
        // always hands back a real HashSet, but nothing here should crash, or resolve every match
        // by some default-permissive behaviour, if that ever stops being true.
        private static void CheckResolveListItemAppearancesOnNullAvailableTagsReturnsEmpty(Checklist checklist)
        {
            ApplicationSetting setting = new ApplicationSetting { Name = "game", FileName = @"D:\A\game.exe" };
            List<ApplicationSetting> settings = new List<ApplicationSetting> { setting };

            bool threw = false;
            List<ApplicationListItemAppearance> result = null;
            try
            {
                result = VibranceGUI.ResolveListItemAppearances(settings, "game", null);
            }
            catch (Exception)
            {
                threw = true;
            }

            checklist.Check(!threw && result != null && result.Count == 0,
                string.Format("P5: a null availableTags never throws and returns an empty (not null) list, got threw={0} result={1}",
                    threw, result == null ? "null" : "count=" + result.Count));
        }

        // P6. A null settings list (the same startup window FindApplicationSettingsByName's own
        // O5 already covers) must not throw flowing through this extra layer either.
        private static void CheckResolveListItemAppearancesOnNullSettingsReturnsEmpty(Checklist checklist)
        {
            bool threw = false;
            List<ApplicationListItemAppearance> result = null;
            try
            {
                result = VibranceGUI.ResolveListItemAppearances(null, "game", new HashSet<string>());
            }
            catch (Exception)
            {
                threw = true;
            }

            checklist.Check(!threw && result != null && result.Count == 0,
                string.Format("P6: a null settings list never throws and returns an empty (not null) list, got threw={0} result={1}",
                    threw, result == null ? "null" : "count=" + result.Count));
        }

        // P7. A setting whose Name matches but whose own FileName is null or empty must be
        // skipped, never matched against availableTags at all - even in the one case that check
        // could otherwise (wrongly) succeed: availableTags itself containing string.Empty (a
        // ListViewItem whose Tag somehow ended up ""). Mutation this guards: dropping the
        // FileName guard and relying on availableTags.Contains alone - HashSet&lt;string&gt;.Contains(null)
        // returns false harmlessly (proven separately: that half of this guard is not load-bearing
        // on its own), but Contains(string.Empty) against a set that legitimately holds "" is not,
        // which is why this check pins string.Empty rather than null.
        private static void CheckResolveListItemAppearancesSkipsNullFileName(Checklist checklist)
        {
            ApplicationSetting blankFileName = new ApplicationSetting { Name = "game", FileName = string.Empty };
            List<ApplicationSetting> settings = new List<ApplicationSetting> { blankFileName };
            HashSet<string> tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { string.Empty };

            bool threw = false;
            List<ApplicationListItemAppearance> result = null;
            try
            {
                result = VibranceGUI.ResolveListItemAppearances(settings, "game", tags);
            }
            catch (Exception)
            {
                threw = true;
            }

            checklist.Check(!threw && result != null && result.Count == 0,
                string.Format("P7: a matching setting with a null FileName is skipped, not dereferenced, got threw={0} count={1}",
                    threw, result == null ? -1 : result.Count));
        }

        private static void InvokeNvidiaOnWinEventHook(string processName, IntPtr handle)
        {
            MethodInfo onWinEventHook = typeof(NvidiaDynamicVibranceProxy).GetMethod(
                "OnWinEventHook", BindingFlags.NonPublic | BindingFlags.Static);
            WinEventHookEventArgs args = new WinEventHookEventArgs { Handle = handle, ProcessName = processName, ProcessImagePath = null };
            onWinEventHook.Invoke(null, new object[] { null, args });
        }

        private static Screen GetNvidiaGameScreen()
        {
            FieldInfo f = typeof(NvidiaDynamicVibranceProxy).GetField("_gameScreen", BindingFlags.NonPublic | BindingFlags.Static);
            return (Screen)f.GetValue(null);
        }

        private static void SetNvidiaGameScreen(Screen value)
        {
            FieldInfo f = typeof(NvidiaDynamicVibranceProxy).GetField("_gameScreen", BindingFlags.NonPublic | BindingFlags.Static);
            f.SetValue(null, value);
        }

        // G1. Mutation this guards: remove the suppression gate entirely.
        private static void CheckNvidiaSuppressedGameMakesNoCalls(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestGameG1";
            matchingSetting.IngameLevel = 40;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };
            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.neverChangeResolution = true;
            vibranceInfo.neverChangeColorSettings = true;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);
            ProfileToggleHelper.SetSuppressed("TestGameG1", true);

            InvokeNvidiaOnWinEventHook("TestGameG1", GetDesktopWindow());

            checklist.Check(device.TotalCallCount == 0,
                string.Format("G1: a suppressed game's own foreground event makes zero device calls, got {0}", device.TotalCallCount));

            ProfileToggleHelper.ResetForTests();
        }

        // G2. Mutation this guards: place the gate AFTER "_gameScreen = screen;" instead of
        // before it - _gameScreen is forced to a known baseline (null) first, so this does not
        // depend on how many real monitors are attached.
        private static void CheckNvidiaGameScreenUnchangedWhenSuppressed(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestGameG2";
            matchingSetting.IngameLevel = 40;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };
            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.neverChangeResolution = true;
            vibranceInfo.neverChangeColorSettings = true;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);
            ProfileToggleHelper.SetSuppressed("TestGameG2", true);
            SetNvidiaGameScreen(null);

            InvokeNvidiaOnWinEventHook("TestGameG2", GetDesktopWindow());

            checklist.Check(GetNvidiaGameScreen() == null,
                "G2: _gameScreen is left exactly as it was (null) when the matched game is suppressed - a gate placed AFTER \"_gameScreen = screen\" would leave it non-null instead");

            ProfileToggleHelper.ResetForTests();
        }

        // G3. Mutation this guards: key the gate on "any suppression exists at all"
        // (ProfileToggleHelper.SuppressedCount > 0) instead of THIS setting's own name.
        private static void CheckNvidiaDifferentUnsuppressedGameStillApplies(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            ApplicationSetting suppressedSetting = new ApplicationSetting();
            suppressedSetting.Name = "TestGameG3Suppressed";
            suppressedSetting.IngameLevel = 40;
            ApplicationSetting unsuppressedSetting = new ApplicationSetting();
            unsuppressedSetting.Name = "TestGameG3Unsuppressed";
            unsuppressedSetting.IngameLevel = 60;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { suppressedSetting, unsuppressedSetting };
            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.neverChangeResolution = true;
            vibranceInfo.neverChangeColorSettings = true;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);
            ProfileToggleHelper.SetSuppressed("TestGameG3Suppressed", true);

            InvokeNvidiaOnWinEventHook("TestGameG3Unsuppressed", GetDesktopWindow());

            checklist.Check(device.SetLevelCalls.Count == 1,
                string.Format("G3: a DIFFERENT, unsuppressed game still applies normally while another profile is suppressed, got {0} SetLevel call(s)", device.SetLevelCalls.Count));

            ProfileToggleHelper.ResetForTests();
        }

        // G4. Mutation this guards: gate the WHOLE handler (both branches) on any suppression
        // existing, the shape of the discarded global-pause design's gate.
        private static void CheckNvidiaRestoreBranchStillRunsWhileSomethingIsSuppressed(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            ApplicationSetting suppressedSetting = new ApplicationSetting();
            suppressedSetting.Name = "TestGameG4";
            suppressedSetting.IngameLevel = 40;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { suppressedSetting };
            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.neverChangeResolution = true;
            vibranceInfo.neverChangeColorSettings = true;
            vibranceInfo.isWindowsLevelKnown = true;
            vibranceInfo.userVibranceSettingDefault = 25;
            // Scopes RestoreWindowsVibranceLevel to the work-list + primary
            // (ComposeRestoreTargets/RestoreOneDisplay) - the branch this check actually means to
            // exercise. Left at the struct default (false), it takes the OTHER restore branch
            // instead, which only ever consults displayHandles (null here) and drains the whole
            // work-list via ClearAllGameLevelRecords() without writing anything at all - a false
            // "restore branch does nothing" that has nothing to do with the suppression gate.
            vibranceInfo.affectPrimaryMonitorOnly = true;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);
            ProfileToggleHelper.SetSuppressed("TestGameG4", true);

            const string worklistDevice = "\\\\.\\DISPLAY_TESTONLY_G4";
            VibranceRestoreHelper.RecordGameLevelApplied(worklistDevice);

            // A non-matching process name (desktop, explorer, ...) drives the restore ("else")
            // branch, not the apply branch the suppression gate sits in.
            InvokeNvidiaOnWinEventHook("explorer", GetDesktopWindow());

            checklist.Check(device.SetLevelCalls.Count > 0 && VibranceRestoreHelper.HoldingCount == 0,
                string.Format("G4: the restore branch still runs (and drains the work-list) while a DIFFERENT profile is suppressed, got {0} SetLevel call(s), HoldingCount={1}",
                    device.SetLevelCalls.Count, VibranceRestoreHelper.HoldingCount));

            ProfileToggleHelper.ResetForTests();
        }

        // G5 (PIN). The ordinary, not-suppressed apply path still works once the gate exists.
        private static void CheckNvidiaNotSuppressedAppliesNormally(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestGameG5";
            matchingSetting.IngameLevel = 40;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };
            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.neverChangeResolution = true;
            vibranceInfo.neverChangeColorSettings = true;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);

            InvokeNvidiaOnWinEventHook("TestGameG5", GetDesktopWindow());

            checklist.Check(device.SetLevelCalls.Count == 1,
                string.Format("G5 (pin): not suppressed + a matched game applies the ingame level as today, got {0} SetLevel call(s)", device.SetLevelCalls.Count));
        }

        // G6, AMD's counterpart of G1.
        private static void CheckAmdSuppressedGameMakesNoCalls(Checklist checklist)
        {
            ProfileToggleHelper.ResetForTests();
            FakeAmdAdapter adapter = new FakeAmdAdapter();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestGameG6";
            matchingSetting.IngameLevel = 200;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };
            AmdDynamicVibranceProxy proxy = BuildAmdProxy(adapter, settings);
            proxy.SetVibranceWindowsLevel(90);
            ProfileToggleHelper.SetSuppressed("TestGameG6", true);

            MethodInfo onWinEventHook = typeof(AmdDynamicVibranceProxy).GetMethod("OnWinEventHook", BindingFlags.NonPublic | BindingFlags.Instance);
            WinEventHookEventArgs args = new WinEventHookEventArgs { Handle = GetDesktopWindow(), ProcessName = "TestGameG6", ProcessImagePath = null };
            onWinEventHook.Invoke(proxy, new object[] { null, args });

            checklist.Check(adapter.SetSaturationOnAllDisplaysCallCount == 0 && adapter.SetSaturationOnDisplayNames.Count == 0,
                string.Format("G6: a suppressed AMD game's own foreground event makes zero adapter calls, got allCalls={0} perDisplayCalls={1}",
                    adapter.SetSaturationOnAllDisplaysCallCount, adapter.SetSaturationOnDisplayNames.Count));

            ProfileToggleHelper.ResetForTests();
        }

        // ------------------------------------------------------------------
        // Settings round trip - a temp INI/XML pair, deleted in a finally, NEVER the user's real
        // "%APPDATA%\vibranceGUI\vibranceGUI.ini". Both toggle-hotkey keys (toggleHotkey,
        // toggleHotkeyEnabled), plus ReadVibranceSettings' own affectPrimaryMonitorOnly fallback
        // parity below.
        // ------------------------------------------------------------------

        private static void RunSettingsChecks(Checklist checklist)
        {
            checklist.Lines.Add("SettingsController round trip, both keys (temp INI, never the user's real one):");

            CheckSettingsRoundTripBothKeys(checklist);
            CheckSettingsMissingKeysReadDefaults(checklist);
            CheckSettingsCorruptValuesDoNotThrow(checklist);
            CheckSettingsVibranceFallbackParity(checklist);
            CheckSettingsPartialParseFailureLeavesOtherSixIntact(checklist);
            CheckSettingsPartialParseFailurePreservesConfiguredGames(checklist);
            CheckSettingsMissingInactiveValueDefaultsWithoutLosingRest(checklist);
            CheckSettingsAllValuesUnparseableStillDefaultsAndReadsXml(checklist);

            checklist.Lines.Add(string.Empty);
        }

        private static string NewTempPath(string extension)
        {
            return Path.Combine(Path.GetTempPath(), "vibranceGUI-hotkey-fixture-" + Guid.NewGuid().ToString("N") + extension);
        }

        private static void DeleteFileIfExists(string path)
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
                // Best-effort temp file cleanup only - never let this mask a check's own result.
            }
        }

        // S1. Mutation this guards: have SetToggleHotkey/SetToggleHotkeyEnabled write under the
        // wrong key name, or the readers read a different one.
        private static void CheckSettingsRoundTripBothKeys(Checklist checklist)
        {
            string tempIni = NewTempPath(".ini");
            string tempXml = NewTempPath(".xml");
            try
            {
                SettingsController controller = new SettingsController(tempIni, tempXml);
                bool wroteBinding = controller.SetToggleHotkey("Ctrl+Alt+F9");
                bool wroteEnabled = controller.SetToggleHotkeyEnabled(true);
                string readBackBinding = controller.ReadToggleHotkey();
                bool readBackEnabled = controller.ReadToggleHotkeyEnabled();

                checklist.Check(wroteBinding && wroteEnabled && readBackBinding == "Ctrl+Alt+F9" && readBackEnabled,
                    string.Format("S1: toggleHotkey and toggleHotkeyEnabled both round-trip byte for byte against a temp INI, got wroteBinding={0} wroteEnabled={1} readBackBinding=\"{2}\" readBackEnabled={3}",
                        wroteBinding, wroteEnabled, readBackBinding, readBackEnabled));
            }
            finally
            {
                DeleteFileIfExists(tempIni);
                DeleteFileIfExists(tempXml);
            }
        }

        // S2. Mutation this guards: default either reader's missing-key value to something other
        // than ""/false.
        private static void CheckSettingsMissingKeysReadDefaults(Checklist checklist)
        {
            string tempIni = NewTempPath(".ini");
            string tempXml = NewTempPath(".xml");
            try
            {
                SettingsController controller = new SettingsController(tempIni, tempXml);
                // Writes a DIFFERENT key first, so the INI file exists but never contains either
                // toggle-hotkey key.
                controller.SetVibranceSetting("someOtherKey", "someOtherValue");

                string readBackBinding = controller.ReadToggleHotkey();
                bool readBackEnabled = controller.ReadToggleHotkeyEnabled();
                HotkeyBinding parsedBinding;
                bool parsed = HotkeyBindingParser.TryParse(readBackBinding, out parsedBinding);

                checklist.Check(readBackBinding == string.Empty && !parsed && !readBackEnabled,
                    string.Format("S2: missing keys read back as \"\"/false and never register, got readBackBinding=\"{0}\" parsed={1} readBackEnabled={2}",
                        readBackBinding, parsed, readBackEnabled));
            }
            finally
            {
                DeleteFileIfExists(tempIni);
                DeleteFileIfExists(tempXml);
            }
        }

        // S3. Mutation this guards: let either reader throw on a value the writers never
        // themselves produced.
        private static void CheckSettingsCorruptValuesDoNotThrow(Checklist checklist)
        {
            string tempIni = NewTempPath(".ini");
            string tempXml = NewTempPath(".xml");
            try
            {
                SettingsController controller = new SettingsController(tempIni, tempXml);
                controller.SetToggleHotkey("Ctrl+NotARealKeyName");
                controller.SetVibranceSetting("toggleHotkeyEnabled", "NotABool");

                bool threw = false;
                HotkeyBinding binding = HotkeyBinding.None;
                bool enabled = true;
                try
                {
                    string readBackBinding = controller.ReadToggleHotkey();
                    HotkeyBindingParser.TryParse(readBackBinding, out binding);
                    enabled = controller.ReadToggleHotkeyEnabled();
                }
                catch (Exception)
                {
                    threw = true;
                }

                checklist.Check(!threw && !binding.IsSet && !enabled,
                    string.Format("S3: a corrupt stored binding never throws and yields IsSet == false, and a corrupt bool string defaults to false, got threw={0} isSet={1} enabled={2}",
                        threw, binding.IsSet, enabled));
            }
            finally
            {
                DeleteFileIfExists(tempIni);
                DeleteFileIfExists(tempXml);
            }
        }

        // S4. Mutation this guards: let ReadVibranceSettings' missing-file fallback (:257-268)
        // and its parse-failure fallback (:346-357) set affectPrimaryMonitorOnly to different
        // values, or let either one drift from GetPrivateProfileString's own "true" default for
        // the key when it is simply absent (:291-294) - the ordinary, no-exception path. This is
        // the exact shape of the bug this pins: 62541a6 swept every OTHER default in this method
        // to "true"/"50"/"100" in both fallback blocks and updated the missing-file block's
        // affectPrimaryMonitorOnly to match, but missed the parse-failure block's - so a corrupt
        // INI silently applied vibrance to every monitor while a missing one applied it only to
        // the primary. Drives the real ReadVibranceSettings against real temp files for all three
        // paths - never a copy of its own defaulting logic.
        private static void CheckSettingsVibranceFallbackParity(Checklist checklist)
        {
            bool missingFiles = ReadAffectPrimaryMonitorOnlyWithMissingFiles();
            bool absentKey = ReadAffectPrimaryMonitorOnlyWithKeyAbsent();
            bool corruptValue = ReadAffectPrimaryMonitorOnlyWithCorruptValue();

            checklist.Check(missingFiles == absentKey && absentKey == corruptValue,
                string.Format("S4: ReadVibranceSettings' affectPrimaryMonitorOnly agrees across all three paths - missing file(s), the key simply absent from an otherwise valid file (GetPrivateProfileString's own default), and a present value that fails bool.Parse - got missingFiles={0} absentKey={1} corruptValue={2}",
                    missingFiles, absentKey, corruptValue));
        }

        private static bool ReadAffectPrimaryMonitorOnly(SettingsController controller)
        {
            int vibranceWindowsLevel;
            bool affectPrimaryMonitorOnly;
            bool neverSwitchResolution;
            bool neverChangeColorSettings;
            List<ApplicationSetting> applicationSettings;
            int brightnessWindowsLevel;
            int contrastWindowsLevel;
            int gammaWindowsLevel;

            controller.ReadVibranceSettings(GraphicsAdapter.Nvidia, out vibranceWindowsLevel, out affectPrimaryMonitorOnly,
                out neverSwitchResolution, out neverChangeColorSettings, out applicationSettings,
                out brightnessWindowsLevel, out contrastWindowsLevel, out gammaWindowsLevel);

            return affectPrimaryMonitorOnly;
        }

        // Neither temp path is ever created - :257-268 fires because IsFileExisting fails for
        // both.
        private static bool ReadAffectPrimaryMonitorOnlyWithMissingFiles()
        {
            string tempIni = NewTempPath(".ini");
            string tempXml = NewTempPath(".xml");
            SettingsController controller = new SettingsController(tempIni, tempXml);
            return ReadAffectPrimaryMonitorOnly(controller);
        }

        // Every OTHER key SetVibranceSettings would normally write is set individually instead,
        // so the INI exists and every other field parses cleanly, but affectPrimaryMonitorOnly
        // itself is never written - GetPrivateProfileString falls back to its own "true" default
        // for it (:291-294), and execution never enters either fallback block at all.
        private static bool ReadAffectPrimaryMonitorOnlyWithKeyAbsent()
        {
            string tempIni = NewTempPath(".ini");
            string tempXml = NewTempPath(".xml");
            try
            {
                SettingsController controller = new SettingsController(tempIni, tempXml);
                controller.SetVibranceSetting("inactiveValue", "50");
                controller.SetVibranceSetting("neverSwitchResolution", "true");
                controller.SetVibranceSetting("neverChangeColorSettings", "true");
                controller.SetVibranceSetting("brightnessWindowsLevel", "50");
                controller.SetVibranceSetting("contrastWindowsLevel", "50");
                controller.SetVibranceSetting("gammaWindowsLevel", "100");
                WriteEmptyApplicationSettingsXml(tempXml);

                return ReadAffectPrimaryMonitorOnly(controller);
            }
            finally
            {
                DeleteFileIfExists(tempIni);
                DeleteFileIfExists(tempXml);
            }
        }

        // Both files exist and every other value is well formed - only affectPrimaryMonitorOnly
        // itself fails bool.Parse, landing in the parse-failure block (:346-357) this check
        // exists to pin.
        private static bool ReadAffectPrimaryMonitorOnlyWithCorruptValue()
        {
            string tempIni = NewTempPath(".ini");
            string tempXml = NewTempPath(".xml");
            try
            {
                SettingsController controller = new SettingsController(tempIni, tempXml);
                controller.SetVibranceSettings("50", "NotABool", "true", "true", new List<ApplicationSetting>(),
                    "50", "50", "100");

                return ReadAffectPrimaryMonitorOnly(controller);
            }
            finally
            {
                DeleteFileIfExists(tempIni);
                DeleteFileIfExists(tempXml);
            }
        }

        // Matches SetVibranceSettings' own XML write exactly (SettingsController.cs:80-86) so the
        // "key absent" scenario above reaches a genuinely valid applicationData.xml without ever
        // writing affectPrimaryMonitorOnly to the INI - not a re-implementation of
        // SettingsController's defaulting/parsing decisions, just its own serialization
        // boilerplate.
        private static void WriteEmptyApplicationSettingsXml(string path)
        {
            WriteApplicationSettingsXml(path, new List<ApplicationSetting>());
        }

        // ------------------------------------------------------------------
        // S5-S8. Regression coverage for ReadVibranceSettings' partial-parse defect: a single
        // corrupt/missing INI key used to reset all seven values to defaults, empty
        // applicationSettings, and return before the XML read ever ran - discarding the user's
        // entire configured-game list over one unrelated typo. All four checks below drive the
        // real ReadVibranceSettings through real temp files, never a copy of its defaulting.
        // ------------------------------------------------------------------

        // Holds all eight ReadVibranceSettings outputs together so each check below can read them
        // by name instead of juggling eight separate out-variables - not a reimplementation of any
        // defaulting/parsing decision, just a container for what the real method actually returned.
        private class VibranceSettingsSnapshot
        {
            public int VibranceWindowsLevel;
            public bool AffectPrimaryMonitorOnly;
            public bool NeverSwitchResolution;
            public bool NeverChangeColorSettings;
            public List<ApplicationSetting> ApplicationSettings = new List<ApplicationSetting>();
            public int BrightnessWindowsLevel;
            public int ContrastWindowsLevel;
            public int GammaWindowsLevel;
        }

        private static VibranceSettingsSnapshot ReadVibranceSettingsSnapshot(SettingsController controller, GraphicsAdapter adapter)
        {
            VibranceSettingsSnapshot snapshot = new VibranceSettingsSnapshot();
            controller.ReadVibranceSettings(adapter, out snapshot.VibranceWindowsLevel, out snapshot.AffectPrimaryMonitorOnly,
                out snapshot.NeverSwitchResolution, out snapshot.NeverChangeColorSettings, out snapshot.ApplicationSettings,
                out snapshot.BrightnessWindowsLevel, out snapshot.ContrastWindowsLevel, out snapshot.GammaWindowsLevel);
            return snapshot;
        }

        // Same serialization boilerplate as WriteEmptyApplicationSettingsXml, generalised to any
        // list so S6-S8 below can pin a real, non-empty configured-game list surviving the round
        // trip - not just an empty one.
        private static void WriteApplicationSettingsXml(string path, List<ApplicationSetting> settings)
        {
            System.Xml.XmlWriter writer = System.Xml.XmlWriter.Create(path);
            System.Xml.Serialization.XmlSerializer serializer = new System.Xml.Serialization.XmlSerializer(typeof(List<ApplicationSetting>));
            serializer.Serialize(writer, settings);
            writer.Flush();
            writer.Close();
        }

        private static List<ApplicationSetting> MakeConfiguredGames(int count)
        {
            List<ApplicationSetting> games = new List<ApplicationSetting>();
            for (int i = 0; i < count; i++)
            {
                games.Add(new ApplicationSetting(
                    "Test Game " + i, "C:\\Games\\testgame" + i + ".exe", 30, null, false, 50, 50, 100));
            }
            return games;
        }

        // S5. Mutation this guards: reintroduce a single try/catch around all seven parses (or any
        // change that lets one corrupt key's fallback leak into a sibling key that parsed fine).
        // Six of the seven values are set to distinct, deliberately non-default numbers/bools so a
        // reset-to-defaults cannot masquerade as a pass; only contrastWindowsLevel is corrupted.
        private static void CheckSettingsPartialParseFailureLeavesOtherSixIntact(Checklist checklist)
        {
            string tempIni = NewTempPath(".ini");
            string tempXml = NewTempPath(".xml");
            try
            {
                SettingsController controller = new SettingsController(tempIni, tempXml);
                controller.SetVibranceSetting("inactiveValue", "42");
                controller.SetVibranceSetting("affectPrimaryMonitorOnly", "False");
                controller.SetVibranceSetting("neverSwitchResolution", "False");
                controller.SetVibranceSetting("neverChangeColorSettings", "False");
                controller.SetVibranceSetting("brightnessWindowsLevel", "77");
                controller.SetVibranceSetting("contrastWindowsLevel", "NotANumber");
                controller.SetVibranceSetting("gammaWindowsLevel", "33");
                WriteApplicationSettingsXml(tempXml, MakeConfiguredGames(1));

                VibranceSettingsSnapshot snapshot = ReadVibranceSettingsSnapshot(controller, GraphicsAdapter.Nvidia);

                bool ok = snapshot.VibranceWindowsLevel == 42 &&
                    snapshot.AffectPrimaryMonitorOnly == false &&
                    snapshot.NeverSwitchResolution == false &&
                    snapshot.NeverChangeColorSettings == false &&
                    snapshot.BrightnessWindowsLevel == 77 &&
                    snapshot.ContrastWindowsLevel == 50 &&
                    snapshot.GammaWindowsLevel == 33;

                checklist.Check(ok,
                    string.Format("S5: one corrupt value (contrastWindowsLevel) falls back to its own default (50) while the other six keep their file values, got vibranceWindowsLevel={0} affectPrimaryMonitorOnly={1} neverSwitchResolution={2} neverChangeColorSettings={3} brightnessWindowsLevel={4} contrastWindowsLevel={5} gammaWindowsLevel={6}",
                        snapshot.VibranceWindowsLevel, snapshot.AffectPrimaryMonitorOnly, snapshot.NeverSwitchResolution, snapshot.NeverChangeColorSettings, snapshot.BrightnessWindowsLevel, snapshot.ContrastWindowsLevel, snapshot.GammaWindowsLevel));
            }
            finally
            {
                DeleteFileIfExists(tempIni);
                DeleteFileIfExists(tempXml);
            }
        }

        // S6. Mutation this guards: the "return" inside the old parse-failure block short-circuiting
        // before the XML read ever ran - the core of the reported defect. A corrupt INI value must
        // never prevent SettingsController from even attempting to read applicationData.xml, since
        // that file holds the user's entire configured-game list and has its own, separate
        // try/catch. Two games, not one, so a count mix-up cannot masquerade as a pass.
        private static void CheckSettingsPartialParseFailurePreservesConfiguredGames(Checklist checklist)
        {
            string tempIni = NewTempPath(".ini");
            string tempXml = NewTempPath(".xml");
            try
            {
                SettingsController controller = new SettingsController(tempIni, tempXml);
                controller.SetVibranceSetting("inactiveValue", "10");
                controller.SetVibranceSetting("affectPrimaryMonitorOnly", "true");
                controller.SetVibranceSetting("neverSwitchResolution", "true");
                controller.SetVibranceSetting("neverChangeColorSettings", "true");
                controller.SetVibranceSetting("brightnessWindowsLevel", "50");
                controller.SetVibranceSetting("contrastWindowsLevel", "50");
                controller.SetVibranceSetting("gammaWindowsLevel", "NotANumber");
                List<ApplicationSetting> configuredGames = MakeConfiguredGames(2);
                WriteApplicationSettingsXml(tempXml, configuredGames);

                VibranceSettingsSnapshot snapshot = ReadVibranceSettingsSnapshot(controller, GraphicsAdapter.Nvidia);

                bool ok = snapshot.ApplicationSettings != null &&
                    snapshot.ApplicationSettings.Count == configuredGames.Count &&
                    snapshot.ApplicationSettings[0].FileName == configuredGames[0].FileName &&
                    snapshot.ApplicationSettings[1].FileName == configuredGames[1].FileName;

                checklist.Check(ok,
                    string.Format("S6: a single corrupt INI value (gammaWindowsLevel) never empties or skips reading the configured-game list, got count={0} (expected {1})",
                        snapshot.ApplicationSettings == null ? -1 : snapshot.ApplicationSettings.Count, configuredGames.Count));
            }
            finally
            {
                DeleteFileIfExists(tempIni);
                DeleteFileIfExists(tempXml);
            }
        }

        // S7. Mutation this guards: int.Parse("") throwing on a simply-absent inactiveValue key
        // (its own GetPrivateProfileString default is "", not "0") taking down the other six
        // values and the game list with it - the sharpest trigger for the whole defect, since
        // every other key's own absent-key default ("true"/"50"/"100") already parses cleanly.
        private static void CheckSettingsMissingInactiveValueDefaultsWithoutLosingRest(Checklist checklist)
        {
            string tempIni = NewTempPath(".ini");
            string tempXml = NewTempPath(".xml");
            try
            {
                SettingsController controller = new SettingsController(tempIni, tempXml);
                // inactiveValue itself is never written, so GetPrivateProfileString falls back to
                // "" for it - int.TryParse("") must fail cleanly and default to defaultLevel, not
                // throw and not disturb anything below.
                controller.SetVibranceSetting("affectPrimaryMonitorOnly", "False");
                controller.SetVibranceSetting("neverSwitchResolution", "False");
                controller.SetVibranceSetting("neverChangeColorSettings", "False");
                controller.SetVibranceSetting("brightnessWindowsLevel", "77");
                controller.SetVibranceSetting("contrastWindowsLevel", "88");
                controller.SetVibranceSetting("gammaWindowsLevel", "33");
                List<ApplicationSetting> configuredGames = MakeConfiguredGames(1);
                WriteApplicationSettingsXml(tempXml, configuredGames);

                bool threw = false;
                VibranceSettingsSnapshot snapshot = new VibranceSettingsSnapshot();
                try
                {
                    snapshot = ReadVibranceSettingsSnapshot(controller, GraphicsAdapter.Nvidia);
                }
                catch (Exception)
                {
                    threw = true;
                }

                bool ok = !threw &&
                    snapshot.VibranceWindowsLevel == NvidiaDynamicVibranceProxy.NvapiDefaultLevel &&
                    snapshot.AffectPrimaryMonitorOnly == false &&
                    snapshot.NeverSwitchResolution == false &&
                    snapshot.NeverChangeColorSettings == false &&
                    snapshot.BrightnessWindowsLevel == 77 &&
                    snapshot.ContrastWindowsLevel == 88 &&
                    snapshot.GammaWindowsLevel == 33 &&
                    snapshot.ApplicationSettings != null &&
                    snapshot.ApplicationSettings.Count == configuredGames.Count;

                checklist.Check(ok,
                    string.Format("S7: a missing inactiveValue key (int.Parse(\"\") is the sharpest trigger) never throws and defaults only that value, got threw={0} vibranceWindowsLevel={1} affectPrimaryMonitorOnly={2} neverSwitchResolution={3} neverChangeColorSettings={4} brightnessWindowsLevel={5} contrastWindowsLevel={6} gammaWindowsLevel={7} gameCount={8}",
                        threw, snapshot.VibranceWindowsLevel, snapshot.AffectPrimaryMonitorOnly, snapshot.NeverSwitchResolution, snapshot.NeverChangeColorSettings, snapshot.BrightnessWindowsLevel, snapshot.ContrastWindowsLevel, snapshot.GammaWindowsLevel,
                        snapshot.ApplicationSettings == null ? -1 : snapshot.ApplicationSettings.Count));
            }
            finally
            {
                DeleteFileIfExists(tempIni);
                DeleteFileIfExists(tempXml);
            }
        }

        // S8. Mutation this guards: any per-value fallback that doesn't match the corresponding
        // missing-file default at :257-268, or any change that stops the XML read from running once
        // every INI value has failed to parse - the "all seven fail at once" end of the scenario S6
        // pins for a single value.
        private static void CheckSettingsAllValuesUnparseableStillDefaultsAndReadsXml(Checklist checklist)
        {
            string tempIni = NewTempPath(".ini");
            string tempXml = NewTempPath(".xml");
            try
            {
                SettingsController controller = new SettingsController(tempIni, tempXml);
                controller.SetVibranceSetting("inactiveValue", "NotANumber");
                controller.SetVibranceSetting("affectPrimaryMonitorOnly", "NotABool");
                controller.SetVibranceSetting("neverSwitchResolution", "NotABool");
                controller.SetVibranceSetting("neverChangeColorSettings", "NotABool");
                controller.SetVibranceSetting("brightnessWindowsLevel", "NotANumber");
                controller.SetVibranceSetting("contrastWindowsLevel", "NotANumber");
                controller.SetVibranceSetting("gammaWindowsLevel", "NotANumber");
                List<ApplicationSetting> configuredGames = MakeConfiguredGames(1);
                WriteApplicationSettingsXml(tempXml, configuredGames);

                VibranceSettingsSnapshot snapshot = ReadVibranceSettingsSnapshot(controller, GraphicsAdapter.Nvidia);

                bool ok = snapshot.VibranceWindowsLevel == NvidiaDynamicVibranceProxy.NvapiDefaultLevel &&
                    snapshot.AffectPrimaryMonitorOnly == true &&
                    snapshot.NeverSwitchResolution == true &&
                    snapshot.NeverChangeColorSettings == true &&
                    snapshot.BrightnessWindowsLevel == 50 &&
                    snapshot.ContrastWindowsLevel == 50 &&
                    snapshot.GammaWindowsLevel == 100 &&
                    snapshot.ApplicationSettings != null &&
                    snapshot.ApplicationSettings.Count == configuredGames.Count;

                checklist.Check(ok,
                    string.Format("S8: all seven values unparseable still yields every documented default AND still reads applicationData.xml, got vibranceWindowsLevel={0} affectPrimaryMonitorOnly={1} neverSwitchResolution={2} neverChangeColorSettings={3} brightnessWindowsLevel={4} contrastWindowsLevel={5} gammaWindowsLevel={6} gameCount={7}",
                        snapshot.VibranceWindowsLevel, snapshot.AffectPrimaryMonitorOnly, snapshot.NeverSwitchResolution, snapshot.NeverChangeColorSettings, snapshot.BrightnessWindowsLevel, snapshot.ContrastWindowsLevel, snapshot.GammaWindowsLevel,
                        snapshot.ApplicationSettings == null ? -1 : snapshot.ApplicationSettings.Count));
            }
            finally
            {
                DeleteFileIfExists(tempIni);
                DeleteFileIfExists(tempXml);
            }
        }

        // ------------------------------------------------------------------
        // LogSink - the seam VibranceGUI.Log(string)/Log(Exception) now delegate to instead of
        // opening %APPDATA%\vibranceGUI\vibranceGUI.log directly (see ILogSink.cs). L4/L5 pin
        // LogSink.Current's own default and null-fallback (ILogSink.cs) - the property that
        // regressed once already: installing NullLogSink per --selftest-* flag in Program.Main
        // only ever protected Main's own entry point, never a reflection harness that calls a
        // fixture's Run() directly, which is how every self test in this codebase actually gets
        // run. They run FIRST below, ahead of L1-L3, despite the numbering: L1-L3 each save and
        // restore LogSink.Current around their own use of it, so L4/L5 would still read back
        // correctly even placed last, but only by relying on that restore being correct; running
        // first instead means L4/L5 observe the value nothing in this process has touched yet,
        // which is the actual property worth pinning. No other fixture in this codebase reads or
        // writes LogSink.Current (only Program.cs, ILogSink.cs and this file do), so nothing
        // upstream of ProfileToggleFixture.Run() in a full suite run can have replaced it either.
        // L1/L2 pin SettingsController's parse-failure logging above (LogSettingsParseFailure) by
        // content - S1-S8 above only ever pinned the seven returned VALUES, never what got logged
        // about a failed one, so a wrong key name, a swapped raw-value/fallback argument, or a
        // missing call could regress silently. L3 pins the property Program.LogSafely exists for:
        // DeviceGammaRampHelper's WinEvent-reachable restore path must never throw back into a
        // native callback frame, even when the sink underneath VibranceGUI.Log is itself broken.
        // ------------------------------------------------------------------

        private static void RunLoggingChecks(Checklist checklist)
        {
            checklist.Lines.Add("LogSink (temp INI, RecordingLogSink/ThrowingLogSink fakes, never the real vibranceGUI.log):");

            CheckLogSinkDefaultsToNullSink(checklist);
            CheckLogSinkNullSetterFallsBackToNullSink(checklist);
            CheckSettingsParseFailureLogsSingleKeyContent(checklist);
            CheckSettingsParseFailureLogsEverySpecificFailure(checklist);
            CheckLogSinkNeverThrowsAcrossLogSafely(checklist);

            checklist.Lines.Add(string.Empty);
        }

        // L4. Mutation this guards: LogSink._current initialised to RealLogSink instead of
        // NullLogSink (ILogSink.cs) - the exact regression this fixture exists to catch: a
        // reflection harness that calls ProfileToggleFixture.Run() (or any fixture's Run())
        // directly, bypassing Program.Main entirely, never sees Main's own --selftest guard, so
        // the default LogSink.Current starts with is the only thing standing between such a run
        // and the real, shared vibranceGUI.log. Must run before L1-L3, which each swap
        // LogSink.Current out for the duration of one check - see the section comment above for
        // why.
        private static void CheckLogSinkDefaultsToNullSink(Checklist checklist)
        {
            ILogSink current = LogSink.Current;
            checklist.Check(current is NullLogSink,
                string.Format("L4: LogSink.Current defaults to NullLogSink, so a run that never reaches Program.Main (a reflection harness calling Run() directly, for one) still never appends to the real vibranceGUI.log, got {0}",
                    current == null ? "<null>" : current.GetType().Name));
        }

        // L5. Mutation this guards: LogSink.Current's setter falling back to RealLogSink instead
        // of NullLogSink when assigned null (ILogSink.cs) - the same unsafe default, reachable a
        // second way, since both ResetForTests(null) and a bare Current = null funnel through this
        // one setter.
        private static void CheckLogSinkNullSetterFallsBackToNullSink(Checklist checklist)
        {
            ILogSink previousSink = LogSink.Current;
            try
            {
                LogSink.Current = null;
                ILogSink current = LogSink.Current;

                checklist.Check(current is NullLogSink,
                    string.Format("L5: assigning LogSink.Current = null falls back to NullLogSink, not RealLogSink, got {0}",
                        current == null ? "<null>" : current.GetType().Name));
            }
            finally
            {
                LogSink.Current = previousSink;
            }
        }

        // L1. Mutation this guards: LogSettingsParseFailure's format string dropping the key name,
        // logging the fallback instead of the raw bad value (or vice versa), or logging more than
        // once for one failed key.
        private static void CheckSettingsParseFailureLogsSingleKeyContent(Checklist checklist)
        {
            ILogSink previousSink = LogSink.Current;
            RecordingLogSink recordingSink = new RecordingLogSink();
            string tempIni = NewTempPath(".ini");
            string tempXml = NewTempPath(".xml");
            try
            {
                LogSink.Current = recordingSink;

                SettingsController controller = new SettingsController(tempIni, tempXml);
                controller.SetVibranceSetting("inactiveValue", "42");
                controller.SetVibranceSetting("affectPrimaryMonitorOnly", "False");
                controller.SetVibranceSetting("neverSwitchResolution", "False");
                controller.SetVibranceSetting("neverChangeColorSettings", "False");
                controller.SetVibranceSetting("brightnessWindowsLevel", "77");
                controller.SetVibranceSetting("contrastWindowsLevel", "88");
                controller.SetVibranceSetting("gammaWindowsLevel", "NotANumber");
                WriteApplicationSettingsXml(tempXml, MakeConfiguredGames(1));

                ReadVibranceSettingsSnapshot(controller, GraphicsAdapter.Nvidia);

                const string expected = "Failed to parse the \"gammaWindowsLevel\" setting (\"NotANumber\") from the settings INI, falling back to 100.";
                bool ok = recordingSink.Messages.Count == 1 && recordingSink.Messages[0] == expected;

                checklist.Check(ok,
                    string.Format("L1: a single corrupt gammaWindowsLevel logs exactly one line naming the key, the raw value and the fallback verbatim, got count={0} message=\"{1}\" (expected \"{2}\")",
                        recordingSink.Messages.Count, recordingSink.Messages.Count > 0 ? recordingSink.Messages[0] : "<none>", expected));
            }
            finally
            {
                LogSink.Current = previousSink;
                DeleteFileIfExists(tempIni);
                DeleteFileIfExists(tempXml);
            }
        }

        // L2. Mutation this guards: any one of the seven LogSettingsParseFailure call sites in
        // ReadVibranceSettings (SettingsController.cs) using the wrong key constant or the wrong
        // fallback variable - each of the seven keys is corrupted at once specifically so a
        // copy-pasted line still logging a sibling key's name cannot hide behind the others.
        private static void CheckSettingsParseFailureLogsEverySpecificFailure(Checklist checklist)
        {
            ILogSink previousSink = LogSink.Current;
            RecordingLogSink recordingSink = new RecordingLogSink();
            string tempIni = NewTempPath(".ini");
            string tempXml = NewTempPath(".xml");
            try
            {
                LogSink.Current = recordingSink;

                SettingsController controller = new SettingsController(tempIni, tempXml);
                controller.SetVibranceSetting("inactiveValue", "NotANumber");
                controller.SetVibranceSetting("affectPrimaryMonitorOnly", "NotABool");
                controller.SetVibranceSetting("neverSwitchResolution", "NotABool");
                controller.SetVibranceSetting("neverChangeColorSettings", "NotABool");
                controller.SetVibranceSetting("brightnessWindowsLevel", "NotANumber");
                controller.SetVibranceSetting("contrastWindowsLevel", "NotANumber");
                controller.SetVibranceSetting("gammaWindowsLevel", "NotANumber");
                WriteApplicationSettingsXml(tempXml, MakeConfiguredGames(1));

                ReadVibranceSettingsSnapshot(controller, GraphicsAdapter.Nvidia);

                List<string> expected = new List<string>
                {
                    "Failed to parse the \"inactiveValue\" setting (\"NotANumber\") from the settings INI, falling back to " + NvidiaDynamicVibranceProxy.NvapiDefaultLevel + ".",
                    "Failed to parse the \"affectPrimaryMonitorOnly\" setting (\"NotABool\") from the settings INI, falling back to True.",
                    "Failed to parse the \"neverSwitchResolution\" setting (\"NotABool\") from the settings INI, falling back to True.",
                    "Failed to parse the \"neverChangeColorSettings\" setting (\"NotABool\") from the settings INI, falling back to True.",
                    "Failed to parse the \"brightnessWindowsLevel\" setting (\"NotANumber\") from the settings INI, falling back to 50.",
                    "Failed to parse the \"contrastWindowsLevel\" setting (\"NotANumber\") from the settings INI, falling back to 50.",
                    "Failed to parse the \"gammaWindowsLevel\" setting (\"NotANumber\") from the settings INI, falling back to 100."
                };

                bool ok = recordingSink.Messages.Count == expected.Count;
                if (ok)
                {
                    foreach (string line in expected)
                    {
                        if (!recordingSink.Messages.Contains(line))
                        {
                            ok = false;
                            break;
                        }
                    }
                }

                checklist.Check(ok,
                    string.Format("L2: all seven parse failures each log their own key, raw value and fallback rather than a shared/generic line, got {0} message(s): {1}",
                        recordingSink.Messages.Count, string.Join(" | ", recordingSink.Messages.ToArray())));
            }
            finally
            {
                LogSink.Current = previousSink;
                DeleteFileIfExists(tempIni);
                DeleteFileIfExists(tempXml);
            }
        }

        // L3. Mutation this guards: dropping Program.LogSafely's own try/catch (Program.cs), which
        // is what keeps DeviceGammaRampHelper's WinEvent-reachable restore path from throwing back
        // into a native callback frame when the log write underneath it fails. The direct
        // VibranceGUI.Log probe first proves ThrowingLogSink is actually reached through the seam
        // (so a broken LogSink.Current wire-up cannot make this pass vacuously); only then does the
        // LogSafely probe assert the wrapper swallows exactly the same failure.
        private static void CheckLogSinkNeverThrowsAcrossLogSafely(Checklist checklist)
        {
            ILogSink previousSink = LogSink.Current;
            try
            {
                LogSink.Current = new ThrowingLogSink();

                bool directCallThrew = false;
                try
                {
                    VibranceGUI.Log("L3 direct probe - must reach the fake sink and throw");
                }
                catch (Exception)
                {
                    directCallThrew = true;
                }

                bool safeCallThrew = false;
                try
                {
                    Program.LogSafely("L3 safe probe - must reach the fake sink without throwing");
                }
                catch (Exception)
                {
                    safeCallThrew = true;
                }

                checklist.Check(directCallThrew && !safeCallThrew,
                    string.Format("L3: a throwing ILogSink still throws through the bare VibranceGUI.Log facade (proving the fake is really wired in) but never throws through Program.LogSafely, got directCallThrew={0} safeCallThrew={1}",
                        directCallThrew, safeCallThrew));
            }
            finally
            {
                LogSink.Current = previousSink;
            }
        }

        // RecordingLogSink's fixed content for L1/L2 above - never touches the real
        // vibranceGUI.log, and records the exact string VibranceGUI.Log(string) was called with
        // (LogSettingsParseFailure never logs an Exception, so Write(Exception) is exercised only
        // for interface completeness).
        private class RecordingLogSink : ILogSink
        {
            public readonly List<string> Messages = new List<string>();

            public void Write(string message)
            {
                Messages.Add(message);
            }

            public void Write(Exception ex)
            {
                Messages.Add(ex.ToString());
            }
        }

        // L3's fake - throws from both overloads so a check can prove Program.LogSafely's own
        // try/catch still shields its caller even when the sink underneath VibranceGUI.Log is
        // itself the thing failing, not just a broken File.AppendText the way it always could.
        private class ThrowingLogSink : ILogSink
        {
            public void Write(string message)
            {
                throw new InvalidOperationException("ThrowingLogSink: Write(string) always throws.");
            }

            public void Write(Exception ex)
            {
                throw new InvalidOperationException("ThrowingLogSink: Write(Exception) always throws.");
            }
        }

        // Records every (deviceName -> handle) resolution and every (handle -> level) write this
        // fake is asked to make, plus a combined call counter (TotalCallCount) the gate checks
        // above use for their "zero calls AT ALL" assertions. VibranceRestoreFixture's own
        // FakeNvidiaVibranceDevice is a private nested class there and not reachable from this
        // file, so this is a second, smaller copy of the same shape.
        private class FakeNvidiaVibranceDevice : INvidiaVibranceDevice
        {
            private readonly Dictionary<string, int> _handlesByDeviceName = new Dictionary<string, int>();
            private readonly Dictionary<int, int> _levelsByHandle = new Dictionary<int, int>();
            private readonly HashSet<int> _failNextSetLevel = new HashSet<int>();
            private int _nextHandle = 1;

            public readonly List<int> SetLevelCalls = new List<int>();
            public readonly List<string> ResolvedDeviceNames = new List<string>();
            public int TotalCallCount;

            public int HandleFor(string deviceName)
            {
                return ResolveOrAssign(deviceName);
            }

            public void SeedLevel(string deviceName, int level)
            {
                _levelsByHandle[ResolveOrAssign(deviceName)] = level;
            }

            public void FailNextSetLevel(string deviceName)
            {
                _failNextSetLevel.Add(ResolveOrAssign(deviceName));
            }

            private int ResolveOrAssign(string deviceName)
            {
                int handle;
                if (!_handlesByDeviceName.TryGetValue(deviceName, out handle))
                {
                    handle = _nextHandle++;
                    _handlesByDeviceName[deviceName] = handle;
                }
                return handle;
            }

            public bool IsWindowActive(ref IntPtr hWnd)
            {
                TotalCallCount++;
                return true;
            }

            public int TryResolveDisplayHandle(string deviceName)
            {
                TotalCallCount++;
                ResolvedDeviceNames.Add(deviceName);
                if (string.IsNullOrEmpty(deviceName))
                {
                    return -1;
                }
                return ResolveOrAssign(deviceName);
            }

            public bool IsAtLevel(int displayHandle, int level)
            {
                TotalCallCount++;
                int current;
                return _levelsByHandle.TryGetValue(displayHandle, out current) && current == level;
            }

            public bool SetLevel(int displayHandle, int level)
            {
                TotalCallCount++;
                SetLevelCalls.Add(displayHandle);
                if (_failNextSetLevel.Remove(displayHandle))
                {
                    return false;
                }
                _levelsByHandle[displayHandle] = level;
                return true;
            }
        }

        // Everything IAmdAdapter exposes, none of it touching real hardware - a second, smaller
        // copy of VibranceRestoreFixture's own private nested fake, extended with one-shot
        // per-display failure injection for T4a/T5a above.
        private class FakeAmdAdapter : IAmdAdapter
        {
            private readonly HashSet<string> _failNextSetSaturationOnDisplay = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public int SetSaturationOnAllDisplaysCallCount;

            public readonly List<int> SetSaturationOnDisplayLevels = new List<int>();
            public readonly List<string> SetSaturationOnDisplayNames = new List<string>();

            public void FailNextSetSaturationOnDisplay(string displayName)
            {
                _failNextSetSaturationOnDisplay.Add(displayName ?? string.Empty);
            }

            public void SetSaturationOnAllDisplays(int vibranceLevel)
            {
                SetSaturationOnAllDisplaysCallCount++;
            }

            public bool SetSaturationOnDisplay(int vibranceLevel, string displayName)
            {
                SetSaturationOnDisplayLevels.Add(vibranceLevel);
                SetSaturationOnDisplayNames.Add(displayName);
                return !_failNextSetSaturationOnDisplay.Remove(displayName ?? string.Empty);
            }

            public bool IsAvailable()
            {
                return false;
            }

            public void Init()
            {
            }

            public void Dispose()
            {
            }
        }

        // Records every Register/Unregister call this fake is asked to make, with a one-shot
        // FIFO queue of forced results - mirrors ResolutionChangeFixture.FakeDisplayModeDevice's
        // QueueResult shape. Never touches a real hotkey - see this file's own header comment.
        private class FakeHotkeyRegistrar : IHotkeyRegistrar
        {
            public struct RegisterCall
            {
                public readonly IntPtr HWnd;
                public readonly int Id;
                public readonly uint Modifiers;
                public readonly uint VirtualKey;

                public RegisterCall(IntPtr hWnd, int id, uint modifiers, uint virtualKey)
                {
                    HWnd = hWnd;
                    Id = id;
                    Modifiers = modifiers;
                    VirtualKey = virtualKey;
                }
            }

            private readonly Queue<HotkeyRegistrationResult> _queuedResults = new Queue<HotkeyRegistrationResult>();

            public readonly List<RegisterCall> RegisterCalls = new List<RegisterCall>();
            public readonly List<IntPtr> UnregisterCalls = new List<IntPtr>();

            public void QueueResult(HotkeyRegistrationResult result)
            {
                _queuedResults.Enqueue(result);
            }

            public HotkeyRegistrationResult Register(IntPtr hWnd, int id, uint modifiers, uint virtualKey)
            {
                RegisterCalls.Add(new RegisterCall(hWnd, id, modifiers, virtualKey));
                if (_queuedResults.Count > 0)
                {
                    return _queuedResults.Dequeue();
                }
                return HotkeyRegistrationResult.Registered;
            }

            public void Unregister(IntPtr hWnd, int id)
            {
                UnregisterCalls.Add(hWnd);
            }
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

            // Deliberately not counted in Total/Passed - see StabilityFixture.Checklist.Skip for
            // the convention this follows. None of the checks above currently need it (unlike the
            // pre-existing AMD checks in VibranceRestoreFixture, nothing here reads the real
            // GetForegroundWindow()), but it is kept for parity with every other fixture's
            // Checklist shape.
            public void Skip(string description)
            {
                Lines.Add(string.Format("[SKIP] {0}", description));
            }
        }
    }
}
